// TranslateClient.cs — HTTP calls to the translation providers. NativeAOT-safe: request JSON is written
// with Utf8JsonWriter and responses are read with JsonDocument (no reflection serialization).
//
//   google  Cloud Translation Basic v2  POST .../language/translate/v2?key=KEY  {q,target,format,source?}
//   deepl   DeepL API v2                POST .../v2/translate  Authorization: DeepL-Auth-Key KEY
//           (keys ending ":fx" are Free-plan keys and must use api-free.deepl.com)
//   libre   LibreTranslate              POST {server}/translate  {q,source,target,format,api_key?}
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.Plugin.RynthAi.Translate;

/// <summary>Outcome of one request. <see cref="DetectedSource"/> is empty when the provider didn't report it.</summary>
internal readonly record struct TranslateResult(bool Ok, string Text, string DetectedSource, string Error)
{
    public static TranslateResult Fail(string error) => new(false, string.Empty, string.Empty, error);
}

internal static class TranslateClient
{
    public const string GoogleUrl = "https://translation.googleapis.com/language/translate/v2";
    public const string DeepLProUrl = "https://api.deepl.com/v2/translate";
    public const string DeepLFreeUrl = "https://api-free.deepl.com/v2/translate";
    public const string LibreUrl = "https://libretranslate.com";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RynthAi-Translate/1.0");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    /// <summary>Endpoint shown in the UI when the URL box is blank.</summary>
    public static string DefaultUrl(string provider, string apiKey) => provider switch
    {
        TranslateSettings.ProviderDeepL => IsDeepLFreeKey(apiKey) ? DeepLFreeUrl : DeepLProUrl,
        TranslateSettings.ProviderLibre => LibreUrl,
        _ => GoogleUrl,
    };

    private static bool IsDeepLFreeKey(string apiKey)
        => apiKey.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Translates <paramref name="text"/> from <paramref name="source"/> ("auto" = detect) into
    /// <paramref name="target"/>. Never throws; failures come back as <see cref="TranslateResult.Fail"/>.
    /// </summary>
    public static async Task<TranslateResult> TranslateAsync(string provider, string apiKey, string apiUrl,
        string text, string source, string target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return TranslateResult.Fail("nothing to translate");
        apiKey = (apiKey ?? string.Empty).Trim();
        apiUrl = (apiUrl ?? string.Empty).Trim();
        bool auto = string.IsNullOrWhiteSpace(source) || source.Equals(TranslateLanguages.Auto, StringComparison.OrdinalIgnoreCase);

        try
        {
            return provider switch
            {
                TranslateSettings.ProviderDeepL => await DeepLAsync(apiKey, apiUrl, text, auto ? null : source, target, ct).ConfigureAwait(false),
                TranslateSettings.ProviderLibre => await LibreAsync(apiKey, apiUrl, text, auto ? null : source, target, ct).ConfigureAwait(false),
                _ => await GoogleAsync(apiKey, apiUrl, text, auto ? null : source, target, ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TranslateResult.Fail("request timed out");
        }
        catch (OperationCanceledException)
        {
            return TranslateResult.Fail("cancelled");
        }
        catch (HttpRequestException ex)
        {
            return TranslateResult.Fail("network error: " + ex.Message);
        }
        catch (Exception ex)
        {
            return TranslateResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Google Cloud Translation Basic v2 ──────────────────────────────────

    private static async Task<TranslateResult> GoogleAsync(string key, string url, string text, string? source, string target, CancellationToken ct)
    {
        if (key.Length == 0) return TranslateResult.Fail("Google needs an API key (Cloud Translation API).");
        string baseUrl = url.Length == 0 ? GoogleUrl : url;
        int q = baseUrl.IndexOf('?');
        if (q >= 0) baseUrl = baseUrl.Substring(0, q);

        string body = BuildJson(w =>
        {
            w.WriteString("q", text);
            w.WriteString("target", target);
            w.WriteString("format", "text");
            if (source != null) w.WriteString("source", source);
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "?key=" + Uri.EscapeDataString(key))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var (status, json) = await SendAsync(req, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK) return TranslateResult.Fail(ErrorText(status, json));

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("translations", out var arr)
            && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        {
            var first = arr[0];
            string translated = first.TryGetProperty("translatedText", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            string detected = first.TryGetProperty("detectedSourceLanguage", out var d) ? d.GetString() ?? string.Empty : source ?? string.Empty;
            return new TranslateResult(true, WebUtility.HtmlDecode(translated), detected, string.Empty);
        }
        return TranslateResult.Fail("unexpected Google response: " + Truncate(json, 160));
    }

    // ── DeepL v2 ───────────────────────────────────────────────────────────

    private static async Task<TranslateResult> DeepLAsync(string key, string url, string text, string? source, string target, CancellationToken ct)
    {
        if (key.Length == 0) return TranslateResult.Fail("DeepL needs an API key (Free keys end in :fx).");
        string endpoint = url.Length == 0 ? (IsDeepLFreeKey(key) ? DeepLFreeUrl : DeepLProUrl) : url;

        string body = BuildJson(w =>
        {
            w.WriteStartArray("text");
            w.WriteStringValue(text);
            w.WriteEndArray();
            w.WriteString("target_lang", DeepLTarget(target));
            if (source != null) w.WriteString("source_lang", DeepLSource(source));
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + key);
        var (status, json) = await SendAsync(req, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK) return TranslateResult.Fail(ErrorText(status, json));

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("translations", out var arr)
            && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        {
            var first = arr[0];
            string translated = first.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            string detected = first.TryGetProperty("detected_source_language", out var d) ? (d.GetString() ?? string.Empty).ToLowerInvariant() : string.Empty;
            return new TranslateResult(true, translated, detected, string.Empty);
        }
        return TranslateResult.Fail("unexpected DeepL response: " + Truncate(json, 160));
    }

    /// <summary>DeepL target codes: upper-case, English / Portuguese need a regional variant.</summary>
    private static string DeepLTarget(string code) => code.ToLowerInvariant() switch
    {
        "en" => "EN-US",
        "pt" => "PT-BR",
        "zh-cn" => "ZH-HANS",
        "zh-tw" => "ZH-HANT",
        "no" => "NB",
        var c => c.ToUpperInvariant(),
    };

    /// <summary>DeepL source codes: base language only.</summary>
    private static string DeepLSource(string code)
    {
        string b = TranslateLanguages.BaseOf(code);
        return (b == "no" ? "nb" : b).ToUpperInvariant();
    }

    // ── LibreTranslate ─────────────────────────────────────────────────────

    private static async Task<TranslateResult> LibreAsync(string key, string url, string text, string? source, string target, CancellationToken ct)
    {
        string server = (url.Length == 0 ? LibreUrl : url).TrimEnd('/');
        string endpoint = server.EndsWith("/translate", StringComparison.OrdinalIgnoreCase) ? server : server + "/translate";

        string body = BuildJson(w =>
        {
            w.WriteString("q", text);
            w.WriteString("source", source == null ? "auto" : LibreCode(source));
            w.WriteString("target", LibreCode(target));
            w.WriteString("format", "text");
            if (key.Length > 0) w.WriteString("api_key", key);
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var (status, json) = await SendAsync(req, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK) return TranslateResult.Fail(ErrorText(status, json));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("translatedText", out var t))
        {
            string detected = root.TryGetProperty("detectedLanguage", out var d) && d.ValueKind == JsonValueKind.Object
                              && d.TryGetProperty("language", out var lang)
                ? lang.GetString() ?? string.Empty
                : source ?? string.Empty;
            return new TranslateResult(true, t.GetString() ?? string.Empty, detected, string.Empty);
        }
        return TranslateResult.Fail("unexpected LibreTranslate response: " + Truncate(json, 160));
    }

    /// <summary>LibreTranslate uses "zh" for Simplified and "zt" for Traditional Chinese.</summary>
    private static string LibreCode(string code) => code.ToLowerInvariant() switch
    {
        "zh-cn" => "zh",
        "zh-tw" => "zt",
        "no" => "nb",
        var c => c,
    };

    // ── Shared helpers ─────────────────────────────────────────────────────

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (resp.StatusCode, body);
    }

    private static string BuildJson(Action<Utf8JsonWriter> writeProperties)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            writeProperties(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Pulls the provider's error message (Google error.message, DeepL message, Libre error).</summary>
    private static string ErrorText(HttpStatusCode status, string json)
    {
        string detail = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out var err))
                    detail = err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var m)
                        ? m.GetString() ?? string.Empty
                        : err.ValueKind == JsonValueKind.String ? err.GetString() ?? string.Empty : string.Empty;
                else if (root.TryGetProperty("message", out var msg))
                    detail = msg.GetString() ?? string.Empty;
            }
        }
        catch (JsonException) { detail = Truncate(json, 160); }
        string hint = status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => " (check the API key)",
            (HttpStatusCode)429 => " (rate limited, slow down)",
            (HttpStatusCode)456 => " (DeepL quota used up)",
            _ => string.Empty,
        };
        return $"HTTP {(int)status}{hint}" + (detail.Length > 0 ? ": " + Truncate(detail, 200) : string.Empty);
    }

    internal static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s.Substring(0, max) + "...";
}

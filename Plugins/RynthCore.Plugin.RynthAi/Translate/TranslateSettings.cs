// TranslateSettings.cs — Chat translator settings, shared by every character (RynthAi\translate.json).
// Fields are written by the render thread (UI) and read by the pump thread; each one is a single
// reference / 32-bit value so reads never tear. The JSON keeps channels as names; the masks are the
// runtime form.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi.Translate;

internal sealed class TranslateSettings
{
    public const string ProviderGoogle = "google";
    public const string ProviderDeepL = "deepl";
    public const string ProviderLibre = "libre";
    public static readonly string[] Providers = { ProviderGoogle, ProviderDeepL, ProviderLibre };
    public static readonly string[] ProviderNames = { "Google Cloud Translation", "DeepL", "LibreTranslate" };

    /// <summary>Master switch; off means no API calls and no chat interception.</summary>
    public bool Enabled;

    public string Provider = ProviderGoogle;

    /// <summary>Stored in plain text in translate.json.</summary>
    public string ApiKey = string.Empty;

    /// <summary>Blank uses the provider's default endpoint (LibreTranslate needs a server URL).</summary>
    public string ApiUrl = string.Empty;

    /// <summary>"Receive" language: inbound chat is translated into this.</summary>
    public string MyLanguage = "en";

    /// <summary>Source language of inbound chat; "auto" lets the provider detect it.</summary>
    public string InboundSource = TranslateLanguages.Auto;

    /// <summary>Send language each session starts with (Advanced Settings > Translate).</summary>
    public string DefaultSendLanguage = "es";

    public bool EnableInbound = true;
    public bool EnableOutbound;

    public List<string>? InboundChannels;
    public List<string>? OutboundChannels;

    /// <summary>Skip your own lines inbound (the server echoes what you send).</summary>
    public bool SkipOwn = true;

    /// <summary>Write inbound translations into chat (AC chat window and RynthChat).</summary>
    public bool EchoToChat = true;

    /// <summary>After an outbound translation, show what you typed locally next to what was sent.</summary>
    public bool EchoOutbound = true;

    /// <summary>When an outbound translation fails, send the original text instead of dropping it.</summary>
    public bool SendOriginalOnError = true;

    /// <summary>Lines starting with this go out untranslated (the prefix is removed).</summary>
    public string RawPrefix = "\\";

    /// <summary>Minimum spacing between API requests.</summary>
    public int MinIntervalMs = 750;

    /// <summary>Entries kept in the Translate window log.</summary>
    public int MaxLog = 200;

    public bool ShowWindow;

    // ── Runtime only ────────────────────────────────────────────────────────

    /// <summary>Send language for this session; starts as <see cref="DefaultSendLanguage"/>.</summary>
    [JsonIgnore] public string CurrentSendLanguage = "es";

    [JsonIgnore] public uint InboundMask;
    [JsonIgnore] public uint OutboundMask;

    public bool ListensTo(TranslateChannel ch) => (InboundMask & TranslateChannels.Bit(ch)) != 0;
    public bool SendsOn(TranslateChannel ch) => (OutboundMask & TranslateChannels.Bit(ch)) != 0;

    public static uint DefaultInboundMask()
        => Mask(TranslateChannel.Tell, TranslateChannel.Fellowship, TranslateChannel.Allegiance, TranslateChannel.Patron,
                TranslateChannel.Vassals, TranslateChannel.Covassals, TranslateChannel.Monarch, TranslateChannel.General,
                TranslateChannel.Trade, TranslateChannel.Lfg, TranslateChannel.Roleplay, TranslateChannel.Society,
                TranslateChannel.Olthoi);

    public static uint DefaultOutboundMask()
        => Mask(TranslateChannel.Tell, TranslateChannel.Fellowship, TranslateChannel.Allegiance,
                TranslateChannel.General, TranslateChannel.Roleplay);

    private static uint Mask(params TranslateChannel[] channels)
    {
        uint m = 0;
        foreach (var ch in channels) m |= TranslateChannels.Bit(ch);
        return m;
    }

    internal static uint MaskFromNames(List<string>? names, uint fallback)
    {
        if (names == null) return fallback;
        uint m = 0;
        foreach (var n in names)
            if (TranslateChannels.TryParse(n, out var ch)) m |= TranslateChannels.Bit(ch);
        return m;
    }

    internal static List<string> NamesFromMask(uint mask)
    {
        var list = new List<string>();
        foreach (var ch in TranslateChannels.All)
            if ((mask & TranslateChannels.Bit(ch)) != 0) list.Add(TranslateChannels.Name(ch));
        return list;
    }
}

/// <summary>Loads / saves <see cref="TranslateSettings"/>; writes only when the JSON changed.</summary>
internal sealed class TranslateStore
{
    private readonly string _path;
    private string _lastJson = string.Empty;

    public TranslateStore(string? path = null)
        => _path = path ?? Path.Combine(RynthInstallPaths.RynthAiDir, "translate.json");

    public string FilePath => _path;

    public TranslateSettings Load()
    {
        TranslateSettings? s = null;
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                s = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.TranslateSettings);
                if (s != null) _lastJson = json;
            }
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, $"translate load {_path}"); }
        s ??= new TranslateSettings();
        Normalize(s);
        return s;
    }

    public void SaveIfDirty(TranslateSettings s)
    {
        try
        {
            s.InboundChannels = TranslateSettings.NamesFromMask(s.InboundMask);
            s.OutboundChannels = TranslateSettings.NamesFromMask(s.OutboundMask);
            string json = JsonSerializer.Serialize(s, RynthAiJsonContext.Default.TranslateSettings);
            if (json == _lastJson) return;
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
            _lastJson = json;
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, $"translate save {_path}"); }
    }

    /// <summary>Repairs unknown codes / out-of-range numbers and builds the runtime masks.</summary>
    private static void Normalize(TranslateSettings s)
    {
        if (Array.IndexOf(TranslateSettings.Providers, s.Provider ?? string.Empty) < 0) s.Provider = TranslateSettings.ProviderGoogle;
        s.ApiKey ??= string.Empty;
        s.ApiUrl ??= string.Empty;
        s.RawPrefix ??= string.Empty;
        s.MyLanguage = TranslateLanguages.Normalize(s.MyLanguage) is { } my && my != TranslateLanguages.Auto ? my : "en";
        s.InboundSource = TranslateLanguages.Normalize(s.InboundSource) ?? TranslateLanguages.Auto;
        s.DefaultSendLanguage = TranslateLanguages.Normalize(s.DefaultSendLanguage) is { } send && send != TranslateLanguages.Auto ? send : "es";
        s.CurrentSendLanguage = s.DefaultSendLanguage;
        s.MinIntervalMs = Math.Clamp(s.MinIntervalMs, 250, 10000);
        s.MaxLog = Math.Clamp(s.MaxLog, 20, 2000);
        s.InboundMask = TranslateSettings.MaskFromNames(s.InboundChannels, TranslateSettings.DefaultInboundMask());
        s.OutboundMask = TranslateSettings.MaskFromNames(s.OutboundChannels, TranslateSettings.DefaultOutboundMask());
    }
}

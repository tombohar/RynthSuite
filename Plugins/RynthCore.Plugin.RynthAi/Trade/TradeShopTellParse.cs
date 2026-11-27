// Trade Shop: parse an incoming tell and a buyer's item selector.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here.
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.Trade;

public static class TradeShopTellParse
{
    // "Name tells you, "msg"" with or without the client's <Tell:IIDString:...> name link. The name is
    // the first speaker (lazy), so a quoted "X tells you" inside the message can't change who sent it.
    private static readonly Regex TellLineRx = new(
        @"^(?:<Tell:IIDString:\d+:(?<linkName>[^>]+)>[^<]*<\\Tell>|(?<plainName>[^""<>]+?))\s+tells you,?\s*""(?<msg>.*)""\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));

    // A character name the shop will send a /tell to: letters first, then letters, spaces, ' - (AC names).
    private static readonly Regex NameRx = new(@"^[A-Za-z][A-Za-z '\-]{0,31}$", RegexOptions.CultureInvariant);

    /// <summary>Longest tell body the shop reads (longer ones are ignored).</summary>
    public const int MaxMessageChars = 200;

    /// <summary>Parse AC's "Name tells you, "msg"".</summary>
    public static bool TryParseTell(string? text, out string sender, out string message)
    {
        sender = "";
        message = "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 400) return false;
        Match m;
        try { m = TellLineRx.Match(text.Trim()); }
        catch (RegexMatchTimeoutException) { return false; }
        if (!m.Success) return false;
        sender = (m.Groups["linkName"].Success && m.Groups["linkName"].Length > 0
            ? m.Groups["linkName"].Value : m.Groups["plainName"].Value).Trim();
        message = m.Groups["msg"].Value;
        return IsSafeName(sender) && message.Length <= MaxMessageChars;
    }

    /// <summary>True when <paramref name="name"/> looks like an AC character name (safe to put after /tell).</summary>
    public static bool IsSafeName(string? name) => !string.IsNullOrEmpty(name) && NameRx.IsMatch(name);

    /// <summary>
    /// A selector: slot (#N, slot:N, bare 1..999), price (value:N, py:N, bare 1000 and up), or name text.
    /// </summary>
    public static (int? slot, long? valuePy, string text) ParseSelector(string? raw)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return (null, null, "");
        if (text.Length > 1 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            text = text[1..^1].Trim();

        if (text.StartsWith('#') && int.TryParse(text[1..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hash) && hash > 0)
            return (hash, null, "");
        foreach (string p in new[] { "slot:", "id:" })
            if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(text[p.Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0)
                return (s, null, "");
        foreach (string p in new[] { "value:", "py:", "price:" })
            if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(text[p.Length..].Trim().Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) && v > 0)
                return (null, v, "");
        if (long.TryParse(text.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) && n > 0)
            return n < 1000 ? ((int)n, null, "") : (null, n, "");
        return (null, null, text);
    }

    /// <summary>The verb and argument of a shop tell ("!buy #2" -> ("buy", "#2")); empty verb when it isn't one.</summary>
    public static (string verb, string arg) SplitCommand(string? body)
    {
        string raw = (body ?? "").Trim();
        bool bang = raw.StartsWith('!');
        if (bang) raw = raw[1..].Trim();
        if (raw.Length == 0) return ("", "");
        string[] parts = raw.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        string verb = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1].Trim() : "";
        verb = verb switch
        {
            "help" or "hi" or "hello" or "shop" => "help",
            "rates" or "rate" => "rates",
            "list" or "sheet" or "catalog" or "menu" => "list",
            "price" or "cost" => "price",
            "buy" => "buy",
            _ => "",
        };
        // Without the '!' only a lone greeting counts: free-form chat ("hello, how are you") gets nothing.
        if (!bang && (verb != "help" || arg.Length > 0)) return ("", "");
        return (verb, arg);
    }
}

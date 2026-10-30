// TranslateChannels.cs — AC chat channels the translator can listen to (inbound) or send on (outbound),
// plus parsing of typed slash commands and the chat colour used for local echoes.
using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.Translate;

/// <summary>Values are bit positions in <see cref="TranslateSettings.InboundMask"/> / OutboundMask; do not reorder.</summary>
internal enum TranslateChannel
{
    Local = 0,
    Tell,
    Fellowship,
    Allegiance,
    Patron,
    Vassals,
    Covassals,
    Monarch,
    General,
    Trade,
    Lfg,
    Roleplay,
    Society,
    Olthoi,
}

/// <summary>A typed chat line split into its channel, the text to keep verbatim, and the message body.</summary>
internal readonly record struct OutboundLine(TranslateChannel Channel, string Prefix, string Body, string TellTarget);

internal static class TranslateChannels
{
    public static readonly TranslateChannel[] All = Enum.GetValues<TranslateChannel>();

    public static uint Bit(TranslateChannel ch) => 1u << (int)ch;

    public static string Name(TranslateChannel ch) => ch switch
    {
        TranslateChannel.Lfg => "LFG",
        _ => ch.ToString(),
    };

    /// <summary>Accepts channel names and the usual short forms (case-insensitive).</summary>
    public static bool TryParse(string? text, out TranslateChannel ch)
    {
        ch = TranslateChannel.Local;
        if (string.IsNullOrWhiteSpace(text)) return false;
        switch (text.Trim().ToLowerInvariant())
        {
            case "local": case "say": case "area": ch = TranslateChannel.Local; return true;
            case "tell": case "tells": case "t": ch = TranslateChannel.Tell; return true;
            case "fellowship": case "fellow": case "f": ch = TranslateChannel.Fellowship; return true;
            case "allegiance": case "alleg": case "a": ch = TranslateChannel.Allegiance; return true;
            case "patron": case "p": ch = TranslateChannel.Patron; return true;
            case "vassals": case "vassal": case "v": ch = TranslateChannel.Vassals; return true;
            case "covassals": case "covassal": case "c": ch = TranslateChannel.Covassals; return true;
            case "monarch": case "m": ch = TranslateChannel.Monarch; return true;
            case "general": case "cg": case "g": ch = TranslateChannel.General; return true;
            case "trade": case "ct": ch = TranslateChannel.Trade; return true;
            case "lfg": case "looking for group": case "clfg": ch = TranslateChannel.Lfg; return true;
            case "roleplay": case "rp": case "crp": ch = TranslateChannel.Roleplay; return true;
            case "society": case "cs": ch = TranslateChannel.Society; return true;
            case "olthoi": case "co": ch = TranslateChannel.Olthoi; return true;
            default: return false;
        }
    }

    /// <summary>Chat colour for local echoes, so they land in the same chat tab as the source channel.</summary>
    public static int ChatType(TranslateChannel ch) => ch switch
    {
        TranslateChannel.Local => 0x02,
        TranslateChannel.Tell => 0x03,
        TranslateChannel.Fellowship => 0x13,
        TranslateChannel.Allegiance or TranslateChannel.Patron or TranslateChannel.Vassals
            or TranslateChannel.Covassals or TranslateChannel.Monarch => 0x12,
        TranslateChannel.General => 0x1B,
        TranslateChannel.Trade => 0x1C,
        TranslateChannel.Lfg => 0x1D,
        TranslateChannel.Roleplay => 0x1E,
        TranslateChannel.Olthoi => 0x20,
        TranslateChannel.Society => 0x21,
        _ => 0x01,
    };

    /// <summary>Channel implied by an inbound chat colour; null for generic colours (Channels / Social).</summary>
    public static TranslateChannel? FromChatType(int chatType) => chatType switch
    {
        0x02 => TranslateChannel.Local,
        0x03 => TranslateChannel.Tell,
        0x12 => TranslateChannel.Allegiance,
        0x13 => TranslateChannel.Fellowship,
        0x1B => TranslateChannel.General,
        0x1C => TranslateChannel.Trade,
        0x1D => TranslateChannel.Lfg,
        0x1E => TranslateChannel.Roleplay,
        0x20 => TranslateChannel.Olthoi,
        0x21 => TranslateChannel.Society,
        _ => null,
    };

    /// <summary>
    /// Command prefix used when the translator composes a line itself (Translate window).
    /// A tell needs <paramref name="tellTarget"/>; an empty target replies to the last tell.
    /// </summary>
    public static string SendPrefix(TranslateChannel ch, string? tellTarget) => ch switch
    {
        TranslateChannel.Local => string.Empty,
        TranslateChannel.Tell => string.IsNullOrWhiteSpace(tellTarget) ? "/r " : $"/t {tellTarget.Trim()}, ",
        TranslateChannel.Fellowship => "/f ",
        TranslateChannel.Allegiance => "/a ",
        TranslateChannel.Patron => "/p ",
        TranslateChannel.Vassals => "/v ",
        TranslateChannel.Covassals => "/c ",
        TranslateChannel.Monarch => "/m ",
        TranslateChannel.General => "/cg ",
        TranslateChannel.Trade => "/ct ",
        TranslateChannel.Lfg => "/clfg ",
        TranslateChannel.Roleplay => "/crp ",
        TranslateChannel.Society => "/cs ",
        TranslateChannel.Olthoi => "/co ",
        _ => string.Empty,
    };

    // "/cmd rest" or "@cmd rest"; the body keeps everything after the first run of whitespace.
    private static readonly Regex RxSlash = new(@"^(?<cmd>[/@][A-Za-z]+)(?<sp>\s+)(?<rest>.+)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // Tell target and body: "Name, message".
    private static readonly Regex RxTellArgs = new(@"^(?<name>[^,]+?)\s*,\s*(?<msg>.+)$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits a typed chat line into channel + verbatim prefix + body. Plain text is Local speech.
    /// Returns false for slash commands that are not chat (emotes, /ra, /loc, ...).
    /// </summary>
    public static bool TryParseOutbound(string text, out OutboundLine line)
    {
        line = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();

        if (t[0] != '/' && t[0] != '@')
        {
            line = new OutboundLine(TranslateChannel.Local, string.Empty, t, string.Empty);
            return true;
        }

        var m = RxSlash.Match(t);
        if (!m.Success) return false;
        string cmd = m.Groups["cmd"].Value.Substring(1).ToLowerInvariant();
        string rest = m.Groups["rest"].Value;
        string head = m.Groups["cmd"].Value + m.Groups["sp"].Value;

        TranslateChannel ch;
        switch (cmd)
        {
            case "s": case "say": ch = TranslateChannel.Local; break;
            case "f": case "fellowship": ch = TranslateChannel.Fellowship; break;
            case "a": case "allegiance": ch = TranslateChannel.Allegiance; break;
            case "p": case "patron": ch = TranslateChannel.Patron; break;
            case "v": case "vassals": ch = TranslateChannel.Vassals; break;
            case "c": case "covassals": ch = TranslateChannel.Covassals; break;
            case "m": case "monarch": ch = TranslateChannel.Monarch; break;
            case "cg": ch = TranslateChannel.General; break;
            case "ct": ch = TranslateChannel.Trade; break;
            case "clfg": ch = TranslateChannel.Lfg; break;
            case "crp": case "rp": ch = TranslateChannel.Roleplay; break;
            case "cs": ch = TranslateChannel.Society; break;
            case "co": ch = TranslateChannel.Olthoi; break;
            case "r": case "reply":
                line = new OutboundLine(TranslateChannel.Tell, head, rest, string.Empty);
                return true;
            case "t": case "tell":
                var tm = RxTellArgs.Match(rest);
                if (!tm.Success) return false;
                string msg = tm.Groups["msg"].Value;
                string prefix = t.Substring(0, t.Length - msg.Length);
                line = new OutboundLine(TranslateChannel.Tell, prefix, msg, tm.Groups["name"].Value.Trim());
                return true;
            default:
                return false;
        }

        line = new OutboundLine(ch, head, rest, string.Empty);
        return true;
    }
}

// AcChatEscapes.cs — AC chat text helpers for the translator.
//
// AC carries characters above Latin-1 (Hangul, CJK, Cyrillic, ...) in chat as "<hhhh>" UTF-16 escapes,
// and the outbound chat path converts each char to one byte, so anything above U+00FF must be escaped
// before sending. Latin-1 letters (ä ö ü ñ é) stay literal; escaping them shows "<00e4>" in chat.
// Speaker names arrive wrapped in "<Tell:IIDString:id:Name>Name<\Tell>" link markup.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.Translate;

internal static class AcChatEscapes
{
    private static readonly Regex RxEscape = new(@"<([0-9a-fA-F]{4})>", RegexOptions.CultureInvariant);
    private static readonly Regex RxTellLink = new(@"<Tell:[^>]*>(?<n>[^<]*)<\\Tell>", RegexOptions.CultureInvariant);

    /// <summary>Expands "&lt;c2dc&gt;" escapes into the characters they stand for.</summary>
    public static string Decode(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text ?? string.Empty;
        return RxEscape.Replace(text, m =>
        {
            int cp = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return cp is > 0 and <= 0xFFFF ? ((char)cp).ToString() : m.Value;
        });
    }

    /// <summary>Escapes characters above U+00FF as "&lt;hhhh&gt;"; drops other control characters.</summary>
    public static string EncodeForSend(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            if (c == '\r' || c == '\n' || c == '\t') { sb.Append(' '); continue; }
            if (c < 0x20) continue;
            if (c <= 0xFF) { sb.Append(c); continue; }
            sb.Append('<').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)).Append('>');
        }
        return sb.ToString().Trim();
    }

    /// <summary>Replaces tell-link markup with the plain name it wraps.</summary>
    public static string StripLinks(string text)
        => string.IsNullOrEmpty(text) || text.IndexOf("<Tell:", System.StringComparison.Ordinal) < 0
            ? text ?? string.Empty
            : RxTellLink.Replace(text, "${n}");
}

using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthChat;

// ChatMessageType values from ACE.Entity.Enum.ChatMessageType (ACE binary is
// byte-identical to retail, so these are authoritative for retail acclient.exe too).
internal static class ChatClassifier
{
    internal const string Chat     = "Chat";      // Local/Tell/Allegiance/Fellow — all player-to-player
    internal const string Channels = "Channels";  // General/Trade/LFG/Roleplay/Society/Admin — all 0x08/0x09
    internal const string System   = "System";
    internal const string Combat   = "Combat";
    internal const string Other    = "Other";

    internal static readonly string[] AllChannels =
        { Chat, Channels, System, Combat, Other };

    // ── Channel classification ─────────────────────────────────────────────

    internal static string ChannelFor(int chatType) => (uint)chatType switch
    {
        0x02 or 0x03 or 0x04 or 0x0A or 0x0B
            or 0x0C or 0x12 or 0x13                     => Chat,
        0x08 or 0x09                                    => Channels,
        0x00 or 0x05 or 0x0D or 0x14 or 0x17 or 0x18
            or 0x19 or 0x1F                             => System,
        0x06 or 0x07 or 0x11 or 0x15 or 0x16           => Combat,
        _                                               => Other,
    };

    internal static uint ColorFor(string channel) => channel switch
    {
        Chat     => 0xFFE0E0E0,
        Channels => 0xFF7AB8F5,
        System   => 0xFF8CA6BF,
        Combat   => 0xFFD93333,
        _        => 0xFFAAAAAA,
    };

    // ── Sender extraction ──────────────────────────────────────────────────

    private static readonly Regex _sayRe        = new(@"^(.+?) says, """,                           RegexOptions.Compiled);
    private static readonly Regex _tellInRe     = new(@"^(.+?) tells you, """,                      RegexOptions.Compiled);
    private static readonly Regex _tellOutRe    = new(@"^You tell (.+?), """,                       RegexOptions.Compiled);
    private static readonly Regex _allegianceRe = new(@"^(.+?) says on the Allegiance channel, """, RegexOptions.Compiled);
    private static readonly Regex _fellowRe     = new(@"^(.+?) says on the Fellowship channel, """, RegexOptions.Compiled);
    private static readonly Regex _chanSenderRe = new(@"^(.+?) says on the .+? channel, """,        RegexOptions.Compiled);

    internal static string? SenderFor(string text, int chatType) => (uint)chatType switch
    {
        0x02 => Match(_sayRe, text),
        0x03 => Match(_tellInRe, text),
        0x04 => TellOutSender(text),
        0x12 => Match(_allegianceRe, text) ?? Match(_sayRe, text),
        0x13 => Match(_fellowRe, text)     ?? Match(_sayRe, text),
        0x0A => Match(_sayRe, text),
        0x08 => Match(_chanSenderRe, text),
        _    => null,
    };

    private static string? Match(Regex re, string text)
    {
        var m = re.Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? TellOutSender(string text)
    {
        var m = _tellOutRe.Match(text);
        return m.Success ? $"→{m.Groups[1].Value}" : null;
    }
}

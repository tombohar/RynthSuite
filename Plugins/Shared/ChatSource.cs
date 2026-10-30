// ============================================================================
//  RynthSuite - Plugins/Shared/ChatSource.cs
//  Telling a server line from a player's imitation of one.
//
//  Some chat lines are signals: "Your missile attack hit the environment." moves
//  the combat target, a "~ael1 " data line feeds the vault view. Another player
//  can type the same words, so a reader of a signal needs both checks here:
//    - the chat type is not one a player writes in (ACE ChatMessageType, which
//      is the client's eChatTypes: ACE.Entity/Enum/ChatMessageType.cs);
//    - the line STARTS with the marker. The client puts the speaker in front
//      of player text ("Name says, ...", "Name tells you, ...", "Name waves"),
//      so a player's copy never starts with it.
//  Compiled into each plugin that reads a signal (linked source).
// ============================================================================

using System;

namespace RynthCore.Plugin.Shared;

internal static class ChatSource
{
    // Chat types whose text another player writes.
    public const int Speech = 0x02;
    public const int Tell = 0x03;
    public const int OutgoingTell = 0x04;
    public const int Channel = 0x08;
    public const int ChannelSend = 0x09;
    public const int Social = 0x0A;
    public const int SocialSend = 0x0B;
    public const int Emote = 0x0C;
    public const int Allegiance = 0x12;
    public const int Fellowship = 0x13;

    /// <summary>
    /// True when a line of this chat type is a player's words. A caller that
    /// doesn't know the type passes -1 (false), and then only the anchor protects it.
    /// </summary>
    public static bool IsPlayerAuthored(int chatType) => chatType switch
    {
        Speech or Tell or OutgoingTell or Channel or ChannelSend
            or Social or SocialSend or Emote or Allegiance or Fellowship => true,
        _ => false,
    };

    /// <summary>The index just past <paramref name="line"/>'s leading whitespace.</summary>
    public static int ContentStart(string line)
    {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return i;
    }

    /// <summary>True when <paramref name="line"/>, past leading whitespace, starts with <paramref name="marker"/>.</summary>
    public static bool StartsWithMarker(string? line, string marker, StringComparison comparison = StringComparison.Ordinal)
        => line != null && line.AsSpan(ContentStart(line)).StartsWith(marker.AsSpan(), comparison);
}

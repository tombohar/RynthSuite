// RynthChat's channel classifier: a line's [General]/[Trade]/... tag decides the Channels tab (ILT delivers
// those as chat type 0x1C), and the type map covers 0x1C too. Ported from SilentKelpie's PR 12.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthChat;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

internal static class ChatClassifierTests
{
    public static void Register(Runner r)
    {
        r.Add("chat classifier: a [General]/[Trade]/... tag puts the line on Channels whatever its chat type", TaggedLines);
        r.Add("chat classifier: type 0x1C is a channel; the old type map is unchanged", TypeMap);
        r.Add("chat classifier: untagged system and combat lines stay where they were", UntaggedLines);
        r.Add("chat classifier: player chat is tells, say, emotes, fellowship, allegiance, channels and room lines", PlayerChat);
        r.Add("chat buffer: player lines outlast a full ring of combat and come back first on a fresh read", PlayerRing);
        r.Add("chat buffer: an ordinary poll still gets only the new lines; the player ring resizes and turns off", PlayerRingPollAndResize);
    }

    // The plugin's raw text: AC's name links are still in it.
    private const string Link = "<Tell:IIDString:0:Bob>Bob<\\Tell>";

    private static bool Player(string text, int type)
    {
        string channel = ChatClassifier.IsItemInfo(text) ? ChatClassifier.Items
            : ChatClassifier.IsRynthOutput(text) ? ChatClassifier.Rynth
            : ChatClassifier.ChannelFor(text, type);
        return ChatClassifier.IsPlayerChat(channel, type, text);
    }

    private static void PlayerChat()
    {
        Check.True(Player(Link + " tells you, \"hi\"", 0x03), "a tell to you");
        Check.True(Player("You tell " + Link + ", \"hi\"", 0x04), "your tell");
        Check.True(Player(Link + " says, \"hi\"", 0x02), "say");
        Check.True(Player("Bob waves.", 0x0C), "emote");
        Check.True(Player("[Allegiance] " + Link + " says, \"hi\"", 0x12), "allegiance");
        Check.True(Player(Link + " says to your fellowship, \"hi\"", 0x13), "fellowship");
        Check.True(Player("[General] " + Link + " says, \"hi\"", 0x08), "General at 0x08");
        Check.True(Player("[Trade] " + Link + " says, \"WTS\"", 0x1C), "Trade at 0x1C (ILT)");
        Check.True(Player("[General] " + Link + " says, \"ty ty\"", 27), "General at 27");
        Check.True(Player("[Roleplay] Bob@coldeve says, \"hi\"", 0x05), "a network Global room line at a system type");
        Check.True(Player("[Hunters] Bob@coldeve says, \"hi\"", 0x05), "an invite-only network room at a system type");
        Check.True(Player("[Hunters] You say, \"hi\"", 27), "your own line in a room");
        Check.True(Player("[Patron] " + Link + " says, \"hi\"", 0x05), "a patron line at a system type");
        Check.True(Player("Admin tells you, \"hello\"", 0x1F), "an admin's tell");

        Check.False(Player("You say, \"Zojak Equin\"", 0x11), "your spell words");
        Check.False(Player("Drudge Sorcerer says, \"Zojak Equin\"", 0x11), "a monster's spell words");
        Check.False(Player("You slash Drudge for 54 points of slashing damage!", 0x16), "your combat");
        Check.False(Player("Drudge grazes your foot for 4 points of bludgeoning damage!", 0x15), "enemy combat");
        Check.False(Player("You cast Strength Self VII on yourself", 0x07), "magic");
        Check.False(Player("[BANK] Your balances:", 0x05), "a [BANK] system line");
        Check.False(Player("You don't have all the components for this spell.", 26), "a notice");
        Check.False(Player("[RynthAi] Bob says, \"x\"", 0x02), "plugin output, whatever it says");
        Check.False(Player("[RynthAi Item] Bob's Sword", 0x00), "item info");
        Check.False(Player("You have killed 50 Drudges! Your task is complete!", 0x00), "a kill task line");
    }

    private static string[] TextsIn(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var list = new List<string>();
        foreach (var el in doc.RootElement.EnumerateArray()) list.Add(el.GetProperty("text").GetString() ?? "");
        return list.ToArray();
    }

    private static ulong[] SeqsIn(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var list = new List<ulong>();
        foreach (var el in doc.RootElement.EnumerateArray()) list.Add(el.GetProperty("seq").GetUInt64());
        return list.ToArray();
    }

    private static void PlayerRing()
    {
        var buf = new ChatBuffer(capacity: 100, playerCapacity: 50);
        buf.Add(Link + " tells you, \"first\"", 0x03);
        buf.Add("[Allegiance] " + Link + " says, \"second\"", 0x12);
        for (int i = 0; i < 500; i++) buf.Add($"You slash Drudge for {i} points of slashing damage!", 0x16);
        buf.Add(Link + " says to your fellowship, \"third\"", 0x13);
        for (int i = 0; i < 99; i++) buf.Add($"Drudge grazes your foot for {i} points of bludgeoning damage!", 0x15);

        string[] all = TextsIn(buf.BuildJson(0));
        Check.Eq(all.Length, 102, "the 100-line ring plus the two player lines it lost");
        Check.True(all[0].Contains("first") && all[1].Contains("second"), "the old player lines come first");
        Check.True(all[2].Contains("third"), "then the main ring, oldest first");
        ulong[] seqs = SeqsIn(buf.BuildJson(0));
        bool ordered = true;
        for (int i = 1; i < seqs.Length; i++) ordered &= seqs[i] > seqs[i - 1];
        Check.True(ordered, "in seq order, no line twice");
        Check.Eq(TextsIn(buf.BuildJson(seqs[0])).Length, 101, "a read past the first player line starts after it");

        // More player lines than the player ring holds: the oldest go.
        for (int i = 0; i < 60; i++) buf.Add($"{Link} tells you, \"t{i}\"", 0x03);
        for (int i = 0; i < 200; i++) buf.Add($"You slash Drudge for {i} points of slashing damage!", 0x16);
        string[] later = TextsIn(buf.BuildJson(0));
        int tells = 0;
        foreach (string t in later) if (t.Contains(" tells you, ")) tells++;
        Check.Eq(tells, 50, "the newest 50 player lines are kept");
        Check.False(Array.Exists(later, t => t.Contains("first")), "the oldest player line is gone");
        Check.True(later[^1].Contains("slash"), "the main ring is still last");
    }

    private static void PlayerRingPollAndResize()
    {
        var buf = new ChatBuffer(capacity: 100, playerCapacity: 50);
        buf.Add(Link + " tells you, \"old\"", 0x03);
        for (int i = 0; i < 300; i++) buf.Add($"You slash Drudge for {i} points of slashing damage!", 0x16);
        ulong newest = SeqsIn(buf.BuildJson(0))[^1];
        buf.Add(Link + " tells you, \"new\"", 0x03);
        buf.Add("You evaded Drudge!", 0x15);
        string[] poll = TextsIn(buf.BuildJson(newest));
        Check.Eq(poll.Length, 2, "a poll after the last seq seen gets the two new lines only");
        Check.True(poll[0].Contains("new"), "the new tell, once");

        Check.Eq(buf.SetPlayerCapacity(1), 1, "shrunk to 1");
        Check.Eq(buf.PlayerCapacity, 1, "capacity 1");
        string[] one = TextsIn(buf.BuildJson(0));
        Check.False(Array.Exists(one, t => t.Contains("\"old\"")), "the older player line left with the shrink");
        Check.Eq(buf.SetPlayerCapacity(0), -1, "0 turns it off (-1: applied, off)");
        Check.Eq(TextsIn(buf.BuildJson(0)).Length, 100, "off: just the main ring");
        Check.Eq(buf.SetPlayerCapacity(99999), ChatBuffer.MaxPlayerCapacity, "clamped to the most");
    }

    private static void TaggedLines()
    {
        foreach (string tag in new[] { "[General]", "[Trade]", "[LFG]", "[Roleplay]", "[Society]", "[Celestial Hand]", "[Eldrytch Web]", "[Radiant Blood]", "[Admin]" })
        {
            Check.Eq(ChatClassifier.ChannelFor(tag + " Someone says, \"hi\"", 0x1C), ChatClassifier.Channels, tag + " at 0x1C");
            Check.Eq(ChatClassifier.ChannelFor(tag.ToLowerInvariant() + " x", 0x00), ChatClassifier.Channels, tag + " in lower case at type 0");
        }
        Check.False(ChatClassifier.IsChannelTagged("[Bank] Balance: 5"), "[Bank] is not a channel tag");
        Check.False(ChatClassifier.IsChannelTagged(null), "null");
        Check.False(ChatClassifier.IsChannelTagged(""), "empty");
        Check.False(ChatClassifier.IsChannelTagged("General says hi"), "no bracket");
    }

    private static void TypeMap()
    {
        Check.Eq(ChatClassifier.ChannelForType(0x1C), ChatClassifier.Channels, "0x1C");
        Check.Eq(ChatClassifier.ChannelForType(0x08), ChatClassifier.Channels, "0x08");
        Check.Eq(ChatClassifier.ChannelForType(0x09), ChatClassifier.Channels, "0x09");
        Check.Eq(ChatClassifier.ChannelForType(0x03), ChatClassifier.Chat, "tell");
        Check.Eq(ChatClassifier.ChannelForType(0x06), ChatClassifier.Combat, "combat");
        Check.Eq(ChatClassifier.ChannelForType(0x01), ChatClassifier.Other, "unknown");
    }

    private static void UntaggedLines()
    {
        Check.Eq(ChatClassifier.ChannelFor("You are hungry.", 0x00), ChatClassifier.System, "system line");
        Check.Eq(ChatClassifier.ChannelFor("You hit the Drudge for 5.", 0x06), ChatClassifier.Combat, "combat line");
        Check.Eq(ChatClassifier.ChannelFor("[Bank] Balance: 5", 0x00), ChatClassifier.System, "a [Bank] line keeps its type");
    }
}

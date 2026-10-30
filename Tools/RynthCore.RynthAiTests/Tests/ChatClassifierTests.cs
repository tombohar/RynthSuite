// RynthChat's channel classifier: a line's [General]/[Trade]/... tag decides the Channels tab (ILT delivers
// those as chat type 0x1C), and the type map covers 0x1C too. Ported from SilentKelpie's PR 12.
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

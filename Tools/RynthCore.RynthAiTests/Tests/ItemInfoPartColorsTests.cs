using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// 2026-11-11, per-part colours for the one-line item info (ItemInfo/ItemInfoPartColors.cs): the
// line's (part, text) pieces become character ranges, then the engine's "nextspans" payload
// ("start:length:RRGGBB;..."). The prefix and the ", " between parts stay uncoloured.
internal static class ItemInfoPartColorsTests
{
    private const string Prefix = "[RynthAi Item] ";

    public static void Register(Runner r)
    {
        r.Add("item info parts: every part has a colour, None has none", Colours);
        r.Add("item info parts: ranges after the prefix, the commas between parts left out", Ranges);
        r.Add("item info parts: one part's pieces next to each other make one range", SamePartJoined);
        r.Add("item info parts: empty pieces and uncoloured text", EmptyAndUncoloured);
        r.Add("item info parts: at most 32 ranges", Cap);
        r.Add("item info parts: the nextspans payload", Payload);
        r.Add("item info parts: the sample line shows every part", Sample);
    }

    private static List<(ItemInfoPart Part, string Text)> Sword() => new()
    {
        (ItemInfoPart.Name, "Gold Long Sword (Slash Heavy)"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Set, "Noble Relic Set"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Special, "CS"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Stats, "152-236"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Stats, "0.35v"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Bonus, "+18%a"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Spells, "Legendary Blood Thirst"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Spells, "Epic Strength"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Requirements, "Wield Lvl 180"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.Craft, "Craft 8"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.ValueBurden, "Value 9,000"),
        (ItemInfoPart.None, ", "), (ItemInfoPart.ValueBurden, "BU 300"),
    };

    private static void Colours()
    {
        foreach (ItemInfoPart p in System.Enum.GetValues<ItemInfoPart>())
        {
            string c = ItemInfoPartColors.ColorOf(p);
            if (p == ItemInfoPart.None) Check.Eq(c, "", "None keeps the line's colour");
            else Check.True(c.Length == 7 && c[0] == '#' && c[1..].All(System.Uri.IsHexDigit), $"{p}: a #RRGGBB colour ({c})");
        }
        Check.Eq(ItemInfoPartColors.Defaults.Select(d => d.Part).Distinct().Count(), ItemInfoPartColors.Defaults.Length, "each part listed once");
        Check.Eq(ItemInfoPartColors.Defaults.Length, System.Enum.GetValues<ItemInfoPart>().Length - 1, "every part but None listed");
        Check.True(ItemInfoPartColors.Defaults.All(d => d.Label.Length > 0), "every part has a label");
    }

    private static void Ranges()
    {
        var pieces = Sword();
        string line = Prefix + ItemInfoPartColors.Join(pieces);
        var ranges = ItemInfoPartColors.Ranges(pieces, Prefix.Length);
        Check.Eq(ranges[0].Start, Prefix.Length, "the first range starts after the prefix");
        Check.Eq(line.Substring(ranges[0].Start, ranges[0].Length), "Gold Long Sword (Slash Heavy)", "the name");
        Check.Eq(ranges[0].Color, ItemInfoPartColors.ColorOf(ItemInfoPart.Name), "in the name's colour");
        Check.Eq(line.Substring(ranges[1].Start, ranges[1].Length), "Noble Relic Set", "the set, without the comma before it");
        Check.Eq(ranges[1].Color, ItemInfoPartColors.ColorOf(ItemInfoPart.Set), "in the set's colour");
        var texts = ranges.Select(r => line.Substring(r.Start, r.Length)).ToList();
        Check.True(texts.All(t => !t.StartsWith(",") && !t.StartsWith(" ") && !t.EndsWith(",") && !t.EndsWith(" ")), "no range starts or ends on a separator: " + string.Join(" | ", texts));
        for (int i = 1; i < ranges.Count; i++)
            Check.True(ranges[i].Start >= ranges[i - 1].Start + ranges[i - 1].Length, "ranges in order, not overlapping");
        Check.True(ranges.All(r => r.Start + r.Length <= line.Length), "every range inside the line");
    }

    private static void SamePartJoined()
    {
        var pieces = Sword();
        string line = ItemInfoPartColors.Join(pieces);
        var ranges = ItemInfoPartColors.Ranges(pieces);
        var texts = ranges.Select(r => line.Substring(r.Start, r.Length)).ToList();
        Check.True(texts.Contains("152-236, 0.35v"), "damage and variance: one range, the comma inside it: " + string.Join(" | ", texts));
        Check.True(texts.Contains("Legendary Blood Thirst, Epic Strength"), "two spells: one range");
        Check.True(texts.Contains("Value 9,000, BU 300"), "value and burden: one range");
        Check.Eq(ranges.Count, 9, "name, set, imbue, damage, bonus, spells, requirement, craft, value / burden: 9 ranges");
    }

    private static void EmptyAndUncoloured()
    {
        Check.Eq(ItemInfoPartColors.Ranges(new List<(ItemInfoPart, string)>()).Count, 0, "no pieces: no ranges");
        var pieces = new List<(ItemInfoPart Part, string Text)>
        {
            (ItemInfoPart.Name, "Sword"), (ItemInfoPart.Set, ""), (ItemInfoPart.None, ", "), (ItemInfoPart.None, "Keys: 3"),
            (ItemInfoPart.None, ", "), (ItemInfoPart.Name, "Again"),
        };
        var ranges = ItemInfoPartColors.Ranges(pieces, 4);
        Check.Eq(ranges.Count, 2, "an empty piece adds nothing; uncoloured text (not a separator) splits a part");
        Check.Eq(ranges[0], (4, 5, ItemInfoPartColors.ColorOf(ItemInfoPart.Name)), "the first name");
        Check.Eq(ranges[1].Start, 4 + "Sword, Keys: 3, ".Length, "the second, after the uncoloured text");
        Check.Eq(ItemInfoPartColors.Ranges(new List<(ItemInfoPart, string)> { (ItemInfoPart.None, "plain") }).Count, 0, "uncoloured only: none");
        Check.Eq(ItemInfoPartColors.Ranges(new List<(ItemInfoPart, string)> { (ItemInfoPart.Name, "x") }, -5)[0].Start, 0, "a negative offset counts as 0");
    }

    private static void Cap()
    {
        var pieces = new List<(ItemInfoPart Part, string Text)>();
        for (int i = 0; i < 50; i++)
        {
            if (i > 0) pieces.Add((ItemInfoPart.None, ", "));
            pieces.Add((i % 2 == 0 ? ItemInfoPart.Spells : ItemInfoPart.Stats, "p" + i));
        }
        var ranges = ItemInfoPartColors.Ranges(pieces);
        Check.Eq(ranges.Count, ItemInfoPartColors.MaxSpans, "50 alternating parts: the first 32 ranges");
        Check.Eq(ItemInfoPartColors.Payload(ranges, ItemInfoPartColors.Join(pieces).Length).Split(';').Length, 32, "all of them in the payload");
    }

    private static void Payload()
    {
        var ranges = new List<(int, int, string)> { (15, 5, "#E6B450"), (22, 3, "80FF7F") };
        Check.Eq(ItemInfoPartColors.Payload(ranges, 25), "15:5:E6B450;22:3:80FF7F", "start:length:RRGGBB joined by ';', no '#'");
        Check.Eq(ItemInfoPartColors.Payload(new List<(int, int, string)>(), 10), "", "no ranges: empty");
        Check.Eq(ItemInfoPartColors.Payload(ranges, 24), "", "a range past the line's end: none (the engine would refuse it)");
        Check.Eq(ItemInfoPartColors.Payload(new List<(int, int, string)> { (5, 5, "#FFFFFF"), (7, 2, "#FFFFFF") }, 20), "", "overlapping: none");
        Check.Eq(ItemInfoPartColors.Payload(new List<(int, int, string)> { (0, 0, "#FFFFFF") }, 20), "", "an empty range: none");
        Check.Eq(ItemInfoPartColors.Payload(new List<(int, int, string)> { (0, 2, "#FFF") }, 20), "", "a short colour: none");

        var pieces = Sword();
        string line = Prefix + ItemInfoPartColors.Join(pieces) + " (not identified)";
        string payload = ItemInfoPartColors.Payload(ItemInfoPartColors.Ranges(pieces, Prefix.Length), line.Length);
        Check.True(payload.Length > 0 && payload.StartsWith($"{Prefix.Length}:29:E6B450;"), "a real line's payload: " + payload);
        Check.True(payload.Length < 1024, "well under the engine's 1,024 characters");
        Check.Eq(ItemInfoPartColors.NextSpansAction, "nextspans", "the action the engine takes");
    }

    private static void Sample()
    {
        var pieces = ItemInfoPartColors.SamplePieces();
        string line = ItemInfoPartColors.Join(pieces);
        var ranges = ItemInfoPartColors.Ranges(pieces);
        Check.Eq(ranges.Count, ItemInfoPartColors.Defaults.Length, "one range per part");
        for (int i = 0; i < ranges.Count; i++)
        {
            Check.Eq(line.Substring(ranges[i].Start, ranges[i].Length), ItemInfoPartColors.Defaults[i].Label, $"part {i}: its label");
            Check.Eq(ranges[i].Color, ItemInfoPartColors.Defaults[i].Color, $"part {i}: its colour");
        }
    }
}

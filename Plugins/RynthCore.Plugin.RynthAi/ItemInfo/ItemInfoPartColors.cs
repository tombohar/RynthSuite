// ItemInfoPartColors.cs — a colour per part of the one-line item info (2026-11-11).
//
// A player asked for the Mag-style one-liner with its name, armour / damage, spells, set,
// value / burden, workmanship ... each in a colour of its own, instead of one colour for the line.
// MagItemDescription.ToOneLineParts gives the line as (part, text) pieces; this file turns them
// into character ranges and the payload RynthCore's chat takes:
//
//   SendPluginCommand("RynthCore.Chat", "nextspans", "start:length:RRGGBB;start:length:RRGGBB")
//
// (engine UI/Data/ChatOwnColors.cs). Text outside every range (the prefix, the ", " between parts,
// the "not identified" and loot verdict tails) keeps the line's colour: the Info text colour, else
// the chat type's own. No dependencies besides System, so Tools/RynthCore.RynthAiTests compiles it in.
using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

/// <summary>What a piece of the one-line item info is, for its colour. None = the line's own colour.</summary>
internal enum ItemInfoPart
{
    None,
    /// <summary>"[Material ]Name[ (DamageType Mastery)]".</summary>
    Name,
    /// <summary>Equipment set.</summary>
    Set,
    /// <summary>T11 tier, quality, gear grade, property slots, tainted, weapon grade.</summary>
    Tier,
    /// <summary>Armour level, damage, variance, elemental / damage bonuses, base protections.</summary>
    Stats,
    /// <summary>Imbues, procs, armour cleaving, crushing / biting, cleaving / multi-strike, splits, range, slayer.</summary>
    Special,
    /// <summary>Attack, defence and mana conversion percents.</summary>
    Bonus,
    /// <summary>Spell names.</summary>
    Spells,
    /// <summary>The "[D 3, CD 2]" ratings cluster and T11 modifiers.</summary>
    Ratings,
    /// <summary>Wield / activation / use requirements and arcane lore difficulty.</summary>
    Requirements,
    /// <summary>Workmanship ("Craft 8", salvage "Work"), tinks and applied materials.</summary>
    Craft,
    /// <summary>Value and burden.</summary>
    ValueBurden,
}

internal static class ItemInfoPartColors
{
    /// <summary>The engine takes at most this many ranges for one line (ChatOwnColors.MaxSpans).</summary>
    public const int MaxSpans = 32;
    /// <summary>The SendPluginCommand action ("RynthCore.Chat" target) that arms the ranges for the next line.</summary>
    public const string NextSpansAction = "nextspans";

    /// <summary>
    /// Each part's colour ("#RRGGBB"), close to how AC's appraisal panel and chat colour such things:
    /// the name amber, sets green, spells magic blue, ratings cyan, requirements rose, value grey.
    /// <c>Label</c> is what the Item Info window's legend and the sample line call the part.
    /// </summary>
    public static readonly (ItemInfoPart Part, string Label, string Color)[] Defaults =
    {
        (ItemInfoPart.Name,         "Name",               "#E6B450"),
        (ItemInfoPart.Set,          "Set",                "#80FF7F"),
        (ItemInfoPart.Tier,         "Tier / grade",       "#D2D264"),
        (ItemInfoPart.Stats,        "Armour / damage",    "#FFFFFF"),
        (ItemInfoPart.Special,      "Imbues / slayer",    "#FF7FFF"),
        (ItemInfoPart.Bonus,        "Attack / defence %", "#9FD8C8"),
        (ItemInfoPart.Spells,       "Spells",             "#3FBFFF"),
        (ItemInfoPart.Ratings,      "Ratings",            "#3FDCDC"),
        (ItemInfoPart.Requirements, "Requirements",       "#FF9696"),
        (ItemInfoPart.Craft,        "Workmanship / tinks", "#EE921E"),
        (ItemInfoPart.ValueBurden,  "Value / burden",     "#A8A8A8"),
    };

    /// <summary>A part's colour ("#RRGGBB"), or "" for <see cref="ItemInfoPart.None"/> (the line's own).</summary>
    public static string ColorOf(ItemInfoPart part)
    {
        foreach (var d in Defaults)
            if (d.Part == part) return d.Color;
        return string.Empty;
    }

    /// <summary>
    /// The coloured ranges of a line whose text is <paramref name="offset"/> characters of something
    /// else (the prefix) followed by the pieces in order. Pieces of one part next to each other, and
    /// what lies between them (", " between two spells), become one range; empty and uncoloured
    /// pieces make none. At most <see cref="MaxSpans"/>: later parts keep the line's colour.
    /// </summary>
    public static List<(int Start, int Length, string Color)> Ranges(IReadOnlyList<(ItemInfoPart Part, string Text)> pieces, int offset = 0)
    {
        var ranges = new List<(int Start, int Length, string Color)>();
        int pos = Math.Max(0, offset);
        // The open range: its part, start, and end (exclusive) of its last coloured piece.
        ItemInfoPart openPart = ItemInfoPart.None;
        int openStart = 0, openEnd = 0;
        // Uncoloured text since the open range's last piece: only separators may join two pieces.
        bool gapIsSeparator = true;

        void Close()
        {
            if (openPart != ItemInfoPart.None && openEnd > openStart && ranges.Count < MaxSpans)
                ranges.Add((openStart, openEnd - openStart, ColorOf(openPart)));
            openPart = ItemInfoPart.None;
        }

        foreach (var (part, text) in pieces)
        {
            string t = text ?? string.Empty;
            if (t.Length == 0) continue;
            string color = ColorOf(part);
            if (color.Length == 0)
            {
                if (!IsSeparator(t)) gapIsSeparator = false;
                pos += t.Length;
                continue;
            }
            if (part == openPart && gapIsSeparator) openEnd = pos + t.Length;   // the same part again: one range
            else
            {
                Close();
                openPart = part;
                openStart = pos;
                openEnd = pos + t.Length;
            }
            gapIsSeparator = true;
            pos += t.Length;
        }
        Close();
        return ranges;
    }

    /// <summary>", " and the like: what may sit inside one range between two pieces of a part.</summary>
    private static bool IsSeparator(string t)
    {
        foreach (char c in t)
            if (c != ',' && c != ' ' && c != ';') return false;
        return true;
    }

    /// <summary>
    /// The ranges as the engine's "nextspans" payload: "start:length:RRGGBB" joined by ';'
    /// ("" when there are none, or when a range doesn't fit a line <paramref name="lineLength"/> long).
    /// </summary>
    public static string Payload(IReadOnlyList<(int Start, int Length, string Color)> ranges, int lineLength)
    {
        var sb = new StringBuilder(ranges.Count * 14);
        int prevEnd = 0;
        foreach (var (start, length, color) in ranges)
        {
            string hex = color.StartsWith('#') ? color[1..] : color;
            if (start < prevEnd || length < 1 || start + length > lineLength || hex.Length != 6) return string.Empty;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(start).Append(':').Append(length).Append(':').Append(hex);
            prevEnd = start + length;
        }
        return sb.ToString();
    }

    /// <summary>
    /// A sample line for the window's Test button: every part's label, comma-separated, as pieces
    /// (so it is coloured exactly like a real line).
    /// </summary>
    public static List<(ItemInfoPart Part, string Text)> SamplePieces()
    {
        var pieces = new List<(ItemInfoPart Part, string Text)>(Defaults.Length * 2);
        foreach (var d in Defaults)
        {
            if (pieces.Count > 0) pieces.Add((ItemInfoPart.None, ", "));
            pieces.Add((d.Part, d.Label));
        }
        return pieces;
    }

    /// <summary>The pieces' text, joined.</summary>
    public static string Join(IReadOnlyList<(ItemInfoPart Part, string Text)> pieces)
    {
        var sb = new StringBuilder(256);
        foreach (var (_, text) in pieces) sb.Append(text);
        return sb.ToString();
    }
}

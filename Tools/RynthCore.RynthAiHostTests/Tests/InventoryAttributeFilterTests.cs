using RynthCore.Engine.Compatibility;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The engine Inventory panel's attribute filter (RynthCore Compatibility/InventoryAttributeFilter.cs,
// linked): rule matching, unidentified items, all / any, the value conversions and the saved line.
internal static class InventoryAttributeFilterTests
{
    public static void Register(Runner r)
    {
        r.Add("inv filter: number, flag, text and slot rules", RuleKinds);
        r.Add("inv filter: appraised rules on an unidentified item are unknown", Unknowns);
        r.Add("inv filter: match all vs match any, off and empty filters", Modes);
        r.Add("inv filter: percents, wield level, material and set names", Conversions);
        r.Add("inv filter: saved line round-trips and skips broken rules", Persistence);
    }

    /// <summary>Hand-filled facts: absent values are 0 / false / "", as in the engine.</summary>
    private sealed class Facts : IInventoryFilterFacts
    {
        public bool Identified { get; set; } = true;
        public uint SlotMask { get; set; }
        public readonly System.Collections.Generic.Dictionary<FilterAttr, double> Numbers = new();
        public readonly System.Collections.Generic.Dictionary<FilterAttr, bool> Flags = new();
        public readonly System.Collections.Generic.Dictionary<FilterAttr, string> Texts = new();
        public double Number(FilterAttrInfo a) => Numbers.TryGetValue(a.Id, out double v) ? v : 0;
        public bool Flag(FilterAttrInfo a) => Flags.TryGetValue(a.Id, out bool v) && v;
        public string Text(FilterAttrInfo a) => Texts.TryGetValue(a.Id, out string? v) ? v : "";
    }

    private static InventoryFilterRule Rule(FilterAttr a, FilterOp op, double n = 0, string text = "", int choice = 0) =>
        new() { Attr = a, Op = op, Number = n, Text = text, Choice = choice };

    private static FilterVerdict One(InventoryFilterRule rule, Facts f) => InventoryAttributeFilter.EvaluateRule(rule, f);

    private static void RuleKinds()
    {
        var f = new Facts { SlotMask = 0x00008000 };   // neck
        f.Numbers[FilterAttr.ArmorLevel] = 300;
        f.Flags[FilterAttr.Imbued] = true;
        f.Texts[FilterAttr.Spells] = "Legendary Strength\nEpic Endurance";

        Check.Eq(One(Rule(FilterAttr.ArmorLevel, FilterOp.AtLeast, 300), f), FilterVerdict.Match, "300 >= 300");
        Check.Eq(One(Rule(FilterAttr.ArmorLevel, FilterOp.AtLeast, 301), f), FilterVerdict.NoMatch, "300 >= 301");
        Check.Eq(One(Rule(FilterAttr.ArmorLevel, FilterOp.AtMost, 300), f), FilterVerdict.Match, "300 <= 300");
        Check.Eq(One(Rule(FilterAttr.ArmorLevel, FilterOp.Equal, 300), f), FilterVerdict.Match, "300 = 300");
        Check.Eq(One(Rule(FilterAttr.ArmorLevel, FilterOp.NotEqual, 300), f), FilterVerdict.NoMatch, "300 != 300");
        Check.Eq(One(Rule(FilterAttr.MaxDamage, FilterOp.AtMost, 10), f), FilterVerdict.Match, "absent number is 0");

        Check.Eq(One(Rule(FilterAttr.Imbued, FilterOp.Yes), f), FilterVerdict.Match, "imbued yes");
        Check.Eq(One(Rule(FilterAttr.Imbued, FilterOp.No), f), FilterVerdict.NoMatch, "imbued no");
        Check.Eq(One(Rule(FilterAttr.Retained, FilterOp.No), f), FilterVerdict.Match, "absent flag is no");

        Check.Eq(One(Rule(FilterAttr.Spells, FilterOp.Contains, text: "legendary str"), f), FilterVerdict.Match, "case-insensitive contains");
        Check.Eq(One(Rule(FilterAttr.Spells, FilterOp.NotContains, text: "Legendary"), f), FilterVerdict.NoMatch, "doesn't contain");
        Check.Eq(One(Rule(FilterAttr.Spells, FilterOp.Contains, text: "strength epic"), f), FilterVerdict.NoMatch,
            "text can't span two spells");
        Check.Eq(One(Rule(FilterAttr.Spells, FilterOp.NotContains, text: ""), f), FilterVerdict.Match, "empty text tests nothing");

        int neck = System.Array.IndexOf(InventoryAttributeFilter.SlotNames, "Neck");
        int finger = System.Array.IndexOf(InventoryAttributeFilter.SlotNames, "Finger");
        Check.Eq(One(Rule(FilterAttr.Slot, FilterOp.Is, choice: neck), f), FilterVerdict.Match, "fits neck");
        Check.Eq(One(Rule(FilterAttr.Slot, FilterOp.Is, choice: finger), f), FilterVerdict.NoMatch, "not a ring");
        Check.Eq(One(Rule(FilterAttr.Slot, FilterOp.IsNot, choice: finger), f), FilterVerdict.Match, "is not a ring");
        Check.True(InventoryAttributeFilter.FitsSlot(0x00000200, System.Array.IndexOf(InventoryAttributeFilter.SlotNames, "Chest")),
            "chest underclothes count as chest");
        Check.Eq(InventoryAttributeFilter.SlotNames.Length, InventoryAttributeFilter.SlotMasks.Length, "a mask per slot name");
    }

    private static void Unknowns()
    {
        var f = new Facts { Identified = false };
        f.Numbers[FilterAttr.Value] = 5000;
        Check.Eq(One(Rule(FilterAttr.ArmorLevel, FilterOp.AtLeast, 1), f), FilterVerdict.Unknown, "appraised rule needs an identify");
        Check.Eq(One(Rule(FilterAttr.Value, FilterOp.AtLeast, 1000), f), FilterVerdict.Match, "public rule works without one");
        Check.Eq(One(Rule(FilterAttr.Spells, FilterOp.Contains, text: ""), f), FilterVerdict.Match, "an empty text rule never hides");
        Check.False(InventoryAttributeFilter.Info(FilterAttr.Value).NeedsId, "value is public");
        Check.True(InventoryAttributeFilter.Info(FilterAttr.WieldLevel).NeedsId, "level to wield is appraised");
    }

    private static void Modes()
    {
        var f = new Facts { Identified = false };
        f.Numbers[FilterAttr.Value] = 5000;
        var filter = new InventoryAttributeFilter { Enabled = true };
        filter.Rules.Add(Rule(FilterAttr.Value, FilterOp.AtLeast, 1000));
        filter.Rules.Add(Rule(FilterAttr.ArmorLevel, FilterOp.AtLeast, 300));

        Check.Eq(filter.Evaluate(f), FilterVerdict.Unknown, "all: one match + one unknown is unknown");
        filter.Rules[0].Number = 10_000;
        Check.Eq(filter.Evaluate(f), FilterVerdict.NoMatch, "all: a no-match wins over unknown");

        filter.MatchAny = true;
        Check.Eq(filter.Evaluate(f), FilterVerdict.Unknown, "any: no-match + unknown is unknown");
        filter.Rules[0].Number = 1000;
        Check.Eq(filter.Evaluate(f), FilterVerdict.Match, "any: a match wins over unknown");

        f.Identified = true;
        filter.Rules[0].Number = 10_000;
        Check.Eq(filter.Evaluate(f), FilterVerdict.NoMatch, "any: nothing matches");

        filter.Enabled = false;
        Check.Eq(filter.Evaluate(f), FilterVerdict.Match, "off keeps everything");
        Check.False(filter.Active, "off is not active");
        Check.True(filter.NeedsIdentify, "an armor level rule needs identifies");
        var empty = new InventoryAttributeFilter { Enabled = true };
        Check.False(empty.Active, "no rules is not active");
        Check.Eq(empty.Evaluate(f), FilterVerdict.Match, "no rules keeps everything");
    }

    private static void Conversions()
    {
        Check.Near(InventoryAttributeFilter.Scale(FilterSource.FloatModPercent, 1.15), 15, 1e-9, "attack mod 1.15 is 15%");
        Check.Near(InventoryAttributeFilter.Scale(FilterSource.FloatModPercent, 0), 0, 1e-9, "absent mod is 0%");
        Check.Near(InventoryAttributeFilter.Scale(FilterSource.FloatPercent, 0.05), 5, 1e-9, "mana conversion 0.05 is 5%");
        Check.True(InventoryAttributeFilter.CompareNumber(InventoryAttributeFilter.Scale(FilterSource.FloatModPercent, 1.15), FilterOp.Equal, 15),
            "float rounding still equals the typed percent");

        Check.Eq(InventoryAttributeFilter.WieldLevel(7, 150, 0, 0), 150, "level in the first requirement");
        Check.Eq(InventoryAttributeFilter.WieldLevel(2, 300, 7, 180), 180, "level in the second requirement");
        Check.Eq(InventoryAttributeFilter.WieldLevel(2, 300, 0, 0), 0, "a skill requirement isn't a level");

        Check.Eq(InventoryAttributeFilter.MaterialName(15), "Black Garnet", "MaterialType 15");
        Check.Eq(InventoryAttributeFilter.MaterialName(77), "Teak", "the last material");
        Check.Eq(InventoryAttributeFilter.MaterialName(0), "", "no material");
        Check.Eq(InventoryAttributeFilter.MaterialName(78), "", "past the list");
        Check.Eq(InventoryAttributeFilter.SetName(13), "Soldiers", "set 13");
        Check.Eq(InventoryAttributeFilter.SetName(81), "Cloak War Magic", "set 81");
        Check.Eq(InventoryAttributeFilter.SetName(43), "Olthoi Armor C Rat", "a run of capitals splits before the next word");
        Check.Eq(InventoryAttributeFilter.SetName(89), "Ninja New", "underscore");
        Check.Eq(InventoryAttributeFilter.SetName(140), "Paragon Melee", "the last set");
        Check.Eq(InventoryAttributeFilter.SetName(200), "Set 200", "a custom set id");
        Check.Eq(InventoryAttributeFilter.SetName(0), "", "no set");
    }

    private static void Persistence()
    {
        var f = new InventoryAttributeFilter { Enabled = true, MatchAny = true };
        f.Rules.Add(Rule(FilterAttr.ArmorLevel, FilterOp.AtLeast, 300));
        f.Rules.Add(Rule(FilterAttr.Spells, FilterOp.Contains, text: "Legendary; Epic, 100%"));
        f.Rules.Add(Rule(FilterAttr.Slot, FilterOp.IsNot, choice: 3));
        f.Rules.Add(Rule(FilterAttr.AttackBonus, FilterOp.Equal, 12.5));

        string line = f.Serialize();
        Check.False(line.Contains('\n'), "one line");
        InventoryAttributeFilter back = InventoryAttributeFilter.Parse(line);
        Check.True(back.Enabled && back.MatchAny, "on, any");
        Check.Eq(back.Rules.Count, 4, "four rules");
        Check.Eq(back.Rules[0].Attr, FilterAttr.ArmorLevel, "rule 1 attribute");
        Check.Eq(back.Rules[0].Op, FilterOp.AtLeast, "rule 1 operator");
        Check.Near(back.Rules[0].Number, 300, 1e-9, "rule 1 number");
        Check.Eq(back.Rules[1].Text, "Legendary; Epic, 100%", "separators in text survive");
        Check.Eq(back.Rules[2].Choice, 3, "slot choice");
        Check.Near(back.Rules[3].Number, 12.5, 1e-9, "fractional number");
        Check.Eq(back.Serialize(), line, "stable");

        InventoryAttributeFilter broken = InventoryAttributeFilter.Parse("v1;on;all;nosuch,ge,1,0,;al,has,1,0,;al,le,abc,0,;dmg,ge,40,0,");
        Check.Eq(broken.Rules.Count, 2, "unknown attribute and wrong operator skipped");
        Check.Near(broken.Rules[0].Number, 0, 1e-9, "a bad number reads as 0");
        Check.Eq(broken.Rules[1].Attr, FilterAttr.MaxDamage, "later good rule kept");
        Check.Eq(InventoryAttributeFilter.Parse("").Rules.Count, 0, "empty line");
        Check.Eq(InventoryAttributeFilter.Parse("v9;on;all;al,ge,1,0,").Rules.Count, 0, "unknown version ignored");
        Check.Eq(InventoryAttributeFilter.Parse("v1;on;all;slot,is,0,99,").Rules[0].Choice,
            InventoryAttributeFilter.SlotNames.Length - 1, "slot choice clamped");
    }
}

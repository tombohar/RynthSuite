using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.T11;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// "Add to loot profile" with "Include T11 attributes" (2026-10-07): the T11 conditions a
// clicked item adds, on the server-format weapon and armor text LootT11Tests parses.
internal static partial class Program
{
    private static void RunLootAddT11Tests()
    {
        Console.WriteLine("\n-- LootAdd: include T11 attributes --");
        TestAddT11Weapon();
        TestAddT11Armor();
        TestAddT11EdgeCases();
        TestAddT11GradedGear();
    }

    private static void TestAddT11GradedGear()
    {
        // Graded armor with a stamped tier: exact tier, gear grade, modifiers; no slots or Tainted.
        LootItemFacts f = T11Facts("T11 - Celdon Breastplate", AcObjectClass.Armor, null, T11GradedArmorUse);
        f.T11 = T11ItemInfo.Parse(f.Name, null, T11GradedArmorUse, new T11ServerProps(18, 0, -1));
        LootItemRuleDraft? d = LootItemRules.Build(f, new LootItemRuleOptions { IncludeT11 = true }, out _);
        Check(d != null, "add t11 graded: armor builds");
        if (d == null) return;
        List<string> t = T11Conds(d.Rule);
        Check(t.Contains(GE(T11Keys.EstimatedTier, 18)), "add t11 graded: stamped tier");
        Check(t.Contains(GE(T11Keys.GearGrade, 12)), "add t11 graded: gear grade B+");
        Check(t.Contains(GE(T11Keys.ForModifier(50), 60)) && t.Contains(GE(T11Keys.ForModifier(19), 50)), "add t11 graded: modifiers");
        Check(!t.Any(c => c.EndsWith("," + T11Keys.Tainted) || c.EndsWith("," + T11Keys.PropertySlots)
            || c.EndsWith("," + T11Keys.FreePropertySlots)), "add t11 graded: no bag state");
        Eq(d.Notes.Count, 0, "add t11 graded: no notes");
        Check(LootItemRules.PreviewLines(d.Rule).Any(l => l.Contains("T11: Gear Grade")), "add t11 graded: preview names Gear Grade");

        // Jewelry: the key-54 Cast on Strike is catalogued now, so it gets a condition instead of a note.
        LootItemFacts ring = T11Facts("T11 - Ring", AcObjectClass.Jewelry, null, T11JewelryUse);
        LootItemRuleDraft? r = LootItemRules.Build(ring, new LootItemRuleOptions { IncludeT11 = true }, out _);
        List<string> rt = r == null ? new List<string>() : T11Conds(r.Rule);
        Check(rt.Contains(GE(T11Keys.ForModifier(54), 75)), "add t11 graded: jewelry Cast on Strike power");
        Check(rt.Contains(GE(T11Keys.CastOnStrikeCount, 1)), "add t11 graded: jewelry proc counted");
        Check(rt.Contains(GE(T11Keys.GearGrade, 14)), "add t11 graded: jewelry gear grade A");
        Check(r != null && r.Notes.Count == 0, "add t11 graded: jewelry has no unknown-modifier note");
    }

    /// <summary>A condition as "type:line,line" for order-sensitive comparisons.</summary>
    private static string CondText(VTankLootCondition c) => c.NodeType + ":" + string.Join(",", c.DataLines);

    private static string GE(int key, int v) => $"{VTankNodeTypes.LongValKeyGE}:{v},{key}";
    private static string EQ(int key, int v) => $"{VTankNodeTypes.LongValKeyE}:{v},{key}";

    private static List<string> T11Conds(VTankLootRule r) =>
        r.Conditions.Where(c => c.DataLines.Count == 2 && int.TryParse(c.DataLines[1], out int k) && T11Keys.IsVirtual(k))
                    .Select(CondText).ToList();

    private static LootItemFacts T11Facts(string name, AcObjectClass cls, string? longDesc, string? use) => new()
    {
        Id = 0x7000_0001,
        Name = name,
        ObjectClass = (int)cls,
        T11 = T11ItemInfo.Parse(name, longDesc, use),
    };

    private static void TestAddT11Weapon()
    {
        LootItemFacts f = T11Facts("T11 - Tachi", AcObjectClass.MeleeWeapon, T11WeaponLongDesc, null);

        LootItemRuleDraft? off = LootItemRules.Build(f, new LootItemRuleOptions(), out _);
        Eq(off == null ? -1 : T11Conds(off.Rule).Count, 0, "add t11: unchecked adds no T11 conditions");

        LootItemRuleDraft? d = LootItemRules.Build(f, new LootItemRuleOptions { IncludeT11 = true }, out string err);
        Check(d != null, "add t11: weapon builds (" + err + ")");
        if (d == null) return;
        Eq(CondText(d.Rule.Conditions[0]), $"{VTankNodeTypes.ObjectClass}:{(int)AcObjectClass.MeleeWeapon}", "add t11: name + class first");
        Eq(string.Join(" | ", T11Conds(d.Rule)), string.Join(" | ", new[]
        {
            EQ(T11Keys.IsT11, 1),
            GE(T11Keys.EstimatedTier, 13),
            GE(T11Keys.WeaponGrade, 12),
            GE(T11Keys.DamagePercent, 86),
            GE(T11Keys.ForModifier(28), 41),   // Damage Rating
            GE(T11Keys.ForModifier(29), 10),   // Crit Damage Rating
            GE(T11Keys.ForModifier(44), 2),    // Pct HP Damage: a slot special with a value
            GE(T11Keys.CastOnStrikeCount, 1),
        }), "add t11: weapon conditions, at least this item's");
        Check(d.Rule.Conditions.All(c => CondText(c) != EQ(T11Keys.HasSlotSpecial, 1)), "add t11: valued special needs no Has Slot Special");
        Check(d.Rule.Conditions.All(c => CondText(c) != EQ(T11Keys.ZoneLocked, 1)), "add t11: zone lock left out");
        Eq(d.Notes.Count, 0, "add t11: weapon has no notes");
        Check(LootItemRules.PreviewLines(d.Rule).Any(l => l.Contains("T11 Mod: Damage Rating")), "add t11: preview names the modifier key");

        var dropped = new List<string>();
        LootRule? native = LootItemRules.ToNative(d.Rule, dropped);
        Eq(dropped.Count, 0, "add t11: native format keeps every T11 condition");
        Eq(native?.Conditions.Count ?? -1, d.Rule.Conditions.Count, "add t11: native condition count");
    }

    private static void TestAddT11Armor()
    {
        LootItemFacts f = T11Facts("T11 - Celdon Breastplate", AcObjectClass.Armor, T11ArmorLongDesc, T11ArmorUse);
        LootItemRuleDraft? d = LootItemRules.Build(f, new LootItemRuleOptions { IncludeT11 = true }, out _);
        Check(d != null, "add t11: armor builds");
        if (d == null) return;
        List<string> t = T11Conds(d.Rule);
        Check(t.Contains(GE(T11Keys.EstimatedTier, 16)), "add t11: armor tier from the Triune Weave gate");
        Check(!t.Any(c => c.EndsWith("," + T11Keys.WeaponGrade)), "add t11: no grade on armor");
        Check(t.Contains(GE(T11Keys.ForModifier(50), 8)), "add t11: Damage Resist");
        Check(t.Contains(GE(T11Keys.ForModifier(19), 120)), "add t11: Max Health");
        Check(t.Contains(GE(T11Keys.ForModifier(47), 2)), "add t11: Max Health Pct");
        Check(!t.Any(c => c.EndsWith("," + T11Keys.ForModifier(60))), "add t11: unknown modifier has no condition");
        Check(d.Notes.Any(n => n.Contains("Modifier 60")), "add t11: unknown modifier named in a note");
    }

    private static void TestAddT11EdgeCases()
    {
        // Not T11 gear: a note, no conditions.
        var plain = new LootItemFacts { Id = 2, Name = "Iron Mace", ObjectClass = (int)AcObjectClass.MeleeWeapon, T11 = T11ItemInfo.None };
        LootItemRuleDraft? p = LootItemRules.Build(plain, new LootItemRuleOptions { IncludeT11 = true }, out _);
        Eq(p == null ? -1 : T11Conds(p.Rule).Count, 0, "add t11: non-T11 item adds nothing");
        Check(p != null && p.Notes.Any(n => n.StartsWith("Not a T11 item")), "add t11: non-T11 note");

        // T11 by name but not identified yet: Is T11 only, and a note to assess it.
        LootItemFacts unid = T11Facts("T11 - Tachi", AcObjectClass.MeleeWeapon, null, null);
        LootItemRuleDraft? u = LootItemRules.Build(unid, new LootItemRuleOptions { IncludeT11 = true }, out _);
        Eq(u == null ? "" : string.Join(" | ", T11Conds(u.Rule)), EQ(T11Keys.IsT11, 1), "add t11: unidentified gives Is T11 only");
        Check(u != null && u.Notes.Any(n => n.Contains("hasn't been identified")), "add t11: unidentified note");

        // A value-less slot special is required through Has Slot Special.
        LootItemFacts ring = T11Facts("T11 - Ring", AcObjectClass.Jewelry, null, "Modifiers:\n- Fortify Vitals\n");
        LootItemRuleDraft? r = LootItemRules.Build(ring, new LootItemRuleOptions { IncludeT11 = true }, out _);
        Check(r != null && T11Conds(r.Rule).Contains(EQ(T11Keys.HasSlotSpecial, 1)), "add t11: value-less special -> Has Slot Special");

        // "Items like this": T11 conditions count, so the "matches every X" warning goes away.
        LootItemFacts like = T11Facts("T11 - Tachi", AcObjectClass.MeleeWeapon, T11WeaponLongDesc, null);
        LootItemRuleDraft? l = LootItemRules.Build(like, new LootItemRuleOptions { Match = LootItemMatch.Like, IncludeT11 = true }, out _);
        Check(l != null && !l.Notes.Any(n => n.StartsWith("Nothing but the class")), "add t11: like + T11 is not class-only");
        Check(l != null && l.Rule.Conditions.All(c => c.NodeType != VTankNodeTypes.StringValueMatch), "add t11: like still has no name");
        LootItemRuleDraft? bare = LootItemRules.Build(plain, new LootItemRuleOptions { Match = LootItemMatch.Like, IncludeT11 = true }, out _);
        Check(bare != null && bare.Notes.Any(n => n.StartsWith("Nothing but the class")), "add t11: like + non-T11 still warns class-only");
    }
}

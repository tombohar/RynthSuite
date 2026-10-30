using RynthCore.Loot;
using RynthCore.Loot.T11;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using N = RynthCore.Loot.VTank.VTankNodeTypes;

namespace RynthCore.RynthAiHostTests.Tests;

// T11 (ACECustom tier 11+) items through the real evaluator: the virtual T11 keys answered by
// WorldObject.Values, the wield requirement and ratings the server strips put back from the
// item text, and "Can Wield" fed by the wield counters the server plugin reports (RynthILT reads the
// "/aug" reply and applies its item-aug override since 2026-10-20; RynthAi.Hub SetPlayerCounter).
internal static class T11LootTests
{
    private const string WeaponLongDesc =
        "Property Details:\n" +
        "- Weapon Grade: A- (91% of max damage)\n" +
        "- Wield requires: 2,500 Item Augmentations\n" +
        "\n" +
        "Modifiers:\n" +
        "- Damage Rating +30 [14-69]\n" +
        "- Crit Chance +4 [1-8]\n";

    private const string ArmorUse =
        "Modifiers:\n" +
        "- Damage Resist +6 [2-12]\n" +
        "- Max Health +100 [40-200]\n";

    public static void Register(Runner r)
    {
        r.Add("loot t11: virtual keys read the item text", VirtualKeys);
        r.Add("loot t11: stripped wield requirement 158/159/160 restored", StrippedWield);
        r.Add("loot t11: modifier ratings feed Gear* keys and TotalRatings", ModifierRatings);
        r.Add("loot t11: a live rating wins over the modifier text", LiveRatingWins);
        r.Add("loot t11: retail items are untouched", RetailUntouched);
        r.Add("loot t11: Can Wield from the counters the server plugin reports (unknown without one)", CanWield);
        r.Add("loot t11: server tier and quality properties", ServerProps);
    }

    private static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);

    private static bool M(WorldObject item, params VTankLootCondition[] conds)
    {
        var rule = new VTankLootRule { Name = "r", Action = VTankLootAction.Keep };
        rule.Conditions.AddRange(conds);
        return VTankLootEvaluator.Match(rule, item);
    }

    private static string K(int key) => key.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Item Weapon() => new Item("T11 - Tachi", AcObjectClass.MeleeWeapon).Str(16, WeaponLongDesc);

    private static void VirtualKeys()
    {
        T11ItemSupport.Reset();
        WorldObject w = Weapon();
        Check.Eq(w.Values(T11Keys.IsT11, -5), 1, "IsT11");
        Check.Eq(w.Values(T11Keys.EstimatedTier, -5), 12, "2,500 gate = T12");
        Check.Eq(w.Values(T11Keys.WeaponGrade, -5), 13, "A- = 13");
        Check.True(M(w, C(N.LongValKeyGE, "12", K(T11Keys.WeaponGrade))), "rule: grade B+ or better");
        Check.False(M(w, C(N.LongValKeyGE, "14", K(T11Keys.WeaponGrade))), "rule: grade A or better");
        Check.True(M(w, C(N.LongValKeyGE, "25", K(T11Keys.ForModifier(28)))), "rule: Damage Rating mod >= 25");
        Check.True(M(w, C(N.LongValKeyE, "0", K(T11Keys.ZoneLocked))), "rule: not zone locked");
        Check.Eq(w.Values(T11Keys.Base + 0x1FF, -5), -5, "unused virtual id = default");
        Check.Eq(new Item("Tachi").Wo.Values(T11Keys.IsT11, -5), 0, "retail item: IsT11 = 0");
    }

    private static void StrippedWield()
    {
        T11ItemSupport.Reset();
        WorldObject w = Weapon();
        Check.Eq(w.Values(158, 0), T11Keys.WieldRequirementInt64Stat, "158 = Int64 Property");
        Check.Eq(w.Values(159, 0), 9008, "159 = item augs");
        Check.Eq(w.Values(160, 0), 2500, "160 = gate");
        Check.True(M(w, C(N.LongValKeyLE, "2500", "160")), "rule: wield difficulty <= 2500");
        Check.False(M(w, C(N.LongValKeyLE, "2000", "160")), "rule: wield difficulty <= 2000");
        Check.Eq(w.Values(161, -1), -1, "other keys keep the default");
    }

    private static void ModifierRatings()
    {
        T11ItemSupport.Reset();
        WorldObject w = Weapon();
        Check.Eq(w.Values(370, 0), 30, "370 Damage Rating from text");
        Check.Eq(w.Values(372, 0), 4, "372 Crit from text");
        Check.True(M(w, C(N.TotalRatingsGE, "34")), "TotalRatings counts modifier ratings");
        Check.False(M(w, C(N.TotalRatingsGE, "35")), "TotalRatings exact");

        WorldObject armor = new Item("T11 - Celdon Breastplate", AcObjectClass.Armor).Str(14, ArmorUse);
        Check.Eq(armor.Values(371, 0), 6, "371 from the Use string");
        Check.True(M(armor, C(N.TotalRatingsGE, "106")), "armor TotalRatings 371 + 379");
    }

    private static void LiveRatingWins()
    {
        T11ItemSupport.Reset();
        WorldObject w = Weapon().Int(370, 7);
        Check.Eq(w.Values(370, 0), 7, "a property the item has is never replaced");
    }

    private static void RetailUntouched()
    {
        T11ItemSupport.Reset();
        WorldObject retail = new Item("Tachi", AcObjectClass.MeleeWeapon).Str(16, WeaponLongDesc);
        Check.Eq(retail.Values(160, 0), 0, "no T11 prefix: no stripped-key fallback");
        Check.Eq(retail.Values(370, -1), -1, "no T11 prefix: rating default kept");
    }

    private static void CanWield()
    {
        T11ItemSupport.Reset();
        WorldObject w = Weapon();
        Check.Eq(w.Values(T11Keys.CanWield, -5), -1, "unknown until a server plugin reports the counters (retail: never)");
        Check.True(T11ItemSupport.SetCounter((uint)T11Counter.ItemAugmentations, 2400), "RynthILT reports 2,400 item augs");
        Check.Eq(w.Values(T11Keys.CanWield, -5), 0, "2,400 < 2,500");
        Check.False(M(w, C(N.LongValKeyE, "1", K(T11Keys.CanWield))), "rule: wieldable fails");
        T11ItemSupport.SetCounter((uint)T11Counter.ItemAugmentations, 2500);   // RynthILT's override, applied there
        Check.Eq(w.Values(T11Keys.CanWield, -5), 1, "2,500 meets the gate");
        T11ItemSupport.SetCounter((uint)T11Counter.ItemAugmentations, -1);
        Check.Eq(w.Values(T11Keys.CanWield, -5), -1, "reported unknown again: unknown");
        Check.False(T11ItemSupport.SetCounter(12345, 7), "an id that is no wield counter is refused");
        Check.Eq(new Item("T11 - Robe").Wo.Values(T11Keys.CanWield, -5), -1, "not appraised yet: unknown");
        Check.Eq(new Item("T11 - Robe").Str(16, "A robe.").Wo.Values(T11Keys.CanWield, -5), 1, "appraised, no gate: wieldable");
        T11ItemSupport.Reset();
    }

    // ACECustom sends ZcTier (50109) and WeaponAugScaleTier / Quality (9061 / 9060) in the appraisal.
    private static void ServerProps()
    {
        T11ItemSupport.Reset();
        Item weapon = Weapon();
        WorldObject w = weapon;
        Check.Eq(w.Values(T11Keys.EstimatedTier, -5), 12, "before the props: gate estimate T12");
        Check.Eq(w.Values(T11Keys.TierIsExact, -5), 0, "before the props: estimate");
        weapon.Int(T11Catalog.PropWeaponAugScaleTier, 14).Int(T11Catalog.PropWeaponAugScaleQuality, 873);
        Check.Eq(w.Values(T11Keys.EstimatedTier, -5), 14, "props arriving later re-parse: stamped T14");
        Check.Eq(w.Values(T11Keys.TierIsExact, -5), 1, "stamped tier is exact");
        Check.True(M(w, C(N.LongValKeyGE, "850", K(T11Catalog.PropWeaponAugScaleQuality))), "rule: raw quality >= 850");
        Check.False(M(w, C(N.LongValKeyGE, "900", K(T11Catalog.PropWeaponAugScaleQuality))), "rule: raw quality >= 900");

        // No name prefix: the stamped tier alone makes it T11 and turns on the stripped-key fallback.
        WorldObject plain = new Item("Celdon Breastplate", AcObjectClass.Armor)
            .Str(16, "Wield requires: 4,000 Item Augmentations\n").Str(14, ArmorUse).Int(T11Catalog.PropZcTier, 18);
        Check.Eq(plain.Values(T11Keys.IsT11, -5), 1, "ZcTier 18 marks T11 without the prefix");
        Check.Eq(plain.Values(T11Keys.EstimatedTier, -5), 18, "tier from ZcTier");
        Check.Eq(plain.Values(160, 0), 4000, "stripped wield gate restored on a ZcTier item");
        Check.Eq(plain.Values(371, 0), 6, "stripped rating restored on a ZcTier item");
        T11ItemSupport.Reset();
    }
}

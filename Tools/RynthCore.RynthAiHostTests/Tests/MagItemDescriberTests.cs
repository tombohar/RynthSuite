using System.Collections.Generic;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.ItemInfo;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Mag-style item info line (/ra iteminfo, ID-on-click) against what the ACECustom appraisal
// panel shows for the same item: whole-number damage range, signed bonuses, Crushing Blow as the
// dealt multiplier, and the imbue strength / grade / wield gate that only exist as text.
internal static class MagItemDescriberTests
{
    public static void Register(Runner r)
    {
        r.Add("iteminfo: T11 fire staff matches the appraisal panel", T11FireStaff);
        r.Add("iteminfo: Biting Strike and a defense penalty", BitingStrikeAndPenalty);
        r.Add("iteminfo: retail imbues stay bare labels without detail text", RetailImbuesBare);
        r.Add("iteminfo: Int64 wield requirement never prints as a skill", Int64WieldHidden);
        r.Add("iteminfo: T11 armor modifiers print on the ratings line", T11ArmorModifiers);
        r.Add("iteminfo: T11 tier, gear grade, slots and tainted", T11GradedArmor);
    }

    /// <summary>In-memory property bag standing in for the client object cache.</summary>
    private sealed class FakeItem : IItemPropertySource
    {
        public readonly Dictionary<uint, int> Ints = new();
        public readonly Dictionary<uint, double> Doubles = new();
        public readonly Dictionary<uint, string> Strings = new();

        public FakeItem(string name, AcObjectClass cls) { Name = name; ObjectClass = cls; }

        public string Name { get; }
        public AcObjectClass ObjectClass { get; }
        public bool TryGetInt(uint stype, out int value) => Ints.TryGetValue(stype, out value);
        public bool TryGetDouble(uint stype, out double value) => Doubles.TryGetValue(stype, out value);
        public string GetString(uint stype) => Strings.TryGetValue(stype, out string? v) ? v : string.Empty;
        public IReadOnlyList<int> SpellIds { get; } = new List<int>();
    }

    private static List<string> Pet(FakeItem item) => MagItemDescriber.Build(item, MagItemInfoOptions.Default).ToPetLines();

    // The item from the bug report: ACECustom shows Damage 1790 - 2069, Bonus to Attack Skill
    // +20%, Melee Defense +20.0%, Crushing Blow 3.66x, Fire Rending +176%, Grade S (100%),
    // Crit Dam 24, Wield requires 2,000 Item Augmentations.
    private static void T11FireStaff()
    {
        var staff = new FakeItem("Flaming Quarter Staff", AcObjectClass.MeleeWeapon);
        staff.Ints[44] = 2069;      // max damage
        staff.Ints[45] = 0x10;      // fire
        staff.Ints[353] = 7;        // staff mastery
        staff.Ints[105] = 9;        // workmanship
        staff.Ints[179] = 0x0200;   // Fire Rending
        staff.Ints[374] = 24;       // crit damage rating
        staff.Doubles[22] = 0.135;  // variance
        staff.Doubles[62] = 1.20;   // attack
        staff.Doubles[29] = 1.20;   // melee defense
        staff.Doubles[136] = 2.66;  // CriticalMultiplier (Crushing Blow 3.66x)
        staff.Ints[9061] = 11;      // WeaponAugScaleTier (sent since ACECustom 2026-09-29)
        staff.Ints[9060] = 1000;    // WeaponAugScaleQuality: the S roll
        staff.Strings[16] =
            "Property Details:\n" +
            "- Weapon Grade: S (100% of max damage)\n" +
            "- Properties: 2 of 5\n" +
            "- Crushing Blow: 3.66x Crit Dmg\n" +
            "- Fire Rending: +176% Dmg\n" +
            "- Effective Melee Defense: 39851\n" +
            "- Wield requires: 2,000 Item Augmentations\n";

        List<string> lines = Pet(staff);
        Check.Eq(lines.Count, 3, "line count");
        Check.Eq(lines[0], "Flaming Quarter Staff (Fire Staff), T11, Quality 1000, Props 2/5, Grade S (100%), Craft 9", "identity line");
        Check.Eq(lines[1], "FireRend +176%, Crushing Blow 3.66x, 1790-2069, 0.135v, +20%a, +20%md", "combat line");
        Check.Eq(lines[2], "[CD 24]  Wield 2,000 Item Augs", "ratings / requirements line");
    }

    private static void BitingStrikeAndPenalty()
    {
        var sword = new FakeItem("Sword", AcObjectClass.MeleeWeapon);
        sword.Ints[44] = 100;
        sword.Doubles[147] = 0.15;  // Biting Strike
        sword.Doubles[136] = 1.0;   // normal 2x crit, not Crushing Blow
        sword.Doubles[29] = 0.95;   // -5% melee defense
        string line = MagItemDescriber.Describe(sword, MagItemInfoOptions.Default);
        Check.True(line.Contains("Biting Strike 15%"), $"Biting Strike shown: {line}");
        Check.False(line.Contains("Crushing Blow"), $"1.0 multiplier is not Crushing Blow: {line}");
        Check.True(line.Contains("-5%md"), $"penalty keeps its sign: {line}");
        Check.True(line.Contains(", 100,") || line.EndsWith(", 100"), $"no variance prints max only: {line}");
    }

    private static void RetailImbuesBare()
    {
        var axe = new FakeItem("Axe", AcObjectClass.MeleeWeapon);
        axe.Ints[179] = 0x0001 | 0x0004; // CS + AR
        string line = MagItemDescriber.Describe(axe, MagItemInfoOptions.Default);
        Check.True(line.Contains(", CS AR"), $"bare Mag labels: {line}");

        axe.Strings[16] =
            "Property Details:\n" +
            "- Critical Strike: +12.5 % Crit Chance\n" + // culture space before %
            "- Armor Rending: 30.0% Ignored\n";
        line = MagItemDescriber.Describe(axe, MagItemInfoOptions.Default);
        Check.True(line.Contains("CS +12.5%, AR 30.0%"), $"strengths from detail text: {line}");
    }

    private static void Int64WieldHidden()
    {
        var mace = new FakeItem("Mace", AcObjectClass.MeleeWeapon);
        mace.Ints[158] = 13;        // WieldRequirement.Int64Stat
        mace.Ints[159] = 9008;      // LumAugItemCount
        mace.Ints[160] = 2500;
        string line = MagItemDescriber.Describe(mace, MagItemInfoOptions.Default);
        Check.False(line.Contains("Skill 9008"), $"no fake skill name: {line}");
    }

    private static void T11ArmorModifiers()
    {
        var helm = new FakeItem("T11 - Helm", AcObjectClass.Armor);
        helm.Strings[16] = "Wield requires: 500 Triune Weave\n";
        helm.Strings[14] =
            "Modifiers:\n" +
            "- Damage Resist +6 [2-12]\n" +
            "- Max Health +100 [40-200]\n";
        List<string> lines = Pet(helm);
        string last = lines[^1];
        Check.True(last.Contains("Mods: Damage Resist +6, Max Health +100"), $"modifiers listed: {last}");
        Check.True(last.Contains("Wield 500 Triune Weave"), $"triune gate listed: {last}");
    }

    // Armor in the #543 appraisal format: Gear Grade, Properties and Tainted lead / close the
    // block, markers follow the band. No tier property, so the tier is the gate estimate ("~").
    private static void T11GradedArmor()
    {
        var helm = new FakeItem("T11 - Helm", AcObjectClass.Armor);
        helm.Strings[16] = "Wield requires: 500 Triune Weave\n";
        helm.Strings[14] =
            "Modifiers:\n" +
            "- Gear Grade: B+ (average of 2 rolled lines)\n" +
            "- Properties: 2 of 5\n" +
            "- Damage Resist +60 [35-69] (Built-in)\n" +
            "- Max Health +50 [14-69] (+20 tinkered) (Locked)\n" +
            "- Tainted: bags no longer work on this item\n";
        string line = MagItemDescriber.Describe(helm, MagItemInfoOptions.Default);
        Check.True(line.Contains("~T16, Gear B+ (2 lines), Props 2/5, Tainted"), $"T11 state listed: {line}");
        Check.True(line.Contains("Mods: Damage Resist +60, Max Health +50 (+20 tink) (Locked)"), $"markers on mods: {line}");
        Check.False(line.Contains("Gear Grade:") || line.Contains("Properties:"), $"item lines are not mods: {line}");

        // The stamped tier wins over the estimate and drops the "~".
        helm.Ints[50109] = 17;
        line = MagItemDescriber.Describe(helm, MagItemInfoOptions.Default);
        Check.True(line.Contains(", T17, Gear B+"), $"ZcTier shown exact: {line}");

        Check.True(MagItemInfoCatalog.TryFindField("t11", out var field) && field == MagItemInfoField.T11, "t11 field found by name");
    }
}

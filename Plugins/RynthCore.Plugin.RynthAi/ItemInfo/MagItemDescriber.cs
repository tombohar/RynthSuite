// MagItemDescriber.cs — one-line "Mag-style" item info (Mag-Tools ItemInfo / UtilityBelt ub-IT ItemInfo).
//
//   Gold Ornate Long Sword (Slash Sword), Noble Relic Set, CS, Undead Slayer, Tinks 4,
//   Applied: Steel x2, 152-236, 0.35v, +18%a, +12%md, Legendary Blood Thirst, Wield Lvl 180,
//   Heavy Weapons 375, Diff 270, Craft 8, [D 3, CD 2]
//
// The same fields can also print as the pet-roster layout (MiniRemote/PetStats: identity line, stats
// line, ratings/requirements line), plus a spells line:
//
//   Gold Ornate Long Sword (Slash Sword), Noble Relic Set, Tinks 4, Applied: Steel x2, Craft 8
//     CS, Undead Slayer, 152-236, 0.35v, +18%a, +12%md
//     [D 3, CD 2]  Wield Lvl 180, Heavy Weapons 375, Diff 270
//     Spells: Legendary Blood Thirst
//
// Values follow what the ACECustom appraisal panel shows: the low damage end is rounded like the
// client, Crushing Blow is the dealt multiplier (1 + CriticalMultiplier), and imbue strengths,
// the T11 weapon / gear grades, property slots, procs, zone modifiers and item-aug wield gates
// come from the description text because the server keeps those properties off the wire; the
// T11 tier and weapon quality roll are real properties (ZcTier, WeaponAugScale*):
//
//   Flaming Quarter Staff (Fire Staff), T16, Quality 1000, Props 2/5, Grade S (100%), Craft 9
//     FireRend +176%, Crushing Blow 3.66x, 1790-2069, 0.135v, +20%a, +20%md
//     [CD 24]  Wield 2,000 Item Augs
//
// Field order and per-class gating follow ub-IT (Tools/ItemInfo.cs ItemDescriptions.ToString,
// Lib/ItemDescribeMagShorthand.cs, Lib/ItemInfoHelper/ItemIdentifyFieldSet.cs); labels and the
// spell filter follow Mag-Tools. All ids are raw ACE properties (RynthCore reads CBaseQualities by
// STypeInt/STypeFloat/STypeString id — not Decal's remapped LongValueKey/DoubleValueKey numbers).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RynthCore.Loot;
using RynthCore.Loot.T11;

namespace RynthCore.Plugin.RynthAi.ItemInfo;

/// <summary>Property reads for one item. Implemented over the live object cache (see
/// <see cref="CacheItemPropertySource"/>); a missing property returns false / empty.</summary>
internal interface IItemPropertySource
{
    string Name { get; }
    AcObjectClass ObjectClass { get; }
    bool TryGetInt(uint stype, out int value);
    bool TryGetDouble(uint stype, out double value);
    string GetString(uint stype);
    /// <summary>Spell ids on the item (inscribed + cantrips), unsorted.</summary>
    IReadOnlyList<int> SpellIds { get; }
}

/// <summary>Per-call output options, snapshotted from <see cref="MagItemInfoSettings"/>.</summary>
internal readonly record struct MagItemInfoOptions(
    MagItemInfoField Hidden, int HiddenRatings, bool ShowValueAndBurden, bool AllSpells, int MaxListedSpells)
{
    /// <summary>Everything shown, Mag spell filter — used when no settings are loaded yet.</summary>
    public static readonly MagItemInfoOptions Default = new(MagItemInfoField.None, 0, false, false, 26);

    public static MagItemInfoOptions From(MagItemInfoSettings? s) => s == null
        ? Default
        : new((MagItemInfoField)s.HiddenFields, s.HiddenRatings, s.ShowValueAndBurden,
              s.SpellMode == MagItemInfoSettings.SpellModeAll, s.MaxListedSpells);

    public bool Shows(MagItemInfoField f) => (Hidden & f) == 0;
}

/// <summary>Which pet-style line a field lands on (<see cref="MagItemDescription.ToPetLines"/>).</summary>
internal enum MagItemSection
{
    /// <summary>Line 1 after the name: set, AL, tinks, applied, craft, keyring.</summary>
    Identity,
    /// <summary>Line 2: imbues, slayer, damage, attack/defense/mana-conversion, protections.</summary>
    Combat,
    /// <summary>Line 3 lead: the "[D 3, CD 2]" cluster.</summary>
    Ratings,
    /// <summary>Line 3 tail: wield / activation / difficulty, value and burden.</summary>
    Requirements,
    /// <summary>Line 4: spell names.</summary>
    Spells,
}

/// <summary>
/// One described item: the title plus fields tagged with their section, kept in Mag print order
/// so <see cref="ToOneLine"/> reproduces the classic line exactly.
/// </summary>
internal sealed class MagItemDescription
{
    /// <summary>"[Material ]Name[ (DamageType Mastery)]".</summary>
    public string Title = string.Empty;
    public readonly List<(MagItemSection Section, string Text)> Segments = new();

    public void Add(MagItemSection section, string text)
    {
        if (!string.IsNullOrEmpty(text)) Segments.Add((section, text));
    }

    /// <summary>Classic Mag one-liner: title, then every field comma-separated.</summary>
    public string ToOneLine()
    {
        var sb = new StringBuilder(Title, 256);
        foreach (var (_, text) in Segments) sb.Append(", ").Append(text);
        return sb.ToString();
    }

    /// <summary>
    /// Pet-roster layout: line 1 is always present; empty stat / requirement / spell lines are
    /// dropped so plain armor stays short.
    /// </summary>
    public List<string> ToPetLines()
    {
        var lines = new List<string>(4);

        var identity = new StringBuilder(Title);
        foreach (string t in Section(MagItemSection.Identity)) identity.Append(", ").Append(t);
        lines.Add(identity.ToString());

        string combat = string.Join(", ", Section(MagItemSection.Combat));
        if (combat.Length > 0) lines.Add(combat);

        // Like the pet roster's third line: ratings first, then the rest two-space separated.
        var tail = new List<string>(2);
        string ratings = string.Join(", ", Section(MagItemSection.Ratings));
        if (ratings.Length > 0) tail.Add(ratings);
        string reqs = string.Join(", ", Section(MagItemSection.Requirements));
        if (reqs.Length > 0) tail.Add(reqs);
        if (tail.Count > 0) lines.Add(string.Join("  ", tail));

        var spells = Section(MagItemSection.Spells);
        if (spells.Count > 0)
        {
            // The verbose list is a single pre-labelled "Spells: a; b" segment.
            lines.Add(spells.Count == 1 && spells[0].StartsWith("Spells:", StringComparison.Ordinal)
                ? spells[0]
                : "Spells: " + string.Join(", ", spells));
        }
        return lines;
    }

    private List<string> Section(MagItemSection section)
    {
        var list = new List<string>();
        foreach (var (s, text) in Segments)
            if (s == section) list.Add(text);
        return list;
    }
}

/// <summary>Builds the Mag-style item info line.</summary>
internal static class MagItemDescriber
{
    // ── ACE PropertyInt ids ──────────────────────────────────────────────────
    private const uint IntBurden              = 5;    // EncumbranceVal
    private const uint IntValue               = 19;
    private const uint IntArmorLevel          = 28;
    private const uint IntResistMagic         = 36;   // >= 9999 = unenchantable
    private const uint IntMaxDamage           = 44;   // Damage
    private const uint IntDamageType          = 45;
    private const uint IntAttackType          = 47;
    private const uint IntStructure           = 92;   // keyring uses remaining
    private const uint IntWorkmanship         = 105;
    private const uint IntItemDifficulty      = 109;  // arcane lore requirement ("Diff")
    private const uint IntActivationLevel     = 115;  // ItemSkillLevelLimit
    private const uint IntMaterial            = 131;
    private const uint IntWieldReq            = 158;
    private const uint IntWieldSkill          = 159;
    private const uint IntWieldValue          = 160;
    private const uint IntSlayerCreature      = 166;
    private const uint IntNumItemsInMaterial  = 170;  // salvage: workmanship = 105 / 170
    private const uint IntTinks               = 171;
    private const uint IntActivationSkill     = 176;  // AppraisalItemSkill
    private const uint IntImbued              = 179;  // ImbuedEffect (+ 303..306 for 2..5)
    private const uint IntNumKeys             = 193;
    private const uint IntElementalBonus      = 204;
    private const uint IntEquipmentSet        = 265;
    private const uint IntWieldReq2           = 270;
    private const uint IntWieldSkill2         = 271;
    private const uint IntWieldValue2         = 272;
    private const uint IntWeaponType          = 353;  // mastery
    private const uint IntUseRequiresSkill    = 366;
    private const uint IntUseRequiresSkillLvl = 367;
    private const uint IntUseRequiresSpec     = 368;
    private const uint IntUseRequiresLevel    = 369;
    private const uint IntSplitArrows         = 9031; // ACECustom SplitArrowCount

    // ── ACE PropertyFloat ids ────────────────────────────────────────────────
    private const uint FloatArmorVsSlash      = 13;   // 13..19 = Slash/Pierce/Bludgeon/Cold/Fire/Acid/Electric
    private const uint FloatVariance          = 22;
    private const uint FloatMaxVelocity       = 26;
    private const uint FloatMeleeDefense      = 29;   // WeaponDefense
    private const uint FloatAttack            = 62;   // WeaponOffense
    private const uint FloatDamageMod         = 63;
    private const uint FloatCritMultiplier    = 136;
    private const uint FloatManaConversion    = 144;
    private const uint FloatCritFrequency     = 147;
    private const uint FloatMissileDefense    = 149;
    private const uint FloatMagicDefense      = 150;
    private const uint FloatVsMonsters        = 152;  // ElementalDamageMod
    private const uint FloatIgnoreArmor       = 155;  // armor cleaving

    // ── ACE PropertyString ids ───────────────────────────────────────────────
    private const uint StringUse              = 14;   // ACECustom: T11 armor/jewelry "Modifiers:" block
    private const uint StringLongDesc         = 16;   // ACECustom: "Property Details:" lines (imbue strengths, grade, gates)
    private const uint StringTinkerLog        = 9007; // ACECustom: CSV of applied material ids

    /// <summary>ACECustom's WieldRequirement.Int64Stat: the client gets it as text, never as properties.</summary>
    private const int WieldReqInt64Stat = 13;

    /// <summary>Gear ratings are small (1..~50). Anything above is a wrong key leaking through.</summary>
    private const int MaxPlausibleRating = 200;

    /// <summary>Class-specific fields (ub-IT ItemIdentifyFieldSet). Generic fields print for every class.</summary>
    [Flags]
    private enum Field
    {
        None           = 0,
        DamageType     = 1 << 0,
        CritStats      = 1 << 1,
        Splits         = 1 << 2,
        Cleaves        = 1 << 3,
        Slayer         = 1 << 4,
        Range          = 1 << 5,
        Damage         = 1 << 6,
        AttackBonus    = 1 << 7,
        MeleeDefense   = 1 << 8,
        MissileDefense = 1 << 9,
        MagicDefense   = 1 << 10,
        ManaConversion = 1 << 11,
        ArmorCleave    = 1 << 12,
        WeaponDefenses = MeleeDefense | MissileDefense | MagicDefense,
    }

    private static Field FieldsFor(AcObjectClass cls) => cls switch
    {
        AcObjectClass.MissileWeapon => Field.DamageType | Field.CritStats | Field.Splits | Field.Cleaves
                                     | Field.Slayer | Field.Range | Field.Damage | Field.WeaponDefenses
                                     | Field.ArmorCleave,
        AcObjectClass.MeleeWeapon   => Field.DamageType | Field.CritStats | Field.Cleaves | Field.Slayer
                                     | Field.Damage | Field.AttackBonus | Field.WeaponDefenses
                                     | Field.ArmorCleave,
        AcObjectClass.WandStaffOrb  => Field.DamageType | Field.CritStats | Field.Slayer | Field.Damage
                                     | Field.WeaponDefenses | Field.ManaConversion,
        AcObjectClass.Jewelry       => Field.None,
        // Armor / clothing / misc: keep Mag's generic bonus lines (shields roll %md etc.).
        _                           => Field.AttackBonus | Field.WeaponDefenses | Field.ManaConversion,
    };

    /// <summary>Full Mag-style line for one item (name first, comma-separated fields).</summary>
    public static string Describe(IItemPropertySource item, MagItemInfoOptions options)
        => Build(item, options).ToOneLine();

    /// <summary>Reads every enabled field once, tagged by section (one-line or pet-style output).</summary>
    public static MagItemDescription Build(IItemPropertySource item, MagItemInfoOptions options)
    {
        var d = new MagItemDescription();
        var sb = new StringBuilder(128); // title only
        AcObjectClass cls = item.ObjectClass;
        Field fields = FieldsFor(cls);
        bool Has(Field f) => (fields & f) == f;
        bool Show(MagItemInfoField f) => options.Shows(f);

        int Int(uint id) => item.TryGetInt(id, out int v) ? v : 0;
        double Dbl(uint id, double fallback) => item.TryGetDouble(id, out double v) ? v : fallback;

        // ── Name: "[Material ]Name (DamageType Mastery)" ────────────────────────
        int material = Int(IntMaterial);
        if (material > 0 && Show(MagItemInfoField.Material))
        {
            string matName = MagItemInfoTables.Materials.TryGetValue(material, out string? m) ? m : $"material {material}";
            // Loot names usually omit the material; don't double it when the name already starts with it.
            if (!item.Name.StartsWith(matName, StringComparison.OrdinalIgnoreCase))
                sb.Append(matName).Append(' ');
        }
        sb.Append(item.Name);

        if (Has(Field.DamageType) && Show(MagItemInfoField.TypeMastery))
        {
            string dmgType = MagItemInfoTables.DamageTypeName(Int(IntDamageType));
            int mastery = Int(IntWeaponType);
            string masteryName = mastery > 0
                ? (MagItemInfoTables.Masteries.TryGetValue(mastery, out string? mn) ? mn : $"Unknown mastery {mastery}")
                : string.Empty;
            string inner = $"{dmgType} {masteryName}".Trim();
            if (inner.Length > 0) sb.Append(" (").Append(inner).Append(')');
        }
        d.Title = sb.ToString();

        const MagItemSection Id = MagItemSection.Identity, Cbt = MagItemSection.Combat, Req = MagItemSection.Requirements;
        string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        // Text-only appraisal data (empty until the item has been IDed).
        string longDesc = item.GetString(StringLongDesc);
        var t11Server = new T11ServerProps(
            Int(T11Catalog.PropZcTier),
            Int(T11Catalog.PropWeaponAugScaleTier),
            item.TryGetInt(T11Catalog.PropWeaponAugScaleQuality, out int quality) ? quality : -1);
        T11ItemInfo t11 = T11ItemInfo.Parse(item.Name, longDesc, item.GetString(StringUse), t11Server);

        // ── Set ─────────────────────────────────────────────────────────────────
        int set = Int(IntEquipmentSet);
        if (set > 0 && Show(MagItemInfoField.Set))
            d.Add(Id, MagItemInfoTables.EquipmentSets.TryGetValue(set, out string? setName) ? setName : $"Unknown set {set}");

        // ── T11 state: "T16, Gear B+ (4 lines), Props 3/5, Tainted" ───────────
        if (t11.IsT11 && Show(MagItemInfoField.T11))
        {
            // "~" marks a tier estimated from the wield gates (older server, or not yet IDed).
            d.Add(Id, t11.TierIsExact ? "T" + I(t11.Tier) : "~T" + I(t11.Tier));
            if (t11.WeaponQuality >= 0) d.Add(Id, "Quality " + I(t11.WeaponQuality));
            if (t11.GearGradeRank > 0)
                d.Add(Id, t11.GearGradeLines > 0 ? $"Gear {t11.GearGrade} ({I(t11.GearGradeLines)} lines)" : $"Gear {t11.GearGrade}");
            if (t11.PropertySlots >= 0)
                d.Add(Id, t11.PropertySlotCap > 0 ? $"Props {I(t11.PropertySlots)}/{I(t11.PropertySlotCap)}" : $"Props {I(t11.PropertySlots)}");
            if (t11.Tainted) d.Add(Id, "Tainted");
        }

        // ── Armor level ─────────────────────────────────────────────────────────
        int al = Int(IntArmorLevel);
        if (al > 0 && Show(MagItemInfoField.ArmorLevel)) d.Add(Id, "AL " + I(al));

        // ── Imbues (ImbuedEffect 179 plus ImbuedEffect2..5 = 303..306) ─────────
        if (Show(MagItemInfoField.Imbues))
            d.Add(Cbt, ImbueLabels(Int(IntImbued) | Int(303) | Int(304) | Int(305) | Int(306), ImbueStrengths(longDesc)));

        if (Has(Field.ArmorCleave) && Show(MagItemInfoField.ArmorCleave) && Dbl(FloatIgnoreArmor, 0) != 0)
            d.Add(Cbt, "AC");

        if (Has(Field.CritStats) && Show(MagItemInfoField.CritStats))
        {
            // A crit deals 1 + CriticalMultiplier; a stored 1.0 is a normal 2x crit, so ACE only
            // calls it Crushing Blow above 1.0.
            double critMult = Dbl(FloatCritMultiplier, 0);
            if (critMult > 1) d.Add(Cbt, $"Crushing Blow {Fmt(critMult + 1)}x");
            // Biting Strike replaces the base crit chance with this fraction.
            double critFreq = Dbl(FloatCritFrequency, 0);
            if (critFreq > 0) d.Add(Cbt, $"Biting Strike {Math.Round(critFreq * 100).ToString(CultureInfo.InvariantCulture)}%");
        }

        // T11 Cast on Strike procs: "Proc Force Arc 13%".
        if (Show(MagItemInfoField.Imbues))
            foreach (var (procName, chance) in t11.Procs)
                d.Add(Cbt, $"Proc {procName} {Fmt(chance)}%");

        if (Has(Field.Splits) && Show(MagItemInfoField.Splits))
        {
            int splits = Int(IntSplitArrows);
            if (splits > 0) d.Add(Cbt, $"Splits ({I(splits)})");
        }

        if (Has(Field.Range) && Show(MagItemInfoField.Range))
        {
            int yards = MissileRangeYards(Dbl(FloatMaxVelocity, 0));
            if (yards > 0) d.Add(Cbt, $"Range {I(yards)}yd");
        }

        if (Has(Field.Cleaves) && Show(MagItemInfoField.CleaveMultiStrike))
        {
            int attackType = Int(IntAttackType);
            int mastery = Int(IntWeaponType);
            bool cleaving = (attackType == 4 && mastery == 11) || (attackType == 1 && mastery != 11 && mastery != 0);
            if (cleaving) d.Add(Cbt, "Cleaving");
            else if (attackType is 160 or 166) d.Add(Cbt, "Multi-Strike");
            else if (attackType == 486) d.Add(Cbt, "Multi-Strike (3)");
        }

        // ── Slayer ──────────────────────────────────────────────────────────────
        if (Has(Field.Slayer) && Show(MagItemInfoField.Slayer))
        {
            string? slayer = MagItemInfoTables.CreatureTypeName(Int(IntSlayerCreature));
            if (slayer != null) d.Add(Cbt, slayer + " Slayer");
        }

        // ── Tinks + applied materials (TinkerLog) ──────────────────────────────
        int tinks = Int(IntTinks);
        if (tinks > 0 && Show(MagItemInfoField.Tinks)) d.Add(Id, "Tinks " + I(tinks));
        if (Show(MagItemInfoField.Applied))
        {
            string applied = FormatTinkerLog(item.GetString(StringTinkerLog));
            if (applied.Length > 0) d.Add(Id, "Applied: " + applied);
        }

        // ── Damage ──────────────────────────────────────────────────────────────
        // T11 weapon grade sits with the identity fields: "Grade S (100%)".
        if (Has(Field.Damage) && Show(MagItemInfoField.Damage) && t11.GradeRank > 0)
            d.Add(Id, t11.DamagePercent >= 0 ? $"Grade {t11.Grade} ({I(t11.DamagePercent)}%)" : $"Grade {t11.Grade}");

        if (Has(Field.Damage) && Show(MagItemInfoField.Damage))
            AddDamage(d, cls, Int(IntMaxDamage), Dbl(FloatVariance, 0), Int(IntElementalBonus),
                Dbl(FloatDamageMod, 1), Dbl(FloatVsMonsters, 1));

        // ── Attack / defense / mana conversion percents ───────────────────────
        bool defenses = Show(MagItemInfoField.Defenses);
        if (Has(Field.AttackBonus) && Show(MagItemInfoField.Attack)) AddPercent(d, Dbl(FloatAttack, 1), "%a", 0);
        if (Has(Field.MeleeDefense) && defenses)   AddPercent(d, Dbl(FloatMeleeDefense, 1), "%md", 0);
        if (Has(Field.MagicDefense) && defenses)   AddPercent(d, Dbl(FloatMagicDefense, 1), "%mgc.d", 1);
        if (Has(Field.MissileDefense) && defenses) AddPercent(d, Dbl(FloatMissileDefense, 1), "%msl.d", 1);
        if (Has(Field.ManaConversion) && Show(MagItemInfoField.ManaConversion))
        {
            double mc = Dbl(FloatManaConversion, 0);
            if (mc != 0) d.Add(Cbt, Math.Round(mc * 100).ToString(CultureInfo.InvariantCulture) + "%mc");
        }

        // ── Spells ──────────────────────────────────────────────────────────────
        bool unenchantable = Int(IntResistMagic) >= 9999;
        if (Show(MagItemInfoField.Spells))
            AddSpells(d, item.SpellIds, unenchantable, options.AllSpells, options.MaxListedSpells);

        // ── Requirements ────────────────────────────────────────────────────────
        if (Show(MagItemInfoField.Wield))
        {
            AddWieldRequirement(d, Int(IntWieldReq), Int(IntWieldSkill), Int(IntWieldValue));
            AddWieldRequirement(d, Int(IntWieldReq2), Int(IntWieldSkill2), Int(IntWieldValue2));
            // T11 gates (item augs, Triune Weave, charms) only exist as "Wield requires:" text.
            foreach (T11WieldGate gate in t11.WieldGates)
                d.Add(Req, $"Wield {gate.Amount.ToString("N0", CultureInfo.InvariantCulture)} {GateName(gate.Counter)}");
        }

        if (Show(MagItemInfoField.Activation))
        {
            int useLevel = Int(IntUseRequiresLevel);
            if (useLevel > 0) d.Add(Req, "Lvl " + I(useLevel));

            // "Melee Defense 300 to Activate" — hidden when the wield requirement already covers it.
            int actSkill = Int(IntActivationSkill), actLevel = Int(IntActivationLevel);
            if (actLevel > 0 && (Int(IntWieldSkill) != actSkill || Int(IntWieldValue) < actLevel))
                d.Add(Req, $"{SkillName(actSkill)} {I(actLevel)} to Activate");

            // Summoning essences / gems.
            int useSkill = Int(IntUseRequiresSkill), useSkillLvl = Int(IntUseRequiresSkillLvl), useSpec = Int(IntUseRequiresSpec);
            if (useSkill > 0 && useSkillLvl > 0) d.Add(Req, $"{SkillName(useSkill)} {I(useSkillLvl)}");
            if (useSpec > 0 && useSkillLvl > 0) d.Add(Req, $"Spec {SkillName(useSpec)} {I(useSkillLvl)}");
        }

        int diff = Int(IntItemDifficulty);
        if (diff > 0 && Show(MagItemInfoField.Difficulty)) d.Add(Req, "Diff " + I(diff));

        // ── Workmanship ─────────────────────────────────────────────────────────
        int work = Show(MagItemInfoField.Craft) ? Int(IntWorkmanship) : 0;
        if (cls == AcObjectClass.Salvage)
        {
            int items = Int(IntNumItemsInMaterial);
            if (work > 0)
            {
                double avg = items > 0 ? (double)work / items : work;
                d.Add(Id, "Work " + avg.ToString("N2", CultureInfo.InvariantCulture));
            }
        }
        else if (work > 0 && tinks != 10) // Mag: hide craft once fully tinked
        {
            d.Add(Id, "Craft " + I(work));
        }

        // ── Unenchantable armor: base protections [S/P/B/C/F/A/L] ─────────────
        if (cls == AcObjectClass.Armor && unenchantable && Show(MagItemInfoField.Protections))
        {
            var prot = new StringBuilder("[");
            for (uint i = 0; i < 7; i++)
            {
                if (i > 0) prot.Append('/');
                prot.Append(Dbl(FloatArmorVsSlash + i, 0).ToString("N1", CultureInfo.InvariantCulture));
            }
            d.Add(Cbt, prot.Append(']').ToString());
        }

        if (options.ShowValueAndBurden)
        {
            int value = Int(IntValue);
            if (value > 0) d.Add(Req, "Value " + value.ToString("N0", CultureInfo.InvariantCulture));
            int burden = Int(IntBurden);
            if (burden > 0) d.Add(Req, "BU " + I(burden));
        }

        // ── Ratings ─────────────────────────────────────────────────────────────
        if (Show(MagItemInfoField.Ratings))
        {
            d.Add(MagItemSection.Ratings, RatingsCluster(item, options.HiddenRatings));
            // T11 zone modifiers: the server drops a modifier's Gear* rating from the ratings it
            // sends, so the modifier lines are the only place those values appear.
            if (t11.Modifiers.Count > 0)
            {
                var mods = new List<string>(t11.Modifiers.Count);
                foreach (T11Modifier m in t11.Modifiers)
                {
                    string mod = m.Value != 0 ? $"{m.Name} {(m.Value > 0 ? "+" : "")}{I(m.Value)}" : m.Name;
                    if (m.Tinkered != 0) mod += $" ({(m.Tinkered > 0 ? "+" : "")}{I(m.Tinkered)} tink)";
                    if (m.Locked) mod += " (Locked)";
                    mods.Add(mod);
                }
                d.Add(MagItemSection.Ratings, "Mods: " + string.Join(", ", mods));
            }
        }

        if (cls == AcObjectClass.Misc && Show(MagItemInfoField.Keyring)
            && item.Name.Contains("Keyring", StringComparison.OrdinalIgnoreCase))
        {
            d.Add(Id, "Keys: " + I(Int(IntNumKeys)));
            d.Add(Id, "Uses: " + I(Int(IntStructure)));
        }

        return d;
    }

    /// <summary>Mag imbue labels ("CS AR"), plus the ACE flags Mag predates (missile imbue, nether
    /// rend); empty when no known bit is set.</summary>
    private static string ImbueLabels(int mask, IReadOnlyDictionary<int, string> strengths)
    {
        if (mask == 0) return string.Empty;
        var sb = new StringBuilder();
        // Bare labels stay Mag-style "CS AR"; once a strength is shown, commas keep values readable.
        string sep = strengths.Count > 0 ? ", " : " ";
        void Flag(int bit, string label)
        {
            if ((mask & bit) == 0) return;
            if (sb.Length > 0) sb.Append(sep);
            sb.Append(label);
            if (strengths.TryGetValue(bit, out string? value)) sb.Append(' ').Append(value);
        }
        Flag(0x0001, "CS");
        Flag(0x0002, "CB");
        Flag(0x0004, "AR");
        Flag(0x0008, "SlashRend");
        Flag(0x0010, "PierceRend");
        Flag(0x0020, "BludgeRend");
        Flag(0x0040, "AcidRend");
        Flag(0x0080, "FrostRend");
        Flag(0x0100, "LightRend");
        Flag(0x0200, "FireRend");
        Flag(0x4000, "NetherRend");
        Flag(0x0400, "MeleeImbue");
        Flag(0x0800, "MissileImbue");
        Flag(0x1000, "MagicImbue");
        Flag(0x2000, "Hematited");
        Flag(0x20000000, "MagicAbsorb");
        return sb.ToString(); // only unknown bits → empty → field skipped
    }

    // ACECustom "Property Details:" imbue lines, e.g. "- Fire Rending: +176% Dmg",
    // "- Armor Rending: 12.5% Ignored", "- Crippling Blow: 2.5x Crit Dmg". The value may carry a
    // culture space before '%' and group separators.
    private static readonly Regex ImbueDetailLine = new(
        @"^\s*-\s*(?<name>[A-Za-z' ]+?)\s*:\s*(?<value>[+-]?[\d.,]+\s?[%x])",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Imbue bit → strength text ("+176%", "12.5%", "2.5x") from the description. Rendings are
    /// matched by damage keyword because the server builds their names from DamageType.DisplayName.
    /// </summary>
    private static IReadOnlyDictionary<int, string> ImbueStrengths(string longDesc)
    {
        var result = new Dictionary<int, string>();
        if (string.IsNullOrEmpty(longDesc)) return result;
        foreach (Match m in ImbueDetailLine.Matches(longDesc))
        {
            int bit = ImbueBitForDetail(m.Groups["name"].Value);
            if (bit != 0 && !result.ContainsKey(bit))
                result[bit] = m.Groups["value"].Value.Replace(" ", "");
        }
        return result;
    }

    private static int ImbueBitForDetail(string name)
    {
        bool Has(string s) => name.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Critical Strike")) return 0x0001;
        if (Has("Crippling Blow")) return 0x0002;
        if (Has("Armor Rending")) return 0x0004;
        if (!Has("Rending")) return 0;
        if (Has("Slash")) return 0x0008;
        if (Has("Pierc")) return 0x0010;
        if (Has("Bludg")) return 0x0020;
        if (Has("Acid")) return 0x0040;
        if (Has("Cold") || Has("Frost")) return 0x0080;
        if (Has("Electric") || Has("Lightning")) return 0x0100;
        if (Has("Fire")) return 0x0200;
        if (Has("Nether")) return 0x4000;
        return 0;
    }

    /// <summary>Short wield-gate counter names: "Item Augs", "Triune Weave", charm names.</summary>
    private static string GateName(T11Counter counter) =>
        counter == T11Counter.ItemAugmentations ? "Item Augs" : T11Catalog.CounterName(counter);

    /// <summary>Melee "min-max", variance, "+elem", "+mod%", wand "+N% vs. Monsters" (combat line).</summary>
    private static void AddDamage(MagItemDescription d, AcObjectClass cls, int maxDmg, double variance,
        int elemBonus, double dmgMod, double vsMonsters)
    {
        const MagItemSection Cbt = MagItemSection.Combat;
        if (cls != AcObjectClass.MissileWeapon && maxDmg > 0)
        {
            string max = maxDmg.ToString(CultureInfo.InvariantCulture);
            // The appraisal panel rounds the low end to a whole number (2069 * 0.865 → 1790).
            long min = (long)Math.Round(maxDmg * (1 - variance), MidpointRounding.AwayFromZero);
            d.Add(Cbt, variance > 0 ? min.ToString(CultureInfo.InvariantCulture) + "-" + max : max);
        }
        // Three places: loot variances are often x.xx5 and two places would misreport them.
        if (variance > 0)
            d.Add(Cbt, Math.Round(variance, 3).ToString("0.###", CultureInfo.InvariantCulture) + "v");
        if (elemBonus != 0)
            d.Add(Cbt, "+" + elemBonus.ToString(CultureInfo.InvariantCulture));
        // A missing/zero multiplier means "no bonus" (0 would print as -100%).
        if (dmgMod > 0 && dmgMod != 1)
            d.Add(Cbt, "+" + Math.Round((dmgMod - 1) * 100).ToString(CultureInfo.InvariantCulture) + "%");
        if (vsMonsters > 0 && vsMonsters != 1)
            d.Add(Cbt, "+" + Math.Round((vsMonsters - 1) * 100).ToString(CultureInfo.InvariantCulture) + "% vs. Monsters");
    }

    /// <summary>Mag "+18%a" from a 1.0-based multiplier; skips missing (≤0) and neutral 1.0.</summary>
    private static void AddPercent(MagItemDescription d, double multiplier, string suffix, int decimals)
    {
        if (multiplier <= 0 || multiplier == 1) return;
        double pct = Math.Round((multiplier - 1) * 100, decimals);
        if (pct == 0) return; // rounds to nothing at this precision
        // Signed like the appraisal panel ("Bonus to Attack Skill +20%"); penalties keep their '-'.
        d.Add(MagItemSection.Combat, (pct > 0 ? "+" : "") + pct.ToString(CultureInfo.InvariantCulture) + suffix);
    }

    /// <summary>"Wield Lvl 180" for a level requirement, else "Heavy Weapons 375".</summary>
    private static void AddWieldRequirement(MagItemDescription d, int reqType, int skillOrAttr, int value)
    {
        // Int64 gates name a counter, not a skill; the T11 text supplies those.
        if (value <= 0 || reqType == WieldReqInt64Stat) return;
        string v = value.ToString(CultureInfo.InvariantCulture);
        d.Add(MagItemSection.Requirements, reqType == 7 // WieldRequirement.Level
            ? "Wield Lvl " + v
            : SkillName(skillOrAttr) + " " + v);
    }

    private static string SkillName(int skill) =>
        MagItemInfoTables.Skills.TryGetValue(skill, out string? name) ? name : $"Skill {skill}";

    /// <summary>
    /// Mag-Tools spell filter (ub-IT applies it to every item): keep cantrip Impen, Augmented
    /// (trinket) spells, banes/Impen only on unenchantable gear, level 7+ weapon auras, and
    /// named high-tier spells (cantrips); drop I–VI, level-7 lore buffs and Incantations.
    /// Verbose lists every spell (capped) for troubleshooting.
    /// </summary>
    private static void AddSpells(MagItemDescription d, IReadOnlyList<int> spellIds, bool unenchantable,
        bool verbose, int maxVerbose)
    {
        if (spellIds.Count == 0) return;
        var ids = new List<int>(spellIds);
        ids.Sort();
        ids.Reverse(); // Mag lists highest spell id first

        var seen = new HashSet<int>();
        var verboseList = verbose ? new StringBuilder() : null;
        int shown = 0, skippedVerbose = 0;
        foreach (int id in ids)
        {
            if (!seen.Add(id)) continue;
            bool known = SpellDatabase.HasSpell(id);
            if (verboseList != null)
            {
                // Verbose is the troubleshooting view: list ids SpellData.txt doesn't know too.
                if (shown >= maxVerbose) { skippedVerbose++; continue; }
                verboseList.Append(shown == 0 ? "Spells: " : "; ")
                           .Append(known ? SpellDatabase.GetSpellName(id) : $"Spell {id}");
                shown++;
                continue;
            }
            if (!known) continue;
            string name = SpellDatabase.GetSpellName(id);
            SpellDatabase.TryGetSpellMeta(id, out int family, out int difficulty);
            if (ShouldShowSpell(name, family, difficulty, unenchantable))
                d.Add(MagItemSection.Spells, name);
        }
        if (verboseList == null || verboseList.Length == 0) return;
        if (skippedVerbose > 0) verboseList.Append("; (+").Append(skippedVerbose).Append(" more spells)");
        d.Add(MagItemSection.Spells, verboseList.ToString());
    }

    private static bool ShouldShowSpell(string name, int family, int difficulty, bool unenchantable)
    {
        if (name.Contains("Minor Impenetrability") || name.Contains("Major Impenetrability")
            || name.Contains("Epic Impenetrability") || name.Contains("Legendary Impenetrability"))
            return true;
        if (name.Contains("Augmented"))
            return true;

        // Banes / Impen / Brogard's: only meaningful on unenchantable gear (loot armor
        // already reflects them in AL and protections).
        bool baneLike = name.Contains(" Bane") || name.Contains("Impen") || name.StartsWith("Brogard", StringComparison.Ordinal);
        if (baneLike) return unenchantable;

        // Weapon auras (Elysa's Sight .. Swift Killer, Hermetic Link, Vision of the Hunter):
        // level 7 (300) and 8+ (400+) only.
        if ((family >= 152 && family <= 158) || family == 195 || family == 325)
            return difficulty == 300 || difficulty >= 400;

        if (name.EndsWith(" I") || name.EndsWith(" II") || name.EndsWith(" III")
            || name.EndsWith(" IV") || name.EndsWith(" V") || name.EndsWith(" VI"))
            return false;
        if (difficulty == 300) return false;       // level-7 lore buffs (Might of the Lugians …)
        if (name.Contains("Incantation")) return false;
        return true;                                // cantrips: Legendary Strength, Epic Defender …
    }

    /// <summary>Rating cluster "[D 11, C 8, CD 4]" from ACE Gear* ratings 370–389
    /// (<see cref="MagItemInfoCatalog.Ratings"/>); bit i of <paramref name="hiddenMask"/> skips rating i.</summary>
    private static string RatingsCluster(IItemPropertySource item, int hiddenMask)
    {
        var sb = new StringBuilder();
        var ratings = MagItemInfoCatalog.Ratings;
        for (int i = 0; i < ratings.Length; i++)
        {
            if ((hiddenMask & (1 << i)) != 0) continue;
            if (!item.TryGetInt(ratings[i].StatId, out int v) || v <= 0 || v > MaxPlausibleRating) continue;
            sb.Append(sb.Length == 0 ? "[" : ", ").Append(ratings[i].Tag).Append(' ')
              .Append(v.ToString(CultureInfo.InvariantCulture));
        }
        return sb.Length > 0 ? sb.Append(']').ToString() : string.Empty;
    }

    /// <summary>ACECustom TinkerLog CSV ("64,64,46") → "Steel x2, White Quartz".</summary>
    private static string FormatTinkerLog(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return string.Empty;
        var order = new List<int>();
        var counts = new Dictionary<int, int>();
        foreach (string raw in csv.Split(','))
        {
            if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id <= 0)
                continue;
            if (counts.TryGetValue(id, out int c)) counts[id] = c + 1;
            else { counts[id] = 1; order.Add(id); }
        }
        var sb = new StringBuilder();
        foreach (int id in order)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(MagItemInfoTables.Materials.TryGetValue(id, out string? name) ? name : $"material {id}");
            if (counts[id] > 1) sb.Append(" x").Append(counts[id]);
        }
        return sb.ToString();
    }

    /// <summary>Max ballistic range (45° arc, v²/g) in yards; 0 when velocity is missing.</summary>
    private static int MissileRangeYards(double velocity)
    {
        if (double.IsNaN(velocity) || velocity <= 0) return 0;
        double meters = velocity * velocity / 9.8;
        return (int)Math.Round(meters * 1.0936133);
    }

    private static string Fmt(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
}

/// <summary><see cref="IItemPropertySource"/> over the live client object (RynthCore host reads).</summary>
internal sealed class CacheItemPropertySource : IItemPropertySource
{
    private readonly RynthCore.PluginSdk.RynthCoreHost _host;
    private readonly uint _id;
    private IReadOnlyList<int>? _spells;

    public CacheItemPropertySource(RynthCore.PluginSdk.RynthCoreHost host, WorldObject wo)
    {
        _host = host;
        _id = unchecked((uint)wo.Id);
        Name = wo.Name;
        ObjectClass = wo.ObjectClass;
    }

    public string Name { get; }
    public AcObjectClass ObjectClass { get; }

    public bool TryGetInt(uint stype, out int value)
    {
        value = 0;
        return _host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(_id, stype, out value);
    }

    public bool TryGetDouble(uint stype, out double value)
    {
        value = 0;
        return _host.HasGetObjectDoubleProperty && _host.TryGetObjectDoubleProperty(_id, stype, out value);
    }

    public string GetString(uint stype) =>
        _host.HasGetObjectStringProperty && _host.TryGetObjectStringProperty(_id, stype, out string? v) && v != null
            ? v
            : string.Empty;

    public IReadOnlyList<int> SpellIds => _spells ??= ReadSpells();

    private IReadOnlyList<int> ReadSpells()
    {
        if (!_host.HasGetObjectSpellIds) return Array.Empty<int>();
        uint[] buf = new uint[128];
        int n = _host.GetObjectSpellIds(_id, buf, buf.Length);
        if (n <= 0) return Array.Empty<int>();
        int take = Math.Min(n, buf.Length);
        var list = new List<int>(take);
        for (int i = 0; i < take; i++) list.Add(unchecked((int)buf[i]));
        return list;
    }
}

using System;
using System.Collections.Generic;

namespace RynthCore.Loot.T11;

/// <summary>
/// The player counter a T11 wield gate is checked against. On the server these are
/// PropertyInt64 values (WieldRequirement.Int64Stat); the client never receives the
/// gate as properties, only as a "Wield requires: N &lt;counter&gt;" description line.
/// </summary>
public enum T11Counter
{
    /// <summary>LumAugItemCount (PropertyInt64 9008). The server checks the raw count, without Triune Weave.</summary>
    ItemAugmentations = 9008,
    /// <summary>TriuneWeaveCount (PropertyInt64 50000), the T16+ armor gate.</summary>
    TriuneWeave = 50000,
    /// <summary>BattlemagesWrathCharmCount (PropertyInt64 50001).</summary>
    BattlemagesWrath = 50001,
    /// <summary>NetherVeilCharmCount (PropertyInt64 50002).</summary>
    NetherVeil = 50002,
    /// <summary>CrashingSteelCharmCount (PropertyInt64 50003).</summary>
    CrashingSteel = 50003,
    /// <summary>TrueShotCharmCount (PropertyInt64 50004).</summary>
    TrueShot = 50004,
}

/// <summary>One Zone Control modifier the server can stamp on T11 gear.</summary>
/// <param name="Key">The server's catalog key (ZoneModifiers.Catalog).</param>
/// <param name="Name">The name printed at the start of the appraisal line.</param>
/// <param name="RatingKey">The retail Gear* rating property the modifier sets, or 0.</param>
/// <param name="SlotSpecial">True for the one-per-slot specials, whose line has no roll band.</param>
public sealed record T11ModifierDef(int Key, string Name, int RatingKey, bool SlotSpecial);

/// <summary>
/// Static T11 tables mirrored from ACECustom: sub-grades, the Zone Control
/// modifier catalog, the wield-gate counter names and the tier-from-gate estimate.
/// Pure data, shared by the RynthAi evaluator, the loot editor and the tests.
/// </summary>
public static class T11Catalog
{
    /// <summary>The name prefix the server gives every tier 11+ drop (ApplyT11NamePrefix).</summary>
    public const string NamePrefix = "T11 - ";

    /// <summary>Retail rating keys counted by the TotalRatings condition (VTank's set).</summary>
    public static readonly int[] TotalRatingKeys = { 370, 371, 372, 373, 374, 375, 376, 379 };

    /// <summary>
    /// Sub-grades best to worst with their rank (S = 16 ... F- = 1, 0 = no grade). Order and
    /// labels follow WeaponScalingManager.SubGradeBands; Weapon Grade and Gear Grade share them.
    /// </summary>
    public static readonly (int Id, string Name)[] Grades =
    {
        (16, "S"), (15, "A+"), (14, "A"), (13, "A-"), (12, "B+"), (11, "B"), (10, "B-"),
        (9, "C+"), (8, "C"), (7, "C-"), (6, "D+"), (5, "D"), (4, "D-"), (3, "F+"), (2, "F"), (1, "F-"),
    };

    /// <summary>
    /// ZoneModifiers.Catalog (ACECustom). Rating keys: 370 Damage, 371 Damage Resist, 372 Crit,
    /// 373 Crit Resist, 374 Crit Damage, 375 Crit Damage Resist, 376 Healing Boost,
    /// 377 Nether Resist, 379 Max Health.
    /// </summary>
    public static readonly T11ModifierDef[] Modifiers =
    {
        new(19, "Max Health", 379, false),
        new(25, "Armor Level", 0, false),
        new(28, "Damage Rating", 370, false),
        new(29, "Crit Damage Rating", 374, false),
        new(31, "Healing Boost", 376, false),
        new(32, "Spell Duration", 0, false),
        new(33, "Crit Chance", 372, false),
        new(41, "Fortify Vitals", 0, true),
        new(42, "Battle Mending", 0, true),
        new(43, "All Attributes", 0, false),
        new(44, "Pct HP Damage", 0, true),
        new(45, "Cheat Death", 0, true),
        new(46, "Regeneration", 0, true),
        new(47, "Max Health Pct", 0, false),
        new(48, "Life on Hit", 0, false),
        new(49, "Reinforced", 0, false),
        new(50, "Damage Resist", 371, false),
        new(51, "Crit Damage Resist", 375, false),
        new(52, "Crit Resist", 373, false),
        new(53, "Nether Resist", 377, false),
        // Jewelry only; the value is the rolled power percent ("Cast on Strike 75% power [50-100] - Force Arc, 13% per hit").
        new(54, "Cast on Strike", 0, false),
    };

    /// <summary>
    /// PropertyInt WeaponAugScaleQuality (ACECustom 9060): a T11+ weapon's quality roll, 0-1000, the
    /// number its Weapon Grade letter comes from. Sent in the appraisal since 2026-09-29.
    /// </summary>
    public const int PropWeaponAugScaleQuality = 9060;
    /// <summary>PropertyInt WeaponAugScaleTier (ACECustom 9061): the loot tier a T11+ weapon was stamped at.</summary>
    public const int PropWeaponAugScaleTier = 9061;
    /// <summary>PropertyInt ZcTier (ACECustom 50109): the Zone Control loot tier a ZC-lined piece was stamped at.</summary>
    public const int PropZcTier = 50109;

    /// <summary>Lowest tier the server stamps T11 gear at (LootGenerationFactory.ZoneLootSetMinTier).</summary>
    public const int MinTier = 11;

    // Longest names first, so "Max Health Pct" is tried before "Max Health".
    private static readonly T11ModifierDef[] ModifiersByNameLength = SortByNameLength(Modifiers);

    private static T11ModifierDef[] SortByNameLength(T11ModifierDef[] defs)
    {
        var copy = (T11ModifierDef[])defs.Clone();
        Array.Sort(copy, (a, b) => b.Name.Length.CompareTo(a.Name.Length));
        return copy;
    }

    /// <summary>
    /// The catalog entry whose name starts <paramref name="text"/> as a whole word
    /// ("Max Health Pct +2%" is Max Health Pct, not Max Health), or null.
    /// </summary>
    public static T11ModifierDef? MatchModifier(string text)
    {
        foreach (var def in ModifiersByNameLength)
        {
            if (!text.StartsWith(def.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (text.Length == def.Name.Length || text[def.Name.Length] == ' ') return def;
        }
        return null;
    }

    /// <summary>The catalog entry for a server key, or null.</summary>
    public static T11ModifierDef? ModifierByKey(int key)
    {
        foreach (var def in Modifiers)
            if (def.Key == key) return def;
        return null;
    }

    /// <summary>Rank of a sub-grade label ("B+" = 12), or 0 when it isn't one.</summary>
    public static int GradeRank(string? label)
    {
        if (string.IsNullOrEmpty(label)) return 0;
        foreach (var (id, name) in Grades)
            if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase)) return id;
        return 0;
    }

    /// <summary>The words a "Wield requires:" line uses for each counter.</summary>
    public static readonly (T11Counter Counter, string Name)[] CounterNames =
    {
        (T11Counter.ItemAugmentations, "Item Augmentations"),
        (T11Counter.TriuneWeave, "Triune Weave"),
        (T11Counter.BattlemagesWrath, "Battlemage's Wrath"),
        (T11Counter.NetherVeil, "Nether Veil"),
        (T11Counter.CrashingSteel, "Crashing Steel"),
        (T11Counter.TrueShot, "True Shot"),
    };

    /// <summary>The counter named by a "Wield requires:" line, or null.</summary>
    public static T11Counter? CounterByName(string name)
    {
        foreach (var (counter, n) in CounterNames)
            if (string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase)) return counter;
        return null;
    }

    /// <summary>Display name of a counter.</summary>
    public static string CounterName(T11Counter counter)
    {
        foreach (var (c, n) in CounterNames)
            if (c == counter) return n;
        return counter.ToString();
    }

    /// <summary>Highest loot tier on the server's default weapon-scaling ladder (T11-T25).</summary>
    public const int MaxTier = 25;

    /// <summary>
    /// Triune Weave required per tier above 15 on the default ladder: 500 at T16 up to 5,000
    /// at T25. Weapons and armor carry the same Triune gate; weapons add a family-charm gate
    /// of the same size, which isn't needed for the estimate.
    /// </summary>
    public const int TriunePerTier = 500;

    /// <summary>
    /// The loot tier implied by an item-augmentation wield gate, using the server's default
    /// weapon-scaling table: T11 = 2,000, T12 = 2,500, then +500 per tier, capped at 4,000.
    /// Without a Triune Weave gate the cap makes T15 and above read as 15 (see the
    /// three-argument overload). Returns 11 for a T11-named item with no gate read yet, and 0
    /// for anything else. The server owner can retune the table, so this is an estimate.
    /// </summary>
    public static int EstimateTier(int itemAugGate, bool isT11)
    {
        if (itemAugGate <= 0) return isT11 ? 11 : 0;
        if (itemAugGate < 2500) return 11;
        return Math.Min(15, 12 + (itemAugGate - 2500) / 500);
    }

    /// <summary>
    /// The loot tier implied by both T11 wield gates. The item-aug gate stops rising at T15,
    /// so from T16 the Triune Weave gate (<see cref="TriunePerTier"/> x (tier - 15)) carries
    /// the tier: 500 = T16 ... 5,000 = T25. Any Triune gate means T16 or higher (the server
    /// only stamps it from T16) and the result is capped at <see cref="MaxTier"/>. Without a
    /// Triune gate this is <see cref="EstimateTier(int, bool)"/>.
    /// </summary>
    public static int EstimateTier(int itemAugGate, int triuneGate, bool isT11)
    {
        if (triuneGate > 0) return Math.Clamp(15 + triuneGate / TriunePerTier, 16, MaxTier);
        return EstimateTier(itemAugGate, isT11);
    }

    /// <summary>Modifier keys with a rating, for callers that map Gear* keys back to modifiers.</summary>
    public static IEnumerable<T11ModifierDef> RatingModifiers()
    {
        foreach (var def in Modifiers)
            if (def.RatingKey != 0) yield return def;
    }
}

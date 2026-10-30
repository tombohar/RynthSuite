using System;
using System.Collections.Generic;

namespace RynthCore.Loot.T11;

/// <summary>
/// Virtual long-value keys for T11 loot rules. A loot rule uses them like any long key
/// (LongValKeyGE / LE / E / NE), so VTank-format profiles need no new node types. RynthAi
/// answers them from <see cref="T11ItemInfo"/>; other loot engines (VTank, Mag) don't know
/// them and read them as 0.
/// </summary>
public static class T11Keys
{
    /// <summary>
    /// First id of the reserved block ("RY" in the high bytes). Far above retail STypeInt ids,
    /// ACECustom's custom ids (9000-50999) and Decal's 0x0D000000 WCID key.
    /// </summary>
    public const int Base = 0x52590000;

    /// <summary>1 when the item is T11 gear, else 0.</summary>
    public const int IsT11 = Base + 1;
    /// <summary>
    /// Loot tier (11-25), 0 for non-T11: the server's stamped tier (ZcTier 50109 / WeaponAugScaleTier 9061)
    /// when the appraisal sent it, else estimated from the wield gates (T16+ from the Triune Weave gate).
    /// </summary>
    public const int EstimatedTier = Base + 2;
    /// <summary>Weapon sub-grade rank: S = 16, A+ = 15 ... F- = 1, 0 = no grade.</summary>
    public const int WeaponGrade = Base + 3;
    /// <summary>The weapon's damage as a percent of a perfect roll, 0 when not shown.</summary>
    public const int DamagePercent = Base + 4;
    /// <summary>Item augmentations required to wield, 0 for none.</summary>
    public const int WieldItemAugs = Base + 5;
    /// <summary>1 = this character meets every wield gate, 0 = it doesn't, -1 = a needed counter is unknown.</summary>
    public const int CanWield = Base + 6;
    /// <summary>Number of Zone Control modifier lines.</summary>
    public const int ModifierCount = Base + 7;
    /// <summary>Sum of modifier values that count toward TotalRatings.</summary>
    public const int ModifierRatingTotal = Base + 8;
    /// <summary>Average roll position of the banded modifiers, 0-100.</summary>
    public const int AverageRollPercent = Base + 9;
    /// <summary>Best roll position of the banded modifiers, 0-100.</summary>
    public const int BestRollPercent = Base + 10;
    /// <summary>1 when the item has a slot special (Fortify Vitals, Battle Mending, ...).</summary>
    public const int HasSlotSpecial = Base + 11;
    /// <summary>Number of "Cast on Strike" procs.</summary>
    public const int CastOnStrikeCount = Base + 12;
    /// <summary>1 when the appraisal said "Zone Locked (Power Reduced)". Depends on where you stood when it was appraised.</summary>
    public const int ZoneLocked = Base + 13;
    /// <summary>Triune Weave required to wield (T16+ armor), 0 for none.</summary>
    public const int WieldTriuneWeave = Base + 14;
    /// <summary>Gear Grade rank of armor / jewelry / clothing: S = 16, A+ = 15 ... F- = 1, 0 = no grade.</summary>
    public const int GearGrade = Base + 15;
    /// <summary>Property slots in use ("Properties: N of M"), 0 when not shown.</summary>
    public const int PropertySlots = Base + 16;
    /// <summary>The tier's property slot limit ("Properties: N of M"), 0 when unlimited or not shown.</summary>
    public const int PropertySlotCap = Base + 17;
    /// <summary>Free property slots (limit minus used), 0 without a known limit.</summary>
    public const int FreePropertySlots = Base + 18;
    /// <summary>1 when the item is Tainted (Gear Essence bags no longer work on it).</summary>
    public const int Tainted = Base + 19;
    /// <summary>1 when the tier is the server's stamped value, 0 when it is estimated from the wield gates.</summary>
    public const int TierIsExact = Base + 20;

    /// <summary>
    /// Base of the per-modifier keys: <c>ModifierBase + catalog key</c> is that modifier's value
    /// on the item, 0 when absent (ModifierBase + 28 = Damage Rating).
    /// </summary>
    public const int ModifierBase = Base + 0x100;

    /// <summary>One past the last id in the block.</summary>
    public const int End = Base + 0x200;

    /// <summary>True for any id in the T11 block.</summary>
    public static bool IsVirtual(int key) => key > Base && key < End;

    /// <summary>The per-modifier key for a catalog key.</summary>
    public static int ForModifier(int catalogKey) => ModifierBase + catalogKey;

    /// <summary>Editor names for every T11 key, summary keys first.</summary>
    public static IEnumerable<(int Id, string Name)> Names()
    {
        yield return (IsT11, "T11: Is T11");
        yield return (EstimatedTier, "T11: Tier");
        yield return (TierIsExact, "T11: Tier Is Exact");
        yield return (WeaponGrade, "T11: Weapon Grade");
        yield return (DamagePercent, "T11: Damage % of Max");
        yield return (GearGrade, "T11: Gear Grade");
        yield return (PropertySlots, "T11: Property Slots Used");
        yield return (PropertySlotCap, "T11: Property Slot Limit");
        yield return (FreePropertySlots, "T11: Free Property Slots");
        yield return (Tainted, "T11: Tainted");
        yield return (WieldItemAugs, "T11: Wield Item Augs");
        yield return (WieldTriuneWeave, "T11: Wield Triune Weave");
        yield return (CanWield, "T11: Can Wield");
        yield return (ModifierCount, "T11: Modifier Count");
        yield return (ModifierRatingTotal, "T11: Modifier Ratings");
        yield return (AverageRollPercent, "T11: Avg Roll %");
        yield return (BestRollPercent, "T11: Best Roll %");
        yield return (HasSlotSpecial, "T11: Has Slot Special");
        yield return (CastOnStrikeCount, "T11: Cast on Strike Count");
        yield return (ZoneLocked, "T11: Zone Locked");
        foreach (var def in T11Catalog.Modifiers)
            yield return (ForModifier(def.Key), "T11 Mod: " + def.Name);
    }

    /// <summary>
    /// The value of T11 key <paramref name="key"/> for <paramref name="item"/>. CanWield needs
    /// <paramref name="player"/>; without it CanWield reads -1 (unknown) for gated items.
    /// Returns false for an id outside the block or an unused id in it.
    /// </summary>
    public static bool TryGetValue(T11ItemInfo item, int key, T11PlayerAugs? player, out int value)
    {
        value = 0;
        if (!IsVirtual(key)) return false;
        if (key >= ModifierBase)
        {
            int catalogKey = key - ModifierBase;
            if (T11Catalog.ModifierByKey(catalogKey) == null) return false;
            value = item.ModifierValue(catalogKey);
            return true;
        }
        switch (key)
        {
            case IsT11: value = item.IsT11 ? 1 : 0; return true;
            case EstimatedTier: value = item.Tier; return true;
            case TierIsExact: value = item.TierIsExact ? 1 : 0; return true;
            case GearGrade: value = item.GearGradeRank; return true;
            case PropertySlots: value = Math.Max(0, item.PropertySlots); return true;
            case PropertySlotCap: value = item.PropertySlotCap; return true;
            case FreePropertySlots: value = item.FreePropertySlots; return true;
            case Tainted: value = item.Tainted ? 1 : 0; return true;
            case WeaponGrade: value = item.GradeRank; return true;
            case DamagePercent: value = item.DamagePercent < 0 ? 0 : item.DamagePercent; return true;
            case WieldItemAugs: value = item.WieldItemAugs; return true;
            case WieldTriuneWeave: value = item.GateAmount(T11Counter.TriuneWeave); return true;
            case CanWield:
                // Before its ID a T11 item's gates are unknown, so it can't count as wieldable yet.
                if (item.WieldGates.Count == 0) value = item.IsT11 && !item.HasText ? -1 : 1;
                else value = player?.CanWield(item) ?? -1;
                return true;
            case ModifierCount: value = item.Modifiers.Count; return true;
            case ModifierRatingTotal: value = item.ModifierRatingTotal; return true;
            case AverageRollPercent: value = item.AverageRollPercent; return true;
            case BestRollPercent: value = item.BestRollPercent; return true;
            case HasSlotSpecial: value = item.HasSlotSpecial ? 1 : 0; return true;
            case CastOnStrikeCount: value = item.Procs.Count; return true;
            case ZoneLocked: value = item.ZoneLocked ? 1 : 0; return true;
            default: return false;
        }
    }

    /// <summary>
    /// The value the server stripped from a T11 item's appraisal, for the retail keys loot rules
    /// already use: wield requirement slot 1 (158 = 13 Int64 Property, 159 = 9008, 160 = the
    /// item-aug gate) and the Gear* ratings a modifier line carries (370-379). Only call this
    /// when the live property is absent. Returns false when the item supplies no value.
    /// </summary>
    public static bool TryGetStrippedValue(T11ItemInfo item, int key, out int value)
    {
        value = 0;
        if (!item.IsT11) return false;
        switch (key)
        {
            case 158:
                if (item.WieldItemAugs <= 0) return false;
                value = WieldRequirementInt64Stat;
                return true;
            case 159:
                if (item.WieldItemAugs <= 0) return false;
                value = (int)T11Counter.ItemAugmentations;
                return true;
            case 160:
                if (item.WieldItemAugs <= 0) return false;
                value = item.WieldItemAugs;
                return true;
        }
        if (key < 370 || key > 379) return false;
        int fromMods = item.RatingFromModifiers(key);
        if (fromMods == 0) return false;
        value = fromMods;
        return true;
    }

    /// <summary>ACECustom's WieldRequirement.Int64Stat (the value after HeritageType = 12).</summary>
    public const int WieldRequirementInt64Stat = 13;

    /// <summary>True for the retail keys <see cref="TryGetStrippedValue"/> can answer.</summary>
    public static bool IsStrippedKey(int key) => key is 158 or 159 or 160 || (key >= 370 && key <= 379);
}

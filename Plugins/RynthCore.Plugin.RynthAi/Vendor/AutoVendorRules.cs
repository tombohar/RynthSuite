using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Loot;
using RynthCore.Loot.VTank;

namespace RynthCore.Plugin.RynthAi.Vendor;

// Pure rule judging for AutoVendor: no host, no WorldObject. The manager hands in a
// per-condition matcher (VTankLootEvaluator.MatchCondition) and the facts it has about
// the item; this file decides which conditions can be answered with that data and turns
// the answers into a profile verdict. Kept dependency-free so the test project can link it.
//
// Selling is destructive, so every answer is three-valued: Yes, No, or Unknown ("we don't
// have the data VTank would use"). A rule with an Unknown condition never counts as a
// match. UB leans on VTank's own evaluator; ours has approximations (buffed damage, colour
// similarity, base skill), and those node types always read Unknown here.

internal enum Tri : byte
{
    No = 0,
    Yes = 1,
    Unknown = 2,
}

/// <summary>What the manager knows about an item before judging it.</summary>
internal readonly struct ItemFacts
{
    /// <summary>An item on the vendor's list: only the engine's vendor snapshot is known.</summary>
    public bool IsVendorListing { get; init; }
    /// <summary>Inventory item with appraisal (ID) data.</summary>
    public bool Appraised { get; init; }
    public AcObjectClass ObjectClass { get; init; }
    /// <summary>The item's spell list can be read (appraised and the host exposes spell ids).</summary>
    public bool HasSpellData { get; init; }
    public bool HasPalettes { get; init; }
    public bool HasCharacterSkills { get; init; }
    public bool HasCharacterLevel { get; init; }
}

/// <summary>The profile's answer for one item.</summary>
internal sealed class ProfileVerdict
{
    /// <summary>First rule that matched (Certain) or might match (!Certain); null = no rule can match.</summary>
    public VTankLootRule? Rule { get; init; }
    public int RuleIndex { get; init; } = -1;
    /// <summary>True when Rule definitely matched, or when no rule can match at all.</summary>
    public bool Certain { get; init; }
    /// <summary>Some Keep / Keep # rule anywhere in the profile matches or might match.</summary>
    public bool KeepPossible { get; init; }
    /// <summary>Name of a Keep / Keep # rule that matches or might match (for messages).</summary>
    public string KeepRuleName { get; init; } = string.Empty;
    /// <summary>Why the deciding rule could not be judged (node type name), when !Certain.</summary>
    public string UnknownReason { get; init; } = string.Empty;

    public string RuleName => Rule == null ? string.Empty : AutoVendorRules.RuleLabel(Rule, RuleIndex);
}

internal enum BuyKind : byte
{
    None = 0,
    /// <summary>Keep: buy as many as you can afford.</summary>
    Keep = 1,
    /// <summary>Keep #: buy up to the rule's count.</summary>
    KeepUpTo = 2,
}

internal readonly record struct BuyDecision(BuyKind Kind, int KeepCount, string RuleName);

internal readonly record struct SellDecision(bool Sell, string RuleName, string Reason);

internal static class AutoVendorRules
{
    // STypeInt keys a vendor listing carries (engine VendorItem: ItemType, Burden,
    // MaxStackSize, StackSize, Value). Anything else on a listing is unknown.
    internal static readonly HashSet<int> ListingIntKeys = new() { 1, 5, 11, 12, 19 };

    // Keys the client keeps in PublicWeenieDesc, readable without appraisal
    // (items/containers capacity, locations, wielded location, stack sizes, material).
    internal static readonly HashSet<int> PwdIntKeys = new() { 6, 7, 9, 10, 11, 12, 131 };

    // Decal's synthetic LongValueKey ids start at 0x0D000000; RynthAi reads STypes only.
    internal const int MaxStandardKey = 0xFFFF;
    // Except Decal's "Type" (0x0D000000), the weenie class id, which RynthAi reads too.
    internal const int DecalTypeKey = 0x0D000000;

    // RynthAi derives ObjectClass from ITEM_TYPE alone. Decal splits these further
    // (Misc -> HealingKit/Lockpick, Book -> Scroll/Journal/Sign, Container -> Foci,
    // Key -> Lockpick, Food -> Plant), so an ObjectClass test on them is unknown.
    internal static readonly HashSet<AcObjectClass> CoarseClasses = new()
    {
        AcObjectClass.Unknown, AcObjectClass.Misc, AcObjectClass.Book,
        AcObjectClass.Container, AcObjectClass.Food, AcObjectClass.Key,
    };

    public static string RuleLabel(VTankLootRule rule, int index)
        => string.IsNullOrWhiteSpace(rule.Name) ? $"#{index}" : rule.Name.Trim();

    /// <summary>Can this condition be answered for an item with these facts?</summary>
    public static bool CanJudge(VTankLootCondition cond, in ItemFacts f)
    {
        var d = cond.DataLines;
        switch (cond.NodeType)
        {
            case VTankNodeTypes.DisabledRule:
            case VTankNodeTypes.DamagePercentGE:            // VTank itself always answers "no"
            case VTankNodeTypes.ObjectClass:                // coarse classes handled in JudgeObjectClass
                return true;

            case VTankNodeTypes.CharacterMainPackEmptySlotsGE:
                return true;
            case VTankNodeTypes.CharacterSkillGE:
                return f.HasCharacterSkills;
            case VTankNodeTypes.CharacterLevelGE:
            case VTankNodeTypes.CharacterLevelLE:
                return f.HasCharacterLevel;

            case VTankNodeTypes.StringValueMatch:
            {
                if (!TryKey(d, 1, out int key)) return false;
                if (key == 1) return true;                          // Name
                return !f.IsVendorListing && f.Appraised && key <= MaxStandardKey;
            }

            case VTankNodeTypes.LongValKeyLE:
            case VTankNodeTypes.LongValKeyGE:
            case VTankNodeTypes.LongValKeyE:
            case VTankNodeTypes.LongValKeyNE:
            case VTankNodeTypes.LongValKeyFlagExists:
            {
                if (TryKey(d, 1, out int wkey) && wkey == DecalTypeKey) return true;   // the WCID: always known
                if (!TryKey(d, 1, out int key) || key < 0 || key > MaxStandardKey) return false;
                if (f.IsVendorListing) return ListingIntKeys.Contains(key);
                return f.Appraised || PwdIntKeys.Contains(key);
            }

            case VTankNodeTypes.DoubleValKeyLE:
            case VTankNodeTypes.DoubleValKeyGE:
            {
                if (!TryKey(d, 1, out int key) || key < 0 || key > MaxStandardKey) return false;
                return !f.IsVendorListing && f.Appraised;
            }

            case VTankNodeTypes.SpellNameMatch:
            case VTankNodeTypes.SpellMatch:
            case VTankNodeTypes.SpellCountGE:
                return !f.IsVendorListing && f.HasSpellData;

            case VTankNodeTypes.SlotExactPalette:
                return !f.IsVendorListing && f.HasPalettes;

            case VTankNodeTypes.TotalRatingsGE:
                return !f.IsVendorListing && f.Appraised;

            // Approximated or not implemented by RynthAi's evaluator: never trusted.
            case VTankNodeTypes.AnySimilarColor:
            case VTankNodeTypes.SimilarColorArmorType:
            case VTankNodeTypes.SlotSimilarColor:
            case VTankNodeTypes.CharacterBaseSkill:
            case VTankNodeTypes.MinDamageGE:
            case VTankNodeTypes.BuffedMedianDamageGE:
            case VTankNodeTypes.BuffedMissileDamageGE:
            case VTankNodeTypes.BuffedLongValKeyGE:
            case VTankNodeTypes.BuffedDoubleValKeyGE:
            case VTankNodeTypes.CalcdBuffedTinkedDamageGE:
            case VTankNodeTypes.CalcedBuffedTinkedTargetMeleeGE:
            default:
                return false;
        }
    }

    /// <summary>ObjectClass test without the evaluator: unknown for classes Decal splits further.</summary>
    public static Tri JudgeObjectClass(VTankLootCondition cond, AcObjectClass itemClass)
    {
        if (!TryKey(cond.DataLines, 0, out int target)) return Tri.Unknown;
        if (CoarseClasses.Contains(itemClass)) return Tri.Unknown;
        return (int)itemClass == target ? Tri.Yes : Tri.No;
    }

    /// <summary>
    /// Judge one condition. <paramref name="match"/> is only called for conditions that
    /// CanJudge says are answerable; if it throws (malformed data line) the answer is Unknown.
    /// </summary>
    public static Tri JudgeCondition(VTankLootCondition cond, in ItemFacts f, Func<VTankLootCondition, bool> match)
    {
        if (!CanJudge(cond, f)) return Tri.Unknown;
        if (cond.NodeType == VTankNodeTypes.ObjectClass) return JudgeObjectClass(cond, f.ObjectClass);
        try { return match(cond) ? Tri.Yes : Tri.No; }
        catch { return Tri.Unknown; }
    }

    /// <summary>All conditions must hold: any No -> No, else any Unknown -> Unknown, else Yes.</summary>
    public static Tri JudgeRule(VTankLootRule rule, Func<VTankLootCondition, Tri> cond, out string unknownReason)
    {
        unknownReason = string.Empty;
        if (rule.Conditions.Count == 0) return Tri.Yes;
        bool unknown = false;
        foreach (var c in rule.Conditions)
        {
            Tri t = cond(c);
            if (t == Tri.No) { unknownReason = string.Empty; return Tri.No; }
            if (t == Tri.Unknown && !unknown)
            {
                unknown = true;
                unknownReason = VTankNodeTypes.DisplayName(c.NodeType);
            }
        }
        return unknown ? Tri.Unknown : Tri.Yes;
    }

    /// <summary>
    /// VTank order: the first rule that matches decides. A rule that might match stops the
    /// scan too (we can't know whether it or a later rule applies). Also records whether
    /// any Keep / Keep # rule anywhere could match, which AutoVendor treats as "never sell".
    /// </summary>
    public static ProfileVerdict Decide(IReadOnlyList<VTankLootRule> rules, Func<VTankLootCondition, Tri> cond)
    {
        VTankLootRule? decided = null;
        int decidedIdx = -1;
        bool certain = true;
        string reason = string.Empty;
        bool keepPossible = false;
        string keepName = string.Empty;

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            bool isKeep = rule.Action is VTankLootAction.Keep or VTankLootAction.KeepUpTo;
            if (decided != null && (!isKeep || keepPossible))
                continue;   // only still scanning for keep rules

            Tri t = JudgeRule(rule, cond, out string why);
            if (t == Tri.No) continue;

            if (isKeep && !keepPossible)
            {
                keepPossible = true;
                keepName = RuleLabel(rule, i);
            }
            if (decided == null)
            {
                decided = rule;
                decidedIdx = i;
                certain = t == Tri.Yes;
                reason = why;
            }
        }

        return new ProfileVerdict
        {
            Rule = decided,
            RuleIndex = decidedIdx,
            Certain = decided == null || certain,
            UnknownReason = reason,
            KeepPossible = keepPossible,
            KeepRuleName = keepName,
        };
    }

    /// <summary>
    /// UB GetBuyItems for one vendor listing: Keep # buys up to the count, Keep buys all it
    /// can afford. First match decides, as in VTank. A rule we can't judge (a listing carries
    /// only name, type, value, stack and burden) is handled so we never buy more than VTank would:
    /// an unjudgeable non-buy rule ends the scan with no purchase (it might be the one that
    /// matches); an unjudgeable Keep / Keep # rule lets the scan go on, since either way the
    /// item gets bought, and the smallest count seen is used.
    /// </summary>
    public static BuyDecision DecideBuy(IReadOnlyList<VTankLootRule> rules, Func<VTankLootCondition, Tri> cond)
    {
        long cap = long.MaxValue;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            Tri t = JudgeRule(rule, cond, out _);
            if (t == Tri.No) continue;

            long count = rule.Action switch
            {
                VTankLootAction.KeepUpTo => Math.Max(0, rule.KeepCount ?? 0),
                VTankLootAction.Keep => int.MaxValue,
                _ => -1,
            };
            if (count < 0)
                return new BuyDecision(BuyKind.None, 0, RuleLabel(rule, i));   // a non-buy rule matches or might

            cap = Math.Min(cap, count);
            if (t == Tri.Yes)
            {
                int n = (int)Math.Min(cap, int.MaxValue);
                return n == int.MaxValue
                    ? new BuyDecision(BuyKind.Keep, int.MaxValue, RuleLabel(rule, i))
                    : new BuyDecision(BuyKind.KeepUpTo, n, RuleLabel(rule, i));
            }
        }
        return new BuyDecision(BuyKind.None, 0, string.Empty);
    }

    /// <summary>
    /// UB GetSellItems (result.IsSell), stricter: the deciding rule must be a Sell rule that
    /// definitely matched, and no Keep / Keep # rule anywhere in the profile may match or
    /// possibly match the item.
    /// </summary>
    public static SellDecision JudgeSell(ProfileVerdict v)
    {
        if (v.Rule == null) return new SellDecision(false, string.Empty, "no rule matches");
        if (v.Rule.Action != VTankLootAction.Sell)
            return new SellDecision(false, v.RuleName, $"first rule is {v.Rule.Action}");
        if (!v.Certain)
            return new SellDecision(false, v.RuleName, $"rule '{v.RuleName}' needs {v.UnknownReason} data we don't have");
        if (v.KeepPossible)
            return new SellDecision(false, v.RuleName, $"also matches keep rule '{v.KeepRuleName}'");
        return new SellDecision(true, v.RuleName, string.Empty);
    }

    private static bool TryKey(IList<string> d, int idx, out int value)
    {
        value = 0;
        return d != null && idx < d.Count
            && int.TryParse(d[idx].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

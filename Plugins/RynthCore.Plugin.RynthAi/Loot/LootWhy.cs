using System.Collections.Generic;
using RynthCore.Loot.VTank;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// /ra loot why: which rule of a VTank profile takes an item, and why the rules above it
/// didn't. Pure (no chat, no host calls beyond the context's reads), so tests can run it.
/// </summary>
internal static class LootWhy
{
    /// <summary>One rule above the winner that came close: it passed at least one condition before one failed.</summary>
    public readonly record struct NearMiss(int Index, string RuleName, string Failed);

    public sealed class Result
    {
        public VTankLootVerdict Verdict { get; init; }
        /// <summary>The matched rule (Match), the rule that waits for the ID (NeedsId), or -1.</summary>
        public int Index { get; init; } = -1;
        public string RuleName { get; init; } = string.Empty;
        public VTankLootAction Action { get; init; }
        public List<NearMiss> NearMisses { get; } = new();
    }

    public static string NameOf(VTankLootRule rule, int index)
        => string.IsNullOrWhiteSpace(rule.Name) ? "#" + index : rule.Name.Trim();

    /// <summary>
    /// The decision (<see cref="VTankLootEvaluator.Decide"/>) and, for each enabled rule above
    /// it that passed a condition before failing, the first condition that failed and what it read.
    /// Rules that fail on their first condition (another class, weapon type, material or name)
    /// aren't listed: they were never close.
    /// </summary>
    public static Result Explain(VTankLootProfile profile, WorldObject item, VTankLootContext? ctx, bool hasIdData)
    {
        VTankLootVerdict verdict = VTankLootEvaluator.Decide(profile, item, ctx, hasIdData, out int index);
        var result = new Result
        {
            Verdict = verdict,
            Index = index,
            RuleName = index >= 0 ? NameOf(profile.Rules[index], index) : string.Empty,
            Action = index >= 0 ? profile.Rules[index].Action : default,
        };

        int end = index >= 0 ? index : profile.Rules.Count;
        for (int i = 0; i < end; i++)
        {
            VTankLootRule rule = profile.Rules[i];
            if (IsDisabled(rule)) continue;
            int passed = 0;
            foreach (VTankLootCondition cond in rule.Conditions)
            {
                if (!hasIdData && VTankLootEvaluator.ConditionNeedsId(cond)) continue;
                bool ok;
                try { ok = VTankLootEvaluator.MatchCondition(cond, item, ctx); }
                catch { ok = false; }
                if (ok) { passed++; continue; }
                if (passed > 0)
                    result.NearMisses.Add(new NearMiss(i, NameOf(rule, i), VTankLootEvaluator.Explain(cond, item, ctx)));
                break;
            }
        }
        return result;
    }

    private static bool IsDisabled(VTankLootRule rule)
    {
        foreach (VTankLootCondition c in rule.Conditions)
            if (c.NodeType == VTankNodeTypes.DisabledRule && c.DataLines.Count > 0
                && string.Equals(c.DataLines[0].Trim(), "true", System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

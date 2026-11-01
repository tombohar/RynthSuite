using System.Collections.Generic;
using RynthCore.Loot.VTank;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// /ra loot why: which rule of a VTank profile takes an item, and why the rules above it
/// didn't, and the words for it (chat and the "Why this loot rule?" window). Pure (no chat,
/// no host calls beyond the context's reads), so tests can run it.
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
        /// <summary>
        /// The first switched-off rule above the decision that would take the item if it were on
        /// (<see cref="VTankLootEvaluator.FirstSwitchedOffMatch"/>), or -1. Only with the ID in.
        /// </summary>
        public int SwitchedOffIndex { get; init; } = -1;
        public string SwitchedOffName { get; init; } = string.Empty;
        public VTankLootAction SwitchedOffAction { get; init; }
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
        // Without the ID its stats read 0, so a switched-off rule can't be judged yet.
        int off = hasIdData ? VTankLootEvaluator.FirstSwitchedOffMatch(profile, item, ctx, index) : -1;
        var result = new Result
        {
            Verdict = verdict,
            Index = index,
            RuleName = index >= 0 ? NameOf(profile.Rules[index], index) : string.Empty,
            Action = index >= 0 ? profile.Rules[index].Action : default,
            SwitchedOffIndex = off,
            SwitchedOffName = off >= 0 ? NameOf(profile.Rules[off], off) : string.Empty,
            SwitchedOffAction = off >= 0 ? profile.Rules[off].Action : default,
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

    /// <summary>
    /// What /ra loot why answers and the "Why this loot rule?" window shows (immutable, so the
    /// tick can hand it to the render thread). <see cref="Ok"/> false: <see cref="Error"/> says
    /// why there is no answer. <see cref="Looter"/>: what the looter does with it ("[Keep] rule",
    /// "left"). <see cref="RuleDetail"/>: a VTank profile, so <see cref="Decision"/> names its
    /// rule and <see cref="NearMisses"/> lists the rules above it that came close.
    /// </summary>
    public sealed record Report(bool Ok, string Error, string ItemName, bool HasId, string Looter, string Decision,
        bool RuleDetail, bool AskedForId, IReadOnlyList<NearMiss> NearMisses)
    {
        public static Report Fail(string why) =>
            new(false, why, string.Empty, false, string.Empty, string.Empty, false, false, System.Array.Empty<NearMiss>());

        /// <summary>"'Item' (identified) -> [Keep] rule".</summary>
        public string Headline => Ok ? $"'{ItemName}' ({IdNote(HasId)}) -> {Looter}" : Error;

        /// <summary>
        /// A switched-off rule above the decision would take the item ("#12 '(W) War (Max 385)' [Keep]"),
        /// or empty. Said ahead of the near misses: it is the usual answer to "why was this left?"
        /// when the profile has the rule but it's off.
        /// </summary>
        public string SwitchedOff { get; init; } = string.Empty;
    }

    /// <summary>"Switched off in the profile: #12 '(W) War (Max 385)' [Keep] would take it."</summary>
    public static string SwitchedOffLine(string switchedOff) =>
        $"Switched off in the profile: {switchedOff} would take it. Switch that rule on in the Loot Editor to loot items like this.";

    /// <summary>"identified" / "not identified yet".</summary>
    public static string IdNote(bool hasId) => hasId ? "identified" : "not identified yet";

    /// <summary>
    /// The report for <paramref name="itemName"/>: the looter's own verdict (<paramref name="looter"/>:
    /// "[Keep] rule" or "left", with a built-in keep), then, with <paramref name="why"/> (a VTank
    /// profile, <paramref name="file"/> with <paramref name="ruleCount"/> rules), the rule that decides.
    /// </summary>
    public static Report Describe(string itemName, bool hasId, string looter, string file, int ruleCount, Result? why, bool askedForId)
    {
        if (why == null)
            return new Report(true, string.Empty, itemName, hasId, looter, string.Empty, false, false, System.Array.Empty<NearMiss>());
        string rule = why.Index >= 0 ? $"rule {why.Index + 1} of {ruleCount} '{why.RuleName}'" : string.Empty;
        string decision = why.Verdict switch
        {
            VTankLootVerdict.Match => $"{file}: {rule} [{why.Action}]",
            VTankLootVerdict.NeedsId => $"{file}: {rule} needs the item's ID to decide",
            _ => $"{file}: no rule matches",
        };
        string off = why.SwitchedOffIndex >= 0 ? $"#{why.SwitchedOffIndex + 1} '{why.SwitchedOffName}' [{why.SwitchedOffAction}]" : string.Empty;
        return new Report(true, string.Empty, itemName, hasId, looter, decision, true, askedForId, why.NearMisses.ToArray())
        {
            SwitchedOff = off,
        };
    }

    /// <summary>
    /// The report when the Loot Mode, not the profile, decides (rares mode): the looter's verdict
    /// and <paramref name="modeNote"/>, shown in place of the rule-by-rule detail.
    /// </summary>
    public static Report ModeOnly(string itemName, bool hasId, string looter, string modeNote) =>
        new(true, string.Empty, itemName, hasId, looter, modeNote, false, false, System.Array.Empty<NearMiss>());

    /// <summary>
    /// /ra loot why's chat lines for <paramref name="r"/>, at most <paramref name="maxNearMisses"/>
    /// near misses (the rest are counted; the log has them all). A failed report is one line.
    /// </summary>
    public static List<string> ChatLines(Report r, int maxNearMisses)
    {
        var lines = new List<string>();
        if (!r.Ok)
        {
            lines.Add("[RynthAi] loot why: " + r.Headline);
            return lines;
        }
        if (!r.RuleDetail)
        {
            lines.Add(r.Decision.Length > 0
                ? $"[RynthAi] Loot why: {r.Headline}. {r.Decision}."
                : $"[RynthAi] Loot why: {r.Headline}. (Rule-by-rule detail is for VTank .utl profiles.)");
            return lines;
        }
        lines.Add("[RynthAi] Loot why: " + r.Headline);
        lines.Add("[RynthAi]   " + r.Decision);
        if (r.AskedForId) lines.Add("[RynthAi]   Asked for its ID; /ra loot why again once it's in.");
        if (r.SwitchedOff.Length > 0) lines.Add("[RynthAi]   " + SwitchedOffLine(r.SwitchedOff));
        if (r.NearMisses.Count == 0)
        {
            lines.Add("[RynthAi]   No rule above it came close (each failed on its first condition).");
            return lines;
        }
        lines.Add($"[RynthAi]   {r.NearMisses.Count} rule(s) above it came close; the first condition each failed:");
        for (int i = 0; i < r.NearMisses.Count && i < maxNearMisses; i++)
            lines.Add("[RynthAi]   " + NearMissLine(r.NearMisses[i]));
        if (r.NearMisses.Count > maxNearMisses)
            lines.Add($"[RynthAi]   ... {r.NearMisses.Count - maxNearMisses} more in the log ([LootWhy] lines).");
        return lines;
    }

    /// <summary>"#3 'Good bows': ..." (chat, the log and the window).</summary>
    public static string NearMissLine(NearMiss m) => $"#{m.Index + 1} '{m.RuleName}': {m.Failed}";

    private static bool IsDisabled(VTankLootRule rule)
    {
        foreach (VTankLootCondition c in rule.Conditions)
            if (c.NodeType == VTankNodeTypes.DisabledRule && c.DataLines.Count > 0
                && string.Equals(c.DataLines[0].Trim(), "true", System.StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

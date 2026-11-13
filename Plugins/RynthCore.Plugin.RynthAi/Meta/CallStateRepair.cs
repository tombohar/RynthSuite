using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>
/// CallState rules that lost their target (2026-11-11).
///
/// Before 34b7d45e (2026-09-30) the .met parser read a CallState's data (a TABLE {st, ret}) as a
/// string and left the target empty. The .af writer then saved such a rule as
/// "CallState {} {&lt;the rule's own state&gt;}" ("{Default}" for one inside a DoAll), so every .af
/// saved from a .met through RynthAi's meta editor before then has every CallState broken: the
/// rule fires, calls nothing (MetaManager skips an empty target), latches, and the meta stays in
/// that state for good. The return state written then is wrong as well (VTank's "ret" is often the
/// next state, not the caller).
///
/// This finds those rules (nested DoAll ones included) and, when the original .met is still
/// there, puts its CallState targets and return states back, matched by order: per state, the
/// .met's CallState actions depth-first and the .af's in the same walk. The .af writer keeps each
/// state's rules in file order, so the Nth CallState of a state is the same rule in both files.
/// A state whose counts differ is refused rather than guessed.
/// </summary>
internal static class CallStateRepair
{
    /// <summary>A CallState rule with no state to call.</summary>
    public static bool IsEmptyTarget(MetaRule rule)
        => rule.Action == MetaActionType.CallMetaState && string.IsNullOrWhiteSpace(rule.ActionData);

    /// <summary>
    /// The CallState actions of one rule, depth first: the rule itself, or the children of a DoAll
    /// (the same lists MetaManager runs: ActionChildren, else Children).
    /// </summary>
    public static IEnumerable<MetaRule> CallStatesInOrder(MetaRule rule)
    {
        if (rule.Action == MetaActionType.CallMetaState)
        {
            yield return rule;
            yield break;
        }
        if (rule.Action != MetaActionType.All) yield break;
        List<MetaRule>? children = rule.ActionChildren is { Count: > 0 } ? rule.ActionChildren : rule.Children;
        if (children == null) yield break;
        foreach (MetaRule child in children)
            foreach (MetaRule c in CallStatesInOrder(child))
                yield return c;
    }

    /// <summary>Each state's CallState actions in order (states in first-seen order; names ignore case).</summary>
    public static List<(string State, List<MetaRule> Calls)> CallStatesByState(IEnumerable<MetaRule> rules)
    {
        var order = new List<(string State, List<MetaRule> Calls)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (MetaRule rule in rules)
        {
            string state = rule.State ?? string.Empty;
            if (!index.TryGetValue(state, out int i))
            {
                i = order.Count;
                index[state] = i;
                order.Add((state, new List<MetaRule>()));
            }
            order[i].Calls.AddRange(CallStatesInOrder(rule));
        }
        return order;
    }

    /// <summary>The states that have CallState rules without a target, and how many each (file order).</summary>
    public static List<(string State, int Count)> EmptyTargets(IEnumerable<MetaRule> rules)
    {
        var result = new List<(string State, int Count)>();
        foreach (var (state, calls) in CallStatesByState(rules))
        {
            int n = 0;
            foreach (MetaRule c in calls)
                if (IsEmptyTarget(c)) n++;
            if (n > 0) result.Add((state, n));
        }
        return result;
    }

    /// <summary>How many CallState rules have no target.</summary>
    public static int CountEmpty(IEnumerable<MetaRule> rules)
    {
        int n = 0;
        foreach (var (_, count) in EmptyTargets(rules)) n += count;
        return n;
    }

    /// <summary>
    /// The .met the .af came from: the same name next to it, else in a folder above it up to the
    /// MetaFiles folder (metas live flat in MetaFiles; this also covers a subfolder of it). Null
    /// when there is none.
    /// </summary>
    public static string? FindSourceMet(string afPath)
    {
        try
        {
            if (string.IsNullOrEmpty(afPath)) return null;
            string full = Path.GetFullPath(afPath);
            string metName = Path.GetFileNameWithoutExtension(full) + ".met";
            string? dir = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(dir)) return null;
            string own = Path.Combine(dir, metName);
            if (File.Exists(own)) return own;
            // In a subfolder of MetaFiles: the folders above it, up to MetaFiles itself.
            if (IsMetaFiles(dir) || !UnderMetaFiles(dir)) return null;
            for (string? d = Path.GetDirectoryName(dir); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            {
                string candidate = Path.Combine(d, metName);
                if (File.Exists(candidate)) return candidate;
                if (IsMetaFiles(d)) break;
            }
        }
        catch { }
        return null;
    }

    private static bool IsMetaFiles(string dir)
        => Path.GetFileName(dir).Equals("MetaFiles", StringComparison.OrdinalIgnoreCase);

    private static bool UnderMetaFiles(string dir)
    {
        for (string? d = Path.GetDirectoryName(dir); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (IsMetaFiles(d)) return true;
        return false;
    }

    /// <summary>The chat line for a meta that loaded with CallState rules that call nothing.</summary>
    public static string WarningLine(string afFileName, int count, string? metPath)
    {
        string rules = count == 1 ? "1 CallState rule has" : $"{count} CallState rules have";
        string head = $"[RynthAi] {afFileName}: {rules} no state to call. The file was saved by an older RynthAi and the targets are lost.";
        return metPath != null
            ? $"{head} Load the original .met instead, or use Repair from .met (Macro Rules window, or /ra meta repair) to fill them from {Path.GetFileName(metPath)}."
            : $"{head} No {Path.GetFileNameWithoutExtension(afFileName)}.met is next to it to repair from: load the original .met if you have it, or set each CallState's target in the editor.";
    }

    /// <summary>What <see cref="Repair"/> did.</summary>
    internal sealed class Result
    {
        public bool Ok;
        /// <summary>CallState rules given a target.</summary>
        public int Filled;
        /// <summary>States with a rule filled.</summary>
        public int States;
        /// <summary>Why it was refused (empty when it worked).</summary>
        public string Why = string.Empty;
        /// <summary>Each state the repair touched, with the targets it put back ("20_UPPER_S: 90_DO_STONE").</summary>
        public List<string> Details = new();
    }

    /// <summary>
    /// Fills the empty CallState targets (and their return states) in <paramref name="afRules"/>
    /// from <paramref name="metRules"/>, by order within each state. Nothing is changed unless every
    /// state with an empty target matches: the state is in the .met, it has as many CallState
    /// actions there, and the .met's matching rule has a target. A CallState that already has a
    /// target is left as it is.
    /// </summary>
    public static Result Repair(List<MetaRule> afRules, List<MetaRule> metRules)
    {
        var result = new Result();
        var af = CallStatesByState(afRules);
        var met = new Dictionary<string, List<MetaRule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (state, calls) in CallStatesByState(metRules))
            met[state] = calls;

        var problems = new List<string>();
        var work = new List<(string State, List<MetaRule> Af, List<MetaRule> Met)>();
        foreach (var (state, calls) in af)
        {
            bool needs = false;
            foreach (MetaRule c in calls)
                if (IsEmptyTarget(c)) { needs = true; break; }
            if (!needs) continue;

            if (!met.TryGetValue(state, out var metCalls))
            {
                problems.Add($"state '{state}' isn't in the .met");
                continue;
            }
            if (metCalls.Count != calls.Count)
            {
                problems.Add($"state '{state}' has {calls.Count} CallState rule{(calls.Count == 1 ? "" : "s")} in the .af and {metCalls.Count} in the .met");
                continue;
            }
            for (int i = 0; i < calls.Count; i++)
                if (IsEmptyTarget(calls[i]) && string.IsNullOrWhiteSpace(metCalls[i].ActionData))
                    problems.Add($"state '{state}': CallState {i + 1} has no target in the .met either");
            work.Add((state, calls, metCalls));
        }

        if (work.Count == 0 && problems.Count == 0)
        {
            result.Why = "no CallState rule is missing its target";
            return result;
        }
        if (problems.Count > 0)
        {
            const int Shown = 3;
            string list = string.Join("; ", problems.GetRange(0, Math.Min(Shown, problems.Count)));
            if (problems.Count > Shown) list += $"; and {problems.Count - Shown} more";
            result.Why = $"the .met doesn't match the .af ({list}). The .af may have been edited since; nothing was changed";
            return result;
        }

        foreach (var (state, afCalls, metCalls) in work)
        {
            var filled = new List<string>();
            for (int i = 0; i < afCalls.Count; i++)
            {
                if (!IsEmptyTarget(afCalls[i])) continue;
                MetaRule from = metCalls[i];
                afCalls[i].ActionData = from.ActionData;
                // VTank's "ret": what Return goes back to. Empty there means the calling state.
                afCalls[i].CallReturnState = string.IsNullOrWhiteSpace(from.CallReturnState) ? state : from.CallReturnState;
                filled.Add(from.ActionData + " (return " + afCalls[i].CallReturnState + ")");
                result.Filled++;
            }
            if (filled.Count > 0)
            {
                result.States++;
                result.Details.Add($"{state}: {string.Join(", ", filled)}");
            }
        }
        result.Ok = true;
        return result;
    }

    /// <summary>The backup copy's path: "&lt;name&gt;.af.bak-yyyyMMdd-HHmmss" next to it.</summary>
    public static string BackupPath(string afPath, DateTime now)
        => afPath + ".bak-" + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
}

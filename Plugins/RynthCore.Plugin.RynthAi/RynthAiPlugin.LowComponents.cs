using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Recall When Low On Components (2026-10-14; off by default): with the macro running, tapers
/// (all kinds together) at or under Low Tapers At, or any one kind of scarab except Diamond at or
/// under Low Scarabs At, stop the macro and recall to the lifestone, saying which ran low. Lucy died
/// that day with every heal spell refused for components, so the bot goes home before that.
///
/// A kind only counts once it has been seen in the pack this session: a character that never
/// carries a Mana Scarab (or no tapers at all, a pure fighter) isn't "low" on it; one that runs a
/// kind out entirely (0, gone from the pack) still is. Checked every LowCompsCheckMs; it fires once
/// and re-arms when the macro is started again.
/// </summary>
public sealed partial class RynthAiPlugin
{
    private const long LowCompsCheckMs = 5_000;
    private long _lowCompsCheckedAt;
    private bool _lowCompsFired;
    private bool _lowCompsWasRunning;
    // Component kinds seen in the pack this session ("taper" for all tapers together, else the scarab's name).
    private readonly HashSet<string> _compsSeen = new(StringComparer.OrdinalIgnoreCase);

    internal const string TaperKey = "tapers";

    /// <summary>Every tick with the safety checks; does the work every LowCompsCheckMs.</summary>
    private void CheckLowComponents(LegacyUiSettings s)
    {
        if (s.IsMacroRunning && !_lowCompsWasRunning) _lowCompsFired = false;   // a new run re-arms it
        _lowCompsWasRunning = s.IsMacroRunning;
        if (!s.LowCompsRecall || !s.IsMacroRunning || _lowCompsFired || _objectCache == null) return;
        long now = Environment.TickCount64;
        if (now - _lowCompsCheckedAt < LowCompsCheckMs) return;
        _lowCompsCheckedAt = now;

        var counts = CountComponents(_objectCache.GetInventory());
        string? low = LowComponents(counts, _compsSeen, s.LowCompsTaperMin, s.LowCompsScarabMin);
        if (low == null) return;

        _lowCompsFired = true;
        HandleMacroRunCommand("stop", Array.Empty<string>());
        bool recalled = Host.HasInvokeChatParser;
        if (recalled) Host.InvokeChatParser("/lifestone");
        string line = $"Low on components ({low}): macro stopped" + (recalled ? ", recalling to the lifestone" : "") +
                      " (Recall When Low On Components is on).";
        ChatLine("[RynthAi] " + line);
        Log("[RynthAi] " + line);
    }

    /// <summary>Tapers (every kind together, under <see cref="TaperKey"/>) and each scarab kind but Diamond, by stack size.</summary>
    internal static Dictionary<string, int> CountComponents(IEnumerable<WorldObject> inventory)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var wo in inventory)
        {
            string name = wo?.Name ?? "";
            string? key = name.EndsWith(" Taper", StringComparison.OrdinalIgnoreCase) ? TaperKey
                : name.EndsWith(" Scarab", StringComparison.OrdinalIgnoreCase)
                  && !name.StartsWith("Diamond", StringComparison.OrdinalIgnoreCase) ? name
                : null;
            if (key == null) continue;
            int n = Math.Max(1, wo!.Values(LongValueKey.StackCount, 1));
            counts[key] = counts.TryGetValue(key, out int c) ? c + n : n;
        }
        return counts;
    }

    /// <summary>
    /// "tapers 9, Lead Scarab 2" for the kinds at or under their line, or null. Kinds in
    /// <paramref name="counts"/> join <paramref name="seen"/>; a seen kind missing now counts as 0.
    /// A line of 0 or less is off for that kind.
    /// </summary>
    internal static string? LowComponents(Dictionary<string, int> counts, HashSet<string> seen, int taperMin, int scarabMin)
    {
        foreach (var k in counts.Keys) seen.Add(k);
        var low = new List<string>();
        foreach (string kind in seen)
        {
            int have = counts.TryGetValue(kind, out int c) ? c : 0;
            bool taper = kind.Equals(TaperKey, StringComparison.OrdinalIgnoreCase);
            int line = taper ? taperMin : scarabMin;
            if (line > 0 && have <= line) low.Add($"{(taper ? "tapers" : kind)} {have}");
        }
        if (low.Count == 0) return null;
        low.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", low);
    }
}

// ============================================================================
//  RynthSuite - Combat/CombatManager.PassedOver.cs
//  Why a higher-priority monster was not the target (R-20261031-201214-8be7).
//
//  Target selection only ever sees the scan's candidates: monsters inside
//  Monster Range that are attackable, alive, not blacklisted and in line of
//  sight. A Priority 5 monster standing behind a tree, or a few metres past
//  Monster Range, is not a candidate, so a Priority 1 monster beside it is
//  fought; from the chair that looks like the priority being ignored. The
//  report could not tell which: the [ScanTele] counts said two monsters in
//  range were LOS-blocked, not which ones or their priority.
//
//  Now the lock/switch line names the best monster it passed over and why
//  ("[passed over 0x.. 'X' priority 5, 3.1 m: line of sight blocked]"), and
//  while the target is fought, a higher-priority monster in range that is not
//  a candidate is logged once per reason. Log only: selection is unchanged.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager
{
    /// <summary>A monster inside Monster Range the last scan left out, and why.</summary>
    private struct PassedOverMonster
    {
        public int Id;
        public double Distance;
        public string Why;
    }

    // Refilled by every scan (ScanNearbyTargets): in-range monsters it left out for a reason
    // that can hide a monster worth fighting. Not the dead (just killed, health 0) or the
    // Priority 0 ones (ignored by the rule, so never above anything).
    private readonly List<PassedOverMonster> _passedOver = new();

    // Refilled by every scan: monsters up to PassedOverBeyondRangeM past Monster Range (not the
    // engaged one), id and distance only. A higher priority among them is not a candidate only
    // because of the Monster Range setting, which a player can't tell from the chair (the owner,
    // 2026-11-01: "might have just been the attack distance being set too low").
    private readonly List<PassedOverMonster> _beyondRange = new();

    // Per lock: the monsters already logged as passed over, with the reason logged, so a
    // monster held behind a wall is one line, not one per tick. Emptied by every lock.
    private readonly Dictionary<int, string> _passedOverLogged = new();

    /// <summary>How far past Monster Range the lock line still looks for a higher priority.</summary>
    internal const double PassedOverBeyondRangeM = 15.0;

    internal const string PassedOverLos = "line of sight blocked";
    internal const string PassedOverBlacklisted = "blacklisted";
    internal const string PassedOverNeverAttack = "on the never-attack list";
    internal const string PassedOverGhost = "a ghost (the server doesn't have it)";
    internal const string PassedOverEnvironment = "left for hitting the environment";
    internal const string PassedOverNotAttackable = "not attackable";

    /// <summary>Tests: the LOS verdict per monster instead of the ray test (true = blocked).</summary>
    internal Func<int, bool>? LosBlockedOverride { get; set; }

    private void NotePassedOver(int id, double distance, string why)
        => _passedOver.Add(new PassedOverMonster { Id = id, Distance = distance, Why = why });

    /// <summary>
    /// The best monster of a higher Priority than <paramref name="priority"/> that was not a
    /// candidate, as "0x.. 'name' priority P, D m: why", or null when there is none. In range
    /// and left out by the scan first (the highest priority, then the closest); else the
    /// closest higher priority up to <see cref="PassedOverBeyondRangeM"/> past Monster Range.
    /// The second half walks the landscape, so it is for the lock line only (a lock is rare).
    /// </summary>
    internal string? DescribePassedOver(int priority, int pickedId, bool lookBeyondRange)
        => DescribePassedOver(priority, pickedId, lookBeyondRange, out _);

    private string? DescribePassedOver(int priority, int pickedId, bool lookBeyondRange, out int namedId)
    {
        namedId = 0;
        if (TryBestPassedOver(priority, pickedId, out var po, out int poPriority))
        {
            namedId = po.Id;
            return $"0x{po.Id:X8} '{_worldFilter[po.Id]?.Name}' priority {poPriority}, {po.Distance:0.0} m: {po.Why}";
        }
        if (!lookBeyondRange) return null;

        int playerId = (int)_playerId;
        if (playerId == 0) return null;
        double range = Math.Max(1.0, _settings.MonsterRange);
        int bestId = 0, bestP = 0;
        double bestDist = double.MaxValue;
        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.Id == playerId || wo.Id == pickedId) continue;
            if ((int)wo.ObjectClass != (int)AcObjectClass.Monster || IsSpellProjectileName(wo.Name)) continue;
            double d = _worldFilter.Distance(playerId, wo.Id);
            if (d <= range || d > range + PassedOverBeyondRangeM || d >= bestDist) continue;
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f || _recentlyKilled.ContainsKey(wo.Id)) continue;
            int p = PriorityOf(wo.Id);
            if (p <= priority) continue;
            bestId = wo.Id; bestP = p; bestDist = d;
        }
        namedId = bestId;
        return bestId == 0 ? null
            : $"0x{bestId:X8} '{_worldFilter[bestId]?.Name}' priority {bestP}, {bestDist:0.0} m: beyond Monster Range ({range:0.#} m)";
    }

    private bool TryBestPassedOver(int priority, int skipId, out PassedOverMonster best, out int bestPriority)
    {
        best = default;
        bestPriority = int.MinValue;
        for (int i = 0; i < _passedOver.Count; i++)
        {
            var po = _passedOver[i];
            if (po.Id == skipId) continue;
            int p = PriorityOf(po.Id);
            if (p <= priority) continue;
            if (p > bestPriority || (p == bestPriority && po.Distance < best.Distance))
            {
                best = po;
                bestPriority = p;
            }
        }
        return bestPriority != int.MinValue;
    }

    /// <summary>
    /// While a target is fought: a monster in range of a higher Priority than the target that
    /// the scan left out is logged, once per monster and reason for this lock. Selection
    /// already switches to such a monster the moment it is a candidate; this says why it isn't.
    /// </summary>
    internal void NotePassedOverWhileEngaged()
    {
        if (_lockedTargetId == 0) return;
        NoteBeyondRangeWhileEngaged();
        if (_passedOver.Count == 0) return;
        for (int i = 0; i < _passedOver.Count; i++)
        {
            var po = _passedOver[i];
            if (po.Id == _lockedTargetId) continue;
            int p = PriorityOf(po.Id);
            if (p <= _lockedPriority) continue;
            if (_passedOverLogged.TryGetValue(po.Id, out string? was) && was == po.Why) continue;
            if (_passedOverLogged.Count >= 64) _passedOverLogged.Clear();
            _passedOverLogged[po.Id] = po.Why;
            _host.Log($"[CombatTarget] passed over 0x{po.Id:X8} '{_worldFilter[po.Id]?.Name}' priority {p}, {po.Distance:0.0} m: {po.Why}; " +
                      $"the target stays 0x{_lockedTargetId:X8} '{_worldFilter[_lockedTargetId]?.Name}' priority {_lockedPriority}");
        }
    }

    /// <summary>The reason logged for a higher priority that is only past Monster Range.</summary>
    internal string BeyondRangeWhy() =>
        $"beyond Monster Range ({Math.Max(1.0, _settings.MonsterRange):0.#} m), the Monster Range setting";

    /// <summary>
    /// While a target is fought: a monster of a higher Priority just past Monster Range is logged
    /// once per monster for this lock. Selection is unchanged (only monsters inside Monster Range
    /// are candidates); this says the setting is why.
    /// </summary>
    private void NoteBeyondRangeWhileEngaged()
    {
        if (_beyondRange.Count == 0) return;
        string why = BeyondRangeWhy();
        for (int i = 0; i < _beyondRange.Count; i++)
        {
            var po = _beyondRange[i];
            if (po.Id == _lockedTargetId) continue;
            if (_passedOverLogged.TryGetValue(po.Id, out string? was) && was == why) continue;
            if (_recentlyKilled.ContainsKey(po.Id) || _worldFilter.GetHealthRatio(po.Id) == 0f) continue;
            int p = PriorityOf(po.Id);
            if (p <= _lockedPriority) continue;
            if (_passedOverLogged.Count >= 64) _passedOverLogged.Clear();
            _passedOverLogged[po.Id] = why;
            _host.Log($"[CombatTarget] passed over 0x{po.Id:X8} '{_worldFilter[po.Id]?.Name}' priority {p}, {po.Distance:0.0} m: {why}; " +
                      $"the target stays 0x{_lockedTargetId:X8} '{_worldFilter[_lockedTargetId]?.Name}' priority {_lockedPriority}");
        }
    }

    /// <summary>
    /// For /ra why: the best higher-priority monster the current target was picked over (left out
    /// by the scan, or just past Monster Range) and why, or null when there is none or no target.
    /// </summary>
    internal string? DescribeHigherPriorityPassedOver()
    {
        if (_lockedTargetId == 0) return null;
        return DescribePassedOver(_lockedPriority, _lockedTargetId, lookBeyondRange: true);
    }

    /// <summary>A new lock: the lock line names what it passed over; the per-lock log starts over.</summary>
    private string PassedOverNoteForLock(int bestId, int bestPriority)
    {
        _passedOverLogged.Clear();
        string? po = DescribePassedOver(bestPriority, bestId, lookBeyondRange: true, out int namedId);
        if (po == null) return "";
        // Named on the lock line: not logged again by NoteBeyondRangeWhileEngaged.
        if (namedId != 0) _passedOverLogged[namedId] = BeyondRangeWhy();
        // The monster named here isn't logged again by NotePassedOverWhileEngaged for the same reason.
        if (TryBestPassedOver(bestPriority, bestId, out var inRange, out _))
            _passedOverLogged[inRange.Id] = inRange.Why;
        return $" [passed over {po}]";
    }
}

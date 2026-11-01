// ============================================================================
//  RynthSuite - Combat/CombatManager.TargetLock.cs
//  Target lock (VTank's "Target lock" hotkey, /ra targetlock): the macro attacks
//  the monster the player selected, ahead of the Targeting setting, the rule
//  Priority tiers (Priority 0 "ignore" included) and the never-attack name list.
//
//  The selected monster must be attackable and alive. It joins the scan like the
//  engaged target (out to the disengage distance); LOS and the failure blacklist
//  still apply, so a monster behind a wall waits until it is in sight. While it
//  isn't in the scan the normal selection runs. The lock ends when it dies or
//  turns into a corpse, gets blacklisted (unreachable), is out of the scan for
//  ForcedTargetLostMs, or the player clears it (/ra targetlock off, or the same
//  monster again).
//
//  "Fight the monster I select" (CombatManager.SelectedTarget.cs) sets the same lock from the
//  player's own in-game selection; _forcedBySelection tells the two apart (it ends quietly on
//  the kill, the player's next monster selection moves it, a deselect clears it, and turning
//  the setting off releases it).
// ============================================================================

using System;
using System.Threading;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager
{
    /// <summary>Out of the scan this long (never in reach, or gone): the lock ends.</summary>
    internal const double ForcedTargetLostMs = 30_000;

    private int _forcedTargetId;
    // Pump-thread state: when the current forced target was first and last seen.
    private int _forcedSeenFor;
    private int _forcedSetSeq;     // bumped on every lock (any thread); the pump compares _forcedSeenSeq
    private int _forcedSeenSeq;
    private DateTime _forcedSince = DateTime.MinValue;
    private DateTime _forcedLastInScan = DateTime.MinValue;

    /// <summary>The monster the player locked combat onto (0 = none). Any thread.</summary>
    public int ForcedTargetId => Volatile.Read(ref _forcedTargetId);

    /// <summary>Why <paramref name="id"/> can't be locked onto, or null when it can.</summary>
    internal string? ForcedTargetRefusal(int id)
    {
        if (id == 0) return "nothing is selected";
        if (unchecked((uint)id) == _playerId) return "that's you";
        var wo = _worldFilter[id];
        if (wo == null) return "it isn't known yet (try again in a moment)";
        if ((int)wo.ObjectClass != (int)AcObjectClass.Monster || IsSpellProjectileName(wo.Name)) return $"{wo.Name} isn't a monster";
        if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(unchecked((uint)id))) return $"{wo.Name} can't be attacked";
        if (_worldFilter.GetHealthRatio(id) == 0f || _recentlyKilled.ContainsKey(id)) return $"{wo.Name} is dead";
        return null;
    }

    /// <summary>Locks combat onto <paramref name="id"/> (checked with <see cref="ForcedTargetRefusal"/> first).
    /// A /ra targetlock lock: the player's later selections don't move it.</summary>
    internal void SetForcedTarget(int id)
    {
        _forcedBySelection = false;
        Interlocked.Increment(ref _forcedSetSeq);
        Volatile.Write(ref _forcedTargetId, id);
    }

    /// <summary>Ends the target lock (the monster stays the fight target until normal selection moves on).</summary>
    internal void ClearForcedTarget()
    {
        Volatile.Write(ref _forcedTargetId, 0);
        _forcedBySelection = false;
    }

    /// <summary>
    /// HandleCombatTrigger, before the normal selection: true when the forced target holds the
    /// tick (locked now, or kept while it blinks out of the scan).
    /// </summary>
    private bool TryLockForcedTarget()
    {
        int fid = ForcedTargetId;
        if (fid == 0) return false;
        DateTime now = TargetClock();
        bool bySelection = _forcedBySelection;
        int seq = Volatile.Read(ref _forcedSetSeq);
        // A new lock, or the same monster locked again (selected, deselected, selected): its clock restarts.
        if (_forcedSeenFor != fid || _forcedSeenSeq != seq)
        {
            _forcedSeenFor = fid;
            _forcedSeenSeq = seq;
            // A selected monster's 30 s count from the click, not from the first tick that saw it.
            DateTime selectedAt = SelectedAt;
            _forcedSince = bySelection && selectedAt != DateTime.MinValue ? selectedAt : now;
            _forcedLastInScan = DateTime.MinValue;
        }

        string? end = bySelection && !_settings.FightSelectedTarget ? SelectedSettingOff
            : ForcedTargetEnded(fid, now);
        if (end != null)
        {
            // _forcedBySelection is left as is: every lock sets it before its id, and with the id 0 it means nothing.
            Interlocked.CompareExchange(ref _forcedTargetId, 0, fid);
            string name = _worldFilter[fid]?.Name ?? $"0x{(uint)fid:X8}";
            if (bySelection)
            {
                _host.Log($"[CombatTarget] fight-selected: 0x{(uint)fid:X8} '{name}' ended: {end}");
                // The kill is the normal end (and the setting off was the player's own doing): no chat line.
                if (end is not ("killed" or "it died" or SelectedSettingOff))
                    _host.WriteToChat($"[RynthAi] Stopped fighting the monster you selected ({name}): {end}.", 1);
                return false;
            }
            _host.Log($"[CombatTarget] target lock on 0x{(uint)fid:X8} '{name}' ended: {end}");
            _host.WriteToChat($"[RynthAi] Target lock on {name} ended: {end}.", 1);
            return false;
        }

        foreach (var c in _scannedTargets)
        {
            if (c.Id != fid) continue;
            _forcedLastInScan = now;
            int priority = PriorityOf(fid);
            if (_lockedTargetId == fid)
            {
                activeTargetId = fid;
                _lockedPriority = priority;
                _lockedLastSeen = c;
                _lockedLastSeenInScan = now;
                return true;
            }
            if (bySelection)
                LockTarget(c, priority, $"fight the monster I select: selected by the player, {DescribeTargetPos(fid)}",
                    _lockedTargetId != 0 ? "the player selected another monster" : null);
            else
                LockTarget(c, priority, $"target lock: picked by the player, {DescribeTargetPos(fid)}",
                    _lockedTargetId != 0 ? "target lock" : null);
            return true;
        }

        // Locked and briefly out of the scan: kept within the scan grace (the normal path would
        // drop a Priority 0 monster at once).
        return _lockedTargetId == fid && _lockedLastSeenInScan != DateTime.MinValue
            && (now - _lockedLastSeenInScan).TotalMilliseconds <= TARGET_SCAN_GRACE_MS;
    }

    private string? ForcedTargetEnded(int fid, DateTime now)
    {
        if (_recentlyKilled.ContainsKey(fid)) return "killed";
        var wo = _worldFilter[fid];
        if (wo != null && (int)wo.ObjectClass != (int)AcObjectClass.Monster) return "it died";
        if (wo != null && _worldFilter.GetHealthRatio(fid) == 0f) return "it died";
        if (_blacklistManager.IsBlacklisted(fid)) return "it can't be reached (blacklisted)";
        DateTime seen = _forcedLastInScan != DateTime.MinValue ? _forcedLastInScan : _forcedSince;
        if ((now - seen).TotalMilliseconds > ForcedTargetLostMs)
            return _forcedLastInScan == DateTime.MinValue ? "never in reach (out of range or out of sight)" : "out of reach";
        return null;
    }
}

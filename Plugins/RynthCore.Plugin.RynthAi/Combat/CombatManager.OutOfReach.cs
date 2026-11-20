// ============================================================================
//  RynthSuite - Combat/CombatManager.OutOfReach.cs
//  A target past the attack's reach (2026-11-19, R-20261119-155301-121a, 2026.11.15.3).
//
//  Back from a death, a caster held an Olthoi Hive Warrior 88 m off: every Incantation of Force
//  Arc at it came back UseDone 0x550 (ACE's MissileOutOfRange, the client's "Out of Range!")
//  about 60 ms after the send, and combat cast again at once: 1,027 casts since the last kill,
//  about 12 a second, for well over a minute. The no-damage count never got there, because a cast
//  sent inside the last one's 500 ms refusal window restarts that window, and the target was in the
//  scan because Monster Range reached past the spell's own range. The server's answer says it
//  plainly: from here, this attack can't reach it.
//
//  Now one such answer drops the target and leaves it out of the scan while it is no nearer than
//  OutOfReachCloserM inside the distance it was refused at, for OutOfReachSkipSec at most. Said
//  once in chat per target. The hunt still walks toward it (it doesn't read this), so closing in
//  brings it back.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager
{
    /// <summary>ACE's MissileOutOfRange, as a UseDone error ("Out of Range!"): the attack can't reach the target from here.</summary>
    internal const uint MissileOutOfRangeErr = 0x550;
    /// <summary>A target refused as out of range stays out of the scan at most this long.</summary>
    internal const double OutOfReachSkipSec = 60;
    /// <summary>...and only while it is no nearer than this inside the distance it was refused at (meters).</summary>
    internal const double OutOfReachCloserM = 3;

    private readonly Dictionary<int, (double Distance, DateTime Until)> _outOfReach = new();
    private int _outOfReachSaidId;

    /// <summary>The server answered an attack at <paramref name="target"/> MissileOutOfRange.</summary>
    private void NoteOutOfReach(int target)
    {
        double dist = _worldFilter.Distance(unchecked((int)_playerId), target);
        if (dist >= double.MaxValue) return;   // no positions: nothing to measure "nearer" against
        DateTime now = RynthClock.Now;
        if (_outOfReach.Count >= 64) PruneOutOfReach(now);
        _outOfReach[target] = (dist, now.AddSeconds(OutOfReachSkipSec));
        string name = _worldFilter[target]?.Name ?? $"0x{(uint)target:X8}";
        string why = $"out of range at {dist:0} m (the server answered 'Out of Range!', UseDone 0x550): " +
                     $"left until it is within {Math.Max(0, dist - OutOfReachCloserM):0} m, {OutOfReachSkipSec:0} s at most";
        _host.Log($"[CombatTarget] 0x{(uint)target:X8} '{name}' {why}");
        if (_outOfReachSaidId != target)
        {
            _outOfReachSaidId = target;
            _host.WriteToChat($"[RynthAi] {name} is out of range at {dist:0} yd: fighting others until it comes closer" +
                              (dist <= _settings.MonsterRange ? $" (Monster Range {_settings.MonsterRange:0} reaches past the attack)." : "."), 1);
        }
        if (target == activeTargetId) DropTarget(why);
    }

    /// <summary>The scan leaves <paramref name="id"/> out: refused as out of range, and no nearer yet.</summary>
    private bool IsOutOfReach(int id, double dist)
    {
        if (_outOfReach.Count == 0 || !_outOfReach.TryGetValue(id, out var e)) return false;
        if (RynthClock.Now < e.Until && dist > e.Distance - OutOfReachCloserM) return true;
        _outOfReach.Remove(id);
        return false;
    }

    private readonly List<int> _outOfReachPrune = new();
    private void PruneOutOfReach(DateTime now)
    {
        _outOfReachPrune.Clear();
        foreach (var kv in _outOfReach)
            if (now >= kv.Value.Until) _outOfReachPrune.Add(kv.Key);
        foreach (int id in _outOfReachPrune) _outOfReach.Remove(id);
    }
}

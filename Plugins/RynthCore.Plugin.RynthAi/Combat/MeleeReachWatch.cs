using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

// A melee target the server can't walk us to (2026-11-06, Olthoi beside a wall, flight recorder
// 19:09-19:12): each attack came back as a MoveToObject to a mob 4.5 m off with a wall between,
// so the character ran on the spot against the wall and never swung, while the mob's own reach
// went through and hit him about once a second. The stall ladder then took him to peace and took
// his weapon off, which costs spells and can't help: nothing is wrong with the client.
//
// The rule: the step-1 re-issue has had its 3 s to walk (at 3 m/s that closes 9 m) and the target
// is no closer and not next to us. That target is left for a while, longer each time.
//
// A wedged client also doesn't close on anything, and the ladder is what recovers that. So the
// rule gives way once MaxStreak targets in a row failed with no swing in between.
internal sealed class MeleeReachWatch
{
    /// <summary>At or under this we are next to it: a stall here is not about walking.</summary>
    public const double NextToM = 2.0;
    /// <summary>The re-issue must close at least this much for the walk to count as working.</summary>
    public const double MustCloseM = 0.75;
    public const int FirstSkipSec = 60;
    public const int MaxSkipSec = 300;
    public const int MaxStreak = 5;

    private readonly Dictionary<int, int> _failures = new();
    private int _reissueTarget;
    private double _reissueDist;
    private int _streak;
    private DateTime _lastFailureAt = DateTime.MinValue;

    /// <summary>Step 1 went out: the walk is measured from here.</summary>
    public void NoteReissue(int targetId, double dist)
    {
        _reissueTarget = targetId;
        _reissueDist = dist;
    }

    /// <summary>At step 2: true when the target can't be reached.</summary>
    public bool CantReach(int targetId, double dist, DateTime lastSwingEvidenceAt)
    {
        if (lastSwingEvidenceAt > _lastFailureAt) _streak = 0;   // something swung since: not a wedge
        if (targetId == 0 || targetId != _reissueTarget) return false;
        if (dist <= NextToM) return false;
        if (dist <= _reissueDist - MustCloseM) return false;
        return _streak < MaxStreak;
    }

    /// <summary>Records the failure. Returns how long to leave the target: 60 s, doubling to 5 min.</summary>
    public int NoteFailure(int targetId, DateTime now)
    {
        _streak++;
        _lastFailureAt = now;
        _reissueTarget = 0;
        _failures.TryGetValue(targetId, out int n);
        _failures[targetId] = n + 1;
        long secs = (long)FirstSkipSec << Math.Min(n, 8);
        return (int)Math.Min(secs, MaxSkipSec);
    }

    /// <summary>It took a hit or died: it can be reached after all, or its id is free again.</summary>
    public void Forget(int targetId) => _failures.Remove(targetId);
}

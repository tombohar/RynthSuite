using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Which of a monster rule's debuffs (Imperil, the vulns, Fester, Yield, Broadside, Gravity
/// Well, the Void ones, custom ones) are on which target, and when each is due again. A landed debuff with a
/// known duration is renewed a little before it wears off (2026-10-20: they were cast once
/// per target, so a long fight with a high-HP monster lost them after 4-5 minutes). One with
/// no known duration, or given up on after <see cref="MaxFailures"/> fizzles or resists, is
/// not cast at that target again until the fight moves to another one.
/// Timers live across target switches (a monster fought again keeps them) and go when they
/// fall due or the monster does. CombatManager's thread only; no host calls.
/// </summary>
internal sealed class DebuffTimers
{
    public const int MaxFailures = 3;
    /// <summary>Renew this long before the end, at most (a fifth of a short debuff's length).</summary>
    public const double MaxLeadSeconds = 5.0;
    private const int MaxTimers = 256;

    private readonly Dictionary<(int Target, string Key), DateTime> _renewAt = new();
    private readonly HashSet<(int Target, string Key)> _done = new();
    private readonly Dictionary<(int Target, string Key), int> _failures = new();

    /// <summary>On the target and not due yet, or not to be cast at it again.</summary>
    public bool IsOn(int target, string key, DateTime now) =>
        _done.Contains((target, key)) || (_renewAt.TryGetValue((target, key), out DateTime at) && now < at);

    /// <summary>When a landed debuff is due again; null when it has no timer.</summary>
    public DateTime? RenewAt(int target, string key) =>
        _renewAt.TryGetValue((target, key), out DateTime at) ? at : null;

    /// <summary>
    /// It landed (or no result line came back in time, taken as landed). With a duration it is
    /// due again <see cref="Lead"/> before it wears off; without one, never on this target.
    /// </summary>
    public void Landed(int target, string key, double durationSeconds, DateTime now)
    {
        _failures.Remove((target, key));   // a renewal gets its own tries
        if (durationSeconds <= 0)
        {
            _done.Add((target, key));
            return;
        }
        if (_renewAt.Count >= MaxTimers && !_renewAt.ContainsKey((target, key))) Prune(now);
        _renewAt[(target, key)] = now.AddSeconds(durationSeconds - Lead(durationSeconds));
    }

    /// <summary>How long before a debuff of this length runs out it is cast again.</summary>
    public static double Lead(double durationSeconds) => Math.Min(MaxLeadSeconds, durationSeconds * 0.2);

    /// <summary>A fizzle, resist or refusal. True when that was the last try (given up on this target).</summary>
    public bool Failed(int target, string key)
    {
        _failures.TryGetValue((target, key), out int n);
        _failures[(target, key)] = ++n;
        if (n < MaxFailures) return false;
        _done.Add((target, key));
        return true;
    }

    public int Failures(int target, string key) => _failures.TryGetValue((target, key), out int n) ? n : 0;

    /// <summary>The fight moved to another target: the once-only and given-up marks start over, timers stay.</summary>
    public void NewTarget(DateTime now)
    {
        _done.Clear();
        _failures.Clear();
        Prune(now);
    }

    /// <summary>The monster is gone (dead or out of view): its timers too.</summary>
    public void Forget(int target)
    {
        if (_renewAt.Count == 0) return;
        List<(int, string)>? drop = null;
        foreach (var k in _renewAt.Keys)
            if (k.Target == target) (drop ??= new()).Add(k);
        if (drop != null) foreach (var k in drop) _renewAt.Remove(k);
    }

    public int TimerCount => _renewAt.Count;

    private void Prune(DateTime now)
    {
        if (_renewAt.Count == 0) return;
        List<(int, string)>? drop = null;
        foreach (var kv in _renewAt)
            if (kv.Value <= now) (drop ??= new()).Add(kv.Key);
        if (drop != null) foreach (var k in drop) _renewAt.Remove(k);
        // Still full of live timers (hundreds of monsters debuffed at once): drop the soonest due.
        while (_renewAt.Count >= MaxTimers)
        {
            (int, string) soonest = default;
            DateTime best = DateTime.MaxValue;
            foreach (var kv in _renewAt)
                if (kv.Value < best) { best = kv.Value; soonest = kv.Key; }
            _renewAt.Remove(soonest);
        }
    }
}

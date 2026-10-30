using System;
using System.Text;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Measures how long combat takes from locking a target to sending the first attack or cast at
/// it, and what it waited on, so a player's own log answers "why didn't it attack right away".
///
/// CombatManager calls <see cref="OnLock"/> when it locks a new target, <see cref="OnTick"/> once
/// per combat tick with the reason that tick didn't attack (the time since the previous tick is
/// charged to that reason), and <see cref="OnAttack"/> when the first attack or cast goes out.
/// OnAttack returns one line per engagement, at most one per <see cref="MinLineGapMs"/> (the
/// skipped ones are counted into the next line):
///
///   [CombatLatency] lock->attack 180 ms (stance 0, face 60, busy 0, cast 120, interval 0, other 0) Magic 'Olthoi Nymph', kill->attack 210 ms
///
/// Pure: the caller passes the clock. Pump thread only.
/// </summary>
internal sealed class CombatLatencyTracker
{
    /// <summary>What a tick waited on. Index into the bucket array.</summary>
    public enum Wait
    {
        /// <summary>Weapon or stance not ready (EquipWeaponAndSetStance said wait).</summary>
        Stance,
        /// <summary>Turning to face, or the settle after letting go of a held turn.</summary>
        Face,
        /// <summary>The client busy count (an action still in progress).</summary>
        Busy,
        /// <summary>Magic gates: the previous cast resolving, the cast gesture, the equip gate.</summary>
        Cast,
        /// <summary>The attack/cast interval since the previous attack command.</summary>
        Interval,
        /// <summary>Anything else (stall watchdog, scan grace, distance, no ammo).</summary>
        Other,
    }

    public const double MinLineGapMs = 1000;
    private const int WaitCount = 6;

    private readonly double[] _waits = new double[WaitCount];
    private int _targetId;
    private double _lockAtMs = double.NaN;
    private double _lastTickMs = double.NaN;
    private double _killAtMs = double.NaN;
    private double _lastLineAtMs = double.NegativeInfinity;
    private int _suppressed;

    /// <summary>True between a lock and its first attack.</summary>
    public bool Pending => !double.IsNaN(_lockAtMs);

    /// <summary>A new target was locked. <paramref name="killAtMs"/> is when the previous target's
    /// kill was noticed, or NaN when this lock didn't follow a kill.</summary>
    public void OnLock(int targetId, double nowMs, double killAtMs = double.NaN)
    {
        _targetId = targetId;
        _lockAtMs = nowMs;
        _lastTickMs = nowMs;
        _killAtMs = killAtMs;
        Array.Clear(_waits);
    }

    /// <summary>The lock was dropped before any attack went out.</summary>
    public void OnDrop()
    {
        _lockAtMs = double.NaN;
        _lastTickMs = double.NaN;
    }

    /// <summary>A combat tick that did not attack: charge the time since the previous tick to
    /// <paramref name="wait"/>.</summary>
    public void OnTick(int targetId, double nowMs, Wait wait)
    {
        if (!Pending) return;
        if (targetId != _targetId) { OnDrop(); return; }
        double dt = nowMs - _lastTickMs;
        if (dt > 0) _waits[(int)wait] += dt;
        _lastTickMs = nowMs;
    }

    /// <summary>The first attack or cast went out. Returns the log line, or null when nothing was
    /// pending for this target or the line is throttled.</summary>
    public string? OnAttack(int targetId, double nowMs, string mode, string? targetName)
    {
        if (!Pending || targetId != _targetId) return null;
        // The tick that attacked: whatever it waited on before the attack call was the
        // interval/other bookkeeping of that same tick, so its slice goes to Other.
        double dt = nowMs - _lastTickMs;
        if (dt > 0) _waits[(int)Wait.Other] += dt;
        double total = nowMs - _lockAtMs;
        double killToAttack = double.IsNaN(_killAtMs) ? double.NaN : nowMs - _killAtMs;
        _lockAtMs = double.NaN;
        _lastTickMs = double.NaN;

        if (nowMs - _lastLineAtMs < MinLineGapMs)
        {
            _suppressed++;
            return null;
        }
        _lastLineAtMs = nowMs;

        var sb = new StringBuilder(160);
        sb.Append("[CombatLatency] lock->attack ").Append(Ms(total)).Append(" ms (")
          .Append("stance ").Append(Ms(_waits[(int)Wait.Stance]))
          .Append(", face ").Append(Ms(_waits[(int)Wait.Face]))
          .Append(", busy ").Append(Ms(_waits[(int)Wait.Busy]))
          .Append(", cast ").Append(Ms(_waits[(int)Wait.Cast]))
          .Append(", interval ").Append(Ms(_waits[(int)Wait.Interval]))
          .Append(", other ").Append(Ms(_waits[(int)Wait.Other]))
          .Append(") ").Append(mode);
        if (!string.IsNullOrEmpty(targetName)) sb.Append(" '").Append(targetName).Append('\'');
        if (!double.IsNaN(killToAttack)) sb.Append(", kill->attack ").Append(Ms(killToAttack)).Append(" ms");
        if (_suppressed > 0) { sb.Append(" (+").Append(_suppressed).Append(" engagements not logged)"); _suppressed = 0; }
        return sb.ToString();
    }

    /// <summary>The time charged to one wait so far (tests and diagnostics).</summary>
    public double Waited(Wait wait) => _waits[(int)wait];

    private static long Ms(double v) => (long)Math.Round(Math.Max(0, v));
}

using System;
using System.Collections.Generic;

namespace RynthNav.Routing;

/// <summary>
/// The goto's heading servo, host-free (RynthNavPlugin.Turn.cs feeds it the player's heading and
/// sends what it asks for; RynthNav.RouteTests walks it along made-up paths).
///
/// The heading is slewed toward the wanted one at a capped rate; a big turn is made standing
/// (autorun off) and the run resumes once lined up. Until 11-01 it stopped to turn for anything
/// over 20 degrees and sent a turn every tick (~45 a second): every path corner and every shift
/// of the aim stopped the run, a "bounces left and right with a lot of stopping" walk (11-01, 862
/// turns and 21 stops in 107 s). Now: running, corrections up to <see cref="StopTurnAngleDeg"/> are
/// made on the move, small ones (<see cref="RunDeadZoneDeg"/>) are left alone, and a turn goes
/// out at most every <see cref="MinTurnIntervalMs"/>.
/// </summary>
internal sealed class WalkSteer
{
    public double TurnRateDegPerSec = 270.0;
    public double DeadZoneDeg = 4.0;          // standing: closer than this is lined up
    public double RunDeadZoneDeg = 6.0;       // running: closer than this isn't worth a turn
    public double StopTurnAngleDeg = 50.0;    // farther off than this: stop and turn standing
    public double ResumeTurnAngleDeg = 20.0;  // ...and run again once this close
    public long MinTurnIntervalMs = 50;       // at most 20 turns a second

    public bool TurnInPlace { get; private set; }
    private long _lastTurnMs;

    /// <summary>The servo as it was before 11-01 (for comparison in the tests).</summary>
    public static WalkSteer Legacy() => new()
    { RunDeadZoneDeg = 4.0, StopTurnAngleDeg = 20.0, ResumeTurnAngleDeg = 10.0, MinTurnIntervalMs = 0 };

    public void Reset() { TurnInPlace = false; _lastTurnMs = 0; _lastCallMs = 0; _held = false; }

    /// <summary>Signed smallest difference a - b, in -180..180.</summary>
    public static double Diff(double a, double b)
    {
        double e = (a - b) % 360.0;
        if (e > 180.0) e -= 360.0; else if (e < -180.0) e += 360.0;
        return e;
    }

    public static double Norm(double deg) => ((deg % 360.0) + 360.0) % 360.0;

    /// <summary>
    /// One tick: wanted and current heading (0 = north, clockwise). Returns the heading to turn to
    /// now, or NaN for none; <paramref name="run"/> is whether to run.
    /// </summary>
    public double Step(double desiredDeg, double currentDeg, long nowMs, out bool run)
    {
        double error = Diff(Norm(desiredDeg), Norm(currentDeg));
        double abs = Math.Abs(error);

        // Run gate with hysteresis: stop to turn when far off, run again once close.
        if (TurnInPlace) { if (abs <= ResumeTurnAngleDeg) TurnInPlace = false; }
        else if (abs > StopTurnAngleDeg) TurnInPlace = true;
        run = !TurnInPlace;

        // The slew covers the time since the last call, or since the last turn when the calls in
        // between wanted one and were held back by the rate limit (as before 11-01 with no limit).
        long since = _held ? nowMs - _lastTurnMs : nowMs - _lastCallMs;
        bool first = _lastCallMs == 0;
        _lastCallMs = nowMs;
        _held = false;
        double dead = TurnInPlace ? DeadZoneDeg : RunDeadZoneDeg;
        if (abs <= dead) return double.NaN;
        if (_lastTurnMs != 0 && nowMs - _lastTurnMs < MinTurnIntervalMs) { _held = true; return double.NaN; }
        double dt = first ? 33.0 : Math.Clamp(since, 10, 200);
        _lastTurnMs = nowMs;
        double maxStep = TurnRateDegPerSec * dt / 1000.0;
        return Norm(currentDeg + Math.Clamp(error, -maxStep, maxStep));
    }

    private long _lastCallMs;
    private bool _held;

    /// <summary>
    /// Which way to step round something the walk ran into: away from <paramref name="headingDeg"/>
    /// on <paramref name="side"/> (+1 clockwise, -1 counter-clockwise) first, the first heading the
    /// navmesh shows open ground along (<paramref name="open"/>), else straight back. Before 11-01 it
    /// was always 120 degrees off, alternating sides, open or not (into the bank of the river beside
    /// a bridge, then back into the bridge).
    /// </summary>
    public static double UnstickHeading(double headingDeg, int side, Func<double, bool>? open)
    {
        side = side >= 0 ? 1 : -1;
        foreach (double off in new[] { 100.0, 130.0, 70.0, 160.0 })
            foreach (int s in new[] { side, -side })
            {
                double h = Norm(headingDeg + s * off);
                if (open == null || open(h)) return h;
            }
        return Norm(headingDeg + 180.0);
    }
}

/// <summary>
/// Which path corner the walk heads for (host-free). A corner is passed when the walk is within
/// <c>cornerReach</c> of it, or, from <c>lookahead</c> out, as soon as the corner after it can be
/// walked to in a straight line (<c>clear</c>: the navmesh's raycast, which keeps the agent radius
/// from every wall, and no portal circle on the way). Before 11-01 only the first: each corner was
/// run to within 1.5 m, overshot at run speed and turned back to.
/// </summary>
internal static class PathFollow
{
    public static int Advance(IReadOnlyList<(double X, double Y)> path, int idx, double x, double y,
                              double cornerReach, double arrival, double lookahead,
                              Func<double, double, double, double, bool>? clear)
    {
        while (idx < path.Count)
        {
            bool last = idx >= path.Count - 1;
            double d = Dist(path[idx].X, path[idx].Y, x, y);
            if (d < (last ? arrival : cornerReach)) { idx++; continue; }
            if (!last && clear != null && d < lookahead && clear(x, y, path[idx + 1].X, path[idx + 1].Y)) { idx++; continue; }
            break;
        }
        return idx;
    }

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
}

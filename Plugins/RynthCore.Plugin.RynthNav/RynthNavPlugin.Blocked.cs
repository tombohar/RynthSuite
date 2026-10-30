using System;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace RynthCore.Plugin.RynthNav;

// Running into a wall: the walk is running but the character isn't moving. The navmesh is baked
// with a 2 m agent radius (RynthNav.Baker), which seals gaps under ~4 m and leaves some ground in
// little islands (Shoushi's plaza, where the Town Network drops you, 10-08); where the plan runs
// out the walk heads straight for its goal for a moment (BlindPushTicks), and that, or a wall the
// mesh doesn't show, used to mean running into it until the 30 s progress watchdog. Now: blocked
// for StuckWindowMs = turn away at an angle (alternating sides), plan again, and after
// StuckMaxTries on one leg without progress stop and say where.
public sealed partial class RynthNavPlugin
{
    private const long StuckWindowMs = 1200;       // running this long...
    private const double StuckMoveUnits = 0.6;     // ...and moved less than this = blocked (a run covers 4+ u/s)
    private const long UnstickMs = 1600;           // then turn away from it and run this long (~0.5 s of it turning)
    private const double UnstickAngleDeg = 120.0;  // away from where it was heading, off to one side
    private const int StuckMaxTries = 4;           // blocked this often on a leg without progress = stop

    private bool _ranLastTick;                     // the walk asked to run last tick (not standing, not turning in place)
    private double _lastDriveDeg;
    private long _stuckWinMs, _unstickUntilMs;
    private double _stuckWx, _stuckWy, _unstickDeg;
    private int _stuckTries, _stuckSide = 1;

    /// <summary>DriveTo for the walk's own steering: remembers that it ran and which way.</summary>
    private void WalkDrive(double deg)
    {
        DriveTo(deg);
        _lastDriveDeg = deg;
        _ranLastTick = !_turnInPlace;
    }

    /// <summary>
    /// Nothing between here and (<paramref name="tx"/>, <paramref name="ty"/>) as far as the navmesh
    /// shows: every metre of the line, bar 2 m at each end (we and a portal stand off the eroded
    /// mesh), is within 1.5 m of walkable ground. A wall leaves a gap of twice the agent radius plus
    /// its thickness, over 4 m, so its middle fails. No query (no tiles): true, as before.
    /// </summary>
    private bool StraightOnMesh(double tx, double ty)
    {
        if (_query == null) return true;
        var filter = new DtQueryDefaultFilter();
        double dx = tx - _wx, dy = ty - _wy, len = Math.Sqrt(dx * dx + dy * dy);
        for (double s = 2.0; s < len - 2.0; s += 1.0)
        {
            var pt = new RcVec3f((float)(_wx + dx * s / len), (float)_wz, (float)(_wy + dy * s / len));
            _query.FindNearestPoly(pt, new RcVec3f(1.5f, 4, 1.5f), filter, out long r, out _, out _);
            if (r == 0) return false;
        }
        return true;
    }

    /// <summary>A new leg (or goto): nothing blocked yet.</summary>
    private void ResetBlocked()
    {
        _ranLastTick = false;
        _stuckWinMs = 0; _unstickUntilMs = 0; _stuckTries = 0;
    }

    /// <summary>
    /// Once per walking tick, before steering. True when it drove this tick (stepping round
    /// something) or finished the goto; the caller returns.
    /// </summary>
    private bool StepBlocked()
    {
        long now = NowMs;
        bool ran = _ranLastTick;
        _ranLastTick = false;
        if (_unstickUntilMs > now)
        {
            DriveTo(_unstickDeg);
            lock (_gate) _status = $"blocked: stepping round it ({_stuckTries}/{StuckMaxTries})";
            return true;
        }
        if (_unstickUntilMs != 0)
        {
            // Stepped aside: plan again from here (and allow one more push where the plan ends).
            _unstickUntilMs = 0;
            lock (_gotoGate) _gotoPath = null;
            _blindTicks = 0; _replanTick = 0;
            _stuckWinMs = 0;
            return false;
        }
        if (!ran || _stuckWinMs == 0) { _stuckWinMs = now; _stuckWx = _wx; _stuckWy = _wy; return false; }
        if (now - _stuckWinMs < StuckWindowMs) return false;
        double moved = Math.Sqrt((_wx - _stuckWx) * (_wx - _stuckWx) + (_wy - _stuckWy) * (_wy - _stuckWy));
        _stuckWinMs = now; _stuckWx = _wx; _stuckWy = _wy;
        if (moved >= StuckMoveUnits) return false;

        string at = NavCoords.Fmt(NavCoords.NsFromWorld(_wy), NavCoords.EwFromWorld(_wx));
        if (++_stuckTries > StuckMaxTries)
        {
            string why; lock (_gate) why = _status;
            FinishGoto($"stopped: blocked at {at} ({StuckMaxTries} tries to get round it; something in the way the map doesn't show) ({why})");
            return true;
        }
        _stuckSide = -_stuckSide;
        _unstickDeg = ((_lastDriveDeg + 180.0 - _stuckSide * (180.0 - UnstickAngleDeg)) % 360.0 + 360.0) % 360.0;
        _unstickUntilMs = now + UnstickMs;
        Host.Log($"[RynthNav] walk: blocked at {at} heading {_lastDriveDeg:F0} (moved {moved:F1}u in {StuckWindowMs / 1000.0:F1} s); "
               + $"turning to {_unstickDeg:F0} and planning again ({_stuckTries}/{StuckMaxTries})");
        DriveTo(_unstickDeg);
        lock (_gate) _status = $"blocked: stepping round it ({_stuckTries}/{StuckMaxTries})";
        return true;
    }
}

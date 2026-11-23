using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// How long this character's trips take, measured as they go (11-19, wave 2): its run speed, a portal's
/// time at the portal, a recall's cast, and portal space. The planner costs teleports with them and the
/// trip's time estimate uses them (TripTimes). Each starts at TripTimes' defaults and moves toward what
/// is measured (a weighted average, a few trips to settle); a reading far outside what a trip can take
/// (a recall that fizzled for 40 s, a portal backed off from five times) is left out. Kept for the
/// session: a new character starts from the defaults again. Host-free (the route tests drive it).
/// </summary>
internal sealed class TripClock
{
    /// <summary>A new reading's weight in the average.</summary>
    public const double Weight = 0.3;
    /// <summary>A run speed sample: running this long without a stop or a sharp turn.</summary>
    public const long RunWindowMs = 2000;
    /// <summary>Run speed: the upper middle (this quantile) of the last samples, since walls and slopes only slow a run.</summary>
    public const double RunQuantile = 0.75;
    public const int RunSamples = 15;

    private readonly TripTimes _t = new();
    private readonly List<double> _runs = new();
    private bool _runOn;
    private long _runSinceMs;
    private double _runLastWx, _runLastWy, _runPath;

    public int PortalReadings { get; private set; }
    public int RecallReadings { get; private set; }
    public int SpaceReadings { get; private set; }
    public int RunReadings => _runs.Count;

    /// <summary>The times to plan with now (a copy: a plan keeps the times it was made with).</summary>
    public TripTimes Times => _t.Copy();

    /// <summary>Back to the defaults (a new character).</summary>
    public void Reset()
    {
        TripTimes d = new();
        _t.RunSpeed = d.RunSpeed; _t.PortalSeconds = d.PortalSeconds; _t.SpaceSeconds = d.SpaceSeconds;
        _t.SpellCastSeconds = d.SpellCastSeconds; _t.CommandCastSeconds = d.CommandCastSeconds;
        _runs.Clear(); _runOn = false;
        PortalReadings = RecallReadings = SpaceReadings = 0;
    }

    /// <summary>
    /// Every walking tick: where you are and whether the walk is running straight on (autorun on, not
    /// turning hard). A stop, a turn, a jump of a tick (a teleport) or a slow reading starts the window again.
    /// </summary>
    public void RunTick(long nowMs, double wx, double wy, bool runningStraight)
    {
        if (!runningStraight) { _runOn = false; return; }
        if (!_runOn)
        {
            _runOn = true; _runSinceMs = nowMs; _runLastWx = wx; _runLastWy = wy; _runPath = 0;
            return;
        }
        double step = Math.Sqrt((wx - _runLastWx) * (wx - _runLastWx) + (wy - _runLastWy) * (wy - _runLastWy));
        _runLastWx = wx; _runLastWy = wy;
        if (step > 5) { _runOn = false; return; }   // no run covers that in a tick
        _runPath += step;
        long dt = nowMs - _runSinceMs;
        if (dt < RunWindowMs) return;
        double speed = _runPath / (dt / 1000.0);
        _runSinceMs = nowMs; _runPath = 0;
        if (speed < 4 || speed > 40) return;   // blocked, or not a run
        _runs.Add(speed);
        if (_runs.Count > RunSamples) _runs.RemoveAt(0);
        if (_runs.Count >= 3)
        {
            var sorted = new List<double>(_runs);
            sorted.Sort();
            _t.RunSpeed = sorted[Math.Min(sorted.Count - 1, (int)(RunQuantile * sorted.Count))];
        }
    }

    /// <summary>A portal step: from reaching its entrance (PortalArriveUnits out) to the jump.</summary>
    public void Portal(double seconds, double approachUnits)
    {
        double s = seconds - approachUnits / _t.RunSpeed;
        if (s < 0.5 || s > 30) return;
        _t.PortalSeconds += Weight * (s - _t.PortalSeconds);
        PortalReadings++;
    }

    /// <summary>A recall step: from its start (the stance, a wand, the cast) to the jump.</summary>
    public void Recall(double seconds, bool spell)
    {
        if (seconds < 0.5 || seconds > (spell ? 20 : 40)) return;
        if (spell) _t.SpellCastSeconds += Weight * (seconds - _t.SpellCastSeconds);
        else _t.CommandCastSeconds += Weight * (seconds - _t.CommandCastSeconds);
        RecallReadings++;
    }

    /// <summary>Portal space: from the jump to standing (the walker's settle), when portal space was seen.</summary>
    public void Space(double seconds)
    {
        if (seconds < 1 || seconds > 40) return;
        _t.SpaceSeconds += Weight * (seconds - _t.SpaceSeconds);
        SpaceReadings++;
    }

    /// <summary>One line for the log and /rnav times.</summary>
    public string Describe()
        => $"running {_t.RunSpeed:F1} u/s ({RunReadings} sample(s)), a portal {_t.PortalSeconds:F1} s ({PortalReadings}), "
         + $"a recall spell {_t.SpellCastSeconds:F1} s, a command recall {_t.CommandCastSeconds:F1} s ({RecallReadings}), "
         + $"portal space {_t.SpaceSeconds:F1} s ({SpaceReadings})";
}

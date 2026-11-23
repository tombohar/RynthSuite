using System;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// How long a trip takes (11-19, wave 2). The planner costs teleports by the time they take (TripTimes),
// with this character's own times as they're measured (TripClock): its run speed while a goto runs
// straight on, each portal step from its entrance to the jump, each recall step from its start to the
// jump, and portal space from the jump to standing. The trip's time shows when it's planned ("walking to
// X via 3 teleports, about 4 min"), in the route view ("secs") and, while walking, as the time left
// ("eta", RynthNavFace). /rnav times says what has been measured. Tick thread.
public sealed partial class RynthNavPlugin
{
    private readonly TripClock _clock = new();
    private string _clockChar = "";
    /// <summary>The times the last plan was made with.</summary>
    private TripTimes _planTimes = new();
    /// <summary>The goto's plan: its cost (units) and its times (NaN = no trip).</summary>
    private double _tripEst = double.NaN;
    private TripTimes _tripTimes = new();
    /// <summary>When the current portal step reached its entrance (0 = not at one).</summary>
    private long _portalStepMs;
    /// <summary>The panel's Info line (status JSON "info.times"): built on the tick, read on any thread.</summary>
    private volatile string _clockLine = "";
    private long _clockLineMs;

    /// <summary>The Info line, every few seconds. Tick thread.</summary>
    private void RefreshClockLine()
    {
        long now = NowMs;
        if (now - _clockLineMs < 5000 && _clockLine.Length > 0) return;
        _clockLineMs = now;
        _clockLine = _clock.Describe();
    }

    /// <summary>The times to plan with: this character's (a new character starts from the defaults).</summary>
    private TripTimes PlanTimes()
    {
        if (_charKey.Length > 0 && !_charKey.Equals(_clockChar, StringComparison.Ordinal))
        {
            _clockChar = _charKey;
            _clock.Reset();
        }
        _planTimes = _clock.Times;
        return _planTimes;
    }

    /// <summary>"about 4 min" for a plan's cost (units) with the times it was made with.</summary>
    private static string AboutTime(TripTimes t, double est) => double.IsFinite(est) ? TripTimes.About(t.Seconds(est)) : "";

    private void ClockPortal()
    {
        if (_portalStepMs == 0) return;
        _clock.Portal((NowMs - _portalStepMs) / 1000.0, PortalArriveUnits);
        _portalStepMs = 0;
        Host.Log($"[RynthNav] trip times: {_clock.Describe()}");
    }

    private void ClockRecall(RecallOption o)
    {
        _clock.Recall((NowMs - _recallStartMs) / 1000.0, o.SpellId != 0);
    }

    private void ClockSpace(long fromJumpMs)
    {
        _clock.Space((NowMs - fromJumpMs) / 1000.0);
    }

    /// <summary>
    /// The trip's time left, in seconds (-1: not known): what is left of the current step, from where
    /// you stand and how long its teleport has been going, plus the plan's cost of the steps after it.
    /// </summary>
    private double TripSecondsLeft(string phase, double left)
    {
        if (!_gotoActive || !double.IsFinite(_tripEst)) return -1;
        TripTimes t = _tripTimes;
        double walk = left >= 0 ? t.Seconds(left) : 0;
        if (_route == null || _route.Count == 0) return left >= 0 ? walk : -1;
        int i = Math.Clamp(_routeIdx, 0, _route.Count - 1);
        RouteStep step = _route[i];
        double rest = step.EstAfter > 0 ? t.Seconds(Math.Max(0, _tripEst - step.EstAfter)) : 0;
        double portal = t.PortalSeconds + t.SpaceSeconds;
        double here;
        if (_settling) here = Math.Max(0.5, t.SpaceSeconds - (NowMs - _settleJumpMs) / 1000.0);
        else if (phase == "recall")
        {
            RecallOption? o = CurrentRecallOption();
            double total = (o == null || o.SpellId != 0 ? t.SpellCastSeconds : t.CommandCastSeconds) + t.SpaceSeconds;
            here = Math.Max(1, total - (NowMs - _recallStartMs) / 1000.0);
        }
        else if (phase == "portal") here = Math.Max(1, portal - (_portalStepMs > 0 ? (NowMs - _portalStepMs) / 1000.0 : 0));
        else if (phase == "town") here = walk + portal;
        else if (step.UseRecall) here = t.SpellCastSeconds + t.SpaceSeconds;
        else here = walk + (step.UsePortal ? portal : 0);
        return here + rest;
    }

    /// <summary>/rnav times: what this character's trips have been measured to take.</summary>
    private void CmdTimes()
    {
        Say($"trip times ({(_clockChar.Length > 0 ? "this character" : "not logged in")}): {_clock.Describe()}. "
          + "A portal costs its time at the portal and in portal space, a recall its cast and portal space, at your run speed.");
    }
}

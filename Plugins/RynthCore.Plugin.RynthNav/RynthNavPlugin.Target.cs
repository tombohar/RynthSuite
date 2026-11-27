using System;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// One target at a time, the newest wins (11-24, a player: "It puts it into the top part of this arrow
// window. The bottom part follows the rynthnav thing though"). The arrow window showed the arrow's
// target (clicked chat coordinates) over the last route planned or walked, which was for somewhere
// else. Now a new target replaces the old one (TargetSwitch): the arrow set elsewhere drops the shown
// route and ends a walk to another place; a plan (/rnav route, Plan) points the arrow along itself;
// /rnav clear (the arrow window's x) forgets all of it. The same place again changes nothing.
// Tick thread: the commands run there (RunCommand).
public sealed partial class RynthNavPlugin
{
    // Where the shown route goes (NaN: none, or unknown), for "the same place" and the status's route goal.
    private double _shownRouteNs = double.NaN, _shownRouteEw = double.NaN;

    private void SetShownRouteGoal(double ns, double ew) { _shownRouteNs = ns; _shownRouteEw = ew; }

    /// <summary>The shown route and the trip, as TargetSwitch reads them.</summary>
    private HeldTarget Held() => new(
        _shownRoute != null, _shownRouteFor, _shownRouteNs, _shownRouteEw,
        _gotoActive, _gotoName,
        _gotoActive ? NavCoords.NsFromWorld(_finalTns) : double.NaN,
        _gotoActive ? NavCoords.EwFromWorld(_finalTew) : double.NaN);

    /// <summary>
    /// Applies what a new target does to the old one: the walk stopped (as Stop does), the shown
    /// route forgotten. Returns what was ended, for the command's reply ("" for nothing).
    /// </summary>
    private string ApplyTargetChange(TargetChange c, in HeldTarget held)
    {
        string ended = "";
        if (c.StopTrip && held.Walking)
        {
            StopTripNow();
            ended = "stopped the walk to " + (held.TripFor.Length > 0 ? held.TripFor : NavCoords.Fmt(held.TripNs, held.TripEw));
        }
        if (c.DropPlan && _shownRoute != null)
        {
            if (ended.Length == 0 && !_shownRouteLive && _shownRouteFor.Length > 0) ended = "dropped the route to " + _shownRouteFor;
            _shownRoute = null;
            _shownRouteFor = "";
            _shownRouteLive = false;
            SetShownRouteGoal(double.NaN, double.NaN);
            _routeVersion++;
            HideMarkers("a new target");
        }
        return ended;
    }

    /// <summary>
    /// Ends the trip at once, as Stop does (ProcessRequests' cancel), but here on the tick so a plan
    /// made right after keeps its markers (the cancel request would hide them later this tick).
    /// </summary>
    private void StopTripNow()
    {
        _goNote = "stopped";
        _gotoActive = false; _paused = false; _route = null; _portalWait = false;
        if (_recallWait) EndRecall();
        ResetHub();
        AutoRun.Stop();
        HideMarkers("a new target");
        lock (_moveGate) { _desiredRun = 0; _turnState = 0; _haltPending = true; }   // ApplyMovement lets go of the keys
    }

    /// <summary>/rnav clear: no target at all. The walk stops, the route goes, the arrow goes off.</summary>
    private void CmdClear()
    {
        HeldTarget held = Held();
        bool hadArrow = _arrow.Active;
        string ended = ApplyTargetChange(TargetSwitch.For(NewTarget.Clear, held), held);
        _arrow.Clear();
        if (!hadArrow && !held.HasPlan && !held.Walking) { Say("clear: no target to clear"); return; }
        Note("target cleared" + (ended.Length > 0 ? ": " + ended : "") + (hadArrow ? "; arrow off" : ""));
    }
}

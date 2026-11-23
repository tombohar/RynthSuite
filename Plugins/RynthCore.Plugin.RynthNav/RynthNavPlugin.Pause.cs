using System;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// Pause and Resume (11-19, a player: "Can auto nav have a pause button? So that if it messes up I can
// still see the current planned route"). Stop ends the trip and the route goes from the World Map;
// Pause stops walking and keeps all of it: the route, the leg, the path, the markers and the arrow.
// While paused you drive (the keys, or the panel's d-pad, which no longer ends the trip), and Resume
// carries on: the same leg from where you stand, or, when you've gone elsewhere meanwhile (through a
// portal by hand, a recall) or it was paused in the middle of a teleport, the trip planned again
// from here, keeping the way chosen (route next) and not counted as one of the trip's replans.
//
// Tick thread: the commands run there (RunCommand), as StepGoto does.
public sealed partial class RynthNavPlugin
{
    /// <summary>Resume farther than this from where Pause was pressed: plan the trip again from here.</summary>
    private const double ResumeReplanUnits = 60.0;

    private volatile bool _paused;
    private double _pauseWx, _pauseWy;
    private uint _pauseCell;
    private bool _pausedMidTeleport;

    private void CmdPause()
    {
        if (!_gotoActive) { Say("pause: not walking anywhere (/rnav go <place> starts a trip)"); return; }
        if (_paused) { Say("pause: already paused. /rnav resume carries on, /rnav stop ends the trip"); return; }
        _paused = true;
        _pauseWx = _wx; _pauseWy = _wy; _pauseCell = _cellId;
        _pausedMidTeleport = _portalWait || _recallWait || _settling || CurrentStepIsHub;
        AutoRun.Stop();   // ApplyMovement stops the rest (its "goto ended" branch) on the next tick
        string line; lock (_gate) line = _goLine;
        lock (_gate) _status = "paused";
        Host.Log($"[RynthNav] paused ({(line.Length > 0 ? line : "walking")}) at {NavCoords.Fmt(NavCoords.NsFromWorld(_wy), NavCoords.EwFromWorld(_wx))}");
        Note("paused: the route stays on the map. /rnav resume carries on, /rnav stop ends the trip");
    }

    private void CmdResume()
    {
        if (!_paused || !_gotoActive)
        {
            _paused = false;
            Say(_gotoActive ? "resume: not paused, still walking" : "resume: no trip to carry on (/rnav go <place>)");
            return;
        }
        _paused = false;
        // Whatever the d-pad was doing while paused stops: the walk drives again.
        lock (_moveGate) { _desiredRun = 0; _turnState = 0; }
        if (_appliedTurn == -1) Host.SetMotion(MotionTurnLeft, false);
        else if (_appliedTurn == 1) Host.SetMotion(MotionTurnRight, false);
        if (_appliedRun == 2) Host.StopMovement(MotionWalkBackward, HoldKeyRun);
        else if (_appliedRun == 1) Host.SetAutoRun(false);
        _appliedRun = 0; _appliedTurn = 0;
        AutoRun.Forget();

        double moved = Math.Sqrt((_wx - _pauseWx) * (_wx - _pauseWx) + (_wy - _pauseWy) * (_wy - _pauseWy));
        bool elsewhere = moved > ResumeReplanUnits || InDungeon(_cellId) != InDungeon(_pauseCell);
        if (_pausedMidTeleport || elsewhere)
        {
            string why = _pausedMidTeleport ? "paused mid-teleport" : $"{NavCoords.FmtDistance(moved)} from where it paused";
            Host.Log($"[RynthNav] resume: {why}: the trip planned again from here");
            Say($"resumed: {why}, so planning the rest of the trip from here");
            string goal = _gotoName, type = _gotoType, enter = _enterName, enterType = _enterType;
            double gns = NavCoords.NsFromWorld(_finalTns), gew = NavCoords.EwFromWorld(_finalTew);
            if (_recallWait) EndRecall();
            // landReplan: keeps this trip's replan count and refusals; the way chosen sticks (PlanRoute).
            StartWalk(goal.Length > 0 ? goal : NavCoords.Fmt(gns, gew), type, gns, gew, enter, enterType, landReplan: true);
            return;
        }
        // The same leg from here: a fresh path and a fresh progress watchdog.
        SetSubGoalToCurrentLeg();
        lock (_gotoGate) { _gotoPath = null; _gotoIdx = 0; }
        _replanTick = 0;
        lock (_gate) _status = "walking";
        Host.Log($"[RynthNav] resumed (leg {_routeIdx + 1} of {_route?.Count ?? 1})");
        Note("resumed: walking on");
    }
}

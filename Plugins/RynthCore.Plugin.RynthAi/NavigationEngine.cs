using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthCore navigation engine — handles route following, waypoint steering, and nav commands.
///
/// Forward motion  : SetAutoRun(true/false) via RynthCoreHost.
/// Steering while running : SetMotion(TurnRight/TurnLeft) — combines with autorun naturally.
/// Large turns (>BigTurnEnter°): stop autorun, TurnToHeading, resume when error < BigTurnExit°.
/// Closest-approach detection prevents circling waypoints.
/// Stuck watchdog fires JumpNonAutonomous(0.5f) every 5 s if < 2 yd moved.
/// </summary>
internal sealed class NavigationEngine
{
    // ── Motion constants ─────────────────────────────────────────────────────
    private const uint MotionTurnRight = 0x6500000D;
    private const uint MotionTurnLeft  = 0x6500000E;

    // ── Timing constants (milliseconds) ─────────────────────────────────────
    private const double NavTickMs       = 33.0;     // ~30 Hz tick rate
    private const double StopDebounceMs  = 300.0;    // debounce before killing autorun
    private const double HeartbeatMs     = 500.0;    // periodic autorun re-assert
    private const double WatchdogMs      = 5000.0;   // stuck check interval
    private const double StuckYd         = 2.0;      // min yards to not be "stuck"
    private const double RecoveryMs      = 1200.0;   // pause after jump recovery
    private const double ActionTimeoutMs = 10000.0;  // max wait for recall/portal
    private const double SettleDelayMs   = 600.0;    // pause before recall/portal action
    private const double PostTeleportMs  = 4000.0;   // settle after teleport

    private readonly RynthCoreHost      _host;
    private readonly LegacyUiSettings _settings;

    // ── Timing ──────────────────────────────────────────────────────────────
    private long _lastNavTick;
    private long _lastHeartbeat;
    private long _stopRequestedAt = long.MaxValue;

    // ── Movement state ───────────────────────────────────────────────────────
    private bool  _isMovingForward;
    private bool  _isTurning;
    private bool  _hasStopped = true;
    private float _lastGoodHeading;
    private bool  _hasGoodHeading;

    // ── Route state ──────────────────────────────────────────────────────────
    private int    _linearDir  = 1;
    private bool   _inPause;
    private long   _pauseUntil;
    private double _prevDist = double.MaxValue;

    // ── Portal/Recall state machine ──────────────────────────────────────────
    private enum PortalState
    {
        None,
        Settling,           // brief pause to let motions stop
        FiringAction,       // send /rs or approach NPC
        WaitingForTeleport, // watching for position change
        PostTeleportSettle  // hammer-stop after teleport
    }
    private PortalState _portalState = PortalState.None;
    private long   _portalStateStart;
    private double _prePortalNS = double.NaN;
    private double _prePortalEW = double.NaN;

    // ── Stuck watchdog ───────────────────────────────────────────────────────
    private double _watchdogNs = double.NaN;
    private double _watchdogEw = double.NaN;
    private long   _watchdogNext;
    private int    _stuckCount;
    private bool   _inRecovery;
    private long   _recoveryUntil;

    // ── Derived thresholds (from settings) ──────────────────────────────────
    // These match the old NavigationManager exactly.
    private double DeadZone     => Math.Max(0.5,  _settings.NavDeadZone);
    private double BigTurnEnter => Math.Max(5.0,  _settings.NavStopTurnAngle);
    private double BigTurnExit  => Math.Max(1.0,  Math.Min(_settings.NavResumeTurnAngle, BigTurnEnter - 1.0));
    private double SweepMult    => Math.Max(1.0,  _settings.NavSweepMult);
    private double ArrivalYards => Math.Max(1.5,  _settings.FollowNavMin);
    private double LookaheadYards => ArrivalYards * 0.1;

    private static long Now => Environment.TickCount64;

    public NavigationEngine(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host     = host;
        _settings = settings;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ══════════════════════════════════════════════════════════════════════════

    public void Tick()
    {
        bool shouldNav = _settings.IsMacroRunning
                      && _settings.EnableNavigation
                      && (_settings.CurrentState == "Idle" || _settings.CurrentState == "Navigating");

        if (!shouldNav)
        {
            if (!_hasStopped)
            {
                ClearTurnMotions();
                if (_stopRequestedAt == long.MaxValue)
                    _stopRequestedAt = Now;

                if (Now - _stopRequestedAt >= (long)StopDebounceMs)
                {
                    _host.SetAutoRun(false);
                    _isMovingForward = false;
                    _isTurning       = false;
                    _hasStopped      = true;
                    ResetPortalState();
                }
            }
            return;
        }

        _stopRequestedAt = long.MaxValue;
        _hasStopped      = false;

        var route = _settings.CurrentRoute;
        if (route == null || route.Points.Count == 0) { StopMovement(); return; }

        _settings.CurrentState = "Navigating";

        // Rate-limit to ~30 Hz
        if (Now - _lastNavTick < (long)NavTickMs) return;
        _lastNavTick = Now;

        int idx = _settings.ActiveNavIndex;
        if (!IndexValid(idx, route)) { HandleRouteEnd(route); return; }

        UpdateWatchdog();
        if (_inRecovery)
        {
            if (Now >= _recoveryUntil) { _inRecovery = false; _settings.NavIsStuck = false; }
            return;
        }

        var pt = route.Points[idx];

        // ── Point-type dispatch ──────────────────────────────────────────────
        switch (pt.Type)
        {
            case NavPointType.Pause:
                HandlePause(pt, route);
                return;
            case NavPointType.Chat:
                HandleChat(pt, route);
                return;
            case NavPointType.Recall:
            case NavPointType.PortalNPC:
                HandlePortalOrRecall(pt, route);
                return;
        }

        // ── Standard coordinate waypoint ─────────────────────────────────────
        _portalState = PortalState.None;
        _inPause     = false;

        if (!TryGetPos(out double ns, out double ew)) return;

        double dNS  = pt.NS - ns;
        double dEW  = pt.EW - ew;
        double dist = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;

        // Arrival check
        if (dist < ArrivalYards)
        {
            _host.Log($"Nav: arrived at [{idx}] dist={dist:F1}yd → advancing");
            _prevDist = double.MaxValue;
            UpdateStatusLine(idx, dist, route, 0.0);
            Advance(route);
            return;
        }

        // Closest-approach detection — prevents circling.
        // Matches old NavigationManager exactly.
        if (_prevDist < ArrivalYards * SweepMult && dist > _prevDist + 0.3)
        {
            _host.Log($"Nav: sweep-pass [{idx}] prev={_prevDist:F1} now={dist:F1}yd → advancing");
            _prevDist = double.MaxValue;
            Advance(route);
            return;
        }
        _prevDist = dist;

        SteerToWaypoint(idx, pt, route, ns, ew, dist);
    }

    public void Stop()
    {
        _inPause         = false;
        _inRecovery      = false;
        _linearDir       = 1;
        _stopRequestedAt = long.MaxValue;
        ResetPortalState();
        _host.SetAutoRun(false);
        ClearTurnMotions();
        _isMovingForward = false;
        _isTurning       = false;
        _hasStopped      = true;
    }

    public void ResetRouteState()
    {
        Stop();
        _stuckCount     = 0;
        _prevDist       = double.MaxValue;
        _watchdogNs     = double.NaN;
        _watchdogEw     = double.NaN;
        _hasGoodHeading = false;
    }

    public int FindNearestWaypoint(NavRouteParser route)
    {
        if (route?.Points == null || route.Points.Count == 0) return 0;
        if (!TryGetPos(out double ns, out double ew)) return 0;

        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < route.Points.Count; i++)
        {
            var pt = route.Points[i];
            if (pt.Type != NavPointType.Point) continue;
            double dNS = pt.NS - ns, dEW = pt.EW - ew;
            double d = Math.Sqrt(dNS * dNS + dEW * dEW);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  STEERING (Tier 0/1)
    //
    //  Faithfully replicates the old NavigationManager steering logic:
    //
    //  Current heading: derived from player pose quaternion (direct SmartBox
    //  memory reads, proven reliable). Avoids the hardcoded-VA GetHeading
    //  which returned garbage values.
    //
    //  Big turns (>BigTurnEnter°): stop running, SNAP heading instantly via
    //  TurnToHeading (equivalent to old _core.Actions.Heading = desiredDeg),
    //  resume when error < BigTurnExit°.
    //
    //  Small corrections while running: SetMotion(TurnRight/TurnLeft) —
    //  combines with autorun naturally (like pressing W+A or W+D).
    //
    //  Near waypoint (< ArrivalYards * SweepMult): clear turns, run straight.
    //  Sweep detection handles the rest.
    // ══════════════════════════════════════════════════════════════════════════

    private int _diagTickCount;

    private void SteerToWaypoint(int idx, NavPoint pt, NavRouteParser route,
                                 double ns, double ew, double dist)
    {
        // ── Lookahead blend ──────────────────────────────────────────────────
        double tNS = pt.NS, tEW = pt.EW;
        if (dist < LookaheadYards)
        {
            int ni = PeekNext(idx, route);
            if (ni >= 0)
            {
                var np = route.Points[ni];
                double t = 1.0 - dist / LookaheadYards;
                tNS = Lerp(pt.NS, np.NS, t);
                tEW = Lerp(pt.EW, np.EW, t);
            }
        }

        // ── Desired heading (0=North, clockwise) ────────────────────────────
        double desiredDeg = Math.Atan2(tEW - ew, tNS - ns) * (180.0 / Math.PI);
        if (desiredDeg < 0) desiredDeg += 360.0;

        // ── Current heading from quaternion (always reliable) ───────────────
        double currentDeg;
        if (TryGetQuaternionHeading(out float qHeading))
        {
            currentDeg       = qHeading;
            _lastGoodHeading = qHeading;
            _hasGoodHeading  = true;
        }
        else if (_hasGoodHeading)
        {
            currentDeg = _lastGoodHeading;
        }
        else
        {
            currentDeg = desiredDeg;
        }

        double error    = NormalizeAngle(desiredDeg - currentDeg);
        double absError = Math.Abs(error);

        UpdateStatusLine(idx, dist, route, error);

        // ── Diagnostic log — every 10th tick for ~200 ticks (~6 seconds) ───
        _diagTickCount++;
        if (_diagTickCount <= 200 && _diagTickCount % 10 == 1)
        {
            string state = _isTurning ? "TURN" : (_isMovingForward ? "RUN" : "STOP");
            _host.Log($"Nav[{_diagTickCount}]: pos=({ns:F6},{ew:F6}) tgt=({pt.NS:F4},{pt.EW:F4}) hdg={currentDeg:F1} des={desiredDeg:F1} err={error:F1} dist={dist:F1}yd [{state}]");
        }

        // ── STATE: Turning in place (big turn while stopped) ────────────────
        // Uses native turn motions for smooth animation instead of instant snap.
        if (_isTurning)
        {
            if (absError <= BigTurnExit)
            {
                // Close enough — stop turning, start running
                _isTurning = false;
                ClearTurnMotions();
                StartForward();
                return;
            }

            // Continue smooth turn in the correct direction
            if (error > 0)
            {
                _host.SetMotion(MotionTurnRight, true);
                _host.SetMotion(MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotion(MotionTurnLeft,  true);
                _host.SetMotion(MotionTurnRight, false);
            }
            return;
        }

        // ── BIG TURN (>BigTurnEnter°): stop and turn smoothly ──────────────
        // Uses SetMotion turn keys so the character rotates with native animation.
        if (absError > BigTurnEnter)
        {
            StopForward();
            if (error > 0)
            {
                _host.SetMotion(MotionTurnRight, true);
                _host.SetMotion(MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotion(MotionTurnLeft,  true);
                _host.SetMotion(MotionTurnRight, false);
            }
            _isTurning = true;
            return;
        }

        // ── Ensure forward motion ───────────────────────────────────────────
        StartForward();

        // ── Near waypoint: stop turning, just run straight ──────────────────
        // Matches old code: dist < ArrivalYards * SweepMult
        bool closeToWaypoint = dist < ArrivalYards * SweepMult;

        // ── SMALL CORRECTION: TurnRight/TurnLeft while running ──────────────
        // These combine with autorun naturally (like pressing W+A or W+D).
        // No heading snap, no motion interrupt.
        if (absError > DeadZone && !closeToWaypoint)
        {
            if (error > 0)
            {
                _host.SetMotion(MotionTurnRight, true);
                _host.SetMotion(MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotion(MotionTurnLeft,  true);
                _host.SetMotion(MotionTurnRight, false);
            }
        }
        else
        {
            ClearTurnMotions();
        }

        // ── Heartbeat: periodically re-assert autorun ───────────────────────
        if (_isMovingForward && Now - _lastHeartbeat > (long)HeartbeatMs)
        {
            _host.SetAutoRun(true);
            _lastHeartbeat = Now;
        }
    }

    /// <summary>
    /// Derives heading from player pose quaternion — direct SmartBox memory
    /// reads, no hardcoded function VAs. Always reliable when the player
    /// object is available.
    /// </summary>
    private bool TryGetQuaternionHeading(out float headingDeg)
    {
        headingDeg = 0;
        if (!_host.TryGetPlayerPose(out _, out _, out _, out _, out float qw, out _, out _, out float qz))
            return false;

        // Physics yaw from quaternion: 0° = North, counterclockwise
        double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        // Convert to: 0° = North, clockwise (negate CCW→CW)
        double heading = (-physYawDeg + 720.0) % 360.0;
        headingDeg = (float)heading;
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ROUTE ADVANCEMENT
    // ══════════════════════════════════════════════════════════════════════════

    private void Advance(NavRouteParser route)
    {
        int oldIdx = _settings.ActiveNavIndex;
        _prevDist    = double.MaxValue;
        _portalState = PortalState.None;
        _diagTickCount = 0; // reset so we get diag for next waypoint

        switch (route.RouteType)
        {
            case NavRouteType.Circular:
                _settings.ActiveNavIndex = (_settings.ActiveNavIndex + 1) % route.Points.Count;
                break;

            case NavRouteType.Linear:
                int n = _settings.ActiveNavIndex + _linearDir;
                if (n < 0 || n >= route.Points.Count)
                {
                    _linearDir = -_linearDir;
                    n          = _settings.ActiveNavIndex + _linearDir;
                }
                _settings.ActiveNavIndex = n;
                break;

            case NavRouteType.Once:
                _settings.ActiveNavIndex++;
                if (_settings.ActiveNavIndex >= route.Points.Count)
                {
                    _settings.EnableNavigation = false;
                    StopMovement();
                }
                break;

            case NavRouteType.Follow:
                _settings.ActiveNavIndex = 0;
                break;
        }

        if (_settings.ActiveNavIndex != oldIdx && IndexValid(_settings.ActiveNavIndex, route))
        {
            var np = route.Points[_settings.ActiveNavIndex];
            _host.Log($"Nav: advance [{oldIdx}]→[{_settings.ActiveNavIndex}] type={np.Type} tgt=({np.NS:F3},{np.EW:F3})");
        }
    }

    private void HandleRouteEnd(NavRouteParser route)
    {
        if (route.RouteType == NavRouteType.Circular)
            _settings.ActiveNavIndex = 0;
        else
            StopMovement();
    }

    private int PeekNext(int cur, NavRouteParser route)
    {
        switch (route.RouteType)
        {
            case NavRouteType.Circular: return (cur + 1) % route.Points.Count;
            case NavRouteType.Linear:
                int n = cur + _linearDir;
                return (n >= 0 && n < route.Points.Count) ? n : -1;
            case NavRouteType.Once:
                return (cur + 1 < route.Points.Count) ? cur + 1 : -1;
            default: return -1;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  POINT-TYPE HANDLERS
    // ══════════════════════════════════════════════════════════════════════════

    private void HandlePause(NavPoint pt, NavRouteParser route)
    {
        StopMovement();
        if (!_inPause)
        {
            _inPause    = true;
            _pauseUntil = Now + (long)pt.PauseTimeMs;
            _settings.NavStatusLine = $"Nav: pausing {pt.PauseTimeMs / 1000.0:F1}s";
        }
        if (Now >= _pauseUntil)
        {
            _inPause = false;
            Advance(route);
        }
    }

    private void HandleChat(NavPoint pt, NavRouteParser route)
    {
        StopMovement();
        if (_portalState == PortalState.None)
        {
            _portalState = PortalState.FiringAction; // use as "fired" flag
            _host.WriteToChat(pt.ChatCommand, 0);
            Advance(route);
        }
    }

    /// <summary>
    /// Full portal/recall state machine — ported from old NavigationManager.ProcessPortalAction.
    /// Simplified for RynthCore: uses /rs chat command instead of CastSpell (no wand equip needed).
    /// PortalNPC uses UseObject if available.
    /// </summary>
    private void HandlePortalOrRecall(NavPoint pt, NavRouteParser route)
    {
        // Global timeout
        if (_portalState != PortalState.None)
        {
            if (Now - _portalStateStart > (long)ActionTimeoutMs + (long)PostTeleportMs)
            {
                _host.Log("Nav: portal/recall global timeout, advancing.");
                ResetPortalState();
                Advance(route);
                return;
            }
        }

        // Initialize — enter Settling state
        if (_portalState == PortalState.None)
        {
            StopMovement();
            _portalState      = PortalState.Settling;
            _portalStateStart = Now;

            // Record pre-action position for teleport detection
            TryGetPos(out _prePortalNS, out _prePortalEW);
            return;
        }

        switch (_portalState)
        {
            case PortalState.Settling:
                // Keep clearing motions until settled
                ClearTurnMotions();
                _host.SetAutoRun(false);
                _isMovingForward = false;

                if (Now - _portalStateStart > (long)SettleDelayMs)
                {
                    _portalState      = PortalState.FiringAction;
                    _portalStateStart = Now;
                }
                break;

            case PortalState.FiringAction:
                // Fire the action ONCE (first 100ms of this state)
                if (Now - _portalStateStart < 100)
                {
                    if (pt.Type == NavPointType.Recall)
                    {
                        _host.WriteToChat($"/rs {pt.SpellId}", 0);
                        _settings.NavStatusLine = $"Nav: recall spell {pt.SpellId}...";
                    }
                    else if (pt.Type == NavPointType.PortalNPC)
                    {
                        _settings.NavStatusLine = $"Nav: portal NPC '{pt.TargetName}'...";
                        // TODO: UseObject by name search when API available
                        _host.Log($"Nav: PortalNPC '{pt.TargetName}' — UseObject not yet available, using /use command.");
                    }
                }

                // After a brief wait, move to watching for teleport
                if (Now - _portalStateStart > 500)
                {
                    _portalState      = PortalState.WaitingForTeleport;
                    _portalStateStart = Now;
                    // Re-record position (more accurate after settle)
                    TryGetPos(out _prePortalNS, out _prePortalEW);
                }
                break;

            case PortalState.WaitingForTeleport:
                // Detect teleport by large position change
                if (TryGetPos(out double ns, out double ew) && !double.IsNaN(_prePortalNS))
                {
                    double dNS = ns - _prePortalNS, dEW = ew - _prePortalEW;
                    double movedYd = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;
                    if (movedYd > 50.0)
                    {
                        _portalState      = PortalState.PostTeleportSettle;
                        _portalStateStart = Now;
                        _settings.NavStatusLine = "Nav: teleported, settling...";
                        return;
                    }
                }

                // Timeout for recall (short) — don't wait forever
                if (pt.Type == NavPointType.Recall && Now - _portalStateStart > (long)ActionTimeoutMs)
                {
                    _host.Log("Nav: recall timed out, advancing.");
                    ResetPortalState();
                    Advance(route);
                }
                break;

            case PortalState.PostTeleportSettle:
                // Hammer-stop to cancel any lingering UseItem walk
                _host.SetAutoRun(false);
                ClearTurnMotions();
                _isMovingForward = false;
                _isTurning       = false;

                if (Now - _portalStateStart > (long)PostTeleportMs)
                {
                    ResetPortalState();
                    Advance(route);
                }
                break;
        }
    }

    private void ResetPortalState()
    {
        _portalState    = PortalState.None;
        _prePortalNS    = double.NaN;
        _prePortalEW    = double.NaN;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  STUCK WATCHDOG
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateWatchdog()
    {
        if (Now < _watchdogNext) return;
        _watchdogNext = Now + (long)WatchdogMs;

        if (!TryGetPos(out double ns, out double ew)) return;

        if (!double.IsNaN(_watchdogNs) && _isMovingForward)
        {
            double dN = ns - _watchdogNs, dE = ew - _watchdogEw;
            double moved = Math.Sqrt(dN * dN + dE * dE) * 240.0;
            if (moved < StuckYd)
            {
                _stuckCount++;
                BeginRecovery();
            }
            else
            {
                _stuckCount = 0;
            }
        }
        _watchdogNs = ns;
        _watchdogEw = ew;
    }

    private void BeginRecovery()
    {
        _inRecovery    = true;
        _recoveryUntil = Now + (long)RecoveryMs;
        _settings.NavIsStuck = true;
        StopMovement();
        _host.JumpNonAutonomous(0.5f);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MOVEMENT HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    private void StartForward()
    {
        if (!_isMovingForward)
        {
            _host.SetAutoRun(true);
            _isMovingForward = true;
        }
    }

    private void StopForward()
    {
        _host.SetAutoRun(false);
        _isMovingForward = false;
    }

    private void ClearTurnMotions()
    {
        _host.SetMotion(MotionTurnRight, false);
        _host.SetMotion(MotionTurnLeft,  false);
    }

    private void StopMovement()
    {
        _isTurning = false;
        StopForward();
        ClearTurnMotions();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  STATUS
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateStatusLine(int idx, double dist, NavRouteParser route, double headingErr)
    {
        if (!_inRecovery) _settings.NavIsStuck = false;

        string modeStr = _isTurning ? " [TURN]" : string.Empty;
        string errStr  = Math.Abs(headingErr) > 0.5 ? $" err={headingErr:+0.0;-0.0}\u00b0" : string.Empty;

        _settings.NavStatusLine = route.Points.Count > 0
            ? $"Nav: {idx + 1}/{route.Points.Count}  {dist:F1}yd{errStr}{modeStr}"
            : "Nav: idle";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  UTILITIES
    // ══════════════════════════════════════════════════════════════════════════

    private bool TryGetPos(out double ns, out double ew)
        => NavCoordinateHelper.TryGetNavCoords(_host, out ns, out ew);

    private static bool IndexValid(int i, NavRouteParser r)
        => r?.Points != null && i >= 0 && i < r.Points.Count;

    private static double NormalizeAngle(double a)
    {
        while (a >  180.0) a -= 360.0;
        while (a < -180.0) a += 360.0;
        return a;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

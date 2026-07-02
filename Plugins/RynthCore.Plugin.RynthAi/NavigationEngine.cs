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
    private const double ActionTimeoutMs = 60000.0;  // max wait for recall/portal (longer so cast can finish)
    private const double SettleDelayMs   = 600.0;    // pause before recall/portal action
    private const double RecallCastRetryMs = 4000.0; // re-issue CastSpell every N ms until teleport
    private const double PortalNpcRetryMs  = 1500.0; // re-search cache for portal NPC every N ms until found (cache classifies on a budget after teleport)

    // Tunable: settle delay after any portal/recall teleport (from settings, in seconds).
    private double PostTeleportMs => Math.Max(0.0, _settings.PostPortalDelaySec) * 1000.0;

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
    private bool  _postTeleport;        // true on the first steer after a teleport — forces big-turn
    private int   _lastTurnDir;         // +1 = right, -1 = left, 0 = none — hysteresis for small corrections (legacy/servo)
    private int   _tier1TurnDir;        // +1 = right, -1 = left, 0 = none — edge-tracking for Tier 1 (CM_Movement) turn commands
    private bool  _trackingPortalSpace; // global portal-space edge detection (independent of HandlePortalOrRecall)
    private double _globalLastNS = double.NaN; // position tracking for teleport detection
    private double _globalLastEW = double.NaN;
    private bool   _globalSettling;            // true during post-teleport settle (global detection)
    private long   _globalSettleStart;

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

    /// <summary>True when the nav engine is executing a portal/recall action and must keep ticking to detect the teleport.</summary>
    public bool IsInPortalAction => _portalState != PortalState.None;
    private double _prePortalNS = double.NaN;
    private double _prePortalEW = double.NaN;
    private uint   _prePortalLb;     // landblock (objCellId>>16) at the cast/use site; 0 = unknown
    private bool _wasInPortalSpace;  // tracks IsPortaling() edge for teleport detection

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
    // VTank navclosestoprange: stop short of a finite (Once) route's final point.
    // Stored as a landblock fraction; ×240 → yards. 0 = off.
    private double CloseStopYards => _settings.NavCloseStopRange > 0f ? _settings.NavCloseStopRange * 240.0 : 0.0;

    // Lookahead: within this distance of a waypoint, blend the aim point toward
    // the next one so corners are cut smoothly. 0 = off (aim straight at each
    // waypoint). Tunable in Advanced ▸ Navigation ▸ Steering.
    private double LookaheadYards => Math.Max(0.0, _settings.NavLookaheadYards);

    // Mode 0 heading servo: cap the heading change we command per tick so the
    // turn is smooth and never overshoots (deadbeat). Floored at 10°/s so a
    // stray 0 from an old config can't freeze turning.
    private double MaxStepDeg => Math.Max(10.0, _settings.NavTurnRateDegPerSec) * (NavTickMs / 1000.0);

    // Mode 1 (Tier 1 / CM_Movement) DoMovement turn-command speed = magnitude of
    // the client's CMotionInterp turn_speed (1.0 = native keyboard turn rate).
    // Treat 0/unset as the default rather than flooring to a crawl.
    private double Tier1TurnSpeed => _settings.NavTier1TurnSpeed > 0f ? _settings.NavTier1TurnSpeed : 3.0;

    // True when the Tier 1 (CM_Movement) actuator should drive steering: the
    // user picked Movement Engine = Tier 1 and the host exposes the CM_Movement
    // events. Mode 0 (heading servo) and an unbuilt Tier 2 fall through to the
    // servo path.
    private bool Tier1Movement => _settings.MovementMode == 1 && _host.HasDoMovement && _host.HasStopMovement;

    private static long Now => Environment.TickCount64;

    private WorldObjectCache? _objectCache;
    private uint _playerId;
    private CombatManager? _combatManager;
    private long _lastRecallCastAt;
    private bool _portalNpcFired;     // true once UseObject was successfully called for a PortalNPC waypoint (prevents canceling the walk-to-NPC with a second UseObject)
    private bool _portalNpcDiagLogged; // one-shot: deep dump of nearest landscape + any portal-named object across all buckets, on the first miss only

    // Reference-tracked so we detect route swaps (e.g., meta EmbedNav) and reset state.
    private NavRouteParser? _lastRoute;

    public NavigationEngine(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host     = host;
        _settings = settings;
    }

    public void SetWorldObjectCache(WorldObjectCache cache) => _objectCache = cache;
    public void SetPlayerId(uint id) => _playerId = id;
    public void SetCombatManager(CombatManager cm) => _combatManager = cm;

    // ══════════════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ══════════════════════════════════════════════════════════════════════════

    // Diagnostic: emit a single line summarizing nav state whenever any of
    // the gating inputs CHANGE. Lets us see (a) when shouldNav flips false,
    // (b) when _inRecovery flips, and (c) when BotAction changes — the three
    // things that silently stop nav mid-route. Rate-limited to one log per
    // distinct state-tuple so the steady-state isn't spammy.
    private string _lastNavStateKey = "";
    private void LogNavStateIfChanged()
    {
        string key = $"macro={_settings.IsMacroRunning} navEnabled={_settings.EnableNavigation} action='{_settings.BotAction}' recovery={_inRecovery} moving={_isMovingForward} turning={_isTurning} idx={_settings.ActiveNavIndex}";
        if (key == _lastNavStateKey) return;
        _lastNavStateKey = key;
        _host.Log($"Nav: state {key}");
    }

    public void Tick()
    {
        // Nav runs independently of meta state — metas define arbitrary state names,
        // and a running meta can fire EmbeddedNavRoute while in any state. Combat
        // pause is handled by CombatManager taking over state; we only gate on
        // the hard combat lock and the "Looting" interlock.
        bool shouldNav = _settings.IsMacroRunning
                      && _settings.EnableNavigation
                      && _settings.BotAction != "Combat"
                      && _settings.BotAction != "Looting";

        LogNavStateIfChanged();

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

        // Fellowship-follow takes priority over route nav while enabled: steer
        // toward the leader's LIVE position instead of a fixed waypoint. Opt-in,
        // so it never affects normal route running. No route required.
        if (_settings.FollowMode && _settings.FollowTargetId != 0)
        {
            if (_settings.BotAction == "Default" || _settings.BotAction == "Navigating")
                _settings.BotAction = "Following";
            FollowTarget();
            return;
        }

        var route = _settings.CurrentRoute;
        if (route == null || route.Points.Count == 0) { StopMovement(); return; }

        // Route swap (e.g., meta EmbedNav replaced CurrentRoute): reset carry-over state
        // so a new route doesn't inherit the old route's pause/portal/stuck progress.
        if (!ReferenceEquals(route, _lastRoute))
        {
            _lastRoute      = route;
            _inPause        = false;
            _prevDist       = double.MaxValue;
            _linearDir      = 1;
            _watchdogNs     = double.NaN;
            _watchdogEw     = double.NaN;
            _stuckCount     = 0;
            _inRecovery     = false;
            _hasGoodHeading = false;
            ResetPortalState();
            _host.Log($"Nav: route swap detected, {route.Points.Count} pts, startIdx={_settings.ActiveNavIndex}");
        }

        // Don't stomp on meta state names — only self-promote from plain "Default".
        if (_settings.BotAction == "Default")
            _settings.BotAction = "Navigating";

        // Rate-limit to ~30 Hz
        if (Now - _lastNavTick < (long)NavTickMs) return;
        _lastNavTick = Now;

        // ── Global teleport detection ────────────────────────────────────────
        // Catches portals used outside of HandlePortalOrRecall (cast portals,
        // manually entered portals, etc). Detects teleport via:
        // 1) IsPortaling edge — entered portal space then exited
        // 2) Position jump — moved > 50 yards between ticks
        // When detected, enter a settle period before resuming nav.
        if (_portalState == PortalState.None)
        {
            // Handle active settle period
            if (_globalSettling)
            {
                _host.SetAutoRun(false);
                ClearTurnMotions();
                if (_host.HasStopCompletely) _host.StopCompletely();
                _isMovingForward = false;
                _isTurning       = false;

                if (Now - _globalSettleStart > (long)PostTeleportMs)
                {
                    _host.Log($"Nav: global post-teleport settle done ({PostTeleportMs:F0}ms)");
                    if (_host.HasStopCompletely) _host.StopCompletely();
                    if (_host.HasForceResetBusyCount) _host.ForceResetBusyCount();
                    if (_combatManager != null) _combatManager.BusyCount = 0;
                    _hasGoodHeading = false;
                    _lastTurnDir    = 0;
                    _postTeleport   = true;
                    _watchdogNs     = double.NaN;
                    _watchdogEw     = double.NaN;
                    _watchdogNext   = Now + (long)WatchdogMs;
                    _prevDist       = double.MaxValue;
                    _globalSettling = false;
                }
                return;
            }

            bool inPS = _host.HasIsPortaling && _host.IsPortaling();
            if (inPS)
            {
                if (!_trackingPortalSpace)
                {
                    _trackingPortalSpace = true;
                    StopMovement();
                }
                return; // don't steer while in portal space
            }

            // Detect teleport: portal-space exit edge OR position jump > 50 yards
            bool portalExited = _trackingPortalSpace && !inPS;
            bool positionJumped = false;
            if (TryGetPos(out double gNS, out double gEW))
            {
                if (!double.IsNaN(_globalLastNS))
                {
                    double dN = gNS - _globalLastNS, dE = gEW - _globalLastEW;
                    double jumpYd = Math.Sqrt(dN * dN + dE * dE) * 240.0;
                    positionJumped = jumpYd > 50.0;
                }
                _globalLastNS = gNS;
                _globalLastEW = gEW;
            }

            if (portalExited || positionJumped)
            {
                _trackingPortalSpace = false;
                _globalSettling      = true;
                _globalSettleStart   = Now;
                StopMovement();
                _host.Log($"Nav: teleport detected (portalExit={portalExited} posJump={positionJumped}), settling {PostTeleportMs:F0}ms...");
                return;
            }
        }

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

        // VTank navclosestoprange: on a finite (Once) route's final point, treat
        // arrival as reached once within the close-stop distance and stop short of
        // the destination instead of walking onto it.
        if (CloseStopYards > 0.0 && route.RouteType == NavRouteType.Once
            && PeekNext(idx, route) < 0 && dist < CloseStopYards)
        {
            _host.Log($"Nav: close-stop at final pt [{idx}] dist={dist:F1}yd ≤ {CloseStopYards:F1}yd");
            StopMovement();
            HandleRouteEnd(route);
            return;
        }

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


    private void SteerToWaypoint(int idx, NavPoint pt, NavRouteParser route,
                                 double ns, double ew, double dist)
    {
        // ── Lookahead blend ──────────────────────────────────────────────────
        double tNS = pt.NS, tEW = pt.EW;
        if (dist < LookaheadYards)
        {
            int ni = PeekNext(idx, route);
            // Only blend toward the NEXT waypoint when it is a real travel target
            // (a Point). Action waypoints (PortalNPC / Recall / Chat / Pause) are
            // never navigated to — they fire in place once the index reaches them —
            // and their stored coordinate is frequently a placeholder far from the
            // actual spot (a PortalNPC whose coord points off "to the abyss" is the
            // recurring case). Blending toward it swung the avatar to face that
            // bogus direction on arrival, right before using the portal.
            if (ni >= 0 && route.Points[ni].Type == NavPointType.Point)
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

        // ── Steering actuator selection ─────────────────────────────────────
        // Mode 1 (Tier 1) uses CM_Movement turn commands; mode 0 uses the
        // heading servo. Only fall back to the legacy bang-bang motion keys when
        // neither actuator is available (stale host without TurnToHeading and
        // not Tier-1-capable).
        if (!Tier1Movement && !_host.HasTurnToHeading)
        {
            SteerToWaypointLegacy(error, absError, dist);
            return;
        }

        // Run-gate hysteresis: when we're far off heading, turn in place (autorun
        // off) so we don't arc wide; once within BigTurnExit, resume running and
        // keep slewing to hug the line. BigTurnEnter/BigTurnExit keep their
        // original meaning — they now gate the servo instead of the turn keys.
        if (_isTurning)
        {
            if (absError <= BigTurnExit)
            {
                _isTurning    = false;
                _postTeleport = false;
                StartForward();
            }
            else if (_isMovingForward)
            {
                StopForward();
            }
        }
        else if (absError > BigTurnEnter || (_postTeleport && absError > BigTurnExit))
        {
            _isTurning = true;
            if (_isMovingForward) StopForward();
        }
        else
        {
            _postTeleport = false;
            StartForward();
        }

        // ── Turn actuator ───────────────────────────────────────────────────
        if (Tier1Movement)
        {
            // Tier 1 (CM_Movement): edge-triggered DoMovement/StopMovement turn.
            // We send the turn command ONCE when the desired direction changes
            // and stop it ONCE when aligned — the server then rotates the body
            // smoothly, instead of toggling a key every tick (the old weave).
            // Forward stays on autorun and combines with the turn (like W+D).
            int want = absError <= DeadZone ? 0 : (error > 0 ? 1 : -1);
            if (want != _tier1TurnDir)
            {
                if (_tier1TurnDir > 0)      _host.StopMovement(MotionTurnRight, 0);
                else if (_tier1TurnDir < 0) _host.StopMovement(MotionTurnLeft,  0);

                if (want > 0)      _host.DoMovement(MotionTurnRight, (float)Tier1TurnSpeed, 0);
                else if (want < 0) _host.DoMovement(MotionTurnLeft,  (float)Tier1TurnSpeed, 0);

                _tier1TurnDir = want;
            }
        }
        else
        {
            // Mode 0 heading servo: command the heading directly toward the
            // target, rate-limited to MaxStepDeg/tick. Because we never command
            // past the target, the turn converges with no overshoot.
            if (absError > DeadZone)
            {
                double step       = Math.Clamp(error, -MaxStepDeg, MaxStepDeg);
                double newHeading = currentDeg + step;
                if (newHeading >= 360.0)     newHeading -= 360.0;
                else if (newHeading <   0.0) newHeading += 360.0;
                _host.TurnToHeading((float)newHeading);
            }
        }

        // ── Heartbeat: periodically re-assert autorun ───────────────────────
        if (_isMovingForward && Now - _lastHeartbeat > (long)HeartbeatMs)
        {
            _host.SetAutoRun(true);
            _lastHeartbeat = Now;
        }
    }

    /// <summary>
    /// Legacy bang-bang turn fallback for engines that don't expose
    /// TurnToHeading. Toggles the native TurnLeft/TurnRight motion keys — the
    /// old steering behaviour, kept only so a stale host still navigates.
    /// </summary>
    private void SteerToWaypointLegacy(double error, double absError, double dist)
    {
        if (_isTurning)
        {
            if (absError <= BigTurnExit)
            {
                _isTurning = false;
                ClearTurnMotions();
                StartForward();
                return;
            }

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

        if (absError > BigTurnEnter || (_postTeleport && absError > DeadZone))
        {
            _postTeleport = false;
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
        _postTeleport = false;

        StartForward();

        bool closeToWaypoint = dist < ArrivalYards * SweepMult;

        if (absError > DeadZone && !closeToWaypoint)
        {
            int wantDir = error > 0 ? 1 : -1;
            if (_lastTurnDir != 0 && _lastTurnDir != wantDir && absError < DeadZone * 2.0)
            {
                // hold current direction until error grows or clearly crosses zero
            }
            else
            {
                _lastTurnDir = wantDir;
            }

            if (_lastTurnDir > 0)
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
            _lastTurnDir = 0;
            ClearTurnMotions();
        }

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
                    route.Points.Clear();   // Clear in-memory points (file on disk unchanged)
                    StopMovement();
                }
                break;

            case NavRouteType.Follow:
                _settings.ActiveNavIndex = 0;
                break;
        }

        // Dense-waypoint skip: high-resolution route files (e.g., from auto-
        // pathfinders that emit 0.03-yard inter-point spacing) leave the
        // player inside ArrivalYards of every consecutive point. Without
        // this, Tick() repeatedly hits the `dist < ArrivalYards` branch and
        // never reaches SteerToWaypoint, so no movement command goes out
        // and the bot "navigates" while standing still.
        //
        // Walk forward (within this route's iteration direction) over any
        // contiguous run of waypoints that are also already inside our
        // arrival radius from the current player position. Only stop on a
        // waypoint we'd actually need to move toward — or a non-Point
        // waypoint (Pause/Chat/Recall/PortalNPC) that needs explicit
        // handling regardless of distance.
        if (_settings.ActiveNavIndex != oldIdx &&
            IndexValid(_settings.ActiveNavIndex, route) &&
            TryGetPos(out double curNs, out double curEw))
        {
            double arrival = ArrivalYards;
            int skipped = 0;
            const int SkipBudget = 4096; // hard cap so we can't loop a circular route forever
            while (skipped < SkipBudget)
            {
                var candidate = route.Points[_settings.ActiveNavIndex];
                if (candidate.Type != NavPointType.Point)
                    break; // never skip a control point (Pause/Chat/Recall/PortalNPC)

                double cNS = candidate.NS - curNs;
                double cEW = candidate.EW - curEw;
                double cDist = Math.Sqrt(cNS * cNS + cEW * cEW) * 240.0;
                if (cDist >= arrival)
                    break; // far enough that SteerToWaypoint has something to do

                int beforeSkip = _settings.ActiveNavIndex;
                AdvanceOneIndex(route);
                if (_settings.ActiveNavIndex == beforeSkip)
                    break; // route ended / cleared / circular wrapped back; stop
                skipped++;
            }

            if (skipped > 0)
                _host.Log($"Nav: skipped {skipped} dense waypoint(s) within {arrival:F1}yd → now on [{_settings.ActiveNavIndex}]");

            // Collinear-waypoint skip: if current target lies nearly on the straight
            // line from the player to the waypoint after it, skip it. Handles straight
            // dungeon corridors and outdoor paths with too many recorded points.
            const double CollinearThreshYards = 2.0;
            const int    ColSkipBudget        = 64;
            int          colSkipped           = 0;
            while (colSkipped < ColSkipBudget && IndexValid(_settings.ActiveNavIndex, route))
            {
                var curr = route.Points[_settings.ActiveNavIndex];
                if (curr.Type != NavPointType.Point) break;

                int ni = PeekNext(_settings.ActiveNavIndex, route);
                if (ni < 0 || ni == _settings.ActiveNavIndex) break;
                var next = route.Points[ni];
                if (next.Type != NavPointType.Point) break;

                // Only skip if next is farther from player than curr (don't skip past a turn)
                double dCurrNS = curr.NS - curNs, dCurrEW = curr.EW - curEw;
                double dNextNS = next.NS - curNs, dNextEW = next.EW - curEw;
                if (dNextNS * dNextNS + dNextEW * dNextEW <= dCurrNS * dCurrNS + dCurrEW * dCurrEW) break;

                if (CrossDistYards(curNs, curEw, curr.NS, curr.EW, next.NS, next.EW) >= CollinearThreshYards) break;

                int beforeCol = _settings.ActiveNavIndex;
                AdvanceOneIndex(route);
                if (_settings.ActiveNavIndex == beforeCol) break;
                colSkipped++;
            }
            if (colSkipped > 0)
                _host.Log($"Nav: skipped {colSkipped} collinear waypoint(s) → now on [{_settings.ActiveNavIndex}]");
        }

        if (_settings.ActiveNavIndex != oldIdx && IndexValid(_settings.ActiveNavIndex, route))
        {
            var np = route.Points[_settings.ActiveNavIndex];
            _host.Log($"Nav: advance [{oldIdx}]→[{_settings.ActiveNavIndex}] type={np.Type} tgt=({np.NS:F3},{np.EW:F3})");
        }
    }

    /// <summary>
    /// Bumps <see cref="LegacyUiSettings.ActiveNavIndex"/> by one in the
    /// current route's iteration direction. Used by the dense-waypoint
    /// skip loop in <see cref="Advance"/>. Mirrors the single-step logic
    /// in the main switch; factored out so the skip loop can call it
    /// without re-clearing _prevDist / _portalState.
    /// </summary>
    private void AdvanceOneIndex(NavRouteParser route)
    {
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
                    route.Points.Clear();
                    StopMovement();
                }
                break;

            case NavRouteType.Follow:
                _settings.ActiveNavIndex = 0;
                break;
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
            string cmd = pt.ChatCommand ?? string.Empty;
            if (cmd.StartsWith("/") && _host.HasInvokeChatParser)
                _host.InvokeChatParser(cmd);
            else
                _host.WriteToChat(cmd, 0);
            Advance(route);
        }
    }

    /// <summary>
    /// Cast a recall spell by ID. Recalls are self-cast — target = player.
    /// Uses CastSpell directly because WriteToChat("/rs N") only *displays* text;
    /// it does not route through the chat parser.
    /// </summary>
    private void FireRecallSpell(NavPoint pt)
    {
        _settings.NavStatusLine = $"Nav: recall spell {pt.SpellId}...";
        if (!_host.HasCastSpell)
        {
            _host.Log($"Nav: CastSpell not available — cannot fire recall {pt.SpellId}");
            return;
        }
        uint target = _playerId != 0 ? _playerId : (uint)_host.GetPlayerId();
        if (target == 0)
        {
            _host.Log($"Nav: no player id — cannot fire recall {pt.SpellId}");
            return;
        }
        _host.CastSpell(target, pt.SpellId);
        _host.Log($"Nav: CastSpell(recall {pt.SpellId}) on player 0x{target:X8}");
    }

    /// <summary>
    /// Find the landscape object whose name matches pt.TargetName (case-insensitive)
    /// and UseObject it. The player has already navigated to the NPC's point,
    /// so the nearest match is the correct one.
    /// Returns true iff UseObject was actually called (caller uses this to gate
    /// retries — a "no match" is retried by the caller until the cache catches up).
    /// </summary>
    private bool FirePortalNpcUse(NavPoint pt)
    {
        _settings.NavStatusLine = $"Nav: portal '{pt.TargetName}'...";
        if (_objectCache == null || string.IsNullOrWhiteSpace(pt.TargetName) || !_host.HasUseObject)
        {
            _host.Log($"Nav: PortalNPC — cache/target/UseObject unavailable for '{pt.TargetName}'");
            return false;
        }

        string target = pt.TargetName.Trim();
        int pid = unchecked((int)(_playerId != 0 ? _playerId : (uint)_host.GetPlayerId()));

        int bestId = 0;
        double bestDist = double.MaxValue;
        int landscapeCount = 0;
        int emptyNameRefreshed = 0;  // landscape items whose empty name was successfully backfilled this pass
        int emptyNameStillBlank = 0; // landscape items whose name was still empty after a forced probe
        int fallbackChecked = 0;     // _byId items scanned in the fallback pass (only when landscape misses)
        int fallbackHits = 0;        // _byId items whose name matched (after distance guard)
        int probeChecked = 0;        // direct ID probes (cache-bypass scan) issued this pass
        int probeNamed = 0;          // probes that returned a non-empty name
        int probeHits = 0;           // probes whose name matched the target
        string fallbackSource = "";  // "landscape", "all-known", or "id-probe" — which pass produced bestId

        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            landscapeCount++;

            // Refresh empty names directly. WorldObjectCache classifies on a
            // per-tick budget and can park objects in _landscape with no name
            // when AC's initial GetObjectName probe races weenie-data load
            // (the [ReclassifyDiag] "stuck Unknown landscape candidate(s)"
            // line is the symptom). Going through the cache's indexer triggers
            // its empty-name patch path — successful lookups write back into
            // _byId so the next pass finds the name already populated.
            string name = wo.Name;
            if (string.IsNullOrEmpty(name))
            {
                var refreshed = _objectCache[wo.Id];
                if (refreshed != null && !string.IsNullOrEmpty(refreshed.Name))
                {
                    name = refreshed.Name;
                    emptyNameRefreshed++;
                }
                else
                {
                    emptyNameStillBlank++;
                }
            }
            if (string.IsNullOrEmpty(name)) continue;

            if (!name.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            double d = pid != 0 ? _objectCache.Distance(pid, wo.Id) : 0.0;
            if (d < bestDist) { bestDist = d; bestId = wo.Id; fallbackSource = "landscape"; }
        }

        // Tier 2 fallback: WorldObjectCache's classification race can put a
        // static landscape object (esp. portal NPCs whose initial
        // GetObjectPosition probe returns no position) into _inventory or
        // leave it in _byId without ever adding to _landscape. When the
        // landscape pass misses, search every known object — gated by
        // distance so we don't match a stale 50,000yd portal entry from a
        // prior landblock.
        const double FallbackMaxDistYd = 250.0;
        if (bestId == 0)
        {
            foreach (var wo in _objectCache.AllKnownObjects())
            {
                fallbackChecked++;
                string name = wo.Name;
                if (string.IsNullOrEmpty(name))
                {
                    var refreshed = _objectCache[wo.Id];
                    if (refreshed != null && !string.IsNullOrEmpty(refreshed.Name))
                        name = refreshed.Name;
                }
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                    name.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                double d = pid != 0 ? _objectCache.Distance(pid, wo.Id) : 0.0;
                if (d > FallbackMaxDistYd) continue; // stale position guard
                fallbackHits++;
                if (d < bestDist) { bestDist = d; bestId = wo.Id; fallbackSource = "all-known"; }
            }
        }

        // Tier 3 fallback: cache-bypass probe of the CURRENT landblock's
        // static-object id range. AC static GUIDs are laid out as
        //   0x70000000 | (landblock << 12) | index
        // so every static object (portals, NPCs, signs) in the player's
        // landblock lives in [base, base+0xFFF]. WorldObjectCache's
        // OnCreateObject hook misses some of these entirely — Town Network
        // portals are the recurring case: the object is visible/clickable
        // in-game but never lands in _byId (confirmed live 2026-06-15: a
        // 'Portal to Town Network' at 0x7F682018 used fine when cached at
        // 12:08, then the same portal was absent from every cache bucket at
        // 14:12 and the bot retried forever). TryGetObjectName reads AC's
        // object table directly, so it finds the portal regardless of hook
        // coverage. Landblock-scoping replaces the old hardcoded
        // 0x70007000-0x700070FF range (that range only covered one town's
        // devices and held creatures, not portals, in the live logs — it
        // never matched the real 0x7Exxxxxx/0x7Fxxxxxx portal ids). Every
        // candidate is in the player's landblock by construction, so a name
        // match is the right object; distance only breaks ties (and a match
        // is kept even when its position can't be read).
        if (bestId == 0 && pid != 0)
        {
            uint lb = CurrentLandblock();
            if (lb != 0)
            {
                uint probeBase = 0x70000000u | (lb << 12);
                for (uint offset = 0; offset <= 0xFFFu; offset++)
                {
                    uint candidateId = probeBase + offset;
                    probeChecked++;
                    if (!_host.TryGetObjectName(candidateId, out string probeName) || string.IsNullOrEmpty(probeName))
                        continue;
                    probeNamed++;
                    if (!probeName.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                        probeName.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    probeHits++;

                    int candidateSid = unchecked((int)candidateId);
                    double d = _objectCache.Distance(pid, candidateSid);
                    if (bestId == 0 || d < bestDist) { bestDist = d; bestId = candidateSid; fallbackSource = "id-probe-lb"; }
                }
            }
        }

        if (bestId == 0)
        {
            // landscapeCount tells us whether the cache is still warming up
            // (low count after a teleport) vs. genuinely missing the object
            // (high count but no name match → route file likely has a typo).
            // fallback{Checked,Hits} report on the all-known-objects rescue.
            _host.Log($"Nav: PortalNPC — no match for '{target}' (landscapeCount={landscapeCount} refreshedNames={emptyNameRefreshed} stillBlank={emptyNameStillBlank} fallbackChecked={fallbackChecked} fallbackHits={fallbackHits} probeChecked={probeChecked} probeNamed={probeNamed} probeHits={probeHits}) — will retry");

            // ONE-SHOT DEEP DUMP on the first miss: closest 8 landscape items
            // (so we can see what the cache *does* think is around the player)
            // and EVERY object across all buckets whose name contains "portal"
            // (in case the portal is real but landed in a different bucket, or
            // has a name we didn't expect). Resets in ResetPortalState.
            if (!_portalNpcDiagLogged)
            {
                _portalNpcDiagLogged = true;
                LogPortalSearchDiag(target, pid);
            }

            return false;
        }

        _host.UseObject((uint)bestId);
        // Publish the resolved object so the marker renderer can draw a ring +
        // line to the portal's real position (the waypoint coord is a placeholder).
        _settings.ActivePortalObjId = (uint)bestId;
        _host.Log($"Nav: UseObject portal '{target}' (dist={bestDist:F1}yd src={fallbackSource}) → 0x{bestId:X8}");
        return true;
    }

    /// <summary>
    /// One-shot diagnostic when FirePortalNpcUse can't find its target. Dumps:
    ///   (a) the 8 closest landscape items by distance, so we can see what the
    ///       cache thinks is around the player; and
    ///   (b) any object across all buckets (landscape, creatures, inventory,
    ///       unknown) whose name contains "portal" — catches the case where
    ///       AC's portal landed in a non-landscape bucket or has a name that
    ///       doesn't include the substring we searched for.
    /// Logs at most ~10 lines. Called once per portal attempt (reset by
    /// ResetPortalState), so log volume stays bounded.
    /// </summary>
    private void LogPortalSearchDiag(string searchTarget, int pid)
    {
        if (_objectCache == null) return;

        // (a) Closest 8 landscape items by distance.
        var landscapeByDist = new System.Collections.Generic.List<(double d, int id, string name)>();
        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            double d = pid != 0 ? _objectCache.Distance(pid, wo.Id) : double.MaxValue;
            landscapeByDist.Add((d, wo.Id, wo.Name ?? "<null>"));
        }
        landscapeByDist.Sort((a, b) => a.d.CompareTo(b.d));
        int take = Math.Min(8, landscapeByDist.Count);
        _host.Log($"Nav: PortalNPC diag — closest {take} landscape obj(s):");
        for (int i = 0; i < take; i++)
        {
            var (d, id, name) = landscapeByDist[i];
            _host.Log($"  [{i}] 0x{id:X8} '{name}' dist={d:F1}yd");
        }

        // (b) Any object with "portal" in name across all buckets. Cap output
        //     so a portal-heavy area can't spam the log.
        int portalHits = 0;
        const int MaxPortalDumpLines = 50;
        foreach (var wo in _objectCache.AllKnownObjects())
        {
            if (string.IsNullOrEmpty(wo.Name)) continue;
            if (wo.Name.IndexOf("portal", StringComparison.OrdinalIgnoreCase) < 0) continue;
            portalHits++;
            if (portalHits > MaxPortalDumpLines) continue;

            double d = pid != 0 ? _objectCache.Distance(pid, wo.Id) : double.MaxValue;
            bool inLandscape = false;
            // Cheap landscape membership check — re-iterate, since the cache
            // doesn't expose a public Contains helper. Only runs once per
            // portal attempt and the inner set is small.
            foreach (var ls in _objectCache.GetLandscapeObjects())
            {
                if (ls.Id == wo.Id) { inLandscape = true; break; }
            }
            _host.Log($"Nav: PortalNPC diag — portal-named: 0x{wo.Id:X8} '{wo.Name}' dist={d:F1}yd inLandscape={(inLandscape ? 1 : 0)}");
        }
        if (portalHits == 0)
        {
            _host.Log($"Nav: PortalNPC diag — NO object across any bucket has 'portal' in its name (searched for '{searchTarget}'). Portal is missing from cache entirely OR named without the word 'portal'.");
        }
        else if (portalHits > MaxPortalDumpLines)
        {
            _host.Log($"Nav: PortalNPC diag — {portalHits} portal-named object(s) total (showed first {MaxPortalDumpLines}).");
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
            _portalState         = PortalState.Settling;
            _portalStateStart    = Now;
            _lastRecallCastAt    = 0;
            _portalNpcFired      = false;
            _portalNpcDiagLogged = false;

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
                    // Re-record position right before we start trying to cast,
                    // so teleport detection is relative to the cast site.
                    TryGetPos(out _prePortalNS, out _prePortalEW);
                    _prePortalLb = CurrentLandblock();
                }
                break;

            case PortalState.FiringAction:
                // Keep our injected turn motions clear while we settle / cast /
                // search for the object — but once a PortalNPC UseObject has
                // fired, STOP clearing so AC's native use-walk can turn the
                // avatar toward the portal smoothly. Clearing every tick after
                // the use was cancelling that auto-walk turn and produced the
                // awkward swing-away-then-enter. (Recall never sets
                // _portalNpcFired, so its cast still gets motions cleared.)
                if (!(pt.Type == NavPointType.PortalNPC && _portalNpcFired))
                    ClearTurnMotions();

                // Teleport detection — two methods, same as meta system:
                // 1) IsPortaling edge: entered portal space then exited = confirmed teleport
                // 2) Position change: moved > 50 yards from pre-portal position
                bool inPortalSpace = _host.HasIsPortaling && _host.IsPortaling();
                if (inPortalSpace)
                    _wasInPortalSpace = true;
                bool portalExited = _wasInPortalSpace && !inPortalSpace;

                bool positionChanged = false;
                if (TryGetPos(out double ns, out double ew) && !double.IsNaN(_prePortalNS))
                {
                    double dNS = ns - _prePortalNS, dEW = ew - _prePortalEW;
                    double movedYd = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;
                    positionChanged = movedYd > 50.0;
                }

                // 3) Landblock change: a portal/recall that crosses a landblock
                //    boundary is a confirmed teleport even when it moves the
                //    player < 50 yards (short-hop dungeon/interior portals).
                bool landblockChanged = false;
                if (_prePortalLb != 0)
                {
                    uint lbNow = CurrentLandblock();
                    landblockChanged = lbNow != 0 && lbNow != _prePortalLb;
                }

                if (portalExited || positionChanged || landblockChanged)
                {
                    int busyNow = _host.HasGetBusyState ? _host.GetBusyState() : -1;
                    _host.Log($"Nav: teleport detected (portalExit={portalExited} posChange={positionChanged} lbChange={landblockChanged}) busyState={busyNow}");
                    _portalState      = PortalState.PostTeleportSettle;
                    _portalStateStart = Now;
                    _settings.NavStatusLine = "Nav: teleported, settling...";
                    return;
                }

                if (pt.Type == NavPointType.Recall)
                {
                    // Ensure magic stance + wand before every cast attempt. Retry the cast
                    // every RecallCastRetryMs so a fizzle or interruption doesn't strand us.
                    bool ready = _combatManager?.EnsureMagicReady() ?? false;
                    if (!ready)
                    {
                        _settings.NavStatusLine = "Nav: wielding wand / entering magic mode...";
                    }
                    else if (_lastRecallCastAt == 0 ||
                             Now - _lastRecallCastAt > (long)RecallCastRetryMs)
                    {
                        FireRecallSpell(pt);
                        _lastRecallCastAt = Now;
                    }
                }
                else if (pt.Type == NavPointType.PortalNPC)
                {
                    // PortalNPC is fire-once *once it actually fires* — UseObject
                    // starts a walk to the NPC and a second call would cancel
                    // it. But if the target isn't in WorldObjectCache._landscape
                    // yet (common right after a teleport — classification runs
                    // on a per-tick budget so a portal at the destination can
                    // take a couple of seconds to land in _landscape), retry
                    // the search every PortalNpcRetryMs until FirePortalNpcUse
                    // returns true. Without retry, the no-match path would set
                    // _lastRecallCastAt and we'd burn the full 60s timeout then
                    // skip the portal — exactly the failure seen at
                    // 12:02:10/18:55:33 in the 2026-05-24 log.
                    if (!_portalNpcFired &&
                        (_lastRecallCastAt == 0 || Now - _lastRecallCastAt > (long)PortalNpcRetryMs))
                    {
                        if (FirePortalNpcUse(pt))
                            _portalNpcFired = true;
                        _lastRecallCastAt = Now;
                    }
                }
                break;

            case PortalState.PostTeleportSettle:
                // Hammer-stop to cancel any lingering UseItem walk and clear
                // the client's internal action queue (prevents stuck hourglass
                // cursor when UseObject is interrupted by portal teleport).
                _host.SetAutoRun(false);
                ClearTurnMotions();
                if (_host.HasStopCompletely) _host.StopCompletely();
                _isMovingForward = false;
                _isTurning       = false;

                if (Now - _portalStateStart > (long)PostTeleportMs)
                {
                    int busyAfter = _host.HasGetBusyState ? _host.GetBusyState() : -1;
                    _host.Log($"Nav: PostTeleportSettle done, busyState={busyAfter}");
                    // Force-clear the client's internal busy count (hourglass cursor)
                    // and our tracked busy count. Portal teleport interrupts actions
                    // without firing the matching DecrementBusyCount callback.
                    if (_host.HasStopCompletely) _host.StopCompletely();
                    if (_host.HasForceResetBusyCount) _host.ForceResetBusyCount();
                    if (_combatManager != null) _combatManager.BusyCount = 0;

                    // Invalidate stale heading and watchdog data so the nav engine
                    // starts clean after the teleport — prevents oscillating turns
                    // caused by pre-portal heading/position data.
                    _hasGoodHeading = false;
                    _lastTurnDir    = 0;
                    _postTeleport   = true;
                    _watchdogNs     = double.NaN;
                    _watchdogEw     = double.NaN;
                    _watchdogNext   = Now + (long)WatchdogMs;
                    ResetPortalState();
                    Advance(route);
                }
                break;
        }
    }

    // ── Fellowship-follow ────────────────────────────────────────────────────
    private const double FollowArrivalYd = 5.0;   // stop within this of the leader
    private const double FollowResumeYd  = 8.0;   // resume moving once beyond this (hysteresis)
    private bool _followMoving;

    /// <summary>
    /// Steer toward the LIVE position of _settings.FollowTargetId (the fellowship
    /// leader). Self-contained — does NOT touch the route steering. Faces the
    /// target and autoruns when beyond FollowResumeYd; stops within FollowArrivalYd.
    /// </summary>
    private void FollowTarget()
    {
        uint targetId = _settings.FollowTargetId;

        if (!_host.HasGetObjectPosition ||
            !_host.TryGetObjectPosition(targetId, out uint tcell, out float tx, out float ty, out _) ||
            !NavCoordinateHelper.TryConvertPoseToCoords(tcell, tx, ty, out double tNS, out double tEW))
        {
            // Leader not loaded (different landblock / out of range) — hold.
            StopMovement();
            _followMoving = false;
            _settings.NavStatusLine = "Follow: leader out of range";
            return;
        }

        if (!TryGetPos(out double ns, out double ew)) return;

        double dNS = tNS - ns, dEW = tEW - ew;
        double distYd = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;
        _settings.NavStatusLine = $"Follow: {distYd:F0}yd";

        // Hysteresis so we don't jitter at the boundary: start moving past
        // FollowResumeYd, stop once inside FollowArrivalYd.
        if (_followMoving) { if (distYd <= FollowArrivalYd) _followMoving = false; }
        else               { if (distYd >  FollowResumeYd)  _followMoving = true;  }

        if (!_followMoving)
        {
            StopMovement();
            return;
        }

        double desiredDeg = Math.Atan2(tEW - ew, tNS - ns) * (180.0 / Math.PI);
        if (desiredDeg < 0) desiredDeg += 360.0;

        if (!_host.HasTurnToHeading)
        {
            StartForward();   // best-effort on a stale host
            return;
        }

        if (TryGetQuaternionHeading(out float curDeg))
        {
            double err  = NormalizeAngle(desiredDeg - curDeg);
            double step = Math.Clamp(err, -MaxStepDeg, MaxStepDeg);
            double newHeading = curDeg + step;
            if (newHeading >= 360.0) newHeading -= 360.0; else if (newHeading < 0.0) newHeading += 360.0;
            _host.TurnToHeading((float)newHeading);
            // Run only once roughly aligned, so we don't arc wide on a big turn.
            if (Math.Abs(err) <= BigTurnEnter) StartForward(); else StopForward();
        }
        else
        {
            _host.TurnToHeading((float)desiredDeg);
            StartForward();
        }
    }

    /// <summary>
    /// Current player landblock (objCellId &gt;&gt; 16), or 0 if unavailable. Used as
    /// a teleport-confirmation signal: a portal/recall that crosses a landblock
    /// boundary is confirmed even when it moves the player &lt; 50 yards (short-hop
    /// dungeon/interior portals the planar-distance test misses).
    /// </summary>
    private uint CurrentLandblock() =>
        _host.HasGetPlayerPose &&
        _host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _)
            ? cell >> 16
            : 0u;

    private void ResetPortalState()
    {
        _portalState         = PortalState.None;
        _prePortalNS         = double.NaN;
        _prePortalEW         = double.NaN;
        _prePortalLb         = 0;
        _wasInPortalSpace    = false;
        _trackingPortalSpace = false;
        _globalSettling      = false;
        _globalLastNS        = double.NaN;
        _globalLastEW        = double.NaN;
        _portalNpcFired      = false;
        _portalNpcDiagLogged = false;
        _settings.ActivePortalObjId = 0;   // stop drawing the portal marker/line
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
        // Mode-0/legacy motion keys are local cmdinterp toggles — cheap, always clear.
        _host.SetMotion(MotionTurnRight, false);
        _host.SetMotion(MotionTurnLeft,  false);

        // Tier 1 turns are CM_Movement *server events* (0xF661). Only send a
        // StopMovement when a turn is actually in flight — this method is called
        // every tick by the pause/portal/settle/idle paths, so an unconditional
        // send floods the server. _tier1TurnDir tracks the one active direction.
        if (_tier1TurnDir != 0 && _host.HasStopMovement)
        {
            _host.StopMovement(_tier1TurnDir > 0 ? MotionTurnRight : MotionTurnLeft, 0);
            _tier1TurnDir = 0;
        }
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

    // Perpendicular distance (yards) from point B to the infinite line through A and C.
    private static double CrossDistYards(double aNS, double aEW, double bNS, double bEW, double cNS, double cEW)
    {
        double acNS = cNS - aNS, acEW = cEW - aEW;
        double acLen = Math.Sqrt(acNS * acNS + acEW * acEW);
        if (acLen < 1e-9) return 0.0;
        double abNS = bNS - aNS, abEW = bEW - aEW;
        return Math.Abs(abNS * acEW - abEW * acNS) / acLen * 240.0;
    }
}

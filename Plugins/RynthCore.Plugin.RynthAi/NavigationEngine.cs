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
    private int   _lastTurnDir;         // +1 = right, -1 = left, 0 = none — hysteresis for small corrections
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
    private double LookaheadYards => ArrivalYards * 0.1;

    private static long Now => Environment.TickCount64;

    private WorldObjectCache? _objectCache;
    private uint _playerId;
    private CombatManager? _combatManager;
    private long _lastRecallCastAt;

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

        // ── BIG TURN (>BigTurnEnter° or first steer after teleport): stop and turn ─
        // Uses SetMotion turn keys so the character rotates with native animation.
        // After a teleport, always do a big turn to properly orient before running —
        // jumping straight into small corrections causes left-right oscillation.
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

        // ── Ensure forward motion ───────────────────────────────────────────
        StartForward();

        // ── Near waypoint: stop turning, just run straight ──────────────────
        // Matches old code: dist < ArrivalYards * SweepMult
        bool closeToWaypoint = dist < ArrivalYards * SweepMult;

        // ── SMALL CORRECTION: TurnRight/TurnLeft while running ──────────────
        // These combine with autorun naturally (like pressing W+A or W+D).
        // Hysteresis: once a direction is chosen, hold it until error crosses
        // zero or enters the dead zone. Prevents left-right oscillation when
        // the error hovers near zero (common after portals).
        if (absError > DeadZone && !closeToWaypoint)
        {
            int wantDir = error > 0 ? 1 : -1;

            // Only change direction if the error has clearly crossed to the other side
            if (_lastTurnDir != 0 && _lastTurnDir != wantDir && absError < DeadZone * 2.0)
            {
                // Error is small and we'd be reversing — hold current direction
                // until the error either grows or clearly crosses zero.
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
    /// </summary>
    private void FirePortalNpcUse(NavPoint pt)
    {
        _settings.NavStatusLine = $"Nav: portal '{pt.TargetName}'...";
        if (_objectCache == null || string.IsNullOrWhiteSpace(pt.TargetName) || !_host.HasUseObject)
        {
            _host.Log($"Nav: PortalNPC — cache/target/UseObject unavailable for '{pt.TargetName}'");
            return;
        }

        string target = pt.TargetName.Trim();
        int pid = unchecked((int)(_playerId != 0 ? _playerId : (uint)_host.GetPlayerId()));

        int bestId = 0;
        double bestDist = double.MaxValue;

        foreach (var wo in _objectCache.GetLandscapeObjects())
        {
            if (string.IsNullOrEmpty(wo.Name)) continue;
            if (!wo.Name.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                wo.Name.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            double d = pid != 0 ? _objectCache.Distance(pid, wo.Id) : 0.0;
            if (d < bestDist) { bestDist = d; bestId = wo.Id; }
        }

        if (bestId == 0)
        {
            _host.Log($"Nav: PortalNPC — no match found for '{target}'");
            return;
        }

        _host.UseObject((uint)bestId);
        _host.Log($"Nav: UseObject portal '{target}' (dist={bestDist:F1}yd) → 0x{bestId:X8}");
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
            _lastRecallCastAt = 0;

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
                }
                break;

            case PortalState.FiringAction:
                // Keep motions clear — UseObject/recall cast handles its own movement
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

                if (portalExited || positionChanged)
                {
                    int busyNow = _host.HasGetBusyState ? _host.GetBusyState() : -1;
                    _host.Log($"Nav: teleport detected (portalExit={portalExited} posChange={positionChanged}) busyState={busyNow}");
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
                    // PortalNPC is fire-once (UseObject starts a walk to the NPC).
                    if (_lastRecallCastAt == 0)
                    {
                        FirePortalNpcUse(pt);
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

    private void ResetPortalState()
    {
        _portalState         = PortalState.None;
        _prePortalNS         = double.NaN;
        _prePortalEW         = double.NaN;
        _wasInPortalSpace    = false;
        _trackingPortalSpace = false;
        _globalSettling      = false;
        _globalLastNS        = double.NaN;
        _globalLastEW        = double.NaN;
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

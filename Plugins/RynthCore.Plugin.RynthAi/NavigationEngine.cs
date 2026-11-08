using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthCore navigation engine — handles route following, waypoint steering, and nav commands.
///
/// Forward motion  : SetAutoRun(true/false) via RynthCoreHost.
/// Steering while running : SetMotion(TurnRight/TurnLeft) — combines with autorun naturally.
/// Large turns (>BigTurnEnter°): stop autorun, TurnToHeading, resume when error < BigTurnExit°.
/// Closest-approach detection prevents circling waypoints.
/// Stuck watchdog fires every 5 s if &lt; 2 yd moved, escalating jump → side-step →
/// side-step (other side) → back-out → start over. Waypoints are never skipped.
/// Route recovery (NavRecoveryEnabled): once the jump rung fails, or nav itself
/// wanders more than NavOffTrackYards off the route, a way back is planned (dungeon
/// cell graph indoors, RynthNav navmesh outdoors; NavRecovery.cs) and walked as a
/// temporary detour, opening doors on it, then the route resumes at the rejoin
/// waypoint. A waypoint in a different area (another dungeon, dungeon vs landscape)
/// holds nav instead: nothing can path there.
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
    private const double RecoveryBurstMs = 1500.0;   // run time for a side-step / back-out escape
    private const double SideStepDeg     = 75.0;     // heading offset for the side-step escape
    private const double BackOutDeg      = 165.0;    // heading offset for the back-out escape
    private const int    StuckRestartAfter = 5;      // after the back-out rung, start the escape ladder over
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
    // Where nav stopped for a pause (combat, loot, buff, door); NaN when not paused. With
    // NavResumeNearest on, a pause that moved the character re-picks the closest waypoint.
    private double _pauseNs = double.NaN;
    private double _pauseEw = double.NaN;
    private bool   _resumeCheckPending;      // nav took over again; check _pauseNs/_pauseEw on the next steer tick
    private const double ResumeMovedYards    = 10.0;   // moved less while paused: carry on to the same waypoint
    private const double ResumeTeleportYards = 400.0;  // moved more: a teleport, which the portal handling owns
    private int    _linearDir  = 1;
    private bool   _inPause;
    private int    _pauseIdx = -1;     // the Pause waypoint _pauseUntil belongs to
    private long   _pauseUntil;
    // Closest the character has come to the current waypoint since it became the target (or
    // since a pause / escape / teleport reset it). It used to hold only the previous tick's
    // distance, so a "sweep pass" needed the distance to grow 0.3 yd in ONE 33 ms tick (over
    // 9 yd/s straight away from the point): just past a point the distance grows slowly, the
    // pass was never seen, and nav turned round and walked back to the point (2026-10-05).
    private double _closestDist = double.MaxValue;
    // Where the character stood when it started on the current waypoint (NaN = take it on the
    // next steering tick). With the waypoint it gives the way the route arrives at the point,
    // for the pass-through rule (PassedThrough).
    private double _legStartNs = double.NaN, _legStartEw = double.NaN;

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
    // The Recall/PortalNPC waypoint whose teleport was confirmed but not yet advanced
    // past (the post-teleport settle). Survives ResetPortalState, which combat, buffing,
    // a door or AutoVendor/AutoTrade trigger through Stop(); null = none.
    private NavRouteParser? _teleportedRoute;
    private int _teleportedIdx = -1;
    // The Recall/PortalNPC waypoint whose action was FIRED (recall cast, portal used)
    // when a hold reset the portal state before nav saw a teleport. The teleport can
    // still happen while nav is held (a vital recharge right after the recall cast is
    // the usual case); when nav comes back, a large move from here means it did.
    private NavRouteParser? _firedRoute;
    private int    _firedIdx = -1;
    private double _firedNS = double.NaN, _firedEW = double.NaN;
    private uint   _firedCell;
    private uint   _firedPortalId;
    private double _firedPortalUseDistYd = double.MaxValue;

    // ── Stuck watchdog ───────────────────────────────────────────────────────
    private double _watchdogNs = double.NaN;
    private double _watchdogEw = double.NaN;
    private long   _watchdogNext;
    private int    _stuckCount;
    private bool   _inRecovery;
    private long   _recoveryUntil;
    // A jump only frees the "snagged on geometry" case. Repeated stucks at the
    // same spot need lateral displacement, then a back-out, and finally giving
    // up on the waypoint — otherwise the bot jumps in place forever.
    private enum RecoveryKind { Jump, Escape }
    private RecoveryKind _recoveryKind;

    // ── Route recovery: pathfinder detours (NavRecovery.cs plans them) ───────
    // When the ladder's cheap jump didn't free us (or this spot has snagged us
    // before), or nav itself wandered far off the route, plan a way back to the
    // route with the dungeon cell graph (indoors) or RynthNav's navmesh (outdoors)
    // and walk it as a temporary detour. The route and ActiveNavIndex are not
    // touched until the detour reaches its end; then the route resumes at the
    // rejoin waypoint. Bounded by NavMaxDetourAttempts per waypoint; after that
    // the old ladder carries on alone. Setting NavRecoveryEnabled off restores
    // the old behaviour exactly (no detours, no off-track check, no area hold).
    private sealed class Detour
    {
        public int    Number;          // session recovery counter, for the log
        public string Reason = string.Empty;
        public string Planner = string.Empty;
        public List<NavPoint> Points = new();
        public int    Index;           // next detour point to reach
        public int    StartIdx;        // ActiveNavIndex when it began (a change cancels it)
        public int    RejoinIdx;       // route waypoint to resume at
        public int    Skipped;         // route waypoints the rejoin passes over
        public double LengthYd;
        public int    Doorways;
        public long   StartedAt;
        public long   DeadlineAt;
        public double ClosestDist = double.MaxValue;   // closest to the current detour point so far
        public bool   Paused;          // nav was stopped (combat, loot, buff, door) mid-detour
        public long   PausedAt;
        public double PausedNs = double.NaN, PausedEw = double.NaN;
        public int    Replans;
        public int    DoorRequestCount;
        public long   NextDoorCheckAt;
        public readonly Dictionary<int, int> DoorRequests = new();
    }

    private const double OffTrackGraceMs      = 5000.0;  // off track this long before recovering
    private const double OffTrackMinYards     = 160.0;   // metas take the bot up to ~159 yd away on purpose
    private const double RejoinLookaheadYards = 20.0;    // route length past the target the rejoin may pick from
    private const int    RejoinLookaheadPts   = 8;
    private const int    StuckSpotRepeat      = 3;       // stucks within StuckSpotYards: go straight to a detour
    private const double StuckSpotYards       = 5.0;
    private const double DetourReplanMovedYd  = 5.0;     // moved this far while paused: plan again
    private const int    DetourMaxReplans     = 3;
    private const double DetourDoorCheckMs    = 400.0;
    private const double DetourDoorLookYd     = 12.0;    // look for doors this far along the next leg
    private const double DetourDoorCorridorYd = 3.0;     // ... and this close to it
    private const int    DetourDoorMaxRequests = 2;      // per door per detour

    private bool   RecoveryEnabled   => _settings.NavRecoveryEnabled;
    private double OffTrackYards     => Math.Max(OffTrackMinYards, _settings.NavOffTrackYards);
    private int    MaxDetourAttempts => Math.Clamp(_settings.NavMaxDetourAttempts, 1, 10);

    private INavRecoveryPlanner? _planner;
    private Detour? _detour;
    private int    _detourAttempts;          // detours tried (or failed to plan) for the current waypoint
    private bool   _detourGaveUpNotified;
    private int    _recoveryNumber;
    private double _stuckSpotNs = double.NaN, _stuckSpotEw = double.NaN;
    private int    _stuckSpotHits;
    private bool   _offTrackArmed;           // nav has been within OffTrackYards since it last took over
    private long   _offTrackSince;           // 0 = on track

    // Area check (route in another dungeon / on the landscape): recomputed only when
    // one of its inputs changes, announced once per mismatch.
    private NavRouteParser? _areaRoute;
    private int    _areaIdx = -1;
    private uint   _areaPlayerLb = uint.MaxValue;
    private bool   _areaPlayerInCell;
    private long   _areaRetryAt;
    private bool   _areaMismatch;
    private long   _areaMismatchSince;
    private string _areaMessage = string.Empty;
    private string _areaAnnounced = string.Empty;
    // The two places the last mismatch named ("the landscape", "in dungeon 0x5756").
    private string _areaRouteName = string.Empty;
    private string _areaPlayerName = string.Empty;
    // The look along the route for a waypoint in the character's area (TryCarryOnHere) runs once
    // per route / landblock / indoors-or-out; a look that found none is not repeated on the
    // 3 s rechecks. Null route = not looked yet.
    private NavRouteParser? _areaScanRoute;
    private uint _areaScanLb = uint.MaxValue;
    private bool _areaScanInCell;
    private bool _areaScanNone;
    // A route that walks INTO a portal has its last point on this side and the next
    // one on the other side. For a few seconds after reaching that last point, keep
    // walking on through it (the way the route came in) so the portal can fire,
    // before holding.
    private const double AreaGraceMs     = 6000.0;
    private const long   AreaRecheckMs   = 3000;
    private const double AreaGraceNearYd = 10.0;
    private const double AreaGraceOverYd = 4.0;

    // ── Observability (GetStateSnapshot / /ra navstate) ──────────────────────
    private double _lastDistYd     = double.NaN;
    private double _lastHeadingErr = double.NaN;
    private long   _lastSteerAt;

    // ── Derived thresholds (from settings) ──────────────────────────────────
    // These match the old NavigationManager exactly.
    private double DeadZone     => Math.Max(0.5,  _settings.NavDeadZone);
    private double BigTurnEnter => Math.Max(5.0,  _settings.NavStopTurnAngle);
    private double BigTurnExit  => Math.Max(1.0,  Math.Min(_settings.NavResumeTurnAngle, BigTurnEnter - 1.0));
    // Heading for a doorway point: turn in place much sooner and walk only when lined up, so the
    // character goes into the opening straight instead of arcing into its sides (2026-10-04).
    private const double DoorwayTurnEnter = 8.0, DoorwayTurnExit = 3.0;
    private bool _steerDoorway;
    private double TurnEnterNow => _steerDoorway ? Math.Min(BigTurnEnter, DoorwayTurnEnter) : BigTurnEnter;
    // Never tighter than the turn dead zone (+0.5): the servo stops turning inside DeadZone, so an
    // exit below it left the error between the two, no turn and no run: nav stood still until the
    // player tapped forward (doorway exit 3 vs dead zone 4, 2026-10-04).
    private double TurnExitNow  => Math.Max(DeadZone + 0.5, _steerDoorway ? Math.Min(BigTurnExit, DoorwayTurnExit) : BigTurnExit);
    private double SweepMult    => Math.Max(1.0,  _settings.NavSweepMult);
    // Nav point reach (FollowNavMin, VTank's "Follow/Nav Min Distance"): how close to get to
    // each nav point before moving on. Floored at the Settings minimum (0.5); it used to be
    // floored at 1.5, so a smaller value did nothing. Nothing else leans on 1.5: the sweep-pass
    // radius scales with it, the stuck watchdog measures movement (StuckYd) not distance to the
    // point, and turning keys off the heading error. A meta's navclosestoprange sets it too.
    private double ArrivalYards => Math.Max(LegacyUiSettings.FollowNavMinLowest, _settings.FollowNavMin);

    // Doorway points (NavPoint.Doorway: the dungeon pathfinder's approach / exit points,
    // centred in front of and beyond a narrow opening) have their own, tighter reach: at most
    // DoorwayReachYd (the nav point reach when that is smaller), and a closest-approach pass
    // only counts within DoorwaySweepMult of it. With the general reach (1.5 yd) and the sweep
    // pass (x2.5 = 3.75 yd) a corner was taken up to ~4 yd early and the character met the
    // opening at an angle; Lucy's log at 0x6346 had 70% of advances as sweep passes at 1.5-4 yd.
    // With 1 yd at a point 2.5 m in front of the opening the character crosses it within about
    // half a metre of its centre line, at under 15 degrees. The lookahead is off at them too.
    internal const double DoorwayReachYd  = 1.0;   // NavMarkerRenderer draws doorway rings at this size
    private const double DoorwaySweepMult = 1.5;
    private double ReachFor(NavPoint p) => p.Doorway ? Math.Min(ArrivalYards, DoorwayReachYd) : ArrivalYards;
    private double SweepFor(NavPoint p) => ReachFor(p) * (p.Doorway ? Math.Min(SweepMult, DoorwaySweepMult) : SweepMult);

    // Lookahead (corner rounding): within this distance of a waypoint, blend the aim point
    // toward the next one so corners are rounded. It used to be the Lookahead setting alone
    // (4 yd), whatever the nav point reach: at a 1 yd reach the aim was already 75% of the way
    // down the next leg when the character got to 1 yd, so it cut the corner, never got within
    // the reach, and ran into the wall at the corner (2026-10-05). Now the reach decides how
    // much rounding there is: none at or under CornerRoundFreeReachYd, then CornerRoundPerReachYd
    // per yard of reach above it, never more than the Lookahead setting (Advanced > Navigation >
    // Steering; 0 = off). Blending only matters where it starts outside the reach, so there is
    // none up to a 2 yd reach (the 1.5 yd default gives 1 yd, inside it); it reaches the 4 yd
    // setting at a 3 yd reach, so 3-5 yd round corners as before. Doorway points never blend.
    internal const double CornerRoundFreeReachYd = 1.0, CornerRoundPerReachYd = 2.0;
    internal static double CornerLookaheadYards(double lookaheadSetting, double reachYd)
        => Math.Clamp(CornerRoundPerReachYd * (reachYd - CornerRoundFreeReachYd), 0.0, Math.Max(0.0, lookaheadSetting));
    private double LookaheadYards => CornerLookaheadYards(_settings.NavLookaheadYards, ArrivalYards);

    // Pass-through: a plain waypoint the character has gone past (it is beyond the point along
    // the way it came, and no farther to the side than the sweep radius) counts as reached,
    // so nav doesn't turn round to walk back to it. "Beyond along the way it came" means the
    // point is behind the character: a corner cut toward the next point never qualifies.
    // Doorway points keep their own tight reach and sweep and never use it.
    private const double PassMinLegYd = 1.0;   // shorter approach: no reliable direction
    private bool PassedThrough(NavPoint pt, double ns, double ew)
    {
        if (pt.Doorway || double.IsNaN(_legStartNs)) return false;
        double ux = (pt.NS - _legStartNs) * 240.0, uy = (pt.EW - _legStartEw) * 240.0;
        double len = Math.Sqrt(ux * ux + uy * uy);
        if (len < PassMinLegYd) return false;
        ux /= len; uy /= len;
        double ax = (ns - pt.NS) * 240.0, ay = (ew - pt.EW) * 240.0;
        double along = ax * ux + ay * uy;
        double side  = Math.Abs(ax * uy - ay * ux);
        return along > 0.0 && side <= SweepFor(pt);
    }


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
    //
    // Off for now (2026-09-28): Tier 1 and the unbuilt Tier 2 misbehaved in
    // testing while Legacy works, so every setting runs Legacy. The mode is
    // also forced to 0 when settings load or change; see MovementMode.
    private bool Tier1Movement => false;

    /// <summary>Millisecond clock for every nav timer. Tests swap in a fake clock.</summary>
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;
    private static long Now => Clock();

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
    public void SetRecoveryPlanner(INavRecoveryPlanner planner) => _planner = planner;

    /// <summary>
    /// Closest closed door on the leg from (ax, ay) to (bx, by), world units, within
    /// <c>corridor</c> of it and near height z; 0 when none (set by the plugin).
    /// </summary>
    public Func<double, double, double, double, double, double, int>? FindClosedDoorOnLeg;

    /// <summary>Asks the door controller to open this door next tick (set by the plugin).</summary>
    public Action<int>? RequestDoorOpen;

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
        string key = $"macro={_settings.IsMacroRunning} navEnabled={_settings.EnableNavigation} action='{_settings.BotAction}' recovery={_inRecovery} detour={_detour != null} areaHold={_areaMismatch} moving={_isMovingForward} turning={_isTurning} idx={_settings.ActiveNavIndex}";
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
        //
        // STEP 5: these string reads are now redundant BY CONSTRUCTION rather than
        // wrong. OnTick is the only caller and only calls Tick() when the arbiter
        // decided Navigating (string is "Navigating"/"Following", so both tests
        // pass) or while a portal action is in flight. They are kept as a cheap
        // inner assertion, NOT as a second authority.
        //
        // Known residual: in the portal-action case the decision may be Combat or
        // Looting, and then this gate takes the stop path instead of running the
        // teleport detection the portal exception exists for. That predates the
        // migration — the old cascade reached the same place by a longer route —
        // and closing it means deciding whether nav should route-walk while combat
        // owns the tick, which wants a live session, not a guess.
        bool shouldNav = _settings.IsMacroRunning
                      && _settings.EnableNavigation
                      && _settings.BotAction != "Combat"
                      && _settings.BotAction != "Looting";

        LogNavStateIfChanged();

        if (!shouldNav)
        {
            // Nav doesn't own movement: whatever moves the character now, being far
            // from the route afterwards is not "off track".
            _offTrackArmed = false;
            _offTrackSince = 0;
            if (_detour != null)
            {
                if (!_settings.IsMacroRunning) EndDetour("cancelled, the macro stopped", success: false);
                else MarkDetourPaused();
            }
            if (!_hasStopped)
            {
                // The macro switched off with nav idle (no route, between routes): the character may be
                // the player's, running by hand, and is left running (the owner, 2026-11-07). Nav's own
                // movement is still stopped.
                bool navMoving = _isMovingForward || _isTurning || _tier1TurnDir != 0;
                bool sendStops = _settings.IsMacroRunning || navMoving;
                if (sendStops) ClearTurnMotions();
                if (_stopRequestedAt == long.MaxValue)
                    _stopRequestedAt = Now;

                if (Now - _stopRequestedAt >= (long)StopDebounceMs)
                {
                    if (sendStops) _host.SetAutoRunBy("Nav", false);
                    _isMovingForward = false;
                    _isTurning       = false;
                    _hasStopped      = true;
                    MarkPausePosition();
                    RememberFiredPortalAction();
                    ResetPortalState();
                }
            }
            return;
        }

        _stopRequestedAt = long.MaxValue;
        if (_hasStopped) _resumeCheckPending = true;
        _hasStopped      = false;

        // Fellowship-follow takes priority over route nav while enabled: steer
        // toward the leader's LIVE position instead of a fixed waypoint. Opt-in,
        // so it never affects normal route running. No route required.
        if (_settings.FollowMode && _settings.FollowTargetId != 0)
        {
            // STEP 5: the "Following" write moved to ActivityArbiter.Apply, which
            // projects it from Navigating + FollowActive. Nav is no longer a
            // BotAction writer at all.
            FollowTarget(_settings.FollowTargetId);
            return;
        }

        // A follow-type nav file names who to follow instead of listing points.
        var followRoute = _settings.CurrentRoute;
        if (followRoute != null && followRoute.RouteType == NavRouteType.Follow
            && followRoute.FollowTargetName.Length > 0)
        {
            FollowTarget(ResolveFollowTarget(followRoute));
            return;
        }

        var route = _settings.CurrentRoute;
        if (route == null || route.Points.Count == 0) { StopMovement(); return; }

        // Route swap (e.g., meta EmbedNav replaced CurrentRoute): reset carry-over state
        // so a new route doesn't inherit the old route's pause/portal/stuck progress.
        if (!ReferenceEquals(route, _lastRoute))
        {
            // Outdoor Hunt plans its walk again every 8 s: that is the same walk, not a new route.
            // Resetting here kept a character pinned at a wall on the first rung of the stuck ladder
            // (a jump) for good: no count ever built up, so no detour was ever tried (2026-11-06).
            bool sameWalk = route.OutdoorHuntWalk && _lastRoute?.OutdoorHuntWalk == true;
            _lastRoute      = route;
            _inPause        = false;
            _closestDist    = double.MaxValue;
            _linearDir      = 1;
            if (!sameWalk)
            {
                _watchdogNs = double.NaN;
                _watchdogEw = double.NaN;
                _stuckCount = 0;
                _inRecovery = false;
            }
            _hasGoodHeading = false;
            // The loader chose the new route's start index; a pause on the old route says nothing.
            ClearPausePosition();
            ResetPortalState();
            if (!sameWalk) ResetRecovery("the route changed");
            _host.Log($"Nav: route swap detected, {route.Points.Count} pts, startIdx={_settings.ActiveNavIndex}{(sameWalk ? " (the same hunt walk: the stuck count carries on)" : "")}");
        }

        // STEP 5: the self-promote from "Default" to "Navigating" is gone. Tick()
        // only runs at all when the arbiter decided Navigating (or during a
        // portal action), and the arbiter has already written the string this
        // tick — promoting it here was both redundant and the documented
        // violation of the arbiter's "sole writer of Navigating" invariant.

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
                _host.SetAutoRunBy("Nav", false);
                ClearTurnMotions();
                if (_host.HasStopCompletely) _host.StopCompletelyBy("Nav");
                _isMovingForward = false;
                _isTurning       = false;

                if (Now - _globalSettleStart > (long)PostTeleportMs)
                {
                    _host.Log($"Nav: global post-teleport settle done ({PostTeleportMs:F0}ms)");
                    if (_host.HasStopCompletely) _host.StopCompletelyBy("Nav");
                    if (_host.HasForceResetBusyCount) _host.ForceResetBusyCount();
                    if (_combatManager != null) _combatManager.BusyCount = 0;
                    _hasGoodHeading = false;
                    _lastTurnDir    = 0;
                    _postTeleport   = true;
                    _watchdogNs     = double.NaN;
                    _watchdogEw     = double.NaN;
                    _watchdogNext   = Now + (long)WatchdogMs;
                    _closestDist    = double.MaxValue;
                    _globalSettling = false;
                    ResetRecovery("teleported");
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

        // Back from a pause, past any teleport settle: rejoin at the closest waypoint if it moved us.
        if (_resumeCheckPending)
        {
            _resumeCheckPending = false;
            ResumeAfterPause();
        }

        int idx = _settings.ActiveNavIndex;
        if (!IndexValid(idx, route)) { HandleRouteEnd(route); return; }

        UpdateWatchdog(route);
        if (_inRecovery)
        {
            if (Now < _recoveryUntil) return;
            // An escape burst was driving autorun — kill it before handing
            // steering back to the route, or the first steer tick inherits
            // forward motion on the escape heading.
            if (_recoveryKind == RecoveryKind.Escape) StopMovement();
            _inRecovery = false;
            _settings.NavIsStuck = false;
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
                if (TeleportAlreadyDone(route, idx)) return;
                HandlePortalOrRecall(pt, route);
                return;
        }

        // ── Standard coordinate waypoint ─────────────────────────────────────
        _portalState = PortalState.None;
        _inPause     = false;
        _teleportedRoute = null;
        _firedRoute      = null;

        if (!TryGetPos(out double ns, out double ew)) return;

        // Route recovery. The area hold comes first: when the waypoint is in another
        // dungeon (or dungeon vs landscape) no detour or escape can reach it.
        if (RecoveryEnabled)
        {
            if (AreaMismatchHold(route, idx, pt)) return;
            if (_detour != null) { TickDetour(route, ns, ew); return; }
            CheckOffTrack(route, idx, ns, ew);
            if (_detour != null) return;
        }
        else if (_detour != null || _areaMismatch)
        {
            // Switched off mid-recovery: drop it and steer the route as before.
            ResetRecovery("recovery switched off");
        }

        double dNS  = pt.NS - ns;
        double dEW  = pt.EW - ew;
        double dist = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;

        // A camp route (a Circular/Linear route whose waypoints are all here, e.g. a
        // single point to hold a spot): advancing can't take us anywhere, so stand on
        // it. Advancing every tick instead left autorun on, so the bot ran past the
        // point, turned round and ran back, forever, logging "arrived" 30 times a second.
        if (dist < ArrivalYards && IsCampRoute(route, ns, ew))
        {
            HoldAtCampRoute(idx, dist, route);
            return;
        }
        _campHoldAnnounced = false;

        // Arrival check
        if (dist < ReachFor(pt))
        {
            _host.Log($"Nav: arrived at [{idx}] dist={dist:F1}yd → advancing");
            _closestDist = double.MaxValue;
            UpdateStatusLine(idx, dist, route, 0.0);
            // Advance before the jump: the Jumper pauses nav and later restores what it
            // saw, so jumping first turned a finished Once route back on (re-run from [0]).
            Advance(route);
            if (pt.Type == NavPointType.Jump) FireJump(pt, idx);
            return;
        }

        // First tick on this waypoint (or after a pause / escape / teleport): remember where
        // the approach starts, for the pass-through rule.
        if (_closestDist == double.MaxValue) { _legStartNs = ns; _legStartEw = ew; }

        // Went past it: the point is behind, close enough to the side. Counts as reached
        // instead of turning round to walk back to it.
        if (PassedThrough(pt, ns, ew))
        {
            _host.Log($"Nav: passed [{idx}] dist={dist:F1}yd → advancing");
            _closestDist = double.MaxValue;
            UpdateStatusLine(idx, dist, route, 0.0);
            Advance(route);
            if (pt.Type == NavPointType.Jump) FireJump(pt, idx);
            return;
        }

        // Closest-approach detection — prevents circling: within the sweep radius, then
        // 0.3 yd farther out than the closest it came.
        if (_closestDist < SweepFor(pt) && dist > _closestDist + 0.3)
        {
            _host.Log($"Nav: sweep-pass [{idx}] closest={_closestDist:F1} now={dist:F1}yd → advancing");
            _closestDist = double.MaxValue;
            Advance(route);
            if (pt.Type == NavPointType.Jump) FireJump(pt, idx);
            return;
        }
        _closestDist = Math.Min(_closestDist, dist);

        SteerToWaypoint(idx, pt, route, ns, ew, dist);
    }

    public void Stop()
    {
        // _inPause / _pauseUntil are NOT reset here: a fight at a Pause waypoint
        // restarted its full timer, so a "wait N s for the respawn" pause where mobs came
        // back sooner than N s never finished. The deadline keeps running through the
        // interruption; a new route, a route reset or leaving the point clears it.
        _inRecovery      = false;
        // _linearDir is NOT reset here. Stop() runs on every pause (combat, loot,
        // buff, door), and resetting it turned a Linear route around after each
        // fight on the way back: the bot re-walked the far leg to the end instead of
        // finishing the return. A new route resets it (route swap / ResetRouteState).
        //
        // Combat, looting, buffing and doors stop nav and move the character. The
        // closest-approach reading from before the pause says nothing about where we
        // are now; kept, the first tick back reads the displacement as a sweep-pass
        // and advances past a waypoint that was never reached.
        _closestDist     = double.MaxValue;
        _stopRequestedAt = long.MaxValue;
        // A detour survives the pause: it re-plans on the way back if the pause
        // moved us. Whatever moved us, being far off the route afterwards is not
        // "off track" (metas pull the bot up to ~159 yd away on purpose).
        MarkDetourPaused();
        _offTrackArmed   = false;
        _offTrackSince   = 0;
        RememberFiredPortalAction();
        ResetPortalState();
        _host.SetAutoRunBy("Nav", false);
        ClearTurnMotions();
        _isMovingForward = false;
        _isTurning       = false;
        _hasStopped      = true;
        MarkPausePosition();
    }

    /// <summary>Remembers where the first stop of a pause happened (later stops in the same pause keep it).</summary>
    private void MarkPausePosition()
    {
        if (!double.IsNaN(_pauseNs)) return;
        if (!TryGetPos(out double ns, out double ew)) return;
        _pauseNs = ns;
        _pauseEw = ew;
    }

    private void ClearPausePosition()
    {
        _pauseNs = double.NaN;
        _pauseEw = double.NaN;
        _resumeCheckPending = false;
    }

    /// <summary>
    /// Nav took over again after a pause. With <c>NavResumeNearest</c> on and the pause having
    /// moved the character <see cref="ResumeMovedYards"/> or more (a chase, a corpse run), the
    /// route re-picks its closest waypoint instead of walking back to the one it was heading for.
    /// A detour or area hold in progress owns getting back, so it is left alone.
    /// </summary>
    private void ResumeAfterPause()
    {
        double pNs = _pauseNs, pEw = _pauseEw;
        ClearPausePosition();
        if (!_settings.NavResumeNearest || double.IsNaN(pNs)) return;
        if (_detour != null || _areaMismatch) return;
        if (KeepWaypointAfterPause?.Invoke() == true) return;
        if (!TryGetPos(out double ns, out double ew)) return;

        double movedYd = Math.Sqrt(Sq(ns - pNs) + Sq(ew - pEw)) * 240.0;
        if (movedYd < ResumeMovedYards || movedYd > ResumeTeleportYards) return;
        RepickNearestWaypoint($"back from a pause that moved {movedYd:F0}yd", resetRecovery: false);
    }

    /// <summary>
    /// True while the route must carry on to the waypoint it was heading for after a pause, even
    /// with NavResumeNearest on (set by the plugin: the dungeon patrol). A patrol loop comes back
    /// past the rooms it started in, so the closest waypoint after a chase is often one near the
    /// END of the loop, and everything in between would be skipped.
    /// </summary>
    public Func<bool>? KeepWaypointAfterPause;

    /// <summary>Sends a Chat waypoint's text as if typed in chat (set by the plugin).</summary>
    public Action<string>? ChatSubmit;

    /// <summary>
    /// Points the route at the waypoint nearest the player (macro start). Circular
    /// and Linear routes pick from the whole route; a Once route only from the
    /// current waypoint onward, so finished steps (a recall, a portal) aren't
    /// repeated, and a finished Once route stays finished. Only plain waypoints are
    /// candidates; if the current one is a Recall/Portal/Chat/Pause step it is kept.
    /// A Linear route keeps its direction. With <c>NavResumeNearest</c> off the
    /// route keeps its last waypoint, unless <paramref name="always"/> (hazard escapes
    /// and corpse runs, which leave the route behind).
    /// </summary>
    public void ResumeFromNearestWaypoint(bool always = false)
    {
        ClearPausePosition();
        if (!always && !_settings.NavResumeNearest) return;
        RepickNearestWaypoint("macro start", resetRecovery: true);
    }

    private void RepickNearestWaypoint(string why, bool resetRecovery)
    {
        if (resetRecovery) ResetRecovery("macro started");
        var route = _settings.CurrentRoute;
        if (route == null || route.Points.Count == 0) return;
        if (route.RouteType == NavRouteType.Follow) return;
        // A finished Once route (index left past its end) is done: picking from [0] here
        // used to turn it back on at the nearest point, so a stop + start re-ran it.
        if (_settings.NavRouteFinished()) return;
        if (!TryGetPos(out double ns, out double ew)) return;

        int cur = _settings.ActiveNavIndex;
        if (IndexValid(cur, route) && !NavRouteParser.IsPlainWaypoint(route.Points[cur].Type)) return;

        bool once = route.RouteType == NavRouteType.Once && IndexValid(cur, route);
        int from = once ? cur : 0;
        int best = -1;
        double bestD = double.MaxValue;
        for (int i = from; i < route.Points.Count; i++)
        {
            var p = route.Points[i];
            // A Once route's steps still to come (a Chat, Pause, Recall, Portal, Jump, NPC
            // or vendor point) must not be jumped over: a town run started at its far end
            // used to skip the vendor step and complete at once.
            if (once && !NavRouteParser.IsPlainWaypoint(p.Type)) break;
            if (!NavRouteParser.IsPlainWaypoint(p.Type)) continue;
            double dN = p.NS - ns, dE = p.EW - ew;
            double d = dN * dN + dE * dE;
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return;

        // Indoors, "nearest" by map distance is often through a wall or on another floor (a
        // waypoint 26 yd away was a 307 yd walk, and the bot ran into the wall). There the
        // few nearest waypoints, and the current one, are compared by the dungeon map's
        // walking length; with no path for any of them the straight-line pick stands.
        string how = "nearest";
        int walkBest = PickNearestByWalk(route, from, once, cur, ns, ew, out double walkYd);
        if (walkBest >= 0) { best = walkBest; how = $"nearest on foot ({walkYd:F0}yd walk)"; }
        else
        {
            // Already walked past the nearest point toward the next one: the closest spot on
            // the route lies on the leg ahead, so head for its far end instead of turning back.
            int ahead = PassedToward(route, best, ns, ew);
            if (ahead >= 0) { best = ahead; how = "next after the nearest"; }
        }
        if (best == cur) return;

        _host.Log($"Nav: {why} — {how} waypoint [{best}] ({Math.Sqrt(Sq(route.Points[best].NS - ns) + Sq(route.Points[best].EW - ew)) * 240.0:F0}yd) instead of [{cur}]");
        _settings.ActiveNavIndex = best;
        _closestDist = double.MaxValue;
        _stuckCount = 0;
        _watchdogNs = double.NaN;
        _watchdogEw = double.NaN;
    }

    /// <summary>
    /// The waypoint after <paramref name="idx"/> in the direction the route runs, when the
    /// closest spot on the route is on the leg out of <paramref name="idx"/> (the player has
    /// walked past it) rather than on the leg into it; otherwise -1. The next point must be a
    /// plain waypoint (a step is never jumped to), and a Linear route at its end has no leg
    /// ahead in its current direction. With no plain point before <paramref name="idx"/> the
    /// leg in is the point itself.
    /// </summary>
    private int PassedToward(NavRouteParser route, int idx, double ns, double ew)
    {
        int n = route.Points.Count;
        int step = route.RouteType == NavRouteType.Linear ? _linearDir : 1;
        int next = idx + step, prev = idx - step;
        if (route.RouteType == NavRouteType.Circular) { next = (idx + 1) % n; prev = (idx - 1 + n) % n; }
        if (!IndexValid(next, route) || next == idx) return -1;
        var a = route.Points[idx];
        var b = route.Points[next];
        if (!NavRouteParser.IsPlainWaypoint(b.Type)) return -1;

        double legOut = SegmentDistSq(ns, ew, a, b);
        double legIn  = IndexValid(prev, route) && prev != idx && NavRouteParser.IsPlainWaypoint(route.Points[prev].Type)
            ? SegmentDistSq(ns, ew, route.Points[prev], a)
            : Sq(ns - a.NS) + Sq(ew - a.EW);
        return legOut < legIn ? next : -1;
    }

    /// <summary>Squared map distance from (ns, ew) to the leg a→b.</summary>
    private static double SegmentDistSq(double ns, double ew, NavPoint a, NavPoint b)
    {
        double vN = b.NS - a.NS, vE = b.EW - a.EW;
        double len = vN * vN + vE * vE;
        double t = len <= 0 ? 0 : Math.Clamp(((ns - a.NS) * vN + (ew - a.EW) * vE) / len, 0, 1);
        return Sq(ns - (a.NS + t * vN)) + Sq(ew - (a.EW + t * vE));
    }

    private const int WalkCandidates = 5;

    private static double Sq(double v) => v * v;

    /// <summary>
    /// Inside a cell (dungeon or building) with a planner: of the <see cref="WalkCandidates"/>
    /// waypoints nearest by map distance, plus the current one, the one with the shortest
    /// planned walk. -1 outdoors, with no planner, or when none of them has a path.
    /// </summary>
    private int PickNearestByWalk(NavRouteParser route, int from, bool once, int cur, double ns, double ew, out double walkYd)
    {
        walkYd = 0;
        if (_planner == null || !_host.HasGetPlayerPose) return -1;
        if (!_host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _)) return -1;
        if ((cell & 0xFFFF) < 0x0100) return -1;

        var near = new List<(int Idx, double D)>();
        for (int i = from; i < route.Points.Count; i++)
        {
            var p = route.Points[i];
            if (once && !NavRouteParser.IsPlainWaypoint(p.Type)) break;
            if (!NavRouteParser.IsPlainWaypoint(p.Type)) continue;
            near.Add((i, Sq(p.NS - ns) + Sq(p.EW - ew)));
        }
        near.Sort((a, b) => a.D.CompareTo(b.D));
        var candidates = new List<int>();
        for (int k = 0; k < near.Count && k < WalkCandidates; k++) candidates.Add(near[k].Idx);
        if (IndexValid(cur, route) && cur >= from && NavRouteParser.IsPlainWaypoint(route.Points[cur].Type) && !candidates.Contains(cur))
            candidates.Add(cur);

        int best = -1;
        double bestYd = double.MaxValue;
        foreach (int i in candidates)
        {
            var plan = _planner.TryPlan(route.Points[i], out _);
            if (plan == null) continue;
            if (plan.LengthYd < bestYd) { bestYd = plan.LengthYd; best = i; }
        }
        walkYd = best >= 0 ? bestYd : 0;
        return best;
    }

    public void ResetRouteState()
    {
        Stop();
        ClearPausePosition();
        ResetRecovery("route reset");
        _teleportedRoute = null;
        _firedRoute      = null;
        _inPause        = false;
        _linearDir      = 1;
        _stuckCount     = 0;
        _recoveryKind   = RecoveryKind.Jump;
        _closestDist    = double.MaxValue;
        _watchdogNs     = double.NaN;
        _watchdogEw     = double.NaN;
        _hasGoodHeading = false;
    }

    /// <summary>
    /// /ra nav restart: the loaded route starts over from its first point, as when it was
    /// loaded with NavResumeNearest off (a Linear route heading out again, a finished Once
    /// route running again). Nav is switched on; the macro is left as it is. False (nothing
    /// done) with no waypoints to walk (none loaded, or a Follow route).
    /// </summary>
    public bool RestartRoute()
    {
        var route = _settings.CurrentRoute;
        if (route == null || route.Points.Count == 0 || route.RouteType == NavRouteType.Follow) return false;
        ResetRouteState();
        _settings.ActiveNavIndex   = 0;
        _settings.EnableNavigation = true;
        _host.Log($"Nav: route restarted from [0] ({route.RouteType}, {route.Points.Count} pts)");
        return true;
    }

    /// <summary>
    /// One-shot view of everything the nav engine is keying off. Nav had no
    /// snapshot (Combat/Buff/Meta did), so a wedged route meant guessing which
    /// of the gate, the pause, the portal FSM or the stuck watchdog was holding
    /// it. Read-only; safe to call from the UI or a chat command.
    /// </summary>
    public NavStateSnapshot GetStateSnapshot()
    {
        var route = _settings.CurrentRoute;
        return new NavStateSnapshot
        {
            EnableNavigation = _settings.EnableNavigation,
            IsMacroRunning   = _settings.IsMacroRunning,
            BotAction        = _settings.BotAction ?? string.Empty,
            RouteType        = route?.RouteType.ToString() ?? "none",
            PointCount       = route?.Points.Count ?? 0,
            Index            = _settings.ActiveNavIndex,
            PointType        = route != null && IndexValid(_settings.ActiveNavIndex, route)
                                   ? route.Points[_settings.ActiveNavIndex].Type.ToString()
                                   : "n/a",
            LinearDir        = _linearDir,
            DistYd           = _lastDistYd,
            HeadingErrDeg    = _lastHeadingErr,
            MsSinceSteer     = _lastSteerAt == 0 ? -1 : Now - _lastSteerAt,
            MovingForward    = _isMovingForward,
            Turning          = _isTurning,
            Stopped          = _hasStopped,
            InPause          = _inPause,
            PauseRemainMs    = _inPause ? Math.Max(0, _pauseUntil - Now) : 0,
            PortalState      = _portalState.ToString(),
            InRecovery       = _inRecovery,
            RecoveryKind     = _recoveryKind.ToString(),
            RecoveryRemainMs = _inRecovery ? Math.Max(0, _recoveryUntil - Now) : 0,
            StuckCount       = _stuckCount,
            Recovery         = DescribeRecovery(),
            DetourAttempts   = _detourAttempts,
            FollowMode       = _settings.FollowMode,
            FollowTargetId   = _settings.FollowTargetId,
            StatusLine       = _settings.NavStatusLine ?? string.Empty,
        };
    }

    public struct NavStateSnapshot
    {
        public bool   EnableNavigation;
        public bool   IsMacroRunning;
        public string BotAction;
        public string RouteType;
        public int    PointCount;
        public int    Index;
        public string PointType;
        public int    LinearDir;
        public double DistYd;
        public double HeadingErrDeg;
        public long   MsSinceSteer;
        public bool   MovingForward;
        public bool   Turning;
        public bool   Stopped;
        public bool   InPause;
        public long   PauseRemainMs;
        public string PortalState;
        public bool   InRecovery;
        public string RecoveryKind;
        public long   RecoveryRemainMs;
        public int    StuckCount;
        public string Recovery;        // "none", "detour ...", "area hold: ..."
        public int    DetourAttempts;
        public bool   FollowMode;
        public uint   FollowTargetId;
        public string StatusLine;
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
            if (!NavRouteParser.IsPlainWaypoint(pt.Type)) continue;
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
        _steerDoorway = pt.Doorway;
        // ── Lookahead blend ──────────────────────────────────────────────────
        double tNS = pt.NS, tEW = pt.EW;
        // Never at a doorway point: blending toward the next point there is exactly the corner
        // cut that meets a narrow opening at an angle.
        if (dist < LookaheadYards && !pt.Doorway)
        {
            int ni = PeekNext(idx, route);
            // Only blend toward the NEXT waypoint when it is a real travel target
            // (a Point). Action waypoints (PortalNPC / Recall / Chat / Pause) are
            // never navigated to — they fire in place once the index reaches them —
            // and their stored coordinate is frequently a placeholder far from the
            // actual spot (a PortalNPC whose coord points off "to the abyss" is the
            // recurring case). Blending toward it swung the avatar to face that
            // bogus direction on arrival, right before using the portal.
            if (ni >= 0 && NavRouteParser.IsPlainWaypoint(route.Points[ni].Type))
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

        UpdateStatusLine(idx, dist, route, error);
        DriveHeading(error, currentDeg, dist);
    }

    /// <summary>Current heading (0 = North, clockwise) and the signed error to the aim point.</summary>
    private double HeadingErrorTo(double tNS, double tEW, double ns, double ew, out double currentDeg)
    {
        double desiredDeg = Math.Atan2(tEW - ew, tNS - ns) * (180.0 / Math.PI);
        if (desiredDeg < 0) desiredDeg += 360.0;

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
        return NormalizeAngle(desiredDeg - currentDeg);
    }

    /// <summary>
    /// The actuator half of the steering: run gate, turn servo and autorun heartbeat
    /// for a heading error. Shared by route steering and recovery detours.
    /// </summary>
    private void DriveHeading(double error, double currentDeg, double dist)
    {
        double absError = Math.Abs(error);

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
            if (absError <= TurnExitNow)
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
        else if (absError > TurnEnterNow || (_postTeleport && absError > TurnExitNow))
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
                if (_tier1TurnDir > 0)      _host.StopMovementBy("Nav", MotionTurnRight, 0);
                else if (_tier1TurnDir < 0) _host.StopMovementBy("Nav", MotionTurnLeft,  0);

                if (want > 0)      _host.DoMovementBy("Nav", MotionTurnRight, (float)Tier1TurnSpeed, 0);
                else if (want < 0) _host.DoMovementBy("Nav", MotionTurnLeft,  (float)Tier1TurnSpeed, 0);

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
                _host.TurnToHeadingBy("Nav", (float)newHeading);
            }
        }

        // ── Heartbeat: periodically re-assert autorun ───────────────────────
        if (_isMovingForward && Now - _lastHeartbeat > (long)HeartbeatMs)
        {
            _host.SetAutoRunBy("Nav", true);
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
            if (absError <= TurnExitNow)
            {
                _isTurning = false;
                ClearTurnMotions();
                StartForward();
                return;
            }

            if (error > 0)
            {
                _host.SetMotionBy("Nav", MotionTurnRight, true);
                _host.SetMotionBy("Nav", MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotionBy("Nav", MotionTurnLeft,  true);
                _host.SetMotionBy("Nav", MotionTurnRight, false);
            }
            return;
        }

        if (absError > TurnEnterNow || (_postTeleport && absError > DeadZone))
        {
            _postTeleport = false;
            StopForward();
            if (error > 0)
            {
                _host.SetMotionBy("Nav", MotionTurnRight, true);
                _host.SetMotionBy("Nav", MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotionBy("Nav", MotionTurnLeft,  true);
                _host.SetMotionBy("Nav", MotionTurnRight, false);
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
                _host.SetMotionBy("Nav", MotionTurnRight, true);
                _host.SetMotionBy("Nav", MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotionBy("Nav", MotionTurnLeft,  true);
                _host.SetMotionBy("Nav", MotionTurnRight, false);
            }
        }
        else
        {
            _lastTurnDir = 0;
            ClearTurnMotions();
        }

        if (_isMovingForward && Now - _lastHeartbeat > (long)HeartbeatMs)
        {
            _host.SetAutoRunBy("Nav", true);
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
        _closestDist = double.MaxValue;
        _portalState = PortalState.None;
        // Reaching a waypoint is progress: the next snag gets a fresh detour budget.
        _detourAttempts       = 0;
        _detourGaveUpNotified = false;


        switch (route.RouteType)
        {
            case NavRouteType.Circular:
                _settings.ActiveNavIndex = (_settings.ActiveNavIndex + 1) % route.Points.Count;
                break;

            case NavRouteType.Linear:
                _settings.ActiveNavIndex = NextLinearIndex(_settings.ActiveNavIndex, route.Points.Count);
                break;

            case NavRouteType.Once:
                _settings.ActiveNavIndex++;
                if (_settings.ActiveNavIndex >= route.Points.Count)
                    CompleteOnceRoute(route);
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
            // Never more than one lap: two points on the same spot of a Circular route
            // used to bounce between each other 4096 times a tick.
            int skipBudget = Math.Min(4096, route.Points.Count - 1);
            while (skipped < skipBudget)
            {
                var candidate = route.Points[_settings.ActiveNavIndex];
                if (!NavRouteParser.IsPlainWaypoint(candidate.Type))
                    break; // never skip a control point (Pause/Chat/Recall/PortalNPC)

                double cNS = candidate.NS - curNs;
                double cEW = candidate.EW - curEw;
                double cDist = Math.Sqrt(cNS * cNS + cEW * cEW) * 240.0;
                if (cDist >= ReachFor(candidate))
                    break; // far enough that SteerToWaypoint has something to do

                int beforeSkip = _settings.ActiveNavIndex;
                AdvanceOneIndex(route);
                if (_settings.ActiveNavIndex == beforeSkip)
                    break; // route ended / cleared / circular wrapped back; stop
                skipped++;
                // A Once route that skipped off its last point has just completed; the
                // index is past the end, and reading Points[index] next would throw.
                if (!IndexValid(_settings.ActiveNavIndex, route))
                    break;
            }

            if (skipped > 0)
                _host.Log($"Nav: skipped {skipped} dense waypoint(s) within {arrival:F1}yd → now on [{_settings.ActiveNavIndex}]");

            // No straight-line shortcut past waypoints: every waypoint is visited
            // (2026-09-28). NavShortcutYards is kept in saved settings but unused.
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
    /// without re-clearing _closestDist / _portalState.
    /// </summary>
    private void AdvanceOneIndex(NavRouteParser route)
    {
        switch (route.RouteType)
        {
            case NavRouteType.Circular:
                _settings.ActiveNavIndex = (_settings.ActiveNavIndex + 1) % route.Points.Count;
                break;

            case NavRouteType.Linear:
                _settings.ActiveNavIndex = NextLinearIndex(_settings.ActiveNavIndex, route.Points.Count);
                break;

            case NavRouteType.Once:
                _settings.ActiveNavIndex++;
                if (_settings.ActiveNavIndex >= route.Points.Count)
                    CompleteOnceRoute(route);
                break;

            case NavRouteType.Follow:
                _settings.ActiveNavIndex = 0;
                break;
        }
    }

    /// <summary>
    /// One step along a Linear route, turning round at either end. A one-point route
    /// stays on its point: bouncing used to leave the index at -1, and nav then stood
    /// still for good instead of walking back to the point after a fight.
    /// </summary>
    private int NextLinearIndex(int cur, int count)
    {
        int n = cur + _linearDir;
        if (n < 0 || n >= count)
        {
            _linearDir = -_linearDir;
            n          = cur + _linearDir;
        }
        if (n < 0 || n >= count) n = Math.Clamp(cur, 0, Math.Max(0, count - 1));
        return n;
    }

    /// <summary>
    /// A finite (Once) route ran off its last point: stop and disable nav, but leave
    /// the points intact. This used to <c>Points.Clear()</c> the in-memory route, so
    /// re-enabling nav was a silent no-op until the profile was reloaded
    /// (2026-06-03 audit P2). The index is left past the end, which marks the route
    /// finished (<see cref="LegacyUiSettings.NavRouteFinished"/>): it stays finished
    /// over a macro stop/start and a meta turning nav on, and runs again when it is
    /// loaded again, edited, or restarted (/ra nav restart, or the player's Nav switch /
    /// Start Navigation button, <see cref="LegacyUiSettings.RearmFinishedNavRoute"/>).
    /// </summary>
    private void CompleteOnceRoute(NavRouteParser route)
    {
        _settings.EnableNavigation = false;
        StopMovement();
        _host.Log($"Nav: Once route complete ({route.Points.Count} pts) — nav disabled, route retained (load it again or /ra nav restart to re-run)");
    }

    private void HandleRouteEnd(NavRouteParser route)
    {
        if (route.RouteType == NavRouteType.Circular)
        {
            _settings.ActiveNavIndex = 0;
            return;
        }

        // Nav was switched on over a finished Once route (CompleteOnceRoute leaves the
        // index past the end). This used to rewind it to [0], so anything that turned nav
        // on re-ran the route: a meta's "/vt opt set enablenav true" replayed by a macro
        // start, an expression. It stays finished, as a finished VTank Once route (its
        // points used up) does; the player's Nav switch / Start Navigation button and
        // /ra nav restart rewind it before nav gets here. Nav goes off again so the
        // arbiter doesn't hold "Navigating" (above combat with BoostNavPriority) for a
        // route with nothing left to walk.
        if (_settings.NavRouteFinished())
        {
            _settings.EnableNavigation = false;
            StopMovement();
            _host.Log($"Nav: the Once route is finished ({route.Points.Count} pts) — nav stays off; load it again or /ra nav restart to re-run it");
            return;
        }

        // A Linear route never ends; an index off either end (a route edit, a type
        // change after a finished Once run) used to stop nav here for good. Carry on
        // from the end it fell off, heading back.
        if (route.RouteType == NavRouteType.Linear && route.Points.Count > 0)
        {
            int from = _settings.ActiveNavIndex;
            bool offStart = from < 0;
            _settings.ActiveNavIndex = offStart ? 0 : route.Points.Count - 1;
            _linearDir = offStart ? 1 : -1;
            _host.Log($"Nav: Linear route index {from} was off the route - continuing from [{_settings.ActiveNavIndex}]");
            return;
        }

        StopMovement();
    }

    private bool _campHoldAnnounced;

    /// <summary>
    /// True for a looping (Circular/Linear) route made only of plain waypoints that are
    /// all within arrival range of the character: there is nowhere to advance to.
    /// </summary>
    private bool IsCampRoute(NavRouteParser route, double ns, double ew)
    {
        if (route.RouteType != NavRouteType.Circular && route.RouteType != NavRouteType.Linear) return false;
        foreach (var p in route.Points)
        {
            if (!NavRouteParser.IsPlainWaypoint(p.Type)) return false;
            if (NavYd(p.NS - ns, p.EW - ew) >= ArrivalYards) return false;
        }
        return true;
    }

    private void HoldAtCampRoute(int idx, double dist, NavRouteParser route)
    {
        if (_isMovingForward || _isTurning) StopMovement();
        _closestDist = double.MaxValue;
        UpdateStatusLine(idx, dist, route, 0.0);
        if (!_campHoldAnnounced)
        {
            _campHoldAnnounced = true;
            _host.Log($"Nav: at the route's only spot ([{idx}], {route.Points.Count} pt(s) within {ArrivalYards:F1}yd) - holding here");
        }
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
        if (!_inPause || _pauseIdx != _settings.ActiveNavIndex)
        {
            _inPause    = true;
            _pauseIdx   = _settings.ActiveNavIndex;
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
            // Through the plugin's chat path, as if typed: /ra, /ub, /mt run here and
            // anything else goes to the game. InvokeChatParser alone never reached
            // the plugin's commands, and plain text was only echoed locally.
            if (ChatSubmit != null)
            {
                if (cmd.Length > 0) ChatSubmit(cmd);
            }
            else if (cmd.StartsWith("/") && _host.HasInvokeChatParser)
                _host.InvokeChatParser(cmd);
            else
                _host.WriteToChat(cmd, 0);

            // The command loaded another route ("/vt nav load next", "/ra nav load x", a
            // meta EmbedNav): that route and the start point its loader picked take over.
            // Advancing the old route here wrote old-index + 1 onto the new route, and a
            // Once route ending in such a chat point was "completed", switching nav off.
            if (!ReferenceEquals(_settings.CurrentRoute, route))
            {
                _portalState = PortalState.None;
                _host.Log($"Nav: chat waypoint '{cmd}' loaded another route - it starts at [{_settings.ActiveNavIndex}]");
                return;
            }
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
        _host.CastSpellFor(target, pt.SpellId, "Nav", $"recall spell {pt.SpellId}");
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

        // A use the engine did not send (UseObject said no) is not a use: false lets the caller ask again.
        if (!_host.UseFor((uint)bestId, "Nav", $"portal/NPC waypoint '{target}'"))
        {
            _host.Log($"Nav: UseObject portal '{target}' (0x{bestId:X8}) was not sent - will retry");
            return false;
        }
        // Publish the resolved object so the marker renderer can draw a ring +
        // line to the portal's real position (the waypoint coord is a placeholder).
        _settings.ActivePortalObjId = (uint)bestId;
        _portalUseDistYd = bestDist;
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
                ResetPortalState();
                // No teleport. The waypoints after a recall/portal are normally on the far
                // side, so walking on would cross the world (or head for a dungeon from
                // the landscape). Only carry on when the next waypoint is close by (the
                // teleport happened but wasn't seen, or the route stays on this side).
                if (NextWalkTargetBeyond(route, _settings.ActiveNavIndex, OffTrackYards, out int farIdx, out double farYd))
                {
                    string what = pt.Type == NavPointType.Recall ? $"the recall ({NavPoint.RecallName(pt.SpellId)})" : $"the portal '{pt.TargetName}'";
                    string msg = $"Nav: {what} didn't teleport within {ActionTimeoutMs / 1000:F0}s, and the next waypoint [{farIdx}] is {farYd:F0}yd away - navigation stopped.";
                    _host.Log(msg);
                    _host.WriteToChat(msg, 1);
                    _settings.EnableNavigation = false;
                    _settings.NavStatusLine = "Nav: stopped - the teleport didn't happen";
                    StopMovement();
                    return;
                }
                _host.Log("Nav: portal/recall global timeout, advancing.");
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
                _host.SetAutoRunBy("Nav", false);
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

                // A PortalNPC's UseObject walks the character to the portal first. That
                // walk can cross a landblock line or cover 50 yd; read as a teleport, the
                // settle's StopCompletely cancelled it and nav advanced to the far side of
                // a portal it never went through. While the portal is still in view and no
                // farther than when it was used, a move is the walk (portal space still counts).
                if (!portalExited && (positionChanged || landblockChanged)
                    && pt.Type == NavPointType.PortalNPC && StillWalkingToPortal())
                {
                    positionChanged  = false;
                    landblockChanged = false;
                }

                if (portalExited || positionChanged || landblockChanged)
                {
                    int busyNow = _host.HasGetBusyState ? _host.GetBusyState() : -1;
                    _host.Log($"Nav: teleport detected (portalExit={portalExited} posChange={positionChanged} lbChange={landblockChanged}) busyState={busyNow}");
                    _portalState      = PortalState.PostTeleportSettle;
                    _portalStateStart = Now;
                    _teleportedRoute  = route;
                    _teleportedIdx    = _settings.ActiveNavIndex;
                    _firedRoute       = null;
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
                _host.SetAutoRunBy("Nav", false);
                ClearTurnMotions();
                if (_host.HasStopCompletely) _host.StopCompletelyBy("Nav");
                _isMovingForward = false;
                _isTurning       = false;

                if (Now - _portalStateStart > (long)PostTeleportMs)
                {
                    int busyAfter = _host.HasGetBusyState ? _host.GetBusyState() : -1;
                    _host.Log($"Nav: PostTeleportSettle done, busyState={busyAfter}");
                    // Force-clear the client's internal busy count (hourglass cursor)
                    // and our tracked busy count. Portal teleport interrupts actions
                    // without firing the matching DecrementBusyCount callback.
                    if (_host.HasStopCompletely) _host.StopCompletelyBy("Nav");
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
                    _teleportedRoute = null;
                    ResetPortalState();
                    Advance(route);
                }
                break;
        }
    }

    private double _portalUseDistYd = double.MaxValue; // distance to the portal when UseObject was sent

    /// <summary>
    /// True while a PortalNPC's UseObject has fired and the portal object is still in
    /// view, no farther than it was when used (plus a little): the character is on its
    /// way to the portal, not through it. After a teleport the portal is gone or far off.
    /// </summary>
    private bool StillWalkingToPortal()
    {
        if (!_portalNpcFired || _objectCache == null) return false;
        uint portalId = _settings.ActivePortalObjId;
        if (portalId == 0 || !double.IsFinite(_portalUseDistYd) || _portalUseDistYd > 1000.0) return false;
        int pid = unchecked((int)(_playerId != 0 ? _playerId : (uint)_host.GetPlayerId()));
        if (pid == 0) return false;
        double d = _objectCache.Distance(pid, unchecked((int)portalId));
        return d <= _portalUseDistYd + 5.0;
    }

    /// <summary>
    /// A Recall/PortalNPC waypoint whose teleport was already confirmed, but whose
    /// post-teleport settle was cut short (combat or looting at the arrival spot,
    /// buffing, a door, AutoVendor/AutoTrade all reset the portal state): the teleport
    /// counts, so move on. Firing it again recast the recall from the destination, or
    /// hunted the destination for the portal and used it back.
    /// </summary>
    private bool TeleportAlreadyDone(NavRouteParser route, int idx)
    {
        if (_portalState != PortalState.None) return false;
        bool confirmed = _teleportedRoute != null
                         && ReferenceEquals(_teleportedRoute, route) && _teleportedIdx == idx;
        _teleportedRoute = null;
        double movedYd = 0;
        bool unseen = !confirmed && FiredTeleportHappened(route, idx, out movedYd);
        _firedRoute = null;   // one look only, on the first return to the waypoint
        if (!confirmed && !unseen) return false;

        _host.Log(confirmed
            ? $"Nav: [{idx}] already teleported before an interruption - moving on instead of firing it again"
            : $"Nav: [{idx}] the recall/portal went off while nav was held (moved {movedYd:F0} yd) - moving on instead of firing it again");
        _hasGoodHeading = false;
        _lastTurnDir    = 0;
        _postTeleport   = true;
        _watchdogNs     = double.NaN;
        _watchdogEw     = double.NaN;
        _watchdogNext   = Now + (long)WatchdogMs;
        Advance(route);
        return true;
    }

    /// <summary>
    /// Called just before a hold resets the portal state. If the recall was already cast
    /// (or the portal used) and nav hasn't seen the teleport yet, remember where it was
    /// fired from, so the return to this waypoint can tell whether it went off meanwhile.
    /// </summary>
    private void RememberFiredPortalAction()
    {
        if (_portalState != PortalState.FiringAction || _lastRoute == null) return;
        int idx = _settings.ActiveNavIndex;
        if (!IndexValid(idx, _lastRoute) || double.IsNaN(_prePortalNS)) return;
        var type = _lastRoute.Points[idx].Type;
        bool fired = (type == NavPointType.Recall && _lastRecallCastAt != 0)
                     || (type == NavPointType.PortalNPC && _portalNpcFired);
        if (!fired) return;

        _firedRoute = _lastRoute;
        _firedIdx   = idx;
        _firedNS    = _prePortalNS;
        _firedEW    = _prePortalEW;
        _firedCell  = _host.HasGetPlayerPose
                      && _host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _)
                      ? cell : 0u;
        _firedPortalId        = _settings.ActivePortalObjId;
        _firedPortalUseDistYd = _portalUseDistYd;
    }

    /// <summary>
    /// Did the remembered recall/portal teleport the character while nav was held?
    /// Only a move no fight or use-walk makes counts: over 250 yd, or into or out of a
    /// dungeon (a different landblock with an indoor cell at either end). For a portal,
    /// the portal must also be out of reach, so an interrupted use-walk isn't taken for
    /// the trip. When unsure, the answer is no and the waypoint fires as before.
    /// </summary>
    private bool FiredTeleportHappened(NavRouteParser route, int idx, out double movedYd)
    {
        movedYd = 0;
        if (_firedRoute == null || !ReferenceEquals(_firedRoute, route) || _firedIdx != idx) return false;
        if (!TryGetPos(out double ns, out double ew) || double.IsNaN(_firedNS)) return false;
        double dNS = ns - _firedNS, dEW = ew - _firedEW;
        movedYd = Math.Sqrt(dNS * dNS + dEW * dEW) * 240.0;

        uint cellNow = _host.HasGetPlayerPose
                       && _host.TryGetPlayerPose(out uint c, out _, out _, out _, out _, out _, out _, out _)
                       ? c : 0u;
        static bool Indoor(uint cell) => (cell & 0xFFFF) >= 0x100;
        bool dungeonHop = _firedCell != 0 && cellNow != 0
                          && (cellNow >> 16) != (_firedCell >> 16)
                          && (Indoor(cellNow) || Indoor(_firedCell));
        if (movedYd <= 250.0 && !dungeonHop) return false;

        if (route.Points[idx].Type == NavPointType.PortalNPC)
        {
            if (_firedPortalId == 0 || _objectCache == null) return false;
            int pid = unchecked((int)(_playerId != 0 ? _playerId : (uint)_host.GetPlayerId()));
            if (pid == 0) return false;
            double d = _objectCache.Distance(pid, unchecked((int)_firedPortalId));
            bool outOfReach = d == double.MaxValue
                              || (double.IsFinite(_firedPortalUseDistYd) && d > _firedPortalUseDistYd + 5.0);
            if (!outOfReach) return false;
        }
        return true;
    }

    // ── Fellowship-follow ────────────────────────────────────────────────────
    // Follow stops within the nav point reach (FollowNavMin, VTank's "Follow/Nav Min Distance",
    // which sets both in VTank too) and moves again once the leader is this much farther away.
    private const double FollowResumeExtraYd = 3.0;
    private double FollowArrivalYd => ArrivalYards;
    private double FollowResumeYd  => ArrivalYards + FollowResumeExtraYd;
    private bool _followMoving;

    /// <summary>
    /// Steer toward the LIVE position of _settings.FollowTargetId (the fellowship
    /// leader). Self-contained — does NOT touch the route steering. Faces the
    /// target and autoruns when beyond FollowResumeYd; stops within FollowArrivalYd.
    /// </summary>
    /// <summary>
    /// The follow nav's character: its saved id while that object is around, else
    /// found by name (ids from another session can differ).
    /// </summary>
    private uint ResolveFollowTarget(NavRouteParser route)
    {
        if (route.FollowTargetId != 0 && _host.HasGetObjectPosition
            && _host.TryGetObjectPosition(route.FollowTargetId, out _, out _, out _, out _))
            return route.FollowTargetId;
        uint byName = FindObjectByName?.Invoke(route.FollowTargetName) ?? 0;
        if (byName != 0) route.FollowTargetId = byName;
        return byName;
    }

    /// <summary>Finds a nearby object's id by exact name (set by the plugin).</summary>
    public Func<string, uint>? FindObjectByName;

    /// <summary>Performs a Jump waypoint: face heading, jump with power ms (set by the plugin).</summary>
    public Action<float, bool, int>? Jump;

    private void FireJump(NavPoint pt, int idx)
    {
        StopMovement();
        float heading = (float)(((pt.JumpHeading % 360.0) + 360.0) % 360.0);
        int ms = (int)Math.Clamp(Math.Round(pt.JumpMs), 0, 1000);
        _host.Log($"Nav: jump at [{idx}] heading={heading:F0} hold={ms}ms shift={pt.JumpShift}");
        if (Jump != null) Jump(heading, pt.JumpShift, ms);
        else _host.JumpNonAutonomous(ms / 1000f);
    }

    private void FollowTarget(uint targetId)
    {

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
            _host.TurnToHeadingBy("Nav", (float)newHeading);
            // Run only once roughly aligned, so we don't arc wide on a big turn.
            if (Math.Abs(err) <= BigTurnEnter) StartForward(); else StopForward();
        }
        else
        {
            _host.TurnToHeadingBy("Nav", (float)desiredDeg);
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

    private void UpdateWatchdog(NavRouteParser route)
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
                BeginRecovery(route);
            }
            else
            {
                _stuckCount = 0;
            }
        }
        _watchdogNs = ns;
        _watchdogEw = ew;
    }

    private void BeginRecovery(NavRouteParser route)
    {
        // The waypoint is in another area: no detour or escape can reach it, and
        // AreaMismatchHold is holding (or walking into a portal). Don't jump about.
        if (RecoveryEnabled && _areaMismatch)
        {
            _stuckCount = 0;
            return;
        }

        _settings.NavIsStuck = true;
        StopMovement();

        // Escalation ladder, keyed on how many consecutive watchdog windows have
        // passed without real movement. Each rung is tried once per stuck streak;
        // any real movement resets _stuckCount and puts us back on rung 1.
        if (_stuckCount >= StuckRestartAfter)
        {
            // Nothing shook us loose. Waypoints are never skipped (2026-09-28):
            // start the ladder over (jump, side-steps, back-out) on the same one.
            _host.Log($"Nav: stuck x{_stuckCount} at [{_settings.ActiveNavIndex}] — still stuck, retrying the escapes.");
            _stuckCount = 1;
        }

        // Route recovery: a detour that snagged has failed (the ladder takes this
        // window); otherwise, once the jump rung has failed (or this spot keeps
        // snagging us), plan a way around instead of side-stepping blindly.
        NoteStuckSpot();
        if (_detour != null)
            EndDetour("stuck on the detour", success: false);
        else if (TryStuckDetour(route))
            return;

        // Rungs 2+ need a heading we can command. Without TurnToHeading the only
        // primitive we have is the jump, so stay on rung 1 rather than autorunning
        // blindly into whatever we happen to be facing.
        if (_stuckCount <= 1 || !_host.HasTurnToHeading || !TryGetQuaternionHeading(out float curDeg))
        {
            _recoveryKind  = RecoveryKind.Jump;
            _recoveryUntil = Now + (long)RecoveryMs;
            _inRecovery    = true;
            _host.JumpNonAutonomous(0.5f);
            return;
        }

        double offset = _stuckCount switch
        {
            2 => +SideStepDeg,
            3 => -SideStepDeg,
            _ => BackOutDeg,
        };

        double escapeHeading = curDeg + offset;
        if (escapeHeading >= 360.0) escapeHeading -= 360.0; else if (escapeHeading < 0.0) escapeHeading += 360.0;

        _host.Log($"Nav: stuck x{_stuckCount} — escape burst {offset:+0;-0}° for {RecoveryBurstMs:F0}ms.");
        _host.TurnToHeadingBy("Nav", (float)escapeHeading);
        StartForward();
        _recoveryKind  = RecoveryKind.Escape;
        _recoveryUntil = Now + (long)RecoveryBurstMs;
        _inRecovery    = true;

        // The escape moves us off the approach line, so the closest-approach
        // tracker's last reading is meaningless — reset it or the next steer
        // tick reads the retreat as a sweep-pass and advances the index.
        _closestDist = double.MaxValue;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ROUTE RECOVERY (pathfinder detours, off-track, area hold)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Waypoint types nav walks to (the rest fire in place).</summary>
    private static bool IsWalkTarget(NavPointType t) => NavRouteParser.IsPlainWaypoint(t) || t == NavPointType.Jump;

    private static double NavYd(double dNs, double dEw) => Math.Sqrt(dNs * dNs + dEw * dEw) * 240.0;

    private static double NavToWorld(double navCoord) => (navCoord * 10.0 + 1019.5) * 24.0;

    /// <summary>Drops any detour and the per-waypoint recovery bookkeeping (route swap, teleport, reset).</summary>
    private void ResetRecovery(string reason)
    {
        if (_detour != null) EndDetour($"cancelled, {reason}", success: false);
        _detourAttempts       = 0;
        _detourGaveUpNotified = false;
        _stuckSpotNs          = double.NaN;
        _stuckSpotEw          = double.NaN;
        _stuckSpotHits        = 0;
        _offTrackArmed        = false;
        _offTrackSince        = 0;
        _areaRoute            = null;
        _areaIdx              = -1;
        _areaPlayerLb         = uint.MaxValue;
        _areaRetryAt          = 0;
        _areaMismatch         = false;
        // A new route, a teleport or a macro start: a hold that comes back says so again (a route
        // loaded again over the same hold used to change nothing the player could see, 2026-11-01).
        _areaAnnounced        = string.Empty;
        _areaScanRoute        = null;
        _areaScanNone         = false;
        _settings.NavAreaHoldOffersPatrol = false;
    }

    private string DescribeRecovery()
    {
        if (_areaMismatch) return "area hold: " + _areaMessage;
        var d = _detour;
        if (d != null)
            return $"detour #{d.Number} point {Math.Min(d.Index + 1, d.Points.Count)}/{d.Points.Count} -> [{d.RejoinIdx}] via {d.Planner}{(d.Paused ? " (paused)" : "")}";
        return _detourAttempts > 0 ? $"none ({_detourAttempts} detour attempt(s) at this waypoint)" : "none";
    }

    // ── Area hold ────────────────────────────────────────────────────────────

    /// <summary>
    /// True (and nav held still) while the current waypoint is in a different area from
    /// the character: another dungeon, or dungeon vs landscape. No detour or escape can
    /// join those, so nothing is attempted; one chat line per mismatch. The check is
    /// recomputed only when the waypoint, the route or the character's landblock/cell
    /// kind changes, and resumes on its own when they match again (e.g. after a portal).
    ///
    /// Three cases (2026-11-01):
    ///   - At the route's own way out (the character stands at the last waypoint before the
    ///     change, a walk-in portal): walk on through it for a few seconds, then hold.
    ///   - Anywhere else (the character got here by a portal, a recall, a death, or loaded the
    ///     route here): the route is looked along, in the order it is walked, for the first
    ///     waypoint in the character's area, and nav carries on from there. A Circular route
    ///     whose loop starts with a recall cast in this dungeon goes on to that recall.
    ///   - No waypoint of the route is here: hold, and say once what to do (load a route for
    ///     here; in a dungeon, or press Patrol).
    /// Nothing is ever walked in an area its waypoint isn't in.
    /// </summary>
    private bool AreaMismatchHold(NavRouteParser route, int idx, NavPoint pt)
    {
        if (_planner == null || !_host.HasGetPlayerPose) return false;
        if (!_host.TryGetPlayerPose(out uint cell, out _, out _, out _, out _, out _, out _, out _)) return _areaMismatch;

        uint lb = cell >> 16;
        bool inCell = (cell & 0xFFFF) >= 0x0100;
        bool changed = !ReferenceEquals(route, _areaRoute) || idx != _areaIdx || lb != _areaPlayerLb || inCell != _areaPlayerInCell;
        if (changed || (_areaRetryAt != 0 && Now >= _areaRetryAt))
        {
            _areaRoute        = route;
            _areaIdx          = idx;
            _areaPlayerLb     = lb;
            _areaPlayerInCell = inCell;
            _areaRetryAt      = 0;

            bool was = _areaMismatch;
            var result = _planner.CheckArea(cell, pt.NS, pt.EW, out string routeArea, out string playerArea);
            if (result == NavAreaCheck.Unknown) _areaRetryAt = Now + 2000;   // dats still loading
            _areaMismatch = result == NavAreaCheck.Mismatch;
            // A hold looks again every few seconds: held still, nothing else changes, so a
            // wrong answer (a pose read mid-teleport) would otherwise hold nav for good.
            if (_areaMismatch) _areaRetryAt = Now + AreaRecheckMs;

            if (_areaMismatch)
            {
                if (!was) _areaMismatchSince = Now;
                _areaRouteName  = routeArea;
                _areaPlayerName = playerArea;
                _areaMessage = $"Nav: this route is in a different area ({routeArea}) - you are {playerArea}. Navigation stopped.";
                if (_detour != null) EndDetour("the route is in a different area", success: false);
            }
            else if (was)
            {
                _areaAnnounced = string.Empty;
                _settings.NavAreaHoldOffersPatrol = false;
                _host.Log($"Nav: the route's area matches again at waypoint [{idx}] - navigation resumes");
            }
        }

        if (!_areaMismatch) return false;

        if (AtAreaBoundary(route, idx))
        {
            // The route's own way out: walk on through for the grace, then hold (the portal didn't fire).
            if (Now - _areaMismatchSince < (long)AreaGraceMs && AreaGraceWalk(route, idx))
                return true;
        }
        else if (TryCarryOnHere(route, idx, cell, lb, inCell))
        {
            return true;
        }
        else if (_areaScanNone)
        {
            // Nothing of this route is here: say what to do instead of only "stopped".
            _areaMessage = $"Nav: this route is in a different area ({_areaRouteName}) - you are {_areaPlayerName}, and nothing ahead on it is here. "
                         + (inCell ? "Navigation is waiting: load a route for this dungeon, or press Patrol to patrol it."
                                   : "Navigation is waiting: load a route for here.");
            _settings.NavAreaHoldOffersPatrol = inCell;
        }

        // Hold: one chat line per mismatch.
        if (_areaMessage != _areaAnnounced)
        {
            _areaAnnounced = _areaMessage;
            _host.WriteToChat(_areaMessage, 1);
            _host.Log($"{_areaMessage} [waypoint {idx}, cell 0x{cell:X8}]");
        }
        StopMovement();
        _settings.NavIsStuck    = true;
        _settings.NavStatusLine = _areaScanNone
            ? $"Nav: this route is for {_areaRouteName} - load a route for here{(inCell ? " or press Patrol" : "")}"
            : "Nav: route is in a different area - stopped";
        return true;
    }

    /// <summary>
    /// The character stands at (within AreaGraceNearYd of) the walk point just before
    /// <paramref name="idx"/>: the route's own way into the other area (a walk-in portal there).
    /// </summary>
    private bool AtAreaBoundary(NavRouteParser route, int idx)
    {
        int prev = PrevIndex(idx, route);
        if (prev < 0 || !IsWalkTarget(route.Points[prev].Type)) return false;
        if (!TryGetPos(out double ns, out double ew)) return false;
        var p = route.Points[prev];
        return NavYd(p.NS - ns, p.EW - ew) <= AreaGraceNearYd;
    }

    /// <summary>
    /// The route looked along from <paramref name="idx"/> in the order it is walked (a Circular
    /// route round to the point before it, a Linear route to its end and back, a Once route to
    /// its end) for the first waypoint in the character's area, of any kind (a recall cast here,
    /// a portal used here, a point). Found: nav carries on from it, said once in chat, and true.
    /// Not found: false, remembered (<see cref="_areaScanNone"/>) until the route or the
    /// character's area changes.
    /// </summary>
    private bool TryCarryOnHere(NavRouteParser route, int idx, uint cell, uint lb, bool inCell)
    {
        if (ReferenceEquals(route, _areaScanRoute) && lb == _areaScanLb && inCell == _areaScanInCell && _areaScanNone)
            return false;
        _areaScanRoute  = route;
        _areaScanLb     = lb;
        _areaScanInCell = inCell;
        _areaScanNone   = false;

        // Only a clear Match counts: the dats are loaded by now (the current waypoint's check said
        // Mismatch), so Unknown means a point off the map, which is nowhere to carry on from.
        int found = -1, foundDir = _linearDir;
        foreach (var (k, dir) in WalkOrderAfter(route, idx))
        {
            var p = route.Points[k];
            if (_planner!.CheckArea(cell, p.NS, p.EW, out _, out _) != NavAreaCheck.Match) continue;
            found = k; foundDir = dir;
            break;
        }
        if (found < 0)
        {
            _areaScanNone = true;
            return false;
        }

        var to = route.Points[found];
        string msg = $"Nav: waypoint [{idx}] is in another area ({_areaRouteName}); [{found}] {to} is here ({PlaceName(_areaPlayerName)}) - carrying on from [{found}].";
        _host.WriteToChat(msg, 1);
        _host.Log($"{msg} [cell 0x{cell:X8}]");

        _settings.ActiveNavIndex = found;
        if (route.RouteType == NavRouteType.Linear) _linearDir = foundDir;
        _areaMismatch    = false;
        _areaAnnounced   = string.Empty;
        _settings.NavIsStuck = false;
        _settings.NavAreaHoldOffersPatrol = false;
        _settings.NavStatusLine = $"Nav: carrying on from [{found}], the route's next waypoint here";
        _closestDist     = double.MaxValue;
        _legStartNs      = double.NaN;
        _legStartEw      = double.NaN;
        _watchdogNs      = double.NaN;
        _watchdogEw      = double.NaN;
        _stuckCount      = 0;
        StopMovement();
        return true;
    }

    /// <summary>
    /// The waypoints after <paramref name="idx"/> in the order nav walks them, each with the Linear
    /// direction it is reached in. At most one lap; <paramref name="idx"/> itself is not included.
    /// </summary>
    private IEnumerable<(int Idx, int Dir)> WalkOrderAfter(NavRouteParser route, int idx)
    {
        int n = route.Points.Count;
        switch (route.RouteType)
        {
            case NavRouteType.Circular:
                for (int s = 1; s < n; s++) yield return ((idx + s) % n, 1);
                break;
            case NavRouteType.Once:
                for (int k = idx + 1; k < n; k++) yield return (k, 1);
                break;
            case NavRouteType.Linear:
                int d = _linearDir >= 0 ? 1 : -1;
                for (int k = idx + d; k >= 0 && k < n; k += d) yield return (k, d);
                // ... then back from the far end: the points behind idx, reached heading the other way.
                for (int k = idx - d; k >= 0 && k < n; k -= d) yield return (k, -d);
                break;
        }
    }

    /// <summary>"in dungeon 0x5756" / "on the landscape" (CheckArea's wording) as a place: "dungeon 0x5756".</summary>
    private static string PlaceName(string playerArea)
        => playerArea.StartsWith("in ", StringComparison.Ordinal) || playerArea.StartsWith("on ", StringComparison.Ordinal) ? playerArea[3..] : playerArea;

    /// <summary>
    /// Mismatch grace: when the character is at (within AreaGraceNearYd of) the last
    /// waypoint before the area change, keep walking a few yards on through it along the
    /// route's incoming direction, so a walk-in portal there can fire. False when that
    /// doesn't apply (the previous point is an action point, or far away).
    /// </summary>
    private bool AreaGraceWalk(NavRouteParser route, int idx)
    {
        int prev = PrevIndex(idx, route);
        if (prev < 0 || !IsWalkTarget(route.Points[prev].Type)) return false;
        if (!TryGetPos(out double ns, out double ew)) return false;
        var p = route.Points[prev];
        if (NavYd(p.NS - ns, p.EW - ew) > AreaGraceNearYd) return false;

        double aNS = p.NS, aEW = p.EW;
        int pp = PrevIndex(prev, route);
        if (pp >= 0 && pp != idx && IsWalkTarget(route.Points[pp].Type))
        {
            var q = route.Points[pp];
            double len = NavYd(p.NS - q.NS, p.EW - q.EW);
            if (len > 0.5)
            {
                double k = AreaGraceOverYd / len;
                aNS = p.NS + (p.NS - q.NS) * k;
                aEW = p.EW + (p.EW - q.EW) * k;
            }
        }

        _settings.NavStatusLine = "Nav: next waypoint is in another area - walking on through the last one";
        double dist = NavYd(aNS - ns, aEW - ew);
        if (dist < 1.0) { StopMovement(); return true; }   // wait out the grace in place
        double error = HeadingErrorTo(aNS, aEW, ns, ew, out double currentDeg);
        DriveHeading(error, currentDeg, dist);
        return true;
    }

    // ── Off-track ────────────────────────────────────────────────────────────

    /// <summary>
    /// Only called while nav owns movement. Being far from the route is "off track" only
    /// if nav got there by itself: every pause (combat, looting, buffing, doors, metas)
    /// disarms the check, and it re-arms once the character is back within the limit.
    /// So a return from a long meta approach (up to ~159 yd) just heads back normally.
    /// </summary>
    private void CheckOffTrack(NavRouteParser route, int idx, double ns, double ew)
    {
        double off = DistanceToSegmentYd(route, idx, ns, ew);
        if (off <= OffTrackYards)
        {
            _offTrackArmed = true;
            _offTrackSince = 0;
            return;
        }
        if (!_offTrackArmed) return;
        if (_offTrackSince == 0) { _offTrackSince = Now; return; }
        if (Now - _offTrackSince < (long)OffTrackGraceMs) return;

        // One recovery per excursion; the check re-arms when we're back within the limit.
        _offTrackArmed = false;
        _offTrackSince = 0;
        TryStartDetour(route, idx, $"off track, {off:F0}yd from the route for {OffTrackGraceMs / 1000:F0}s");
    }

    /// <summary>Yards from the character to the leg that ends at waypoint <paramref name="idx"/>.</summary>
    private double DistanceToSegmentYd(NavRouteParser route, int idx, double ns, double ew)
    {
        var b = route.Points[idx];
        int prev = PrevIndex(idx, route);
        if (prev < 0 || !IsWalkTarget(route.Points[prev].Type))
            return NavYd(b.NS - ns, b.EW - ew);

        var a = route.Points[prev];
        double abN = b.NS - a.NS, abE = b.EW - a.EW;
        double len2 = abN * abN + abE * abE;
        double t = len2 < 1e-12 ? 0.0 : Math.Clamp(((ns - a.NS) * abN + (ew - a.EW) * abE) / len2, 0.0, 1.0);
        return NavYd(a.NS + abN * t - ns, a.EW + abE * t - ew);
    }

    private int PrevIndex(int cur, NavRouteParser route)
    {
        int n = route.Points.Count;
        switch (route.RouteType)
        {
            case NavRouteType.Circular: return n > 1 ? (cur - 1 + n) % n : -1;
            case NavRouteType.Linear:
                int p = cur - _linearDir;
                return (p >= 0 && p < n) ? p : -1;
            case NavRouteType.Once: return cur - 1;
            default: return -1;
        }
    }

    /// <summary>
    /// The next waypoint in travel order without changing any state: Circular wraps,
    /// Linear stops at the end (no bounce), Once stops at the last point.
    /// </summary>
    private int NextInOrder(int cur, NavRouteParser route)
    {
        int n = route.Points.Count;
        switch (route.RouteType)
        {
            case NavRouteType.Circular: return n > 1 ? (cur + 1) % n : -1;
            case NavRouteType.Linear:
                int x = cur + _linearDir;
                return (x >= 0 && x < n) ? x : -1;
            case NavRouteType.Once: return cur + 1 < n ? cur + 1 : -1;
            default: return -1;
        }
    }

    /// <summary>
    /// True when the first waypoint nav would walk to after <paramref name="cur"/> (skipping
    /// Chat/Pause/Recall/Portal points, at most a dozen steps ahead) is farther than
    /// <paramref name="limitYd"/> from the character. False when it is near, or there is
    /// none, or our position is unknown.
    /// </summary>
    private bool NextWalkTargetBeyond(NavRouteParser route, int cur, double limitYd, out int idx, out double yd)
    {
        idx = -1;
        yd = 0;
        if (!IndexValid(cur, route) || !TryGetPos(out double ns, out double ew)) return false;
        int i = cur;
        for (int step = 0; step < 12; step++)
        {
            i = NextInOrder(i, route);
            if (i < 0 || i == cur) return false;
            var p = route.Points[i];
            if (!IsWalkTarget(p.Type)) continue;
            idx = i;
            yd = NavYd(p.NS - ns, p.EW - ew);
            return yd > limitYd;
        }
        return false;
    }

    /// <summary>
    /// Where to rejoin the route: the waypoint nearest the character among the current
    /// target and the few just after it (in travel order), never behind it, never past a
    /// Portal/Recall/Chat/Pause point, and never more than RejoinLookaheadYards of route
    /// ahead, so a rejoin can pass over at most a short stretch of dense waypoints.
    /// </summary>
    private int PickRejoinIndex(NavRouteParser route, int idx, double ns, double ew, out int skipped)
    {
        skipped = 0;
        int best = idx;
        double bestD = NavYd(route.Points[idx].NS - ns, route.Points[idx].EW - ew);
        // A Jump point may be the rejoin target but is never passed over: the jump only
        // fires on arrival, and the point past it is across the gap or up the ledge.
        if (route.Points[idx].Type == NavPointType.Jump) return best;
        int cur = idx;
        double along = 0;
        for (int step = 1; step <= RejoinLookaheadPts; step++)
        {
            int next = NextInOrder(cur, route);
            if (next < 0 || next == idx) break;
            var np = route.Points[next];
            if (!IsWalkTarget(np.Type)) break;
            var cp = route.Points[cur];
            along += NavYd(np.NS - cp.NS, np.EW - cp.EW);
            if (along > RejoinLookaheadYards) break;
            double d = NavYd(np.NS - ns, np.EW - ew);
            if (d < bestD - 0.5) { best = next; bestD = d; skipped = step; }
            if (np.Type == NavPointType.Jump) break;
            cur = next;
        }
        return best;
    }

    // ── Starting / ending a detour ───────────────────────────────────────────

    private void NoteStuckSpot()
    {
        if (!TryGetPos(out double ns, out double ew)) return;
        if (!double.IsNaN(_stuckSpotNs) && NavYd(ns - _stuckSpotNs, ew - _stuckSpotEw) <= StuckSpotYards)
        {
            _stuckSpotHits++;
        }
        else
        {
            _stuckSpotNs   = ns;
            _stuckSpotEw   = ew;
            _stuckSpotHits = 1;
        }
    }

    /// <summary>
    /// From the stuck ladder: after the cheap jump rung has failed (stuck x2), or at the
    /// first stuck when this spot has already snagged us several times.
    /// </summary>
    private bool TryStuckDetour(NavRouteParser route)
    {
        if (!RecoveryEnabled || _planner == null || _areaMismatch) return false;
        bool repeatSpot = _stuckSpotHits >= StuckSpotRepeat;
        if (_stuckCount != 2 && !(repeatSpot && _stuckCount == 1)) return false;

        int idx = _settings.ActiveNavIndex;
        if (!IndexValid(idx, route) || !IsWalkTarget(route.Points[idx].Type)) return false;

        string why = _stuckCount == 1
            ? $"stuck, this spot has snagged us {_stuckSpotHits} times"
            : "stuck, the jump didn't free us";
        return TryStartDetour(route, idx, why);
    }

    private bool TryStartDetour(NavRouteParser route, int idx, string reason)
    {
        if (_planner == null) return false;
        if (_detourAttempts >= MaxDetourAttempts) { NotifyGaveUp(idx); return false; }
        if (!TryGetPos(out double ns, out double ew)) return false;

        _detourAttempts++;
        int number = ++_recoveryNumber;
        int rejoin = PickRejoinIndex(route, idx, ns, ew, out int skipped);
        string skipNote = skipped > 0 ? $" (passes over {skipped} waypoint(s))" : "";

        var plan = _planner.TryPlan(route.Points[rejoin], out string why);
        if (plan == null)
        {
            _host.Log($"Nav: recovery #{number} ({reason}) rejoin=[{rejoin}]{skipNote} planner=none - result: no plan ({why}); attempt {_detourAttempts}/{MaxDetourAttempts}, using the stuck escapes");
            if (_detourAttempts >= MaxDetourAttempts) NotifyGaveUp(idx);
            return false;
        }

        long now = Now;
        _detour = new Detour
        {
            Number     = number,
            Reason     = reason,
            Planner    = plan.Planner,
            Points     = plan.Points,
            StartIdx   = idx,
            RejoinIdx  = rejoin,
            Skipped    = skipped,
            LengthYd   = plan.LengthYd,
            Doorways   = plan.Doorways,
            StartedAt  = now,
            // Generous: ~0.6 s per yard (a slow walk) plus room for a door or two.
            DeadlineAt = now + 15000 + (long)(plan.LengthYd * 600.0),
        };
        _host.Log($"Nav: recovery #{number} started ({reason}) rejoin=[{rejoin}]{skipNote} via {plan.Planner}: {plan.Points.Count} pts, {plan.LengthYd:F0}yd, {plan.Doorways} doorway(s); attempt {_detourAttempts}/{MaxDetourAttempts}");

        StopMovement();
        _inRecovery          = false;
        _closestDist         = double.MaxValue;
        _settings.NavIsStuck = true;
        return true;
    }

    private void NotifyGaveUp(int idx)
    {
        if (_detourGaveUpNotified) return;
        _detourGaveUpNotified = true;
        string msg = $"Nav: couldn't find a way back to waypoint {idx + 1} after {_detourAttempts} tries - using the jump and side-step escapes.";
        _host.Log(msg);
        _host.WriteToChat(msg, 1);
    }

    /// <summary>The one summary line per recovery: why, rejoin, planner, path, result.</summary>
    private void EndDetour(string result, bool success)
    {
        var d = _detour;
        if (d == null) return;
        _detour = null;

        string skipNote = d.Skipped > 0 ? $" (passes over {d.Skipped})" : "";
        _host.Log($"Nav: recovery #{d.Number} {(success ? "OK" : "FAILED")} - why: {d.Reason}; rejoin=[{d.RejoinIdx}]{skipNote}; "
                  + $"planner: {d.Planner}, {d.Points.Count} pts / {d.LengthYd:F0}yd, {d.Doorways} doorway(s); "
                  + $"reached {Math.Min(d.Index, d.Points.Count)}/{d.Points.Count}, door requests {d.DoorRequestCount}, re-plans {d.Replans}, "
                  + $"{(Now - d.StartedAt) / 1000.0:F1}s - result: {result}");

        _closestDist = double.MaxValue;
        if (!_inRecovery) _settings.NavIsStuck = false;
    }

    private void MarkDetourPaused()
    {
        var d = _detour;
        if (d == null || d.Paused) return;
        d.Paused   = true;
        d.PausedAt = Now;
        if (TryGetPos(out double ns, out double ew)) { d.PausedNs = ns; d.PausedEw = ew; }
        else { d.PausedNs = double.NaN; d.PausedEw = double.NaN; }
    }

    // ── Walking a detour ─────────────────────────────────────────────────────

    private void TickDetour(NavRouteParser route, double ns, double ew)
    {
        var d = _detour!;
        if (_settings.ActiveNavIndex != d.StartIdx || !IndexValid(d.RejoinIdx, route))
        {
            EndDetour("cancelled, the route's waypoint was changed", success: false);
            return;
        }

        if (d.Paused)
        {
            // Back from combat / looting / buffing / a door. Paused time doesn't count
            // against the deadline; if the pause moved us, plan again from here.
            d.Paused      = false;
            d.DeadlineAt += Now - d.PausedAt;
            d.ClosestDist = double.MaxValue;
            double moved  = double.IsNaN(d.PausedNs) ? 0.0 : NavYd(ns - d.PausedNs, ew - d.PausedEw);
            if (moved > DetourReplanMovedYd && !ReplanDetour(route, d, moved)) return;
        }

        if (Now > d.DeadlineAt)
        {
            EndDetour("timed out", success: false);
            return;
        }

        if (OpenDoorOnDetour(d)) return;

        var p = d.Points[d.Index];
        double dist = NavYd(p.NS - ns, p.EW - ew);
        if (dist < ReachFor(p) || (d.ClosestDist < SweepFor(p) && dist > d.ClosestDist + 0.3))
        {
            d.Index++;
            d.ClosestDist = double.MaxValue;
            if (d.Index >= d.Points.Count)
            {
                // Back on the route: resume at the rejoin waypoint; normal nav walks the last leg.
                _settings.ActiveNavIndex = d.RejoinIdx;
                EndDetour($"rejoined the route at [{d.RejoinIdx}]", success: true);
            }
            return;
        }
        d.ClosestDist = Math.Min(d.ClosestDist, dist);

        // Aim point: blend toward the following point near arrival, but less than the
        // route does (doorways are narrow).
        double tNS = p.NS, tEW = p.EW;
        double look = Math.Min(LookaheadYards, 2.0);
        if (look > 0.0 && dist < look && !p.Doorway)
        {
            var next = d.Index + 1 < d.Points.Count ? d.Points[d.Index + 1] : route.Points[d.RejoinIdx];
            double t = 1.0 - dist / look;
            tNS = Lerp(p.NS, next.NS, t);
            tEW = Lerp(p.EW, next.EW, t);
        }

        double error = HeadingErrorTo(tNS, tEW, ns, ew, out double currentDeg);
        UpdateDetourStatus(d, dist, error);
        DriveHeading(error, currentDeg, dist);
    }

    private bool ReplanDetour(NavRouteParser route, Detour d, double movedYd)
    {
        if (_planner == null || d.Replans >= DetourMaxReplans)
        {
            EndDetour($"moved {movedYd:F0}yd while paused and out of re-plans", success: false);
            return false;
        }
        var plan = _planner.TryPlan(route.Points[d.RejoinIdx], out string why);
        if (plan == null)
        {
            // Often "already in the waypoint's room": normal nav takes it from here.
            EndDetour($"moved {movedYd:F0}yd while paused, re-plan found nothing ({why})", success: false);
            return false;
        }
        d.Replans++;
        d.Points   = plan.Points;
        d.Index    = 0;
        d.Planner  = plan.Planner;
        d.LengthYd = plan.LengthYd;
        d.Doorways = plan.Doorways;
        d.DoorRequests.Clear();
        _host.Log($"Nav: recovery #{d.Number} re-planned after a pause (moved {movedYd:F0}yd): {plan.Points.Count} pts, {plan.LengthYd:F0}yd via {plan.Planner}");
        return true;
    }

    /// <summary>
    /// A closed door on the leg to the next detour point (within DetourDoorLookYd): hand
    /// it to the door controller, which opens it (lockpick if allowed and locked) before
    /// nav ticks again. At most DetourDoorMaxRequests per door; after that the stuck
    /// watchdog decides.
    /// </summary>
    private bool OpenDoorOnDetour(Detour d)
    {
        if (FindClosedDoorOnLeg == null || RequestDoorOpen == null) return false;
        if (Now < d.NextDoorCheckAt) return false;
        d.NextDoorCheckAt = Now + (long)DetourDoorCheckMs;

        if (!_host.TryGetPlayerPose(out uint cell, out float lx, out float ly, out float lz, out _, out _, out _, out _)) return false;
        double ax = ((cell >> 24) & 0xFF) * 192.0 + lx;
        double ay = ((cell >> 16) & 0xFF) * 192.0 + ly;
        var p = d.Points[d.Index];
        double bx = NavToWorld(p.EW), by = NavToWorld(p.NS);
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        if (len > DetourDoorLookYd) { bx = ax + dx / len * DetourDoorLookYd; by = ay + dy / len * DetourDoorLookYd; }

        int door = FindClosedDoorOnLeg(ax, ay, bx, by, lz, DetourDoorCorridorYd);
        if (door == 0) return false;
        d.DoorRequests.TryGetValue(door, out int asked);
        if (asked >= DetourDoorMaxRequests) return false;

        d.DoorRequests[door] = asked + 1;
        d.DoorRequestCount++;
        _host.Log($"Nav: recovery #{d.Number} - door 0x{(uint)door:X8} is closed on the detour, opening it (request {asked + 1}/{DetourDoorMaxRequests})");
        StopMovement();
        RequestDoorOpen(door);
        return true;
    }

    private void UpdateDetourStatus(Detour d, double dist, double headingErr)
    {
        _lastDistYd     = dist;
        _lastHeadingErr = headingErr;
        _lastSteerAt    = Now;
        _settings.NavIsStuck = true;

        string modeStr = _isTurning ? " [TURN]" : string.Empty;
        string errStr  = Math.Abs(headingErr) > 0.5 ? $" err={headingErr:+0.0;-0.0}°" : string.Empty;
        _settings.NavStatusLine = $"Nav: recovering - detour {d.Index + 1}/{d.Points.Count} to waypoint {d.RejoinIdx + 1}  {dist:F1}yd{errStr}{modeStr}";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MOVEMENT HELPERS
    // ══════════════════════════════════════════════════════════════════════════

    // Auto-run re-sent this often while nav wants to move. The flag alone trusted that auto-run
    // stayed on; a cast, a stance change, a corpse use or a server stop turns it off without
    // telling nav, and nav sat still until the player tapped forward (2026-10-04). Setting it
    // on when it already is costs nothing; nav only ticks while it's the active activity.
    private const long ForwardReassertMs = 1000;
    private long _lastForwardAssertMs;

    private void StartForward()
    {
        long now = Environment.TickCount64;
        if (!_isMovingForward || now - _lastForwardAssertMs >= ForwardReassertMs)
        {
            _host.SetAutoRunBy("Nav", true);
            _isMovingForward = true;
            _lastForwardAssertMs = now;
        }
    }

    private void StopForward()
    {
        _host.SetAutoRunBy("Nav", false);
        _isMovingForward = false;
    }

    private void ClearTurnMotions()
    {
        // Mode-0/legacy motion keys are local cmdinterp toggles — cheap, always clear.
        _host.SetMotionBy("Nav", MotionTurnRight, false);
        _host.SetMotionBy("Nav", MotionTurnLeft,  false);

        // Tier 1 turns are CM_Movement *server events* (0xF661). Only send a
        // StopMovement when a turn is actually in flight — this method is called
        // every tick by the pause/portal/settle/idle paths, so an unconditional
        // send floods the server. _tier1TurnDir tracks the one active direction.
        if (_tier1TurnDir != 0 && _host.HasStopMovement)
        {
            _host.StopMovementBy("Nav", _tier1TurnDir > 0 ? MotionTurnRight : MotionTurnLeft, 0);
            _tier1TurnDir = 0;
        }
    }

    // Already stopped, StopMovement sends a reminder at most this often (as StartForward re-asserts).
    private const long StopReassertMs = 1000;
    private long _lastStopSentMs;
    private bool _stopSent;

    private void StopMovement()
    {
        // The pause, chat, portal, settle and idle paths call this every tick. It sent autorun-off and
        // both turn keys each time even with the character standing still: in portal space the game
        // thread takes none of them, 137 piled up and ran 12.7 s late on the far side, against the next
        // route leg (Dargoth Hera's report, 2026-10-19). Now it sends when something moves, and a
        // reminder once a second while stopped (something else may have started the character).
        bool moving = _isMovingForward || _isTurning || _tier1TurnDir != 0;
        _isTurning = false;
        long now = Now;
        if (!moving && _stopSent && now - _lastStopSentMs < StopReassertMs) return;
        _stopSent = true;
        _lastStopSentMs = now;
        StopForward();
        ClearTurnMotions();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  STATUS
    // ══════════════════════════════════════════════════════════════════════════

    private void UpdateStatusLine(int idx, double dist, NavRouteParser route, double headingErr)
    {
        if (!_inRecovery) _settings.NavIsStuck = false;

        // Snapshot inputs — these are the numbers you actually want when nav
        // wedges, and this is the one place that has all three at once.
        _lastDistYd     = dist;
        _lastHeadingErr = headingErr;
        _lastSteerAt    = Now;

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

    // ── Shared turning for other movers (corpse approach) ───────────────────
    private long _servoLastTicks;
    private bool _servoTurning;

    /// <summary>
    /// Turns toward <paramref name="desiredDeg"/> exactly as nav does: the heading servo at
    /// NavTurnRateDegPerSec, turning in place past NavStopTurnAngle and moving again within
    /// NavResumeTurnAngle. True = aligned enough to move forward now. Looting used the walking
    /// turn keys (slow) and stopped for any turn over 18 degrees (2026-10-04).
    /// </summary>
    internal bool ServoToward(double desiredDeg)
    {
        if (!_host.HasTurnToHeading) return true;
        if (!TryGetQuaternionHeading(out float curDeg))
        {
            _host.TurnToHeadingBy("Nav", (float)desiredDeg);
            return true;
        }
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = _servoLastTicks == 0 ? NavTickMs / 1000.0
            : Math.Clamp((nowTicks - _servoLastTicks) / (double)System.Diagnostics.Stopwatch.Frequency, 0.0, 0.2);
        _servoLastTicks = nowTicks;

        double err = NormalizeAngle(desiredDeg - curDeg);
        double maxStep = Math.Max(10.0, _settings.NavTurnRateDegPerSec) * Math.Max(dt, 0.01);
        double newHeading = curDeg + Math.Clamp(err, -maxStep, maxStep);
        if (newHeading >= 360.0) newHeading -= 360.0; else if (newHeading < 0.0) newHeading += 360.0;
        _host.TurnToHeadingBy("Nav", (float)newHeading);

        double abs = Math.Abs(err);
        if (_servoTurning) { if (abs <= BigTurnExit) _servoTurning = false; }
        else if (abs > BigTurnEnter) _servoTurning = true;
        return !_servoTurning;
    }

    private static double NormalizeAngle(double a)
    {
        while (a >  180.0) a -= 360.0;
        while (a < -180.0) a += 360.0;
        return a;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

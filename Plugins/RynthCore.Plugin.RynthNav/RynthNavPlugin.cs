using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using RynthCore.PluginCore;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// RynthNav v0.4 — tiled streaming navmesh. Loads a window of CONNECTED Detour
/// tiles (baked by RynthNav.Baker --tiled) around the player into one multi-tile
/// navmesh, so goto can route across landblocks. Streams tiles in/out as she moves
/// and loads the corridor toward a far goto target.
///
/// Threading: ALL navmesh + AC access happens on the AC tick thread (OnTick).
/// Panel/chat actions only set a pending request; OnTick executes it. Nothing
/// touches the navmesh or AC off-thread.
/// </summary>
public sealed partial class RynthNavPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthNav");
    internal const string PluginVersion = "0.6.7";
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi(PluginVersion);

    /// <summary>Lets PluginExports log through this plugin's host (the base Log is protected).</summary>
    internal void LogFromExport(string message) => Log(message);

    // Where the tiles, portals.tsv, locations.json and the player's atlas.txt/recalls.txt live.
    // From RYNTHNAV_NAVDATA, else "navDataDir" in %APPDATA%\RynthCore\rynthnav.json, else the
    // default (the folder the release zip and the launcher's tile updater fill). See NavDataConfig.
    private static readonly NavDataConfig NavConfig = NavDataConfig.Resolve();
    private static string NavDataDir => NavConfig.Dir;
    private const int VertsPerPoly = 6;
    private const int WindowRadius = 2;        // load a 5x5 window around the player
    private const int KeepRadius = 4;          // evict tiles beyond a 9x9 window
    private const int MaxTiles = 256;
    private const int MaxCorridorTiles = 220;
    private const double ArrivalUnits = 7.0;
    private const double CornerLookaheadUnits = 6.0;  // a path corner is cut from this far out when the next is in plain view
    private const int PreviewMaxLandblocks = 3;   // preview only targets this many landblocks away (a <= 6x6 tile box)
    private const int BlindPushTicks = 45;        // ~1.5 s straight at the goal when the plan runs out short of it (crosses a seam)
    private const int NoProgressTicks = 30 * 30;  // ~30 s without getting ProgressUnits closer to the leg's goal = give up
    private const double ProgressUnits = 5.0;
    private const double RouteLostUnits = 600.0;  // a route's remaining length this far above its best = lost: stop

    // Portal routing.
    private const double PortalArriveUnits = 14.0;  // switch to "walk into portal" within this of the entrance
    private const double PortalContactUnits = 4.0;   // within this, stand on the portal instead of orbiting it
    private const long SettleAfterTeleportMs = 15000;  // portal space this soon after a planned teleport is that teleport's own
    private const double PortalJumpUnits = 240.0;    // a position jump this big = a teleport happened (1 /loc deg)
    private const int PortalWaitTimeoutTicks = 300;  // ~10s at 30Hz to trigger a portal before giving up
    private const long PortalCooldownMs = 4000;      // ACE: no portal within 3.5 s of a teleport
    private const double PortalHoldUnits = 7.0;      // during that time, stay this far from the next portal
    private bool _cooldownSaid;
    /// <summary>When the last teleport (portal, recall) landed, by any of the detectors; 0 = none seen.</summary>
    private long LastTeleportMs => Math.Max(_trkLastTeleportMs, _lastLandMs);
    private long _lastLandMs;

    private const uint MotionWalkBackward = 0x45000006;
    private const uint MotionTurnLeft = 0x6500000E;
    private const uint MotionTurnRight = 0x6500000D;
    private const int HoldKeyRun = 1;

    private readonly object _gate = new();     // guards _status / _lastPath
    private string _status = "idle";
    private string _lastPath = "";

    // Navmesh — tick-thread only.
    private DtNavMesh? _navMesh;
    private DtNavMeshQuery? _query;
    private readonly HashSet<uint> _loadedTiles = new();
    private readonly HashSet<uint> _badTiles = new();   // failed to read/add once: logged, not retried every tick
    private volatile int _tileCount;

    // Pose cache (tick writes, others read).
    private volatile bool _hasPose;
    private uint _cellId;
    private double _wx, _wy, _wz;
    private uint _lastSeenLb;
    private int _posWriteTick;

    // Movement marshaling.
    private readonly object _moveGate = new();
    private int _desiredRun, _turnState;
    private bool _haltPending;
    private int _appliedRun, _appliedTurn, _moveHeartbeat;

    // Auto-walk.
    private readonly object _gotoGate = new();
    private List<(double ew, double ns)>? _gotoPath; // local path toward the current sub-goal
    private int _gotoIdx;
    private volatile bool _gotoActive;
    private bool _wasGoto;
    private double _gotoTew, _gotoTns; // CURRENT sub-goal world (EW, NS) — a route leg or the final target
    private double _finalTew, _finalTns; // ultimate target world (EW, NS)
    private int _replanTick;
    private bool _goalOnMesh;                 // Replan found walkable ground near the leg's goal...
    private double _goalMeshEw, _goalMeshNs;  // ...here (the path's end); reaching it counts as arriving
    private int _blindTicks;                  // ticks since the plan ran out (see BlindPushTicks)
    private int _legTicks, _bestTick;         // progress watchdog for the current leg
    private double _bestDist = double.MaxValue;
    private string _gotoName = "";            // what the goto is walking to (for its chat lines)
    private volatile bool _echoChat;          // the pending load/test/preview came from chat: answer there

    // Portal route execution (tick thread only).
    private List<PortalLink>? _portals;          // loaded lazily from NavData\portals.tsv
    // OFF by default: goto walks straight to the coord (the original, working behavior).
    // Portal routing is opt-in via "/rnav portals on" while we get it reliable.
    private volatile bool _portalsEnabled = false;
    private List<RouteStep>? _route;             // current planned legs (null = pure navmesh goto)
    private int _routeIdx;
    private bool _portalWait;                     // standing at a portal entrance, waiting for the teleport
    private int _portalWaitTicks;
    private double _prePortalWx, _prePortalWy;
    private uint _prePortalLb;                     // landblock when we started the portal — change == teleported
    private bool _wasPortaling;

    // Pending panel/chat requests (UI/chat thread sets; tick thread runs).
    private readonly object _reqGate = new();
    private bool _reqLoad, _reqTest;
    private string? _reqPreview, _reqGoto;
    private (string Name, string Type, double Ns, double Ew, string Enter, string EnterType)? _reqGotoNamed;   // a place by name (go, the panel)
    // The goto ends by walking into this place's portal (a dungeon, a portal): "" = no.
    private string _enterName = "", _enterType = "";
    // Deep-audit finding #17 (2026-06-18): DoMove used to mutate
    // _gotoActive/_route/_portalWait directly from the UI/chat thread while
    // the tick thread's StepGoto/OnSubGoalReached read _route non-atomically
    // (_route.Count / _route[_routeIdx]) — a TOCTOU NRE window. Routed
    // through the same request-gate pattern as every other panel action.
    private bool _reqCancel;

    public override int Initialize()
    {
        _ = Reply;
        _status = "initialized";
        // Every use logged (Plugins/Shared/UseAudit.cs). No macro here: no door/corpse guard.
        RynthCore.Plugin.Shared.UseAudit.Reset("RynthNav", null);
        // The panel's switches survive reloads and relogs (rynthnav.json, next to navDataDir).
        _portalsEnabled = NavDataConfig.ReadSwitch("portals", false);
        _recallsEnabled = NavDataConfig.ReadSwitch("recalls", true);
        LoadAvoidSettings();
        LoadMarkerSettings();
        LoadMeshSettings();
        Host.Log($"[RynthNav] Initialized v{PluginVersion} (tiled streaming + long-range goto + portal/recall routing + arrow + atlas). Panel: RynthNav. Tiles: {NavDataDir} ({NavConfig.Source})");
        InitTravel();
        LoadGraph();
        LoadTownNet();
        return 0;
    }

    // A plugin reload (any RynthSuite deploy reloads every plugin) in the middle of a
    // goto left AC's autorun on with nothing steering: the bot ran off in a straight line.
    public override void Shutdown()
    {
        if (!_gotoActive && !_wasGoto && _appliedRun == 0) return;
        _gotoActive = false;
        try { Host.SetAutoRun(false); Host.StopCompletely(); } catch { }
    }

    public override void OnLoginComplete()
    {
        Host.Log("[RynthNav] Login — loading tile window.");
        lock (_reqGate) _reqLoad = true;
        OnLoginTravel();
    }

    public override void OnLogout()
    {
        _navMesh = null; _query = null; _loadedTiles.Clear(); _tileCount = 0;
        // Forget the old pose: kept, the panel went on showing it and the login load
        // request could run against it. With _lastSeenLb cleared, the first real pose
        // after the next login reloads the tile window (the navmesh was dropped above).
        _hasPose = false; _lastSeenLb = 0;
        lock (_gate) { _status = "logged out"; _lastPath = ""; }
        lock (_moveGate) { _desiredRun = 0; _turnState = 0; _haltPending = true; }
        _gotoActive = false;
        _route = null; _portalWait = false; _portalWaitTicks = 0; _wasPortaling = false; _settling = false;
        ResetHub();
        lock (_gotoGate) { _gotoPath = null; _gotoIdx = 0; }
        _badTiles.Clear();
        _tileRegions.Clear(); _tilePrints.Clear(); ResetCoarse();
        Reply.Clear();
        _goNote = "";
        HideMarkers("logged out");
        _meshView.Clear(); _meshDunLb = 0;
        _trip = null; _tripPath = null;
        ResetPathApi();
        OnLogoutTravel();
    }

    public override void OnTick()
    {
        if (Host.HasGetPlayerPose &&
            Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _))
        {
            int lbX = (int)((cell >> 24) & 0xFF), lbY = (int)((cell >> 16) & 0xFF);
            _cellId = cell; _wx = lbX * 192.0 + x; _wy = lbY * 192.0 + y; _wz = z; _hasPose = true;
            uint lb = (cell >> 16) & 0xFFFF;
            bool newLb = lb != _lastSeenLb;
            if (newLb) { _lastSeenLb = lb; if (!_gotoActive) RefreshTiles(lb); }
            RefreshInfo(newLb);
            if ((++_posWriteTick % 60) == 0) WritePosFile(); // ~2s: feed the bake-ahead watcher
        }
        TickTravel();
        ProcessRequests();
        ApplyMovement();
        TickMarkers();
        TickMeshView();
        UpdateGoLine();
        FlushChat();
    }

    // Chat from the tick thread. The engine drops an off-thread WriteToChat that comes
    // within 100 ms of any other plugin's (RynthAi writes often), so a line said here is
    // queued and retried on later ticks until it goes through (one line per tick; a line
    // that still can't be written after ~1.5 s goes to the log so the queue never jams).
    // The queue is the shared one every plugin answers commands through (Plugins/Shared/CommandReply.cs).
    private RynthCore.Plugin.Shared.CommandReply? _reply;
    private RynthCore.Plugin.Shared.CommandReply Reply =>
        _reply ??= new RynthCore.Plugin.Shared.CommandReply("[RynthNav]", (t, c) => Host.WriteToChat(t, c), t => Host.Log(t));
    private void Say(string text) => Reply.Ok(text);
    /// <summary>A command that didn't work: why, and the usage (the failure colour).</summary>
    private void SayFail(string text) => Reply.Fail(text);
    private void FlushChat() => Reply.Flush();

    private uint CurrentLandblock => (_cellId >> 16) & 0xFFFF;

    // ── Tile streaming (tick thread only) ────────────────────────────────────────
    private void EnsureNavMesh()
    {
        if (_navMesh != null) return;
        var nav = new DtNavMesh();
        var p = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = MaxTiles, maxPolys = 1 << 16 };
        nav.Init(ref p, VertsPerPoly);
        _navMesh = nav;
        _query = new DtNavMeshQuery(nav);
    }

    private bool EnsureTile(uint lb)
    {
        if (_loadedTiles.Contains(lb)) return true;
        if (_loadedTiles.Count >= MaxTiles - 1 || _badTiles.Contains(lb)) return false;
        string path = Path.Combine(NavDataDir, $"nav_{lb:X4}.tile");
        if (!File.Exists(path)) return false;
        try
        {
            DtMeshData md;
            using (var fr = File.OpenRead(path)) using (var br = new BinaryReader(fr)) md = new DtMeshDataReader().Read(br, VertsPerPoly);
            EnsureNavMesh();
            var st = _navMesh!.AddTile(md, 0, 0, out _);
            if (st.Failed()) { _badTiles.Add(lb); Host.Log($"[RynthNav] tile 0x{lb:X4} not added (status 0x{st.Value:X})"); return false; }
            _loadedTiles.Add(lb);
            OnTileLoaded(lb, md);
            return true;
        }
        catch (Exception ex) { _badTiles.Add(lb); Host.Log($"[RynthNav] tile 0x{lb:X4} failed to load: {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    private bool EnsureWindow(int cx, int cy)
    {
        EnsureNavMesh();
        int before = _loadedTiles.Count;
        for (int dx = -WindowRadius; dx <= WindowRadius; dx++)
            for (int dy = -WindowRadius; dy <= WindowRadius; dy++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || x > 255 || y < 0 || y > 255) continue;
                EnsureTile((uint)((x << 8) | y));
            }
        EvictFar(cx, cy, KeepRadius);
        _tileCount = _loadedTiles.Count;
        return _loadedTiles.Count != before;
    }

    private void RefreshTiles(uint centerLb)
    {
        int cx = (int)((centerLb >> 8) & 0xFF), cy = (int)(centerLb & 0xFF);
        if (EnsureWindow(cx, cy)) _query = new DtNavMeshQuery(_navMesh!);
        lock (_gate) _status = _tileCount > 0 ? $"{_tileCount} tiles @ 0x{centerLb:X4}" : $"no tile for 0x{centerLb:X4} — bake it";
    }

    // Feed the bake-ahead watcher: player landblock + goto-target landblock. Written only when it
    // changes (2026-11-02): every client rewrote it a few times a second, and Windows Defender scans
    // each write.
    private string _lastPosText = "";

    private void WritePosFile()
    {
        try
        {
            int pX = (int)((_cellId >> 24) & 0xFF), pY = (int)((_cellId >> 16) & 0xFF);
            int tX = pX, tY = pY;
            if (_gotoActive) { tX = (int)(_gotoTew / 192.0); tY = (int)(_gotoTns / 192.0); }
            string text = $"{pX},{pY},{tX},{tY}";
            if (text == _lastPosText) return;
            File.WriteAllText(Path.Combine(NavDataDir, "_player.txt"), text);
            _lastPosText = text;
        }
        catch { }
    }

    private void EvictFar(int cx, int cy, int keep)
    {
        if (_loadedTiles.Count <= (2 * keep + 1) * (2 * keep + 1)) return;
        var remove = new List<uint>();
        foreach (var lb in _loadedTiles)
        {
            int x = (int)((lb >> 8) & 0xFF), y = (int)(lb & 0xFF);
            if (Math.Abs(x - cx) > keep || Math.Abs(y - cy) > keep) remove.Add(lb);
        }
        foreach (var lb in remove)
        {
            long r = _navMesh!.GetTileRefAt((int)((lb >> 8) & 0xFF), (int)(lb & 0xFF), 0);
            if (r != 0) _navMesh.RemoveTile(r);
            _loadedTiles.Remove(lb);
            OnTileUnloaded(lb);
        }
    }

    /// <summary>
    /// The "RynthNav.Path" interface's tiles: the box from (sx, sy) to (gx, gy) plus a one-tile margin
    /// (<see cref="NavPathQuery.TileBox"/>; <paramref name="shrinkMargin"/>: without the margin when that
    /// alone fits). False when it's more than <paramref name="maxTiles"/>; else <see cref="_query"/> is
    /// ready (made again when a tile was added).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_query))]
    private bool LoadPathBox(double sx, double sy, double gx, double gy, int maxTiles, bool shrinkMargin)
    {
        EnsureNavMesh();
        if (!NavPathQuery.TileBox(sx, sy, gx, gy, maxTiles, shrinkMargin, out int minX, out int minY, out int maxX, out int maxY)) return false;
        bool changed = false;
        for (int x = minX; x <= maxX; x++)
            for (int y = minY; y <= maxY; y++)
                changed |= !_loadedTiles.Contains((uint)((x << 8) | y)) && EnsureTile((uint)((x << 8) | y));
        if (changed || _query == null) _query = new DtNavMeshQuery(_navMesh!);
        _tileCount = _loadedTiles.Count;
        return true;
    }

    // Load every tile in the bounding box from player to target (the route corridor).
    private void LoadCorridorTo(double twx, double twy)
    {
        EnsureNavMesh();
        int tLbX = (int)(twx / 192.0), tLbY = (int)(twy / 192.0);
        int pLbX = (int)((_cellId >> 24) & 0xFF), pLbY = (int)((_cellId >> 16) & 0xFF);
        int minX = Math.Min(pLbX, tLbX) - 1, maxX = Math.Max(pLbX, tLbX) + 1;
        int minY = Math.Min(pLbY, tLbY) - 1, maxY = Math.Max(pLbY, tLbY) + 1;
        int count = 0;
        for (int x = minX; x <= maxX && count < MaxCorridorTiles; x++)
            for (int y = minY; y <= maxY && count < MaxCorridorTiles; y++)
            {
                if (x < 0 || x > 255 || y < 0 || y > 255) continue;
                if (EnsureTile((uint)((x << 8) | y))) count++;
            }
        _query = new DtNavMeshQuery(_navMesh!);
        _tileCount = _loadedTiles.Count;
    }

    // ── Pending requests (set on UI/chat thread; executed on tick thread) ────────
    public void DoLoadTile() { lock (_reqGate) _reqLoad = true; }
    public void DoTestQuery() { lock (_reqGate) _reqTest = true; }
    public void DoPreviewPath(string? coord) { lock (_reqGate) _reqPreview = coord ?? ""; }
    public void DoGoto(string? coord) { lock (_reqGate) _reqGoto = coord ?? ""; }

    private void ProcessRequests()
    {
        bool load, test, cancel; string? prev, gotoTo;
        (string Name, string Type, double Ns, double Ew, string Enter, string EnterType)? named;
        lock (_reqGate) { load = _reqLoad; _reqLoad = false; test = _reqTest; _reqTest = false; prev = _reqPreview; _reqPreview = null; gotoTo = _reqGoto; _reqGoto = null; cancel = _reqCancel; _reqCancel = false; named = _reqGotoNamed; _reqGotoNamed = null; }
        // Cancel first: runs before this same tick's ApplyMovement/StepGoto
        // (OnTick calls ProcessRequests then ApplyMovement), so a DoMove that
        // fired this tick takes effect immediately, same as the old direct
        // mutation did — just on the tick thread instead of the caller's.
        if (cancel)
        {
            if (_gotoActive) _goNote = "stopped";   // the panel's status line (the d-pad and Stop say nothing in chat)
            _gotoActive = false; _route = null; _portalWait = false; if (_recallWait) EndRecall();
            ResetHub();
            HideMarkers("cancelled");
        }
        if (load && _hasPose) RefreshTiles(CurrentLandblock);
        if (test) TestImpl();
        if (prev != null) PathImpl(prev, walk: false);
        if (gotoTo != null) PathImpl(gotoTo, walk: true);
        if (named is { } n) StartWalk(n.Name, n.Type, n.Ns, n.Ew, n.Enter, n.EnterType);
        // Load/test/preview only fed the panel; typed in chat they answered nothing.
        if ((load || test || prev != null) && _echoChat)
        {
            _echoChat = false;
            string st, lp;
            lock (_gate) { st = _status; lp = _lastPath; }
            Say($"{st}{(prev != null && lp.Length > 0 ? " — " + lp : "")}");
        }
    }

    private void TestImpl()
    {
        if (_query == null) { lock (_gate) _status = "no navmesh — load first"; return; }
        if (!_hasPose) { lock (_gate) _status = "no pose"; return; }
        var p = new RcVec3f((float)_wx, (float)_wz, (float)_wy);
        var st = _query.FindNearestPoly(p, new RcVec3f(6, 32, 6), new DtQueryDefaultFilter(), out long pr, out _, out _);
        bool ok = st.Succeeded() && pr != 0;
        lock (_gate) _status = ok ? $"on navmesh ({_tileCount} tiles)" : "OFF navmesh (no poly under you)";
        Host.Log($"[RynthNav] test: world=({_wx:F1},{_wy:F1},{_wz:F1}) ok={ok}");
    }

    // walk=false → bounded preview; walk=true → start incremental long-range auto-walk.
    private void PathImpl(string coord, bool walk)
    {
        if (!_hasPose) { lock (_gate) _status = "no pose"; if (walk) Say("goto: no player position yet"); return; }
        if (!NavCoords.TrySplitCoordsAndName(coord, out double tns, out double tew, out string given))
        {
            lock (_gate) { _status = "bad coord"; _lastPath = "type e.g. 42.5N, 33.6E"; }
            if (walk) Say($"goto: can't read '{coord}' — type e.g. 42.5N, 33.6E");
            return;
        }

        double twx = NavCoords.WorldX(tew);
        double twy = NavCoords.WorldY(tns);

        if (walk)
        {
            StartWalk(given.Length > 0 ? given : NavCoords.Fmt(tns, tew), "", tns, tew);
            return;
        }

        // Preview: bounded one-shot path (nearby targets only). A far target used to
        // load its whole bounding box of tiles here (up to 220 tile files, each read
        // and added on the game's tick) — a multi-second freeze — and then fill the
        // navmesh so the next window load couldn't add tiles.
        int pvX = (int)((_cellId >> 24) & 0xFF), pvY = (int)((_cellId >> 16) & 0xFF);
        if (Math.Max(Math.Abs((int)(twx / 192.0) - pvX), Math.Abs((int)(twy / 192.0) - pvY)) > PreviewMaxLandblocks)
        {
            lock (_gate) { _status = $"preview -> {Fmt(tns, 'N', 'S')} {Fmt(tew, 'E', 'W')}"; _lastPath = "too far to preview — just Go ▶"; }
            return;
        }
        LoadCorridorTo(twx, twy);
        if (_query == null) { lock (_gate) { _status = "no navmesh — load first"; _lastPath = "load a tile first"; } return; }
        var filter = new DtQueryDefaultFilter();
        var startP = new RcVec3f((float)_wx, (float)_wz, (float)_wy);
        var endP = new RcVec3f((float)twx, (float)_wz, (float)twy);
        var half = new RcVec3f(8, 256, 8);
        _query.FindNearestPoly(startP, half, filter, out long sRef, out RcVec3f sPt, out _);
        _query.FindNearestPoly(endP, half, filter, out long eRef, out RcVec3f ePt, out _);
        if (sRef == 0 || eRef == 0) { lock (_gate) { _status = "target has no baked tile"; _lastPath = "too far to preview — just Go ▶"; } return; }
        Span<long> path = new long[1024];
        _query.FindPath(sRef, eRef, sPt, ePt, filter, path, out int pc, 1024);
        Span<DtStraightPath> sp = new DtStraightPath[1024];
        _query.FindStraightPath(sPt, ePt, path[..pc], pc, sp, out int spc, 1024, 0);
        if (spc < 2) { lock (_gate) { _status = "no path"; _lastPath = "unreachable"; } return; }
        double len = 0;
        for (int i = 1; i < spc; i++) { double e0 = sp[i - 1].pos.X, n0 = sp[i - 1].pos.Z, e1 = sp[i].pos.X, n1 = sp[i].pos.Z; len += Math.Sqrt((e1 - e0) * (e1 - e0) + (n1 - n0) * (n1 - n0)); }
        lock (_gate) { _status = $"preview -> {Fmt(tns, 'N', 'S')} {Fmt(tew, 'E', 'W')}"; _lastPath = $"{spc} wpts, {len:F0}u, {pc} polys"; }
        Host.Log($"[RynthNav] preview: {spc} wpts {len:F0}u to ({tns:F2},{tew:F2})");
    }

    /// <summary>
    /// Starts the auto-walk to a place: plans a route (portals and recalls, when portal
    /// routing is on), points the arrow along it, and walks the legs. Tick thread.
    /// </summary>
    private void StartWalk(string name, string type, double tns, double tew, string enter = "", string enterType = "", bool landReplan = false)
    {
        if (!_hasPose) { lock (_gate) _status = "no pose"; Note("goto: no player position yet"); return; }
        // A new goto (not the re-plan after "no route over land") may re-plan once again.
        if (!landReplan) { _landReplanned = false; _enterPortal = enter.Length > 0 ? _nextEnterPortal : ""; _nextEnterPortal = ""; }
        // Whatever was running stops first: a refused goto must not leave the old one half-changed.
        if (_gotoActive) { _gotoActive = false; _route = null; _portalWait = false; ResetHub(); AutoRun.Stop(); HideMarkers("a new goto"); }
        double twx = NavCoords.WorldX(tew), twy = NavCoords.WorldY(tns);
        int gLbX = (int)Math.Floor(twx / 192.0), gLbY = (int)Math.Floor(twy / 192.0);
        if (double.IsNaN(twx) || double.IsNaN(twy) || gLbX < 0 || gLbX > 255 || gLbY < 0 || gLbY > 255)
        {
            Note($"goto: {NavCoords.Fmt(tns, tew)} is off the map");
            return;
        }
        if (_recallWait) EndRecall();
        _finalTew = twx; _finalTns = twy;
        _replanTick = 0;
        _portalWait = false; _portalWaitTicks = 0; _wasPortaling = false; _settling = false;
        lock (_gotoGate) { _gotoPath = null; _gotoIdx = 0; }
        lock (_moveGate) { _desiredRun = 0; _turnState = 0; }

        // Plan a route from here to the target. If it uses portals or recalls, walk the
        // legs; otherwise StepGoto just navmesh-walks straight to the final target.
        // Inside the Town Network there is no navmesh: the way on is one of its exits.
        ResetHub();
        bool inHub = _townNet?.Contains(CurrentLandblock) == true;
        int portalsUsed; double est; List<RecallOption> recalls;
        if (inHub)
        {
            recalls = new List<RecallOption>();
            _route = PlanFromInsideHub(tns, tew, out est, out portalsUsed);
            if (_route == null)
            {
                Note("goto: you're in the Town Network and none of its exit portals is open to you");
                return;
            }
        }
        else
        {
            TravelPlan.Result plan = PlanRoute(name, tns, tew, out recalls);
            if (plan.Refusal.Length > 0)
            {
                Note("goto: " + plan.Refusal);
                lock (_gate) _status = "no way there";
                _route = null;
                return;
            }
            _route = plan.Steps; portalsUsed = plan.Teleports; est = plan.Est;
        }
        _routeRecalls = recalls;

        _routeIdx = 0;
        // Into a dungeon (or through a portal): the last leg walks into its portal and waits for
        // the teleport, like a portal step of a route; it doesn't stop beside it.
        _enterName = enter; _enterType = enterType;
        if (enter.Length > 0) _route = Entrance.EndingInPortal(_route, tns, tew, enter);
        _shownRoute = _route ?? new List<RouteStep> { new(tns, tew, false, false, "walk") };
        _shownRouteFor = name; _shownRouteEst = est; _shownRouteLive = true; _routeVersion++;
        SetSubGoalToCurrentLeg();
        if (_route != null && portalsUsed > 0) _arrow.SetRoute(_route, name, type, tns, tew);
        else _arrow.SetTarget(name, type, tns, tew);
        _openArrowSeq++;

        // No navmesh where we stand (outside the baked area, every dungeon): say so at once
        // instead of standing still with "walking ->". A route that starts with a recall
        // needs no navmesh here.
        bool recallFirst = _route != null && _route.Count > 0 && (_route[0].UseRecall || _route[0].IsHub);
        uint hereLb = CurrentLandblock;
        // A dungeon's landblock has a tile too, but it's the surface's (dungeon cells aren't
        // baked): walking on it inside the dungeon runs into walls. Say so instead.
        if (!recallFirst && InDungeon(_cellId))
        {
            _route = null;
            lock (_gate) { _status = $"in a dungeon (0x{hereLb:X4}): no navmesh"; _lastPath = ""; }
            Note($"goto: no navmesh inside dungeons yet (landblock 0x{hereLb:X4}); walk out through its portal, or turn on recalls so the route can start with one");
            return;
        }
        if (!recallFirst && !File.Exists(Path.Combine(NavDataDir, $"nav_{hereLb:X4}.tile")))
        {
            _route = null;
            lock (_gate) { _status = $"no tile for 0x{hereLb:X4} — bake it"; _lastPath = ""; }
            bool indoors = (_cellId & 0xFFFF) >= 0x100;
            Note($"goto: no navmesh here (landblock 0x{hereLb:X4}{(indoors ? ", indoors" : "")} has no tile in {NavDataDir}){(indoors ? "" : "; the arrow points the way")}");
            return;
        }
        uint goalLb = (uint)((gLbX << 8) | gLbY);
        if (_route == null && !File.Exists(Path.Combine(NavDataDir, $"nav_{goalLb:X4}.tile")))
            Say($"goto: the target's landblock 0x{goalLb:X4} has no tile; walking as far as the navmesh goes");

        SetTrip(_route, name, tns, tew, recalls, enter, live: true);
        AutoRun.Forget();   // the d-pad or another plugin may have moved since: the first call goes out
        _gotoActive = true;
        _gotoName = name; _gotoType = type;
        _walkHaveLast = false;
        string via = (_route != null && portalsUsed > 0) ? $" via {portalsUsed} teleport(s), ~{est:F0}u" : "";
        lock (_gate) { _status = $"walking -> {NavCoords.Fmt(tns, tew)}{via}"; _lastPath = "planning…"; }
        Host.Log($"[RynthNav] goto {name} ({tns:F2},{tew:F2}){via}");
        string at = name == NavCoords.Fmt(tns, tew) ? name : $"{name} at {NavCoords.Fmt(tns, tew)}";
        if (enter.Length > 0) at = $"{enter}'s portal at {NavCoords.Fmt(tns, tew)}";
        Say($"walking to {at}{via} — /rnav stop to cancel");
        string hub = HubStartText();
        if (hub.Length > 0) Say(hub);
    }

    // ── Movement (d-pad), unchanged ──────────────────────────────────────────────
    public void DoMove(int cmd)
    {
        // Any manual input cancels auto-walk. Routed through _reqGate (finding
        // #17) instead of mutating _gotoActive/_route/_portalWait directly —
        // those are tick-thread-owned state read non-atomically by
        // StepGoto/OnSubGoalReached.
        lock (_reqGate) _reqCancel = true;
        lock (_moveGate)
        {
            switch (cmd)
            {
                case 1: _desiredRun = _desiredRun == 1 ? 0 : 1; break;
                case 2: _desiredRun = _desiredRun == 2 ? 0 : 2; break;
                case 3: _turnState = _turnState == -1 ? 0 : -1; break;
                case 4: _turnState = _turnState == 1 ? 0 : 1; break;
                case 5: _desiredRun = 0; _turnState = 0; _haltPending = true; break;
            }
        }
    }

    private void ApplyMovement()
    {
        if (_gotoActive) { _wasGoto = true; StepGoto(); return; }
        if (_wasGoto)
        {
            _wasGoto = false;
            Host.SetAutoRun(false);
            Host.StopMovement(MotionWalkBackward, HoldKeyRun);
            Host.SetMotion(MotionTurnLeft, false);
            Host.SetMotion(MotionTurnRight, false);
            Host.StopCompletely();
            _appliedRun = 0; _appliedTurn = 0;
        }

        int desiredRun, turnState; bool halt;
        lock (_moveGate) { desiredRun = _desiredRun; turnState = _turnState; halt = _haltPending; _haltPending = false; }

        if (halt)
        {
            Host.SetAutoRun(false);
            Host.StopMovement(MotionWalkBackward, HoldKeyRun);
            Host.SetMotion(MotionTurnLeft, false);
            Host.SetMotion(MotionTurnRight, false);
            Host.StopCompletely();
            _appliedRun = 0; _appliedTurn = 0;
            return;
        }

        if (desiredRun != _appliedRun)
        {
            if (_appliedRun == 1) Host.SetAutoRun(false);
            else if (_appliedRun == 2) Host.StopMovement(MotionWalkBackward, HoldKeyRun);
            if (desiredRun == 1) Host.SetAutoRun(true);
            else if (desiredRun == 2) Host.DoMovement(MotionWalkBackward, 1.0f, HoldKeyRun);
            _appliedRun = desiredRun;
            _moveHeartbeat = 0;
        }
        else if (desiredRun != 0 && (++_moveHeartbeat % 15) == 0)
        {
            if (desiredRun == 1) Host.SetAutoRun(true);
            else if (desiredRun == 2) Host.DoMovement(MotionWalkBackward, 1.0f, HoldKeyRun);
        }

        if (turnState != _appliedTurn)
        {
            if (_appliedTurn == -1) Host.SetMotion(MotionTurnLeft, false);
            else if (_appliedTurn == 1) Host.SetMotion(MotionTurnRight, false);
            if (turnState == -1) Host.SetMotion(MotionTurnLeft, true);
            else if (turnState == 1) Host.SetMotion(MotionTurnRight, true);
            _appliedTurn = turnState;
        }
    }

    private void StepGoto()
    {
        if (!_hasPose) return;
        if (_settling) { StepSettle(); return; }      // a teleport is still arriving
        if (_recallWait) { StepRecall(); return; }   // a recall needs no navmesh
        if (!_portalWait && CurrentStepIsHub) { StepHub(); return; }   // inside the Town Network: its own walks
        if (_query == null) return;

        if (_portalWait) { StepPortalWait(); return; }

        // A teleport on a walking leg is never planned: a portal we touched by accident. Stop.
        bool portalingNow = Host.HasIsPortaling && Host.IsPortaling();
        double jump = _walkHaveLast ? Math.Sqrt((_wx - _walkLastWx) * (_wx - _walkLastWx) + (_wy - _walkLastWy) * (_wy - _walkLastWy)) : 0;
        if (portalingNow && !_walkHaveLast) { AutoRun.Run(false); return; }   // still arriving from the last step's teleport
        // The client can show portal space a second or more AFTER the position jump that ended a
        // recall or portal step (seen live: @lifestone jumped at 19:45:11, portal space began at
        // 19:45:12). That portal space is the step's own: wait it out, and count the landing from
        // its end, rather than calling it a stray portal.
        if (portalingNow && NowMs - _lastLandMs < SettleAfterTeleportMs)
        {
            AutoRun.Run(false);
            _walkHaveLast = false;
            _lastLandMs = NowMs;
            return;
        }
        if (portalingNow || jump > PortalJumpUnits)
        {
            FinishGoto($"stopped: a portal on the way took you {(portalingNow ? "into portal space" : $"to landblock 0x{CurrentLandblock:X4}")}; it wasn't on the route");
            return;
        }
        _walkHaveLast = true; _walkLastWx = _wx; _walkLastWy = _wy;
        RefreshAvoid(force: false);

        bool isPortalLeg = _route != null && _routeIdx < _route.Count && _route[_routeIdx].UsePortal;
        double arrive = isPortalLeg ? PortalArriveUnits : ArrivalUnits;
        double fdew = _gotoTew - _wx, fdns = _gotoTns - _wy;
        double toGoal = Math.Sqrt(fdew * fdew + fdns * fdns);
        // A /loc inside a building, a tree or on a steep slope has no walkable ground:
        // the path ends at the nearest walkable spot, which can be more than
        // ArrivalUnits from the typed point. Standing on that spot is arriving.
        bool atMeshGoal = !isPortalLeg && _goalOnMesh &&
            Math.Sqrt((_goalMeshEw - _wx) * (_goalMeshEw - _wx) + (_goalMeshNs - _wy) * (_goalMeshNs - _wy)) < ArrivalUnits;
        // A portal is run into straight from PortalArriveUnits: only with nothing between, as far as
        // the navmesh shows. A portal round a corner (a wall within 14 u of it) is reached along
        // the path first; it used to be run at through the wall, 12 s a try, five tries.
        if (isPortalLeg && toGoal < arrive && toGoal > PortalContactUnits && !atMeshGoal)
        {
            bool pathLeft;
            lock (_gotoGate) pathLeft = _gotoPath != null && _gotoIdx < _gotoPath.Count;
            if (pathLeft && !StraightOnMesh(_gotoTew, _gotoTns)) arrive = PortalContactUnits;
        }
        if (toGoal < arrive || atMeshGoal)
        {
            OnSubGoalReached();
            return;
        }

        // Progress watchdog: a goto that can't get closer stops and says why, instead of
        // standing ("off navmesh") or pacing at a seam for ever.
        _legTicks++;
        // Following a route over land, progress is what's left of the route (it can lead away
        // from the target for a while); otherwise the straight distance.
        double left = _coarse != null ? _coarse.Remaining(_wx, _wy) : toGoal;
        if (left < _bestDist - ProgressUnits) { _bestDist = left; _bestTick = _legTicks; _stuckTries = 0; }
        else if (_coarse != null && left > _bestDist + RouteLostUnits)
        {
            // Following a route but getting ever farther from it: stop rather than wander.
            string why; lock (_gate) why = _status;
            FinishGoto($"stopped: walking away from the route, {left:F0}u of it left (best {_bestDist:F0}u) ({why})");
            return;
        }
        else if (_legTicks - _bestTick > NoProgressTicks)
        {
            string why; lock (_gate) why = _status;
            FinishGoto($"stopped: no progress for 30 s, {toGoal:F0}u short ({why})");
            return;
        }
        // Running into something (RynthNavPlugin.Blocked.cs): turn away, plan again, or stop and say where.
        if (StepBlocked()) return;

        // Plan at once, re-plan every 20 ticks, and every 5 while there is no plan.
        bool due = _gotoPath == null ? (_replanTick++ % 5) == 0 : (++_replanTick % 20) == 0;
        if (due) Replan();

        // With a route graph, don't move until its search answers (a fraction of a second): the
        // first steps used to head straight for the target, and on 10-01 straight into a portal
        // before the search said "no route over land".
        if (_coarseHoldTicks > 0 && _coarse == null && _gotoActive)
        {
            _coarseHoldTicks--;
            AutoRun.Run(false);
            lock (_gate) _status = "planning the way…";
            return;
        }

        List<(double ew, double ns)>? path; int idx;
        lock (_gotoGate) { path = _gotoPath; idx = _gotoIdx; }
        if (path != null)
        {
            // A corner is reached close up (cornerReachMetres); only the path's end uses
            // ArrivalUnits. 7 u for every corner turned early and cut across the inside of
            // each one, into the building it was going round (10-01 20:15). From
            // CornerLookaheadUnits out, a corner is also passed once the next one can be walked to
            // straight (the navmesh's raycast: the agent radius from every wall it knows, and out
            // of the portal circles): the walk turns smoothly instead of overshooting each corner
            // at run speed and turning back to it (11-01).
            idx = PathFollow.Advance(path, idx, _wx, _wy, _cornerReach, ArrivalUnits, CornerLookaheadUnits, WalkableStraight);
            lock (_gotoGate) _gotoIdx = idx;
        }
        if (path == null || idx >= path.Count)
        {
            // No plan, or it ended short of the goal (a seam between two tiles that don't
            // link, the edge of the baked area, a goal off the mesh). Head straight for the
            // goal for a moment, which carries us over a seam, then stand and keep re-planning.
            // Before, autorun stayed on with the old heading and nothing steering: the bot
            // ran off in a straight line, or (no plan at all) stood forcing autorun off.
            if (_blindTicks < BlindPushTicks)
            {
                _blindTicks++;
                // Straight at the goal, or along the route at its next crossing.
                double bh = (_coarse != null ? Math.Atan2(_aimEw - _wx, _aimNs - _wy) : Math.Atan2(fdew, fdns)) * 180.0 / Math.PI;
                bh = SteerRoundPortals(bh < 0 ? bh + 360.0 : bh, 10.0);
                WalkDrive(bh);
                if (_blindTicks % 10 == 0) lock (_gotoGate) _gotoPath = null; // re-plan soon
            }
            else if (_blindTicks++ == BlindPushTicks)
            {
                AutoRun.Run(false);
            }
            return;
        }
        _blindTicks = 0;

        double dew = path[idx].ew - _wx, dns = path[idx].ns - _wy;
        double desired = Math.Atan2(dew, dns) * 180.0 / Math.PI;
        if (desired < 0) desired += 360.0;
        // No steering on a planned path: the path already keeps out of portals (AvoidFilter + Bend),
        // and the steering doesn't know about walls. Over it, at Holtburg's lifestone (inside the
        // hub portals' circles, a wall beside), it held 234 deg into the wall while the path wanted
        // 301 and re-planned every 0.6 s (10-01 20:07, "just spazzing"). Steering is for the
        // blind push and the last steps onto a portal, where there is no path.
        WalkDrive(desired);
    }

    // ── Portal routing ───────────────────────────────────────────────────────────
    // Reached the current leg's target: finish, take the portal, or advance.
    private void OnSubGoalReached()
    {
        if (_route == null) { FinishGoto("arrived"); return; }
        var step = _route[_routeIdx];
        if (step.UseRecall)
        {
            // Cast the recall (or send its command) and wait for the teleport (StepRecall).
            BeginRecallStep();
            return;
        }
        if (step.UsePortal)
        {
            _portalWait = true; _portalWaitTicks = 0; _wasPortaling = false;
            _prePortalWx = _wx; _prePortalWy = _wy; _prePortalLb = CurrentLandblock;
            BeginPortalAim();
            lock (_gate) _status = $"taking portal '{Trunc(step.Label)}'";
            Host.Log($"[RynthNav] at portal entrance '{step.Label}' lb=0x{_prePortalLb:X4} — approaching");
            return;
        }
        if (_routeIdx >= _route.Count - 1) { FinishGoto("arrived"); return; }
        AdvanceRoute();
    }

    private void AdvanceRoute()
    {
        _routeIdx++;
        if (_route != null && _routeIdx >= _route.Count) { FinishGoto("arrived"); return; }
        SetSubGoalToCurrentLeg();
        lock (_gotoGate) { _gotoPath = null; _gotoIdx = 0; }
        _replanTick = 0;
    }

    private void SetSubGoalToCurrentLeg()
    {
        if (_route != null && _routeIdx < _route.Count)
        {
            var s = _route[_routeIdx];
            _gotoTew = (s.Ew * 10.0 + 1019.5) * 24.0;
            _gotoTns = (s.Ns * 10.0 + 1019.5) * 24.0;
        }
        else { _gotoTew = _finalTew; _gotoTns = _finalTns; }
        // New leg: fresh progress watchdog, and the old leg's walkable end no longer counts.
        ResetHub();
        ResetBlocked();
        ResetTurn();
        _coarseHoldTicks = _graph != null ? CoarseHoldMaxTicks : 0;
        _nextAvoidMs = 0;
        _walkHaveLast = false;   // a new leg starts where the last step's teleport put us
        _goalOnMesh = false; _blindTicks = 0;
        _legTicks = 0; _bestTick = 0; _bestDist = double.MaxValue;
        ResetCoarse();
    }

    private void FinishGoto(string status)
    {
        if (_recallWait) EndRecall();
        _gotoActive = false; _route = null; _portalWait = false;
        ResetHub();
        _avoid.Clear(); _coarseHoldTicks = 0;
        ResetBlocked();
        AutoRun.Stop();
        lock (_gate) _status = status;
        Host.Log($"[RynthNav] goto: {status}");
        Say(status == "arrived" && _gotoName.Length > 0 ? $"goto: arrived at {_gotoName}" : $"goto: {status}");
        _goNote = status == "arrived" && _gotoName.Length > 0 ? $"arrived at {_gotoName}" : status;
        _enterName = ""; _enterType = "";
        HideMarkers(status == "arrived" ? "arrived" : "the goto ended");
    }

    // At a portal entrance, waiting for the teleport. A portal ALWAYS changes the
    // landblock, so a landblock change is the reliable trigger signal (the old
    // distance check missed teleports). To avoid orbiting the entrance at run speed
    // we approach to a small radius then settle and let the collision fire.
    private void StepPortalWait()
    {
        bool portaling = Host.HasIsPortaling && Host.IsPortaling();
        if (portaling) _wasPortaling = true;
        bool lbChanged = CurrentLandblock != _prePortalLb;
        double moved = Math.Sqrt((_wx - _prePortalWx) * (_wx - _prePortalWx) + (_wy - _prePortalWy) * (_wy - _prePortalWy));
        bool teleported = lbChanged || (_wasPortaling && !portaling) || moved > PortalJumpUnits;

        if (teleported)
        {
            AutoRun.Stop();
            _portalWait = false;
            _lastLandMs = NowMs;
            Host.Log($"[RynthNav] teleport detected (lb 0x{_prePortalLb:X4}->0x{CurrentLandblock:X4}, moved {moved:F0}u) — next leg");
            RefreshTiles(CurrentLandblock); // stream the area we landed in
            if (_enterName.Length > 0 && _route != null && _routeIdx >= _route.Count - 1)
            {
                FinishGoto(Entrance.ArrivedText(_enterName, _enterType));   // inside: done (autorun off)
                return;
            }
            BeginSettle();
            return;
        }

        SnapToLivePortal();
        double dew = _gotoTew - _wx, dns = _gotoTns - _wy;
        double d = Math.Sqrt(dew * dew + dns * dns);

        // ACE refuses a portal within 3.5 s of a teleport ("You have been teleported too recently!"),
        // and a refused portal doesn't fire again until you step out and back in. Just after a
        // recall or another portal (Recall Aphus Lassel lands 12 yd from a Town Network portal),
        // hold short of this one until 4 s have passed, backing off if we landed on it.
        long sinceTeleport = NowMs - LastTeleportMs;
        if (LastTeleportMs > 0 && sinceTeleport < PortalCooldownMs && d < PortalHoldUnits)
        {
            if (d < PortalContactUnits + 1.0)
            {
                double away = Math.Atan2(-dew, -dns) * 180.0 / Math.PI;
                DriveTo(away);
            }
            else AutoRun.Halt();
            _portalWaitTicks = 0;
            _portalTryStartMs = NowMs;   // the cooldown isn't a failed try
            if (!_cooldownSaid) { _cooldownSaid = true; Host.Log($"[RynthNav] waiting {(PortalCooldownMs - sinceTeleport) / 1000.0:F1} s before the portal (teleported {sinceTeleport} ms ago)"); }
            lock (_gate) _status = "waiting for the portal to let you through";
            return;
        }
        _cooldownSaid = false;

        _portalWaitTicks++;
        DriveIntoPortal(dew, dns, d);
    }

    private void EnsurePortalsLoaded()
    {
        if (_portals != null) return;
        var list = new List<PortalLink>();
        try
        {
            string path = Path.Combine(NavDataDir, "portals.tsv");
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var f = line.Split('\t');
                    if (f.Length < 4) continue;
                    if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double sns)) continue;
                    if (!double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sew)) continue;
                    if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double dns)) continue;
                    if (!double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double dew)) continue;
                    string pname = f.Length > 4 ? f[4] : "";
                    if (Entrance.IsDestroyed(pname)) continue;   // wrecks take you nowhere
                    list.Add(new PortalLink(sns, sew, dns, dew, pname));
                }
                Host.Log($"[RynthNav] loaded {list.Count} portals from portals.tsv");
            }
            else Host.Log("[RynthNav] portals.tsv not found — portal routing off");
        }
        catch (Exception ex) { Host.Log($"[RynthNav] portals load failed: {ex.Message}"); }
        _portals = list;
    }

    // Returns the leg list if portals or recalls beat walking, else null (pure navmesh goto).
    // force: plan even with portal routing off (the route preview).
    /// <summary>
    /// Plans a trip from where you stand (TravelPlan: the switches, a dungeon start, the land) and
    /// logs what it considered. Steps null = walk straight there; a refusal = there's no way, and why.
    /// Tick thread.
    /// </summary>
    private TravelPlan.Result PlanRoute(string name, double goalNs, double goalEw, out List<RecallOption> recalls)
    {
        double startNs = NavCoords.NsFromWorld(_wy), startEw = NavCoords.EwFromWorld(_wx);
        bool startOnMap = IsOnMap(_cellId);
        var recallNotes = new List<string>();
        recalls = _charKey.Length == 0 ? new List<RecallOption>() : RecallPlanner.Build(_atlas, _known, _memory, _charKey, recallNotes);
        if (_charKey.Length == 0) recallNotes.Add("not logged in yet");
        var hubNotes = new List<string>();
        HubLinks? hub = _portalsEnabled ? CurrentHubLinks(hubNotes) : null;
        if (hub != null && _preferAphus && _recallsEnabled) hub = ViaAphus(hub, recalls, hubNotes);
        if (_portalsEnabled) EnsurePortalsLoaded();
        // Walks that can't be done over land (another landmass) are left out, so an island or the
        // far side of water is only reached by a portal, a recall or the Town Network.
        NavGraph? g = UsableGraph();
        Func<double, double, double, double, bool>? canWalk = g == null ? null
            : (ans, aew, bns, bew) => g.SameLand(NavGraph.LandblockAt(NavCoords.WorldX(aew), NavCoords.WorldY(ans)),
                                                 NavGraph.LandblockAt(NavCoords.WorldX(bew), NavCoords.WorldY(bns))) != false;
        bool? land = startOnMap && g != null ? g.SameLand(CurrentLandblock, NavGraph.LandblockAt(NavCoords.WorldX(goalEw), NavCoords.WorldY(goalNs))) : null;

        TravelPlan.Result r = TravelPlan.Plan(_portalsEnabled ? _portals : null, RecallPlanner.Links(recalls), hub,
            _portalsEnabled, _recallsEnabled, startOnMap, startNs, startEw, goalNs, goalEw, canWalk, land, name);

        Host.Log($"[RynthNav] plan {name}: {r.Basis}; {(_portalsEnabled ? $"{_portals?.Count ?? 0} portals" : "no portals")}, "
               + $"{recalls.Count} recall(s) ready" + (recalls.Count > 0 ? " (" + string.Join(", ", recalls.ConvertAll(o => o.Name)) + ")" : "")
               + $", Town Network {(hub == null ? (_portalsEnabled ? "not loaded" : "unused") : $"{hub.Entries.Count} entries / {hub.Exits.Count} exits")}"
               + $", land check {(canWalk == null ? "off (no route graph)" : "on")}"
               + (startOnMap ? "" : $" (in landblock 0x{CurrentLandblock:X4})"));
        foreach (string n in recallNotes) Host.Log($"[RynthNav] plan: recall left out: {n}");
        foreach (string n in hubNotes) Host.Log($"[RynthNav] plan: {n}");
        Host.Log(r.Refusal.Length > 0
            ? $"[RynthNav] route to {name}: none: {r.Refusal}"
            : $"[RynthNav] route to {name}: {RouteLine(r.Steps, startNs, startEw, goalNs, goalEw, r.Est, land)}");
        return r;
    }

    private static string Trunc(string s) => s.Length <= 28 ? s : s.Substring(0, 28);

    // Re-plan toward the (possibly far) target using loaded tiles, streaming the
    // window + a few tiles toward the goal so the navmesh keeps extending ahead.
    private void Replan()
    {
        int pLbX = (int)((_cellId >> 24) & 0xFF), pLbY = (int)((_cellId >> 16) & 0xFF);
        bool changed = EnsureWindow(pLbX, pLbY);
        if (_coarse == null) changed |= EnsureToward(_gotoTew, _gotoTns);
        if (changed || _query == null) _query = new DtNavMeshQuery(_navMesh!);
        if (_query == null) return;

        var filter = new DtQueryDefaultFilter();
        var startP = new RcVec3f((float)_wx, (float)_wz, (float)_wy);
        // The ground we stand on first: the mesh keeps the agent radius (2 m) from walls, so close
        // up it's within that, at our height. Only then the wide search, which can pick ground on
        // the far side of a thin wall or a roof overhead, and the walk then runs into that wall.
        long sRef = NavPathQuery.SnapStart(_query, startP, filter, out RcVec3f sPt);
        // A failed plan drops the old one: StepGoto must not keep following a stale path.
        if (sRef == 0) { lock (_gate) _status = "off navmesh"; lock (_gotoGate) _gotoPath = null; return; }

        // A long walk follows the route graph: plan to its furthest loaded border crossing.
        bool loadedMore = false;
        var steer = CoarseSteer(sRef, sPt, ref loadedMore);
        if (loadedMore) _query = new DtNavMeshQuery(_navMesh!);

        // Prefer a DIRECT path to the actual target when its tile is loaded — one stable
        // corridor we follow steadily. Only fall back to the 500u line-probe leapfrog when
        // the target is too far to be loaded yet (that probe jitters and must stay a last resort).
        double aimEw = steer?.Ew ?? _gotoTew, aimNs = steer?.Ns ?? _gotoTns;
        _aimEw = aimEw; _aimNs = aimNs;
        // The route's last stretch ends on the ground its search ended on: the spot itself, or the
        // nearest ground a walk can end on when the spot is fenced in (NavCoarse.ProbeReachable).
        // Planned to the spot itself, the nearest polygon is the fenced-in one again.
        var targetP = steer is { } st ? new RcVec3f(st.Ew, st.Up, st.Ns)
            : _coarse != null && !double.IsNaN(_coarse.GoalUp) ? new RcVec3f((float)_coarse.GoalEw, (float)_coarse.GoalUp, (float)_coarse.GoalNs)
            : new RcVec3f((float)_gotoTew, (float)_wz, (float)_gotoTns);
        AvoidFilter avoid = WalkFilter();
        avoid.AllowA = sRef; avoid.AllowB = 0;
        // The goal is looked up WITHOUT the portal circles: the goal's polygon is always allowed
        // (AllowB below). Through the avoid filter, a big open-ground polygon that also reaches
        // another portal's circle hid the goal: 10-02 16:05, Lucy at the Lady Maila Estates Portal
        // in a cluster of housing portals: "no tile ahead", 74u short, on the collision-built tiles.
        _query.FindNearestPoly(targetP, steer != null ? new RcVec3f(8, 32, 8) : new RcVec3f(12, 256, 12), filter, out long gRef, out RcVec3f gPt, out _);
        bool direct = gRef != 0 && steer == null;
        if (gRef == 0) gRef = FindGoalToward(aimEw, aimNs, out gPt);
        if (gRef == 0) { lock (_gate) _status = "no tile ahead — bake the route"; lock (_gotoGate) _gotoPath = null; return; }

        Span<long> p = new long[NavPathQuery.MaxPolys];
        avoid.AllowB = gRef;
        // No way round the portals to there: planned as before; steering still keeps out of them.
        int pc = NavPathQuery.Corridor(_query, sRef, gRef, sPt, gPt, avoid, filter, _avoid.Count > 0, p, out bool fellBack, out _);
        if (fellBack && !_avoidFallbackSaid) { _avoidFallbackSaid = true; Host.Log("[RynthNav] avoid: no path round the portals here; steering round them instead"); }
        Span<DtStraightPath> sp = new DtStraightPath[512];
        _query.FindStraightPath(sPt, gPt, p[..pc], pc, sp, out int spc, 512, 0);
        if (spc < 2) { lock (_gate) _status = "no path"; lock (_gotoGate) _gotoPath = null; return; }
        // A complete path to walkable ground beside the goal: its end is where we arrive.
        // (A partial path, cut at a seam or a dead end, ends somewhere else.)
        _goalOnMesh = direct && pc > 0 && p[pc - 1] == gRef;
        _goalMeshEw = gPt.X; _goalMeshNs = gPt.Z;

        var wps = new List<(double, double)>(spc);
        for (int i = 0; i < spc; i++) wps.Add((sp[i].pos.X, sp[i].pos.Z));
        // Round any portal the path still crosses (big open-ground polygons let it cut straight over one).
        // Every mesh test at our own height (the walker plans from where it stands).
        float wz = (float)_wz;
        if (_avoid.Count > 0)
        {
            int before = wps.Count;
            wps = NavPathQuery.BendRoundPortals(_query, _avoid, wps, filter, (_, _) => wz);
            if (wps.Count != before) Host.Log($"[RynthNav] avoid: path bent round {wps.Count - before} portal(s)");
        }
        // Walls: each corner the path turns round sits on the mesh's edge (the agent radius from a
        // building); push it out along the turn's outside bisector where the ground is walkable.
        if (_avoidWalls) NavPathQuery.PushFromWalls(_query, wps, _wallClearance, filter, (_, _) => wz);
        lock (_gotoGate) { _gotoPath = wps; _gotoIdx = 0; }
        int rem = (int)Math.Sqrt((_gotoTew - _wx) * (_gotoTew - _wx) + (_gotoTns - _wy) * (_gotoTns - _wy));
        string via = _coarse != null && _coarse.Count > 0 ? $", route {_coarse.Crossed}/{_coarse.Count}" : "";
        lock (_gate) _lastPath = _goalOnMesh || !direct ? $"{rem}u to target{via}" : $"{rem}u to target (partial path: navmesh gap ahead)";
    }

    // ── "RynthNav.Path" interface (Shared/RynthNavPathApi.cs) ────────────────────
    // Called by another plugin from its OnTick: the same pump thread as our OnTick, so
    // the navmesh is touched on the tick thread only. Loads the tiles covering start and
    // goal (plus a one-tile margin) and plans on the same query object goto uses.
    // Version 2 (FindPathEx, Raycast): RynthNavPlugin.PathApi.cs.

    internal unsafe int InterfaceFindPath(double sx, double sy, double sz, double gx, double gy, double gz,
                                          double* points, int maxPoints, int* count)
    {
        if (count != null) *count = 0;
        if (points == null || maxPoints < 2) return Shared.RynthNavPathApiV1.ResultNoPath;
        if (!_hasPose) return Shared.RynthNavPathApiV1.ResultNoMesh;
        if (!IsFinite(sx) || !IsFinite(sy) || !IsFinite(sz) || !IsFinite(gx) || !IsFinite(gy) || !IsFinite(gz))
            return Shared.RynthNavPathApiV1.ResultNoPath;

        // Tiles for both ends and everything between (plus a margin), capped.
        if (!LoadPathBox(sx, sy, gx, gy, NavPathQuery.DefaultMaxTiles, shrinkMargin: false)) return Shared.RynthNavPathApiV1.ResultNoPath;

        var filter = new DtQueryDefaultFilter();
        var startP = new RcVec3f((float)sx, (float)sz, (float)sy);
        var goalP = new RcVec3f((float)gx, (float)gz, (float)gy);
        _query.FindNearestPoly(startP, new RcVec3f(8, 64, 8), filter, out long sRef, out RcVec3f sPt, out _);
        if (sRef == 0) return Shared.RynthNavPathApiV1.ResultNoMesh;
        _query.FindNearestPoly(goalP, new RcVec3f(8, 64, 8), filter, out long gRef, out RcVec3f gPt, out _);
        if (gRef == 0) return Shared.RynthNavPathApiV1.ResultNoMesh;

        Span<long> polys = new long[512];
        var st = _query.FindPath(sRef, gRef, sPt, gPt, filter, polys, out int pc, 512);
        if (st.Failed() || pc == 0 || polys[pc - 1] != gRef) return Shared.RynthNavPathApiV1.ResultNoPath;

        int cap = Math.Min(maxPoints, 256);
        Span<DtStraightPath> sp = new DtStraightPath[cap];
        _query.FindStraightPath(sPt, gPt, polys[..pc], pc, sp, out int spc, cap, 0);
        if (spc < 2) return Shared.RynthNavPathApiV1.ResultNoPath;

        var last = sp[spc - 1].pos;
        double slackE = last.X - gPt.X, slackN = last.Z - gPt.Z;
        if (Math.Sqrt(slackE * slackE + slackN * slackN) > NavPathQuery.GoalSlackUnits) return Shared.RynthNavPathApiV1.ResultNoPath;

        for (int i = 0; i < spc; i++)
        {
            points[i * 3]     = sp[i].pos.X;  // east
            points[i * 3 + 1] = sp[i].pos.Z;  // north
            points[i * 3 + 2] = sp[i].pos.Y;  // height
        }
        if (count != null) *count = spc;
        Host.Log($"[RynthNav] path request: {spc} corners, {pc} polys ({sx:F0},{sy:F0}) -> ({gx:F0},{gy:F0})");
        return Shared.RynthNavPathApiV1.ResultOk;
    }

    private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    // Farthest loaded poly along the straight line toward the target.
    private long FindGoalToward(double tew, double tns, out RcVec3f goalPt)
    {
        goalPt = default;
        double dEW = tew - _wx, dNS = tns - _wy;
        double d = Math.Sqrt(dEW * dEW + dNS * dNS);
        if (d < 1) return 0;
        double ux = dEW / d, uy = dNS / d;
        // Default filter, as for the goal itself (see Replan): the path is then planned round the
        // portal circles with this polygon allowed as its end.
        var filter = new DtQueryDefaultFilter();
        for (double reach = Math.Min(d, 500); reach >= 24; reach -= 64)
        {
            var pt = new RcVec3f((float)(_wx + ux * reach), (float)_wz, (float)(_wy + uy * reach));
            _query!.FindNearestPoly(pt, new RcVec3f(16, 256, 16), filter, out long r, out RcVec3f np, out _);
            if (r != 0) { goalPt = np; return r; }
        }
        return 0;
    }

    // Load up to a few landblocks toward the target so the navmesh extends ahead.
    private bool EnsureToward(double tew, double tns)
    {
        int pLbX = (int)((_cellId >> 24) & 0xFF), pLbY = (int)((_cellId >> 16) & 0xFF);
        int tLbX = (int)(tew / 192.0), tLbY = (int)(tns / 192.0);
        int dx = Math.Sign(tLbX - pLbX), dy = Math.Sign(tLbY - pLbY);
        bool changed = false;
        for (int step = 1; step <= 3; step++)
        {
            int x = pLbX + dx * step, y = pLbY + dy * step;
            if (x >= 0 && x <= 255 && y >= 0 && y <= 255) changed |= EnsureTile((uint)((x << 8) | y));
        }
        _tileCount = _loadedTiles.Count;
        return changed;
    }

    // ── Chat ─────────────────────────────────────────────────────────────────────
    // /rnav commands are queued and run on the tick (RunCommand in the Travel part).
    // Any other command typed is remembered for a moment: a recall announced right after
    // it ("X is recalling home.") learns that command.
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        if (!t.StartsWith("/rnav", StringComparison.OrdinalIgnoreCase) || (t.Length > 5 && t[5] != ' '))
        {
            if (t[0] is '@' or '/') { _lastChatCommand = t; _lastChatCommandMs = Environment.TickCount64; }
            return;
        }
        eat = 1;
        string raw = t.Length > 5 ? t.Substring(5).Trim() : string.Empty;
        if (raw.Length == 0) raw = "help";
        EnqueueCommand(raw);
    }

    private string Where()
    {
        if (!_hasPose) return "no player pose yet";
        uint lb = CurrentLandblock;
        bool exists = File.Exists(Path.Combine(NavDataDir, $"nav_{lb:X4}.tile"));
        int onDisk = 0;
        try { if (Directory.Exists(NavDataDir)) onDisk = Directory.GetFiles(NavDataDir, "nav_*.tile").Length; } catch { }
        bool indoors = (_cellId & 0xFFFF) >= 0x0100;
        bool dunFile = indoors && File.Exists(Path.Combine(NavDataDir, DungeonTileFile.FileName(lb)));
        bool dungeon = InDungeon(_cellId) || dunFile;
        string status; lock (_gate) status = _status;
        // Where exactly: the cell and the landblock-local position (what a dungeon's tile and its objects are placed in).
        double lx = _wx - ((_cellId >> 24) & 0xFF) * 192.0, ly = _wy - ((_cellId >> 16) & 0xFF) * 192.0;
        string at = $" cell {_cellId:X8} local ({lx:F1}, {ly:F1}, {_wz:F1}),";
        return $"landblock 0x{lb:X4}{(dungeon ? " (a dungeon)" : indoors ? " (indoors)" : "")},{at} — tile "
             + $"{(dungeon ? (dunFile ? $"dungeon navmesh {DungeonTileFile.FileName(lb)} FOUND (the mesh view draws it; goto doesn't walk dungeons yet)" : "no dungeon navmesh baked") : exists ? "FOUND" : "MISSING")}, {_tileCount} loaded, "
             + $"{onDisk} tile(s) in {NavDataDir}; route graph: {_graphStatus}; Town Network: {_townNetStatus}; goto {(_gotoActive ? "ACTIVE" : "off")}"
             + $"{(_coarse != null ? $" (route {_coarse.Crossed}/{_coarse.Count})" : "")}, portals {(_portalsEnabled ? "on" : "off")}; status: {status}";
    }

    // ── Status JSON for the panel ────────────────────────────────────────────────
    public string BuildStatusJson()
    {
        var ci = CultureInfo.InvariantCulture;
        bool hasPose = _hasPose;
        double ns = 0, ew = 0; uint lb = 0;
        if (hasPose) { ns = (_wy / 24.0 - 1019.5) / 10.0; ew = (_wx / 24.0 - 1019.5) / 10.0; lb = CurrentLandblock; }
        int tiles = _tileCount;
        string status, lastPath;
        lock (_gate) { status = _status; lastPath = _lastPath; }

        var sb = new StringBuilder(320);
        sb.Append('{');
        sb.Append("\"hasPose\":").Append(hasPose ? 1 : 0).Append(',');
        sb.Append("\"landblock\":\"").Append(hasPose ? lb.ToString("X4") : "----").Append("\",");
        // 0 in a dungeon: ns/ew are then not map coordinates (the panel says "in a dungeon").
        sb.Append("\"onMap\":").Append(hasPose && IsOnMap(_cellId) ? 1 : 0).Append(',');
        sb.Append("\"ns\":").Append(ns.ToString("F2", ci)).Append(',');
        sb.Append("\"ew\":").Append(ew.ToString("F2", ci)).Append(',');
        sb.Append("\"tileLoaded\":").Append(tiles > 0 ? 1 : 0).Append(',');
        sb.Append("\"loadedLb\":\"").Append(hasPose ? lb.ToString("X4") : "----").Append("\",");
        sb.Append("\"polyCount\":").Append(tiles).Append(',');
        sb.Append("\"runState\":").Append(_appliedRun).Append(',');
        sb.Append("\"turnState\":").Append(_appliedTurn).Append(',');
        sb.Append("\"goto\":").Append(_gotoActive ? 1 : 0).Append(',');
        sb.Append("\"status\":\"").Append(Esc(status)).Append("\",");
        sb.Append("\"lastPath\":\"").Append(Esc(lastPath)).Append('"');
        string travel;
        lock (_gate) travel = _travelJson;
        sb.Append(travel);
        AppendPanelJson(sb);
        sb.Append('}');
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", " ");
    private static string Fmt(double v, char pos, char neg) => NavCoords.FmtOne(v, pos, neg);
}

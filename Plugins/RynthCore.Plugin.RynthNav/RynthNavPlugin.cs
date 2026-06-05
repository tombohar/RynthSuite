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
public sealed class RynthNavPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthNav");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.5.4");

    private const string NavDataDir = @"C:\Games\RynthCore\NavData";
    private const int VertsPerPoly = 6;
    private const int WindowRadius = 2;        // load a 5x5 window around the player
    private const int KeepRadius = 4;          // evict tiles beyond a 9x9 window
    private const int MaxTiles = 256;
    private const int MaxCorridorTiles = 220;
    private const double ArrivalUnits = 7.0;

    // Portal routing.
    private const double PortalArriveUnits = 14.0;  // switch to "walk into portal" within this of the entrance
    private const double PortalContactUnits = 4.0;   // within this, stand on the portal instead of orbiting it
    private const double PortalJumpUnits = 240.0;    // a position jump this big = a teleport happened (1 /loc deg)
    private const int PortalWaitTimeoutTicks = 300;  // ~10s at 30Hz to trigger a portal before giving up

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

    public override int Initialize()
    {
        _status = "initialized";
        Host.Log($"[RynthNav] Initialized v0.5.4 (tiled streaming + long-range goto + portal routing). Panel: RynthNav. Tiles: {NavDataDir}");
        return 0;
    }

    public override void OnLoginComplete()
    {
        Host.Log("[RynthNav] Login — loading tile window.");
        lock (_reqGate) _reqLoad = true;
    }

    public override void OnLogout()
    {
        _navMesh = null; _query = null; _loadedTiles.Clear(); _tileCount = 0;
        lock (_gate) { _status = "logged out"; _lastPath = ""; }
        lock (_moveGate) { _desiredRun = 0; _turnState = 0; _haltPending = true; }
        _gotoActive = false;
        _route = null; _portalWait = false; _portalWaitTicks = 0; _wasPortaling = false;
        lock (_gotoGate) { _gotoPath = null; _gotoIdx = 0; }
    }

    public override void OnTick()
    {
        if (Host.HasGetPlayerPose &&
            Host.TryGetPlayerPose(out uint cell, out float x, out float y, out float z, out _, out _, out _, out _))
        {
            int lbX = (int)((cell >> 24) & 0xFF), lbY = (int)((cell >> 16) & 0xFF);
            _cellId = cell; _wx = lbX * 192.0 + x; _wy = lbY * 192.0 + y; _wz = z; _hasPose = true;
            uint lb = (cell >> 16) & 0xFFFF;
            if (lb != _lastSeenLb) { _lastSeenLb = lb; if (!_gotoActive) RefreshTiles(lb); }
            if ((++_posWriteTick % 60) == 0) WritePosFile(); // ~2s: feed the bake-ahead watcher
        }
        ProcessRequests();
        ApplyMovement();
    }

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
        if (_loadedTiles.Count >= MaxTiles - 1) return false;
        string path = Path.Combine(NavDataDir, $"nav_{lb:X4}.tile");
        if (!File.Exists(path)) return false;
        try
        {
            DtMeshData md;
            using (var fr = File.OpenRead(path)) using (var br = new BinaryReader(fr)) md = new DtMeshDataReader().Read(br, VertsPerPoly);
            EnsureNavMesh();
            _navMesh!.AddTile(md, 0, 0, out _);
            _loadedTiles.Add(lb);
            return true;
        }
        catch { return false; }
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

    // Feed the bake-ahead watcher: player landblock + goto-target landblock.
    private void WritePosFile()
    {
        try
        {
            int pX = (int)((_cellId >> 24) & 0xFF), pY = (int)((_cellId >> 16) & 0xFF);
            int tX = pX, tY = pY;
            if (_gotoActive) { tX = (int)(_gotoTew / 192.0); tY = (int)(_gotoTns / 192.0); }
            File.WriteAllText(Path.Combine(NavDataDir, "_player.txt"), $"{pX},{pY},{tX},{tY}");
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
        }
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
        bool load, test; string? prev, gotoTo;
        lock (_reqGate) { load = _reqLoad; _reqLoad = false; test = _reqTest; _reqTest = false; prev = _reqPreview; _reqPreview = null; gotoTo = _reqGoto; _reqGoto = null; }
        if (load && _hasPose) RefreshTiles(CurrentLandblock);
        if (test) TestImpl();
        if (prev != null) PathImpl(prev, walk: false);
        if (gotoTo != null) PathImpl(gotoTo, walk: true);
    }

    // Plan and report a portal route from here to a coord — no movement.
    // Pure CPU (planner does no AC access); safe to call on the chat thread.
    private void RouteImpl(string coord)
    {
        if (!_hasPose) { Host.WriteToChat("[RynthNav] no player pose yet", 1); return; }
        if (!TryParseLoc(coord, out double tns, out double tew)) { Host.WriteToChat("[RynthNav] bad coord — e.g. /rnav route 2.7N, 18.9E", 1); return; }
        var steps = PlanRoute(tns, tew, out int used, out double est);
        if (steps == null || used == 0)
        {
            Host.WriteToChat($"[RynthNav] route to {Fmt(tns, 'N', 'S')} {Fmt(tew, 'E', 'W')}: walk directly (no portal helps)", 1);
            return;
        }
        Host.WriteToChat($"[RynthNav] route to {Fmt(tns, 'N', 'S')} {Fmt(tew, 'E', 'W')}: ~{est:F0}u, {used} portal(s):", 1);
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            string act = s.UsePortal ? $"portal '{Trunc(s.Label)}'" : "walk to goal";
            Host.WriteToChat($"  {i + 1}. {Fmt(s.Ns, 'N', 'S')} {Fmt(s.Ew, 'E', 'W')} — {act}", 1);
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
        if (!_hasPose) { lock (_gate) _status = "no pose"; return; }
        if (!TryParseLoc(coord, out double tns, out double tew)) { lock (_gate) { _status = "bad coord"; _lastPath = "type e.g. 42.5N, 33.6E"; } return; }

        double twx = (tew * 10.0 + 1019.5) * 24.0;
        double twy = (tns * 10.0 + 1019.5) * 24.0;

        if (walk)
        {
            _finalTew = twx; _finalTns = twy;
            _replanTick = 0;
            _portalWait = false; _portalWaitTicks = 0; _wasPortaling = false;
            lock (_gotoGate) { _gotoPath = null; _gotoIdx = 0; }
            lock (_moveGate) { _desiredRun = 0; _turnState = 0; }

            // Plan a portal route from here to the target. If it uses portals, walk the
            // legs; otherwise StepGoto just navmesh-walks straight to the final target.
            _route = PlanRoute(tns, tew, out int portalsUsed, out double est);
            _routeIdx = 0;
            SetSubGoalToCurrentLeg();

            _gotoActive = true;
            string via = (_route != null && portalsUsed > 0) ? $" via {portalsUsed} portal(s), ~{est:F0}u" : "";
            lock (_gate) { _status = $"walking -> {Fmt(tns, 'N', 'S')} {Fmt(tew, 'E', 'W')}{via}"; _lastPath = "planning…"; }
            Host.Log($"[RynthNav] goto to ({tns:F2},{tew:F2}){via}");
            return;
        }

        // Preview: bounded one-shot path (nearby targets only).
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

    // ── Movement (d-pad), unchanged ──────────────────────────────────────────────
    public void DoMove(int cmd)
    {
        _gotoActive = false; // any manual input cancels auto-walk
        _route = null; _portalWait = false;
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
        if (!_hasPose || _query == null) return;

        if (_portalWait) { StepPortalWait(); return; }

        bool isPortalLeg = _route != null && _routeIdx < _route.Count && _route[_routeIdx].UsePortal;
        double arrive = isPortalLeg ? PortalArriveUnits : ArrivalUnits;
        double fdew = _gotoTew - _wx, fdns = _gotoTns - _wy;
        if (Math.Sqrt(fdew * fdew + fdns * fdns) < arrive)
        {
            OnSubGoalReached();
            return;
        }

        if (_gotoPath == null || (++_replanTick % 20) == 0) Replan();

        List<(double ew, double ns)>? path; int idx;
        lock (_gotoGate) { path = _gotoPath; idx = _gotoIdx; }
        if (path == null || path.Count == 0) { Host.SetAutoRun(false); return; }

        while (idx < path.Count)
        {
            double ddew = path[idx].ew - _wx, ddns = path[idx].ns - _wy;
            if (Math.Sqrt(ddew * ddew + ddns * ddns) < ArrivalUnits) idx++;
            else break;
        }
        lock (_gotoGate) _gotoIdx = idx;
        if (idx >= path.Count) return; // reached local sub-goal; re-plan next tick

        double dew = path[idx].ew - _wx, dns = path[idx].ns - _wy;
        double desired = Math.Atan2(dew, dns) * 180.0 / Math.PI;
        if (desired < 0) desired += 360.0;
        Host.TurnToHeading((float)desired);
        Host.SetAutoRun(true);
    }

    // ── Portal routing ───────────────────────────────────────────────────────────
    // Reached the current leg's target: finish, take the portal, or advance.
    private void OnSubGoalReached()
    {
        if (_route == null) { FinishGoto("arrived"); return; }
        var step = _route[_routeIdx];
        if (step.UseRecall)
        {
            // Recalls need magic/wand handling we don't have here yet; skip the hop.
            Host.Log("[RynthNav] route recall step skipped (not supported in plugin v1)");
            AdvanceRoute();
            return;
        }
        if (step.UsePortal)
        {
            _portalWait = true; _portalWaitTicks = 0; _wasPortaling = false;
            _prePortalWx = _wx; _prePortalWy = _wy; _prePortalLb = CurrentLandblock;
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
    }

    private void FinishGoto(string status)
    {
        _gotoActive = false; _route = null; _portalWait = false;
        Host.SetAutoRun(false); Host.StopCompletely();
        lock (_gate) _status = status;
        Host.Log($"[RynthNav] goto: {status}");
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
            Host.SetAutoRun(false); Host.StopCompletely();
            _portalWait = false;
            Host.Log($"[RynthNav] teleport detected (lb 0x{_prePortalLb:X4}->0x{CurrentLandblock:X4}, moved {moved:F0}u) — next leg");
            RefreshTiles(CurrentLandblock); // stream the area we landed in
            AdvanceRoute();
            return;
        }

        if (++_portalWaitTicks > PortalWaitTimeoutTicks)
        {
            Host.Log("[RynthNav] portal did not fire (coord ~off, or it needs a click) — stopping");
            FinishGoto("portal didn't fire — check route");
            return;
        }

        double dew = _gotoTew - _wx, dns = _gotoTns - _wy;
        double d = Math.Sqrt(dew * dew + dns * dns);
        if (d > PortalContactUnits)
        {
            // Approach: face the entrance and run in.
            double desired = Math.Atan2(dew, dns) * 180.0 / Math.PI;
            if (desired < 0) desired += 360.0;
            Host.TurnToHeading((float)desired);
            Host.SetAutoRun(true);
        }
        else
        {
            // Within contact range: stop and stand on it so the collision can fire
            // instead of overshooting and orbiting. Nudge forward briefly every ~1s
            // in case we settled just short of the trigger.
            if ((_portalWaitTicks % 30) < 4) Host.SetAutoRun(true);
            else { Host.SetAutoRun(false); Host.StopCompletely(); }
        }
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
                    list.Add(new PortalLink(sns, sew, dns, dew, f.Length > 4 ? f[4] : ""));
                }
                Host.Log($"[RynthNav] loaded {list.Count} portals from portals.tsv");
            }
            else Host.Log("[RynthNav] portals.tsv not found — portal routing off");
        }
        catch (Exception ex) { Host.Log($"[RynthNav] portals load failed: {ex.Message}"); }
        _portals = list;
    }

    // Returns the leg list if a portal route beats walking, else null (pure navmesh goto).
    private List<RouteStep>? PlanRoute(double goalNs, double goalEw, out int portalsUsed, out double est)
    {
        portalsUsed = 0; est = 0;
        if (!_portalsEnabled) return null;
        EnsurePortalsLoaded();
        if (_portals == null || _portals.Count == 0) return null;
        double startNs = (_wy / 24.0 - 1019.5) / 10.0;
        double startEw = (_wx / 24.0 - 1019.5) / 10.0;
        var steps = PortalRoute.Plan(_portals, null, startNs, startEw, goalNs, goalEw, out est, out portalsUsed);
        return portalsUsed > 0 ? steps : null;
    }

    private static string Trunc(string s) => s.Length <= 28 ? s : s.Substring(0, 28);

    // Re-plan toward the (possibly far) target using loaded tiles, streaming the
    // window + a few tiles toward the goal so the navmesh keeps extending ahead.
    private void Replan()
    {
        int pLbX = (int)((_cellId >> 24) & 0xFF), pLbY = (int)((_cellId >> 16) & 0xFF);
        bool changed = EnsureWindow(pLbX, pLbY);
        changed |= EnsureToward(_gotoTew, _gotoTns);
        if (changed || _query == null) _query = new DtNavMeshQuery(_navMesh!);
        if (_query == null) return;

        var filter = new DtQueryDefaultFilter();
        var startP = new RcVec3f((float)_wx, (float)_wz, (float)_wy);
        _query.FindNearestPoly(startP, new RcVec3f(8, 64, 8), filter, out long sRef, out RcVec3f sPt, out _);
        if (sRef == 0) { lock (_gate) _status = "off navmesh"; return; }

        // Prefer a DIRECT path to the actual target when its tile is loaded — one stable
        // corridor we follow steadily. Only fall back to the 500u line-probe leapfrog when
        // the target is too far to be loaded yet (that probe jitters and must stay a last resort).
        var targetP = new RcVec3f((float)_gotoTew, (float)_wz, (float)_gotoTns);
        _query.FindNearestPoly(targetP, new RcVec3f(12, 256, 12), filter, out long gRef, out RcVec3f gPt, out _);
        if (gRef == 0) gRef = FindGoalToward(_gotoTew, _gotoTns, out gPt);
        if (gRef == 0) { lock (_gate) _status = "no tile ahead — bake the route"; return; }

        Span<long> p = new long[512];
        _query.FindPath(sRef, gRef, sPt, gPt, filter, p, out int pc, 512);
        Span<DtStraightPath> sp = new DtStraightPath[512];
        _query.FindStraightPath(sPt, gPt, p[..pc], pc, sp, out int spc, 512, 0);
        if (spc < 2) { lock (_gate) _status = "no path"; return; }

        var wps = new List<(double, double)>(spc);
        for (int i = 0; i < spc; i++) wps.Add((sp[i].pos.X, sp[i].pos.Z));
        lock (_gotoGate) { _gotoPath = wps; _gotoIdx = 0; }
        int rem = (int)Math.Sqrt((_gotoTew - _wx) * (_gotoTew - _wx) + (_gotoTns - _wy) * (_gotoTns - _wy));
        lock (_gate) _lastPath = $"{rem}u to target";
    }

    // Farthest loaded poly along the straight line toward the target.
    private long FindGoalToward(double tew, double tns, out RcVec3f goalPt)
    {
        goalPt = default;
        double dEW = tew - _wx, dNS = tns - _wy;
        double d = Math.Sqrt(dEW * dEW + dNS * dNS);
        if (d < 1) return 0;
        double ux = dEW / d, uy = dNS / d;
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
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/rnav", StringComparison.OrdinalIgnoreCase)) return;
        eat = 1;
        string raw = text.Length > 5 ? text.Substring(5).Trim() : string.Empty;
        int sp = raw.IndexOf(' ');
        string cmd = (sp < 0 ? raw : raw.Substring(0, sp)).ToLowerInvariant();
        string rest = sp < 0 ? string.Empty : raw.Substring(sp + 1).Trim();
        switch (cmd)
        {
            case "":
            case "help": Host.WriteToChat("[RynthNav] /rnav goto <coord> | route <coord> | portals on|off | stop | here | load | test", 1); break;
            case "here": Host.WriteToChat($"[RynthNav] {Where()}", 1); break;
            case "load": DoLoadTile(); Host.WriteToChat("[RynthNav] loading tile window…", 1); break;
            case "test": DoTestQuery(); Host.WriteToChat("[RynthNav] testing…", 1); break;
            case "goto": DoGoto(rest); Host.WriteToChat($"[RynthNav] goto {rest}", 1); break;
            // Route preview is pure CPU (no AC access) — run it here on the chat thread
            // so its WriteToChat output actually surfaces.
            case "route": RouteImpl(rest); break;
            case "preview": DoPreviewPath(rest); Host.WriteToChat($"[RynthNav] preview {rest}", 1); break;
            case "portals":
                _portalsEnabled = !rest.Equals("off", StringComparison.OrdinalIgnoreCase);
                Host.WriteToChat($"[RynthNav] portal routing {(_portalsEnabled ? "ON" : "OFF")}", 1);
                break;
            case "stop": DoMove(5); Host.WriteToChat("[RynthNav] stopped", 1); break;
            default: Host.WriteToChat($"[RynthNav] unknown '{cmd}'", 1); break;
        }
    }

    private string Where()
    {
        if (!_hasPose) return "no player pose yet";
        uint lb = CurrentLandblock;
        bool exists = File.Exists(Path.Combine(NavDataDir, $"nav_{lb:X4}.tile"));
        return $"landblock 0x{lb:X4} — tile {(exists ? "FOUND" : "MISSING")}, {_tileCount} loaded";
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
        sb.Append('}');
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Fmt(double v, char pos, char neg) => $"{Math.Abs(v):F1}{(v >= 0 ? pos : neg)}";

    private static bool TryParseLoc(string? s, out double ns, out double ew)
    {
        ns = 0; ew = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        return TryCoord(parts[0], out ns) && TryCoord(parts[1], out ew);
    }

    private static bool TryCoord(string tok, out double val)
    {
        val = 0;
        tok = tok.Trim().ToUpperInvariant();
        if (tok.Length == 0) return false;
        int sign = 1;
        char last = tok[^1];
        if (last is 'N' or 'S' or 'E' or 'W') { if (last is 'S' or 'W') sign = -1; tok = tok[..^1]; }
        if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out val)) return false;
        val *= sign;
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

// RynthNav's long-range walking: the coarse route graph (NavData\navgraph.bin, see NavGraph)
// tells the walker which way round a bay, a lake or a ridge to go; the detailed Detour planner
// still does every step, inside the usual 5x5 tile window. Without a graph (or with one from
// another bake) goto walks as before: straight at the target as far as the navmesh allows.
// Tick thread, except the background load.
public sealed partial class RynthNavPlugin
{
    private const int CoarseRetryReplans = 5;    // a failed or abandoned route is planned again after this many re-plans (~3 s)
    private const int CoarseNoRouteReplans = 100; // after "no route": try again in ~60 s (the player may have moved)
    private const int CoarseOffRouteReplans = 3;  // re-plans off the route before planning it again
    private const int CoarseLoadAhead = 3;       // load route tiles up to this many landblocks from the player

    private volatile NavGraph? _graph;
    private volatile string _graphStatus = "loading…";
    private NavGraph? _graphChecked;             // the graph every loaded tile has been checked against
    // Region per polygon, and the fingerprint, of every loaded tile.
    private readonly Dictionary<uint, int[]> _tileRegions = new();
    private readonly Dictionary<uint, (int Polys, int Verts, int Tris)> _tilePrints = new();
    private NavCoarse? _coarse;                  // the current leg's route (null = none, or none yet)
    private int _coarseRetry;                    // re-plans left before planning a route again
    private int _coarseOff;                      // re-plans in a row off the route
    private bool _coarseAnnounced, _coarseNoRouteSaid;
    private double _aimEw, _aimNs;               // where the last re-plan aimed (the blind push follows it)

    private void LoadGraph()
    {
        string path = Path.Combine(NavDataDir, NavGraph.FileName);
        Task.Run(() =>
        {
            try
            {
                if (!File.Exists(path)) { _graphStatus = "no navgraph.bin: long gotos head straight for the target"; return; }
                NavGraph g = NavGraph.Load(path);
                g.BuildComponents();   // landmasses: which walks can be done at all
                _graph = g;
                _graphStatus = $"{g.TileCount} tiles, {g.PortalCount} portals (set {g.SetIdHex[..8]})";
                Host.Log($"[RynthNav] route graph: {_graphStatus}");
            }
            catch (Exception ex)
            {
                _graphStatus = $"navgraph.bin failed to load ({ex.GetType().Name}: {ex.Message}): long gotos head straight for the target";
                Host.Log($"[RynthNav] {_graphStatus}");
            }
        });
    }

    private void OnTileLoaded(uint lb, DtMeshData md)
    {
        _tileRegions[lb] = NavRegions.Build(md, out _);
        _tilePrints[lb] = NavRegions.Fingerprint(md);
        _meshView.Register(lb, md);
        NavGraph? g = _graph;
        if (g != null && ReferenceEquals(g, _graphChecked)) CheckTile(g, lb);
    }

    private void OnTileUnloaded(uint lb) { _tileRegions.Remove(lb); _tilePrints.Remove(lb); _meshView.Unregister(lb); }

    private void CheckTile(NavGraph g, uint lb)
    {
        if (!_tilePrints.TryGetValue(lb, out var fp) || g.Matches(lb, fp.Polys, fp.Verts, fp.Tris)) return;
        DropGraph($"tile 0x{lb:X4} isn't the one it was built from (a graph from another bake, or tiles changed since)");
    }

    private void DropGraph(string why)
    {
        _graph = null; _graphChecked = null; _coarse = null; _coarseTask = null; _coarseVersion++;
        _graphStatus = "ignored: " + why;
        Host.Log($"[RynthNav] route graph {_graphStatus}");
    }

    /// <summary>The graph, once every loaded tile matches it; null to walk without it.</summary>
    private NavGraph? UsableGraph()
    {
        NavGraph? g = _graph;
        if (g == null) return null;
        if (!ReferenceEquals(g, _graphChecked))
        {
            _graphChecked = g;
            foreach (uint lb in new List<uint>(_tilePrints.Keys)) { CheckTile(g, lb); if (_graph == null) return null; }
        }
        return _graph;
    }

    /// <summary>A new leg (or goto): its route is planned on the first re-plan.</summary>
    /// <summary>
    /// Can the target's landblock be walked to from region <paramref name="here"/>? The region's own
    /// landmass when it's a real one; on a roof or a ledge (a tiny landmass), its landblock's.
    /// </summary>
    private static bool? LandFromRegion(NavGraph g, int here, uint goalLb)
    {
        int c = g.Component(here);
        int[] goalComps = g.LandblockComponents(goalLb);
        if (c < 0 || goalComps.Length == 0) return null;
        if (g.ComponentSize(c) < 8) return g.SameLand(g.RegionLandblock(here), goalLb);
        return Array.IndexOf(goalComps, c) >= 0;
    }

    private void ResetCoarse()
    {
        _coarse = null; _coarseRetry = 0; _coarseOff = 0; _coarseAnnounced = false; _coarseNoRouteSaid = false;
        _coarseTask = null; _coarseVersion++;
    }

    // The search runs on a worker (it touches only the immutable graph; the navmesh walks it
    // needs at both ends are measured on the tick first). Its result is picked up on a later
    // re-plan, if the leg it was planned for is still the current one.
    private Task<(NavCoarse? Route, int Expanded, long Ms, int Version, int Here, int Goal, double Straight)>? _coarseTask;
    private int _coarseVersion;

    /// <summary>
    /// Called from Replan with the polygon underfoot: keeps the route in step with the player,
    /// plans it when needed, loads its tiles ahead, and returns the point to plan to next
    /// (null = the target itself, as without a graph). Sets <paramref name="tilesChanged"/>
    /// when it loaded tiles (the query must be rebuilt).
    /// </summary>
    private (float Ew, float Up, float Ns)? CoarseSteer(long sRef, RcVec3f sPt, ref bool tilesChanged)
    {
        NavGraph? g = UsableGraph();
        if (g == null) { _coarse = null; _coarseTask = null; _coarseHoldTicks = 0; return null; }
        TakeCoarseResult();
        int here = NavCoarse.RegionOf(g, _navMesh!, sRef, _tileRegions);
        // Off the route: the detailed path can cut through a small region the route doesn't
        // list (a ledge, a porch), so only plan again after a few re-plans (~2 s) off it.
        if (_coarse != null)
        {
            if (_coarse.Advance(here)) _coarseOff = 0;
            else if (++_coarseOff >= CoarseOffRouteReplans) { _coarse = null; _coarseRetry = 0; _coarseOff = 0; }
        }
        if (_coarse == null)
        {
            if (_coarseTask != null) return null;   // being planned
            if (_coarseRetry > 0) { _coarseRetry--; return null; }
            _coarseRetry = CoarseRetryReplans;
            if (here < 0) { _coarseHoldTicks = 0; return null; }
            PlanCoarse(g, sRef, sPt);
            if (_coarseTask == null) _coarseHoldTicks = 0;   // nothing to wait for (no ground at one end)
            return null;
        }

        int pX = (int)((_cellId >> 24) & 0xFF), pY = (int)((_cellId >> 16) & 0xFF);
        foreach (uint lb in _coarse.Upcoming(8))
        {
            int x = (int)(lb >> 8), y = (int)(lb & 0xFF);
            if (Math.Max(Math.Abs(x - pX), Math.Abs(y - pY)) > CoarseLoadAhead) break;
            if (!_loadedTiles.Contains(lb) && EnsureTile(lb)) tilesChanged = true;
        }
        _tileCount = _loadedTiles.Count;
        var s = _coarse.Steer(lb => _loadedTiles.Contains(lb), out bool final);
        return final ? null : s;
    }

    private void PlanCoarse(NavGraph g, long sRef, RcVec3f sPt)
    {
        NavCoarse.Probe? start = _query == null ? null : NavCoarse.ProbeAt(g, _navMesh!, _query, sRef, sPt, _tileRegions);
        if (start == null) return;
        // The target's region: from the loaded navmesh when its tile is in, else from its tile file.
        NavCoarse.Probe? target = null;
        int gx = (int)Math.Floor(_gotoTew / 192.0), gy = (int)Math.Floor(_gotoTns / 192.0);
        uint goalLb = gx is >= 0 and <= 255 && gy is >= 0 and <= 255 ? (uint)((gx << 8) | gy) : 0;
        try
        {
            target = _loadedTiles.Contains(goalLb)
                ? NavCoarse.ProbeNear(g, _navMesh!, _query!, _gotoTew, _wz, _gotoTns, 40f, _tileRegions)
                : NavCoarse.ProbeFile(g, NavDataDir, _gotoTew, _wz, _gotoTns, 40f);
        }
        catch (Exception ex) { Host.Log($"[RynthNav] route: target tile unreadable: {ex.Message}"); }
        if (target == null) return;   // no navmesh at the target: head straight, as without a graph

        NavCoarse.Request req = NavCoarse.Request.Measure(g, start, target);
        int version = _coarseVersion, here = start.Region, goal = target.Region;
        double straight = Math.Sqrt((_gotoTew - _wx) * (_gotoTew - _wx) + (_gotoTns - _wy) * (_gotoTns - _wy));
        _coarseTask = Task.Run(() =>
        {
            long t0 = Environment.TickCount64;
            NavCoarse? c = NavCoarse.Plan(g, req, out int expanded);
            return (c, expanded, Environment.TickCount64 - t0, version, here, goal, straight);
        });
    }

    private void TakeCoarseResult()
    {
        if (_coarseTask is not { IsCompleted: true } task) return;
        _coarseTask = null;
        if (task.IsFaulted) { Host.Log($"[RynthNav] route search failed: {task.Exception?.GetBaseException().Message}"); return; }
        var (c, expanded, ms, version, here, goal, straight) = task.Result;
        if (version != _coarseVersion || !_gotoActive) return;   // planned for a leg that's over
        _coarseHoldTicks = 0;   // the answer is in: walk (or stop)
        if (c == null)
        {
            NavGraph? g = _graph;
            uint goalLb = NavGraph.LandblockAt(_gotoTew, _gotoTns);
            bool? land = g == null ? null : LandFromRegion(g, here, goalLb);
            Host.Log($"[RynthNav] route: none from region {here} to {goal} ({expanded} expanded, {ms} ms); over land: {(land == null ? "unknown" : land.Value ? "the target's landblock has ground on this landmass" : "no, another landmass")}");
            _coarseRetry = CoarseNoRouteReplans;   // searching a whole landmass again every few seconds would cost
            if (land == false)
            {
                // Water or cliffs between here and there: walking (or a blind push) only leads
                // somewhere wrong, like into a portal. Plan with portals and recalls, or stop.
                NoLandRoute();
                return;
            }
            if (!_coarseNoRouteSaid)
            {
                _coarseNoRouteSaid = true;
                Say("goto: no walkable route to that exact spot on the navmesh (a roof or a ledge?); heading for it as far as it goes");
            }
            return;
        }
        _coarse = c;
        _coarseOff = 0;
        // A new route measures progress differently: restart the watchdog's best.
        _bestDist = double.MaxValue; _bestTick = _legTicks;
        Host.Log($"[RynthNav] route: {c.Count} border crossing(s), ~{c.Cost:F0}u walking vs {straight:F0}u straight ({expanded} expanded, {ms} ms)");
        if (!_coarseAnnounced && c.Count > 0)
        {
            _coarseAnnounced = true;
            if (c.Cost > straight * 1.15) Say($"goto: the way over land is about {c.Cost:F0} yd (straight line {straight:F0} yd)");
        }
    }
}

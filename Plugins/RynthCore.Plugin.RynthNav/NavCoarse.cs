using System;
using System.Collections.Generic;
using System.IO;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;

namespace RynthNav.Routing;

/// <summary>
/// A long-range walking route over <see cref="NavGraph"/>: the portals to cross, in order, from
/// the region underfoot to the target's region. The walker keeps its 5x5 tile window and the
/// detailed Detour planner; this only tells it which point to plan to next (the furthest portal
/// on the route whose tiles are loaded), so a goto can swing around a bay, a lake or a ridge
/// instead of probing along the straight line. Tick thread only (Detour objects are not shared).
/// </summary>
internal sealed class NavCoarse
{
    private readonly NavGraph _g;
    private readonly List<int> _portals;
    private readonly int[] _regions;      // _regions[i] = region before portal i; _regions[^1] = the goal's region
    private int _idx;                     // portals crossed so far
    public double GoalEw { get; }
    public double GoalNs { get; }
    public float Cost { get; }
    public int Count => _portals.Count;
    public int Crossed => _idx;

    private NavCoarse(NavGraph g, List<int> portals, int startRegion, double gx, double gz, float cost)
    {
        _g = g; _portals = portals; GoalEw = gx; GoalNs = gz; Cost = cost;
        _regions = new int[portals.Count + 1];
        _regions[0] = startRegion;
        for (int i = 0; i < portals.Count; i++) _regions[i + 1] = g.Across(portals[i], _regions[i]);
    }

    /// <summary>
    /// A* over the graph from <paramref name="start"/> to <paramref name="goal"/>; null when it has no
    /// route between their regions. The first and last legs are measured on the navmesh (the start's
    /// and the goal's tiles), so a portal behind a wall in the start tile isn't mistaken for a near one.
    /// </summary>
    /// <summary>A* with straight first and last legs (no navmesh at hand: tests, tools).</summary>
    public static NavCoarse? Plan(NavGraph g, int startRegion, double sx, double sz, int goalRegion, double gx, double gz, out int expanded)
    {
        List<int>? route = g.FindRoute(startRegion, (float)sx, (float)sz, goalRegion, (float)gx, (float)gz, out float cost, out expanded);
        return route == null ? null : new NavCoarse(g, route, startRegion, gx, gz, cost);
    }

    public static NavCoarse? Plan(NavGraph g, Probe start, Probe goal, out int expanded)
        => Plan(g, Request.Measure(g, start, goal), out expanded);

    /// <summary>
    /// Everything A* needs, measured on the navmesh up front (tick thread): the walks from the start
    /// to its region's portals and from the goal's region's portals to the goal. The search itself
    /// then touches only the graph, so it can run on a worker.
    /// </summary>
    internal sealed class Request
    {
        public int StartRegion, GoalRegion;
        public float Sx, Sz, Gx, Gz;
        public Dictionary<int, float> StartLegs = new(), GoalLegs = new();

        public static Request Measure(NavGraph g, Probe start, Probe goal)
        {
            var r = new Request { StartRegion = start.Region, GoalRegion = goal.Region, Sx = start.Pt.X, Sz = start.Pt.Z, Gx = goal.Pt.X, Gz = goal.Pt.Z };
            foreach (int p in g.RegionPortals(start.Region)) r.StartLegs[p] = start.WalkTo(g.Portal(p));
            if (start.Region != goal.Region)
                foreach (int p in g.RegionPortals(goal.Region)) r.GoalLegs[p] = goal.WalkTo(g.Portal(p));
            return r;
        }
    }

    /// <summary>A* for a measured request. Touches only the (immutable) graph: safe on a worker thread.</summary>
    public static NavCoarse? Plan(NavGraph g, Request q, out int expanded)
    {
        List<int>? route = g.FindRoute(q.StartRegion, q.Sx, q.Sz, q.GoalRegion, q.Gx, q.Gz, out float cost, out expanded,
            startLeg: p => q.StartLegs.TryGetValue(p, out float c) ? c : float.NaN,
            goalLeg: p => q.GoalLegs.TryGetValue(p, out float c) ? c : float.NaN);
        return route == null ? null : new NavCoarse(g, route, q.StartRegion, q.Gx, q.Gz, cost);
    }

    /// <summary>
    /// Follows the player: the route position moves to the furthest region on the rest of the route
    /// that is the region underfoot. False when that region isn't on the rest of the route (wandered
    /// off it, or knocked onto a roof): plan again.
    /// </summary>
    public bool Advance(int currentRegion)
    {
        if (currentRegion < 0) return true;   // between polygons for a moment: keep going
        for (int k = _regions.Length - 1; k >= _idx; k--)
            if (_regions[k] == currentRegion) { _idx = k; return true; }
        return false;
    }

    /// <summary>
    /// Where to plan to next: the furthest portal on the rest of the route whose tiles are all
    /// loaded (scanning stops at the first one that isn't), or the goal itself once every
    /// remaining portal is loaded (<paramref name="final"/>).
    /// </summary>
    public (float Ew, float Up, float Ns) Steer(Func<uint, bool> loaded, out bool final)
    {
        int j = -1;
        for (int i = _idx; i < _portals.Count; i++)
        {
            if (!loaded(_g.RegionLandblock(_regions[i])) || !loaded(_g.RegionLandblock(_regions[i + 1]))) break;
            j = i;
        }
        final = _idx >= _portals.Count || j == _portals.Count - 1;
        if (final) return ((float)GoalEw, float.NaN, (float)GoalNs);
        // The next portal isn't loaded yet (j < _idx): aim at it anyway; the caller falls back to
        // probing toward it until its tile streams in.
        return _g.Portal(_portals[Math.Max(j, _idx)]);
    }

    /// <summary>Landblocks of the next few regions on the route (to load ahead).</summary>
    public IEnumerable<uint> Upcoming(int count)
    {
        uint last = uint.MaxValue;
        for (int i = _idx; i < _regions.Length && count > 0; i++)
        {
            uint lb = _g.RegionLandblock(_regions[i]);
            if (lb == last) continue;
            last = lb; count--;
            yield return lb;
        }
    }

    /// <summary>
    /// What is left to walk: to the next portal, portal to portal, then to the goal (straight
    /// lines). Falls as the walker follows the route even where the route turns away from the goal,
    /// so the no-progress watchdog doesn't stop a long detour.
    /// </summary>
    public double Remaining(double ew, double ns)
    {
        double total = 0, x = ew, z = ns;
        for (int i = _idx; i < _portals.Count; i++)
        {
            var p = _g.Portal(_portals[i]);
            total += Math.Sqrt((p.Ew - x) * (p.Ew - x) + (p.Ns - z) * (p.Ns - z));
            x = p.Ew; z = p.Ns;
        }
        return total + Math.Sqrt((GoalEw - x) * (GoalEw - x) + (GoalNs - z) * (GoalNs - z));
    }

    // ── Regions and walking distances at the two ends (shared by the plugin and the harness) ──

    /// <summary>A point on the navmesh, its graph region, and a query to measure walks from it.</summary>
    internal sealed class Probe
    {
        public readonly DtNavMeshQuery Query;
        public readonly long Ref;
        public readonly RcVec3f Pt;
        public readonly int Region;

        public Probe(DtNavMeshQuery q, long polyRef, RcVec3f pt, int region) { Query = q; Ref = polyRef; Pt = pt; Region = region; }

        /// <summary>Walking distance (2D) from here to a portal point; NaN when the navmesh can't tell.</summary>
        public float WalkTo((float Ew, float Up, float Ns) portal)
        {
            var filter = new DtQueryDefaultFilter();
            var target = new RcVec3f(portal.Ew, portal.Up, portal.Ns);
            Query.FindNearestPoly(target, new RcVec3f(3, 8, 3), filter, out long pref, out RcVec3f ppt, out _);
            if (pref == 0) return float.NaN;
            if (pref == Ref) return Flat(Pt, ppt);
            Span<long> path = new long[1024];
            var st = Query.FindPath(Ref, pref, Pt, ppt, filter, path, out int pc, 1024);
            if (st.Failed() || pc == 0 || path[pc - 1] != pref) return float.NaN;
            Span<DtStraightPath> sp = new DtStraightPath[256];
            Query.FindStraightPath(Pt, ppt, path[..pc], pc, sp, out int spc, 256, 0);
            if (spc < 2) return float.NaN;
            float len = 0;
            for (int i = 1; i < spc; i++) len += Flat(sp[i - 1].pos, sp[i].pos);
            return len + Flat(sp[spc - 1].pos, ppt);
        }

        private static float Flat(RcVec3f a, RcVec3f b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    }

    /// <summary>A probe on the loaded navmesh at a polygon already found (the one underfoot).</summary>
    public static Probe? ProbeAt(NavGraph g, DtNavMesh nav, DtNavMeshQuery q, long polyRef, RcVec3f pt, IReadOnlyDictionary<uint, int[]> tileRegions)
    {
        int r = RegionOf(g, nav, polyRef, tileRegions);
        return r < 0 ? null : new Probe(q, polyRef, pt, r);
    }

    /// <summary>A probe on the loaded navmesh near a point (12 units, then <paramref name="reach"/>).</summary>
    public static Probe? ProbeNear(NavGraph g, DtNavMesh nav, DtNavMeshQuery q, double ew, double up, double ns, float reach, IReadOnlyDictionary<uint, int[]> tileRegions)
    {
        var filter = new DtQueryDefaultFilter();
        foreach (float r in new[] { 12f, reach })
        {
            q.FindNearestPoly(new RcVec3f((float)ew, (float)up, (float)ns), new RcVec3f(r, 512, r), filter, out long pref, out RcVec3f pt, out _);
            if (pref != 0) return ProbeAt(g, nav, q, pref, pt, tileRegions);
        }
        return null;
    }

    /// <summary>The graph region of a polygon of a loaded tile, or -1.</summary>
    public static int RegionOf(NavGraph g, DtNavMesh nav, long polyRef, IReadOnlyDictionary<uint, int[]> tileRegions)
    {
        if (polyRef == 0) return -1;
        if (nav.GetTileAndPolyByRef(polyRef, out DtMeshTile tile, out DtPoly poly).Failed() || tile?.data?.header == null) return -1;
        uint lb = (uint)((tile.data.header.x << 8) | tile.data.header.y);
        if (!tileRegions.TryGetValue(lb, out int[]? regions) || poly.index >= regions.Length) return -1;
        return g.Region(lb, regions[poly.index]);
    }

    /// <summary>
    /// A probe at a point of a tile that may not be loaded: reads the tile file into a throwaway
    /// one-tile navmesh. Null when there is no tile, it doesn't match the graph, or no walkable
    /// ground is within <paramref name="reach"/> units.
    /// </summary>
    public static Probe? ProbeFile(NavGraph g, string navDataDir, double ew, double up, double ns, float reach)
    {
        int x = (int)Math.Floor(ew / 192.0), y = (int)Math.Floor(ns / 192.0);
        if (x < 0 || x > 255 || y < 0 || y > 255) return null;
        uint lb = (uint)((x << 8) | y);
        string path = Path.Combine(navDataDir, $"nav_{lb:X4}.tile");
        if (!File.Exists(path)) return null;
        DtMeshData md;
        using (var fr = File.OpenRead(path)) using (var br = new BinaryReader(fr)) md = new DtMeshDataReader().Read(br, 6);
        var (polys, verts, tris) = NavRegions.Fingerprint(md);
        if (!g.Matches(lb, polys, verts, tris)) return null;
        var regions = new Dictionary<uint, int[]> { [lb] = NavRegions.Build(md, out _) };
        var nav = new DtNavMesh();
        var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 2, maxPolys = 1 << 16 };
        nav.Init(ref prm, 6);
        nav.AddTile(md, 0, 0, out _);
        return ProbeNear(g, nav, new DtNavMeshQuery(nav), ew, float.IsFinite((float)up) ? up : 0, ns, reach, regions);
    }
}

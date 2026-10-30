using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using DotRecast.Recast.Toolset;
using DotRecast.Recast.Toolset.Builder;
using RynthNav.Baker;
using RynthNav.Routing;

internal static class Program
{
    private static int _fails, _asserts;
    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private static string _root = "";

    private static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "rnav-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            Console.WriteLine("=== RynthNav route graph tests ===");
            Run(nameof(RegionsSplitIslands), RegionsSplitIslands);
            Run(nameof(PortalsMatchAcrossTheSeam), PortalsMatchAcrossTheSeam);
            Run(nameof(GapSplitsAStretchAndCostsFollowTheFloor), GapSplitsAStretchAndCostsFollowTheFloor);
            Run(nameof(AStarPicksTheCheaperWay), AStarPicksTheCheaperWay);
            Run(nameof(AStarNoRouteAndSameRegion), AStarNoRouteAndSameRegion);
            Run(nameof(FileRoundTripAndSetCheck), FileRoundTripAndSetCheck);
            Run(nameof(RouteFollower), RouteFollower);
            Run(nameof(EndToEndAroundAWall), EndToEndAroundAWall);
            Run(nameof(LandmassesAndSameLand), LandmassesAndSameLand);
            Run(nameof(PathGoesRoundAPortal), PathGoesRoundAPortal);
            Run(nameof(TripWalkFromTileFiles), TripWalkFromTileFiles);
            Run(nameof(RealTripWalk), RealTripWalk);
            Run(nameof(MeshViewFloorAndWalls), MeshViewFloorAndWalls);
            Run(nameof(MeshViewEmitBudgetAndFrame), MeshViewEmitBudgetAndFrame);
            Run(nameof(RealMeshView), RealMeshView);
            Run(nameof(DungeonFileAndLayer), DungeonFileAndLayer);
            Run(nameof(RealDungeons), RealDungeons);
        }
        finally { try { Directory.Delete(_root, true); } catch { } }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 && _asserts > 0 ? $"PASS ({_asserts} assertions)" : $"FAIL ({_fails} of {_asserts} assertions failed)");
        return _fails == 0 && _asserts > 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        int before = _fails;
        try { test(); }
        catch (Exception ex) { _fails++; _asserts++; Console.WriteLine($"  [FAIL] {name} threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        Console.WriteLine($"{(_fails == before ? "ok  " : "FAIL")} {name}");
    }

    // ── Baking made-up floors with the baker's Recast settings ───────────────────

    /// <summary>Flat rectangles (x0, z0, x1, z1) at height 0, plus a corner marker so the grid starts at (0,0).</summary>
    private static string Bake(string name, params (float x0, float z0, float x1, float z1)[] floors)
    {
        var verts = new List<float>(); var faces = new List<int>();
        void Quad(float x0, float z0, float x1, float z1, float y)
        {
            int b = verts.Count / 3;
            verts.AddRange(new[] { x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1 });
            // Both windings: Recast keeps the upward-facing one as walkable.
            faces.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3, b, b + 2, b + 1, b, b + 3, b + 2 });
        }
        foreach (var f in floors) Quad(f.x0, f.z0, f.x1, f.z1, 0);
        // Pin the bounds to the landblock grid (unused vertices, as the baker does).
        float maxX = floors.Max(f => f.x1), maxZ = floors.Max(f => f.z1);
        verts.AddRange(new[] { 0f, -1f, 0f, (float)Math.Ceiling(maxX / 192) * 192f, 1f, (float)Math.Ceiling(maxZ / 192) * 192f });

        var geom = new RcSampleInputGeomProvider(verts.ToArray(), faces.ToArray());
        geom.CalculateNormals();
        var settings = new RcNavMeshBuildSettings
        {
            cellSize = 0.5f, cellHeight = 0.2f, agentHeight = 2.0f, agentRadius = 2.0f, agentMaxClimb = 1.0f, agentMaxSlope = 48f,
            minRegionSize = 8, mergedRegionSize = 20, partitioning = (int)RcPartition.WATERSHED,
            filterLowHangingObstacles = true, filterLedgeSpans = true, filterWalkableLowHeightSpans = true,
            edgeMaxLen = 12f, edgeMaxError = 1.3f, vertsPerPoly = 6, detailSampleDist = 6f, detailSampleMaxError = 1f,
            tiled = true, tileSize = 384, keepInterResults = false, buildAll = true,
        };
        NavMeshBuildResult res = new TileNavMeshBuilder().Build(geom, settings);
        if (!res.Success || res.NavMesh == null) throw new InvalidOperationException("bake failed");
        string dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < res.NavMesh.GetMaxTiles(); i++)
        {
            DtMeshTile? t = res.NavMesh.GetTile(i);
            if (t?.data?.header == null || t.data.header.polyCount == 0) continue;
            uint lb = (uint)((t.data.header.x << 8) | t.data.header.y);
            using var fs = File.Create(Path.Combine(dir, $"nav_{lb:X4}.tile"));
            using var bw = new BinaryWriter(fs);
            new DtMeshDataWriter().Write(bw, t.data, RcByteOrder.LITTLE_ENDIAN, false);
        }
        return dir;
    }

    private static DtMeshData ReadTile(string dir, uint lb)
    {
        using var fr = File.OpenRead(Path.Combine(dir, $"nav_{lb:X4}.tile"));
        using var br = new BinaryReader(fr);
        return new DtMeshDataReader().Read(br, 6);
    }

    /// <summary>Local region of the polygon under (x, z) in a single tile.</summary>
    private static int RegionAtPoint(DtMeshData md, int[] regions, float x, float z)
    {
        var nav = new DtNavMesh();
        var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 2, maxPolys = 1 << 16 };
        nav.Init(ref prm, 6);
        nav.AddTile(md, 0, 0, out _);
        new DtNavMeshQuery(nav).FindNearestPoly(new RcVec3f(x, 0, z), new RcVec3f(4, 10, 4), new DtQueryDefaultFilter(), out long r, out _, out _);
        if (r == 0) return -1;
        nav.GetTileAndPolyByRefUnsafe(r, out _, out DtPoly p);
        return regions[p.index];
    }

    private static GraphBuild.Stats BuildGraph(string dir, out NavGraph g)
    {
        string path = Path.Combine(dir, NavGraph.FileName);
        var st = GraphBuild.Run(dir, path, _ => { }, TimeSpan.Zero);
        g = NavGraph.Load(path);
        return st;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    private static void RegionsSplitIslands()
    {
        // One tile, two floors 40 m apart: two regions, and the numbering is stable.
        string dir = Bake("islands", (10, 10, 70, 180), (110, 10, 180, 180));
        DtMeshData md = ReadTile(dir, 0x0000);
        int[] r = NavRegions.Build(md, out int count);
        Check(count == 2, $"two islands are two regions (got {count})");
        int a = RegionAtPoint(md, r, 40, 90), b = RegionAtPoint(md, r, 145, 90);
        Check(a >= 0 && b >= 0 && a != b, "each floor is its own region");
        Check(r[0] == 0, "region ids follow polygon order (polygon 0 is in region 0)");
        int[] again = NavRegions.Build(ReadTile(dir, 0x0000), out _);
        Check(again.SequenceEqual(r), "the same tile always gives the same numbering");
        var fp = NavRegions.Fingerprint(md);
        Check(fp.Polys == md.header.polyCount && fp.Verts == md.header.vertCount, "fingerprint = the header's counts");
    }

    private static void PortalsMatchAcrossTheSeam()
    {
        // Two 2-tile strips (x 0..384), z 10..60 and z 120..170: each crosses the seam at x = 192.
        string dir = Bake("strips", (0, 10, 384, 60), (0, 120, 384, 170));
        var st = BuildGraph(dir, out NavGraph g);
        Check(st.Tiles == 2, $"two tiles (got {st.Tiles})");
        Check(g.PortalCount == 2, $"one portal per strip (got {g.PortalCount})");
        DtMeshData m0 = ReadTile(dir, 0x0000), m1 = ReadTile(dir, 0x0100);
        int[] r0 = NavRegions.Build(m0, out _), r1 = NavRegions.Build(m1, out _);
        int s0 = g.Region(0x0000, RegionAtPoint(m0, r0, 100, 35)), s1 = g.Region(0x0100, RegionAtPoint(m1, r1, 300, 35));
        int n0 = g.Region(0x0000, RegionAtPoint(m0, r0, 100, 145)), n1 = g.Region(0x0100, RegionAtPoint(m1, r1, 300, 145));
        bool southLinked = false, northLinked = false, crossed = false;
        for (int p = 0; p < g.PortalCount; p++)
        {
            var (a, b) = g.PortalRegions(p);
            var pos = g.Portal(p);
            if ((a == s0 && b == s1) || (a == s1 && b == s0)) { southLinked = true; Check(pos.Ns > 10 && pos.Ns < 60 && Math.Abs(pos.Ew - 192) < 0.5f, $"south portal on the seam inside the strip ({pos.Ew:F1}, {pos.Ns:F1})"); }
            if ((a == n0 && b == n1) || (a == n1 && b == n0)) { northLinked = true; Check(pos.Ns > 120 && pos.Ns < 170, "north portal inside its strip"); }
            if ((a == s0 && b == n1) || (a == n0 && b == s1)) crossed = true;
        }
        Check(southLinked && northLinked, "each strip links to the same strip across the seam");
        Check(!crossed, "no portal joins one strip to the other");
    }

    private static void GapSplitsAStretchAndCostsFollowTheFloor()
    {
        // Tile 0: a full floor. Tile 1: a C open to the west (touches the seam at z 0..40 and
        // 150..192, joined by a bar along x 340..384). One region each side; two stretches.
        string dir = Bake("cshape", (0, 0, 192, 192), (192, 0, 384, 40), (192, 150, 384, 192), (340, 0, 384, 192));
        BuildGraph(dir, out NavGraph g);
        Check(g.PortalCount == 2, $"a 110 m gap on the seam makes two portals between the same two regions (got {g.PortalCount})");
        if (g.PortalCount != 2) return;
        var (a0, b0) = g.PortalRegions(0); var (a1, b1) = g.PortalRegions(1);
        Check(a0 == a1 && b0 == b1, "both portals join the same pair of regions");
        // Inside the C (tile 1) the walk between the two portals goes round by the bar.
        int cRegion = g.RegionLandblock(a0) == 0x0100 ? a0 : b0;
        int[] ps = g.RegionPortals(cRegion);
        var p0 = g.Portal(ps[0]); var p1 = g.Portal(ps[1]);
        float straight = MathF.Sqrt((p0.Ew - p1.Ew) * (p0.Ew - p1.Ew) + (p0.Ns - p1.Ns) * (p0.Ns - p1.Ns));
        // Route from the south arm to the north arm, both inside the C: the only way is round.
        var route = g.FindRoute(cRegion, 300, 20, cRegion, 300, 170, out _, out _);
        Check(route != null && route.Count == 0, "same region: nothing to cross");
        int open = g.RegionLandblock(a0) == 0x0000 ? a0 : b0;
        // From the open floor in tile 0 to the north arm: enter by the north portal, not round the C.
        route = g.FindRoute(open, 150, 170, cRegion, 300, 170, out float cost, out _);
        Check(route != null && route.Count == 1 && g.Portal(route[0]).Ns > 150, "enters the C by the near (north) arm");
        // From the south arm of the C to the open floor's north: cross at the south portal, walk the open floor.
        // Straight end legs would take the north arm (the wall of the C is invisible to them):
        route = g.FindRoute(cRegion, 300, 20, open, 150, 170, out cost, out _);
        Check(route != null && route.Count == 1, "a route either way");
        // Measured on the navmesh, the start leg round the C is long: it leaves by the south arm
        // (~108 to the south portal + ~156 across the open floor; the north arm would be ~400).
        NavCoarse.Probe? sp = NavCoarse.ProbeFile(g, dir, 300, 0, 20, 10), gp = NavCoarse.ProbeFile(g, dir, 150, 0, 170, 10);
        NavCoarse? walked = sp != null && gp != null ? NavCoarse.Plan(g, sp, gp, out _) : null;
        Check(walked != null && walked.Count == 1 && walked.Cost < 300, $"with walked end legs it leaves the C by the south arm (cost {walked?.Cost:F0})");
        Check(straight > 100, $"the two portals are far apart along the seam ({straight:F0})");
    }

    private static NavGraph HandGraph()
    {
        // Regions 0..4 in tiles 0x0000..0x0400 (one region each). Portals: 0-1, 1-2, 2-4 (the long
        // way, cheap), 1-3, 3-4 (the short way, but region 3 is costly to cross).
        var b = new NavGraph.Builder();
        for (uint i = 0; i < 5; i++) b.AddTile(i << 8, 10, 10, 10, 1);
        int p01 = b.AddPortal(192, 0, 96, 0, 1);
        int p12 = b.AddPortal(384, 0, 96, 1, 2);
        int p24 = b.AddPortal(576, 0, 300, 2, 4);
        int p13 = b.AddPortal(300, 0, 192, 1, 3);
        int p34 = b.AddPortal(480, 0, 250, 3, 4);
        b.SetRegionCosts(1, new[] { p01, p12, p13 }, new float[] { 0, 192, 150, 0, 0, 120, 0, 0, 0 });
        b.SetRegionCosts(2, new[] { p12, p24 }, new float[] { 0, 250, 0, 0 });
        b.SetRegionCosts(3, new[] { p13, p34 }, new float[] { 0, 2000, 0, 0 });   // a long climb round a ridge
        b.SetRegionCosts(0, new[] { p01 }, new float[] { 0 });
        b.SetRegionCosts(4, new[] { p24, p34 }, new float[] { 0, 60, 0, 0 });
        using var ms = new MemoryStream();
        b.Write(ms);
        ms.Position = 0;
        return NavGraph.Load(ms);
    }

    private static void AStarPicksTheCheaperWay()
    {
        NavGraph g = HandGraph();
        var route = g.FindRoute(0, 96, 96, 4, 600, 280, out float cost, out _);
        Check(route != null && route.SequenceEqual(new[] { 0, 1, 2 }), $"goes 0-1-2-4, not over the costly region 3 (got {string.Join(",", route ?? new())})");
        Check(cost > 192 + 250 && cost < 1500, $"cost adds the walks inside regions ({cost:F0})");
    }

    private static void AStarNoRouteAndSameRegion()
    {
        var b = new NavGraph.Builder();
        b.AddTile(0x0000, 1, 1, 1, 2);   // two islands, nothing between them
        b.AddTile(0x0100, 1, 1, 1, 1);
        int p = b.AddPortal(192, 0, 50, 0, 2);
        b.SetRegionCosts(0, new[] { p }, new float[] { 0 });
        b.SetRegionCosts(2, new[] { p }, new float[] { 0 });
        using var ms = new MemoryStream(); b.Write(ms); ms.Position = 0;
        NavGraph g = NavGraph.Load(ms);
        Check(g.FindRoute(1, 10, 10, 2, 300, 50, out _, out _) == null, "an island with no portal: no route");
        Check(g.FindRoute(0, 10, 10, 2, 300, 50, out _, out _) is { Count: 1 }, "the connected one: one crossing");
        Check(g.FindRoute(2, 300, 50, 2, 310, 60, out _, out _) is { Count: 0 }, "same region: empty route");
        Check(g.FindRoute(-1, 0, 0, 2, 0, 0, out _, out _) == null && g.FindRoute(0, 0, 0, 99, 0, 0, out _, out _) == null, "bad regions: no route");
    }

    private static void FileRoundTripAndSetCheck()
    {
        NavGraph g = HandGraph();
        Check(g.TileCount == 5 && g.RegionCount == 5 && g.PortalCount == 5, "counts survive the file");
        Check(g.Matches(0x0200, 10, 10, 10), "a matching tile matches");
        Check(!g.Matches(0x0200, 11, 10, 10) && !g.Matches(0x0900, 10, 10, 10), "another tile (or one the graph lacks) doesn't");
        Check(g.RegionLandblock(3) == 0x0300 && g.Region(0x0300, 0) == 3 && g.Region(0x0300, 1) == -1, "region <-> landblock");
        var ids = new List<NavGraph.TileInfo> { new(1, 2, 3, 4, 0, 1) };
        var ids2 = new List<NavGraph.TileInfo> { new(1, 2, 3, 5, 0, 1) };
        Check(!NavGraph.SetIdOf(ids).SequenceEqual(NavGraph.SetIdOf(ids2)), "set id changes with any tile's fingerprint");
        bool threw = false;
        try { NavGraph.Load(new MemoryStream(new byte[] { 1, 2, 3, 4 })); } catch (Exception) { threw = true; }
        Check(threw, "garbage is refused");
    }

    private static void RouteFollower()
    {
        NavGraph g = HandGraph();
        NavCoarse c = NavCoarse.Plan(g, 0, 96, 96, 4, 600, 280, out _)!;
        Check(c != null && c.Count == 3, "three crossings");
        var loaded = new HashSet<uint> { 0x0000, 0x0100 };
        var s = c!.Steer(lb => loaded.Contains(lb), out bool final);
        Check(!final && s.Ew == 192, "aims at the furthest loaded crossing (0-1)");
        loaded.Add(0x0200);
        s = c.Steer(lb => loaded.Contains(lb), out final);
        Check(!final && s.Ew == 384, "more tiles loaded: aims further (1-2)");
        double before = c.Remaining(96, 96);
        Check(c.Advance(1) && c.Crossed == 1, "standing in region 1: one crossing done");
        Check(c.Remaining(200, 96) < before, "what's left shrinks along the route");
        Check(!c.Advance(3), "region 3 isn't on this route: plan again");
        Check(c.Advance(-1), "between polygons for a moment: keep going");
        loaded.Add(0x0400);
        Check(c.Advance(4) && c.Crossed == 3, "in the goal's region");
        c.Steer(lb => loaded.Contains(lb), out final);
        Check(final, "then the goal itself is the aim");
        Check(c.Upcoming(3).Count() <= 3, "upcoming landblocks are limited");
    }

    private static void EndToEndAroundAWall()
    {
        // Three tiles in a row; the middle one is walkable only along its north edge, so a walk
        // from the south of tile 0 to the south of tile 2 must go north and back.
        string dir = Bake("wall", (0, 0, 192, 192), (192, 150, 384, 192), (384, 0, 576, 192));
        BuildGraph(dir, out NavGraph g);
        DtMeshData m0 = ReadTile(dir, 0x0000), m2 = ReadTile(dir, 0x0200);
        int r0 = g.Region(0x0000, RegionAtPoint(m0, NavRegions.Build(m0, out _), 100, 20));
        int r2 = g.Region(0x0200, RegionAtPoint(m2, NavRegions.Build(m2, out _), 480, 20));
        NavCoarse? c = NavCoarse.Plan(g, r0, 100, 20, r2, 480, 20, out _);
        Check(c != null && c.Count == 2, "two crossings, through the middle tile");
        var first = c!.Steer(lb => lb == 0x0000 || lb == 0x0100, out _);
        Check(first.Ns > 150, $"the first aim is the north passage, not straight east ({first.Ew:F0}, {first.Ns:F0})");
        Check(c.Cost > 2 * 130, $"the cost includes going north and back ({c.Cost:F0})");
        NavCoarse.Probe? s0 = NavCoarse.ProbeFile(g, dir, 100, 0, 20, 40), t2 = NavCoarse.ProbeFile(g, dir, 480, 0, 20, 40);
        Check(t2 != null && t2.Region == r2 && s0 != null && s0.Region == r0, "the ends' regions read straight from their tile files");
        NavCoarse? m = NavCoarse.Plan(g, s0!, t2!, out _);
        Check(m != null && m.Count == 2 && m.Cost >= c.Cost - 1, $"with walked end legs too ({m?.Cost:F0})");
    }

    private static void LandmassesAndSameLand()
    {
        // Two joined tiles (the mainland) and a tile two landblocks away (an island).
        string dir = Bake("land", (0, 0, 192, 192), (192, 0, 384, 192), (576, 0, 768, 192));
        BuildGraph(dir, out NavGraph g);
        Check(g.SameLand(0x0000, 0x0100) == null, "before BuildComponents nothing is known");
        g.BuildComponents();
        Check(g.SameLand(0x0000, 0x0100) == true, "joined tiles: the same landmass");
        Check(g.SameLand(0x0000, 0x0300) == false && g.SameLand(0x0300, 0x0100) == false, "the island is another landmass");
        Check(g.SameLand(0x0000, 0x0900) == null, "no tile: unknown (walks are allowed)");
        Check(g.LandblockComponents(0x0300).Length == 1 && g.ComponentSize(g.LandblockComponents(0x0300)[0]) >= 1, "the island's landmass");
        Check(NavGraph.LandblockAt(200, 10) == 0x0100 && NavGraph.LandblockAt(-1, 10) == uint.MaxValue, "landblock of a world point");
    }

    private static void PathGoesRoundAPortal()
    {
        // One open floor; a portal stands on the straight line between start and goal.
        string dir = Bake("portalfloor", (0, 0, 192, 192));
        var nav = new DtNavMesh();
        var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 4, maxPolys = 1 << 16 };
        nav.Init(prm, 6);
        var md0 = ReadTile(dir, 0x0000);
        nav.AddTile(md0, 0, 0, out _);
        var q = new DtNavMeshQuery(nav);
        var plain = new DtQueryDefaultFilter();
        var sP = new RcVec3f(30, 0, 96); var gP = new RcVec3f(160, 0, 96);
        q.FindNearestPoly(sP, new RcVec3f(4, 10, 4), plain, out long sRef, out RcVec3f sPt, out _);
        q.FindNearestPoly(gP, new RcVec3f(4, 10, 4), plain, out long gRef, out RcVec3f gPt, out _);
        Check(sRef != 0 && gRef != 0, "start and goal on the floor");

        List<RcVec3f> Path(IDtQueryFilter f)
        {
            Span<long> polys = new long[256];
            q.FindPath(sRef, gRef, sPt, gPt, f, polys, out int pc, 256);
            Span<DtStraightPath> sp = new DtStraightPath[64];
            q.FindStraightPath(sPt, gPt, polys[..pc], pc, sp, out int spc, 64, 0);
            var pts = new List<RcVec3f>();
            for (int i = 0; i < spc; i++) pts.Add(sp[i].pos);
            if (pc == 0 || polys[pc - 1] != gRef) pts.Clear();
            return pts;
        }
        double Closest(List<RcVec3f> pts, double x, double z)
        {
            double best = double.MaxValue;
            for (int i = 1; i < pts.Count; i++) best = Math.Min(best, PortalAvoid.SegmentDistance(x, z, pts[i - 1].X, pts[i - 1].Z, pts[i].X, pts[i].Z));
            return best;
        }

        var straight = Path(plain);
        Check(straight.Count >= 2 && Closest(straight, 95, 96) < 1, "the plain path walks right over the portal's spot");

        var avoid = new PortalAvoid();
        avoid.Set(new[] { new PortalAvoid.Spot(95, 96, PortalAvoid.LiveRadius, "Unwanted Portal") }, Array.Empty<PortalAvoid.Spot>());
        var filter = new AvoidFilter(avoid) { AllowA = sRef, AllowB = gRef };
        var round = Path(filter);
        Check(round.Count >= 2, "with the portal avoided there is still a path");
        // On open ground one big polygon can hold the start, the portal and more: the plugin then
        // bends the path round the portal (only onto ground that's on the navmesh).
        var pts = round.ConvertAll(v => ((double)v.X, (double)v.Z));
        var bent = avoid.Bend(pts, (x, z) =>
        {
            q.FindNearestPoly(new RcVec3f((float)x, 0, (float)z), new RcVec3f(1.5f, 10, 1.5f), plain, out long r, out _, out _);
            return r != 0;
        });
        double gap = double.MaxValue;
        for (int i = 1; i < bent.Count; i++) gap = Math.Min(gap, PortalAvoid.SegmentDistance(95, 96, bent[i - 1].X, bent[i - 1].Y, bent[i].X, bent[i].Y));
        Console.WriteLine($"     {md0.header.polyCount} polys; plain path {straight.Count} corners, filtered {round.Count}, bent {bent.Count}: closest {gap:F2} u");
        Check(gap >= PortalAvoid.LiveRadius - 0.01, $"the path keeps out of the portal's circle (closest {gap:F2} u)");
        Check(bent.Count > straight.Count, "it bends round it");
        Check(Math.Abs(bent[^1].X - gP.X) < 1 && Math.Abs(bent[^1].Y - gP.Z) < 1, "and still ends at the goal");
        // Beside the floor's edge only one side is ground: the bend goes that way.
        var edge = new PortalAvoid();
        edge.Set(new[] { new PortalAvoid.Spot(95, 3, PortalAvoid.LiveRadius, "by the edge") }, Array.Empty<PortalAvoid.Spot>());
        var e = edge.Bend(new List<(double, double)> { (30, 3), (160, 3) }, (x, z) => z > 2.5);
        Check(e.Count == 3 && e[1].Y > 3, "next to the edge it bends onto the ground side");

        // The goal right beside an unwanted portal: the goal polygon nearest the spot outside the circle.
        q.FindNearestPoly(new RcVec3f(96, 0, 96), new RcVec3f(8, 10, 8), new AvoidFilter(avoid), out long nearRef, out RcVec3f nearPt, out _);
        double d = Math.Sqrt((nearPt.X - 95) * (nearPt.X - 95) + (nearPt.Z - 96) * (nearPt.Z - 96));
        Check(nearRef != 0 && d >= PortalAvoid.LiveRadius - 0.01, $"a goal beside the portal snaps to ground outside its circle ({d:F2} u away)");
    }

    // ── A planned trip's walk, from the tile files (TilePathFinder, on a worker in the plugin) ──

    private static void TripWalkFromTileFiles()
    {
        // The wall: from the south of tile 0 to the south of tile 2, the only way is round the north.
        string dir = Bake("tripwall", (0, 0, 192, 192), (192, 150, 384, 192), (384, 0, 576, 192));
        BuildGraph(dir, out NavGraph g);
        var from = new RynthCore.Plugin.RynthNav.WorldPt(100, 20, 0);
        var to = new RynthCore.Plugin.RynthNav.WorldPt(480, 20, double.NaN);
        var finder = new TilePathFinder(dir, g, null);
        var path = finder.Walk(from, to, false, out string note);
        Check(path != null && path.Count >= 4, $"a path round the wall ({path?.Count} corners; {note})");
        Check(note == "", "no gaps: " + note);
        Check(path != null && path.Exists(p => p.Y > 150), "it goes north through the passage");
        Check(path != null && Math.Abs(path[^1].X - 480) < 3 && Math.Abs(path[^1].Y - 20) < 3, "and ends at the goal");
        Check(path != null && path.TrueForAll(p => !double.IsNaN(p.H) && Math.Abs(p.H) < 2), "heights from the navmesh");
        Check(finder.TilesRead >= 3, $"read the tiles it needed ({finder.TilesRead})");
        var thin = RynthCore.Plugin.RynthNav.TripPathBuilder.Thin(path!);
        Check(thin.Exists(p => p.Y > 150), "thinned, it still goes round");

        // Without the route graph, a short leg is planned on the tiles alone.
        var direct = new TilePathFinder(dir, null, null).Walk(from, to, false, out string note2);
        Check(direct != null && direct.Exists(p => p.Y > 150), "without the graph: the same way round (" + note2 + ")");
        // No navmesh at the far end: none, and why.
        var nowhere = new TilePathFinder(dir, g, null).Walk(from, new RynthCore.Plugin.RynthNav.WorldPt(2000, 2000, 0), false, out string why);
        Check(nowhere == null && why.Length > 0, "no tile at the end: no path (" + why + ")");

        // An Atlas portal on the straight line over open ground: kept out of, unless it's the leg's own.
        string open = Bake("tripportal", (0, 0, 192, 192));
        var spot = new[] { new PortalAvoid.Spot(95, 96, PortalAvoid.MapRadius, "Unwanted Portal") };
        var round = new TilePathFinder(open, null, spot).Walk(new(30, 96, 0), new(160, 96, 0), false, out _);
        double gap = double.MaxValue;
        for (int i = 1; round != null && i < round.Count; i++) gap = Math.Min(gap, PortalAvoid.SegmentDistance(95, 96, round[i - 1].X, round[i - 1].Y, round[i].X, round[i].Y));
        Check(round != null && gap >= PortalAvoid.MapRadius - 0.01, $"the walk keeps out of the portal's circle ({gap:F2} u)");
        var own = new TilePathFinder(open, null, spot).Walk(new(30, 96, 0), new(95, 96, 0), true, out _);
        Check(own != null && Math.Abs(own[^1].X - 95) < 3, "walking into that portal: it's the goal, not avoided");
    }

    // ── The navmesh view (NavMeshView) ───────────────────────────────────────────

    private sealed class TriSink : RynthCore.Plugin.Shared.INav3DSink
    {
        public readonly List<(float X1, float Y1, float Z1, float X2, float Y2, float Z2, float X3, float Y3, float Z3, uint Color)> Tris = new();
        public int Lines;
        public void Line(float x1, float y1, float z1, float x2, float y2, float z2, float t, uint c) => Lines++;
        public void Triangle(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint c)
            => Tris.Add((x1, y1, z1, x2, y2, z2, x3, y3, z3, c));
    }

    private static void MeshViewFloorAndWalls()
    {
        // Two floors with a 40 m gap between them (x 70..110): green on the floors, nothing in the gap,
        // red walls along the lips of the gap (and the floors' outer edges).
        string dir = Bake("meshview", (10, 10, 70, 180), (110, 10, 180, 180));
        var t = NavMeshView.BuildTile(0, ReadTile(dir, 0));
        Check(t.FloorCount > 0 && t.WallCount > 0, $"floor and walls found ({t.FloorCount} triangles, {t.WallCount} wall edges)");

        bool inGap = false; float minY = float.MaxValue, maxY = float.MinValue;
        for (int k = 0; k < t.FloorCount; k++)
            for (int v = 0; v < 3; v++)
            {
                float x = t.Floor[k * 9 + v * 3], y = t.Floor[k * 9 + v * 3 + 1];
                if (x > 71 && x < 109) inGap = true;
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        Check(!inGap, "no floor triangle reaches into the gap");
        Check(minY > -1 && maxY < 1, $"the floor lies at the floor's height ({minY:F2}..{maxY:F2})");

        // Wall edges along the gap: the west lip (x about 68, the floor shrunk by the 2 m body radius) and the east lip (about 112).
        int west = 0, east = 0;
        for (int k = 0; k < t.WallCount; k++)
        {
            float mx = (t.Walls[k * 6] + t.Walls[k * 6 + 3]) / 2, mz = (t.Walls[k * 6 + 2] + t.Walls[k * 6 + 5]) / 2;
            if (mx > 60 && mx < 71 && mz > 12 && mz < 178) west++;
            if (mx > 109 && mx < 120 && mz > 12 && mz < 178) east++;
        }
        Check(west > 0 && east > 0, $"walls stand along both lips of the gap (west {west}, east {east})");
        // Every wall edge is on the floor's edge, none out in the middle of a floor.
        bool stray = false;
        for (int k = 0; k < t.WallCount; k++)
        {
            float mx = (t.Walls[k * 6] + t.Walls[k * 6 + 3]) / 2, mz = (t.Walls[k * 6 + 2] + t.Walls[k * 6 + 5]) / 2;
            bool nearEdge = Math.Abs(mx - 68) < 3 || Math.Abs(mx - 112) < 3 || Math.Abs(mx - 12) < 3 || Math.Abs(mx - 178) < 3
                || Math.Abs(mz - 12) < 3 || Math.Abs(mz - 178) < 3;
            if (!nearEdge) stray = true;
        }
        Check(!stray, "no wall edge inside a floor");
    }

    private static void MeshViewEmitBudgetAndFrame()
    {
        string dir = Bake("meshview2", (10, 10, 70, 180), (110, 10, 180, 180));
        DtMeshData md = ReadTile(dir, 0);
        var view = new NavMeshView();
        view.Register(0, md);
        var big = new NavMeshView.Options(500f, 100000, true, true);

        var all = new TriSink();
        int n = view.Emit(all, 40, 100, 0, big);
        var tile = NavMeshView.BuildTile(0, md);
        Check(n == tile.FloorCount + tile.WallCount * 2 && all.Tris.Count == n, $"everything in range is drawn ({n} = {tile.FloorCount} floor + 2 x {tile.WallCount} walls)");
        Check(all.Tris.Any(x => x.Color == NavMeshView.DefaultFloorColor) && all.Tris.Any(x => x.Color == NavMeshView.DefaultWallColor), "green and red both drawn");
        float meshY = tile.Floor[1];   // the mesh surface sits a voxel above the input floor
        Check(all.Tris.Where(x => x.Color == NavMeshView.DefaultFloorColor).All(x => Math.Abs(x.Y1 - (meshY + 0.15f)) < 0.01f), "floor is lifted 15 cm off the mesh");
        float wallTop = all.Tris.Where(x => x.Color == NavMeshView.DefaultWallColor).Max(x => Math.Max(x.Y1, Math.Max(x.Y2, x.Y3)));
        Check(Math.Abs(wallTop - (meshY + 0.15f + 1.5f)) < 0.01f, $"walls stand 1.5 m high ({wallTop - meshY - 0.15f:F2})");

        // Radius: a small circle at the west floor leaves the east floor out.
        var near = new TriSink();
        view.Emit(near, 30, 100, 0, new NavMeshView.Options(20f, 100000, true, true));
        Check(near.Tris.Count > 0 && near.Tris.Count < n && near.Tris.All(x => x.X1 < 100), "a small radius draws only what is near");

        // The cap holds, and walls survive it (nearest first, walls favoured).
        var capped = new TriSink();
        int c = view.Emit(capped, 66, 100, 0, new NavMeshView.Options(500f, 40, true, true));
        Check(c <= 40 && c > 30, $"a 40-triangle budget draws about 40 ({c})");
        Check(capped.Tris.Any(x => x.Color == NavMeshView.DefaultWallColor), "walls are kept under the cap");
        var wallsOnly = new TriSink();
        view.Emit(wallsOnly, 40, 100, 0, new NavMeshView.Options(500f, 100000, false, true));
        Check(wallsOnly.Tris.Count > 0 && wallsOnly.Tris.All(x => x.Color == NavMeshView.DefaultWallColor), "walls alone");
        var floorOnly = new TriSink();
        view.Emit(floorOnly, 40, 100, 0, new NavMeshView.Options(500f, 100000, true, false));
        Check(floorOnly.Tris.Count == tile.FloorCount && floorOnly.Tris.All(x => x.Color == NavMeshView.DefaultFloorColor), "floor alone");

        // Frame: the same tile registered as landblock 0x0100 (east of the first), character at world x = 192 + 40.
        string dir2 = Bake("meshview3", (192 + 10, 10, 192 + 70, 180), (192 + 110, 10, 192 + 180, 180));
        var shifted = new NavMeshView();
        shifted.Register(0x0100, ReadTile(dir2, 0x0100));
        var s = new TriSink();
        int sn = shifted.Emit(s, 192 + 40, 100, 0, big);
        Check(sn == n, "the same floors one landblock east draw the same");
        Check(Math.Abs(s.Tris.Min(x => x.X1) - all.Tris.Min(x => x.X1)) < 0.01f, "positions are in the character's landblock (X = east metres)");
        // ...and from the next landblock over (character at world x = 192 + 4): the tile is one landblock west of the character's.
        var west = new TriSink();
        shifted.Emit(west, 192 * 2 + 4, 100, 0, new NavMeshView.Options(500f, 100000, true, true));
        Check(west.Tris.Count == 0 || west.Tris.Min(x => x.X1) < 0, "a tile in the landblock to the west is drawn at negative X");

        // The pick follows the character: moving 50 m re-picks.
        var moved = new TriSink();
        int a1 = view.Emit(moved, 30, 100, 0, new NavMeshView.Options(20f, 100000, true, true));
        moved.Tris.Clear();
        int a2 = view.Emit(moved, 150, 100, 0, new NavMeshView.Options(20f, 100000, true, true));
        Check(a1 > 0 && a2 > 0 && moved.Tris.All(x => x.X1 > 100), "after moving to the east floor, it is the east floor that is drawn");
        view.Unregister(0);
        moved.Tris.Clear();
        Check(view.Emit(moved, 150, 100, 0, big) == 0, "an unloaded tile is no longer drawn");
    }

    /// <summary>
    /// A dungeon file: tiles on a shifted frame (mesh = local + shift) that read back, draw in the character's
    /// landblock-local frame (negative x/y included) and stay apart from the outdoor tiles.
    /// </summary>
    private static void DungeonFileAndLayer()
    {
        // A floor at mesh x 10..70, y 10..180 with shift (192, 192): landblock-local x -182..-122, y -182..-12.
        string dir = Bake("dun1", (10, 10, 70, 180));
        DtMeshData md = ReadTile(dir, 0);
        string file = Path.Combine(_root, DungeonTileFile.FileName(0x0505));
        DungeonTileFile.Write(file, 192, 192, new[] { md });
        var back = DungeonTileFile.Read(file);
        Check(back != null && back.ShiftX == 192 && back.ShiftY == 192 && back.Tiles.Count == 1, "the dungeon file reads back");
        Check(back != null && back.Tiles[0].header.polyCount == md.header.polyCount && back.Tiles[0].header.x == 0, "its tile is intact");
        Check(DungeonTileFile.Read(Path.Combine(_root, "nonexistent.tile")) == null, "a missing file is null, not a throw");
        File.WriteAllBytes(Path.Combine(_root, "junk.tile"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });
        Check(DungeonTileFile.Read(Path.Combine(_root, "junk.tile")) == null, "junk is null");
        byte[] cut = File.ReadAllBytes(file); File.WriteAllBytes(Path.Combine(_root, "cut.tile"), cut.AsSpan(0, cut.Length / 2).ToArray());
        Check(DungeonTileFile.Read(Path.Combine(_root, "cut.tile")) == null, "a cut-short file is null");

        // The mesh navigates on its own frame: a point on the floor is found at mesh (40, 100).
        var nav = back!.BuildNavMesh();
        var q = new DtNavMeshQuery(nav);
        q.FindNearestPoly(new RcVec3f(40, 0.2f, 100), new RcVec3f(2, 4, 2), new DtQueryDefaultFilter(), out long poly, out _, out _);
        Check(poly != 0, "the dungeon's own navmesh finds the floor at its mesh position");

        // Drawing: landblock 0x0505 is world (960, 960); the character stands at local (-150, -100) = world (810, 860).
        var view = new NavMeshView();
        view.RegisterDungeon(0x0505, back);
        Check(view.DungeonLandblock == 0x0505, "the dungeon is registered");
        var s = new TriSink();
        var dun = new NavMeshView.Options(100f, 100000, true, true, layer: 1, frameX: 960, frameY: 960);
        int n = view.Emit(s, 810, 860, 0, dun);
        Check(n > 0, "the dungeon draws");
        float lo = s.Tris.Min(t => Math.Min(t.X1, Math.Min(t.X2, t.X3))), hi = s.Tris.Max(t => Math.Max(t.X1, Math.Max(t.X2, t.X3)));
        Check(lo > -184 && hi < -120, $"drawn in the landblock-local frame, negative x ({lo:F1}..{hi:F1})");
        float zlo = s.Tris.Min(t => Math.Min(t.Z1, Math.Min(t.Z2, t.Z3))), zhi = s.Tris.Max(t => Math.Max(t.Z1, Math.Max(t.Z2, t.Z3)));
        Check(zlo > -184 && zhi < -10, $"and negative z/north ({zlo:F1}..{zhi:F1})");

        // Layers: asking for the outdoor layer draws none of the dungeon, and an outdoor tile never shows in the dungeon.
        var outdoor = new TriSink();
        Check(view.Emit(outdoor, 810, 860, 0, new NavMeshView.Options(100f, 100000, true, true)) == 0, "the outdoor layer is empty while only a dungeon is registered");
        view.Register(0x0505, md);
        var again = new TriSink();
        int n2 = view.Emit(again, 810, 860, 0, dun);
        Check(n2 == n, "registering an outdoor tile doesn't change the dungeon's draw");
        view.UnregisterDungeon();
        Check(view.DungeonLandblock == 0 && view.Emit(new TriSink(), 810, 860, 0, dun) == 0, "unregistering the dungeon empties its layer");

        // Floors above and below the character are left out (maxUpDown).
        var tall = new NavMeshView();
        tall.RegisterDungeon(0x0505, back);
        var farUp = new TriSink();
        Check(tall.Emit(farUp, 810, 860, 50, new NavMeshView.Options(100f, 100000, true, true, maxUpDown: 8f, layer: 1, frameX: 960, frameY: 960)) == 0, "a floor 50 m away vertically isn't drawn");
    }

    /// <summary>The dungeon tiles a bake wrote (RYNTHNAV_DUNGEONS, or the live NavData), when there are any.</summary>
    private static void RealDungeons()
    {
        string dir = Environment.GetEnvironmentVariable("RYNTHNAV_DUNGEONS") ?? @"C:\Projects\meshview\_dun";
        if (!Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "dun_*.tile").Any()) { Console.WriteLine("     (no baked dungeons here: skipped)"); return; }
        int files = 0, tiles = 0, bad = 0; long polys = 0, wallEdges = 0, floorTris = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (string f in Directory.EnumerateFiles(dir, "dun_*.tile"))
        {
            files++;
            var d = DungeonTileFile.Read(f);
            if (d == null) { bad++; continue; }
            tiles += d.Tiles.Count;
            uint lb = Convert.ToUInt32(Path.GetFileNameWithoutExtension(f).Substring(4), 16);
            var view = new NavMeshView(); view.RegisterDungeon(lb, d);
            foreach (var md in d.Tiles)
            {
                polys += md.header.polyCount;
                var t = NavMeshView.BuildTile(md, md.header.x * 192, md.header.y * 192, 0, 0);
                floorTris += t.FloorCount; wallEdges += t.WallCount;
                if (!t.Floor.All(float.IsFinite)) bad++;
            }
        }
        Console.WriteLine($"     {files} dungeon files: {tiles} tiles, {polys} polygons, {floorTris} floor triangles, {wallEdges} wall edges; {sw.ElapsedMilliseconds} ms to read all");
        Check(bad == 0, $"every dungeon file reads and is finite ({bad} bad)");

        // Connectivity: how much of a dungeon's walkable ground is one connected piece (polygon links, across tiles).
        // A sound mesh has most of its area in one region; a low share means stairs, ramps or doorways were lost.
        var shares = new List<(uint lb, double share, int regions, int polys)>();
        foreach (string f in Directory.EnumerateFiles(dir, "dun_*.tile").Take(400))
        {
            var d = DungeonTileFile.Read(f);
            if (d == null) continue;
            uint lb = Convert.ToUInt32(Path.GetFileNameWithoutExtension(f).Substring(4), 16);
            var comp = Components(d.BuildNavMesh());
            if (comp.Count == 0) continue;
            int total = comp.Sum(), big = comp.Max();
            shares.Add((lb, (double)big / total, comp.Count, total));
        }
        if (shares.Count == 0) return;
        var sorted = shares.Select(s => s.share).OrderBy(x => x).ToList();
        double Pct(double p) => sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
        Console.WriteLine($"     connectivity of {shares.Count} dungeons (share of polygons in the largest piece): " +
            $"worst {sorted[0]:P0}, 10th pct {Pct(0.10):P0}, median {Pct(0.5):P0}; {shares.Count(s => s.share >= 0.9)} are 90%+ one piece, {shares.Count(s => s.share < 0.5)} under 50%");
        foreach (var s in shares.OrderBy(s => s.share).Take(5)) Console.WriteLine($"       0x{s.lb:X4}: {s.share:P0} of {s.polys} polygons in the biggest of {s.regions} pieces");
        // No assertion on the share: many dungeons are areas joined by drops and teleports, so a low share is the dungeon, not the mesh.
        // The baker's --dungeons-check compares against the game's own doorway graph, which is the fidelity measure.
    }

    /// <summary>Sizes (polygon counts) of the connected pieces of a navmesh, joined by Detour's polygon links.</summary>
    private static List<int> Components(DtNavMesh nav)
    {
        var seen = new HashSet<long>();
        var sizes = new List<int>();
        for (int ti = 0; ti < nav.GetMaxTiles(); ti++)
        {
            DtMeshTile? tile = nav.GetTile(ti);
            if (tile?.data?.header == null) continue;
            long baseRef = nav.GetPolyRefBase(tile);
            for (int pi = 0; pi < tile.data.header.polyCount; pi++)
            {
                long start = baseRef | (long)pi;
                if (!seen.Add(start)) continue;
                int size = 0;
                var stack = new Stack<long>(); stack.Push(start);
                while (stack.Count > 0)
                {
                    long r = stack.Pop(); size++;
                    if (nav.GetTileAndPolyByRef(r, out DtMeshTile t, out DtPoly p).Failed()) continue;
                    for (int k = p.firstLink; k != DtDetour.DT_NULL_LINK; k = t.links[k].next)
                    {
                        long n = t.links[k].refs;
                        if (n != 0 && seen.Add(n)) stack.Push(n);
                    }
                }
                sizes.Add(size);
            }
        }
        return sizes;
    }

    /// <summary>Real tiles (when installed): the triangle and wall counts of a town tile, and a whole 5 x 5 window.</summary>
    private static void RealMeshView()
    {
        string dir = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData";
        uint holtburg = 0xA9B4;   // Holtburg
        string file = Path.Combine(dir, $"nav_{holtburg:X4}.tile");
        if (!File.Exists(file)) { Console.WriteLine("     (no real NavData here: skipped)"); return; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var view = new NavMeshView();
        int loaded = 0, floor = 0, walls = 0;
        for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            {
                uint lb = (uint)((((int)(holtburg >> 8) + dx) << 8) | ((int)(holtburg & 0xFF) + dy));
                if (!File.Exists(Path.Combine(dir, $"nav_{lb:X4}.tile"))) continue;
                DtMeshData md = ReadTile(dir, lb);
                view.Register(lb, md); loaded++;
                var t = NavMeshView.BuildTile(lb, md);
                floor += t.FloorCount; walls += t.WallCount;
                Check(t.Floor.All(float.IsFinite) && t.Walls.All(float.IsFinite), $"tile 0x{lb:X4}: all finite");
            }
        long buildMs = sw.ElapsedMilliseconds;
        var s = new TriSink();
        double px = (holtburg >> 8) * 192.0 + 96, py = (holtburg & 0xFF) * 192.0 + 96;
        sw.Restart();
        int n = view.Emit(s, px, py, 100, new NavMeshView.Options(40f, 3000, true, true, maxUpDown: 1000f));
        long emitMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"     Holtburg window: {loaded} tiles, {floor} floor triangles, {walls} wall edges in all; {buildMs} ms to read+build; a 40 m draw = {n} triangles in {emitMs} ms");
        Check(loaded > 0 && floor > 0, "real tiles read");
        Check(n <= 3000, $"the budget holds on real tiles ({n})");
    }

    /// <summary>
    /// The real map (read only, when it's installed): Holtburg to Lytelthorpe, about 11k yd over
    /// land, as the path markers and "save as nav route" would work it out. Prints the size and time.
    /// </summary>
    private static void RealTripWalk()
    {
        string dir = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData";
        string graphFile = Path.Combine(dir, NavGraph.FileName);
        if (!File.Exists(graphFile)) { Console.WriteLine("     (no real NavData here: skipped)"); return; }
        NavGraph g = NavGraph.Load(graphFile);
        var from = RynthCore.Plugin.RynthNav.WorldPt.FromMap(42.08, 33.6, double.NaN);
        var to = RynthCore.Plugin.RynthNav.WorldPt.FromMap(-0.8, 51.2, double.NaN);
        // Landmasses, as the plugin has them (LoadGraph): Lytelthorpe's centre probes onto a roof,
        // a landmass of one region; the ground beside it is found instead.
        g.BuildComponents();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var finder = new TilePathFinder(dir, g, null);
        var path = finder.Walk(from, to, false, out string note);
        long ms = sw.ElapsedMilliseconds;
        Check(path != null, "Holtburg to Lytelthorpe on the real navmesh: " + note);
        if (path == null) return;
        var thin = RynthCore.Plugin.RynthNav.TripPathBuilder.Thin(path);
        int corners = RynthCore.Plugin.RynthNav.TripPathBuilder.Thin(path, maxSpacing: 0).Count;
        double len = 0;
        for (int i = 1; i < thin.Count; i++) len += thin[i - 1].FlatDistance(thin[i]);
        Console.WriteLine($"     Holtburg -> Lytelthorpe: {path.Count} corners, {corners} kept by thinning, {thin.Count} with a point every {RynthCore.Plugin.RynthNav.TripPathBuilder.MaxSpacing} yd, {len:F0} yd, {finder.TilesRead} tiles read, {ms} ms{(note.Length > 0 ? "; " + note : "")}");
        Check(len > from.FlatDistance(to) * 0.98 && len < from.FlatDistance(to) * 2.5, $"a sensible length ({len:F0} vs {from.FlatDistance(to):F0} straight)");
        Check(ms < 30000, $"worked out in reasonable time ({ms} ms)");
    }
}

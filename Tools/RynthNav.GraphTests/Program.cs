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
using RynthCore.Plugin.Shared;

internal static partial class Program
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
            Run(nameof(FencedInGoalMovesToReachableGround), FencedInGoalMovesToReachableGround);
            Run(nameof(RealFencedInGoal), RealFencedInGoal);
            Run(nameof(PathGoesRoundAPortal), PathGoesRoundAPortal);
            Run(nameof(TripWalkFromTileFiles), TripWalkFromTileFiles);
            Run(nameof(PathInterfaceVersions), PathInterfaceVersions);
            Run(nameof(PathTileBox), PathTileBox);
            Run(nameof(PathExOpenFloorAndInfo), PathExOpenFloorAndInfo);
            Run(nameof(PathExRoundAPortal), PathExRoundAPortal);
            Run(nameof(PathExPartial), PathExPartial);
            Run(nameof(PathExNoMeshAndStartSnap), PathExNoMeshAndStartSnap);
            Run(nameof(PathExWallsAndBuffer), PathExWallsAndBuffer);
            Run(nameof(RaycastOnTheMesh), RaycastOnTheMesh);
            Run(nameof(RealPathEx), RealPathEx);
            Run(nameof(RealDoorsteps), RealDoorsteps);
            Run(nameof(RealTripWalk), RealTripWalk);
            Run(nameof(MeshViewFloorAndWalls), MeshViewFloorAndWalls);
            Run(nameof(MeshViewEmitBudgetAndFrame), MeshViewEmitBudgetAndFrame);
            Run(nameof(RealMeshView), RealMeshView);
            Run(nameof(DungeonFileAndLayer), DungeonFileAndLayer);
            Run(nameof(RealDungeons), RealDungeons);
            RunRouteMaker();   // RouteMakerTests.cs
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
        => BakeLayers(name, floors.Select(f => (f.x0, f.z0, f.x1, f.z1, 0f)).ToArray());

    /// <summary>Flat rectangles (x0, z0, x1, z1) each at its own height y: a floor with a roof over part of it.</summary>
    private static string BakeLayers(string name, params (float x0, float z0, float x1, float z1, float y)[] floors)
    {
        var verts = new List<float>(); var faces = new List<int>();
        void Quad(float x0, float z0, float x1, float z1, float y)
        {
            int b = verts.Count / 3;
            verts.AddRange(new[] { x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1 });
            // Both windings: Recast keeps the upward-facing one as walkable.
            faces.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3, b, b + 2, b + 1, b, b + 3, b + 2 });
        }
        foreach (var f in floors) Quad(f.x0, f.z0, f.x1, f.z1, f.y);
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

    private static void FencedInGoalMovesToReachableGround()
    {
        // 11-01: a /loc on ground fenced in by a building's walls. Two tiles of open ground; in tile 1
        // a small floor (300..320, 80..100) with a 10 m moat round it: a pocket region of its own,
        // with no border links. Every route search to it found nothing (0 expanded), and the walk
        // headed straight for the spot instead. The goal end now moves to the nearest ground a
        // walk can end on.
        string dir = Bake("pocket", (0, 10, 384, 70), (0, 110, 384, 180), (0, 70, 290, 110), (330, 70, 384, 110), (300, 80, 320, 100));
        BuildGraph(dir, out NavGraph g);
        g.BuildComponents();
        var nav = new DtNavMesh();
        var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 4, maxPolys = 1 << 16 };
        nav.Init(prm, 6);
        var regions = new Dictionary<uint, int[]>();
        foreach (uint lb in new uint[] { 0x0000, 0x0100 })
        {
            DtMeshData md = ReadTile(dir, lb);
            nav.AddTile(md, 0, 0, out _);
            regions[lb] = NavRegions.Build(md, out _);
        }
        var q = new DtNavMeshQuery(nav);
        NavCoarse.Probe? start = NavCoarse.ProbeNear(g, nav, q, 40, 0, 40, 12, regions);
        Check(start != null && g.RegionPortals(start.Region).Length > 0, "the start is on open ground with border links");

        NavCoarse.Probe? pocket = NavCoarse.ProbeNear(g, nav, q, 310, 0, 90, 40, regions);
        Check(pocket != null && g.RegionPortals(pocket.Region).Length == 0, "the spot's own polygon is a pocket with no border links");
        var none = g.FindRoute(start!.Region, 40, 40, pocket!.Region, 310, 90, out _, out int expanded);
        Check(none == null && expanded == 0, $"to the pocket: no route, nothing expanded (the 11-01 log: '0 expanded') (expanded {expanded})");
        Check(!NavCoarse.Reachable(g, start.Region, pocket.Region) && NavCoarse.Reachable(g, start.Region, start.Region), "a pocket isn't reachable; the start's own region is");

        NavCoarse.Probe? goal = NavCoarse.ProbeReachable(g, nav, q, 310, 0, 90, 40, regions, start.Region, out bool moved);
        double off = goal == null ? double.NaN : Math.Sqrt((goal.Pt.X - 310) * (goal.Pt.X - 310) + (goal.Pt.Z - 90) * (goal.Pt.Z - 90));
        Check(goal != null && moved && goal.Region != pocket.Region && NavCoarse.Reachable(g, start.Region, goal.Region),
              $"the goal moves to linked ground (region {goal?.Region}, moved {moved})");
        Check(off < 25, $"...the nearest such ground, beside the moat ({off:F1} u from the spot)");
        NavCoarse? c = NavCoarse.Plan(g, start, goal!, out _);
        Check(c != null && c.Count >= 1 && Math.Abs(c.GoalEw - goal!.Pt.X) < 0.01 && !double.IsNaN(c.GoalUp), "a route to it, ending on that ground (with its height)");

        NavCoarse.Probe? fromFile = NavCoarse.ProbeFile(g, dir, 310, 0, 90, 40, start.Region, out bool movedFile);
        Check(fromFile != null && movedFile && fromFile.Region == goal!.Region, "the same from the tile file (the target's tile not loaded)");

        NavCoarse.Probe? open = NavCoarse.ProbeReachable(g, nav, q, 250, 0, 150, 40, regions, start.Region, out bool movedOpen);
        Check(open != null && !movedOpen && Math.Abs(open.Pt.X - 250) < 0.5 && Math.Abs(open.Pt.Z - 150) < 0.5, "a spot on open ground stays where it is");
    }

    private static void RealFencedInGoal()
    {
        // The 11-01 goto to 43.6N, 85.4W on the real navmesh: the spot probes onto a fenced-in
        // polygon (a region of its own, no border links); every search from 41.8N, 82.2W (and from
        // everywhere else on the way) found no route.
        string dir = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData";
        string graphFile = Path.Combine(dir, NavGraph.FileName);
        if (!File.Exists(graphFile)) { Console.WriteLine("     (no real NavData here: skipped)"); return; }
        NavGraph g = NavGraph.Load(graphFile);
        g.BuildComponents();
        static double W(double map) => (map * 10.0 + 1019.5) * 24.0;
        double sx = W(-82.2), sy = W(41.8), tx = W(-85.4), ty = W(43.6);
        NavCoarse.Probe? start = NavCoarse.ProbeFile(g, dir, sx, 22, sy, 12);
        if (start == null) { Console.WriteLine("     (these tiles aren't the ones this test was written for: skipped)"); return; }
        NavCoarse.Probe? old = NavCoarse.ProbeFile(g, dir, tx, 22, ty, 40);
        NavCoarse.Probe? goal = NavCoarse.ProbeFile(g, dir, tx, 22, ty, 40, start.Region, out bool moved);
        Check(old != null && goal != null, "both probes find ground");
        if (old == null || goal == null) return;
        bool oldRoute = NavCoarse.Plan(g, start, old, out int oldExpanded) != null;
        NavCoarse? c = NavCoarse.Plan(g, start, goal, out int expanded);
        double off = Math.Sqrt((goal.Pt.X - tx) * (goal.Pt.X - tx) + (goal.Pt.Z - ty) * (goal.Pt.Z - ty));
        Console.WriteLine($"     43.6N 85.4W: the spot's own region {old.Region} ({g.RegionPortals(old.Region).Length} border links, route {(oldRoute ? "yes" : "none")}, {oldExpanded} expanded); "
                        + $"moved {moved} to region {goal.Region}, {off:F1} u off; route {(c == null ? "none" : $"{c.Count} crossings, ~{c.Cost:F0} u")} ({expanded} expanded)");
        Check(c != null, "a route to the ground beside the fenced-in spot");
        Check(off < 12, $"that ground is beside the spot ({off:F1} u)");
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

    // ── "RynthNav.Path" v2: FindPathEx and Raycast (NavPathQuery, the plugin's interface) ──

    /// <summary>Every tile file of a bake in one navmesh, as the plugin has its loaded tiles.</summary>
    private static DtNavMeshQuery QueryOn(string dir)
    {
        var nav = new DtNavMesh();
        var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 64, maxPolys = 1 << 16 };
        nav.Init(prm, 6);
        foreach (string f in Directory.GetFiles(dir, "nav_*.tile"))
        {
            uint lb = Convert.ToUInt32(Path.GetFileNameWithoutExtension(f).Substring(4), 16);
            nav.AddTile(ReadTile(dir, lb), 0, 0, out _);
        }
        return new DtNavMeshQuery(nav);
    }

    private static PathExResult Ex(DtNavMeshQuery q, double sx, double sy, double gx, double gy, int flags,
                                   AvoidFilter? portals = null, double clearance = 1.0, int maxCorners = 64, double sz = 0, double gz = 0)
    {
        var r = new PathExResult();
        int code = NavPathQuery.FindPathEx(q, sx, sy, sz, gx, gy, gz, flags, maxCorners, portals, clearance, r);
        if (code != r.Code) throw new InvalidOperationException("the returned code and the result's differ");
        return r;
    }

    /// <summary>The path's closest pass to (x, y), flat.</summary>
    private static double Closest(PathExResult r, double x, double y)
    {
        double best = double.MaxValue;
        for (int i = 1; i < r.Corners.Count; i++)
            best = Math.Min(best, PortalAvoid.SegmentDistance(x, y, r.Corners[i - 1].X, r.Corners[i - 1].Y, r.Corners[i].X, r.Corners[i].Y));
        return best;
    }

    private static double Flat((double X, double Y, double Z) a, double x, double y) => Math.Sqrt((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y));

    private static double Legs(PathExResult r)
    {
        double len = 0;
        for (int i = 1; i < r.Corners.Count; i++)
        {
            var a = r.Corners[i - 1]; var b = r.Corners[i];
            len += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
        }
        return len;
    }

    /// <summary>
    /// The table is handed to v1 callers too (older RynthAi's stuck recovery asks for 1) and to v2 and
    /// v3 callers, never for 0 or a newer version; and the layout only ever grew at its end. v3
    /// (2026-10-25) added no field, only the PathStartOutdoors flag (RynthAi's hunt plans from a
    /// building's doorstep while the player is inside).
    /// </summary>
    private static unsafe void PathInterfaceVersions()
    {
        const string name = RynthNavPathApiV1.InterfaceName;
        Check(RynthNavPathApiV1.InterfaceVersion == 3, "the contract is version 3");
        Check(!NavPathQuery.ServesInterface(name, 0), "version 0: refused");
        Check(NavPathQuery.ServesInterface(name, 1), "version 1 (FindPath callers): served the same table");
        Check(NavPathQuery.ServesInterface(name, 2), "version 2: served");
        Check(NavPathQuery.ServesInterface(name, 3), "version 3: served");
        Check(!NavPathQuery.ServesInterface(name, 4) && !NavPathQuery.ServesInterface(name, uint.MaxValue), "a version newer than the one built: refused");
        Check(RynthNavPathApiV1.PathStartOutdoors == 16
              && (RynthNavPathApiV1.PathStartOutdoors & (RynthNavPathApiV1.PathAvoidPortals | RynthNavPathApiV1.PathWallClearance
                                                          | RynthNavPathApiV1.PathAllowPartial | RynthNavPathApiV1.PathQuiet)) == 0,
              "the v3 flag is a bit of its own");
        Check(!NavPathQuery.ServesInterface("RynthNav.path", 1) && !NavPathQuery.ServesInterface("", 2) && !NavPathQuery.ServesInterface(null, 2)
              && !NavPathQuery.ServesInterface("RynthAi.Script", 1), "another interface's name: refused");

        RynthNavPathApiV1 t = default;
        byte* b = (byte*)&t;
        int p = IntPtr.Size;
        Check((byte*)&t.Version - b == 0 && (byte*)&t.StructSize - b == 4 && (byte*)&t.FindPath - b == 8, "v1's fields where v1 callers read them");
        Check((byte*)&t.FindPathEx - b == 8 + p && (byte*)&t.Raycast - b == 8 + 2 * p && sizeof(RynthNavPathApiV1) == 8 + 3 * p,
              $"v2 appended after FindPath (size {sizeof(RynthNavPathApiV1)} with {p}-byte pointers)");
        Check(RynthNavPathApiV1.InfoLength == 6 && RynthNavPathApiV1.ResultPartial == 2 && RynthNavPathApiV1.RayClear == 1 && RynthNavPathApiV1.RayBlocked == 0,
              "the v2 constants");
    }

    private static void PathTileBox()
    {
        Check(NavPathQuery.ClampMaxTiles(0) == 25 && NavPathQuery.ClampMaxTiles(-4) == 25, "maxTiles 0: the default 25");
        Check(NavPathQuery.ClampMaxTiles(9) == 9 && NavPathQuery.ClampMaxTiles(49) == 49 && NavPathQuery.ClampMaxTiles(1000) == 49, "else as asked, at most 49");
        double hx = 0xA9 * 192 + 96, hy = 0xB4 * 192 + 96;   // the middle of Holtburg's landblock
        Check(NavPathQuery.TileBox(hx, hy, hx + 50, hy + 50, 25, false, out int x0, out int y0, out int x1, out int y1)
              && x0 == 0xA8 && x1 == 0xAA && y0 == 0xB3 && y1 == 0xB5, "both ends in one landblock: it and the ring round it");
        Check(!NavPathQuery.TileBox(hx, hy, hx + 192, hy, 9, false, out _, out _, out _, out _), "FindPath (v1): 4x3 with the margin doesn't fit 9, no smaller box");
        Check(NavPathQuery.TileBox(hx, hy, hx + 192, hy, 9, true, out x0, out y0, out x1, out y1) && x0 == 0xA9 && x1 == 0xAA && y0 == 0xB4 && y1 == 0xB4,
              "FindPathEx: then the two landblocks alone");
        Check(NavPathQuery.TileBox(hx, hy, hx + 192, hy, 12, true, out x0, out _, out x1, out _) && x0 == 0xA8 && x1 == 0xAB, "with room for it, the margin stays");
        Check(NavPathQuery.TileBox(hx, hy, hx + 192 * 8, hy, 49, true, out _, out _, out _, out _), "nine landblocks in a row fit 49 without the margin (9 tiles)");
        Check(!NavPathQuery.TileBox(hx, hy, hx + 192 * 8, hy + 192 * 8, 49, true, out _, out _, out _, out _), "nine apart both ways (81 tiles): too far even for 49");
        Check(NavPathQuery.TileBox(10, 10, 20, 20, 25, false, out x0, out y0, out x1, out y1) && x0 == 0 && y0 == 0 && x1 == 1 && y1 == 1, "the box stops at the map's edge");
        Check(!NavPathQuery.TileBox(1e12, 0, 0, 0, 49, true, out _, out _, out _, out _), "a position far off the map: no box (and no overflow)");
    }

    private static void PathExOpenFloorAndInfo()
    {
        var q = QueryOn(Bake("exfloor", (0, 0, 192, 192)));
        var r = Ex(q, 30, 96, 160, 96, 0);
        Check(r.Code == RynthNavPathApiV1.ResultOk && !r.Partial, $"open ground: ResultOk ({r.Code}, {r.Note})");
        Check(r.Corners.Count >= 2 && Flat(r.Corners[0], 30, 96) < 0.5 && Flat(r.Corners[^1], 160, 96) < 0.5, "start first, goal last");
        Check(Math.Abs(r.Length - Legs(r)) < 1e-9 && Math.Abs(r.Length - 130) < 1, $"info[0]: the sum of the legs ({r.Length:F2})");
        Check(r.EndDistance < 0.5, $"info[5]: at the goal ({r.EndDistance:F2})");
        Check(r.Corners.TrueForAll(c => Math.Abs(c.Z) < 0.5), "heights from the mesh (the floor's)");
        var up = Ex(q, 30, 96, 160, 96, 0, gz: 5);
        Check(up.Code == RynthNavPathApiV1.ResultOk && Math.Abs(up.EndDistance - 5) < 0.6, $"info[5] is 3D: a goal 5 u above the ground is ~5 u away ({up.EndDistance:F2})");
        var same = Ex(q, 50, 50, 50, 50, 0);
        Check(same.Code == RynthNavPathApiV1.ResultOk && same.Corners.Count == 2 && same.Length < 1e-6, "start = goal: two corners, length 0");
        var quiet = Ex(q, 30, 96, 160, 96, RynthNavPathApiV1.PathQuiet);
        Check(quiet.Code == r.Code && quiet.Corners.Count == r.Corners.Count && Math.Abs(quiet.Length - r.Length) < 1e-9, "PathQuiet changes only the log");
    }

    private static void PathExRoundAPortal()
    {
        var q = QueryOn(Bake("exportal", (0, 0, 192, 192)));
        var spots = new PortalAvoid();
        spots.Set(new[] { new PortalAvoid.Spot(95, 96, PortalAvoid.LiveRadius, "Unwanted Portal") }, Array.Empty<PortalAvoid.Spot>());
        var filter = new AvoidFilter(spots);
        var over = Ex(q, 30, 96, 160, 96, 0, filter);
        Check(over.Code == RynthNavPathApiV1.ResultOk && Closest(over, 95, 96) < 1 && over.Portals == 0, "without PathAvoidPortals it walks over the portal");
        var round = Ex(q, 30, 96, 160, 96, RynthNavPathApiV1.PathAvoidPortals, filter);
        double gap = Closest(round, 95, 96);
        Console.WriteLine($"     portal on the line: {over.Corners.Count} corners {over.Length:F1}u straight; avoided {round.Corners.Count} corners {round.Length:F1}u, closest {gap:F2}u, bent {round.Bent}");
        Check(round.Code == RynthNavPathApiV1.ResultOk && gap >= PortalAvoid.LiveRadius - 0.01, $"PathAvoidPortals: keeps out of the circle ({gap:F2} u)");
        Check(round.Portals == 1 && round.Corners.Count > over.Corners.Count && round.Length > over.Length, "bent round it, a little longer");
        Check(Flat(round.Corners[^1], 160, 96) < 0.5 && Math.Abs(round.Length - Legs(round)) < 1e-9, "still to the goal; the length counts the bend");
        Check(round.Corners.TrueForAll(c => double.IsFinite(c.Z) && Math.Abs(c.Z) < 0.5), "a bend corner gets the mesh's height");
        var into = Ex(q, 30, 96, 95, 96, RynthNavPathApiV1.PathAvoidPortals, filter);
        Check(into.Code == RynthNavPathApiV1.ResultOk && Flat(into.Corners[^1], 95, 96) < 1, "a goal inside a circle is still reached (its polygon is allowed)");
        var empty = new AvoidFilter(new PortalAvoid());
        Check(Ex(q, 30, 96, 160, 96, RynthNavPathApiV1.PathAvoidPortals, empty).Corners.Count == over.Corners.Count, "no portals near: the plain path");
    }

    private static void PathExPartial()
    {
        var q = QueryOn(Bake("exisland", (10, 10, 70, 180), (110, 10, 180, 180)));
        var no = Ex(q, 40, 96, 150, 96, 0);
        Check(no.Code == RynthNavPathApiV1.ResultNoPath && no.Corners.Count == 0, "the other island without PathAllowPartial: ResultNoPath (as FindPath)");
        var part = Ex(q, 40, 96, 150, 96, RynthNavPathApiV1.PathAllowPartial);
        Check(part.Code == RynthNavPathApiV1.ResultPartial && part.Partial, $"with it: ResultPartial ({part.Code}, {part.Note})");
        if (part.Corners.Count == 0) return;
        var end = part.Corners[^1];
        Console.WriteLine($"     partial: {part.Corners.Count} corners, ends at ({end.X:F1}, {end.Y:F1}), {part.EndDistance:F1}u from the goal");
        Check(end.X > 60 && end.X < 70 && Math.Abs(end.Y - 96) < 8, $"it ends on the near island's edge, facing the goal ({end.X:F1}, {end.Y:F1})");
        Check(Math.Abs(part.EndDistance - Math.Sqrt((150 - end.X) * (150 - end.X) + (96 - end.Y) * (96 - end.Y) + end.Z * end.Z)) < 1e-9 && part.EndDistance > 70,
              $"info[5]: what's left to the goal ({part.EndDistance:F1})");
        var all = Ex(q, 40, 96, 150, 96, RynthNavPathApiV1.PathAllowPartial | RynthNavPathApiV1.PathAvoidPortals | RynthNavPathApiV1.PathWallClearance | RynthNavPathApiV1.PathQuiet,
                     new AvoidFilter(new PortalAvoid()));
        Check(all.Code == RynthNavPathApiV1.ResultPartial, "with every flag at once too");
        var reach = Ex(q, 40, 96, 60, 150, RynthNavPathApiV1.PathAllowPartial);
        Check(reach.Code == RynthNavPathApiV1.ResultOk && !reach.Partial, "a goal it can reach is ResultOk, flag or not");
    }

    private static void PathExNoMeshAndStartSnap()
    {
        var q = QueryOn(Bake("exsnap", (10, 10, 70, 180), (110, 10, 180, 180)));
        var far = Ex(q, 40, 96, 2000, 2000, 0);
        Check(far.Code == RynthNavPathApiV1.ResultNoMesh && far.Note.Contains("goal"), $"no tile at the goal: ResultNoMesh ({far.Note})");
        var gap = Ex(q, 90, 96, 40, 96, 0);
        Check(gap.Code == RynthNavPathApiV1.ResultNoMesh && gap.Note.Contains("start"), $"a start 20 u from any ground: ResultNoMesh ({gap.Note})");
        var off = Ex(q, 73, 96, 40, 96, 0);
        Check(off.Code == RynthNavPathApiV1.ResultOk && off.Corners[0].X < 69 && off.Corners[0].X > 60, $"a start a few yards off the mesh: the wide search finds the ground beside it ({(off.Corners.Count > 0 ? off.Corners[0].X : 0):F1})");

        // A roof over part of a floor (3.5 u up: room to walk under it).
        var rq = QueryOn(BakeLayers("exroof", (0, 0, 192, 192, 0f), (60, 60, 130, 130, 3.5f)));
        var under = Ex(rq, 95, 95, 20, 95, 0, sz: 0.1);
        Check(under.Code == RynthNavPathApiV1.ResultOk && Math.Abs(under.Corners[0].Z) < 1, $"a start under a roof starts on the floor ({(under.Corners.Count > 0 ? under.Corners[0].Z : double.NaN):F2})");
        var onTop = Ex(rq, 95, 95, 100, 100, 0, sz: 3.6, gz: 3.6);
        Check(onTop.Code == RynthNavPathApiV1.ResultOk && Math.Abs(onTop.Corners[0].Z - 3.5) < 1, "one standing on the roof starts on the roof");
        var down = Ex(rq, 95, 95, 20, 95, RynthNavPathApiV1.PathAllowPartial, sz: 3.6, gz: 0);
        Check(down.Code == RynthNavPathApiV1.ResultPartial, "and the roof has no way down: partial");
    }

    private static void PathExWallsAndBuffer()
    {
        // The wall: from the south of tile 0 to the south of tile 2, the only way is round the north.
        var q = QueryOn(Bake("exwall", (0, 0, 192, 192), (192, 150, 384, 192), (384, 0, 576, 192)));
        var bare = Ex(q, 100, 20, 480, 20, 0);
        Check(bare.Code == RynthNavPathApiV1.ResultOk && bare.Corners.Count >= 4 && bare.Corners.Exists(c => c.Y > 150), $"round the wall through the north passage ({bare.Corners.Count} corners)");
        Check(bare.Length > 380 * 1.3 && bare.Length < 380 * 1.5, $"the length goes north and back ({bare.Length:F0}u, 380u straight)");
        var walls = Ex(q, 100, 20, 480, 20, RynthNavPathApiV1.PathWallClearance, clearance: 1.0);
        Check(walls.Code == RynthNavPathApiV1.ResultOk && walls.Pushed > 0 && walls.Corners.Count == bare.Corners.Count, $"PathWallClearance: corners pushed off the walls ({walls.Pushed})");
        int moved = 0; bool byClearance = true, away = true, onMesh = true;
        var plain = new DtQueryDefaultFilter();
        for (int i = 1; i + 1 < walls.Corners.Count && i < bare.Corners.Count; i++)
        {
            var a = bare.Corners[i]; var b = walls.Corners[i];
            double d = Flat(a, b.X, b.Y);
            onMesh &= NavPathQuery.OnMesh(q, plain, b.X, b.Y, (float)b.Z);
            if (d < 0.01) continue;
            moved++;
            byClearance &= Math.Abs(d - 1.0) < 0.01;
            // Away from the wall's own corner (the void is x 192..384, y 0..150).
            double ca = Math.Min(Flat(a, 192, 150), Flat(a, 384, 150)), cb = Math.Min(Flat(b, 192, 150), Flat(b, 384, 150));
            away &= cb > ca + 0.5;
        }
        Check(moved == walls.Pushed && byClearance, $"each moved corner moved the clearance, 1 u ({moved} moved)");
        Check(away, "out from the wall it turns round, not into it");
        Check(onMesh, "every corner on walkable ground");
        var zero = Ex(q, 100, 20, 480, 20, RynthNavPathApiV1.PathWallClearance, clearance: 0);
        Check(zero.Pushed == 0 && Math.Abs(zero.Length - bare.Length) < 1e-9, "a clearance of 0: nothing moves");

        // More corners than the caller has room for.
        Check(Ex(q, 100, 20, 480, 20, 0, maxCorners: 2).Code == RynthNavPathApiV1.ResultNoPath, "no room for the corners: ResultNoPath (FindPath's rule)");
        var cut = Ex(q, 100, 20, 480, 20, RynthNavPathApiV1.PathAllowPartial, maxCorners: 3);
        Check(cut.Code == RynthNavPathApiV1.ResultPartial && cut.Corners.Count == 3 && cut.Truncated && cut.EndDistance > 6,
              $"with PathAllowPartial: the first stretch, partial ({cut.Corners.Count} corners, {cut.EndDistance:F0}u left)");
    }

    private static void RaycastOnTheMesh()
    {
        var open = QueryOn(Bake("rayfloor", (0, 0, 192, 192)));
        Check(NavPathQuery.Raycast(open, 30, 96, 0, 160, 96, 0, out float t1) == RynthNavPathApiV1.RayClear && t1 >= 1, "open ground: clear");
        Check(NavPathQuery.Raycast(open, 30, 96, 0, 30, 96, 0, out _) == RynthNavPathApiV1.RayClear, "no distance at all: clear");
        var isl = QueryOn(Bake("rayisland", (10, 10, 70, 180), (110, 10, 180, 180)));
        Check(NavPathQuery.Raycast(isl, 40, 96, 0, 150, 96, 0, out float t2) == RynthNavPathApiV1.RayBlocked && t2 > 0.2f && t2 < 0.4f,
              $"across the gap: blocked at the island's edge (t {t2:F2})");
        Check(NavPathQuery.Raycast(isl, 90, 96, 0, 40, 96, 0, out _) == RynthNavPathApiV1.ResultNoMesh, "a start off the mesh: ResultNoMesh");
        Check(NavPathQuery.Raycast(isl, 40, 96, 0, 2000, 2000, 0, out _) == RynthNavPathApiV1.ResultNoMesh, "no mesh at the goal: ResultNoMesh");
        var wall = QueryOn(Bake("raywall", (0, 0, 192, 192), (192, 150, 384, 192), (384, 0, 576, 192)));
        Check(NavPathQuery.Raycast(wall, 100, 20, 0, 480, 20, 0, out _) == RynthNavPathApiV1.RayBlocked, "straight through the wall: blocked");
        Check(NavPathQuery.Raycast(wall, 100, 20, 0, 100, 170, 0, out _) == RynthNavPathApiV1.RayClear, "north on open ground: clear");
        Check(NavPathQuery.Raycast(wall, 100, 170, 0, 480, 170, 0, out _) == RynthNavPathApiV1.RayClear, "along the passage, over two tile seams: clear");
    }

    /// <summary>
    /// The real map (read only, when it's installed): FindPathEx and Raycast round Holtburg on the tiles
    /// a short query loads, a ring of goals at 40/80/150 u; the answers agree, and how long they take.
    /// </summary>
    /// <summary>
    /// RynthAi's hunt into buildings (2026-10-25) walks to a door's step out (BuildingMap: 1.5 m out of
    /// the door, at its foot) and asks RynthNav where the ground is there (a path from the step to
    /// itself): within 3 yd across and 1.5 in height it is a door from the ground. The steps of
    /// Holtburg's doors (A9B4, as BuildingMap finds them on the real dats) on the real tiles.
    /// </summary>
    private static void RealDoorsteps()
    {
        string dir = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData";
        if (!File.Exists(Path.Combine(dir, "nav_A9B4.tile"))) { Console.WriteLine("     (no real NavData here: skipped)"); return; }
        var nav = new DtNavMesh();
        nav.Init(new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 64, maxPolys = 1 << 16 }, 6);
        Check(NavPathQuery.TileBox(0xA9 * 192 + 96, 0xB4 * 192 + 96, 0xA9 * 192 + 96, 0xB4 * 192 + 96, NavPathQuery.DefaultMaxTiles, true, out int x0, out int y0, out int x1, out int y1), "A9B4's box");
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                uint lb = (uint)((x << 8) | y);
                if (File.Exists(Path.Combine(dir, $"nav_{lb:X4}.tile"))) nav.AddTile(ReadTile(dir, lb), 0, 0, out _);
            }
        var q = new DtNavMeshQuery(nav);
        // (door cell, step out x, y, z): every A9B4 door BuildingMap offers (its log, 2026-10-25).
        var steps = new (string Cell, double X, double Y, double Z)[]
        {
            ("A9B40102", 32537.1, 34698.0, 67.0), ("A9B40107", 32527.6, 34700.5, 66.8), ("A9B4010B", 32519.6, 34691.5, 66.0),
            ("A9B40113", 32474.7, 34714.7, 66.0), ("A9B40114", 32473.7, 34721.9, 67.0), ("A9B40115", 32482.7, 34714.6, 66.0),
            ("A9B40116", 32598.0, 34688.1, 67.0), ("A9B40118", 32595.5, 34690.6, 66.0), ("A9B40119", 32608.8, 34695.0, 66.0),
            ("A9B4011F", 32560.2, 34695.8, 70.0), ("A9B40120", 32555.4, 34702.6, 67.0), ("A9B40122", 32549.0, 34702.6, 67.0),
            ("A9B40124", 32560.2, 34693.4, 66.0), ("A9B40125", 32552.2, 34702.6, 66.0), ("A9B4012B", 32499.3, 34694.9, 66.0),
            ("A9B4012C", 32505.3, 34701.4, 66.0), ("A9B40133", 32506.9, 34701.4, 70.0), ("A9B40139", 32575.2, 34719.3, 66.0),
            ("A9B4013D", 32585.8, 34708.7, 66.0), ("A9B4014C", 32577.6, 34578.6, 95.0), ("A9B4014F", 32587.4, 34581.0, 94.8),
            ("A9B40150", 32580.5, 34578.6, 94.0), ("A9B40164", 32568.0, 34596.0, 94.0), ("A9B40165", 32544.0, 34596.0, 94.0),
            ("A9B4016C", 32522.7, 34592.7, 94.0), ("A9B4016D", 32521.7, 34599.9, 95.0), ("A9B4016E", 32530.7, 34592.6, 94.0),
            ("A9B40170", 32603.4, 34577.5, 94.0), ("A9B40177", 32508.8, 34716.5, 66.2), ("A9B40179", 32603.9, 34595.0, 96.0),
            ("A9B4017F", 32605.3, 34667.5, 66.0), ("A9B40184", 32478.7, 34614.6, 78.0), ("A9B40189", 32556.1, 34649.0, 80.0),
        };
        int flags = RynthNavPathApiV1.PathWallClearance | RynthNavPathApiV1.PathAllowPartial | RynthNavPathApiV1.PathQuiet;
        var r = new PathExResult();
        int ground = 0;
        var off = new List<string>();
        foreach (var (cell, x, y, z) in steps)
        {
            int code = NavPathQuery.FindPathEx(q, x, y, z, x, y, z, flags, 64, null, 1.0, r);
            bool ok = code is RynthNavPathApiV1.ResultOk or RynthNavPathApiV1.ResultPartial && r.Corners.Count > 0;
            double flat = 99, dz = 99;
            if (ok)
            {
                var c = r.Corners[^1];
                flat = Math.Sqrt((c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y));
                dz = c.Z - z;
            }
            if (ok && flat <= 3 && Math.Abs(dz) <= 1.5) ground++;
            else off.Add($"{cell} ({(ok ? $"{flat:F1} across, {dz:+0.0;-0.0} high" : $"code {code}")})");
        }
        Console.WriteLine($"     A9B4 doorsteps on RynthNav's ground: {ground} of {steps.Length}; not: {string.Join(", ", off)}");
        // 2026-10-25: 32 of 33; A9B4011F's step is 3.8 above the ground (an upper door with no floor under its step).
        Check(ground >= 30, $"Holtburg's door steps are on the ground ({ground} of {steps.Length})");
        Check(off.Any(o => o.StartsWith("A9B4011F")), "the upper door A9B4011F's isn't (the hunt leaves it alone)");
    }

    private static void RealPathEx()
    {
        string dir = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData";
        if (!File.Exists(Path.Combine(dir, "nav_A9B4.tile"))) { Console.WriteLine("     (no real NavData here: skipped)"); return; }
        var centre = RynthCore.Plugin.RynthNav.WorldPt.FromMap(42.08, 33.6);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nav = new DtNavMesh();
        var prm = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 64, maxPolys = 1 << 16 };
        nav.Init(prm, 6);
        int loaded = 0;
        Check(NavPathQuery.TileBox(centre.X, centre.Y, centre.X, centre.Y, NavPathQuery.DefaultMaxTiles, true, out int x0, out int y0, out int x1, out int y1), "Holtburg's box");
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                uint lb = (uint)((x << 8) | y);
                if (!File.Exists(Path.Combine(dir, $"nav_{lb:X4}.tile"))) continue;
                if (nav.AddTile(ReadTile(dir, lb), 0, 0, out _).Succeeded()) loaded++;
            }
        long loadMs = sw.ElapsedMilliseconds;
        var q = new DtNavMeshQuery(nav);

        // The start: walkable ground at the town's centre (it's under a roof in places; the snap finds the ground).
        var plain = new DtQueryDefaultFilter();
        q.FindNearestPoly(new RcVec3f((float)centre.X, 0, (float)centre.Y), new RcVec3f(30, 600, 30), plain, out long sRef, out RcVec3f s0, out _);
        Check(sRef != 0, "ground at Holtburg's centre");
        if (sRef == 0) return;
        double sx = s0.X, sy = s0.Z, sz = s0.Y;

        int flags = RynthNavPathApiV1.PathWallClearance | RynthNavPathApiV1.PathAllowPartial | RynthNavPathApiV1.PathQuiet;
        var counts = new Dictionary<int, int>();
        int rays = 0, clear = 0, agree = 0, compared = 0, queries = 0;
        double worst = 1, pathMs = 0, rayMs = 0;
        bool infoOk = true;
        var r = new PathExResult();
        var paths = new List<(double gx, double gy, double gz, double len)>();
        foreach (double dist in new[] { 40.0, 80.0, 150.0 })
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                double gx = sx + Math.Cos(a) * dist, gy = sy + Math.Sin(a) * dist;
                // A goal on the ground there (a monster stands on it), else the raw spot.
                q.FindNearestPoly(new RcVec3f((float)gx, (float)sz, (float)gy), new RcVec3f(2, 64, 2), plain, out long gr, out RcVec3f gp, out _);
                double gz = gr != 0 ? gp.Y : sz;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int code = NavPathQuery.FindPathEx(q, sx, sy, sz, gx, gy, gz, flags, 64, null, 1.0, r);
                pathMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; queries++;
                counts[code] = counts.GetValueOrDefault(code) + 1;
                if (code is RynthNavPathApiV1.ResultOk or RynthNavPathApiV1.ResultPartial)
                {
                    infoOk &= Math.Abs(r.Length - Legs(r)) < 1e-6 && r.Corners.Count >= 2 && r.Corners.Count <= 64
                              && (code == RynthNavPathApiV1.ResultPartial) == r.Partial;
                    if (code == RynthNavPathApiV1.ResultOk) paths.Add((gx, gy, gz, r.Length));
                }
                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int ray = NavPathQuery.Raycast(q, sx, sy, sz, gx, gy, gz, out _);
                rayMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; rays++;
                if (ray == RynthNavPathApiV1.RayClear)
                {
                    clear++;
                    // A clear straight walk: the planned path is (about) the straight line.
                    if (code == RynthNavPathApiV1.ResultOk)
                    {
                        compared++;
                        double straight = Math.Sqrt((r.Corners[^1].X - sx) * (r.Corners[^1].X - sx) + (r.Corners[^1].Y - sy) * (r.Corners[^1].Y - sy));
                        double ratio = straight < 1 ? 1 : r.Length / straight;
                        worst = Math.Max(worst, ratio);
                        if (ratio < 1.25) agree++;
                        else Console.WriteLine($"     clear ray, path x{ratio:F2}: goal ({gx - sx:F0},{gy - sy:F0}) from the start, {r.Corners.Count} corners, {r.Polys} polys, pushed {r.Pushed}: " + string.Join(" ", r.Corners.Select(c => $"({c.X - sx:F1},{c.Y - sy:F1},{c.Z - sz:F1})")));
                    }
                }
            }

        // Portals: one on the way of each of the longer paths, kept out of (or the plain path when there's no way round).
        int portalTried = 0, keptOut = 0, fellBack = 0;
        foreach (var p in paths)
        {
            if (p.len < 30) continue;
            NavPathQuery.FindPathEx(q, sx, sy, sz, p.gx, p.gy, p.gz, 0, 64, null, 1.0, r);
            if (r.Corners.Count < 2) continue;
            var mid = (X: (r.Corners[0].X + r.Corners[1].X) / 2, Y: (r.Corners[0].Y + r.Corners[1].Y) / 2);
            if (Flat(r.Corners[0], mid.X, mid.Y) < PortalAvoid.LiveRadius + 3) continue;   // the start would be inside it
            var spots = new PortalAvoid();
            spots.Set(new[] { new PortalAvoid.Spot(mid.X, mid.Y, PortalAvoid.LiveRadius, "test portal") }, Array.Empty<PortalAvoid.Spot>());
            int code = NavPathQuery.FindPathEx(q, sx, sy, sz, p.gx, p.gy, p.gz, flags | RynthNavPathApiV1.PathAvoidPortals, 64, new AvoidFilter(spots), 1.0, r);
            if (code is not (RynthNavPathApiV1.ResultOk or RynthNavPathApiV1.ResultPartial)) continue;
            portalTried++;
            if (Closest(r, mid.X, mid.Y) >= PortalAvoid.LiveRadius - 0.01) keptOut++;
            if (r.FellBack) fellBack++;
        }

        // A typical short query, many times (the tiles already loaded, as in a hunt).
        var pick = paths.Count > 0 ? paths[0] : (sx + 40, sy, sz, 0.0);
        int reps = 500;
        long tr = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < reps; i++) NavPathQuery.FindPathEx(q, sx, sy, sz, pick.Item1, pick.Item2, pick.Item3, flags | RynthNavPathApiV1.PathAvoidPortals, 64, null, 1.0, r);
        double perPath = System.Diagnostics.Stopwatch.GetElapsedTime(tr).TotalMilliseconds / reps;
        tr = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < reps; i++) NavPathQuery.Raycast(q, sx, sy, sz, pick.Item1, pick.Item2, pick.Item3, out _);
        double perRay = System.Diagnostics.Stopwatch.GetElapsedTime(tr).TotalMilliseconds / reps;

        string tally = string.Join(", ", counts.OrderBy(kv => -kv.Key).Select(kv => $"{(kv.Key switch { 1 => "ok", 2 => "partial", 0 => "no path", _ => "no mesh" })} {kv.Value}"));
        Console.WriteLine($"     Holtburg: {loaded} tiles read in {loadMs} ms; {queries} FindPathEx ({tally}), {pathMs / queries:F2} ms each cold; "
                        + $"Raycast {clear}/{rays} clear, {rayMs / rays:F3} ms each; clear rays vs paths {agree}/{compared} near-straight (worst x{worst:F2}); "
                        + $"portal on the way: kept out {keptOut}/{portalTried} ({fellBack} plain fallback); a short query {perPath:F3} ms, its ray {perRay:F3} ms");
        Check(loaded >= 9, $"Holtburg's 3x3 tiles ({loaded})");
        Check(counts.GetValueOrDefault(RynthNavPathApiV1.ResultOk) >= 12, "most of the ring is reached");
        Check(infoOk, "info consistent with the corners on every path");
        Check(compared == 0 || agree >= compared - 1, $"a clear ray's path is the straight line (or near it): {agree}/{compared}");
        Check(portalTried == 0 || keptOut + fellBack >= portalTried, $"a portal on the way is kept out of, or the plain path is taken ({keptOut}+{fellBack}/{portalTried})");
        Check(perPath < 20 && perRay < 5, $"fast enough to ask several times a second ({perPath:F3} / {perRay:F3} ms)");
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

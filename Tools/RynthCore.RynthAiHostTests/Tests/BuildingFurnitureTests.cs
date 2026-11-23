using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;
using V3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;
using Vec = RynthCore.Plugin.RynthAi.OutdoorHunt.Vec;

namespace RynthCore.RynthAiHostTests.Tests;

// Building walks and the furniture the cells place (2026-11-18). BuildingMap never added the cells' placed objects:
// in the building survey's six landblocks (7D64, E74E, BA17, A9B4, A9B3, DF61) walks went through tables, chairs and
// beds, and their corners stood in them. Now the furniture is on the map (the stairs left out), a door's step in and
// the doorway points are kept out of it, a walk round can leave furniture it starts in, and the dungeon map of a
// landblock with buildings leaves its stairs off too.
//   - hand-built: a house with a table, a ramp stair and a chair; a door with furniture inside it; walks round from
//     inside a box, and from a stair corner in the air;
//   - the real dats when they are on this PC: every door to every room of the six landblocks, with and without the
//     furniture; DungeonPathfinder's map of 7D64 and E74E.
internal static class BuildingFurnitureTests
{
    private const string AcDir = @"C:\Turbine\Asheron's Call";

    public static void Register(Runner r)
    {
        r.Add("building furniture: a hand-built house: the walk goes round its table; its ramp stair is no wall, its chair (a connector by the map's chain) is", HouseWithFurniture);
        r.Add("building furniture: a door's step in clear of furniture: shortened, then sideways, else no door; a walk from it starts in the cell that holds it", DoorStepIn);
        r.Add("building furniture: a walk round that starts inside a box gets out of it; in a building map one from a stair corner in the air doesn't go round on the floor below", WalkRoundStart);
        r.Add("building furniture (real dats): the survey's six landblocks, every door to every room: no corner in furniture, no walk lost, stairs left out, landblock objects on the map", RealWalks);
        r.Add("building furniture (real dats): DungeonPathfinder's map of 7D64 and E74E: the stairs no walls and no doorway closed for them, every room still reached", RealDungeonMap);
        r.Add("building doors (real dats): a window with a deep sill is no door (Thread B470#25's 1.5 m windows, 25E1's); a tall door with a step down inside, and a low one at the ground, still are", WindowsNotDoors);
        r.Add("building furniture (review): a sphere the chain reaches from a dais beside it is furniture, not a stair (its rings are no steps); 8090012A's 1 m ball on the real dats", RoundNotStairs);
    }

    private static V3 P(double x, double y, double z) => new((float)x, (float)y, (float)z);

    private static V3[] Rect(double x0, double y0, double x1, double y1, double z = 0) => new[] { P(x0, y0, z), P(x1, y0, z), P(x1, y1, z), P(x0, y1, z) };

    private static V3[] Upright(double x0, double y0, double x1, double y1, double z0 = 0, double z1 = 3)
        => new[] { P(x0, y0, z0), P(x1, y1, z0), P(x1, y1, z1), P(x0, y0, z1) };

    private static DungeonNavNode Node(uint id, double cx, double cy, double z = 0) =>
        new() { CellId = id, EW = DungeonGeometry.WorldToNav(cx), NS = DungeonGeometry.WorldToNav(cy), Z = (float)z };

    private static V3[] Box(double x0, double y0, double z0, double x1, double y1, double z1) => PlacedObjectTests.Box(x0, y0, z0, x1, y1, z1);

    /// <summary>A closed wedge over x0..x1, rising along y from z0 at y0 to z1 at y1 (a ramp stair).</summary>
    private static V3[] Wedge(double x0, double y0, double x1, double y1, double z0, double z1)
    {
        V3 a = P(x0, y0, z0), b = P(x1, y0, z0), c = P(x1, y1, z0), d = P(x0, y1, z0), e = P(x1, y1, z1), f = P(x0, y1, z1);
        return new[]
        {
            a, c, b, a, d, c,      // bottom
            a, b, e, a, e, f,      // the slope
            d, f, e, d, e, c,      // the back, at y1
            a, f, d,               // the west side
            b, c, e,               // the east side
        };
    }

    private static BoundingVolume Mesh(V3[] tris)
    {
        var min = tris[0]; var max = tris[0];
        foreach (var v in tris) { min = V3.Min(min, v); max = V3.Max(max, v); }
        return new BoundingVolume { Type = BoundingVolume.VolumeType.TriangleMesh, MeshTriangles = tris, Min = min, Max = max, NoAabbFallback = true };
    }

    private const uint Lb = 0xA9B4;
    private const uint CellA = 0xA9B40100, CellB = 0xA9B40101, CellV = 0xA9B40110, CellR = 0xA9B40111;
    private const uint Table = 0x0200011E, Ramp = 0x020001B4, Chair = 0x020000A5, Post = 0x02000158;

    /// <summary>
    /// A (100..110 x 100..110) and B (110..130 x 100..120), joined by a 4 m opening at x 110, y 103..107; a door in A's
    /// west wall (x 100, y 103..106). In B: a table (117..120 x 103.5..106.5, 0.8 m) across the way from the opening to
    /// (126, 105), a ramp stair up 2 m over 3 m (122..124 x 112..115), a chair (seat 0.45 m, back 1.0 m) at 112, 116.
    /// </summary>
    private static BuildingMap.Block FurnishedHouse(IReadOnlyList<(uint SetupId, uint CellId, BoundingVolume Mesh)>? statics)
    {
        var graph = new Dictionary<uint, DungeonNavNode> { [CellA] = Node(CellA, 105, 105), [CellB] = Node(CellB, 120, 110) };
        graph[CellA].Neighbors.Add(CellB); graph[CellB].Neighbors.Add(CellA);
        var ab = Upright(110, 103, 110, 107);
        var door = Upright(100, 103, 100, 106);
        var portals = new List<(uint, V3[])> { (CellA, ab), (CellB, ab.Reverse().ToArray()), (CellA, door) };
        var floors = new List<(uint, V3[])> { (CellA, Rect(100, 100, 110, 110)), (CellB, Rect(110, 100, 130, 120)) };
        return BuildingMap.Build(Lb, graph, portals, floors, new List<(uint, V3[])>(), new List<(uint, V3[])> { (CellA, door) }, statics);
    }

    private static List<(uint SetupId, uint CellId, BoundingVolume Mesh)> HouseStatics() => new()
    {
        (Table, CellB, Mesh(Box(117, 103.5, 0, 120, 106.5, 0.8))),
        (Ramp, CellB, Mesh(Wedge(122, 112, 124, 115, 0, 2.0))),
        (Chair, CellB, Mesh(Box(112, 116, 0, 112.5, 116.5, 0.45).Concat(Box(112, 116.5, 0, 112.5, 116.6, 1.0)).ToArray())),
    };

    private static void HouseWithFurniture()
    {
        var bare = FurnishedHouse(null);
        var blk = FurnishedHouse(HouseStatics());
        var geo = blk.Geo!;
        Check.True(blk.PlacedGiven == 3 && blk.Furniture == 2 && blk.Stairs == 1, $"3 pieces given: 2 furniture, 1 stair left out ({blk.PlacedGiven}, {blk.Furniture}, {blk.Stairs})");
        var info = geo.PlacedObjects;
        var ramp = info.Single(p => p.Setup == Ramp);
        var chair = info.Single(p => p.Setup == Chair);
        var table = info.Single(p => p.Setup == Table);
        Check.True(ramp.Class == PlacedClass.Connector && !ramp.OnMap, $"the ramp: a connector, left off the map ({ramp.Class}, on the map {ramp.OnMap})");
        Check.True(chair.Class == PlacedClass.Connector && chair.OnMap, $"the chair: a connector by the map's chain (seat, then its back's top at 1 m), but furniture: on the map ({chair.Class}, {chair.OnMap})");
        Check.True(table.Class == PlacedClass.Obstacle && table.OnMap, $"the table: on the map ({table.Class})");
        Check.False(geo.InsidePlaced(123, 113.5, 0), "on the ramp's foot: nothing in the way (no wall for a stair)");
        Check.True(geo.InsidePlaced(118.5, 105, 0) && geo.InsidePlaced(112.25, 116.55, 0), "in the table and the chair's back: inside");

        var d = blk.ByCell[CellA].Doors.Single();
        var goal = new Vec(126, 105, 0);
        var map0 = new BuildingMap(null, null); map0.Add(bare);
        var map = new BuildingMap(null, null); map.Add(blk);
        var p0 = map0.Inside(d.Cell, d.Inner, CellB, goal);
        var p = map.Inside(d.Cell, d.Inner, CellB, goal);
        var way0 = new List<Vec> { d.Inner }; way0.AddRange(p0.Corners);
        var way = new List<Vec> { d.Inner }; way.AddRange(p.Corners);
        Check.True(Through(geo, way0) > 0, "bare rooms: the walk goes straight through the table: " + Pts(way0));
        Check.True(p.Status == OutdoorHunt.PathStatus.Ok && p.Corners[^1] == goal, $"furnished: a walk ({p.Status})");
        Check.Eq(Through(geo, way), 0, "no leg of it through the table: " + Pts(way));
        Check.Eq(way.Skip(1).Count(v => DungeonPathfinder.InsidePlacedAt(geo, v.X, v.Y, v.Z)), 0, "no corner in furniture");
        Check.True(p.Length < p0.Length + 4, $"round it, not far off ({p.Length:F1} m, {p0.Length:F1} bare)");
    }

    // Legs (0.25 m samples, on the floor under them) inside something placed.
    private static int Through(DungeonGeometry geo, List<Vec> way)
    {
        int n = 0;
        for (int i = 0; i + 1 < way.Count; i++)
            if (!BuildingMap.LegClearOfPlaced(geo, way[i].X, way[i].Y, way[i + 1].X, way[i + 1].Y, FloorAt(geo, way[i]))) n++;
        return n;
    }

    private static double FloorAt(DungeonGeometry g, Vec v) => g.TryFloorHeight(v.X, v.Y, v.Z, 1.0, out double h) ? h : v.Z;

    private static string Pts(IReadOnlyList<Vec> c) => string.Join(" ", c.Select(v => $"{v.X:0.##},{v.Y:0.##},{v.Z:0.#}{(v.Door ? "D" : "")}"));

    /// <summary>One room A (100..110 x 100..110) with a west door (x 100, y 103..106), the pieces given in it.</summary>
    private static BuildingMap.Block DoorRoom(params V3[][] pieces)
    {
        var graph = new Dictionary<uint, DungeonNavNode> { [CellA] = Node(CellA, 105, 105) };
        var door = Upright(100, 103, 100, 106);
        var statics = pieces.Select(t => (Post, CellA, Mesh(t))).ToList();
        return BuildingMap.Build(Lb, graph, new List<(uint, V3[])> { (CellA, door) }, new List<(uint, V3[])> { (CellA, Rect(100, 100, 110, 110)) },
                                 new List<(uint, V3[])>(), new List<(uint, V3[])> { (CellA, door) }, statics);
    }

    private static void DoorStepIn()
    {
        // Nothing in the way: 1.5 m in, on the door's centre line (y 104.5).
        var free = DoorRoom(Box(105, 101, 0, 106, 102, 1)).ByCell[CellA].Doors.Single();
        Check.True(Math.Abs(free.Inner.X - 101.5) < 0.01 && Math.Abs(free.Inner.Y - 104.5) < 0.01, $"clear: 1.5 m in on its centre line ({free.Inner.X:F2}, {free.Inner.Y:F2})");

        // A stool at 1.2 to 1.8 m in: shortened to 1.0 m.
        var stool = DoorRoom(Box(101.2, 103.8, 0, 101.8, 105.2, 0.6)).ByCell[CellA].Doors.Single();
        Check.True(Math.Abs(stool.Inner.X - 101.0) < 0.01 && Math.Abs(stool.Inner.Y - 104.5) < 0.01, $"a stool 1.2 m in: the step in shortened to 1.0 m ({stool.Inner.X:F2}, {stool.Inner.Y:F2})");

        // A post on the centre line from 0.4 m in: every point on the line is in it or behind it, so 0.5 m to one side.
        var postBlk = DoorRoom(Box(100.4, 104.4, 0, 101.6, 104.6, 1.8));
        var post = postBlk.ByCell[CellA].Doors.Single();
        Check.True(Math.Abs(post.Inner.X - 101.5) < 0.01 && Math.Abs(Math.Abs(post.Inner.Y - 104.5) - BuildingMap.InnerSideways) < 0.01,
                   $"a post on its centre line: 0.5 m to one side ({post.Inner.X:F2}, {post.Inner.Y:F2})");
        Check.True(BuildingMap.LegClearOfPlaced(postBlk.Geo!, post.At.X, post.At.Y, post.Inner.X, post.Inner.Y, 0), "and the leg in to it misses the post");

        // A cabinet filling the door's inside: no door.
        var full = DoorRoom(Box(100.3, 102.5, 0, 102.5, 106.5, 2.0)).ByCell[CellA];
        Check.Eq(full.Doors.Count, 0, "a cabinet filling its inside: no door");
        Check.True(full.NotDoors.Any(n => n.Contains("furniture stands a step inside it")), "and why: " + string.Join(" | ", full.NotDoors));

        // A door cell V 1 m deep (100..101) before the room R: the step in is on R's floor, and a walk from it starts in R.
        var graph = new Dictionary<uint, DungeonNavNode> { [CellV] = Node(CellV, 100.5, 104.5), [CellR] = Node(CellR, 105, 105) };
        graph[CellV].Neighbors.Add(CellR); graph[CellR].Neighbors.Add(CellV);
        var door = Upright(100, 103, 100, 106);
        var vr = Upright(101, 103, 101, 106);
        var blk = BuildingMap.Build(Lb, graph, new List<(uint, V3[])> { (CellV, door), (CellV, vr), (CellR, vr.Reverse().ToArray()) },
                                    new List<(uint, V3[])> { (CellV, Rect(100, 103, 101, 106)), (CellR, Rect(101, 100, 110, 110)) },
                                    new List<(uint, V3[])>(), new List<(uint, V3[])> { (CellV, door) });
        var dv = blk.ByCell[CellV].Doors.Single();
        Check.True(dv.Inner.X > 101 && dv.Cell == CellV, $"a door in a 1 m door cell: its own cell, its step in on the room's floor ({dv.Cell:X8}, step in at x {dv.Inner.X:F2})");
        var map = new BuildingMap(null, null); map.Add(blk);
        var p = map.Inside(dv.Cell, dv.Inner, CellR, new Vec(108, 105, 0));
        Check.True(p.Status == OutdoorHunt.PathStatus.Ok && p.Corners.All(v => v.X > dv.Inner.X - 0.01), "a walk on from it doesn't go back out to the doorway into the room first: " + Pts(p.Corners));
        var back = map.Inside(CellR, new Vec(108, 105, 0), dv.Cell, dv.Inner);
        Check.True(back.Status == OutdoorHunt.PathStatus.Ok && back.Corners.All(v => v.X > dv.Inner.X - 0.01), "nor one to it past it into the door cell and back: " + Pts(back.Corners));
    }

    // Review of wf/obst-int, finding 2: the stair test counted a sphere's walkable rings as steps, so a collision sphere the
    // connector chain reached from a dais beside it was left off the map as a stair, and walks went through a 1 m ball.
    private static void RoundNotStairs()
    {
        const uint Ball = 0x0200035E, Dais = 0x0200033D;
        var sphere = new List<V3>();
        GeometryLoader.AppendSphereSolid(sphere, P(119.5, 113, 0.55), 0.5f);   // a 1 m ball, 5 cm over the floor
        var ball = sphere.ToArray();
        Check.True(BuildingMap.ClimbsByItself(ball, 0), "the ball's rings pass the stair test's steps (why it was left out)");
        foreach (bool round in new[] { false, true })
        {
            var m = Mesh(ball);
            m.Round = round;
            var blk = FurnishedHouse(new List<(uint, uint, BoundingVolume)> { (Dais, CellB, Mesh(Box(117, 112, 0, 119, 114, 0.55))), (Ball, CellB, m) });
            var b = blk.Geo!.PlacedObjects.Single(p => p.Setup == Ball);
            Check.Eq(b.Class, PlacedClass.Connector, $"round {round}: the chain reaches the ball from the dais's top: a connector");
            if (!round) Check.False(b.OnMap, "a model's polygons: taken for a stair, left off (the review's case)");
            else Check.True(b.OnMap && blk.Geo.InsidePlaced(119.5, 113, 0) && blk.Stairs == 0, $"the loader's sphere (round): furniture, on the map, in the way at its middle ({blk.Stairs} stairs)");
        }

        var g = Dats();
        if (g == null) { Check.True(true, "no dats on this PC: the real case skipped"); return; }
        var statics = g.GetDungeonStaticMeshes(0x8090);
        var pieces = statics.Where(s => s.SetupId == Ball).ToList();
        Check.True(pieces.Count > 0 && pieces.All(s => s.Mesh.Round), $"8090: the loader's {pieces.Count} pieces of 0x0200035E (spheres) are marked round");
        Check.True(statics.Where(s => s.SetupId == Dais).All(s => !s.Mesh.Round), "the dais (polygons) is not");
        var blk8090 = BuildingMap.Load(0x8090, g.CellDat, g.DungeonLOS, x => g.GetDungeonStaticMeshes(x));
        var geo = blk8090.Geo!;
        var balls = geo.PlacedObjects.Where(p => p.Setup == Ball).ToList();
        var chained = balls.Where(p => p.Cell == 0x8090012A).ToList();
        Check.True(chained.Count > 0 && chained.All(p => p.OnMap && geo.InsidePlaced((p.MinX + p.MaxX) / 2, (p.MinY + p.MaxY) / 2, p.Floor)),
                   $"8090012A's ball beside the dais: on the map, in the way at its middle ({string.Join(" ", chained.Select(p => $"{p.Class}/{p.OnMap}"))})");
        Check.True(balls.All(p => p.OnMap), $"all {balls.Count} copies of it in 8090 on the map ({balls.Count(p => p.OnMap)})");
        Console.WriteLine($"          8090: {balls.Count} balls on the map; {blk8090.Furniture} furniture, {blk8090.Stairs} stairs left out");
    }

    private static DungeonGeometry Floors(params (uint Cell, V3[] Poly)[] floors)
    {
        var graph = new Dictionary<uint, DungeonNavNode>();
        foreach (var (c, _) in floors) graph.TryAdd(c, Node(c, 10, 5));
        return DungeonGeometry.Build(graph, Array.Empty<(uint, V3[])>(), floors, Array.Empty<(uint, V3[])>());
    }

    private static NavPoint N(double x, double y, double z) => new() { Type = NavPointType.Point, NS = DungeonGeometry.WorldToNav(y), EW = DungeonGeometry.WorldToNav(x), Z = z / 240.0 };

    private static void WalkRoundStart()
    {
        // A 20 x 10 m room; the walk starts in a box (a corner laid by a bed) and its leg goes through a second box.
        const uint c = 0x7E7E0100;
        var geo = Floors((c, Rect(0, 0, 20, 10)));
        geo.AddPlacedObjects(new (uint, uint, V3[])[] { (Table, c, Box(4, 4, 0, 6, 6, 1)), (Table, c, Box(9, 3, 0, 11, 7, 1)) });
        Check.True(geo.InsidePlaced(5, 5, 0), "the start is inside the first box");
        var pts = new List<NavPoint> { N(5, 5, 0), N(15, 5, 0) };
        DungeonPathfinder.WalkRoundHoles(pts, geo, objectsOnly: true);
        Check.True(DungeonPathfinder.HolesWalkedRound == 1 && DungeonPathfinder.HolesLeft == 0,
                   $"walked round (from inside the box there was no way at all before): {DungeonPathfinder.HolesWalkedRound} round, {DungeonPathfinder.HolesLeft} left");
        var way = pts.Select(q => new Vec(DungeonGeometry.NavToWorld(q.EW), DungeonGeometry.NavToWorld(q.NS), q.Z * 240)).ToList();
        Check.True(way.Count >= 3 && !geo.InsidePlaced(way[1].X, way[1].Y, 0), "its first corner is out of the box: " + Pts(way));
        Check.Eq(Through(geo, way.Skip(1).ToList()), 0, "and from there no leg goes into either box: " + Pts(way));

        // A ground floor at 0 (0..20 x 0..10) under an upper floor at 3 (10..20), a crate on the ground floor at 11..12;
        // a leg from a stair corner in the air (8, 5, 1.5) to the upper floor (16, 5, 3).
        var two = Floors((c, Rect(0, 0, 20, 10, 0)), (c, Rect(10, 0, 20, 10, 3)));
        two.AddPlacedObjects(new (uint, uint, V3[])[] { (Table, c, Box(11, 3, 0, 12, 7, 1)) });
        var dun = new List<NavPoint> { N(8, 5, 1.5), N(16, 5, 3) };
        DungeonPathfinder.WalkRoundHoles(dun, two, objectsOnly: true);
        bool down = dun.Skip(1).Take(dun.Count - 2).Any(q => q.Z * 240 < 0.5);
        Check.True(DungeonPathfinder.HolesWalkedRound == 1 && down, $"a dungeon map: the floor nearest the corner within 3 m is the ground floor's, the walk round goes down there ({dun.Count} points)");

        var bld = Floors((c, Rect(0, 0, 20, 10, 0)), (c, Rect(10, 0, 20, 10, 3)));
        bld.AddPlacedObjects(new (uint, uint, V3[])[] { (Table, c, Box(11, 3, 0, 12, 7, 1)) });
        DungeonPathfinder.MarkBuildingMap(bld);
        var up = new List<NavPoint> { N(8, 5, 1.5), N(16, 5, 3) };
        DungeonPathfinder.WalkRoundHoles(up, bld, objectsOnly: true);
        Check.True(DungeonPathfinder.HolesWalkedRound == 0 && DungeonPathfinder.HolesLeft == 1 && up.Count == 2,
                   $"a building map: no floor within 1 m of the corner, no walk round down to the ground floor (the leg left as laid: {DungeonPathfinder.HolesWalkedRound} round, {DungeonPathfinder.HolesLeft} left)");
        // From the ground floor itself, a walk round on it is still a way.
        var ground = new List<NavPoint> { N(8, 5, 0), N(15, 5, 0) };
        DungeonPathfinder.WalkRoundHoles(ground, bld, objectsOnly: true);
        Check.True(DungeonPathfinder.HolesWalkedRound == 1, "a building map, from the ground floor across it: walked round the crate");
    }

    // ── The real dats ────────────────────────────────────────────────────────────────────────────

    /// <summary>DreamWeave Thread B470 (2026-11-19, "threads are still trying to navigate through windows"): building #25's
    /// openings in cells 018D and 018E are 1.5 m tall, their foot 1.0 m over the ground and the room's floor; the floor half
    /// a metre in is the sill's own top, so the old sill test passed them and the hunt walked at them as doors.</summary>
    private static void WindowsNotDoors()
    {
        var g = Dats();
        if (g == null) { Check.True(true, "no dats on this PC: skipped"); return; }
        double Ground(double x, double y) => g.GetTerrainZWorld((float)x, (float)y);
        BuildingMap.Block Load(uint lb, bool ground) => BuildingMap.Load(lb, g.CellDat!, g.DungeonLOS, x => g.GetDungeonStaticMeshes(x), ground ? Ground : null);
        static bool Has(BuildingMap.Building b, uint cellLow) => b.Doors.Any(d => (d.Cell & 0xFFFF) == cellLow);
        static bool Window(BuildingMap.Building b, uint cellLow) => b.NotDoors.Any(n => n.Contains(cellLow.ToString("X4") + ": a window", StringComparison.Ordinal));

        var before = Load(0xB470, ground: false);
        var after = Load(0xB470, ground: true);
        var b25 = after.Buildings[25];
        Check.True(Has(before.Buildings[25], 0x018D) && Has(before.Buildings[25], 0x018E), "B470#25: without the ground, its windows in 018D and 018E read as doors (as live)");
        Check.True(!Has(b25, 0x018D) && !Has(b25, 0x018E), $"B470#25: with the ground, 018D and 018E are no doors ({string.Join(", ", b25.Doors.Select(d => (d.Cell & 0xFFFF).ToString("X4")))})");
        Check.True(Window(b25, 0x018D) && Window(b25, 0x018E), "B470#25: each is named a window: " + string.Join(" | ", b25.NotDoors.Where(n => n.Contains("window"))));
        Check.True(Has(b25, 0x018A) && Has(b25, 0x018C), "B470#25: its two real doors (018A, 018C: 2.5 m tall, the foot at the floor) are still doors");
        int otherBefore = before.Buildings.Where(b => b.Index != 25).Sum(b => b.Doors.Count);
        int otherAfter = after.Buildings.Where(b => b.Index != 25).Sum(b => b.Doors.Count);
        Check.True(otherBefore == otherAfter && otherAfter > 0, $"B470: every other building keeps its doors ({otherBefore} -> {otherAfter})");

        // 25E1#0: a 1.5 x 1.4 m window 0.7 m over the floor and 3.3 m over the ground, its only opening.
        var e1 = Load(0x25E1, ground: true).Buildings[0];
        Check.True(!Has(e1, 0x0101), "25E1#0: its 1.5 m window in 0101 is no door");
        // 132C#0: a 2.5 m door, the floor 0.8 m below its foot a step in: a door with a step down, kept (too tall for a window).
        Check.True(Has(Load(0x132C, ground: true).Buildings[0], 0x0101), "132C#0: its 2.5 m door with a step down inside is still a door");
        // 2581#25: a 1.79 m tall, 2.5 m wide opening at the ground (0.2 m over it): kept.
        Check.True(Has(Load(0x2581, ground: true).Buildings[25], 0x016B), "2581#25: its low opening at the ground (016B) is still a door");
    }

    private static GeometryLoader? _geo;
    private static bool _geoTried;
    private static GeometryLoader? Dats()
    {
        if (_geoTried) return _geo;
        _geoTried = true;
        if (!Directory.Exists(AcDir)) return null;
        var g = new GeometryLoader();
        if (!g.Initialize(AcDir) || g.CellDat == null || g.DungeonLOS == null) return null;
        return _geo = g;
    }

    /// <summary>A spot on the cell's floor: the middle of its biggest upward floor polygon (as HuntBuildingTests).</summary>
    private static bool FloorSpot(GeometryLoader g, BuildingMap.Block blk, uint cell, out Vec spot)
    {
        spot = default;
        double bestArea = 0;
        foreach (var p in g.DungeonLOS.GetDungeonMapFloorPolygons(blk.Landblock))
        {
            if (p.CellId != cell || p.Vertices == null || p.Vertices.Length < 3 || !DungeonGeometry.IsUpwardFloor(p.Vertices)) continue;
            double ox = p.Vertices[0].X, oy = p.Vertices[0].Y, area = 0, sx = 0, sy = 0, sz = 0;
            for (int i = 0; i < p.Vertices.Length; i++)
            {
                var a = p.Vertices[i]; var b = p.Vertices[(i + 1) % p.Vertices.Length];
                area += ((double)a.X - ox) * ((double)b.Y - oy) - ((double)b.X - ox) * ((double)a.Y - oy);
                sx += a.X; sy += a.Y; sz += a.Z;
            }
            area = Math.Abs(area) / 2;
            if (area <= bestArea) continue;
            int n = p.Vertices.Length;
            var c = new Vec(sx / n, sy / n, sz / n);
            if (!blk.Geo!.OnCellFloor(cell, c.X, c.Y, c.Z + 0.1)) continue;
            bestArea = area;
            spot = c;
        }
        return bestArea > 0.5;
    }

    private static bool BackDown(IReadOnlyList<Vec> corners, Vec from)
    {
        bool climbing = false;
        foreach (var v in corners)
        {
            if (v.Z >= from.Z + 1.5) climbing = true;
            else if (climbing && v.Z < from.Z + 0.5) return true;
        }
        return false;
    }

    private static readonly uint[] Survey = { 0x7D64, 0xE74E, 0xBA17, 0xA9B4, 0xA9B3, 0xDF61 };

    private static void RealWalks()
    {
        var g = Dats();
        if (g == null) { Check.True(true, "no dats on this PC: skipped"); return; }
        int walks = 0, lost = 0, cornersIn = 0, innerIn = 0, legs0 = 0, legs = 0, back0 = 0, back = 0;
        double ms0 = 0, ms = 0, worst = 0;
        foreach (uint lb in Survey)
        {
            var blk0 = BuildingMap.Load(lb, g.CellDat, g.DungeonLOS);
            var blk = BuildingMap.Load(lb, g.CellDat, g.DungeonLOS, x => g.GetDungeonStaticMeshes(x));
            var geo = blk.Geo!;
            var doors0 = blk0.Buildings.SelectMany(b => b.Doors).ToList();
            var doors = blk.Buildings.SelectMany(b => b.Doors).ToList();
            Check.Eq(doors.Count, doors0.Count, $"{lb:X4}: as many doors with the furniture ({blk.Furniture} pieces, {blk.Stairs} stairs left out)");
            var map0 = new BuildingMap(null, null); map0.Add(blk0);
            var map = new BuildingMap(null, null); map.Add(blk);
            foreach (var b in blk0.Buildings)
                foreach (var d0 in b.Doors)
                {
                    var d = doors.FirstOrDefault(x => x.At.FlatDistanceTo(d0.At) < 0.01);
                    if (d.Cell == 0) { Check.True(false, $"{lb:X4}: the door at {d0.At.X:F1},{d0.At.Y:F1} is still a door"); continue; }
                    if (DungeonPathfinder.InsidePlacedAt(geo, d.Inner.X, d.Inner.Y, d.Inner.Z)) innerIn++;
                    foreach (uint c in b.Cells)
                    {
                        if (!FloorSpot(g, blk0, c, out var spot)) continue;
                        walks++;
                        var sw = Stopwatch.StartNew();
                        var p0 = map0.Inside(d0.Cell, d0.Inner, c, spot);
                        ms0 += sw.Elapsed.TotalMilliseconds;
                        sw.Restart();
                        var p = map.Inside(d.Cell, d.Inner, c, spot);
                        double t = sw.Elapsed.TotalMilliseconds;
                        ms += t; worst = Math.Max(worst, t);
                        if (p0.Status == OutdoorHunt.PathStatus.Ok && p.Status != OutdoorHunt.PathStatus.Ok)
                        {
                            lost++;
                            Check.True(false, $"{lb:X4} {map.Name(b.Id)}: door {d.Cell & 0xFFFF:X4} to {c & 0xFFFF:X4} was a walk, now {p.Status}");
                        }
                        if (p.Status != OutdoorHunt.PathStatus.Ok) continue;
                        for (int i = 0; i + 1 < p.Corners.Count; i++)
                            if (DungeonPathfinder.InsidePlacedAt(geo, p.Corners[i].X, p.Corners[i].Y, p.Corners[i].Z))
                            {
                                cornersIn++;
                                Check.True(false, $"{lb:X4} door {d.Cell & 0xFFFF:X4} to {c & 0xFFFF:X4}: corner {i} ({p.Corners[i].X:F2},{p.Corners[i].Y:F2},{p.Corners[i].Z:F2}) in furniture");
                            }
                        // Legs through furniture, both walks judged on the furnished map (a goal in furniture: its last leg can't help it).
                        bool goalIn = DungeonPathfinder.InsidePlacedAt(geo, spot.X, spot.Y, spot.Z);
                        var way0 = new List<Vec> { d0.Inner }; way0.AddRange(p0.Corners);
                        var way = new List<Vec> { d.Inner }; way.AddRange(p.Corners);
                        if (goalIn) { way0.RemoveAt(way0.Count - 1); way.RemoveAt(way.Count - 1); }
                        legs0 += Through(geo, way0);
                        legs += Through(geo, way);
                        if (Math.Abs(spot.Z - d.Inner.Z) > 2.0)
                        {
                            if (BackDown(p0.Corners, d0.Inner)) back0++;
                            if (BackDown(p.Corners, d.Inner)) back++;
                        }
                    }
                }
        }
        Check.True(walks >= 1100, $"{walks} walks from every door to every room");
        Check.Eq(lost, 0, "no walk lost to the furniture");
        Check.Eq(cornersIn, 0, "no corner of a walk in furniture");
        Check.Eq(innerIn, 0, "no door's step in in furniture");
        Check.True(back <= back0, $"no more walks upstairs back down to the ground floor ({back}, {back0} bare)");
        Check.True(legs * 3 <= legs0, $"legs through furniture: {legs} (bare rooms: {legs0})");
        Check.True(ms <= ms0 * 3 + 2000 && worst < 1500, $"time: {ms0 / walks:F1} ms a walk bare, {ms / walks:F1} with the furniture, the worst {worst:F0} ms");
        Console.WriteLine($"          {walks} walks: legs through furniture {legs0} -> {legs}, corners in it -> {cornersIn}, upstairs back down {back0} -> {back}; {ms0 / walks:F1} -> {ms / walks:F1} ms a walk (worst {worst:F0})");

        // The stairs left out, the furniture on the map (setups at their cells).
        void Placed(uint lb, uint setup, uint cell, bool onMap, string what)
        {
            var blk = BuildingMap.Load(lb, g.CellDat, g.DungeonLOS, x => g.GetDungeonStaticMeshes(x));
            var found = blk.Geo!.PlacedObjects.Where(p => p.Setup == setup && (p.Cell & 0xFFFF) == cell).ToList();
            Check.True(found.Count > 0 && found.All(p => p.OnMap == onMap), $"{lb:X4} {setup:X8}@{cell:X4}, {what}: {(onMap ? "on the map" : "left out")} ({string.Join(" ", found.Select(p => $"{p.Class}/{p.OnMap}"))})");
        }
        Placed(0xA9B3, 0x020001B4, 0x0103, false, "a 44 degree ramp stair");
        Placed(0x7D64, 0x02000340, 0x0127, false, "a stair");
        Placed(0xDF61, 0x01000E70, 0x010C, false, "the landing between two ramps of a stair");
        Placed(0xE74E, 0x01000ACA, 0x018A, false, "the bottom step up to a door (a 0.6 m ramp)");
        Placed(0xE74E, 0x020000A5, 0x010F, true, "a chair");
        Placed(0x7D64, 0x0200033C, 0x0100, true, "a 3.6 m sphere (its caps walkable faces at many heights)");

        // The landblock's own objects standing on a building's floor (GeometryLoader files them under a cell) reach the map.
        var lbObjects = g.GetStaticCollision(0x7D64).Where(s => s.Source == StaticSource.LandblockObject).ToList();
        var b7d64 = BuildingMap.Load(0x7D64, g.CellDat, g.DungeonLOS, x => g.GetDungeonStaticMeshes(x));
        var placed = b7d64.Geo!.PlacedObjects;
        int given = lbObjects.Count(s => placed.Any(p => p.Setup == s.SetupId && p.Cell == s.CellId));
        int onMap = lbObjects.Count(s => placed.Any(p => p.Setup == s.SetupId && p.Cell == s.CellId && p.OnMap));
        Check.True(lbObjects.Count > 0 && given == lbObjects.Count && onMap > 0, $"7D64: its {lbObjects.Count} landblock objects over a building's floor are given to the map ({given}), {onMap} on it");
    }

    private static void RealDungeonMap()
    {
        var g = Dats();
        if (g == null) { Check.True(true, "no dats on this PC: skipped"); return; }
        var before = DungeonPathfinder.StaticMeshSource;
        try
        {
            foreach (uint lb in new uint[] { 0x7D64, 0xE74E })
            {
                var blk = BuildingMap.Load(lb, g.CellDat, g.DungeonLOS);
                int Reach(bool statics, out string? placedNote, out string? blockedNote)
                {
                    DungeonPathfinder.StaticMeshSource = statics ? (x => g.GetDungeonStaticMeshes(x)) : null;
                    DungeonPathfinder.InvalidateCache();
                    var graph = DungeonPathfinder.GetGraph(lb, g.CellDat);
                    DungeonPathfinder.GetGeometry(lb, g.CellDat, g.DungeonLOS);
                    placedNote = DungeonPathfinder.PlacedNote;
                    blockedNote = DungeonPathfinder.BlockedNote;
                    int n = 0;
                    foreach (var b in blk.Buildings)
                    {
                        if (b.Doors.Count == 0) continue;
                        uint from = b.Doors[0].Cell;
                        foreach (uint c in b.Cells)
                            if (c == from || DungeonPathfinder.FindPath(from, c, graph).Count > 0) n++;
                    }
                    return n;
                }
                int bare = Reach(false, out _, out _);
                int with = Reach(true, out string? placedNote, out string? blockedNote);
                Check.Eq(with, bare, $"{lb:X4}: every cell a building's first door reaches without the statics, it reaches with them ({with} of {bare})");
                Check.True(placedNote != null && placedNote.Contains("stair(s) left out"), $"{lb:X4}: the stairs left off the map: {placedNote}");
                Check.False(blockedNote != null && (blockedNote.Contains("0x02000340") || blockedNote.Contains("0x02000341") || blockedNote.Contains("0x02000301") || blockedNote.Contains("0x02000303")),
                            $"{lb:X4}: no doorway closed for a stair: {blockedNote ?? "none closed"}");
                Console.WriteLine($"          {lb:X4}: {with}/{bare} cells reached; {placedNote}; blocked: {blockedNote ?? "none"}");
            }
            // A dungeon (no buildings in its landblock) is as before: every stab a wall, nothing left out.
            DungeonPathfinder.StaticMeshSource = x => g.GetDungeonStaticMeshes(x);
            DungeonPathfinder.InvalidateCache();
            DungeonPathfinder.GetGeometry(0x000A, g.CellDat, g.DungeonLOS);
            Check.True(DungeonPathfinder.PlacedNote != null && !DungeonPathfinder.PlacedNote.Contains("buildings"), $"0x000A (a dungeon): its placed objects as before: {DungeonPathfinder.PlacedNote}");
        }
        finally
        {
            DungeonPathfinder.StaticMeshSource = before;
            DungeonPathfinder.InvalidateCache();
        }
    }
}

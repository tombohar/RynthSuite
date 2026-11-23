using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Placed objects on the dungeon map (DungeonGeometry.AddPlacedObjects, 2026-11-18). Thread 0x000A: "It also
// still doesn't see world objects such as this altar it tried to run through", and "It tries to navigate over an
// opening in the floor which is impossible". The altars (1 m pedestals the hall's cells place) were nowhere on the
// map: an explore leg ran through one, the character stuck there in a fight and died, four times. The hall's
// 30 x 20 m hole goes down 24 m, deeper than the ledge pass looked (18 m), so its floorless cells were walked
// across. Now:
//   - an object's footprint in a body's band over its floor (0.45 to 1.83 m; PlacedBandTests, 2026-11-18: it was cut
//     at knee, waist and chest height) is walls, inside is no room; an arch keeps its doorway, a slab floating over a
//     deep floor adds nothing;
//   - a leg through one is walked round on the floor, on every walk laid (not only the explore's);
//   - on the real dats: no 0x000A patrol leg through an altar, the hole's rim is a ledge and walks go round it;
//     0x00C1 room 81's middle is inside its 9 m statue.
internal static class PlacedObjectTests
{
    public static void Register(Runner r)
    {
        r.Add("placed objects: an altar is a wall with no room inside, an arch keeps its doorway, a slab over a deep floor adds nothing", Synthetic);
        r.Add("placed objects: a leg through an altar is walked round on the floor", WalkRound);
        r.Add("placed objects: Thread 0x000A and 0x00C1 on the real dats - no patrol leg through an altar, the hall's hole a ledge walked round, room 81's middle inside its statue (if the dats are here)", RealDats);
    }

    private static Vector3 V(double x, double y, double z) => new((float)x, (float)y, (float)z);

    // A closed box as 12 triangles.
    internal static Vector3[] Box(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var c = new[] { V(x0, y0, z0), V(x1, y0, z0), V(x1, y1, z0), V(x0, y1, z0), V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1) };
        int[] f = { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7 };
        return f.Select(i => c[i]).ToArray();
    }

    // One 20 x 20 m room, floor at 0.
    private static DungeonGeometry Room()
    {
        const uint cell = 0x7E7E0100;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(10), NS = DungeonGeometry.WorldToNav(10), Z = 0 } };
        var floors = new List<(uint, Vector3[])> { (cell, new[] { V(0, 0, 0), V(20, 0, 0), V(20, 20, 0), V(0, 20, 0) }) };
        return DungeonGeometry.Build(graph, Array.Empty<(uint, Vector3[])>(), floors, Array.Empty<(uint, Vector3[])>());
    }

    private static int AddScene(DungeonGeometry geo) => geo.AddPlacedObjects(new (uint, uint, Vector3[])[]
    {
        (0x02000969, 0x7E7E0100, Box(9, 9.4, 0, 11, 10.6, 1.0)),                                           // an altar
        (0x02000001, 0x7E7E0100, Box(3, 14.75, 0, 3.5, 15.25, 3).Concat(Box(6, 14.75, 0, 6.5, 15.25, 3))   // an arch: two posts,
                                    .Concat(Box(3, 14.75, 2.5, 6.5, 15.25, 3)).ToArray()),                 // and a beam over them
        (0x020010E3, 0x7E7E0100, Box(14, 4, 12, 16, 6, 12.3)),                                             // a stone 12 m up
    });

    private static void Synthetic()
    {
        var geo = Room();
        Check.Near(geo.Clearance(10, 13, 0), 3.0, 0.01, "before: nothing within the 3 m looked at (the floor's edge is 7 m off)");
        int added = AddScene(geo);
        Check.Eq(added, 2, "the altar and the arch; the stone floating 12 m over the floor adds nothing");
        Check.Eq(geo.Clearance(10, 10, 0), 0.0, "in the altar's middle: no room (far from its sides, still inside)");
        Check.True(geo.InsidePlaced(10, 10, 0) && geo.InsidePlaced(9.2, 9.6, 0), "inside the altar");
        Check.Near(geo.Clearance(10, 13, 0), 2.4, 0.15, "2.4 m north of it: its north face is 2.4 m away");
        Check.Near(geo.Clearance(10, 11.6, 0), 1.0, 0.15, "1 m north of it: 1 m");
        Check.False(geo.InsidePlaced(4.75, 15, 0), "under the arch, between its posts: not inside");
        Check.True(geo.Clearance(4.75, 15, 0) >= 1.1, $"under the arch: room either side ({geo.Clearance(4.75, 15, 0):F2})");
        Check.True(geo.InsidePlaced(3.25, 15, 0) && geo.Clearance(3.25, 15, 0) == 0, "in a post: no room");
        Check.False(geo.InsidePlaced(15, 5, 0), "under the floating stone: nothing");
        Check.False(geo.InsidePlaced(10, 10, 6), "the altar's floor is 0: a floor 6 m up doesn't meet it");
    }

    private static NavPoint P(double x, double y) => new() { Type = NavPointType.Point, EW = DungeonGeometry.WorldToNav(x), NS = DungeonGeometry.WorldToNav(y), Z = 0 };

    private static void WalkRound()
    {
        var geo = Room();
        AddScene(geo);
        var pts = new List<NavPoint> { P(10, 4), P(10, 16) };
        Check.False(DungeonPathfinder.LegOnFloor(geo, pts[0], pts[1]), "the leg through the altar isn't on the floor all the way");
        int n = DungeonPathfinder.WalkRoundHoles(pts, geo);
        Check.Eq(n, 1, "walked round");
        Check.True(pts.Count >= 3, $"a turn put in ({pts.Count} points)");
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            Check.True(DungeonPathfinder.LegOnFloor(geo, pts[i], pts[i + 1]), $"leg {i} on the floor, clear of the altar");
            double least = geo.LegClearance(DungeonGeometry.NavToWorld(pts[i].EW), DungeonGeometry.NavToWorld(pts[i].NS), 0,
                                            DungeonGeometry.NavToWorld(pts[i + 1].EW), DungeonGeometry.NavToWorld(pts[i + 1].NS), 0, 1.0, 0.3, out _, out _, out _);
            Check.True(least >= 0.4, $"leg {i} keeps {least:F2} m off it");
        }
        // A leg beside the altar, clear of it, is left alone.
        var beside = new List<NavPoint> { P(13, 4), P(13, 16) };
        Check.Eq(DungeonPathfinder.WalkRoundHoles(beside, geo), 0, "a leg 2 m beside it is left as it is");
        Check.Eq(beside.Count, 2, "two points still");
    }

    private static void RealDats()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        var before = DungeonPathfinder.StaticMeshSource;
        try
        {
            DungeonPathfinder.StaticMeshSource = lb => loader.GetDungeonStaticMeshes(lb);
            const uint lb = 0x000A;
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
            Console.WriteLine($"    {DungeonPathfinder.PlacedNote}; {DungeonPathfinder.LedgeNote}");
            Check.True(geo.PlacedCount >= 6, $"the hall's altars are on the map ({geo.PlacedCount} objects)");
            Check.Eq(geo.Clearance(60, 1890, 0), 0.0, "in the altar the explore walked into (60,1890): no room");
            Check.True(geo.Clearance(60, 1892.6, 0) > 1.5, "2 m north of it: room");

            // The hole: its rim is a ledge now, and a walk across the hall goes round it on the floor.
            var path = DungeonPathfinder.FindPath((lb << 16) | 0x015D, (lb << 16) | 0x01C3, graph);
            Check.True(path.Count >= 3 && path.All(geo.HasFloor), "015D to 01C3 across the hall: every cell on the way has a floor (" + string.Join(" ", path.Select(c => $"{c & 0xFFFF:X4}")) + ")");
            foreach (uint c in new uint[] { 0x0173, 0x0174, 0x0190, 0x0191, 0x01B1, 0x01B2 })
                Check.True(graph[(lb << 16) | c].Neighbors.All(nb => !geo.HasFloor(nb)), $"hole cell {c:X4}: no walk into it from a floor");

            // The patrol: no point inside an altar, no leg through one.
            var route = DungeonPathfinder.BuildPatrolRoute(graph, (lb << 16) | 0x0164, null, geo, DungeonPatrolOptions.Default with { Coverage = PatrolCoverage.Everything });
            var pts = route.Points;
            int inside = 0, through = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                double ax = DungeonGeometry.NavToWorld(a.EW), ay = DungeonGeometry.NavToWorld(a.NS), bx = DungeonGeometry.NavToWorld(b.EW), by = DungeonGeometry.NavToWorld(b.NS);
                if (geo.InsidePlaced(ax, ay, a.Z * 240)) inside++;
                int n = Math.Max(1, (int)(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) / 0.5));
                for (int s = 1; s < n; s++)
                    if (geo.InsidePlaced(ax + (bx - ax) * s / n, ay + (by - ay) * s / n, a.Z * 240)) { through++; break; }
            }
            Check.True(pts.Count > 40, $"a patrol ({pts.Count} points)");
            Check.Eq(inside, 0, "no patrol point inside a placed object");
            Check.Eq(through, 0, "no patrol leg through one (three went through the altars before)");

            DungeonPathfinder.InvalidateCache();
            DungeonPathfinder.GetGraph(0x00C1, loader.CellDat);
            var geoC1 = DungeonPathfinder.GetGeometry(0x00C1, loader.CellDat, loader.DungeonLOS)!;
            Check.True(geoC1.InsidePlaced(150, 36996, -54) && geoC1.Clearance(150, 36996, -54) == 0, "0x00C1 room 81's middle: inside its statue");
            Check.False(geoC1.InsidePlaced(160, 37006, -66), "the stones of 0224 over the pit add nothing on the pit's floor");
        }
        finally
        {
            DungeonPathfinder.StaticMeshSource = before;
            DungeonPathfinder.InvalidateCache();
            loader.Dispose();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The loader's statics (cylinders, spheres, Resting poses: GeometryLoader.GetStaticCollision) on the body-band map
// (DungeonGeometry.AddPlacedObjects), 2026-11-18. Together they put Thread 0x0169's posts and pillars on its map, and
// its patrol is the cell walk (the room patrol finds nothing from 016A), which only pushed its points off the walls:
// 20 of its legs went through objects the map knew, every one with a way round on the floor. The cell walk now walks
// round them as the room patrol's walks do.
internal static class ObstacleMergeTests
{
    public static void Register(Runner r)
    {
        r.Add("placed objects (11-18 merge): Thread 0x0169's cell-walk patrol goes round the posts and pillars the loader puts on its map (if the dats are here)", CellWalk0169);
    }

    private static void CellWalk0169()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        var before = DungeonPathfinder.StaticMeshSource;
        var kindBefore = DungeonPathfinder.KindLog;
        try
        {
            DungeonPathfinder.StaticMeshSource = lb => loader.GetDungeonStaticMeshes(lb);
            const uint lb = 0x0169;
            var round = loader.GetStaticCollision(lb).Count(s => s.Kind is StaticCollisionKind.Cylinders or StaticCollisionKind.Spheres);
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
            Console.WriteLine($"    0x0169: {round} cylinder or sphere statics; {DungeonPathfinder.PlacedNote}");
            Check.True(round > 0, "the loader gives 0x0169 statics that are cylinders or spheres");

            var kinds = new Dictionary<NavPoint, string>();
            DungeonPathfinder.KindLog = kinds;
            var route = DungeonPathfinder.BuildPatrolRoute(graph, (lb << 16) | 0x016A, null, geo, DungeonPatrolOptions.Default with { Coverage = PatrolCoverage.Everything });
            DungeonPathfinder.KindLog = kindBefore;
            var pts = route.Points;
            Check.True(pts.Any(p => kinds.TryGetValue(p, out var k) && k == "cell-walk"), $"the patrol from 016A is the cell walk ({pts.Count} points)");

            // Every leg, the closing one too, walked on the floor it follows: nothing placed in a body's way.
            int inside = 0, through = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                double ax = DungeonGeometry.NavToWorld(a.EW), ay = DungeonGeometry.NavToWorld(a.NS), az = a.Z * 240;
                double bx = DungeonGeometry.NavToWorld(b.EW), by = DungeonGeometry.NavToWorld(b.NS), bz = b.Z * 240;
                if (geo.TryFloorHeight(ax, ay, az, 3, out double ha) && geo.InsidePlaced(ax, ay, ha)) inside++;
                int n = Math.Max(1, (int)(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) / 0.5));
                double zf = az;
                for (int s = 1; s < n; s++)
                {
                    double t = (double)s / n, x = ax + (bx - ax) * t, y = ay + (by - ay) * t;
                    if (!geo.TryFloorHeight(x, y, zf, 0.6, out double h) && !geo.TryFloorHeight(x, y, az + (bz - az) * t, 0.6, out h)) continue;
                    zf = h;
                    if (geo.InsidePlaced(x, y, h)) { through++; break; }
                }
            }
            Console.WriteLine($"    0x0169 patrol: {pts.Count} points, {inside} inside an object, {through} leg(s) through one; {DungeonPathfinder.HolesWalkedRound} walked round, {DungeonPathfinder.HolesLeft} with no way round");
            Check.Eq(inside, 0, "no patrol point inside a placed object");
            Check.Eq(through, 0, "no patrol leg through one (20 went through before the cell walk walked round them)");
        }
        finally
        {
            DungeonPathfinder.KindLog = kindBefore;
            DungeonPathfinder.StaticMeshSource = before;
            DungeonPathfinder.InvalidateCache();
            loader.Dispose();
        }
    }
}

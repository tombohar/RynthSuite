using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The explore's walk straightened (DungeonPathfinder.Straighten.cs, 2026-11-15). DreamWeave Thread
// 0x00ED: its zig-zag corridors are cut into many 3-5 cell rooms, the walk had a point at every room
// boundary (334 points, 182 of them at openings) and the character turned every 2.5-5 m. On the real
// dats: far fewer points, every leg walkable, and every room the walk was laid into still crossed.
internal static class StraightenTests
{
    public static void Register(Runner r)
    {
        r.Add("straighten: Thread 0x00ED's explore on the real dats has far fewer turns, every leg walkable, every room still crossed (if the dats are here)", Real00ED);
        r.Add("straighten: Thread 0x0278's ramp U-turn under a corridor is never cut by a leg off the floor (if the dats are here)", Real0278);
    }

    // 2026-11-17, a DreamWeave Thread in 0x0278: a ramp (cell 019E) climbs south from the room at -36 to a corridor
    // at -30 that runs back north over it. Seen from above the walk's U-turn lay on one line (x 594), and the
    // straightened walk went from the foot of the ramp straight on north, (594, 22874, -36) to (594, 22927.5, -24):
    // the character ran along the bottom into the room's end wall and stuck there. A straightened leg stays on the
    // floor (DungeonPathfinder.LegOnFloor): the only legs off it are ones the walk was laid with.
    private static void Real0278()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x0278;
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
            Check.NotNull(geo, "geometry");
            static NavPoint W(double x, double y, double z)
                => new() { Type = NavPointType.Point, NS = DungeonGeometry.WorldToNav(y), EW = DungeonGeometry.WorldToNav(x), Z = z / 240.0 };
            Check.False(DungeonPathfinder.LegOnFloor(geo, W(594, 22874, -36), W(594, 22927.5, -24)), "the leg walked in game leaves the floor");
            Check.True(DungeonPathfinder.LegOnFloor(geo, W(594, 22881, -30), W(594, 22927.5, -24)), "the corridor's own leg up to room 72 stays on it");

            string Key(NavPoint a, NavPoint b) => $"{a.NS:F6},{a.EW:F6},{a.Z:F5}>{b.NS:F6},{b.EW:F6},{b.Z:F5}";
            // Where that run started, and where the walks were laid again from after it got stuck.
            foreach (uint start in new uint[] { 0x02780369, 0x0278015E, 0x02780164 })
            {
                DungeonPathfinder.StraightenEnabled = false;
                var laid = DungeonPathfinder.BuildExploreRoute(graph, start, null, geo, null, null, null, null, out _).Points;
                DungeonPathfinder.StraightenEnabled = true;
                var pts = DungeonPathfinder.BuildExploreRoute(graph, start, null, geo, null, null, null, null, out _).Points;
                DungeonPathfinder.MainRouteNote = null;
                var laidOff = new HashSet<string>();
                for (int i = 0; i + 1 < laid.Count; i++)
                    if (!DungeonPathfinder.LegOnFloor(geo, laid[i], laid[i + 1])) laidOff.Add(Key(laid[i], laid[i + 1]));
                var newOff = new List<string>();
                for (int i = 0; i + 1 < pts.Count; i++)
                    if (!DungeonPathfinder.LegOnFloor(geo, pts[i], pts[i + 1]) && !laidOff.Contains(Key(pts[i], pts[i + 1])))
                        newOff.Add($"[{i}] ({DungeonGeometry.NavToWorld(pts[i].EW):F1},{DungeonGeometry.NavToWorld(pts[i].NS):F1},{pts[i].Z * 240:F1})"
                                 + $" > ({DungeonGeometry.NavToWorld(pts[i + 1].EW):F1},{DungeonGeometry.NavToWorld(pts[i + 1].NS):F1},{pts[i + 1].Z * 240:F1})");
                Console.WriteLine($"    from {start:X8}: {pts.Count} points straightened, {laid.Count} as laid ({laidOff.Count} legs laid off the floor: no way round)");
                Check.True(pts.Count < laid.Count, $"from {start:X8}: still straightened ({pts.Count} of {laid.Count})");
                Check.Eq(newOff.Count, 0, $"from {start:X8}: no straightened leg off the floor ({string.Join("; ", newOff.Take(4))})");
            }
        }
        finally
        {
            DungeonPathfinder.StraightenEnabled = true;
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }

    private static void Real00ED()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x00ED, start = 0x00ED0350;
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
            Check.NotNull(geo, "geometry");
            // Where the character arrives (local 180, -330), and the Thread Exit beside it (as logged 2026-11-15).
            double sx = 180.0, sy = 0xED * 192.0 - 330.0;
            var exit = new PatrolPortal { Id = 0x700ED032, Name = "Thread Exit", Cell = 0x00ED0351, X = 180.0, Y = 45160.0, Z = -0.1 };
            // As laid, for comparison: the rooms that walk crosses.
            DungeonPathfinder.StraightenEnabled = false;
            var laid = DungeonPathfinder.BuildExploreRoute(graph, start, null, geo, null, new[] { exit }, (sx, sy, 0), null, out var laidPlan);
            DungeonPathfinder.StraightenEnabled = true;
            DungeonPathfinder.MainRouteNote = null;
            var route = DungeonPathfinder.BuildExploreRoute(graph, start, null, geo, null, new[] { exit }, (sx, sy, 0), null, out var plan);
            var pts = route.Points;
            Console.WriteLine($"    {pts.Count} points (laid in game before: 334), {plan.Order.Count} rooms in order; {DungeonPathfinder.MainRouteNote}");
            // Against the same walk laid unstraightened, waits left out. (2026-11-16: the explore walks into every dead
            // end and keeps those middles, so it has more points than the 334 of 2026-11-15.)
            int moves = pts.Count(p => p.Type == NavPointType.Point), laidMoves = laid.Points.Count(p => p.Type == NavPointType.Point);
            Console.WriteLine($"    {moves} moving points straightened, {laidMoves} as laid");
            Check.True(moves > 50 && moves < laidMoves * 0.85, $"far fewer points ({moves}, {laidMoves} as laid)");

            double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
            double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);
            int bad = 0;
            for (int i = 0; i + 1 < pts.Count; i++)
                if (geo.LegClearance(X(pts[i]), Y(pts[i]), pts[i].Z * 240.0, X(pts[i + 1]), Y(pts[i + 1]), pts[i + 1].Z * 240.0, 1.0, 0.5, out _, out _, out _) < 0.2) bad++;
            Check.Eq(bad, 0, "every leg on walkable floor, off the walls");

            // Every room the walk was laid into is crossed by it (sampled every 0.5 m) - as the walk as laid did.
            HashSet<int> Crossed(List<NavPoint> pts)
            {
            var crossed = new HashSet<int>();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double x0 = X(pts[i]), y0 = Y(pts[i]), z0 = pts[i].Z * 240.0, x1 = X(pts[i + 1]), y1 = Y(pts[i + 1]), z1 = pts[i + 1].Z * 240.0;
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / 0.5));
                double zf = z0;
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps, x = x0 + (x1 - x0) * t, y = y0 + (y1 - y0) * t;
                    if (geo.TryFloorHeight(x, y, zf, 0.6, out double h)) zf = h;
                    foreach (var n in graph.Values)
                        if (geo.OnCellFloor(n.CellId, x, y, zf)) { int r = plan.RoomOf(n.CellId); if (r >= 0) crossed.Add(r); break; }
                }
            }
            return crossed;
            }
            var before = Crossed(laid.Points);
            var after = Crossed(pts);
            var laidMissed = plan.Order.Where(r => !before.Contains(r)).ToList();
            var missed = plan.Order.Where(r => !after.Contains(r) && before.Contains(r)).ToList();
            Console.WriteLine($"    as laid: {laid.Points.Count} points, rooms not crossed {laidMissed.Count} ({string.Join(",", laidMissed.Take(10))})");
            Check.Eq(missed.Count, 0, $"no room the laid walk crossed is left out ({missed.Count}: {string.Join(",", missed.Take(10))})");
        }
        finally
        {
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }
}

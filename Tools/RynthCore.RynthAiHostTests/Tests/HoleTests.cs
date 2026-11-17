using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The explore walked over a hole (DungeonPathfinder.Holes.cs, 2026-11-15). DreamWeave Thread 0x039B:
// a walkway at z 6 through two square rooms, each with a 5 x 5 m pit in the middle (down to the z -6
// rooms) and a 2 m rim; the walk went doorway to doorway over both pits. And a corridor at -6 that
// opens into a round room whose floor (-12) is its other cells' (a floorless cell at the opening):
// a 6 m ledge the ledge finder didn't see.
internal static class HoleTests
{
    public static void Register(Runner r)
    {
        r.Add("holes: Thread 0x039B on the real dats - the explore walks round the walkway's two pits, not over them; its 6 m ledge is seen (if the dats are here)", Real039B);
    }

    private static void Real039B()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x039B;
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
            Check.NotNull(geo, "geometry");
            var ledges = DungeonPathfinder.FindLedges(graph, geo);
            Check.True(ledges.Exists(l => (l.High & 0xFFFF) == 0x014F && (l.Low & 0xFFFF) == 0x0143),
                       $"the corridor's 6 m ledge into the round room is seen ({ledges.Count} ledge(s))");

            var exit = new PatrolPortal { Id = 0x7039B3EB, Name = "Thread Exit", Cell = 0x039B015E, X = 665.9, Y = 29689.7, Z = -6.1 };
            foreach (bool holes in new[] { false, true })
            {
                DungeonPathfinder.HolesEnabled = holes;
                var route = DungeonPathfinder.BuildExploreRoute(graph, 0x039B0177, null, geo, null, new[] { exit }, (606.0, 29720.0, 6.0), null, out _);
                int over = 0;
                var pts = route.Points;
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    double x0 = DungeonGeometry.NavToWorld(pts[i].EW), y0 = DungeonGeometry.NavToWorld(pts[i].NS), z0 = pts[i].Z * 240.0;
                    double x1 = DungeonGeometry.NavToWorld(pts[i + 1].EW), y1 = DungeonGeometry.NavToWorld(pts[i + 1].NS);
                    if (z0 < 4 || pts[i + 1].Z * 240.0 < 4) continue;   // legs along the walkway (z 6), not down its ramp
                    double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                    int n = Math.Max(1, (int)Math.Ceiling(len / 0.5));
                    for (int s = 1; s < n; s++)
                    {
                        double t = (double)s / n, x = x0 + (x1 - x0) * t, y = y0 + (y1 - y0) * t;
                        if (!geo.TryFloorHeight(x, y, 6.0, 1.0, out _)) { over++; break; }
                    }
                }
                Console.WriteLine($"    holes {(holes ? "on" : "off")}: {pts.Count} points, {over} walkway leg(s) over missing floor; {DungeonPathfinder.MainRouteNote}");
                DungeonPathfinder.MainRouteNote = null;
                if (holes) Check.Eq(over, 0, "no walkway leg over a pit");
                else Check.True(over >= 2, $"as laid, the walk went over both pits ({over})");
            }
        }
        finally
        {
            DungeonPathfinder.HolesEnabled = true;
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }
}

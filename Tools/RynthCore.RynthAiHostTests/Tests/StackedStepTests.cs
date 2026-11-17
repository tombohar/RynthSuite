using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Steps nav can't walk (DungeonPathfinder.Explore.cs, 2026-11-16). DreamWeave Thread 0x0278: the explore crossed a
// two-level room straight, from the doorway at its foot (z -42) to the opening on the level above (z -36), 1 m off
// on the map; nav took the upper point as reached and ran from the lower floor into the wall under the next one.
internal static class StackedStepTests
{
    public static void Register(Runner r)
    {
        r.Add("stacked steps: Thread 0x0278's walk from where that run started has no step 6 m up in 1 m (its doorway closed, the walk laid again, the rooms still walked); the stairs of 0x00B5 and 0x0185 are left as they are (if the dats are here)", RealDats);
    }

    private static int Stacked(List<NavPoint> pts, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo)
    {
        DungeonPathfinder.StackedStepOpenings(pts, graph, geo, out int steps);
        return steps;
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
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(0x0278, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(0x0278, loader.CellDat, loader.DungeonLOS)!;

            DungeonPathfinder.CloseStackedSteps = false;
            DungeonPathfinder.MainRouteNote = null;
            var old = DungeonPathfinder.BuildExploreRoute(graph, 0x02780369, null, geo, null, null, null, null, out var oldPlan);
            int oldStacked = Stacked(old.Points, graph, geo);
            DungeonPathfinder.CloseStackedSteps = true;
            DungeonPathfinder.MainRouteNote = null;
            var route = DungeonPathfinder.BuildExploreRoute(graph, 0x02780369, null, geo, null, null, null, null, out var plan);
            string note = DungeonPathfinder.MainRouteNote ?? "";
            Console.WriteLine($"    0x0278: as laid {old.Points.Count} points, {oldStacked} stacked, {oldPlan.Order.Count} rooms; now {route.Points.Count} points, {plan.Order.Count} rooms; {note}");
            Check.True(oldStacked >= 1, "as laid, the walk has the step 6 m up in 1 m");
            Check.Eq(Stacked(route.Points, graph, geo), 0, "now it has none");
            Check.True(note.Contains("stacked steps:"), "and says so: " + note);
            Check.True(plan.Order.Count >= oldPlan.Order.Count * 0.9, $"the rooms still walked ({plan.Order.Count} of {oldPlan.Order.Count})");
            var keys = DungeonPathfinder.StackedStepOpenings(old.Points, graph, geo, out _);
            Check.True(keys.Contains(DungeonPathfinder.OpeningKey(0x0278012C, 0x02780155)) && keys.Contains(DungeonPathfinder.OpeningKey(0x0278012C, 0x02780156)),
                       "the doorway closed is the one at its foot, both cell pairs (012C>0155, 012C>0156)");

            foreach (uint lb in new uint[] { 0x00B5, 0x0185 })
            {
                DungeonPathfinder.InvalidateCache();
                var g = DungeonPathfinder.GetGraph(lb, loader.CellDat);
                var gg = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
                DungeonPathfinder.MainRouteNote = null;
                var r = DungeonPathfinder.BuildExploreRoute(g, g.Keys.Min(), null, gg, null, null, null, null, out _);
                string n = DungeonPathfinder.MainRouteNote ?? "";
                Check.False(n.Contains("stacked steps:"), $"0x{lb:X4}: its stairs (3 m up over 2 m) are walkable steps: " + n);
            }
        }
        finally
        {
            DungeonPathfinder.CloseStackedSteps = true;
            DungeonPathfinder.MainRouteNote = null;
            DungeonPathfinder.StaticMeshSource = before;
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }
}

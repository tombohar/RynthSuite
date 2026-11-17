using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Stuck for good in an opening the map says is open (DungeonPathfinder.OpeningPairs, 2026-11-16). DreamWeave
// Thread 0x5456: cells 014F and 0150 have one centre at the big room's west edge, and both join 0148 through
// the one doorway at (16133, 16492). The explore, stuck in it (something in the doorway no dat has), closed
// 014F>0148, laid the walk again through 0150>0148, and was stuck in the same doorway again (three closings,
// the last an opening inside the big room). The same at 0177 (0173>0177 and 0174>0177) and 0157 (0158>0157
// and 0159>0157): each one doorway.
internal static class StuckOpeningTests
{
    public static void Register(Runner r)
    {
        r.Add("stuck opening: Thread 0x5456 on the real dats - a doorway two stacked cells open into is closed whole; the walk laid again keeps out (if the dats are here)", Real5456);
    }

    private const uint C014F = 0x5456014F, C0150 = 0x54560150, C0148 = 0x54560148, C0147 = 0x54560147,
                       C0173 = 0x54560173, C0174 = 0x54560174, C0177 = 0x54560177;

    private static void Real5456()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x5456;
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
            Check.NotNull(geo, "geometry");
            ulong K(uint a, uint b) => DungeonPathfinder.OpeningKey(a, b);

            var west = DungeonPathfinder.OpeningPairs(graph, geo, C014F, C0148);
            Check.True(west.SetEquals(new[] { K(C014F, C0148), K(C0150, C0148) }),
                       $"the west doorway is 014F>0148 and 0150>0148 ({west.Count} pair(s))");
            var south = DungeonPathfinder.OpeningPairs(graph, geo, C0173, C0177);
            Check.True(south.SetEquals(new[] { K(C0173, C0177), K(C0174, C0177) }),
                       $"the doorway into 0177 is 0173>0177 and 0174>0177, 3 m over it ({south.Count} pair(s))");
            Check.Eq(DungeonPathfinder.OpeningPairs(graph, geo, C0148, C0147).Count, 1, "the next doorway (0148>0147, 2 m on) is one pair");
            var east = DungeonPathfinder.OpeningPairs(graph, geo, 0x54560158, 0x54560157);
            Check.True(east.SetEquals(new[] { K(0x54560158, 0x54560157), K(0x54560159, 0x54560157) }),
                       $"the doorway into 0157 is 0158>0157 and 0159>0157 (the third doorway it was stuck in: {east.Count} pair(s))");

            // A web where the character stood stuck (16148.0, 16506.4): that doorway, both pairs; the big room's middle: none.
            var web = DungeonPathfinder.DoorwayAt(graph, geo, 16148.0, 16506.6, -24.0, 1.5, out var webDoor);
            Check.True(web.SetEquals(east), $"a web at (16148, 16506.6) stands in the doorway into 0157 ({web.Count} pair(s), {webDoor?.CellA:X8}>{webDoor?.CellB:X8})");
            Check.Eq(DungeonPathfinder.DoorwayAt(graph, geo, 16150.0, 16470.0, -24.0, 1.5, out _).Count, 0, "one in the big room's middle stands in no doorway");

            // Laid again from 014F as it was (014F>0148 closed) and as it is now (the doorway's pairs closed).
            var (x, y) = (16137.0, 16492.0);
            foreach (bool whole in new[] { false, true })
            {
                var closed = whole ? west : new HashSet<ulong> { K(C014F, C0148) };
                var g = DungeonPathfinder.WithoutOpenings(graph, closed, C014F);
                var route = DungeonPathfinder.BuildExploreRoute(g, C014F, null, geo, null, null, (x, y, -24.0), null, out _);
                int behind = route.Points.Count(p => DungeonPathfinder.CellAt(graph, geo, p) is C0148 or C0147);
                Console.WriteLine($"    {(whole ? "whole doorway" : "014F>0148 only")} closed: {route.Points.Count} points, {behind} behind the doorway");
                DungeonPathfinder.MainRouteNote = null;
                if (whole) Check.Eq(behind, 0, "the doorway closed whole: the walk keeps out of the rooms behind it");
                else Check.True(behind > 0, $"as it was, the walk went back through the same doorway ({behind})");
            }
        }
        finally
        {
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }
}

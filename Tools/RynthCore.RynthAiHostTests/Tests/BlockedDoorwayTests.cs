using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Doorways a dungeon's placed objects stand across (DungeonPathfinder.Blocked.cs, 2026-11-16). DreamWeave
// Thread 0x5456 places twelve spider webs (model 0x02000D34) in its doorways; the map had them open and the
// explore walked into one after another. The map now closes them; dungeons without such things lose nothing.
internal static class BlockedDoorwayTests
{
    public static void Register(Runner r)
    {
        r.Add("blocked doorways: Thread 0x5456's spider webs close their doorways (every cell pair) on the real dats; the explore keeps out of them; other dungeons lose none, 0x039B its round room's floor-level way (if the dats are here)", RealDats);
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
            ulong K(uint a, uint b) => DungeonPathfinder.OpeningKey(a, b);

            // 0x5456: the three doorways the character was stuck in (both cell pairs of each) are among them, all webs.
            DungeonPathfinder.InvalidateCache();
            var raw = DungeonPathfinder.GetGraph(0x5456, loader.CellDat);
            var copy = raw.ToDictionary(kv => kv.Key, kv => new DungeonNavNode { CellId = kv.Value.CellId, NS = kv.Value.NS, EW = kv.Value.EW, Z = kv.Value.Z, Neighbors = { } });
            foreach (var (id, n) in raw) copy[id].Neighbors.AddRange(n.Neighbors);
            var geo = DungeonPathfinder.GetGeometry(0x5456, loader.CellDat, loader.DungeonLOS)!;
            var blocked = DungeonPathfinder.FindBlockedDoorways(copy, geo, loader.GetDungeonStaticMeshes(0x5456));
            var keys = blocked.Select(x => K(x.A, x.B)).ToHashSet();
            Console.WriteLine($"    0x5456: {blocked.Count} pair(s): {string.Join(" ", blocked.Select(x => $"{x.A & 0xFFFF:X4}>{x.B & 0xFFFF:X4}"))}; {DungeonPathfinder.BlockedNote}");
            foreach (var (a, b) in new[] { (0x5456014Fu, 0x54560148u), (0x54560150u, 0x54560148u), (0x54560158u, 0x54560157u),
                                           (0x54560159u, 0x54560157u), (0x54560173u, 0x54560177u), (0x54560174u, 0x54560177u) })
                Check.True(keys.Contains(K(a, b)), $"{a:X8}>{b:X8} (a doorway it was stuck in) is closed");
            Check.True(blocked.All(x => x.Setups.All(m => m is 0x02000D34 or 0x02000CCE)),
                       "by the web model 0x02000D34, or 0x02000CCE (five 3.8 m blocks filling the way down to the one-cell room 0100)");
            int all = DungeonPathfinder.ReachableCells(copy, 0x545602A0, null).Count, left = DungeonPathfinder.ReachableCells(raw, 0x545602A0, null).Count;
            Console.WriteLine($"    0x5456 from 02A0: {all} cells reachable as the map was, {left} with the webs closed");
            Check.True(DungeonPathfinder.BlockedNote is { } note && note.Contains("0x02000D34"), "the build log names the model");
            Check.False(raw[0x5456014F].Neighbors.Contains(0x54560148) || raw[0x54560150].Neighbors.Contains(0x54560148), "the cached graph has the web doorway closed");

            // The explore laid from where the run started: no step through a closed doorway.
            var route = DungeonPathfinder.BuildExploreRoute(raw, 0x545602A0, null, geo, null, null, (16239.0, 16492.0, 0.0), null, out _);
            int through = 0;
            for (int i = 0; i + 1 < route.Points.Count; i++)
            {
                uint c0 = DungeonPathfinder.CellAt(raw, geo, route.Points[i]), c1 = DungeonPathfinder.CellAt(raw, geo, route.Points[i + 1]);
                if (c0 != 0 && c1 != 0 && keys.Contains(K(c0, c1))) through++;
            }
            Console.WriteLine($"    explore from 02A0: {route.Points.Count} points, {through} step(s) through a web");
            DungeonPathfinder.MainRouteNote = null;
            Check.Eq(through, 0, "the explore walk takes no step through a web's doorway");

            // Dungeons the explore has walked: nothing closed. 0x039B aside: a 9.2 m model 6.2 m tall fills its round
            // room from the floor (-12) to the corridor (-6), and the one doorway into it at floor level meets its side.
            foreach (uint lb in new uint[] { 0x0289, 0x0192, 0x6346, 0x0199, 0x01F5, 0x039B })
            {
                DungeonPathfinder.InvalidateCache();
                var g = DungeonPathfinder.GetGraph(lb, loader.CellDat);
                var before39 = lb == 0x039B ? DungeonPathfinder.ReachableCells(g, 0x039B0177, null) : new HashSet<uint>();
                int reachBefore = before39.Count;
                var gg = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
                if (gg == null) continue;
                Console.WriteLine($"    0x{lb:X4}: {(DungeonPathfinder.BlockedNote ?? "nothing closed")}");
                if (lb == 0x039B)
                {
                    Check.True(DungeonPathfinder.BlockedNote is { } n39 && n39.Contains("0x010000EE") && !n39.Contains(", 0x"), "0x039B: only by the round room's 9 m model");
                    var full = DungeonPathfinder.GetGraph(lb, loader.CellDat);
                    var after39 = DungeonPathfinder.ReachableCells(full, 0x039B0177, null);
                    int reach = after39.Count;
                    Console.WriteLine($"    0x039B from 0177: {reachBefore} cells reachable as the map was, {reach} with it closed");
                    // The cells under the 9 m model's floor-level side (012C-012F at -12, 010A-010F at -18 past them).
                    Check.True(after39.Contains(0x039B0141) && !after39.Contains(0x039B012E) && reachBefore - reach == 11,
                               $"0x039B: the round room stays, the 11 cells only past the model's side are left out ({reachBefore - reach})");
                }
                else Check.Eq(DungeonPathfinder.BlockedNote, null, $"0x{lb:X4}: nothing closed");
            }
        }
        finally
        {
            DungeonPathfinder.StaticMeshSource = before;
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }
}

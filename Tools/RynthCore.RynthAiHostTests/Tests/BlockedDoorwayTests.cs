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
        r.Add("blocked doorways: Thread 0x5456's spider webs close their doorways (every cell pair) on the real dats; the explore keeps out of them; other dungeons lose none, nor do 0x039B's spiral stair, 0x0150's door frames and 0x000A's bridge shut theirs (if the dats are here)", RealDats);
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

            // Dungeons the explore has walked: nothing closed. 0x039B, 0x0150 and 0x000A too since 2026-11-18 (the lanes are
            // a body's walk on the solid models, DungeonPathfinder.Blocked.cs): the old rays counted one that went into a
            // model's box and met none of its polygons (nor does the loader hand that box over now: its pieces are
            // NoAabbFallback, Raycasting/GeometryLoader.cs), and so shut
            //   - 0x039B's floor-level doorway 012E>0142 into its round room by the 9.2 m spiral stair 0x010000EE: the
            //     doorway opens onto the stair's foot (under the body band), nothing of the stair in a body's way within
            //     1.2 m of it, and the 11 cells past it (012C-012F at -12, 010A-010F at -18) were left out;
            //   - 0x0150's doorways 0116>0126 and 0126>012A by door frames 0x01000E60 (two posts and a lintel 2.6 m up),
            //     leaving 43 of 155 cells reachable from 0100;
            //   - 0x000A's four doorways onto the bridge 0x02000224 over the hall (its deck at the doorways' floor, a
            //     body's height clear between its rails).
            foreach (uint lb in new uint[] { 0x0289, 0x0192, 0x6346, 0x0199, 0x01F5, 0x039B, 0x0150, 0x000A })
            {
                DungeonPathfinder.InvalidateCache();
                var g = DungeonPathfinder.GetGraph(lb, loader.CellDat);
                int reachBefore = lb == 0x039B ? DungeonPathfinder.ReachableCells(g, 0x039B0177, null).Count : 0;
                var gg = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
                if (gg == null) continue;
                Console.WriteLine($"    0x{lb:X4}: {(DungeonPathfinder.BlockedNote ?? "nothing closed")}");
                Check.Eq(DungeonPathfinder.BlockedNote, null, $"0x{lb:X4}: nothing closed");
                if (lb == 0x039B)
                {
                    var full = DungeonPathfinder.GetGraph(lb, loader.CellDat);
                    var after39 = DungeonPathfinder.ReachableCells(full, 0x039B0177, null);
                    int reach = after39.Count;
                    Console.WriteLine($"    0x039B from 0177: {reachBefore} cells reachable as the map was, {reach} now");
                    Check.True(after39.Contains(0x039B0141) && after39.Contains(0x039B012E) && reach == reachBefore,
                               $"0x039B: the round room and the cells past its floor-level doorway are reachable ({reachBefore - reach} left out)");
                }
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

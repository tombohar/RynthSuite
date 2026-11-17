using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;
using V3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;

namespace RynthCore.RynthAiHostTests.Tests;

// Ledges (DungeonPathfinder.Ledges.cs, 2026-11-15). DreamWeave Thread 0x0192: the explore walked over
// a 6 m ledge (019201C4, floor 0 -> 019201C6, floor -6), which AC won't walk a character off (it slides
// along the edge) and nobody walks up; it snagged there every time. Now an opening whose floors are more
// than LedgeMeters apart is closed in the dungeon graph (walked round), unless it is the only way on.
//   - a high room and a low room joined by a ledge and by stairs round: the ledge is found (the stairs
//     aren't one) and closed, and the way from one to the other goes by the stairs;
//   - the same with no stairs: the ledge is the only way and stays open;
//   - 0x0192 on the real dats: its twelve ledges are closed, every cell still reached, and the explore's
//     walk never steps across one.
internal static class LedgeTests
{
    public static void Register(Runner r)
    {
        r.Add("ledges: a ledge with stairs round is found (the stairs aren't one) and closed; the way goes by the stairs", LedgeWithStairs);
        r.Add("ledges: a ledge that is the only way on stays open", LedgeOnlyWay);
        r.Add("ledges: Thread 0x0192 on the real dats: 12 ledges closed, every cell reached, the explore never steps over one (if the dats are here)", Real0192);
    }

    private sealed class Dungeon
    {
        public readonly Dictionary<uint, DungeonNavNode> Graph = new();
        public readonly List<(uint, V3[])> Portals = new();
        public readonly List<(uint, V3[])> Floors = new();
        public readonly List<(uint, V3[])> Walls = new();
        private uint _next = 0x7E7E0100;

        public uint Cell(double x0, double y0, double x1, double y1, double z = 0)
        {
            uint id = _next++;
            Graph[id] = new DungeonNavNode { CellId = id, EW = DungeonGeometry.WorldToNav((x0 + x1) / 2), NS = DungeonGeometry.WorldToNav((y0 + y1) / 2), Z = (float)z };
            Floors.Add((id, new[] { new V3((float)x0, (float)y0, (float)z), new V3((float)x1, (float)y0, (float)z), new V3((float)x1, (float)y1, (float)z), new V3((float)x0, (float)y1, (float)z) }));
            return id;
        }

        // An opening from floor z up 3 m (the high side's floor for a ledge).
        public void OpenX(uint a, uint b, double x, double y0, double y1, double z = 0) => Open(a, b, Quad(x, y0, x, y1, z));
        public void OpenY(uint a, uint b, double y, double x0, double x1, double z = 0) => Open(a, b, Quad(x0, y, x1, y, z));

        private void Open(uint a, uint b, V3[] quad)
        {
            Graph[a].Neighbors.Add(b);
            Graph[b].Neighbors.Add(a);
            Portals.Add((a, quad));
            Portals.Add((b, quad.Reverse().ToArray()));
        }

        private static V3[] Quad(double x0, double y0, double x1, double y1, double z) => new[]
        {
            new V3((float)x0, (float)y0, (float)z), new V3((float)x1, (float)y1, (float)z),
            new V3((float)x1, (float)y1, (float)z + 3f), new V3((float)x0, (float)y0, (float)z + 3f),
        };

        public DungeonGeometry Geo() => DungeonGeometry.Build(Graph, Portals, Floors, Walls);
    }

    // A high room (floor 0) south of a low room (floor -6), a ledge between them; with stairs, a way
    // round east: six 1 m steps down, then a corridor back west into the low room.
    private static (Dungeon D, uint High, uint Low) Build(bool stairs)
    {
        var d = new Dungeon();
        uint high = d.Cell(0, 0, 10, 10, 0);
        uint low = d.Cell(0, 10, 10, 20, -6);
        d.OpenY(high, low, 10, 3, 7, 0);
        if (stairs)
        {
            uint prev = high;
            double x = 10;
            for (int i = 1; i <= 6; i++)
            {
                uint step = d.Cell(x, 2, x + 3, 8, -i);
                d.OpenX(prev, step, x, 2, 8, -i + 1);
                prev = step;
                x += 3;
            }
            uint up = d.Cell(x - 3, 8, x, 20, -6);
            d.OpenY(prev, up, 8, x - 3, x, -6);
            uint corr = d.Cell(10, 12, x - 3, 18, -6);
            d.OpenX(corr, up, x - 3, 12, 18, -6);
            d.OpenX(low, corr, 10, 12, 18, -6);
        }
        return (d, high, low);
    }

    private static void LedgeWithStairs()
    {
        var (d, high, low) = Build(stairs: true);
        var geo = d.Geo();
        var ledges = DungeonPathfinder.FindLedges(d.Graph, geo);
        Check.Eq(ledges.Count, 1, "one ledge (the 1 m stairs are not ledges)");
        Check.True(ledges[0].High == high && ledges[0].Low == low, $"high {ledges[0].High:X8} -> low {ledges[0].Low:X8}");
        Check.Near(ledges[0].Step, 6.0, 0.01, "6 m");
        Check.True(DungeonPathfinder.FindPath(high, low, d.Graph).Count == 2, "before: straight over the ledge");

        var kept = DungeonPathfinder.CloseLedges(d.Graph, ledges, out int closed);
        Check.Eq(closed, 1, "closed");
        Check.Eq(kept.Count, 0, "none kept open");
        Check.False(d.Graph[high].Neighbors.Contains(low) || d.Graph[low].Neighbors.Contains(high), "the ledge is gone both ways");
        var path = DungeonPathfinder.FindPath(high, low, d.Graph);
        Check.True(path.Count > 8, $"after: down the stairs and round ({path.Count} cells)");
    }

    private static void LedgeOnlyWay()
    {
        var (d, high, low) = Build(stairs: false);
        var geo = d.Geo();
        var ledges = DungeonPathfinder.FindLedges(d.Graph, geo);
        Check.Eq(ledges.Count, 1, "the ledge");
        var kept = DungeonPathfinder.CloseLedges(d.Graph, ledges, out int closed);
        Check.Eq(closed, 0, "not closed");
        Check.Eq(kept.Count, 1, "kept: the only way on");
        Check.True(d.Graph[high].Neighbors.Contains(low) && d.Graph[low].Neighbors.Contains(high), "still linked both ways");
    }

    private static void Real0192()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x0192, start = 0x019201A8;
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            int linksBefore = graph.Values.Sum(n => n.Neighbors.Count);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
            Check.NotNull(geo, "geometry");
            Console.WriteLine("    " + DungeonPathfinder.LedgeNote);
            Check.True(DungeonPathfinder.LedgeNoteLandblock == lb && DungeonPathfinder.LedgeNote != null
                       && DungeonPathfinder.LedgeNote.StartsWith("12 ledge(s)") && DungeonPathfinder.LedgeNote.Contains("12 closed"), "12 ledges, 12 closed");
            Check.False(graph[0x019201C4].Neighbors.Contains(0x019201C6), "019201C4 -> 019201C6 (the report's) closed");
            Check.True(graph.Values.Sum(n => n.Neighbors.Count) < linksBefore, "links gone from the cached graph");
            Check.True(ReferenceEquals(graph, DungeonPathfinder.GetGraph(lb, loader.CellDat)), "the cached graph itself");

            // Every cell walkable before is still reached.
            var seen = new HashSet<uint> { start };
            var q = new Queue<uint>(); q.Enqueue(start);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                foreach (var nb in graph[c].Neighbors)
                    if (graph.TryGetValue(nb, out var o) && !DungeonPathfinder.IsDropEdge(graph[c], o) && seen.Add(nb)) q.Enqueue(nb);
            }
            Check.Eq(seen.Count, 194, "all 194 cells still reached from the start");

            // The explore's walk never steps from one side of a ledge to the other.
            var ledgeCells = new HashSet<(uint, uint)>
            {
                (0x01920133, 0x01920135), (0x01920133, 0x01920136), (0x0192013B, 0x0192013D), (0x0192013B, 0x0192013E),
                (0x01920143, 0x01920145), (0x01920143, 0x01920146), (0x019201C4, 0x019201C6), (0x019201C4, 0x019201C7),
                (0x019201CA, 0x019201CC), (0x019201CA, 0x019201CD), (0x019201D0, 0x019201D2), (0x019201D0, 0x019201D3),
            };
            var n0 = graph[start];
            var route = DungeonPathfinder.BuildExploreRoute(graph, start, null, geo, null, Array.Empty<PatrolPortal>(),
                (DungeonGeometry.NavToWorld(n0.EW), DungeonGeometry.NavToWorld(n0.NS), n0.Z), null, out var plan);
            Check.True(route.Points.Count > 50, $"an explore ({route.Points.Count} points)");
            int over = 0;
            for (int i = 0; i + 1 < route.Points.Count; i++)
            {
                uint a = DungeonPathfinder.CellAt(graph, geo, route.Points[i]), b = DungeonPathfinder.CellAt(graph, geo, route.Points[i + 1]);
                if (ledgeCells.Contains((a, b)) || ledgeCells.Contains((b, a))) over++;
            }
            Check.Eq(over, 0, "no leg steps over a ledge");
        }
        finally
        {
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }
}

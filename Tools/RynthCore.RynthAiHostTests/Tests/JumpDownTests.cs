using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using V3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;

namespace RynthCore.RynthAiHostTests.Tests;

// Jump-downs (DungeonPathfinder.Jumps.cs, 2026-11-17). DreamWeave Thread 0x0169: the last three monsters
// were in a cave 9-12 m under the top floor; the one way down is a 20 m shaft in a hall (four open-air
// cells, a floor portal at 0 to the cave). The map read the floor portals as floor (an open-air cell has
// no physics polygons, so its render ones were used, portals and all), saw no drop, and had no walk to
// the cave; the character was left there: "It required a jump".
//   - a high room, an open-air cell beyond an opening and a low room under it: the opening is a ledge into
//     air (closed even though it is the only way: nothing beyond it to walk), a jump-down from the high room
//     onto the low one, and the path and walk with jumps take it (a Jump point at the edge, facing over it);
//   - /ra dunnav-to <id> jump walks there with the jump; without "jump" it says there's no walk;
//   - 0x0169 on the real dats: the shaft's cells have no floor, its eight openings are ledges, the top
//     floor's 96 cells are walked, and the way from 016A (where it stopped) to 0103 (the monsters) is one jump.
internal static class JumpDownTests
{
    public static void Register(Runner r)
    {
        r.Add("jump-downs: an opening onto air over a low room is a ledge (closed: only air beyond), a jump onto the room, and the walk with jumps takes it", Synthetic);
        r.Add("jump-downs: no depth limit: a 40 m drop under the opening is a jump too (2026-11-18: \"No limit on jump down\")", Deep);
        r.Add("jump-downs: /ra dunnav-to <id> jump jumps down to a monster with no walk; without jump, no walk", DunNavToJump);
        r.Add("jump-downs: Thread 0x0169 on the real dats: the shaft is air, its eight openings ledges, 016A to 0103 one jump (if the dats are here)", Real0169);
    }

    private sealed class Dungeon
    {
        public readonly Dictionary<uint, DungeonNavNode> Graph = new();
        public readonly List<(uint, V3[])> Portals = new(), Floors = new(), Walls = new();
        private uint _next;
        private readonly double _ox, _oy;
        public Dungeon(uint first, double ox, double oy) { _next = first; _ox = ox; _oy = oy; }

        // A cell over (x0,y0)-(x1,y1) at height z; with a floor there, or none (air).
        public uint Cell(double x0, double y0, double x1, double y1, double z, bool floor = true)
        {
            uint id = _next++;
            Graph[id] = new DungeonNavNode { CellId = id, EW = DungeonGeometry.WorldToNav(_ox + (x0 + x1) / 2), NS = DungeonGeometry.WorldToNav(_oy + (y0 + y1) / 2), Z = (float)z };
            if (floor)
                Floors.Add((id, new[] { P(x0, y0, z), P(x1, y0, z), P(x1, y1, z), P(x0, y1, z) }));
            return id;
        }

        private V3 P(double x, double y, double z) => new((float)(_ox + x), (float)(_oy + y), (float)z);

        // An upright opening at x from y0 to y1, from z up 3 m.
        public void OpenX(uint a, uint b, double x, double y0, double y1, double z)
        {
            var quad = new[] { P(x, y0, z), P(x, y1, z), P(x, y1, z + 3), P(x, y0, z + 3) };
            Graph[a].Neighbors.Add(b);
            Graph[b].Neighbors.Add(a);
            Portals.Add((a, quad));
            Portals.Add((b, quad.Reverse().ToArray()));
        }

        // A floor portal (cells stacked): a graph link only, a drop edge.
        public void Stack(uint upper, uint lower)
        {
            Graph[upper].Neighbors.Add(lower);
            Graph[lower].Neighbors.Add(upper);
        }

        public DungeonGeometry Geo() => DungeonGeometry.Build(Graph, Portals, Floors, Walls);
    }

    // high (0..10, 0..10) floor 0 | opening at x 10 (y 3..7) | air (10..20, 0..10) no floor, over low (10..20, 0..10) floor -10.
    private static (Dungeon D, uint High, uint Air, uint Low) Build(uint first = 0x7E7E0100, double ox = 0, double oy = 0, double lowZ = -10)
    {
        var d = new Dungeon(first, ox, oy);
        uint high = d.Cell(0, 0, 10, 10, 0);
        uint air = d.Cell(10, 0, 20, 10, 0, floor: false);
        uint low = d.Cell(10, 0, 20, 10, lowZ);
        d.OpenX(high, air, 10, 3, 7, 0);
        d.Stack(air, low);
        return (d, high, air, low);
    }

    private static void Synthetic()
    {
        var (d, high, air, low) = Build();
        var geo = d.Geo();
        Check.False(geo.HasFloor(air), "the air cell has no floor");
        var ledges = DungeonPathfinder.FindLedges(d.Graph, geo);
        Check.Eq(ledges.Count, 1, "one ledge");
        Check.True(ledges[0].High == high && ledges[0].Low == air, $"high -> air ({ledges[0].High:X8} -> {ledges[0].Low:X8})");
        Check.Near(ledges[0].Step, 10.0, 0.01, "10 m down");

        // The only way into the air, but only air beyond it: closed (without the floor test, kept as before).
        var copy = d.Graph.ToDictionary(kv => kv.Key, kv => new DungeonNavNode { CellId = kv.Value.CellId, NS = kv.Value.NS, EW = kv.Value.EW, Z = kv.Value.Z, Neighbors = kv.Value.Neighbors.ToList() });
        var keptOld = DungeonPathfinder.CloseLedges(copy, ledges, out int closedOld);
        Check.True(keptOld.Count == 1 && closedOld == 0, "without the floor test: kept, the only way on");
        var jumps = DungeonPathfinder.FindJumps(d.Graph, geo);   // looked for before the graph loses the ledge: it needs no graph link
        var kept = DungeonPathfinder.CloseLedges(d.Graph, ledges, out int closed, geo.HasFloor);
        Check.True(kept.Count == 0 && closed == 1, "with it: closed (nothing but air beyond)");
        Check.Eq(DungeonPathfinder.FindPath(high, low, d.Graph).Count, 0, "no walk from the high room to the low one");

        Check.Eq(jumps.Count, 1, "one jump-down");
        var j = jumps[0];
        Check.True(j.From == high && j.Land == low, $"from the high room onto the low one ({j.From:X8} -> {j.Land:X8})");
        Check.Near(j.Drop, 10.0, 0.01, "10 m");
        Check.Near(DungeonPathfinder.JumpHeadingOf(j.Nx, j.Ny), 90.0, 0.5, "facing east, over the edge");
        Check.Eq(DungeonPathfinder.FindJumps(d.Graph, geo).Count, 1, "found from the geometry still, the ledge closed");

        var path = DungeonPathfinder.FindPathWithJumps(high, low, d.Graph, jumps);
        Check.True(path.Count == 2 && path[0] == (high, null) && path[1].Cell == low && path[1].Jump == j, "the path: the high room, then a jump onto the low one");
        Check.Eq(DungeonPathfinder.FindPathWithJumps(low, high, d.Graph, jumps).Count, 0, "no way back up");

        var start = d.Graph[high];
        double destNs = DungeonGeometry.WorldToNav(5), destEw = DungeonGeometry.WorldToNav(17);
        var pts = DungeonPathfinder.BuildJumpPathPoints(path, d.Graph, geo, null, start.NS, start.EW, 0, destNs, destEw, -10, out int n);
        Check.Eq(n, 1, "one jump");
        int at = pts.FindIndex(p => p.Type == NavPointType.Jump);
        Check.True(at > 0, "a Jump point, after the walk up to it");
        var jp = pts[at];
        double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
        double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);
        Check.True(Math.Abs(X(jp) - 9.9) < 0.05 && Math.Abs(Y(jp) - 5) < 0.05, $"at the edge ({X(jp):F2}, {Y(jp):F2})");
        Check.True(jp.Doorway && jp.JumpMs == DungeonPathfinder.JumpHoldMs && !jp.JumpShift && Math.Abs(jp.JumpHeading - 90) < 0.5, "a short hop east, the tight reach");
        Check.True(Math.Abs(X(pts[at - 1]) - 8.5) < 0.05 && Math.Abs(Y(pts[at - 1]) - 5) < 0.05, "lined up 1.5 m back, square to the edge");
        Check.True(pts.Skip(at + 1).All(p => p.Z * 240 < -9), "after it, the low room's floor");
    }

    private const uint Rat = 0x7E7E9201;

    // It was 15 m at most, and the floor under the far side was looked for only 18 m down.
    private static void Deep()
    {
        foreach (double lowZ in new[] { -16.0, -25.0, -40.0 })
        {
            var (d, high, _, low) = Build(lowZ: lowZ);
            var geo = d.Geo();
            var jumps = DungeonPathfinder.FindJumps(d.Graph, geo);
            Check.True(jumps.Count == 1 && jumps[0].From == high && jumps[0].Land == low && Math.Abs(jumps[0].Drop + lowZ) < 0.01,
                       $"{-lowZ} m down: one jump, high onto low ({string.Join(", ", jumps.Select(j => $"{j.From:X8}->{j.Land:X8} {j.Drop:F1} m"))})");
            var path = DungeonPathfinder.FindPathWithJumps(high, low, d.Graph, jumps);
            Check.True(path.Count >= 2 && path[^1].Cell == low && path.Count(p => p.Jump != null) == 1, $"{-lowZ} m down: the walk with jumps takes it");
            // The ledge pass sees it too, however deep (it looked 18 m down: Thread 0x000A's 24 m hole wasn't one).
            var ledges = DungeonPathfinder.FindLedges(d.Graph, geo);
            Check.True(ledges.Count == 1 && Math.Abs(ledges[0].Step + lowZ) < 0.01, $"{-lowZ} m down: a ledge ({string.Join(", ", ledges.Select(l => $"{l.Step:F1} m"))})");
        }
    }

    private static void DunNavToJump()
    {
        var rig = new DunRouteTests.Rig("NavProfiles-dunnav-jump");
        var (d, high, _, low) = Build((DunRouteTests.Lb << 16) | 0x0300, DunRouteTests.Ox, DunRouteTests.Oy);
        var geo = d.Geo();
        rig.P.DungeonMapOverride = lb => lb == DunRouteTests.Lb ? (d.Graph, geo) : null;
        rig.At(5, 5, high);
        FakeHost.Names[Rat] = "Threadbound Rat";
        FakeHost.Positions[Rat] = (low, 16f, 5f, -10f);
        rig.S.IsMacroRunning = false;

        var said = rig.Typed("/ra dunnav-to 0x7E7E9201");
        Check.True(said.Count == 1 && said[0].Contains("DunNav-To: no walk") && !said[0].Contains("walking there"), "no jump asked: no walk: " + string.Join(" | ", said));
        Check.False(rig.S.IsMacroRunning, "nothing started");

        said = rig.Typed("/ra dunnav-to 0x7E7E9201 jump");
        Check.True(said.Count == 1 && said[0].Contains("1 jump down") && said[0].Contains("walking there"), "with jump: walking there, one jump: " + string.Join(" | ", said));
        var pts = rig.S.CurrentRoute.Points;
        Check.Eq(pts.Count(p => p.Type == NavPointType.Jump), 1, "a Jump point in the walk");
        Check.True(rig.S.IsMacroRunning && rig.S.EnableNavigation && rig.S.CurrentRoute.RouteType == NavRouteType.Once, "macro and navigation on, a Once walk");
        var last = pts[^1];
        Check.True(Math.Abs(DungeonGeometry.NavToWorld(last.EW) - DunRouteTests.Ox - 16) < 0.5 && Math.Abs(DungeonGeometry.NavToWorld(last.NS) - DunRouteTests.Oy - 5) < 0.5,
                   "it ends at the rat");
    }

    private static void Real0169()
    {
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return; }
        try
        {
            const uint lb = 0x0169, start = 0x0169016A, goal = 0x01690103;
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
            Check.NotNull(geo, "geometry");
            Console.WriteLine("    " + DungeonPathfinder.LedgeNote);
            foreach (uint c in new uint[] { 0x01690159, 0x0169015A, 0x0169015F, 0x01690160 })
                Check.False(geo!.HasFloor(c), $"shaft cell {c:X8}: no floor (its floor portal isn't one)");
            Check.True(geo!.HasFloor(0x01690152) && geo.HasFloor(0x01690121), "the hall beside it and the cave under it have floors");
            Check.True(DungeonPathfinder.LedgeNote != null && DungeonPathfinder.LedgeNote.StartsWith("16 ledge(s)") && DungeonPathfinder.LedgeNote.Contains("16 closed"),
                       "16 ledges (the shaft's eight openings on two levels), all closed");

            // The top floor walked: 96 cells (the shaft's four air cells off it), the cave not.
            var seen = new HashSet<uint> { start };
            var q = new Queue<uint>(); q.Enqueue(start);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                foreach (var nb in graph[c].Neighbors)
                    if (graph.TryGetValue(nb, out var o) && !DungeonPathfinder.IsDropEdge(graph[c], o) && seen.Add(nb)) q.Enqueue(nb);
            }
            Check.Eq(seen.Count, 96, "96 cells walked from 016A");
            Check.False(seen.Contains(0x01690159) || seen.Contains(goal), "neither the shaft nor the cave");

            var jumps = DungeonPathfinder.FindJumps(graph, geo);
            Check.Eq(jumps.Count, 8, "eight jump-downs into the shaft, one at each opening");
            Check.True(jumps.All(j => j.Drop > 9 && j.Drop < 12.5 && (j.Land & 0xFFFF) < 0x0146), "each 9-12.5 m, onto the cave");
            var path = DungeonPathfinder.FindPathWithJumps(start, goal, graph, jumps);
            Check.True(path.Count > 0 && path.Count(p => p.Jump != null) == 1, $"016A to 0103: one jump ({path.Count} cells)");
            var n0 = graph[start];
            var pts = DungeonPathfinder.BuildJumpPathPoints(path, graph, geo, null, n0.NS, n0.EW, 0, graph[goal].NS, graph[goal].EW, -12, out int nj);
            var jp = pts.Single(p => p.Type == NavPointType.Jump);
            double jx = DungeonGeometry.NavToWorld(jp.EW), jy = DungeonGeometry.NavToWorld(jp.NS);
            Console.WriteLine($"    jump at ({jx:F1}, {jy:F1}) heading {jp.JumpHeading:F0}, {pts.Count} points");
            Check.True(jx > 206 && jx < 228 && jy > 19984 && jy < 20006, "at the shaft's edge");
            bool intoShaft = Math.Abs(jx - 227) < 0.5 ? Math.Abs(jp.JumpHeading - 270) < 1 : Math.Abs(jx - 207) < 0.5 ? Math.Abs(jp.JumpHeading - 90) < 1
                           : Math.Abs(jy - 20005) < 0.5 ? Math.Abs(jp.JumpHeading - 180) < 1 : Math.Abs(jp.JumpHeading) < 1 || Math.Abs(jp.JumpHeading - 360) < 1;
            Check.True(intoShaft, "facing into the shaft");
        }
        finally
        {
            loader.Dispose();
            DungeonPathfinder.InvalidateCache();
        }
    }
}

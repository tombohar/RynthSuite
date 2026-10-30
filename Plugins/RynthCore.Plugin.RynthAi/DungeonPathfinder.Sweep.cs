// DungeonPathfinder, sweep half: Settings > Dungeon Patrol > Coverage "Sweep" (2026-10-14).
//
// Why: Whole dungeon walks every doorway between rooms once. A pillar hall (0x00AE: a grid of
// pillars built from 4.6 m corridor pieces, 1,700 cells, 386 tiny rooms) is all doorways, so
// the patrol walked every lane between every pair of pillars (1,074 points a lap), though a
// monster three lanes over has seen the character long before.
//
// Sweep walks the same cells (GetMainRouteNodes: Whole dungeon's, dead ends and hazards alike)
// but only near enough that each is SEEN:
//   1. Sight: a cell sees another when their floor centres are within SightMeters of each
//      other, on about the same level (SweepMaxRise), with no wall between them
//      (DungeonGeometry.SightClear). Corridors and crypts barely change: their walls block it.
//   2. Stops: in the patrol's walking order (a depth-first walk from the start, nearest
//      neighbour first), each cell not yet seen gets a stop: of the cells it sees (itself
//      included), the one that sees the most cells still unseen. Its sight marks them seen.
//   3. Tour: from the start, always the nearest stop not yet visited (by walking distance over
//      the cells), improved by 2-opt, and back to the start; each leg the shortest walk.
//   4. Points: the tour's cell path turned into doorway steps and laid by EmitSteps, so
//      doorways, room crossings, the wall distance and the corner cutting are the ones every
//      other patrol uses.
// No geometry, one stop, or a tour that never leaves one room: null, and the caller builds the
// Whole dungeon patrol instead. The other coverages never come here.

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

internal static partial class DungeonPathfinder
{
    // Cells whose floors differ more than this (metres) are taken as different levels: a balcony
    // over a hall, a ramp's other end. A monster on one notices the character on the other less.
    internal const double SweepMaxRise = 3.0;

    /// <summary>For PatrolProof and the tests: the stops the last sweep chose and the cells it covered.</summary>
    [ThreadStatic] internal static int LastSweepStops, LastSweepCells, LastSweepSeen;

    private static NavRouteParser? BuildSweepPatrol(
        Dictionary<uint, DungeonNavNode> graph, uint startCell, HashSet<uint> mainRoute,
        IReadOnlySet<uint>? hazardCells, DungeonGeometry geo, DungeonPatrolOptions o)
    {
        LastSweepStops = LastSweepCells = LastSweepSeen = 0;
        double sight = o.SightMeters;

        // Floor centres, and the walkable neighbours within the patrol's cells.
        var pos = new Dictionary<uint, (double X, double Y, double Z)>();
        foreach (uint c in mainRoute)
        {
            if (!graph.TryGetValue(c, out var n)) continue;
            pos[c] = geo.TryCellFloorCentre(c, out double fx, out double fy, out double fz)
                ? (fx, fy, fz)
                : (DungeonGeometry.NavToWorld(n.EW), DungeonGeometry.NavToWorld(n.NS), n.Z);
        }
        if (pos.Count < 2) return null;
        var adj = new Dictionary<uint, List<uint>>();
        foreach (uint c in pos.Keys)
        {
            var list = new List<uint>();
            var cn = graph[c];
            foreach (uint nb in cn.Neighbors)
            {
                if (!pos.ContainsKey(nb) || !graph.TryGetValue(nb, out var nbn) || IsDropEdge(cn, nbn)) continue;
                list.Add(nb);
            }
            list.Sort((a, b) => Dist(pos[c], pos[a]).CompareTo(Dist(pos[c], pos[b])));
            adj[c] = list;
        }

        uint start = pos.ContainsKey(startCell) ? startCell : NearestOf(pos, graph, startCell);
        if (start == 0) return null;

        // 1. Sight, worked out on demand (cells bucketed by SightMeters).
        var buckets = new Dictionary<long, List<uint>>();
        foreach (var (c, p) in pos)
        {
            long k = BucketKey(p.X, p.Y, sight);
            if (!buckets.TryGetValue(k, out var b)) buckets[k] = b = new List<uint>();
            b.Add(c);
        }
        var seenFrom = new Dictionary<uint, List<uint>>();
        List<uint> Sees(uint c)
        {
            if (seenFrom.TryGetValue(c, out var list)) return list;
            list = new List<uint> { c };
            var p = pos[c];
            int bx = (int)Math.Floor(p.X / sight), by = (int)Math.Floor(p.Y / sight);
            for (int gx = bx - 1; gx <= bx + 1; gx++)
                for (int gy = by - 1; gy <= by + 1; gy++)
                {
                    if (!buckets.TryGetValue(((long)gx << 32) ^ (uint)gy, out var b)) continue;
                    foreach (uint v in b)
                    {
                        if (v == c) continue;
                        var q = pos[v];
                        if (Math.Abs(q.Z - p.Z) > SweepMaxRise || Dist(p, q) > sight) continue;
                        if (geo.SightClear(p.X, p.Y, p.Z, q.X, q.Y, q.Z)) list.Add(v);
                    }
                }
            seenFrom[c] = list;
            return list;
        }

        // 2. Stops, in the walking order.
        var order = new List<uint>();
        var visited = new HashSet<uint> { start };
        var stack = new Stack<uint>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            uint c = stack.Pop();
            order.Add(c);
            var nbs = adj[c];
            for (int i = nbs.Count - 1; i >= 0; i--)   // nearest neighbour first
                if (visited.Add(nbs[i])) stack.Push(nbs[i]);
        }
        var covered = new HashSet<uint>();
        var stops = new List<uint>();
        foreach (uint u in order)
        {
            if (covered.Contains(u)) continue;
            uint best = u;
            int bestGain = -1;
            foreach (uint v in Sees(u))
            {
                int gain = 0;
                foreach (uint w in Sees(v)) if (!covered.Contains(w)) gain++;
                if (gain > bestGain) { bestGain = gain; best = v; }
            }
            stops.Add(best);
            foreach (uint w in Sees(best)) covered.Add(w);
            covered.Add(u);   // u sees best, so best sees u (sight is symmetric)
        }
        LastSweepStops = stops.Count;
        LastSweepCells = order.Count;
        LastSweepSeen = covered.Count;
        if (stops.Count < 2) return null;

        // 3. Tour: the start, then the nearest unvisited stop each time (by walking distance), then
        //    2-opt (reverse any stretch whose ends are closer the other way round) to take out the
        //    criss-crossing a nearest-next tour leaves; each leg the shortest walk.
        var tourCells = new List<uint>(stops.Count + 1) { start };
        foreach (uint st in stops) if (st != start) tourCells.Add(st);
        int stopCount = tourCells.Count;
        var index = new Dictionary<uint, int>();
        for (int i = 0; i < stopCount; i++) index[tourCells[i]] = i;
        var walk = new double[stopCount, stopCount];
        for (int i = 0; i < stopCount; i++)
        {
            var d = WalkDistances(adj, pos, tourCells[i]);
            for (int j = 0; j < stopCount; j++) walk[i, j] = d.TryGetValue(tourCells[j], out double v) ? v : double.PositiveInfinity;
        }
        var tour = new List<int> { 0 };
        var left = new HashSet<int>();
        for (int i = 1; i < stopCount; i++) if (!double.IsPositiveInfinity(walk[0, i])) left.Add(i);   // unreachable stops dropped
        while (left.Count > 0)
        {
            int last = tour[^1], next = -1;
            foreach (int j in left) if (next < 0 || walk[last, j] < walk[last, next]) next = j;
            tour.Add(next);
            left.Remove(next);
        }
        TwoOpt(tour, walk);

        var path = new List<uint> { start };
        for (int k = 0; k < tour.Count; k++)
        {
            uint from = tourCells[tour[k]], to = tourCells[tour[(k + 1) % tour.Count]];
            if (from == to) continue;
            var leg = WalkToNearest(adj, pos, from, new HashSet<uint> { to });
            if (leg == null) continue;
            for (int i = 1; i < leg.Count; i++) path.Add(leg[i]);
        }

        // 4. Points, through every doorway the tour takes.
        var rooms = BuildRooms(graph, geo, hazardCells);
        var steps = new List<Step>();
        for (int i = 0; i + 1 < path.Count; i++)
        {
            uint a = path[i], b = path[i + 1];
            if (!rooms.RoomOf.TryGetValue(a, out int ra) || !rooms.RoomOf.TryGetValue(b, out int rb) || ra == rb) continue;
            steps.Add(new Step(a, b, EdgeKey(a, b)));
        }
        if (steps.Count == 0) return null;
        if (PointLog != null)
            MainRouteNote += $"; sweep: {stops.Count} stops see {covered.Count} of {order.Count} cells within {sight:0.#} m, "
                           + $"tour {path.Count} cells, {steps.Count} doorway steps";
        var route = new NavRouteParser { RouteType = NavRouteType.Circular };
        route.Points.AddRange(EmitSteps(graph, geo, rooms, steps, closed: true, null, 0, null, 0, o, out _));
        return route;
    }

    private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static long BucketKey(double x, double y, double size)
        => ((long)(int)Math.Floor(x / size) << 32) ^ (uint)(int)Math.Floor(y / size);

    private static uint NearestOf(Dictionary<uint, (double X, double Y, double Z)> pos, Dictionary<uint, DungeonNavNode> graph, uint cell)
    {
        if (!graph.TryGetValue(cell, out var n)) return 0;
        var p = (DungeonGeometry.NavToWorld(n.EW), DungeonGeometry.NavToWorld(n.NS), n.Z);
        uint best = 0;
        double bestD = double.MaxValue;
        foreach (var (c, q) in pos)
        {
            double d = Dist(p, q) + Math.Abs(q.Z - n.Z);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    // Dijkstra from src: the walking distance to every cell it reaches.
    private static Dictionary<uint, double> WalkDistances(Dictionary<uint, List<uint>> adj,
                                                          Dictionary<uint, (double X, double Y, double Z)> pos, uint src)
    {
        var dist = new Dictionary<uint, double> { [src] = 0 };
        var pq = new PriorityQueue<uint, double>();
        pq.Enqueue(src, 0);
        while (pq.TryDequeue(out uint c, out double d))
        {
            if (d > dist[c]) continue;
            foreach (uint nb in adj[c])
            {
                double nd = d + Dist(pos[c], pos[nb]) + Math.Abs(pos[c].Z - pos[nb].Z);
                if (dist.TryGetValue(nb, out double old) && old <= nd) continue;
                dist[nb] = nd;
                pq.Enqueue(nb, nd);
            }
        }
        return dist;
    }

    // 2-opt over a closed tour (tour[0], the start, stays first): reverse tour[i..j] whenever that
    // shortens it, until no reversal does (bounded passes). Walking distances need not be symmetric
    // to the metre (levels), so each reversal is judged on the full stretch.
    internal static void TwoOpt(List<int> tour, double[,] walk)
    {
        int n = tour.Count;
        if (n < 4) return;
        double Leg(int a, int b) => walk[tour[a], tour[b % n]];
        for (int pass = 0; pass < 50; pass++)
        {
            bool improved = false;
            for (int i = 1; i < n - 1; i++)
                for (int j = i + 1; j < n; j++)
                {
                    double before = Leg(i - 1, i) + Leg(j, j + 1);
                    double after = walk[tour[i - 1], tour[j]] + walk[tour[i], tour[(j + 1) % n]];
                    if (after + 1e-6 >= before) continue;
                    tour.Reverse(i, j - i + 1);
                    improved = true;
                }
            if (!improved) break;
        }
    }

    // Dijkstra from src (by the distance between floor centres) to the nearest cell in goals; the path, src first.
    private static List<uint>? WalkToNearest(Dictionary<uint, List<uint>> adj, Dictionary<uint, (double X, double Y, double Z)> pos,
                                             uint src, HashSet<uint> goals)
    {
        var dist = new Dictionary<uint, double> { [src] = 0 };
        var prev = new Dictionary<uint, uint>();
        var pq = new PriorityQueue<uint, double>();
        pq.Enqueue(src, 0);
        while (pq.TryDequeue(out uint c, out double d))
        {
            if (d > dist[c]) continue;
            if (c != src && goals.Contains(c))
            {
                var path = new List<uint>();
                for (uint at = c; ; at = prev[at]) { path.Add(at); if (at == src) break; }
                path.Reverse();
                return path;
            }
            foreach (uint nb in adj[c])
            {
                double nd = d + Dist(pos[c], pos[nb]) + Math.Abs(pos[c].Z - pos[nb].Z);
                if (dist.TryGetValue(nb, out double old) && old <= nd) continue;
                dist[nb] = nd;
                prev[nb] = c;
                pq.Enqueue(nb, nd);
            }
        }
        return null;
    }
}

// DungeonPathfinder, jump-downs (2026-11-17).
//
// AC won't walk a character off a ledge (it slides along the edge): a jump goes over. DreamWeave Thread
// 0x0169: the last three monsters were in a cave 9-12 m under the top floor, and the one way down is a
// 20 m shaft in the middle of a hall (four open-air cells, no rim). The dungeon map had no walk there, so
// the character was left there; a jump by hand took it in.
//
// A jump-down is one way (nobody climbs back up a ledge), so a walk takes one only when asked:
// /ra dunnav-to <id> jump, which a script sends only when no monster left can be walked to. In a
// Thread that is safe: the cache and the exit appear where you stand once it is cleared.
//
// A jump-down is an opening (a doorway of the map) from a cell standing on its own floor to a side whose
// floor, under the opening, is more than LedgeMeters lower, however much: the ledges, found from the
// openings themselves (a closed ledge is gone from the graph, not from the geometry). It lands in the cell
// whose own floor is under the spot a short hop past the edge. No depth limit (2026-11-18: "No limit
// on jump down"; the fall doesn't matter): it was 15 m, and the floor under the far side was looked for
// only as deep as the ledge pass looks (18 m); now all the way down to the dungeon's lowest floor.

using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

/// <summary>A jump down off the edge of an opening: from cell <see cref="From"/> (its floor at <see cref="Z"/>)
/// at the opening's centre (<see cref="X"/>, <see cref="Y"/>), facing (<see cref="Nx"/>, <see cref="Ny"/>), onto
/// cell <see cref="Land"/>'s floor at (<see cref="LandX"/>, <see cref="LandY"/>, <see cref="LandZ"/>).</summary>
internal readonly record struct DungeonJump(uint From, uint Land, double X, double Y, double Z, double Nx, double Ny,
                                            double LandX, double LandY, double LandZ)
{
    public double Drop => Z - LandZ;
}

internal static partial class DungeonPathfinder
{
    /// <summary>Below the ledge pass's look (18 m), the floor under a jump is looked for every this many metres
    /// (each look covers LedgeDownBand either side) down to the dungeon's lowest floor.</summary>
    internal const double JumpDownStep = 3.0;
    /// <summary>A jump-down costs the path this much walk: any walk round is taken first.</summary>
    internal const double JumpCostMeters = 40.0;
    /// <summary>The jump's power (spacebar held, of 1000): a short hop over the edge.</summary>
    internal const int JumpHoldMs = 300;
    /// <summary>The walk lines up square to the edge from this far back ...</summary>
    internal const double JumpApproachMeters = 1.5;
    /// <summary>... and jumps from this far short of it (the edge stops the character there anyway).</summary>
    internal const double JumpBackMeters = 0.1;

    // Where the hop is looked for a floor to land on: this far past the edge (the first that has one).
    private static readonly double[] JumpLandInsets = { 2.0, 1.5, 3.0, 1.0, 4.0 };

    /// <summary>Every jump-down of the dungeon (both ways of every opening looked at).</summary>
    internal static List<DungeonJump> FindJumps(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo)
    {
        var list = new List<DungeonJump>();
        if (graph.Count == 0) return list;
        double lowest = LowestLook(graph);   // under the lowest cell: the deepest look
        var solids = new FallSolids(graph);
        foreach (var d in geo.AllDoorways)
            foreach (var (from, to) in new[] { (d.CellA, d.CellB), (d.CellB, d.CellA) })
            {
                if (!graph.ContainsKey(from) || !graph.ContainsKey(to)) continue;
                var (nx, ny) = d.NormalFrom(from);   // out of from, over the edge
                if (OwnFloorInside(geo, from, d, -nx, -ny) is not { } top) continue;   // standing on nothing: no jump from there
                if (FloorInside(geo, to, d, nx, ny, LedgeFloorFallback ? lowest : double.NaN) is not { } low) continue;
                double drop = top - low;
                if (drop <= LedgeMeters) continue;
                // Where the walk lines up and jumps from: nobody stands there inside something placed.
                if (geo.InsidePlaced(d.X - nx * JumpApproachMeters, d.Y - ny * JumpApproachMeters, top)
                    || geo.InsidePlaced(d.X - nx * JumpBackMeters, d.Y - ny * JumpBackMeters, top)) continue;
                if (!TryLanding(graph, geo, d, nx, ny, top, lowest, solids, out uint land, out double lx, out double ly, out double lz) || land == from) continue;
                list.Add(new DungeonJump(from, land, d.X, d.Y, top, nx, ny, lx, ly, lz));
            }
        return list;
    }

    // ── What the cells place, in the way of a jump (2026-11-18) ──────────────────
    // The landing was looked for on the cells' floors only. A spot inside an altar or a statue on that floor is no
    // landing (the map's InsidePlaced), and a fall that meets something placed between the take-off and the floor
    // lands on that instead: a platform or a bridge over a pit (0x000A's hall: the 0x02000224 bridge and the
    // 0x02000222 slabs at z -12.8 over the hole near 0131/0132; 22 of 458 jumps in nine dungeons fell onto one).
    // Those are looked for in every object the cells place (StaticMeshSource), the ones the map leaves off too (a
    // slab has nothing in a body's band at its own floor), in the body's column at the landing spot.

    /// <summary>The column a falling body sweeps: its middle and four points this far out.</summary>
    internal const double JumpFallRadius = 0.4;
    /// <summary>The fall is looked at from the landing floor + PlacedBandLow (lower is stepped over on landing) up to the
    /// take-off height + this (a deck flush with the edge is walked onto, not jumped past).</summary>
    internal const double JumpFallOverTop = 0.3;

    // The objects the cells place, as solid models, placed the first time a landing spot needs them.
    private sealed class FallSolids
    {
        private readonly List<(uint SetupId, uint CellId, BoundingVolume Mesh)>? _statics;
        private readonly Dictionary<int, PlacedShape?> _shapes = new();

        public FallSolids(Dictionary<uint, DungeonNavNode> graph)
        {
            var source = StaticMeshSource;
            if (source == null || graph.Count == 0) return;
            uint lb = 0;
            foreach (uint c in graph.Keys) { lb = c >> 16; break; }
            try { _statics = source(lb); } catch { _statics = null; }
        }

        /// <summary>Something placed is solid in the body's column at (x, y) between zLo and zHi.</summary>
        public bool Hit(double x, double y, double zLo, double zHi)
        {
            if (_statics == null || zHi <= zLo) return false;
            double r = JumpFallRadius;
            for (int i = 0; i < _statics.Count; i++)
            {
                var m = _statics[i].Mesh;
                if (m.MeshTriangles is not { Length: >= 3 }) continue;
                if (m.Max.X < x - r || m.Min.X > x + r || m.Max.Y < y - r || m.Min.Y > y + r || m.Max.Z < zLo || m.Min.Z > zHi) continue;
                if (!_shapes.TryGetValue(i, out var s)) _shapes[i] = s = PlacedSolid.Place(_statics[i].SetupId, m.MeshTriangles);
                if (s == null) continue;
                if (s.SolidIn(x, y, zLo, zHi) || s.SolidIn(x + r, y, zLo, zHi) || s.SolidIn(x - r, y, zLo, zHi)
                    || s.SolidIn(x, y + r, zLo, zHi) || s.SolidIn(x, y - r, zLo, zHi)) return true;
            }
            return false;
        }
    }


    // The cell's own floor just inside the opening (none: null).
    private static double? OwnFloorInside(DungeonGeometry geo, uint cell, DungeonDoorway d, double ux, double uy)
    {
        foreach (double inset in LedgeInsets)
        {
            double x = d.X + ux * inset, y = d.Y + uy * inset;
            foreach (double dz in LedgeZRefs)
                if (geo.TryCellFloorHeight(cell, x, y, d.Z + dz, out double h)) return h;
        }
        return null;
    }

    // The floor a hop past the edge lands on, and the cell it is the floor of: the first floor under the spot (the
    // one a fall meets), clear of what the cells place there and on the way down; else the next inset.
    private static bool TryLanding(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, DungeonDoorway d,
                                   double nx, double ny, double top, double lowest, FallSolids? solids,
                                   out uint land, out double lx, out double ly, out double lz)
    {
        foreach (double inset in JumpLandInsets)
        {
            double x = d.X + nx * inset, y = d.Y + ny * inset;
            bool found = false;
            double fx = 0, fy = 0, fz = 0;
            uint fc = 0;
            for (double dz = 0; top + dz >= lowest && !found; dz -= JumpDownStep)   // 0, -3, -6 ... as far down as the dungeon goes
            {
                if (!geo.TryFloorHeight(x, y, top + dz, LedgeDownBand, out double h) || h > top - LedgeMeters) continue;
                foreach (var n in graph.Values)
                    if (geo.TryCellFloorHeight(n.CellId, x, y, h, out double hh) && Math.Abs(hh - h) < 0.5)
                    {
                        fc = n.CellId; fx = x; fy = y; fz = hh;
                        found = true;
                        break;
                    }
            }
            if (!found) continue;
            // Inside an altar or a statue on that floor, or a platform on the way down: not here.
            if (geo.InsidePlaced(fx, fy, fz)) continue;
            if (solids != null && solids.Hit(fx, fy, fz + DungeonGeometry.PlacedBandLow, top + JumpFallOverTop)) continue;
            land = fc; lx = fx; ly = fy; lz = fz;
            return true;
        }
        land = 0; lx = ly = lz = 0;
        return false;
    }

    /// <summary>
    /// A* from <paramref name="start"/> to <paramref name="goal"/> as <see cref="FindPath"/>, and the
    /// jump-downs as one-way steps (each costs <see cref="JumpCostMeters"/> of walk). Each cell comes with
    /// the jump taken into it (null: walked). Empty: unreachable even so.
    /// </summary>
    internal static List<(uint Cell, DungeonJump? Jump)> FindPathWithJumps(
        uint start, uint goal, Dictionary<uint, DungeonNavNode> graph, IReadOnlyList<DungeonJump> jumps,
        IReadOnlySet<uint>? hazardCells = null)
    {
        var path = new List<(uint, DungeonJump?)>();
        if (!graph.ContainsKey(start) || !graph.ContainsKey(goal)) return path;
        var byFrom = jumps.GroupBy(j => j.From).ToDictionary(g => g.Key, g => g.ToList());
        var goalNode = graph[goal];
        var gScore = new Dictionary<uint, double> { [start] = 0.0 };
        var cameFrom = new Dictionary<uint, (uint From, DungeonJump? Jump)>();
        var open = new PriorityQueue<uint, double>();
        var closed = new HashSet<uint>();
        open.Enqueue(start, H(graph[start], goalNode));
        double jumpCost = JumpCostMeters / 240.0;   // nav units

        while (open.Count > 0)
        {
            uint current = open.Dequeue();
            if (!closed.Add(current)) continue;
            if (current == goal) break;
            var cur = graph[current];
            double gCur = gScore[current];

            void Relax(uint nb, double cost, DungeonJump? jump)
            {
                if (closed.Contains(nb) || !graph.TryGetValue(nb, out var nbNode)) return;
                if (nb != goal && hazardCells != null && hazardCells.Contains(nb)) return;
                double g = gCur + cost;
                if (g >= gScore.GetValueOrDefault(nb, double.MaxValue)) return;
                gScore[nb] = g;
                cameFrom[nb] = (current, jump);
                open.Enqueue(nb, g + H(nbNode, goalNode));
            }

            foreach (uint nb in cur.Neighbors)
                if (graph.TryGetValue(nb, out var nbNode) && !IsDropEdge(cur, nbNode)) Relax(nb, H(cur, nbNode), null);
            if (byFrom.TryGetValue(current, out var js))
                foreach (var j in js)
                {
                    var landNode = graph[j.Land];
                    Relax(j.Land, H(cur, landNode) + jumpCost, j);
                }
        }
        if (!closed.Contains(goal)) return path;
        for (uint c = goal; ; )
        {
            if (cameFrom.TryGetValue(c, out var step)) { path.Add((c, step.Jump)); c = step.From; }
            else { path.Add((c, null)); break; }
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// Points for walking a path with jump-downs (from <see cref="FindPathWithJumps"/>), the destination
    /// not included: each stretch walked as <see cref="BuildPathPoints"/> lays it; at each jump, a point
    /// <see cref="JumpApproachMeters"/> back from the edge (square to it), then a Jump point just short of
    /// it facing over it; the next stretch starts where it lands.
    /// </summary>
    internal static List<NavPoint> BuildJumpPathPoints(
        List<(uint Cell, DungeonJump? Jump)> path, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo,
        IReadOnlySet<uint>? hazardCells, double startNS, double startEW, double startZ,
        double destNS, double destEW, double destZ, out int jumps)
    {
        using var holes = CountHoles();   // one build: every stretch's walk-rounds count
        var pts = new List<NavPoint>();
        jumps = 0;
        int seg = 0;
        double sNs = startNS, sEw = startEW, sZ = startZ;
        for (int k = 1; k <= path.Count; k++)
        {
            if (k < path.Count && path[k].Jump is null) continue;
            var cells = path.GetRange(seg, k - seg).Select(p => p.Cell).ToList();
            if (k == path.Count)
            {
                pts.AddRange(BuildPathPoints(cells, graph, geo, hazardCells, sNs, sEw, sZ, destNS, destEW, destZ, out _));
                break;
            }
            var j = path[k].Jump!.Value;
            double aNs = DungeonGeometry.WorldToNav(j.Y - j.Ny * JumpApproachMeters), aEw = DungeonGeometry.WorldToNav(j.X - j.Nx * JumpApproachMeters);
            // The stretch to the approach point: BuildPathPoints walks its legs round what the cells place, the leg
            // into the approach point too (the stretch's destination, 2026-11-18).
            pts.AddRange(BuildPathPoints(cells, graph, geo, hazardCells, sNs, sEw, sZ, aNs, aEw, j.Z, out _));
            var approach = new NavPoint { Type = NavPointType.Point, NS = aNs, EW = aEw, Z = j.Z / 240.0 };
            var jump = new NavPoint
            {
                Type = NavPointType.Jump,
                NS = DungeonGeometry.WorldToNav(j.Y - j.Ny * JumpBackMeters),
                EW = DungeonGeometry.WorldToNav(j.X - j.Nx * JumpBackMeters),
                Z = j.Z / 240.0,
                JumpHeading = JumpHeadingOf(j.Nx, j.Ny),
                JumpShift = false,
                JumpMs = JumpHoldMs,
                Doorway = true,   // the tight reach: jump at the edge, not a metre short of it
            };
            pts.Add(approach);
            // The line-up to the edge (1.4 m, square to it) was never looked at either: an object on it is walked
            // round to the Jump point, whose heading still faces over the edge.
            if (geo != null && geo.PlacedCount > 0)
            {
                var lineUp = new List<NavPoint> { approach, jump };
                if (WalkRoundHoles(lineUp, geo, objectsOnly: true) > 0)
                    for (int q = 1; q + 1 < lineUp.Count; q++) pts.Add(lineUp[q]);
            }
            pts.Add(jump);
            jumps++;
            sNs = DungeonGeometry.WorldToNav(j.LandY);
            sEw = DungeonGeometry.WorldToNav(j.LandX);
            sZ = j.LandZ;
            seg = k;
        }
        return pts;
    }

    /// <summary>The compass heading (0 north, 90 east) of the world direction (x east, y north).</summary>
    internal static double JumpHeadingOf(double nx, double ny)
    {
        double deg = Math.Atan2(nx, ny) * (180.0 / Math.PI);
        return deg < 0 ? deg + 360.0 : deg;
    }
}

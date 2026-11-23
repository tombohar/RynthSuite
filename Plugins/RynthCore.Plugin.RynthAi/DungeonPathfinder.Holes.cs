// DungeonPathfinder, holes (2026-11-15).
//
// The walks are laid on the dungeon map: cell to cell through their openings, a room's openings
// string-pulled. Nothing looks at the floor inside a cell. DreamWeave Thread 0x039B has a walkway at
// z 6 through two square rooms, each with a 5 x 5 m pit in the middle down to the z -6 rooms below
// and a 2 m rim round it. The explore went doorway to doorway, straight over each pit ("trying to
// navigate over a big hole", Tom). LegClearance read 0 on those legs: a floor edge counts as a wall.
// A ledge between two cells is closed in the graph (DungeonPathfinder.Ledges.cs); a hole inside one
// cell is not an opening, so that never saw it.
//
// WalkRoundHoles goes over a laid walk. A leg whose straight line leaves the floor it is on (a sample
// with no floor within a step of the last, or a drop of more than a step) is replaced by a walk round
// on the floor. That is an A* over floor samples 0.5 m apart, in a box round the leg, at the floor's
// own height (up and down ramps and stairs, a step at a time), on samples clear of walls and floor
// edges by HoleKeepOff. The A* path is then pulled straight wherever a leg stays on the floor and
// keeps that clearance. With no way round, the leg stays as laid (a drop the walk can't do
// otherwise) and the note says so.
//
// 2026-11-18, objects: a walk round can start inside something placed (a corner laid by a chair, the
// character itself beside a table): every sample round it was too close to it, so there was no way
// round at all (46 of the 139 legs the building survey still found through furniture). It may now
// cross the samples of what it stands in, and the ring round it, until it is out. In a building map
// (BuildingMap, or the dungeon map of a landblock with buildings) a walk point's height is often not
// a floor's (a doorway's lowest corner, a corner on a stair the map has no floor for): the floor nearest
// it within 3 m was the ground floor under the stair, and the walk round went down there (Holtburg
// A9B3#0, 0104 to 0106 by 32626,34455 at 116.0 from a stair corner at 117.5). There it is the floor
// within BuildingStartBand of the point, else the point's own height, and a walk round that leaves the
// leg's heights by more than BuildingRoundBand is no way round.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

internal static partial class DungeonPathfinder
{
    /// <summary>Off: the walk as laid (tests compare the two).</summary>
    internal static bool HolesEnabled = true;

    private const double HoleSample = 0.5;      // the grid, and the samples along a leg (metres)
    private const double HoleStep = 0.6;        // floor heights this far apart are one walkable step
    private const double HoleKeepOff = 0.6;     // a grid sample this clear of walls and floor edges
    private const double HolePad = 10.0;        // the box round the leg (metres each way)
    private const int HoleMaxGrid = 160 * 160;
    private const double HoleStartBand = 3.0;  // a walk point's floor, looked for this far above or below it
    // In a building map: a walk point's floor within this of its height, and no walk round leaving the leg's
    // heights by more than BuildingRoundBand.
    internal const double BuildingStartBand = 1.0, BuildingRoundBand = 1.0;
    // In a building map the box round a leg is this much each way (its rooms are small; with no way round, the A*
    // went over every sample of a 10 m box, at a clearance each: most of a furnished walk's time).
    internal const double BuildingHolePad = 4.0;

    /// <summary>How many legs over a hole (or through a placed object) the build walked round, and how many it couldn't:
    /// running counts over one build (<see cref="CountHoles"/>), every walk-round of the build added in (the patrol's
    /// rooms, its cell walk, the portal step's legs, each stretch of a jump walk). A call outside any build counts on
    /// its own. They were the last call's only, so a patrol's notes said 0 with no way round where 4 legs had none
    /// (0x00FE, 0x00D6: re-walking the finished patrols found them).</summary>
    [ThreadStatic] internal static int HolesWalkedRound, HolesLeft;
    [ThreadStatic] private static int _holeCountDepth;

    /// <summary>Begins a build's count (the outermost one only sets the counts to 0); dispose it when the build is done.</summary>
    internal static HoleCount CountHoles()
    {
        if (_holeCountDepth++ == 0) HolesWalkedRound = HolesLeft = 0;
        return new HoleCount();
    }

    internal readonly struct HoleCount : IDisposable
    {
        public void Dispose() { if (_holeCountDepth > 0) _holeCountDepth--; }
    }

    // The geometries that are a building's (BuildingMap's, or the dungeon map of a landblock with buildings).
    private static readonly ConditionalWeakTable<DungeonGeometry, object> _buildingMaps = new();

    /// <summary>Marks <paramref name="geo"/> as a building's map: its walks round start on the floor at their own height.</summary>
    internal static void MarkBuildingMap(DungeonGeometry geo) => _buildingMaps.AddOrUpdate(geo, _buildingMaps);

    internal static bool IsBuildingMap(DungeonGeometry geo) => _buildingMaps.TryGetValue(geo, out _);

    /// <summary>
    /// Replaces each leg of <paramref name="pts"/> that leaves the floor with a walk round on it
    /// (see the file's comment). Returns how many legs this call walked round (the build's counts
    /// go up by as many, and by the legs it couldn't). <paramref name="accept"/>, when given, must
    /// take every leg of a walk round (from, to) or the leg stays as laid and counts as having no way
    /// round: the portal step's (DungeonPathfinder.Portals.cs: a walk round an object must not go into
    /// a portal's circle).
    /// </summary>
    internal static int WalkRoundHoles(List<NavPoint> pts, DungeonGeometry geo, bool objectsOnly = false,
                                       Func<NavPoint, NavPoint, bool>? accept = null)
    {
        using var count = CountHoles();
        if (!HolesEnabled || pts.Count < 2) return 0;
        if (objectsOnly && geo.PlacedCount == 0) return 0;
        int walked = 0;
        var outPts = new List<NavPoint>(pts.Count + 16) { pts[0] };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            double ax = DungeonGeometry.NavToWorld(a.EW), ay = DungeonGeometry.NavToWorld(a.NS), az = a.Z * 240.0;
            double bx = DungeonGeometry.NavToWorld(b.EW), by = DungeonGeometry.NavToWorld(b.NS), bz = b.Z * 240.0;
            if (objectsOnly ? !LegMeetsPlaced(geo, ax, ay, az, bx, by, bz) : OnFloorAll(geo, ax, ay, az, bx, by, bz))
            {
                outPts.Add(b);
                continue;
            }
            var round = FloorPath(geo, ax, ay, az, bx, by, bz);
            List<NavPoint>? laid = null;
            if (round != null)
            {
                laid = new List<NavPoint>(round.Count);
                foreach (var (x, y, z) in round)
                    laid.Add(new NavPoint { Type = NavPointType.Point, NS = DungeonGeometry.WorldToNav(y), EW = DungeonGeometry.WorldToNav(x), Z = z / 240.0 });
                if (accept != null)
                {
                    var from = a;
                    for (int k = 0; k <= laid.Count; k++)
                    {
                        var p = k < laid.Count ? laid[k] : b;
                        if (!accept(from, p)) { laid = null; break; }
                        from = p;
                    }
                }
            }
            if (laid == null)
            {
                HolesLeft++;
                outPts.Add(b);
                continue;
            }
            HolesWalkedRound++;
            walked++;
            outPts.AddRange(laid);
            outPts.Add(b);
        }
        if (walked > 0)
        {
            pts.Clear();
            pts.AddRange(outPts);
        }
        return walked;
    }

    /// <summary>The straight walk from a to b stays on the floor all the way (OnFloorAll): the hole rule's
    /// test, and the straightener's (DungeonPathfinder.Straighten.cs).</summary>
    internal static bool LegOnFloor(DungeonGeometry geo, NavPoint a, NavPoint b)
        => OnFloorAll(geo, DungeonGeometry.NavToWorld(a.EW), DungeonGeometry.NavToWorld(a.NS), a.Z * 240.0,
                      DungeonGeometry.NavToWorld(b.EW), DungeonGeometry.NavToWorld(b.NS), b.Z * 240.0);

    // The straight walk a-b goes through something the cells place (an altar, a statue) on the floor it
    // follows; a stretch with no floor under it on the map is no concern here (the walk round holes is the
    // explore's: a tube's floorless cells would be walked round to nowhere).
    private static bool ThroughPlaced(DungeonGeometry geo, double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < HoleSample) return false;
        if (!geo.TryFloorHeight(ax, ay, az, HoleStartBand, out double zf)) zf = az;
        int steps = (int)Math.Ceiling(len / HoleSample);
        for (int s = 1; s < steps; s++)
        {
            double t = (double)s / steps, x = ax + dx * t, y = ay + dy * t;
            if (!geo.TryFloorHeight(x, y, zf, HoleStep, out double h) && !geo.TryFloorHeight(x, y, az + (bz - az) * t, HoleStep, out h)) continue;
            zf = h;
            if (geo.InsidePlaced(x, y, h)) return true;
        }
        return false;
    }

    // The straight walk a-b stays on the floor all the way: every sample has a floor within a step of
    // the last one's (following ramps and stairs). The ends are where the walk already stands.
    private static bool OnFloorAll(DungeonGeometry geo, double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < HoleSample) return true;
        if (!geo.TryFloorHeight(ax, ay, az, HoleStartBand, out double zf)) zf = az;   // a point's height can be off a little
        int steps = (int)Math.Ceiling(len / HoleSample);
        for (int s = 1; s <= steps; s++)
        {
            double t = (double)s / steps, x = ax + dx * t, y = ay + dy * t;
            if (!geo.TryFloorHeight(x, y, zf, HoleStep, out double h))
            {
                // The far end on another level (the leg's own end): a stair the samples skipped.
                if (s == steps && Math.Abs(bz - zf) <= 1.5 && geo.TryFloorHeight(x, y, bz, HoleStep, out _)) return true;
                return false;
            }
            // Through an altar or a statue the cells place (DungeonGeometry.AddPlacedObjects): no floor to
            // walk there either, so the leg is walked round it like a hole (the far end is the walk's own).
            if (s < steps && geo.InsidePlaced(x, y, h)) return false;
            zf = h;
        }
        return true;
    }

    // A* round on the floor from a to b; the points between (pulled straight), or null with no way.
    private static List<(double X, double Y, double Z)>? FloorPath(DungeonGeometry geo, double ax, double ay, double az, double bx, double by, double bz)
    {
        bool building = IsBuildingMap(geo);
        double pad = building ? BuildingHolePad : HolePad;
        double x0 = Math.Min(ax, bx) - pad, y0 = Math.Min(ay, by) - pad;
        int nx = (int)Math.Ceiling((Math.Abs(bx - ax) + 2 * pad) / HoleSample) + 1;
        int ny = (int)Math.Ceiling((Math.Abs(by - ay) + 2 * pad) / HoleSample) + 1;
        if (nx * ny > HoleMaxGrid) return null;
        int Idx(int i, int j) => j * nx + i;
        double Gx(int i) => x0 + i * HoleSample;
        double Gy(int j) => y0 + j * HoleSample;
        int si = (int)Math.Round((ax - x0) / HoleSample), sj = (int)Math.Round((ay - y0) / HoleSample);
        int ti = (int)Math.Round((bx - x0) / HoleSample), tj = (int)Math.Round((by - y0) / HoleSample);

        var height = new double[nx * ny];
        var state = new byte[nx * ny];   // 0 unknown, 1 floor and clear, 2 no
        var g = new double[nx * ny];
        var from = new int[nx * ny];
        for (int k = 0; k < g.Length; k++) { g[k] = double.MaxValue; from[k] = -1; }

        if (!geo.TryFloorHeight(ax, ay, az, building ? BuildingStartBand : HoleStartBand, out double sh)) sh = az;
        int start = Idx(si, sj), goal = Idx(ti, tj);
        height[start] = sh; state[start] = 1;   // the walk stands there already
        g[start] = 0;

        // A start inside something placed: the boxes of what it stands in, with the ring a sample keeps off them,
        // where the walk may cross their samples on its way out (only from the start, or from a sample it reached so).
        bool[]? esc = null;
        byte[]? escState = null;   // 0 unknown, 1 floor and not in a wall, 2 no
        double ex0 = double.MaxValue, ey0 = double.MaxValue, ex1 = double.MinValue, ey1 = double.MinValue;
        if (geo.PlacedCount > 0 && geo.InsidePlaced(ax, ay, sh))
        {
            foreach (var o in geo.PlacedNear(ax, ay, 0))
            {
                ex0 = Math.Min(ex0, o.MinX); ey0 = Math.Min(ey0, o.MinY);
                ex1 = Math.Max(ex1, o.MaxX); ey1 = Math.Max(ey1, o.MaxY);
            }
            if (ex0 <= ex1)
            {
                double ring = HoleKeepOff + HoleSample;
                ex0 -= ring; ey0 -= ring; ex1 += ring; ey1 += ring;
                esc = new bool[nx * ny];
                escState = new byte[nx * ny];
                esc[start] = true;
            }
        }

        // Floor at a step from `fromH`, and clear of walls and edges (the goal: only its floor).
        bool Walkable(int i, int j, double fromH, bool isGoal, out double h)
        {
            h = 0;
            if (i < 0 || j < 0 || i >= nx || j >= ny) return false;
            int k = Idx(i, j);
            if (!geo.TryFloorHeight(Gx(i), Gy(j), fromH, HoleStep, out h)) return false;
            if (isGoal) return true;
            if (state[k] == 2) return false;
            if (state[k] == 1 && Math.Abs(height[k] - h) < 0.05) return true;
            if (geo.ClearanceMemo(Gx(i), Gy(j), h, HoleKeepOff + 0.1) < HoleKeepOff) { state[k] = 2; return false; }
            state[k] = 1;
            height[k] = h;
            return true;
        }

        // On the way out of what the start stands in: a floor sample in its box (or the ring round it), not in a wall.
        bool Escapes(int i, int j, double fromH, out double h)
        {
            h = 0;
            if (escState == null || i < 0 || j < 0 || i >= nx || j >= ny) return false;
            double x = Gx(i), y = Gy(j);
            if (x < ex0 || x > ex1 || y < ey0 || y > ey1) return false;
            if (!geo.TryFloorHeight(x, y, fromH, HoleStep, out h)) return false;
            int k = Idx(i, j);
            if (escState[k] == 0)
                escState[k] = geo.InsidePlaced(x, y, h) || geo.ClearanceMemo(x, y, h, HoleKeepOff + 0.1) > 0.05 ? (byte)1 : (byte)2;
            return escState[k] == 1;
        }

        // A diagonal's side sample: walkable, or on the way out.
        bool Side(int i, int j, double fromH, bool leaving) => Walkable(i, j, fromH, false, out _) || (leaving && Escapes(i, j, fromH, out _));

        var open = new PriorityQueue<int, double>();
        open.Enqueue(start, HoleDist(ax, ay, bx, by));
        var closed = new bool[nx * ny];
        int[] di = { 1, -1, 0, 0, 1, 1, -1, -1 }, dj = { 0, 0, 1, -1, 1, -1, 1, -1 };
        bool reached = false;
        int expanded = 0;
        while (open.Count > 0)
        {
            int k = open.Dequeue();
            if (closed[k]) continue;
            closed[k] = true;
            if (k == goal) { reached = true; break; }
            if (++expanded > HoleMaxGrid) break;
            int ci = k % nx, cj = k / nx;
            for (int d = 0; d < 8; d++)
            {
                int i2 = ci + di[d], j2 = cj + dj[d];
                if (i2 < 0 || j2 < 0 || i2 >= nx || j2 >= ny) continue;
                int k2 = Idx(i2, j2);
                if (closed[k2]) continue;
                bool isGoal = k2 == goal;
                bool leaving = esc != null && esc[k];   // still on the way out of what the start stands in
                bool viaEscape = false;
                if (!Walkable(i2, j2, height[k], isGoal, out double h2))
                {
                    if (!leaving || !Escapes(i2, j2, height[k], out h2)) continue;
                    viaEscape = true;
                }
                if (isGoal) height[k2] = h2;
                // A diagonal step only between two floor samples (not across a corner of the hole).
                if (d >= 4 && !(Side(ci + di[d], cj, height[k], leaving) && Side(ci, cj + dj[d], height[k], leaving))) continue;
                double ng = g[k] + (d >= 4 ? 1.41421356 : 1.0) * HoleSample;
                if (ng >= g[k2]) continue;
                g[k2] = ng;
                from[k2] = k;
                if (viaEscape) { height[k2] = h2; esc![k2] = true; }
                else if (esc != null) esc[k2] = false;
                open.Enqueue(k2, ng + HoleDist(Gx(i2), Gy(j2), bx, by));
            }
        }
        if (!reached) return null;

        var cells = new List<int>();
        for (int k = goal; k != -1; k = from[k]) cells.Add(k);
        cells.Reverse();
        // In a building map, a walk round that goes up or down a floor from the leg's own heights (down to the ground
        // floor under a stair the map has no floor for) is no way round.
        if (building)
        {
            // The leg's heights: its ends' and the floors under them (a doorway's height is its lowest corner, a metre
            // over the floor under it in some of Holtburg's houses).
            double zLo = Math.Min(Math.Min(az, bz), Math.Min(sh, height[goal])) - BuildingRoundBand;
            double zHi = Math.Max(Math.Max(az, bz), Math.Max(sh, height[goal])) + BuildingRoundBand;
            foreach (int k in cells)
                if (height[k] < zLo || height[k] > zHi) return null;
        }
        var path = new List<(double X, double Y, double Z)>(cells.Count);
        foreach (int k in cells) path.Add((Gx(k % nx), Gy(k / nx), height[k]));
        path[0] = (ax, ay, az);
        path[path.Count - 1] = (bx, by, bz);

        // Pulled straight: from each point, the farthest one a straight leg reaches on the floor, clear.
        var kept = new List<(double X, double Y, double Z)>();
        int at = 0;
        if (esc != null)
        {
            // Out of what the start stands in: straight to the first sample clear of it (across it), pulled from there.
            int e = 0;
            while (e + 1 < cells.Count - 1 && esc[cells[e + 1]]) e++;
            if (e > 0)
            {
                at = e + 1;
                if (at < path.Count - 1) kept.Add(path[at]);
            }
        }
        while (at < path.Count - 1)
        {
            int best = at + 1;
            for (int j = path.Count - 1; j > at + 1; j--)
            {
                var p = path[at];
                var q = path[j];
                if (OnFloorAll(geo, p.X, p.Y, p.Z, q.X, q.Y, q.Z)
                    && geo.LegClearance(p.X, p.Y, p.Z, q.X, q.Y, q.Z, HoleKeepOff, 0.3, out _, out _, out _) >= HoleKeepOff - 0.15)
                {
                    best = j;
                    break;
                }
            }
            if (best < path.Count - 1) kept.Add(path[best]);
            at = best;
        }
        return kept;
    }

    private static double HoleDist(double ax, double ay, double bx, double by) => Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
}

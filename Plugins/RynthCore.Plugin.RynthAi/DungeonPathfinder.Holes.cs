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

using System;
using System.Collections.Generic;
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

    /// <summary>How many legs over a hole the last walk had walked round, and how many it couldn't.</summary>
    internal static int HolesWalkedRound, HolesLeft;

    /// <summary>
    /// Replaces each leg of <paramref name="pts"/> that leaves the floor with a walk round on it
    /// (see the file's comment). Returns how many legs were walked round.
    /// </summary>
    internal static int WalkRoundHoles(List<NavPoint> pts, DungeonGeometry geo)
    {
        HolesWalkedRound = HolesLeft = 0;
        if (!HolesEnabled || pts.Count < 2) return 0;
        var outPts = new List<NavPoint>(pts.Count + 16) { pts[0] };
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            double ax = DungeonGeometry.NavToWorld(a.EW), ay = DungeonGeometry.NavToWorld(a.NS), az = a.Z * 240.0;
            double bx = DungeonGeometry.NavToWorld(b.EW), by = DungeonGeometry.NavToWorld(b.NS), bz = b.Z * 240.0;
            if (OnFloorAll(geo, ax, ay, az, bx, by, bz))
            {
                outPts.Add(b);
                continue;
            }
            var round = FloorPath(geo, ax, ay, az, bx, by, bz);
            if (round == null)
            {
                HolesLeft++;
                outPts.Add(b);
                continue;
            }
            HolesWalkedRound++;
            foreach (var (x, y, z) in round)
                outPts.Add(new NavPoint { Type = NavPointType.Point, NS = DungeonGeometry.WorldToNav(y), EW = DungeonGeometry.WorldToNav(x), Z = z / 240.0 });
            outPts.Add(b);
        }
        if (HolesWalkedRound > 0)
        {
            pts.Clear();
            pts.AddRange(outPts);
        }
        return HolesWalkedRound;
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
            zf = h;
        }
        return true;
    }

    // A* round on the floor from a to b; the points between (pulled straight), or null with no way.
    private static List<(double X, double Y, double Z)>? FloorPath(DungeonGeometry geo, double ax, double ay, double az, double bx, double by, double bz)
    {
        double x0 = Math.Min(ax, bx) - HolePad, y0 = Math.Min(ay, by) - HolePad;
        int nx = (int)Math.Ceiling((Math.Abs(bx - ax) + 2 * HolePad) / HoleSample) + 1;
        int ny = (int)Math.Ceiling((Math.Abs(by - ay) + 2 * HolePad) / HoleSample) + 1;
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

        if (!geo.TryFloorHeight(ax, ay, az, HoleStartBand, out double sh)) sh = az;
        int start = Idx(si, sj), goal = Idx(ti, tj);
        height[start] = sh; state[start] = 1;   // the walk stands there already
        g[start] = 0;

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
                if (!Walkable(i2, j2, height[k], isGoal, out double h2)) continue;
                if (isGoal) height[k2] = h2;
                // A diagonal step only between two floor samples (not across a corner of the hole).
                if (d >= 4 && !(Walkable(ci + di[d], cj, height[k], false, out _) && Walkable(ci, cj + dj[d], height[k], false, out _))) continue;
                double ng = g[k] + (d >= 4 ? 1.41421356 : 1.0) * HoleSample;
                if (ng >= g[k2]) continue;
                g[k2] = ng;
                from[k2] = k;
                open.Enqueue(k2, ng + HoleDist(Gx(i2), Gy(j2), bx, by));
            }
        }
        if (!reached) return null;

        var cells = new List<int>();
        for (int k = goal; k != -1; k = from[k]) cells.Add(k);
        cells.Reverse();
        var path = new List<(double X, double Y, double Z)>(cells.Count);
        foreach (int k in cells) path.Add((Gx(k % nx), Gy(k / nx), height[k]));
        path[0] = (ax, ay, az);
        path[path.Count - 1] = (bx, by, bz);

        // Pulled straight: from each point, the farthest one a straight leg reaches on the floor, clear.
        var kept = new List<(double X, double Y, double Z)>();
        int at = 0;
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

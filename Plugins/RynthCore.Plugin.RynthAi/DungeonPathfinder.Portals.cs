// DungeonPathfinder, portals half: the patrol keeps out of portals (2026-10-23).
//
// Why: a portal in AC fires the moment the character touches it (the portal's collision sphere,
// 1.36 m for the usual portal Setup 0x020005D3, meets the character's 0.48 m spheres), and the
// patrol knew nothing of portals. Tiny Hive (0x0289), coverage Everything: the start cell
// 028902A5 opens north onto a ramp (028902BB) up into a 3 m nook (028902BA) whose back is the
// Surface Portal (0x7028905D, world 494.0, 26300.2, -24.0). The nook is a dead end, so the patrol
// stepped into it: waypoint 1 was the doorway's exit point 1.2 m in, 0.02 m from the portal, and
// the 35 m leg to it ran from the room south of the start straight up the ramp. A client that
// started its lap at waypoint 1 was in portal space 1.5 s later (Tom, 2026-10-23).
//
// Now BuildPatrolRoute takes the portals of the landblock (the live objects; BuildDunPatrol) as
// circles to keep out of: PatrolPortal.Radius, the touch distance plus steering slack
// (PortalKeepOutRadius). Points, legs and floor on the portal's level (PortalLevelBand) count.
//   1. Each room (DungeonRooms) the circle reaches is split by it: its floor, less the circle and
//      less a body's width along its edges, falls into parts. A doorway out of the room whose
//      passable spots are all inside the circle, or which only reaches a part the patrol doesn't
//      keep, is closed: the edge is left out of the cell graph the patrol is laid on. The part
//      kept is the start's own (where the character stands), else the one most doorways reach.
//      So a portal's nook is a dead end the patrol turns round before, and a corridor a portal
//      stands across is walked up to it from either side. Without floor data, a doorway the circle
//      covers (every spot a character passes through, half a metre in from its sides) is closed.
//   2. The route laid, a point inside a circle is moved out past its edge (on walkable floor, the
//      legs to its neighbours walkable) or dropped, and a leg passing through one is bent round it:
//      corners on a slightly bigger circle (every new leg a tangent of it), the shorter way round
//      first, each corner on walkable floor and every new leg walkable.
//   3. A leg no bend gets round: the cell it passes the portal in is cut off as well (never the
//      start's) and the patrol laid again, up to PortalMaxRounds times.
// The start: BuildDunPatrol walks the character out of a circle it stands in first (PortalWalkOut;
// the patrol is then built from there), then round any circle between it and the first point
// (PortalLeadIn). One log line per portal: PortalAvoidance.Describe.
// Patrol routes have no Portal points. A recorded route that walks into a portal on purpose (its
// portal step) is not laid here, and the navigation engine runs it as before.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

/// <summary>A portal the patrol keeps out of: a circle round it on its level (world metres).</summary>
internal sealed class PatrolPortal
{
    public uint   Id;
    public string Name = "";
    /// <summary>The cell it stands in (0 = not known: the cell under it is used).</summary>
    public uint   Cell;
    /// <summary>World position (x east, y north, z its base).</summary>
    public double X, Y, Z;
    /// <summary>Points and legs stay at least this far away (horizontally).</summary>
    public double Radius = DungeonPathfinder.PortalKeepOutDefault;
}

/// <summary>What the patrol build did about one portal (the log line BuildDunPatrol writes).</summary>
internal sealed class PortalAvoidance
{
    public PatrolPortal Portal = null!;
    /// <summary>Openings closed because the circle covers them.</summary>
    public readonly List<(uint A, uint B)> Closed = new();
    /// <summary>Cells cut off because a leg could not be bent round the portal there.</summary>
    public readonly List<uint> CutCells = new();
    public int Moved, Dropped, Bent;
    /// <summary>The character stood inside the circle when the patrol was built (led out first).</summary>
    public bool StartInside;
    /// <summary>The finished route's nearest point or leg on the portal's level (metres; +infinity: none).</summary>
    public double Nearest = double.PositiveInfinity;
    /// <summary>How many times the patrol was laid (1 + the rounds with cells cut off), and how long the
    /// whole portal-aware build took (the same for every portal of one build).</summary>
    public int Laid;
    public double BuildMs, SplitMs;

    public bool Clear => Nearest >= Portal.Radius - 0.05;

    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var p = Portal;
        string closed = Closed.Count == 0 ? "none" : string.Join(" ", Closed.Select(e => $"{e.A:X8}/{e.B:X8}"));
        string cut = CutCells.Count == 0 ? "" : "; cells cut off " + string.Join(" ", CutCells.Select(c => c.ToString("X8")));
        string near = double.IsPositiveInfinity(Nearest) ? "nothing on its level" : Nearest.ToString("F1", inv) + " m";
        return string.Format(inv,
            "portal 0x{0:X8} '{1}' ({2:F1},{3:F1},{4:F1}) cell {5:X8}, kept {6:F1} m out: openings closed {7}{8}; points moved {9}, dropped {10}; legs bent {11}{12}; nearest {13}{14}",
            p.Id, p.Name, p.X, p.Y, p.Z, p.Cell, p.Radius, closed, cut, Moved, Dropped, Bent,
            StartInside ? "; start inside it: walked out first" : "", near, Clear ? "" : " (STILL INSIDE: no way round found)")
            + string.Format(inv, " [patrol laid {0}x, {1:F0} ms, of it {2:F0} ms splitting rooms]", Laid, BuildMs, SplitMs);
    }
}

internal static partial class DungeonPathfinder
{
    /// <summary>The character touches a portal this far from its centre: the usual portal sphere
    /// (Setup 0x020005D3: 1.36 m) plus the character's (0.48 m), rounded up.</summary>
    internal const double PortalTouchMeters = 1.9;
    /// <summary>The least room past the touch distance: the steering's arrival ring (1.5 yd by
    /// default) cuts a corner by about that much, and the character drifts off the line a little.</summary>
    internal const double PortalSlackMeters = 1.6;
    /// <summary>The keep-out radius at the default nav point reach (1.5 yd): 3.5 m.</summary>
    internal const double PortalKeepOutDefault = PortalTouchMeters + PortalSlackMeters;
    /// <summary>A point or leg counts against a portal when its height is within this of the portal's base.</summary>
    internal const double PortalLevelBand = 3.0;
    /// <summary>Legs and openings: how many times the patrol is laid again with more cut off.</summary>
    internal const int PortalMaxRounds = 4;

    // A point moved out goes this far past the circle's edge, and at most PortalMoveMax from where it was.
    private const double PortalPointOut = 0.4, PortalMoveMax = 2.5;
    // Bend corners stand on circles this much bigger than the keep-out (the bigger first), every
    // PortalBendStepDeg degrees round at most.
    private static readonly double[] PortalBendOut = { 0.75, 0.3 };
    private const double PortalBendStepDeg = 40.0;
    // Openings: a character passes this far in from either side.
    private const double OpeningSideMargin = 0.5;
    // Corners and moved points keep this much clearance from walls (or the wall distance, if less);
    // new legs at least PortalLegWalkMin (walkable: on the floor, through no wall).
    private const double PortalCornerClearMin = 0.5, PortalLegWalkMin = 0.25;
    private const double PortalEps = 0.01;

    /// <summary>What the last BuildPatrolRoute with portals did about each (per thread; null without portals).</summary>
    [ThreadStatic] internal static List<PortalAvoidance>? LastPortalAvoidance;

    /// <summary>The keep-out radius for a nav point reach (FollowNavMin) or corner rounding of
    /// <paramref name="cutYards"/>: the touch distance plus that much (at least PortalSlackMeters).</summary>
    internal static double PortalKeepOutRadius(double cutYards)
        => PortalTouchMeters + Math.Max(PortalSlackMeters, (double.IsFinite(cutYards) ? cutYards : 0) + 0.1);

    // ── The build ────────────────────────────────────────────────────────────

    private static NavRouteParser BuildPatrolRouteAroundPortals(
        Dictionary<uint, DungeonNavNode> graph, uint startCell, IReadOnlySet<uint>? hazardCells,
        DungeonGeometry? geo, DungeonPatrolOptions o, IReadOnlyList<PatrolPortal> portals,
        (double X, double Y, double Z)? startAt)
    {
        foreach (var p in portals)
        {
            if (!(p.Radius > 0) || !double.IsFinite(p.Radius)) p.Radius = PortalKeepOutDefault;
            if (p.Cell == 0 || !graph.ContainsKey(p.Cell)) p.Cell = CellAt(graph, geo, WorldPoint(p.X, p.Y, p.Z, false));
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var reports = portals.Select(p => new PortalAvoidance { Portal = p }).ToList();
        var closed = PortalClosedOpenings(graph, geo, hazardCells, portals, startCell, startAt, reports);
        double splitMs = clock.Elapsed.TotalMilliseconds;
        int laid = 0;

        NavRouteParser route;
        Dictionary<uint, DungeonNavNode> g;
        for (int round = 0; ; round++)
        {
            g = WithoutOpenings(graph, closed, startCell);
            route = BuildPatrolRoute(g, startCell, hazardCells, geo, o);
            laid++;
            foreach (var r in reports) r.Moved = r.Dropped = r.Bent = 0;   // the last laying's
            var unresolved = KeepOutOfPortals(route.Points, closed: true, g, geo, portals, reports, o.WallClearance);
            if (unresolved.Count == 0 || round >= PortalMaxRounds) break;

            // Legs no bend gets round: cut off the cell there (never the start), and lay it again.
            bool more = false;
            foreach (var (k, cell) in unresolved)
            {
                uint cut = cell != 0 && cell != startCell ? cell
                         : portals[k].Cell != 0 && portals[k].Cell != startCell ? portals[k].Cell : 0;
                if (cut == 0 || !graph.TryGetValue(cut, out var node)) continue;
                bool any = false;
                foreach (uint nb in node.Neighbors) any |= closed.Add(EdgeKey(cut, nb));
                if (any && !reports[k].CutCells.Contains(cut)) reports[k].CutCells.Add(cut);
                more |= any;
            }
            if (!more) break;
        }
        foreach (var r in reports)
        {
            r.Nearest = NearestApproach(route.Points, closed: true, r.Portal);
            r.Laid = laid;
            r.BuildMs = clock.Elapsed.TotalMilliseconds;
            r.SplitMs = splitMs;
        }
        LastPortalAvoidance = reports;
        return route;
    }

    /// <summary>
    /// The doorways (graph edges between rooms) the patrol must not use because of the portals: each
    /// room a circle reaches is split by it (<see cref="SplitRoomByPortal"/>), and a doorway that
    /// only reaches a part of the room the patrol doesn't keep, or none, is closed. Without the map
    /// geometry (or for a room without floor data), a doorway the circle covers is closed
    /// (<see cref="OpeningCovered"/>; no opening known: the midpoint of the two cell centres).
    /// </summary>
    internal static HashSet<ulong> PortalClosedOpenings(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo,
                                                        IReadOnlySet<uint>? hazardCells, IReadOnlyList<PatrolPortal> portals,
                                                        uint startCell, (double X, double Y, double Z)? startAt,
                                                        List<PortalAvoidance>? reports = null)
    {
        var closed = new HashSet<ulong>();
        void Close(int k, uint a, uint b)
        {
            if (closed.Add(EdgeKey(a, b)) && reports != null && k < reports.Count) reports[k].Closed.Add((a, b));
        }

        if (geo == null || geo.DoorwayCount == 0)
        {
            foreach (var node in graph.Values)
                foreach (uint nb in node.Neighbors)
                {
                    if (nb <= node.CellId || !graph.TryGetValue(nb, out var other) || IsDropEdge(node, other)) continue;
                    for (int k = 0; k < portals.Count; k++)
                        if (OpeningCovered(geo, node, other, portals[k])) Close(k, node.CellId, nb);
                }
            return closed;
        }

        var rooms = BuildRooms(graph, geo, hazardCells);
        for (int k = 0; k < portals.Count; k++)
        {
            var p = portals[k];
            double reach = p.Radius + PortalFreeOut;
            for (int r = 0; r < rooms.Cells.Count; r++)
            {
                bool near = false;
                foreach (uint c in rooms.Cells[r])
                {
                    if (geo.TryCellFloorBounds(c, out double x0, out double y0, out double x1, out double y1))
                        near = p.X > x0 - reach && p.X < x1 + reach && p.Y > y0 - reach && p.Y < y1 + reach;
                    else if (graph.TryGetValue(c, out var n))
                        near = Dist(DungeonGeometry.NavToWorld(n.EW), DungeonGeometry.NavToWorld(n.NS), p.X, p.Y) < reach + 8.0;
                    if (near) break;
                }
                if (near) SplitRoomByPortal(graph, geo, rooms, r, p, startCell, startAt, (a, b) => Close(k, a, b));
            }
        }
        return closed;
    }

    // The free floor (outside the circle) is this much bigger than the keep-out, so the smaller
    // bend circle fits where the floor says the way round is.
    private const double PortalFreeOut = 0.3;
    // A spot of floor is free when a body this wide fits there (walkable floor this far to all four sides).
    private const double PortalBodyMeters = 0.45;
    // Doorway spots tried this far into the room.
    private const double PortalDoorInset = 0.6;

    /// <summary>
    /// Splits room <paramref name="room"/> by portal <paramref name="p"/>: its floor (the cells' floor
    /// polygons, on a 0.4 m grid) less the circle (where the floor is on the portal's level) and
    /// less a body's width along its edges falls into parts. Each doorway out of the room reaches the
    /// parts its passable spots (just inside the room) are in. The part kept is the one where the
    /// character stands (<paramref name="startAt"/>, the start's room), else the one most doorways
    /// reach (then the biggest); every doorway that doesn't reach it is closed. A room with no floor
    /// left keeps no doorway, unless the start is in it. Doorways whose spots have no floor data are
    /// judged by <see cref="OpeningCovered"/>.
    /// </summary>
    private static void SplitRoomByPortal(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, DungeonRooms rooms, int room,
                                          PatrolPortal p, uint startCell, (double X, double Y, double Z)? startAt, Action<uint, uint> close)
    {
        var cells = rooms.Cells[room];
        var doors = new List<(uint C, uint N)>();
        foreach (uint c in cells)
        {
            if (!graph.TryGetValue(c, out var n)) continue;
            foreach (uint nb in n.Neighbors)
                if (graph.TryGetValue(nb, out var o) && rooms.RoomOf.TryGetValue(nb, out int rn) && rn != room && !IsDropEdge(n, o))
                    doors.Add((c, nb));
        }
        if (doors.Count == 0) return;

        // The floor grid.
        var floored = new List<(uint Cell, double X0, double Y0, double X1, double Y1)>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (uint c in cells)
        {
            if (!geo.TryCellFloorBounds(c, out double x0, out double y0, out double x1, out double y1)) continue;
            floored.Add((c, x0, y0, x1, y1));
            minX = Math.Min(minX, x0); minY = Math.Min(minY, y0); maxX = Math.Max(maxX, x1); maxY = Math.Max(maxY, y1);
        }
        if (floored.Count == 0)
        {
            foreach (var (c, nb) in doors)
                if (OpeningCovered(geo, graph[c], graph[nb], p)) close(c, nb);
            return;
        }
        double step = 0.4;
        while ((maxX - minX) / step * ((maxY - minY) / step) > 90_000) step *= 1.25;
        int nx = (int)Math.Ceiling((maxX - minX) / step) + 1, ny = (int)Math.Ceiling((maxY - minY) / step) + 1;
        // Per spot: off the floor, a body doesn't fit, inside the circle, or free.
        const byte Off = 0, Tight = 1, InCircle = 2, Free = 3;
        var spot = new byte[nx * ny];
        double rf = p.Radius + PortalFreeOut;
        bool anyInCircle = false;
        for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++)
            {
                double x = minX + ix * step, y = minY + iy * step;
                bool on = false;
                double h = 0, best = double.MaxValue;
                foreach (var f in floored)
                {
                    if (x < f.X0 || x > f.X1 || y < f.Y0 || y > f.Y1) continue;
                    if (geo.TryCellFloorHeight(f.Cell, x, y, p.Z, out double fh) && Math.Abs(fh - p.Z) < best)
                    { best = Math.Abs(fh - p.Z); h = fh; on = true; }
                }
                int idx = iy * nx + ix;
                if (!on) { spot[idx] = Off; continue; }
                bool body = geo.OnWalkableFloor(x + PortalBodyMeters, y, h) && geo.OnWalkableFloor(x - PortalBodyMeters, y, h)
                         && geo.OnWalkableFloor(x, y + PortalBodyMeters, h) && geo.OnWalkableFloor(x, y - PortalBodyMeters, h);
                if (!body) { spot[idx] = Tight; continue; }
                bool inCircle = SameLevel(p, h) && Dist(x, y, p.X, p.Y) < rf;
                anyInCircle |= inCircle;
                spot[idx] = inCircle ? InCircle : Free;
            }
        if (!anyInCircle) return;   // the circle takes nothing a body could stand on here

        // The parts, four ways round: with the circle cut out, and (to tell what the portal does from
        // what the room is anyway: a neck too narrow for a body) without.
        int[] Parts(bool cut, out int count, out List<int> sizes)
        {
            var part = new int[spot.Length];
            for (int s = 0; s < part.Length; s++) part[s] = spot[s] == Free || (!cut && spot[s] == InCircle) ? int.MaxValue : -1;
            count = 0;
            sizes = new List<int>();
            var queue = new Queue<int>();
            for (int s = 0; s < part.Length; s++)
            {
                if (part[s] != int.MaxValue) continue;
                int id = count++, n = 0;
                part[s] = id; queue.Enqueue(s);
                while (queue.Count > 0)
                {
                    int q = queue.Dequeue(); n++;
                    int qx = q % nx, qy = q / nx;
                    if (qx > 0 && part[q - 1] == int.MaxValue) { part[q - 1] = id; queue.Enqueue(q - 1); }
                    if (qx + 1 < nx && part[q + 1] == int.MaxValue) { part[q + 1] = id; queue.Enqueue(q + 1); }
                    if (qy > 0 && part[q - nx] == int.MaxValue) { part[q - nx] = id; queue.Enqueue(q - nx); }
                    if (qy + 1 < ny && part[q + nx] == int.MaxValue) { part[q + nx] = id; queue.Enqueue(q + nx); }
                }
                sizes.Add(n);
            }
            return part;
        }
        var cutParts = Parts(true, out int parts, out var size);
        var wholeParts = Parts(false, out _, out _);

        // Which parts each doorway reaches, with and without the circle (null: no floor data at its spots).
        var reach = new List<HashSet<int>?>(doors.Count);
        var reachWhole = new List<HashSet<int>>(doors.Count);
        foreach (var (c, nb) in doors)
        {
            var spots = new List<(double X, double Y)>();
            if (geo.TryGetDoorway(c, nb, out var d))
            {
                var (ox, oy) = d.NormalFrom(c);   // out of the room
                double half = Math.Max(0, d.Width / 2 - OpeningSideMargin);
                int n = Math.Max(1, (int)Math.Ceiling(half / 0.3));
                for (int i = -n; i <= n; i++)
                {
                    double s = half * i / n;
                    spots.Add((d.X - ox * PortalDoorInset - oy * s, d.Y - oy * PortalDoorInset + ox * s));
                }
            }
            else
            {
                var a = graph[c]; var b = graph[nb];
                double ax = DungeonGeometry.NavToWorld(a.EW), ay = DungeonGeometry.NavToWorld(a.NS);
                double mx = (ax + DungeonGeometry.NavToWorld(b.EW)) * 0.5, my = (ay + DungeonGeometry.NavToWorld(b.NS)) * 0.5;
                double dl = Dist(ax, ay, mx, my);
                spots.Add(dl > PortalDoorInset ? (mx + (ax - mx) / dl * PortalDoorInset, my + (ay - my) / dl * PortalDoorInset) : (ax, ay));
            }
            HashSet<int>? got = null;
            var whole = new HashSet<int>();
            foreach (var (sx, sy) in spots)
            {
                int cx = (int)Math.Round((sx - minX) / step), cy = (int)Math.Round((sy - minY) / step);
                for (int ddy = -1; ddy <= 1; ddy++)
                    for (int ddx = -1; ddx <= 1; ddx++)
                    {
                        int gx = cx + ddx, gy = cy + ddy;
                        if (gx < 0 || gy < 0 || gx >= nx || gy >= ny) continue;
                        int at = gy * nx + gx;
                        if (spot[at] == Off) continue;
                        got ??= new HashSet<int>();
                        if (cutParts[at] >= 0) got.Add(cutParts[at]);
                        if (wholeParts[at] >= 0) whole.Add(wholeParts[at]);
                    }
            }
            reach.Add(got);
            reachWhole.Add(whole);
        }

        // The part kept.
        bool startHere = cells.Contains(startCell);
        int kept = -1;
        if (startHere && startAt is { } st)
        {
            double best = double.MaxValue;
            for (int q = 0; q < cutParts.Length; q++)
            {
                if (cutParts[q] < 0) continue;
                double dq = Dist(minX + (q % nx) * step, minY + (q / nx) * step, st.X, st.Y);
                if (dq < best) { best = dq; kept = cutParts[q]; }
            }
        }
        if (kept < 0)
        {
            var doorsOf = new int[parts];
            foreach (var got in reach) if (got != null) foreach (int v in got) doorsOf[v]++;
            for (int v = 0; v < parts; v++)
                if (kept < 0 || doorsOf[v] > doorsOf[kept] || (doorsOf[v] == doorsOf[kept] && size[v] > size[kept])) kept = v;
        }
        // The character's own part with no doorway (boxed in by the portal): only what the circle
        // covers is closed; it has to get out somehow.
        if (startHere && kept >= 0 && !reach.Any(g => g != null && g.Contains(kept))) kept = -1;
        int keptWhole = -1;
        if (kept >= 0)
            for (int q = 0; q < cutParts.Length && keptWhole < 0; q++)
                if (cutParts[q] == kept) keptWhole = wholeParts[q];

        for (int i = 0; i < doors.Count; i++)
        {
            var (c, nb) = doors[i];
            var got = reach[i];
            bool shut;
            if (got == null) shut = OpeningCovered(geo, graph[c], graph[nb], p);
            else if (got.Count == 0) shut = reachWhole[i].Count > 0;   // every spot it has is inside the circle
            else if (kept >= 0) shut = !got.Contains(kept) && reachWhole[i].Contains(keptWhole);   // cut off from the part kept by the circle
            else shut = !startHere;   // nothing of the room is left to keep
            if (shut) close(c, nb);
        }
    }

    private static bool OpeningCovered(DungeonGeometry? geo, DungeonNavNode a, DungeonNavNode b, PatrolPortal p)
    {
        if (geo != null && geo.TryGetDoorway(a.CellId, b.CellId, out var d))
        {
            if (!SameLevel(p, d.Z)) return false;
            double half = Math.Max(0, d.Width / 2 - OpeningSideMargin);
            double ux = -d.Ny, uy = d.Nx;   // along the opening
            int n = Math.Max(1, (int)Math.Ceiling(half / 0.25));
            for (int i = -n; i <= n; i++)
            {
                double s = half * i / n;
                if (Dist(d.X + ux * s, d.Y + uy * s, p.X, p.Y) >= p.Radius) return false;
            }
            return true;
        }
        double mx = (DungeonGeometry.NavToWorld(a.EW) + DungeonGeometry.NavToWorld(b.EW)) * 0.5;
        double my = (DungeonGeometry.NavToWorld(a.NS) + DungeonGeometry.NavToWorld(b.NS)) * 0.5;
        return SameLevel(p, (a.Z + b.Z) * 0.5) && Dist(mx, my, p.X, p.Y) < p.Radius;
    }

    /// <summary>The graph without the <paramref name="closed"/> edges (a copy where they were; the
    /// cached graph is never changed). The start keeps its edges when closing them all would leave
    /// it no way out: the character is already there.</summary>
    internal static Dictionary<uint, DungeonNavNode> WithoutOpenings(Dictionary<uint, DungeonNavNode> graph, HashSet<ulong> closed, uint startCell)
    {
        if (closed.Count == 0) return graph;
        bool keepStart = false;
        if (graph.TryGetValue(startCell, out var s))
        {
            int open = 0, any = 0;
            foreach (uint nb in s.Neighbors)
            {
                if (!graph.TryGetValue(nb, out var o) || IsDropEdge(s, o)) continue;
                any++;
                if (!closed.Contains(EdgeKey(startCell, nb))) open++;
            }
            keepStart = any > 0 && open == 0;
        }
        var g = new Dictionary<uint, DungeonNavNode>(graph.Count);
        foreach (var (id, n) in graph)
        {
            bool touched = false;
            foreach (uint nb in n.Neighbors)
                if (Closed(id, nb)) { touched = true; break; }
            if (!touched) { g[id] = n; continue; }
            var copy = new DungeonNavNode { CellId = n.CellId, NS = n.NS, EW = n.EW, Z = n.Z };
            foreach (uint nb in n.Neighbors) if (!Closed(id, nb)) copy.Neighbors.Add(nb);
            g[id] = copy;
        }
        return g;

        bool Closed(uint a, uint b) => closed.Contains(EdgeKey(a, b)) && !(keepStart && (a == startCell || b == startCell));
    }

    // ── Points and legs ──────────────────────────────────────────────────────

    /// <summary>
    /// Keeps the points and legs of <paramref name="pts"/> (a loop when <paramref name="closed"/>)
    /// out of every portal's circle: a point inside is moved out past the edge or dropped, a leg
    /// through one is bent round it. Returns the legs it could not bend: (portal index, the cell the
    /// leg passes the portal in).
    /// </summary>
    internal static List<(int Portal, uint Cell)> KeepOutOfPortals(List<NavPoint> pts, bool closed, Dictionary<uint, DungeonNavNode> graph,
                                                                 DungeonGeometry? geo, IReadOnlyList<PatrolPortal> portals,
                                                                 List<PortalAvoidance>? reports, double wall)
    {
        var unresolved = new List<(int, uint)>();
        if (portals.Count == 0 || pts.Count == 0) return unresolved;

        // Points inside a circle.
        for (int k = 0; k < portals.Count; k++)
        {
            var p = portals[k];
            for (int i = 0; i < pts.Count; i++)
            {
                var pt = pts[i];
                if (!NavRouteParser.IsPlainWaypoint(pt.Type) || !Inside(p, pt)) continue;
                NavPoint? prev = pts.Count > 1 && (i > 0 || closed) ? pts[(i - 1 + pts.Count) % pts.Count] : null;
                NavPoint? next = pts.Count > 1 && (i + 1 < pts.Count || closed) ? pts[(i + 1) % pts.Count] : null;
                if (TryMoveOut(geo, p, portals, pt, prev, next, wall, out double mx, out double my, out double mz))
                {
                    pt.EW = DungeonGeometry.WorldToNav(mx);
                    pt.NS = DungeonGeometry.WorldToNav(my);
                    pt.Z  = mz / 240.0;
                    NotePortal(pt, "moved-out-of-portal");
                    if (reports != null && k < reports.Count) reports[k].Moved++;
                }
                else
                {
                    pts.RemoveAt(i--);
                    if (reports != null && k < reports.Count) reports[k].Dropped++;
                    if (pts.Count == 0) return unresolved;
                }
            }
        }

        // Legs through a circle.
        var failed = new HashSet<(NavPoint, NavPoint, int)>();
        int budget = 8 * pts.Count + 64;
        int at = 0;
        while (pts.Count >= 2 && at < (closed ? pts.Count : pts.Count - 1))
        {
            var a = pts[at]; var b = pts[(at + 1) % pts.Count];
            int worst = -1; double worstD = double.MaxValue, worstT = 0;
            for (int k = 0; k < portals.Count; k++)
            {
                double d = LegDistance(portals[k], a, b, out double t);
                if (d < portals[k].Radius - PortalEps && d < worstD && !failed.Contains((a, b, k))) { worst = k; worstD = d; worstT = t; }
            }
            if (worst < 0) { at++; continue; }
            if (budget-- > 0 && TryBend(geo, a, b, portals[worst], portals, wall, out var corners))
            {
                pts.InsertRange(at + 1, corners);
                if (reports != null && worst < reports.Count) reports[worst].Bent++;
                continue;   // the first new leg next (another portal may be in its way)
            }
            failed.Add((a, b, worst));
            double ax = X(a), ay = Y(a), az = a.Z * 240.0;
            var spot = WorldPoint(ax + (X(b) - ax) * worstT, ay + (Y(b) - ay) * worstT, az + (b.Z * 240.0 - az) * worstT, false);
            unresolved.Add((worst, CellAt(graph, geo, spot)));
        }
        return unresolved;
    }

    /// <summary>
    /// Where the character at (px, py, pz) walks first when it stands inside a portal's circle:
    /// straight away from the portal (else turned up to 90 degrees either way) to just past the
    /// circle's edge, on walkable floor with some clearance, out of every circle. Null when it
    /// stands in none. BuildDunPatrol builds the patrol from there (its cell, its part of a room the
    /// portal splits). Tagged "portal-lead".
    /// </summary>
    internal static NavPoint? PortalWalkOut(DungeonGeometry? geo, IReadOnlyList<PatrolPortal> portals, double px, double py, double pz,
                                            double wall, List<PortalAvoidance>? reports = null)
    {
        var here = WorldPoint(px, py, pz, false);
        NavPoint? result = null;
        for (int pass = 0; pass < 3; pass++)   // out of one circle can be into the next (portals side by side)
        {
            int k = -1;
            for (int i = 0; i < portals.Count && k < 0; i++) if (Inside(portals[i], here)) k = i;
            if (k < 0) break;
            if (reports != null && k < reports.Count) reports[k].StartInside = true;
            var p = portals[k];
            double hx = X(here), hy = Y(here), hz = here.Z * 240.0, d = Dist(hx, hy, p.X, p.Y);
            double ux = d > 0.05 ? (hx - p.X) / d : 1, uy = d > 0.05 ? (hy - p.Y) / d : 0;
            double want = p.Radius + PortalPointOut;
            double ox = p.X + ux * want, oy = p.Y + uy * want, oz = hz;
            foreach (double deg in new[] { 0.0, 20, -20, 40, -40, 60, -60, 90, -90 })
            {
                double a = deg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
                double cx = p.X + (ux * c - uy * s) * want, cy = p.Y + (ux * s + uy * c) * want, cz = hz;
                if (geo != null)
                {
                    if (geo.TryFloorHeight(cx, cy, hz, 1.5, out double h)) cz = h;
                    if (!geo.OnWalkableFloor(cx, cy, cz) || geo.ClearanceMemo(cx, cy, cz, wall + 0.5) < Math.Min(wall, PortalCornerClearMin)) continue;
                    if (geo.LegClearance(hx, hy, hz, cx, cy, cz, 1.0, 0.3, out _, out _, out _) < 0.1) continue;   // through a wall
                }
                if (InsideAny(portals, cx, cy, cz)) continue;
                ox = cx; oy = cy; oz = cz;
                break;
            }
            here = Tag(WorldPoint(ox, oy, oz, false), "portal-lead");
            NotePortal(here, "out-of-portal");
            result = here;
        }
        return result;
    }

    /// <summary>
    /// The corners to walk between <paramref name="from"/> (the character, or where it walked out of
    /// a circle to) and the route's first point <paramref name="first"/> so the way there keeps out of
    /// the portals (bent round any circle in between). Empty when the straight walk is clear. Tagged
    /// "portal-lead".
    /// </summary>
    internal static List<NavPoint> PortalLeadIn(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo, IReadOnlyList<PatrolPortal> portals,
                                                NavPoint from, NavPoint first, double wall, List<PortalAvoidance>? reports = null)
    {
        var lead = new List<NavPoint>();
        var legs = new List<NavPoint> { from, first };
        KeepOutOfPortals(legs, closed: false, graph, geo, portals, reports, wall);
        foreach (var p in legs)
        {
            if (ReferenceEquals(p, from) || ReferenceEquals(p, first)) continue;
            Tag(p, "portal-lead");
            lead.Add(p);
        }
        return lead;
    }

    /// <summary>The least distance (metres) from the portal to the route's points and legs on its
    /// level: +infinity when none is.</summary>
    internal static double NearestApproach(List<NavPoint> pts, bool closed, PatrolPortal p)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < pts.Count; i++)
        {
            if (SameLevel(p, pts[i].Z * 240.0)) best = Math.Min(best, Dist(X(pts[i]), Y(pts[i]), p.X, p.Y));
            if (i + 1 < pts.Count || (closed && pts.Count > 1))
                best = Math.Min(best, LegDistance(p, pts[i], pts[(i + 1) % pts.Count], out _));
        }
        return best;
    }

    // A point inside the circle moved out to PortalPointOut past its edge: straight away from the
    // portal, else turned up to 90 degrees either way; on walkable floor with some clearance, out of
    // every circle, the legs to its neighbours walkable, at most PortalMoveMax from where it was.
    private static bool TryMoveOut(DungeonGeometry? geo, PatrolPortal p, IReadOnlyList<PatrolPortal> all, NavPoint pt,
                                   NavPoint? prev, NavPoint? next, double wall, out double mx, out double my, out double mz)
    {
        mx = my = mz = 0;
        double x0 = X(pt), y0 = Y(pt), z0 = pt.Z * 240.0;
        double d = Dist(x0, y0, p.X, p.Y), ux, uy;
        if (d > 0.05) { ux = (x0 - p.X) / d; uy = (y0 - p.Y) / d; }
        else
        {
            var toward = prev ?? next;
            double tx = toward != null ? X(toward) - p.X : 1, ty = toward != null ? Y(toward) - p.Y : 0, tl = Math.Sqrt(tx * tx + ty * ty);
            if (tl < 0.05) { tx = 1; ty = 0; tl = 1; }
            ux = tx / tl; uy = ty / tl;
        }
        double want = p.Radius + PortalPointOut;
        foreach (double deg in new[] { 0.0, 20, -20, 40, -40, 60, -60, 90, -90 })
        {
            double a = deg * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
            double vx = ux * c - uy * s, vy = ux * s + uy * c;
            double cx = p.X + vx * want, cy = p.Y + vy * want, cz = z0;
            if (Dist(cx, cy, x0, y0) > PortalMoveMax) continue;
            if (geo != null)
            {
                if (geo.TryFloorHeight(cx, cy, z0, 1.5, out double h)) cz = h;
                if (!geo.OnWalkableFloor(cx, cy, cz)) continue;
                if (geo.ClearanceMemo(cx, cy, cz, wall + 0.5) < Math.Min(wall, PortalCornerClearMin)) continue;
            }
            if (InsideAny(all, cx, cy, cz)) continue;
            if (geo != null)
            {
                var cand = WorldPoint(cx, cy, cz, false);
                if (prev != null && !LegWalkable(geo, prev, cand)) continue;
                if (next != null && !LegWalkable(geo, cand, next)) continue;
            }
            mx = cx; my = cy; mz = cz;
            return true;
        }

        // Too deep in for that: back along a leg to a neighbour, to where it leaves the circle (the
        // patrol turns round there instead), the nearer of the two.
        double bestMove = double.MaxValue;
        bool found = false;
        foreach (var nb in new[] { prev, next })
        {
            if (nb == null) continue;
            double bx = X(nb), by = Y(nb), bz = nb.Z * 240.0;
            double dx = bx - x0, dy = by - y0, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6 || Dist(bx, by, p.X, p.Y) < want) continue;   // the neighbour is inside too (it goes as well)
            // The first t along pt -> nb at distance `want` from the portal: |pt + t*u - P| = want.
            double fx = x0 - p.X, fy = y0 - p.Y, ex = dx / len, ey = dy / len;
            double bq = fx * ex + fy * ey, cq = fx * fx + fy * fy - want * want, disc = bq * bq - cq;
            if (disc < 0) continue;
            double t = -bq + Math.Sqrt(disc);
            if (t <= 0 || t > len) continue;
            double cx = x0 + ex * t, cy = y0 + ey * t, cz = z0 + (bz - z0) * (t / len);
            if (geo != null && geo.TryFloorHeight(cx, cy, cz, 1.5, out double h)) cz = h;
            if (InsideAny(all, cx, cy, cz) || t >= bestMove) continue;
            bestMove = t; mx = cx; my = cy; mz = cz; found = true;
        }
        return found;
    }

    // The leg a-b bent round the circle: corners on a circle a little bigger, each leg a tangent of it
    // (so every new leg keeps out), the shorter way round first; every corner on walkable floor with
    // some clearance and out of every circle, every new leg walkable.
    private static bool TryBend(DungeonGeometry? geo, NavPoint a, NavPoint b, PatrolPortal p, IReadOnlyList<PatrolPortal> all,
                                double wall, out List<NavPoint> corners)
    {
        corners = null!;
        double ax = X(a), ay = Y(a), az = a.Z * 240.0, bx = X(b), by = Y(b), bz = b.Z * 240.0;
        double da = Dist(ax, ay, p.X, p.Y), db = Dist(bx, by, p.X, p.Y);
        if (da < p.Radius - PortalEps || db < p.Radius - PortalEps) return false;   // an end inside: not a bend's job
        double angA = Math.Atan2(ay - p.Y, ax - p.X), angB = Math.Atan2(by - p.Y, bx - p.X);
        foreach (double extra in PortalBendOut)
        {
            double rb = p.Radius + extra;
            double offA = da > rb ? Math.Acos(rb / da) : 0, offB = db > rb ? Math.Acos(rb / db) : 0;
            var ways = new List<(int Side, double Start, double Sweep)>(2);
            foreach (int side in new[] { 1, -1 })
            {
                double start = angA + side * offA, end = angB - side * offB;
                double sweep = side > 0 ? Wrap2Pi(end - start) : Wrap2Pi(start - end);
                if (sweep > 1.5 * Math.PI) continue;   // all the way round: never the way
                ways.Add((side, start, sweep));
            }
            ways.Sort((u, v) => u.Sweep.CompareTo(v.Sweep));
            foreach (var (side, start, sweep) in ways)
            {
                int n = Math.Max(1, (int)Math.Ceiling(sweep / (PortalBendStepDeg * Math.PI / 180.0) - 1e-9));
                double step = sweep / n, r = rb / Math.Cos(step / 2);
                var list = new List<NavPoint>(n);
                bool ok = true;
                for (int j = 0; j < n && ok; j++)
                {
                    double ang = start + side * step * (j + 0.5);
                    double cx = p.X + Math.Cos(ang) * r, cy = p.Y + Math.Sin(ang) * r;
                    double zRef = az + (bz - az) * Along(ax, ay, bx, by, cx, cy), cz = zRef;
                    if (geo != null)
                    {
                        if (geo.TryFloorHeight(cx, cy, zRef, 1.5, out double h)) cz = h;
                        ok = geo.OnWalkableFloor(cx, cy, cz) && geo.ClearanceMemo(cx, cy, cz, wall + 0.5) >= Math.Min(wall, PortalCornerClearMin);
                    }
                    ok = ok && !InsideAny(all, cx, cy, cz);
                    if (ok) list.Add(WorldPoint(cx, cy, cz, false));
                }
                if (!ok) continue;
                NavPoint from = a;
                foreach (var c in list.Append(b))
                {
                    if (LegDistance(p, from, c, out _) < p.Radius - PortalEps || (geo != null && !LegWalkable(geo, from, c))) { ok = false; break; }
                    from = c;
                }
                if (!ok) continue;
                foreach (var c in list) { Tag(c, "portal-bend"); NotePortal(c, "round-portal"); }
                corners = list;
                return true;
            }
        }
        return false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool SameLevel(PatrolPortal p, double z) => Math.Abs(z - p.Z) <= PortalLevelBand;

    private static bool Inside(PatrolPortal p, NavPoint pt)
        => SameLevel(p, pt.Z * 240.0) && Dist(X(pt), Y(pt), p.X, p.Y) < p.Radius - PortalEps;

    /// <summary>The straight walk a-b keeps out of every portal's circle (and, with the map geometry,
    /// is walkable: on the floor, through no wall).</summary>
    internal static bool PortalLegClear(DungeonGeometry? geo, IReadOnlyList<PatrolPortal> portals, NavPoint a, NavPoint b)
    {
        foreach (var p in portals)
            if (LegDistance(p, a, b, out _) < p.Radius - PortalEps) return false;
        return geo == null || LegWalkable(geo, a, b);
    }

    /// <summary>(x, y, z) (world) is inside the portal's circle, on its level.</summary>
    internal static bool InsidePortal(PatrolPortal p, double x, double y, double z)
        => SameLevel(p, z) && Dist(x, y, p.X, p.Y) < p.Radius - PortalEps;

    private static bool InsideAny(IReadOnlyList<PatrolPortal> all, double x, double y, double z)
    {
        foreach (var q in all)
            if (SameLevel(q, z) && Dist(x, y, q.X, q.Y) < q.Radius - PortalEps) return true;
        return false;
    }

    /// <summary>The least horizontal distance from the portal to the leg a-b over the part of the leg
    /// on the portal's level (+infinity when no part of it is); t: where (0..1).</summary>
    internal static double LegDistance(PatrolPortal p, NavPoint a, NavPoint b, out double t)
    {
        double ax = X(a), ay = Y(a), az = a.Z * 240.0, bx = X(b), by = Y(b), bz = b.Z * 240.0;
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        t = l2 < 1e-12 ? 0 : Math.Clamp(((p.X - ax) * dx + (p.Y - ay) * dy) / l2, 0, 1);
        double d = Dist(ax + dx * t, ay + dy * t, p.X, p.Y);
        if (SameLevel(p, az + (bz - az) * t)) return d;
        if (SameLevel(p, az) == SameLevel(p, bz) && !SameLevel(p, az) && (az - p.Z) * (bz - p.Z) > 0) return double.PositiveInfinity;   // all of it above or below
        // Off its level at the nearest spot: the nearest spot of the leg that is on it (a ramp).
        int n = Math.Clamp((int)Math.Ceiling(Math.Sqrt(l2) / 0.5), 8, 200);
        double best = double.PositiveInfinity, bestT = t;
        for (int i = 0; i <= n; i++)
        {
            double s = (double)i / n;
            if (!SameLevel(p, az + (bz - az) * s)) continue;
            double ds = Dist(ax + dx * s, ay + dy * s, p.X, p.Y);
            if (ds < best) { best = ds; bestT = s; }
        }
        t = bestT;
        return best;
    }

    // On the floor and through no wall: the leg's least clearance at least PortalLegWalkMin.
    private static bool LegWalkable(DungeonGeometry geo, NavPoint a, NavPoint b)
        => geo.LegClearance(X(a), Y(a), a.Z * 240.0, X(b), Y(b), b.Z * 240.0, 1.0, 0.3, out _, out _, out _) >= PortalLegWalkMin;

    private static void NotePortal(NavPoint p, string why)
    {
        if (PointLog == null) return;
        var info = Info(p);
        info.Why = info.Why.Length > 0 ? info.Why + "," + why : why;
    }

    // Where (0..1) the point (x, y) projects onto the segment a-b.
    private static double Along(double ax, double ay, double bx, double by, double x, double y)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        return l2 < 1e-12 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1);
    }

    private static double Wrap2Pi(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }

    private static double Dist(double ax, double ay, double bx, double by) => Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
    private static double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
    private static double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);
}

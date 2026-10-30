using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;
using V3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;

namespace RynthCore.RynthAiHostTests.Tests;

// Dungeon routes laid on real openings (DungeonGeometry, DungeonPathfinder.Rooms.cs), on small
// hand-built dungeons: cells as floor rectangles, openings as portal polygons in both cells,
// an optional wall. World units (metres), x east, y north, floors at z = 0.
//   - a doorway gets an approach point centred in front of it and an exit point centred
//     beyond it, flagged NavPoint.Doorway;
//   - openings close together in a row (thin joint cells) are one run: nothing doubles back;
//   - a 3x3 room joined by wide openings is one room, crossed straight from door to door
//     (the old cell walk zig-zagged through it); an L-shaped room or a pillar bends the
//     crossing round the corner;
//   - the patrol over such a dungeon puts no point inside the big room and still goes
//     through every doorway; a big dead-end room is looked into;
//   - ceilings are not floors;
//   - every point keeps 1 m from walls and floor edges where the space allows (pushed off a
//     wall, centred in a narrow corridor, a doorway point moved back along its centre line,
//     a crypt bend, a dead end's look-in point out of its end cap), and a leg cutting an
//     inside corner gets a corner.
internal static class PatrolDoorwayTests
{
    // 2026-10-10, 0x6146: hazards cut the loops and the patrol fell back to every dead end. Now,
    // in that case, only small dead ends (closets) go: a junction J with a 1-cell closet, a 3-cell
    // spur and a 5-cell branch hanging off a loop. Since 2026-10-13 whole dead-end areas
    // (TrimDeadEndPockets, PatrolPocketTests): the branch is walked until 3 cells are left.
    private static void TrimSmallDeadEnds()
    {
        var g = new Dictionary<uint, DungeonNavNode>();
        void Link(uint a, uint b)
        {
            foreach (uint c in new[] { a, b })
                if (!g.ContainsKey(c)) g[c] = new DungeonNavNode { CellId = c, NS = (c & 0xFF) * 0.01, EW = (c >> 8 & 0xFF) * 0.01 };
            g[a].Neighbors.Add(b); g[b].Neighbors.Add(a);
        }
        // Loop 1-2-3-4, J = 1.
        Link(0x01, 0x02); Link(0x02, 0x03); Link(0x03, 0x04); Link(0x04, 0x01);
        Link(0x01, 0x10);                                                // closet
        Link(0x01, 0x20); Link(0x20, 0x21); Link(0x21, 0x22);            // 3-cell spur
        Link(0x02, 0x30); Link(0x30, 0x31); Link(0x31, 0x32); Link(0x32, 0x33); Link(0x33, 0x34); // 5-cell branch
        var all = new HashSet<uint>(g.Keys);
        var kept = DungeonPathfinder.TrimDeadEndPockets(g, all, 0x03, 3);
        Check.False(kept.Contains(0x10), "the 1-cell closet goes");
        Check.False(kept.Overlaps(new uint[] { 0x20, 0x21, 0x22 }), "the 3-cell spur goes");
        Check.True(kept.IsSupersetOf(new uint[] { 0x30, 0x31 }) && !kept.Overlaps(new uint[] { 0x32, 0x33, 0x34 }),
                   "the 5-cell branch is walked until 3 cells are left");
        Check.True(kept.IsSupersetOf(new uint[] { 1, 2, 3, 4 }), "the loop stays");
        var keepStart = DungeonPathfinder.TrimDeadEndPockets(g, all, 0x10, DungeonPathfinder.PatrolMaxDeadEndCells);
        Check.True(keepStart.Contains(0x10), "the start cell is never trimmed");
    }

    public static void Register(Runner r)
    {
        r.Add("dungeon doorways: an opening's centre, facing and width come from its portal polygons", DoorwayFromPortals);
        r.Add("dungeon doorways: approach and exit points centred on the opening, on its centre line, flagged", ApproachAndExit);
        r.Add("dungeon doorways: openings 0.7 m apart (joint cells) are one run, nothing doubles back", JointRun);
        r.Add("dungeon rooms: cells joined by wide openings, or with one origin, are one room; hazards never merge", RoomsMerge);
        r.Add("dungeon rooms: a big room is crossed straight from door to door (old walk: 12 points)", BigRoomCrossedDirectly);
        r.Add("dungeon rooms: an L-shaped room's crossing goes round the corner, on the floor", LShapedRoom);
        r.Add("dungeon rooms: a pillar in the way bends the crossing round it", PillarInTheWay);
        r.Add("dungeon patrol: a loop through a big room: no points inside it, every doorway taken", PatrolLoop);
        r.Add("dungeon patrol: a big dead-end room is looked into, not skirted (and not when under Walk Into Rooms From)", PatrolLooksIntoDeadEndRoom);
        r.Add("dungeon patrol settings: Distance From Walls 0.5 / 1 / 1.5 / 2 m kept by every point", PatrolWallDistanceSetting);
        r.Add("dungeon floors: a ceiling (facing down) is not a floor", CeilingIsNotFloor);
        r.Add("dungeon patrol: dead ends of up to 3 cells trimmed, a longer one walked until 3 are left", TrimSmallDeadEnds);
        r.Add("dungeon clearance: walls, floor edges and slopes too steep to stand on bound it; a face high above doesn't", ClearanceMeasure);
        r.Add("dungeon clearance: a point next to a wall (or past the floor's edge) is pushed out to 1 m", PushedOffWall);
        r.Add("dungeon clearance: a point in a corridor narrower than 2 m ends on its middle line", NarrowCorridorCentred);
        r.Add("dungeon doorways: a doorway point near the far wall of a T junction moves back along the centre line", DoorwayPointMovesAlongCentreLine);
        r.Add("dungeon doorways: in a short chamfered bend (crypt corner) the points come off the bend's outer wall", ChamferedBend);
        r.Add("dungeon patrol: a dead end's look-in point comes out of the end cap onto the floor", DeadEndLookInOnFloor);
        r.Add("dungeon legs: a leg cutting an inside corner gets a corner put in", LegAroundInsideCorner);
        r.Add("dungeon patrol (sweep): a pillar hall is walked in lanes, every cell within sight of the route; far fewer points than Whole dungeon", SweepPillarHall);
        r.Add("dungeon patrol (sweep): a wall blocks sight, so two corridors side by side are both walked", SweepWallBlocksSight);
    }

    // ── Fixture builder ──────────────────────────────────────────────────────

    private sealed class Dungeon
    {
        public readonly Dictionary<uint, DungeonNavNode> Graph = new();
        public readonly List<(uint, V3[])> Portals = new();
        public readonly List<(uint, V3[])> Floors = new();
        public readonly List<(uint, V3[])> Walls = new();
        private uint _next = 0x63460100;

        /// <summary>A cell whose floor is the rectangle (x0,y0)-(x1,y1); its origin is the middle.</summary>
        public uint Cell(double x0, double y0, double x1, double y1)
        {
            uint id = _next++;
            Graph[id] = new DungeonNavNode
            {
                CellId = id,
                EW = DungeonGeometry.WorldToNav((x0 + x1) / 2),
                NS = DungeonGeometry.WorldToNav((y0 + y1) / 2),
                Z = 0,
            };
            Floors.Add((id, Rect(x0, y0, x1, y1, 0)));
            return id;
        }

        /// <summary>An opening between a and b on the vertical line x (from y0 to y1).</summary>
        public void OpenX(uint a, uint b, double x, double y0, double y1) => Open(a, b, VerticalQuad(x, y0, x, y1));

        /// <summary>An opening between a and b on the horizontal line y (from x0 to x1).</summary>
        public void OpenY(uint a, uint b, double y, double x0, double x1) => Open(a, b, VerticalQuad(x0, y, x1, y));

        private void Open(uint a, uint b, V3[] quad)
        {
            Graph[a].Neighbors.Add(b);
            Graph[b].Neighbors.Add(a);
            Portals.Add((a, quad));
            Portals.Add((b, quad.Reverse().ToArray()));   // the other cell's copy, wound the other way
        }

        public void Wall(uint cell, double x0, double y0, double x1, double y1) => Walls.Add((cell, VerticalQuad(x0, y0, x1, y1)));

        /// <summary>A cell whose origin is (ox, oy) and whose floor is the given polygon (counter-clockwise from above).</summary>
        public uint CellPoly(double ox, double oy, params (double X, double Y, double Z)[] floor)
        {
            uint id = _next++;
            Graph[id] = new DungeonNavNode { CellId = id, EW = DungeonGeometry.WorldToNav(ox), NS = DungeonGeometry.WorldToNav(oy), Z = 0 };
            Floors.Add((id, floor.Select(v => new V3((float)v.X, (float)v.Y, (float)v.Z)).ToArray()));
            return id;
        }

        public DungeonGeometry Geo() => DungeonGeometry.Build(Graph, Portals, Floors, Walls);
    }

    private static V3[] Rect(double x0, double y0, double x1, double y1, double z) => new[]
    {
        new V3((float)x0, (float)y0, (float)z), new V3((float)x1, (float)y0, (float)z),
        new V3((float)x1, (float)y1, (float)z), new V3((float)x0, (float)y1, (float)z),
    };

    private static V3[] VerticalQuad(double x0, double y0, double x1, double y1) => new[]
    {
        new V3((float)x0, (float)y0, 0f), new V3((float)x1, (float)y1, 0f),
        new V3((float)x1, (float)y1, 3f), new V3((float)x0, (float)y0, 3f),
    };

    // A 10 x 10 hall of 5 m cells joined by 3 m openings (doorways, so 100 rooms): to the patrol,
    // what the 0x00AE pillar hall is (2026-10-14).
    private static (Dungeon D, uint[,] Cells) PillarHall(int n = 10, double size = 5)
    {
        var d = new Dungeon();
        var c = new uint[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                c[i, j] = d.Cell(i * size, j * size, (i + 1) * size, (j + 1) * size);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double x0 = i * size, y0 = j * size;
                if (i + 1 < n) d.OpenX(c[i, j], c[i + 1, j], x0 + size, y0 + 1, y0 + 4);
                if (j + 1 < n) d.OpenY(c[i, j], c[i, j + 1], y0 + size, x0 + 1, x0 + 4);
            }
        return (d, c);
    }

    private static void SweepPillarHall()
    {
        var (d, c) = PillarHall();
        var geo = d.Geo();
        var whole = DungeonPathfinder.BuildPatrolRoute(d.Graph, c[0, 0], null, geo);
        var sweep = DungeonPathfinder.BuildPatrolRoute(d.Graph, c[0, 0], null, geo,
            new DungeonPatrolOptions { Coverage = PatrolCoverage.Sweep, SightMeters = 15 });
        Check.True(sweep.Points.Count > 0 && sweep.Points.Count * 2 < whole.Points.Count,
            $"sweep {sweep.Points.Count} points, whole dungeon {whole.Points.Count}");
        Check.Eq(DungeonPathfinder.LastSweepSeen, DungeonPathfinder.LastSweepCells, "every cell is seen by a stop");
        Check.True(DungeonPathfinder.LastSweepStops > 1 && DungeonPathfinder.LastSweepStops < 40, $"{DungeonPathfinder.LastSweepStops} stops for 100 cells");
        // Every cell's middle within the sight distance of the route (its points or the legs between them).
        double worst = 0;
        foreach (var node in d.Graph.Values)
        {
            double cx = DungeonGeometry.NavToWorld(node.EW), cy = DungeonGeometry.NavToWorld(node.NS), best = double.MaxValue;
            for (int i = 0; i < sweep.Points.Count; i++)
            {
                var a = sweep.Points[i]; var b = sweep.Points[(i + 1) % sweep.Points.Count];
                best = Math.Min(best, SegDist(cx, cy, X(a), Y(a), X(b), Y(b)));
            }
            worst = Math.Max(worst, best);
        }
        // The route goes through a stop's doorways, not its exact middle: within half a cell of the sight.
        Check.True(worst <= 15.0 + 2.5, $"the farthest cell is {worst:F1} m from the route (sight 15, cells 5 m)");
        int stops15 = DungeonPathfinder.LastSweepStops;
        DungeonPathfinder.BuildPatrolRoute(d.Graph, c[0, 0], null, geo,
            new DungeonPatrolOptions { Coverage = PatrolCoverage.Sweep, SightMeters = 30 });
        Check.True(DungeonPathfinder.LastSweepStops < stops15, $"a longer sight, fewer stops ({DungeonPathfinder.LastSweepStops} vs {stops15})");
    }

    private static void SweepWallBlocksSight()
    {
        // Two corridors of 8 cells (y 0..5 and 5..10), joined only at their two ends, a wall all along
        // between them. Without the wall one corridor's walk would see the other at 15 m.
        var d = new Dungeon();
        var a = new uint[8]; var b = new uint[8];
        for (int i = 0; i < 8; i++) { a[i] = d.Cell(i * 5, 0, i * 5 + 5, 5); b[i] = d.Cell(i * 5, 5, i * 5 + 5, 10); }
        for (int i = 0; i + 1 < 8; i++) { d.OpenX(a[i], a[i + 1], i * 5 + 5, 1, 4); d.OpenX(b[i], b[i + 1], i * 5 + 5, 6, 9); }
        d.OpenY(a[0], b[0], 5, 1, 4);
        d.OpenY(a[7], b[7], 5, 36, 39);
        for (int i = 1; i < 7; i++) { d.Wall(a[i], i * 5, 5, i * 5 + 5, 5); d.Wall(b[i], i * 5, 5, i * 5 + 5, 5); }
        var geo = d.Geo();
        var sweep = DungeonPathfinder.BuildPatrolRoute(d.Graph, a[0], null, geo,
            new DungeonPatrolOptions { Coverage = PatrolCoverage.Sweep, SightMeters = 15 });
        Check.Eq(DungeonPathfinder.LastSweepSeen, DungeonPathfinder.LastSweepCells, "every cell seen");
        // A leg along each corridor through its middle (x 20).
        bool Leg(Func<double, bool> side)
        {
            for (int i = 0; i < sweep.Points.Count; i++)
            {
                var p = sweep.Points[i]; var q = sweep.Points[(i + 1) % sweep.Points.Count];
                if (Math.Min(X(p), X(q)) < 20 && Math.Max(X(p), X(q)) > 20 && side(Y(p)) && side(Y(q))) return true;
            }
            return false;
        }
        bool south = Leg(y => y < 5), north = Leg(y => y > 5);
        Check.True(south && north, "both corridors walked in the middle: " + string.Join(" ", sweep.Points.Select(p => $"({X(p):F0},{Y(p):F0})")));
    }

    private static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-9 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        double qx = ax + dx * t - px, qy = ay + dy * t - py;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    private static double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
    private static double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);

    private static List<NavPoint> Path(Dungeon d, DungeonGeometry? geo, double sx, double sy, double dx, double dy, params uint[] cells)
        => DungeonPathfinder.BuildPathPoints(cells.ToList(), d.Graph, geo, null,
            DungeonGeometry.WorldToNav(sy), DungeonGeometry.WorldToNav(sx), 0,
            DungeonGeometry.WorldToNav(dy), DungeonGeometry.WorldToNav(dx), 0, out _);

    // Every leg (start, points..., dest) stays on the floor of the given cells and clear of walls.
    private static bool LegsClear(DungeonGeometry geo, IReadOnlyCollection<uint> cells, double sx, double sy, List<NavPoint> pts, double dx, double dy)
    {
        var xs = new List<(double X, double Y)> { (sx, sy) };
        xs.AddRange(pts.Select(p => (X(p), Y(p))));
        xs.Add((dx, dy));
        for (int i = 0; i + 1 < xs.Count; i++)
            if (!geo.SegmentClear(cells, xs[i].X, xs[i].Y, 0, xs[i + 1].X, xs[i + 1].Y, 0, 0.45)) return false;
        return true;
    }

    // ── A room (x 0..10, y 0..10) and a corridor east of it (x 10..20, 5.3 m wide) ────

    private static (Dungeon D, uint Room, uint Corr) RoomAndCorridor()
    {
        var d = new Dungeon();
        uint room = d.Cell(0, 0, 10, 10);
        uint corr = d.Cell(10, 2.35, 20, 7.65);
        d.OpenX(room, corr, 10, 2.35, 7.65);
        return (d, room, corr);
    }

    private static void DoorwayFromPortals()
    {
        var (d, room, corr) = RoomAndCorridor();
        var geo = d.Geo();
        Check.True(geo.TryGetDoorway(room, corr, out var door), "the opening is matched from the two portal polygons");
        Check.Near(door.X, 10, 1e-4, "centre x");
        Check.Near(door.Y, 5, 1e-4, "centre y (middle of the opening)");
        Check.Near(door.Z, 0, 1e-4, "at floor level (its lowest vertex), not halfway up");
        Check.Near(door.Width, 5.3, 1e-3, "width");
        Check.False(door.IsWide, "5.3 m is a doorway, not a room joint");
        var (nx, ny) = door.NormalFrom(room);
        Check.Near(nx, 1, 1e-6, "faces from the room into the corridor (east)");
        Check.Near(ny, 0, 1e-6, "faces due east");
        Check.True(geo.TryGetDoorway(corr, room, out _), "found from either side");
    }

    private static void ApproachAndExit()
    {
        var (d, room, corr) = RoomAndCorridor();
        var geo = d.Geo();
        // From the room's far corner to the corridor's end.
        var pts = Path(d, geo, 1, 9, 18, 5, room, corr);
        Check.Eq(pts.Count, 2, "an approach and an exit point, nothing else (the room is open)");
        Check.Near(X(pts[0]), 6, 1e-3, "approach 4 m in front of the opening");
        Check.Near(Y(pts[0]), 5, 1e-3, "approach centred on the opening");
        Check.Near(X(pts[1]), 14, 1e-3, "exit 4 m beyond it");
        Check.Near(Y(pts[1]), 5, 1e-3, "exit centred on the opening");
        Check.True(pts.All(p => p.Doorway), "both flagged as doorway points");

        // The old cell walk: 30% / 50% of the way between the cell centres, no flag.
        var old = Path(d, null, 1, 9, 18, 5, room, corr);
        Check.False(old.Any(p => p.Doorway), "without geometry: the old points, unflagged");

        // An opening off the line between the cell centres: the points still sit on its centre line.
        var d2 = new Dungeon();
        uint big = d2.Cell(0, 0, 10, 20);
        uint side = d2.Cell(10, 13, 20, 18);
        d2.OpenX(big, side, 10, 13, 18);
        var p2 = Path(d2, d2.Geo(), 2, 2, 18, 15.5, big, side);
        Check.Near(Y(p2[0]), 15.5, 1e-3, "approach on the opening's centre line (y 15.5), not the cell centres' line");
        Check.Near(Y(p2[1]), 15.5, 1e-3, "exit on it too");
    }

    private static void JointRun()
    {
        // Room, two 0.7 m joint cells, corridor: openings at x = 10, 10.7, 11.4.
        var d = new Dungeon();
        uint room = d.Cell(0, 0, 10, 10);
        uint j1 = d.Cell(10, 2.35, 10.7, 7.65);
        uint j2 = d.Cell(10.7, 2.35, 11.4, 7.65);
        uint corr = d.Cell(11.4, 2.35, 21.4, 7.65);
        d.OpenX(room, j1, 10, 2.35, 7.65);
        d.OpenX(j1, j2, 10.7, 2.35, 7.65);
        d.OpenX(j2, corr, 11.4, 2.35, 7.65);
        var pts = Path(d, d.Geo(), 2, 5, 20, 5, room, j1, j2, corr);
        var xs = pts.Select(X).ToList();
        Check.True(xs.Zip(xs.Skip(1), (a, b) => b > a).All(b => b), $"x only increases ({string.Join(", ", xs.Select(x => x.ToString("F2")))})");
        Check.Eq(xs.Count(x => x > 9.9 && x < 11.5), 0, "no point among the joints");
        Check.Near(xs.First(), 6, 1e-3, "one approach, 4 m in front of the first opening");
        Check.Near(xs.Last(), 15.4, 1e-3, "one exit, 4 m beyond the last");
        Check.True(pts.All(p => Math.Abs(Y(p) - 5) < 1e-3), "all on the centre line");
    }

    // ── A 3x3 room of 10 m cells (x 0..30, y 0..30), wide openings inside ────────

    private static (Dungeon D, uint[,] C) BigRoom()
    {
        var d = new Dungeon();
        var c = new uint[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                c[i, j] = d.Cell(i * 10, j * 10, i * 10 + 10, j * 10 + 10);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                if (i < 2) d.OpenX(c[i, j], c[i + 1, j], i * 10 + 10, j * 10, j * 10 + 10);
                if (j < 2) d.OpenY(c[i, j], c[i, j + 1], j * 10 + 10, i * 10, i * 10 + 10);
            }
        return (d, c);
    }

    private static void RoomsMerge()
    {
        var (d, c) = BigRoom();
        uint west = d.Cell(-10, 22.35, 0, 27.65);
        d.OpenX(west, c[0, 2], 0, 22.35, 27.65);
        var geo = d.Geo();
        var rooms = DungeonPathfinder.BuildRooms(d.Graph, geo);
        int r = rooms.RoomOf[c[1, 1]];
        Check.Eq(rooms.Cells[r].Count, 9, "the nine cells are one room");
        Check.True(rooms.RoomOf[west] != r, "the corridor behind a doorway is a room of its own");

        var hazard = new HashSet<uint> { c[1, 1] };
        var withHazard = DungeonPathfinder.BuildRooms(d.Graph, geo, hazard);
        Check.Eq(withHazard.Cells[withHazard.RoomOf[c[1, 1]]].Count, 1, "a hazard cell stays a room of its own");
        Check.Eq(withHazard.Cells[withHazard.RoomOf[c[0, 0]]].Count, 8, "the rest still join round it");

        // Pieces of one junction: two cells with one origin, joined by a narrow opening.
        var j = new Dungeon();
        uint p1 = j.Cell(0, 0, 10, 10);
        uint p2 = j.Cell(2, 2, 8, 8);            // same middle (5, 5)
        uint off = j.Cell(10, 2.35, 20, 7.65);
        j.OpenX(p1, off, 10, 2.35, 7.65);
        j.OpenY(p1, p2, 2, 2.35, 7.65);
        var jr = DungeonPathfinder.BuildRooms(j.Graph, j.Geo());
        Check.Eq(jr.RoomOf[p1], jr.RoomOf[p2], "cells with the same origin are one junction (one room)");
        Check.True(jr.RoomOf[off] != jr.RoomOf[p1], "a neighbour through a doorway is not");
    }

    private static (Dungeon D, uint[,] C, uint West, uint East) BigRoomWithDoors()
    {
        var (d, c) = BigRoom();
        uint west = d.Cell(-10, 22.35, 0, 27.65);   // doorway into the room's north-west cell
        uint east = d.Cell(30, 2.35, 40, 7.65);      // doorway out of its south-east cell
        d.OpenX(west, c[0, 2], 0, 22.35, 27.65);
        d.OpenX(c[2, 0], east, 30, 2.35, 7.65);
        return (d, c, west, east);
    }

    private static void BigRoomCrossedDirectly()
    {
        var (d, c, west, east) = BigRoomWithDoors();
        var geo = d.Geo();
        // A* style cell path that zig-zags through the room.
        uint[] cells = { west, c[0, 2], c[1, 2], c[1, 1], c[2, 1], c[2, 0], east };
        var pts = Path(d, geo, -8, 25, 38, 5, cells);
        Check.Eq(pts.Count, 4, "approach + exit at each doorway, nothing in between");
        Check.Near(X(pts[1]), 4, 1e-3, "in through the west doorway");
        Check.Near(X(pts[2]), 26, 1e-3, "then straight to the east doorway's approach");
        Check.Near(Y(pts[2]), 5, 1e-3, "centred on it");

        var old = Path(d, null, -8, 25, 38, 5, cells);
        Check.Eq(old.Count, 12, "the old walk: two points per cell step");
    }

    private static void LShapedRoom()
    {
        var d = new Dungeon();
        uint c00 = d.Cell(0, 0, 10, 10), c01 = d.Cell(0, 10, 10, 20), c10 = d.Cell(10, 0, 20, 10);
        d.OpenY(c00, c01, 10, 0, 10);
        d.OpenX(c00, c10, 10, 0, 10);
        uint west = d.Cell(-10, 12.35, 0, 17.65), east = d.Cell(20, 2.35, 30, 7.65);
        d.OpenX(west, c01, 0, 12.35, 17.65);
        d.OpenX(c10, east, 20, 2.35, 7.65);
        var geo = d.Geo();
        var pts = Path(d, geo, -8, 15, 28, 5, west, c01, c00, c10, east);
        var room = new[] { c00, c01, c10 };
        var turns = pts.Where(p => !p.Doorway).ToList();
        Check.Eq(turns.Count, 1, "one turn point inside the room besides the doorway points");
        // The east door's approach point (16, 5) is in line with the turn point and the exit
        // beyond the door, so it is dropped as collinear; the legs are checked to it.
        Check.True(LegsClear(geo, room, 4, 15, turns, 16, 5), "both legs across the room stay on its floor");
        Check.False(geo.SegmentClear(room, 4, 15, 0, 16, 5, 0, 0.45), "the straight line would have cut the inside corner");
        Check.Near(Y(pts[^1]), 5, 1e-3, "out through the east door on its centre line");
    }

    private static void PillarInTheWay()
    {
        var (d, c, west, east) = BigRoomWithDoors();
        // A wall across the room's middle, right on the diagonal from door to door.
        d.Wall(c[1, 1], 11, 19, 19, 11);
        var geo = d.Geo();
        var room = Enumerable.Range(0, 9).Select(k => c[k / 3, k % 3]).ToArray();
        var pts = Path(d, geo, -8, 25, 38, 5, west, c[0, 2], c[1, 2], c[1, 1], c[2, 1], c[2, 0], east);
        var turns = pts.Where(p => !p.Doorway).ToList();
        Check.True(turns.Count >= 1, "a turn point to get round the wall");
        Check.False(geo.SegmentClear(room, 4, 25, 0, 26, 5, 0, 0.45), "the straight line would hit the wall");
        Check.True(LegsClear(geo, room, 4, 25, turns, 26, 5), "every leg across the room misses the wall");
    }

    // ── Patrol ───────────────────────────────────────────────────────────────

    // The big room with a corridor loop round its north side: west door -> up, along the top,
    // down the east side -> a corner cell -> the east door's corridor.
    private static (Dungeon D, uint[,] C, uint West, uint Top2, uint East) LoopDungeon()
    {
        var (d, c, west, east) = BigRoomWithDoors();
        uint up1 = d.Cell(-10, 27.65, -4.7, 37.65);
        d.OpenY(west, up1, 27.65, -10, -4.7);
        uint top1 = d.Cell(-10, 37.65, 10, 42.95);
        d.OpenY(up1, top1, 37.65, -10, -4.7);
        uint top2 = d.Cell(10, 37.65, 30, 42.95);
        d.OpenX(top1, top2, 10, 37.65, 42.95);
        uint top3 = d.Cell(30, 37.65, 45.3, 42.95);
        d.OpenX(top2, top3, 30, 37.65, 42.95);
        uint down1 = d.Cell(40, 7.65, 45.3, 37.65);
        d.OpenY(down1, top3, 37.65, 40, 45.3);
        uint corner = d.Cell(40, 2.35, 45.3, 7.65);
        d.OpenY(corner, down1, 7.65, 40, 45.3);
        d.OpenX(east, corner, 40, 2.35, 7.65);
        return (d, c, west, top2, east);
    }

    private static void PatrolLoop()
    {
        var (d, c, west, _, _) = LoopDungeon();
        var geo = d.Geo();
        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo);
        Check.True(route.Points.Count > 0, "a patrol");
        Check.Eq(route.RouteType, NavRouteType.Circular, "circular");
        int inside = route.Points.Count(p => X(p) > 4.5 && X(p) < 25.5 && Y(p) > 4.5 && Y(p) < 25.5);
        Check.Eq(inside, 0, "no point inside the big room (it is crossed door to door)");

        // Every doorway (narrow opening) of the loop is passed through near its centre.
        foreach (var (a, b) in d.Graph.Values.SelectMany(n => n.Neighbors.Select(m => (n.CellId, m))).Where(e => e.CellId < e.m))
        {
            if (!geo.TryGetDoorway(a, b, out var door) || door.IsWide) continue;
            double best = double.MaxValue;
            for (int i = 0; i < route.Points.Count; i++)
            {
                var p = route.Points[i]; var q = route.Points[(i + 1) % route.Points.Count];
                best = Math.Min(best, DistToSegment(door.X, door.Y, X(p), Y(p), X(q), Y(q)));
            }
            Check.True(best < 0.3, $"through the opening at ({door.X:F1}, {door.Y:F1}) within 0.3 m of its centre ({best:F2})");
        }

        var old = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null);
        int oldInside = old.Points.Count(p => X(p) > 3 && X(p) < 27 && Y(p) > 3 && Y(p) < 27);
        Check.True(oldInside >= 4, $"the old cell walk wandered through the room ({oldInside} points inside)");
    }

    private static void PatrolLooksIntoDeadEndRoom()
    {
        // Cut the east door: the room hangs off the west corridor by one doorway. A short ring
        // north of the top corridor keeps a cycle in the main route.
        var (d, c, west, top2, east) = LoopDungeon();
        d.Graph[east].Neighbors.Remove(c[2, 0]);
        d.Graph[c[2, 0]].Neighbors.Remove(east);
        uint ringA = d.Cell(15, 42.95, 20.3, 52.95), ringB = d.Cell(15, 52.95, 30, 58.25), ringC = d.Cell(24.7, 42.95, 30, 52.95);
        d.OpenY(top2, ringA, 42.95, 15, 20.3);
        d.OpenY(ringA, ringB, 52.95, 15, 20.3);
        d.OpenY(ringC, ringB, 52.95, 24.7, 30);
        d.OpenY(top2, ringC, 42.95, 24.7, 30);
        var geo = d.Geo();
        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo);
        bool looked = route.Points.Any(p => X(p) > 10 && X(p) < 20 && Y(p) > 10 && Y(p) < 20);
        Check.True(looked, "the patrol walks into the middle of the dead-end room");

        // Settings "Walk Into Rooms From": the room (3x3 cells, 20 m between its outer cell
        // centres: spread 28 m) is under 40 m, so it is only looked into from its doorway.
        var big = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo, new DungeonPatrolOptions { LookInRoomMeters = 40 });
        Check.False(big.Points.Any(p => X(p) > 10 && X(p) < 20 && Y(p) > 10 && Y(p) < 20),
                    "with rooms from 40 m: the 28 m room's middle is not walked to");
        var at20 = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo, new DungeonPatrolOptions { LookInRoomMeters = 20 });
        Check.True(at20.Points.Any(p => X(p) > 10 && X(p) < 20 && Y(p) > 10 && Y(p) < 20), "with rooms from 20 m: it is");
    }

    // Settings "Distance From Walls": every patrol point keeps the chosen distance where the
    // corridors (5.3 m wide) and rooms allow it.
    private static void PatrolWallDistanceSetting()
    {
        var (d, _, west, _, _) = LoopDungeon();
        var geo = d.Geo();
        foreach (double wall in new[] { 0.5, 1.0, 1.5, 2.0 })
        {
            var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo, new DungeonPatrolOptions { WallClearance = wall });
            Check.True(route.Points.Count > 0, $"{wall} m: a patrol");
            double least = route.Points.Min(p => Clear(geo, p));
            Check.True(least >= wall - 0.02, $"{wall} m: every point at least that far from the walls (least {least:F2})");
        }
        var near = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo, new DungeonPatrolOptions { WallClearance = 0.5 });
        var far  = DungeonPathfinder.BuildPatrolRoute(d.Graph, west, null, geo, new DungeonPatrolOptions { WallClearance = 2.0 });
        Check.True(far.Points.Min(p => Clear(geo, p)) > near.Points.Min(p => Clear(geo, p)) + 0.5, "2 m keeps points further off the walls than 0.5 m");
    }

    private static void CeilingIsNotFloor()
    {
        Check.True(DungeonGeometry.IsUpwardFloor(Rect(0, 0, 10, 10, 0)), "counter-clockwise from above: a floor");
        Check.False(DungeonGeometry.IsUpwardFloor(Rect(0, 0, 10, 10, 4).Reverse().ToArray()), "clockwise from above: a ceiling");

        var d = new Dungeon();
        uint a = d.Cell(0, 0, 10, 10);
        d.Floors.Clear();
        d.Floors.Add((a, Rect(0, 0, 10, 10, 3.8).Reverse().ToArray()));   // only its ceiling is known
        var geo = d.Geo();
        Check.False(geo.HasFloor(a), "a cell with only a ceiling has no floor data");
        Check.True(geo.OnFloorOfAny(new[] { a }, 5, 5, 0), "so nothing is checked against it");
    }

    // ── Clearance from walls ─────────────────────────────────────────────────

    private static double Clear(DungeonGeometry geo, NavPoint p) => geo.Clearance(X(p), Y(p), p.Z * 240.0, 3.0);

    private static void ClearanceMeasure()
    {
        // A 10 x 10 room, a wall along its west side; a steep slope (2 m up over 1.6 m, about 51
        // degrees) beyond its east side.
        var d = new Dungeon();
        uint room = d.Cell(0, 0, 10, 10);
        d.Wall(room, 0, 0, 0, 10);
        d.Floors.Add((room, new[] { new V3(10f, 0f, 0f), new V3(11.6f, 0f, 2f), new V3(11.6f, 10f, 2f), new V3(10f, 10f, 0f) }));
        // A face across the room's north half that starts 2.5 m up (the face above an arch).
        d.Walls.Add((room, new[] { new V3(0f, 8f, 2.5f), new V3(10f, 8f, 2.5f), new V3(10f, 8f, 4f), new V3(0f, 8f, 4f) }));
        var geo = d.Geo();
        Check.Near(geo.Clearance(0.4, 5, 0), 0.4, 0.06, "0.4 m from the west wall");
        Check.Near(geo.Clearance(9.3, 5, 0), 0.7, 0.06, "the slope too steep to stand on bounds the floor (0.7 m to its foot)");
        Check.Near(geo.Clearance(5, 5, 0), 3.0, 1e-6, "the middle: nothing within 3 m (the face 3 m north is 2.5 m up)");
        Check.Near(geo.Clearance(5, 8.2, 0), 1.8, 0.06, "under the high face: the nearest bound is the room's north edge, 1.8 m away");
        Check.Eq(geo.Clearance(-0.5, 5, 0), 0.0, "past the floor's edge: 0");
        Check.Eq(geo.Clearance(10.8, 5, 1), 0.0, "on the steep slope: 0");
    }

    private static void PushedOffWall()
    {
        var d = new Dungeon();
        uint room = d.Cell(0, 0, 10, 10);
        d.Wall(room, 0, 0, 0, 10);
        var geo = d.Geo();
        double x = 0.3, y = 5;
        double c = geo.PushClear(ref x, ref y, 0, 1.0, 2.0);
        Check.True(c >= 0.98, $"pushed to 1 m clearance ({c:F2})");
        Check.True(x >= 0.98 && x <= 1.2, $"straight out from the wall, not further than needed (x {x:F2})");
        Check.Near(y, 5, 0.15, "along the wall's normal");

        // Past the floor's edge (a dead end's cell origin inside its end cap): onto the floor first.
        x = -0.5; y = 5;
        c = geo.PushClear(ref x, ref y, 0, 1.0, 3.0);
        Check.True(c >= 0.98 && x > 0, $"onto the floor with 1 m clearance ({x:F2}, {y:F2}: {c:F2})");

        // In the open: left alone.
        x = 5; y = 5;
        geo.PushClear(ref x, ref y, 0, 1.0, 2.0);
        Check.True(x == 5 && y == 5, "a point with room enough is not moved");
    }

    private static void NarrowCorridorCentred()
    {
        var d = new Dungeon();
        uint corr = d.Cell(0, 0, 20, 1.6);
        d.Wall(corr, 0, 0, 20, 0);
        d.Wall(corr, 0, 1.6, 20, 1.6);
        var geo = d.Geo();
        double x = 10, y = 0.3;
        double c = geo.PushClear(ref x, ref y, 0, 1.0, 2.0);
        Check.Near(y, 0.8, 0.06, $"on the middle line of the 1.6 m corridor (y {y:F2})");
        Check.Near(c, 0.8, 0.06, "as clear as the corridor allows");
        Check.Near(x, 10, 0.5, "not moved along the corridor");
    }

    private static void DoorwayPointMovesAlongCentreLine()
    {
        // A room (x 0..10) opening east into a north-south corridor 4.6 m wide (x 10..14.6), like
        // a hive corridor: the 4 m standoff would put the exit point 0.6 m from the far wall.
        var d = new Dungeon();
        uint room = d.Cell(0, 0, 10, 10);
        uint corr = d.Cell(10, -10, 14.6, 20);
        d.OpenX(room, corr, 10, 2.35, 7.65);
        d.Wall(corr, 14.6, -10, 14.6, 20);
        var geo = d.Geo();
        var pts = Path(d, geo, 2, 5, 12.3, -8, room, corr);
        var exit = pts.First(p => X(p) > 10);
        Check.Near(Y(exit), 5, 1e-3, "the exit point stays on the opening's centre line");
        Check.Near(X(exit), 13.5, 0.13, $"3.5 m beyond the opening, not 4 (x {X(exit):F2})");
        Check.True(Clear(geo, exit) >= 0.98, $"1 m from the far wall ({Clear(geo, exit):F2})");
        Check.True(exit.Doorway, "still a doorway point");
        Check.Near(X(pts[0]), 6, 1e-3, "the approach in the open room keeps its 4 m");
    }

    // Rithwic Crypt's corner: a corridor 3.33 m wide comes from the west, a chamfered cell turns
    // it north. The chamfer strip is 2.36 m across, so the doorway points' centre lines (y 1.67
    // and x 5) run into its outer wall within a metre of the openings.
    private static (Dungeon D, uint West, uint Corner, uint North) ChamferDungeon()
    {
        var d = new Dungeon();
        uint west = d.Cell(-10, 0, 0, 3.33);
        uint corner = d.CellPoly(2.5, 4.2, (0, 0, 0), (6.67, 6.67, 0), (3.33, 6.67, 0), (0, 3.33, 0));
        uint north = d.Cell(3.33, 6.67, 6.67, 16.67);
        d.OpenX(west, corner, 0, 0, 3.33);
        d.OpenY(corner, north, 6.67, 3.33, 6.67);
        d.Wall(corner, 0, 0, 6.67, 6.67);         // the bend's outer wall
        d.Wall(corner, 0, 3.33, 3.33, 6.67);      // its inner wall
        d.Wall(west, -10, 0, 0, 0); d.Wall(west, -10, 3.33, 0, 3.33);
        d.Wall(north, 3.33, 6.67, 3.33, 16.67); d.Wall(north, 6.67, 6.67, 6.67, 16.67);
        return (d, west, corner, north);
    }

    private static void ChamferedBend()
    {
        var (d, west, corner, north) = ChamferDungeon();
        var geo = d.Geo();
        var pts = Path(d, geo, -8, 1.67, 5, 15, west, corner, north);
        // The bend is walked through: a point at or past the west opening and one at or before the
        // north one. Since 2026-10-14 the two openings (at right angles) are one run: their centres,
        // and the leg between cuts the chamfer (the checks below keep it on the floor and off the walls).
        string path = string.Join(" ", pts.Select(p => $"({X(p):F2},{Y(p):F2})"));
        Check.True(pts.Any(p => X(p) >= -0.01 && Y(p) < 6.67) && pts.Any(p => X(p) > 0 && Y(p) <= 6.68),
            "the path goes through the bend: " + path);
        Check.True(pts.Count <= 5, "no doubling back in the bend: " + path);
        foreach (var p in pts)
            Check.True(Clear(geo, p) >= 0.98, $"({X(p):F2}, {Y(p):F2}) is 1 m from every wall ({Clear(geo, p):F2})");
        // Before: the exit point on the west opening's centre line, 1 m in: 0.47 m from the outer wall.
        Check.True(geo.Clearance(1, 1.67, 0) < 0.5, "the centre line's best spot was under 0.5 m from the outer wall");
        var cells = new[] { west, corner, north };
        Check.True(LegsClear(geo, cells, -8, 1.67, pts, 5, 15), "every leg stays on the floor and off the walls");
    }

    private static void DeadEndLookInOnFloor()
    {
        // The loop dungeon with a dead end off its east corner: a corridor (x 45.3..53.7) closed by
        // a steep end cap (53.7..55.3, 2 m up), its cell origin behind the cap (55.8), as in the
        // Olthoi hive. The patrol starts there, so it looks into it.
        var (d, _, _, _, _) = LoopDungeon();
        uint corner = d.Graph.Values.First(n => Math.Abs(DungeonGeometry.NavToWorld(n.EW) - 42.65) < 0.01 && Math.Abs(DungeonGeometry.NavToWorld(n.NS) - 5) < 0.01).CellId;
        uint dead = d.CellPoly(55.8, 5, (45.3, 2.35, 0), (53.7, 2.35, 0), (53.7, 7.65, 0), (45.3, 7.65, 0));
        d.Floors.Add((dead, new[] { new V3(53.7f, 7.65f, 0f), new V3(53.7f, 2.35f, 0f), new V3(55.3f, 5f, 2f) }));
        d.OpenX(corner, dead, 45.3, 2.35, 7.65);
        var geo = d.Geo();
        var route = DungeonPathfinder.BuildPatrolRoute(d.Graph, dead, null, geo);
        var lookIn = route.Points.Where(p => X(p) > 46 && Math.Abs(Y(p) - 5) < 1).ToList();
        Check.True(lookIn.Count >= 1, "the patrol walks into the dead end");
        Check.False(route.Points.Any(p => X(p) > 53.7), "no point on or behind the end cap (the cell origin is at x 55.8)");
        foreach (var p in lookIn) Check.True(Clear(geo, p) >= 0.98, $"look-in point ({X(p):F2}, {Y(p):F2}) 1 m from the cap and walls ({Clear(geo, p):F2})");
    }

    private static void LegAroundInsideCorner()
    {
        // An L: a corridor down from the north (x 0..3.33) turning east (y 0..3.33); the inside
        // corner at (3.33, 3.33). A leg from the north arm to the east arm cuts through it.
        var d = new Dungeon();
        uint down = d.Cell(0, 3.33, 3.33, 13.33);
        uint turn = d.Cell(0, 0, 3.33, 3.33);
        uint across = d.Cell(3.33, 0, 13.33, 3.33);
        d.OpenY(turn, down, 3.33, 0, 3.33);
        d.OpenX(turn, across, 3.33, 0, 3.33);
        d.Wall(down, 3.33, 3.33, 3.33, 13.33);
        d.Wall(across, 3.33, 3.33, 13.33, 3.33);
        var geo = d.Geo();
        NavPoint P(double x, double y) => new() { EW = DungeonGeometry.WorldToNav(x), NS = DungeonGeometry.WorldToNav(y), Z = 0 };
        var pts = new List<NavPoint> { P(1.67, 8), P(9, 1.67) };
        Check.True(geo.LegClearance(1.67, 8, 0, 9, 1.67, 0, 1.0, 0.5, out _, out _, out _) < 0.3, "the straight leg passes the inside corner closer than 0.3 m");
        DungeonPathfinder.KeepLegsOffWalls(geo, pts, closed: false);
        Check.True(pts.Count >= 3, $"a corner put in ({pts.Count - 2})");
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            double least = geo.LegClearance(X(pts[i]), Y(pts[i]), 0, X(pts[i + 1]), Y(pts[i + 1]), 0, 1.0, 0.5, out _, out _, out _);
            Check.True(least >= 0.6, $"leg {i} keeps 0.6 m from the corner ({least:F2})");
        }
    }

    private static double DistToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        double t = len2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
        double cx = ax + dx * t - px, cy = ay + dy * t - py;
        return Math.Sqrt(cx * cx + cy * cy);
    }
}

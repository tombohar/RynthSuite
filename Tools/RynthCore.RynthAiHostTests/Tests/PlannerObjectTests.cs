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

// Wave 1 of "any object in the world that may stop the bot" (2026-11-18), the planners: the dungeon map knows what
// the cells place (body-band footprints, cylinders, spheres, Resting poses); every walk planner and walker in a
// dungeon now uses it:
//   - a walk that ends (a stuck detour, dunroute, a jump walk's stretches) walks its first and last legs round the
//     objects too, and a walk inside one room is walked round them (it went straight on);
//   - the stuck recovery plans inside one room ("already in the waypoint's room" was 9 of 29 logged no-plans): round
//     a table, out of a statue the waypoint stands in (and reaching that spot reaches the waypoint);
//   - a jump doesn't land inside an object or onto a platform on the way down;
//   - the portal step's changed legs are walked round objects, never into a portal's circle (the portal wins);
//   - the walk-round counts (HolesLeft) run over a whole build;
//   - The patrol's first waypoint goes by a walk test at body height, not by sight.
internal static class PlannerObjectTests
{
    public static void Register(Runner r)
    {
        r.Add("planner objects (11-18): a walk that ends walks its first and last legs round an object; a walk in one room too", EndLegs);
        r.Add("planner objects (11-18): HolesLeft counts every walk round of one build; a call on its own counts alone", RunningCount);
        r.Add("planner objects (11-18): a point laid inside an object is pushed out of it, or dropped when no push gets out", PointsOut);
        r.Add("planner objects (11-18): the stuck recovery in one room walks round a table and out of a statue the waypoint is in", InRoomSynthetic);
        r.Add("planner objects (11-18): a detour whose waypoint is inside an object: reaching its last point reaches the waypoint", TargetMovedEngine);
        r.Add("planner objects (11-18): a jump never lands inside an object or onto a platform on the way down", JumpLandings);
        r.Add("planner objects (11-18): the portal step's legs go round an object; a way round into a portal's circle is not taken", PortalLegs);
        r.Add("planner objects (11-18): the straight walk test at body height stops at a 0.9 m table the eye-height sight line passes", WalkTest);
        r.Add("planner objects (11-18): a stuck escape burst heading into a wall or an object within 2 m turns to where the dungeon map has room", EscapeRoomTest);
        r.Add("planner objects (11-18): 0x0190 room 88's table and 0x00C1 room 81's statue on the real dats - an in-room recovery plan, no sample inside (if the dats are here)", InRoomReal);
        r.Add("planner objects (11-18): Thread patrols with an exit portal at the entry cell on the real dats - no leg through an object, none into the portal (if the dats are here)", PortalPatrolsReal);
        r.Add("planner objects (review): the room planner never moves a waypoint up on a dais, a ramp or a platform, or in a step, off it (TargetMoved skipped it); a statue's still", OnTopTargets);
        r.Add("planner objects (review): the walk test from a spot up on a dais is judged from where the leg leaves it, not failed at its first sample", WalkTestFromOnTop);
    }

    private static V3 V(double x, double y, double z) => new((float)x, (float)y, (float)z);
    private static double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
    private static double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);
    private static NavPoint P(double x, double y, double z = 0) => new() { Type = NavPointType.Point, EW = DungeonGeometry.WorldToNav(x), NS = DungeonGeometry.WorldToNav(y), Z = z / 240.0 };
    private static V3[] Box(double x0, double y0, double z0, double x1, double y1, double z1) => PlacedObjectTests.Box(x0, y0, z0, x1, y1, z1);

    // A ramp from (x0, y0..y1) on the floor up to height h at x1, closed (base, slope, its high end and two sides).
    private static V3[] Ramp(double x0, double y0, double x1, double y1, double h)
    {
        V3 a = V(x0, y0, 0), b = V(x1, y0, 0), c = V(x1, y1, 0), d = V(x0, y1, 0), e = V(x1, y0, h), f = V(x1, y1, h);
        return new[] { a, c, b, a, d, c, a, e, f, a, f, d, b, c, f, b, f, e, a, b, e, d, f, c };
    }

    // One 40 x 20 m room (floor 0) with a 0.5 m dais (8..12 x 8..12), a ramp up 1 m along x (14..18) onto a 1 m platform
    // (18..24), and a 4 m statue (30..36 x 4..10).
    private static DungeonGeometry SteppedRoom(out Dictionary<uint, PlacedClass> classes)
    {
        const uint cell = 0x7E7E0800;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(20), NS = DungeonGeometry.WorldToNav(10), Z = 0 } };
        var floors = new List<(uint, V3[])> { (cell, new[] { V(0, 0, 0), V(40, 0, 0), V(40, 20, 0), V(0, 20, 0) }) };
        var geo = DungeonGeometry.Build(graph, Array.Empty<(uint, V3[])>(), floors, Array.Empty<(uint, V3[])>());
        geo.AddPlacedObjects(new (uint, uint, V3[])[]
        {
            (0x7E7E0031, cell, Box(8, 8, 0, 12, 12, 0.5)),
            (0x7E7E0032, cell, Ramp(14, 8, 18, 12, 1.0)),
            (0x7E7E0033, cell, Box(18, 8, 0, 24, 12, 1.0)),
            (0x7E7E0034, cell, Box(30, 4, 0, 36, 10, 4.0)),
        });
        classes = geo.PlacedObjects.ToDictionary(p => p.Setup, p => p.Class);
        return geo;
    }

    // Review of wf/obst-int, finding 1: the room planner took a waypoint recorded up on a step-over or a connector for one
    // inside it (it measured from the map's floor under it, not the top it was recorded on), moved it down beside the
    // object and set TargetMoved, so the detour's end skipped it and the next waypoint up there ran into the object again.
    private static void OnTopTargets()
    {
        var geo = SteppedRoom(out var cls);
        Check.True(cls[0x7E7E0031] == PlacedClass.StepOver && cls[0x7E7E0032] == PlacedClass.Connector && cls[0x7E7E0033] == PlacedClass.Connector
                   && cls[0x7E7E0034] == PlacedClass.Obstacle, $"a step-over dais, a connector ramp and platform, an obstacle statue ({string.Join(" ", cls.Values)})");
        NavRecoveryPlan? Plan(double px, double py, double tx, double ty, double tz, out string why)
            => NavRecoveryPlanner.PlanInRoom(geo, DungeonGeometry.WorldToNav(py), DungeonGeometry.WorldToNav(px), 0,
                                             DungeonGeometry.WorldToNav(ty), DungeonGeometry.WorldToNav(tx), tz, out why);
        foreach (var (name, x, y, z) in new[] { ("on the dais's top", 10.0, 10.0, 0.5), ("half way up the ramp", 16.0, 10.0, 0.5),
                                                ("on the platform", 21.0, 10.0, 1.0), ("in the dais at the floor's height", 10.0, 10.0, 0.0) })
        {
            Check.True(geo.InsidePlaced(x, y, 0), $"{name}: inside it at the map's floor");
            var plan = Plan(4, 10, x, y, z, out string why);
            Check.Null(plan, $"{name} ({x},{y},{z}): no plan, the waypoint not moved ({plan?.Planner}, moved {plan?.TargetMoved}: {why})");
            Check.True(why.Contains("up on something placed") || why.Contains("step or a ramp"), $"{name}: says why: {why}");
        }
        // The statue's middle (an obstacle): moved out of it, as before.
        var statue = Plan(33, 13, 33, 7, 0, out string why2);
        Check.True(statue != null && statue.TargetMoved, $"a waypoint in the statue: moved out of it ({statue?.Planner}: {why2})");
        // A waypoint on the floor beside the dais, the dais between: walked round it as before.
        var round = Plan(4, 10, 13, 10, 0, out string why3);
        Check.True(round != null && !round.TargetMoved && round.Points.Count >= 1, $"to the floor past the dais: walked round it ({round?.Planner}: {why3})");
    }

    // Review of wf/obst-int, finding 3: the walk test (the patrol's first waypoint) from a character
    // standing on a dais failed every leg at its first sample (the dais in the band at the floor under it), so the patrol
    // started at waypoint 0.
    private static void WalkTestFromOnTop()
    {
        var geo = SteppedRoom(out _);
        geo.AddPlacedObjects(new (uint, uint, V3[])[] { (0x020002FB, 0x7E7E0800, Box(4, 14, 0, 5.4, 18, 0.9)) });   // a table north-west of the dais
        var onDais = P(10, 10, 0.5);
        Check.True(DungeonPathfinder.StraightWalkClear(geo, onDais, P(3, 10)), "from the dais's top west to the open floor: clear");
        Check.True(DungeonPathfinder.StraightWalkClear(geo, onDais, P(10, 18)), "from the dais's top north: clear");
        Check.True(DungeonPathfinder.StraightWalkClear(geo, P(3, 10), onDais), "and to a spot up on it (a stand point there)");
        Check.True(DungeonPathfinder.StraightWalkClear(geo, onDais, P(11, 11, 0.5)), "across its top");
        Check.True(DungeonPathfinder.StraightWalkClear(geo, P(16, 10, 0.5), P(21, 10, 1.0)), "up the ramp onto the platform");
        Check.False(DungeonPathfinder.StraightWalkClear(geo, onDais, P(1, 18)), "off the dais through the table: still not clear");
        Check.False(DungeonPathfinder.StraightWalkClear(geo, P(3, 10), P(13, 10)), "on the floor through the dais: still not clear");
        Check.False(DungeonPathfinder.StraightWalkClear(geo, P(28, 7), P(38, 7)), "on the floor through the statue: still not clear");
    }

    // Samples 0.25 m apart along a-b (ends left out) that are inside a placed object, on the floor followed.
    private static int SamplesInside(DungeonGeometry geo, NavPoint a, NavPoint b)
    {
        double ax = X(a), ay = Y(a), az = a.Z * 240, bx = X(b), by = Y(b), bz = b.Z * 240;
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        int n = Math.Max(1, (int)Math.Ceiling(len / 0.25)), inside = 0;
        double zf = geo.TryFloorHeight(ax, ay, az, 3, out double h0) ? h0 : az;
        for (int s = 1; s < n; s++)
        {
            double t = (double)s / n, x = ax + (bx - ax) * t, y = ay + (by - ay) * t;
            if (!geo.TryFloorHeight(x, y, zf, 0.6, out double h) && !geo.TryFloorHeight(x, y, az + (bz - az) * t, 0.6, out h)) continue;
            zf = h;
            if (geo.InsidePlaced(x, y, h)) inside++;
        }
        return inside;
    }

    private static int WalkInside(DungeonGeometry geo, List<NavPoint> walk, bool closed = false)
    {
        int n = 0;
        for (int i = 0; i + 1 < walk.Count || (closed && i < walk.Count && walk.Count > 1); i++)
            n += SamplesInside(geo, walk[i], walk[(i + 1) % walk.Count]);
        return n;
    }

    // Two 20 x 20 m rooms side by side (floor 0), a 4 m doorway between them at x 20 (y 8..12).
    private sealed class TwoRooms
    {
        public readonly Dictionary<uint, DungeonNavNode> Graph = new();
        public const uint A = 0x7E7E0100, B = 0x7E7E0101;
        public readonly DungeonGeometry Geo;

        public TwoRooms(params V3[][] objects)
        {
            Graph[A] = new DungeonNavNode { CellId = A, EW = DungeonGeometry.WorldToNav(10), NS = DungeonGeometry.WorldToNav(10), Z = 0 };
            Graph[B] = new DungeonNavNode { CellId = B, EW = DungeonGeometry.WorldToNav(30), NS = DungeonGeometry.WorldToNav(10), Z = 0 };
            Graph[A].Neighbors.Add(B);
            Graph[B].Neighbors.Add(A);
            var floors = new List<(uint, V3[])>
            {
                (A, new[] { V(0, 0, 0), V(20, 0, 0), V(20, 20, 0), V(0, 20, 0) }),
                (B, new[] { V(20, 0, 0), V(40, 0, 0), V(40, 20, 0), V(20, 20, 0) }),
            };
            var door = new[] { V(20, 8, 0), V(20, 12, 0), V(20, 12, 3), V(20, 8, 3) };
            var portals = new List<(uint, V3[])> { (A, door), (B, door.Reverse().ToArray()) };
            // The wall between them, either side of the doorway.
            var walls = new List<(uint, V3[])>
            {
                (A, new[] { V(20, 0, 0), V(20, 8, 0), V(20, 8, 3), V(20, 0, 3) }),
                (A, new[] { V(20, 12, 0), V(20, 20, 0), V(20, 20, 3), V(20, 12, 3) }),
            };
            Geo = DungeonGeometry.Build(Graph, portals, floors, walls);
            Geo.AddPlacedObjects(objects.Select((t, i) => (0x02000969u + (uint)i, i % 2 == 0 ? A : B, t)));
        }
    }

    // Item 1: the detour's end legs. Room A: an altar between the start and the doorway's approach point; room B: one
    // between the exit point and the destination.
    private static void EndLegs()
    {
        var rooms = new TwoRooms(Box(6, 9.2, 0, 8, 10.8, 1.0), Box(33, 9.2, 0, 35, 10.8, 1.0));
        var geo = rooms.Geo;
        Check.Eq(geo.PlacedCount, 2, "both altars on the map");
        Check.True(geo.TryGetDoorway(TwoRooms.A, TwoRooms.B, out var door) && !door.IsWide, "a narrow doorway between the rooms");
        var start = P(2, 10); var dest = P(38, 10);
        var pts = DungeonPathfinder.BuildPathPoints(new List<uint> { TwoRooms.A, TwoRooms.B }, rooms.Graph, geo, null,
                                                    start.NS, start.EW, 0, dest.NS, dest.EW, 0, out int doorways);
        Check.Eq(doorways, 1, "through the doorway");
        var walk = new List<NavPoint> { start };
        walk.AddRange(pts);
        walk.Add(dest);
        Console.WriteLine("    walk: " + string.Join(" ", walk.Select(p => $"({X(p):F1},{Y(p):F1})")));
        Check.Eq(SamplesInside(geo, walk[0], walk[1]), 0, "the first leg (from the start) goes round the altar in room A");
        Check.Eq(SamplesInside(geo, walk[^2], walk[^1]), 0, "the last leg (to the destination) goes round the altar in room B");
        Check.Eq(WalkInside(geo, walk), 0, "no leg of the walk through either altar");
        Check.False(ReferenceEquals(pts.FirstOrDefault(), start) || ReferenceEquals(pts.LastOrDefault(), dest), "the start and the destination are the caller's, not in the points");

        // Within one room (one cell): the straight walk across it used to be left as it was.
        var one = DungeonPathfinder.BuildPathPoints(new List<uint> { TwoRooms.A }, rooms.Graph, geo, null,
                                                    start.NS, start.EW, 0, DungeonGeometry.WorldToNav(10), DungeonGeometry.WorldToNav(14), 0, out _);
        var oneWalk = new List<NavPoint> { start };
        oneWalk.AddRange(one);
        oneWalk.Add(P(14, 10));
        Check.True(one.Count >= 1, $"a walk across room A past the altar gets a turn ({one.Count} point(s))");
        Check.Eq(WalkInside(geo, oneWalk), 0, "and goes round it");

        // BuildNavRoute (dunnav, a script's GoTo): the same, its last point the destination.
        var route = DungeonPathfinder.BuildNavRoute(new List<uint> { TwoRooms.A, TwoRooms.B }, rooms.Graph, dest.NS, dest.EW, geo, null, start.NS, start.EW, 0, 0);
        var rw = new List<NavPoint> { start };
        rw.AddRange(route.Points);
        Check.Eq(WalkInside(geo, rw), 0, "BuildNavRoute: no leg through an altar");
    }

    // Item 3.
    private static void RunningCount()
    {
        // A 6 m wide hall: two blocks across it wall to wall, and one free-standing: two legs with no way round, one walked round.
        const uint cell = 0x7E7E0200;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(20), NS = DungeonGeometry.WorldToNav(3), Z = 0 } };
        var floors = new List<(uint, V3[])> { (cell, new[] { V(0, 0, 0), V(40, 0, 0), V(40, 6, 0), V(0, 6, 0) }) };
        var geo = DungeonGeometry.Build(graph, Array.Empty<(uint, V3[])>(), floors, Array.Empty<(uint, V3[])>());
        geo.AddPlacedObjects(new (uint, uint, V3[])[] { (1, cell, Box(5, -1, 0, 6, 7, 1.5)), (2, cell, Box(15, -1, 0, 16, 7, 1.5)), (3, cell, Box(29.5, 2.5, 0, 30.5, 3.5, 1)) });
        var a = new List<NavPoint> { P(2, 3), P(9, 3) };
        var b = new List<NavPoint> { P(12, 3), P(19, 3) };
        var c = new List<NavPoint> { P(27, 3), P(33, 3) };

        DungeonPathfinder.WalkRoundHoles(a, geo, objectsOnly: true);
        Check.Eq(DungeonPathfinder.HolesLeft, 1, "a call on its own: its own count");
        DungeonPathfinder.WalkRoundHoles(b, geo, objectsOnly: true);
        Check.Eq(DungeonPathfinder.HolesLeft, 1, "the next call on its own starts again");

        using (DungeonPathfinder.CountHoles())
        {
            DungeonPathfinder.WalkRoundHoles(a, geo, objectsOnly: true);
            DungeonPathfinder.WalkRoundHoles(b, geo, objectsOnly: true);
            Check.Eq(DungeonPathfinder.WalkRoundHoles(c, geo, objectsOnly: true), 1, "the third leg is walked round (that call's own return)");
            using (DungeonPathfinder.CountHoles())   // a nested build (a stretch of a jump walk) doesn't start again
                Check.Eq(DungeonPathfinder.HolesLeft, 2, "nested: the outer count still");
        }
        Check.Eq(DungeonPathfinder.HolesLeft, 2, "one build: both legs with no way round counted (it said 1: the last call's)");
        Check.Eq(DungeonPathfinder.HolesWalkedRound, 1, "and the one walked round");
    }

    // Item 9.
    private static void PointsOut()
    {
        const uint cell = 0x7E7E0700;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(15), NS = DungeonGeometry.WorldToNav(15), Z = 0 } };
        var floors = new List<(uint, V3[])> { (cell, new[] { V(0, 0, 0), V(30, 0, 0), V(30, 30, 0), V(0, 30, 0) }) };
        var geo = DungeonGeometry.Build(graph, Array.Empty<(uint, V3[])>(), floors, Array.Empty<(uint, V3[])>());
        // An altar, and a block too big for any push (13 x 13 m: its middle is 6.5 m from every side).
        geo.AddPlacedObjects(new (uint, uint, V3[])[] { (0x02000969, cell, Box(4, 4, 0, 6, 5.2, 1.0)), (0x020014A3, cell, Box(14, 14, 0, 27, 27, 3)) });
        var onAltar = P(5, 4.6);
        var inBlock = P(20.5, 20.5);
        var pts = new List<NavPoint> { P(2, 2), onAltar, P(10, 2), inBlock, P(10, 10) };
        int changed = DungeonPathfinder.KeepPointsOutOfObjects(geo, pts, closed: true);
        Check.Eq(changed, 2, "two points were inside");
        Check.True(pts.Contains(onAltar) && !geo.InsidePlaced(X(onAltar), Y(onAltar), 0), $"the one on the altar is moved off it ({X(onAltar):F1},{Y(onAltar):F1})");
        Check.True(Math.Sqrt(Math.Pow(X(onAltar) - 5, 2) + Math.Pow(Y(onAltar) - 4.6, 2)) <= 4.5 + 1e-6, "at most 4.5 m");
        Check.False(pts.Contains(inBlock), "the one in the middle of the block is dropped");
        Check.Eq(pts.Count, 4, "the rest kept");
    }

    // Item 4, synthetic: a table between the character and the waypoint, and a statue the waypoint stands in.
    private static void InRoomSynthetic()
    {
        const uint cell = 0x7E7E0300;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(15), NS = DungeonGeometry.WorldToNav(15), Z = 0 } };
        var floors = new List<(uint, V3[])> { (cell, new[] { V(0, 0, 0), V(30, 0, 0), V(30, 30, 0), V(0, 30, 0) }) };
        var geo = DungeonGeometry.Build(graph, Array.Empty<(uint, V3[])>(), floors, Array.Empty<(uint, V3[])>());
        // A table: a 1.4 x 3 m top at 0.8-0.9 m on four legs.
        var table = Box(8, 5, 0.8, 9.4, 8, 0.9).Concat(Box(8, 5, 0, 8.1, 5.1, 0.8)).Concat(Box(9.3, 5, 0, 9.4, 5.1, 0.8))
                    .Concat(Box(8, 7.9, 0, 8.1, 8, 0.8)).Concat(Box(9.3, 7.9, 0, 9.4, 8, 0.8)).ToArray();
        geo.AddPlacedObjects(new (uint, uint, V3[])[] { (0x020002FB, cell, table), (0x020014A3, cell, Box(18, 18, 0, 27, 27, 4)) });

        // Stuck at the table's west side, the waypoint just past its east side.
        var plan = NavRecoveryPlanner.PlanInRoom(geo, DungeonGeometry.WorldToNav(6.5), DungeonGeometry.WorldToNav(7.47), 0,
                                                 DungeonGeometry.WorldToNav(6.5), DungeonGeometry.WorldToNav(11), 0, out string why);
        Check.NotNull(plan, "a plan round the table: " + why);
        Check.True(plan!.Planner.StartsWith("dungeon map (room)", StringComparison.Ordinal) && !plan.TargetMoved, "a dungeon map (room) plan, the waypoint where it is: " + plan.Planner);
        var walk = new List<NavPoint> { P(7.47, 6.5) };
        walk.AddRange(plan.Points);
        walk.Add(P(11, 6.5));
        Check.True(plan.Points.Count >= 1, $"turns put in ({plan.Points.Count})");
        Check.Eq(WalkInside(geo, walk), 0, "no sample inside the table (between its legs too)");

        // The waypoint in the statue's middle: moved out first, and the plan ends there.
        var plan2 = NavRecoveryPlanner.PlanInRoom(geo, DungeonGeometry.WorldToNav(22.5), DungeonGeometry.WorldToNav(16.5), 0,
                                                  DungeonGeometry.WorldToNav(22.5), DungeonGeometry.WorldToNav(22.5), 0, out why);
        Check.NotNull(plan2, "a plan out of the statue: " + why);
        Check.True(plan2!.TargetMoved, "the waypoint was moved: " + plan2.Planner);
        var end = plan2.Points[^1];
        Check.False(geo.InsidePlaced(X(end), Y(end), 0), $"its last point is clear of the statue ({X(end):F1},{Y(end):F1})");
        Check.True(geo.Clearance(X(end), Y(end), 0) >= NavRecoveryPlanner.InRoomTargetClearance - 0.05, $"with room ({geo.Clearance(X(end), Y(end), 0):F2})");
        var walk2 = new List<NavPoint> { P(16.5, 22.5) };
        walk2.AddRange(plan2.Points);
        Check.Eq(WalkInside(geo, walk2), 0, "no sample inside the statue");

        // A clear straight walk: nothing for the room planner to add.
        Check.Null(NavRecoveryPlanner.PlanInRoom(geo, DungeonGeometry.WorldToNav(2), DungeonGeometry.WorldToNav(2), 0,
                                                 DungeonGeometry.WorldToNav(2), DungeonGeometry.WorldToNav(14), 0, out why), "no objects in the way: no plan");
        Check.True(why.Contains("clear"), "says the straight walk is clear: " + why);
    }

    // Item 4, the engine: a detour planned to the spot nearest a waypoint inside an object ends on the next waypoint.
    private const double NS0 = 42.0, EW0 = 33.0;
    private static long _now;
    private static void At(double north, double east) => FakeHost.SetNavPosition(NS0 + north / 240.0, EW0 + east / 240.0, 0f);
    private static NavPoint Pt(double north, double east) => new() { Type = NavPointType.Point, NS = NS0 + north / 240.0, EW = EW0 + east / 240.0 };

    private sealed class MovedPlanner : INavRecoveryPlanner
    {
        public bool Moved;
        public NavRecoveryPlan? TryPlan(NavPoint target, out string why)
        {
            why = string.Empty;
            var plan = new NavRecoveryPlan { Planner = "dungeon map (room)", LengthYd = 20, TargetMoved = Moved };
            plan.Points.Add(Pt(0, 10));
            plan.Points.Add(Pt(25, 10));
            return plan;
        }
        public NavAreaCheck CheckArea(uint playerCell, double tNs, double tEw, out string routeArea, out string playerArea)
        {
            routeArea = playerArea = string.Empty;
            return NavAreaCheck.Match;
        }
    }

    private static void TargetMovedEngine()
    {
        foreach (bool moved in new[] { true, false })
        {
            FakeHost.Reset();
            At(0, 0);
            _now = 1_000_000;
            NavigationEngine.Clock = static () => _now;
            var route = new NavRouteParser { RouteType = NavRouteType.Circular };
            route.Points.Add(Pt(30, 0));     // the waypoint (inside a statue, as the planner says)
            route.Points.Add(Pt(30, 40));
            var s = new LegacyUiSettings { IsMacroRunning = true, EnableNavigation = true, CurrentRoute = route, ActiveNavIndex = 0 };
            var e = new NavigationEngine(FakeHost.Create(), s);
            e.SetRecoveryPlanner(new MovedPlanner { Moved = moved });
            for (int i = 0; i < 110; i++) { _now += 100; e.Tick(); }   // stuck x1 (jump), x2: the detour
            Check.True(e.GetStateSnapshot().Recovery.Contains("point 1/2"), "a detour: " + e.GetStateSnapshot().Recovery);
            At(0, 10); _now += 100; e.Tick();
            At(25, 10); _now += 100; e.Tick();
            if (moved)
            {
                Check.Eq(s.ActiveNavIndex, 1, "its last point reached: the waypoint inside the statue counts as reached, on to the next");
                Check.True(FakeHost.Logs.Any(l => l.Contains("reached the spot nearest [0]")), "the recovery line says so");
            }
            else
                Check.Eq(s.ActiveNavIndex, 0, "a plain detour rejoins at the waypoint as before");
        }
    }

    // Item 5: high room (0..10) at 0, an opening at x 10 (y 3..7) onto air (10..20) over a low room at -10.
    private static void JumpLandings()
    {
        var graph = new Dictionary<uint, DungeonNavNode>();
        uint high = 0x7E7E0100, air = 0x7E7E0101, low = 0x7E7E0102;
        graph[high] = new DungeonNavNode { CellId = high, EW = DungeonGeometry.WorldToNav(5), NS = DungeonGeometry.WorldToNav(5), Z = 0 };
        graph[air] = new DungeonNavNode { CellId = air, EW = DungeonGeometry.WorldToNav(15), NS = DungeonGeometry.WorldToNav(5), Z = 0 };
        graph[low] = new DungeonNavNode { CellId = low, EW = DungeonGeometry.WorldToNav(15), NS = DungeonGeometry.WorldToNav(5), Z = -10 };
        graph[high].Neighbors.Add(air); graph[air].Neighbors.Add(high);
        graph[air].Neighbors.Add(low); graph[low].Neighbors.Add(air);
        var floors = new List<(uint, V3[])>
        {
            (high, new[] { V(0, 0, 0), V(10, 0, 0), V(10, 10, 0), V(0, 10, 0) }),
            (low, new[] { V(10, 0, -10), V(20, 0, -10), V(20, 10, -10), V(10, 10, -10) }),
        };
        var quad = new[] { V(10, 3, 0), V(10, 7, 0), V(10, 7, 3), V(10, 3, 3) };
        var portals = new List<(uint, V3[])> { (high, quad), (air, quad.Reverse().ToArray()) };

        var before = DungeonPathfinder.StaticMeshSource;
        try
        {
            List<DungeonJump> Jumps(params (uint Setup, V3[] Tris)[] statics)
            {
                var list = statics.Select(s =>
                {
                    var min = V(s.Tris.Min(v => v.X), s.Tris.Min(v => v.Y), s.Tris.Min(v => v.Z));
                    var max = V(s.Tris.Max(v => v.X), s.Tris.Max(v => v.Y), s.Tris.Max(v => v.Z));
                    return (s.Setup, low, new BoundingVolume { Type = BoundingVolume.VolumeType.TriangleMesh, Min = min, Max = max, Center = (min + max) * 0.5f, Dimensions = max - min, MeshTriangles = s.Tris, NoAabbFallback = true });
                }).ToList();
                DungeonPathfinder.StaticMeshSource = lb => list;
                var geo = DungeonGeometry.Build(graph, portals, floors, Array.Empty<(uint, V3[])>());
                geo.AddPlacedObjects(list.Select(s => (s.Item1, s.Item2, s.Item3.MeshTriangles)));
                return DungeonPathfinder.FindJumps(graph, geo);
            }

            var plain = Jumps();
            Check.Eq(plain.Count, 1, "nothing placed: one jump");
            Check.Near(plain[0].LandX, 12.0, 0.01, "landing 2 m past the edge (the first spot tried)");

            // An altar on the low floor at the first landing spots (x 10.6..13.2): inside it is no landing.
            var altar = Jumps((0x02000969, Box(10.6, 3, -10, 13.2, 7, -9)));
            Check.Eq(altar.Count, 1, "an altar under the first spots: still a jump");
            Check.True(altar[0].LandX >= 13.9, $"landing past the altar ({altar[0].LandX:F1})");

            // A platform half-way down over the first spots (0x000A's slabs over its hall's hole): a fall meets it.
            var platform = Jumps((0x02000222, Box(10.5, 2, -5.3, 13.2, 8, -5)));
            Check.Eq(platform.Count, 1, "a platform over the first spots: still a jump");
            Check.True(platform[0].LandX >= 13.9 && platform[0].LandZ < -9, $"landing past the platform, on the low floor ({platform[0].LandX:F1}, {platform[0].LandZ:F1})");

            // A platform over every spot: no jump there.
            Check.Eq(Jumps((0x02000224, Box(10.2, 0, -5.3, 20, 10, -5))).Count, 0, "a deck over the whole drop: no jump (it would land on it)");
        }
        finally { DungeonPathfinder.StaticMeshSource = before; }
    }

    // Item 6.
    private static void PortalLegs()
    {
        const uint cell = 0x7E7E0400;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(15), NS = DungeonGeometry.WorldToNav(10), Z = 0 } };
        var floors = new List<(uint, V3[])> { (cell, new[] { V(0, 0, 0), V(30, 0, 0), V(30, 20, 0), V(0, 20, 0) }) };
        var geo = DungeonGeometry.Build(graph, Array.Empty<(uint, V3[])>(), floors, Array.Empty<(uint, V3[])>());
        geo.AddPlacedObjects(new (uint, uint, V3[])[] { (0x02000969, cell, Box(8, 9.2, 0, 10, 10.8, 1.0)) });

        // A leg the portal step made (a dropped point's neighbours joined): walked round the altar, out of the portal.
        var far = new List<PatrolPortal> { new() { Id = 1, Name = "exit", Cell = cell, X = 24, Y = 4, Z = 0 } };
        var pts = new List<NavPoint> { P(2, 10), P(28, 10) };
        var none = new Dictionary<(NavPoint, NavPoint), (double, double, double, double, double, double)>();
        Check.Eq(DungeonPathfinder.WalkPortalLegsRoundObjects(pts, false, geo, far, none), 1, "the changed leg through the altar is walked round");
        Check.Eq(WalkInside(geo, pts), 0, "no sample inside the altar");
        for (int i = 0; i + 1 < pts.Count; i++)
            Check.True(DungeonPathfinder.PortalLegClear(null, far, pts[i], pts[i + 1]), $"leg {i} keeps out of the portal");

        // A leg as laid is not looked at again (EmitSteps walked it round; it is counted there).
        var laid = new List<NavPoint> { P(2, 10), P(28, 10) };
        var asLaid = new Dictionary<(NavPoint, NavPoint), (double, double, double, double, double, double)>
        { [(laid[0], laid[1])] = (laid[0].EW, laid[0].NS, laid[0].Z, laid[1].EW, laid[1].NS, laid[1].Z) };
        Check.Eq(DungeonPathfinder.WalkPortalLegsRoundObjects(laid, false, geo, far, asLaid), 0, "an unchanged leg is left to EmitSteps");

        // Portals either side of the altar: every way round goes into a circle. The portal wins: the leg stays, counted.
        var both = new List<PatrolPortal>
        {
            new() { Id = 2, Name = "north", Cell = cell, X = 9, Y = 14.6, Z = 0 },
            new() { Id = 3, Name = "south", Cell = cell, X = 9, Y = 5.4, Z = 0 },
        };
        var tight = new List<NavPoint> { P(2, 10), P(16, 10) };
        Check.True(DungeonPathfinder.PortalLegClear(null, both, tight[0], tight[1]), "the straight leg itself keeps out of both circles (4.6 m from their middles)");
        using (DungeonPathfinder.CountHoles())
        {
            Check.Eq(DungeonPathfinder.WalkPortalLegsRoundObjects(tight, false, geo, both, none), 0, "no way round that keeps out of the portals");
            Check.Eq(tight.Count, 2, "the leg as it was");
            Check.Eq(DungeonPathfinder.HolesLeft, 1, "counted as having no way round");
        }
    }

    // Items 7 and 8: the walk test.
    private static void WalkTest()
    {
        const uint cell = 0x7E7E0500;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(10), NS = DungeonGeometry.WorldToNav(10), Z = 0 } };
        var floors = new List<(uint, V3[])> { (cell, new[] { V(0, 0, 0), V(20, 0, 0), V(20, 20, 0), V(0, 20, 0) }) };
        var geo = DungeonGeometry.Build(graph, Array.Empty<(uint, V3[])>(), floors, Array.Empty<(uint, V3[])>());
        geo.AddPlacedObjects(new (uint, uint, V3[])[] { (0x020002FB, cell, Box(9, 8, 0, 10.4, 11, 0.9)) });
        var a = P(5, 9.5); var b = P(14, 9.5);
        // A raised sight line: the eye 1.2 m up, walls looked for from 0.25 to 1.8 m over that.
        Check.True(geo.SightClear(5, 9.5, 1.2, 14, 9.5, 1.2), "the eye-height sight line passes over the 0.9 m table");
        Check.False(DungeonPathfinder.StraightWalkClear(geo, a, b), "the walk test at body height stops at it");
        Check.True(DungeonPathfinder.StraightWalkClear(geo, P(5, 14), P(14, 14)), "a walk beside it passes");
    }

    // Item 10.
    private static void EscapeRoomTest()
    {
        const uint cell = 0x7E7E0600;
        var graph = new Dictionary<uint, DungeonNavNode> { [cell] = new DungeonNavNode { CellId = cell, EW = DungeonGeometry.WorldToNav(10), NS = DungeonGeometry.WorldToNav(10), Z = 0 } };
        var floors = new List<(uint, V3[])> { (cell, new[] { V(0, 0, 0), V(20, 0, 0), V(20, 20, 0), V(0, 20, 0) }) };
        var geo = DungeonGeometry.Build(graph, Array.Empty<(uint, V3[])>(), floors, Array.Empty<(uint, V3[])>());
        // Stuck facing north at an altar (its face 0.48 m off), a crate right beside it to the east.
        geo.AddPlacedObjects(new (uint, uint, V3[])[]
        {
            (0x02000969, cell, Box(9, 10.48, 0, 11, 12, 1.0)),
            (0x0200032A, cell, Box(10.6, 8.5, 0, 12, 10.48, 1.2)),
        });
        Func<double, double> room = h => NavRecoveryPlanner.EscapeRoomOn(geo, 10, 10, 0, h);
        Check.True(room(75) < NavigationEngine.EscapeRoomTight, $"east-north-east runs head-on into the crate ({room(75):F2} m)");
        Check.True(room(285) >= NavigationEngine.EscapeRoomTight, $"west-north-west slides along the altar's face a way ({room(285):F2} m)");
        Check.True(room(195) >= 1.9, $"south-south-west is open ({room(195):F2} m)");

        double off = 75;   // the x2 rung
        Check.True(NavigationEngine.PickRoomiestEscape(0, ref off, room, out string? note), "the +75 burst is turned");
        Check.True(off != 75 && room(off < 0 ? off + 360 : off) >= 1.9, $"to the heading with the most room ({off:+0;-0}: {room(off < 0 ? off + 360 : off):F2} m)");
        Check.True(note != null && note.Contains("for room on the dungeon map"), "the line says why: " + note);

        off = -75;   // the x3 rung: it slides along the altar
        Check.False(NavigationEngine.PickRoomiestEscape(0, ref off, room, out _), "a burst that gets on keeps its rung's heading");
        Check.Eq(off, -75.0, "unchanged");
        off = 75;
        Check.False(NavigationEngine.PickRoomiestEscape(0, ref off, _ => double.NaN, out _), "no map: as before");
        Check.Eq(off, 75.0, "unchanged");
    }

    // ── Real dats ─────────────────────────────────────────────────────────────────────────────

    private static GeometryLoader? Loader(out Func<uint, List<(uint, uint, BoundingVolume)>>? before)
    {
        before = DungeonPathfinder.StaticMeshSource;
        string acDir = DunRouteTests.AcDir;
        if (!Directory.Exists(acDir)) { Console.WriteLine("    (no dats on this PC: skipped)"); return null; }
        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("    (geometry loader failed: " + loader.StatusMessage + ")"); return null; }
        DungeonPathfinder.StaticMeshSource = lb => loader.GetDungeonStaticMeshes(lb);
        return loader;
    }

    // The straight walk from a to b meets something placed: the spot a body's front (0.48 m ahead) first touches it.
    private static (double X, double Y) StuckSpot(DungeonGeometry geo, double ax, double ay, double bx, double by, double z)
    {
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        dx /= len; dy /= len;
        for (double s = 0; s < len; s += 0.05)
            if (geo.InsidePlaced(ax + dx * (s + 0.48), ay + dy * (s + 0.48), z)) return (ax + dx * s, ay + dy * s);
        return (bx, by);
    }

    private static void InRoomReal()
    {
        var loader = Loader(out var before);
        if (loader == null) return;
        try
        {
            // The logged cases: 0x0190 wp120 -> wp121 (room 88, the table 0x020002FB), 0x00C1 wp45 -> wp46 (room 81, the statue).
            foreach (var (lb, ax, ay, bx, by, z, name) in new[]
            {
                (0x0190u, 253.0, 27598.0, 262.0, 27598.0, -12.0, "0x0190 room 88 table"),
                (0x00C1u, 165.0, 36996.0, 150.0, 36996.0, -54.0, "0x00C1 room 81 statue"),
            })
            {
                DungeonPathfinder.InvalidateCache();
                var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
                var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
                var (sx, sy) = StuckSpot(geo, ax, ay, bx, by, z);
                Check.True(Math.Abs(sx - bx) + Math.Abs(sy - by) > 0.5, $"{name}: the straight walk meets it ({sx:F2},{sy:F2})");
                uint cs = DungeonPathfinder.CellAt(graph, geo, P(sx, sy, z)), ct = DungeonPathfinder.CellAt(graph, geo, P(bx, by, z));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var plan = NavRecoveryPlanner.PlanInRoom(geo, DungeonGeometry.WorldToNav(sy), DungeonGeometry.WorldToNav(sx), z,
                                                         DungeonGeometry.WorldToNav(by), DungeonGeometry.WorldToNav(bx), z, out string why);
                double ms = clock.Elapsed.TotalMilliseconds;
                Check.NotNull(plan, $"{name}: an in-room plan ({why})");
                var walk = new List<NavPoint> { P(sx, sy, z) };
                walk.AddRange(plan!.Points);
                if (!plan.TargetMoved) walk.Add(P(bx, by, z));
                double least = 9;
                for (int i = 0; i + 1 < walk.Count; i++)
                    least = Math.Min(least, geo.LegClearance(X(walk[i]), Y(walk[i]), z, X(walk[i + 1]), Y(walk[i + 1]), z, 2.0, 0.3, out _, out _, out _));
                var end = walk[^1];
                Console.WriteLine($"    {name}: stuck at ({sx:F2},{sy:F2}) cell {cs:X8}, waypoint cell {ct:X8}; {plan.Planner}, {plan.Points.Count} point(s), {plan.LengthYd:F1} yd, "
                                  + $"{ms:F1} ms; end ({X(end):F1},{Y(end):F1}) moved {Math.Sqrt(Math.Pow(X(end) - bx, 2) + Math.Pow(Y(end) - by, 2)):F1} m; least clearance along it {least:F2}");
                Check.Eq(WalkInside(geo, walk), 0, $"{name}: no sample of the plan inside a placed object");
                if (lb == 0x00C1)
                {
                    Check.True(plan.TargetMoved, "the statue's waypoint is moved out of it");
                    Check.True(geo.Clearance(X(end), Y(end), z) >= NavRecoveryPlanner.InRoomTargetClearance - 0.05, $"to room ({geo.Clearance(X(end), Y(end), z):F2})");
                }
                else Check.False(plan.TargetMoved, "the table's waypoint stays where it is");

                // The escape bursts there (item 10): how far each rung's heading gets on the map.
                double facing = Math.Atan2(bx - ax, by - ay) * 180 / Math.PI;
                var rooms = new[] { 75.0, -75.0, 165.0, -165.0 }.Select(o => (o, NavRecoveryPlanner.EscapeRoomOn(geo, sx, sy, z, (facing + o + 720) % 360))).ToList();
                Console.WriteLine($"    {name}: escape room facing {(facing + 360) % 360:F0}: " + string.Join(", ", rooms.Select(r => $"{r.o:+0;-0} {r.Item2:F1} m")));
                Check.True(rooms.All(r => !double.IsNaN(r.Item2)), "the map says how far each burst gets");
            }
        }
        finally
        {
            DungeonPathfinder.StaticMeshSource = before;
            DungeonPathfinder.InvalidateCache();
            loader.Dispose();
        }
    }

    private static void PortalPatrolsReal()
    {
        var loader = Loader(out var before);
        if (loader == null) return;
        try
        {
            foreach (var (lb, startLow) in new[] { (0x000Au, 0x0164u), (0x0169u, 0x016Au), (0x0190u, 0u) })
            {
                DungeonPathfinder.InvalidateCache();
                var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
                var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS)!;
                uint start = startLow != 0 ? (lb << 16) | startLow : graph.Keys.Min();
                Check.True(geo.TryCellFloorCentre(start, out double px, out double py, out double pz), $"{lb:X4}: the entry cell {start & 0xFFFF:X4} has a floor");
                var exit = new PatrolPortal { Id = 0x70000001, Name = "Thread Exit", Cell = start, X = px, Y = py, Z = pz };
                var portals = new List<PatrolPortal> { exit };
                var o = DungeonPatrolOptions.Default with { Coverage = PatrolCoverage.Everything };
                var walkOut = DungeonPathfinder.PortalWalkOut(geo, portals, px, py, pz, o.WallClearance, graph: graph, startCell: start);
                (double X, double Y, double Z) at = walkOut != null ? (X(walkOut), Y(walkOut), walkOut.Z * 240) : (px, py, pz);
                uint pStart = walkOut != null && graph.ContainsKey(DungeonPathfinder.CellAt(graph, geo, walkOut)) ? DungeonPathfinder.CellAt(graph, geo, walkOut) : start;
                var route = DungeonPathfinder.BuildPatrolRoute(graph, pStart, null, geo, o, portals, at);
                var pts = route.Points;
                int through = 0, inCircle = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    if (SamplesInside(geo, pts[i], pts[(i + 1) % pts.Count]) > 0) through++;
                    if (DungeonPathfinder.LegDistance(exit, pts[i], pts[(i + 1) % pts.Count], out _) < exit.Radius - 0.01) inCircle++;
                }
                var rep = DungeonPathfinder.LastPortalAvoidance?.FirstOrDefault();
                Console.WriteLine($"    {lb:X4} from {pStart & 0xFFFF:X4}, exit portal at ({px:F1},{py:F1},{pz:F1}): {pts.Count} points; {through} leg(s) through an object, {inCircle} in the portal's circle; "
                                  + $"bent {rep?.Bent}, moved {rep?.Moved}, dropped {rep?.Dropped}; walk-rounds {DungeonPathfinder.HolesWalkedRound}, no way round {DungeonPathfinder.HolesLeft}");
                Check.True(pts.Count > 20, $"{lb:X4}: a patrol");
                Check.Eq(through, 0, $"{lb:X4}: no leg through an object");
                Check.Eq(inCircle, 0, $"{lb:X4}: no leg into the exit portal's circle");
            }
        }
        finally
        {
            DungeonPathfinder.StaticMeshSource = before;
            DungeonPathfinder.InvalidateCache();
            loader.Dispose();
        }
    }
}

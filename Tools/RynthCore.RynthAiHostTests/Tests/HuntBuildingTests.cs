using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.RynthAiTests.Harness;
using V3 = RynthCore.Plugin.RynthAi.Raycasting.Vector3;
using Vec = RynthCore.Plugin.RynthAi.OutdoorHunt.Vec;

namespace RynthCore.RynthAiHostTests.Tests;

// The outdoor hunt into buildings (2026-10-25, Tom: "the entire radar range, also going inside houses").
// RynthNav's map ends at a building's walls: a walk into one is RynthNav's way to the outside of a door,
// then the building's rooms (BuildingMap, cell.dat); from inside, out through a door first.
//   - pure (OutdoorHunt with a fake building map and a straight-line RynthNav): which monster, which
//     door, the corners, the log line, the fallbacks;
//   - BuildingMap on a hand-built house (doors, windows, a shared opening, portals inside);
//   - BuildingMap on the real dats when they are on this PC (Holtburg A9B4 / A9B3, DF61).
internal static class HuntBuildingTests
{
    private const string AcDir = @"C:\Turbine\Asheron's Call";

    public static void Register(Runner r)
    {
        r.Add("hunt buildings: a monster inside is picked when its walk is the shortest; the walk goes to the door, through it, then the rooms", IntoBuilding);
        r.Add("hunt buildings: of a building's two doors, the one giving the shorter walk (the other's way round never asked for)", NearerDoor);
        r.Add("hunt buildings: from inside, a monster outdoors is walked to out through the nearer door, then on RynthNav's map", OutOfBuilding);
        r.Add("hunt buildings: inside a building with no door known (or none known at all): back out the way in", NoDoorFallsBack);
        r.Add("hunt buildings: one in the same building through its rooms; one in another building out of one door and in by another", SameAndOtherBuilding);
        r.Add("hunt buildings: a door whose step out isn't on the ground is never used; a building it doesn't know is left alone", DoorOffTheGround);
        r.Add("building map: a hand-built house: its door faces out, a window and a slit are no doors, a shared opening joins two buildings", HandBuiltHouse);
        r.Add("building map: a portal inside: walks bend round it, one to a monster in its circle stops at its edge, a door it stands at isn't offered", PortalInside);
        r.Add("building map (real dats): Holtburg A9B4/A9B3: doors found, each step out off every floor and each step in on one, a walk from a door to every room", RealHoltburg);
        r.Add("building map (real dats): DF61 (a test hunt's): openings shared by two cell groups joined, doors onto the wall tops left out, walks through", RealDf61);
        r.Add("hunt buildings (real dats): into Holtburg's A9B4#0 from outdoors and back out, a portal in its rooms kept out of", RealHuntHoltburg);
    }

    // ── Fakes ────────────────────────────────────────────────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 10, 25, 12, 0, 0);
    private static Vec V(double x, double y, double z = 0) => new(x, y, z);
    private static Vec D(double x, double y, double z = 0) => new(x, y, z, true);

    private const uint Outdoors = 0xA9B4003Au;
    private static OutdoorHunt.Candidate C(int id, Vec at, Vec me, uint cell = Outdoors, string? name = null)
        => new(id, name ?? $"Monster {id}", at, at.DistanceTo(me), 1, cell);

    /// <summary>A west-facing door at (x, y) of cell: inner point 1.5 east, outer 1.5 west.</summary>
    private static OutdoorHunt.Door West(uint cell, double x, double y) => new(cell, D(x + 1.5, y), D(x, y), D(x - 1.5, y));
    private static OutdoorHunt.Door East(uint cell, double x, double y) => new(cell, D(x - 1.5, y), D(x, y), D(x + 1.5, y));

    /// <summary>Buildings as cells to building ids and doors; inside, a straight walk (NoPath between buildings or cells kept apart).</summary>
    private sealed class FakeBuildings : OutdoorHunt.IBuildings
    {
        public readonly Dictionary<uint, int> Of = new();
        public readonly Dictionary<int, List<OutdoorHunt.Door>> DoorsOf = new();
        public readonly HashSet<(uint, uint)> Apart = new();
        public int InsideCalls;
        public int BuildingOf(uint cell) => Of.TryGetValue(cell, out int b) ? b : 0;
        public IReadOnlyList<OutdoorHunt.Door> Doors(int b) => DoorsOf.TryGetValue(b, out var l) ? l : new List<OutdoorHunt.Door>();
        public OutdoorHunt.PathResult Inside(uint fc, Vec f, uint tc, Vec t)
        {
            InsideCalls++;
            int b = BuildingOf(fc);
            if (b == 0 || b != BuildingOf(tc) || Apart.Contains((fc, tc)) || Apart.Contains((tc, fc)))
                return new(OutdoorHunt.PathStatus.NoPath, Array.Empty<Vec>(), 0, 0);
            return new(OutdoorHunt.PathStatus.Ok, new[] { t }, f.DistanceTo(t), 0);
        }
        public string Name(int b) => $"house {b}";
    }

    /// <summary>RynthNav: a straight walk, with per-goal overrides; every goal asked for is noted.</summary>
    private sealed class Nav
    {
        public readonly Dictionary<(double, double), OutdoorHunt.PathResult> Overrides = new();
        /// <summary>The map's ground at a doorstep (a path from a spot to itself): its height there.</summary>
        public readonly Dictionary<(double, double), double> Ground = new();
        public readonly List<(Vec A, Vec B)> Asked = new();
        public OutdoorHunt.PathResult Path(Vec a, Vec b)
        {
            Asked.Add((a, b));
            if (a.X == b.X && a.Y == b.Y && Ground.TryGetValue((b.X, b.Y), out double gz))
                return new(OutdoorHunt.PathStatus.Ok, new[] { V(b.X, b.Y, gz) }, Math.Abs(gz - b.Z), Math.Abs(gz - b.Z));
            if (Overrides.TryGetValue((b.X, b.Y), out var o)) return o;
            return new(OutdoorHunt.PathStatus.Ok, new[] { b with { Door = false } }, a.DistanceTo(b), 0);
        }
        public void Around(Vec to, double length) =>
            Overrides[(to.X, to.Y)] = new(OutdoorHunt.PathStatus.Ok, new[] { V(to.X - 5, to.Y + 8), to with { Door = false } }, length, 0);
        public int Walks => Asked.Count(q => q.A != q.B);
    }

    private static List<OutdoorHunt.Candidate> Sorted(params OutdoorHunt.Candidate[] c) => c.OrderBy(x => x.Distance).ToList();
    private static int Idx(IReadOnlyList<Vec> c, Vec v)
    {
        for (int i = 0; i < c.Count; i++) if (c[i] == v) return i;
        return -1;
    }

    private static string Pts(IReadOnlyList<Vec>? c) => c == null ? "(none)" : string.Join(" ", c.Select(v => $"{v.X:0.#},{v.Y:0.#}{(v.Door ? "D" : "")}"));

    // A house: cells 0101 (west room) and 0102 (east room), x 1020..1030, y 995..1005; doors given per test.
    private const uint RoomW = 0xA9B40101, RoomE = 0xA9B40102, House2 = 0xA9B40110;

    private static FakeBuildings House(params OutdoorHunt.Door[] doors)
    {
        var b = new FakeBuildings();
        b.Of[RoomW] = 1; b.Of[RoomE] = 1;
        b.DoorsOf[1] = doors.ToList();
        return b;
    }

    // ── Pure ─────────────────────────────────────────────────────────────────────────────────────

    private static void IntoBuilding()
    {
        var me = V(1000, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        var house = House(West(RoomW, 1020, 1000));
        var inside = V(1025, 1000);
        var list = Sorted(C(1, inside, me, RoomW), C(2, V(1000, 1040), me));
        var d = h.Tick(T0, me, Outdoors, true, list, nav.Path, house);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.TargetId == 1, $"walks to the one inside (25 yd walk) before the one 40 yd away in the open: {d.Act} {d.TargetId}");
        var want = new[] { D(1018.5, 1000), D(1020, 1000), D(1021.5, 1000), inside };
        Check.True(d.Corners != null && d.Corners.SequenceEqual(want), "to the door's step out, through it, a step in, then the rooms to it: " + Pts(d.Corners));
        Check.True(h.Note.StartsWith("next: 'Monster 1', inside house 1") && h.Note.Contains($"cell {RoomW:X8}")
                   && h.Note.Contains("a 25 yd walk through the door at 1020,1000"), "logged: " + h.Note);

        // The same building behind a long way round (RynthNav: 150 yd to the door): too far round (over 2x Hunt Range 60).
        var far = new OutdoorHunt { RangeYd = 60 };
        far.Start(T0, me);
        var nav2 = new Nav();
        nav2.Around(V(1018.5, 1000), 150);
        d = far.Tick(T0, me, Outdoors, true, Sorted(C(1, inside, me, RoomW), C(2, V(1000, 1040), me)), nav2.Path, house);
        Check.True(d.TargetId == 2 && far.IsSkipped(1, T0.AddSeconds(5)), $"a 150 yd walk into it is too far round: the other one ({d.TargetId})");
    }

    private static void NearerDoor()
    {
        // East of the house; the west door is round the far side (RynthNav: 45 yd), the east door is 13.5 yd away.
        var me = V(1045, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var nav = new Nav();
        var west = West(RoomW, 1020, 1000);
        var east = East(RoomE, 1030, 1000);
        nav.Around(west.Outside, 45);
        var house = House(west, east);
        var d = h.Tick(T0, me, Outdoors, true, Sorted(C(1, V(1027, 1000), me, RoomE)), nav.Path, house);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.Corners!.Contains(east.At) && !d.Corners!.Contains(west.At), "through the east door: " + Pts(d.Corners));
        Check.True(h.Note.Contains("through the door at 1030,1000"), "logged with that door: " + h.Note);
        Check.False(nav.Asked.Any(q => q.A != q.B && q.B.X == west.Outside.X && q.B.Y == west.Outside.Y),
                    "the west door's way round is never asked for (it can't beat the east door even in a straight line)");
        Check.Eq(nav.Walks, 1, "one RynthNav walk for the pick (and a look at each doorstep)");
    }

    private static void OutOfBuilding()
    {
        var me = V(1022, 1000);
        var h = new OutdoorHunt();
        h.Start(T0, V(990, 1000));
        var nav = new Nav();
        var west = West(RoomW, 1020, 1000);
        var east = East(RoomE, 1030, 1000);
        var house = House(west, east);
        var target = V(990, 1000);
        var d = h.Tick(T0, me, RoomW, true, Sorted(C(1, target, me)), nav.Path, house);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.TargetId == 1, $"walks to it from inside: {d.Act}");
        var want = new[] { D(1021.5, 1000), D(1020, 1000), D(1018.5, 1000), target };
        Check.True(d.Corners != null && d.Corners.SequenceEqual(want), "a step in front of the west door, through it, its step out, then RynthNav's way: " + Pts(d.Corners));
        Check.True(nav.Asked.Any(q => q.A == west.Outside && q.B == target), "RynthNav asked from the west door's step out");
        Check.True(h.Note.Contains("out of house 1 through the door at 1020,1000") && !h.WalkingOut, "logged, not the way-back-out walk: " + h.Note);

        // A monster east: out through the east door (the nearer one for that walk).
        var h2 = new OutdoorHunt();
        h2.Start(T0, me);
        d = h2.Tick(T0, me, RoomW, true, Sorted(C(2, V(1060, 1000), me)), new Nav().Path, house);
        Check.True(d.Corners != null && d.Corners.Contains(east.At) && !d.Corners.Contains(west.At), "east one: out of the east door: " + Pts(d.Corners));
    }

    private static void NoDoorFallsBack()
    {
        var h = new OutdoorHunt();
        h.Start(T0, V(990, 1000));
        h.Track(V(1018, 1000), indoors: false);   // the last spot outdoors, at the door
        h.Track(V(1020, 1000), indoors: true);
        h.Track(V(1022, 1000), indoors: true);
        var noDoors = House();                     // known rooms, no door
        var nav = new Nav();
        var d = h.Tick(T0, V(1022, 1000), RoomW, true, Sorted(C(1, V(990, 1000), V(1022, 1000))), nav.Path, noDoors);
        Check.True(d.Act == OutdoorHunt.Act.Walk && h.WalkingOut && h.Note.Contains("back out the way in"), $"no door known: back out the way in ({d.Act}, {h.Note})");
        Check.True(d.Corners != null && d.Corners.Contains(V(1018, 1000)), "along the way in: " + Pts(d.Corners));
        Check.Eq(nav.Asked.Count, 0, "no RynthNav walk from inside");

        var h2 = new OutdoorHunt();
        h2.Start(T0, V(990, 1000));
        h2.Track(V(1018, 1000), indoors: false);
        h2.Track(V(1021, 1000), indoors: true);
        d = h2.Tick(T0, V(1021, 1000), RoomW, true, Sorted(C(1, V(990, 1000), V(1021, 1000))), nav.Path, null);
        Check.True(d.Act == OutdoorHunt.Act.Walk && h2.WalkingOut, "no building map at all: the same");
    }

    private static void SameAndOtherBuilding()
    {
        var me = V(1022, 1000);
        var west = West(RoomW, 1020, 1000);
        var house = House(west, East(RoomE, 1030, 1000));
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var d = h.Tick(T0, me, RoomW, true, Sorted(C(1, V(1028, 1002), me, RoomE)), new Nav().Path, house);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.Corners!.SequenceEqual(new[] { V(1028, 1002) }), "same building: through its rooms only: " + Pts(d.Corners));
        Check.True(h.Note.Contains("through its rooms"), "logged: " + h.Note);

        // Another house north, its door on its south wall at (1025, 1030), step out (1025, 1028.5).
        house.Of[House2] = 2;
        var south = new OutdoorHunt.Door(House2, D(1025, 1031.5), D(1025, 1030), D(1025, 1028.5));
        house.DoorsOf[2] = new List<OutdoorHunt.Door> { south };
        var h2 = new OutdoorHunt();
        h2.Start(T0, me);
        var goal = V(1025, 1035);
        d = h2.Tick(T0, me, RoomW, true, Sorted(C(2, goal, me, House2)), new Nav().Path, house);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.Corners![^1] == goal && d.Corners.Contains(south.At)
                   && Idx(d.Corners, south.Outside) > Idx(d.Corners, west.Outside), "out of this house, across, in by the other's door: " + Pts(d.Corners));
        Check.True(h2.Note.Contains("inside house 2") && h2.Note.Contains("out of house 1") && h2.Note.Contains("in through the door at 1025,1030"), "logged: " + h2.Note);

        // Two parts of one building that only the outside joins: out by one door and in by another.
        house.Apart.Add((RoomW, RoomE));
        var h3 = new OutdoorHunt();
        h3.Start(T0, me);
        d = h3.Tick(T0, me, RoomW, true, Sorted(C(3, V(1028, 1000), me, RoomE)), new Nav().Path, house);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.Corners!.Contains(west.At) && d.Corners.Contains(D(1030, 1000)), "rooms not joined inside: out of the west door, in at the east: " + Pts(d.Corners));
    }

    private static void DoorOffTheGround()
    {
        var me = V(1000, 1000);
        var balcony = new OutdoorHunt.Door(RoomW, D(1021.5, 1000, 4), D(1020, 1000, 4), D(1018.5, 1000, 4));
        var nav = new Nav();
        nav.Ground[(1018.5, 1000)] = 0;   // the ground under the balcony's step out is 4 below it
        var house = House(balcony);
        var h = new OutdoorHunt();
        h.Start(T0, me);
        h.ClearNotes();
        var d = h.Tick(T0, me, Outdoors, true, Sorted(C(1, V(1025, 1000, 4), me, RoomW), C(2, V(1000, 1040), me)), nav.Path, house);
        Check.True(d.TargetId == 2 && h.IsSkipped(1, T0.AddSeconds(5)), $"the only door is a balcony's: the one inside is left out ({d.TargetId})");
        Check.True(h.PendingNotes.Any(n => n.Contains("Monster 1") && n.Contains("no door of house 1 leads to it")), "and says why: " + string.Join(" | ", h.PendingNotes));

        // With a door on the ground too: that one.
        var ground = East(RoomE, 1030, 1000);
        var house2 = House(balcony, ground);
        var h2 = new OutdoorHunt();
        h2.Start(T0, me);
        d = h2.Tick(T0, me, Outdoors, true, Sorted(C(1, V(1025, 1000, 4), me, RoomW)), nav.Path, house2);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.Corners!.Contains(ground.At) && !d.Corners.Contains(balcony.At), "the ground door, round the far side: " + Pts(d.Corners));

        // A monster in a building the map doesn't know: left alone (no walk, not left out, no log line).
        var h3 = new OutdoorHunt();
        h3.Start(T0, me);
        h3.ClearNotes();
        d = h3.Tick(T0, me, Outdoors, true, Sorted(C(1, V(1010, 1000), me, 0xA9B40150), C(2, V(1000, 1030), me)), new Nav().Path, house);
        Check.True(d.TargetId == 2 && !h3.IsSkipped(1, T0), $"an unknown building's monster is passed over ({d.TargetId})");
        Check.False(h3.PendingNotes.Any(n => n.Contains("Monster 1")), "without a word: " + string.Join(" | ", h3.PendingNotes));
    }

    // ── A hand-built house ───────────────────────────────────────────────────────────────────────

    private const uint Lb = 0xA9B4;
    private const uint CellA = 0xA9B40100, CellB = 0xA9B40101, CellC = 0xA9B40102, CellD = 0xA9B40103;

    private static V3[] Rect(double x0, double y0, double x1, double y1, double z = 0) => new[]
    {
        new V3((float)x0, (float)y0, (float)z), new V3((float)x1, (float)y0, (float)z),
        new V3((float)x1, (float)y1, (float)z), new V3((float)x0, (float)y1, (float)z),
    };

    /// <summary>An upright quad from (x0, y0) to (x1, y1), z0 to z1: a wall's opening.</summary>
    private static V3[] Upright(double x0, double y0, double x1, double y1, double z0 = 0, double z1 = 3) => new[]
    {
        new V3((float)x0, (float)y0, (float)z0), new V3((float)x1, (float)y1, (float)z0),
        new V3((float)x1, (float)y1, (float)z1), new V3((float)x0, (float)y0, (float)z1),
    };

    private static DungeonNavNode Node(uint id, double cx, double cy) =>
        new() { CellId = id, EW = DungeonGeometry.WorldToNav(cx), NS = DungeonGeometry.WorldToNav(cy), Z = 0 };

    /// <summary>
    /// House 1: A (100..110 x 100..110) and B (110..130 x 100..120), joined by a 4 m opening at x 110,
    /// y 103..107; a door in A's west wall (x 100, y 103..106), a window in A's north wall (foot 1 m
    /// up), a 0.5 m slit in B's east wall. House 2: C (200..210) and D (210..220), no portal record
    /// between them but one opening at x 210 that each sees as its way out; a door in C's west wall.
    /// </summary>
    private static BuildingMap.Block HouseBlock()
    {
        var graph = new Dictionary<uint, DungeonNavNode>
        {
            [CellA] = Node(CellA, 105, 105), [CellB] = Node(CellB, 120, 110),
            [CellC] = Node(CellC, 205, 105), [CellD] = Node(CellD, 215, 105),
        };
        graph[CellA].Neighbors.Add(CellB); graph[CellB].Neighbors.Add(CellA);
        var ab = Upright(110, 103, 110, 107);
        var portals = new List<(uint, V3[])> { (CellA, ab), (CellB, ab.Reverse().ToArray()) };
        var floors = new List<(uint, V3[])>
        {
            (CellA, Rect(100, 100, 110, 110)), (CellB, Rect(110, 100, 130, 120)),
            (CellC, Rect(200, 100, 210, 110)), (CellD, Rect(210, 100, 220, 110)),
        };
        var walls = new List<(uint, V3[])>();
        var door = Upright(100, 103, 100, 106);
        var window = Upright(103, 110, 105, 110, 1.0, 2.8);
        var slit = Upright(130, 110, 130, 110.5);
        var shared = Upright(210, 103, 210, 107);
        var doorC = Upright(200, 103, 200, 106);
        var exits = new List<(uint, V3[])>
        {
            (CellA, door), (CellA, window), (CellB, slit),
            (CellC, shared), (CellD, shared.Reverse().ToArray()), (CellC, doorC),
        };
        foreach (var e in exits) portals.Add(e);
        return BuildingMap.Build(Lb, graph, portals, floors, walls, exits);
    }

    private static void HandBuiltHouse()
    {
        var blk = HouseBlock();
        Check.Eq(blk.Buildings.Count, 2, "two buildings (C and D joined by the opening they share)");
        Check.Eq(blk.SharedOpenings, 1, "one shared opening joined");
        var h1 = blk.ByCell[CellA];
        Check.True(blk.ByCell[CellB] == h1 && blk.ByCell[CellC] == blk.ByCell[CellD] && blk.ByCell[CellC] != h1, "A+B and C+D");
        Check.Eq(h1.Doors.Count, 1, "house 1: one door");
        var d = h1.Doors[0];
        Check.True(d.Cell == CellA && Math.Abs(d.At.X - 100) < 0.01 && Math.Abs(d.At.Y - 104.5) < 0.01, $"in A's west wall: {d.At}");
        Check.True(d.Outside.X < 100 && Math.Abs(d.Outside.X - (100 - BuildingMap.OutsideMeters)) < 0.01, $"its step out is west, outside: {d.Outside}");
        Check.True(d.Inner.X > 100 && blk.Geo!.OnCellFloor(CellA, d.Inner.X, d.Inner.Y, 0), $"its step in is on A's floor: {d.Inner}");
        Check.True(d.Inner.Door && d.At.Door && d.Outside.Door, "all three lined up on the door (Door)");
        Check.True(h1.NotDoors.Any(n => n.Contains("window")) && h1.NotDoors.Any(n => n.Contains("0.5 m wide")), "the window and the slit aren't doors: " + string.Join(" | ", h1.NotDoors));
        var h2 = blk.ByCell[CellC];
        Check.True(h2.Doors.Count == 1 && Math.Abs(h2.Doors[0].At.X - 200) < 0.01, $"house 2: only its west door (the shared opening is inside it now): {h2.Doors.Count}");
        Check.True(blk.Geo!.TryGetDoorway(CellC, CellD, out _), "and the shared opening is a doorway between C and D");

        var map = new BuildingMap(null, null);
        map.Add(blk);
        Check.True(map.BuildingOf(CellA) != 0 && map.BuildingOf(CellA) == map.BuildingOf(CellB) && map.BuildingOf(0xA9B4003A) == 0, "BuildingOf: a cell's house; outdoors 0");
        Check.Eq(map.Name(map.BuildingOf(CellA)), "building A9B4#0", "named by landblock and index");
        var goal = V(125, 115);
        var p = map.Inside(CellA, d.Inner, CellB, goal);
        Check.True(p.Status == OutdoorHunt.PathStatus.Ok && p.Corners[^1] == goal, $"a walk from the door into B: {p.Status}");
        var way = new List<Vec> { d.Inner };
        way.AddRange(p.Corners);
        bool throughOpening = false;
        for (int i = 0; i + 1 < way.Count; i++)
        {
            var a = way[i]; var b = way[i + 1];
            if ((a.X - 110) * (b.X - 110) > 0) continue;   // this leg doesn't cross x 110
            double t = (110 - a.X) / (b.X - a.X), y = a.Y + (b.Y - a.Y) * t;
            if (y >= 103 && y <= 107) throughOpening = true;
        }
        Check.True(throughOpening, "through the opening between A and B: " + Pts(way));
        var p2 = map.Inside(CellC, h2.Doors[0].Inner, CellD, V(217, 105));
        Check.True(p2.Status == OutdoorHunt.PathStatus.Ok, $"house 2: C to D through the shared opening ({p2.Status})");
        Check.Eq(map.Inside(CellA, d.Inner, CellC, V(205, 105)).Status, OutdoorHunt.PathStatus.NoPath, "no walk inside from one house to the other");
    }

    private static void PortalInside()
    {
        var blk = HouseBlock();
        var map = new BuildingMap(null, null);
        map.Add(blk);
        var door = blk.ByCell[CellA].Doors[0];
        int house = map.BuildingOf(CellA);
        var portal = new BuildingMap.PortalSpot(0x7A9B4001, 118, 105, 0);
        map.Portals = new[] { portal };

        var goal = V(126, 105);
        var p = map.Inside(CellA, door.Inner, CellB, goal);
        var way = new List<Vec> { door.Inner };
        way.AddRange(p.Corners);
        double nearest = double.MaxValue;
        for (int i = 0; i + 1 < way.Count; i++) nearest = Math.Min(nearest, SegDist(way[i], way[i + 1], portal.X, portal.Y));
        Check.True(p.Status == OutdoorHunt.PathStatus.Ok && p.Corners[^1] == goal, $"walks on past it ({p.Status})");
        Check.True(nearest >= portal.R - 0.01, $"never inside its {portal.R} m circle (nearest {nearest:F2} m): {Pts(way)}");

        var inCircle = V(120, 106);
        p = map.Inside(CellA, door.Inner, CellB, inCircle);
        Check.True(p.Status == OutdoorHunt.PathStatus.Partial && p.Gap > 0, $"a monster in its circle: the walk ends short ({p.Status}, gap {p.Gap:F1})");
        Check.True(p.Corners.Count > 0 && Math.Sqrt(Math.Pow(p.Corners[^1].X - portal.X, 2) + Math.Pow(p.Corners[^1].Y - portal.Y, 2)) >= portal.R - 0.01,
                   $"at its edge, not in it: {Pts(p.Corners)}");

        Check.Eq(map.Doors(house).Count, 1, "a portal across the room: the door is still offered");
        map.Portals = new[] { new BuildingMap.PortalSpot(0x7A9B4002, 101, 104.5, 0) };
        Check.Eq(map.Doors(house).Count, 0, "a portal at the door: the door isn't offered");
        map.Portals = new[] { new BuildingMap.PortalSpot(0x7A9B4003, 97, 104.5, 0) };
        Check.Eq(map.Doors(house).Count, 0, "one outdoors by its step out: nor then (RynthNav's way to the step could end in its circle)");
        map.Portals = new[] { new BuildingMap.PortalSpot(0x7A9B4004, 90, 104.5, 0) };
        Check.Eq(map.Doors(house).Count, 1, "one farther off outdoors: it is (RynthNav's way keeps out of that one)");

        // A corridor 2 m wide with a portal in it: no way round.
        var corridor = new List<Vec> { V(0, 0), V(20, 0) };
        var status = BuildingMap.KeepOutOfPortals(corridor, new[] { new BuildingMap.PortalSpot(1, 10, 0, 0) }, v => Math.Abs(v.Y) <= 1, null, out _);
        Check.Eq(status, OutdoorHunt.PathStatus.NoPath, "a portal across a narrow corridor: no way past it");
        // Standing in the ring round a portal (combat took it there): the walk past it steps away round it first.
        var ring = new[] { new BuildingMap.PortalSpot(1, 10, 0, 0) };
        var past = new List<Vec> { V(12, 0), V(0, 0) };
        Check.Eq(BuildingMap.KeepOutOfPortals(past, ring, null, null, out _), OutdoorHunt.PathStatus.Ok, "from inside its ring, a walk past it: " + Pts(past));
        Check.True(past.Count > 2 && (past[1].X - 10) * (past[1].X - 12) > 0 && Math.Abs(past[1].Y) > 3, "its first step leads away from it: " + Pts(past));
        for (int i = 1; i + 1 < past.Count; i++)
            Check.True(SegDist(past[i], past[i + 1], 10, 0) >= 3.5 - 0.01, $"then keeps out of the circle (leg {i})");
        // A corner of the way in the circle (a doorway beside it): left out, the legs bent round instead.
        var beside = new List<Vec> { V(0, 0), V(10, 1), V(20, 0) };
        Check.Eq(BuildingMap.KeepOutOfPortals(beside, ring, null, null, out _), OutdoorHunt.PathStatus.Ok, "a corner in the circle: still a way");
        Check.True(!beside.Any(v => BuildingMap.InPortal(ring[0], v)) && Enumerable.Range(0, beside.Count - 1).All(i => SegDist(beside[i], beside[i + 1], 10, 0) >= 3.5 - 0.01),
                   "no corner or leg in it: " + Pts(beside));
        var other = new List<Vec> { V(0, 0), V(20, 0) };
        Check.Eq(BuildingMap.KeepOutOfPortals(other, new[] { new BuildingMap.PortalSpot(1, 10, 0, 8) }, v => Math.Abs(v.Y) <= 1, null, out _),
                 OutdoorHunt.PathStatus.Ok, "one on the floor above (8 m up) doesn't count");
    }

    /// <summary>On one of the cells' floors, or within 0.25 m of it (an opening's centre is on the seam between two floors).</summary>
    private static bool NearFloor(DungeonGeometry geo, IReadOnlyCollection<uint> cells, Vec v)
    {
        for (double dx = -0.25; dx <= 0.25; dx += 0.25)
            for (double dy = -0.25; dy <= 0.25; dy += 0.25)
                if (geo.OnFloorOfAny(cells, v.X + dx, v.Y + dy, v.Z + 0.1)) return true;
        return false;
    }

    private static double SegDist(Vec a, Vec b, double px, double py)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / l2, 0, 1);
        return Math.Sqrt(Math.Pow(a.X + dx * t - px, 2) + Math.Pow(a.Y + dy * t - py, 2));
    }

    // ── The real dats ────────────────────────────────────────────────────────────────────────────

    private static GeometryLoader? _geo;
    private static bool _geoTried;
    private static GeometryLoader? Dats()
    {
        if (_geoTried) return _geo;
        _geoTried = true;
        if (!Directory.Exists(AcDir)) return null;
        var g = new GeometryLoader();
        if (!g.Initialize(AcDir) || g.CellDat == null || g.DungeonLOS == null) return null;
        return _geo = g;
    }

    /// <summary>A spot on the cell's floor: the middle of its biggest upward floor polygon (in doubles).</summary>
    private static bool FloorSpot(GeometryLoader g, BuildingMap.Block blk, uint cell, out Vec spot)
    {
        spot = default;
        double bestArea = 0;
        foreach (var p in g.DungeonLOS.GetDungeonMapFloorPolygons(blk.Landblock))
        {
            if (p.CellId != cell || p.Vertices == null || p.Vertices.Length < 3 || !DungeonGeometry.IsUpwardFloor(p.Vertices)) continue;
            double ox = p.Vertices[0].X, oy = p.Vertices[0].Y, area = 0, sx = 0, sy = 0, sz = 0;
            for (int i = 0; i < p.Vertices.Length; i++)
            {
                var a = p.Vertices[i]; var b = p.Vertices[(i + 1) % p.Vertices.Length];
                area += ((double)a.X - ox) * ((double)b.Y - oy) - ((double)b.X - ox) * ((double)a.Y - oy);
                sx += a.X; sy += a.Y; sz += a.Z;
            }
            area = Math.Abs(area) / 2;
            if (area <= bestArea) continue;
            int n = p.Vertices.Length;
            var c = new Vec(sx / n, sy / n, sz / n);
            if (!blk.Geo!.OnCellFloor(cell, c.X, c.Y, c.Z + 0.1)) continue;
            bestArea = area;
            spot = c;
        }
        return bestArea > 0.5;
    }

    private static void CheckDoorsAndWalks(GeometryLoader g, BuildingMap.Block blk, string label, out int doors, out int rooms, out int walks)
    {
        doors = rooms = walks = 0;
        var map = new BuildingMap(null, null);
        map.Add(blk);
        var geo = blk.Geo!;
        foreach (var b in blk.Buildings)
        {
            foreach (var d in b.Doors)
            {
                doors++;
                Check.False(geo.TryFloorHeight(d.Outside.X, d.Outside.Y, d.Outside.Z - 6, 6.4, out _),
                            $"{label} {map.Name(b.Id)} door {d.Cell:X8} at {d.At.X:F1},{d.At.Y:F1}: its step out is over no floor");
                Check.True(geo.OnFloorOfAny(b.Cells, d.Inner.X, d.Inner.Y, d.Inner.Z), $"{label} {map.Name(b.Id)} door {d.Cell:X8}: its step in is on the floor");
                Check.True(d.Inner.FlatDistanceTo(d.Outside) > 1.9 && d.Inner.FlatDistanceTo(d.At) + d.At.FlatDistanceTo(d.Outside) < d.Inner.FlatDistanceTo(d.Outside) + 0.01,
                           $"{label} door {d.Cell:X8}: in, door, out in a line");
            }
            if (b.Doors.Count == 0) continue;
            var d0 = b.Doors[0];
            foreach (uint c in b.Cells)
            {
                if (!FloorSpot(g, blk, c, out var spot)) continue;
                rooms++;
                var p = map.Inside(d0.Cell, d0.Inner, c, spot);
                if (p.Status == OutdoorHunt.PathStatus.Ok) walks++;
                else Check.True(false, $"{label} {map.Name(b.Id)}: no walk from door {d0.Cell:X8} to {c:X8} ({p.Status})");
            }
        }
    }

    private static void RealHoltburg()
    {
        var g = Dats();
        if (g == null) { Check.True(true, "no dats on this PC: skipped"); return; }
        var a9b4 = BuildingMap.Load(0xA9B4, g.CellDat, g.DungeonLOS);
        Check.True(a9b4.Graph.Count == 138 && a9b4.Buildings.Count == 15, $"A9B4 (Holtburg): {a9b4.Graph.Count} cells in {a9b4.Buildings.Count} buildings");
        Check.True(a9b4.Buildings.All(b => b.Doors.Count > 0), "every building in it has a door: " + string.Join(" ", a9b4.Buildings.Select(b => b.Doors.Count)));
        var b0 = a9b4.Buildings[0];
        Check.True(b0.Cells.Count == 17 && b0.Doors.Count == 3, $"A9B4#0: 17 cells, 3 doors ({b0.Cells.Count}, {b0.Doors.Count})");
        Check.True(b0.NotDoors.Any(n => n.Contains("window")), "and a window that isn't one");
        var b4 = a9b4.Buildings[4];
        Check.True(b4.NotDoors.Count(n => n.Contains("step out is over a floor")) == 2, "A9B4#4: its two upper-storey doors onto the roof below aren't doors: " + string.Join(" | ", b4.NotDoors));
        CheckDoorsAndWalks(g, a9b4, "A9B4", out int doors, out int rooms, out int walks);
        Check.True(doors >= 30 && rooms >= 60 && walks == rooms, $"A9B4: {doors} doors, a walk from a door to all {rooms} rooms with a floor ({walks})");
        Console.WriteLine($"          A9B4: {a9b4.Graph.Count} cells, {a9b4.Buildings.Count} buildings, {doors} doors ({a9b4.Buildings.Sum(b => b.NotDoors.Count)} openings out that are not), {walks}/{rooms} rooms walked to from a door");

        var a9b3 = BuildingMap.Load(0xA9B3, g.CellDat, g.DungeonLOS);
        CheckDoorsAndWalks(g, a9b3, "A9B3", out doors, out rooms, out walks);
        Check.True(a9b3.Buildings.Count == 2 && doors == 4 && walks == rooms && rooms > 0, $"A9B3: {a9b3.Buildings.Count} buildings, {doors} doors, {walks}/{rooms} rooms walked to");
        Console.WriteLine($"          A9B3: {a9b3.Buildings.Count} buildings, {doors} doors, {walks}/{rooms} rooms walked to");
        var empty = BuildingMap.Load(0xA8B3, g.CellDat, g.DungeonLOS);
        Check.Eq(empty.Buildings.Count, 0, "A8B3: no buildings");
    }

    private static void RealDf61()
    {
        var g = Dats();
        if (g == null) { Check.True(true, "no dats on this PC: skipped"); return; }
        var blk = BuildingMap.Load(0xDF61, g.CellDat, g.DungeonLOS);
        Check.True(blk.SharedOpenings >= 5 && blk.Buildings.Count == 6, $"DF61: {blk.SharedOpenings} shared openings joined, {blk.Graph.Count} cells in {blk.Buildings.Count} buildings (11 groups before)");
        int wallTops = blk.Buildings.Sum(b => b.NotDoors.Count(n => n.Contains("step out is over a floor")));
        Check.True(wallTops >= 5, $"{wallTops} openings onto the wall tops aren't doors");
        CheckDoorsAndWalks(g, blk, "DF61", out int doors, out int rooms, out int walks);
        Check.True(doors >= 8 && walks == rooms && rooms >= 15, $"DF61: {doors} doors, {walks}/{rooms} rooms walked to");
        Console.WriteLine($"          DF61: {blk.Graph.Count} cells, {blk.SharedOpenings} shared openings joined, {blk.Buildings.Count} buildings, {doors} doors, {wallTops} wall-top openings left out, {walks}/{rooms} rooms walked to");
    }

    private static void RealHuntHoltburg()
    {
        var g = Dats();
        if (g == null) { Check.True(true, "no dats on this PC: skipped"); return; }
        var map = new BuildingMap(null, null);
        var blk = BuildingMap.Load(0xA9B4, g.CellDat, g.DungeonLOS);
        map.Add(blk);
        var b0 = blk.Buildings[0];
        // The room farthest from the first door, and a spot 30 yd out from that door.
        var d0 = b0.Doors[0];
        uint room = 0; Vec spot = default; double far = -1;
        foreach (uint c in b0.Cells)
            if (FloorSpot(g, blk, c, out var s) && s.FlatDistanceTo(d0.At) > far) { far = s.FlatDistanceTo(d0.At); room = c; spot = s; }
        Check.True(room != 0, $"a room of A9B4#0 to hunt in: {room:X8} at {spot.X:F1},{spot.Y:F1},{spot.Z:F1}");
        var outward = new Vec(d0.Outside.X - d0.At.X, d0.Outside.Y - d0.At.Y, 0);
        double ol = Math.Sqrt(outward.X * outward.X + outward.Y * outward.Y);
        var me = new Vec(d0.At.X + outward.X / ol * 30, d0.At.Y + outward.Y / ol * 30, d0.At.Z);
        var nav = new Nav();
        var h = new OutdoorHunt();
        h.Start(T0, me);
        var d = h.Tick(T0, me, 0xA9B4002Au, true, Sorted(C(1, spot, me, room)), nav.Path, map);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.Corners != null && d.Corners[^1] == spot, $"walks in to it: {d.Act}");
        Check.True(h.Note.Contains("inside building A9B4#0") && h.Note.Contains("through the door at"), "logged: " + h.Note);
        Console.WriteLine("          " + h.Note);
        int door = d.Corners!.ToList().FindIndex(v => b0.Doors.Any(x => x.At == v));
        Check.True(door > 0, "through one of its doors: " + Pts(d.Corners));
        foreach (var v in d.Corners.Skip(door + 1))
            Check.True(NearFloor(blk.Geo!, b0.Cells, v), $"every corner past the door is on the building's floor ({v.X:F1},{v.Y:F1},{v.Z:F1})");

        // From that room back out to a monster 40 yd out of the door: through a door, then on the map.
        var outside = new Vec(d0.At.X + outward.X / ol * 40, d0.At.Y + outward.Y / ol * 40, d0.At.Z);
        var h2 = new OutdoorHunt();
        h2.Start(T0, spot);
        d = h2.Tick(T0, spot, room, true, Sorted(C(2, outside, spot)), new Nav().Path, map);
        Check.True(d.Act == OutdoorHunt.Act.Walk && d.Corners![^1] == outside && h2.Note.Contains("out of building A9B4#0 through the door at"), "and back out: " + h2.Note);
        Console.WriteLine("          " + h2.Note);
        var exit = b0.Doors.FirstOrDefault(x => d.Corners!.Contains(x.At));
        Check.True(exit.Cell != 0 && Idx(d.Corners!, exit.Outside) == Idx(d.Corners!, exit.At) + 1, "door, then its step out: " + Pts(d.Corners));

        // A portal in the middle of the walk's longest leg: kept out of, or no walk at all; never through it.
        var way = new List<Vec> { d0.Inner };
        var direct = map.Inside(d0.Cell, d0.Inner, room, spot);
        way.AddRange(direct.Corners);
        int longest = 0;
        for (int i = 1; i + 1 < way.Count; i++)
            if (way[i].FlatDistanceTo(way[i + 1]) > way[longest].FlatDistanceTo(way[longest + 1])) longest = i;
        var la = way[longest]; var lb = way[longest + 1];
        Check.True(la.FlatDistanceTo(lb) >= 4, $"the walk in has a leg of 4 m or more ({la.FlatDistanceTo(lb):F1})");
        var mid = new Vec((la.X + lb.X) / 2, (la.Y + lb.Y) / 2, (la.Z + lb.Z) / 2);
        var portal = new BuildingMap.PortalSpot(1, mid.X, mid.Y, mid.Z, 1.5);
        map.Portals = new[] { portal };
        var round = map.Inside(d0.Cell, d0.Inner, room, spot);
        if (round.Status != OutdoorHunt.PathStatus.NoPath)
        {
            var w2 = new List<Vec> { d0.Inner };
            w2.AddRange(round.Corners);
            double nearest = double.MaxValue;
            for (int i = 0; i + 1 < w2.Count; i++) nearest = Math.Min(nearest, SegDist(w2[i], w2[i + 1], portal.X, portal.Y));
            Check.True(nearest >= portal.R - 0.01, $"a 1.5 m portal at {mid.X:F1},{mid.Y:F1}: the walk keeps {nearest:F2} m from it ({round.Status}): {Pts(w2)}");
            Console.WriteLine($"          a 1.5 m portal at {mid.X:F1},{mid.Y:F1}: {round.Status}, the walk keeps {nearest:F2} m from it, {w2.Count - 1} corners (was {way.Count - 1})");
        }
        else Check.True(true, "a portal where the walk can't get round it: no walk");
    }
}

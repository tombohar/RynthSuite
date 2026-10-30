using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Dead ends the patrol leaves out (DungeonPathfinder.TrimDeadEndPockets, Settings "Skip Dead Ends
// Up To", 2026-10-13). They were spurs: from a tip inward through corridor cells to the first
// junction, at most 3 cells. A small room's cells join each other (junctions), so the walk
// stopped at the room and a corridor ending in a 3-5 cell room was always walked into (Tom:
// "running into small rooms again"; raising the limit changed nothing in 0x6146). Now whole
// dead-end areas are measured: everything hanging off the rest by one cell (or room) counts.
//
// The fixture: a 12-cell loop (1..12, 10 m a cell) with
//   off 1:  a corridor 0x20-0x21 into a 4-cell room 0x22-0x25 (its cells all joined): 6 cells
//   off 4:  a closet 0x30
//   off 7:  a branch: corridor 0x40-0x43, then a 16-cell hall 0x50-0x5F (a 4x4 grid), and off
//           the hall a corridor 0x60 into a 3-cell room 0x61-0x63 (4 cells); off corridor cell
//           0x41 a closet 0x48
internal static class PatrolPocketTests
{
    public static void Register(Runner r)
    {
        r.Add("patrol dead ends: a corridor into a small room is one dead end, left out whole (spurs kept it)", CorridorAndRoomDropped);
        r.Add("patrol dead ends: a branch to a big hall is walked; the small dead ends off it are not", BigBranchKept);
        r.Add("patrol dead ends: in a long dead end the patrol turns round where only N cells are left", LongDeadEndTurnsRound);
        r.Add("patrol dead ends: the start's own dead end is kept (it walks out of it)", StartInsidePocket);
        r.Add("patrol dead ends: a linear dungeon is walked end to end, room and all", LinearUntouched);
        r.Add("patrol dead ends: by room, a junction's own arms are part of it; a big room is never left out", ByRoom);
        r.Add("patrol dead ends: the main route log says dead-end areas", MainRouteNote);
    }

    private static readonly uint[] Loop = Enumerable.Range(1, 12).Select(i => (uint)i).ToArray();
    private static readonly uint[] SmallRoomPocket = { 0x20, 0x21, 0x22, 0x23, 0x24, 0x25 };
    private static readonly uint[] Hall = Enumerable.Range(0x50, 16).Select(i => (uint)i).ToArray();
    private static readonly uint[] HallCorridor = { 0x40, 0x41, 0x42, 0x43 };
    private static readonly uint[] OffHall = { 0x60, 0x61, 0x62, 0x63 };

    private sealed class G
    {
        public readonly Dictionary<uint, DungeonNavNode> Graph = new();
        public void At(uint c, double eastM, double northM)
        {
            if (!Graph.TryGetValue(c, out var n)) Graph[c] = n = new DungeonNavNode { CellId = c };
            n.EW = eastM / 240.0; n.NS = northM / 240.0;
        }
        public void Link(uint a, uint b)
        {
            foreach (uint c in new[] { a, b })
                if (!Graph.ContainsKey(c)) At(c, (c & 0xF) * 10, (c >> 4) * 10);
            Graph[a].Neighbors.Add(b); Graph[b].Neighbors.Add(a);
        }
        public void Chain(params uint[] cells) { for (int i = 0; i + 1 < cells.Length; i++) Link(cells[i], cells[i + 1]); }
        public void Clique(params uint[] cells) { for (int i = 0; i < cells.Length; i++) for (int j = i + 1; j < cells.Length; j++) Link(cells[i], cells[j]); }
    }

    private static G Fixture()
    {
        var g = new G();
        for (int i = 0; i < Loop.Length; i++)
        {
            double a = 2 * Math.PI * i / Loop.Length;
            g.At(Loop[i], 200 + 20 * Math.Cos(a), 200 + 20 * Math.Sin(a));
        }
        for (int i = 0; i < Loop.Length; i++) g.Link(Loop[i], Loop[(i + 1) % Loop.Length]);
        g.Chain(1, 0x20, 0x21, 0x22);
        g.Clique(0x22, 0x23, 0x24, 0x25);
        g.Link(4, 0x30);
        g.Chain(7, 0x40, 0x41, 0x42, 0x43, 0x50);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                uint c = (uint)(0x50 + y * 4 + x);
                if (x < 3) g.Link(c, c + 1);
                if (y < 3) g.Link(c, c + 4);
            }
        g.Chain(0x5F, 0x60, 0x61);
        g.Clique(0x61, 0x62, 0x63);
        g.Link(0x41, 0x48);
        return g;
    }

    private static HashSet<uint> All(G g) => new(g.Graph.Keys);
    private static string Ids(IEnumerable<uint> c) => string.Join(",", c.OrderBy(x => x).Select(x => x.ToString("X")));

    private static void CorridorAndRoomDropped()
    {
        var g = Fixture();
        var kept = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 6, 10);
        Check.False(kept.Overlaps(SmallRoomPocket), $"corridor + room (6 cells) left out ({Ids(kept.Intersect(SmallRoomPocket))} kept)");
        Check.False(kept.Contains(0x30), "the closet left out");
        Check.True(kept.IsSupersetOf(Loop), "the loop walked");
        Check.True(DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 6, 6).IsSupersetOf(Loop)
                   && !DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 6, 6).Overlaps(SmallRoomPocket), "at 6 too (exactly its size)");
        Check.Eq(DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 6, 0).Count, g.Graph.Count, "0: nothing left out");
    }

    private static void BigBranchKept()
    {
        var g = Fixture();
        var kept = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 6, 10);
        Check.True(kept.IsSupersetOf(HallCorridor) && kept.IsSupersetOf(Hall), $"the corridor and the 16-cell hall walked ({Ids(kept)})");
        Check.False(kept.Overlaps(OffHall), "the corridor + small room off the hall left out");
        Check.False(kept.Contains(0x48), "the closet off the branch's corridor left out");
        // Cell by cell the hall past its first cell (0x50) hangs off that cell alone: 19 cells.
        var at18 = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 6, 18);
        Check.True(at18.IsSupersetOf(Hall) && !at18.Overlaps(OffHall), $"at 18 the hall is still walked ({Ids(at18)})");
        var at20 = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 6, 20);
        Check.True(at20.IsSupersetOf(HallCorridor) && !at20.Overlaps(Hall),
                   "at 20 the hall and what's behind it (20 cells) is a small dead end: the patrol turns round before it");
        var at30 = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 6, 30);
        Check.False(at30.Overlaps(Hall) || at30.Overlaps(HallCorridor), "at 30 the whole branch is a small dead end");
    }

    private static void LongDeadEndTurnsRound()
    {
        // A 14-cell corridor off a loop, nothing at its end: at 10 the first 4 cells are walked.
        var g = new G();
        for (int i = 0; i < Loop.Length; i++) g.Link(Loop[i], Loop[(i + 1) % Loop.Length]);
        var corridor = Enumerable.Range(0x70, 14).Select(i => (uint)i).ToArray();
        g.Chain(new[] { 3u }.Concat(corridor).ToArray());
        var kept = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 9, 10);
        Check.True(kept.IsSupersetOf(corridor.Take(4)) && !kept.Overlaps(corridor.Skip(4)),
                   $"walked in until 10 cells are left ({Ids(kept.Intersect(corridor))})");
    }

    private static void StartInsidePocket()
    {
        var g = Fixture();
        var inRoom = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 0x23, 10);
        Check.True(inRoom.IsSupersetOf(SmallRoomPocket), "starting in the small room: the room and its corridor kept");
        Check.False(inRoom.Contains(0x30), "the closet elsewhere still left out");
        Check.True(inRoom.IsSupersetOf(Loop) && inRoom.IsSupersetOf(Hall), "and the rest walked");
        var inCloset = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 0x30, 10);
        Check.True(inCloset.Contains(0x30), "starting in the closet: kept");
        Check.False(inCloset.Overlaps(SmallRoomPocket), "the small room still left out");

        // Through GetMainRouteNodes (the patrol's way in).
        var route = DungeonPathfinder.GetMainRouteNodes(g.Graph, 0x30, null, DungeonPatrolOptions.Default);
        Check.True(route.Contains(0x30) && !route.Overlaps(SmallRoomPocket), $"main route from the closet ({Ids(route)})");
    }

    private static void LinearUntouched()
    {
        // No loops: a 12-cell corridor with a 3-cell side passage and a 2-cell closet off it.
        // The dungeon is linear and every cell is walked whatever the limit.
        var g = new G();
        var corridor = Enumerable.Range(0x70, 12).Select(i => (uint)i).ToArray();
        g.Chain(corridor);
        g.Chain(0x74, 0x80, 0x81, 0x82);
        g.Chain(0x79, 0x90, 0x91);
        foreach (PatrolCoverage cov in Enum.GetValues<PatrolCoverage>())
            foreach (int n in new[] { 3, 10, 30 })
                Check.Eq(DungeonPathfinder.GetMainRouteNodes(g.Graph, 0x76, null, new DungeonPatrolOptions { Coverage = cov, MaxDeadEndCells = n }).Count,
                         17, $"{cov}, dead ends {n}: all 17 cells");
    }

    private static void ByRoom()
    {
        // A star junction: centre 0x91 with arms 0x90, 0x92, 0x93, 0x94 (one room: the pieces of a
        // junction). 0x90 and 0x92 go on round a loop; 0x93 and 0x94 lead nowhere.
        var g = new G();
        foreach (uint arm in new uint[] { 0x90, 0x92, 0x93, 0x94 }) g.Link(0x91, arm);
        var ring = Enumerable.Range(0xA0, 10).Select(i => (uint)i).ToArray();
        g.Chain(new[] { 0x92u }.Concat(ring).Concat(new[] { 0x90u }).ToArray());
        // Off the ring: a corridor 0xB0 into a big room 0xB1-0xB4 (cells spread 30 m: a hall).
        g.Link(0xA5, 0xB0);
        g.Clique(0xB1, 0xB2, 0xB3, 0xB4);
        g.Link(0xB0, 0xB1);
        g.At(0xB1, 500, 500); g.At(0xB2, 520, 500); g.At(0xB3, 500, 520); g.At(0xB4, 520, 520);

        var rooms = new DungeonRooms();
        void Room(params uint[] cells)
        {
            int r = rooms.Cells.Count;
            rooms.Cells.Add(cells.ToList());
            foreach (uint c in cells) rooms.RoomOf[c] = r;
        }
        Room(0x90, 0x91, 0x92, 0x93, 0x94);
        Room(0xB1, 0xB2, 0xB3, 0xB4);
        foreach (uint c in g.Graph.Keys) if (!rooms.RoomOf.ContainsKey(c)) Room(c);

        var byCell = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 0xA2, 10);
        Check.False(byCell.Contains(0x93) || byCell.Contains(0x94), "cell by cell, the junction's dead arms are closets");
        var byRoom = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 0xA2, 10, rooms);
        Check.True(byRoom.IsSupersetOf(new uint[] { 0x90, 0x91, 0x92, 0x93, 0x94 }), "by room, the junction is walked whole");
        Check.True(byRoom.IsSupersetOf(new uint[] { 0xB0, 0xB1, 0xB2, 0xB3, 0xB4 }), "a big room (28 m) in a 5-cell dead end is walked");
        var lookIn40 = DungeonPathfinder.TrimDeadEndPockets(g.Graph, All(g), 0xA2, 10, rooms, bigRoomMeters: 40);
        Check.False(lookIn40.Overlaps(new uint[] { 0xB0, 0xB1, 0xB2, 0xB3, 0xB4 }), "with Walk Into Rooms From 40 it is a small one: left out");
        Check.False(byCell.Overlaps(new uint[] { 0xB0, 0xB1 }), "cell by cell (no rooms, no sizes) it is left out too");
    }

    private static void MainRouteNote()
    {
        var g = Fixture();
        DungeonPathfinder.PointLog = new Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo>(ReferenceEqualityComparer.Instance);
        try { DungeonPathfinder.GetMainRouteNodes(g.Graph, 6, null, DungeonPatrolOptions.Default); }
        finally { DungeonPathfinder.PointLog = null; }
        string note = DungeonPathfinder.MainRouteNote ?? "";
        Check.True(note.Contains("dead-end areas of <= 10 cells"), note);
    }
}

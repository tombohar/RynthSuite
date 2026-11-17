// DungeonPathfinder, explore half: one walk through every room of the dungeon, then stop (2026-11-13).
//
// Why: Tom wanted a script that explores a dungeon: every room once, what turned up listed at the
// end. The patrol is a loop for hunting: it goes round for ever, and a reroute (a hazard sighted,
// a portal coming into view: in a dungeon nobody has walked yet that is every few rooms) lays the
// whole lap again from where the character stands, so a walk that should end would keep crossing
// rooms it has already been through.
//
// The explore (/ra dunnav-explore, RynthAiPlugin.DunExplore.cs) is a Once route over the same rooms
// as the patrol with coverage Everything (DungeonRooms: cells joined by wide openings; each cell its
// own room until the dungeon map is loaded), laid by the patrol's own pieces: doorways taken straight
// on, rooms crossed directly, big rooms looked into (EmitSteps, lookIn), lava/acid and drops left out,
// portals kept out of (LayAroundPortals). What is new is the order:
//   - from the room the character is in, the next room is a room next door not yet entered; of
//     several, the one with the fewest new rooms beyond it (a closet or a dead end first, so it is
//     never walked back to from far away later);
//   - with none next door, the shortest way (in doorways) to the nearest room not yet entered;
//   - it ends in the middle of the last new room (no walk back to the start).
// The rooms already entered (visitedCells: the cells the character has stood in on this explore)
// count as done, so a reroute lays only the walk through the rest.
// Shut doors (a door the character can't use: a lever opens it) are left out by the caller (their
// openings closed in the graph); when one opens, the rooms behind it (preferCells) are walked first,
// since it shuts again on its own (8 s to 5 min in ACE's data).

using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

/// <summary>The rooms an explore walk was laid over, numbered as its <see cref="Rooms"/>.</summary>
internal sealed class ExplorePlan
{
    /// <summary>Every cell of the graph in a room (the map's rooms, or one cell each without the map).</summary>
    public DungeonRooms Rooms = new();
    /// <summary>The rooms the walk is meant to enter: every room the character can walk to from where it
    /// stood (no lava/acid, no drop, no opening a portal closes).</summary>
    public readonly HashSet<int> PlanRooms = new();
    /// <summary>Of PlanRooms, those entered before this walk was laid (it doesn't go back to them).</summary>
    public readonly HashSet<int> DoneRooms = new();
    /// <summary>The rooms the walk goes into, in order (the start room not included).</summary>
    public readonly List<int> Order = new();
    /// <summary>Laid on the dungeon map's rooms (false: the cell walk, each cell a room).</summary>
    public bool ByRoom;
    /// <summary>The room middles the walk goes to (look-ins, the last room's): waited at (DungeonPathfinder.ExploreScanMs).</summary>
    public readonly List<NavPoint> ScanMiddles = new();

    public int RoomOf(uint cell) => Rooms.RoomOf.TryGetValue(cell, out int r) ? r : -1;
}

internal static partial class DungeonPathfinder
{
    /// <summary>
    /// A Once route from <paramref name="startCell"/> through every room the character can walk to and
    /// has not entered yet (<paramref name="visitedCells"/>: cells it has stood in), ending in the
    /// middle of the last one. The patrol settings in <paramref name="options"/> apply but for the
    /// coverage, always Everything. Empty when there is nothing left to walk to.
    /// </summary>
    public static NavRouteParser BuildExploreRoute(
        Dictionary<uint, DungeonNavNode> graph,
        uint startCell,
        IReadOnlySet<uint>? hazardCells,
        DungeonGeometry? geo,
        DungeonPatrolOptions? options,
        IReadOnlyList<PatrolPortal>? portals,
        (double X, double Y, double Z)? startAt,
        IReadOnlySet<uint>? visitedCells,
        out ExplorePlan plan,
        IReadOnlySet<uint>? preferCells = null)
    {
        LastPortalAvoidance = null;
        var o = ((options ?? DungeonPatrolOptions.Default) with { Coverage = PatrolCoverage.Everything }).Clamped();
        NavRouteParser route;
        if (portals is not { Count: > 0 })
            route = LayExplore(graph, startCell, hazardCells, geo, o, visitedCells, out plan, preferCells);
        else
        {
            ExplorePlan? last = null;
            route = LayAroundPortals(graph, startCell, hazardCells, geo, portals, startAt, o.WallClearance, loop: false,
                g =>
                {
                    var r = LayExplore(g, startCell, hazardCells, geo, o, visitedCells, out var p, preferCells);
                    last = p;
                    return r;
                });
            plan = last ?? new ExplorePlan();
        }
        // The waits go in last, on the finished walk (the portal step moves points out of a portal's circle; a
        // middle it moved gets no wait).
        int scans = ExploreScanMs > 0 && plan.ScanMiddles.Count > 0 ? AddScanPauses(route.Points, plan.ScanMiddles, ExploreScanMs) : 0;
        if (scans > 0) MainRouteNote = (MainRouteNote == null ? "" : MainRouteNote + "; ") + $"scans: {scans} room middle(s) waited at {ExploreScanMs / 1000.0:0.#} s";
        return route;
    }

    // ── Steps nav can't walk (2026-11-16) ──────────────────────────────────────────────────────────
    // DreamWeave Thread 0x0278: the explore crossed a two-level room straight, from its doorway at the foot
    // (z -42) to the opening on the level above (z -36), 1 m off on the map. Nav takes a point as reached by
    // the flat distance, so it skipped the upper one and ran from the lower floor at the next point, 50 m off
    // on the level above: into the wall under it, until it was stopped. A step rising (or dropping) more than
    // StackedRiseMeters within StackedFlatMeters is no stair (a stair's steps here are 3 m up over 2 m): the
    // doorway at its end is closed for this walk (all its cell pairs) and the walk laid again.

    internal const double StackedRiseMeters = 4.5, StackedFlatMeters = 2.5;
    /// <summary>Off: the walk as laid (tests compare the two).</summary>
    internal static bool CloseStackedSteps = true;
    private const int StackedRetries = 4;

    /// <summary>The walk's steps nav can't walk as laid (see above), and the doorways at their ends (all
    /// their cell pairs) to close.</summary>
    internal static HashSet<ulong> StackedStepOpenings(List<NavPoint> pts, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, out int steps)
    {
        var keys = new HashSet<ulong>();
        steps = 0;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            NavPoint a = pts[i], b = pts[i + 1];
            if (!NavRouteParser.IsPlainWaypoint(a.Type) || !NavRouteParser.IsPlainWaypoint(b.Type)) continue;
            double ax = DungeonGeometry.NavToWorld(a.EW), ay = DungeonGeometry.NavToWorld(a.NS);
            double bx = DungeonGeometry.NavToWorld(b.EW), by = DungeonGeometry.NavToWorld(b.NS);
            double rise = Math.Abs(b.Z - a.Z) * 240.0, flat = Math.Sqrt(Sq(bx - ax) + Sq(by - ay));
            if (rise <= StackedRiseMeters || flat >= StackedFlatMeters) continue;
            steps++;
            var at = DoorwayAt(graph, geo, ax, ay, a.Z * 240.0, 1.5, out _);
            if (at.Count == 0) at = DoorwayAt(graph, geo, bx, by, b.Z * 240.0, 1.5, out _);
            keys.UnionWith(at);
        }
        return keys;
    }

    private static NavRouteParser LayExplore(
        Dictionary<uint, DungeonNavNode> graph, uint startCell, IReadOnlySet<uint>? hazardCells,
        DungeonGeometry? geo, DungeonPatrolOptions o, IReadOnlySet<uint>? visitedCells, out ExplorePlan plan,
        IReadOnlySet<uint>? preferCells = null)
    {
        string? note = MainRouteNote;
        var route = LayExploreOnce(graph, startCell, hazardCells, geo, o, visitedCells, out plan, preferCells);
        if (geo == null || !plan.ByRoom || !CloseStackedSteps) return route;
        HashSet<ulong>? cut = null;
        var cur = graph;
        int stacked = 0;
        for (int round = 0; round < StackedRetries; round++)
        {
            var keys = StackedStepOpenings(route.Points, cur, geo, out int steps);
            if (steps == 0) break;
            stacked += steps;
            cut ??= new HashSet<ulong>();
            int before = cut.Count;
            cut.UnionWith(keys);
            if (cut.Count == before) break;   // no doorway to close: the walk stays as it is
            cur = WithoutOpenings(graph, cut, startCell);
            string? laid = MainRouteNote;
            MainRouteNote = note;
            var again = LayExploreOnce(cur, startCell, hazardCells, geo, o, visitedCells, out var p, preferCells);
            if (again.Points.Count == 0) { MainRouteNote = laid; break; }   // nothing left to walk: keep the first
            route = again;
            plan = p;
        }
        if (stacked > 0)
            MainRouteNote = (MainRouteNote == null ? "" : MainRouteNote + "; ")
                          + $"stacked steps: {stacked} found, {cut?.Count ?? 0} opening pair(s) closed for the walk";
        return route;
    }

    private static NavRouteParser LayExploreOnce(
        Dictionary<uint, DungeonNavNode> graph, uint startCell, IReadOnlySet<uint>? hazardCells,
        DungeonGeometry? geo, DungeonPatrolOptions o, IReadOnlySet<uint>? visitedCells, out ExplorePlan plan,
        IReadOnlySet<uint>? preferCells = null)
    {
        var route = new NavRouteParser { RouteType = NavRouteType.Once };
        bool byRoom = geo != null && geo.DoorwayCount > 0;
        var rooms = byRoom ? BuildRooms(graph, geo!, hazardCells) : CellRooms(graph);
        plan = new ExplorePlan { Rooms = rooms, ByRoom = byRoom };

        // Coverage Everything: every cell walkable to from the start, dead ends included.
        foreach (uint c in GetMainRouteNodes(graph, startCell, hazardCells, o, byRoom ? rooms : null))
            if (rooms.RoomOf.TryGetValue(c, out int r)) plan.PlanRooms.Add(r);
        if (!rooms.RoomOf.TryGetValue(startCell, out int startRoom) || !plan.PlanRooms.Contains(startRoom))
            return route;

        var entered = new HashSet<int> { startRoom };
        if (visitedCells != null)
            foreach (uint c in visitedCells)
                if (rooms.RoomOf.TryGetValue(c, out int vr) && plan.PlanRooms.Contains(vr)) entered.Add(vr);
        plan.DoneRooms.UnionWith(entered);

        var adj = RoomAdjacency(graph, rooms, plan.PlanRooms, hazardCells, out _);
        HashSet<int>? prefer = null;
        if (preferCells is { Count: > 0 })
        {
            prefer = new HashSet<int>();
            foreach (uint c in preferCells)
                if (rooms.RoomOf.TryGetValue(c, out int pr) && plan.PlanRooms.Contains(pr) && !entered.Contains(pr)) prefer.Add(pr);
        }
        var walk = ExploreWalk(adj, startRoom, entered, plan.Order, prefer);
        if (walk.Count == 0) return route;

        var steps = new List<Step>(walk.Count);
        foreach (var e in walk) steps.Add(new Step(e.From, e.To, e.Key));
        if (byRoom)
        {
            // Into the middle of the last room, so the walk ends inside it, not just past its doorway.
            int lastRoom = walk[walk.Count - 1].Room;
            var mid = RoomMiddle(graph, geo!, rooms.Cells[lastRoom], o.WallClearance, out uint midCell);
            if (mid != null && midCell == 0) midCell = walk[walk.Count - 1].To;
            var middles = plan.ScanMiddles;
            ScanMiddles = middles;
            List<NavPoint> pts;
            try { pts = EmitSteps(graph, geo!, rooms, steps, closed: false, null, 0, mid, midCell, o, out _, lookIn: true); }
            finally { ScanMiddles = null; }
            if (mid != null) { Append(pts, Tag(mid, "explore-end")); middles.Add(mid); }
            // Round a hole in a cell's floor, not over it (DungeonPathfinder.Holes.cs): before the
            // straightening, so its legs keep the clearance the walk round has.
            int holes = WalkRoundHoles(pts, geo!);
            if (holes > 0 || HolesLeft > 0)
                MainRouteNote = (MainRouteNote == null ? "" : MainRouteNote + "; ") + $"holes: {holes} walked round" + (HolesLeft > 0 ? $", {HolesLeft} with no way round" : "");
            // Fewer turns: straight legs over the room boundaries a corridor of small rooms is cut into
            // (DungeonPathfinder.Straighten.cs), every room still walked through.
            int straightened = StraightenWalk(pts, graph, geo!, rooms, o.WallClearance, middles);
            if (straightened > 0) MainRouteNote = (MainRouteNote == null ? "" : MainRouteNote + "; ") + $"straightened: {straightened} turn(s) taken out";
            route.Points.AddRange(pts);
        }
        else
        {
            foreach (var s in steps)
                if (graph.TryGetValue(s.From, out var a) && graph.TryGetValue(s.To, out var b)) AddPortalWaypoints(route, a, b);
            if (steps.Count > 0 && graph.TryGetValue(steps[steps.Count - 1].To, out var end))
                route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = end.NS, EW = end.EW, Z = end.Z / 240.0 });
            SimplifyRoute(route);
        }
        return route;
    }

    /// <summary>
    /// The doorway steps of the explore walk: from <paramref name="startRoom"/>, into a room next door not
    /// yet <paramref name="entered"/> (the one with the fewest new rooms beyond it first), else the
    /// shortest way to the nearest room not yet entered, until every room it can reach is entered.
    /// <paramref name="entered"/> is added to as it goes; <paramref name="order"/> gets each new room.
    /// </summary>
    private static List<RoomEdge> ExploreWalk(Dictionary<int, List<RoomEdge>> adj, int startRoom, HashSet<int> entered, List<int>? order = null,
                                              HashSet<int>? prefer = null)
    {
        var walk = new List<RoomEdge>();
        int cur = startRoom;
        // Behind a door that just opened first: the shortest way to the nearest of those rooms (it shuts again).
        if (prefer is { Count: > 0 })
        {
            var first = RoomBfs(adj, cur, r => prefer.Contains(r) && !entered.Contains(r));
            if (first != null)
            {
                foreach (var e in first)
                {
                    walk.Add(e);
                    if (entered.Add(e.Room)) order?.Add(e.Room);
                }
                cur = first[first.Count - 1].Room;
            }
        }
        // Every pass enters one new room (or stops), so this bounds it.
        int guard = adj.Count + 16;
        while (guard-- > 0)
        {
            RoomEdge? next = null;
            int fewest = int.MaxValue;
            if (adj.TryGetValue(cur, out var doors))
                foreach (var e in doors)
                {
                    if (entered.Contains(e.Room)) continue;
                    int beyond = NewRoomsBeyond(adj, e.Room, cur, entered);
                    if (beyond < fewest) { fewest = beyond; next = e; }
                }

            if (next is RoomEdge step)
            {
                walk.Add(step);
                entered.Add(step.Room);
                order?.Add(step.Room);
                cur = step.Room;
                continue;
            }

            var hop = RoomBfs(adj, cur, r => !entered.Contains(r));
            if (hop == null) break;   // every room it can reach is entered
            walk.AddRange(hop);
            cur = hop[hop.Count - 1].Room;
            entered.Add(cur);
            order?.Add(cur);
        }
        return walk;
    }

    // The rooms next to `room` (but `from`) not yet entered.
    private static int NewRoomsBeyond(Dictionary<int, List<RoomEdge>> adj, int room, int from, HashSet<int> entered)
    {
        if (!adj.TryGetValue(room, out var doors)) return 0;
        var seen = new HashSet<int>();
        foreach (var e in doors)
            if (e.Room != from && !entered.Contains(e.Room)) seen.Add(e.Room);
        return seen.Count;
    }

    /// <summary>The key the graph's openings are closed by (<see cref="WithoutOpenings"/>).</summary>
    internal static ulong OpeningKey(uint a, uint b) => EdgeKey(a, b);

    // ── Enter and scan (2026-11-16) ─────────────────────────────────────────
    // A room's monsters reach the client as it comes into view: DreamWeave Thread 0x00B5's boss was known with no
    // position while the walk touched its room's doorway and turned back, so the fight never started. The explore
    // walks into every dead end (LooksIn, enterDeadEnds) and waits at each room middle it walks to (and the last
    // room's), a nav Pause point after it: the room's monsters arrive and the fight starts before it walks on.

    /// <summary>How long the explore waits at a room middle it walks to (0: no waits).</summary>
    internal static int ExploreScanMs = 2000;

    // The room middles EmitSteps walks to (CrossRoom's look-in), while the explore's walk is laid.
    [ThreadStatic] private static List<NavPoint>? ScanMiddles;

    /// <summary>A Pause after each point of <paramref name="pts"/> still at one of <paramref name="middles"/> (the
    /// clean-up passes keep or drop whole points), once per middle. The number put in.</summary>
    private static int AddScanPauses(List<NavPoint> pts, List<NavPoint> middles, int ms)
    {
        int added = 0;
        var left = new List<NavPoint>(middles);
        for (int i = 0; i < pts.Count && left.Count > 0; i++)
        {
            var p = pts[i];
            if (p.Type != NavPointType.Point) continue;
            int m = left.FindIndex(q => NavPointYd(q, p) < 0.3 && Math.Abs(q.Z - p.Z) * 240.0 < 1.0);
            if (m < 0) continue;
            left.RemoveAt(m);
            pts.Insert(i + 1, new NavPoint { Type = NavPointType.Pause, NS = p.NS, EW = p.EW, Z = p.Z, PauseTimeMs = ms });
            i++;
            added++;
        }
        return added;
    }

    // Another cell pair's portal is in the same opening when its centre is this close (horizontally, and in
    // height: 0x5456's 0174>0177 is 3 m over 0173>0177) and it faces the same way.
    private const double SameOpeningMeters = 1.0, SameOpeningRise = 3.5;

    /// <summary>
    /// Every cell pair whose portal is in the one opening <paramref name="a"/>-<paramref name="b"/> is (its key
    /// first). A cell stacked on another at a room's edge (two cells with one centre, each with its own
    /// portal in the doorway) gives the doorway a second pair. DreamWeave Thread 0x5456 (2026-11-16): 014F and
    /// 0150 both join 0148 at (16133, 16492); the explore stuck there closed 014F>0148 only, laid the walk
    /// again through 0150>0148 (the same gap), and was stuck there again, three times over.
    /// </summary>
    internal static HashSet<ulong> OpeningPairs(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo, uint a, uint b)
    {
        var keys = new HashSet<ulong> { EdgeKey(a, b) };
        if (geo == null || !geo.TryGetDoorway(a, b, out var d)) return keys;
        foreach (var o in geo.DoorwaysNear(d.X, d.Y, d.Z, SameOpeningMeters, SameOpeningRise))
        {
            if (o.CellA != a && o.CellA != b && o.CellB != a && o.CellB != b) continue;   // another opening's
            if (Math.Abs(o.Nx * d.Nx + o.Ny * d.Ny) < 0.9) continue;                      // across it, not through it
            if (!graph.TryGetValue(o.CellA, out var na) || !na.Neighbors.Contains(o.CellB)) continue;
            keys.Add(EdgeKey(o.CellA, o.CellB));
        }
        return keys;
    }

    /// <summary>
    /// The doorway something at (x, y, z) (world metres) stands in: the nearest opening narrower than a room's
    /// own (not IsWide) whose middle is within <paramref name="within"/> of it, 3 m in height, its cells joined
    /// in the graph; as all its cell pairs (<see cref="OpeningPairs"/>). Empty when it stands in none.
    /// </summary>
    internal static HashSet<ulong> DoorwayAt(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, double x, double y, double z,
                                             double within, out DungeonDoorway? doorway)
    {
        doorway = null;
        double best = double.MaxValue;
        foreach (var d in geo.DoorwaysNear(x, y, z, within, 3.0))
        {
            if (d.IsWide || !graph.TryGetValue(d.CellA, out var na) || !graph.ContainsKey(d.CellB) || !na.Neighbors.Contains(d.CellB)) continue;
            double dist = Sq(d.X - x) + Sq(d.Y - y);
            if (dist < best) { best = dist; doorway = d; }
        }
        return doorway == null ? new HashSet<ulong>() : OpeningPairs(graph, geo, doorway.CellA, doorway.CellB);
    }

    /// <summary>Every cell walkable to from <paramref name="startCell"/> (no drops, no lava/acid): what a shut
    /// door's rooms behind it are measured with.</summary>
    internal static HashSet<uint> ReachableCells(Dictionary<uint, DungeonNavNode> graph, uint startCell, IReadOnlySet<uint>? hazardCells)
        => ReachableAndCore(graph, startCell, hazardCells).Reachable;

    /// <summary>
    /// The opening(s) a door at (x, y, z) (world metres, in <paramref name="cell"/>) stands in: the map's
    /// doorway nearest it within 3 m on its level (and any within 0.75 m of that one: a double door);
    /// without the map, the step from its cell to the neighbour whose shared midpoint is nearest it.
    /// Empty when nothing is near enough (the door isn't in an opening the graph knows).
    /// </summary>
    internal static HashSet<ulong> DoorOpenings(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo,
                                               uint cell, double x, double y, double z)
    {
        var edges = new HashSet<ulong>();
        if (geo != null && geo.DoorwayCount > 0)
        {
            var near = geo.DoorwaysNear(x, y, z, 3.0)
                .Where(d => graph.ContainsKey(d.CellA) && graph.ContainsKey(d.CellB))
                .Select(d => (d, dist: Math.Sqrt(Sq(d.X - x) + Sq(d.Y - y))))
                .OrderBy(t => t.dist).ToList();
            if (near.Count > 0)
            {
                double best = near[0].dist;
                foreach (var (d, dist) in near)
                    if (dist <= best + 0.75) edges.Add(EdgeKey(d.CellA, d.CellB));
                return edges;
            }
        }
        if (!graph.TryGetValue(cell, out var n)) return edges;
        double wx = DungeonGeometry.WorldToNav(x), wy = DungeonGeometry.WorldToNav(y);
        uint pick = 0;
        double bestD = double.MaxValue;
        foreach (uint nb in n.Neighbors)
        {
            if (!graph.TryGetValue(nb, out var m)) continue;
            double d = Sq((n.EW + m.EW) / 2 - wx) + Sq((n.NS + m.NS) / 2 - wy);
            if (d < bestD) { bestD = d; pick = nb; }
        }
        if (pick != 0) edges.Add(EdgeKey(cell, pick));
        return edges;
    }

    /// <summary>Each cell its own room: the explore without the dungeon map.</summary>
    private static DungeonRooms CellRooms(Dictionary<uint, DungeonNavNode> graph)
    {
        var rooms = new DungeonRooms();
        foreach (uint c in graph.Keys.OrderBy(k => k))
        {
            rooms.RoomOf[c] = rooms.Cells.Count;
            rooms.Cells.Add(new List<uint> { c });
        }
        return rooms;
    }
}

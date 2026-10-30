// DungeonPathfinder, rooms half: routes laid on real openings, rooms crossed directly.
//
// Why: the cell walk put two points (30% and 50% of the way between cell centres) on every
// cell-to-cell step. A big room is several EnvCells joined by wide openings, so the patrol
// (which walks every graph edge once) went cell to cell inside it, around and across: a
// zig-zag. And at real doorways the points were only near the opening, not centred on it,
// and the steering's lookahead and sweep pass cut the corner into the door frame.
//
// Now, when the dungeon map geometry is there (DungeonGeometry):
//   - cells joined by a WIDE opening, or sharing one origin (the pieces of a junction), are
//     one room (union-find; hazard cells never merge);
//   - the patrol walks the ROOM graph: every doorway between rooms of the main route once
//     (same coverage goal as before, see GetMainRouteNodes), crossing each room in a straight
//     line from the doorway it came in by to the one it leaves by;
//   - every NARROW opening gets an approach point centred in front of it and an exit point
//     centred beyond it, flagged NavPoint.Doorway: the engine reaches them tightly and does
//     not cut corners at them, so the character lines up and goes through straight;
//   - a crossing that would leave the room's floor or hit a wall (an L-shaped room, a
//     pillar) goes through the room's own cells instead, string-pulled to the fewest points;
//   - a big room entered and left by the same doorway, or by two doorways close together
//     (a dead end, or one the patrol would only skirt), gets one point in its middle, once
//     a lap, so the patrol still looks into it.
// Without the geometry (DungeonLOS not loaded) the old cell walk is used unchanged.

using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

/// <summary>Cells grouped into rooms (cells joined by wide openings).</summary>
internal sealed class DungeonRooms
{
    public readonly Dictionary<uint, int> RoomOf = new();
    public readonly List<List<uint>> Cells = new();
}

internal static partial class DungeonPathfinder
{
    // Approach / exit points: this far in front of / beyond the opening's centre, shortened
    // (in DoorwayStandoffStep steps, down to the shortest) until the point is on the cell's
    // floor AND clear of walls (WallClearance).
    // 4 m (was 2.5): at 2.5 the turn into the opening still began close enough to climb the
    // raised sides of a hallway mouth (Tom, 2026-10-04: "plenty of room to get into the hallway").
    private const double DoorwayStandoffLongest = 4.0, DoorwayStandoffShortest = 1.0, DoorwayStandoffStep = 0.25;

    // Every point laid keeps at least this far from walls and floor edges where the space
    // allows. The character is a 0.48 m sphere stack (human Setup 0x02000001, PatrolProof
    // "human"); the other half metre is steering slack: the arrival ring, the arc of a turn
    // begun at speed, the sweep pass. Points closer than this put the character's body on the
    // wall when it arrives and it stuck there (Tom, 2026-10-04, testing 2026.10.4.x: "nav
    // points ... almost in the wall"). Measured with PatrolProof "clear": in crypts and
    // castles most approach/exit points in short bend and junction cells sat on the bend's
    // outer wall (Rithwic Crypt: 440 of 488 under 0.25 m), and a dead end's look-in point is
    // its cell origin, which is inside the end cap.
    // The patrol's is a setting (DungeonPatrolOptions.WallClearance, Settings "Distance From
    // Walls"); this default is what dunnav paths and recovery detours always use.
    internal const double WallClearance = DungeonPatrolOptions.WallClearanceDefault;
    // How far a point may be pushed off where its rule put it to get that clearance.
    private const double DoorwayPushMax = 2.0, CornerPushMax = 1.5, MiddlePushMax = 4.5;

    // Room crossings keep WallClearance either side of the walking line where the room allows,
    // else at least this much (the crossing as it was before the clearance work).
    private const double CrossingClearance = 0.5;
    // Emitted points closer than this to the previous one are dropped.
    private const double MinPointSpacing = 0.3;
    // A point within this of the straight line through its neighbours is dropped.
    private const double CollinearTolerance = 0.25;

    // ── Geometry cache ───────────────────────────────────────────────────────

    private static uint _geoLandblock;
    private static DungeonGeometry? _cachedGeometry;

    /// <summary>
    /// The openings, floors and walls of the landblock's dungeon (cached per landblock), from
    /// the dungeon map geometry DungeonLOS builds. Null when DungeonLOS is missing.
    /// </summary>
    public static DungeonGeometry? GetGeometry(uint landblockKey, DatDatabase cellDat, DungeonLOS? los)
    {
        if (los == null) return null;
        if (_cachedGeometry != null && _geoLandblock == landblockKey) return _cachedGeometry;
        var graph = GetGraph(landblockKey, cellDat);
        var portals = new List<(uint, Vector3[])>();
        var walls   = new List<(uint, Vector3[])>();
        var floors  = new List<(uint, Vector3[])>();
        try
        {
            foreach (var p in los.GetDungeonMapPolygons(landblockKey))
            {
                if (p.Vertices == null) continue;
                if (p.IsPortal) portals.Add((p.CellId, p.Vertices)); else walls.Add((p.CellId, p.Vertices));
            }
            foreach (var p in los.GetDungeonMapFloorPolygons(landblockKey))
                if (p.Vertices != null && !p.IsPortal) floors.Add((p.CellId, p.Vertices));
        }
        catch
        {
            return null;
        }
        _cachedGeometry = DungeonGeometry.Build(graph, portals, floors, walls);
        _geoLandblock   = landblockKey;
        return _cachedGeometry;
    }

    // ── Rooms ────────────────────────────────────────────────────────────────

    // Cells whose origins are this close (metres, each axis) are pieces of one junction.
    private const double SamePieceMeters = 1.0;

    /// <summary>
    /// Groups cells joined by wide, walkable openings (or pieces of one junction: cells with
    /// the same origin) into rooms. A cell in
    /// <paramref name="excluded"/> (a hazard) never merges: it stays a room of its own.
    /// </summary>
    internal static DungeonRooms BuildRooms(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo,
                                            IReadOnlySet<uint>? excluded = null)
    {
        var parent = new Dictionary<uint, uint>(graph.Count);
        foreach (uint c in graph.Keys) parent[c] = c;

        uint Find(uint x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        foreach (var node in graph.Values)
        {
            if (excluded != null && excluded.Contains(node.CellId)) continue;
            foreach (uint nb in node.Neighbors)
            {
                if (nb <= node.CellId || !graph.TryGetValue(nb, out var other)) continue;
                if (excluded != null && excluded.Contains(nb)) continue;
                if (IsDropEdge(node, other)) continue;
                bool wide = geo.TryGetDoorway(node.CellId, nb, out var d) && d.IsWide;
                // Pieces of one junction: AC builds a crossing or a corner from several cells
                // with the same origin (0x6544 has clusters of three). Their openings to each
                // other are inside the junction; kept apart, the patrol looped round them.
                bool samePiece = Math.Abs(node.NS - other.NS) * 240.0 < SamePieceMeters
                              && Math.Abs(node.EW - other.EW) * 240.0 < SamePieceMeters
                              && Math.Abs(node.Z - other.Z) < SamePieceMeters;
                if (!wide && !samePiece) continue;
                uint ra = Find(node.CellId), rb = Find(nb);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }

        var rooms = new DungeonRooms();
        var index = new Dictionary<uint, int>();
        var ids = new List<uint>(graph.Keys);
        ids.Sort();
        foreach (uint c in ids)
        {
            uint root = Find(c);
            if (!index.TryGetValue(root, out int r))
            {
                r = rooms.Cells.Count;
                index[root] = r;
                rooms.Cells.Add(new List<uint>());
            }
            rooms.Cells[r].Add(c);
            rooms.RoomOf[c] = r;
        }
        return rooms;
    }

    // ── Patrol over rooms ────────────────────────────────────────────────────

    private readonly record struct RoomEdge(int Room, ulong Key, uint From, uint To);

    /// <summary>
    /// The patrol over rooms: a closed walk taking every doorway between the main route's
    /// rooms once (greedy, re-treading only to reach the nearest unused doorway), crossing
    /// each room directly. Null when there is nothing to walk.
    /// </summary>
    private static NavRouteParser? BuildRoomPatrol(
        Dictionary<uint, DungeonNavNode> graph, uint startCell, HashSet<uint> mainRoute,
        IReadOnlySet<uint>? hazardCells, DungeonGeometry geo, DungeonPatrolOptions o)
    {
        var rooms = BuildRooms(graph, geo, hazardCells);
        var patrolRooms = new HashSet<int>();
        foreach (uint c in mainRoute)
            if (rooms.RoomOf.TryGetValue(c, out int r)) patrolRooms.Add(r);

        var adj = new Dictionary<int, List<RoomEdge>>();
        var edges = new HashSet<ulong>();
        foreach (int r in patrolRooms)
        {
            foreach (uint c in rooms.Cells[r])
            {
                if (hazardCells != null && hazardCells.Contains(c)) continue;
                if (!graph.TryGetValue(c, out var cn)) continue;
                foreach (uint nb in cn.Neighbors)
                {
                    if (!rooms.RoomOf.TryGetValue(nb, out int rn) || rn == r || !patrolRooms.Contains(rn)) continue;
                    if (hazardCells != null && hazardCells.Contains(nb)) continue;
                    if (!graph.TryGetValue(nb, out var nbn) || IsDropEdge(cn, nbn)) continue;
                    if (!adj.TryGetValue(r, out var list)) adj[r] = list = new List<RoomEdge>();
                    list.Add(new RoomEdge(rn, EdgeKey(c, nb), c, nb));
                    edges.Add(EdgeKey(c, nb));
                }
            }
        }
        if (edges.Count == 0) return null;

        // Start in the player's room if it has a doorway on the patrol, else the nearest that does.
        int startRoom = rooms.RoomOf.TryGetValue(startCell, out int sr) && adj.ContainsKey(sr) ? sr : -1;
        if (startRoom < 0 && graph.TryGetValue(startCell, out var sNode))
        {
            double bestD = double.MaxValue;
            foreach (int r in adj.Keys)
                foreach (uint c in rooms.Cells[r])
                {
                    if (!graph.TryGetValue(c, out var n)) continue;
                    double d = Sq(n.NS - sNode.NS) + Sq(n.EW - sNode.EW);
                    if (d < bestD) { bestD = d; startRoom = r; }
                }
        }
        if (startRoom < 0) return null;

        // Greedy edge-covering closed walk over the room multigraph.
        var walk = new List<RoomEdge>();
        var remaining = new HashSet<ulong>(edges);
        int cur = startRoom;
        int guard = edges.Count * 4 + rooms.Cells.Count + 16;
        while (remaining.Count > 0 && guard-- > 0)
        {
            RoomEdge? next = null;
            if (adj.TryGetValue(cur, out var list))
                foreach (var e in list)
                    if (remaining.Contains(e.Key)) { next = e; break; }

            if (next is RoomEdge step)
            {
                remaining.Remove(step.Key);
                walk.Add(step);
                cur = step.Room;
            }
            else
            {
                var hop = RoomBfs(adj, cur, r => r != cur && HasUnused(adj, r, remaining));
                if (hop == null) break;
                walk.AddRange(hop);
                cur = hop[hop.Count - 1].Room;
            }
        }
        if (cur != startRoom)
        {
            var back = RoomBfs(adj, cur, r => r == startRoom);
            if (back != null) walk.AddRange(back);
        }
        if (walk.Count == 0) return null;

        var steps = new List<Step>(walk.Count);
        foreach (var e in walk) steps.Add(new Step(e.From, e.To, e.Key));
        var route = new NavRouteParser { RouteType = NavRouteType.Circular };
        route.Points.AddRange(EmitSteps(graph, geo, rooms, steps, closed: true, null, 0, null, 0, o, out _));
        return route;
    }

    private static bool HasUnused(Dictionary<int, List<RoomEdge>> adj, int room, HashSet<ulong> remaining)
    {
        if (!adj.TryGetValue(room, out var list)) return false;
        foreach (var e in list) if (remaining.Contains(e.Key)) return true;
        return false;
    }

    // Shortest hop path (as the doorway steps taken) from src to the first room passing goal.
    private static List<RoomEdge>? RoomBfs(Dictionary<int, List<RoomEdge>> adj, int src, Func<int, bool> goal)
    {
        var prev = new Dictionary<int, (int From, RoomEdge Step)>();
        var seen = new HashSet<int> { src };
        var q = new Queue<int>();
        q.Enqueue(src);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            if (c != src && goal(c))
            {
                var path = new List<RoomEdge>();
                for (int at = c; at != src; at = prev[at].From) path.Add(prev[at].Step);
                path.Reverse();
                return path;
            }
            if (!adj.TryGetValue(c, out var list)) continue;
            foreach (var e in list)
            {
                if (!seen.Add(e.Room)) continue;
                prev[e.Room] = (c, e);
                q.Enqueue(e.Room);
            }
        }
        return null;
    }

    // ── Paths over rooms (dunnav, recovery detours) ──────────────────────────

    /// <summary>
    /// Points for walking a cell path from (startNS, startEW) to (destNS, destEW), the
    /// destination itself not included: straight on through every narrow opening, straight
    /// across rooms. Without geometry, the old cell-centre midpoints. <paramref name="doorways"/>
    /// counts the narrow openings given approach and exit points.
    /// </summary>
    public static List<NavPoint> BuildPathPoints(
        List<uint> cellPath, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo,
        IReadOnlySet<uint>? hazardCells,
        double startNS, double startEW, double startZ,
        double destNS, double destEW, double destZ,
        out int doorways)
    {
        doorways = 0;
        var pts = new List<NavPoint>();
        if (geo == null)
        {
            var tmp = new NavRouteParser();
            for (int i = 0; i + 1 < cellPath.Count; i++)
            {
                if (!graph.TryGetValue(cellPath[i], out var a) || !graph.TryGetValue(cellPath[i + 1], out var b)) continue;
                AddPortalWaypoints(tmp, a, b);
            }
            return tmp.Points;
        }

        var rooms = BuildRooms(graph, geo, hazardCells);
        var steps = new List<Step>();
        for (int i = 0; i + 1 < cellPath.Count; i++)
        {
            uint a = cellPath[i], b = cellPath[i + 1];
            if (!rooms.RoomOf.TryGetValue(a, out int ra) || !rooms.RoomOf.TryGetValue(b, out int rb) || ra == rb) continue;
            steps.Add(new Step(a, b, EdgeKey(a, b)));
        }
        if (cellPath.Count == 0) return pts;
        var start = WorldPoint(DungeonGeometry.NavToWorld(startEW), DungeonGeometry.NavToWorld(startNS), startZ, false);
        var dest  = WorldPoint(DungeonGeometry.NavToWorld(destEW), DungeonGeometry.NavToWorld(destNS), destZ, false);
        return EmitSteps(graph, geo, rooms, steps, closed: false, start, cellPath[0], dest, cellPath[cellPath.Count - 1],
                         DungeonPatrolOptions.Default, out doorways);
    }

    // ── Laying points along doorway steps ────────────────────────────────────

    private readonly record struct Step(uint From, uint To, ulong Key);

    /// <summary>
    /// Points for a sequence of doorway steps (cell From in one room to cell To in the next).
    /// Openings close together in a row (the Olthoi hive puts 0.7 m joint cells between its
    /// corridor pieces: three openings in 1.3 m) are one run: one approach point in front of
    /// the first, one exit point beyond the last, and the openings' centres in between only
    /// when the run turns. Between runs, the room is crossed (<see cref="CrossRoom"/>).
    /// <paramref name="closed"/>: a loop (the patrol), its last exit crossing back to its first
    /// approach. Otherwise <paramref name="start"/> / <paramref name="dest"/> (in startCell /
    /// destCell) begin and end it, neither included. <paramref name="doorways"/> counts the
    /// runs through narrow openings.
    /// </summary>
    private static List<NavPoint> EmitSteps(
        Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, DungeonRooms rooms, List<Step> steps, bool closed,
        NavPoint? start, uint startCell, NavPoint? dest, uint destCell, DungeonPatrolOptions o, out int doorways)
    {
        doorways = 0;
        var pts = new List<NavPoint>();
        double wall = o.WallClearance;

        if (steps.Count == 0)
        {
            if (start != null && dest != null && rooms.RoomOf.TryGetValue(startCell, out int r0))
                CrossRoom(pts, graph, geo, rooms, r0, start, startCell, dest, destCell, revisit: false, wall);
            return pts;
        }

        // A loop starts where a run starts (not halfway along one).
        if (closed && steps.Count > 1)
        {
            int k = 0;
            while (k < steps.Count && Continues(geo, steps[(k - 1 + steps.Count) % steps.Count], steps[k])) k++;
            if (k > 0 && k < steps.Count) steps = steps.GetRange(k, steps.Count - k).Concat(steps.GetRange(0, k)).ToList();
        }

        NavPoint? prev = start;
        uint prevCell = startCell;
        NavPoint? firstApproach = null;
        var lookedInto = new HashSet<int>();   // rooms already looked into this lap
        int i = 0;
        while (i < steps.Count)
        {
            int j = i;
            while (j + 1 < steps.Count && Continues(geo, steps[j], steps[j + 1])) j++;

            RunPoints(graph, geo, rooms, steps, i, j, wall, out var approach, out var middle, out var exit, out bool narrow);
            if (narrow) doorways++;
            if (prev != null)
            {
                // Only the patrol looks into rooms; a path to somewhere (dunnav, a detour) doesn't.
                string? why = null;
                bool revisit = i > 0 && (closed ? LooksIn(graph, rooms, lookedInto, steps[i - 1], steps[i], prev, approach, o.LookInRoomMeters, out why) : steps[i - 1].Key == steps[i].Key);
                CrossRoom(pts, graph, geo, rooms, rooms.RoomOf[steps[i].From], prev, prevCell, approach, steps[i].From, revisit, wall, why);
            }
            else firstApproach = approach;
            Append(pts, approach);
            foreach (var m in middle) Append(pts, m);
            Append(pts, exit);
            prev = exit;
            prevCell = steps[j].To;
            i = j + 1;
        }

        if (closed && firstApproach != null && prev != null)
        {
            bool revisit = LooksIn(graph, rooms, lookedInto, steps[steps.Count - 1], steps[0], prev, firstApproach, o.LookInRoomMeters, out string why);
            CrossRoom(pts, graph, geo, rooms, rooms.RoomOf[steps[0].From], prev, prevCell, firstApproach, steps[0].From, revisit, wall, why);
        }
        if (!closed && dest != null && prev != null && rooms.RoomOf.TryGetValue(destCell, out int rd))
            CrossRoom(pts, graph, geo, rooms, rd, prev, prevCell, dest, destCell, revisit: false, wall);

        DropCollinear(pts, closed);
        KeepLegsOffWalls(geo, pts, closed, wall);
        return pts;
    }

    // A leg brushing a wall: its least clearance (away from its end points) under this, or under
    // its end points' own when they are tighter (a narrow opening's centre).
    private const double LegMinClearance = 0.6;
    private const double LegEndSkip = 0.5, LegCornerPushMax = 2.5, LegCornerSearch = 4.0;

    /// <summary>
    /// Every leg between consecutive points is walked along (DungeonGeometry.LegClearance) and
    /// where it passes closer to a wall than LegMinClearance (cutting an inside corner after
    /// the points were pushed, a run of openings that jogs sideways, a turning run's diagonal
    /// across a corner), a corner is put in: the leg's tightest spot, pushed off the wall,
    /// kept only when both new legs are clearer than the old one.
    /// </summary>
    internal static void KeepLegsOffWalls(DungeonGeometry geo, List<NavPoint> pts, bool closed, double wall = WallClearance)
    {
        int budget = pts.Count / 2 + 8, perLeg = 0;
        int i = 0;
        while (i < (closed ? pts.Count : pts.Count - 1) && pts.Count >= 2)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            double least = LegLeast(geo, a, b, wall, out double wx, out double wy, out double wz);
            double target = Math.Min(Math.Min(LegMinClearance, wall), Math.Min(PointClearance(geo, a, wall), PointClearance(geo, b, wall)));
            if (least >= target - 0.05 || budget <= 0 || perLeg >= 3) { i++; perLeg = 0; continue; }

            // The corner leaving both new legs clearest, if that is clearly better.
            NavPoint? best = BestCorner(geo, a, b, wx, wy, wz, least + 0.1, wall, out _);
            if (best != null)
            {
                pts.Insert(i + 1, Tag(best, "leg-corner"));
                budget--; perLeg++;
                continue;   // check the first new leg next
            }
            // No one corner does it (a jog round two corners): a first corner the walk reaches
            // clear, then the best corner for the rest.
            bool two = false;
            foreach (var first in CornerCandidates(geo, wx, wy, wz, wall))
            {
                if (LegLeast(geo, a, first, wall, out _, out _, out _) < target - 0.05 || TurnsBack(a, first, b)) continue;
                double rest = LegLeast(geo, first, b, wall, out double rx, out double ry, out double rz);
                var second = BestCorner(geo, first, b, rx, ry, rz, Math.Max(rest, least) + 0.1, wall, out double m2);
                if (second == null || m2 <= least + 0.1) continue;
                pts.Insert(i + 1, Tag(first, "leg-corner"));
                pts.Insert(i + 2, Tag(second, "leg-corner"));
                budget -= 2; perLeg += 2;
                two = true;
                break;
            }
            if (two) continue;
            i++; perLeg = 0;
        }
    }

    // Corners to try for a leg whose tightest spot is (wx, wy, wz): that spot pushed off the
    // wall, and the centres of the openings near it (a leg that jogs through the wrong side of a
    // junction needs the opening it skipped).
    private static List<NavPoint> CornerCandidates(DungeonGeometry geo, double wx, double wy, double wz, double wall)
    {
        double cx = wx, cy = wy;
        geo.PushClear(ref cx, ref cy, wz, wall, LegCornerPushMax);
        var list = new List<NavPoint> { WorldPoint(cx, cy, wz, false) };
        foreach (var d in geo.DoorwaysNear(wx, wy, wz, LegCornerSearch)) list.Add(WorldPoint(d.X, d.Y, d.Z, false));
        return list;
    }

    // The candidate leaving both new legs a-corner-b clearest, if better than mustBeat.
    private static NavPoint? BestCorner(DungeonGeometry geo, NavPoint a, NavPoint b, double wx, double wy, double wz,
                                        double mustBeat, double wall, out double bestMin)
    {
        NavPoint? best = null;
        bestMin = mustBeat;
        foreach (var corner in CornerCandidates(geo, wx, wy, wz, wall))
        {
            if (PointClearance(geo, corner, wall) <= mustBeat) continue;
            if (TurnsBack(a, corner, b)) continue;
            double m = Math.Min(LegLeast(geo, a, corner, wall, out _, out _, out _), LegLeast(geo, corner, b, wall, out _, out _, out _));
            if (m > bestMin) { bestMin = m; best = corner; }
        }
        return best;
    }

    // A corner that sends the walk back more than 120 degrees: a detour round something the
    // map draws but the character walks through (render faces across a ramp at 0x6544), not a
    // way round a corner.
    private static bool TurnsBack(NavPoint a, NavPoint corner, NavPoint b)
    {
        double ux = corner.EW - a.EW, uy = corner.NS - a.NS, vx = b.EW - corner.EW, vy = b.NS - corner.NS;
        double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
        if (lu < 1e-12 || lv < 1e-12) return false;
        return (ux * vx + uy * vy) / (lu * lv) < Math.Cos(120.0 * Math.PI / 180.0);
    }

    private static double LegLeast(DungeonGeometry geo, NavPoint a, NavPoint b, double wall, out double wx, out double wy, out double wz)
        => geo.LegClearance(DungeonGeometry.NavToWorld(a.EW), DungeonGeometry.NavToWorld(a.NS), a.Z * 240.0,
                            DungeonGeometry.NavToWorld(b.EW), DungeonGeometry.NavToWorld(b.NS), b.Z * 240.0,
                            wall, LegEndSkip, out wx, out wy, out wz);

    private static double PointClearance(DungeonGeometry geo, NavPoint p, double wall)
        => geo.ClearanceMemo(DungeonGeometry.NavToWorld(p.EW), DungeonGeometry.NavToWorld(p.NS), p.Z * 240.0, wall + 0.5);

    // A big room (its cells spread over 10 m or more by default: DungeonPatrolOptions.LookInRoomMeters,
    // Settings "Walk Into Rooms From") left again close to where it was entered is looked into: a
    // walk to its middle and back. A straight crossing to a nearby opening would only skirt
    // it (the patrol hunts; the old cell walk went all round such rooms). Also any room left
    // by the opening it was entered by.
    private const double LookInMaxSkirtMeters = 8.0;

    // Once per room per lap: a hub room with many doorways was otherwise walked to its middle
    // again every time the patrol passed between two of its doorways.
    private static bool LooksIn(Dictionary<uint, DungeonNavNode> graph, DungeonRooms rooms, HashSet<int> lookedInto,
                                Step inStep, Step outStep, NavPoint entered, NavPoint leaving, double lookInMeters, out string why)
    {
        why = "cross";
        if (!rooms.RoomOf.TryGetValue(outStep.From, out int r)) return false;
        double spread = RoomSpreadMeters(graph, rooms.Cells[r]);
        bool big = spread >= lookInMeters;
        bool sameDoor = inStep.Key == outStep.Key;
        double skirt = NavPointYd(entered, leaving);
        bool look = (sameDoor && (big || rooms.Cells[r].Count == 1))
                 || (big && skirt < LookInMaxSkirtMeters);
        bool first = look && lookedInto.Add(r);
        if (PointLog != null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string what = sameDoor && (big || rooms.Cells[r].Count == 1)
                ? (big ? "same-doorway,big" : "same-doorway,1-cell")
                : "big,skirt" + skirt.ToString("F1", inv) + "m";
            why = !look ? (sameDoor ? "cross(same-doorway,small)" : "cross(" + (big ? "big," : "small,") + "other-doorway)")
                : first ? "look-in(" + what + ",spread" + spread.ToString("F0", inv) + "m)"
                : "cross(already-looked-in)";
        }
        return first;
    }

    // The diagonal of the box round a room's cell origins: 0 for the pieces of one junction
    // (several cells, one origin), 28 m for a 3x3 room of 10 m cells.
    internal static double RoomSpreadMeters(Dictionary<uint, DungeonNavNode> graph, List<uint> cells)
    {
        double minN = double.MaxValue, maxN = double.MinValue, minE = double.MaxValue, maxE = double.MinValue;
        foreach (uint c in cells)
        {
            if (!graph.TryGetValue(c, out var n)) continue;
            minN = Math.Min(minN, n.NS); maxN = Math.Max(maxN, n.NS);
            minE = Math.Min(minE, n.EW); maxE = Math.Max(maxE, n.EW);
        }
        if (minN > maxN) return 0;
        return Math.Sqrt(Sq(maxN - minN) + Sq(maxE - minE)) * 240.0;
    }

    // Openings this close (centre to centre) are taken as one: the room between them is too
    // short for an exit point and the next approach point (DoorwayStandoffs[0] each) without
    // doubling back.
    private const double RunGapMeters = 8.0;
    // cos(~93 degrees): openings at right angles (a staircase) still continue a run.
    private const double RightAngleSlack = 0.05;

    private static bool Continues(DungeonGeometry geo, Step a, Step b)
    {
        if (a.Key == b.Key) return false;   // turning back through the same opening
        if (!geo.TryGetDoorway(a.From, a.To, out var da) || !geo.TryGetDoorway(b.From, b.To, out var db)) return false;
        double dx = da.X - db.X, dy = da.Y - db.Y;
        if (dx * dx + dy * dy >= RunGapMeters * RunGapMeters || Math.Abs(da.Z - db.Z) >= 2.0) return false;
        // A run goes on: through the next opening no more than 90 degrees off the last. Turning
        // back (in at one opening, out at another beside it) is a visit to the room between.
        // 90 degrees exactly counts (2026-10-14): a diagonal corridor is a staircase of square
        // cells whose openings face north, east, north, east...; each failed the old "> 0" by a
        // hair, got its own approach and exit point 4 m out along its facing, and the patrol
        // walked the staircase (zig-zag) instead of the corridor's centre line.
        var (ax, ay) = da.NormalFrom(a.From);
        var (bx, by) = db.NormalFrom(b.From);
        return ax * bx + ay * by > -RightAngleSlack;
    }

    // The approach point before steps[i], the exit point after steps[j], and the opening centres
    // between them when the run turns (more than 25 degrees between openings).
    private static void RunPoints(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, DungeonRooms rooms,
                                  List<Step> steps, int i, int j, double wall,
                                  out NavPoint approach, out List<NavPoint> middle, out NavPoint exit, out bool narrow)
    {
        middle = new List<NavPoint>();
        if (i == j)
        {
            if (!DoorwayPoints(graph, geo, rooms, steps[i].From, steps[i].To, wall, out approach, out exit, out narrow)) narrow = false;
            return;
        }

        DoorwayPoints(graph, geo, rooms, steps[i].From, steps[i].To, wall, out approach, out _, out bool n0);
        DoorwayPoints(graph, geo, rooms, steps[j].From, steps[j].To, wall, out _, out exit, out bool n1);
        narrow = n0 || n1;
        var doors = new List<(DungeonDoorway Door, uint From, int Step)>();
        for (int k = i; k <= j; k++)
        {
            if (!geo.TryGetDoorway(steps[k].From, steps[k].To, out var d)) continue;
            doors.Add((d, steps[k].From, k));
            narrow |= !d.IsWide;
        }
        bool turns = false;
        for (int k = 1; k < doors.Count && !turns; k++)
        {
            var (ax, ay) = doors[k - 1].Door.NormalFrom(doors[k - 1].From);
            var (bx, by) = doors[k].Door.NormalFrom(doors[k].From);
            turns = ax * bx + ay * by < Math.Cos(25.0 * Math.PI / 180.0);
        }
        if (turns)
        {
            // The openings' centres, and between two of them the way through the room they
            // share: the straight line from one centre to the next cut the inside corner of a
            // bend (crypt corners: an L of two junction pieces, the line through its wall).
            NavPoint? prevM = null;
            int prevStep = -1;
            foreach (var (d, _, k) in doors)
            {
                var m = Note(Tag(WorldPoint(d.X, d.Y, d.Z, !d.IsWide), "run-opening"), steps[k].From, steps[k].From, steps[k].To, "doorway-run");
                if (prevM != null && k == prevStep + 1
                    && rooms.RoomOf.TryGetValue(steps[prevStep].To, out int ra) && rooms.RoomOf.TryGetValue(steps[k].From, out int rb) && ra == rb)
                    PullThrough(middle, graph, geo, rooms, rooms.Cells[ra], prevM, steps[prevStep].To, m, steps[k].From, wall);
                Append(middle, m);
                prevM = m;
                prevStep = k;
            }
        }
        if (narrow) { approach.Doorway = true; exit.Doorway = true; }
    }

    // ── Doorway points ───────────────────────────────────────────────────────

    /// <summary>
    /// The approach point (in a, centred in front of the opening) and exit point (in b, centred
    /// beyond it) for stepping from cell a into cell b. True when the opening is known;
    /// <paramref name="narrow"/> when it is a doorway (the points are then flagged
    /// NavPoint.Doorway). Unknown openings fall back to the old 30% / 50% points.
    /// </summary>
    private static bool DoorwayPoints(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, DungeonRooms rooms,
                                      uint a, uint b, double wall, out NavPoint approach, out NavPoint exit, out bool narrow)
    {
        narrow = false;
        if (geo.TryGetDoorway(a, b, out var d))
        {
            var (nx, ny) = d.NormalFrom(a);
            narrow = !d.IsWide;
            approach = Note(Tag(DoorwayPoint(geo, rooms, a, d, -nx, -ny, narrow, wall), "approach"), a, a, b, "doorway");
            exit     = Note(Tag(DoorwayPoint(geo, rooms, b, d, nx, ny, narrow, wall), "exit"), b, a, b, "doorway");
            return true;
        }

        var na = graph[a]; var nb = graph[b];
        double dNS = nb.NS - na.NS, dEW = nb.EW - na.EW, dZ = nb.Z - na.Z;
        approach = Tag(new NavPoint { Type = NavPointType.Point, NS = na.NS + dNS * 0.3, EW = na.EW + dEW * 0.3, Z = (na.Z + dZ * 0.3) / 240.0 }, "cell-fallback");
        exit     = Tag(new NavPoint { Type = NavPointType.Point, NS = na.NS + dNS * 0.5, EW = na.EW + dEW * 0.5, Z = (na.Z + dZ * 0.5) / 240.0 }, "cell-fallback");
        Note(approach, a, a, b, "doorway(no-opening)"); Note(exit, 0, a, b, "doorway(no-opening)");
        PushOffWalls(geo, approach, RoomCells(rooms, a), CornerPushMax, wall);
        PushOffWalls(geo, exit, null, CornerPushMax, wall);   // on the line between the two rooms
        return false;
    }

    /// <summary>
    /// The doorway point on <paramref name="cell"/>'s side of the opening: on its centre line,
    /// at the longest standoff (4 m down to 1 m) that is on the room's floor and keeps
    /// WallClearance from every wall. Where no standoff does (a short bend or junction cell:
    /// the centre line runs into the bend's outer wall), the clearest one, pushed off the walls
    /// (in a corridor too narrow for the clearance, onto its middle).
    /// </summary>
    private static NavPoint DoorwayPoint(DungeonGeometry geo, DungeonRooms rooms, uint cell, DungeonDoorway d,
                                         double dirX, double dirY, bool narrow, double wall)
    {
        var cells = RoomCells(rooms, cell);
        double s = PickStandoff(geo, cells, d, dirX, dirY, out double clearance, wall);
        var p = WorldPoint(d.X + dirX * s, d.Y + dirY * s, d.Z, narrow);
        if (clearance < wall) PushOffWalls(geo, p, cells, DoorwayPushMax, wall);
        return p;
    }

    private static IReadOnlyCollection<uint> RoomCells(DungeonRooms rooms, uint cell)
        => rooms.RoomOf.TryGetValue(cell, out int r) ? rooms.Cells[r] : new[] { cell };

    // The longest standoff whose point is on the floor of the cell's room with WallClearance;
    // else the one with the most clearance (the longer of near ties); else the shortest.
    internal static double PickStandoff(DungeonGeometry geo, IReadOnlyCollection<uint> cells, DungeonDoorway d,
                                        double dirX, double dirY, out double clearance, double wall = WallClearance)
    {
        double bestS = -1;
        clearance = 0;
        for (double s = DoorwayStandoffLongest; s >= DoorwayStandoffShortest - 1e-9; s -= DoorwayStandoffStep)
        {
            double px = d.X + dirX * s, py = d.Y + dirY * s;
            if (!geo.OnFloorOfAny(cells, px, py, d.Z)) continue;
            double c = geo.ClearanceMemo(px, py, d.Z, wall + 0.5);
            if (c >= wall) { clearance = c; return s; }
            if (bestS < 0 || c > clearance + 0.05) { bestS = s; clearance = c; }
        }
        return bestS > 0 ? bestS : DoorwayStandoffShortest;
    }

    /// <summary>Moves a point (in place) to <paramref name="wall"/> (the patrol's wall distance,
    /// WallClearance by default) from walls and floor edges, at most <paramref name="maxMove"/>,
    /// staying on the floor of <paramref name="cells"/> when given.</summary>
    private static void PushOffWalls(DungeonGeometry geo, NavPoint p, IReadOnlyCollection<uint>? cells, double maxMove, double wall)
    {
        double x = DungeonGeometry.NavToWorld(p.EW), y = DungeonGeometry.NavToWorld(p.NS);
        double ox = x, oy = y;
        geo.PushClear(ref x, ref y, p.Z * 240.0, wall, maxMove, cells);
        if (x == ox && y == oy) return;
        p.EW = DungeonGeometry.WorldToNav(x);
        p.NS = DungeonGeometry.WorldToNav(y);
    }

    // ── Crossing a room ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds the points needed to walk from <paramref name="from"/> (in cell fromCell) to
    /// <paramref name="to"/> (in cell toCell) inside one room, neither end included: nothing
    /// when the straight line is clear, else the room's cells string-pulled. A revisit (in and
    /// out by one doorway) first goes to the room's middle.
    /// </summary>
    private static void CrossRoom(List<NavPoint> pts, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo,
                                  DungeonRooms rooms, int room, NavPoint from, uint fromCell, NavPoint to, uint toCell,
                                  bool revisit, double wall, string? why = null)
    {
        var cells = rooms.Cells[room];
        Trace?.Invoke($"cross room {room} ({cells.Count}) revisit={revisit} from={fromCell:X8} to={toCell:X8}");
        if (revisit)
        {
            var mid = RoomMiddle(graph, geo, cells, wall, out uint midCell);
            if (mid != null)
            {
                int before = pts.Count;
                Note(mid, midCell);
                PullThrough(pts, graph, geo, rooms, cells, from, fromCell, mid, midCell, wall);
                Append(pts, mid);
                PullThrough(pts, graph, geo, rooms, cells, mid, midCell, to, toCell, wall);
                NoteCrossing(pts, before, room, why ?? "look-in");
                return;
            }
        }
        int start = pts.Count;
        PullThrough(pts, graph, geo, rooms, cells, from, fromCell, to, toCell, wall);
        NoteCrossing(pts, start, room, why ?? "cross");
    }

    internal static Action<string>? Trace;

    // Legs across a room keep WallClearance either side where the room allows: the straight
    // line, else the room's cells string-pulled with every leg that clear. Where the room is
    // too tight for that, the crossing as before (0.5 m either side).
    private static void PullThrough(List<NavPoint> pts, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo,
                                    DungeonRooms rooms, List<uint> cells, NavPoint from, uint fromCell, NavPoint to, uint toCell,
                                    double wall)
    {
        if (Clear(geo, cells, from, to, wall)) return;

        // Through the room's own cells: the openings between them (or cell-centre midpoints),
        // pushed off the walls like every other point.
        var cellPath = CellBfs(graph, rooms, fromCell, toCell);
        var corners = new List<NavPoint>();
        for (int i = 0; i + 1 < cellPath.Count; i++)
        {
            uint a = cellPath[i], b = cellPath[i + 1];
            NavPoint corner;
            if (geo.TryGetDoorway(a, b, out var d)) corner = Note(Tag(WorldPoint(d.X, d.Y, d.Z, false), "pull-opening"), a, a, b);
            else
            {
                var na = graph[a]; var nb = graph[b];
                corner = Note(Tag(new NavPoint { Type = NavPointType.Point, NS = (na.NS + nb.NS) * 0.5, EW = (na.EW + nb.EW) * 0.5, Z = (na.Z + nb.Z) * 0.5 / 240.0 }, "pull-midpoint"), a, a, b);
            }
            PushOffWalls(geo, corner, cells, CornerPushMax, wall);
            corners.Add(corner);
        }
        corners.Add(to);

        var strict = new List<NavPoint>();
        if (StringPull(strict, geo, cells, from, corners, wall))
        {
            foreach (var p in strict) Append(pts, p);
            return;
        }
        double loosest = Math.Min(CrossingClearance, wall);
        if (Clear(geo, cells, from, to, loosest)) return;
        var loose = new List<NavPoint>();
        StringPull(loose, geo, cells, from, corners, loosest);
        foreach (var p in loose) Append(pts, p);
    }

    // From the anchor, the farthest corner in a clear straight line, and on; the last corner
    // (the end) not included. True when every leg of the result is clear.
    private static bool StringPull(List<NavPoint> outPts, DungeonGeometry geo, List<uint> cells, NavPoint from,
                                   List<NavPoint> corners, double clearance)
    {
        var to = corners[corners.Count - 1];
        if (Clear(geo, cells, from, to, clearance)) return true;
        NavPoint anchor = from;
        int at = 0;
        bool allClear = true;
        int guard = corners.Count + 2;
        while (at < corners.Count - 1 && guard-- > 0)
        {
            int pick = -1;
            for (int k = corners.Count - 1; k >= at; k--)
                if (Clear(geo, cells, anchor, corners[k], clearance)) { pick = k; break; }
            if (pick < 0) { pick = at; allClear = false; }   // always move on at least one corner
            if (pick == corners.Count - 1) return allClear;  // the end is in sight
            outPts.Add(corners[pick]);
            anchor = corners[pick];
            at = pick + 1;
            if (Clear(geo, cells, anchor, to, clearance)) return allClear;
        }
        return allClear && Clear(geo, cells, anchor, to, clearance);
    }

    private static bool Clear(DungeonGeometry geo, List<uint> cells, NavPoint a, NavPoint b, double clearance)
        => geo.SegmentClear(cells,
            DungeonGeometry.NavToWorld(a.EW), DungeonGeometry.NavToWorld(a.NS), a.Z * 240.0,
            DungeonGeometry.NavToWorld(b.EW), DungeonGeometry.NavToWorld(b.NS), b.Z * 240.0,
            clearance);

    // Shortest cell path inside one room (inclusive), or just [from, to] when there is none.
    private static List<uint> CellBfs(Dictionary<uint, DungeonNavNode> graph, DungeonRooms rooms, uint from, uint to)
    {
        if (from == to) return new List<uint> { from };
        int room = rooms.RoomOf.TryGetValue(from, out int r) ? r : -1;
        var prev = new Dictionary<uint, uint>();
        var seen = new HashSet<uint> { from };
        var q = new Queue<uint>();
        q.Enqueue(from);
        while (q.Count > 0)
        {
            uint c = q.Dequeue();
            if (c == to) return RebuildPath(prev, from, to);
            if (!graph.TryGetValue(c, out var n)) continue;
            foreach (uint nb in n.Neighbors)
            {
                if (!rooms.RoomOf.TryGetValue(nb, out int rn) || rn != room) continue;
                if (!seen.Add(nb)) continue;
                prev[nb] = c;
                q.Enqueue(nb);
            }
        }
        return new List<uint> { from, to };
    }

    // The point a patrol visits to look into a room: the middle of its cell centres when that
    // is on its floor, else the cell centre nearest that middle; then pushed off the walls.
    private static NavPoint? RoomMiddle(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, List<uint> cells, double wall, out uint midCell)
    {
        midCell = 0;
        double sx = 0, sy = 0, sz = 0;
        int n = 0;
        foreach (uint c in cells)
        {
            if (!graph.TryGetValue(c, out var node)) continue;
            sx += DungeonGeometry.NavToWorld(node.EW); sy += DungeonGeometry.NavToWorld(node.NS); sz += node.Z;
            n++;
        }
        if (n == 0) return null;
        sx /= n; sy /= n; sz /= n;

        double best = double.MaxValue;
        DungeonNavNode? nearest = null;
        foreach (uint c in cells)
        {
            if (!graph.TryGetValue(c, out var node)) continue;
            double d = Sq(DungeonGeometry.NavToWorld(node.EW) - sx) + Sq(DungeonGeometry.NavToWorld(node.NS) - sy);
            if (d < best) { best = d; nearest = node; }
        }
        if (nearest == null) return null;
        midCell = nearest.CellId;
        NavPoint mid = geo.OnCellFloor(nearest.CellId, sx, sy, sz) || geo.OnFloorOfAny(cells, sx, sy, sz) && !geo.HasFloor(nearest.CellId)
            ? Tag(WorldPoint(sx, sy, sz, false), "room-middle")
            : Tag(WorldPoint(DungeonGeometry.NavToWorld(nearest.EW), DungeonGeometry.NavToWorld(nearest.NS), nearest.Z, false), "room-middle-cell");
        // A one-cell dead end's origin is inside its end cap; a big room's middle can be a pillar.
        PushOffWalls(geo, mid, cells, MiddlePushMax, wall);
        return mid;
    }

    // ── Point list helpers ───────────────────────────────────────────────────

    /// <summary>Offline tools (PatrolProof) set this to learn which rule laid each point:
    /// approach, exit, run-opening, pull-opening, pull-midpoint, room-middle, room-middle-cell,
    /// cell-fallback.</summary>
    internal static Dictionary<NavPoint, string>? KindLog;

    /// <summary>Why one patrol point is where it is (diagnostics only; filled while
    /// <see cref="PointLog"/> is set, never read by the route itself).</summary>
    internal sealed class PatrolPointInfo
    {
        public string Kind = "";        // the rule that laid it (as KindLog)
        public uint   Cell;             // the cell the rule put it in (0 = not known)
        public uint   OpenFrom, OpenTo; // the opening it belongs to (doorway-type points)
        public int    Room = -1;        // the room being crossed, for points laid while crossing one
        public string Why = "";         // doorway / cross / look-in reason
    }

    /// <summary>Set (per thread) around a route build to record a <see cref="PatrolPointInfo"/>
    /// for every point laid: BuildDunPatrol's waypoint log, PatrolProof "waypoints".</summary>
    [ThreadStatic] internal static Dictionary<NavPoint, PatrolPointInfo>? PointLog;

    /// <summary>GetMainRouteNodes' sizes for the last build while <see cref="PointLog"/> was set.</summary>
    [ThreadStatic] internal static string? MainRouteNote;

    private static NavPoint Tag(NavPoint p, string kind)
    {
        if (KindLog != null) KindLog[p] = kind;
        if (PointLog != null) Info(p).Kind = kind;
        return p;
    }

    private static PatrolPointInfo Info(NavPoint p)
    {
        if (!PointLog!.TryGetValue(p, out var info)) PointLog[p] = info = new PatrolPointInfo();
        return info;
    }

    // Records the cell (and opening) a point was laid for, when the point log is on.
    private static NavPoint Note(NavPoint p, uint cell, uint openFrom = 0, uint openTo = 0, string? why = null)
    {
        if (PointLog == null) return p;
        var info = Info(p);
        info.Cell = cell;
        if (openFrom != 0) { info.OpenFrom = openFrom; info.OpenTo = openTo; }
        if (why != null) info.Why = why;
        return p;
    }

    // Points added to pts from index `from` on were laid crossing `room` for `why`.
    private static void NoteCrossing(List<NavPoint> pts, int from, int room, string why)
    {
        if (PointLog == null) return;
        for (int k = Math.Max(0, from); k < pts.Count; k++)
        {
            var info = Info(pts[k]);
            if (info.Room < 0) { info.Room = room; info.Why = why; }
        }
    }

    /// <summary>The cell whose floor is under a point, else the nearest cell centre.</summary>
    internal static uint CellAt(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry? geo, NavPoint p)
    {
        if (geo != null)
        {
            double x = DungeonGeometry.NavToWorld(p.EW), y = DungeonGeometry.NavToWorld(p.NS), z = p.Z * 240.0;
            uint best = 0; double bestD = double.MaxValue;
            foreach (var n in graph.Values)
            {
                if (!geo.OnCellFloor(n.CellId, x, y, z)) continue;
                double d = Math.Abs(n.Z - z);
                if (d < bestD) { bestD = d; best = n.CellId; }
            }
            if (best != 0) return best;
        }
        return NearestCell(graph, p.NS, p.EW, (float)(p.Z * 240.0));
    }

    /// <summary>
    /// One line per patrol waypoint (index, world x/y/z, cell, room and its size, the rule that
    /// laid it, the opening for doorway points, why) and a summary line, from the point log of
    /// the build. Rooms are BuildRooms(graph, geo, hazards), as the builder used them.
    /// Lines carry no prefix; the plugin adds "[RynthAi] DunNav-Patrol ".
    /// </summary>
    internal static List<string> DescribePatrol(NavRouteParser route, Dictionary<uint, DungeonNavNode> graph,
                                                DungeonGeometry? geo, IReadOnlySet<uint>? hazards,
                                                Dictionary<NavPoint, PatrolPointInfo>? log, int tinyMaxCells = 5)
    {
        var lines = new List<string>();
        var rooms = geo != null ? BuildRooms(graph, geo, hazards) : null;
        int tiny = 0, tinyEntered = 0, tinyLooked = 0;
        var tinyRooms = new SortedDictionary<int, (int Cells, int Entered, int Looked)>();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < route.Points.Count; i++)
        {
            var p = route.Points[i];
            PatrolPointInfo? info = null;
            log?.TryGetValue(p, out info);
            uint cell = info != null && info.Cell != 0 ? info.Cell : CellAt(graph, geo, p);
            int room = rooms != null && rooms.RoomOf.TryGetValue(cell, out int rr) ? rr : -1;
            int size = room >= 0 ? rooms!.Cells[room].Count : 0;
            string kind = info?.Kind is { Length: > 0 } k ? k : "unlogged";
            string why = info?.Why ?? "";
            string open = "";
            if (info != null && info.OpenFrom != 0)
            {
                string w = geo != null && geo.TryGetDoorway(info.OpenFrom, info.OpenTo, out var d)
                    ? (d.IsWide ? " wide " : " narrow ") + d.Width.ToString("F1", inv) + "m" : "";
                open = $" open {info.OpenFrom:X8}>{info.OpenTo:X8}{w}";
            }
            string crossed = info != null && info.Room >= 0 && info.Room != room && rooms != null
                ? $" crossing room {info.Room} ({rooms.Cells[info.Room].Count})" : "";
            bool haz = hazards != null && hazards.Contains(cell);
            lines.Add(string.Format(inv, "wp {0} ({1:F1},{2:F1},{3:F1}) cell {4:X8} room {5} ({6} cells){7} {8}{9}{10}{11}{12}",
                i, DungeonGeometry.NavToWorld(p.EW), DungeonGeometry.NavToWorld(p.NS), p.Z * 240.0,
                cell, room, size, haz ? " HAZARD" : "", kind, p.Doorway ? " [door]" : "", open, crossed,
                why.Length > 0 ? " why=" + why : ""));
            if (room >= 0 && size <= tinyMaxCells)
            {
                tiny++;
                bool looked = why.StartsWith("look-in", StringComparison.Ordinal) && info!.Room == room;
                if (looked) tinyLooked++; else tinyEntered++;
                tinyRooms.TryGetValue(room, out var t);
                tinyRooms[room] = (size, t.Entered + (looked ? 0 : 1), t.Looked + (looked ? 1 : 0));
            }
        }
        string roomList = string.Join(" ", tinyRooms.Select(kv => $"{kv.Key}({kv.Value.Cells}c:{kv.Value.Entered}e/{kv.Value.Looked}l)"));
        if (MainRouteNote != null) lines.Add("wp " + MainRouteNote);
        lines.Add($"wp summary: {route.Points.Count} points, {rooms?.Cells.Count ?? 0} rooms, {hazards?.Count ?? 0} hazard cells; " +
                  $"in rooms of <= {tinyMaxCells} cells: {tiny} (entered {tinyEntered}, looked into {tinyLooked}) in {tinyRooms.Count} rooms: {roomList}");
        return lines;
    }

    private static NavPoint WorldPoint(double wx, double wy, double wz, bool doorway) => new()
    {
        Type    = NavPointType.Point,
        EW      = DungeonGeometry.WorldToNav(wx),
        NS      = DungeonGeometry.WorldToNav(wy),
        Z       = wz / 240.0,
        Doorway = doorway,
    };

    // Adds p unless it is on top of the last point (keeping the doorway flag if either has it).
    private static void Append(List<NavPoint> pts, NavPoint p)
    {
        if (pts.Count > 0)
        {
            var last = pts[pts.Count - 1];
            if (NavPointYd(last, p) < MinPointSpacing)
            {
                last.Doorway |= p.Doorway;
                return;
            }
        }
        pts.Add(p);
    }

    // Drops points within CollinearTolerance of the straight line through their neighbours:
    // e.g. the exit and approach points between two openings in line along a corridor.
    private static void DropCollinear(List<NavPoint> pts, bool closed)
    {
        bool changed = true;
        while (changed && pts.Count > 2)
        {
            changed = false;
            for (int i = closed ? 0 : 1; i < (closed ? pts.Count : pts.Count - 1); i++)
            {
                if (pts.Count <= 2) break;
                var a = pts[(i - 1 + pts.Count) % pts.Count];
                var p = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if (!NavRouteParser.IsPlainWaypoint(p.Type)) continue;
                if (!WithinSegment(a, b, p)) continue;
                if (SegDistYards(a, b, p) < CollinearTolerance)
                {
                    pts.RemoveAt(i);
                    i--;
                    changed = true;
                }
            }
        }
    }

    // p lies between a and b along their line (not beyond either end).
    private static bool WithinSegment(NavPoint a, NavPoint b, NavPoint p)
    {
        double abN = b.NS - a.NS, abE = b.EW - a.EW, apN = p.NS - a.NS, apE = p.EW - a.EW;
        double dot = abN * apN + abE * apE, len2 = abN * abN + abE * abE;
        return dot >= 0 && dot <= len2;
    }

    private static double NavPointYd(NavPoint a, NavPoint b) => Math.Sqrt(Sq(a.NS - b.NS) + Sq(a.EW - b.EW)) * 240.0;

    private static double Sq(double v) => v * v;
}

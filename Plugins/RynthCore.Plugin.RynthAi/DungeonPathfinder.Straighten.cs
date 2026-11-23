// DungeonPathfinder, straightening (2026-11-15).
//
// The explore's walk is laid room by room: the openings between one room's cells are string-pulled
// (PullThrough), the doorways between rooms are taken straight on. A corridor the dungeon map splits
// into many small rooms (DreamWeave Thread 0x00ED: zig-zag corridors of 3-5 cell rooms; 182 of the
// walk's 334 points sat at openings) gets a point at every room boundary, and the character turned at
// each one, 2.5-5 m apart ("the nav point does a lot of unnecessary turning", Tom).
//
// StraightenWalk goes over the whole walk once it is laid: from each point, the farthest point ahead
// (at most StraightenLookAhead) that a straight leg reaches
//   - on walkable floor, keeping the walk's wall clearance, or at least as much as the legs it
//     replaces had (LegClearance: a floor edge counts as a wall, so never over a ledge or a pit), and
//   - passing through the room of every point it skips (the explore must still walk into each room
//     it was laid through), and
//   - staying on the floor all the way, a step at a time (OnFloorAll, as the hole rule judges a leg).
//     2026-11-17, a DreamWeave Thread in 0x0278: a ramp climbs south from a room at -36 to a corridor
//     at -30 that runs back north over that room. The walk went up the ramp and back along the
//     corridor; seen from above every point of that U-turn lies on one line, so one straight leg north
//     "passed through" all their rooms, and its samples kept their clearance (none fell in the 7 m with
//     no floor between the room's end and the corridor). The character ran north along the bottom
//     into the room's end wall and stuck there ("running into a wall in a place that makes no sense").
// Only plain waypoints are skipped, and never one in keepPoints: the middles of the rooms the explore walks
// into (2026-11-16, enter and scan). A look into a dead end, in and out by one doorway, passes "through" the
// room at its doorway: Thread 0x00B5's boss room lost its middle here and was only touched at the door.

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

internal static partial class DungeonPathfinder
{
    /// <summary>Off: the walk as laid (tests compare the two).</summary>
    internal static bool StraightenEnabled = true;

    /// <summary>How many points ahead a straightened leg may reach.</summary>
    internal const int StraightenLookAhead = 16;
    // Samples along a leg for the rooms it passes through (metres apart).
    private const double StraightenSampleMeters = 0.5;

    /// <summary>
    /// Straightens <paramref name="pts"/> in place (see the file's comment). Returns how many points
    /// were taken out.
    /// </summary>
    internal static int StraightenWalk(List<NavPoint> pts, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo,
                                       DungeonRooms rooms, double wall, IReadOnlyCollection<NavPoint>? keepPoints = null)
    {
        var keepSet = keepPoints is { Count: > 0 } ? new HashSet<NavPoint>(keepPoints, ReferenceEqualityComparer.Instance) : null;
        if (pts.Count < 3 || !StraightenEnabled) return 0;
        var cells = new uint[pts.Count];
        for (int k = 0; k < pts.Count; k++) cells[k] = CellAt(graph, geo, pts[k]);
        // The clearance of each leg as laid (leg k joins point k and k + 1).
        var legClear = new double[pts.Count - 1];
        for (int k = 0; k + 1 < pts.Count; k++) legClear[k] = LegLeast(geo, pts[k], pts[k + 1], wall, out _, out _, out _);

        // The rooms each leg as laid passes through (a room can be crossed with no point in it), by
        // either way of judging it: the straightened walk must cross each of them by both.
        var legRooms = new HashSet<int>[pts.Count - 1];
        for (int k = 0; k + 1 < pts.Count; k++) legRooms[k] = RoomsAlong(pts[k], pts[k + 1], NearCells(cells, k, k + 1, graph), geo, rooms, graph, both: false);

        var keep = new List<NavPoint>(pts.Count) { pts[0] };
        int i = 0, removed = 0;
        while (i < pts.Count - 1)
        {
            int best = i + 1;
            double least = legClear[i];
            for (int j = i + 2; j < pts.Count && j <= i + StraightenLookAhead; j++)
            {
                if (!NavRouteParser.IsPlainWaypoint(pts[j - 1].Type)) break;   // a special point: never skipped
                if (keepSet != null && keepSet.Contains(pts[j - 1])) break;     // a room middle the explore walks into
                least = Math.Min(least, legClear[j - 1]);
                if (CanStraighten(pts, cells, legRooms, i, j, graph, geo, rooms, Math.Max(PortalLegWalkMin, Math.Min(wall, least)), wall))
                    best = j;
            }
            removed += best - i - 1;
            keep.Add(pts[best]);
            i = best;
        }
        if (removed > 0)
        {
            pts.Clear();
            pts.AddRange(keep);
        }
        return removed;
    }

    // The straight leg from point i to point j: clear enough, and through every room the legs it
    // replaces passed through (the rooms of the points between included).
    private static bool CanStraighten(List<NavPoint> pts, uint[] cells, HashSet<int>[] legRooms, int i, int j,
                                      Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, DungeonRooms rooms,
                                      double needClear, double wall)
    {
        if (LegLeast(geo, pts[i], pts[j], wall, out _, out _, out _) < needClear - 0.01) return false;
        if (!LegOnFloor(geo, pts[i], pts[j])) return false;
        var must = new HashSet<int>();
        for (int k = i; k < j; k++) must.UnionWith(legRooms[k]);
        for (int k = i + 1; k < j; k++)
        {
            if (cells[k] == 0 || !rooms.RoomOf.TryGetValue(cells[k], out int r)) return false;
            must.Add(r);
        }
        if (must.Count == 0) return true;
        var along = RoomsAlong(pts[i], pts[j], NearCells(cells, i, j, graph), geo, rooms, graph, both: true);
        must.ExceptWith(along);
        return must.Count == 0;
    }

    // The cells of points from..to and their neighbours: where a leg among them can be.
    private static HashSet<uint> NearCells(uint[] cells, int from, int to, Dictionary<uint, DungeonNavNode> graph)
    {
        var near = new HashSet<uint>();
        for (int k = from; k <= to; k++)
        {
            uint c = cells[k];
            if (c == 0) continue;
            near.Add(c);
            if (!graph.TryGetValue(c, out var n)) continue;
            foreach (uint nb in n.Neighbors)
            {
                near.Add(nb);
                if (graph.TryGetValue(nb, out var m)) foreach (uint nb2 in m.Neighbors) near.Add(nb2);
            }
        }
        return near;
    }

    // The rooms the straight walk a-b passes through (sampled every StraightenSampleMeters), judged two
    // ways: following the floor, and by the cell at the leg's straight height (CellAt, as the explore's
    // tests do). On a ramp or a stair room the two can differ. both: crossed by both (what a new leg
    // must manage); else by either (what a leg as laid is taken to cross).
    private static HashSet<int> RoomsAlong(NavPoint a, NavPoint b, HashSet<uint> near, DungeonGeometry geo, DungeonRooms rooms,
                                           Dictionary<uint, DungeonNavNode> graph, bool both)
    {
        var floor = RoomsAlongFloor(a, b, near, geo, rooms);
        var flat = new HashSet<int>();
        double len = Math.Sqrt(Math.Pow(DungeonGeometry.NavToWorld(b.EW) - DungeonGeometry.NavToWorld(a.EW), 2)
                             + Math.Pow(DungeonGeometry.NavToWorld(b.NS) - DungeonGeometry.NavToWorld(a.NS), 2));
        int n = Math.Max(1, (int)Math.Ceiling(len / StraightenSampleMeters));
        for (int k = 0; k <= n; k++)
        {
            double t = (double)k / n;
            var q = new NavPoint { NS = a.NS + (b.NS - a.NS) * t, EW = a.EW + (b.EW - a.EW) * t, Z = a.Z + (b.Z - a.Z) * t };
            uint c = CellAt(graph, geo, q);
            if (c != 0 && rooms.RoomOf.TryGetValue(c, out int r)) flat.Add(r);
        }
        if (both) floor.IntersectWith(flat); else floor.UnionWith(flat);
        return floor;
    }

    private static HashSet<int> RoomsAlongFloor(NavPoint a, NavPoint b, HashSet<uint> near, DungeonGeometry geo, DungeonRooms rooms)
    {
        var found = new HashSet<int>();
        double x0 = DungeonGeometry.NavToWorld(a.EW), y0 = DungeonGeometry.NavToWorld(a.NS), z0 = a.Z * 240.0;
        double x1 = DungeonGeometry.NavToWorld(b.EW), y1 = DungeonGeometry.NavToWorld(b.NS), z1 = b.Z * 240.0;
        double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        int steps = Math.Max(1, (int)Math.Ceiling(len / StraightenSampleMeters));
        double zf = z0;
        for (int s = 0; s <= steps; s++)
        {
            double t = (double)s / steps, x = x0 + (x1 - x0) * t, y = y0 + (y1 - y0) * t, zl = z0 + (z1 - z0) * t;
            if (geo.TryFloorHeight(x, y, zf, 0.6, out double h)) zf = h;
            else if (geo.TryFloorHeight(x, y, zl, 0.6, out h)) zf = h;
            else zf = zl;
            foreach (uint c in near)
            {
                if (!geo.OnCellFloor(c, x, y, zf)) continue;
                if (rooms.RoomOf.TryGetValue(c, out int r)) found.Add(r);
                break;
            }
        }
        return found;
    }
}

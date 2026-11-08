// DungeonPathfinder — cell-graph builder + A* for autonomous dungeon navigation.
//
// ADJACENCY MODEL — uses portal records, not visible-cell lists.
//
// The EnvCell header has two adjacency fields:
//   • numVisibleCells / visible-cell list  — cells visible for LOD rendering. This includes
//     cells reachable through multiple doorways (A can see C through B). Using this for
//     pathfinding creates diagonal edges that cut through walls.
//   • numPortals / portal records           — cells sharing a physical doorway with this cell.
//     This is the correct walking adjacency: every edge is a real door opening.
//
// WAYPOINTS — portal midpoints, not cell centres.
//
// Navigating straight from cell-A centre to cell-B centre can cut through a corner if the
// doorway is offset from the line between centres. Instead we navigate to the midpoint of
// the two cell centres — which approximates the doorway position on the cell boundary —
// then continue to the next portal midpoint, and finally to the user's exact destination.
//
// Pipeline:
//   1. BuildGraph(landblockKey, cellDat)
//        → parse every EnvCell header, read portal records for true walking adjacency
//        → return Dictionary<cellId, DungeonNavNode>
//   2. NearestCell(graph, ns, ew)   → snap a position to the nearest node
//   3. FindPath(start, goal, graph) → A*, returns ordered List<cellId>
//   4. BuildNavRoute(path, graph, destNS, destEW)
//        → portal midpoints as waypoints + exact destination at the end
//   5. Set settings.CurrentRoute = route; settings.EnableNavigation = true;

using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

internal sealed class DungeonNavNode
{
    public uint       CellId;
    public double     NS, EW;
    public float      Z;
    public List<uint> Neighbors = new(); // portal-connected cells only
}

internal static partial class DungeonPathfinder
{
    // Angle threshold (degrees) above which a portal edge is classified as a drop.
    // A slope has significant horizontal movement relative to its Z change — its
    // centre-to-centre angle is well below 45°. A drop is near-vertical (the two
    // cell centres are almost directly above each other) so the angle approaches 90°.
    // 45° = equal horizontal and vertical movement. Ramps are typically 10-25°,
    // a direct floor-shaft drop is 60-90°.
    private const double DropAngleDeg = 45.0;

    // Returns true when the portal edge between a and b is a drop the bot cannot
    // traverse by walking. Uses a slope-angle calculation:
    //   - Convert NS/EW (nav coords) to raw world units (×240) so they share the
    //     same scale as Z.
    //   - atan2(|dZ|, dHoriz) > DropAngleDeg → drop.
    // Near-zero horizontal distance (rooms stacked vertically) is always a drop.
    internal static bool IsDropEdge(DungeonNavNode a, DungeonNavNode b)
    {
        double dZ = Math.Abs(b.Z - a.Z);
        if (dZ < 0.5) return false; // trivially flat

        double dNS = (b.NS - a.NS) * 240.0;
        double dEW = (b.EW - a.EW) * 240.0;
        double dHoriz = Math.Sqrt(dNS * dNS + dEW * dEW);

        if (dHoriz < 1.0) return true; // near-vertical shaft
        double angleDeg = Math.Atan2(dZ, dHoriz) * (180.0 / Math.PI);
        return angleDeg > DropAngleDeg;
    }

    private static uint _cachedLandblockKey;
    private static Dictionary<uint, DungeonNavNode>? _cachedGraph;

    /// <summary>Returns the cached graph for the landblock, rebuilding if stale.</summary>
    public static Dictionary<uint, DungeonNavNode> GetGraph(uint landblockKey, DatDatabase cellDat)
    {
        if (_cachedLandblockKey == landblockKey && _cachedGraph != null)
            return _cachedGraph;
        _cachedGraph        = BuildGraph(landblockKey, cellDat);
        _cachedLandblockKey = landblockKey;
        return _cachedGraph;
    }

    /// <summary>Another landblock's graph without touching the cache (the current dungeon's stays):
    /// /ra dunroute info on a route made elsewhere.</summary>
    internal static Dictionary<uint, DungeonNavNode> GraphOf(uint landblockKey, DatDatabase cellDat)
        => _cachedLandblockKey == landblockKey && _cachedGraph != null ? _cachedGraph : BuildGraph(landblockKey, cellDat);

    public static void InvalidateCache()
    {
        _cachedGraph        = null;
        _cachedLandblockKey = 0;
        _cachedGeometry     = null;
        _geoLandblock       = 0;
    }

    // ── Graph construction ──────────────────────────────────────────────────

    /// <summary>
    /// Parses every EnvCell header and builds an adjacency graph from portal records.
    /// Only cells that share a physical doorway become neighbours — visible-cell lists
    /// are ignored to prevent diagonal shortcuts through walls.
    ///
    /// EnvCell binary layout:
    ///   uint  id, flags, dupId
    ///   byte  numSurfaces, byte numPortals, ushort numVisibleCells
    ///   ushort[numSurfaces]                  ← surface IDs, skipped
    ///   ushort envId, ushort cellStruct
    ///   float posX, posY, posZ               ← cell centre in landblock-local space
    ///   float rotW, rotX, rotY, rotZ
    ///   CellPortal[numPortals]:              ← each 8 bytes = 4 × ushort
    ///     ushort flags
    ///     ushort polygonId                   ← portal poly key in CellStruct (not needed here)
    ///     ushort otherCellId                 ← lower 16 bits of connected cell
    ///     ushort otherPortalId
    ///   ushort[numVisibleCells]              ← skipped
    /// </summary>
    internal static Dictionary<uint, DungeonNavNode> BuildGraph(uint landblockKey, DatDatabase cellDat)
    {
        var nodes = new Dictionary<uint, DungeonNavNode>();

        float gx = ((landblockKey >> 8) & 0xFF) * 192f;
        float gy =  (landblockKey        & 0xFF) * 192f;

        var cellIds = cellDat.GetLandblockCellIds(landblockKey);
        foreach (uint cellId in cellIds)
        {
            byte[]? data = cellDat.GetFileData(cellId);
            if (data == null || data.Length < 36) continue;

            try
            {
                using var ms     = new MemoryStream(data);
                using var reader = new BinaryReader(ms);

                reader.ReadUInt32(); // id
                reader.ReadUInt32(); // flags
                reader.ReadUInt32(); // dup id

                byte   numSurfaces     = reader.ReadByte();
                byte   numPortals      = reader.ReadByte();
                ushort numVisibleCells = reader.ReadUInt16();

                ms.Seek(numSurfaces * 2, SeekOrigin.Current); // skip surface IDs

                reader.ReadUInt16(); // envId
                reader.ReadUInt16(); // cellStructure

                float posX = reader.ReadSingle();
                float posY = reader.ReadSingle();
                float posZ = reader.ReadSingle();
                ms.Seek(16, SeekOrigin.Current); // skip rotation

                // Read portal records — these are the true walking adjacency.
                // Each portal record: flags(2) + polygonId(2) + otherCellId(2) + otherPortalId(2) = 8 bytes
                var neighbors = new List<uint>(numPortals);
                for (int i = 0; i < numPortals; i++)
                {
                    if (ms.Position + 8 > ms.Length) break;
                    reader.ReadUInt16(); // flags
                    reader.ReadUInt16(); // polygonId (portal poly key — not needed for nav)
                    ushort otherCellLo = reader.ReadUInt16();
                    reader.ReadUInt16(); // otherPortalId

                    // Only include EnvCells (lower word 0x0100–0xFFFD)
                    if (otherCellLo < 0x0100 || otherCellLo > 0xFFFD) continue;
                    uint nid = (landblockKey << 16) | otherCellLo;
                    if (nid != cellId)
                        neighbors.Add(nid);
                }

                // worldX = lbX × 192 + posX  →  EW = (worldX/24 − 1019.5) / 10
                double ew = ((posX + gx) / 24.0 - 1019.5) / 10.0;
                double ns = ((posY + gy) / 24.0 - 1019.5) / 10.0;

                nodes[cellId] = new DungeonNavNode
                {
                    CellId    = cellId,
                    NS        = ns,
                    EW        = ew,
                    Z         = posZ,
                    Neighbors = neighbors
                };
            }
            catch { /* malformed cell — skip */ }
        }

        return nodes;
    }

    // ── Query helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cell ID whose centre is closest to (ns, ew, z). Returns 0 if empty.
    /// For multi-level dungeons, z is the player's world Z used to prefer same-floor cells.
    /// Cells within 8 world units in Z (same floor band) are preferred; falls back to 2D
    /// nearest-only if the dungeon has no cells in that band (e.g. z unknown / single level).
    /// </summary>
    public static uint NearestCell(Dictionary<uint, DungeonNavNode> graph, double ns, double ew, float z = float.NaN)
    {
        const float zBand = 8f;
        uint   best2d   = 0;
        double best2dDist = double.MaxValue;
        uint   bestZ    = 0;
        double bestZDist = double.MaxValue;

        bool useZ = !float.IsNaN(z);

        foreach (var node in graph.Values)
        {
            double dNS = node.NS - ns;
            double dEW = node.EW - ew;
            double d   = dNS * dNS + dEW * dEW;

            if (d < best2dDist) { best2dDist = d; best2d = node.CellId; }

            if (useZ && Math.Abs(node.Z - z) <= zBand && d < bestZDist)
            {
                bestZDist = d; bestZ = node.CellId;
            }
        }

        return (useZ && bestZ != 0) ? bestZ : best2d;
    }

    /// <summary>
    /// Nearest non-hazard cell to evacuate to when the patrol start cell is itself a hazard
    /// (player standing in acid/lava). Prefers a directly-connected, walkable (non-drop) neighbor —
    /// guaranteed reachable through a doorway — and only falls back to the geometrically nearest
    /// safe cell anywhere when the player is surrounded (deep in a pool with no safe neighbor).
    /// Returns 0 if the graph has no non-hazard cell at all.
    /// </summary>
    public static uint NearestSafeCell(
        Dictionary<uint, DungeonNavNode> graph, uint fromCell,
        IReadOnlySet<uint> hazards, float z = float.NaN)
    {
        if (!graph.TryGetValue(fromCell, out var from)) return 0;

        // 1) Prefer a walkable non-hazard doorway neighbor (closest by distance).
        uint   bestNb     = 0;
        double bestNbDist = double.MaxValue;
        foreach (uint nb in from.Neighbors)
        {
            if (hazards != null && hazards.Contains(nb)) continue;
            if (!graph.TryGetValue(nb, out var nbNode)) continue;
            if (IsDropEdge(from, nbNode)) continue;
            double dN = nbNode.NS - from.NS, dE = nbNode.EW - from.EW;
            double d = dN * dN + dE * dE;
            if (d < bestNbDist) { bestNbDist = d; bestNb = nb; }
        }
        if (bestNb != 0) return bestNb;

        // 2) Fall back to the geometrically nearest safe cell anywhere (same-floor preferred).
        const float zBand = 8f;
        uint   best2d = 0; double best2dDist = double.MaxValue;
        uint   bestZ  = 0; double bestZDist  = double.MaxValue;
        bool   useZ   = !float.IsNaN(z);
        foreach (var node in graph.Values)
        {
            if (node.CellId == fromCell) continue;
            if (hazards != null && hazards.Contains(node.CellId)) continue;
            double dN = node.NS - from.NS, dE = node.EW - from.EW;
            double d = dN * dN + dE * dE;
            if (d < best2dDist) { best2dDist = d; best2d = node.CellId; }
            if (useZ && Math.Abs(node.Z - z) <= zBand && d < bestZDist) { bestZDist = d; bestZ = node.CellId; }
        }
        return (useZ && bestZ != 0) ? bestZ : best2d;
    }

    // ── A* ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the shortest portal-connected path from startCell to goalCell,
    /// skipping any edge classified as a drop by IsDropEdge and any neighbor cell
    /// in <paramref name="hazardCells"/> (lava / acid pools, etc.).
    /// Returns ordered List of cell IDs, or empty if unreachable.
    /// </summary>
    public static List<uint> FindPath(
        uint startCell, uint goalCell,
        Dictionary<uint, DungeonNavNode> graph,
        IReadOnlySet<uint>? hazardCells = null)
    {
        if (!graph.ContainsKey(startCell) || !graph.ContainsKey(goalCell))
            return new List<uint>();
        if (startCell == goalCell)
            return new List<uint> { startCell };

        var goalNode = graph[goalCell];
        var gScore   = new Dictionary<uint, double> { [startCell] = 0.0 };
        var cameFrom = new Dictionary<uint, uint>();
        var open     = new PriorityQueue<uint, double>();
        var closed   = new HashSet<uint>();

        open.Enqueue(startCell, H(graph[startCell], goalNode));

        while (open.Count > 0)
        {
            uint current = open.Dequeue();
            if (!closed.Add(current)) continue;
            if (current == goalCell) return Reconstruct(cameFrom, current);

            if (!graph.TryGetValue(current, out var curNode)) continue;
            double gCur = gScore.GetValueOrDefault(current, double.MaxValue);

            foreach (uint nbId in curNode.Neighbors)
            {
                if (closed.Contains(nbId)) continue;
                if (!graph.TryGetValue(nbId, out var nbNode)) continue;
                if (IsDropEdge(curNode, nbNode)) continue;
                // Goal cell is allowed even if marked hazardous (the player asked to go there).
                if (nbId != goalCell && hazardCells != null && hazardCells.Contains(nbId)) continue;

                double dNS   = curNode.NS - nbNode.NS;
                double dEW   = curNode.EW - nbNode.EW;
                double tentG = gCur + Math.Sqrt(dNS * dNS + dEW * dEW);

                if (tentG < gScore.GetValueOrDefault(nbId, double.MaxValue))
                {
                    cameFrom[nbId] = current;
                    gScore[nbId]   = tentG;
                    open.Enqueue(nbId, tentG + H(nbNode, goalNode));
                }
            }
        }

        return new List<uint>();
    }

    private static double H(DungeonNavNode a, DungeonNavNode b)
    {
        double dNS = a.NS - b.NS, dEW = a.EW - b.EW;
        return Math.Sqrt(dNS * dNS + dEW * dEW);
    }

    private static List<uint> Reconstruct(Dictionary<uint, uint> cameFrom, uint goal)
    {
        var path = new List<uint>();
        uint cur = goal;
        while (true) { path.Add(cur); if (!cameFrom.TryGetValue(cur, out cur)) break; }
        path.Reverse();
        return path;
    }

    // ── Route construction ───────────────────────────────────────────────────

    /// <summary>
    /// Converts a cell-path into a Once-type NavRouteParser.
    ///
    /// Waypoint sequence for path [A, B, C, D] navigating to (destNS, destEW):
    ///   midpoint(A,B)  → midpoint(B,C)  → midpoint(C,D)  → (destNS, destEW)
    ///
    /// Each midpoint approximates the portal position at the cell boundary.
    /// Navigating through doorway midpoints prevents the bot from steering
    /// into walls when doorways are offset from the cell-to-cell straight line.
    /// </summary>
    public static NavRouteParser BuildNavRoute(
        List<uint> cellPath,
        Dictionary<uint, DungeonNavNode> graph,
        double destNS, double destEW,
        DungeonGeometry? geo = null, IReadOnlySet<uint>? hazardCells = null,
        double startNS = double.NaN, double startEW = double.NaN, double startZ = 0, double destZ = 0)
    {
        var route = new NavRouteParser { RouteType = NavRouteType.Once };

        // With the dungeon map geometry: straight on through doorways, straight across rooms.
        if (geo != null && geo.DoorwayCount > 0 && cellPath.Count > 0 && graph.TryGetValue(cellPath[0], out var s0))
        {
            double sNs = double.IsNaN(startNS) ? s0.NS : startNS, sEw = double.IsNaN(startEW) ? s0.EW : startEW;
            route.Points.AddRange(BuildPathPoints(cellPath, graph, geo, hazardCells, sNs, sEw, startZ, destNS, destEW, destZ, out _));
            route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = destNS, EW = destEW, Z = destZ / 240.0 });
            return route;
        }

        for (int i = 0; i + 1 < cellPath.Count; i++)
        {
            if (!graph.TryGetValue(cellPath[i],     out var a)) continue;
            if (!graph.TryGetValue(cellPath[i + 1], out var b)) continue;
            AddPortalWaypoints(route, a, b);
        }

        // Final destination — the user's exact target, not just a cell centre.
        route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = destNS, EW = destEW });

        SimplifyRoute(route);
        return route;
    }

    // Adds a pre-approach waypoint (30% into the transition from A) followed by the
    // portal midpoint (50%). The pre-approach aligns the bot perpendicular to the
    // doorway before it enters, preventing it from clipping sloped or narrow entrances.
    private static void AddPortalWaypoints(NavRouteParser route, DungeonNavNode a, DungeonNavNode b)
    {
        double dNS  = b.NS - a.NS, dEW = b.EW - a.EW;
        double dZ   = b.Z  - a.Z;
        // DungeonNavNode.Z is in raw AC units; NavPoint.Z must be in nav-format
        // (raw / 240) so that RouteHeightToWorldY(navZ) = navZ * 240 = rawZ.
        double navZA = a.Z / 240.0;

        route.Points.Add(new NavPoint
        {
            Type = NavPointType.Point,
            NS   = a.NS  + dNS  * 0.3,
            EW   = a.EW  + dEW  * 0.3,
            Z    = navZA + (dZ  / 240.0) * 0.3,
        });
        route.Points.Add(new NavPoint
        {
            Type = NavPointType.Point,
            NS   = a.NS  + dNS  * 0.5,
            EW   = a.EW  + dEW  * 0.5,
            Z    = navZA + (dZ  / 240.0) * 0.5,
        });
    }

    // Removes intermediate Point waypoints that lie within thresholdYards of the
    // straight line through their neighbours. Runs repeatedly until stable.
    // Control points (Pause/Chat/Recall/PortalNPC) are always preserved.
    private static void SimplifyRoute(NavRouteParser route, double thresholdYards = 1.5)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 1; i + 1 < route.Points.Count; i++)
            {
                if (!NavRouteParser.IsPlainWaypoint(route.Points[i].Type)) continue;
                if (SegDistYards(route.Points[i - 1], route.Points[i + 1], route.Points[i]) < thresholdYards)
                {
                    route.Points.RemoveAt(i--);
                    changed = true;
                }
            }
        }
    }

    // Perpendicular distance (yards) from p to the line through a and b.
    private static double SegDistYards(NavPoint a, NavPoint b, NavPoint p)
    {
        double acNS = b.NS - a.NS, acEW = b.EW - a.EW;
        double len  = Math.Sqrt(acNS * acNS + acEW * acEW);
        if (len < 1e-9)
            return Math.Sqrt((p.NS - a.NS) * (p.NS - a.NS) + (p.EW - a.EW) * (p.EW - a.EW)) * 240.0;
        double abNS = p.NS - a.NS, abEW = p.EW - a.EW;
        return Math.Abs(abNS * acEW - abEW * acNS) / len * 240.0;
    }

    // ── Patrol route ────────────────────────────────────────────────────────

    // Safety valves: if convergence pruning collapses the graph too far (e.g. a
    // linear dungeon with no cycles), fall back to the full reachable set.
    private const int    PatrolMinKeepNodes    = 8;
    private const double PatrolMinKeepFraction = 0.20;

    /// <summary>
    /// The cells the patrol walks, by <see cref="DungeonPatrolOptions.Coverage"/>:
    ///   Whole dungeon (default): every cell reachable from <paramref name="startCell"/> without
    ///     drops or hazards, less the dead-end areas of up to MaxDeadEndCells cells in all (closets,
    ///     a short corridor ending in a small room; <see cref="TrimDeadEndPockets"/>). Longer dead
    ///     ends are walked in until only that much is left ahead (a big room: to it), and back.
    ///   Loops only: the 2-core (cells on a loop or between two junctions), as before 2026-10-12:
    ///     every dead-end branch goes, however long. In a hive (0x6146 Matron Hive East: 524
    ///     reachable, 2-core 171) that leaves two thirds of the dungeon unpatrolled. Where the
    ///     hazards cut the loops (the 2-core collapses but the dungeon isn't linear), the
    ///     reachable cells less the small dead-end areas, as Whole dungeon.
    ///   Everything: every reachable cell.
    ///
    /// A linear dungeon (its hazard-free 2-core under PatrolMinKeepNodes or 20% of reachable)
    /// keeps every reachable cell, spurs included, whatever the coverage: there the spurs are
    /// the dungeon.
    ///
    /// <paramref name="rooms"/> (BuildRooms with the same hazards, when the map geometry is
    /// there): dead-end areas are found room by room rather than cell by cell.
    /// </summary>
    internal static HashSet<uint> GetMainRouteNodes(
        Dictionary<uint, DungeonNavNode> graph, uint startCell,
        IReadOnlySet<uint>? hazardCells = null, DungeonPatrolOptions? options = null, DungeonRooms? rooms = null)
    {
        var o = options ?? DungeonPatrolOptions.Default;
        var (reachable, pruned) = ReachableAndCore(graph, startCell, hazardCells);
        int originalCount = reachable.Count;

        // Linear is judged on the dungeon itself, not on what the hazards left (2026-10-10, 0x6146:
        // 3 acid cells + 35 lava-floor cells cut the loops, 2-core 277 -> 68).
        bool collapsed = pruned.Count < PatrolMinKeepNodes || pruned.Count < originalCount * PatrolMinKeepFraction;
        bool linear = collapsed;
        if (linear && hazardCells != null && hazardCells.Count > 0)
        {
            var (freeReach, freeCore) = ReachableAndCore(graph, startCell, null);
            linear = freeCore.Count < PatrolMinKeepNodes || freeCore.Count < freeReach.Count * PatrolMinKeepFraction;
        }

        HashSet<uint> result;
        string how;
        if (o.Coverage == PatrolCoverage.Everything)
        {
            result = reachable;
            how = $"everything: all {originalCount} reachable cells, dead ends included";
        }
        else if (linear)
        {
            result = reachable;
            how = $"linear: patrol uses all {originalCount} reachable cells, dead ends included";
        }
        else if (o.Coverage == PatrolCoverage.LoopsOnly && !collapsed)
        {
            result = pruned;
            how = $"loops only: the 2-core ({pruned.Count} cells)";
        }
        else
        {
            result = TrimDeadEndPockets(graph, reachable, startCell, o.MaxDeadEndCells, rooms, o.LookInRoomMeters);
            how = (o.Coverage == PatrolCoverage.LoopsOnly ? "loops only, but hazards cut the loops: "
                   : o.Coverage == PatrolCoverage.Sweep ? "sweep, over " : "")
                + $"all reachable cells but dead-end areas of <= {o.MaxDeadEndCells} cells{(rooms != null ? " (by room)" : "")} ({result.Count} kept)";
        }
        if (PointLog != null)
            MainRouteNote = $"main route from {startCell:X8}: {originalCount} reachable cells, 2-core {pruned.Count} "
                          + $"({hazardCells?.Count ?? 0} hazard cells) -> " + how + " [" + o.Describe() + "]";
        return result;
    }

    /// <summary>Dead-end areas this small (all their cells, from where they leave the rest of the
    /// dungeon) are left out of the patrol by default (Settings: Skip Dead Ends Up To).</summary>
    internal const int PatrolMaxDeadEndCells = DungeonPatrolOptions.MaxDeadEndCellsDefault;

    /// <summary>
    /// The hazard cells the patrol keeps out of: <paramref name="all"/> (the live set: floor
    /// textures, pools sighted, cells marked by hand), less the lava/acid floor cells
    /// (<paramref name="floorCells"/>) when <see cref="DungeonPatrolOptions.AvoidHazardFloors"/> is
    /// off. A floor cell that is also in <paramref name="markedCells"/> (sighted or marked by hand,
    /// DungeonHazardStore) stays avoided. Null when nothing is left.
    /// </summary>
    internal static IReadOnlySet<uint>? PatrolHazards(IReadOnlySet<uint>? all, IEnumerable<uint>? floorCells,
                                                      IReadOnlySet<uint>? markedCells, DungeonPatrolOptions? options)
    {
        if (all == null || all.Count == 0) return all;
        if ((options ?? DungeonPatrolOptions.Default).AvoidHazardFloors || floorCells == null) return all;
        var result = new HashSet<uint>(all);
        foreach (uint c in floorCells)
            if (markedCells == null || !markedCells.Contains(c)) result.Remove(c);
        return result;
    }

    /// <summary>
    /// <paramref name="cells"/> without every dead-end area of at most <paramref name="maxCells"/>
    /// cells. A dead-end area (a pocket) is everything that hangs off the rest by one cell or
    /// room: a closet, a corridor with a small room at its end, a little loop off a corridor. It
    /// is measured whole, so a short corridor ending in a 3-5 cell room goes (2026-10-13: the spur
    /// walk this replaces stopped at the room, whose cells join each other, and kept it; raising
    /// its limit changed nothing in 0x6146).
    ///
    /// Pockets nest, and every one that small goes wherever it is: the patrol never walks into a
    /// dead end of maxCells cells or fewer. A long branch is walked (and the small pockets off it
    /// are not); where what is left of it ahead is that short, the patrol turns round. A branch to
    /// a big room keeps it: a room spreading over <paramref name="bigRoomMeters"/> or more (the
    /// look-in size) counts as too big to skip, so a pocket holding one is never dropped.
    ///
    /// With <paramref name="rooms"/> (the map geometry is there) the pockets are found on the room
    /// graph, a room's cells counted together: entering a room at all is entering it, and a
    /// room's own corners (a star-shaped junction's arms) are not pockets of their own. Without,
    /// cell by cell (and no room is big).
    ///
    /// Rooted at <paramref name="keep"/> (the start): the side the start is on is never a pocket,
    /// so a start in a closet keeps the closet and walks out of it. Everything hangs off the start
    /// in that sense, so of what hangs off it the biggest part (the rest of the dungeon, or all
    /// of it when the start is on a loop) always stays, however small the dungeon.
    /// </summary>
    internal static HashSet<uint> TrimDeadEndPockets(Dictionary<uint, DungeonNavNode> graph, HashSet<uint> cells, uint keep,
                                                     int maxCells, DungeonRooms? rooms = null,
                                                     double bigRoomMeters = DungeonPatrolOptions.LookInRoomDefault)
    {
        var result = new HashSet<uint>(cells);
        if (maxCells <= 0 || cells.Count == 0) return result;
        if (rooms == null || !rooms.RoomOf.TryGetValue(keep, out int keepRoom))
        {
            result.ExceptWith(DroppedPockets(FindDeadEndPockets(CellAdjacency(graph, cells), _ => 1, keep), maxCells));
            return result;
        }

        var (adj, weight) = RoomAdjacency(graph, cells, rooms);
        int Weight(uint r) => RoomSpreadMeters(graph, rooms.Cells[(int)r]) >= bigRoomMeters ? maxCells + 1 : weight[r];
        var droppedRooms = DroppedPockets(FindDeadEndPockets(adj, Weight, (uint)keepRoom), maxCells);
        foreach (uint c in cells)
            if (rooms.RoomOf.TryGetValue(c, out int r) && droppedRooms.Contains((uint)r)) result.Remove(c);
        return result;
    }

    /// <summary>The nodes of every pocket of at most <paramref name="maxSize"/>, but the biggest
    /// hanging off each root of the walk (the dungeon itself, seen from the start).</summary>
    private static HashSet<uint> DroppedPockets(DeadEndPockets p, int maxSize)
    {
        var main = new HashSet<uint>();
        foreach (uint root in p.Roots)
        {
            DeadEndPocket? best = null;
            foreach (var q in p.All)
                if (q.Attach == root && (best == null || q.Size > best.Value.Size)) best = q;
            if (best != null) main.Add(best.Value.Root);
        }
        var dropped = new HashSet<uint>();
        foreach (var q in p.All)
            if (q.Size <= maxSize && !main.Contains(q.Root) && !dropped.Contains(q.Root))
                foreach (uint c in p.NodesOf(q)) dropped.Add(c);
        return dropped;
    }

    /// <summary>The room graph of <paramref name="cells"/>: a node per room (its index) with cells in
    /// the set, weighted by those cells, joined where two of their cells are walkable neighbours.</summary>
    internal static (Dictionary<uint, List<uint>> Adj, Dictionary<uint, int> Weight) RoomAdjacency(
        Dictionary<uint, DungeonNavNode> graph, HashSet<uint> cells, DungeonRooms rooms)
    {
        var weight = new Dictionary<uint, int>();
        foreach (uint c in cells)
            if (rooms.RoomOf.TryGetValue(c, out int r)) weight[(uint)r] = weight.GetValueOrDefault((uint)r) + 1;
        var adj = new Dictionary<uint, List<uint>>(weight.Count);
        foreach (uint r in weight.Keys) adj[r] = new List<uint>();
        foreach (var (c, nbs) in CellAdjacency(graph, cells))
        {
            if (!rooms.RoomOf.TryGetValue(c, out int ra)) continue;
            foreach (uint nb in nbs)
                if (rooms.RoomOf.TryGetValue(nb, out int rb) && ra != rb && !adj[(uint)ra].Contains((uint)rb))
                    adj[(uint)ra].Add((uint)rb);
        }
        return (adj, weight);
    }

    /// <summary>The walkable neighbours (no drops) of each cell inside <paramref name="cells"/>, each once.</summary>
    private static Dictionary<uint, List<uint>> CellAdjacency(Dictionary<uint, DungeonNavNode> graph, HashSet<uint> cells)
    {
        var adj = new Dictionary<uint, List<uint>>(cells.Count);
        foreach (uint c in cells)
        {
            var list = new List<uint>();
            if (graph.TryGetValue(c, out var n))
                foreach (uint nb in n.Neighbors)
                    if (nb != c && cells.Contains(nb) && graph.TryGetValue(nb, out var o) && !IsDropEdge(n, o) && !list.Contains(nb))
                        list.Add(nb);
            adj[c] = list;
        }
        return adj;
    }

    /// <summary>A dead-end area: <see cref="Root"/> and everything behind it hang off the rest by
    /// <see cref="Attach"/> alone. <see cref="Size"/> is its weight (cells), Attach not counted.</summary>
    internal readonly record struct DeadEndPocket(uint Attach, uint Root, int Size);

    /// <summary>Every dead-end area of a graph, nested ones included (<see cref="FindDeadEndPockets"/>).</summary>
    internal sealed class DeadEndPockets
    {
        internal readonly List<uint> Order = new();
        internal readonly Dictionary<uint, int> Disc = new();
        internal readonly Dictionary<uint, int> Count = new();
        /// <summary>Every pocket, in depth-first order of their roots (outer ones first).</summary>
        public readonly List<DeadEndPocket> All = new();
        /// <summary>Where the walk started: keep, then a node of each part not connected to it.</summary>
        public readonly List<uint> Roots = new();

        /// <summary>The pocket's nodes: its root's subtree, a run of the depth-first order.</summary>
        public IEnumerable<uint> NodesOf(DeadEndPocket p)
        {
            for (int k = Disc[p.Root], end = k + Count[p.Root]; k < end; k++) yield return Order[k];
        }
    }

    /// <summary>
    /// The dead-end areas of a graph seen from <paramref name="keep"/>: a depth-first walk from it
    /// (Tarjan's cut vertices), where a child w of v whose subtree has no way round v (low[w] >=
    /// disc[v]) is a pocket hanging off v, weighing the sum of its nodes' weights. Nodes not
    /// connected to keep are walked from the lowest id up.
    /// </summary>
    internal static DeadEndPockets FindDeadEndPockets(Dictionary<uint, List<uint>> adj, Func<uint, int> weight, uint keep)
    {
        var r = new DeadEndPockets();
        var disc = r.Disc;
        var count = r.Count;
        var order = r.Order;
        var size = new Dictionary<uint, int>(adj.Count);
        var low = new Dictionary<uint, int>(adj.Count);
        var parent = new Dictionary<uint, uint>(adj.Count);
        var roots = new List<uint>();
        if (adj.ContainsKey(keep)) roots.Add(keep);
        var rest = new List<uint>(adj.Keys);
        rest.Sort();
        roots.AddRange(rest);
        var stack = new Stack<(uint Node, int Next)>();
        foreach (uint root in roots)
        {
            if (disc.ContainsKey(root)) continue;
            r.Roots.Add(root);
            disc[root] = low[root] = order.Count; order.Add(root); count[root] = 1; size[root] = weight(root);
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (v, i) = stack.Pop();
                var nbs = adj[v];
                if (i < nbs.Count)
                {
                    stack.Push((v, i + 1));
                    uint w = nbs[i];
                    if (!adj.ContainsKey(w)) continue;
                    if (!disc.ContainsKey(w))
                    {
                        parent[w] = v;
                        disc[w] = low[w] = order.Count; order.Add(w); count[w] = 1; size[w] = weight(w);
                        stack.Push((w, 0));
                    }
                    else if (!(parent.TryGetValue(v, out uint pv) && pv == w))
                        low[v] = Math.Min(low[v], disc[w]);
                    continue;
                }
                // v finished: hand its low-link, node count and weight up to its parent.
                if (parent.TryGetValue(v, out uint p))
                {
                    low[p] = Math.Min(low[p], low[v]);
                    count[p] += count[v];
                    size[p] += size[v];
                }
            }
        }

        foreach (var (w, v) in parent)
            if (low[w] >= disc[v]) r.All.Add(new DeadEndPocket(v, w, size[w]));
        r.All.Sort((a, b) => disc[a.Root].CompareTo(disc[b.Root]));
        return r;
    }

    /// <summary>The cells reachable from startCell (no drops, no hazards) and their 2-core.</summary>
    private static (HashSet<uint> Reachable, HashSet<uint> Core) ReachableAndCore(
        Dictionary<uint, DungeonNavNode> graph, uint startCell, IReadOnlySet<uint>? hazardCells)
    {
        // BFS to collect all cells reachable from startCell without drops or hazards.
        // The start cell is always kept (player is already standing there) even if
        // it's somehow marked hazardous — we can't refuse to start the patrol.
        var reachable = new HashSet<uint>();
        var queue     = new Queue<uint>();
        if (graph.ContainsKey(startCell))
        {
            queue.Enqueue(startCell);
            reachable.Add(startCell);
            while (queue.Count > 0)
            {
                uint cur = queue.Dequeue();
                if (!graph.TryGetValue(cur, out var node)) continue;
                foreach (uint nb in node.Neighbors)
                {
                    if (reachable.Contains(nb)) continue;
                    if (!graph.ContainsKey(nb)) continue;
                    if (IsDropEdge(node, graph[nb])) continue;
                    if (hazardCells != null && hazardCells.Contains(nb)) continue;
                    reachable.Add(nb);
                    queue.Enqueue(nb);
                }
            }
        }

        var pruned = new HashSet<uint>(reachable);

        // Prune to convergence: repeatedly remove every node whose effective
        // degree within `pruned` is ≤ 1. Each pass peels one cell from every
        // branch tip; iterating until no changes remain strips dead-end spurs
        // of any depth, leaving only the 2-core (cycles + through-corridors).
        while (true)
        {
            var toRemove = new List<uint>();
            foreach (uint id in pruned)
            {
                if (id == startCell) continue; // always keep spawn cell
                if (!graph.TryGetValue(id, out var node)) continue;
                int deg = 0;
                foreach (uint nb in node.Neighbors)
                {
                    if (pruned.Contains(nb) && graph.ContainsKey(nb) &&
                        !IsDropEdge(node, graph[nb]))
                    {
                        deg++;
                        if (deg > 1) break;
                    }
                }
                if (deg <= 1) toRemove.Add(id);
            }

            if (toRemove.Count == 0) break;
            foreach (uint id in toRemove) pruned.Remove(id);
        }

        return (reachable, pruned);
    }

    /// <summary>
    /// Generates a Circular patrol route over the dungeon's main route (the cells from
    /// <see cref="GetMainRouteNodes"/>).
    ///
    /// Uses a route-inspection (Chinese-postman-style) closed walk rather than a DFS tree
    /// traversal: it covers every corridor once and — crucially — TAKES loop-closing edges,
    /// so a cycle is walked once and the bot continues forward instead of running to the end
    /// of a loop and backtracking the way it came. Only true dead-ends and bridges are
    /// re-tread (the unavoidable in-and-out), via the shortest reconnecting path. The walk is
    /// closed back to the start so the Circular route returns home.
    ///
    /// <paramref name="portals"/> (the landblock's live portals): the patrol keeps out of their
    /// circles, turning round before a portal it can't get past (DungeonPathfinder.Portals.cs;
    /// what it did about each: <see cref="LastPortalAvoidance"/>). <paramref name="startAt"/>
    /// (world): where the character stands, so a portal that splits its room leaves it the part it
    /// is in.
    /// </summary>
    public static NavRouteParser BuildPatrolRoute(
        Dictionary<uint, DungeonNavNode> graph,
        uint startCell,
        IReadOnlySet<uint>? hazardCells = null,
        DungeonGeometry? geo = null,
        DungeonPatrolOptions? options = null,
        IReadOnlyList<PatrolPortal>? portals = null,
        (double X, double Y, double Z)? startAt = null)
    {
        LastPortalAvoidance = null;
        var o = (options ?? DungeonPatrolOptions.Default).Clamped();
        if (portals is { Count: > 0 }) return BuildPatrolRouteAroundPortals(graph, startCell, hazardCells, geo, o, portals, startAt);
        // Dead ends are judged room by room when the rooms can be made (the room patrol below).
        var rooms = geo != null && geo.DoorwayCount > 0 ? BuildRooms(graph, geo, hazardCells) : null;
        var mainRoute = GetMainRouteNodes(graph, startCell, hazardCells, o, rooms);

        // Sweep (DungeonPathfinder.Sweep.cs): the same cells, walked only near enough to be seen.
        // Falls through to the room patrol below when it can't build one. No other coverage comes here.
        if (o.Coverage == PatrolCoverage.Sweep && geo != null && geo.DoorwayCount > 0)
        {
            var sweep = BuildSweepPatrol(graph, startCell, mainRoute, hazardCells, geo, o);
            if (sweep != null && sweep.Points.Count > 0) return sweep;
        }

        // With the dungeon map geometry: the same coverage over rooms instead of cells, rooms
        // crossed directly, doorways taken straight on (DungeonPathfinder.Rooms.cs).
        if (geo != null && geo.DoorwayCount > 0)
        {
            var byRoom = BuildRoomPatrol(graph, startCell, mainRoute, hazardCells, geo, o);
            if (byRoom != null && byRoom.Points.Count > 0) return byRoom;
        }

        // Undirected walkable subgraph over mainRoute (portal-adjacent, non-drop). mainRoute is
        // already hazard-filtered, so membership there implies non-hazard.
        var adj   = new Dictionary<uint, List<uint>>();
        var edges = new HashSet<ulong>();
        foreach (uint c in mainRoute)
        {
            if (!graph.TryGetValue(c, out var cn)) continue;
            foreach (uint nb in cn.Neighbors)
            {
                if (!mainRoute.Contains(nb) || !graph.TryGetValue(nb, out var nbn)) continue;
                if (IsDropEdge(cn, nbn)) continue;
                if (!adj.TryGetValue(c, out var list)) { list = new List<uint>(); adj[c] = list; }
                list.Add(nb);
                edges.Add(EdgeKey(c, nb));
            }
        }

        var route = new NavRouteParser { RouteType = NavRouteType.Circular };

        // Euler start: the player's cell if it borders a corridor, else the nearest cell that does.
        uint walkStart = (adj.TryGetValue(startCell, out var sList) && sList.Count > 0)
            ? startCell
            : NearestCellWithEdges(graph, adj, startCell);
        if (walkStart == 0 || edges.Count == 0)
        {
            SimplifyRoute(route);
            return route; // nothing walkable — caller's empty-route guard handles it
        }

        // Greedy edge-covering closed walk.
        var walk      = new List<uint> { walkStart };
        var remaining = new HashSet<ulong>(edges);
        uint cur      = walkStart;
        // Hard bound: every edge is consumed once and each exhausted vertex triggers at most one
        // reconnection (which is itself followed by an edge consumption), so this can't spin forever.
        int guard = edges.Count * 4 + mainRoute.Count + 16;

        while (remaining.Count > 0 && guard-- > 0)
        {
            uint next = 0; bool found = false;
            if (adj.TryGetValue(cur, out var nbrs))
            {
                foreach (uint nb in nbrs)
                    if (remaining.Contains(EdgeKey(cur, nb))) { next = nb; found = true; break; }
            }

            if (found)
            {
                remaining.Remove(EdgeKey(cur, next));
                walk.Add(next);
                cur = next;
            }
            else
            {
                // Out of fresh corridors here — hop (shortest path) to the nearest cell that still
                // has one. These re-tread edges are the unavoidable dead-end/bridge repeats.
                var path = BfsToUnusedEdge(adj, cur, remaining);
                if (path == null) break; // remaining edges unreachable (disconnected) — stop
                for (int i = 1; i < path.Count; i++) walk.Add(path[i]);
                cur = path[path.Count - 1];
            }
        }

        // Close the loop back to the start so the Circular route returns home.
        if (cur != walkStart)
        {
            var back = BfsPath(adj, cur, walkStart);
            if (back != null) for (int i = 1; i < back.Count; i++) walk.Add(back[i]);
        }

        for (int i = 0; i + 1 < walk.Count; i++)
        {
            if (!graph.TryGetValue(walk[i],     out var a)) continue;
            if (!graph.TryGetValue(walk[i + 1], out var b)) continue;
            AddPortalWaypoints(route, a, b);
        }

        SimplifyRoute(route);
        // The map geometry is there but the room patrol found nothing to walk (the start is cut
        // off from every doorway): the cell walk's points still keep off the walls.
        if (geo != null)
            foreach (var p in route.Points)
                PushOffWalls(geo, Tag(p, "cell-walk"), null, CornerPushMax, o.WallClearance);
        return route;
    }

    // Canonical undirected-edge key (order-independent).
    private static ulong EdgeKey(uint a, uint b)
        => a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

    // Nearest subgraph cell that has at least one corridor edge (for an Euler start when the player
    // stands on an edge-less cell, e.g. a stripped spur). 0 if the subgraph has no edges.
    private static uint NearestCellWithEdges(
        Dictionary<uint, DungeonNavNode> graph, Dictionary<uint, List<uint>> adj, uint fromCell)
    {
        if (!graph.TryGetValue(fromCell, out var from))
        {
            foreach (var kv in adj) if (kv.Value.Count > 0) return kv.Key;
            return 0;
        }
        uint best = 0; double bestD = double.MaxValue;
        foreach (var kv in adj)
        {
            if (kv.Value.Count == 0) continue;
            if (!graph.TryGetValue(kv.Key, out var n)) continue;
            double dN = n.NS - from.NS, dE = n.EW - from.EW;
            double d = dN * dN + dE * dE;
            if (d < bestD) { bestD = d; best = kv.Key; }
        }
        return best;
    }

    // Shortest hop-path src→dst over the subgraph (inclusive endpoints), or null if unreachable.
    private static List<uint>? BfsPath(Dictionary<uint, List<uint>> adj, uint src, uint dst)
    {
        if (src == dst) return new List<uint> { src };
        var prev = new Dictionary<uint, uint>();
        var seen = new HashSet<uint> { src };
        var q = new Queue<uint>();
        q.Enqueue(src);
        while (q.Count > 0)
        {
            uint c = q.Dequeue();
            if (!adj.TryGetValue(c, out var ns)) continue;
            foreach (uint nb in ns)
            {
                if (!seen.Add(nb)) continue;
                prev[nb] = c;
                if (nb == dst) return RebuildPath(prev, src, dst);
                q.Enqueue(nb);
            }
        }
        return null;
    }

    // Shortest hop-path from src to the nearest cell that still has an unused edge, or null.
    private static List<uint>? BfsToUnusedEdge(
        Dictionary<uint, List<uint>> adj, uint src, HashSet<ulong> remaining)
    {
        var prev = new Dictionary<uint, uint>();
        var seen = new HashSet<uint> { src };
        var q = new Queue<uint>();
        q.Enqueue(src);
        while (q.Count > 0)
        {
            uint c = q.Dequeue();
            if (c != src && HasUnusedEdge(adj, c, remaining))
                return RebuildPath(prev, src, c);
            if (!adj.TryGetValue(c, out var ns)) continue;
            foreach (uint nb in ns)
            {
                if (!seen.Add(nb)) continue;
                prev[nb] = c;
                q.Enqueue(nb);
            }
        }
        return null;
    }

    private static bool HasUnusedEdge(Dictionary<uint, List<uint>> adj, uint cell, HashSet<ulong> remaining)
    {
        if (!adj.TryGetValue(cell, out var ns)) return false;
        foreach (uint nb in ns) if (remaining.Contains(EdgeKey(cell, nb))) return true;
        return false;
    }

    private static List<uint> RebuildPath(Dictionary<uint, uint> prev, uint src, uint dst)
    {
        var path = new List<uint> { dst };
        uint c = dst;
        while (c != src && prev.TryGetValue(c, out uint p)) { path.Add(p); c = p; }
        path.Reverse();
        return path;
    }

    // ── Convenience entry point ──────────────────────────────────────────────

    /// <summary>
    /// One-shot: build graph, A* from player to dest, return a ready-to-run NavRouteParser.
    /// Returns null if graph is empty, player/dest cell not found, or no path exists.
    /// </summary>
    public static NavRouteParser? NavigateTo(
        uint landblockKey, DatDatabase cellDat,
        double playerNS, double playerEW, float playerZ,
        double destNS,   double destEW,
        out int nodeCount, out int pathLength,
        IReadOnlySet<uint>? hazardCells = null,
        DungeonLOS? los = null)
    {
        nodeCount  = 0;
        pathLength = 0;

        var graph = GetGraph(landblockKey, cellDat);
        nodeCount = graph.Count;
        if (nodeCount == 0) return null;

        // Use player Z for both start and goal — assumes destination is on the same floor.
        uint startCell = NearestCell(graph, playerNS, playerEW, playerZ);
        uint goalCell  = NearestCell(graph, destNS,   destEW,   playerZ);
        if (startCell == 0 || goalCell == 0) return null;

        // IsDropEdge filters out jumps — AC requires a jump to initiate a drop
        // and the navigation engine has no jump primitive yet.
        // If the destination is only reachable via a drop this returns null.
        var path = FindPath(startCell, goalCell, graph, hazardCells);
        pathLength = path.Count;
        if (pathLength == 0) return null;

        var geo = GetGeometry(landblockKey, cellDat, los);
        return BuildNavRoute(path, graph, destNS, destEW, geo, hazardCells, playerNS, playerEW, playerZ, playerZ);
    }
}

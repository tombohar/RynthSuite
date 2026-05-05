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

internal static class DungeonPathfinder
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
    private static bool IsDropEdge(DungeonNavNode a, DungeonNavNode b)
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

    public static void InvalidateCache()
    {
        _cachedGraph        = null;
        _cachedLandblockKey = 0;
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
    private static Dictionary<uint, DungeonNavNode> BuildGraph(uint landblockKey, DatDatabase cellDat)
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

    // ── A* ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the shortest portal-connected path from startCell to goalCell,
    /// skipping any edge classified as a drop by IsDropEdge.
    /// Returns ordered List of cell IDs, or empty if unreachable.
    /// </summary>
    public static List<uint> FindPath(
        uint startCell, uint goalCell,
        Dictionary<uint, DungeonNavNode> graph)
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
        double destNS, double destEW)
    {
        var route = new NavRouteParser { RouteType = NavRouteType.Once };

        for (int i = 0; i + 1 < cellPath.Count; i++)
        {
            if (!graph.TryGetValue(cellPath[i],     out var a)) continue;
            if (!graph.TryGetValue(cellPath[i + 1], out var b)) continue;
            AddPortalWaypoints(route, a, b);
        }

        // Final destination — the user's exact target, not just a cell centre.
        route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = destNS, EW = destEW });

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

    // ── Patrol route ────────────────────────────────────────────────────────

    // Safety valves: if convergence pruning collapses the graph too far (e.g. a
    // linear dungeon with no cycles), fall back to the full reachable set.
    private const int    PatrolMinKeepNodes    = 8;
    private const double PatrolMinKeepFraction = 0.20;

    /// <summary>
    /// Returns the 2-core of the dungeon's walkable subgraph — the subset of
    /// cells where every node has at least two walkable neighbours within the
    /// set. Dead-end spurs of any length are stripped by repeatedly removing
    /// leaf nodes (degree ≤ 1) until none remain. Only corridors that form
    /// part of a cycle or connect two junctions survive, which is exactly the
    /// "main route" a patrol bot should follow.
    ///
    /// Falls back to the full reachable set if the pruned graph would be too
    /// small to be useful (small or purely linear dungeons).
    /// </summary>
    private static HashSet<uint> GetMainRouteNodes(
        Dictionary<uint, DungeonNavNode> graph, uint startCell)
    {
        // BFS to collect all cells reachable from startCell without drops.
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
                    if (!reachable.Contains(nb) && graph.ContainsKey(nb) &&
                        !IsDropEdge(node, graph[nb]))
                    {
                        reachable.Add(nb);
                        queue.Enqueue(nb);
                    }
                }
            }
        }

        int originalCount = reachable.Count;
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

        // Fall back to full reachable set if pruning collapsed the graph.
        if (pruned.Count < PatrolMinKeepNodes ||
            pruned.Count < originalCount * PatrolMinKeepFraction)
            return reachable;

        return pruned;
    }

    /// <summary>
    /// Generates a Circular patrol route covering the dungeon's main route.
    /// Dead-end spurs shorter than <see cref="PatrolDeadEndPruneDepth"/> cells are
    /// skipped; only corridors and rooms that connect to the trunk are visited.
    /// </summary>
    public static NavRouteParser BuildPatrolRoute(
        Dictionary<uint, DungeonNavNode> graph,
        uint startCell)
    {
        var mainRoute = GetMainRouteNodes(graph, startCell);

        var visited  = new HashSet<uint>();
        var walkPath = new List<uint>();

        var stack = new Stack<(uint cell, int nbIdx)>();
        stack.Push((startCell, 0));
        visited.Add(startCell);
        walkPath.Add(startCell);

        while (stack.Count > 0)
        {
            var (cell, nbIdx) = stack.Peek();
            if (!graph.TryGetValue(cell, out var node))
            {
                stack.Pop();
                if (stack.Count > 0) walkPath.Add(stack.Peek().cell);
                continue;
            }

            int nextNb = nbIdx;
            while (nextNb < node.Neighbors.Count)
            {
                uint nb = node.Neighbors[nextNb];
                if (!visited.Contains(nb) && mainRoute.Contains(nb) &&
                    graph.ContainsKey(nb) && !IsDropEdge(node, graph[nb])) break;
                nextNb++;
            }

            if (nextNb >= node.Neighbors.Count)
            {
                stack.Pop();
                if (stack.Count > 0) walkPath.Add(stack.Peek().cell);
            }
            else
            {
                stack.Pop();
                stack.Push((cell, nextNb + 1));
                uint nb = node.Neighbors[nextNb];
                visited.Add(nb);
                walkPath.Add(nb);
                stack.Push((nb, 0));
            }
        }

        var route = new NavRouteParser { RouteType = NavRouteType.Circular };
        for (int i = 0; i + 1 < walkPath.Count; i++)
        {
            if (!graph.TryGetValue(walkPath[i],     out var a)) continue;
            if (!graph.TryGetValue(walkPath[i + 1], out var b)) continue;
            AddPortalWaypoints(route, a, b);
        }

        return route;
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
        out int nodeCount, out int pathLength)
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
        var path = FindPath(startCell, goalCell, graph);
        pathLength = path.Count;
        if (pathLength == 0) return null;

        return BuildNavRoute(path, graph, destNS, destEW);
    }
}

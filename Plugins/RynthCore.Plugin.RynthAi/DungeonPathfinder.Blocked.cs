// DungeonPathfinder, blocked doorways half (2026-11-16).
//
// A dungeon's cells can place static objects (the EnvCells' stabs), and the dungeon map has none of
// them: it is the cells' walls, floors and openings only. DreamWeave Thread 0x5456 has twelve spider
// webs (model 0x02000D34: 5.3 m wide, 0.55 to 3.75 m off the floor, physics polygons and all) placed in
// its doorways. The map had those doorways open, the explore walked into a web, was stuck, and the
// stuck fallback (TryFindStuckOpening) gave the doorway up a minute later, a web at a time.
//
// GetGeometry closes each doorway a placed object stands across (both ways, as a wall), before the
// ledges are looked at, so every walk laid on the map (patrol, explore, dunroute, the stuck detours)
// goes round. Blocked: walking lines straight through the doorway, 0.25 m apart across it, meet an
// object within 1.2 m of the doorway in a body's way, leaving no free gap of BlockedGapMeters. An arch or a
// frame round a doorway leaves its middle free; a pillar to one side leaves a gap.
//
// 2026-11-18: the objects are the map's own solid models (PlacedSolid) and a lane is a body's walk, not
// rays. The rays (two of four heights, 0.6 to 1.5 m, hitting) counted a ray that went into an object's box
// and met none of its polygons as a hit, so an arch round a doorway shut it (0x0150's 0x01000E60), the
// bridge over 0x000A's hall shut the four doorways onto it (0x02000224: they open onto its deck, a body's
// height clear between its rails), 0x039B's spiral stair shut the doorway that opens onto its foot, and
// 0x238E's stair towers their doors. Now each lane is walked from free floor on either side: the feet on
// the doorway's floor or on what is placed, a step up of StepOverTop at most at a time (onto the foot of a
// ramp and up it to a platform: 0x8804's 0x0200033D), stopped by anything between the feet + PlacedBandLow
// and + PlacedBandHigh (the band the placed objects' walls use). A web's strands at every height add up to
// a closed line; a step block under a door sill (its top the doorway's floor) is stood on; a block taller
// than a step (0x5456's 0x02000CCE) stops the walk. The objects come from StaticMeshSource (the plugin points
// it at GeometryLoader.GetDungeonStaticMeshes once the raycast is up; null: nothing closed).
//
// 2026-11-18, landblocks with buildings: this map is the stuck detours' there (NavRecovery.PlanIndoor), and it
// took every stab, stairs too, as walls and closed the doorways stairs stand in (1,094 building doorways
// world-wide, 1,620 cells no door reached any more; 7D64 from its first doors 113 rooms to 99, E74E 155 to 129).
// There the objects are BuildingMap's furniture: its stairs (BuildingMap.FurnitureFilter) are no walls and close
// no doorway. Dungeons are as before.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

internal static partial class DungeonPathfinder
{
    /// <summary>The static objects a dungeon's cells place, as physics meshes in world metres (GeometryLoader
    /// .GetDungeonStaticMeshes). Null: no doorway is closed for them.</summary>
    internal static Func<uint, List<(uint SetupId, uint CellId, BoundingVolume Mesh)>>? StaticMeshSource;

    /// <summary>A doorway with no free gap this wide left at body height is blocked.</summary>
    internal const double BlockedGapMeters = 1.0;
    // Lanes this far apart across the doorway, none nearer its sides than the margin; each walked from
    // BlockedNear before the doorway to BlockedNear past it (a web 0.8 m into the next cell is in it), a
    // sample every BlockedAlong (half a column of the solid models). A lane starts on free floor, looked for
    // up to BlockedRunUp further out (the foot of a ramp up to the doorway).
    private const double BlockedLaneStep = 0.25, BlockedEdgeMargin = 0.4, BlockedNear = 1.2, BlockedAlong = 0.05, BlockedRunUp = 2.0;

    /// <summary>What closing the blocked doorways did in the last dungeon (one line, for the build log), or null.</summary>
    internal static string? BlockedNote;
    internal static uint BlockedNoteLandblock;

    /// <summary>
    /// Every doorway (narrower than a room's own openings) that a placed object stands across: one entry per
    /// cell pair, with the objects' model ids.
    /// </summary>
    internal static List<(uint A, uint B, uint[] Setups)> FindBlockedDoorways(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo,
                                                                         IReadOnlyList<(uint SetupId, uint CellId, BoundingVolume Mesh)> statics)
    {
        var list = new List<(uint, uint, uint[])>();
        if (statics.Count == 0) return list;
        var seen = new HashSet<ulong>();
        var shapes = new Dictionary<int, PlacedShape?>();   // each object's model, placed once
        foreach (var n in graph.Values)
            foreach (uint nb in n.Neighbors)
            {
                if (!graph.ContainsKey(nb) || !seen.Add(EdgeKey(n.CellId, nb))) continue;
                if (!geo.TryGetDoorway(n.CellId, nb, out var d) || d.IsWide) continue;
                var near = new List<(uint SetupId, PlacedShape Shape)>();
                for (int i = 0; i < statics.Count; i++)
                {
                    var m = statics[i].Mesh;
                    if (m.MeshTriangles is not { Length: >= 3 }) continue;
                    if (m.Max.X < d.X - 4 || m.Min.X > d.X + 4 || m.Max.Y < d.Y - 4 || m.Min.Y > d.Y + 4
                        || m.Max.Z < d.Z || m.Min.Z > d.Z + 2.5) continue;
                    if (!shapes.TryGetValue(i, out var shape)) shapes[i] = shape = PlacedSolid.Place(statics[i].SetupId, m.MeshTriangles);
                    if (shape != null) near.Add((statics[i].SetupId, shape));
                }
                if (near.Count == 0) continue;
                if (DoorwayBlocked(d, near, out var by)) list.Add((d.CellA, d.CellB, by));
            }
        // The other cell pairs of each blocked opening (a portal higher up in it, a stacked cell's: OpeningPairs).
        var have = new HashSet<ulong>(list.Select(x => EdgeKey(x.Item1, x.Item2)));
        foreach (var (a, b, by) in list.ToList())
            foreach (ulong k in OpeningPairs(graph, geo, a, b))
                if (have.Add(k)) list.Add(((uint)(k >> 32), (uint)k, by));
        return list;
    }

    private static bool DoorwayBlocked(DungeonDoorway d, List<(uint SetupId, PlacedShape Shape)> near, out uint[] by)
    {
        var setups = new HashSet<uint>();
        double half = d.Width / 2 - BlockedEdgeMargin;
        double run = 0, bestRun = 0;
        var onLane = new List<(uint SetupId, PlacedShape Shape)>();
        double reach = BlockedNear + BlockedRunUp;
        for (double o = -half; o <= half + 1e-6; o += BlockedLaneStep)
        {
            // The objects whose box the lane (with its run-ups) crosses: none, nothing to walk.
            double lx = d.X - d.Ny * o, ly = d.Y + d.Nx * o;
            double x0 = Math.Min(lx - d.Nx * reach, lx + d.Nx * reach) - 0.2, x1 = Math.Max(lx - d.Nx * reach, lx + d.Nx * reach) + 0.2;
            double y0 = Math.Min(ly - d.Ny * reach, ly + d.Ny * reach) - 0.2, y1 = Math.Max(ly - d.Ny * reach, ly + d.Ny * reach) + 0.2;
            onLane.Clear();
            foreach (var s in near)
                if (s.Shape.MaxX >= x0 && s.Shape.MinX <= x1 && s.Shape.MaxY >= y0 && s.Shape.MinY <= y1) onLane.Add(s);
            // Open when a body gets through either way.
            bool open = onLane.Count == 0 || LaneWalk(d, o, onLane, +1, setups) || LaneWalk(d, o, onLane, -1, setups);
            if (!open) run = 0;
            else { run += BlockedLaneStep; bestRun = Math.Max(bestRun, run); }
        }
        by = setups.ToArray();
        return bestRun < BlockedGapMeters && setups.Count > 0;
    }

    // A body walking lane o of the doorway (dir +1: from CellA's side). It starts on free floor (nothing placed from the
    // doorway's floor up to the body's top), at the lane's end or up to BlockedRunUp further out; its feet are on the
    // doorway's floor or on the highest thing a step up at most from them; anything in its band over its feet stops it
    // (that object's model goes into `by`).
    private static bool LaneWalk(DungeonDoorway d, double o, List<(uint SetupId, PlacedShape Shape)> near, int dir, HashSet<uint> by)
    {
        double lx = d.X - d.Ny * o, ly = d.Y + d.Nx * o;
        double startA = -dir * BlockedNear, endA = dir * BlockedNear;
        bool Free(double a)
        {
            double x = lx + d.Nx * a, y = ly + d.Ny * a;
            foreach (var s in near) if (s.Shape.SolidIn(x, y, d.Z + 0.05, d.Z + DungeonGeometry.PlacedBandHigh)) return false;
            return true;
        }
        for (double ext = 0; ext <= BlockedRunUp + 1e-9; ext += BlockedAlong)
            if (Free(startA - dir * ext)) { startA -= dir * ext; break; }
        double feet = d.Z;
        int n = (int)Math.Round((endA - startA) * dir / BlockedAlong);
        for (int k = 0; k <= n; k++)
        {
            double a = startA + dir * k * BlockedAlong;
            double x = lx + d.Nx * a, y = ly + d.Ny * a;
            double stand = d.Z;
            foreach (var s in near) stand = Math.Max(stand, s.Shape.TopAtMost(x, y, feet + DungeonGeometry.StepOverTop));
            feet = stand;
            foreach (var s in near)
                if (s.Shape.SolidIn(x, y, feet + DungeonGeometry.PlacedBandLow, feet + DungeonGeometry.PlacedBandHigh)) { by.Add(s.SetupId); return false; }
        }
        return true;
    }

    /// <summary>Off: placed objects are no walls on the map (tests compare; the blocked doorways are apart).</summary>
    internal static bool PlacedObjectsAsWalls = true;

    // The stairs left off a building landblock's map (AddPlacedObjectsTo), by its geometry, for the blocked doorways.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DungeonGeometry, HashSet<BuildingMap.PieceKey>> _buildingStairs = new();

    private static readonly Dictionary<uint, bool> _hasBuildings = new();

    /// <summary>The landblock's LandBlockInfo lists buildings (the outdoor world's towns and houses; a dungeon's lists none).</summary>
    internal static bool HasBuildings(uint landblockKey, DatDatabase cellDat)
    {
        lock (_hasBuildings)
            if (_hasBuildings.TryGetValue(landblockKey, out bool known)) return known;
        bool has = false;
        try
        {
            byte[]? info = cellDat.GetFileData((landblockKey << 16) | 0xFFFE);
            if (info != null && info.Length >= 12)
            {
                uint numObjects = BitConverter.ToUInt32(info, 8);
                long at = 12 + (long)numObjects * 32;
                has = at + 2 <= info.Length && BitConverter.ToUInt16(info, (int)at) > 0;
            }
        }
        catch { has = false; }
        lock (_hasBuildings)
        {
            if (_hasBuildings.Count > 512) _hasBuildings.Clear();
            _hasBuildings[landblockKey] = has;
        }
        return has;
    }

    /// <summary>What the placed objects added to the last dungeon's map (one line, for the build log), or null.</summary>
    internal static string? PlacedNote;

    /// <summary>GetGeometry's step: the objects the cells place (an altar, a statue) on the map by what they put in
    /// a body's way at their floor, the outline as walls (DungeonGeometry.AddPlacedObjects), so every walk keeps
    /// clear of them. On a landblock with buildings (<paramref name="cellDat"/> says), its furniture, the stairs left out.</summary>
    private static void AddPlacedObjectsTo(uint landblockKey, DungeonGeometry geo, DatDatabase? cellDat = null)
    {
        PlacedNote = null;
        var source = StaticMeshSource;
        if (!PlacedObjectsAsWalls || source == null) return;
        try
        {
            var statics = source(landblockKey);
            if (statics == null || statics.Count == 0) return;
            int added, stairs = 0;
            bool buildings = cellDat != null && HasBuildings(landblockKey, cellDat);
            if (buildings)
            {
                MarkBuildingMap(geo);
                var f = BuildingMap.AddFurniture(geo, statics);
                _buildingStairs.AddOrUpdate(geo, f.Stairs);
                added = f.Kept;
                stairs = f.Stairs.Count;
            }
            else added = geo.AddPlacedObjects(statics.Where(s => s.Mesh.MeshTriangles is { Length: >= 3 })
                                                    .Select(s => (s.SetupId, s.CellId, s.Mesh.MeshTriangles)));
            var inv = CultureInfo.InvariantCulture;
            var classes = geo.PlacedObjects.GroupBy(p => p.Class).OrderBy(g => g.Key)
                             .Select(g => string.Format(inv, "{0} {1}", g.Key.ToString().ToLowerInvariant(), g.Count()));
            PlacedNote = string.Format(inv, "{0} of {1} placed object(s) stand in the way at body height: walls on the map ({2}){3}",
                                       added, statics.Count, string.Join(", ", classes),
                                       buildings ? string.Format(inv, "; a landblock with buildings: {0} stair(s) left out", stairs) : "");
        }
        catch (Exception e)
        {
            PlacedNote = "placed objects not added: " + e.GetType().Name;
        }
    }

    /// <summary>GetGeometry's step: the cached graph of <paramref name="landblockKey"/> loses the doorways placed
    /// objects stand across (both ways), then its ledges.</summary>
    private static void CloseMapBlocksOf(uint landblockKey, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo)
    {
        CloseBlockedDoorwaysOf(landblockKey, graph, geo);
        CloseLedgesOf(landblockKey, graph, geo);
    }

    private static void CloseBlockedDoorwaysOf(uint landblockKey, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo)
    {
        BlockedNoteLandblock = landblockKey;
        BlockedNote = null;
        var source = StaticMeshSource;
        if (source == null) return;
        try
        {
            var statics = source(landblockKey);
            if (statics == null || statics.Count == 0) return;
            // A building's stairs close no doorway (AddPlacedObjectsTo left them off its map).
            if (_buildingStairs.TryGetValue(geo, out var stairs) && stairs.Count > 0)
                statics = statics.Where(s => BuildingMap.KeyOf(s.SetupId, s.CellId, s.Mesh.MeshTriangles) is not BuildingMap.PieceKey k || !stairs.Contains(k)).ToList();
            var blocked = FindBlockedDoorways(graph, geo, statics);
            if (blocked.Count == 0) return;
            foreach (var (a, b, _) in blocked)
            {
                if (graph.TryGetValue(a, out var na)) na.Neighbors.RemoveAll(c => c == b);
                if (graph.TryGetValue(b, out var nb)) nb.Neighbors.RemoveAll(c => c == a);
            }
            var inv = CultureInfo.InvariantCulture;
            var models = blocked.SelectMany(x => x.Setups).GroupBy(s => s).Select(g => string.Format(inv, "0x{0:X8}", g.Key));
            BlockedNote = string.Format(inv, "{0} doorway cell pair(s) closed: something the cells place stands across them (model {1}), walked round: {2}",
                blocked.Count, string.Join(", ", models),
                string.Join(", ", blocked.Take(8).Select(x => string.Format(inv, "{0:X8}>{1:X8}", x.A, x.B))) + (blocked.Count > 8 ? ", ..." : ""));
        }
        catch (Exception e)
        {
            BlockedNote = "placed objects not looked at: " + e.GetType().Name;
        }
    }
}

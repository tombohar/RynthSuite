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
// goes round. Blocked: walking lines straight through the doorway, 0.25 m apart across it, hit an
// object's physics polygons within 1.2 m of the doorway at some body height (0.6 to 1.5 m: a web's
// strands leave gaps a line slips through and a character doesn't), leaving no free gap of
// BlockedGapMeters. An arch or a frame round a doorway leaves its
// middle free; a pillar to one side leaves a gap. The objects come from StaticMeshSource (the plugin
// points it at GeometryLoader.GetDungeonStaticMeshes once the raycast is up; null: nothing closed).

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
    // Lanes this far apart across the doorway, none nearer its sides than the margin; each looked along from
    // BlockedNear before the doorway to BlockedNear past it (a web 0.8 m into the next cell is in it).
    private const double BlockedLaneStep = 0.25, BlockedEdgeMargin = 0.4, BlockedNear = 1.2;
    // A body's height: a web's strands leave gaps a thin line slips through, a character doesn't.
    private static readonly double[] BlockedHeights = { 0.6, 0.9, 1.2, 1.5 };

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
        foreach (var n in graph.Values)
            foreach (uint nb in n.Neighbors)
            {
                if (!graph.ContainsKey(nb) || !seen.Add(EdgeKey(n.CellId, nb))) continue;
                if (!geo.TryGetDoorway(n.CellId, nb, out var d) || d.IsWide) continue;
                var near = statics.Where(s => s.Mesh.Max.X >= d.X - 4 && s.Mesh.Min.X <= d.X + 4 && s.Mesh.Max.Y >= d.Y - 4 && s.Mesh.Min.Y <= d.Y + 4
                                              && s.Mesh.Max.Z >= d.Z && s.Mesh.Min.Z <= d.Z + 2.5).ToList();
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

    private static bool DoorwayBlocked(DungeonDoorway d, List<(uint SetupId, uint CellId, BoundingVolume Mesh)> near, out uint[] by)
    {
        var setups = new HashSet<uint>();
        double tx = -d.Ny, ty = d.Nx, half = d.Width / 2 - BlockedEdgeMargin;
        var dir = new Vector3((float)d.Nx, (float)d.Ny, 0f);
        double run = 0, bestRun = 0;
        for (double o = -half; o <= half + 1e-6; o += BlockedLaneStep)
        {
            // Shut when something is in the way at two body heights or more (a step or a low kerb meets the lowest only).
            int heights = 0;
            foreach (double h in BlockedHeights)
            {
                var start = new Vector3((float)(d.X + tx * o - d.Nx * BlockedNear), (float)(d.Y + ty * o - d.Ny * BlockedNear), (float)(d.Z + h));
                foreach (var s in near)
                    if (s.Mesh.RayIntersect(start, dir, (float)(2 * BlockedNear), out _)) { heights++; setups.Add(s.SetupId); break; }
            }
            bool shut = heights >= 2;
            if (shut) run = 0;
            else { run += BlockedLaneStep; bestRun = Math.Max(bestRun, run); }
        }
        by = setups.ToArray();
        return bestRun < BlockedGapMeters && setups.Count > 0;
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

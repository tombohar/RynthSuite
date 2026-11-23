// DungeonPathfinder, ledges half (2026-11-15).
//
// A ledge is an opening between two cells whose floors, just inside it on either side, are more
// than LedgeMeters apart. AC won't walk a character off one (it slides along the edge: only a jump
// goes over), and nobody walks up one. DreamWeave Thread 0x0192: twelve 6 m ledges in pairs (a row of
// pits on two levels). The explore laid a walk from 019201C4 (floor 0) to 019201C6 (floor -6), the
// character snagged on the edge every time, and the detours (laid on the same map) never got past
// either. IsDropEdge only sees cells stacked over each other (centres within a metre, or steeper
// than 45 degrees centre to centre), not a ledge between two cells side by side.
//
// GetGeometry closes every ledge in the cached graph (both ways, as if it were a wall), so every walk
// laid on the dungeon map (patrol, explore, dunroute, the stuck detours) goes round by the stairs. A
// ledge whose closing would cut cells off from the rest is the only way on: it stays as it was (a jump
// down would be needed, which the walks don't do yet) and LedgeNote names it. 0x0192: all 194 cells
// stay reachable with its twelve ledges closed.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RynthCore.Plugin.RynthAi;

/// <summary>An opening whose floors on either side are more than DungeonPathfinder.LedgeMeters apart.</summary>
internal readonly record struct DungeonLedge(uint High, uint Low, double Step, double X, double Y, double Z);

internal static partial class DungeonPathfinder
{
    /// <summary>Floors this far apart across an opening make it a ledge (well above a stair's step).</summary>
    internal const double LedgeMeters = 1.5;

    /// <summary>Off: a ledge is judged by the two cells' own floors only (tests compare).</summary>
    internal static bool LedgeFloorFallback = true;

    // The floor is looked for this far inside the opening on each side (the first that has one).
    private static readonly double[] LedgeInsets = { 0.6, 1.0, 1.5 };
    // ... at these heights from the opening's lowest vertex (a floor 6 m below a ledge's opening, say).
    private static readonly double[] LedgeZRefs = { 0, 3, -3, 6, -6, 12, -12 };
    // ... and, for a cell with no floor there, below the opening only.
    private static readonly double[] LedgeDownRefs = { 0, -3, -6, -9, -12, -15, -18 };

    /// <summary>What closing the ledges did in the last dungeon (one line, for the build log), or null.</summary>
    internal static string? LedgeNote;
    internal static uint LedgeNoteLandblock;

    /// <summary>Every ledge of the dungeon: each pair of cells joined by an opening whose floors just
    /// inside it differ by more than <see cref="LedgeMeters"/> (one entry per pair).</summary>
    internal static List<DungeonLedge> FindLedges(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo)
    {
        var list = new List<DungeonLedge>();
        var seen = new HashSet<ulong>();
        double lowest = LowestLook(graph);
        foreach (var n in graph.Values)
            foreach (uint nb in n.Neighbors)
            {
                if (!graph.ContainsKey(nb) || !seen.Add(EdgeKey(n.CellId, nb))) continue;
                if (!geo.TryGetDoorway(n.CellId, nb, out var d)) continue;
                var (nx, ny) = d.NormalFrom(n.CellId);   // out of n, into nb
                double? ha = FloorInside(geo, n.CellId, d, -nx, -ny, lowest), hb = FloorInside(geo, nb, d, nx, ny, lowest);
                if (ha is not { } a || hb is not { } b || Math.Abs(a - b) <= LedgeMeters) continue;
                list.Add(a > b ? new DungeonLedge(n.CellId, nb, a - b, d.X, d.Y, d.Z) : new DungeonLedge(nb, n.CellId, b - a, d.X, d.Y, d.Z));
            }
        return list;
    }

    /// <summary>Under every cell of the graph: as deep as a look for a floor under an opening goes.</summary>
    internal static double LowestLook(Dictionary<uint, DungeonNavNode> graph)
        => graph.Count == 0 ? 0 : graph.Values.Min(n => (double)n.Z) - 2 * JumpDownStep;

    // The floor just inside the opening on the cell's side, its own or (none) any under it, looked for as
    // deep as `lowest` (NaN: the LedgeDownRefs' 18 m). It went no deeper than 18 m (+1.5) before 2026-11-18:
    // Thread 0x000A's hall has a 30 x 20 m hole in its middle down to a floor 24 m below, no ledge was seen
    // round it, and its six floorless cells were walked across ("It tries to navigate over an opening
    // in the floor which is impossible").
    private static double? FloorInside(DungeonGeometry geo, uint cell, DungeonDoorway d, double ux, double uy, double lowest = double.NaN)
    {
        foreach (double inset in LedgeInsets)
        {
            double x = d.X + ux * inset, y = d.Y + uy * inset;
            foreach (double dz in LedgeZRefs)
                if (geo.TryCellFloorHeight(cell, x, y, d.Z + dz, out double h)) return h;
        }
        if (!LedgeFloorFallback) return null;
        // A cell with no floor of its own there (the air of a tall room the opening looks into: Thread
        // 0x039B's corridor at -6 opens through 0143 into a round room whose floor, at -12, is its other
        // cells'): the floor UNDER that side, whichever cell it is. Only under it: a level above is no
        // floor to land on (Tiny Hive's floorless tube cells found the next level's floor 5 m up).
        // Each height looked at covers LedgeDownBand either side, so together they leave no gap (at
        // 0.6 m round heights 3 m apart, a floor 9.7 m down was between two: Thread 0x0169's shaft, whose
        // cave floor slopes from -9 to -12, had two of its eight openings found as no ledge).
        foreach (double inset in LedgeInsets)
        {
            double x = d.X + ux * inset, y = d.Y + uy * inset;
            foreach (double dz in LedgeDownRefs)
                if (geo.TryFloorHeight(x, y, d.Z + dz, LedgeDownBand, out double h) && h <= d.Z + 0.6) return h;
        }
        if (double.IsNaN(lowest)) return null;
        // Nothing within 18 m at any inset: deeper (only then, so the floors found before are found as before).
        foreach (double inset in LedgeInsets)
        {
            double x = d.X + ux * inset, y = d.Y + uy * inset;
            for (double z = d.Z + LedgeDownRefs[^1] - JumpDownStep; z >= lowest; z -= JumpDownStep)
                if (geo.TryFloorHeight(x, y, z, LedgeDownBand, out double h) && h <= d.Z + 0.6) return h;
        }
        return null;
    }

    /// <summary>Half the 3 m between two LedgeDownRefs: every height below the opening is looked at.</summary>
    private const double LedgeDownBand = 1.5;

    /// <summary>
    /// Closes each ledge in <paramref name="graph"/> (both ways) unless that leaves its two cells with no
    /// other way between them; those are returned (the only way on). The graph is changed in place.
    /// With <paramref name="hasFloor"/>, a ledge into air is closed even then: when nothing it alone
    /// leads to has a floor of its own (Thread 0x0169's shaft, four open-air cells over a cave), it
    /// leads nowhere to walk, and keeping it open had the walks step off into the shaft.
    /// </summary>
    internal static List<DungeonLedge> CloseLedges(Dictionary<uint, DungeonNavNode> graph, IReadOnlyList<DungeonLedge> ledges, out int closed,
                                                   Func<uint, bool>? hasFloor = null)
    {
        closed = 0;
        var kept = new List<DungeonLedge>();
        foreach (var l in ledges)
        {
            if (!graph.TryGetValue(l.High, out var hi) || !graph.TryGetValue(l.Low, out var lo)) continue;
            int hiLinks = hi.Neighbors.RemoveAll(c => c == l.Low);
            int loLinks = lo.Neighbors.RemoveAll(c => c == l.High);
            if (hiLinks + loLinks == 0) continue;
            if (Linked(graph, l.High, l.Low)) { closed++; continue; }
            if (hasFloor != null && !Walked(graph, l.Low).Any(hasFloor)) { closed++; continue; }   // only air beyond it
            // The only way between them: put it back.
            for (int i = 0; i < Math.Max(1, hiLinks); i++) hi.Neighbors.Add(l.Low);
            for (int i = 0; i < Math.Max(1, loLinks); i++) lo.Neighbors.Add(l.High);
            kept.Add(l);
        }
        return kept;
    }

    // Every cell a walk (no drop edges) reaches from a, a included.
    private static HashSet<uint> Walked(Dictionary<uint, DungeonNavNode> graph, uint a)
    {
        var seen = new HashSet<uint> { a };
        var queue = new Queue<uint>();
        queue.Enqueue(a);
        while (queue.Count > 0)
        {
            if (!graph.TryGetValue(queue.Dequeue(), out var n)) continue;
            foreach (uint nb in n.Neighbors)
                if (graph.TryGetValue(nb, out var o) && !IsDropEdge(n, o) && seen.Add(nb)) queue.Enqueue(nb);
        }
        return seen;
    }

    // a and b joined by a walk (no drop edges).
    private static bool Linked(Dictionary<uint, DungeonNavNode> graph, uint a, uint b)
    {
        var seen = new HashSet<uint> { a };
        var queue = new Queue<uint>();
        queue.Enqueue(a);
        while (queue.Count > 0)
        {
            uint c = queue.Dequeue();
            if (c == b) return true;
            if (!graph.TryGetValue(c, out var n)) continue;
            foreach (uint nb in n.Neighbors)
                if (graph.TryGetValue(nb, out var o) && !IsDropEdge(n, o) && seen.Add(nb)) queue.Enqueue(nb);
        }
        return false;
    }

    /// <summary>GetGeometry's step: the cached graph of <paramref name="landblockKey"/> loses its ledges.</summary>
    private static void CloseLedgesOf(uint landblockKey, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo)
    {
        try
        {
            var ledges = FindLedges(graph, geo);
            LedgeNoteLandblock = landblockKey;
            if (ledges.Count == 0) { LedgeNote = null; return; }
            var kept = CloseLedges(graph, ledges, out int closed, geo.HasFloor);
            var inv = CultureInfo.InvariantCulture;
            LedgeNote = string.Format(inv, "{0} ledge(s) (floors over {1:0.#} m apart across an opening): {2} closed, walked round",
                ledges.Count, LedgeMeters, closed)
                + (kept.Count == 0 ? "" : string.Format(inv, "; {0} left open, the only way on (a jump down is needed): {1}", kept.Count,
                    string.Join(", ", kept.Take(6).Select(k => string.Format(inv, "{0:X8}>{1:X8} {2:F1} m", k.High, k.Low, k.Step)))));
        }
        catch (Exception e)
        {
            LedgeNote = "ledges not looked at: " + e.GetType().Name;
        }
    }
}

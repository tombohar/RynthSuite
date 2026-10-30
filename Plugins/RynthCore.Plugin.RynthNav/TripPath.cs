using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// The ground a <see cref="PlannedTrip"/> walks, worked out once for the whole trip (on a worker:
/// TilePathFinder reads the tiles and the route graph into its own navmesh): per walking step, the
/// path from where the step starts to its point, thinned to the corners that matter; per hub step,
/// the walk inside the Town Network. The path markers draw it and "save as nav route" writes it.
/// Never changed after it's built. Host-free: the route tests build these with a fake path finder.
/// </summary>
internal sealed class TripPath
{
    public readonly int TripId;
    /// <summary>Per step: the walk, first point = where it starts, last = the step's point (null = no walk).</summary>
    public readonly WorldPt[]?[] Walks;
    /// <summary>Per hub step: the walk inside the Town Network to the exit portal (null otherwise).</summary>
    public readonly WorldPt[]?[] Hub;
    /// <summary>What couldn't be worked out ("step 2: no navmesh path, a straight line"), for chat.</summary>
    public readonly IReadOnlyList<string> Notes;

    public TripPath(int tripId, WorldPt[]?[] walks, WorldPt[]?[] hub, IReadOnlyList<string> notes)
    {
        TripId = tripId; Walks = walks; Hub = hub; Notes = notes;
    }
}

internal static class TripPathBuilder
{
    /// <summary>A corner closer than this (yards) to the straight line past it is dropped.</summary>
    public const double ThinTolerance = 0.75;
    /// <summary>Straight stretches get a point at least this often (yards), so the markers and a nav follow the ground.</summary>
    public const double MaxSpacing = 60.0;
    /// <summary>Points closer together than this are one point.</summary>
    public const double DuplicateUnits = 0.5;

    /// <summary>
    /// A walkable path from <paramref name="from"/> to <paramref name="to"/> (corners, both ends
    /// included), or null when there is none to be had; <paramref name="note"/> says why or what was
    /// approximated. <paramref name="intoPortal"/>: the walk ends by stepping into a portal at
    /// <paramref name="to"/> (so that one isn't kept out of).
    /// </summary>
    public delegate List<WorldPt>? WalkFinder(WorldPt from, WorldPt to, bool intoPortal, out string note);

    /// <summary>
    /// The trip's ground: each walking step through <paramref name="walk"/> (a straight line where it
    /// has nothing, with a note), each hub step from <paramref name="net"/>; all thinned.
    /// </summary>
    public static TripPath Build(PlannedTrip trip, WalkFinder? walk, TownNet? net)
    {
        int n = trip.Count;
        var walks = new WorldPt[]?[n];
        var hub = new WorldPt[]?[n];
        var notes = new List<string>();
        for (int i = 0; i < n; i++)
        {
            if (trip.Steps[i].IsHub)
            {
                List<WorldPt>? inside = trip.HubWalk(i, net, out string why);
                if (inside == null) notes.Add($"step {i + 1}: {why}");
                else hub[i] = Thin(inside).ToArray();
                continue;
            }
            if (trip.WalkFrom[i] is not WorldPt from || trip.Point[i] is not WorldPt to) continue;
            string note = "";
            List<WorldPt>? raw = null;
            try { raw = walk?.Invoke(from, to, trip.Steps[i].UsePortal, out note); }
            catch (Exception ex) { note = "path finder failed: " + ex.Message; raw = null; }
            if (raw == null || raw.Count < 2)
            {
                raw = new List<WorldPt> { from, to };
                notes.Add($"step {i + 1}: {(note.Length > 0 ? note : "no navmesh path")}; a straight line there");
            }
            else
            {
                if (note.Length > 0) notes.Add($"step {i + 1}: {note}");
                // The path ends on the navmesh beside the point; the point itself is where the step goes.
                if (raw[0].FlatDistance(from) > 2.0) raw.Insert(0, from);
                if (raw[^1].FlatDistance(to) > 2.0) raw.Add(new WorldPt(to.X, to.Y, raw[^1].H));
            }
            walks[i] = Thin(raw).ToArray();
        }
        return new TripPath(trip.Id, walks, hub, notes);
    }

    /// <summary>
    /// The path with near-duplicate points merged, corners within <paramref name="tolerance"/> of the
    /// line past them dropped (Ramer-Douglas-Peucker, ends kept), and straight stretches longer than
    /// <paramref name="maxSpacing"/> split evenly. Unknown heights are filled from the neighbours.
    /// </summary>
    public static List<WorldPt> Thin(IReadOnlyList<WorldPt> pts, double tolerance = ThinTolerance, double maxSpacing = MaxSpacing)
    {
        var dedup = new List<WorldPt>(pts.Count);
        foreach (WorldPt p in pts)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
            if (dedup.Count > 0 && dedup[^1].FlatDistance(p) < DuplicateUnits)
            {
                if (double.IsNaN(dedup[^1].H) && !double.IsNaN(p.H)) dedup[^1] = dedup[^1] with { H = p.H };
                continue;
            }
            dedup.Add(p);
        }
        FillHeights(dedup);
        if (dedup.Count <= 2) return Split(dedup, maxSpacing);

        var keep = new bool[dedup.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, dedup.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            int best = -1;
            double bestD = tolerance;
            for (int i = a + 1; i < b; i++)
            {
                double d = PlannedTrip.SegmentDistance(dedup[i], dedup[a], dedup[b]);
                if (d > bestD) { bestD = d; best = i; }
            }
            if (best < 0) continue;
            keep[best] = true;
            stack.Push((a, best));
            stack.Push((best, b));
        }
        var kept = new List<WorldPt>();
        for (int i = 0; i < dedup.Count; i++) if (keep[i]) kept.Add(dedup[i]);
        return Split(kept, maxSpacing);
    }

    private static List<WorldPt> Split(List<WorldPt> pts, double maxSpacing)
    {
        if (pts.Count < 2 || maxSpacing <= 0) return pts;
        var o = new List<WorldPt>(pts.Count) { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            WorldPt a = pts[i - 1], b = pts[i];
            int parts = (int)Math.Ceiling(a.FlatDistance(b) / maxSpacing);
            for (int k = 1; k < parts; k++)
            {
                double t = (double)k / parts;
                o.Add(new WorldPt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.H + (b.H - a.H) * t));
            }
            o.Add(b);
        }
        return o;
    }

    /// <summary>NaN heights take the nearest known one before them (else after them).</summary>
    private static void FillHeights(List<WorldPt> pts)
    {
        double last = double.NaN;
        for (int i = 0; i < pts.Count; i++)
        {
            if (!double.IsNaN(pts[i].H)) last = pts[i].H;
            else if (!double.IsNaN(last)) pts[i] = pts[i] with { H = last };
        }
        double next = double.NaN;
        for (int i = pts.Count - 1; i >= 0; i--)
        {
            if (!double.IsNaN(pts[i].H)) next = pts[i].H;
            else if (!double.IsNaN(next)) pts[i] = pts[i] with { H = next };
        }
    }

    /// <summary>
    /// The path stopped <paramref name="units"/> short of its end, measured along it (a walk to a
    /// portal stops before stepping in). Only the start is left when the path is shorter than that.
    /// </summary>
    public static List<WorldPt> StopShort(IReadOnlyList<WorldPt> pts, double units)
    {
        var o = new List<WorldPt>(pts);
        double left = units;
        while (o.Count >= 2 && left > 0)
        {
            WorldPt a = o[^2], b = o[^1];
            double d = a.FlatDistance(b);
            if (d > left)
            {
                double t = (d - left) / d;
                o[^1] = new WorldPt(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.H + (b.H - a.H) * t);
                return o;
            }
            left -= d;
            o.RemoveAt(o.Count - 1);
        }
        return o;
    }
}

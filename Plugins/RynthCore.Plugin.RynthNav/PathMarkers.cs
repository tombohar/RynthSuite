using System;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

internal enum MarkKind : byte { Waypoint, Portal, Recall, Town, Goal }

/// <summary>One marker on the ground: a waypoint of a walk, or where a step happens.</summary>
internal readonly record struct Mark(WorldPt P, MarkKind Kind, string Label, int Step, bool LineToNext);

/// <summary>
/// A trip's markers, built on the tick from the trip and its path and then never changed: the
/// Nav3D emit reads only this (never the plan, the route or the walker's state).
/// </summary>
internal sealed class MarkerSnapshot
{
    public static readonly MarkerSnapshot Empty = new(0, false, Array.Empty<Mark>());

    public readonly int TripId;
    /// <summary>Built with the trip's path (waypoints and lines), not just the steps' points.</summary>
    public readonly bool HasPath;
    public readonly Mark[] Marks;

    public MarkerSnapshot(int tripId, bool hasPath, Mark[] marks) { TripId = tripId; HasPath = hasPath; Marks = marks; }

    /// <summary>The first marker of step <paramref name="step"/>, or -1.</summary>
    public int FirstOfStep(int step)
    {
        for (int i = 0; i < Marks.Length; i++) if (Marks[i].Step == step) return i;
        return -1;
    }

    /// <summary>The last marker of step <paramref name="step"/>, or -1.</summary>
    public int LastOfStep(int step)
    {
        for (int i = Marks.Length - 1; i >= 0; i--) if (Marks[i].Step == step) return i;
        return -1;
    }
}

/// <summary>Where the markers go: the engine's Nav3D buffer in the game, a recorder in the tests.</summary>
internal interface IMarkerSink : INav3DSink
{
    void Ring(float x, float y, float z, float radius, float thickness, uint colorArgb);
}

/// <summary>What the emit needs to know about this tick (all plain values, taken on the tick).</summary>
internal readonly record struct MarkerView(
    double Px, double Py, double Pz,   // the character: world east, north, height
    int Next,                          // the marker the walk is heading for (-1: none, a plan only)
    bool Labels, bool Guide);

/// <summary>
/// RynthNav's path markers: the planned trip drawn on the ground the way RynthAi draws its nav
/// routes (rings at the waypoints, lines between them, on the engine's Nav3D buffer), with the
/// place each step happens (a portal, a recall, the Town Network, the goal) in its own colour and,
/// optionally, a label (Plugins/Shared/Nav3DText) and a guide line from your feet to the next
/// waypoint. Host-free: the route tests build and emit these into a recorder.
/// </summary>
internal static class PathMarkers
{
    public const double WaypointRange = 160.0;   // waypoints this close (yards) are drawn
    public const double StepRange = 400.0;       // a step's own marker, this close
    public const double LabelRange = 90.0;
    public const double MaxGuideUnits = 500.0;
    public const double NextReach = 4.0;         // the next waypoint counts as reached this close
    public const int MaxWaypointRings = 40;
    public const int MaxStepRings = 8;
    public const int MaxLines = 90;
    public const int MaxLabelTriangles = 900;    // the engine keeps 8192 a tick for every plugin
    public const int MaxLabelChars = 22;

    public const uint WaypointColor = 0xFF40D973, LineColor = 0xFF2FB863, NextColor = 0xFFFF4444,
        PortalColor = 0xFFBF4DFF, RecallColor = 0xFFFFC040, TownColor = 0xFF2EC4C4, GoalColor = 0xFFFFD24D,
        GuideColor = 0xFFFFD21E;

    private const float WaypointRadius = 0.9f, StepRadius = 1.8f, RingThick = 0.12f, LineThick = 0.08f, Lift = 0.1f;

    // ── Build (tick) ─────────────────────────────────────────────────────

    /// <summary>
    /// The markers for <paramref name="trip"/>: with its <paramref name="path"/> (same trip), every
    /// walk's waypoints joined by lines and each step's own marker; without it (still being worked
    /// out, or none to be had), only the steps' markers.
    /// </summary>
    public static MarkerSnapshot Build(PlannedTrip trip, TripPath? path, TownNet? net)
    {
        if (path != null && path.TripId != trip.Id) path = null;
        var marks = new List<Mark>();
        int n = trip.Count;
        for (int i = 0; i < n; i++)
        {
            RouteStep s = trip.Steps[i];
            bool last = i == n - 1;
            if (s.UseRecall)
            {
                if (trip.Point[i] is WorldPt at)
                    marks.Add(new Mark(at, MarkKind.Recall, Fit("RECALL " + (trip.RecallOf(i)?.Name ?? s.Label)), i, false));
                continue;
            }
            if (s.IsHub)
            {
                WorldPt? exit = PlannedTrip.HubExitPoint(net, s);
                WorldPt[]? inside = path?.Hub[i];
                if (inside != null)
                    foreach (WorldPt p in inside)
                    {
                        if (exit is WorldPt e && p.FlatDistance(e) < 2.0) continue;
                        Add(marks, new Mark(p, MarkKind.Waypoint, "", i, true));
                    }
                WorldPt? at = exit ?? (inside is { Length: > 0 } ? inside[^1] : null);
                string where = net != null && s.HubExit >= 0 && s.HubExit < net.Exits.Count ? net.Exits[s.HubExit].Where : s.Label;
                if (at is WorldPt a) marks.Add(new Mark(a, MarkKind.Town, Fit("PORTAL TO " + where), i, false));
                continue;
            }
            if (trip.Point[i] is not WorldPt to) continue;
            WorldPt[]? walk = path?.Walks[i];
            if (walk != null)
                for (int k = 0; k < walk.Length - 1; k++)
                    Add(marks, new Mark(walk[k], MarkKind.Waypoint, "", i, true));
            if (walk is { Length: > 0 }) to = to with { H = walk[^1].H };
            Mark end;
            if (s.UsePortal)
            {
                bool intoHub = i + 1 < n && trip.Steps[i + 1].IsHub;
                string label = intoHub ? "TOWN NETWORK"
                    : last && trip.EndsInEntrance ? "ENTER " + trip.EnterName
                    : "PORTAL " + s.Label;
                end = new Mark(to, intoHub ? MarkKind.Town : MarkKind.Portal, Fit(label), i, false);
            }
            else if (last) end = new Mark(to, MarkKind.Goal, Fit(trip.Goal.Length > 0 ? trip.Goal : "ARRIVE"), i, false);
            else end = new Mark(to, MarkKind.Waypoint, "", i, walk != null);
            Add(marks, end);
        }
        return new MarkerSnapshot(trip.Id, path != null, marks.ToArray());
    }

    // A leg starts where the last one ended: the same point once. A step's own marker takes the
    // place of a waypoint under it (the line before it then runs into the step's marker).
    private static void Add(List<Mark> marks, Mark m)
    {
        if (marks.Count > 0 && marks[^1].P.FlatDistance(m.P) < 1.0)
        {
            Mark prev = marks[^1];
            if (m.Kind == MarkKind.Waypoint)
            {
                if (m.LineToNext && !prev.LineToNext && prev.Kind == MarkKind.Waypoint) marks[^1] = prev with { LineToNext = true };
                return;
            }
            if (prev.Kind == MarkKind.Waypoint) marks.RemoveAt(marks.Count - 1);
        }
        marks.Add(m);
    }

    private static string Fit(string s)
    {
        s = s.Trim().ToUpperInvariant();
        return s.Length > MaxLabelChars ? s[..(MaxLabelChars - 2)] + ".." : s;
    }

    // ── The next waypoint (tick) ─────────────────────────────────────────

    /// <summary>
    /// The marker the walk on step <paramref name="step"/> heads for next, carried on from
    /// <paramref name="idx"/> (last tick's): it moves on when the character is within
    /// <see cref="NextReach"/> of it, or is already nearer the stretch after it than the one before
    /// it (a re-plan or a cut corner). Never moves back within a step. -1 when the step has no markers.
    /// </summary>
    public static int Advance(MarkerSnapshot s, int idx, int step, double px, double py)
    {
        Mark[] m = s.Marks;
        int first = s.FirstOfStep(step), lastOfStep = s.LastOfStep(step);
        if (first < 0) return -1;
        if (idx < first || idx > lastOfStep) idx = first;
        var me = new WorldPt(px, py, 0);
        while (idx < lastOfStep)
        {
            if (m[idx].P.FlatDistance(me) < NextReach) { idx++; continue; }
            WorldPt before = idx > first ? m[idx - 1].P : m[idx].P;
            double toBefore = idx > first ? PlannedTrip.SegmentDistance(me, before, m[idx].P) : double.MaxValue;
            double toAfter = PlannedTrip.SegmentDistance(me, m[idx].P, m[idx + 1].P, out double t);
            if (toAfter + 1.0 < toBefore && t > 0.05) { idx++; continue; }
            break;
        }
        return idx;
    }

    /// <summary>The marker nearest the character (a plan only: where the guide line points), or -1.</summary>
    public static int Nearest(MarkerSnapshot s, double px, double py)
    {
        int best = -1;
        double bestD = MaxGuideUnits;
        var me = new WorldPt(px, py, 0);
        for (int i = 0; i < s.Marks.Length; i++)
        {
            double d = s.Marks[i].P.FlatDistance(me);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ── Emit (tick, into the Nav3D buffer) ───────────────────────────────

    /// <summary>
    /// Submits the markers near the character: rings (waypoints within <see cref="WaypointRange"/>,
    /// at most <see cref="MaxWaypointRings"/> nearest; steps' markers within <see cref="StepRange"/>),
    /// lines between neighbouring waypoints, the guide line first (so a full line buffer never drops
    /// it) and the labels last. Positions go in the Nav3D frame: the character's landblock, X = east
    /// metres, Y = height, Z = north metres. Returns the number of rings drawn.
    /// </summary>
    public static int Emit(MarkerSnapshot s, in MarkerView v, IMarkerSink sink)
    {
        Mark[] m = s.Marks;
        if (m.Length == 0) return 0;
        double baseX = Math.Floor(v.Px / 192.0) * 192.0, baseY = Math.Floor(v.Py / 192.0) * 192.0;
        float px = (float)(v.Px - baseX), py = (float)(v.Py - baseY), pz = (float)v.Pz;
        var me = new WorldPt(v.Px, v.Py, v.Pz);

        // Which markers: steps' own within StepRange, waypoints within WaypointRange (the nearest).
        var dist = new double[m.Length];
        var drawn = new bool[m.Length];
        var waypoints = new List<int>();
        int steps = 0;
        for (int i = 0; i < m.Length; i++)
        {
            dist[i] = m[i].P.FlatDistance(me);
            if (m[i].Kind == MarkKind.Waypoint) { if (dist[i] <= WaypointRange) waypoints.Add(i); }
            else if (dist[i] <= StepRange && steps < MaxStepRings) { drawn[i] = true; steps++; }
        }
        if (waypoints.Count > MaxWaypointRings) waypoints.Sort((a, b) => dist[a].CompareTo(dist[b]));
        for (int k = 0; k < waypoints.Count && k < MaxWaypointRings; k++) drawn[waypoints[k]] = true;
        if (v.Next >= 0 && v.Next < m.Length && dist[v.Next] <= StepRange) drawn[v.Next] = true;

        (float X, float Y, float Z) Local(WorldPt p) =>
            ((float)(p.X - baseX), (double.IsNaN(p.H) ? pz : (float)p.H) + Lift, (float)(p.Y - baseY));

        // Guide line: to the next waypoint (a walk), else the nearest marker (a plan).
        if (v.Guide)
        {
            int g = v.Next >= 0 && v.Next < m.Length ? v.Next : Nearest(s, v.Px, v.Py);
            if (g >= 0 && dist[g] <= MaxGuideUnits)
            {
                var (gx, gy, gz) = Local(m[g].P);
                sink.Line(px, pz + Lift + 0.05f, py, gx, gy, gz, LineThick, GuideColor);
            }
        }

        int rings = 0;
        for (int i = 0; i < m.Length; i++)
        {
            if (!drawn[i]) continue;
            var (x, y, z) = Local(m[i].P);
            bool next = i == v.Next;
            bool step = m[i].Kind != MarkKind.Waypoint;
            uint color = next ? NextColor : ColorOf(m[i].Kind);
            sink.Ring(x, y, z, step ? StepRadius : next ? WaypointRadius * 1.2f : WaypointRadius, step || next ? RingThick * 1.4f : RingThick, color);
            rings++;
        }

        int lines = 0;
        for (int i = 0; i + 1 < m.Length && lines < MaxLines; i++)
        {
            if (!m[i].LineToNext) continue;
            // A line when either end is drawn and the other isn't far off.
            if (!(drawn[i] || drawn[i + 1]) || Math.Min(dist[i], dist[i + 1]) > WaypointRange) continue;
            var (ax, ay, az) = Local(m[i].P);
            var (bx, by, bz) = Local(m[i + 1].P);
            sink.Line(ax, ay, az, bx, by, bz, LineThick, LineColor);
            lines++;
        }

        if (v.Labels)
        {
            int tris = 0;
            for (int i = 0; i < m.Length; i++)
            {
                if (!drawn[i] || m[i].Label.Length == 0 || dist[i] > LabelRange) continue;
                var (x, y, z) = Local(m[i].P);
                if (!Nav3DText.Emit(x, y, z, m[i].Label, 0, ColorOf(m[i].Kind), px, py, sink, ref tris, MaxLabelTriangles)) break;
            }
        }
        return rings;
    }

    public static uint ColorOf(MarkKind k) => k switch
    {
        MarkKind.Portal => PortalColor,
        MarkKind.Recall => RecallColor,
        MarkKind.Town => TownColor,
        MarkKind.Goal => GoalColor,
        _ => WaypointColor,
    };
}

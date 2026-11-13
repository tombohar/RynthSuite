// ============================================================================
//  RynthAi - Dashboard/NavTrail.cs
//  The Navigation key's icon, "Trail" (2026-11-11; the mock-ups are the
//  artifact "Navigation Key Icons"). A shape for the way RynthAi navigates:
//    Circular  a loop with its direction arrow
//    Linear    a trail with an arrow at each end
//    Once      a trail to a flag
//    Follow    footsteps after a leader arrow
//    Patrol    a sweep back and forth across the rooms (dungeon patrol)
//    Hunt      a radar (Outdoor Hunt)
//    None      an empty dotted trail: navigation on, nothing loaded
//  Off is a dotted outline; On is the trail with its marker at rest;
//  Moving (the activity reads Navigating) sends the marker along it with a
//  green trail behind (the radar sweeps, the footsteps march).
//  Priority (Boost Nav Priority, on a route, follow or patrol: picked
//  2026-11-11, "green and lane", artifact "Nav Priority Mark") draws the
//  trail in green over a green lane, the way the Looting key's chest turns
//  gold. Drawn on a 15 px em (IconKit) and scaled to the em asked for. The
//  top right stays clear for the key's LED. Render thread; allocates nothing
//  per frame.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using static RynthCore.Plugin.RynthAi.Dashboard.IconKit;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Dashboard;

internal static class NavTrail
{
    internal enum Look { Off, On, Moving }

    /// <summary>What the Navigation key shows (RynthAiPlugin.ReadNavKind picks it on the tick).</summary>
    internal enum Kind { None, Circular, Linear, Once, Follow, Patrol, Hunt }

    // The stamina green (go) and its light.
    private static readonly uint Green = Argb(0xFF73D885), GreenLight = Argb(0xFFD2F5DA);

    private const float Q = MathF.PI / 2;

    /// <summary>A path in design pixels: its points, the running length at each, closed or not.</summary>
    private sealed class Path
    {
        public readonly Vector2[] Pts;
        public readonly float[] Len;   // Len[i]: length up to point i; closed paths have one more, back to Pts[0]
        public readonly bool Closed;
        public float Total => Len[^1];

        public Path(Vector2[] pts, bool closed)
        {
            Pts = pts;
            Closed = closed;
            int segs = closed ? pts.Length : pts.Length - 1;
            Len = new float[segs + 1];
            for (int i = 1; i <= segs; i++) Len[i] = Len[i - 1] + Vector2.Distance(pts[i - 1], pts[i % pts.Length]);
        }

        /// <summary>The point a fraction <paramref name="u"/> of the way along (wrapped when closed, clamped when not).</summary>
        public Vector2 At(float u)
        {
            u = Closed ? u - MathF.Floor(u) : Math.Clamp(u, 0f, 1f);
            float d = u * Total;
            int i = 1;
            while (i < Len.Length - 1 && Len[i] < d) i++;
            float span = MathF.Max(1e-6f, Len[i] - Len[i - 1]);
            return Vector2.Lerp(Pts[i - 1], Pts[i % Pts.Length], (d - Len[i - 1]) / span);
        }

        public float AngleAt(float u)
        {
            Vector2 d = At(u + 0.01f) - At(u - 0.01f);
            return MathF.Atan2(d.Y, d.X);
        }
    }

    // ── The shapes, in pixels at a 15 px em, built once ────────────────

    private static readonly Path Loop = BuildLoop();
    private static readonly Path S = new(Cubic(new(5.4f, 5.4f), new(6.4f, -1.0f), new(-6.4f, 1.4f), new(-5.4f, -4.6f), 36), false);
    private static readonly Path Once = new(Cubic(new(5.4f, 5.6f), new(6.0f, 0f), new(-5.6f, 2.2f), new(-4.2f, -1.2f), 32), false);
    private static readonly Path Fol = new(Cubic(new(4.6f, 5.2f), new(4.2f, 0.2f), new(-1.0f, 1.2f), new(-3.6f, -3.6f), 30), false);
    private static readonly Path Serp = BuildSerpentine();
    private const int MaxPoints = 64;

    /// <summary>An ellipse 12.2 by 9.8 starting at the top, clockwise.</summary>
    private static Path BuildLoop()
    {
        var pts = new Vector2[48];
        for (int i = 0; i < pts.Length; i++)
        {
            float a = -Q + MathF.Tau * i / pts.Length;
            pts[i] = new Vector2(MathF.Cos(a) * 6.1f, MathF.Sin(a) * 6.1f * 0.8f + 0.6f);
        }
        return new Path(pts, true);
    }

    /// <summary>Four runs across, joined by U-turns at alternate ends (the runs are the gaps between the arcs).</summary>
    private static Path BuildSerpentine()
    {
        var pts = new System.Collections.Generic.List<Vector2> { new(-5.2f, -4.6f) };
        AddArc(pts, new(3.6f, -3.05f), 1.55f, -Q, Q, 8);
        AddArc(pts, new(-3.6f, 0.05f), 1.55f, -Q, -3 * Q, 8);
        AddArc(pts, new(3.6f, 3.15f), 1.55f, -Q, Q, 8);
        pts.Add(new(-5.2f, 4.7f));
        return new Path(pts.ToArray(), false);
    }

    private static void AddArc(System.Collections.Generic.List<Vector2> pts, Vector2 c, float r, float a0, float a1, int n)
    {
        for (int i = 0; i <= n; i++) pts.Add(c + Dir(a0 + (a1 - a0) * i / n) * r);
    }

    private static Vector2[] Cubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p3, int n)
    {
        var o = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float t = i / (float)n, u = 1f - t;
            o[i] = u * u * u * p0 + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * p3;
        }
        return o;
    }

    // ── Drawing ─────────────────────────────────────────────────────────

    /// <summary>One frame's placement: the icon's centre and scale, the line width and the colour.</summary>
    private readonly struct Pen
    {
        public readonly ImDrawListPtr Dl;
        public readonly Vector2 C;
        public readonly float S, Line;
        public readonly uint Fg;
        public readonly bool Glow;
        public Pen(ImDrawListPtr dl, Vector2 c, float s, uint fg, bool glow)
        {
            Dl = dl; C = c; S = s; Line = MathF.Max(1f, s); Fg = fg; Glow = glow;
        }
        public Vector2 P(Vector2 design) => C + design * S;
    }

    // The priority lane: a green band under the trail, an inner one and a wider, fainter one;
    // it breathes while the marker runs.
    private const float LaneW = 4.6f, LaneOuterW = 6.4f, LaneAlpha = 0.22f, LaneOuter = 0.6f, LanePulse = 0.07f, LaneHz = 0.8f;
    private const float PriGlowR = 6.6f, PriGlowY = 0.4f, PriGlow = 0.05f;

    /// <summary>The kinds Boost Nav Priority applies to: a route, a follow or a patrol (not the hunt, not nothing).</summary>
    internal static bool IsRoute(Kind kind) => kind is Kind.Circular or Kind.Linear or Kind.Once or Kind.Follow or Kind.Patrol;

    /// <summary>
    /// Draws the Navigation icon for <paramref name="kind"/> centred on <paramref name="centre"/>,
    /// <paramref name="em"/> pixels across, in <paramref name="color"/>. <paramref name="priority"/>
    /// (Boost Nav Priority) draws a route in green over a green lane; the hunt and no route ignore it.
    /// <paramref name="glow"/> adds the green glows (the key has them; the activity line doesn't).
    /// </summary>
    public static void Draw(ImDrawListPtr dl, Vector2 centre, float em, uint color, Look look, Kind kind, bool priority = false, bool glow = true)
    {
        float t = Clock();
        bool pri = priority && look != Look.Off && IsRoute(kind);
        var pen = new Pen(dl, centre, em / DesignEm, pri ? Green : color, glow);
        if (pri)
        {
            if (glow) Glow(dl, pen.P(new Vector2(0f, PriGlowY)), PriGlowR * pen.S, Green, PriGlow);
            Lane(pen, PathOf(kind), look == Look.Moving, t);
        }
        switch (kind)
        {
            case Kind.Circular: DrawLoop(pen, look, t); break;
            case Kind.Linear: DrawLinear(pen, look, t); break;
            case Kind.Once: DrawOnce(pen, look, t); break;
            case Kind.Follow: DrawFollow(pen, look, t); break;
            case Kind.Patrol: DrawPatrol(pen, look, t); break;
            case Kind.Hunt: DrawHunt(pen, look, t); break;
            default: DrawNone(pen, look); break;
        }
    }

    private static void DrawLoop(Pen p, Look look, float t)
    {
        bool moving = look == Look.Moving;
        float u = moving ? t * 0.45f % 1f : 0.75f;
        PathLook(p, Loop, look);
        if (look != Look.Off)
        {
            // Waypoints at the quarters; the resting marker covers the left one.
            for (int k = 0; k < 4; k++)
                if (k != 3 || moving) p.Dl.AddCircleFilled(p.P(Loop.At(k * 0.25f)), 0.7f * p.S, Alpha(p.Fg, 0.75f), 8);
        }
        Chevron(p, new Vector2(1.3f, Loop.At(0).Y), 0f, 1.3f, look != Look.Off ? p.Fg : Alpha(p.Fg, 0.6f), 1.15f);
        if (moving) Tail(p, Loop, u, 0.32f, forward: true);
        Marker(p, Loop.At(u), look);
    }

    private static void DrawLinear(Pen p, Look look, float t)
    {
        bool moving = look == Look.Moving;
        float u = 0.5f, ph = t * 0.42f % 1f;
        if (moving)
        {
            u = ph < 0.5f ? ph * 2f : 2f - ph * 2f;
            u = u * u * (3f - 2f * u);   // eases at each end
        }
        PathLook(p, S, look);
        uint ends = look != Look.Off ? p.Fg : Alpha(p.Fg, 0.6f);
        Chevron(p, S.At(1f) + new Vector2(0, -0.4f), S.AngleAt(0.99f), 1.4f, ends, 1.2f);
        Chevron(p, S.At(0f) + new Vector2(0, 0.4f), S.AngleAt(0.01f) + MathF.PI, 1.4f, ends, 1.2f);
        if (moving) Tail(p, S, u, 0.22f, forward: ph < 0.5f);
        Marker(p, S.At(u), look);
    }

    private static void DrawOnce(Pen p, Look look, float t)
    {
        bool moving = look == Look.Moving;
        float ph = t * 0.4f % 1f;
        float u = moving ? MathF.Min(1f, ph / 0.8f) : 0f;
        bool arrived = moving && u >= 1f;
        PathLook(p, Once, look);
        // The flag at the trail's end: a pole and a pennant, lit while the marker is there.
        Vector2 pole = Once.At(1f) + new Vector2(-0.2f, 0);
        uint fc = look != Look.Off ? p.Fg : Alpha(p.Fg, 0.6f);
        p.Dl.AddLine(p.P(pole), p.P(pole + new Vector2(0, -6f)), fc, 1.15f * p.Line);
        Span<Vector2> flag = stackalloc Vector2[3];
        flag[0] = p.P(pole + new Vector2(0, -6f));
        flag[1] = p.P(pole + new Vector2(3.8f, -4.9f));
        flag[2] = p.P(pole + new Vector2(0, -3.6f));
        if (look != Look.Off) p.Dl.AddConvexPolyFilled(ref flag[0], 3, arrived ? Mix(p.Fg, GreenLight, 0.5f) : p.Fg);
        else p.Dl.AddPolyline(ref flag[0], 3, fc, ImDrawFlags.Closed, p.Line);
        if (arrived && p.Glow) Glow(p.Dl, p.P(pole + new Vector2(1.6f, -4.8f)), 3.6f * p.S, Green, 0.16f * (1f - (ph - 0.8f) / 0.2f));
        if (moving && !arrived) Tail(p, Once, u, 0.25f, forward: true);
        if (!arrived) Marker(p, Once.At(u), look);
    }

    private static void DrawFollow(Pen p, Look look, float t)
    {
        bool on = look != Look.Off, moving = look == Look.Moving;
        // Six footsteps toward the leader, marching while moving.
        float off = moving ? t * 1.2f % 1f : 0f;
        for (int i = 0; i < 6; i++)
        {
            float u = MathF.Min(0.86f, (i + off) / 6f * 0.86f + 0.06f);
            float a = !on ? 0.55f : moving ? 0.35f + 0.65f * MathF.Sin(MathF.Min(1f, u / 0.86f) * MathF.PI) : 0.7f;
            p.Dl.AddCircleFilled(p.P(Fol.At(u)), (on ? 0.75f : 0.55f) * p.S, Alpha(p.Fg, a), 8);
        }
        // The leader: an arrowhead at the end, pointing the way.
        Vector2 lead = Fol.At(1f);
        float la = Fol.AngleAt(0.97f);
        Span<Vector2> kite = stackalloc Vector2[4];
        kite[0] = p.P(lead + Turn(2.7f, 0f, la));
        kite[1] = p.P(lead + Turn(-1.9f, 1.9f, la));
        kite[2] = p.P(lead + Turn(-0.9f, 0f, la));
        kite[3] = p.P(lead + Turn(-1.9f, -1.9f, la));
        if (on)
        {
            if (moving && p.Glow) Glow(p.Dl, p.P(lead), 3.4f * p.S, Green, 0.12f);
            p.Dl.AddConcavePolyFilled(ref kite[0], 4, p.Fg);
        }
        else p.Dl.AddPolyline(ref kite[0], 4, p.Fg, ImDrawFlags.Closed, p.Line);
        // The follower, at the start, never moves: the steps do.
        Marker(p, Fol.At(0f), look == Look.Off ? Look.Off : Look.On);
    }

    private static void DrawPatrol(Pen p, Look look, float t)
    {
        bool moving = look == Look.Moving;
        float u = moving ? t * 0.3f % 1f : 0f;
        PathLook(p, Serp, look);
        if (moving) Tail(p, Serp, u, 0.18f, forward: true);
        Marker(p, Serp.At(u), look);
    }

    private static void DrawHunt(Pen p, Look look, float t)
    {
        const float R = 6.1f;
        if (look == Look.Off)
        {
            uint dot = Alpha(p.Fg, 0.55f);
            for (int i = 0; i < 16; i++) p.Dl.AddCircleFilled(p.P(Dir(i * MathF.Tau / 16f) * R), MathF.Max(0.55f, 0.5f * p.S), dot, 6);
            p.Dl.AddCircle(p.C, 1.1f * p.S, p.Fg, 10, p.Line);
            return;
        }
        bool moving = look == Look.Moving;
        p.Dl.AddCircle(p.C, R * p.S, Alpha(p.Fg, moving ? 0.45f : 0.6f), 32, p.Line);
        for (int k = 0; k < 4; k++)
        {
            Vector2 d = Dir(k * Q);
            p.Dl.AddLine(p.P(d * (R - 1.4f)), p.P(d * (R + 0.9f)), p.Fg, 1.15f * p.Line);
        }
        float sweep = moving ? (t * 0.7f % 1f) * MathF.Tau - Q : -MathF.PI * 0.8f;
        if (moving)
        {
            // The sweep: a fading wedge behind the needle, and two blips that light as it passes.
            const int n = 12;
            const float span = 1.4f;
            for (int j = 0; j < n; j++)
            {
                float a1 = sweep - span * j / n, a0 = sweep - span * (j + 1) / n;
                p.Dl.PathLineTo(p.C);
                p.Dl.PathArcTo(p.C, (R - 0.6f) * p.S, a0, a1, 3);
                p.Dl.PathFillConvex(Alpha(Green, 0.28f * MathF.Pow(1f - j / (float)n, 1.3f)));
            }
            Blip(p, sweep, -MathF.PI * 0.15f, 3.6f);
            Blip(p, sweep, MathF.PI * 0.62f, 4.4f);
        }
        p.Dl.AddLine(p.C, p.P(Dir(sweep) * (R - 1f)), moving ? GreenLight : p.Fg, 1.3f * p.Line);
        p.Dl.AddCircleFilled(p.C, 1.2f * p.S, p.Fg, 10);
    }

    private static void Blip(Pen p, float sweep, float angle, float radius)
    {
        float d = ((sweep - angle) % MathF.Tau + MathF.Tau) % MathF.Tau;   // how far the needle has gone past it
        if (d >= 2.2f) return;
        p.Dl.AddCircleFilled(p.P(Dir(angle) * radius), 1.1f * p.S, Alpha(GreenLight, 1f - d / 2.2f), 8);
    }

    private static void DrawNone(Pen p, Look look)
    {
        bool on = look != Look.Off;
        Dotted(p, S, Alpha(p.Fg, on ? 0.75f : 0.5f));
        uint ends = Alpha(p.Fg, on ? 0.9f : 0.6f);
        p.Dl.AddCircle(p.P(S.At(0f)), 1.35f * p.S, ends, 10, p.Line);
        p.Dl.AddCircle(p.P(S.At(1f)), 1.35f * p.S, ends, 10, p.Line);
    }

    // ── Pieces ──────────────────────────────────────────────────────────

    /// <summary>The path a route kind draws (the footsteps' path for a follow).</summary>
    private static Path PathOf(Kind kind) => kind switch
    {
        Kind.Circular => Loop,
        Kind.Linear => S,
        Kind.Once => Once,
        Kind.Follow => Fol,
        _ => Serp,
    };

    /// <summary>The priority lane under <paramref name="path"/>: a wide faint green band, then a narrower one.</summary>
    private static void Lane(Pen p, Path path, bool moving, float t)
    {
        float a = LaneAlpha + (moving ? LanePulse * MathF.Sin(t * MathF.Tau * LaneHz) : 0f);
        int n = Math.Min(path.Pts.Length, MaxPoints);
        Span<Vector2> pts = stackalloc Vector2[MaxPoints];
        for (int i = 0; i < n; i++) pts[i] = p.P(path.Pts[i]);
        ImDrawFlags flags = path.Closed ? ImDrawFlags.Closed : ImDrawFlags.None;
        p.Dl.AddPolyline(ref pts[0], n, Alpha(Green, a * LaneOuter), flags, LaneOuterW * p.S);
        p.Dl.AddPolyline(ref pts[0], n, Alpha(Green, a), flags, LaneW * p.S);
    }

    /// <summary>The path: dotted when off, solid (fainter while the marker runs) when on.</summary>
    private static void PathLook(Pen p, Path path, Look look)
    {
        if (look == Look.Off) { Dotted(p, path, Alpha(p.Fg, 0.6f)); return; }
        int n = Math.Min(path.Pts.Length, MaxPoints);
        Span<Vector2> pts = stackalloc Vector2[MaxPoints];
        for (int i = 0; i < n; i++) pts[i] = p.P(path.Pts[i]);
        p.Dl.AddPolyline(ref pts[0], n, Alpha(p.Fg, look == Look.Moving ? 0.45f : 0.75f),
            path.Closed ? ImDrawFlags.Closed : ImDrawFlags.None, 1.35f * p.Line);
    }

    private static void Dotted(Pen p, Path path, uint color)
    {
        const float Gap = 1.6f;
        int n = Math.Max(2, (int)MathF.Round(path.Total / Gap));
        int last = path.Closed ? n - 1 : n;
        float r = MathF.Max(0.55f, 0.55f * p.S);
        for (int i = 0; i <= last; i++) p.Dl.AddCircleFilled(p.P(path.At(i / (float)n)), r, color, 6);
    }

    /// <summary>The green tail behind the marker at <paramref name="u"/>, <paramref name="span"/> of the path long.</summary>
    private static void Tail(Pen p, Path path, float u, float span, bool forward)
    {
        const int n = 14;
        for (int j = 0; j < n; j++)
        {
            float a = forward ? u - span * (j + 1) / n : u + span * (j + 1) / n;
            float b = forward ? u - span * j / n : u + span * j / n;
            if (!path.Closed && (forward ? b <= 0f : b >= 1f)) break;
            p.Dl.AddLine(p.P(path.At(a)), p.P(path.At(b)), Alpha(GreenLight, MathF.Pow(1f - j / (float)n, 1.4f)), 1.6f * p.Line);
        }
    }

    /// <summary>The marker: a ring when off, a dot when on, a greener dot with a glow while moving.</summary>
    private static void Marker(Pen p, Vector2 at, Look look)
    {
        Vector2 c = p.P(at);
        if (look == Look.Off) { p.Dl.AddCircle(c, 1.5f * p.S, p.Fg, 12, p.Line); return; }
        bool moving = look == Look.Moving;
        if (moving && p.Glow) Glow(p.Dl, c, 3.6f * p.S, Green, 0.15f);
        p.Dl.AddCircleFilled(c, 1.95f * p.S, moving ? Mix(p.Fg, GreenLight, 0.35f) : p.Fg, 12);
    }

    /// <summary>An open arrowhead with its tip at <paramref name="tip"/>, pointing along <paramref name="angle"/>.</summary>
    private static void Chevron(Pen p, Vector2 tip, float angle, float size, uint color, float width)
    {
        Span<Vector2> pts = stackalloc Vector2[3];
        pts[0] = p.P(tip + Turn(-size, -size, angle));
        pts[1] = p.P(tip);
        pts[2] = p.P(tip + Turn(-size, size, angle));
        p.Dl.AddPolyline(ref pts[0], 3, color, ImDrawFlags.None, width * p.Line);
    }
}

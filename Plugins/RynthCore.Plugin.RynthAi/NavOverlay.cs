using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>Settings ▸ Navigation ▸ "Waypoint Labels" (LegacyUiSettings.NavLabelMode).</summary>
internal static class NavLabelModes
{
    public const int Off = 0, Numbers = 1, Names = 2;
    public static int Clamp(int mode) => mode is >= Off and <= Names ? mode : Off;
}

/// <summary>
/// One tick's waypoint labels and guide line, worked out from the route on the plugin tick and
/// then never changed: the emit step reads only this, never the route's lists. Positions are in
/// the Nav3D frame NavMarkerRenderer uses (X = east metres, Y = height, Z = north metres, in
/// the player's landblock).
/// </summary>
internal sealed class NavOverlayFrame
{
    public static readonly NavOverlayFrame Empty = new(Array.Empty<NavLabel>(), null, 0f, 0f, 0f);

    public NavOverlayFrame(NavLabel[] labels, NavGuide? guide, float playerX, float playerY, float playerZ)
    {
        Labels = labels;
        Guide = guide;
        PlayerX = playerX;
        PlayerY = playerY;
        PlayerZ = playerZ;
    }

    public IReadOnlyList<NavLabel> Labels { get; }
    public NavGuide? Guide { get; }
    public float PlayerX { get; }
    public float PlayerY { get; }
    public float PlayerZ { get; }
}

/// <summary>One text line over a waypoint. Row 0 sits just above the ring; higher rows stack above it.</summary>
internal sealed record NavLabel(float X, float Y, float Z, string Text, int Row, uint Color);

/// <summary>The line from the character's feet to the waypoint nav is heading for.</summary>
internal sealed record NavGuide(float X1, float Y1, float Z1, float X2, float Y2, float Z2, uint Color);

/// <summary>
/// Waypoint labels (numbers, or numbers and what happens there) and the guide line for the Nav3D
/// markers (Silentkelpie's rynth PR #1 idea, done on our pump-thread Nav3D path). Both are off by
/// default. Labels are drawn as filled strokes (Nav3D triangles) on an upright plane turned toward
/// the character, sized up with distance so far labels stay readable; the guide is one Nav3D line.
/// </summary>
internal static class NavOverlay
{
    /// <summary>Labels further than this from the character are left out (metres).</summary>
    public const float LabelRange = 60f;
    /// <summary>At most this many label lines a tick, nearest first (the active point's always first).</summary>
    public const int MaxLabelLines = 24;
    /// <summary>At most this many action lines stacked over one point.</summary>
    public const int MaxActionLines = 3;
    /// <summary>Triangle budget for all labels together (the engine keeps 8192 a tick for every plugin).</summary>
    public const int MaxLabelTriangles = 1600;
    public const int MaxLabelChars = 22;

    public const uint LabelColor = 0xFFE6FFFF;    // near white, a cyan touch like the rings
    public const uint ActiveLabelColor = 0xFFFF6A6A;
    public const uint ActionLabelColor = 0xFFFFC040;
    public const uint GuideColor = 0xFFFFD21E;

    private const float GuideLift = 0.05f;
    private const float MaxGuideLength = 500f;

    // ── Text per route (rebuilt when the route or the mode changes) ──────

    /// <summary>
    /// Label text for every route point, built once per route and mode: <c>Own[i]</c> for a point
    /// with ground coordinates (null otherwise), and the action lines ("13 RECALL ...") hung on
    /// the ground point before them (the point where nav does them).
    /// </summary>
    internal sealed class RouteText
    {
        public readonly object Route;
        public readonly int Count, Mode;
        public readonly string?[] Own;
        public readonly string[]?[] Actions;

        public RouteText(object route, int count, int mode, string?[] own, string[]?[] actions)
        {
            Route = route; Count = count; Mode = mode; Own = own; Actions = actions;
        }
    }

    public static RouteText BuildText(object route, IReadOnlyList<NavPoint> points, int mode, Func<int, string> spellName)
    {
        int n = points.Count;
        var own = new string?[n];
        var actions = new string[]?[n];
        var pending = new List<string>();
        int lastGround = -1;
        for (int i = 0; i < n; i++)
        {
            NavPoint p = points[i];
            if (HasGroundCoords(p.Type))
            {
                own[i] = mode == NavLabelModes.Names ? Fit(GroundName(i, p)) : i.ToString();
                lastGround = i;
                continue;
            }
            if (mode != NavLabelModes.Names || lastGround < 0) continue;
            var list = actions[lastGround] ??= Array.Empty<string>();
            if (list.Length < MaxActionLines)
                actions[lastGround] = Append(list, Fit(ActionName(i, p, spellName)));
            else if (list.Length == MaxActionLines)
                actions[lastGround] = Append(list, "...");
        }
        return new RouteText(route, n, mode, own, actions);
    }

    private static string[] Append(string[] a, string s)
    {
        var b = new string[a.Length + 1];
        Array.Copy(a, b, a.Length);
        b[a.Length] = s;
        return b;
    }

    private static bool HasGroundCoords(NavPointType t) => NavRouteParser.IsPlainWaypoint(t) || t == NavPointType.Jump;

    private static string GroundName(int i, NavPoint p) => p.Type switch
    {
        NavPointType.Checkpoint => $"{i} CHECKPOINT",
        NavPointType.Jump => $"{i} JUMP",
        _ => i.ToString(),
    };

    private static string ActionName(int i, NavPoint p, Func<int, string> spellName) => p.Type switch
    {
        NavPointType.Recall => $"{i} {spellName(p.SpellId)}",
        NavPointType.Pause => $"{i} PAUSE {Math.Max(0, p.PauseTimeMs) / 1000.0:0.#}S",
        NavPointType.Chat => $"{i} CHAT {p.ChatCommand}",
        NavPointType.OpenVendor => $"{i} VENDOR {p.TargetName}",
        NavPointType.PortalNPC => $"{i} PORTAL {p.TargetName}",
        NavPointType.Npc => $"{i} NPC {p.TargetName}",
        _ => $"{i} {p.Type}",
    };

    private static string Fit(string s)
    {
        s = s.Trim().ToUpperInvariant();
        return s.Length > MaxLabelChars ? s[..(MaxLabelChars - 2)] + ".." : s;
    }

    // ── The frame (plugin tick) ───────────────────────────────────────

    /// <summary>
    /// Builds the tick's overlay from <paramref name="text"/> (null: no labels) and the waypoints'
    /// positions (<paramref name="worldOf"/>, only asked for points that have ground coordinates).
    /// <paramref name="active"/> is the point nav is heading for; <paramref name="guide"/> draws the
    /// line to it (the caller passes false when that point has no ground coordinates). Plugin tick only.
    /// </summary>
    public static NavOverlayFrame BuildFrame(RouteText? text, bool guide, int active,
        Func<int, (float X, float Y, float Z)> worldOf, float playerX, float playerY, float playerZ, float lift)
    {
        NavGuide? g = null;
        if (guide && active >= 0)
        {
            var (ax, ay, az) = worldOf(active);
            float dx = ax - playerX, dz = az - playerZ;
            if (dx * dx + dz * dz <= MaxGuideLength * MaxGuideLength)
                g = new NavGuide(playerX, playerY + lift + GuideLift, playerZ, ax, ay, az, GuideColor);
        }

        if (text == null || text.Mode == NavLabelModes.Off)
            return g == null ? NavOverlayFrame.Empty : new NavOverlayFrame(Array.Empty<NavLabel>(), g, playerX, playerY, playerZ);

        // Ground points in range, nearest first; the active point first of all.
        var near = new List<(float D2, int I, float X, float Y, float Z)>();
        float range2 = LabelRange * LabelRange;
        for (int i = 0; i < text.Count; i++)
        {
            if (text.Own[i] == null) continue;
            var (x, y, z) = worldOf(i);
            float dx = x - playerX, dz = z - playerZ;
            float d2 = dx * dx + dz * dz;
            if (d2 > range2) continue;
            near.Add((i == active ? -1f : d2, i, x, y, z));
        }
        near.Sort((a, b) => a.D2.CompareTo(b.D2));

        var labels = new List<NavLabel>(Math.Min(MaxLabelLines, near.Count * 2));
        foreach (var (_, i, x, y, z) in near)
        {
            if (labels.Count >= MaxLabelLines) break;
            labels.Add(new NavLabel(x, y, z, text.Own[i]!, 0, i == active ? ActiveLabelColor : LabelColor));
            string[]? acts = text.Actions[i];
            if (acts == null) continue;
            for (int k = 0; k < acts.Length && labels.Count < MaxLabelLines; k++)
                labels.Add(new NavLabel(x, y, z, acts[k], k + 1, ActionLabelColor));
        }
        return new NavOverlayFrame(labels.ToArray(), g, playerX, playerY, playerZ);
    }

    // ── Emit (plugin tick, into the Nav3D buffer) ──────────────────────

    /// <summary>The guide line, if the frame has one: submitted before the route's lines so a full line buffer never drops it.</summary>
    public static void EmitGuide(NavOverlayFrame frame, INav3DSink sink, float thickness)
    {
        NavGuide? g = frame.Guide;
        if (g != null)
            sink.Line(g.X1, g.Y1, g.Z1, g.X2, g.Y2, g.Z2, thickness, g.Color);
    }

    /// <summary>
    /// Each label as filled strokes (triangles). Returns the number of triangles used (at most
    /// <see cref="MaxLabelTriangles"/>).
    /// </summary>
    public static int EmitLabels(NavOverlayFrame frame, INav3DSink sink)
    {
        int tris = 0;
        foreach (NavLabel l in frame.Labels)
        {
            if (!EmitLabel(l, frame.PlayerX, frame.PlayerZ, sink, ref tris)) break;
        }
        return tris;
    }

    /// <summary>The line height (m) for a label this far from the character.</summary>
    internal static float LineHeight(float distance) => Nav3DText.LineHeight(distance);

    // The text itself is drawn by the shared Nav3DText (Plugins/Shared/Nav3DText.cs), which
    // RynthNav's path markers use too.
    private static bool EmitLabel(NavLabel l, float playerX, float playerZ, INav3DSink sink, ref int tris)
        => Nav3DText.Emit(l.X, l.Y, l.Z, l.Text, l.Row, l.Color, playerX, playerZ, sink, ref tris, MaxLabelTriangles);
}

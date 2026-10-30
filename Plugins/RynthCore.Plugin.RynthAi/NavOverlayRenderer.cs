// NavOverlayRenderer.cs — on-screen navigation aids on top of NavMarkerRenderer's route rings:
//
//   * Waypoint HUD: small movable window with the active waypoint, distance, a direction
//     arrow relative to the character's facing, the next step, remaining route length,
//     breadcrumb / recording status.
//   * Waypoint labels: "#12  34yd" over the next N travel points in the 3D view.
//   * Guide line: player → active waypoint (UB-IT world-overlay "breadcrumb" line).
//   * Breadcrumb trail: NavBreadcrumbTracker samples drawn on the ground, fading with age.
//
// 3D geometry (guide line, trail) is submitted from OnTick through the engine's Nav3D buffer
// (shared budget: 512 lines across all plugins), with an ImGui projected fallback when the
// engine has no Nav3D API. HUD and labels are ImGui and need the overlay.
using System;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

internal sealed class NavOverlayRenderer
{
    /// <summary>Most trail segments submitted per frame (route markers use up to 128 lines).</summary>
    private const int MaxTrailLines = 200;
    /// <summary>Label text sits this far above the waypoint ring, world units.</summary>
    private const float LabelLift = 1.2f;

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly NavBreadcrumbTracker _trail;

    public NavOverlayRenderer(RynthCoreHost host, LegacyUiSettings settings, NavBreadcrumbTracker trail)
    {
        _host = host;
        _settings = settings;
        _trail = trail;
    }

    /// <summary>Player frame shared by every pass (marker world frame: X=EW, Y=up, Z=NS).</summary>
    private readonly record struct Frame(double NS, double EW, float Px, float Py, float Pz, float HeadingDeg);

    private bool TryGetFrame(out Frame f)
    {
        f = default;
        if (_host.HasIsPortaling && _host.IsPortaling()) return false;
        if (!NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew)) return false;
        if (!_host.TryGetPlayerPose(out uint cell, out float px, out float py, out float pz,
                out float qw, out _, out _, out float qz)) return false;
        if ((cell >> 16) == 0) return false;
        // Physics yaw (CCW from north) → compass heading (0 = north, clockwise), as NavigationEngine.
        double yaw = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        f = new Frame(ns, ew, px, py, pz, (float)((-yaw + 720.0) % 360.0));
        return true;
    }

    private Vector3 ToWorld(in Frame f, double ns, double ew, float worldY)
        => new(f.Px + (float)((ew - f.EW) * NavRouteEditing.NavToYards), worldY,
               f.Py + (float)((ns - f.NS) * NavRouteEditing.NavToYards));

    private float WaypointWorldY(NavPoint p) => (float)(p.Z * NavCoordinateHelper.NavZScale) + _settings.NavHeightOffset;

    /// <summary>Active waypoint when it is a travel Point, else null.</summary>
    private NavPoint? ActivePoint(out int index)
    {
        var pts = _settings.CurrentRoute?.Points;
        index = _settings.ActiveNavIndex;
        if (pts == null || index < 0 || index >= pts.Count) return null;
        return pts[index];
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3D (tick thread)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Guide line + breadcrumb trail into the Nav3D buffer. Call from OnTick.</summary>
    public void SubmitNav3D()
    {
        if (!_host.HasNav3D) return;
        try
        {
            if (!TryGetFrame(out var f)) return;
            var o = _settings.NavOverlay;
            float lineThick = _settings.NavLineThickness * 0.01f;

            if (o.TrailVisible())
                ForEachTrailSegment(f, o, (a, b, fade) =>
                    _host.Nav3DAddLine(a.X, a.Y, a.Z, b.X, b.Y, b.Z, lineThick * 0.7f,
                        NavOverlaySettings.ScaleAlpha(o.ColorBreadcrumb, fade)));

            if (o.GuideLineVisible() && ActivePoint(out _) is { Type: NavPointType.Point } ap)
            {
                Vector3 me = new(f.Px, f.Pz + _settings.NavHeightOffset, f.Py);
                Vector3 to = ToWorld(f, ap.NS, ap.EW, WaypointWorldY(ap));
                _host.Nav3DAddLine(me.X, me.Y, me.Z, to.X, to.Y, to.Z, lineThick * 0.8f, o.ColorGuide);
            }
        }
        catch (Exception ex) { RynthLog.Write(LogCat.Navigation, $"NavOverlay(3D): {ex.Message}"); }
    }

    /// <summary>
    /// Newest-first walk of the trail within draw range, capped at <see cref="MaxTrailLines"/>;
    /// no segment crosses a teleport break. <c>fade</c> is 1 for the newest segment.
    /// </summary>
    private void ForEachTrailSegment(in Frame f, NavOverlaySettings o, Action<Vector3, Vector3, float> draw)
    {
        NavCrumb[] trail = _trail.Trail;
        if (trail.Length < 2) return;
        float lift = _settings.NavHeightOffset + 0.03f;
        int drawn = 0, total = Math.Min(trail.Length - 1, MaxTrailLines);
        for (int i = trail.Length - 1; i >= 1 && drawn < MaxTrailLines; i--)
        {
            NavCrumb b = trail[i], a = trail[i - 1];
            if (b.SegmentStart) continue;
            if (NavRouteEditing.DistanceYards(f.NS, f.EW, b.NS, b.EW) > o.BreadcrumbDrawRangeYards) continue;
            float fade = o.BreadcrumbFade ? 1f - 0.8f * drawn / Math.Max(1, total) : 1f;
            draw(ToWorld(f, a.NS, a.EW, a.Z + lift), ToWorld(f, b.NS, b.EW, b.Z + lift), fade);
            drawn++;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ImGui (render thread)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>HUD window, waypoint labels, and the projected fallback for trail/guide. Call inside an ImGui frame.</summary>
    public void Render()
    {
        try
        {
            var o = _settings.NavOverlay;
            var route = _settings.CurrentRoute;
            bool hasRoute = route?.Points is { Count: > 0 };
            bool navigating = _settings.IsMacroRunning && _settings.EnableNavigation;
            bool wantFallback3D = !_host.HasNav3D && (o.TrailVisible() || o.GuideLineVisible());
            bool wantLabels = o.LabelsVisible() && hasRoute;
            bool wantHud = o.ShowHud && (hasRoute || _settings.IsRecordingNav) && (!o.HudOnlyWhileNavigating || navigating);
            if (!wantFallback3D && !wantLabels && !wantHud) return;
            if (!TryGetFrame(out var f)) return;

            if (wantFallback3D || wantLabels) RenderWorldOverlay(f, o, wantLabels, wantFallback3D);
            if (wantHud) RenderHud(f, o);
        }
        catch (Exception ex) { RynthLog.Write(LogCat.Navigation, $"NavOverlay(ImGui): {ex.Message}"); }
    }

    private void RenderWorldOverlay(in Frame f, NavOverlaySettings o, bool labels, bool fallback3D)
    {
        if (!_host.HasWorldToScreen || !_host.HasGetViewportSize) return;
        if (!_host.TryGetViewportSize(out uint vpW, out uint vpH) || vpW == 0 || vpH == 0) return;

        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(vpW, vpH));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = true;
        bool visible = ImGui.Begin("##NavWaypointOverlay", ref open,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings);
        try
        {
            if (!visible) return;
            var dl = ImGui.GetWindowDrawList();
            float thick = Math.Max(1f, _settings.NavLineThickness * 0.5f);
            var frame = f;

            if (fallback3D && o.TrailVisible())
            {
                ForEachTrailSegment(f, o, (a, b, fade) =>
                {
                    if (Project(a, out var sa) && Project(b, out var sb))
                        dl.AddLine(sa, sb, NavOverlaySettings.ArgbToImGui(NavOverlaySettings.ScaleAlpha(o.ColorBreadcrumb, fade)), thick);
                });
            }
            if (fallback3D && o.GuideLineVisible() && ActivePoint(out _) is { Type: NavPointType.Point } ap
                && Project(new Vector3(f.Px, f.Pz + _settings.NavHeightOffset, f.Py), out var sMe)
                && Project(ToWorld(frame, ap.NS, ap.EW, WaypointWorldY(ap)), out var sTo))
                dl.AddLine(sMe, sTo, NavOverlaySettings.ArgbToImGui(o.ColorGuide), thick);

            if (labels) DrawWaypointLabels(dl, f, o);
        }
        finally
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
        }
    }

    private bool Project(Vector3 w, out Vector2 s)
    {
        bool ok = _host.WorldToScreen(w.X, w.Y, w.Z, out float sx, out float sy);
        s = new Vector2(sx, sy);
        return ok;
    }

    /// <summary>"#12  34yd" over the active travel point and the next ones in route order.</summary>
    private void DrawWaypointLabels(ImDrawListPtr dl, in Frame f, NavOverlaySettings o)
    {
        var route = _settings.CurrentRoute;
        var pts = route.Points;
        int n = pts.Count;
        int idx = Math.Clamp(_settings.ActiveNavIndex, 0, n - 1);
        bool circular = route.RouteType == NavRouteType.Circular;
        uint activeCol = NavOverlaySettings.ArgbToImGui(o.ColorActiveRing);
        uint nextCol = NavOverlaySettings.ArgbToImGui(o.ColorRing);
        uint bg = 0xB0000000;

        int labelled = 0;
        for (int step = 0; step < n && labelled < o.LabelsAhead; step++)
        {
            int i = circular ? (idx + step) % n : idx + step;
            if (i >= n) break;
            var p = pts[i];
            if (p.Type != NavPointType.Point) continue;

            Vector3 w = ToWorld(f, p.NS, p.EW, WaypointWorldY(p) + LabelLift);
            if (!Project(w, out var s)) { labelled++; continue; }

            double yd = NavRouteEditing.DistanceYards(f.NS, f.EW, p.NS, p.EW);
            string text = $"#{i}  {yd:F0}yd";
            Vector2 size = ImGui.CalcTextSize(text);
            Vector2 tl = s - new Vector2(size.X * 0.5f + 4, size.Y + 2);
            dl.AddRectFilled(tl, tl + size + new Vector2(8, 4), bg, 3f);
            dl.AddText(tl + new Vector2(4, 2), i == _settings.ActiveNavIndex ? activeCol : nextCol, text);
            labelled++;
        }
    }

    private void RenderHud(in Frame f, NavOverlaySettings o)
    {
        ImGui.SetNextWindowPos(new Vector2(20, 220), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(0.65f);
        if (!ImGui.Begin("Nav HUD##RynthAiNavHud", ref o.ShowHud,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
        {
            ImGui.End();
            return;
        }

        var route = _settings.CurrentRoute;
        var pts = route.Points;
        string name = string.IsNullOrEmpty(_settings.CurrentNavPath) ? "(unsaved)" : Path.GetFileNameWithoutExtension(_settings.CurrentNavPath);
        SafeImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"{name}  [{route.RouteType}]");
        if (_settings.IsRecordingNav)
        {
            ImGui.SameLine();
            SafeImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), $"REC +{_trail.RecordedCount}");
        }

        int idx = _settings.ActiveNavIndex;
        if (pts.Count > 0 && idx >= 0 && idx < pts.Count)
        {
            var p = pts[idx];
            bool travel = p.Type == NavPointType.Point;
            ImGui.BeginGroup();
            SafeImGui.Text($"WP {idx}/{pts.Count - 1}  {TypeLabel(p)}");
            if (travel)
            {
                double yd = NavRouteEditing.DistanceYards(f.NS, f.EW, p.NS, p.EW);
                double dz = (p.Z * NavCoordinateHelper.NavZScale) - f.Pz;
                SafeImGui.Text(Math.Abs(dz) >= 3 ? $"{yd:F1} yd  ({(dz > 0 ? "up" : "down")} {Math.Abs(dz):F0})" : $"{yd:F1} yd");
            }
            else
            {
                SafeImGui.TextDisabled(p.ToString());
            }
            int ni = NextIndex(route, idx);
            if (ni >= 0) SafeImGui.TextDisabled($"Next: [{ni}] {pts[ni]}");
            double remain = RemainingYards(route, idx, f);
            if (remain > 0) SafeImGui.TextDisabled(route.RouteType == NavRouteType.Circular ? $"Loop: {remain:F0} yd" : $"Remaining: ~{remain:F0} yd");
            ImGui.EndGroup();

            if (travel)
            {
                ImGui.SameLine();
                double bearing = Math.Atan2(p.EW - f.EW, p.NS - f.NS) * (180.0 / Math.PI);
                DrawArrow((float)(bearing - f.HeadingDeg), NavOverlaySettings.ArgbToImGui(o.ColorGuide));
            }
        }
        else if (pts.Count == 0)
        {
            ImGui.TextDisabled("Route is empty.");
        }

        if (!string.IsNullOrEmpty(_settings.NavStatusLine))
            SafeImGui.TextColored(_settings.NavIsStuck ? new Vector4(0.91f, 0.70f, 0.20f, 1f) : new Vector4(0.6f, 0.75f, 0.6f, 1f),
                _settings.NavStatusLine);
        if (o.TrackBreadcrumbs)
            SafeImGui.TextDisabled($"Trail: {_trail.Trail.Length} pts, {_trail.TrailYards:F0} yd");
        ImGui.End();
    }

    /// <summary>Direction arrow in a 36 px box; <paramref name="relDeg"/> 0 = straight ahead, clockwise.</summary>
    private static void DrawArrow(float relDeg, uint color)
    {
        const float Box = 36f;
        Vector2 tl = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(Box, Box));
        var dl = ImGui.GetWindowDrawList();
        Vector2 c = tl + new Vector2(Box * 0.5f, Box * 0.5f);
        dl.AddCircle(c, Box * 0.48f, 0x60FFFFFF, 24, 1.5f);

        double a = relDeg * Math.PI / 180.0;
        var dir = new Vector2((float)Math.Sin(a), -(float)Math.Cos(a));
        var perp = new Vector2(-dir.Y, dir.X);
        dl.AddTriangleFilled(c + dir * 14f, c - dir * 8f + perp * 7f, c - dir * 8f - perp * 7f, color);
    }

    private static string TypeLabel(NavPoint p) => p.Type switch
    {
        NavPointType.Point      => "Point",
        NavPointType.Recall     => "Recall",
        NavPointType.Pause      => "Pause",
        NavPointType.Chat       => "Chat",
        NavPointType.OpenVendor => "Vendor",
        NavPointType.PortalNPC  => "Portal",
        NavPointType.Npc        => "NPC",
        _                       => p.Type.ToString(),
    };

    /// <summary>Following waypoint in route order (wraps on circular), or -1 at the end.</summary>
    private static int NextIndex(NavRouteParser route, int idx)
    {
        int n = route.Points.Count;
        if (idx + 1 < n) return idx + 1;
        return route.RouteType == NavRouteType.Circular && n > 1 ? 0 : -1;
    }

    /// <summary>Player → active point → … end of route (one lap for circular), walking legs only.</summary>
    private static double RemainingYards(NavRouteParser route, int idx, in Frame f)
    {
        var pts = route.Points;
        int n = pts.Count;
        if (idx < 0 || idx >= n) return 0;
        double total = 0;
        NavPoint? prev = null;
        int steps = route.RouteType == NavRouteType.Circular ? n : n - idx;
        for (int s = 0; s < steps; s++)
        {
            var p = pts[(idx + s) % n];
            if (p.Type != NavPointType.Point) { prev = null; continue; }
            total += prev == null
                ? (s == 0 ? NavRouteEditing.DistanceYards(f.NS, f.EW, p.NS, p.EW) : 0)
                : NavRouteEditing.DistanceYards(prev.NS, prev.EW, p.NS, p.EW);
            prev = p;
        }
        return total;
    }
}

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Draws navigation route markers using the engine's 3D rendering API.
/// Rings are real D3D9 geometry (flat annuli on the ground) with natural
/// perspective distortion — they look 3D like Utility Belt's D3DObj.
/// Connecting lines are also 3D quads lying on the ground.
///
/// Falls back to ImGui 2D line rendering if the 3D API is unavailable.
/// </summary>
internal sealed class NavMarkerRenderer
{
    // Colors — ARGB format for D3D9 (0xAARRGGBB)
    private const uint ColorCyan3D = 0xFF00FFFF;
    private const uint ColorRed3D  = 0xFFFF4444;
    private const uint ColorLine3D = 0xFF0088FF;

    // Colors — ABGR format for ImGui fallback
    private const uint ColorCyanImGui = 0xFFFFFF00;
    private const uint ColorRedImGui  = 0xFF4444FF;
    private const uint ColorLineImGui = 0xFFFF8800;

    // Ring geometry for ImGui fallback
    private const int RingSegments = 32;
    private static readonly float[] _cos = new float[RingSegments];
    private static readonly float[] _sin = new float[RingSegments];

    static NavMarkerRenderer()
    {
        for (int i = 0; i < RingSegments; i++)
        {
            double a = 2.0 * Math.PI * i / RingSegments;
            _cos[i] = (float)Math.Cos(a);
            _sin[i] = (float)Math.Sin(a);
        }
    }

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private int _frameCount;

    public NavMarkerRenderer(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    // Pre-allocated arrays for ImGui fallback
    private const int MaxMarkers = 128;
    private readonly Vector2[] _centerScreen = new Vector2[MaxMarkers];
    private readonly bool[] _centerVis = new bool[MaxMarkers];
    private readonly float[] _centerDepth = new float[MaxMarkers];

    public void Render()
    {
        try { RenderCore(); }
        catch (Exception ex) { _host.Log($"NavMarkers: {ex.Message}"); }
    }

    private void RenderCore()
    {
        _frameCount++;
        bool doLog = (_frameCount % 300 == 1);

        if (doLog)
            _host.Log($"NavMarkers: HasNav3D={_host.HasNav3D} version={_host.Version}");

        var route = _settings.CurrentRoute;
        if (route?.Points == null || route.Points.Count == 0)
            return;

        if (!NavCoordinateHelper.TryGetNavCoords(_host, out double playerNS, out double playerEW))
            return;

        if (!_host.TryGetPlayerPose(out uint cellId, out float px, out float py, out float pz,
                out _, out _, out _, out _))
            return;

        // Don't paint during portal animation — portalspace has landblock 0x0000
        if ((cellId >> 16) == 0)
            return;

        float ringRadius = Math.Max(0.1f, _settings.FollowNavMin);
        float heightOffset = _settings.NavHeightOffset;
        int count = Math.Min(route.Points.Count, MaxMarkers);

        if (_host.HasNav3D)
        {
            Render3D(route, count, px, py, pz, playerNS, playerEW, ringRadius, heightOffset, doLog);
        }
        else
        {
            RenderImGuiFallback(route, count, px, py, pz, playerNS, playerEW, ringRadius, heightOffset, doLog);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3D rendering path — real D3D9 geometry via engine API
    // ═══════════════════════════════════════════════════════════════════

    private void Render3D(NavRouteParser route, int count, float px, float py, float pz,
        double playerNS, double playerEW, float ringRadius, float heightOffset, bool doLog)
    {
        // Ring thickness in world units (the band width of the annulus)
        float ringThick = _settings.NavRingThickness * 0.02f;  // Scale from UI units to world units
        float lineThick = _settings.NavLineThickness * 0.01f;

        // ── Pass 1: Submit rings ─────────────────────────────────────
        // Store world positions for line pass
        float[] wxArr = new float[count];
        float[] wyArr = new float[count];
        float[] wzArr = new float[count];
        bool[] validArr = new bool[count];

        for (int i = 0; i < count; i++)
        {
            var pt = route.Points[i];
            if (pt.Type != NavPointType.Point &&
                pt.Type != NavPointType.Recall &&
                pt.Type != NavPointType.PortalNPC)
                continue;

            // D3D coords: X=EW, Y=height(up), Z=NS
            float wx = px + (float)((pt.EW - playerEW) * 240.0);
            float wy = RouteHeightToWorldY((float)pt.Z, heightOffset);
            float wz = py + (float)((pt.NS - playerNS) * 240.0);

            wxArr[i] = wx; wyArr[i] = wy; wzArr[i] = wz;
            validArr[i] = true;

            bool isActive = (i == _settings.ActiveNavIndex);
            uint color = isActive ? ColorRed3D : ColorCyan3D;
            float thick = isActive ? ringThick * 1.3f : ringThick;

            _host.Nav3DAddRing(wx, wy, wz, ringRadius, thick, color);

            if (doLog && i == 0)
                _host.Log($"NavMarkers3D: wp0 world=({wx:F1},{wy:F1},{wz:F1}) r={ringRadius:F2} t={thick:F3}");
        }

        // ── Pass 2: Submit connecting lines ──────────────────────────
        for (int i = 0; i < count; i++)
        {
            if (!validArr[i]) continue;
            int next = NextVisualIdx(i, route);
            if (next < 0 || next >= count || !validArr[next]) continue;

            _host.Nav3DAddLine(wxArr[i], wyArr[i], wzArr[i],
                               wxArr[next], wyArr[next], wzArr[next],
                               lineThick, ColorLine3D);
        }

        if (doLog)
            _host.Log($"NavMarkers3D: submitted {count} markers");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ImGui fallback path — 2D projected lines (original implementation)
    // ═══════════════════════════════════════════════════════════════════

    private void RenderImGuiFallback(NavRouteParser route, int count, float px, float py, float pz,
        double playerNS, double playerEW, float ringRadius, float heightOffset, bool doLog)
    {
        if (!_host.HasWorldToScreen || !_host.HasGetViewportSize)
            return;

        if (!_host.TryGetViewportSize(out uint vpW, out uint vpH) || vpW == 0 || vpH == 0)
            return;

        // Fullscreen transparent overlay window
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(vpW, vpH));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        bool open = true;
        if (!ImGui.Begin("##NavOverlay", ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();
            return;
        }

        var drawList = ImGui.GetWindowDrawList();

        // ── Pass 1: project waypoint centers ─────────────────────────
        int projected = 0;
        for (int i = 0; i < count; i++)
        {
            var pt = route.Points[i];
            _centerVis[i] = false;

            if (pt.Type != NavPointType.Point &&
                pt.Type != NavPointType.Recall &&
                pt.Type != NavPointType.PortalNPC)
                continue;

            float wx = px + (float)((pt.EW - playerEW) * 240.0);
            float wy = RouteHeightToWorldY((float)pt.Z, heightOffset);
            float wz = py + (float)((pt.NS - playerNS) * 240.0);

            if (_host.WorldToScreen(wx, wy, wz, out float sx, out float sy))
            {
                _centerScreen[i] = new Vector2(sx, sy);
                _centerVis[i] = true;

                float dx = wx - px, dy = wy - pz, dz = wz - py;
                _centerDepth[i] = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                projected++;
            }
        }

        // ── Pass 2: connecting lines ─────────────────────────────────
        float baseLineThick = Math.Max(1f, _settings.NavLineThickness);
        for (int i = 0; i < count; i++)
        {
            if (!_centerVis[i]) continue;
            int next = NextVisualIdx(i, route);
            if (next < 0 || next >= count || !_centerVis[next]) continue;

            float avgD = (_centerDepth[i] + _centerDepth[next]) * 0.5f;
            float thick = Math.Clamp(baseLineThick * 60f / Math.Max(avgD, 1f), baseLineThick * 0.5f, baseLineThick * 2f);
            drawList.AddLine(_centerScreen[i], _centerScreen[next], ColorLineImGui, thick);
        }

        // ── Pass 3: 3D ground rings at each waypoint ────────────────
        float[] rsx = new float[RingSegments];
        float[] rsy = new float[RingSegments];
        bool[] rvis = new bool[RingSegments];
        float baseRingThick = Math.Max(1f, _settings.NavRingThickness);

        for (int i = 0; i < count; i++)
        {
            var pt = route.Points[i];
            if (pt.Type != NavPointType.Point &&
                pt.Type != NavPointType.Recall &&
                pt.Type != NavPointType.PortalNPC)
                continue;

            float cx = px + (float)((pt.EW - playerEW) * 240.0);
            float cy = RouteHeightToWorldY((float)pt.Z, heightOffset);
            float cz = py + (float)((pt.NS - playerNS) * 240.0);

            int visCount = 0;
            for (int s = 0; s < RingSegments; s++)
            {
                float rwx = cx + _cos[s] * ringRadius;
                float rwz = cz + _sin[s] * ringRadius;
                rvis[s] = _host.WorldToScreen(rwx, cy, rwz, out rsx[s], out rsy[s]);
                if (rvis[s]) visCount++;
            }

            if (visCount < 2) continue;

            bool isActive = (i == _settings.ActiveNavIndex);
            uint color = isActive ? ColorRedImGui : ColorCyanImGui;
            float ringThick = isActive ? baseRingThick * 1.3f : baseRingThick;

            for (int s = 0; s < RingSegments; s++)
            {
                int next = (s + 1) % RingSegments;
                if (rvis[s] && rvis[next])
                {
                    drawList.AddLine(
                        new Vector2(rsx[s], rsy[s]),
                        new Vector2(rsx[next], rsy[next]),
                        color, ringThick);
                }
            }

            // Index label above the ring
            if (_centerVis[i])
            {
                string label = i.ToString();
                var sz = ImGui.CalcTextSize(label);
                drawList.AddText(_centerScreen[i] - new Vector2(sz.X * 0.5f, 16f), color, label);
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private static int NextVisualIdx(int i, NavRouteParser route)
    {
        int next = i + 1;
        if (next >= route.Points.Count)
            return route.RouteType == NavRouteType.Circular ? 0 : -1;
        return next;
    }

    private static float RouteHeightToWorldY(float routeZ, float heightOffset)
        => routeZ * 240.0f + heightOffset;
}

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

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
    private const uint ColorCyan3D   = 0xFF00FFFF;
    private const uint ColorRed3D    = 0xFFFF4444;
    private const uint ColorLine3D   = 0xFF0088FF;
    private const uint ColorPortal3D = 0xFFFF44FF;  // magenta — live portal target

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

    /// <summary>The raycast geometry (dungeon cells, terrain), once loaded; lines follow the floor with it.</summary>
    internal Func<Raycasting.GeometryLoader?>? GeometrySource { get; set; }

    // Floor-following lines: a straight segment between two points on a slope or stairs
    // cut through the floor. Each segment is split into ~1.5 m steps set on the floor
    // below them (dungeon floor by a down-ray, terrain height outside). Worked out once per
    // route and landblock, cached by segment.
    private const float FloorStep = 1.5f;
    private object? _floorRoute;
    private int _floorRouteCount;
    private uint _floorBlock;
    private readonly System.Collections.Generic.Dictionary<(int, int), float[]> _floorCache = new();
    private uint _frameCell;

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

    /// <summary>
    /// Submits 3D ring + line geometry to the engine's Nav3D buffer.
    /// Safe to call from OnTick — does not touch ImGui state. No-op when
    /// the engine doesn't expose the 3D nav API.
    /// </summary>
    public void SubmitNav3D()
    {
        if (!_host.HasNav3D)
            return;
        try
        {
            if (!TryPrepareFrame(out var route, out int winStart, out int count, out float px, out float py, out float pz,
                    out double playerNS, out double playerEW, out float ringRadius, out float heightOffset))
                return;

            Render3D(route, winStart, count, px, py, pz, playerNS, playerEW, ringRadius, heightOffset);
        }
        catch (Exception ex) { _host.Log($"NavMarkers(3D): {ex.Message}"); }
    }

    /// <summary>
    /// Draws the ImGui-projected fallback (2D rings + lines). Only used
    /// when the engine doesn't expose the 3D nav API. Must be called from
    /// inside an ImGui frame.
    /// </summary>
    public void RenderImGuiFallback()
    {
        if (_host.HasNav3D)
            return;
        try
        {
            if (!TryPrepareFrame(out var route, out int winStart, out int count, out float px, out float py, out float pz,
                    out double playerNS, out double playerEW, out float ringRadius, out float heightOffset))
                return;

            RenderImGuiFallback(route, winStart, count, px, py, pz, playerNS, playerEW, ringRadius, heightOffset);
        }
        catch (Exception ex) { _host.Log($"NavMarkers(ImGui): {ex.Message}"); }
    }

    private bool TryPrepareFrame(out NavRouteParser route, out int winStart, out int count,
        out float px, out float py, out float pz,
        out double playerNS, out double playerEW,
        out float ringRadius, out float heightOffset)
    {
        _frameCount++;
        route = null!; winStart = 0; count = 0;
        px = py = pz = 0f;
        playerNS = playerEW = 0.0;
        ringRadius = 0f; heightOffset = 0f;

        // Route overlay switched off (Nav panel / "/ra navoverlay off"): no rings or lines.
        if (!_settings.NavOverlay.ShowRouteMarkers)
            return false;

        var r = _settings.CurrentRoute;
        if (r?.Points == null || r.Points.Count == 0)
            return false;

        if (!NavCoordinateHelper.TryGetNavCoords(_host, out playerNS, out playerEW))
            return false;

        if (!_host.TryGetPlayerPose(out uint cellId, out px, out py, out pz,
                out _, out _, out _, out _))
            return false;
        _frameCell = cellId;

        // Don't paint during portal animation — portalspace has landblock 0x0000,
        // but also check IsPortaling() for the brief window before cellId zeroes out.
        if ((cellId >> 16) == 0)
            return false;
        if (_host.HasIsPortaling && _host.IsPortaling())
            return false;

        route = r;
        // The ring is the nav point reach: same floor as NavigationEngine.ArrivalYards (rings were drawn
        // down to 0.1 while nav still went to 1.5). Doorway points use their own tighter reach (pass 1).
        ringRadius = Math.Max(LegacyUiSettings.FollowNavMinLowest, _settings.FollowNavMin);
        heightOffset = _settings.NavHeightOffset;

        // Render a contiguous window of the route centered on the active
        // waypoint rather than the first MaxMarkers points. Rings and lines are
        // per-primitive D3D9 draw calls (not batched), so an unbounded route
        // would flood EndScene — but a player-centered window keeps the markers
        // visible wherever you patrol while the draw-call count stays bounded.
        // (Was: first-MaxMarkers truncation, which left long stretches of a
        // 300+ waypoint dungeon route — ordered by DFS, so visually patchy —
        // with no markers at all.)
        int n = r.Points.Count;
        count = Math.Min(n, MaxMarkers);
        if (n <= count)
        {
            winStart = 0;
        }
        else
        {
            int active = WindowCenter(r, playerNS, playerEW);
            int half = count / 2;
            if (r.RouteType == NavRouteType.Circular)
                winStart = ((active - half) % n + n) % n;
            else
                winStart = Math.Clamp(active - half, 0, n - count);
        }
        return true;
    }

    private int _nearestIdx = -1;
    private int _nearestFrame;
    private NavRouteParser? _nearestRoute;

    /// <summary>
    /// Route index the marker window is centred on: the active waypoint while it's
    /// near the player, otherwise the waypoint nearest the player. With the macro
    /// off after a recall, the active waypoint can be far away, which left a long
    /// route's markers drawn only around that distant spot and none nearby.
    /// </summary>
    private int WindowCenter(NavRouteParser r, double playerNS, double playerEW)
    {
        int n = r.Points.Count;
        int active = _settings.ActiveNavIndex;
        if (active < 0 || active >= n) active = 0;
        var ap = r.Points[active];
        double adN = ap.NS - playerNS, adE = ap.EW - playerEW;
        if (!HasGroundCoords(ap.Type) || Math.Sqrt(adN * adN + adE * adE) * 240.0 <= NearActiveYards)
            return active;

        if (!ReferenceEquals(r, _nearestRoute) || _frameCount - _nearestFrame >= 15 || _nearestIdx >= n)
        {
            _nearestRoute = r;
            _nearestFrame = _frameCount;
            _nearestIdx = active;
            double best = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var p = r.Points[i];
                if (!HasGroundCoords(p.Type)) continue;
                double dN = p.NS - playerNS, dE = p.EW - playerEW;
                double d = dN * dN + dE * dE;
                if (d < best) { best = d; _nearestIdx = i; }
            }
        }
        return _nearestIdx;
    }

    private const double NearActiveYards = 60.0;

    // Waypoints whose coordinates are real ground positions (drawn as markers).
    // Recall/Portal/Chat/Pause carry placeholder coordinates and are skipped.
    private static bool HasGroundCoords(NavPointType t)
        => NavRouteParser.IsPlainWaypoint(t) || t == NavPointType.Jump;

    // Absolute route index of the k-th point in the render window. The modulo
    // wraps circular routes; for non-circular routes winStart is clamped so the
    // window never runs off the end and the modulo is a no-op.
    private static int WindowAbsIdx(int winStart, int k, int n) => (winStart + k) % n;

    // Next window slot to connect a line to. Stays within the window (contiguous
    // route points), and only wraps to slot 0 when the window IS the whole route
    // (small circular route) — never draws a long wrap line across a partial
    // window.
    private static int NextInWindow(int k, int count, int n, bool circular)
    {
        if (k + 1 < count) return k + 1;
        if (count == n && circular) return 0;
        return -1;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3D rendering path — real D3D9 geometry via engine API
    // ═══════════════════════════════════════════════════════════════════

    private void Render3D(NavRouteParser route, int winStart, int count, float px, float py, float pz,
        double playerNS, double playerEW, float ringRadius, float heightOffset)
    {
        // Ring thickness in world units (the band width of the annulus)
        float ringThick = _settings.NavRingThickness * 0.02f;  // Scale from UI units to world units
        float lineThick = _settings.NavLineThickness * 0.01f;

        int n = route.Points.Count;
        bool circular = route.RouteType == NavRouteType.Circular;

        // ── Waypoint labels and guide line (both off by default) ─────
        // Worked out from the route here, on the plugin tick, into an immutable frame; the
        // emit steps read only that frame. The guide goes in first so the route's lines can't
        // crowd it out of the engine's line buffer; the labels go in last (pass 4).
        NavOverlayFrame overlay = BuildOverlay(route, px, py, pz, playerNS, playerEW, heightOffset);
        NavOverlay.EmitGuide(overlay, _sink ??= new HostNav3DSink(_host), lineThick);

        // ── Pass 1: Submit rings ─────────────────────────────────────
        // Arrays are indexed by window slot k (0..count), not absolute route
        // index — the absolute index can run far past count on a long route.
        float[] wxArr = new float[count];
        float[] wyArr = new float[count];
        float[] wzArr = new float[count];
        bool[] validArr = new bool[count];

        for (int k = 0; k < count; k++)
        {
            int i = WindowAbsIdx(winStart, k, n);
            var pt = route.Points[i];
            // Only draw markers at real travel Points. Action waypoints
            // (PortalNPC / Recall / Chat / Pause) fire in place and carry
            // placeholder coordinates (a PortalNPC coord pointing "to the
            // abyss" is the recurring case), so a ring drawn there floats far
            // off in empty space.
            if (!HasGroundCoords(pt.Type))
                continue;

            // D3D coords: X=EW, Y=height(up), Z=NS
            float wx = px + (float)((pt.EW - playerEW) * 240.0);
            float wy = ResolveMarkerWorldY(route, i, heightOffset);
            float wz = py + (float)((pt.NS - playerNS) * 240.0);

            wxArr[k] = wx; wyArr[k] = wy; wzArr[k] = wz;
            validArr[k] = true;

            bool isActive = (i == _settings.ActiveNavIndex);
            uint color = isActive ? ColorRed3D : ColorCyan3D;
            float thick = isActive ? ringThick * 1.3f : ringThick;

            float reach = pt.Doorway ? Math.Min(ringRadius, (float)NavigationEngine.DoorwayReachYd) : ringRadius;
            _host.Nav3DAddRing(wx, wy, wz, reach, thick, color);

        }

        // ── Pass 2: Submit connecting lines (consecutive Points only) ──
        // validArr is true only for Point waypoints, so a non-Point between two
        // Points (portal / recall / chat / pause) leaves the next slot invalid
        // and BREAKS the line there. That is correct: you teleport across a
        // portal, you don't walk — so no segment should span it (this is what
        // produced the "line to the abyss", whether to the portal's placeholder
        // coord or onward to the post-teleport destination point).
        PrepareFloorCache(route);
        for (int k = 0; k < count; k++)
        {
            if (!validArr[k]) continue;
            int nk = NextInWindow(k, count, n, circular);
            if (nk < 0 || nk >= count || !validArr[nk]) continue;

            float[]? heights = FloorHeights(WindowAbsIdx(winStart, k, n), WindowAbsIdx(winStart, nk, n),
                                            wxArr[k], wyArr[k], wzArr[k], wxArr[nk], wyArr[nk], wzArr[nk], heightOffset);
            if (heights == null)
            {
                _host.Nav3DAddLine(wxArr[k], wyArr[k], wzArr[k],
                                   wxArr[nk], wyArr[nk], wzArr[nk],
                                   lineThick, ColorLine3D);
                continue;
            }
            // heights[s] is the line's height at step s of heights.Length-1 equal steps.
            int steps = heights.Length - 1;
            for (int s = 0; s < steps; s++)
            {
                float t0 = (float)s / steps, t1 = (float)(s + 1) / steps;
                _host.Nav3DAddLine(
                    wxArr[k] + (wxArr[nk] - wxArr[k]) * t0, heights[s],     wzArr[k] + (wzArr[nk] - wzArr[k]) * t0,
                    wxArr[k] + (wxArr[nk] - wxArr[k]) * t1, heights[s + 1], wzArr[k] + (wzArr[nk] - wzArr[k]) * t1,
                    lineThick, ColorLine3D);
            }
        }

        // ── Pass 3: live portal marker + line ────────────────────────
        // While the engine is actively using a portal/NPC it publishes the
        // resolved object id (the waypoint's own coord is a placeholder, so we
        // can't use it). Draw a distinct magenta ring at the portal's REAL
        // position plus a line from the player to it — a visible "walk in here".
        uint portalId = _settings.ActivePortalObjId;
        if (portalId != 0 &&
            _host.HasGetObjectPosition &&
            _host.TryGetObjectPosition(portalId, out uint pcell, out float ppx, out float ppy, out float ppz) &&
            NavCoordinateHelper.TryConvertPoseToCoords(pcell, ppx, ppy, out double portNS, out double portEW))
        {
            float pwx = px + (float)((portEW - playerEW) * 240.0);
            float pwz = py + (float)((portNS - playerNS) * 240.0);
            float pwy = ppz + heightOffset;   // portal ground height (same frame as marker Z)

            _host.Nav3DAddRing(pwx, pwy, pwz, ringRadius, ringThick * 1.4f, ColorPortal3D);
            _host.Nav3DAddLine(px, pwy, py, pwx, pwy, pwz, lineThick, ColorPortal3D);
        }

        // ── Pass 4: waypoint labels (filled strokes, Nav3D triangles) ─
        if (overlay.Labels.Count > 0 && _host.HasNav3DTriangle)
            NavOverlay.EmitLabels(overlay, _sink);
    }

    // ── Labels and guide line ────────────────────────────────────────
    private NavOverlay.RouteText? _labelText;
    private INav3DSink? _sink;

    /// <summary>
    /// The tick's labels and guide line from the route and the two settings (Navigation ▸
    /// Waypoint Labels / Guide Line). Plugin tick only; returns an immutable frame.
    /// </summary>
    internal NavOverlayFrame BuildOverlay(NavRouteParser route, float px, float py, float pz,
        double playerNS, double playerEW, float heightOffset)
    {
        int mode = NavLabelModes.Clamp(_settings.NavLabelMode);
        bool guideOn = _settings.NavGuideLine;
        if (mode == NavLabelModes.Off && !guideOn)
        {
            _labelText = null;
            return NavOverlayFrame.Empty;
        }

        var points = route.Points;
        int n = points.Count;
        NavOverlay.RouteText? text = null;
        if (mode != NavLabelModes.Off)
        {
            // Rebuilt only when the route, its length or the mode change (an edit in place keeps
            // the count: the Nav panel's edits replace the route or change its length).
            text = _labelText;
            if (text == null || !ReferenceEquals(text.Route, route) || text.Count != n || text.Mode != mode)
                _labelText = text = NavOverlay.BuildText(route, points, mode, SpellDatabase.GetSpellNameOrId);
        }
        else _labelText = null;

        int active = _settings.ActiveNavIndex;
        bool guide = guideOn && active >= 0 && active < n && HasGroundCoords(points[active].Type);

        (float X, float Y, float Z) WorldOf(int i)
        {
            var pt = points[i];
            return (px + (float)((pt.EW - playerEW) * 240.0),
                    ResolveMarkerWorldY(route, i, heightOffset),
                    py + (float)((pt.NS - playerNS) * 240.0));
        }

        // D3D frame: X = EW, Y = height, Z = NS; the player's height is pose z.
        return NavOverlay.BuildFrame(text, guide, active, WorldOf, px, pz, py, heightOffset);
    }

    /// <summary>The engine's Nav3D buffer.</summary>
    private sealed class HostNav3DSink : INav3DSink
    {
        private readonly RynthCoreHost _host;
        public HostNav3DSink(RynthCoreHost host) => _host = host;
        public void Line(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb)
            => _host.Nav3DAddLine(x1, y1, z1, x2, y2, z2, thickness, colorArgb);
        public void Triangle(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint colorArgb)
            => _host.Nav3DAddTriangle(x1, y1, z1, x2, y2, z2, x3, y3, z3, colorArgb);
    }

    private void PrepareFloorCache(NavRouteParser route)
    {
        uint block = _frameCell >> 16;
        if (!ReferenceEquals(route, _floorRoute) || route.Points.Count != _floorRouteCount || block != _floorBlock)
        {
            _floorCache.Clear();
            _floorRoute = route;
            _floorRouteCount = route.Points.Count;
            _floorBlock = block;
        }
    }

    /// <summary>
    /// Line heights at equal steps from point a to point b following the floor, or null when
    /// the segment is short, flat, or no floor data is available (draw it straight). The frame
    /// here is the player's landblock: x = EW metres, y = height, z = NS metres.
    /// </summary>
    private float[]? FloorHeights(int ia, int ib, float ax, float ay, float az, float bx, float by, float bz, float lift)
    {
        if (_floorCache.TryGetValue((ia, ib), out float[]? cached)) return cached.Length == 0 ? null : cached;

        float[]? result = ComputeFloorHeights(ax, ay, az, bx, by, bz, lift);
        _floorCache[(ia, ib)] = result ?? Array.Empty<float>();
        return result;
    }

    private float[]? ComputeFloorHeights(float ax, float ay, float az, float bx, float by, float bz, float lift)
    {
        var geo = GeometrySource?.Invoke();
        if (geo == null) return null;
        float dx = bx - ax, dz = bz - az;
        float run = MathF.Sqrt(dx * dx + dz * dz);
        if (run < FloorStep * 1.5f) return null;

        uint cell = _frameCell;
        bool dungeon = (cell & 0xFFFF) >= 0x0100;
        float baseX = ((cell >> 24) & 0xFF) * 192f, baseY = ((cell >> 16) & 0xFF) * 192f;
        System.Collections.Generic.List<Raycasting.BoundingVolume>? geometry = dungeon ? geo.GetLandblockGeometry(cell) : null;
        if (dungeon && (geometry == null || geometry.Count == 0)) return null;

        int steps = Math.Clamp((int)MathF.Ceiling(run / FloorStep), 2, 64);
        var heights = new float[steps + 1];
        bool anyFloor = false;
        for (int s = 0; s <= steps; s++)
        {
            float t = (float)s / steps;
            float lineY = ay + (by - ay) * t;               // straight-line height (already lifted)
            float gx = baseX + ax + dx * t, gy = baseY + az + dz * t;
            float floor = dungeon ? DungeonFloor(geometry!, gx, gy, lineY - lift) : geo.GetTerrainZWorld(gx, gy);
            // Only snap to a floor near the straight line: never to a floor on another level.
            if (!float.IsNaN(floor) && MathF.Abs(floor + lift - lineY) < 4f)
            {
                heights[s] = floor + lift;
                anyFloor = true;
            }
            else heights[s] = lineY;
        }
        // Keep the ends on the rings.
        heights[0] = ay;
        heights[steps] = by;
        return anyFloor ? heights : null;
    }

    /// <summary>Highest floor at or below (gx, gy, nearZ + 2): a down-ray against the dungeon's cell geometry.</summary>
    private static float DungeonFloor(System.Collections.Generic.List<Raycasting.BoundingVolume> geometry, float gx, float gy, float nearZ)
    {
        var origin = new Raycasting.Vector3(gx, gy, nearZ + 2f);
        var down = new Raycasting.Vector3(0, 0, -1);
        float best = float.MaxValue;
        foreach (var v in geometry)
        {
            if (v.IsDoor) continue;
            if (v.RayIntersect(origin, down, 6f, out float d) && d < best) best = d;
        }
        return best == float.MaxValue ? float.NaN : origin.Z - best;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ImGui fallback path — 2D projected lines (original implementation)
    // ═══════════════════════════════════════════════════════════════════

    private void RenderImGuiFallback(NavRouteParser route, int winStart, int count, float px, float py, float pz,
        double playerNS, double playerEW, float ringRadius, float heightOffset)
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

        int n = route.Points.Count;
        bool circular = route.RouteType == NavRouteType.Circular;

        // ── Pass 1: project waypoint centers ─────────────────────────
        // Per-frame arrays are indexed by window slot k (0..count); the active
        // waypoint's absolute route index i is used only for route lookups and
        // the on-screen index label.
        int projected = 0;
        for (int k = 0; k < count; k++)
        {
            int i = WindowAbsIdx(winStart, k, n);
            var pt = route.Points[i];
            _centerVis[k] = false;

            // Only draw markers at real travel Points. Action waypoints
            // (PortalNPC / Recall / Chat / Pause) fire in place and carry
            // placeholder coordinates (a PortalNPC coord pointing "to the
            // abyss" is the recurring case), so a ring drawn there floats far
            // off in empty space.
            if (!HasGroundCoords(pt.Type))
                continue;

            float wx = px + (float)((pt.EW - playerEW) * 240.0);
            float wy = ResolveMarkerWorldY(route, i, heightOffset);
            float wz = py + (float)((pt.NS - playerNS) * 240.0);

            if (_host.WorldToScreen(wx, wy, wz, out float sx, out float sy))
            {
                _centerScreen[k] = new Vector2(sx, sy);
                _centerVis[k] = true;

                float dx = wx - px, dy = wy - pz, dz = wz - py;
                _centerDepth[k] = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                projected++;
            }
        }

        // ── Pass 2: connecting lines (consecutive Points only) ───────
        // _centerVis is set only for Point waypoints, so a non-Point between two
        // Points breaks the line there (no segment spans a teleport).
        float baseLineThick = Math.Max(1f, _settings.NavLineThickness);
        for (int k = 0; k < count; k++)
        {
            if (!_centerVis[k]) continue;
            int nk = NextInWindow(k, count, n, circular);
            if (nk < 0 || nk >= count || !_centerVis[nk]) continue;

            float avgD = (_centerDepth[k] + _centerDepth[nk]) * 0.5f;
            float thick = Math.Clamp(baseLineThick * 60f / Math.Max(avgD, 1f), baseLineThick * 0.5f, baseLineThick * 2f);
            drawList.AddLine(_centerScreen[k], _centerScreen[nk], ColorLineImGui, thick);
        }

        // ── Pass 3: 3D ground rings at each waypoint ────────────────
        float[] rsx = new float[RingSegments];
        float[] rsy = new float[RingSegments];
        bool[] rvis = new bool[RingSegments];
        float baseRingThick = Math.Max(1f, _settings.NavRingThickness);

        for (int k = 0; k < count; k++)
        {
            int i = WindowAbsIdx(winStart, k, n);
            var pt = route.Points[i];
            // Only draw markers at real travel Points. Action waypoints
            // (PortalNPC / Recall / Chat / Pause) fire in place and carry
            // placeholder coordinates (a PortalNPC coord pointing "to the
            // abyss" is the recurring case), so a ring drawn there floats far
            // off in empty space.
            if (!HasGroundCoords(pt.Type))
                continue;

            float cx = px + (float)((pt.EW - playerEW) * 240.0);
            float cy = ResolveMarkerWorldY(route, i, heightOffset);
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

            // Index label above the ring (absolute route index)
            if (_centerVis[k])
            {
                string label = i.ToString();
                var sz = ImGui.CalcTextSize(label);
                drawList.AddText(_centerScreen[k] - new Vector2(sz.X * 0.5f, 16f), color, label);
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    // Nav-coord → AC world-metre scale. The horizontal render math (proven
    // correct) is `wx = px + (pt.EW − playerEW) * 240`, i.e. a *relative*
    // delta from the player scaled by 240. We reuse exactly that delta and
    // anchor it to the player's known world metres, so this is unit-safe for
    // whatever frame the uTank2 .nav stores (NOT the /loc frame).
    private const double NavToMetres = 240.0;

    /// <summary>
    /// World-Y for a nav marker. Base height is the recorded path Z — the
    /// original, known-good behaviour. A flat ring floats above the downhill
    /// ground on a slope; we estimate the local grade purely from the route's
    /// own neighbouring points (rise/run in nav units — no terrain lookup and
    /// no shared raycast state, so it is thread-safe and works in structure
    /// cells) and sink the marker by NavSlopeSink x grade. Flat route =&gt;
    /// grade 0 =&gt; identical to the original behaviour.
    /// </summary>
    private float ResolveMarkerWorldY(NavRouteParser route, int i, float heightOffset)
    {
        var pt = route.Points[i];
        float baseY = (float)(pt.Z * NavToMetres) + heightOffset;

        float sink = _settings.NavSlopeSink;
        if (sink <= 0f)
            return baseY;

        // Steeper of the two route segments meeting at this point. Delta-Z and
        // the horizontal run are both in nav units, so grade is a unitless
        // rise/run (= tan slope) and the 240 scale cancels out.
        double grade = Math.Max(SegmentGrade(route, i, -1), SegmentGrade(route, i, +1));
        if (grade <= 0.0)
            return baseY;
        if (grade > 2.0) grade = 2.0;                    // bound garbage/cliff segments

        return baseY - sink * (float)grade;
    }

    /// <summary>
    /// |Delta-Z| / horizontal-run between route point i and the neighbour
    /// i+step (honouring circular wrap). 0 when the neighbour is missing or
    /// coincident.
    /// </summary>
    private static double SegmentGrade(NavRouteParser route, int i, int step)
    {
        int n = route.Points.Count;
        int j = i + step;
        if (j < 0 || j >= n)
        {
            if (route.RouteType != NavRouteType.Circular || n < 2)
                return 0.0;
            j = ((j % n) + n) % n;
        }

        var a = route.Points[i];
        var b = route.Points[j];
        double dE = b.EW - a.EW;
        double dN = b.NS - a.NS;
        double run = Math.Sqrt(dE * dE + dN * dN);
        if (run < 1e-6)
            return 0.0;
        return Math.Abs(b.Z - a.Z) / run;
    }
}

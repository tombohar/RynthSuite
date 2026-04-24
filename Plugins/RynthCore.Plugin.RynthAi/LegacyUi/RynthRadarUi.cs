using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// RynthRadar — square, movable, resizable radar. Shares the DungeonMapUi's
/// rasterised floor + outer-edge cache so walls look identical to the big map
/// (gap-free portal bridges and all). Outdoors: dot markers only (no walls).
/// Gear icon in the top-right corner opens an inline settings popup; player
/// coords (N/S, E/W, Z) sit below the canvas.
/// </summary>
internal sealed class RynthRadarUi
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private DungeonMapUi? _mapData;
    private MainLogic? _raycast;
    private WorldObjectCache? _objectCache;

    private bool _open = true;

    // Per-session explored state (grid cells at the DungeonMapUi rasteriser's
    // resolution). Cleared whenever the player transitions landblocks, so
    // re-entering a dungeon resets the coloring.
    private const float GridCell = 0.5f;
    private readonly System.Collections.Generic.HashSet<(int gx, int gy)> _visitedCells = new();
    private uint _visitedLandblock;

    // Colours
    private static readonly uint ColFrameOuter  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.05f, 0.08f, 1.00f));
    private static readonly uint ColFrameInner  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.62f, 0.52f, 0.24f, 1.00f));
    private static readonly uint ColFrameAccent = ImGui.ColorConvertFloat4ToU32(new Vector4(0.90f, 0.78f, 0.40f, 1.00f));
    private static readonly uint ColCardinal    = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 1.00f, 1.00f, 1.00f));
    private static readonly uint ColCanvasBg    = ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.07f, 0.11f, 1.00f));
    private static readonly uint ColCrosshair   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.40f, 0.50f, 0.60f));
    private static readonly uint ColCoordText   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.97f, 1.00f, 1.00f));
    private static readonly uint ColTextOutline = ImGui.ColorConvertFloat4ToU32(new Vector4(0.00f, 0.00f, 0.00f, 0.95f));

    // Wall colours — white by default, blue when the source cell has been visited this session.
    private static readonly uint ColWallUnseen   = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 1.00f, 1.00f, 1.00f));
    private static readonly uint ColWallVisited  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 0.60f, 1.00f, 1.00f));

    // Floor fill — kept subtle under the walls.
    private static readonly uint ColFloorFill    = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.18f, 0.28f, 0.55f));

    // Marker colours — all distinct so they read at a glance.
    private static readonly uint ColPlayer       = ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 1.00f, 0.40f, 1.00f)); // green
    private static readonly uint ColMonster      = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.20f, 0.20f, 1.00f)); // red
    private static readonly uint ColNpc          = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.85f, 0.20f, 1.00f)); // yellow
    private static readonly uint ColPortal       = ImGui.ColorConvertFloat4ToU32(new Vector4(0.75f, 0.30f, 1.00f, 1.00f)); // magenta/purple
    private static readonly uint ColDoor         = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.55f, 0.10f, 1.00f)); // vivid orange
    private static readonly uint ColDoorEdge     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.40f, 0.20f, 0.00f, 1.00f));

    public Action? OnSettingChanged { get; set; }

    public RynthRadarUi(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    internal void SetMapData(DungeonMapUi mapData) => _mapData = mapData;
    public void SetRaycast(MainLogic raycast) => _raycast = raycast;
    public void SetWorldObjectCache(WorldObjectCache cache) => _objectCache = cache;

    public void Render()
    {
        if (!_settings.ShowRynthRadar) return;
        if (!_host.HasGetPlayerPose) return;
        if (!_host.TryGetPlayerPose(out uint cellId, out float px, out float py, out float pz,
                out _, out _, out _, out _))
            return;

        bool snapRotate = _settings.RadarRotateWithPlayer;
        bool snapShowM  = _settings.RadarShowMonsters;
        bool snapShowN  = _settings.RadarShowNpcs;
        bool snapShowP  = _settings.RadarShowPortals;
        bool snapShowD  = _settings.RadarShowDoors;
        float snapOp    = _settings.RadarOpacity;
        float snapZoom  = _settings.RadarZoom;
        int   snapPaint = _settings.RadarWallPaintRadius;
        bool  snapCirc  = _settings.RadarCircular;

        ImGui.SetNextWindowSize(new Vector2(260, 284), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(140, 160), new Vector2(900, 932));
        ImGui.SetNextWindowBgAlpha(0f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);

        // NoBringToFrontOnFocus keeps the gear sub-window (rendered after) on top
        // when the user clicks the main window to drag or resize.
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                                | ImGuiWindowFlags.NoScrollbar
                                | ImGuiWindowFlags.NoScrollWithMouse
                                | ImGuiWindowFlags.NoCollapse
                                | ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (_settings.RadarClickThrough)
        {
            // Mouse / keyboard events pass straight through to the game. The
            // gear popup is unreachable while this is on — the Advanced Settings
            // panel is the escape hatch.
            flags |= ImGuiWindowFlags.NoInputs
                   | ImGuiWindowFlags.NoMove
                   | ImGuiWindowFlags.NoResize;
        }

        bool visible = ImGui.Begin("##RynthRadar", ref _open, flags);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        if (!visible) { ImGui.End(); return; }

        Vector2 winPos  = ImGui.GetWindowPos();
        Vector2 winSize = ImGui.GetWindowSize();

        // Keep canvas square; footer sits below and is sized for the 16pt coord text.
        const float FooterH = 30f;
        float sq = MathF.Min(winSize.X, winSize.Y - FooterH);
        if (MathF.Abs(winSize.X - sq) > 0.5f || MathF.Abs(winSize.Y - (sq + FooterH)) > 0.5f)
            ImGui.SetWindowSize(new Vector2(sq, sq + FooterH));

        Vector2 canvasPos = winPos;
        Vector2 canvasSize = new Vector2(sq, sq);
        Vector2 centre = canvasPos + canvasSize * 0.5f;
        float radius = sq * 0.5f - 6f;

        // Reserve the full window extent so later cursor moves (gear button) and
        // drawlist calls that extend past the implicit cursor don't trip ImGui's
        // "SetCursorPos extends parent boundary" assertion in End.
        ImGui.Dummy(new Vector2(sq, sq + FooterH));

        var dl = ImGui.GetWindowDrawList();
        bool circular = _settings.RadarCircular;

        DrawFrame(dl, canvasPos, canvasSize, circular);

        var clipMin = new Vector2(canvasPos.X + 4, canvasPos.Y + 4);
        var clipMax = new Vector2(canvasPos.X + canvasSize.X - 4, canvasPos.Y + canvasSize.Y - 4);
        dl.PushClipRect(clipMin, clipMax, intersect_with_current_clip_rect: true);

        // Per-element circular clip: content outside this radius is skipped in circle mode.
        float innerRadius = radius;
        float innerRadiusSq = innerRadius * innerRadius;

        // Crosshair — shortened to the visible area so it doesn't poke out of the circle.
        if (circular)
        {
            dl.AddLine(new Vector2(centre.X, centre.Y - innerRadius), new Vector2(centre.X, centre.Y + innerRadius), ColCrosshair, 1f);
            dl.AddLine(new Vector2(centre.X - innerRadius, centre.Y), new Vector2(centre.X + innerRadius, centre.Y), ColCrosshair, 1f);
        }
        else
        {
            dl.AddLine(new Vector2(centre.X, clipMin.Y), new Vector2(centre.X, clipMax.Y), ColCrosshair, 1f);
            dl.AddLine(new Vector2(clipMin.X, centre.Y), new Vector2(clipMax.X, centre.Y), ColCrosshair, 1f);
        }

        // Local helper — used to cull content that lies outside the circle in circle mode.
        bool InsideShape(Vector2 p)
        {
            if (!circular) return true; // square mode uses rect clip already
            float dx = p.X - centre.X;
            float dy = p.Y - centre.Y;
            return dx * dx + dy * dy <= innerRadiusSq;
        }

        // Heading setup. AC convention: heading 0° = east, 90° = north (math CCW from east).
        float heading = 0f;
        _host.TryGetPlayerHeading(out heading);
        float hRad = heading * MathF.PI / 180f;
        float sinH = MathF.Sin(hRad);
        float cosH = MathF.Cos(hRad);
        bool rotate = _settings.RadarRotateWithPlayer;

        float zoom = MathF.Max(0.5f, _settings.RadarZoom);
        uint landblock = cellId >> 16;
        float gxLB = ((landblock >> 8) & 0xFF) * 192f;
        float gyLB = (landblock & 0xFF) * 192f;
        float playerWX = px + gxLB;
        float playerWY = py + gyLB;
        bool isIndoor = (cellId & 0xFFFF) >= 0x100 && landblock != 0;

        // Reset per-landblock explored state so re-entering a dungeon starts fresh.
        if (landblock != _visitedLandblock)
        {
            _visitedLandblock = landblock;
            _visitedCells.Clear();
        }

        // Mark the player's current grid cell as visited (plus a user-tunable
        // radius around it so walls light up well before the player is on top).
        if (isIndoor)
        {
            int pgx = (int)MathF.Floor(playerWX / GridCell);
            int pgy = (int)MathF.Floor(playerWY / GridCell);
            int r = Math.Clamp(_settings.RadarWallPaintRadius, 0, 40);
            for (int oy = -r; oy <= r; oy++)
                for (int ox = -r; ox <= r; ox++)
                    _visitedCells.Add((pgx + ox, pgy + oy));
        }

        // ── Floor fill + outer edges (indoor, from shared big-map cache) ─
        // We draw three layers: current − 1, current + 1, and current. The ±1 layers
        // are drawn first and dimmed so the current floor stays visually dominant.
        if (isIndoor && _mapData != null)
        {
            _mapData.EnsureCache(landblock);
            var zLayers = _mapData._zLayers;
            if (zLayers is { Count: > 0 })
            {
                int curIdx = _mapData.BestLayerIdxFor(pz);

                // Render order: below, above, current (current drawn last → on top).
                Span<(int idx, float alpha, bool isCurrent)> order = stackalloc (int, float, bool)[3];
                int n = 0;
                if (curIdx - 1 >= 0)              order[n++] = (curIdx - 1, 0.30f, false);
                if (curIdx + 1 < zLayers.Count)   order[n++] = (curIdx + 1, 0.30f, false);
                order[n++] = (curIdx, 1.00f, true);

                for (int li = 0; li < n; li++)
                {
                    float layerZ = zLayers[order[li].idx];
                    float a      = order[li].alpha;
                    bool current = order[li].isCurrent;

                    uint fillCol    = SetAlpha(ColFloorFill,   a * 0.55f);
                    uint wallUnseen = SetAlpha(ColWallUnseen,  a);
                    uint wallSeen   = SetAlpha(ColWallVisited, a);

                    // Floor fill
                    if (_mapData._fillStrips != null &&
                        _mapData._fillStrips.TryGetValue(layerZ, out var strips))
                    {
                        foreach (var (x0, y0, x1, y1, _) in strips)
                        {
                            var p0 = ProjectWorld(x0, y0, playerWX, playerWY, centre, zoom, rotate, sinH, cosH);
                            var p1 = ProjectWorld(x1, y1, playerWX, playerWY, centre, zoom, rotate, sinH, cosH);
                            var min = new Vector2(MathF.Min(p0.X, p1.X), MathF.Min(p0.Y, p1.Y));
                            var max = new Vector2(MathF.Max(p0.X, p1.X), MathF.Max(p0.Y, p1.Y));
                            if (max.X < clipMin.X || min.X > clipMax.X ||
                                max.Y < clipMin.Y || min.Y > clipMax.Y) continue;
                            // In circle mode, drop fill rects with any corner outside the circle
                            // so the floor doesn't poke past the round frame.
                            if (circular)
                            {
                                if (!InsideShape(new Vector2(min.X, min.Y)) ||
                                    !InsideShape(new Vector2(max.X, min.Y)) ||
                                    !InsideShape(new Vector2(min.X, max.Y)) ||
                                    !InsideShape(new Vector2(max.X, max.Y))) continue;
                            }

                            if (rotate)
                            {
                                var p2 = ProjectWorld(x1, y0, playerWX, playerWY, centre, zoom, rotate, sinH, cosH);
                                var p3 = ProjectWorld(x0, y1, playerWX, playerWY, centre, zoom, rotate, sinH, cosH);
                                dl.AddQuadFilled(p0, p2, p1, p3, fillCol);
                            }
                            else
                            {
                                dl.AddRectFilled(min, max, fillCol);
                            }
                        }
                    }

                    // Walls — per-cell raw edges so each segment can colour by visit state.
                    // Visited-blue only applies to the current floor; adjacent floors stay
                    // plain (dim) white so the current floor remains the focal point.
                    if (_mapData._outerEdgesRaw != null &&
                        _mapData._outerEdgesRaw.TryGetValue(layerZ, out var rawEdges))
                    {
                        foreach (var (ax, ay, bx, by, _, cellGx, cellGy) in rawEdges)
                        {
                            var p0 = ProjectWorld(ax, ay, playerWX, playerWY, centre, zoom, rotate, sinH, cosH);
                            var p1 = ProjectWorld(bx, by, playerWX, playerWY, centre, zoom, rotate, sinH, cosH);
                            if (MathF.Max(p0.X, p1.X) < clipMin.X || MathF.Min(p0.X, p1.X) > clipMax.X ||
                                MathF.Max(p0.Y, p1.Y) < clipMin.Y || MathF.Min(p0.Y, p1.Y) > clipMax.Y) continue;
                            if (!InsideShape(p0) || !InsideShape(p1)) continue;

                            uint col = (current && _visitedCells.Contains((cellGx, cellGy)))
                                ? wallSeen : wallUnseen;
                            dl.AddLine(p0, p1, col, current ? 1.5f : 1.0f);
                        }
                    }
                }
            }
        }

        // ── Dot markers (indoor + outdoor) ───────────────────────────────
        if (_objectCache != null && _host.HasGetObjectPosition && landblock != 0)
        {
            const uint STypeCreatureType = 2u;
            const int  CreatureTypeNpc   = 14;
            const uint TypePortal        = 0x10000u;

            if (_settings.RadarShowMonsters || _settings.RadarShowNpcs)
            {
                foreach (var wo in _objectCache.GetLandscape())
                {
                    if (!_host.TryGetObjectPosition((uint)wo.Id, out uint cCellId,
                            out float cox, out float coy, out _)) continue;
                    if ((cCellId >> 16) != landblock) continue;

                    bool isNpc = _host.HasGetObjectIntProperty
                              && _host.TryGetObjectIntProperty((uint)wo.Id, STypeCreatureType, out int ct)
                              && ct == CreatureTypeNpc;
                    if (isNpc && !_settings.RadarShowNpcs) continue;
                    if (!isNpc && !_settings.RadarShowMonsters) continue;

                    var p = ProjectWorld(cox, coy, px, py, centre, zoom, rotate, sinH, cosH);
                    if (!InRect(p, clipMin, clipMax)) continue;
                    if (!InsideShape(p)) continue;
                    dl.AddCircleFilled(p, 3f, isNpc ? ColNpc : ColMonster);
                }
            }

            if (_settings.RadarShowPortals || _settings.RadarShowDoors)
            {
                foreach (var wo in _objectCache.GetLandscapeObjects())
                {
                    if ((uint)wo.Id >= 0x80000000u) continue;
                    if (!_host.TryGetObjectPosition((uint)wo.Id, out uint oCellId,
                            out float oox, out float ooy, out _)) continue;
                    if ((oCellId >> 16) != landblock) continue;

                    bool isPortal = _host.HasGetItemType
                                 && _host.TryGetItemType((uint)wo.Id, out uint typeFlags)
                                 && (typeFlags & TypePortal) != 0;
                    bool isDoor = !isPortal && IsDoorName(wo.Name);
                    if (isPortal && !_settings.RadarShowPortals) continue;
                    if (isDoor && !_settings.RadarShowDoors) continue;
                    if (!isPortal && !isDoor) continue;

                    var p = ProjectWorld(oox, ooy, px, py, centre, zoom, rotate, sinH, cosH);
                    if (!InRect(p, clipMin, clipMax)) continue;
                    if (!InsideShape(p)) continue;
                    if (isPortal)
                    {
                        // Portal: purple diamond.
                        const float s = 4f;
                        dl.AddQuadFilled(
                            new Vector2(p.X, p.Y - s),
                            new Vector2(p.X + s, p.Y),
                            new Vector2(p.X, p.Y + s),
                            new Vector2(p.X - s, p.Y), ColPortal);

                        // Destination label directly above the diamond.
                        string label = GetPortalLabel(wo);
                        if (!string.IsNullOrEmpty(label))
                        {
                            ImFontPtr font = ImGui.GetFont();
                            const float lblSize = 13f;
                            Vector2 ts = font.CalcTextSizeA(lblSize, float.MaxValue, 0f, label);
                            var lp = new Vector2(p.X - ts.X * 0.5f, p.Y - s - ts.Y - 1f);
                            DrawTextOutlined(dl, font, lblSize, lp, label, ColCardinal, ColTextOutline);
                        }
                    }
                    else
                    {
                        // Door: orange square with a dark outline — very different silhouette
                        // from the monster/NPC circles so it reads at a glance.
                        const float s = 3.5f;
                        dl.AddRectFilled(new Vector2(p.X - s, p.Y - s), new Vector2(p.X + s, p.Y + s), ColDoor);
                        dl.AddRect(new Vector2(p.X - s, p.Y - s), new Vector2(p.X + s, p.Y + s), ColDoorEdge, 0f, ImDrawFlags.None, 1f);
                    }
                }
            }
        }

        // ── Player centre + heading arrow ────────────────────────────────
        dl.AddCircleFilled(centre, 3.5f, ColPlayer);
        {
            float ax, ay;
            if (rotate)
            {
                ax = 0f; ay = -12f;        // player's facing is always screen-up
            }
            else
            {
                // North-up: arrow follows actual world heading.
                // Heading 0° = east → (+x, 0); 90° = north → (0, -y) on screen.
                ax = cosH * 12f;
                ay = -sinH * 12f;
            }
            dl.AddLine(centre, new Vector2(centre.X + ax, centre.Y + ay), ColPlayer, 2f);
        }

        dl.PopClipRect();

        // Cardinals — ride the inner edge of the frame (square or circular).
        DrawCardinals(dl, centre, sq * 0.5f - 10f, rotate ? (hRad - MathF.PI * 0.5f) : 0f, circular);

        // Gear button + popup — hoisted into a separate always-interactive sub-window
        // so it stays clickable even when the main radar window has NoInputs (click-through).
        {
            const float btn = 14f;
            Vector2 bPos;
            if (circular)
            {
                float R = innerRadius;
                const float k = 0.82f;
                bPos = new Vector2(
                    centre.X + R * 0.707f * k - btn * 0.5f,
                    centre.Y - R * 0.707f * k - btn * 0.5f);
            }
            else
            {
                bPos = new Vector2(canvasPos.X + canvasSize.X - btn - 6f, canvasPos.Y + 6f);
            }
            RenderGearWindow("##rradar_gear_win", "*##rradar_gear", "##rradar_settings", bPos, btn);
        }

        // Footer — outdoor shows world coords (N/S, E/W, Z); indoor hides N/S and E/W
        // since they barely change inside a single landblock, and shows the landblock
        // ID + Z instead.
        {
            string? coords = null;
            if (isIndoor)
            {
                coords = $"LB {landblock:X4}  Z {pz:0.0}";
            }
            else if (NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew))
            {
                coords = $"{MathF.Abs((float)ns):0.0}{(ns >= 0 ? "N" : "S")}  {MathF.Abs((float)ew):0.0}{(ew >= 0 ? "E" : "W")}  Z {pz:0.0}";
            }

            if (coords != null)
            {
                ImFontPtr font = ImGui.GetFont();
                const float coordSize = 16f;
                Vector2 ts = font.CalcTextSizeA(coordSize, float.MaxValue, 0f, coords);
                Vector2 tp = new Vector2(canvasPos.X + (canvasSize.X - ts.X) * 0.5f,
                                         canvasPos.Y + canvasSize.Y + (FooterH - ts.Y) * 0.5f);
                DrawTextOutlined(dl, font, coordSize, tp, coords, ColCoordText, ColTextOutline);
            }
        }

        // Scroll-wheel zoom over the canvas area.
        // Note: we deliberately do NOT use an InvisibleButton here, because it would block
        // the window's edge-resize grips and drag-to-move behaviour.
        if (ImGui.IsWindowHovered())
        {
            var mp = ImGui.GetIO().MousePos;
            if (mp.X >= clipMin.X && mp.X <= clipMax.X && mp.Y >= clipMin.Y && mp.Y <= clipMax.Y)
            {
                float wheel = ImGui.GetIO().MouseWheel;
                if (float.IsFinite(wheel) && wheel != 0f)
                {
                    _settings.RadarZoom = Math.Clamp(_settings.RadarZoom + wheel * 0.25f, 0.5f, 20f);
                    ImGui.SetNextFrameWantCaptureMouse(true);
                }
            }
        }

        ImGui.End();

        if (snapRotate != _settings.RadarRotateWithPlayer
            || snapShowM != _settings.RadarShowMonsters
            || snapShowN != _settings.RadarShowNpcs
            || snapShowP != _settings.RadarShowPortals
            || snapShowD != _settings.RadarShowDoors
            || snapPaint != _settings.RadarWallPaintRadius
            || snapCirc  != _settings.RadarCircular
            || MathF.Abs(snapOp   - _settings.RadarOpacity) > 0.001f
            || MathF.Abs(snapZoom - _settings.RadarZoom)    > 0.001f)
        {
            OnSettingChanged?.Invoke();
        }
    }

    // ── Frame drawing ───────────────────────────────────────────────────

    private void DrawFrame(ImDrawListPtr dl, Vector2 pos, Vector2 size, bool circular)
    {
        float op = Math.Clamp(_settings.RadarOpacity, 0f, 1f);
        uint bg = SetAlpha(ColCanvasBg, op);

        Vector2 a = pos;
        Vector2 b = pos + size;

        if (circular)
        {
            Vector2 c = (a + b) * 0.5f;
            float r = MathF.Min(size.X, size.Y) * 0.5f - 2f;
            const int seg = 64;

            dl.AddCircleFilled(c, r, bg, seg);
            dl.AddCircle(c, r,     ColFrameOuter, seg, 2f);
            dl.AddCircle(c, r - 3, ColFrameInner, seg, 1f);

            // Four compass tick marks just inside the rim (N/E/S/W) in the accent colour.
            float ti = r - 8f;
            float to = r - 2f;
            dl.AddLine(new Vector2(c.X, c.Y - ti), new Vector2(c.X, c.Y - to), ColFrameAccent, 2f);
            dl.AddLine(new Vector2(c.X, c.Y + ti), new Vector2(c.X, c.Y + to), ColFrameAccent, 2f);
            dl.AddLine(new Vector2(c.X - ti, c.Y), new Vector2(c.X - to, c.Y), ColFrameAccent, 2f);
            dl.AddLine(new Vector2(c.X + ti, c.Y), new Vector2(c.X + to, c.Y), ColFrameAccent, 2f);
            return;
        }

        float rr = 4f;
        dl.AddRectFilled(a, b, bg, rr);
        dl.AddRect(new Vector2(a.X + 1, a.Y + 1), new Vector2(b.X - 1, b.Y - 1), ColFrameOuter, rr, ImDrawFlags.None, 2f);
        dl.AddRect(new Vector2(a.X + 3, a.Y + 3), new Vector2(b.X - 3, b.Y - 3), ColFrameInner, rr, ImDrawFlags.None, 1f);

        float cl = 8f;
        float co = 2f;
        dl.AddLine(new Vector2(a.X - co, a.Y + cl), new Vector2(a.X - co, a.Y - co), ColFrameAccent, 2f);
        dl.AddLine(new Vector2(a.X - co, a.Y - co), new Vector2(a.X + cl, a.Y - co), ColFrameAccent, 2f);
        dl.AddLine(new Vector2(b.X - cl, a.Y - co), new Vector2(b.X + co, a.Y - co), ColFrameAccent, 2f);
        dl.AddLine(new Vector2(b.X + co, a.Y - co), new Vector2(b.X + co, a.Y + cl), ColFrameAccent, 2f);
        dl.AddLine(new Vector2(a.X - co, b.Y - cl), new Vector2(a.X - co, b.Y + co), ColFrameAccent, 2f);
        dl.AddLine(new Vector2(a.X - co, b.Y + co), new Vector2(a.X + cl, b.Y + co), ColFrameAccent, 2f);
        dl.AddLine(new Vector2(b.X - cl, b.Y + co), new Vector2(b.X + co, b.Y + co), ColFrameAccent, 2f);
        dl.AddLine(new Vector2(b.X + co, b.Y + co), new Vector2(b.X + co, b.Y - cl), ColFrameAccent, 2f);
    }

    private static void DrawCardinals(ImDrawListPtr dl, Vector2 centre, float halfSize, float rotationRad, bool circular)
    {
        ReadOnlySpan<string> labels = new[] { "N", "E", "S", "W" };
        // Clockwise angles from screen-up, when rotationRad = 0 (north-up).
        ReadOnlySpan<float> angles = new[] { 0f, MathF.PI * 0.5f, MathF.PI, MathF.PI * 1.5f };

        ImFontPtr font = ImGui.GetFont();
        const float size = 18f;

        for (int i = 0; i < 4; i++)
        {
            float a = angles[i] + rotationRad;
            // Direction vector from centre: (sin a, -cos a) so angle 0 = up.
            float dx = MathF.Sin(a);
            float dy = -MathF.Cos(a);

            float x, y;
            if (circular)
            {
                // Ride the circle of radius = halfSize.
                x = centre.X + dx * halfSize;
                y = centre.Y + dy * halfSize;
            }
            else
            {
                // Project onto the SQUARE of half-side `halfSize`: scale dir so
                // that max(|dx|,|dy|) equals halfSize → label sits on the perimeter.
                float m = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
                if (m < 1e-4f) continue;
                float k = halfSize / m;
                x = centre.X + dx * k;
                y = centre.Y + dy * k;
            }

            Vector2 ts = font.CalcTextSizeA(size, float.MaxValue, 0f, labels[i]);
            var pos = new Vector2(x - ts.X * 0.5f, y - ts.Y * 0.5f);
            DrawTextOutlined(dl, font, size, pos, labels[i], ColCardinal, ColTextOutline);
        }
    }

    /// <summary>Draws text with a 1px black outline (4-way offset) for legibility
    /// over varied backgrounds.</summary>
    private static void DrawTextOutlined(ImDrawListPtr dl, ImFontPtr font, float size,
        Vector2 pos, string text, uint fg, uint outline)
    {
        dl.AddText(font, size, new Vector2(pos.X + 1, pos.Y),     outline, text);
        dl.AddText(font, size, new Vector2(pos.X - 1, pos.Y),     outline, text);
        dl.AddText(font, size, new Vector2(pos.X,     pos.Y + 1), outline, text);
        dl.AddText(font, size, new Vector2(pos.X,     pos.Y - 1), outline, text);
        dl.AddText(font, size, new Vector2(pos.X + 1, pos.Y + 1), outline, text);
        dl.AddText(font, size, new Vector2(pos.X - 1, pos.Y - 1), outline, text);
        dl.AddText(font, size, new Vector2(pos.X + 1, pos.Y - 1), outline, text);
        dl.AddText(font, size, new Vector2(pos.X - 1, pos.Y + 1), outline, text);
        dl.AddText(font, size, pos, fg, text);
    }

    /// <summary>
    /// Renders the gear button and its settings popup in a separate, always-
    /// interactive ImGui sub-window, so the gear stays clickable even when the
    /// main radar window has NoInputs (click-through).
    /// </summary>
    private void RenderGearWindow(string winId, string btnId, string popupId, Vector2 pos, float size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(new Vector2(size, size));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                                      | ImGuiWindowFlags.NoResize
                                      | ImGuiWindowFlags.NoMove
                                      | ImGuiWindowFlags.NoCollapse
                                      | ImGuiWindowFlags.NoBackground
                                      | ImGuiWindowFlags.NoSavedSettings
                                      | ImGuiWindowFlags.NoBringToFrontOnFocus;

        bool open = true;
        if (ImGui.Begin(winId, ref open, flags))
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0.55f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.30f, 0.40f, 0.85f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.20f, 0.25f, 0.35f, 1f));
            if (ImGui.Button(btnId, new Vector2(size, size)))
                ImGui.OpenPopup(popupId);
            ImGui.PopStyleColor(3);
            RenderSettingsPopup();
        }
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void RenderSettingsPopup()
    {
        if (!ImGui.BeginPopup("##rradar_settings")) return;

        ImGui.Text("Radar Settings");
        ImGui.Separator();

        ImGui.Checkbox("Rotate with Player", ref _settings.RadarRotateWithPlayer);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("On: player facing points up and compass rotates.\nOff: north is always up.");

        ImGui.Checkbox("Circular Radar", ref _settings.RadarCircular);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off: square frame. On: circular frame (content outside the circle is culled).");

        ImGui.Checkbox("Click-Through", ref _settings.RadarClickThrough);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mouse events pass through to the game. Move/resize/gear are disabled while on.\nTurn it back off from Advanced Settings → Navigation → Radar.");

        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Opacity", ref _settings.RadarOpacity, 0.15f, 1f, "%.2f");
        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Zoom",    ref _settings.RadarZoom,    0.5f, 20f, "%.1f");
        ImGui.SetNextItemWidth(140);
        ImGui.SliderInt("Paint Radius", ref _settings.RadarWallPaintRadius, 1, 20);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Distance (in 0.5m grid cells) around the player that\nmarks walls as explored. Higher = walls light up from further away.");

        ImGui.Separator();
        ImGui.Text("Show:");
        ImGui.Checkbox("Monsters##rradar", ref _settings.RadarShowMonsters);
        ImGui.Checkbox("NPCs##rradar",     ref _settings.RadarShowNpcs);
        ImGui.Checkbox("Portals##rradar",  ref _settings.RadarShowPortals);
        ImGui.Checkbox("Doors##rradar",    ref _settings.RadarShowDoors);

        ImGui.EndPopup();
    }

    // ── Projection ──────────────────────────────────────────────────────

    /// <summary>
    /// Project a world-space XY point (landblock-local is fine as long as playerX/playerY
    /// are expressed in the same basis) to radar screen coords.
    ///
    /// AC convention: +X = east, +Y = north, heading 0° = east, 90° = north (CCW from east).
    /// </summary>
    private static Vector2 ProjectWorld(
        float wx, float wy, float playerX, float playerY,
        Vector2 centre, float zoom, bool rotate, float sinH, float cosH)
    {
        float dx = wx - playerX;   // east offset
        float dy = wy - playerY;   // north offset

        float sx, sy;
        if (rotate)
        {
            // Rotate world by (90° - heading) so facing → screen-up.
            //   newX = sin(h)*dx - cos(h)*dy   (screen right)
            //   newY = cos(h)*dx + sin(h)*dy   (screen up → screen -Y)
            float right   = sinH * dx - cosH * dy;
            float forward = cosH * dx + sinH * dy;
            sx = centre.X + right   * zoom;
            sy = centre.Y - forward * zoom;
        }
        else
        {
            sx = centre.X + dx * zoom;
            sy = centre.Y - dy * zoom;
        }
        return new Vector2(sx, sy);
    }

    private static bool InRect(Vector2 p, Vector2 min, Vector2 max)
        => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;

    private static uint SetAlpha(uint col, float alpha)
    {
        uint a = (uint)(Math.Clamp(alpha, 0f, 1f) * 255f);
        return (col & 0x00FFFFFF) | (a << 24);
    }

    /// <summary>
    /// Best-effort portal display name. STypePortalDest (38) holds the raw destination
    /// when available, otherwise we fall back to the object name. The label is then
    /// reduced to just the destination proper:
    ///   • first line only (strip anything past a newline)
    ///   • tokenise on whitespace/punctuation
    ///   • drop any token that is literally "portal"
    ///   • drop any token that looks like a coord ("5.6N", "-12.3W", etc.)
    /// Result is trimmed and ellipsis-truncated at 22 chars.
    /// </summary>
    private string GetPortalLabel(WorldObject wo)
    {
        const uint STypePortalDest = 38u;
        string label = wo.Name ?? string.Empty;

        if (_host.HasGetObjectStringProperty
            && _host.TryGetObjectStringProperty((uint)wo.Id, STypePortalDest, out string dest)
            && !string.IsNullOrEmpty(dest))
        {
            label = dest;
        }

        int nl = label.IndexOf('\n');
        if (nl >= 0) label = label.Substring(0, nl);

        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < label.Length)
        {
            while (i < label.Length && IsLabelSep(label[i])) i++;
            if (i >= label.Length) break;
            int start = i;
            while (i < label.Length && !IsLabelSep(label[i])) i++;
            string tok = label.Substring(start, i - start);

            if (tok.Equals("portal", StringComparison.OrdinalIgnoreCase)) continue;
            if (tok.Equals("to",     StringComparison.OrdinalIgnoreCase)) continue;
            if (LooksLikeCoord(tok)) continue;
            if (IsNumericToken(tok))  continue;

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(tok);
        }

        string result = sb.ToString().Trim();
        if (result.Length > 22) result = result.Substring(0, 22) + "…";
        return result;
    }

    private static bool IsLabelSep(char c)
        => c == ' ' || c == '\t' || c == ',' || c == ';' || c == ':' || c == '(' || c == ')' || c == '.';

    /// <summary>True if the token looks like a coordinate — a digit/sign followed
    /// eventually by a compass letter (N/S/E/W), e.g. "5.6N", "-12.3W", "0N".
    /// Note: IsLabelSep consumes '.' so coord fragments arrive as "56N" / "123W".</summary>
    /// <summary>True if every char in the token is a digit or sign (e.g. "49", "-12",
    /// "+5"). Catches bare coord numbers that don't carry a compass letter.</summary>
    private static bool IsNumericToken(string tok)
    {
        if (tok.Length == 0) return false;
        int start = 0;
        if (tok[0] == '-' || tok[0] == '+') { if (tok.Length == 1) return false; start = 1; }
        for (int i = start; i < tok.Length; i++)
            if (!char.IsDigit(tok[i])) return false;
        return true;
    }

    private static bool LooksLikeCoord(string tok)
    {
        if (tok.Length < 2) return false;
        char last = tok[tok.Length - 1];
        bool endsCompass = last == 'N' || last == 'S' || last == 'E' || last == 'W'
                        || last == 'n' || last == 's' || last == 'e' || last == 'w';
        if (!endsCompass) return false;
        char first = tok[0];
        if (!(char.IsDigit(first) || first == '-' || first == '+')) return false;
        for (int i = 1; i < tok.Length - 1; i++)
            if (!char.IsDigit(tok[i])) return false;
        return true;
    }

    private static bool IsDoorName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.Contains("Door",       StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gate",       StringComparison.OrdinalIgnoreCase)
            || name.Contains("Portcullis", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hatch",      StringComparison.OrdinalIgnoreCase)
            || name.Contains("Trapdoor",   StringComparison.OrdinalIgnoreCase);
    }
}

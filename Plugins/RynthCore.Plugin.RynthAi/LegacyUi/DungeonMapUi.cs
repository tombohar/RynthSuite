using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Maps;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// Dungeon map: rasterise floor/slope polygons into a typed 2D grid, then:
///   • Fill   — merge grid rows into horizontal strips, draw AddRectFilled.
///   • Edges  — emit a coloured line on every filled cell face bordering empty space.
///
/// CellType colouring:
///   Flat      → light-blue fill + light-blue edge
///   SlopeUp   → green fill + green edge   (avg vertex Z > layerZ)
///   SlopeDown → red fill   + red edge     (avg vertex Z &lt; layerZ)
///
/// Gap fix: besides scanline fill, each polygon vertex cell is explicitly
/// marked — prevents degenerate thin polygons from being skipped.
/// </summary>
internal sealed class DungeonMapUi
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private MainLogic? _raycast;
    private WorldObjectCache? _objectCache;

    internal uint _cachedLandblock;

    internal enum CellType : byte { Flat = 0, SlopeUp = 1, SlopeDown = 2 }

    // Pre-built per Z-layer — internal so RynthRadarUi can share the cache.
    internal Dictionary<float, List<(float ax, float ay, float bx, float by, CellType type)>>? _outerEdges;
    // Raw (unmerged) outer edges with source grid-cell coord, used by RynthRadarUi
    // for per-cell visited-state coloring.
    internal Dictionary<float, List<(float ax, float ay, float bx, float by, CellType type, int gx, int gy)>>? _outerEdgesRaw;
    internal Dictionary<float, List<(float x0, float y0, float x1, float y1, CellType type)>>? _fillStrips;
    internal List<float>? _zLayers;
    internal int _autoLayerIdx;
    // EnvCell list for the cached landblock — used by edit mode to map a screen
    // click back to the prefab + local-grid cell it should patch.
    internal List<DungeonLOS.MapCell>? _lbCells;

    /// <summary>Force the landblock cache to be built. Used by RynthRadarUi to share walls/fills.</summary>
    internal void EnsureCache(uint landblock)
    {
        if (landblock != _cachedLandblock || _outerEdges is null)
            RefreshMap(landblock);
    }

    internal int BestLayerIdxFor(float playerZ) => BestLayerIdx(playerZ);

    private readonly HashSet<int> _visibleLayers = new();

    private float _zoom = 5.0f;
    private Vector2 _pan;
    private bool _autoFollow = true;
    private bool _show1U1D   = true;  // show current floor ± 1 (default)
    private bool _open = true;
    private bool _autoHidden;         // true when we auto-hid the map because player went outdoors
    public bool IsAutoHidden => _autoHidden;

    // Toggle-stabilisation: when toolbar is visible we track both the full window
    // rect and the canvas rect.  On hide we snap the window to the canvas rect so
    // it sits exactly over the map area; on show we restore the full window rect.
    private bool    _prevHideToolbar;
    private Vector2 _fullWindowPos;    // window pos  when toolbar is visible
    private Vector2 _fullWindowSize;   // window size when toolbar is visible
    private Vector2 _canvasPos;        // canvas top-left when toolbar is visible
    private Vector2 _canvasSize;       // canvas size      when toolbar is visible

    // ── Edit mode (paint patches over polygon-derived map) ──────────────────
    private static readonly string PatchFilePath =
        Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") is { Length: > 0 } pf
            ? Path.GetPathRoot(pf) ?? @"C:\"
            : @"C:\",
            "Games", "RynthSuite", "RynthAi", "MapPatches.txt");
    private readonly MapPatchStore _patches = new MapPatchStore(PatchFilePath);
    private bool _editMode;
    private MapPatchStore.PatchKind _brushKind = MapPatchStore.PatchKind.AddFlat;
    private bool _eraseMode;        // right-click brush
    private (int gx, int gy)? _hoverPatchCell;  // brush highlight position in last hovered cell's local space
    private uint _hoverEnvId;
    private uint _hoverCsIdx;
    private float _hoverCellRotation;
    private float _hoverCellWorldX, _hoverCellWorldY;

    // Walking-paint: patches the player's current grid cell on every frame they
    // step into a new one. Defaults ON because the user's natural mental model
    // is "the floor I walked through obviously had floor". Patches are per-prefab,
    // so walking through one corridor cell fixes that prefab everywhere.
    private (uint envId, uint csIdx, int lgx, int lgy)? _lastWalkPaintCell;
    private bool _walkPaintEnabled = true;
    private DateTime _lastPatchSave = DateTime.UtcNow;

    // Auto-scan: at every landblock change, run a "discover everything" pass that
    // rasterises with full permissive logic (including zero-coverage fallback) and
    // captures every filled cell as a per-prefab patch. Toggle off if it's too heavy.
    private bool _autoScanEnabled = true;
    private uint _lastScannedLandblock;
    private bool _scanInProgress;
    private int  _lastScanPatchCount;

    // Edge colours
    private static readonly uint ColBg           = ImGui.ColorConvertFloat4ToU32(new Vector4(0.04f, 0.07f, 0.12f, 1.00f));
    private static readonly uint ColBorder       = ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.30f, 0.40f, 1.00f));
    private static readonly uint ColFlat         = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.80f, 1.00f, 1.00f));
    private static readonly uint ColSlopeUp      = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.35f, 0.25f, 1.00f)); // red  — leads to higher floor
    private static readonly uint ColSlopeDown    = ImGui.ColorConvertFloat4ToU32(new Vector4(0.25f, 1.00f, 0.35f, 1.00f)); // green — leads to lower floor
    private static readonly uint ColPlayer        = ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 1.00f, 0.40f, 1.00f));
    private static readonly uint ColPortal        = ImGui.ColorConvertFloat4ToU32(new Vector4(0.90f, 0.45f, 1.00f, 1.00f)); // purple
    private static readonly uint ColPortalRing    = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.75f, 1.00f, 0.70f));
    private static readonly uint ColPortalLabel   = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.88f, 1.00f, 1.00f));
    private static readonly uint ColDoor          = ImGui.ColorConvertFloat4ToU32(new Vector4(0.75f, 0.55f, 0.25f, 1.00f)); // tan
    private static readonly uint ColDoorRing      = ImGui.ColorConvertFloat4ToU32(new Vector4(0.90f, 0.75f, 0.50f, 0.70f));
    private static readonly uint ColDoorLabel     = ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.85f, 0.65f, 1.00f));
    private static readonly uint ColMonster       = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.25f, 0.20f, 1.00f)); // red
    private static readonly uint ColMonsterRing   = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.55f, 0.50f, 0.70f));
    private static readonly uint ColNpc           = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.85f, 0.20f, 1.00f)); // yellow-gold
    private static readonly uint ColNpcRing       = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.95f, 0.55f, 0.70f));
    private static readonly uint ColCreatureLabel = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.90f, 0.90f, 1.00f));
    // Fill colours (semi-transparent)
    private static readonly uint ColFlatFill      = ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 0.40f, 0.65f, 0.40f));
    private static readonly uint ColSlopeUpFill   = ImGui.ColorConvertFloat4ToU32(new Vector4(0.55f, 0.12f, 0.08f, 0.40f));
    private static readonly uint ColSlopeDownFill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.50f, 0.15f, 0.40f));

    private const float GridCell             = 0.5f;
    private const float FloorNormalThreshold = 0.4f;  // |Nz|/|N| — must exceed to be floor-like
    private const float FlatNormalThreshold  = 0.95f; // |Nz|/|N| — must exceed to be flat (not a ramp)
    private const byte  NotFloor            = 255;    // sentinel returned by ClassifyPoly

    /// <summary>Called immediately whenever a map setting is changed by the user.</summary>
    public Action? OnSettingChanged { get; set; }

    public DungeonMapUi(RynthCoreHost host, LegacyUiSettings settings) { _host = host; _settings = settings; }

    public void SetWorldObjectCache(WorldObjectCache cache) => _objectCache = cache;

    public void SetRaycast(MainLogic raycast)
    {
        _raycast = raycast;
        _outerEdges = null;
        _fillStrips = null;
        _cachedLandblock = 0;
    }

    public void Render()
    {
        // Check indoor/outdoor before creating the ImGui window.
        // Auto-hide when outdoors (or in portalspace), auto-show when returning indoors.
        bool isIndoor = false;
        if (_host.HasGetPlayerPose && _host.TryGetPlayerPose(
                out uint preCellId, out _, out _, out _, out _, out _, out _, out _))
        {
            isIndoor = (preCellId & 0xFFFF) >= 0x100 && (preCellId >> 16) != 0;
        }

        if (!isIndoor)
        {
            if (!_autoHidden)
            {
                _autoHidden = true;
                DashWindows.ShowDungeonMap = false;
            }
            return;
        }

        if (_autoHidden)
        {
            _autoHidden = false;
            _open = true;
            DashWindows.ShowDungeonMap = true;
        }

        // Snapshot map-specific settings so we can detect changes and save immediately.
        bool  snapDoors     = _settings.MapShowDoors;
        bool  snapCreatures = _settings.MapShowCreatures;
        bool  snapToolbar   = _settings.MapShowToolbar;
        float snapOpacity   = _settings.MapBgOpacity;

        float op          = _settings.MapBgOpacity;
        bool  hideToolbar = !_settings.MapShowToolbar;

        // FirstUseEver must come BEFORE any Always override — each SetNextWindowSize call
        // overwrites the previous one, so Always must be last to win.
        ImGui.SetNextWindowSize(new Vector2(480, 520), ImGuiCond.FirstUseEver);

        // On toggle: snap the window to the canvas rect (hide) or full window rect (show).
        bool stateChanged = hideToolbar != _prevHideToolbar;
        _prevHideToolbar = hideToolbar;
        if (stateChanged)
        {
            if (hideToolbar && _canvasSize.X > 0)
            {
                // Shrink to exactly where the canvas was — no title bar / toolbar chrome.
                ImGui.SetNextWindowPos(_canvasPos,  ImGuiCond.Always);
                ImGui.SetNextWindowSize(_canvasSize, ImGuiCond.Always);
            }
            else if (!hideToolbar && _fullWindowSize.X > 0)
            {
                // Restore the full window including toolbar and title bar.
                ImGui.SetNextWindowPos(_fullWindowPos,  ImGuiCond.Always);
                ImGui.SetNextWindowSize(_fullWindowSize, ImGuiCond.Always);
            }
        }

        // Remove border and padding when toolbar is hidden so no empty chrome is visible.
        int styleVarCount = 0;
        if (hideToolbar)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            styleVarCount = 2;
        }

        // Apply opacity to title bar colours too — SetNextWindowBgAlpha only covers the content area.
        var titleCol = new Vector4(0.08f, 0.12f, 0.18f, op);
        ImGui.PushStyleColor(ImGuiCol.TitleBg,        titleCol);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,  new Vector4(0.10f, 0.16f, 0.24f, op));

        ImGui.SetNextWindowBgAlpha(op);

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        if (hideToolbar) flags |= ImGuiWindowFlags.NoTitleBar;

        bool beginVisible = ImGui.Begin("Dungeon Map##RynthAiDungeonMap", ref _open, flags);
        if (styleVarCount > 0) ImGui.PopStyleVar(styleVarCount);
        ImGui.PopStyleColor(2);

        if (!beginVisible) { ImGui.End(); return; }
        if (!_open) { DashWindows.ShowDungeonMap = false; ImGui.End(); return; }

        // Track full window rect every frame when toolbar is visible.
        if (!hideToolbar)
        {
            _fullWindowPos  = ImGui.GetWindowPos();
            _fullWindowSize = ImGui.GetWindowSize();
        }

        if (_raycast is null || !_raycast.IsInitialized)
        {
            ImGui.TextDisabled(_raycast is null
                ? "Raycasting not yet initialised — loading DAT files..."
                : $"Raycasting failed: {_raycast.StatusMessage}");
            ImGui.End(); return;
        }

        if (!_host.HasGetPlayerPose || !_host.TryGetPlayerPose(
                out uint cellId, out float px, out float py, out float pz,
                out _, out _, out _, out _))
        { ImGui.TextDisabled("Player position unavailable."); ImGui.End(); return; }

        // Indoor check already handled before window creation — this is a safety fallback.
        if ((cellId & 0xFFFF) < 0x100)
        { ImGui.End(); return; }

        uint landblock = cellId >> 16;
        if (landblock != _cachedLandblock || _outerEdges is null)
            RefreshMap(landblock);

        if (_outerEdges is null || _outerEdges.Count == 0)
        { ImGui.TextDisabled($"No dungeon geometry for 0x{landblock:X4}."); ImGui.End(); return; }

        float gx = ((landblock >> 8) & 0xFF) * 192.0f;
        float gy =  (landblock        & 0xFF) * 192.0f;

        // Walking-paint: every frame the player stands in a new 0.5u grid cell,
        // record a Flat patch for that prefab. Walking through gaps in the
        // polygon-derived map fills them in, and because patches key per-prefab,
        // one trip through a "Marble Hallway 03" cell paints that prefab in every
        // dungeon that uses it.
        if (_walkPaintEnabled) UpdateWalkPaint(px + gx, py + gy, pz);

        if (!hideToolbar)
        {
            if (ImGui.SmallButton("^##tbToggle")) _settings.MapShowToolbar = false;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Hide settings");
            ImGui.SameLine(0, 8);
            RenderToolbar(pz);
        }

        RenderCanvas(px + gx, py + gy, pz, hideToolbar);

        // Save immediately when any map setting changes.
        if (_settings.MapShowDoors     != snapDoors     ||
            _settings.MapShowCreatures != snapCreatures ||
            _settings.MapShowToolbar   != snapToolbar   ||
            MathF.Abs(_settings.MapBgOpacity - snapOpacity) > 0.001f)
        {
            OnSettingChanged?.Invoke();
        }

        ImGui.End();
    }

    // ── Data refresh ─────────────────────────────────────────────────────────

    private void RefreshMap(uint landblock)
    {
        // Landblock change → save accumulated walking-paint patches from the previous
        // dungeon. This is a natural commit point and saves work if the player crashes
        // out without ever toggling edit mode.
        if (_cachedLandblock != 0 && _cachedLandblock != landblock)
            _patches.SaveIfDirty();

        _cachedLandblock = landblock;
        _pan = Vector2.Zero;
        _outerEdges = null;
        _outerEdgesRaw = null;
        _fillStrips  = null;

        var los = _raycast?.GeometryLoader?.DungeonLOS;
        if (los is null) return;

        var polys = los.GetDungeonMapPolygons(landblock);
        if (polys.Count == 0) return;

        // Per-prefab patches: we need each EnvCell's (envId, cellStructIdx, world position,
        // rotation, layer) so a click in world space maps back to the correct prefab.
        _lbCells = los.GetDungeonMapCells(landblock);
        var lbCells = _lbCells;

        var zSet = new SortedSet<float>();
        foreach (var p in polys)
            zSet.Add((float)Math.Round(p.CellZ, 1));
        _zLayers = new List<float>(zSet);

        _autoLayerIdx = 0;
        _visibleLayers.Clear();
        if (_show1U1D)
        {
            _visibleLayers.Add(0);
            if (_zLayers.Count > 1) _visibleLayers.Add(1);
        }
        else
        {
            for (int i = 0; i < _zLayers.Count; i++) _visibleLayers.Add(i);
        }

        _outerEdges = new Dictionary<float, List<(float, float, float, float, CellType)>>(_zLayers.Count);
        _fillStrips  = new Dictionary<float, List<(float, float, float, float, CellType)>>(_zLayers.Count);

        const float LayerTol = 1.0f;

        foreach (float layerZ in _zLayers)
        {
            var filled = new Dictionary<(int, int), CellType>();

            foreach (var poly in polys)
            {
                if (MathF.Abs(poly.CellZ - layerZ) > LayerTol) continue;
                var verts = poly.Vertices;
                if (verts == null || verts.Length < 3) continue;

                // Match what raycast sees: every polygon contributes its XY projection.
                // Vertical walls have zero-area shadows (the 4 wall verts share 2 XY
                // positions, so the projected polygon collapses to a line and
                // rasterisation fills nothing). Tilted slabs / borderline-classified
                // polys add their real XY footprint. This catches geometry the
                // floor-orientation classifier was rejecting outright.
                CellType type = ClassifyPoly(verts, layerZ);
                if ((byte)type == NotFloor) type = CellType.Flat;

                RasterizeXY(verts, filled, type);
            }

            // ── Portal gap-closing (width-matched bridges) ───────────────
            // For each pair of portals within 5 units, fill a rectangle
            // bridge between them.  Width = actual doorway opening derived
            // from the portal polygon's larger XY bounding-box dimension.
            {
                var portals = new List<(float cx, float cy, float halfW)>();
                var pDedup  = new HashSet<(int, int)>();
                foreach (var poly in polys)
                {
                    if (!poly.IsPortal) continue;
                    if (MathF.Abs(poly.CellZ - layerZ) > LayerTol) continue;
                    var verts = poly.Vertices;
                    if (verts == null || verts.Length < 3) continue;
                    float cx = 0, cy = 0;
                    float xMin = verts[0].X, xMax = verts[0].X;
                    float yMin = verts[0].Y, yMax = verts[0].Y;
                    for (int k = 0; k < verts.Length; k++)
                    {
                        cx += verts[k].X; cy += verts[k].Y;
                        if (verts[k].X < xMin) xMin = verts[k].X;
                        if (verts[k].X > xMax) xMax = verts[k].X;
                        if (verts[k].Y < yMin) yMin = verts[k].Y;
                        if (verts[k].Y > yMax) yMax = verts[k].Y;
                    }
                    cx /= verts.Length; cy /= verts.Length;
                    float halfW = MathF.Max(xMax - xMin, yMax - yMin) * 0.5f;
                    halfW = MathF.Max(halfW, 1.0f); // minimum 1 unit wide
                    if (pDedup.Add(((int)MathF.Round(cx), (int)MathF.Round(cy))))
                        portals.Add((cx, cy, halfW));
                }

                const float ConnectDist = 5f;

                for (int a = 0; a < portals.Count; a++)
                for (int b = a + 1; b < portals.Count; b++)
                {
                    float ddx = portals[b].cx - portals[a].cx;
                    float ddy = portals[b].cy - portals[a].cy;
                    float distSq = ddx * ddx + ddy * ddy;
                    if (distSq > ConnectDist * ConnectDist) continue;

                    float dist  = MathF.Sqrt(distSq);
                    float hw    = MathF.Max(portals[a].halfW, portals[b].halfW);

                    // Normalised A→B direction, then perpendicular scaled by half-width
                    float normDx = dist > 0.001f ? ddx / dist : 1f;
                    float normDy = dist > 0.001f ? ddy / dist : 0f;
                    float perpX  = -normDy * hw;
                    float perpY  =  normDx * hw;

                    float pax = portals[a].cx, pay = portals[a].cy;
                    float pbx = portals[b].cx, pby = portals[b].cy;

                    // 4 corners of the bridge quad
                    float c0x = pax - perpX, c0y = pay - perpY;
                    float c1x = pax + perpX, c1y = pay + perpY;
                    float c2x = pbx + perpX, c2y = pby + perpY;
                    float c3x = pbx - perpX, c3y = pby - perpY;

                    float bbMinX = MathF.Min(MathF.Min(c0x, c1x), MathF.Min(c2x, c3x));
                    float bbMaxX = MathF.Max(MathF.Max(c0x, c1x), MathF.Max(c2x, c3x));
                    float bbMinY = MathF.Min(MathF.Min(c0y, c1y), MathF.Min(c2y, c3y));
                    float bbMaxY = MathF.Max(MathF.Max(c0y, c1y), MathF.Max(c2y, c3y));

                    int gcxMin2 = (int)MathF.Floor(bbMinX / GridCell);
                    int gcxMax2 = (int)MathF.Ceiling(bbMaxX / GridCell);
                    int gcyMin2 = (int)MathF.Floor(bbMinY / GridCell);
                    int gcyMax2 = (int)MathF.Ceiling(bbMaxY / GridCell);

                    for (int gcy2 = gcyMin2; gcy2 <= gcyMax2; gcy2++)
                    for (int gcx2 = gcxMin2; gcx2 <= gcxMax2; gcx2++)
                    {
                        float testX = (gcx2 + 0.5f) * GridCell;
                        float testY = (gcy2 + 0.5f) * GridCell;
                        if (PointInConvexQuad(testX, testY,
                                c0x, c0y, c1x, c1y, c2x, c2y, c3x, c3y))
                        {
                            var key = (gcx2, gcy2);
                            if (!filled.ContainsKey(key)) filled[key] = CellType.Flat;
                        }
                    }
                }
            }

            // ── Apply user-drawn patches (per-prefab) ────────────────────
            // For each EnvCell at this layer, look up patches keyed by its
            // (envId, cellStructIdx) and overlay them into the world grid.
            // 90°-rotation aware: local (gx,gy) → world via cell.Rotation +
            // cell.WorldX/Y. Same prefab in another dungeon picks up the
            // same patches automatically.
            foreach (var c in lbCells)
            {
                if (MathF.Abs(c.CellZ - layerZ) > LayerTol) continue;
                var cellPatches = _patches.GetPatches(c.EnvironmentId, c.CellStructureIndex);
                if (cellPatches == null || cellPatches.Count == 0) continue;

                float sinR = MathF.Sin(c.Rotation);
                float cosR = MathF.Cos(c.Rotation);

                foreach (var ((lgx, lgy), kind) in cellPatches)
                {
                    // Local-grid centre (lgx,lgy) → local XY → rotated → world XY → world grid
                    float lx = (lgx + 0.5f) * GridCell;
                    float ly = (lgy + 0.5f) * GridCell;
                    float wx = c.WorldX + lx * cosR - ly * sinR;
                    float wy = c.WorldY + lx * sinR + ly * cosR;
                    int wgx = (int)MathF.Floor(wx / GridCell);
                    int wgy = (int)MathF.Floor(wy / GridCell);

                    if (kind == MapPatchStore.PatchKind.Remove)
                    {
                        filled.Remove((wgx, wgy));
                    }
                    else if (!filled.ContainsKey((wgx, wgy)))
                    {
                        // Additive only: don't override polygon-derived slope/flat
                        // classifications. Walking-paint marks every traversed cell
                        // as Flat, but if the polygons say Slope here we want the
                        // polygon's answer to win.
                        var type = kind switch
                        {
                            MapPatchStore.PatchKind.AddSlopeUp   => CellType.SlopeUp,
                            MapPatchStore.PatchKind.AddSlopeDown => CellType.SlopeDown,
                            _                                    => CellType.Flat,
                        };
                        filled[(wgx, wgy)] = type;
                    }
                }
            }

            // ── Edit-mode safety net: zero-coverage cell fallback ────────
            // For every EnvCell at this layer, if NO grid cells in its 10×10
            // world footprint are filled (polygon classification rejected
            // every poly, OR the prefab's geometry only encodes walls/ceiling
            // that fell outside our floor heuristic), fill the entire
            // footprint as Flat. Raycast can see geometry there, so the player
            // can clearly walk there — the map just couldn't classify it. This
            // lights up empty cells immediately on first dungeon entry without
            // the player having to walk through them. L-shape cells with
            // partial polygon coverage are unaffected (they have non-zero
            // coverage so this branch doesn't trigger).
            if (_editMode)
            {
                foreach (var c in lbCells)
                {
                    if (MathF.Abs(c.CellZ - layerZ) > LayerTol) continue;

                    int cgxMin = (int)MathF.Floor((c.WorldX - 5f) / GridCell);
                    int cgxMax = (int)MathF.Ceiling((c.WorldX + 5f) / GridCell);
                    int cgyMin = (int)MathF.Floor((c.WorldY - 5f) / GridCell);
                    int cgyMax = (int)MathF.Ceiling((c.WorldY + 5f) / GridCell);

                    bool anyFilled = false;
                    for (int gy = cgyMin; gy < cgyMax && !anyFilled; gy++)
                    for (int gx = cgxMin; gx < cgxMax; gx++)
                    {
                        if (filled.ContainsKey((gx, gy))) { anyFilled = true; break; }
                    }
                    if (anyFilled) continue;

                    for (int gy = cgyMin; gy < cgyMax; gy++)
                    for (int gx = cgxMin; gx < cgxMax; gx++)
                        filled[(gx, gy)] = CellType.Flat;
                }
            }

            if (filled.Count == 0) continue;

            // ── Outer edges ──────────────────────────────────────────────
            var outer = new List<(float, float, float, float, CellType)>(filled.Count * 2);
            var outerRaw = new List<(float, float, float, float, CellType, int, int)>(filled.Count * 2);
            foreach (var ((gx, gy), type) in filled)
            {
                float x0 = gx * GridCell, y0 = gy * GridCell;
                float x1 = x0 + GridCell, y1 = y0 + GridCell;

                if (!filled.ContainsKey((gx + 1, gy))) { outer.Add((x1, y0, x1, y1, type)); outerRaw.Add((x1, y0, x1, y1, type, gx, gy)); }
                if (!filled.ContainsKey((gx - 1, gy))) { outer.Add((x0, y0, x0, y1, type)); outerRaw.Add((x0, y0, x0, y1, type, gx, gy)); }
                if (!filled.ContainsKey((gx, gy + 1))) { outer.Add((x0, y1, x1, y1, type)); outerRaw.Add((x0, y1, x1, y1, type, gx, gy)); }
                if (!filled.ContainsKey((gx, gy - 1))) { outer.Add((x0, y0, x1, y0, type)); outerRaw.Add((x0, y0, x1, y0, type, gx, gy)); }
            }
            if (outer.Count > 0) _outerEdges[layerZ] = MergeEdges(outer);
            if (outerRaw.Count > 0) (_outerEdgesRaw ??= new())[layerZ] = outerRaw;

            // ── Fill strips: merge cells → horizontal rows → vertical columns ─
            var rows = new Dictionary<int, List<(int gx, CellType type)>>(filled.Count / 4);
            foreach (var ((gx, gy), type) in filled)
            {
                if (!rows.TryGetValue(gy, out var row))
                    rows[gy] = row = new List<(int, CellType)>();
                row.Add((gx, type));
            }

            // Horizontal merge: contiguous cells of same type in each row → one strip
            var hStrips = new List<(float x0, float y0, float x1, float y1, CellType type)>(rows.Count * 2);
            foreach (var (gy, row) in rows)
            {
                row.Sort((a, b) => a.gx.CompareTo(b.gx));
                float y0 = gy * GridCell, y1 = y0 + GridCell;
                int i = 0;
                while (i < row.Count)
                {
                    CellType stripType = row[i].type;
                    int cur = i + 1;
                    while (cur < row.Count
                        && row[cur].gx   == row[cur - 1].gx + 1
                        && row[cur].type == stripType)
                        cur++;
                    hStrips.Add((row[i].gx * GridCell, y0, (row[cur - 1].gx + 1) * GridCell, y1, stripType));
                    i = cur;
                }
            }

            // Vertical merge: rows with identical X extents and type → one tall rect
            var colMap = new Dictionary<(int x0s, int x1s, CellType), List<(float y0, float y1)>>();
            foreach (var (x0, y0, x1, y1, type) in hStrips)
            {
                int x0s = (int)MathF.Round(x0 / GridCell);
                int x1s = (int)MathF.Round(x1 / GridCell);
                var key  = (x0s, x1s, type);
                if (!colMap.TryGetValue(key, out var lst)) colMap[key] = lst = new();
                lst.Add((y0, y1));
            }

            var strips = new List<(float, float, float, float, CellType)>(colMap.Count * 2);
            foreach (var ((x0s, x1s, type), lst) in colMap)
            {
                lst.Sort((a, b) => a.y0.CompareTo(b.y0));
                float rx0 = x0s * GridCell, rx1 = x1s * GridCell;
                int i = 0;
                while (i < lst.Count)
                {
                    float y0 = lst[i].y0, y1 = lst[i].y1;
                    while (i + 1 < lst.Count && lst[i + 1].y0 <= y1 + 0.001f)
                        y1 = MathF.Max(y1, lst[++i].y1);
                    strips.Add((rx0, y0, rx1, y1, type));
                    i++;
                }
            }
            if (strips.Count > 0) _fillStrips[layerZ] = strips;
        }

        // Auto-scan: first time we land in this landblock with auto-scan on, run
        // a discover-everything pass and save the result as patches. Future entries
        // skip this (they already have the patches).
        if (_autoScanEnabled && !_scanInProgress && _lastScannedLandblock != landblock)
        {
            _lastScannedLandblock = landblock;
            _lastScanPatchCount = DoLandblockScan();
        }
    }

    /// <summary>
    /// "Discover everything" pass: re-rasterises the landblock with the most
    /// permissive logic (Edit-mode behavior — every polygon contributes XY shadow,
    /// plus zero-coverage cells get full footprint fill), then captures every
    /// filled grid cell as a per-prefab patch. Patches apply to every dungeon
    /// using the same prefab, so a single scan benefits more than just this map.
    /// </summary>
    private int DoLandblockScan()
    {
        if (_scanInProgress || _lbCells is null || _fillStrips is null)
            return 0;

        _scanInProgress = true;
        try
        {
            // Force Edit-mode rasterisation so the zero-coverage fallback runs.
            bool savedEdit = _editMode;
            _editMode = true;
            _outerEdges = null;
            RefreshMap(_cachedLandblock);
            _editMode = savedEdit;

            int newPatches = 0;
            if (_fillStrips != null && _lbCells != null)
            {
                foreach (var (layerZ, strips) in _fillStrips)
                {
                    foreach (var (x0, y0, x1, y1, type) in strips)
                    {
                        int gxMin = (int)MathF.Round(x0 / GridCell);
                        int gxMax = (int)MathF.Round(x1 / GridCell);
                        int gyMin = (int)MathF.Round(y0 / GridCell);
                        int gyMax = (int)MathF.Round(y1 / GridCell);

                        for (int gy = gyMin; gy < gyMax; gy++)
                        for (int gx = gxMin; gx < gxMax; gx++)
                        {
                            if (CapturePatch(gx, gy, type, layerZ)) newPatches++;
                        }
                    }
                }
            }

            _patches.SaveIfDirty();
            // Force one more refresh so the live render reflects the new patches.
            _outerEdges = null;
            return newPatches;
        }
        finally { _scanInProgress = false; }
    }

    /// <summary>
    /// Convert a world-grid cell into a per-prefab patch by finding which EnvCell
    /// the world coord lies in, inverse-rotating into that cell's local space, and
    /// snapping back to the 0.5u grid. Returns true if a NEW patch was added.
    /// </summary>
    private bool CapturePatch(int wgx, int wgy, CellType type, float layerZ)
    {
        if (_lbCells is null) return false;

        float wcx = (wgx + 0.5f) * GridCell;
        float wcy = (wgy + 0.5f) * GridCell;

        foreach (var c in _lbCells)
        {
            if (MathF.Abs(c.CellZ - layerZ) > 1.0f) continue;

            float relX = wcx - c.WorldX;
            float relY = wcy - c.WorldY;
            float sinR = MathF.Sin(c.Rotation);
            float cosR = MathF.Cos(c.Rotation);
            float lcx =  relX * cosR + relY * sinR;
            float lcy = -relX * sinR + relY * cosR;

            if (lcx < -5.001f || lcx > 5.001f || lcy < -5.001f || lcy > 5.001f) continue;

            int lgx = (int)MathF.Floor(lcx / GridCell);
            int lgy = (int)MathF.Floor(lcy / GridCell);

            var kind = type switch
            {
                CellType.SlopeUp   => MapPatchStore.PatchKind.AddSlopeUp,
                CellType.SlopeDown => MapPatchStore.PatchKind.AddSlopeDown,
                _                  => MapPatchStore.PatchKind.AddFlat,
            };

            // Skip if this prefab already has this exact patch
            var existing = _patches.GetPatches(c.EnvironmentId, c.CellStructureIndex);
            if (existing != null && existing.TryGetValue((lgx, lgy), out var existingKind)
                && existingKind == kind) return false;

            _patches.Set(c.EnvironmentId, c.CellStructureIndex, lgx, lgy, kind);
            return true;
        }
        return false;
    }

    // Reduce a colour to ~25 % opacity for non-current-floor rendering.
    // ImGui packs colours as 0xAABBGGRR; the alpha is in bits 31–24.
    private static uint DimCol(uint c) =>
        ((uint)((c >> 24 & 0xFF) * 0.25f) << 24) | (c & 0x00FFFFFFu);

    // Merge collinear/adjacent axis-aligned edge segments into the fewest possible lines.
    // A 10-unit straight wall built from 20 half-unit grid edges becomes 1 line.
    private static List<(float ax, float ay, float bx, float by, CellType type)> MergeEdges(
        List<(float ax, float ay, float bx, float by, CellType type)> edges)
    {
        // Bucket horizontal (ay==by) by (ySnap, type) → list of x intervals
        // Bucket vertical   (ax==bx) by (xSnap, type) → list of y intervals
        var hBuckets = new Dictionary<(int ySnap, CellType), List<(float lo, float hi)>>();
        var vBuckets = new Dictionary<(int xSnap, CellType), List<(float lo, float hi)>>();

        foreach (var (ax, ay, bx, by, type) in edges)
        {
            if (MathF.Abs(ay - by) < 0.001f) // horizontal
            {
                int ySnap = (int)MathF.Round(ay / GridCell);
                if (!hBuckets.TryGetValue((ySnap, type), out var lst))
                    hBuckets[(ySnap, type)] = lst = new();
                lst.Add((MathF.Min(ax, bx), MathF.Max(ax, bx)));
            }
            else // vertical
            {
                int xSnap = (int)MathF.Round(ax / GridCell);
                if (!vBuckets.TryGetValue((xSnap, type), out var lst))
                    vBuckets[(xSnap, type)] = lst = new();
                lst.Add((MathF.Min(ay, by), MathF.Max(ay, by)));
            }
        }

        var result = new List<(float, float, float, float, CellType)>(hBuckets.Count + vBuckets.Count);

        foreach (var ((ySnap, type), lst) in hBuckets)
        {
            lst.Sort((a, b) => a.lo.CompareTo(b.lo));
            float ay = ySnap * GridCell;
            int i = 0;
            while (i < lst.Count)
            {
                float lo = lst[i].lo, hi = lst[i].hi;
                while (i + 1 < lst.Count && lst[i + 1].lo <= hi + 0.001f)
                    hi = MathF.Max(hi, lst[++i].hi);
                result.Add((lo, ay, hi, ay, type));
                i++;
            }
        }

        foreach (var ((xSnap, type), lst) in vBuckets)
        {
            lst.Sort((a, b) => a.lo.CompareTo(b.lo));
            float ax = xSnap * GridCell;
            int i = 0;
            while (i < lst.Count)
            {
                float lo = lst[i].lo, hi = lst[i].hi;
                while (i + 1 < lst.Count && lst[i + 1].lo <= hi + 0.001f)
                    hi = MathF.Max(hi, lst[++i].hi);
                result.Add((ax, lo, ax, hi, type));
                i++;
            }
        }

        return result;
    }

    // Classify a polygon as Flat / SlopeUp / SlopeDown, or NotFloor if vertical.
    private static CellType ClassifyPoly(Raycasting.Vector3[] verts, float layerZ)
    {
        float nx = 0, ny = 0, nz = 0;
        int n = verts.Length;
        for (int i = 0; i < n; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % n];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 0.0001f) return (CellType)NotFloor;

        float absNz = MathF.Abs(nz) / len;
        if (absNz < FloorNormalThreshold) return (CellType)NotFloor;
        if (absNz >= FlatNormalThreshold)  return CellType.Flat;

        // Slope — direction by average vertex Z vs layer Z
        float avgZ = 0;
        for (int i = 0; i < n; i++) avgZ += verts[i].Z;
        avgZ /= n;
        return avgZ > layerZ + 0.3f ? CellType.SlopeUp : CellType.SlopeDown;
    }

    // Scanline-rasterise a polygon's XY footprint into the typed grid.
    // Vertex cells are explicitly marked first to cover degenerate thin polygons.
    // Flat (0) beats slope if a cell already has a type.
    private static void RasterizeXY(Raycasting.Vector3[] verts, Dictionary<(int, int), CellType> filled, CellType type)
    {
        int n = verts.Length;

        // ── Vertex marking (gap fix for near-zero-area slivers) ──────────
        for (int i = 0; i < n; i++)
        {
            var key = ((int)MathF.Floor(verts[i].X / GridCell),
                       (int)MathF.Floor(verts[i].Y / GridCell));
            if (!filled.TryGetValue(key, out var existing) || type < existing)
                filled[key] = type;
        }

        // ── Scanline fill ─────────────────────────────────────────────────
        float minY = verts[0].Y, maxY = verts[0].Y;
        for (int i = 1; i < n; i++)
        {
            if (verts[i].Y < minY) minY = verts[i].Y;
            if (verts[i].Y > maxY) maxY = verts[i].Y;
        }

        int gyMin = (int)MathF.Floor(minY / GridCell);
        int gyMax = (int)MathF.Ceiling(maxY / GridCell);

        var xs = new List<float>(n);

        for (int gy = gyMin; gy < gyMax; gy++)
        {
            float scanY = (gy + 0.5f) * GridCell;
            xs.Clear();

            for (int i = 0; i < n; i++)
            {
                float ay = verts[i].Y,           ax = verts[i].X;
                float by = verts[(i + 1) % n].Y, bx = verts[(i + 1) % n].X;

                if ((ay <= scanY && by > scanY) || (by <= scanY && ay > scanY))
                    xs.Add(ax + (scanY - ay) / (by - ay) * (bx - ax));
            }

            if (xs.Count < 2) continue;
            xs.Sort();

            for (int i = 0; i + 1 < xs.Count; i += 2)
            {
                int gxMin = (int)MathF.Floor(xs[i]       / GridCell);
                int gxMax = (int)MathF.Ceiling(xs[i + 1] / GridCell);
                for (int gx = gxMin; gx < gxMax; gx++)
                {
                    var key = (gx, gy);
                    if (!filled.TryGetValue(key, out var existing) || type < existing)
                        filled[key] = type;
                }
            }
        }
    }

    // Returns true if (px,py) is inside the convex quad (a→b→c→d).
    // Works for both CW and CCW winding by checking all cross-products share a sign.
    private static bool PointInConvexQuad(
        float px, float py,
        float ax, float ay, float bx, float by,
        float cx, float cy, float dx, float dy)
    {
        static float Cross(float ox, float oy, float ex, float ey, float ptx, float pty)
            => (ex - ox) * (pty - oy) - (ey - oy) * (ptx - ox);

        float s0 = Cross(ax, ay, bx, by, px, py);
        float s1 = Cross(bx, by, cx, cy, px, py);
        float s2 = Cross(cx, cy, dx, dy, px, py);
        float s3 = Cross(dx, dy, ax, ay, px, py);

        return (s0 >= 0f && s1 >= 0f && s2 >= 0f && s3 >= 0f) ||
               (s0 <= 0f && s1 <= 0f && s2 <= 0f && s3 <= 0f);
    }

    private int BestLayerIdx(float playerZ)
    {
        if (_zLayers is null || _zLayers.Count == 0) return 0;
        int best = 0; float bestDist = float.MaxValue;
        for (int i = 0; i < _zLayers.Count; i++)
        {
            float d = MathF.Abs(_zLayers[i] - playerZ);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ── Toolbar ──────────────────────────────────────────────────────────────

    // Returns true if the last-drawn item's right edge + gap + nextWidth fits before the window's right edge.
    private static bool FitsOnLine(float nextWidth, float gap = 8f)
    {
        float itemRight   = ImGui.GetItemRectMax().X;
        float windowRight = ImGui.GetWindowPos().X + ImGui.GetWindowSize().X
                            - ImGui.GetStyle().WindowPadding.X;
        return itemRight + gap + nextWidth < windowRight;
    }

    private void RenderToolbar(float playerZ)
    {
        // ── 1U1D toggle ───────────────────────────────────────────────────
        bool was1U1D = _show1U1D;
        if (was1U1D)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.15f, 0.45f, 0.70f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.58f, 0.88f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.10f, 0.34f, 0.58f, 1.0f));
        }
        if (ImGui.SmallButton("1U1D##1u1d"))
        {
            _show1U1D = !_show1U1D;
            if (_show1U1D && _zLayers is not null)
            {
                _visibleLayers.Clear();
                if (_autoLayerIdx > 0) _visibleLayers.Add(_autoLayerIdx - 1);
                _visibleLayers.Add(_autoLayerIdx);
                if (_autoLayerIdx < _zLayers.Count - 1) _visibleLayers.Add(_autoLayerIdx + 1);
            }
        }
        if (was1U1D) ImGui.PopStyleColor(3);

        // ── Floor buttons ─────────────────────────────────────────────────
        if (_zLayers is not null && _zLayers.Count > 1)
        {
            float floorsWidth = 52f + _zLayers.Count * 26f + 60f; // "Floors:" + Fn buttons + All/Here
            if (FitsOnLine(floorsWidth, 10f)) ImGui.SameLine(0, 10);

            ImGui.Text("Floors:");
            ImGui.SameLine();
            for (int i = 0; i < _zLayers.Count; i++)
            {
                if (i > 0) ImGui.SameLine();
                bool on = _visibleLayers.Contains(i);
                bool isCurrent = i == _autoLayerIdx;
                if (on && isCurrent)
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.65f, 0.30f, 1.0f));
                else if (on)
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.45f, 0.70f, 1.0f));
                if (ImGui.SmallButton($"F{i + 1}##floor{i}"))
                {
                    _show1U1D = false;
                    if (on) _visibleLayers.Remove(i); else _visibleLayers.Add(i);
                }
                if (on) ImGui.PopStyleColor();
            }
            ImGui.SameLine(0, 6);
            if (ImGui.SmallButton("All##floorAll"))
            {
                _show1U1D = false;
                _visibleLayers.Clear();
                for (int i = 0; i < _zLayers.Count; i++) _visibleLayers.Add(i);
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("Here##floorHere"))
            {
                _show1U1D = false;
                _visibleLayers.Clear();
                _visibleLayers.Add(BestLayerIdx(playerZ));
            }
        }

        // ── Zoom ──────────────────────────────────────────────────────────
        if (FitsOnLine(130f, 12f)) ImGui.SameLine(0, 12);
        ImGui.Text("Zoom:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80);
        ImGui.SliderFloat("##mapzoom", ref _zoom, 1.0f, 20.0f, "%.1fx");
        if (!float.IsFinite(_zoom)) _zoom = 5.0f;

        // ── Follow / Reset ────────────────────────────────────────────────
        if (FitsOnLine(110f, 12f)) ImGui.SameLine(0, 12);
        ImGui.Checkbox("Follow##mapfollow", ref _autoFollow);
        ImGui.SameLine(0, 6);
        if (ImGui.SmallButton("Reset##mapreset"))
        { _pan = Vector2.Zero; _zoom = 5.0f; _autoFollow = true; _show1U1D = true; }

        // ── Doors / Creatures ─────────────────────────────────────────────
        if (FitsOnLine(148f, 12f)) ImGui.SameLine(0, 12);
        ImGui.Checkbox("Doors##mapDoors", ref _settings.MapShowDoors);
        ImGui.SameLine(0, 6);
        ImGui.Checkbox("Creatures##mapCreatures", ref _settings.MapShowCreatures);

        // ── Opacity ───────────────────────────────────────────────────────
        if (FitsOnLine(130f, 12f)) ImGui.SameLine(0, 12);
        ImGui.Text("Opacity:");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(70);
        ImGui.SliderFloat("##mapOpacity", ref _settings.MapBgOpacity, 0f, 1f, "%.2f");
        _settings.MapBgOpacity = Math.Clamp(_settings.MapBgOpacity, 0f, 1f);

        // ── Edit mode (paint per-prefab patches over polygon-derived map) ─
        if (FitsOnLine(150f, 12f)) ImGui.SameLine(0, 12);
        bool wasEditMode = _editMode; // snapshot — button click flips _editMode mid-block
        if (wasEditMode)
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.85f, 0.45f, 0.10f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.95f, 0.55f, 0.15f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.65f, 0.35f, 0.05f, 1.0f));
        }
        if (ImGui.SmallButton(wasEditMode ? "Editing##mapEdit" : "Edit##mapEdit"))
        {
            _editMode = !_editMode;
            // Always invalidate so the zero-coverage fallback kicks in (or off)
            // on the next frame and the user sees the toggle take effect immediately.
            _outerEdges = null;
            _hoverPatchCell = null;
            if (!_editMode) _patches.SaveIfDirty();
        }
        if (wasEditMode) ImGui.PopStyleColor(3);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_editMode
                ? "Click to exit edit mode (saves patches).\nEvery cell shows its 10×10 footprint.\nLeft-click paints fine detail, Right-click erases."
                : $"Edit mode: fills empty cells to their full footprint, enables painting.\n{_patches.TotalPatchCount} patches loaded.");

        if (_editMode)
        {
            ImGui.SameLine(0, 6);
            ImGui.Text("Brush:");
            ImGui.SameLine(0, 4);
            int b = (int)_brushKind;
            string[] brushLabels = { "Flat", "SlopeUp", "SlopeDown" };
            ImGui.SetNextItemWidth(90);
            if (ImGui.Combo("##mapBrush", ref b, brushLabels, brushLabels.Length))
                _brushKind = (MapPatchStore.PatchKind)b;
        }

        // ── Scan: capture full-permissive rasterisation as per-prefab patches ─
        if (FitsOnLine(140f, 12f)) ImGui.SameLine(0, 12);
        if (ImGui.SmallButton("Scan##mapScan"))
        {
            _lastScanPatchCount = DoLandblockScan();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Discover every cell in this landblock and save patches.\n" +
                             "Patches apply to all dungeons using the same prefabs.\n" +
                             $"Last scan: {_lastScanPatchCount} new patches\n" +
                             $"Total patches: {_patches.TotalPatchCount}");

        ImGui.SameLine(0, 6);
        ImGui.Checkbox("Auto##mapAutoScan", ref _autoScanEnabled);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Auto-scan on every dungeon entry.\n" +
                             "First entry to a dungeon is ~50–200ms slower.\n" +
                             "After a few dungeons, common prefabs are fully patched\n" +
                             "and future entries are instant.");
    }

    // ── Canvas ────────────────────────────────────────────────────────────────

    private void RenderCanvas(float playerWX, float playerWY, float playerZ, bool showExpandBtn = false)
    {
        Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 50 || canvasSize.Y < 50) return;

        Vector2 origin = ImGui.GetCursorScreenPos();

        // Record canvas screen rect while toolbar is visible so we can snap to it on hide.
        if (!showExpandBtn)
        {
            _canvasPos  = origin;
            _canvasSize = canvasSize;
        }
        Vector2 centre = origin + canvasSize * 0.5f;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        // Only the canvas background scales with opacity — fills and edges stay full colour.
        uint colBgDyn = SetAlpha(ColBg, _settings.MapBgOpacity);

        dl.AddRectFilled(origin, origin + canvasSize, colBgDyn);
        dl.AddRect(origin, origin + canvasSize, ColBorder);
        dl.PushClipRect(origin, origin + canvasSize, true);

        if (_autoFollow) _pan = Vector2.Zero;

        if (_zLayers is not null)
        {
            int best = BestLayerIdx(playerZ);
            if (best != _autoLayerIdx)
            {
                _autoLayerIdx = best;
                if (_show1U1D)
                {
                    _visibleLayers.Clear();
                    if (best > 0) _visibleLayers.Add(best - 1);
                    _visibleLayers.Add(best);
                    if (best < _zLayers.Count - 1) _visibleLayers.Add(best + 1);
                }
                else if (_autoFollow) { _visibleLayers.Clear(); _visibleLayers.Add(best); }
            }
        }

        Span<float> visZ = stackalloc float[_zLayers?.Count ?? 0];
        int visCount = 0;
        if (_zLayers is not null)
            foreach (int idx in _visibleLayers)
                if (idx >= 0 && idx < _zLayers.Count)
                    visZ[visCount++] = _zLayers[idx];

        float cxMin = origin.X, cxMax = origin.X + canvasSize.X;
        float cyMin = origin.Y, cyMax = origin.Y + canvasSize.Y;
        const float Margin = 20f;

        float currentLayerZ = (_zLayers is not null && _autoLayerIdx < _zLayers.Count)
            ? _zLayers[_autoLayerIdx] : float.NaN;

        // ── Floor fill (drawn first, under edges) ────────────────────────
        if (_fillStrips is not null)
        {
            for (int vi = 0; vi < visCount; vi++)
            {
                if (!_fillStrips.TryGetValue(visZ[vi], out var strips)) continue;
                bool isCurrent = MathF.Abs(visZ[vi] - currentLayerZ) < 0.01f;

                foreach (var (x0, y0, x1, y1, type) in strips)
                {
                    float sx0 = centre.X + (x0 - playerWX) * _zoom + _pan.X;
                    float sx1 = centre.X + (x1 - playerWX) * _zoom + _pan.X;
                    float sy1 = centre.Y - (y1 - playerWY) * _zoom + _pan.Y;
                    float sy0 = centre.Y - (y0 - playerWY) * _zoom + _pan.Y;

                    if (sx1 < cxMin - Margin || sx0 > cxMax + Margin ||
                        sy1 < cyMin - Margin || sy0 > cyMax + Margin)
                        continue;

                    uint fillCol = type switch
                    {
                        CellType.SlopeUp   => ColSlopeUpFill,
                        CellType.SlopeDown => ColSlopeDownFill,
                        _                  => ColFlatFill,
                    };
                    if (!isCurrent) fillCol = DimCol(fillCol);

                    dl.AddRectFilled(new Vector2(sx0, sy1), new Vector2(sx1, sy0), fillCol);
                }
            }
        }

        // ── Outer edges ───────────────────────────────────────────────────
        if (_outerEdges is not null)
        {
            for (int vi = 0; vi < visCount; vi++)
            {
                if (!_outerEdges.TryGetValue(visZ[vi], out var edges)) continue;
                bool isCurrent = MathF.Abs(visZ[vi] - currentLayerZ) < 0.01f;

                foreach (var (ax, ay, bx, by, type) in edges)
                {
                    float sx1 = centre.X + (ax - playerWX) * _zoom + _pan.X;
                    float sy1 = centre.Y - (ay - playerWY) * _zoom + _pan.Y;
                    float sx2 = centre.X + (bx - playerWX) * _zoom + _pan.X;
                    float sy2 = centre.Y - (by - playerWY) * _zoom + _pan.Y;

                    if (MathF.Max(sx1, sx2) < cxMin - Margin || MathF.Min(sx1, sx2) > cxMax + Margin ||
                        MathF.Max(sy1, sy2) < cyMin - Margin || MathF.Min(sy1, sy2) > cyMax + Margin)
                        continue;

                    uint col = type switch
                    {
                        CellType.SlopeUp   => ColSlopeUp,
                        CellType.SlopeDown => ColSlopeDown,
                        _                  => ColFlat,
                    };
                    if (!isCurrent) col = DimCol(col);

                    dl.AddLine(new Vector2(sx1, sy1), new Vector2(sx2, sy2), col, 1.5f);
                }
            }
        }

        // ── Teleport portals ──────────────────────────────────────────────
        if (_objectCache != null && _host.HasGetObjectPosition && _host.HasGetItemType)
        {
            const uint TypePortal    = 0x10000u;
            const uint STypePortalDest = 38u;
            float gxLB = ((_cachedLandblock >> 8) & 0xFF) * 192f;
            float gyLB = (_cachedLandblock & 0xFF) * 192f;

            foreach (var wo in _objectCache.GetLandscapeObjects())
            {
                // Static GUIDs only (portals/lifestones are below 0x80000000)
                if ((uint)wo.Id >= 0x80000000u) continue;
                if (!_host.TryGetItemType((uint)wo.Id, out uint flags)) continue;
                if ((flags & TypePortal) == 0) continue;
                if (!_host.TryGetObjectPosition((uint)wo.Id, out uint portalCellId,
                        out float portalX, out float portalY, out _)) continue;
                if ((portalCellId >> 16) != _cachedLandblock) continue;

                float worldX = portalX + gxLB;
                float worldY = portalY + gyLB;
                float psx = centre.X + (worldX - playerWX) * _zoom + _pan.X;
                float psy = centre.Y - (worldY - playerWY) * _zoom + _pan.Y;

                if (psx < cxMin - 20 || psx > cxMax + 20 ||
                    psy < cyMin - 20 || psy > cyMax + 20) continue;

                var psc = new Vector2(psx, psy);
                dl.AddCircleFilled(psc, 5f, ColPortal);
                dl.AddCircle(psc, 7f, ColPortalRing, 12, 1.5f);

                string label = wo.Name;
                if (_host.TryGetObjectStringProperty((uint)wo.Id, STypePortalDest, out string dest)
                    && !string.IsNullOrEmpty(dest))
                    label = dest.Length > 22 ? dest.Substring(0, 22) + "…" : dest;
                dl.AddText(new Vector2(psx + 9f, psy - 7f), ColPortalLabel, label);
            }
        }

        // ── Doors ─────────────────────────────────────────────────────────
        if (_settings.MapShowDoors && _objectCache != null && _host.HasGetObjectPosition)
        {
            float gxLBd = ((_cachedLandblock >> 8) & 0xFF) * 192f;
            float gyLBd = (_cachedLandblock & 0xFF) * 192f;

            foreach (var wo in _objectCache.GetLandscapeObjects())
            {
                if ((uint)wo.Id >= 0x80000000u) continue;
                if (!IsDoorName(wo.Name)) continue;
                if (!_host.TryGetObjectPosition((uint)wo.Id, out uint dCellId,
                        out float dox, out float doy, out _)) continue;
                if ((dCellId >> 16) != _cachedLandblock) continue;

                float dwx = dox + gxLBd;
                float dwy = doy + gyLBd;
                float dsx = centre.X + (dwx - playerWX) * _zoom + _pan.X;
                float dsy = centre.Y - (dwy - playerWY) * _zoom + _pan.Y;

                if (dsx < cxMin - 20 || dsx > cxMax + 20 ||
                    dsy < cyMin - 20 || dsy > cyMax + 20) continue;

                // Diamond symbol
                const float Dr = 5.0f;
                dl.AddQuadFilled(
                    new Vector2(dsx,      dsy - Dr),
                    new Vector2(dsx + Dr, dsy),
                    new Vector2(dsx,      dsy + Dr),
                    new Vector2(dsx - Dr, dsy),
                    ColDoor);
                dl.AddQuad(
                    new Vector2(dsx,          dsy - Dr - 1.5f),
                    new Vector2(dsx + Dr + 1.5f, dsy),
                    new Vector2(dsx,          dsy + Dr + 1.5f),
                    new Vector2(dsx - Dr - 1.5f, dsy),
                    ColDoorRing, 1.2f);
                dl.AddText(new Vector2(dsx + 8f, dsy - 7f), ColDoorLabel, wo.Name.Length > 20 ? wo.Name[..20] + "…" : wo.Name);
            }
        }

        // ── Creatures & NPCs ──────────────────────────────────────────────
        if (_settings.MapShowCreatures && _objectCache != null && _host.HasGetObjectPosition)
        {
            const uint STypeCreatureType = 2u;
            const int  CreatureTypeNpc   = 14;
            bool canQueryCreatureType    = _host.HasGetObjectIntProperty;
            float gxLBc = ((_cachedLandblock >> 8) & 0xFF) * 192f;
            float gyLBc = (_cachedLandblock & 0xFF) * 192f;

            foreach (var wo in _objectCache.GetLandscape())
            {
                if (!_host.TryGetObjectPosition((uint)wo.Id, out uint cCellId,
                        out float cox, out float coy, out _)) continue;
                if ((cCellId >> 16) != _cachedLandblock) continue;

                float cwx = cox + gxLBc;
                float cwy = coy + gyLBc;
                float csx = centre.X + (cwx - playerWX) * _zoom + _pan.X;
                float csy = centre.Y - (cwy - playerWY) * _zoom + _pan.Y;

                if (csx < cxMin - 20 || csx > cxMax + 20 ||
                    csy < cyMin - 20 || csy > cyMax + 20) continue;

                bool isNpc = canQueryCreatureType
                             && _host.TryGetObjectIntProperty((uint)wo.Id, STypeCreatureType, out int ct)
                             && ct == CreatureTypeNpc;

                var csc = new Vector2(csx, csy);
                if (isNpc)
                {
                    dl.AddCircleFilled(csc, 4f, ColNpc);
                    dl.AddCircle(csc, 6f, ColNpcRing, 10, 1.2f);
                }
                else
                {
                    dl.AddCircleFilled(csc, 4f, ColMonster);
                    dl.AddCircle(csc, 6f, ColMonsterRing, 10, 1.0f);
                }

                string clabel = wo.Name.Length > 20 ? wo.Name[..20] + "…" : wo.Name;
                dl.AddText(new Vector2(csx + 7f, csy - 7f), ColCreatureLabel, clabel);
            }
        }

        // ── Player dot + heading ──────────────────────────────────────────
        var playerScreen = new Vector2(centre.X + _pan.X, centre.Y + _pan.Y);
        dl.AddCircleFilled(playerScreen, 4.0f, ColPlayer);

        if (_host.HasGetPlayerHeading && _host.TryGetPlayerHeading(out float heading))
        {
            float rad = heading * MathF.PI / 180.0f;
            var tip = playerScreen + new Vector2(MathF.Cos(rad) * 10f, -MathF.Sin(rad) * 10f);
            dl.AddLine(playerScreen, tip, ColPlayer, 1.5f);
        }

        dl.PopClipRect();

        // Expand button rendered BEFORE InvisibleButton so ImGui gives it
        // input priority over the canvas drag area.  SetCursorScreenPos is
        // restored afterward so InvisibleButton still covers the full canvas.
        if (showExpandBtn)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(4, 4));
            if (ImGui.SmallButton("v##tbToggle")) _settings.MapShowToolbar = true;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show settings");
            ImGui.SetCursorScreenPos(origin);
        }

        // InvisibleButton captures mouse so the window title-bar drag handler
        // never sees the click — prevents the window from moving when panning.
        ImGui.InvisibleButton("##mapcanvas", canvasSize);

        // Tell the backend to block mouse events from reaching the game when
        // the cursor is over the canvas (fixes click-through to AC world).
        bool hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetNextFrameWantCaptureMouse(true);

        if (_editMode)
        {
            // Edit mode: left-click paints, right-click erases. No panning.
            HandleEditMode(dl, hovered, centre, playerWX, playerWY);
        }
        else if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            Vector2 delta = ImGui.GetIO().MouseDelta;
            if (float.IsFinite(delta.X) && float.IsFinite(delta.Y))
            {
                _autoFollow = false;
                _pan += delta;
            }
        }

        // Scroll-wheel zoom: check mouse position directly against canvas bounds.
        // IsWindowHovered can return false if another item is active, so we bypass it.
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (float.IsFinite(wheel) && wheel != 0f)
            {
                var mp = ImGui.GetIO().MousePos;
                if (mp.X >= origin.X && mp.X <= origin.X + canvasSize.X &&
                    mp.Y >= origin.Y && mp.Y <= origin.Y + canvasSize.Y)
                {
                    _zoom = Math.Clamp(_zoom + wheel * 0.5f, 1.0f, 20.0f);
                    ImGui.SetNextFrameWantCaptureMouse(true);
                }
            }
        }
    }

    // Sets the alpha byte of an ImGui ABGR colour to the given 0-1 value.
    private static uint SetAlpha(uint col, float alpha)
    {
        uint a = (uint)(Math.Clamp(alpha, 0f, 1f) * 255f);
        return (col & 0x00FFFFFF) | (a << 24);
    }

    /// <summary>
    /// Walking-paint: the player's current grid cell becomes a Flat patch on the
    /// prefab they're standing in. Called every frame the map renders. The patch
    /// applies to every dungeon that uses the same prefab, so walking one corridor
    /// teaches the map about that corridor everywhere.
    /// </summary>
    private void UpdateWalkPaint(float playerWX, float playerWY, float playerZ)
    {
        if (_lbCells is null || _lbCells.Count == 0) return;

        const float CellHalf = 5f;
        DungeonLOS.MapCell? hit = null;
        int hitLgx = 0, hitLgy = 0;

        foreach (var c in _lbCells)
        {
            // Player Z must be near this cell's floor — ±2u handles small height
            // variation as the player walks across slopes.
            if (MathF.Abs(c.CellZ - playerZ) > 2.0f) continue;

            float relX = playerWX - c.WorldX;
            float relY = playerWY - c.WorldY;
            float sinR = MathF.Sin(c.Rotation);
            float cosR = MathF.Cos(c.Rotation);
            float lx =  relX * cosR + relY * sinR;
            float ly = -relX * sinR + relY * cosR;

            if (lx < -CellHalf - 0.001f || lx > CellHalf + 0.001f ||
                ly < -CellHalf - 0.001f || ly > CellHalf + 0.001f) continue;

            hit = c;
            hitLgx = (int)MathF.Floor(lx / GridCell);
            hitLgy = (int)MathF.Floor(ly / GridCell);
            break;
        }
        if (hit == null) return;

        // Cheap dedup — only act when the player crosses into a new grid cell.
        var key = (hit.EnvironmentId, hit.CellStructureIndex, hitLgx, hitLgy);
        if (_lastWalkPaintCell == key) return;
        _lastWalkPaintCell = key;

        // If the polygon-derived map already covers this cell, no patch needed.
        // (The patch store only fills empties at apply time, but skipping here
        //  also avoids cluttering the file with redundant entries.)
        var existing = _patches.GetPatches(hit.EnvironmentId, hit.CellStructureIndex);
        if (existing != null && existing.ContainsKey((hitLgx, hitLgy))) return;

        _patches.Set(hit.EnvironmentId, hit.CellStructureIndex, hitLgx, hitLgy,
                     MapPatchStore.PatchKind.AddFlat);
        _outerEdges = null; // invalidate so RefreshMap picks up the patch next frame

        // Throttled save — every 15 seconds while dirty so a crash never costs
        // more than the last 15 seconds of exploration.
        if ((DateTime.UtcNow - _lastPatchSave).TotalSeconds >= 15)
        {
            _patches.SaveIfDirty();
            _lastPatchSave = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Edit-mode mouse handling: figure out which prefab the cursor is over, snap to the
    /// 0.5u local grid, draw a brush preview, and on click add/remove the patch (which
    /// applies to every cell in every dungeon that uses that prefab).
    /// </summary>
    private void HandleEditMode(ImDrawListPtr dl, bool hovered, Vector2 centre, float playerWX, float playerWY)
    {
        _hoverPatchCell = null;
        if (!hovered || _lbCells is null) return;

        // Mouse screen → world (inverse of project: sx = centre.X + (wx - playerWX) * zoom + pan.X).
        var mp = ImGui.GetIO().MousePos;
        float mouseWX = playerWX + (mp.X - centre.X - _pan.X) / _zoom;
        float mouseWY = playerWY - (mp.Y - centre.Y - _pan.Y) / _zoom;

        // Find which cell on the current floor contains the mouse.
        float currentLayerZ = (_zLayers is not null && _autoLayerIdx < _zLayers.Count)
            ? _zLayers[_autoLayerIdx] : float.NaN;

        const float CellHalf = 5f;
        DungeonLOS.MapCell? hit = null;
        int hitLgx = 0, hitLgy = 0;

        foreach (var c in _lbCells)
        {
            if (!float.IsNaN(currentLayerZ) && MathF.Abs(c.CellZ - currentLayerZ) > 1.0f) continue;

            float relX = mouseWX - c.WorldX;
            float relY = mouseWY - c.WorldY;
            float sinR = MathF.Sin(c.Rotation);
            float cosR = MathF.Cos(c.Rotation);
            // Inverse rotation (world → cell-local).
            float lx =  relX * cosR + relY * sinR;
            float ly = -relX * sinR + relY * cosR;

            if (lx < -CellHalf - 0.001f || lx > CellHalf + 0.001f ||
                ly < -CellHalf - 0.001f || ly > CellHalf + 0.001f) continue;

            hit = c;
            hitLgx = (int)MathF.Floor(lx / GridCell);
            hitLgy = (int)MathF.Floor(ly / GridCell);
            break;
        }

        if (hit == null) return;

        _hoverPatchCell      = (hitLgx, hitLgy);
        _hoverEnvId          = hit.EnvironmentId;
        _hoverCsIdx          = hit.CellStructureIndex;
        _hoverCellWorldX     = hit.WorldX;
        _hoverCellWorldY     = hit.WorldY;
        _hoverCellRotation   = hit.Rotation;

        // Draw brush highlight at the snapped grid cell, transformed back to screen.
        DrawBrushHighlight(dl, hit, hitLgx, hitLgy, centre, playerWX, playerWY);

        // Apply on click. We respond to active drags too so the user can paint by
        // sweeping the mouse — every frame the cursor sits over a new grid cell, that
        // cell gets patched.
        bool leftDown  = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool rightDown = ImGui.IsMouseDown(ImGuiMouseButton.Right);

        if (leftDown && !rightDown)
        {
            _patches.Set(hit.EnvironmentId, hit.CellStructureIndex, hitLgx, hitLgy, _brushKind);
            _outerEdges = null; // invalidate cache → next frame rebuilds with new patch
        }
        else if (rightDown && !leftDown)
        {
            // Right-click paints "Remove" — explicitly hides a polygon-derived cell.
            // Right-click on an already-removed or empty cell clears the patch entirely.
            var existing = _patches.GetPatches(hit.EnvironmentId, hit.CellStructureIndex);
            if (existing != null && existing.TryGetValue((hitLgx, hitLgy), out var kind))
            {
                if (kind == MapPatchStore.PatchKind.Remove)
                    _patches.Clear(hit.EnvironmentId, hit.CellStructureIndex, hitLgx, hitLgy);
                else
                    _patches.Set(hit.EnvironmentId, hit.CellStructureIndex, hitLgx, hitLgy, MapPatchStore.PatchKind.Remove);
            }
            else
            {
                _patches.Set(hit.EnvironmentId, hit.CellStructureIndex, hitLgx, hitLgy, MapPatchStore.PatchKind.Remove);
            }
            _outerEdges = null;
        }
    }

    private void DrawBrushHighlight(ImDrawListPtr dl, DungeonLOS.MapCell c, int lgx, int lgy,
                                    Vector2 centre, float playerWX, float playerWY)
    {
        // Local-cell rect for the brush.
        float lx0 = lgx * GridCell;
        float ly0 = lgy * GridCell;
        float lx1 = lx0 + GridCell;
        float ly1 = ly0 + GridCell;

        float sinR = MathF.Sin(c.Rotation);
        float cosR = MathF.Cos(c.Rotation);

        Vector2 P(float lx, float ly)
        {
            float wx = c.WorldX + lx * cosR - ly * sinR;
            float wy = c.WorldY + lx * sinR + ly * cosR;
            return new Vector2(centre.X + (wx - playerWX) * _zoom + _pan.X,
                               centre.Y - (wy - playerWY) * _zoom + _pan.Y);
        }
        var p00 = P(lx0, ly0);
        var p10 = P(lx1, ly0);
        var p11 = P(lx1, ly1);
        var p01 = P(lx0, ly1);

        uint fill = _brushKind switch
        {
            MapPatchStore.PatchKind.AddSlopeUp   => 0x6020A0FFu, // ABGR-ish; ImGui packs as little-endian
            MapPatchStore.PatchKind.AddSlopeDown => 0x6040C040u,
            MapPatchStore.PatchKind.Remove       => 0x80404040u,
            _                                    => 0x60FFC040u, // flat
        };
        uint outline = 0xFFFFFFFFu;

        dl.AddQuadFilled(p00, p10, p11, p01, fill);
        dl.AddLine(p00, p10, outline, 1.5f);
        dl.AddLine(p10, p11, outline, 1.5f);
        dl.AddLine(p11, p01, outline, 1.5f);
        dl.AddLine(p01, p00, outline, 1.5f);
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

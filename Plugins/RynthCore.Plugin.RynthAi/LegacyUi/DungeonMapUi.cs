using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
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

    private uint _cachedLandblock;

    private enum CellType : byte { Flat = 0, SlopeUp = 1, SlopeDown = 2 }

    // Pre-built per Z-layer
    private Dictionary<float, List<(float ax, float ay, float bx, float by, CellType type)>>? _outerEdges;
    private Dictionary<float, List<(float x0, float y0, float x1, float y1, CellType type)>>? _fillStrips;
    private List<float>? _zLayers;
    private int _autoLayerIdx;

    private readonly HashSet<int> _visibleLayers = new();

    private float _zoom = 5.0f;
    private Vector2 _pan;
    private bool _autoFollow = true;
    private bool _show1U1D   = true;  // show current floor ± 1 (default)
    private bool _open = true;

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
        ImGui.SetNextWindowSize(new Vector2(480, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Dungeon Map##RynthAiDungeonMap", ref _open,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        { ImGui.End(); return; }

        if (!_open) { DashWindows.ShowDungeonMap = false; ImGui.End(); return; }

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

        if ((cellId & 0xFFFF) == 0xFFFF)
        { ImGui.TextDisabled("No dungeon map — outdoor landblock."); ImGui.End(); return; }

        uint landblock = cellId >> 16;
        if (landblock != _cachedLandblock || _outerEdges is null)
            RefreshMap(landblock);

        if (_outerEdges is null || _outerEdges.Count == 0)
        { ImGui.TextDisabled($"No dungeon geometry for 0x{landblock:X4}."); ImGui.End(); return; }

        float gx = ((landblock >> 8) & 0xFF) * 192.0f;
        float gy =  (landblock        & 0xFF) * 192.0f;

        RenderToolbar(pz);
        RenderCanvas(px + gx, py + gy, pz);
        ImGui.End();
    }

    // ── Data refresh ─────────────────────────────────────────────────────────

    private void RefreshMap(uint landblock)
    {
        _cachedLandblock = landblock;
        _pan = Vector2.Zero;
        _outerEdges = null;
        _fillStrips  = null;

        var los = _raycast?.GeometryLoader?.DungeonLOS;
        if (los is null) return;

        var polys = los.GetDungeonMapPolygons(landblock);
        if (polys.Count == 0) return;

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

                CellType type = ClassifyPoly(verts, layerZ);
                if ((byte)type == NotFloor) continue;

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

            if (filled.Count == 0) continue;

            // ── Outer edges ──────────────────────────────────────────────
            var outer = new List<(float, float, float, float, CellType)>(filled.Count * 2);
            foreach (var ((gx, gy), type) in filled)
            {
                float x0 = gx * GridCell, y0 = gy * GridCell;
                float x1 = x0 + GridCell, y1 = y0 + GridCell;

                if (!filled.ContainsKey((gx + 1, gy))) outer.Add((x1, y0, x1, y1, type));
                if (!filled.ContainsKey((gx - 1, gy))) outer.Add((x0, y0, x0, y1, type));
                if (!filled.ContainsKey((gx, gy + 1))) outer.Add((x0, y1, x1, y1, type));
                if (!filled.ContainsKey((gx, gy - 1))) outer.Add((x0, y0, x1, y0, type));
            }
            if (outer.Count > 0) _outerEdges[layerZ] = MergeEdges(outer);

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

    private void RenderToolbar(float playerZ)
    {
        // ── 1U1D toggle (far left) ────────────────────────────────────────
        bool was1U1D = _show1U1D; // capture BEFORE click changes state
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

        if (_zLayers is not null && _zLayers.Count > 1)
        {
            ImGui.SameLine(0, 10);
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
                    _show1U1D = false; // manual floor selection overrides 1U1D
                    if (on) _visibleLayers.Remove(i); else _visibleLayers.Add(i);
                }
                if (on) ImGui.PopStyleColor();
            }
            ImGui.SameLine(0, 10);
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
            ImGui.SameLine(0, 16);
        }
        else ImGui.SameLine(0, 16);

        ImGui.Text("Zoom:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        ImGui.SliderFloat("##mapzoom", ref _zoom, 1.0f, 20.0f, "%.1fx");
        if (!float.IsFinite(_zoom)) _zoom = 5.0f;
        ImGui.SameLine(0, 16);
        ImGui.Checkbox("Follow", ref _autoFollow);
        ImGui.SameLine();
        if (ImGui.SmallButton("Reset##mapreset"))
        { _pan = Vector2.Zero; _zoom = 5.0f; _autoFollow = true; _show1U1D = true; }

        ImGui.SameLine(0, 16);
        ImGui.Checkbox("Doors##mapDoors", ref _settings.MapShowDoors);
        ImGui.SameLine();
        ImGui.Checkbox("Creatures##mapCreatures", ref _settings.MapShowCreatures);
    }

    // ── Canvas ────────────────────────────────────────────────────────────────

    private void RenderCanvas(float playerWX, float playerWY, float playerZ)
    {
        Vector2 canvasSize = ImGui.GetContentRegionAvail();
        if (canvasSize.X < 50 || canvasSize.Y < 50) return;

        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 centre = origin + canvasSize * 0.5f;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(origin, origin + canvasSize, ColBg);
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

        // InvisibleButton captures mouse so the window title-bar drag handler
        // never sees the click — prevents the window from moving when panning.
        ImGui.InvisibleButton("##mapcanvas", canvasSize);

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            Vector2 delta = ImGui.GetIO().MouseDelta;
            if (float.IsFinite(delta.X) && float.IsFinite(delta.Y))
            {
                _autoFollow = false;
                _pan += delta;
            }
        }

        // Scroll-wheel zoom — checked after InvisibleButton so IsWindowHovered
        // is evaluated with the correct item state.
        {
            float wheel = ImGui.GetIO().MouseWheel;
            if (float.IsFinite(wheel) && wheel != 0f &&
                ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                _zoom = Math.Clamp(_zoom + wheel * 0.5f, 1.0f, 20.0f);
        }
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

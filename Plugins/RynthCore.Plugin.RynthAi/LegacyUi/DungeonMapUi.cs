using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// The dungeon geometry cache behind the radar snapshot (RynthRadarUi.BuildSnapshotJson),
/// which feeds the engine's Radar and Dungeon Map faces. Uses physics floor polygons from
/// the dat for accurate corner coverage: they are rasterised into a typed 2D grid, then
///   • Fill   — grid rows merged into strips (_fillStrips);
///   • Edges  — every filled cell face bordering empty space (_outerEdgesRaw, and merged
///              into the fewest lines in _outerEdges).
/// CellType: Flat, SlopeUp (avg vertex Z above the layer), SlopeDown (below).
/// This was also RynthAi's plugin-drawn Dungeon Map window (with baked D3D9 floor
/// textures); that window is the engine's DungeonMapFace now, so nothing here draws.
/// The floor grids are also baked to disk (Maps/DungeonMapBake) for DrakRemote's /map.
/// </summary>
internal sealed class DungeonMapUi
{
    private MainLogic? _raycast;

    internal uint _cachedLandblock;

    internal enum CellType : byte { Flat = 0, SlopeUp = 1, SlopeDown = 2 }

    // Pre-built per Z-layer — internal so RynthRadarUi can share the cache.
    internal Dictionary<float, List<(float ax, float ay, float bx, float by, CellType type)>>? _outerEdges;
    // Raw (unmerged) outer edges with source grid-cell coord, used by RynthRadarUi
    // for per-cell visited-state coloring.
    internal Dictionary<float, List<(float ax, float ay, float bx, float by, CellType type, int gx, int gy)>>? _outerEdgesRaw;
    internal Dictionary<float, List<(float x0, float y0, float x1, float y1, CellType type)>>? _fillStrips;
    // Per-layer floor cell lookup used to build visited strips.
    internal Dictionary<float, Dictionary<(int, int), CellType>>? _floorCells;
    internal List<float>? _zLayers;

    /// <summary>Force the landblock cache to be built. Used by RynthRadarUi to share walls/fills.</summary>
    internal void EnsureCache(uint landblock)
    {
        if (landblock != _cachedLandblock || _outerEdges is null)
            RefreshMap(landblock);
    }

    internal int BestLayerIdxFor(float playerZ) => BestLayerIdx(playerZ);

    // Last indoor landblock TickBake handed to the bake (0 = none yet).
    private uint _bakeCheckLandblock;

    /// <summary>
    /// Game-thread tick (throttled by the caller): when the player is indoors in a landblock not
    /// seen yet, bake its floor plans to disk for the StatusAgent's /map, whether or not a Radar
    /// or Dungeon Map panel is open (only those build this cache). One pose read here; the dat
    /// read, rasterise and disk work run on a pool thread, once per landblock per session.
    /// Also logs what the background bakes reported.
    /// </summary>
    internal void TickBake(RynthCoreHost host)
    {
        Maps.DungeonMapBake.DrainLog(host.Log);
        if (!host.HasGetPlayerPose
            || !host.TryGetPlayerPose(out uint cellId, out _, out _, out _, out _, out _, out _, out _))
            return;
        uint landblock = cellId >> 16;
        bool isIndoor = (cellId & 0xFFFF) >= 0x100 && landblock != 0;
        if (!isIndoor || landblock == _bakeCheckLandblock) return;
        var los = _raycast?.GeometryLoader?.DungeonLOS;
        if (los is null) return; // dats still loading; try again next tick
        _bakeCheckLandblock = landblock;
        Maps.DungeonMapBake.RequestFromDat(los, landblock);
    }

    private const float GridCell             = 0.5f;
    private const float FloorNormalThreshold = 0.4f;  // |Nz|/|N| — must exceed to be floor-like
    private const float FlatNormalThreshold  = 0.95f; // |Nz|/|N| — must exceed to be flat (not a ramp)
    private const byte  NotFloor            = 255;    // sentinel returned by ClassifyPoly

    public void SetRaycast(MainLogic raycast)
    {
        _raycast = raycast;
        _outerEdges = null;
        _fillStrips = null;
        _floorCells = null;
        _cachedLandblock = 0;
    }

    // ── Data refresh ─────────────────────────────────────────────────────────

    private void RefreshMap(uint landblock)
    {
        _cachedLandblock = landblock;
        _outerEdges = null;
        _outerEdgesRaw = null;
        _fillStrips  = null;
        _floorCells  = null;

        var los = _raycast?.GeometryLoader?.DungeonLOS;
        if (los is null) return;

        // Physics floor polygons for rasterisation; render polys for portal bridges
        var floorPolys  = los.GetDungeonMapFloorPolygons(landblock);
        var portalPolys = los.GetDungeonMapPolygons(landblock);
        if (floorPolys.Count == 0 && portalPolys.Count == 0) return;

        _zLayers = LayerZs(floorPolys, portalPolys);

        _outerEdges = new Dictionary<float, List<(float, float, float, float, CellType)>>(_zLayers.Count);
        _fillStrips  = new Dictionary<float, List<(float, float, float, float, CellType)>>(_zLayers.Count);

        foreach (float layerZ in _zLayers)
        {
            var filled = RasterizeLayer(floorPolys, portalPolys, layerZ);

            if (filled.Count == 0) continue;

            // Snapshot the typed cell grid so RynthRadarUi can look up per-cell type for visited coloring.
            (_floorCells ??= new())[layerZ] = new Dictionary<(int, int), CellType>(filled);

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

        // Disk floor plans for DrakRemote (StatusAgent /map). Once per landblock, off this
        // thread; a plan that is already on disk with the same bytes is left alone.
        Maps.DungeonMapBake.Request(landblock, _zLayers, _floorCells);
    }

    /// <summary>The floor heights (cell Z rounded to 0.1) of a landblock's polygons, ascending.
    /// A layer's index in this list is the layer number in the baked map file names.</summary>
    internal static List<float> LayerZs(List<DungeonLOS.MapPolygon> floorPolys, List<DungeonLOS.MapPolygon> portalPolys)
    {
        var zSet = new SortedSet<float>();
        foreach (var p in floorPolys)  zSet.Add((float)Math.Round(p.CellZ, 1));
        foreach (var p in portalPolys) zSet.Add((float)Math.Round(p.CellZ, 1));
        return new List<float>(zSet);
    }

    /// <summary>
    /// The typed floor-cell grid of one layer (GridCell squares): physics floor polygons
    /// rasterised, plus width-matched bridges between nearby portals. Pure; no state.
    /// </summary>
    internal static Dictionary<(int, int), CellType> RasterizeLayer(
        List<DungeonLOS.MapPolygon> floorPolys, List<DungeonLOS.MapPolygon> portalPolys, float layerZ)
    {
        const float LayerTol = 1.0f;
        var filled = new Dictionary<(int, int), CellType>();

        foreach (var poly in floorPolys)
        {
            if (MathF.Abs(poly.CellZ - layerZ) > LayerTol) continue;
            var verts = poly.Vertices;
            if (verts == null || verts.Length < 3) continue;

            CellType type = ClassifyPoly(verts, layerZ);
            if ((byte)type == NotFloor) continue; // physics floor polys are upward-facing; skip any edge cases

            RasterizeXY(verts, filled, type);
        }

        // ── Portal gap-closing (width-matched bridges) ───────────────
        // For each pair of portals within 5 units, fill a rectangle
        // bridge between them.  Width = actual doorway opening derived
        // from the portal polygon's larger XY bounding-box dimension.
        {
            var portals = new List<(float cx, float cy, float halfW)>();
            var pDedup  = new HashSet<(int, int)>();
            foreach (var poly in portalPolys)
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

        return filled;
    }

    /// <summary>
    /// The layer heights and per-layer floor grids of a landblock, the same ones RefreshMap
    /// builds, without touching this instance's cache. Used by the background map bake. Layers
    /// with no cells are left out of the dictionary (but keep their index in the list).
    /// </summary>
    internal static (List<float> ZLayers, Dictionary<float, Dictionary<(int, int), CellType>> Cells) BuildFloorCells(
        DungeonLOS los, uint landblock)
    {
        var floorPolys  = los.GetDungeonMapFloorPolygons(landblock);
        var portalPolys = los.GetDungeonMapPolygons(landblock);
        var cells = new Dictionary<float, Dictionary<(int, int), CellType>>();
        if (floorPolys.Count == 0 && portalPolys.Count == 0) return (new List<float>(), cells);
        var zLayers = LayerZs(floorPolys, portalPolys);
        foreach (float layerZ in zLayers)
        {
            var filled = RasterizeLayer(floorPolys, portalPolys, layerZ);
            if (filled.Count > 0) cells[layerZ] = filled;
        }
        return (zLayers, cells);
    }

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

    // Merge a set of flat visited grid cells into horizontal+vertical strips.
    // Same algorithm as the main fill-strip builder; keeps vertex count O(strips) not O(cells).
    internal static List<(float x0, float y0, float x1, float y1)> BuildVisitedStripsFrom(
        IEnumerable<(int gx, int gy)> cells)
    {
        var rows = new Dictionary<int, List<int>>();
        foreach (var (gx, gy) in cells)
        {
            if (!rows.TryGetValue(gy, out var row)) rows[gy] = row = new List<int>();
            row.Add(gx);
        }
        if (rows.Count == 0) return new List<(float, float, float, float)>();

        var hStrips = new List<(float x0, float y0, float x1, float y1)>(rows.Count * 2);
        foreach (var (gy, row) in rows)
        {
            row.Sort();
            float y0 = gy * GridCell, y1 = y0 + GridCell;
            int i = 0;
            while (i < row.Count)
            {
                int cur = i + 1;
                while (cur < row.Count && row[cur] == row[cur - 1] + 1) cur++;
                hStrips.Add((row[i] * GridCell, y0, (row[cur - 1] + 1) * GridCell, y1));
                i = cur;
            }
        }

        var colMap = new Dictionary<(int x0s, int x1s), List<(float y0, float y1)>>();
        foreach (var (x0, y0, x1, y1) in hStrips)
        {
            int x0s = (int)MathF.Round(x0 / GridCell);
            int x1s = (int)MathF.Round(x1 / GridCell);
            var key = (x0s, x1s);
            if (!colMap.TryGetValue(key, out var lst)) colMap[key] = lst = new();
            lst.Add((y0, y1));
        }

        var result = new List<(float, float, float, float)>(colMap.Count * 2);
        foreach (var ((x0s, x1s), lst) in colMap)
        {
            lst.Sort((a, b) => a.y0.CompareTo(b.y0));
            float rx0 = x0s * GridCell, rx1 = x1s * GridCell;
            int i = 0;
            while (i < lst.Count)
            {
                float y0 = lst[i].y0, y1 = lst[i].y1;
                while (i + 1 < lst.Count && lst[i + 1].y0 <= y1 + 0.001f)
                    y1 = MathF.Max(y1, lst[++i].y1);
                result.Add((rx0, y0, rx1, y1));
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

    // ── Canvas ────────────────────────────────────────────────────────────────

}

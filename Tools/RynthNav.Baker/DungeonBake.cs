using RynthCore2.TerrainData;
using System.Diagnostics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;
using DotRecast.Recast.Geom;
using DotRecast.Recast.Toolset.Builder;
using RynthNav.Routing;
using V3 = System.Numerics.Vector3;

namespace RynthNav.Baker;

// Dungeons: the EnvCells that are not buildings (CollisionGeometry.AppendDungeonCells), baked into
// dun_LLLL.tile (DungeonTileFile: every Detour tile of the dungeon in one file, on a frame shifted so it
// is positive). A dungeon landblock keeps its surface tile (nav_LLLL.tile, the terrain nobody can use
// from inside), so the dungeon's file has its own name and the plugin keeps it in a mesh of its own.
//  --dungeons-survey [--limit n]        read every dungeon landblock and print its size and bounds (writes nothing)
//  --dungeons all|LLLL,LLLL --out <dir> bake them
internal static class DungeonBake
{
    private const float Lb = 192f;

    /// <summary>The cell dat's landblocks that have dungeon cells, ascending.</summary>
    public static List<uint> Find(CollisionGeometry cg, Action<string>? log = null)
    {
        var list = new List<uint>();
        for (uint lb = 0; lb <= 0xFFFF; lb++)
            if (cg.DungeonCellCount(lb) > 0) list.Add(lb);
        log?.Invoke($"{list.Count} landblocks have dungeon cells");
        return list;
    }

    /// <summary>A dungeon landblock's collision triangles (world metres, AC frame: x east, y north, z up).</summary>
    public static List<CollisionGeometry.Tri> Gather(CollisionGeometry cg, uint lb, out int cells)
    {
        var tris = new List<CollisionGeometry.Tri>();
        CollisionGeometry.Climb = NavBake.AgentMaxClimb;
        cells = 0;
        try { cells = cg.AppendDungeonCells(lb, tris); } catch { }
        try { cg.AppendStatics(lb, tris); } catch { }   // objects the landblock places (no terrain to stand on: lowest point is the base)
        return tris;
    }

    private static bool Fin(float a, float b, float c) => float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c);

    // ── Survey ───────────────────────────────────────────────────────────────

    public static int Survey(CollisionGeometry cg, int limit, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        List<uint> all = Find(cg, log);
        int n = 0, outside = 0, empty = 0; long totalTris = 0;
        var rows = new List<(uint lb, int cells, int tris, float x0, float x1, float y0, float y1, float z0, float z1, bool outs)>();
        foreach (uint lb in all)
        {
            if (n++ >= limit) break;
            var tris = Gather(cg, lb, out int cells);
            if (tris.Count == 0) { empty++; continue; }
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
            float offX = ((lb >> 8) & 0xFF) * Lb, offY = (lb & 0xFF) * Lb;
            foreach (var t in tris)
                foreach (V3 v in new[] { t.A, t.B, t.C })
                {
                    if (!Fin(v.X, v.Y, v.Z)) continue;
                    x0 = Math.Min(x0, v.X - offX); x1 = Math.Max(x1, v.X - offX);
                    y0 = Math.Min(y0, v.Y - offY); y1 = Math.Max(y1, v.Y - offY);
                    z0 = Math.Min(z0, v.Z); z1 = Math.Max(z1, v.Z);
                }
            bool outs = x0 < -0.01f || y0 < -0.01f || x1 > Lb + 0.01f || y1 > Lb + 0.01f;
            if (outs) outside++;
            totalTris += tris.Count;
            rows.Add((lb, cells, tris.Count, x0, x1, y0, y1, z0, z1, outs));
        }
        foreach (var r in rows.OrderByDescending(r => r.tris).Take(10))
            log($"  0x{r.lb:X4}: {r.cells} cells, {r.tris} tris, x {r.x0:F0}..{r.x1:F0}  y {r.y0:F0}..{r.y1:F0}  z {r.z0:F0}..{r.z1:F0}{(r.outs ? "  OUTSIDE THE LANDBLOCK" : "")}");
        log($"surveyed {rows.Count} dungeons ({empty} without geometry): {totalTris} triangles, avg {(rows.Count == 0 ? 0 : totalTris / rows.Count)}; " +
            $"{outside} reach outside their landblock; cells read {cg.CellsRead}, failed {cg.CellsFailed}, objects {cg.CellObjects}; {sw.Elapsed.TotalSeconds:F1} s");
        return 0;
    }

    // ── Models: what the bake does with each object of a landblock ──────────────

    /// <summary>--dungeon-models LLLL: every model placed in the landblock's dungeon cells and info, how many collision triangles each gives.</summary>
    public static int Models(CollisionGeometry cg, uint lb, Action<string> log)
    {
        var count = new Dictionary<uint, int>();
        var cellTris = new List<CollisionGeometry.Tri>();
        cg.AppendDungeonCells(lb, cellTris, pl => { count[pl.Model] = count.GetValueOrDefault(pl.Model) + 1; return true; });
        foreach (var pl in cg.ReadLandBlockInfo(lb)) count[pl.Model] = count.GetValueOrDefault(pl.Model) + 1;
        int zero = 0, zeroPlacements = 0;
        foreach (var kv in count.OrderByDescending(k => k.Value))
        {
            var tris = new List<CollisionGeometry.Tri>();
            cg.AppendModel(kv.Key, new Frame(), 1f, 0, 0, tris);
            if (tris.Count > 0) continue;
            zero++; zeroPlacements += kv.Value;
            string detail;
            if ((kv.Key >> 24) == 0x01)
            {
                var g = cg.GfxObj(kv.Key);
                detail = g == null ? "GfxObj unreadable" : $"GfxObj flags 0x{g.Flags:X} physics polys {g.Phys.Count}, drawing polys {g.Draw.Count}";
            }
            else
            {
                var st = cg.SetupModel(kv.Key);
                if (st == null) detail = "Setup unreadable";
                else
                {
                    var parts = st.Parts.Select(pid => cg.GfxObj(pid)).ToList();
                    detail = $"Setup flags 0x{st.Flags:X}, {st.Parts.Length} parts (physics BSP {parts.Count(g => g != null && (g.Flags & 1) != 0 && g.Phys.Count > 0)}, drawing only {parts.Count(g => g != null && g.Draw.Count > 0 && g.Phys.Count == 0)}), cylinders {st.Cyl.Count}, spheres {st.Sph.Count}, height {st.Height:F2} radius {st.Radius:F2}";
                }
            }
            // Size of what is drawn (all parts, ignoring part frames): a panel is tall and thin.
            var meshes = (kv.Key >> 24) == 0x01 ? new[] { cg.GfxObj(kv.Key) } : (cg.SetupModel(kv.Key)?.Parts.Select(pid => cg.GfxObj(pid)).ToArray() ?? Array.Empty<CollisionGeometry.Mesh?>());
            V3 lo = new(float.MaxValue), hi = new(float.MinValue);
            foreach (var m in meshes)
                if (m != null) foreach (var poly in m.Draw) foreach (int vid in poly) if (m.Verts.TryGetValue(vid, out V3 v)) { lo = V3.Min(lo, v); hi = V3.Max(hi, v); }
            string size = hi.X < lo.X ? "" : $" size {hi.X - lo.X:F1} x {hi.Y - lo.Y:F1} x {hi.Z - lo.Z:F1} m";
            log($"  {kv.Key:X8} x{kv.Value}: no collision - {detail};{size}");
        }
        log($"0x{lb:X4}: {count.Count} distinct models, {count.Values.Sum()} placements; {zero} models ({zeroPlacements} placements) give no collision triangles");
        return 0;
    }

    /// <summary>--dungeons near --lb LLLL --at x,y[,r]: the collision triangles gathered within r metres (default 1) of a landblock-local point.</summary>
    public static int Near(CollisionGeometry cg, uint lb, string at, Action<string> log)
    {
        var a = at.Split(',');
        float x = float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture), y = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture);
        float r = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 1f;
        float offX = ((lb >> 8) & 0xFF) * Lb, offY = (lb & 0xFF) * Lb;
        var tris = Gather(cg, lb, out int cells);
        int n = 0;
        foreach (var t in tris)
        {
            V3 c = (t.A + t.B + t.C) / 3f;
            if (Math.Abs(c.X - offX - x) > r || Math.Abs(c.Y - offY - y) > r) continue;
            n++;
            if (n <= 40)
            {
                V3 lo = V3.Min(t.A, V3.Min(t.B, t.C)), hi = V3.Max(t.A, V3.Max(t.B, t.C));
                log($"  tri: x {lo.X - offX:F2}..{hi.X - offX:F2}  y {lo.Y - offY:F2}..{hi.Y - offY:F2}  z {lo.Z:F2}..{hi.Z:F2}");
            }
        }
        log($"{n} of {tris.Count} triangles within {r} m of ({x}, {y}) in 0x{lb:X4}");
        return 0;
    }

    /// <summary>--dungeons model --model HEX: one model's collision as the bake sees it (triangles, size, shape).</summary>
    public static int ModelInfo(CollisionGeometry cg, uint model, Action<string> log)
    {
        var tris = new List<CollisionGeometry.Tri>();
        cg.AppendModel(model, new Frame(), 1f, 0, 0, tris);
        V3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var t in tris) foreach (V3 v in new[] { t.A, t.B, t.C }) { lo = V3.Min(lo, v); hi = V3.Max(hi, v); }
        string kind;
        if ((model >> 24) == 0x01) kind = "GfxObj";
        else
        {
            var st = cg.SetupModel(model);
            if (st == null) { log($"{model:X8}: Setup unreadable"); return 1; }
            var parts = st.Parts.Select(pid => cg.GfxObj(pid)).ToList();
            kind = $"Setup flags 0x{st.Flags:X}, {st.Parts.Length} parts; physics BSP {parts.Count(g => g != null && (g.Flags & 1) != 0 && g.Phys.Count > 0)}, cylinders {st.Cyl.Count}, spheres {st.Sph.Count}, height {st.Height:F2} radius {st.Radius:F2}, step up {st.StepUp:F2}";
            foreach (var c in st.Cyl) log($"   cylinder: bottom ({c.BottomCenter.X:F2}, {c.BottomCenter.Y:F2}, {c.BottomCenter.Z:F2}) radius {c.Radius:F2} height {c.Height:F2}");
        }
        log($"{model:X8}: {kind}");
        log(tris.Count == 0 ? "   no collision triangles" : $"   {tris.Count} collision triangles, x {lo.X:F2}..{hi.X:F2}  y {lo.Y:F2}..{hi.Y:F2}  z {lo.Z:F2}..{hi.Z:F2}  (size {hi.X - lo.X:F2} x {hi.Y - lo.Y:F2} x {hi.Z - lo.Z:F2} m)");
        return 0;
    }

    // ── Bake ─────────────────────────────────────────────────────────────────

    public sealed class Result
    {
        public int Cells, Tris, Tiles, Polys, ShiftX, ShiftY, Pieces;
        public double Share;                 // polygons in the largest connected piece / all polygons
        public List<int> PieceSizes = new();
        public bool Written;
        public string Note = "";
    }

    private static int FloorDiv192(double v) => (int)Math.Floor(v / Lb);

    /// <summary>One dungeon landblock into <paramref name="outDir"/>\dun_LLLL.tile (see DungeonTileFile for the frame).</summary>
    public static Result BakeOne(CollisionGeometry cg, uint lb, string outDir, float agentRadius)
    {
        var res = new Result();
        var tris = Gather(cg, lb, out res.Cells);
        float offX = ((lb >> 8) & 0xFF) * Lb, offY = (lb & 0xFF) * Lb;

        // Landblock-local bounds of what is usable.
        float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        var good = new List<CollisionGeometry.Tri>(tris.Count);
        foreach (var t in tris)
        {
            if (!Fin(t.A.X, t.A.Y, t.A.Z) || !Fin(t.B.X, t.B.Y, t.B.Z) || !Fin(t.C.X, t.C.Y, t.C.Z)) continue;
            if (V3.Cross(t.B - t.A, t.C - t.A).LengthSquared() < 1e-10f) continue;   // degenerate
            good.Add(t);
            foreach (V3 v in new[] { t.A, t.B, t.C })
            {
                minX = Math.Min(minX, v.X - offX); maxX = Math.Max(maxX, v.X - offX);
                minY = Math.Min(minY, v.Y - offY); maxY = Math.Max(maxY, v.Y - offY);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }
        }
        res.Tris = good.Count;
        if (good.Count == 0) { res.Note = "no geometry"; return res; }

        // Shift so every mesh coordinate is positive and the tile grid starts on a multiple of 192.
        int sx = minX < 0 ? (int)(Lb * Math.Ceiling(-minX / Lb)) : 0, sy = minY < 0 ? (int)(Lb * Math.Ceiling(-minY / Lb)) : 0;
        res.ShiftX = sx; res.ShiftY = sy;
        var verts = new List<float>(good.Count * 9); var faces = new List<int>(good.Count * 3);
        int Add(V3 v) { verts.Add(v.X - offX + sx); verts.Add(v.Z); verts.Add(v.Y - offY + sy); return verts.Count / 3 - 1; }   // AC (x, y, z) -> Recast (EW, up, NS)
        foreach (var t in good)
        {
            int a = Add(t.A), b = Add(t.B), c = Add(t.C);
            faces.Add(a); faces.Add(c); faces.Add(b);   // reversed winding (+Y up), as the terrain bake
        }
        // Two unused vertices put the bounds (so the tile grid and the height origin) on 192 m lines.
        float boxMinX = FloorDiv192(minX + sx) * Lb, boxMinZ = FloorDiv192(minY + sy) * Lb;
        float boxMaxX = (FloorDiv192(maxX + sx) + 1) * Lb, boxMaxZ = (FloorDiv192(maxY + sy) + 1) * Lb;
        verts.Add(boxMinX); verts.Add(minZ); verts.Add(boxMinZ);
        verts.Add(boxMaxX); verts.Add(maxZ); verts.Add(boxMaxZ);

        var geom = new RcSampleInputGeomProvider(verts.ToArray(), faces.ToArray());
        geom.CalculateNormals();
        var settings = NavBake.Settings(agentRadius, tiled: true, cellSize: NavBake.TiledCellSizeValue);
        NavMeshBuildResult nm = new TileNavMeshBuilder().Build(geom, settings);
        if (!nm.Success || nm.NavMesh == null) { res.Note = "Recast failed"; return res; }

        RcVec3f bmin = geom.GetMeshBoundsMin();
        if (Math.Abs(bmin.X - boxMinX) > 0.01f || Math.Abs(bmin.Z - boxMinZ) > 0.01f) { res.Note = $"grid offset ({bmin.X - boxMinX:F2}, {bmin.Z - boxMinZ:F2})"; return res; }
        int baseTx = (int)Math.Round(bmin.X / Lb), baseTy = (int)Math.Round(bmin.Z / Lb);

        DtNavMesh nav = nm.NavMesh;
        var tiles = new List<DtMeshData>();
        for (int i = 0; i < nav.GetMaxTiles(); i++)
        {
            DtMeshTile? tile = nav.GetTile(i);
            if (tile?.data?.header == null || tile.data.header.polyCount == 0) continue;
            DtMeshData md = tile.data;
            md.header.x = baseTx + md.header.x;   // absolute index on the shifted frame
            md.header.y = baseTy + md.header.y;
            tiles.Add(md);
            res.Polys += md.header.polyCount;
        }
        if (tiles.Count == 0) { res.Note = "no polygons"; return res; }
        res.PieceSizes = Components(nav).OrderByDescending(x => x).ToList();
        res.Pieces = res.PieceSizes.Count; res.Share = res.PieceSizes.Count == 0 ? 0 : (double)res.PieceSizes[0] / res.PieceSizes.Sum();
        OutputGuard.Check(outDir);
        DungeonTileFile.Write(Path.Combine(outDir, DungeonTileFile.FileName(lb)), sx, sy, tiles);
        res.Tiles = tiles.Count; res.Written = true;
        return res;
    }

    /// <summary>Sizes (polygon counts) of the connected pieces of a navmesh, joined by Detour's polygon links.</summary>
    public static List<int> Components(DtNavMesh nav)
    {
        var seen = new HashSet<long>();
        var sizes = new List<int>();
        for (int ti = 0; ti < nav.GetMaxTiles(); ti++)
        {
            DtMeshTile? tile = nav.GetTile(ti);
            if (tile?.data?.header == null) continue;
            long baseRef = nav.GetPolyRefBase(tile);
            for (int pi = 0; pi < tile.data.header.polyCount; pi++)
            {
                long start = baseRef | (long)pi;
                if (!seen.Add(start)) continue;
                int size = 0;
                var stack = new Stack<long>(); stack.Push(start);
                while (stack.Count > 0)
                {
                    long r = stack.Pop(); size++;
                    if (nav.GetTileAndPolyByRef(r, out DtMeshTile t, out DtPoly p).Failed()) continue;
                    for (int k = p.firstLink; k != DtDetour.DT_NULL_LINK; k = t.links[k].next)
                    {
                        long n = t.links[k].refs;
                        if (n != 0 && seen.Add(n)) stack.Push(n);
                    }
                }
                sizes.Add(size);
            }
        }
        return sizes;
    }

    public static int Run(CollisionGeometry cg, string spec, int limit, string outDir, float agentRadius, Action<string> log)
    {
        List<uint> lbs = spec.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? Find(cg, log)
            : spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(s => Convert.ToUInt32(s, 16)).ToList();
        var sw = Stopwatch.StartNew();
        int ok = 0, none = 0, done = 0, tilesN = 0, lowShare = 0; long polys = 0, bytes = 0; double shareSum = 0;
        foreach (uint lb in lbs)
        {
            if (done++ >= limit) break;
            Result r;
            try { r = BakeOne(cg, lb, outDir, agentRadius); }
            catch (InvalidOperationException) { throw; }
            catch (Exception ex) { r = new Result { Note = ex.GetType().Name + ": " + ex.Message }; }
            if (r.Written) { ok++; polys += r.Polys; tilesN += r.Tiles; bytes += new FileInfo(Path.Combine(outDir, DungeonTileFile.FileName(lb))).Length; }
            else { none++; log($"  0x{lb:X4}: not written ({r.Note})"); }
            if (lbs.Count <= 20) log($"  0x{lb:X4}: {r.Cells} cells, {r.Tris} tris -> {r.Tiles} tile(s), {r.Polys} polys, shift ({r.ShiftX}, {r.ShiftY}); biggest piece {r.Share:P0} of {r.Pieces} pieces (largest {string.Join(",", r.PieceSizes.Take(6))}){(r.Note.Length > 0 ? ", " + r.Note : "")}");
            if (r.Written) { shareSum += r.Share; if (r.Share < 0.5) lowShare++; }
            if (done % 50 == 0) log($"  {done}/{lbs.Count} ... ({ok} dungeons, {sw.Elapsed.TotalSeconds:F0} s)");
        }
        log($"dungeon bake DONE: {ok} dungeons ({tilesN} tiles, {polys} polygons, {bytes / 1024} KB), {none} not written; mean biggest-piece share {(ok == 0 ? 0 : shareSum / ok):P0}, {lowShare} under 50%; {sw.Elapsed.TotalSeconds:F1} s.");
        return 0;
    }
}

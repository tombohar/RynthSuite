using System.Text;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using DotRecast.Recast.Toolset;
using DotRecast.Recast.Toolset.Builder;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using RynthCore.TerrainData;
using RynthCore.Plugin.RynthAi.Raycasting; // LandblockData

namespace RynthNav.Baker;

// Bakes AC landblocks into Detour tiles.
//  - Solo (--lb):    one self-contained tile per landblock.
//  - Tiled (--tiled): a whole region baked as ONE connected tiled navmesh, then
//    each tile serialized with its ABSOLUTE landblock coords in the header so the
//    plugin can stream a window of them into a multi-tile navmesh that connects.
internal static class NavBake
{
    private const float CL = LandblockData.CellLength;     // 24
    public const int VertsPerPoly = 6;
    // Tiled tiles align 1:1 to landblocks: tileSize(cells) * cellSize = 192.
    /// <summary>
    /// The tiled bake's cell size (--cell). Recast erodes the walkable ground by ceil(agentRadius / cell) whole
    /// cells, so a 0.6 m radius at 0.5 m cells takes 1.0 m off each side and shuts every doorway under 2 m;
    /// finer cells keep the erosion near the real radius. Tiles stay one landblock: tileSize = 192 / cell.
    /// </summary>
    internal static float TiledCellSizeValue = 0.5f;
    private static float TiledCellSize => TiledCellSizeValue;
    private static int TiledTileSize => (int)Math.Round(192f / TiledCellSizeValue);

    /// <summary>--draw-geometry: bake from the drawing meshes like before (for comparisons only).</summary>
    public static bool DrawMeshes;

    private static CollisionGeometry? _collision;
    /// <summary>One CollisionGeometry (and its model caches) per GeometryLoader.</summary>
    public static CollisionGeometry Collision(RynthCore2.Raycast.GeometryLoader geo)
    {
        if (_collision == null || !ReferenceEquals(_collision.Geo, geo)) _collision = new CollisionGeometry(geo);
        return _collision;
    }

    private static RcVec3f AcToRec(double ewX, double nsY, double upZ) => new((float)ewX, (float)upZ, (float)nsY);
    private static (double ew, double ns, double up) RecToAc(RcVec3f v) => (v.X, v.Z, v.Y);

    // ── Geometry: append one landblock's terrain + obstacle triangles (Recast frame) ──
    public static bool AppendLandblock(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader? geo, uint lb, List<float> verts, List<int> faces)
    {
        LandblockData? land = sampler.LoadLandblock(lb);
        if (land == null) return false;

        void Tri(int a, int b, int c) { faces.Add(a); faces.Add(c); faces.Add(b); } // reversed winding (+Y up)
        int AddVtx(double ax, double ay, double az) { RcVec3f r = AcToRec(ax, ay, az); verts.Add(r.X); verts.Add(r.Y); verts.Add(r.Z); return verts.Count / 3 - 1; }

        int[,] vidx = new int[9, 9];
        for (int iy = 0; iy < 9; iy++)
            for (int ix = 0; ix < 9; ix++)
                vidx[ix, iy] = AddVtx(land.WorldOriginX + ix * CL, land.WorldOriginY + iy * CL, land.GetVertexZ(ix, iy));
        for (int cy = 0; cy < 8; cy++)
            for (int cx = 0; cx < 8; cx++)
            {
                int sw = vidx[cx, cy], se = vidx[cx + 1, cy], nw = vidx[cx, cy + 1], ne = vidx[cx + 1, cy + 1];
                if (TerrainSampler.SwToNeCut(lb, cx, cy)) { Tri(sw, se, ne); Tri(sw, ne, nw); }
                else { Tri(sw, se, nw); Tri(se, ne, nw); }
            }

        if (geo != null && DrawMeshes)
        {
            // The old input (--draw-geometry): the DRAWING polygons of static objects and scatter.
            void Append(List<RynthCore2.Raycast.GeometryLoader.TexTri> tris)
            {
                foreach (var t in tris)
                {
                    if (!Fin(t.A.X, t.A.Y, t.A.Z) || !Fin(t.B.X, t.B.Y, t.B.Z) || !Fin(t.C.X, t.C.Y, t.C.Z)) continue;
                    Tri(AddVtx(t.A.X, t.A.Y, t.A.Z), AddVtx(t.B.X, t.B.Y, t.B.Z), AddVtx(t.C.X, t.C.Y, t.C.Z));
                }
            }
            try
            {
                Append(geo.GetTexturedStaticObjects(lb));
                Append(geo.GetTexturedScatter(lb, (wx, wy) => { float z = land.GetTerrainZWorld(wx, wy); return float.IsNaN(z) ? land.GetNearestVertexHeightWorld(wx, wy) : z; }));
            }
            catch { }
        }
        else if (geo != null)
        {
            // What the game collides with (CollisionGeometry): physics polygons of the static
            // objects and building shells, the walls/floors of the buildings' cells and the
            // objects in them, and the scenery the server places, each with its real shape.
            var cg = Collision(geo);
            var tris = new List<CollisionGeometry.Tri>();
            CollisionGeometry.Climb = AgentMaxClimb;
            try { cg.AppendStatics(lb, tris, (x, y) => TerrainSampler.GetTerrainZ(land, x, y)); } catch { }
            try { cg.AppendBuildingCells(lb, tris); } catch { }
            try { cg.AppendScenery(lb, land, tris); } catch { }
            foreach (var t in tris)
            {
                if (!Fin(t.A.X, t.A.Y, t.A.Z) || !Fin(t.B.X, t.B.Y, t.B.Z) || !Fin(t.C.X, t.C.Y, t.C.Z)) continue;
                if (System.Numerics.Vector3.Cross(t.B - t.A, t.C - t.A).LengthSquared() < 1e-10f) continue;   // degenerate
                Tri(AddVtx(t.A.X, t.A.Y, t.A.Z), AddVtx(t.B.X, t.B.Y, t.B.Z), AddVtx(t.C.X, t.C.Y, t.C.Z));
            }
        }
        return true;
    }

    /// <summary>
    /// The navmesh's step height. 1 m keeps steep terrain (up to AC's walkable slope) connected at a
    /// 0.5 m cell; a character only steps 0.6 m, and CollisionGeometry.RaiseIfLow turns objects
    /// between the two into walls. --climb overrides it (tests only).
    /// </summary>
    public static float AgentMaxClimb = 1.0f;

    /// <summary>
    /// How far a wall outline may be simplified. 1.3 m was fine inside the old 2 m agent margin;
    /// with a body-sized radius (0.6 m: gaps under about 4 m no longer close, Shoushi 2026-10-08)
    /// a 1.3 m simplification could cut a corner through the margin, so it follows the radius.
    /// </summary>
    internal static float EdgeMaxError(float radius) => Math.Min(1.3f, radius + 0.15f);

    internal static RcNavMeshBuildSettings Settings(float radius, bool tiled, float cellSize)
    {
        float slopeDeg = (float)(Math.Acos(TerrainSampler.FloorZ) * 180.0 / Math.PI);
        return new RcNavMeshBuildSettings
        {
            cellSize = cellSize, cellHeight = 0.20f,
            agentHeight = 2.0f, agentRadius = radius, agentMaxClimb = AgentMaxClimb, agentMaxSlope = slopeDeg,
            minRegionSize = 8, mergedRegionSize = 20, partitioning = (int)RcPartition.WATERSHED,
            filterLowHangingObstacles = true, filterLedgeSpans = true, filterWalkableLowHeightSpans = true,
            edgeMaxLen = 12f, edgeMaxError = EdgeMaxError(radius), vertsPerPoly = VertsPerPoly, detailSampleDist = 6f, detailSampleMaxError = 1f,
            // The solo bake reads the intermediate results (the .obj export); the tiled bake only
            // needs the finished navmesh, and keeping every tile's heightfields cost 1.5-2.7 GB a chunk.
            tiled = tiled, tileSize = tiled ? TiledTileSize : 0, keepInterResults = !tiled, buildAll = true,
        };
    }

    private static bool Fin(float a, float b, float c) => float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c);

    // ── Solo bake (one self-contained tile) ─────────────────────────────────────
    public static int BakeLandblock(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader? geo, uint lb, string outDir, bool writeObj, float agentRadius)
    {
        var verts = new List<float>(); var faces = new List<int>();
        if (!AppendLandblock(sampler, geo, lb, verts, faces)) return -1;

        var geom = new RcSampleInputGeomProvider(verts.ToArray(), faces.ToArray());
        geom.CalculateNormals();
        var settings = Settings(agentRadius, tiled: false, cellSize: 0.45f);
        NavMeshBuildResult res = new SoloNavMeshBuilder().Build(geom, settings);
        if (!res.Success || res.NavMesh == null || res.RecastBuilderResults.Count == 0) return -1;
        RcBuilderResult rb = res.RecastBuilderResults[0];
        if (rb.Mesh.npolys == 0) return 0;

        DtMeshData md = new SoloNavMeshBuilder().BuildMeshData(geom, settings.cellSize, settings.cellHeight,
            settings.agentHeight, settings.agentRadius, settings.agentMaxClimb, rb);
        WriteTile(Path.Combine(outDir, $"nav_{lb:X4}.tile"), md);
        if (writeObj) ExportDetailObj(Path.Combine(outDir, $"nav_{lb:X4}.obj"), rb.MeshDetail, lb);
        return rb.Mesh.npolys;
    }

    // ── Tiled bake: a region as ONE connected navmesh, serialized per-tile ───────
    public static void BakeRegionTiled(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader? geo,
        int x0, int x1, int y0, int y1, string outDir, float agentRadius, out int tiles, out int empty)
    {
        BakeRegionTiled(sampler, geo, x0, x1, y0, y1, outDir, agentRadius, null, out tiles, out empty, out _);
    }

    /// <summary>
    /// Tiled bake of x0..x1, y0..y1. <paramref name="keep"/> (optional) picks which
    /// landblocks get a tile written (the others are baked as context only).
    /// <paramref name="gridOk"/> is false when the chunk's tile grid came out off the
    /// landblock grid (its tiles would not link to other chunks).
    /// </summary>
    public static void BakeRegionTiled(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader? geo,
        int x0, int x1, int y0, int y1, string outDir, float agentRadius, Func<uint, bool>? keep,
        out int tiles, out int empty, out bool gridOk, Func<int, int, bool>? gather = null)
    {
        tiles = 0; empty = 0; gridOk = true;
        var verts = new List<float>(); var faces = new List<int>();
        int gathered = 0;
        int gMinX = 256, gMaxX = -1, gMinY = 256, gMaxY = -1;   // landblocks that had terrain
        // Gather a 1-landblock BORDER beyond the output rectangle so this chunk's
        // edge tiles share geometry context with neighbouring chunks — that makes
        // separately-baked chunks reconnect at their seams when both are loaded.
        for (int x = x0 - 1; x <= x1 + 1; x++)
            for (int y = y0 - 1; y <= y1 + 1; y++)
            {
                if (x < 0 || x > 255 || y < 0 || y > 255) continue;
                // gather: optional filter (the world bake leaves out open water that touches
                // no landblock it keeps; Recast passes over empty tiles almost for free).
                if (gather != null && !gather(x, y)) continue;
                if (AppendLandblock(sampler, geo, (uint)((x << 8) | y), verts, faces))
                {
                    gathered++;
                    gMinX = Math.Min(gMinX, x); gMaxX = Math.Max(gMaxX, x);
                    gMinY = Math.Min(gMinY, y); gMaxY = Math.Max(gMaxY, y);
                }
            }
        if (gathered == 0) return;

        // Pin the tile grid to the landblock grid. Recast lays its tiles out from the
        // geometry's minimum corner, and a building or tree poking out past the border
        // ring moved that corner by up to ~25 m: every chunk got its own grid offset,
        // tiles no longer matched landblocks, and neighbouring chunks never linked at
        // their seams. Dropping triangles that leave the gathered box puts the minimum
        // corner back on the gathered terrain's corner, a multiple of 192. (The box is
        // the landblocks that actually had terrain, so a missing ring column at the
        // edge of the map can't leave an object vertex as the minimum.)
        float boxMinX = gMinX * 192f, boxMaxX = (gMaxX + 1) * 192f;
        float boxMinZ = gMinY * 192f, boxMaxZ = (gMaxY + 1) * 192f;
        bool Inside(int v) => verts[v * 3] >= boxMinX && verts[v * 3] <= boxMaxX && verts[v * 3 + 2] >= boxMinZ && verts[v * 3 + 2] <= boxMaxZ;
        // The bounds come from every vertex, used or not, so rebuild both lists.
        var keptVerts = new List<float>(verts.Count); var keptFaces = new List<int>(faces.Count);
        var remap = new Dictionary<int, int>();
        int Keep(int v)
        {
            if (!remap.TryGetValue(v, out int n)) { n = keptVerts.Count / 3; keptVerts.Add(verts[v * 3]); keptVerts.Add(verts[v * 3 + 1]); keptVerts.Add(verts[v * 3 + 2]); remap[v] = n; }
            return n;
        }
        // The ring is context for the chunk's edge tiles only, and Recast reads just
        // (agent radius + 3 cells) = 4.5 m past a tile, so only a ContextMargin-wide strip of
        // it is rasterized; the rest of the ring was baked into tiles that are thrown away.
        // Two unused vertices keep the bounds (and so the grid and the height origin)
        // exactly where the whole ring put them.
        const float ContextMargin = 24f;   // one terrain cell
        float cMinX = x0 * 192f - ContextMargin, cMaxX = (x1 + 1) * 192f + ContextMargin;
        float cMinZ = y0 * 192f - ContextMargin, cMaxZ = (y1 + 1) * 192f + ContextMargin;
        bool NearChunk(int a, int b, int c)
        {
            float minX = Math.Min(verts[a * 3], Math.Min(verts[b * 3], verts[c * 3])), maxX = Math.Max(verts[a * 3], Math.Max(verts[b * 3], verts[c * 3]));
            float minZ = Math.Min(verts[a * 3 + 2], Math.Min(verts[b * 3 + 2], verts[c * 3 + 2])), maxZ = Math.Max(verts[a * 3 + 2], Math.Max(verts[b * 3 + 2], verts[c * 3 + 2]));
            return maxX >= cMinX && minX <= cMaxX && maxZ >= cMinZ && minZ <= cMaxZ;
        }
        float yMin = float.MaxValue, yMax = float.MinValue;
        for (int f = 0; f < faces.Count; f += 3)
        {
            int a = faces[f], b = faces[f + 1], c = faces[f + 2];
            if (!(Inside(a) && Inside(b) && Inside(c))) continue;
            yMin = Math.Min(yMin, Math.Min(verts[a * 3 + 1], Math.Min(verts[b * 3 + 1], verts[c * 3 + 1])));
            yMax = Math.Max(yMax, Math.Max(verts[a * 3 + 1], Math.Max(verts[b * 3 + 1], verts[c * 3 + 1])));
            if (!NearChunk(a, b, c)) continue;
            keptFaces.Add(Keep(a)); keptFaces.Add(Keep(b)); keptFaces.Add(Keep(c));
        }
        if (keptFaces.Count == 0) return;
        keptVerts.Add(boxMinX); keptVerts.Add(yMin); keptVerts.Add(boxMinZ);
        keptVerts.Add(boxMaxX); keptVerts.Add(yMax); keptVerts.Add(boxMaxZ);
        verts = keptVerts; faces = keptFaces;

        var geom = new RcSampleInputGeomProvider(verts.ToArray(), faces.ToArray());
        geom.CalculateNormals();
        var settings = Settings(agentRadius, tiled: true, cellSize: TiledCellSize);
        NavMeshBuildResult res = new TileNavMeshBuilder().Build(geom, settings);
        if (!res.Success || res.NavMesh == null) return;

        DtNavMesh nav = res.NavMesh;
        RcVec3f bmin = geom.GetMeshBoundsMin();
        int baseLbX = (int)Math.Round(bmin.X / 192.0); // Recast x = world EW = lbX*192
        int baseLbZ = (int)Math.Round(bmin.Z / 192.0); // Recast z = world NS = lbY*192
        if (Math.Abs(bmin.X - baseLbX * 192f) > 0.01f || Math.Abs(bmin.Z - baseLbZ * 192f) > 0.01f)
        {
            gridOk = false;
            Console.WriteLine($"  WARNING: grid offset ({bmin.X - baseLbX * 192f:F2}, {bmin.Z - baseLbZ * 192f:F2}) — these tiles won't link to other chunks");
        }

        for (int i = 0; i < nav.GetMaxTiles(); i++)
        {
            DtMeshTile? tile = nav.GetTile(i);
            if (tile?.data?.header == null) continue;
            DtMeshData md = tile.data;
            if (md.header.polyCount == 0) { empty++; continue; }
            int absLbX = baseLbX + md.header.x;
            int absLbY = baseLbZ + md.header.y;
            if (absLbX < x0 || absLbX > x1 || absLbY < y0 || absLbY > y1) continue; // border tile = context only
            md.header.x = absLbX; // rewrite to ABSOLUTE landblock coords (world-positioned grid)
            md.header.y = absLbY;
            uint lb = (uint)((absLbX << 8) | absLbY);
            if (keep != null && !keep(lb)) continue;
            WriteTile(Path.Combine(outDir, $"nav_{lb:X4}.tile"), md);
            tiles++;
        }
    }

    // ── Validation: reload two adjacent tiles like the plugin would, path across ──
    public static string ValidateConnectivity(string outDir, uint lbA, uint lbB)
    {
        var nav = new DtNavMesh();
        var p = new DtNavMeshParams { orig = new RcVec3f(0, 0, 0), tileWidth = 192f, tileHeight = 192f, maxTiles = 64, maxPolys = 1 << 16 };
        nav.Init(ref p, VertsPerPoly);
        foreach (uint lb in new[] { lbA, lbB })
        {
            string path = Path.Combine(outDir, $"nav_{lb:X4}.tile");
            if (!File.Exists(path)) return $"missing tile 0x{lb:X4}";
            DtMeshData md;
            using (var fr = File.OpenRead(path)) using (var br = new BinaryReader(fr)) md = new DtMeshDataReader().Read(br, VertsPerPoly);
            nav.AddTile(md, 0, 0, out _);
        }
        var q = new DtNavMeshQuery(nav);
        var filter = new DtQueryDefaultFilter();
        var half = new RcVec3f(8, 400, 8);
        RcVec3f a = new((((lbA >> 8) & 0xFF) * 192f) + 96f, 0, ((lbA & 0xFF) * 192f) + 96f);
        RcVec3f b = new((((lbB >> 8) & 0xFF) * 192f) + 96f, 0, ((lbB & 0xFF) * 192f) + 96f);
        q.FindNearestPoly(a, half, filter, out long ra, out RcVec3f pa, out _);
        q.FindNearestPoly(b, half, filter, out long rb2, out RcVec3f pb, out _);
        if (ra == 0 || rb2 == 0) return $"poly not found (ra={ra} rb={rb2})";
        Span<long> path2 = new long[256];
        var st = q.FindPath(ra, rb2, pa, pb, filter, path2, out int pc, 256);
        bool crosses = false;
        for (int i = 0; i < pc; i++) { nav.GetTileAndPolyByRef(path2[i], out DtMeshTile t, out _); if (t?.data?.header != null && (uint)((t.data.header.x << 8) | t.data.header.y) == lbB) { crosses = true; break; } }
        return $"FindPath {st.Succeeded()} corridor={pc} reaches-0x{lbB:X4}={crosses}";
    }

    // Written to a .tmp and moved into place, so a bake stopped half way never
    // leaves a truncated tile behind (a resumed batch bake trusts tiles on disk).
    internal static void WriteTile(string path, DtMeshData md)
    {
        OutputGuard.Check(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var bw = new BinaryWriter(fs))
            new DtMeshDataWriter().Write(bw, md, RcByteOrder.LITTLE_ENDIAN, false);
        File.Move(tmp, path, overwrite: true);
    }

    private static void ExportDetailObj(string path, RcPolyMeshDetail d, uint lb)
    {
        OutputGuard.Check(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder();
        sb.AppendLine($"# RynthNav detail mesh, landblock 0x{lb:X4}, AC frame (x=EW, y=NS, z=up)");
        for (int i = 0; i < d.nverts; i++)
        {
            var (ew, ns, up) = RecToAc(new RcVec3f(d.verts[i * 3], d.verts[i * 3 + 1], d.verts[i * 3 + 2]));
            sb.AppendLine($"v {ew:F3} {ns:F3} {up:F3}");
        }
        for (int m = 0; m < d.nmeshes; m++)
        {
            int bverts = d.meshes[m * 4 + 0], btris = d.meshes[m * 4 + 2], ntris = d.meshes[m * 4 + 3];
            for (int t = 0; t < ntris; t++)
            {
                int a = bverts + d.tris[(btris + t) * 4 + 0] + 1;
                int b = bverts + d.tris[(btris + t) * 4 + 1] + 1;
                int c = bverts + d.tris[(btris + t) * 4 + 2] + 1;
                sb.AppendLine($"f {a} {b} {c}");
            }
        }
        File.WriteAllText(path, sb.ToString());
    }
}

using System.Diagnostics;
using System.IO.Compression;
using RynthCore.TerrainData;

namespace RynthNav.Baker;

// --map-world: the whole of Dereth, in the game's own look, as an in-game map file (worldmap.rmap version 2).
//
//  --map-world --ppc 2|4|8|16|32 --out <dir> [--no-objects] [--threads n] [--name file.rmap] [--chunks x0,x1,y0,y1]
//
//  Tiers (the engine uses the best file it finds in NavData): ppc 2 = worldmap.rmap (Standard), 8 = worldmap-hd.rmap,
//  16 = worldmap-ultra.rmap. ppc is pixels per 24-yard cell; the picture is 2048 * ppc pixels square.
//
//  How: the world is cut into chunks of landblocks (8 x 8, or more when a 256 px tile is wider than that). Each
//  chunk is drawn with a landblock of margin all round (so a roof or a tree that crosses the edge, and the
//  slope light, match across it): the ground cell by cell from the game's textures (TexturedTerrain), then the
//  static objects and scenery from above (ObjectRaster). The middle is cut into 256 px tiles, the coarser zoom
//  levels the chunk can make by itself are made by halving, and every tile is DXT1-compressed (Dxt1) and
//  deflated. A chunk's result goes to its own file under _work-<name>, so an interrupted bake resumes where it
//  stopped; the one-tile "apex" of each chunk is kept for the end, when the levels above the chunks are made
//  from them and everything is laid out in the final file.
//
//  File (little-endian, as version 1 in WorldMapRender.WriteRmap except each tile is the deflate of its DXT1
//  blocks, 256 x 256 px = 32 KB, instead of RGB): "RMAP", u32 version (2), u32 tile size, u32 levels, u32 pixels
//  per cell at level 0, u32 cells wide, u32 cells high; per level u32 width, height, columns, rows, first tile;
//  u32 tile count; per tile u64 offset + u32 length (0 length: no tile); then the tiles. Row 0 is the north edge.
internal static class WorldMapBake
{
    private const int Tile = 256, Margin = 1;               // tile px; margin in landblocks

    internal sealed record Options(int Ppc, bool Objects, int Threads, string Name, int ChunkX0 = 0, int ChunkX1 = 255, int ChunkY0 = 0, int ChunkY1 = 255);

    private sealed class Ctx
    {
        public required WorldGrid Grid; public required float[] Shade; public required TexturedTerrain Terrain;
        public required ObjectRaster.SurfaceStore Surfaces; public required RynthCore2.Raycast.GeometryLoader Geo;
        public required object GeoLock; public required int Ppc, ChunkLb, ChunkPx, TilesPerChunk, ChunkLevels, ChunksPerSide;
        public required bool Objects; public required string Work;
    }

    private static int Log2(int n) { int l = 0; while ((1 << l) < n) l++; return l; }

    public static string DefaultName(int ppc) => ppc switch { 2 => "worldmap.rmap", 8 => "worldmap-hd.rmap", 16 => "worldmap-ultra.rmap", _ => $"worldmap-p{ppc}.rmap" };

    public static int Run(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader geo, string outDir, Options o, Action<string> log)
    {
        int ppc = o.Ppc;
        if (ppc < 2 || ppc > 128 || (ppc & (ppc - 1)) != 0) { log("--ppc must be 2, 4, 8, 16, 32, 64 or 128 for the world bake (a power of two)"); return 1; }
        int tileCells = Tile / ppc;                         // cells across a tile (a tile can be smaller than a landblock: chunks are what align)
        if (ppc >= 64) log($"note: at {ppc} px per cell a chunk's drawing buffer is {(8 + 2 * Margin) * 8 * ppc} px square (about {(long)(8 + 2 * Margin) * 8 * ppc * (8 + 2 * Margin) * 8 * ppc * 7 / (1 << 20)} MB a thread); use --threads to fit your memory, and expect roughly {ppc * ppc * 0.86 / 1024:F1} GB of tiles (scaled from the 32 px bake)");
        int tileLb = Math.Max(1, tileCells / 8);
        int chunkLb = Math.Max(8, tileLb);
        int chunksPerSide = 256 / chunkLb;
        int chunkPx = chunkLb * 8 * ppc, tilesPerChunk = chunkPx / Tile;
        int worldPx = 2048 * ppc, worldTiles = worldPx / Tile;
        int levels = Log2(worldTiles) + 1, chunkLevels = Log2(tilesPerChunk) + 1;
        log($"world bake: {worldPx} x {worldPx} px ({ppc} px per cell), {worldTiles} x {worldTiles} tiles at level 0, {levels} levels; chunks of {chunkLb} x {chunkLb} landblocks ({chunkPx} px), {chunksPerSide * chunksPerSide} of them");

        OutputGuard.Check(outDir);
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(outDir, "_work-" + Path.GetFileNameWithoutExtension(o.Name));
        Directory.CreateDirectory(work);
        var sw = Stopwatch.StartNew();

        WorldGrid grid = WorldGrid.Load(sampler, 0, 255, 0, 255);
        log($"terrain read: {grid.Loaded} landblocks ({sw.Elapsed.TotalSeconds:F1}s)");
        float[] shade = grid.Shade(1.3f);
        TexturedTerrain? terrain = TexturedTerrain.Load(geo, log);
        if (terrain == null) return 1;
        var ctx = new Ctx
        {
            Grid = grid, Shade = shade, Terrain = terrain, Surfaces = new ObjectRaster.SurfaceStore(terrain.Decoder), Geo = geo, GeoLock = new object(),
            Ppc = ppc, ChunkLb = chunkLb, ChunkPx = chunkPx, TilesPerChunk = tilesPerChunk, ChunkLevels = chunkLevels, ChunksPerSide = chunksPerSide,
            Objects = o.Objects, Work = work,
        };

        // The chunks to bake: those inside the requested range (all, normally), by chunk column and row (row 0 = north).
        var todo = new List<(int Cc, int Cr)>();
        for (int cr = 0; cr < chunksPerSide; cr++)
            for (int cc = 0; cc < chunksPerSide; cc++)
                if (cc >= o.ChunkX0 && cc <= o.ChunkX1 && cr >= o.ChunkY0 && cr <= o.ChunkY1) todo.Add((cc, cr));
        int done = 0, skipped = 0;
        int total = todo.Count;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Threads) }, cell =>
        {
            string file = ChunkFile(work, cell.Cc, cell.Cr);
            try
            {
                if (File.Exists(file)) { Interlocked.Increment(ref skipped); }
                else BakeChunk(ctx, cell.Cc, cell.Cr, file);
            }
            catch (Exception ex) { failures.Add($"chunk {cell.Cc},{cell.Cr}: {ex.Message}"); }
            int n = Interlocked.Increment(ref done);
            if (n % 8 == 0 || n == total)
            {
                double el = sw.Elapsed.TotalSeconds, perChunk = el / Math.Max(1, n - skipped);
                log($"  {n}/{total} chunks ({skipped} already done), {el / 60:F1} min, about {(total - n) * perChunk / 60:F0} min left");
            }
        });
        foreach (string f in failures) log("FAILED " + f);
        if (!failures.IsEmpty) { log("some chunks failed: fix and rerun (finished chunks are kept), no file written"); return 1; }
        log($"surfaces decoded: {ctx.Surfaces.Count}");

        string final = Path.Combine(outDir, o.Name);
        Assemble(ctx, ppc, worldTiles, levels, final, log);
        log($"wrote {final} ({new FileInfo(final).Length / (1024 * 1024)} MB) in {sw.Elapsed.TotalMinutes:F1} min");
        return 0;
    }

    private static string ChunkFile(string work, int cc, int cr) => Path.Combine(work, $"chunk_{cc:D2}_{cr:D2}.bin");

    // ── One chunk ────────────────────────────────────────────────────────
    private static void BakeChunk(Ctx c, int cc, int cr, string file)
    {
        int ppc = c.Ppc;
        int lx0 = cc * c.ChunkLb, ly0 = (c.ChunksPerSide - 1 - cr) * c.ChunkLb;     // south-west landblock of the chunk (row 0 = north)
        int bufCx0 = (lx0 - Margin) * 8, bufCy0 = (ly0 - Margin) * 8;                         // buffer origin, in cells
        int bufCells = (c.ChunkLb + 2 * Margin) * 8, W = bufCells * ppc, H = W;
        var rgb = new byte[(long)W * H * 3];

        // Ground, cell by cell (cells outside the world stay black; the crop never reaches them).
        for (int cj = 0; cj < bufCells; cj++)
        {
            int gy = bufCy0 + (bufCells - 1 - cj);                                            // north first
            if (gy < 0 || gy >= 2048) continue;
            for (int ci = 0; ci < bufCells; ci++)
            {
                int gx = bufCx0 + ci;
                if (gx < 0 || gx >= 2048) continue;
                c.Terrain.PaintCell(gx, gy, c.Grid.Type, c.Grid.Road, c.Shade, WorldGrid.V, ppc, rgb, W, ci * ppc, cj * ppc);
            }
        }

        // Objects and scenery from above, from this chunk's landblocks and the margin round it.
        if (c.Objects)
        {
            var raster = new ObjectRaster(rgb, W, H, bufCx0, bufCy0, ppc, c.Surfaces);
            for (int lx = lx0 - Margin; lx < lx0 + c.ChunkLb + Margin; lx++)
                for (int ly = ly0 - Margin; ly < ly0 + c.ChunkLb + Margin; ly++)
                {
                    if (lx < 0 || ly < 0 || lx > 255 || ly > 255) continue;
                    uint key = (uint)((lx << 8) | ly);
                    List<RynthCore2.Raycast.GeometryLoader.TexTri> statics, scatter;
                    lock (c.GeoLock)
                    {
                        statics = c.Geo.GetTexturedStaticObjects(key);
                        scatter = c.Geo.GetTexturedScatter(key, (wx, wy) => c.Geo.GetTerrainZWorld(wx, wy));
                    }
                    raster.Add(statics);
                    raster.Add(scatter);
                }
        }

        // The middle of the buffer is the chunk.
        int m = Margin * 8 * ppc, cp = c.ChunkPx;
        var level = new byte[(long)cp * cp * 3];
        for (int y = 0; y < cp; y++) Buffer.BlockCopy(rgb, ((y + m) * W + m) * 3, level, y * cp * 3, cp * 3);

        // The chunk's own pyramid: level 0 and as many halvings as reach a single tile (the apex).
        var tiles = new List<(int Level, int Col, int Row, byte[] Blob)>();
        int size = cp;
        byte[]? apex = null;
        for (int k = 0; k < c.ChunkLevels; k++)
        {
            int n = size / Tile;
            for (int tr = 0; tr < n; tr++)
                for (int tc = 0; tc < n; tc++)
                    tiles.Add((k, cc * n + tc, cr * n + tr, EncodeTile(level, size, tc * Tile, tr * Tile)));
            if (k == c.ChunkLevels - 1) apex = level;
            else { level = Downsample(level, size); size /= 2; }
        }

        string tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(new byte[] { (byte)'R', (byte)'C', (byte)'H', (byte)'K' });
            w.Write((uint)tiles.Count);
            foreach (var t in tiles) { w.Write((byte)t.Level); w.Write((ushort)t.Col); w.Write((ushort)t.Row); w.Write((uint)t.Blob.Length); w.Write(t.Blob); }
            w.Write(apex!);                                                                     // 256 x 256 RGB, for the levels above
        }
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>The 256 x 256 tile at (x0, y0) of a square RGB image, as the deflate of its DXT1 blocks.</summary>
    private static byte[] EncodeTile(byte[] img, int size, int x0, int y0)
    {
        var tile = new byte[Tile * Tile * 3];
        for (int y = 0; y < Tile; y++) Buffer.BlockCopy(img, ((y0 + y) * size + x0) * 3, tile, y * Tile * 3, Tile * 3);
        return Deflate(Dxt1.Encode(tile, Tile, Tile));
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>Half the size of a square RGB image, each pixel the mean of four.</summary>
    private static byte[] Downsample(byte[] src, int size)
    {
        int h = size / 2;
        var d = new byte[(long)h * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < h; x++)
            {
                long a = ((long)(2 * y) * size + 2 * x) * 3, b = a + 3, cc = a + (long)size * 3, e = cc + 3;
                long o = ((long)y * h + x) * 3;
                d[o] = (byte)((src[a] + src[b] + src[cc] + src[e] + 2) >> 2);
                d[o + 1] = (byte)((src[a + 1] + src[b + 1] + src[cc + 1] + src[e + 1] + 2) >> 2);
                d[o + 2] = (byte)((src[a + 2] + src[b + 2] + src[cc + 2] + src[e + 2] + 2) >> 2);
            }
        return d;
    }

    // ── Putting it together ──────────────────────────────────────────────
    private static void Assemble(Ctx c, int ppc, int worldTiles, int levels, string final, Action<string> log)
    {
        var first = new int[levels];
        var cols = new int[levels];
        int count = 0;
        for (int k = 0; k < levels; k++) { first[k] = count; cols[k] = worldTiles >> k; count += cols[k] * cols[k]; }
        var offsets = new ulong[count];
        var lengths = new uint[count];

        string dataFile = final + ".data.tmp";
        long pos = 0;
        var apexes = new byte[]?[c.ChunksPerSide * c.ChunksPerSide];
        using (var data = new FileStream(dataFile, FileMode.Create, FileAccess.Write))
        {
            void Put(int level, int col, int row, byte[] blob)
            {
                int idx = first[level] + row * cols[level] + col;
                offsets[idx] = (ulong)pos; lengths[idx] = (uint)blob.Length;
                data.Write(blob);
                pos += blob.Length;
            }

            // The chunks' own tiles, and their apexes.
            foreach (string f in Directory.EnumerateFiles(c.Work, "chunk_*.bin").OrderBy(s => s, StringComparer.Ordinal))
            {
                string[] parts = Path.GetFileNameWithoutExtension(f).Split('_');
                int cc = int.Parse(parts[1]), cr = int.Parse(parts[2]);
                using var fs = File.OpenRead(f);
                using var r = new BinaryReader(fs);
                if (r.ReadUInt32() != 0x4B484352u) throw new InvalidDataException(f + " is not a chunk file");   // "RCHK"
                uint n = r.ReadUInt32();
                for (uint i = 0; i < n; i++)
                {
                    int level = r.ReadByte(), col = r.ReadUInt16(), row = r.ReadUInt16();
                    byte[] blob = r.ReadBytes((int)r.ReadUInt32());
                    Put(level, col, row, blob);
                }
                apexes[cr * c.ChunksPerSide + cc] = r.ReadBytes(Tile * Tile * 3);
            }

            // The levels above the chunks, each tile the halved mean of four below it.
            int side = c.ChunksPerSide;
            var cur = apexes;
            for (int k = c.ChunkLevels; k < levels; k++)
            {
                int ns = side / 2;
                var next = new byte[ns * ns][];
                for (int tr = 0; tr < ns; tr++)
                    for (int tc = 0; tc < ns; tc++)
                    {
                        var comp = new byte[Tile * 2 * Tile * 2 * 3];
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                byte[]? child = cur[(2 * tr + dy) * side + 2 * tc + dx];
                                if (child == null) continue;
                                for (int y = 0; y < Tile; y++)
                                    Buffer.BlockCopy(child, y * Tile * 3, comp, ((dy * Tile + y) * Tile * 2 + dx * Tile) * 3, Tile * 3);
                            }
                        byte[] half = Downsample(comp, Tile * 2);
                        next[tr * ns + tc] = half;
                        Put(k, tc, tr, Deflate(Dxt1.Encode(half, Tile, Tile)));
                    }
                cur = next; side = ns;
            }
        }

        long headerSize = 4 + 6 * 4 + levels * 20 + 4 + (long)count * 12;
        string tmp = final + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(new byte[] { (byte)'R', (byte)'M', (byte)'A', (byte)'P' });
            w.Write(2u); w.Write((uint)Tile); w.Write((uint)levels); w.Write((uint)ppc);
            w.Write(2048u); w.Write(2048u);
            for (int k = 0; k < levels; k++)
            { w.Write((uint)(worldTiles * Tile >> k)); w.Write((uint)(worldTiles * Tile >> k)); w.Write((uint)cols[k]); w.Write((uint)cols[k]); w.Write((uint)first[k]); }
            w.Write((uint)count);
            for (int i = 0; i < count; i++) { w.Write(lengths[i] == 0 ? 0UL : offsets[i] + (ulong)headerSize); w.Write(lengths[i]); }
            using var data = File.OpenRead(dataFile);
            data.CopyTo(fs);
        }
        File.Move(tmp, final, overwrite: true);
        File.Delete(dataFile);
        int present = lengths.Count(l => l > 0);
        log($"tiles: {present} of {count}");
    }

    // ── Look inside a map file: --rmap-png <file> --level k [--tiles c0,c1,r0,r1] --out <dir> ──────────────
    public static int ExtractPng(string file, int level, string? tiles, string outDir, Action<string> log)
    {
        using var fs = File.OpenRead(file);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != 0x5041_4D52u) { log("not a map file"); return 1; }
        uint version = r.ReadUInt32(); uint tile = r.ReadUInt32(), lv = r.ReadUInt32(), ppc = r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
        var info = new (uint W, uint H, uint Cols, uint Rows, uint First)[lv];
        for (int i = 0; i < lv; i++) info[i] = (r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
        int count = (int)r.ReadUInt32();
        var off = new ulong[count]; var len = new uint[count];
        for (int i = 0; i < count; i++) { off[i] = r.ReadUInt64(); len[i] = r.ReadUInt32(); }
        if (level < 0 || level >= lv) { log($"the file has levels 0..{lv - 1}"); return 1; }
        var L = info[level];
        int c0 = 0, c1 = (int)L.Cols - 1, r0 = 0, r1 = (int)L.Rows - 1;
        if (tiles != null)
        {
            var p = tiles.Split(',', StringSplitOptions.TrimEntries);
            if (p.Length != 4) { log("--tiles needs c0,c1,r0,r1"); return 1; }
            c0 = int.Parse(p[0]); c1 = int.Parse(p[1]); r0 = int.Parse(p[2]); r1 = int.Parse(p[3]);
        }
        int w = (c1 - c0 + 1) * Tile, h = (r1 - r0 + 1) * Tile;
        var img = new byte[(long)w * h * 3];
        int shown = 0;
        for (int row = r0; row <= r1; row++)
            for (int col = c0; col <= c1; col++)
            {
                int idx = (int)L.First + row * (int)L.Cols + col;
                if (len[idx] == 0) continue;
                fs.Seek((long)off[idx], SeekOrigin.Begin);
                var comp = r.ReadBytes((int)len[idx]);
                byte[] raw;
                using (var z = new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress)) { var ms = new MemoryStream(); z.CopyTo(ms); raw = ms.ToArray(); }
                byte[] rgb;
                if (version == 2) rgb = Dxt1.Decode(raw, Tile, Tile);
                else rgb = raw;
                for (int y = 0; y < Tile; y++) Buffer.BlockCopy(rgb, y * Tile * 3, img, (((row - r0) * Tile + y) * w + (col - c0) * Tile) * 3, Tile * 3);
                shown++;
            }
        Directory.CreateDirectory(outDir);
        string png = Path.Combine(outDir, $"rmap_L{level}_{c0}-{c1}_{r0}-{r1}.png");
        WorldMapRender.WritePngPublic(png, img, w, h);
        log($"{shown} tiles, {w} x {h} px (level {level}, {L.W} px wide, {ppc} px per cell at level 0, version {version}) -> {png}");
        return 0;
    }
}

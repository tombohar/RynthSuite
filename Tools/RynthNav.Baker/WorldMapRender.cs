using System.Diagnostics;
using System.IO.Compression;
using RynthCore.TerrainData;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthNav.Baker;

// --map: renders Dereth top-down from client_cell_1.dat, the source image for RynthNav's world map.
//  - Every landblock's 9x9 vertices go into one global grid (the east/north edge vertex of a landblock
//    is the west/south edge vertex of its neighbour), so each pixel blends four real vertices and
//    there are no seams at landblock borders.
//  - Land is a bilinear blend of the vertices' terrain-type colours with a hillshade from the vertex
//    heights; water is decided per pixel from the water share of the four vertices, so shorelines are
//    crisp, not smeared. Roads (terrain-word bits 0-1) are tinted on top.
//  - --ppc pixels per 24-unit cell (default 2: 4096x4096 for all of Dereth); --area x0,x1,y0,y1 (hex
//    landblocks) renders a crop. North is at the top.
internal static class WorldMapRender
{
    private const int Blocks = 256, Side = 8;                 // landblocks per axis, cells per landblock
    private const int V = Blocks * Side + 1;                  // vertices per axis
    private const float CellUnits = 24f;

    // Terrain type indices (TerrainTextureType): 16..20 are water.
    private static readonly (byte R, byte G, byte B)[] Land =
    {
        (140, 125, 110), // 0  BarrenRock
        (110, 150,  70), // 1  Grassland
        (225, 240, 250), // 2  Ice
        ( 60, 130,  55), // 3  LushGrass
        ( 85, 110,  70), // 4  MarshSparseSwamp
        (110,  80,  55), // 5  MudRichDirt
        ( 60,  55,  65), // 6  ObsidianPlain
        (150, 125,  85), // 7  PackedDirt
        (135, 115,  75), // 8  PatchyDirt
        (125, 150,  80), // 9  PatchyGrassland
        (215, 190, 120), // 10 SandYellow
        (170, 165, 150), // 11 SandGrey
        (185, 165, 125), // 12 SandRockStrewn
        (160, 120,  90), // 13 SedimentaryRock
        (130, 120, 100), // 14 SemiBarrenRock
        (245, 248, 252), // 15 Snow
        (  0,   0,   0), // 16..20 water: WaterColour
        (  0,   0,   0), (  0,   0,   0), (  0,   0,   0), (  0,   0,   0),
        ( 44, 104,  50), // 21 forestfloor
        (  0,   0,   0), // 22 FauxWaterRunning: counted as water by name
        ( 92, 120,  72), // 23 SeaSlime
        (172, 112,  82), // 24 Argila
        ( 84,  52,  46), // 25 Volcano1
        (112,  58,  42), // 26 Volcano2
        (190, 215, 235), // 27 BlueIce
        ( 92, 128,  72), // 28 Moss
        ( 62,  94,  56), // 29 DarkMoss
        (122,  96,  70), // 30 olthoi (hive resin)
        (132, 116,  96), // 31 DesolateLands
    };
    private static readonly (byte R, byte G, byte B) RoadColour = (176, 152, 108);
    private static (byte R, byte G, byte B) WaterColour(int t) => t switch
    {
        16 or 17 => (82, 142, 186),   // running / standing fresh
        18 or 19 => (60, 120, 168),   // shallow sea
        _ => (28, 66, 118),           // deep sea (and open sea with no landblock)
    };

    public static int Run(TerrainSampler sampler, string outDir, int ppc, string? area, string acFolder, bool tiles = false, WorldMapVectors.Data? overlay = null, RynthCore2.Raycast.GeometryLoader? textureGeo = null, bool objects = false)
    {
        var pal = TerrainPalette.Load(acFolder);
        var landRgb = new (byte R, byte G, byte B)[32];
        var water = new bool[32];
        for (int t = 0; t < 32; t++) { landRgb[t] = Land[t]; water[t] = t >= 16 && t <= 20; }
        if (pal == null) Console.WriteLine("terrain palette: not found in the Region file; using the built-in colours");
        else
        {
            Console.WriteLine($"terrain palette: {pal.Names.Length} types from the Region file");
            for (int t = 0; t < Math.Min(32, pal.Names.Length); t++)
            {
                Console.WriteLine($"  {t,2} {pal.Names[t],-28} 0x{pal.Argb[t]:X8}");
                water[t] = pal.Names[t].Contains("Water", StringComparison.OrdinalIgnoreCase);
            }
        }
        int x0 = 0, x1 = Blocks - 1, y0 = 0, y1 = Blocks - 1;
        if (area != null)
        {
            var p = area.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 4) { Console.Error.WriteLine("--area needs minX,maxX,minY,maxY (hex)"); return 1; }
            x0 = Convert.ToInt32(p[0], 16); x1 = Convert.ToInt32(p[1], 16);
            y0 = Convert.ToInt32(p[2], 16); y1 = Convert.ToInt32(p[3], 16);
            if (x0 > x1 || y0 > y1 || x0 < 0 || y0 < 0 || x1 >= Blocks || y1 >= Blocks) { Console.Error.WriteLine("--area out of range 00..FF"); return 1; }
        }
        ppc = Math.Clamp(ppc, 1, 24);

        var sw = Stopwatch.StartNew();
        // One landblock of margin so edge pixels in the crop still blend with real neighbours.
        WorldGrid grid = WorldGrid.Load(sampler, x0 - 1, x1 + 1, y0 - 1, y1 + 1, m => Console.WriteLine($"{m} ({sw.Elapsed.TotalSeconds:F0}s)"));
        byte[] type = grid.Type, road = grid.Road;
        long[] seen = grid.TypeCounts();
        Console.WriteLine($"read {grid.Loaded} landblocks in {sw.Elapsed.TotalSeconds:F1}s; terrain types seen:");
        for (int t = 0; t < 32; t++) if (seen[t] > 0) Console.WriteLine($"  type {t,2}: {seen[t]} vertices");
        float[] shade = grid.Shade(textureGeo != null ? 1.3f : 2.2f);   // exaggeration: Dereth's relief is gentle (less under real textures)

        int cx0 = x0 * Side, cy0 = y0 * Side;                  // crop origin in cells
        int wCells = (x1 - x0 + 1) * Side, hCells = (y1 - y0 + 1) * Side;
        int W = wCells * ppc, H = hCells * ppc;
        var rgb = new byte[(long)W * H * 3];
        Console.WriteLine($"rendering {W}x{H} ({ppc} px per cell)...");

        TexturedTerrain? tex = textureGeo != null ? TexturedTerrain.Load(textureGeo, Console.WriteLine) : null;
        if (tex != null)
        {
            // The game's own look: each cell blended from its textures (TexturedTerrain), lit by the slope.
            int cellsW = (x1 - x0 + 1) * Side, cellsH = (y1 - y0 + 1) * Side;
            Parallel.For(0, cellsH, cj =>
            {
                int gy = cy0 + (cellsH - 1 - cj);                     // north first
                for (int ci = 0; ci < cellsW; ci++) tex.PaintCell(cx0 + ci, gy, type, road, shade, V, ppc, rgb, W, ci * ppc, cj * ppc);
            });
            Console.WriteLine($"terrain painted ({sw.Elapsed.TotalSeconds:F1}s)");
            if (objects)
            {
                // Buildings, walls and scenery from above, over the ground.
                var raster = new ObjectRaster(rgb, W, H, cx0, cy0, ppc, new ObjectRaster.SurfaceStore(tex.Decoder));
                float GroundZ(float wx, float wy) => textureGeo!.GetTerrainZWorld(wx, wy);
                for (int lx = x0; lx <= x1; lx++)
                    for (int ly = y0; ly <= y1; ly++)
                    {
                        uint key = (uint)((lx << 8) | ly);
                        raster.Add(textureGeo!.GetTexturedStaticObjects(key));
                        raster.Add(textureGeo.GetTexturedScatter(key, GroundZ));
                    }
                Console.WriteLine($"objects drawn: {raster.Triangles} triangles ({sw.Elapsed.TotalSeconds:F1}s)");
            }
        }
        else
        {
        Parallel.For(0, H, py =>
            {
                float fy = cy0 + (H - 1 - py + 0.5f) / ppc;        // north at the top
                int gy0 = Math.Min((int)fy, V - 2);
                float ty = fy - gy0;
                for (int px = 0; px < W; px++)
                {
                    float fx = cx0 + (px + 0.5f) / ppc;
                    int gx0 = Math.Min((int)fx, V - 2);
                    float tx = fx - gx0;
    
                    float wWater = 0, rW = 0, gW = 0, bW = 0;     // water share + weighted water colour
                    float wLand = 0, rL = 0, gL = 0, bL = 0, shd = 0, rd = 0;
                    for (int c = 0; c < 4; c++)
                    {
                        int ox = c & 1, oy = c >> 1;
                        float wt = (ox == 0 ? 1 - tx : tx) * (oy == 0 ? 1 - ty : ty);
                        int gi = (gy0 + oy) * V + gx0 + ox;
                        int t = type[gi];
                        if (t == 255 || water[t])
                        {
                            var wc = WaterColour(t == 255 ? 20 : t);
                            wWater += wt; rW += wc.R * wt; gW += wc.G * wt; bW += wc.B * wt;
                        }
                        else
                        {
                            var lc = landRgb[t];
                            wLand += wt; rL += lc.R * wt; gL += lc.G * wt; bL += lc.B * wt;
                            shd += shade[gi] * wt;
                            rd += (road[gi] != 0 ? 1f : 0f) * wt;
                        }
                    }
    
                    float r, g, b;
                    if (wWater >= 0.5f)
                    {
                        r = rW / wWater; g = gW / wWater; b = bW / wWater;
                    }
                    else
                    {
                        float inv = 1f / wLand;
                        r = rL * inv; g = gL * inv; b = bL * inv;
                        float s = shd * inv;
                        float road01 = rd * inv;
                        if (road01 > 0.5f && ppc >= 2)
                        {
                            float a = Math.Clamp((road01 - 0.5f) * 2f, 0f, 0.85f);
                            r += (RoadColour.R - r) * a; g += (RoadColour.G - g) * a; b += (RoadColour.B - b) * a;
                        }
                        r *= s; g *= s; b *= s;
                        if (wWater > 0f && wWater > 0.35f)         // a darker rim just inside the shoreline
                        {
                            float a = (wWater - 0.35f) / 0.15f * 0.25f;
                            r *= 1 - a; g *= 1 - a; b *= 1 - a;
                        }
                    }
                    long o = ((long)py * W + px) * 3;
                    rgb[o] = (byte)Math.Clamp(r, 0, 255);
                    rgb[o + 1] = (byte)Math.Clamp(g, 0, 255);
                    rgb[o + 2] = (byte)Math.Clamp(b, 0, 255);
                }
            });
        }
        if (overlay != null && area != null) WorldMapVectors.DrawOverlay(rgb, W, H, cx0, cy0, ppc, overlay);   // a preview of the vector layers
        OutputGuard.Check(outDir);
        Directory.CreateDirectory(outDir);
        string file = Path.Combine(outDir, area == null ? "world_map.png" : $"world_map_{x0:X2}-{x1:X2}_{y0:X2}-{y1:X2}.png");
        WritePng(file, rgb, W, H);
        Console.WriteLine($"wrote {file} ({new FileInfo(file).Length / 1024} KB) in {sw.Elapsed.TotalSeconds:F1}s");
        if (tiles)
        {
            if (area != null || W != H || W % TileSize != 0 || (W / TileSize & (W / TileSize - 1)) != 0)
            { Console.Error.WriteLine("--map-tiles needs the whole world at a --ppc that makes a square power-of-two number of 256 px tiles (ppc 2 = 4096 px)"); return 1; }
            string rmap = Path.Combine(outDir, "worldmap.rmap");
            WriteRmap(rmap, rgb, W, H, ppc);
            Console.WriteLine($"wrote {rmap} ({new FileInfo(rmap).Length / 1024} KB) in {sw.Elapsed.TotalSeconds:F1}s");
        }
        return 0;
    }

    // ── worldmap.rmap: the whole map as a pyramid of 256 px tiles, one file ───────────────────
    //  All little-endian. Header: "RMAP", u32 version (1), u32 tile size, u32 level count, u32 pixels per
    //  cell at level 0, u32 cells wide, u32 cells high; then per level u32 width, height, columns, rows,
    //  first tile number; then u32 tile count and per tile u64 file offset + u32 length; then the tiles,
    //  each the zlib stream of its 256x256 RGB pixels (row 0 = north). Level 0 is the full-resolution
    //  image, each level above it half the size, down to one tile. Tiles are numbered level by level,
    //  row by row. The engine's WorldMapTiles reads this.
    public const int TileSize = 256;

    private static void WriteRmap(string file, byte[] rgb, int W, int H, int ppc0)
    {
        var levels = new List<(byte[] Px, int W, int H)> { (rgb, W, H) };
        while (levels[^1].W > TileSize) levels.Add(Half(levels[^1]));

        var jobs = new List<(int Level, int Col, int Row)>();
        var first = new int[levels.Count];
        for (int l = 0; l < levels.Count; l++)
        {
            first[l] = jobs.Count;
            for (int r = 0; r < levels[l].H / TileSize; r++)
                for (int c = 0; c < levels[l].W / TileSize; c++) jobs.Add((l, c, r));
        }
        var blobs = new byte[jobs.Count][];
        Parallel.For(0, jobs.Count, i =>
        {
            var (l, c, r) = jobs[i];
            var (px, w, _) = levels[l];
            var raw = new byte[TileSize * TileSize * 3];
            for (int y = 0; y < TileSize; y++)
                Buffer.BlockCopy(px, ((r * TileSize + y) * w + c * TileSize) * 3, raw, y * TileSize * 3, TileSize * 3);
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            blobs[i] = ms.ToArray();
        });

        long headerSize = 4 + 6 * 4 + levels.Count * 5 * 4 + 4 + (long)jobs.Count * 12;
        string tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(new byte[] { (byte)'R', (byte)'M', (byte)'A', (byte)'P' });
            bw.Write(1u); bw.Write((uint)TileSize); bw.Write((uint)levels.Count); bw.Write((uint)ppc0);
            bw.Write((uint)(W / ppc0)); bw.Write((uint)(H / ppc0));
            for (int l = 0; l < levels.Count; l++)
            {
                bw.Write((uint)levels[l].W); bw.Write((uint)levels[l].H);
                bw.Write((uint)(levels[l].W / TileSize)); bw.Write((uint)(levels[l].H / TileSize));
                bw.Write((uint)first[l]);
            }
            bw.Write((uint)jobs.Count);
            long off = headerSize;
            foreach (byte[] b in blobs) { bw.Write((ulong)off); bw.Write((uint)b.Length); off += b.Length; }
            foreach (byte[] b in blobs) bw.Write(b);
        }
        File.Move(tmp, file, overwrite: true);
        Console.WriteLine($"  {levels.Count} levels, {jobs.Count} tiles");
    }

    private static (byte[] Px, int W, int H) Half((byte[] Px, int W, int H) s)
    {
        int w = s.W / 2, h = s.H / 2;
        var d = new byte[w * h * 3];
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 3; k++)
                {
                    int a = ((2 * y) * s.W + 2 * x) * 3 + k, b = a + 3, c = a + s.W * 3, e = c + 3;
                    d[(y * w + x) * 3 + k] = (byte)((s.Px[a] + s.Px[b] + s.Px[c] + s.Px[e] + 2) >> 2);
                }
        });
        return (d, w, h);
    }

    // ── Minimal PNG writer (8-bit RGB, no filter): no imaging dependency in the baker ──────────

    internal static void WritePngPublic(string path, byte[] rgb, int w, int h) => WritePng(path, rgb, w, h);

    private static void WritePng(string path, byte[] rgb, int w, int h)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var ihdr = new byte[13];
            BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
            ihdr[8] = 8; ihdr[9] = 2;                           // 8 bits, RGB
            Chunk(fs, "IHDR", ihdr);

            using var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                var zero = new byte[] { 0 };
                for (int y = 0; y < h; y++)
                {
                    z.Write(zero);                              // filter type 0
                    z.Write(rgb, (int)((long)y * w * 3), w * 3);
                }
            }
            Chunk(fs, "IDAT", raw.ToArray());
            Chunk(fs, "IEND", Array.Empty<byte>());
        }
        File.Move(tmp, path, overwrite: true);
    }

    private static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, Crc32(td)); s.Write(crc);
    }

    private static uint[]? _crcTable;
    private static uint Crc32(byte[] d)
    {
        if (_crcTable == null)
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
            _crcTable = t;
        }
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in d) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}

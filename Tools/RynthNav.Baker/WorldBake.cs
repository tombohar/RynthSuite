using System.Diagnostics;
using System.Text;
using RynthCore.TerrainData;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthNav.Baker;

// --world: bakes every landblock of Dereth that has land, in grid-aligned chunks.
//  - Which landblocks: a landblock with terrain in client_cell_1.dat counts as land unless
//    all 81 of its terrain vertices are water AND it has no static objects (open sea or a
//    lake's middle: nobody can stand there). Those are still gathered as context for their
//    neighbours, they just get no tile of their own. --survey writes the map it uses.
//  - Resumable: each finished chunk is appended to _bake\done-*.txt in the output folder,
//    and a rerun skips those chunks. Tiles are written atomically (NavBake.WriteTile).
//  - Progress, timings and an ETA go to the console and to a log file.
internal static class WorldBake
{
    public const char None = '.', Water = '~', Land = '#';

    // AC terrain types 16..20: WaterRunning, WaterStandingFresh, WaterShallowSea,
    // WaterShallowStillSea, WaterDeepSea.
    private static bool IsWaterType(int t) => t >= 16 && t <= 20;

    /// <summary>The landblock class map, [x, y]. Cached in _bake\landblocks.txt (north at the top).</summary>
    public static char[,] Survey(TerrainSampler sampler, string outDir)
    {
        string dir = Path.Combine(outDir, "_bake");
        string file = Path.Combine(dir, "landblocks.txt");
        var map = new char[256, 256];
        if (File.Exists(file))
        {
            string[] lines = File.ReadAllLines(file).Where(l => l.Length == 256).ToArray();
            if (lines.Length == 256)
            {
                for (int row = 0; row < 256; row++)
                    for (int x = 0; x < 256; x++) map[x, 255 - row] = lines[row][x];
                return map;
            }
        }
        var sw = Stopwatch.StartNew();
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
            {
                LandblockData? land = null;
                try { land = sampler.LoadLandblock((uint)((x << 8) | y)); } catch { }
                if (land == null) { map[x, y] = None; continue; }
                bool allWater = true;
                for (int ix = 0; ix < 9 && allWater; ix++)
                    for (int iy = 0; iy < 9 && allWater; iy++)
                        if (!IsWaterType(TerrainSampler.GetTerrainType(land, ix, iy))) allWater = false;
                map[x, y] = allWater && !land.HasObjects ? Water : Land;
            }
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder(257 * 256);
        for (int row = 0; row < 256; row++)
        {
            for (int x = 0; x < 256; x++) sb.Append(map[x, 255 - row]);
            sb.Append('\n');
        }
        File.WriteAllText(file, sb.ToString());
        Console.WriteLine($"survey: {sw.Elapsed.TotalSeconds:F1} s -> {file}");
        return map;
    }

    public static string Describe(char[,] map)
    {
        int land = 0, water = 0, none = 0;
        foreach (char c in map) { if (c == Land) land++; else if (c == Water) water++; else none++; }
        return $"landblocks: {land} land (baked), {water} open water (skipped), {none} without terrain";
    }

    private static (int x0, int x1, int y0, int y1) ParseArea(string? area)
    {
        if (area == null) return (0, 255, 0, 255);
        var p = area.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 4) throw new ArgumentException("--area needs minX,maxX,minY,maxY (hex)");
        return (Convert.ToInt32(p[0], 16), Convert.ToInt32(p[1], 16), Convert.ToInt32(p[2], 16), Convert.ToInt32(p[3], 16));
    }

    // Town positions in landblock units, from RynthNav's Atlas (locations.json).
    private static List<(double x, double y)> Towns(string file)
    {
        var list = new List<(double, double)>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
        foreach (var l in doc.RootElement.GetProperty("locations").EnumerateArray())
        {
            if (l.TryGetProperty("type", out var ty) && ty.GetString() == "Town" && l.TryGetProperty("ns", out var ns) && l.TryGetProperty("ew", out var ew))
                list.Add(((ew.GetDouble() * 10.0 + 1019.5) / 8.0, (ns.GetDouble() * 10.0 + 1019.5) / 8.0));
        }
        return list;
    }

    private static long FreeBytes(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return -1; }
    }

    public static int Run(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader? geo, string outDir, float radius,
        int chunk, string? areaArg, int shardK, int shardN, int limit, string logPath, long minFreeBytes, string? priorityFile = null)
    {
        var (ax0, ax1, ay0, ay1) = ParseArea(areaArg);
        string bakeDir = Path.Combine(outDir, "_bake");
        Directory.CreateDirectory(bakeDir);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        void Log(string s) { string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {s}"; Console.WriteLine(line); log.WriteLine(line); }

        // Every tile of one set must come from the same settings: tiles baked with another agent
        // radius would be mixed in silently. The first run records them; a resume must match.
        string settingsFile = Path.Combine(bakeDir, "settings.txt");
        // geometry= names what the obstacles are made from: tiles from the drawing meshes (before
        // 2026-10-02) and from the collision geometry must not be mixed in one set.
        string geometry = NavBake.DrawMeshes ? "" : $" geometry=collision-v2" + (NavBake.AgentMaxClimb != 1f ? $" climb={NavBake.AgentMaxClimb:0.##}" : "");
        string settings = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"radius={radius:0.###} chunk={chunk}{geometry}{(NavBake.TiledCellSizeValue != 0.5f ? $" cell={NavBake.TiledCellSizeValue:0.###}" : "")}");
        if (File.Exists(settingsFile))
        {
            string was = File.ReadAllText(settingsFile).Trim();
            if (was != settings)
            {
                Log($"REFUSED: this folder was baked with '{was}', this run is '{settings}'. Use another --out, or the same settings.");
                return 5;
            }
        }
        else File.WriteAllText(settingsFile, settings);

        // A stopped run can leave a half-written tile's .tmp behind; the tile itself is untouched.
        foreach (string tmp in Directory.GetFiles(outDir, "nav_*.tile.tmp")) { try { File.Delete(tmp); } catch { } }

        char[,] map = Survey(sampler, outDir);
        bool Wanted(int x, int y) => x >= ax0 && x <= ax1 && y >= ay0 && y <= ay1 && map[x, y] == Land;

        // Chunks on a fixed grid (multiples of the chunk size), row by row from the south-west.
        var chunks = new List<(int x0, int y0, int land)>();
        for (int cy = 0; cy < 256; cy += chunk)
            for (int cx = 0; cx < 256; cx += chunk)
            {
                int land = 0;
                for (int x = cx; x < Math.Min(256, cx + chunk); x++)
                    for (int y = cy; y < Math.Min(256, cy + chunk); y++)
                        if (Wanted(x, y)) land++;
                if (land > 0) chunks.Add((cx, cy, land));
            }
        // --priority <locations.json>: chunks nearest a town first, so a bake that has to stop
        // early (time, disk) has covered the places players go.
        if (priorityFile != null)
        {
            var towns = Towns(priorityFile);
            Log($"priority: {towns.Count} towns from {priorityFile}");
            if (towns.Count > 0)
                chunks = chunks.OrderBy(c =>
                {
                    double cx = c.x0 + chunk / 2.0, cy = c.y0 + chunk / 2.0;
                    return towns.Min(t => (t.x - cx) * (t.x - cx) + (t.y - cy) * (t.y - cy));
                }).ToList();
        }
        var mine = chunks.Where((c, i) => i % shardN == shardK).ToList();

        // Resume: every done-*.txt in _bake (any shard), for this chunk size.
        var done = new HashSet<string>();
        foreach (string f in Directory.GetFiles(bakeDir, "done*.txt"))
            foreach (string l in File.ReadAllLines(f))
            {
                var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) done.Add(parts[0] + " " + parts[1]);
            }
        string Key(int x0, int y0) => $"c{chunk} {((x0 << 8) | y0):X4}";
        var todo = mine.Where(c => !done.Contains(Key(c.x0, c.y0))).ToList();
        int landTotal = mine.Sum(c => c.land), landTodo = todo.Sum(c => c.land);
        Log($"world bake: {Describe(map)}; area {ax0:X2}..{ax1:X2},{ay0:X2}..{ay1:X2}; chunk {chunk}x{chunk}; shard {shardK}/{shardN}: " +
            $"{mine.Count} chunks ({landTotal} landblocks), {mine.Count - todo.Count} already done, {todo.Count} to go ({landTodo} landblocks); radius {radius}; out {outDir}");

        string doneFile = Path.Combine(bakeDir, shardN > 1 ? $"done-{shardK}of{shardN}.txt" : "done.txt");
        var all = Stopwatch.StartNew();
        int n = 0, tilesTotal = 0, landDone = 0, offGrid = 0, failed = 0;
        foreach (var c in todo)
        {
            if (n >= limit) { Log($"stopping after --limit {limit} chunks"); break; }
            // A whole-world bake writes ~2 GB: stop (resumably) before the disk fills up.
            long free = FreeBytes(outDir);
            if (free >= 0 && free < minFreeBytes)
            {
                Log($"PAUSED: only {free / (1024 * 1024)} MB free on the output drive (minimum {minFreeBytes / (1024 * 1024)} MB). Free some space and rerun the same command to resume.");
                return 4;
            }
            n++;
            int x1 = Math.Min(255, c.x0 + chunk - 1), y1 = Math.Min(255, c.y0 + chunk - 1);
            var sw = Stopwatch.StartNew();
            int tiles = 0, empty = 0; bool gridOk = true; string err = "";
            try
            {
                // Context: every landblock next to one we keep (land or water); open water
                // farther out adds nothing to the kept tiles and only costs time.
                bool NearWanted(int gx, int gy)
                {
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int wx = gx + dx, wy = gy + dy;
                            if (wx >= c.x0 && wx <= x1 && wy >= c.y0 && wy <= y1 && Wanted(wx, wy)) return true;
                        }
                    return false;
                }
                NavBake.BakeRegionTiled(sampler, geo, c.x0, x1, c.y0, y1, outDir, radius, lb => Wanted((int)(lb >> 8), (int)(lb & 0xFF)),
                    out tiles, out empty, out gridOk, NearWanted);
            }
            catch (InvalidOperationException) { throw; }   // the output guard: never carry on
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
            double secs = sw.Elapsed.TotalSeconds;
            landDone += c.land;
            if (err.Length > 0) { failed++; Log($"chunk 0x{((c.x0 << 8) | c.y0):X4} FAILED after {secs:F1} s: {err} (not marked done; a rerun retries it)"); continue; }
            if (!gridOk) offGrid++;
            tilesTotal += tiles;
            // Only a chunk the --area covers whole is done: a chunk cut by the area still has
            // landblocks to bake, and a later run (another area, or the whole world) does it again.
            bool whole = c.x0 >= ax0 && x1 <= ax1 && c.y0 >= ay0 && y1 <= ay1;
            File.AppendAllText(doneFile, $"{(whole ? Key(c.x0, c.y0) : "partial " + Key(c.x0, c.y0))} land={c.land} tiles={tiles} empty={empty} grid={(gridOk ? "ok" : "OFF")} secs={secs:F1}\n");
            double rate = all.Elapsed.TotalSeconds / Math.Max(1, landDone);   // seconds per landblock
            TimeSpan eta = TimeSpan.FromSeconds(rate * (landTodo - landDone));
            Log($"chunk {n}/{todo.Count} 0x{((c.x0 << 8) | c.y0):X4}: {c.land} land, {tiles} tiles, {empty} empty{(gridOk ? "" : ", GRID OFF")}, {secs:F1} s | " +
                $"{landDone}/{landTodo} landblocks, {tilesTotal} tiles, {rate:F2} s/landblock, ETA {eta:hh\\:mm\\:ss}");
        }
        Log($"world bake {(n >= todo.Count ? "DONE" : "paused")}: {n} chunks this run, {tilesTotal} tiles, {offGrid} off-grid, {failed} failed, {all.Elapsed:hh\\:mm\\:ss}");
        return failed == 0 ? 0 : 3;
    }
}

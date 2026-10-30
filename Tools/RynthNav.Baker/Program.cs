using System.Diagnostics;
using System.Globalization;
using RynthCore.TerrainData;
using RynthNav.Baker;

// RynthNav.Baker — offline AC navmesh baker.
//   single:  --lb 0xA9B4 --out <dir>
//   region:  --region 60,66,40,90 --out <dir>   (lbX 0x60..0x66, lbY 0x40..0x90 inclusive, hex; solo tiles)
//   tiled:   --tiled 9F,A5,A9,AF --out <dir>     (one connected chunk)
//   world:   --world --out <dir> [--chunk 8] [--area x0,x1,y0,y1] [--shard k/n] [--log <file>]
//            every landblock of Dereth that has land, in grid-aligned chunks; resumable;
//            pauses when the output drive has under --min-free-mb (default 1024) left;
//            --priority <locations.json> bakes the chunks nearest a town first
//   survey:  --survey --out <dir>                 (classify landblocks: land / water / none; no baking)
//   map:     --map --out <dir> [--ppc 2] [--area x0,x1,y0,y1] [--map-tiles] [--textured [--objects]]
//   world:   --map-world --ppc 2|4|8|16|32 --out <dir> [--no-objects] [--threads n] [--name f.rmap] [--chunks x0,x1,y0,y1]   (the whole world, textured: worldmap.rmap v2, DXT1 tiles; resumable)
//   (map:   (and --map-vectors: worldmap.vec, building outlines + roads; with --area also a PNG preview)   (top-down render of Dereth, world_map.png; --map-tiles also writes worldmap.rmap, the in-game map's tile pyramid)
//   seams:   --seams <tile dir>                   (how many neighbouring tiles link at their shared edge)
//   graph:   --graph <tile dir> [--graph-out <file>]  (the coarse route graph, navgraph.bin; no rebake)
//   watch:   --watch --out <dir> --force-live     (bake ahead of the player; the live folder needs --force-live)
//   inspect: --inspect x0,x1,y0,y1 --out <dir> [--tiles <dir>]   (baker input + collision + navmesh as .obj)
//   path:    --path LLLL:x,y LLLL:x,y [--tiles <dir>]             (a route, and whether it goes through collision)
//   sweep:   --sweep LLLL,LLLL,... [--tiles <dir>] [--pairs n] [--verbose]   (random routes vs collision)
//   check:   --check [--tiles <dir>]                              (the regression set: castle, ring, towns)
//   portal:  --portal "<Atlas portal>" --from LLLL:x,y|<Atlas name> [--tiles <dir>]   (RynthNav's last leg to a portal)
//   model:   --model 0x0200xxxx[,..]                              (a model's collision shape)
//   audit:   --audit                                              (read every landblock's collision; no baking)
// Options: --ac <dats>  --noobs  --radius <m>  --force-live  --dumpapi
//          --draw-geometry   bake from drawing meshes as before 2026-10-02 (comparisons only)
//
// --out is required for anything that writes. The live folder C:\Games\RynthCore\NavData is
// refused unless --force-live is given too (a bare run once overwrote a live tile).

if (args.Contains("--dumpapi")) { ApiDump.Run(); return 0; }

string? GetArg(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}

static uint ParseLb(string s)
{
    s = s.Trim();
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
    return Convert.ToUInt32(s, 16) & 0xFFFF;
}

// Next landblock to bake: prefer stepping toward the target, else rings around the player.
static (int, int)? FindNextUnbaked(int pX, int pY, int tX, int tY, string dir, System.Collections.Generic.HashSet<int> attempted)
{
    bool Miss(int x, int y) => x >= 0 && x <= 255 && y >= 0 && y <= 255 && !attempted.Contains((x << 8) | y)
        && !File.Exists(Path.Combine(dir, $"nav_{((x << 8) | y):X4}.tile"));
    int dx = tX - pX, dy = tY - pY, steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
    for (int s = 1; s <= Math.Min(steps, 14); s++)
    {
        int x = pX + (int)Math.Round((double)dx * s / Math.Max(steps, 1));
        int y = pY + (int)Math.Round((double)dy * s / Math.Max(steps, 1));
        if (Miss(x, y)) return (x, y);
    }
    for (int r = 0; r <= 6; r++)
        for (int x = pX - r; x <= pX + r; x++)
            for (int y = pY - r; y <= pY + r; y++)
                if (Math.Max(Math.Abs(x - pX), Math.Abs(y - pY)) == r && Miss(x, y)) return (x, y);
    return null;
}

// ── Seam check (reads tiles only; no dats needed) ─────────────────────────────
string? seamsDir = GetArg("--seams");
if (seamsDir != null)
{
    var r = SeamCheck.Run(seamsDir, GetArg("--area"));
    Console.WriteLine(r);
    return 0;
}

// ── Coarse route graph (reads tiles only; no dats needed) ──────────────────────
string? graphDir = GetArg("--graph");
if (graphDir != null)
{
    string graphOut = GetArg("--graph-out") ?? Path.Combine(graphDir, RynthNav.Routing.NavGraph.FileName);
    OutputGuard.ForceLive = args.Contains("--force-live");
    try { OutputGuard.Check(Path.GetDirectoryName(Path.GetFullPath(graphOut))!); }
    catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    GraphBuild.Run(graphDir, graphOut, Console.WriteLine);
    return 0;
}

string acFolder = GetArg("--ac") ?? @"C:\Games\ACE\Dats";
if (float.TryParse(GetArg("--cell"), NumberStyles.Float, CultureInfo.InvariantCulture, out float cellArg) && cellArg >= 0.1f && Math.Abs(192f / cellArg - Math.Round(192f / cellArg)) < 1e-3)
    NavBake.TiledCellSizeValue = cellArg;

// ── Path query (reads tiles + dats; writes nothing) ───────────────────────────
//   --path LLLL:x,y LLLL:x,y --tiles <dir>   (landblock hex : local metres)
if (args.Contains("--audit"))
{
    var geoA = new RynthCore2.Raycast.GeometryLoader();
    if (!geoA.Initialize(acFolder)) { Console.Error.WriteLine(geoA.StatusMessage); return 1; }
    using var samplerA = new TerrainSampler();
    if (!samplerA.Initialize(acFolder)) { Console.Error.WriteLine(samplerA.Status); return 1; }
    Diag.Audit(samplerA, geoA, Console.WriteLine);
    return 0;
}

// ── Dungeons (the cell dat's non-building EnvCells) ───────────────────────────
//   --dungeons-survey [--limit n]        read them all and print sizes/bounds; writes nothing
//   --dungeons all|LLLL,LLLL --out <dir> [--radius r] [--limit n]   bake dun_LLLL.tile for each
if (GetArg("--probe-dungeon") is string pdSpec)
{
    var pts = args.SkipWhile(a => a != "--probe-dungeon").Skip(2).TakeWhile(a => !a.StartsWith("--")).ToArray();
    return ProbeCheck.Dungeon(pdSpec, pts, GetArg("--tiles") ?? OutputGuard.LiveNavData, Console.WriteLine);
}

if (GetArg("--probe") is string probeSpec)
{
    using var samplerPr = new TerrainSampler();
    if (!samplerPr.Initialize(acFolder)) { Console.Error.WriteLine(samplerPr.Status); return 1; }
    return ProbeCheck.Run(samplerPr, probeSpec, GetArg("--tiles") ?? OutputGuard.LiveNavData, Console.WriteLine);
}

if (args.Contains("--buildings-list"))
{
    var geoL = new RynthCore2.Raycast.GeometryLoader();
    if (!geoL.Initialize(acFolder)) { Console.Error.WriteLine(geoL.StatusMessage); return 1; }
    return BuildingCheck.List(geoL, Console.Error.WriteLine);
}

if (GetArg("--buildings-check") is string bcheck)
{
    var geoB = new RynthCore2.Raycast.GeometryLoader();
    if (!geoB.Initialize(acFolder)) { Console.Error.WriteLine(geoB.StatusMessage); return 1; }
    return BuildingCheck.Run(geoB, bcheck, GetArg("--tiles") ?? OutputGuard.LiveNavData, Console.WriteLine);
}

if (GetArg("--dungeons-check") is string dcheck)
{
    var geoK = new RynthCore2.Raycast.GeometryLoader();
    if (!geoK.Initialize(acFolder)) { Console.Error.WriteLine(geoK.StatusMessage); return 1; }
    return DungeonCheck.Run(geoK, dcheck, GetArg("--tiles") ?? "", Console.WriteLine);
}

if (args.Contains("--dungeons-survey") || GetArg("--dungeons") != null)
{
    var geoD = new RynthCore2.Raycast.GeometryLoader();
    if (!geoD.Initialize(acFolder)) { Console.Error.WriteLine(geoD.StatusMessage); return 1; }
    var cgD = NavBake.Collision(geoD);
    int limD = int.TryParse(GetArg("--limit"), out int ld) ? ld : int.MaxValue;
    if (args.Contains("--dungeons-survey")) return DungeonBake.Survey(cgD, limD, Console.WriteLine);
    if (GetArg("--dungeons") == "near") return DungeonBake.Near(cgD, Convert.ToUInt32(GetArg("--lb") ?? "7", 16), GetArg("--at") ?? "0,0", Console.WriteLine);
    if (GetArg("--dungeons") == "model") return DungeonBake.ModelInfo(cgD, Convert.ToUInt32(GetArg("--model") ?? "0", 16), Console.WriteLine);
    if (GetArg("--dungeons") == "models") return DungeonBake.Models(cgD, Convert.ToUInt32(GetArg("--lb") ?? "7", 16), Console.WriteLine);
    string? dOut = GetArg("--out");
    if (dOut == null) { Console.Error.WriteLine("--out <folder> is required for --dungeons"); return 2; }
    dOut = Path.GetFullPath(dOut);
    OutputGuard.ForceLive = args.Contains("--force-live");
    try { OutputGuard.Check(dOut); } catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }
    Directory.CreateDirectory(dOut);
    float radD = float.TryParse(GetArg("--radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out float rD) ? rD : 0.6f;
    return DungeonBake.Run(cgD, GetArg("--dungeons")!, limD, dOut, radD, Console.WriteLine);
}

if (args.Contains("--check"))
{
    var geoC = new RynthCore2.Raycast.GeometryLoader();
    if (!geoC.Initialize(acFolder)) { Console.Error.WriteLine(geoC.StatusMessage); return 1; }
    using var samplerC = new TerrainSampler();
    if (!samplerC.Initialize(acFolder)) { Console.Error.WriteLine(samplerC.Status); return 1; }
    return Diag.RegressionCheck(GetArg("--tiles") ?? OutputGuard.LiveNavData, samplerC, geoC, Console.WriteLine) == 0 ? 0 : 6;
}

// --portal "<Atlas portal name>" --from LLLL:x,y|<Atlas name> [--tiles <dir>] [--atlas locations.json]
string? portalArg = GetArg("--portal");
if (portalArg != null)
{
    if (GetArg("--atlas") is string atl) Diag.AtlasPath = atl;
    var geoR = new RynthCore2.Raycast.GeometryLoader();
    if (!geoR.Initialize(acFolder)) { Console.Error.WriteLine(geoR.StatusMessage); return 1; }
    using var samplerR = new TerrainSampler();
    if (!samplerR.Initialize(acFolder)) { Console.Error.WriteLine(samplerR.Status); return 1; }
    string tdirR = GetArg("--tiles") ?? OutputGuard.LiveNavData, fromR = GetArg("--from") ?? "";
    if (portalArg == "*")
    {
        // Every housing portal on the map, walked to from 40 m away (4 directions): how many the
        // 0.6.4 goal lookup and the fixed one reach.
        var housing = Diag.Atlas().Where(l => l.type == "Portal" && (l.name.Contains("Estates") || l.name.Contains("Cottages") || l.name.Contains("Villas"))).ToList();
        int oldOk = 0, newOk = 0, n = 0;
        foreach (var h in housing)
            foreach (var (dx, dy) in new[] { (40, 0), (-40, 0), (0, 40), (0, -40) })
            {
                double sx = h.x + dx, sy = h.y + dy;
                int lx = (int)(sx / 192), ly = (int)(sy / 192);
                string from = string.Create(CultureInfo.InvariantCulture, $"{(lx << 8) | ly:X4}:{sx - lx * 192:F1},{sy - ly * 192:F1}");
                n++;
                bool o = Diag.PortalReach(tdirR, samplerR, geoR, from, h.name, true, 7.3f, out string lo);
                bool w = Diag.PortalReach(tdirR, samplerR, geoR, from, h.name, false, 7.3f, out string lw);
                if (o) oldOk++; if (w) newOk++;
                if (!w) Console.WriteLine("fails: " + lw);
                else if (!o && args.Contains("--verbose")) Console.WriteLine("fixed: " + lo);
            }
        Console.WriteLine($"housing portals: {housing.Count}, walks: {n}; reached with the 0.6.4 goal lookup: {oldOk}, with the fix: {newOk}");
        return 0;
    }
    foreach (bool old in new[] { true, false })
    {
        bool ok = Diag.PortalReach(tdirR, samplerR, geoR, fromR, portalArg, old, 7.3f, out string line);
        Console.WriteLine($"{(ok ? "reaches" : "FAILS  ")}  {line}");
    }
    return 0;
}

string? descModel = GetArg("--model");
if (descModel != null)
{
    var geoM = new RynthCore2.Raycast.GeometryLoader();
    if (!geoM.Initialize(acFolder)) { Console.Error.WriteLine(geoM.StatusMessage); return 1; }
    foreach (var id in descModel.Split(','))
        foreach (var line in NavBake.Collision(geoM).DescribeModel(Convert.ToUInt32(id.Replace("0x", ""), 16))) Console.WriteLine(line);
    return 0;
}

string? sweep = GetArg("--sweep");
if (sweep != null)
{
    var geoS = new RynthCore2.Raycast.GeometryLoader();
    if (!geoS.Initialize(acFolder)) { Console.Error.WriteLine(geoS.StatusMessage); return 1; }
    var lbs = sweep.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ParseLb);
    int pairs = int.TryParse(GetArg("--pairs"), out int pp) ? pp : 200;
    Diag.Verbose = args.Contains("--verbose");
    using var samplerS = new TerrainSampler();
    if (!samplerS.Initialize(acFolder)) { Console.Error.WriteLine(samplerS.Status); return 1; }
    Diag.Sweep(GetArg("--tiles") ?? OutputGuard.LiveNavData, samplerS, geoS, lbs, pairs, Console.WriteLine);
    return 0;
}

string? pathFrom = GetArg("--path");
if (pathFrom != null)
{
    int ia = Array.FindIndex(args, a => string.Equals(a, "--path", StringComparison.OrdinalIgnoreCase));
    string pathTo = args[ia + 2];
    string tdir = GetArg("--tiles") ?? OutputGuard.LiveNavData;
    var a = Diag.ParsePoint(pathFrom); var b = Diag.ParsePoint(pathTo);
    var geoP = new RynthCore2.Raycast.GeometryLoader();
    if (!geoP.Initialize(acFolder)) { Console.Error.WriteLine(geoP.StatusMessage); return 1; }
    using var samplerP = new TerrainSampler();
    if (!samplerP.Initialize(acFolder)) { Console.Error.WriteLine(samplerP.Status); return 1; }
    Console.WriteLine(Diag.DescribeRoute(tdir, samplerP, geoP, a, b));
    return 0;
}
string? outArg = GetArg("--out");
if (outArg == null)
{
    Console.Error.WriteLine("--out <folder> is required (where the tiles go). The live folder " + OutputGuard.LiveNavData +
                            " also needs --force-live.");
    return 2;
}
string outDir = Path.GetFullPath(outArg);
OutputGuard.ForceLive = args.Contains("--force-live");
try { OutputGuard.Check(outDir); }
catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }
bool noObs = args.Contains("--noobs");
NavBake.DrawMeshes = args.Contains("--draw-geometry");
if (float.TryParse(GetArg("--climb"), NumberStyles.Float, CultureInfo.InvariantCulture, out float climb)) NavBake.AgentMaxClimb = climb;
float radius = float.TryParse(GetArg("--radius"), NumberStyles.Float, CultureInfo.InvariantCulture, out float rr) ? rr : 2.0f;
Directory.CreateDirectory(outDir);

Console.WriteLine($"RynthNav.Baker — dats={acFolder}  out={outDir}  obstacles={!noObs}  radius={radius}");

using var sampler = new TerrainSampler();
if (!sampler.Initialize(acFolder)) { Console.Error.WriteLine($"TerrainSampler init failed: {sampler.Status}"); return 1; }

RynthCore2.Raycast.GeometryLoader? MakeGeo()
{
    if (noObs) return null;
    var g = new RynthCore2.Raycast.GeometryLoader();
    if (g.Initialize(acFolder)) return g;
    Console.WriteLine($"obstacles: GeometryLoader init failed ({g.StatusMessage}); terrain only");
    return null;
}

string? inspect = GetArg("--inspect");
if (inspect != null)
{
    var p = inspect.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (p.Length != 4) { Console.Error.WriteLine("--inspect needs minX,maxX,minY,maxY (hex)"); return 1; }
    using var geoI = MakeGeo();
    Diag.Inspect(sampler, geoI, Convert.ToInt32(p[0], 16), Convert.ToInt32(p[1], 16), Convert.ToInt32(p[2], 16), Convert.ToInt32(p[3], 16), outDir, GetArg("--tiles"));
    return 0;
}

if (args.Contains("--survey"))
{
    var map = WorldBake.Survey(sampler, outDir);
    Console.WriteLine(WorldBake.Describe(map));
    return 0;
}

if (GetArg("--rmap-png") is string rmapFile)
    return WorldMapBake.ExtractPng(rmapFile, int.TryParse(GetArg("--level"), out int rlv) ? rlv : 0, GetArg("--tiles"), outDir, Console.WriteLine);

if (args.Contains("--map-world"))
{
    // The in-game world map in the game's own look (worldmap.rmap version 2, DXT1 tiles). See WorldMapBake.
    var geoW2 = new RynthCore2.Raycast.GeometryLoader();
    if (!geoW2.Initialize(acFolder)) { Console.Error.WriteLine(geoW2.StatusMessage); return 1; }
    int wppc = int.TryParse(GetArg("--ppc"), out int wp) ? wp : 8;
    int wthreads = int.TryParse(GetArg("--threads"), out int wt) ? wt : Math.Max(1, Environment.ProcessorCount - 1);
    int cx0 = 0, cx1 = 255, cy0 = 0, cy1 = 255;
    if (GetArg("--chunks") is string chunkArg)
    {
        var cp = chunkArg.Split(',', StringSplitOptions.TrimEntries);
        if (cp.Length != 4 || !int.TryParse(cp[0], out cx0) || !int.TryParse(cp[1], out cx1) || !int.TryParse(cp[2], out cy0) || !int.TryParse(cp[3], out cy1))
        { Console.Error.WriteLine("--chunks needs x0,x1,y0,y1 (chunk columns from the west, rows from the north, decimal)"); return 1; }
    }
    var wopts = new WorldMapBake.Options(wppc, !args.Contains("--no-objects"), wthreads, GetArg("--name") ?? WorldMapBake.DefaultName(wppc), cx0, cx1, cy0, cy1);
    return WorldMapBake.Run(sampler, geoW2, outDir, wopts, Console.WriteLine);
}

if (args.Contains("--map-vectors"))
{
    // worldmap.vec: building outlines and roads for the World Map. With --area (and --ppc) also a PNG crop of the terrain with them drawn on, to check by eye.
    using var geoV = new RynthCore2.Raycast.GeometryLoader();
    if (!geoV.Initialize(acFolder)) { Console.Error.WriteLine(geoV.StatusMessage); return 1; }
    var vec = WorldMapVectors.Build(sampler, geoV, Console.WriteLine);
    OutputGuard.Check(outDir);
    string vecFile = Path.Combine(outDir, "worldmap.vec");
    WorldMapVectors.Write(vecFile, vec);
    Console.WriteLine($"wrote {vecFile} ({new FileInfo(vecFile).Length / 1024} KB)");
    if (GetArg("--area") != null)
        return WorldMapRender.Run(sampler, outDir, int.TryParse(GetArg("--ppc"), out int ppcV) ? ppcV : 8, GetArg("--area"), acFolder, false, vec);
    return 0;
}

if (args.Contains("--map"))
{
    // --textured: the game's own terrain textures, blended per cell as the client does (TexturedTerrain), instead of flat colours.
    RynthCore2.Raycast.GeometryLoader? geoM = null;
    if (args.Contains("--textured"))
    {
        geoM = new RynthCore2.Raycast.GeometryLoader();
        if (!geoM.Initialize(acFolder)) { Console.Error.WriteLine(geoM.StatusMessage); return 1; }
    }
    return WorldMapRender.Run(sampler, outDir, int.TryParse(GetArg("--ppc"), out int ppcArg) ? ppcArg : 2, GetArg("--area"), acFolder, args.Contains("--map-tiles"), null, geoM, args.Contains("--objects"));
}

if (args.Contains("--world"))
{
    int chunk = int.TryParse(GetArg("--chunk"), out int c) && c >= 1 && c <= 32 ? c : 8;
    int shardK = 0, shardN = 1;
    string? shard = GetArg("--shard");
    if (shard != null)
    {
        var sp = shard.Split('/');
        if (sp.Length != 2 || !int.TryParse(sp[0], out shardK) || !int.TryParse(sp[1], out shardN) || shardN < 1 || shardK < 0 || shardK >= shardN)
        { Console.Error.WriteLine("--shard needs k/n, e.g. 0/2"); return 1; }
    }
    int limit = int.TryParse(GetArg("--limit"), out int lim) ? lim : int.MaxValue;
    using var geoW = MakeGeo();
    return WorldBake.Run(sampler, geoW, outDir, radius, chunk, GetArg("--area"), shardK, shardN, limit,
        GetArg("--log") ?? Path.Combine(outDir, "_bake", shardN > 1 ? $"progress-{shardK}of{shardN}.log" : "progress.log"),
        (long.TryParse(GetArg("--min-free-mb"), out long mf) ? mf : 1024) * 1024 * 1024, GetArg("--priority"));
}

string? region = GetArg("--region");
if (region != null)
{
    var p = region.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (p.Length != 4) { Console.Error.WriteLine("--region needs minX,maxX,minY,maxY (hex)"); return 1; }
    int x0 = Convert.ToInt32(p[0], 16), x1 = Convert.ToInt32(p[1], 16);
    int y0 = Convert.ToInt32(p[2], 16), y1 = Convert.ToInt32(p[3], 16);
    int total = (x1 - x0 + 1) * (y1 - y0 + 1);
    Console.WriteLine($"region bake: lbX 0x{x0:X2}..0x{x1:X2}  lbY 0x{y0:X2}..0x{y1:X2}  ({total} landblocks)…");

    using var geo = MakeGeo();
    int ok = 0, empty = 0, none = 0, done = 0;
    for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        {
            uint key = (uint)((x << 8) | y);
            int polys;
            try { polys = NavBake.BakeLandblock(sampler, geo, key, outDir, false, radius); }
            catch (InvalidOperationException) { throw; }
            catch { polys = -1; }
            if (polys > 0) ok++; else if (polys == 0) empty++; else none++;
            if (++done % 50 == 0) Console.WriteLine($"  {done}/{total} … (tiles={ok})");
        }
    Console.WriteLine($"region bake DONE: {ok} tiles written, {empty} empty, {none} no-terrain (of {total}).");
    return 0;
}

string? tiled = GetArg("--tiled");
if (tiled != null)
{
    var p = tiled.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (p.Length != 4) { Console.Error.WriteLine("--tiled needs minX,maxX,minY,maxY (hex)"); return 1; }
    int x0 = Convert.ToInt32(p[0], 16), x1 = Convert.ToInt32(p[1], 16), y0 = Convert.ToInt32(p[2], 16), y1 = Convert.ToInt32(p[3], 16);
    Console.WriteLine($"tiled bake: lbX 0x{x0:X2}..0x{x1:X2}  lbY 0x{y0:X2}..0x{y1:X2}  (radius={radius})…");
    using var geoT = MakeGeo();
    var sw = Stopwatch.StartNew();
    NavBake.BakeRegionTiled(sampler, geoT, x0, x1, y0, y1, outDir, radius, out int tiles, out int empty);
    Console.WriteLine($"tiled bake DONE: {tiles} tiles written, {empty} empty, {sw.Elapsed.TotalSeconds:F1} s.");
    if (x1 > x0) Console.WriteLine("connectivity X: " + NavBake.ValidateConnectivity(outDir, (uint)((x0 << 8) | y0), (uint)(((x0 + 1) << 8) | y0)));
    if (y1 > y0) Console.WriteLine("connectivity Y: " + NavBake.ValidateConnectivity(outDir, (uint)((x0 << 8) | y0), (uint)((x0 << 8) | (y0 + 1))));
    return 0;
}

if (args.Contains("--watch"))
{
    int R = int.TryParse(GetArg("--chunk"), out int cr) ? cr : 3; // chunk half-size (7x7 default)
    string posFile = Path.Combine(outDir, "_player.txt");
    Console.WriteLine($"RynthNav.Baker WATCH — baking ahead of the player (chunk {2 * R + 1}x{2 * R + 1}, radius={radius}). Watching {posFile}. Ctrl+C to stop.");
    using var geoW = MakeGeo();
    var attempted = new System.Collections.Generic.HashSet<int>();
    while (true)
    {
        bool didWork = false;
        try
        {
            if (File.Exists(posFile))
            {
                var f = File.ReadAllText(posFile).Trim().Split(',');
                if (f.Length >= 4 && int.TryParse(f[0], out int pX) && int.TryParse(f[1], out int pY) && int.TryParse(f[2], out int tX) && int.TryParse(f[3], out int tY))
                {
                    var next = FindNextUnbaked(pX, pY, tX, tY, outDir, attempted);
                    if (next.HasValue)
                    {
                        int cx = next.Value.Item1, cy = next.Value.Item2;
                        Console.Write($"[{DateTime.Now:HH:mm:ss}] baking chunk @ 0x{((cx << 8) | cy):X4} … ");
                        NavBake.BakeRegionTiled(sampler, geoW, Math.Max(0, cx - R), Math.Min(255, cx + R), Math.Max(0, cy - R), Math.Min(255, cy + R), outDir, radius, out int t, out _);
                        for (int x = cx - R; x <= cx + R; x++)
                            for (int y = cy - R; y <= cy + R; y++)
                                if (x >= 0 && x <= 255 && y >= 0 && y <= 255) attempted.Add((x << 8) | y);
                        Console.WriteLine($"{t} tiles.");
                        didWork = true;
                    }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("watch error: " + ex.Message); }
        if (!didWork) System.Threading.Thread.Sleep(1500);
    }
}

// Single landblock.
uint lb = ParseLb(GetArg("--lb") ?? "A9B4");
using (var geo = MakeGeo())
{
    int polys = NavBake.BakeLandblock(sampler, geo, lb, outDir, writeObj: true, radius);
    if (polys < 0) { Console.Error.WriteLine($"0x{lb:X4}: no terrain / build failed"); return 1; }
    Console.WriteLine($"0x{lb:X4}: {polys} polys -> {Path.Combine(outDir, $"nav_{lb:X4}.tile")}");
}
return 0;

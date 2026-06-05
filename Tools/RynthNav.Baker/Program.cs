using RynthCore.TerrainData;
using RynthNav.Baker;

// RynthNav.Baker — offline AC navmesh baker.
//   single:  --lb 0xA9B4
//   region:  --region 60,66,40,90   (lbX 0x60..0x66, lbY 0x40..0x90 inclusive, hex)
// Options: --ac <dats>  --out <dir>  --noobs  --dumpapi

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

string acFolder = GetArg("--ac") ?? @"C:\Games\ACE\Dats";
string outDir = GetArg("--out") ?? @"C:\Games\RynthCore\NavData";
bool noObs = args.Contains("--noobs");
float radius = float.TryParse(GetArg("--radius"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rr) ? rr : 2.0f;
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
    NavBake.BakeRegionTiled(sampler, geoT, x0, x1, y0, y1, outDir, radius, out int tiles, out int empty);
    Console.WriteLine($"tiled bake DONE: {tiles} tiles written, {empty} empty.");
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

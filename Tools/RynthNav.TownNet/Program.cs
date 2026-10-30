using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using RynthNav.TownNet;
using DatDb = RynthCore.Plugin.RynthAi.Raycasting.DatDatabase;

// RynthNav.TownNet: the walks inside the Town Network, for townnet.json.
//
//   build  --in <townnet.json> [--out <file>] [--ac <dats>]
//          plans a walk from every arrival point to every exit portal, checks each one, and writes
//          them into the file (default: back into --in) as "walks", with "walkInfo".
//   check  --in <townnet.json> [--ac <dats>]
//          checks the walks already in the file against the dats; exit 1 when any fails or is missing.
//   dump   [--lb 0x0007] [--ac <dats>] [--stabs 1]
//          lists a landblock's cells: floors, walls, doorways, static objects.
//   map    --in <townnet.json> --out <file.svg> [--from A1] [--z 0]
//          a floor plan with the walls, objects, portals and walks, to look them over.
//   test   geometry self-tests on a made-up two-room floor (no dats); exit 0 = pass.
//
// --ac defaults to C:\Games\ACE\Dats (client_cell_1.dat + client_portal.dat, opened read only).
// The portal list in townnet.json comes from RynthCore's tools/navdata/gen_townnet.py.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string? Arg(string name)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
string acDir = Arg("--ac") ?? @"C:\Games\ACE\Dats";

if (cmd == "test") return SelfTest.Run();

if (cmd is not ("build" or "check" or "dump" or "map"))
{
    Console.Error.WriteLine("usage: RynthNav.TownNet build|check|map|dump|test (see Program.cs)");
    return 2;
}

using var cellDat = new DatDb();
using var portalDat = new DatDb();
string cellPath = Path.Combine(acDir, "client_cell_1.dat"), portalPath = Path.Combine(acDir, "client_portal.dat");
if (!cellDat.Open(cellPath) || !portalDat.Open(portalPath))
{
    Console.Error.WriteLine("cannot open the dats in " + acDir);
    return 2;
}

if (cmd == "dump" && Arg("--model") is string model)
{
    uint mid = Convert.ToUInt32(model.Replace("0x", ""), 16);
    var shape = new ModelReader(portalDat).Get(mid);
    Console.WriteLine($"model {mid:X8}: parsed {shape.Parsed}, radius {shape.Radius:F2}, height {shape.Height:F2}, {shape.Polygons.Count} physics polygons");
    foreach (var c in shape.Cylinders) Console.WriteLine($"  cylinder at ({c.Origin.X:F2},{c.Origin.Y:F2},{c.Origin.Z:F2}) r {c.Radius:F2} h {c.Height:F2}");
    foreach (var s in shape.Spheres) Console.WriteLine($"  sphere at ({s.Center.X:F2},{s.Center.Y:F2},{s.Center.Z:F2}) r {s.Radius:F2}");
    if (shape.Polygons.Count > 0)
        Console.WriteLine($"  polygons span x {shape.Polygons.Min(p => p.Min(v => v.X)):F2}..{shape.Polygons.Max(p => p.Max(v => v.X)):F2}, " +
            $"y {shape.Polygons.Min(p => p.Min(v => v.Y)):F2}..{shape.Polygons.Max(p => p.Max(v => v.Y)):F2}, z {shape.Polygons.Min(p => p.Min(v => v.Z)):F2}..{shape.Polygons.Max(p => p.Max(v => v.Z)):F2}");
    return 0;
}

if (cmd == "dump")
{
    uint lb = Convert.ToUInt32((Arg("--lb") ?? "0x0007").Replace("0x", ""), 16);
    var cells = CellDat.LoadLandblock(cellDat, portalDat, lb);
    Console.WriteLine($"landblock {lb:X4}: {cells.Count} cells");
    foreach (var c in cells)
    {
        string fz = c.Floors.Count == 0 ? "-" : $"{c.Floors.Min(f => f.Min(v => v.Z)):F2}..{c.Floors.Max(f => f.Max(v => v.Z)):F2}";
        Console.WriteLine($"{c.Id:X8} env {c.EnvironmentId:X8}/{c.CellStructure} at ({c.Origin.X:F1},{c.Origin.Y:F1},{c.Origin.Z:F2}) " +
            $"floors {c.Floors.Count} z {fz} walls {c.Walls.Count} stabs {c.Stabs.Count} doorways " +
            string.Join(" ", c.Portals.Select(p => $"{p.OtherCell:X4}")));
        if (Arg("--stabs") != null)
            foreach (var s in c.Stabs) Console.WriteLine($"    object {s.Id:X8} at ({s.Origin.X:F1},{s.Origin.Y:F1},{s.Origin.Z:F2})");
    }
    return 0;
}

string inPath = Arg("--in") ?? throw new ArgumentException("--in <townnet.json> is required");
var doc = TownNetFile.Load(inPath);
var sw = Stopwatch.StartNew();
var world = WorldLoader.Load(doc, cellDat, portalDat, out var reachable);
var grid = new NavGrid(world, reachable);
// --extra <m>: the margin kept beyond the walker's body (default 0.5; a walk with no room at that margin is planned at 0.25).
float extra = Arg("--extra") is string ex ? float.Parse(ex, CultureInfo.InvariantCulture) : WalkPlanner.DefaultExtra;
var planner = new WalkPlanner(world, grid, extra);
var fallbackPlanner = extra > 0.25f ? new WalkPlanner(world, grid, 0.25f) : null;
Console.WriteLine($"{reachable.Count} cells, {grid.Nodes.Count:N0} floor samples, {world.Solids.Count:N0} solid shapes, " +
    $"{world.Portals.Count} exit portals; walker radius {world.AgentRadius:F2} m ({sw.Elapsed.TotalSeconds:F1} s)");
foreach (var n in world.Notes) Console.WriteLine("  " + n);
var radii = world.Portals.GroupBy(p => MathF.Round(p.Radius, 2)).Select(g => $"{g.Key:F2} m x{g.Count()}");
Console.WriteLine("  portal radii: " + string.Join(", ", radii));

var arrivals = doc["arrivals"]!.AsArray().Select(a => (
    Id: (string)a!["id"]!, Cell: WorldLoader.Hex(a["cell"]), P: new Vector3(WorldLoader.F(a["x"]), WorldLoader.F(a["y"]), WorldLoader.F(a["z"])))).ToList();

if (cmd == "map")
{
    string svgPath = Arg("--out") ?? throw new ArgumentException("--out <file.svg> is required");
    float level = Arg("--z") is string zs ? float.Parse(zs, CultureInfo.InvariantCulture) : 0f;
    File.WriteAllText(svgPath, MapSvg.Draw(world, reachable, doc, Arg("--from"), level));
    Console.WriteLine("wrote " + svgPath);
    return 0;
}

if (cmd == "build")
{
    var walks = new List<Walk>();
    foreach (var a in arrivals)
    {
        var ws = planner.PlanFrom(a.Id, a.Cell, a.P, world.Portals);
        if (fallbackPlanner != null && ws.Any(w => w.Error != null))
        {
            var alt = fallbackPlanner.PlanFrom(a.Id, a.Cell, a.P, world.Portals);
            for (int i = 0; i < ws.Count; i++)
                if (ws[i].Error != null)
                {
                    var better = alt.FirstOrDefault(x => x.To == ws[i].To && x.Error == null);
                    if (better != null) { ws[i] = better; Console.WriteLine($"    {better.ExitName}: no room at {extra:0.##} m, planned at 0.25 m"); }
                }
        }
        walks.AddRange(ws);
        var ok = ws.Where(w => w.Error == null).ToList();
        Console.WriteLine($"{a.Id} {a.Cell:X8} ({a.P.X:F1},{a.P.Y:F1}): {ok.Count}/{ws.Count} exits, " +
            (ok.Count > 0 ? $"length {ok.Min(w => w.Length):F1}..{ok.Max(w => w.Length):F1} m, avg {ok.Average(w => w.Length):F1} m, " +
                            $"{ok.Average(w => w.Points.Count):F1} waypoints avg, min clearance {ok.Min(w => w.MinClear):F2} m" : ""));
        foreach (var w in ws.Where(w => w.Error != null)) Console.WriteLine($"    {w.ExitName}: {w.Error}");
        if (Arg("--verbose") != null)
            foreach (var w in ok) Console.WriteLine($"    {w.ExitName}: {w.Length:F1} m, {w.Points.Count} points, clear {w.MinClear:F2} ({w.MinClearAt}), portals {w.MinPortalClear:F2}");
    }
    var jw = new JsonArray();
    foreach (var w in walks) jw.Add(ToJson(w));
    var good = walks.Where(w => w.Error == null).ToList();
    var info = new JsonObject
    {
        ["tool"] = "RynthNav.TownNet build",
        ["dats"] = new JsonArray(DatInfo(cellPath), DatInfo(portalPath)),
        ["grid"] = NavGrid.Cell,
        ["walkerRadius"] = R2(world.AgentRadius),
        ["clearance"] = R2(planner.MustClear),
        ["margin"] = R2(extra),
        ["portalClearance"] = R2(planner.MustPortal),
        ["cells"] = reachable.Count,
        ["walks"] = walks.Count,
        ["walkable"] = good.Count,
        ["lengthMin"] = good.Count > 0 ? R2(good.Min(w => w.Length)) : null,
        ["lengthMax"] = good.Count > 0 ? R2(good.Max(w => w.Length)) : null,
        ["lengthAvg"] = good.Count > 0 ? R2(good.Average(w => w.Length)) : null,
    };
    doc["walkInfo"] = info;
    doc["walks"] = jw;
    string outPath = Arg("--out") ?? inPath;
    // The live NavData folder and the bake folders are never written by accident.
    string outFull = Path.GetFullPath(outPath);
    if (outFull.StartsWith(@"C:\Games\RynthCore\Nav", StringComparison.OrdinalIgnoreCase) && !args.Contains("--force-live"))
    {
        Console.Error.WriteLine($"refusing to write {outFull}: that is RynthCore's live NavData (or a bake). Pass --force-live to mean it.");
        return 2;
    }
    TownNetFile.Save(outPath, doc);
    Console.WriteLine($"{good.Count}/{walks.Count} walks; wrote {outPath} (set {(string)doc["setId"]!}) in {sw.Elapsed.TotalSeconds:F1} s");
    return good.Count == walks.Count ? 0 : 1;
}

// check
{
    int bad = 0, n = 0;
    string stored = (string?)doc["setId"] ?? "", actual = TownNetFile.SetId(File.ReadAllText(inPath));
    if (stored != actual) { Console.WriteLine($"set id {stored} does not match the file's content ({actual})"); bad++; }
    var have = new HashSet<(string, string)>();
    foreach (var j in doc["walks"]?.AsArray() ?? new JsonArray())
    {
        string from = (string)j!["from"]!, to = (string)j["to"]!;
        have.Add((from, to));
        if (j["error"] != null) { Console.WriteLine($"{from} -> {(string?)j["exit"]}: no walk ({(string?)j["error"]})"); bad++; continue; }
        var target = world.Portals.FirstOrDefault(p => p.Guid == to);
        if (target == null) { Console.WriteLine($"{from} -> {to}: no such exit portal"); bad++; continue; }
        var w = new Walk { From = from, To = to, ExitName = target.Name };
        foreach (var p in j["points"]!.AsArray())
        {
            var a = p!.AsArray();
            w.Points.Add(new Waypoint(WorldLoader.Hex(a[0]), new Vector3(WorldLoader.F(a[1]), WorldLoader.F(a[2]), WorldLoader.F(a[3]))));
        }
        var arr = arrivals.FirstOrDefault(x => x.Id == from);
        if (arr.Id == null) { Console.WriteLine($"{from} -> {to}: no such arrival"); bad++; continue; }
        if (w.Points.Count < 2 || Vector2.Distance(new Vector2(w.Points[0].P.X, w.Points[0].P.Y), new Vector2(arr.P.X, arr.P.Y)) > 0.05f
            || Vector2.Distance(new Vector2(w.Points[^1].P.X, w.Points[^1].P.Y), new Vector2(target.Pos.X, target.Pos.Y)) > target.Radius)
        { Console.WriteLine($"{from} -> {target.Name}: does not start at the arrival or end in the portal"); bad++; continue; }
        planner.Check(w, target);
        n++;
        if (w.Error != null) { Console.WriteLine($"{from} -> {target.Name}: {w.Error}"); bad++; }
    }
    foreach (var a in arrivals)
        foreach (var p in world.Portals)
            if (!have.Contains((a.Id, p.Guid))) { Console.WriteLine($"{a.Id} -> {p.Name}: missing"); bad++; }
    Console.WriteLine($"checked {n} walks against the dats: {(bad == 0 ? "all pass" : bad + " problem(s)")}");
    return bad == 0 ? 0 : 1;
}

static JsonNode R2(float v) => JsonValue.Create(Math.Round(v, 2))!;

static JsonObject ToJson(Walk w)
{
    var o = new JsonObject { ["from"] = w.From, ["to"] = w.To, ["exit"] = w.ExitName };
    if (w.Error != null) { o["error"] = w.Error; return o; }
    o["length"] = R2(w.Length);
    o["minClear"] = R2(w.MinClear);
    o["cells"] = w.Cells;
    var pts = new JsonArray();
    foreach (var p in w.Points)
        pts.Add(new JsonArray($"{p.Cell:X8}", Math.Round(p.P.X, 2), Math.Round(p.P.Y, 2), Math.Round(p.P.Z, 2)));
    o["points"] = pts;
    return o;
}

static JsonNode DatInfo(string path)
{
    var fi = new FileInfo(path);
    return $"{fi.Name} {fi.Length} bytes {fi.LastWriteTimeUtc:yyyy-MM-dd}";
}

using System.Globalization;
using System.Text;
using DotRecast.Detour;
using DotRecast.Detour.Io;
using RynthCore.TerrainData;

namespace RynthNav.Baker;

// --inspect x0,x1,y0,y1 --out <dir> [--tiles <dir>]
//   Writes what the baker feeds Recast for those landblocks (geom.obj, AC frame: x=EW, y=NS,
//   z=up, world metres), the list of LandBlockInfo placements (placements.txt), and, when
//   --tiles is given, the navmesh polygons of those tiles (navmesh.obj). Nothing is baked.
internal static class Diag
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void Inspect(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader? geo, int x0, int x1, int y0, int y1, string outDir, string? tileDir)
    {
        OutputGuard.Check(outDir);
        Directory.CreateDirectory(outDir);
        var verts = new List<float>(); var faces = new List<int>();
        var groups = new List<(string name, int firstFace)>();
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                groups.Add(($"lb_{(x << 8) | y:X4}", faces.Count / 3));
                NavBake.AppendLandblock(sampler, geo, (uint)((x << 8) | y), verts, faces);
            }
        var sb = new StringBuilder();
        sb.AppendLine("# RynthNav baker input geometry, AC frame (x=EW, y=NS, z=up), world metres");
        for (int i = 0; i < verts.Count; i += 3)
            sb.Append("v ").Append(verts[i].ToString("F3", Inv)).Append(' ').Append(verts[i + 2].ToString("F3", Inv)).Append(' ').Append(verts[i + 1].ToString("F3", Inv)).Append('\n');
        int gi = 0;
        for (int f = 0; f < faces.Count; f += 3)
        {
            while (gi < groups.Count && groups[gi].firstFace == f / 3) { sb.AppendLine("g " + groups[gi].name); gi++; }
            // AppendLandblock stores faces reversed for Recast's +Y up; the AC frame flips handedness back.
            sb.Append("f ").Append(faces[f] + 1).Append(' ').Append(faces[f + 1] + 1).Append(' ').Append(faces[f + 2] + 1).Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, "geom.obj"), sb.ToString());
        Console.WriteLine($"geom.obj: {verts.Count / 3} verts, {faces.Count / 3} tris");

        if (geo != null)
        {
            var pl = new StringBuilder();
            var cg = NavBake.Collision(geo);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    foreach (var p in cg.Describe((uint)((x << 8) | y)))
                        pl.AppendLine(p);
            File.WriteAllText(Path.Combine(outDir, "placements.txt"), pl.ToString());
            var sOld = new StringBuilder(); var sNew = new StringBuilder();
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    uint lb = (uint)((x << 8) | y);
                    var land = sampler.LoadLandblock(lb);
                    if (land == null) continue;
                    var acc = new List<(float wx, float wy, uint model)>();
                    geo.GetTexturedScatter(lb, null, acc);
                    foreach (var a in acc) sOld.AppendLine(string.Create(Inv, $"{a.wx:F2} {a.wy:F2} 0x{a.model:X8}"));
                    foreach (var p2 in cg.AppendScenery(lb, land, new List<CollisionGeometry.Tri>()))
                        sNew.AppendLine(string.Create(Inv, $"{x * 192 + p2.Frame.OriginX:F2} {y * 192 + p2.Frame.OriginY:F2} 0x{p2.ObjId:X8} {p2.Scale:F2}"));
                }
            File.WriteAllText(Path.Combine(outDir, "scenery-old.txt"), sOld.ToString());
            File.WriteAllText(Path.Combine(outDir, "scenery-new.txt"), sNew.ToString());
            var tris = CollectCollision(sampler, geo, x0, x1, y0, y1);
            WriteTris(Path.Combine(outDir, "collision.obj"), tris);
        }

        if (tileDir != null) ExportTiles(tileDir, x0, x1, y0, y1, Path.Combine(outDir, "navmesh.obj"));
    }

    public static void WriteTris(string path, List<CollisionGeometry.Tri> tris)
    {
        var sb = new StringBuilder("# collision triangles, AC frame (x=EW, y=NS, z=up)\n");
        foreach (var t in tris)
            foreach (var v in new[] { t.A, t.B, t.C })
                sb.Append("v ").Append(v.X.ToString("F3", Inv)).Append(' ').Append(v.Y.ToString("F3", Inv)).Append(' ').Append(v.Z.ToString("F3", Inv)).Append('\n');
        for (int i = 0; i < tris.Count; i++) sb.Append("f ").Append(i * 3 + 1).Append(' ').Append(i * 3 + 2).Append(' ').Append(i * 3 + 3).Append('\n');
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"{Path.GetFileName(path)}: {tris.Count} tris");
    }

    /// <summary>"BDD2:188.7,55.5" (landblock hex : local metres) -> world metres.</summary>
    public static (float ew, float ns) ParsePoint(string s)
    {
        var p = s.Split(':');
        uint lb = Convert.ToUInt32(p[0].Replace("0x", ""), 16) & 0xFFFF;
        var xy = p[1].Split(',');
        return (((lb >> 8) & 0xFF) * 192f + float.Parse(xy[0], Inv), (lb & 0xFF) * 192f + float.Parse(xy[1], Inv));
    }

    /// <summary>A route on the tiles between two points, and where its straight legs cross the game's collision.</summary>
    /// <summary>Everything the game collides with in a rectangle of landblocks (what the baker now feeds Recast, minus terrain).</summary>
    public static List<CollisionGeometry.Tri> CollectCollision(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader geo, int x0, int x1, int y0, int y1)
    {
        var cg = NavBake.Collision(geo);
        var tris = new List<CollisionGeometry.Tri>();
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                uint lb = (uint)((x << 8) | y);
                var land = sampler.LoadLandblock(lb);
                cg.AppendStatics(lb, tris, land == null ? null : (lx, ly) => TerrainSampler.GetTerrainZ(land, lx, ly));
                cg.AppendBuildingCells(lb, tris);
                if (land != null) cg.AppendScenery(lb, land, tris);
            }
        return tris;
    }

    public static string DescribeRoute(string tileDir, TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader geo, (float ew, float ns) a, (float ew, float ns) b)
    {
        int ax = (int)(a.ew / 192), ay = (int)(a.ns / 192), bx = (int)(b.ew / 192), by = (int)(b.ns / 192);
        int x0 = Math.Min(ax, bx) - 1, x1 = Math.Max(ax, bx) + 1, y0 = Math.Min(ay, by) - 1, y1 = Math.Max(ay, by) + 1;
        var nc = new NavCheck(tileDir, x0, x1, y0, y1);
        var tz = TerrainZ(sampler);
        var r = nc.Route(a.ew, a.ns, b.ew, b.ns, tz(a.ew, a.ns), tz(b.ew, b.ns), 12f);
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(Inv, $"route {Fmt(a)} -> {Fmt(b)} on {tileDir}: {r}, ends {r.EndDistance(b.ew, b.ns):F1} m from the goal"));
        if (r.Points.Count == 0) return sb.ToString();
        sb.AppendLine("  waypoints: " + string.Join(" ", r.Points.Select(p => Fmt((p.ew, p.ns)))));
        var tris = CollectCollision(sampler, geo, x0, x1, y0, y1);
        var hits = NavCheck.Crossings(r.Points, tris, TerrainZ(sampler));
        sb.AppendLine(hits.Count == 0 ? "  crosses no collision (statics, building cells, scenery)" :
            $"  CROSSES collision {hits.Count}x, first at " + string.Join(" ", hits.Take(6).Select(h => Fmt((h.ew, h.ns)) + $"[0x{h.src:X8}]")));
        return sb.ToString();
    }

    /// <summary>
    /// Random routes inside each landblock (fixed seed); counts the ones whose straight legs go
    /// through the game's building/static collision. Returns the number of crossing routes.
    /// </summary>
    public static bool Verbose;

    public static int Sweep(string tileDir, TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader geo, IEnumerable<uint> lbs, int pairs, Action<string> log)
    {
        int bad = 0;
        foreach (uint lb in lbs)
        {
            int lx = (int)(lb >> 8), ly = (int)(lb & 0xFF);
            var nc = new NavCheck(tileDir, lx - 1, lx + 1, ly - 1, ly + 1);
            var tris = CollectCollision(sampler, geo, lx - 1, lx + 1, ly - 1, ly + 1);
            var rnd = new Random((int)lb);
            bool OnGround((float ew, float ns, float up) p)
            {
                var land = sampler.LoadLandblock((uint)(((int)(p.ew / 192) << 8) | (int)(p.ns / 192)));
                if (land == null) return false;
                float z = land.GetTerrainZWorld(p.ew, p.ns);
                return !float.IsNaN(z) && Math.Abs(p.up - z) < 1.5f;
            }
            int routes = 0, complete = 0, crossing = 0;
            var where = new List<string>();
            for (int i = 0; i < pairs; i++)
            {
                float ax = lx * 192 + (float)rnd.NextDouble() * 192, ay = ly * 192 + (float)rnd.NextDouble() * 192;
                float bx = lx * 192 + (float)rnd.NextDouble() * 192, by = ly * 192 + (float)rnd.NextDouble() * 192;
                var tzs = TerrainZ(sampler);
                var r = nc.Route(ax, ay, bx, by, tzs(ax, ay), tzs(bx, by));
                if (r.Points.Count < 2) continue;
                // Ground-level routes only (not ones that start or end on a roof or a wall top).
                if (!OnGround(r.Points[0]) || !OnGround(r.Points[^1])) continue;
                routes++;
                if (r.Complete) complete++;
                var hits = NavCheck.Crossings(r.Points, tris, TerrainZ(sampler));
                if (hits.Count > 0)
                {
                    crossing++;
                    if (where.Count < 8) where.Add(Fmt((hits[0].ew, hits[0].ns)) + $"[0x{hits[0].src:X8}]");
                    if (Verbose) log($"    {Fmt((r.Points[0].ew, r.Points[0].ns))} -> {Fmt((r.Points[^1].ew, r.Points[^1].ns))} ({(r.Complete ? "complete" : "partial")}) crosses " + string.Join(" ", hits.Take(4).Select(h => Fmt((h.ew, h.ns)) + $"[0x{h.src:X8}]")));
                }
            }
            if (crossing > 0) bad++;
            log($"0x{lb:X4}: {routes} routes ({complete} complete), {crossing} cross collision{(where.Count > 0 ? " e.g. at " + string.Join(" ", where) : "")}");
        }
        return bad;
    }

    /// <summary>RynthNav's Atlas (NavData\locations.json): on-map entries as (name, type, world ew, world ns).</summary>
    public static string AtlasPath = Path.Combine(OutputGuard.LiveNavData, "locations.json");
    private static List<(string name, string type, double x, double y)>? _atlas;
    public static List<(string name, string type, double x, double y)> Atlas()
    {
        if (_atlas != null) return _atlas;
        var list = new List<(string, string, double, double)>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AtlasPath));
        foreach (var l in doc.RootElement.GetProperty("locations").EnumerateArray())
        {
            if (!l.TryGetProperty("ns", out var ns) || !l.TryGetProperty("ew", out var ew)) continue;
            if (l.TryGetProperty("place", out var pl) && pl.GetString() != "outdoor") continue;
            list.Add((l.GetProperty("name").GetString() ?? "", l.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                (ew.GetDouble() * 10.0 + 1019.5) * 24.0, (ns.GetDouble() * 10.0 + 1019.5) * 24.0));
        }
        return _atlas = list;
    }

    /// <summary>
    /// Walk from <paramref name="from"/> (a "LLLL:x,y" point or an Atlas name) to the Atlas portal
    /// <paramref name="portal"/> the way RynthNav plans its last leg; ok when the walk arrives
    /// within <paramref name="reach"/> m (RynthNav uses a portal from 8 yd).
    /// </summary>
    public static bool PortalReach(string tileDir, TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader geo, string from, string portal,
        bool oldLookup, float reach, out string line)
    {
        var atlas = Atlas();
        (float ew, float ns) a;
        if (from.Contains(':')) a = ParsePoint(from);
        else { var t = atlas.First(l => l.name == from); a = ((float)t.x, (float)t.y); }
        var p = atlas.First(l => l.name == portal && l.type == "Portal");
        (float ew, float ns) b = ((float)p.x, (float)p.y);
        int x0 = (int)(Math.Min(a.ew, b.ew) / 192) - 1, x1 = (int)(Math.Max(a.ew, b.ew) / 192) + 1;
        int y0 = (int)(Math.Min(a.ns, b.ns) / 192) - 1, y1 = (int)(Math.Max(a.ns, b.ns) / 192) + 1;
        var tz = TerrainZ(sampler);
        var portals = atlas.Where(l => l.type == "Portal").Select(l => (l.x, l.y));
        var r = new NavCheck(tileDir, x0, x1, y0, y1).PortalWalk(a.ew, a.ns, tz(a.ew, a.ns), b.ew, b.ns, tz(b.ew, b.ns), portals, oldLookup, out string how);
        float end = r.EndDistance(b.ew, b.ns);
        var hits = r.Points.Count > 1 ? NavCheck.Crossings(r.Points, CollectCollision(sampler, geo, x0, x1, y0, y1), tz) : new();
        bool ok = r.Complete && end <= reach && hits.Count == 0;
        line = string.Create(Inv, $"{from} -> {portal} ({Fmt(b)}){(oldLookup ? " [0.6.4 goal lookup]" : "")}: {r}, {how}, ends {(end == float.MaxValue ? "-" : end.ToString("F1", Inv))} m from it (need {reach:F0}){(hits.Count > 0 ? $", goes through collision at {Fmt((hits[0].ew, hits[0].ns))}" : "")}");
        return ok;
    }

    /// <summary>
    /// The regression set (--check): Bandit Castle (BDD2), the housing portal ring (C9A8), and two
    /// towns that must stay connected (Holtburg A9B4, Shoushi DA55). Needs those landblocks' tiles
    /// (and their neighbours) in <paramref name="tileDir"/>. Returns the number of failures.
    /// </summary>
    public static int RegressionCheck(string tileDir, TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader geo, Action<string> log)
    {
        int fails = 0;
        void Result(bool ok, string what) { if (!ok) fails++; log($"{(ok ? "PASS" : "FAIL")}  {what}"); }

        // A route that must exist and keep out of every wall, post, tree and building cell.
        NavCheck.Result? Route(string from, string to, string why, Func<NavCheck.Result, bool>? extra = null, string extraWhy = "")
        {
            var a = ParsePoint(from); var b = ParsePoint(to);
            int x0 = (int)(Math.Min(a.ew, b.ew) / 192) - 1, x1 = (int)(Math.Max(a.ew, b.ew) / 192) + 1;
            int y0 = (int)(Math.Min(a.ns, b.ns) / 192) - 1, y1 = (int)(Math.Max(a.ns, b.ns) / 192) + 1;
            var r = new NavCheck(tileDir, x0, x1, y0, y1).Route(a.ew, a.ns, b.ew, b.ns);
            var hits = r.Points.Count > 1 ? NavCheck.Crossings(r.Points, CollectCollision(sampler, geo, x0, x1, y0, y1), TerrainZ(sampler)) : new();
            bool ok = r.Complete && hits.Count == 0 && (extra == null || extra(r));
            Result(ok, $"{why}: {from} -> {to}: {r}{(hits.Count > 0 ? $", goes through collision at {Fmt((hits[0].ew, hits[0].ns))}" : "")}{(extra != null && r.Complete && !extra(r) ? ", " + extraWhy : "")}");
            return r;
        }
        // Random routes inside a landblock: none may go through collision; most must arrive.
        void Area(string lbHex, double minComplete)
        {
            uint lb = Convert.ToUInt32(lbHex, 16);
            string line = "";
            int crossing = Sweep(tileDir, sampler, geo, new[] { lb }, 300, l => line = l);
            var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+) routes \((\d+) complete\), (\d+) cross");
            int routes = int.Parse(m.Groups[1].Value), complete = int.Parse(m.Groups[2].Value), cross = int.Parse(m.Groups[3].Value);
            double frac = routes == 0 ? 0 : (double)complete / routes;
            Result(cross == 0 && routes > 100 && frac >= minComplete,
                string.Create(Inv, $"0x{lbHex}: 300 random routes: {routes} on the ground, {frac:P0} arrive (need {minComplete:P0}), {cross} go through collision (need 0)"));
        }

        log("-- Bandit Castle (BDD2): the walls are real, the east gate is open");
        // The gate arch spans local y 79.8..88.3 at x 180..192 (gatehouse 0x01000804 at 186,84).
        bool ThroughGate(NavCheck.Result r)
        {
            for (int i = 1; i < r.Points.Count; i++)
            {
                var (ax, ay, _) = r.Points[i - 1]; var (bx, by, _) = r.Points[i];
                float gx = 0xBD * 192 + 186f;
                if ((ax - gx) * (bx - gx) > 0) continue;
                float y = ay + (by - ay) * (gx - ax) / (bx - ax) - 0xD2 * 192;
                if (y > 79.8f && y < 88.3f) return true;
            }
            return false;
        }
        Route("BED2:10,84", "BDD2:137,95", "outside the east gate to the inner courtyard", ThroughGate, "but not through the gate");
        Route("BDD2:188.7,55.5", "BDD2:176.6,63.3", "where Lucy stuck (SE corner) to the courtyard NPC behind the wall", ThroughGate, "but not through the gate");
        Route("BDD2:105,84", "BED2:15,84", "west of the castle to east of it, round the outside");
        Route("BDD2:156,30", "BDD2:156,140", "south of the castle to north of it");
        Area("BDD2", 0.85);

        log("-- Housing portal ring (C9A8)");
        Route("C9A8:140,95", "C9A8:160,108", "across the ring past the stuck spot");
        Route("C9A8:120,80", "C9A8:180,120", "through the ring");
        Area("C9A8", 0.95);

        log("-- Housing portals (walked to the way RynthNav plans it; it uses a portal from 8 yd)");
        foreach (var (from, portal) in new[]
        {
            ("E632:108,9.6", "Lady Maila Estates Portal"),     // the Mayoi Town Network landing (Lucy, 10-02 16:04)
            ("Tou-Tou", "Dame Tolani Villas Portal"),
            ("Kara", "Direvale Villas Portal"),
            ("Baishi", "Ring of Crystals Estates Portal"),
        })
        {
            bool ok = PortalReach(tileDir, sampler, geo, from, portal, false, 7.3f, out string line);
            Result(ok, line);
        }

        log("-- Towns that must stay connected");
        foreach (string t in new[] { "A9B4", "DA55" })
        {
            Route($"{t}:20,170", $"{t}:170,20", $"across {t} NW-SE");
            Route($"{t}:10,96", $"{t}:182,96", $"across {t} W-E");
            Route($"{t}:96,10", $"{t}:96,182", $"across {t} S-N");
            Area(t, 0.85);
            uint lb = Convert.ToUInt32(t, 16);
            foreach (uint nb in new[] { lb + 0x100, lb + 1 })
            {
                string c = NavBake.ValidateConnectivity(tileDir, lb, nb);
                Result(c.Contains("reaches-") && c.EndsWith("=True"), $"0x{lb:X4} links to its neighbour 0x{nb:X4}: {c}");
            }
        }
        log(fails == 0 ? "check: all passed" : $"check: {fails} FAILED");
        return fails;
    }

    /// <summary>
    /// --audit: reads every landblock's collision geometry (no baking) and counts models or cells
    /// that could not be read, and the landblocks with the most triangles.
    /// </summary>
    public static void Audit(TerrainSampler sampler, RynthCore2.Raycast.GeometryLoader geo, Action<string> log)
    {
        var cg = NavBake.Collision(geo);
        int lbs = 0, models = 0, badModels = 0, cellsBad = 0; long tris = 0;
        var top = new List<(int n, uint lb)>();
        var badIds = new HashSet<uint>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int x = 0; x < 256; x++)
            for (int y = 0; y < 256; y++)
            {
                uint lb = (uint)((x << 8) | y);
                var land = sampler.LoadLandblock(lb);
                if (land == null) continue;
                lbs++;
                foreach (var p in cg.ReadLandBlockInfo(lb))
                {
                    models++;
                    bool ok = (p.Model >> 24) == 0x01 ? cg.GfxObj(p.Model) != null : cg.SetupModel(p.Model) != null;
                    if (!ok) { badModels++; badIds.Add(p.Model); }
                }
                var t = new List<CollisionGeometry.Tri>();
                cg.AppendStatics(lb, t, (lx, ly) => TerrainSampler.GetTerrainZ(land, lx, ly));
                int before = t.Count;
                try { cg.AppendBuildingCells(lb, t); } catch { cellsBad++; }
                cg.AppendScenery(lb, land, t);
                tris += t.Count;
                top.Add((t.Count, lb));
            }
        log($"audit: {lbs} landblocks, {models} placed models ({badModels} unreadable: {string.Join(" ", badIds.Take(10).Select(i => $"0x{i:X8}"))}), {cg.CellsRead} building cells read, {cg.CellsFailed} unreadable, {cg.CellObjects} objects in them, {tris} collision triangles, {sw.Elapsed.TotalSeconds:F0} s");
        log("most triangles: " + string.Join(" ", top.OrderByDescending(v => v.n).Take(8).Select(v => $"0x{v.lb:X4}={v.n}")));
    }

    /// <summary>Terrain height at a world point (on AC's per-cell triangle split); NaN off the map.</summary>
    public static Func<float, float, float> TerrainZ(TerrainSampler sampler) => (x, y) =>
    {
        uint lb = (uint)((((int)(x / 192)) << 8) | (int)(y / 192));
        var land = sampler.LoadLandblock(lb);
        return land == null ? float.NaN : TerrainSampler.GetTerrainZ(land, x - land.WorldOriginX, y - land.WorldOriginY);
    };

    public static string Fmt((float ew, float ns) p)
    {
        int lx = (int)(p.ew / 192), ly = (int)(p.ns / 192);
        return string.Create(Inv, $"{(lx << 8) | ly:X4}:{p.ew - lx * 192:F1},{p.ns - ly * 192:F1}");
    }

    /// <summary>Navmesh polygons (the walkable surface) of the tiles in a rectangle, AC frame.</summary>
    public static void ExportTiles(string tileDir, int x0, int x1, int y0, int y1, string objPath)
    {
        OutputGuard.Check(Path.GetDirectoryName(Path.GetFullPath(objPath))!);
        var sb = new StringBuilder();
        sb.AppendLine("# RynthNav navmesh polygons, AC frame (x=EW, y=NS, z=up)");
        int vbase = 1, n = 0;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                string path = Path.Combine(tileDir, $"nav_{(x << 8) | y:X4}.tile");
                if (!File.Exists(path)) continue;
                DtMeshData md;
                using (var fr = File.OpenRead(path)) using (var br = new BinaryReader(fr)) md = new DtMeshDataReader().Read(br, NavBake.VertsPerPoly);
                sb.AppendLine($"g tile_{(x << 8) | y:X4}");
                for (int i = 0; i < md.header.vertCount; i++)
                    sb.Append("v ").Append(md.verts[i * 3].ToString("F3", Inv)).Append(' ').Append(md.verts[i * 3 + 2].ToString("F3", Inv)).Append(' ').Append(md.verts[i * 3 + 1].ToString("F3", Inv)).Append('\n');
                for (int p = 0; p < md.header.polyCount; p++)
                {
                    DtPoly poly = md.polys[p];
                    if (poly.GetPolyType() != DtPolyTypes.DT_POLYTYPE_GROUND) continue;
                    sb.Append('f');
                    for (int k = 0; k < poly.vertCount; k++) sb.Append(' ').Append(vbase + poly.verts[k]);
                    sb.Append('\n');
                    n++;
                }
                vbase += md.header.vertCount;
            }
        File.WriteAllText(objPath, sb.ToString());
        Console.WriteLine($"{Path.GetFileName(objPath)}: {n} polys from {tileDir}");
    }
}

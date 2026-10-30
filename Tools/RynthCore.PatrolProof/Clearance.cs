using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.PatrolProof;

// "clear" mode: how far each point the dungeon pathfinder lays is from the nearest wall or
// edge of the walkable floor (DungeonGeometry.Clearance), by the rule that laid it
// (DungeonPathfinder.KindLog), for the patrol from a dozen start cells and for dunnav /
// recovery paths between 40 random cell pairs, in each landblock given. Also the route
// totals (points, doorway points, lap length, turns), to compare runs before and after a
// change, and each leg's least clearance along it (sampled every 0.25 m).
//
// Run: dotnet run -c Release -- clear [landblock hex ...] [worst N] [human] [csv <file>]
internal static class Clearance
{
    private static readonly string[] Buckets = { "<0.25", "<0.50", "<0.75", "<1.00", "<1.50", ">=1.5" };
    private static readonly double[] Edges = { 0.25, 0.5, 0.75, 1.0, 1.5 };

    public static int Run(string[] args)
    {
        string acDir = @"C:\Turbine\Asheron's Call";
        var lbs = new List<uint>();
        int worst = 0;
        string csv = null;
        bool human = false;
        var probes = new List<(double X, double Y, double Z)>();
        var legProbes = new List<double[]>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "worst" && i + 1 < args.Length) worst = int.Parse(args[++i]);
            else if (a == "csv" && i + 1 < args.Length) csv = args[++i];
            else if (a == "human") human = true;
            else if (a == "leg" && i + 6 < args.Length)
            {
                var v = Enumerable.Range(1, 6).Select(k => double.Parse(args[i + k], CultureInfo.InvariantCulture)).ToArray();
                legProbes.Add(v);
                i += 6;
            }
            else if (a == "probe" && i + 3 < args.Length)
            {
                probes.Add((double.Parse(args[i + 1], CultureInfo.InvariantCulture), double.Parse(args[i + 2], CultureInfo.InvariantCulture), double.Parse(args[i + 3], CultureInfo.InvariantCulture)));
                i += 3;
            }
            else if (a.Length <= 6 && uint.TryParse(a.Replace("0x", ""), NumberStyles.HexNumber, null, out uint v)) lbs.Add(v);
            else acDir = a;
        }
        if (lbs.Count == 0) lbs.AddRange(new uint[] { 0x6346, 0x6544, 0x0174 });

        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("Geometry loader failed: " + loader.StatusMessage); return 1; }

        if (human)
        {
            var s = new SetupInfo();
            byte[] data = loader.PortalDat.GetFileData(0x02000001);
            if (data != null && s.Unpack(data))
                Console.WriteLine($"Human setup 0x02000001: radius {s.Radius:F3} height {s.Height:F3}, cylinders [{string.Join(", ", s.Cylinders.Select(c => $"r={c.Radius:F3} h={c.Height:F3}"))}], spheres [{string.Join(", ", s.Spheres.Select(c => $"r={c.Radius:F3} z={c.Center.Z:F2}"))}]");
            else Console.WriteLine("Human setup: could not read");
        }

        StreamWriter csvOut = csv != null ? new StreamWriter(csv) : null;
        csvOut?.WriteLine("lb,set,kind,x,y,z,clearance");
        var pathfinderType = typeof(DungeonPathfinder);

        foreach (uint lb in lbs)
        {
            DungeonPathfinder.InvalidateCache();
            var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
            if (graph.Count == 0) { Console.WriteLine($"0x{lb:X4}: no cells"); continue; }
            var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
            var floors = loader.DungeonLOS.GetDungeonMapFloorPolygons(lb);
            Console.WriteLine();
            Console.WriteLine($"=== 0x{lb:X4}: {graph.Count} cells, {geo.DoorwayCount} openings, floors for {geo.FloorCellCount} cells");
            foreach (var v in legProbes)
            {
                double least = geo.LegClearance(v[0], v[1], v[2], v[3], v[4], v[5], 1.0, 0.5, out double wx, out double wy, out double wz);
                double cx = wx, cy = wy;
                double cc = geo.PushClear(ref cx, ref cy, wz, 1.0, 2.5);
                double la = geo.LegClearance(v[0], v[1], v[2], cx, cy, wz, 1.0, 0.5, out _, out _, out _);
                double lb2 = geo.LegClearance(cx, cy, wz, v[3], v[4], v[5], 1.0, 0.5, out _, out _, out _);
                Console.WriteLine($"  leg ({v[0]},{v[1]},{v[2]})->({v[3]},{v[4]},{v[5]}): least {least:F2} at ({wx:F2},{wy:F2},{wz:F2}); corner pushed to ({cx:F2},{cy:F2}) clearance {cc:F2}; new legs {la:F2} / {lb2:F2}");
                foreach (var d in geo.DoorwaysNear(wx, wy, wz, 4.0))
                    Console.WriteLine($"    opening {d.CellA:X8}-{d.CellB:X8} ({d.X:F2},{d.Y:F2},{d.Z:F2}) w={d.Width:F2} clearance {geo.Clearance(d.X, d.Y, d.Z, 1.5):F2}; legs {geo.LegClearance(v[0], v[1], v[2], d.X, d.Y, d.Z, 1.0, 0.5, out _, out _, out _):F2} / {geo.LegClearance(d.X, d.Y, d.Z, v[3], v[4], v[5], 1.0, 0.5, out _, out _, out _):F2}");
                var two = new List<NavPoint> { NP(v[0], v[1], v[2]), NP(v[3], v[4], v[5]) };
                DungeonPathfinder.KeepLegsOffWalls(geo, two, false);
                Console.WriteLine("    plugin's leg pass: " + string.Join(" -> ", two.Select(p => $"({X(p):F2},{Y(p):F2})")));
            }
            if (legProbes.Count > 0 && probes.Count == 0) continue;
            if (probes.Count > 0)
            {
                foreach (var (px, py, pz) in probes)
                {
                    var runs = geo.ProbeRuns(px, py, pz, 4.0);
                    Console.WriteLine($"  probe ({px}, {py}, {pz}): clearance {geo.Clearance(px, py, pz, 4.0):F2}, on walkable floor {geo.OnWalkableFloor(px, py, pz)}; runs E,ENE,..: {string.Join(" ", runs.Select(r => r.ToString("F2")))}");
                    foreach (var w in geo.WallsNear(px, py, 1.0).OrderBy(w => w.Dist))
                        Console.WriteLine($"    wall ({w.X0:F2},{w.Y0:F2})-({w.X1:F2},{w.Y1:F2}) z {w.ZMin:F2}..{w.ZMax:F2} at {w.Dist:F2}");
                    foreach (var f in floors.Where(f => f.Vertices.Any(v => Math.Abs(v.X - px) < 2 && Math.Abs(v.Y - py) < 2)))
                        Console.WriteLine($"    floor {f.CellId:X8} up={DungeonGeometry.IsUpwardFloor(f.Vertices)}: " + string.Join(" ", f.Vertices.Select(v => $"({v.X:F2},{v.Y:F2},{v.Z:F2})")));
                }
                continue;
            }

            // Patrol from a dozen start cells.
            var kinds = new Dictionary<NavPoint, string>(ReferenceEqualityComparer.Instance);
            DungeonPathfinder.KindLog = kinds;
            var starts = graph.Keys.OrderBy(k => k).Where((k, i) => i % Math.Max(1, graph.Count / 12) == 0).ToList();
            var patrolSamples = new List<(string Kind, NavPoint P, double C)>();
            var patrolLegs = new List<double>();
            var worstLegs = new List<(NavPoint A, NavPoint B, double C, double Wx, double Wy)>();
            int pts = 0, door = 0, t60 = 0, t120 = 0, off = 0; double len = 0, buildMs = 0;
            foreach (uint s in starts)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = DungeonPathfinder.BuildPatrolRoute(graph, s, null, geo);
                buildMs += sw.Elapsed.TotalMilliseconds;
                int n = r.Points.Count;
                pts += n; door += r.Points.Count(p => p.Doorway);
                for (int i = 0; i < n; i++)
                {
                    var a = r.Points[i]; var b = r.Points[(i + 1) % n];
                    len += Yd(a, b);
                    if (n > 2)
                    {
                        double turn = TurnDeg(a, b, r.Points[(i + 2) % n]);
                        if (turn > 60) t60++;
                        if (turn > 120) t120++;
                    }
                    if (!LegOnFloor(a, b, floors)) off++;
                    double lc = LegClearance(geo, a, b, out double wx, out double wy);
                    patrolLegs.Add(lc);
                    worstLegs.Add((a, b, lc, wx, wy));
                    patrolSamples.Add((kinds.TryGetValue(a, out var k) ? k : "other", a, PointClearance(geo, a)));
                }
            }
            Console.WriteLine($"  patrol, {starts.Count} starts: {pts} points ({door} doorway), {len:F0} yd, turns >60 {t60}, >120 {t120}, off-floor legs {off}; built in {buildMs / starts.Count:F0} ms each");
            Table("patrol", patrolSamples, patrolLegs);

            // Dunnav / recovery paths.
            kinds.Clear();
            var rnd = new Random(6346);
            var ids = graph.Keys.OrderBy(k => k).ToList();
            var pathSamples = new List<(string Kind, NavPoint P, double C)>();
            var pathLegs = new List<double>();
            int pairs = 0, ppts = 0, pdoor = 0, pt60 = 0, poff = 0; double plen = 0;
            for (int k = 0; k < 40; k++)
            {
                uint a = ids[rnd.Next(ids.Count)], b = ids[rnd.Next(ids.Count)];
                var path = DungeonPathfinder.FindPath(a, b, graph);
                if (path.Count < 3) continue;
                pairs++;
                var na = graph[a]; var nb = graph[b];
                var r = DungeonPathfinder.BuildNavRoute(path, graph, nb.NS, nb.EW, geo, null, na.NS, na.EW, na.Z, nb.Z);
                r.Points.Insert(0, new NavPoint { NS = na.NS, EW = na.EW, Z = na.Z / 240.0 });
                ppts += r.Points.Count; pdoor += r.Points.Count(p => p.Doorway);
                for (int i = 0; i + 1 < r.Points.Count; i++)
                {
                    plen += Yd(r.Points[i], r.Points[i + 1]);
                    if (i + 2 < r.Points.Count && TurnDeg(r.Points[i], r.Points[i + 1], r.Points[i + 2]) > 60) pt60++;
                    if (!LegOnFloor(r.Points[i], r.Points[i + 1], floors)) poff++;
                    // Legs from the start cell centre and into the destination aren't the pathfinder's.
                    if (i > 0 && i + 2 < r.Points.Count) pathLegs.Add(LegClearance(geo, r.Points[i], r.Points[i + 1], out _, out _));
                }
                for (int i = 1; i + 1 < r.Points.Count; i++)
                    pathSamples.Add((kinds.TryGetValue(r.Points[i], out var kk) ? kk : "other", r.Points[i], PointClearance(geo, r.Points[i])));
            }
            DungeonPathfinder.KindLog = null;
            Console.WriteLine($"  dunnav/recovery, {pairs} paths: {ppts} points ({pdoor} doorway), {plen:F0} yd, turns >60 {pt60}, off-floor legs {poff}");
            Table("paths", pathSamples, pathLegs);

            if (worst > 0)
            {
                Console.WriteLine($"  worst {worst} patrol points:");
                foreach (var w in patrolSamples.OrderBy(s => s.C).Take(worst))
                    Console.WriteLine($"    {w.Kind,-16} ({X(w.P):F2}, {Y(w.P):F2}, {w.P.Z * 240:F2}) clearance {w.C:F2}");
                Console.WriteLine($"  worst {worst} path points:");
                foreach (var w in pathSamples.OrderBy(s => s.C).Take(worst))
                    Console.WriteLine($"    {w.Kind,-16} ({X(w.P):F2}, {Y(w.P):F2}, {w.P.Z * 240:F2}) clearance {w.C:F2}");
                Console.WriteLine($"  worst {worst} patrol legs:");
                foreach (var w in worstLegs.OrderBy(s => s.C).Take(worst))
                    Console.WriteLine($"    ({X(w.A):F2}, {Y(w.A):F2}, {w.A.Z * 240:F2}) -> ({X(w.B):F2}, {Y(w.B):F2}, {w.B.Z * 240:F2}) least {w.C:F2} at ({w.Wx:F2}, {w.Wy:F2})");
            }
            if (csvOut != null)
            {
                foreach (var s in patrolSamples) csvOut.WriteLine(Row(lb, "patrol", s));
                foreach (var s in pathSamples) csvOut.WriteLine(Row(lb, "paths", s));
            }
        }
        csvOut?.Dispose();
        return 0;
    }

    private static string Row(uint lb, string set, (string Kind, NavPoint P, double C) s)
        => string.Join(",", $"0x{lb:X4}", set, s.Kind,
            X(s.P).ToString("F2", CultureInfo.InvariantCulture), Y(s.P).ToString("F2", CultureInfo.InvariantCulture),
            (s.P.Z * 240).ToString("F2", CultureInfo.InvariantCulture), s.C.ToString("F3", CultureInfo.InvariantCulture));

    private static void Table(string set, List<(string Kind, NavPoint P, double C)> samples, List<double> legs)
    {
        Console.WriteLine($"    {"kind",-18} {"n",5}  {"min",5}  " + string.Join(" ", Buckets.Select(b => $"{b,6}")));
        foreach (var g in samples.GroupBy(s => s.Kind).OrderBy(g => g.Key))
            Line(g.Key, g.Select(s => s.C).ToList());
        Line("ALL POINTS", samples.Select(s => s.C).ToList());
        Line("legs (least)", legs);
    }

    private static void Line(string name, List<double> cs)
    {
        var counts = new int[Buckets.Length];
        foreach (double c in cs)
        {
            int b = 0;
            while (b < Edges.Length && c >= Edges[b]) b++;
            counts[b]++;
        }
        double min = cs.Count > 0 ? cs.Min() : 0;
        Console.WriteLine($"    {name,-18} {cs.Count,5}  {min,5:F2}  " + string.Join(" ", counts.Select(c => $"{c,6}")));
    }

    private static double PointClearance(DungeonGeometry geo, NavPoint p) => geo.Clearance(X(p), Y(p), p.Z * 240.0, 3.0);

    // The least clearance along a leg, sampled every 0.25 m, leaving out 0.5 m at each end (the
    // end points are measured as points; within 0.5 m of a point with 1 m clearance a leg has at
    // least 0.5 m, so a lower reading there is the sampler taking a stacked floor's height).
    private static double LegClearance(DungeonGeometry geo, NavPoint a, NavPoint b, out double wx, out double wy)
    {
        wx = X(a); wy = Y(a);
        double ax = X(a), ay = Y(a), bx = X(b), by = Y(b), az = a.Z * 240, bz = b.Z * 240;
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        if (len <= 1.0) return 3.0;
        int steps = Math.Max(1, (int)Math.Ceiling(len / 0.25));
        double best = 3.0;
        double zf = az;   // follows the floor (ramps aren't straight between the end points)
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps;
            if (t * len < 0.5 - 1e-9 || t * len > len - 0.5 + 1e-9)
            {
                // still follow the floor's height through the skipped ends
                double sx0 = ax + (bx - ax) * t, sy0 = ay + (by - ay) * t;
                if (geo.TryFloorHeight(sx0, sy0, zf, 0.6, out double h0)) zf = h0;
                continue;
            }
            double x = ax + (bx - ax) * t, y = ay + (by - ay) * t;
            if (geo.TryFloorHeight(x, y, zf, 0.6, out double h)) zf = h;
            else if (geo.TryFloorHeight(x, y, az + (bz - az) * t, 0.6, out h)) zf = h;
            double c = geo.Clearance(x, y, zf, 3.0);
            if (c < best) { best = c; wx = x; wy = y; }
        }
        return best;
    }

    private static NavPoint NP(double x, double y, double z) => new() { EW = DungeonGeometry.WorldToNav(x), NS = DungeonGeometry.WorldToNav(y), Z = z / 240.0 };

    private static double X(NavPoint p) => DungeonGeometry.NavToWorld(p.EW);
    private static double Y(NavPoint p) => DungeonGeometry.NavToWorld(p.NS);

    private static double Yd(NavPoint a, NavPoint b) => Math.Sqrt((a.NS - b.NS) * (a.NS - b.NS) + (a.EW - b.EW) * (a.EW - b.EW)) * 240.0;

    private static double TurnDeg(NavPoint a, NavPoint b, NavPoint c)
    {
        double ux = b.EW - a.EW, uy = b.NS - a.NS, vx = c.EW - b.EW, vy = c.NS - b.NS;
        double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
        if (lu < 1e-9 || lv < 1e-9) return 0;
        double cos = Math.Clamp((ux * vx + uy * vy) / (lu * lv), -1, 1);
        return Math.Acos(cos) * 180 / Math.PI;
    }

    // Same test as Program's: every half metre of the leg over some floor polygon near its height.
    private static bool LegOnFloor(NavPoint a, NavPoint b, List<DungeonLOS.MapPolygon> floors)
    {
        double ax = X(a), ay = Y(a), bx = X(b), by = Y(b), za = a.Z * 240.0, zb = b.Z * 240.0;
        double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        int steps = Math.Max(1, (int)(len / 0.5));
        for (int i = 0; i <= steps; i++)
        {
            double t = (double)i / steps, x = ax + (bx - ax) * t, y = ay + (by - ay) * t, z = za + (zb - za) * t;
            bool ok = false;
            foreach (var f in floors)
            {
                if (!DungeonGeometry.InsidePolygon2D(f.Vertices, x, y)) continue;
                double fz = f.Vertices.Average(v => v.Z);
                if (z - fz >= -1.5 && z - fz <= 4.5) { ok = true; break; }
            }
            if (!ok) return false;
        }
        return true;
    }
}

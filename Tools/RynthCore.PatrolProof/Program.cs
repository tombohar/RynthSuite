using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.PatrolProof;

// Offline look at RynthAi's dungeon patrol (DungeonPathfinder.BuildPatrolRoute) on the real
// dats, read only. For a landblock (0x6346 by default) it builds the patrol the plugin would
// build from a dozen start cells, the old way (cell walk, no map geometry) and the new way
// (rooms + doorways, DungeonGeometry), and compares them:
//   rooms     cells merged into rooms by wide openings
//   points    route points; doorway = points centred on a narrow opening (NavPoint.Doorway)
//   length    one lap, yards
//   turns     legs turning more than 60 / 120 degrees from the previous one (zig-zag)
//   in-room   for rooms of 3+ cells: route length spent inside them per visit, and how many
//             route points fall inside them per visit (a direct crossing has 0-1)
//   off-floor legs whose straight line leaves the floor of the cells along it (walls hit)
// With "dump <dir>" it also writes old.csv / new.csv / cells.csv for plotting.
//
// It also compares /ra dunnav paths between 40 random cell pairs. Extra words:
//   verbose      list the off-floor legs      trace     log every room crossing
//   waypoints    one line per patrol waypoint and the rule that laid it (Waypoints.cs)
//   doors x y    list the cells, openings and floor polygons within 20 m of world (x, y)
//
// Run: dotnet run -c Release [-- <landblock hex>] [-- <AC folder>] [-- dump <dir>] [-- verbose]
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "clear") return Clearance.Run(args.Skip(1).ToArray());
        if (args.Contains("waypoints")) return Waypoints.Run(args);
        uint lb = 0x6346;
        string acDir = @"C:\Turbine\Asheron's Call";
        string dumpDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "dump" && i + 1 < args.Length) dumpDir = args[++i];
            else if (a == "doors") { i += 2; continue; }
            else if (a == "trace" || a == "verbose") continue;
            else if (a.Length <= 6 && uint.TryParse(a.Replace("0x", ""), NumberStyles.HexNumber, null, out uint v)) lb = v;
            else acDir = a;
        }

        var geo = new GeometryLoader();
        if (!geo.Initialize(acDir)) { Console.WriteLine("Geometry loader failed: " + geo.StatusMessage); return 1; }
        Console.WriteLine("Dats: " + geo.StatusMessage);

        var graph = DungeonPathfinder.GetGraph(lb, geo.CellDat);
        var map = DungeonPathfinder.GetGeometry(lb, geo.CellDat, geo.DungeonLOS);
        var rooms = DungeonPathfinder.BuildRooms(graph, map);
        int edges = graph.Values.Sum(n => n.Neighbors.Count) / 2;
        var bigRooms = rooms.Cells.Where(c => c.Count >= 3).ToList();
        Console.WriteLine($"Landblock 0x{lb:X4}: {graph.Count} cells, {edges} portal edges, {map.DoorwayCount} openings matched, floors for {map.FloorCellCount} cells");
        Console.WriteLine($"  rooms: {rooms.Cells.Count} ({rooms.Cells.Count(c => c.Count > 1)} of 2+ cells, {bigRooms.Count} of 3+, largest {rooms.Cells.Max(c => c.Count)} cells)");

        var floorPolys = geo.DungeonLOS.GetDungeonMapFloorPolygons(lb);
        var cellOfBig = new Dictionary<uint, int>();
        for (int r = 0; r < bigRooms.Count; r++) foreach (uint c in bigRooms[r]) cellOfBig[c] = r;

        Verbose = args.Contains("verbose");
        if (args.Contains("trace")) DungeonPathfinder.Trace = m => Console.WriteLine("    " + m);
        if (args.Contains("doors"))
        {
            int di = Array.IndexOf(args, "doors");
            double qx = di + 2 < args.Length ? double.Parse(args[di + 1], CultureInfo.InvariantCulture) : 19018;
            double qy = di + 2 < args.Length ? double.Parse(args[di + 2], CultureInfo.InvariantCulture) : 13265;
            foreach (var node in graph.Values)
            {
                double cx = DungeonGeometry.NavToWorld(node.EW), cy = DungeonGeometry.NavToWorld(node.NS);
                if (Math.Abs(cx - qx) > 20 || Math.Abs(cy - qy) > 20) continue;
                int room = rooms.RoomOf[node.CellId];
                Console.WriteLine($"  cell {node.CellId:X8} c=({cx:F1},{cy:F1},{node.Z:F1}) room {room} ({rooms.Cells[room].Count} cells) nbrs {string.Join(",", node.Neighbors.Select(n => n.ToString("X4").Substring(4 > n.ToString("X").Length ? 0 : n.ToString("X").Length - 4)))} floor={map.HasFloor(node.CellId)}");
            }
            foreach (var node in graph.Values)
                foreach (uint nb in node.Neighbors)
                {
                    if (nb < node.CellId || !map.TryGetDoorway(node.CellId, nb, out var d)) continue;
                    if (Math.Abs(d.X - qx) > 8 || Math.Abs(d.Y - qy) > 8) continue;
                    var o = graph[nb];
                    Console.WriteLine($"  door {d.CellA:X8}->{d.CellB:X8} c=({d.X:F2},{d.Y:F2},{d.Z:F2}) n=({d.Nx:F2},{d.Ny:F2}) w={d.Width:F2}  A=({DungeonGeometry.NavToWorld(node.EW):F2},{DungeonGeometry.NavToWorld(node.NS):F2},{node.Z:F2}) B=({DungeonGeometry.NavToWorld(o.EW):F2},{DungeonGeometry.NavToWorld(o.NS):F2},{o.Z:F2})");
                }
            foreach (var f in floorPolys)
            {
                if (!f.Vertices.Any(v => Math.Abs(v.X - qx) < 8 && Math.Abs(v.Y - qy) < 8)) continue;
                Console.WriteLine($"  floor {f.CellId:X8}: " + string.Join(" ", f.Vertices.Select(v => $"({v.X:F1},{v.Y:F1},{v.Z:F1})")));
            }
            return 0;
        }

        var starts = graph.Keys.OrderBy(k => k).Where((k, i) => i % Math.Max(1, graph.Count / 12) == 0).ToList();
        Console.WriteLine();
        Console.WriteLine("  start        | way | pts  doorway | length yd | turns>60 >120 | in big rooms: yd/visit pts/visit | off-floor legs");
        NavRouteParser firstOld = null, firstNew = null;
        foreach (uint s in starts)
        {
            var oldR = DungeonPathfinder.BuildPatrolRoute(graph, s, null);
            var newR = DungeonPathfinder.BuildPatrolRoute(graph, s, null, map);
            firstOld ??= oldR; firstNew ??= newR;
            Report($"0x{s:X8}", "old", oldR, floorPolys, cellOfBig);
            Report($"0x{s:X8}", "new", newR, floorPolys, cellOfBig);
        }

        // Dungeon paths (/ra dunnav, recovery detours) between a spread of cell pairs.
        Console.WriteLine();
        Console.WriteLine("  dunnav paths (cell centre to cell centre), totals over the pairs:");
        var rnd = new Random(6346);
        var ids = graph.Keys.OrderBy(k => k).ToList();
        int pairs = 0;
        var tot = new Dictionary<string, (int Pts, int Door, double Len, int T60, int Off)>();
        for (int k = 0; k < 40; k++)
        {
            uint a = ids[rnd.Next(ids.Count)], b = ids[rnd.Next(ids.Count)];
            var path = DungeonPathfinder.FindPath(a, b, graph);
            if (path.Count < 3) continue;
            pairs++;
            var na = graph[a]; var nb = graph[b];
            foreach (var (way, g2) in new[] { ("old", (DungeonGeometry)null), ("new", map) })
            {
                var r = DungeonPathfinder.BuildNavRoute(path, graph, nb.NS, nb.EW, g2, null, na.NS, na.EW, na.Z, nb.Z);
                r.Points.Insert(0, new NavPoint { NS = na.NS, EW = na.EW, Z = na.Z / 240.0 });
                int t60 = 0, off = 0; double len = 0;
                for (int i = 0; i + 1 < r.Points.Count; i++)
                {
                    len += Yd(r.Points[i], r.Points[i + 1]);
                    if (i + 2 < r.Points.Count && TurnDeg(r.Points[i], r.Points[i + 1], r.Points[i + 2]) > 60) t60++;
                    if (!LegOnFloor(r.Points[i], r.Points[i + 1], floorPolys)) off++;
                }
                tot.TryGetValue(way, out var t);
                tot[way] = (t.Pts + r.Points.Count, t.Door + r.Points.Count(p => p.Doorway), t.Len + len, t.T60 + t60, t.Off + off);
            }
        }
        foreach (var kv in tot)
            Console.WriteLine($"    {kv.Key}: {pairs} paths, {kv.Value.Pts} points ({kv.Value.Door} doorway), {kv.Value.Len:F0} yd, {kv.Value.T60} turns > 60, {kv.Value.Off} off-floor legs");

        if (dumpDir != null)
        {
            Directory.CreateDirectory(dumpDir);
            Dump(Path.Combine(dumpDir, "old.csv"), firstOld);
            Dump(Path.Combine(dumpDir, "new.csv"), firstNew);
            using var w = new StreamWriter(Path.Combine(dumpDir, "floors.csv"));
            w.WriteLine("cell,room,big,x,y");   // one line per polygon vertex, polygons separated by cell+index
            int pi = 0;
            foreach (var f in floorPolys)
            {
                int room = rooms.RoomOf.TryGetValue(f.CellId, out int rr) ? rr : -1;
                bool big = room >= 0 && rooms.Cells[room].Count >= 3;
                foreach (var v in f.Vertices) w.WriteLine($"{pi},{room},{(big ? 1 : 0)},{v.X.ToString(CultureInfo.InvariantCulture)},{v.Y.ToString(CultureInfo.InvariantCulture)}");
                pi++;
            }
            Console.WriteLine($"\n  wrote old.csv, new.csv, floors.csv to {dumpDir}");
        }
        return 0;
    }

    private static void Report(string start, string way, NavRouteParser r, List<DungeonLOS.MapPolygon> floors, Dictionary<uint, int> cellOfBig)
    {
        int n = r.Points.Count;
        int doorway = r.Points.Count(p => p.Doorway);
        double len = 0;
        int t60 = 0, t120 = 0, offFloor = 0;
        // Which big room each point is in (by floor polygon), -1 = none.
        var roomAt = r.Points.Select(p => BigRoomAt(p, floors, cellOfBig)).ToArray();
        var legs = new List<string>();
        double inRoomLen = 0;
        int visits = 0, inRoomPts = 0;
        for (int i = 0; i < n; i++)
        {
            var a = r.Points[i]; var b = r.Points[(i + 1) % n];
            double leg = Yd(a, b);
            len += leg;
            if (n > 2)
            {
                var c = r.Points[(i + 2) % n];
                double turn = TurnDeg(a, b, c);
                if (turn > 60) t60++;
                if (turn > 120) t120++;
            }
            if (!LegOnFloor(a, b, floors))
            {
                offFloor++;
                if (Verbose) legs.Add($"      off-floor leg [{i}] ({DungeonGeometry.NavToWorld(a.EW):F1},{DungeonGeometry.NavToWorld(a.NS):F1},{a.Z * 240:F1}) -> ({DungeonGeometry.NavToWorld(b.EW):F1},{DungeonGeometry.NavToWorld(b.NS):F1},{b.Z * 240:F1})");
            }
            int ra = roomAt[i], rb = roomAt[(i + 1) % n];
            if (ra >= 0 && ra == rb) inRoomLen += leg;
            if (ra >= 0) inRoomPts++;
            if (rb >= 0 && rb != ra) visits++;
        }
        string inRoom = visits > 0 ? $"{inRoomLen / visits,6:F1} {(double)inRoomPts / visits,6:F1}" : "     -      -";
        Console.WriteLine($"  {start} | {way} | {n,4} {doorway,6} | {len,9:F0} | {t60,7} {t120,5} | {inRoom,28} | {offFloor,5}");
        foreach (var l in legs) Console.WriteLine(l);
    }

    private static bool Verbose;

    private static int BigRoomAt(NavPoint p, List<DungeonLOS.MapPolygon> floors, Dictionary<uint, int> cellOfBig)
    {
        double x = DungeonGeometry.NavToWorld(p.EW), y = DungeonGeometry.NavToWorld(p.NS), z = p.Z * 240.0;
        foreach (var f in floors)
        {
            if (!cellOfBig.TryGetValue(f.CellId, out int r)) continue;
            if (!DungeonGeometry.InsidePolygon2D(f.Vertices, x, y)) continue;
            double fz = f.Vertices.Average(v => v.Z);
            if (z != 0 && (z - fz < -1 || z - fz > 4)) continue;
            return r;
        }
        return -1;
    }

    // Every half metre of the leg is over some floor polygon (any cell) near the legs' height.
    private static bool LegOnFloor(NavPoint a, NavPoint b, List<DungeonLOS.MapPolygon> floors)
    {
        double ax = DungeonGeometry.NavToWorld(a.EW), ay = DungeonGeometry.NavToWorld(a.NS);
        double bx = DungeonGeometry.NavToWorld(b.EW), by = DungeonGeometry.NavToWorld(b.NS);
        double za = a.Z * 240.0, zb = b.Z * 240.0;
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

    private static double Yd(NavPoint a, NavPoint b) => Math.Sqrt((a.NS - b.NS) * (a.NS - b.NS) + (a.EW - b.EW) * (a.EW - b.EW)) * 240.0;

    private static double TurnDeg(NavPoint a, NavPoint b, NavPoint c)
    {
        double ux = b.EW - a.EW, uy = b.NS - a.NS, vx = c.EW - b.EW, vy = c.NS - b.NS;
        double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
        if (lu < 1e-9 || lv < 1e-9) return 0;
        double cos = Math.Clamp((ux * vx + uy * vy) / (lu * lv), -1, 1);
        return Math.Acos(cos) * 180 / Math.PI;
    }

    private static void Dump(string path, NavRouteParser r)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("i,x,y,z,doorway");
        for (int i = 0; i < r.Points.Count; i++)
        {
            var p = r.Points[i];
            w.WriteLine(string.Join(",", i,
                DungeonGeometry.NavToWorld(p.EW).ToString("F2", CultureInfo.InvariantCulture),
                DungeonGeometry.NavToWorld(p.NS).ToString("F2", CultureInfo.InvariantCulture),
                (p.Z * 240).ToString("F2", CultureInfo.InvariantCulture), p.Doorway ? 1 : 0));
        }
    }
}

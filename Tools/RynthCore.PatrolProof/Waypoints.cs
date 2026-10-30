using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.PatrolProof;

// "waypoints": the patrol the plugin builds from given start cells, one line per waypoint with
// the rule that laid it (DungeonPathfinder.DescribePatrol, the same lines BuildDunPatrol logs as
// "[RynthAi] DunNav-Patrol wp ..."), and the hazard set the game would use:
//   dotnet run -c Release -- 6146 waypoints 61460100 61460325 [surface] [hzfile <file>] [hz 012E,0131] [summary] [tiny]
//     surface      add the Detector C cells (floor textures in HazardTextures.txt, as the game seeds them)
//     hzfile f     add the cells listed in a DungeonHazards/<lb>.txt file
//     hz a,b,...   add cells (low 16 bits hex, or full ids)
//     compare      for each start, the summary with none / surface / file+hz / all hazards
//     summary      summary lines only        tiny   only waypoints in rooms of <= 5 cells
//   The patrol settings (Settings > Navigation > Dungeon Patrol; DungeonPatrolOptions), default
//   as in the game:
//     coverage whole|loops|all|sweep    sight M (5-60, sweep's sight distance)    spurs N (0-30: dead-end areas of <= N cells skipped)    floors on|off    walls M (0.5-2)    lookin M (3-40)
//     floors off keeps the surface cells out of the hazards unless hzfile/hz also list them, as
//     BuildDunPatrol does with the cells in DungeonHazards/<lb>.txt.
//     modes        for each start, one line per setting variant (coverage modes, dead-end limits,
//                  floors off, wall distances, look-in sizes): patrol cells, waypoints, lap length
//     pockets      for each start, the dead-end areas off the reachable cells (largest first, the
//                  ones hanging off the trunk), their rooms and the smaller areas inside them
//     portal x,y,z[,r]  a portal the patrol keeps out of (world metres; r the keep-out radius, default
//                  3.5 m), as BuildDunPatrol passes the live ones; repeatable. One line per portal.
// The start cell is handled as BuildDunPatrol does: a start in a hazard moves to NearestSafeCell.
internal static class Waypoints
{
    public static int Run(string[] args)
    {
        uint lb = 0x6146;
        string acDir = @"C:\Turbine\Asheron's Call";
        var starts = new List<uint>();
        bool surface = false, summaryOnly = false, tinyOnly = false, compare = false, modes = false, pocketList = false;
        var opt = DungeonPatrolOptions.Default;
        var inv = CultureInfo.InvariantCulture;
        var extra = new HashSet<uint>();
        var fileCells = new HashSet<uint>();
        var portals = new List<PatrolPortal>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "waypoints") continue;
            if (a == "surface") surface = true;
            else if (a == "summary") summaryOnly = true;
            else if (a == "tiny") tinyOnly = true;
            else if (a == "compare") compare = true;
            else if (a == "modes") modes = true;
            else if (a == "pockets") pocketList = true;
            else if (a == "coverage" && i + 1 < args.Length)
                opt = opt with { Coverage = args[++i] switch { "loops" => PatrolCoverage.LoopsOnly, "all" or "everything" => PatrolCoverage.Everything, "sweep" => PatrolCoverage.Sweep, _ => PatrolCoverage.WholeDungeon } };
            else if (a == "sight" && i + 1 < args.Length) opt = opt with { SightMeters = double.Parse(args[++i], inv) };
            else if (a == "spurs" && i + 1 < args.Length) opt = opt with { MaxDeadEndCells = int.Parse(args[++i], inv) };
            else if (a == "floors" && i + 1 < args.Length) opt = opt with { AvoidHazardFloors = args[++i] != "off" };
            else if (a == "walls" && i + 1 < args.Length) opt = opt with { WallClearance = double.Parse(args[++i], inv) };
            else if (a == "lookin" && i + 1 < args.Length) opt = opt with { LookInRoomMeters = double.Parse(args[++i], inv) };
            else if (a == "hzfile" && i + 1 < args.Length)
            {
                foreach (string raw in File.ReadAllLines(args[++i]))
                {
                    string l = raw.Trim();
                    if (l.Length == 0 || l[0] == '#') continue;
                    if (uint.TryParse(l.Replace("0x", ""), NumberStyles.HexNumber, null, out uint c)) fileCells.Add(c);
                }
            }
            else if (a == "portal" && i + 1 < args.Length)
            {
                double[] v = args[++i].Split(',').Select(t => double.Parse(t, inv)).ToArray();
                portals.Add(new PatrolPortal { Id = (uint)portals.Count + 1, Name = "given", X = v[0], Y = v[1], Z = v[2],
                                               Radius = v.Length > 3 ? v[3] : DungeonPathfinder.PortalKeepOutDefault });
            }
            else if (a == "hz" && i + 1 < args.Length)
                foreach (string t in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    extra.Add(uint.Parse(t.Replace("0x", ""), NumberStyles.HexNumber));
            else if (a.Length <= 6 && uint.TryParse(a.Replace("0x", ""), NumberStyles.HexNumber, null, out uint v)) lb = v;
            else if (a.Length == 8 && uint.TryParse(a, NumberStyles.HexNumber, null, out uint s)) starts.Add(s);
            else acDir = a;
        }
        // Low-16 ids become full cell ids of this landblock.
        extra = extra.Select(c => c <= 0xFFFF ? (lb << 16) | c : c).ToHashSet();

        var loader = new GeometryLoader();
        if (!loader.Initialize(acDir)) { Console.WriteLine("Geometry loader failed: " + loader.StatusMessage); return 1; }
        var graph = DungeonPathfinder.GetGraph(lb, loader.CellDat);
        var geo = DungeonPathfinder.GetGeometry(lb, loader.CellDat, loader.DungeonLOS);
        var surfaceCells = DungeonHazardSurfaces.ComputeHazardCells(lb, loader.DungeonLOS, loader.PortalDat);
        Console.WriteLine($"Landblock 0x{lb:X4}: {graph.Count} cells; hazard textures {DungeonHazardSurfaces.HazardTextureCount} -> {surfaceCells.Count} Detector C cells: {Ids(surfaceCells)}");
        if (fileCells.Count > 0) Console.WriteLine($"  hzfile cells: {Ids(fileCells)}");
        if (extra.Count > 0) Console.WriteLine($"  hz cells: {Ids(extra)}");
        if (starts.Count == 0) starts.Add(graph.Keys.Min());
        opt = opt.Clamped();
        Console.WriteLine("Patrol settings: " + opt.Describe());
        if (pocketList) return Pockets(graph, geo, starts, surface ? surfaceCells : new HashSet<uint>(), new HashSet<uint>(fileCells.Concat(extra)), opt);
        if (modes) return Modes(graph, geo, starts, surface ? surfaceCells : new HashSet<uint>(), new HashSet<uint>(fileCells.Concat(extra)), opt);

        var sets = new List<(string Name, HashSet<uint> Cells)>();
        var given = new HashSet<uint>(fileCells.Concat(extra));
        if (compare)
        {
            sets.Add(("none", new HashSet<uint>()));
            sets.Add(("surface", new HashSet<uint>(surfaceCells)));
            if (given.Count > 0) sets.Add(("file+hz", new HashSet<uint>(given)));
            if (given.Count > 0) sets.Add(("all", new HashSet<uint>(surfaceCells.Concat(given))));
        }
        else
        {
            var cells = new HashSet<uint>(given);
            if (surface) cells.UnionWith(surfaceCells);
            sets.Add((surface ? (given.Count > 0 ? "all" : "surface") : given.Count > 0 ? "file+hz" : "none", cells));
        }

        foreach (uint s0 in starts)
            foreach (var (name, cells0) in sets)
            {
                uint start = s0;
                var cells = new HashSet<uint>(DungeonPathfinder.PatrolHazards(cells0, surfaceCells, given, opt) ?? cells0);
                IReadOnlySet<uint> hazards = cells.Count > 0 ? cells : null;
                if (hazards != null && hazards.Contains(start))
                {
                    uint safe = DungeonPathfinder.NearestSafeCell(graph, start, hazards);
                    if (safe != 0) start = safe;
                }
                var log = new Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo>(ReferenceEqualityComparer.Instance);
                DungeonPathfinder.PointLog = log;
                NavRouteParser route;
                List<PortalAvoidance> avoided;
                try
                {
                    route = DungeonPathfinder.BuildPatrolRoute(graph, start, hazards, geo, opt, portals);
                    avoided = DungeonPathfinder.LastPortalAvoidance;
                }
                finally { DungeonPathfinder.PointLog = null; }
                var lines = DungeonPathfinder.DescribePatrol(route, graph, geo, hazards, log);
                if (avoided != null) lines.AddRange(avoided.Select(r => "wp " + r.Describe()));
                Console.WriteLine();
                Console.WriteLine($"== start {s0:X8}{(start != s0 ? $" (moved to safe {start:X8})" : "")}, hazards {name} ({cells.Count})");
                Console.WriteLine("   " + EnteredRooms(route, graph, geo, hazards, log));
                foreach (string l in lines)
                {
                    bool isSummary = l.StartsWith("wp summary", StringComparison.Ordinal) || l.StartsWith("wp main route", StringComparison.Ordinal)
                                  || l.StartsWith("wp portal", StringComparison.Ordinal);
                    if (summaryOnly && !isSummary) continue;
                    if (tinyOnly && !isSummary && !IsTiny(l)) continue;
                    Console.WriteLine("   " + l);
                }
            }
        return 0;
    }

    // One line per setting variant: what each Settings > Dungeon Patrol choice does to the patrol.
    //   cells     patrol cells (GetMainRouteNodes)        rooms<=5   small rooms (<= 5 cells) entered
    //   off-loop  of those, rooms with no cell on the loops (the hazard-aware 2-core): dead-end rooms
    //   skipped   reachable rooms left out whole, and the biggest of them (cells, metres across)
    private static int Modes(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, List<uint> starts,
                             HashSet<uint> surfaceCells, HashSet<uint> given, DungeonPatrolOptions baseOpt)
    {
        var d = baseOpt;
        var variants = new List<(string Name, DungeonPatrolOptions O)>
        {
            ($"whole dungeon, dead ends {d.MaxDeadEndCells} (given)", d),
        };
        foreach (int n in new[] { 0, 3, 6, 8, 10, 12, 15, 20 })
            if (n != d.MaxDeadEndCells) variants.Add(($"whole dungeon, dead ends {n}", d with { MaxDeadEndCells = n }));
        variants.Add(("loops only", d with { Coverage = PatrolCoverage.LoopsOnly }));
        variants.Add(("everything", d with { Coverage = PatrolCoverage.Everything }));
        variants.Add(("lava/acid floors walked", d with { AvoidHazardFloors = false }));
        variants.Add(("walls 0.5 m", d with { WallClearance = 0.5 }));
        variants.Add(("look into rooms >= 20 m", d with { LookInRoomMeters = 20 }));
        var all = new HashSet<uint>(surfaceCells.Concat(given));
        foreach (uint s0 in starts)
        {
            Console.WriteLine();
            Console.WriteLine($"== start {s0:X8}, hazards {all.Count} ({surfaceCells.Count} floor, {given.Count} given)");
            Console.WriteLine("   setting                            | hazards | cells | waypoints (doorway) | lap yd | look-ins | rooms<=5 (off-loop) | skipped rooms (biggest)");
            foreach (var (name, o) in variants)
            {
                var hz = DungeonPathfinder.PatrolHazards(all, surfaceCells, given, o);
                IReadOnlySet<uint> hazards = hz is { Count: > 0 } ? hz : null;
                uint start = s0;
                if (hazards != null && hazards.Contains(start))
                {
                    uint safe = DungeonPathfinder.NearestSafeCell(graph, start, hazards);
                    if (safe != 0) start = safe;
                }
                var rooms = DungeonPathfinder.BuildRooms(graph, geo, hazards);
                var cellSet = DungeonPathfinder.GetMainRouteNodes(graph, start, hazards, o, geo.DoorwayCount > 0 ? rooms : null);
                var everything = DungeonPathfinder.GetMainRouteNodes(graph, start, hazards, o with { Coverage = PatrolCoverage.Everything });
                var loops = DungeonPathfinder.GetMainRouteNodes(graph, start, hazards, o with { Coverage = PatrolCoverage.LoopsOnly });
                var log = new Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo>(ReferenceEqualityComparer.Instance);
                DungeonPathfinder.PointLog = log;
                NavRouteParser route;
                try { route = DungeonPathfinder.BuildPatrolRoute(graph, start, hazards, geo, o); }
                finally { DungeonPathfinder.PointLog = null; }
                int n = route.Points.Count, door = route.Points.Count(p => p.Doorway);
                double len = 0;
                for (int i = 0; i < n; i++)
                {
                    var a = route.Points[i]; var b = route.Points[(i + 1) % n];
                    len += Math.Sqrt((a.NS - b.NS) * (a.NS - b.NS) + (a.EW - b.EW) * (a.EW - b.EW)) * 240.0;
                }
                int lookIns = log.Values.Where(v => v.Why.StartsWith("look-in", StringComparison.Ordinal)).Select(v => v.Room).Distinct().Count();
                var entered = Entered(route, graph, geo, rooms, log);
                var small = entered.Where(r => rooms.Cells[r].Count <= 5).ToList();
                int offLoop = small.Count(r => !rooms.Cells[r].Any(loops.Contains));
                var skipped = Enumerable.Range(0, rooms.Cells.Count)
                    .Where(r => rooms.Cells[r].Any(everything.Contains) && !rooms.Cells[r].Any(cellSet.Contains)).ToList();
                string biggest = skipped.Count == 0 ? "-" : skipped.Select(r => (Cells: rooms.Cells[r].Count, M: Across(graph, rooms.Cells[r])))
                    .OrderByDescending(t => t.Cells).ThenByDescending(t => t.M).Select(t => $"{t.Cells}c {t.M:F0}m").First();
                Console.WriteLine($"   {name,-34} | {hazards?.Count ?? 0,7} | {cellSet.Count,5} | {n,9} ({door,5})   | {len,6:F0} | {lookIns,8} | {small.Count,8} ({offLoop,7})  | {skipped.Count,5} ({biggest})");
            }
        }
        return 0;
    }

    // The dead-end areas (DungeonPathfinder.FindDeadEndPockets) of the reachable rooms, as the
    // patrol finds them: those hanging off the loops (rooms with a 2-core cell), largest first, with
    // their rooms and the sizes of the pockets nested in them; then how many pockets of each size.
    private static int Pockets(Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo, List<uint> starts,
                               HashSet<uint> surfaceCells, HashSet<uint> given, DungeonPatrolOptions opt)
    {
        var all = new HashSet<uint>(surfaceCells.Concat(given));
        foreach (uint s0 in starts)
        {
            var hz = DungeonPathfinder.PatrolHazards(all, surfaceCells, given, opt);
            IReadOnlySet<uint> hazards = hz is { Count: > 0 } ? hz : null;
            uint start = s0;
            if (hazards != null && hazards.Contains(start))
            {
                uint safe = DungeonPathfinder.NearestSafeCell(graph, start, hazards);
                if (safe != 0) start = safe;
            }
            var reach = DungeonPathfinder.GetMainRouteNodes(graph, start, hazards, opt with { Coverage = PatrolCoverage.Everything });
            var rooms = DungeonPathfinder.BuildRooms(graph, geo, hazards);
            var (adj, weight) = DungeonPathfinder.RoomAdjacency(graph, reach, rooms);
            // Weighted as the patrol weighs them: a big room (look-in size) as 1000.
            bool Big(uint r) => DungeonPathfinder.RoomSpreadMeters(graph, rooms.Cells[(int)r]) >= opt.LookInRoomMeters;
            var p = DungeonPathfinder.FindDeadEndPockets(adj, r => Big(r) ? 1000 : weight[r], (uint)rooms.RoomOf[start]);
            // The trunk: the loops (2-core). Pockets off it are the branches; a pocket the start is
            // in turns the rest of the dungeon into one big pocket, so the trunk isn't "no pocket".
            var core = DungeonPathfinder.GetMainRouteNodes(graph, start, hazards, opt with { Coverage = PatrolCoverage.LoopsOnly })
                .Select(c => (uint)rooms.RoomOf[c]).ToHashSet();
            var top = p.All.Where(q => core.Contains(q.Attach) && !core.Contains(q.Root)).OrderByDescending(q => q.Size).ToList();
            string Room(uint r) => (rooms.Cells[(int)r][0] & 0xFFFF).ToString("X4");
            Console.WriteLine();
            Console.WriteLine($"== start {s0:X8}: {reach.Count} reachable cells in {adj.Count} rooms, {core.Count} rooms on the loops, "
                            + $"{p.All.Count} pockets ({top.Count} off the loops)");
            foreach (var q in top)
            {
                var inside = p.NodesOf(q).ToHashSet();
                var rs = inside.Select(r => Big(r) ? $"{weight[r]}big" : $"{weight[r]}").GroupBy(n => n).OrderByDescending(g => g.Key.Length).ThenByDescending(g => g.Key)
                              .Select(g => g.Count() == 1 ? g.Key : $"{g.Key}x{g.Count()}");
                var nested = p.All.Where(n => n.Root != q.Root && inside.Contains(n.Root)).Select(n => n.Size).OrderByDescending(n => n).Take(12);
                Console.WriteLine($"   off room {Room(q.Attach)} at {Room(q.Root)}: {q.Size,3} cells, rooms [{string.Join(",", rs)}], nested [{string.Join(",", nested)}]");
            }
            var bins = new[] { 1, 2, 3, 5, 8, 10, 12, 15, 20, 30, 50, int.MaxValue };
            int lo = 1;
            var hist = new List<string>();
            foreach (int b in bins)
            {
                int c = p.All.Count(q => q.Size >= lo && q.Size <= b);
                hist.Add($"{lo}{(b == lo ? "" : b == int.MaxValue ? "+" : "-" + b)}: {c}");
                lo = b + 1;
            }
            Console.WriteLine("   all pockets by size: " + string.Join(", ", hist));
        }
        return 0;
    }

    // Rooms with at least one waypoint not laid by a look-in.
    private static HashSet<int> Entered(NavRouteParser route, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo,
                                        DungeonRooms rooms, Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo> log)
    {
        var entered = new HashSet<int>();
        foreach (var p in route.Points)
        {
            log.TryGetValue(p, out var info);
            uint cell = info != null && info.Cell != 0 ? info.Cell : DungeonPathfinder.CellAt(graph, geo, p);
            if (!rooms.RoomOf.TryGetValue(cell, out int r)) continue;
            if (info != null && info.Why.StartsWith("look-in", StringComparison.Ordinal) && info.Room == r) continue;
            entered.Add(r);
        }
        return entered;
    }

    // Metres across a room's cell centres.
    private static double Across(Dictionary<uint, DungeonNavNode> graph, List<uint> cells)
    {
        double best = 0;
        foreach (uint a in cells)
            foreach (uint b in cells)
            {
                var x = graph[a]; var y = graph[b];
                best = Math.Max(best, Math.Sqrt((x.NS - y.NS) * (x.NS - y.NS) + (x.EW - y.EW) * (x.EW - y.EW)) * 240.0);
            }
        return best;
    }

    // "room N (k cells)" with k <= 5.
    private static bool IsTiny(string line)
    {
        int at = line.IndexOf(" room ", StringComparison.Ordinal);
        if (at < 0) return false;
        int open = line.IndexOf('(', at), sp = line.IndexOf(" cells)", at, StringComparison.Ordinal);
        return open > 0 && sp > open && int.TryParse(line.AsSpan(open + 1, sp - open - 1), out int n) && n > 0 && n <= 5;
    }

    // Rooms with at least one waypoint not laid by a look-in, by size.
    private static string EnteredRooms(NavRouteParser route, Dictionary<uint, DungeonNavNode> graph, DungeonGeometry geo,
                                       IReadOnlySet<uint> hazards, Dictionary<NavPoint, DungeonPathfinder.PatrolPointInfo> log)
    {
        var rooms = DungeonPathfinder.BuildRooms(graph, geo, hazards);
        var entered = new HashSet<int>();
        var looked = new HashSet<int>();
        foreach (var p in route.Points)
        {
            log.TryGetValue(p, out var info);
            uint cell = info != null && info.Cell != 0 ? info.Cell : DungeonPathfinder.CellAt(graph, geo, p);
            if (!rooms.RoomOf.TryGetValue(cell, out int r)) continue;
            if (info != null && info.Why.StartsWith("look-in", StringComparison.Ordinal) && info.Room == r) looked.Add(r);
            else entered.Add(r);
        }
        int tiny = entered.Count(r => rooms.Cells[r].Count <= 5);
        var tinyList = entered.Where(r => rooms.Cells[r].Count <= 5).OrderBy(r => r)
            .Select(r => $"{r}[{string.Join(",", rooms.Cells[r].Select(c => (c & 0xFFFF).ToString("X4")))}]");
        return $"rooms entered {entered.Count} (of {rooms.Cells.Count}), of them <= 5 cells: {tiny}: {string.Join(" ", tinyList)}; looked into {looked.Count}";
    }

    private static string Ids(IEnumerable<uint> cells) => string.Join(",", cells.OrderBy(c => c).Select(c => (c & 0xFFFF).ToString("X4")));
}

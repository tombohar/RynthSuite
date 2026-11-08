using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

// The route maker on the navmesh (LoopMaker, TilePathFinder's route maker options), on floors baked here
// and on the real map when it's installed (read only): a loop's points (walkable, out of buildings, away
// from portals, reachable, in an order whose legs don't cross), its legs (round a pond, never through a
// portal's circle, round a building's rooms, off walls), what is dropped and why, and a path through
// given points (each leg walked; a point or a leg that can't be, named).
internal static partial class Program
{
    private static void RunRouteMaker()
    {
        Run(nameof(LoopOnOpenGround), LoopOnOpenGround);
        Run(nameof(LoopLegsGoRoundAPond), LoopLegsGoRoundAPond);
        Run(nameof(LoopDropsWhatItCantReach), LoopDropsWhatItCantReach);
        Run(nameof(LegsNeverThroughAPortal), LegsNeverThroughAPortal);
        Run(nameof(LegsRoundABuildingAndOffWalls), LegsRoundABuildingAndOffWalls);
        Run(nameof(PathThroughGivenPoints), PathThroughGivenPoints);
        Run(nameof(RealLoopsNearHoltburg), RealLoopsNearHoltburg);
    }

    private static double MinGap(IReadOnlyList<WorldPt> path, double x, double y)
    {
        double best = double.MaxValue;
        for (int i = 1; i < path.Count; i++) best = Math.Min(best, PortalAvoid.SegmentDistance(x, y, path[i - 1].X, path[i - 1].Y, path[i].X, path[i].Y));
        return best;
    }

    /// <summary>Does any stretch of the path pass through the box (sampled every half yard)?</summary>
    private static bool Enters(IReadOnlyList<WorldPt> path, double x0, double y0, double x1, double y1)
    {
        for (int i = 1; i < path.Count; i++)
        {
            WorldPt a = path[i - 1], b = path[i];
            int n = Math.Max(1, (int)(a.FlatDistance(b) * 2));
            for (int k = 0; k <= n; k++)
            {
                double x = a.X + (b.X - a.X) * k / n, y = a.Y + (b.Y - a.Y) * k / n;
                if (x > x0 && x < x1 && y > y0 && y < y1) return true;
            }
        }
        return false;
    }

    private static void CheckLoopRules(LoopMaker.Result r, TilePathFinder mesh, BuildingOutlines? buildings, IReadOnlyList<PortalAvoid.Spot> portals, string what)
    {
        Check(r.Ok && r.Route != null, $"{what}: a loop ({r.Error})");
        if (!r.Ok || r.Route == null) return;
        Check(r.Points.Count >= 3 && r.Legs.Count == r.Points.Count, $"{what}: {r.Points.Count} points, a leg from each");
        foreach (WorldPt p in r.Points)
        {
            Check(mesh.Snap(p, 1.0, out _, out _), $"{what}: point {NavCoords.Fmt(p.Ns, p.Ew)} is on walkable ground");
            if (buildings != null) Check(buildings.Inside(p.X, p.Y, 2.9) < 0, $"{what}: point {NavCoords.Fmt(p.Ns, p.Ew)} is out of every building");
            foreach (PortalAvoid.Spot s in portals)
                Check(Math.Sqrt((p.X - s.X) * (p.X - s.X) + (p.Y - s.Y) * (p.Y - s.Y)) >= 20 - 0.01, $"{what}: point 20 yd or more from {s.Name}");
        }
        Check(RouteShape.Crossings(r.Points, closed: true) == 0, $"{what}: walked in an order where no two legs cross");
        for (int i = 0; i < r.Legs.Count; i++)
        {
            List<WorldPt> leg = r.Legs[i];
            Check(leg[0].FlatDistance(r.Points[i]) < 1 && leg[^1].FlatDistance(r.Points[(i + 1) % r.Points.Count]) < 2.5, $"{what}: leg {i + 1} joins its two points");
            foreach (PortalAvoid.Spot s in portals)
                Check(MinGap(leg, s.X, s.Y) >= s.R - 0.06, $"{what}: leg {i + 1} keeps out of {s.Name}'s circle ({MinGap(leg, s.X, s.Y):F2} yd)");
        }
        Check(r.Route.RouteType == NavRouteType.Circular && r.Route.Points.Count >= r.Points.Count, $"{what}: a circular nav, a waypoint at least per point");
        Check(Math.Abs(RouteInfo.Length(r.Route) - r.Yards) < 2, $"{what}: its length is the nav file's ({r.Yards:F0} yd)");
        NavRouteParser back = NavRouteParser.LoadFromLines(r.Route.ToLines());
        Check(back.LoadWarning == null && back.Points.Count == r.Route.Points.Count && back.RouteType == NavRouteType.Circular, $"{what}: RynthAi reads it back");
    }

    private static void LoopOnOpenGround()
    {
        // 3 x 3 tiles of open ground; a building's outline where the north point would go, a portal where the east one would.
        string dir = Bake("loopopen", (0, 0, 576, 576));
        var centre = new WorldPt(288, 288, double.NaN);
        var buildings = BuildingOutlines.FromPolygons(new[] { new (double, double)[] { (270, 350), (306, 350), (306, 386), (270, 386) } });
        var portals = new List<PortalAvoid.Spot> { new(368, 288, PortalAvoid.MapRadius, "Unwanted Portal") };
        var mesh = new TilePathFinder(dir, null, portals);
        var opt = new LoopMaker.LoopOptions { Radius = 80, Points = 8, WallClearance = 1.0 };
        LoopMaker.Result r = LoopMaker.Loop(mesh, centre, opt, buildings, portals);
        CheckLoopRules(r, mesh, buildings, portals, "open ground");
        Check(r.Points.Count == 8 && r.DroppedPoints.Count == 0, $"every point placed ({r.Points.Count}, {r.DroppedPoints.Count} dropped)");
        Check(r.Points.All(p => Math.Abs(p.FlatDistance(r.Centre) - 80) < 45), "the points round the circle");
        Check(r.Yards > 2 * Math.PI * 80 * 0.6 && r.Yards < 2 * Math.PI * 80 * 1.3, $"a lap of about the circle ({r.Yards:F0} yd)");
        Console.WriteLine($"     open ground: {r.Points.Count} points, {r.Route?.Points.Count} waypoints, {r.Yards:F0} yd a lap, {mesh.TilesRead} tiles");

        // Default point count, and the order starts north and goes clockwise.
        var r2 = LoopMaker.Loop(new TilePathFinder(dir, null, null), centre, new LoopMaker.LoopOptions { Radius = 40 }, null, Array.Empty<PortalAvoid.Spot>());
        Check(r2.Ok && r2.Asked == RouteShape.DefaultPoints(40), $"no point count: {RouteShape.DefaultPoints(40)} by default ({r2.Asked})");
        Check(r2.Ok && RouteShape.Bearing(r2.Centre, r2.Points[0]) < 30 && RouteShape.Bearing(r2.Centre, r2.Points[1]) > RouteShape.Bearing(r2.Centre, r2.Points[0]), "north first, clockwise");
        Check(r2.Notes.Any(n => n.Contains("worldmap.vec")), "without building outlines it says so");

        // No ground at the centre: refused, saying where.
        var none = LoopMaker.Loop(new TilePathFinder(dir, null, null), new WorldPt(5000, 5000, double.NaN), opt, null, Array.Empty<PortalAvoid.Spot>());
        Check(!none.Ok && none.Error.Contains("navmesh"), "no tiles at the centre: " + none.Error);
    }

    private static void LoopLegsGoRoundAPond()
    {
        // Open ground with a pond (x 300..330, y 340..365) right on the straight leg from the north point to the north-east one.
        string dir = Bake("looppond", (0, 0, 576, 340), (0, 340, 300, 576), (330, 340, 576, 576), (300, 365, 330, 576));
        var mesh = new TilePathFinder(dir, null, null);
        LoopMaker.Result r = LoopMaker.Loop(mesh, new WorldPt(288, 288, double.NaN), new LoopMaker.LoopOptions { Radius = 80, Points = 8 }, null, Array.Empty<PortalAvoid.Spot>());
        CheckLoopRules(r, mesh, null, Array.Empty<PortalAvoid.Spot>(), "pond");
        bool anyNear = false;
        foreach (List<WorldPt> leg in r.Legs)
        {
            Check(!Enters(leg, 300.5, 340.5, 329.5, 364.5), "no leg walks through the pond");
            anyNear |= Enters(leg, 296, 336, 334, 369);
        }
        Check(anyNear, "a leg goes round its edge (it was in the way)");
        Check(!Enters(r.Route!.Points.Select(p => WorldPt.FromMap(p.NS, p.EW)).ToList(), 300.5, 340.5, 329.5, 364.5), "nor does the saved route");
    }

    private static void LoopDropsWhatItCantReach()
    {
        // The main ground ends at x = 375; past 20 yd of water (to 395) an island no walk reaches.
        string dir = Bake("loopisland", (0, 0, 375, 576), (395, 0, 576, 576));
        var mesh = new TilePathFinder(dir, null, null);
        LoopMaker.Result r = LoopMaker.Loop(mesh, new WorldPt(340, 288, double.NaN), new LoopMaker.LoopOptions { Radius = 100, Points = 8 }, null, Array.Empty<PortalAvoid.Spot>());
        CheckLoopRules(r, mesh, null, Array.Empty<PortalAvoid.Spot>(), "island");
        Check(r.Points.All(p => p.X < 376), "every point on the main ground");
        Check(r.DroppedPoints.Count >= 1 && r.DroppedPoints.Any(d => d.Why.Contains("not reachable")), $"the east is dropped: {string.Join("; ", r.DroppedPoints.Select(d => d.Why))}");
        Check(r.Points.Count + r.DroppedPoints.Count == 8, "kept + dropped = asked");

        // Too little ground for a loop: refused, with why.
        string tiny = Bake("looptiny", (100, 100, 112, 112));
        var t = LoopMaker.Loop(new TilePathFinder(tiny, null, null), new WorldPt(106, 106, double.NaN), new LoopMaker.LoopOptions { Radius = 60, Points = 6 }, null, Array.Empty<PortalAvoid.Spot>());
        Check(!t.Ok && (t.Error.Contains("could be placed") || t.Error.Contains("ground")), "a speck of ground: no loop: " + t.Error);
    }

    private static void LegsNeverThroughAPortal()
    {
        // Two rooms joined only by a corridor 12 yd wide with a portal in the middle of it.
        string dir = Bake("portalcorridor", (0, 0, 150, 192), (150, 90, 250, 102), (250, 0, 400, 192));
        var portals = new List<PortalAvoid.Spot> { new(200, 96, PortalAvoid.MapRadius, "Blocking Portal") };
        var a = new WorldPt(75, 96, double.NaN);
        var b = new WorldPt(325, 96, double.NaN);

        var loose = new TilePathFinder(dir, null, portals);
        loose.LoadArea(0, 0, 400, 192);
        List<WorldPt>? through = loose.Leg(a, b, out string n1);
        Check(through != null && MinGap(through, 200, 96) < PortalAvoid.MapRadius, "the plain planner walks through it when there's no way round (saveroute's behaviour, unchanged): " + n1);

        var strict = new TilePathFinder(dir, null, portals) { StrictPortals = true };
        strict.LoadArea(0, 0, 400, 192);
        List<WorldPt>? none = strict.Leg(a, b, out string note);
        Check(none == null && note.Contains("Blocking Portal"), "strict: no leg, and the portal is named: " + note);
        var walk = new TilePathFinder(dir, null, portals) { StrictPortals = true, NoGaps = true };
        Check(walk.Walk(a, b, false, out string wn) == null && wn.Contains("Blocking Portal"), "strict walk with no gaps allowed: none either: " + wn);

        // A loop round the first room never uses the corridor, and its legs keep out of the circle.
        var room = new TilePathFinder(dir, null, portals);
        LoopMaker.Result r = LoopMaker.Loop(room, new WorldPt(75, 96, double.NaN), new LoopMaker.LoopOptions { Radius = 60, Points = 6 }, null, portals);
        CheckLoopRules(r, room, null, portals, "beside a portal");
        Check(r.Points.All(p => p.X < 151), "all its points in the first room");

        // A live portal (one the client sees) counts the same.
        var live = new List<PortalAvoid.Spot> { new(200, 96, PortalAvoid.LiveRadius, "Live Portal") };
        var liveStrict = new TilePathFinder(dir, null, null, live) { StrictPortals = true };
        liveStrict.LoadArea(0, 0, 400, 192);
        Check(liveStrict.Leg(a, b, out string ln) == null && ln.Contains("Live Portal"), "a live portal blocks it too: " + ln);
    }

    private static void LegsRoundABuildingAndOffWalls()
    {
        // Two strips of ground joined by a building (a room with a narrow way in from each strip, the two not
        // in line: its floor is navmesh too) and, the long way, a lane at the west end. In line, Recast made
        // one thin polygon from strip to strip straight through the room, and a leg's own two polygons are
        // always allowed, so a leg along that line couldn't be kept out.
        string dir = Bake("building", (0, 0, 384, 60), (0, 132, 384, 192), (0, 60, 30, 132),
            (170, 68, 214, 124), (172, 58, 184, 70), (200, 122, 212, 134));
        var a = new WorldPt(300, 30, double.NaN);
        var b = new WorldPt(300, 162, double.NaN);
        var through = new TilePathFinder(dir, null, null);
        through.LoadArea(0, 0, 384, 192);
        List<WorldPt>? direct = through.Leg(a, b, out _);
        Check(direct != null && Enters(direct, 172, 64, 212, 128), "without outlines the leg goes through the building");

        var outline = BuildingOutlines.FromPolygons(new[] { new (double, double)[] { (168, 62), (216, 62), (216, 130), (168, 130) } });
        var round = new TilePathFinder(dir, null, null) { AvoidArea = (x, y) => outline.Inside(x, y) >= 0 };
        round.LoadArea(0, 0, 384, 192);
        List<WorldPt>? outside = round.Leg(a, b, out string note);
        Check(outside != null && !Enters(outside, 172, 64, 212, 128), "with the outline it goes round by the lane: " + note);
        Check(outside != null && outside.Any(p => p.X < 32), "...the west lane");
        // A building that is the only way: through it all the same (keeping off buildings gives way).
        string only = Bake("buildingonly", (0, 0, 384, 60), (0, 132, 384, 192), (170, 68, 214, 124), (172, 58, 184, 70), (200, 122, 212, 134));
        var must = new TilePathFinder(only, null, null) { AvoidArea = (x, y) => outline.Inside(x, y) >= 0 };
        must.LoadArea(0, 0, 384, 192);
        Check(must.Leg(a, b, out _) != null, "when the building is the only way, through it");

        // Walls: the corners round the end of the wall come off it by the clearance, onto the ground.
        string wall = Bake("wallcorner", (0, 0, 192, 192), (192, 150, 384, 192), (384, 0, 576, 192));
        var from = new WorldPt(100, 20, double.NaN);
        var to = new WorldPt(480, 20, double.NaN);
        var plain = new TilePathFinder(wall, null, null);
        plain.LoadArea(0, 0, 576, 192);
        var pushed = new TilePathFinder(wall, null, null) { WallClearance = 1.0 };
        pushed.LoadArea(0, 0, 576, 192);
        List<WorldPt>? p0 = plain.Leg(from, to, out _), p1 = pushed.Leg(from, to, out _);
        Check(p0 != null && p1 != null && p0.Count == p1.Count, "the same corners with and without the clearance");
        if (p0 != null && p1 != null && p0.Count == p1.Count)
        {
            double moved = 0;
            for (int i = 1; i < p0.Count - 1; i++) moved = Math.Max(moved, p0[i].FlatDistance(p1[i]));
            Check(moved > 0.9 && moved < 1.1, $"a corner moved by the clearance ({moved:F2} yd)");
            Check(p1.All(p => pushed.Snap(p, 0.2, out _, out _)), "every corner still on walkable ground");
            Check(p0[0].FlatDistance(p1[0]) < 0.01 && p0[^1].FlatDistance(p1[^1]) < 0.01, "the ends never move");
        }
    }

    private static void PathThroughGivenPoints()
    {
        // The wall again: from the south of tile 0 to the south of tile 2 the only way is round the north.
        string dir = Bake("pathwall", (0, 0, 192, 192), (192, 150, 384, 192), (384, 0, 576, 192), (700, 0, 760, 60));
        BuildGraph(dir, out NavGraph g);
        g.BuildComponents();
        var pts = new List<WorldPt> { new(100, 20, double.NaN), new(480, 20, double.NaN), new(500, 120, double.NaN) };
        var mesh = new TilePathFinder(dir, g, null) { WallClearance = 1.0 };
        LoopMaker.Result r = LoopMaker.Path(mesh, pts, NavRouteType.Once, 30, Array.Empty<PortalAvoid.Spot>());
        Check(r.Ok && r.Legs.Count == 2, $"two legs ({r.Error})");
        Check(r.Ok && r.Legs[0].Any(p => p.Y > 150), "the first goes north through the passage");
        Check(r.Ok && r.Route!.RouteType == NavRouteType.Once && r.Route.Points.Count >= 4, "a once route");
        Check(r.Ok && NavCoords.Distance(r.Route!.Points[0].NS, r.Route.Points[0].EW, pts[0].Ns, pts[0].Ew) < 3
            && NavCoords.Distance(r.Route.Points[^1].NS, r.Route.Points[^1].EW, pts[2].Ns, pts[2].Ew) < 3, "it starts and ends at the given points");
        var back = NavRouteParser.LoadFromLines(r.Route!.ToLines());
        Check(back.LoadWarning == null && back.Points.Count == r.Route.Points.Count, "RynthAi reads it back");

        var loop = LoopMaker.Path(new TilePathFinder(dir, g, null), pts, NavRouteType.Circular, 30, Array.Empty<PortalAvoid.Spot>());
        Check(loop.Ok && loop.Legs.Count == 3 && loop.Route!.RouteType == NavRouteType.Circular, "a loop also walks back from the last point to the first");
        var line = LoopMaker.Path(new TilePathFinder(dir, g, null), pts, NavRouteType.Linear, 30, Array.Empty<PortalAvoid.Spot>());
        Check(line.Ok && line.Legs.Count == 2 && line.Route!.RouteType == NavRouteType.Linear, "a line doesn't");

        // A point with no ground, a leg that can't be walked, a point in a portal's circle: refused, named.
        var off = LoopMaker.Path(new TilePathFinder(dir, g, null), new List<WorldPt> { pts[0], new(288, 50, double.NaN), pts[1] }, NavRouteType.Once, 30, Array.Empty<PortalAvoid.Spot>());
        Check(!off.Ok && off.Error.StartsWith("point 2"), "a point in the gap: " + off.Error);
        var island = LoopMaker.Path(new TilePathFinder(dir, g, null), new List<WorldPt> { pts[1], new(730, 30, double.NaN) }, NavRouteType.Once, 30, Array.Empty<PortalAvoid.Spot>());
        Check(!island.Ok && island.Error.StartsWith("leg 1"), "a leg to an island: " + island.Error);
        var portal = new List<PortalAvoid.Spot> { new(480, 22, PortalAvoid.MapRadius, "Some Portal") };
        var inPortal = LoopMaker.Path(new TilePathFinder(dir, g, portal), pts, NavRouteType.Once, 30, portal);
        Check(!inPortal.Ok && inPortal.Error.StartsWith("point 2") && inPortal.Error.Contains("Some Portal"), "a point at a portal: " + inPortal.Error);
    }

    /// <summary>Yards of the legs inside building outlines (sampled every half yard).</summary>
    private static double YardsInBuildings(IEnumerable<List<WorldPt>> legs, BuildingOutlines b)
    {
        double inside = 0;
        foreach (List<WorldPt> leg in legs)
            for (int i = 1; i < leg.Count; i++)
            {
                WorldPt p = leg[i - 1], q = leg[i];
                int n = Math.Max(1, (int)(p.FlatDistance(q) * 2));
                for (int k = 0; k < n; k++)
                    if (b.Inside(p.X + (q.X - p.X) * (k + 0.5) / n, p.Y + (q.Y - p.Y) * (k + 0.5) / n) >= 0) inside += p.FlatDistance(q) / n;
            }
        return inside;
    }

    /// <summary>
    /// The real map (read only, when it's installed): loops by Holtburg (in town, among its buildings and
    /// portals, and out on the open ground east of it) with the real building outlines and the Atlas's
    /// portals, with and without keeping the legs off building floors. Prints the sizes and times.
    /// </summary>
    private static void RealLoopsNearHoltburg()
    {
        string dir = Environment.GetEnvironmentVariable("RYNTHNAV_NAVDATA") ?? @"C:\Games\RynthCore\NavData";
        if (!File.Exists(Path.Combine(dir, "nav_A9B4.tile"))) { Console.WriteLine("     (no real NavData here: skipped)"); return; }
        BuildingOutlines? buildings = BuildingOutlines.Load(Path.Combine(dir, BuildingOutlines.FileName));
        var portals = new List<PortalAvoid.Spot>();
        string tsv = Path.Combine(dir, "portals.tsv");
        if (File.Exists(tsv))
            foreach (string line in File.ReadLines(tsv))
            {
                string[] f = line.Split('\t');
                if (f.Length < 5 || !double.TryParse(f[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ns)
                    || !double.TryParse(f[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ew)) continue;
                portals.Add(new PortalAvoid.Spot(NavCoords.WorldX(ew), NavCoords.WorldY(ns), PortalAvoid.MapRadius, f[4]));
            }
        Console.WriteLine($"     {buildings?.Count ?? 0} building outlines, {portals.Count} portals");
        foreach (var (name, ns, ew, radius) in new[] { ("Holtburg", 42.08, 33.6, 60.0), ("east of Holtburg", 42.0, 34.6, 150.0) })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var near = portals.Where(p => Math.Abs(p.X - NavCoords.WorldX(ew)) < radius + 300 && Math.Abs(p.Y - NavCoords.WorldY(ns)) < radius + 300).ToList();
            var mesh = new TilePathFinder(dir, null, near) { };
            LoopMaker.Result r = LoopMaker.Loop(mesh, WorldPt.FromMap(ns, ew, double.NaN), new LoopMaker.LoopOptions { Radius = radius, WallClearance = 1.0 }, buildings, near);
            long ms = sw.ElapsedMilliseconds;
            Console.WriteLine($"     {name}, {radius:F0} yd: {(r.Ok ? $"{r.Points.Count} of {r.Asked} points, {r.Route?.Points.Count} waypoints, {r.Yards:F0} yd a lap" : r.Error)}, "
                + $"{r.DroppedPoints.Count} dropped{(r.DroppedPoints.Count > 0 ? $" ({string.Join("; ", r.DroppedPoints.Select(d => d.Why).Distinct())})" : "")}, {mesh.TilesRead} tiles, {ms} ms");
            CheckLoopRules(r, mesh, buildings, near, name);
            Check(ms < 30000, $"{name}: in reasonable time ({ms} ms)");
            if (buildings == null || !r.Ok) continue;
            // The same loop with the legs free to cross building floors: they go through at least as much.
            LoopMaker.Result free = LoopMaker.Loop(new TilePathFinder(dir, null, near), WorldPt.FromMap(ns, ew, double.NaN),
                new LoopMaker.LoopOptions { Radius = radius, WallClearance = 1.0, LegsRoundBuildings = false }, buildings, near);
            double kept = YardsInBuildings(r.Legs, buildings), loose = free.Ok ? YardsInBuildings(free.Legs, buildings) : double.NaN;
            Console.WriteLine($"       legs inside building outlines: {kept:F0} yd kept off them, {loose:F0} yd free ({free.Yards:F0} yd a lap)");
            Check(!free.Ok || kept <= loose + 1, $"{name}: keeping legs off buildings doesn't put more of them inside ({kept:F0} vs {loose:F0} yd)");
        }

        // A path from Holtburg out east and back up north, on the route graph, never through a portal's circle.
        string graphFile = Path.Combine(dir, NavGraph.FileName);
        if (!File.Exists(graphFile)) return;
        NavGraph g = NavGraph.Load(graphFile);
        g.BuildComponents();
        var pts = new List<WorldPt> { WorldPt.FromMap(42.0, 33.9, double.NaN), WorldPt.FromMap(41.6, 35.2, double.NaN), WorldPt.FromMap(42.4, 35.6, double.NaN) };
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var finder = new TilePathFinder(dir, g, portals) { StrictPortals = true, WallClearance = 1.0 };
        LoopMaker.Result p = LoopMaker.Path(finder, pts, NavRouteType.Once, 30, portals);
        Console.WriteLine($"     a path east of Holtburg: {(p.Ok ? $"{p.Legs.Count} legs, {p.Route?.Points.Count} waypoints, {p.Yards:F0} yd" : p.Error)}, {finder.TilesRead} tiles, {sw2.ElapsedMilliseconds} ms");
        Check(p.Ok && p.Legs.Count == 2, "the real path: two legs " + p.Error);
        if (p.Ok)
            foreach (List<WorldPt> leg in p.Legs)
                foreach (PortalAvoid.Spot s in portals.Where(s => Math.Abs(s.X - pts[1].X) < 1000 && Math.Abs(s.Y - pts[1].Y) < 1000))
                    Check(MinGap(leg, s.X, s.Y) >= s.R - 0.06, $"the real path keeps out of {s.Name}");
    }
}

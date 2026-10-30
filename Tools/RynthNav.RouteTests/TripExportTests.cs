using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthNav;
using RynthCore.Plugin.Shared;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// A planned trip on the ground and as a RynthAi nav route: the trip's legs (PlannedTrip), the
// walking path thinned (TripPathBuilder), the path markers (PathMarkers: what's built, what's drawn,
// the next waypoint, when the markers go) and "save as nav route" (NavRouteExport: walks, recalls,
// portals and the Town Network as nav steps, read back with RynthAi's own reader; a file that's
// there isn't replaced without being asked).
internal static partial class Program
{
    private static void RunTripExport()
    {
        Run(nameof(ThinKeepsCornersAndSplitsStraights), ThinKeepsCornersAndSplitsStraights);
        Run(nameof(StopShortAlongThePath), StopShortAlongThePath);
        Run(nameof(TripLegsWalkRecallPortal), TripLegsWalkRecallPortal);
        Run(nameof(TripPathStraightLineWhenNoMesh), TripPathStraightLineWhenNoMesh);
        Run(nameof(ExportWalkRecallPortalRoundTrip), ExportWalkRecallPortalRoundTrip);
        Run(nameof(ExportCommandRecallAndDungeonEntrance), ExportCommandRecallAndDungeonEntrance);
        Run(nameof(ExportTownNetwork), ExportTownNetwork);
        Run(nameof(ExportStopsAtAStepWithNoNavEquivalent), ExportStopsAtAStepWithNoNavEquivalent);
        Run(nameof(SaveRefusesToOverwrite), SaveRefusesToOverwrite);
        Run(nameof(MarkersBuiltFromTheTrip), MarkersBuiltFromTheTrip);
        Run(nameof(MarkersEmitNearTheCharacter), MarkersEmitNearTheCharacter);
        Run(nameof(MarkersNextWaypointAdvances), MarkersNextWaypointAdvances);
        Run(nameof(MarkersClearOffTheTrip), MarkersClearOffTheTrip);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>A navmesh-like path a→b: a point every 2 yd, swinging 10 yd out round an obstacle halfway.</summary>
    private static List<WorldPt>? FakeWalk(WorldPt a, WorldPt b, bool intoPortal, out string note)
    {
        note = "";
        var pts = new List<WorldPt>();
        double len = a.FlatDistance(b);
        int n = Math.Max(2, (int)(len / 2));
        double nx = -(b.Y - a.Y) / len, ny = (b.X - a.X) / len;   // left of the way
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            double off = 10.0 * Math.Max(0, 1 - Math.Abs(t - 0.5) * 8);   // a tent 10 yd high from t=0.375 to 0.625
            pts.Add(new WorldPt(a.X + (b.X - a.X) * t + nx * off, a.Y + (b.Y - a.Y) * t + ny * off, 5.0));
        }
        return pts;
    }

    private static PlannedTrip Trip(List<RouteStep> steps, double startNs, double startEw, string goal, double goalNs, double goalEw,
        List<TripRecall>? recalls = null, string enterPortal = "", string enterName = "", bool startOnMap = true, int id = 1)
    {
        WorldPt start = WorldPt.FromMap(startNs, startEw, 5.0);
        uint lb = start.Landblock;
        uint cell = (lb << 16) | 0x0001;
        return new PlannedTrip(id, steps, goal, goalNs, goalEw, startOnMap, start, cell, recalls, enterPortal, enterName);
    }

    private sealed class Recorder : IMarkerSink
    {
        public readonly List<(float X, float Y, float Z, float R, uint C)> Rings = new();
        public readonly List<(float X1, float Y1, float Z1, float X2, float Y2, float Z2, uint C)> Lines = new();
        public int Tris;
        public void Ring(float x, float y, float z, float radius, float thickness, uint colorArgb) => Rings.Add((x, y, z, radius, colorArgb));
        public void Line(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb) => Lines.Add((x1, y1, z1, x2, y2, z2, colorArgb));
        public void Triangle(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint colorArgb) => Tris++;
    }

    // ── Thinning ─────────────────────────────────────────────────────────

    private static void ThinKeepsCornersAndSplitsStraights()
    {
        // 100 yd east in 1 yd steps with 0.2 yd wobble, then 50 yd north; heights unknown at first.
        var raw = new List<WorldPt>();
        for (int i = 0; i <= 100; i++) raw.Add(new WorldPt(1000 + i, 1000 + (i % 2 == 0 ? 0.2 : -0.2), i < 3 ? double.NaN : 7));
        for (int i = 1; i <= 50; i++) raw.Add(new WorldPt(1100, 1000 + i, 7));
        raw.Add(new WorldPt(1100, 1050.1, 7));   // a near duplicate
        List<WorldPt> thin = TripPathBuilder.Thin(raw);
        Check(thin.Count < 12, $"151 navmesh points thin to a few ({thin.Count})");
        Check(thin[0].FlatDistance(raw[0]) < 0.01 && thin[^1].FlatDistance(raw[^1]) < 0.6, "the ends are kept");
        Check(thin.Any(p => Math.Abs(p.X - 1100) < 0.6 && Math.Abs(p.Y - 1000) < 0.6), "the corner is kept");
        for (int i = 1; i < thin.Count; i++)
            Check(thin[i - 1].FlatDistance(thin[i]) <= TripPathBuilder.MaxSpacing + 0.01, $"no stretch longer than {TripPathBuilder.MaxSpacing} yd ({i})");
        Check(thin.All(p => !double.IsNaN(p.H)), "unknown heights filled from the neighbours");
        foreach (WorldPt p in raw)
        {
            double best = double.MaxValue;
            for (int i = 1; i < thin.Count; i++) best = Math.Min(best, PlannedTrip.SegmentDistance(p, thin[i - 1], thin[i]));
            if (best > TripPathBuilder.ThinTolerance + 0.01) { Check(false, $"a dropped point is {best:F2} yd off the thinned path"); break; }
        }
        Check(TripPathBuilder.Thin(new List<WorldPt> { new(0, 0, 1), new(0, 100, 1) }).Count == 3, "a 100 yd straight gets a point every 60 yd at most (2 stretches)");
    }

    private static void StopShortAlongThePath()
    {
        var path = new List<WorldPt> { new(0, 0, 1), new(10, 0, 1), new(10, 3, 1) };
        List<WorldPt> s = TripPathBuilder.StopShort(path, 5);
        Check(s.Count == 2 && Math.Abs(s[^1].X - 8) < 1e-9 && Math.Abs(s[^1].Y) < 1e-9, "5 yd short, back round the corner");
        Check(TripPathBuilder.StopShort(path, 20).Count == 1, "shorter than that: only the start");
        Check(TripPathBuilder.StopShort(path, 0).Count == 3, "0: unchanged");
    }

    // ── The trip's legs ──────────────────────────────────────────────────

    private static void TripLegsWalkRecallPortal()
    {
        var steps = new List<RouteStep>
        {
            new(10, 10, false, true, "Lifestone Recall", 0, 20, 20),          // cast here, land at 20,20
            new(20.5, 20, true, false, "Portal to Far", -1, 50, 50),         // walk to a portal, land at 50,50
            new(50.4, 50.3, false, false, "walk"),                            // walk to the goal
        };
        var trip = Trip(steps, 10, 10, "Far Goal", 50.4, 50.3, new List<TripRecall> { new("Lifestone Recall", 1635, "", 13) });
        Check(trip.WalkFrom[0] == null && trip.Point[0] is WorldPt c && c.FlatDistance(trip.Start) < 0.01, "a recall walks nowhere; it's cast at the start");
        Check(trip.WalkFrom[1] is WorldPt f1 && Math.Abs(f1.Ns - 20) < 1e-9 && Math.Abs(f1.Ew - 20) < 1e-9, "the next walk starts at the recall's landing");
        Check(trip.Point[1] is WorldPt p1 && Math.Abs(p1.Ns - 20.5) < 1e-9, "...and ends at the portal");
        Check(trip.WalkFrom[2] is WorldPt f2 && Math.Abs(f2.Ns - 50) < 1e-9, "the last walk starts at the portal's landing");
        Check(trip.RecallOf(0)?.SpellId == 1635 && trip.RecallOf(1) == null, "the recall by index");

        // A portal into a dungeon (no landing on the map) and nothing after it: the dungeon goto.
        var into = Trip(new List<RouteStep> { new(10.2, 10.2, true, false, "Matron Hive") }, 10, 10, "Matron Hive", 10.2, 10.2,
            enterPortal: "Portal to Matron Hive", enterName: "Matron Hive");
        Check(into.EndsInEntrance && into.PortalName(0) == "Portal to Matron Hive", "a dungeon goto's portal is the portal object, not the dungeon's name");
        // No steps: a plain walk to the goal.
        var plain = Trip(new List<RouteStep>(), 10, 10, "Near", 10.3, 10.1);
        Check(plain.Count == 1 && plain.WalkFrom[0] != null && !plain.Steps[0].UsePortal, "no steps = one walk");
    }

    private static void TripPathStraightLineWhenNoMesh()
    {
        var trip = Trip(new List<RouteStep> { new(10.5, 10, false, false, "walk") }, 10, 10, "G", 10.5, 10);
        TripPath p = TripPathBuilder.Build(trip, (WorldPt a, WorldPt b, bool ip, out string note) => { note = "no navmesh at its start"; return null; }, null);
        Check(p.Walks[0] != null && p.Walks[0]!.Length == 3, $"a straight line, split every 60 yd (120 yd: {p.Walks[0]?.Length})");
        Check(p.Notes.Count == 1 && p.Notes[0].Contains("step 1") && p.Notes[0].Contains("straight"), "...with a note: " + string.Join("; ", p.Notes));
        TripPath q = TripPathBuilder.Build(trip, FakeWalk, null);
        Check(q.Notes.Count == 0 && q.Walks[0]!.Length < 20, $"the navmesh path, thinned ({q.Walks[0]!.Length} points)");
        Check(q.Walks[0]!.Any(w => PlannedTrip.SegmentDistance(w, trip.Start, trip.Point[0]!.Value) > 9), "it keeps the swing round the obstacle");
    }

    // ── Save as nav route ────────────────────────────────────────────────

    private static void ExportWalkRecallPortalRoundTrip()
    {
        var steps = new List<RouteStep>
        {
            new(10, 10, false, true, "Lifestone Recall", 0, 20, 20),
            new(20.5, 20, true, false, "Portal to Far", -1, 50, 50),
            new(50.4, 50.3, false, false, "walk"),
        };
        var trip = Trip(steps, 10, 10, "Far Goal", 50.4, 50.3, new List<TripRecall> { new("Lifestone Recall", 1635, "", 13) });
        TripPath path = TripPathBuilder.Build(trip, FakeWalk, null);
        NavRouteExport.Result r = NavRouteExport.Convert(trip, path, null);
        Check(r.StoppedAt == -1 && r.StopReason == "", "the whole trip converts: " + r.StopReason);
        List<NavPoint> pts = r.Route.Points;
        Check(r.Route.RouteType == NavRouteType.Once, "a Once route");
        Check(pts[0].Type == NavPointType.Recall && pts[0].SpellId == 1635, "first: the recall spell");
        int portal = pts.FindIndex(p => p.Type == NavPointType.PortalNPC);
        Check(portal > 1, "then walking points, then the portal step");
        Check(pts.Skip(1).Take(portal - 1).All(p => p.Type == NavPointType.Point), "walking legs are Points");
        Check(portal - 1 < 15, $"thinned: not every navmesh corner ({portal - 1} points for 120 yd with an obstacle)");
        NavPoint pn = pts[portal];
        Check(pn.TargetName == "Portal to Far" && pn.ObjectClass == NavRouteExport.PortalObjectClass && pn.IsTie, "the portal by name, VTank's portal class");
        Check(Math.Abs(pn.PortalExitNS - 20.5) < 1e-6 && Math.Abs(pn.PortalExitEW - 20) < 1e-6, "at the portal's entrance");
        NavPoint before = pts[portal - 1];
        double toPortal = NavCoords.Distance(before.NS, before.EW, 20.5, 20);
        Check(toPortal > NavRouteExport.PortalStopShort - 0.5 && toPortal < 12, $"the walk stops short of the entrance ({toPortal:F1} yd)");
        Check(pts[^1].Type == NavPointType.Point && NavCoords.Distance(pts[^1].NS, pts[^1].EW, 50.4, 50.3) < 0.5, "the last point is the goal");
        Check(NavCoords.Distance(pts[portal + 1].NS, pts[portal + 1].EW, 50, 50) < TripPathBuilder.MaxSpacing, "after the portal the walk starts near its landing");
        Check(pts.Where(p => p.Type == NavPointType.Point).All(p => Math.Abs(p.Z - 5.0 / 240.0) < 1e-9), "heights in nav units (metres / 240)");

        // Read back with RynthAi's own reader.
        var back = NavRouteParser.LoadFromLines(r.Route.ToLines());
        Check(back.LoadWarning == null && back.Points.Count == pts.Count && back.RouteType == NavRouteType.Once, "RynthAi reads it back whole");
        bool same = true;
        for (int i = 0; i < pts.Count && same; i++)
            same = back.Points[i].Type == pts[i].Type && Math.Abs(back.Points[i].NS - pts[i].NS) < 1e-9 && Math.Abs(back.Points[i].EW - pts[i].EW) < 1e-9
                && back.Points[i].SpellId == pts[i].SpellId && back.Points[i].TargetName == pts[i].TargetName && back.Points[i].ObjectClass == pts[i].ObjectClass;
        Check(same, "every step the same after the round trip");
        Check(r.Waypoints + r.Portals + r.Recalls == pts.Count, "the counts add up");
    }

    private static void ExportCommandRecallAndDungeonEntrance()
    {
        var steps = new List<RouteStep>
        {
            new(10, 10, false, true, "@lifestone", 0, 20, 20),
            new(20.3, 20.2, true, false, "Matron Hive"),   // Entrance.EndingInPortal labels it with the dungeon
        };
        var trip = Trip(steps, 10, 10, "Matron Hive", 20.3, 20.2, new List<TripRecall> { new("@lifestone", 0, "@lifestone", 15) },
            enterPortal: "Portal to Matron Hive", enterName: "Matron Hive");
        NavRouteExport.Result r = NavRouteExport.Convert(trip, TripPathBuilder.Build(trip, FakeWalk, null), null);
        List<NavPoint> pts = r.Route.Points;
        Check(pts[0].Type == NavPointType.Chat && pts[0].ChatCommand == "@lifestone", "a command recall is a chat step");
        Check(pts[1].Type == NavPointType.Pause && pts[1].PauseTimeMs == NavRouteExport.CommandRecallPauseMs, "...and a pause for its teleport");
        Check(pts[^1].Type == NavPointType.PortalNPC && pts[^1].TargetName == "Portal to Matron Hive", "it ends with the dungeon's entrance portal, by the portal's name");
        Check(r.StoppedAt == -1, "nothing left out");
        var back = NavRouteParser.LoadFromLines(r.Route.ToLines());
        Check(back.Points.Count == pts.Count && back.Points[0].ChatCommand == "@lifestone" && back.Points[1].PauseTimeMs == NavRouteExport.CommandRecallPauseMs,
            "read back by RynthAi");
    }

    private static void ExportTownNetwork()
    {
        TownNet net = TownNet.Parse(SmallTownNet(), out _)!;
        var steps = PortalRoute.Plan(NoPortals, null, net.Links(90, false, null), 0, 0, 40, 40, out _, out int used);
        Check(used == 2 && steps.Count == 3 && steps[1].IsHub, "the plan: walk to the entry, the network, walk on");
        var trip = Trip(steps, 0, 0, "Eastwatch", 40, 40);
        TripPath path = TripPathBuilder.Build(trip, FakeWalk, net);
        Check(path.Hub[1] != null && path.Hub[1]!.Length >= 2, "the walk inside comes from townnet.json");
        NavRouteExport.Result r = NavRouteExport.Convert(trip, path, net);
        List<NavPoint> pts = r.Route.Points;
        var portals = pts.Where(p => p.Type == NavPointType.PortalNPC).ToList();
        Check(r.StoppedAt == -1 && portals.Count == 2, "two portal steps: " + r.StopReason);
        Check(portals[0].TargetName == "Portal to Town Network" && portals[1].TargetName == "Portal to Eastwatch", "into the network, then the exit by name");
        int exit = pts.IndexOf(portals[1]), entry = pts.IndexOf(portals[0]);
        Check(exit - entry >= 2, "waypoints inside the network between them");
        // Inside: the network's landblock 0x0007 (coordinates the way RynthAi reads a dungeon pose).
        // The stored walk (60,-70) to (70,-70) to (86.5,-70) is one straight line: thinned to its end,
        // stopped 2 yd short of the exit portal.
        NavCoordinateHelper(0x0007016Bu, 84.5, -70, out double ins, out double iew);
        Check(pts.Skip(entry + 1).Take(exit - entry - 1).Any(p => Math.Abs(p.NS - ins) < 0.001 && Math.Abs(p.EW - iew) < 0.001), "inside points in dungeon coordinates");
        NavCoordinateHelper(0x0007016Bu, 86.5, -70, out double xns, out double xew);
        Check(Math.Abs(portals[1].PortalExitNS - xns) < 1e-6 && Math.Abs(portals[1].PortalExitEW - xew) < 1e-6, "the exit portal where it stands");

        // Without townnet.json: stop before the network, say why.
        NavRouteExport.Result none = NavRouteExport.Convert(trip, TripPathBuilder.Build(trip, FakeWalk, null), null);
        Check(none.StoppedAt == 1 && none.StopReason.Contains("Town Network"), "no network data: stops at it: " + none.StopReason);
        Check(none.Route.Points.Count > 0 && none.Route.Points[^1].Type == NavPointType.PortalNPC, "...keeping the walk to the entry portal");
    }

    private static void NavCoordinateHelper(uint cell, double x, double y, out double ns, out double ew) => NavCoords.FromCell(cell, x, y, out ns, out ew);

    private static void ExportStopsAtAStepWithNoNavEquivalent()
    {
        var steps = new List<RouteStep>
        {
            new(10.2, 10, false, false, "walk"),
            new(10.2, 10, false, true, "Some Recall", 3, 20, 20),   // index 3: not in the trip's recalls
            new(20.1, 20, false, false, "walk"),
        };
        var trip = Trip(steps, 10, 10, "G", 20.1, 20, new List<TripRecall>());
        NavRouteExport.Result r = NavRouteExport.Convert(trip, TripPathBuilder.Build(trip, FakeWalk, null), null);
        Check(r.StoppedAt == 1 && r.StopReason.StartsWith("step 2:") && r.StopReason.Contains("Some Recall"), "stops at the recall it can't cast: " + r.StopReason);
        Check(r.Route.Points.Count > 0 && r.Route.Points.All(p => p.Type == NavPointType.Point), "the walk before it is kept");

        var other = Trip(steps, 10, 10, "G", 20.1, 20, new List<TripRecall>(), id: 2);
        NavRouteExport.Result wrong = NavRouteExport.Convert(other, TripPathBuilder.Build(trip, FakeWalk, null), null);
        Check(wrong.StoppedAt == 0 && wrong.Route.Points.Count == 0, "a path from another trip isn't used");
    }

    private static void SaveRefusesToOverwrite()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rnav-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trip = Trip(new List<RouteStep> { new(10.2, 10, false, false, "walk") }, 10, 10, "G", 10.2, 10);
            NavRouteParser route = NavRouteExport.Convert(trip, TripPathBuilder.Build(trip, FakeWalk, null), null).Route;
            Check(NavRouteExport.Save(dir, "To G", route, false, out string path, out bool exists, out string err) && !exists && err == "",
                "a new file is written (the folder made): " + err);
            string first = File.ReadAllText(path);
            Check(NavRouteParser.Load(path).Points.Count == route.Points.Count, "RynthAi loads the file");

            var other = new NavRouteParser { RouteType = NavRouteType.Once };
            other.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 1, EW = 2 });
            Check(!NavRouteExport.Save(dir, "To G", other, false, out _, out exists, out _) && exists, "the same name again: refused");
            Check(File.ReadAllText(path) == first, "...and the file is untouched");
            Check(!File.Exists(path + ".tmp"), "no temporary file left");
            Check(NavRouteExport.Save(dir, "To G", other, true, out _, out _, out _) && NavRouteParser.Load(path).Points.Count == 1, "overwrite: replaced");

            Check(NavRouteExport.FileName("  To Holtburg.nav ", out _) == "To Holtburg", "a typed name: trimmed, without .nav");
            Check(NavRouteExport.FileName("a/b", out string e1) == "" && e1.Length > 0, "a path isn't a name");
            Check(NavRouteExport.FileName("..", out _) == "" && NavRouteExport.FileName("", out _) == "", "nor .. or nothing");
            Check(NavRouteExport.SafeName("To Lin: \"Town\"?") == "To Lin Town", "a place's name made safe for a file");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── Markers ──────────────────────────────────────────────────────────

    private static (PlannedTrip Trip, TripPath Path) MarkerTrip()
    {
        var steps = new List<RouteStep>
        {
            new(10, 10, false, true, "Lifestone Recall", 0, 20, 20),
            new(20.5, 20, true, false, "Portal to Far", -1, 50, 50),
            new(50.4, 50.3, false, false, "walk"),
        };
        var trip = Trip(steps, 10, 10, "Far Goal", 50.4, 50.3, new List<TripRecall> { new("Lifestone Recall", 1635, "", 13) });
        return (trip, TripPathBuilder.Build(trip, FakeWalk, null));
    }

    private static void MarkersBuiltFromTheTrip()
    {
        var (trip, path) = MarkerTrip();
        MarkerSnapshot bare = PathMarkers.Build(trip, null, null);
        Check(!bare.HasPath && bare.Marks.Length == 3, $"without the path: the steps' markers only ({bare.Marks.Length})");
        Check(bare.Marks[0].Kind == MarkKind.Recall && bare.Marks[0].Label.StartsWith("RECALL LIFESTONE") && bare.Marks[0].Label.Length <= PathMarkers.MaxLabelChars,
            "the recall, where it's cast: " + bare.Marks[0].Label);
        Check(bare.Marks[1].Kind == MarkKind.Portal && bare.Marks[1].Label.StartsWith("PORTAL"), "the portal at its entrance");
        Check(bare.Marks[2].Kind == MarkKind.Goal && bare.Marks[2].Label == "FAR GOAL", "the goal");
        Check(bare.Marks.All(m => !m.LineToNext), "no lines over ground not worked out yet");

        MarkerSnapshot s = PathMarkers.Build(trip, path, null);
        Check(s.HasPath && s.Marks.Length > 6, $"with the path: the waypoints too ({s.Marks.Length})");
        int portal = Array.FindIndex(s.Marks, m => m.Kind == MarkKind.Portal);
        Check(!s.Marks[portal].LineToNext && !s.Marks[0].LineToNext, "no line across a teleport (after the portal, after the recall)");
        Check(s.Marks[portal - 1].LineToNext && s.Marks[portal - 1].Kind == MarkKind.Waypoint, "a line into the portal");
        Check(s.Marks.Count(m => m.Kind == MarkKind.Goal) == 1 && s.Marks[^1].Kind == MarkKind.Goal, "the goal last");
        for (int i = 1; i < s.Marks.Length; i++)
            if (s.Marks[i].P.FlatDistance(s.Marks[i - 1].P) < 0.99) { Check(false, $"two markers on one spot ({i})"); break; }
        Check(PathMarkers.Build(trip, TripPathBuilder.Build(Trip(trip.Steps.ToList(), 10, 10, "x", 1, 1, id: 9), FakeWalk, null), null).HasPath == false,
            "another trip's path isn't drawn");
    }

    private static void MarkersEmitNearTheCharacter()
    {
        var (trip, path) = MarkerTrip();
        MarkerSnapshot s = PathMarkers.Build(trip, path, null);
        WorldPt landing = WorldPt.FromMap(20.3, 20);   // on the walk from the recall's landing to the portal, 48 yd short of it
        var rec = new Recorder();
        int rings = PathMarkers.Emit(s, new MarkerView(landing.X, landing.Y, 5.0, -1, true, true), rec);
        Check(rings > 3 && rec.Rings.Count == rings, $"rings near the character ({rings})");
        Check(rec.Lines.Count > 2, "lines between them");
        Check(rec.Lines[0].C == PathMarkers.GuideColor, "the guide line goes in first");
        float lx = (float)(landing.X - Math.Floor(landing.X / 192) * 192), lz = (float)(landing.Y - Math.Floor(landing.Y / 192) * 192);
        Check(Math.Abs(rec.Lines[0].X1 - lx) < 0.01 && Math.Abs(rec.Lines[0].Z1 - lz) < 0.01, "...from the character's feet, in the landblock's frame");
        Check(rec.Rings.Any(r => r.C == PathMarkers.PortalColor), "the portal in its own colour");
        Check(rec.Rings.All(r => r.X > -200 && r.X < 400 && r.Z > -200 && r.Z < 400 && Math.Abs(r.Y - 5.1f) < 0.01), "landblock-local, at path height");
        Check(rec.Tris > 0, "the portal's label drawn (triangles)");
        Check(!rec.Rings.Any(r => r.C == PathMarkers.GoalColor), "the goal, far away, isn't drawn");

        var off = new Recorder();
        PathMarkers.Emit(s, new MarkerView(landing.X, landing.Y, 5.0, -1, false, false), off);
        Check(off.Tris == 0 && off.Lines.All(l => l.C != PathMarkers.GuideColor), "labels and guide off: neither");

        WorldPt far = WorldPt.FromMap(80, 80);
        var none = new Recorder();
        Check(PathMarkers.Emit(s, new MarkerView(far.X, far.Y, 0, -1, true, true), none) == 0 && none.Lines.Count == 0, "nothing far from the trip");

        // A long dense walk: at most MaxWaypointRings rings.
        var many = new List<Mark>();
        for (int i = 0; i < 200; i++) many.Add(new Mark(new WorldPt(1000 + i, 1000, 1), MarkKind.Waypoint, "", 0, true));
        var dense = new MarkerSnapshot(5, true, many.ToArray());
        var cap = new Recorder();
        PathMarkers.Emit(dense, new MarkerView(1100, 1000, 1, 100, false, false), cap);
        Check(cap.Rings.Count <= PathMarkers.MaxWaypointRings + 1 && cap.Lines.Count <= PathMarkers.MaxLines, $"capped ({cap.Rings.Count} rings, {cap.Lines.Count} lines)");
        Check(cap.Rings.Count(r => r.C == PathMarkers.NextColor) == 1, "the next waypoint in red");
    }

    private static void MarkersNextWaypointAdvances()
    {
        var marks = new List<Mark>();
        for (int i = 0; i <= 10; i++) marks.Add(new Mark(new WorldPt(1000 + i * 10, 1000, 1), MarkKind.Waypoint, "", 0, true));
        marks.Add(new Mark(new WorldPt(1100, 1010, 1), MarkKind.Portal, "PORTAL X", 0, false));
        marks.Add(new Mark(new WorldPt(3000, 3000, 1), MarkKind.Goal, "G", 1, false));
        var s = new MarkerSnapshot(1, true, marks.ToArray());
        int next = PathMarkers.Advance(s, -1, 0, 1000, 1000);
        Check(next == 1, $"standing on the first: the next is the second ({next})");
        next = PathMarkers.Advance(s, next, 0, 1033, 1001);
        Check(next == 4, $"part way along: the one ahead ({next})");
        Check(PathMarkers.Advance(s, next, 0, 1005, 1000) == 4, "never back");
        Check(PathMarkers.Advance(s, next, 0, 1100, 1009) == 11, "at the end: the portal (the step's own marker)");
        Check(PathMarkers.Advance(s, 11, 1, 2900, 2900) == 12, "next step: its first marker");
        Check(PathMarkers.Advance(s, 0, 7, 0, 0) == -1, "a step with no markers: none");
    }

    private static void MarkersClearOffTheTrip()
    {
        var (trip, path) = MarkerTrip();
        uint startLb = (trip.StartCell >> 16) & 0xFFFF;
        Check(trip.Covers(startLb, path, null), "where it starts");
        Check(trip.Covers(WorldPt.FromMap(20, 20).Landblock, path, null), "the recall's landing");
        Check(trip.Covers(WorldPt.FromMap(35, 35).Landblock, path, null) == false, "between the portal and its landing (a teleport) isn't on it");
        Check(trip.Covers(WorldPt.FromMap(50.2, 50.1).Landblock, path, null), "the last walk");
        Check(!trip.Covers(WorldPt.FromMap(-60, -60).Landblock, path, null), "a teleport elsewhere isn't");
        Check(trip.Covers(0, path, null), "portal space (landblock 0) doesn't count against it");

        TownNet net = TownNet.Parse(SmallTownNet(), out _)!;
        var steps = PortalRoute.Plan(NoPortals, null, net.Links(90, false, null), 0, 0, 40, 40, out _, out _);
        var hubTrip = Trip(steps, 0, 0, "Eastwatch", 40, 40);
        Check(hubTrip.Covers(0x0007, null, net.Contains), "inside the Town Network on a trip that takes it");
        Check(!trip.Covers(0x0007, path, net.Contains), "...but not on one that doesn't");
    }
}

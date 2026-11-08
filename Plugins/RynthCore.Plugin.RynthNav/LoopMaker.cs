using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// The route maker's walks on the navmesh (/rnav makeloop and makepath, the "RynthNav.Routes"
/// interface), worked out on a worker in a TilePathFinder of its own, never the walker's navmesh.
///
/// A hunting loop round a centre (<see cref="Loop"/>):
///   1. the ground at the centre;
///   2. per point, a direction round the circle (north first, clockwise); in it, the first spot (out
///      at the radius, then nearer, then a little to either side) that is walkable ground, out of every
///      building's outline (+ <see cref="LoopOptions.BuildingMargin"/>), at least
///      <see cref="LoopOptions.PortalKeep"/> from every portal, <see cref="LoopOptions.MinWall"/> from the
///      navmesh's edge, apart from the points already chosen, and reached from the centre by a walk that
///      never enters a portal's circle and isn't the long way round. A direction with no such spot is
///      dropped, with the reason;
///   3. the points in walking order (RouteShape.OrderLoop: round the centre, no two legs crossing);
///   4. each point joined to the next, the last back to the first, by a walk that keeps out of every
///      portal's circle (strict: there is no fallback through one), pushed off walls by the wall
///      clearance, and round buildings where there is a way. A leg that can't be walked, or only far
///      the long way round, drops its far point and the next point is tried; fewer than three left is
///      a failure;
///   5. the legs as a circular nav route (RouteShape.Assemble).
/// A path through given points (<see cref="Path"/>): each point onto the ground within
/// <see cref="SnapUnits"/>, each leg walked (the route graph for long ones), any leg that can't be is a
/// failure naming it; loop, line or once.
/// Host-free (Detour only): the graph tests make loops and paths on baked floors and on the real map.
/// </summary>
internal static class LoopMaker
{
    /// <summary>A makepath point must have walkable ground within this many yards.</summary>
    public const double SnapUnits = 12.0;

    internal sealed class LoopOptions
    {
        public double Radius;
        public int Points;
        /// <summary>A point at least this far (yards) from any portal: a fight beside one ends in it.</summary>
        public double PortalKeep = 20.0;
        /// <summary>A point at least this far outside every building's outline.</summary>
        public double BuildingMargin = 3.0;
        /// <summary>A point at least this far from the navmesh's edge (a wall, a drop, water).</summary>
        public double MinWall = 2.0;
        /// <summary>Corners pushed this far off walls (RynthNav's "walls" avoid setting; 0 = off).</summary>
        public double WallClearance;
        /// <summary>Legs kept off building floors where there's another way (the points always are).</summary>
        public bool LegsRoundBuildings = true;
        public double Spacing = RouteShape.LoopSpacing;
    }

    internal sealed class Dropped
    {
        public double Ns, Ew;
        public string Why = "";
    }

    internal sealed class Result
    {
        public bool Ok;
        public string Error = "";
        public WorldPt Centre;
        /// <summary>The points kept, in walking order (the snapped ground).</summary>
        public readonly List<WorldPt> Points = new();
        public readonly List<Dropped> DroppedPoints = new();
        public readonly List<List<WorldPt>> Legs = new();
        public NavRouteParser? Route;
        public double Yards;
        /// <summary>Things worth saying that didn't stop it ("no building outlines"...).</summary>
        public readonly List<string> Notes = new();
        public int Asked;

        public Result Fail(string why) { Ok = false; Error = why; Route = null; return this; }
    }

    private static string At(WorldPt p) => NavCoords.Fmt(p.Ns, p.Ew);

    /// <summary>The nearest of <paramref name="portals"/> to (x, y), and how far; -1 = none.</summary>
    private static int NearestPortal(IReadOnlyList<PortalAvoid.Spot> portals, double x, double y, out double dist)
    {
        int best = -1; dist = double.MaxValue;
        for (int i = 0; i < portals.Count; i++)
        {
            double d = Math.Sqrt((portals[i].X - x) * (portals[i].X - x) + (portals[i].Y - y) * (portals[i].Y - y));
            if (d < dist) { dist = d; best = i; }
        }
        return best;
    }

    private static double Length(IReadOnlyList<WorldPt> pts)
    {
        double d = 0;
        for (int i = 1; i < pts.Count; i++) d += pts[i - 1].FlatDistance(pts[i]);
        return d;
    }

    /// <summary>A leg this much longer than the straight line is the long way round: its far point goes.</summary>
    private static bool TooLong(double walked, double straight, double radius) =>
        walked > Math.Max(2.5 * straight + 40.0, straight + radius);

    /// <summary>
    /// A hunting loop round <paramref name="centre"/> (its height NaN when not known) on
    /// <paramref name="mesh"/>, keeping out of <paramref name="buildings"/> (null: no outlines) and away
    /// from <paramref name="portals"/> (the Atlas's and the live ones, as circles of their avoid radius).
    /// The mesh's options are set here.
    /// </summary>
    public static Result Loop(TilePathFinder mesh, WorldPt centre, LoopOptions o, BuildingOutlines? buildings,
        IReadOnlyList<PortalAvoid.Spot> portals)
    {
        var r = new Result { Centre = centre };
        double R = o.Radius;
        int n = o.Points > 0 ? o.Points : RouteShape.DefaultPoints(R);
        r.Asked = n;
        mesh.StrictPortals = true;
        mesh.NoGaps = true;
        mesh.WallClearance = o.WallClearance;
        mesh.AvoidArea = buildings == null || !o.LegsRoundBuildings ? null : (x, y) => buildings.Inside(x, y) >= 0;
        if (buildings == null) r.Notes.Add("no building outlines (NavData\\worldmap.vec): points were only kept off walls");

        if (!mesh.LoadArea(centre.X - R - 30, centre.Y - R - 30, centre.X + R + 30, centre.Y + R + 30))
            return r.Fail($"no navmesh round {At(centre)} (its tiles aren't in NavData)");
        if (!mesh.Snap(centre, 16, out WorldPt c, out _))
            return r.Fail($"no walkable ground within 16 yd of {At(centre)}");
        r.Centre = c;

        // 2. A point per direction.
        var chosen = new List<WorldPt>();
        double minApart = Math.Max(10.0, R * 0.25);
        double sector = 2 * Math.PI / n;
        double[] reaches = { 1.0, 0.85, 0.7, 0.55, 0.4 };
        double[] turns = { 0, 0.3, -0.3 };
        for (int k = 0; k < n; k++)
        {
            string why = "";
            int whyRank = -1;
            void Reason(int rank, string text) { if (rank > whyRank) { whyRank = rank; why = text; } }
            bool found = false;
            foreach (double f in reaches)
            {
                foreach (double turn in turns)
                {
                    double a = k * sector + turn * sector;
                    var want = new WorldPt(c.X + Math.Sin(a) * R * f, c.Y + Math.Cos(a) * R * f, c.H);
                    if (!mesh.Snap(want, 6, out WorldPt g, out long poly)) { Reason(0, "no walkable ground there (water, a cliff, a building's wall)"); continue; }
                    if (buildings != null && buildings.Inside(g.X, g.Y, o.BuildingMargin) >= 0) { Reason(1, "in a building"); continue; }
                    int pi = NearestPortal(portals, g.X, g.Y, out double pd);
                    if (pi >= 0 && pd < o.PortalKeep) { Reason(2, $"{pd:F0} yd from the portal {(portals[pi].Name.Length > 0 ? portals[pi].Name : "there")}"); continue; }
                    if (mesh.WallDistance(g, poly, o.MinWall) < o.MinWall) { Reason(1, "against a wall or a drop"); continue; }
                    bool crowded = false;
                    foreach (WorldPt p in chosen) crowded |= p.FlatDistance(g) < minApart;
                    if (crowded) { Reason(1, "too near another point"); continue; }
                    List<WorldPt>? walk = mesh.Leg(c, g, out string note);
                    if (walk == null) { Reason(3, "not reachable from the centre" + (note.Length > 0 ? $" ({note})" : "")); continue; }
                    double len = Length(walk);
                    if (len > 4 * R + 60) { Reason(3, $"only reachable the long way round ({len:F0} yd from the centre)"); continue; }
                    chosen.Add(g);
                    found = true;
                    break;
                }
                if (found) break;
            }
            if (!found)
            {
                double a0 = k * sector;
                var spot = new WorldPt(c.X + Math.Sin(a0) * R, c.Y + Math.Cos(a0) * R, double.NaN);
                r.DroppedPoints.Add(new Dropped { Ns = spot.Ns, Ew = spot.Ew, Why = why.Length > 0 ? why : "no ground there" });
            }
        }
        if (chosen.Count < RouteLimits.MinLoopPoints)
            return r.Fail($"only {chosen.Count} of {n} points could be placed round {At(c)} within {R:F0} yd"
                + (r.DroppedPoints.Count > 0 ? $" ({r.DroppedPoints[0].Why})" : "") + "; try another centre or radius");

        // 3. Walking order.
        int[] order = RouteShape.OrderLoop(chosen, c);
        var pts = new List<WorldPt>(order.Length);
        foreach (int i in order) pts.Add(chosen[i]);

        // 4. Join them.
        var legs = new List<List<WorldPt>>();
        int at = 0;
        while (at < pts.Count)
        {
            if (pts.Count < RouteLimits.MinLoopPoints)
                return r.Fail($"only {pts.Count} points could be joined into a loop round {At(c)}"
                    + (r.DroppedPoints.Count > 0 ? $" ({r.DroppedPoints[^1].Why})" : ""));
            int next = (at + 1) % pts.Count;
            List<WorldPt>? leg = mesh.Leg(pts[at], pts[next], out string note);
            double straight = pts[at].FlatDistance(pts[next]);
            string why = leg == null ? (note.Length > 0 ? note : "no walk to it")
                : TooLong(Length(leg), straight, R) ? $"the walk to it is {Length(leg):F0} yd, the long way round" : "";
            if (why.Length == 0) { legs.Add(leg!); at++; continue; }
            // Drop the far point; the closing leg (back to the first) drops the last one instead.
            int drop = next == 0 ? at : next;
            r.DroppedPoints.Add(new Dropped { Ns = pts[drop].Ns, Ew = pts[drop].Ew, Why = why });
            pts.RemoveAt(drop);
            if (drop == at && legs.Count > 0) { legs.RemoveAt(legs.Count - 1); at--; }
        }

        // 5. The route.
        r.Points.AddRange(pts);
        r.Legs.AddRange(legs);
        r.Route = RouteShape.Assemble(legs, NavRouteType.Circular, o.Spacing, out r.Yards);
        r.Ok = r.Route.Points.Count >= 2;
        if (!r.Ok) r.Error = "the joined legs came to nothing";
        return r;
    }

    /// <summary>
    /// A route through <paramref name="given"/> in order (<paramref name="type"/>: a circular route also
    /// walks from the last back to the first), on <paramref name="mesh"/> with its options as set by the
    /// caller; each leg must be walkable, or it fails saying which. A point inside one of
    /// <paramref name="portals"/>' circles is refused (walking there would take the portal).
    /// </summary>
    public static Result Path(TilePathFinder mesh, IReadOnlyList<WorldPt> given, NavRouteType type, double spacing,
        IReadOnlyList<PortalAvoid.Spot> portals)
    {
        var r = new Result { Centre = given.Count > 0 ? given[0] : default, Asked = given.Count };
        mesh.NoGaps = true;
        var ground = new List<WorldPt>(given.Count);
        for (int i = 0; i < given.Count; i++)
        {
            WorldPt p = given[i];
            if (!mesh.LoadArea(p.X - SnapUnits - 2, p.Y - SnapUnits - 2, p.X + SnapUnits + 2, p.Y + SnapUnits + 2)
                || !mesh.Snap(p, SnapUnits, out WorldPt g, out _))
                return r.Fail($"point {i + 1} ({At(p)}) has no walkable ground within {SnapUnits:F0} yd (no navmesh there, water, or inside a wall)");
            int pi = NearestPortal(portals, g.X, g.Y, out double pd);
            if (pi >= 0 && pd < portals[pi].R)
                return r.Fail($"point {i + 1} ({At(p)}) is {pd:F1} yd from the portal {(portals[pi].Name.Length > 0 ? portals[pi].Name : "there")}: walking there would take it");
            ground.Add(g);
        }
        int legs = type == NavRouteType.Circular ? ground.Count : ground.Count - 1;
        for (int i = 0; i < legs; i++)
        {
            int j = (i + 1) % ground.Count;
            List<WorldPt>? leg = mesh.Walk(ground[i], ground[j], false, out string note);
            if (leg == null || leg.Count < 2)
                return r.Fail($"leg {i + 1} (point {i + 1} {At(ground[i])} to point {j + 1} {At(ground[j])}) can't be walked: {(note.Length > 0 ? note : "no path on the navmesh")}");
            // The walk ends on the navmesh beside the point; the point (on the ground) is where it goes.
            if (leg[0].FlatDistance(ground[i]) > 2.0) leg.Insert(0, ground[i]);
            if (leg[^1].FlatDistance(ground[j]) > 2.0) leg.Add(ground[j]);
            r.Legs.Add(leg);
        }
        r.Points.AddRange(ground);
        r.Route = RouteShape.Assemble(r.Legs, type, spacing, out r.Yards);
        r.Ok = r.Route.Points.Count >= 2;
        if (!r.Ok) r.Error = "the joined legs came to nothing";
        return r;
    }
}

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// The route maker's geometry that needs no navmesh (RynthNavPlugin.RouteMaker.cs, LoopMaker.cs):
/// how many points a loop of a radius gets, the order a loop's points are walked in (round the
/// centre, then untangled so no two legs cross), and the joined legs as a RynthAi nav route (thinned,
/// heights as nav Z, a circular route not repeating its first point). Host-free and Detour-free:
/// the route tests drive it.
/// </summary>
internal static class RouteShape
{
    /// <summary>A loop's legs get a point at least this often (yards), so RynthAi's nav follows the ground.</summary>
    public const double LoopSpacing = 30.0;

    /// <summary>A loop's point count when none is asked for: about one every 45 yd of its circle, 4 to 12.</summary>
    public static int DefaultPoints(double radius) =>
        Math.Clamp((int)Math.Round(2 * Math.PI * radius / 45.0), 4, 12);

    /// <summary>Compass bearing (0 = north, clockwise) from <paramref name="c"/> to <paramref name="p"/>, 0..360.</summary>
    public static double Bearing(WorldPt c, WorldPt p)
    {
        double b = Math.Atan2(p.X - c.X, p.Y - c.Y) * 180.0 / Math.PI;
        return b < 0 ? b + 360.0 : b;
    }

    /// <summary>
    /// The order to walk a loop's points in: clockwise round <paramref name="centre"/> from the first
    /// point north of it, then untangled (2-opt: two legs that cross are uncrossed by walking the
    /// stretch between them the other way) until no two straight legs cross. Indexes into
    /// <paramref name="pts"/>.
    /// </summary>
    public static int[] OrderLoop(IReadOnlyList<WorldPt> pts, WorldPt centre)
    {
        int n = pts.Count;
        var order = new List<int>(n);
        for (int i = 0; i < n; i++) order.Add(i);
        order.Sort((a, b) =>
        {
            int c = Bearing(centre, pts[a]).CompareTo(Bearing(centre, pts[b]));
            return c != 0 ? c : pts[a].FlatDistance(centre).CompareTo(pts[b].FlatDistance(centre));
        });
        if (n < 4) return order.ToArray();
        int first = order[0];
        for (int pass = 0; pass < n * n; pass++)
        {
            bool changed = false;
            for (int i = 0; i < n && !changed; i++)
                for (int j = i + 2; j < n && !changed; j++)
                {
                    if (i == 0 && j == n - 1) continue;   // the two legs meet at order[0]
                    WorldPt a = pts[order[i]], b = pts[order[i + 1]], c = pts[order[j]], d = pts[order[(j + 1) % n]];
                    if (!SegmentsCross(a, b, c, d)) continue;
                    order.Reverse(i + 1, j - i);
                    changed = true;
                }
            if (!changed) break;
        }
        int at = order.IndexOf(first);
        var o = new int[n];
        for (int k = 0; k < n; k++) o[k] = order[(at + k) % n];
        return o;
    }

    /// <summary>How many pairs of straight legs of the path cross (<paramref name="closed"/>: the last point joins the first).</summary>
    public static int Crossings(IReadOnlyList<WorldPt> pts, bool closed)
    {
        int n = pts.Count, legs = closed ? n : n - 1, count = 0;
        for (int i = 0; i < legs; i++)
            for (int j = i + 1; j < legs; j++)
            {
                if (j == i + 1 || (closed && i == 0 && j == n - 1)) continue;   // neighbours share a point
                if (SegmentsCross(pts[i], pts[(i + 1) % n], pts[j], pts[(j + 1) % n])) count++;
            }
        return count;
    }

    /// <summary>True when the segments a-b and c-d cross (touching at an end doesn't count).</summary>
    public static bool SegmentsCross(WorldPt a, WorldPt b, WorldPt c, WorldPt d)
    {
        double d1 = Cross(c, d, a), d2 = Cross(c, d, b), d3 = Cross(a, b, c), d4 = Cross(a, b, d);
        return ((d1 > 1e-9 && d2 < -1e-9) || (d1 < -1e-9 && d2 > 1e-9)) && ((d3 > 1e-9 && d4 < -1e-9) || (d3 < -1e-9 && d4 > 1e-9));
    }

    private static double Cross(WorldPt o, WorldPt a, WorldPt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    /// <summary>
    /// The joined legs as a nav route of <paramref name="type"/>: leg k runs from point k to point k+1
    /// (first and last points included), each thinned (corners that matter, a point at least every
    /// <paramref name="spacing"/> yd). A circular route's last leg ends where the first starts, so that
    /// point isn't written twice (RynthAi walks from the last point back to the first).
    /// <paramref name="yards"/> is the walk, a circular route's closing leg included.
    /// </summary>
    public static NavRouteParser Assemble(IReadOnlyList<IReadOnlyList<WorldPt>> legs, NavRouteType type, double spacing, out double yards)
    {
        var route = new NavRouteParser { RouteType = type };
        yards = 0;
        var pts = new List<WorldPt>();
        foreach (IReadOnlyList<WorldPt> leg in legs)
        {
            if (leg.Count == 0) continue;
            List<WorldPt> thin = TripPathBuilder.Thin(leg, TripPathBuilder.ThinTolerance, spacing);
            for (int i = 1; i < thin.Count; i++) yards += thin[i - 1].FlatDistance(thin[i]);
            for (int i = pts.Count == 0 ? 0 : 1; i < thin.Count; i++) pts.Add(thin[i]);
        }
        if (type == NavRouteType.Circular && pts.Count > 2 && pts[^1].FlatDistance(pts[0]) < TripPathBuilder.DuplicateUnits * 4)
            pts.RemoveAt(pts.Count - 1);
        double lastH = 0;
        foreach (WorldPt p in pts)
        {
            if (!double.IsNaN(p.H)) lastH = p.H;
            route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = p.Ns, EW = p.Ew, Z = lastH / 240.0 });
        }
        return route;
    }
}

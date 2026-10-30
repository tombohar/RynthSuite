using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// The two live bugs of 10-01: a goto that ran into a portal it didn't want (the steering check),
// and a goto from an island that walked instead of taking a portal or a recall (walks that can't be
// done over land are left out of the plan).
internal static partial class Program
{
    private static void RunAvoid()
    {
        Run(nameof(IslandNeedsAPortal), IslandNeedsAPortal);
        Run(nameof(IslandNeedsARecall), IslandNeedsARecall);
        Run(nameof(IslandNoWayOut), IslandNoWayOut);
        Run(nameof(SteerRoundAPortalInTheWay), SteerRoundAPortalInTheWay);
        Run(nameof(GoalBesideAnUnwantedPortal), GoalBesideAnUnwantedPortal);
        Run(nameof(StartInsideAPortalCircle), StartInsideAPortalCircle);
        Run(nameof(RowOfPortalsNoFlailing), RowOfPortalsNoFlailing);
        Run(nameof(AvoidSetAndPolygons), AvoidSetAndPolygons);
        Run(nameof(BendStaysOnTheMesh), BendStaysOnTheMesh);
    }

    // An island north of 60N; everything else is one mainland.
    private static bool IslandLand(double ans, double aew, double bns, double bew) => (ans > 60) == (bns > 60);

    private static void IslandNeedsAPortal()
    {
        // On the island (61N), going to 59N on the mainland: 2 degrees of walking (480 u) is far
        // cheaper on paper than the island's portal (about 770 u), but it's across water.
        var portals = new List<PortalLink>
        {
            new(62, 47, 59.5, 47.5, "Island Exit"),             // island -> mainland
            new(42.1, 33.6, 84.1, 47.2, "Aerlinthe Island"),    // mainland -> island (no use here)
        };
        PortalRoute.Plan(portals, null, null, 61, 47, 59, 47, out double naiveEst, out int naiveUsed);
        Check(naiveUsed == 0, "without the land check the plan walks straight off the island (the 10-01 bug)");
        var steps = PortalRoute.Plan(portals, null, null, 61, 47, 59, 47, out double est, out int used, IslandLand);
        Check(used == 1 && steps.Count == 2 && steps[0].UsePortal && steps[0].Label == "Island Exit", "with it: walk to the island's portal and take it");
        Check(!double.IsInfinity(est) && est > naiveEst, $"a real plan ({est:F0} vs {naiveEst:F0} straight)");
    }

    private static void IslandNeedsARecall()
    {
        var recalls = new List<RecallLink> { new(42.1, 33.6, "Lifestone Recall", PortalRoute.RecallCostUnits(5)) };
        var steps = PortalRoute.Plan(NoPortals, recalls, null, 61, 47, 59, 47, out _, out int used, IslandLand);
        Check(used == 1 && steps.Count == 2 && steps[0].UseRecall && steps[0].Label == "Lifestone Recall", "no portal off the island: the recall, then walk");
        // On the mainland, the same goto doesn't need it.
        PortalRoute.Plan(NoPortals, recalls, null, 41, 32, 40, 30, out _, out used, IslandLand);
        Check(used == 0, "on the mainland it walks");
    }

    private static void IslandNoWayOut()
    {
        var steps = PortalRoute.Plan(NoPortals, null, null, 61, 47, 59, 47, out double est, out int used, IslandLand);
        Check(steps.Count == 0 && used == 0 && double.IsPositiveInfinity(est), "no portal and no recall: no plan at all (the goto stops and says why)");
        // A portal whose entrance can't be walked to doesn't help either.
        var portals = new List<PortalLink> { new(42.1, 33.6, 58.9, 47.1, "Mainland Portal") };
        PortalRoute.Plan(portals, null, null, 61, 47, 59, 47, out est, out used, IslandLand);
        Check(used == 0 && double.IsPositiveInfinity(est), "a portal on the other landmass isn't reachable");
    }

    // ── Steering round portals: ideal movement, 0.35 u a tick ─────────────────

    private static (double MinDist, bool Arrived, int Ticks) WalkWithAvoid(PortalAvoid avoid, double x, double y, double gx, double gy,
        double px, double py, double arrive = 7.0, int maxTicks = 2000)
    {
        double minDist = double.MaxValue;
        for (int t = 0; t < maxTicks; t++)
        {
            minDist = Math.Min(minDist, Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py)));
            double dg = Math.Sqrt((gx - x) * (gx - x) + (gy - y) * (gy - y));
            if (dg < arrive) return (minDist, true, t);
            double want = Math.Atan2(gx - x, gy - y) * 180.0 / Math.PI;
            if (want < 0) want += 360;
            double h = avoid.Steer(x, y, want, Math.Min(10.0, dg + 2.0), out _) * Math.PI / 180.0;
            x += Math.Sin(h) * 0.35; y += Math.Cos(h) * 0.35;
        }
        return (minDist, false, maxTicks);
    }

    private static void SteerRoundAPortalInTheWay()
    {
        var avoid = new PortalAvoid();
        avoid.Set(new[] { new PortalAvoid.Spot(100, 120, PortalAvoid.LiveRadius, "Aerlinthe Island") }, Array.Empty<PortalAvoid.Spot>());
        // Straight north from (100, 100) to (100, 160): the portal sits right on the line.
        var r = WalkWithAvoid(avoid, 100, 100, 100, 160, 100, 120);
        Check(r.Arrived, "it still gets there");
        Check(r.MinDist >= PortalAvoid.LiveRadius - 0.05, $"never inside the portal's circle (closest {r.MinDist:F2} u)");
        // Without the portal in the set it walks straight through that spot.
        var none = new PortalAvoid();
        var r0 = WalkWithAvoid(none, 100, 100, 100, 160, 100, 120);
        Check(r0.MinDist < 0.5, "with no portal known it would have walked through it");
        // A portal off to the side isn't steered round.
        double h = avoid.Steer(90, 100, 0, 10, out int spot);
        Check(spot < 0 && h == 0, "a portal 10 u to the side: the heading is kept");
    }

    private static void GoalBesideAnUnwantedPortal()
    {
        var avoid = new PortalAvoid();
        avoid.Set(new[] { new PortalAvoid.Spot(100, 150, PortalAvoid.LiveRadius, "Unwanted") }, Array.Empty<PortalAvoid.Spot>());
        // The goal is 1 u from the portal, approached head on through it.
        var r = WalkWithAvoid(avoid, 100, 100, 100, 151, 100, 150);
        Check(r.Arrived, "arrives (within 7 u of the goal)");
        Check(r.MinDist >= PortalAvoid.LiveRadius - 0.05, $"without stepping into the portal (closest {r.MinDist:F2} u)");
        // From the far side too.
        r = WalkWithAvoid(avoid, 100, 200, 100, 151, 100, 150);
        Check(r.Arrived && r.MinDist >= PortalAvoid.LiveRadius - 0.05, $"from the other side too (closest {r.MinDist:F2} u)");
        // The step's own target portal is never avoided.
        var own = new PortalAvoid();
        own.Set(new[] { new PortalAvoid.Spot(100, 150, PortalAvoid.LiveRadius, "Target") }, Array.Empty<PortalAvoid.Spot>(), 100, 150, 15);
        Check(own.Count == 0, "the step's own portal is left out of the set");
        var into = WalkWithAvoid(own, 100, 100, 100, 150, 100, 150, arrive: 0.5);
        Check(into.Arrived && into.MinDist < 0.5, "and the walker goes straight into it");
    }

    private static void StartInsideAPortalCircle()
    {
        var avoid = new PortalAvoid();
        avoid.Set(new[] { new PortalAvoid.Spot(100, 100, PortalAvoid.LiveRadius, "Just landed beside it") }, Array.Empty<PortalAvoid.Spot>());
        // Standing 1 u south of a portal, wanting to go north through it.
        double h = avoid.Steer(100, 99, 0, 10, out int spot);
        Check(spot == 0 && (h > 90 && h < 270), $"inside the circle: heads out of it first, not on through ({h:F0} deg)");
        var r = WalkWithAvoid(avoid, 100, 99, 100, 140, 100, 100);
        Check(r.Arrived, "then on to the goal");
    }

    private static void RowOfPortalsNoFlailing()
    {
        // Holtburg 10-01 19:46: four hub portals in a row ~5 u apart; the lifestone drops you inside
        // the row's circles and the goal is beyond it. Get out and round the row without flailing.
        var avoid = new PortalAvoid();
        var row = new[] { 95.2, 100.0, 104.8, 109.6 };
        var spots = new System.Collections.Generic.List<PortalAvoid.Spot>();
        foreach (double rx in row) spots.Add(new PortalAvoid.Spot(rx, 100, PortalAvoid.LiveRadius, "hub"));
        avoid.Set(spots, Array.Empty<PortalAvoid.Spot>());
        double x = 101.5, y = 98.6, gx = 101, gy = 160, last = double.NaN;
        int bigFlips = 0; bool arrived = false; double minClear = double.MaxValue; int ticksInside = 0;
        for (int t = 0; t < 3000 && !arrived; t++)
        {
            double dg = Math.Sqrt((gx - x) * (gx - x) + (gy - y) * (gy - y));
            if (dg < 7.0) { arrived = true; break; }
            double want = Math.Atan2(gx - x, gy - y) * 180.0 / Math.PI; if (want < 0) want += 360;
            double h = avoid.Steer(x, y, want, Math.Min(10.0, dg + 2.0), out _);
            if (!double.IsNaN(last)) { double dh = Math.Abs(h - last); if (dh > 180) dh = 360 - dh; if (dh > 120) bigFlips++; }
            last = h;
            bool inside = avoid.Inside(x, y) >= 0;
            if (inside) ticksInside++;
            else foreach (double rx in row) minClear = Math.Min(minClear, Math.Sqrt((x - rx) * (x - rx) + (y - 100) * (y - 100)) - PortalAvoid.LiveRadius);
            double hr = h * Math.PI / 180.0; x += Math.Sin(hr) * 0.35; y += Math.Cos(hr) * 0.35;
        }
        Check(arrived, "gets round the row to the goal");
        Check(bigFlips <= 1, $"no flailing: {bigFlips} heading flips over 120 deg");
        Check(ticksInside < 40, $"out of the circles quickly ({ticksInside} ticks inside)");
        Check(minClear > -0.05, $"once out, never back into a circle (closest {minClear:F2} u past an edge)");
    }

    private static void AvoidSetAndPolygons()
    {
        var avoid = new PortalAvoid();
        var live = new[] { new PortalAvoid.Spot(10, 10, PortalAvoid.LiveRadius, "live") };
        var map = new[] { new PortalAvoid.Spot(12, 11, PortalAvoid.MapRadius, "the same one from the Atlas"), new PortalAvoid.Spot(50, 50, PortalAvoid.MapRadius, "another") };
        avoid.Set(live, map);
        Check(avoid.Count == 2 && avoid.Spots[0].R == PortalAvoid.LiveRadius, "a live portal wins over its Atlas entry; others stay");
        Check(avoid.Inside(11, 11) == 0 && avoid.Inside(20, 20) == -1, "inside / outside");
        // A square 0..20 contains the live portal; a square far away doesn't touch anything.
        Check(avoid.Touches(new double[] { 0, 20, 20, 0 }, new double[] { 0, 0, 20, 20 }), "a polygon around a portal touches it");
        Check(avoid.Touches(new double[] { 12.0, 30, 30, 12.0 }, new double[] { 0, 0, 30, 30 }), "a polygon 2 u from it touches its circle");
        Check(!avoid.Touches(new double[] { 20, 40, 40, 20 }, new double[] { 0, 0, 8, 8 }), "a polygon well clear of both doesn't");
        avoid.Clear();
        Check(avoid.Count == 0 && avoid.Steer(0, 0, 45, 10, out _) == 45, "no portals: nothing changes");
    }

    // C9A8, 10-01 21:03: the bend round a housing portal put its corner beside a tree, in the
    // navmesh's no-go ring round the trunk; the walker went there and stuck. A corner must now be
    // on the mesh and so must both legs to and from it; with no such corner the path is kept.
    private static void BendStaysOnTheMesh()
    {
        var avoid = new PortalAvoid();
        avoid.Set(new[] { new PortalAvoid.Spot(50, 0.5, PortalAvoid.LiveRadius, "housing portal") }, Array.Empty<PortalAvoid.Spot>());
        var path = new List<(double, double)> { (0, 0), (100, 0) };
        // The bend's natural side is south (the path passes south of the portal's centre).
        double off = PortalAvoid.LiveRadius + PortalAvoid.SteerMargin + 0.5;

        // Open ground: it bends, on the side the path passes.
        var open = avoid.Bend(path, (x, y) => true, 12, (ax, ay, bx, by) => true);
        Check(open.Count == 3 && open[1].Y < 0, "open ground: one corner, on the near (south) side");

        // A tree's no-go ring (2 m; its trunk 1 m south of the corner) covers the south corner: north instead.
        bool NoTreeSouth(double x, double y) => Math.Sqrt((x - 50) * (x - 50) + (y + off + 1) * (y + off + 1)) > 2.0;
        bool Leg(Func<double, double, bool> mesh, double ax, double ay, double bx, double by)
        {
            int n = (int)Math.Ceiling(Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
            for (int k = 1; k < n; k++) if (!mesh(ax + (bx - ax) * k / n, ay + (by - ay) * k / n)) return false;
            return true;
        }
        var north = avoid.Bend(path, NoTreeSouth, 12, (ax, ay, bx, by) => Leg(NoTreeSouth, ax, ay, bx, by));
        Check(north.Count == 3 && north[1].Y > 0, "a tree on the near side: the corner goes on the far side");

        // The old test (any navmesh within 1.5 m) took the south corner beside the tree.
        bool Near(double x, double y) { for (double dx = -1.5; dx <= 1.5; dx += 0.5) for (double dy = -1.5; dy <= 1.5; dy += 0.5) if (NoTreeSouth(x + dx, y + dy)) return true; return false; }
        var old = avoid.Bend(path, Near);
        Check(old.Count == 3 && old[1].Y < 0 && !NoTreeSouth(old[1].X, old[1].Y), "(the old 'near the mesh' test put the corner inside the tree's ring)");

        // Corners on the mesh on both sides, but each leg to them crosses a no-go area: kept as it was.
        bool Walls(double x, double y) => !(Math.Abs(x - 25) < 1 && Math.Abs(y) > 2) && !(Math.Abs(x - 75) < 1 && Math.Abs(y) > 2);
        var kept = avoid.Bend(path, Walls, 12, (ax, ay, bx, by) => Leg(Walls, ax, ay, bx, by));
        Check(kept.Count == 2 && kept[0] == path[0] && kept[1] == path[1], "no corner with legs on the mesh: the path is kept (steering takes over)");

        // Neither side on the mesh at all: kept too.
        var none = avoid.Bend(path, (x, y) => Math.Abs(y) < 1, 12, (ax, ay, bx, by) => true);
        Check(none.Count == 2, "no corner on the mesh: the path is kept");
    }
}

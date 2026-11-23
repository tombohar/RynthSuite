using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// 11-19, a Conquest player: a goto to coords by the Paradox-touched Olthoi tents "tried to run around the
// mountains from mountain shortcut". With the recall known, the planner still took Mountain Shortcut for
// goals 1-2 degrees south of the tents: its landing is 300-700 u from them in a straight line and about
// 8,500 u on foot round the mountains. Walks are now costed on the route graph (PortalRoute's walkCost,
// the plugin's RealWalk), lazily: the plan's own walks, until the plan holds. Coordinates from the grid
// probe on the live data (the landings, the entrance, one goal it got wrong); the walks are made up.
internal static partial class Program
{
    private static void RunRealWalks()
    {
        Run(nameof(MountainShortcutOverTheRidge), MountainShortcutOverTheRidge);
        Run(nameof(WalkingAllTheWayCostedForReal), WalkingAllTheWayCostedForReal);
        Run(nameof(UnknownWalksKeepTheStraightLine), UnknownWalksKeepTheStraightLine);
        Run(nameof(RealWalksStayWithinBudget), RealWalksStayWithinBudget);
        Run(nameof(TravelPlanPassesRealWalks), TravelPlanPassesRealWalks);
        Run(nameof(FarWalkAllKeepsItsStraightLine), FarWalkAllKeepsItsStraightLine);
        Run(nameof(BestWayIsCostedBeforeTheOthers), BestWayIsCostedBeforeTheOthers);
    }

    // Mountain Shortcut: entrance 34.89N 56.01E, lands 40.98N 56.03E. The recall lands 43.76N 54.94E.
    private static readonly PortalLink Shortcut = new(34.89, 56.01, 40.98, 56.03, "Mountain Shortcut");
    private static readonly RecallLink OlthoiRecall = new(43.76, 54.94, "Paradox-touched Olthoi Infested Area Recall", PortalRoute.RecallCostUnits(5));
    private const double TentsGoalNs = 42.2, TentsGoalEw = 55.5;   // on the tents' plateau, south of the landing

    /// <summary>The ridge: from Mountain Shortcut's landing (or its foot) onto the plateau is a long way round.</summary>
    private static double RidgeWalk(double ans, double aew, double bns, double bew)
    {
        double straight = NavCoords.Distance(ans, aew, bns, bew);
        bool southA = ans < 41.6, southB = bns < 41.6;   // the ridge runs east-west at about 41.6N here
        bool plateauA = ans >= 41.6 && aew < 57.5, plateauB = bns >= 41.6 && bew < 57.5;
        if ((southA && plateauB) || (plateauA && southB)) return straight + 8200;
        return straight * 1.1;
    }

    private static void MountainShortcutOverTheRidge()
    {
        var portals = new[] { Shortcut };
        var recalls = new[] { OlthoiRecall };
        List<RouteStep> before = PortalRoute.Plan(portals, recalls, null, 34.95, 56.0, TentsGoalNs, TentsGoalEw, out double estBefore, out _);
        Check(before.Any(s => s.UsePortal && s.Label == "Mountain Shortcut"), $"straight lines: Mountain Shortcut ({estBefore:F0}u), the 11-19 mistake");

        int calls = 0;
        double Real(double a, double b, double c, double d) { calls++; return RidgeWalk(a, b, c, d); }
        List<RouteStep> after = PortalRoute.Plan(portals, recalls, null, 34.95, 56.0, TentsGoalNs, TentsGoalEw, out double est, out int used,
            walkCost: Real);
        Console.WriteLine($"     with the walks costed: {string.Join(" | ", after.Select(s => s.UseRecall ? "recall" : s.UsePortal ? "portal " + s.Label : "walk"))}, ~{est:F0}u, {calls} walks costed");
        Check(after.Count == 2 && after[0].UseRecall && after[0].Label == OlthoiRecall.Name && used == 1, "the recall, then the walk to the goal");
        double expect = OlthoiRecall.CostUnits + 1.1 * NavCoords.Distance(OlthoiRecall.DstNs, OlthoiRecall.DstEw, TentsGoalNs, TentsGoalEw);
        Check(Math.Abs(est - expect) < 1, $"its cost has the walk from the landing costed ({est:F0} vs {expect:F0}u)");
        Check(calls is > 0 and <= 4, $"a handful of walks costed, not every one ({calls})");
    }

    private static void WalkingAllTheWayCostedForReal()
    {
        // Standing at Mountain Shortcut's landing: 330 u straight to the goal, so it walked (8,500 u really).
        var portals = new[] { Shortcut };
        var recalls = new[] { OlthoiRecall };
        List<RouteStep> before = PortalRoute.Plan(portals, recalls, null, 40.98, 56.03, TentsGoalNs, TentsGoalEw, out double e0, out int u0);
        Check(u0 == 0, $"straight lines: walk it ({e0:F0}u)");
        List<RouteOption> ways = PortalRoute.PlanOptions(portals, recalls, null, 40.98, 56.03, TentsGoalNs, TentsGoalEw, 3, walkCost: RidgeWalk);
        Check(ways.Count >= 2 && ways[0].Steps[0].UseRecall, "costed: the recall first");
        RouteOption? walk = ways.FirstOrDefault(o => o.Teleports == 0);
        Check(walk != null && walk.Est > 8000, $"walking all the way is offered at its real cost ({walk?.Est ?? 0:F0}u)");
    }

    private static void UnknownWalksKeepTheStraightLine()
    {
        var portals = new[] { Shortcut };
        var recalls = new[] { OlthoiRecall };
        PortalRoute.Plan(portals, recalls, null, 34.95, 56.0, TentsGoalNs, TentsGoalEw, out double straight, out _);
        List<RouteStep> s = PortalRoute.Plan(portals, recalls, null, 34.95, 56.0, TentsGoalNs, TentsGoalEw, out double est, out _,
            walkCost: (_, _, _, _) => double.NaN);
        Check(s.Any(x => x.UsePortal) && Math.Abs(est - straight) < 1e-6, "a walk the route graph can't cost keeps the straight line");
        // Cheaper than the straight line (a rounding of the graph's points): never under it.
        PortalRoute.Plan(portals, recalls, null, 34.95, 56.0, TentsGoalNs, TentsGoalEw, out double est2, out _,
            walkCost: (a, b, c, d) => NavCoords.Distance(a, b, c, d) * 0.5);
        Check(Math.Abs(est2 - straight) < 1e-6, "a real walk is never counted shorter than the straight line");
    }

    private static void RealWalksStayWithinBudget()
    {
        // A field of portals, every walk much dearer than its straight line: the plan keeps re-planning,
        // but costs at most MaxRealWalks walks.
        var rnd = new Random(1119);
        var portals = new List<PortalLink>();
        for (int i = 0; i < 80; i++)
            portals.Add(new PortalLink(rnd.NextDouble() * 20, rnd.NextDouble() * 20, rnd.NextDouble() * 20, rnd.NextDouble() * 20, "p" + i));
        int calls = 0;
        List<RouteStep> s = PortalRoute.Plan(portals, null, null, 0, 0, 20, 20, out double est, out _,
            walkCost: (a, b, c, d) => { calls++; return NavCoords.Distance(a, b, c, d) * (1.5 + (calls % 7)); });
        Check(calls <= PortalRoute.MaxRealWalks, $"at most {PortalRoute.MaxRealWalks} walks costed ({calls})");
        Check(s.Count > 0 && double.IsFinite(est), "and still a plan");
    }

    private static void FarWalkAllKeepsItsStraightLine()
    {
        // 11-20 live: costing a 39,000 u walk all the way took 130-190 ms, the plan's whole budget, for an
        // option twelve times the best way's cost. Far past the best way, it keeps its straight line. Three
        // portals and three ways: each way still has a portal, so no way's own plan is the walk.
        var portals = new[]
        {
            new PortalLink(0.0, 0.1, 19.9, 20.0, "Far Portal"),
            new PortalLink(0.1, 0.0, 20.0, 19.9, "Other Portal"),
            new PortalLink(0.1, 0.1, 19.9, 19.9, "Third Portal"),
        };
        double straight = NavCoords.Distance(0, 0, 20, 20);
        int walkAll = 0, calls = 0;
        double Real(double a, double b, double c, double d)
        {
            calls++;
            if (a == 0 && b == 0 && c == 20 && d == 20) walkAll++;
            return NavCoords.Distance(a, b, c, d) * 1.2;
        }
        List<RouteOption> ways = PortalRoute.PlanOptions(portals, null, null, 0, 0, 20, 20, 3, walkCost: Real);
        Check(ways.Count == 3 && ways.All(o => o.Teleports == 1), "three ways, each by a portal");
        Check(straight >= PortalRoute.WalkAllRealFactor * ways[0].Est, $"(the case: walking all the way, {straight:F0}u, is past {PortalRoute.WalkAllRealFactor}x the best way, {ways[0].Est:F0}u)");
        Check(walkAll == 0, $"walking all the way isn't costed on the route graph ({calls} walks costed)");
        // Within the factor it is (WalkingAllTheWayCostedForReal: 330 u against a 1,000 u recall).
    }

    private static void BestWayIsCostedBeforeTheOthers()
    {
        // RealWalk's budgets: the best way's walks first, then bestPlanned, then the other ways'. Standing at
        // Mountain Shortcut's landing (WalkingAllTheWayCostedForReal), walking straight there is the best way
        // by straight lines, so it is costed with the best way (8,500 u: the recall wins).
        var portals = new[] { Shortcut };
        var recalls = new[] { OlthoiRecall };
        var events = new List<string>();
        double Real(double a, double b, double c, double d)
        {
            bool all = Math.Abs(a - 40.98) < 1e-9 && Math.Abs(b - 56.03) < 1e-9 && Math.Abs(c - TentsGoalNs) < 1e-9 && Math.Abs(d - TentsGoalEw) < 1e-9;
            events.Add(all ? "all" : "w");
            return RidgeWalk(a, b, c, d);
        }
        List<RouteOption> ways = PortalRoute.PlanOptions(portals, recalls, null, 40.98, 56.03, TentsGoalNs, TentsGoalEw, 3,
            walkCost: Real, bestPlanned: () => events.Add("B"));
        int b = events.IndexOf("B");
        Console.WriteLine($"     order: {string.Join(" ", events)}");
        Check(events.Count(e => e == "B") == 1, "bestPlanned once");
        Check(b > 0 && events.Take(b).Contains("all") && events.Take(b).Contains("w"),
              "the best way's walks are costed before it (the walk there, then the recall's landing to the tents)");
        Check(events.Count > b + 1, "the other ways' after it");
        Check(ways.Count >= 2 && ways[0].Steps[0].UseRecall, "and the plan is the same: the recall first");
        // TravelPlan passes it on.
        int told = 0;
        TravelPlan.Plan(portals, recalls, null, true, true, true, 34.95, 56.0, TentsGoalNs, TentsGoalEw,
            null, true, "the tents", null, 2, RidgeWalk, null, () => told++);
        Check(told == 1, "TravelPlan passes bestPlanned on");
    }

    private static void TravelPlanPassesRealWalks()
    {
        var portals = new[] { Shortcut };
        var recalls = new[] { OlthoiRecall };
        TravelPlan.Result r = TravelPlan.Plan(portals, recalls, null, true, true, true, 34.95, 56.0, TentsGoalNs, TentsGoalEw,
            null, true, "the tents", null, 2, RidgeWalk);
        Check(r.Steps != null && r.Steps[0].UseRecall, "TravelPlan: the recall when the walks are costed");
        TravelPlan.Result off = TravelPlan.Plan(portals, recalls, null, true, false, true, 34.95, 56.0, TentsGoalNs, TentsGoalEw,
            null, true, "the tents", null, 2, RidgeWalk);
        Check(off.Steps != null && off.Steps.Any(x => x.UsePortal) && off.Est > 8000,
              $"recalls off: Mountain Shortcut still, at its real cost ({off.Est:F0}u)");
    }
}

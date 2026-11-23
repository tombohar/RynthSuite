using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthNav;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// Wave 2 (11-19): teleports costed by the time they take (TripTimes), measured as you go (TripClock), and
// the trip's time: at plan time (Est / RunSpeed) and while walking (RouteStep.EstAfter: what is left).
internal static partial class Program
{
    private static void RunTripTimes()
    {
        Run(nameof(TeleportsCostTheirTime), TeleportsCostTheirTime);
        Run(nameof(RecallsCostTheirCast), RecallsCostTheirCast);
        Run(nameof(StepsCarryWhatIsLeft), StepsCarryWhatIsLeft);
        Run(nameof(ClockLearnsRunSpeed), ClockLearnsRunSpeed);
        Run(nameof(ClockLearnsTeleports), ClockLearnsTeleports);
        Run(nameof(TimesSaidPlainly), TimesSaidPlainly);
        Run(nameof(EnteringAPortalKeepsWhatIsLeft), EnteringAPortalKeepsWhatIsLeft);
    }

    private static void TeleportsCostTheirTime()
    {
        var t = new TripTimes();
        Check(Math.Abs(t.PortalUnits - 12.5 * 11.7) < 0.01, $"a portal costs 5 s at it and 7.5 s of portal space, at 11.7 u/s ({t.PortalUnits:F0}u)");
        // A portal that saves 336 u of running in all, 24 u of it walked: the flat 360 u penalty walks the
        // 360 u, its real 12.5 s (146 u) takes it.
        var portals = new[] { new PortalLink(0.0, 0.05, 0.0, 1.45, "Shortcut") };
        double walk = NavCoords.Distance(0, 0, 0, 1.5);
        PortalRoute.Plan(portals, null, null, 0, 0, 0, 1.5, out double flatEst, out int flatUsed);
        Check(flatUsed == 0 && Math.Abs(flatEst - walk) < 1, $"flat costs: walks {walk:F0}u rather than a portal at 360u");
        List<RouteStep> s = PortalRoute.Plan(portals, null, null, 0, 0, 0, 1.5, out double est, out int used, times: t);
        Check(used == 1 && s[0].UsePortal, "teleports at their time: takes the portal");
        Check(Math.Abs(est - (12 + t.PortalUnits + 12)) < 1, $"its cost is the two walks and the portal's time ({est:F0}u, {t.Seconds(est):F0} s)");
    }

    private static void RecallsCostTheirCast()
    {
        var t = new TripTimes { RunSpeed = 10, SpellCastSeconds = 2, CommandCastSeconds = 14, SpaceSeconds = 8 };
        var spell = new RecallOption { Name = "Recall Aphus Lassel", SpellId = 2931, CastSeconds = RecallPlanner.SpellCastSeconds };
        var command = new RecallOption { Name = "Lifestone (@lifestone)", Command = "@lifestone", CastSeconds = RecallPlanner.CommandRecallSeconds };
        Check(Math.Abs(spell.ToLink(t).CostUnits - 100) < 1e-9, "a recall spell: (cast 2 s + portal space 8 s) x 10 u/s");
        Check(Math.Abs(command.ToLink(t).CostUnits - 220) < 1e-9, "a command recall: (14 s + 8 s) x 10 u/s");
        Check(Math.Abs(spell.ToLink().CostUnits - PortalRoute.RecallCostUnits(5)) < 1e-9, "no times: the flat cost as before");
        var links = RecallPlanner.Links(new List<RecallOption> { spell, command }, t);
        Check(links.Count == 2 && links[1].CostUnits > links[0].CostUnits, "Links passes the times on");
    }

    private static void StepsCarryWhatIsLeft()
    {
        var t = new TripTimes();
        var portals = new[] { new PortalLink(0.0, 0.5, 5.0, 5.0, "Far Portal"), new PortalLink(5.1, 5.0, 9.0, 9.0, "Next Portal") };
        var recalls = new[] { new RecallLink(0.0, 0.6, "Short Recall", t.RecallUnits(true)) };
        List<RouteStep> s = PortalRoute.Plan(portals, recalls, null, 0, 0, 9.2, 9.2, out double est, out int used, times: t);
        Console.WriteLine($"     {string.Join(" | ", s.Select(x => (x.UseRecall ? "recall" : x.UsePortal ? "portal " + x.Label : "walk") + $" @{x.EstAfter:F0}"))}; est {est:F0}u");
        Check(s.Count >= 2 && s.All(x => x.EstAfter > 0), "every step says what the plan cost up to its end");
        bool rising = true;
        for (int i = 1; i < s.Count; i++) rising &= s[i].EstAfter >= s[i - 1].EstAfter;
        Check(rising && Math.Abs(s[^1].EstAfter - est) < 1e-6, "rising to the plan's cost at the last step");
    }

    private static void ClockLearnsRunSpeed()
    {
        var c = new TripClock();
        Check(Math.Abs(c.Times.RunSpeed - 11.7) < 1e-9, "starts at 11.7 u/s");
        long now = 1_000_000; double x = 0;
        for (int i = 0; i < 30 * 20; i++) { now += 33; x += 13.2 * 0.033; c.RunTick(now, x, 0, true); }   // 20 s at 13.2 u/s
        Check(Math.Abs(c.Times.RunSpeed - 13.2) < 0.3, $"learns this character's 13.2 u/s ({c.Times.RunSpeed:F2})");
        // Against a wall (1 u/s) and stops: left out.
        for (int i = 0; i < 30 * 20; i++) { now += 33; x += 1 * 0.033; c.RunTick(now, x, 0, true); }
        Check(c.Times.RunSpeed > 12.5, $"blocked running doesn't drag it down ({c.Times.RunSpeed:F2})");
        c.RunTick(now + 33, x + 60, 0, true);   // a teleport mid-run: not a run
        Check(c.Times.RunSpeed < 14, "a jump isn't a run");
        TripTimes before = c.Times;
        before.RunSpeed = 99;
        Check(Math.Abs(c.Times.RunSpeed - 99) > 1, "a plan's times are its own copy");
        c.Reset();
        Check(Math.Abs(c.Times.RunSpeed - 11.7) < 1e-9 && c.RunReadings == 0, "another character: the defaults again");
    }

    private static void ClockLearnsTeleports()
    {
        var c = new TripClock();
        for (int i = 0; i < 12; i++) { c.Portal(6.5, 14); c.Recall(1.7, spell: true); c.Space(7.7); }
        TripTimes t = c.Times;
        Check(Math.Abs(t.PortalSeconds - (6.5 - 14 / 11.7)) < 0.1, $"a portal: 6.5 s from 14 u out, less the walk ({t.PortalSeconds:F2} s)");
        Check(Math.Abs(t.SpellCastSeconds - 1.7) < 0.1 && Math.Abs(t.SpaceSeconds - 7.7) < 0.1, $"a recall's cast and portal space ({t.SpellCastSeconds:F2}, {t.SpaceSeconds:F2})");
        Check(Math.Abs(t.CommandCastSeconds - 14) < 1e-9, "a spell's reading leaves the command recalls alone");
        c.Portal(95, 14); c.Recall(41, spell: true); c.Space(0.2);
        Check(Math.Abs(c.Times.PortalSeconds - t.PortalSeconds) < 1e-9 && Math.Abs(c.Times.SpellCastSeconds - t.SpellCastSeconds) < 1e-9
              && Math.Abs(c.Times.SpaceSeconds - t.SpaceSeconds) < 1e-9, "readings no trip can take are left out");
        Check(c.Describe().Contains("portal space 7.7 s", StringComparison.Ordinal), $"says it: {c.Describe()}");
    }

    private static void TimesSaidPlainly()
    {
        Check(TripTimes.About(42) == "about 40 s", TripTimes.About(42));
        Check(TripTimes.About(2) == "about 5 s", TripTimes.About(2));
        Check(TripTimes.About(250) == "about 4 min", TripTimes.About(250));
        Check(TripTimes.About(3900) == "about 1 h 5 min", TripTimes.About(3900));
        Check(TripTimes.About(double.PositiveInfinity) == "" && TripTimes.About(-1) == "", "none for no trip");
    }

    private static void EnteringAPortalKeepsWhatIsLeft()
    {
        var route = new List<RouteStep>
        {
            new RouteStep(0, 0, false, true, "A Recall", 0, 1, 1) { EstAfter = 146 },
            new RouteStep(1.5, 1.5, false, false, "walk") { EstAfter = 316 },
        };
        List<RouteStep> s = Entrance.EndingInPortal(route, 1.5, 1.5, "Dungeon Portal");
        Check(s.Count == 2 && s[1].UsePortal && Math.Abs(s[1].EstAfter - 316) < 1e-9, "the walk turned into the dungeon's portal keeps its cost");
    }
}

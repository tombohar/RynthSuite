using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// The goto's steering (WalkSteer, PathFollow). 11-01: "whenever I use auto navigation mode (Select
// coordinates) it bounces left and right a lot with a lot of stopping": 862 turns and 21 stops in
// 107 s of one goto. The old servo stopped the run for anything over 20 degrees and each path corner
// was run to within 1.5 m, overshot and turned back to. A simulated character walks made-up paths
// round buildings with the old and the new settings.
internal static partial class Program
{
    private static void RunSteering()
    {
        Run(nameof(ServoSmallCorrectionsOnTheRun), ServoSmallCorrectionsOnTheRun);
        Run(nameof(ServoBigTurnStandsAndResumes), ServoBigTurnStandsAndResumes);
        Run(nameof(ServoTurnRateLimited), ServoTurnRateLimited);
        Run(nameof(PathFollowCutsOnlyInPlainView), PathFollowCutsOnlyInPlainView);
        Run(nameof(UnstickPicksOpenGround), UnstickPicksOpenGround);
        Run(nameof(SimulatedWalkRoundBuildings), SimulatedWalkRoundBuildings);
    }

    private static void ServoSmallCorrectionsOnTheRun()
    {
        var s = new WalkSteer();
        double t = s.Step(5, 0, 1000, out bool run);
        Check(run && double.IsNaN(t), "5 degrees off while running: no turn (dead zone), still running");
        t = s.Step(35, 0, 1100, out run);
        Check(run && !double.IsNaN(t) && t > 0 && t <= 35, $"35 degrees off: turns on the run ({t:F1})");
        var old = WalkSteer.Legacy();
        old.Step(35, 0, 1000, out bool oldRun);
        Check(!oldRun, "the old servo stopped the run for 35 degrees");
        Check(Math.Abs(WalkSteer.Diff(350, 10) + 20) < 1e-9 && Math.Abs(WalkSteer.Diff(10, 350) - 20) < 1e-9, "the shortest way round north");
        t = s.Step(345, 5, 1300, out _);
        Check(!double.IsNaN(t) && (t < 5 || t > 340), $"from 5 to 345 it turns left across north ({t:F1})");
    }

    private static void ServoBigTurnStandsAndResumes()
    {
        var s = new WalkSteer();
        long now = 1000;
        double h = 0;
        s.Step(120, h, now, out bool run);
        Check(!run && s.TurnInPlace, "120 degrees off: stops to turn");
        int ticks = 0;
        while (!run && ticks++ < 200)
        {
            now += 18;
            double t = s.Step(120, h, now, out run);
            if (!double.IsNaN(t)) h = t;
        }
        Check(run && Math.Abs(WalkSteer.Diff(120, h)) <= 20.0 + 1e-9, $"runs again once within 20 degrees ({h:F1} after {ticks} ticks)");
        Check(ticks * 18 < 600, $"a 120 degree turn takes under 0.6 s ({ticks * 18} ms)");
    }

    private static void ServoTurnRateLimited()
    {
        var s = new WalkSteer();
        int sent = 0;
        double h = 0;
        for (long now = 1000; now < 2000; now += 18)
        {
            double t = s.Step(h + 30, h, now, out _);   // a target that keeps moving away
            if (!double.IsNaN(t)) { sent++; h = t; }
        }
        Check(sent <= 21, $"at most 20 turns a second (sent {sent} in 1 s; the old servo: one every tick, 56)");
        var old = WalkSteer.Legacy();
        int oldSent = 0; h = 0;
        for (long now = 1000; now < 2000; now += 18)
        {
            double t = old.Step(h + 30, h, now, out _);
            if (!double.IsNaN(t)) { oldSent++; h = t; }
        }
        Check(oldSent >= 50, $"(the old one sent {oldSent})");
    }

    private static void PathFollowCutsOnlyInPlainView()
    {
        var path = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 10), (20, 10) };
        Check(PathFollow.Advance(path, 1, 6, 0, 1.5, 7, 6, null) == 1, "without a view check: the corner is run to");
        Check(PathFollow.Advance(path, 1, 6, 0, 1.5, 7, 6, (ax, ay, bx, by) => true) == 2, "4 m from it with the next corner in view: on to the next");
        Check(PathFollow.Advance(path, 1, 6, 0, 1.5, 7, 6, (ax, ay, bx, by) => false) == 1, "...not when something is between");
        Check(PathFollow.Advance(path, 1, 2, 0, 1.5, 7, 6, (ax, ay, bx, by) => true) == 1, "not from farther than the look-ahead");
        Check(PathFollow.Advance(path, 1, 9, 0, 1.5, 7, 6, null) == 2, "within the corner reach: passed");
        Check(PathFollow.Advance(path, 3, 15, 10, 1.5, 7, 6, (ax, ay, bx, by) => true) == 4, "the end is reached at the arrival distance");
    }

    private static void UnstickPicksOpenGround()
    {
        Check(Math.Abs(WalkSteer.UnstickHeading(0, 1, null) - 100) < 1e-9, "no view check: 100 degrees off on the given side");
        Check(Math.Abs(WalkSteer.UnstickHeading(0, -1, h => true) - 260) < 1e-9, "the other side when asked");
        // A river bank on the right (every heading from 30 to 180 blocked): the left.
        double left = WalkSteer.UnstickHeading(0, 1, h => !(h >= 30 && h <= 180));
        Check(Math.Abs(left - 260) < 1e-9, $"open ground on the other side ({left:F0})");
        Check(Math.Abs(WalkSteer.UnstickHeading(90, 1, h => false) - 270) < 1e-9, "nothing open: straight back");
    }

    // ── A simulated walk ───────────────────────────────────────────────────────────

    private sealed record Building(double X, double Y, double R);

    private sealed class WalkResult
    {
        public bool Arrived;
        public double Seconds, Travelled, MinClearance = double.MaxValue;
        public int Turns, Stops;
    }

    /// <summary>
    /// A path like Detour's: every other corner turns 25..45 degrees round a round building, both
    /// legs tangent to its eroded edge (building radius + the 2 m agent radius); the corners
    /// between are 5..15 degree bends on open ground (polygon edges), nothing near.
    /// </summary>
    private static (List<(double X, double Y)> Path, List<Building> Buildings) WeavingPath()
    {
        var path = new List<(double X, double Y)> { (0, 0) };
        var buildings = new List<Building>();
        var rng = new Random(11_01);
        double x = 0, y = 0, heading = 45;
        for (int i = 0; i < 30; i++)
        {
            double leg = 6 + rng.NextDouble() * 8;
            x += Math.Sin(heading * Math.PI / 180) * leg; y += Math.Cos(heading * Math.PI / 180) * leg;
            path.Add((x, y));
            int sign = (i / 2) % 2 == 0 ? 1 : -1;
            bool round = i % 2 == 0;
            double turn = sign * (round ? 25 + rng.NextDouble() * 20 : 5 + rng.NextDouble() * 10);
            if (round)
            {
                // The building on the inside of the turn, tangent to both legs at the agent radius.
                double inner = heading + (turn > 0 ? 90 : -90) + turn / 2;
                double r = 3 + rng.NextDouble() * 3;
                double d = (r + 2) / Math.Cos(Math.Abs(turn) / 2 * Math.PI / 180);
                buildings.Add(new Building(x + Math.Sin(inner * Math.PI / 180) * d, y + Math.Cos(inner * Math.PI / 180) * d, r));
            }
            heading += turn;
        }
        x += Math.Sin(heading * Math.PI / 180) * 20; y += Math.Cos(heading * Math.PI / 180) * 20;
        path.Add((x, y));
        return (path, buildings);
    }

    private static double SegDist(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
        double t = l2 < 1e-12 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / l2, 0, 1);
        double cx = ax + dx * t, cy = ay + dy * t;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    /// <summary>
    /// Walks the path at 30 ticks a second: the heading is what the last turn set (TurnToHeading is
    /// instant), a run covers 10 u/s, a stop halts at once, and a run takes 0.4 s to get going again
    /// (in the 11-01 log a stop and the run after it are 0.3..0.6 s apart).
    /// </summary>
    private static WalkResult Walk(List<(double X, double Y)> path, List<Building> buildings, WalkSteer servo, bool lookahead)
    {
        const long TickMs = 33;
        const double RunSpeed = 10.0, AgentRadius = 2.0;
        const long StartUpMs = 400;
        bool Clear(double ax, double ay, double bx, double by)
        {
            foreach (Building b in buildings) if (SegDist(b.X, b.Y, ax, ay, bx, by) < b.R + AgentRadius - 1e-6) return false;
            return true;
        }
        var res = new WalkResult();
        double x = path[0].X, y = path[0].Y;
        double h = Math.Atan2(path[1].X - x, path[1].Y - y) * 180 / Math.PI;
        int idx = 0;
        bool running = false;
        long runSince = 0;
        long now = 1_000_000;
        for (int tick = 0; tick < 30 * 180; tick++)
        {
            now += TickMs;
            idx = PathFollow.Advance(path, idx, x, y, 1.5, 7.0, 6.0, lookahead ? Clear : null);
            if (idx >= path.Count) { res.Arrived = true; break; }
            double desired = WalkSteer.Norm(Math.Atan2(path[idx].X - x, path[idx].Y - y) * 180 / Math.PI);
            double t = servo.Step(desired, h, now, out bool run);
            if (!double.IsNaN(t)) { res.Turns++; h = t; }
            if (running && !run) res.Stops++;
            if (!running && run) runSince = now;
            running = run;
            double step = running && now - runSince >= StartUpMs ? RunSpeed * TickMs / 1000.0 : 0;
            x += Math.Sin(h * Math.PI / 180) * step; y += Math.Cos(h * Math.PI / 180) * step;
            res.Travelled += step;
            foreach (Building b in buildings)
                res.MinClearance = Math.Min(res.MinClearance, Math.Sqrt((x - b.X) * (x - b.X) + (y - b.Y) * (y - b.Y)) - b.R);
            res.Seconds += TickMs / 1000.0;
        }
        return res;
    }

    private static void SimulatedWalkRoundBuildings()
    {
        var (path, buildings) = WeavingPath();
        double len = 0;
        for (int i = 1; i < path.Count; i++) len += Math.Sqrt((path[i].X - path[i - 1].X) * (path[i].X - path[i - 1].X) + (path[i].Y - path[i - 1].Y) * (path[i].Y - path[i - 1].Y));
        WalkResult old = Walk(path, buildings, WalkSteer.Legacy(), lookahead: false);
        WalkResult now = Walk(path, buildings, new WalkSteer(), lookahead: true);
        Console.WriteLine($"     {path.Count} corners, {buildings.Count} buildings, {len:F0} u: old {old.Seconds:F1} s, {old.Stops} stops, {old.Turns} turns, clearance {old.MinClearance:F1} u; "
                        + $"new {now.Seconds:F1} s, {now.Stops} stops, {now.Turns} turns, clearance {now.MinClearance:F1} u");
        Check(old.Arrived && now.Arrived, "both walks arrive");
        Check(old.Stops >= 10, $"the old servo stops at the corners ({old.Stops} stops)");
        Check(now.Stops <= old.Stops / 4, $"far fewer stops ({now.Stops} vs {old.Stops})");
        Check(now.Turns * 4 <= old.Turns * 3, $"a quarter fewer turn commands or better ({now.Turns} vs {old.Turns})");
        Check(now.Seconds < old.Seconds * 0.85, $"quicker ({now.Seconds:F1} s vs {old.Seconds:F1} s)");
        Check(now.MinClearance >= 1.0, $"never closer than 1 m to a building ({now.MinClearance:F2} u)");
        Check(now.Travelled < len * 1.05, $"no wandering: walked {now.Travelled:F0} u of a {len:F0} u path");
    }
}

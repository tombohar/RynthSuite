using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthNav.RouteTests;

// 11-19, a player: "Is it intentional that it stops 1 yard or so before hitting the designated coords?"
// Every leg ended within 7 u (ArrivalUnits). The goto's own goal is now stood on: within 2.5 u
// (FinalArrivalUnits), the path's end reached as closely, and the last 20 u (FinalApproachUnits) run
// with the tight servo (WalkSteer.Legacy: 4 degree dead zone, stops to turn past 20) so the run lines
// up on the spot instead of passing beside it. Simulated as SteeringTests walks (30 ticks a second,
// 10 u/s, a run 0.4 s to get going): from all round, arrival without circling or a pile of stops.
internal static partial class Program
{
    private static void RunArrival()
    {
        Run(nameof(FinalSpotStoodOn), FinalSpotStoodOn);
    }

    private sealed class Approach
    {
        public bool Arrived;
        public double Seconds, Off, Travelled;
        public int Stops;
    }

    // Mirrors RynthNavPlugin.StepGoto's last leg: PathFollow.Advance with the path's end at `reach`, the
    // tight servo within `tightFrom` of the goal, arrived within `reach` of it.
    private static Approach WalkToSpot(List<(double X, double Y)> path, double startHeading, double reach, double tightFrom)
    {
        const long TickMs = 33;
        const double RunSpeed = 10.0;
        const long StartUpMs = 400;
        var open = new WalkSteer();
        WalkSteer tight = WalkSteer.Legacy();
        var res = new Approach();
        double x = path[0].X, y = path[0].Y, h = startHeading;
        (double gx, double gy) = path[^1];
        int idx = 1;
        bool running = false;
        long runSince = 0, now = 1_000_000;
        for (int tick = 0; tick < 30 * 120; tick++)
        {
            now += TickMs;
            double toGoal = Math.Sqrt((gx - x) * (gx - x) + (gy - y) * (gy - y));
            if (toGoal < reach) { res.Arrived = true; res.Off = toGoal; break; }
            idx = PathFollow.Advance(path, idx, x, y, 1.5, reach, 6.0, null);
            (double tx, double ty) = idx < path.Count ? path[idx] : (gx, gy);
            double desired = WalkSteer.Norm(Math.Atan2(tx - x, ty - y) * 180 / Math.PI);
            WalkSteer servo = toGoal < tightFrom ? tight : open;
            double t = servo.Step(desired, h, now, out bool run);
            if (!double.IsNaN(t)) h = t;
            if (running && !run) res.Stops++;
            if (!running && run) runSince = now;
            running = run;
            double step = running && now - runSince >= StartUpMs ? RunSpeed * TickMs / 1000.0 : 0;
            x += Math.Sin(h * Math.PI / 180) * step; y += Math.Cos(h * Math.PI / 180) * step;
            res.Travelled += step;
            res.Seconds += TickMs / 1000.0;
        }
        return res;
    }

    private static void FinalSpotStoodOn()
    {
        var rnd = new Random(1119);
        int n = 0, arrived = 0, oldArrived = 0, maxStops = 0;
        double worstOff = 0, sumOff = 0, sumOldOff = 0, worstExtra = 0, sumExtraSeconds = 0;
        for (int i = 0; i < 300; i++)
        {
            // A last leg 8..200 u long, straight or with a corner, started facing anywhere.
            double len = 8 + rnd.NextDouble() * 192, a = rnd.NextDouble() * 2 * Math.PI;
            var path = new List<(double X, double Y)> { (0, 0) };
            if (rnd.Next(2) == 0)
            {
                double c = a + (rnd.NextDouble() - 0.5) * 1.6;
                path.Add((Math.Sin(c) * len * 0.5, Math.Cos(c) * len * 0.5));
            }
            path.Add((Math.Sin(a) * len, Math.Cos(a) * len));
            double heading = rnd.NextDouble() * 360;
            Approach now = WalkToSpot(path, heading, 2.5, 20.0);
            Approach old = WalkToSpot(path, heading, 7.0, 0.0);
            n++;
            if (now.Arrived) arrived++;
            if (old.Arrived) oldArrived++;
            maxStops = Math.Max(maxStops, now.Stops);
            worstOff = Math.Max(worstOff, now.Off);
            sumOff += now.Off; sumOldOff += old.Off;
            double extra = now.Seconds - old.Seconds;
            worstExtra = Math.Max(worstExtra, extra);
            sumExtraSeconds += extra;
        }
        Console.WriteLine($"     {n} last legs: stopped {sumOff / n:F1} u from the spot on average (was {sumOldOff / n:F1} u), worst {worstOff:F1} u; "
                        + $"{sumExtraSeconds / n:F2} s longer on average (worst {worstExtra:F1} s), at most {maxStops} stops");
        Check(arrived == n && oldArrived == n, $"every walk arrives ({arrived}/{n})");
        Check(worstOff < 2.5, $"within 2.5 u of the spot, every time (worst {worstOff:F2} u)");
        Check(sumOff / n < sumOldOff / n / 2, $"under half as far off as before ({sumOff / n:F1} vs {sumOldOff / n:F1} u)");
        Check(maxStops <= 3, $"no circling: at most 3 stops on a last leg ({maxStops})");
        Check(worstExtra < 3.0, $"at most 3 s more than stopping 7 u short (worst {worstExtra:F1} s)");
    }
}

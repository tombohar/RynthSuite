using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using T = RynthCore.Plugin.RynthAi.LegacyUi.NavPointType;
using RT = RynthCore.Plugin.RynthAi.LegacyUi.NavRouteType;

namespace RynthCore.RynthAiHostTests.Tests;

// The nav point reach (FollowNavMin) really decides how close nav gets to each point before it
// turns for the next (Tom, 2026-10-05: "at 1.0 it should pretty much go directly to it. It gets
// to it, passes it and turns around to go back"; "it's turning too early and running into walls").
//
// Unlike NavEngineTests, where the fake character only moves when a test puts it somewhere, these
// walk it: each nav tick (33 ms) the character runs forward along FakeHost.HeadingDeg while nav
// has autorun on (using the autorun state from the tick before, so a stop takes a tick to land,
// as in the game). Nav turns it with TurnToHeading, rate-limited to the turn-rate setting.
//
// The corner: a corridor 2 yd wide runs north from the origin to a point 20 yd north, then east.
// Its inside wall corner is at (19 N, 1 E): any step with north < 19 and east > 1 went through it.
internal static class NavReachTests
{
    private const double NS0 = 42.0, EW0 = 33.0;
    private static long _now;

    public static void Register(Runner r)
    {
        r.Add("nav reach: 1 yd at a 90 degree corner by a wall - touches the point, then turns, no corner cut", CornerTightReach);
        r.Add("nav reach: the corner rounding scales with the reach - none up to the 1.5 yd default, the Lookahead setting at 3 yd+", CornerRoundingScales);
        r.Add("nav reach: 5 yd still rounds a corner (moves on 5 yd out and heads across)", CornerWideReach);
        r.Add("nav reach: going past a point on the side counts as reached - no turning round", PassBySide);
        r.Add("nav reach: a corner cut never counts as passing (the point must be behind)", CutIsNotAPass);
        r.Add("nav reach: fast run at a low frame rate skips over a 0.5 yd reach - counted, no turning round", FastRunSkipsOver);
        r.Add("nav reach: the sweep pass uses the closest approach, not the last tick", SweepUsesClosest);
        r.Add("nav reach: doorway points unchanged - 1 yd reach, no pass-through, no rounding", DoorwayUnchanged);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private static NavPoint Pt(double north, double east, bool doorway = false)
        => new() { Type = T.Point, NS = NS0 + north / 240.0, EW = EW0 + east / 240.0, Doorway = doorway };

    private static NavRouteParser Route(RT type, params NavPoint[] pts)
    {
        var r = new NavRouteParser { RouteType = type };
        r.Points.AddRange(pts);
        return r;
    }

    private sealed class Walk
    {
        public readonly LegacyUiSettings S;
        public readonly NavigationEngine E;
        public double N, E_;
        public double SpeedYdPerS = 11.0;   // a good run skill
        public int TickMs = 33;
        public readonly List<(double N, double E, int Idx, bool Run)> Path = new();

        public Walk(NavRouteParser route, float reach, double north, double east, double faceDeg)
        {
            FakeHost.Reset();
            _now = 1_000_000;
            NavigationEngine.Clock = static () => _now;
            N = north; E_ = east;
            FakeHost.SetNavPosition(NS0 + N / 240.0, EW0 + E_ / 240.0);
            FakeHost.HeadingDeg = faceDeg;
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, EnableNavigation = true, CurrentRoute = route, ActiveNavIndex = 0,
                FollowNavMin = reach,
            };
            E = new NavigationEngine(FakeHost.Create(), S);
        }

        public int Idx => S.ActiveNavIndex;

        /// <summary>One nav tick, then the character moves for one tick if autorun is on.</summary>
        public void Step()
        {
            _now += TickMs;
            bool run = FakeHost.AutoRun;     // what the client was doing going into this tick
            E.Tick();
            if (run)
            {
                double h = FakeHost.HeadingDeg * Math.PI / 180.0, d = SpeedYdPerS * TickMs / 1000.0;
                N += d * Math.Cos(h);
                E_ += d * Math.Sin(h);
                FakeHost.SetNavPosition(NS0 + N / 240.0, EW0 + E_ / 240.0);
            }
            Path.Add((N, E_, Idx, FakeHost.AutoRun));
        }

        public void RunUntil(Func<bool> done, double maxSeconds = 20)
        {
            int n = (int)(maxSeconds * 1000 / TickMs);
            for (int i = 0; i < n && !done(); i++) Step();
        }
    }

    private static double Dist(double n0, double e0, double n1, double e1) => Math.Sqrt((n1 - n0) * (n1 - n0) + (e1 - e0) * (e1 - e0));

    private static int LogCount(string contains) => FakeHost.Logs.Count(l => l.Contains(contains));

    /// <summary>Corridor north 20 yd then east 20 yd; returns the walk after it moved on from the corner.</summary>
    private static Walk Corner(float reach, float lookahead = 4.0f)
    {
        var w = new Walk(Route(RT.Once, Pt(20, 0), Pt(20, 20), Pt(20, 40)), reach, 0, 0, 0);
        w.S.NavLookaheadYards = lookahead;
        w.RunUntil(() => w.Idx >= 1 && w.E_ > 6);
        return w;
    }

    private static double ClosestToCorner(Walk w) => w.Path.Min(p => Dist(p.N, p.E, 20, 0));

    // ── Tests ─────────────────────────────────────────────────────────────────

    private static void CornerTightReach()
    {
        var w = Corner(1.0f);
        Check.True(w.Idx >= 1, "moved on to the east leg");
        var onCorner = w.Path.Where(p => p.Idx == 0).ToList();
        var first = w.Path.FirstOrDefault(p => p.Idx >= 1);
        double atAdvance = Dist(first.N, first.E, 20, 0);
        Check.True(atAdvance <= 1.0, $"moved on within 1 yd of the corner point ({atAdvance:F2} yd)");
        double closest = ClosestToCorner(w);
        Check.True(closest <= 0.5, $"touched the point: came within {closest:F2} yd of it");
        // While the corner is the target the character runs straight up the corridor centre:
        // no turning east early.
        double drift = onCorner.Max(p => Math.Abs(p.E));
        Check.True(drift < 0.05, $"no early turn toward the next point ({drift:F2} yd off the centre line before arriving)");
        Check.False(w.Path.Any(p => p.N < 19 && p.E > 1), "never inside the corridor's inside wall corner (19 N, 1 E)");
        double over = w.Path.Max(p => p.N);
        Check.True(over < 21.0, $"no overshoot into the far wall at 21 N (got to {over:F2} N)");
        // No turning round: north never drops back more than a hair after its highest.
        double maxN = 0, back = 0;
        foreach (var p in w.Path) { maxN = Math.Max(maxN, p.N); back = Math.Max(back, maxN - p.N); }
        Check.True(back < 0.1, $"never walked back south ({back:F2} yd)");
        Check.Eq(LogCount("sweep-pass"), 0, "reached properly, not by a sweep pass");
    }

    private static void CornerRoundingScales()
    {
        Check.Near(NavigationEngine.CornerLookaheadYards(4.0, 0.5), 0.0, 1e-9, "0.5 yd: no rounding");
        Check.Near(NavigationEngine.CornerLookaheadYards(4.0, 1.0), 0.0, 1e-9, "1 yd: no rounding");
        Check.True(NavigationEngine.CornerLookaheadYards(4.0, 1.5) <= 1.5, "1.5 yd default: rounding inside the reach, so none");
        Check.True(NavigationEngine.CornerLookaheadYards(4.0, 2.0) <= 2.0, "2 yd: still inside the reach, so none");
        Check.Near(NavigationEngine.CornerLookaheadYards(4.0, 3.0), 4.0, 1e-9, "3 yd: the 4 yd Lookahead setting");
        Check.Near(NavigationEngine.CornerLookaheadYards(4.0, 5.0), 4.0, 1e-9, "5 yd: the 4 yd Lookahead setting (as before)");
        Check.Near(NavigationEngine.CornerLookaheadYards(0.0, 5.0), 0.0, 1e-9, "Lookahead 0 is still off");
        Check.Near(NavigationEngine.CornerLookaheadYards(6.0, 5.0), 6.0, 1e-9, "a bigger Lookahead setting is used at a big reach");

        // The default reach walks the corner like the 1 yd one: straight to the point.
        var w = Corner(1.5f);
        var onCorner = w.Path.Where(p => p.Idx == 0).ToList();
        Check.True(onCorner.Max(p => Math.Abs(p.E)) < 0.05, "1.5 yd: no early turn");
        var first = w.Path.First(p => p.Idx >= 1);
        Check.True(Dist(first.N, first.E, 20, 0) <= 1.5, "1.5 yd: moved on within 1.5 yd");
        Check.False(w.Path.Any(p => p.N < 19 && p.E > 1), "1.5 yd: no corner cut");
    }

    private static void CornerWideReach()
    {
        // A 5 yd reach moves on 5 yd short of the corner and heads across it for the next point:
        // the open-area corner rounding a big reach has always given.
        var w = Corner(5.0f);
        Check.True(w.Idx >= 1, "moved on");
        var first = w.Path.First(p => p.Idx >= 1);
        double atAdvance = Dist(first.N, first.E, 20, 0);
        Check.True(atAdvance > 4.0 && atAdvance <= 5.0, $"moved on 4-5 yd short of the corner ({atAdvance:F2})");
        Check.True(w.Path.Any(p => p.N < 19 && p.E > 1), "rounded the corner (inside the 2 yd corridor's corner: fine in the open)");
        double maxN = 0, back = 0;
        foreach (var p in w.Path) { maxN = Math.Max(maxN, p.N); back = Math.Max(back, maxN - p.N); }
        Check.True(back < 0.1, $"never walked back south ({back:F2} yd)");
    }

    private static void PassBySide()
    {
        // The character is carried past [0] 1.2 yd to its east (outside a 1 yd reach); the old
        // code saw the distance shrink then grow by under 0.3 yd a tick, so it turned round.
        var w = new Walk(Route(RT.Circular, Pt(10, 0), Pt(30, 0)), 1.0f, 0, 1.2, 0);
        void At(double n, double e) { w.N = n; w.E_ = e; FakeHost.SetNavPosition(NS0 + n / 240.0, EW0 + e / 240.0); }
        foreach (double n in new[] { 2.0, 5.0, 8.0, 9.5, 10.0 }) { At(n, 1.2); FakeHost.HeadingDeg = 0; w.Step(); }
        Check.Eq(w.Idx, 0, "level with the point, 1.2 yd off: not yet");
        At(10.4, 1.2); FakeHost.HeadingDeg = 0; w.Step();
        Check.Eq(w.Idx, 1, "just past it: counted as reached");
        Check.Eq(LogCount("passed [0]"), 1, "logged as a pass");
        At(10.8, 1.2); w.Step();
        Check.True(FakeHost.AutoRun, "still running");
        double h = FakeHost.HeadingDeg;
        Check.True(h < 20 || h > 340, $"heading on north to [1], not back to [0] ({h:F0})");

        // Too far to the side is not a pass (2.5 yd sweep radius at a 1 yd reach).
        var far = new Walk(Route(RT.Circular, Pt(10, 0), Pt(30, 0)), 1.0f, 0, 3.0, 0);
        void AtF(double n, double e) { far.N = n; far.E_ = e; FakeHost.SetNavPosition(NS0 + n / 240.0, EW0 + e / 240.0); }
        foreach (double n in new[] { 2.0, 6.0, 9.0, 10.5, 12.0 }) { AtF(n, 3.0); FakeHost.HeadingDeg = 0; far.Step(); }
        Check.Eq(far.Idx, 0, "3 yd to the side: not passed");
    }

    private static void CutIsNotAPass()
    {
        // Heading for the corner (20 N, 0 E) from the south, the character drifts east toward the
        // next point while still short of the corner: beyond the corner along the NEXT leg, but
        // the corner is not behind it, so it is not a pass.
        var w = new Walk(Route(RT.Once, Pt(20, 0), Pt(20, 20)), 1.0f, 0, 0, 0);
        void At(double n, double e) { w.N = n; w.E_ = e; FakeHost.SetNavPosition(NS0 + n / 240.0, EW0 + e / 240.0); }
        foreach (var (n, e) in new[] { (5.0, 0.0), (12.0, 0.5), (17.0, 1.5), (18.5, 2.0), (19.0, 2.2) })
        {
            At(n, e); w.Step();
        }
        Check.Eq(w.Idx, 0, "cutting toward the next point short of the corner never counts");
        Check.Eq(LogCount("passed [0]"), 0, "no pass logged");
    }

    private static void FastRunSkipsOver()
    {
        // 15 yd/s at 10 frames a second: 1.5 yd a tick, three times the 0.5 yd reach's width.
        var w = new Walk(Route(RT.Once, Pt(20, 0.3), Pt(40, 0.3), Pt(60, 0.3)), 0.5f, 0, 0.3, 0);
        w.SpeedYdPerS = 15.0; w.TickMs = 100;
        w.RunUntil(() => w.Idx >= 1 && w.N > 25, 10);
        Check.True(w.Idx >= 1, "moved on");
        double maxN = 0, back = 0;
        foreach (var p in w.Path) { maxN = Math.Max(maxN, p.N); back = Math.Max(back, maxN - p.N); }
        Check.True(back < 0.1, $"never turned round ({back:F2} yd back)");
    }

    private static void SweepUsesClosest()
    {
        // A slow pass by at about 2.3 yd (inside the 2.5 yd sweep radius of a 1 yd reach): the distance
        // grows 0.1 yd a tick, never 0.3 in one tick. The old check never fired.
        var w = new Walk(Route(RT.Circular, Pt(10, 0), Pt(10, 30)), 1.0f, 8, 2, 90);
        void At(double n, double e) { w.N = n; w.E_ = e; FakeHost.SetNavPosition(NS0 + n / 240.0, EW0 + e / 240.0); }
        // Coming from the south-east toward the point, then sliding away east (pushed along a wall).
        At(8.2, 1.4); w.Step();          // first tick: the approach starts here (2.3 yd)
        At(8.4, 1.6); w.Step();
        At(8.4, 1.8); w.Step();
        At(8.4, 2.0); w.Step();
        At(8.4, 2.2); w.Step();
        Check.Eq(w.Idx, 1, "0.3 yd beyond the closest approach (inside 2.5 yd): sweep pass");
        Check.Eq(LogCount("sweep-pass"), 1, "logged as a sweep pass");
    }

    private static void DoorwayUnchanged()
    {
        // Doorway reach 1 yd, sweep 1.5 yd; the pass-through rule does not apply.
        var w = new Walk(Route(RT.Circular, Pt(10, 0, doorway: true), Pt(10, 10)), 1.5f, 0, 1.2, 0);
        void At(double n, double e) { w.N = n; w.E_ = e; FakeHost.SetNavPosition(NS0 + n / 240.0, EW0 + e / 240.0); }
        foreach (double n in new[] { 4.0, 8.0, 9.5, 10.4 }) { At(n, 1.2); FakeHost.HeadingDeg = 0; w.Step(); }
        Check.Eq(w.Idx, 0, "1.2 yd beside a doorway point and past it: not reached (doorways line up properly)");

        // With the 5 yd reach a plain point would round the corner; a doorway point is walked straight.
        var d = new Walk(Route(RT.Once, Pt(20, 0, doorway: true), Pt(20, 20), Pt(20, 40)), 5.0f, 0, 0, 0);
        d.RunUntil(() => d.Idx >= 1 && d.E_ > 6);
        var onDoor = d.Path.Where(p => p.Idx == 0).ToList();
        Check.True(onDoor.Max(p => Math.Abs(p.E)) < 0.05, "doorway point: straight at it");
        var first = d.Path.First(p => p.Idx >= 1);
        Check.True(Dist(first.N, first.E, 20, 0) <= 1.0, "doorway point reached within 1 yd at a 5 yd reach");
    }
}

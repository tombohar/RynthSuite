using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using T = RynthCore.Plugin.RynthAi.LegacyUi.NavPointType;
using RT = RynthCore.Plugin.RynthAi.LegacyUi.NavRouteType;

namespace RynthCore.RynthAiHostTests.Tests;

// Route walking (NavigationEngine.Tick) on a fake clock and a fake character: advancing
// through Circular, Linear and Once routes, pause / chat / jump / recall points, the
// closest-point start, the off-track rule (never under 160 yd, which covers a meta's 159 yd),
// where a recovery rejoins the route, and the different-area hold. The recovery planner is
// faked; the area decision itself (NavRecoveryPlanner.ClassifyArea) is tested with fake dat
// lookups. The fake character never moves by itself: each test puts it where it should be.
internal static class NavEngineTests
{
    // Positions are in yards north/east of an origin on the landscape near Holtburg.
    private const double NS0 = 42.0, EW0 = 33.0;
    private static double _n, _e;

    public static void Register(Runner r)
    {
        r.Add("nav engine: walks to each waypoint, a Circular route wraps", CircularWraps);
        r.Add("nav engine: a Linear route turns round at both ends", LinearBounces);
        r.Add("nav engine: a Linear route keeps its direction through a pause", LinearDirectionSurvivesStop);
        r.Add("nav engine: a Once route ends, turns nav off, stays finished, and the player can re-run it from [0]", OnceCompletes);
        r.Add("nav engine: closest approach counts as arriving; a pause forgets it", SweepPass);
        r.Add("nav engine: a pause point waits its time on the clock", PausePoint);
        r.Add("nav engine: standing still through a pause, the stop is sent once and then once a second (not every tick)", PauseStopsOnce);
        r.Add("nav engine: a chat point sends its text once and moves on", ChatPoint);
        r.Add("nav engine: a jump point jumps on arrival (heading and power clamped)", JumpPoint);
        r.Add("nav engine: a recall that teleports moves on after the settle", RecallTeleports);
        r.Add("nav engine: a recall that never fires, next waypoint close: moves on after 60 s", RecallTimeoutNear);
        r.Add("nav engine: macro start picks the nearest plain waypoint", ResumeNearest);
        r.Add("nav engine: indoors, macro start picks the nearest waypoint on foot, not through a wall", ResumeNearestByWalk);
        r.Add("nav engine: macro start skips a nearest waypoint already walked past, not one still ahead", ResumeNearestPassed);
        r.Add("nav engine: back from a pause that moved 10+ yd, nav heads for the closest waypoint", ResumeAfterPauseMoved);
        r.Add("nav engine: back from a pause that moved under 10 yd, nav keeps its waypoint", ResumeAfterPauseStill);
        r.Add("nav engine: the dungeon patrol keeps its waypoint after a pause that moved it (no closest-waypoint pick)", ResumeAfterPausePatrol);
        r.Add("nav engine: Go To Closest Waypoint off keeps the last waypoint (hazard/corpse still rejoin)", ResumeNearestOff);
        r.Add("nav engine: a loaded route starts at [0] for Once/Follow, or for any route with the option off", StartsAtFirstPoint);
        r.Add("nav engine: nearest waypoint lookup", FindNearest);
        r.Add("nav engine: off track only beyond 160 yd (159 yd never), after 5 s, once per trip", OffTrack159);
        r.Add("nav engine: coming back from a pause far off the route is not off track", OffTrackDisarmedByStop);
        r.Add("nav engine: a stuck detour rejoins the nearest waypoint ahead", RejoinAhead);
        r.Add("nav engine: a rejoin never goes back, past a chat point, beyond 20 yd or 8 points", RejoinLimits);
        r.Add("nav engine: a rejoin on a Linear route heading back follows the route backwards", RejoinLinearReverse);
        r.Add("nav engine: a waypoint in a different area holds nav with one chat line", AreaHold);
        r.Add("nav engine: an undecided area check walks on and asks again every 2 s", AreaUnknownRetry);
        r.Add("nav engine: the hold ends when the areas match again", AreaResumes);
        r.Add("nav engine: a hold is checked again every 3 s, so a wrong answer doesn't hold for good", AreaRecheck);
        r.Add("nav engine: at the last waypoint before the area change it walks on through for 6 s", AreaGraceWalk);
        r.Add("nav engine: with recovery off the area is never checked", AreaRecoveryOff);
        r.Add("nav area: the decision (same dungeon, other dungeon, landscape, off the map)", ClassifyArea);
        r.Add("nav recovery: a waypoint behind a wall is in the room whose floor holds it, not the nearest centre", ContainingCellByFloor);

        r.Add("nav point reach: values under 1.5 yd work (it used to be floored at 1.5)", ReachBelowOneAndAHalf);
        r.Add("nav point reach: a doorway point is reached within 1 yd, its sweep pass within 1.5 yd", DoorwayPointTight);
        r.Add("nav point reach: no lookahead corner cut at a doorway point (or at a small reach)", DoorwayNoLookahead);
        r.Add("nav point reach: Follow stops within it and moves again 3 yd farther out", FollowUsesReach);
        r.Add("nav point reach: metas set it as FollowNavMin (yd) or VTank's NavCloseStopRange (x240)", ReachFromMetas);

        r.Add("nav engine: a one-point Circular camp route holds on the spot", CampCircularHolds);   // was a known failure; fixed by 91178c1
        r.Add("nav engine: a one-point Linear camp route walks back to its point", CampLinearWalksBack);   // was a known failure; fixed by 91178c1
        r.Add("nav engine: two waypoints on one spot don't bounce thousands of times", TwoPointsOneSpot);   // was a known failure; fixed by 91178c1
        r.Add("nav engine: a Once route ending in dense waypoints finishes cleanly", OnceDenseEnd);   // was a known failure; fixed by 91178c1
        r.Add("nav engine: a Linear index past the end carries on from that end", LinearIndexOffEnd);   // was a known failure; fixed by 91178c1
        r.Add("nav engine: a chat point that loads another route hands over to it", ChatLoadsRoute);   // was a known failure; fixed by a8e5de0
        r.Add("nav engine: a recall that never fires, next waypoint far: stops instead of walking off", RecallTimeoutFar);   // was a known failure; fixed by 5cfbcea
        r.Add("nav engine: a portal/NPC use the engine did not send is not logged as sent and is asked again", PortalNpcUseNotSent);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private static long _now;

    private sealed class Nav
    {
        public readonly LegacyUiSettings S;
        public readonly NavigationEngine E;
        public readonly FakePlanner? P;

        /// <summary>Call FakeHost.Reset() and At(...) first.</summary>
        public Nav(NavRouteParser route, int idx = 0, FakePlanner? planner = null)
        {
            _now = 1_000_000;
            NavigationEngine.Clock = static () => _now;
            var host = FakeHost.Create();
            S = new LegacyUiSettings { IsMacroRunning = true, EnableNavigation = true, CurrentRoute = route, ActiveNavIndex = idx };
            E = new NavigationEngine(host, S);
            P = planner;
            if (planner != null) E.SetRecoveryPlanner(planner);
        }

        public void Tick(int ms = 40) { _now += ms; E.Tick(); }

        /// <summary>Ticks for <paramref name="seconds"/> (100 ms apart), running <paramref name="each"/> first each time.</summary>
        public void Run(double seconds, Action? each = null)
        {
            int n = (int)Math.Round(seconds * 10);
            for (int i = 0; i < n; i++) { each?.Invoke(); Tick(100); }
        }

        public int Idx => S.ActiveNavIndex;
        public string Recovery => E.GetStateSnapshot().Recovery;
    }

    private sealed class FakePlanner : INavRecoveryPlanner
    {
        public NavAreaCheck Area = NavAreaCheck.Match;
        public int AreaCalls;
        public bool GivePlan;
        /// <summary>Per-target walk length (yd); null = no path. Used instead of GivePlan when set.</summary>
        public Func<NavPoint, double?>? WalkYd;
        public readonly List<NavPoint> PlanTargets = new();

        public NavRecoveryPlan? TryPlan(NavPoint target, out string why)
        {
            PlanTargets.Add(target);
            double? walk = WalkYd != null ? WalkYd(target) : (GivePlan ? 5 : null);
            if (walk == null) { why = "fake planner: no plan"; return null; }
            why = string.Empty;
            var plan = new NavRecoveryPlan { Planner = "fake planner", LengthYd = walk.Value };
            plan.Points.Add(new NavPoint { Type = T.Point, NS = target.NS + 1 / 240.0, EW = target.EW });
            return plan;
        }

        public NavAreaCheck CheckArea(uint playerCell, double tNs, double tEw, out string routeArea, out string playerArea)
        {
            AreaCalls++;
            bool mis = Area == NavAreaCheck.Mismatch;
            routeArea = mis ? "dungeon 0x01D9" : string.Empty;
            playerArea = mis ? "on the landscape" : string.Empty;
            return Area;
        }
    }

    private static void At(double north, double east)
    {
        _n = north; _e = east;
        FakeHost.SetNavPosition(NS0 + north / 240.0, EW0 + east / 240.0);
    }

    /// <summary>Turns the fake character to face (north, east) from where it stands.</summary>
    private static void Face(double north, double east)
    {
        double deg = Math.Atan2(east - _e, north - _n) * 180.0 / Math.PI;
        FakeHost.HeadingDeg = deg < 0 ? deg + 360.0 : deg;
    }

    private static NavPoint Pt(double north, double east, T type = T.Point)
        => new() { Type = type, NS = NS0 + north / 240.0, EW = EW0 + east / 240.0 };

    private static NavRouteParser Route(RT type, params NavPoint[] pts)
    {
        var r = new NavRouteParser { RouteType = type };
        r.Points.AddRange(pts);
        return r;
    }

    /// <summary>Points every <paramref name="step"/> yd due north of the origin.</summary>
    private static NavRouteParser Line(RT type, int count, double step)
        => Route(type, Enumerable.Range(0, count).Select(i => Pt(i * step, 0)).ToArray());

    private static int LogCount(string contains) => FakeHost.Logs.Count(l => l.Contains(contains));

    // ── Advancing ─────────────────────────────────────────────────────────────

    private static void CircularWraps()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Circular, Pt(10, 0), Pt(20, 0), Pt(30, 0)));
        nav.Tick();
        Check.True(FakeHost.AutoRun, "runs toward [0]");
        Check.Eq(nav.Idx, 0, "still on [0] while walking");
        At(9.5, 0); nav.Tick();
        Check.Eq(nav.Idx, 1, "within 1.5 yd of [0]: on to [1]");
        At(19.2, 0); nav.Tick();
        Check.Eq(nav.Idx, 2, "on to [2]");
        At(29.4, 0); nav.Tick();
        Check.Eq(nav.Idx, 0, "past the last point a Circular route wraps to [0]");
        Check.True(nav.S.EnableNavigation, "nav stays on");
        Check.Eq(LogCount("arrived at"), 3, "one arrival logged per waypoint");
    }

    private static void LinearBounces()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Linear, Pt(0.5, 0), Pt(20, 0), Pt(40, 0)));
        var seen = new List<int>();
        double[] stops = { 0, 20, 40, 20, 0, 20 };
        foreach (double n in stops) { At(n, 0); nav.Tick(); seen.Add(nav.Idx); }
        Check.Eq(string.Join(",", seen), "1,2,1,0,1,2", "0 -> 1 -> 2, back 1 -> 0, out again");
        Check.Eq(nav.E.GetStateSnapshot().LinearDir, 1, "heading out again");
    }

    private static void LinearDirectionSurvivesStop()
    {
        FakeHost.Reset(); At(40, 0);
        var nav = new Nav(Route(RT.Linear, Pt(0, 0), Pt(20, 0), Pt(40, 0)), idx: 2);
        nav.Tick();
        Check.Eq(nav.Idx, 1, "turned round at the end");
        nav.E.Stop();   // a fight, loot or buff pause
        At(20, 0); nav.Tick();
        Check.Eq(nav.Idx, 0, "after the pause it carries on back toward [0], not out to [2] again");
    }

    private static void OnceCompletes()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Once, Pt(10, 0), Pt(20, 0)));
        At(10, 0); nav.Tick();
        Check.Eq(nav.Idx, 1, "on to [1]");
        At(20, 0); nav.Tick();
        Check.False(nav.S.EnableNavigation, "the end of a Once route turns nav off");
        Check.Eq(nav.S.CurrentRoute.Points.Count, 2, "the points are kept");
        Check.Eq(nav.Idx, 2, "the index is left past the end");
        Check.False(FakeHost.AutoRun, "stopped");

        // A meta or expression switching nav on (a macro start replays "/vt opt set enablenav true"):
        // the route stays finished, as a finished VTank Once route does (2026-10-28; it re-ran from [0]).
        nav.S.EnableNavigation = true;
        nav.Tick();
        Check.Eq(nav.Idx, 2, "nav switched on: still finished");
        Check.False(nav.S.EnableNavigation, "and nav off again");
        Check.Eq(LogCount("the Once route is finished"), 1, "logged");

        // The player's Nav switch / Start Navigation (RearmFinishedNavRoute) re-runs it from [0].
        Check.True(nav.S.RearmFinishedNavRoute(), "the player re-runs it");
        nav.S.EnableNavigation = true;
        nav.Tick();
        Check.Eq(nav.Idx, 0, "re-run from [0]");
        Check.True(nav.S.EnableNavigation, "nav on");
        Check.False(nav.S.RearmFinishedNavRoute(), "a route that isn't finished is left alone");
    }

    private static void SweepPass()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Circular, Pt(10, 0), Pt(40, 0)));
        nav.Tick();
        At(7, 0); nav.Tick();          // 3 yd short: inside the 3.75 yd sweep radius
        At(6.5, 0); nav.Tick();        // moving away again (3.5 > 3.0 + 0.3)
        Check.Eq(nav.Idx, 1, "the closest approach counted as arriving");
        Check.Eq(LogCount("sweep-pass"), 1, "logged as a sweep pass");

        FakeHost.Reset(); At(0, 0);
        var paused = new Nav(Route(RT.Circular, Pt(10, 0), Pt(40, 0)));
        paused.Tick();
        At(7, 0); paused.Tick();
        paused.E.Stop();               // a fight moved us
        At(6.5, 0); paused.Tick();
        Check.Eq(paused.Idx, 0, "after a pause the old closest approach is forgotten");
        At(6.7, 0); paused.Tick();
        Check.Eq(paused.Idx, 0, "3.3 yd is not 0.3 past the fresh reading of 3.5");
    }

    private static void PausePoint()
    {
        FakeHost.Reset(); At(0, 0);
        var pause = Pt(0, 0, T.Pause); pause.PauseTimeMs = 3000;
        var nav = new Nav(Route(RT.Circular, pause, Pt(20, 0)));
        nav.Tick(100);
        Check.True(nav.E.GetStateSnapshot().InPause, "pausing");
        Check.False(FakeHost.AutoRun, "standing still");
        nav.Run(2.8);
        Check.Eq(nav.Idx, 0, "2.9 s: still pausing");
        nav.Run(0.2);
        Check.Eq(nav.Idx, 1, "3.1 s: on to [1]");
    }

    /// <summary>
    /// Standing still through a pause, nav sends its stop once and then a reminder a second, not every
    /// tick: in portal space the game thread takes none of them and 137 ran 12.7 s late, against the
    /// next leg (Dargoth Hera, 2026-10-19).
    /// </summary>
    private static void PauseStopsOnce()
    {
        FakeHost.Reset(); At(0, 0);
        var pause = Pt(0, 0, T.Pause); pause.PauseTimeMs = 5000;
        var nav = new Nav(Route(RT.Circular, pause, Pt(20, 0)));
        nav.Tick(100);
        int before = FakeHost.AutoRunCalls.Count(on => !on);
        nav.Run(4.5);
        int during = FakeHost.AutoRunCalls.Count(on => !on) - before;
        Check.True(nav.E.GetStateSnapshot().InPause, "still pausing");
        Check.True(during <= 5, $"4.5 s of pause: {during} autorun-off sends (a reminder a second at most)");
        Check.True(during >= 3, $"but the reminders keep coming ({during})");
        Check.False(FakeHost.AutoRun, "standing still");
    }

    private static void ChatPoint()
    {
        FakeHost.Reset(); At(0, 0);
        var chat = Pt(0, 0, T.Chat); chat.ChatCommand = "/vt opt set EnableLooting true";
        var sent = new List<string>();
        var nav = new Nav(Route(RT.Circular, chat, Pt(20, 0)));
        nav.E.ChatSubmit = sent.Add;
        nav.Tick(); nav.Tick(); nav.Tick();
        Check.Eq(string.Join("|", sent), "/vt opt set EnableLooting true", "sent once, as typed");
        Check.Eq(nav.Idx, 1, "moved on");

        // Without the plugin's chat path: commands go to the chat parser, text to the chat window.
        FakeHost.Reset(); At(0, 0);
        var cmd = Pt(0, 0, T.Chat); cmd.ChatCommand = "/ra echo hi";
        var text = Pt(0, 0, T.Chat); text.ChatCommand = "hello there";
        var raw = new Nav(Route(RT.Circular, cmd, text, Pt(20, 0)));
        raw.Tick(); raw.Tick();
        Check.True(FakeHost.ChatCommands.Contains("/ra echo hi"), "a / command goes to the chat parser");
        Check.True(FakeHost.Chat.Contains("hello there"), "plain text is written to the chat window");
        Check.Eq(raw.Idx, 2, "both moved on");

        FakeHost.Reset(); At(0, 0);
        var empty = Pt(0, 0, T.Chat);
        var blank = new Nav(Route(RT.Circular, empty, Pt(20, 0)));
        var none = new List<string>();
        blank.E.ChatSubmit = none.Add;
        blank.Tick();
        Check.Eq(none.Count, 0, "an empty chat point sends nothing");
        Check.Eq(blank.Idx, 1, "and moves on");
    }

    private static void JumpPoint()
    {
        FakeHost.Reset(); At(0, 0);
        var jump = Pt(10, 0, T.Jump); jump.JumpHeading = -90; jump.JumpMs = 1500; jump.JumpShift = true;
        var nav = new Nav(Route(RT.Circular, jump, Pt(30, 0)));
        var jumps = new List<(float, bool, int)>();
        nav.E.Jump = (h, s, ms) => jumps.Add((h, s, ms));
        nav.Tick();
        Check.Eq(jumps.Count, 0, "no jump on the way");
        At(9.6, 0); nav.Tick();
        Check.Eq(jumps.Count, 1, "one jump on arrival");
        if (jumps.Count == 1)
        {
            Check.Near(jumps[0].Item1, 270, 1e-3, "heading -90 is faced as 270");
            Check.True(jumps[0].Item2, "shift jump");
            Check.Eq(jumps[0].Item3, 1000, "1500 ms of power is capped at 1000");
        }
        Check.Eq(nav.Idx, 1, "moved on");

        FakeHost.Reset(); At(9.6, 0);
        var plain = Pt(10, 0, T.Jump); plain.JumpMs = 500;
        var bare = new Nav(Route(RT.Circular, plain, Pt(30, 0)));
        bare.Tick();
        Check.Eq(FakeHost.Jumps, 1, "without the plugin's jump, the engine jump is used");
    }

    // ── Recalls ───────────────────────────────────────────────────────────────

    private static Nav RecallRoute(double nextNorth)
    {
        FakeHost.Reset(); At(0, 0);
        var recall = Pt(0, 0, T.Recall); recall.SpellId = 1635;
        return new Nav(Route(RT.Once, recall, Pt(nextNorth, 0)));
    }

    private static void RecallTeleports()
    {
        var nav = RecallRoute(1000);
        nav.Run(1.0);   // settle, then waiting for the cast
        Check.Eq(nav.E.GetStateSnapshot().PortalState, "FiringAction", "past the settle, waiting for the teleport");
        At(995, 0);     // teleported
        nav.Tick(100);
        Check.Eq(nav.E.GetStateSnapshot().PortalState, "PostTeleportSettle", "teleport seen");
        nav.Run(3.8);
        Check.Eq(nav.Idx, 0, "3.8 s into the 4 s post-portal settle: still on the recall");
        nav.Run(0.4);
        Check.Eq(nav.Idx, 1, "after the settle: on to [1]");
        Check.True(nav.S.EnableNavigation, "nav still on");
    }

    private static void PortalNpcUseNotSent()
    {
        FakeHost.Reset(); At(0, 0);
        FakeHost.WeaponCalls = true;   // UseObject
        const uint Npc = 0x7A000001, Me = 0x50000F01;
        var pose = FakeHost.PlayerPose;
        FakeHost.Names[Me] = "Tester";
        FakeHost.Positions[Me] = pose;
        FakeHost.Names[Npc] = "Gateway";
        FakeHost.Positions[Npc] = (pose.Cell, pose.X + 1f, pose.Y, pose.Z);
        var host = FakeHost.Create(Me);
        var cache = FakeHost.MakeCache(host, Me, new[] { Npc });
        var nav = new Nav(Route(RT.Once, Pt(10, 0), Pt(20, 0)));
        nav.E.SetWorldObjectCache(cache);
        nav.E.SetPlayerId(Me);
        var fire = typeof(NavigationEngine).GetMethod("FirePortalNpcUse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var point = new NavPoint { Type = T.PortalNPC, TargetName = "Gateway" };

        FakeHost.UseObjectAccepts = false;
        Check.False((bool)fire.Invoke(nav.E, new object[] { point })!, "the engine did not send the use: not fired, the caller asks again");
        Check.Eq(LogCount("UseObject portal 'Gateway' (dist="), 0, "and no 'used' line is logged");
        Check.Eq(LogCount("was not sent - will retry"), 1, "the refusal is logged");

        FakeHost.UseObjectAccepts = true;
        Check.True((bool)fire.Invoke(nav.E, new object[] { point })!, "once the engine sends it the portal counts as fired");
        Check.Eq(LogCount("UseObject portal 'Gateway' (dist="), 1, "and the 'used' line is logged");
    }

    private static void RecallTimeoutNear()
    {
        var nav = RecallRoute(30);
        nav.Run(64.0);
        Check.Eq(nav.Idx, 0, "64 s: still waiting");
        nav.Run(1.5);
        Check.Eq(nav.Idx, 1, "after 60 s + the settle: moves on to the close waypoint");
        Check.True(nav.S.EnableNavigation, "nav still on");
        Check.Eq(LogCount("global timeout"), 1, "the timeout is logged");
    }

    private static void RecallTimeoutFar()
    {
        var nav = RecallRoute(1000);
        nav.Run(66.0);
        Check.False(nav.S.EnableNavigation, "nav turned off: the next waypoint is 1000 yd away");
        Check.Eq(nav.Idx, 0, "the index stays on the recall, so re-enabling tries it again");
        Check.True(FakeHost.Chat.Exists(c => c.Contains("didn't teleport")), "the player is told why");
    }

    // ── Where a route starts ──────────────────────────────────────────────────

    private static void ResumeNearest()
    {
        FakeHost.Reset(); At(99, 0);
        var chatHere = Pt(99, 0, T.Chat); chatHere.ChatCommand = "/say hi";
        var circ = new Nav(Route(RT.Circular, Pt(0, 0), Pt(50, 0), chatHere, Pt(100, 1), Pt(150, 0)));
        circ.E.ResumeFromNearestWaypoint();
        Check.Eq(circ.Idx, 3, "Circular: nearest plain waypoint anywhere on the route (the chat point on the spot is not a candidate)");

        FakeHost.Reset(); At(99, 0);
        var lin = new Nav(Route(RT.Linear, Pt(0, 0), Pt(50, 0), Pt(100, 1)), idx: 0);
        lin.E.ResumeFromNearestWaypoint();
        Check.Eq(lin.Idx, 2, "Linear: nearest anywhere");

        FakeHost.Reset(); At(0, 0);
        var once = new Nav(Route(RT.Once, Pt(0, 0), Pt(50, 0), Pt(100, 0), Pt(60, 0)), idx: 1);
        once.E.ResumeFromNearestWaypoint();
        Check.Eq(once.Idx, 1, "Once: only from the current waypoint on ([0] is nearer but done)");
        once.S.ActiveNavIndex = 2;
        once.E.ResumeFromNearestWaypoint();
        Check.Eq(once.Idx, 3, "Once from [2]: [3] is nearer than [2]");

        FakeHost.Reset(); At(0, 0);
        var recall = Pt(500, 0, T.Recall); recall.SpellId = 1635;
        var onRecall = new Nav(Route(RT.Circular, Pt(0, 0), recall, Pt(50, 0)), idx: 1);
        onRecall.E.ResumeFromNearestWaypoint();
        Check.Eq(onRecall.Idx, 1, "standing on a recall step: kept");

        FakeHost.Reset(); At(0, 0);
        var follow = new Nav(new NavRouteParser { RouteType = RT.Follow, FollowTargetName = "Leader" }, idx: 0);
        follow.E.ResumeFromNearestWaypoint();
        Check.Eq(follow.Idx, 0, "Follow: untouched");

        FakeHost.Reset(); FakeHost.HasPose = false;
        var blind = new Nav(Route(RT.Circular, Pt(0, 0), Pt(50, 0)), idx: 1);
        blind.E.ResumeFromNearestWaypoint();
        Check.Eq(blind.Idx, 1, "no position: untouched");
    }

    private static void ResumeNearestByWalk()
    {
        // In a dungeon cell: [2] is 5 yd away through a wall (a 300 yd walk), [1] (current) is
        // 20 yd away and a 25 yd walk.
        FakeHost.Reset(); At(0, 0);
        var pose = FakeHost.PlayerPose;
        FakeHost.PlayerPose = ((pose.Cell & 0xFFFF0000) | 0x0105, pose.X, pose.Y, pose.Z);
        var route = Route(RT.Circular, Pt(-200, 0), Pt(20, 0), Pt(5, 0), Pt(200, 0));
        var p = new FakePlanner { WalkYd = t => t.NS == route.Points[2].NS ? 300 : t.NS == route.Points[1].NS ? 25 : 400 };
        var nav = new Nav(route, idx: 1, planner: p);
        nav.E.ResumeFromNearestWaypoint();
        Check.Eq(nav.Idx, 1, "the current waypoint is the shortest walk: kept");

        // No path to any: the straight-line nearest, as before.
        FakeHost.Reset(); At(0, 0);
        pose = FakeHost.PlayerPose;
        FakeHost.PlayerPose = ((pose.Cell & 0xFFFF0000) | 0x0105, pose.X, pose.Y, pose.Z);
        var p2 = new FakePlanner { WalkYd = _ => null };
        var nav2 = new Nav(Route(RT.Circular, Pt(-200, 0), Pt(20, 0), Pt(5, 0), Pt(200, 0)), idx: 1, planner: p2);
        nav2.E.ResumeFromNearestWaypoint();
        Check.Eq(nav2.Idx, 2, "no paths: straight-line nearest");

        // Outdoors the planner isn't asked.
        FakeHost.Reset(); At(0, 0);
        var p3 = new FakePlanner { WalkYd = _ => 1 };
        var nav3 = new Nav(Route(RT.Circular, Pt(-200, 0), Pt(20, 0), Pt(5, 0), Pt(200, 0)), idx: 1, planner: p3);
        nav3.E.ResumeFromNearestWaypoint();
        Check.Eq(nav3.Idx, 2, "outdoors: straight-line nearest");
        Check.Eq(p3.PlanTargets.Count, 0, "outdoors: no plans asked for");
    }

    /// <summary>A 40 yd square, run clockwise: [0] origin, [1] 40 east, [2] 40 north-east, [3] 40 north.</summary>
    private static NavRouteParser Square()
        => Route(RT.Circular, Pt(0, 0), Pt(0, 40), Pt(40, 40), Pt(40, 0));

    private static void ResumeNearestPassed()
    {
        // Just beyond [2] on the leg toward [3]: the closest spot on the route is on that leg.
        FakeHost.Reset(); At(42, 30);
        var past = new Nav(Square(), idx: 0);
        past.E.ResumeFromNearestWaypoint();
        Check.Eq(past.Idx, 3, "walked past [2]: on to [3] instead of turning back");

        // Still short of [1], a little off the leg into it: [1] stays the target.
        FakeHost.Reset(); At(2, 30);
        var shy = new Nav(Square(), idx: 0);
        shy.E.ResumeFromNearestWaypoint();
        Check.Eq(shy.Idx, 1, "still on the leg into [1]: [1]");
    }

    private static void ResumeAfterPauseMoved()
    {
        FakeHost.Reset(); At(0, 10);
        var nav = new Nav(Square(), idx: 1);
        nav.Tick();
        nav.E.Stop();                  // a fight starts
        At(38, 42);                    // ... and the chase ends near [2]
        nav.Run(6.0);                  // past any teleport settle
        Check.Eq(nav.Idx, 2, "the closest waypoint, not the one it was heading for");
        Check.True(LogCount("back from a pause") == 1, "logged once");
    }

    private static void ResumeAfterPausePatrol()
    {
        FakeHost.Reset(); At(0, 10);
        var nav = new Nav(Square(), idx: 1);
        nav.E.KeepWaypointAfterPause = () => true;   // the plugin: _dunPatrolActive
        nav.Tick();
        nav.E.Stop();
        At(38, 42);                    // the same chase as ResumeAfterPauseMoved
        nav.Run(6.0);
        Check.Eq(nav.Idx, 1, "patrol: carries on to the waypoint it was heading for");
        Check.Eq(LogCount("back from a pause"), 0, "nothing re-picked");

        nav.E.ResumeFromNearestWaypoint(always: true);
        Check.Eq(nav.Idx, 2, "hazard escape / corpse run: still rejoins at the closest waypoint");
    }

    private static void ResumeAfterPauseStill()
    {
        FakeHost.Reset(); At(2, 38);
        var nav = new Nav(Square(), idx: 2);
        nav.Tick();
        nav.E.Stop();
        At(-3, 36);                    // 5 yd: [1] is now nearer, but the pause barely moved us
        nav.Run(1.0);
        Check.Eq(nav.Idx, 2, "kept: moved under 10 yd");
        Check.Eq(LogCount("back from a pause"), 0, "nothing re-picked");
    }

    private static void ResumeNearestOff()
    {
        FakeHost.Reset(); At(0, 10);
        var nav = new Nav(Square(), idx: 1);
        nav.S.NavResumeNearest = false;
        nav.Tick();
        nav.E.Stop();
        At(38, 42);
        nav.Run(6.0);
        Check.Eq(nav.Idx, 1, "pause: carries on to the waypoint it was heading for");

        nav.E.ResumeFromNearestWaypoint();
        Check.Eq(nav.Idx, 1, "macro start: keeps the last waypoint");

        nav.E.ResumeFromNearestWaypoint(always: true);
        Check.Eq(nav.Idx, 2, "hazard escape / corpse run: rejoins at the closest waypoint anyway");
    }

    private static void StartsAtFirstPoint()
    {
        Check.False(NavRouteParser.StartsAtFirstPoint(RT.Circular, true), "Circular, option on: nearest");
        Check.False(NavRouteParser.StartsAtFirstPoint(RT.Linear, true), "Linear, option on: nearest");
        Check.True(NavRouteParser.StartsAtFirstPoint(RT.Once, true), "Once: always [0]");
        Check.True(NavRouteParser.StartsAtFirstPoint(RT.Follow, true), "Follow: always [0]");
        Check.True(NavRouteParser.StartsAtFirstPoint(RT.Circular, false), "Circular, option off: [0]");
        Check.True(NavRouteParser.StartsAtFirstPoint(RT.Linear, false), "Linear, option off: [0]");
    }

    private static void FindNearest()
    {
        FakeHost.Reset(); At(48, 0);
        var nav = new Nav(Route(RT.Circular));
        var route = Route(RT.Circular, Pt(0, 0), Pt(50, 0, T.Recall), Pt(45, 0), Pt(100, 0));
        Check.Eq(nav.E.FindNearestWaypoint(route), 2, "nearest plain waypoint (the recall on the spot is skipped)");
        Check.Eq(nav.E.FindNearestWaypoint(Route(RT.Circular, Pt(50, 0, T.Recall), Pt(49, 0, T.Chat))), 0, "no plain waypoint: 0");
        Check.Eq(nav.E.FindNearestWaypoint(Route(RT.Circular)), 0, "empty route: 0");
        FakeHost.HasPose = false;
        Check.Eq(nav.E.FindNearestWaypoint(route), 0, "no position: 0");
    }

    // ── Off track and rejoin ──────────────────────────────────────────────────

    /// <summary>A 240 yd leg due north; nav heads for its far end.</summary>
    private static Nav LongLeg(FakePlanner p, float offTrackYards)
    {
        FakeHost.Reset(); At(10, 0);
        var nav = new Nav(Route(RT.Circular, Pt(0, 0), Pt(240, 0)), idx: 1, planner: p);
        nav.S.NavOffTrackYards = offTrackYards;
        return nav;
    }

    /// <summary>Walks east to <paramref name="east"/> yd off the leg in steps under 50 yd (a bigger
    /// jump reads as a teleport), drifting north so the stuck watchdog sees movement.</summary>
    private static void Drift(Nav nav, double east)
    {
        while (Math.Abs(_e - east) > 0.01)
        {
            double step = Math.Clamp(east - _e, -30, 30);
            At(_n + 0.2, _e + step);
            nav.Tick(100);
        }
    }

    private static void Hold(Nav nav, double seconds) => nav.Run(seconds, () => At(_n + 0.2, _e));

    private static void OffTrack159()
    {
        var p = new FakePlanner();
        var nav = LongLeg(p, offTrackYards: 100);   // below the floor: 160 applies
        nav.Tick();
        Drift(nav, 159);
        Hold(nav, 8);
        Check.Eq(p.PlanTargets.Count, 0, "159 yd off the route for 8 s: no recovery");

        Drift(nav, 161);
        Hold(nav, 4.8);
        Check.Eq(p.PlanTargets.Count, 0, "161 yd for under 5 s: not yet");
        Hold(nav, 0.5);
        Check.Eq(p.PlanTargets.Count, 1, "161 yd for 5 s: one recovery");
        Check.True(FakeHost.Logs.Exists(l => l.Contains("off track, 161yd from the route for 5s")), "logged with the distance");
        Hold(nav, 10);
        Check.Eq(p.PlanTargets.Count, 1, "only one recovery per trip off the route");

        Drift(nav, 150);
        nav.Tick(100);
        Drift(nav, 161);
        Hold(nav, 5.3);
        Check.Eq(p.PlanTargets.Count, 2, "back within the limit re-arms it: a second trip gets a second recovery");

        var p2 = new FakePlanner();
        var dflt = LongLeg(p2, offTrackYards: 170);  // the default setting
        dflt.Tick();
        Drift(dflt, 165);
        Hold(dflt, 8);
        Check.Eq(p2.PlanTargets.Count, 0, "the default limit is 170 yd: 165 yd is not off track");
    }

    private static void OffTrackDisarmedByStop()
    {
        var p = new FakePlanner();
        var nav = LongLeg(p, offTrackYards: 160);
        nav.Tick();
        nav.E.Stop();              // a fight takes over (combat owns the tick) ...
        nav.S.BotAction = "Combat";
        Drift(nav, 175);           // ... and ends 175 yd off the route
        nav.S.BotAction = "Default";
        Hold(nav, 8);
        Check.Eq(p.PlanTargets.Count, 0, "far off after a pause: it just heads back, no recovery");
        Drift(nav, 100);           // back within the limit: armed again
        Drift(nav, 170);
        Hold(nav, 5.3);
        Check.Eq(p.PlanTargets.Count, 1, "once back within the limit, a later trip out counts again");
    }

    /// <summary>Stands still (facing the target so nav runs) until the stuck ladder asks for a
    /// detour: the second 5 s watchdog window without movement, so at most 20 s.</summary>
    private static void StuckUntilDetour(Nav nav)
    {
        var tgt = nav.S.CurrentRoute.Points[nav.Idx];
        Face((tgt.NS - NS0) * 240.0, (tgt.EW - EW0) * 240.0);
        for (int i = 0; i < 200 && nav.P!.PlanTargets.Count == 0; i++) nav.Tick(100);
    }

    private static int IndexOf(Nav nav, NavPoint? p) => p == null ? -1 : nav.S.CurrentRoute.Points.IndexOf(p);

    private static int RejoinFrom(NavRouteParser route, int idx, double north, double east)
    {
        FakeHost.Reset(); At(north, east);
        var p = new FakePlanner { GivePlan = true };
        var nav = new Nav(route, idx, p);
        StuckUntilDetour(nav);
        return p.PlanTargets.Count == 0 ? -99 : IndexOf(nav, p.PlanTargets[0]);
    }

    private static void RejoinAhead()
    {
        FakeHost.Reset(); At(10.5, 2);
        var p = new FakePlanner { GivePlan = true };
        var nav = new Nav(Line(RT.Circular, 16, 3), idx: 1, planner: p);   // points every 3 yd
        StuckUntilDetour(nav);
        Check.Eq(FakeHost.Jumps, 1, "first stuck: the jump rung");
        Check.Eq(p.PlanTargets.Count, 1, "second stuck: a detour is planned");
        Check.Eq(IndexOf(nav, p.PlanTargets.FirstOrDefault()), 3, "rejoins at [3], the nearest point ahead (passing [2])");
        Check.True(nav.Recovery.Contains("-> [3]"), $"the detour heads for [3] ({nav.Recovery})");
        Check.True(FakeHost.Logs.Exists(l => l.Contains("passes over 2 waypoint(s)")), "logged as passing over 2 waypoints");
    }

    private static void RejoinLimits()
    {
        Check.Eq(RejoinFrom(Line(RT.Circular, 16, 3), 1, 0.5, 2), 1, "nearer to [0], behind the target: stays on [1]");

        var chat = Line(RT.Circular, 16, 3);
        chat.Points[3] = Pt(9, 0, T.Chat);
        Check.Eq(RejoinFrom(chat, 1, 11.5, 1), 2, "a chat point at [3]: never past it");

        Check.Eq(RejoinFrom(Line(RT.Circular, 8, 8), 1, 31, 2), 3,
            "points 8 yd apart, nearest [4]: only 20 yd of route ahead may be passed, so [3]");

        Check.Eq(RejoinFrom(Line(RT.Circular, 21, 2), 1, 30, 1), 9,
            "points 2 yd apart, nearest [15]: at most 8 points ahead, so [9]");

        Check.Eq(RejoinFrom(Line(RT.Circular, 16, 3), 1, 4.8, 12), 1,
            "12 yd to the side, [2] nearer than [1] by under 0.5 yd: stays on [1]");
    }

    private static void RejoinLinearReverse()
    {
        FakeHost.Reset(); At(45, 0);
        var p = new FakePlanner { GivePlan = true };
        var nav = new Nav(Line(RT.Linear, 16, 3), idx: 15, planner: p);
        nav.Tick();
        Check.Eq(nav.Idx, 14, "turned round at the far end");
        At(34.5, 2);
        StuckUntilDetour(nav);
        Check.Eq(IndexOf(nav, p.PlanTargets.FirstOrDefault()), 12, "heading back, it rejoins at [12] (the route backwards), not [11] or [14]");
    }

    // ── Different area ────────────────────────────────────────────────────────

    private static void AreaHold()
    {
        FakeHost.Reset(); At(30, 0);
        var p = new FakePlanner { Area = NavAreaCheck.Mismatch };
        var nav = new Nav(Route(RT.Circular, Pt(0, 0), Pt(50, 0)), idx: 1, planner: p);
        nav.Tick();
        Check.False(FakeHost.AutoRun, "standing still");
        var lines = FakeHost.Chat.Where(c => c.Contains("different area")).ToList();
        Check.Eq(lines.Count, 1, "one chat line");
        Check.True(lines.Count == 1 && lines[0].Contains("dungeon 0x01D9") && lines[0].Contains("on the landscape"), "it names both places");
        Check.True(nav.S.NavIsStuck, "flagged stuck");
        Check.True(nav.Recovery.StartsWith("area hold"), $"snapshot shows the hold ({nav.Recovery})");

        nav.Run(12);
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("different area")), 1, "still one chat line after 12 s");
        Check.True(p.AreaCalls >= 4 && p.AreaCalls <= 5, $"asked again every 3 s while held ({p.AreaCalls} calls in 12 s)");
        Check.Eq(p.PlanTargets.Count, 0, "no detour is tried");
        Check.Eq(FakeHost.Jumps, 0, "no escape jumps");
        Check.Eq(nav.Idx, 1, "the waypoint is kept");
    }

    private static void AreaUnknownRetry()
    {
        FakeHost.Reset(); At(30, 0);
        var p = new FakePlanner { Area = NavAreaCheck.Unknown };
        var nav = new Nav(Route(RT.Circular, Pt(0, 0), Pt(50, 0)), idx: 1, planner: p);
        nav.Tick();
        Check.True(FakeHost.AutoRun, "undecided: walks on");
        Check.Eq(p.AreaCalls, 1, "asked once");
        nav.Run(1.8, () => At(_n + 0.1, _e));
        Check.Eq(p.AreaCalls, 1, "not again within 2 s");
        nav.Run(0.4, () => At(_n + 0.1, _e));
        Check.Eq(p.AreaCalls, 2, "asked again after 2 s");
    }

    private static void AreaResumes()
    {
        // Origin is 12 yd south of a landblock edge: 5 yd north is one landblock, 15 yd the next.
        FakeHost.Reset(); At(5, 0);
        var p = new FakePlanner { Area = NavAreaCheck.Mismatch };
        var nav = new Nav(Route(RT.Circular, Pt(-40, 0), Pt(60, 0)), idx: 1, planner: p);
        nav.Tick();
        Check.False(FakeHost.AutoRun, "held");
        p.Area = NavAreaCheck.Match;
        nav.Run(2);
        Check.False(FakeHost.AutoRun, "nothing changed and under 3 s: still held");
        At(15, 0);   // crossed into the next landblock
        nav.Tick(100);
        nav.Tick(100);
        Check.True(FakeHost.Logs.Exists(l => l.Contains("matches again")), "the landblock change re-checked it: resumes");
        Check.True(FakeHost.AutoRun, "walking again");
        Check.Eq(nav.Recovery, "none", "no hold");
    }

    private static void AreaRecheck()
    {
        FakeHost.Reset(); At(30, 0);
        var p = new FakePlanner { Area = NavAreaCheck.Mismatch };
        var nav = new Nav(Route(RT.Circular, Pt(0, 0), Pt(50, 0)), idx: 1, planner: p);
        nav.Tick();
        Check.False(FakeHost.AutoRun, "held");
        p.Area = NavAreaCheck.Match;   // e.g. the first answer came from a pose read mid-teleport
        nav.Run(3.5);
        Check.True(FakeHost.Logs.Exists(l => l.Contains("matches again")), "re-checked without moving: resumes");
        Check.True(FakeHost.AutoRun, "walking again");
        Check.Eq(nav.Recovery, "none", "no hold");
    }

    private static void AreaGraceWalk()
    {
        FakeHost.Reset(); At(0.5, 0);
        var p = new FakePlanner { Area = NavAreaCheck.Mismatch };
        var nav = new Nav(Route(RT.Circular, Pt(-20, 0), Pt(0, 0), Pt(900, 0)), idx: 2, planner: p);
        nav.Tick();
        Check.True(FakeHost.AutoRun, "walks on through [1] toward a spot 4 yd past it");
        Check.True(nav.S.NavStatusLine.Contains("walking on through"), "status says so");
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("different area")), 0, "no chat line yet");
        nav.Run(5.8);
        Check.Eq(FakeHost.Jumps, 0, "the stuck watchdog does not jump during the hold");
        nav.Run(0.4);
        Check.False(FakeHost.AutoRun, "after 6 s: holds");
        Check.Eq(FakeHost.Chat.Count(c => c.Contains("different area")), 1, "and says so once");

        // Too far from the last waypoint: no grace, straight to the hold.
        FakeHost.Reset(); At(11, 0);
        var far = new Nav(Route(RT.Circular, Pt(-20, 0), Pt(0, 0), Pt(900, 0)), idx: 2, planner: new FakePlanner { Area = NavAreaCheck.Mismatch });
        far.Tick();
        Check.False(FakeHost.AutoRun, "11 yd from the last waypoint: held at once");
    }

    private static void AreaRecoveryOff()
    {
        FakeHost.Reset(); At(30, 0);
        var p = new FakePlanner { Area = NavAreaCheck.Mismatch };
        var nav = new Nav(Route(RT.Circular, Pt(0, 0), Pt(50, 0)), idx: 1, planner: p);
        nav.S.NavRecoveryEnabled = false;
        nav.Run(1);
        Check.Eq(p.AreaCalls, 0, "never asked");
        Check.True(FakeHost.AutoRun, "walks toward the waypoint");
    }

    private static void ContainingCellByFloor()
    {
        static RynthCore.Plugin.RynthAi.Raycasting.Vector3[] Sq(float x0, float y0, float x1, float y1, float z) => new[]
        {
            new RynthCore.Plugin.RynthAi.Raycasting.Vector3(x0, y0, z), new RynthCore.Plugin.RynthAi.Raycasting.Vector3(x1, y0, z),
            new RynthCore.Plugin.RynthAi.Raycasting.Vector3(x1, y1, z), new RynthCore.Plugin.RynthAi.Raycasting.Vector3(x0, y1, z),
        };
        // Room A: x 0..20 (centre 10,10). Room B: x 20..60 (centre 40,10). Wall at x = 20.
        // (20.5, 10) is in B, but 10.5 from A's centre and 19.5 from B's: nearest-centre said A.
        var floors = new List<(uint, RynthCore.Plugin.RynthAi.Raycasting.Vector3[])>
        {
            (0x63460101u, Sq(0, 0, 20, 20, 0)),
            (0x63460102u, Sq(20, 0, 60, 20, 0)),
            (0x63460103u, Sq(20, 0, 60, 20, 12)),   // a floor above B (stacked level)
        };
        Check.Eq(NavRecoveryPlanner.PickContainingCell(floors, 20.5, 10, 0f), 0x63460102u, "behind the wall: room B");
        Check.Eq(NavRecoveryPlanner.PickContainingCell(floors, 19.5, 10, 0f), 0x63460101u, "this side: room A");
        Check.Eq(NavRecoveryPlanner.PickContainingCell(floors, 30, 10, 12.5f), 0x63460103u, "stacked: the floor under the feet");
        Check.Eq(NavRecoveryPlanner.PickContainingCell(floors, 70, 10, 0f), 0u, "outside every floor: 0 (nearest centre is the fallback)");
    }

    // ── Nav point reach (FollowNavMin) and doorway points ─────────────────────

    private static void ReachBelowOneAndAHalf()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Circular, Pt(10, 0), Pt(30, 0)));
        nav.S.FollowNavMin = 0.5f;
        nav.Tick();
        At(9.0, 0); nav.Tick();
        Check.Eq(nav.Idx, 0, "1 yd short with a 0.5 yd reach: not there yet (the old 1.5 floor said arrived)");
        At(9.6, 0); nav.Tick();
        Check.Eq(nav.Idx, 1, "0.4 yd: arrived");
    }

    private static NavPoint Door(double north, double east) { var p = Pt(north, east); p.Doorway = true; return p; }

    private static void DoorwayPointTight()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Circular, Door(10, 0), Pt(10, 10)));
        nav.Tick();
        At(8.8, 0); nav.Tick();
        Check.Eq(nav.Idx, 0, "1.2 yd from a doorway point: not there (a plain point would count at 1.5)");
        At(9.1, 0); nav.Tick();
        Check.Eq(nav.Idx, 1, "0.9 yd: reached");

        FakeHost.Reset(); At(0, 0);
        var sweep = new Nav(Route(RT.Circular, Door(10, 0), Pt(10, 10)));
        sweep.Tick();
        At(7, 0); sweep.Tick();       // 3 yd: inside a plain point's 3.75 yd sweep radius
        At(6.5, 0); sweep.Tick();     // moving away
        Check.Eq(sweep.Idx, 0, "no sweep pass 3 yd out from a doorway point (a plain point advances, see the sweep test)");
        At(8.7, 0); sweep.Tick();     // 1.3 yd
        At(8.2, 0); sweep.Tick();     // moving away again
        Check.Eq(sweep.Idx, 1, "a sweep pass within 1.5 yd still counts");

        FakeHost.Reset(); At(0, 0);
        var small = new Nav(Route(RT.Circular, Door(10, 0), Pt(10, 10)));
        small.S.FollowNavMin = 0.6f;
        small.Tick();
        At(9.2, 0); small.Tick();
        Check.Eq(small.Idx, 0, "a nav point reach under 1 yd applies to doorway points too");
    }

    private static void DoorwayNoLookahead()
    {
        // 3 yd short of a point, the next one due east, with a 2.8 yd reach (big enough to round
        // corners: 3.6 yd of lookahead): a plain point blends the aim east, a doorway point keeps
        // aiming straight at itself. At the 1.5 yd default reach there is no rounding at all
        // (the lookahead scales with the reach since 2026-10-05, see NavReachTests).
        foreach (bool doorway in new[] { false, true })
        {
            FakeHost.Reset(); At(7, 0); Face(10, 0);
            var first = doorway ? Door(10, 0) : Pt(10, 0);
            var nav = new Nav(Route(RT.Circular, first, Pt(10, 10)));
            nav.S.FollowNavMin = 2.8f;
            nav.Tick();
            double h = FakeHost.HeadingDeg;
            if (doorway) Check.True(h < 1.0 || h > 359.0, $"doorway point: still heading north ({h:F1})");
            else Check.True(h > 3.0 && h < 90.0, $"plain point: turning east early ({h:F1})");
        }

        FakeHost.Reset(); At(8, 0); Face(10, 0);
        var dflt = new Nav(Route(RT.Circular, Pt(10, 0), Pt(10, 10)));
        dflt.Tick();
        double hd = FakeHost.HeadingDeg;
        Check.True(hd < 1.0 || hd > 359.0, $"1.5 yd default reach, 2 yd short: a plain point is walked straight at too ({hd:F1})");
    }

    private static void FollowUsesReach()
    {
        const uint leader = 0x50000077;
        void LeaderAt(double north, double east)
        {
            uint cell = FakeHost.NavCell(NS0 + north / 240.0, EW0 + east / 240.0, out float x, out float y);
            FakeHost.Positions[leader] = (cell, x, y, 0f);
        }
        FakeHost.Reset(); At(0, 0); Face(10, 0);
        var nav = new Nav(Route(RT.Circular, Pt(50, 0)));
        nav.S.FollowMode = true;
        nav.S.FollowTargetId = leader;
        LeaderAt(4, 0); nav.Tick();
        Check.False(FakeHost.AutoRun, "4 yd: inside reach + 3 (4.5), stays put");
        LeaderAt(6, 0); nav.Tick();
        Check.True(FakeHost.AutoRun, "6 yd: follows");
        LeaderAt(2, 0); nav.Tick();
        Check.True(FakeHost.AutoRun, "2 yd: still closing in (it used to stop at 5)");
        LeaderAt(1.2, 0); nav.Tick();
        Check.False(FakeHost.AutoRun, "1.2 yd: within the 1.5 yd reach, stops");
    }

    private static void ReachFromMetas()
    {
        var s = new LegacyUiSettings();
        var e = ExpressionTests.Engine(settings: s);
        Check.Eq(e.Evaluate("vtsetsetting[NavCloseStopRange, 0.00625]"), "1", "VTank's option name is known");
        Check.Near(s.FollowNavMin, 1.5, 1e-4, "0.00625 landblocks = 1.5 yd");
        Check.Eq(e.Evaluate("vtsetsetting[FollowNavMin, 0.8]"), "1", "RynthAi's own name");
        Check.Near(s.FollowNavMin, 0.8, 1e-4, "set in yards");
        Check.Near(double.Parse(e.Evaluate("vtgetsetting[NavCloseStopRange]"), System.Globalization.CultureInfo.InvariantCulture), 0.8 / 240.0, 1e-6, "read back in landblocks");
        e.Evaluate("vtsetsetting[FollowNavMin, 0.1]");
        Check.Near(s.FollowNavMin, 0.5, 1e-4, "clamped to the 0.5 yd minimum");
        e.Evaluate("vtsetsetting[NavCloseStopRange, 0]");
        Check.Near(s.FollowNavMin, 0.5, 1e-4, "0 (VTank's 'off') leaves it alone");
        Check.True(RynthCore.Plugin.RynthAi.Meta.MetaManager.TryMapVtOption("navclosestoprange", out string ra) && ra == "NavCloseStopRange", "/vt opt navclosestoprange maps to it");
        Check.True(RynthCore.Plugin.RynthAi.Meta.MetaManager.TryMapVtOption("FollowNavMin", out string ra2) && ra2 == "FollowNavMin", "/vt opt follownavmin too");
    }

    private static void ClassifyArea()
    {
        // Landblock 0x01D9 is a dungeon here; its cells reach (72.05N, 100.75W) and (72.9N, 100.75W).
        const uint Dungeon = 0x01D9, Other = 0x01DA;
        var dungeons = new HashSet<uint> { Dungeon, Other };
        bool IsDungeon(uint lb) => dungeons.Contains(lb);
        bool Near(uint lb, double ns, double ew) =>
            lb == Dungeon && Math.Abs(ew - -100.75) < 0.01 && (Math.Abs(ns - 72.05) < 0.01 || Math.Abs(ns - 72.9) < 0.01);

        NavAreaCheck Classify(uint cell, double ns, double ew, out string route, out string player)
            => NavRecoveryPlanner.ClassifyArea(cell, ns, ew, IsDungeon, Near, out route, out player);

        const uint InDungeon = 0x01D90105, Outside = 0xA9B4001C, InBuilding = 0xA9B40105;

        Check.Eq(Classify(InDungeon, 72.05, -100.75, out _, out _), NavAreaCheck.Match, "same dungeon landblock");
        Check.Eq(Classify(InDungeon, 72.9, -100.75, out _, out _), NavAreaCheck.Match,
            "a waypoint mapping to the next landblock but near one of this dungeon's cells");
        Check.Eq(Classify(InDungeon, 72.85, -100.8, out string r1, out string p1), NavAreaCheck.Mismatch, "another dungeon");
        Check.Eq(r1, "dungeon 0x01DA", "names the other dungeon");
        Check.Eq(p1, "in dungeon 0x01D9", "names ours");
        Check.Eq(Classify(InDungeon, 42.1, 33.6, out string r2, out _), NavAreaCheck.Mismatch, "a landscape waypoint from a dungeon");
        Check.Eq(r2, "the landscape", "names the landscape");

        Check.Eq(Classify(Outside, 72.05, -100.75, out string r3, out string p3), NavAreaCheck.Mismatch, "a dungeon waypoint from the landscape");
        Check.Eq(r3, "dungeon 0x01D9", "names the dungeon");
        Check.Eq(p3, "on the landscape", "and where we are");
        Check.Eq(Classify(Outside, 72.3, -100.75, out _, out _), NavAreaCheck.Match,
            "in a dungeon landblock but near none of its cells: not provably in the dungeon");
        Check.Eq(Classify(Outside, 42.2, 33.7, out _, out _), NavAreaCheck.Match, "landscape to landscape");
        Check.Eq(Classify(InBuilding, 42.2, 33.7, out _, out _), NavAreaCheck.Match, "inside a building counts as the landscape");
        // A dungeon the landblock test misses (north-west corner, or terrain): the player is in
        // a cell, and a room past its landblock edge can sit near the next dungeon's cells.
        Check.Eq(Classify(InBuilding, 72.05, -100.75, out _, out _), NavAreaCheck.Match,
            "in a cell of a non-dungeon landblock: never provably elsewhere");
        // Pinned: a landscape waypoint in another town is the "same area", so nav walks there.
        Check.Eq(Classify(Outside, -30.0, 90.0, out _, out _), NavAreaCheck.Match, "a landscape waypoint across the world: Match (pinned)");

        Check.Eq(Classify(Outside, 0, -110, out _, out _), NavAreaCheck.Unknown, "west of the map: unknown");
        Check.Eq(Classify(Outside, 110, 0, out _, out _), NavAreaCheck.Unknown, "north of the map: unknown");

        // Holtburg (42.1N 33.6E) maps to landblock 0xA9B4.
        dungeons.Add(0xA9B4);
        Classify(InDungeon, 42.1, 33.6, out string holt, out _);
        Check.Eq(holt, "dungeon 0xA9B4", "42.1N 33.6E is landblock 0xA9B4");
    }

    // ── Bug tests (fixed by 91178c1, a8e5de0 and 5cfbcea) ─────────────────────

    private static void CampCircularHolds()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Circular, Pt(20, 0)));
        nav.Tick();
        Check.True(FakeHost.AutoRun, "walks to the camp spot");
        At(19.6, 0);
        for (int i = 0; i < 10; i++) nav.Tick();
        Check.False(FakeHost.AutoRun, "on the spot: autorun off");
        Check.True(LogCount("arrived at") <= 1, $"'arrived' logged at most once (got {LogCount("arrived at")})");
    }

    private static void CampLinearWalksBack()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Linear, Pt(20, 0)));
        nav.Tick();
        At(19.6, 0);
        nav.Tick(); nav.Tick();
        Check.Eq(nav.Idx, 0, "on the spot, the index stays on the point");
        At(0, 0);   // a fight took the character 20 yd away
        FakeHost.HeadingDeg = 0;
        nav.Tick(); nav.Tick();
        Check.Eq(nav.Idx, 0, "index still on the point");
        Check.True(FakeHost.AutoRun, "walks back to the point");
    }

    private static void TwoPointsOneSpot()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Circular, Pt(20, 0), Pt(20.3, 0)));
        nav.Tick();
        At(19.9, 0);
        nav.Tick();
        int worst = 0;
        foreach (string l in FakeHost.Logs)
        {
            var m = Regex.Match(l, @"skipped (\d+) dense");
            if (m.Success) worst = Math.Max(worst, int.Parse(m.Groups[1].Value));
        }
        Check.True(worst <= 1, $"at most one lap of skipping (skipped {worst})");
    }

    private static void OnceDenseEnd()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Once, Pt(10, 0), Pt(20, 0), Pt(20.5, 0), Pt(21, 0)), idx: 1);
        nav.Tick();
        At(19.8, 0);
        Exception? threw = null;
        try { nav.Tick(); } catch (Exception ex) { threw = ex; }
        Check.Null(threw?.GetType().Name, "no exception out of the tick");
        Check.False(nav.S.EnableNavigation, "the route finished: nav off");
    }

    private static void LinearIndexOffEnd()
    {
        FakeHost.Reset(); At(0, 0);
        var nav = new Nav(Route(RT.Linear, Pt(10, 0), Pt(20, 0), Pt(30, 0)), idx: 5);   // e.g. after a route edit
        nav.Tick(); nav.Tick(); nav.Tick();
        Check.Eq(nav.Idx, 2, "back on the route at the end it fell off");
        Check.True(FakeHost.AutoRun, "walking");
    }

    private static void ChatLoadsRoute()
    {
        var next = Route(RT.Circular, Pt(100, 0), Pt(110, 0), Pt(120, 0));

        FakeHost.Reset(); At(0, 0);
        var chat = Pt(0, 0, T.Chat); chat.ChatCommand = "/vt nav load next";
        var once = new Nav(Route(RT.Once, Pt(-10, 0), chat), idx: 1);
        once.E.ChatSubmit = _ => { once.S.CurrentRoute = next; once.S.ActiveNavIndex = 2; };   // what the loader does
        once.Tick();
        Check.True(ReferenceEquals(once.S.CurrentRoute, next), "the new route is loaded");
        Check.Eq(once.Idx, 2, "Once route ending in the chat point: the new route starts where its loader said");
        Check.True(once.S.EnableNavigation, "and nav stays on");

        FakeHost.Reset(); At(0, 0);
        var chat2 = Pt(0, 0, T.Chat); chat2.ChatCommand = "/vt nav load next";
        var circ = new Nav(Route(RT.Circular, Pt(-10, 0), chat2), idx: 1);
        circ.E.ChatSubmit = _ => { circ.S.CurrentRoute = next; circ.S.ActiveNavIndex = 2; };
        circ.Tick();
        Check.Eq(circ.Idx, 2, "Circular: the new route starts where its loader said");
    }
}

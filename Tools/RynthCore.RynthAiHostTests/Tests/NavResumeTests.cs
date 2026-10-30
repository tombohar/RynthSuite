using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using A = RynthCore.Plugin.RynthAi.LegacyUi.MetaActionType;
using C = RynthCore.Plugin.RynthAi.LegacyUi.MetaConditionType;
using RT = RynthCore.Plugin.RynthAi.LegacyUi.NavRouteType;

namespace RynthCore.RynthAiHostTests.Tests;

// Stopping and starting the macro must not restart the nav (Tom, 2026-10-28: "anything that was a
// 'once' instead of linear or circular gets restarted"). Starting the macro re-arms the meta's rules,
// so a state's "Always -> Embedded Nav Route" fired again and loaded its route fresh: a Once route
// went back to [0], a finished one ran again, a Linear route turned round. Here the meta and the nav
// engine run on one settings object in the plugin's tick order (the macro-start closest-waypoint pick,
// then nav, then the meta), on a fake clock and a fake character that only moves when told.
internal static class NavResumeTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const double NS0 = 42.0, EW0 = 33.0;
    private static long _now;

    public static void Register(Runner r)
    {
        r.Add("nav resume: stop + start keeps a meta's Once route on its waypoint (no restart from [0])", OnceKeepsWaypoint);
        r.Add("nav resume: stop + start keeps a Circular route's waypoint, also with Go To Closest Waypoint off", CircularKeepsWaypoint);
        r.Add("nav resume: stop + start keeps a Linear route's waypoint and its direction", LinearKeepsDirection);
        r.Add("nav resume: a finished Once route stays finished across stop + start (a replayed enablenav too)", FinishedOnceStaysFinished);
        r.Add("nav resume: loading the route again starts it fresh (state entry, edited, another route loaded)", LoadAgainStartsFresh);
        r.Add("nav resume: a rule that first fires after the start loads its route fresh", FirstFireAfterStartIsFresh);
        r.Add("nav resume: a replayed /vt nav load or /ra nav load of the running .nav keeps its place; a replayed /ra nav restart is skipped", ChatNavLoadKept);
        r.Add("nav resume: /ra nav restart runs the route from the top; the player's Nav switch re-runs a finished Once route", RestartCommand);
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private sealed class Bot
    {
        public readonly LegacyUiSettings S;
        public readonly NavigationEngine E;
        public readonly MetaManager M;
        public readonly List<string> Ra = new();
        public DateTime Now = new(2026, 10, 28, 12, 0, 0);
        private bool _wasRunning;

        /// <summary>Call FakeHost.Reset() and At(...) first. The first tick is a macro start (as at login).</summary>
        public Bot(params MetaRule[] rules)
        {
            _now = 1_000_000;
            NavigationEngine.Clock = static () => _now;
            var host = FakeHost.Create();
            S = new LegacyUiSettings { IsMacroRunning = true, EnableMeta = true, CurrentState = "Default" };
            S.MetaRules = rules.ToList();
            E = new NavigationEngine(host, S);
            M = new MetaManager(S, host, new PlayerVitalsCache()) { Clock = () => Now };
            M.SetRaCommandHandler(Ra.Add);
        }

        /// <summary>One plugin tick: the macro-start closest pick (RynthAiPlugin.OnTick), nav when the
        /// arbiter would run it, then the meta. <paramref name="metaSeconds"/> moves only the meta's clock.</summary>
        public void Tick(int ms = 100, double metaSeconds = 0)
        {
            _now += ms;
            Now = Now.AddMilliseconds(ms).AddSeconds(metaSeconds);
            if (!_wasRunning && S.IsMacroRunning) E.ResumeFromNearestWaypoint();
            _wasRunning = S.IsMacroRunning;
            if (S.IsMacroRunning && S.EnableNavigation && S.CurrentRoute.Points.Count > 0) E.Tick();
            M.Think();
        }

        public void Ticks(int n) { for (int i = 0; i < n; i++) Tick(); }

        /// <summary>/ra stop, a tick, /ra start, a few ticks.</summary>
        public void StopStart()
        {
            S.IsMacroRunning = false;
            E.Stop();
            Ticks(3);
            S.IsMacroRunning = true;
            Ticks(3);
        }

        public int Idx => S.ActiveNavIndex;
        public int Dir => E.GetStateSnapshot().LinearDir;
        public string Var(string name) => M.Expressions.Variables.TryGetValue(name, out string? v) ? v : "(unset)";
    }

    private static void At(double north, double east)
        => FakeHost.SetNavPosition(NS0 + north / 240.0, EW0 + east / 240.0);

    private static NavPoint Pt(double north, double east)
        => new() { Type = NavPointType.Point, NS = NS0 + north / 240.0, EW = EW0 + east / 240.0 };

    /// <summary>Points every 20 yd due north of the origin, from 0 (or 10 for <paramref name="from10"/>).</summary>
    private static NavRouteParser Line(RT type, int count, bool from10 = false)
    {
        var r = new NavRouteParser { RouteType = type };
        for (int i = 0; i < count; i++) r.Points.Add(Pt((from10 ? 10 : 0) + i * 20, 0));
        return r;
    }

    private static MetaRule Rule(string state, C cond, string condData, A act, string actData)
        => new() { State = state, Condition = cond, ConditionData = condData, Action = act, ActionData = actData };

    private static MetaRule EmbedNav(string name, C cond = C.Always, string condData = "")
        => Rule("Default", cond, condData, A.EmbeddedNavRoute, name);

    /// <summary>A bot whose meta loads <paramref name="route"/> as "R" with an Always rule; ticked until it runs.</summary>
    private static Bot Running(NavRouteParser route, params MetaRule[] more)
    {
        var bot = new Bot(new[] { EmbedNav("R") }.Concat(more).ToArray());
        bot.S.EmbeddedNavs["R"] = route.ToLines();
        bot.Tick();
        return bot;
    }

    // ── Stop + start keeps the place ──────────────────────────────────────────

    private static void OnceKeepsWaypoint()
    {
        FakeHost.Reset(); At(0, 0);
        var bot = Running(Line(RT.Once, 4, from10: true));   // [0] 10, [1] 30, [2] 50, [3] 70 yd north
        Check.Eq(bot.Idx, 0, "the meta loaded the Once route at [0]");
        var loaded = bot.S.CurrentRoute;
        At(10, 0); bot.Tick();
        At(30, 0); bot.Tick();
        Check.Eq(bot.Idx, 2, "walked on to [2]");
        At(40, 0); bot.Tick();                                 // on the leg to [2]

        bot.StopStart();
        Check.True(ReferenceEquals(bot.S.CurrentRoute, loaded), "the route was not loaded again");
        Check.Eq(bot.Idx, 2, "still heading for [2] (it went back to [0] before)");
        Check.True(bot.S.EnableNavigation, "nav on");
        At(50, 0); bot.Tick();
        Check.Eq(bot.Idx, 3, "and carries on to [3]");
    }

    private static void CircularKeepsWaypoint()
    {
        FakeHost.Reset(); At(0, 0);
        var bot = Running(Line(RT.Circular, 4));               // [0] 0, [1] 20, [2] 40, [3] 60
        bot.Tick();
        Check.Eq(bot.Idx, 1, "loaded at the nearest point [0], arrived, on to [1]");
        var loaded = bot.S.CurrentRoute;
        At(20, 0); bot.Tick();
        At(32, 0); bot.Tick();                                 // on the leg to [2]
        Check.Eq(bot.Idx, 2, "heading for [2]");

        bot.StopStart();
        Check.True(ReferenceEquals(bot.S.CurrentRoute, loaded), "the route was not loaded again");
        Check.Eq(bot.Idx, 2, "still heading for [2]");

        // Go To Closest Waypoint off: a loaded Circular route starts at [0], so the old reload sent it
        // back there; the waypoint is kept even after walking away from the route while stopped.
        bot.S.NavResumeNearest = false;
        bot.S.IsMacroRunning = false; bot.E.Stop(); bot.Ticks(2);
        At(5, 0); bot.Tick();
        bot.S.IsMacroRunning = true; bot.Ticks(3);
        Check.Eq(bot.Idx, 2, "option off: kept exactly");
        Check.True(ReferenceEquals(bot.S.CurrentRoute, loaded), "same route");
    }

    private static void LinearKeepsDirection()
    {
        FakeHost.Reset(); At(0, 0);
        var bot = Running(Line(RT.Linear, 3));                 // [0] 0, [1] 20, [2] 40
        bot.Tick();
        At(20, 0); bot.Tick();
        At(40, 0); bot.Tick();
        Check.Eq(bot.Idx, 1, "turned round at the end, heading back to [1]");
        Check.Eq(bot.Dir, -1, "heading back");
        At(33, 0); bot.Tick();                                 // on the way back, nearer [2] than [1]

        bot.StopStart();
        Check.Eq(bot.Idx, 1, "still heading back to [1] (the reload sent it to the nearest point, [2])");
        Check.Eq(bot.Dir, -1, "direction kept (the reload turned it round)");
        At(20, 0); bot.Tick();
        Check.Eq(bot.Idx, 0, "carries on back to [0]");
    }

    private static void FinishedOnceStaysFinished()
    {
        FakeHost.Reset(); At(0, 0);
        // As the CQ metas do: one Always rule switches nav on and loads the route.
        var all = Rule("Default", C.Always, "", A.All, "");
        all.ActionChildren.Add(new MetaRule { Action = A.ChatCommand, ActionData = "/vt opt set enablenav true" });
        all.ActionChildren.Add(new MetaRule { Action = A.EmbeddedNavRoute, ActionData = "R" });
        var done = Rule("Default", C.NavrouteEmpty, "", A.ExpressionAction, "setvar[done, getvar[done]+1]");
        var bot = new Bot(all, done);
        bot.S.EmbeddedNavs["R"] = Line(RT.Once, 2, from10: true).ToLines();
        bot.Tick();
        var loaded = bot.S.CurrentRoute;
        At(10, 0); bot.Tick();
        At(30, 0); bot.Tick();
        bot.Tick();
        Check.Eq(bot.Idx, 2, "finished: the index is past the end");
        Check.False(bot.S.EnableNavigation, "the end of a Once route turns nav off");
        Check.Eq(bot.Var("done"), "1", "Nav route empty fired");

        At(0, 0); bot.Tick();                                  // walks back to the start
        bot.StopStart();
        bot.Ticks(5);
        Check.True(ReferenceEquals(bot.S.CurrentRoute, loaded), "the route was not loaded again");
        Check.Eq(bot.Idx, 2, "still finished (it ran again from [0] before)");
        Check.False(bot.S.EnableNavigation, "nav off again after the replayed enablenav");
        Check.False(FakeHost.AutoRun, "standing still");
        Check.Eq(bot.Var("done"), "2", "Nav route empty is true straight away after the start");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("Once route re-armed")), 0, "never re-armed");

        // The engine alone: the macro-start closest pick leaves a finished route alone.
        bot.E.ResumeFromNearestWaypoint(always: true);
        Check.Eq(bot.Idx, 2, "the closest-waypoint pick (death recall, hazard escape) keeps it finished");
        // A meta or expression switching nav on: it stays finished.
        bot.S.EnableNavigation = true;
        bot.Ticks(2);
        Check.Eq(bot.Idx, 2, "nav switched on by a meta: still finished");
        Check.False(bot.S.EnableNavigation, "and nav off again");
    }

    // ── Loading again starts fresh ────────────────────────────────────────────

    private static void LoadAgainStartsFresh()
    {
        FakeHost.Reset(); At(0, 0);
        var bot = Running(Line(RT.Once, 4, from10: true));
        At(10, 0); bot.Tick();
        At(30, 0); bot.Tick();
        Check.Eq(bot.Idx, 2, "on [2]");

        // A real state entry (SetMetaState to the state it is in) loads it fresh, as before.
        var first = bot.S.CurrentRoute;
        bot.S.ForceStateReset = true;
        bot.Tick();
        Check.False(ReferenceEquals(bot.S.CurrentRoute, first), "state entry: loaded again");
        Check.Eq(bot.Idx, 0, "state entry: a Once route starts at [0]");

        // Edited while stopped: the replay loads the meta's copy fresh.
        At(10, 0); bot.Tick();
        At(30, 0); bot.Tick();
        Check.Eq(bot.Idx, 2, "on [2] again");
        bot.S.IsMacroRunning = false; bot.E.Stop(); bot.Ticks(2);
        var edited = bot.S.CurrentRoute;
        edited.Points.Add(Pt(90, 0));
        bot.S.IsMacroRunning = true; bot.Ticks(2);
        Check.False(ReferenceEquals(bot.S.CurrentRoute, edited), "edited: loaded again");
        Check.Eq(bot.S.CurrentRoute.Points.Count, 4, "from the meta's copy");
        Check.Eq(bot.Idx, 0, "edited: starts at [0]");

        // Another route loaded while stopped: the meta puts its own back, fresh.
        bot.S.IsMacroRunning = false; bot.E.Stop(); bot.Ticks(2);
        bot.S.CurrentRoute = Line(RT.Circular, 2);
        bot.S.CurrentNavPath = "<embedded:Other>";
        bot.S.ActiveNavIndex = 1;
        bot.S.IsMacroRunning = true; bot.Ticks(2);
        Check.Eq(bot.S.CurrentNavPath, "<embedded:R>", "the meta's route is back");
        Check.Eq(bot.S.CurrentRoute.RouteType, RT.Once, "Once");
        Check.Eq(bot.Idx, 0, "at [0]");
    }

    private static void FirstFireAfterStartIsFresh()
    {
        FakeHost.Reset(); At(0, 0);
        // Rule 2 reloads the same route after 30 s in the state; it hadn't fired when the macro stopped.
        var bot = Running(Line(RT.Once, 4, from10: true), EmbedNav("R", C.SecondsInState_GE, "30"));
        At(10, 0); bot.Tick();
        At(30, 0); bot.Tick();
        bot.StopStart();
        Check.Eq(bot.Idx, 2, "the replayed Always rule kept [2]");
        var kept = bot.S.CurrentRoute;
        bot.Tick(metaSeconds: 31);
        Check.False(ReferenceEquals(bot.S.CurrentRoute, kept), "the 30 s rule (a first fire, not a replay) loaded it again");
        Check.Eq(bot.Idx, 0, "fresh: [0]");
    }

    private static void ChatNavLoadKept()
    {
        FakeHost.Reset(); At(0, 0);
        string folder = Path.Combine(Program.TempRoot, "nav-resume", "NavProfiles");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "Hive.nav");
        Line(RT.Once, 4, from10: true).Save(path);

        foreach (string cmd in new[] { "/vt nav load Hive", "/ra nav load Hive.nav" })
        {
            var bot = new Bot(Rule("Default", C.Always, "", A.ChatCommand, cmd));
            bot.M.NavFolder = folder;
            bot.Tick();
            Check.Eq(bot.Ra.Count, 1, $"{cmd}: the first start loads it through /ra nav load");
            // What /ra nav load did (LegacyDashboardRenderer.LoadSelectedNav), then walked to [2].
            bot.S.CurrentNavPath = path;
            bot.S.CurrentRoute = NavRouteParser.Load(path);
            bot.S.ActiveNavIndex = 0;
            bot.S.EnableNavigation = true;
            var loaded = bot.S.CurrentRoute;
            At(10, 0); bot.Tick();
            At(30, 0); bot.Tick();
            Check.Eq(bot.Idx, 2, $"{cmd}: on [2]");

            bot.StopStart();
            Check.Eq(bot.Ra.Count, 1, $"{cmd}: the replay did not load it again");
            Check.True(ReferenceEquals(bot.S.CurrentRoute, loaded) && bot.Idx == 2, $"{cmd}: still on [2]");

            // The file changed on disk: the replay loads it again, as before.
            var changed = Line(RT.Once, 5, from10: true);
            changed.Save(path);
            bot.StopStart();
            Check.Eq(bot.Ra.Count, 2, $"{cmd}: a changed file is loaded again");
            Line(RT.Once, 4, from10: true).Save(path);
            At(0, 0);
        }

        // A state that restarts its route on entry: not again for a macro start.
        var restarter = new Bot(Rule("Default", C.Always, "", A.ChatCommand, "/ra nav restart"));
        restarter.Tick();
        Check.Eq(string.Join("|", restarter.Ra), "/ra nav restart", "on entering the state it runs");
        restarter.StopStart();
        Check.Eq(restarter.Ra.Count, 1, "a replayed /ra nav restart is not run again");
        restarter.S.ForceStateReset = true;
        restarter.Tick();
        Check.Eq(restarter.Ra.Count, 2, "a real state entry runs it again");
    }

    // ── /ra nav restart and the player's Nav switch ───────────────────────────

    private static void RestartCommand()
    {
        FakeHost.Reset(); At(0, 0);
        var s = new LegacyUiSettings { IsMacroRunning = true };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        void Set(string f, object v) => typeof(LegacyDashboardRenderer).GetField(f, Any)!.SetValue(dash, v);
        Set("_settings", s);
        Set("_host", FakeHost.Create());
        Set("_navFiles", new List<string>());
        Set("_metaFiles", new List<string>());
        Set("_profileListsLock", new object());
        // _settingsFilePath stays null: SaveSettings writes nothing.

        var plugin = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(plugin, new object[] { FakeHost.LastApi });
        _now = 1_000_000;
        NavigationEngine.Clock = static () => _now;
        var engine = new NavigationEngine(FakeHost.Create(), s);
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(plugin, dash);
        typeof(RynthAiPlugin).GetField("_navigationEngine", Any)!.SetValue(plugin, engine);
        MethodInfo dispatch = typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!;
        void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            dispatch.Invoke(plugin, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }
        void Tick() { _now += 100; engine.Tick(); }

        Ra("/ra nav restart");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("No nav route loaded to restart")), "nothing loaded: says so");

        // A finished Once route.
        s.CurrentRoute = Line(RT.Once, 2, from10: true);
        s.ActiveNavIndex = 2;
        s.EnableNavigation = false;
        FakeHost.Chat.Clear();
        Ra("/ra nav restart");
        Check.Eq(s.ActiveNavIndex, 0, "restart: back to [0]");
        Check.True(s.EnableNavigation, "restart: nav on");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("Nav route restarted from its first point (Once, 2 waypoints)")), "told: " + string.Join(" / ", FakeHost.Chat));
        Tick();
        Check.True(FakeHost.AutoRun, "and it walks");

        // A Linear route heading back starts out again.
        s.CurrentRoute = Line(RT.Linear, 3);
        s.ActiveNavIndex = 0;
        At(0, 0); Tick();
        At(20, 0); Tick();
        At(40, 0); Tick();
        Check.Eq(engine.GetStateSnapshot().LinearDir, -1, "Linear heading back");
        Ra("/ra nav restart");
        Check.Eq(s.ActiveNavIndex, 0, "Linear restart: [0]");
        Check.Eq(engine.GetStateSnapshot().LinearDir, 1, "Linear restart: heading out");

        // The player's Nav switch (dashboard, mini remote, phone) re-runs a finished Once route.
        s.CurrentRoute = Line(RT.Once, 2, from10: true);
        s.ActiveNavIndex = 2;
        s.EnableNavigation = false;
        dash.SetSubsystemEnabled(2, true);
        Check.Eq(s.ActiveNavIndex, 0, "Nav switch on: a finished Once route runs again from [0]");
        s.ActiveNavIndex = 1;
        dash.SetSubsystemEnabled(2, false);
        dash.SetSubsystemEnabled(2, true);
        Check.Eq(s.ActiveNavIndex, 1, "Nav switch off and on mid-route: the waypoint is kept");

        FakeHost.Chat.Clear();
        Ra("/ra nav");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("/ra nav restart")), "listed in the /ra nav usage");
    }
}

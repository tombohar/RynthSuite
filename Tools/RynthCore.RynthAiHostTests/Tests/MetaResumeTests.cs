using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using C = RynthCore.Plugin.RynthAi.LegacyUi.MetaConditionType;
using A = RynthCore.Plugin.RynthAi.LegacyUi.MetaActionType;

namespace RynthCore.RynthAiHostTests.Tests;

// Resume Meta After Reload (2026-11-07, Meta/MetaResume.cs). Tom: the meta should "restart in that state so
// Drakkon doesn't recall back every time", and "there might be some variables set in default of other
// states". A hot reload is modelled as what it is: the old MetaManager's runtime exported, then a NEW
// LegacyUiSettings + MetaManager built the way OnLoginComplete builds them (state Default, macro off, the
// meta's rules freshly loaded, no route), and the runtime restored into it.
internal static class MetaResumeTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 20, 0, 0);

    public static void Register(Runner r)
    {
        r.Add("meta resume: without it, a reload starts Default again and recalls (the reported problem)", WithoutResumeRecalls);
        r.Add("meta resume: a reload carries on in the state, with Default's variables, no second recall, the route at its waypoint", ResumeCarriesOn);
        r.Add("meta resume: after resuming, the meta runs on as before (timers continue, later states enter normally)", ResumedMetaRunsOn);
        r.Add("meta resume: call stack, watchdog and dictionaries come back", StackWatchdogDicts);
        r.Add("meta resume: stopwatches come back with their time (a map cooldown on a stopwatch fires again)", Stopwatches);
        r.Add("meta resume: a state entered but not yet applied is entered afresh", EntryPending);
        r.Add("meta resume: the file round-trips anything a variable holds; a torn file is nothing", FileFormat);
        r.Add("meta resume: only this client, this character, recently, with the same meta", WhyNotChecks);
    }

    private static List<string> Loop() => new()
    {
        "uTank2 NAV 1.2", "1", "4",
        "0", "10", "20", "0", "0",
        "0", "10.1", "20", "0", "0",
        "0", "10.1", "20.1", "0", "0",
        "0", "10", "20.1", "0", "0",
    };

    // A meta shaped like the reported one: Default sets the run up and recalls, then the hunt runs a route.
    private static List<MetaRule> Meta() => new()
    {
        new() { State = "Default", Condition = C.Always, Action = A.ExpressionAction, ActionData = "setvar[mode, hunt]" },
        new() { State = "Default", Condition = C.Always, Action = A.ChatCommand, ActionData = "/lifestone" },
        new() { State = "Default", Condition = C.SecondsInState_GE, ConditionData = "5", Action = A.SetMetaState, ActionData = "Hunt" },
        new() { State = "Hunt", Condition = C.Always, Action = A.EmbeddedNavRoute, ActionData = "Loop" },
        new() { State = "Hunt", Condition = C.Expression, ConditionData = "getvar[mode]==`hunt`", Action = A.ExpressionAction, ActionData = "setvar[seen, getvar[seen]+1]" },
        new() { State = "Hunt", Condition = C.SecondsInState_GE, ConditionData = "100", Action = A.SetMetaState, ActionData = "Default" },
    };

    private sealed class Client
    {
        public LegacyUiSettings S = null!;
        public MetaManager M = null!;
        public DateTime Now = T0;
        private readonly RynthCoreHost _host;

        public Client(RynthCoreHost host) { _host = host; Login(running: true); }

        /// <summary>What OnLoginComplete builds: a new settings object and manager, Default, the meta loaded.</summary>
        public void Login(bool running)
        {
            S = new LegacyUiSettings { IsMacroRunning = running, EnableMeta = true, CurrentState = "Default" };
            S.MetaRules = Meta();
            S.EmbeddedNavs["Loop"] = Loop();
            S.CurrentMetaPath = @"C:\Games\RynthSuite\RynthAi\MetaFiles\Hunt.af";
            M = new MetaManager(S, _host, new PlayerVitalsCache()) { Clock = () => Now };
            // Stopwatches on the same fake clock. A new copy's tick count has nothing to do with the old one's.
            long reloadBase = ++_logins * 7_000_000L;
            M.Expressions.TickMs = () => reloadBase + (long)(Now - T0).TotalMilliseconds;
        }

        private int _logins;

        public void Tick(double seconds = 0) { Now = Now.AddSeconds(seconds); M.Think(); }

        public MetaResumeState Export()
        {
            var s = new MetaResumeState { ProcessId = 1, ProcessStartUtcTicks = 2, CharacterId = 3, SavedUtcTicks = T0.Ticks };
            M.ExportRuntime(s);
            // Through the file format, as the plugin does.
            Check.True(MetaResumeStore.TryParse(MetaResumeStore.Serialize(s), out MetaResumeState back), "the save parses");
            return back;
        }

        public string Var(string name) => M.Expressions.Variables.TryGetValue(name, out string? v) ? v : "(unset)";
    }

    private static int Recalls() => FakeHost.ChatCommands.Count(c => c == "/lifestone");

    /// <summary>Runs Default, moves to Hunt, loads the route and walks it to waypoint 2.</summary>
    private static Client Hunting()
    {
        FakeHost.Reset();
        var c = new Client(FakeHost.Create(playerId: 0x50000A01));
        c.Tick();                 // Default: mode=hunt, /lifestone
        c.Tick(6);                // 5 s in Default: -> Hunt
        c.Tick();                 // Hunt: route loaded, seen=1
        c.S.ActiveNavIndex = 2;   // nav walked on
        c.Tick(3);
        Check.Eq(c.S.CurrentState, "Hunt", "hunting");
        Check.Eq(Recalls(), 1, "one recall at the start");
        Check.Eq(c.Var("seen"), "1", "Hunt's rule ran once");
        return c;
    }

    private static void WithoutResumeRecalls()
    {
        Client c = Hunting();
        c.Login(running: true);   // a reload without resume: Default again
        c.Tick();
        Check.Eq(c.S.CurrentState, "Default", "back in Default");
        Check.Eq(Recalls(), 2, "and it recalls again");
        Check.Eq(c.S.CurrentRoute.Points.Count, 0, "the route is gone until Hunt loads it again");
    }

    private static void ResumeCarriesOn()
    {
        Client c = Hunting();
        int pointsBefore = c.S.CurrentRoute.Points.Count;
        Check.Eq(pointsBefore, 4, "the route has its four waypoints");
        MetaResumeState saved = c.Export();
        c.Login(running: false);   // the new copy: Default, macro off (Start Macro On Login off)
        c.M.RestoreRuntime(saved);
        for (int i = 0; i < 5; i++) c.Tick(1);

        Check.Eq(c.S.CurrentState, "Hunt", "still hunting");
        Check.True(c.S.IsMacroRunning, "the macro runs as it did");
        Check.Eq(Recalls(), 1, "no second recall");
        Check.Eq(c.Var("mode"), "hunt", "Default's variable is there");
        Check.Eq(c.Var("seen"), "1", "Hunt's rules that had fired don't fire again");
        Check.Eq(c.S.CurrentRoute.Points.Count, pointsBefore, "the route is back");
        Check.Eq(c.S.ActiveNavIndex, 2, "at the same waypoint");
        Check.Eq(c.S.CurrentNavPath, "<embedded:Loop>", "the same route");
        Check.True(c.S.EnableNavigation, "nav on");
    }

    private static void ResumedMetaRunsOn()
    {
        Client c = Hunting();          // 3 s into Hunt
        MetaResumeState saved = c.Export();
        Check.True(saved.SecondsInState >= 3 && saved.SecondsInState < 4, "seconds in state saved: " + saved.SecondsInState.ToString(CultureInfo.InvariantCulture));
        c.Login(running: false);
        c.M.RestoreRuntime(saved);
        c.Tick(90);
        Check.Eq(c.S.CurrentState, "Hunt", "93 s in: still Hunt");
        c.Tick(8);                     // 101 s in Hunt, counting from before the reload
        Check.Eq(c.S.CurrentState, "Default", "the 100 s timer counted across the reload");
        c.Tick();
        Check.Eq(Recalls(), 2, "entering Default for real runs it as always");
    }

    // 2026-11-07: a reload kept $MapSW ("SW:4") but not the stopwatch, which then read 0 s for ever, so the
    // meta's "testvar[MapSW]==0||stopwatchelapsedseconds[$MapSW]>=$MapCool" never held and a treasure map
    // sat in the pack unused.
    private static void Stopwatches()
    {
        Client c = Hunting();
        string Eval(string e) => c.M.Expressions.Evaluate(e).ToString() ?? "";
        const string Cooldown = "testvar[MapSW]==0||stopwatchelapsedseconds[$MapSW]>=30";
        Eval("setvar[MapSW, stopwatchcreate[]];stopwatchstart[$MapSW]");
        Eval("setvar[Held, stopwatchcreate[]];stopwatchstart[$Held]");
        Eval("setvar[Idle, stopwatchcreate[]]");
        c.Tick(4);
        Eval("stopwatchstop[$Held]");
        c.Tick(6);                     // MapSW 10 s, Held stopped at 4 s, Idle never started
        Check.Eq(Eval("stopwatchelapsedseconds[$MapSW]"), "10", "10 s before the reload");
        Check.Eq(Eval(Cooldown), "0", "map cooldown still running");

        MetaResumeState saved = c.Export();
        Check.Eq(saved.Stopwatches.Count, 3, "three stopwatches saved");
        c.Login(running: false);
        Check.Eq(Eval("stopwatchelapsedseconds[$MapSW]"), "0", "(a new copy knows no stopwatches)");
        c.M.RestoreRuntime(saved);

        Check.Eq(Eval("stopwatchelapsedseconds[$MapSW]"), "10", "the stopwatch is back at 10 s");
        Check.Eq(Eval("stopwatchelapsedseconds[$Held]"), "4", "a stopped one keeps its time");
        Check.Eq(Eval("stopwatchelapsedseconds[$Idle]"), "0", "one never started is still at 0");
        c.Tick(21);
        Check.Eq(Eval("stopwatchelapsedseconds[$MapSW]"), "31", "it runs on after the reload");
        Check.Eq(Eval("stopwatchelapsedseconds[$Held]"), "4", "the stopped one doesn't");
        Check.Eq(Eval(Cooldown), "1", "the map cooldown ends (it never did before)");
        Eval("stopwatchstart[$Held]");
        c.Tick(2);
        Check.Eq(Eval("stopwatchelapsedseconds[$Held]"), "6", "a restored stopwatch starts again from its time");
        string fresh = Eval("stopwatchcreate[]");
        Check.True(!saved.Stopwatches.ContainsKey(fresh), "a new stopwatch never reuses a saved handle: " + fresh);

        string text = MetaResumeStore.Serialize(saved);
        Check.False(MetaResumeStore.TryParse(text.Replace("sw=SW%3A0|", "sw=SW%3A0|x"), out _), "a bad stopwatch time is nothing");
        Check.True(MetaResumeStore.TryParse(text.Replace("sw=", "zz="), out MetaResumeState old), "an older save without stopwatches still parses");
        Check.Eq(old.Stopwatches.Count, 0, "(and has none)");
    }

    private static void StackWatchdogDicts()
    {
        FakeHost.Reset();
        var c = new Client(FakeHost.Create(playerId: 0x50000A01));
        c.S.MetaRules = new List<MetaRule>
        {
            new() { State = "Default", Condition = C.Always, Action = A.ExpressionAction, ActionData = "setvar[d, dictcreate[]]" },
            new() { State = "Default", Condition = C.Always, Action = A.ExpressionAction, ActionData = "dictadditem[getvar[d], loot, 7]" },
            new() { State = "Default", Condition = C.Always, Action = A.CallMetaState, ActionData = "Buff" },
            new() { State = "Buff", Condition = C.Always, Action = A.SetWatchdog, ActionData = "Stuck;5;60" },
            new() { State = "Buff", Condition = C.SecondsInState_GE, ConditionData = "30", Action = A.ReturnFromCall },
        };
        c.Tick();
        c.Tick();
        c.Tick(2);
        Check.Eq(c.S.CurrentState, "Buff", "called into Buff");
        MetaResumeState saved = c.Export();
        Check.Eq(saved.CallStack.Count, 1, "one call deep");
        Check.True(saved.WatchdogActive, "watchdog armed: " + saved.WatchdogState);

        var rules = c.S.MetaRules;
        c.Login(running: false);
        c.S.MetaRules = rules.Select(r => new MetaRule { State = r.State, Condition = r.Condition, ConditionData = r.ConditionData, Action = r.Action, ActionData = r.ActionData }).ToList();
        c.M.RestoreRuntime(saved);
        Check.Eq(c.M.Expressions.Evaluate("dictgetitem[getvar[d], loot]").ToString(), "7", "the dictionary behind the handle is back");
        c.Tick(29);
        Check.Eq(c.S.CurrentState, "Default", "the Return pops the restored stack");
    }

    private static void EntryPending()
    {
        Client c = Hunting();
        c.M.Expressions.Evaluate("setmetastate[Default]");   // asked for, not applied by a tick yet
        MetaResumeState saved = c.Export();
        Check.True(saved.EntryPending, "pending entry saved");
        Check.Eq(saved.FiredRules.Count, 0, "the old state's fired marks aren't carried");
        c.Login(running: false);
        c.M.RestoreRuntime(saved);
        c.Tick();
        Check.Eq(c.S.CurrentState, "Default", "Default entered");
        Check.Eq(Recalls(), 2, "entered for real, as the tick would have");
    }

    private static void FileFormat()
    {
        var s = new MetaResumeState
        {
            ProcessId = 4321, ProcessStartUtcTicks = 638_000_000_000_000_000, CharacterId = 0x50000A01, SavedUtcTicks = T0.Ticks,
            MetaPath = @"C:\Metas\Odd|name,with:colons.af", RulesSignature = "6:ABCDEF", MacroRunning = true,
            State = "Hunt | 2", SecondsInState = 12.5, SecondsInStateP = 40.25, EntryPending = false,
            WatchdogActive = true, WatchdogState = "Stuck", WatchdogMeters = 5, WatchdogSeconds = 60, WatchdogSecondsLeft = 41.5,
            NextDictId = 9, NavPath = "<embedded:Loop>", NavIndex = 3, NavEnabled = true,
        };
        s.CallStack.Add(("Default", 3.5));
        s.CallStack.Add(("Buff", null));
        s.FiredRules.AddRange(new[] { 0, 4, 7 });
        s.Variables["note"] = "line one\nline two | with, commas: and =";
        s.Variables["empty"] = "";
        s.Dicts["D:3"] = new Dictionary<string, string> { ["k:1"] = "v,1", ["k2"] = "" };
        s.RouteLines.AddRange(Loop());

        string text = MetaResumeStore.Serialize(s);
        Check.True(MetaResumeStore.TryParse(text, out MetaResumeState b), "parses");
        Check.Eq(b.MetaPath, s.MetaPath, "path");
        Check.Eq(b.State, s.State, "state");
        Check.Eq(b.Variables["note"], s.Variables["note"], "a variable with newlines and separators");
        Check.Eq(b.Variables["empty"], "", "an empty variable");
        Check.Eq(b.Dicts["D:3"]["k:1"], "v,1", "a dictionary");
        Check.Eq(string.Join(";", b.CallStack.Select(x => x.State + "=" + (x.PSeconds?.ToString(CultureInfo.InvariantCulture) ?? "-"))), "Default=3.5;Buff=-", "call stack, bottom first");
        Check.Eq(string.Join(",", b.FiredRules), "0,4,7", "fired rules");
        Check.Eq(b.RouteLines.Count, s.RouteLines.Count, "route lines");
        Check.Eq(b.WatchdogSecondsLeft, 41.5, "watchdog time left");
        Check.False(MetaResumeStore.TryParse(text.Replace("end=1\n", ""), out _), "a torn file (no end) is nothing");
        Check.False(MetaResumeStore.TryParse("something else\n" + text, out _), "another header is nothing");
        Check.False(MetaResumeStore.TryParse(text.Replace("secs=12.5", "secs=abc"), out _), "a bad number is nothing");
    }

    private static void WhyNotChecks()
    {
        List<MetaRule> rules = Meta();
        string sig = MetaResumeStore.Signature(rules);
        var s = new MetaResumeState { ProcessId = 10, ProcessStartUtcTicks = 20, CharacterId = 30, SavedUtcTicks = T0.Ticks, MetaPath = @"C:\M\a.af", RulesSignature = sig };
        DateTime now = T0.AddMinutes(1);
        Check.Null(MetaResumeStore.WhyNot(s, 10, 20, 30, @"c:\m\A.af", sig, now), "same client, character and meta (path case ignored)");
        Check.NotNull(MetaResumeStore.WhyNot(s, 11, 20, 30, @"C:\M\a.af", sig, now), "another client");
        Check.NotNull(MetaResumeStore.WhyNot(s, 10, 21, 30, @"C:\M\a.af", sig, now), "a restarted client with the same pid");
        Check.NotNull(MetaResumeStore.WhyNot(s, 10, 20, 31, @"C:\M\a.af", sig, now), "another character");
        Check.NotNull(MetaResumeStore.WhyNot(s, 10, 20, 30, @"C:\M\b.af", sig, now), "another meta");
        Check.NotNull(MetaResumeStore.WhyNot(s, 10, 20, 30, @"C:\M\a.af", sig, T0.AddMinutes(11)), "too old");
        rules[1].ActionData = "/recall";
        Check.NotNull(MetaResumeStore.WhyNot(s, 10, 20, 30, @"C:\M\a.af", MetaResumeStore.Signature(rules), now), "the meta's rules were edited");
        Check.Eq(MetaResumeStore.Signature(Meta()), sig, "the same rules sign the same");
    }
}

using System;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Keep Hunting After Death (2026-11-13): a death pauses the Outdoor Hunt instead of ending it, and it
// starts again once the character is back up; the Dungeon Patrol follows into a dungeon a death sent it to.
internal static class KeepHuntingAfterDeathTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("keep hunting (11-13): the restart waits for the death's steps; the box off, the macro off or another route: it stays off", Decide);
        r.Add("keep hunting (11-13): the patrol follows into a dungeon a death sent it to only with the box on", PatrolFollow);
        r.Add("keep hunting (11-13): a death with the box on pauses the hunt, back up it starts again (and says so); off, it ends as before", PauseAndRestart);
        r.Add("keep hunting (11-13): a meta's route loaded after the death, or the macro stopped: not started again, said in chat", Dropped);
        r.Add("keep hunting (11-13): /ra hunt death answers, sets it, says why not with the usage", Command);
        r.Add("keep hunting (11-13): KeepHuntingAfterDeath is off by default and round-trips (saved, copied)", Setting);
        r.Add("hunt (2026-11-16): a walk that ran out leaves nav on with no walk left, not off until the next walk", NavStaysOn);
    }

    private static void Decide()
    {
        var S = AfterDeath.HuntStep.Start;
        var W = AfterDeath.HuntStep.Wait;
        Check.Eq(AfterDeath.DecideHunt(true, true, false, false, false), S, "back up, nothing else to do: start");
        Check.Eq(AfterDeath.DecideHunt(true, true, false, true, false), W, "a recall or the corpse trip running: wait");
        Check.Eq(AfterDeath.DecideHunt(true, true, false, false, true), W, "portal space: wait");
        Check.Eq(AfterDeath.DecideHunt(false, true, false, false, false), AfterDeath.HuntStep.DropOff, "the box turned off: dropped");
        Check.Eq(AfterDeath.DecideHunt(true, false, false, true, false), AfterDeath.HuntStep.DropMacro, "the macro stopped: dropped, even mid-recall");
        Check.Eq(AfterDeath.DecideHunt(true, true, true, true, false), AfterDeath.HuntStep.DropRoute, "another route: dropped, even mid-recall");
    }

    private static void PatrolFollow()
    {
        const uint patrolLb = 0x5756, other = 0x00830188;
        var D = RynthAiPlugin.PatrolFollow.AfterDeath;
        var F = RynthAiPlugin.PatrolFollow.Follow;
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, other, true, false, uint.MaxValue, 30_000, true), D, "box off: as before, not after a death");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, other, true, false, uint.MaxValue, 30_000, true, followAfterDeath: true), F, "box on: follows");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, 0xC4B60002, false, false, uint.MaxValue, 30_000, true, followAfterDeath: true),
            RynthAiPlugin.PatrolFollow.None, "box on, landed outdoors: nothing (the patrol waits for its dungeon)");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, other, true, true, uint.MaxValue, 30_000, true, followAfterDeath: true),
            RynthAiPlugin.PatrolFollow.None, "box on, mid-portal: wait");
    }

    private sealed class Rig
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyDashboardRenderer Dash;
        public LegacyUiSettings S => Dash.Settings;

        public Rig()
        {
            FakeHost.Reset();
            var host = FakeHost.Create();
            typeof(RynthCore.PluginCore.RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            Dash = new LegacyDashboardRenderer(host);
            typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(P, Dash);
            FakeHost.HasPose = true;
            FakeHost.PlayerPose = (0xA9B40021u, 100f, 100f, 50f);
        }

        public object? Get(string f) => typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P);
        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public void Call(string m) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, null);

        /// <summary>A hunt running (its route the current one), the macro on.</summary>
        public void Hunting()
        {
            S.IsMacroRunning = true;
            var hunt = (OutdoorHunt)Get("_hunt")!;
            hunt.Start(DateTime.UtcNow, new OutdoorHunt.Vec(100, 100, 50));
            var route = new NavRouteParser { RouteType = NavRouteType.Once, OutdoorHuntWalk = true };
            Set("_huntRoute", route);
            S.CurrentNavPath = string.Empty;
            S.CurrentRoute = route;
            S.EnableNavigation = true;
        }

        public bool Pending => (bool)Get("_huntAfterDeath")!;
        public string All => string.Join(" | ", FakeHost.Chat);
    }

    private static void NavStaysOn()
    {
        var g = new Rig();
        g.Hunting();
        var walk = (NavRouteParser)g.Get("_huntRoute")!;
        walk.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 1, EW = 1 });
        g.S.ActiveNavIndex = 1;          // walked off its end
        g.S.EnableNavigation = false;    // what the nav engine does then (CompleteOnceRoute)
        Check.True(g.S.NavRouteFinished(), "the walk is a finished Once route");
        g.Call("TickOutdoorHunt");
        Check.True(g.S.EnableNavigation, "nav on again at once");
        Check.True(ReferenceEquals(g.S.CurrentRoute, g.Get("_huntRoute")) && g.S.CurrentRoute.Points.Count == 0,
            "with an empty walk: nothing for the arbiter to hold Navigating for");
        Check.True(g.P.HuntActive, "and the hunt still on");

        // The player turning nav off mid-walk is left alone (a walk still to go: not finished).
        var h = new Rig();
        h.Hunting();
        var w2 = (NavRouteParser)h.Get("_huntRoute")!;
        w2.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 1, EW = 1 });
        w2.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 2, EW = 2 });
        h.S.ActiveNavIndex = 1;
        h.S.EnableNavigation = false;
        h.Call("TickOutdoorHunt");
        Check.False(h.S.EnableNavigation, "nav off mid-walk: left off");
    }

    private static void PauseAndRestart()
    {
        var g = new Rig();
        g.Hunting();
        g.S.KeepHuntingAfterDeath = true;
        FakeHost.Chat.Clear();
        g.Call("PauseHuntForDeath");
        Check.False(g.P.HuntActive, "the hunt is off while dead");
        Check.True(g.Pending, "and waits to start again");
        Check.True(g.All.Contains("starts again once you're back up"), "said in chat: " + g.All);

        g.Set("_deathHolding", true);
        g.Call("TickOutdoorHunt");
        Check.True(g.Pending && !g.P.HuntActive, "still dead or settling: waits");
        g.Call("PauseHuntForDeath");
        Check.True(g.Pending, "died again while waiting: still waits");

        g.Set("_deathHolding", false);
        FakeHost.Chat.Clear();
        g.Call("TickOutdoorHunt");
        Check.False(g.Pending, "back up: no longer waiting");
        // No RynthNav on the fake host: the Hunt button's own check refuses, under this box's name.
        Check.True(g.All.Contains(RynthAiPlugin.KeepHuntingRefusedPrefix.Trim()) && g.All.Contains("needs RynthNav"),
            "it tried to start the hunt again, from here, and says why it couldn't: " + g.All);

        var off = new Rig();
        off.Hunting();
        FakeHost.Chat.Clear();
        off.Call("PauseHuntForDeath");
        Check.False(off.P.HuntActive || off.Pending, "box off: the death ends the hunt, nothing waits");
        Check.True(off.All.Contains("Outdoor hunt off (died)"), "said as before: " + off.All);
    }

    private static void Dropped()
    {
        var g = new Rig();
        g.Hunting();
        g.S.KeepHuntingAfterDeath = true;
        g.Call("PauseHuntForDeath");
        g.S.CurrentRoute = new NavRouteParser();
        g.S.CurrentNavPath = "<embedded:BackToCamp>";
        FakeHost.Chat.Clear();
        g.Call("TickOutdoorHunt");
        Check.False(g.Pending || g.P.HuntActive, "a meta's route after the death: the hunt stays off");
        Check.True(g.All.Contains(RynthAiPlugin.KeepHuntingRefusedPrefix.Trim()) && g.All.Contains("the meta's route 'BackToCamp'"),
            "and says whose route it is: " + g.All);

        var m = new Rig();
        m.Hunting();
        m.S.KeepHuntingAfterDeath = true;
        m.Call("PauseHuntForDeath");
        m.S.IsMacroRunning = false;
        FakeHost.Chat.Clear();
        m.Call("TickOutdoorHunt");
        Check.False(m.Pending || m.P.HuntActive, "the macro stopped: the hunt stays off");
        Check.True(m.All.Contains("the macro is off"), "said: " + m.All);

        var stopped = new Rig();
        stopped.Hunting();
        stopped.S.KeepHuntingAfterDeath = true;
        stopped.S.IsMacroRunning = false;   // Stop Macro On Death stopped it first
        stopped.Call("PauseHuntForDeath");
        Check.False(stopped.Pending, "Stop Macro On Death wins: nothing waits");
    }

    private static void Command()
    {
        var g = new Rig();
        void Ra(string line)
        {
            FakeHost.Chat.Clear();
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(g.P, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }
        Ra("/ra hunt death");
        Check.True(g.All.Contains("Keep Hunting After Death: off") && g.All.Contains(RynthAiPlugin.KeepHuntingUsage), "no word: off, and the usage: " + g.All);
        Ra("/ra hunt death on");
        Check.True(g.S.KeepHuntingAfterDeath && g.All.Contains("Keep Hunting After Death set: on"), "on, said: " + g.All);
        Ra("/ra hunt death sometimes");
        Check.True(g.S.KeepHuntingAfterDeath && g.All.Contains("isn't on or off") && g.All.Contains("Usage: " + RynthAiPlugin.KeepHuntingUsage),
            "an unknown word changes nothing, says why: " + g.All);
        Ra("/ra hunt death off");
        Check.True(!g.S.KeepHuntingAfterDeath && g.All.Contains("set: off"), "off, said: " + g.All);
        Ra("/ra hunt nonsense");
        Check.True(g.All.Contains(RynthAiPlugin.KeepHuntingUsage), "the hunt's usage lists it: " + g.All);
    }

    private static void Setting()
    {
        Check.False(new LegacyUiSettings().KeepHuntingAfterDeath, "off by default");
        var ctx = RynthAiJsonContext.Default.LegacyUiSettings;
        var saved = JsonSerializer.Deserialize(JsonSerializer.Serialize(new LegacyUiSettings { KeepHuntingAfterDeath = true }, ctx), ctx)!;
        Check.True(saved.KeepHuntingAfterDeath, "saved");
        var live = new LegacyUiSettings();
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", Any)!.Invoke(null, new object[] { saved, live });
        Check.True(live.KeepHuntingAfterDeath, "copied");
        Check.False(JsonSerializer.Deserialize("{\"HuntOnLogin\":true}", ctx)!.KeepHuntingAfterDeath, "a profile from before the setting: off");
    }
}

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Dungeon Patrol as the chosen mode (2026-11-01): it stays the route through a macro stop and
// start, ends (said in chat) only when another route is loaded in its place, and follows the
// character through a portal into another dungeon (not after a death). The patrol tick
// (TickDunPatrolHazardReroute) is driven directly, as ServerPolicyTests does.
internal static class PatrolModeTests
{
    private const uint Player = 0x50000D01;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const uint PatrolCell = 0x57560124, OtherDungeonCell = 0x00830188, OutdoorCell = 0xC4B60002;

    public static void Register(Runner r)
    {
        r.Add("patrol mode: a macro stop and start keeps the patrol as the route and as the mode", StopStartKeepsPatrol);
        r.Add("patrol mode: a nav file loaded in its place ends it, said in chat with the file's name", ReplacedSaysSo);
        r.Add("patrol mode: through a portal taken while it runs it follows into another dungeon; not after a death or with the macro off, not into a building, once per dungeon", FollowDecision);
    }

    private sealed class Rig
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new() { IsMacroRunning = true, EnableNavigation = true };

        public Rig()
        {
            FakeHost.Reset();
            ServerPolicy.Forget();
            FakeHost.WorldName = "Conquest";
            FakeHost.Names[Player] = "Mage";
            FakeHost.PlayerPose = (PatrolCell, 70f, -80f, 0f);
            FakeHost.Create(Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(P, dash);
        }

        private object? Get(string f) => typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P);
        private void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);

        public bool Active => (bool)Get("_dunPatrolActive")!;
        public void Tick() => typeof(RynthAiPlugin).GetMethod("TickDunPatrolHazardReroute", Any)!.Invoke(P, null);

        /// <summary>A patrol as BuildDunPatrol leaves it: the route, its object remembered, nav on, this dungeon.</summary>
        public NavRouteParser RunningPatrol()
        {
            var route = new NavRouteParser { RouteType = NavRouteType.Circular };
            route.Points.Add(new NavPoint { NS = -33.3, EW = -32.3 });
            route.Points.Add(new NavPoint { NS = -33.2, EW = -32.3 });
            S.CurrentNavPath = string.Empty;
            S.CurrentRoute = route;
            Set("_dunPatrolActive", true);
            Set("_dunPatrolRoute", route);
            Set("_dunPatrolLandblock", PatrolCell >> 16);
            return route;
        }
    }

    private static bool Said(string text) => FakeHost.Chat.Exists(l => l.Contains(text, StringComparison.Ordinal));

    private static void StopStartKeepsPatrol()
    {
        var b = new Rig();
        var patrol = b.RunningPatrol();
        b.Tick();
        Check.True(b.Active, "running: the patrol is the mode");

        b.S.IsMacroRunning = false;   // the player stops the macro
        b.Tick();
        Check.True(b.Active, "stopped: still the mode (it used to be stood down here)");
        Check.True(ReferenceEquals(b.S.CurrentRoute, patrol), "and still the route");

        b.S.IsMacroRunning = true;    // ... and starts it again
        b.Tick();
        Check.True(b.Active, "started again: the patrol carries on");
        Check.True(ReferenceEquals(b.S.CurrentRoute, patrol), "the same route, not the nav file loaded before it");

        b.S.EnableNavigation = false; b.Tick(); b.S.EnableNavigation = true; b.Tick();
        Check.True(b.Active, "nav switched off and on: the same");
        Check.False(Said("Dungeon Patrol ended"), "nothing said");
    }

    private static void ReplacedSaysSo()
    {
        var b = new Rig();
        b.RunningPatrol();
        b.Tick();
        // The player picks a nav file (LoadSelectedNav: the path and a new route).
        var file = new NavRouteParser { RouteType = NavRouteType.Circular };
        file.Points.Add(new NavPoint { NS = 42.4, EW = 54.9 });
        b.S.CurrentNavPath = @"C:\Games\RynthSuite\RynthAi\NavProfiles\ToSomewhere.nav";
        b.S.CurrentRoute = file;
        b.Tick();
        Check.False(b.Active, "the patrol ended");
        Check.True(Said("Dungeon Patrol ended: nav route 'ToSomewhere' is the route now"), "said in chat, naming the file");
        Check.True(FakeHost.Logs.Exists(l => l.Contains("DunNav-Patrol: ended")), "and logged");
        Check.True(ReferenceEquals(b.S.CurrentRoute, file), "the loaded route is left alone");

        // A meta's route in its place is named as the meta's.
        var m = new Rig();
        m.RunningPatrol();
        var emb = new NavRouteParser { RouteType = NavRouteType.Linear };
        emb.Points.Add(new NavPoint { NS = 1, EW = 1 });
        m.S.CurrentNavPath = "<embedded:Hunt>";
        m.S.CurrentRoute = emb;
        m.Tick();
        Check.True(Said("the meta's route 'Hunt'"), "a meta's route is named as the meta's");
    }

    private static void FollowDecision()
    {
        uint patrolLb = PatrolCell >> 16;
        var F = RynthAiPlugin.PatrolFollow.Follow;
        var N = RynthAiPlugin.PatrolFollow.None;
        var D = RynthAiPlugin.PatrolFollow.AfterDeath;
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, OtherDungeonCell, true, false, uint.MaxValue, -1, true), F, "portalled into another dungeon while running: follow");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, OtherDungeonCell, true, false, uint.MaxValue, -1, false), RynthAiPlugin.PatrolFollow.CameWhileStopped, "got there with the macro off (a failsafe recall, by hand): says so, doesn't follow");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, PatrolCell + 5, true, false, uint.MaxValue, -1, true), N, "same dungeon: nothing");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, OutdoorCell, false, false, uint.MaxValue, -1, true), N, "the landscape: nothing (nav holds and says so)");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, 0xC4B60105, false, false, uint.MaxValue, -1, true), N, "a building on the landscape: not a dungeon");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, OtherDungeonCell, true, true, uint.MaxValue, -1, true), N, "mid-portal: wait");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, OtherDungeonCell, true, false, OtherDungeonCell >> 16, -1, true), N, "tried there already: once per dungeon");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, OtherDungeonCell, true, false, uint.MaxValue, 30_000, true), D, "30 s after a death: says so, doesn't follow");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, OtherDungeonCell, true, false, uint.MaxValue, RynthAiPlugin.PatrolFollowDeathMs + 1, true), F, "long after a death: follows");
        Check.Eq(RynthAiPlugin.DecidePatrolFollow(patrolLb, 0, true, false, uint.MaxValue, -1, true), N, "no pose: nothing");
    }
}

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The dungeon patrol is off in Threads on DreamWeave (its owner asked, 2026-10-15) and only there: ServerPolicy,
// asked by BuildDunPatrol, the one way into the patrol (command, dashboard Patrol button, a meta's "Patrol", the
// hazard reroute). A Thread is known from DreamWeave's chat: "The Thread Gem opens a way into ..." and the
// "[REALM] ... eph=1" line after each teleport; leaving the run's landblock or an eph=0 line ends it.
internal static class ServerPolicyTests
{
    private const uint Player = 0x50000D01;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    // DreamWeave's lines, as our clients logged them (2026-10-12).
    private const string GemLine = "The Thread Gem opens a way into Watery Grotto. It will bring you back 5 more time(s) while the dungeon holds.";
    private const string RealmThread = "[REALM] id=0 inst=7841 eph=1 name=Base World";
    private const string RealmWorld = "[REALM] id=0 inst=0 eph=0 name=Base World";
    private const string RealmHub = "[REALM] id=1 inst=0 eph=0 name=Weave Content 1";

    private const uint HubCell = 0x00070154;      // where the gem is used (a made-up indoor hub cell)
    private const uint ThreadCell = 0x017B0154;   // Watery Grotto, as the logged teleport landed
    private const uint OtherDungeonCell = 0x01CD0102;

    public static void Register(Runner r)
    {
        r.Add("server policy: which world names keep the dungeon patrol off in Threads", WorldNames);
        r.Add("server policy: DreamWeave's Thread lines start a run and the eph=0 line or another landblock ends it", ThreadLines);
        r.Add("server policy: in a Thread on DreamWeave the patrol is refused with a message", RefusedInAThread);
        r.Add("server policy: outside a Thread on DreamWeave, and on any other server, the patrol isn't refused", AllowedElsewhere);
        r.Add("server policy: a patrol running when the character enters a Thread is stood down", StopsARunningPatrol);
        r.Add("server policy: only the patrol is off (the dunnav-to-a-cell command still runs in a Thread)", OnlyThePatrol);
    }

    private sealed class Rig
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new() { IsMacroRunning = true };
        public readonly RynthCoreHost Host;

        public Rig(string world)
        {
            FakeHost.Reset();
            ServerPolicy.Forget();
            FakeHost.WorldName = world;
            FakeHost.Names[Player] = "Mage";
            FakeHost.SetNavPosition(42.1, 33.6);   // outdoors: a patrol that gets past the gate says "inside a dungeon"
            Host = FakeHost.Create(Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(P, dash);
        }

        public bool Active { get => (bool)typeof(RynthAiPlugin).GetField("_dunPatrolActive", Any)!.GetValue(P)!; set => typeof(RynthAiPlugin).GetField("_dunPatrolActive", Any)!.SetValue(P, value); }
        public void Patrol() => P.HandleDungeonNavPatrol();
        public void Tick() => typeof(RynthAiPlugin).GetMethod("TickDunPatrolHazardReroute", Any)!.Invoke(P, null);
        public void Chat(string text) => typeof(RynthAiPlugin).GetMethod("NoteServerPolicyChat", Any)!.Invoke(P, new object[] { text });
        public void At(uint cell) => FakeHost.PlayerPose = (cell, 70f, -80f, 0f);

        /// <summary>Uses a Thread Gem in the hub and lands in Watery Grotto, as the logs show it.</summary>
        public void EnterThread()
        {
            At(HubCell);
            Chat(GemLine);
            At(ThreadCell);
            Chat(RealmThread);
        }

        public void RunningPatrol()
        {
            Active = true;
            S.EnableNavigation = true;
            S.CurrentRoute = new NavRouteParser { RouteType = NavRouteType.Circular };
            S.CurrentRoute.Points.Add(new NavPoint { NS = 42.0, EW = 33.0 });
        }
    }

    private static bool Said(string text) => FakeHost.Chat.Exists(l => l.Contains(text, StringComparison.Ordinal));

    private static void WorldNames()
    {
        Check.True(ServerPolicy.PatrolOffInThreadsOn("DreamWeave"), "DreamWeave");
        Check.True(ServerPolicy.PatrolOffInThreadsOn("dreamweave"), "any case");
        Check.True(ServerPolicy.PatrolOffInThreadsOn("DreamWeave PvE"), "decorated");
        Check.False(ServerPolicy.PatrolOffInThreadsOn("Aelrynth"), "Aelrynth");
        Check.False(ServerPolicy.PatrolOffInThreadsOn("Aeshnidae"), "Aeshnidae");
        Check.False(ServerPolicy.PatrolOffInThreadsOn("Dream"), "only part of the name");
        Check.False(ServerPolicy.PatrolOffInThreadsOn(""), "empty");
        Check.False(ServerPolicy.PatrolOffInThreadsOn(null), "null");
    }

    private static void ThreadLines()
    {
        ServerPolicy.Forget();
        Check.False(ServerPolicy.InThread(ThreadCell), "nothing said yet: not in a Thread");

        // The gem line in the hub: a run that learns its landblock after the teleport.
        Check.True(ServerPolicy.NoteChat(GemLine, HubCell), "the gem line is a Thread line");
        Check.True(ServerPolicy.InThread(HubCell), "still in the hub, waiting for the teleport");
        Check.True(ServerPolicy.InThread(ThreadCell), "landed in the Thread");
        Check.Eq(ServerPolicy.Run!.Value.Landblock, ThreadCell >> 16, "the run is Watery Grotto's landblock");
        Check.True(ServerPolicy.InThread(ThreadCell + 3), "another cell of the same dungeon");
        Check.False(ServerPolicy.InThread(OtherDungeonCell), "another landblock: out of the Thread");
        Check.True(ServerPolicy.Run == null, "and the run is over");

        // The [REALM] line alone (a login or re-entry inside a Thread).
        Check.True(ServerPolicy.NoteChat(RealmThread, ThreadCell), "eph=1 is a Thread line");
        Check.True(ServerPolicy.InThread(ThreadCell), "eph=1: in a Thread");
        Check.True(ServerPolicy.NoteChat(RealmHub, HubCell), "eph=0 is a Thread line too");
        Check.False(ServerPolicy.InThread(HubCell), "eph=0: out");
        ServerPolicy.NoteChat(RealmThread, ThreadCell);
        Check.True(ServerPolicy.NoteChat(RealmWorld, ThreadCell), "Base World eph=0");
        Check.False(ServerPolicy.InThread(ThreadCell), "the same dungeon outside a Thread is not a Thread");

        // Not Thread lines: other chat, a player quoting them, the in-combat refusal.
        Check.False(ServerPolicy.NoteChat("Mage says, \"[REALM] id=0 inst=1 eph=1 name=Base World\"", ThreadCell), "a player saying it");
        Check.False(ServerPolicy.NoteChat("Mage says, \"The Thread Gem opens a way into Tiny Hive.\"", ThreadCell), "a player saying the gem line");
        Check.False(ServerPolicy.NoteChat("You cannot use the Thread Gem while in combat.", HubCell), "the in-combat refusal");
        Check.False(ServerPolicy.NoteChat("You cast Mind Blossom on yourself", ThreadCell), "other chat");
        Check.False(ServerPolicy.InThread(ThreadCell), "none of them started a run");

        // A logout forgets the run.
        ServerPolicy.NoteChat(RealmThread, ThreadCell);
        ServerPolicy.Forget();
        Check.False(ServerPolicy.InThread(ThreadCell), "forgotten at logout");
    }

    private static void RefusedInAThread()
    {
        var dw = new Rig("DreamWeave");
        dw.EnterThread();
        dw.Patrol();
        Check.True(Said("turned off in Threads on DreamWeave"), "in a Thread on DreamWeave: told it is off");
        Check.False(Said("Cannot get player position") || Said("Cell dat not loaded") || Said("requires you to be inside a dungeon"),
            "and never got as far as building one");
        Check.False(dw.Active, "no patrol running");
        Check.False(dw.S.EnableNavigation, "navigation not switched on");
        ServerPolicy.Forget();
    }

    private static void AllowedElsewhere()
    {
        // DreamWeave, not in a Thread: the patrol itself runs (outdoors here, so it asks for a dungeon).
        var dw = new Rig("DreamWeave");
        dw.Patrol();
        Check.False(Said("turned off"), "DreamWeave outside a Thread: not refused");
        Check.True(Said("requires you to be inside a dungeon"), "it went on to the patrol itself");

        // DreamWeave, in a retail dungeon outside a Thread (the server said eph=0): the patrol goes on past the gate.
        var dun = new Rig("DreamWeave");
        dun.At(ThreadCell);
        dun.Chat(RealmWorld);
        dun.Patrol();
        Check.False(Said("turned off"), "the same dungeon outside a Thread: not refused");
        Check.True(Said("Cell dat not loaded"), "it went on to the patrol itself (no cell dat in this rig)");

        // DreamWeave, after leaving the Thread by portal (eph=0 line).
        var left = new Rig("DreamWeave");
        left.EnterThread();
        left.At(HubCell);
        left.Chat(RealmHub);
        left.Patrol();
        Check.False(Said("turned off"), "out of the Thread again: not refused");

        // Another server, even if the same lines came (they don't): untouched.
        var ael = new Rig("Aelrynth");
        ael.EnterThread();
        ael.Patrol();
        Check.False(Said("turned off"), "Aelrynth: not refused");
        Check.True(Said("Cell dat not loaded"), "it went on to the patrol itself (no cell dat in this rig)");

        var unknown = new Rig("");
        unknown.EnterThread();
        unknown.Patrol();
        Check.False(Said("turned off"), "an unknown world name is not refused");
        ServerPolicy.Forget();
    }

    private static void StopsARunningPatrol()
    {
        // A patrol is running in a dungeon; the character uses a Thread Gem and lands in the Thread.
        var b = new Rig("DreamWeave");
        b.At(OtherDungeonCell);
        b.RunningPatrol();
        b.Tick();
        Check.False(Said("turned off"), "before the Thread: the patrol keeps going");
        b.EnterThread();
        b.Tick();
        Check.False(b.Active, "in the Thread: the patrol is stood down");
        Check.False(b.S.EnableNavigation, "navigation is off");
        Check.Eq(b.S.CurrentRoute.Points.Count, 0, "its route is gone");
        Check.True(Said("turned off in Threads on DreamWeave"), "and the character is told why");

        // The same on another server: untouched.
        var ok = new Rig("Aelrynth");
        ok.At(OtherDungeonCell);
        ok.RunningPatrol();
        ok.Chat(RealmThread);
        ok.Tick();
        Check.False(Said("turned off"), "another server: no message");
        Check.Eq(ok.S.CurrentRoute.Points.Count, 1, "its route is untouched");
        ServerPolicy.Forget();
    }

    private static void OnlyThePatrol()
    {
        // /ra dunnav <NS> <EW> (walk to a dungeon cell) is a different command; DreamWeave's owners did not ask for it
        // to be off, and it still runs in a Thread (outdoors here, so it gets as far as asking for a dungeon).
        var b = new Rig("DreamWeave");
        b.Chat(RealmThread);
        Check.True(ServerPolicy.Run != null, "the server said: in a Thread");
        var dunnav = typeof(RynthAiPlugin).GetMethod("HandleDungeonNavCommand", Any)!;
        dunnav.Invoke(b.P, new object[] { new[] { "/ra", "dunnav", "12.5", "-34.2" } });
        Check.True(Said("dunnav requires you to be inside a dungeon"), "dunnav ran");
        Check.False(Said("turned off"), "and was not refused");
        ServerPolicy.Forget();
    }
}

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// ub-IT's remaining /ub verbs (2026-10-18): metas written for ub-IT send them, so every one is
// handled: mapped where RynthAi, RynthNav, RynthSort or the server has the feature, left to
// ub-Rythai for its verbs, and accepted quietly where nothing here matches (videopatch, ...).
internal static class UbCompatTests
{
    private const uint Player = 0x50000F21;
    private const uint Robe = 0x80282101, WornRobe = 0x80282102;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("ub compat: ub-IT verbs with no RynthAi equivalent are accepted, said once to a meta and every time typed", NoEquivalentAccepted);
        r.Add("ub compat: ub-Rythai's verbs get no RynthAi reply, and a meta passes them on", UbRythaiVerbsLeftAlone);
        r.Add("ub compat: aliases and features map onto RynthAi, RynthNav, RynthSort and the server", AliasesAndFeatures);
        r.Add("ub compat: a meta's /ub line uses the /ub meaning, not /mt's", MetaUsesUbMeaning);
    }

    private sealed class World
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new();

        public World()
        {
            FakeHost.Reset();
            FakeHost.PluginCommandCalls = true;
            FakeHost.SalvageCalls = true;   // GetObjectWielderInfo: the worn robe reads as worn
            FakeHost.Names[Player] = "Kelpie";
            FakeHost.PlayerIdValue = Player;
            foreach (var (id, name) in new[] { (Robe, "Faran Robe"), (WornRobe, "Faran Robe") })
            {
                FakeHost.Names[id] = name;
                FakeHost.Containers[id] = Player;
                FakeHost.ItemTypes[id] = 0x2;   // clothing
            }
            FakeHost.Wielded[WornRobe] = 0x0400;
            var host = FakeHost.Create(playerId: Player);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            var cache = new WorldObjectCache(host);
            cache.SetPlayerId(Player);
            Set("_objectCache", cache);
            Set("_playerId", Player);
            Set("_initialized", true);
            Set("_loginComplete", true);
            foreach (uint id in new[] { Robe, WornRobe }) cache.OnCreateObject(id);
            cache.Tick();
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public bool Ub(string line) => (bool)typeof(RynthAiPlugin).GetMethod("HandleUbCommand", Any)!.Invoke(P, new object[] { line })!;
        public bool Meta(string line) => (bool)typeof(RynthAiPlugin).GetMethod("HandleMetaChatCommand", Any)!.Invoke(P, new object[] { line })!;
        public int ChatWith(string text) => FakeHost.Chat.Count(c => c.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static void NoEquivalentAccepted()
    {
        var w = new World();
        Check.True(w.Meta("/ub soak on"), "/ub soak is handled");
        Check.True(w.Meta("/ub soak off"), "again");
        Check.Eq(w.ChatWith("/ub soak"), 1, "a meta's: said once per session, not per meta pulse");
        // Typed, it answers every time (a typed command always answers, CommandReply.cs).
        Check.True(w.Ub("/ub soak on"), "typed /ub soak is handled");
        Check.True(w.Ub("/ub soak on"), "typed again");
        Check.Eq(w.ChatWith("/ub soak"), 3, "typed: said each time");
        // /ub videopatch is not here: RynthAi already forwards it to the engine's /rc videopatch.
        foreach (string line in new[] { "/ub chat max", "/ub playeroption AutoRepeatAttack on", "/ub statushud toggle", "/ub professor war 7", "/ub xp level" })
            Check.True(w.Ub(line), line + " is handled");
        Check.Eq(w.ChatWith("Unrecognized") + w.ChatWith("Unknown /ub command"), 0, "no 'Unknown /ub command'");
        Check.False(w.Ub("/ub notaubitcommand"), "a verb ub-IT doesn't have is still unknown");
    }

    private static void UbRythaiVerbsLeftAlone()
    {
        var w = new World();
        int chat = FakeHost.Chat.Count;
        foreach (string line in new[] { "/ub bank status", "/ub periodic add 5 /say hi", "/ub xpmeter", "/ub portal use Aerlinthe", "/ub portal list", "/ub leaftide status" })
            Check.True(w.Ub(line), line + ": not unknown");
        Check.Eq(FakeHost.Chat.Count, chat, "RynthAi says nothing; ub-Rythai answers these");
        // A meta hands them to the chat parser, which every plugin (ub-Rythai too) sees.
        Check.False(w.Meta("/ub bank status"), "a meta's /ub bank goes on to the chat parser");
        // "/ub portal <name>" is RynthAi's (the nearest portal), not a portal gem.
        Check.False(RynthAiPluginIsUbRythai("portal", "Town Network"), "/ub portal <name> stays RynthAi's");
    }

    private static bool RynthAiPluginIsUbRythai(string cmd, string args)
        => (bool)typeof(RynthAiPlugin).GetMethod("IsUbRythaiCommand", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { cmd, args })!;

    private static void AliasesAndFeatures()
    {
        var w = new World();

        // wear: the carried copy, never the worn one (using a worn item takes it off).
        Check.True(w.Ub("/ub wear Faran Robe"), "/ub wear");
        Check.True(FakeHost.Uses.Contains(Robe) && !FakeHost.Uses.Contains(WornRobe), "wears the carried robe only");

        // Frame limits are RynthAi's FPS settings.
        w.Ub("/ub globalframelimit 45");
        Check.Eq(w.S.TargetFPSFocused, 45, "globalframelimit");
        Check.True(w.S.EnableFPSLimit, "and the limit is on");
        w.Ub("/ub bgframelimit 20");
        Check.Eq(w.S.TargetFPSBackground, 20, "bgframelimit");
        w.Ub("/ub globalframelimit 0");
        Check.False(w.S.EnableFPSLimit, "0 turns the limit off");

        // RynthNav and RynthSort through the plugin broker.
        w.Ub("/ub arrow point 42.1N, 33.6E");
        Check.True(FakeHost.PluginCommands.Contains(("RynthNav", "rnav", "arrow 42.1N, 33.6E")), "arrow point -> /rnav arrow");
        w.Ub("/ub navgoto -12.5 40.25 walk");
        Check.True(FakeHost.PluginCommands.Contains(("RynthNav", "rnav", "go 12.5S, 40.25E")), "navgoto signed coords -> /rnav go");
        w.Ub("/ub packsort");
        Check.True(FakeHost.PluginCommands.Contains(("RynthSort", "rsort", "now")), "packsort -> /rsort now");

        // Server commands go to the chat parser.
        w.Ub("/ub qblist");
        w.Ub("/ub augs");
        w.Ub("/ub window save");
        w.Ub("/ub fellow dismiss Storm");
        // qblist / augs are ACECustom's commands: the server plugin's (RynthILT) since 2026-10-20.
        Check.False(FakeHost.ChatCommands.Contains("/qb list") || FakeHost.ChatCommands.Contains("/aug"),
                    "qblist / augs: no server plugin, so nothing is sent to the server");
        // The engine has no /rc window save (it never had): RynthAi says where the setting is instead.
        Check.False(FakeHost.ChatCommands.Contains("/rc window save"), "window save sends no engine command");
        Check.Eq(w.ChatWith("Remember Game Window Position"), 1, "window save says where RynthCore keeps the window");
        Check.True(FakeHost.ChatCommands.Contains("/fellowship dismiss Storm"), "fellow dismiss -> /fellowship dismiss");

        // With a server plugin they are its verbs ("ub qblist", "ub augs").
        FakeServerHub.Reset();
        FakeServerHub.TakesCommand = line => line.StartsWith("ub ", StringComparison.Ordinal);
        var hub = new RynthCore.Plugin.RynthAi.MiniRemote.ServerHubClient(default);
        hub.UseTableForTests(FakeServerHub.Table);
        w.Set("_serverHub", hub);
        w.Ub("/ub qblist");
        w.Ub("/ub augs");
        Check.True(FakeServerHub.Commands.Contains("ub qblist") && FakeServerHub.Commands.Contains("ub augs"), "qblist / augs -> the server plugin");

        // stfl / getxy are the /mt strafe / cursor commands (no game window here: they say so).
        Check.True(w.Ub("/ub stfl 500"), "stfl handled");
        Check.True(w.Ub("/ub getxy"), "getxy handled");
    }

    private static void MetaUsesUbMeaning()
    {
        var w = new World();
        // A /ub verb /mt doesn't know used to fall to the chat parser and come back round.
        Check.True(w.Meta("/ub soak on"), "a meta's /ub line runs the /ub dispatcher");
        Check.Eq(FakeHost.ChatCommands.Count(c => c.StartsWith("/ub", StringComparison.Ordinal)), 0, "not re-sent to the chat parser");
        Check.True(w.Meta("/mt get xy"), "/mt lines still go to /mt");
    }
}

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// "/ra" verbs RynthAi doesn't have go to the active server plugin (2026-10-20): "/ra t11" (moved to
// RynthILT with the T11 counters), "/ra guardian", "/ra bounty". Without a server plugin they are not
// RynthAi's at all: the line is left for the server, as any unknown "/ra", and nothing ILT answers.
internal static class ServerPluginForwardTests
{
    private const uint Player = 0x50000F21;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("server plugin verbs: /ra t11 and /ra guardian go to the server plugin; RynthAi's own verbs don't", Forwarded);
        r.Add("server plugin verbs: without a server plugin /ra t11 is no RynthAi command (left for the server)", NoServerPlugin);
    }

    private static RynthAiPlugin NewPlugin(bool serverPlugin)
    {
        FakeHost.Reset();
        FakeServerHub.Reset();
        FakeHost.Names[Player] = "Tester";
        var host = FakeHost.Create(Player);
        var p = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, new LegacyUiSettings());
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(p, dash);
        var client = new ServerHubClient(host);
        if (serverPlugin) client.UseTableForTests(FakeServerHub.Table);
        typeof(RynthAiPlugin).GetField("_serverHub", Any)!.SetValue(p, client);
        return p;
    }

    /// <summary>Runs "/ra ..." through RynthAi's dispatcher; returns its eat flag (1 = RynthAi or a plugin took it).</summary>
    private static int Ra(RynthAiPlugin p, string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        object[] args = { parts[1].ToLowerInvariant(), parts, 1 };
        typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(p, args);
        return (int)args[2];
    }

    private static void Forwarded()
    {
        var p = NewPlugin(serverPlugin: true);
        FakeServerHub.TakesCommand = line => line.StartsWith("t11", StringComparison.Ordinal) || line.StartsWith("guardian", StringComparison.Ordinal);
        Check.Eq(Ra(p, "/ra t11 augs 2500"), 1, "/ra t11 is taken");
        Check.Eq(FakeServerHub.Commands.Count, 1, "by the server plugin");
        Check.Eq(FakeServerHub.Commands[0], "t11 augs 2500", "the words after /ra, as typed");
        Check.Eq(Ra(p, "/ra guardian handin"), 1, "/ra guardian is taken");
        Check.Eq(FakeServerHub.Commands[1], "guardian handin", "by the server plugin too");
        Check.Eq(Ra(p, "/ra nosuchverb"), 0, "a verb nobody has is left for the server");
        Check.Eq(FakeServerHub.Commands.Count, 2, "and the plugin took nothing more");
        Ra(p, "/ra version");
        Check.Eq(FakeServerHub.Commands.Count, 2, "RynthAi's own verbs never reach the server plugin");
    }

    private static void NoServerPlugin()
    {
        var p = NewPlugin(serverPlugin: false);
        FakeHost.Chat.Clear();
        Check.Eq(Ra(p, "/ra t11"), 0, "/ra t11 isn't RynthAi's: left for the server (unknown command there)");
        Check.False(FakeHost.Chat.Exists(l => l.Contains("T11")), "RynthAi says nothing about T11");
        Check.Eq(Ra(p, "/ra guardian"), 0, "/ra guardian likewise");
    }
}

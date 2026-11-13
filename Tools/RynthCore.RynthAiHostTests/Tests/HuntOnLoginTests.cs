using System;
using System.Reflection;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Hunt On Login (2026-11-11): the Outdoor Hunt starts once the character is in the world, as Patrol On
// Login starts the Dungeon Patrol. One or the other; the same checks as the Hunt button.
internal static class HuntOnLoginTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("hunt on login: Patrol On Login and Hunt On Login are one choice; both on: the one that fits the place", Choice);
        r.Add("hunt on login: the start checks run in order, each only once the ones before passed; chat says why", StartChecks);
        r.Add("hunt on login: no position or no RynthNav at login: it doesn't start, says why (Hunt On Login), the macro stays off", LoginRefused);
        r.Add("hunt on login: /ra hunt login answers, sets it, clears Patrol On Login, and says why not with the usage", Command);
        r.Add("hunt on login: HuntOnLogin round-trips (saved, copied, engine payload); the engine ticking Patrol On Login clears it", Setting);
    }

    private static void Choice()
    {
        Check.Eq(LoginAutoStart.Choose(false, false, false), LoginAutoStart.Start.None, "neither: nothing");
        Check.Eq(LoginAutoStart.Choose(true, false, false), LoginAutoStart.Start.Patrol, "patrol only: the patrol (it says why itself outdoors)");
        Check.Eq(LoginAutoStart.Choose(false, true, true), LoginAutoStart.Start.Hunt, "hunt only: the hunt (it says why itself in a dungeon)");
        Check.Eq(LoginAutoStart.Choose(true, true, true), LoginAutoStart.Start.Patrol, "both, in a dungeon: the patrol");
        Check.Eq(LoginAutoStart.Choose(true, true, false), LoginAutoStart.Start.Hunt, "both, outdoors: the hunt");

        Check.Eq(LoginAutoStart.Exclusive(false, false, false, true), (false, true), "hunt ticked");
        Check.Eq(LoginAutoStart.Exclusive(true, false, true, true), (false, true), "hunt ticked clears patrol");
        Check.Eq(LoginAutoStart.Exclusive(false, true, true, true), (true, false), "patrol ticked clears hunt");
        Check.Eq(LoginAutoStart.Exclusive(true, false, false, false), (false, false), "patrol unticked: hunt stays off");
        Check.Eq(LoginAutoStart.Exclusive(true, true, true, false), (true, false), "hunt unticked: patrol stays as it was");
        Check.Eq(LoginAutoStart.Exclusive(false, false, true, true), (true, false), "both ticked in one change: the patrol");
        Check.Eq(LoginAutoStart.Exclusive(true, true, true, true), (true, true), "nothing changed: left as it is");
    }

    private static void StartChecks()
    {
        int asked = 0;
        Func<bool> Yes() => () => { asked++; return true; };
        Func<bool> No() => () => { asked++; return false; };
        Check.Eq(OutdoorHunt.WhyNoStart(false, true, Yes(), Yes(), Yes()), OutdoorHunt.StartBlock.NoSettings, "no settings first");
        Check.Eq(OutdoorHunt.WhyNoStart(true, false, Yes(), Yes(), Yes()), OutdoorHunt.StartBlock.NoPosition, "then a position");
        Check.Eq(asked, 0, "nothing else asked before those pass");
        Check.Eq(OutdoorHunt.WhyNoStart(true, true, Yes(), Yes(), Yes()), OutdoorHunt.StartBlock.InDungeon, "then the dungeon");
        Check.Eq(asked, 1, "only the dungeon asked");
        asked = 0;
        Check.Eq(OutdoorHunt.WhyNoStart(true, true, No(), Yes(), Yes()), OutdoorHunt.StartBlock.UnknownBuilding, "then the building");
        Check.Eq(asked, 2, "RynthNav not asked");
        Check.Eq(OutdoorHunt.WhyNoStart(true, true, No(), No(), Yes()), OutdoorHunt.StartBlock.NoRynthNav, "then RynthNav");
        Check.Eq(OutdoorHunt.WhyNoStart(true, true, No(), No(), No()), OutdoorHunt.StartBlock.None, "all passed: it can start");
        foreach (OutdoorHunt.StartBlock b in Enum.GetValues(typeof(OutdoorHunt.StartBlock)))
            Check.True(b == OutdoorHunt.StartBlock.None ? OutdoorHunt.StartBlockText(b).Length == 0 : OutdoorHunt.StartBlockText(b).Length > 10,
                $"{b}: a reason in words");
        Check.True(OutdoorHunt.StartBlockText(OutdoorHunt.StartBlock.NoRynthNav).Contains("RynthNav"), "no RynthNav says so");
    }

    private static (RynthAiPlugin P, LegacyDashboardRenderer Dash, Action<string> Ra, Func<string> All) Rig()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var p = new RynthAiPlugin();
        typeof(RynthCore.PluginCore.RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
        var dash = new LegacyDashboardRenderer(host);
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(p, dash);
        var chat = FakeHost.Chat;
        void Ra(string line)
        {
            chat.Clear();
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!.Invoke(p, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }
        return (p, dash, Ra, () => string.Join(" | ", chat));
    }

    private static void LoginRefused()
    {
        var (p, dash, _, All) = Rig();
        var start = typeof(RynthAiPlugin).GetMethod("StartHunt", Any)!;
        FakeHost.HasPose = false;
        FakeHost.Chat.Clear();
        start.Invoke(p, new object[] { true });
        Check.False(p.HuntActive, "no position: not started");
        Check.True(All().Contains(RynthAiPlugin.HuntOnLoginRefusedPrefix.Trim()) && All().Contains("no position yet"), "says Hunt On Login and why: " + All());

        // Outdoors, with a position, but no RynthNav on this host: the Hunt button's own check refuses it.
        FakeHost.HasPose = true;
        FakeHost.PlayerPose = (0xA9B40021u, 100f, 100f, 50f);
        FakeHost.Chat.Clear();
        start.Invoke(p, new object[] { true });
        Check.False(p.HuntActive, "no RynthNav: not started");
        Check.False(dash.Settings.IsMacroRunning, "and the macro isn't turned on");
        Check.True(All().Contains("Hunt On Login") && All().Contains("needs RynthNav"), "says why: " + All());

        // The Hunt button's own wording is unchanged.
        FakeHost.Chat.Clear();
        start.Invoke(p, new object[] { false });
        Check.True(All().StartsWith("[RynthAi] Outdoor hunt needs RynthNav") && !All().Contains("Hunt On Login"), "the button says it as before: " + All());
    }

    private static void Command()
    {
        var (_, dash, Ra, All) = Rig();
        var s = dash.Settings;
        Ra("/ra hunt login");
        Check.True(All().Contains("Hunt On Login: off") && All().Contains(RynthAiPlugin.HuntLoginUsage), "no word: off, and the usage: " + All());
        s.PatrolOnLogin = true;
        Ra("/ra hunt login on");
        Check.True(s.HuntOnLogin && !s.PatrolOnLogin, "on, and Patrol On Login off");
        Check.True(All().Contains("Hunt On Login set: on") && All().Contains("Patrol On Login is off now"), "and says both: " + All());
        Ra("/ra hunt login on");
        Check.False(All().Contains("Patrol On Login is off now"), "patrol was already off: not said again: " + All());
        Ra("/ra hunt login maybe");
        Check.True(s.HuntOnLogin, "an unknown word changes nothing");
        Check.True(All().Contains("isn't on or off") && All().Contains("on now") && All().Contains("Usage: " + RynthAiPlugin.HuntLoginUsage),
            "why, what it is, the usage: " + All());
        Ra("/ra hunt login off");
        Check.True(!s.HuntOnLogin && !s.PatrolOnLogin, "off; patrol stays off");
        Check.True(All().Contains("Hunt On Login set: off"), "and says so: " + All());
        Ra("/ra hunt nonsense");
        Check.True(All().Contains(RynthAiPlugin.HuntLoginUsage), "the hunt's usage lists it: " + All());
    }

    private static void Setting()
    {
        Check.False(new LegacyUiSettings().HuntOnLogin, "off by default");
        var ctx = RynthAiJsonContext.Default.LegacyUiSettings;
        var saved = JsonSerializer.Deserialize(JsonSerializer.Serialize(new LegacyUiSettings { HuntOnLogin = true }, ctx), ctx)!;
        Check.True(saved.HuntOnLogin, "saved");
        var live = new LegacyUiSettings();
        typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(null, new object[] { saved, live });
        Check.True(live.HuntOnLogin, "copied");
        var old = JsonSerializer.Deserialize("{\"PatrolOnLogin\":true}", ctx)!;
        Check.True(old.PatrolOnLogin && !old.HuntOnLogin, "a profile from before the setting: off, its patrol kept");

        var s = new LegacyUiSettings { HuntOnLogin = true };
        var dash = (LegacyDashboardRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", F)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", F)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", F)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));
        string json = dash.BuildSettingsJson();
        using (var doc = JsonDocument.Parse(json))
            Check.True(doc.RootElement.TryGetProperty("HuntOnLogin", out var he) && he.GetBoolean(), "sent to the engine as \"HuntOnLogin\"");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.HuntOnLogin && !s.PatrolOnLogin, "an engine without it leaves it alone");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":true}");
        Check.True(s.PatrolOnLogin && !s.HuntOnLogin, "the engine ticking Patrol On Login clears Hunt On Login");
        dash.ApplySettingsJson("{\"HuntOnLogin\":true}");
        Check.True(s.HuntOnLogin && !s.PatrolOnLogin, "HuntOnLogin sent: on, and Patrol On Login off");
        dash.ApplySettingsJson("{\"HuntOnLogin\":false}");
        Check.True(!s.HuntOnLogin && !s.PatrolOnLogin, "and off");
    }
}

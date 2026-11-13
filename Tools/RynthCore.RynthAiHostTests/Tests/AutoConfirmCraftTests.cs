using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Engine.Compatibility;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Auto-confirm crafting (a players' request, 2026-11-11: "auto confirm on 100% tinker and crafting
// chance"). RynthAi keeps the engine's sticky CraftInteraction answer armed (plugin API v97): Yes at
// the threshold, a lower chance shows AC's dialog, never a No. The fake engine here answers prompts
// with the engine's own rules (ConfirmationArmPolicy.cs, linked), two slots: the one-shot that the
// tinker runner arms per step goes first and never disturbs the sticky arm.
internal static unsafe class AutoConfirmCraftTests
{
    private const uint Craft = RynthCoreHost.ConfirmationKind.CraftInteraction;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const uint Player = 0x50000F61;

    public static void Register(Runner r)
    {
        r.Add("autoconfirm: on keeps a sticky Yes-at-100 arm, re-armed before it runs out; off drops it", ArmsAndReArms);
        r.Add("autoconfirm: 100% is answered with one chat line; below the threshold AC's dialog shows, no line, never a No", AnswersAndLines);
        r.Add("autoconfirm: the tinker runner's one-shot goes first, its disarm and re-arms never fight the sticky arm", NeverFightsTheRunner);
        r.Add("autoconfirm: an engine before v97 - nothing armed, the reason in the status and the command", OldEngine);
        r.Add("autoconfirm: the threshold is clamped to 50-100 and a change re-arms at once", Threshold);
        r.Add("autoconfirm: logout releases the arm", Logout);
        r.Add("autoconfirm: settings - off at 100 by default, saved, copied, sent to the engine face as nullable fields", Settings);
        r.Add("autoconfirm: /ra autoconfirm on|off|<percent>|status always answers", Command);
    }

    // ── The fake engine's confirmation calls (static: function pointers can't capture) ──────────

    private static ConfirmationArm _oneShot, _sticky;
    private static long _now;
    private static int _seq, _lastPercent = -1, _lastOutcome;
    private static uint _lastType;
    private static readonly List<(uint Type, int Min, int Timeout)> ArmCalls = new();

    private static void ResetEngine()
    {
        _oneShot = default;
        _sticky = default;
        _now = 1_000_000;
        _seq = 0;
        _lastPercent = -1;
        _lastOutcome = 0;
        _lastType = 0;
        ArmCalls.Clear();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FakeArm(uint type, int minPercent, int timeoutMs)
    {
        ArmCalls.Add((type, minPercent, timeoutMs));
        return ConfirmationArmPolicy.Apply(ref _oneShot, ref _sticky, type, minPercent, timeoutMs, _now, available: true);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FakeLast(uint* type, uint* contextId, int* percent, int* outcome, int* ageMs)
    {
        if (type != null) *type = _lastType;
        if (contextId != null) *contextId = 0x1234;
        if (percent != null) *percent = _lastPercent;
        if (outcome != null) *outcome = _lastOutcome;
        if (ageMs != null) *ageMs = 0;
        return _seq;
    }

    /// <summary>The server's prompt reaches the engine; returns the outcome code (as GetLastConfirmation reports it).</summary>
    private static int Prompt(int percent, uint type = Craft)
    {
        var v = ConfirmationArmPolicy.Decide(ref _oneShot, ref _sticky, type, percent, _now);
        _seq++;
        _lastType = type;
        _lastPercent = percent;
        _lastOutcome = v switch
        {
            ConfirmationArmPolicy.Verdict.Accept => RynthCoreHost.ConfirmationOutcome.Accepted,
            ConfirmationArmPolicy.Verdict.Decline => RynthCoreHost.ConfirmationOutcome.Declined,
            ConfirmationArmPolicy.Verdict.AcceptSticky => RynthCoreHost.ConfirmationOutcome.AcceptedSticky,
            _ => RynthCoreHost.ConfirmationOutcome.Shown,
        };
        return _lastOutcome;
    }

    /// <summary>A host whose confirmation calls are the fake engine's, reporting <paramref name="version"/>.</summary>
    private static RynthCoreHost Host(uint version = RynthCoreHost.CurrentApiVersion, bool calls = true)
    {
        FakeHost.Reset();
        ResetEngine();
        FakeHost.ConfigureApi = (ref RynthCoreApiNative api) =>
        {
            api.Version = version;
            if (!calls) return;
            api.ArmConfirmationFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int, int>)&FakeArm;
            api.GetLastConfirmationFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, int*, int*, int*, int>)&FakeLast;
        };
        try { return FakeHost.Create(); }
        finally { FakeHost.ConfigureApi = null; }
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { AutoConfirmCraft = true };
        public readonly List<string> Lines = new();
        public readonly CraftAutoConfirm A;
        public Rig(RynthCoreHost host)
        {
            A = new CraftAutoConfirm(() => host, () => S, Lines.Add);
        }
        public void Tick(long advanceMs = 0)
        {
            _now += advanceMs;
            A.Tick(_now);
        }
    }

    private static void ArmsAndReArms()
    {
        var rig = new Rig(Host());
        rig.Tick();
        Check.True(rig.A.Supported, "a v97 engine supports it");
        Check.True(rig.A.Armed && _sticky.Armed, "on: the sticky arm is up");
        Check.Eq(_sticky.MinPercent, 100, "at 100%");
        Check.Eq(_sticky.Type, Craft, "for craft prompts");
        Check.Eq(ArmCalls.Count, 1, "one arm call");
        Check.Eq(ArmCalls[0].Min, 100 | RynthCoreHost.ConfirmationArmFlags.LeaveBelow, "with the leave-below flag");
        Check.Eq(ArmCalls[0].Timeout, CraftAutoConfirm.ArmTimeoutMs, "for the arm's life");
        Check.True(CraftAutoConfirm.RearmAfterMs < CraftAutoConfirm.ArmTimeoutMs / 2, "re-armed well inside its life");
        Check.False(_oneShot.Armed, "the one-shot slot is not used");

        for (int i = 0; i < 10; i++) rig.Tick(100);
        Check.Eq(ArmCalls.Count, 1, "no call every tick");
        rig.Tick(CraftAutoConfirm.RearmAfterMs);
        Check.Eq(ArmCalls.Count, 2, "re-armed after RearmAfterMs");
        long until = _sticky.Until;
        Check.True(until - _now == CraftAutoConfirm.ArmTimeoutMs, "the re-arm pushes the timeout out");
        // Ticking for ten minutes: never lapses.
        bool lapsed = false;
        for (int i = 0; i < 600; i++) { rig.Tick(1_000); lapsed |= !_sticky.Armed || _now > _sticky.Until; }
        Check.False(lapsed, "never lapses while on");

        rig.S.AutoConfirmCraft = false;
        rig.Tick(100);
        Check.False(_sticky.Armed, "off: the engine's sticky arm is dropped");
        Check.False(rig.A.Armed, "and RynthAi knows");
        Check.Eq(ArmCalls.Last().Min, RynthCoreHost.ConfirmationArmFlags.LeaveBelow, "the drop is the sticky disarm (the flag)");
        Check.Eq(ArmCalls.Last().Timeout, 0, "with timeout 0");
        int calls = ArmCalls.Count;
        rig.Tick(CraftAutoConfirm.RearmAfterMs * 3);
        Check.Eq(ArmCalls.Count, calls, "off: no more calls");
        Check.Eq(rig.A.Status, "off", "status off");

        rig.S.AutoConfirmCraft = true;
        rig.Tick(100);
        Check.True(_sticky.Armed, "on again: armed at once");
    }

    private static void AnswersAndLines()
    {
        var rig = new Rig(Host());
        rig.Tick();
        Check.Eq(Prompt(100), RynthCoreHost.ConfirmationOutcome.AcceptedSticky, "100%: Yes without the dialog");
        rig.Tick(16);
        Check.Eq(rig.Lines.Count, 1, "one chat line");
        Check.Eq(rig.Lines.FirstOrDefault() ?? "", "[RynthAi] Auto-confirmed: 100% chance", "the line");
        rig.Tick(16);
        Check.Eq(rig.Lines.Count, 1, "said once, not every tick");

        Check.Eq(Prompt(99), RynthCoreHost.ConfirmationOutcome.Shown, "99%: AC's dialog");
        rig.Tick(16);
        Check.Eq(rig.Lines.Count, 1, "no line for a prompt below the threshold");
        Check.True(_sticky.Armed && rig.A.Armed, "still armed");
        Check.Eq(Prompt(-1), RynthCoreHost.ConfirmationOutcome.Shown, "a prompt without a percent: AC's dialog");
        Check.Eq(Prompt(100, RynthCoreHost.ConfirmationKind.YesNo), RynthCoreHost.ConfirmationOutcome.Shown, "another kind of prompt: AC's dialog");
        rig.Tick(16);
        Check.Eq(rig.Lines.Count, 1, "no line for those");

        bool anyNo = false;
        for (int pct = 0; pct <= 100; pct++) anyNo |= Prompt(pct) == RynthCoreHost.ConfirmationOutcome.Declined;
        Check.False(anyNo, "never a No, at any chance");

        Prompt(100);
        rig.Tick(16);
        Prompt(100);
        rig.Tick(16);
        Check.Eq(rig.Lines.Count, 3, "a line per confirmed craft (repeated crafts)");
        Check.Eq(rig.A.AcceptedCount, 3, "counted");

        // Prompts from before RynthAi was looking are not reported.
        var late = new Rig(Host());
        Prompt(100);   // nothing armed yet: AC's dialog
        _lastOutcome = RynthCoreHost.ConfirmationOutcome.AcceptedSticky;   // even if it had been a sticky Yes
        late.Tick();
        Check.Eq(late.Lines.Count, 0, "the first look only takes the sequence number");
    }

    private static void NeverFightsTheRunner()
    {
        var host = Host();
        var rig = new Rig(host);
        rig.Tick();
        Check.True(_sticky.Armed, "sticky arm up");

        // A tinker step: TinkerRunner arms its one-shot (Yes at the tab's Min chance %, else No) for 15 s.
        rig.Tick(CraftAutoConfirm.RearmAfterMs - 1_000);
        Check.Eq(host.ArmConfirmation(Craft, 70, 15_000), 1, "the runner's one-shot arms");
        Check.True(_oneShot.Armed && _sticky.Armed, "both armed");
        // RynthAi re-arms in the middle of the step: the step's one-shot stays as the runner left it.
        int calls = ArmCalls.Count;
        rig.Tick(2_000);
        Check.Eq(ArmCalls.Count, calls + 1, "RynthAi re-armed during the step");
        Check.True(_oneShot.Armed && _oneShot.MinPercent == 70, "a sticky re-arm leaves the runner's one-shot");
        Check.Eq(Prompt(60), RynthCoreHost.ConfirmationOutcome.Declined, "the step's prompt follows the runner's rule (60% under 70: No)");
        rig.Tick(16);
        Check.Eq(rig.Lines.Count, 0, "the runner's answer is not reported as an auto-confirm");
        Check.True(_sticky.Armed, "the sticky arm is untouched");

        Check.Eq(host.ArmConfirmation(Craft, 70, 15_000), 1, "next step");
        Check.Eq(Prompt(100), RynthCoreHost.ConfirmationOutcome.Accepted, "the runner's Yes is its own (outcome Accepted)");
        rig.Tick(16);
        Check.Eq(rig.Lines.Count, 0, "still no auto-confirm line");

        // The runner stops mid-step and disarms its one-shot (ArmConfirmation(type, 0, 0)).
        Check.Eq(host.ArmConfirmation(Craft, 70, 15_000), 1, "a step");
        Check.Eq(host.ArmConfirmation(Craft, 0, 0), 1, "the runner's disarm");
        Check.False(_oneShot.Armed, "the one-shot is gone");
        Check.True(_sticky.Armed, "the sticky arm is still there after the run");
        rig.Tick(16);
        Check.True(rig.A.Armed, "RynthAi still has it");
        Check.Eq(Prompt(100), RynthCoreHost.ConfirmationOutcome.AcceptedSticky, "a hand-made 100% craft after the run: auto-confirmed");
        rig.Tick(16);
        Check.Eq(rig.Lines.Count, 1, "with its line");
        Check.Eq(Prompt(50), RynthCoreHost.ConfirmationOutcome.Shown, "a hand-made 50% craft: AC's dialog");

        // Turning it off never touches a runner's one-shot.
        Check.Eq(host.ArmConfirmation(Craft, 70, 15_000), 1, "a step");
        rig.S.AutoConfirmCraft = false;
        rig.Tick(16);
        Check.False(_sticky.Armed, "off: the sticky arm is gone");
        Check.True(_oneShot.Armed && _oneShot.MinPercent == 70, "the runner's one-shot is still there");
    }

    private static void OldEngine()
    {
        foreach (uint version in new uint[] { 85, 95, 96 })
        {
            var rig = new Rig(Host(version));
            rig.Tick();
            Check.False(rig.A.Supported, $"v{version}: not supported");
            Check.False(rig.A.Armed, $"v{version}: nothing armed");
            Check.Eq(ArmCalls.Count, 0, $"v{version}: the engine is not called (its disarm would clear another plugin's one-shot)");
            Check.True(rig.A.Status.Contains("plugin API 97"), $"v{version}: the status says why: {rig.A.Status}");
            rig.S.AutoConfirmCraft = false;
            rig.Tick(16);
            Check.Eq(ArmCalls.Count, 0, $"v{version}: turning it off calls nothing");
        }
        var none = new Rig(Host(RynthCoreHost.CurrentApiVersion, calls: false));
        none.Tick();
        Check.False(none.A.Supported, "no confirmation calls at all: not supported");
    }

    private static void Threshold()
    {
        Check.Eq(CraftAutoConfirm.ClampPercent(0), 50, "0 -> 50");
        Check.Eq(CraftAutoConfirm.ClampPercent(49), 50, "49 -> 50");
        Check.Eq(CraftAutoConfirm.ClampPercent(75), 75, "75");
        Check.Eq(CraftAutoConfirm.ClampPercent(150), 100, "150 -> 100");
        var rig = new Rig(Host());
        rig.S.AutoConfirmCraftPercent = 90;
        rig.Tick();
        Check.Eq(_sticky.MinPercent, 90, "armed at 90");
        Check.Eq(Prompt(92), RynthCoreHost.ConfirmationOutcome.AcceptedSticky, "92% at 90: Yes");
        rig.Tick(16);
        Check.Eq(rig.Lines.LastOrDefault() ?? "", "[RynthAi] Auto-confirmed: 92% chance", "the line says the chance");
        rig.S.AutoConfirmCraftPercent = 100;
        rig.Tick(16);
        Check.Eq(_sticky.MinPercent, 100, "a change re-arms at once");
        rig.S.AutoConfirmCraftPercent = 10;
        rig.Tick(16);
        Check.Eq(_sticky.MinPercent, 50, "a saved value under 50 arms at 50");
    }

    private static void Logout()
    {
        var rig = new Rig(Host());
        rig.Tick();
        Check.True(_sticky.Armed, "armed");
        rig.A.Release();
        Check.False(_sticky.Armed, "logout drops the engine's arm");
        Check.False(rig.A.Armed, "and RynthAi's");
    }

    private static void Settings()
    {
        var d = new LegacyUiSettings();
        Check.False(d.AutoConfirmCraft, "off by default");
        Check.Eq(d.AutoConfirmCraftPercent, 100, "100% by default");
        var old = JsonSerializer.Deserialize("{\"EnableAutostack\":true}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.False(old.AutoConfirmCraft, "a profile from before the setting: off");
        Check.Eq(old.AutoConfirmCraftPercent, 100, "and 100%");
        var saved = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { AutoConfirmCraft = true, AutoConfirmCraftPercent = 80 }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(saved.AutoConfirmCraft, "on saves and loads");
        Check.Eq(saved.AutoConfirmCraftPercent, 80, "the percent saves and loads");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { AutoConfirmCraft = true, AutoConfirmCraftPercent = 70 }, dst });
        Check.True(dst.AutoConfirmCraft, "a profile switch copies it");
        Check.Eq(dst.AutoConfirmCraftPercent, 70, "and the percent");

        var payload = JsonSerializer.Deserialize("{\"PatrolOnLogin\":false}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(payload != null && payload.AutoConfirmCraft == null && payload.AutoConfirmCraftPercent == null, "an older engine's payload has no value");
        var s = new LegacyUiSettings { AutoConfirmCraft = true, AutoConfirmCraftPercent = 90 };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", Any)!.SetValue(dash, new LegacyAdvancedSettingsUi(s));
        var sent = JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.AutoConfirmCraft == true && sent.AutoConfirmCraftPercent == 90, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.AutoConfirmCraft, "an engine save without the fields leaves them");
        Check.Eq(s.AutoConfirmCraftPercent, 90, "(the percent too)");
        dash.ApplySettingsJson("{\"AutoConfirmCraft\":false,\"AutoConfirmCraftPercent\":20}");
        Check.False(s.AutoConfirmCraft, "an engine save with it sets it");
        Check.Eq(s.AutoConfirmCraftPercent, 50, "a percent under 50 is clamped");
    }

    // ── /ra autoconfirm ──────────────────────────────────────────────────────

    private static (RynthAiPlugin P, LegacyUiSettings S) Plugin(uint version)
    {
        FakeHost.Reset();
        ResetEngine();
        FakeHost.Names[Player] = "Tester";
        FakeHost.ConfigureApi = (ref RynthCoreApiNative api) =>
        {
            api.Version = version;
            api.ArmConfirmationFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint, int, int, int>)&FakeArm;
            api.GetLastConfirmationFn = (IntPtr)(delegate* unmanaged[Cdecl]<uint*, uint*, int*, int*, int*, int>)&FakeLast;
        };
        try { FakeHost.Create(playerId: Player); }
        finally { FakeHost.ConfigureApi = null; }
        var p = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
        var s = new LegacyUiSettings();
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(p, dash);
        return (p, s);
    }

    private static string Say(RynthAiPlugin p, string line)
    {
        FakeHost.Chat.Clear();
        typeof(RynthAiPlugin).GetMethod("HandleAutoConfirmCommand", Any)!.Invoke(p, new object[] { line.Split(' ') });
        return string.Join(" | ", FakeHost.Chat);
    }

    private static void Command()
    {
        var (p, s) = Plugin(RynthCoreHost.CurrentApiVersion);
        string r = Say(p, "/ra autoconfirm");
        Check.True(r.Contains("Auto-confirm crafting: off"), "status (the default): off: " + r);
        r = Say(p, "/ra autoconfirm on");
        Check.True(s.AutoConfirmCraft, "on turns it on");
        Check.True(r.Contains("on, Yes at 100% or better"), "and says the value now: " + r);
        r = Say(p, "/ra autoconfirm status");
        Check.True(r.Contains("on, Yes at 100%"), "status: on: " + r);
        r = Say(p, "/ra autoconfirm 90%");
        Check.Eq(s.AutoConfirmCraftPercent, 90, "a percent sets it");
        Check.True(r.Contains("Yes at 90%"), "and says so: " + r);
        r = Say(p, "/ra autoconfirm 30");
        Check.Eq(s.AutoConfirmCraftPercent, 90, "30 is refused");
        Check.True(r.Contains("50-100") && r.Contains("Usage"), "with the range and the usage: " + r);
        r = Say(p, "/ra autoconfirm banana");
        Check.True(r.Contains("unknown option") && r.Contains("Usage"), "an unknown word: why and the usage: " + r);
        r = Say(p, "/ra autoconfirm off");
        Check.False(s.AutoConfirmCraft, "off turns it off");
        Check.True(r.Contains("off"), "and says so: " + r);

        var (oldP, oldS) = Plugin(96);
        r = Say(oldP, "/ra autoconfirm on");
        Check.False(oldS.AutoConfirmCraft, "an older engine: on is refused");
        Check.True(r.Contains("plugin API 97"), "with the reason: " + r);
        r = Say(oldP, "/ra autoconfirm 90");
        Check.True(r.Contains("plugin API 97") && oldS.AutoConfirmCraftPercent == 100, "a percent too, nothing changed: " + r);
        r = Say(oldP, "/ra autoconfirm");
        Check.True(r.Contains("plugin API 97"), "status says why: " + r);
    }
}

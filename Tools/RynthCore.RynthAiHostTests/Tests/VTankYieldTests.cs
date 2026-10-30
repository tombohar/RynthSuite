using System;
using System.Collections.Generic;
using System.Text.Json;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// One bot per client under the Decal bridge (2026-10-01): when VTank's macro starts, RynthAi's
// stops with one line; while VTank runs a start is refused; VTank stopping restarts nothing.
// The VTank signal is the engine's API v74 GetVTankState, faked here (FakeHost.VTankFlags):
// 1 = the engine watches (Decal bridge), 2 = VTank's macro runs. The plugin's per-tick step is
// VTankYield.Apply (RynthAiPlugin.TickVTankYield calls it with these same arguments) and its
// explicit starts go through VTankYield.RefuseStart (RynthAiPlugin.RefuseMacroStartForVTank).
internal static class VTankYieldTests
{
    private const int Watching = 1, Running = 2;

    public static void Register(Runner r)
    {
        r.Add("vtank yield: VTank starts while RynthAi runs -> stopped, one line", StopsOnVTankStart);
        r.Add("vtank yield: while VTank runs, starts are refused (button, /ra start, meta, phone, script)", RefusesWhileRunning);
        r.Add("vtank yield: a start that skipped the check is switched back off (throttled line)", TickCatchesUncheckedStart);
        r.Add("vtank yield: VTank stops -> nothing restarts, starting is allowed again", NoAutoRestart);
        r.Add("vtank yield: Yield to VTank off -> nothing stops or refuses", YieldOff);
        r.Add("vtank yield: yield turned back on while both run -> stopped", YieldBackOn);
        r.Add("vtank yield: VTank starts while RynthAi is off -> silent, then starts refused", VTankStartsWhileIdle);
        r.Add("vtank yield: VTank restarted later stops RynthAi again", SecondVTankStart);
        r.Add("vtank yield: without Decal (or an older engine) nothing runs", NoDecalNoEffect);
        r.Add("vtank yield: the setting defaults on and survives old profiles and payloads", SettingDefaults);
    }

    // A plugin-shaped harness: the host's signal, the settings, the macro flag's previous tick,
    // what was printed and how often the macro was stopped.
    private sealed class Bot
    {
        public readonly RynthCoreHost Host;
        public readonly VTankYield Yield = new();
        public readonly LegacyUiSettings Settings = new();
        public readonly List<string> Chat = new();
        public bool MacroWasRunning;
        public int Stops;
        public long Now = 1_000_000;

        public Bot(int flags)
        {
            FakeHost.Reset();
            FakeHost.VTankFlags = flags;
            Host = FakeHost.Create();
        }

        public void SetVTank(int flags) { FakeHost.VTankFlags = flags; FakeHost.VTankSeq++; }

        /// <summary>One plugin tick: poll, decide, then remember the macro flag (as OnTick's edge does).</summary>
        public VTankYield.Verdict Tick(long advanceMs = 100)
        {
            Now += advanceMs;
            Yield.Poll(Host);
            var v = Yield.Apply(Settings, MacroWasRunning, () => Stops++, Chat.Add, Now);
            MacroWasRunning = Settings.IsMacroRunning;
            return v;
        }

        /// <summary>An explicit start (button, /ra start, a meta, the phone, a script).</summary>
        public bool TryStart()
        {
            Yield.Poll(Host);
            if (Yield.RefuseStart(Settings, Chat.Add)) return false;
            Settings.IsMacroRunning = true;
            return true;
        }
    }

    private static void StopsOnVTankStart()
    {
        var b = new Bot(Watching);
        Check.True(b.TryStart(), "RynthAi starts while VTank is off");
        Check.Eq(b.Tick(), VTankYield.Verdict.None, "running alone: nothing");
        Check.True(b.Settings.IsMacroRunning, "still running");

        b.SetVTank(Watching | Running);   // "/vt start" seen by the engine
        Check.Eq(b.Tick(), VTankYield.Verdict.StopForVTankStart, "VTank started: stop");
        Check.False(b.Settings.IsMacroRunning, "the macro flag is off");
        Check.Eq(b.Stops, 1, "stopped like /ra stop (nav stop, pause cancelled)");
        Check.Eq(b.Chat.Count, 1, "one line");
        Check.Eq(b.Chat[0], "[RynthAi] VTank started - RynthAi macro stopped (one bot per client).", "the exact line");

        for (int i = 0; i < 20; i++)
            Check.Eq(b.Tick(), VTankYield.Verdict.None, $"later tick {i}: nothing more");
        Check.Eq(b.Chat.Count, 1, "still one line");
        Check.Eq(b.Stops, 1, "stopped once");
    }

    private static void RefusesWhileRunning()
    {
        var b = new Bot(Watching | Running);
        b.Tick();
        Check.False(b.TryStart(), "start refused");
        Check.False(b.Settings.IsMacroRunning, "the macro stays off");
        Check.Eq(b.Chat.Count, 1, "one refusal line");
        Check.True(b.Chat[0].StartsWith("[RynthAi] VTank is running - RynthAi macro not started (one bot per client).", StringComparison.Ordinal),
            "the refusal says why");
        Check.False(b.TryStart(), "refused again");
        Check.Eq(b.Chat.Count, 2, "an explicit start always answers");
        Check.Eq(b.Tick(), VTankYield.Verdict.None, "nothing to stop");
    }

    private static void TickCatchesUncheckedStart()
    {
        var b = new Bot(Watching | Running);
        b.Tick();
        // Start-on-login / an old UI set the flag directly.
        b.Settings.IsMacroRunning = true;
        Check.Eq(b.Tick(), VTankYield.Verdict.RefuseStart, "switched back off");
        Check.False(b.Settings.IsMacroRunning, "off before the macro acted");
        Check.False(b.MacroWasRunning, "the macro never counted as running");
        Check.Eq(b.Chat.Count, 1, "refusal printed");

        b.Settings.IsMacroRunning = true;
        Check.Eq(b.Tick(1000), VTankYield.Verdict.RefuseStart, "again");
        Check.False(b.Settings.IsMacroRunning, "off again");
        Check.Eq(b.Chat.Count, 1, "no second line within 10 s");

        b.Settings.IsMacroRunning = true;
        Check.Eq(b.Tick(VTankYield.RefuseRepeatMs), VTankYield.Verdict.RefuseStart, "again, later");
        Check.Eq(b.Chat.Count, 2, "a line again after 10 s");
    }

    private static void NoAutoRestart()
    {
        var b = new Bot(Watching);
        b.TryStart();
        b.Tick();
        b.SetVTank(Watching | Running);
        b.Tick();
        Check.False(b.Settings.IsMacroRunning, "stopped for VTank");

        b.SetVTank(Watching);             // "/vt stop"
        for (int i = 0; i < 10; i++)
            Check.Eq(b.Tick(), VTankYield.Verdict.None, $"tick {i} after VTank stopped: nothing");
        Check.False(b.Settings.IsMacroRunning, "not restarted");
        Check.Eq(b.Chat.Count, 1, "no line when VTank stops");

        Check.True(b.TryStart(), "a start is allowed again");
        Check.Eq(b.Tick(), VTankYield.Verdict.None, "and stays on");
        Check.True(b.Settings.IsMacroRunning, "running");
    }

    private static void YieldOff()
    {
        var b = new Bot(Watching);
        b.Settings.YieldToVTank = false;
        b.TryStart();
        b.Tick();
        b.SetVTank(Watching | Running);
        Check.Eq(b.Tick(), VTankYield.Verdict.None, "VTank started, yield off: nothing");
        Check.True(b.Settings.IsMacroRunning, "still running");
        b.Settings.IsMacroRunning = false;
        Check.True(b.TryStart(), "a start isn't refused");
        Check.Eq(b.Chat.Count, 0, "no lines");
    }

    private static void YieldBackOn()
    {
        var b = new Bot(Watching);
        b.Settings.YieldToVTank = false;
        b.TryStart();
        b.Tick();
        b.SetVTank(Watching | Running);
        b.Tick();
        Check.True(b.Settings.IsMacroRunning, "both running with yield off");

        b.Settings.YieldToVTank = true;   // /ra vtankyield on
        Check.Eq(b.Tick(), VTankYield.Verdict.StopWhileVTankRuns, "stopped");
        Check.False(b.Settings.IsMacroRunning, "off");
        Check.Eq(b.Chat.Count, 1, "one line");
        Check.Eq(b.Chat[0], "[RynthAi] VTank is running - RynthAi macro stopped (one bot per client).", "says it stopped");
    }

    private static void VTankStartsWhileIdle()
    {
        var b = new Bot(Watching);
        b.Tick();
        b.SetVTank(Watching | Running);
        Check.Eq(b.Tick(), VTankYield.Verdict.None, "RynthAi off: nothing to stop");
        Check.Eq(b.Chat.Count, 0, "no line");
        Check.False(b.TryStart(), "a start is refused");
        Check.Eq(b.Chat.Count, 1, "the refusal line");
    }

    private static void SecondVTankStart()
    {
        var b = new Bot(Watching);
        b.TryStart(); b.Tick();
        b.SetVTank(Watching | Running); b.Tick();
        b.SetVTank(Watching); b.Tick();
        Check.True(b.TryStart(), "restarted by hand after VTank stopped");
        b.Tick();
        b.SetVTank(Watching | Running);
        Check.Eq(b.Tick(), VTankYield.Verdict.StopForVTankStart, "VTank's second start stops it again");
        Check.Eq(b.Chat.Count, 2, "a line each time");
        Check.Eq(b.Stops, 2, "two stops");
    }

    private static void NoDecalNoEffect()
    {
        // Engine v74 without Decal: GetVTankState answers 0.
        var b = new Bot(0);
        Check.True(b.Host.HasGetVTankState, "the engine has the call");
        b.TryStart(); b.Tick();
        Check.False(b.Yield.Watching, "not watching");
        Check.Eq(b.Tick(), VTankYield.Verdict.None, "nothing");

        // A "running" bit without the watching bit never counts.
        b.SetVTank(Running);
        Check.Eq(b.Tick(), VTankYield.Verdict.None, "running bit alone: nothing");
        Check.True(b.Settings.IsMacroRunning, "still running");

        // An engine before v74: no call at all.
        var old = new Bot(-1);
        Check.False(old.Host.HasGetVTankState, "older engine: no call");
        Check.False(old.Host.TryGetVTankState(out _, out _, out _), "TryGetVTankState says no");
        old.TryStart(); old.Tick();
        Check.Eq(old.Tick(), VTankYield.Verdict.None, "nothing");
        Check.True(old.Settings.IsMacroRunning, "running");
        Check.Eq(old.Chat.Count + b.Chat.Count, 0, "no lines anywhere");

        // The SDK wrapper reads the flags and the sequence.
        FakeHost.VTankFlags = Watching | Running; FakeHost.VTankSeq = 7;
        Check.True(b.Host.TryGetVTankState(out bool w, out bool run, out int seq), "v74 call");
        Check.True(w && run, "watching and running");
        Check.Eq(seq, 7, "sequence passed through");
    }

    private static void SettingDefaults()
    {
        Check.True(new LegacyUiSettings().YieldToVTank, "a new profile: on");
        var old = JsonSerializer.Deserialize("{\"StartMacroOnLogin\":true}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(old != null && old.YieldToVTank, "a profile saved before the setting: on");
        var off = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(new LegacyUiSettings { YieldToVTank = false }, RynthAiJsonContext.Default.LegacyUiSettings),
            RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(off != null && !off.YieldToVTank, "off is saved and loaded");
        var payload = JsonSerializer.Deserialize("{\"PatrolOnLogin\":false}", RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(payload != null && payload.YieldToVTank, "an older engine's settings payload (no field): on");
    }
}

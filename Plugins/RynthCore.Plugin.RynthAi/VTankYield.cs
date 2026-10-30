using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// One bot per client under the Decal bridge: when VTank's macro starts, RynthAi's stops, and
/// it isn't started again while VTank runs. VTank stopping only allows it again; nothing is
/// restarted. The engine says whether VTank's macro runs (RynthCoreHost.TryGetVTankState, API
/// v74: VTank's documented /vt start and /vt stop through Decal's chat parser). Without Decal
/// the engine isn't watching and nothing here ever stops or refuses anything.
///
/// Pure decisions only (the plugin does the stopping and the chat), so the host tests drive it
/// with a fake VTank signal.
/// </summary>
internal sealed class VTankYield
{
    public const string StoppedForStartLine = "[RynthAi] VTank started - RynthAi macro stopped (one bot per client).";
    public const string StoppedWhileRunningLine = "[RynthAi] VTank is running - RynthAi macro stopped (one bot per client).";
    public const string RefusedLine = "[RynthAi] VTank is running - RynthAi macro not started (one bot per client). Stop VTank first (/vt stop), or turn off Yield to VTank (/ra vtankyield off).";

    /// <summary>Refusals the tick itself makes (a start that bypassed the checked paths) print at most this often.</summary>
    public const long RefuseRepeatMs = 10_000;

    public enum Verdict
    {
        /// <summary>Nothing to do.</summary>
        None,
        /// <summary>VTank just started and RynthAi's macro is on: stop it, print <see cref="StoppedForStartLine"/>.</summary>
        StopForVTankStart,
        /// <summary>VTank was already running and RynthAi's macro was running too (e.g. yield was just turned
        /// back on): stop it, print <see cref="StoppedWhileRunningLine"/>.</summary>
        StopWhileVTankRuns,
        /// <summary>Something switched RynthAi's macro on while VTank runs: switch it back off before it acts,
        /// print <see cref="RefusedLine"/> (throttled, see <see cref="ShouldPrintRefusal"/>).</summary>
        RefuseStart,
    }

    private bool _vtankWasRunning;
    private long _lastRefusalMs = long.MinValue / 2;

    /// <summary>The engine watches VTank (Decal bridge mode), as of the last <see cref="Observe"/>.</summary>
    public bool Watching { get; private set; }

    /// <summary>VTank's macro runs, as of the last <see cref="Observe"/> (false whenever not watching).</summary>
    public bool VTankRunning { get; private set; }

    /// <summary>Asks the engine (API v74). An older engine, or no Decal bridge: not watching.</summary>
    public void Poll(RynthCoreHost host)
    {
        if (host.TryGetVTankState(out bool watching, out bool running, out _))
            Observe(watching, running);
        else
            Observe(false, false);
    }

    /// <summary>
    /// The per-tick step the plugin runs before the macro acts: decides, switches the macro flag
    /// off when it must (<paramref name="onStop"/> does the rest of a /ra stop: nav stop, pause
    /// cancel), and prints the line. Returns what it decided.
    /// </summary>
    public Verdict Apply(LegacyUiSettings settings, bool macroWasRunning, Action? onStop, Action<string> chat, long nowMs)
    {
        Verdict v = Tick(settings.IsMacroRunning, macroWasRunning, settings.YieldToVTank);
        if (v == Verdict.None)
            return v;
        settings.IsMacroRunning = false;
        onStop?.Invoke();
        if (v == Verdict.StopForVTankStart) chat(StoppedForStartLine);
        else if (v == Verdict.StopWhileVTankRuns) chat(StoppedWhileRunningLine);
        else if (ShouldPrintRefusal(nowMs)) chat(RefusedLine);
        return v;
    }

    /// <summary>An explicit start: true = refused (the line is printed), the caller must not start.</summary>
    public bool RefuseStart(LegacyUiSettings settings, Action<string> chat)
    {
        if (!BlocksStart(settings.YieldToVTank))
            return false;
        chat(RefusedLine);
        return true;
    }

    /// <summary>Takes the engine's answer (call before deciding; cheap, any number of times).</summary>
    public void Observe(bool watching, bool vtankRunning)
    {
        Watching = watching;
        VTankRunning = watching && vtankRunning;
    }

    /// <summary>
    /// Once per plugin tick, BEFORE the macro acts. <paramref name="macroOn"/> = the macro flag now;
    /// <paramref name="macroWasRunning"/> = it was on at the end of the previous tick.
    /// </summary>
    public Verdict Tick(bool macroOn, bool macroWasRunning, bool yieldEnabled)
    {
        bool started = VTankRunning && !_vtankWasRunning;
        _vtankWasRunning = VTankRunning;
        if (!yieldEnabled || !VTankRunning || !macroOn)
            return Verdict.None;
        if (started)
            return Verdict.StopForVTankStart;
        return macroWasRunning ? Verdict.StopWhileVTankRuns : Verdict.RefuseStart;
    }

    /// <summary>An explicit start (button, /ra start, a meta, the phone, a script) must be refused.</summary>
    public bool BlocksStart(bool yieldEnabled) => yieldEnabled && VTankRunning;

    /// <summary>Throttle for <see cref="Verdict.RefuseStart"/> lines from the tick (explicit starts always print).</summary>
    public bool ShouldPrintRefusal(long nowMs)
    {
        if (nowMs - _lastRefusalMs < RefuseRepeatMs)
            return false;
        _lastRefusalMs = nowMs;
        return true;
    }

    public string Describe(bool yieldEnabled, bool engineHasIt)
    {
        if (!engineHasIt)
            return "[RynthAi] Yield to VTank: " + (yieldEnabled ? "on" : "off") + " - this engine can't tell whether VTank runs (needs API v74), so it does nothing.";
        if (!Watching)
            return "[RynthAi] Yield to VTank: " + (yieldEnabled ? "on" : "off") + " - no Decal bridge in this client, so it does nothing.";
        return "[RynthAi] Yield to VTank: " + (yieldEnabled ? "on" : "off") + ". VTank macro: " + (VTankRunning ? "RUNNING" : "not running") +
               " (seen through /vt start and /vt stop; VTank's own Run Macro button isn't seen - /rc vtank clear if it was stopped that way).";
    }
}

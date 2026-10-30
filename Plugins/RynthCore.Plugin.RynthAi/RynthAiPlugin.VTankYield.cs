using System;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// One bot per client under the Decal bridge (VTankYield.cs): VTank starting stops RynthAi's
// macro, and RynthAi's macro doesn't start while VTank runs. "Yield to VTank" (settings, on by
// default, /ra vtankyield) turns it off. Without Decal the engine isn't watching and none of
// this ever refuses or stops anything.
public sealed partial class RynthAiPlugin
{
    private readonly VTankYield _vtankYield = new();
    // Set by the panel's macro button (it can come from another thread): the refusal line is
    // printed on the next tick, from the pump thread.
    private volatile bool _vtankRefusalPending;

    /// <summary>
    /// For every explicit start (/ra start, a meta, the phone, a script, a patrol): true = VTank
    /// runs and yield is on, so the caller must not start; the refusal line is printed here.
    /// Pump thread.
    /// </summary>
    internal bool RefuseMacroStartForVTank()
    {
        var dash = _dashboard;
        if (dash == null)
            return false;
        _vtankYield.Poll(Host);
        if (!_vtankYield.RefuseStart(dash.Settings, ReplyFail))   // a refusal: said even to a meta (once a minute)
            return false;
        Host.Log("[RynthAi] VTank yield: macro start refused (VTank's macro runs).");
        return true;
    }

    /// <summary>The panel's macro button (RynthPluginToggleMacro). Uses the state the last tick read.</summary>
    internal void ToggleMacroFromPanel()
    {
        var dash = _dashboard;
        if (dash == null) return;
        if (!dash.Settings.IsMacroRunning && _vtankYield.BlocksStart(dash.Settings.YieldToVTank))
        {
            _vtankRefusalPending = true;
            return;
        }
        dash.TogglePanelMacro();
    }

    /// <summary>
    /// Once per tick, before anything the macro does. Stops the macro the way /ra stop does
    /// (the macro-stop edge in OnTick then cancels the attack, stops movement and clears busy).
    /// </summary>
    private void TickVTankYield(LegacyUiSettings settings)
    {
        _vtankYield.Poll(Host);
        if (_vtankRefusalPending)
        {
            _vtankRefusalPending = false;
            ChatLine(VTankYield.RefusedLine);
        }

        var verdict = _vtankYield.Apply(settings, _macroWasRunning, onStop: () =>
        {
            _macroResumeAt = 0;            // a /ra pause must not bring it back
            _navigationEngine?.Stop();     // as /ra stop: autorun off now
        }, ChatLine, Environment.TickCount64);

        if (verdict != VTankYield.Verdict.None)
            Host.Log($"[RynthAi] VTank yield: {verdict} - macro switched off.");
    }

    /// <summary>/ra vtankyield [on|off]</summary>
    private void HandleVTankYieldCommand(string[] parts)
    {
        var dash = _dashboard;
        if (dash == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        if (parts.Length >= 3)
        {
            string v = parts[2].ToLowerInvariant();
            if (v is "on" or "1" or "true") dash.Settings.YieldToVTank = true;
            else if (v is "off" or "0" or "false") dash.Settings.YieldToVTank = false;
            else { ReplyFail("[RynthAi] Usage: /ra vtankyield [on|off]"); return; }
            dash.SaveSettings();
        }
        _vtankYield.Poll(Host);
        string line = _vtankYield.Describe(dash.Settings.YieldToVTank, Host.HasGetVTankState);
        if (parts.Length >= 3) ReplyOk(line);   // set: what it is now
        else ChatLine(line);                    // the status asked for
    }
}

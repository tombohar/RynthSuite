// ============================================================================
//  RynthSuite - RynthAiPlugin.MacroStopWho.cs
//  The "Macro stopped" line says who stopped it, and the health at that moment.
//
//  The stop is seen at the next tick (the macro switch went false), so the line used to say only what
//  the stop did. Who did it (the panel's button, a typed /ra stop, a meta, a Lua script, the HP failsafe,
//  Stop Macro On Death...) is recorded where the switch is written (LegacyUiSettings.SetMacroRunning,
//  MacroSource) and said here. With health under a heal line nothing heals while the macro is off: said
//  too, and flagged when the stop came from a script, a meta, a route or a remote rather than the player.
// ============================================================================

using System;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private string MacroStopWho(LegacyUiSettings settings)
    {
        string line = "";
        int hp = 0, lineValue = 0;
        try { line = _buffManager?.HealLineNow(out hp, out lineValue) ?? ""; }
        catch { line = ""; }
        return DescribeMacroStop(settings.MacroStopSource, hp, line, lineValue);
    }

    /// <summary>The tail of the "Macro stopped" line (internal for the host tests).</summary>
    internal static string DescribeMacroStop(string source, int healthPct, string healLine, int lineValue)
    {
        string who = string.IsNullOrEmpty(source) ? "a plain write (no source named)" : source;
        string text = $"Stopped by {who}";
        if (healLine.Length == 0) return text + ".";
        text += $"; health {healthPct}% is under {healLine} {lineValue}%: no heal goes while the macro is off";
        if (!StopIsThePlayers(who)) text += ". FLAG: stopped by a script, meta, route or remote with a heal due";
        return text + ".";
    }

    /// <summary>The player's own hands: the panel, the Mini Remote, a typed command, Stop Macro On Death.</summary>
    internal static bool StopIsThePlayers(string who) =>
        who.StartsWith("the panel", StringComparison.Ordinal)
        || who.StartsWith("the Mini Remote", StringComparison.Ordinal)
        || who.Contains("(typed", StringComparison.Ordinal)
        || who.StartsWith("Stop Macro On Death", StringComparison.Ordinal)
        || who.StartsWith("the character session ending", StringComparison.Ordinal);
}

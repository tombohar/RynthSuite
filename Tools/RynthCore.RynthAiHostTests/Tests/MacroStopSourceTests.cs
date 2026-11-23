using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// "Macro stopped" with no cause (2026-11-22): the stop is seen a tick later, and nothing said who made it.
// Every write of the macro switch now records its source (LegacyUiSettings.SetMacroRunning / MacroSource),
// and the stop line says it, with the health when a heal is due (RynthAiPlugin.DescribeMacroStop).
internal static class MacroStopSourceTests
{
    public static void Register(Runner r)
    {
        r.Add("macro source: a named stop or start is recorded; a plain write says it named nobody", NamedAndPlain);
        r.Add("macro source: scopes name the caller around a command (a meta's /ra stop), without repeating", Scopes);
        r.Add("macro source: the stop line says who, the health under a heal line, and flags a script's stop with a heal due", StopLine);
    }

    private static void NamedAndPlain()
    {
        var s = new LegacyUiSettings();
        Check.Eq(s.MacroStopSource, "", "nothing yet");
        s.SetMacroRunning(true, "the panel's Run/Stop button");
        Check.Eq(s.MacroStartSource, "the panel's Run/Stop button", "the start's source");
        s.SetMacroRunning(false, "Stop Macro On Death");
        Check.Eq(s.MacroStopSource, "Stop Macro On Death", "the stop's source");
        Check.False(s.IsMacroRunning, "off");
        s.IsMacroRunning = true;
        s.IsMacroRunning = false;
        Check.Eq(s.MacroStopSource, "a plain write (no source named)", "a plain write says so");
        s.SetMacroRunning(false, "something else");
        Check.Eq(s.MacroStopSource, "a plain write (no source named)", "no change, no new source");
    }

    private static void Scopes()
    {
        var s = new LegacyUiSettings { IsMacroRunning = true };
        using (s.MacroSource("the meta (state 'Vendor')"))
            s.SetMacroRunning(false, "the command /ra stop");
        Check.Eq(s.MacroStopSource, "the command /ra stop (from the meta (state 'Vendor'))", "the command, from the meta");

        s.IsMacroRunning = true;
        using (s.MacroSource("the meta (state 'Vendor')"))
            s.SetMacroRunning(false, "the command /ra stop from the meta (state 'Vendor')");
        Check.Eq(s.MacroStopSource, "the command /ra stop from the meta (state 'Vendor')", "a scope the source already names is not repeated");

        s.IsMacroRunning = true;
        using (s.MacroSource("a Lua script's command '/ra stop'"))
            s.IsMacroRunning = false;
        Check.Eq(s.MacroStopSource, "a Lua script's command '/ra stop'", "a plain write inside a scope takes the scope");

        s.IsMacroRunning = true;
        s.IsMacroRunning = false;
        Check.Eq(s.MacroStopSource, "a plain write (no source named)", "the scope ended with its using");
    }

    private static void StopLine()
    {
        Check.Eq(RynthAiPlugin.DescribeMacroStop("the panel's Run/Stop button", 90, "", 0),
            "Stopped by the panel's Run/Stop button.", "no heal due: who only");
        string mine = RynthAiPlugin.DescribeMacroStop("the panel's Run/Stop button", 20, "Top Off HP", 80);
        Check.True(mine.Contains("health 20% is under Top Off HP 80%: no heal goes while the macro is off", StringComparison.Ordinal), mine);
        Check.False(mine.Contains("FLAG", StringComparison.Ordinal), "the player's own stop is not flagged");
        string script = RynthAiPlugin.DescribeMacroStop("a Lua script (ai.MacroRunning = false)", 20, "Heal At", 50);
        Check.True(script.Contains("FLAG: stopped by a script, meta, route or remote with a heal due", StringComparison.Ordinal), script);
        string typed = RynthAiPlugin.DescribeMacroStop("the command /ra stop (typed, or a hotkey or alias)", 20, "Heal At", 50);
        Check.False(typed.Contains("FLAG", StringComparison.Ordinal), "typed: the player's");
    }
}

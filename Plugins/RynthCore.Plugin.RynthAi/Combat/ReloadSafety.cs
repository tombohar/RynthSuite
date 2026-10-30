using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// What RynthAi tells the engine before a hot reload (the RynthPluginReloadBlocker export).
///
/// Drakkon died on 2026-10-05 13:43:20 after an engine hot reload at 13:42:55 took every plugin
/// down for about 8 s in the middle of a fight. The engine now asks each plugin before it reloads
/// and waits (up to 2 minutes) while one says a reload would hurt. RynthAi says so while the macro
/// runs and a monster is engaged, health is under a heal line, or a cast is awaiting its result.
///
/// Pure: the plugin computes the text on its own thread each tick; the export only hands the last
/// text to the engine.
/// </summary>
internal static class ReloadSafety
{
    /// <summary>Why a reload now would hurt, or "" when it is safe. The engine logs
    /// "engine reload waiting: RynthAi " followed by this text.</summary>
    public static string Reason(bool macroRunning, bool monsterEngaged, string healLine, int healthPct,
                                int lineValue, string castInFlight)
    {
        if (!macroRunning) return "";
        var parts = new List<string>(3);
        if (monsterEngaged) parts.Add("a monster engaged");
        if (healLine.Length > 0) parts.Add($"health {healthPct}% under {healLine} {lineValue}%");
        if (castInFlight.Length > 0) parts.Add($"'{castInFlight}' awaiting its result");
        if (parts.Count == 0) return "";
        bool fight = monsterEngaged || healLine.Length > 0;
        return (fight ? "in combat" : "casting") + " (" + string.Join(", ", parts) + ")";
    }
}

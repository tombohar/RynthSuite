// MovedTabs.cs - Mini Remote tabs that moved out of the Mini Remote (2026-10-13), and where to.
//
// RynthILT's Character tab left the Mini Remote: its character overview and session went to
// RynthOracle's Character tab ("/ro character"), its ILT session rates to RynthCore's Mini Remote
// "Session Stats" section (RynthILT's own "section:ilt-rates"), and the force-server override to
// RynthCore Options > Diagnostics ("/rc diagnostics"). Infinite Leaftide's own screens stay
// RynthILT's: Charms and Banking are still Mini Remote ILT tabs (RynthILT's own manifest places
// them on the rail since 2026-10-20), drawn by RynthILT.
// Old saved tab ids, links and "/ra hub open|tab <name>" land on the new home through CommandFor.
//
// Pure: the host tests check the mapping offline.
using System;

namespace RynthCore.Plugin.RynthAi.Huds;

internal static class MovedTabs
{
    /// <summary>RynthOracle's Character tab (the character overview and this session).</summary>
    public const string OracleCharacter = "/ro character";

    /// <summary>RynthCore Options > Diagnostics (the server picker; RynthCore 2026.10.5.54+).</summary>
    public const string Diagnostics = "/rc diagnostics";

    /// <summary>
    /// RynthAi Settings (Advanced Settings): RynthCore's "Settings" panel (SettingsFace.Title), opened
    /// by name through "/rc ui open &lt;Panel&gt;" (RynthCoreChatCommands, the generic panel verb).
    /// </summary>
    public const string Settings = "/rc ui open Settings";

    /// <summary>
    /// The chat command that opens where tab <paramref name="tab"/> lives now, or null when it is
    /// still a Mini Remote tab (or not a tab at all). Case and padding don't matter.
    /// </summary>
    public static string? CommandFor(string? tab) => (tab ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "character" or "char" or "rates" => OracleCharacter,
        _ => null,
    };

    /// <summary>Where <paramref name="command"/> opens, in words, for chat replies and tooltips.</summary>
    public static string HomeOf(string command) => command switch
    {
        OracleCharacter => "RynthOracle's Character tab",
        Diagnostics => "RynthCore Options > Diagnostics",
        Settings => "RynthAi Settings (Advanced Settings)",
        _ => command,
    };
}

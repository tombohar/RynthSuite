// ============================================================================
//  RynthVision - VisionCommands.cs
//  The /rv chat command's words and what it answers (2026-10-28: every
//  command answers in chat, Plugins/Shared/CommandReply.cs). "/rv light ..."
//  is Light/DungeonLight.cs's. Pure, so RynthVision.Tests checks every verb.
// ============================================================================

using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthVision;

internal enum VisionVerb { Status, Radar, Slopes, Water, WaterTypes, Debug, Light, Unknown }

internal static class VisionCommands
{
    public const string Usage = "/rv [radar | slopes | water] (show or hide) | watertypes | debug | light ... (/rv light help)";

    /// <summary>The words after "/rv" (lower case): the verb, and for light what follows it.</summary>
    public static VisionVerb Parse(string arg, out string lightArgs)
    {
        lightArgs = string.Empty;
        string a = (arg ?? string.Empty).Trim().ToLowerInvariant();
        if (a == "light" || a.StartsWith("light ", System.StringComparison.Ordinal))
        {
            lightArgs = a.Substring(5);
            return VisionVerb.Light;
        }
        return a switch
        {
            "" => VisionVerb.Status,
            "radar" => VisionVerb.Radar,
            "slopes" => VisionVerb.Slopes,
            "water" => VisionVerb.Water,
            "watertypes" => VisionVerb.WaterTypes,
            "debug" => VisionVerb.Debug,
            _ => VisionVerb.Unknown,
        };
    }

    /// <summary>The overlay a verb switches: its name in chat.</summary>
    public static string OverlayName(VisionVerb verb) => verb switch
    {
        VisionVerb.Radar => "Radar ring",
        VisionVerb.Slopes => "Unclimbable slopes",
        VisionVerb.Water => "Impassable water",
        _ => verb.ToString(),
    };

    /// <summary>"/rv radar": the overlay's new state.</summary>
    public static string Switched(VisionVerb verb, bool on) => $"{OverlayName(verb)} {CommandReply.OnOff(on)}.";

    /// <summary>"/rv" alone: the overlays' state and the usage.</summary>
    public static string State(bool radar, bool slopes, bool water) =>
        $"Radar ring {CommandReply.OnOff(radar)}, unclimbable slopes {CommandReply.OnOff(slopes)}, impassable water {CommandReply.OnOff(water)}. {Usage}";

    /// <summary>"/rv debug": the pose stream's new state.</summary>
    public static string Debug(bool on) => $"Debug {CommandReply.OnOff(on)} (the player's pose and the overlays' landblock lines go to the log).";

    /// <summary>A word /rv doesn't know.</summary>
    public static string Unknown(string arg) => $"Unknown '/rv {(arg ?? string.Empty).Trim()}'. Usage: {Usage}";
}

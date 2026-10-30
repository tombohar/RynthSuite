using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Asks the engine's flight recorder (RynthCore Net/FlightRecorder.cs, 2026-10-06) to write its
/// buffer: the last minutes of client/server messages and engine markers. Called where RynthAi
/// notices a stall (LOOT WEDGE, the use-refusal check, a held-cast nudge), so the dump holds
/// the message sequence that led into it. The engine writes at most one automatic dump per
/// 10 minutes, so calling this often is harmless. Uses the plugin-command broker (API v64)
/// with a name the engine reserves; an engine without the recorder finds no such plugin and
/// nothing happens. Never throws.
/// </summary>
internal static class FlightRecorderSignal
{
    public const string Target = "RynthCore.FlightRecorder";

    public static void Dump(RynthCoreHost host, string reason)
    {
        try
        {
            if (host.HasSendPluginCommand)
                host.SendPluginCommand(Target, "dump", "RynthAi " + reason);
        }
        catch { }
    }
}

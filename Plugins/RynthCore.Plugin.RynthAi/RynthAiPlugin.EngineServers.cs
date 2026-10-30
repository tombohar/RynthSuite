// RynthAiPlugin.EngineServers.cs - server chat commands from RynthAi's views in RynthCore's sections.
//
// The Fellowship view RynthCore's Fellowship section draws ("section:fellowship",
// Huds/FellowshipSectionUi.cs) has buttons that run a chat command (a server plugin's /fship ...).
// They are clicked on the render thread; the command is queued here and run on the plugin tick
// through the engine's chat parser.
//
// The Infinite Leaftide world list and the "Treat this world as ILT" override are RynthILT's
// (since 2026-10-13), and RynthCore adds RynthILT's verdict to its own detection, so nothing of
// RynthAi's is copied to RynthCore (the feat/dynamic-sections copy of RynthAi's old settings was
// dropped in the merge).
using System;
using System.Collections.Concurrent;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>At most this many commands wait for the tick (a stuck button can't flood chat).</summary>
    private const int MaxQueuedEngineCommands = 32;

    private readonly ConcurrentQueue<string> _engineCommands = new();

    /// <summary>Plugin tick: the queued chat commands, through the engine's chat parser.</summary>
    private void DrainEngineCommands()
    {
        while (_engineCommands.TryDequeue(out string? command))
        {
            if (Host.HasInvokeChatParser && Host.InvokeChatParser(command)) continue;
            Log($"[RynthAi] RynthCore didn't take: {command}");
        }
    }
}

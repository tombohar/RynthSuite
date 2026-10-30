// ServerCommandModel.cs - Server commands other plugins hand RynthAi for its section views.
//
// A server plugin sends the "servercommands" remote command (Plugins/Shared/ServerCommandShare.cs)
// while its server is active: RynthILT sends Infinite Leaftide's /fship commands for the
// "fellowship" section every 10 s, and an empty list when ILT isn't active. RynthAi keeps the
// latest set per section and drops one that hasn't been re-sent for StaleMs (the sender unloaded
// or the engine lost it), so the buttons follow the server without RynthAi knowing any of them.
using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Huds;

internal static class ServerCommandModel
{
    /// <summary>A set not re-sent within this long is dropped (senders re-send every 10 s).</summary>
    public const long StaleMs = 35_000;

    private sealed record Entry(ServerCommandSet Set, long ReceivedAt);

    // Replaced whole on each change: the pump thread writes, the render thread reads.
    private static Dictionary<string, Entry> _sets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pump thread: stores a "servercommands" push. A malformed payload is ignored, never thrown.</summary>
    public static void Apply(string payload) => Apply(payload, Environment.TickCount64);

    /// <summary>Stores a push received at <paramref name="now"/> (tests pass the clock).</summary>
    public static void Apply(string payload, long now)
    {
        ServerCommandSet? set = ServerCommandShare.Parse(payload);
        if (set == null) return;
        var next = new Dictionary<string, Entry>(Volatile.Read(ref _sets), StringComparer.OrdinalIgnoreCase);
        if (set.Commands.Count == 0) next.Remove(set.Section);
        else next[set.Section] = new Entry(set, now);
        Volatile.Write(ref _sets, next);
    }

    /// <summary>The commands for <paramref name="section"/>, or null when none are current. Any thread.</summary>
    public static ServerCommandSet? For(string section) => For(section, Environment.TickCount64);

    /// <summary>The commands for <paramref name="section"/> at <paramref name="now"/>, or null.</summary>
    public static ServerCommandSet? For(string section, long now)
    {
        if (!Volatile.Read(ref _sets).TryGetValue(section, out Entry? e)) return null;
        return now - e.ReceivedAt <= StaleMs ? e.Set : null;
    }

    /// <summary>Forgets every set (logout: the next character's server sends its own).</summary>
    public static void Clear() => Volatile.Write(ref _sets, new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
}

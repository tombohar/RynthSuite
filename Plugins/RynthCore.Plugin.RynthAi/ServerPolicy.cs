using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Features a server's owners have asked us to keep off in part of their server (and only there). The server is
/// told apart by its world name, as RynthThreads tells DreamWeave: a world-name substring, any case, so
/// "DreamWeave PvE" counts too. Every other server is untouched.
///
/// DreamWeave's owner asked (2026-10-15) for the dungeon patrol (/ra dunnav-patrol, the dashboard's Patrol
/// button, a meta's Embedded Nav Route "Patrol") to be off in Threads. A Thread is DreamWeave's private,
/// single-use copy of a retail dungeon, opened by using a Thread Gem (made at the Fragment Press in the Drift
/// Network hall; wiki.acdreamweave.com/index.php/Threads). 24 retail dungeons are in its rotation and the same
/// dungeons can still be walked outside a Thread, so the rule follows the Thread run, not a landblock list.
/// Everywhere else on DreamWeave the patrol works as before, and nothing else changes in a Thread: recorded nav
/// routes, going to a dungeon cell (dunnav) and the rest work as they always did. The patrol is built in one
/// place (BuildDunPatrol), which asks here first.
///
/// How a client knows it is in a Thread, from DreamWeave's own chat (our clients' logs, 2026-10-07..10-15):
///   "The Thread Gem opens a way into Watery Grotto. It will bring you back 5 more time(s) ..."
///       just before the teleport into the Thread: a run starts; its landblock is the first one the character
///       stands in after leaving the one the gem was used in.
///   "[REALM] id=0 inst=7841 eph=1 name=Base World"
///       after every login and every teleport. eph=1 is an ephemeral instance: it came after every Thread entry
///       (20 entries into 14 dungeons) and nowhere else, so it counts as being in a Thread; eph=0 is the shared
///       world (the hub, the Marketplace, a retail dungeon outside a Thread) and ends the run.
/// Leaving the run's landblock also ends it, so a run stays scoped to its dungeon even if the [REALM] line is
/// missing. The state is kept in a process-wide environment variable, so a plugin reloaded inside a Thread
/// still knows; a logout forgets it (the next login's [REALM] line says where the character is).
///
/// A world name the client can't give yet counts as "not listed": the check is repeated while a patrol
/// runs, so one that is running when the character enters a Thread is stood down at once.
/// </summary>
internal static class ServerPolicy
{
    /// <summary>World-name fragments of the servers whose owners asked for the dungeon patrol to be off in Threads.</summary>
    private static readonly string[] PatrolOffInThreadsServers = { "DreamWeave" };

    /// <summary>DreamWeave's line just before the teleport into a Thread (a server line starts with it).</summary>
    private const string ThreadGemOpensLine = "The Thread Gem opens a way into ";

    /// <summary>DreamWeave's line after each login and teleport: "[REALM] id=0 inst=7841 eph=1 name=Base World".</summary>
    private static readonly Regex RealmLine = new(@"^\s*\[REALM\][^\r\n]*?\beph=([01])\b", RegexOptions.CultureInvariant);

    private const string ThreadVar = "RYNTHAI_THREAD_RUN";

    public const string PatrolOffMessage = "[RynthAi] The dungeon patrol is turned off in Threads on DreamWeave (its owners asked for that).";

    /// <summary>A Thread run: the dungeon landblock it is in (0 = not known yet) and the landblock the gem was
    /// used in (0 = none: the run was learned from a [REALM] line).</summary>
    internal readonly record struct ThreadRun(uint Landblock, uint FromLandblock);

    /// <summary>True when <paramref name="world"/> (the server's world name) keeps the dungeon patrol off in Threads.</summary>
    internal static bool PatrolOffInThreadsOn(string? world) =>
        !string.IsNullOrWhiteSpace(world)
        && PatrolOffInThreadsServers.Any(s => world.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);

    // ── The run, kept across a plugin reload ─────────────────────────────────────────────────

    private static string Pid => Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The Thread run the character is in, or null.</summary>
    internal static ThreadRun? Run
    {
        get
        {
            try
            {
                string? v = Environment.GetEnvironmentVariable(ThreadVar);
                if (string.IsNullOrEmpty(v)) return null;
                string[] p = v.Split(':');
                if (p.Length != 3 || p[0] != Pid) return null;
                return new ThreadRun(uint.Parse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                                     uint.Parse(p[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            catch { return null; }
        }
        private set
        {
            try
            {
                Environment.SetEnvironmentVariable(ThreadVar,
                    value is { } r ? $"{Pid}:{r.Landblock:X4}:{r.FromLandblock:X4}" : null);
            }
            catch { }
        }
    }

    /// <summary>The dungeon landblock of <paramref name="cell"/>, or 0 outdoors / unknown.</summary>
    private static uint DungeonLandblock(uint cell) => cell != 0 && (cell & 0xFFFF) >= 0x0100 ? cell >> 16 : 0;

    /// <summary>
    /// Reads one chat message for DreamWeave's Thread lines; <paramref name="cell"/> is the character's cell now
    /// (0 = unknown). Returns true when it was one. Any server's chat may come here; only DreamWeave sends these
    /// lines, and <see cref="PatrolOff"/> asks the world name as well.
    /// </summary>
    internal static bool NoteChat(string? text, uint cell)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string t = text.TrimStart();
        if (t.StartsWith(ThreadGemOpensLine, StringComparison.Ordinal))
        {
            // The teleport follows: the run's landblock is the next one the character stands in.
            uint from = cell != 0 ? cell >> 16 : 0;
            Run = new ThreadRun(0, from == 0 ? 0xFFFFFFFFu : from);
            return true;
        }
        if (!t.StartsWith("[REALM]", StringComparison.Ordinal)) return false;
        var m = RealmLine.Match(t);
        if (!m.Success) return false;
        if (m.Groups[1].Value == "1")
        {
            // After the teleport's LoginComplete: the character stands in the Thread's landblock already.
            Run = new ThreadRun(DungeonLandblock(cell), 0);
        }
        else Run = null;
        return true;
    }

    /// <summary>
    /// Follows the character's cell: the run learns its landblock, and ends when the character leaves it.
    /// Returns true while the character is in a Thread.
    /// </summary>
    internal static bool InThread(uint cell)
    {
        if (Run is not { } r) return false;
        if (cell == 0) return true;                        // position not known: keep what the server said
        uint lb = cell >> 16;
        if (r.Landblock == 0)
        {
            uint dungeon = DungeonLandblock(cell);
            // Still where the gem was used, or not inside a dungeon yet: wait for the teleport.
            if (dungeon == 0 || lb == r.FromLandblock) return true;
            Run = r with { Landblock = dungeon };
            return true;
        }
        if (lb == r.Landblock) return true;
        Run = null;                                        // left the Thread's dungeon
        return false;
    }

    /// <summary>A logout: the next login's [REALM] line says where the character is.</summary>
    internal static void Forget() => Run = null;

    /// <summary>True when the dungeon patrol is off where this character is now (a Thread on DreamWeave).</summary>
    internal static bool PatrolOff(RynthCoreHost host)
    {
        try
        {
            if (Run == null) return false;
            if (!host.HasGetWorldName || !host.TryGetWorldName(out string world) || !PatrolOffInThreadsOn(world)) return false;
            uint cell = host.HasGetPlayerPose && host.TryGetPlayerPose(out uint c, out _, out _, out _, out _, out _, out _, out _) ? c : 0;
            return InThread(cell);
        }
        catch { return false; }
    }
}

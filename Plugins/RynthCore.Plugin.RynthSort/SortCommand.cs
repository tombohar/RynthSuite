using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort;

internal enum SortVerb { Window, On, Off, Now, Clean, Unpack, Status, Help, Unknown }

/// <summary>
/// A parsed "/rsort ..." and the lines it answers with (2026-10-28: every command answers,
/// Plugins/Shared/CommandReply.cs). <see cref="Quiet"/>: "on quiet" / "off quiet", the engine
/// dashboard's toggle, which shows the state itself (its success is logged only). Pure, so
/// RynthSort.Tests checks every word.
/// </summary>
internal readonly record struct SortCommand(SortVerb Verb, int? Pack = null, bool Quiet = false, string Word = "")
{
    public static SortCommand Parse(string? arg)
    {
        string[] words = (arg ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string word = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
        bool quiet = words.Length > 1 && words[1].Equals("quiet", StringComparison.OrdinalIgnoreCase);
        return word switch
        {
            "" => new(SortVerb.Window),
            "on" => new(SortVerb.On, Quiet: quiet),
            "off" => new(SortVerb.Off, Quiet: quiet),
            "now" or "tidy" => new(SortVerb.Now),
            "clean" => new(SortVerb.Clean),
            "unpack" => new(SortVerb.Unpack, words.Length > 1 && int.TryParse(words[1], out int p) && p > 0 ? p : null),
            "status" => new(SortVerb.Status),
            "help" or "?" => new(SortVerb.Help),
            _ => new(SortVerb.Unknown, Word: word),
        };
    }

    /// <summary>The window and help work before the character is known; everything else waits for it.</summary>
    public bool NeedsLogin => Verb is not (SortVerb.Window or SortVerb.Help);

    public const string Usage = "/rsort [on | off | now | tidy | clean | unpack [pack] | status]";

    public const string NoWindows =
        "This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). Sorting still runs: /rsort on|off|now|tidy|clean|unpack.";

    public static string Window(bool shown) => CommandReply.Shown("RynthSort window", shown);

    /// <summary>"/rsort now|tidy|clean": the job that starts (it runs as soon as the way is clear).</summary>
    public static string Started(SortVerb verb) => verb == SortVerb.Clean
        ? "Cleaning: every pack sorted, items no rule wants back to the main pack."
        : "Sorting every pack now.";

    public static string Unpacking(int? pack) =>
        (pack != null ? $"Emptying pack {pack}" : "Emptying your side packs") + " into the main pack; auto-sort is off (/rsort on when done).";

    public static string Unknown(string word) => $"Unknown '/rsort {word}'. Usage: {Usage}";
}

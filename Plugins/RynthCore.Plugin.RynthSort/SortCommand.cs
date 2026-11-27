using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthSort;

internal enum SortVerb { Window, On, Off, Now, Clean, Unpack, Back, Add, Status, Help, Unknown }

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
            "back" or "undo" => new(SortVerb.Back),
            "add" => new(SortVerb.Add, words.Length > 1 && int.TryParse(words[1], out int a) && a > 0 ? a : null),
            "status" => new(SortVerb.Status),
            "help" or "?" => new(SortVerb.Help),
            _ => new(SortVerb.Unknown, Word: word),
        };
    }

    /// <summary>The window and help work before the character is known; everything else waits for it.</summary>
    public bool NeedsLogin => Verb is not (SortVerb.Window or SortVerb.Help);

    public const string Usage = "/rsort [on | off | now | tidy | clean | unpack [pack] | back | add <pack> | status]";

    public const string AddUsage = "/rsort add <pack>: puts the item selected in the game into that pack's rules (pack numbers as the window shows them).";

    public const string NoWindows =
        "This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). Sorting still runs: /rsort on|off|now|tidy|clean|unpack|back.";

    public static string Window(bool shown) => CommandReply.Shown("RynthSort window", shown);

    /// <summary>"/rsort now|tidy|clean": the job that starts (it runs as soon as the way is clear).</summary>
    public static string Started(SortVerb verb) => verb == SortVerb.Clean
        ? "Cleaning: every pack sorted, items no rule wants back to the main pack."
        : "Sorting every pack now.";

    public static string Unpacking(int? pack) =>
        (pack != null ? $"Emptying pack {pack}" : "Emptying your side packs") + " into the main pack; auto-sort is off (/rsort on when done).";

    /// <summary>"/rsort back": Put back starts, or there is nothing to put back.</summary>
    public static string PuttingBack(int count) => count > 0
        ? $"Putting back the {count} item{(count == 1 ? "" : "s")} the last sort moved; auto-sort is off (/rsort on when done)."
        : "Nothing to put back: RynthSort hasn't moved anything to another pack lately.";

    public static string Unknown(string word) => $"Unknown '/rsort {word}'. Usage: {Usage}";
}

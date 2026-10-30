using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthAi.Meta;

/// <summary>What one chat line is, as far as /myquests goes.</summary>
internal enum MyQuestsLineKind
{
    /// <summary>Not a /myquests line.</summary>
    None,
    /// <summary>One quest: <see cref="MyQuestsEntry"/>.</summary>
    Quest,
    /// <summary>"Quest list is empty." - the whole (empty) reply.</summary>
    Empty,
    /// <summary>The server has /myquests turned off.</summary>
    Disabled,
    /// <summary>GDLe-style "This command may only be run once every ..." (ACE never sends it).</summary>
    RateLimited,
}

/// <summary>One /myquests line. Times are the server's unix seconds.</summary>
internal readonly record struct MyQuestsEntry(
    string Name, int Solves, long LastSolvedUnix, string Message, int MaxSolves, long MinDeltaSeconds)
{
    /// <summary>ACE: a quest with a solve limit (MaxSolves &gt; -1) that has reached it can never be solved again.</summary>
    public bool AtSolveLimit => MaxSolves > -1 && Solves >= MaxSolves;

    /// <summary>When the timer runs out (last solve + min delta); MaxValue at the solve limit.</summary>
    public DateTime ReadyAtUtc => AtSolveLimit
        ? DateTime.MaxValue
        : DateTime.UnixEpoch.AddSeconds((double)LastSolvedUnix + MinDeltaSeconds);

    /// <summary>ACE QuestManager.GetNextSolveTime == MinValue: not at the limit and the timer has run out.</summary>
    public bool IsReady(DateTime utcNow) => !AtSolveLimit && utcNow >= ReadyAtUtc;
}

/// <summary>
/// Parses ACE's /myquests reply (ACE.Server PlayerCommands.HandleQuests, "GDLe formatting
/// to match plugin expectations"). One Broadcast line per quest:
/// <code>{name lower} - {solves} solves ({last solved unix})"{quest message}" {max solves} {min delta}</code>
/// e.g. <c>stipendtimer_monthly - 2 solves (1759276800)"Monthly stipend" -1 2592000</c>.
/// The min delta is already scaled by the server's quest_mindelta_rate.
/// </summary>
internal static class MyQuestsParser
{
    // The name is lowercase and has no spaces in practice; allow anything up to " - N solves (".
    // The message is greedy up to the last '" ' so a quote inside it can't cut it short.
    private static readonly Regex QuestLine = new(
        "^(?<name>\\S.*?) - (?<solves>-?\\d+) solves \\((?<last>\\d+)\\)\"(?<msg>.*)\" (?<max>-?\\d+) (?<delta>\\d+)$",
        RegexOptions.CultureInvariant);

    public const string EmptyText = "Quest list is empty.";
    public const string DisabledText = "The command \"myquests\" is not currently enabled on this server.";

    public static MyQuestsLineKind Parse(string? text, out MyQuestsEntry entry)
    {
        entry = default;
        if (string.IsNullOrEmpty(text)) return MyQuestsLineKind.None;
        string line = text.Trim();
        if (line.Length == 0) return MyQuestsLineKind.None;

        if (line.Equals(EmptyText, StringComparison.Ordinal)) return MyQuestsLineKind.Empty;
        if (line.Equals(DisabledText, StringComparison.Ordinal)) return MyQuestsLineKind.Disabled;
        if (line.StartsWith("This command may only be run once every", StringComparison.Ordinal)) return MyQuestsLineKind.RateLimited;

        // Cheap pre-check before the regex: every quest line has " solves (".
        if (line.IndexOf(" solves (", StringComparison.Ordinal) < 0) return MyQuestsLineKind.None;
        Match m = QuestLine.Match(line);
        if (!m.Success) return MyQuestsLineKind.None;

        if (!int.TryParse(m.Groups["solves"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int solves)
            || !long.TryParse(m.Groups["last"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long last)
            || !int.TryParse(m.Groups["max"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int max)
            || !long.TryParse(m.Groups["delta"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long delta))
            return MyQuestsLineKind.None;

        entry = new MyQuestsEntry(m.Groups["name"].Value.Trim(), solves, last, m.Groups["msg"].Value, max, delta);
        return MyQuestsLineKind.Quest;
    }
}

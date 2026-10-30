// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// The lines a click on a quest prints in chat. Upstream's quest list "thinks" them to the player:
/// the quest's info and hint text ("{flag}: {details}", or "{flag}: No hint available"), its wiki
/// link ("{flag}: No url available" when there is none), and the flag's own history
/// ("{flag}: {description} CompletedOn:... Solves:... MaxSolves:... RepeatTime:...", or
/// "{flag}: Never completed"). The texts come from the quest list's Info and Hint columns.
/// </summary>
internal static class QuestText
{
    public static List<string> Lines(Quest q, QuestFlagStore flags, QuestBook book, DateTime utcNow)
    {
        string flag = q.Flag;
        var lines = new List<string>();

        string status = q.IsComplete(flags) ? q.Status(flags, utcNow) : "not done";
        string name = q.IsNew ? (q.Name.Length > 0 ? q.Name : "Not in the quest list") : q.DisplayName;
        lines.Add($"{name} ({flag}) - {status}");

        string details = q.Details();
        lines.Add(details.Length > 0 ? $"{flag}: {details}" : $"{flag}: No hint available");
        lines.Add(q.Url.Length > 0 ? $"{flag}: {q.Url}" : $"{flag}: No url available");
        lines.Add(History(flag, flags));

        if (book.TryGetReward(flag, out QuestReward r)) lines.Add("Rewards: " + r.LongSummary());
        return lines;
    }

    /// <summary>Upstream's QuestFlag.ToString(), or "Never completed" for a flag the character doesn't hold.</summary>
    public static string History(string flag, QuestFlagStore flags)
    {
        if (!flags.TryGet(flag, out QuestFlag f)) return $"{flag}: Never completed";
        string done = f.CompletedOnUtc == DateTime.MinValue ? "never" : f.CompletedOnUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return $"{f.Key}: {f.Description} CompletedOn:{done} Solves:{f.Solves} MaxSolves:{f.MaxSolves} RepeatTime:{TimeText.Friendly(f.RepeatTime)}";
    }
}

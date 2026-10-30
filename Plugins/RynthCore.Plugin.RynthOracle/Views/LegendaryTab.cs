using RynthCore.Plugin.RynthOracle.Data;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Legendary Key quests (John in Arwic counts 30 for his reward): rows of the Quests tab's merged
/// list since 2026-10-18 (QuestsTab.cs: LEG rows, John's counter in the trackers' status). The list
/// and notes follow the "Arwic John Route" sheet (legendary.csv). What is left here is the chat print.
/// </summary>
internal sealed partial class OracleView
{
    /// <summary>Chat: the quest, its flags and status, the sheet's notes, wiki and video links.</summary>
    private void PrintLegendary(LegendaryQuest q, string status)
    {
        PrintEntry(q.Label, string.Join(", ", q.Flags), status, q.Note, q.Url);
        if (q.Video.Length > 0) _c.Print("Video: " + q.Video);
    }
}

// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>Society: membership, rank, ribbons to the next rank and today, and the society quests.</summary>
internal sealed partial class OracleView
{
    private void DrawSociety(UiWindow w)
    {
        CharacterSnapshot c = _c.Character;
        var s = new SocietyStanding(
            c.Int(_c.Host, CharacterSnapshot.IntSocietyCelhan),
            c.Int(_c.Host, CharacterSnapshot.IntSocietyEldweb),
            c.Int(_c.Host, CharacterSnapshot.IntSocietyRadblo));
        QuestFlagStore flags = _c.Flags;
        DateTime now = _c.UtcNow;

        if (!s.IsMember)
        {
            w.Text("Not in a society.");
        }
        else
        {
            w.SeparatorText($"{s.SocietyName}: {s.RankName}");
            w.ProgressBar(s.RankMax > 0 ? (float)s.Progress / s.RankMax : 1f, -1f, 0f, $"{s.Progress} / {s.RankMax} ribbons");
            if (s.NextRankName.Length > 0) Field(w, "To " + s.NextRankName, $"{s.RibbonsToNextRank} ribbons", 0f, 130f);
            else Field(w, "Rank", "Master (the top rank)", 0f, 130f);
            Field(w, "Today", s.DailyLimit > 0
                ? $"{s.RibbonsToday(flags, now)} / {s.DailyLimit} ribbons"
                : "no daily limit at Master", 300f, 400f);
            if (flags.LastReadUtc == DateTime.MinValue)
                w.TextDisabled("Read your quest flags (Quests tab) for today's ribbons and quest timers.");
        }

        w.SeparatorText("Quests");
        foreach (SocietyQuest q in SocietyQuestList.Visible(s))
        {
            if (q.IsBlank) { w.Spacing(); continue; }
            if (q.IsHeading) { w.TextColored(UiColors.Gold, q.Name); continue; }
            DoneMark(w, q.IsComplete(s, flags, now));
            w.SameLine(50f);
            SocietyQuest row = q;
            string status = q.Status(s, flags, now);
            w.Selectable(Cut(q.Name, 36), "s.q." + q.Name, false, () => PrintEntry(row.Name, row.Flag, status, row.Hint, row.Url), 250f);
            w.SameLine(310f);
            w.TextDisabled(Cut(q.Area, 18));
            w.SameLine(450f);
            w.Text(status);
        }
    }
}

// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Augmentations: aug-gem quest timers, experience augmentations and luminance auras, with
/// luminance spent and still to go (upstream's Augmentations and Luminance tabs).
/// </summary>
internal sealed partial class OracleView
{
    private void DrawAugs(UiWindow w)
    {
        QuestFlagStore flags = _c.Flags;
        Func<uint, int> prop = _c.Props;
        DateTime now = _c.UtcNow;
        if (flags.LastReadUtc == DateTime.MinValue)
            w.TextDisabled("Quest flags not read yet (Quests tab, Refresh): gem timers and seer auras show as not done.");

        if (w.CollapsingHeader("Augmentation gem quests", "a.gems", defaultOpen: true))
        {
            foreach (AugQuest q in AugmentationList.Quests)
            {
                bool held = flags.TryGet(q.Flag, out QuestFlag f);
                bool ready = !held || f.Ready(now);
                DoneMark(w, !ready);
                w.SameLine(50f);
                AugQuest row = q;
                w.Selectable(Cut(q.Name, 44), "a.g." + q.Flag, false,
                    () => PrintEntry(row.Name, row.Flag, ready ? "ready" : "on cooldown", row.Hint, row.Url), 300f);
                w.SameLine(370f);
                w.Text(held ? f.NextAvailable(now) : "ready");
                if (held)
                {
                    w.SameLine(480f);
                    w.Text(f.Solves.ToString());
                }
            }
        }

        // Starts closed: it is the longest list, and every row drawn costs replay time each frame.
        if (w.CollapsingHeader("Experience augmentations", "a.xp", defaultOpen: false))
            DrawAugRows(w, xp: true, prop, flags);

        if (w.CollapsingHeader("Luminance auras", "a.lum", defaultOpen: true))
        {
            long spent = AugmentationList.TotalLuminanceSpent(prop, flags);
            long total = AugmentationList.TotalLuminance;
            w.ProgressBar((float)spent / total, -1f, 0f,
                $"{TimeText.Number(spent)} spent of {TimeText.Number(total)} ({(int)(spent * 100 / total)}%), {TimeText.Number(Math.Max(0, total - spent))} to go");
            DrawAugRows(w, xp: false, prop, flags);
        }
    }

    private void DrawAugRows(UiWindow w, bool xp, Func<uint, int> prop, QuestFlagStore flags)
    {
        w.TextDisabled("Done");
        w.SameLine(50f);
        w.TextDisabled("Have");
        w.SameLine(100f);
        w.TextDisabled("Augmentation");
        w.SameLine(300f);
        w.TextDisabled("Effect");
        w.SameLine(520f);
        w.TextDisabled("Cost");
        foreach (Augmentation a in AugmentationList.All)
        {
            if (a.IsXp != xp || a.Name == "Blank") continue;
            if (a.IsHeading)
            {
                w.SeparatorText(a.Name);
                continue;
            }
            if (a.IsUnknown) w.TextColored(UiColors.Grey, "?");
            else DoneMark(w, a.IsComplete(prop, flags));
            w.SameLine(50f);
            w.Text(a.IsUnknown ? "" : a.Text(prop, flags));
            w.SameLine(100f);
            Augmentation row = a;
            w.Selectable(Cut(a.Name, 26), "a.r." + a.Category + a.Id + a.Flag + a.Name, false,
                () => PrintEntry(row.Name, row.Flag, row.Effect, row.Hint, row.Url), 190f);
            if (a.IsUnknown) w.Tooltip("Asheron's Lesser Benediction is an item; RynthOracle can't count it.");
            w.SameLine(300f);
            w.Text(Cut(a.Effect, 34));
            string cost = a.CostText(prop, flags);
            if (cost.Length > 0)
            {
                w.SameLine(520f);
                w.Text(cost);
            }
        }
    }
}

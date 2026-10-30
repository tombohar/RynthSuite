using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// The Quest Timers window: the quests this character tracks (the * column on the Quests tab),
/// each with the time left before it can be done again, "ready", a kill task's kills left, or
/// done. Replaces RynthAi's Mini Remote quest favorites HUD and UtilityBelt ILT's favorites HUD.
/// Ready quests first, then by time left. Hovering a quest shows its flag and recorded rewards.
/// </summary>
internal sealed class QuestTimersView
{
    private const float StatusX = 190f;
    private const float RemoveX = 275f;

    private readonly OracleContext _c;
    private readonly List<Quest> _rows = new();

    public QuestTimersView(OracleContext c) => _c = c;

    public void Draw(UiWindow w)
    {
        if (!_c.InWorld)
        {
            w.TextDisabled("Not in the world.");
            return;
        }
        if (!_c.Book.Loaded)
        {
            w.TextDisabled("Waiting for the character...");
            return;
        }
        QuestFlagStore flags = _c.Flags;
        if (_c.Book.Tracked.Count == 0)
        {
            w.TextDisabled("No quests tracked.");
            w.TextWrapped("Tick * next to a quest on Oracle's Quests tab (/ro) to list it here.");
            return;
        }

        DateTime now = _c.UtcNow;
        _rows.Clear();
        foreach (string flag in _c.Book.Tracked)
            _rows.Add(_c.Catalog.TryGet(flag, out Quest q) ? q : new Quest { Flag = flag, IsNew = true });
        _rows.Sort((a, b) =>
        {
            int c = QuestTimer.SortKey(a, flags, now).CompareTo(QuestTimer.SortKey(b, flags, now));
            return c != 0 ? c : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });

        foreach (Quest q in _rows)
        {
            string flag = q.Flag;
            Quest row = q;
            w.Selectable(Cut(q.Label, 26), "qt.r." + flag, false, () => _c.PrintQuest(row), StatusX - 10f);
            string reward = _c.Book.RewardText(flag);
            w.Tooltip((reward.Length > 0 ? $"{q.Label}\n{flag}\nRewards: {reward}" : $"{q.Label}\n{flag}") + "\nClick: prints its texts in chat.");
            w.SameLine(StatusX);
            QuestTimerState s = QuestTimer.State(q, flags, now, out string text);
            bool held = flags.TryGet(flag, out _);
            switch (s)
            {
                case QuestTimerState.Ready: w.TextColored(UiColors.Green, held ? text : "not done"); break;
                case QuestTimerState.InProgress: w.TextColored(UiColors.Yellow, text); break;
                case QuestTimerState.Done: w.TextColored(UiColors.Grey, text); break;
                default: w.Text(text); break;
            }
            w.SameLine(RemoveX);
            w.SmallButton("x", "qt.x." + flag, () => _c.Book.SetTracked(flag, false));
            w.Tooltip("Stop tracking this quest.");
        }

        if (flags.Loading) w.TextColored(UiColors.Yellow, "Reading quest flags...");
        else if (flags.LastReadUtc == DateTime.MinValue) w.TextDisabled("Quest flags not read yet.");
        w.SmallButton("Refresh", "qt.refresh", () => _c.RequestQuests());
        w.Tooltip("Sends /myquests and reads the reply (timers count down by themselves between reads).");
        if (flags.LastReadUtc != DateTime.MinValue)
        {
            w.SameLine();
            w.TextDisabled($"read {flags.LastReadUtc.ToLocalTime():HH:mm}");
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 3)) + "...";
}

using System;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>How a quest stands right now, for the timer columns and the Quest Timers window.</summary>
internal enum QuestTimerState
{
    /// <summary>Can be done now (never done, or the timer ran out).</summary>
    Ready,
    /// <summary>Waiting on its repeat timer.</summary>
    Waiting,
    /// <summary>A kill task still collecting kills.</summary>
    InProgress,
    /// <summary>Done for good: a one-time quest, a solve limit reached, or a finished kill task with no timer.</summary>
    Done,
}

/// <summary>
/// Quest timer text shared by the Quests tab and the Quest Timers window. Kill tasks follow
/// UtilityBelt ILT's rule (QuestTracker.GetKTStatusText): "N left" while counting, then the reset
/// countdown, then "done". Kill-task flags are recognised by name as RynthAi's tracker did.
/// </summary>
internal static class QuestTimer
{
    // RynthAi's / UtilityBelt's kill-task pattern (counter flags), with a solve limit set.
    private static readonly Regex KillTaskRegex = new(@"(killtask|killcount|slayerquest|totalgolem.*dead|(kills$))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A kill-task counter flag (its name says so and it has a solve limit).</summary>
    public static bool IsKillTask(QuestFlag f) => f.MaxSolves > 0 && KillTaskRegex.IsMatch(f.Key);

    /// <summary>A quest's state; <paramref name="text"/> is what the column shows.</summary>
    public static QuestTimerState State(Quest q, QuestFlagStore flags, DateTime utcNow, out string text)
    {
        if (!flags.TryGet(q.Flag, out QuestFlag f))
        {
            text = "ready";
            return QuestTimerState.Ready;
        }
        return State(f, q.IsRepeatable(flags), utcNow, out text);
    }

    /// <summary>A held flag's state (<paramref name="repeatable"/>: the quest can be done again at all).</summary>
    public static QuestTimerState State(QuestFlag f, bool repeatable, DateTime utcNow, out string text)
    {
        TimeSpan left = f.NextAvailableTime(utcNow);
        if (IsKillTask(f))
        {
            if (f.Solves < f.MaxSolves)
            {
                text = $"{f.MaxSolves - f.Solves} left";
                return QuestTimerState.InProgress;
            }
            if (f.RepeatTime > TimeSpan.Zero && left > TimeSpan.Zero)
            {
                text = TimeText.Friendly(left);
                return QuestTimerState.Waiting;
            }
            text = "done";
            return QuestTimerState.Done;
        }
        if (!repeatable)
        {
            text = "completed";
            return QuestTimerState.Done;
        }
        if (f.AtSolveLimit)
        {
            text = "maxed";
            return QuestTimerState.Done;
        }
        if (left > TimeSpan.Zero)
        {
            text = TimeText.Friendly(left);
            return QuestTimerState.Waiting;
        }
        text = "ready";
        return QuestTimerState.Ready;
    }

    /// <summary>Sort key: ready first, then by time left, then kill tasks in progress, then done.</summary>
    public static double SortKey(Quest q, QuestFlagStore flags, DateTime utcNow)
    {
        QuestTimerState s = State(q, flags, utcNow, out _);
        return s switch
        {
            QuestTimerState.Ready => 0,
            QuestTimerState.Waiting when flags.TryGet(q.Flag, out QuestFlag f) => 1 + Math.Max(0, f.NextAvailableTime(utcNow).TotalSeconds),
            QuestTimerState.InProgress => 1e12,
            _ => 2e12,
        };
    }
}

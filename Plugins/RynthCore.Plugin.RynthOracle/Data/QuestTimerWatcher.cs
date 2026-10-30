using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// Notices when a quest timer comes up. It remembers which watched quests were on cooldown and
/// reports each one once, the moment its timer runs out. A quest that is simply ready when first
/// seen (the login read) is not reported: only the step from waiting to ready is.
/// </summary>
internal sealed class QuestTimerWatcher
{
    private readonly HashSet<string> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Quest> _ready = new();

    /// <summary>Forget everything (logout, or a different character).</summary>
    public void Reset() => _waiting.Clear();

    /// <summary>
    /// Checks the watched quests and returns those whose timers came up since the last check (the
    /// list is reused: read it before the next call). <paramref name="watch"/> is the flags to
    /// watch, or null for every quest the character holds.
    /// </summary>
    public IReadOnlyList<Quest> Check(QuestFlagStore flags, QuestCatalog catalog, IEnumerable<string>? watch, DateTime utcNow)
    {
        _ready.Clear();
        if (flags.LastReadUtc == DateTime.MinValue) return _ready;

        _seen.Clear();
        foreach (string flag in watch ?? flags.Flags.Keys)
        {
            _seen.Add(flag);
            Quest q = catalog.TryGet(flag, out Quest known) ? known : new Quest { Flag = flag, IsNew = true };
            if (!flags.TryGet(flag, out _)) { _waiting.Remove(flag); continue; }
            QuestTimerState state = QuestTimer.State(q, flags, utcNow, out _);
            if (state == QuestTimerState.Waiting) _waiting.Add(flag);
            else if (state == QuestTimerState.Ready && _waiting.Remove(flag)) _ready.Add(q);
            else _waiting.Remove(flag); // in progress, or done for good
        }
        _waiting.RemoveWhere(k => !_seen.Contains(k)); // no longer watched
        return _ready;
    }
}

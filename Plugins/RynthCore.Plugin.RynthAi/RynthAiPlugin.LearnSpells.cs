using System;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;

namespace RynthCore.Plugin.RynthAi;

// Learn unknown spells (VTank's ReadUnknownScrolls). The decisions live in
// Loot/ScrollLearner.cs; this is the plugin side: when it is safe to read, and the
// known-spell answer from the spellbook snapshot.
public sealed partial class RynthAiPlugin
{
    private ScrollLearner CreateScrollLearner(LegacyUiSettings settings) =>
        new(Host, settings, _objectCache)
        {
            PlayerId = () => _playerId,
            KnownSpell = IsSpellKnownForScrolls,
            Chat = s => Host.WriteToChat(s, 1),
        };

    /// <summary>
    /// From the spellbook snapshot only. With it cold there is no answer: the IsSpellKnown
    /// oracle can say "known" for spells the character doesn't know (see SpellManager).
    /// </summary>
    private bool? IsSpellKnownForScrolls(int spellId)
    {
        var sm = _spellManager;
        if (sm == null) return null;
        sm.RefreshKnownSpells();
        return sm.IsKnownSnapshotWarm ? sm.IsKnownSpellId(spellId) : null;
    }

    /// <summary>
    /// A safe moment to read a scroll: macro on, no monster within MonsterRange (nor one
    /// on top of us), not buffing (no cast in flight), not on a corpse, not salvaging, not
    /// in a portal. Reading puts the character in peace mode for a few seconds.
    /// </summary>
    internal bool IsSafeToReadScroll(LegacyUiSettings s)
    {
        if (!s.IsMacroRunning) return false;
        if (_activity is BotActivity.Combat or BotActivity.Buffing or BotActivity.Looting or BotActivity.Salvaging) return false;
        if (FightIsOn(s)) return false;
        if (_openedContainerId != 0 || _targetCorpseId != 0) return false;
        if (_buffManager != null && _buffManager.PendingSpellId != 0) return false;
        if (_salvageManager?.IsBusy == true) return false;
        if (_navigationEngine?.IsInPortalAction == true) return false;
        return true;
    }

    /// <summary>One learner tick; true while a read is in flight and the bot should hold still.</summary>
    private bool TickScrollLearner(LegacyUiSettings settings)
    {
        var learner = _scrollLearner;
        if (learner == null) return false;
        try
        {
            return learner.Tick(IsSafeToReadScroll(settings), _busyCount);
        }
        catch (Exception ex)
        {
            if (Environment.TickCount64 - _lastLearnErrorLogAt > 10_000)
            {
                _lastLearnErrorLogAt = Environment.TickCount64;
                Host.Log($"[Learn] tick threw: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }

    private long _lastLearnErrorLogAt = -100_000;
}

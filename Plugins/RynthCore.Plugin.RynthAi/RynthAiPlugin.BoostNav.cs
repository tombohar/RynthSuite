using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// Boost Nav Priority (owner's rule, 2026-11-02): with the flag on RynthAi ONLY navigates. No
// hunting, no looting, no fighting back; with nothing to navigate it stands idle. Buffing and
// vitals still run. The arbiter (ActivityArbiter.Decide) never picks Combat or Looting with the
// flag on, CombatManager.OnHeartbeat stands down (drops the target, ends the attack, lets go of a
// turn), the looter passes every corpse by (CorpseOpenController.BoostNavHoldsLoot) and no pet is
// summoned (PetManager). The flag comes on quietly from NavLootPrio or a hunting meta's SetOpt, and
// a player who never knew it was on saw only that the bot would not loot
// (R-20261102-041824-3f7c). So the first fight or corpse it skips says so, once per session.
public sealed partial class RynthAiPlugin
{
    internal const string BoostNavNotice =
        "[RynthAi] Boost Nav Priority is on: RynthAi only navigates (no hunting, no looting). Turn it off to hunt and loot.";

    /// <summary>How often the corpse half of the notice looks for a corpse it would have looted.</summary>
    internal const long BoostNavLootCheckMs = 2000;

    private bool _boostNavNoticeSaid;      // once per session (reset at logout)
    private long _boostNavLootCheckAt;     // CorpseNowMs of the last corpse look

    /// <summary>
    /// After the arbiter's decision, every tick: Boost Nav Priority is why a fight (combat on and a
    /// monster to engage) or a corpse (looting on and one in range it would loot) is skipped. Says
    /// so once per session, in chat and the log; nothing at all once said.
    /// </summary>
    private void NoteBoostNavSkip(LegacyUiSettings settings, bool wantCombat)
    {
        if (_boostNavNoticeSaid || !settings.IsMacroRunning || !settings.BoostNavPriority) return;

        string? skipped = null;
        if (wantCombat)
            skipped = "a fight";
        else if (settings.EnableLooting)
        {
            long now = CorpseNowMs;
            if (now - _boostNavLootCheckAt >= BoostNavLootCheckMs)
            {
                _boostNavLootCheckAt = now;
                if (BoostNavSkipsLoot(settings)) skipped = "a corpse";
            }
        }
        if (skipped == null) return;

        _boostNavNoticeSaid = true;
        Host.Log($"{BoostNavNotice} (skipped {skipped})");
        Host.WriteToChat(BoostNavNotice, 2);
    }
}

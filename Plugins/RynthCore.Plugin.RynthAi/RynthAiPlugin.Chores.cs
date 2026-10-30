using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// Item chores and fights (structural audit 2026-10-21, finding 3): missile crafting held combat with
// no danger check, and mana tapping and AutoCram/AutoStack acted between swings (their only gate was
// the busy count, and a swing repeat is not a paced action). They now all wait on one signal,
// FightIsOn, the one salvage already used.
public sealed partial class RynthAiPlugin
{
    /// <summary>
    /// A fight is on: a monster within Monster Range or on top of us, or the target combat is locked
    /// on (CombatManager.HasEngageableTarget, the arbiter's Combat input, and IsUnderCloseAttack).
    /// Optional item work waits for it to end: salvage, AutoCram/AutoStack, draining loot into mana
    /// stones, reading scrolls, and missile crafting's optional crafts (MissileCraftingManager).
    /// Only with the macro on: the target scan stops with the macro, and its last picture must not
    /// hold a /ub autocram or a salvage the player asks for.
    /// </summary>
    internal bool FightIsOn(LegacyUiSettings? s = null)
    {
        s ??= _dashboard?.Settings;
        if (s == null || !s.IsMacroRunning || !s.EnableCombat) return false;
        var combat = _combatManager;
        return combat != null && (combat.HasEngageableTarget || combat.IsUnderCloseAttack);
    }

    /// <summary>
    /// AutoCram / AutoStack, one tick: not while a corpse is open or claimed, and not in a fight (a
    /// pack move went out between melee swings). The manager itself waits for the busy count.
    /// </summary>
    private void TickCramStack(LegacyUiSettings settings)
    {
        if (_inventoryManager == null || _openedContainerId != 0 || _targetCorpseId != 0) return;
        if (FightIsOn(settings)) return;
        Guard(TickStep.CramStack, () => _inventoryManager?.OnHeartbeat(PacedBusy));
    }
}

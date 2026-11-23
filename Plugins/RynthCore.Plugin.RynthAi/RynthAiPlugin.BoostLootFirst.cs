// ============================================================================
//  RynthSuite - RynthAiPlugin.BoostLootFirst.cs
//  Boost Loot Priority: corpses first, then the hunt (2026-11-22: "boost loot should loot any
//  new corpse available and hunt after looting is complete").
//
//  R-20261122-010831-9488 (Shadowfang Keep, Conquest): fast spawns, and Boost Loot "is not looting
//  every corpse before moving along to the next". It gave way to any monster within 5 yd twice over:
//  the arbiter gave Looting the tick only while combat wasn't engaged (a target locked, or a monster
//  within CloseAttackYards), and combat's hold on its next target (BoostLootHoldsNewTarget) ended the
//  moment a monster came that close. In a room that refills at once one always is, so the next
//  monster was locked 1 ms after each kill and the corpses waited for a lull that rarely came.
//
//  Now, with Boost Loot on, while there is loot work (a corpse claimed, open, lootable in range, or
//  the kill's corpse still forming) neither gives way to a close monster: the corpses are looted,
//  then the fight and the hunt go on. One exception kept for safety: with health under Heal At (the
//  line a fight heals at) a monster on you is fought as before, so looting never stands still while
//  the character is being killed.
// ============================================================================

using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    /// <summary>
    /// Boost Loot Priority puts corpses ahead of a monster that is close or already fought: Boost Loot
    /// can run, and health is at or above Heal At (or not known yet).
    /// </summary>
    private bool BoostLootFirst(LegacyUiSettings settings)
        => BoostLootCanRun(settings) && (_vitals.MaxHealth == 0 || _vitals.HealthPct >= settings.HealAt);

    /// <summary>CombatManager.HoldNewTargetThroughThreat: the hold on the next target applies with a monster close.</summary>
    private bool BoostLootHoldsThroughThreat()
        => _dashboard?.Settings is { } s && BoostLootFirst(s);
}

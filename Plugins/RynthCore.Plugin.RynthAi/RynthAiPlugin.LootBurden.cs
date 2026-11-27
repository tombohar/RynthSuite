// ============================================================================
//  RynthSuite - RynthAiPlugin.LootBurden.cs
//  Loot the server would refuse for burden is left with a clear line, not resent (2026-11-23).
//
//  R-20261123-065337-d200 (Item Tool chest looter on Legendary Armor Chests): "the item he is
//  trying to loot will flicker and he will seem like he is trying to pick the item up". An
//  'Olthoi Breastplate' kept by the profile was refused 94 ms after every send with no error
//  code: 3 resends taken for "too busy", 3 tries, then left - and the next key on the chest
//  found it again, chest after chest. ACE turns a pickup away before any motion when the
//  character's burden plus the item's would pass 300% of its carrying capacity
//  (Player.HandleActionPutItemInContainer_Verify, HasEnoughBurdenToAddToInventory: "You are too
//  encumbered to carry that!" and InventoryServerSaveFailed with no code), so it looks exactly
//  like a busy refusal. The client knows all of it: its own burden (STypeInt 5, sent on login and
//  kept current), its Strength, the carrying capacity augmentation (STypeInt 230) and the item's
//  burden (STypeInt 5). An item that can't go in is now left before it is sent, with one chat
//  line every 5 minutes; lighter items on the same corpse or chest are still taken.
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private const uint BurdenIntKey = 5;              // STypeInt ENCUMB_VAL: an object's burden (the character's: all it carries)
    private const uint CarryCapacityAugIntKey = 230;  // STypeInt AugmentationIncreasedCarryingCapacity
    private const uint StrengthAttribute = 1;

    /// <summary>
    /// ACE's carrying capacity: 150 burden per point of current Strength, plus 30 per point for
    /// each carrying capacity augmentation (Player.GetEncumbranceCapacity).
    /// </summary>
    internal static long CarryCapacity(uint strength, int capacityAugs)
        => 150L * strength + 30L * Math.Max(0, capacityAugs) * strength;

    /// <summary>
    /// The server refuses this pickup for burden: what the character carries plus the item's burden
    /// is over 3 times its carrying capacity (Player.HasEnoughBurdenToAddToInventory).
    /// </summary>
    internal static bool PickupOverBurdenLimit(int carried, int itemBurden, uint strength, int capacityAugs, out long limit)
    {
        limit = 3 * CarryCapacity(strength, capacityAugs);
        return strength > 0 && itemBurden > 0 && (long)carried + itemBurden > limit;
    }

    /// <summary>
    /// The item can't be picked up for burden. False whenever a number isn't known (no character, no
    /// Strength, no burden on the item yet): then the server decides, as before.
    /// </summary>
    private bool LootItemOverBurden(int itemId, out string detail)
    {
        detail = "";
        try
        {
            if (_playerId == 0 || _objectCache == null || !Host.HasGetObjectIntProperty || !Host.HasGetObjectAttribute)
                return false;
            if (!Host.TryGetObjectIntProperty(_playerId, BurdenIntKey, out int carried))
                return false;
            if (!Host.TryGetObjectAttribute(_playerId, StrengthAttribute, 0, out uint strength) || strength == 0)
                return false;
            int augs = Host.TryGetObjectIntProperty(_playerId, CarryCapacityAugIntKey, out int a) ? a : 0;
            int itemBurden = _objectCache.GetIntProperty(itemId, BurdenIntKey, 0);
            if (!PickupOverBurdenLimit(carried, itemBurden, strength, augs, out long limit))
                return false;
            long capacity = CarryCapacity(strength, augs);
            long total = carried + (long)itemBurden;
            detail = $"you carry {carried} burden and it weighs {itemBurden}: {total} is {total * 100 / capacity}% of your {capacity} capacity, over the server's 300% limit of {limit}";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private DateTime _overBurdenWarnedAt = DateTime.MinValue;

    /// <summary>Once per 5 minutes in chat: loot is left for burden (looting goes on).</summary>
    private void WarnLootOverBurden(string itemName, string detail)
    {
        if ((RynthClock.Now - _overBurdenWarnedAt).TotalMinutes < 5) return;
        _overBurdenWarnedAt = RynthClock.Now;
        ChatLine($"[RynthAi] Too heavy to loot: '{itemName}' is left ({detail}). The server won't let a character pick up past 300% burden. " +
                 "Drop, sell or salvage something heavy and looting takes it again.");
    }
}

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// Keep # (KeepUpTo) arithmetic, split out of <c>RynthAiPlugin.UnderKeepCap</c> so it can be
/// tested offline. The plugin passes in the live pack snapshot and its per-corpse "approved
/// but not in the pack yet" table; nothing here touches the host.
/// </summary>
internal static class LootKeepCap
{
    private const int ValueKey = 19;    // STypeInt VALUE (a stack's is the whole stack's)
    private const int BurdenKey = 5;    // STypeInt ENCUMB_VAL (likewise)

    /// <summary>How much one item counts toward a cap: its stack size, never less than 1.</summary>
    internal static int StackOf(WorldObject item) => Math.Max(1, item.Values(LongValueKey.StackCount, 1));

    /// <summary>
    /// One unit of a stack, for testing a pack item against a Keep # rule: the same item with its
    /// value and burden divided by its stack size (UB's Value / Stack, as AutoVendor prices it).
    /// A stack's value is the whole stack's, so "value over 10,000, burden under 400" matched a
    /// 25,000-pyreal stack and counted all 25,000 toward a cap of 25 (2026-10-29); spell components
    /// and trade goods did the same. Rules by name count every unit as before. A single item, or a
    /// stack whose value and burden aren't known, comes back unchanged.
    /// </summary>
    internal static WorldObject OneUnit(WorldObject item)
    {
        int stack = StackOf(item);
        if (stack <= 1) return item;
        int value = item.Values(ValueKey, int.MinValue);
        int burden = item.Values(BurdenKey, int.MinValue);
        if (value == int.MinValue && burden == int.MinValue) return item;

        var from = item.Overlay;
        var unit = new ItemPropertyOverlay { LiveFallback = from?.LiveFallback ?? true };
        if (from != null)
        {
            foreach (var kv in from.Ints) unit.Ints[kv.Key] = kv.Value;
            foreach (var kv in from.Strings) unit.Strings[kv.Key] = kv.Value;
            foreach (var kv in from.Doubles) unit.Doubles[kv.Key] = kv.Value;
        }
        if (value != int.MinValue) unit.Ints[ValueKey] = value / stack;
        if (burden != int.MinValue) unit.Ints[BurdenKey] = burden / stack;
        return new WorldObject(item.Id, item.Name, item.ObjectClass)
        {
            Cache = item.Cache,
            Overlay = unit,
            _wieldedLocationDirect = item._wieldedLocationDirect,
            _directContainerId = item._directContainerId,
            _directSlot = item._directSlot,
        };
    }

    /// <summary>
    /// How many of what a Keep # rule matches the player already has: whole stacks of every
    /// named pack item whose one unit the rule matches (<see cref="OneUnit"/>; a matcher that
    /// throws counts as no match), plus items approved on this corpse that are not in the pack
    /// yet. An approved item that is already in the pack snapshot, or that no longer exists (a
    /// looted stack merged into a pack stack and was deleted), is not counted twice.
    /// </summary>
    internal static int CountHave(
        IEnumerable<WorldObject>? inventory,
        Func<WorldObject, bool> matches,
        IReadOnlyDictionary<int, int> pending,
        Func<int, bool> stillExists)
    {
        int have = 0;
        var liveIds = new HashSet<int>();
        if (inventory != null)
        {
            foreach (var inv in inventory)
            {
                liveIds.Add(inv.Id);
                if (string.IsNullOrEmpty(inv.Name)) continue;
                bool m;
                try { m = matches(OneUnit(inv)); } catch { m = false; }
                if (m) have += StackOf(inv);
            }
        }
        foreach (var kv in pending)
            if (!liveIds.Contains(kv.Key) && stillExists(kv.Key)) have += kv.Value;
        return have;
    }

    /// <summary>True while the player holds fewer than <paramref name="cap"/>; a cap of 0 or less never loots.</summary>
    internal static bool UnderCap(int have, int cap) => have < cap;
}

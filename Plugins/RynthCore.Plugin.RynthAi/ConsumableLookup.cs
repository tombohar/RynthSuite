using System;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The item a Consumables rule means right now. A rule is saved with the stack it was added
/// from (its object id), and that stack disappears when it runs out: 200 Stamina Tinctures
/// used up, and the next stack was never touched until the player added it again (report
/// 2026-10-10). So: the listed stack while it is still known, else any carried item with the
/// same name. Potions, healing kits and lockpicks go through here; pets and mana stones
/// already matched by name.
/// </summary>
internal static class ConsumableLookup
{
    public static WorldObject? Resolve(WorldObjectCache cache, ConsumableRule rule)
    {
        if (rule.Id != 0 && cache[rule.Id] is WorldObject byId) return byId;
        if (string.IsNullOrWhiteSpace(rule.Name)) return null;
        foreach (WorldObject wo in cache.GetInventory())
            if (wo != null && wo.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))
                return wo;
        return null;
    }
}

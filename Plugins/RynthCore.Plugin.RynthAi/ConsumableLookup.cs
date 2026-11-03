using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The item a Consumables rule means right now. A rule is saved with the stack it was added
/// from (its object id), and that stack disappears when it runs out: 200 Stamina Tinctures
/// used up, and the next stack was never touched until the player added it again (report
/// 2026-10-10). So: the listed stack while it is still carried, else any carried item with the
/// same name. Potions, healing kits and lockpicks go through here; pets and mana stones
/// already matched by name.
///
/// "Carried" is the live container walk (WorldObjectCache.GetDirectInventory: the client's own
/// pack contents, read at most once a second), not the cache's inventory set: a used-up kit
/// can stay in that set when its removal is missed, and was then "used" over and over with
/// nothing happening (2026-11-03, DreamWeave: a used-up Treated Healing Kit was picked 70 times
/// after a respawn, every use answered at once with no heal, and only natural regeneration
/// raised health). Without the walk (an engine without GetContainerContents) the cache is used.
/// </summary>
internal static class ConsumableLookup
{
    public static WorldObject? Resolve(WorldObjectCache cache, ConsumableRule rule)
    {
        IReadOnlyList<WorldObject> carried = cache.GetDirectInventory();
        if (carried.Count == 0)
            return ResolveFromCache(cache, rule);   // no walk yet (or none possible): the cache's view
        if (rule.Id != 0)
            foreach (WorldObject wo in carried)
                if (wo != null && wo.Id == rule.Id) return wo;
        if (string.IsNullOrWhiteSpace(rule.Name)) return null;
        foreach (WorldObject wo in carried)
            if (wo != null && wo.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))
                return wo;
        return null;
    }

    private static WorldObject? ResolveFromCache(WorldObjectCache cache, ConsumableRule rule)
    {
        if (rule.Id != 0 && cache[rule.Id] is WorldObject byId) return byId;
        if (string.IsNullOrWhiteSpace(rule.Name)) return null;
        foreach (WorldObject wo in cache.GetInventory())
            if (wo != null && wo.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase))
                return wo;
        return null;
    }
}

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
///
/// <paramref name="skip"/>: ids the caller knows are gone although the walk may still list them (a kit
/// its result line called used up, inside the walk's 1 s reuse; BuffManager.KitUsedUp). A skipped
/// listed id falls through to the name match, so the next stack of that name is used.
/// </summary>
internal static class ConsumableLookup
{
    public static WorldObject? Resolve(WorldObjectCache cache, ConsumableRule rule, Func<int, bool>? skip = null)
    {
        IReadOnlyList<WorldObject> carried = cache.GetDirectInventory();
        if (carried.Count == 0)
            return ResolveFromCache(cache, rule, skip);   // no walk yet (or none possible): the cache's view
        if (rule.Id != 0)
            foreach (WorldObject wo in carried)
                if (wo != null && wo.Id == rule.Id && !Skipped(skip, wo.Id)) return wo;
        if (string.IsNullOrWhiteSpace(rule.Name)) return null;
        foreach (WorldObject wo in carried)
            if (wo != null && wo.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase) && !Skipped(skip, wo.Id))
                return wo;
        return null;
    }

    private static WorldObject? ResolveFromCache(WorldObjectCache cache, ConsumableRule rule, Func<int, bool>? skip)
    {
        if (rule.Id != 0 && !Skipped(skip, rule.Id) && cache[rule.Id] is WorldObject byId) return byId;
        if (string.IsNullOrWhiteSpace(rule.Name)) return null;
        foreach (WorldObject wo in cache.GetInventory())
            if (wo != null && wo.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase) && !Skipped(skip, wo.Id))
                return wo;
        return null;
    }

    private static bool Skipped(Func<int, bool>? skip, int id) => skip != null && skip(id);
}

using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>What <see cref="LootPickup.Send"/> did.</summary>
internal enum LootPickupResult
{
    /// <summary>MoveItemExternal into one of our packs was sent.</summary>
    Moved,
    /// <summary>UseObject was sent (no move call on this engine, and the item can't be worn).</summary>
    Used,
    /// <summary>No pack of ours has a free slot.</summary>
    NoRoom,
    /// <summary>No move call, and a use could put the item on the character, so nothing was sent.</summary>
    Refused,
    /// <summary>The engine turned the call down.</summary>
    Failed,
}

/// <summary>
/// Picks a corpse item up into a pack. Looting used to send UseObject (a double-click). On
/// a wearable item outside the pack the client turns a use into get-and-wield when the slot
/// is free, so jewellery looted for salvage went onto the character whenever its ring,
/// bracelet or necklace slot was empty (2026-10-02 tester report). A move into a pack never
/// wields anything, so it is the only call used when the engine has it.
/// </summary>
internal static class LootPickup
{
    /// <remarks><paramref name="skip"/>: destinations the server refused lately (<see cref="PackRefusals"/>).</remarks>
    public static LootPickupResult Send(RynthCoreHost host, WorldObjectCache? cache, uint itemId, uint playerId,
        out uint destination, System.Func<int, bool>? skip = null)
    {
        destination = 0;
        if (itemId == 0) return LootPickupResult.Failed;

        if (host.HasMoveItemExternal)
        {
            if (playerId == 0) return LootPickupResult.Failed;
            int pid = unchecked((int)playerId);
            int dest = cache == null
                ? pid
                : WorldObjectCache.FindLootDestination(cache.GetDirectInventory(forceRefresh: false), pid, host, skip);
            if (dest == 0) return LootPickupResult.NoRoom;
            destination = unchecked((uint)dest);
            return host.MoveItemExternal(itemId, destination, 0) ? LootPickupResult.Moved : LootPickupResult.Failed;
        }

        if (CouldBeWorn(cache?[unchecked((int)itemId)]))
            return LootPickupResult.Refused;
        return host.HasUseObject && host.UseFor(itemId, "Loot", "pick up from the open corpse") ? LootPickupResult.Used : LootPickupResult.Failed;
    }

    /// <summary>
    /// True when a use on <paramref name="wo"/> could wield it: it has equip slots, or it is
    /// a kind of thing that is worn or wielded. Unknown items count as wearable.
    /// </summary>
    public static bool CouldBeWorn(WorldObject? wo)
    {
        if (wo == null) return true;
        if (wo.Values(LongValueKey.Locations, 0) != 0) return true;
        return wo.ObjectClass is AcObjectClass.Unknown or AcObjectClass.Jewelry or AcObjectClass.Armor
            or AcObjectClass.Clothing or AcObjectClass.MeleeWeapon or AcObjectClass.MissileWeapon
            or AcObjectClass.WandStaffOrb;
    }
}

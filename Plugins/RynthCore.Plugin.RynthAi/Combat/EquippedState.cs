using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// What the player wields and where, from the engine's EquipmentTracker (API v87: the server's
/// WieldObject / InventoryPutObjInContainer / InventoryPutObjectIn3D messages, the way Decal knows
/// it). When the engine has it, it is the only answer to "is it in hand?": the client's own
/// fields go stale on this client (CurrentWieldedLocation reads 0, the wielder record keeps the
/// player after the item went into a pack), and the guesses around them had Drakkon fighting
/// bare-handed and dying (2026-10-17/18). Read at most every <see cref="TtlMs"/>; any thread.
/// </summary>
internal static class EquippedState
{
    private const long TtlMs = 100;
    private static readonly object Gate = new();
    private static Dictionary<int, int> _map = new();
    private static long _at;
    private static bool _fresh;   // a read is cached (a flag, not a sentinel time: now - long.MinValue overflows)
    private static bool _ok;

    /// <summary>Tests: a fixed clock (null = Environment.TickCount64).</summary>
    internal static Func<long>? Clock;

    /// <summary>The wielded items (id -> EquipMask). False when the engine has no tracker or it can't start yet.</summary>
    public static bool TryGet(RynthCoreHost host, out Dictionary<int, int> map)
    {
        long now = Clock?.Invoke() ?? Environment.TickCount64;
        lock (Gate)
        {
            if (_fresh && now - _at < TtlMs) { map = _map; return _ok; }
            _at = now;
            _fresh = true;
            if (!host.HasGetWieldedItems || !host.TryGetWieldedItems(out var items)) { _ok = false; map = _map = new(); return false; }
            var m = new Dictionary<int, int>(items.Length);
            foreach (var (id, loc) in items) m[unchecked((int)id)] = unchecked((int)loc);
            _map = m;
            _ok = true;
            map = m;
            return true;
        }
    }

    /// <summary>Wielded per the tracker; null when there is no tracker (the caller's own logic then).</summary>
    public static bool? IsWielded(RynthCoreHost host, int id) =>
        TryGet(host, out var map) ? map.ContainsKey(id) : null;

    /// <summary>The slot per the tracker (0 = not wielded); null when there is no tracker.</summary>
    public static int? SlotOf(RynthCoreHost host, int id) =>
        TryGet(host, out var map) ? (map.TryGetValue(id, out int loc) ? loc : 0) : null;

    /// <summary>Drop the cached read (a wield or move was just sent, or tests).</summary>
    public static void Invalidate() { lock (Gate) _fresh = false; }

    /// <summary>
    /// What is wielded and where (item id -> EquipMask), read fresh: the tracker's list, or without
    /// it (an engine before v87) the client's own fields, a slot read 0 guessed from the item's kind.
    /// A copy, safe to hold across ticks (the hand swaps, HandSwapSequence).
    /// </summary>
    public static Dictionary<int, int> ReadFresh(RynthCoreHost host, WorldObjectCache? cache)
    {
        Invalidate();
        if (TryGet(host, out var map)) return new Dictionary<int, int>(map);
        var m = new Dictionary<int, int>();
        if (cache == null) return m;
        foreach (var wo in cache.GetDirectInventory(forceRefresh: true))
        {
            if (!WorldObjectCache.IsWieldedByPlayer(host, wo)) continue;
            int slot = wo.Values(LongValueKey.CurrentWieldedLocation, 0);
            if (slot == 0)
                slot = WeaponList.IsWand(wo) ? HandSwapSequence.HeldSlot
                     : wo.ObjectClass == AcObjectClass.MissileWeapon ? HandSwapSequence.MissileSlot
                     : wo.ObjectClass == AcObjectClass.MeleeWeapon ? HandSwapSequence.MeleeSlot : 0;
            if (slot != 0) m[wo.Id] = slot;
        }
        return m;
    }
}

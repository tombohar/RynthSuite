using System;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Shared serializer for weapon/wand equip actions across BuffManager and
/// CombatManager (and any other subsystem that swaps the wielded item).
///
/// AC allows only ONE item action in flight at a time. Two UseObject equips
/// fired within ~1s — the buff↔combat wand/sword flap, or CombatManager's own
/// re-equip + EquipWeaponAndSetStance in the same tick — collide and trigger
/// the "you can only move or use one item at a time" notice, and in the worst
/// case an object-teardown access violation (DBOCache::DestroyObj). Each
/// subsystem had its OWN equip throttle (_lastEquipTime, _pendingWieldId) but
/// they didn't know about each other, so cross-subsystem swaps still raced.
///
/// This gate is a single shared instance both managers consult: a swap is only
/// allowed if no other swap has been issued within MinIntervalMs. The loser
/// yields and retries on a later tick. Spell casts and non-weapon item actions
/// (loot moves, corpse opens) do NOT go through here.
/// </summary>
public sealed class WeaponSwapGate
{
    private readonly object _lock = new();
    private long _lastSwapTickMs = long.MinValue / 4;
    private string _lastSwapBy = "";

    /// <summary>
    /// Minimum milliseconds between two weapon swaps. ~3s comfortably covers
    /// AC's equip + stance-change settle (matches the existing 3000ms cast
    /// equip-gate in CombatManager) so a swap fully resolves before the next.
    /// </summary>
    public int MinIntervalMs { get; set; } = 3000;

    /// <summary>
    /// Atomically claim the swap slot. Returns true (recording the swap) when
    /// no swap has occurred within MinIntervalMs; false when the caller must
    /// wait. Only mutates state when it returns true, so a denied caller never
    /// "wastes" the slot.
    /// </summary>
    public bool TryBeginSwap(string requestedBy)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            if (now - _lastSwapTickMs < MinIntervalMs)
                return false;
            _lastSwapTickMs = now;
            _lastSwapBy = requestedBy ?? "";
            return true;
        }
    }

    /// <summary>Milliseconds since the last successful swap (diagnostics).</summary>
    public long MsSinceLastSwap
    {
        get { lock (_lock) { return Environment.TickCount64 - _lastSwapTickMs; } }
    }

    /// <summary>Who last claimed a swap (diagnostics).</summary>
    public string LastSwapBy { get { lock (_lock) { return _lastSwapBy; } } }
}

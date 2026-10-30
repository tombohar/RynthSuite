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
/// allowed once the last one is done on the server (or MinIntervalMs has passed).
/// The loser yields and retries on a later tick. Spell casts and non-weapon item actions
/// (loot moves, corpse opens) do NOT go through here.
/// </summary>
public sealed class WeaponSwapGate
{
    private readonly object _lock = new();
    private long _lastSwapTickMs = long.MinValue / 4;
    private string _lastSwapBy = "";
    private int _movingId;
    private long _claims;
    private string _heldBy = "";
    private long _heldUntilMs;

    /// <summary>Swaps claimed so far: a caller can tell whether another swap went out after its own.</summary>
    public long Claims { get { lock (_lock) { return _claims; } } }

    /// <summary>
    /// The longest one swap holds the next: a swap the server's answer can't be seen for (or
    /// with no <see cref="ServerIdle"/>) waits this long, as before 2026-10-06.
    /// </summary>
    public int MinIntervalMs { get; set; } = 3000;

    /// <summary>
    /// Swap speed (2026-10-06): the next swap may go once the last one is done on the server,
    /// after at least this long. A weapon change takes up to three steps (a shield or arrows
    /// into the pack, the weapon, the off hand); at a flat 3 s per step it took 6-9 s.
    /// </summary>
    public int FloorMs { get; set; } = 400;

    /// <summary>True when no use or cast is waiting for the server's answer (ActionPacer).</summary>
    public Func<bool>? ServerIdle { get; set; }

    /// <summary>Reads whether the character wields an item (for a noted move, <see cref="NoteMove"/>).</summary>
    public Func<int, bool>? IsWielded { get; set; }

    public Func<long> NowMs { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// Atomically claim the swap slot. Returns true (recording the swap) when the last swap is
    /// done on the server (see <see cref="FloorMs"/>) or MinIntervalMs has passed; false when
    /// the caller must wait. Only mutates state when it returns true, so a denied caller never
    /// "wastes" the slot.
    /// </summary>
    public bool TryBeginSwap(string requestedBy)
    {
        long now = NowMs();
        lock (_lock)
        {
            if (_heldBy.Length > 0 && now < _heldUntilMs) return false;   // a multi-step swap is running (Hold)
            long since = now - _lastSwapTickMs;
            if (since < MinIntervalMs && !LastSwapDone(since))
                return false;
            _lastSwapTickMs = now;
            _lastSwapBy = requestedBy ?? "";
            _movingId = 0;
            _claims++;
            return true;
        }
    }

    /// <summary>
    /// A multi-step swap (HandSwapSequence, 2026-10-22) that has claimed the gate keeps it for its
    /// steps: no other swap goes until <see cref="Release"/>, or <paramref name="maxMs"/> as a backstop.
    /// Its own steps are paced on the server's answers, not by this gate.
    /// </summary>
    public void Hold(string by, long maxMs)
    {
        lock (_lock)
        {
            _heldBy = by ?? "?";
            _heldUntilMs = NowMs() + maxMs;
        }
    }

    /// <summary>
    /// The held swap is over. <paramref name="lastMovedId"/> (its last pack move or slot wield, 0 =
    /// none) holds the next swap the full <see cref="MinIntervalMs"/> from now, as a noted move does.
    /// </summary>
    public void Release(string by, int lastMovedId)
    {
        lock (_lock)
        {
            if (_heldBy != (by ?? "?")) return;
            _heldBy = "";
            _lastSwapTickMs = NowMs();
            _movingId = lastMovedId;
        }
    }

    /// <summary>Who holds the gate for a multi-step swap ("" = no one; diagnostics).</summary>
    public string HeldBy { get { lock (_lock) { return _heldBy.Length > 0 && NowMs() < _heldUntilMs ? _heldBy : ""; } } }

    /// <summary>
    /// The claimed swap moved <paramref name="itemId"/> into or out of a hand without a use (a
    /// pack move or a slot wield): the next swap waits the full MinIntervalMs, as before.
    /// Drakkon 2026-10-06 13:58: the wand went out 1.3 s after the axe's pack move (the axe read
    /// out of hand) and the server then held the first cast open until a relog. The server's
    /// unwield motion outlasts the item move, and nothing tells us when it ends.
    /// </summary>
    public void NoteMove(int itemId, bool wieldedNow)
    {
        lock (_lock) _movingId = itemId;
    }

    /// <summary>
    /// The caller saw the noted move of <paramref name="itemId"/> finish and settle: the next swap
    /// is paced on the server's answers (<see cref="FloorMs"/>) instead of the full MinIntervalMs.
    /// Only for moves with no unwield motion behind them (ammo out of the ammunition slot); a
    /// weapon or shield pack move keeps the full wait (see <see cref="NoteMove"/>).
    /// </summary>
    public void MoveDone(int itemId)
    {
        lock (_lock)
        {
            if (_movingId == itemId) _movingId = 0;
        }
    }

    private bool LastSwapDone(long since)
    {
        if (since < FloorMs || ServerIdle == null || IsWielded == null) return false;
        try
        {
            return _movingId == 0 && ServerIdle();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True while <paramref name="itemId"/> may still be on its way into or out of a hand: a
    /// multi-step swap holds the gate, or the last claimed swap noted a move of this item and the
    /// next swap would still wait for it (MinIntervalMs). Buffing doesn't aim a spell at a shield
    /// then (BuffManager.Shield.cs).
    /// </summary>
    public bool MayBeMoving(int itemId)
    {
        long now = NowMs();
        lock (_lock)
        {
            if (_heldBy.Length > 0 && now < _heldUntilMs) return true;
            return itemId != 0 && _movingId == itemId && now - _lastSwapTickMs < MinIntervalMs;
        }
    }

    /// <summary>Milliseconds since the last successful swap (diagnostics).</summary>
    public long MsSinceLastSwap
    {
        get { lock (_lock) { return NowMs() - _lastSwapTickMs; } }
    }

    /// <summary>Who last claimed a swap (diagnostics).</summary>
    public string LastSwapBy { get { lock (_lock) { return _lastSwapBy; } } }
}

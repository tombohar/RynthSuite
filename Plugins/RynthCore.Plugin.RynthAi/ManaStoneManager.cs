using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Mana stone tapping state machine.
///
/// Each think tick from Idle picks one of two actions:
///   1. If any worn item has CurrentMana below the threshold AND we have a
///      charged stone in inventory, use the stone on the player.
///   2. Otherwise, if we have an empty stone and an inventory item with at
///      least the threshold of CurrentMana, drain it.
///
/// Single-shot semantics:
///   - Using an empty stone on an item destroys the item and charges the stone.
///   - Using a charged stone on the player may also destroy the stone — if so
///     we just loot another next corpse.
/// </summary>
internal sealed class ManaStoneManager
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _objectCache;

    private enum TapState { Idle, TappingItem, UsingOnPlayer }

    private TapState _state        = TapState.Idle;
    private int      _activeStoneId;
    private int      _activeItemId;
    private long     _actionIssuedAt;
    private long     _lastThinkAt;

    // After a use-on-player times out we blacklist the offending stone
    // for a while so we don't re-select it next tick and loop forever.
    private readonly Dictionary<int, long> _stoneUseOnPlayerCooldownUntil = new();
    private const long StoneUseOnPlayerCooldownMs = 5 * 60 * 1000;

    // Same idea for tap targets: an item that won't drain (wielded server-side,
    // already at 0 mana, etc.) gets shelved for 5 minutes so we move on.
    private readonly Dictionary<int, long> _tapTargetCooldownUntil = new();
    private const long TapTargetCooldownMs = 5 * 60 * 1000;

    // And for the stone half of the pair: a "stone" that AC silently refuses
    // to use (often because it's actually charged but our cache reads its
    // CurrentMana as 0/unreadable) gets parked so FindEmptyStone moves on.
    // Short cooldown — most failures are transient (server lag, mid-action),
    // and a genuinely-charged stone is filtered by the cur>0 check anyway.
    private readonly Dictionary<int, long> _stoneTapCooldownUntil = new();
    private const long StoneTapCooldownMs = 30 * 1000;

    // Stones we've positively confirmed are charged via AC chat ("is already
    // full of mana"). Distinct from the cooldown so the cache's stale CurrentMana
    // reading doesn't auto-clear them. Only cleared when the stone disappears
    // from cache (consumed via use-on-player).
    private readonly HashSet<int> _stoneKnownCharged = new();

    // Worn equipment is only treated as "needs refill" when current mana drops
    // below this fraction of its MaxMana. Prevents the bot from continually
    // dumping stones on the player just because an item dipped slightly.
    private const double WornRefillPctOfMax = 0.25;

    private const long ThinkIntervalMs  = 600;
    private const long ActionTimeoutMs  = 8000;

    private static long NowMs => Environment.TickCount64;

    public ManaStoneManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache objectCache)
    {
        _host        = host;
        _settings    = settings;
        _objectCache = objectCache;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void OnHeartbeat(int busyCount)
    {
        if (!_settings.EnableManaTapping) { Reset(); return; }
        if (!_settings.IsMacroRunning)    { Reset(); return; }

        long now = NowMs;

        // Positive completion check: if the stone or target item is no longer
        // in the cache, the action effectively completed (stone was destroyed
        // mid-use, item drained and destroyed). Don't wait for chat — clear
        // the state so we can issue the next action.
        if (_state == TapState.TappingItem)
        {
            bool itemGone  = _activeItemId  != 0 && _objectCache[_activeItemId]  == null;
            bool stoneGone = _activeStoneId != 0 && _objectCache[_activeStoneId] == null;
            if (itemGone || stoneGone)
            {
                _host.Log($"[RynthAi] ManaStone: tap completion detected (itemGone={itemGone} stoneGone={stoneGone}).");
                GoIdle();
            }
        }
        else if (_state == TapState.UsingOnPlayer)
        {
            if (_activeStoneId != 0 && _objectCache[_activeStoneId] == null)
            {
                _host.Log($"[RynthAi] ManaStone: use-on-player completion detected (stone destroyed).");
                Reset();
            }
        }

        // Check the action timeout BEFORE the busy-count gate. busyCount can get
        // stuck > 0 (a server-side decrement that never lands), and if we waited
        // for it we'd never recover. Without this, a single stuck use-on-player
        // blocks the entire mana stone subsystem indefinitely.
        if (_state != TapState.Idle && now - _actionIssuedAt > ActionTimeoutMs)
        {
            _host.Log($"[RynthAi] ManaStone: action timeout in state {_state} after {now - _actionIssuedAt}ms (stone=0x{(uint)_activeStoneId:X8} item=0x{(uint)_activeItemId:X8}), resetting.");

            // If a use-on-player keeps timing out with the same stone, blacklist
            // the stone so subsequent ticks don't pick it again. Otherwise we'd
            // re-select it every tick and never get to Priority 2 (drain looted).
            if (_state == TapState.UsingOnPlayer && _activeStoneId != 0)
            {
                _stoneUseOnPlayerCooldownUntil[_activeStoneId] = now + StoneUseOnPlayerCooldownMs;
                _host.Log($"[RynthAi] ManaStone: blacklisting stone 0x{(uint)_activeStoneId:X8} for use-on-player ({StoneUseOnPlayerCooldownMs / 1000}s cooldown).");
            }
            // For the tap path, the STONE is the more likely culprit when an
            // action silently fails — AC won't drain into a charged stone, so
            // a "looks empty in cache" stone that's really charged keeps
            // failing on every tap target. Park the stone, not the item.
            if (_state == TapState.TappingItem && _activeStoneId != 0)
            {
                _stoneTapCooldownUntil[_activeStoneId] = now + StoneTapCooldownMs;
                _host.Log($"[RynthAi] ManaStone: blacklisting stone 0x{(uint)_activeStoneId:X8} for tap ({StoneTapCooldownMs / 1000}s cooldown).");
            }
            Reset();
            return; // let things settle before starting a new action this tick
        }

        if (!_host.HasUseObjectOn) return;
        if (busyCount > 0) return;

        if (now - _lastThinkAt < ThinkIntervalMs) return;
        _lastThinkAt = now;

        if (_state == TapState.Idle)
            TryBeginAction();
    }

    public void OnChatWindowText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_state == TapState.TappingItem)
        {
            // "The Mana Stone drains X points of mana from the ..." — single drain,
            // item is destroyed, stone is now charged. Back to idle so the next think
            // tick can decide whether worn gear needs the charge yet.
            if (text.Contains("The Mana Stone drains")
                || text.Contains("is destroyed")
                || text.Contains("has been destroyed"))
            {
                GoIdle();
                return;
            }

            // "The X is already full of mana." — AC's response when we used a
            // CHARGED stone on a non-equipped item (or a fully-loaded item). Our
            // cache mis-read this stone as empty. Blacklist it for the tap path
            // immediately so the next think tick picks a different stone.
            if (text.Contains("is already full of mana") && _activeStoneId != 0)
            {
                _stoneKnownCharged.Add(_activeStoneId);
                _stoneTapCooldownUntil.Remove(_activeStoneId); // supersede cooldown
                _host.Log($"[RynthAi] ManaStone: stone 0x{(uint)_activeStoneId:X8} is actually charged (server: 'already full') — marking known-charged.");
                GoIdle();
                return;
            }
            return;
        }

        if (_state == TapState.UsingOnPlayer)
        {
            // "The Mana Stone gives X points of mana to ..." — transfer complete.
            // The stone may also have been destroyed in the process; either way
            // reset and let the next tick pick a fresh stone.
            if (text.Contains("The Mana Stone gives")
                || text.Contains("The Mana Stone is destroyed"))
            {
                Reset();
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void TryBeginAction()
    {
        int threshold = _settings.ManaTapMinMana;

        // Priority 1 (was 2): drain a tappable looted item into an empty stone.
        // This wins over refilling worn gear because:
        //   * draining is the user's primary goal (loot-for-tap behavior),
        //   * the use-on-player path only accepts certain equipment slots and
        //     can loop forever on items like wielded weapons that AC won't
        //     refill via stone — that loop blacklists stones one by one and
        //     starves the drain path entirely.
        int stoneId  = FindEmptyStone();
        int targetId = stoneId != 0 ? FindTapTarget(threshold) : 0;

        if (stoneId != 0 && targetId != 0)
        {
            _activeStoneId = stoneId;
            _activeItemId  = targetId;
            _host.Log($"[RynthAi] ManaStone: draining 0x{(uint)targetId:X8} with stone 0x{(uint)stoneId:X8}");
            IssueTapItem(stoneId, targetId);
            return;
        }

        // Priority 2 (was 1): refill worn gear with a charged stone, but only
        // when there is nothing to drain right now.
        if (WornEquipmentNeedsMana(threshold))
        {
            int chargedStone = FindChargedStone();
            if (chargedStone != 0)
            {
                _activeStoneId = chargedStone;
                _activeItemId  = 0;
                IssueUseOnPlayer();
                return;
            }
        }

    }

    private void IssueTapItem(int stoneId, int itemId)
    {
        _state           = TapState.TappingItem;
        _actionIssuedAt  = NowMs;
        _host.UseObjectOn(unchecked((uint)stoneId), unchecked((uint)itemId));
    }

    private void IssueUseOnPlayer()
    {
        uint playerId = _host.GetPlayerId();
        if (playerId == 0) { Reset(); return; }

        // Confirm the stone still exists in inventory
        if (_objectCache[_activeStoneId] == null) { Reset(); return; }

        _host.Log($"[RynthAi] ManaStone: using charged stone 0x{(uint)_activeStoneId:X8} on player");
        _state          = TapState.UsingOnPlayer;
        _actionIssuedAt = NowMs;
        _host.UseObjectOn(unchecked((uint)_activeStoneId), playerId);
    }

    private void GoIdle()
    {
        _state          = TapState.Idle;
        _actionIssuedAt = 0;
    }

    private void Reset()
    {
        _state          = TapState.Idle;
        _activeStoneId  = 0;
        _activeItemId   = 0;
        _actionIssuedAt = 0;
    }

    /// <summary>
    /// True if at least one configured mana stone in the player's inventory has zero charge.
    /// CorpseOpenController uses this to decide whether to loot mana-bearing items for draining.
    /// </summary>
    public bool HasEmptyManaStone() => FindEmptyStone() != 0;

    /// <summary>
    /// Number of empty (zero-charge) configured mana stones currently in inventory.
    /// </summary>
    public int CountEmptyStones()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;
        int count = 0;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (!IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;
            if (_stoneKnownCharged.Contains(item.Id)) continue;

            bool readMana = _host.TryGetObjectIntProperty(unchecked((uint)item.Id),
                                                          (uint)AcIntProperty.CurrentMana, out int cur);
            if (readMana && cur == 0)
                _stoneTapCooldownUntil.Remove(item.Id);

            if (_stoneTapCooldownUntil.TryGetValue(item.Id, out long until) && now < until) continue;
            if (readMana && cur > 0) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Items already in the player's inventory that match the tap-target criteria
    /// (mana ≥ threshold, not equipped, not loot-kept, not blacklisted). Each one
    /// will eventually consume one empty stone, so they count against the
    /// "free slots" quota that CorpseOpenController uses for ManaTap pickup.
    /// </summary>
    public int CountPendingDrainItems()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int threshold = _settings.ManaTapMinMana;
        if (threshold <= 0) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;
        int count = 0;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;
            if (_tapTargetCooldownUntil.TryGetValue(item.Id, out long until) && now < until) continue;
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                && wield != 0) continue;
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana)
                || curMana < threshold) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Free empty-stone slots = empty stones in inventory minus drain candidates
    /// already in inventory waiting to be tapped. CorpseOpenController only
    /// looks for new ManaTap pickups when this is &gt; 0.
    /// </summary>
    public int FreeEmptyStoneSlots() => Math.Max(0, CountEmptyStones() - CountPendingDrainItems());

    /// <summary>True only when the item's name matches a configured ManaStone consumable.</summary>
    private bool IsManaStone(WorldObject item)
    {
        foreach (var rule in _settings.ConsumableRules)
        {
            if (!rule.Type.Equals("ManaStone", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(rule.Name)) continue;
            if (rule.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>True if the item's container chain roots at the player's pack.</summary>
    private bool IsOwnedByPlayer(WorldObject item, int playerId)
    {
        if (item.ObjectClass == AcObjectClass.Corpse) return false;
        if (item.Wielder != 0 && playerId != 0 && item.Wielder != playerId) return false;
        if (item.Container != 0 && playerId != 0 && item.Container != playerId)
        {
            var owner = _objectCache[item.Container];
            if (owner == null || owner.ObjectClass == AcObjectClass.Corpse) return false;
            if (owner.Wielder != playerId && owner.Container != playerId) return false;
        }
        return true;
    }

    /// <summary>Find a stone with no charge (CurrentMana == 0) — only those can drain an item.</summary>
    private int FindEmptyStone()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (!IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            // Stones AC has explicitly told us are charged ("already full of
            // mana") are never empty until they're consumed/destroyed.
            if (_stoneKnownCharged.Contains(item.Id)) continue;

            bool readMana = _host.TryGetObjectIntProperty(unchecked((uint)item.Id),
                                                          (uint)AcIntProperty.CurrentMana, out int curMana);

            // If we can read CurrentMana and it's truly 0, the stone IS empty —
            // clear any stale blacklist entry. This makes the cooldown self-heal
            // once the cache catches up to the actual state.
            if (readMana && curMana == 0)
                _stoneTapCooldownUntil.Remove(item.Id);

            // Skip stones that recently failed a tap so we don't loop on the
            // same broken pair. The auto-clear above lets a stone return as
            // soon as its real state is known.
            if (_stoneTapCooldownUntil.TryGetValue(item.Id, out long until) && now < until)
                continue;

            // Treat the stone as empty if CurrentMana is 0 OR unreadable.
            // Unreadable happens for stones whose CBaseQualities InqInt isn't
            // populated yet (cache race, partial classification). Being too
            // strict here makes us blind to real empty stones; the stone-tap
            // blacklist handles the misfires when an "empty-looking" stone
            // is actually charged server-side.
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int cur)
                && cur > 0)
                continue;

            return item.Id;
        }

        return 0;
    }

    /// <summary>Find the most-charged mana stone in inventory (any positive charge).</summary>
    private int FindChargedStone()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;

        int bestId   = 0;
        int bestMana = 0;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (!IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            // Skip stones that timed out on use-on-player recently.
            if (_stoneUseOnPlayerCooldownUntil.TryGetValue(item.Id, out long until) && now < until)
                continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int cur))
                continue;
            if (cur <= 0) continue;

            if (cur > bestMana) { bestMana = cur; bestId = item.Id; }
        }

        return bestId;
    }

    private int FindTapTarget(int threshold)
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            // Skip items that recently failed to drain.
            if (_tapTargetCooldownUntil.TryGetValue(item.Id, out long until) && now < until)
                continue;

            // Skip equipped items
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                && wield != 0)
                continue;

            // Must currently hold at least the threshold worth of mana to be worth a drain
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana)
                || curMana < threshold)
                continue;

            return item.Id;
        }

        return 0;
    }

    /// <summary>
    /// True when at least one currently-worn item is BELOW its MaxMana AND
    /// its CurrentMana sits under the configured refill threshold. The
    /// MaxMana floor matters: an item with MaxMana=500 and CurrentMana=500
    /// is full — refilling it is impossible, so it must not register as
    /// "needs mana" or we get an infinite Priority-1 loop that blocks
    /// Priority-2 looted-item draining.
    /// </summary>
    private bool WornEquipmentNeedsMana(int threshold)
    {
        if (!_host.HasGetObjectIntProperty) return false;
        int playerId = unchecked((int)_host.GetPlayerId());

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                || wield == 0)
                continue;

            // Only items that actually consume mana matter
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.MaxMana, out int maxMana)
                || maxMana <= 0)
                continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana))
                continue;

            // Refill only when an item has dropped to a small fraction of its
            // MaxMana. The configured threshold is for *draining* loose items,
            // not for "the player's robe is at 88% of full" — using min(threshold,
            // maxMana) caused the bot to dump charged stones constantly on
            // near-full equipment.
            int floor = (int)(maxMana * WornRefillPctOfMax);
            if (curMana < floor)
                return true;
        }

        return false;
    }
}

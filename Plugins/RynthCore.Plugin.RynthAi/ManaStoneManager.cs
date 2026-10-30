using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Mana stone tapping state machine.
///
/// Each think tick from Idle picks one of two actions:
///   1. If any worn item has CurrentMana below the threshold AND we have a
///      charged stone in inventory, use the stone on the player.
///   2. Otherwise, if we have an empty stone and an item the looter picked up
///      as "ManaTap" (looted only to be drained) still holds at least the
///      threshold of CurrentMana, drain it. Nothing else is ever drained.
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

    // Same idea for tap targets: an item whose drain never completed waits TapTargetRetryMs
    // before it is tried again (with the next stone).
    private readonly Dictionary<int, long> _tapTargetCooldownUntil = new();

    // And for the stone half of the pair: a "stone" that AC silently refuses
    // to use (often because it's actually charged but our cache reads its
    // CurrentMana as 0/unreadable) gets parked so FindEmptyStone moves on.
    // Short cooldown — most failures are transient (server lag, mid-action),
    // and a genuinely-charged stone is filtered by the cur>0 check anyway.
    private readonly Dictionary<int, long> _stoneTapCooldownUntil = new();
    private const long StoneTapCooldownMs = 30 * 1000;

    // Stones we've positively confirmed are charged via AC chat ("is already
    // full of mana"). Distinct from the cooldown so the cache's stale CurrentMana
    // reading doesn't auto-clear them. Cleared when the stone gives its mana away
    // (see _stoneKnownEmpty below).
    private readonly HashSet<int> _stoneKnownCharged = new();

    // A stone's charge is read from its last appraisal, and the server never sends a change to it:
    // after a stone pours its mana into worn gear the read still shows the old charge, and after a
    // drain charges it the read still shows it empty (or can't be read at all). So what the server
    // said about a stone since then wins over the read:
    //   * _stoneKnownCharged (above): "drains N points" into it, "already full of mana", or "no items
    //     equipped that need mana" (only said for a charged stone). It refills worn gear even when its
    //     charge can't be read; it never could, so a charged stone whose charge wasn't readable was
    //     tried as an empty stone, called charged by the server, and then never used on the gear
    //     (Khanda-handled Mace at 0% for over an hour with five charged stones in the pack, 2026-10-10).
    //   * _stoneKnownEmpty: it gave its mana ("The Mana Stone gives ..."). Value = the read at that
    //     moment; a different read later is a newer appraisal and supersedes the mark. Without it the
    //     same, now empty, stone was picked again 600 ms later (the read still said charged), the
    //     server cancelled the use without a word, and it timed out and was parked for 5 minutes, stone
    //     after stone (2026-10-08).
    private readonly Dictionary<int, int?> _stoneKnownEmpty = new();
    // Stones whose charge can't be read are identified (at most every 5 minutes each) so a charged one
    // can refill gear with tapping off, where nothing else ever tells us it is charged.
    private readonly Dictionary<int, long> _stoneIdRequestedAt = new();
    private const long StoneIdRetryMs = 5 * 60 * 1000;

    // The server said the worn gear is full ("Your items are fully charged." / "You have no items
    // equipped that need mana."). Worn-item mana is read from appraisals too, so the gear can still read
    // low; no refill for a while instead of a stone every think tick.
    private long _refillHoldUntil;
    private const long RefillHoldMs = 2 * 60 * 1000;
    private long _lastNoStoneDiagAt;

    // Worn gear's mana is read from its last appraisal, and ACE never sends a change to it while the
    // gear burns mana (Player.ManaConsumersTick), so a mace identified at 90% read 90% all night while
    // it ran dry, and nothing was refilled (Mana Charges in the pack, 2026-10-11). Two fixes:
    //   * "Your X is low on Mana." / "Your X is out of Mana." (the server's own word) means the gear
    //     needs mana whatever the reads say, until the server says it's full again.
    //   * worn items are identified again every 10 minutes (one every 5 seconds at most), and right
    //     after a stone gave its mana, so the reads follow the gear.
    private long _serverSaidGearLowAt;
    private readonly Dictionary<int, long> _gearReadRefreshedAt = new();
    private long _lastGearRefreshAt;
    private const long GearRefreshMs = 10 * 60 * 1000;
    private const long GearRefreshSpacingMs = 5000;

    private const uint StackSizeKey = 12;   // STACK_SIZE (the client's live copy)
    private long _stoneMarksPrunedAt;

    // Worn equipment is only treated as "needs refill" when current mana drops
    // below this fraction of its MaxMana. Prevents the bot from continually
    // dumping stones on the player just because an item dipped slightly.
    private const double WornRefillPctOfMax = 0.25;
    // With an Eternal Mana Charge (never used up) there's no reason to wait for 25%: top
    // gear up below 75% (Lucy carried one on her Items list and it was never used, 2026-09-30).
    private const double WornRefillPctOfMaxEternal = 0.75;
    private long _lastGearDiagAt;
    private readonly Dictionary<int, long> _manaIdRequestedAt = new();

    private const long ThinkIntervalMs  = 600;
    private const long ActionTimeoutMs  = 8000;

    /// <summary>Clock (ms); tests replace it.</summary>
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;
    private static long NowMs => Clock();

    /// <summary>
    /// Set by the plugin: true when a Keep or Keep # rule in the active loot profile
    /// matches the item. Draining destroys the item, so kept loot is never a tap target.
    /// </summary>
    public Func<WorldObject, bool>? IsLootKept { get; set; }

    /// <summary>
    /// Set by the plugin (RynthAiPlugin.FightIsOn): true while a fight is on. Draining loot into an
    /// empty stone is optional work and waits for it to end: every UseObjectOn cancels the move-to
    /// chains and bumps the busy count, and between melee swings it held the next swing (a swing
    /// repeat is not a paced action). Refilling worn gear that runs low still goes ahead: armor
    /// with no mana drops its spells mid-fight.
    /// </summary>
    public Func<bool>? FightIsOn { get; set; }
    private bool _drainHeldForFight;

    // Items the looter picked up as "ManaTap" (looted only to be drained), with the
    // time each was recorded. Draining destroys the item, so these are the ONLY tap
    // targets: gear, quest items, bought items and loot kept by a rule are never in
    // here. Session memory only: a hot reload or relog builds a new manager and the
    // set starts empty, so anything looted for tapping before that just stays in
    // the pack undrained (the safe direction).
    private readonly Dictionary<int, long> _lootedForTap = new();
    // An entry is not pruned for "not in the pack" this soon after it was recorded:
    // the pickup is recorded when it is sent, before the item arrives.
    private const long LootedForTapGraceMs = 30_000;
    private const long LootedForTapPruneIntervalMs = 1_000;
    private long _lootedForTapPrunedAt;

    // When each tap item's pickup was confirmed (it is in the pack). A drain is only sent for an
    // item that has arrived: one went out 30 ms before its pickup was confirmed (2026-10-15) and
    // the server never answered it. An item whose confirm was lost counts as arrived once it is
    // owned and TapArrivalFallbackMs have passed since the pickup was sent.
    private readonly Dictionary<int, long> _tapArrivedAt = new();
    private const long TapArrivalSettleMs = 500;
    private const long TapArrivalFallbackMs = 5_000;

    // Drains that never completed, per item and per stone. An item gets TapTargetMaxTries (each
    // with the next stone), then it is given up: it stays in the pack as ordinary loot. A stone
    // that failed twice is parked for StoneTapLongCooldownMs. Before this a failed item was
    // shelved for 5 minutes and stopped counting against the stones, so the looter took another
    // item in its place, and the pack filled with undrained tap items (9 in 10 drains timed out).
    private readonly Dictionary<int, int> _tapTargetFailures = new();
    private readonly Dictionary<int, int> _stoneTapFailures = new();
    private const int TapTargetMaxTries = 3;
    private const long TapTargetRetryMs = 20_000;
    private const long StoneTapLongCooldownMs = 30 * 60 * 1000;

    // The server's lines while a drain is out, logged so a drain that never completes shows why.
    private int _tapChatLogged;
    private const int TapChatLogMax = 4;

    // HoldsLootForDrain's answer, worked out at most every HoldCheckMs (the finders walk the cache).
    private bool _holdLoot;
    private long _holdLootAt = -HoldCheckMs;
    private const long HoldCheckMs = 250;

    public ManaStoneManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache objectCache)
    {
        _host        = host;
        _settings    = settings;
        _objectCache = objectCache;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void OnHeartbeat(int busyCount)
    {
        // Before the early returns: items sold, given away or dropped while tapping is
        // off or the macro is stopped must leave the set too.
        PruneLootedForTap(NowMs);
        PruneStoneMarks(NowMs);

        // Refilling worn gear from a charged stone (or a free Eternal charge) works with tapping
        // off too; only draining loot into empty stones needs tapping on. It used to need tapping
        // on for any refill, so charged stones and bought Mana Charges were never used with it
        // off (2026-10-08).
        if (!_settings.EnableManaTapping && !HasChargedRefillStone()) { Reset(); return; }
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
                if (itemGone) ForgetTapItem(_activeItemId); // drained and destroyed
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
                int stoneFails = _stoneTapFailures.TryGetValue(_activeStoneId, out int sf) ? sf + 1 : 1;
                _stoneTapFailures[_activeStoneId] = stoneFails;
                long park = stoneFails >= 2 ? StoneTapLongCooldownMs : StoneTapCooldownMs;
                _stoneTapCooldownUntil[_activeStoneId] = now + park;
                _host.Log($"[RynthAi] ManaStone: blacklisting stone 0x{(uint)_activeStoneId:X8} for tap ({park / 1000}s cooldown, {stoneFails} failed drain(s)).");
            }
            // ALSO shelve the TARGET item. A target that never completes a drain
            // (server can't resolve the stone guid, item is retained/undrainable,
            // it's a casting wand, etc.) is otherwise re-paired with the next
            // empty stone on the very next think tick and loops forever — and
            // every attempt issues UseObjectOn, which cancels the player's
            // move-to chains and bumps m_cBusy, locking the character (and the
            // bot's own casts) out of movement/combat. Shelving only the stone
            // (above) does NOT break this loop because the WAND keeps getting
            // selected. See [rynthai_respawn_blind_wedge] / 2026-06-22 wedge.
            // It keeps its place against the stones meanwhile (FreeEmptyStoneSlots), so no other
            // item is looted to tap while it waits; after TapTargetMaxTries it is given up.
            if (_state == TapState.TappingItem && _activeItemId != 0)
            {
                int fails = _tapTargetFailures.TryGetValue(_activeItemId, out int f) ? f + 1 : 1;
                _tapTargetFailures[_activeItemId] = fails;
                if (fails >= TapTargetMaxTries)
                    GiveUpTapTarget(_activeItemId, fails);
                else
                {
                    _tapTargetCooldownUntil[_activeItemId] = now + TapTargetRetryMs;
                    _host.Log($"[RynthAi] ManaStone: tap target 0x{(uint)_activeItemId:X8} did not drain (try {fails} of {TapTargetMaxTries}); trying again in {TapTargetRetryMs / 1000}s.");
                }
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

        // In any state. ACE: "Your {item} is low on Mana." (about two minutes left) and "Your {item} is
        // out of Mana." - worn gear needs mana now, whatever its (appraisal) read says.
        if (text.StartsWith("Your ", StringComparison.Ordinal)
            && (text.Contains(" is low on Mana", StringComparison.OrdinalIgnoreCase)
                || text.Contains(" is out of Mana", StringComparison.OrdinalIgnoreCase)))
        {
            if (_serverSaidGearLowAt == 0)
                _host.Log($"[RynthAi] ManaStone: the server says worn gear is low ('{text.Trim()}') - refilling whatever the gear's mana reads say.");
            _serverSaidGearLowAt = NowMs;
            _refillHoldUntil = 0;   // the server's word is newer than "fully charged"
            return;
        }
        // The stone-on-player reply's second line; it may arrive as a line of its own.
        if (text.Contains("Your items are fully charged"))
        {
            _serverSaidGearLowAt = 0;
            HoldRefills("the server says the worn items are fully charged");
        }
        if (text.Contains("no items equipped that need mana"))
            _serverSaidGearLowAt = 0;

        if (_state == TapState.TappingItem)
        {
            // "The Mana Stone drains X points of mana from the ..." — single drain,
            // item is destroyed, stone is now charged. Back to idle so the next think
            // tick can decide whether worn gear needs the charge yet.
            if (text.Contains("The Mana Stone drains")
                || text.Contains("is destroyed")
                || text.Contains("has been destroyed"))
            {
                // The stone is charged now; its read still shows it empty until it is appraised again.
                if (text.Contains("The Mana Stone drains"))
                {
                    MarkStoneCharged(_activeStoneId);
                    _stoneTapFailures.Remove(_activeStoneId);
                    _host.Log($"[RynthAi] ManaStone: drained 0x{(uint)_activeItemId:X8} into stone 0x{(uint)_activeStoneId:X8}.");
                }
                if (_activeItemId != 0) ForgetTapItem(_activeItemId); // drained and destroyed
                GoIdle();
                return;
            }

            // "The Mana Stone gives N points of mana to the X." - the stone was charged after all and
            // poured its mana into the item (ACE does that for a charged stone used on an item that
            // isn't full). The stone is empty now.
            if (text.Contains("The Mana Stone gives"))
            {
                _host.Log($"[RynthAi] ManaStone: stone 0x{(uint)_activeStoneId:X8} was charged and gave its mana to the item instead - it is empty now.");
                MarkStoneEmpty(_activeStoneId);
                GoIdle();
                return;
            }

            // "The X is already full of mana." — AC's response when we used a
            // CHARGED stone on a non-equipped item (or a fully-loaded item). Our
            // cache mis-read this stone as empty. Blacklist it for the tap path
            // immediately so the next think tick picks a different stone.
            if (text.Contains("is already full of mana") && _activeStoneId != 0)
            {
                MarkStoneCharged(_activeStoneId);
                _stoneTapCooldownUntil.Remove(_activeStoneId); // supersede cooldown
                _host.Log($"[RynthAi] ManaStone: stone 0x{(uint)_activeStoneId:X8} is actually charged (server: 'already full') — marking known-charged.");
                GoIdle();
                return;
            }
            if (_tapChatLogged < TapChatLogMax)
            {
                _tapChatLogged++;
                _host.Log($"[RynthAi] ManaStone: while draining 0x{(uint)_activeItemId:X8} with 0x{(uint)_activeStoneId:X8} the server said '{text.Trim()}'.");
            }
            return;
        }

        if (_state == TapState.UsingOnPlayer)
        {
            // "The Mana Stone gives X points of mana to ..." — transfer complete.
            // The stone may also have been destroyed in the process; either way
            // reset and let the next tick pick a fresh stone.
            if (text.Contains("The Mana Stone gives"))
            {
                // The stone gave its mana and is empty now (an Eternal charge refills itself), though
                // its read still shows the old charge.
                WorldObject? stone = _activeStoneId != 0 ? _objectCache[_activeStoneId] : null;
                if (stone == null || !IsEternal(stone)) MarkStoneEmpty(_activeStoneId);
                // ("Your items are fully charged" was handled above.) The gear's reads are from before
                // the refill: identify the worn items again.
                _gearReadRefreshedAt.Clear();
                Reset();
                return;
            }
            if (text.Contains("The Mana Stone is destroyed"))
            {
                Reset();
                return;
            }
            // ACE says this only for a charged stone: the stone keeps its charge, the gear is full.
            if (text.Contains("no items equipped that need mana"))
            {
                MarkStoneCharged(_activeStoneId);
                HoldRefills("the server says no worn item needs mana");
                Reset();
            }
        }
    }

    private void HoldRefills(string why)
    {
        _refillHoldUntil = NowMs + RefillHoldMs;
        _host.Log($"[RynthAi] ManaStone: no worn-gear refill for {RefillHoldMs / 1000}s ({why}; the gear's mana reads are from its last appraisal).");
    }

    private int? ReadStoneMana(int id) =>
        _host.TryGetObjectIntProperty(unchecked((uint)id), (uint)AcIntProperty.CurrentMana, out int cur) ? cur : null;

    /// <summary>The server showed this stone empty since its read was taken (and no newer read came in).</summary>
    private bool IsKnownEmpty(int id, int? read)
    {
        if (!_stoneKnownEmpty.TryGetValue(id, out int? atMark)) return false;
        if (read != atMark) { _stoneKnownEmpty.Remove(id); return false; }   // a newer appraisal: trust it
        return true;
    }

    private void MarkStoneEmpty(int id)
    {
        if (id == 0) return;
        // A stack of stones or charges gave one item's mana; the rest of the stack is still charged
        // and keeps its id (retail Mana Charges don't stack and are destroyed by every use, but a
        // stacked one must not be written off after its first refill).
        if (_host.TryGetObjectIntProperty(unchecked((uint)id), StackSizeKey, out int stack) && stack > 1)
        {
            _host.Log($"[RynthAi] ManaStone: stone 0x{(uint)id:X8} gave its mana, but it is a stack of {stack} - still a source.");
            return;
        }
        _stoneKnownCharged.Remove(id);
        _stoneKnownEmpty[id] = ReadStoneMana(id);
    }

    /// <summary>
    /// Forgets what the server said about stones that left the pack (used up, destroyed, sold). AC
    /// reuses object ids, so a stale "gave its mana" mark could otherwise write off a new charge.
    /// </summary>
    private void PruneStoneMarks(long now)
    {
        if (now - _stoneMarksPrunedAt < 5000) return;
        _stoneMarksPrunedAt = now;
        if (_stoneKnownEmpty.Count == 0 && _stoneKnownCharged.Count == 0) return;
        List<int>? gone = null;
        foreach (int id in _stoneKnownEmpty.Keys)
            if (id != _activeStoneId && _objectCache[id] == null) (gone ??= new List<int>()).Add(id);
        foreach (int id in _stoneKnownCharged)
            if (id != _activeStoneId && _objectCache[id] == null) (gone ??= new List<int>()).Add(id);
        if (gone == null) return;
        foreach (int id in gone)
        {
            _stoneKnownEmpty.Remove(id);
            _stoneKnownCharged.Remove(id);
        }
    }

    private void MarkStoneCharged(int id)
    {
        if (id == 0) return;
        _stoneKnownEmpty.Remove(id);
        _stoneKnownCharged.Add(id);
    }

    /// <summary>
    /// CorpseOpenController calls this when it picks an item up as "ManaTap" (no loot
    /// rule matched, it holds enough mana and an empty stone is free): when the pickup
    /// is sent, and again when it is confirmed. Only these items are ever drained.
    /// </summary>
    /// <param name="arrived">The pickup is confirmed: the item is in the pack (false: just sent).</param>
    public void MarkLootedForTap(int itemId, bool arrived = true)
    {
        if (itemId == 0) return;
        _lootedForTap[itemId] = NowMs;
        if (arrived) _tapArrivedAt[itemId] = NowMs;
    }

    private void ForgetTapItem(int itemId)
    {
        _lootedForTap.Remove(itemId);
        _tapArrivedAt.Remove(itemId);
        _tapTargetFailures.Remove(itemId);
        _tapTargetCooldownUntil.Remove(itemId);
    }

    /// <summary>Drains of this item never completed: it stays in the pack as ordinary loot.</summary>
    private void GiveUpTapTarget(int itemId, int tries)
    {
        string name = _objectCache[itemId]?.Name ?? $"0x{(uint)itemId:X8}";
        _host.Log($"[RynthAi] ManaStone: giving up on tap target 0x{(uint)itemId:X8} '{name}' after {tries} drains that never completed.");
        _host.WriteToChat($"[RynthAi] Mana tapping: {name} would not drain after {tries} tries; it stays in your pack.", 1);
        ForgetTapItem(itemId);
    }

    /// <summary>The pickup is confirmed and has settled, or its confirm was lost and it has long since been sent.</summary>
    private bool TapItemArrived(int itemId, long now)
    {
        if (_tapArrivedAt.TryGetValue(itemId, out long at)) return now - at >= TapArrivalSettleMs;
        return _lootedForTap.TryGetValue(itemId, out long sent) && now - sent >= TapArrivalFallbackMs;
    }

    /// <summary>The looter picked this item up as "ManaTap" this session (it is waiting to be drained).</summary>
    public bool WasLootedForTap(int itemId) => _lootedForTap.ContainsKey(itemId);

    /// <summary>
    /// Drops entries whose item has left the pack: drained (destroyed), sold, given
    /// away, salvaged, dropped, or never arrived. Also drops items the player has
    /// since put on or added to the Weapons tab: that is the player choosing to keep
    /// it, so it stops being tap fodder for good.
    /// </summary>
    private void PruneLootedForTap(long now)
    {
        if (_lootedForTap.Count == 0) return;
        if (now - _lootedForTapPrunedAt < LootedForTapPruneIntervalMs) return;
        _lootedForTapPrunedAt = now;

        int playerId = unchecked((int)_host.GetPlayerId());
        List<int>? drop = null;
        foreach (var kv in _lootedForTap)
        {
            WorldObject? item = _objectCache[kv.Key];
            bool keptByPlayer = item != null
                && ((playerId != 0 && item.Wielder == playerId)
                    || WorldObjectCache.IsWieldedByPlayer(_host, item)
                    || _settings.ItemRules.Any(r => r.Id == item.Id));
            if (!keptByPlayer)
            {
                if (now - kv.Value < LootedForTapGraceMs) continue; // pickup may still be arriving
                if (item != null && IsOwnedByPlayer(item, playerId)) continue;
            }
            (drop ??= new List<int>()).Add(kv.Key);
        }
        if (drop == null) return;
        foreach (int id in drop)
            ForgetTapItem(id);
        _host.Log($"[RynthAi] ManaStone: {drop.Count} looted-for-tap item(s) left the pack or were kept by the player; {_lootedForTap.Count} left to drain.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void TryBeginAction()
    {
        int threshold = _settings.ManaTapMinMana;

        // Priority 1 (was 2): drain an item looted for tapping into an empty stone.
        // This wins over refilling worn gear because:
        //   * draining is the user's primary goal (loot-for-tap behavior),
        //   * the use-on-player path only accepts certain equipment slots and
        //     can loop forever on items like wielded weapons that AC won't
        //     refill via stone — that loop blacklists stones one by one and
        //     starves the drain path entirely.
        // Not in a fight (FightIsOn): the drain waits for it to end.
        bool drainWaits = DrainWaitsForFight();
        int stoneId  = _settings.EnableManaTapping && !drainWaits ? FindEmptyStone() : 0;
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
        if (WornEquipmentNeedsMana(threshold) && NowMs >= _refillHoldUntil)
        {
            int chargedStone = FindChargedStone();
            if (chargedStone != 0)
            {
                _activeStoneId = chargedStone;
                _activeItemId  = 0;
                IssueUseOnPlayer();
                return;
            }
            long now = NowMs;
            if (now - _lastNoStoneDiagAt > 60_000)
            {
                _lastNoStoneDiagAt = now;
                _host.Log($"[RynthAi] ManaStone: worn gear needs mana but no charged stone is known ({_stoneKnownCharged.Count} known charged, {_stoneKnownEmpty.Count} known empty).");
            }
        }
    }

    /// <summary>
    /// <see cref="FightIsOn"/> says a fight is on: drains wait. One log line per fight while an
    /// item looted to tap is waiting.
    /// </summary>
    private bool DrainWaitsForFight()
    {
        bool fight;
        try { fight = FightIsOn?.Invoke() == true; }
        catch { fight = false; }
        if (!fight)
        {
            _drainHeldForFight = false;
            return false;
        }
        if (!_drainHeldForFight && _lootedForTap.Count > 0)
        {
            _drainHeldForFight = true;
            _host.Log("[RynthAi] ManaStone: a fight is on - draining loot into a stone waits for it to end.");
        }
        return true;
    }

    private void IssueTapItem(int stoneId, int itemId)
    {
        _tapChatLogged   = 0;
        _state           = TapState.TappingItem;
        _actionIssuedAt  = NowMs;
        _host.UseOnFor(unchecked((uint)stoneId), unchecked((uint)itemId), "ManaStone", "tap an item into a mana stone");
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
        _host.UseOnFor(unchecked((uint)_activeStoneId), playerId, "ManaStone", "charged mana stone on yourself");
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
            if (!IsManaStone(item) || IsManaCharge(item)) continue;   // keep in sync with FindEmptyStone
            if (!IsOwnedByPlayer(item, playerId) || !InPack(item.Id)) continue;
            if (_stoneKnownCharged.Contains(item.Id)) continue;

            int? read = ReadStoneMana(item.Id);
            bool knownEmpty = IsKnownEmpty(item.Id, read);   // keep in sync with FindEmptyStone
            if (StoneParkedForTap(item.Id, knownEmpty, read, now)) continue;
            if (!knownEmpty && read > 0) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Items a drain must never touch (the drain destroys the item): gear the player
    /// is wearing or wielding, weapons and wands on the Weapons tab (the bow sits in
    /// the pack while the wand buffs), and loot the profile keeps. Stype 10 can read 0
    /// for a wielded item, so worn gear is also checked by its wielder.
    /// </summary>
    private bool IsProtectedFromTap(WorldObject item, int playerId)
    {
        if (playerId != 0 && item.Wielder == playerId) return true;
        if (WorldObjectCache.IsWieldedByPlayer(_host, item)) return true;
        foreach (var rule in _settings.ItemRules)
            if (rule.Id == item.Id) return true;
        var kept = IsLootKept;
        if (kept != null)
        {
            try { if (kept(item)) return true; }
            catch { return true; }
        }
        return false;
    }

    /// <summary>
    /// How many items the looter may pick up to tap: 1 when an empty stone is in the pack and
    /// no item looted to tap is waiting for its drain, else 0. One at a time (2026-10-15): the
    /// stone first, then one item, drained before the next is looted. An item whose drain is
    /// retrying still holds its place (it used to stop counting once shelved, and the looter
    /// took another in its place). CorpseOpenController only looks for new ManaTap pickups when
    /// this is &gt; 0.
    /// </summary>
    public int FreeEmptyStoneSlots()
    {
        if (_lootedForTap.Count > 0) return 0;
        return CountEmptyStones() > 0 ? 1 : 0;
    }

    /// <summary>
    /// True while a drain is out, or an item looted to tap is in the pack with an empty stone
    /// ready for it: the looter waits (no next item, no next corpse) so the drain goes first.
    /// An item waiting to retry a failed drain does not hold the looter.
    /// </summary>
    public bool HoldsLootForDrain()
    {
        if (_state == TapState.TappingItem) return true;
        if (_lootedForTap.Count == 0) return false;
        if (!_settings.EnableManaTapping || !_settings.IsMacroRunning || !_host.HasUseObjectOn) return false;
        // The drain waits for the fight to end, so it holds nothing meanwhile. No second item is
        // looted to tap: FreeEmptyStoneSlots is 0 while this one waits.
        if (DrainWaitsForFight()) return false;
        long now = NowMs;
        if (now - _holdLootAt < HoldCheckMs) return _holdLoot;
        _holdLootAt = now;
        _holdLoot = FindEmptyStone() != 0 && FindTapTarget(_settings.ManaTapMinMana) != 0;
        return _holdLoot;
    }

    /// <summary>
    /// True when the item is a mana stone.
    ///   * If the user configured one or more ManaStone consumable rules, only
    ///     stones whose name matches a rule qualify (explicit opt-in to specific
    ///     stones — the user narrowed it on purpose).
    ///   * If there are no ManaStone rules and tapping is enabled, an item
    ///     qualifies if it's classified ObjectClass.ManaStone OR named
    ///     "Mana Stone" — so looting and tapping work with zero configuration.
    ///     The name fallback is load-bearing: items still on a corpse are
    ///     usually unappraised, AC hasn't sent ITEM_TYPE, so the cache can only
    ///     classify them Unknown. Name survives where ITEM_TYPE doesn't.
    /// CorpseOpenController shares this predicate for both the loot gate and the
    /// KeepCount inventory tally — they must agree or the cap stops working.
    /// </summary>
    public bool IsManaStone(WorldObject item)
    {
        if (IsEternal(item)) return true;
        bool hasRule = false;
        foreach (var rule in _settings.ConsumableRules)
        {
            if (!IsStoneRule(rule)) continue;
            hasRule = true;
            if (rule.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (hasRule) return false;                      // explicit rules → match by name only
        if (!_settings.EnableManaTapping) return false; // no rules → auto-detect only when tapping is on

        // Only the ordinary tapping stones and mana charges. Retail also has special stones
        // (Aligned, Aetheria, Unstable, Enchanted, Adjanite Mana Stone) that are not for
        // draining or recharging: an Aligned Mana Stone was used on a wand (2026-10-08). A
        // special stone is only touched when a ManaStone consumable rule names it.
        if (string.IsNullOrEmpty(item.Name))
            return item.ObjectClass == AcObjectClass.ManaStone;   // unnamed yet: the class decides (looting)
        return OrdinaryStoneNames.Contains(item.Name)
               || item.Name.Contains("Mana Charge", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A ManaStone line in Items that names a stone. A kit isn't one: "Greater Mana Kit" (a kit that
    /// restores your own mana) was typed ManaStone from the word "Mana" (Drakkon, 10-23). As a stone rule
    /// it switched off the ordinary stones (rules → named ones only), so his Greater and Major Mana Stones
    /// were never used and his shirt ran dry, and RynthAi tried to drain gear into the kit (refused).
    /// </summary>
    private bool IsStoneRule(ConsumableRule rule)
    {
        if (!rule.Type.Equals("ManaStone", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(rule.Name)) return false;
        if (!IsKitName(rule.Name)) return true;
        if (_kitRulesNoted.Add(rule.Name))
            _host.Log($"[RynthAi] ManaStone: '{rule.Name}' is in Items as a mana stone but is a kit: not used as a stone (Items: change its type).");
        return false;
    }
    private readonly HashSet<string> _kitRulesNoted = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsKitName(string name) => name.TrimEnd().EndsWith(" Kit", StringComparison.OrdinalIgnoreCase);

    /// <summary>The retail tapping stones (ACE world data names), smallest to largest.</summary>
    internal static readonly HashSet<string> OrdinaryStoneNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Minor Mana Stone", "Lesser Mana Stone", "Moderate Mana Stone", "Mana Stone",
        "Greater Mana Stone", "Major Mana Stone",
    };

    /// <summary>
    /// An unlimited mana charge (Eternal Mana Charge: 10,000 mana, never used up). Always
    /// "charged", never an empty stone to drain into, and preferred for refilling gear.
    /// </summary>
    private static bool IsEternal(WorldObject item) =>
        !string.IsNullOrEmpty(item.Name) && item.Name.StartsWith("Eternal Mana", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A bought Mana Charge (Tiny ... Massive, Titan, Superb; Eternal too): always charged, only ever
    /// a refill source. ACE gives every one a stored charge and destroys it on use (the Eternal one
    /// excepted), so it is never drained into.
    /// </summary>
    internal static bool IsManaCharge(WorldObject item) =>
        !string.IsNullOrEmpty(item.Name) && item.Name.Contains("Mana Charge", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A stone that may refill worn gear when charged: what <see cref="IsManaStone"/> takes, but
    /// with tapping off as well (no rules: the ordinary stones and mana charges; with ManaStone
    /// rules: the named ones).
    /// </summary>
    internal bool IsRefillStone(WorldObject item)
    {
        // Mana Charges are bought to refill gear: ManaStone rules (which pick the stones to loot and
        // drain into) don't turn them off.
        if (IsManaCharge(item)) return true;
        if (IsManaStone(item)) return true;
        if (_settings.EnableManaTapping || string.IsNullOrEmpty(item.Name)) return false;
        foreach (var rule in _settings.ConsumableRules)
            if (IsStoneRule(rule))
                return false;   // rules decide (IsManaStone already said no)
        return OrdinaryStoneNames.Contains(item.Name)
               || item.Name.Contains("Mana Charge", StringComparison.OrdinalIgnoreCase);
    }

    // FindChargedStone walks every known object; with tapping off it gates the whole tick, so
    // the answer is kept for a few seconds.
    private long _chargedCheckAt;
    private bool _chargedCached;
    private bool HasChargedRefillStone()
    {
        long now = NowMs;
        if (now - _chargedCheckAt < 5000) return _chargedCached;
        _chargedCheckAt = now;
        _chargedCached = HasEternalCharge() || FindChargedStone() != 0;
        return _chargedCached;
    }

    private bool HasEternalCharge()
    {
        int playerId = unchecked((int)_host.GetPlayerId());
        foreach (var item in _objectCache.GetDirectInventory(forceRefresh: false))
            if (IsEternal(item) && IsOwnedByPlayer(item, playerId)) return true;
        return false;
    }

    /// <summary>True if the item's container chain roots at the player's pack.</summary>
    // What the player owns, rebuilt at most every 600 ms: the pack walk (GetContainerContents,
    // what kits and potions already use) plus the cache's inventory set (wielded items too).
    // The per-object container/wielder fields below read 0 off AC's thread, so every item
    // looked like it lay on the ground: gear-check logged worn=0 and eternal=False for a
    // character wearing gear with an Eternal Mana Charge in the pack (2026-09-30).
    private readonly HashSet<int> _ownedIds = new();
    private long _ownedIdsAt;

    private HashSet<int> OwnedIds()
    {
        long now = NowMs;
        if (now - _ownedIdsAt < ThinkIntervalMs && _ownedIds.Count > 0) return _ownedIds;
        _ownedIdsAt = now;
        _ownedIds.Clear();
        foreach (var wo in _objectCache.GetDirectInventory(forceRefresh: false)) _ownedIds.Add(wo.Id);
        foreach (var wo in _objectCache.GetInventory()) _ownedIds.Add(wo.Id);
        return _ownedIds;
    }

    private bool IsOwnedByPlayer(WorldObject item, int playerId)
    {
        if (item.ObjectClass == AcObjectClass.Corpse) return false;
        if (OwnedIds().Contains(item.Id)) return true;
        if (item.Wielder != 0 && playerId != 0 && item.Wielder != playerId) return false;
        // In no container and wielded by nobody = lying on the ground. Those passed as
        // owned (the scans walk every known object), so a Mana Stone on a dungeon floor
        // became the "empty stone": the tap could never complete, timed out after 8 s
        // and shelved the real tap target for 5 minutes. Only when the engine can read
        // ownership; without it every object reads 0 and the old behaviour stays.
        if (item.Container == 0 && item.Wielder == 0 && playerId != 0 && _host.HasGetObjectOwnershipInfo)
            return false;
        if (item.Container != 0 && playerId != 0 && item.Container != playerId)
        {
            var owner = _objectCache[item.Container];
            if (owner == null || owner.ObjectClass == AcObjectClass.Corpse) return false;
            if (owner.Wielder != playerId && owner.Container != playerId) return false;
        }
        return true;
    }

    // The client's own pack walk (GetDirectInventory: GetContainerContents, plus worn gear),
    // rebuilt at most every ThinkIntervalMs. A stone, and an item to drain, must be in it: the
    // cache files every object without a world position as inventory (corpse contents too) and
    // keeps ids the client has since dropped, and both passed IsOwnedByPlayer. Drakkon drained
    // (2026-10-16) with a Major Mana Stone still on the corpse and with a stone the client no
    // longer knew ('?'): 12 drains, none completed, and the looter kept taking items for them.
    private readonly HashSet<int> _packIds = new();
    private long _packIdsAt = -ThinkIntervalMs;

    private bool InPack(int id)
    {
        if (!_host.HasGetContainerContents) return true;   // no walk: IsOwnedByPlayer decides alone
        long now = NowMs;
        if (now - _packIdsAt >= ThinkIntervalMs)
        {
            _packIdsAt = now;
            _packIds.Clear();
            foreach (var wo in _objectCache.GetDirectInventory(forceRefresh: false)) _packIds.Add(wo.Id);
        }
        return _packIds.Contains(id);
    }

    /// <summary>
    /// True while a stone sits out its tap cooldown after a drain that never completed. A stone
    /// that reads empty used to come straight back (the read "self-healed" the cooldown), so the
    /// 30-minute park after its second failure lasted one think tick: stone 0x801ACCDC failed 36
    /// drains in a row (2026-10-16). The short park after one failure still clears on an empty read.
    /// </summary>
    private bool StoneParkedForTap(int stoneId, bool knownEmpty, int? read, long now)
    {
        bool longPark = _stoneTapFailures.TryGetValue(stoneId, out int fails) && fails >= 2;
        if ((knownEmpty || read == 0) && !longPark)
            _stoneTapCooldownUntil.Remove(stoneId);
        return _stoneTapCooldownUntil.TryGetValue(stoneId, out long until) && now < until;
    }

    /// <summary>Find a stone with no charge (CurrentMana == 0) — only those can drain an item.</summary>
    private int FindEmptyStone()
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            // A Mana Charge is never an empty stone: it is sold charged and every use destroys it
            // (ACE ManaStoneDestroyChance 1.0). One whose charge wasn't read yet was taken for the
            // empty stone, and "draining" loot with it poured the whole charge into the loot item.
            if (!IsManaStone(item) || IsManaCharge(item)) continue;
            if (!IsOwnedByPlayer(item, playerId) || !InPack(item.Id)) continue;

            // Stones AC has explicitly told us are charged ("already full of
            // mana") are never empty until they're consumed/destroyed.
            if (_stoneKnownCharged.Contains(item.Id)) continue;

            int? read = ReadStoneMana(item.Id);
            // It gave its mana to worn gear: empty, whatever the (older) read says.
            bool knownEmpty = IsKnownEmpty(item.Id, read);

            // Skip stones that recently failed a tap so we don't loop on the same broken pair.
            if (StoneParkedForTap(item.Id, knownEmpty, read, now)) continue;

            // Treat the stone as empty if CurrentMana is 0 OR unreadable.
            // Unreadable happens for stones whose CBaseQualities InqInt isn't
            // populated yet (cache race, partial classification). Being too
            // strict here makes us blind to real empty stones; the stone-tap
            // blacklist handles the misfires when an "empty-looking" stone
            // is actually charged server-side.
            if (!knownEmpty && read > 0)
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
        bool requestedId = false;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (!IsRefillStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId) || !InPack(item.Id)) continue;

            // Skip stones that timed out on use-on-player recently.
            if (_stoneUseOnPlayerCooldownUntil.TryGetValue(item.Id, out long until) && now < until)
                continue;

            // Eternal: always charged (its mana often can't be read), never used up — first choice.
            if (IsEternal(item)) return item.Id;

            int? read = ReadStoneMana(item.Id);
            // It already gave its mana; the read is from before that.
            if (IsKnownEmpty(item.Id, read)) continue;

            int mana;
            if (read > 0) mana = read.Value;
            else if (_stoneKnownCharged.Contains(item.Id)) mana = 1;   // the server said charged; amount unknown
            else
            {
                if (read == null && !requestedId) requestedId = RequestStoneId(item.Id, now);
                continue;
            }

            if (mana > bestMana) { bestMana = mana; bestId = item.Id; }
        }

        return bestId;
    }

    /// <summary>
    /// Identifies a stone whose charge can't be read (at most every 5 minutes), so a charged one shows
    /// its charge. Not when it already has appraisal data: ACE leaves CurrentMana out for an empty
    /// stone, so an appraised stone with no readable charge is empty.
    /// </summary>
    private bool RequestStoneId(int id, long now)
    {
        if (!_host.HasRequestId) return false;
        if (_host.HasHasAppraisalData && _host.HasAppraisalData(unchecked((uint)id))) return false;
        if (_stoneIdRequestedAt.TryGetValue(id, out long at) && now - at < StoneIdRetryMs) return false;
        if (!_host.RequestId(unchecked((uint)id))) return false;   // refused (a death or a portal): asked again next pass, not in 5 minutes
        _stoneIdRequestedAt[id] = now;
        _host.Log($"[RynthAi] ManaStone: identifying stone 0x{(uint)id:X8} (its charge can't be read).");
        return true;
    }

    /// <summary>
    /// The next item to drain. Only items the looter picked up as "ManaTap" are
    /// candidates (the drain destroys the item); every check below is a second
    /// safety net on top of that.
    /// </summary>
    private int FindTapTarget(int threshold)
    {
        if (!_host.HasGetObjectIntProperty) return 0;
        if (_lootedForTap.Count == 0) return 0;
        int playerId = unchecked((int)_host.GetPlayerId());
        long now = NowMs;

        foreach (int id in _lootedForTap.Keys)
        {
            WorldObject? item = _objectCache[id];
            if (item == null) continue;
            if (IsManaStone(item)) continue;
            if (!TapItemArrived(id, now)) continue;   // its pickup is still on the way
            if (!IsOwnedByPlayer(item, playerId) || !InPack(item.Id)) continue;

            // Wands, staves and orbs drain like any other item (ACE's ManaStone only refuses
            // retained items). Only items looted for Mana Tap are candidates, and the wielded /
            // listed-item / loot-kept checks below protect the character's own casters.

            // Skip items that recently failed to drain.
            if (_tapTargetCooldownUntil.TryGetValue(item.Id, out long until) && now < until)
                continue;

            // Skip equipped items: worn per the tracker (a wield record can read 0 for worn gear), or
            // per the item's own slot.
            if (EquippedState.IsWielded(_host, item.Id) == true)
                continue;
            if (_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield)
                && wield != 0)
                continue;

            // Must currently hold at least the threshold worth of mana to be worth a drain
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana)
                || curMana < threshold)
                continue;

            // Worn gear (stype 10 can read 0), Weapons-tab items and kept loot.
            if (IsProtectedFromTap(item, playerId))
                continue;

            return item.Id;
        }

        return 0;
    }

    /// <summary>
    /// True for casting implements (wand/staff/orb). They drain like any other item;
    /// this is only a classifier. ObjectClass alone is unreliable for unappraised items,
    /// so we fall back to the name, matching CombatManager/BuffManager.IsWandObject.
    /// </summary>
    internal static bool IsWandLike(WorldObject item)
        => item.ObjectClass == AcObjectClass.WandStaffOrb || IsWandName(item.Name);

    private static bool IsWandName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("Orb",      StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Staff",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Wand",     StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Scepter",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Sceptre",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Baton",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Crozier",  StringComparison.OrdinalIgnoreCase) >= 0;
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
        bool eternal = HasEternalCharge();
        double pct = eternal ? WornRefillPctOfMaxEternal : WornRefillPctOfMax;
        int worn = 0, readable = 0, lowestPct = 101;
        string lowestName = "";
        bool needs = false;
        bool requestedThisPass = false;

        foreach (var item in _objectCache.AllKnownObjects())
        {
            if (IsManaStone(item)) continue;
            if (!IsOwnedByPlayer(item, playerId)) continue;

            // What is worn: the engine's tracker of the server's wield/move messages (API v87) when it
            // has one. An item's own wield record goes stale once it is moved to a pack: those counted
            // as worn ("worn=41" for a character wearing 20, 2026-10-20) and took the one appraisal a
            // pass, while gear really worn whose mana couldn't be read waited, skipped as unreadable.
            // Without a tracker: the live slot read, then the cache's copy (what /ra wielded uses).
            if (EquippedState.IsWielded(_host, item.Id) is bool tracked)
            {
                if (!tracked) continue;
            }
            else
            {
                if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.WieldedSlot, out int wield) || wield == 0)
                    wield = item.Values(LongValueKey.CurrentWieldedLocation, 0);
                if (wield == 0)
                    continue;
            }
            worn++;

            // Only items that actually consume mana matter. Off AC's thread an item's mana is
            // only known once it has been identified (appraisal cache); worn gear often never
            // is, so nothing ever read as low and no stone or charge was used. Identify one such
            // item per check, each at most every 5 minutes (a request bumps the busy count).
            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.MaxMana, out int maxMana))
            {
                long nowMs = NowMs;
                if (!requestedThisPass && _host.HasRequestId
                    && (!_manaIdRequestedAt.TryGetValue(item.Id, out long at) || nowMs - at > 300_000))
                {
                    if (_host.RequestId(unchecked((uint)item.Id))) _manaIdRequestedAt[item.Id] = nowMs;   // a refused one is asked again next pass
                    requestedThisPass = true;
                }
                continue;
            }
            if (maxMana <= 0)
                continue;

            if (!_host.TryGetObjectIntProperty(unchecked((uint)item.Id), (uint)AcIntProperty.CurrentMana, out int curMana))
                continue;
            readable++;
            _manaIdRequestedAt.Remove(item.Id);

            // The read is from the item's last appraisal and the server sends no change while the gear
            // burns mana: identify it again every 10 minutes (and soon after a refill), one item every
            // 5 seconds at most, so the read follows the gear.
            {
                long nowMs = NowMs;
                if (!requestedThisPass && _host.HasRequestId
                    && nowMs - _lastGearRefreshAt >= GearRefreshSpacingMs
                    && (!_gearReadRefreshedAt.TryGetValue(item.Id, out long readAt) || nowMs - readAt >= GearRefreshMs))
                {
                    if (_host.RequestId(unchecked((uint)item.Id)))   // a refused one is asked again next pass
                    {
                        _gearReadRefreshedAt[item.Id] = nowMs;
                        _lastGearRefreshAt = nowMs;
                    }
                    requestedThisPass = true;
                }
            }
            int p = (int)(curMana * 100L / maxMana);
            if (p < lowestPct) { lowestPct = p; lowestName = item.Name ?? ""; }

            // Refill only when an item has dropped to a fraction of its MaxMana (25%, or
            // 75% with an Eternal charge). The configured threshold is for *draining* loose
            // items, not for "the player's robe is at 88% of full" — using
            // min(threshold, maxMana) caused the bot to dump charged stones constantly on
            // near-full equipment.
            int floor = (int)(maxMana * pct);
            if (curMana < floor)
                needs = true;
        }

        // The server said an item is low or out of mana: that beats every read.
        bool serverLow = _serverSaidGearLowAt != 0;
        if (serverLow) needs = true;

        // Once a minute: what it can see, so "the charge is never used" can be told apart
        // from "the gear's mana can't be read".
        long now = NowMs;
        if (now - _lastGearDiagAt > 60_000)
        {
            _lastGearDiagAt = now;
            _host.Log($"[RynthAi] ManaStone: gear check - worn={worn} manaReadable={readable} lowest={(readable > 0 ? $"{lowestPct}% ({lowestName})" : "n/a")} " +
                      $"refillBelow={(int)(pct * 100)}% eternal={eternal} needs={needs} tapping={_settings.EnableManaTapping}" +
                      (serverLow ? $" serverSaidLow={(now - _serverSaidGearLowAt) / 1000}s ago" : "") +
                      (now < _refillHoldUntil ? $" hold={(_refillHoldUntil - now) / 1000}s" : ""));
        }

        return needs;
    }
}

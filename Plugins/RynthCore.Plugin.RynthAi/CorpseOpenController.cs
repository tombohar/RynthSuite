using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private readonly struct CorpseLootDecision
    {
        public bool ShouldLoot { get; init; }
        public string ModeName { get; init; }
        public string Reason { get; init; }
        public string KillerName { get; init; }
        public string PlayerName { get; init; }
        public string LongDesc { get; init; }
        public bool InFellowship { get; init; }
        public bool KillerIsFellow { get; init; }
        /// <summary>Your own death corpse, to be recovered whole (LootOwnCorpse; see MatchOwnDeathCorpse).</summary>
        public bool IsOwnDeathCorpse { get; init; }
    }

    // id → completed-at ms. Authoritative "looted, do not reopen" set. Aged out
    // by PruneCorpseCollections well past max AC corpse lifetime; deliberately
    // never cleared by a reopen, so AC re-announcing a still-open looted corpse
    // on a patrol loop cannot resurrect it.
    private readonly Dictionary<int, long> _completedCorpses = new();
    private readonly HashSet<int> _processedCorpseItems = new();
    private readonly Dictionary<int, int> _corpseItemAttempts = new();
    private readonly Dictionary<int, long> _corpseCooldownUntil = new();
    // Never-opened abandon tally per corpse: a timeout on a corpse we never got
    // open earns a retry cooldown (not the 30-min completed mark); after 3 such
    // abandons the corpse is treated as unreachable and completed for real.
    private readonly Dictionary<int, int> _corpseAbandonCounts = new();
    private const int NeverOpenedAbandonRetryMs = 30_000;
    private readonly HashSet<int> _ownershipSkipLogged = new();
    private readonly HashSet<int> _corpseIdRequested = new(); // corpses whose LongDesc ID request the engine ACCEPTED
    private readonly Dictionary<int, long> _corpseIdAskedAt = new(); // ActionPacer.NowMs of the newest accepted request, for the re-ask
    private readonly Dictionary<int, long> _corpseIdRefusedAt = new(); // ActionPacer.NowMs of the newest REFUSED request
    private const long CorpseIdRefusedRetryMs = 250;  // a refused request is retried no faster than this, per corpse
    private const int CorpseIdPerTick = 2;            // new accepted requests per scan tick (the refusal window ending must not burst)
    private int _corpseIdAcceptedThisTick;
    private long _corpseIdBudgetAt;   // when the budget last started over (a scan tick, or CorpseIdRefusedRetryMs without one)
    private const long CorpseIdReaskMs = 3000;   // no appraisal data this long after an accepted request: ask once more
    private const int CorpseIdMaxReasks = 3;     // ...at most this many times per corpse (each accepted ask raises the client's busy count and holds opens)
    private readonly Dictionary<int, int> _corpseIdAskCount = new(); // accepted requests per corpse, the first included
    private int _busyCount;
    private long _busyCountLastIncrementAt;
    private long _busyCountBecamePositiveAt; // when count first went 0→positive
    private const long BUSY_TIMEOUT_MS = 10_000; // safety: force-clear if stuck >10s
    private const long NameWaitMs = 3_000;       // extra wait past the assess window for a corpse item's name
    private const long AppraisalWaitMs = 3_000;  // extra wait past the assess window for an item's appraisal
    private const long ContentsSweepMs = 500;    // how often an open corpse's contents are checked for items the cache missed
    private long _lastContentsSweepAt;
    private int _lastContentsSweepCorpse;
    private const long AppraisalHoldTimeoutMarginMs = 1_000; // stop holding this long before the corpse timeout

    // Wedge detection: when AC's item-action state locks up (server-side, e.g.
    // after a corpse pile is opened too fast), EVERY corpse open times out.
    // Rather than hammer a dead client for 45 min, count consecutive open
    // timeouts → at the threshold, halt looting briefly, fire one reset, and
    // tell the user to relog (only thing that clears a server-side lock).
    private int _consecutiveLootTimeouts;
    private long _lootHaltUntil;
    private const int LootWedgeThreshold = 3;    // consecutive open timeouts ⇒ wedged
    private const long LootWedgeHaltMs = 20_000; // pause looting this long after detecting
    private const uint MotionTurnRight = 0x6500000D;
    private const uint MotionTurnLeft  = 0x6500000E;
    private int _openedContainerId;
    private int _pendingLootCheckContainerId; // non-corpse container opened natively: run lootcheck on inventory arrival
    private int _corpseItemsEvaluated;  // items checked against loot profile this corpse session
    // What came of this corpse's (or chest's) items, for the "nothing to loot" line at its end
    // (NothingLootedLine). Ids, not counts: every item is classified again on every pass.
    private readonly HashSet<int> _corpseMatchedIds = new();  // a loot rule approved it for pickup
    private readonly HashSet<int> _corpseCappedIds = new();   // it matched, left at its Keep # / keep count
    private int _corpseItemsLooted;                           // pickups confirmed (the only "looted")

    /// <summary>A new corpse or chest session: its tally starts over.</summary>
    private void ResetLootTally()
    {
        _corpseItemsEvaluated = 0;
        _corpseMatchedIds.Clear();
        _corpseCappedIds.Clear();
        _corpseItemsLooted = 0;
    }

    /// <summary>
    /// The chat line for a corpse or chest that ends with nothing picked up, or null when something
    /// was. A match only counts as looted once its pickup is confirmed: an item left at its Keep #
    /// or mana-stone keep count, for want of pack room, or as salvage with no UST is not. Items that
    /// matched but were all left at their counts used to end with only the "Keep # ... leaving"
    /// lines, never a "nothing to loot" (owner, 2026-10-31).
    /// </summary>
    private string? NothingLootedLine(string name, bool chest = false)
    {
        if (_corpseItemsLooted > 0) return null;
        int approved = _corpseMatchedIds.Count;
        int capped = _corpseCappedIds.Count(id => !_corpseMatchedIds.Contains(id));
        if (approved == 0 && capped == 0)
            return _corpseItemsEvaluated > 0
                ? (LootModes.IsRares(_dashboard?.Settings?.LootMode ?? LootModes.Profile)
                    ? $"[RynthAi] Nothing {(chest ? "in" : "on")} {name} is a rare or a needed mana stone (Loot Mode: rares)."
                    : $"[RynthAi] Nothing {(chest ? "in" : "on")} {name} matches the loot profile.")
                : $"[RynthAi] Nothing to loot on {name}.";
        if (approved == 0)
            return $"[RynthAi] Nothing to loot on {name}: {capped} item(s) match, but you already have enough.";
        return $"[RynthAi] Nothing to loot on {name}: {approved + capped} item(s) match, but none could be taken"
               + (capped > 0 ? $" (you already have enough of {capped})." : ".");
    }
    // Mana stones we've approved for loot but whose cache arrival hasn't landed yet.
    // Added to haveCount so the KeepCount cap isn't bypassed in the window between
    // MoveItemExternal confirmation and the player-inventory CreateObject event.
    // HashSet (not a plain count) because ClassifyItemAgainstProfile is called for
    // every item on the corpse every tick — a counter would over-count by N each
    // re-evaluation pass.
    private readonly HashSet<int> _pendingManaStoneIds = new();

    // Keep # (KeepUpTo) rules: items approved on this corpse per rule, with their
    // stack size, not yet counted in the pack. Same idea as _pendingManaStoneIds.
    private readonly Dictionary<string, Dictionary<int, int>> _pendingKeepUpTo = new();

    // Same idea for ManaTap candidates: tracks items approved this corpse
    // session so we don't approve more than the available empty-stone slots.
    private readonly HashSet<int> _pendingManaTapIds = new();

    // Action label of an item looted only so ManaStoneManager can drain it. A pickup
    // with this label is recorded in ManaStoneManager, whose drain touches nothing else.
    private const string ManaTapActionLabel = "ManaTap";
    private int _targetCorpseId;
    private int _currentLootItemId;
    private long _corpseTargetSince;
    private long _openedContainerAt;
    private long _openedContainerInventoryObservedAt;
    private long _lastCorpseOpenAttemptAt;
    private long _lastCorpseApproachHeartbeatAt;
    private long _lastLootActionAt;
    private long _currentLootItemRequestedAt;
    private long _currentLootItemMoveRequestedAt;
    private bool _currentLootItemRequested;
    private bool _currentLootItemMovePending;
    private bool _corpseAutorunActive;
    private int _currentLootItemMoveAttempts;
    // Where the current item's last move went, and whether it already got a second pack.
    private int _currentLootItemDest;
    private bool _currentLootItemPackRetried;
    // Packs the server took none of an item's tries into (Loot/PackRefusals.cs).
    private readonly RynthCore.Plugin.RynthAi.Loot.PackRefusals _packRefusals = new();
    private long _currentLootItemStillSince;
    private long _currentLootItemLeftAt;
    private bool _currentLootItemSawAnim;
    private bool _currentLootItemStackable;   // a stack at send: it may merge into a pack stack and vanish
    private bool _currentLootItemPipelined;   // sent while the last pickup's Return played (ACE queues it)
    private bool _currentLootItemJumped;      // jump-loot: sent in the air, its own jump or the last one's (not a resend)
    private bool _currentLootItemSentInAirWindow; // sent on the strength of the last no-bend-down pickup (JumpPickupInAir)
    private int _currentLootItemErrSeqAtSend; // TryGetLastWeenieError seq at the send (0 = unknown)
    private int _currentLootItemFreeRetries;  // resends after a too-busy answer (not counted as attempts)
    // 2026-10-29 (R-20261029-122319-321f): what the server said to the last send, and what the
    // tries into the current destination came to (TickPendingCorpsePickup, JudgeLootDest).
    private bool _currentLootItemAwaitingAnswer;   // the last send has had no answer yet
    private long _currentLootItemRefusedAt;        // when a refusal of the last send was first seen (0 = none)
    private uint _currentLootItemRefusalError;
    private uint _currentLootItemRefusalObject;    // the item the refusal named (0: none, a quest refusal)
    private bool _currentLootItemRefusalBusy;      // "too busy": before its pickup could run, not the pack's answer
    private int _currentLootItemDestRefusals;      // tries into _currentLootItemDest refused after the pickup ran
    private int _currentLootItemDestTimeouts;      // tries into it with no answer in PickupAnswerWaitMs
    private bool _currentLootItemTimedOut;         // a send of this item went unanswered: a later answer may be to it
    private readonly List<int> _currentLootItemDestsTried = new();
    private readonly List<int> _currentLootItemFullPacks = new();   // packs this item's refusals marked full
    private long _moveAwaitTickAt;                 // CorpseNowMs of the previous answer-wait call
    // 2026-11-02 (R-20261102-204309-206c): an open corpse while a fight has the tick (HoldOpenCorpseForFight).
    private long _fightHoldSince;                  // CorpseNowMs the open corpse began waiting for the fight; 0 = none
    private long _fightHoldTickAt;                 // CorpseNowMs of the previous held tick (the corpse clock is paid back)
    private long _fightHoldEndedAt;                // CorpseNowMs the last such wait ended (NoteNextSendWaits counts from it)
    private bool _currentLootItemFightHeld;        // the last send had no answer when a fight took the tick
    private int _currentLootItemFightResends;      // resends for a fight in between (not counted as tries)
    private bool _currentLootItemResendWaitLogged; // the "next send waits" line was written for the last send
    // How long the server took to answer recent pickups (confirmations and refusals after the
    // pickup ran): the answer wait is sized from the slowest of them.
    private readonly long[] _pickupAnswerLatencies = new long[8];
    private int _pickupAnswerLatencyCount, _pickupAnswerLatencyNext;
    // Items given up (or dropped by a reset) with a move still possibly at the server: one that
    // lands in a pack afterwards un-marks that pack (WatchLateLandings).
    private readonly Dictionary<int, (long Until, long SentAt)> _lateLandings = new();
    // The last answered pickup, until the character has finished its "Return" (stood up again).
    // The corpse/chest is closed only after this (2026-10-07), and the next move may go out while
    // it plays: ACE queues a move that arrives during the Return as its NextPickup.
    private int _settlingLootItemId;
    private int _settlingContainerId;
    private long _settlingSince;
    private bool _settlingSawAnim;
    private bool _settlingAllowsPipeline;
    private int _pipelinedAfterItemId;        // the answered pickup a move was already queued behind
    // A jump-loot pickup answered with no bend-down (JumpPickupHadNoMotion): until this CorpseNowMs
    // the client's motion queue is the jump's own (take-off, fall, landing), not a server pickup.
    private long _jumpPickupAirUntil;
    // The no-bend-down call is off until this CorpseNowMs (DistrustJumpNoMotion): the server turned a
    // move or open away as too busy that went out on it, so it had played a bend-down after all.
    private long _jumpNoMotionOffUntil;
    // The UseDone count before a corpse open sent inside the no-bend-down window (-1: none watched).
    private int _openInAirWindowSeq = -1;
    /// <summary>How long the no-bend-down call stays off after the server proved it wrong.</summary>
    internal const long JumpNoMotionDistrustMs = 5 * 60_000;

    // Flight budget. A pickup sent in the air with no fresh jump is a no-bend-down pickup only if the
    // server still sees the character jumping when it processes the move. Prediction: jumpAge (ms
    // since the loot jump) + the recent send-to-off-corpse time of no-bend-down pickups must stay
    // within this budget, else the send waits for the landing and takes a fresh jump (a landing and
    // a jump cost about 0.5 s; a late in-air send got the whole bend-down and Return, about 2 s).
    // Measured in client logs from one ACE server: of 56 in-air sends answered with no bend-down,
    // the largest jumpAge + off-corpse ms was 809.
    internal const long LootJumpFlightBudgetMs = 809;

    /// <summary>The send-to-off-corpse time assumed until a no-bend-down pickup has been measured (log median 203 ms).</summary>
    internal const long DefaultNoMotionOffCorpseMs = 200;

    // The send-to-off-corpse ms of the last 4 pickups answered with no bend-down (ConfirmCurrentLootItem).
    private readonly long[] _noMotionOffCorpse = new long[4];
    private int _noMotionOffCorpseCount, _noMotionOffCorpseNext;
    private bool _flightHoldLogged;            // the hold for the current send has been logged
    // [LootEval] lines: one per item per open (a matched item was re-printed before every pickup).
    private readonly Dictionary<int, string> _lootEvalLogged = new();
    private int _manaStoneCapLoggedFor;        // container the "ManaStone count for cap check" line was logged for
    private int _manaStoneLiveCount;
    private long _manaStoneLiveCountAt;
    private const long ManaStoneLiveCountMs = 200;
    private long _pickupAnimSince;
    private string _currentLootItemActionLabel = string.Empty;
    private string _currentLootItemName = string.Empty;
    private bool _currentLootItemIsSalvage;
    private (string Line, int ChatType)? _currentLootItemChat;   // the described "Looted" line; null = the plain one
    private bool _corpseIdsRequested;
    private long _corpseIdsRequestedAt;
    private bool _corpseApproachTurnsActive;

    /// <summary>The loot clock (ms); the host tests replace it to run minutes of looting at once.</summary>
    internal static Func<long> CorpseClock = static () => Environment.TickCount64;
    private static long CorpseNowMs => CorpseClock();
    private void LootDiag(string message)
    {
        Log(message);
    }

    // The values behind the last [LootSettings] line; a new line goes out only when one changes.
    private (LootBaseline.Timers Timers, double Max, double Min)? _lootSettingsLogged;

    /// <summary>
    /// Logs the loot timers against the measured baseline (LootBaseline.cs), each marked "baseline"
    /// or "custom (baseline N)", so a loot log says which settings it ran with. Runs every loot tick,
    /// so it compares the raw values and builds the line only on a change.
    /// </summary>
    private void NoteLootSettings(LegacyUiSettings settings)
    {
        var timers = new LootBaseline.Timers(
            settings.LootContentSettleMs, settings.LootEmptyCorpseMs, settings.LootAssessWindowMs,
            settings.LootRetryTimeoutMs, settings.LootOpenRetryMs, settings.LootCorpseTimeoutMs);
        var current = (timers, settings.CorpseApproachRangeMax, settings.CorpseApproachRangeMin);
        if (_lootSettingsLogged == current)
            return;
        _lootSettingsLogged = current;
        LootDiag(LootBaseline.Describe(timers, settings.CorpseApproachRangeMax, settings.CorpseApproachRangeMin));
    }

    public override void OnBusyCountIncremented()
    {
        if (_busyCount == 0)
            _busyCountBecamePositiveAt = CorpseNowMs; // record first transition 0→positive
        _busyCount++;
        _busyCountLastIncrementAt = CorpseNowMs;
        if (_combatManager != null) _combatManager.BusyCount = _busyCount;
        if (_buffManager != null) _buffManager.BusyCount = _busyCount;
    }

    public override void OnBusyCountDecremented()
    {
        if (_busyCount > 0)
            _busyCount--;
        if (_busyCount == 0)
            _busyCountBecamePositiveAt = 0; // back to idle — reset the elevation timer
        if (_combatManager != null) _combatManager.BusyCount = _busyCount;
        if (_buffManager != null) _buffManager.BusyCount = _busyCount;
    }

    /// <summary>Safety valve: if busy count has been continuously positive for too long,
    /// force-reset it. Uses the "first went positive" timestamp so that a stream of
    /// new increments cannot keep refreshing the window indefinitely.
    /// When combat has live targets, drops to a much shorter threshold — stale busy
    /// from salvage retries shouldn't lock the bot out of fighting for 10s.</summary>
    // 5s, deliberately ABOVE the engine reconciler's 4s no-gesture timeout
    // (BusyCountHooks.CastBusyReconcileTimeoutMs): the reconciler's clean
    // deterministic decrement must win this race. At the old 2s, this clear
    // fired first on every corpse open — force-clears (with their mid-flight
    // busy-zero churn) paced the whole loot grind, and the 2026-06-12 09:33
    // storm of them latched the variant-2 stance/item deadlock.
    private const long BUSY_TIMEOUT_COMBAT_URGENT_MS = 5_000;
    // The engine's count is the one owner (2026-10-06; analysis by Silentkelpie, re-implemented
    // here): this mirror is fed by the engine's busy events, and the engine now sends one for
    // every UseDone and every reset it does, so the two agree except for events still on their
    // way. A disagreement that lasts BusyMirrorFollowMs is settled the engine's way; the old
    // 5/10 s mirror reset below is only for an engine without GetBusyState.
    private const long BusyMirrorFollowMs = 1_500;
    private long _busyMismatchSince;
    private long _lastBusyFollowLogAt;
    private int _busyFollowsSinceLog;

    private bool FollowEngineBusyCount()
    {
        if (!Host.HasGetBusyState) return false;
        int engine;
        try { engine = Host.GetBusyState(); } catch { return false; }
        long now = CorpseNowMs;
        if (engine == _busyCount) { _busyMismatchSince = 0; return true; }
        if (_busyMismatchSince == 0) { _busyMismatchSince = now; return true; }
        if (now - _busyMismatchSince < BusyMirrorFollowMs) return true;
        int was = _busyCount;
        _busyMismatchSince = 0;
        _busyCount = Math.Max(0, engine);
        _busyCountLastIncrementAt = 0;
        _busyCountBecamePositiveAt = _busyCount > 0 ? now : 0;
        if (_combatManager != null) _combatManager.BusyCount = _busyCount;
        if (_buffManager != null) _buffManager.BusyCount = _busyCount;
        _busyFollowsSinceLog++;
        if (now - _lastBusyFollowLogAt >= 10_000)
        {
            Log($"[RynthAi] Busy mirror {was} -> {_busyCount}: following the engine's count ({_busyFollowsSinceLog} in the last {(now - _lastBusyFollowLogAt) / 1000.0:0} s; the client's count is never cleared by RynthAi).");
            _lastBusyFollowLogAt = now;
            _busyFollowsSinceLog = 0;
        }
        return true;
    }

    private void CheckBusyTimeout()
    {
        if (FollowEngineBusyCount())
        {
            PruneCorpseCollections();
            return;
        }
        if (_busyCount > 0 && _busyCountBecamePositiveAt != 0)
        {
            long elapsed = CorpseNowMs - _busyCountBecamePositiveAt;
            bool combatUrgent = _combatManager?.HasTargets == true;
            // Also treat the corpse approach/open phase as urgent: offensive/buff casts
            // leak m_cBusy (off-thread CastSpell increments but never pairs a decrement),
            // and a lingering phantom count makes TickCorpseOpening sit on OpenBusyWaitMaxMs
            // before opening the next corpse — ~8s of dead time per corpse between kills,
            // when HasTargets is false so the 10s fallback applied. Scoped to "corpse
            // claimed but not yet open" so it never interrupts an in-flight item pickup
            // (no pickup is pending until a container is open).
            bool lootOpenUrgent = _targetCorpseId != 0 && _openedContainerId == 0;
            bool urgent = combatUrgent || lootOpenUrgent;
            long threshold = urgent ? BUSY_TIMEOUT_COMBAT_URGENT_MS : BUSY_TIMEOUT_MS;

            // Don't abort a legitimately in-progress cast. The explicit-target war
            // cast (FreeHandsAndCastSpell readies the wand THEN casts, so m_cBusy
            // often climbs to 2) holds busy for >2s on high tiers — longer than the
            // combat-urgent threshold. Force-clearing at 2s aborted the cast before
            // its projectile launched (dealt~0, the mob survived) and churned AC's
            // action state via ForceResetBusyCount — a prime driver of the
            // "can't enter combat mode" wedge. CanCastNow is the engine's
            // CMotionInterp gesture gate (the SAME signal BuffManager casts on);
            // while a gesture animates, the count is REAL, so extend to the 10s
            // absolute backstop (a truly stuck count still self-heals). No-ops on
            // an engine without the gate (HasGetCastBusyState false → unchanged).
            bool castGestureInProgress = Host.HasGetCastBusyState && !Host.CanCastNow;
            if (castGestureInProgress && threshold < BUSY_TIMEOUT_MS)
                threshold = BUSY_TIMEOUT_MS;

            if (elapsed > threshold)
            {
                // Our mirror only (2026-10-06). AC's m_cBusy is +1 per action the client sends and
                // -1 per server UseDone (ClientUISystem::Handle_Item::UseDone, inline - which is why
                // this mirror, fed by the Increment/Decrement hooks, never saw the decrement and
                // looked "stuck"). Force-clearing AC's count here zeroed the count of actions the
                // server had not answered yet (each later UseDone then drove it negative: ~190
                // repairs an hour in the 10-05/10-06 logs) and let the next action out on top of
                // them. The pacing waits on the server's answers now (ActionPacer); the client's
                // own count is left to the client and the engine.
                bool serverWaiting = RynthCore.Plugin.Shared.ActionPacer.Current(Host) != null;
                Log($"[RynthAi] Busy mirror at {_busyCount} for >{elapsed}ms (combatUrgent={combatUrgent} lootOpenUrgent={lootOpenUrgent} castGesture={castGestureInProgress}" +
                    $"{(serverWaiting ? ", a server action is still unanswered" : "")}) — resetting the mirror only (the client's count is left alone).");
                ResetCurrentLootItem();
                _corpseIdsRequested = false;
                _lastLootActionAt = CorpseNowMs;
                _busyCount = 0;
                _busyCountLastIncrementAt = 0;
                _busyCountBecamePositiveAt = 0;
                if (_combatManager != null) _combatManager.BusyCount = 0;
                if (_buffManager != null) _buffManager.BusyCount = 0;
            }
        }

        PruneCorpseCollections();
    }

    // BuffManager calls this the instant a cast RESOLVES in chat: reset the
    // plugin's SHADOW busy counter so the next buff isn't gated on a stale
    // mirror. ⚠ This no longer touches the REAL m_cBusy: the engine's
    // delta-reconciler (e70ac92/9611af8) owns the real field now, decrementing
    // exactly what each direct cast/item-action added once its gesture ends.
    // The old Host.ForceResetBusyCount() here fired on EVERY cast resolution —
    // during the 2026-06-12 buff no-show storm that was a blind real-field
    // zero every ~2.5s, which (a) beat the reconciler to the decrement so it
    // never ran (rec frozen while fcl climbed), and (b) destroyed AC's own
    // item-action serialization, feeding the "one item at a time" latch. The
    // CheckBusyTimeout watchdog above remains the real-field backstop.
    private void OnBuffCastResolved(string reason)
    {
        if (_busyCount <= 0) return;
        int was = _busyCount;
        _busyCount = 0;
        _busyCountLastIncrementAt = 0;
        _busyCountBecamePositiveAt = 0;
        if (_combatManager != null) _combatManager.BusyCount = 0;
        if (_buffManager != null) _buffManager.BusyCount = 0;
        Log($"[RynthAi] shadow busy-reset on cast resolve (was {was}, {reason}) — real field left to the engine reconciler");
    }

    // CombatManager calls this when the busy count holding its attack is a leftover: the server
    // finished every action behind it (a UseDone arrived after it last rose) and no gesture is
    // animating — AC lowered the real field inline, where the busy hook can't see it (see
    // CombatBusyGate). Same as OnBuffCastResolved: the shadow only, the real field stays with the
    // engine reconciler, and it spares CheckBusyTimeout's real-field force-clear 5 s later.
    // Logged at most every 10 s (it can happen on every cast).
    private long _lastCombatBusyStaleLogAt;
    private int _combatBusyStaleSinceLog;
    private void OnCombatBusyStale(string reason)
    {
        if (_busyCount <= 0) return;
        int was = _busyCount;
        _busyCount = 0;
        _busyCountLastIncrementAt = 0;
        _busyCountBecamePositiveAt = 0;
        if (_combatManager != null) _combatManager.BusyCount = 0;
        if (_buffManager != null) _buffManager.BusyCount = 0;
        _combatBusyStaleSinceLog++;
        long now = CorpseNowMs;
        if (now - _lastCombatBusyStaleLogAt >= 10_000)
        {
            string count = _lastCombatBusyStaleLogAt == 0 ? "first"
                : $"{_combatBusyStaleSinceLog} in the last {(now - _lastCombatBusyStaleLogAt) / 1000.0:0}s";
            Log($"[RynthAi] shadow busy-reset for a leftover count (was {was}, {reason}; {count}) — real field left to the engine reconciler");
            _lastCombatBusyStaleLogAt = now;
            _combatBusyStaleSinceLog = 0;
        }
    }

    private long _lastCorpsePruneAt;
    private const long CorpsePruneIntervalMs = 60_000; // prune once per minute
    // Retain a completed-corpse mark this long. Must exceed the longest AC corpse
    // lifetime by a wide margin (so an aged-out entry's corpse is provably gone and
    // can never be re-looted) while staying well under server GUID-reuse timescales.
    private const long CompletedCorpseRetentionMs = 30 * 60_000; // 30 min

    private void PruneCorpseCollections()
    {
        long now = CorpseNowMs;
        if (now - _lastCorpsePruneAt < CorpsePruneIntervalMs) return;
        _lastCorpsePruneAt = now;

        // _completedCorpses — authoritative "looted, do not reopen" set. Evict ONLY by
        // age: an entry older than the retention window refers to a corpse that has long
        // since despawned (well past max AC corpse lifetime), so it cannot be re-looted
        // and its id is past realistic server GUID reuse. Presence in the render cache is
        // NOT a despawn signal — an out-of-range corpse must stay marked or the bot
        // re-opens it on the next patrol loop — so it is deliberately not consulted here.
        if (_completedCorpses.Count > 0)
        {
            var stale = new List<int>();
            foreach (var kvp in _completedCorpses)
                if (now - kvp.Value >= CompletedCorpseRetentionMs)
                    stale.Add(kvp.Key);
            if (stale.Count > 0)
            {
                int before = _completedCorpses.Count;
                foreach (int id in stale)
                    _completedCorpses.Remove(id);
                Log($"[RynthAi] Pruned _completedCorpses: {before} → {_completedCorpses.Count}");
            }
        }

        // _ownershipSkipLogged / _corpseIdRequested — pure spam-prevention, safe to wipe periodically
        if (_ownershipSkipLogged.Count > 200)
        {
            _ownershipSkipLogged.Clear();
            Log($"[RynthAi] Pruned _ownershipSkipLogged");
        }
        if (_corpseIdRequested.Count > 200)
        {
            _corpseIdRequested.Clear();
            _corpseIdAskedAt.Clear();
            _corpseIdRefusedAt.Clear();
            _corpseIdAskCount.Clear();
        }
        if (_corpseSkipLogged.Count > 200)
            _corpseSkipLogged.Clear();
        if (_corpseWaitingSince.Count > 200)
        {
            var old = new List<int>();
            foreach (var kvp in _corpseWaitingSince)
                if (now - kvp.Value > 10 * 60_000 || _completedCorpses.ContainsKey(kvp.Key)) old.Add(kvp.Key);
            foreach (int id in old) _corpseWaitingSince.Remove(id);
        }

        // _corpseCooldownUntil — remove expired entries
        var expiredCooldowns = new List<int>();
        foreach (var kvp in _corpseCooldownUntil)
            if (kvp.Value <= now) expiredCooldowns.Add(kvp.Key);
        foreach (var id in expiredCooldowns)
            _corpseCooldownUntil.Remove(id);
    }

    public override void OnViewObjectContents(uint objectId)
    {
        int sid = unchecked((int)objectId);
        if (!IsCorpseLikeObject(sid))
        {
            // Non-corpse container (chest, bag, etc.) — queue a loot-check once inventory arrives.
            _pendingLootCheckContainerId = sid;
            // And a chest to loot, if it turns out eligible (RynthAiPlugin.ChestLoot.cs).
            NoteContainerOpened(sid);
            NoteChestKeyOpened(sid);   // the Item Tool's chest looter waits for this
            NoteManualChestOpened(sid); // Pickup with macro off (RynthAiPlugin.ChestLoot.cs)
            return;
        }

        // A corpse opened while a chest was the loot container: the server closed the chest.
        if (_lootChestId != 0 && _lootChestId != sid)
            _lootChestId = 0;

        // _completedCorpses is authoritative. On an engine without CloseContainer (API
        // before v76) a looted corpse stays open server-side, so AC re-announces its
        // contents every time it re-streams into range on a patrol loop. Treat any reopen of a completed
        // corpse as a passive re-announce: never clear the mark, never start a loot
        // session. Membership alone decides — same rule the polling reconciler
        // already enforces in SyncCorpseContainerState. A corpse NOT in the set
        // (incl. one the user manually opens) still loots normally below.
        if (_completedCorpses.ContainsKey(sid))
        {
            LootDiag($"[RynthAi] Corpse loot: ignoring reopen of completed corpse 0x{objectId:X8}.");
            return;
        }

        _corpseCooldownUntil.Remove(sid);
        bool sameCorpse = _targetCorpseId == sid;
        bool switchedTarget = _targetCorpseId != 0 && _targetCorpseId != sid;

        _openedContainerId = sid;
        _openedContainerAt = CorpseNowMs;
        _openedContainerInventoryObservedAt = 0;
        _lastLootActionAt = 0;
        ResetLootTally();
        _corpseIdsRequested = false;
        ResetCurrentLootItem();
        if (!sameCorpse)
        {
            _processedCorpseItems.Clear();
            _corpseItemAttempts.Clear();
            _pendingManaStoneIds.Clear(); _pendingManaTapIds.Clear(); _pendingKeepUpTo.Clear();
        }
        LootDiag($"[RynthAi] Corpse loot: opened container 0x{objectId:X8}.");
        _consecutiveLootTimeouts = 0; // a successful open proves AC is responsive again
        _lootWedgeAnnounced = false;
        if (_useCheckJumpAt != 0)
        {
            Log($"[RynthAi] Use check: 0x{objectId:X8} opened {CorpseNowMs - _useCheckJumpAt} ms after the use check's jump - the jump cleared the server's stuck turn.");
            _useCheckJumpAt = 0;
        }
        ApplyUseRefusalVerdict(_useRefusals.OnOpened(CorpseNowMs), CorpseNowMs);
        if (switchedTarget)
            LootDiag($"[RynthAi] Corpse loot: switching active corpse target from 0x{(uint)_targetCorpseId:X8} to 0x{objectId:X8}.");

        _targetCorpseId = sid;
        _corpseTargetSince = CorpseNowMs;
        _lastCorpseOpenAttemptAt = 0;
    }

    public override void OnUpdateObjectInventory(uint objectId)
    {
        _objectCache?.MarkInventoryDirty();
        _inventoryManager?.MarkDirty();

        int sid = unchecked((int)objectId);
        if (sid == 0)
            return;

        // Non-corpse container opened natively — run lootcheck on all its items.
        if (_pendingLootCheckContainerId != 0 && sid == _pendingLootCheckContainerId)
        {
            _pendingLootCheckContainerId = 0;
            // A chest the looter takes: it logs and says per item what it does.
            if (ChestLooterTakesContainer(sid))
                return;
            if (_objectCache != null)
            {
                foreach (WorldObject item in _objectCache.GetContainedItems(sid))
                    InspectLootRuleForItem(item.Id, quiet: true);
            }
            return;
        }

        if (_openedContainerId != 0)
        {
            if (sid != _openedContainerId)
                return;
        }
        else if (_targetCorpseId == 0 || sid != _targetCorpseId || !IsCorpseLikeObject(sid))
        {
            return;
        }

        bool firstObservation = _openedContainerInventoryObservedAt == 0;
        _openedContainerInventoryObservedAt = CorpseNowMs;
        if (firstObservation)
            LootDiag($"[RynthAi] Corpse loot: inventory update observed for 0x{objectId:X8}.");
    }

    public override void OnStopViewingObjectContents(uint objectId)
    {
        int sid = unchecked((int)objectId);
        if (_pendingLootCheckContainerId == sid)
            _pendingLootCheckContainerId = 0;
        // A chest: closed by the player, the server (walked away) or our own close. Never reopened.
        if (OnChestGone(sid, "was closed"))
            return;
        bool wasOpenCorpse = _openedContainerId == sid;
        if (_openedContainerId == sid)
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
        }

        if (_targetCorpseId == sid)
        {
            if (_completedCorpses.ContainsKey(sid))
            {
                ResetCorpseTarget();
                return;
            }

            if (wasOpenCorpse)
            {
                _lastCorpseOpenAttemptAt = 0;
                _lastLootActionAt = CorpseNowMs;
                LootDiag($"[RynthAi] Corpse loot: container 0x{objectId:X8} closed before completion; will retry open until timeout.");
            }
        }
    }

    public override void OnVendorOpen(uint vendorId)
    {
        _metaManager?.OnVendorOpen(vendorId);

        // AutoVendor prints the open line itself (UB's merchant-info line when it can read the
        // vendor, once per open rather than on every list re-send) and may start a session.
        var autoVendor = _autoVendor;
        if (autoVendor != null)
        {
            autoVendor.OnVendorOpen(vendorId);
            return;
        }
        string label = Host.TryGetObjectName(vendorId, out string name) ? name : $"0x{vendorId:X8}";
        Host.WriteToChat($"[RynthAi] Vendor open: {label}", 1);
    }

    public override void OnVendorClose(uint vendorId)
    {
        _metaManager?.OnVendorClose(vendorId);
        // /mt vendor addbuy / addsell lists belong to the vendor that was open.
        ClearMtVendorLists();

        var autoVendor = _autoVendor;
        if (autoVendor != null)
        {
            autoVendor.OnVendorClose(vendorId);
            return;
        }
        string label = Host.TryGetObjectName(vendorId, out string name) ? name : $"0x{vendorId:X8}";
        Host.WriteToChat($"[RynthAi] Vendor closed: {label}", 1);
    }

    // -- Loot stall watch (R-20261031-203155-cea0) ---------------------------------------
    // The arbiter held the bot in Looting (HasLootWork: loot=True) while this claim returned
    // early every tick, and nothing said why: Boost Nav Priority was on with no route to run,
    // so Decide()'s BoostNav clause (guarded by WantNav) let Looting win while the full stop
    // below reset every claim. The bot stood at its kill showing "Looting" with not one
    // "Corpse loot:" line. Now every early return names its reason, and a Looting tick that
    // ends with nothing claimed or open logs it once the stall has lasted LootStallLogMs,
    // then again every LootStallRepeatMs while it lasts.
    internal const long LootStallLogMs = 3_000;
    internal const long LootStallRepeatMs = 15_000;
    private string _lootSkipWhy = string.Empty;   // why the last loot tick claimed nothing
    private string _lootWorkWhy = string.Empty;   // what HasLootWork last counted as loot work
    private long _lootStallSince;                 // CorpseNowMs the stall began; 0 = none
    private long _lootStallLoggedAt;

    private void TickCorpseOpening()
    {
        _lootSkipWhy = string.Empty;
        TickCorpseOpeningCore();
        WatchLootStall();
    }

    /// <summary>
    /// After each loot tick: the arbiter gave Looting the tick, yet no corpse, chest or ground
    /// item is claimed or open. A short gap is normal (the loot grace before a corpse exists,
    /// the death animation); one past LootStallLogMs is logged with the skip reason.
    /// </summary>
    private void WatchLootStall()
    {
        bool stalled = _activity == BotActivity.Looting
            && _targetCorpseId == 0 && _openedContainerId == 0 && _groundTargetId == 0;
        long now = CorpseNowMs;
        if (!stalled)
        {
            if (_lootStallSince != 0 && _lootStallLoggedAt != 0)
                LootDiag($"[RynthAi] Corpse loot: the Looting stall is over after {(now - _lootStallSince) / 1000.0:0.0}s.");
            _lootStallSince = 0;
            _lootStallLoggedAt = 0;
            return;
        }
        if (_lootStallSince == 0) { _lootStallSince = now; return; }
        long held = now - _lootStallSince;
        if (held < LootStallLogMs) return;
        if (_lootStallLoggedAt != 0 && now - _lootStallLoggedAt < LootStallRepeatMs) return;
        _lootStallLoggedAt = now;
        string why = _lootSkipWhy.Length > 0 ? _lootSkipWhy : "no reason recorded";
        string work = _lootWorkWhy.Length > 0 ? _lootWorkWhy : "unknown";
        LootDiag($"[RynthAi] Corpse loot: Looting has held the tick {held / 1000.0:0.0}s with nothing claimed: {why} (loot work: {work}).");
    }

    /// <summary>
    /// Boost Nav Priority with a nav route running means no looting at all: corpses are passed by
    /// and the bot follows the route (owner, 2026-10-31: "when navprio is on it shouldn't loot at
    /// all, just bypass all corpses and navigate"). The claim and HasLootWork both read this one
    /// test, so Looting never takes the tick while the looter stands down
    /// (R-20261031-203155-cea0). Owner's corrected rule (2026-11-02, R-20261103-000159-a65e):
    /// only while a nav route runs (BoostNavRule.Active); with no route, a Once route at its end,
    /// or the Outdoor Hunt, corpses are looted as with the flag off. A forced loot (/ra lootthis)
    /// always goes.
    /// </summary>
    private static bool BoostNavHoldsLoot(LegacyUiSettings settings)
        => BoostNavRule.Active(settings);

    private void TickCorpseOpeningCore()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
        {
            _lootSkipWhy = "no settings, object cache or player yet";
            return;
        }

        SyncCorpseContainerState();
        WatchLateLandings(CorpseNowMs);
        TickDeferredClose(settings);
        TakeLootThisRequest(settings);   // /ra lootthis (RynthAiPlugin.VTankHotkeys.cs)
        TakeChestKeyRequest();           // the Item Tool's chest looter (RynthAiPlugin.ItemTool.cs)
        TakeManualChest(settings);       // Pickup with macro off (RynthAiPlugin.ChestLoot.cs)

        // ── Macro off / looting disabled / nav-boost — full stop and reset ──
        // Must come before the Buffing check so that turning off the macro always
        // clears motion state (smooth turns, autorun) set during corpse approach.
        // A container flagged with /ra lootthis is looted anyway (only that one).
        if ((!settings.IsMacroRunning || !settings.EnableLooting || BoostNavHoldsLoot(settings)) && !ForcedLootHolds())
        {
            _lootSkipWhy = !settings.IsMacroRunning ? "the macro is off"
                : !settings.EnableLooting ? "looting is off" : "Boost Nav Priority is on and a nav route runs (RynthAi only follows it)";
            ResetCorpseTarget();
            return;
        }
        NoteLootSettings(settings);

        // Wedge halt: AC's item-action state is locked (consecutive open
        // timeouts). Stop firing opens into a dead client until the halt window
        // elapses — hammering it can't recover a server-side lock, only a relog
        // can. Navigation/combat (non-item-action) still run elsewhere.
        // The use-refusal watch first: it reads the server's answers to our opens, and while it
        // has decided the server refuses every use (Stuck) no corpse is claimed or opened until
        // its back-off ends.
        long haltNow = CorpseNowMs;
        TickUseRefusalWatch(haltNow);
        if (haltNow < _lootHaltUntil || _useRefusals.HoldsOpens(haltNow))
        {
            _lootSkipWhy = haltNow < _lootHaltUntil
                ? $"loot-wedge halt ({(_lootHaltUntil - haltNow) / 1000.0:0.0}s left)"
                : "the server refuses every use (use-refusal back-off)";
            return;
        }

        // ── Buffing: halt loot work but retain target so looting resumes immediately ──
        if (string.Equals(settings.BotAction, "Buffing", StringComparison.OrdinalIgnoreCase))
        {
            _lootSkipWhy = "buffing";
            FreezeCorpseTimeout(CorpseNowMs);
            return;
        }

        // ── Mana tapping: an item looted to tap is drained before the next pickup or corpse ──
        // (2026-10-15: the looter went on opening corpses over the drain; a pickup still in
        // flight finishes first, since its answer is what lets the drain go.)
        if (!_currentLootItemMovePending && _manaStoneManager?.HoldsLootForDrain() == true)
        {
            _lootSkipWhy = "a mana-tap drain runs first";
            FreezeCorpseTimeout(CorpseNowMs);
            return;
        }

        // ── Combat takes priority over claiming NEW corpses ───────────────
        // But if we already have an opened container, finish looting it
        // (no movement, no conflict with combat) - while the arbiter gives Looting
        // the tick; one that gives Combat the tick makes the open corpse wait
        // for the fight (HoldOpenCorpseForFight).
        // Exception: loot starvation (see LootStarveMs) - the arbiter gave this tick to
        // Looting because a corpse waited too long and no monster is close.
        if (settings.EnableCombat
            && _combatManager?.HasTargets == true
            && !settings.BoostLootPriority
            && !_lootStarveTurn
            && _openedContainerId == 0)
        {
            _lootSkipWhy = "combat has targets in range";
            LogCorpsesHeldByCombat();
            LogChestHeldByCombat();
            FreezeCorpseTimeout(CorpseNowMs);
            return; // Keep our target, just wait — don't ResetCorpseTarget
        }

        // The timeout clock counts again only where loot work really runs: an open container
        // below, or an open the arbiter lets out (the Looting check further down). Resetting it
        // here, before that check, let every tick Combat owned age the claim (2026-10-09:
        // with Boost Loot Priority on, Drakkon dropped 20 corpses in an Olthoi spawn, each
        // claimed between kills and abandoned 12 s later, never opened).

        // ── Active corpse work — pause navigation ────────────────────────
        if (_openedContainerId != 0 || _targetCorpseId != 0)
            PauseNavigationForCorpse();

        if (_openedContainerId != 0)
        {
            _corpseHoldFrozenAge = -1;
            StopCorpseMovement();
            TickOpenCorpseLooting(settings);
            return;
        }

        // A chest the player or a meta opened: looted where it stands (RynthAiPlugin.ChestLoot.cs).
        if (_targetCorpseId == 0 && _chestCandidateId != 0 && TryTakeOpenChest(settings, CorpseNowMs))
            return;

        // A named object (Loot by name) when no corpse is claimed and nothing is open.
        if (_targetCorpseId == 0 && _lootNameTargetId != 0 && TryLootByName(settings, CorpseNowMs))
            return;

        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        double minMeters = GetCorpseApproachRangeMinMeters(settings, maxMeters);
        if (maxMeters <= 0.25)
        {
            _lootSkipWhy = $"corpse approach range is {maxMeters:0.00} m (0.25 m or less)";
            return;
        }

        long now = CorpseNowMs;
        if (_targetCorpseId == 0)
        {
            // Same gate as HasLootWork: this claim runs every tick whatever the arbiter
            // decided, so without it a full pack still claimed the next corpse. Your own
            // corpse is let through unless no pack has a slot at all.
            if (IsLootingBlockedByPack(settings) && !OwnCorpseBypassesPackGate(settings, maxMeters))
            {
                _lootSkipWhy = "the pack is full (Stop Looting When Pack Full)";
                return;
            }

            // A ground pickup already sent finishes before a corpse is claimed.
            if (GroundPickupInFlight)
            {
                _lootSkipWhy = "a ground pickup is in flight";
                TickGroundLoot(settings, maxMeters);
                return;
            }

            if (!TryFindNearestCorpse(maxMeters, out WorldObject? corpse, out _))
            {
                _lootSkipWhy = $"no lootable corpse within {maxMeters:0.0} m";
                // No corpses in range. Loose ground items that match the loot profile
                // get their turn here; HasLootWork keeps Looting while any remain.
                TickGroundLoot(settings, maxMeters);
                return;
            }

            // A corpse outranks an unsent ground claim.
            if (_groundTargetId != 0)
                ResetGroundLoot(clearCaches: false);

            if (corpse == null)
                return;

            _targetCorpseId = corpse.Id;
            _corpseTargetSince = now;
            _lastCorpseOpenAttemptAt = 0;
            _lastOpenAttemptCorpseId = 0;   // a new claim: no open sent for it yet
            LootDiag($"[RynthAi] Corpse loot: claimed corpse target 0x{(uint)corpse.Id:X8}.");
            PauseNavigationForCorpse();
        }
        else if (_openedContainerId == 0 && settings.LootOwnCorpse)
        {
            // Your own corpse came into range while another (unopened) corpse was claimed:
            // it beats normal looting, so switch to it.
            WorldObject? claimed = _objectCache[_targetCorpseId];
            if ((claimed == null || !EvaluateCorpseLootDecision(claimed).IsOwnDeathCorpse)
                && TryFindOwnCorpseInRange(maxMeters, out WorldObject? ownNow) && ownNow != null && ownNow.Id != _targetCorpseId)
            {
                LootDiag($"[RynthAi] Own corpse: 0x{(uint)ownNow.Id:X8} is in range; switching to it from 0x{(uint)_targetCorpseId:X8}.");
                ResetCorpseTarget();
                _targetCorpseId = ownNow.Id;
                _corpseTargetSince = now;
                _lastCorpseOpenAttemptAt = 0;
                _lastOpenAttemptCorpseId = 0;
            }
        }

        // A claim can be tentative: the killer is unknown until the corpse's ID lands.
        // Once it says the corpse isn't ours to loot, drop the claim instead of walking
        // over and firing opens the server refuses until the 12 s timeout (which also
        // fed the loot-wedge counter). TryFindNearestCorpse then skips it as usual.
        WorldObject? claimedCorpse = _objectCache[_targetCorpseId];
        if (claimedCorpse != null && claimedCorpse.ObjectClass == AcObjectClass.Corpse)
        {
            CorpseLootDecision claimDecision = EvaluateCorpseLootDecision(claimedCorpse);
            if (!claimDecision.ShouldLoot)
            {
                LootDiag($"[RynthAi] Corpse loot: dropping claim on 0x{(uint)_targetCorpseId:X8} — killer='{claimDecision.KillerName}' mode='{claimDecision.ModeName}' reason='{claimDecision.Reason}'");
                ResetCorpseTarget();
                return;
            }
        }

        if (HasCorpseTimedOut(settings, now))
        {
            AbandonCurrentCorpse(now, "timeout");
            return;
        }

        WorldObject? targetCorpse = _objectCache[_targetCorpseId];
        if (targetCorpse == null || targetCorpse.ObjectClass != AcObjectClass.Corpse)
        {
            MarkCorpseCooldown(_targetCorpseId, 3000);
            ResetCorpseTarget();
            return;
        }

        // STEP 4: BotAction is not written here any more. The arbiter already
        // decided Looting this tick (HasLootWork saw this corpse in range before
        // the claim), and the "don't override Buffing" special case this branch
        // encoded is now just the fixed priority order in Decide().

        double distanceMeters = _objectCache.Distance(unchecked((int)_playerId), _targetCorpseId);
        if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters) || distanceMeters == double.MaxValue)
            return;

        if (distanceMeters > Math.Max(maxMeters * 1.5, maxMeters + 2.0))
        {
            MarkCorpseCooldown(_targetCorpseId, 3000);
            ResetCorpseTarget();
            return;
        }

        // An open goes out only once the arbiter has given Looting the tick. A corpse claimed on
        // the tick a fight ended (Combat still owned it) used to be opened at once, and the
        // arbiter's nav stop (StopCompletely) and Combat's idle peace-mode swap followed 3-30 ms
        // later. On ACE both cancel the server's walk/turn to the corpse - a mode change runs
        // HandleActionCancelAttack -> cancel_moveto - so that open could only fail (2026-10-06
        // 06:18:44, the first failed open of the day's stall). One tick later the stop goes
        // out first (PauseNavigationForCorpse) and the open after it.
        if (_activity != BotActivity.Looting)
        {
            // Combat (or anything else) owns the tick: the claim waits, its clock stopped.
            FreezeCorpseTimeout(now);
            return;
        }
        // The salvage panel is opening (UST used, execute not sent yet): the client would swallow
        // the use (SalvageManager.PanelPending). Wait, with the claim's timeout clock stopped (this
        // sits before the freeze reset below so the frozen age carries across held ticks).
        if (_salvageManager?.PanelPending == true)
        {
            FreezeCorpseTimeout(now);
            if (!_openHeldBySalvageLogged)
            {
                _openHeldBySalvageLogged = true;
                LootDiag($"[RynthAi] Corpse loot: open of 0x{(uint)_targetCorpseId:X8} waits for the salvage panel (UST used, salvage not sent yet).");
            }
            return;
        }
        _openHeldBySalvageLogged = false;
        _corpseHoldFrozenAge = -1;

        // One server action at a time (2026-10-06, ActionPacer): no open while the server is still
        // answering another action - a kit, a cast, a stance change, or the last open itself (a
        // re-sent open restarts the server's walk and fails the first one's callback). The claim's
        // 12 s timeout keeps running (the longest wait here is the 5 s limit on an open).
        if (!ActionPacer.MayStart(Host, "Loot", HealNeed.None, out _))
            return;
        // The last corpse's close is not answered yet (CloseGroundContainer, ~40 ms): a use now makes
        // the client send its own NoLongerViewingContents for that corpse too (flight recorder
        // 2026-10-06 07:57:09.801: the engine's close, then the client's, beside the next use).
        if (_closeSentFor != 0 && _closeSentFor != _targetCorpseId && now - _closeSentAt < CloseAnswerWaitMs
            && Host.HasGetGroundContainerId && unchecked((int)Host.GetGroundContainerId()) == _closeSentFor)
            return;

        // Peace Mode When Idle: into peace before the open, not during it. Combat holds its own
        // idle swap while looting has work (LootHoldsIdlePeace: a mode change cancels the
        // server's walk/turn to a corpse being opened), so the character looted in combat
        // stance (2026-10-09). Here no open is out yet, so the swap can't cancel anything.
        if (WantsPeaceBeforeOpen(settings, now))
            return;

        // Farther than reach: just use the corpse. The server walks the character to it and turns
        // it (as a player's double-click does); steering there ourselves looked awkward and
        // turned slowly (owner, 2026-10-04). Our own movement stays off so it can't cancel that
        // walk, and the use is re-sent only after about the walk's time, not every 500 ms
        // (each re-send restarts the server's walk).
        if (distanceMeters > minMeters)
        {
            StopCorpseMovement();
            long walkMs = (long)(distanceMeters / FarOpenWalkMetersPerSec * 1000.0) + FarOpenSlackMs;
            if (_lastCorpseOpenAttemptAt == 0 || now - _lastCorpseOpenAttemptAt >= walkMs)
            {
                if (_busyCount > 0 && now - _lastCorpseOpenAttemptAt < OpenBusyWaitMaxMs) return;
                if (AttemptOpenCorpse(_targetCorpseId))
                    _lastCorpseOpenAttemptAt = now;
            }
            return;
        }

        StopCorpseMovement();

        // Don't stack a corpse-open on top of a still-pending item action. AC
        // allows one at a time; firing the open every 500ms regardless of busy
        // spikes the count to 5-6 during loot+salvage and trips "you can only
        // move or use one item at a time". Wait for busy to clear — but only up
        // to OpenBusyWaitMaxMs, so a leaked/desynced count (off-thread casts,
        // salvage) can't stall looting forever; the cast-resolve reset and the
        // busy watchdog clear the leak independently.
        bool throttleOk = _lastCorpseOpenAttemptAt == 0
            || now - _lastCorpseOpenAttemptAt >= Math.Max(500, settings.LootOpenRetryMs);
        if (throttleOk)
        {
            if (_busyCount > 0)
            {
                if (_corpseOpenBusyWaitSince == 0) _corpseOpenBusyWaitSince = now;
                if (now - _corpseOpenBusyWaitSince < OpenBusyWaitMaxMs)
                    return; // busy — hold the open briefly
            }
            _corpseOpenBusyWaitSince = 0;
            if (AttemptOpenCorpse(_targetCorpseId))
                _lastCorpseOpenAttemptAt = now;
        }
    }

    private long _corpseOpenBusyWaitSince;
    private bool _openHeldBySalvageLogged;

    // Peace before the open: one request per claimed corpse, only with no monster within
    // LootStarveCloseYards (the same test that gives looting its turn while combat still has a
    // target), and the open goes out anyway after PeaceBeforeOpenMaxMs.
    private const long PeaceBeforeOpenMaxMs = 2500;
    private int _peaceBeforeOpenFor;
    private long _peaceBeforeOpenSince;
    /// <summary>The peace request for _peaceBeforeOpenFor was accepted by the engine (a refusal is retried).</summary>
    private bool _peaceSent;

    /// <summary>True while the open should wait for the peace-mode swap it just asked for.</summary>
    private bool WantsPeaceBeforeOpen(LegacyUiSettings settings, long now)
    {
        // Not HasTargets: a target combat locked but can't reach kept it on (2026-10-09, Drakkon looted
        // in Magic mode with a target unattacked for 82 s and nothing within 5 yd).
        if (!settings.PeaceModeWhenIdle || _combatManager == null || _combatManager.HasCloseThreat(LootStarveCloseYards)
            || _combatManager.CurrentCombatMode == CombatMode.NonCombat
            || _lastCorpseOpenAttemptAt != 0)   // an open is already out: no mode change now
            return false;
        if (_peaceBeforeOpenFor != _targetCorpseId)
        {
            _peaceBeforeOpenFor = _targetCorpseId;
            _peaceBeforeOpenSince = now;   // the ceiling counts from the first ask
            _peaceSent = false;
        }
        // One accepted request per corpse. A refusal (the engine turns stance changes away around a
        // teleport) or a pacing hold is asked again on the next call, inside the same ceiling.
        if (!_peaceSent)
            _peaceSent = Host.ChangeCombatModeFor(CombatMode.NonCombat, "Loot", "peace before opening the corpse (Peace Mode When Idle)");
        return now - _peaceBeforeOpenSince < PeaceBeforeOpenMaxMs;
    }
    // Wait for busy to genuinely clear before opening the next corpse. The backstop
    // only exists so a leaked/desynced count can't freeze looting — CheckBusyTimeout
    // now force-clears a leak in 2s during the corpse approach/open phase (lootOpenUrgent),
    // so this sits just above that: by the time we'd give up waiting, the watchdog has
    // already zeroed the phantom count and the open proceeds cleanly. Was 8000ms, which
    // cost ~8s of dead time per corpse whenever a cast had leaked the busy count.
    private const long OpenBusyWaitMaxMs = 2500;
    // Far corpse: the server walks us there (running ~4 m/s; slack for the turn and the open).
    private const double FarOpenWalkMetersPerSec = 3.0;
    private const long FarOpenSlackMs = 1500;

    private bool ShouldDriveCorpseOpening(LegacyUiSettings settings)
    {
        if (!settings.IsMacroRunning || !settings.EnableLooting)
            return false;

        if (BoostNavHoldsLoot(settings))
            return false;

        // Combat is #1 priority — block new corpse claims while combat is
        // enabled and has ANY targets (scanned or actively engaged).
        // If combat is disabled, targets don't matter — loot freely.
        // Exception: if we already have an opened container, finish looting it
        // since that involves no movement and doesn't conflict with combat.
        if (settings.EnableCombat
            && _combatManager?.HasTargets == true
            && !settings.BoostLootPriority
            && !_lootStarveTurn)
        {
            if (_openedContainerId == 0)
                return false;
        }

        return settings.BotAction == "Default"
            || settings.BotAction == "Navigating"
            || settings.BotAction == "Looting"
            || settings.BotAction == "Combat";
    }

    private bool TryFindNearestCorpse(double maxMeters, out WorldObject? corpse, out double distanceMeters)
    {
        corpse = null;
        distanceMeters = double.MaxValue;
        if (_objectCache == null || _playerId == 0)
            return false;

        long now = CorpseNowMs;
        int playerId = unchecked((int)_playerId);
        WorldObject? ownCorpse = null;
        double ownDistance = double.MaxValue;
        foreach (WorldObject candidate in _objectCache.GetLandscapeObjects())
        {
            if (candidate.ObjectClass != AcObjectClass.Corpse)
                continue;

            if (candidate.Id == _targetCorpseId || _completedCorpses.ContainsKey(candidate.Id))
                continue;

            if (_corpseCooldownUntil.TryGetValue(candidate.Id, out long blockedUntil))
            {
                if (blockedUntil > now)
                    continue;

                _corpseCooldownUntil.Remove(candidate.Id);
            }

            CorpseLootDecision decision = EvaluateCorpseLootDecision(candidate);
            if (!decision.ShouldLoot)
            {
                if (_ownershipSkipLogged.Add(candidate.Id))
                {
                    LootDiag($"[RynthAi] Corpse loot: ownership skip 0x{(uint)candidate.Id:X8} name='{candidate.Name}' mode='{decision.ModeName}' killer='{decision.KillerName}' me='{decision.PlayerName}' reason='{decision.Reason}'");
                }
                continue;
            }

            double dist = _objectCache.Distance(playerId, candidate.Id);
            if (dist > maxMeters)
                continue;

            // Your own corpse beats any other corpse in range, whatever the distances.
            if (decision.IsOwnDeathCorpse)
            {
                if (ownCorpse == null || dist < ownDistance) { ownCorpse = candidate; ownDistance = dist; }
                continue;
            }

            if (dist >= distanceMeters)
                continue;

            corpse = candidate;
            distanceMeters = dist;
        }

        if (ownCorpse != null)
        {
            corpse = ownCorpse;
            distanceMeters = ownDistance;
        }
        return corpse != null;
    }

    private bool ShouldLootCorpse(WorldObject corpse)
    {
        return EvaluateCorpseLootDecision(corpse).ShouldLoot;
    }

    /// <summary>
    /// Asks the engine to identify a corpse so its LongDesc (the killer name) arrives. Only a request the
    /// engine accepted counts: it refuses RequestId around a teleport or a death and drops stale queued
    /// requests, so a refused or dropped one is asked again. A refused one is retried every
    /// CorpseIdRefusedRetryMs; an accepted one with no data is asked again every CorpseIdReaskMs, and only for
    /// the corpse the bot is targeting or has open (any other corpse in range gets one accepted request, not a
    /// stream), at most CorpseIdMaxReasks times, and only while the loot mode needs the killer name (My Kills
    /// Only, Fellowship Kills). All Corpses still gets the one first request (the radar colours own kills from
    /// it) but never a stream of them; Recover My Corpse keeps its own 5 s ask (RequestOwnCorpseId). At most
    /// CorpseIdPerTick requests are accepted per scan tick, so a refusal window ending with many uncached
    /// corpses in range does not burst.
    /// </summary>
    private void TryRequestCorpseId(WorldObject corpse, string longDesc)
    {
        if (!string.IsNullOrWhiteSpace(longDesc) || !Host.HasRequestId) return;
        long idNow = ActionPacer.NowMs();
        if (idNow - _corpseIdBudgetAt >= CorpseIdRefusedRetryMs) { _corpseIdAcceptedThisTick = 0; _corpseIdBudgetAt = idNow; }   // evaluations outside a scan must not exhaust it for good
        bool candidate = corpse.Id == _targetCorpseId || (_openedContainerId != 0 && corpse.Id == _openedContainerId);
        int asks = _corpseIdAskCount.TryGetValue(corpse.Id, out int askCount) ? askCount : 0;
        bool due = !_corpseIdRequested.Contains(corpse.Id)
                   || (candidate && asks <= CorpseIdMaxReasks && CorpseNameNeeded()
                       && _corpseIdAskedAt.TryGetValue(corpse.Id, out long askedAt) && idNow - askedAt >= CorpseIdReaskMs);
        if (!due || _corpseIdAcceptedThisTick >= CorpseIdPerTick) return;
        if (_corpseIdRefusedAt.TryGetValue(corpse.Id, out long refusedAt) && idNow - refusedAt < CorpseIdRefusedRetryMs) return;
        if (Host.RequestId(unchecked((uint)corpse.Id)))
        {
            _corpseIdRequested.Add(corpse.Id);
            _corpseIdAskedAt[corpse.Id] = idNow;
            _corpseIdAskCount[corpse.Id] = asks + 1;
            _corpseIdRefusedAt.Remove(corpse.Id);
            _corpseIdAcceptedThisTick++;
        }
        else
        {
            _corpseIdRefusedAt[corpse.Id] = idNow;
        }
    }

    /// <summary>The loot mode reads the killer name (My Kills Only, Fellowship Kills); All Corpses does not.</summary>
    private bool CorpseNameNeeded() => (_dashboard?.Settings?.LootOwnership ?? 0) < 2;

    private CorpseLootDecision EvaluateCorpseLootDecision(WorldObject? corpse)
    {
        var settings = _dashboard?.Settings;
        if (corpse == null)
        {
            return new CorpseLootDecision
            {
                ShouldLoot = false,
                ModeName = "Unknown",
                Reason = "Corpse is not available."
            };
        }

        int lootOwnership = settings?.LootOwnership ?? 0;
        string modeName = GetLootOwnershipModeName(lootOwnership);
        string longDesc = corpse.Values(StringValueKey.LongDesc, string.Empty);

        // Corpse LongDesc is only available after the object is identified.
        // Request ID so the killer name is populated for the next check.
        TryRequestCorpseId(corpse, longDesc);
        string killerName = ExtractCorpseKillerName(longDesc) ?? string.Empty;
        string myName = GetPlayerNameForLootOwnership();
        bool inFellowship = _fellowshipTracker?.IsInFellowship == true;
        bool killerIsFellow = !string.IsNullOrWhiteSpace(killerName) && _fellowshipTracker?.IsMember(killerName) == true;

        if (settings == null)
        {
            return new CorpseLootDecision
            {
                ShouldLoot = false,
                ModeName = modeName,
                Reason = "Settings are not ready yet.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        // Your own death corpse (LootOwnCorpse): recovered whole, whatever Loot From says. A
        // corpse with your name whose ID hasn't landed yet waits for it (no profile looting of
        // it meanwhile); anything else falls through to the ownership rules below unchanged.
        if (settings.LootOwnCorpse)
        {
            OwnCorpseMatch own = MatchOwnDeathCorpse(corpse.Name, myName, longDesc);
            if (own == OwnCorpseMatch.Yes)
            {
                return new CorpseLootDecision
                {
                    ShouldLoot = true,
                    ModeName = "Own corpse",
                    Reason = "Your own corpse: every item on it is recovered (Recover My Corpse).",
                    KillerName = killerName,
                    PlayerName = myName,
                    LongDesc = longDesc,
                    InFellowship = inFellowship,
                    KillerIsFellow = killerIsFellow,
                    IsOwnDeathCorpse = true
                };
            }
            if (own == OwnCorpseMatch.Pending)
            {
                RequestOwnCorpseId(corpse.Id);
                return new CorpseLootDecision
                {
                    ShouldLoot = false,
                    ModeName = "Own corpse",
                    Reason = "Named like your corpse; waiting for its ID (the 'Killed by' line) before recovering it.",
                    KillerName = killerName,
                    PlayerName = myName,
                    LongDesc = longDesc,
                    InFellowship = inFellowship,
                    KillerIsFellow = killerIsFellow
                };
            }
        }

        if (lootOwnership >= 2)
        {
            return new CorpseLootDecision
            {
                ShouldLoot = true,
                ModeName = modeName,
                Reason = "Loot From is set to All Corpses.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        if (lootOwnership <= 0)
        {
            if (string.IsNullOrWhiteSpace(killerName))
            {
                // LongDesc not loaded yet — ID was requested above.
                // Treat as tentatively lootable so the corpse gets claimed;
                // once the ID response arrives the next check will verify ownership.
                return new CorpseLootDecision
                {
                    ShouldLoot = true,
                    ModeName = modeName,
                    Reason = "Corpse not yet identified — treating as lootable pending ID response.",
                    KillerName = killerName,
                    PlayerName = myName,
                    LongDesc = longDesc,
                    InFellowship = inFellowship,
                    KillerIsFellow = killerIsFellow
                };
            }

            bool matchesPlayer = !string.IsNullOrWhiteSpace(myName)
                && NormalizeCharName(killerName).Equals(NormalizeCharName(myName), StringComparison.OrdinalIgnoreCase);
            return new CorpseLootDecision
            {
                ShouldLoot = matchesPlayer,
                ModeName = modeName,
                Reason = matchesPlayer
                    ? "Corpse killer matches the current character."
                    : "Corpse killer does not match the current character.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        if (!string.IsNullOrWhiteSpace(killerName)
            && !string.IsNullOrWhiteSpace(myName)
            && NormalizeCharName(killerName).Equals(NormalizeCharName(myName), StringComparison.OrdinalIgnoreCase))
        {
            return new CorpseLootDecision
            {
                ShouldLoot = true,
                ModeName = modeName,
                Reason = "Corpse killer matches the current character.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        if (inFellowship)
        {
            if (killerIsFellow)
            {
                return new CorpseLootDecision
                {
                    ShouldLoot = true,
                    ModeName = modeName,
                    Reason = "Corpse killer is a tracked fellowship member.",
                    KillerName = killerName,
                    PlayerName = myName,
                    LongDesc = longDesc,
                    InFellowship = inFellowship,
                    KillerIsFellow = killerIsFellow
                };
            }

            // When fellowship membership data lags behind the live client, allow the server
            // to be the final authority instead of skipping potentially valid fellowship corpses.
            return new CorpseLootDecision
            {
                ShouldLoot = true,
                ModeName = modeName,
                Reason = "In fellowship mode, RynthAi will let the server decide when killer ownership is not fully resolved yet.",
                KillerName = killerName,
                PlayerName = myName,
                LongDesc = longDesc,
                InFellowship = inFellowship,
                KillerIsFellow = killerIsFellow
            };
        }

        bool killerKnown = !string.IsNullOrWhiteSpace(killerName);
        bool fallbackMatchesPlayer = killerKnown
            && !string.IsNullOrWhiteSpace(myName)
            && NormalizeCharName(killerName).Equals(NormalizeCharName(myName), StringComparison.OrdinalIgnoreCase);
        bool tentative = !killerKnown; // LongDesc not loaded yet
        return new CorpseLootDecision
        {
            ShouldLoot = fallbackMatchesPlayer || tentative,
            ModeName = modeName,
            Reason = tentative
                ? "Corpse not yet identified — treating as lootable pending ID response."
                : fallbackMatchesPlayer
                    ? "Not in a fellowship, so the corpse is treated as your own kill."
                    : "Not in a fellowship and the corpse killer does not match the current character.",
            KillerName = killerName,
            PlayerName = myName,
            LongDesc = longDesc,
            InFellowship = inFellowship,
            KillerIsFellow = killerIsFellow
        };
    }

    private string GetPlayerNameForLootOwnership()
    {
        if (_playerId == 0)
            return string.Empty;

        return Host.TryGetObjectName(_playerId, out string playerName) && !string.IsNullOrWhiteSpace(playerName)
            ? playerName
            : string.Empty;
    }

    /// <summary>Strip GM/admin sigils (leading '+', surrounding whitespace) so admin characters
    /// whose player-object name is "+Buffi" match the "Killed by Buffi." string in corpse LongDesc.</summary>
    private static string NormalizeCharName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string trimmed = name.Trim();
        // Admin/GM sigils appear at the start of the display name (+, @, etc.) but
        // are stripped out of the "Killed by X" string the server writes to LongDesc.
        while (trimmed.Length > 0 && (trimmed[0] == '+' || trimmed[0] == '@' || trimmed[0] == '#'))
            trimmed = trimmed.Substring(1);
        return trimmed.Trim();
    }

    /// <summary>
    /// X from the "Killed by X." line of a corpse's LongDesc, or null. Only that line counts:
    /// the server appends more lines ("Corpse will decay in 56s") as a corpse ages, and reading
    /// them as part of the name failed the own-kill match, so late-reached own kills were
    /// skipped unopened (2026-10-06, Silentkelpie). Shared with the radar's killer column.
    /// </summary>
    internal static string? ExtractCorpseKillerName(string? longDesc)
    {
        if (string.IsNullOrWhiteSpace(longDesc))
            return null;

        const string prefix = "Killed by ";
        int idx = longDesc.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        string remainder = longDesc[(idx + prefix.Length)..];
        int lineEnd = remainder.IndexOfAny(new[] { '\r', '\n' });
        if (lineEnd >= 0)
            remainder = remainder[..lineEnd];
        remainder = remainder.Trim().TrimEnd('.').Trim();
        return remainder.Length == 0 ? null : remainder;
    }

    private static string GetLootOwnershipModeName(int lootOwnership)
    {
        return lootOwnership switch
        {
            <= 0 => "My Kills Only",
            1 => "Fellowship Kills",
            _ => "All Corpses"
        };
    }

    private void TickOpenCorpseLooting(LegacyUiSettings settings)
    {
        int corpseId = _openedContainerId;
        if (corpseId == 0)
            return;

        if (_targetCorpseId == 0)
        {
            _targetCorpseId = corpseId;
            _corpseTargetSince = CorpseNowMs;
        }

        if (_completedCorpses.ContainsKey(corpseId))
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
            ResetCorpseTarget();
            return;
        }

        long now = CorpseNowMs;
        if (_openedContainerAt == 0)
            _openedContainerAt = now;

        // A fight has the tick: the corpse waits for it, its clock stopped (R-20261102-204309-206c).
        if (HoldOpenCorpseForFight(settings, corpseId, now))
            return;

        if (HasCorpseTimedOut(settings, now))
        {
            AbandonCurrentCorpse(now, "timeout");
            return;
        }

        long openAge = now - _openedContainerAt;

        // A pickup in flight: only its answer is looked at (one ownership read), not the whole
        // container. The snapshot below scans every object the cache knows with an ownership
        // probe each and refreshes every item's identity - for a 100-item chest that ran on
        // every tick of every pickup. A move is only ever sent after the contents were seen,
        // so the observed-inventory gate below has already passed.
        if (_currentLootItemMovePending && _openedContainerInventoryObservedAt != 0)
        {
            TickPendingCorpsePickup(settings);
            if (_currentLootItemMovePending)
                return; // Still waiting for this pickup's answer
            if (_openedContainerId != corpseId)
                return; // the retry finished or paused the corpse (no pack had room)
            now = CorpseNowMs;
        }

        // Snapshot the corpse's contents ONCE per tick and reuse it everywhere below.
        // GetContainedItems scans the entire landscape+inventory set (200+ objects) with
        // a native ownership probe per object; this path used to call it up to 3× per tick
        // (fast-path probe, ID-request loop, eval loop) on the shared plugin tick thread.
        List<WorldObject> containedItems = _objectCache.GetContainedItems(corpseId).ToList();
        // Fill in names/classes the cache stored before they were readable (every tick,
        // so an item picks its name up as soon as the engine's snapshot has it).
        for (int i = 0; i < containedItems.Count; i++)
            containedItems[i] = _objectCache.RefreshIdentity(containedItems[i]);

        // Items the client has inside the corpse that the cache never classified (no create
        // processed for them) are not in containedItems at all, so they were never judged
        // and the corpse was finished without them. Ask the client what the corpse holds,
        // at most every ContentsSweepMs, and look the missing ones up.
        if (now - _lastContentsSweepAt >= ContentsSweepMs || _lastContentsSweepCorpse != corpseId)
        {
            _lastContentsSweepAt = now;
            _lastContentsSweepCorpse = corpseId;
            List<WorldObject> missed = _objectCache.RescueContainedItems(corpseId, containedItems);
            if (missed.Count > 0)
            {
                foreach (WorldObject wo in missed)
                    containedItems.Add(_objectCache.RefreshIdentity(wo));
                LootDiag($"[RynthAi] Corpse loot: found {missed.Count} item(s) on 0x{(uint)corpseId:X8} the object cache had missed: " +
                         string.Join(", ", missed.Select(w => $"0x{(uint)w.Id:X8} '{w.Name}' {w.ObjectClass}")) + ".");
            }
        }

        // Fast path: items for this corpse are already in the cache. AC's CreateObject
        // burst populates the corpse's contents during the landscape sweep that precedes
        // the ViewContents packet, so OnUpdateObjectInventory effectively never fires for
        // corpses — leaving the settle+probe gates below as pure dead time (~400ms/corpse).
        // When the cache already has contents, mark observed and proceed immediately.
        if (_openedContainerInventoryObservedAt == 0 && containedItems.Count > 0)
        {
            _openedContainerInventoryObservedAt = now;
            LootDiag($"[RynthAi] {LootTag}: cache pre-populated for 0x{(uint)corpseId:X8} at {openAge}ms; skipping inv-wait.");
        }
        else
        {
            // Brief settle after open for client to populate container
            if (openAge < Math.Max(50, settings.LootContentSettleMs))
                return;

            // Wait for inventory update event from server
            if (_openedContainerInventoryObservedAt == 0)
            {
                if (openAge < Math.Max(300, settings.LootEmptyCorpseMs))
                    return;
                _openedContainerInventoryObservedAt = _openedContainerAt;
                LootDiag($"[RynthAi] {LootTag}: no inventory update for 0x{(uint)corpseId:X8}; probing after {openAge}ms.");
            }
        }

        // Handle pending pickup confirmation (don't block evaluation — just check)
        if (_currentLootItemMovePending)
        {
            TickPendingCorpsePickup(settings);
            if (_currentLootItemMovePending)
                return; // Still waiting for this pickup to clear
            if (_openedContainerId != corpseId)
                return; // the retry finished or paused the corpse (no pack had room)
        }

        // Your own corpse: every item, no profile (RynthAiPlugin.OwnCorpse.cs).
        if (IsOwnDeathCorpseId(settings, corpseId))
        {
            TickOwnCorpseRecovery(settings, corpseId, containedItems, openAge, now);
            return;
        }

        // Pre-classify items and batch-request IDs only for items that need them.
        //
        // Each Host.RequestId() call increments ClientUISystem::m_cBusy (busy count).
        // After MarkCorpseCompleted, the _busyCount > 0 guard in TickCorpseOpening
        // blocks AttemptOpenCorpse until every ID response clears the count — which
        // takes 2-4 seconds for a full corpse batch.  Fix: classify items whose class
        // has no stat-based rules immediately (no ID needed); only request ID for items
        // where the profile has numeric/spell conditions that require appraisal data.
        if (!_corpseIdsRequested)
        {
            _corpseIdsRequested = true;
            _corpseIdsRequestedAt = now;
            _lootEvalLogged.Clear();
            _needsIdCache.Clear();
            _corpseItemIdReaskedAt.Clear();
            _manaStoneCapLoggedFor = 0;
            int requested = 0;
            int preClassified = 0;

            foreach (WorldObject item in containedItems)
            {
                if (_processedCorpseItems.Contains(item.Id)) continue;

                bool hasData = Host.HasHasAppraisalData && Host.HasAppraisalData(unchecked((uint)item.Id));
                if (hasData) continue; // Already has full appraisal — will evaluate below

                bool hasName = !string.IsNullOrWhiteSpace(item.Name);

                // Items with a name whose class has no stat-based loot rules can be
                // classified immediately from name/class data — no ID request needed.
                // Not while the class is still Unknown: a class rule would reject the item
                // here and mark it processed before its type was readable.
                if (hasName && item.ObjectClass != AcObjectClass.Unknown && !ItemNeedsAppraisalForLoot(item))
                {
                    // Pre-classify: if no match, mark processed so we never re-evaluate.
                    // Items that DO match (e.g. name-only rules) stay unprocessed and will
                    // be picked up in the eval loop below on this same tick.
                    if (!ClassifyItemAgainstProfile(item, settings, out _, out _, out _))
                        _processedCorpseItems.Add(item.Id);
                    preClassified++;
                    continue; // No busy-count-inflating ID request
                }

                // Item needs appraisal for accurate classification — request ID.
                Host.RequestId(unchecked((uint)item.Id));
                requested++;
            }

            if (requested > 0)
                LootDiag($"[RynthAi] {LootTag}: requested {requested} item ID(s) (pre-classified {preClassified}) for 0x{(uint)corpseId:X8}.");
            else if (preClassified > 0)
                LootDiag($"[RynthAi] {LootTag}: pre-classified {preClassified} item(s) without ID for 0x{(uint)corpseId:X8}.");
            // Fall through either way: items already decided (their appraisal is here, or the
            // profile needs none for them) are picked up now while the other IDs stream in;
            // the ones still waiting are held below and judged as their data arrives.
        }

        // Evaluate ALL items with data in one pass — do NOT gate evaluation on
        // busy count.  Only the actual UseObject pickup needs busy == 0.
        // This lets us classify every item on the corpse as soon as ID data
        // arrives, instead of waiting for each RequestId to fully clear.
        int visibleItemCount = 0;
        int pendingDataCount = 0;
        bool assessTimedOut = now - _corpseIdsRequestedAt >= Math.Max(100, settings.LootAssessWindowMs);

        int    firstMatchId     = 0;
        string matchItemName    = string.Empty;
        string matchActionLabel = string.Empty;
        string matchRuleLabel   = string.Empty;
        bool   matchIsSalvage   = false;

        foreach (WorldObject item in containedItems)
        {
            visibleItemCount++;
            if (_processedCorpseItems.Contains(item.Id))
                continue;

            bool hasAppraisalData = Host.HasHasAppraisalData && Host.HasAppraisalData(unchecked((uint)item.Id));
            bool hasName = !string.IsNullOrWhiteSpace(item.Name);

            // Hold an item until we can judge it correctly. Stat properties
            // (ArmorLevel, damage, total ratings, spells) arrive in the appraisal/
            // RequestId response, NOT the basic create — before that, item.Values(...)
            // reads 0, so a stat-gated loot-snob rule fails and the item would be marked
            // processed and left on the corpse: a silent loot miss. So wait while the item
            // lacks appraisal AND either has no name yet OR the profile needs appraisal to
            // classify its class — bounded by the assess window, then best-effort below.
            //
            // A missing NAME holds the item even once appraised: a blank name fails every
            // name rule, and the item would be marked processed and left for good - 3 of 4
            // items on 2026-09-27, the appraisal having arrived before the engine's name
            // snapshot caught up. Allow NameWaitMs past the assess window for the name.
            if (!hasName)
            {
                if (now - _corpseIdsRequestedAt < Math.Max(100, settings.LootAssessWindowMs) + NameWaitMs)
                {
                    pendingDataCount++;
                    continue;
                }
                // Still nameless well past the window — skip this item (and say so: it used to
                // leave nothing in the log, so a missed item couldn't be traced).
                LootDiag($"[LootEval] 0x{(uint)item.Id:X8} on 0x{(uint)corpseId:X8} still has no name after the wait (value={item.Values(19, 0)} type=0x{item.Values(1, 0):X} wcid={item.Values(unchecked((int)WorldObjectCache.DecalTypeKey), 0)}) -> left");
                _processedCorpseItems.Add(item.Id);
                continue;
            }
            if (!hasAppraisalData && ItemNeedsAppraisalForLoot(item))
            {
                if (!assessTimedOut)
                {
                    pendingDataCount++;
                    continue;
                }
                // The ID often lands after the assess window (about 350 ms in the live
                // logs against a 200 ms window). Judged before it, every stat reads 0, a
                // stat rule fails and the item was marked processed and left for good.
                // Keep holding it for AppraisalWaitMs, but never into the corpse timeout.
                bool beforeTimeout = now - _corpseTargetSince < GetCorpseTimeoutMs(settings) - AppraisalHoldTimeoutMarginMs;
                if (now - _corpseIdsRequestedAt < Math.Max(100, settings.LootAssessWindowMs) + AppraisalWaitMs && beforeTimeout)
                {
                    pendingDataCount++;
                    continue;
                }
                // Still no appraisal. A rule that decides this item needs it, so judging it now
                // reads its stats as 0 and lets a later rule take it: a +9 crossbow went to the
                // Salvage rule on its gold (until 2026-10-31, "best-effort" here). Ask again and
                // keep holding while the corpse lasts; then leave it, never classify it without.
                // (An engine that can't say whether the ID is in still judges it best-effort.)
                if (Host.HasHasAppraisalData)
                {
                    if (beforeTimeout)
                    {
                        ReaskCorpseItemId(item, now);
                        pendingDataCount++;
                        continue;
                    }
                    string leftVerdict = "left: no ID came, and a rule that decides it needs one";
                    if (!_lootEvalLogged.TryGetValue(item.Id, out string? leftLogged) || leftLogged != leftVerdict)
                    {
                        _lootEvalLogged[item.Id] = leftVerdict;
                        LootDiag($"[LootEval] '{item.Name}' cls={item.ObjectClass} id=0x{(uint)item.Id:X8} value={item.Values(19, 0)} appraised=False timedOut=True -> {leftVerdict}");
                    }
                    _processedCorpseItems.Add(item.Id);
                    continue;
                }
            }

            // A keeper already judged on this open is judged again only when its turn comes (it
            // is the first match): that fresh pass is what Keep # and the mana stone cap count
            // against. Re-running the profile on every waiting keeper each pass did nothing else.
            if (firstMatchId != 0 && _lootEvalLogged.TryGetValue(item.Id, out string? judged) && judged.StartsWith("KEEP", StringComparison.Ordinal))
                continue;

            // Evaluate against loot profile — pure classification, no UseObject.
            bool classified = ClassifyItemAgainstProfile(item, settings, out string actionLabel, out bool isSalvage, out string ruleLabel);
            // Nothing to salvage with: an item only a Salvage rule wants stays on the corpse.
            bool noUst = classified && isSalvage && _salvageManager != null && !_salvageManager.HasUst();
            if (noUst) { classified = false; NoteNoUstLeft(item.Name); }

            // Per-item decision to the LOG, not just chat. The "Loot rule:" line
            // below uses ChatLine (Host.WriteToChat), so nothing about WHY an item
            // was taken has ever reached RynthCore.log — leaving "it loots things
            // that don't match the profile" undiagnosable after the fact.
            //
            // appraised= is the key field. Stat properties (ArmorLevel, damage,
            // ratings, spells) arrive in the RequestId response, not the create,
            // so before that every item.Values(...) reads 0 — and a VTank
            // LongValKeyLE/NE condition compares against 0 and PASSES. The
            // best-effort path below deliberately classifies without appraisal
            // once the assess window expires, so an unappraised item can match a
            // rule it should not. If keeps correlate with appraised=False, that
            // is the cause.
            // Once per item per open, and again only if the verdict changes (a matched item was
            // re-printed before every pickup: ~200 lines for a 20-keeper chest).
            string verdict = classified ? $"KEEP [{actionLabel}] rule='{ruleLabel}'" : noUst ? $"leave (Salvage rule '{ruleLabel}', no UST carried)" : "leave";
            if (!_lootEvalLogged.TryGetValue(item.Id, out string? loggedVerdict) || loggedVerdict != verdict)
            {
                _lootEvalLogged[item.Id] = verdict;
                // A left item that a rule switched off in the profile would take says so: the log
                // of a "loot misses my wands" report then shows the rule (once per item per open).
                string offNote = !classified && !noUst && hasAppraisalData ? SwitchedOffNote(item, settings) : string.Empty;
                LootDiag($"[LootEval] '{item.Name}' cls={item.ObjectClass} id=0x{(uint)item.Id:X8} value={item.Values(19, 0)} stack={item.Values(12, 0)} type=0x{item.Values(1, 0):X} wcid={item.Values(unchecked((int)WorldObjectCache.DecalTypeKey), 0)} appraised={hasAppraisalData} timedOut={assessTimedOut} -> " + verdict + offNote);
            }

            if (classified)
            {
                // First match wins — save it for pickup below
                if (firstMatchId == 0)
                {
                    firstMatchId     = item.Id;
                    matchItemName    = item.Name ?? string.Empty;
                    matchActionLabel = actionLabel;
                    matchRuleLabel   = ruleLabel;
                    matchIsSalvage   = isSalvage;
                }
                // Don't break — keep evaluating to mark non-matches processed
            }
            else
            {
                // No match — mark processed so we never re-evaluate
                _processedCorpseItems.Add(item.Id);
            }
        }

        // Empty corpse check
        if (visibleItemCount == 0)
        {
            if (openAge >= Math.Max(200, settings.LootEmptyCorpseMs) && !LastPickupSettling(now))
            {
                LootDiag($"[RynthAi] {LootTag}: no items on 0x{(uint)corpseId:X8} after {openAge}ms; marking complete.");
                MarkCorpseCompleted(corpseId);
            }
            return;
        }

        // Matched item — attempt pickup. Its decision is final (its own appraisal is here, the
        // profile needs none for it, or the wait for it ran out), so it goes now: there used to
        // be a busy-count gate here for the assess window, but the RequestIds don't hold item
        // moves (AC's one-item-at-a-time gate is the pending inventory request, not m_cBusy).
        if (firstMatchId != 0)
        {
            // Keep room for weapon swaps: with a full pack the bow can't be put away to
            // switch to the wand, so buffs and spell heals stop (2026-09-28 death). Leave
            // the corpse rather than filling the last slots; -1 = side-pack capacity unknown.
            int freeSlots = WorldObjectCache.CountFreeItemSlots(Host, _objectCache);
            if (freeSlots >= 0 && freeSlots < WorldObjectCache.LootReserveFreeSlots)
            {
                if (LastPickupSettling(now))
                    return; // the last item lands first, then the corpse closes
                LootDiag($"[RynthAi] {LootTag}: skipping '{matchItemName}' on 0x{(uint)corpseId:X8} — {freeSlots} free slot(s), keeping {WorldObjectCache.LootReserveFreeSlots} for weapon swaps.");
                WorldObjectCache.WarnPackFull(Host, $"looting paused with {freeSlots} free slot(s) so weapon swaps still work");
                MarkCorpseCompleted(corpseId);
                return;
            }

            // The character still animates. Right after a pickup's answer that is its "Return"
            // (standing up): ACE queues a move that arrives then and runs it the moment the
            // Return ends (Player.HandleActionPutItemInContainer_Verify, NextPickup), so the next
            // move goes out now - one, never two. Any other animation: wait until it ends. Not the
            // jump's own motion after a jump-loot pickup the server answered with no bend-down: ACE
            // has nothing to play then (no Return, not busy) and takes a pickup in the air.
            bool pipelined = false;
            if (PickupAnimating(now) && !JumpPickupInAir(now))
            {
                if (!CanPipelinePickup(now))
                    return;
                pipelined = true;
            }

            // The server still owes an answer to a cast or use: it refuses a pickup now.
            if (PickupHeldByServerAction())
                return;

            // Jump-loot (see the jump below): the settings and the flight are read first, so a send
            // held for the landing does nothing else this tick (no chat line, no selection).
            bool jumpLoot = !pipelined && ShouldJumpLoot(settings);
            long jumpAge = jumpLoot ? ActionPacer.LootJumpAgeMs() : -1;
            bool stillInAir = jumpLoot && ActionPacer.LootJumpAirborne(Host, out _);
            if (stillInAir && FlightBudgetSpent(jumpAge, out long expectedAnswerMs))
            {
                // A pickup sent now would reach the server after the flight is over and get the
                // whole bend-down and Return: hold until LootJumpAirborne says landed (JumpLandMaxMs
                // at most); the path below then jumps and sends.
                if (!_flightHoldLogged)
                {
                    _flightHoldLogged = true;
                    LootDiag($"[RynthAi] {LootTag}: jump-loot: holding 0x{(uint)firstMatchId:X8} '{matchItemName}' for the landing - {jumpAge} ms into the flight and an answer takes about {expectedAnswerMs} ms, over the {LootJumpFlightBudgetMs} ms flight budget.");
                }
                return;
            }
            _flightHoldLogged = false;

            ChatLine($"[RynthAi] Loot rule: {matchItemName} -> [{matchActionLabel}] {matchRuleLabel}");
            _currentLootItemId = firstMatchId;
            RynthCore.Plugin.RynthAi.ItemInfo.SelfSelect.Mark(unchecked((uint)firstMatchId));
            Host.SelectItem(unchecked((uint)firstMatchId));
            LootDiag($"[RynthAi] {LootTag}: picking up 0x{(uint)firstMatchId:X8} '{matchItemName}' from corpse 0x{(uint)corpseId:X8}.");

            // Jump-loot: jump, then the move while the character is still in the air, so the server
            // skips the pickup bend-down (on ACE the pickup reads IsJumping in Player_Inventory's
            // GetPickupMotion, gated by the server setting allow_jump_loot, default true). The jump is
            // the keyboard one (TapJump, 0xF61B Jump, which ACE handles); JumpNonAutonomous (0xF7C9)
            // has no handler on ACE and did nothing. The
            // jump goes FIRST: ACE's jump handler stops any walk-to in progress, so a jump after the
            // move would cancel the pickup. Every pickup, a corpse's only match included (most
            // corpses hold one match, so a multi-item gate meant jump-loot almost never ran), with
            // stamina > 5 to cover the cost. Not for a pipelined move: it is already queued behind
            // the last pickup's Return, and a jump there would only disturb that queue. The tap jump
            // has a fixed height, so LootJumpHeight no longer has an effect. No second jump while
            // still in the air (on ACE an air jump only costs stamina); a jump taken is noted, so
            // combat and buffs wait for the landing (ACE refuses them in the air). A send late in
            // the flight was held above (FlightBudgetSpent).
            bool jumped = false;
            if (jumpLoot && !stillInAir)
            {
                try { jumped = Host.TapJump(); } catch { jumped = false; }
                if (jumped) ActionPacer.NoteLootJump(Host);
            }

            // Always a move into a pack, never a use: a use on a wearable corpse item is
            // get-and-wield, which put salvage jewellery on the character (2026-10-02).
            int errSeqAtSend = LastWeenieErrorSeq();
            bool stackable = IsStackAtSend(firstMatchId);
            if (pipelined)
            {
                _pipelinedAfterItemId = _settlingLootItemId;
                LootDiag($"[RynthAi] {LootTag}: queuing 0x{(uint)firstMatchId:X8} '{matchItemName}' behind 0x{(uint)_settlingLootItemId:X8}'s Return.");
            }
            LootPickupResult pickup = SendLootPickup(firstMatchId, matchItemName, corpseId);
            if (pickup == LootPickupResult.NoRoom || pickup == LootPickupResult.Refused)
                return;
            bool moved = pickup == LootPickupResult.Moved || pickup == LootPickupResult.Used;

            if (!moved)
            {
                // If the game rejected the request due to its own busy state, don't
                // count it as a failed attempt — just return and retry next tick.
                if (_busyCount > 0)
                    return;

                LootDiag($"[RynthAi] {LootTag}: use request failed for 0x{(uint)firstMatchId:X8}.");
                if (IncrementCorpseItemAttempt(firstMatchId) >= 3)
                    _processedCorpseItems.Add(firstMatchId);
                ResetCurrentLootItem();
                _lastLootActionAt = now;
                return;
            }
            // Record a loot-for-tap pickup as soon as it is sent: several resets (busy
            // force-clear, corpse closed or reopened) drop the pending pickup without
            // confirming it, and the item may still land in the pack. If it never
            // does, ManaStoneManager prunes the id.
            if (matchActionLabel == ManaTapActionLabel)
                _manaStoneManager?.MarkLootedForTap(firstMatchId, arrived: false);
            // Same for salvage: the confirm below can be lost the same ways, and an
            // unconfirmed salvage item used to sit in the pack for good. The salvage
            // manager's sweep queues it once it shows up in the pack.
            if (matchIsSalvage)
                _salvageManager?.NoteLootedForSalvage(unchecked((uint)firstMatchId), matchItemName);
            // Read (the built-in learn rule, or a loot profile's Read action): read it once it
            // is in the pack. Noted on send for the same reason as salvage above.
            if (matchActionLabel == ScrollLearner.ReadAction)
                _scrollLearner?.NoteLooted(firstMatchId, matchItemName, fromProfile: matchRuleLabel != ScrollLearner.RuleLabel);
            _currentLootItemMovePending      = true;
            _currentLootItemMoveRequestedAt  = now;
            // A queued move that the client or server turned away (the client's one-request
            // slot still held, or "You're too busy") must not cost the item one of its 3 tries.
            _currentLootItemMoveAttempts     = pipelined ? 0 : 1;
            _currentLootItemPipelined        = pipelined;
            _currentLootItemJumped           = jumped || stillInAir;
            _currentLootItemSentInAirWindow  = JumpPickupInAir(now);
            _currentLootItemStackable        = stackable;
            _currentLootItemErrSeqAtSend     = errSeqAtSend;
            NoteLootMoveSent();
            _currentLootItemFreeRetries      = 0;
            _currentLootItemActionLabel      = matchActionLabel;
            _currentLootItemName             = matchItemName;
            _currentLootItemIsSalvage        = matchIsSalvage;
            // Described now, while the item and its ID are surely in the cache (a stack can merge away).
            _currentLootItemChat             = DescribeLootPickup(firstMatchId, matchActionLabel, matchItemName,
                                                                  matchRuleLabel, matchIsSalvage, stackable);
            _lastLootActionAt                = now;
            return;
        }

        // All items evaluated, no matches left — mark corpse complete, once the last pickup
        // has played out (closing while it was under way: 2026-10-07).
        if (pendingDataCount == 0 && !LastPickupSettling(now))
        {
            LootDiag($"[RynthAi] {LootTag}: all {visibleItemCount} items on 0x{(uint)corpseId:X8} evaluated; marking complete.");
            MarkCorpseCompleted(corpseId);
        }
    }

    // A pickup that merges into a stack already in the pack (a Silver Pea onto the Silver Peas):
    // the server removes the corpse item (InventoryRemoveObject) and raises the pack stack's size,
    // so the picked object never shows up in the inventory and the arrival check waited out
    // PickupArriveMaxMs (in a client log, 39 stack pickups: median 5018 ms to confirm against
    // 2731 ms). The stacks of that name in the inventory are noted when the pickup goes out; one that has grown since counts as arrival.
    private readonly Dictionary<int, int> _mergeBaseline = new();

    private void NoteMergeTargets(int itemId, string itemName)
    {
        _mergeBaseline.Clear();
        if (_objectCache == null || string.IsNullOrEmpty(itemName)) return;
        foreach (WorldObject inv in _objectCache.GetDirectInventory(forceRefresh: true))
        {
            if (inv.Id == itemId || !string.Equals(inv.Name, itemName, StringComparison.Ordinal)) continue;
            _mergeBaseline[inv.Id] = Math.Max(1, inv.Values(LongValueKey.StackCount, 1));
        }
    }

    /// <summary>A same-name stack in the inventory has grown since the pickup went out.</summary>
    private bool MergedIntoInventoryStack(out int stackId)
    {
        stackId = 0;
        if (_objectCache == null) return false;
        foreach (var kv in _mergeBaseline)
        {
            WorldObject? stack = _objectCache[kv.Key];
            if (stack == null || !InPlayerInventory(stack.Container)) continue;
            if (Math.Max(1, stack.Values(LongValueKey.StackCount, 1)) > kv.Value)
            {
                stackId = kv.Key;
                return true;
            }
        }
        return false;
    }

    /// <summary>Longest a pickup waits on the motion queue (the engine's own hold is 2.5 s).</summary>
    internal const long PickupAnimMaxMs = 3000;

    /// <summary>After a pickup's answer: how long to wait for its Return to show when none is seen.</summary>
    internal const long PickupSettleMs = 1000;

    /// <summary>Off the corpse but not seen in the inventory: counted after this anyway.</summary>
    internal const long PickupArriveMaxMs = 4000;

    /// <summary>
    /// An answer sooner than this after the send isn't believed: ACE plays the whole pickup
    /// motion (about 0.75 s standing, longer in a combat stance) before it moves the item.
    /// Not for a jump-loot send's item seen in a pack (TickPendingCorpsePickup): with no motion
    /// it comes in about the round trip.
    /// </summary>
    internal const long PickupAnswerMinMs = 500;

    /// <summary>The next move is queued behind a Return only this soon after its answer.</summary>
    internal const long PipelineWindowMs = 1000;

    /// <summary>Resends after a "You're too busy" that don't count as one of the item's 3 tries.</summary>
    internal const int PickupFreeRetries = 3;

    /// <summary>Resends after a fight came in before the send's answer that don't count as one of the item's 3 tries.</summary>
    internal const int PickupFightResends = 2;

    /// <summary>A send with no answer this long past the answer wait, whose next send is still held, is logged (once per send).</summary>
    internal const long PickupResendWaitLogMs = 3000;

    private const uint WeenieErrorYoureTooBusy = 0x001D;
    private const uint EventInventoryServerSaveFailed = 0x00A0;

    /// <summary>The container is the character or a pack it carries.</summary>
    private bool InPlayerInventory(int containerId)
    {
        if (containerId == 0 || _playerId == 0 || _objectCache == null) return false;
        int player = unchecked((int)_playerId);
        return containerId == player || _objectCache.GetContainerId(containerId) == player;
    }

    /// <summary>
    /// The character still has motions queued (the engine's cast gate, GetCastBusyState):
    /// the bend-down of the last pickup is playing. False after PickupAnimMaxMs of it, so a
    /// stuck queue can't stop looting, and on an engine without the gate. Each answered pickup
    /// restarts that cap (queued pickups animate back to back for a whole chest).
    /// </summary>
    private bool PickupAnimating(long now)
    {
        bool busy;
        try { busy = Host.HasGetCastBusyState && Host.GetCastBusyState() != 0; } catch { busy = false; }
        if (!busy)
        {
            _pickupAnimSince = 0;
            return false;
        }
        if (_pickupAnimSince == 0)
            _pickupAnimSince = now;
        return now - _pickupAnimSince < PickupAnimMaxMs;
    }

    /// <summary>The engine's refusal counter now (0 when the engine can't say).</summary>
    private int LastWeenieErrorSeq()
    {
        try
        {
            return Host.HasGetLastWeenieError && Host.TryGetLastWeenieError(out int seq, out _, out _, out _) ? seq : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// The server turned the current item's move away since it was sent: "You're too busy"
    /// (it arrived while the last pickup's bend-down still ran) or InventoryServerSaveFailed
    /// naming the item. Someone else's refusal (a fizzle, a use) is not ours.
    /// </summary>
    private bool CurrentPickupRefused(out uint error, out uint objectId)
    {
        error = 0;
        objectId = 0;
        try
        {
            if (!Host.HasGetLastWeenieError || !Host.TryGetLastWeenieError(out int seq, out uint err, out uint evt, out uint obj))
                return false;
            if (seq == 0 || seq == _currentLootItemErrSeqAtSend)
                return false;
            error = err;
            objectId = obj;
            return err == WeenieErrorYoureTooBusy
                || (evt == EventInventoryServerSaveFailed
                    && (obj == unchecked((uint)_currentLootItemId) || (obj == 0 && IsQuestPickupRefusal(err))));
        }
        catch { return false; }
    }

    // ── Items the server won't hand over yet (2026-11-01, #rynth-issues "Stopped looting") ──
    // Conquest treasure maps have pickup timers: "after it reaches its loot attempt limit on
    // some obscure item with pickup timers ... it just selects another item in the container,
    // but doesn't pick it up". ACE runs the pickup motion, then checks the item's quest stamp
    // (Player.VerifyQuest) and, on a timer that has not run out, answers "You have solved this
    // quest too recently!" with InventoryServerSaveFailed(object 0, YouHaveSolvedThisQuestTooRecently)
    // (QuestManager.HandleSolveError) - naming no object. So RynthAi saw no answer at all, and
    // AC's own pending-request slot was never emptied (only an answer naming the item does that:
    // the engine's ClientActionGates), so the client dropped every move after it ("You can only
    // move or use one item at a time") until the engine's watchdog emptied it 10 s later. An
    // answer naming the item (ItemRequiresQuestToBePickedUp, or a timer refusal that does) was
    // taken as the pack's answer: after 3 tries the pack counted as full for 5 min, then the next
    // pack, until no pack was left and every later item was selected and never picked up.
    // Now a quest refusal leaves the item at once (a retry is refused the same way for minutes or
    // hours), blames no pack, and empties the client's slot when the answer named no object.
    private const uint WeenieErrorQuestSolvedTooRecently = 0x043E;
    private const uint WeenieErrorQuestSolvedTooManyTimes = 0x043F;
    private const uint WeenieErrorItemRequiresQuest = 0x0445;

    /// <summary>The server's answer says the item itself can't be picked up (yet), whatever pack it goes to.</summary>
    internal static bool IsQuestPickupRefusal(uint error) =>
        error is WeenieErrorQuestSolvedTooRecently or WeenieErrorQuestSolvedTooManyTimes or WeenieErrorItemRequiresQuest;

    private static string QuestPickupRefusalText(uint error) => error switch
    {
        WeenieErrorQuestSolvedTooRecently => "its pickup timer has not run out (\"solved this quest too recently\")",
        WeenieErrorQuestSolvedTooManyTimes => "it can't be picked up again (\"solved this quest too many times\")",
        WeenieErrorItemRequiresQuest => "it needs a quest flag to be picked up",
        _ => $"error 0x{error:X}",
    };

    /// <summary>
    /// A quest refusal that named no object leaves AC's pending inventory request in place (the
    /// client empties it only for an answer about that object). The server has answered, so the
    /// slot is emptied now, as AC's RecordResponse would have, rather than after the engine's 10 s
    /// watchdog: until then the client drops every move, and the next item's pickup with it.
    /// </summary>
    private void EmptyClientRequestAfterAnonymousRefusal(uint objectId)
    {
        if (objectId == 0 && Host.HasInvokeChatParser)
            Host.InvokeChatParser("/rc unlockactions");
    }

    /// <summary>
    /// The current item was refused for itself (<see cref="IsQuestPickupRefusal"/>): left at once,
    /// no pack blamed, and the next item goes out as soon as the character is still.
    /// </summary>
    private void LeaveQuestRefusedLootItem(long now)
    {
        uint err = _currentLootItemRefusalError;
        long after = _currentLootItemRefusedAt - _currentLootItemMoveRequestedAt;
        string name = string.IsNullOrEmpty(_currentLootItemName) ? $"0x{(uint)_currentLootItemId:X8}" : _currentLootItemName;
        LootDiag($"[RynthAi] {LootTag}: the server won't hand over 0x{(uint)_currentLootItemId:X8} '{_currentLootItemName}': {QuestPickupRefusalText(err)} " +
                 $"(error 0x{err:X}, object 0x{_currentLootItemRefusalObject:X8}, {after} ms after the send, try {Math.Max(1, _currentLootItemMoveAttempts)}); leaving it, no pack is to blame.");
        ChatLine($"[RynthAi] Can't loot {name} - {QuestPickupRefusalText(err)}; leaving it.");
        EmptyClientRequestAfterAnonymousRefusal(_currentLootItemRefusalObject);
        _processedCorpseItems.Add(_currentLootItemId);
        _currentLootItemDestsTried.Clear();   // nothing is at the server: no late landing to watch for
        ResetCurrentLootItem();
        _lastLootActionAt = now;
    }

    /// <summary>
    /// A quest refusal (<see cref="IsQuestPickupRefusal"/>) for <paramref name="itemId"/> came since
    /// the refusal counter read <paramref name="seqAtSend"/> (the ground looter's check).
    /// </summary>
    private bool QuestPickupRefusedSince(int seqAtSend, int itemId, out uint error, out uint objectId)
    {
        error = 0;
        objectId = 0;
        try
        {
            if (!Host.HasGetLastWeenieError || !Host.TryGetLastWeenieError(out int seq, out uint err, out uint evt, out uint obj))
                return false;
            if (seq == 0 || seq == seqAtSend || evt != EventInventoryServerSaveFailed || !IsQuestPickupRefusal(err))
                return false;
            if (obj != 0 && obj != unchecked((uint)itemId))
                return false;
            error = err;
            objectId = obj;
            return true;
        }
        catch { return false; }
    }

    // ── Waiting for the server's answer to a move (2026-10-29, R-20261029-122319-321f) ──
    // Conquest, RynthAi 2026.10.29.1, "if max loot attempts are reached it locks up on looting
    // other items": a pack move was retried every ~0.5 s once the character stood still, and
    // after 3 tries (~1.6 s) the pack counted as full for 5 min. That server took 1.5-2.7 s to
    // answer a pickup ("pickup confirmed for 0x8023F05A; merged into a stack in the pack (1657
    // ms after the send)" came after attempt 3 had gone out), so moves were judged failed while
    // the server was still on them. A resend while one is out does nothing useful either: the
    // client drops it (its one pending inventory request, "You can only move or use one item at
    // a time") or ACE turns it away as too busy. Good side packs were marked full one after
    // another, each item burning two of them, until no pack was left. Now a send is followed by
    // another only once it is answered (the item lands, or the server refuses it) or after
    // PickupAnswerWaitMs with no answer, and only the server's refusals mark a pack full for
    // longer than the item (PackRefusals).

    /// <summary>The least time a move is given to be answered before it counts as unanswered.</summary>
    internal const long PickupAnswerWaitMinMs = 4000;

    /// <summary>The most: a slow server's measured answers raise the wait up to this.</summary>
    internal const long PickupAnswerWaitMaxMs = 8000;

    /// <summary>Added to the slowest recent answer.</summary>
    internal const long PickupAnswerWaitMarginMs = 1500;

    /// <summary>A pickup given up on is watched this long for a late landing.</summary>
    internal const long LateLandingWatchMs = 15_000;

    /// <summary>How long a send waits for its answer: the slowest of the last 8 answers plus a margin, 4-8 s.</summary>
    internal long PickupAnswerWaitMs()
    {
        long slowest = 0;
        for (int i = 0; i < _pickupAnswerLatencyCount; i++)
            slowest = Math.Max(slowest, _pickupAnswerLatencies[i]);
        return Math.Clamp(slowest + PickupAnswerWaitMarginMs, PickupAnswerWaitMinMs, PickupAnswerWaitMaxMs);
    }

    private void NotePickupAnswerLatency(long ms)
    {
        if (ms <= 0) return;
        _pickupAnswerLatencies[_pickupAnswerLatencyNext] = ms;
        _pickupAnswerLatencyNext = (_pickupAnswerLatencyNext + 1) % _pickupAnswerLatencies.Length;
        if (_pickupAnswerLatencyCount < _pickupAnswerLatencies.Length) _pickupAnswerLatencyCount++;
    }

    /// <summary>A move of the current item went out: its answer is awaited afresh.</summary>
    private void NoteLootMoveSent()
    {
        _currentLootItemAwaitingAnswer = true;
        _currentLootItemResendWaitLogged = false;
        _currentLootItemRefusedAt = 0;
        _currentLootItemRefusalError = 0;
        _currentLootItemRefusalObject = 0;
        _currentLootItemRefusalBusy = false;
    }

    /// <summary>
    /// Notes the first refusal of the last send, every tick (so its time is known), and whether
    /// it was "too busy" or the server's own answer after it ran the pickup. The "error 0x0" of
    /// the report is a real refusal: the engine keeps only the last refusal message, ACE follows
    /// a YoureTooBusy (0x1D) with InventoryServerSaveFailed for the item and no error code, and
    /// a full pack answers with the same InventoryServerSaveFailed, no code ("Unable to put X
    /// into container", Player.DoHandleActionPutItemInContainer). The old code took every one
    /// for "too busy" and resent it free: 'Brier Wasp's Treasure Map' went into the full main
    /// pack 6 times over 18 s. The time tells them apart: ACE turns a busy move away when it
    /// arrives, and refuses for room only after the pickup motion (longer than PickupAnswerMinMs).
    /// </summary>
    private void PollCurrentPickupRefusal(long now)
    {
        if (_currentLootItemRefusedAt != 0 || !CurrentPickupRefused(out uint err, out uint obj))
            return;
        long after = now - _currentLootItemMoveRequestedAt;
        _currentLootItemRefusedAt = now;
        _currentLootItemRefusalError = err;
        _currentLootItemRefusalObject = obj;
        // A quest refusal is never "too busy", however soon it comes (2026-11-01).
        _currentLootItemRefusalBusy = !IsQuestPickupRefusal(err) && (err == WeenieErrorYoureTooBusy || after < PickupAnswerMinMs);
        if (!_currentLootItemRefusalBusy && !_currentLootItemTimedOut)
            NotePickupAnswerLatency(after);
        if (_currentLootItemSentInAirWindow && RefusedAsTooBusy(err))
            DistrustJumpNoMotion(now, $"0x{(uint)_currentLootItemId:X8}, sent on it, was refused as too busy {after} ms after the send");
    }

    /// <summary>
    /// The refusal of the current send was ACE's "too busy", not another one (a full pack, a quest
    /// item) that only came fast. ACE sends YoureTooBusy (0x1D) and then InventoryServerSaveFailed
    /// with no code (Player_Inventory HandleActionPutItemInContainer_Verify); the engine keeps only
    /// the last refusal but counts each, so the 0x1D is either the last code or the first of two
    /// refusals since the send. A full pack sends one save-failed.
    /// </summary>
    private bool RefusedAsTooBusy(uint err) =>
        err == WeenieErrorYoureTooBusy
        || (err == 0 && LastWeenieErrorSeq() - _currentLootItemErrSeqAtSend >= 2);

    /// <summary>
    /// The server turned away as too busy a move or open that went out on a no-bend-down call: the
    /// last pickup had a bend-down and a Return after all (on a fast server it can be 0.25-0.4 s,
    /// inside PickupAnswerMinMs). The window ends and JumpPickupHadNoMotion says no for
    /// JumpNoMotionDistrustMs, so every pickup waits its Return as without jump-loot.
    /// </summary>
    private void DistrustJumpNoMotion(long now, string why)
    {
        _jumpPickupAirUntil = 0;
        _openInAirWindowSeq = -1;
        bool was = now < _jumpNoMotionOffUntil;
        _jumpNoMotionOffUntil = now + JumpNoMotionDistrustMs;
        if (!was)
            LootDiag($"[RynthAi] {LootTag}: jump-loot: {why}; the server played a bend-down after all - every pickup waits its Return for {JumpNoMotionDistrustMs / 60_000} min.");
    }

    /// <summary>
    /// While a move waits for its answer the corpse's timeout clock does not run (as for the
    /// cast-answer hold, PauseCorpseClockWhileHeld): three unanswered tries take longer than
    /// the 12 s corpse timeout, which is meant for an idle corpse.
    /// </summary>
    private void PauseCorpseClockWhileMoveAwaits(long now)
    {
        long last = _moveAwaitTickAt;
        _moveAwaitTickAt = now;
        if (last == 0 || _targetCorpseId == 0 || _corpseTargetSince == 0 || _corpseHoldFrozenAge >= 0)
            return;
        long gap = now - last;
        if (gap > 0 && gap <= PickupHoldMaxGapMs)
            _corpseTargetSince += gap;
    }

    /// <summary>
    /// What the tries into the current destination say about it: full (<paramref name="confirmed"/>:
    /// the server refused at least two after running the pickup), doubtful (at least two failed,
    /// some only for want of an answer: the item tries another pack, the mark is the item's
    /// alone), or nothing (the server was only too busy: no pack is to blame).
    /// </summary>
    private bool JudgeLootDest(out bool confirmed)
    {
        confirmed = _currentLootItemDestRefusals >= 2;
        return confirmed || _currentLootItemDestRefusals + _currentLootItemDestTimeouts >= 2;
    }

    private string LootPackName(int packId) =>
        packId == unchecked((int)_playerId) ? "the main pack" : $"pack 0x{(uint)packId:X8}";

    /// <summary>A pack other than the ones left out could take the item (the client's own counts).</summary>
    private bool HasOtherLootDestination()
    {
        if (_objectCache == null || _playerId == 0) return false;
        long now = CorpseNowMs;
        return WorldObjectCache.FindLootDestination(_objectCache.GetDirectInventory(forceRefresh: false),
                   unchecked((int)_playerId), Host, id => _packRefusals.IsRefused(id, now)) != 0;
    }

    /// <summary>The current item is done without its landing seen: watch for one that comes late.</summary>
    private void WatchLateLanding(long now)
    {
        if (_currentLootItemId == 0 || _currentLootItemDestsTried.Count == 0)
            return;
        _lateLandings[_currentLootItemId] = (now + LateLandingWatchMs, _currentLootItemMoveRequestedAt);
    }

    /// <summary>
    /// An item given up on that lands in a pack after all: that pack had room, so it is no longer
    /// treated as full (it may have been marked while the server was still on the item).
    /// </summary>
    private void WatchLateLandings(long now)
    {
        if (_lateLandings.Count == 0 || _objectCache == null)
            return;
        List<int>? done = null;
        foreach (var kv in _lateLandings)
        {
            WorldObject? wo = _objectCache[kv.Key];
            int container = wo?.Container ?? 0;
            if (wo != null && InPlayerInventory(container))
            {
                bool wasMarked = _packRefusals.IsRefused(container, now);
                _packRefusals.Accepted(container);
                LootDiag($"[RynthAi] {LootTag}: 0x{(uint)kv.Key:X8} landed in {LootPackName(container)} {now - kv.Value.SentAt} ms after its last send, after it was given up" +
                         (wasMarked ? $"; {LootPackName(container)} has room after all and is no longer treated as full." : "."));
                (done ??= new()).Add(kv.Key);
            }
            else if (now >= kv.Value.Until)
                (done ??= new()).Add(kv.Key);
        }
        if (done != null)
            foreach (int id in done) _lateLandings.Remove(id);
    }

    /// <summary>A stack when its move is sent: it may merge into a stack in the pack and be deleted.</summary>
    private bool IsStackAtSend(int itemId)
    {
        WorldObject? wo = _objectCache?[itemId];
        if (wo == null) return false;
        return wo.Values(LongValueKey.MaxStackSize, 0) > 1 || wo.Values(LongValueKey.StackCount, 0) > 1;
    }

    /// <summary>
    /// The client no longer has the object at all (no name, no position, no owner): a stack that
    /// merged into one in the pack (ACE's StackableMerge sends InventoryRemoveObject for it, so it
    /// never shows up in the inventory) or anything else the server took away.
    /// </summary>
    private bool ObjectGoneFromClient(int itemId)
    {
        uint uid = unchecked((uint)itemId);
        if (!Host.HasGetObjectName || !Host.HasGetObjectPosition)
            return false; // can't tell
        try
        {
            if (Host.TryGetObjectName(uid, out string n) && !string.IsNullOrEmpty(n)) return false;
            if (Host.TryGetObjectPosition(uid, out _, out _, out _, out _)) return false;
        }
        catch { return false; }
        return true;
    }

    /// <summary>
    /// TickOpenCorpseLooting: a move may be queued behind the last pickup's Return now - the
    /// answer was real (not timed out), it is fresh, it is on this container, and nothing was
    /// queued behind it yet (at most one queued move: ACE refuses a second).
    /// </summary>
    private bool CanPipelinePickup(long now) =>
        Host.HasMoveItemExternal   // a move (PutItemInContainer / StackableMerge), never a use
        && _settlingLootItemId != 0
        && _settlingAllowsPipeline
        && _settlingContainerId == _openedContainerId
        && _pipelinedAfterItemId != _settlingLootItemId
        && now - _settlingSince < PipelineWindowMs;

    /// <summary>
    /// The last answered pickup is still playing out (its Return). True while it is: the corpse
    /// or chest is not closed until it ends (2026-10-07: closed during the last pickup, and the
    /// character picked the item up after the close). Settled once the character stands still
    /// after its Return was seen, or PickupSettleMs after the answer when none was, or
    /// PickupArriveMaxMs after the answer whatever the motion says.
    /// </summary>
    private bool LastPickupSettling(long now)
    {
        if (_settlingLootItemId == 0)
            return false;
        bool animating = PickupAnimating(now);
        if (animating) _settlingSawAnim = true;
        long since = now - _settlingSince;
        bool settled = (!animating && (_settlingSawAnim || since >= PickupSettleMs)) || since >= PickupArriveMaxMs;
        if (!settled)
            return true;
        _settlingLootItemId = 0;
        _settlingContainerId = 0;
        _settlingAllowsPipeline = false;
        return false;
    }

    private bool _pickupHeldLogged;
    private long _pickupHoldTickAt;   // CorpseNowMs of the previous held call; 0 = no hold running

    /// <summary>A gap between two held calls longer than this was not one continuous hold (the corpse clock is not paid back for it).</summary>
    internal const long PickupHoldMaxGapMs = 500;

    /// <summary>Jump-loot for this pickup: the setting, the keyboard jump and the move call, stamina for
    /// the jump, and no fight or heal it could delay (<see cref="JumpLootWouldDelayFight"/>).</summary>
    private bool ShouldJumpLoot(LegacyUiSettings settings) =>
        settings.LootJumpEnabled
        && Host.HasTapJump
        && Host.HasMoveItemExternal
        && _vitals.CurrentStamina > 5
        && !JumpLootWouldDelayFight(settings);

    /// <summary>
    /// The jump's flight holds attacks, buffs and heals (ACE refuses them in the air: ActionPacer
    /// LootJumpAirborne), so a pickup goes without one while monsters are near (looting can run in a
    /// fight with Boost Loot Priority or a loot turn) or health is under Heal At: the bend-down costs
    /// about a second, a heal held in the air could cost the character.
    /// </summary>
    private bool JumpLootWouldDelayFight(LegacyUiSettings settings)
    {
        if (_combatManager?.HasNearbyMonsters == true)
            return true;
        uint max = _vitals.MaxHealth;
        return max > 0 && settings.HealAt > 0 && _vitals.CurrentHealth * 100 < (uint)settings.HealAt * max;
    }

    /// <summary>
    /// True while the server owes an answer to a cast or use (ActionPacer): ACE refuses a pickup
    /// then (Player.HandleActionPutItemInContainer_Verify: IsBusy -> YoureTooBusy and
    /// InventoryServerSaveFailed). In a client log, a pickup sent while Bloodstone Bolt VII
    /// was unanswered came back 0x1D. The assess-timeout path skips the busy-count gate above, so
    /// this is the gate that holds it. Logged once per hold.
    /// </summary>
    private bool PickupHeldByServerAction()
    {
        if (!ActionPacer.Outstanding(Host, out var pending) || pending == null)
        {
            _pickupHeldLogged = false;
            _pickupHoldTickAt = 0;
            return false;
        }
        if (!_pickupHeldLogged)
        {
            _pickupHeldLogged = true;
            LootDiag($"[RynthAi] Corpse loot: pickup waits - the server still owes an answer to {ActionPacer.Describe(pending.Kind, pending.Label)} ({pending.Who}).");
        }
        PauseCorpseClockWhileHeld();
        return true;
    }

    /// <summary>
    /// The corpse's timeout clock does not run while a pickup is held for the server's answer to a
    /// cast or use, as it does not for the buff / salvage-panel holds (FreezeCorpseTimeout). The hold
    /// is bounded by the pacer: an action unanswered past its limit (ActionPacer.CastMaxMs for a
    /// cast) stops being outstanding. FreezeCorpseTimeout itself cannot be used here: the
    /// open-container branch of TickCorpseOpening resets its frozen age every tick, so this adds the
    /// time since the previous held call to the claim time instead. Without it a corpse held for
    /// 12 s timed out the moment the cast was answered, and AbandonCurrentCorpse gave the opened
    /// corpse the 30-minute completed mark, which skipped its loot.
    /// </summary>
    private void PauseCorpseClockWhileHeld()
    {
        long now = CorpseNowMs;
        long last = _pickupHoldTickAt;
        _pickupHoldTickAt = now;
        if (last == 0 || _targetCorpseId == 0 || _corpseTargetSince == 0)
            return;
        long gap = now - last;
        if (gap > 0 && gap <= PickupHoldMaxGapMs)
            _corpseTargetSince += gap;
    }

    // ── A fight while a corpse is open (2026-11-02, R-20261102-204309-206c) ──
    // Conquest, RynthAi 2026.11.2.1, outdoors, Boost Loot Priority on: "it will open the corpse and
    // highlight the item and then just sit waiting". The bow's move went out and had no answer when
    // monsters came 4.4 s later; the arbiter gave Combat the tick (combat is engaged, Boost Loot or
    // not). The open corpse's looter ran on under Combat as if it looted: the answer wait ran out in
    // a lull between shots, the send was counted as an unanswered try, and the resend was held by
    // the shot's busy count, then by the 1 s of stillness the missile fight never gave - silently.
    // With no answer awaited any more nothing stopped the corpse's clock (PauseCorpseClockWhileMoveAwaits
    // runs only while one is), so 19 s after the send the corpse was abandoned mid-pickup with the
    // 30-minute completed mark, and the bow stayed on it. A resend during the fight would not have
    // done better: the client drops an item move while the character attacks ("You cannot move or
    // use an item while attacking", ClientActionGates) and ACE answers one that gets through with
    // "You're too busy". Now, while Combat has the tick, the open corpse waits: its clock stopped,
    // no new pickup, and a move already out is only watched for (it may still land, or be refused).
    // After the fight one with no answer goes again without costing one of its 3 tries
    // (PickupFightResends), and every step says so in the log.

    /// <summary>
    /// TickOpenCorpseLooting: true while a fight has the tick, the open corpse waiting for it (your
    /// own death corpse excepted: its recovery runs as before). Logs when the wait begins and ends.
    /// </summary>
    private bool HoldOpenCorpseForFight(LegacyUiSettings settings, int corpseId, long now)
    {
        bool fight = _activity == BotActivity.Combat && !IsOwnDeathCorpseId(settings, corpseId);
        bool pending = _currentLootItemMovePending && _currentLootItemId != 0;
        if (!fight)
        {
            if (_fightHoldSince != 0)
            {
                _fightHoldEndedAt = now;
                LootDiag($"[RynthAi] {LootTag}: the fight is over after {(now - _fightHoldSince) / 1000.0:0.0}s; looting 0x{(uint)corpseId:X8} goes on" +
                         (pending ? $" (the move of 0x{(uint)_currentLootItemId:X8} '{_currentLootItemName}' sent {now - _currentLootItemMoveRequestedAt} ms ago " +
                                    (_currentLootItemRefusedAt != 0 ? "was refused)." : "has no answer yet).") : "."));
            }
            _fightHoldSince = 0;
            _fightHoldTickAt = 0;
            return false;
        }
        if (_fightHoldSince == 0)
        {
            _fightHoldSince = now;
            LootDiag(pending
                ? $"[RynthAi] {LootTag}: 0x{(uint)corpseId:X8} waits for the fight: the move of 0x{(uint)_currentLootItemId:X8} '{_currentLootItemName}' sent {now - _currentLootItemMoveRequestedAt} ms ago " +
                  (_currentLootItemRefusedAt != 0 ? "was refused" : "has no answer yet") +
                  "; it is watched for but not sent again while combat has the tick (the client drops item moves during an attack); the corpse's timeout clock is stopped."
                : $"[RynthAi] {LootTag}: 0x{(uint)corpseId:X8} waits for the fight: no pickup while combat has the tick; the corpse's timeout clock is stopped.");
        }
        PauseCorpseClockForFight(now);
        if (pending && _openedContainerInventoryObservedAt != 0)
            TickPendingCorpsePickup(settings, heldByFight: true);
        return true;
    }

    /// <summary>
    /// The corpse's timeout clock does not run while a fight holds it, as for the cast-answer hold
    /// (PauseCorpseClockWhileHeld, and for the same reason: the open-container branch resets the
    /// frozen age FreezeCorpseTimeout keeps). A pickup confirmed during the hold restarts the clock.
    /// </summary>
    private void PauseCorpseClockForFight(long now)
    {
        long last = _fightHoldTickAt;
        _fightHoldTickAt = now;
        if (last == 0 || _targetCorpseId == 0 || _corpseTargetSince == 0)
            return;
        long gap = now - last;
        if (gap > 0 && gap <= PickupHoldMaxGapMs)
            _corpseTargetSince += gap;
    }

    /// <summary>
    /// The current item's next send is held (<paramref name="why"/>) although its last send has had
    /// no answer (or a refusal) for PickupResendWaitLogMs past the answer wait (and past the end of a
    /// fight's wait): said once per send, so no pickup waits without a line (R-20261102-204309-206c:
    /// 19 s with none).
    /// </summary>
    private void NoteNextSendWaits(long now, long answerWaitMs, string why)
    {
        if (_currentLootItemResendWaitLogged)
            return;
        long since = now - _currentLootItemMoveRequestedAt;
        long due = Math.Max(_currentLootItemMoveRequestedAt + answerWaitMs, _fightHoldEndedAt);
        if (now - due < PickupResendWaitLogMs)
            return;
        _currentLootItemResendWaitLogged = true;
        string last = _currentLootItemRefusedAt != 0 ? "was refused" : "has had no answer";
        LootDiag($"[RynthAi] {LootTag}: the last send of 0x{(uint)_currentLootItemId:X8} '{_currentLootItemName}' ({since} ms ago) {last}; the next send waits: {why}.");
    }

    private void TickPendingCorpsePickup(LegacyUiSettings settings, bool heldByFight = false)
    {
        if (_objectCache == null || _openedContainerId == 0 || _currentLootItemId == 0)
        {
            ResetCurrentLootItem();
            return;
        }

        long now = CorpseNowMs;
        WorldObject? currentItem = _objectCache[_currentLootItemId];
        int currentContainer = currentItem?.Container ?? 0;
        // One ownership read, the same test GetContainedItems makes for the listing.
        bool stillVisibleOnCorpse = currentItem != null && currentContainer == _openedContainerId;

        // Watch for this pickup's bend-down (the server plays it once the move reaches it).
        bool animating = PickupAnimating(now);
        if (animating) _currentLootItemSawAnim = true;

        // The server's answer: ACE moves the item at the end of the pickup motion and starts the
        // Return in the same breath (InventoryPutObjInContainer, or for a stack that merges into
        // one in the pack InventoryRemoveObject + SetStackSize). So the item is answered once it
        // is off the container AND in the inventory (the main pack, a side pack) or merged away
        // (a stack, now unowned, or the object gone from the client: it never shows up in the
        // inventory, which made every merged stack wait PickupArriveMaxMs, 4 s, 2026-10-12).
        // The corpse still isn't closed until the Return has played (LastPickupSettling).
        if (!stillVisibleOnCorpse)
        {
            if (_currentLootItemLeftAt == 0) _currentLootItemLeftAt = now;
            bool inPack = currentItem != null && InPlayerInventory(currentContainer);
            // Merged into a stack in the pack (it never shows up in the inventory): gone from the
            // client, unowned and a stack when sent (ours, 064a07a), or a same-name stack in the
            // pack has grown since the send (PR #16) - whichever shows first.
            int mergedInto = 0;
            bool mergedAway = !inPack
                && (currentItem == null || ObjectGoneFromClient(_currentLootItemId) || (currentContainer == 0 && _currentLootItemStackable)
                    || MergedIntoInventoryStack(out mergedInto));
            bool landed = inPack || mergedAway;
            // The floor is ACE's motion time; a jump send skips it only for the item seen in a pack,
            // the server's answer about that object, which also frees the client's one request slot
            // (a merged-away object can leave the slot held, so a merge keeps the floor).
            bool jumpAnswer = _currentLootItemJumped && inPack;
            bool believable = jumpAnswer || now - _currentLootItemMoveRequestedAt >= PickupAnswerMinMs;
            long since = now - _currentLootItemLeftAt;
            bool answered = landed && believable;
            if (!answered && since < PickupArriveMaxMs)
                return;
            ConfirmCurrentLootItem(
                now,
                currentItem == null
                    ? "item left cache after use"
                    : inPack
                        ? "item no longer listed on corpse"
                        : mergedInto != 0
                            ? $"merged into the stack 0x{(uint)mergedInto:X8} in the pack"
                            : mergedAway
                                ? "merged into a stack in the pack"
                                : $"off the corpse, not seen in a pack (container=0x{(uint)currentContainer:X8})",
                answered,
                inPack ? currentContainer : 0);
            return;
        }

        // The server turned the move away. "Too busy" (it arrived during the last pickup's
        // bend-down, not its Return): resend it as soon as the character is still, without
        // counting it as one of the item's 3 tries. Refused after the pickup ran (no room): one
        // of the tries, and the evidence a pack is full (PollCurrentPickupRefusal).
        PollCurrentPickupRefusal(now);
        bool refused = _currentLootItemRefusedAt != 0;
        bool busyRefusal = refused && _currentLootItemRefusalBusy;

        // Refused for the item itself (a pickup timer, a quest flag): no try into any pack will
        // do better, so it is left now, with no pack blamed (2026-11-01, #rynth-issues "Stopped looting").
        if (refused && IsQuestPickupRefusal(_currentLootItemRefusalError))
        {
            LeaveQuestRefusedLootItem(now);
            return;
        }

        // A fight has the tick (HoldOpenCorpseForFight, R-20261102-204309-206c): the answer is
        // watched for (above) and nothing else. No resend: the client drops an item move while the
        // character attacks and ACE answers one that gets through "You're too busy". A send with
        // no answer yet goes again after the fight without costing one of the 3 tries.
        if (heldByFight)
        {
            if (!refused) _currentLootItemFightHeld = true;
            _currentLootItemStillSince = 0;
            _moveAwaitTickAt = 0;   // the fight's hold pays the corpse clock back, not the answer wait
            return;
        }

        long answerWaitMs = PickupAnswerWaitMs();

        // Item still on corpse — wait before retrying the UseObject. The window counts from
        // when the character is still: while a pickup animation plays the engine holds the
        // move, and a retry timed from the send gave up on items whose move had not gone
        // out yet (2026-10-07: moves ran ~2.2 s late, 3 tries at 0.5 s skipped the item).
        // A queued move waits here through the last pickup's Return and its own bend-down.
        if (PickupAnimating(now))
        {
            _currentLootItemStillSince = 0;
            if (_currentLootItemAwaitingAnswer) PauseCorpseClockWhileMoveAwaits(now);
            NoteNextSendWaits(now, answerWaitMs, "the character keeps animating");
            return;
        }
        if (_currentLootItemStillSince == 0)
            _currentLootItemStillSince = now;
        bool freeRetry = busyRefusal && _currentLootItemFreeRetries < PickupFreeRetries;
        // The last send had no answer when a fight took the tick: it goes again, free (bounded).
        bool fightResend = !freeRetry && !refused && _currentLootItemFightHeld && _currentLootItemFightResends < PickupFightResends;

        // No answer yet: no resend before PickupAnswerWaitMs (2026-10-29, R-20261029-122319-321f).
        // The Loot Retry Timeout alone (0.5-1 s) is shorter than a slow server's answer (1.5-2.7 s
        // on Conquest), and a resend while the move is out is dropped by the client or refused.
        long sinceSend = now - _currentLootItemMoveRequestedAt;
        if (_currentLootItemAwaitingAnswer && !refused && sinceSend < answerWaitMs)
        {
            PauseCorpseClockWhileMoveAwaits(now);
            return;
        }
        long verifyWindowMs = Math.Max(150, settings.LootRetryTimeoutMs);
        long windowStart = Math.Max(_currentLootItemMoveRequestedAt, _currentLootItemStillSince);
        if (!freeRetry && now - windowStart < verifyWindowMs)
        {
            NoteNextSendWaits(now, answerWaitMs, $"the character has not stood still for {verifyWindowMs} ms");
            return;
        }

        // The last send failed: what it came to, once per send (a free resend's "too busy" is
        // not one of the tries). Refused after the pickup ran counts against the pack; no answer
        // at all only half (JudgeLootDest).
        string lastTry = "";
        if (_currentLootItemAwaitingAnswer && !freeRetry)
        {
            _currentLootItemAwaitingAnswer = false;
            long answerMs = refused ? _currentLootItemRefusedAt - _currentLootItemMoveRequestedAt : sinceSend;
            if (fightResend)
            {
                // The fight, not the pack: no timeout counted against it (JudgeLootDest).
                _currentLootItemTimedOut = true;
                lastTry = $"no answer in {answerMs} ms, a fight in between";
            }
            else if (!refused)
            {
                _currentLootItemDestTimeouts++;
                _currentLootItemTimedOut = true;
                lastTry = $"no answer in {answerMs} ms";
            }
            else if (!busyRefusal && _currentLootItemRefusalError == 0)
            {
                _currentLootItemDestRefusals++;
                lastTry = $"refused {answerMs} ms after the send, after its pickup ran (error 0x{_currentLootItemRefusalError:X})";
            }
            else if (!busyRefusal)
            {
                // ACE's answer for want of room carries no error code ("Unable to put X into
                // container"); one with a code is about the item, not the pack (2026-11-01).
                lastTry = $"refused {answerMs} ms after the send, after its pickup ran, for the item (error 0x{_currentLootItemRefusalError:X}), not the pack";
            }
            else
                lastTry = $"too busy, {answerMs} ms after the send";
        }

        if (!freeRetry && !fightResend && _currentLootItemMoveAttempts >= 3)
        {
            // None of the tries landed. When the server refused them after running the pickup,
            // the pack they went to is full (its contents unknown to the client): leave it out
            // for a while and give the item one more round into the next one; with no other
            // pack, the corpse is left with the pack-full message. When they only went unanswered
            // the pack is not known to be full: the item may try another pack, but the mark is
            // dropped with the item, so the next item and corpse start fresh (R-20261029-122319-321f:
            // those quick marks used up every pack). When the server was only too busy, no pack is
            // to blame.
            int dest = _currentLootItemDest;
            if (dest != 0 && JudgeLootDest(out bool confirmed))
            {
                _packRefusals.Refuse(dest, now, confirmed, _currentLootItemId);
                if (confirmed && !_currentLootItemFullPacks.Contains(dest)) _currentLootItemFullPacks.Add(dest);
                if (!_currentLootItemPackRetried && (confirmed || HasOtherLootDestination()))
                {
                    LootDiag(confirmed
                        ? $"[RynthAi] {LootTag}: {LootPackName(dest)} took none of {_currentLootItemMoveAttempts} tries for 0x{(uint)_currentLootItemId:X8} (the server refused {_currentLootItemDestRefusals} after the pickup ran); treating it as full for {RynthCore.Plugin.RynthAi.Loot.PackRefusals.HoldMs / 60_000} min and trying another pack."
                        : $"[RynthAi] {LootTag}: {LootPackName(dest)} took none of {_currentLootItemMoveAttempts} tries for 0x{(uint)_currentLootItemId:X8} ({_currentLootItemDestTimeouts} unanswered in {answerWaitMs} ms, {_currentLootItemDestRefusals} refused); trying another pack for this item only (not known to be full).");
                    _currentLootItemPackRetried = true;
                    _currentLootItemMoveAttempts = 0;
                    _currentLootItemDestRefusals = 0;
                    _currentLootItemDestTimeouts = 0;
                    _currentLootItemStillSince = 0;
                    _lastLootActionAt = now;
                    return;
                }
            }
            LootDiag($"[RynthAi] {LootTag}: pickup failed for 0x{(uint)_currentLootItemId:X8}; container remained 0x{(uint)_openedContainerId:X8} after {_currentLootItemMoveAttempts} attempt(s)" +
                     (lastTry.Length > 0 ? $" (last: {lastTry})." : "."));
            // Refused for room in every pack it tried (two or more), and in none of them for
            // another item: the server is refusing this item, not the packs (a pickup timer that
            // says no more than a full pack does, 2026-11-01). Its marks go with it, or the next
            // item finds no pack and is selected and never picked up.
            if (_currentLootItemFullPacks.Count >= 2)
            {
                List<int> back = _packRefusals.Withdraw(_currentLootItemId, now);
                if (back.Count > 0)
                    LootDiag($"[RynthAi] {LootTag}: the server refused 0x{(uint)_currentLootItemId:X8} in {_currentLootItemFullPacks.Count} packs the client counts room in: it is refusing the item, not the packs; " +
                             $"{string.Join(" and ", back.Select(LootPackName))} {(back.Count == 1 ? "is" : "are")} no longer treated as full.");
            }
            _processedCorpseItems.Add(_currentLootItemId);
            ResetCurrentLootItem();
            _lastLootActionAt = now;
            return;
        }

        if (_busyCount > 0)
        {
            // Client is busy — don't queue retry. (R-20261102-204309-206c: a shot's busy count
            // held the resend here, with no line, until the corpse timed out.)
            NoteNextSendWaits(now, answerWaitMs, $"the client's busy count is {_busyCount}");
            return;
        }
        if (PickupHeldByServerAction())
            return; // a retry now would be refused as too busy and use up an attempt

        RynthCore.Plugin.RynthAi.ItemInfo.SelfSelect.Mark(unchecked((uint)_currentLootItemId));
        Host.SelectItem(unchecked((uint)_currentLootItemId));
        int errSeqAtSend = LastWeenieErrorSeq();
        long refusedAfter = refused ? _currentLootItemRefusedAt - _currentLootItemMoveRequestedAt : 0;
        uint refusal = _currentLootItemRefusalError;
        LootPickupResult retry = SendLootPickup(_currentLootItemId, _currentLootItemName, _openedContainerId);
        if (retry == LootPickupResult.NoRoom || retry == LootPickupResult.Refused)
            return; // SendLootPickup finished the item / corpse
        bool retried = retry == LootPickupResult.Moved || retry == LootPickupResult.Used;
        if (freeRetry)
            _currentLootItemFreeRetries++;
        else if (fightResend)
            _currentLootItemFightResends++;
        else
            _currentLootItemMoveAttempts++;
        _currentLootItemFightHeld = false;
        _currentLootItemErrSeqAtSend = errSeqAtSend;
        _currentLootItemMoveRequestedAt = now;
        _currentLootItemJumped = false;   // a resend goes without a jump (the floor applies)
        _currentLootItemSentInAirWindow = false;
        if (retried) NoteLootMoveSent();
        _currentLootItemStillSince = 0;
        _currentLootItemSawAnim = false;
        _lastLootActionAt = now;
        LootDiag(freeRetry
            ? $"[RynthAi] {LootTag}: the server refused 0x{(uint)_currentLootItemId:X8} as too busy ({refusedAfter} ms after the send, before its pickup ran; error 0x{refusal:X}); sending it again now the character is still."
            : fightResend
            ? $"[RynthAi] {LootTag}: sending 0x{(uint)_currentLootItemId:X8} again after the fight" +
              (lastTry.Length > 0 ? $" (last: {lastTry})" : "") +
              $"; not one of its 3 tries ({_currentLootItemMoveAttempts} used)."
            : $"[RynthAi] {LootTag}: retrying use for 0x{(uint)_currentLootItemId:X8} attempt {_currentLootItemMoveAttempts}" +
              (lastTry.Length > 0 ? $" (last: {lastTry})." : "."));

        if (!retried && _currentLootItemMoveAttempts >= 3)
        {
            _processedCorpseItems.Add(_currentLootItemId);
            ResetCurrentLootItem();
        }
    }

    private int FindNextCorpseItem(int corpseId, out int visibleItemCount)
    {
        visibleItemCount = 0;
        if (_objectCache == null)
            return 0;

        foreach (WorldObject item in _objectCache.GetContainedItems(corpseId))
        {
            visibleItemCount++;
            if (item.Id == 0 || _processedCorpseItems.Contains(item.Id))
                continue;

            return item.Id;
        }

        return 0;
    }

    /// <summary>
    /// Sends the pickup for a corpse item (<see cref="LootPickup"/>: a move into a pack,
    /// never a use). NoRoom finishes the corpse like the free-slot check does; Refused
    /// leaves the item. Both reset the current item, so the caller just returns.
    /// </summary>
    private LootPickupResult SendLootPickup(int itemId, string itemName, int corpseId)
    {
        NoteMergeTargets(itemId, itemName);
        bool ownCorpse = corpseId != 0 && corpseId == _ownRecoveryCorpseId;
        uint dest;
        LootPickupResult r = ownCorpse
            ? SendOwnCorpsePickup(itemId, out dest)
            : LootPickup.Send(Host, _objectCache, unchecked((uint)itemId), _playerId, out dest,
                              id => _packRefusals.IsRefused(id, CorpseNowMs));
        if (r == LootPickupResult.Moved)
        {
            int to = unchecked((int)dest);
            if (to != _currentLootItemDest)
            {
                // Another pack: what the tries into the last one came to says nothing about it.
                _currentLootItemDestRefusals = 0;
                _currentLootItemDestTimeouts = 0;
            }
            _currentLootItemDest = to;
            if (!_currentLootItemDestsTried.Contains(to)) _currentLootItemDestsTried.Add(to);
        }
        if (ownCorpse && r == LootPickupResult.NoRoom)
        {
            PauseOwnCorpseNoRoom(corpseId, itemName);
            return r;
        }
        switch (r)
        {
            case LootPickupResult.Moved:
                LootDiag($"[RynthAi] {LootTag}: moving 0x{(uint)itemId:X8} '{itemName}' into {(dest == _playerId ? "the main pack" : $"pack 0x{dest:X8}")}.");
                break;
            case LootPickupResult.NoRoom:
                LootDiag($"[RynthAi] {LootTag}: no pack has a free slot for '{itemName}' on 0x{(uint)corpseId:X8}; leaving the corpse.");
                WorldObjectCache.WarnPackFull(Host, "looting stopped, no pack has a free slot");
                ResetCurrentLootItem();
                MarkCorpseCompleted(corpseId);
                break;
            case LootPickupResult.Refused:
                LootDiag($"[RynthAi] {LootTag}: leaving '{itemName}' 0x{(uint)itemId:X8} — the engine has no move call and a use would put it on.");
                _processedCorpseItems.Add(itemId);
                ResetCurrentLootItem();
                break;
        }
        return r;
    }

    /// <summary>
    /// The current pickup is done as far as the item goes (<paramref name="answered"/>: the
    /// server's answer was seen; false: it left the corpse and the wait for it ran out). The
    /// character may still be standing up: that Return is tracked as the settling pickup.
    /// </summary>
    private void ConfirmCurrentLootItemMoved(long now, string reason) => ConfirmCurrentLootItem(now, reason, answered: true);

    /// <param name="landedIn">The pack the item was seen in (0: merged away, or not seen).</param>
    private void ConfirmCurrentLootItem(long now, string reason, bool answered, int landedIn = 0)
    {
        if (_currentLootItemId == 0)
            return;

        int itemId = _currentLootItemId;
        if (answered)
        {
            // The pack it landed in has room, whatever it was marked (an earlier send may have
            // landed after the item moved on to another pack: R-20261029-122319-321f). A stack
            // that merged needed no slot, so it clears its destination only when no refusal
            // from there said otherwise.
            if (landedIn != 0)
            {
                if (_packRefusals.IsRefused(landedIn, now))
                    LootDiag($"[RynthAi] {LootTag}: 0x{(uint)itemId:X8} landed in {LootPackName(landedIn)}; it has room after all and is no longer treated as full.");
                _packRefusals.Accepted(landedIn);
            }
            else if (_currentLootItemDest != 0 && _currentLootItemDestRefusals == 0)
                _packRefusals.Accepted(_currentLootItemDest);
            // Only an answer that is surely to the last send (a lag spike's answer to an earlier,
            // timed-out send would stretch the wait for every item after it).
            if (!_currentLootItemTimedOut) NotePickupAnswerLatency(now - _currentLootItemMoveRequestedAt);
        }
        if (answered) _currentLootItemDestsTried.Clear();   // landed: nothing to watch for (ResetCurrentLootItem)
        _processedCorpseItems.Add(_currentLootItemId);
        _corpseItemsLooted++;
        bool noMotion = answered && JumpPickupHadNoMotion();
        long jumpSentAt = _currentLootItemMoveRequestedAt;
        LootDiag($"[RynthAi] {LootTag}: pickup confirmed for 0x{(uint)_currentLootItemId:X8}; {reason}" +
                 $" ({now - _currentLootItemMoveRequestedAt} ms after the send{(_currentLootItemPipelined ? ", queued" : "")}" +
                 $"{(noMotion ? $", jump-loot: off the corpse {_currentLootItemLeftAt - _currentLootItemMoveRequestedAt} ms after the send, no bend-down or Return to wait for" : "")}).");
        if (noMotion) NoteNoMotionOffCorpse(_currentLootItemLeftAt - _currentLootItemMoveRequestedAt);   // sizes the flight-budget prediction
        // Each item taken is progress, so the corpse/chest timeout counts from the last pickup:
        // a chest gets its 2 minutes per pickup, and a corpse with many keeps is no longer
        // closed part way through by the 12 s timeout (SK, 2026-10-11). Not while a hold
        // has the clock frozen.
        if (_corpseHoldFrozenAge < 0
            && (IsChestLoot(_targetCorpseId) || (_targetCorpseId != 0 && _targetCorpseId == _openedContainerId)))
            _corpseTargetSince = now;
        if (_openedContainerId != 0 && _openedContainerId == _ownRecoveryCorpseId)
            NoteOwnCorpseItemRecovered(now);
        if (_currentLootItemChat is { } described)
            WriteItemInfoLine(described.Line, described.ChatType);
        else if (!string.IsNullOrWhiteSpace(_currentLootItemName))
            ChatLine($"[RynthAi] Looted [{_currentLootItemActionLabel}] {_currentLootItemName}");

        if (_currentLootItemIsSalvage)
            _salvageManager?.EnqueueItem(unchecked((uint)_currentLootItemId));

        // It is in the pack: the drain may go (and the arrival grace counts from now).
        if (_currentLootItemActionLabel == ManaTapActionLabel)
            _manaStoneManager?.MarkLootedForTap(_currentLootItemId, arrived: true);

        ResetCurrentLootItem();
        _lastLootActionAt = now;

        _pickupAnimSince = 0; // the next pickup's animation gets its own PickupAnimMaxMs
        if (noMotion)
        {
            // Jump-loot, answered with no bend-down: ACE played no pickup motion, so EnqueuePickupDone
            // has no Return to play and the server is no longer busy. Nothing to settle: the corpse
            // can close and the next pickup or open can go now, in the air. What the server refuses
            // in the air (a cast, an attack) waits on its own gates (ActionPacer.LootJumpAirborne).
            // Still one move at a time: the next goes only after this one's answer.
            _settlingLootItemId = 0;
            _settlingContainerId = 0;
            _settlingAllowsPipeline = false;
            // The flight ends JumpLandMaxMs after the JUMP, not after this send: a send that rode an
            // earlier jump (the in-air path) is later than its jump. Looked up as an age so the
            // pacer's clock and the loot clock need not agree; -1 (no jump noted: it has landed, or
            // the pacer was reset) falls back to the send time.
            long flightAge = ActionPacer.LootJumpAgeMs();
            _jumpPickupAirUntil = flightAge >= 0
                ? now + (ActionPacer.JumpLandMaxMs - flightAge)
                : jumpSentAt + ActionPacer.JumpLandMaxMs;
            return;
        }

        // Its Return now plays: the close waits for it, and one move may be queued behind it. A
        // busy motion queue is that Return now, not an earlier jump's flight.
        _jumpPickupAirUntil = 0;
        _settlingLootItemId = itemId;
        _settlingContainerId = _openedContainerId;
        _settlingSince = now;
        _settlingSawAnim = false;
        _settlingAllowsPipeline = answered;
    }

    /// <summary>
    /// The current pickup was a jump-loot pickup the server ran with no bend-down: its jump went
    /// out with this very send or it was sent in the air from the last one (not a resend), and the
    /// item was off the corpse sooner than PickupAnswerMinMs after it - quicker than ACE's pickup
    /// motion, so ACE skipped it (in the air, Player_Inventory.GetPickupMotion returns Invalid under
    /// allow_jump_loot). A jump pickup answered later (the character had landed, or allow_jump_loot
    /// is off) had a bend-down and gets its Return waited out as before. Off for
    /// JumpNoMotionDistrustMs after the server proved the call wrong (DistrustJumpNoMotion).
    /// </summary>
    private bool JumpPickupHadNoMotion() =>
        _currentLootItemJumped
        && CorpseNowMs >= _jumpNoMotionOffUntil
        && _currentLootItemLeftAt != 0
        && _currentLootItemLeftAt - _currentLootItemMoveRequestedAt < PickupAnswerMinMs;

    /// <summary>
    /// Inside the flight of the last jump pickup that had no bend-down (at most
    /// ActionPacer.JumpLandMaxMs from its jump): a busy motion queue now is the jump's take-off,
    /// fall or landing, not a server pickup motion.
    /// </summary>
    private bool JumpPickupInAir(long now) => _jumpPickupAirUntil != 0 && now < _jumpPickupAirUntil;

    /// <summary>
    /// A pickup sent in the air now is predicted to reach the server after the loot jump's flight:
    /// jumpAge + the expected answer time is over <see cref="LootJumpFlightBudgetMs"/>.
    /// </summary>
    private bool FlightBudgetSpent(long jumpAge, out long expectedAnswerMs)
    {
        expectedAnswerMs = ExpectedNoMotionAnswerMs();
        return jumpAge >= 0 && jumpAge + expectedAnswerMs > LootJumpFlightBudgetMs;
    }

    /// <summary>The recent send-to-off-corpse time of pickups answered with no bend-down (mean of the last 4), or the default.</summary>
    internal long ExpectedNoMotionAnswerMs()
    {
        if (_noMotionOffCorpseCount == 0) return DefaultNoMotionOffCorpseMs;
        long sum = 0;
        for (int i = 0; i < _noMotionOffCorpseCount; i++) sum += _noMotionOffCorpse[i];
        return sum / _noMotionOffCorpseCount;
    }

    private void NoteNoMotionOffCorpse(long ms)
    {
        if (ms <= 0) return;
        _noMotionOffCorpse[_noMotionOffCorpseNext] = ms;
        _noMotionOffCorpseNext = (_noMotionOffCorpseNext + 1) % _noMotionOffCorpse.Length;
        if (_noMotionOffCorpseCount < _noMotionOffCorpse.Length) _noMotionOffCorpseCount++;
    }

    /// <summary>
    /// WantLooting input for the ActivityArbiter (STEP 4). No game commands, no
    /// state mutation — safe to call from the decision path every tick.
    ///
    /// This deliberately answers "is there loot work to do" rather than "have I
    /// already claimed a corpse". The distinction is about tick order: the
    /// arbiter runs at the top of OnTick and TickCorpseOpening (which is what
    /// sets _targetCorpseId) runs near the bottom, so a claim-only predicate
    /// would lag one tick behind every claim and hand nav a free step away from
    /// each fresh body. Answering from what is in range makes the decision lead
    /// the claim instead of trailing it.
    ///
    /// Caveat, deliberately kept: the in-range scan calls ShouldLootCorpse,
    /// which fires one Host.RequestId per corpse to populate the killer name
    /// (guarded by _corpseIdRequested: once per accepted request, asked again after
    /// CorpseIdReaskMs while no data has arrived). That is a
    /// memoized fetch rather than a mutation, and the old bottom-of-tick
    /// IsCorpseNavigationClaimActive call already did it on the same cadence —
    /// this moves it earlier in the tick, it does not add it.
    /// </summary>
    private bool HasLootWork(LegacyUiSettings settings, long nowMs)
    {
        _lootStarved = false;
        _lootWorkWhy = string.Empty;
        _deathWaitHeld = false;
        if (!settings.IsMacroRunning || (!settings.EnableLooting && !ForcedLootHolds()))
            return false;

        // The claim's own full stop (TickCorpseOpening): with Boost Nav Priority on and a nav route
        // running the looter stands down, so there is no loot work to offer (R-20261031-203155-cea0).
        if (BoostNavHoldsLoot(settings) && !ForcedLootHolds())
            return false;

        // The server refuses every use (UseRefusalWatch, Stuck): no loot work until the back-off
        // ends, so the arbiter lets nav and combat run on instead of standing at the corpses.
        if (_openedContainerId == 0 && _useRefusals.HoldsOpens(CorpseNowMs))
            return false;

        // Already committed to a body, or mid-loot with the container open.
        if (_openedContainerId != 0 || _targetCorpseId != 0 || _corpseAutorunActive)
        {
            int current = _targetCorpseId != 0 ? _targetCorpseId : _openedContainerId;
            _lootStarved = current != 0
                && _corpseWaitingSince.TryGetValue(current, out long since)
                && CorpseNowMs - since >= LootStarveMs;
            _lootWorkWhy = _openedContainerId != 0 ? "a container is open" : _targetCorpseId != 0 ? "a corpse is claimed" : "corpse autorun";
            _deathWaitHeld = DeathWaitHolds(settings);   // the kill's corpse being looted: the wait goes on (RynthAiPlugin.DeathWait.cs)
            return true;
        }

        // No room anywhere: don't start on another corpse (IsPackFull says so once in chat).
        // Your own corpse is let through unless no pack has a slot at all.
        if (IsLootingBlockedByPack(settings) && !OwnCorpseBypassesPackGate(settings, GetCorpseApproachRangeMaxMeters(settings)))
            return false;

        // An open chest the player or a meta opened, eligible to loot (RynthAiPlugin.ChestLoot.cs).
        if (HasChestLootWork(settings))
        {
            _lootWorkWhy = "an open chest";
            return true;
        }

        // A named object in range (Loot by name, RynthAiPlugin.LootByName.cs): used so the client walks there.
        if (HasLootNameWork(settings, CorpseNowMs))
        {
            _lootWorkWhy = "a named item";
            return true;
        }

        // Loot grace: combat just ended and the corpse CreateObject has not
        // arrived yet. Hold Looting across that gap so nav doesn't walk away
        // from a body that is about to exist.
        if (_combatEndedAt != 0 && (nowMs - _combatEndedAt) < LootGraceMs)
        {
            _lootWorkWhy = "loot grace after combat";
            return true;
        }

        // Boost Loot Priority: our kill's corpse is still forming. Looting holds the tick (combat
        // holds its next target) so the corpse is looted before the next fight starts.
        if (BoostLootAwaitsKillCorpse(settings))
        {
            double range = GetCorpseApproachRangeMaxMeters(settings);
            if (range > 0.25) HasUnlootedCorpsesInRange(range);   // keeps _lootableInRange current
            _lootWorkWhy = "Boost Loot: the kill's corpse is forming";
            _deathWaitHeld = DeathWaitHolds(settings);
            return true;
        }

        // The death wait (2026-11-08, RynthAiPlugin.DeathWait.cs): our kill's corpse is on its way, or
        // here and being looted. Looting holds the tick, so nothing walks off meanwhile.
        if (_deathWaitAt != 0)
        {
            double range = GetCorpseApproachRangeMaxMeters(settings);
            if (range > 0.25) HasUnlootedCorpsesInRange(range);   // keeps _lootableInRange current
            _deathWaitHeld = DeathWaitHolds(settings);
            if (_deathWaitHeld)
            {
                _lootWorkWhy = _deathCorpseId == 0 ? "the kill's corpse is on its way (death animation)" : "the kill's corpse is being looted";
                return true;
            }
        }

        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        if (maxMeters <= 0.25)
            return false;

        if (HasUnlootedCorpsesInRange(maxMeters))
        {
            _lootWorkWhy = $"{_lootableInRange.Count} lootable corpse(s) in range, e.g. 0x{(uint)_lootableInRange[0]:X8}";
            return true;
        }
        if (HasGroundLootWork(settings))
        {
            _lootWorkWhy = "ground loot";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Boost Nav Priority, with a nav route running, is passing by a corpse in range that would be
    /// looted with it off. For
    /// the once-per-session notice only (RynthAiPlugin.BoostNav.cs), which calls it every
    /// BoostNavLootCheckMs until it has spoken. The scan's loot-starvation mark is put back: it
    /// belongs to HasLootWork's tick, not to this look.
    /// </summary>
    private bool BoostNavSkipsLoot(LegacyUiSettings settings)
    {
        if (!settings.IsMacroRunning || !settings.EnableLooting || !BoostNavHoldsLoot(settings)) return false;
        if (IsLootingBlockedByPack(settings)) return false;
        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        if (maxMeters <= 0.25) return false;
        bool starved = _lootStarved;
        try { return HasUnlootedCorpsesInRange(maxMeters); }
        finally { _lootStarved = starved; }
    }

    /// <summary>
    /// Stop Looting When Pack Full: no free slot anywhere, or fewer than the weapon-swap
    /// reserve. Below the reserve the pickup gate refuses every item, so a new corpse was
    /// only walked to, opened and given up on (its loot skipped for good).
    /// </summary>
    private bool IsLootingBlockedByPack(LegacyUiSettings settings)
        => settings.StopLootingWhenPackFull && (IsPackFull() || IsBelowLootReserve());

    // STEP 4/5: IsCorpseNavigationClaimActive is gone. It answered "should loot
    // block nav?", which was only ever a question because nav and loot each
    // decided for themselves whether to run. The arbiter answers it once now,
    // for every subsystem, and HasLootWork above carries the parts that were
    // real (claimed corpse, open container, autorun, corpses in range). Its
    // BoostNavPriority early-out became the BoostNav clause in Decide(), and its
    // BotAction=="Looting" gap check became unnecessary once the string stopped
    // being a lock that someone had to remember to release.

    // -- Loot starvation (2026-10-03) ---------------------------------------
    // Combat outranks looting, and on a busy spawn there is always a monster inside
    // MonsterRange, so a corpse could wait behind combat until the area emptied (by which
    // time nav had often moved on). A lootable corpse in range that has waited
    // LootStarveMs gets a turn as soon as no monster is within LootStarveCloseYards.
    internal const long LootStarveMs = 8000;
    internal const double LootStarveCloseYards = 5.0;
    private readonly Dictionary<int, long> _corpseWaitingSince = new(); // lootable corpse first seen in range
    private readonly List<int> _lootableInRange = new();               // last scan's lootable corpses in range
    private readonly HashSet<int> _corpseSkipLogged = new();           // once-per-corpse "why not looted" lines
    private bool _lootStarved;    // HasLootWork: the current/in-range corpse has waited LootStarveMs
    private bool _lootStarveTurn; // the arbiter gave Looting this tick because of starvation

    /// <summary>
    /// Returns true if at least one lootable, non-completed corpse is within range. Also
    /// records when each lootable corpse was first seen in range (loot starvation), and
    /// logs once per corpse when one of your own kills is lootable but out of range.
    /// </summary>
    private bool HasUnlootedCorpsesInRange(double maxMeters)
    {
        _lootableInRange.Clear();
        if (_objectCache == null || _playerId == 0)
            return false;

        // A new scan tick: the identify budget starts over, and the corpse the bot is going for asks first.
        _corpseIdAcceptedThisTick = 0;
        _corpseIdBudgetAt = ActionPacer.NowMs();
        foreach (int cid in new[] { _targetCorpseId, _openedContainerId })
            if (cid != 0 && _objectCache[cid] is { } cand && cand.ObjectClass == AcObjectClass.Corpse)
                TryRequestCorpseId(cand, cand.Values(StringValueKey.LongDesc, string.Empty));
        long now = CorpseNowMs;
        int playerId = unchecked((int)_playerId);
        foreach (WorldObject candidate in _objectCache.GetLandscapeObjects())
        {
            if (candidate.ObjectClass != AcObjectClass.Corpse)
                continue;
            if (_completedCorpses.ContainsKey(candidate.Id))
                continue;
            if (_corpseCooldownUntil.TryGetValue(candidate.Id, out long blockedUntil) && blockedUntil > now)
                continue;
            CorpseLootDecision decision = EvaluateCorpseLootDecision(candidate);
            if (!decision.ShouldLoot)
                continue;

            double dist = _objectCache.Distance(playerId, candidate.Id);
            if (dist <= maxMeters)
            {
                _lootableInRange.Add(candidate.Id);
                if (!_corpseWaitingSince.ContainsKey(candidate.Id))
                    _corpseWaitingSince[candidate.Id] = now;
                if (now - _corpseWaitingSince[candidate.Id] >= LootStarveMs)
                    _lootStarved = true;
            }
            else if (dist != double.MaxValue && decision.IsOwnDeathCorpse && _corpseSkipLogged.Add(candidate.Id))
            {
                LootDiag($"[RynthAi] Own corpse: 0x{(uint)candidate.Id:X8} '{candidate.Name}' is out of loot range (d={dist:0.0}m > {maxMeters:0.0}m); it is recovered once it is within range.");
            }
            else if (dist != double.MaxValue && IsOwnKill(decision) && _corpseSkipLogged.Add(candidate.Id))
            {
                LootDiag($"[RynthAi] Corpse loot: own corpse 0x{(uint)candidate.Id:X8} '{candidate.Name}' is out of loot range (d={dist:0.0}m > {maxMeters:0.0}m); not walking back for it.");
            }
        }

        return _lootableInRange.Count > 0;
    }

    /// <summary>
    /// The arbiter's LootStarved input: looting wants the tick, combat wants it too, a corpse
    /// has waited LootStarveMs (HasLootWork set _lootStarved this tick), and no monster is
    /// within LootStarveCloseYards. Melee fights keep combat in charge (the mob is on you).
    /// </summary>
    private bool IsLootStarvedTurn(bool wantLoot, bool wantCombat)
        => wantLoot && wantCombat && _lootStarved
           && _combatManager?.HasCloseThreat(LootStarveCloseYards) != true;

    // -- Boost Loot Priority: one kill, one loot (2026-10-14) --------------------------------
    // Combat locked its next target a few ms after each kill, and ACE creates the corpse 1-3 s
    // later (the death animation), so the arbiter always saw combat engaged and Boost Loot never
    // won: Kelpie's corpses waited for a lull and were looted two or three at a time. Now our kill
    // starts a wait of up to BoostLootKillWaitMs for its corpse. While it runs, or while a corpse
    // is claimed, open or lootable in range, CombatManager locks no new target (HoldNewTarget)
    // and looting counts as work, so the arbiter gives Looting the tick.
    internal const long BoostLootKillWaitMs = 4000;
    private long _boostLootKillAt;   // CorpseNowMs of our last kill whose corpse isn't looted yet; 0 = none

    /// <summary>Our kill (KillerNotification): its corpse is on its way.</summary>
    private void NoteBoostLootKill() => _boostLootKillAt = CorpseNowMs;

    /// <summary>
    /// A completed corpse first seen after the kill (or never timed) is the kill's corpse: the
    /// wait is over, so combat doesn't stand idle for the rest of it.
    /// </summary>
    private void EndBoostLootKillWait(int corpseId)
    {
        if (_boostLootKillAt == 0) return;
        if (!_corpseWaitingSince.TryGetValue(corpseId, out long seen) || seen >= _boostLootKillAt)
            _boostLootKillAt = 0;
    }

    /// <summary>Boost Loot Priority is on and a corpse could be looted now (not with rare-only looting,
    /// where most kills leave nothing to wait for: LootOnlyRareCorpses, or Loot Mode rares).</summary>
    private bool BoostLootCanRun(LegacyUiSettings settings)
        => settings.BoostLootPriority && settings.IsMacroRunning && settings.EnableLooting
           && !BoostNavHoldsLoot(settings)
           && !settings.LootOnlyRareCorpses
           && !LootModes.IsRares(settings.LootMode)
           && !IsLootingBlockedByPack(settings)
           && !_useRefusals.HoldsOpens(CorpseNowMs);

    /// <summary>Our last kill was under BoostLootKillWaitMs ago and its corpse isn't looted yet.</summary>
    private bool BoostLootAwaitsKillCorpse(LegacyUiSettings settings)
    {
        if (_boostLootKillAt == 0) return false;
        if (CorpseNowMs - _boostLootKillAt >= BoostLootKillWaitMs)
        {
            _boostLootKillAt = 0;
            return false;
        }
        return BoostLootCanRun(settings);
    }

    /// <summary>
    /// CombatManager.HoldNewTarget: with Boost Loot Priority on, no new target while a corpse is
    /// claimed, open or lootable in range, or our kill's corpse is still forming.
    /// </summary>
    private bool BoostLootHoldsNewTarget()
    {
        LegacyUiSettings? settings = _dashboard?.Settings;
        if (settings == null || !BoostLootCanRun(settings)) return false;
        return _targetCorpseId != 0 || _openedContainerId != 0 || _lootableInRange.Count > 0
               || BoostLootAwaitsKillCorpse(settings);
    }

    private static bool IsOwnKill(CorpseLootDecision d)
        => !string.IsNullOrWhiteSpace(d.KillerName) && !string.IsNullOrWhiteSpace(d.PlayerName)
           && NormalizeCharName(d.KillerName).Equals(NormalizeCharName(d.PlayerName), StringComparison.OrdinalIgnoreCase);

    /// <summary>Once per corpse: a lootable corpse in range is waiting because combat holds the tick.</summary>
    private void LogCorpsesHeldByCombat()
    {
        if (_lootableInRange.Count == 0) return;
        long now = CorpseNowMs;
        foreach (int id in _lootableInRange)
        {
            if (!_corpseSkipLogged.Add(id)) continue;
            long waited = _corpseWaitingSince.TryGetValue(id, out long since) ? now - since : 0;
            LootDiag($"[RynthAi] Corpse loot: corpse 0x{(uint)id:X8} waits for combat (targets in range, waited {waited / 1000.0:0.0}s); "
                     + $"it gets a turn after {LootStarveMs / 1000}s with no monster within {LootStarveCloseYards:0} yd, or when the fight ends.");
        }
    }

    private void PauseNavigationForCorpse()
    {
        // STEP 4/5: the corpse-local pause flag and the "Looting" string write
        // are both gone. StopNavFor is the single edge-detect for nav stops and
        // the arbiter is the single writer of the string — this now just routes
        // the corpse claim through them.
        if (_navStopIssued && _navStoppedFor == BotActivity.Looting)
            return;
        // Only while Looting owns the tick (then OnTick's StopNavFor(_activity) already did
        // this). While Combat owned the tick and an opened corpse was being finished, OnTick
        // stopped nav "for Combat" and this stopped it "for Looting": the owner flipped every
        // tick, so Stop + StopCompletely went out twice a tick, 60 times a second, flooding
        // AC's main-thread queue (thousands of dropped actions, mid-attack stops, and a
        // launcher killed the client for it, 2026-10-01).
        if (_activity != BotActivity.Looting)
            return;

        StopNavFor(BotActivity.Looting);

        int corpseId = _openedContainerId != 0 ? _openedContainerId : _targetCorpseId;
        if (corpseId != 0)
            LootDiag($"[RynthAi] {LootTag}: pausing navigation for corpse 0x{(uint)corpseId:X8}.");
    }

    /// <summary>
    /// Classification: evaluates an item against the active loot profile and RynthAi's
    /// built-in keeps without performing any pickup. Returns true if the item is to be
    /// taken; actionLabel/ruleLabel are what [LootEval] logs. The looter's calls record
    /// what they approve (pending sets, counters, chat); ClassifyLootItem has the preview.
    /// </summary>
    private long _noUstLeftSaidAt = long.MinValue / 2;

    /// <summary>Says once every 5 minutes in chat that salvage items are being left for want of a UST.</summary>
    private void NoteNoUstLeft(string? name)
    {
        long now = Environment.TickCount64;
        if (now - _noUstLeftSaidAt < 5 * 60_000) return;
        _noUstLeftSaidAt = now;
        ChatLine($"[RynthAi] No UST in your pack: leaving salvage items on corpses (first: {name}). Carry a Ust to salvage.");
    }

    private bool ClassifyItemAgainstProfile(WorldObject item, LegacyUiSettings settings, out string actionLabel, out bool isSalvage, out string ruleLabel) =>
        ClassifyLootItem(item, settings, preview: false, out actionLabel, out isSalvage, out ruleLabel, out _);

    /// <summary>
    /// The one place the looter decides, with <paramref name="preview"/>: the same verdict
    /// with nothing recorded, counted, logged or said in chat (the "Add to loot profile"
    /// popup asking why an item is taken). <paramref name="builtIn"/> is set when one of
    /// RynthAi's built-in keeps took the item rather than a loot profile rule: Learn unknown
    /// spells and mana stones (checked before the profile), Mana Tap (when no rule matches).
    /// It says what and where to change it, in plain ASCII (the popup's font).
    /// </summary>
    private bool ClassifyLootItem(WorldObject item, LegacyUiSettings settings, bool preview,
        out string actionLabel, out bool isSalvage, out string ruleLabel, out string builtIn)
    {
        actionLabel = string.Empty;
        isSalvage   = false;
        ruleLabel   = string.Empty;
        builtIn     = string.Empty;

        // Loot Mode "Rares and needed mana stones only": the loot profile and the other built-in
        // keeps are not used (ClassifyRaresMode).
        if (LootModes.IsRares(settings.LootMode))
            return ClassifyRaresMode(item, settings, preview, out actionLabel, out ruleLabel, out builtIn);

        // Learn unknown spells (VTank ReadUnknownScrolls): a built-in rule ahead of the loot
        // profile, as in VTank. A scroll of a spell we don't know and can learn is looted to read.
        if (_scrollLearner != null && _scrollLearner.ShouldLootFromCorpse(item, out string learnRule, out string learnWhy, preview))
        {
            actionLabel = ScrollLearner.ReadAction;
            ruleLabel   = learnRule;
            builtIn     = $"Learn unknown spells ({learnWhy}) - change in Settings > Looting, Learn Unknown Spells";
            return true;
        }

        // Consumable override — checked before loot profile. Stone detection is
        // centralized in ManaStoneManager.IsManaStone so this loot gate and the
        // KeepCount tally below use one predicate: named-rule match if the user
        // configured ManaStone rules, otherwise any ObjectClass.ManaStone when
        // tapping is on (zero-config looting).
        bool isConfiguredManaStone = _manaStoneManager?.IsManaStone(item) == true;

        if (isConfiguredManaStone)
            return ClassifyManaStoneKeep(item, settings, preview, raresMode: false, out actionLabel, out ruleLabel, out builtIn);

        if (TryLoadNativeLootProfile(out LootProfile nativeProfile, out _))
        {
            if (!preview) _corpseItemsEvaluated++;
            var (nativeAction, nativeRule) = LootEvaluator.Classify(nativeProfile, item, _charSkills);
            if (nativeRule == null)
            {
                if (TryClassifyAsManaTap(item, settings, preview, out actionLabel, out ruleLabel, out builtIn))
                {
                    if (!preview) _corpseMatchedIds.Add(item.Id);
                    return true;
                }
                return false;
            }
            ruleLabel   = string.IsNullOrWhiteSpace(nativeRule.Name) ? "rule" : nativeRule.Name.Trim();
            if (nativeAction == LootAction.Read && IsReadOfNonScroll(item, ruleLabel, preview))
                return false;
            if (nativeAction == LootAction.KeepUpTo)
            {
                var rule = nativeRule;
                if (!UnderKeepCap("n:" + ruleLabel, ruleLabel, item, rule.KeepCount,
                        inv => LootEvaluator.Matches(rule, inv, _charSkills), preview))
                    return false;
            }
            if (!preview) _corpseMatchedIds.Add(item.Id);
            actionLabel = nativeAction.ToString();
            isSalvage   = nativeAction == LootAction.Salvage;
            return true;
        }

        // VTank .utl profile fallback
        if (!TryLoadLootProfile(string.Empty, out VTankLootProfile vtankProfile, out _))
            return false;

        if (!preview) _corpseItemsEvaluated++;
        VTankLootContext lootCtx = new(Host, _playerId) { Cache = _objectCache };
        VTankLootRule? matchedRule = VTankLootEvaluator.FirstMatch(vtankProfile, item, lootCtx, out int matchedRuleIndex);

        if (matchedRule == null)
        {
            if (TryClassifyAsManaTap(item, settings, preview, out actionLabel, out ruleLabel, out builtIn))
            {
                if (!preview) _corpseMatchedIds.Add(item.Id);
                return true;
            }
            return false;
        }

        ruleLabel = string.IsNullOrWhiteSpace(matchedRule.Name)
            ? $"#{matchedRuleIndex}"
            : matchedRule.Name.Trim();
        if (matchedRule.Action == VTankLootAction.Read && IsReadOfNonScroll(item, ruleLabel, preview))
            return false;
        if (matchedRule.Action == VTankLootAction.KeepUpTo)
        {
            var rule = matchedRule;
            if (!UnderKeepCap("v:" + matchedRuleIndex + ":" + ruleLabel, ruleLabel, item, rule.KeepCount ?? 0,
                    inv => VTankLootEvaluator.Match(rule, inv, lootCtx), preview))
                return false;
        }
        if (!preview) _corpseMatchedIds.Add(item.Id);
        actionLabel = matchedRule.Action.ToString();
        isSalvage   = matchedRule.Action == VTankLootAction.Salvage;
        return true;
    }

    /// <summary>
    /// For the [LootEval] line of an identified item the looter leaves: " (switched-off rule #N
    /// 'name' [Keep] would take it)" when a Keep or Keep # rule that's off in the VTank profile,
    /// above any rule that matched (a Keep # at its count), would take the item; else empty
    /// (switched-off Salvage rules aren't named here: LootSnob ships them all off). Rares mode and
    /// RynthAi's own (.json) profiles have no such rules. Costs one more pass over the profile,
    /// only for a left item's first log line on an open.
    /// </summary>
    private string SwitchedOffNote(WorldObject item, LegacyUiSettings settings)
    {
        try
        {
            if (LootModes.IsRares(settings.LootMode)) return string.Empty;
            if (TryLoadNativeLootProfile(out _, out _)) return string.Empty;
            if (!TryLoadLootProfile(string.Empty, out VTankLootProfile profile, out _)) return string.Empty;
            VTankLootContext ctx = new(Host, _playerId) { Cache = _objectCache };
            VTankLootEvaluator.FirstMatch(profile, item, ctx, out int matched);
            int off = VTankLootEvaluator.FirstSwitchedOffMatch(profile, item, ctx, matched, keepsOnly: true);
            return off < 0 ? string.Empty
                : $" (switched-off rule #{off + 1} '{Loot.LootWhy.NameOf(profile.Rules[off], off)}' [{profile.Rules[off].Action}] would take it)";
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// The built-in mana stone keep: a stone (ManaStoneManager.IsManaStone) is taken while the pack
    /// holds fewer than the Items panel's keep count (Mana Stone Tapping, Keep count), counting
    /// stones approved on this corpse that haven't reached the pack yet; at the count it is left.
    /// Profile mode runs it ahead of the loot profile; rares mode (<paramref name="raresMode"/>)
    /// runs the same count, with its own rule label and reason.
    /// </summary>
    private bool ClassifyManaStoneKeep(WorldObject item, LegacyUiSettings settings, bool preview, bool raresMode,
        out string actionLabel, out string ruleLabel, out string builtIn)
    {
        actionLabel = string.Empty;
        ruleLabel   = string.Empty;
        builtIn     = string.Empty;

        // An Eternal Mana Charge in the pack: tapping is ignored, so stones are not needed.
        if (_manaStoneManager?.HasEternalChargeCached() == true && !ManaStoneManager.IsEternalCharge(item))
        {
            builtIn = "a mana stone, but an Eternal Mana Charge is in the pack: mana tapping is ignored";
            if (!preview) _corpseCappedIds.Add(item.Id);
            return false;
        }

        // Count stones already in the pack against the KeepCount cap, using
        // the SAME IsManaStone predicate as the loot gate above — if these
        // diverged the cap would silently stop working (e.g. gate loots by
        // ObjectClass but tally counts by name → tally stays 0 → over-loot).
        // GetDirectInventory(forceRefresh:true) actively walks the player's
        // containers via Host.GetContainerContents, so it's immune to the
        // cache-classification race that bites AllKnownObjects() right after
        // RL/login. IsManaStone's name fallback also keys on Name (always
        // present), so the tally is reliable even before ITEM_TYPE lands.
        // One walk per pass, not one per stone: a chest with 9 stones walked the whole
        // inventory 9 times in the same tick (ManaStoneLiveCountMs).
        long nowMs = CorpseNowMs;
        int liveCount;
        if (_manaStoneLiveCountAt != 0 && nowMs - _manaStoneLiveCountAt < ManaStoneLiveCountMs)
            liveCount = _manaStoneLiveCount;
        else
        {
            liveCount = 0;
            if (_objectCache != null)
            {
                foreach (var inv in _objectCache.GetDirectInventory(forceRefresh: true))
                {
                    if (string.IsNullOrEmpty(inv.Name)) continue;
                    if (_manaStoneManager?.IsManaStone(inv) == true)
                        liveCount++;
                }
            }
            _manaStoneLiveCount = liveCount;
            _manaStoneLiveCountAt = nowMs;
        }

        // Include stones already approved this session whose CreateObject event
        // hasn't landed yet. Distinct ids only — same item re-evaluated across
        // ticks must not bump the count.
        int pendingNotYetCounted = 0;
        foreach (int id in _pendingManaStoneIds)
        {
            if (id == item.Id) continue; // current item handled below
            pendingNotYetCounted++;
        }
        int haveCount = liveCount + pendingNotYetCounted;

        // One-time-per-corpse log so we can see what the count was based on (it was printed
        // for every stone while none was approved: 9 lines a chest at the cap).
        if (!preview && _pendingManaStoneIds.Count == 0 && _manaStoneCapLoggedFor != _openedContainerId)
        {
            _manaStoneCapLoggedFor = _openedContainerId;
            Host.Log($"[RynthAi] ManaStone count for cap check: live={liveCount} pending={pendingNotYetCounted} cap={settings.ManaStoneKeepCount} (cache_known={_objectCache?.AllKnownObjects().Count() ?? 0})");
        }

        // If this specific stone is already approved, return the same answer
        // we returned last time (no further log spam, no count change).
        string stoneWhy = raresMode
            ? $"a needed mana stone, {haveCount} of {settings.ManaStoneKeepCount} kept (Loot Mode: rares and needed mana stones only) - the keep count is in the Items panel, Mana Stone Tapping"
            : $"a mana stone, {haveCount} of {settings.ManaStoneKeepCount} kept - change in the Items panel, Mana Stone Tapping (Keep count)";
        string stoneRule = raresMode ? LootModes.StoneRule : $"ManaStone (Consumable, {haveCount + 1}/{settings.ManaStoneKeepCount})";
        if (_pendingManaStoneIds.Contains(item.Id))
        {
            actionLabel = "Keep";
            ruleLabel   = stoneRule;
            builtIn     = stoneWhy;
            return true;
        }

        if (haveCount < settings.ManaStoneKeepCount)
        {
            actionLabel = "Keep";
            ruleLabel   = stoneRule;
            builtIn     = stoneWhy;
            if (preview) return true;
            ChatLine($"[RynthAi] Mana stone match: '{item.Name}' (have {haveCount}/{settings.ManaStoneKeepCount}, +1) — looting.");
            _pendingManaStoneIds.Add(item.Id);
            return true;
        }

        // Rares mode: say why a stone is left (/ra loot why), as it has no rule to name.
        if (raresMode)
            builtIn = $"a mana stone, but {haveCount} of {settings.ManaStoneKeepCount} are kept already: not needed (Loot Mode: rares and needed mana stones only)";
        if (!preview)
        {
            _corpseCappedIds.Add(item.Id);
            ChatLine($"[RynthAi] Mana stone match: '{item.Name}' (have {haveCount}/{settings.ManaStoneKeepCount}) — at cap, leaving.");
        }
        return false;
    }

    /// <summary>
    /// Loot Mode "Rares and needed mana stones only" (owner, 2026-10-31): the loot profile is not
    /// used. A rare (RareItems: its rare icon, which the create message carries, or its RareId once
    /// identified) is kept; a mana stone is kept exactly when the built-in mana stone keep wants it
    /// (ClassifyManaStoneKeep: the same IsManaStone test and keep count); everything else is left,
    /// built-in keeps included (Learn Unknown Spells, Mana Tap). Your own corpse is recovered by its
    /// own path (TickOwnCorpseRecovery), which never comes here.
    /// </summary>
    private bool ClassifyRaresMode(WorldObject item, LegacyUiSettings settings, bool preview,
        out string actionLabel, out string ruleLabel, out string builtIn)
    {
        actionLabel = string.Empty;
        ruleLabel   = string.Empty;
        builtIn     = string.Empty;
        if (!preview) _corpseItemsEvaluated++;

        if (RareItems.IsRare(Host, item, out string how))
        {
            actionLabel = "Keep";
            ruleLabel   = LootModes.RareRule;
            builtIn     = $"a rare ({how}) (Loot Mode: rares and needed mana stones only) - change in Settings > Looting, Loot Mode";
            if (!preview) _corpseMatchedIds.Add(item.Id);
            return true;
        }

        if (_manaStoneManager?.IsManaStone(item) == true)
        {
            bool take = ClassifyManaStoneKeep(item, settings, preview, raresMode: true, out actionLabel, out ruleLabel, out builtIn);
            if (take && !preview) _corpseMatchedIds.Add(item.Id);
            return take;
        }

        builtIn = "not a rare or a mana stone (Loot Mode: rares and needed mana stones only) - change in Settings > Looting, Loot Mode";
        return false;
    }

    private readonly HashSet<int> _readNonScrollLogged = new();

    /// <summary>
    /// A loot profile's Read action on something that isn't a scroll. VTank: "Lootplugin
    /// attempted to classify item ... for reading, but that item does not appear to be a
    /// scroll. Ignoring it." The item is left, with one log line.
    /// </summary>
    private bool IsReadOfNonScroll(WorldObject item, string ruleLabel, bool preview = false)
    {
        if (ScrollLearner.LooksLikeScroll(item)) return false;
        if (preview) return true;
        if (_readNonScrollLogged.Count > 1000) _readNonScrollLogged.Clear();
        if (_readNonScrollLogged.Add(item.Id))
            Host.Log($"[Learn] loot rule '{ruleLabel}' says Read, but '{item.Name}' isn't a scroll - leaving it");
        return true;
    }

    /// <summary>
    /// Keep # (KeepUpTo): loot only while the pack holds fewer than
    /// <paramref name="cap"/> of what the rule matches, counting whole stacks, plus
    /// items already approved on this corpse that haven't reached the pack yet.
    /// A matching Keep # rule used to loot everything, like a plain Keep.
    /// </summary>
    private bool UnderKeepCap(string key, string ruleLabel, WorldObject item, int cap, Func<WorldObject, bool> matches, bool preview = false)
    {
        if (!_pendingKeepUpTo.TryGetValue(key, out var pending))
        {
            pending = new Dictionary<int, int>();
            if (!preview) _pendingKeepUpTo[key] = pending;
        }
        if (pending.ContainsKey(item.Id)) return true;   // approved on an earlier pass

        // Whole stacks in the pack that the rule matches, plus approved-but-not-in-the-pack-yet
        // items. Approved ones that no longer exist are skipped: a looted stack merges into a
        // pack stack and is deleted, and is already counted. (LootKeepCap holds the arithmetic.)
        int have = LootKeepCap.CountHave(
            _objectCache?.GetDirectInventory(forceRefresh: true),
            matches,
            pending,
            id => _objectCache?[id] != null);

        int stack = LootKeepCap.StackOf(item);
        if (preview) return LootKeepCap.UnderCap(have, cap);
        if (!LootKeepCap.UnderCap(have, cap))
        {
            _corpseCappedIds.Add(item.Id);
            ChatLine($"[RynthAi] Keep # '{ruleLabel}': have {have}/{cap} — leaving {item.Name}.");
            return false;
        }
        pending[item.Id] = stack;
        ChatLine($"[RynthAi] Keep # '{ruleLabel}': have {have}/{cap} — looting {item.Name}{(stack > 1 ? " x" + stack : "")}.");
        return true;
    }

    // ManaStoneManager asks before draining an inventory item (the drain destroys it).
    // Cached per item: a big VTank profile is costly to run on every think tick.
    private readonly Dictionary<int, (bool Kept, long At)> _lootKeptCache = new();
    private const long LootKeptCacheMs = 60_000;

    /// <summary>
    /// True when a Keep or Keep # rule in the active loot profile matches the item.
    /// Pure: no pending sets, counters or chat (unlike ClassifyItemAgainstProfile).
    /// </summary>
    private bool IsKeptByLootProfile(WorldObject item)
    {
        long now = CorpseNowMs;
        if (_lootKeptCache.TryGetValue(item.Id, out var c) && now - c.At < LootKeptCacheMs)
            return c.Kept;

        bool kept;
        string path = (_dashboard?.Settings?.CurrentLootPath ?? string.Empty).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            kept = false; // no profile: the loaders would print a chat line on every call
        else
        {
            try
            {
                if (TryLoadNativeLootProfile(out LootProfile nativeProfile, out _))
                {
                    var (action, rule) = LootEvaluator.Classify(nativeProfile, item, _charSkills);
                    kept = rule != null && (action == LootAction.Keep || action == LootAction.KeepUpTo);
                }
                else if (TryLoadLootProfile(string.Empty, out VTankLootProfile vtankProfile, out _))
                {
                    kept = false;
                    VTankLootContext ctx = new(Host, _playerId) { Cache = _objectCache };
                    foreach (var rule in vtankProfile.Rules)
                    {
                        if (!VTankLootEvaluator.Match(rule, item, ctx)) continue;
                        kept = rule.Action == VTankLootAction.Keep || rule.Action == VTankLootAction.KeepUpTo;
                        break;
                    }
                }
                else
                    kept = false;
            }
            catch { kept = true; } // can't tell: don't destroy it
        }

        if (_lootKeptCache.Count > 1000) _lootKeptCache.Clear();
        _lootKeptCache[item.Id] = (kept, now);
        return kept;
    }

    /// <summary>
    /// Loot-for-tap fallback: when an item doesn't match any loot rule but it
    /// holds at least the configured mana threshold AND the player has an
    /// uncharged mana stone in inventory, loot the item so ManaStoneManager
    /// can drain it (which destroys the item and charges the stone). This is the
    /// only way an item becomes a tap target: the pickup (label ManaTap) is
    /// recorded via ManaStoneManager.MarkLootedForTap.
    /// </summary>
    private bool TryClassifyAsManaTap(WorldObject item, LegacyUiSettings settings, bool preview,
                                      out string actionLabel, out string ruleLabel, out string builtIn)
    {
        actionLabel = string.Empty;
        ruleLabel   = string.Empty;
        builtIn     = string.Empty;

        if (_manaStoneManager == null) return false;
        if (!Host.HasGetObjectIntProperty) return false;

        int threshold = settings.ManaTapMinMana;
        if (threshold <= 0) return false;

        if (!Host.TryGetObjectIntProperty(unchecked((uint)item.Id),
                                          (uint)AcIntProperty.CurrentMana, out int curMana))
            return false;
        if (curMana < threshold) return false;

        string tapWhy = "Mana Tap (" + curMana.ToString("N0", CultureInfo.InvariantCulture) + " mana >= "
            + threshold.ToString("N0", CultureInfo.InvariantCulture)
            + "), looted to drain into an empty mana stone - change in the Items panel, Mana Stone Tapping";

        // Already approved this session — return same answer without re-logging
        // or re-counting against the quota.
        if (_pendingManaTapIds.Contains(item.Id))
        {
            actionLabel = ManaTapActionLabel;
            ruleLabel   = $"ManaTap (mana={curMana} ≥ {threshold})";
            builtIn     = tapWhy;
            return true;
        }

        // Quota: one item at a time (FreeEmptyStoneSlots is 1 with an empty stone in the pack and
        // no tap item waiting for its drain), so the pack never fills with items it can't drain.
        int freeSlots = _manaStoneManager.FreeEmptyStoneSlots() - _pendingManaTapIds.Count;
        if (freeSlots <= 0) return false;

        actionLabel = ManaTapActionLabel;
        ruleLabel   = $"ManaTap (mana={curMana} ≥ {threshold})";
        builtIn     = tapWhy;
        if (preview) return true;
        ChatLine($"[RynthAi] Mana tap candidate: '{item.Name}' (mana {curMana}, slot {_pendingManaTapIds.Count + 1}) — looting to drain.");
        _pendingManaTapIds.Add(item.Id);
        return true;
    }

    /// <summary>
    /// Returns true if the active loot profile has at least one rule that
    /// (a) could match this item's object class, AND
    /// (b) uses conditions that require appraisal data (numeric stats, spell counts, etc.)
    ///
    /// When false, the item can be definitively evaluated from name/class data alone,
    /// so we skip Host.RequestId() to avoid inflating the client busy count.
    /// </summary>
    private bool ItemNeedsAppraisalForLoot(WorldObject item)
    {
        // The ManaTap fallback (run when no rule matches) reads CurrentMana, which only
        // the appraisal carries, so with tapping on every item waits for its ID.
        var tapSettings = _dashboard?.Settings;
        // Rares mode needs no ID: a rare shows by its rare icon (in the create message), a mana
        // stone by its name, and Mana Tap is off there. (RareId, sent only with an ID, still
        // counts for an item that has one.)
        if (tapSettings != null && LootModes.IsRares(tapSettings.LootMode))
            return false;
        bool manaTapNeedsId = tapSettings != null && tapSettings.EnableManaTapping
                              && tapSettings.ManaTapMinMana > 0 && _manaStoneManager != null;

        if (TryLoadNativeLootProfile(out LootProfile nativeProfile, out _))
            return manaTapNeedsId || NativeProfileNeedsAppraisalForClass(nativeProfile, item.ObjectClass);

        if (TryLoadLootProfile(string.Empty, out VTankLootProfile vtankProfile, out _))
        {
            if (manaTapNeedsId) return true;
            // A type not readable yet fails every class condition: wait for the ID, which has it.
            if (item.ObjectClass == AcObjectClass.Unknown) return true;
            // Per item, as VTank decides: the rules in order, from what the item showed without an
            // ID. It needs the ID when the first rule nothing has ruled out needs it (a launcher's
            // missile damage keep rules, before a Salvage rule on its material). Decided without it
            // (a name rule took it, or no rule can), it's judged now with no ID request.
            // The answer can't change until the ID lands, so it's kept for the open (and dropped
            // when the item's name or class fills in later).
            if (!ReferenceEquals(_needsIdCacheProfile, vtankProfile)) { _needsIdCache.Clear(); _needsIdCacheProfile = vtankProfile; }
            if (_needsIdCache.TryGetValue(item.Id, out var cached) && cached.Class == item.ObjectClass && cached.Name == item.Name)
                return cached.Needs;
            VTankLootContext ctx = new(Host, _playerId) { Cache = _objectCache };
            bool needs = VTankLootEvaluator.Decide(vtankProfile, item, ctx, hasIdData: false, out _) == VTankLootVerdict.NeedsId;
            if (_needsIdCache.Count > 2000) _needsIdCache.Clear();
            _needsIdCache[item.Id] = (needs, item.ObjectClass, item.Name);
            return needs;
        }

        return false; // No active profile — no ID request needed
    }

    // Corpse items whose ID is late, and when it was last asked again (cleared with each open).
    private readonly Dictionary<int, long> _corpseItemIdReaskedAt = new();

    /// <summary>A held corpse item's ID is late: ask again, at most every CorpseIdReaskMs.</summary>
    private void ReaskCorpseItemId(WorldObject item, long now)
    {
        if (_corpseItemIdReaskedAt.TryGetValue(item.Id, out long last) && now - last < CorpseIdReaskMs) return;
        _corpseItemIdReaskedAt[item.Id] = now;
        if (!Host.HasRequestId) return;
        bool sent = Host.RequestId(unchecked((uint)item.Id));
        LootDiag($"[LootEval] '{item.Name}' id=0x{(uint)item.Id:X8}: no ID yet and a rule that decides it needs one; {(sent ? "asked again" : "ID request refused")}, holding it.");
    }

    /// <summary>ItemNeedsAppraisalForLoot's answer per item for a VTank profile, cleared with each corpse open.</summary>
    private readonly Dictionary<int, (bool Needs, AcObjectClass Class, string Name)> _needsIdCache = new();
    private VTankLootProfile? _needsIdCacheProfile;

    private static bool NativeProfileNeedsAppraisalForClass(LootProfile profile, AcObjectClass cls)
    {
        foreach (var rule in profile.Rules)
        {
            if (!rule.Enabled) continue;

            // Determine whether the rule targets this class
            bool hasClassFilter = false;
            bool classMatches   = false;
            foreach (var cond in rule.Conditions)
            {
                if (cond is ObjectClassCondition occ)
                {
                    hasClassFilter = true;
                    if ((AcObjectClass)(int)occ.ObjectClass == cls
                        || ((AcObjectClass)(int)occ.ObjectClass == AcObjectClass.Scroll && cls == AcObjectClass.Book)    // a scroll not yet known as one
                        || ((AcObjectClass)(int)occ.ObjectClass == AcObjectClass.Book && cls == AcObjectClass.Scroll))   // Book rules from when scrolls filed as Book
                        classMatches = true;
                }
            }
            if (hasClassFilter && !classMatches) continue; // Rule filters out this class

            // Rule can match this class — does it require appraisal data?
            foreach (var cond in rule.Conditions)
            {
                if (cond is LongValKeyGECondition
                        or LongValKeyLECondition
                        or LongValKeyECondition
                        or LongValKeyNECondition
                        or LongValKeyFlagCondition
                        or DoubleValKeyGECondition
                        or DoubleValKeyLECondition
                        or TotalRatingsGECondition
                        or MinDamageGECondition
                        or DamagePercentGECondition)
                    return true;
            }
        }
        return false;
    }

    private void MarkCorpseCompleted(int corpseId)
    {
        if (corpseId == 0)
            return;

        // A chest is finished and closed, never marked: it regenerates, and the next open loots it again.
        if (IsChestLoot(corpseId))
        {
            FinishChestLoot(corpseId, "completed", close: true, sayNothingMatched: true);
            return;
        }

        _completedCorpses[corpseId] = CorpseNowMs;
        EndBoostLootKillWait(corpseId);
        EndDeathWaitFor(corpseId);
        _corpseCooldownUntil.Remove(corpseId);
        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        _corpseIdsRequested = false;
        _corpseItemIdReaskedAt.Clear();
        _pendingManaStoneIds.Clear(); _pendingManaTapIds.Clear(); _pendingKeepUpTo.Clear();
        ResetCurrentLootItem();

        // Close the loot window (CloseCorpseContainer: the client's own close packet, which
        // adds nothing to the busy count; never UseObject(corpse), which does and blocks the
        // next open until the server acks it). Engines without it keep the old behaviour: the
        // server closes this corpse when the next UseObject open is sent.
        // Clear container state immediately; SyncCorpseContainerState guards against
        // re-claiming via _completedCorpses until the server's close lands.
        // With another corpse to loot in range, no close at all: ACE closes the open
        // container itself when the next one is used (Container.ActOnUse closes the player's
        // LastOpenedContainerId first), so the close and its answer wait (CloseAnswerWaitMs) only
        // delayed the next open. The close is held back (TickDeferredClose) and still sent if no
        // other corpse is opened, or looting stops.
        if (!(OtherCorpseToLoot(corpseId) && DeferClose(corpseId)))
            CloseCorpseContainer(corpseId, "completed");
        if (_openedContainerId == corpseId)
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
            LootDiag($"[RynthAi] Corpse loot: completed 0x{(uint)corpseId:X8}, cleared container state.");
        }

        // Your own death corpse has its own end line (FinishOwnCorpseRecovery).
        WorldObject? doneCorpse = _objectCache?[corpseId];
        string corpseName = doneCorpse?.Name is { Length: > 0 } cn ? cn : $"0x{(uint)corpseId:X8}";
        if ((doneCorpse == null || !EvaluateCorpseLootDecision(doneCorpse).IsOwnDeathCorpse)
            && NothingLootedLine(corpseName) is { } nothingLine)
            ChatLine(nothingLine);
        LootDiag($"[RynthAi] Corpse loot: completed 0x{(uint)corpseId:X8}, releasing container.");

        // Lightweight target reset — deliberately NOT ResetCorpseTarget(), so the
        // next tick can claim the next corpse immediately. The no-gap property
        // this comment used to credit to holding BotAction="Looting" now comes
        // from HasLootWork: with another body still in range it keeps returning
        // true, so the arbiter never leaves Looting and nav never gets a step in.
        StopCorpseMovement();
        _targetCorpseId = 0;
        _corpseTargetSince = 0;
        _lastCorpseOpenAttemptAt = 0;
        _lastLootActionAt = 0;
    }

    private int IncrementCorpseItemAttempt(int itemId)
    {
        if (!_corpseItemAttempts.TryGetValue(itemId, out int attempts))
            attempts = 0;

        attempts++;
        _corpseItemAttempts[itemId] = attempts;
        return attempts;
    }

    private void ResetCurrentLootItem()
    {
        // An item done without its landing seen (given up, or dropped by a close / reopen /
        // stop) may still land: watch for it. Its unanswered-tries pack marks go with it, so
        // the next item and corpse start fresh (R-20261029-122319-321f).
        long now = CorpseNowMs;
        WatchLateLanding(now);
        _packRefusals.ClearUnconfirmed();
        _currentLootItemAwaitingAnswer = false;
        _currentLootItemRefusedAt = 0;
        _currentLootItemRefusalError = 0;
        _currentLootItemRefusalObject = 0;
        _currentLootItemRefusalBusy = false;
        _currentLootItemDestRefusals = 0;
        _currentLootItemDestTimeouts = 0;
        _currentLootItemTimedOut = false;
        _currentLootItemDestsTried.Clear();
        _currentLootItemFullPacks.Clear();
        _moveAwaitTickAt = 0;
        _currentLootItemId = 0;
        _currentLootItemRequested = false;
        _currentLootItemRequestedAt = 0;
        _currentLootItemMovePending = false;
        _currentLootItemMoveRequestedAt = 0;
        _currentLootItemMoveAttempts = 0;
        _currentLootItemDest = 0;
        _currentLootItemPackRetried = false;
        _currentLootItemStillSince = 0;
        _currentLootItemLeftAt = 0;
        _currentLootItemSawAnim = false;
        _currentLootItemStackable = false;
        _currentLootItemPipelined = false;
        _currentLootItemJumped = false;
        _currentLootItemSentInAirWindow = false;
        _flightHoldLogged = false;   // a hold is logged once per send; a corpse completed or abandoned starts fresh
        _currentLootItemErrSeqAtSend = 0;
        _currentLootItemFreeRetries = 0;
        _currentLootItemFightHeld = false;
        _currentLootItemFightResends = 0;
        _currentLootItemResendWaitLogged = false;
        _currentLootItemActionLabel = string.Empty;
        _currentLootItemName = string.Empty;
        _currentLootItemIsSalvage = false;
        _currentLootItemChat = null;
    }

    private void ApproachCorpse(int corpseId)
    {
        if (!TryGetHeadingToObject(corpseId, out double desiredDeg, out double absError, out double signedError))
            return;

        // Turn the way nav does (fast servo, turn in place on a big turn, steer while running).
        if (_navigationEngine != null && Host.HasTurnToHeading)
        {
            ClearCorpseTurnMotions();
            if (!_navigationEngine.ServoToward(desiredDeg))
            {
                if (_corpseAutorunActive) { Host.SetAutoRunBy("Loot", false); _corpseAutorunActive = false; }
                return;
            }
            long nowMs = CorpseNowMs;
            if (!_corpseAutorunActive || nowMs - _lastCorpseApproachHeartbeatAt >= 500)
            {
                Host.SetAutoRunBy("Loot", true);
                _corpseAutorunActive = true;
                _lastCorpseApproachHeartbeatAt = nowMs;
            }
            return;
        }

        // Fallback (no TurnToHeading on this host): the old motion-key turn.
        if (absError > 18.0)
        {
            StopCorpseMovement();
            // Use smooth motion turns, same as NavigationEngine/CombatManager
            if (signedError > 0)
            {
                Host.SetMotionBy("Loot", MotionTurnRight, true);
                Host.SetMotionBy("Loot", MotionTurnLeft, false);
            }
            else
            {
                Host.SetMotionBy("Loot", MotionTurnLeft, true);
                Host.SetMotionBy("Loot", MotionTurnRight, false);
            }
            _corpseApproachTurnsActive = true;
            return;
        }

        ClearCorpseTurnMotions();

        long now = CorpseNowMs;
        if (!_corpseAutorunActive || now - _lastCorpseApproachHeartbeatAt >= 500)
        {
            Host.SetAutoRunBy("Loot", true);
            _corpseAutorunActive = true;
            _lastCorpseApproachHeartbeatAt = now;
        }
    }

    private void ClearCorpseTurnMotions()
    {
        if (!_corpseApproachTurnsActive)
            return;
        _corpseApproachTurnsActive = false;
        Host.SetMotionBy("Loot", MotionTurnRight, false);
        Host.SetMotionBy("Loot", MotionTurnLeft, false);
    }

    private void StopCorpseMovement()
    {
        ClearCorpseTurnMotions();

        if (!_corpseAutorunActive)
            return;

        Host.SetAutoRunBy("Loot", false);
        if (Host.HasStopCompletely)
            Host.StopCompletelyBy("Loot");

        _corpseAutorunActive = false;
    }

    // Last corpse an open was sent for (AbandonCurrentCorpse: was this a failed open,
    // or a corpse the bot never reached?).
    private int _lastOpenAttemptCorpseId;

    /// <summary>Sends one open. False when the use-refusal watch acted instead (nothing sent).</summary>
    private bool AttemptOpenCorpse(int corpseId)
    {
        long now = CorpseNowMs;
        bool watch = Host.HasUseDoneSeq;
        if (watch)
        {
            // The previous open, answered with nothing opened, is judged before this one goes out:
            // a clean stop or a back-off must not land right after a fresh use (it would cancel it).
            var verdict = _useRefusals.SettleBeforeOpen(now);
            if (verdict != UseRefusalWatch.Verdict.None)
            {
                ApplyUseRefusalVerdict(verdict, now);
                return false;
            }

            // The back-off is over and this is the probe: jump first (clears the server's stuck
            // animation queue, see UseRefusalWatch), send the probe after the pause.
            if (_useRefusals.TakeProbeJump(now) && TryUseCheckJump(now, "before the probe"))
            {
                _lootHaltUntil = Math.Max(_lootHaltUntil, now + UseRefusalWatch.CleanStopPauseMs);
                _lastCorpseOpenAttemptAt = 0;
                _corpseTargetSince = now;
                return false;
            }
        }

        _lastOpenAttemptCorpseId = corpseId;
        StopCorpseMovement();
        int seqBefore = watch ? Host.GetUseDoneSeq() : 0;
        RynthCore.Plugin.RynthAi.ItemInfo.SelfSelect.SelectAsBot(Host, unchecked((uint)corpseId));
        bool sent = Host.UseFor(unchecked((uint)corpseId), "Loot", "open the claimed corpse");
        // Not with monsters about: a swing, a hit or a stance change cancels the server's walk to the
        // corpse, and the use is answered with nothing opened. That says nothing about the server, and
        // four of them in a wave fight paused looting for 5 minutes and told the player to relog
        // (2026-11-06). The check is for a calm character whose opens still don't work.
        if (watch && sent && _combatManager?.HasNearbyMonsters != true)
            _useRefusals.OnOpenSent(corpseId, now, seqBefore);
        // An open on a no-bend-down call: its answer says whether the server was free (DistrustJumpNoMotion).
        _openInAirWindowSeq = watch && sent && JumpPickupInAir(now) ? seqBefore : -1;
        LootDiag($"[RynthAi] Corpse loot: attempting open for 0x{(uint)corpseId:X8}.");
        // The finished corpse held open (MarkCorpseCompleted): this use closes it on the server.
        if (sent && _closeDeferredFor != 0 && _closeDeferredFor != corpseId)
        {
            LootDiag($"[RynthAi] Corpse loot: 0x{(uint)_closeDeferredFor:X8} is left for the server to close with this open.");
            _closeDeferredFor = 0;
        }
        return true;
    }

    // ── Server refuses every use (2026-10-06; Loot/UseRefusalWatch.cs) ─────────────────────
    private readonly UseRefusalWatch _useRefusals = new();
    private bool _useStuckAnnounced;
    private bool _lootWedgeAnnounced;

    /// <summary>Every tick while looting runs: the server's answers to our opens.</summary>
    private void TickUseRefusalWatch(long now)
    {
        if (!Host.HasUseDoneSeq)
            return;
        int seq = Host.GetUseDoneSeq();
        bool haveErr = Host.TryGetLastUseDone(out int lastSeq, out uint err) && lastSeq == seq;
        if (_openInAirWindowSeq >= 0 && seq != _openInAirWindowSeq)
        {
            if (haveErr && err == WeenieErrorYoureTooBusy)
                DistrustJumpNoMotion(now, "the next corpse's open, sent on it, was refused as too busy");
            _openInAirWindowSeq = -1;
        }
        ApplyUseRefusalVerdict(_useRefusals.OnTick(now, seq, err, haveErr), now);
    }

    private void ApplyUseRefusalVerdict(UseRefusalWatch.Verdict verdict, long now)
    {
        switch (verdict)
        {
            case UseRefusalWatch.Verdict.CleanStopAndRetry:
            {
                FlightRecorderSignal.Dump(Host, "use check: corpse opens answered but not opened");
                Log($"[RynthAi] Use check: {_useRefusals.Refusals} corpse opens in a row were answered by the server but nothing opened ({RefusedList()}) - one clean stop, a jump, a {UseRefusalWatch.CleanStopPauseMs} ms pause, then one more try.");
                Host.SetAutoRunBy("Loot/UseCheck", false);
                if (Host.HasStopCompletely)
                    Host.StopCompletelyBy("Loot/UseCheck");
                _corpseAutorunActive = false;
                _corpseApproachTurnsActive = false;
                // The jump is the fix (see UseRefusalWatch): it empties the server's animation
                // queues and cancels the stuck turn; the retry then gets a fresh server turn.
                bool jumped = TryUseCheckJump(now, "for the retry");
                // Without a jump: face the corpse for the retry. Since 2026-10-04 the bot leaves the
                // turn to the server; if the server can't turn the character any more, a corpse already
                // in front may still open (mid-stall, 06:28:19, a use by hand with the macro off
                // opened one - the only open in those 13 minutes; whether it was faced isn't logged).
                if (!jumped && _targetCorpseId != 0 && Host.HasTurnToHeading
                    && TryGetHeadingToObject(_targetCorpseId, out double faceDeg, out double faceErr, out _)
                    && faceErr > 2.0)
                {
                    Host.TurnToHeadingBy("Loot/UseCheck", (float)faceDeg);
                    Log($"[RynthAi] Use check: facing 0x{(uint)_targetCorpseId:X8} ({faceErr:0} deg off) for the retry.");
                }
                _lootHaltUntil = Math.Max(_lootHaltUntil, now + UseRefusalWatch.CleanStopPauseMs);
                _lastCorpseOpenAttemptAt = 0;          // the retry goes out as soon as the pause ends
                if (_targetCorpseId != 0)
                    _corpseTargetSince = now;          // and gets a full corpse timeout
                break;
            }
            case UseRefusalWatch.Verdict.DropCorpse:
            {
                int id = _useRefusals.LastRefusedCorpse;
                Log($"[RynthAi] Use check: corpse 0x{(uint)id:X8} is answered but never opens - giving up on it; the next corpse tells whether every use is refused.");
                if (id != 0 && _targetCorpseId == id && _openedContainerId == 0)
                    AbandonCurrentCorpse(now, "refused");
                else if (id != 0)
                    _corpseCooldownUntil[id] = now + NeverOpenedAbandonRetryMs;
                break;
            }
            case UseRefusalWatch.Verdict.Stuck:
            {
                FlightRecorderSignal.Dump(Host, "use check: server refuses every use");
                Log($"[RynthAi] Use check: the server answers every use but nothing opens ({_useRefusals.Refusals} refusals on {RefusedList()}), the clean stop{(_useCheckJumpAt != 0 ? ", the jump" : "")} and retry didn't help - looting paused {_useRefusals.BackoffMs / 1000} s, then one corpse is tried again{(Host.HasTapJump ? " after a jump" : "")}.");
                _useCheckJumpAt = 0;
                if (!_useStuckAnnounced)
                {
                    _useStuckAnnounced = true;
                    ChatLine($"[RynthAi] The server isn't letting this character use objects; a relog fixes it. Looting is paused and tried again every minute or two.");
                }
                ResetCorpseTarget();
                break;
            }
            case UseRefusalWatch.Verdict.StillStuck:
                Log($"[RynthAi] Use check: still refused{(_useCheckJumpAt != 0 ? " after the jump" : "")} - next try in {_useRefusals.BackoffMs / 1000} s.");
                _useCheckJumpAt = 0;
                ResetCorpseTarget();
                break;
            case UseRefusalWatch.Verdict.Recovered:
                Log("[RynthAi] Use check: a corpse opened - uses work again, looting resumes.");
                if (_useStuckAnnounced)
                    ChatLine("[RynthAi] Objects open again; looting is back on.");
                _useStuckAnnounced = false;
                break;
        }
    }

    /// <summary>When the use check last jumped (0 = not since the last open).</summary>
    private long _useCheckJumpAt;

    /// <summary>
    /// One jump in place for the use check. On ACE the jump (0xF61B, the keyboard path: TapJump)
    /// is the only in-world action that empties the server's animation queues for the character
    /// (Player.HandleActionJump: RemoveLinkAnimations drains MotionTableManager.PendingAnimations,
    /// MotionInterp.PendingMotions.Clear(), IsAnimating = false) and it cancels the stuck walk/turn
    /// (GameActionJump -> StopExistingMoveToChains2). JumpNonAutonomous (0xF7C9) is not used: ACE
    /// has no handler for it. Not while the pacer has an action out (a jump in Magic stance fails a
    /// cast on the server).
    /// </summary>
    private bool TryUseCheckJump(long now, string why)
    {
        if (!Host.HasTapJump)
            return false;
        // Not with monsters about (2026-10-09): mid-fight, an open is answered without opening
        // because a swing, a hit or a stance change cancels the server's walk/turn to the
        // corpse - not the stuck queue the jump clears - and the jump looked like the nav's
        // stuck fix firing while the character was busy. The retry still goes out, unjumped.
        if (_combatManager?.HasNearbyMonsters == true)
        {
            Log($"[RynthAi] Use check: no jump {why} - monsters nearby (a fight cancels opens; it isn't the stuck queue).");
            return false;
        }
        if (ActionPacer.Outstanding(Host, out var pending) && pending != null)
        {
            Log($"[RynthAi] Use check: no jump {why} - the server still owes an answer to {ActionPacer.Describe(pending.Kind, pending.Label)}.");
            return false;
        }
        Host.SetAutoRunBy("Loot/UseCheck", false);
        bool ok = Host.TapJump();
        if (ok)
            _useCheckJumpAt = now;
        Log($"[RynthAi] Use check: jump {why} (clears the server's stuck animation queue; a relog was the only other fix) ok={ok}.");
        return ok;
    }

    private string RefusedList() =>
        string.Join(", ", _useRefusals.RefusedCorpses.Select(id => $"0x{(uint)id:X8}"));

    // The arbiter's wantLoot of the current tick (HasLootWork), for LootHoldsIdlePeace.
    private bool _lastWantLoot;

    /// <summary>
    /// CombatManager's idle peace-mode swap waits while looting has work: a corpse to claim, a
    /// claimed or open corpse, or a door/corpse use the server is still answering. On ACE a
    /// combat-mode change cancels the server's walk/turn to a used corpse (HandleActionCancelAttack
    /// -> cancel_moveto), so the swap that went out 3 ms after the 06:18:44 open (2026-10-06) made
    /// it fail; and between kills in a busy spawn it flipped peace/war every few seconds. Opening a
    /// corpse needs no peace mode; the swap happens once looting is done.
    /// </summary>
    private bool LootHoldsIdlePeace() =>
        _targetCorpseId != 0
        || _openedContainerId != 0
        || _lastWantLoot
        || RynthCore.Plugin.Shared.UseAudit.WorldUseInFlight(Host, out _);

    /// <summary>A fresh session (logout/login): a relog is what clears a server that refuses uses.</summary>
    private void ResetUseRefusalState()
    {
        _useRefusals.Reset();
        _useCheckJumpAt = 0;
        _useStuckAnnounced = false;
        _lootWedgeAnnounced = false;
        _consecutiveLootTimeouts = 0;
        _lootHaltUntil = 0;
    }

    private void SyncCorpseContainerState()
    {
        if (!Host.HasGetGroundContainerId)
            return;

        int rawOpen = unchecked((int)Host.GetGroundContainerId());
        if (rawOpen == _openedContainerId)
            return;

        // A chest being looted ends on its own close (OnStopViewingObjectContents), not here: the
        // engine's ground container is the last ViewContents, which a pack picked up out of the
        // chest moves too. The loot timeout still ends it if the close is never seen.
        if (IsChestLoot(_openedContainerId))
            return;

        long now = CorpseNowMs;
        // A finished corpse the client still shows (its close from the server not in yet: the next
        // corpse was used without closing it, MarkCorpseCompleted) is not a close of the corpse we
        // have open now. A real close of that one empties the client's container (or brings
        // OnStopViewingObjectContents), never shows the old corpse again.
        if (_openedContainerId != 0 && rawOpen != 0 && rawOpen != _openedContainerId && _completedCorpses.ContainsKey(rawOpen))
            return;
        if (_openedContainerId != 0 && rawOpen != _openedContainerId)
        {
            int previousOpen = _openedContainerId;
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;

            if (_targetCorpseId == previousOpen)
            {
                if (_completedCorpses.ContainsKey(previousOpen))
                {
                    LootDiag($"[RynthAi] Corpse loot: observed close for completed corpse 0x{(uint)previousOpen:X8}.");
                    ResetCorpseTarget();
                    return;
                }

                ResetCurrentLootItem();
                _lastCorpseOpenAttemptAt = 0;
                _lastLootActionAt = now;
                LootDiag($"[RynthAi] Corpse loot: reconciled closed corpse 0x{(uint)previousOpen:X8}; will reopen if still active.");
            }
        }

        if (_openedContainerId == 0 && rawOpen != 0 && IsCorpseLikeObject(rawOpen))
        {
            // Don't re-claim a corpse we've already completed — the server still
            // reports it as open, but we're done with it. It will close when we
            // open the next corpse or move away.
            if (_completedCorpses.ContainsKey(rawOpen))
                return;

            bool switchedTarget = _targetCorpseId != 0 && _targetCorpseId != rawOpen;
            _openedContainerId = rawOpen;
            _openedContainerAt = now;
            _openedContainerInventoryObservedAt = 0;
            _lastLootActionAt = 0;
            ResetCurrentLootItem();
            if (switchedTarget)
            {
                _processedCorpseItems.Clear();
                _corpseItemAttempts.Clear();
                _pendingManaStoneIds.Clear(); _pendingManaTapIds.Clear(); _pendingKeepUpTo.Clear();
            }

            _targetCorpseId = rawOpen;
            _corpseTargetSince = now;
            _lastCorpseOpenAttemptAt = 0;
            LootDiag($"[RynthAi] Corpse loot: reconciled open corpse 0x{(uint)rawOpen:X8} from client state.");
        }
    }

    /// <summary>
    /// Drop the current corpse claim and reset the per-corpse item cursor.
    ///
    /// STEP 4: this used to take a releaseState flag whose only job was "hand
    /// the Looting string back to Default so nav can resume". Every caller
    /// passed true, and there is nothing to hand back any more — the arbiter
    /// recomputes from HasLootWork every tick, so clearing _targetCorpseId here
    /// is the entire signal. The parameter is gone rather than left vestigial.
    /// </summary>
    private void ResetCorpseTarget()
    {
        StopCorpseMovement();
        // The no-bend-down window belongs to the loot that set it (the macro off, a claim dropped).
        // ActionPacer's air hold is the character's own flight: it ends on the landing, or at death,
        // portal space and logout (RynthAiPlugin), not with a corpse.
        _jumpPickupAirUntil = 0;
        _openInAirWindowSeq = -1;
        _targetCorpseId = 0;
        _corpseTargetSince = 0;
        _openedContainerAt = 0;
        _openedContainerInventoryObservedAt = 0;
        _lastCorpseOpenAttemptAt = 0;
        _lastLootActionAt = 0;
        _corpseIdsRequested = false;
        _corpseApproachTurnsActive = false;
        _fightHoldSince = 0;
        _fightHoldTickAt = 0;
        ResetCurrentLootItem();
        _processedCorpseItems.Clear();
        _corpseItemAttempts.Clear();
        _pendingManaStoneIds.Clear(); _pendingManaTapIds.Clear(); _pendingKeepUpTo.Clear();
    }

    private void HandleCorpseObjectDeleted(uint objectId)
    {
        int sid = unchecked((int)objectId);
        // A chest leaving range (or despawning) ends its session; it is never completed-marked.
        if (OnChestGone(sid, "left range"))
            return;
        // Do NOT remove from _completedCorpses here. In AC, DeleteObject fires when a corpse
        // leaves render range as well as when it actually despawns — both look identical.
        // If we removed it here, the bot would re-open the same corpse when it circles back
        // into range. PruneCorpseCollections handles eventual cleanup of truly-gone entries.
        _processedCorpseItems.Remove(sid);
        _corpseItemAttempts.Remove(sid);
        _corpseCooldownUntil.Remove(sid);
        _ownershipSkipLogged.Remove(sid);
        _corpseIdRequested.Remove(sid);
        _corpseIdAskedAt.Remove(sid);
        _corpseIdRefusedAt.Remove(sid);
        _corpseIdAskCount.Remove(sid);
        _corpseSkipLogged.Remove(sid);
        _corpseWaitingSince.Remove(sid);

        // If this is the corpse we were actively looting (container was open and we received
        // at least one inventory update), mark it completed before resetting. Without this,
        // the corpse leaves range without an entry in _completedCorpses; on the next patrol
        // pass TryFindNearestCorpse picks it up again and the bot re-opens an already-looted
        // corpse. Marking it here is safe: if items remained, they're unrecoverable once
        // we've walked far enough for DeleteObject to fire; if all matching items were already
        // taken, the mark prevents the pointless re-open.
        if (_targetCorpseId == sid && _openedContainerId == sid && _openedContainerInventoryObservedAt != 0)
        {
            LootDiag($"[RynthAi] Corpse loot: corpse 0x{objectId:X8} left range mid-loot; marking completed to prevent re-open.");
            _completedCorpses[sid] = CorpseNowMs;
        }

        if (_targetCorpseId == sid)
            ResetCorpseTarget();

        if (_openedContainerId == sid)
            _openedContainerId = 0;
    }

    private void MarkCorpseCooldown(int corpseId, int delayMs)
    {
        if (corpseId == 0)
            return;

        _corpseCooldownUntil[corpseId] = CorpseNowMs + Math.Max(500, delayMs);
    }

    private bool IsCorpseLikeObject(int objectId)
    {
        if (objectId == 0)
            return false;

        if ((_objectCache?[objectId]?.ObjectClass ?? AcObjectClass.Unknown) == AcObjectClass.Corpse)
            return true;

        if (_targetCorpseId != 0 && objectId == _targetCorpseId)
            return true;

        if (!Host.TryGetObjectName(unchecked((uint)objectId), out string name) || string.IsNullOrWhiteSpace(name))
            return false;

        return name.EndsWith(" corpse", StringComparison.OrdinalIgnoreCase)
            || name.Contains("'s corpse", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("corpse of ", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetHeadingToObject(int targetId, out double desiredDeg, out double absError, out double signedError)
    {
        desiredDeg = 0;
        absError = 180.0;
        signedError = 0;

        if (!Host.TryGetPlayerPose(out _, out float px, out float py, out _, out float qw, out _, out _, out float qz))
            return false;

        if (!Host.TryGetObjectPosition(unchecked((uint)targetId), out _, out float tx, out float ty, out _))
            return false;

        double dx = tx - px;
        double dy = ty - py;
        desiredDeg = Math.Atan2(dx, dy) * (180.0 / Math.PI);
        if (desiredDeg < 0)
            desiredDeg += 360.0;

        double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        double currentDeg = ((-physYawDeg) % 360.0 + 720.0) % 360.0;

        double error = desiredDeg - currentDeg;
        while (error > 180.0) error -= 360.0;
        while (error < -180.0) error += 360.0;
        signedError = error;
        absError = Math.Abs(error);
        return true;
    }

    private static double GetCorpseApproachRangeMaxMeters(LegacyUiSettings settings)
    {
        return ConvertYardsToMeters(NormalizeCorpseRangeYards(settings.CorpseApproachRangeMax, 10.0));
    }

    private static double GetCorpseApproachRangeMinMeters(LegacyUiSettings settings, double maxMeters)
    {
        double minMeters = ConvertYardsToMeters(NormalizeCorpseRangeYards(settings.CorpseApproachRangeMin, 2.0));
        if (minMeters <= 0.25)
            minMeters = Math.Min(1.5, maxMeters);
        if (minMeters > maxMeters)
            minMeters = maxMeters;
        return minMeters;
    }

    /// <summary>
    /// How long a chest session may go without a pickup (the corpse timeout when that is longer).
    /// A chest can hold dozens of items to assess and take, so the corpse timeout (12 s by default)
    /// cut sessions short; each confirmed pickup restarts this clock (ConfirmCurrentLootItemMoved).
    /// </summary>
    internal const int ChestLootTimeoutMs = 120_000;

    private int GetCorpseTimeoutMs(LegacyUiSettings settings)
    {
        int ms = Math.Max(2000, settings.LootCorpseTimeoutMs);
        return IsChestLoot(_targetCorpseId) ? Math.Max(ms, ChestLootTimeoutMs) : ms;
    }

    private bool HasCorpseTimedOut(LegacyUiSettings settings, long now)
    {
        return _targetCorpseId != 0
            && _corpseTargetSince != 0
            && now - _corpseTargetSince >= GetCorpseTimeoutMs(settings);
    }

    // ── Loot-timeout clock freeze during buff/combat holds ────────────────
    // The holds in TickCorpseOpening early-return while a corpse is claimed;
    // without this, _corpseTargetSince kept aging through any fight/buff cycle
    // longer than LootCorpseTimeoutMs, and the first tick after the hold lifted
    // abandoned a never-opened corpse (completed-marked → loot lost) and fed
    // the wedge detector a phantom timeout. Re-pinning the age each held tick
    // keeps (now − _corpseTargetSince) constant; the clock resumes when work does.
    private long _corpseHoldFrozenAge = -1;

    private void FreezeCorpseTimeout(long now)
    {
        if (_targetCorpseId == 0 || _corpseTargetSince == 0)
        {
            _corpseHoldFrozenAge = -1;
            return;
        }
        if (_corpseHoldFrozenAge < 0)
            _corpseHoldFrozenAge = now - _corpseTargetSince;
        _corpseTargetSince = now - _corpseHoldFrozenAge;
    }

    /// <summary>
    /// Closes the loot window of a corpse the bot is done with, when it is the open container:
    /// Host.CloseContainer (API v76) sends the 0x0195 NoLongerViewingContents game action, the
    /// packet the client sends when the player closes the window. It is not an inventory request:
    /// AC's busy count doesn't move (no OnBusyCountIncremented, so the next open isn't held) and
    /// the client's one pending item request is left alone. The engine queues it on AC's main
    /// thread ahead of the next corpse's UseObject. The server answers with CloseGroundContainer,
    /// which closes the window and brings OnStopViewingObjectContents. Older engines: nothing
    /// (the server closes the corpse when the next one is opened, as before).
    /// </summary>
    // The last close sent, until the server's CloseGroundContainer answers it (see TickCorpseOpening).
    private int _closeSentFor;
    private long _closeSentAt;
    private const long CloseAnswerWaitMs = 500;

    // A finished corpse left open for the server to close when the next one is used (MarkCorpseCompleted).
    private int _closeDeferredFor;
    private long _closeDeferredAt;
    /// <summary>A held-back close is sent anyway when no other corpse has been opened this long.</summary>
    internal const long DeferredCloseMaxMs = 3000;

    /// <summary>
    /// Another corpse to loot is in range (the finished one is already marked completed) and the
    /// looter can go to it now: <see cref="CanClaimNextCorpse"/>. A corpse that combat, a full pack, a
    /// refusing server or a buff / mana drain hold keeps from being claimed would leave the finished
    /// one open for nothing (metas and Lua read the open container meanwhile).
    /// </summary>
    private bool OtherCorpseToLoot(int doneCorpseId)
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null) return false;
        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        if (maxMeters <= 0.25) return false;
        if (!CanClaimNextCorpse(settings, maxMeters)) return false;
        return TryFindNearestCorpse(maxMeters, out WorldObject? next, out _) && next != null && next.Id != doneCorpseId;
    }

    /// <summary>
    /// The holds TickCorpseOpening applies before it claims or opens a corpse, as one answer for
    /// "would the next corpse be claimed now": looting blocked by a full pack (your own corpse
    /// excepted), the use-refusal back-off, buffing, a mana drain, and combat's priority (the open
    /// container no longer shields a claim once the finished corpse is released). What the activity
    /// arbiter hands out later is not known here; the deferred close is sent after DeferredCloseMaxMs,
    /// or as soon as one of these holds starts (TickDeferredClose). Distance is no reason to close
    /// early: a far corpse is used at once and the server walks the character to it.
    /// </summary>
    private bool CanClaimNextCorpse(LegacyUiSettings settings, double maxMeters)
    {
        if (IsLootingBlockedByPack(settings) && !OwnCorpseBypassesPackGate(settings, maxMeters)) return false;
        if (_useRefusals.HoldsOpens(CorpseNowMs)) return false;
        if (string.Equals(settings.BotAction, "Buffing", StringComparison.OrdinalIgnoreCase)) return false;
        if (_manaStoneManager?.HoldsLootForDrain() == true) return false;
        if (settings.EnableCombat && _combatManager?.HasTargets == true && !settings.BoostLootPriority && !_lootStarveTurn)
            return false;
        return true;
    }

    /// <summary>Holds the close of a finished corpse back. Only when the engine can close it later and
    /// reports which container the client has open (so the hold can see the server close it).</summary>
    private bool DeferClose(int corpseId)
    {
        if (!Host.HasCloseContainer || !Host.HasGetGroundContainerId)
            return false;
        if (unchecked((int)Host.GetGroundContainerId()) != corpseId && _openedContainerId != corpseId)
            return false;
        _closeDeferredFor = corpseId;
        _closeDeferredAt = CorpseNowMs;
        LootDiag($"[RynthAi] Corpse loot: 0x{(uint)corpseId:X8} done; no close sent (the server closes it when the next corpse is used).");
        return true;
    }

    /// <summary>
    /// Each tick: a held-back close. Gone from the client (the server closed it): done. Looting
    /// stopped, or no other corpse opened within DeferredCloseMaxMs: the close is sent now.
    /// </summary>
    private void TickDeferredClose(LegacyUiSettings settings)
    {
        int id = _closeDeferredFor;
        if (id == 0) return;
        if (!Host.HasGetGroundContainerId || unchecked((int)Host.GetGroundContainerId()) != id)
        {
            _closeDeferredFor = 0;
            LootDiag($"[RynthAi] Corpse loot: 0x{(uint)id:X8} closed by the server.");
            return;
        }
        bool stopped = !settings.IsMacroRunning || !settings.EnableLooting || BoostNavHoldsLoot(settings);
        // A hold that began after the close was deferred (a fight, a full pack) ends the wait too.
        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        bool blocked = !stopped && maxMeters > 0.25 && !CanClaimNextCorpse(settings, maxMeters);
        if (!stopped && !blocked && CorpseNowMs - _closeDeferredAt < DeferredCloseMaxMs)
            return;
        _closeDeferredFor = 0;
        CloseCorpseContainer(id, stopped ? "completed; looting stopped" : blocked ? "completed; the next corpse can't be claimed now" : "completed; no other corpse opened");
    }

    private void CloseCorpseContainer(int corpseId, string why)
    {
        if (corpseId == 0 || !Host.HasCloseContainer)
            return;

        bool isOpen = _openedContainerId == corpseId
            || (Host.HasGetGroundContainerId && unchecked((int)Host.GetGroundContainerId()) == corpseId);
        if (!isOpen)
            return;

        if (Host.CloseContainer(unchecked((uint)corpseId)))
        {
            _closeSentFor = corpseId;
            _closeSentAt = CorpseNowMs;
            LootDiag($"[RynthAi] {LootTag}: closing 0x{(uint)corpseId:X8} ({why}).");
        }
        else
            LootDiag($"[RynthAi] {LootTag}: close of 0x{(uint)corpseId:X8} not sent ({why}).");
    }

    /// <summary>
    /// The macro was switched off with a corpse open: close it (the corpse isn't marked
    /// completed, so a later run can still loot what is left). The open-container state is
    /// cleared by the server's answer (OnStopViewingObjectContents), as for a window the
    /// player closes. Called once, on the running-to-stopped transition, so a corpse the
    /// player opens by hand while the macro is off is never closed.
    /// </summary>
    private void CloseOpenCorpseOnMacroStop()
    {
        int corpseId = _openedContainerId;
        if (corpseId == 0 || !(IsCorpseLikeObject(corpseId) || IsChestLoot(corpseId)))
            return;

        ResetCurrentLootItem();
        CloseCorpseContainer(corpseId, "macro stopped");
    }

    private void AbandonCurrentCorpse(long now, string reason)
    {
        int corpseId = _targetCorpseId != 0 ? _targetCorpseId : _openedContainerId;
        if (corpseId == 0)
            return;

        long ageMs = _corpseTargetSince != 0 ? now - _corpseTargetSince : 0;

        // A chest: closed and let go, never marked (no open was sent, so no wedge count either).
        if (IsChestLoot(corpseId))
        {
            LootDiag($"[RynthAi] Chest loot: giving up on chest 0x{(uint)corpseId:X8} after {ageMs}ms ({reason}).");
            ChatLine($"[RynthAi] Chest {reason}: giving up on 0x{(uint)corpseId:X8} after {ageMs}ms.");
            FinishChestLoot(corpseId, "abandoned", close: true, sayNothingMatched: false);
            return;
        }
        LootDiag($"[RynthAi] Corpse loot: abandoning corpse 0x{(uint)corpseId:X8} after {ageMs}ms ({reason}).");
        ChatLine($"[RynthAi] Corpse {(reason == "refused" ? "refuses to open" : "timeout")}: giving up on 0x{(uint)corpseId:X8} after {ageMs}ms.");

        // Only a corpse we actually OPENED earns the completed mark (30-min
        // do-not-touch — its loot is permanently skipped). A never-opened
        // abandon (approach failed, open refused) gets a short retry cooldown
        // instead; after a few never-opened abandons, give up for real so an
        // unreachable corpse can't cycle forever.
        bool wasOpened = _openedContainerId == corpseId;
        bool openWasSent = wasOpened || _lastOpenAttemptCorpseId == corpseId;
        int abandons =_corpseAbandonCounts.TryGetValue(corpseId, out int n) ? n + 1 : 1;
        _corpseAbandonCounts[corpseId] = abandons;
        // Your own corpse is never written off after one timeout: it gets OwnCorpseMaxAbandons tries.
        bool ownCorpse = IsOwnDeathCorpseId(_dashboard?.Settings, corpseId);
        if (ownCorpse && abandons < OwnCorpseMaxAbandons)
        {
            _corpseCooldownUntil[corpseId] = now + NeverOpenedAbandonRetryMs;
            LootDiag($"[RynthAi] Own corpse: 0x{(uint)corpseId:X8} timed out (try {abandons} of {OwnCorpseMaxAbandons}); trying again in {NeverOpenedAbandonRetryMs / 1000}s.");
        }
        else if (ownCorpse)
        {
            _completedCorpses[corpseId] = now;
            _corpseCooldownUntil.Remove(corpseId);
            _corpseAbandonCounts.Remove(corpseId);
            ChatLine($"[RynthAi] Own corpse: giving up after {abandons} tries; recover what is left on it by hand.");
        }
        else if (wasOpened || abandons >= 3)
        {
            _completedCorpses[corpseId] = now;
            _corpseCooldownUntil.Remove(corpseId);
            _corpseAbandonCounts.Remove(corpseId);
        }
        else
        {
            _corpseCooldownUntil[corpseId] = now + NeverOpenedAbandonRetryMs;
            LootDiag($"[RynthAi] Corpse loot: 0x{(uint)corpseId:X8} was never opened (abandon #{abandons}) — retry in {NeverOpenedAbandonRetryMs / 1000}s instead of completed-marking.");
        }

        // Close it the client's way (no busy count; see CloseCorpseContainer) and force-clear
        // container state. Never UseObject to close. Without CloseContainer the server closes
        // it when we open the next corpse or move away.
        CloseCorpseContainer(corpseId, "abandoned");
        if (_openedContainerId == corpseId)
        {
            _openedContainerId = 0;
            _openedContainerAt = 0;
            _openedContainerInventoryObservedAt = 0;
        }

        ResetCorpseTarget();

        // Track open timeouts to detect a wedged item-action state. A successful
        // open resets this counter; a run of timeouts means AC stopped accepting
        // item actions (the "you can only move or use one item at a time" lock).
        // Only when an open was actually sent: a corpse the bot never reached (autorun
        // into a wall, a body on a ledge) says nothing about AC's item-action state,
        // and three of those in a row fired the full wedge reset.
        if (openWasSent && reason.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _consecutiveLootTimeouts++;
            if (_consecutiveLootTimeouts >= LootWedgeThreshold)
                HandleLootWedge(now);
        }
    }

    /// <summary>
    /// AC's item-action queue is wedged (N consecutive corpse opens timed out).
    /// Halt looting briefly, fire one client-side reset (same actions as
    /// /ra panic), and tell the user — a server-side lock only clears on relog,
    /// so the worst thing we can do is keep hammering opens into a dead client.
    /// </summary>
    private void HandleLootWedge(long now)
    {
        FlightRecorderSignal.Dump(Host, "loot wedge");
        _lootHaltUntil = now + LootWedgeHaltMs;
        _consecutiveLootTimeouts = 0; // re-arm; if still wedged it trips again after the halt

        if (Host.HasCancelAttack)        Host.CancelAttack();
        // Peace only when not already in it (2026-10-06): an unneeded request went to the server as
        // a raw 0x53 anyway, and on ACE a mode change cancels whatever walk/turn is pending. Not
        // while a server action is unanswered either (ChangeCombatModeFor holds it then).
        Host.ChangeCombatModeFor(CombatMode.NonCombat, "Loot/Wedge", "loot wedge reset");
        if (Host.HasStopCompletely)      Host.StopCompletelyBy("Loot/Wedge");
        // No ForceResetBusyCount (2026-10-06): AC's busy count is the count of actions the server
        // has not answered; zeroing it fixed nothing here (the lock is the server's or the
        // client's item-request gates below) and let the next action out on top of them.
        // The lock is client-side: AC's pending-item-request slot or attacking
        // flag (see the engine's ClientActionGates). Open both now.
        if (Host.HasInvokeChatParser)    Host.InvokeChatParser("/rc unlockactions");
        _busyCount = 0;
        _busyCountLastIncrementAt = 0;
        _busyCountBecamePositiveAt = 0;
        if (_combatManager != null) _combatManager.BusyCount = 0;
        if (_buffManager != null)   _buffManager.BusyCount = 0;

        // Once per stall (until a corpse opens): 2026-10-06 it fired 11 times in 13 minutes.
        if (!_lootWedgeAnnounced)
        {
            _lootWedgeAnnounced = true;
            ChatLine($"[RynthAi] WEDGE: {LootWedgeThreshold} corpse opens timed out — AC item-actions are locked. Halting looting {LootWedgeHaltMs / 1000}s and unlocking. If equip/loot stays stuck, /rc actionstate shows what the client is waiting on.");
        }
        Log($"[RynthAi] LOOT WEDGE detected ({LootWedgeThreshold} consecutive open timeouts) — halt {LootWedgeHaltMs}ms + reset fired.");
    }

    private static double NormalizeCorpseRangeYards(double value, double fallbackYards)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            return fallbackYards;

        return value <= 1.0 ? value * 240.0 : value;
    }

    private static double ConvertYardsToMeters(double yards)
    {
        return Math.Max(0.0, yards) * 0.9144;
    }

    private static double ConvertMetersToYards(double meters)
    {
        return meters / 0.9144;
    }

    private void HandleCorpseInfoCommand()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
        {
            ReplyFail("[RynthAi] Corpse info not ready yet.");
            return;
        }

        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        double minMeters = GetCorpseApproachRangeMinMeters(settings, maxMeters);
        ChatLine($"[RynthAi] Corpse ranges: max={ConvertMetersToYards(maxMeters):F1} yd / {maxMeters:F1} m, min={ConvertMetersToYards(minMeters):F1} yd / {minMeters:F1} m");
        uint rawOpen = Host.HasGetGroundContainerId ? Host.GetGroundContainerId() : 0;
        ChatLine($"[RynthAi] Corpse state: target=0x{(uint)_targetCorpseId:X8}, corpse-open=0x{(uint)_openedContainerId:X8}, raw-open=0x{rawOpen:X8}, busy={_busyCount}, inv-update={(_openedContainerInventoryObservedAt != 0 ? 1 : 0)}, completed={_completedCorpses.Count}");
        long infoNow = CorpseNowMs;
        ChatLine($"[RynthAi] Use check: {_useRefusals.State}, {_useRefusals.Refusals} refused open(s) since the last open"
                 + (_useRefusals.HoldsOpens(infoNow) ? $", looting paused {(_useRefusals.BackoffUntil - infoNow) / 1000}s more (the server refuses uses; a relog fixes it)" : "")
                 + (Host.HasUseDoneSeq ? "" : " (this engine can't report the server's answers: the check is off)"));

        if (TryFindNearestCorpse(maxMeters, out WorldObject? corpse, out double distanceMeters))
            ChatLine($"[RynthAi] Nearest corpse: {corpse!.Name} (0x{(uint)corpse.Id:X8}) at {ConvertMetersToYards(distanceMeters):F1} yd / {distanceMeters:F1} m");
        else
            ChatLine("[RynthAi] No unopened corpses are currently within corpse max range.");
    }

    private void HandleCorpseCheckCommand(string[] parts)
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
        {
            ReplyFail("[RynthAi] Corpse ownership diagnostics are not ready yet.");
            return;
        }

        string selector = parts.Length >= 3 ? parts[2].Trim().ToLowerInvariant() : string.Empty;
        if (!TryResolveCorpseForDiagnostics(selector, out WorldObject? corpse, out string source))
        {
            ReplyFail("[RynthAi] No corpse found for diagnostics. Use /ra corpsecheck [target|open|nearest].");
            return;
        }

        if (Host.HasRequestId)
            Host.RequestId(unchecked((uint)corpse!.Id));

        CorpseLootDecision decision = EvaluateCorpseLootDecision(corpse);
        ChatLine($"[RynthAi] Corpse check ({source}): {corpse!.Name} (0x{(uint)corpse.Id:X8})");
        ChatLine($"[RynthAi]   Loot From: {decision.ModeName}");
        ChatLine($"[RynthAi]   Would loot: {(decision.ShouldLoot ? "YES" : "NO")}");
        ChatLine($"[RynthAi]   Reason: {decision.Reason}");
        ChatLine($"[RynthAi]   Player: {(string.IsNullOrWhiteSpace(decision.PlayerName) ? "<unknown>" : decision.PlayerName)}");
        ChatLine($"[RynthAi]   Killer: {(string.IsNullOrWhiteSpace(decision.KillerName) ? "<unknown>" : decision.KillerName)}");
        ChatLine($"[RynthAi]   Fellowship: {(decision.InFellowship ? "yes" : "no")} | killer is fellow: {(decision.KillerIsFellow ? "yes" : "no")}");
        if (!string.IsNullOrWhiteSpace(decision.LongDesc))
            ChatLine($"[RynthAi]   LongDesc: {decision.LongDesc}");
        else
            ChatLine("[RynthAi]   LongDesc: <empty> (ID was requested; run the command again if the corpse was not identified yet)");
    }

    private void HandleFellowshipInfoCommand()
    {
        HandleFellowshipCommand(["/ra", "fellow", "status"]);
    }

    private void HandleFellowshipCommand(string[] parts)
    {
        if (_fellowshipTracker == null)
        {
            ReplyFail("[RynthAi] Fellowship tracker is not ready yet.");
            return;
        }

        string fellowSub = parts.Length >= 3 ? parts[2].Trim().ToLowerInvariant() : "status";
        if (!_fellowshipTracker.IsInFellowship && fellowSub != "help")
        {
            ReplyFail("[RynthAi] Not in a fellowship.");
            return;
        }

        switch (fellowSub)
        {
            case "status":
                PrintFellowshipStatus();
                break;

            case "leader":
                {
                    int leaderId = _fellowshipTracker.LeaderId;
                    string leaderName = FindFellowshipMemberNameById(leaderId);
                    ChatLine($"[RynthAi] Leader: {leaderName} (0x{unchecked((uint)leaderId):X8}){(_fellowshipTracker.IsLeader ? " (you)" : "")}");
                    break;
                }

            case "count":
                ChatLine($"[RynthAi] Fellowship member count: {_fellowshipTracker.MemberCount}");
                break;

            case "names":
                {
                    var names = new List<string>(_fellowshipTracker.GetMemberNames());
                    ChatLine($"[RynthAi] Members ({names.Count}): {string.Join(", ", names)}");
                    break;
                }

            case "name":
                ChatLine($"[RynthAi] Fellowship name: \"{_fellowshipTracker.FellowshipName}\"");
                break;

            case "open":
                ChatLine($"[RynthAi] Open: {_fellowshipTracker.IsOpen}");
                break;

            case "locked":
                ChatLine($"[RynthAi] Locked: {_fellowshipTracker.IsLocked}");
                break;

            case "sharexp":
                ChatLine($"[RynthAi] ShareXP: {_fellowshipTracker.ShareXP}");
                break;

            case "ismember":
                if (parts.Length >= 4)
                {
                    string checkName = string.Join(" ", parts, 3, parts.Length - 3);
                    bool found = _fellowshipTracker.IsMember(checkName);
                    ChatLine($"[RynthAi] IsMember(\"{checkName}\"): {found}");
                }
                else
                {
                    ReplyFail("[RynthAi] Usage: /ra fellow ismember <name>");
                }
                break;

            case "help":
                ChatLine("[RynthAi] /ra fellow [status|leader|count|names|name|open|locked|sharexp|ismember <name>]");
                break;

            default:
                ReplyFail($"[RynthAi] Unknown fellow subcommand: {fellowSub}. Try /ra fellow help");
                break;
        }
    }

    private void PrintFellowshipStatus()
    {
        if (_fellowshipTracker == null)
        {
            ChatLine("[RynthAi] Fellowship tracker is not ready yet.");
            return;
        }

        string playerName = GetPlayerNameForLootOwnership();
        ChatLine($"[RynthAi] Fellowship: \"{_fellowshipTracker.FellowshipName}\"");
        ChatLine($"[RynthAi] Player: {(string.IsNullOrWhiteSpace(playerName) ? "<unknown>" : playerName)}");
        ChatLine($"[RynthAi] Members: {_fellowshipTracker.MemberCount} | Leader: {(_fellowshipTracker.IsLeader ? "ME" : $"0x{unchecked((uint)_fellowshipTracker.LeaderId):X8}")} | Open: {_fellowshipTracker.IsOpen} | Locked: {_fellowshipTracker.IsLocked} | ShareXP: {_fellowshipTracker.ShareXP}");

        int index = 0;
        foreach (string memberName in _fellowshipTracker.GetMemberNames())
        {
            int memberId = _fellowshipTracker.GetMemberId(index);
            bool isLeader = memberId == _fellowshipTracker.LeaderId;
            ChatLine($"[RynthAi]   [{index}] {memberName} (0x{unchecked((uint)memberId):X8}){(isLeader ? " [LEADER]" : "")}");
            index++;
        }
    }

    private string FindFellowshipMemberNameById(int memberId)
    {
        if (_fellowshipTracker == null || memberId == 0)
            return "(unknown)";

        int index = 0;
        foreach (string memberName in _fellowshipTracker.GetMemberNames())
        {
            if (_fellowshipTracker.GetMemberId(index) == memberId)
                return memberName;

            index++;
        }

        return "(unknown)";
    }

    private bool TryResolveCorpseForDiagnostics(string selector, out WorldObject? corpse, out string source)
    {
        corpse = null;
        source = string.Empty;
        if (_objectCache == null || _playerId == 0)
            return false;

        if ((string.IsNullOrEmpty(selector) || selector == "target") &&
            _currentTargetId != 0 &&
            IsCorpseLikeObject(unchecked((int)_currentTargetId)))
        {
            corpse = _objectCache[unchecked((int)_currentTargetId)];
            if (corpse != null)
            {
                source = "target";
                return true;
            }
        }

        if ((string.IsNullOrEmpty(selector) || selector == "open") &&
            _openedContainerId != 0 &&
            IsCorpseLikeObject(_openedContainerId))
        {
            corpse = _objectCache[_openedContainerId];
            if (corpse != null)
            {
                source = "open";
                return true;
            }
        }

        if (selector == "target" || selector == "open")
            return false;

        double maxMeters = GetCorpseApproachRangeMaxMeters(_dashboard?.Settings ?? new LegacyUi.LegacyUiSettings());
        if (TryFindNearestCorpseForDiagnostics(maxMeters, out corpse, out _))
        {
            source = "nearest";
            return true;
        }

        return false;
    }

    private bool TryFindNearestCorpseForDiagnostics(double maxMeters, out WorldObject? corpse, out double distanceMeters)
    {
        corpse = null;
        distanceMeters = double.MaxValue;
        if (_objectCache == null || _playerId == 0)
            return false;

        int playerId = unchecked((int)_playerId);
        foreach (WorldObject candidate in _objectCache.GetLandscapeObjects())
        {
            if (candidate.ObjectClass != AcObjectClass.Corpse)
                continue;

            double dist = _objectCache.Distance(playerId, candidate.Id);
            if (dist > maxMeters || dist >= distanceMeters)
                continue;

            corpse = candidate;
            distanceMeters = dist;
        }

        return corpse != null;
    }

    private void HandleCorpseOpenCommand()
    {
        var settings = _dashboard?.Settings;
        if (settings == null || _objectCache == null || _playerId == 0)
        {
            ReplyFail("[RynthAi] Corpse opening is not ready yet.");
            return;
        }

        if (_openedContainerId != 0)
        {
            ReplyFail($"[RynthAi] A container is already open: 0x{(uint)_openedContainerId:X8}");
            return;
        }

        double maxMeters = GetCorpseApproachRangeMaxMeters(settings);
        if (!TryFindNearestCorpse(maxMeters, out WorldObject? corpse, out double distanceMeters))
        {
            ReplyFail("[RynthAi] No unopened corpse is within corpse max range.");
            return;
        }

        _targetCorpseId = corpse!.Id;
        _corpseTargetSince = CorpseNowMs;
        _lastCorpseOpenAttemptAt = 0;
        ReplyOk($"[RynthAi] Corpse target set to {corpse.Name} at {ConvertMetersToYards(distanceMeters):F1} yd.");
        TickCorpseOpening();
    }
}

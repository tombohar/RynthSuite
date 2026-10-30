using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// Drives the salvage state machine for loot-rule items marked Salvage.
///
/// Items come from looting: <see cref="NoteLootedForSalvage"/> when the pickup is sent and
/// <see cref="EnqueueItem"/> when it is confirmed. Every such item is TRACKED until it has
/// been salvaged or has left the pack, and a sweep every couple of seconds queues tracked
/// items that are in the pack. Before 2026-10-02 the queue was the only record: a pickup
/// whose confirm was lost (busy reset, corpse closed), an item salvaged before it reached
/// the pack, a missing UST, or three failed tries dropped the item for good, and it sat in
/// the pack.
///
/// Flow per batch (up to <see cref="MaxBatchItems"/> items in one panel cycle):
///   Idle → UseObject(UST), wait OpenDelay
///        → SalvagePanelAddItem each item (AddDelayFast apart), plus matching under-full bags
///        → SalvagePanelExecute, wait SalvageDelay
///        → poll until the items leave the pack (or ResultMaxWaitMs) → one summary log line
/// Items that are worn, retained, on the Items list or the UST itself are skipped with the
/// reason in the log; RynthAi never takes off anything the character is wearing.
///
/// First-open delays (400ms/600ms) let the panel animate open and the gmSalvageUI singleton
/// hook fire before we make thiscall instance calls. Fast delays (50ms) apply once the panel
/// has been opened at least once.
///
/// Without the panel (2026-10-12, setting SalvageWithoutPanel, engine API v82 SalvageDirect):
/// the panel's Salvage button only ever sends one game action, 0x027D (UST, count, item ids),
/// and the engine can send it itself. Then a batch (up to <see cref="MaxDirectBatchItems"/>
/// items, plus matching under-full bags up to the engine's 100 ids) is one request:
///   Idle → SalvageDirect(UST, items + bags) → poll until the items leave the pack → summary
/// No panel on screen, no open/add delays. A request that changes nothing (no item left the
/// pack) is tried once through the panel, and the log says why; after
/// <see cref="DirectOffAfterFailures"/> such batches in a row that the panel then salvaged, the
/// rest of the session uses the panel. The combine sweep sends each bag group the same way
/// (ACE merges salvage bags listed in a salvage request), with the same one panel retry.
/// </summary>
public sealed class SalvageManager
{
    private enum Phase { Idle, OpeningPanel, AddingItems, ReadyToExecute, Salvaging, WaitingForResult, CombiningSalvage }

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache? _cache;

    // ── Single-item salvage: queue, tracking, batch ──────────────────────────
    private readonly Queue<uint> _queue = new();
    private readonly HashSet<uint> _queued = new();
    private Phase _phase = Phase.Idle;
    private long _phaseReadyAt;
    private uint _currentUstId;
    private bool _panelEverOpened;
    private bool _firstResultCycle = true;
    private bool _pendingCombineScan;

    /// <summary>Most items added to the panel for one Salvage press.</summary>
    internal const int MaxBatchItems = 20;
    /// <summary>Most items in one salvage request without the panel. ACE makes new bags per request
    /// (it never tops up a bag already in the pack), so bigger batches leave fewer partial bags; the
    /// matching under-full bags ride along up to the engine's 100 ids.</summary>
    internal const int MaxDirectBatchItems = 50;
    /// <summary>A request without the panel is first checked this long after it was sent...</summary>
    internal const long DirectSettleMs = 150;
    /// <summary>...and polled until its items leave the pack, up to this long.</summary>
    internal const long DirectResultMaxWaitMs = 3_000;
    /// <summary>After this many batches in a row where the request without the panel changed nothing
    /// and the panel then salvaged, the session goes back to the panel.</summary>
    internal const int DirectOffAfterFailures = 3;
    /// <summary>A batch starts this long after the last item was queued (or when a full batch is
    /// waiting), so one corpse's salvage goes in one panel cycle instead of one cycle per item.</summary>
    internal const long GatherMs = 1_000;
    private const long SweepIntervalMs = 2_000;
    /// <summary>A tracked item that never shows up in the pack is forgotten after this.</summary>
    internal const long ArrivalGraceMs = 60_000;
    /// <summary>After <see cref="MaxItemRetries"/> failed tries in a row an item rests this long...</summary>
    internal const long GiveUpCooldownMs = 60_000;
    /// <summary>...and after this many rests it is left in the pack for good (logged).</summary>
    internal const int MaxGiveUpRounds = 3;
    private const long NoUstRetryMs = 10_000;

    private sealed class TrackedItem
    {
        public string Name = string.Empty;
        public long NotedAt;
        public bool SeenInPack;
        public long CooldownUntil;
        public int GiveUps;
    }

    private readonly Dictionary<uint, TrackedItem> _tracked = new();
    private long _lastEnqueueAt;
    private long _lastSweepAt;
    private long _noUstWarnedAt = long.MinValue / 2;

    // The batch in flight: what we meant to add, what the panel took, and per-batch notes.
    private readonly List<uint> _batch = new();
    private readonly List<uint> _batchAdded = new();
    private readonly Dictionary<uint, string> _batchNames = new();
    private int _batchAddIdx;
    private int _batchBagsAdded;
    private int _batchNumber;
    private int _batchAddFailures;
    private readonly SkipNotes _batchSkips = new();

    // Salvage without the panel: how the batch in flight went out, the bags sent along, the
    // salvage bags in the pack before it, and the server's salvage chat seen meanwhile.
    private bool _batchDirect;
    private bool _batchPanelFallback;
    private string _batchFallbackWhy = string.Empty;
    private readonly List<uint> _batchBags = new();
    private HashSet<uint>? _batchBagsBefore;
    private readonly List<string> _salvageChat = new();
    private int _directPanelWorkedStreak;
    private bool _directOff;
    private string _directOffReason = string.Empty;
    private int _directRequests;
    private int _directFallbacks;

    // Session totals for /ra salvstate.
    private int _itemsSalvagedThisSession;
    private int _itemsSkippedThisSession;
    private int _batchesThisSession;

    // Combine-salvage state — process one material group per cycle by opening
    // the salvage panel, adding all under-full bags of that material, and hitting
    // Salvage so the server merges them server-side. Far more reliable than the
    // old MoveItemExternal-into-bag approach which AC silently rejects when the
    // bags have different workmanship.
    private enum CombinePhase { None, OpeningPanel, AddingBags, Salvaging, WaitingForResult }
    private List<List<uint>>? _combineGroups;
    private int _combineGroupIdx;
    private int _combineAddIdx;
    private CombinePhase _combinePhase = CombinePhase.None;
    private long _combinePhaseReadyAt;

    // Verification — snapshot of the bag ids submitted for the current group's
    // Execute. After the result delay, we count how many of these are still in
    // the cache to decide whether the server actually merged them.
    private List<uint>? _combineGroupSnapshot;
    // The current group went out without the panel / is the panel retry of one that did.
    private bool _combineGroupDirect;
    private bool _combineGroupPanelRetry;

    // Polling deadline for verification. The salvage delete event from the
    // server can arrive 500ms-1s after Execute on busy servers; we keep
    // checking inventory until either the items are gone OR this deadline
    // passes. Without this, fast result delays (~300ms) produce false-negative
    // re-queues and prevent _pendingCombineScan from ever firing.
    private long _waitingResultDeadline;
    private const long ResultMaxWaitMs = 2000;

    // Session-level combine metrics. Logged per-group so the user can see
    // running totals without needing a separate diagnostic surface.
    private int _combineGroupsSucceeded;
    private int _combineGroupsFailed;
    private int _bagsMergedThisSession;

    // Per-item retry counter so a failed salvage gets re-queued a few times
    // before it rests. AC sometimes drops a Salvage execute when the bot is
    // busy with other actions; the item stays in the pack and we want to try
    // again when the queue gets back to idle.
    private readonly Dictionary<uint, int> _itemRetryCount = new();
    private const int MaxItemRetries = 3;

    // Backoff after a refused panel step. Without it a failed UseObject/AddItem
    // re-queues the items and TickIdle dequeues them again on the very next tick,
    // so one busy hiccup burns all three attempts inside ~50ms.
    private long _idleRetryReadyAt;
    private const long RetryBackoffMs = 1000;

    // Same for the combine sweep: a refused UseObject used to skip the whole
    // material group immediately, and with no timer set the next tick tried the
    // next group — so one hiccup could tear through every group in the sweep.
    // Retry the group a few times on the same backoff before giving up on it.
    private int _combineOpenAttempts;
    private const int MaxCombineOpenAttempts = 3;

    // Last thing that went wrong, for GetStateSnapshot. The Log() trail has this
    // but the log is where you look *after* you know salvage is the problem;
    // this is so a state dump says so on its own.
    private string _lastError = string.Empty;
    private long _lastErrorAt;

    // Periodic combine sweep — even when no items have been salvaged this
    // session (or every salvage gave up after retries), we still want to
    // periodically merge any under-full bags sitting in inventory.
    private long _lastCombineSweepAt;
    private const long CombineSweepIntervalMs = 30_000;

    /// <summary>Milliseconds clock. Tests replace it to step time; the game uses wall time.</summary>
    internal Func<long> Clock { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long NowMs => Clock();

    /// <summary>
    /// Set by the plugin to point at the currently-loaded loot profile's
    /// SalvageCombine block (if any). When non-null and Enabled, bags are
    /// grouped by (MaterialType, WorkmanshipBand) per the profile's rules
    /// instead of just (MaterialType).
    /// </summary>
    public Func<RynthCore.Loot.SalvageCombineSettings?>? CombineConfigProvider { get; set; }

    public SalvageManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache? cache)
    {
        _host = host;
        _settings = settings;
        _cache = cache;
    }

    private const long HasUstCacheMs = 3000;
    private long _hasUstCheckedAt = long.MinValue / 2;
    private bool _hasUst;

    /// <summary>
    /// A UST is carried (cached 3 s; FindUst can force an inventory walk). Without one there is
    /// nothing to salvage with: looting leaves items that only a Salvage rule wants, and the
    /// combine sweep doesn't run (2026-10-10).
    /// </summary>
    public bool HasUst()
    {
        long now = NowMs;
        if (now - _hasUstCheckedAt >= HasUstCacheMs)
        {
            _hasUstCheckedAt = now;
            _hasUst = FindUst() != 0;
        }
        return _hasUst;
    }

    /// <summary>Returns true while a salvage operation is in flight.</summary>
    public bool IsBusy => _phase != Phase.Idle || _queue.Count > 0;

    private bool PanelAvailable => _host.HasSalvagePanel && _host.HasUseObject;

    /// <summary>Salvage requests go out without the panel: the setting is on, the engine has
    /// SalvageDirect (API v82) and the session hasn't gone back to the panel.</summary>
    internal bool UseDirect => _settings.SalvageWithoutPanel && _host.HasSalvageDirect && !_directOff;

    /// <summary>
    /// Chat from the game (RynthAiPlugin.OnChatWindowText). While a batch or a combine group is in
    /// flight, the server's salvage lines are kept for the summary: "You obtain ... using your
    /// knowledge of ...", and the refusals ("You do not own that salvage tool!", "That is not a
    /// salvaging tool.", "...not suitable for salvaging.", "The list of items you are attempting to
    /// salvage is invalid.", "You cannot salvage items that you are trading!").
    /// </summary>
    public void OnChatWindowText(string? text)
    {
        // A batch is being sent (_batch filled; the reply can beat the phase change) or is in flight.
        if (string.IsNullOrEmpty(text) || (_phase == Phase.Idle && _batch.Count == 0) || _salvageChat.Count >= 8) return;
        if (text.Contains("using your knowledge of", StringComparison.OrdinalIgnoreCase)
            || text.Contains("salvage tool", StringComparison.OrdinalIgnoreCase)
            || text.Contains("salvaging tool", StringComparison.OrdinalIgnoreCase)
            || text.Contains("suitable for salvaging", StringComparison.OrdinalIgnoreCase)
            || text.Contains("attempting to salvage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cannot salvage", StringComparison.OrdinalIgnoreCase))
            _salvageChat.Add(text.Trim());
    }

    /// <summary>The kept salvage chat as a log fragment ("; server said: ..."), or "".</summary>
    private string ChatNote()
    {
        if (_salvageChat.Count == 0) return string.Empty;
        string first = _salvageChat[0].Length > 160 ? _salvageChat[0][..160] + "..." : _salvageChat[0];
        return $"; server said: \"{first}\"" + (_salvageChat.Count > 1 ? $" (+{_salvageChat.Count - 1} more)" : "");
    }

    /// <summary>True when a refusal (not a "You obtain" line) was seen.</summary>
    private bool ChatSaidRefused() => _salvageChat.Any(l => !l.Contains("using your knowledge of", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True from the UST use until the salvage execute has gone out (a batch or a bag combine).
    /// Meanwhile the client swallows a door or corpse use: no Use packet, no busy count, and the
    /// open waited out its 5 s limit (in one log, 6 of 6 corpse opens sent 0-155 ms after the UST
    /// use were lost; 4 of 4 sent after the execute were answered). Narrower than IsBusy, which
    /// also covers a queued batch whose panel isn't open yet.
    /// </summary>
    public bool PanelPending =>
        _phase is Phase.OpeningPanel or Phase.AddingItems or Phase.ReadyToExecute
        || (_phase == Phase.CombiningSalvage
            && _combinePhase is CombinePhase.OpeningPanel or CombinePhase.AddingBags or CombinePhase.Salvaging);

    /// <summary>
    /// Looting sent the pickup of an item whose rule says Salvage. Tracks it without queueing:
    /// the sweep queues it once it is in the pack, even if the pickup confirm never comes.
    /// </summary>
    public void NoteLootedForSalvage(uint itemId, string? name = null)
    {
        if (itemId == 0) return;
        if (IsSalvageBag(name ?? _cache?[unchecked((int)itemId)]?.Name)) return;
        Track(itemId, name);
    }

    /// <summary>Enqueue an item to be salvaged. Called by CorpseOpenController after pickup.</summary>
    public void EnqueueItem(uint itemId)
    {
        if (itemId == 0)
            return;

        // A salvage bag is ALREADY salvage. Feeding one into the single-item
        // flow opens the panel on a lone bag, Executes a no-op, fails the
        // "still present" check, and retry-storms — this is the user-reported
        // "keeps salvaging bags by themselves" behaviour. Bags are consolidated
        // by the combine sweep (grouped, never solo); they must never enter the
        // single-item queue.
        string? name = _cache?[unchecked((int)itemId)]?.Name;
        if (IsSalvageBag(name))
        {
            Log($"[Salvage] Ignoring enqueue of salvage bag 0x{itemId:X8} ({name}) — bags are combined by the sweep, not salvaged solo.");
            if (_settings.EnableCombineSalvage) _pendingCombineScan = true;
            return;
        }

        Track(itemId, name);
        QueueItem(itemId);
    }

    private void Track(uint itemId, string? name)
    {
        long now = NowMs;
        if (!_tracked.TryGetValue(itemId, out TrackedItem? t))
        {
            t = new TrackedItem { NotedAt = now };
            _tracked[itemId] = t;
        }
        if (!string.IsNullOrWhiteSpace(name)) t.Name = name!;
    }

    private void Untrack(uint itemId)
    {
        _tracked.Remove(itemId);
        _itemRetryCount.Remove(itemId);
    }

    private void QueueItem(uint itemId)
    {
        if (!_queued.Add(itemId)) return;
        _queue.Enqueue(itemId);
        _lastEnqueueAt = NowMs;
        _pendingCombineScan = false; // reset; will be set again when queue drains
    }

    private uint DequeueItem()
    {
        uint id = _queue.Dequeue();
        _queued.Remove(id);
        return id;
    }

    /// <summary>Called every game tick from RynthAiPlugin.OnTick.</summary>
    public void OnTick(int busyCount)
    {
        // Nothing to salvage with (no panel calls, no SalvageDirect). A batch already in flight
        // still finishes: its steps fail cleanly and the items go back in the queue.
        if (_phase == Phase.Idle && !PanelAvailable && !UseDirect)
            return;

        long now = NowMs;

        switch (_phase)
        {
            case Phase.Idle:
                TickIdle(now, busyCount);
                break;

            case Phase.OpeningPanel:
                if (now >= _phaseReadyAt)
                {
                    _batchAddIdx = 0;
                    _phase = Phase.AddingItems;
                    _phaseReadyAt = now;
                    TickAddingItems(now);
                }
                break;

            case Phase.AddingItems:
                if (now >= _phaseReadyAt)
                    TickAddingItems(now);
                break;

            case Phase.ReadyToExecute:
                if (now >= _phaseReadyAt)
                    BeginSalvaging(now);
                break;

            case Phase.Salvaging:
                if (now >= _phaseReadyAt)
                    BeginWaitingForResult(now);
                break;

            case Phase.WaitingForResult:
                if (now < _phaseReadyAt) break;
                // Poll: while any added item is still in the pack and we're under the
                // deadline, wait. Server delete events for salvage-consumed items can
                // lag the result delay by 500ms+.
                if (now < _waitingResultDeadline && AnyStillInPack(_batchAdded))
                    break;
                OnBatchResult(now);
                break;

            case Phase.CombiningSalvage:
                TickCombiningSalvage(now);
                break;
        }
    }

    // ── Phase handlers ────────────────────────────────────────────────────────

    private void TickIdle(long now, int busyCount)
    {
        SweepTracked(now);

        if (_queue.Count == 0)
        {
            if (_pendingCombineScan && _settings.EnableCombineSalvage)
            {
                _pendingCombineScan = false;
                _lastCombineSweepAt = now;
                if (HasUst()) BeginCombineSalvage(now);
                return;
            }
            // Periodic standalone sweep: if 30s have passed since the last
            // combine attempt and there are still under-full bag groups in the
            // pack, run another pass. Catches bags accumulated from sources
            // outside the salvage queue (or sessions where every salvage gave
            // up after retries).
            if (_settings.EnableCombineSalvage
                && busyCount == 0
                && now - _lastCombineSweepAt >= CombineSweepIntervalMs)
            {
                _lastCombineSweepAt = now;
                if (HasUst()) BeginCombineSalvage(now);
            }
            return;
        }

        // Don't start while the game is busy processing a prior action.
        if (busyCount > 0)
            return;

        // Serving out the backoff from a refused panel step.
        if (now < _idleRetryReadyAt)
            return;

        // Let a corpse's worth of salvage gather so it goes in one panel cycle (or one request).
        bool direct = UseDirect;
        int maxBatch = direct ? MaxDirectBatchItems : MaxBatchItems;
        if (_queue.Count < maxBatch && now - _lastEnqueueAt < GatherMs)
            return;

        if (!BuildBatch(now, maxBatch))
            return;

        uint ustId = FindUst();
        _hasUstCheckedAt = now; _hasUst = ustId != 0;
        if (ustId == 0)
        {
            // Keep the items (they used to be dropped here for good) and look again later.
            if (now - _noUstWarnedAt >= 5 * 60_000)
            {
                _noUstWarnedAt = now;
                _host.WriteToChat("[RynthAi] Salvage: no UST in your pack — salvage is waiting. Add a Ust to your pack.", 2);
            }
            NoteError("no UST in inventory — salvage waiting");
            Log($"[Salvage] No UST found — {_batch.Count} item(s) wait; trying again in {NoUstRetryMs / 1000}s.");
            ReturnBatchToQueue();
            _idleRetryReadyAt = now + NoUstRetryMs;
            return;
        }

        _currentUstId = ustId;

        if (direct)
        {
            SendDirectBatch(now);
            return;
        }
        StartPanelCycle(now);
    }

    /// <summary>
    /// Sends the batch (plus matching under-full bags when CombineBagsDuringSalvage is on) as one
    /// salvage request without the panel. Not sent at all (the engine refused, or its sender isn't
    /// bound) → the panel takes this batch, and an engine that can't send turns the setting's
    /// effect off for the session.
    /// </summary>
    private void SendDirectBatch(long now)
    {
        _batchBags.Clear();
        if (_settings.CombineBagsDuringSalvage)
            _batchBags.AddRange(FindMatchingUnderFullBags(_batch, RynthCoreHost.MaxSalvageDirectItems - _batch.Count));
        _batchBagsBefore = SnapshotSalvageBagIds();
        _salvageChat.Clear();

        var ids = new uint[_batch.Count + _batchBags.Count];
        _batch.CopyTo(ids, 0);
        _batchBags.CopyTo(ids, _batch.Count);
        int sent = _host.SalvageDirect(_currentUstId, ids);
        if (sent <= 0)
        {
            string why = sent switch
            {
                -1 => "the engine's salvage sender isn't bound on this client",
                -2 => "the engine is older than plugin API v82",
                _ => "the engine refused the request",
            };
            if (sent is -1 or -2) TurnDirectOff(why);
            NoteError($"salvage without the panel not sent ({why})");
            _directFallbacks++;
            _batchBags.Clear();
            _batchPanelFallback = true;
            _batchFallbackWhy = "the request without the panel wasn't sent: " + why;
            Log($"[Salvage] Batch {_batchNumber + 1}: salvage without the panel wasn't sent ({why}) — using the panel for these {_batch.Count} item(s).");
            StartPanelCycle(now);
            return;
        }

        _directRequests++;
        _batchDirect = true;
        _batchAdded.AddRange(_batch);
        _batchBagsAdded = _batchBags.Count;
        _phaseReadyAt = now + DirectSettleMs;
        _waitingResultDeadline = now + DirectResultMaxWaitMs;
        _phase = Phase.WaitingForResult;
    }

    private void TurnDirectOff(string why)
    {
        if (_directOff) return;
        _directOff = true;
        _directOffReason = why;
        Log($"[Salvage] Salvage without the panel is off for this session: {why}. The salvage panel is used instead.");
    }

    /// <summary>The panel path: use the UST (opens the panel), then add, execute, verify.</summary>
    private void StartPanelCycle(long now)
    {
        if (!PanelAvailable)
        {
            Log($"[Salvage] No salvage panel on this client — {_batch.Count} item(s) back in the queue.");
            NoteError("no salvage panel calls on this client");
            foreach (uint id in _batch) RequeueOrDrop(id, "no salvage panel");
            ClearBatch();
            _currentUstId = 0;
            _phase = Phase.Idle;
            _idleRetryReadyAt = now + RetryBackoffMs;
            return;
        }
        _salvageChat.Clear();

        // UseObject on the UST mimics a double-click — this is the reliable path to
        // trigger gmSalvageUI::OpenSalvagePanel (our hook captures the instance there).
        // SendNotice_OpenSalvagePanel does not reliably call the hooked function.
        if (!_host.UseFor(_currentUstId, "Salvage", "open the salvage panel (UST)"))
        {
            Log($"[Salvage] UseObject(UST) failed for 0x{_currentUstId:X8} — {_batch.Count} item(s) back in the queue.");
            NoteError($"UseObject(UST) failed for 0x{_currentUstId:X8}");
            _currentUstId = 0;
            foreach (uint id in _batch) RequeueOrDrop(id, "UseObject(UST) failed");
            ClearBatch();
            _phase = Phase.Idle;
            _idleRetryReadyAt = now + RetryBackoffMs;
            return;
        }

        int openDelay = _panelEverOpened ? _settings.SalvageOpenDelayFastMs : _settings.SalvageOpenDelayFirstMs;
        _phaseReadyAt = now + openDelay;
        _phase = Phase.OpeningPanel;
    }

    /// <summary>
    /// Takes up to <see cref="MaxBatchItems"/> items off the queue that can be salvaged now.
    /// Items not in the pack yet stay tracked (the sweep queues them on arrival); items that
    /// can't be salvaged are skipped with a reason. False when nothing is left to salvage.
    /// </summary>
    private bool BuildBatch(long now, int maxBatch)
    {
        ClearBatch();
        if (_cache == null)
        {
            while (_queue.Count > 0) DequeueItem();
            return false;
        }

        var inPack = new Dictionary<uint, WorldObject>();
        foreach (WorldObject wo in _cache.GetDirectInventory(forceRefresh: true))
            inPack[unchecked((uint)wo.Id)] = wo;

        int waiting = 0;
        while (_queue.Count > 0 && _batch.Count < maxBatch)
        {
            uint id = DequeueItem();
            _tracked.TryGetValue(id, out TrackedItem? t);

            if (!inPack.TryGetValue(id, out WorldObject? wo))
            {
                string name = t?.Name ?? _cache[unchecked((int)id)]?.Name ?? string.Empty;
                if (IsWornByPlayer(_cache[unchecked((int)id)]))
                {
                    SkipForGood(id, name, SkipWorn);
                    continue;
                }
                if (t != null && !t.SeenInPack && now - t.NotedAt < ArrivalGraceMs)
                {
                    waiting++; // still on its way; the sweep queues it when it lands
                    continue;
                }
                // Already gone (salvaged, sold, dropped) or never arrived.
                Untrack(id);
                continue;
            }

            if (t != null) t.SeenInPack = true;
            string itemName = wo.Name;
            string? reason = SkipReasonFor(id, wo);
            if (reason != null)
            {
                SkipForGood(id, itemName, reason);
                continue;
            }

            _batch.Add(id);
            _batchNames[id] = itemName;
        }

        if (_batch.Count == 0)
        {
            // Nothing to send: say why once (this runs only when items were queued, and
            // skipped/waiting items leave the queue, so it can't repeat every tick).
            if (_batchSkips.Count > 0 || waiting > 0)
                Log($"[Salvage] Nothing to salvage this round: {_batchSkips.Describe()}"
                    + (waiting > 0 ? $"{(_batchSkips.Count > 0 ? "; " : "")}{waiting} not in the pack yet (will salvage when they arrive)" : "")
                    + ".");
            _batchSkips.Clear();
            return false;
        }

        if (waiting > 0)
            _batchSkips.Add("not in the pack yet (will salvage when they arrive)", $"{waiting} item(s)", countOnly: true, n: waiting);
        return true;
    }

    // Skip reasons (also the log wording).
    internal const string SkipWorn = "worn (RynthAi never takes off what the character is wearing)";
    internal const string SkipRetained = "retained";
    internal const string SkipItemsList = "on the Items list";
    internal const string SkipUst = "it is the UST";
    internal const string SkipBag = "already a salvage bag (the combine sweep handles bags)";
    internal const string SkipGaveUp = "salvage failed too often";

    /// <summary>Why <paramref name="wo"/> (in the pack) must not be salvaged, or null.</summary>
    private string? SkipReasonFor(uint id, WorldObject wo)
    {
        if (IsSalvageBag(wo.Name)) return SkipBag;
        if (IsWornByPlayer(wo)) return SkipWorn;
        if (wo.ObjectClass == AcObjectClass.Ust || IsUst(wo.Name)
            || (_host.HasGetObjectWcid && _host.TryGetObjectWcid(id, out uint wcid) && wcid == UstWcid))
            return SkipUst;
        int sid = unchecked((int)id);
        if (_settings.ItemRules.Any(r => r.Id == sid)) return SkipItemsList;
        // ACE skips a Retained item without a word (Player_Crafting.HandleSalvaging).
        if (_host.TryGetObjectBoolProperty(id, StypeBoolRetained, out bool retained) && retained) return SkipRetained;
        return null;
    }

    private const uint StypeBoolRetained = 91;

    private bool IsWornByPlayer(WorldObject? wo)
    {
        if (wo == null) return false;
        if (wo.WieldedLocation > 0) return true;
        uint pid = _host.GetPlayerId();
        if (pid != 0 && wo.Wielder == unchecked((int)pid)) return true;
        return WorldObjectCache.IsWieldedByPlayer(_host, wo);
    }

    private void SkipForGood(uint id, string name, string reason)
    {
        _batchSkips.Add(reason, string.IsNullOrWhiteSpace(name) ? $"0x{id:X8}" : name);
        _itemsSkippedThisSession++;
        Untrack(id);
        if (reason == SkipWorn)
            _host.WriteToChat($"[RynthAi] Salvage: left {(string.IsNullOrWhiteSpace(name) ? "an item" : $"'{name}'")} alone — the character is wearing it. Take it off by hand if it should be salvaged.", 2);
    }

    private void ReturnBatchToQueue()
    {
        foreach (uint id in _batch) QueueItem(id);
        ClearBatch();
    }

    private void ClearBatch()
    {
        _batch.Clear();
        _batchAdded.Clear();
        _batchNames.Clear();
        _batchSkips.Clear();
        _batchAddIdx = 0;
        _batchBagsAdded = 0;
        _batchAddFailures = 0;
        _batchDirect = false;
        _batchPanelFallback = false;
        _batchFallbackWhy = string.Empty;
        _batchBags.Clear();
        _batchBagsBefore = null;
    }

    /// <summary>Adds the batch to the open panel one item per step, then matching bags.</summary>
    private void TickAddingItems(long now)
    {
        bool wasFirstOpen = !_panelEverOpened;
        if (_batchAddIdx < _batch.Count)
        {
            uint id = _batch[_batchAddIdx];
            if (_host.SalvagePanelAddItem(id))
            {
                _batchAdded.Add(id);
            }
            else if (_batchAdded.Count == 0 && _batchAddIdx == 0)
            {
                // The first add failing means the panel instance isn't ready: put the
                // whole batch back and retry on the longer first-open delays.
                Log($"[Salvage] SalvagePanelAddItem failed (panel instance not ready) — {_batch.Count} item(s) back in the queue.");
                NoteError("SalvagePanelAddItem failed (panel not ready)");
                foreach (uint b in _batch) RequeueOrDrop(b, "SalvagePanelAddItem failed");
                ClearBatch();
                _currentUstId = 0;
                _panelEverOpened = false;
                _phase = Phase.Idle;
                _idleRetryReadyAt = now + RetryBackoffMs;
                return;
            }
            else
            {
                _batchAddFailures++;
                RequeueOrDrop(id, "SalvagePanelAddItem failed");
            }
            _batchAddIdx++;
            _phaseReadyAt = now + _settings.SalvageAddDelayFastMs;
            return;
        }

        if (_batchAdded.Count == 0)
        {
            _phase = Phase.Idle;
            ClearBatch();
            return;
        }

        // Combine-during-salvage: add ALL under-full bags of the batch's materials
        // (and workmanship bands, if configured) so the server merges everything in
        // one operation.
        if (_settings.CombineBagsDuringSalvage)
            _batchBagsAdded = AddAllMatchingUnderFullBags(_batchAdded);

        _panelEverOpened = true;
        int settle = (wasFirstOpen ? _settings.SalvageAddDelayFirstMs : _settings.SalvageAddDelayFastMs)
                     + _batchBagsAdded * _settings.SalvageAddDelayFastMs;
        _phaseReadyAt = now + settle;
        _phase = Phase.ReadyToExecute;
    }

    // STypes (canonical values from Chorizite STypes.cs — verified against
    // the live binary; the PropertyNames.IntNames index in this codebase is
    // off-by-some-rows so do NOT use it as the source of truth).
    private const uint StypeMaxStructure       = 91;
    private const uint StypeStructure          = 92;
    private const uint StypeItemWorkmanship    = 105;
    private const uint StypeMaterialType       = 131;
    // On a salvage BAG, ItemWorkmanship(#105) is a cumulative SUM, not a 1-10
    // value (ACE Player_Crafting.TryAddSalvage). The per-bag average workmanship
    // used for band grouping is #105 / NumItemsInMaterial(#170).
    private const uint StypeNumItemsInMaterial = 170;

    /// <summary>
    /// Scans inventory for every under-full salvage bag whose material (and
    /// workmanship band, if configured) matches one of <paramref name="itemIds"/>,
    /// adds each one to the open salvage panel, and returns how many were added.
    /// Adding them all at once lets the server merge everything in one Salvage
    /// operation instead of leaving partial bags for the periodic sweep.
    /// </summary>
    private int AddAllMatchingUnderFullBags(IReadOnlyCollection<uint> itemIds)
    {
        int added = 0;
        foreach (uint bagId in FindMatchingUnderFullBags(itemIds, int.MaxValue))
            if (_host.SalvagePanelAddItem(bagId))
                added++;
        return added;
    }

    /// <summary>
    /// Every under-full salvage bag in the pack whose material (and workmanship band, if
    /// configured) matches one of <paramref name="itemIds"/>, at most <paramref name="max"/>.
    /// </summary>
    private List<uint> FindMatchingUnderFullBags(IReadOnlyCollection<uint> itemIds, int max)
    {
        var found = new List<uint>();
        if (_cache == null || itemIds.Count == 0 || max <= 0) return found;

        RynthCore.Loot.SalvageCombineSettings? cfg = CombineConfigProvider?.Invoke();
        bool useBands = cfg != null && cfg.Enabled;

        // A freshly-looted ITEM carries a true 1-10 ItemWorkmanship(#105) —
        // unlike a bag, whose #105 is a cumulative sum. So the item's band is
        // read directly here; bag keys come from TryGetBagCombineKey (average).
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (uint itemId in itemIds)
        {
            if (!_host.TryGetObjectIntProperty(itemId, StypeMaterialType, out int itemMat) || itemMat == 0)
                continue;
            if (useBands)
            {
                if (!_host.TryGetObjectIntProperty(itemId, StypeItemWorkmanship, out int itemWm) || itemWm <= 0)
                    continue;
                string? itemBand = cfg!.GetBandKey(itemMat, itemWm);
                if (itemBand == null) continue;
                keys.Add($"{itemMat}|{itemBand}");
            }
            else
            {
                keys.Add(itemMat.ToString());
            }
        }
        if (keys.Count == 0) return found;

        var batch = new HashSet<uint>(itemIds);
        foreach (WorldObject bag in _cache.GetDirectInventory(forceRefresh: true))
        {
            if (found.Count >= max) break;
            uint bagId = unchecked((uint)bag.Id);
            if (batch.Contains(bagId)) continue;
            if (!IsSalvageBag(bag.Name)) continue;
            if (!IsBagUnderFull(bagId, bag.Name)) continue;
            if (!TryGetBagCombineKey(bagId, bag.Name, cfg, useBands, out string bagKey, out _)) continue;
            if (!keys.Contains(bagKey)) continue;
            found.Add(bagId);
        }
        return found;
    }

    /// <summary>The salvage bags in the pack now (a fresh walk).</summary>
    private HashSet<uint> SnapshotSalvageBagIds()
    {
        var set = new HashSet<uint>();
        if (_cache == null) return set;
        foreach (WorldObject wo in _cache.GetDirectInventory(forceRefresh: true))
            if (IsSalvageBag(wo.Name)) set.Add(unchecked((uint)wo.Id));
        return set;
    }

    private void BeginSalvaging(long now)
    {
        if (!_host.SalvagePanelExecute())
        {
            Log($"[Salvage] SalvagePanelExecute failed — {_batchAdded.Count} item(s) back in the queue.");
            NoteError("SalvagePanelExecute failed");
            foreach (uint id in _batchAdded) RequeueOrDrop(id, "execute failed");
            ClearBatch();
            _phase = Phase.Idle;
            _idleRetryReadyAt = now + RetryBackoffMs;
            return;
        }

        _phaseReadyAt = now + _settings.SalvageSalvageDelayMs;
        _phase = Phase.Salvaging;
    }

    private void BeginWaitingForResult(long now)
    {
        int resultDelay = _firstResultCycle
            ? _settings.SalvageResultDelayFirstMs
            : _settings.SalvageResultDelayFastMs;
        _firstResultCycle = false;
        _phaseReadyAt = now + resultDelay;
        _waitingResultDeadline = now + resultDelay + ResultMaxWaitMs;
        _phase = Phase.WaitingForResult;
    }

    private void OnBatchResult(long now)
    {
        // An item still in the player's actual inventory after the result wait wasn't
        // consumed (panel closed mid-execute, bot got busy, AC dropped the request, ...):
        // it goes back in the queue. GetDirectInventory(forceRefresh: true) — a live walk
        // of the player's containers — is the ground truth; salvage-consumed items don't
        // reliably trigger OnDeleteObject in the cache.
        var live = SnapshotDirectInventoryIds();

        // A request without the panel that changed nothing (no item left the pack) is tried once
        // through the panel, before any retry is counted against the items.
        if (_batchDirect && PanelAvailable && !_batchAdded.Any(id => !live.Contains(id)))
        {
            string reply = _salvageChat.Count > 0 ? ChatNote() : "; no salvage reply from the server";
            _directFallbacks++;
            NoteError("salvage without the panel changed nothing — panel retry");
            Log($"[Salvage] Batch {_batchNumber + 1}: salvage without the panel changed nothing ({_batchAdded.Count} item(s) still in the pack{reply}) — trying them once through the panel.");
            _batchFallbackWhy = "the request without the panel changed nothing";
            _batchAdded.Clear();
            _batchBags.Clear();
            _batchBagsAdded = 0;
            _batchBagsBefore = null;
            _batchDirect = false;
            _batchPanelFallback = true;
            StartPanelCycle(now);
            return;
        }

        var salvagedNames = new List<string>();
        int stillThere = 0, requeued = 0;
        foreach (uint id in _batchAdded)
        {
            string name = _batchNames.TryGetValue(id, out string? n) ? n : $"0x{id:X8}";
            if (live.Contains(id))
            {
                stillThere++;
                if (RequeueOrDrop(id, "still in the pack after Salvage")) requeued++;
            }
            else
            {
                salvagedNames.Add(name);
                Untrack(id);
            }
        }

        _batchNumber++;
        _batchesThisSession++;
        _itemsSalvagedThisSession += salvagedNames.Count;

        // Bags: what a request without the panel did to them (the panel path keeps its old note).
        string bagNote;
        if (_batchDirect && _batchBagsBefore != null)
        {
            var bagsNow = SnapshotSalvageBagIds();
            int newBags = bagsNow.Count(b => !_batchBagsBefore.Contains(b));
            int mergedIn = _batchBags.Count(b => !bagsNow.Contains(b));
            bagNote = (newBags > 0 ? $", {newBags} new salvage bag(s)" : salvagedNames.Count > 0 ? ", no new salvage bag seen yet" : "")
                      + (_batchBags.Count > 0 ? $", {mergedIn}/{_batchBags.Count} bag(s) merged in" : "");
        }
        else
        {
            bagNote = _batchBagsAdded > 0 ? $", +{_batchBagsAdded} bag(s) combined" : "";
        }

        string how = _batchDirect ? " without the panel"
                   : _batchPanelFallback ? $" through the panel ({_batchFallbackWhy})"
                   : "";

        // The panel salvaged what a request without it couldn't: count it; enough in a row and the
        // session goes back to the panel. A request that worked resets the count.
        if (_batchDirect && salvagedNames.Count > 0)
            _directPanelWorkedStreak = 0;
        else if (_batchPanelFallback && salvagedNames.Count > 0 && UseDirect)
        {
            _directPanelWorkedStreak++;
            if (_directPanelWorkedStreak >= DirectOffAfterFailures)
            {
                TurnDirectOff($"{_directPanelWorkedStreak} batches in a row changed nothing without the panel and were salvaged through it");
                _host.WriteToChat("[RynthAi] Salvage: salvaging without the panel didn't work here; using the salvage panel for the rest of the session.", 2);
            }
        }

        string line = $"[Salvage] Batch {_batchNumber}{how}: salvaged {salvagedNames.Count}/{_batchAdded.Count}"
            + (salvagedNames.Count > 0 ? $" ({SkipNotes.NameList(salvagedNames)})" : "")
            + bagNote
            + (stillThere > 0 ? $"; {stillThere} still in the pack ({requeued} queued again"
                                 + (stillThere > requeued ? $", {stillThere - requeued} resting or given up, see above" : "") + ")" : "")
            + (_batchAddFailures > 0 ? $"; panel refused {_batchAddFailures} (queued again)" : "")
            + (_batchSkips.Count > 0 ? $"; skipped: {_batchSkips.Describe()}" : "")
            + ChatNote()
            + $". Session: {_itemsSalvagedThisSession} salvaged, {_itemsSkippedThisSession} skipped, {_tracked.Count} still tracked.";
        Log(line);
        if (stillThere > 0 && ChatSaidRefused())
            NoteError($"server refused salvage{ChatNote()}");
        _salvageChat.Clear();

        ClearBatch();
        _currentUstId = 0;
        _phase = Phase.Idle;

        // Always trigger a combine scan on the next idle tick when the queue
        // drains — both to consolidate any newly-merged bags and so combine
        // doesn't depend on the busyCount==0 gate of the periodic timer (which
        // an active combat macro rarely satisfies).
        if (_queue.Count == 0)
            _pendingCombineScan = true;
    }

    /// <summary>
    /// Every <see cref="SweepIntervalMs"/>: queue tracked items that are in the pack, forget
    /// ones that left it (or never arrived), and skip ones the character is wearing. One log
    /// line when it queues or skips something; nothing when there's nothing to say.
    /// </summary>
    private void SweepTracked(long now)
    {
        if (_tracked.Count == 0 || _cache == null) return;
        if (now - _lastSweepAt < SweepIntervalMs) return;
        _lastSweepAt = now;

        var inPack = new Dictionary<uint, WorldObject>();
        foreach (WorldObject wo in _cache.GetDirectInventory(forceRefresh: true))
            inPack[unchecked((uint)wo.Id)] = wo;

        int queuedNow = 0, resting = 0;
        List<uint>? forget = null;
        List<(uint Id, string Name)>? worn = null;
        foreach (var kv in _tracked)
        {
            uint id = kv.Key;
            TrackedItem t = kv.Value;
            if (inPack.TryGetValue(id, out WorldObject? wo))
            {
                t.SeenInPack = true;
                if (string.IsNullOrEmpty(t.Name)) t.Name = wo.Name;
                if (_queued.Contains(id) || _batch.Contains(id)) continue; // BuildBatch checks it
                if (IsWornByPlayer(wo)) { (worn ??= new()).Add((id, wo.Name)); continue; }
                if (now < t.CooldownUntil) { resting++; continue; }
                QueueItem(id);
                queuedNow++;
                continue;
            }

            if (_queued.Contains(id) || _batch.Contains(id)) continue;
            WorldObject? cached = _cache[unchecked((int)id)];
            if (IsWornByPlayer(cached)) { (worn ??= new()).Add((id, cached?.Name ?? t.Name)); continue; }
            if (t.SeenInPack || now - t.NotedAt >= ArrivalGraceMs)
                (forget ??= new()).Add(id);
        }

        if (forget != null)
            foreach (uint id in forget) Untrack(id);
        if (worn != null)
        {
            foreach (var (id, name) in worn) SkipForGood(id, name, SkipWorn);
            Log($"[Salvage] Sweep: skipped {_batchSkips.Describe()}.");
            _batchSkips.Clear();
        }
        if (queuedNow > 0)
            Log($"[Salvage] Sweep: queued {queuedNow} looted-for-salvage item(s) found in the pack"
                + (resting > 0 ? $", {resting} resting after failures" : "")
                + $" ({_tracked.Count} tracked).");
    }

    /// <summary>
    /// Re-queue an item that failed a salvage step. After MaxItemRetries failures in a row
    /// the item rests for GiveUpCooldownMs (the sweep queues it again after that); after
    /// MaxGiveUpRounds rests it is left in the pack for good, with a log line saying so.
    /// True when the item went straight back in the queue.
    /// </summary>
    private bool RequeueOrDrop(uint itemId, string reason)
    {
        if (itemId == 0) return false;

        int prior = _itemRetryCount.TryGetValue(itemId, out int n) ? n : 0;
        int next = prior + 1;
        if (next < MaxItemRetries)
        {
            _itemRetryCount[itemId] = next;
            QueueItem(itemId);
            return true;
        }

        _itemRetryCount.Remove(itemId);
        string name = _tracked.TryGetValue(itemId, out TrackedItem? t) && t.Name.Length > 0 ? t.Name : $"0x{itemId:X8}";
        if (t != null && t.GiveUps + 1 < MaxGiveUpRounds)
        {
            t.GiveUps++;
            t.CooldownUntil = NowMs + GiveUpCooldownMs;
            Log($"[Salvage] '{name}' 0x{itemId:X8} failed {MaxItemRetries} times ({reason}) — trying again in {GiveUpCooldownMs / 1000}s (round {t.GiveUps}/{MaxGiveUpRounds}).");
        }
        else
        {
            Untrack(itemId);
            _itemsSkippedThisSession++;
            Log($"[Salvage] Giving up on '{name}' 0x{itemId:X8}: {SkipGaveUp} ({reason}). It stays in the pack.");
            NoteError($"gave up on 0x{itemId:X8} ({reason})");
        }
        // Trigger a combine scan if this left the queue empty.
        if (_queue.Count == 0) _pendingCombineScan = true;
        return false;
    }

    /// <summary>Per-batch skip notes, grouped by reason, for one bounded log line.</summary>
    private sealed class SkipNotes
    {
        private readonly List<(string Reason, List<string> Names, int Extra)> _byReason = new();
        public int Count { get; private set; }

        public void Add(string reason, string name, bool countOnly = false, int n = 1)
        {
            int i = _byReason.FindIndex(e => e.Reason == reason);
            if (i < 0) { _byReason.Add((reason, new List<string>(), 0)); i = _byReason.Count - 1; }
            var e = _byReason[i];
            if (countOnly) _byReason[i] = (e.Reason, e.Names, e.Extra + n);
            else e.Names.Add(name);
            Count += n;
        }

        public void Clear() { _byReason.Clear(); Count = 0; }

        public string Describe()
            => string.Join("; ", _byReason.Select(e =>
                e.Names.Count == 0 ? $"{e.Extra} {e.Reason}" : $"{e.Reason}: {NameList(e.Names)}"));

        /// <summary>"A, B x2, C" — at most 8 distinct names, then "+N more".</summary>
        public static string NameList(List<string> names)
        {
            var groups = names.GroupBy(x => x).Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key).ToList();
            return groups.Count <= 8 ? string.Join(", ", groups) : string.Join(", ", groups.Take(8)) + $", +{groups.Count - 8} more";
        }
    }

    // ── Combine salvage bags ──────────────────────────────────────────────────

    private void BeginCombineSalvage(long now)
    {
        if (_cache == null) return;

        RynthCore.Loot.SalvageCombineSettings? cfg = CombineConfigProvider?.Invoke();
        bool useBands = cfg != null && cfg.Enabled;

        // Group under-full bags by MaterialType (and Workmanship band, if a
        // SalvageCombine config is loaded and enabled). Bags at 100/100 are
        // skipped (already full); singleton groups are skipped (nothing to merge).
        // forceRefresh: true — startup combines work because the engine's replay
        // freshly populates the cache, but during a session the snapshot drifts
        // and forceRefresh:false returns yesterday's inventory. Always walk live.
        var byKey = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        var inv = _cache.GetDirectInventory(forceRefresh: true);
        int scannedItems = inv.Count;
        int salvageBagCount = 0;
        int underFullCount = 0;
        int rejMaterialUnknown = 0;
        int rejWmUnread = 0;
        foreach (WorldObject item in inv)
        {
            if (!IsSalvageBag(item.Name)) continue;
            salvageBagCount++;
            uint id = unchecked((uint)item.Id);
            if (!IsBagUnderFull(id, item.Name)) continue;
            underFullCount++;

            if (!TryGetBagCombineKey(id, item.Name, cfg, useBands, out string key, out string diag))
            {
                rejMaterialUnknown++;
                continue;
            }
            if (diag == "workmanship-unread") rejWmUnread++;

            if (!byKey.TryGetValue(key, out var list)) { list = new List<uint>(); byKey[key] = list; }
            list.Add(id);
        }

        _combineGroups = new List<List<uint>>();
        int eligibleBags = 0;
        foreach (var kv in byKey)
        {
            eligibleBags += kv.Value.Count;
            if (kv.Value.Count >= 2)
                _combineGroups.Add(kv.Value);
        }

        if (_combineGroups.Count == 0)
        {
            _combineGroups = null;
            Log($"[Salvage] Combine sweep: scanned {scannedItems} inv items, {salvageBagCount} salvage bag(s), {underFullCount} under-full, {eligibleBags} mat-eligible, rej(mat?={rejMaterialUnknown},wm?={rejWmUnread}), 0 mergeable groups.");
            return;
        }
        Log($"[Salvage] Combine sweep: scanned {scannedItems} inv items, {salvageBagCount} salvage bag(s), {underFullCount} under-full, rej(mat?={rejMaterialUnknown},wm?={rejWmUnread}) → {_combineGroups.Count} mergeable group(s).");

        _combineGroupIdx = 0;
        _combineAddIdx = 0;
        _combineGroupDirect = false;
        _combineGroupPanelRetry = false;
        _combinePhase = CombinePhase.None;
        _combinePhaseReadyAt = now;
        _phase = Phase.CombiningSalvage;
    }

    private void TickCombiningSalvage(long now)
    {
        if (_combineGroups == null) { _phase = Phase.Idle; return; }
        if (_combineGroupIdx >= _combineGroups.Count)
        {
            _combineGroups = null;
            _combinePhase = CombinePhase.None;
            _phase = Phase.Idle;
            return;
        }

        if (now < _combinePhaseReadyAt) return;

        var group = _combineGroups[_combineGroupIdx];
        switch (_combinePhase)
        {
            case CombinePhase.None:
            {
                uint ust = FindUst();
                if (ust == 0)
                {
                    Log("[Salvage] Combine: no UST available — aborting combine cycle.");
            NoteError("combine: no UST in inventory");
                    _combineGroups = null;
                    _phase = Phase.Idle;
                    return;
                }
                // Without the panel: the group's bags go in one salvage request (ACE merges the
                // salvage bags listed in it). Not sent -> the panel takes this group.
                if (UseDirect && !_combineGroupPanelRetry)
                {
                    uint[] bagIds = group.Take(RynthCoreHost.MaxSalvageDirectItems).ToArray();
                    _salvageChat.Clear();
                    int sent = _host.SalvageDirect(ust, bagIds);
                    if (sent > 0)
                    {
                        _directRequests++;
                        _combineGroupDirect = true;
                        _combineGroupSnapshot = new List<uint>(bagIds);
                        _combinePhase = CombinePhase.WaitingForResult;
                        _combinePhaseReadyAt = now + DirectSettleMs;
                        _waitingResultDeadline = now + DirectResultMaxWaitMs;
                        break;
                    }
                    string why = sent switch
                    {
                        -1 => "the engine's salvage sender isn't bound on this client",
                        -2 => "the engine is older than plugin API v82",
                        _ => "the engine refused the request",
                    };
                    if (sent is -1 or -2) TurnDirectOff(why);
                    _directFallbacks++;
                    Log($"[Salvage] Combine group {_combineGroupIdx + 1}/{_combineGroups.Count}: request without the panel wasn't sent ({why}) — using the panel.");
                }
                if (!PanelAvailable)
                {
                    Log($"[Salvage] Combine group {_combineGroupIdx + 1}/{_combineGroups.Count}: no salvage panel on this client — skipping the group.");
                    NoteError("combine: no salvage panel calls on this client");
                    _combineGroupIdx++;
                    _combineAddIdx = 0;
                    _combineGroupPanelRetry = false;
                    _combinePhaseReadyAt = now + RetryBackoffMs;
                    return;
                }
                _salvageChat.Clear();
                if (!_host.UseFor(ust, "Salvage", "combine salvage: open the salvage panel (UST)"))
                {
                    _combineOpenAttempts++;
                    if (_combineOpenAttempts < MaxCombineOpenAttempts)
                    {
                        Log($"[Salvage] Combine: UseObject(UST) failed — retrying this group in {RetryBackoffMs}ms (attempt {_combineOpenAttempts}/{MaxCombineOpenAttempts}).");
                        _combinePhaseReadyAt = now + RetryBackoffMs;
                        return;
                    }
                    Log($"[Salvage] Combine: UseObject(UST) failed {_combineOpenAttempts}x — skipping this group.");
                    NoteError($"combine: UseObject(UST) failed {_combineOpenAttempts}x — group skipped");
                    _combineOpenAttempts = 0;
                    _combineGroupIdx++;
                    _combineAddIdx = 0;
                    _combineGroupPanelRetry = false;
                    _combinePhaseReadyAt = now + RetryBackoffMs;
                    return;
                }
                _combineOpenAttempts = 0;
                int openDelay = _panelEverOpened ? _settings.SalvageOpenDelayFastMs : _settings.SalvageOpenDelayFirstMs;
                _combinePhaseReadyAt = now + openDelay;
                _combinePhase = CombinePhase.OpeningPanel;
                break;
            }
            case CombinePhase.OpeningPanel:
            {
                _panelEverOpened = true;
                _combineAddIdx = 0;
                _combinePhase = CombinePhase.AddingBags;
                _combinePhaseReadyAt = now;
                break;
            }
            case CombinePhase.AddingBags:
            {
                if (_combineAddIdx < group.Count)
                {
                    uint bagId = group[_combineAddIdx];
                    if (_host.SalvagePanelAddItem(bagId))
                        Log($"[Salvage] Combine: added bag 0x{bagId:X8} to panel ({_combineAddIdx + 1}/{group.Count}).");
                    else
                        Log($"[Salvage] Combine: SalvagePanelAddItem failed for 0x{bagId:X8}.");
                    _combineAddIdx++;
                    _combinePhaseReadyAt = now + _settings.SalvageAddDelayFastMs;
                }
                else
                {
                    _combinePhase = CombinePhase.Salvaging;
                    _combinePhaseReadyAt = now;
                }
                break;
            }
            case CombinePhase.Salvaging:
            {
                // Snapshot the bag IDs we expect the server to merge so the
                // verification pass in WaitingForResult can count survivors.
                _combineGroupSnapshot = new List<uint>(group);

                if (!_host.SalvagePanelExecute())
                    Log($"[Salvage] Combine group {_combineGroupIdx + 1}/{_combineGroups!.Count}: SalvagePanelExecute returned false (group of {group.Count} bag(s)) — verifying server-side outcome anyway.");

                _combinePhase = CombinePhase.WaitingForResult;
                _combinePhaseReadyAt = now + _settings.SalvageSalvageDelayMs + _settings.SalvageResultDelayFastMs;
                _waitingResultDeadline = _combinePhaseReadyAt + ResultMaxWaitMs;
                break;
            }
            case CombinePhase.WaitingForResult:
            {
                // Poll: if any snapshot bag is still in inventory and we're
                // under the deadline, wait. Server delete events for merged
                // bags arrive after the result delay on busy servers.
                if (now < _waitingResultDeadline
                    && _combineGroupSnapshot != null
                    && AnySnapshotBagStillInInventory(_combineGroupSnapshot))
                {
                    break;
                }
                bool merged = VerifyCombineGroupResult();
                if (!merged && _combineGroupDirect && PanelAvailable)
                {
                    // The request without the panel merged nothing: this group once more, through the panel.
                    _directFallbacks++;
                    Log($"[Salvage] Combine group {_combineGroupIdx + 1}/{_combineGroups!.Count}: the request without the panel merged nothing"
                        + (_salvageChat.Count > 0 ? ChatNote() : "; no salvage reply from the server") + " — trying the panel once.");
                    _combineGroupDirect = false;
                    _combineGroupPanelRetry = true;
                    _combineAddIdx = 0;
                    _combinePhase = CombinePhase.None;
                    _combinePhaseReadyAt = now;
                    break;
                }
                _combineGroupDirect = false;
                _combineGroupPanelRetry = false;
                _salvageChat.Clear();
                _combineGroupIdx++;
                _combineAddIdx = 0;
                _combinePhase = CombinePhase.None;
                _combinePhaseReadyAt = now;
                break;
            }
        }
    }

    /// <summary>
    /// Compares the surviving bag ids against the snapshot taken before
    /// SalvagePanelExecute. Uses GetDirectInventory(forceRefresh: true) for
    /// ground truth — _cache[id] is unreliable for salvage-consumed bags
    /// because OnDeleteObject doesn't always fire on the merge path.
    ///
    /// Outcomes (M = input bags, S = survivors):
    ///   S == M       → no merge happened (server ignored Execute / panel was empty / etc.)
    ///   S == 1       → clean merge into a single bag
    ///   1 &lt; S &lt; M     → partial merge (overflow / mixed-band / next sweep can finish it)
    /// </summary>
    private bool VerifyCombineGroupResult()
    {
        var snapshot = _combineGroupSnapshot;
        _combineGroupSnapshot = null;
        if (snapshot == null || snapshot.Count == 0 || _cache == null) return true;

        var liveIds = SnapshotDirectInventoryIds();
        int survivors = 0;
        foreach (uint id in snapshot)
        {
            if (liveIds.Contains(id))
                survivors++;
        }

        int merged = snapshot.Count - survivors;
        int groupNum = _combineGroupIdx + 1;
        int totalGroups = _combineGroups?.Count ?? 0;
        string how = _combineGroupDirect ? " (without the panel)" : _combineGroupPanelRetry ? " (panel retry)" : "";

        if (survivors == snapshot.Count)
        {
            _combineGroupsFailed++;
            string ids = string.Join(", ", snapshot.ConvertAll(id => $"0x{id:X8}"));
            Log($"[Salvage] Combine group {groupNum}/{totalGroups}{how}: NO MERGE — all {snapshot.Count} bag(s) still in inventory. Surviving ids: {ids}. Session: {_combineGroupsSucceeded} ok / {_combineGroupsFailed} failed, {_bagsMergedThisSession} bag(s) merged.");
            return false;
        }

        _combineGroupsSucceeded++;
        _bagsMergedThisSession += merged;

        if (survivors <= 1)
        {
            Log($"[Salvage] Combine group {groupNum}/{totalGroups}{how}: merged {snapshot.Count} bag(s) → {survivors} survivor (+{merged} consumed). Session: {_combineGroupsSucceeded} ok / {_combineGroupsFailed} failed, {_bagsMergedThisSession} bag(s) merged.");
        }
        else
        {
            Log($"[Salvage] Combine group {groupNum}/{totalGroups}{how}: PARTIAL merge — {snapshot.Count} bag(s) → {survivors} survivors (+{merged} consumed). Remaining bag(s) will be picked up by next sweep. Session: {_combineGroupsSucceeded} ok / {_combineGroupsFailed} failed, {_bagsMergedThisSession} bag(s) merged.");
        }
        return true;
    }

    /// <summary>
    /// True when a salvage bag is under-full (structure &lt; max) and thus worth
    /// merging. The #92/#91 property reads are authoritative but fail-close off
    /// the AC main thread (P0-2 qualities gate) — which is ALWAYS the case from
    /// the plugin pump. The old code then "assumed under-full", so on this ACE
    /// server (bags are "Salvage (N)" where N is the FILL level —
    /// Player_Crafting.cs ~281, see TryGetSalvageBagMaterial) every FULL
    /// "Salvage (100)" looked mergeable forever → a combine sweep every 30s
    /// that re-merged already-full bags and interrupted vendors/gameplay.
    /// Fix: when the properties can't be read, trust the bag NAME's trailing
    /// "(N)" fill level (covers both "Iron Salvage (91)" and ACE "Salvage
    /// (100)"); only if there's no parseable fill do we treat the bag as full
    /// (skip) so an unreadable bag can never drive an endless sweep.
    /// </summary>
    private bool IsBagUnderFull(uint bagId, string? name)
    {
        if (_host.TryGetObjectIntProperty(bagId, StypeStructure, out int cur)
            && _host.TryGetObjectIntProperty(bagId, StypeMaxStructure, out int max))
            return max <= 0 || cur < max;

        if (TryParseTrailingNumber(name, out int fill))
            return fill < 100;

        Log($"[Salvage] IsBagUnderFull: Structure unreadable AND name '{name}' has no numeric fill — treating 0x{bagId:X8} as full (skip) so it can't drive an endless sweep.");
        return false;
    }

    /// <summary>
    /// Resolves a salvage bag's material id from MaterialType(#131) — the only
    /// reliable source. The previous "parse trailing (NN) as material" fallback
    /// was WRONG on ACE: ACE names bags "Salvage ({Structure})"
    /// (Player_Crafting.cs ~line 281) — the number is the bag's FILL LEVEL, not
    /// its material — so the fallback scattered same-material bags across
    /// different bogus keys and they never combined. When the property can't be
    /// read we skip the bag for this sweep; the next sweep picks it up once the
    /// client has the bag's material cached.
    /// </summary>
    private bool TryGetSalvageBagMaterial(uint bagId, string? name, out int material)
    {
        material = 0;
        if (_host.TryGetObjectIntProperty(bagId, StypeMaterialType, out int mat) && mat > 0)
        {
            material = mat;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Resolves the combine grouping key for a salvage BAG.
    ///
    /// ACE's server merges bags by MaterialType + under-full only
    /// (Player_Crafting.GetSalvageBag) — workmanship is never a server-side
    /// merge criterion. Banding is a client-side user preference to keep
    /// different-workmanship salvage in separate bags. A bag's int
    /// ItemWorkmanship(#105) is a cumulative SUM (TryAddSalvage), so the band
    /// must be derived from the AVERAGE: #105 / NumItemsInMaterial(#170).
    ///
    /// When workmanship can't be read we fall back to a "{mat}|?" key instead
    /// of excluding the bag — excluding every bag is exactly the original
    /// "never combines" bug. Same-material unknown-band bags then still merge
    /// with each other, which is server-safe (the server ignores workmanship).
    /// Returns false only when the MATERIAL itself is unknown (can't group at
    /// all); <paramref name="diag"/> carries a short reason for logging.
    /// </summary>
    private bool TryGetBagCombineKey(uint bagId, string? name,
        RynthCore.Loot.SalvageCombineSettings? cfg, bool useBands,
        out string key, out string diag)
    {
        key = string.Empty;
        diag = string.Empty;

        if (!TryGetSalvageBagMaterial(bagId, name, out int mat))
        {
            diag = "material-unknown";
            return false;
        }

        if (!useBands)
        {
            key = mat.ToString();
            return true;
        }

        if (TryGetBagAvgWorkmanship(bagId, out int avgWm))
        {
            string? band = cfg!.GetBandKey(mat, avgWm);
            if (band != null)
            {
                key = $"{mat}|{band}";
                return true;
            }
            // avg falls in a gap between configured bands — keep it separate by
            // exact average so identical-average bags still merge together.
            diag = $"avgWm {avgWm} outside bands";
            key = $"{mat}|wm{avgWm}";
            return true;
        }

        diag = "workmanship-unread";
        key = $"{mat}|?";
        return true;
    }

    /// <summary>
    /// Average workmanship of a salvage bag = ItemWorkmanship(#105, a cumulative
    /// sum) / NumItemsInMaterial(#170). Integer floor — band ranges are integer,
    /// so flooring is exact for grouping (bags with the same average land in the
    /// same band). Returns false if either property is unreadable or the bag is
    /// empty/fresh (#170 == 0), in which case the caller treats the band as
    /// unknown rather than excluding the bag.
    /// </summary>
    private bool TryGetBagAvgWorkmanship(uint bagId, out int avgWm)
    {
        avgWm = 0;
        if (!_host.TryGetObjectIntProperty(bagId, StypeItemWorkmanship, out int sum) || sum <= 0)
            return false;
        if (!_host.TryGetObjectIntProperty(bagId, StypeNumItemsInMaterial, out int n) || n <= 0)
            return false;
        avgWm = sum / n;
        if (avgWm < 1) avgWm = 1;
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True if any of <paramref name="ids"/> appears in a fresh walk of the player's
    /// inventory containers. Ground truth for "did the server actually destroy
    /// this item" — the cache's _byId can hold stale entries for salvage-
    /// consumed items because OnDeleteObject doesn't fire on every merge path.
    /// </summary>
    private bool AnyStillInPack(List<uint> ids) => ids.Count > 0 && AnySnapshotBagStillInInventory(ids);

    /// <summary>
    /// Snapshots the ids currently in the player's direct inventory as a
    /// HashSet for O(1) survivor counting.
    /// </summary>
    private HashSet<uint> SnapshotDirectInventoryIds()
    {
        var set = new HashSet<uint>();
        if (_cache == null) return set;
        foreach (var item in _cache.GetDirectInventory(forceRefresh: true))
            set.Add(unchecked((uint)item.Id));
        return set;
    }

    /// <summary>
    /// True if any of <paramref name="snapshot"/>'s ids are still in the
    /// player's direct inventory. Used by combine WaitingForResult polling.
    /// </summary>
    private bool AnySnapshotBagStillInInventory(List<uint> snapshot)
    {
        var liveIds = SnapshotDirectInventoryIds();
        foreach (uint id in snapshot)
        {
            if (liveIds.Contains(id)) return true;
        }
        return false;
    }

    // Canonical UST weenie class id. Same for every UST instance on every
    // character and every server (the WCID is part of the weenie definition,
    // not the per-instance GUID). Most reliable signal we have.
    private const uint UstWcid = 20646;

    private uint FindUst()
    {
        if (_cache == null)
            return 0;

        // Pass 1 — WCID match. Universal: every UST shares weenie class 20646
        // so this works on every server / variant / character regardless of
        // name, ItemType classification, or cache state.
        if (_host.HasGetObjectWcid)
        {
            foreach (var item in _cache.AllKnownObjects())
            {
                if (!IsCarriedByPlayer(item)) continue;
                if (!_host.TryGetObjectWcid(unchecked((uint)item.Id), out uint wcid)) continue;
                if (wcid == UstWcid) return unchecked((uint)item.Id);
            }
        }

        // Pass 2 — type-based via the cached ObjectClass. Same intent as the
        // WCID pass but reads the cached classification (ItemType TinkeringTool
        // bit). Catches USTs whose WCID lookup hasn't been bound on the host.
        foreach (var item in _cache.AllKnownObjects())
        {
            if (item.ObjectClass != AcObjectClass.Ust) continue;
            if (!IsCarriedByPlayer(item)) continue;
            return unchecked((uint)item.Id);
        }

        // Pass 3 — name-based fallback. Some items get cached before their
        // ItemType is read; the cache stays AcObjectClass.Unknown until then.
        // The name path catches those.
        foreach (WorldObject item in _cache.GetDirectInventory(forceRefresh: false))
        {
            if (IsUst(item.Name)) return unchecked((uint)item.Id);
        }

        // Pass 4 — same name fallback after a forced refresh.
        var fresh = _cache.GetDirectInventory(forceRefresh: true);
        foreach (WorldObject item in fresh)
        {
            if (IsUst(item.Name)) return unchecked((uint)item.Id);
        }

        // Pass 5 — WCID retry over the freshly-walked direct inventory in case
        // a UST only just appeared in the cache via the forced refresh.
        if (_host.HasGetObjectWcid)
        {
            foreach (var item in fresh)
            {
                if (!_host.TryGetObjectWcid(unchecked((uint)item.Id), out uint wcid)) continue;
                if (wcid == UstWcid) return unchecked((uint)item.Id);
            }
        }

        // Diagnostic on miss — sample what's in inventory so we can see
        // whether the UST really isn't there or just has an unexpected name.
        int shown = 0;
        var names = new List<string>();
        foreach (WorldObject item in fresh)
        {
            if (string.IsNullOrEmpty(item.Name)) continue;
            names.Add(item.Name);
            if (++shown >= 40) break;
        }
        Log($"[Salvage] UST search miss. {fresh.Count} item(s) scanned. Sample: {string.Join(", ", names)}");

        return 0;
    }

    /// <summary>
    /// True when the item lives in the player's pack (or one of the player's
    /// side-packs). Skips USTs lying in nearby corpses or another player's pack.
    /// </summary>
    private bool IsCarriedByPlayer(WorldObject item)
    {
        if (_cache == null) return false;
        uint playerId = _host.GetPlayerId();
        // Fail closed when the player id isn't readable yet — matches every other
        // playerId==0 guard in the plugin (InventoryManager:94, WorldObjectCache:977,
        // FindPackFor). Returning true here counted every scanned object as ours.
        if (playerId == 0) return false;

        int pid = unchecked((int)playerId);
        if (item.Wielder != 0 && item.Wielder != pid) return false;
        if (item.Container == pid) return true;
        // Container==0 means "not in any pack" — that's a wielded item (ours only if
        // we're the wielder) or an item lying on the ground. It used to return true
        // for both, so ground USTs read as carried. 2026-06-03 audit P2.
        if (item.Container == 0) return item.Wielder == pid;

        var owner = _cache[item.Container];
        if (owner == null) return false;
        if (owner.ObjectClass == AcObjectClass.Corpse) return false;
        return owner.Wielder == pid || owner.Container == pid;
    }

    private static bool IsUst(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        // UST names carry "Ust" as its own word (e.g. "Salvaging Ust", "Aged Legendary
        // Salvaging Ust", "Sturdy Iron Salvaging Ust"). A bare Contains also matched
        // "Just"/"Robust"/"Lustrous" (2026-06-03 audit P2); a word match keeps working
        // if a name ever carries a suffix, which EndsWith would not.
        // Exclude actual salvage bags — the "Salvage (Material)" format, via IsSalvageBag.
        return HasWordUst(name) && !IsSalvageBag(name);
    }

    /// <summary>True when "Ust" appears as a whole word (letter/digit boundaries).</summary>
    private static bool HasWordUst(string name)
    {
        for (int i = 0; i + 3 <= name.Length; i++)
        {
            if (!(name[i] is 'U' or 'u') || !(name[i + 1] is 'S' or 's') || !(name[i + 2] is 'T' or 't'))
                continue;
            if (i > 0 && char.IsLetterOrDigit(name[i - 1])) continue;
            if (i + 3 < name.Length && char.IsLetterOrDigit(name[i + 3])) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses a trailing "(NN)" group from a salvage-bag name (e.g.
    /// "Gold Salvage (91)" → 91). Returns false if the name has no trailing
    /// integer in parentheses.
    /// </summary>
    private static bool TryParseTrailingNumber(string? name, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(name)) return false;
        int close = name.LastIndexOf(')');
        if (close <= 0 || close != name.Length - 1) return false;
        int open = name.LastIndexOf('(', close - 1);
        if (open < 0) return false;
        string inside = name.Substring(open + 1, close - open - 1);
        return int.TryParse(inside, out value);
    }

    private static bool IsSalvageBag(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        // Two known formats observed across servers/emulators:
        //   "Salvage (Gold)"   — retail-style, material in parens
        //   "Gold Salvage (91)" — emulator style, fullness in parens
        // Both contain "Salvage (" as a substring; "Salvaging Ust" doesn't.
        return name.Contains("Salvage (", StringComparison.OrdinalIgnoreCase);
    }

    private void Log(string message) => RynthLog.Write(LogCat.Salvage, message);

    private void NoteError(string what)
    {
        _lastError   = what;
        _lastErrorAt = NowMs;
    }

    /// <summary>
    /// One-shot view of the salvage FSM. Salvage had no snapshot (Combat/Buff/Meta
    /// did), so a wedged panel cycle meant reading the log backwards to work out
    /// which phase it died in. Read-only; safe from the UI or a chat command.
    /// </summary>
    public SalvageStateSnapshot GetStateSnapshot()
    {
        long now = NowMs;
        return new SalvageStateSnapshot
        {
            HasPanelApi        = _host.HasSalvagePanel && _host.HasUseObject,
            HasDirectApi       = _host.HasSalvageDirect,
            DirectOn           = UseDirect,
            DirectOffReason    = _directOff ? _directOffReason : (!_settings.SalvageWithoutPanel ? "setting off" : !_host.HasSalvageDirect ? "engine before API v82" : string.Empty),
            DirectRequests     = _directRequests,
            DirectFallbacks    = _directFallbacks,
            EnableCombine      = _settings.EnableCombineSalvage,
            Phase              = _phase.ToString(),
            PhaseReadyInMs     = Math.Max(0, _phaseReadyAt - now),
            QueueCount         = _queue.Count,
            CurrentItemId      = _batch.Count > 0 ? _batch[0] : 0,
            CurrentUstId       = _currentUstId,
            PanelEverOpened    = _panelEverOpened,
            PendingCombineScan = _pendingCombineScan,
            RetryTrackedItems  = _itemRetryCount.Count,
            RetryBackoffInMs   = Math.Max(0, _idleRetryReadyAt - now),
            CombinePhase       = _combinePhase.ToString(),
            CombineGroupIdx    = _combineGroupIdx,
            CombineGroupCount  = _combineGroups?.Count ?? 0,
            CombineAddIdx      = _combineAddIdx,
            CombineOpenAttempts = _combineOpenAttempts,
            MsSinceCombineSweep = _lastCombineSweepAt == 0 ? -1 : now - _lastCombineSweepAt,
            GroupsSucceeded    = _combineGroupsSucceeded,
            GroupsFailed       = _combineGroupsFailed,
            BagsMerged         = _bagsMergedThisSession,
            LastError          = _lastError,
            MsSinceLastError   = _lastErrorAt == 0 ? -1 : now - _lastErrorAt,
            TrackedItems       = _tracked.Count,
            BatchSize          = _batch.Count,
            Batches            = _batchesThisSession,
            ItemsSalvaged      = _itemsSalvagedThisSession,
            ItemsSkipped       = _itemsSkippedThisSession,
        };
    }

    public struct SalvageStateSnapshot
    {
        public bool   HasPanelApi;
        /// <summary>The engine has SalvageDirect (API v82).</summary>
        public bool   HasDirectApi;
        /// <summary>Requests go out without the panel now.</summary>
        public bool   DirectOn;
        /// <summary>Why not, when <see cref="DirectOn"/> is false ("" when on).</summary>
        public string DirectOffReason;
        public int    DirectRequests;
        public int    DirectFallbacks;
        public bool   EnableCombine;
        public string Phase;
        public long   PhaseReadyInMs;
        public int    QueueCount;
        public uint   CurrentItemId;
        public uint   CurrentUstId;
        public bool   PanelEverOpened;
        public bool   PendingCombineScan;
        public int    RetryTrackedItems;
        public long   RetryBackoffInMs;
        public string CombinePhase;
        public int    CombineGroupIdx;
        public int    CombineGroupCount;
        public int    CombineAddIdx;
        public int    CombineOpenAttempts;
        public long   MsSinceCombineSweep;
        public int    GroupsSucceeded;
        public int    GroupsFailed;
        public int    BagsMerged;
        public string LastError;
        public long   MsSinceLastError;
        public int    TrackedItems;
        public int    BatchSize;
        public int    Batches;
        public int    ItemsSalvaged;
        public int    ItemsSkipped;
    }
}

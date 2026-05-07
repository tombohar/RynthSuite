using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// Drives the salvage state machine for loot-rule items marked Salvage.
/// Items are enqueued by CorpseOpenController after pickup confirmation.
///
/// Flow per item:
///   Idle → call OpenSalvagePanel(ustId), wait OpenDelay
///        → call AddNewItem(itemId), wait AddDelay
///        → call Salvage(), wait SalvageDelay
///        → wait ResultDelay → Idle (next item or CombineSalvage)
///
/// First-open delays (400ms/600ms) let the panel animate open and the
/// gmSalvageUI singleton hook fire before we make thiscall instance calls.
/// Fast delays (50ms) apply once the panel has been opened at least once.
/// </summary>
public sealed class SalvageManager
{
    private enum Phase { Idle, OpeningPanel, AddingItem, Salvaging, WaitingForResult, CombiningSalvage }

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache? _cache;

    private readonly Queue<uint> _queue = new();
    private Phase _phase = Phase.Idle;
    private long _phaseReadyAt;
    private uint _currentItemId;
    private uint _currentUstId;
    private bool _panelEverOpened;
    private bool _firstResultCycle = true;
    private bool _pendingCombineScan;

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
    // before we give up. AC sometimes drops a Salvage execute when the bot is
    // busy with other actions; the item stays in the pack and we want to try
    // again when the queue gets back to idle.
    private readonly Dictionary<uint, int> _itemRetryCount = new();
    private const int MaxItemRetries = 3;

    // Periodic combine sweep — even when no items have been salvaged this
    // session (or every salvage gave up after retries), we still want to
    // periodically merge any under-full bags sitting in inventory.
    private long _lastCombineSweepAt;
    private const long CombineSweepIntervalMs = 30_000;

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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

    /// <summary>Returns true while a salvage operation is in flight.</summary>
    public bool IsBusy => _phase != Phase.Idle || _queue.Count > 0;

    /// <summary>Enqueue an item to be salvaged. Called by CorpseOpenController after pickup.</summary>
    public void EnqueueItem(uint itemId)
    {
        if (itemId != 0)
        {
            _queue.Enqueue(itemId);
            _pendingCombineScan = false; // reset; will be set again when queue drains
        }
    }

    /// <summary>Called every game tick from RynthAiPlugin.OnTick.</summary>
    public void OnTick(int busyCount)
    {
        if (!_host.HasSalvagePanel || !_host.HasUseObject)
            return;

        long now = NowMs;

        switch (_phase)
        {
            case Phase.Idle:
                TickIdle(now, busyCount);
                break;

            case Phase.OpeningPanel:
                if (now >= _phaseReadyAt)
                    BeginAddingItem(now);
                break;

            case Phase.AddingItem:
                if (now >= _phaseReadyAt)
                    BeginSalvaging(now);
                break;

            case Phase.Salvaging:
                if (now >= _phaseReadyAt)
                    BeginWaitingForResult(now);
                break;

            case Phase.WaitingForResult:
                if (now < _phaseReadyAt) break;
                // Poll: if the item is still in inventory and we're under the
                // deadline, wait for the next tick. Server delete events for
                // salvage-consumed items can lag the result delay by 500ms+.
                if (_currentItemId != 0
                    && now < _waitingResultDeadline
                    && IsItemInDirectInventory(_currentItemId))
                {
                    break;
                }
                OnResultReady(now);
                break;

            case Phase.CombiningSalvage:
                TickCombiningSalvage(now);
                break;
        }
    }

    // ── Phase handlers ────────────────────────────────────────────────────────

    private void TickIdle(long now, int busyCount)
    {
        if (_queue.Count == 0)
        {
            if (_pendingCombineScan && _settings.EnableCombineSalvage)
            {
                _pendingCombineScan = false;
                _lastCombineSweepAt = now;
                BeginCombineSalvage(now);
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
                BeginCombineSalvage(now);
            }
            return;
        }

        // Don't start while the game is busy processing a prior action.
        if (busyCount > 0)
            return;

        uint itemId = _queue.Dequeue();
        if (itemId == 0)
            return;

        uint ustId = FindUst();
        if (ustId == 0)
        {
            _host.WriteToChat("[RynthAi] Salvage: No UST found in inventory — item skipped. Add a Ust to your pack.", 2);
            Log($"[Salvage] No UST found in inventory — skipping 0x{itemId:X8}.");
            return;
        }

        _currentItemId = itemId;
        _currentUstId = ustId;

        // UseObject on the UST mimics a double-click — this is the reliable path to
        // trigger gmSalvageUI::OpenSalvagePanel (our hook captures the instance there).
        // SendNotice_OpenSalvagePanel does not reliably call the hooked function.
        if (!_host.UseObject(_currentUstId))
        {
            Log($"[Salvage] UseObject(UST) failed for 0x{_currentUstId:X8} — re-queuing item.");
            _queue.Enqueue(itemId); // re-queue so we retry next tick
            _currentItemId = 0;
            _currentUstId = 0;
            return;
        }

        int openDelay = _panelEverOpened ? _settings.SalvageOpenDelayFastMs : _settings.SalvageOpenDelayFirstMs;
        _phaseReadyAt = now + openDelay;
        _phase = Phase.OpeningPanel;
    }

    private void BeginAddingItem(long now)
    {
        if (!_host.SalvagePanelAddItem(_currentItemId))
        {
            Log($"[Salvage] SalvagePanelAddItem failed for 0x{_currentItemId:X8} (panel instance not ready — re-queuing).");
            // Re-queue the item so we retry on the next idle cycle. Reset panel-ever-opened
            // so the longer first-open delays are applied on retry.
            _queue.Enqueue(_currentItemId);
            _currentItemId = 0;
            _currentUstId = 0;
            _panelEverOpened = false;
            _phase = Phase.Idle;
            return;
        }

        // Combine-during-salvage: add ALL under-full bags of the same material
        // (and workmanship band, if configured) alongside the new item so the
        // server merges everything in one operation.
        int extraBags = 0;
        if (_settings.CombineBagsDuringSalvage)
            extraBags = AddAllMatchingUnderFullBags(_currentItemId);

        bool wasFirstOpen = !_panelEverOpened;
        _panelEverOpened = true;
        // Allow 50 ms per extra bag so AC can register each add before Execute fires.
        int addDelay = (wasFirstOpen ? _settings.SalvageAddDelayFirstMs : _settings.SalvageAddDelayFastMs)
                       + extraBags * _settings.SalvageAddDelayFastMs;
        _phaseReadyAt = now + addDelay;
        _phase = Phase.AddingItem;
    }

    // STypes (canonical values from Chorizite STypes.cs — verified against
    // the live binary; the PropertyNames.IntNames index in this codebase is
    // off-by-some-rows so do NOT use it as the source of truth).
    private const uint StypeMaxStructure    = 91;
    private const uint StypeStructure       = 92;
    private const uint StypeItemWorkmanship = 105;
    private const uint StypeMaterialType    = 131;

    /// <summary>
    /// Scans inventory for every under-full salvage bag whose material (and
    /// workmanship band, if configured) matches <paramref name="itemId"/>, adds
    /// each one to the open salvage panel, and returns how many were added.
    /// Adding them all at once lets the server merge everything in one Salvage
    /// operation instead of leaving partial bags for the periodic sweep.
    /// </summary>
    private int AddAllMatchingUnderFullBags(uint itemId)
    {
        if (_cache == null) return 0;
        if (!_host.TryGetObjectIntProperty(itemId, StypeMaterialType, out int itemMat) || itemMat == 0)
            return 0;

        RynthCore.Loot.SalvageCombineSettings? cfg = CombineConfigProvider?.Invoke();
        bool useBands = cfg != null && cfg.Enabled;

        string? itemBand = null;
        if (useBands)
        {
            if (!_host.TryGetObjectIntProperty(itemId, StypeItemWorkmanship, out int itemWm) || itemWm <= 0)
                return 0;
            itemBand = cfg!.GetBandKey(itemMat, itemWm);
            if (itemBand == null) return 0;
        }

        int added = 0;
        foreach (WorldObject bag in _cache.GetDirectInventory(forceRefresh: true))
        {
            uint bagId = unchecked((uint)bag.Id);
            if (bagId == itemId) continue;
            if (!IsSalvageBag(bag.Name)) continue;
            if (!TryGetSalvageBagMaterial(bagId, bag.Name, out int bagMat) || bagMat != itemMat)
                continue;
            if (!IsBagUnderFull(bagId, bag.Name)) continue;

            if (useBands)
            {
                if (!_host.TryGetObjectIntProperty(bagId, StypeItemWorkmanship, out int bagWm) || bagWm <= 0) continue;
                string? bagBand = cfg!.GetBandKey(bagMat, bagWm);
                if (bagBand != itemBand) continue;
            }

            if (_host.SalvagePanelAddItem(bagId))
            {
                Log($"[Salvage] Combine-during-salvage: added bag {bag.Name} 0x{bagId:X8} ({added + 1}).");
                added++;
            }
        }

        return added;
    }

    private void BeginSalvaging(long now)
    {
        if (!_host.SalvagePanelExecute())
        {
            Log($"[Salvage] SalvagePanelExecute failed for 0x{_currentItemId:X8} — re-queuing.");
            RequeueOrDrop(_currentItemId, "execute failed");
            _currentItemId = 0;
            _phase = Phase.Idle;
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

    private void OnResultReady(long now)
    {
        // If the item we tried to salvage is STILL in the player's actual
        // inventory after the result delay, the salvage didn't consume it
        // (panel closed mid-execute, bot got busy, AC dropped the request,
        // etc.). Re-queue so we try again.
        //
        // We use GetDirectInventory(forceRefresh: true) — a live walk of the
        // player's containers — instead of _cache[id] because salvage-consumed
        // items don't reliably trigger OnDeleteObject in the cache, leading to
        // false positives where successfully-salvaged items get re-queued.
        uint itemId = _currentItemId;
        bool itemStillPresent = itemId != 0 && IsItemInDirectInventory(itemId);

        _currentItemId = 0;
        _phase = Phase.Idle;

        if (itemStillPresent)
        {
            Log($"[Salvage] Item 0x{itemId:X8} still in inventory after salvage cycle — re-queuing.");
            RequeueOrDrop(itemId, "item still present after result");
        }
        else
        {
            // Successful salvage — clear retry counter for this id.
            _itemRetryCount.Remove(itemId);
        }

        // Always trigger a combine scan on the next idle tick when the queue
        // drains — both to consolidate any newly-merged bags and so combine
        // doesn't depend on the busyCount==0 gate of the periodic timer (which
        // an active combat macro rarely satisfies).
        if (_queue.Count == 0)
            _pendingCombineScan = true;
    }

    /// <summary>
    /// Re-queue an item that failed a salvage step. Counts retries per id and
    /// drops the item once it's failed MaxItemRetries times in a row, so a
    /// truly un-salvageable item doesn't trap the queue forever.
    /// </summary>
    private void RequeueOrDrop(uint itemId, string reason)
    {
        if (itemId == 0) return;

        int prior = _itemRetryCount.TryGetValue(itemId, out int n) ? n : 0;
        int next = prior + 1;
        if (next > MaxItemRetries)
        {
            _itemRetryCount.Remove(itemId);
            Log($"[Salvage] Giving up on 0x{itemId:X8} after {MaxItemRetries} attempts ({reason}).");
            // Trigger a combine scan if dropping this item left the queue empty.
            if (_queue.Count == 0) _pendingCombineScan = true;
            return;
        }
        _itemRetryCount[itemId] = next;
        _queue.Enqueue(itemId);
        Log($"[Salvage] Re-queued 0x{itemId:X8} ({reason}) — attempt {next}/{MaxItemRetries}.");
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
        foreach (WorldObject item in inv)
        {
            if (!IsSalvageBag(item.Name)) continue;
            salvageBagCount++;
            uint id = unchecked((uint)item.Id);
            if (!IsBagUnderFull(id, item.Name)) continue;
            underFullCount++;
            if (!TryGetSalvageBagMaterial(id, item.Name, out int mat)) continue;

            string key;
            if (useBands)
            {
                if (!_host.TryGetObjectIntProperty(id, StypeItemWorkmanship, out int wm) || wm <= 0) continue;
                string? band = cfg!.GetBandKey(mat, wm);
                if (band == null) continue; // workmanship outside any defined band — keep separate
                key = $"{mat}|{band}";
            }
            else
            {
                key = mat.ToString();
            }

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
            Log($"[Salvage] Combine sweep: scanned {scannedItems} inv items, {salvageBagCount} salvage bag(s), {underFullCount} under-full, {eligibleBags} mat-eligible, 0 mergeable groups.");
            return;
        }
        Log($"[Salvage] Combine sweep: scanned {scannedItems} inv items, {salvageBagCount} salvage bag(s), {underFullCount} under-full → {_combineGroups.Count} mergeable group(s).");

        _combineGroupIdx = 0;
        _combineAddIdx = 0;
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
                    _combineGroups = null;
                    _phase = Phase.Idle;
                    return;
                }
                if (!_host.UseObject(ust))
                {
                    Log("[Salvage] Combine: UseObject(UST) failed — skipping this group.");
                    _combineGroupIdx++;
                    _combineAddIdx = 0;
                    return;
                }
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
                VerifyCombineGroupResult();
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
    private void VerifyCombineGroupResult()
    {
        var snapshot = _combineGroupSnapshot;
        _combineGroupSnapshot = null;
        if (snapshot == null || snapshot.Count == 0 || _cache == null) return;

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

        if (survivors == snapshot.Count)
        {
            _combineGroupsFailed++;
            string ids = string.Join(", ", snapshot.ConvertAll(id => $"0x{id:X8}"));
            Log($"[Salvage] Combine group {groupNum}/{totalGroups}: NO MERGE — all {snapshot.Count} bag(s) still in inventory. Surviving ids: {ids}. Session: {_combineGroupsSucceeded} ok / {_combineGroupsFailed} failed, {_bagsMergedThisSession} bag(s) merged.");
            return;
        }

        _combineGroupsSucceeded++;
        _bagsMergedThisSession += merged;

        if (survivors <= 1)
        {
            Log($"[Salvage] Combine group {groupNum}/{totalGroups}: merged {snapshot.Count} bag(s) → {survivors} survivor (+{merged} consumed). Session: {_combineGroupsSucceeded} ok / {_combineGroupsFailed} failed, {_bagsMergedThisSession} bag(s) merged.");
        }
        else
        {
            Log($"[Salvage] Combine group {groupNum}/{totalGroups}: PARTIAL merge — {snapshot.Count} bag(s) → {survivors} survivors (+{merged} consumed). Remaining bag(s) will be picked up by next sweep. Session: {_combineGroupsSucceeded} ok / {_combineGroupsFailed} failed, {_bagsMergedThisSession} bag(s) merged.");
        }
    }

    /// <summary>
    /// Returns true when a salvage bag has structure < max (i.e. not full).
    /// Two name formats observed:
    ///   "Iron Salvage (91)" — emulator style, trailing number IS fullness
    ///   "Salvage (11)"      — retail/this-server style, trailing number is the
    ///                          MATERIAL ID, not fullness — must not be misread.
    /// For the second format we have to trust the property reads. Defaults to
    /// true on read failure: assume under-full and let the server reject a
    /// truly-full merge, vs. treating it as full and never combining (review §6).
    /// </summary>
    private bool IsBagUnderFull(uint bagId, string? name)
    {
        if (HasPrefixBeforeSalvage(name) && TryParseTrailingNumber(name, out int pct))
            return pct < 100;
        if (!_host.TryGetObjectIntProperty(bagId, StypeStructure, out int cur)) return true;
        if (!_host.TryGetObjectIntProperty(bagId, StypeMaxStructure, out int max)) return true;
        return max <= 0 || cur < max;
    }

    /// <summary>
    /// True if the bag name starts with something other than "Salvage" — i.e.
    /// "Iron Salvage (91)" returns true, "Salvage (11)" returns false. Used to
    /// disambiguate the two known bag-name formats so the trailing number is
    /// only interpreted as fullness when it's actually fullness.
    /// </summary>
    private static bool HasPrefixBeforeSalvage(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        ReadOnlySpan<char> s = name.AsSpan().TrimStart();
        return !s.StartsWith("Salvage".AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a salvage bag's material id. Tries the property read first;
    /// when that fails (AC's property cache is flaky for non-active bags),
    /// falls back to parsing the trailing number from "Salvage (X)" names
    /// where X is the material id on this server's naming convention.
    /// </summary>
    private bool TryGetSalvageBagMaterial(uint bagId, string? name, out int material)
    {
        if (_host.TryGetObjectIntProperty(bagId, StypeMaterialType, out int mat) && mat > 0)
        {
            material = mat;
            return true;
        }
        if (!HasPrefixBeforeSalvage(name)
            && TryParseTrailingNumber(name, out int parsedMat)
            && parsedMat > 0 && parsedMat < 256)
        {
            material = parsedMat;
            return true;
        }
        material = 0;
        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True if <paramref name="id"/> appears in a fresh walk of the player's
    /// inventory containers. Ground truth for "did the server actually destroy
    /// this item" — the cache's _byId can hold stale entries for salvage-
    /// consumed items because OnDeleteObject doesn't fire on every merge path.
    /// </summary>
    private bool IsItemInDirectInventory(uint id)
    {
        if (_cache == null) return false;
        var inv = _cache.GetDirectInventory(forceRefresh: true);
        foreach (var item in inv)
        {
            if (unchecked((uint)item.Id) == id) return true;
        }
        return false;
    }

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
        if (playerId == 0) return true;

        int pid = unchecked((int)playerId);
        if (item.Wielder != 0 && item.Wielder != pid) return false;
        if (item.Container == 0 || item.Container == pid) return true;

        var owner = _cache[item.Container];
        if (owner == null) return false;
        if (owner.ObjectClass == AcObjectClass.Corpse) return false;
        return owner.Wielder == pid || owner.Container == pid;
    }

    private static bool IsUst(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        // UST names end in "Ust" (e.g. "Salvaging Ust", "Aged Legendary Salvaging Ust",
        // "Sturdy Iron Salvaging Ust"). Exclude only actual salvage bags — those follow
        // the precise "Salvage (Material)" format, caught by IsSalvageBag.
        return name.Contains("Ust", StringComparison.OrdinalIgnoreCase)
            && !IsSalvageBag(name);
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

    private void Log(string message) => _host.Log(message);
}

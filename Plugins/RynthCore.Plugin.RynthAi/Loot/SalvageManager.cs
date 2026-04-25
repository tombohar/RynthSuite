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

    // Combine-salvage state
    private List<(string Name, List<uint> Ids)>? _combineBatches;
    private int _combineGroupIdx;
    private int _combineItemIdx;
    private long _combineActionAt;

    // Per-item retry counter so a failed salvage gets re-queued a few times
    // before we give up. AC sometimes drops a Salvage execute when the bot is
    // busy with other actions; the item stays in the pack and we want to try
    // again when the queue gets back to idle.
    private readonly Dictionary<uint, int> _itemRetryCount = new();
    private const int MaxItemRetries = 3;

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
                if (now >= _phaseReadyAt)
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
            if (_pendingCombineScan && _settings.EnableCombineSalvage && _panelEverOpened)
            {
                _pendingCombineScan = false;
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

        bool wasFirstOpen = !_panelEverOpened;
        _panelEverOpened = true;
        int addDelay = wasFirstOpen ? _settings.SalvageAddDelayFirstMs : _settings.SalvageAddDelayFastMs;
        _phaseReadyAt = now + addDelay;
        _phase = Phase.AddingItem;
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
        _phase = Phase.WaitingForResult;
    }

    private void OnResultReady(long now)
    {
        // If the item we tried to salvage is STILL in the cache after the
        // result delay, the salvage didn't actually consume it (the panel
        // may have closed mid-execute, the bot got busy, AC dropped the
        // request, etc.). Re-queue so we try again instead of leaving the
        // item dangling in the player's pack with mana value still on it.
        uint itemId = _currentItemId;
        bool itemStillPresent = itemId != 0 && _cache != null && _cache[unchecked((int)itemId)] != null;

        _currentItemId = 0;
        _phase = Phase.Idle;

        if (itemStillPresent)
        {
            Log($"[Salvage] Item 0x{itemId:X8} still in inventory after salvage cycle — re-queuing.");
            RequeueOrDrop(itemId, "item still present after result");
            return;
        }

        // Successful salvage — clear retry counter for this id (defensive; the
        // server has destroyed it anyway, but the dict shouldn't leak entries).
        _itemRetryCount.Remove(itemId);

        // If queue is now empty, trigger a combine scan on the next idle tick.
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
            return;
        }
        _itemRetryCount[itemId] = next;
        _queue.Enqueue(itemId);
        Log($"[Salvage] Re-queued 0x{itemId:X8} ({reason}) — attempt {next}/{MaxItemRetries}.");
    }

    // ── Combine salvage bags ──────────────────────────────────────────────────

    private void BeginCombineSalvage(long now)
    {
        if (_cache == null)
            return;

        // Build groups of salvage bags by name.
        var groups = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (WorldObject item in _cache.GetDirectInventory(forceRefresh: false))
        {
            if (!IsSalvageBag(item.Name))
                continue;

            if (!groups.TryGetValue(item.Name, out var list))
            {
                list = new List<uint>();
                groups[item.Name] = list;
            }
            list.Add(unchecked((uint)item.Id));
        }

        // Only keep groups with more than one bag.
        _combineBatches = new List<(string, List<uint>)>();
        foreach (var kv in groups)
        {
            if (kv.Value.Count > 1)
                _combineBatches.Add((kv.Key, kv.Value));
        }

        if (_combineBatches.Count == 0)
        {
            _combineBatches = null;
            return; // Nothing to combine.
        }

        _combineGroupIdx = 0;
        _combineItemIdx = 1; // index 0 is the target; start merging from index 1
        _combineActionAt = now;
        _phase = Phase.CombiningSalvage;
    }

    private void TickCombiningSalvage(long now)
    {
        if (_combineBatches == null || now < _combineActionAt)
            return;

        if (_combineGroupIdx >= _combineBatches.Count)
        {
            _combineBatches = null;
            _phase = Phase.Idle;
            return;
        }

        var (name, ids) = _combineBatches[_combineGroupIdx];
        if (_combineItemIdx >= ids.Count)
        {
            _combineGroupIdx++;
            _combineItemIdx = 1;
            return;
        }

        uint target = ids[0];
        uint source = ids[_combineItemIdx];
        if (source != 0 && target != 0 && source != target)
        {
            // Move source bag into target bag — the server combines same-type salvage.
            _host.MoveItemExternal(source, target, 0);
            Log($"[Salvage] Combine: moving {name} 0x{source:X8} → 0x{target:X8}.");
        }

        _combineItemIdx++;
        _combineActionAt = now + 150; // pace out combine actions
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static bool IsSalvageBag(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        return name.StartsWith("Salvage (", StringComparison.OrdinalIgnoreCase);
    }

    private void Log(string message) => _host.Log(message);
}

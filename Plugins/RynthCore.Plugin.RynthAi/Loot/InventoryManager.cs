using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// AutoCram + AutoStack. Ported from the legacy NexSuite2 LootManager.
///
/// AutoCram moves non-equipped loose items out of the player's main pack into
/// sub-packs so the main pack stays empty for new loot pickups.
///
/// AutoStack merges two partial stacks of the same item (arrows, components,
/// salvage, pyreals, etc.) by dropping the smaller one onto the larger one's
/// slot — the server auto-merges stackable same-type items at the same slot.
/// </summary>
public sealed class InventoryManager
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _cache;

    private DateTime _nextInventoryActionTime = DateTime.MinValue;

    // ── Confirm-via-snapshot + per-entry TTL/backoff state (P1b + P2b) ────────
    // Replaces the old wholesale 30s _failedStackPairs/_cramBlacklist clear, which
    // re-attempted (and re-relogged) a genuinely-unmovable item every 30s and could
    // not tell a successful enqueue from a failed one.
    //
    // PENDING = a move/merge was enqueued; we have NOT yet confirmed it landed. The
    // engine return is enqueue-accepted, not moved-confirmed (AcMainThreadQueue
    // gesture-defers a move up to ~250 ticks ≈ 4-8s), so success and failure are
    // indistinguishable at the call site. We confirm on a LATER snapshot.
    private sealed class Pending { public DateTime EnqueuedAt; public int FromContainer; }
    private sealed class Backoff { public DateTime NextRetry; public int Attempts; public DateTime LastTouched; }
    private readonly Dictionary<int, Pending> _cramPending = new();        // item id -> in-flight move
    private readonly Dictionary<int, Backoff> _cramBackoff = new();        // item id -> confirmed-fail backoff
    private readonly Dictionary<string, Pending> _stackPending = new();    // "tgt_src" -> in-flight merge
    private readonly Dictionary<string, Backoff> _stackBackoff = new();    // "tgt_src" -> confirmed-fail backoff
    private DateTime _lastBackoffPrune = DateTime.MinValue;

    // Grace window for confirm-via-snapshot. MUST be strictly larger than the
    // AcMainThreadQueue max gesture-defer (DrainDeferTickCap=250 ticks ≈ 4-8s at the
    // 30-63 Hz tick rate). A shorter window mis-flags in-flight moves as failures and
    // falsely backs them off. 10s gives clear headroom above the 8s worst case.
    private const double MoveConfirmGraceMs = 10000.0;
    // Per-entry backoff: exponential from a base, capped. A genuinely-unmovable item
    // backs off instead of retrying every 30s.
    private const double BackoffBaseMs = 5000.0;     // first retry after a confirmed fail
    private const double BackoffMaxMs  = 300000.0;   // cap at 5 min
    private const int    MaxAttemptsBudget = 6;      // after this, park far out (still age-pruned)
    // Age-prune cadence + max idle age (replaces the wholesale clear as the memory bound).
    private const double BackoffPruneIntervalMs = 60000.0;
    private const double BackoffMaxIdleMs = 600000.0;

    // AC item-type flags we care about for cram filtering
    private const uint ItemTypeContainer = 0x00000200;

    public InventoryManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache cache)
    {
        _host = host;
        _settings = settings;
        _cache = cache;
    }

    // Kept as a no-op for call-site compatibility. Scans are now driven by
    // the backoff timer below — we re-scan every 2 seconds when idle, every
    // 500 ms after an action, so external "dirty" signals aren't needed.
    public void MarkDirty() { }

    public void OnHeartbeat(int busyCount)
    {
        if (!_settings.IsMacroRunning) return;
        if (busyCount != 0) return; // don't move items while the client is busy (casting, crafting, etc.)

        // Age-prune the per-entry backoff/pending maps (replaces the old wholesale
        // 30s clear, which was also the de-facto memory bound). Dead ids / pair-keys
        // that haven't been touched in BackoffMaxIdleMs are dropped so they don't
        // accumulate across a multi-hour session; live backoff state persists so a
        // genuinely-unmovable item stays backed off.
        if ((DateTime.Now - _lastBackoffPrune).TotalMilliseconds >= BackoffPruneIntervalMs)
        {
            PruneBackoff();
            _lastBackoffPrune = DateTime.Now;
        }

        if (DateTime.Now < _nextInventoryActionTime) return;

        // Snapshot inventory once per tick — both passes use the same list.
        var inv = _cache.GetDirectInventory(forceRefresh: true).ToList();
        uint playerId = _host.GetPlayerId();
        if (playerId == 0 || inv.Count == 0) return;

        // AutoCram runs before AutoStack — clearing space in the main pack is
        // higher priority than tidying partials, and the two passes can starve
        // each other if AutoStack keeps finding new partial pairs every tick.
        if (ProcessAutoCram(inv, (int)playerId))
        {
            _nextInventoryActionTime = DateTime.Now.AddMilliseconds(500);
            return;
        }

        if (ProcessAutoStack(inv, (int)playerId))
        {
            _nextInventoryActionTime = DateTime.Now.AddMilliseconds(500);
            return;
        }

        // Nothing to do — back off re-scan for a bit.
        _nextInventoryActionTime = DateTime.Now.AddMilliseconds(2000);
    }

    // ── AutoCram ──────────────────────────────────────────────────────────

    private bool ProcessAutoCram(List<WorldObject> inv, int playerId)
    {
        if (!_settings.EnableAutocram) return false;

        int crammable = 0;
        foreach (var item in inv)
        {
            if (item.Container != playerId) continue;             // only main pack
            if (item.Values(LongValueKey.EquippedSlots, 0) != 0) continue; // not worn/wielded
            if (item.ObjectClass == AcObjectClass.Container) continue; // don't cram containers
            // Confirm-via-snapshot: if a prior cram of this item is still in flight,
            // check whether it actually moved out of the main pack. Moved => handled
            // (drop pending). Still here past the grace window => CONFIRMED failure
            // => start/advance per-entry backoff. (P1b)
            if (_cramPending.TryGetValue(item.Id, out var cp))
            {
                if (item.Container != playerId)
                {
                    _cramPending.Remove(item.Id);   // moved — success confirmed
                    _cramBackoff.Remove(item.Id);
                    continue;
                }
                if ((DateTime.Now - cp.EnqueuedAt).TotalMilliseconds < MoveConfirmGraceMs)
                    continue;                       // still in flight — don't re-enqueue or blacklist
                _cramPending.Remove(item.Id);
                RegisterFailure(_cramBackoff, item.Id);
                _host.Log($"[RynthAi] AutoCram: CONFIRMED move failure id=0x{item.Id:X8} (still in main pack after {MoveConfirmGraceMs:0}ms) — backing off");
                continue;
            }
            // Backed-off after a confirmed failure: skip until NextRetry.
            if (_cramBackoff.TryGetValue(item.Id, out var cb) && DateTime.Now < cb.NextRetry)
                continue;
            // Skip items the user has registered in the RynthAi Weapons tab —
            // those are weapons they want to keep accessible at the top level.
            if (_settings.ItemRules.Any(r => r.Id == item.Id)) continue;

            // Name-based foci guard — focis are also classified as containers above,
            // but catch named edge cases where itemType isn't set yet.
            if (!string.IsNullOrEmpty(item.Name)
                && item.Name.IndexOf("Foci", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            // Capacity check — if this item has its own item slots, treat it as a pack.
            if (item.Values(LongValueKey.ItemsCapacity, 0) > 0) continue;
            if (item.Values(LongValueKey.ContainersCapacity, 0) > 0) continue;

            crammable++;

            int targetPack = WorldObjectCache.FindPackFor(inv, playerId, includeMainPack: false, requireFree: 2);
            if (targetPack == 0)
            {
                // Overflow tolerance (P0, 2026-06-24): no SUB-pack has room. The item is
                // ALREADY loose in the main pack (loop precondition: item.Container ==
                // playerId), so there is nothing to strand — moving it into the main pack
                // would be a useless self-move. Only treat this as a genuine "inventory
                // full" condition when the main pack ITSELF is full. Reuse the proven
                // 102-mainUsed calc (RynthAiPlugin.cs:594-604 / BuffManager.cs:2156-2188).
                int mainUsed = 0;
                foreach (var m in inv)
                {
                    if (m.Container != playerId) continue;
                    if (m.ObjectClass == AcObjectClass.Container) continue;
                    if (m.ObjectClass == AcObjectClass.Foci) continue;
                    if (m.Values(LongValueKey.EquippedSlots, 0) != 0) continue;
                    mainUsed++;
                }
                int mainFree = 102 - mainUsed;
                if (mainFree > 0)
                    _host.Log($"[RynthAi] AutoCram: {crammable} crammable items but no open sub-pack; main pack has room (mainFree={mainFree}) — leaving items in main pack");
                else
                    _host.Log($"[RynthAi] AutoCram: {crammable} crammable items and inventory genuinely full (no sub-pack room, mainUsed={mainUsed}/102)");
                break; // no open SUB-pack — stop scanning this tick (engine gate makes any future move fail-closed)
            }

            int amount = item.Values(LongValueKey.StackCount, 1);
            if (amount < 1) amount = 1;

            _host.Log($"[RynthAi] AutoCram: move {item.Name} id=0x{item.Id:X8} -> pack=0x{(uint)targetPack:X8} slot=0 amount={amount}");
            bool ok = _host.MoveItemInternal(
                unchecked((uint)item.Id),
                unchecked((uint)targetPack),
                slot: 0,
                amount: amount);

            if (!ok)
            {
                // Enqueue itself was rejected (engine guard, e.g. amount<=0 or the P0
                // full-owned-container gate) — that IS a confirmed failure now.
                RegisterFailure(_cramBackoff, item.Id);
                _host.Log($"[RynthAi] AutoCram: MoveItemInternal REJECTED id=0x{item.Id:X8} pack=0x{(uint)targetPack:X8} amount={amount} — backing off");
                return true; // one action per tick
            }

            // Enqueue accepted — record as PENDING and confirm on a later snapshot (do
            // NOT blacklist; success and a deferred-but-pending move look identical here).
            _cramPending[item.Id] = new Pending { EnqueuedAt = DateTime.Now, FromContainer = playerId };
            return true; // one action per tick
        }

        return false;
    }

    // FindOpenPack DELETED (P2a) — replaced by the shared
    // WorldObjectCache.FindPackFor(host, inv, playerId, includeMainPack:false, requireFree:2).
    // AutoCram uses includeMainPack:FALSE (cram empties the main pack — must NOT target
    // it or it spins) and requireFree:2 (anti-churn margin so a single mis-count can't
    // tip a near-full sub-pack). It reuses AutoCram's single per-tick `inv` snapshot via
    // the snapshot overload (no extra BFS / no double-refresh).

    // ── AutoStack ─────────────────────────────────────────────────────────

    private bool ProcessAutoStack(List<WorldObject> inv, int playerId)
    {
        if (!_settings.EnableAutostack) return false;

        // Build candidate list — items belonging to the player (directly or in a sub-pack).
        var playerItems = new List<WorldObject>();
        foreach (var item in inv)
        {
            bool inPlayerScope;
            if (item.Container == playerId) inPlayerScope = true;
            else
            {
                var container = inv.FirstOrDefault(c => c.Id == item.Container);
                inPlayerScope = container != null && container.Container == playerId;
            }
            if (inPlayerScope) playerItems.Add(item);
        }

        // Group by name, keep only stackable items with >1 in the same group.
        var groupList = playerItems
            .Where(x => GetMaxStackSize(x) > 1)
            .GroupBy(x => x.Name)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var g in groupList)
        {
            int max = GetMaxStackSize(g.First());

            // Read raw stack counts. Sentinel -999 distinguishes "InqInt failed" from
            // "real stack of 1" (a freshly-looted single doesn't have STACK_SIZE in the
            // qualities table because 1 is the implicit default).
            var withCounts = g
                .Select(x => (Item: x, Raw: x.Values(LongValueKey.StackCount, -999)))
                .ToList();

            // For merging, treat InqInt failure as a stack of 1 (legitimate single stack).
            // Partials = anything below the cap. Sorted descending so we walk from
            // largest target down to find one that fits with the smallest source.
            var parts = withCounts
                .Select(t => (t.Item, Count: t.Raw == -999 ? 1 : t.Raw))
                .Where(t => t.Count > 0 && t.Count < max)
                .OrderByDescending(t => t.Count)
                .ToList();

            if (parts.Count < 2) continue;

            // Drain ALL partials of this group across ticks (P2b): walk candidate
            // target/source pairs (largest target, smallest viable source) and pick
            // the first pair NOT pending and NOT backed-off. The old code only ever
            // tried parts[0]+parts[last] and then BLOCKED that exact pair on success,
            // starving multi-partial groups (tapers/scarabs) of consolidation.
            (WorldObject Item, int Count)? chosenTarget = null;
            (WorldObject Item, int Count)? chosenSource = null;
            string? pairKey = null;
            for (int ti = 0; ti < parts.Count && chosenTarget == null; ti++)
            {
                for (int si = parts.Count - 1; si > ti; si--)
                {
                    string key = parts[ti].Item.Id + "_" + parts[si].Item.Id;
                    // Confirm-via-snapshot: a prior merge of this pair either consumed
                    // the source (id vanished from inv) or grew the target. Either =>
                    // success; drop pending. Source gone counts as success, NOT an
                    // unreadable failure.
                    if (_stackPending.TryGetValue(key, out var sp))
                    {
                        bool sourceGone = !inv.Any(x => x.Id == parts[si].Item.Id);
                        if (sourceGone)
                        {
                            _stackPending.Remove(key);
                            _stackBackoff.Remove(key);
                            continue; // this pair resolved — try another
                        }
                        if ((DateTime.Now - sp.EnqueuedAt).TotalMilliseconds < MoveConfirmGraceMs)
                            continue; // still in flight — skip this pair this tick
                        _stackPending.Remove(key);
                        RegisterFailure(_stackBackoff, key);
                        _host.Log($"[RynthAi] AutoStack: CONFIRMED merge failure {key} (source survived after {MoveConfirmGraceMs:0}ms) — backing off");
                        continue;
                    }
                    if (_stackBackoff.TryGetValue(key, out var sb) && DateTime.Now < sb.NextRetry)
                        continue; // backed off — try another pair
                    chosenTarget = parts[ti];
                    chosenSource = parts[si];
                    pairKey = key;
                    break;
                }
            }
            if (chosenTarget == null || chosenSource == null || pairKey == null)
                continue; // every pair in this group is pending/backed-off — next group

            var target = chosenTarget.Value;
            var source = chosenSource.Value;

            _host.Log($"[RynthAi] AutoStack: merge {source.Item.Name} src=0x{source.Item.Id:X8}({source.Count}) -> tgt=0x{target.Item.Id:X8}({target.Count}) max={max}");
            bool ok = _host.MergeStackInternal(
                unchecked((uint)source.Item.Id),
                unchecked((uint)target.Item.Id));

            if (!ok)
            {
                RegisterFailure(_stackBackoff, pairKey);
                _host.Log($"[RynthAi] AutoStack: MergeStackInternal REJECTED src=0x{source.Item.Id:X8} tgt=0x{target.Item.Id:X8} — backing off");
                return true; // one action per tick
            }

            // Enqueue accepted — record PENDING, confirm on a later snapshot. Do NOT
            // add the pair-key on success (the old bug that poisoned good merges and
            // starved multi-partial consolidation).
            _stackPending[pairKey] = new Pending { EnqueuedAt = DateTime.Now, FromContainer = playerId };
            return true; // one action per tick
        }

        return false;
    }

    // ── Per-entry backoff helpers (P2b) ───────────────────────────────────
    // Advance exponential backoff for a confirmed failure. Generic over the key type
    // so cram (int id) and stack (string pair-key) share one implementation.
    private static void RegisterFailure<TKey>(Dictionary<TKey, Backoff> map, TKey key) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var b))
        {
            b = new Backoff { Attempts = 0 };
            map[key] = b;
        }
        b.Attempts++;
        b.LastTouched = DateTime.Now;
        int shift = b.Attempts - 1;
        if (shift > 6) shift = 6;                       // cap the shift so the multiply can't overflow
        double delay = BackoffBaseMs * (1 << shift);    // 5s,10s,20s,40s,80s,160s,320s→capped
        if (delay > BackoffMaxMs) delay = BackoffMaxMs;
        // Past the attempt budget, park far out (still age-pruned) instead of looping.
        if (b.Attempts >= MaxAttemptsBudget) delay = BackoffMaxMs;
        b.NextRetry = DateTime.Now.AddMilliseconds(delay);
    }

    // Age-prune dead/idle entries so the maps don't grow unbounded across a long
    // session (the wholesale clear used to be the de-facto bound).
    private void PruneBackoff()
    {
        DateTime now = DateTime.Now;
        PruneMap(_cramBackoff, b => b.LastTouched, now);
        PruneMap(_stackBackoff, b => b.LastTouched, now);
        PruneMap(_cramPending, p => p.EnqueuedAt, now);
        PruneMap(_stackPending, p => p.EnqueuedAt, now);
    }

    private static void PruneMap<TKey, TVal>(Dictionary<TKey, TVal> map, Func<TVal, DateTime> stamp, DateTime now)
        where TKey : notnull
    {
        List<TKey>? dead = null;
        foreach (var kv in map)
            if ((now - stamp(kv.Value)).TotalMilliseconds > BackoffMaxIdleMs)
                (dead ??= new()).Add(kv.Key);
        if (dead != null)
            foreach (var k in dead) map.Remove(k);
    }

    // ── Stack-size lookup ─────────────────────────────────────────────────

    private static int GetMaxStackSize(WorldObject item)
    {
        int max = item.Values(LongValueKey.MaxStackSize, 0);
        if (max > 1) return max;

        // Name-based fallbacks for items whose quality property didn't resolve yet.
        string n = item.Name;
        if (string.IsNullOrEmpty(n)) return 1;

        if (n.Contains("Pyreal", StringComparison.OrdinalIgnoreCase)) return 25000;
        if (n.Contains("Trade Note", StringComparison.OrdinalIgnoreCase)) return 100;
        if (n.Contains("Taper", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Scarab", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Prismatic", StringComparison.OrdinalIgnoreCase)) return 100;
        if (n.Contains("Arrow", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Bolt", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Quarrel", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Atlatl Dart", StringComparison.OrdinalIgnoreCase)) return 250;
        if (n.Contains("Pea", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Grain", StringComparison.OrdinalIgnoreCase)) return 100;

        // Salvage bags are intentionally NOT here — each bag is a unique item
        // with quality props; they do not stack.

        return 1;
    }
}

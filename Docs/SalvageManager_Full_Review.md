# SalvageManager Full Review

**Reviewer:** Claude (Opus 4.7)
**Date:** May 2026
**Scope:** `Plugins/RynthCore.Plugin.RynthAi/Loot/SalvageManager.cs` (655 LOC), end to end. This supersedes the combine-focused review by covering the whole file with the same depth as the CombatManager review.

The previous pass was scoped to the bag-combining problem. This is the full-file review — single-item flow, `FindUst`, `IsCarriedByPlayer`, the dual-enum state machine, the helpers, settings/observability, and everything in between. The combine-specific findings still apply; I'll briefly cross-reference them where relevant.

---

## TL;DR — three categories

**Category 1: Real bugs.**
- `IsCarriedByPlayer` line 607 returns `true` for items with `Container == 0`. Items on the ground have `Container == 0`. So a UST sitting in your view on the ground passes the "is this carried by the player" check. **Real bug, easy fix.**
- `IsUst` line 622 is a substring match on `"Ust"` — it matches "Just", "Lustful", "Sustained", etc. Loose enough that on most servers it's fine in practice but it's not robust.
- `RequeueOrDrop` is bypassed in two re-queue paths (lines 187 and 205) — those use `_queue.Enqueue` directly, so failures can loop indefinitely without hitting the retry cap.
- The combine flow has zero verification (covered in prior review §1; still the headline issue).

**Category 2: Architectural smells that will become bugs.**
- Two parallel enum state machines (`Phase` and `CombinePhase`) running concurrently; the bridge state is `_phase = Phase.CombiningSalvage`. Same dual-system pattern as the CombatManager blacklists.
- No top-level watchdog. If `_phase` wedges in `WaitingForResult` because of an exception in the handler, the manager is permanently dead until plugin restart.
- `_panelEverOpened` is sticky-true for the session and can drift from AC's actual panel state.
- `_firstResultCycle` is set once at startup, set to false after the first cycle, and never reset. So the longer first-cycle delay only ever applies to the literal first salvage of the session.

**Category 3: Inefficiency and observability gaps.**
- `FindUst` does a 5-pass cascade where the first two passes walk `AllKnownObjects` (potentially hundreds of items) before getting to the small inventory walk. Should be inverted.
- `FindUst` is called fresh at the start of every combine group (line 439) instead of being cached for the duration of the combine cycle.
- No `GetStateSnapshot()` or metrics surface — users have no visibility into queue depth, retries, sweep activity, etc.

Most of this is small. But the volume adds up — about a dozen distinct issues that are individually 5-30 minutes to fix.

---

## 1. What the manager does well (keep these)

To set context for what's worth changing, here's what's clearly right:

- **The phase-based design.** State machine with explicit phases (`Idle → OpeningPanel → AddingItem → Salvaging → WaitingForResult`) is the correct shape. Each phase has a deadline (`_phaseReadyAt`) and OnTick just dispatches based on time. Clean.
- **Per-item retry counting** (`_itemRetryCount` + `MaxItemRetries`). Failed items get re-queued up to 3 times before being dropped. Prevents infinite loops on un-salvageable items.
- **Per-item result verification.** `OnResultReady` (line 314-341) checks whether the item is still in the cache after the result delay. If yes, salvage didn't actually happen → re-queue. This is exactly the right pattern. The combine flow needs to copy it.
- **The hookless approach.** `UseObject(ustId)` mimics a double-click rather than relying on a network packet — comment at line 181-183 explains why this is more reliable than `SendNotice_OpenSalvagePanel`. Smart adaptation to the client's actual behavior.
- **Combine-during-salvage.** `AddAllMatchingUnderFullBags` (line 244) merges existing bags into the current salvage panel before Execute. Far better than a separate panel cycle for each merge. Pattern is right; just needs verification (per prior review).
- **The diagnostic on UST miss.** Line 580-590 dumps inventory contents when the UST search fails. This is exactly the kind of "tell the user what we saw" that's missing from BuffManager and other places. Keep this pattern.
- **WCID as the primary UST identifier.** WCIDs are stable across servers and characters; name-based identification isn't. Comment at line 521-524 articulates this correctly. Keep the WCID-first approach (just reorder the cascade — see §5).

OK, now the issues.

---

## 2. Real bugs

### 2.1 `IsCarriedByPlayer` returns true for items on the ground

`SalvageManager.cs:607`:

```csharp
private bool IsCarriedByPlayer(WorldObject item)
{
    if (_cache == null) return false;
    uint playerId = _host.GetPlayerId();
    if (playerId == 0) return true;                                          // ← issue 2.1.1

    int pid = unchecked((int)playerId);
    if (item.Wielder != 0 && item.Wielder != pid) return false;
    if (item.Container == 0 || item.Container == pid) return true;           // ← issue 2.1.2
    // ...
}
```

Items on the ground have `Container == 0` and `Wielder == 0`. The first check passes (Wielder is 0, so the `Wielder != 0 && Wielder != pid` is false → no early return). The second check has `Container == 0 → return true`. So a UST on the ground is reported as "carried by player" and `FindUst` will happily use it. The `UseObject(ustId)` call may even succeed because AC lets you use objects in view, leading to bizarre behavior where the bot opens a UST it doesn't actually own.

This is rare in practice (USTs aren't usually dropped on the ground in players' view) but it's wrong. The correct logic:

```csharp
private bool IsCarriedByPlayer(WorldObject item)
{
    if (_cache == null) return false;
    uint playerId = _host.GetPlayerId();
    if (playerId == 0) return false;  // ← changed from `return true` (issue 2.1.1)

    int pid = unchecked((int)playerId);

    // Wielded by another player → not ours
    if (item.Wielder != 0 && item.Wielder != pid) return false;

    // No container at all → on the ground or in-hand of a creature → not in pack
    if (item.Container == 0)
    {
        // Edge case: items the player is wielding (Wielder == pid) have Container == 0
        // but ARE on the player. The earlier wielder check already returned for non-pid
        // wielders, so this catches the "wielded by player" case.
        return item.Wielder == pid;
    }

    if (item.Container == pid) return true;

    // Walk up the container chain (covers nested side-packs)
    int currentContainer = item.Container;
    int guard = 0;
    while (currentContainer != 0 && guard++ < 10)
    {
        if (currentContainer == pid) return true;
        var owner = _cache[currentContainer];
        if (owner == null) return false;
        if (owner.ObjectClass == AcObjectClass.Corpse) return false;
        if (owner.Wielder == pid) return true;
        currentContainer = owner.Container;
    }
    return false;
}
```

Two changes:
- `playerId == 0 → return false` (was `true`). Defensive default should be "not ours" if we don't know who we are.
- `Container == 0` only counts as "carried" if the player is the wielder.
- Container walk is now recursive (with a guard) instead of one level — handles nested side-packs.

### 2.2 `IsUst` substring match is too loose

Line 622:

```csharp
return name.Contains("Ust", StringComparison.OrdinalIgnoreCase)
    && !IsSalvageBag(name);
```

`"Ust"` matches:
- `"Just a thing"`
- `"Sustained Magic"`
- `"Lustful Pulse"` (a real spell name)
- `"Adjustable Pouch"` (hypothetical but plausible)

The exclusion for salvage bags only catches names containing `"Salvage ("`. Anything else with "ust" in it slips through.

Real UST names (from comment at line 619-620): `"Salvaging Ust"`, `"Aged Legendary Salvaging Ust"`, `"Sturdy Iron Salvaging Ust"`. They all **end in "Ust"** (case-sensitive). The right check is end-with rather than contains:

```csharp
private static bool IsUst(string? name)
{
    if (string.IsNullOrEmpty(name)) return false;
    // UST names always end in "Ust" — typically " Ust" or "Salvaging Ust".
    // Use OrdinalIgnoreCase EndsWith so case variations don't slip through.
    return name.EndsWith("Ust", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Ust ", StringComparison.OrdinalIgnoreCase);  // trailing-space defense
}
```

The salvage-bag exclusion isn't needed because no salvage bag name ends in "Ust" (they end in `")"`).

### 2.3 Two re-queue paths bypass `RequeueOrDrop`

There's a `RequeueOrDrop` helper specifically for tracking retries with a 3-strikes cap. But two paths bypass it:

**Line 187** (UseObject failure):
```csharp
if (!_host.UseObject(_currentUstId))
{
    Log($"[Salvage] UseObject(UST) failed for 0x{_currentUstId:X8} — re-queuing item.");
    _queue.Enqueue(itemId); // re-queue so we retry next tick
    // ...
}
```

**Line 205** (SalvagePanelAddItem failure):
```csharp
if (!_host.SalvagePanelAddItem(_currentItemId))
{
    Log($"[Salvage] SalvagePanelAddItem failed for 0x{_currentItemId:X8} (panel instance not ready — re-queuing).");
    _queue.Enqueue(_currentItemId);
    // ...
}
```

Both use `_queue.Enqueue` directly. If the same failure mode keeps happening for the same item — say UST is in a side-pack the cache can't track properly — the item loops forever. The retry counter never increments because these paths skip it.

**Fix:** route both through `RequeueOrDrop`:

```csharp
if (!_host.UseObject(_currentUstId))
{
    Log($"[Salvage] UseObject(UST) failed for 0x{_currentUstId:X8} — re-queuing item.");
    RequeueOrDrop(itemId, "UseObject failed");
    _currentItemId = 0;
    _currentUstId = 0;
    return;
}
```

(And similarly for the AddItem failure path.) This is two 3-line changes. Eliminates a real infinite-loop risk.

### 2.4 `_pendingCombineScan` path doesn't check `busyCount`

`TickIdle` line 138-159:

```csharp
if (_queue.Count == 0)
{
    if (_pendingCombineScan && _settings.EnableCombineSalvage)
    {
        _pendingCombineScan = false;
        _lastCombineSweepAt = now;
        BeginCombineSalvage(now);                  // ← fires regardless of busyCount
        return;
    }
    if (_settings.EnableCombineSalvage
        && busyCount == 0                          // ← gate exists here
        && now - _lastCombineSweepAt >= CombineSweepIntervalMs)
    {
        // ...
    }
    return;
}
```

The 30-second time-based sweep correctly gates on `busyCount == 0`. The post-drain sweep (`_pendingCombineScan = true`) skips the busy check. So a combine cycle can start while the game is busy with another action — opening a salvage panel mid-cast, mid-loot, mid-portal-action.

In practice this is masked because `_pendingCombineScan` only fires after `OnResultReady` (line 339-340), which itself only runs after the salvage cycle's full delay sequence — busy is usually 0 by then. But "usually" isn't always. The gate should be consistent:

```csharp
if (_queue.Count == 0)
{
    if (_settings.EnableCombineSalvage && busyCount == 0)
    {
        if (_pendingCombineScan
            || now - _lastCombineSweepAt >= CombineSweepIntervalMs)
        {
            _pendingCombineScan = false;
            _lastCombineSweepAt = now;
            BeginCombineSalvage(now);
        }
    }
    return;
}
```

Single decision, single gate.

### 2.5 No-UST silent drop spams chat for queued items

Line 170-175:

```csharp
uint ustId = FindUst();
if (ustId == 0)
{
    _host.WriteToChat("[RynthAi] Salvage: No UST found in inventory — item skipped. Add a Ust to your pack.", 2);
    Log($"[Salvage] No UST found in inventory — skipping 0x{itemId:X8}.");
    return;
}
```

The item was already dequeued at line 166. The function returns without doing anything else — the item is **silently dropped from the queue** (not re-queued, no retry tracking). If 10 items are in the queue when the UST runs out (gets full and has been removed, or was used up), the user gets 10 chat warnings in a row, one per dequeue, and all 10 items are lost.

Should:
1. Re-queue (or at least re-queue once and track) so the item isn't lost
2. Stop processing the queue entirely until a UST is available — the next 10 items will hit the same wall

```csharp
uint ustId = FindUst();
if (ustId == 0)
{
    // Don't dequeue more items if we have no UST. Push the dequeued one back.
    _queue.Enqueue(itemId);
    if (!_warnedNoUst || (now - _lastNoUstWarnAt) > 30_000)
    {
        _host.WriteToChat($"[RynthAi] Salvage: No UST in inventory — pausing salvage queue ({_queue.Count} item(s) waiting).", 2);
        _warnedNoUst = true;
        _lastNoUstWarnAt = now;
    }
    return;
}
_warnedNoUst = false;
```

(And check Wait — this re-queues at the END of the queue, so the same item gets reprocessed eventually when more items are added in front. To avoid this, you'd want a `Deque.PushFront` or a separate "blocked items" set. The simpler interim is just enqueuing back and accepting that ordering may shift slightly during a no-UST window.)

This wasn't called out as a user-reported bug but it's a real footgun for anyone whose UST runs out mid-session.

### 2.6 `SalvagePanelExecute` failure in combine flow is silently absorbed

`TickCombiningSalvage` line 488-489:

```csharp
case CombinePhase.Salvaging:
{
    if (!_host.SalvagePanelExecute())
        Log("[Salvage] Combine: SalvagePanelExecute failed.");
    _combinePhase = CombinePhase.WaitingForResult;
    _combinePhaseReadyAt = now + _settings.SalvageSalvageDelayMs + _settings.SalvageResultDelayFastMs;
    break;
}
```

If Execute fails (panel not ready, etc.), we just log and proceed to WaitingForResult anyway. The result-wait fires, increments `_combineGroupIdx`, and we move to the next group. No retry. No "this group failed entirely" tracking.

Compare to the single-item path at line 289-302 which calls `RequeueOrDrop` on Execute failure. The combine path has no analog.

**Fix:** treat Execute failure as "this whole group failed" and skip but log explicitly:

```csharp
case CombinePhase.Salvaging:
{
    if (!_host.SalvagePanelExecute())
    {
        Log($"[Salvage] Combine group {_combineGroupIdx}: SalvagePanelExecute failed — skipping group.");
        _combineGroupIdx++;
        _combineAddIdx = 0;
        _combinePhase = CombinePhase.None;
        _combinePhaseReadyAt = now;
        // Note: panel may now be in a weird state. Reset _panelEverOpened so the next group's open uses long delays.
        _panelEverOpened = false;
        break;
    }
    _combinePhase = CombinePhase.WaitingForResult;
    _combinePhaseReadyAt = now + _settings.SalvageSalvageDelayMs + _settings.SalvageResultDelayFastMs;
    break;
}
```

---

## 3. State machine architecture

### 3.1 Two enums for one job

There's `Phase` (for single-item) and `CombinePhase` (for combine). The bridge is `_phase = Phase.CombiningSalvage`, after which `TickCombiningSalvage` switches on `_combinePhase`.

This is structurally identical to the dual-blacklist problem in CombatManager. The two state machines:

**`Phase`:** Idle, OpeningPanel, AddingItem, Salvaging, WaitingForResult, **CombiningSalvage**
**`CombinePhase`:** None, OpeningPanel, AddingBags, Salvaging, WaitingForResult

The states `OpeningPanel`, `Salvaging`, and `WaitingForResult` exist in both. The only real difference is "AddingItem" (single) vs "AddingBags" (multiple). A unified state machine handles both:

```csharp
private enum SalvagePhase
{
    Idle,
    OpeningPanel,
    AddingItems,        // unified — handles 1 or N items
    Salvaging,
    WaitingForResult,
}

private uint[] _currentBatch = Array.Empty<uint>();   // items to add this cycle
private int _batchAddIdx;                              // current item being added
private SalvageBatchKind _batchKind;                   // SingleItem | CombineGroup
```

`SalvageBatchKind` lets the WaitingForResult handler do the right verification (single-item check for SingleItem; multi-bag count diff for CombineGroup). Single Execute path. Single result-wait path. The single-item flow becomes "batch of 1, kind=SingleItem"; the combine flow becomes "batch of N, kind=CombineGroup".

I noted this in the prior review (§4) but it's worth reinforcing here — the dual-enum design is a smell beyond just the verification gap. Estimated effort to unify: 4 hours including testing.

### 3.2 No top-level watchdog

`NavigationEngine` has a stuck-watchdog (5s window, jump-recovery). `CombatManager` has `TARGET_SCAN_GRACE_MS` and `TargetNoProgressTimeoutSec`. SalvageManager has nothing equivalent — no "if we've been in phase X for > 30s, force-reset to Idle."

Failure modes that get permanently stuck:
- `Phase.WaitingForResult` if `OnResultReady` throws an exception (the function isn't wrapped in try/catch).
- `Phase.AddingItem` if `_phaseReadyAt` somehow gets set to a future time that never elapses (e.g., system clock jumps).
- `Phase.CombiningSalvage` with `_combineGroups != null` and `_combineGroupIdx >= _combineGroups.Count` — actually that's handled by line 424-430.

The watchdog should be at the top of `OnTick`:

```csharp
public void OnTick(int busyCount)
{
    if (!_host.HasSalvagePanel || !_host.HasUseObject) return;

    long now = NowMs;

    // Top-level watchdog: if we've been in a non-Idle phase for >30s, reset.
    if (_phase != Phase.Idle)
    {
        if (_phaseEnteredAt == 0) _phaseEnteredAt = now;
        if (now - _phaseEnteredAt > 30_000)
        {
            Log($"[Salvage] Phase wedged ({_phase}) for >30s — resetting.");
            ForceResetState();
            _phaseEnteredAt = 0;
        }
    }
    else
    {
        _phaseEnteredAt = 0;
    }

    // ... existing dispatch
}
```

`_phaseEnteredAt` is set whenever phase transitions from Idle → non-Idle. `ForceResetState()` clears `_currentItemId`, `_combineGroups`, etc. If a real bug locks the state machine, the user loses 30s of salvage time but doesn't have to restart.

### 3.3 `_panelEverOpened` is sticky and can drift

Set to `true` in `BeginAddingItem` line 221 and `CombinePhase.OpeningPanel` line 461. Reset to `false` only in `BeginAddingItem`'s failure path line 208.

That's once true for the session, almost always. The intent was to skip the long first-open delays after the panel has been opened once. But:

- If the user manually closes the salvage panel mid-cycle, `_panelEverOpened` stays true. The next salvage cycle uses fast delays (50ms), but the panel needs to re-animate-open (might not be ready in 50ms).
- If AC auto-closes the panel between groups in a combine cycle, same issue.
- If the player opens then closes then re-opens during a long wait state, our state diverges from reality.

The right fix is to track *recency* of panel opening, not just "ever":

```csharp
private long _lastPanelOpenAt;
private const long PanelStillOpenWindowMs = 5_000;  // panel considered open if used within last 5s

private bool IsPanelLikelyOpen() =>
    _lastPanelOpenAt != 0 && (NowMs - _lastPanelOpenAt) < PanelStillOpenWindowMs;
```

And use this to choose between the fast and first-open delays:

```csharp
int openDelay = IsPanelLikelyOpen() ? _settings.SalvageOpenDelayFastMs : _settings.SalvageOpenDelayFirstMs;
```

`_lastPanelOpenAt` is updated whenever an Execute completes (panel may auto-close), whenever UseObject fires, etc. It's a heuristic but a better one than "ever opened in this session."

### 3.4 `_firstResultCycle` only ever applies to literally the first salvage of the session

`SalvageManager.cs:36` declares `_firstResultCycle = true`. It's set to `false` in `BeginWaitingForResult` line 309. **Never set back to true.**

So the longer first-cycle result delay (`SalvageResultDelayFirstMs = 1000ms`) only applies to the very first salvage of the entire plugin session. The 999th salvage uses the fast 250ms delay even if there's been a lull where the panel state may have decayed.

The semantic is wrong. "First result cycle" was probably meant to capture "first cycle after a panel-open burst," but it's tracking session-lifetime instead. Either:
- Reset to true when the panel hasn't been used in a while (paired with `_lastPanelOpenAt` from §3.3), or
- Just remove it — the fast delay is probably fine for everything.

I'd lean toward removing it. The 250ms fast delay is enough; the 1000ms initial delay was probably overkill and has just been kept around. Easy A/B test: try with `SalvageResultDelayFastMs = 1000` always and see if anything regresses.

---

## 4. `FindUst` cascade is inverted

The current 5-pass cascade is logically:

1. WCID over `AllKnownObjects` + IsCarriedByPlayer
2. ObjectClass over `AllKnownObjects` + IsCarriedByPlayer
3. Name over `GetDirectInventory(forceRefresh: false)`
4. Name over `GetDirectInventory(forceRefresh: true)`
5. WCID over the freshly-walked inventory

Two problems:

**4.1: Wrong order.** A UST must be in the player's pack to be useful. The cheapest, smallest set is `GetDirectInventory()` — bounded by the player's actual inventory size (~50-100 items max). `AllKnownObjects` is potentially in the thousands on a busy spawn.

**4.2: Redundant work.** Pass 1 and Pass 5 are functionally identical — both look for WCID matches. Pass 1 walks AllKnownObjects with a per-item filter; Pass 5 walks DirectInventory after a forced refresh. The case where Pass 5 succeeds but Pass 1 fails is "the cache hadn't refreshed yet" — a race we can fix by always forcing a refresh first.

**Restructure:**

```csharp
private uint FindUst()
{
    if (_cache == null) return 0;

    // The UST is in the player's pack by definition. Walk inventory only.
    // Force-refresh once at the top so the rest of the function is consistent.
    var inv = _cache.GetDirectInventory(forceRefresh: true);

    // 1. WCID match — most reliable, server-independent.
    if (_host.HasGetObjectWcid)
    {
        foreach (var item in inv)
        {
            if (_host.TryGetObjectWcid(unchecked((uint)item.Id), out uint wcid)
             && wcid == UstWcid)
                return unchecked((uint)item.Id);
        }
    }

    // 2. ObjectClass — needs cache classification.
    foreach (var item in inv)
        if (item.ObjectClass == AcObjectClass.Ust)
            return unchecked((uint)item.Id);

    // 3. Name fallback — works when neither WCID nor ItemType is available.
    foreach (var item in inv)
        if (IsUst(item.Name))
            return unchecked((uint)item.Id);

    LogUstMiss(inv);
    return 0;
}
```

5 passes → 3, no `AllKnownObjects` walks, no `IsCarriedByPlayer` overhead per pass.

**4.3: Cache between groups.** `TickCombiningSalvage` line 439 calls `FindUst()` at the start of every group:

```csharp
case CombinePhase.None:
{
    uint ust = FindUst();
    // ...
}
```

But the UST doesn't change between groups within a single combine cycle. Cache it once at the start of the cycle:

```csharp
// In BeginCombineSalvage, after groups are computed:
_combineCycleUstId = FindUst();
if (_combineCycleUstId == 0) { /* abort */ }
```

Then `TickCombiningSalvage` uses `_combineCycleUstId` instead of calling `FindUst()` per group. (If the UST somehow becomes invalid mid-cycle — got destroyed, dropped — that's an edge case we can detect by checking the cache for the cached id before each `UseObject`.)

---

## 5. Per-bag, per-item, and `WaitingForResult` issues

### 5.1 `OnResultReady`'s "still in cache" check is coarse

Line 322:

```csharp
bool itemStillPresent = itemId != 0 && _cache != null && _cache[unchecked((int)itemId)] != null;
```

This detects "salvage didn't happen." It doesn't detect:
- "Salvage happened but UST broke before consuming the item."
- "Salvage happened to the wrong item (panel had multiple items)."
- "Cache is stale — item was consumed but cooldown hasn't expired so cache still shows it."

The third one is concerning. `WorldObjectCache._directInventoryCooldownMs = 1000`. The salvage cycle's result delay is 250ms (fast) or 1000ms (first). On a fast cycle, the cache snapshot from before salvage is still valid — `_cache[itemId]` returns the pre-salvage entry, falsely indicating "still present." We re-queue an item that was actually consumed.

**Fix options:**
- Force a refresh before the check: `_cache.GetDirectInventory(forceRefresh: true)` then look up the item via the fresh result.
- Use the per-object delete tracking. `WorldObjectCache.OnDeleteObject` removes items from `_byId` — if that path runs synchronously when the server's delete arrives, the lookup is fresh.

Let me check the cache to see which is true:

(From `WorldObjectCache.cs:107-111` in the previous reviews — `OnDeleteObject` does immediate `_byId.Remove(sid)`. So `_cache[itemId]` IS fresh after a delete event arrives. But the delete event may not have arrived by the time the result delay fires — there's a server round-trip.)

So the right fix is **wait long enough for the delete event to arrive before checking**. The result delay (250ms fast) is probably enough on local servers but tight on high-latency servers.

Make the result delay user-tunable, or longer:

```csharp
public int SalvageResultDelayFastMs = 500;   // bump from 250 to give server time
```

Or add a "wait until cache reflects the salvage" loop:

```csharp
case Phase.WaitingForResult:
{
    if (now < _phaseReadyAt) break;

    // Don't trust the cache snapshot too soon — give the OnDeleteObject event time to arrive.
    if (_cache != null && _cache[unchecked((int)_currentItemId)] != null
        && now - _phaseReadyAt < 1500)  // wait up to 1.5s extra
    {
        // Item still in cache — wait one more cycle
        break;
    }

    OnResultReady(now);
    break;
}
```

This polls the cache for up to 1.5s after the initial result-ready moment, treating "item still present" as evidence that the delete event hasn't fired yet. Once the cache says deleted (or 1.5s elapses), proceed.

### 5.2 `_itemRetryCount.Remove(itemId)` order fragility

Line 321-336:
```csharp
uint itemId = _currentItemId;                    // capture local
bool itemStillPresent = ...;
_currentItemId = 0;                               // clear field
_phase = Phase.Idle;
if (itemStillPresent) { ... }
_itemRetryCount.Remove(itemId);                   // uses local
```

This works because `itemId` is captured before `_currentItemId` is cleared. But the visual proximity of `_currentItemId = 0` followed by `_itemRetryCount.Remove(itemId)` is suspicious — somebody refactoring might decide "let me use `_currentItemId` consistently" and break it.

Pull the captured local up to the top and rename to make it obvious:

```csharp
uint completedItemId = _currentItemId;
_currentItemId = 0;
_phase = Phase.Idle;

bool itemStillPresent = completedItemId != 0
    && _cache != null
    && _cache[unchecked((int)completedItemId)] != null;

if (itemStillPresent)
{
    Log($"[Salvage] Item 0x{completedItemId:X8} still in inventory — re-queuing.");
    RequeueOrDrop(completedItemId, "item still present after result");
    return;
}

_itemRetryCount.Remove(completedItemId);
if (_queue.Count == 0) _pendingCombineScan = true;
```

Clearer ownership — `completedItemId` is the local copy that survives `_currentItemId` being cleared.

### 5.3 Failed re-queues don't trigger combine sweep

Line 339-340:

```csharp
if (_queue.Count == 0)
    _pendingCombineScan = true;
```

This fires only when the queue is empty AND the salvage succeeded. If the salvage failed and triggered a `RequeueOrDrop`:
- If the item was re-queued, queue is non-empty → no scan trigger (correct — there's more work).
- If the item was DROPPED (3 strikes), queue may now be empty — but `RequeueOrDrop` doesn't set `_pendingCombineScan`.

Fix: have `RequeueOrDrop` set the flag when it drops:

```csharp
private void RequeueOrDrop(uint itemId, string reason)
{
    if (itemId == 0) return;
    int prior = _itemRetryCount.TryGetValue(itemId, out int n) ? n : 0;
    int next = prior + 1;
    if (next > MaxItemRetries)
    {
        _itemRetryCount.Remove(itemId);
        Log($"[Salvage] Giving up on 0x{itemId:X8} after {MaxItemRetries} attempts ({reason}).");
        // Trigger a combine scan if this was the last item
        if (_queue.Count == 0) _pendingCombineScan = true;
        return;
    }
    _itemRetryCount[itemId] = next;
    _queue.Enqueue(itemId);
    Log($"[Salvage] Re-queued 0x{itemId:X8} ({reason}) — attempt {next}/{MaxItemRetries}.");
}
```

### 5.4 `MaxItemRetries` is global, not per-failure-reason

A UseObject failure (transient — busy, mid-action) and a "still present after result" failure (often persistent — un-salvageable item, profile mismatch, broken UST) both count toward the same 3-strike counter.

For an UnsalvageableItem, we burn 3 retries cheaply and discover nothing new. For transient UseObject failures, we have only 3 chances before giving up, even though the next attempt 1 second later might succeed.

**Better:** track separate counters:
- `transientRetries` — UseObject, AddItem failures. Cap higher (5-7).
- `persistentRetries` — "still present after result" failures. Cap lower (1-2).

Or even simpler: track first-failure-time per item, and give up after a fixed wall-clock window:

```csharp
private readonly Dictionary<uint, long> _itemFirstFailureAt = new();
private const long ItemRetryWindowMs = 30_000;  // 30 seconds

// In RequeueOrDrop:
long firstFail = _itemFirstFailureAt.TryGetValue(itemId, out long t) ? t : 0;
if (firstFail == 0) _itemFirstFailureAt[itemId] = NowMs;
else if (NowMs - firstFail > ItemRetryWindowMs) {
    Log($"[Salvage] Giving up on 0x{itemId:X8} after {(NowMs - firstFail) / 1000}s of retries.");
    _itemFirstFailureAt.Remove(itemId);
    return;
}
_queue.Enqueue(itemId);
```

This gives transient failures the time they need but caps total time spent on one item. More forgiving for genuine retries, equally protective against infinite loops.

---

## 6. `IsBagUnderFull`, `TryParseTrailingNumber`, `IsSalvageBag`

I covered these in the prior review (§5, §6). Brief recap:

- **`IsBagUnderFull` returns false on property-read failure** → bag treated as full → never combined. Should default to true (assume under-full when unknown).
- **`TryParseTrailingNumber` requires the closing paren to be the last character** → fails on names with trailing whitespace or suffixes. Should trim and search for last `(NN)` group.
- **No support for `(75/100)` X-of-Y format** → some servers use this, parser silently returns false.
- **`IsSalvageBag` is just `name.Contains("Salvage (")`** → won't match servers using "Salvage Bag of Iron" style. Better to use a WCID list or ObjectClass.

All covered previously. Still relevant.

---

## 7. Helpers I didn't cover before

### 7.1 The four STypes constants

Lines 232-235:

```csharp
private const uint StypeMaxStructure    = 91;
private const uint StypeStructure       = 92;
private const uint StypeItemWorkmanship = 105;
private const uint StypeMaterialType    = 131;
```

The comment at line 229-231 explains why these are inlined here rather than from `PropertyNames.IntNames`:

> *"The PropertyNames.IntNames index in this codebase is off-by-some-rows so do NOT use it as the source of truth."*

This is fine — the constants are correct, and the comment explains the gotcha. But it's worth fixing the underlying issue (the off-by-some-rows in PropertyNames) so future code doesn't have to re-discover it. That's outside SalvageManager scope but worth filing.

### 7.2 `_currentUstId` is dead state

`_currentUstId` is set in TickIdle line 179, used in line 184, reset in line 189 and 207. It's never used after `BeginAddingItem` — but it's a class field carrying state across phase transitions where it's not actually needed.

Make it a local in TickIdle:

```csharp
private void TickIdle(long now, int busyCount)
{
    // ...
    uint ustId = FindUst();
    if (ustId == 0) { /* ... */ return; }
    if (!_host.UseObject(ustId)) { /* ... */ return; }

    _currentItemId = ustId;  // <-- only this needs to persist
    int openDelay = ...;
    _phaseReadyAt = now + openDelay;
    _phase = Phase.OpeningPanel;
}
```

(Keep `_currentUstId` only if you actually need it for combine-cycle UST caching per §4.3.)

### 7.3 `IsBusy` reports based on `_phase` and `_queue` but not `_combineGroups`

`IsBusy => _phase != Phase.Idle || _queue.Count > 0`. During a combine cycle, `_phase == Phase.CombiningSalvage` so this is correctly true. But what if `_combineGroups != null` and `_phase` somehow got reset to Idle (e.g., the wedge-watchdog from §3.2 fires)? Then IsBusy says false but `_combineGroups` is leaked memory.

Defensive: clear `_combineGroups` whenever `_phase` returns to Idle. The wedge-watchdog reset path should handle this.

---

## 8. Settings, observability, integration

### 8.1 Settings exposure parity (the question I missed first time)

Searching `RynthSuite-main` for the salvage settings:

```
LegacyUiSettings.cs           — declares them all (lines 61-86)
LegacyAdvancedSettingsUi.cs   — exposes them in ImGui Advanced Settings
NavPanel.cs / various Avalonia panels — none expose any salvage settings
```

Same pattern as the navigation review: the legacy ImGui panel exposes everything, the Avalonia panels expose nothing. When ImGui retires, salvage settings vanish from the UI.

**Items to add to the migration parity list:**
- `EnableCombineSalvage` (toggle)
- `CombineBagsDuringSalvage` (toggle)
- `SalvageOpenDelayFirstMs`, `SalvageOpenDelayFastMs`
- `SalvageAddDelayFirstMs`, `SalvageAddDelayFastMs`
- `SalvageSalvageDelayMs`
- `SalvageResultDelayFirstMs`, `SalvageResultDelayFastMs`

The two toggles are user-facing; the timing settings should go in an "Advanced" subsection.

### 8.2 No `GetStateSnapshot()`

`CombatManager.GetStateSnapshot()` exposes ~20 fields for the diag panel. `BuffManager.GetStateSnapshot()` does similar. SalvageManager has nothing.

What a snapshot should expose:

```csharp
public SalvageStateSnapshot GetStateSnapshot() => new()
{
    Phase                = _phase.ToString(),
    QueueDepth           = _queue.Count,
    CurrentItemId        = _currentItemId,
    CurrentUstId         = _currentUstId,
    PanelEverOpened      = _panelEverOpened,
    PendingCombineScan   = _pendingCombineScan,
    LastCombineSweepAt   = _lastCombineSweepAt,
    CombinePhase         = _combinePhase.ToString(),
    CombineGroupCount    = _combineGroups?.Count ?? 0,
    CombineCurrentGroup  = _combineGroupIdx,
    RetriedItemCount     = _itemRetryCount.Count,
    BagsMergedThisSession = _bagsMergedThisSession,   // <-- new
    SweepsRunThisSession  = _sweepsRunThisSession,    // <-- new
    LastSweepResult       = _lastSweepResult,         // <-- new (string: "merged 3", "no candidates", etc.)
};
```

Surface this in `/ra salvage` (or whatever the diagnostic command is). Without it, users have no visibility into "is the queue stuck", "how many bags have been merged", "when did the last sweep run". You can't tell whether salvage is broken, idle, or doing fine — and that's the same information gap that made the combine bug invisible.

### 8.3 No metrics surface for "did this session do useful work?"

Counters that should exist:
- `_itemsSalvaged` — items actually consumed
- `_itemsDropped` — items that hit max retries
- `_bagsMergedThisSession` — successful combine merges (delta of input bag count vs output)
- `_sweepsRun` — combine sweeps attempted
- `_sweepsThatFoundCandidates` — sweeps that had something to do
- `_combineGroupsSucceeded` / `_combineGroupsFailed` — per-group outcome

Five minutes per counter to add. The aggregate snapshot makes the manager's behavior legible.

### 8.4 The interaction with the priority architecture

From the priority review: SalvageManager mutates `_settings.BotAction = "Salvaging"` indirectly (via the OnTick orchestrator at lines 419-430). If the scheduler-based architecture lands, SalvageManager becomes an `IActivity` and:

- `CanRun(ctx)` = `ctx.Settings.IsMacroRunning && (ctx.Settings.EnableCombineSalvage || _queue.Count > 0)`
- `Score(ctx)` = high when `_queue.Count > 0` (active queue), medium when `_pendingCombineScan` is set, low otherwise
- `Stickiness` = high — don't interrupt a combine cycle mid-flight

The state-mutation pattern goes away. Cross-reference: the priority review's §3.3 sketch shows where SalvageManager would slot in.

---

## 9. Misc smaller items

### 9.1 Logging consistency

Some logs use `_host.Log(...)`, some use `_host.WriteToChat(...)` with a chat code. The "no UST" case at line 173 uses `WriteToChat` (visible to user); the rest are Log (file/diag only). The mix is fine but not always principled — e.g., "Item still in inventory after salvage" is Log-only at line 329, but a user being told "your salvage cycle didn't actually consume the item" might be useful at a low chat level (verbose mode).

### 9.2 `EnqueueItem` doesn't dedup

```csharp
public void EnqueueItem(uint itemId)
{
    if (itemId != 0)
    {
        _queue.Enqueue(itemId);
        _pendingCombineScan = false;
    }
}
```

If the same item gets enqueued twice (race in CorpseOpenController, hot-reload double-fire, etc.), it's processed twice. The second cycle will hit "item still present" → `RequeueOrDrop` → eventually drop. So it's self-correcting but wastes 1-3 retry slots.

```csharp
public void EnqueueItem(uint itemId)
{
    if (itemId == 0) return;
    if (_queue.Contains(itemId)) return;  // dedup — skip duplicates
    if (_currentItemId == itemId) return; // also skip if we're processing it right now
    _queue.Enqueue(itemId);
    _pendingCombineScan = false;
}
```

`Queue<T>.Contains` is O(n) but n is small (queue depth rarely exceeds 20). Negligible cost.

### 9.3 The `_pendingCombineScan = false` reset on enqueue is interesting

Line 90:

```csharp
_pendingCombineScan = false; // reset; will be set again when queue drains
```

The intent: if a new item is queued while we're already idle waiting to scan, don't run the scan now — finish the queue first, then scan. Correct logic, but the comment could be clearer:

```csharp
// If a combine scan was pending after the previous queue drain, defer it —
// new items just arrived, process them first. The scan flag will be re-set
// when this new batch drains.
_pendingCombineScan = false;
```

### 9.4 Comment at line 50-54 is slightly misleading

> *"AC sometimes drops a Salvage execute when the bot is busy with other actions; the item stays in the pack and we want to try again when the queue gets back to idle."*

But the busyCount gate at line 163 prevents salvage from STARTING when busy. The retry tracker is for cases where the salvage cycle began cleanly but didn't complete server-side. Probably worth refining:

> *"AC sometimes silently drops a Salvage execute even when we wait for busy to clear — server-side races during combat, mid-portal-action, etc. The item stays in the player's pack with mana value still on it. The retry counter caps re-attempts so we don't loop forever on a truly un-salvageable item."*

### 9.5 `AddAllMatchingUnderFullBags` has zero per-add delay

Line 263-284. The function adds bags in a tight `foreach` loop with no inter-call delay:

```csharp
foreach (WorldObject bag in _cache.GetDirectInventory(forceRefresh: false))
{
    // filtering...
    if (_host.SalvagePanelAddItem(bagId)) { /* count it */ }
}
```

Compare to `TickCombiningSalvage.AddingBags` which spaces adds by `SalvageAddDelayFastMs = 50ms` between each. So during-salvage combine is "rapid-fire 5 adds with no delay" while the combine flow is "carefully spaced 5 adds 50ms apart."

If 50ms is needed for the panel to register an add reliably, the during-salvage path is unsafe — some adds will be dropped silently. If 50ms isn't needed, the combine flow is unnecessarily slow (250ms wait for 5 bags).

One of these is wrong. The during-salvage compensation is the `extraBags * SalvageAddDelayFastMs` margin in `BeginAddingItem` line 224 — but that's a one-shot delay BEFORE Execute, not BETWEEN adds. So if the panel can't handle rapid adds, the rapid adds are lost regardless of how long we wait before Execute.

Easy way to check: instrument the `if (_host.SalvagePanelAddItem(bagId))` return value. If on a multi-bag combine, some adds return false, you've found the answer. If they all return true but the server still doesn't merge them, the panel handled the rapid adds fine and the combine flow's per-add delay can be reduced.

### 9.6 No explicit Reset on hot-reload / login

When the plugin hot-reloads, RynthAiPlugin recreates SalvageManager. Old instance's state is discarded. New instance starts from scratch.

But what if the salvage panel was OPEN when the plugin reloaded? AC's panel state still shows "open" but our `_panelEverOpened = false` (new instance). Next cycle: we call `UseObject(ust)` to open the panel — AC sees "panel already open, no-op" — we wait `SalvageOpenDelayFirstMs = 400ms` for an open animation that isn't happening.

Defensive: at login/reload, consider sending a "close salvage panel" command if the user has one open. That guarantees a clean slate. Or just live with the 400ms wasted on first cycle — minor.

### 9.7 No try/catch around `OnTick` body

If any of the phase handlers throws (e.g., a host method in a transient state), the exception bubbles to the plugin's OnTick, which catches it generically (`RynthAiPlugin.OnTick` line 545-548 has a top-level try/catch). The salvage state is left in whatever partial state caused the throw.

Wrap the phase dispatch in a try/catch and force-reset on exception:

```csharp
public void OnTick(int busyCount)
{
    if (!_host.HasSalvagePanel || !_host.HasUseObject) return;
    long now = NowMs;

    try
    {
        // ... phase dispatch ...
    }
    catch (Exception ex)
    {
        Log($"[Salvage] OnTick crashed in phase {_phase}/{_combinePhase}: {ex.GetType().Name}: {ex.Message}");
        ForceResetState();
    }
}
```

Localizes failure to one tick rather than letting state corruption persist.

---

## 10. Action plan (full review)

### Day 1 — real bugs

| # | Item | Effort | Section |
|---|---|---|---|
| 1 | Fix `IsCarriedByPlayer` Container==0 false-positive | 15min | §2.1 |
| 2 | Tighten `IsUst` to EndsWith semantics | 5min | §2.2 |
| 3 | Route both raw `_queue.Enqueue` paths through `RequeueOrDrop` | 15min | §2.3 |
| 4 | Make `_pendingCombineScan` path respect `busyCount == 0` | 10min | §2.4 |
| 5 | Push back item to queue and pause-with-warning when no UST | 30min | §2.5 |
| 6 | Treat `SalvagePanelExecute` failure in combine flow as group-skip | 20min | §2.6 |

### Day 2 — verification (the prior review's headline issue)

| # | Item | Effort | Section |
|---|---|---|---|
| 7 | Combine result verification: snapshot bag IDs, count survivors, log | 1-2h | Prior review §1 |
| 8 | Track per-(material,band) failures, exclude after 3 strikes | 1h | Prior review §7.5 |
| 9 | Add `_bagsMergedThisSession`, `_sweepsRun*` metrics | 30min | §8.3 |
| 10 | Add `GetStateSnapshot()` | 30min | §8.2 |

### Week 1 — architecture

| # | Item | Effort | Section |
|---|---|---|---|
| 11 | Add top-level phase watchdog (30s wedge → reset) | 1h | §3.2 |
| 12 | Replace `_panelEverOpened` with `_lastPanelOpenAt` recency check | 1h | §3.3 |
| 13 | Remove `_firstResultCycle` (or rebuild as recency-based) | 30min | §3.4 |
| 14 | Restructure `FindUst` cascade — direct inventory only, 3 passes | 1h | §4 |
| 15 | Cache UST id for the duration of a combine cycle | 30min | §4.3 |
| 16 | Wrap OnTick body in try/catch with state reset on exception | 30min | §9.7 |
| 17 | Dedup `EnqueueItem` | 5min | §9.2 |

### Week 2 — bigger refactors

| # | Item | Effort | Section |
|---|---|---|---|
| 18 | Unify `Phase` and `CombinePhase` into single state machine + batch concept | 4h | §3.1 |
| 19 | Replace `MaxItemRetries` cap with wall-clock retry window | 1h | §5.4 |
| 20 | Cache-stable gate (depends on shared signal from CombatManager review) | 1h | Prior review §3.2 |
| 21 | Avalonia panel parity for salvage settings | 3h | §8.1 |

### Cross-reference

Items that depend on or relate to other reviews:

- **Prior salvage review §1 (verification):** Day 2 items 7-9 are the prior review's headline. Highest impact for the user's reported bug.
- **CombatManager review §3 (scheduler):** Items in week 2 (item 18 unification, item 20 cache-stable gate) align with the scheduler refactor.
- **NavigationEngine review §4 (Avalonia panel parity):** Item 21 fits into the same migration plan.
- **Earlier reviews:** the same general pattern — verify before declaring success, expose state through snapshots, unify dual state machines — recurs across all the manager classes.

---

## 11. The "honest small thing" that's worth doing today

If you only have 30 minutes this week, the highest-leverage items are:

1. **Combine result verification** (prior review §1) — converts "not combining sometimes" from invisible to debuggable.
2. **`IsCarriedByPlayer` Container==0 fix** (§2.1) — a real bug, 15-minute fix.
3. **`GetStateSnapshot()`** (§8.2) — gives you visibility into queue depth and current phase.

Those three changes alone give you the ability to *observe* the manager's behavior in real time, see what's failing, and fix the most obvious bug. Everything else can wait until the failure modes are visible.

The first review's claim still stands: **fix the verification first, because every other fix is hard to evaluate without it.** The full-review additions are mostly polish, robustness, and code-quality improvements that compound but don't change the user's day-to-day experience as dramatically as the verification gap.

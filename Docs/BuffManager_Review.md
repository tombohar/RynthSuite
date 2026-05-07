# BuffManager Review + The Item Enchantment Duration Question

**Reviewer:** Claude (Opus 4.7)
**Date:** May 2026
**Scope:** `Plugins/RynthCore.Plugin.RynthAi/Combat/BuffManager.cs` (1,125 LOC), `Engine/Compatibility/EnchantmentHooks.cs` (197 LOC), `Engine/Compatibility/AppraisalHooks.cs` (424 LOC), with detours into `PropertyUpdateHooks.cs` and the on-cast tracking flow.

---

## Headline: about the missing item-enchantment durations

You said: *"Buff bar does not show countdown for item enchantment spells on armor only. It does for everything else. The spell is on the armor itself and we couldn't find a way to figure out duration from code. VTank couldn't either."*

I want to address this first because it shapes everything else.

### You're right. The data really isn't there.

Three independent confirmations, working from the symptom outward:

**1. The AC client UI itself doesn't show countdowns for these.** That's an extremely strong signal. The buff bar shows timers for player enchantments (rendering from `CEnchantmentRegistry._mult_list`/`_add_list` start_time + duration fields). For armor-cast Impen/Banes, no timer is shown in vanilla AC. If the timer data lived in client memory anywhere accessible to the renderer, the renderer would use it. It doesn't.

**2. VTank had two decades of community-accumulated reverse engineering and the same memory access you do.** If the data were findable, somebody on the VTank team would have found it. They didn't.

**3. Your own code already proves the registry path is the right answer for *player* buffs and the wrong answer for *item* buffs.** `EnchantmentHooks.ReadPlayerEnchantments` works correctly — `RefreshFromLiveMemory` uses it and gets accurate timers. `RefreshEquippedItemEnchantments` exists and *also* uses the same registry-walk via `ReadObjectEnchantments` on each equipped item — but `PrintBuffDebug` line 737 has the comment:

> *"TODO: Item enchantment scanning disabled — crashes when reading inventory item memory via ReadObjectEnchantments. Needs investigation (AV in native InqInt or GetWeenieObject for certain inventory items)."*

The crash is a real bug (and it's the same class as the login `ObjectIsAttackable` bug from the previous review — items whose weenie pointer hasn't populated yet). But here's what matters for the duration question: **even after that crash is fixed and the registry walk works**, the registry on the item only contains useful timer data if the server sent it to the client. And for armor-cast Impen, the server doesn't send timer data through the regular network path.

### Why? AC's protocol design

In AC, enchantment data flows two ways:

**Path A: regular `Enchantment_Notice` packet** (server → client when a spell is applied). For spells cast on the player, this fires immediately and includes `_start_time` and `_duration` as part of the `Enchantment` struct. The client deserializes it directly into the player's `CEnchantmentRegistry`. Result: full timer data, buff bar shows countdown, your hook reads it correctly.

**Path B: `IdentifyResponse` (opcode 0xC9, "AppraisalProfile")**. For item enchantments — the spell is on the item, not the player. The server stores it in the item's record server-side. The client doesn't receive it through `Enchantment_Notice` because the player isn't the target. The client only sees this data when:

- The player presses Assess on the item (sends a `RequestId` packet → server replies with `IdentifyResponse`).
- The `IdentifyResponse` includes an "Enchantments" field within `AppraisalProfile`.

That's the *only* path by which the client could ever know the durations.

### What's in the AppraisalProfile, and what's missing from your hook

Looking at `AppraisalHooks.cs` lines 234-406, the existing hook caches four things from the profile struct:

| Field | Offset in `AppraisalProfile` | Captured? |
|---|---|---|
| `_intStatsTable` | `+0x18` | ✅ via `CacheIntProps` |
| `_boolStatsTable` | `+0x20` | ✅ via `CacheBoolProps` |
| `_strStatsTable` | `+0x28` | ✅ via `CacheStringProps` |
| `_spellBook` (PSmartArray<uint>) | `+0x30` | ✅ via `CacheSpellIds` |
| **`_enchantments`** (active enchantments list with timers) | **somewhere in the same profile** | ❌ **not captured** |

The `_spellBook` at +0x30 is the list of *cantrip spell IDs* that are imbued in the item (passive cantrips, e.g., "Major Armor"). It's just `uint[]`, no timer data — that matches the existing code's reading of just spell IDs.

The `_enchantments` field is a different structure on the same `AppraisalProfile` — it's the list of *cast-on-item* spells with full `Enchantment` records (the same 80-byte struct as in `CEnchantmentRegistry`, with `_start_time` and `_duration` doubles). This is what's currently not being read.

**This is potentially the answer to your question.** The data probably *does* arrive at the client — but only on appraisal — and the existing hook is parsing the AppraisalProfile but skipping the enchantments field.

### What I can't tell you for sure without testing

I'm being honest about my uncertainty here:

- **The exact offset of `_enchantments` within `AppraisalProfile`.** Looking at the struct layout from the surrounding offsets (0x18, 0x20, 0x28, 0x30), the next plausible offset is `+0x34` or `+0x38`, but I don't have ground truth from disassembly.
- **The exact structure of the field.** Likely a `PSmartArray<Enchantment>` or a `PackableList<Enchantment>` mirroring the registry layout, but possibly a flat array or a hashtable keyed by spell ID.
- **Whether the data persists in client memory after the appraisal popup closes.** The C++ client *might* free the profile after rendering. If it's released within milliseconds of receiving, only a packet-level hook (intercepting before deserialization) catches it.
- **Whether all server emulators send this data.** ACE and GDLE both serialize enchantments in the appraisal response, so any modern emulator should. Retail AC also sent it (the appraisal panel showed enchantment counts and the spell book). But a custom server build might omit it.

### How to find out (in priority order)

1. **Disassemble `CIdentifyResponseEventHandler` in `acclient.exe`.** This is the function that parses the IdentifyResponse packet into AppraisalProfile. Find where it allocates the enchantment list, and you'll see both the offset within the profile and the struct layout. ~30 minutes with Ghidra if you've got symbol files; longer if you're working from raw disassembly. The function probably reads from packet offsets and stores into the profile, so the cross-reference from `AppraisalProfile`'s constructor (or the operator that fills it) is the cleanest entry point.

2. **Hook the packet handler directly.** Even if the profile struct doesn't keep the enchantment list, the network deserializer must touch every byte of the packet to parse it. Hook `CClientNetwork::DispatchInboundPacket` or whatever `acclient.exe` calls it, filter for opcode 0xC9, parse the bytes yourself. ACE's `GameMessageSetAppraiseInfo` source code documents the wire format; you can mirror it. This is more invasive but avoids any "client discards the data after rendering" problem.

3. **Pre-cast diff approach.** Before casting Impen on an item, snapshot its registry. After casting, snapshot again. Diff: the new spell entry should have `_start_time` matching server time and `_duration` matching whatever the server applied. If the registry is populated post-cast, you've got it. If only the spell ID appears with `_duration = 0`, the server isn't sending the timer through that path.

I'd start with #3 first — it's the cheapest experiment. Five minutes:

```csharp
// In RecordItemSpellCast, immediately after the cast goes through:
if (_host.HasReadObjectEnchantments)
{
    uint[] sids = new uint[16];
    double[] expiry = new double[16];
    int n = _host.ReadObjectEnchantments(itemId, sids, expiry, 16);
    _host.Log($"Post-cast item registry: {n} entries");
    for (int i = 0; i < n; i++)
        _host.Log($"  spell={sids[i]} expiry={expiry[i]} (serverNow={serverNow})");
}
```

If that comes back with a real `_duration` for the spell you just cast, half the problem is solved (only thing left is fixing the AV).

If `_duration` is 0 or `expiry == _start_time`, then the registry doesn't have it and you need #1 or #2.

### So given all that — is the disk-based approach actually wrong?

**No, it's the right answer for the constraint.** Server-side timer + server-only ground truth = the client can only ever have a cached/stale view. Recording cast time + known duration is *exactly* the right substitute when the client doesn't know.

What you'd gain from the registry/appraisal path:

- **Detection of buffs cast by other people.** If a fellow buffs your Impen, your disk file doesn't know — but if their cast triggers an appraisal (or the registry walk works), you'd see it. This is real but minor — most Impen-on-self workflows are self-cast.
- **Recovery from disk corruption / lost timers.** A re-appraisal could rebuild the file. Currently a corrupted bufftimers.txt = forced rebuff sequence.
- **Log-out / hot-reload accuracy.** If you log out for an hour and the timer expires while you're offline, the disk version says "still active" until you next interact with it. Live registry would say "expired."

What you wouldn't gain: the buff itself is just as effective whether you know its remaining time or not. The only thing this affects is "when do I recast preemptively" — and the disk approach is right within ±a few seconds.

So: investigation is worth doing because if the registry path works, it's strictly better. But your current approach is **not a hack you should feel bad about** — it's the correct adaptation to a server-authoritative protocol.

---

## 1. The architectural issue with BuffManager: three overlapping timer dictionaries

Even setting aside the duration question, BuffManager has a structural problem. Three separate dictionaries cover three slightly different concepts:

| Field | Key | What it tracks | Source |
|---|---|---|---|
| `_ramBuffTimers` | `int` (spell family) | Player enchantments | Live read from `CEnchantmentRegistry` via `ReadPlayerEnchantments` |
| `_itemBuffTimers` | `long` ((objectId<<32) \| family) | Per-item enchantments | Live read from item's `CEnchantmentRegistry` via `ReadObjectEnchantments` (currently disabled due to AV) |
| `_itemSpellTimers` | `int` (spell family) | Cast-time recorded item spells | Recorded optimistically when the plugin casts the spell |

`_ramBuffTimers` and `_itemSpellTimers` overlap in concept ("how long until this spell expires") but cover different categories (player vs item). `_itemSpellTimers` and `_itemBuffTimers` *also* overlap — they both track "spells on items" but one is local-record-and-trust, the other is registry-read.

`RefreshFromLiveMemory` (line 599-633) has this beautiful piece of code that betrays the confusion:

```csharp
// Preserve item enchantment timers (armor banes, Impenetrability, etc.)
// that live on the item, not the player — ReadPlayerEnchantments won't
// return them, so clearing would lose them every login.
var preservedItemTimers = new Dictionary<int, RamTimerInfo>();
foreach (var kvp in _ramBuffTimers)
{
    if (kvp.Value.Expiration > DateTime.Now && IsArmorEnchantment(kvp.Value.SpellName))
        preservedItemTimers[kvp.Key] = kvp.Value;
}

_ramBuffTimers.Clear();
// ... refresh from registry ...

foreach (var kvp in preservedItemTimers)
    _ramBuffTimers.TryAdd(kvp.Key, kvp.Value);
```

So `_ramBuffTimers` *also* contains item enchantments? Yes, because `IsBuffActive` (line 489-531) only consults `_itemSpellTimers` for armor enchantments — but historical / loaded-from-disk timers might still be in `_ramBuffTimers` from older sessions. The "preserve" hack keeps them alive across the refresh.

**This is doing one job badly because it's pretending to be three dictionaries that are actually trying to be one source of truth.** The conceptually correct shape is:

```csharp
public enum BuffSource
{
    PlayerRegistry,   // read from player's CEnchantmentRegistry — high confidence
    ItemRegistry,     // read from item's CEnchantmentRegistry  — high confidence (post-AV-fix)
    SelfCast,         // recorded at cast time + known duration — medium confidence
    AppraisalCache,   // captured from IdentifyResponse           — high but stale
}

public sealed class BuffEntry
{
    public int       Family;
    public uint?     ItemObjectId;     // null for player buffs
    public string    SpellName;
    public int       SpellLevel;
    public DateTime  RecordedAt;
    public DateTime  ExpiresAt;
    public BuffSource Source;
}

private readonly Dictionary<(int family, uint? item), BuffEntry> _buffs = new();
```

One dictionary, keyed by the natural pair (family, optional item). `IsBuffActive` looks up by family for player buffs or (family, item) for item buffs. `Refresh` doesn't have to "preserve" anything — each refresh source updates only the entries with its `Source` tag. Disk persistence walks `_buffs.Values.Where(b => b.Source != PlayerRegistry)` (registry-sourced ones aren't worth saving — they're always re-readable).

This refactor is half a day. After it, the "preserve item timers when refreshing" hack disappears, the disk format becomes one shape instead of two, and `IsBuffActive` doesn't have to branch on `IsArmorEnchantment` to choose which dictionary to consult.

---

## 2. The `IsArmorEnchantment` whitelist is brittle

`BuffManager.cs:458-469`:

```csharp
private static bool IsArmorEnchantment(string name)
{
    string[] armorSpells = {
        "Impenetrability", "Brogard's Defiance", "Acid Bane", "Olthoi's Bane",
        "Blade Bane", "Swordsman's Bane", "Swordman's Bane", "Bludgeoning Bane", "Tusker's Bane",
        "Flame Bane", "Inferno's Bane", "Frost Bane", "Gelidite's Bane",
        "Lightning Bane", "Astyrrian's Bane", "Piercing Bane", "Archer's Bane"
    };
    foreach (string s in armorSpells)
        if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
    return false;
}
```

The list is incomplete (missing weapon-cast spells like Spirit Drinker, Hermetic Link, etc.) and wrong-typed in two places (note `"Swordman's Bane"` *and* `"Swordsman's Bane"` — somebody worked around a typo by including both). Substring matching is also a footgun: "Impenetrability" matches "Impenetrability VII" but also matches anything containing that exact substring; it's not robust to future spell additions.

A spell's "is this cast on an item or on a creature" property is a *server-side property* of the spell definition. In AC's spell table (parsed from .dat files or hardcoded in `SpellTableStub`), each spell has a `Targets` field with values like `Self`, `Other`, `Item`, `Creature`. The right test is `spell.Targets == SpellTargets.Item` — not a name whitelist.

If `SpellTableStub` doesn't expose `Targets` today, that's the missing data. Adding it once means every other place in the codebase that needs to know "is this an item spell" gets the right answer for free. Five minutes of plumbing now, hours of bug-hunting later when someone adds a spell to the cast list and the timer doesn't track because the name didn't match.

---

## 3. `GetSpellLevel` is a string-parsing pile

Lines 533-558. To classify a spell as level 1-8, the code does:

```csharp
if (n.StartsWith("Incantation")) return 8;
if (n.Contains(" VII")) return 7;
if (n.Contains(" VI")) return 6;
if (n.Contains(" V")) return 5;
if (n.Contains(" IV")) return 4;
if (n.Contains(" III")) return 3;
if (n.Contains(" II")) return 2;
if (n.EndsWith(" I") || n.Contains(" I ")) return 1;
// then a 30-line list of Mastery/Blessing/etc spells that are level 7
```

`Contains(" V")` matches "Major Magic Defense V" but also matches anything containing " V" — like "Improved Vitae" (if such a thing existed) or "Power V Sigils". Order matters too — `" VI"` must be checked before `" V"` (it is), `" VII"` must be checked before `" VI"` (it is). A future spell with "Vitae" in the name would be misclassified.

Most importantly: **AC spells have a `Power` field in the spell table.** Every spell already has its level as data. Looking at `SpellTableStub`, if it doesn't expose `Power` today, that's a missing column. The fix:

```csharp
private static int GetSpellLevel(SpellInfo spell) => spell.Power;  // 1-8 from spell table
```

If you really need a fallback for spells not in the table (parser failure, server custom spells), the string-matching can be an *exception path*, not the default.

---

## 4. `GetCustomSpellDuration` is hardcoded magic numbers

Lines 566-575:

```csharp
private double GetCustomSpellDuration(int spellLevel)
{
    double baseSeconds = 1800;
    if (spellLevel == 6) baseSeconds = 2700;
    else if (spellLevel == 7) baseSeconds = 3600;
    else if (spellLevel == 8) baseSeconds = 5400;

    int augs = GetArchmageEnduranceCount();
    return baseSeconds * (1.0 + (augs * 0.20));
}
```

Three problems compounding:

**1. The numbers are server-specific.** Retail AC durations differed from ACE defaults differed from custom emulators. Hardcoding "30 minutes for Impen V" assumes a specific server. If the user moves servers (or the server admin tweaks durations), every cast records a wrong expiry — and the bot under-buffs (recasts too late) or over-buffs (wastes mana).

**2. The fallback for unknown levels (default 1800 seconds) is wrong for low levels.** Impen I has a much shorter duration on most servers. The "if not 6-8, assume 30 minutes" default is silently wrong for any cast at lower tier.

**3. `GetArchmageEnduranceCount` is stubbed to 0** (line 562-564). The TODO says "Read augmentation count from AC object memory (key 238 on player object)." So even on the right server, the duration is currently undercounted by 20% per Archmage's Endurance aug — and most archmages have it at level 4 (80% bonus). The bot *thinks* Impen lasts 60 minutes; on a fully-aug'd character the actual duration is 108 minutes. Net effect: the bot recasts way too early.

**Fix layer 1:** Read augs from the player object. Looking at `AppraisalHooks.TryGetCachedIntProperty`, this is already plumbed — just look up stype 238 on the player guid:

```csharp
private int GetArchmageEnduranceCount()
{
    uint pid = (uint)_host.GetPlayerId();
    if (pid == 0) return 0;
    if (AppraisalHooks.TryGetCachedIntProperty(pid, /* stype */ 238, out int v))
        return v;
    // Fallback: trigger an appraisal and try again next tick.
    return 0;
}
```

The player gets auto-appraised at login; the value should be in cache. If it's not, request an appraisal once.

**Fix layer 2:** Source durations from spell data, not hardcoded constants. Each spell in the table has a `Duration` field — use it. If `SpellTableStub` doesn't have it, that's another column to add. Once it does:

```csharp
private double GetSpellDuration(SpellInfo spell)
{
    double base_ = spell.BaseDuration;        // from spell table
    int augs = GetArchmageEnduranceCount();
    return base_ * (1.0 + augs * 0.20);
}
```

Now the value is correct for every server, every spell tier, every aug level.

---

## 5. The disabled item-registry walk is the same bug as the login bug

The TODO at line 737-739 says `RefreshEquippedItemEnchantments` AVs "for certain inventory items." Looking at `EnchantmentHooks.ReadObjectEnchantments` (which is what it calls) line 102-138:

```csharp
if (!ClientObjectHooks.TryGetWeenieObjectPtr(objectId, out IntPtr weeniePtr))
    return -1;

IntPtr qualAddr = weeniePtr + ClientObjectHooks.WeenieQualitiesOffset;
if (!SmartBoxLocator.IsMemoryReadable(qualAddr, 4))
    return -1;
IntPtr qualPtr = Marshal.ReadIntPtr(qualAddr);
if (qualPtr == IntPtr.Zero) return -1;
```

The defenses look correct. `TryGetWeenieObjectPtr` returns false if the weenie isn't in the table; `SmartBoxLocator.IsMemoryReadable` validates the qualities pointer; `qualPtr == IntPtr.Zero` returns early. So in theory it shouldn't AV.

But — **same as the login `ObjectIsAttackable` bug** — there's a window where:

1. The client has just finished syncing inventory (post-login burst, or mid-session add)
2. `_worldObjectCache` has the item ID classified
3. `GetWeenieObject(itemId)` returns a *non-null but partially-initialized* pointer
4. Reading `qualPtr` succeeds with a value that points into freed/unmapped memory
5. `Marshal.ReadIntPtr(qualPtr)` AVs

`SmartBoxLocator.IsMemoryReadable` uses `VirtualQuery` which only checks page protection — it can't tell "this page is mapped but currently being concurrently freed by another thread." That's the gap.

**Two ways to address:**

**(a) Defensive — gate behind quality stability.** Check `qualPtr` *and* `qualPtr → vtable` *and* validate vtable is in module *before* dereferencing the registry pointer. Looks like `ReadObjectEnchantments` already does this at lines 117-121 — but the AV is happening earlier, at the `Marshal.ReadIntPtr(qualAddr)` on line 113.

**(b) Cooperative — only walk after cache stability signal.** The same "cache stable" signal I suggested for the activity scheduler in the previous review applies here. `WorldObjectCache.IsStable => _pending.Count == 0 && _classifyRetry.Count == 0`. Don't walk inventory items until the cache has been stable for at least ~500ms post-login. This avoids the dangerous window entirely.

For (a), wrap the unsafe parts in a SEH guard. NativeAOT's `try/catch` doesn't catch AV (Corrupted State Exception → FailFast), but the engine could provide a `SafeReadIntPtr(IntPtr)` helper that uses Win32 `IsBadReadPtr` (deprecated but still works) or sets up a vectored exception handler.

I'd do (b) first — the simpler, and also useful as a general-purpose "is the world settled?" signal — and (a) as belt-and-suspenders. Once that lands, `RefreshEquippedItemEnchantments` becomes safely callable; even if the registry duration data isn't there for armor casts (per the headline discussion), at least cantrip enumeration via the registry would be reliable.

---

## 6. `RefreshEquippedItemEnchantments` clears `_itemBuffTimers` every call (race risk)

Line 646:

```csharp
_itemBuffTimers.Clear();
// ... walk inventory, repopulate ...
```

This is a clear-then-rebuild pattern. If anything reads `_itemBuffTimers` while the rebuild is mid-flight (e.g., a UI render pulling the buff list, a meta rule firing on `Time Left On Spell <`), it gets a transient empty result. Combined with `RefreshFromLiveMemory` running on `_buffManager.OnHeartbeat()` (60Hz), there's a 30-frame-per-second window of "no item buffs" visible to readers.

Fix: rebuild into a local dictionary, then atomic-swap:

```csharp
var newBuffs = new Dictionary<long, ItemBuffTimerInfo>();
foreach (var item in inventorySnapshot)
{
    // ... populate newBuffs ...
}
Interlocked.Exchange(ref _itemBuffTimers, newBuffs);
```

(With `_itemBuffTimers` declared as `private Dictionary<long, ItemBuffTimerInfo> _itemBuffTimers = new();` rather than `readonly`.)

Or use a `ConcurrentDictionary` and rebuild in-place with a "seen this key" marker. Either way — never expose the empty intermediate state.

This same pattern repeats in `RefreshFromLiveMemory` line 609 — same fix applies.

---

## 7. `RecordItemSpellCast` records optimistically before the cast resolves

Lines 471-487:

```csharp
private void RecordItemSpellCast(SpellInfo spellInfo)
{
    double duration = GetCustomSpellDuration(GetSpellLevel(spellInfo));
    var now = DateTime.Now;
    _itemSpellTimers[spellInfo.Family] = new ItemSpellRecord
    {
        CastAt    = now,
        ExpiresAt = now.AddSeconds(duration),
        SpellName = spellInfo.Name,
        SpellLevel = GetSpellLevel(spellInfo),
    };
    SaveBuffTimers();
}
```

The doc comment on `ItemSpellRecord` (line 42-44) says: *"Recorded immediately on cast — independent of chat parsing and enchantment hooks. ... Removed on fizzle."* But I don't see the "removed on fizzle" code anywhere. Searching the file for "fizzle" turns up nothing. Searching for "resist" likewise.

So fizzles, resists, and out-of-range failures all leave a phantom timer in `_itemSpellTimers` for the configured duration. The bot says "Impen is up for 60 minutes" when actually nothing was applied. Next time `IsBuffActive` is called for that family, it returns true, and the bot doesn't recast.

This is a real correctness bug. Two ways to fix:

**(a) Listen for cast-resolution chat lines.** *"You cast Impenetrability VII"* → confirm. *"Your spell fizzled"* / *"Your target resists your spell"* / *"That target is too far away"* → withdraw the optimistic record. `CombatManager.HandleChatForDebuffs` already does this for debuff spells (lines 484-510); the same pattern should exist here. Currently it doesn't.

**(b) Listen for the actual enchantment success packet.** Even better — when a spell resolves successfully on the server, the client receives a confirmation message (in retail it was a sound + buff icon flash; the network event is dispatched). If RynthCore's hooks expose this, withdraw the optimistic record only if confirmation isn't received within ~2 seconds.

(a) is easier; (b) is more reliable. (a) plus a 30-second "still optimistic" tag (after which the record is upgraded to "confirmed" only if no negative chat appeared) catches most cases.

---

## 8. Smaller stuff worth noting

### 8.1 `LoadBuffTimers` silently swallows all exceptions

Line 203: `catch { }`. The disk format is delimited by `|`. If a name contains `|`, parsing fails silently; if a tick value is malformed, `long.Parse` throws and is swallowed. The user has a corrupt `bufftimers.txt` and never knows.

Fix: catch specific exceptions (`FormatException`, `IOException`), log them, and consider quarantining the corrupt file (rename to `.txt.bak.{datetime}` so the user can debug it).

### 8.2 `bufftimers.txt` format is brittle

Pipe-delimited with positional fields, two record types (`ram|...` and `item|...`), mixed legacy support. Adding a new record type or field means version-bumping carefully. JSON would be future-proof, less than twice the size, and lets you express things like `{ "source": "registry", "casterFellow": true }` later without rewriting parsers.

### 8.3 `SaveBuffTimers` writes on every cast (line 486)

For chain-casting Banes across multiple armor pieces (head, chest, legs, etc.), this means 6+ disk writes in a few seconds. `File.WriteAllLines` is sync and buffered but still hits the kernel. Debounce: schedule a save 1 second after the last cast, only fire if no further casts happen.

### 8.4 `RefreshFromLiveMemory` skips permanents via magic threshold

Line 620: `if (remainingSeconds > 86400 * 365) continue; // permanent`. So enchantments with remaining time over a year are treated as "skip" rather than "store with a sentinel." That's fine for the timer-display logic but has a subtle effect: `IsBuffActive` will return `false` for a permanent enchantment that legitimately should stay buffed forever. Worth double-checking that all your tracked spells have finite durations on your server.

### 8.5 `_isForceRebuffing` flag is unused for anything but display

Lines 19, 210-214, 528. Set true on `ForceFullRebuff()`, set false in `CancelBuffing()`. But searching for *reads* of `_isForceRebuffing` outside of those: only line 528 reads it (`if (_isForceRebuffing) return false;`) — but that line is inside `IsBuffActive`'s control flow that has already returned. So `_isForceRebuffing` essentially does nothing currently. Either wire it into `IsBuffActive` properly (return false to force a recast on every check) or remove it.

### 8.6 `BuffStateSnapshot` is missing `_itemBuffTimers.Count`

Line 244-256. The snapshot exposes `RamBuffTimerCount` and `ItemSpellTimerCount` but not `ItemBuffCount`. Three timer dictionaries; only two reported. Easy add but speaks to the broader "three dictionaries that should be one" problem.

---

## 9. Prioritized action list

### Investigative / experimental (first)

| # | Item | Effort |
|---|---|---|
| 1 | Run the post-cast item-registry diff experiment (§headline) — does the registry actually contain durations for armor-cast Impen? | 30min including a real test in-game |
| 2 | If yes: fix the AV in `RefreshEquippedItemEnchantments` (§5), enable the path | 2-4h |
| 3 | If no: disassemble `CIdentifyResponseEventHandler` to find the `_enchantments` offset in `AppraisalProfile` (§headline) | 1-2h with Ghidra |

### Correctness fixes (next)

| # | Item | Effort |
|---|---|---|
| 4 | Wire `RecordItemSpellCast` to chat-based confirmation/withdrawal (§7) | 1h |
| 5 | Read Archmage's Endurance aug count from `AppraisalHooks` cache (§4) | 30min |
| 6 | Source spell durations from spell table, not hardcoded constants (§4) | 1-2h |
| 7 | Source spell levels from spell table `Power` field (§3) | 1h |
| 8 | Replace `IsArmorEnchantment` whitelist with `spell.Targets == Item` (§2) | 1h |
| 9 | Fix the clear-then-rebuild race in both `Refresh*` methods (§6) | 30min |

### Architectural cleanup (when you have a day)

| # | Item | Effort |
|---|---|---|
| 10 | Collapse three timer dictionaries into one `_buffs` keyed by (family, item?) with `BuffSource` tag (§1) | 4h |
| 11 | Switch `bufftimers.txt` to JSON (§8.2) | 1h |
| 12 | Debounce `SaveBuffTimers` (§8.3) | 30min |
| 13 | Quarantine corrupt files instead of silently failing (§8.1) | 30min |

### Notes for later

- The "cache stable" signal from the previous CombatManager review is the same gate `RefreshEquippedItemEnchantments` needs. If you build it for the scheduler, BuffManager is a natural second consumer.
- Once item-registry reads work, a future improvement is to read enchantments on items in *fellow's* equipped list — the same `ReadObjectEnchantments` works on any object ID. You'd see when someone in your fellow has Impen on their gear about to expire and could request a re-buff. Out of scope, but worth filing.

---

## 10. The honest one-line answer to your headline question

> *"It would be a lot better to find the actual time. We did exhaustive search for it but could not find the information anywhere."*

**For armor-cast item enchantments specifically, you're probably right that the duration data isn't in the client at steady state — it lives server-side and the client only sees it when an appraisal response arrives.** The disk-based approach is the correct adaptation to that constraint, not a workaround you should feel bad about. The two places I'd still push you to look:

1. **The post-cast registry experiment** (§headline #3 in the priority list). Five minutes, definitive answer for whether the *cast itself* populates the item registry with timer data. If yes, you've got it.

2. **The `_enchantments` field in `AppraisalProfile`** that the existing AppraisalHooks doesn't capture. Adding that capture (once you find the offset) gives you ground-truth durations whenever an item is appraised — which means a periodic auto-appraisal of equipped items keeps timers accurate without any cast-tracking at all.

Both are worth trying. If both come back negative, the disk-based approach with the correctness fixes from §4-§7 is genuinely as good as it gets.

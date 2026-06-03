# Spellcasting Fix Plan — 2026-05-18

**Status:** PLAN (not yet implemented — awaiting review)
**Scope:** RynthCore engine + RynthAi plugin
**Owner context:** spellcasting regressed to "won't cast at all, just retries forever" after the 2026-05-17/18 crash-fix session introduced main-thread cast marshalling.

---

## 1. Symptom

Bot never casts. Live log (pid 24620, char `+Buffi`):

```
[BuffChat] pending=2215 type=5 text='[RynthAi] Casting: Adja's Blessing'   (repeats every ~0.4s, forever)
```

- `pending=2215` (Adja's Blessing) never clears.
- **Zero** `You cast …` lines anywhere — AC never actually casts.
- Cast binding is healthy: `ClientMagicSystem::CastSpell bound at 0x00568DE0 (… plausible=True)`.
- Broken engine DLL is confirmed deployed and live (`Runtime\RynthCore.Engine.dll` mtime 7:57 PM > last source edit 7:54 PM).

## 2. Root cause

The crash-fix session correctly diagnosed that calling `ClientMagicSystem::CastSpell`
from the plugin pump thread (off AC's main thread) causes the WRITE AVs, and
implemented the documented "proper fix" — **main-thread marshalling**:

- New `src/RynthCore.Engine/Compatibility/AcMainThreadQueue.cs` — alloc-free SPSC
  ring. Pump thread enqueues `(SelectItem, CastSpell)`; AC's main thread drains.
- `CombatActionHooks.cs:431` — off-thread cast calls `EnqueueCast(...)`, returns `true`.
- Drain wired into `SmartBoxHooks.cs:149`, inside **`DispatchGameEventDetour`**.

**The idea is right; the drain site is fatally wrong.** `DispatchGameEvent` only
fires when the server sends game-event packets. While the bot stands still
self-buffing (no combat, no nav, no mobs) the server sends ~none → the detour
doesn't fire → **the queue never drains → AC never casts → no "You cast" chat →
pending never clears → infinite retry.**

This is verbatim **dead-end attempt #3** from the prior CastSpell investigation
(memory `rynthcore_castspell_fallback_pitfall.md`): *"engine stage drained from
`DispatchGameEventDetour` → doesn't fire while idle-buffing → ALL casting dead."*
It was re-implemented unknowingly.

### Two compounding bugs

1. **Enqueue contract violated.** `AcMainThreadQueue.EnqueueCast` returns `false`
   when it rejects (single-outstanding / full) and its doc-comment states the
   caller *must* return `false` so the plugin retries. `CombatActionHooks.CastSpell`
   **ignores the bool and always returns `true`**. Once the queue is stuck, every
   retry is silently dropped while the plugin keeps false-waiting for chat.
2. **Blacklist re-poison.** Each stuck buff hits the plugin 5 s NO-CHAT valve
   (`BuffManager.cs:382-399`) → `MarkSpellUnresolvable` → persisted to the char's
   `bufftimers.txt` as `unknown|<id>`, which never expires (memory
   `rynthcore_bufftimers_stale_unresolvable.md`). So affected chars keep skipping
   those spells even after the engine is fixed.

## 3. Chorizite — confirmed dead end for this

Chorizite's own `acclient.map` lists `ClientMagicSystem::CastSpell(ulong, bool)` —
the exact `(spellId, targetIsSelected)` function already bound at `0x00568DE0`.
The casting **method** was never the problem and there is nothing better to switch
to. The problem is purely *which thread / where* it is invoked.

## 4. The fix

### Part A — Core engine fix (RynthCore repo)

**A1. Relocate the drain to the always-on main-thread path.**
`src/RynthCore.Engine/ImGui/EngineFrameController.cs`, `OnEndScene`, as the first
statement of the always-on `try` block (~line 245, before `EnsureCore(pDevice)`):

```csharp
// Drain pump-thread-queued AC writes (SelectItem/CastSpell) on AC's main
// thread. EndScene is the always-on ~30Hz main-thread heartbeat (fires
// regardless of EnableImGuiBackend / idle / combat). Alloc-free + native-only:
// does NOT reintroduce the 2026-05-16 reverse-pinvoke GC fail-fast class.
try { AcMainThreadQueue.Drain(); } catch { }
```

Add `using RynthCore.Engine.Compatibility;` if absent, or fully-qualify.
`OnEndScene` is confirmed firing at 22–33 Hz in the user's exact env (log FPS
lines), on AC's render/main thread, regardless of `EnableImGuiBackend`.

**A2. Honor the enqueue contract.** `Compatibility/CombatActionHooks.cs:431-435`:

```csharp
if (!MainThreadGuard.IsOnMainThread())
    return AcMainThreadQueue.EnqueueCast(targetId, (uint)spellId);
```

**A3. Keep the `SmartBoxHooks.cs:149` `DispatchGameEvent` drain** as a harmless
secondary trigger (same single AC main thread, serialized with EndScene → SPSC
invariant preserved; helps during heavy-combat bursts). Fix the now-stale
"Called from DispatchGameEventDetour" comment in `AcMainThreadQueue.cs`.

**Build/deploy:** `dotnet publish src/RynthCore.Engine -c Release` (NativeAOT,
~2 min, vswhere on PATH) → copy DLL to
`C:\Games\RynthCore\Runtime\RynthCore.Engine.dll` → verify mtime > source edit +
size sane (staleness trap) → RL each client.
Note: publish ships the whole working tree, so the deployed DLL also carries the
uncommitted 2026-05-18 crash fixes (`SmartBoxHooks`/`ClientObjectHooks`) — desired
here, flagged for awareness.

### Part B — Plugin bufftimers recovery (RynthSuite repo) — required for `+Buffi`

The engine fix alone won't un-poison persisted blacklists.

- **B1 (one-time, manual):** with no acclient running, back up then strip only
  `^unknown\|` lines (keep `ram|`/`item|`) from affected chars under
  `C:\Games\RynthSuite\Rynthai\SettingsProfiles\ACEmulator\<Char>\bufftimers.txt`.
  Re-scan all chars — this session likely poisoned more than `+Buffi`.
- **B2 (durable self-heal):** when the known-spell snapshot is warm, drop any
  persisted `unknown|<id>` that is in the snapshot (a demonstrably-known spell
  can't be "unresolvable because unknown"). Must run post-login after the
  snapshot warms, not at file-load. Kills this residue class permanently.
  Plugin-only (`SpellManager`/`BuffManager`) — re-verify file:line before editing.

### Part C — Defense-in-depth (RynthSuite)

`BuffManager.cs:382-399` NO-CHAT valve blacklists unconditionally. Apply the same
guard the combat path already has (`IsKnownSnapshotWarm && IsKnownSpellId` →
log + retry, never blacklist/persist). Makes any future transient drain stall
non-destructive.

## 5. Recommended sequence

1. Part A — build + deploy.
2. Part B1 — strip `bufftimers.txt` for the test char (char not running).
3. Relaunch test char → verify casting works.
4. Part B2 + Part C — the durability layer.

## 6. Verification (test char only — prod stays on Decal/VTank)

In `RynthCore.log`, while idle self-buffing, expect:
- Real `[BuffChat] … 'You cast Adja's Blessing'` lines (AC actually casting).
- `pending=` clearing; buff timers recording.
- No `NO-CHAT TIMEOUT`.
- Cadence back to ~1 cast/sec, not the 0.4 s retry spam.

## 7. Hard constraints

- **No GC/managed work added near EndScene** — drain is alloc-free + native-only,
  so it does not reintroduce the 2026-05-16 reverse-pinvoke fail-fast class that
  forced the plugin tick off this thread.
- SPSC invariant: pump thread = sole producer; AC main thread = sole consumer.
  EndScene + DispatchGameEvent are both AC-main-thread and serialized (AC is
  single-threaded for game/render) → never concurrent → safe.
- Never re-enable the pattern-scanned `_castSpell` game-action-0x4A fallback.
- Engine-only change ⇒ RL each client, not the full wipe-redeploy script.

## 8. File reference index

| Purpose | File:line |
|---|---|
| Cast queue (new) | `RynthCore/src/RynthCore.Engine/Compatibility/AcMainThreadQueue.cs` |
| Off-thread cast → enqueue | `RynthCore/src/RynthCore.Engine/Compatibility/CombatActionHooks.cs:431` |
| Current (wrong) drain site | `RynthCore/src/RynthCore.Engine/Compatibility/SmartBoxHooks.cs:149` |
| Correct drain site (always-on, main thread) | `RynthCore/src/RynthCore.Engine/ImGui/EngineFrameController.cs:238` (`OnEndScene`) |
| Plugin buff cast / pending / retry | `RynthSuite/Plugins/RynthCore.Plugin.RynthAi/Combat/BuffManager.cs:382` (NO-CHAT valve), `:505`, `:593-606` |
| Persisted blacklist | `C:\Games\RynthSuite\Rynthai\SettingsProfiles\ACEmulator\<Char>\bufftimers.txt` |
| Live log | `C:\Games\RynthCore\Logs\RynthCore.log` |

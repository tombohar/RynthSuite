# RynthCore + RynthAi — Overnight Deep-Dive Master Plan

**Date:** 2026-05-16 (overnight pass)
**Scope:** Engine crash-safety/soak, engine hook+render+loader layer, RynthAi Activity-Arbiter
migration, reconciliation of the 4 existing manager reviews vs current code, launcher /
Thwargle-parity / multi-box. Read-only audit; **no code was changed.**

This is a synthesis. The four manager reviews (`CombatManager_Priority_Review.md`,
`BuffManager_Review.md`, `SalvageManager_Full_Review.md`, `MetaManager_Review.md`) and
`ACTIVITY_ARBITER_PLAN.md` remain the source of truth for their problem statements — this
doc reconciles them against the code as it stands tonight and adds the areas they never
covered (engine render/hook/loader, launcher).

---

## VERDICT

1. **The crash soak is clean enough to release.** All three 2026-05-16 engine fixes
   (ProcessExitHooks fail-fast removal, plugin-pump-off-render-thread, CrashLogger VEH
   removal) are verified present in code. Two clients ran ~5h49m continuously across a
   successful hot-reload (17:44→23:33+) with **zero** `==== CRASH ====`, AV,
   `RhpReversePInvoke` fail-fast, `UploadFrame` error, or abnormal exit. Last crash dump
   was 10:13; 3rd fix landed ~10:27; nothing in the 13+ h since. **All four Meta
   crash-class items are also fixed.** Release is unblocked from a stability standpoint.
2. **But two latent crash-class items remain that the clean soak did *not* exercise hard**
   — the `OverlayTextureRenderer.UploadFrame` width/buffer desync race, and the broad
   `ClientObjectHooks` cross-thread TOCTOU. Neither fired tonight; both are real. Decide
   whether to tag the release now (recommended — soak is clean) and fix these in the
   post-release window, or hold one more day. See P0.
3. **The launcher is structurally ~90% of a Thwargle replacement but one dead line
   (`SessionStateStore.cs:59 return null`) silently neuters auto-relaunch, window
   placement, and title-rewrite.** This is the single highest-leverage launcher fix.

### Recommended next 3 sessions
- **Session A (release + 2 quick crash-class):** tag engine v0.14/RynthAi v0.13 release
  (soak clean), then fix `UploadFrame` desync (P0-1, ~1h) and the `BoostLootPriority`
  combat-starvation trap (P1-2, ~30m — same bug class as the one you fixed this morning).
- **Session B (launcher unblock):** fix `SessionStateStore` launcher read path (P1-4),
  which lights up Thwargle punch-list items 2/3/4 that are already coded. Biggest
  Thwargle-retirement step for the effort.
- **Session C (arbiter Step 3):** migrate Buffing into the arbiter per
  `ACTIVITY_ARBITER_PLAN.md` — one subsystem, test between. Kills the buff/combat
  string-race class structurally.

---

## P0 — Release gate & latent crash-class

> Nothing here blocks tagging the release (soak is clean), but these are the
> "kills `acclient.exe`" items and should lead the post-release queue.

**P0-1 — [crash-class] `OverlayTextureRenderer.UploadFrame` width/buffer desync.**
`OverlayTextureRenderer.cs:262,298-306` + `SoftwareOverlaySurfaceBridge.cs:32-34,63-70`.
Per-frame buffer pooling and raw-fn-pointer hardening are already done (the old LFH fix
held — verified in-code at `:284-291`). The remaining bug: `UploadFrame` computes
`srcStride = w*4` and copies `h` rows out of `pixels`, but `(pixels, w, h)` are not
guaranteed to come from one atomic snapshot. On an overlay resize the consumer can copy
with new (larger) `w/h` against an old (smaller) `pixels` array → out-of-bounds read →
the exact ntdll write-AV (`addr …FFC`) signature. Tonight's soak didn't resize the
overlay under load so it never fired. **Fix:** in `SoftwareOverlaySurfaceBridge.TryConsume`
snapshot `(buffer, byteCount, width, height)` as one tuple under `_sync`; in `UploadFrame`
clamp the copy to `min(buffer.Length, w*4*h)` and never let `w/h` originate from a
different read than the buffer. ~1h. **Highest-priority post-release item.**

**P0-2 — [crash-class] `ClientObjectHooks` cross-thread TOCTOU / main-thread gate gap.**
`ClientObjectHooks.cs` — only `:798` and `:1125` gate on `MainThreadGuard.IsOnMainThread()`;
the other ~25 `IsReadablePointer`→`Marshal.Read*` qualities/skill/attribute sites do not
(e.g. `:642, :822-844, :1200-1221, :1385, :1463, :1502, :1584, :1636-1669`). The plugin
tick now runs on `RynthCore.NormalPluginPump` (off AC's main thread by design), so any
plugin-driven classification that reaches these reads is cross-thread against AC's
non-thread-safe qualities sub-tables — the documented `0x00416C86`/partial-qualities/
"skills read 0" signature. **Note:** the shipped player-skills fix (main-thread snapshot
cache via `PrefetchPlayerSkills` from EndScene) already covers the *player-skill* path
specifically; this is the *broader* class for the other ~25 reads, not a regression of
that fix. **Fix direction:** uniformly gate every plugin-reachable accessor on
`IsOnMainThread` (fail-closed) or serve from a main-thread snapshot like skills already do;
longer-term an SEH read trampoline (already noted as the planned structural fix in the
partial-qualities memory). Larger effort — scope as its own session; do not blind-patch.

**P0-3 — [crash-class watch-item] SmartBox / packet `[UnmanagedCallersOnly]` detours.**
`SmartBoxHooks.cs:109,150`, plus lighter `RawPacketHooks`, `PlayerVitalsHooks`,
`UpdateObject/VectorUpdate` dispatch. Same structural shape as the bug you fixed today
(managed reverse-P/Invoke on an AC-owned thread doing GC-capable work), but pre-existing,
broadly try/caught, and clean for the whole soak. **No action now.** If a future dump
shows `RhpReversePInvoke` with a SmartBox/packet frame, move the heavy body to a queue
drained by the pump thread (same remedy as the plugin-tick fix). Documented so it isn't
re-investigated from scratch.

---

## P1 — High-value functional / architectural

**P1-1 — Activity-Arbiter migration, Steps 3→5.** `ACTIVITY_ARBITER_PLAN.md`.
Verified actual state: **Step 2 (Nav↔Combat authoritative) is DONE** — `ActivityArbiter.ApplyStep2`
(`ActivityArbiter.cs:125`) is sole writer of `Combat/Navigating/Default`; `CombatManager`
no longer writes BotAction. **Steps 3/4/5 are NOT done.** 17 live `BotAction` writes remain
to remove (BuffManager 8, CorpseOpenController 5, NavigationEngine 1, OnTick gap-fills 3).
Design diverged from the plan: predicates are inline in `OnTick` via an `ArbiterInputs`
snapshot, not per-subsystem `WantsToRun()` — functional but Step 5 has no clean seam.
Concrete targets (one step per session, test between — do **not** one-shot):
- **Step 3 (Buffing):** remove `"Buffing"` from `legacyOwnsString` (`ActivityArbiter.cs:138`);
  delete OnTick buff gap-fill (`RynthAiPlugin.cs:427-436`) + buff pause block (`:438-455`);
  delete 8 BuffManager writes (`BuffManager.cs:363,370,388,394,402,419-420`). **Must move
  the "pending cast holds Buffing" logic into the pure predicate** (e.g.
  `NeedsAnyBuff() || _pendingSpellId != 0`) or the "You're too busy" lock returns.
  **Constraint: Buffing stays arbiter priority 5 / top (`Decide` `:67`) — do not reorder.**
- **Step 4 (Looting+Salvaging):** make arbiter authoritative for `Looting/Salvaging`;
  delete salvage gap-fill (`RynthAiPlugin.cs:496-515`); delete 5 CorpseOpenController
  writes (`:354,:387,:1125,:1635` + the "retain Looting" contract `:1454`); add pure
  `HasLootWork`; replace corpse-controller `HasTargets` gates (`:320-326,:428-434`) with
  `HasEngageableTarget`.
- **Step 5 (delete cascade):** delete the 4 pause flags + `_combatEndedAt`/`LootGraceMs`
  grace logic (`RynthAiPlugin.cs:319-323,585-636`); delete `NavigationEngine.cs:194-195`
  self-promote; `shouldNav` (`NavigationEngine.cs:143-146`) reads `arbiter.Current` not the
  string; collapse `OnTick:438-639` to inputs→Decide→winner.Execute()+always Think().

**P1-2 — [high] `BoostLootPriority` combat-starvation trap.** `RynthAiPlugin.cs:563-568`.
Exact shape of the `BoostNavPriority` bug you fixed this morning, still unfixed and
**with no escape hatch**: when `BoostLootPriority` is on, `_combatManager.OnHeartbeat()`
is gated behind `!IsCorpseNavigationClaimActive` (`CorpseOpenController.cs:1058`), which is
true whenever `BotAction=="Looting"` or any unlooted corpse is in range — so a mob beating
on you while a corpse sits nearby never gets combat ticks. **Fix:** mirror the
`navActiveForBoost` fix — bypass the loot-claim gate when
`_combatManager.HasEngageableTarget`. ~30m, isolated, high user-visible value. Do this
even before Step 4 folds it into the arbiter.

**P1-3 — [high] Buff §4: `GetArchmageEnduranceCount` still stubbed to 0.**
`BuffManager.cs:860-864` returns `0`; `GetCustomSpellDuration` still hardcodes
1800/2700/3600/5400 (`:866-875`). On a fully-aug'd archmage the bot thinks Impen lasts
~60m when it's ~108m → recasts far too early, wastes mana, and interleaves needless
buffing with combat. Read aug count from `AppraisalHooks` cache (stype 238 on player id —
plumbing already exists per the Buff review §4). ~30m. Highest-value Buff correctness item.

**P1-4 — [high] Launcher: `SessionStateStore.TryReadForProcess` hard-returns null.**
`SessionStateStore.cs:59`. Verified: the `return null;` is real and intentional, but its
KNOWN-DEFERRED comment only reasoned about the *stuck-client reaper* — it predates the
Thwargle-parity features (auto-relaunch, window-position, title-rewrite) that were since
built and silently depend on this read path. Because `RynthCore.App` is `<Compile Include>`
-shared into the launcher, the **launcher** (a normal managed process — the AC-side AV
cannot occur there) never sees `IsLoggedIn=true`, so:
- Auto-relaunch (punch #2): a 45-min mid-session crash is misclassified as a normal close
  → no relaunch (`MainWindow.axaml.cs:2099,2120`).
- Window-position (punch #4): early-`return` at `MainWindow.axaml.cs:2205-2207` fires every
  tick → placement never applied or saved. 100% coded, 0% functional.
- Title-rewrite (punch #3): can't reflect the actually-logged-in character.
**Fix:** give the launcher a reader that doesn't hit the deferred path — e.g. gate the
`return null` on "am I inside acclient.exe" (process-name check) so the engine keeps its
workaround and the launcher reads normally; or a launcher-only static. ~2-4h incl.
re-testing 2/3/4. **This is the biggest Thwargle-retirement step for the effort.**

**P1-5 — [medium] `DecalDetection.ProbeOnce` uses `Process.GetCurrentProcess().Modules`
inside the injected client.** `DecalDetection.cs:52-53`, called at engine init
(`EntryPoint.cs:502,550`). This is the documented `System.Diagnostics.Process` NativeAOT
AV pitfall, on the *critical* D3D9-vs-coexistence init path. Survives today only because
`.Modules` is less fragile than `GetProcessById` and it's try/caught + one-shot.
**Fix:** replace with Win32 `EnumProcessModules`/`GetModuleFileNameEx` (or PEB walk) —
same technique as `PluginLoader.IsPidAliveWin32`. Also `SessionStateRegistry
.GetProcessStartTimeUtc` (`SessionStateRegistry.cs:135`) is the same class, lower
frequency. ~1-2h together.

**P1-6 — [high] `LaunchContextStore.WriteLegacy` last-writer-wins race on parallel
launch.** `MainWindow.axaml.cs:1080` + `LaunchContextStore.cs:54-59`. Every account writes
the single `launch_context.json` in the prepare loop before staggered tasks; the last
account clobbers earlier ones, so a consumer of the legacy file can attribute the wrong
account/character to a client during the launch window. PID-specific `WriteForProcess` is
authoritative, so **fix = drop `WriteLegacy` from the multi-account path** (or only write
it for single-account launches). ~30m.

---

## P2 — Correctness & robustness backlog

Consolidated still-open items from the four reviews (verified against current code) plus
new engine findings. Each is independently shippable.

### Engine
- **[high] `Nav3DRenderInjector.Detour` runs managed Nav3D render on AC's render thread**
  inside the `DrawIndexedPrimitive` detour (`Nav3DRenderInjector.cs:82-93`). Last
  render-thread managed-work offender after the plugin-tick move. `catch{}` does not stop
  a NativeAOT fail-fast. **Fix:** detour sets a flag only; render Nav3D from the existing
  EndScene call site. ~half day.
- **[high] Fixed RVAs without pattern-scan/signature-verify** in `CObjectMaintHooks.cs:46`
  (`0x00842ADC`), `RawPacketHooks.cs:21` (`0x007935AC`), `PlayerVitalsHooks.cs:137-145`.
  Violates the CLAUDE.md "never ship decompile RVAs" rule; silent wrong-memory reads on
  any acclient build drift. `CreateObjectHooks.cs:45` already does it right — copy that
  pattern. Biggest build-drift robustness gap.
- **[medium] Loader `Reload()` leaks the engine module (~26MB/gen) and drains prior-gen
  threads with fixed sleeps, not joins** (`Loader/EntryPoint.cs:456-476`,
  `EngineLifecycle.cs:115-144`). This is the gen-8 hot-reload-death driver. Structural fix
  = real thread joins (Avalonia STA, plugin pump, HeartbeatLogger, watchers) before
  re-init, i.e. the long-outstanding **hot-reload Path A** (move MinHook ownership into
  the loader). Band-aid holds ~7 reloads; only gen2 was reached tonight, so not
  release-blocking. 1-2 day dedicated session when ready.
- **[medium] `RecursionGuard.Tick` is a permanent no-op still called per detour**
  (`RecursionGuard.cs:52-53`). Dead instrumentation masquerading as live coverage —
  re-enable behind a flag or delete the class + call sites.
- **[low] Injector stale `net9.0-windows` probe paths** (`EngineInjectionService.cs:567-571`);
  10s init wait treats non-zero loader codes as success (`:286-294`). Cleanup.
- **[low] `OverlayFrameBuffer.Submit` per-frame `byte[]` alloc** (`OverlayFrameBuffer.cs:48-50`)
  — re-introduces the LFH pattern *if* still referenced. Confirm dead → delete (memory
  already flagged `OverlayFrameBuffer` as zero-call-site) or pool it.

### Combat (all ✅ items verified present; these are the still-open ⬜)
- `Think()` still one ~200-line method (`CombatManager.cs:565`) — pipeline-of-phases
  refactor (review §2.2d, 4h).
- Magic-number tunings still `const` in combat code (`:108` etc.) → settings (§2.2f, 1h).
- No `FacingController`; facing logic inline in Think (§2.2g, 2h).
- `SCAN_INTERVAL_MS`→`SCAN_DEBOUNCE_MS` rename + diagnostic counter resets (§4.6/4.7, 30m).

### Buff
- **[medium] `IsArmorEnchantment` still a name whitelist** incl. the
  `"Swordman's"/"Swordsman's"` double-typo (`BuffManager.cs:576-587`) → use
  `spell.Targets == Item` (review §2). 1h.
- **[medium] clear-then-rebuild race in `Refresh*`** still present (`BuffManager.cs:909,946`,
  no atomic swap) — readers can see a transient empty buff list (§6). 30m.
- §7 phantom-timer is **mostly fixed** (chat-driven withdrawal added at
  `BuffManager.cs:1278-1341`) — verify it covers resist as well as fizzle/fail/too-busy;
  not a positive-confirmation model but adequate.

### Salvage
- **[medium] `IsCarriedByPlayer` Container==0 false-positive** (`SalvageManager.cs:913,917`)
  — ground items read as carried (§2.1). 15m.
- **[low] `IsUst` still `Contains("Ust")`** not `EndsWith` despite the comment claiming
  EndsWith (`SalvageManager.cs:932`) — matches "Just"/"Lustful" (§2.2). 5m.
- **[medium] no top-level phase watchdog** (§3.2) — a wedged `WaitingForResult` is
  permanent until plugin restart. 1h.
- **[low] no `GetStateSnapshot()`** (§8.2) — session counters exist but no queryable
  state for `/ra salvage`. 30m.
- **Resolved:** salvage-band-mismatch is **fixed** (banding now opt-in; same-material
  bags merge per ACE — `SalvageManager.cs:709-763`). The memory note
  `rynthai_salvage_band_mismatch.md` can be marked closed.

### Meta — all crash-class items DONE; remaining are correctness/structure
Verified done: §2.1 `MaxEvalDepth=64` (`ExpressionEngine.cs:48,170`), §2.2 `MetaRulesLock`
(`MetaManager.cs:281` + 3 renderer sites), §2.3 ERR:-fail-closed (`MetaManager.cs:567-574`),
§2.5 `tboha` path removed, plus bonus per-state index + `RegexCache` + `_lastExprError`.
**Still open (none crash-class):** §2.4 `delayexec` on threadpool thread vs AC memory;
§2.6 lossy `.af` round-trip (`_LE`→`_GE`); §3.1 `Think()` called from 5 sites; §3.4 no
`GetStateSnapshot()`. These are the Week-1/Week-2 items in `MetaManager_Review.md §6` —
follow that table; nothing urgent.

### Raycasting (fresh — no prior review)
Structurally safe: no off-thread AC reads (uses SDK pose APIs + on-disk `.dat`, game-thread
serialized), no unbounded recursion (`DatDatabase` iterative + bounds-checked + locked),
no per-frame alloc (poly buffers cached per-landblock). **Only finding [low]:** higher-level
geometry caches (`GeometryLoader`, `DungeonLOS._floorPolyCache`, `ScatterSystem`) are plain
`Dictionary`, safe only by the game-thread-only assumption — add a one-line asserting
comment to pin it before someone calls LOS from a background nav precompute.

---

## P3 — Cleanup / observability / Thwargle polish

- **Launcher Thwargle parity status** (verify-confirmed): #1 stagger DONE; #5 prefs-swap
  DONE (but `_userPrefsSwapLock` serializes "parallel" launch whenever per-account prefs
  are used — Tom's normal config → ~15-30s serial startup; optimization, not correctness,
  `MainWindow.axaml.cs:1150-1208`); Phase-2 Decal injection DONE/exceeds Thwargle. #2/#3/#4
  coded but dead pending P1-4. After P1-4, ~1-2h of re-test + the title-format decision
  (`{Account}/{Character}@{Server}` in code vs `{Account}@{Server}-{Character}` in the
  punch list — confirm with Tom).
- **[low] `EngineInjectionService.FindTargetProcesses` returns undisposed `Process[]`**
  re-queried multiple times per 2s tick (`EngineInjectionService.cs:98-101`, consumed
  `MainWindow.axaml.cs:1690,2037,2547`). Launcher-side (Process allowed there) — slow
  handle pressure over a multi-box day. Dispose + query-once-per-tick. 30m.
- **[low] doubled-`Runtime` plugin shadow path** (`PluginManager.cs:251-259`) — cosmetic
  for the default deploy, wrong for staged/dev layouts. Walk up for any `Runtime` segment.
- **[low] `MaybeKillStuckClients` reaper** depends on the dead `IsLoggedIn` signal
  (`MainWindow.axaml.cs:1786-1886`) — off by default; auto-resolves with P1-4; re-test
  before enabling.
- **RynthAi DAT path** still hardcoded `C:\Games\RynthCore\AcClient` — plumb from launcher
  `AcClientPath` through `EngineSettings` (~1h, low, no symptom).
- Observability theme recurring across every review: add `GetStateSnapshot()` to Salvage
  and Meta (Combat/Buff have them). Cheap, makes the next bug visible instead of guessed.

---

## Cross-references
- Arbiter migration detail: `ACTIVITY_ARBITER_PLAN.md` (steps unchanged; status corrected
  above — Step 2 done, 3-5 pending, 17 writes remaining).
- Per-manager problem statements: the four `*_Review.md` docs. This plan only updates their
  *status* and priority ordering; it does not supersede their analysis.
- Crash-class engine history & rules: memory `rynthcore_crash_investigation.md`,
  `rynthcore_nativeaot_pitfalls.md`, `rynthcore_overlay_lfh_pitfall.md`,
  `rynthcore_hot_reload_architecture.md`. P0-1/P0-2/P1-5 are new latent instances of those
  documented classes — not regressions of the shipped fixes.

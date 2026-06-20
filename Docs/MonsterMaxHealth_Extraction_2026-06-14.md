# Extracting Monster MAX Health from the AC Client — Definitive RE Findings

**Date:** 2026-06-14
**Method:** Multi-agent decompile sweep (40,307-fn Ghidra output) + ACE server-source cross-check (`C:\Projects\ACE`) + live RynthCore engine audit. 7 investigation angles → 9 candidate strategies → adversarial 3-lens verification (data-exists / crash-safety / ACE-reality).
**Verdict tally:** confirmed-core = S1+S6 (+S3 backup, S9 fallback); rejected = S2, S4, S5, S7.

---

## 1. TL;DR

A monster's **absolute** max health reaches the client on **exactly one opcode**: `0x00C9 IdentifyObjectResponse`, inside the `AppraiseInfo`'s **`CreatureProfile`** sub-block. The streaming combat-health packet (`0x01C0 UpdateHealth`) is **ratio-only** — ACE divides `Current / MaxValue` to a float server-side *before* sending, so the absolute is destroyed on the server and never reaches you.

**Therefore: combat alone never tells you a mob's max HP. You must appraise it (send `0xC8`).** The good news: **a *failed* assess roll still delivers true `Health` + `HealthMax`** (only the attribute/stam/mana block is skill-gated), so no `AssessCreature` skill investment is needed — one `0xC8` round-trip suffices.

**Recommended approach (S1 + S6):** appraise each combat target once (`0xC8`, selection-free), parse `HealthMax` from the `0xC9` `CreatureProfile`, cache by **GUID**, then track live current HP as `ratio × max` from the `0x01C0` stream for the rest of the fight. RynthCore *already has this whole pipeline built* — the observed `0/0` is a **priming/timing problem plus two concrete bugs**, not a hard limit.

---

## 2. The hard boundary (what is genuinely impossible client-side)

1. **Never-appraised, never-broadcast mob → absolute max is GENUINELY ABSENT.** Only the `0.0–1.0` ratio exists. Server-enforced: `0x01C0` payload is exactly `[u32 objectId][f32 health]` where `health = Current/MaxValue` (ACE `WorldObject.cs:610-619`, `GameEventUpdateHealth.cs:5-10`). No client read can recover what was never transmitted. *(Confirms the prior-session conclusion — it was correct for the unappraised case.)*
2. **One-shot / fast-AOE kills are unknowable in real time.** The mob dies before the injected `0xC8`→`0xC9` round-trip (tens-to-hundreds of ms) returns. Exact max for such kills only ever comes from the **persisted/learned fallback (S9)**.
3. **There is no flat stored `MaxHealth` int in `CACQualities`/`AttributeCache`.** It is always recomputed `base(block+0x04) + bonus(block+0x08)` from the Health vital block at `[cache+0x1c]`. On an unappraised ACE monster that cache/block is null/partial, so even a raw field read finds nothing — the value was never assembled.
4. **The selected/combat target has no numeric current/max UI.** The only true numerator+denominator the client reads (`gmVitalsUI @ 0x004C0B0B`) binds to a `PlayerDesc` interface that exists only for the local player and cannot be repointed at a monster.
5. **`NPCLooksLikeObject` creatures suppress the `CreatureProfile` flag** (`AppraiseInfo.cs:715`) — they never deliver `HealthMax`. Irrelevant for huntable mobs.

---

## 3. Strategies (ranked) + adversarial verdicts

### ✅ CORE — adopt these

**S1 — Capture absolute max from the appraisal `CreatureProfile` (`0xC9`)** — *conditional / viable-with-caveats (high confidence)*
The authoritative, AV-free max source. Send `0xC8 IdentifyObject` (selection-free; payload is just the 4-byte GUID — never touches `SelectedIdGlobal 0x00871E54`). ACE replies with `0xC9` → `AppraiseInfo`, which for **any** Creature (non-`NPCLooksLikeObject`) **always** includes a `CreatureProfile` (`AppraiseInfo.cs:112-113` unconditional). Wire layout (VERIFIED `CreatureProfileExtensions.Write:81-99`):
```
[u32 Flags][u32 Health=Current][u32 HealthMax=MaxValue]   <- always present
[10× attribute/stam/mana dwords]  only if Flags & 0x8 (ShowAttributes)   <- skill-gated
[u16 highlights][u16 colors]      only if Flags & 0x1 (HasBuffsDebuffs)
```
`Health`/`HealthMax` are assigned **before** the `if(!success) return;` (`CreatureProfile.cs:52-55`) — a failed roll still delivers true max.

**S6 — Live current HP = streamed ratio × cached max** — *conditional / viable-with-caveats (high)*
Once S1/S3 gives you a max (stable for the creature's lifetime), multiply the per-tick `0x01C0` ratio by it for accurate absolute current HP all fight, no re-appraisal. **Correction to the synthesis prose:** ACE auto-re-broadcasts to `selectedTargets` on every damage tick/heal after a *single* `0x01BF QueryHealth` — you do **not** need to keep re-issuing it. One query per fight is enough.

### 🟡 BACKUP / FALLBACK

**S3 — Read the transient `CreatureAppraisalProfile` in-frame from the existing `AppraisalHooks` detour** — *conditional; crash-safety rated viable (cleanest of all)*
Same data as S1, read from the native struct instead of the raw wire. In the existing `Handle_Appraisal (0x006B05B0)` detour, null-check `*(AppraisalProfile+0x08)`; if non-null read `+0x28 = HealthMax`, `+0x1C = Health` (VERIFIED `CreatureAppraisalProfile::UnPack 0x005B7240`, `InqAttribute2nd 0x005B6ED0`). **Key advantage: it is NOT success-gated** — it captures `HealthMax` on failed-roll appraisals that the current wire parser throws away (see bug #1). Recommended as belt-and-suspenders alongside S1.

**S9 — RynthAi's persisted learned max (fallback for one-shots)** — *already built*
`CreatureProfileStore` (`creatures.json`, name|wcid, "max observed") + `MonsterDamageStore` `HpPool`/`HpManual`. `CombatManager.ResolveFightHp (cs:1312-1368)` already chains: manual override → appraised `CreatureProfile.MaxHealth` → learned `HpPool` → else fire QueryHealth. The only answer for one-shot kills. **Correction to an earlier caveat:** persisted `CreatureProfile.MaxHealth` is *not* a "~50 stub" risk — it is written only from a `maxHealth > 0` capture sourced from the appraisal `CreatureProfile`, so stored values are exact when seeded (`RynthAiPlugin.cs:972-973,1227`). The only suspect provenance is the now-rejected S2 InqAttribute2nd fallback inside `QueryHealthResponseDetour` — distrust/disable that path so a fabricated value can't poison the store. `HpPool` itself is an approximate damage-to-kill EMA, as designed.

### ❌ REJECTED (with proof — do not pursue)

**S2 — SEH-guarded `InqAttribute2nd(MaxHealth)` on the monster** — *REJECTED (data-exists + ace-reality both not-viable, high)*
ACE never serializes a monster's vital sub-table to a remote viewer. Live-log proof: **0** qualities-read successes across all session logs, **26,155** `hp=0/0` samples; the engine already rejected this (`SmartBoxHooks.cs:324-328`: "creature Inq maxes are unreliable on ACE and fabricated wildly wrong values, e.g. 50/50 for a 190-hp mob"). It cannot conjure data the server never sent, and where the block *is* populated, S1 already gives the answer with no AV risk. *(SEH trampoline stays — just don't rely on this path for monster HP.)*

**S4 — Harvest a `UpdateAttribute2ndLevel` broadcast stream** — *REJECTED (not-viable, high)*
`GameMessagePublicUpdateVital` (the only GUID-carrying vital broadcast) is constructed **nowhere** in `ACE.Server` — dead code. Monster vital changes are server-internal except the `0x01C0` ratio. The `SeenCur|SeenMax` pair in `PlayerVitalsHooks` can never complete for a monster.

**S5 — `TryGetTargetVitals` cache read alone** — *REJECTED as an independent source*
Pure cache reader; returns `0/0` on miss and never self-populates. It's the *consumption* end of S1/S3, not a source. (Also note: the two live consumers, `RynthAiPlugin.cs:1230` / `LegacyDashboardRenderer.cs:1403`, currently discard the health out-params and read only stam/mana.)

**S8 — Combat-derived solve `max = damage ÷ ratio-drop`** — *REJECTED (data-exists + ace-reality both not-viable, high)*
Verified unworkable for this playstyle: (a) **one-shot kills produce no non-lethal before/after ratio pair** — the only transition is to ratio 0 at death (`Creature_Vitals.cs:53`); (b) monster **regen** (`VitalHeartBeat` ~5s → `OnHeal` → an *up* ratio broadcast) injects deltas uncorrelated to any hit, corrupting the denominator (`Creature_Vitals.cs:148-152`); (c) **float quantization** — relative error ≈ `1/damage`, so small-fraction hits on high-HP mobs are low-information exactly when needed; (d) **war/void magic (your primary combat) sends NO `0x01B1` exact-damage packet** — chat-only (`SpellProjectile.cs:814`), and this server's melee log carries no damage numbers either. Useless as primary; not worth building even as a fallback.

**S7 — `gmVitalsUI` numerator/denominator** — *REJECTED (dead end, recorded so it isn't re-investigated)*
`PlayerDesc`-bound, local-player only. No selected-creature numeric vitals UI exists in retail AC.

---

## 4. Recommended implementation (minimal, isolation-respecting)

The pipeline exists end-to-end (`AutoIdService → CombatActionHooks.TryParseIdentifyResponse → ObjectQualityCache → QueryHealthResponseDetour → RynthCoreHost.TryGetTargetVitals → RynthAi stores`). Three targeted fixes make it reliable:

> **⚠ IMPLEMENTATION CORRECTION (2026-06-14, after reading the live code):** Fix #1 as framed below targets `CombatActionHooks.TryParseIdentifyResponse`, but that function is **dead code on ACE** — its `InnerDispatcher` caller is disabled (`CombatActionHooks.cs:315`) and its `SmartBoxHooks` caller was removed 2026-05-18 (main-thread `[UnmanagedCallersOnly]` + `ObjectQualityCache` dict-growth → GC → `STATUS_FAIL_FAST`). So editing line 743 is a no-op. The **actual fix shipped** is the un-gated S3 path: a new `AppraisalHooks.CacheCreatureVitals` in the *live* `SendNoticeDetour` (hook on `0x006B05B0`), which reads `CreatureAppraisalProfile` (`profile+0x08`) at the decompile-verified offsets and publishes to `ObjectQualityCache.SetCreatureVitals` + `PluginManager.QueueUpdateHealth`. It is inherently un-gated, so it already captures failed-roll appraisals — no success-gate edit needed. ✅ built + deployed (`Runtime\RynthCore.Engine.dll`), pending live-verify. The original Fix #1 analysis is retained below for the record.

### 🐛 Fix #1 (THE important one) — stop discarding `HealthMax` on failed assess rolls
`CombatActionHooks.cs:743`:
```csharp
if (success == 0 || objectId == 0) return;   // <-- bails BEFORE reading CreatureProfile
```
On ACE, `Success` is an RNG roll (`AssessCreature` vs `Deception`, `Player.cs:322-330`), so for any mob above your combat char's assess skill this **throws away the `HealthMax` that is sitting right there on the wire**. **Fix:** extract `Health`/`HealthMax` from the `CreatureProfile` *before* the success gate (the value is present regardless of `success`). This single change is what makes appraisal-based max reliable without skill investment — and it's the likely real reason past attempts saw `0/0` even after the bot fired RequestId.

### 🐛 Fix #2 — marshal the auto-assess send to AC's main thread + prioritize the combat target
`AutoIdService.DrainTick (cs:100)` calls `RequestId` on a **Timer thread-pool thread**; `RequestId (cs:421)` is **not** marshalled to AC's main thread, so the native `0xC8` send (`Proto_UI::SendToWeenie 0x005473D0`) does AC heap allocation + a non-atomic shared UI-counter increment off-thread — the off-thread-mutation crash class. **Fix:** route the send through the existing main-thread marshalling (UseTime-drain idiom). While there, give the **current combat target** priority in the queue (today it's a plain 30/s FIFO round-robin with no combat priority, so a same-frame kill outruns it).

### ➕ Add — S3 in-frame capture as the un-gated backstop
Extend the existing `AppraisalHooks.SendNoticeDetour` on `0x006B05B0` to read `*(profile+0x08)` → `+0x28`/`+0x1C` and write into `ObjectQualityCache.SetCreatureVitals` keyed by GUID. Pure additive null-checked read of the engine's own live stack object; crash-safety rated it the cleanest path. Catches failed-roll appraisals independent of the wire parser.

### Then — S6 surfaces live current HP
With max cached by GUID, `currentHealth = round(maxHealth × clamp(ratio,0,1))` in `QueryHealthResponseDetour` (already parses `targetId@+4`, `ratio f32@+8`). Plugins read it via `TryGetTargetVitals`.

**Guardrails (from the audit):** keep `AllowNonPlayerQualities = false` (flipping it re-enables the unwrapped `Inq*` AV class — and S2 is rejected anyway); key **all** max caches by **GUID**, never by pointer (`_maxHealthByPtr` was removed 2026-06-11 — heap ptrs get reused); throttle/defer the auto-assess away from the login/spawn window (`gmVitalsUI::Update` login-burst crash class, see `rynthcore_vitals_login_crash`).

**Must be live-verified:** (a) failed-roll `HealthMax` actually populates after Fix #1; (b) main-thread-marshalled `0xC8` is crash-clean in a busy farm; (c) `ratio × max` tracks correctly across a full fight. Needs a clean no-Decal client.

---

## 5. Appendix — key addresses / offsets / packet layouts

| Item | Value | Notes |
|---|---|---|
| `0x00C8 IdentifyObject` (send) | `CM_Item::Event_Appraise 0x006A94A0`; UI `ExamineObject 0x005657B0` | engine `CombatActionHooks.RequestId`; payload = 4-byte GUID only (selection-free) |
| `0x00C9 IdentifyObjectResponse` | `gmGlobalEventHandler::Handle_Appraisal 0x006B05B0` | the ONLY opcode carrying absolute creature `HealthMax` |
| `0x01BF QueryHealth` (send) | `CM_Combat::Event_QueryHealth 0x004C0068` | registers in `selectedTargets`; ratio-only reply |
| `0x01C0 UpdateHealth` | `CM_Combat::DispatchUI_QueryHealthResponse 0x0056B2A0` | wire `[u32 objectId][f32 ratio]`; engine parse `CombatActionHooks.cs:885` (`id@+4`, `f32@+8`) |
| Appraisal unpack (stack-local) | `UIQueueManager::ProcessNetBlobData 0x0055BCD0` (case 0xc9) → `AppraisalProfile::UnPack 0x005B4A30` | transient; copy out in-frame |
| `CreatureAppraisalProfile::UnPack` | `0x005B7240` | `Health→+0x1C`, `HealthMax→+0x28` (unconditional); attrs gated by wire-flag `0x8` |
| `CreatureAppraisalProfile::InqAttribute2nd` | `0x005B6ED0` | confirms case1=`+0x28`, case2=`+0x1C` |
| `AppraisalProfile+0x08` | → `CreatureAppraisalProfile*` | set only when top-level UnPack flag bit `0x100` present (`005B4A30.c:149-159`) |
| `CACQualities::InqAttribute2nd` | `0x00592D20` (uint), `0x005927F0` (struct overload) | computes `base+bonus`; **AVs** on partial monster tables |
| `AttributeCache::InqAttribute2nd` | `0x005CD520` / `0x005CDA20` | case1: `block=[cache+0x1c]`, returns `*(block+8)+*(block+4)`; **no inner null-check** |
| `CACQualities` layout | weenie `+0x14C`; `+0x38` CBaseQualities; `+0x60` attribute-cache ptr | MaxHealth path derefs `[this+0x60]`→`[cache+0x1c]`→`+0x04/+0x08` |
| `PropertyAttribute2nd` stype | 1=MaxHealth, 2=Health, 3=MaxStamina, 4=Stamina, 5=MaxMana, 6=Mana | |
| Selection global | `SelectedIdGlobal 0x00871E54`; `GetAttackTarget 0x0056B630`; `SetSelectedObject 0x0058D110` | `0xC8` does NOT touch these |
| `gmVitalsUI` (player only) | update `0x004C0B0B`/`0x004C0B64`/`0x004C0C67`, vtable `0x007B5DA0` | binds `PlayerDesc`; not usable for monsters |

**ACE source anchors:** `AppraiseInfo.cs:112-113,715-716,737-756` · `CreatureProfile.cs:47-55` · `CreatureProfileExtensions.Write:81-99` · `GameEventUpdateHealth.cs:5-10` · `WorldObject.cs:610-619` · `Player_Vitals.cs:171-173` · `Player.cs:322-330` (assess roll).

**RynthCore anchors:** `CombatActionHooks.cs` (`:421` RequestId, `:734` TryParseIdentifyResponse, `:743` success gate ⚠, `:800-801` SetCreatureVitals, `:885` ratio parse, `:903-934` SEH fallback) · `AutoIdService.cs` (`:21-24,86-105` drain) · `AppraisalHooks.cs` (`Handle_Appraisal` detour) · `ObjectQualityCache.cs` · `PlayerVitalsHooks.cs` (`:296-311` TryReadCreatureMaxHealthSafe, `:418-544` ForwardCreatureHealth) · `RynthCoreHost.cs:705` TryGetTargetVitals · `PluginManager.cs:3150-3173` GetTargetVitals · `CombatManager.cs:1312-1368` ResolveFightHp.

> Foundational fact: `acclient.exe` is byte-identical to retail, so every retail VA above is valid live, and ACE's `CreatureProfile` field order matches the client's `UnPack` expectation field-for-field.

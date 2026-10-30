# RynthAi Activity Arbiter — rewrite plan

## STATUS: steps 1-5 all landed 2026-09-04 — NOT yet soaked

All five migration steps are in code. `ActivityArbiter.Apply` is the sole writer
of every `BotAction` string; the buff and salvage gap-fills, the four
`_*PausedNav` flags, the `IsCorpseNavigationClaimActive` nav gate, and
NavigationEngine's two self-promoting writes are all deleted. `OnTick` now asks
one question — what did the arbiter decide — and the string is a display
projection.

**Steps 4 and 5 shipped in the same build, which the Risk Controls below
explicitly warn against.** That was a deliberate scope call (2026-09-04), not an
oversight: it means a regression in the full loop has a two-step bisect surface
rather than one. Soak the patrol → aggro → fight → loot → salvage → resume loop
before trusting it, and if it misbehaves, suspect step 5 (the OnTick collapse)
before step 4 (the Looting/Salvaging migration) — step 5 changed who ticks, step
4 only changed who writes.

Remaining known deviations from the target design:
- The three UI resets (`LegacyDashboardRenderer` ×2, `LegacyNavigationUi` ×1)
  still write `"Default"` directly on stop/reset. They are cosmetic — the
  arbiter overwrites on the next tick — and were deliberately left alone.
- `OnTick` is not literally `switch(Current){ winner.Tick() }`. Manager call
  order (salvage → mana/pet → crafting → combat/corpse → nav) carries behaviour
  the plan never enumerated, so the decision gates *whether* each runs rather
  than replacing the sequence wholesale.
- `NavigationEngine.Tick`'s own `shouldNav` still reads the string. It is now
  redundant by construction (OnTick is the sole caller and only calls Tick when
  the decision is Navigating) and is kept as an inner assertion, not a second
  authority. One residual predating the migration: during a portal action with
  Combat/Looting decided, that gate takes the stop path instead of running the
  teleport detection the portal exception exists for. Closing it means deciding
  whether nav should route-walk while combat owns the tick — a live-session
  question, not a refactor.
- The Meta integration below is still deferred, and remains the right shape.
  Worth noting the migration made it easier: meta mutates `_settings` (routes,
  enables), and those are now arbiter *inputs*, so a meta write is recomputed
  into the decision on the next tick instead of fighting a written string.

## Problem (proven 2026-05-15)

`LegacyUiSettings.BotAction` is a bare `string` written from ~20 sites across 6
files (BuffManager, CombatManager, CorpseOpenController, NavigationEngine, 2 UI
files, RynthAiPlugin gap-fill). No single owner. `RynthAiPlugin.OnTick` is a
~200-line imperative priority cascade with explicit "Gap-fill" hacks and a soup
of hand-managed pause flags (`_buffingPausedNav`, `_combatPausedNav`,
`_corpsePausedNav`, `_combatEndedAt`).

Priority is emergent from (tick order) × (each manager's bespoke gate) × (who
wrote the string last this tick). "Bot just stands there" = every subsystem
inspects the string, each concludes "not my turn," nobody executes, bot idles.
Unfixable by patching: each patch is another gap-fill that moves the dead zone.

## Target design

```
enum BotActivity { Idle, Navigating, Salvaging, Looting, Combat, Buffing }
// priority low→high: Idle < Navigating < Salvaging < Looting < Combat < Buffing
// (user-confirmed 2026-05-15)
```

Single `ActivityArbiter`. Each tick:

1. `arbiter.Decide()` calls a **pure** `WantsToRun()` query on each subsystem —
   no game commands, no state writes, no BotAction mutation. Returns
   `(bool wants, string reason)`.
2. Arbiter picks the highest-priority subsystem whose `WantsToRun()==true`.
3. Arbiter is the **sole writer** of `BotAction` (kept as string only for UI /
   back-compat; derived from the winning `BotActivity`).
4. Arbiter calls **only the winner's** `Execute()`. Losers do nothing.
5. Arbiter logs every activity transition with the reason.

Structural property that kills the entire "stands there" bug class: claims are
recomputed from scratch every tick. Nothing persists. Combat cannot "hold a
lock" — no engageable target this tick → it doesn't claim → Navigation wins →
bot moves. No retained state to wedge. The pause-flag soup is deleted entirely;
"pause nav for combat" becomes "combat won the tick, nav's Execute didn't run."

## Subsystem `WantsToRun()` predicates (pure, cheap, no side effects)

- **Buffing**: `EnableBuffing && _buffManager.NeedsAnyBuff()` (already exists —
  used by the gap-fill at RynthAiPlugin.cs:385).
- **Combat**: `EnableCombat && CombatManager has an engageable target` — needs a
  new pure `HasEngageableTarget` (scanned target in range, not blacklisted, AC
  combat-actable). Distinct from existing `HasTargets` which is too broad and
  caused the squat-without-fighting bug.
- **Looting**: `CorpseOpenController has a reachable corpse with loot or a
  pending loot action` — needs pure `HasLootWork`.
- **Salvaging**: `_salvageManager.IsBusy || queue non-empty` (IsBusy exists).
- **Navigating**: `IsMacroRunning && EnableNavigation && route loaded && not at
  route end` — extract from NavigationEngine.Tick's `shouldNav` minus the
  BotAction checks (those become the arbiter's job).

## Migration order (one testable step per commit)

1. **Add `BotActivity` enum + `ActivityArbiter` skeleton.** Arbiter computes the
   winner from the predicates and writes BotAction. Do NOT yet remove manager
   writes — run arbiter in "shadow mode": it logs `Arbiter: would pick X
   (reason)` next to the real BotAction so we can compare its decision to the
   legacy cascade on real sessions WITHOUT changing behavior. **Test: confirm
   arbiter's shadow decision matches sane expectation across combat/nav/buff.**

2. **Flip arbiter to authoritative for the Navigating↔Combat boundary only.**
   Remove CombatManager's 4 BotAction writes + NavigationEngine's gate; both now
   read `arbiter.Current`. Leave Buffing/Looting/Salvage on the legacy path.
   **Test: the exact "stands there surrounded by mobs / 2-steps-and-stops"
   repro. This is the highest-value, smallest-surface step.**

3. **Migrate Buffing** into the arbiter; delete the RynthAiPlugin gap-fill
   (lines ~380-389) and BuffManager's 6 writes. **Test: buff cycle + combat
   interleave; no "You're too busy" lock.**

4. **Migrate Looting + Salvaging**; delete CorpseOpenController's 5 writes, the
   salvage gap-fill (~line 434), and all `_*PausedNav` flags. **Test: full
   loop — patrol → aggro → fight → loot → salvage → resume patrol.**
   *(DONE 2026-09-04. `HasLootWork` answers "is there loot work in range"
   rather than "have I claimed a corpse", so the decision leads the claim
   instead of lagging a tick behind it and handing nav a free step.)*

5. **Delete dead code**: the entire OnTick imperative cascade, pause flags,
   `_combatEndedAt`. OnTick becomes `arbiter.Tick()`.
   *(DONE 2026-09-04, with two deliberate deviations. `_combatEndedAt` SURVIVES
   as loot-grace: corpse CreateObjects arrive a tick or two after the kill, and
   deleting it would let nav walk away from a body that is about to exist. It is
   no longer a lock — it feeds the pure `HasLootWork` predicate, which is the
   property that mattered. And `BoostNavPriority`/`BoostLootPriority`, which the
   cascade implemented by stomping the string, became inputs to `Decide()`.)*

## Risk controls

- Step 1 is shadow-mode: zero behavior change, pure observability. Validates the
  arbiter's decisions against reality before it controls anything.
- Each subsequent step is independently testable and revertable (one subsystem
  at a time).
- The state-change logging added 2026-05-15 (`Nav: state`, `Combat: state`)
  stays — it's exactly the instrumentation needed to validate each step.
- Do NOT do steps 2-5 in one session without a test between each. The failure
  mode of this whole project is untested control-flow changes regressing a
  working state.

## Non-goals

- NOT rewriting CombatManager targeting, BuffManager spell selection, or
  NavigationEngine pathing. Those work. Only the coordination/arbitration glue
  is replaced.
- NOT touching the engine (RynthCore) — this is entirely RynthAi plugin-side.

## Deferred inbound integration: Meta (added 2026-05-17)

The meta-system review (`Docs/MetaManager_Review.md` §3.1/§3.2) found that
MetaManager is an unaccounted-for ~20th writer of `BotAction`/state: meta
actions (`SetState`, `SetRAOption`→`VtOptionMap`, `EmbedNav`) mutate
`_settings` one tick *after* the arbiter decides, so meta silently fights the
arbiter. The proper fix is to make Meta a **declared arbiter input** (a
high-priority claimant when a meta rule wants to force a state), not a post-hoc
settings mutator.

This is intentionally **deferred** — it is NOT one of steps 2-5 above and must
not be bolted onto a half-migrated arbiter mid-soak (same failure mode the Risk
Controls warn about). Sequence it as its own step **after** steps 2-5 land and
soak clean: add a `Meta` claimant to the arbiter with priority above the
operational activities (a fired meta `SetState` should win over Combat/Nav
cycling), and delete meta's direct `_settings` writes in favour of an arbiter
claim. Until then, the meta-review Day1-Week1 changes (locking, recursion guard,
schema, observability) are independent and already shipped.

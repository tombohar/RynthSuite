# RynthAi Activity Arbiter — rewrite plan

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

5. **Delete dead code**: the entire OnTick imperative cascade, pause flags,
   `_combatEndedAt`. OnTick becomes `arbiter.Tick()`.

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

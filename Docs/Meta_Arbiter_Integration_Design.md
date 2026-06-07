# Meta ↔ Activity-Arbiter Integration — Design (2026-06-06)

**Status: DESIGN ONLY — awaiting sign-off before any code (Tier 3 of the meta deep-dive).**

## Context

RynthAi runs two independent state machines that share storage:

- **Activity Arbiter** (`ActivityArbiter.cs`) → owns the operational `BotAction` string.
  Pure function of `ArbiterInputs` (wantBuff/Combat/Loot/Salvage/Nav), recomputed every
  tick (`ApplyStep2`, `RynthAiPlugin.cs:554`). Priority low→high:
  `Idle < Navigating < Salvaging < Looting < Combat < Buffing`.
  **Authoritative for Combat / Navigating / Default only**; Buffing / Looting / Salvaging
  strings are still written by their legacy managers (arbiter plan steps 3-4 pending).
- **Meta engine** (`MetaManager.cs`) → owns the user-authored `CurrentState` FSM.
  Fires actions: SetState/CallState, EmbedNav, ChatCommand (`/vt opt set`),
  ExpressionAction, watchdog. Runs late in the tick (`Think()`, `RynthAiPlugin.cs:770`).

## Meta already influences operations (it is NOT fully disconnected)

- **EmbedNav** sets `CurrentRoute` + `EnableNavigation=true` + `ActiveNavIndex` → next tick
  `wantNav` is true → arbiter grants Navigating (if nothing higher wants to run).
- **`/vt opt set enablecombat 0`** → `EnableCombat=false` → next tick `wantCombat=false` →
  arbiter won't pick Combat.

So meta drives the arbiter **indirectly, through the shared `Enable*` flags and the nav
route** — the very inputs the arbiter reads. This already covers the common
"meta turns nav on / turns combat off" cases.

## The real gaps

1. **One-tick lag.** `ApplyStep2` runs at line 554; `Think()` at line 770. Meta's writes this
   tick are seen by the arbiter *next* tick (~16–33 ms). Negligible except for very tight
   hand-offs.
2. **No explicit, time-boxed meta claim.** Meta can only flip *coarse* `Enable*` flags
   (disable combat globally). It can't say "hold everything below priority X for the next N ms
   while I run a precise sequence, then release." That is the missing capability.
3. **Half-migrated arbiter.** The arbiter owns only Combat/Nav/Default today. A meta claim that
   needs to preempt Looting/Salvaging/Buffing can't be honored cleanly until those strings are
   arbiter-owned (steps 3-4).

## Target design (end-state)

Add an optional meta-originated claim to `ArbiterInputs`:

- New field `MetaClaim = (BotActivity activity, long untilTickMs)` (None when unset).
- New meta expression actions: `raclaimactivity[<activity>, <seconds>]` sets a time-boxed
  claim on the engine; `rareleaseclaim[]` clears it.
- `Decide()` honors a live `MetaClaim` at its stated priority (e.g., a claim for Navigating can
  beat Combat when the meta author wants travel to win). It **expires automatically** at
  `untilTickMs`, so a crashed/looping meta can't wedge the bot — the same self-healing
  property the arbiter already relies on.
- The claim is read at the start of the tick from a field the meta wrote on a previous tick, so
  **no OnTick reordering is required** — the field is the hand-off, and the 1-tick lag stops
  mattering because the claim is explicit and sticky.

## Sequencing recommendation

**Defer implementation until arbiter steps 3-5 are complete** (see
`rynthai_activity_arbiter` memory / ACTIVITY_ARBITER_PLAN.md):

- A claim that can preempt Looting/Salvaging/Buffing needs those to be arbiter-owned first;
  building it against the half-migrated arbiter means special-casing legacy strings — exactly
  the fragility the arbiter rewrite is removing.
- The common cases (nav on, combat off) already work via the indirect path.
- Net-new arbiter surface on a migrating subsystem is high-risk for a bot where stability is
  paramount.

The target design above is written so steps 3-5 can land "claim-aware."

## Optional interim (only if a meta you're writing *today* needs it)

`raclaimactivity` scoped to **Combat/Nav only** (the two the arbiter already owns). Additive,
time-boxed, doesn't touch legacy strings. Lower value (covers less) and still adds surface to a
migrating system. Recommend against unless there's a concrete present need.

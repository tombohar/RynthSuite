# RynthAi 0.5.66-legacy-ui - Boost Loot Priority loots each kill before the next fight

Previous release: 0.5.65-legacy-ui.

## Problem

With Boost Loot Priority on, Silentkelpie still fought two or three monsters before looting
their corpses together (2026-10-14, 22:56):

- 22:56:43.021 Old Growler killed.
- 22:56:43.027 the next target (Skitters McGee) locked, 6 ms later.
- 22:56:46 Old Growler's corpse found and claimed, but the arbiter kept Combat:
  `want[cbt=True loot=True] eng=True boost[loot=True]`.
- 22:56:54 Skitters McGee killed, then both corpses were looted.

Boost Loot Priority only lets looting outrank combat when combat is not engaged, and
"engaged" includes a locked target at any range. After a kill, combat locked the next target
in the same tick, 1-3 s before ACE creates the corpse (the death animation). So with monsters
around, the arbiter never saw a gap, and Boost Loot made no difference.

## Change

With Boost Loot Priority on, each of our kills is looted before the next fight:

- A kill (KillerNotification) starts a wait of up to 4 s (`BoostLootKillWaitMs`) for its
  corpse.
- While that wait runs, or while a corpse is claimed, open or lootable in range, combat locks
  no new target (`CombatManager.HoldNewTarget`, answered by
  `RynthAiPlugin.BoostLootHoldsNewTarget`). The log says
  `[CombatTarget] next target waits: Boost Loot Priority loots the kill first` once per wait.
- The forming corpse counts as loot work, so the arbiter gives Looting the tick and nav
  waits.
- Looting the kill's corpse ends the wait, so combat goes on right away. A corpse from an
  earlier kill doesn't end it.
- Unchanged safety: a monster within 5 yd is fought at once, and a target already being
  fought is finished first.
- No wait with Boost Loot off, Loot Only Rare Corpses on, looting off, packs full, or while
  the server is refusing uses. If the corpse never comes, combat goes on after 4 s.

## Tests

- RynthCore.RynthAiHostTests: 1005 passed, 1 failed. The failure is the known
  "map bake 0x6346" check, which needs the game data files. New cases:
  - `BoostLootOneKillOneLoot`: no target is locked while the corpse forms, the arbiter
    picks Looting, and the corpse is claimed and opened when it arrives.
  - `BoostLootHoldLimits`: the wait runs out after 4 s; a monster at 2 m, Boost Loot off, or
    Loot Only Rare Corpses means no wait.
  - `BoostLootWaitEndsOnKillCorpse`: the kill's corpse ends the wait, and an older corpse
    doesn't.
- RynthCore.RynthAiTests: 39/39.

## Install

Installed locally on 2026-10-14: RynthAi 0.5.66-legacy-ui (RynthSuite e85b49c, clean NativeAOT
publish) copied to C:\Games\RynthSuite\RynthAi. 0.5.65-legacy-ui was backed up to
PreviousReleases\0.5.65-legacy-ui. Running clients load it on a restart or the overlay's RL
(hot reload).

## Files

- `Plugins/RynthCore.Plugin.RynthAi/Combat/CombatManager.cs`
- `Plugins/RynthCore.Plugin.RynthAi/CorpseOpenController.cs`
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiPlugin.cs`
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiVersionCommand.cs`
- `Tools/RynthCore.RynthAiHostTests/Tests/LootCorpseTests.cs`

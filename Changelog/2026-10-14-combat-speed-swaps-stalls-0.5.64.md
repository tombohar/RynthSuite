# RynthAi 0.5.64-legacy-ui - fewer weapon swaps, faster point-blank stall recovery

Previous release: 0.5.63-legacy-ui.

Goes with RynthCore 2026.10.5.57 (gesture-safe attacks and target selects,
`RynthCore/Changelog/2026-10-14-gesture-safe-attacks-2026.10.5.57.md`).

## Problem

Silentkelpie (missile, several elemental crossbows) killed slowly in the
2026-10-14 logs. Silentstorm (magic) did not: about 1.7 s per cast, limited by
spawns. Kelpie's time went to three things:

- **Weapon swaps.** About 58 swaps an hour, each costing 3-4 s of no attacks,
  including ping-pong (Fire -> Piercing -> Fire crossbow 2.5 s apart,
  11:41:06-08). Causes:
  - A wield that reached the hand after the choice had moved on (a new target
    wanted another weapon) was timed out as "not seen in hand". The landing was
    never recorded, so the swap straight back wasn't held.
  - A swap started on a target that was locked 230 ms earlier, then the
    target changed and another swap followed.
  - Swaps for a target that was nearly dead.
  - Weakness swaps for a small gain (for example 1.1x over 1.0x).
- **Attacks waiting behind crossbow gestures** in the engine's action queue
  (fixed in RynthCore 2026.10.5.57).
- **Point-blank missile stalls.** 22 of 40 missile stalls were at 1.5-2.5 m. The
  watchdog's step 1 (re-issue) usually brought swings back within half a
  second, so the 6 s wait before it was nearly the whole cost.

## Change

- `WeaponSwapTracker`: a wield counts as landed when its weapon reaches the
  hand, even if the choice moved on while it was in flight. The A -> B landing
  is recorded, so the existing "just swapped away from it" and swaps-per-minute
  holds apply.
- New swap settings in Advanced Settings > Melee Combat > "Weapon Swaps" (also
  readable from meta expressions):
  - `WeaponSwapSettleMs` (default 750, 0-3000): a swap for a target locked
    less than this long ago waits. Combat holds with the weapon in hand until
    the target has settled.
  - `WeaponSwapMinHealthPct` (default 25, 0-100): a target at or under this
    health is finished with the weapon in hand, when both weapons are the same
    kind (bow for bow, sword for sword).
  - `WeaponSwapMinGainPct` (default 20, 0-200): a swap chosen only by
    weakness must beat the weapon in hand by this much (`WeaponPlanner.KeepInHand`).
    It applies only when both multipliers are known. A Monsters rule element,
    order-only (learned) weakness data, or a hand weapon that does nothing to
    the target always swaps.
  - Set any of them to 0 to turn it off.
- `CombatStallWatchdog`: missile targets within 3 m start the ladder after
  3.5 s instead of 6 s (`PointBlankStallMs`, `PointBlankMissileM`). The melee
  "quiet" early start (2.5 s) is unchanged.
- Each deferred swap is logged once per 10 s:
  `[EquipDiag] not swapping 0x.. 'X' for 0x.. 'Y' yet (why)`.

## Ammo-swap fix kept

The launcher and ammo fix (ccbbf86: the launcher swap holds the stance, the
400 ms ammo-off settle, and the ammo dequip not stamping the equip pacing) is
untouched. The new holds and delays never apply while any part of a swap is
under way (`SwapHoldsAllowed`):

- a wield in flight
- old ammo or an off-hand item on its way into the pack
- the ammo follow-up armed

A launcher in hand is only kept when the ammo wielded fits it. So a launcher is
never kept after its ammo went into the pack.

## Tests

- RynthCore.RynthAiTests: 34/34. New `PointBlank`: step 1 at 3.5 s, with
  the default still 6 s.
- RynthCore.RynthAiHostTests: 1002 passed, 1 failed. The failure is the known
  "map bake 0x6346" baseline (no dats in the worktree). New cases:
  - `SwapLandsWhenChoiceMoved` (Kelpie's Fire / Piercing case).
  - `ChoiceMinSwapGain`: threshold off, inside and outside the gain,
    order-only data, immune hand, rule element.
  - `LowHealthKeepsLauncher`.
  - `LowHealthMidSwapFinishes`: a launcher swap already under way finishes even
    when the target drops low.
  - The existing `LauncherSwapTests` and `AmmoTests` all pass.
- Every other RynthSuite and RynthCore test project passes, except the 4 known
  RynthLua ScriptWindowsTests failures (docs and icon data missing from the
  worktree).

## Files

- `Plugins/RynthCore.Plugin.RynthAi/Combat/CombatManager.cs`, `CombatStallWatchdog.cs`,
  `WeaponPlanner.cs`, `WeaponSwapTracker.cs`
- `Plugins/RynthCore.Plugin.RynthAi/LegacyUi/LegacyUiSettings.cs`,
  `LegacyDashboardRenderer.cs`, `LegacyAdvancedSettingsUi.cs`
- `Plugins/RynthCore.Plugin.RynthAi/Meta/ExpressionEngine.cs`, `RynthAiPlugin.cs`,
  `RynthAiVersionCommand.cs`
- `Tools/RynthCore.RynthAiHostTests/Tests/WeaponTests.cs`, `LauncherSwapTests.cs`
- `Tools/RynthCore.RynthAiTests/Tests/CombatStallWatchdogTests.cs`

## Install

Installed locally on 2026-10-14: RynthAi 0.5.64-legacy-ui (RynthSuite 53af975,
NativeAOT publish) copied to C:\Games\RynthSuite\RynthAi. 0.5.63-legacy-ui was
backed up to PreviousReleases\0.5.63-legacy-ui.

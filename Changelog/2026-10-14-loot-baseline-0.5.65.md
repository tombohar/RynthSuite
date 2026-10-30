# RynthAi 0.5.65-legacy-ui - loot timer baseline, logged; unused loot options hidden

Previous release: 0.5.64-legacy-ui.

## What the loot logs show

Silentkelpie on InfiniteLeaftide (ACE), 2026-10-14 12:38-13:53, loot profile `01.utl`:

- 226 corpses opened, 2191 items judged, 39 pickups, every one confirmed on the first
  try (no retries, no failed pickups, no corpse timeouts). 199 of 203 timed corpses
  opened on the first try.
- Kept: Thaelaryn Core 33, Necrolytic Extract 18, Coalesced Aetheria 1. Everything else
  was left by the profile.
- No item was misjudged for lack of an appraisal. The 52 items judged without one were all
  keepers matched by name; every item left had its appraisal.
- Appraisal (ID) replies: 127 ms median, 256 ms at the 95th percentile, 350 ms at most.
- Every open found its items already in the object cache, so Content Settle and Empty
  Corpse Wait were never waited on.
- Pickups were confirmed 1.7-2.2 s after the send (the pickup animation).
- Skipped corpses, both as set: 133 killed by another player (Loot From = My Kills Only),
  and 42 of his own kills beyond his 40 yd (36.6 m) loot range.
- His Approach Range Min is 240 yd with a Max of 40 yd; the loot code uses Max for Min.
- Silentstorm has looting off.

## Baseline

| Timer | Baseline | Was default | Why |
|---|---|---|---|
| Content Settle | 100 ms | 100 | never waited on (items already cached) |
| Empty Corpse Wait | 300 ms | 300 | no empty corpse or contents probe seen |
| Assess Window | 300 ms | 200 | above the 256 ms 95th percentile of appraisal replies |
| Loot Retry Timeout | 1000 ms | 500 | 39 of 39 pickups landed without a retry at 1000 ms |
| Corpse Open Retry | 1500 ms | 1500 | 199 of 203 opened on the first try |
| Corpse Timeout | 12000 ms | 12000 | no corpse timed out |

`LootBaseline.cs` holds these values; the settings defaults now come from it, so a new
settings profile starts at the baseline. Saved profiles keep their own values.

## Change

- **Logged.** When looting runs, the log gets a `[LootSettings]` line with each loot timer
  marked `baseline` or `custom (baseline N)`, a count, `[acts as N]` for a value under the
  code's minimum, and a note when Approach Range Min is above Max. It is written again only
  when one of those values changes. Kelpie's current settings would log:
  `Content Settle 125 ms custom (baseline 100) | Empty Corpse Wait 550 ms custom (baseline 300) |
  Assess Window 450 ms custom (baseline 300) | Loot Retry Timeout 1000 ms baseline |
  Corpse Open Retry 1200 ms custom (baseline 1500) | Corpse Timeout 12000 ms baseline --
  4 of 6 custom | Approach Range Min 240 yd is above Max 40 yd, so Min acts as 40 yd`.
- **Advanced Settings > Looting.**
  - Each loot timer shows `baseline` next to it, or `baseline N` in amber when it differs,
    and a tooltip saying what it does.
  - New **Use Baseline** button sets all six timers to the baseline.
- **Hidden, not wired.** Nothing in the corpse looting reads these, so they are no longer
  shown. The settings still load and save, so profiles are unchanged.
  - Loot Only Rare Corpses (the meta/MagTools option of that name still sets Mine Only)
  - Inter-Item Delay
  - Closing Delay

## Tests

- RynthCore.RynthAiTests: 39/39, with 5 new baseline tests (the values; the all-baseline
  line; Kelpie's custom timers and range note; a value under the floor; legacy fractional
  ranges).
- RynthCore.RynthAiHostTests: loot tests 124/124. The pickup-retry pacing test now takes its
  retry window from the settings instead of assuming 500 ms. The full run has one other
  failure, `map bake: 0x6346 from the real dats ...` ("made no checks"), which fails the same
  way on 0.5.64: it depends on the game data files on this machine.
- The plugin builds.

## Install

- Installed locally on 2026-10-14: RynthAi 0.5.65-legacy-ui (RynthSuite 81205e9, NativeAOT
  publish) copied to C:\Games\RynthSuite\RynthAi. 0.5.64-legacy-ui (53af975) was backed up
  to PreviousReleases\0.5.64-legacy-ui. Clients load it when they restart.
- Silentkelpie's loot timers set to the baseline with his client closed (Content Settle
  125 -> 100, Empty Corpse Wait 550 -> 300, Assess Window 450 -> 300, Corpse Open Retry
  1200 -> 1500). His Approach Range Min (240 yd) is left as it is. The previous file is
  `SettingsProfiles\InfiniteLeaftide\Silentkelpie\Default.json.pre-loot-baseline-20261014.bak`.

## Files

- `Plugins/RynthCore.Plugin.RynthAi/LootBaseline.cs` (new)
- `Plugins/RynthCore.Plugin.RynthAi/CorpseOpenController.cs`
- `Plugins/RynthCore.Plugin.RynthAi/LegacyUi/LegacyUiSettings.cs`, `LegacyAdvancedSettingsUi.cs`
- `Plugins/RynthCore.Plugin.RynthAi/RynthAiVersionCommand.cs`
- `Tools/RynthCore.RynthAiTests/` (csproj, `Program.cs`, `Tests/LootBaselineTests.cs`)
- `Tools/RynthCore.RynthAiHostTests/Tests/LootCorpseTests.cs`

# SK RynthAi work through 2026-10-10, rebased on 2026.10.12.1

Replaces the closed per-feature PRs RynthSuite #2-#11 and the earlier `sk` commits. This commit
carries everything from those PRs that main doesn't already have, merged onto release 2026.10.12.1
with the conflicts resolved. Work from 2026-10-11 follows in separate commits.
`HandWrittenVersion` stays `0.5.0-legacy-ui`.

The merge-stack result the looter reads is engine API v81 now (rynth's 2026.10.12.1 took v80);
`HasGetMergeStackResult` in the SDK checks that, so an older engine just reports it unavailable.

## Needs a decision: item info

Both sides had `/ra iteminfo`. This commit keeps the Mag-Tools port (settings window, field and
rating toggles, click trigger, pet or line layout, T11 fields) and folds in rynth's additions:

- `/ra iteminfo <item name>` describes an inventory item by name.
- Items RynthAi selects itself (looter, meta `selectitem`) are not described.
- The line ends with the looter's verdict (`| Loot: [Keep] rule`), as a preview.
- The default prefix is `[RynthAi Item] `, so RynthChat files it under Items.
- Rynth's "Item Info On Select" checkbox, the meta setting `ItemInfoOnSelect` and the engine's
  settings DTO drive `ItemInfoSettings.OnSelect`; an Options button opens the Item Info window.

To do that it **removes** rynth's `RynthAiPlugin.ItemInfo.cs`, `ItemInfo/ItemInfoText.cs` and
`Tests/ItemInfoTests.cs`. If rynth prefers his implementation, this is the part to drop.

## How the merge with 2026.10.11.1 was settled

- Rynth's side: pet essence refill-loop guard, typed-object filing (`WorldObjectCache`), spell
  category and power (`SpellDatabase`), Decal synthetic keys, dungeon hazards, radar walls, meta
  UI, and install paths (everything links `Plugins/Shared/RynthInstallPaths.cs`).
- Ours: the T11 loot catalog, keys and `/ra t11`, `LootItemRules`, the T11 loot tests, and
  `RynthLog` categories in loot logging.
- Both: `LootRuleText` (rynth's text plus the T11 property keys and Gear Grade table), the
  `MonsterEditor` JSON context, chat hooks, and the host test list.

## Features

- **ILT Hub** (`IltHub/`): character, quests, pets and pet stats, banking, gear and equip suits,
  games, buff keepers, guardian answers, temple attributes, sigil chain, lum income, session
  rates, USD import, and a server feature gate so ILT-only parts stay off elsewhere.
- **Attribute raiser and augmentation planner** with UB's InfiniteLeaftide cost math and
  per-server cost settings (`IltAttributeRaiser`, `IltAugCosts`, `IltProgression`).
- **HUDs** (`Huds/`): Mini Remote, combat HUD and item count HUD, with an icon cache.
- **Item info** as above (`MagItemDescriber`, `MagItemInfoSettings`, `LegacyItemInfoUi`).
- **Chat translation** (`Translate/`): per-channel auto-translation with its own settings UI.
- **Ground loot** (`GroundLootController`).
- **Nav:** route editing in the dashboard, breadcrumbs, and a route overlay
  (`NavRouteEditing`, `NavBreadcrumbTracker`, `NavOverlayRenderer`).
- **Spells:** a spell catalog generated from the client's spell table
  (`SpellCatalog.tsv`, `Tools/Generate-SpellCatalog.ps1`), buff profiles, and a Spells window.
- **Creature data:** UB's damage seed in the Auto element ranking (`Shared/UbMobSeed.cs`,
  `UbMobSeedStore`), and VirindiTank profile and USD monster imports (plugin and Monster Editor).
- **Loot:** the ACECustom T11 item state for item ID and loot rules; "Include T11 attributes"
  when adding an item to a loot profile; unknown VTank conditions bounded by their length code.
- **Corpses:** only the "Killed by" line names the killer.
- **Diagnostics:** per-category, per-process RynthAi log files (`Diagnostics/RynthLog.cs`).
- **Plumbing:** the plugin calls cimgui through its own binding (`ImGuiNativeBinding`), window
  placement helpers (`UiPlacement`), and the plugin exports the engine's overlay, embed and remote
  commands use (`PluginExports`).

## Testing

- `dotnet build -c Release`: RynthAi plugin and Monster Editor.
- LootSdkTests pass.
- RynthAiHostTests pass except the map bake, which needs the real dat files on the test machine.

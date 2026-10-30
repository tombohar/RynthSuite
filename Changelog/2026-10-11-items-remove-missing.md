# Items: Remove Missing (clean out items no longer in your inventory)

Pairs with the matching RynthCore commit on `sk`, which adds the Remove Missing button to the Items panel.

## Why

The Items lists (weapons and consumables) keep an entry for every item ever added. Sold, dropped,
traded or used-up items stayed listed. The lists were cleaned only once: when a profile copied
from another character was first loaded.

## What changed

- **Remove Missing:** a new button at the top of the engine's Items panel, and a new chat command,
  `/ra items clean`. Both drop listed weapons and consumables whose item is no longer in your
  inventory. Your inventory means the main pack, side packs and wielded gear.
- **Re-pointing instead of removing:** when an entry's item is gone but your pack holds an
  unlisted item with the same name (a new stack of healing kits, a replacement weapon), the entry
  is pointed at that item. Its element or type stays as you set it.
- **Duplicates:** a second entry for an item that is already listed is removed.
- **Safe while loading:** nothing is removed while your inventory is still arriving after login,
  or while the inventory reads as empty.
- **Report:** a chat line lists what was removed and what was re-pointed.
- **Profile copy check:** the one-time check on a profile copied from another character uses the
  same rules. Before, it kept a dead entry whenever any item of that name existed; now it
  re-points the entry or removes the duplicate.
- `/ra items` with no argument now lists both `fill` and `clean`.

## Files

- `LegacyUi/LegacyWeaponsUi.cs`: `CleanMissing` (`PruneNotOwned` now calls it).
- `LegacyUi/LegacyDashboardRenderer.cs`: `CleanMissingItems`, with the loading guards and the
  chat report.
- `RynthAiCommands.cs`, `RynthAiPlugin.cs`: `/ra items clean`.
- `PluginExports.cs`: the `RynthPluginCleanItems` export, used by the engine button.

## Tests

`Tools/RynthCore.RynthAiHostTests/Tests/ItemsCleanTests.cs`, 3 new tests:

- gone entries are removed and owned ones are kept;
- a gone entry is re-pointed at an unlisted item of the same name (case-insensitive), keeping its
  element;
- a gone duplicate of an item that is already listed is removed, not re-pointed.

Result: 622 of 623 pass. The one failure is the known map-bake test, which needs local dats.

Not yet tested in game.

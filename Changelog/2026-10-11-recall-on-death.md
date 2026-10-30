# Recall On Death: cast a recall spell or use a recall item after dying

Pairs with the matching RynthCore commit on `sk`, which shows the option on the Settings page.

## What changed

- **New option: Settings > On Death > Recall On Death.** The choices are:
  - Off (the default);
  - Cast a recall spell: pick it from the nav editor's recall list (Primary Portal Recall by
    default);
  - Use a recall item: type the item's name as it shows in your inventory. An exact name wins;
    otherwise the first item whose name contains it is used.
- **When it runs:**
  - after you die while the macro is running;
  - once you've respawned and the existing 5-second settle after the death portal is over;
  - Stop Macro On Death still recalls first, because the recall is armed at the moment you die.
- **While it runs:**
  - nothing else runs (no combat, buffing, looting or nav);
  - a spell first gets a wand and magic mode, the same step a nav Recall waypoint takes;
  - a fizzle or failed use is retried every 4 s for a spell and every 6 s for an item, up to
    5 tries;
  - once portal space or a landblock change shows the teleport happened, nothing more is fired.
- **Afterwards:**
  - 3 s after landing, nav rejoins its route at the closest waypoint;
  - Travel Back To My Corpse then starts from the landing spot. A Primary Portal Recall to your
    hunting ground makes that walk much shorter.
- **Giving up:** after 5 tries or 60 s, it says why in chat and carries on from where you are.
- **Not started**, with a chat line saying why, when:
  - the item isn't in your inventory;
  - no item name is set;
  - the engine can't cast or use items.
- Dying again during a recall cancels it.

## Files

- `DeathRecallRun.cs` (new): the attempt as a pure, clock-driven state machine: when to fire, when
  the teleport counts as done, when to give up.
- `RynthAiPlugin.DeathRecall.cs` (new): armed on the death edge, started on the respawn edge,
  ticked after the death hold; spell cast or item use, the chat report, then nav and the corpse
  trip.
- `RynthAiPlugin.Safety.cs`, `RynthAiPlugin.cs`: wiring.
- `LegacyUiSettings.cs`: `DeathRecallMode`, `DeathRecallSpellId`, `DeathRecallItem`, plus the
  engine DTO fields (nullable, so an older engine leaves them alone).
- `LegacyDashboardRenderer.cs`: engine round trip and `CopySettings`.
- `ExpressionEngine.cs`: the three setting names for metas.

## Tests

`Tools/RynthCore.RynthAiHostTests/Tests/DeathRecallTests.cs`, 5 new tests:

- fires at once, then retries every 4 s;
- portal space stops the firing, and the run is done 3 s after landing;
- a landblock change counts as the teleport;
- gives up after 5 tries, with items retried every 6 s;
- never fires while not ready, and times out after 60 s.

Result: 627 of 628 pass. The one failure is the known map-bake test, which needs local dats.

Not yet tested in game.

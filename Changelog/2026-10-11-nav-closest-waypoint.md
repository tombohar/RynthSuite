# Navigation: Go To Closest Waypoint (on by default)

Pairs with the matching RynthCore commit on `sk`, which shows the option on the engine Settings page.

## Why

Navigation only looked for the closest waypoint at macro start. Two cases went the long way round:

- **After a pause:** combat, looting, buffing and doors stop nav, then hand back to it. Nav carried
  on to the waypoint it was heading for before the pause, even when a chase had taken the
  character next to a different part of the route. It walked back to the old waypoint first.
- **Already past the nearest point:** macro start picked the nearest waypoint even when the
  character had just walked past it. Nav turned round to touch it, then turned back.

## What changed

- **New option: Advanced Settings > Navigation > Getting Back On Route > Go To Closest Waypoint.**
  It is on by default. It is also on the engine Settings page under Navigation, and metas can read
  or set it as `NavResumeNearest`.
- **With the option on:**
  - **Macro start:** heads for the closest waypoint, as before.
  - **Back from a pause that moved the character 10 yd or more:** re-picks the closest waypoint
    instead of walking back to the old one. A pause that moved the character less than 10 yd
    carries on to the same waypoint. A move of more than 400 yd counts as a teleport, which the
    portal handling already deals with.
  - **No turning back:** when the closest spot on the route is on the leg after the nearest
    waypoint (the character already walked past it), nav heads for the next waypoint. A Linear
    route follows its current direction. A step (recall, portal, chat, pause, NPC) is never
    skipped to or over.
  - **Loading a Circular or Linear route:** starts at its nearest waypoint, as before.
  - Once routes still only look ahead of the current step, and Follow routes are not changed.
  - A detour or area hold in progress keeps control, so the re-pick does not interrupt it.
  - Indoors, macro start still compares the nearest waypoints by walking distance on the
    dungeon map.
- **With the option off:**
  - Pauses carry on to the waypoint nav was heading for.
  - Macro start keeps the last waypoint.
  - A loaded route starts at its first point.
  - Hazard escapes and corpse runs still rejoin at the closest waypoint, because they take the
    character off the route.

## Files

- `NavigationEngine.cs`:
  - remembers where a pause started (`MarkPausePosition`);
  - re-picks on the first steer tick back (`ResumeAfterPause`);
  - `ResumeFromNearestWaypoint(bool always = false)` now honours the setting;
  - `PassedToward` and `SegmentDistSq` implement the no-turning-back rule.
- `LegacyUiSettings.cs`: `NavResumeNearest` (default true), plus the engine DTO field (nullable,
  so an absent value keeps the plugin's setting).
- `LegacyDashboardRenderer.cs`: engine round trip, `CopySettings`, and the route-load start index.
- `NavRouteParser.StartsAtFirstPoint`: the shared route-load rule, also used by
  `MetaManager.StartIndexForRoute`.
- `LegacyAdvancedSettingsUi.cs`: the checkbox and its tooltip.
- `ExpressionEngine.cs`: the `NavResumeNearest` setting name for metas.
- `RynthAiPlugin.HazardEscape.cs`, `RynthAiPlugin.OwnCorpse.cs`: always rejoin at the closest
  waypoint.

## Tests

`Tools/RynthCore.RynthAiHostTests`, 5 new tests:

- macro start skips a nearest waypoint already walked past, but not one still ahead;
- back from a pause that moved 10 yd or more, nav heads for the closest waypoint;
- back from a pause that moved less than 10 yd, nav keeps its waypoint;
- with the option off: the pause and macro-start cases keep the last waypoint, and hazard or
  corpse rejoins still re-pick;
- the route-load start rule.

Result: 619 of 620 pass. The one failure is the known map-bake test, which needs local dats.

Not yet tested in game.

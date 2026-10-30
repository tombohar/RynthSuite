# RynthILT 0.2.0: the ILT Hub moves out of RynthAi

SilentKelpie's ILT Hub (the Infinite Leaftide / ACECustom features) is now part of the RynthILT
plugin. RynthAi keeps the Mini Remote and the parts every server has. Pairs with RynthCore
`refactor/ilt-into-plugin` (the Guardian panel, the Progression tab and the ILT menus read
RynthILT).

## What changed

- **RynthILT has the ILT features** (`Plugins/RynthCore.Plugin.RynthILT/Hub`): the Mini Remote's
  Character, Banking, Gear, Charms and Games tabs, the Guardian panel, the Skills panel's
  Progression data and the attribute raiser, server feature detection, the `/qb` list and the
  `/pets` and `/shinies` logs. They run only on an ILT world (its server list, or the
  character's "Treat this world as ILT"), as before.
- **RynthAi keeps** the Mini Remote, its Quests and Pets tabs (every server; settings now in
  `miniremote.json`, read once from the old `ilt-hub.json`) and the VirindiTank `.usd` importer
  (it edits RynthAi's rules; it still shows in the Gear tab).
- **The two plugins talk through two interfaces** (`Plugins/Shared`): `RynthILT.Hub` (the ILT tabs,
  sections, feature bits and verbs the Mini Remote shows) and `RynthAi.Hub` (the Mini Remote,
  `/myquests`, spell names, VTank loot matching, the importer and the session stats RynthILT
  borrows). Without RynthILT the Mini Remote has no ILT tabs and the ILT verbs say so; without
  RynthAi, RynthILT's panels and `/ilt` commands still work (suits, dispel names, `/myquests`
  rows and the XP rows need RynthAi).
- **Saved data is copied over** on each character's first login with RynthILT: `ilt-hub.json`,
  `ilt-hub-transactions.json`, `ilt-hub-profiles\`, the shared `IltHubProfiles\` and
  `ilt-aug-costs.json` go to `%APPDATA%\RynthCore\RynthILT`, and the RynthAi profile's Server
  Features list and "Force enable" are folded into RynthILT's settings. Nothing is deleted.
- **Failed saves and copies say so** (as RynthAi's do since 2026.10.13.1; `DataWriteGuard` moved to
  `Plugins/Shared` and is linked into both plugins): the Hub state, bank log, Hub profiles,
  settings, aug costs, the bounty list and every file copied from RynthAi are logged with the
  reason when they fail, and said in chat once per folder (with the fix) on an ILT world only.
  A shared file that couldn't be copied is tried again at the next login; the originals in the
  RynthAi folder are never touched.
- **An unfinished character copy is never shadowed:** if one of a character's files can't be
  copied, the character is marked pending (in RynthILT's `settings.json`, so it survives a
  restart). Until the copy completes the Hub saves no `hub.json`, bank log or per-character
  profile for it, running from the RynthAi originals read-only where they can be read (else the
  defaults). The copy is retried every 2 minutes and at each login; once it works the Hub
  restarts on the copied files.
- **Commands:** `/ra hub|quests|pets` work as before; the ILT verbs (`/ra hub bank|attr|suit|
  profile|force|refresh|status|clap|open <ILT tab>`, `/ra guardian`, `/ra bounty`) go on to
  RynthILT. New: `/ra hub servers [list|<names>|default]` (the old Settings > Misc list),
  `/ilt hub ...`, `/ilt guardian ...`, `/ilt servers`, `/ilt trace`, `/ilt debug`.
- The ILT trace categories leave RynthAi's log; RynthILT writes its own
  (`%APPDATA%\RynthCore\RynthILT\Logs`).

## Not carried

- Hub profiles hold only the ILT settings now (the Pets tab's settings stay per character).
- `RynthPluginGetCharmsJson` (the Charms Tracking tab of engines up to 2026.10.5.35) is gone.

## Tests

- `Tools/RynthILT.Tests` runs the raiser, aug cost, bank and sigil tests (moved from the RynthAi
  host tests) and new migration, interface and routing tests (failed copies and saves reported,
  the VTank hotkey verbs stay RynthAi's); `RynthCore.RynthAiHostTests` covers the Mini Remote side.

Not yet tested in game.

# RynthNet 0.1.1: object id and combat mode in the shared status

Pairs with the RynthCore engine's Characters panel (`/rc characters`, RynthCore
`feature/characters-panel`).

## What changed

- Each client's shared status (sent every `statusIntervalMs`) now has two more fields:
  - `id`: the character's object id. It is the same on every client, so the Characters panel can
    select that character with a click when it is in view.
  - `cmode`: AC's combat mode (1 peace, 2 melee, 4 missile, 8 magic). The panel shows a stance
    when the macro is off.
- The fields are optional. A 0.1.0 client's status simply lacks them, and readers treat every
  status field as optional.
- `Docs/RYNTHNET.md` now lists the fields and points to the Characters panel as the NetworkUI HUD.

## Files

- `RynthNetPlugin.Status.cs`: the two fields. `cmode` is written only when the host has
  `GetCurrentCombatMode`, which reads a cached value off AC's thread.
- `RynthNetPlugin.cs`: version 0.1.1.

Not yet tested in game.

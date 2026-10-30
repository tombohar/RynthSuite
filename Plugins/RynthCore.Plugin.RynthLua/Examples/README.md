# RynthLua examples

Copy a file into `C:\Games\RynthSuite\RynthLua\Scripts\` (or make a folder `<name>\` and
save it there as `index.lua`), then `/lua start <name>` or press Start in the Lua panel.
Scripts that listen for events keep running until you stop them (`/lua stop <name>`).

- `world-demo.lua`: game.World and WorldObject: what's around you and in your packs, filters (class, name, function), the selection, and the object, tell, selection and chat events.
- `character-demo.lua`: game.Character: identity, vitals, skills, attributes, burden, inventory and equipment, enchantments, the spellbook, then every character event.
- `actions-demo.lua`: the action queue: using an item on yourself with `await`, a failure as a value, a callback, a spell cast with retries, and `RunAllOrdered`. It really acts: set `KIT_NAME` and `SPELL_ID` first.
- `library-demo.lua`: the modules: enums, `json`, `storage` (per script and per character), `filesystem`, `rynth.ai` and `rynthlua`.
- `hud-demo.lua`: a small script window (`require("views")`, `require("imgui")`): a counter button, a checkbox and a running time; `/hud` shows or hides it. `/lua hud bench <n>` opens a test window with n widgets for measurements.
- `xp-tracker.lua`: a tracker window: XP gained and per hour, kills, a health bar, and Reset with a tooltip; the first size set in `OnPreRender`.
- `hunt-settings.lua`: a settings form saved with `storage` (checkbox, slider, 0-based combo, text box) and a button that sets your combat mode; `/hunt` shows or hides it.
- `salvage-list.lua`: a list with buttons: your salvage, gathered in a spawned loop, a filter box, a scrolling region with `PushID` rows, and Use / Give buttons (Give waits for its result in a thread of its own). The buttons really act.
- `item-icons.lua`: an item list with icons: your pack items drawn with their AC icons (`views.Huds.GetObjectIconTexture`, `ImGui.ImageButton`, `ImGui.Image`), an `InputInt` for how many rows and a `DragFloat` for the icon size. Nothing acts on the game. Needs an engine from 2026-09-30 or later.
- `combat-handler.lua`: RynthAi's combat hook (`rynth.ai` SetAttackHandler / SetHealHandler): an attack handler that casts a set spell while the target's health is under a line and otherwise lets RynthAi choose, a heal handler that heals you under a health line, the refusal reasons printed, and `/combathook` to turn them on and off. Set its spell ids first; it needs RynthAi running.

The window examples need RynthCore API v71 with ImGui on; see "Script windows" in the reference.

The API reference is `docs/RYNTHLUA.md` in RynthSuite; `Types/` has editor completion for VS Code.

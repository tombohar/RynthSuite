# RynthLua editor types

Completion, hover docs and type checks for RynthLua scripts in VS Code (or any editor
that uses the Lua language server, LuaLS / "sumneko").

| File | What it describes |
| --- | --- |
| `rynthlua.d.lua` | Everything: the globals (`game`, `sleep`, `await`, v1 `chat`/`me`/...), WorldObject, Character, World, Actions, ActionQueue, events and their argument tables, the module types, the enum tables, and script windows (`Vector2`, `Vector4`, the hud, the `imgui` functions and the ImGui enums) |
| `json.lua`, `storage.lua`, `enums.lua`, `filesystem.lua`, `rynthlua.lua`, `rynth/ai.lua` | Small stubs so `require("json")`, `require("rynth.ai")`, ... resolve to the right types |
| `views.lua`, `imgui.lua` | Stubs for `require("views")` and `require("imgui")` (script windows) |

None of these files are loaded in the game; they only describe the API. The reference is
`docs/RYNTHLUA.md` in RynthSuite.

## Setup (VS Code)

1. Install the **Lua** extension (publisher: sumneko).
2. Copy this `Types` folder somewhere stable, for example
   `C:\Games\RynthSuite\RynthLua\Types`.
3. Open your scripts folder (`C:\Games\RynthSuite\RynthLua\Scripts`) in VS Code and put a
   `.luarc.json` in it:

```json
{
  "$schema": "https://raw.githubusercontent.com/LuaLS/vscode-lua/master/setting/schema.json",
  "runtime.version": "Lua 5.2",
  "workspace.library": ["C:/Games/RynthSuite/RynthLua/Types"],
  "workspace.checkThirdParty": false,
  "diagnostics.disable": ["lowercase-global"]
}
```

Use the path where you put the folder (forward slashes are fine). Reload the window
(`Developer: Reload Window`) after changing `.luarc.json`.

The same settings work in VS Code's `settings.json` with a `Lua.` prefix
(`"Lua.workspace.library": [...]`, `"Lua.runtime.version": "Lua 5.2"`).

## Notes

- RynthLua runs Lua 5.2 (MoonSharp), hence `runtime.version`.
- API tables are declared with dot calls (`game.World.GetAll()`,
  `game.Actions.ObjectUse(id)`, `storage.Get("key")`), WorldObject methods with colon
  calls (`wo:Use()`, `wo:DistanceTo(other)`). In the game both forms work everywhere, so if
  the editor warns about `wo.Use()` or `game.World:GetAll()`, the script is still fine.
- Events: `game.World.OnTell.Add(function(e) ... end)`; the handler's `e` is typed, so
  `e.` completes the event's fields.
- Script windows: `local hud = views.Huds.CreateHud("X")` is a `RynthLua.Hud`; inside
  `hud.OnRender.Add(function() ... end)` the `ImGui.` calls complete with their arguments and
  their extra return values (`local changed, v = ImGui.Checkbox("On", v)`).
- Enum-valued fields (`wo.ObjectClass`, `game.Character.CombatMode`, `e.Type`, ...) are
  numbers typed as the enum, so `wo.ObjectClass == ObjectClass.Monster` checks cleanly.
- If a `require` doesn't pick up its type (an unusual `runtime.path`), annotate it by hand:
  `local json = require("json") ---@type RynthLua.JsonModule`.
- A script's own modules (`require("helpers")` for `helpers.lua` next to `index.lua`)
  resolve normally when the script's folder is the open workspace.

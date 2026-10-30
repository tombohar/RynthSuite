-- hunt-settings.lua: a settings form (script windows) saved with storage: a checkbox, a
-- slider, a drop-down and a text box, plus a button that sets your combat mode.
-- Needs RynthCore API v71 (engine) with ImGui on. Start it with /lua start hunt-settings,
-- then type /hunt to show or hide the window.
--
-- The settings are only an example: nothing reads them but this form, and they are kept
-- in the script's storage (Data\hunt-settings\storage.json). The "Apply combat mode"
-- button really changes your combat mode.
--
-- Value widgets return two values, (changed, newValue): keep the new value, and save when
-- it changed. Combo's index is 0-based, as in ImGui.NET; the Lua list is 1-based.
-- The window isn't shown at start, so the player's last choice decides: it opens again
-- after a restart only if it was left open on this character.

local views, ImGui, storage = require("views"), require("imgui"), require("storage")

local s = storage.Get("settings", { enabled = true, range = 30, mode = 0, note = "" })
local modes = { "Melee", "Missile", "Magic" }
local function save() storage.Set("settings", s) end        -- storage debounces the file write

local hud = views.Huds.CreateHud("Hunt Settings")

hud.OnRender.Add(function()
  local ch
  ch, s.enabled = ImGui.Checkbox("Enabled", s.enabled);          if ch then save() end
  ch, s.range   = ImGui.SliderInt("Range (m)", s.range, 5, 60);  if ch then save() end
  ch, s.mode    = ImGui.Combo("Mode", s.mode, modes);            if ch then save() end   -- 0-based, like UB
  ch, s.note    = ImGui.InputText("Note", s.note, 128);          if ch then save() end
  ImGui.Separator()
  if ImGui.Button("Apply combat mode") then
    game.Actions.SetCombatMode(modes[s.mode + 1])
  end
end)

game.RegisterCommand("/hunt", function() hud.Visible = not hud.Visible end)

print("Hunt Settings ready: /hunt shows or hides the window.")

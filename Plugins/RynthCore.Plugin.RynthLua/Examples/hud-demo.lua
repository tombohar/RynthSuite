-- hud-demo.lua: a small window (script windows): a counter button, a checkbox and a
-- running time.
-- Needs RynthCore API v71 (engine) with ImGui on. Start it with /lua start hud-demo,
-- close the window with its X, bring it back with /hud (or the bar's Scripts button).
--
-- OnRender runs about 20 times a second on the plugin thread and records what to draw;
-- the engine draws it every frame. A click shows up as ImGui.Button returning true on the
-- next pass (one pass late), and ImGui.Checkbox returns (changed, newValue).
-- The full list of ImGui functions is in docs/RYNTHLUA.md ("Script windows"); see also
-- xp-tracker.lua, hunt-settings.lua and salvage-list.lua.

local views = require("views")
local ImGui = require("imgui")

if not views.Available then
  print("Script windows aren't available here (engine older than API v71, or ImGui is off).")
end

local hud = views.Huds.CreateHud("Tracker")
hud.Title = "Tracker demo"
hud.Visible = true

local startedAt = clock()
local clicks = 0
local showTime = true

hud.OnRender.Add(function()
  ImGui.Text("Hello, " .. (game.Character.Name or "?"))
  ImGui.Separator()

  if ImGui.Button("Count") then
    clicks = clicks + 1
  end
  ImGui.SameLine()
  ImGui.Text("clicked " .. clicks .. " time(s)")

  local changed
  changed, showTime = ImGui.Checkbox("Show running time", showTime)
  if changed then print("Show running time: " .. tostring(showTime)) end

  if showTime then
    local s = math.floor((clock() - startedAt) / 1000)
    ImGui.Text(("Running for %d:%02d"):format(math.floor(s / 60), s % 60))
  end

  if ImGui.SmallButton("Reset") then
    clicks, startedAt = 0, clock()
  end
end)

-- The player's X sets hud.Visible to false; /hud shows it again.
game.RegisterCommand("/hud", function()
  hud.Visible = not hud.Visible
end)

print("Tracker hud open. /hud toggles it; /lua stop hud-demo closes it.")

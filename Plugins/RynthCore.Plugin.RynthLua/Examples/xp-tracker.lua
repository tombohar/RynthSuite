-- xp-tracker.lua: a tracker window (script windows): XP gained, XP per hour, kills and a
-- health bar, with a Reset button that has a tooltip.
-- Needs RynthCore API v71 (engine) with ImGui on. Start it with /lua start xp-tracker;
-- close the window with its X and bring it back from the bar's Scripts button or with
-- /lua hud show xp-tracker/XP Tracker.
--
-- OnRender runs about 20 times a second while the window is shown and records what to
-- draw; the engine draws it every frame. ImGui.Button returns true on the pass after the
-- click. OnPreRender runs just before OnRender in the same pass: it is where the window's
-- first size goes (FirstUseEver: only until the player moves or sizes it).

local views = require("views")
local ImGui = require("imgui")

if not views.Available then
  print("Script windows aren't available here (engine older than API v71, or ImGui is off).")
end

local hud = views.Huds.CreateHud("XP Tracker")
-- Shown at start. Leave this line out to let the player's last choice (the X, the bar's
-- Scripts menu) decide instead.
hud.Visible = true

local startXp, startAt, kills = game.Character.TotalExperience, clock(), 0
on("kill", function() kills = kills + 1 end)              -- v1 event; v2 has no kill event yet

hud.OnPreRender.Add(function()
  ImGui.SetNextWindowSize(Vector2.new(240, 130), ImGuiCond.FirstUseEver)
end)

hud.OnRender.Add(function()
  local c = game.Character
  local gained = c.TotalExperience - startXp
  local hours = math.max((clock() - startAt) / 3600000, 1 / 3600)
  ImGui.Text("XP gained:  " .. math.floor(gained))
  ImGui.Text("XP / hour:  " .. math.floor(gained / hours))
  ImGui.Text("Kills:      " .. kills)
  ImGui.ProgressBar(c.Health / math.max(c.MaxHealth, 1), Vector2.new(-1, 0),
    c.Health .. " / " .. c.MaxHealth)
  if ImGui.Button("Reset") then
    startXp, startAt, kills = c.TotalExperience, clock(), 0
  end
  ImGui.SetItemTooltip("Start counting again from now")
end)

print("XP Tracker open. /lua stop xp-tracker closes it.")

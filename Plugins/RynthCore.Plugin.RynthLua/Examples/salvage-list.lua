-- salvage-list.lua: a list with buttons (script windows): your salvage bags, a filter box,
-- and Use / Give buttons on each row.
-- Needs RynthCore API v71 (engine) with ImGui on. Start it with /lua start salvage-list.
--
-- The buttons really act: Use uses the bag, "Give to selected" gives it to whatever you
-- have selected.
--
-- The list is gathered in a spawned thread once a second, not in every pass: OnRender only
-- draws what it has. OnRender can't sleep or await, so the Give button starts a thread of
-- its own to wait for the result. PushID(r.id) gives each row's buttons their own IDs, so
-- every row can have a "Use" button.

local views, ImGui = require("views"), require("imgui")

local hud = views.Huds.CreateHud("Salvage")
hud.Visible = true
local rows, filter = {}, ""

spawn(function()                        -- refresh the data once a second, not in every pass
  while true do
    rows = {}
    for _, wo in ipairs(game.Character.Inventory(ObjectClass.Salvage)) do
      rows[#rows + 1] = { id = wo.Id, name = wo.Name, obj = wo }
    end
    sleep(1000)
  end
end)

local function give(r)
  local target = game.World.Selected
  if not target then print("Select someone to give to first.") return end
  spawn(function()                      -- OnRender can't await; a spawned thread can
    local a = await(r.obj.Give(target))
    if not a.Success then print("give failed: " .. a.Error .. " " .. (a.ErrorDetails or "")) end
  end)
end

hud.OnRender.Add(function()
  local _
  _, filter = ImGui.InputText("Filter", filter, 64)
  if ImGui.BeginChild("rows", Vector2.new(0, 0), true) then
    for _, r in ipairs(rows) do
      if filter == "" or r.name:lower():find(filter:lower(), 1, true) then
        ImGui.PushID(r.id)
        ImGui.Text(r.name)
        ImGui.SameLine(220)
        if ImGui.SmallButton("Use") then r.obj.Use() end
        ImGui.SameLine()
        if ImGui.SmallButton("Give to selected") then give(r) end
        ImGui.PopID()
      end
    end
  end
  ImGui.EndChild()
end)

print("Salvage list open. /lua stop salvage-list closes it.")

-- item-icons.lua: an item list with icons (script windows): your pack items with their
-- AC icons, an InputInt for how many rows to show and a DragFloat for the icon size.
-- Needs a RynthCore engine from 2026-09-30 or later (script windows "op level 2": icons
-- and number inputs) with ImGui on. Start it with /lua start item-icons.
--
-- Nothing here acts on the game: clicking an icon only picks that row.
--
-- views.Huds.GetObjectIconTexture(id) is the item as AC draws it (underlay, icon,
-- overlay). UB's way, views.Huds.GetIconTexture(iconId) + ImGui.Image(tex.TexturePtr, size),
-- works too. Until the engine has decoded a picture (a frame or two), or when it can't find
-- one, a dim square stands in: a missing icon is never an error.

local views, ImGui = require("views"), require("imgui")

local hud = views.Huds.CreateHud("Item Icons")
hud.Visible = true
hud.DefaultSize = Vector2.new(320, 360)

local rows, maxRows, iconSize, picked = {}, 25, 24, nil

spawn(function()                        -- gather the list once a second, not in every pass
  while true do
    local list = {}
    for _, wo in ipairs(game.Character.Inventory()) do
      list[#list + 1] = { id = wo.Id, name = wo.Name }
    end
    table.sort(list, function(a, b) return a.name < b.name end)
    rows = list
    sleep(1000)
  end
end)

hud.OnRender.Add(function()
  local changed
  changed, maxRows = ImGui.InputInt("Rows", maxRows, 5, 25)
  if changed then maxRows = math.max(1, math.min(maxRows, 200)) end
  changed, iconSize = ImGui.DragFloat("Icon size", iconSize, 0.25, 16, 48, "%.0f px")
  ImGui.Separator()

  if ImGui.BeginChild("items", Vector2.new(0, -ImGui.GetFrameHeight() - 6), true) then
    for i = 1, math.min(#rows, maxRows) do
      local r = rows[i]
      ImGui.PushID(r.id)
      local tex = views.Huds.GetObjectIconTexture(r.id)
      if ImGui.ImageButton("icon", tex, Vector2.new(iconSize, iconSize)) then picked = r end
      ImGui.SetItemTooltip(r.name)
      ImGui.SameLine()
      ImGui.Text(r.name)
      ImGui.PopID()
    end
  end
  ImGui.EndChild()

  if picked then
    ImGui.Image(views.Huds.GetObjectIconTexture(picked.id), Vector2.new(16, 16))
    ImGui.SameLine()
    ImGui.Text("Picked: " .. picked.name)
  else
    ImGui.TextDisabled(#rows .. " items; click an icon to pick one")
  end
end)

print("Item icons open. /lua stop item-icons closes it.")

-- world-demo: a tour of game.World and WorldObject.
-- Prints what's around you and in your packs, then keeps listening for
-- objects, tells and selection changes until you press Stop.

local World = game.World

-- ObjectClass numbers (Decal/UtilityBelt order). A filter can also be the exact
-- class name as a string: World.GetLandscape("Monster").
local MONSTER, PORTAL, CORPSE = 5, 14, 27

local function where(wo)
  local p = wo.Position
  if not p then return "(no position)" end
  return ("%.1f%s %.1f%s"):format(math.abs(p.NS), p.NS >= 0 and "N" or "S",
                                  math.abs(p.EW), p.EW >= 0 and "E" or "W")
end

-- Around you ----------------------------------------------------------------
local me = World.Get(game.CharacterId)
if not me then print("Log in first.") return end
print("You are " .. tostring(me) .. " at " .. where(me))

local monsters = World.GetLandscape(MONSTER)
print(#monsters .. " monster(s) nearby")
for _, m in ipairs(monsters) do
  print(("  %-28s %6.1f m  wcid %d"):format(m.Name, me:DistanceTo(m), m.WeenieClassId))
end

local portal = World.GetNearest("Portal")      -- by class name
if portal then
  print("Nearest portal: " .. portal.Name .. " at " .. where(portal))
end

-- A function filter: corpses within 30 m.
local close = World.GetLandscape(function(wo)
  return wo.ObjectClass == CORPSE and me:DistanceTo(wo) < 30
end)
print(#close .. " corpse(s) within 30 m")

-- Your stuff ----------------------------------------------------------------
local inv = World.GetInventory()
print(#inv .. " item(s) in your packs (game.Character.Inventory() adds what you wear)")
local burden = 0
for _, item in ipairs(inv) do burden = burden + item.Burden end
print("Burden of appraised items: " .. burden)

for _, pack in ipairs(World.GetInventory(function(wo) return wo.ObjectClass == 10 end)) do
  print(("  %s holds %d item(s)"):format(pack.Name, #pack:Items()))
end

local tapers = World.GetInventory("Taper")     -- a name substring
for _, p in ipairs(tapers) do
  print(("  %s x%d (stack prop 12 = %d)"):format(p.Name, p.StackSize, p:IntValue(12, 1)))
end

-- The selection ---------------------------------------------------------------
local sel = World.Selected
if sel then
  print("Selected: " .. tostring(sel) .. ", class " .. sel.ObjectClass ..
        ", appraised: " .. tostring(sel.HasAppraisalData))
end

-- Events ------------------------------------------------------------------------
World.OnObjectCreated.Add(function(e)
  local wo = e.Object
  if wo and wo.ObjectClass == PORTAL then
    print("A portal appeared: " .. wo.Name)
  end
end)

World.OnObjectReleased.Add(function(e)
  -- e.ObjectId only: the object is already gone.
end)

World.OnTell.Add(function(e)
  print(os.date("%H:%M") .. "  tell from " .. e.SenderName .. ": " .. e.Message)
end)

World.OnSelected.Add(function(e)
  if e.Object then
    print("You selected " .. e.Object.Name .. " (" .. where(e.Object) .. ")")
  end
end)

-- Stops listening to chat after the first line that contains "stop demo".
World.OnChatText.Until(function(e)
  if e.Message:find("stop demo") then
    print("OnChatText handler removed.")
    return true
  end
end)

print("Listening for portals, tells and selections. Press Stop to end.")

-- UB-style game.World use: lookups, searches with every filter kind, nearest by 2D/3D,
-- the open container and vendor, and the world events (objects, selection, chat, input).
local ids = harness.ids
local World = game.World

local function waitUntil(fn, ms)
  local t = 0
  while not fn() and t < (ms or 3000) do sleep(20); t = t + 20 end
  return fn()
end

local function names(list)
  local set = {}
  for _, wo in ipairs(list) do set[wo.Name] = true end
  return set
end

-- lookups
eq(World.Get(ids.Drudge).Name, "Drudge Skulker", "Get")
local ok, wo = World.TryGet(ids.Crier)
check(ok and wo.Name == "Town Crier", "TryGet found")
local ok2, wo2 = World.TryGet(0x7FFFFFF0)
check(not ok2 and wo2 == nil, "TryGet missing")
check(World.Exists(ids.PortalObj), "Exists")
check(not World.Exists(0x7FFFFFF1), "Exists missing")
eq(World[ids.Lever].Name, "Tower Lever", "World[id]")
check(World.Selected == nil, "nothing selected")

-- searches
local all = names(World.GetAll())
check(all["Plain Sword"] and all["Drudge Skulker"] and all["Tester"], "GetAll() has pack items, landscape and me")
local monsters = World.GetAll(ObjectClass.Monster)
eq(#monsters, 1, "GetAll(ObjectClass.Monster)")
eq(monsters[1].Name, "Drudge Skulker", "the monster")
local creatures = names(World.GetAll(ObjectType.Creature))
check(creatures["Drudge Skulker"] and creatures["Town Crier"] and creatures["Shopkeeper"] and creatures["Friendly Player"], "GetAll(ObjectType.Creature)")
check(not creatures["Plain Sword"], "GetAll(ObjectType.Creature) has no items")
local cheap = World.GetAll(function(w) return w:Value(IntId.Value) >= 500 end)
local cheapNames = names(cheap)
check(cheapNames["Plain Sword"] and cheapNames["Amethyst"], "GetAll(function)")
local land = names(World.GetLandscape())
check(land["Drudge Skulker"] and land["Chest"], "GetLandscape()")
check(not land["Tester"] and not land["Plain Sword"] and not land["Amethyst"], "GetLandscape() has no me and no pack/chest items")
local portals = World.GetLandscape(ObjectType.Portal)
eq(#portals, 1, "GetLandscape(ObjectType.Portal)")
eq(#World.GetLandscape(ObjectClass.Npc), 1, "GetLandscape(ObjectClass.Npc)")

-- nearest
eq(World.GetNearest(ObjectClass.Monster).Name, "Drudge Skulker", "GetNearest(ObjectClass)")
eq(World.GetNearest(ObjectType.Portal).Name, "Portal to Town", "GetNearest(ObjectType)")
eq(World.GetNearest("Town Crier").Name, "Town Crier", "GetNearest(name)")
local leverOrCrier = function(w) return w.Name == "Tower Lever" or w.Name == "Town Crier" end
eq(World.GetNearest(leverOrCrier).Name, "Town Crier", "GetNearest default is 3D")
eq(World.GetNearest(leverOrCrier, DistanceType.T3D).Name, "Town Crier", "GetNearest T3D")
eq(World.GetNearest(leverOrCrier, DistanceType.T2D).Name, "Tower Lever", "GetNearest T2D ignores height")

-- selection
local selected
World.OnObjectSelected.Add(function(e) selected = e.ObjectId end)
harness.select(ids.Crier)
check(waitUntil(function() return selected == ids.Crier end), "OnObjectSelected.ObjectId")
eq(World.Selected.Id, ids.Crier, "World.Selected")

-- objects created and released
local created, released
World.OnObjectCreated.Add(function(e) created = e.ObjectId end)
World.OnObjectReleased.Add(function(e) released = e.ObjectId end)
local newId = harness.create({ Name = "Olthoi Grub", ItemType = ObjectType.Creature:ToNumber(), Bitfield = 0x10, X = 2 })
check(waitUntil(function() return created == newId end), "OnObjectCreated.ObjectId")
eq(World.Get(newId).Name, "Olthoi Grub", "new object is known")
harness.delete(newId)
check(waitUntil(function() return released == newId end), "OnObjectReleased.ObjectId")
check(World.Get(newId) == nil, "released object is gone")

-- the open container
local opened, closed
World.OnContainerOpened.Add(function(e) opened = e.Container end)
World.OnContainerClosed.Add(function(e) closed = e.Container end)
harness.container(ids.Chest)
check(waitUntil(function() return opened ~= nil end), "OnContainerOpened")
eq(opened and opened.Id, ids.Chest, "OnContainerOpened.Container")
eq(World.OpenContainer.Id, ids.Chest, "World.OpenContainer")
eq(World.OpenContainer.Items[1].Name, "Amethyst", "OpenContainer.Items")
harness.container(nil)
check(waitUntil(function() return closed ~= nil end), "OnContainerClosed")
eq(closed and closed.Id, ids.Chest, "OnContainerClosed.Container")
check(World.OpenContainer == nil, "no open container")

-- the vendor
local vendorOpened, vendorClosed = false, false
World.Vendor.OnOpened.Add(function() vendorOpened = true end)
World.Vendor.OnClosed.Add(function() vendorClosed = true end)
check(not World.Vendor.IsOpen, "Vendor closed")
harness.vendor(ids.Shopkeeper)
check(waitUntil(function() return vendorOpened end), "Vendor.OnOpened")
check(World.Vendor.IsOpen, "Vendor.IsOpen")
eq(World.Vendor.VendorId, ids.Shopkeeper, "Vendor.VendorId")
eq(#World.Vendor.Items, 2, "Vendor.Items")
eq(World.Vendor.Items[1].ObjectId, ids.VendorItemA, "Vendor.Items[1].ObjectId")
eq(World.Vendor.Items[2].Amount, 500, "Vendor.Items[2].Amount")
check(math.abs(World.Vendor.SellPriceModifier - 1.15) < 0.001, "Vendor.SellPriceModifier")
check(World.Vendor.Category:HasFlags(ObjectType.Gem), "Vendor.Category")
harness.vendor(nil)
check(waitUntil(function() return vendorClosed end), "Vendor.OnClosed")
check(not World.Vendor.IsOpen, "Vendor closed again")

-- chat
local lines, tells, locals, channels, fellows, emotes = {}, {}, {}, {}, {}, {}
World.OnChatText.Add(function(e) lines[#lines + 1] = e end)
World.OnTell.Add(function(e) tells[#tells + 1] = e end)
World.OnLocalMessage.Add(function(e) locals[#locals + 1] = e end)
World.OnChannelMessage.Add(function(e) channels[#channels + 1] = e end)
World.OnFellowMessage.Add(function(e) fellows[#fellows + 1] = e end)
World.OnEmote.Add(function(e) emotes[#emotes + 1] = e end)
harness.chat("You have 5 minutes left.", ChatMessageType.System:ToNumber())
harness.chat('<Tell:IIDString:1342177282:Friendly Player>Friendly Player<\\Tell> tells you, "buff me"', ChatMessageType.Tell)
harness.chat('Town Crier says, "Hear ye!"', ChatMessageType.Speech)
harness.chat('[General] Friendly Player says, "WTS sword"', ChatMessageType.Channels)
harness.chat('[Fellowship] Friendly Player says, "pull"', ChatMessageType.Fellowship)
harness.chat('Friendly Player waves.', ChatMessageType.Emote)
check(waitUntil(function() return #lines >= 6 end), "OnChatText saw every line")
local sys = lines[1]
eq(sys.Message, "You have 5 minutes left.", "ChatText.Message")
eq(sys.Type, ChatMessageType.System, "ChatText.Type")
eq(sys.SenderName, "", "no sender gives an empty SenderName")
eq(sys.SenderId, 0, "no sender gives SenderId 0")
eq(sys.Room, ChatChannel.None, "ChatText.Room None")
eq(sys.Eat, false, "ChatText.Eat")
eq(#tells, 1, "OnTell once")
eq(tells[1].SenderName, "Friendly Player", "Tell.SenderName")
eq(tells[1].SenderId, 1342177282, "Tell.SenderId")
eq(tells[1].Message, "buff me", "Tell.Message")
eq(#locals, 1, "OnLocalMessage once")
eq(locals[1].SenderName, "Town Crier", "Local.SenderName")
eq(locals[1].Message, "Hear ye!", "Local.Message")
eq(#channels, 1, "OnChannelMessage once")
eq(channels[1].Room, ChatChannel.General, "Channel.Room")
eq(channels[1].SenderName, "Friendly Player", "Channel.SenderName")
eq(channels[1].Message, "WTS sword", "Channel.Message")
eq(#fellows, 1, "OnFellowMessage once")
eq(fellows[1].Message, "pull", "Fellow.Message")
eq(#emotes, 1, "OnEmote once")
eq(emotes[1].Message, "Friendly Player waves.", "Emote.Message")

-- chat input: UB scripts eat their own commands with e.Eat = true
local inputs = {}
World.OnChatInput.Add(function(e)
  inputs[#inputs + 1] = e.Text
  if e.Text:sub(1, 4) == "/mm " then e.Eat = true end
end)
local eaten1 = harness.chatInput("/mm hello")
check(waitUntil(function() return #inputs >= 1 end), "OnChatInput fired")
eq(inputs[1], "/mm hello", "OnChatInput.Text")
sleep(50)
local eaten2 = harness.chatInput("/mm again")
check(eaten2, "a command a handler ate once is eaten from then on")
check(waitUntil(function() return #inputs >= 2 end), "OnChatInput fired again")
eq(inputs[2], "/mm again", "second input")
local eaten3 = harness.chatInput("hello everyone")
check(not eaten3, "plain chat is never eaten")

-- World.OnTick
local ticks = 0
World.OnTick.Add(function() ticks = ticks + 1 end)
check(waitUntil(function() return ticks >= 3 end), "World.OnTick")

done()

-- UB-style game.Actions use: UB's argument order and defaults, ActionOptions, callbacks,
-- await, action fields (ActionType, Error), RunAllOrdered, the vendor, trade and salvage
-- actions, the WorldObject action methods, and game.ActionQueue.
local ids = harness.ids
local Actions = game.Actions
local me = game.Character.Id

local function waitUntil(fn, ms)
  local t = 0
  while not fn() and t < (ms or 5000) do sleep(20); t = t + 20 end
  return fn()
end

local function invokedHas(text)
  for _, line in ipairs(harness.invoked()) do if line == text then return true end end
  return false
end

local function callsHave(prefix)
  for _, line in ipairs(harness.calls()) do if line:sub(1, #prefix) == prefix then return true end end
  return false
end

-- use with await; the action is queued already when the call returns
local use = Actions.ObjectUse(ids.Potion)
check(use.ActionType == ActionType.Misc, "ObjectUse ActionType == ActionType.Misc")
eq(use.ObjectId, ids.Potion, "ObjectUse.ObjectId")
check(game.ActionQueue.Contains(use), "ActionQueue.Contains")
await(use)
check(use.IsFinished and use.Success, "ObjectUse succeeded: " .. tostring(use.ErrorDetails))
check(use.Error == ActionError.None, "Error == ActionError.None")
eq(harness.stackOf(ids.Potion), 4, "a potion was used")

-- UB's positional callback: ObjectUse(id, target, options, callback)
local cbResult
Actions.ObjectUse(ids.Potion, 0, nil, function(a) cbResult = a end)
check(waitUntil(function() return cbResult ~= nil end), "callback ran")
check(cbResult and cbResult.Success, "callback got the finished action")

-- action:Await()
local sel = Actions.ObjectSelect(ids.Drudge)
sel:Await()
check(sel.Success, "ObjectSelect")
eq(game.World.Selected.Id, ids.Drudge, "selected")

-- ActionOptions, the UB way
local opts = ActionOptions.new()
opts.Priority = ActionType.Wield
opts.MaxRetryCount = 0
opts.TimeoutMilliseconds = 4000
local appraise = Actions.ObjectAppraise(ids.Drudge, opts)
eq(appraise.Priority, ActionType.Wield, "options Priority used")
await(appraise)
check(appraise.Success, "ObjectAppraise")
check(game.World.Get(ids.Drudge).HasAppraisalData, "appraised")
local opts2 = ActionOptions()
check(opts2 ~= nil, "ActionOptions() also constructs")

-- a failure reports UB's error names
local bad = await(Actions.ObjectUse(0x7FFFFFF0))
check(not bad.Success, "using an unknown object fails")
check(bad.Error == ActionError.InvalidSourceObject, "Error == ActionError.InvalidSourceObject, got " .. tostring(bad.Error))

-- inventory actions with UB's arguments
local move = await(Actions.ObjectMove(ids.Tapers, me, 0, true))
check(move.Success, "ObjectMove to the main pack: " .. tostring(move.ErrorDetails))
check(move.ActionType == ActionType.Inventory, "ObjectMove ActionType == ActionType.Inventory")
eq(harness.containerOf(ids.Tapers), me, "tapers moved")
local split = await(Actions.ObjectSplit(ids.Pyreals, me, 100))
check(split.Success, "ObjectSplit: " .. tostring(split.ErrorDetails))
check(split.NewStackObjectId ~= nil and split.NewStackObjectId ~= 0, "ObjectSplit.NewStackObjectId")
eq(harness.stackOf(ids.Pyreals), 2400, "source stack shrank")
eq(harness.stackOf(split.NewStackObjectId), 100, "new stack of 100")
local drop = await(Actions.ObjectDrop(split.NewStackObjectId))
check(drop.Success, "ObjectDrop")
local give = await(Actions.ObjectGive(ids.ManaStone, ids.OtherPlayer))
check(give.Success, "ObjectGive")
check(not harness.exists(ids.ManaStone), "given away")
local wield = await(Actions.ObjectWield(ids.Sword, EquipMask.MeleeWeapon))
check(wield.Success, "ObjectWield with a slot: " .. tostring(wield.ErrorDetails))
check(wield.ActionType == ActionType.Wield, "ObjectWield ActionType == ActionType.Wield")
eq(harness.wielderOf(ids.Sword), me, "sword wielded")

-- combat mode and casting
local mode = await(Actions.SetCombatMode(CombatMode.Magic))
check(mode.Success, "SetCombatMode(CombatMode.Magic)")
check(game.Character.CombatMode == CombatMode.Magic, "in magic mode")
local cast = await(Actions.CastSpell(4567, ids.Drudge))
check(cast.Success, "CastSpell: " .. tostring(cast.ErrorDetails))
check(cast.ActionType == ActionType.CastSpell, "CastSpell ActionType")
local wandCast = await(Actions.CastEquippedWandSpell(ids.Drudge))
check(wandCast.Success, "CastEquippedWandSpell: " .. tostring(wandCast.ErrorDetails))
check(callsHave(string.format("UseEquippedItem 0x%08X 0x%08X", ids.Wand, ids.Drudge)), "the wand was used on the target")

-- chat, tells, sleep, blank, autorun
check(await(Actions.InvokeChat("/say hello")).Success, "InvokeChat")
check(invokedHas("/say hello"), "InvokeChat line")
check(await(Actions.SendTellById(ids.OtherPlayer, "hi there")).Success, "SendTellById")
check(invokedHas("/t Friendly Player, hi there"), "SendTellById line")
local t0 = clock()
check(await(Actions.Sleep(150)).Success, "Sleep action")
check(clock() - t0 >= 140, "Sleep waited")
check(await(Actions.Blank()).Success, "Blank")
check(await(Actions.SetAutorun(true)).Success, "SetAutorun")
check(harness.autorun(), "autorun on")

-- RunAllOrdered
local steps = {
  Actions.ObjectSelect(ids.Crier),
  Actions.Sleep(50),
  Actions.ObjectSelect(ids.Lever),
}
local all = await(Actions.RunAllOrdered(steps))
check(all.Success, "RunAllOrdered")
eq(game.World.Selected.Id, ids.Lever, "RunAllOrdered ran in order")

-- salvage with the ust
local salvageAdd = await(Actions.SalvageAdd(ids.Tapers))
check(salvageAdd.Success, "SalvageAdd: " .. tostring(salvageAdd.ErrorDetails))
eq(harness.salvageItems()[1], ids.Tapers, "in the salvage panel")
local salvage = await(Actions.Salvage())
check(salvage.Success, "Salvage: " .. tostring(salvage.ErrorDetails))
check(not harness.exists(ids.Tapers), "salvaged")

-- the vendor
harness.vendor(ids.Shopkeeper)
check(waitUntil(function() return game.World.Vendor.IsOpen end), "vendor open")
check(await(Actions.VendorAddToBuyList(ids.VendorItemA, 2)).Success, "VendorAddToBuyList")
local buy = await(Actions.VendorBuyAll())
check(buy.Success, "VendorBuyAll: " .. tostring(buy.ErrorDetails))
check(buy.ActionType == ActionType.Vendor, "VendorBuyAll ActionType")
check(game.Character.GetFirstInventory("Mana Potion") ~= nil, "bought a Mana Potion")
eq(game.Character.GetInventoryCount("Mana Potion"), 2, "bought two")
check(await(Actions.VendorAddToSellList(ids.Ust)).Success, "VendorAddToSellList")
check(await(Actions.VendorClearSellList()).Success, "VendorClearSellList")
check(await(Actions.VendorAddToSellList(ids.Potion)).Success, "VendorAddToSellList again")
local sell = await(Actions.VendorSellAll())
check(sell.Success, "VendorSellAll: " .. tostring(sell.ErrorDetails))
check(not harness.exists(ids.Potion), "sold the potions")
check(harness.exists(ids.Ust), "the cleared item wasn't sold")
harness.vendor(nil)
check(waitUntil(function() return not game.World.Vendor.IsOpen end), "vendor closed")
local noVendor = await(Actions.VendorBuyAll())
check(noVendor.Error == ActionError.VendorNotOpen, "VendorBuyAll without a vendor: " .. tostring(noVendor.Error))

-- trade
harness.tradeStart(ids.OtherPlayer)
check(waitUntil(function() return game.Character.Trade.IsOpen end), "trade open")
local add = await(Actions.TradeAdd(ids.Ust))
check(add.Success, "TradeAdd: " .. tostring(add.ErrorDetails))
eq(game.Character.Trade.YourItemIds[1], ids.Ust, "in the trade window")
check(await(Actions.TradeAccept()).Success, "TradeAccept")
check(game.Character.Trade.YouAccepted, "accepted")
check(await(Actions.TradeDecline()).Success, "TradeDecline")
check(await(Actions.TradeReset()).Success, "TradeReset")
eq(#game.Character.Trade.YourItemIds, 0, "trade window emptied")
check(await(Actions.TradeEnd()).Success, "TradeEnd")
check(not game.Character.Trade.IsOpen, "trade ended")

-- WorldObject action methods
local lever = game.World.Get(ids.Lever)
check(await(lever:Select()).Success, "wo:Select()")
check(await(lever:Use()).Success, "wo:Use()")
local cap = game.World.Get(ids.Cap)
harness.tradeStart(ids.OtherPlayer)
check(waitUntil(function() return game.Character.Trade.IsOpen end), "trade open again")
check(await(cap:AddToTrade()).Success, "wo:AddToTrade()")
local sword = game.World.Get(ids.Sword)
local woSalvage = await(sword:Salvage())
check(woSalvage.Success, "wo:Salvage(): " .. tostring(woSalvage.ErrorDetails))
check(not harness.exists(ids.Sword), "wo:Salvage() salvaged it")

-- not available yet: these fail cleanly instead of being missing
for _, name in ipairs({ "Login", "Logout", "SkillAdvance", "AttributeAddExperience", "SkillAddExperience",
    "VitalAddExperience", "FellowCreate", "FellowDisband", "FellowDismiss", "FellowSetLeader", "FellowQuit",
    "FellowRecruit", "FellowSetOpen", "AllegianceSwear", "AllegianceBreak", "Inscribe" }) do
  check(type(Actions[name]) == "function", "Actions." .. name .. " exists")
end
local inscribe = await(Actions.Inscribe(ids.Cap, "mine"))
check(not inscribe.Success and inscribe.ErrorDetails:find("engine") ~= nil, "Inscribe fails and says why")

-- the queue
local q1 = Actions.Sleep(300, { Priority = ActionType.Misc })
local q2 = Actions.ObjectSelect(ids.Crier, { Priority = ActionType.Inventory })
check(game.ActionQueue.Contains(q2), "queued")
game.ActionQueue.Remove(q2)
check(q2.IsFinished and q2.Error == ActionError.Cancelled, "Remove cancels")
await(q1)
local n = 0
for _, a in ipairs(game.ActionQueue.Queue) do n = n + 1 end
eq(n, 0, "queue empty")

done()

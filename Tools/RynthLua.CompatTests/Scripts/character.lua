-- UB-style game.Character use: identity, stats on the weenie, inventory lists and
-- searches, spellbook, enchantments, the character events, and the trade window.
local ids = harness.ids
local Character = game.Character

local function waitUntil(fn, ms)
  local t = 0
  while not fn() and t < (ms or 3000) do sleep(20); t = t + 20 end
  return fn()
end

-- identity and simple state
eq(Character.Id, harness.player, "Character.Id")
eq(game.CharacterId, harness.player, "game.CharacterId")
eq(Character.Weenie.Name, "Tester", "Character.Weenie.Name")
eq(Character.Weenie:Value(IntId.Level), 100, "level from the weenie")
eq(Character.Weenie:Value(Int64Id.TotalExperience), 1000000, "total xp from the weenie")
eq(Character.Weenie:Value(StringId.Template), "Adventurer", "template from the weenie")
check(Character.CombatMode == CombatMode.NonCombat, "CombatMode == CombatMode.NonCombat")
eq(Character.InPortalSpace, false, "InPortalSpace")
eq(Character.Vitae, 1, "Vitae")
eq(Character.BurdenUnits, 1500, "BurdenUnits")
eq(Character.MaxBurdenUnits, 200 * 150, "MaxBurdenUnits")
eq(Character.IsBusy(), false, "IsBusy()")

-- stats the UB way (on the weenie) and with the vital numbering UB uses
local vitals = Character.Weenie.Vitals
eq(vitals[VitalId.Health].Current, 300, "Health.Current")
eq(vitals[VitalId.Health].Max, 350, "Health.Max")
eq(vitals[VitalId.Stamina].Current, 400, "Stamina.Current")
eq(vitals[VitalId.Mana].Current, 250, "Mana.Current")
eq(vitals[3].Current, 400, "a hard-coded 3 is Stamina, as in UB")
eq(vitals[5].Max, 500, "a hard-coded 5 is Mana, as in UB")
local skills = Character.Weenie.Skills
eq(skills[SkillId.WarMagic].Current, 430, "WarMagic.Current")
eq(skills[SkillId.WarMagic].Base, 380, "WarMagic.Base")
check(skills[SkillId.WarMagic].Training == SkillTrainingType.Trained, "WarMagic trained")
check(skills[SkillId.Axe].Training == SkillTrainingType.Untrained, "Axe untrained")
eq(skills[SkillId.MissleWeapons].Current, 350, "UB's MissleWeapons spelling")
eq(Character.Weenie.Attributes[AttributeId.Self].Current, 190, "Self.Current")

-- inventory as UB lists
local inv = Character.Inventory
check(#inv >= 8, "Inventory has everything carried")
local seen = {}
for _, wo in ipairs(inv) do seen[wo.Name] = true end
check(seen["Healing Potion"] and seen["Mana Stone"] and seen["Wand of Testing"], "Inventory: packs, side packs and equipment")
eq(#Character.Containers, 1, "Containers")
eq(Character.Containers[1].Name, "Backpack", "Containers[1]")
local eqp = {}
for _, wo in ipairs(Character.Equipment) do eqp[wo.Name] = true end
check(eqp["Wand of Testing"] and eqp["Leather Cap"], "Equipment")
local n = 0
for wo in Character.Inventory do n = n + 1 end
eq(n, #inv, "for wo in Character.Inventory do")

-- UB's searches
local potions = Character.GetInventory("Potion")
eq(#potions, 1, "GetInventory(name)")
eq(#Character.GetInventory(ObjectClass.SpellComponent), 1, "GetInventory(ObjectClass)")
eq(#Character.GetInventory(function(wo) return wo:Value(IntId.Value) >= 500 end), 2, "GetInventory(function)")
eq(Character.GetFirstInventory("Healing Potion").Id, ids.Potion, "GetFirstInventory(name)")
check(Character.GetFirstInventory("Nonexistent Thing") == nil, "GetFirstInventory miss")
eq(Character.GetInventoryCount("Prismatic Taper"), 100, "GetInventoryCount counts stack sizes")
eq(Character.GetInventoryCount(ObjectClass.Money), 2500, "GetInventoryCount(ObjectClass)")
eq(Character:GetInventoryCount("Healing Potion"), 5, "colon call form")
-- RynthLua's own call forms keep working
eq(#Character.Inventory("Potion"), 1, "Inventory(filter) still works")
eq(Character.InventoryCount("Prismatic Taper"), 100, "InventoryCount still works")

-- spellbook
local known = Character.SpellBook.KnownSpellsIds
eq(#known, 4, "SpellBook.KnownSpellsIds")
check(Character.SpellBook.IsKnown(2345), "SpellBook.IsKnown")
check(not Character.SpellBook.IsKnown(9999), "SpellBook.IsKnown false")
eq(Character.SpellBook.Count, 4, "SpellBook.Count")

-- enchantments
local active = Character.ActiveEnchantments()
eq(#active, 2, "ActiveEnchantments()")
local byId = {}
for _, e in ipairs(active) do byId[e.SpellId] = e end
check(byId[3456] ~= nil and byId[3456].ExpiresAt ~= nil, "enchantment 3456 has ExpiresAt")
check(byId[5000] ~= nil, "permanent enchantment 5000")
eq(#Character.AllEnchantments(), 2, "AllEnchantments()")

-- events
local vital, ench, level, xp, lum, vitae, death
local portalIn, portalOut = false, false
Character.OnVitalChanged.Add(function(e) if e.Type == VitalId.Stamina then vital = e end end)
Character.OnEnchantmentsChanged.Add(function(e) ench = e end)
Character.OnLevelChanged.Add(function(e) level = e end)
Character.OnTotalExperienceChanged.Add(function(e) xp = e end)
Character.OnAvailableLuminanceChanged.Add(function(e) lum = e end)
Character.OnVitaeChanged.Add(function(e) vitae = e end)
Character.OnPortalSpaceEntered.Add(function() portalIn = true end)
Character.OnPortalSpaceExited.Add(function() portalOut = true end)
Character.OnDeath.Add(function(e) death = e end)
sleep(700)   -- the slow polls take their baseline

harness.vitals({ Stamina = 350 })
check(waitUntil(function() return vital ~= nil end), "OnVitalChanged")
if vital then
  eq(vital.Value, 350, "OnVitalChanged.Value")
  eq(vital.OldValue, 400, "OnVitalChanged.OldValue")
end

harness.enchant(7777, 1800)
check(waitUntil(function() return ench ~= nil end), "OnEnchantmentsChanged")
if ench then
  check(ench.Type == AddRemoveEventType.Added, "OnEnchantmentsChanged.Type Added")
  eq(ench.Enchantment.SpellId, 7777, "Enchantment.SpellId")
  eq(ench.LayeredSpellId.Id, 7777, "LayeredSpellId.Id")
end
ench = nil
harness.dispel(7777)
check(waitUntil(function() return ench ~= nil end), "OnEnchantmentsChanged removed")
if ench then check(ench.Type == AddRemoveEventType.Removed, "OnEnchantmentsChanged.Type Removed") end

harness.level(101)
harness.xp(1200000)
harness.luminance(6000)
harness.vitae(0.95)
check(waitUntil(function() return level and xp and lum and vitae end), "level/xp/luminance/vitae events")
if level then eq(level.Level, 101, "Level"); eq(level.OldLevel, 100, "OldLevel") end
if xp then eq(xp.TotalExperience, 1200000, "TotalExperience"); eq(xp.OldTotalExperience, 1000000, "OldTotalExperience") end
if lum then eq(lum.AvailableLuminance, 6000, "AvailableLuminance"); eq(lum.OldAvailableLuminance, 5000, "OldAvailableLuminance") end
if vitae then check(math.abs(vitae.Vitae - 0.95) < 0.001, "Vitae") end

harness.portal(true)
check(waitUntil(function() return portalIn end), "OnPortalSpaceEntered")
harness.portal(false)
check(waitUntil(function() return portalOut end), "OnPortalSpaceExited")

harness.chat("You have been killed by a Drudge Skulker!", ChatMessageType.System)
check(waitUntil(function() return death ~= nil end), "OnDeath")
if death then
  check(death.Text:find("killed") ~= nil, "OnDeath.Text")
  eq(death.KillerId, 0, "OnDeath.KillerId (unknown: 0)")
end

-- the trade window
local Trade = Character.Trade
check(not Trade.IsOpen, "no trade open")
local started, added, accepted, completed, ended
Trade.OnStarted.Add(function(e) started = e end)
Trade.OnObjectAdded.Add(function(e) added = e end)
Trade.OnAccepted.Add(function(e) accepted = e end)
Trade.OnCompleted.Add(function(e) completed = e end)
Trade.OnEnded.Add(function(e) ended = e end)
harness.tradeStart(ids.OtherPlayer)
check(waitUntil(function() return started ~= nil end), "Trade.OnStarted")
if started then eq(started.PartnerId, ids.OtherPlayer, "OnStarted.PartnerId") end
check(Trade.IsOpen, "Trade.IsOpen")
eq(Trade.PartnerId, ids.OtherPlayer, "Trade.PartnerId")
harness.partnerAdd(ids.ChestGem)
check(waitUntil(function() return added ~= nil end), "Trade.OnObjectAdded")
if added then
  eq(added.ObjectId, ids.ChestGem, "OnObjectAdded.ObjectId")
  check(added.Side == TradeSide.Partner, "OnObjectAdded.Side")
end
eq(Trade.PartnerItemIds[1], ids.ChestGem, "Trade.PartnerItemIds")
harness.partnerAccept()
check(waitUntil(function() return accepted ~= nil end), "Trade.OnAccepted")
if accepted then eq(accepted.AcceptorId, ids.OtherPlayer, "OnAccepted.AcceptorId") end
check(Trade.PartnerAccepted, "Trade.PartnerAccepted")
harness.tradeComplete()
check(waitUntil(function() return completed ~= nil and ended ~= nil end), "Trade.OnCompleted and OnEnded")
if completed then eq(completed.PartnerItemIds[1], ids.ChestGem, "OnCompleted.PartnerItemIds") end
check(not Trade.IsOpen, "trade closed")

done()

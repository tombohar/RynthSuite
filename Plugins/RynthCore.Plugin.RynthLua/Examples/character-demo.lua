-- character-demo.lua: a tour of game.Character (RynthLua v2).
-- Prints who you are, your vitals, a few skills and attributes, burden, what you carry and
-- wear, your enchantments, then watches the character events until you stop the script.

local C = game.Character

-- ── Who and what ────────────────────────────────────────────────────────────
print(tostring(C))
print(("%s, level %d, %s mode%s"):format(C.Name, C.Level, C.CombatModeName,
  C.InPortalSpace and " (in portal space)" or ""))
print(("Vitae %.0f%%   Busy: %s"):format(C.Vitae * 100, tostring(C.Busy)))
print(("XP %d (unassigned %d)   Luminance %d / %d"):format(C.TotalExperience, C.UnassignedExperience,
  C.AvailableLuminance, C.MaximumLuminance))

-- ── Vitals: shortcuts, by name, or by VitalId ───────────────────────────────
print(("Health %d/%d   Stamina %d/%d   Mana %d/%d"):format(C.Health, C.MaxHealth,
  C.Stamina, C.MaxStamina, C.Mana, C.MaxMana))
print(tostring(C.Vitals))
local hp = C.Vitals.Health           -- same object as C.Vitals[1] or C.Vital("Health")
print(("Health: %d of %d (unbuffed max %d)"):format(hp.Current, hp.Max, hp.Base))

-- ── Skills and attributes: a SkillId number or a name ───────────────────────
for _, name in ipairs({ "WarMagic", "LifeMagic", "MeleeDefense", "Healing" }) do
  local sk = C.Skill(name)
  print(("  %-14s %4d (base %d) %s"):format(sk.Name, sk.Current, sk.Base, sk.TrainingName))
end
print("Trained skills:")
for id, sk in pairs(C.Skills) do        -- every skill, keyed by SkillId
  if sk.Training >= 2 then print(("  %s %d"):format(sk.Name, sk.Current)) end
end
local str = C.Attribute("Strength")
print(("Strength %d (base %d), Self %d"):format(str.Current, str.Base, C.Attributes.Self.Current))

-- ── Burden ──────────────────────────────────────────────────────────────────
print(("Burden %d / %d (%.0f%%)"):format(C.BurdenUnits, C.MaxBurdenUnits, C.Burden))

-- ── Inventory and equipment (WorldObjects; filters: name, ObjectClass, function) ──
local inv = C.Inventory()
print(("Carrying %d items in %d side packs"):format(#inv, #C.Containers()))
for _, wo in ipairs(C.Equipment()) do print("  wearing/wielding: " .. tostring(wo.Name)) end
print("Pyreals: " .. C.InventoryCount("Pyreal"))
print("Mana stones: " .. #C.Inventory(function(wo) return (wo.Name or ""):find("Mana Stone") ~= nil end))

-- ── Enchantments and the spellbook ──────────────────────────────────────────
local enchantments = C.Enchantments()
print(("%d enchantments:"):format(#enchantments))
for _, e in ipairs(enchantments) do
  local left = e.Permanent and "permanent" or (e.Remaining and ("%ds left"):format(math.floor(e.Remaining)) or "?")
  print(("  %s (%d): %s"):format(e.Name or "spell", e.SpellId, left))
end
print("Strength Self I active: " .. tostring(C.HasEnchantment(2)))
print(("Spellbook: %d spells; knows Strength Self I: %s"):format(C.SpellBook.Count, tostring(C.KnowsSpell(2))))

-- ── Events ──────────────────────────────────────────────────────────────────
C.OnVitalChanged.Add(function(e)
  if e.Type == VitalId.Health and e.Value < e.OldValue then
    print(("Ouch: health %d -> %d"):format(e.OldValue, e.Value))
  end
end)
C.OnCombatModeChanged.Add(function(e) print(("Combat mode %s -> %s"):format(e.OldModeName, e.NewModeName)) end)
C.OnPortalSpaceEntered.Add(function() print("Entered portal space") end)
C.OnPortalSpaceExited.Add(function() print("Left portal space") end)
C.OnEnchantmentsChanged.Add(function(e)
  print(("Enchantment %s%s: %s (%d)"):format(e.TypeName, e.Refreshed and " (refreshed)" or "",
    e.Name or "spell", e.SpellId))
end)
C.OnDeath.Add(function(e) print("You died. " .. e.Text) end)
C.OnLevelChanged.Add(function(e) print(("Level %d -> %d!"):format(e.OldLevel, e.Level)) end)
C.OnVitaeChanged.Add(function(e) print(("Vitae %.0f%% -> %.0f%%"):format(e.OldVitae * 100, e.Vitae * 100)) end)

print("Watching character events; /lua stop character-demo to end.")

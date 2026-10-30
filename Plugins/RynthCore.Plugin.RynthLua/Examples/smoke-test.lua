-- RynthLua smoke test: exercises the v2 API in game and reports PASS / FAIL / INFO.
--
-- Run it somewhere safe (it switches combat mode and back, selects and appraises things;
-- it never uses, moves, drops or gives anything):
--     /lua run smoke-test
-- Results go to this script's console and to
--     C:\Games\RynthSuite\RynthLua\Data\smoke-test\results.txt
-- Afterwards it stays running for its chat command:
--     /smoketest hello    -> answers "pong: hello" (tests script commands)
--     /smoketest stop     -> stops the script via stop() (tests game.OnScriptEnd)
-- Run it again afterwards (or after /rc rl): the "persist" checks compare with the last run.

local json    = require("json")
local storage = require("storage")
local fs      = require("filesystem").GetData()
local ai      = require("rynth.ai")
local info_m  = require("rynthlua")

local C, W, A, Q = game.Character, game.World, game.Actions, game.ActionQueue
local lines, counts = {}, { PASS = 0, FAIL = 0, INFO = 0 }

local function out(kind, name, detail)
  counts[kind] = counts[kind] + 1
  local line = kind .. "  " .. name
  if detail ~= nil and detail ~= "" then line = line .. ": " .. tostring(detail) end
  lines[#lines + 1] = line
  print(line)
end

-- check(name, fn): fn returns ok [, detail]; an error is a FAIL with the message.
local function check(name, fn)
  local ok, r, d = pcall(fn)
  if not ok then out("FAIL", name, r)
  elseif r then out("PASS", name, d)
  else out("FAIL", name, d) end
end

-- note(name, fn): fn returns a value to report; never a failure unless it errors.
local function note(name, fn)
  local ok, r = pcall(fn)
  if ok then out("INFO", name, r) else out("FAIL", name, r) end
end

local function section(title)
  lines[#lines + 1] = ""
  lines[#lines + 1] = "== " .. title
  print("== " .. title)
end

local function count(t) local n = 0; for _ in pairs(t or {}) do n = n + 1 end; return n end

local runStarted = os.time()
out("INFO", "started", os.date("%Y-%m-%d %H:%M:%S") .. " on " .. game.ServerName .. " as " .. C.Name)

-- ── Environment ───────────────────────────────────────────────────────────
section("Environment")
check("game.State is InGame", function() return game.State == "InGame", game.State end)
check("game.CharacterId", function() return game.CharacterId ~= 0, string.format("0x%08X", game.CharacterId) end)
check("game.ServerName", function() return game.ServerName ~= "", game.ServerName end)
check("game.ScriptName", function() return game.ScriptName == "smoke-test", game.ScriptName end)
check("rynthlua module", function() return info_m.Name == "RynthLua", tostring(info_m.Version) .. ", data " .. tostring(info_m.DataFolder) end)
check("clock() advances", function() local a = clock(); sleep(50); local b = clock(); return b - a >= 40, (b - a) .. " ms" end)

-- ── Persistence from the last run ─────────────────────────────────────────
section("Persistence (last run)")
local runs = storage.Get("runs", 0)
local lastStart, lastEnd = storage.Get("lastStart"), storage.Get("lastEnd")
if runs == 0 then
  out("INFO", "first run", "run again after /smoketest stop (or /rc rl) to check persistence")
else
  out("PASS", "storage kept the run count", "run #" .. (runs + 1))
  check("game.OnScriptEnd saved on the last stop", function()
    return lastEnd ~= nil and lastStart ~= nil and lastEnd >= lastStart,
      "lastStart " .. tostring(lastStart) .. ", lastEnd " .. tostring(lastEnd)
  end)
  check("storage.Character kept its value", function()
    local v = storage.Character.Get("seen")
    return v == C.Name, tostring(v)
  end)
end
storage.Set("runs", runs + 1)
storage.Set("lastStart", runStarted)
storage.Character.Set("seen", C.Name)
check("storage.Save()", function() return storage.Save() == true end)

game.OnScriptEnd.Add(function()
  storage.Set("lastEnd", os.time())
  storage.Save()
end)

-- ── Enums, json ───────────────────────────────────────────────────────────
section("Enums and json")
check("SkillId both ways", function() return SkillId.WarMagic == 34 and SkillId[34] == "WarMagic" end)
check("SkillId any case", function() return SkillId.warmagic == 34 end)
check("ObjectClass.Portal", function() return ObjectClass.Portal == 14 and ObjectClass[14] == "Portal" end)
check("CombatMode.Magic", function() return CombatMode.Magic == 8 end)
check("IntId.StackSize", function() return IntId.StackSize == 12 end)
check("ActionError.TooBusy", function() return ActionError.TooBusy == "TooBusy" end)
check("require('enums') == globals", function() return require("enums").VitalId.Mana == VitalId.Mana end)
check("json round trip", function()
  local t = json.decode(json.encode({ a = 1, b = { 1, 2, 3 }, c = "x", d = true }))
  return t.a == 1 and #t.b == 3 and t.c == "x" and t.d == true
end)
check("json empty table is []", function() return json.encode({}) == "[]", json.encode({}) end)
check("global json is RynthLua's", function() return type(json.encode) == "function" and _G.json.encode ~= nil end)
check("json error on a function", function() return not pcall(json.encode, { f = print }) end)

-- ── Character ─────────────────────────────────────────────────────────────
section("game.Character")
check("Name / Level", function() return C.Name ~= "" and C.Level > 0, C.Name .. ", level " .. C.Level end)
check("Id matches game.CharacterId", function() return C.Id == game.CharacterId end)
check("Weenie is you", function() local w = C.Weenie; return w ~= nil and w.Id == C.Id, tostring(w) end)
check("Health within Max", function() return C.Health > 0 and C.Health <= C.MaxHealth, C.Health .. "/" .. C.MaxHealth end)
check("Stamina / Mana", function() return C.MaxStamina > 0 and C.MaxMana >= 0, C.Stamina .. "/" .. C.MaxStamina .. ", " .. C.Mana .. "/" .. C.MaxMana end)
check("Vitals.Health matches", function() local v = C.Vitals.Health; return v.Current == C.Health and v.Max == C.MaxHealth, "base " .. tostring(v.Base) end)
check("Vitals[VitalId.Mana]", function() return C.Vitals[VitalId.Mana].Max == C.MaxMana end)
check("Vital('stamina')", function() return C.Vital("stamina").Max == C.MaxStamina end)
check("Skills has entries", function() local n = count(C.Skills); return n >= 30, n .. " skills" end)
check("Skill('war magic')", function()
  local s = C.Skill("war magic")
  return s.Id == 34 and type(s.Training) == "number" and type(s.TrainingName) == "string",
    s.Name .. " " .. s.Current .. " (base " .. s.Base .. ", " .. s.TrainingName .. ")"
end)
check("Skill trained flag", function()
  local s = C.Skills[SkillId.MeleeDefense]
  return s.IsTrained == (s.Training >= 2), "Melee Defense " .. s.Current .. " " .. s.TrainingName
end)
check("Attributes", function()
  local a = C.Attributes.Strength
  return a.Current > 0 and C.Attribute(AttributeId.Self).Current > 0, "Str " .. a.Current .. " (base " .. a.Base .. ")"
end)
check("CombatMode is a number", function() return type(C.CombatMode) == "number" and C.CombatModeName ~= "", C.CombatModeName end)
check("Burden", function() return C.MaxBurdenUnits > 0, C.BurdenUnits .. "/" .. C.MaxBurdenUnits .. " (" .. string.format("%.0f", C.Burden) .. "%)" end)
note("Vitae", function() return C.Vitae end)
note("Busy / InPortalSpace", function() return tostring(C.Busy) .. " / " .. tostring(C.InPortalSpace) end)
note("Experience", function() return "total " .. C.TotalExperience .. ", unassigned " .. C.UnassignedExperience end)
check("SpellBook loads", function()
  local ok = waitfor(function() return C.SpellBook.Count > 0 end, 6000)
  return ok, C.SpellBook.Count .. " spells"
end)
check("KnowsSpell(first known)", function()
  local ids = C.SpellBook.KnownSpellIds()
  if #ids == 0 then return false, "spellbook empty" end
  return C.KnowsSpell(ids[1]) and not C.KnowsSpell(65000), "spell " .. ids[1] .. " (" .. tostring(C.SpellBook.Name(ids[1])) .. ")"
end)
check("Enchantments()", function()
  local e = C.Enchantments()
  local sample = e[1] and (tostring(e[1].Name or e[1].SpellId) .. ", " .. tostring(e[1].Remaining) .. " s left") or "none active"
  return type(e) == "table", #e .. " active; " .. sample
end)
local inv = {}
check("Inventory()", function() inv = C.Inventory(); return #inv > 0, #inv .. " items" end)
local equip = {}
check("Equipment()", function() equip = C.Equipment(); return #equip > 0, #equip .. " worn/wielded" end)
check("Equipment is inside Inventory", function()
  local ids = {}
  for _, wo in ipairs(inv) do ids[wo.Id] = true end
  for _, wo in ipairs(equip) do if not ids[wo.Id] then return false, tostring(wo) .. " missing" end end
  return true
end)
check("Equipment wielded by you", function()
  for _, wo in ipairs(equip) do if wo.WielderId ~= C.Id then return false, tostring(wo) .. " wielder " .. wo.WielderId end end
  return true
end)
note("Containers()", function() local c = C.Containers(); return #c .. " side packs" end)
note("InventoryCount('Pyreal')", function() return C.InventoryCount("Pyreal") end)
check("Inventory filter by function", function()
  local packs = C.Inventory(function(wo) return wo.ObjectClass == ObjectClass.Container end)
  return #packs == #C.Containers(), #packs .. " containers"
end)

-- ── World ─────────────────────────────────────────────────────────────────
section("game.World")
check("Get(me)", function() local wo = W.Get(C.Id); return wo ~= nil and wo.Name == C.Name, tostring(wo) end)
check("World[id] / Exists", function() return W[C.Id] ~= nil and W.Exists(C.Id) and not W.Exists(1) end)
check("Get('0x...') string id", function() return W.Get(string.format("0x%08X", C.Id)) ~= nil end)
local all, land = {}, {}
check("GetAll()", function() all = W.GetAll(); return #all > #inv, #all .. " known objects" end)
check("GetLandscape()", function() land = W.GetLandscape(); return #land > 0, #land .. " on the landscape" end)
check("GetInventory() matches Character.Inventory()", function()
  local wi = W.GetInventory()
  return #wi == #inv, "World " .. #wi .. ", Character " .. #inv
end)
check("Landscape has no carried items", function()
  for _, wo in ipairs(land) do
    if wo.ContainerId ~= 0 or wo.WielderId ~= 0 or wo.Id == C.Id then return false, tostring(wo) end
  end
  return true
end)
check("class name filter == class number filter", function()
  local a, b = #W.GetAll("Monster"), #W.GetAll(ObjectClass.Monster)
  return a == b, a .. " monsters"
end)
note("portals nearby", function() return #W.GetLandscape(ObjectClass.Portal) end)
note("players nearby", function() return #W.GetLandscape(ObjectClass.Player) end)
local near
check("GetNearest(function)", function()
  near = W.GetNearest(function(wo) return wo.Name ~= "" end)
  return near ~= nil, near and (tostring(near) .. " at " .. string.format("%.1f", near.DistanceTo(C.Weenie)) .. " m") or "nothing"
end)
if near then
  check("WorldObject basics", function()
    return type(near.ObjectClass) == "number" and type(near.ObjectClassName) == "string" and near.WeenieClassId > 0,
      near.ObjectClassName .. ", wcid " .. near.WeenieClassId
  end)
  check("WorldObject Position", function()
    local p = near.Position
    return p ~= nil and p.Landcell ~= 0, p and string.format("%.2f%s %.2f%s, z %.1f, cell 0x%08X",
      math.abs(p.NS), p.NS >= 0 and "N" or "S", math.abs(p.EW), p.EW >= 0 and "E" or "W", p.Z, p.Landcell) or "nil"
  end)
  check("tostring / equality", function()
    local again = W.Get(near.Id)
    return again == near and tostring(near):find("0x") ~= nil, tostring(near)
  end)
end
local item = inv[1]
for _, wo in ipairs(inv) do if wo.ObjectClass ~= ObjectClass.Container and wo.WielderId == 0 then item = wo; break end end
if item then
  check("inventory item properties", function()
    return item.ContainerId ~= 0 and item.StackSize >= 1,
      tostring(item) .. " in " .. tostring(item.Container) .. ", stack " .. item.StackSize .. ", value " .. item:Value(IntId.Value) .. ", burden " .. item.Burden
  end)
  note("item IntValue(IntId.Value) / HasAppraisalData", function() return tostring(item.IntValue(IntId.Value, -1)) .. " / " .. tostring(item.HasAppraisalData) end)
end
local packs = C.Containers()
if #packs > 0 then
  check("pack Items()", function() local it = packs[1].Items(); return type(it) == "table", tostring(packs[1]) .. ": " .. #it .. " items" end)
end
note("Selected", function() return tostring(W.Selected) end)

-- ── Actions ───────────────────────────────────────────────────────────────
section("game.Actions")
local t0 = clock()
local sl = await(A.Sleep(300))
check("Sleep + await", function() return sl.Success and sl.IsFinished and clock() - t0 >= 280, (clock() - t0) .. " ms, Error " .. sl.Error end)

check("bad argument is an error", function() return not pcall(A.ObjectUse) end)

local selectedEvent = false
W.OnSelected.Once(function(e) selectedEvent = (item ~= nil and e.ObjectId == item.Id) end)
if item then
  local cbFired = false
  local a = await(A.ObjectSelect(item, function(act) cbFired = true end))
  check("ObjectSelect", function() return a.Success and W.Selected ~= nil and W.Selected.Id == item.Id, "Error " .. a.Error .. " " .. a.ErrorDetails end)
  check("action callback ran", function() return waitfor(function() return cbFired end, 2000) end)
  check("World.OnSelected fired", function() return waitfor(function() return selectedEvent end, 2000) end)
  local m = await(item.Select())
  check("WorldObject:Select() method", function() return m.Success, m.Error end)
end

local apTarget = near or item
if apTarget then
  local ap = await(A.ObjectAppraise(apTarget))
  check("ObjectAppraise", function() return ap.Success, tostring(apTarget) .. ", Error " .. ap.Error .. " " .. ap.ErrorDetails end)
  note("appraised HasAppraisalData", function() return tostring(apTarget.HasAppraisalData) end)
end

-- Combat mode: to a fighting mode that suits what you wield, then back.
local startMode = C.CombatMode
local fightMode = CombatMode.Melee
for _, wo in ipairs(equip) do
  if wo.ObjectClass == ObjectClass.MissileWeapon then fightMode = CombatMode.Missile end
  if wo.ObjectClass == ObjectClass.WandStaffOrb then fightMode = CombatMode.Magic end
end
local modeEvents = 0
C.OnCombatModeChanged.Add(function(e) modeEvents = modeEvents + 1 end)
local same = await(A.SetCombatMode(startMode))
check("SetCombatMode(current) is immediate", function() return same.Success, same.Error end)
local target = startMode == CombatMode.NonCombat and fightMode or CombatMode.NonCombat
local m1 = await(A.SetCombatMode(target))
check("SetCombatMode -> " .. CombatMode[target], function() return m1.Success and C.CombatMode == target, "now " .. C.CombatModeName .. ", Error " .. m1.Error .. " " .. m1.ErrorDetails end)
local m2 = await(A.SetCombatMode(startMode))
check("SetCombatMode back -> " .. CombatMode[startMode], function() return m2.Success and C.CombatMode == startMode, "now " .. C.CombatModeName .. ", Error " .. m2.Error end)
check("OnCombatModeChanged fired", function() return waitfor(function() return modeEvents >= 2 end, 2000), modeEvents .. " events" end)

local long = A.Sleep(5000)
long.Cancel()
await(long)
check("Cancel()", function() return long.IsFinished and not long.Success and long.Error == "Cancelled", long.Error end)

local s1, s2 = A.Sleep(100), A.Sleep(100)
local all2 = await(A.RunAllOrdered({ s1, s2 }))
check("RunAllOrdered", function() return all2.Success and s1.Success and s2.Success, all2.Error end)
check("ActionQueue", function() return type(Q.Count) == "number" and type(Q.IsBusy) == "boolean", "count " .. Q.Count .. ", busy " .. tostring(Q.IsBusy) end)

-- ── Events ────────────────────────────────────────────────────────────────
section("Events")
local ticks = 0
local function onTick() ticks = ticks + 1 end
game.OnTick.Add(onTick)
sleep(1000)
game.OnTick.Remove(onTick)
check("game.OnTick", function() return ticks >= 5, ticks .. " ticks in 1 s" end)
local after = ticks
sleep(300)
-- A handler call started in the same tick as Remove may still finish once.
check("Event Remove", function() return ticks <= after + 1, (ticks - after) .. " late call(s)" end)
local untilN = 0
game.OnTick.Until(function() untilN = untilN + 1; return untilN >= 3 end)
sleep(600)
check("Event Until stops at true", function() return untilN == 3, untilN end)
local vitalEvents = 0
C.OnVitalChanged.Add(function() vitalEvents = vitalEvents + 1 end)
note("Character.OnVitalChanged (seen so far)", function() return vitalEvents end)
local chatSeen = false
W.OnChatText.Once(function(e) chatSeen = true end)
chat("[smoke-test] chat line to your own window")
note("World.OnChatText from chat()", function() return tostring(waitfor(function() return chatSeen end, 1500)) end)

-- ── RynthAi (rynth.ai module and v1 globals) ──────────────────────────────
section("RynthAi")
if not ai.Available then
  out("INFO", "rynth.ai", "RynthAi not loaded, skipped")
else
  out("PASS", "rynth.ai Available")
  check("SettingNames()", function() local n = ai.SettingNames(); return #n > 0, #n .. " settings" end)
  note("MacroRunning", function() return tostring(ai.MacroRunning) end)
  note("Evaluate('getcharintprop[25]') (level)", function() return tostring(ai.Evaluate("getcharintprop[25]")) end)
  if near then note("ai.DistanceTo(nearest)", function() return ai.DistanceTo(near) end) end
  note("ai.FindNearest('Portal', true)", function() local wo, d = ai.FindNearest("Portal", true); return tostring(wo) .. " " .. tostring(d) end)
  -- v1 globals (me, expr, ...) come from RynthAi too.
  check("v1 me.name", function() return me.name == C.Name end)
  check("v1 me.maxhealth", function() return me.maxhealth == C.MaxHealth, "me " .. tostring(me.maxhealth) .. ", Character " .. C.MaxHealth .. " (base " .. C.Vitals.Health.Base .. ")" end)
  check("v1 me.maxmana", function() return me.maxmana == C.MaxMana, "me " .. tostring(me.maxmana) .. ", Character " .. C.MaxMana end)
end

-- ── Command, results ──────────────────────────────────────────────────────
section("Done")
game.RegisterCommand("/smoketest", function(args)
  local line = "INFO  /smoketest " .. args .. ": command received"
  print(line)
  local prev = fs.ReadText("results.txt") or ""
  fs.WriteText("results.txt", prev .. "\n" .. line)
  if args == "stop" then
    print("stopping via stop()")
    stop()
  else
    chat("[smoke-test] pong: " .. args)
  end
end)

local summary = string.format("%d passed, %d failed, %d info", counts.PASS, counts.FAIL, counts.INFO)
lines[#lines + 1] = ""
lines[#lines + 1] = summary
print(summary)
local ok, err = fs.WriteText("results.txt", table.concat(lines, "\n"))
if not ok then print("couldn't write results.txt: " .. tostring(err)) end
chat("[smoke-test] " .. summary .. ". Now try /smoketest hello, then /smoketest stop.")

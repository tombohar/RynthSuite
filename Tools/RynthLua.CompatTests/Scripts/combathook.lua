-- RynthAi's combat hook from a script (rynth.ai SetAttackHandler / SetHealHandler, RynthLua's
-- CombatHookPump against a fake "RynthAi.Combat" table): the handler's answer reaches Decide, a
-- yielding handler still answers, an error declines, three in a row unregister, one script per
-- kind, a stopped script releases its kinds, and no table means a refusal with the reason.
local ai = require("rynth.ai")
local ATTACK, HEAL = 1, 2

local function decisions() return harness.combatDecisions() end
local function waitDecisions(n)
  return waitfor(function() return #decisions() >= n end, 3000)
end

-- No table: RynthAi without the hook (or not loaded).
harness.combatTable(false)
local ok, why = ai.SetAttackHandler(function() return 1 end)
eq(ok, false, "no combat table: refused")
eq(why, "RynthAi has no combat hook", "and says why")
eq(ai.CombatHook.Supported, false, "CombatHook.Supported is false")
eq(ai.CombatHook.AttackOwner, nil, "no owner")

harness.combatTable(true)
sleep(10)   -- RynthLua looks the table up again on its next tick
eq(ai.CombatHook.Supported, true, "table on: supported")

-- 1. A decision is posted, with the request's context.
local seen
ok = ai.SetAttackHandler(function(ctx)
  seen = ctx
  return 1234, ctx.monsters[2].id
end)
eq(ok, true, "attack handler registered")
eq(ai.CombatHook.AttackOwner, "combathook", "the owner is this script")
check(waitfor(function() return harness.combatState().kinds == ATTACK end, 2000), "the pump claims the attack kind")
eq(harness.combatState().owner, "RynthLua/combathook", "claimed under RynthLua/<script>")
eq(ai.CombatHook.Claimed, true, "Claimed")

harness.combatScanned({ { 0x80000101, 3.5 }, { 0x80000102, 7.25 } })
local seq1 = harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(1), "the handler answered")
local d = decisions()[1]
eq(d.seq, seq1, "decided for the open seq")
eq(d.kind, ATTACK, "attack kind")
eq(d.spell, 1234, "the handler's spell")
eq(d.target, 0x80000102, "the handler's target")
eq(d.flags, 0, "no flags")
eq(d.taken, true, "taken by RynthAi")
eq(seen.seq, seq1, "ctx.seq")
eq(seen.kind, "attack", "ctx.kind")
eq(seen.targetId, 0x80000101, "ctx.targetId")
eq(seen.targetHealth, 0.5, "ctx.targetHealth")
eq(seen.healthPct, 80, "ctx.healthPct")
eq(seen.inCombat, true, "ctx.inCombat")
eq(seen.combatMode, 8, "ctx.combatMode")
check(seen.deadline > seen.issuedAt, "ctx.deadline after ctx.issuedAt")
check(seen.deadline > clock(), "ctx.deadline is on clock()'s scale and still ahead")
eq(#seen.monsters, 2, "ctx.monsters")
eq(seen.monsters[1].distance, 3.5, "ctx.monsters[1].distance")
eq(seen.last.seq, 0, "no resolved cast yet")
eq(seen.pending, nil, "nothing in flight yet")

-- ctx.pending: the cast in flight; ctx.last: the most recent RESOLVED cast, err final.
local seq2 = harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(2), "second request answered")
eq(seen.pending.seq, seq1, "ctx.pending.seq: the first cast, no answer yet")
eq(seen.pending.spell, 1234, "ctx.pending.spell")
eq(seen.pending.target, 0x80000102, "ctx.pending.target")
eq(seen.last.seq, 0, "ctx.last: still no resolved cast")
eq(seen.last.err, nil, "ctx.last.err: nil before the first resolved cast")

harness.combatResolve(0x1D)     -- the first cast's UseDone: 0x1D
local seqE = harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(3), "third request answered")
eq(seen.last.seq, seq1, "ctx.last: the first cast, now resolved")
eq(seen.last.spell, 1234, "ctx.last.spell")
eq(seen.last.target, 0x80000102, "ctx.last.target")
eq(seen.last.err, 29, "ctx.last.err = 0x1D (refused 'too busy')")
eq(seen.pending.seq, seq2, "ctx.pending: the second cast")

-- No answer for the second before the third went out: resolved with no answer (nil).
harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(4), "fourth request answered")
eq(seen.last.seq, seq2, "ctx.last: the second cast")
eq(seen.last.err, nil, "no answer read for it: nil")
eq(seen.pending.seq, seqE, "ctx.pending: the third cast")

harness.combatResolve(0)
harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(5), "fifth request answered")
eq(seen.last.seq, seqE, "ctx.last: the third cast")
eq(seen.last.err, 0, "ctx.last.err = 0 (done)")

harness.combatResolve(0x1D)
harness.combatOlderRynthAi(true)
harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(6), "older RynthAi request answered")
eq(seen.last.err, nil, "an older RynthAi: no err")
eq(seen.pending, nil, "an older RynthAi: no pending")
check(seen.last.seq > seqE, "an older RynthAi: ctx.last is its last decision")
harness.combatOlderRynthAi(false)

-- 2. A yielding handler (the same script registering again replaces its handler), area flag.
ok = ai.SetAttackHandler(function(ctx)
  sleep(60)
  return 55, nil, "area"
end)
eq(ok, true, "the same script replaces its handler")
local seq3 = harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(7), "the yielding handler still answers")
d = decisions()[7]
eq(d.seq, seq3, "for its seq")
eq(d.spell, 55, "its spell")
eq(d.target, 0, "nil target = the request's target")
eq(d.flags, 1, "\"area\" sets the AREA flag")
eq(d.taken, true, "taken")

-- 3. nil declines.
ai.SetAttackHandler(function(ctx) return nil end)
harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(8), "a nil answer is still an answer")
eq(decisions()[8].spell, 0, "nil = decline (spell 0)")

-- 4. An error declines at once; three in a row unregister.
ai.SetAttackHandler(function(ctx) error("boom (expected)") end)
for i = 1, 3 do
  harness.combatOpen(ATTACK, 0x80000101)
  check(waitDecisions(8 + i), "error " .. i .. ": declined at once")
  eq(decisions()[8 + i].spell, 0, "error " .. i .. ": spell 0")
  eq(decisions()[8 + i].taken, true, "error " .. i .. ": taken (no wait for the deadline)")
end
eq(ai.CombatHook.AttackOwner, nil, "three errors: the handler is removed")
check(waitfor(function() return harness.combatState().kinds == 0 end, 2000), "and the kind released in RynthAi")
local unregistered = false
for _, line in ipairs(harness.chatLog()) do
  if line:find("failed 3 times in a row", 1, true) then unregistered = true end
end
check(unregistered, "a chat warning says so")
check(harness.scriptConsole("combathook"):find("combat attack handler failed (declined; 3/3 in a row)", 1, true) ~= nil, "each error is in the console")
local _, booms = harness.scriptConsole("combathook"):gsub("boom %(expected%)", "")
eq(booms, 3, "each Lua error's message is printed once (by the script host), not again by the pump")

-- A bad answer counts as an error too (and declines).
ai.SetAttackHandler(function(ctx) return "fireball" end)
harness.combatOpen(ATTACK, 0x80000101)
check(waitDecisions(12), "a bad answer declines")
eq(decisions()[12].spell, 0, "spell 0")
check(harness.scriptConsole("combathook"):find("expected a spell id", 1, true) ~= nil, "and says what was wrong")
ai.ClearCombatHandlers()
eq(ai.CombatHook.AttackOwner, nil, "ClearCombatHandlers drops it")

-- 5. One script per kind; a stopped script releases its kinds.
ok = ai.SetHealHandler(function(ctx) return 2001, 0 end)
eq(ok, true, "heal handler registered")
harness.runScript("rival", [[
  local ai = require("rynth.ai")
  local ok, why = ai.SetHealHandler(function(ctx) return 9 end)
  eq(ok, false, "rival: the heal kind is taken")
  eq(why, "the heal handler is already set by combathook", "rival: and by whom")
  ok = ai.SetAttackHandler(function(ctx) return 777 end)
  eq(ok, true, "rival: the attack kind is free")
  rivalReady = true
]])
check(waitfor(function() return ai.CombatHook.AttackOwner == "rival" end, 2000), "the rival holds attack")
eq(ai.CombatHook.HealOwner, "combathook", "this script still holds heal")
check(waitfor(function() return harness.combatState().kinds == ATTACK + HEAL end, 2000), "both kinds claimed")
eq(harness.combatState().owner, "RynthLua/combathook+rival", "under both scripts' names")
check(harness.scriptLoaded("rival"), "the rival's main chunk ended but its handler keeps it running")

local healSeq = harness.combatOpen(HEAL, 0)
check(waitDecisions(13), "heal answered")
eq(decisions()[13].seq, healSeq, "heal seq")
eq(decisions()[13].kind, HEAL, "heal kind")
eq(decisions()[13].spell, 2001, "heal spell")

local releasesBefore = harness.combatState().releases
check(harness.stopScript("rival"), "stop the rival")
check(not harness.scriptLoaded("rival"), "stopped")
check(harness.combatState().releases > releasesBefore, "stopping it released in RynthAi")
check(harness.combatState().releasedKinds % 2 == 1, "the attack kind among the released")
eq(ai.CombatHook.AttackOwner, nil, "attack is free again")
eq(ai.CombatHook.HealOwner, "combathook", "heal untouched")
check(waitfor(function() return harness.combatState().kinds == HEAL end, 2000), "only heal claimed now")

-- SetHealHandler(nil) clears this script's heal handler.
ai.SetHealHandler(nil)
eq(ai.CombatHook.HealOwner, nil, "nil clears it")
check(waitfor(function() return harness.combatState().kinds == 0 end, 2000), "nothing claimed")

-- 6. Flags: "nojudge", a table of flags, true = area; an unknown flag is an error.
local function answerWith(fn)
  ai.SetAttackHandler(fn)
  local n = #decisions()
  harness.combatOpen(ATTACK, 0x80000101)
  check(waitDecisions(n + 1), "answered")
  return decisions()[n + 1]
end
eq(answerWith(function() return 55, nil, "nojudge" end).flags, 2, "\"nojudge\" sets the NOJUDGE flag")
eq(answerWith(function() return 55, nil, { area = true, nojudge = true } end).flags, 3, "a table sets both")
eq(answerWith(function() return 55, nil, { nojudge = true, area = false } end).flags, 2, "a false field is no flag")
eq(answerWith(function() return 55, nil, true end).flags, 1, "true is still \"area\"")
d = answerWith(function() return 55, nil, "aera" end)
eq(d.spell, 0, "an unknown flag declines")
check(harness.scriptConsole("combathook"):find('expected "area", "nojudge"', 1, true) ~= nil, "and says what it expected")
ai.ClearCombatHandlers()

-- 7. Set/clear cycles register the script's KeepAlive / OnStop hooks once, not once per cycle.
ai.SetAttackHandler(function() return nil end)
local hooks = harness.scriptHooks("combathook")
check(hooks.keepAlive >= 1 and hooks.onStop >= 1, "the hooks are registered")
for i = 1, 3 do
  ai.ClearCombatHandlers()
  ai.SetAttackHandler(function() return nil end)
  ai.SetHealHandler(function() return nil end)
end
local after = harness.scriptHooks("combathook")
eq(after.keepAlive, hooks.keepAlive, "no KeepAlive hook added by the cycles")
eq(after.onStop, hooks.onStop, "no OnStop hook added by the cycles")
ai.ClearCombatHandlers()
check(waitfor(function() return harness.combatState().kinds == 0 end, 2000), "nothing claimed")

-- 8. A handler that never returns is aborted after the deadline plus the stall budget and counts
--    as an error; three in a row remove it and release the kind.
ai.SetAttackHandler(function(ctx) while true do sleep(50) end end)
check(waitfor(function() return harness.combatState().kinds == ATTACK end, 2000), "the stalling handler's kind is claimed")
for i = 1, 3 do
  local n = #decisions()
  local t0 = clock()
  harness.combatOpen(ATTACK, 0x80000101, 100)
  check(waitDecisions(n + 1), "stall " .. i .. ": the stuck handler is aborted and declines")
  eq(decisions()[n + 1].spell, 0, "stall " .. i .. ": spell 0")
  check(clock() - t0 >= 1000, "stall " .. i .. ": only after the deadline plus the stall budget")
end
eq(ai.CombatHook.AttackOwner, nil, "three stalls: the handler is removed")
check(waitfor(function() return harness.combatState().kinds == 0 end, 2000), "and the kind released in RynthAi")
local stalledLine = false
for _, line in ipairs(harness.chatLog()) do
  if line:find("did not answer within the deadline 3 times in a row", 1, true) then stalledLine = true end
end
check(stalledLine, "a chat line says the handler did not answer within the deadline")
check(harness.scriptConsole("combathook"):find("the handler did not answer within the deadline", 1, true) ~= nil, "each stall is in the console")

-- 8b. A handler that answers after the deadline but within the stall budget: RynthAi has already
--     chosen for itself, so the answer is wasted. It declines and counts as a stall; three in a row
--     remove the handler.
ai.SetAttackHandler(function(ctx) sleep(300); return 4321 end)
check(waitfor(function() return harness.combatState().kinds == ATTACK end, 2000), "the late handler's kind is claimed")
for i = 1, 3 do
  local n = #decisions()
  local t0 = clock()
  harness.combatOpen(ATTACK, 0x80000101, 100)
  check(waitDecisions(n + 1), "late " .. i .. ": an answer comes")
  eq(decisions()[n + 1].spell, 0, "late " .. i .. ": declined (spell 0), not its late spell")
  check(clock() - t0 < 1000, "late " .. i .. ": at the late answer, not after the stall budget")
end
eq(ai.CombatHook.AttackOwner, nil, "three late answers: the handler is removed")
check(waitfor(function() return harness.combatState().kinds == 0 end, 2000), "and the kind released in RynthAi")
check(harness.scriptConsole("combathook"):find("answered after the deadline", 1, true) ~= nil, "each late answer is in the console")

-- Control: the same handler with a deadline it meets is taken and keeps its kind.
ai.SetAttackHandler(function(ctx) sleep(300); return 4321 end)
check(waitfor(function() return harness.combatState().kinds == ATTACK end, 2000), "claimed again")
for i = 1, 3 do
  local n = #decisions()
  harness.combatOpen(ATTACK, 0x80000101, 2000)
  check(waitDecisions(n + 1), "in time " .. i .. ": answered")
  eq(decisions()[n + 1].spell, 4321, "in time " .. i .. ": its spell")
end
eq(ai.CombatHook.AttackOwner, "combathook", "answers in time: the handler stays")
ai.ClearCombatHandlers()
check(waitfor(function() return harness.combatState().kinds == 0 end, 2000), "nothing claimed")

-- 9. RynthAi reloads (its seq restarts): a new request whose seq repeats the last one started is
--    still answered.
ai.SetAttackHandler(function(ctx) return 4242 end)
harness.combatReload()
local before = #decisions()
eq(harness.combatOpen(ATTACK, 0x80000101), 1, "after a reload the first request is seq 1")
check(waitDecisions(before + 1), "answered")
harness.combatReload()   -- again: the next request is seq 1 too, the same seq as the last one started
before = #decisions()
eq(harness.combatOpen(ATTACK, 0x80000101), 1, "seq 1 again")
check(waitDecisions(before + 1), "the repeated seq is still answered (it is a new request)")
eq(decisions()[before + 1].spell, 4242, "with the handler's spell")
eq(decisions()[before + 1].taken, true, "taken")
ai.ClearCombatHandlers()

-- 10. Replacing a handler while it is still answering: the new one answers the open request.
local slowStarted = false
ai.SetAttackHandler(function(ctx) slowStarted = true; sleep(400); return 11 end)
check(waitfor(function() return harness.combatState().kinds == ATTACK end, 2000), "claimed")
before = #decisions()
harness.combatOpen(ATTACK, 0x80000101)
check(waitfor(function() return slowStarted end, 2000), "the slow handler is running")
ai.SetAttackHandler(function(ctx) return 77 end)
check(waitDecisions(before + 1), "the replacement answers the open request")
eq(decisions()[before + 1].spell, 77, "the new handler's answer")
eq(decisions()[before + 1].taken, true, "taken")
sleep(500)
eq(#decisions(), before + 1, "the replaced handler's answer never arrives")
ai.ClearCombatHandlers()
check(waitfor(function() return harness.combatState().kinds == 0 end, 2000), "nothing claimed")

-- 11. Examples\combat-handler.lua, with its spell ids and lines set.
local example = harness.exampleCode("combat-handler.lua")
local code = example
local n1, n2, n3, n4
code, n1 = code:gsub("local FINISHER_SPELL = 0", "local FINISHER_SPELL = 3001", 1)
code, n2 = code:gsub("local FINISH_BELOW   = 0%.30", "local FINISH_BELOW   = 0.60", 1)
code, n3 = code:gsub("local HEAL_SPELL     = 0", "local HEAL_SPELL     = 3002", 1)
code, n4 = code:gsub("local HEAL_BELOW     = 50", "local HEAL_BELOW     = 90", 1)
eq(n1 + n2 + n3 + n4, 4, "example: its four settings found")
harness.runScript("combat-handler", code)
check(waitfor(function() return ai.CombatHook.AttackOwner == "combat-handler" and ai.CombatHook.HealOwner == "combat-handler" end, 2000),
  "example: holds attack and heal")
check(harness.scriptConsole("combat-handler"):find("combat handlers on", 1, true) ~= nil, "example: says it is on")
before = #decisions()
harness.combatOpen(ATTACK, 0x80000101)   -- target health 0.5, under its 0.60
check(waitDecisions(before + 1), "example: attack answered")
eq(decisions()[before + 1].spell, 3001, "example: its finisher under the line")
eq(decisions()[before + 1].target, 0, "example: at RynthAi's target")
harness.combatOpen(HEAL, 0)              -- health 80%, under its 90
check(waitDecisions(before + 2), "example: heal answered")
eq(decisions()[before + 2].spell, 3002, "example: its heal under the line")
eq(decisions()[before + 2].target, 0, "example: on yourself")
harness.chatInput("/combathook")
check(waitfor(function() return ai.CombatHook.AttackOwner == nil and harness.combatState().kinds == 0 end, 2000), "example: /combathook turns it off")
harness.chatInput("/combathook")
check(waitfor(function() return ai.CombatHook.AttackOwner == "combat-handler" end, 2000), "example: and on again")
check(harness.stopScript("combat-handler"), "example: stopped")
eq(ai.CombatHook.AttackOwner, nil, "example: stopping releases attack")
eq(ai.CombatHook.HealOwner, nil, "example: and heal")

-- As shipped (ids 0) it declines; a kind held by another script is refused with the reason.
ai.SetAttackHandler(function() return nil end)
harness.runScript("combat-handler", example)
check(waitfor(function() return ai.CombatHook.HealOwner == "combat-handler" end, 2000), "shipped example: holds heal")
check(harness.scriptConsole("combat-handler"):find("attack handler refused: the attack handler is already set by combathook", 1, true) ~= nil,
  "shipped example: prints why attack was refused")
before = #decisions()
harness.combatOpen(HEAL, 0)
check(waitDecisions(before + 1), "shipped example: heal answered")
eq(decisions()[before + 1].spell, 0, "shipped example: no heal id set, so it declines")
harness.stopScript("combat-handler")
ai.ClearCombatHandlers()

done()

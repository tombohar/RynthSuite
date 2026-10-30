-- combat-handler.lua: choose RynthAi's attack and heal spells from a script (the combat hook).
--
-- RynthAi still picks the target, the stance and the pacing. When it is about to cast it asks
-- the handler, waits until ctx.deadline at most, and casts the answer. A handler returns
--   spellId [, targetId [, flags]]   targetId nil = ctx.targetId (attack) / yourself (heal)
--   nil                              let RynthAi choose this time
-- flags: "area" (a ring/wall cast) or "nojudge" (not meant to damage; no miss counting).
-- /combathook turns the handlers on and off. Set the spell ids below to spells you know.

local ai = require("rynth.ai")

local FINISHER_SPELL = 0      -- attack spell id, cast while the target is under FINISH_BELOW
local FINISH_BELOW   = 0.30   -- the target's health ratio, 0..1
local HEAL_SPELL     = 0      -- heal spell id, cast on yourself under HEAL_BELOW
local HEAL_BELOW     = 50     -- your health percent

local function onAttack(ctx)
  -- ctx.targetHealth is -1 while RynthAi has no health for the target.
  if FINISHER_SPELL ~= 0 and ctx.targetHealth >= 0 and ctx.targetHealth < FINISH_BELOW then
    return FINISHER_SPELL
  end
  return nil                     -- RynthAi casts its own choice
end

local function onHeal(ctx)
  if HEAL_SPELL ~= 0 and ctx.healthPct >= 0 and ctx.healthPct < HEAL_BELOW then
    return HEAL_SPELL, 0         -- target 0 = yourself
  end
  return nil                     -- kits, Heal Self and potions as usual
end

local enabled = false

local function enable()
  local okAttack, whyAttack = ai.SetAttackHandler(onAttack)
  if not okAttack then print("attack handler refused: " .. tostring(whyAttack)) end
  local okHeal, whyHeal = ai.SetHealHandler(onHeal)
  if not okHeal then print("heal handler refused: " .. tostring(whyHeal)) end
  enabled = okAttack or okHeal
  print("combat handlers " .. (enabled and "on" or "off"))
end

local function disable()
  ai.ClearCombatHandlers()
  enabled = false
  print("combat handlers off: RynthAi chooses every spell")
end

game.RegisterCommand("/combathook", function()
  if enabled then disable() else enable() end
end)

-- Stopping the script releases the handlers anyway; clearing them here says so explicitly.
game.OnScriptEnd.Add(function() ai.ClearCombatHandlers() end)

enable()

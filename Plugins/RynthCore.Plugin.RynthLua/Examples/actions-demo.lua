-- actions-demo.lua: the action queue (game.Actions).
--
-- AC does one use / cast / move at a time. Every game.Actions call (and every WorldObject
-- action method: obj.Use(), obj.Give(npc), ...) queues an action and returns it at once;
-- await(action) waits until it has finished and returns it. Then:
--   action.Success       true / false
--   action.Error         "None", "TooBusy", "TimedOut", "InvalidSourceObject", ... (ActionError.X)
--   action.ErrorDetails  what happened, in words
--   action.Result        what it made (the new stack of a split, the appraised object, ...)
-- Options go in a table after the arguments: { Priority, TimeoutMilliseconds, MaxRetryCount,
-- SkipChecks, Name }; a callback function, too, gets the action when it finishes.

local KIT_NAME = "Healing Kit"   -- something in your pack you can use on yourself
local SPELL_ID = 2               -- Strength Self I

local function report(a)
  if a.Success then
    print(("%s: done in %d ms"):format(a.Name, a.FinishedAt - a.StartedAt))
  else
    print(("%s failed: %s (%s)"):format(a.Name, a.Error, a.ErrorDetails))
  end
end

-- 1. Use an item on yourself and wait for the server to finish it.
local kit = game.World.GetInventory(KIT_NAME)[1]
if kit then
  report(await(kit.UseOn(game.CharacterId)))      -- same as game.Actions.ObjectUseOn(kit, game.CharacterId)
else
  print("No " .. KIT_NAME .. " in your pack; skipping the use.")
end

-- 2. Failures are values, not errors: check Success / Error.
local bad = await(game.Actions.ObjectUse(0x7FFFFFF0))   -- no such object
if bad.Error == ActionError.InvalidSourceObject then
  print("As expected: " .. bad.ErrorDetails)
end

-- 3. A callback instead of await: the script carries on, the function runs when it's done.
local target = game.World.Selected or game.Character.Weenie
if target then
  game.Actions.ObjectAppraise(target, function(a)
    if a.Success then print("Appraised " .. a.Result.Name) else report(a) end
  end)
end

-- 4. A cast. Fizzles and "You're too busy!" are retried (MaxRetryCount, 3 by default).
local mode = await(game.Actions.SetCombatMode("Magic"))
if mode.Success then
  local cast = await(game.Actions.CastSpell(SPELL_ID, 0, { MaxRetryCount = 2, TimeoutMilliseconds = 8000 }))
  report(cast)
  if cast.CurrentRetryCount > 0 then print("  (took " .. (cast.CurrentRetryCount + 1) .. " tries)") end
else
  report(mode)
end
await(game.Actions.SetCombatMode("NonCombat"))

-- 5. Several steps as one queue entry, in order; stops at the first failure.
local steps = await(game.Actions.RunAllOrdered({
  game.Actions.Sleep(500),
  game.Actions.InvokeChat("/e stretches."),
}))
report(steps)

print("Queue now: " .. tostring(game.ActionQueue))

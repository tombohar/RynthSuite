-- UB-style event handling: Add/Once/Until/Remove in either case and call style, handlers
-- that sleep, several handlers on one event, errors that don't unsubscribe, event args
-- that can be written to (Eat), and the game-level events.

local function waitUntil(fn, ms)
  local t = 0
  while not fn() and t < (ms or 3000) do sleep(20); t = t + 20 end
  return fn()
end

local chat = game.World.OnChatText

-- Add, in UB's call styles
local a, b, c = 0, 0, 0
local fa = function(e) a = a + 1 end
chat.Add(fa)
chat:Add(function(e) b = b + 1 end)
chat.add(function(e) c = c + 1 end)            -- lower-case verb
harness.chat("one", 0)
check(waitUntil(function() return a == 1 and b == 1 and c == 1 end), "Add / :Add / add all fire")

-- Remove (both cases)
chat.Remove(fa)
harness.chat("two", 0)
check(waitUntil(function() return b == 2 and c == 2 end), "others still fire")
eq(a, 1, "Remove(fn) unsubscribed")

-- Once
local once = 0
chat.Once(function(e) once = once + 1 end)
local once2 = 0
chat.once(function(e) once2 = once2 + 1 end)
harness.chat("three", 0)
harness.chat("four", 0)
check(waitUntil(function() return c == 4 end), "lines delivered")
eq(once, 1, "Once fires once")
eq(once2, 1, "once fires once")

-- Until: kept until the handler returns true
local seen = {}
chat.Until(function(e)
  seen[#seen + 1] = e.Message
  return e.Message == "stop"
end)
harness.chat("go", 0)
harness.chat("stop", 0)
harness.chat("after", 0)
check(waitUntil(function() return c == 7 end), "lines delivered again")
eq(#seen, 2, "Until stopped after returning true")
eq(seen[2], "stop", "Until saw the stopping line")

-- handlers can sleep and still see their own args
local slept
chat.Add(function(e)
  local msg = e.Message
  sleep(100)
  if msg == "sleepy" then slept = e.Message end
end)
harness.chat("sleepy", 0)
check(waitUntil(function() return slept == "sleepy" end), "a handler that sleeps keeps its args")

-- args are writable (UB scripts set e.Eat)
local wrote
chat.Add(function(e) e.Eat = true; wrote = e.Eat end)
harness.chat("eat me", 0)
check(waitUntil(function() return wrote == true end), "e.Eat can be set")

-- an error in a handler is reported but the handler stays subscribed
local calls = 0
game.World.OnTell.Add(function(e)
  calls = calls + 1
  if calls == 1 then error("first tell fails (expected)") end
end)
harness.chat('Friendly Player tells you, "one"', ChatMessageType.Tell)
harness.chat('Friendly Player tells you, "two"', ChatMessageType.Tell)
check(waitUntil(function() return calls == 2 end), "handler still subscribed after an error")

-- game-level events
local ticks = 0
local tick = function() ticks = ticks + 1 end
game.OnTick.Add(tick)
check(waitUntil(function() return ticks >= 3 end), "game.OnTick")
game.OnTick.Remove(tick)
local frozen = ticks
sleep(100)
eq(ticks, frozen, "game.OnTick removed")
check(game.OnStateChanged ~= nil and game.OnScriptEnd ~= nil, "game.OnStateChanged / OnScriptEnd exist")
game.OnScriptEnd.Add(function() end)

-- tostring of an event
check(tostring(chat):find("OnChatText") ~= nil, "tostring(event)")

done()

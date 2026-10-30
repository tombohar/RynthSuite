-- UB-style timing: sleep(ms) and await(action) as globals, in the main chunk, in handlers
-- and in callbacks; coroutines; plus RynthLua's spawn / waitfor / clock helpers.

-- sleep in the main chunk
local t0 = clock()
sleep(120)
check(clock() - t0 >= 110, "sleep(ms) waits")
sleep(0)
check(true, "sleep(0) returns")

-- await an action in the main chunk and in a callback
local a = await(game.Actions.Sleep(80))
check(a.IsFinished and a.Success, "await returns the finished action")
local inner
game.Actions.Sleep(30, nil, function(res)
  inner = await(game.Actions.Sleep(30))
end)
local t = 0
while inner == nil and t < 3000 do sleep(20); t = t + 20 end
check(inner ~= nil and inner.Success, "await inside an action callback")

-- sleep inside an event handler doesn't block other handlers
local order = {}
game.World.OnChatText.Add(function(e)
  if e.Message == "slow" then sleep(200) end
  order[#order + 1] = e.Message
end)
harness.chat("slow", 0)
harness.chat("fast", 0)
t = 0
while #order < 2 and t < 3000 do sleep(20); t = t + 20 end
eq(order[1], "fast", "a sleeping handler doesn't hold up the next event")
eq(order[2], "slow", "the sleeping handler finishes later")

-- plain Lua coroutines work alongside
local co = coroutine.create(function(x)
  local y = coroutine.yield(x + 1)
  return y * 2
end)
local ok1, v1 = coroutine.resume(co, 1)
local ok2, v2 = coroutine.resume(co, 10)
check(ok1 and v1 == 2 and ok2 and v2 == 20, "coroutine.create / resume / yield")
local gen = coroutine.wrap(function() for i = 1, 3 do coroutine.yield(i) end end)
eq(gen() + gen() + gen(), 6, "coroutine.wrap")

-- spawn / waitfor
local flag = false
spawn(function(msg)
  sleep(50)
  flag = (msg == "go")
end, "go")
check(waitfor(function() return flag end, 2000), "spawn + waitfor")
check(not waitfor(function() return false end, 100), "waitfor times out")

-- the action object's Await()
local b = game.Actions.Sleep(40)
b.Await()
check(b.IsFinished, "action.Await()")

-- a long loop doesn't freeze anything (auto-yield), and sleeping in it keeps ticks going
local ticks = 0
game.OnTick.Add(function() ticks = ticks + 1 end)
local sum = 0
for i = 1, 200000 do sum = sum + i end
eq(sum, 200000 * 200001 / 2, "long loop finished")
sleep(100)
check(ticks > 0, "ticks kept coming")

-- os time functions of the sandbox
check(type(os.time()) == "number" and type(os.clock()) == "number", "os.time / os.clock")
check(type(os.date("%Y")) == "string", "os.date")

done()

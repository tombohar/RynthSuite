-- "/lua <name> [args]" runs a script's game.RegisterCommand("/name") handler. In a Decal
-- (bridge-mode) client the engine only forwards /lua to the plugin, so this is the only way
-- to reach a script command there. Built-in /lua verbs always win over a script command.
local calls = {}
game.RegisterCommand("/demo", function(args) calls[#calls + 1] = args end)
game.RegisterCommand("/list", function(args) calls[#calls + 1] = "SHADOW:" .. args end)

local function settle(ms)
  local t = 0
  while t < ms do sleep(20); t = t + 20 end
end

local function chatHas(needle)
  for _, l in ipairs(harness.chatLog()) do
    if string.find(l, needle, 1, true) then return true end
  end
  return false
end

-- 1. a script command through /lua: eaten, runs once, with the rest of the line
harness.clearCalls()
check(harness.chatInput("/lua demo status"), "/lua demo status is eaten")
settle(300)
eq(#calls, 1, "/lua demo status ran the /demo handler exactly once")
eq(calls[1], "status", "the handler got args 'status'")
check(not chatHas("/lua list | start"), "no usage text when a script owns the command")

-- no args and mixed case (owners are case-insensitive, args keep their case)
harness.chatInput("/lua DEMO")
settle(300)
eq(#calls, 2, "/lua DEMO ran the handler")
eq(calls[2], "", "no args gives an empty string")
harness.chatInput("/lua demo Two  Words")
settle(300)
eq(calls[3], "Two  Words", "args pass through unchanged")

-- 2. an unknown name prints usage and runs nothing
harness.clearCalls()
local before = #calls
harness.chatInput("/lua nosuchcmd x")
settle(300)
eq(#calls, before, "an unknown command ran no handler")
check(chatHas("/lua list | start"), "an unknown command prints usage")
check(chatHas("registered: demo."), "usage lists the registered script commands")
harness.clearCalls()
harness.chatInput("/lua")
settle(300)
check(chatHas("/lua list | start"), "bare /lua prints usage")

-- 3. a built-in verb is not shadowed by a script command of the same name
harness.clearCalls()
before = #calls
harness.chatInput("/lua list")
settle(300)
eq(#calls, before, "/lua list did not run the script's /list handler")
check(chatHas("Scripts ("), "/lua list ran the built-in list")

-- typed natively, the script's own /list is still its own
check(harness.chatInput("/list direct"), "a typed /list still reaches the script")
settle(300)
eq(calls[#calls], "SHADOW:direct", "typed /list ran the script handler")

-- 4. the slash form the usage text shows is accepted too
harness.clearCalls()
before = #calls
harness.chatInput("/lua /demo hi")
settle(300)
eq(#calls, before + 1, "/lua /demo hi ran the handler once")
eq(calls[#calls], "hi", "/lua /demo hi: args 'hi'")

-- 5. odd spacing and quoted args pass through; the line is trimmed
before = #calls
harness.chatInput("   /lua   demo   hi  there   ")
settle(300)
eq(#calls, before + 1, "extra spaces: ran once")
eq(calls[#calls], "hi  there", "extra spaces: args trimmed at the ends only")
harness.chatInput("/lua demo say \"hello world\" 'x'")
settle(300)
eq(calls[#calls], "say \"hello world\" 'x'", "quotes are not interpreted")

-- 6. the usage lists names in the form the fallback accepts, minus the unreachable ones
harness.clearCalls()
harness.chatInput("/lua nosuchcmd")
settle(300)
local summary
for _, l in ipairs(harness.chatLog()) do
  summary = summary or string.match(l, "registered: ([^%.]*)%.")
end
check(summary ~= nil, "usage has a registered: list")
check(string.find(summary or "", "demo", 1, true) ~= nil, "the list names demo")
check(string.find(summary or "", "/", 1, true) == nil, "the list has no slash: " .. tostring(summary))
check(string.find(summary or "", "list", 1, true) == nil, "a script /list is left out (it is a built-in verb)")
-- and every name it shows really works
harness.clearCalls()
before = #calls
harness.chatInput("/lua " .. summary)
settle(300)
eq(#calls, before + 1, "the listed form runs the handler")

-- 7. a script command named like a built-in verb is not reachable through /lua
game.RegisterCommand("/exec", function(args) calls[#calls + 1] = "SHADOW-EXEC:" .. args end)
harness.clearCalls()
before = #calls
harness.chatInput("/lua exec return 1")
harness.chatInput("/lua /exec return 1")
settle(300)
eq(#calls, before, "/lua exec did not run the script's /exec handler")
game.UnregisterCommand("/exec")

-- 8. a stopped owner no longer answers: usage instead
harness.chatInput("/lua exec game.RegisterCommand('/ghost', function(a) end)")
settle(300)
harness.clearCalls()
harness.chatInput("/lua ghost")
settle(300)
check(not chatHas("/lua list | start"), "a live owner's /ghost is found (no usage)")
harness.chatInput("/lua stop exec")
settle(300)
harness.clearCalls()
harness.chatInput("/lua ghost")
settle(300)
check(chatHas("/lua list | start"), "after its owner stopped, /lua ghost prints usage")
check(not chatHas("ghost."), "a stopped owner's command is not listed")

-- 9. nothing registered: the usage says so
game.UnregisterCommand("/demo")
game.UnregisterCommand("/list")
harness.clearCalls()
before = #calls
harness.chatInput("/lua demo status")
settle(300)
check(chatHas("none registered now."), "no commands: usage says none registered")
eq(#calls, before, "no handler ran")

done()

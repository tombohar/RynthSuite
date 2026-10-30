-- Every /lua command answers in chat (2026-10-28): each built-in verb typed right and typed wrong
-- gives a "[RynthLua]" line, a wrong one saying why (and the usage). The lines go through the shared
-- reply queue (Plugins/Shared/CommandReply.cs), written on the ticks after the command.

local function settle(ms)
  local t = 0
  while t < ms do sleep(20); t = t + 20 end
end

-- Types the command and returns the RynthLua lines it answered with.
local function answer(cmd)
  harness.clearCalls()
  check(harness.chatInput(cmd), cmd .. ": eaten")
  settle(200)
  local lines = {}
  for _, l in ipairs(harness.chatLog()) do
    if string.find(l, "[RynthLua]", 1, true) == 1 then lines[#lines + 1] = l end
  end
  return lines
end

local function says(cmd, needle)
  local lines = answer(cmd)
  check(#lines > 0, cmd .. ": answers")
  local found = false
  for _, l in ipairs(lines) do
    if string.find(l, needle, 1, true) then found = true end
  end
  check(found, cmd .. ": says '" .. needle .. "' (got: " .. table.concat(lines, " / ") .. ")")
end

harness.writeScript("helper", "while true do sleep(1000) end")

-- right forms
says("/lua list", "Scripts (")
says("/lua start helper", "Running helper.")
says("/lua restart helper", "Restarted helper.")
says("/lua execin helper x = 1", "execin helper: started")
says("/lua autostart helper none", "helper: autostart none.")
says("/lua stop helper", "Stopped helper.")
says("/lua exec y = 2", "exec: started")
says("/lua exec while true do sleep(1000) end", "exec: started")
says("/lua stop exec", "Stopped exec.")
says("/lua hud list", "No huds.")
says("/lua", "/lua list | start")
says("/lua help", "/lua list | start")

-- wrong forms: why, and how it's typed
says("/lua start nosuch", "No script named 'nosuch'")
says("/lua restart", "No script named")
says("/lua stop nosuch", "nosuch isn't running.")
says("/lua exec", "Usage: /lua exec <code>")
says("/lua exec this is not lua", "exec: the code didn't run")
says("/lua execin", "Usage: /lua execin <script> <code>")
says("/lua execin nosuch x = 1", "nosuch isn't running.")
says("/lua autostart", "Usage: /lua autostart")
says("/lua autostart helper sometimes", "Usage: /lua autostart")
says("/lua hud show nosuch/hud", "No hud named")
says("/lua hud bench lots", "Usage: /lua hud bench")
says("/lua hud bench off", "The bench isn't running.")
says("/lua hud wobble", "Unknown '/lua hud wobble'.")
says("/lua wobble", "Unknown '/lua wobble'.")

done()

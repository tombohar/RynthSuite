-- library-demo.lua: the RynthLua library modules in one script.
-- Copy to C:\Games\RynthSuite\RynthLua\Scripts\library-demo.lua (or a folder
-- library-demo\index.lua) and start it with /lua start library-demo.

local json    = require("json")
local storage = require("storage")
local ai      = require("rynth.ai")
local fs      = require("filesystem").GetData()

-- ── enums: globals, both ways ───────────────────────────────────────────────
print("SkillId.MeleeDefense = " .. SkillId.MeleeDefense)          -- 6
print("ObjectClass[5] = " .. ObjectClass[5])                        -- "Monster"
print("ChatType.Tell = " .. ChatType.Tell .. ", IntId.Value = " .. IntId.Value)

-- ── json ───────────────────────────────────────────────────────────────────
local snapshot = { script = game.ScriptName, when = os.time(), list = { 1, 2, 3 }, nested = { ok = true } }
local text = json.encode(snapshot)
print("json: " .. text)
local back = json.decode(text)
print("decoded list[3] = " .. back.list[3] .. ", nested.ok = " .. tostring(back.nested.ok))
local ok, err = pcall(json.encode, { fn = print })
print("encoding a function fails: " .. tostring(err))

-- ── storage: survives restarts, per script and per character ────────────────
local runs = storage.Get("runs", 0) + 1
storage.Set("runs", runs)
storage.Set("lastRun", snapshot)
print(("storage: this script has run %d time(s); keys: %s"):format(runs, table.concat(storage.Keys(), ", ")))
if game.State == "InGame" then
  local mine = storage.Character.Get("runs", 0) + 1
  storage.Character.Set("runs", mine)
  print(("storage.Character: %d run(s) on this character"):format(mine))
end

-- ── filesystem: your own files in the data folder ───────────────────────────
fs.AppendText("log.txt", os.date("%Y-%m-%d %H:%M:%S") .. " run " .. runs .. "\n")
local lines = fs.ReadLines("log.txt")
print("filesystem: log.txt has " .. #lines .. " line(s), last: " .. lines[#lines])
local refused, why = fs.WriteText("../outside.txt", "nope")
print("writing outside the folder is refused: " .. tostring(why))

-- ── rynth.ai: only when RynthAi is loaded ───────────────────────────────────
if ai.Available then
  print("RynthAi: macro " .. (ai.MacroRunning and "running" or "stopped"))
  print("expression getcharvital_current[1] = " .. tostring(ai.Evaluate("getcharvital_current[1]")))
  local portal, distance = ai.FindNearest("Portal", true)
  if portal then print(("nearest portal: %s (%d) at %.1f m"):format(portal.Name or "?", portal.Id, distance)) end
else
  print("RynthAi isn't loaded; rynth.ai calls would raise an error")
end

-- ── rynthlua: the plugin's own info ─────────────────────────────────────────
local info = require("rynthlua")
print(("RynthLua %s, data folder %s"):format(info.Version, info.DataFolder))

-- storage: RynthLua's saved values (UB has no storage module; UB scripts write their own
-- JSON through filesystem, which this also checks against).
local storage = require("storage")
local json = require("json")
local fs = require("filesystem").GetData()

storage.Clear()
eq(#storage.Keys(), 0, "starts empty")
eq(storage.Get("missing"), nil, "missing is nil")
eq(storage.Get("missing", 42), 42, "missing with a default")

storage.Set("count", 3)
storage.Set("name", "Tester")
storage.Set("flags", { a = true, list = { 1, 2, 3 } })
storage.Set("skill", SkillId.WarMagic)
storage.Set("typed", StringId.Name)
eq(storage.Get("count"), 3, "number")
eq(storage.Get("name"), "Tester", "string")
eq(storage.Get("flags").list[3], 3, "table")
eq(storage.Get("skill"), 34, "numeric enum stored as its number")
eq(storage.Get("typed"), 1, "typed enum stored as its number")
check(storage.Has("name"), "Has")
local keys = storage.Keys()
eq(#keys, 5, "Keys")
eq(keys[1], "count", "Keys are sorted")

-- values are copies
local t = storage.Get("flags")
t.a = false
eq(storage.Get("flags").a, true, "Get returns a copy")

check(storage.Remove("count"), "Remove says it was there")
check(not storage.Has("count"), "removed")
storage.Set("name", nil)
check(not storage.Has("name"), "Set(nil) removes")

-- written to the script's data folder as JSON
storage.Save()
local text, err = fs.ReadText("storage.json")
check(text ~= nil, "storage.json written: " .. tostring(err))
if text then
  local onDisk = json.decode(text)
  eq(onDisk.skill, 34, "on disk")
end

-- per character
storage.Character.Set("seen", true)
eq(storage.Character.Get("seen"), true, "storage.Character")
check(not storage.Has("seen"), "character values are separate")

-- the UB way: a script's own JSON file through filesystem
local ok, werr = fs.WriteText("settings.json", json.encode({ enabled = true, range = 30 }))
check(ok, "WriteText: " .. tostring(werr))
local saved = json.decode((fs.ReadText("settings.json")))
eq(saved.range, 30, "read back")
check(fs.FileExists("settings.json"), "FileExists")

storage.Clear()
eq(#storage.Keys(), 0, "Clear")
done()

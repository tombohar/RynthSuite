-- UB-style JSON: the require'd module and the global, the names UB's interpreter ships
-- (parse / serialize / null / isnull) and the rxi-style encode / decode many UB scripts
-- bundle.
local json = require("json")
check(json ~= nil, "require('json')")
check(_G.json ~= nil, "global json")

-- rxi-style round trip
local data = {
  name = "Tester",
  level = 100,
  ratio = 0.25,
  alive = true,
  tags = { "one", "two", "three" },
  nested = { deep = { deeper = { 1, 2, 3 } } },
  unicode = "Élan – “quotes” ✓",
}
local text = json.encode(data)
check(type(text) == "string", "encode gives text")
local back = json.decode(text)
eq(back.name, "Tester", "string")
eq(back.level, 100, "integer")
eq(back.ratio, 0.25, "fraction")
eq(back.alive, true, "boolean")
eq(#back.tags, 3, "array length")
eq(back.tags[2], "two", "array order")
eq(back.nested.deep.deeper[3], 3, "nested")
eq(back.unicode, data.unicode, "unicode text survives")
eq(json.encode({}), "[]", "empty table is an array")
eq(json.encode({ 1, 2, 3 }), "[1,2,3]", "array text")
eq(json.encode(12), "12", "whole numbers without .0")

-- UB interpreter's names
local parsed = json.parse('{"a": 1, "b": null, "list": [1, null, 3]}')
eq(parsed.a, 1, "parse")
check(json.isnull(parsed.b), "parse keeps null as json.null()")
check(parsed.b == json.null(), "json.null() is one value")
eq(#parsed.list, 3, "arrays keep their null holes")
check(json.isnull(parsed.list[2]), "null in an array")
check(json.isnull(nil), "isnull(nil)")
check(not json.isnull(0), "isnull(0) is false")
eq(json.serialize({ x = json.null() }), '{"x":null}', "json.null() serializes as null")
eq(tostring(json.null()), "null", "tostring(json.null())")
local dec = json.decode('{"a": null, "b": 2}')
eq(dec.a, nil, "decode turns null into nil")
eq(dec.b, 2, "decode keeps the rest")

-- API values encode sensibly
local sword = game.World.Get(harness.ids.Sword)
eq(json.encode({ item = sword }), '{"item":' .. harness.ids.Sword .. '}', "a WorldObject encodes as its id")
eq(json.encode({ key = StringId.Name }), '{"key":1}', "a typed enum value encodes as its number")
eq(json.encode({ skill = SkillId.WarMagic }), '{"skill":34}', "a numeric enum value is a number")

-- errors say what's wrong
local ok, err = pcall(json.decode, "{not json")
check(not ok and tostring(err):find("not valid JSON") ~= nil, "bad JSON is an error")
local cyc = {}
cyc.self = cyc
local ok2, err2 = pcall(json.encode, cyc)
check(not ok2 and tostring(err2):find("contains itself") ~= nil, "a cycle is an error")
local ok3 = pcall(json.encode, { f = function() end })
check(not ok3, "a function can't be encoded")

done()

-- string.format as standard Lua 5.2+ has it: %s takes any value through tostring, so a
-- script that formats a missing field (nil) keeps running (MoonSharp alone throws
-- "bad argument #3 to 'format' (string expected, got nil)").
local e = { SenderName = "Cap" }   -- no Message field, as in the DiscordWebhook report
eq(string.format("**%s**: %s", e.SenderName, e.Message), "**Cap**: nil", "nil through %s")
eq(string.format("%s %s %s", true, false, nil), "true false nil", "booleans and nil through %s")
check(string.format("%s", {}):find("^table") ~= nil, "a table through %s")
local obj = setmetatable({}, { __tostring = function() return "custom" end })
eq(string.format("[%s]", obj), "[custom]", "__tostring is honoured")
eq(("%s=%s"):format("k", nil), "k=nil", "the method form too")

-- everything else is unchanged
eq(string.format("%d%% of %5.1f", 50, 3.14159), "50% of   3.1", "%d, %%, widths")
eq(string.format("0x%08X %-4s|", 255, "ab"), "0x000000FF ab  |", "hex and left-aligned %s")
eq(string.format("%q", "a\"b"), "\"a\\\"b\"", "%q")
eq(string.format("no args"), "no args", "no arguments")
eq(string.format("%s", 12), "12", "numbers through %s")
done()

-- UB-style use of the game root: state, identity, capabilities, and a script command
-- (RynthLua's RegisterCommand, next to UB's OnChatInput way in world.lua).
check(game ~= nil, "game global")
check(game.State == ClientState.In_Game, "game.State == ClientState.In_Game")
eq(game.ServerName, "TestServer", "game.ServerName")
eq(game.AccountName, "testaccount", "game.AccountName")
eq(game.CharacterId, harness.player, "game.CharacterId")
check(type(game.LastTick) == "number", "game.LastTick")
check(game.World ~= nil and game.Character ~= nil and game.Actions ~= nil and game.ActionQueue ~= nil, "game's parts")
check(game.OnTick ~= nil and game.OnStateChanged ~= nil and game.OnScriptEnd ~= nil, "game events")

-- capabilities (UB: game.Capabilities.Has(ClientCapability.ImGui))
check(game.Capabilities ~= nil, "game.Capabilities")
check(type(game.Capabilities.Has(ClientCapability.ImGui)) == "boolean", "Capabilities.Has")
check(type(game.Capabilities.Get()) == "number", "Capabilities.Get")

-- a quick look at the world through game
eq(game.Character.Weenie.Name, "Tester", "character name")
eq(game.World.Get(harness.ids.Drudge).Name, "Drudge Skulker", "drudge")
local a = await(game.Actions.ObjectUse(harness.ids.Potion))
check(a.Success, "use a potion: " .. tostring(a.ErrorDetails))

-- script commands
local got
game.RegisterCommand("/testcmd", function(args) got = args end)
check(harness.chatInput("/testcmd hello world"), "a registered command is eaten")
local t = 0
while got == nil and t < 2000 do sleep(20); t = t + 20 end
eq(got, "hello world", "the command got its arguments")
game.UnregisterCommand("/testcmd")
check(not harness.chatInput("/testcmd again"), "unregistered")

-- the script's own name
eq(game.ScriptName, "game", "game.ScriptName")
done()

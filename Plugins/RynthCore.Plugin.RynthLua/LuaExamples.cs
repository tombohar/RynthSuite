using System;
using System.IO;

namespace RynthCore.Plugin.RynthLua;

/// <summary>Starter scripts (or the scripts from RynthAi's old folder), written only when the Scripts folder doesn't exist yet.</summary>
internal static class LuaExamples
{
    public static void EnsureWritten(Action<string> log)
    {
        try
        {
            if (Directory.Exists(LuaScriptHost.ScriptFolder)) return;
            Directory.CreateDirectory(LuaScriptHost.ScriptFolder);
            // Scripts written while Lua lived in RynthAi come along (the old folder is left as is).
            if (Directory.Exists(LuaScriptHost.LegacyScriptFolder))
            {
                int copied = 0;
                foreach (string f in Directory.GetFiles(LuaScriptHost.LegacyScriptFolder, "*.lua"))
                {
                    File.Copy(f, Path.Combine(LuaScriptHost.ScriptFolder, Path.GetFileName(f)), overwrite: false);
                    copied++;
                }
                log($"[Lua] Copied {copied} script(s) from {LuaScriptHost.LegacyScriptFolder}.");
                if (copied > 0) return;
            }
            Write("Example - Hello", Hello);
            Write("Example - Wisp Essence", Wisp);
            Write("Example - Tells", Tells);
        }
        catch (Exception ex) { log($"[Lua] Couldn't write example scripts: {ex.Message}"); }
    }

    private static void Write(string name, string text)
        => File.WriteAllText(Path.Combine(LuaScriptHost.ScriptFolder, name + ".lua"), text.Replace("\r\n", "\n"));

    private const string Hello = """
-- Prints who and where you are, then counts down.
print("Hello, " .. me.name)
print(("Health %d / %d   Mana %d / %d"):format(me.health, me.maxhealth, me.mana, me.maxmana))
print("You are at " .. me.coords)

for i = 3, 1, -1 do
  print(i .. "...")
  sleep(1000)          -- sleep never freezes the game
end
chat("Hello from Lua!")  -- shows in your chat window only
""";

    private const string Wisp = """
-- Keeps a pet essence charged: when it has no uses left, refills it
-- with an Encapsulated Spirit. Change the names to match your items.
local ESSENCE = "Imperial Wisp Essence"
local SPIRIT  = "Encapsulated Spirit"

while true do
  local essence = meta.wobjectfindininventorybyname(ESSENCE)
  if essence ~= 0 then
    local uses = meta.wobjectgetintprop(essence, 92)   -- 92 = uses left
    if uses < 1 then
      if meta.getitemcountininventorybyname(SPIRIT) > 0 then
        print("Refilling " .. ESSENCE)
        command("/ub usei " .. SPIRIT .. " on " .. ESSENCE)
        sleep(3000)
      else
        print("Out of " .. SPIRIT .. "; stopping.")
        stop()
      end
    end
  end
  sleep(5000)
end
""";

    private const string Tells = """
-- Runs in the background and logs every tell you get to the Lua console.
-- Scripts that only register events keep running until you press Stop.
on("chat", function(text, chatType)
  if text:find("tells you") then
    print(os.date("%H:%M") .. "  " .. text)
  end
end)
print("Listening for tells. Press Stop to end.")
""";
}

using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// The lists the API returns (World.GetAll, WorldObject.Items, Character.Inventory, ...):
/// plain 1-based Lua tables, so <c>#list</c>, <c>list[i]</c> and <c>ipairs(list)</c> work,
/// with a metatable that also lets a script
/// <list type="bullet">
/// <item>iterate one directly, <c>for wo in list do ... end</c> (the way a CLR list is
/// iterated in UtilityBelt's Lua), and</item>
/// <item>call one: a plain list returns itself (so the older call forms such as
/// <c>wo:Items()</c> keep working), a list made with an onCall function passes the call on
/// (<c>game.Character.Inventory(filter)</c>).</item>
/// </list>
/// The metatable is made once per script, in Lua, so iteration and filter functions stay
/// ordinary Lua code (they can sleep).
/// </summary>
internal static class LuaLists
{
    private const string RegistryKey = "rynth.lists";

    private const string Factory = """
local cursor = setmetatable({}, { __mode = 'k' })
local function iterate(self, control)
  local i
  if control == nil then
    i = 1
  else
    local c = cursor[self]
    if c ~= nil and rawequal(self[c], control) then
      i = c + 1
    else
      for k = 1, #self do
        if rawequal(self[k], control) then i = k + 1; break end
      end
      if i == nil then return nil end
    end
  end
  cursor[self] = i
  return self[i]
end
local plain = { __call = function(self, ...)
  local a, b = ...
  if select('#', ...) == 2 and a == nil then return iterate(self, b) end
  return self
end }
local function make(items, onCall)
  if onCall == nil then return setmetatable(items, plain) end
  return setmetatable(items, { __call = function(self, ...)
    local a, b = ...
    if select('#', ...) == 2 and a == nil then return iterate(self, b) end
    return onCall(...)
  end })
end
return { plain = plain, make = make }
""";

    private static Table Lib(Script s)
    {
        DynValue lib = s.Registry.Get(RegistryKey);
        if (lib.Type == DataType.Table) return lib.Table;
        lib = s.DoString(Factory, null, "rynth-lists");
        s.Registry.Set(RegistryKey, lib);
        return lib.Table;
    }

    /// <summary>Gives <paramref name="list"/> the list metatable and returns it.</summary>
    public static DynValue Wrap(Script s, Table list)
    {
        list.MetaTable = Lib(s).Get("plain").Table;
        return DynValue.NewTable(list);
    }

    /// <summary><c>make(items [, onCall])</c> for Lua glue code.</summary>
    public static DynValue MakeFunction(Script s) => Lib(s).Get("make");
}

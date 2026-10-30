using System;
using System.Globalization;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// AC icons in script windows (op level 2). UtilityBelt's shape:
///
///   local tex = views.Huds.GetIconTexture(iconId)      -- UB: a ManagedTexture
///   ImGui.Image(tex.TexturePtr, Vector2.new(24, 24))    -- UB passes the texture pointer
///
/// Here a "texture" is a small read-only table naming an icon; TexturePtr is the table itself,
/// so both ImGui.Image(tex, size) and ImGui.Image(tex.TexturePtr, size) work, and a plain
/// number is taken as an icon id. The engine turns it into a real texture (decoded from
/// client_portal.dat, cached) and draws a placeholder until then, or when the icon is missing:
/// no pointer ever comes from a script (design §6.3). RynthLua extras:
/// views.Huds.GetObjectIconTexture(objectOrId) (an item's icon with its underlay and overlay,
/// as AC shows it) and views.Huds.GetSpellIconTexture(spellOrId).
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private const string IconTexMetaKey = "rynth.icontex.mt";
    private const string IconTexCacheKey = "rynth.icontex.cache";
    private const string IconTexCountKey = "rynth.icontex.count";
    private const int IconTexCacheMax = 4096;

    /// <summary>The engine's op level (UiGetInfo); offline (no engine) it is this recorder's own.</summary>
    private int _uiOpLevel = HudFormat.OpLevel;

    private static readonly string[] IconKindNames = { "icon", "object", "spell" };

    /// <summary>A function that records an op-level-2 op refuses on an engine that would skip it.</summary>
    private void RequireOpLevel(string fn)
    {
        if (_uiOpLevel >= HudFormat.OpLevel) return;
        throw new ScriptRuntimeException(
            $"ImGui.{fn} needs a newer RynthCore engine (script windows op level {HudFormat.OpLevel}; this engine has {_uiOpLevel})");
    }

    /// <summary>views.Huds.GetIconTexture / GetObjectIconTexture / GetSpellIconTexture and the UB-only CreateTexture.</summary>
    private void AddIconFunctions(Script s, Table huds)
    {
        huds["GetIconTexture"] = DynValue.NewCallback((c, a) =>
        {
            DynValue v = LuaArgs.At(LuaArgs.Of(a, huds), 0);
            return IconTexture(s, HudFormat.IconKindIcon, IdArg(v, "GetIconTexture", "iconId", allowObject: false));
        });
        huds["GetObjectIconTexture"] = DynValue.NewCallback((c, a) =>
        {
            DynValue v = LuaArgs.At(LuaArgs.Of(a, huds), 0);
            return IconTexture(s, HudFormat.IconKindObject, IdArg(v, "GetObjectIconTexture", "an object or object id", allowObject: true));
        });
        huds["GetSpellIconTexture"] = DynValue.NewCallback((c, a) =>
        {
            DynValue v = LuaArgs.At(LuaArgs.Of(a, huds), 0);
            return IconTexture(s, HudFormat.IconKindSpell, IdArg(v, "GetSpellIconTexture", "a spell or spell id", allowObject: true));
        });
        huds["CreateTexture"] = DynValue.NewCallback((c, a) =>
            throw new ScriptRuntimeException("views.Huds.CreateTexture(file) isn't supported: RynthLua windows draw AC icons only (GetIconTexture, GetObjectIconTexture, GetSpellIconTexture)"));
    }

    /// <summary>
    /// An id argument: a whole number 0..0xFFFFFFFF, or (<paramref name="allowObject"/>) a table
    /// with a numeric Id (a world object, a spell). Anything else is an error.
    /// </summary>
    private static uint IdArg(DynValue v, string fn, string what, bool allowObject)
    {
        double d = double.NaN;
        if (v.Type == DataType.Number) d = v.Number;
        else if (allowObject && v.Type == DataType.Table)
        {
            DynValue id = v.Table.Get("Id");
            if (id.Type == DataType.Number) d = id.Number;
        }
        if (double.IsFinite(d) && d >= 0 && d <= uint.MaxValue && Math.Floor(d) == d) return (uint)d;
        throw new ScriptRuntimeException($"views.Huds.{fn} needs {what} (a whole number 0 to 0xFFFFFFFF), not {DescribeValue(v)}");
    }

    /// <summary>The script's texture table for (kind, id), made once and cached per script.</summary>
    private static DynValue IconTexture(Script s, byte kind, uint id)
    {
        DynValue cacheV = s.Registry.Get(IconTexCacheKey);
        Table cache;
        if (cacheV.Type == DataType.Table) cache = cacheV.Table;
        else
        {
            cache = new Table(s);
            s.Registry.Set(IconTexCacheKey, DynValue.NewTable(cache));
        }
        string cacheKey = kind.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString(CultureInfo.InvariantCulture);
        DynValue hit = cache.RawGet(cacheKey) ?? DynValue.Nil;
        if (hit.Type == DataType.Table) return hit;
        // Bounded: a script asking for thousands of different icons starts a fresh cache.
        DynValue countV = s.Registry.Get(IconTexCountKey);
        double count = countV.Type == DataType.Number ? countV.Number : 0;
        if (count >= IconTexCacheMax)
        {
            cache = new Table(s);
            s.Registry.Set(IconTexCacheKey, DynValue.NewTable(cache));
            count = 0;
        }
        s.Registry.Set(IconTexCountKey, DynValue.NewNumber(count + 1));

        var t = new Table(s);
        t.Set("Kind", DynValue.NewString(IconKindNames[kind]));
        t.Set("Id", DynValue.NewNumber(id));
        DynValue self = DynValue.NewTable(t);
        t.Set("TexturePtr", self);   // UB: ImGui.Image(tex.TexturePtr, size)
        DynValue noop = DynValue.NewCallback((c, a) => DynValue.Nil);
        t.Set("Dispose", noop);     // UB: textures are released by the engine's cache here
        t.Set("Release", noop);
        t.MetaTable = IconTexMeta(s);
        cache.Set(cacheKey, self);
        return self;
    }

    private static Table IconTexMeta(Script s)
    {
        DynValue cached = s.Registry.Get(IconTexMetaKey);
        if (cached.Type == DataType.Table) return cached.Table;
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            if (key is "Bitmap" or "Texture")
                throw new ScriptRuntimeException($"texture.{key} isn't available in RynthLua (the engine owns the texture); pass the texture to ImGui.Image");
            return DynValue.Nil;
        });
        mt["__newindex"] = DynValue.NewCallback((c, a) =>
            throw new ScriptRuntimeException("an icon texture is read-only"));
        mt["__tostring"] = DynValue.NewCallback((c, a) =>
        {
            Table? t = a.Count > 0 && a[0].Type == DataType.Table ? a[0].Table : null;
            string kind = t?.RawGet("Kind")?.CastToString() ?? "icon";
            double id = t?.RawGet("Id")?.Number ?? 0;
            return DynValue.NewString($"IconTexture({kind} 0x{(uint)id:X8})");
        });
        s.Registry.Set(IconTexMetaKey, DynValue.NewTable(mt));
        return mt;
    }

    /// <summary>
    /// ImGui.Image's / ImageButton's texture argument: a texture from views.Huds.Get*IconTexture
    /// (or its TexturePtr), or a number = an icon id (UB: below 0x06000000, 0x06000000 is added;
    /// the engine does that). 0 draws the placeholder. Anything else is an error.
    /// </summary>
    private static void IconArg(Script s, DynValue v, string fn, out byte kind, out uint id)
    {
        if (v.Type == DataType.Number)
        {
            double d = v.Number;
            if (double.IsFinite(d) && d >= 0 && d <= uint.MaxValue && Math.Floor(d) == d)
            {
                kind = HudFormat.IconKindIcon;
                id = (uint)d;
                return;
            }
        }
        else if (v.Type == DataType.Table && ReferenceEquals(v.Table.MetaTable, IconTexMeta(s)))
        {
            string k = v.Table.RawGet("Kind")?.CastToString() ?? "";
            int i = Array.IndexOf(IconKindNames, k);
            DynValue idV = v.Table.RawGet("Id") ?? DynValue.Nil;
            if (i >= 0 && idV.Type == DataType.Number)
            {
                kind = (byte)i;
                id = (uint)idV.Number;
                return;
            }
        }
        throw new ScriptRuntimeException(
            $"ImGui.{fn}: the texture must come from views.Huds.GetIconTexture / GetObjectIconTexture / GetSpellIconTexture, or be an icon id number, not {DescribeValue(v)}");
    }
}

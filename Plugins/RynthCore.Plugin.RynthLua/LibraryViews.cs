using System;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// The views module (require("views")): views.Huds.CreateHud(name) -> hud; views.Available;
/// views.Huds.GetIconTexture / GetObjectIconTexture / GetSpellIconTexture (LibraryIcons.cs).
/// The imgui module is in LibraryImGui.cs.
///
/// hud (§1.2): Name, LastError, IsDisposed (read-only); Title, Visible, ShowInBar,
/// WindowSettings, Chrome, DefaultSize, RefreshRate (read/write); OnPreRender / OnRender /
/// OnShow / OnHide (events with Add / Once / Until / Remove); Dispose(). ImGui calls only
/// record into the hud's display list during its render pass (ScriptWindows.cs); results
/// come one pass late.
/// Design: Docs/RYNTHLUA_WINDOWS_DESIGN.md §1.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private const string ViewsRegistryKey = "rynth.views";

    // ── views ───────────────────────────────────────────────────────────────

    private DynValue CreateViewsModule(Script s, ScriptContext ctx)
    {
        DynValue cached = s.Registry.Get(ViewsRegistryKey);
        if (cached.Type == DataType.Table) return cached;

        var views = new Table(s);
        var huds = new Table(s);
        huds["CreateHud"] = DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, huds);
            string name = LuaArgs.Str(LuaArgs.At(args, 0)).Trim();
            if (name.Length == 0) throw new ScriptRuntimeException("CreateHud(name) needs a name");
            // args[1] (icon) is accepted and ignored until icons exist.
            ScriptHud hud = CreateHud(ctx, name);
            return HudTable(s, hud);
        });
        AddIconFunctions(s, huds);   // GetIconTexture & co. (LibraryIcons.cs)
        views["Huds"] = DynValue.NewTable(huds);

        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            return key == "Available" ? DynValue.NewBoolean(UiAvailable) : DynValue.Nil;
        });
        views.MetaTable = mt;

        DynValue module = DynValue.NewTable(views);
        s.Registry.Set(ViewsRegistryKey, module);
        return module;
    }

    /// <summary>The Lua object for a hud (made once per hud; a hud lives in one script state).</summary>
    private DynValue HudTable(Script s, ScriptHud hud)
    {
        if (hud.LuaObject != null) return hud.LuaObject;

        var t = new Table(s);
        t["OnRender"] = hud.OnRender.CreateTable(s);
        t["OnPreRender"] = hud.OnPreRender.CreateTable(s);
        t["OnShow"] = hud.OnShow.CreateTable(s);
        t["OnHide"] = hud.OnHide.CreateTable(s);
        t["Dispose"] = DynValue.NewCallback((c, a) => { DisposeHud(hud); return DynValue.Nil; });

        // The events and Dispose live in the table itself; the properties go through the
        // metatable; any other key is the script's own field.
        var mt = new Table(s);
        mt["__index"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            return key switch
            {
                "Name" => DynValue.NewString(hud.Name),
                "Title" => DynValue.NewString(hud.Title),
                "Visible" => DynValue.NewBoolean(hud.Visible),
                "ShowInBar" => DynValue.NewBoolean(hud.ShowInBar),
                "WindowSettings" => DynValue.NewNumber(hud.WindowSettings),
                "Chrome" => DynValue.NewString(hud.ChromeNone ? "none" : "standard"),
                "DefaultSize" => LibraryImGuiValues.NewVec2(s, hud.DefaultSize.X, hud.DefaultSize.Y),
                "RefreshRate" => DynValue.NewNumber(hud.RefreshRate),
                "LastError" => hud.LastError == null ? DynValue.Nil : DynValue.NewString(hud.LastError),
                "IsDisposed" => DynValue.NewBoolean(hud.Disposed),
                _ => DynValue.Nil,
            };
        });
        mt["__newindex"] = DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].CastToString() ?? "" : "";
            DynValue v = a.Count > 2 ? a[2] : DynValue.Nil;
            switch (key)
            {
                case "Title":
                    SetHudTitle(hud, LuaArgs.Str(v, hud.Name));
                    break;
                case "Visible":
                    // The script's own choice: it applies (OnShow/OnHide fire) and goes to the
                    // engine with the next submit, but it is NOT remembered as the player's.
                    SetHudVisible(hud, v.CastToBool());
                    break;
                case "ShowInBar":
                {
                    bool show = v.CastToBool();
                    if (hud.ShowInBar != show) { hud.ShowInBar = show; hud.PropsDirty = true; }
                    break;
                }
                case "WindowSettings":
                    SetHudWindowSettings(hud, WindowSettingsArg(v));
                    break;
                case "Chrome":
                {
                    string chrome = v.Type == DataType.String ? v.String.Trim().ToLowerInvariant() : "";
                    if (chrome is not ("standard" or "none"))
                        throw new ScriptRuntimeException($"hud.Chrome must be \"standard\" or \"none\", not {DescribeValue(v)}");
                    bool none = chrome == "none";
                    if (hud.ChromeNone != none) { hud.ChromeNone = none; hud.PropsDirty = true; hud.PassNow = true; }
                    break;
                }
                case "DefaultSize":
                {
                    // The same as SetNextWindowSize(size, ImGuiCond.FirstUseEver): used by the engine
                    // only while nothing is saved for the window. nil resets it to the engine default.
                    System.Numerics.Vector2 size = System.Numerics.Vector2.Zero;
                    if (!v.IsNil())
                    {
                        if (!LibraryImGuiValues.TryVec2(v, out float x, out float y))
                            throw new ScriptRuntimeException($"hud.DefaultSize must be a Vector2 or {{x, y}}, not {DescribeValue(v)}");
                        size = Finite(new System.Numerics.Vector2(x, y));   // the header sends non-finite as 0 anyway
                    }
                    if (hud.DefaultSize != size) { hud.DefaultSize = size; hud.PropsDirty = true; }
                    break;
                }
                case "RefreshRate":
                {
                    // Passes per second while visible, clamped to 1..60 (a pass after input
                    // comes on the next tick whatever the rate).
                    double d = v.IsNil() ? DefaultRefreshRate : LuaArgs.Num(v, double.NaN);
                    if (double.IsNaN(d))
                        throw new ScriptRuntimeException($"hud.RefreshRate must be a number (1 to {MaxRefreshRate}), not {DescribeValue(v)}");
                    int rate = (int)Math.Clamp(Math.Round(d), MinRefreshRate, MaxRefreshRate);
                    if (hud.RefreshRate != rate)
                    {
                        hud.RefreshRate = rate;
                        hud.NextPassAt = 0;   // the new rate starts now
                    }
                    break;
                }
                case "Name":
                case "LastError":
                case "IsDisposed":
                    throw new ScriptRuntimeException($"hud.{key} is read-only");
                default:
                    t.Set(a[1], v);   // the script's own field
                    break;
            }
            return DynValue.Nil;
        });
        mt["__tostring"] = DynValue.NewCallback((c, a) => DynValue.NewString($"Hud {hud.Name}"));
        t.MetaTable = mt;

        hud.LuaObject = DynValue.NewTable(t);
        return hud.LuaObject;
    }

    /// <summary>hud.WindowSettings: an ImGuiWindowFlags number (0 or nil = none).</summary>
    private static long WindowSettingsArg(DynValue v)
    {
        if (v.IsNil()) return 0;
        double d = v.Type == DataType.Number ? v.Number : double.NaN;
        if (!double.IsFinite(d) || d < 0 || d > uint.MaxValue || Math.Floor(d) != d)
            throw new ScriptRuntimeException($"hud.WindowSettings must be ImGuiWindowFlags (a whole number), not {DescribeValue(v)}");
        return (long)d;
    }

    private static string DescribeValue(DynValue v) =>
        v.Type == DataType.String ? $"\"{(v.String.Length > 40 ? v.String[..40] + "..." : v.String)}\""
        : v.Type == DataType.Number ? v.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : v.Type.ToLuaTypeString();
}

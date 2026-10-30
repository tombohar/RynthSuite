using System;
using System.Globalization;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// The value globals the imgui module works with (Docs/RYNTHLUA_WINDOWS_DESIGN.md §1.3):
///
///   Vector2.new(x, y), Vector2(x, y)          a table { X, Y } (x / y also read and write)
///   Vector4.new(x, y, z, w), Vector4(...)     a table { X, Y, Z, W }
///   Vector2.Zero / One, Vector4.Zero / One    fresh values each time
///   + - * / and unary -, == and tostring       (* and / take a number or a vector)
///
/// and the ImGui enums with ImGui.NET 1.91.6.1's names and values: ImGuiWindowFlags, ImGuiCond,
/// ImGuiCol, ImGuiTreeNodeFlags, ImGuiInputTextFlags, ImGuiSelectableFlags, ImGuiChildFlags,
/// ImGuiSliderFlags (DragInt / DragFloat)
/// (the same two-way, case-insensitive tables as the other enum globals).
///
/// Every script gets its own tables; the vector metatables live in its Script.Registry.
/// The helpers (TryVec2, TryVec4, ColorU32, NewVec2) are for the imgui functions.
/// </summary>
internal static class LibraryImGuiValues
{
    private const string Vec2MetaKey = "rynth.vec2mt";
    private const string Vec4MetaKey = "rynth.vec4mt";
    private static readonly string[] Upper = { "X", "Y", "Z", "W" };

    /// <summary>Sets the Vector2 / Vector4 constructors and the ImGui enums as globals of <paramref name="s"/>.</summary>
    public static void Register(Script s)
    {
        s.Globals["Vector2"] = Constructor(s, 2);
        s.Globals["Vector4"] = Constructor(s, 4);
        s.Globals["ImGuiWindowFlags"] = LibraryEnums.Build(s, "ImGuiWindowFlags", WindowFlags);
        s.Globals["ImGuiCond"] = LibraryEnums.Build(s, "ImGuiCond", Cond);
        s.Globals["ImGuiCol"] = LibraryEnums.Build(s, "ImGuiCol", Col);
        s.Globals["ImGuiTreeNodeFlags"] = LibraryEnums.Build(s, "ImGuiTreeNodeFlags", TreeNodeFlags);
        s.Globals["ImGuiInputTextFlags"] = LibraryEnums.Build(s, "ImGuiInputTextFlags", InputTextFlags);
        s.Globals["ImGuiSelectableFlags"] = LibraryEnums.Build(s, "ImGuiSelectableFlags", SelectableFlags);
        s.Globals["ImGuiChildFlags"] = LibraryEnums.Build(s, "ImGuiChildFlags", ChildFlags);
        s.Globals["ImGuiSliderFlags"] = LibraryEnums.Build(s, "ImGuiSliderFlags", SliderFlags);
    }

    // ── Vectors ─────────────────────────────────────────────────────────

    public static DynValue NewVec2(Script s, float x, float y) => NewVec(s, 2, x, y, 0, 0);

    public static DynValue NewVec4(Script s, float x, float y, float z, float w) => NewVec(s, 4, x, y, z, w);

    private static DynValue NewVec(Script s, int dim, double x, double y, double z, double w)
    {
        var t = new Table(s);
        t.Set("X", DynValue.NewNumber(x));
        t.Set("Y", DynValue.NewNumber(y));
        if (dim == 4)
        {
            t.Set("Z", DynValue.NewNumber(z));
            t.Set("W", DynValue.NewNumber(w));
        }
        t.MetaTable = Meta(s, dim);
        return DynValue.NewTable(t);
    }

    /// <summary>The global table: new(...), __call(...), Zero, One.</summary>
    private static DynValue Constructor(Script s, int dim)
    {
        string name = dim == 2 ? "Vector2" : "Vector4";
        var g = new Table(s);
        DynValue make(DynValue[] args)
        {
            double c(int i) => i < args.Length ? Component(args[i], name, i) : 0;
            // Vector4.new(v) with one number fills every component, as System.Numerics does.
            if (args.Length == 1 && args[0].Type == DataType.Number)
            {
                double v = args[0].Number;
                return NewVec(s, dim, v, v, v, v);
            }
            return NewVec(s, dim, c(0), c(1), c(2), c(3));
        }
        g["new"] = DynValue.NewCallback((ctx, a) => make(LuaArgs.Of(a, g)));

        var mt = new Table(s);
        mt["__call"] = DynValue.NewCallback((ctx, a) => make(LuaArgs.Of(a, g)));   // Vector2(x, y): a[0] is the table
        mt["__index"] = DynValue.NewCallback((ctx, a) =>
        {
            string key = a.Count > 1 && a[1].Type == DataType.String ? a[1].String : "";
            return key switch
            {
                "Zero" => NewVec(s, dim, 0, 0, 0, 0),
                "One" => NewVec(s, dim, 1, 1, 1, 1),
                _ => DynValue.Nil,
            };
        });
        mt["__tostring"] = DynValue.NewCallback((ctx, a) => DynValue.NewString(name));
        g.MetaTable = mt;
        return DynValue.NewTable(g);
    }

    private static double Component(DynValue v, string type, int i)
    {
        if (v.IsNil()) return 0;
        double? d = v.CastToNumber();
        if (d == null) throw new ScriptRuntimeException($"{type}.new: component {i + 1} must be a number, got {v.Type.ToLuaTypeString()}");
        return d.Value;
    }

    /// <summary>The script's shared metatable for vectors of <paramref name="dim"/> components (made once).</summary>
    private static Table Meta(Script s, int dim)
    {
        string key = dim == 2 ? Vec2MetaKey : Vec4MetaKey;
        DynValue cached = s.Registry.Get(key);
        if (cached.Type == DataType.Table) return cached.Table;

        string name = dim == 2 ? "Vector2" : "Vector4";
        var mt = new Table(s);

        // Lowercase aliases: v.x reads X; v.x = 1 writes X. Other keys behave as in a plain table.
        mt["__index"] = DynValue.NewCallback((ctx, a) =>
        {
            string? up = Alias(a.Count > 1 ? a[1] : DynValue.Nil, dim);
            return up != null && a[0].Type == DataType.Table ? a[0].Table.RawGet(up) ?? DynValue.Nil : DynValue.Nil;
        });
        mt["__newindex"] = DynValue.NewCallback((ctx, a) =>
        {
            if (a.Count < 2 || a[0].Type != DataType.Table) return DynValue.Nil;
            DynValue k = a[1];
            DynValue v = a.Count > 2 ? a[2] : DynValue.Nil;
            string? up = Alias(k, dim);
            if (up != null) a[0].Table.Set(up, v);
            else a[0].Table.Set(k, v);
            return DynValue.Nil;
        });

        mt["__add"] = DynValue.NewCallback((ctx, a) => Arith(s, dim, name, a, '+'));
        mt["__sub"] = DynValue.NewCallback((ctx, a) => Arith(s, dim, name, a, '-'));
        mt["__mul"] = DynValue.NewCallback((ctx, a) => Arith(s, dim, name, a, '*'));
        mt["__div"] = DynValue.NewCallback((ctx, a) => Arith(s, dim, name, a, '/'));
        mt["__unm"] = DynValue.NewCallback((ctx, a) =>
        {
            Span<double> v = stackalloc double[4];
            if (!Read(a.Count > 0 ? a[0] : DynValue.Nil, dim, v))
                throw new ScriptRuntimeException($"attempt to negate a bad {name}");
            return NewVec(s, dim, -v[0], -v[1], -v[2], -v[3]);
        });
        mt["__eq"] = DynValue.NewCallback((ctx, a) =>
        {
            Span<double> l = stackalloc double[4];
            Span<double> r = stackalloc double[4];
            bool ok = a.Count > 1 && Read(a[0], dim, l) && Read(a[1], dim, r);
            for (int i = 0; ok && i < dim; i++) ok = l[i] == r[i];
            return DynValue.NewBoolean(ok);
        });
        mt["__tostring"] = DynValue.NewCallback((ctx, a) =>
        {
            Span<double> v = stackalloc double[4];
            if (!Read(a.Count > 0 ? a[0] : DynValue.Nil, dim, v)) return DynValue.NewString(name + "(?)");
            return DynValue.NewString(dim == 2
                ? $"{name}({Fmt(v[0])}, {Fmt(v[1])})"
                : $"{name}({Fmt(v[0])}, {Fmt(v[1])}, {Fmt(v[2])}, {Fmt(v[3])})");
        });

        s.Registry.Set(key, DynValue.NewTable(mt));
        return mt;
    }

    private static string? Alias(DynValue key, int dim)
    {
        if (key.Type != DataType.String || key.String.Length != 1) return null;
        int i = key.String[0] switch { 'x' => 0, 'y' => 1, 'z' => 2, 'w' => 3, _ => -1 };
        return i >= 0 && i < dim ? Upper[i] : null;
    }

    private static string Fmt(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>a op b: vector with vector (component-wise) or with a number (every component).</summary>
    private static DynValue Arith(Script s, int dim, string name, CallbackArguments a, char op)
    {
        DynValue left = a.Count > 0 ? a[0] : DynValue.Nil;
        DynValue right = a.Count > 1 ? a[1] : DynValue.Nil;
        Span<double> l = stackalloc double[4];
        Span<double> r = stackalloc double[4];
        if (!Operand(left, dim, l) || !Operand(right, dim, r))
        {
            string verb = op switch { '+' => "add", '-' => "subtract", '*' => "multiply", _ => "divide" };
            throw new ScriptRuntimeException(
                $"attempt to {verb} a {TypeName(left, dim, name)} and a {TypeName(right, dim, name)}");
        }
        Span<double> o = stackalloc double[4];
        for (int i = 0; i < 4; i++)
            o[i] = op switch { '+' => l[i] + r[i], '-' => l[i] - r[i], '*' => l[i] * r[i], _ => l[i] / r[i] };
        return NewVec(s, dim, o[0], o[1], o[2], o[3]);
    }

    /// <summary>A number fills every component; a vector (or vector-shaped table) gives its own.</summary>
    private static bool Operand(DynValue v, int dim, Span<double> into)
    {
        if (v.Type == DataType.Number)
        {
            into.Fill(v.Number);
            return true;
        }
        return Read(v, dim, into);
    }

    private static string TypeName(DynValue v, int dim, string name) =>
        v.Type == DataType.Table && Read(v, dim, stackalloc double[4]) ? name : v.Type.ToLuaTypeString();

    /// <summary>
    /// The first <paramref name="dim"/> components of a vector-shaped table: X/Y/Z/W, x/y/z/w,
    /// or [1]..[4]. Missing components are false (the rest of <paramref name="into"/> is 0).
    /// </summary>
    private static bool Read(DynValue v, int dim, Span<double> into)
    {
        into.Clear();
        if (v.Type != DataType.Table) return false;
        Table t = v.Table;
        for (int i = 0; i < dim; i++)
        {
            DynValue c = t.RawGet(Upper[i]) ?? DynValue.Nil;
            if (c.IsNil()) c = t.RawGet(Upper[i].ToLowerInvariant()) ?? DynValue.Nil;
            if (c.IsNil()) c = t.RawGet(i + 1) ?? DynValue.Nil;
            double? d = c.Type == DataType.Number ? c.Number : c.Type == DataType.String ? c.CastToNumber() : null;
            if (d == null) return false;
            into[i] = d.Value;
        }
        return true;
    }

    // ── Helpers for the imgui functions ─────────────────────────────────

    /// <summary>A Vector2, {x, y}, {X=, Y=} or {x=, y=}. False for nil or anything else.</summary>
    public static bool TryVec2(DynValue v, out float x, out float y)
    {
        Span<double> c = stackalloc double[4];
        bool ok = Read(v, 2, c);
        x = ok ? (float)c[0] : 0f;
        y = ok ? (float)c[1] : 0f;
        return ok;
    }

    /// <summary>A Vector4, {x, y, z, w}, {X=, ...} or {x=, ...}. False for nil or anything else.</summary>
    public static bool TryVec4(DynValue v, out float x, out float y, out float z, out float w)
    {
        Span<double> c = stackalloc double[4];
        bool ok = Read(v, 4, c);
        x = ok ? (float)c[0] : 0f;
        y = ok ? (float)c[1] : 0f;
        z = ok ? (float)c[2] : 0f;
        w = ok ? (float)c[3] : 0f;
        return ok;
    }

    /// <summary>
    /// A colour: a number is taken as 0xAABBGGRR as is; a Vector4 (or {r, g, b, a} table) with
    /// 0..1 components becomes 0xAABBGGRR (R in the low byte), clamped, rounded as ImGui does.
    /// </summary>
    public static uint ColorU32(DynValue v)
    {
        if (v.Type == DataType.Number)
        {
            double d = v.Number;
            if (!double.IsFinite(d)) return 0;
            return d >= 0 ? (uint)Math.Min(d, uint.MaxValue) : unchecked((uint)(int)Math.Max(d, int.MinValue));
        }
        if (TryVec4(v, out float r, out float g, out float b, out float a))
            return Byte(r) | (Byte(g) << 8) | (Byte(b) << 16) | (Byte(a) << 24);
        throw new ScriptRuntimeException($"a colour is a Vector4 (0..1) or a 0xAABBGGRR number, not {v.Type.ToLuaTypeString()}");
    }

    /// <summary>ImGui's IM_F32_TO_INT8_SAT: saturate, * 255, + 0.5.</summary>
    private static uint Byte(float f)
    {
        if (!(f > 0f)) return 0;   // NaN too
        if (f >= 1f) return 255;
        return (uint)(f * 255f + 0.5f);
    }

    // ── ImGui enums (ImGui.NET 1.91.6.1) ────────────────────────────────

    private static readonly (string, int)[] WindowFlags =
    {
        ("None", 0), ("NoTitleBar", 1), ("NoResize", 2), ("NoMove", 4), ("NoScrollbar", 8),
        ("NoScrollWithMouse", 16), ("NoCollapse", 32), ("NoDecoration", 43), ("AlwaysAutoResize", 64),
        ("NoBackground", 128), ("NoSavedSettings", 256), ("NoMouseInputs", 512), ("MenuBar", 1024),
        ("HorizontalScrollbar", 2048), ("NoFocusOnAppearing", 4096), ("NoBringToFrontOnFocus", 8192),
        ("AlwaysVerticalScrollbar", 16384), ("AlwaysHorizontalScrollbar", 32768), ("NoNavInputs", 65536),
        ("NoNavFocus", 131072), ("NoNav", 196608), ("NoInputs", 197120), ("UnsavedDocument", 262144),
        ("NoDocking", 524288),
    };

    private static readonly (string, int)[] Cond =
    {
        ("None", 0), ("Always", 1), ("Once", 2), ("FirstUseEver", 4), ("Appearing", 8),
    };

    private static readonly (string, int)[] Col =
    {
        ("Text", 0), ("TextDisabled", 1), ("WindowBg", 2), ("ChildBg", 3), ("PopupBg", 4), ("Border", 5),
        ("BorderShadow", 6), ("FrameBg", 7), ("FrameBgHovered", 8), ("FrameBgActive", 9), ("TitleBg", 10),
        ("TitleBgActive", 11), ("TitleBgCollapsed", 12), ("MenuBarBg", 13), ("ScrollbarBg", 14),
        ("ScrollbarGrab", 15), ("ScrollbarGrabHovered", 16), ("ScrollbarGrabActive", 17), ("CheckMark", 18),
        ("SliderGrab", 19), ("SliderGrabActive", 20), ("Button", 21), ("ButtonHovered", 22), ("ButtonActive", 23),
        ("Header", 24), ("HeaderHovered", 25), ("HeaderActive", 26), ("Separator", 27), ("SeparatorHovered", 28),
        ("SeparatorActive", 29), ("ResizeGrip", 30), ("ResizeGripHovered", 31), ("ResizeGripActive", 32),
        ("TabHovered", 33), ("Tab", 34), ("TabSelected", 35), ("TabSelectedOverline", 36), ("TabDimmed", 37),
        ("TabDimmedSelected", 38), ("TabDimmedSelectedOverline", 39), ("DockingPreview", 40),
        ("DockingEmptyBg", 41), ("PlotLines", 42), ("PlotLinesHovered", 43), ("PlotHistogram", 44),
        ("PlotHistogramHovered", 45), ("TableHeaderBg", 46), ("TableBorderStrong", 47), ("TableBorderLight", 48),
        ("TableRowBg", 49), ("TableRowBgAlt", 50), ("TextLink", 51), ("TextSelectedBg", 52),
        ("DragDropTarget", 53), ("NavCursor", 54), ("NavWindowingHighlight", 55), ("NavWindowingDimBg", 56),
        ("ModalWindowDimBg", 57), ("COUNT", 58),
    };

    private static readonly (string, int)[] TreeNodeFlags =
    {
        ("None", 0), ("Selected", 1), ("Framed", 2), ("AllowOverlap", 4), ("NoTreePushOnOpen", 8),
        ("NoAutoOpenOnLog", 16), ("CollapsingHeader", 26), ("DefaultOpen", 32), ("OpenOnDoubleClick", 64),
        ("OpenOnArrow", 128), ("Leaf", 256), ("Bullet", 512), ("FramePadding", 1024), ("SpanAvailWidth", 2048),
        ("SpanFullWidth", 4096), ("SpanTextWidth", 8192), ("SpanAllColumns", 16384),
        ("NavLeftJumpsBackHere", 32768),
    };

    private static readonly (string, int)[] InputTextFlags =
    {
        ("None", 0), ("CharsDecimal", 1), ("CharsHexadecimal", 2), ("CharsScientific", 4), ("CharsUppercase", 8),
        ("CharsNoBlank", 16), ("AllowTabInput", 32), ("EnterReturnsTrue", 64), ("EscapeClearsAll", 128),
        ("CtrlEnterForNewLine", 256), ("ReadOnly", 512), ("Password", 1024), ("AlwaysOverwrite", 2048),
        ("AutoSelectAll", 4096), ("ParseEmptyRefVal", 8192), ("DisplayEmptyRefVal", 16384),
        ("NoHorizontalScroll", 32768), ("NoUndoRedo", 65536), ("ElideLeft", 131072),
    };

    private static readonly (string, int)[] SelectableFlags =
    {
        ("None", 0), ("NoAutoClosePopups", 1), ("SpanAllColumns", 2), ("AllowDoubleClick", 4), ("Disabled", 8),
        ("AllowOverlap", 16), ("Highlight", 32),
    };

    private static readonly (string, int)[] SliderFlags =
    {
        ("None", 0), ("Logarithmic", 32), ("NoRoundToFormat", 64), ("NoInput", 128), ("WrapAround", 256),
        ("ClampOnInput", 512), ("ClampZeroRange", 1024), ("AlwaysClamp", 1536),
    };

    private static readonly (string, int)[] ChildFlags =
    {
        ("None", 0), ("Borders", 1), ("AlwaysUseWindowPadding", 2), ("ResizeX", 4), ("ResizeY", 8),
        ("AutoResizeX", 16), ("AutoResizeY", 32), ("AlwaysAutoResize", 64), ("FrameStyle", 128),
        ("NavFlattened", 256),
    };
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// UtilityBelt-style enum values for Lua.
///
/// Typed values (the enums in <see cref="LibraryEnums.TypedEnums"/>): a table per value with
/// the raw fields <c>__enum</c> (the enum's name) and <c>__value</c> (its number), one table
/// per value per script, so <c>==</c> works by identity. They print as their name (flags as
/// "A, B"), compare and order with each other, add and subtract as numbers, concatenate as
/// text, and have <c>ToNumber()</c>, <c>HasFlags(...)</c>, <c>AddFlags(...)</c> and
/// <c>RemoveFlags(...)</c> (the last two return typed values).
///
/// Plain-number enums get the same four helpers through a number metatable (numbers have no
/// fields otherwise; indexing a number with anything else is still an error).
/// </summary>
internal static class LuaEnumValues
{
    public const string EnumField = "__enum";
    public const string ValueField = "__value";

    private sealed class Def
    {
        public bool Flags;
        public readonly Dictionary<long, string> Names = new();
        public readonly List<(string Name, long Value)> Members = new();
    }

    private sealed class Cache
    {
        public readonly Dictionary<(string, long), DynValue> Values = new();
        public readonly Dictionary<string, Table> Metas = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, Def> Defs = new(StringComparer.Ordinal);
    private static readonly object DefsLock = new();
    private static readonly ConditionalWeakTable<Script, Cache> Caches = new();

    /// <summary>Records an enum's names (once; the first name for a number is its name).</summary>
    public static void Define(string name, List<(string Name, long Value)> members, bool flags)
    {
        lock (DefsLock)
        {
            if (Defs.ContainsKey(name)) return;
            var d = new Def { Flags = flags };
            foreach (var (n, v) in members)
            {
                d.Members.Add((n, v));
                d.Names.TryAdd(v, n);
            }
            Defs[name] = d;
        }
    }

    /// <summary>Flags when there are more than three nonzero members and all are single bits.</summary>
    public static bool LooksLikeFlags(List<(string Name, long Value)> members)
    {
        int n = 0;
        foreach (var (_, v) in members)
        {
            if (v == 0) continue;
            if (v < 0 || (v & (v - 1)) != 0) return false;
            n++;
        }
        return n > 3;
    }

    /// <summary>Whether <paramref name="enumName"/> is a flags enum.</summary>
    public static bool IsFlags(string enumName)
    {
        lock (DefsLock) return Defs.TryGetValue(enumName, out Def? d) && d.Flags;
    }

    /// <summary>"Creature", "Creature, Misc" for combined flags, or the number.</summary>
    public static string NameOf(string enumName, long value)
    {
        Def? d;
        lock (DefsLock) Defs.TryGetValue(enumName, out d);
        if (d == null) return value.ToString(CultureInfo.InvariantCulture);
        if (d.Names.TryGetValue(value, out string? name)) return name;
        if (d.Flags && value > 0)
        {
            var parts = new List<string>();
            long rest = value;
            foreach (var (n, v) in d.Members)
                if (v != 0 && (v & (v - 1)) == 0 && (rest & v) == v && !parts.Contains(n)) { parts.Add(n); rest &= ~v; }
            if (rest == 0) return string.Join(", ", parts);
        }
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The typed value of <paramref name="enumName"/> for <paramref name="value"/> in this script.</summary>
    public static DynValue Get(Script s, string enumName, long value)
    {
        Cache cache = Caches.GetOrCreateValue(s);
        if (cache.Values.TryGetValue((enumName, value), out DynValue? v)) return v;
        var t = new Table(s);
        t.Set(EnumField, DynValue.NewString(enumName));
        t.Set(ValueField, DynValue.NewNumber(value));
        t.MetaTable = Meta(s, cache, enumName);
        v = DynValue.NewTable(t);
        cache.Values[(enumName, value)] = v;
        return v;
    }

    /// <summary>A typed value's enum and number.</summary>
    public static bool TryRead(DynValue v, out string enumName, out long value)
    {
        enumName = string.Empty;
        value = 0;
        if (v.Type != DataType.Table) return false;
        DynValue? e = v.Table.RawGet(EnumField);
        DynValue? n = v.Table.RawGet(ValueField);
        if (e == null || n == null || e.Type != DataType.String || n.Type != DataType.Number) return false;
        enumName = e.String;
        value = (long)n.Number;
        return true;
    }

    /// <summary>True for a typed enum value (never an options table or a WorldObject).</summary>
    public static bool IsTyped(DynValue v) => TryRead(v, out _, out _);

    /// <summary>The number of a plain number or a typed value, else null.</summary>
    public static double? AsNumber(DynValue v)
    {
        if (v.Type == DataType.Number) return v.Number;
        return TryRead(v, out _, out long n) ? n : null;
    }

    private static long Bits(DynValue v, string what)
    {
        double? n = AsNumber(v);
        if (n == null) throw new ScriptRuntimeException($"{what}: expected an enum value or a number, got {v.Type.ToString().ToLowerInvariant()}");
        return (long)n.Value;
    }

    private static string Text(DynValue v) =>
        TryRead(v, out string e, out long n) ? NameOf(e, n) : v.Type == DataType.String ? v.String : v.ToPrintString();

    private static Table Meta(Script s, Cache cache, string enumName)
    {
        if (cache.Metas.TryGetValue(enumName, out Table? mt)) return mt;
        mt = new Table(s);
        var methods = new Table(s);
        methods.Set("ToNumber", DynValue.NewCallback((c, a) => DynValue.NewNumber(Bits(a[0], "ToNumber"))));
        methods.Set("HasFlags", DynValue.NewCallback((c, a) =>
        {
            long self = Bits(a[0], "HasFlags");
            for (int i = 1; i < a.Count; i++)
            {
                long f = Bits(a[i], "HasFlags");
                if ((self & f) != f) return DynValue.False;
            }
            return DynValue.True;
        }));
        methods.Set("AddFlags", DynValue.NewCallback((c, a) =>
        {
            long self = Bits(a[0], "AddFlags");
            for (int i = 1; i < a.Count; i++) self |= Bits(a[i], "AddFlags");
            return Get(s, enumName, self);
        }));
        methods.Set("RemoveFlags", DynValue.NewCallback((c, a) =>
        {
            long self = Bits(a[0], "RemoveFlags");
            for (int i = 1; i < a.Count; i++) self &= ~Bits(a[i], "RemoveFlags");
            return Get(s, enumName, self);
        }));
        mt.Set("__index", DynValue.NewTable(methods));
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString(Text(a[0]))));
        mt.Set("__eq", DynValue.NewCallback((c, a) =>
            DynValue.NewBoolean(TryRead(a[0], out string e1, out long v1) && TryRead(a[1], out string e2, out long v2) && e1 == e2 && v1 == v2)));
        mt.Set("__lt", DynValue.NewCallback((c, a) => DynValue.NewBoolean(Bits(a[0], "<") < Bits(a[1], "<"))));
        mt.Set("__le", DynValue.NewCallback((c, a) => DynValue.NewBoolean(Bits(a[0], "<=") <= Bits(a[1], "<="))));
        mt.Set("__add", DynValue.NewCallback((c, a) => DynValue.NewNumber(AsNumber(a[0]) + AsNumber(a[1]) ?? throw new ScriptRuntimeException("attempt to add an enum value and a non-number"))));
        mt.Set("__sub", DynValue.NewCallback((c, a) => DynValue.NewNumber(AsNumber(a[0]) - AsNumber(a[1]) ?? throw new ScriptRuntimeException("attempt to subtract an enum value and a non-number"))));
        mt.Set("__concat", DynValue.NewCallback((c, a) => DynValue.NewString(Text(a[0]) + Text(a[1]))));
        cache.Metas[enumName] = mt;
        return mt;
    }

    /// <summary>
    /// Number helpers (UB's enum methods on plain-number enum values): <c>n:ToNumber()</c>,
    /// <c>n:HasFlags(...)</c>, <c>n:AddFlags(...)</c>, <c>n:RemoveFlags(...)</c>. Any other key
    /// is the usual "attempt to index a number value" error.
    /// </summary>
    public static void InstallNumberHelpers(Script s)
    {
        if (s.GetTypeMetatable(DataType.Number) != null) return;
        var methods = new Dictionary<string, DynValue>(StringComparer.Ordinal)
        {
            ["ToNumber"] = DynValue.NewCallback((c, a) => DynValue.NewNumber(Bits(a[0], "ToNumber"))),
            ["HasFlags"] = DynValue.NewCallback((c, a) =>
            {
                long self = Bits(a[0], "HasFlags");
                for (int i = 1; i < a.Count; i++)
                {
                    long f = Bits(a[i], "HasFlags");
                    if ((self & f) != f) return DynValue.False;
                }
                return DynValue.True;
            }),
            ["AddFlags"] = DynValue.NewCallback((c, a) =>
            {
                long self = Bits(a[0], "AddFlags");
                for (int i = 1; i < a.Count; i++) self |= Bits(a[i], "AddFlags");
                return DynValue.NewNumber(self);
            }),
            ["RemoveFlags"] = DynValue.NewCallback((c, a) =>
            {
                long self = Bits(a[0], "RemoveFlags");
                for (int i = 1; i < a.Count; i++) self &= ~Bits(a[i], "RemoveFlags");
                return DynValue.NewNumber(self);
            }),
        };
        var mt = new Table(s);
        mt.Set("__index", DynValue.NewCallback((c, a) =>
        {
            string key = a.Count > 1 ? a[1].ToPrintString() : "?";
            if (a.Count > 1 && a[1].Type == DataType.String && methods.TryGetValue(a[1].String, out DynValue? fn)) return fn;
            throw new ScriptRuntimeException($"attempt to index a number value (field '{key}')");
        }));
        s.SetTypeMetatable(DataType.Number, mt);
    }
}

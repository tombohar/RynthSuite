using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// <c>require("json")</c>: <c>json.encode(value [, pretty])</c> and <c>json.decode(text)</c>
/// (also <c>Encode</c>/<c>Decode</c>). Written by hand with Utf8JsonWriter / JsonDocument, no
/// reflection serialization (NativeAOT).
///
/// Lua → JSON: nil → null; a table whose keys are exactly 1..n → array (an empty table → [])
/// ; any other table → object (string keys, number keys written as text); a WorldObject →
/// its Id. Functions, coroutines, userdata, NaN/inf and tables that contain themselves are
/// errors that say where the bad value is.
/// JSON → Lua: objects and arrays → tables (arrays 1-based), null → nil (an array keeps its
/// other indexes), numbers → numbers.
///
/// Also the names of the JSON module UtilityBelt's interpreter ships (MoonSharp/WattleScript):
/// <c>json.parse(text)</c> (like decode, but JSON null becomes <c>json.null()</c>, so arrays
/// keep their holes), <c>json.serialize(value)</c> (encode), <c>json.null()</c> (the null
/// value, which encodes as null) and <c>json.isnull(v)</c> (true for nil and json.null()).
/// Typed enum values encode as their number.
/// </summary>
internal static class LibraryJson
{
    /// <summary>Nesting limit both ways (a runaway structure errors instead of overflowing the stack).</summary>
    public const int MaxDepth = 200;

    private static readonly JsonWriterOptions CompactOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = MaxDepth + 8,
    };

    private static readonly JsonWriterOptions PrettyOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = MaxDepth + 8,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = MaxDepth,
    };

    public static DynValue CreateModule(Script s)
    {
        var m = new Table(s);
        DynValue encode = DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, m);
            return DynValue.NewString(Encode(LuaArgs.At(args, 0), LuaArgs.At(args, 1).CastToBool(), "json.encode"));
        });
        DynValue decode = DynValue.NewCallback((c, a) =>
        {
            DynValue text = LuaArgs.At(LuaArgs.Of(a, m), 0);
            if (text.Type != DataType.String)
                throw new ScriptRuntimeException($"json.decode(text) needs a string, got {TypeName(text)}");
            return Decode(s, text.String, "json.decode");
        });
        m.Set("encode", encode);
        m.Set("decode", decode);
        m.Set("Encode", encode);
        m.Set("Decode", decode);

        DynValue jsonNull = NullValue(s);
        m.Set("serialize", encode);
        m.Set("parse", DynValue.NewCallback((c, a) =>
        {
            DynValue text = LuaArgs.At(LuaArgs.Of(a, m), 0);
            if (text.Type != DataType.String)
                throw new ScriptRuntimeException($"json.parse(text) needs a string, got {TypeName(text)}");
            return Decode(s, text.String, "json.parse", jsonNull);
        }));
        m.Set("null", DynValue.NewCallback((c, a) => jsonNull));
        m.Set("isnull", DynValue.NewCallback((c, a) =>
        {
            DynValue v = LuaArgs.At(LuaArgs.Of(a, m), 0);
            return DynValue.NewBoolean(v.IsNil() || IsNull(v));
        }));
        var mt = new Table(s);
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString("module json")));
        m.MetaTable = mt;
        return DynValue.NewTable(m);
    }

    private const string NullMarker = "__rynth_jsonnull";

    /// <summary>This script's json.null() value: a table that encodes as null and prints as "null".</summary>
    private static DynValue NullValue(Script s)
    {
        DynValue cached = s.Registry.Get("rynth.jsonnull");
        if (cached.Type == DataType.Table) return cached;
        var t = new Table(s);
        t.Set(NullMarker, DynValue.True);
        var mt = new Table(s);
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString("null")));
        t.MetaTable = mt;
        DynValue v = DynValue.NewTable(t);
        s.Registry.Set("rynth.jsonnull", v);
        return v;
    }

    private static bool IsNull(DynValue v) =>
        v.Type == DataType.Table && v.Table.RawGet(NullMarker) is { Type: DataType.Boolean, Boolean: true };

    // ── Encode ──────────────────────────────────────────────────────────────

    /// <summary>JSON text for <paramref name="value"/>; <paramref name="what"/> prefixes error messages.</summary>
    public static string Encode(DynValue value, bool pretty, string what)
    {
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using (var w = new Utf8JsonWriter(buffer, pretty ? PrettyOptions : CompactOptions))
            {
                var onPath = new HashSet<Table>(ReferenceEqualityComparer.Instance);
                Write(w, value, onPath, new List<string>(), what);
            }
        }
        catch (ScriptRuntimeException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ScriptRuntimeException($"{what}: {ex.Message}");
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter w, DynValue v, HashSet<Table> onPath, List<string> path, string what)
    {
        switch (v.Type)
        {
            case DataType.Nil:
            case DataType.Void:
                w.WriteNullValue();
                return;
            case DataType.Boolean:
                w.WriteBooleanValue(v.Boolean);
                return;
            case DataType.Number:
                WriteNumber(w, v.Number, path, what);
                return;
            case DataType.String:
                w.WriteStringValue(v.String);
                return;
            case DataType.Tuple:
                Write(w, v.Tuple.Length > 0 ? v.Tuple[0] : DynValue.Nil, onPath, path, what);
                return;
            case DataType.Table:
                WriteTable(w, v.Table, onPath, path, what);
                return;
            default:
                throw Fail(what, path, $"can't encode a {TypeName(v)}");
        }
    }

    private static void WriteNumber(Utf8JsonWriter w, double d, List<string> path, string what)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            throw Fail(what, path, $"can't encode {(double.IsNaN(d) ? "nan" : "an infinite number")} (JSON has no such number)");
        // Whole numbers as integers ("12", not "12.0"), within the exactly representable range.
        if (Math.Floor(d) == d && Math.Abs(d) <= 9007199254740992.0) w.WriteNumberValue((long)d);
        else w.WriteNumberValue(d);
    }

    private static void WriteTable(Utf8JsonWriter w, Table t, HashSet<Table> onPath, List<string> path, string what)
    {
        DynValue self = DynValue.NewTable(t);
        if (IsNull(self)) { w.WriteNullValue(); return; }
        if (LuaEnumValues.TryRead(self, out _, out long enumValue)) { w.WriteNumberValue(enumValue); return; }

        // A WorldObject (RynthLuaPlugin.WrapObject) is written as its id: its other raw
        // fields are action methods, and ids are what a script can look it up by again.
        DynValue? marker = t.RawGet("__rynth_object");
        if (marker != null && marker.Type == DataType.Boolean && marker.Boolean)
        {
            DynValue? id = t.RawGet("Id");
            if (id != null && id.Type == DataType.Number) { w.WriteNumberValue((long)id.Number); return; }
        }

        if (path.Count >= MaxDepth) throw Fail(what, path, $"tables nested more than {MaxDepth} deep");
        if (!onPath.Add(t)) throw Fail(what, path, "the table contains itself (a cycle can't be written as JSON)");

        // An array when the keys are exactly 1..n (every key a positive whole number, the
        // largest equal to the count).
        int count = 0;
        double max = 0;
        bool isArray = true;
        foreach (TablePair p in t.Pairs)
        {
            if (p.Value.IsNil()) continue;
            count++;
            if (!isArray) continue;
            DynValue k = p.Key;
            if (k.Type == DataType.Number && k.Number >= 1 && Math.Floor(k.Number) == k.Number)
            {
                if (k.Number > max) max = k.Number;
            }
            else isArray = false;
        }

        if (isArray && max == count)
        {
            w.WriteStartArray();
            for (int i = 1; i <= count; i++)
            {
                path.Add("[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                Write(w, t.RawGet(i) ?? DynValue.Nil, onPath, path, what);
                path.RemoveAt(path.Count - 1);
            }
            w.WriteEndArray();
        }
        else
        {
            w.WriteStartObject();
            foreach (TablePair p in t.Pairs)
            {
                if (p.Value.IsNil()) continue;
                string key = p.Key.Type switch
                {
                    DataType.String => p.Key.String,
                    DataType.Number => FormatKey(p.Key.Number),
                    _ => throw Fail(what, path, $"can't encode a table key that is a {TypeName(p.Key)} (JSON keys are text)"),
                };
                w.WritePropertyName(key);
                path.Add(p.Key.Type == DataType.String ? "." + key : "[" + key + "]");
                Write(w, p.Value, onPath, path, what);
                path.RemoveAt(path.Count - 1);
            }
            w.WriteEndObject();
        }
        onPath.Remove(t);
    }

    private static string FormatKey(double d) =>
        Math.Floor(d) == d && Math.Abs(d) <= 9007199254740992.0
            ? ((long)d).ToString(CultureInfo.InvariantCulture)
            : d.ToString("R", CultureInfo.InvariantCulture);

    private static ScriptRuntimeException Fail(string what, List<string> path, string message)
    {
        // Deep paths show their last few steps only.
        const int Shown = 8;
        string at = path.Count <= Shown
            ? string.Concat(path).TrimStart('.')
            : "..." + string.Concat(path.GetRange(path.Count - Shown, Shown));
        string where = path.Count == 0 ? string.Empty : " (at " + at + ")";
        return new ScriptRuntimeException($"{what}: {message}{where}");
    }

    internal static string TypeName(DynValue v) => v.Type switch
    {
        DataType.Nil or DataType.Void => "nil",
        DataType.Boolean => "boolean",
        DataType.Number => "number",
        DataType.String => "string",
        DataType.Table => "table",
        DataType.Function or DataType.ClrFunction => "function",
        DataType.Thread => "coroutine",
        DataType.UserData => "userdata",
        _ => v.Type.ToString().ToLowerInvariant(),
    };

    // ── Decode ──────────────────────────────────────────────────────────────

    /// <summary>The Lua value for JSON <paramref name="text"/>; <paramref name="what"/> prefixes error messages.</summary>
    public static DynValue Decode(Script s, string text, string what, DynValue? jsonNull = null)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text, ReadOptions);
            return ToLua(s, doc.RootElement, jsonNull);
        }
        catch (JsonException ex)
        {
            throw new ScriptRuntimeException($"{what}: not valid JSON: {ex.Message}");
        }
    }

    /// <param name="jsonNull">What JSON null becomes (nil when not given).</param>
    internal static DynValue ToLua(Script s, JsonElement e, DynValue? jsonNull = null)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var t = new Table(s);
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    DynValue v = ToLua(s, p.Value, jsonNull);
                    if (!v.IsNil()) t.Set(p.Name, v);
                }
                return DynValue.NewTable(t);
            }
            case JsonValueKind.Array:
            {
                var t = new Table(s);
                int i = 0;
                foreach (JsonElement item in e.EnumerateArray())
                {
                    i++;
                    DynValue v = ToLua(s, item, jsonNull);
                    if (!v.IsNil()) t.Set(i, v);
                }
                return DynValue.NewTable(t);
            }
            case JsonValueKind.String:
                return DynValue.NewString(e.GetString() ?? string.Empty);
            case JsonValueKind.Number:
                if (e.TryGetDouble(out double d)) return DynValue.NewNumber(d);
                return DynValue.NewNumber(double.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture));
            case JsonValueKind.True:
                return DynValue.True;
            case JsonValueKind.False:
                return DynValue.False;
            case JsonValueKind.Null:
                return jsonNull ?? DynValue.Nil;
            default:
                return DynValue.Nil;
        }
    }
}

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using MoonSharp.Interpreter;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// <c>require("storage")</c>: a script's saved values, as JSON in its data folder
/// (<c>C:\Games\RynthSuite\RynthLua\Data\&lt;Script&gt;\</c>):
///
///   storage.Get(key [, default])   the value, or default (nil) when there is none
///   storage.Set(key, value)        any JSON-able value (tables, numbers, text, booleans); nil removes
///   storage.Remove(key)            true when it was there
///   storage.Has(key), storage.Keys() (sorted), storage.Clear(), storage.Save() (write now)
///   storage.Character              the same, for the logged-in character (one file per character)
///
/// Values are copies: Set encodes the value right away (so a table changed afterwards isn't
/// changed in storage until it is Set again) and Get returns a fresh table each time.
/// Keys are just names inside one JSON file, never paths. Writes are debounced (at most
/// ~2 s after the first change) on a timer thread, atomic (temp file, then a move over the
/// old one), and flushed when the script stops.
/// </summary>
internal static class LibraryStorage
{
    public const string FileName = "storage.json";
    public const string CharacterFolder = "characters";

    /// <param name="dataRoot">RynthLua's data folder (the script's folder is made under it).</param>
    /// <param name="characterKey">"server|character" of the logged-in character, "" before login.</param>
    public static DynValue CreateModule(Script s, ScriptContext ctx, string dataRoot, Func<string> characterKey)
    {
        string folder = LibraryPaths.ScriptDataFolder(dataRoot, ctx.Name);
        Action<string> warn = msg => ctx.Host.Print("storage: " + msg);

        var main = new LibraryStore(Path.Combine(folder, FileName), warn);
        var perCharacter = new Dictionary<string, LibraryStore>(StringComparer.OrdinalIgnoreCase);
        ctx.OnStop.Add(() =>
        {
            main.Dispose();
            foreach (LibraryStore st in perCharacter.Values) st.Dispose();
        });

        LibraryStore CharacterStore()
        {
            string key = characterKey();
            if (key.Length == 0)
                throw new ScriptRuntimeException("storage.Character needs a logged-in character");
            if (!perCharacter.TryGetValue(key, out LibraryStore? st))
            {
                string file = Path.Combine(folder, CharacterFolder, LibraryPaths.SafeFileName(key.Replace('|', '_')) + ".json");
                perCharacter[key] = st = new LibraryStore(file, warn);
            }
            return st;
        }

        Table storage = CreateApi(s, () => main, "storage");
        Table character = CreateApi(s, CharacterStore, "storage.Character");
        storage.Set("Character", DynValue.NewTable(character));
        return DynValue.NewTable(storage);
    }

    private static Table CreateApi(Script s, Func<LibraryStore> store, string what)
    {
        var t = new Table(s);

        string Key(DynValue[] args, string method)
        {
            DynValue k = LuaArgs.At(args, 0);
            if (k.Type != DataType.String && k.Type != DataType.Number)
                throw new ScriptRuntimeException($"{what}.{method}(key, ...) needs a key (text), got {LibraryJson.TypeName(k)}");
            string key = k.CastToString() ?? string.Empty;
            if (key.Length == 0) throw new ScriptRuntimeException($"{what}.{method}: the key can't be empty");
            if (key.Length > 512) throw new ScriptRuntimeException($"{what}.{method}: keys are at most 512 characters");
            return key;
        }

        t.Set("Get", DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, t);
            string key = Key(args, "Get");
            string? json = store().Get(key);
            if (json == null) return LuaArgs.At(args, 1);
            DynValue v = LibraryJson.Decode(s, json, $"{what}.Get(\"{key}\")");
            return v.IsNil() ? LuaArgs.At(args, 1) : v;
        }));
        t.Set("Set", DynValue.NewCallback((c, a) =>
        {
            var args = LuaArgs.Of(a, t);
            string key = Key(args, "Set");
            DynValue value = LuaArgs.At(args, 1);
            if (value.IsNil()) store().Remove(key);
            else store().Set(key, LibraryJson.Encode(value, false, $"{what}.Set(\"{key}\")"));
            return DynValue.Nil;
        }));
        t.Set("Remove", DynValue.NewCallback((c, a) =>
            DynValue.NewBoolean(store().Remove(Key(LuaArgs.Of(a, t), "Remove")))));
        t.Set("Has", DynValue.NewCallback((c, a) =>
            DynValue.NewBoolean(store().Get(Key(LuaArgs.Of(a, t), "Has")) != null)));
        t.Set("Keys", DynValue.NewCallback((c, a) =>
        {
            var list = new Table(s);
            foreach (string k in store().Keys()) list.Append(DynValue.NewString(k));
            return DynValue.NewTable(list);
        }));
        t.Set("Clear", DynValue.NewCallback((c, a) => { store().Clear(); return DynValue.Nil; }));
        t.Set("Save", DynValue.NewCallback((c, a) => DynValue.NewBoolean(store().Flush())));

        var mt = new Table(s);
        mt.Set("__tostring", DynValue.NewCallback((c, a) => DynValue.NewString(what)));
        t.MetaTable = mt;
        return t;
    }
}

/// <summary>
/// One JSON file of key → value. Values are kept as their JSON text (so a stored value is a
/// copy). Thread-safe: the debounce timer writes from a pool thread.
/// </summary>
internal sealed class LibraryStore : IDisposable
{
    public const int DebounceMs = 2000;

    /// <summary>One file operation at a time across every store (a restarted script's new store
    /// never reads while its old one is still writing).</summary>
    private static readonly object IoLock = new();

    private static readonly JsonWriterOptions FileWriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly Action<string> _warn;
    private readonly object _lock = new();
    private Dictionary<string, string>? _values;
    private long _version;       // bumped on every change
    private long _savedVersion;  // the version on disk
    private Timer? _timer;
    private bool _disposed;

    public LibraryStore(string path, Action<string> warn)
    {
        _path = path;
        _warn = warn;
    }

    public string FilePath => _path;

    private Dictionary<string, string> Values
    {
        get
        {
            if (_values != null) return _values;
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            lock (IoLock)
            {
                try
                {
                    if (!File.Exists(_path)) return _values;
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(_path),
                        new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = LibraryJson.MaxDepth });
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        throw new JsonException("the file isn't a JSON object");
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                        _values[p.Name] = p.Value.GetRawText();
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Keep the unreadable file for the player instead of overwriting it.
                    string aside = _path + ".unreadable";
                    try { File.Copy(_path, aside, overwrite: true); } catch { }
                    _warn($"couldn't read {Path.GetFileName(_path)} ({ex.Message}); starting empty, the old file is kept as {Path.GetFileName(aside)}");
                    _values.Clear();
                }
            }
            return _values;
        }
    }

    public string? Get(string key)
    {
        lock (_lock) return Values.TryGetValue(key, out string? json) ? json : null;
    }

    public void Set(string key, string json)
    {
        lock (_lock)
        {
            if (Values.TryGetValue(key, out string? old) && old == json) return;
            Values[key] = json;
            Changed();
        }
    }

    public bool Remove(string key)
    {
        lock (_lock)
        {
            if (!Values.Remove(key)) return false;
            Changed();
            return true;
        }
    }

    public string[] Keys()
    {
        lock (_lock)
        {
            var keys = Values.Keys.ToArray();
            Array.Sort(keys, StringComparer.OrdinalIgnoreCase);
            return keys;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (Values.Count == 0) return;
            Values.Clear();
            Changed();
        }
    }

    /// <summary>Under _lock: note the change and start the debounce clock if it isn't running.</summary>
    private void Changed()
    {
        _version++;
        if (_disposed) return;
        if (_timer == null) _timer = new Timer(state => Flush(),null, DebounceMs, Timeout.Infinite);
        else if (_version == _savedVersion + 1) _timer.Change(DebounceMs, Timeout.Infinite);
    }

    /// <summary>Writes the file if anything changed since the last write. False when the write failed.</summary>
    public bool Flush()
    {
        byte[] bytes;
        long version;
        lock (_lock)
        {
            if (_values == null || _version == _savedVersion) return true;
            version = _version;
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer, FileWriterOptions))
            {
                w.WriteStartObject();
                foreach (var kv in _values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    w.WritePropertyName(kv.Key);
                    w.WriteRawValue(kv.Value, skipInputValidation: true);
                }
                w.WriteEndObject();
            }
            bytes = buffer.WrittenSpan.ToArray();
        }

        lock (IoLock)
        {
            string tmp = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                _warn($"couldn't save {Path.GetFileName(_path)}: {ex.Message}");
                return false;
            }
        }

        lock (_lock)
        {
            if (version > _savedVersion) _savedVersion = version;
            // Changes made while writing: start the clock again.
            if (_version != _savedVersion && !_disposed) _timer?.Change(DebounceMs, Timeout.Infinite);
        }
        return true;
    }

    /// <summary>Script stopped: stop the timer and write what's pending, now.</summary>
    public void Dispose()
    {
        Timer? t;
        lock (_lock)
        {
            _disposed = true;
            t = _timer;
            _timer = null;
        }
        t?.Dispose();
        Flush();
    }
}

/// <summary>File and folder names for the library modules: sanitizing, and resolving a
/// script-supplied relative path so it can never leave its root folder.</summary>
internal static class LibraryPaths
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The script's data folder, <c>&lt;dataRoot&gt;\&lt;safe script name&gt;</c>.</summary>
    public static string ScriptDataFolder(string dataRoot, string scriptName) =>
        Path.Combine(dataRoot, SafeFileName(scriptName));

    /// <summary>A single, harmless file name made from <paramref name="name"/> (no separators,
    /// no "..", no device names, no trailing dots or spaces).</summary>
    public static string SafeFileName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = (name ?? string.Empty).Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0 || chars[i] < 32) chars[i] = '_';
        string n = new string(chars).TrimEnd('.', ' ');
        if (n.Length > 80) n = n[..80].TrimEnd('.', ' ');
        if (n.Length == 0 || n.Trim('.').Length == 0) n = "_" + n;
        string stem = n.Split('.')[0];
        if (ReservedNames.Contains(stem)) n = "_" + n;
        return n;
    }

    /// <summary>
    /// The full path of <paramref name="relative"/> under <paramref name="root"/>, or null (with
    /// <paramref name="error"/>) when it is absolute, uses a drive, a device name, a stream
    /// (":"), wildcards, or climbs out of the root. "" and "." are the root itself.
    /// </summary>
    public static string? Resolve(string root, string? relative, out string error)
    {
        error = string.Empty;
        string rel = (relative ?? string.Empty).Trim().Replace('/', '\\');
        string fullRoot = Path.GetFullPath(root).TrimEnd('\\');
        if (rel.Length == 0 || rel == ".") return fullRoot;

        if (Path.IsPathRooted(rel) || rel.Contains(':') || rel.StartsWith('\\'))
        {
            error = $"'{relative}': use a path relative to the folder (no drive letters or leading slashes)";
            return null;
        }
        if (rel.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0 || rel.Any(ch => ch < 32))
        {
            error = $"'{relative}': the path has characters a file name can't have";
            return null;
        }
        foreach (string part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            string stem = part.TrimEnd('.', ' ').Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                error = $"'{relative}': '{part}' is a reserved device name";
                return null;
            }
        }

        string full = Path.GetFullPath(Path.Combine(fullRoot, rel)).TrimEnd('\\');
        if (!full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase))
        {
            error = $"'{relative}': outside the script's folder";
            return null;
        }
        return full;
    }
}

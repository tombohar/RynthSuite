using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// The player's last show/hide choice for each script window, per character (design doc §8
/// decision 9): <c>hud_visibility.json</c> in RynthLua's data folder,
/// <c>{"server|character": {"RynthLua/script/hud": true|false, ...}, ...}</c>.
///
/// Only the player's choices are recorded (the window's X, the bar's Scripts menu,
/// <c>/rc ui</c>, <c>/lua hud show|hide|toggle</c>); a script's own <c>hud.Visible = ...</c> is not.
/// Characters never share entries. Read lazily with JsonDocument and written with
/// Utf8JsonWriter (no serialized types, so nothing pins the plugin's load context), about 2 s
/// after the last change, and on logout and shutdown. Every file operation is caught; a bad or
/// missing file is treated as empty. Pump thread only.
/// </summary>
internal sealed class HudVisibilityMemory
{
    public const int MaxKeysPerCharacter = 500;
    private const long SaveDelayMs = 2000;

    private readonly string _path;
    private readonly Action<string> _log;
    // character → window key → (visible, stamp). The stamp orders entries oldest first, so the
    // cap drops the choice made longest ago.
    private Dictionary<string, Dictionary<string, (bool Visible, long Stamp)>>? _data;
    private long _stamp;
    private bool _dirty;
    private long _dirtyAtMs;
    private readonly HashSet<string> _failuresLogged = new(StringComparer.Ordinal);

    public HudVisibilityMemory(string path, Action<string> log)
    {
        _path = path;
        _log = log;
    }

    public string Path => _path;
    public bool Dirty => _dirty;

    /// <summary>The player's remembered choice for <paramref name="windowKey"/> on <paramref name="character"/>.</summary>
    public bool TryGet(string character, string windowKey, out bool visible)
    {
        visible = false;
        if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(windowKey)) return false;
        Load();
        if (_data!.TryGetValue(character, out var windows) && windows.TryGetValue(windowKey, out var e))
        {
            visible = e.Visible;
            return true;
        }
        return false;
    }

    /// <summary>Records the player's choice. No-op before login (empty character).</summary>
    public void Record(string character, string windowKey, bool visible, long nowMs)
    {
        if (string.IsNullOrEmpty(character) || string.IsNullOrEmpty(windowKey)) return;
        Load();
        if (!_data!.TryGetValue(character, out var windows))
            _data[character] = windows = new Dictionary<string, (bool, long)>(StringComparer.Ordinal);
        if (windows.TryGetValue(windowKey, out var old) && old.Visible == visible)
        {
            windows[windowKey] = (visible, ++_stamp);   // same choice: just freshen it for the cap
            return;
        }
        windows[windowKey] = (visible, ++_stamp);
        Trim(windows);
        _dirty = true;
        _dirtyAtMs = nowMs;
    }

    /// <summary>Called every tick: writes the file once changes have been quiet for about 2 s.</summary>
    public void Tick(long nowMs)
    {
        if (_dirty && nowMs - _dirtyAtMs >= SaveDelayMs) Save();
    }

    /// <summary>Writes pending changes now (logout, shutdown).</summary>
    public void Flush()
    {
        if (_dirty) Save();
    }

    private static void Trim(Dictionary<string, (bool Visible, long Stamp)> windows)
    {
        if (windows.Count <= MaxKeysPerCharacter) return;
        foreach (string key in windows.OrderBy(kv => kv.Value.Stamp).Take(windows.Count - MaxKeysPerCharacter).Select(kv => kv.Key).ToList())
            windows.Remove(key);
    }

    private void Load()
    {
        if (_data != null) return;
        _data = new Dictionary<string, Dictionary<string, (bool, long)>>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path)) return;
            byte[] bytes = File.ReadAllBytes(_path);
            if (bytes.Length == 0) return;
            using JsonDocument doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 8,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (JsonProperty ch in doc.RootElement.EnumerateObject())
            {
                if (ch.Name.Length == 0 || ch.Value.ValueKind != JsonValueKind.Object) continue;
                var windows = new Dictionary<string, (bool, long)>(StringComparer.Ordinal);
                foreach (JsonProperty w in ch.Value.EnumerateObject())
                {
                    if (w.Name.Length == 0) continue;
                    if (w.Value.ValueKind == JsonValueKind.True) windows[w.Name] = (true, ++_stamp);
                    else if (w.Value.ValueKind == JsonValueKind.False) windows[w.Name] = (false, ++_stamp);
                }
                Trim(windows);
                if (windows.Count > 0) _data[ch.Name] = windows;
            }
        }
        catch (Exception ex)
        {
            // A bad file counts as empty; the next save replaces it.
            _data.Clear();
            Fail("read", $"can't read {_path}: {ex.Message}; starting with no remembered hud visibility");
        }
    }

    private void Save()
    {
        _dirty = false;
        if (_data == null) return;
        string tmp = _path + ".tmp";
        try
        {
            using (var buffer = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartObject();
                    foreach (var ch in _data.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    {
                        if (ch.Value.Count == 0) continue;
                        w.WriteStartObject(ch.Key);
                        foreach (var e in ch.Value.OrderBy(kv => kv.Value.Stamp))
                            w.WriteBoolean(e.Key, e.Value.Visible);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                string? dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(tmp, buffer.ToArray());
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Fail("write", $"can't write {_path}: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>At most one log line per failure kind.</summary>
    private void Fail(string kind, string message)
    {
        if (_failuresLogged.Add(kind)) _log("[RynthLua] " + message);
    }
}

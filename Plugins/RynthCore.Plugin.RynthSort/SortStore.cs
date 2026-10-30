using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthSort.Sorting;

namespace RynthCore.Plugin.RynthSort;

/// <summary>RynthSort's settings for one character (the Lua script's DEFAULTS, plus what the plugin adds).</summary>
internal sealed class SortSettings
{
    public const int SettleMin = 0, SettleMax = 30, GapMin = 100, GapMax = 3000;

    /// <summary>Auto-sort: keeps every pack tidy (new items from the main pack after the settle time, misplaced items, stacks, order).</summary>
    public bool Auto = true;
    /// <summary>How long a new item sits in the main pack before it moves (seconds).</summary>
    public int SettleSec = 5;
    /// <summary>Auto-sort's pause between two moves (ms), to leave the bot room.</summary>
    public int GapMs = 300;
    /// <summary>Moves may go while RynthAi fights (never while it waits on the server or animates).</summary>
    public bool SortInCombat;
    /// <summary>Items are put in order inside each pack (A-Z by name), when the engine can read slot orders (API v80).</summary>
    public bool OrderInPacks = true;
    /// <summary>The order inside a pack groups by kind (class) first, then name.</summary>
    public bool OrderKindFirst;
    /// <summary>
    /// The window was open when last seen. Closed for a character RynthSort hasn't seen (it
    /// has a button on the RynthCore bar and /rsort), so a new character or alt doesn't get it
    /// popping up at every login until closed once; open once after taking over the Lua
    /// script's rules, to show them.
    /// </summary>
    public bool WindowOpen;

    public void Clamp()
    {
        SettleSec = Math.Clamp(SettleSec, SettleMin, SettleMax);
        GapMs = Math.Clamp(GapMs, GapMin, GapMax);
    }
}

/// <summary>One character's rules and settings.</summary>
internal sealed class CharacterSortData
{
    public List<SortRule> Rules = SortRules.Defaults();
    public SortSettings Settings = new();
    /// <summary>Where these came from on first run ("" = defaults, else the Lua script's file).</summary>
    public string ImportedFrom = string.Empty;
    /// <summary>
    /// The rules are tied to pack ids (<see cref="PackFix"/> ran once). False for files saved
    /// before pack ids, a new character's defaults and a fresh import from the Lua script.
    /// </summary>
    public bool PacksFixed;
    /// <summary>
    /// The Lua script's rules as its file has them, while <see cref="PacksFixed"/> is false and the
    /// rules came from it: tells untouched imported rules (Lua numbers) from ones picked in
    /// RynthSort's window since. Null when unknown. Not saved.
    /// </summary>
    public List<SortRule>? LuaRules;
}

/// <summary>
/// Per-character files: &lt;SuiteDir&gt;\RynthSort\characters\&lt;server_character&gt;.json, written by
/// hand and read with JsonDocument (plugin types never go through System.Text.Json's serializer,
/// which roots them across hot reloads). The same shape as the Lua script's saved data
/// ({"rules":[...],"settings":{...}}), so its file (&lt;SuiteDir&gt;\RynthLua\Data\RynthSort\characters\...)
/// is read the same way when a character has no RynthSort file yet.
/// </summary>
internal sealed class SortStore
{
    public const int MaxRules = 64;
    public const int MaxPack = 32;

    /// <summary>RynthSort's folder (tests point it at a temp folder).</summary>
    public string Root;
    /// <summary>The Lua script's data folder for RynthSort (tests point it at a temp folder).</summary>
    public string LuaDataRoot;

    public SortStore()
    {
        string suite = RynthCore.Install.RynthInstallPaths.SuiteDir;
        Root = Path.Combine(suite, "RynthSort");
        LuaDataRoot = Path.Combine(suite, "RynthLua", "Data", "RynthSort");
    }

    public SortStore(string root, string luaDataRoot)
    {
        Root = root;
        LuaDataRoot = luaDataRoot;
    }

    /// <summary>"server_character" made safe as a file name, as the Lua script's storage names it.</summary>
    public static string FileStem(string server, string character) => SafeFileName($"{server}_{character}");

    public string PathFor(string server, string character) => Path.Combine(Root, "characters", FileStem(server, character) + ".json");
    public string LuaPathFor(string server, string character) => Path.Combine(LuaDataRoot, "characters", FileStem(server, character) + ".json");

    /// <summary>
    /// The character's rules and settings: RynthSort's own file; else the Lua script's (imported:
    /// <see cref="CharacterSortData.ImportedFrom"/> says so, and the caller saves it as RynthSort's);
    /// else the defaults. A broken file reads as the defaults (the next save rewrites it).
    /// </summary>
    public CharacterSortData Load(string server, string character, out string? problem)
    {
        problem = null;
        string own = PathFor(server, character);
        if (File.Exists(own))
        {
            if (!TryRead(own, out CharacterSortData? d, out problem)) return new CharacterSortData();
            // Imported before pack ids: the Lua script's file says which rules are still its own.
            if (!d!.PacksFixed && d.ImportedFrom.Length > 0 && File.Exists(d.ImportedFrom)
                && TryRead(d.ImportedFrom, out CharacterSortData? luaNow, out _))
                d.LuaRules = luaNow!.Rules;
            return d;
        }
        string lua = LuaPathFor(server, character);
        if (File.Exists(lua) && TryRead(lua, out CharacterSortData? imported, out problem))
        {
            imported!.ImportedFrom = lua;
            imported.PacksFixed = false;           // Lua pack numbers: fixed on the first settled read
            imported.LuaRules = imported.Rules.ConvertAll(r => r.Clone());
            imported.Settings.WindowOpen = true;   // show the rules just taken over (the Lua file has no windowOpen)
            return imported;
        }
        return new CharacterSortData();
    }

    public static bool TryRead(string path, out CharacterSortData? data, out string? problem)
    {
        data = null;
        problem = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            data = Parse(doc.RootElement);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = $"{Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Rules and settings from the file's root object; anything missing or odd keeps its default.</summary>
    internal static CharacterSortData Parse(JsonElement root)
    {
        var d = new CharacterSortData();
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("the file isn't a JSON object");

        if (root.TryGetProperty("rules", out JsonElement rules))
        {
            // The Lua script saved an empty rule list as {} (an empty Lua table): no rules either.
            if (rules.ValueKind == JsonValueKind.Array)
            {
                d.Rules = new List<SortRule>();
                foreach (JsonElement e in rules.EnumerateArray())
                {
                    if (d.Rules.Count >= MaxRules) break;
                    SortRule? r = ParseRule(e);
                    if (r != null) d.Rules.Add(r);
                }
            }
            else if (rules.ValueKind == JsonValueKind.Object && !rules.EnumerateObject().MoveNext())
            {
                d.Rules = new List<SortRule>();
            }
        }

        if (root.TryGetProperty("settings", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
        {
            SortSettings st = d.Settings;
            st.Auto = Bool(s, "auto", st.Auto);
            st.SettleSec = Int(s, "settleSec", st.SettleSec);
            st.GapMs = Int(s, "gapMs", st.GapMs);
            st.SortInCombat = Bool(s, "sortInCombat", st.SortInCombat);
            st.OrderInPacks = Bool(s, "orderInPacks", st.OrderInPacks);
            st.OrderKindFirst = Bool(s, "orderKindFirst", st.OrderKindFirst);
            st.WindowOpen = Bool(s, "windowOpen", st.WindowOpen);
            st.Clamp();
        }
        if (root.TryGetProperty("importedFrom", out JsonElement f) && f.ValueKind == JsonValueKind.String)
            d.ImportedFrom = f.GetString() ?? string.Empty;
        // Missing (a version 1 file, the Lua script's file): rules by number only, not fixed yet.
        d.PacksFixed = Bool(root, "packsFixed", false);
        return d;
    }

    /// <summary>
    /// One rule. Version 3 has "classes" and "names" lists; anything older (versions 1 and 2, and the
    /// Lua script's file) has one test, { kind = "class" | "name", class, text }, which becomes a
    /// rule with that one class or name. Version 3 writes those old fields too (its first entry),
    /// so an older RynthSort reading a newer file still gets a sensible rule.
    /// </summary>
    private static SortRule? ParseRule(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var r = new SortRule
        {
            On = Bool(e, "on", true),
            Pack = Math.Clamp(Int(e, "pack", 1), 1, MaxPack),
            PackId = UInt(e, "packId"),
            PackNote = e.TryGetProperty("packNote", out JsonElement pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() ?? string.Empty : string.Empty,
        };
        bool hasClasses = e.TryGetProperty("classes", out JsonElement cl) && cl.ValueKind == JsonValueKind.Array;
        bool hasNames = e.TryGetProperty("names", out JsonElement nm) && nm.ValueKind == JsonValueKind.Array;
        if (hasClasses || hasNames)
        {
            if (hasClasses)
                foreach (JsonElement c in cl.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out double v) && v >= 0 && v <= 1000) r.AddClass((int)Math.Round(v));
            if (hasNames)
                foreach (JsonElement n in nm.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.String) r.AddName(n.GetString());
        }
        else
        {
            bool byName = e.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String
                          && string.Equals(k.GetString(), "name", StringComparison.OrdinalIgnoreCase);
            if (byName) r.AddName(e.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null);
            else r.AddClass(Int(e, "class", ObjectClasses.SpellComponent));
        }
        if (r.PackNote.Length > 96) r.PackNote = r.PackNote[..96];
        return r;
    }

    /// <summary>
    /// Writes the character's file (temp file, then moved over the old one). <paramref name="backup"/>:
    /// copy the old file to "&lt;file&gt;.bak" first (the one-time pack fix keeps one). Returns an error or null.
    /// </summary>
    public string? Save(string server, string character, CharacterSortData d, bool backup = false)
    {
        string path = PathFor(server, character);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (backup && File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            string tmp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(tmp, ToJson(d), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    internal static string ToJson(CharacterSortData d)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            // 3: a rule holds several classes and names. 2: rules carry packId / packNote, and
            // packsFixed. Versions 1 and 2 still load (one class or name per rule).
            w.WriteNumber("version", 3);
            w.WriteStartArray("rules");
            foreach (SortRule r in d.Rules)
            {
                w.WriteStartObject();
                w.WriteBoolean("on", r.On);
                w.WriteStartArray("classes");
                foreach (int c in r.Classes) w.WriteNumberValue(c);
                w.WriteEndArray();
                w.WriteStartArray("names");
                foreach (string n in r.Names) w.WriteStringValue(n);
                w.WriteEndArray();
                // The first entry the old way, for an older RynthSort reading this file (an empty
                // rule as a blank name rule, which matches nothing there too).
                bool firstIsName = r.Classes.Count == 0;
                w.WriteString("kind", firstIsName ? "name" : "class");
                w.WriteNumber("class", r.Classes.Count > 0 ? r.Classes[0] : ObjectClasses.SpellComponent);
                w.WriteString("text", firstIsName && r.Names.Count > 0 ? r.Names[0] : string.Empty);
                w.WriteNumber("pack", r.Pack);
                if (r.PackId != 0) w.WriteNumber("packId", r.PackId);
                if (r.PackNote.Length > 0) w.WriteString("packNote", r.PackNote);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("settings");
            w.WriteBoolean("auto", d.Settings.Auto);
            w.WriteNumber("settleSec", d.Settings.SettleSec);
            w.WriteNumber("gapMs", d.Settings.GapMs);
            w.WriteBoolean("sortInCombat", d.Settings.SortInCombat);
            w.WriteBoolean("orderInPacks", d.Settings.OrderInPacks);
            w.WriteBoolean("orderKindFirst", d.Settings.OrderKindFirst);
            w.WriteBoolean("windowOpen", d.Settings.WindowOpen);
            w.WriteEndObject();
            if (d.ImportedFrom.Length > 0) w.WriteString("importedFrom", d.ImportedFrom);
            w.WriteBoolean("packsFixed", d.PacksFixed);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static bool Bool(JsonElement o, string name, bool fallback) =>
        o.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;

    /// <summary>An object id (0 when missing, negative or odd).</summary>
    private static uint UInt(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetUInt32(out uint u)) return u;
        return v.TryGetDouble(out double dbl) && dbl >= 0 && dbl <= uint.MaxValue && dbl == Math.Floor(dbl) ? (uint)dbl : 0;
    }

    private static int Int(JsonElement o, string name, int fallback)
    {
        if (!o.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return fallback;
        if (v.TryGetInt32(out int i)) return i;
        // Lua numbers are doubles: 5.0 is 5.
        return v.TryGetDouble(out double dbl) && dbl >= int.MinValue && dbl <= int.MaxValue ? (int)Math.Round(dbl) : fallback;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>RynthLua's LibraryStorage.SafeFileName, so both name a character's file the same way.</summary>
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
}

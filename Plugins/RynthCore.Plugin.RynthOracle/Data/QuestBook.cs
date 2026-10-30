using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// One character's quest notes: the quests tracked in the Quest Timers window and the rewards
/// recorded per quest (UtilityBelt ILT's quest_rewards.json, kept by RynthOracle now). Lives in
/// %APPDATA%\RynthCore\RynthOracle\characters\&lt;world&gt;\&lt;character&gt;.json, written by hand and
/// read with JsonDocument (no reflection serializer: it roots plugin types across hot reloads).
///
/// The first time a character has no file, an existing UtilityBelt quest_rewards.json for the
/// same world and character is imported once (Documents\Decal Plugins\UtilityBelt\&lt;world&gt;\&lt;character&gt;).
/// Pump thread only.
/// </summary>
internal sealed class QuestBook
{
    public const int SchemaVersion = 1;

    private readonly List<string> _tracked = new();
    private readonly HashSet<string> _trackedSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuestReward> _rewards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character the book belongs to ("" = none loaded: changes are not saved).</summary>
    public string Character { get; private set; } = "";
    public string World { get; private set; } = "";
    /// <summary>Bumps on every change (views rebuild their caches on it).</summary>
    public int Revision { get; private set; }
    /// <summary>What happened at load ("imported 4 rewards from UtilityBelt"), shown once in chat; "" = nothing to say.</summary>
    public string LoadNote { get; private set; } = "";

    /// <summary>Overrides the folder (tests).</summary>
    internal string? RootOverride { get; set; }
    /// <summary>Overrides UtilityBelt's folder (tests); "" turns the import off.</summary>
    internal string? UbRootOverride { get; set; }

    public bool Loaded => Character.Length > 0;
    public IReadOnlyList<string> Tracked => _tracked;
    public int RewardCount => _rewards.Count;

    public bool IsTracked(string flag) => _trackedSet.Contains(flag);

    public bool TryGetReward(string flag, out QuestReward reward) => _rewards.TryGetValue(flag, out reward!);

    /// <summary>The recorded rewards' compact text, or "".</summary>
    public string RewardText(string flag) => _rewards.TryGetValue(flag, out QuestReward? r) ? r.ShortSummary() : "";

    private string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "RynthOracle", "characters");

    private string UbRoot => UbRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Decal Plugins", "UtilityBelt");

    /// <summary>The file for a world and character (names made safe for a path).</summary>
    public string PathFor(string world, string character) =>
        Path.Combine(Root, SafeName(world.Length > 0 ? world : "default"), SafeName(character) + ".json");

    /// <summary>Loads (or starts) the book for a character; a no-op when it is already loaded.</summary>
    public void Load(string world, string character)
    {
        world ??= "";
        character = (character ?? "").Trim();
        if (character.Length == 0) return;
        if (string.Equals(character, Character, StringComparison.Ordinal) && string.Equals(world, World, StringComparison.Ordinal)) return;

        ResetData();
        Character = character;
        World = world;
        LoadNote = "";
        string file = PathFor(world, character);
        try
        {
            if (File.Exists(file)) Read(File.ReadAllText(file));
            else if (ImportUb(world, character) is int n and > 0)
            {
                LoadNote = $"Imported {n} quest reward{(n == 1 ? "" : "s")} from UtilityBelt.";
                Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken file means an empty book; the next change rewrites it.
            LoadNote = "Couldn't read the saved quest notes (" + ex.GetType().Name + "); starting empty.";
        }
        Revision++;
    }

    /// <summary>Forgets the character (logout). Changes were saved as they were made.</summary>
    public void Clear()
    {
        ResetData();
        Character = "";
        World = "";
        LoadNote = "";
        Revision++;
    }

    private void ResetData()
    {
        _tracked.Clear();
        _trackedSet.Clear();
        _rewards.Clear();
    }

    /// <summary>Tracks or untracks a quest; returns the new state. Saved at once.</summary>
    public bool SetTracked(string flag, bool tracked)
    {
        if (string.IsNullOrWhiteSpace(flag)) return false;
        if (tracked == _trackedSet.Contains(flag)) return tracked;
        if (tracked)
        {
            _trackedSet.Add(flag);
            _tracked.Add(flag);
        }
        else
        {
            _trackedSet.Remove(flag);
            _tracked.RemoveAll(f => string.Equals(f, flag, StringComparison.OrdinalIgnoreCase));
        }
        Revision++;
        Save();
        return tracked;
    }

    public bool ToggleTracked(string flag) => SetTracked(flag, !IsTracked(flag));

    /// <summary>Stores a quest's rewards (an empty entry removes them). Saved at once.</summary>
    public void SetReward(string flag, QuestReward? reward)
    {
        if (string.IsNullOrWhiteSpace(flag)) return;
        if (reward == null || reward.IsEmpty) _rewards.Remove(flag);
        else _rewards[flag] = reward.Clone();
        Revision++;
        Save();
    }

    /// <summary>Writes the book (atomic replace). Does nothing with no character loaded.</summary>
    public void Save()
    {
        if (!Loaded) return;
        try
        {
            string file = PathFor(World, Character);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Notes are a convenience; a failed write keeps the old file.
        }
    }

    /// <summary>The book as JSON (plain ASCII: anything else is escaped).</summary>
    internal string ToJson()
    {
        var sb = new StringBuilder(1024);
        sb.Append("{\n");
        sb.Append("  \"schemaVersion\": ").Append(SchemaVersion.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("  \"character\": ").Append(Q(Character)).Append(",\n");
        sb.Append("  \"world\": ").Append(Q(World)).Append(",\n");
        sb.Append("  \"savedAtUtc\": ").Append(Q(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",\n");
        sb.Append("  \"tracked\": [");
        for (int i = 0; i < _tracked.Count; i++)
            sb.Append(i > 0 ? ", " : "").Append(Q(_tracked[i]));
        sb.Append("],\n");
        sb.Append("  \"rewards\": {");
        bool first = true;
        var keys = new List<string>(_rewards.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string key in keys)
        {
            QuestReward r = _rewards[key];
            sb.Append(first ? "\n" : ",\n");
            first = false;
            sb.Append("    ").Append(Q(key)).Append(": {");
            sb.Append("\"xpPercent\": ").Append(r.XpPercent.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(", \"lumPercent\": ").Append(r.LumPercent.ToString("R", CultureInfo.InvariantCulture));
            sb.Append(", \"recordedLevel\": ").Append(r.RecordedLevel.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"coins\": ").Append(r.Coins.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"trinkets\": ").Append(r.Trinkets.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"medallions\": ").Append(r.Medallions.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"yellowPowder\": ").Append(r.YellowPowder.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"redPowder\": ").Append(r.RedPowder.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"bluePowder\": ").Append(r.BluePowder.ToString(CultureInfo.InvariantCulture));
            sb.Append(", \"mmd\": ").Append(r.Mmd.ToString(CultureInfo.InvariantCulture));
            sb.Append('}');
        }
        sb.Append(first ? "}\n" : "\n  }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>Reads a saved book (RynthOracle's own lower-case keys, or UB's upper-case ones).</summary>
    internal void Read(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (Prop(root, "tracked", out JsonElement tracked) && tracked.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement t in tracked.EnumerateArray())
            {
                string flag = t.ValueKind == JsonValueKind.String ? (t.GetString() ?? "").Trim() : "";
                if (flag.Length > 0 && _trackedSet.Add(flag)) _tracked.Add(flag);
            }
        }
        ReadRewards(root);
    }

    /// <summary>The "rewards" / "Rewards" object: flag -> entry. Returns how many were kept.</summary>
    private int ReadRewards(JsonElement root)
    {
        if (!Prop(root, "rewards", out JsonElement rewards) || rewards.ValueKind != JsonValueKind.Object) return 0;
        int n = 0;
        foreach (JsonProperty p in rewards.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object || p.Name.Trim().Length == 0) continue;
            JsonElement e = p.Value;
            var r = new QuestReward
            {
                XpPercent = Math.Clamp(Dbl(e, "xpPercent"), 0, QuestReward.MaxPercent),
                LumPercent = Math.Clamp(Dbl(e, "lumPercent"), 0, QuestReward.MaxPercent),
                RecordedLevel = Int(e, "recordedLevel"),
                Coins = Int(e, "coins"),
                Trinkets = Int(e, "trinkets"),
                Medallions = Int(e, "medallions"),
                YellowPowder = Int(e, "yellowPowder"),
                RedPowder = Int(e, "redPowder"),
                BluePowder = Int(e, "bluePowder"),
                Mmd = Int(e, "mmd"),
            };
            if (r.IsEmpty) continue;
            _rewards[p.Name.Trim()] = r;
            n++;
        }
        return n;
    }

    /// <summary>UtilityBelt ILT's quest_rewards.json for this world and character, if there is one. Returns the entries kept.</summary>
    private int ImportUb(string world, string character)
    {
        string ubRoot = UbRoot;
        if (ubRoot.Length == 0 || world.Length == 0) return 0;
        // UB names the server folder after the world with its spaces removed ("InfiniteLeaftide").
        foreach (string w in new[] { world, world.Replace(" ", "") })
        {
            string file = Path.Combine(ubRoot, SafeName(w), SafeName(character), "quest_rewards.json");
            if (!File.Exists(file)) continue;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object ? ReadRewards(doc.RootElement) : 0;
        }
        return 0;
    }

    // ── JSON helpers (keys matched without regard to case: UB writes "XpPercent") ──────────────

    private static bool Prop(JsonElement o, string name, out JsonElement value)
    {
        foreach (JsonProperty p in o.EnumerateObject())
        {
            if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = p.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static double Dbl(JsonElement o, string name) =>
        Prop(o, name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && double.IsFinite(d) ? d : 0;

    private static int Int(JsonElement o, string name)
    {
        if (!Prop(o, name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt64(out long l)) return (int)Math.Clamp(l, 0, int.MaxValue);
        return v.TryGetDouble(out double d) && double.IsFinite(d) ? (int)Math.Clamp(d, 0, int.MaxValue) : 0;
    }

    /// <summary>A JSON string literal, ASCII only.</summary>
    private static string Q(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A name safe as one path segment (invalid characters become '_').</summary>
    internal static string SafeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        char[] bad = Path.GetInvalidFileNameChars();
        foreach (char c in name.Trim()) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
        string s = sb.ToString().Trim('.', ' ');
        return s.Length > 0 ? s : "_";
    }
}

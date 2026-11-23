// ============================================================================
//  RynthAi - Loot/LootPerDayStore.cs
//  "Keep # per day" (2026-11-22, RynthCore.Loot.LootPerDay): what this
//  character picked up for each such rule, with the time of each pickup, so a
//  rule takes at most N in any rolling window (24 h by default). Counted by
//  pickups, not by the pack: treasure maps and the like get used or sold.
//
//  One file per character and server, beside the settings profiles:
//  SettingsProfiles\<server>\<character>\loot_per_day.json
//    {"version":1,"rules":{"Treasure Maps":[{"at":"2026-11-22T10:00:00Z","n":1}]}}
//  Times are UTC (no jump when the clocks change). Pickups older than a week
//  (the longest window) are dropped on load and on every pickup.
//
//  Pure (no host): the host tests drive it with their own clock.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RynthCore.Loot;

namespace RynthCore.Plugin.RynthAi.Loot;

internal sealed class LootPerDayStore
{
    public const string FileName = "loot_per_day.json";
    public const int FileVersion = 1;

    private readonly Dictionary<string, List<Pick>> _rules = new(StringComparer.OrdinalIgnoreCase);

    internal readonly record struct Pick(DateTime AtUtc, int Units);

    /// <summary>The file (empty: kept in memory only, nothing saved).</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>Grows on every change (the Loot Editor's revision follows it).</summary>
    public long Revision { get; private set; }

    /// <summary>The last load or save problem, or empty.</summary>
    public string Problem { get; private set; } = string.Empty;

    /// <summary>
    /// Reads <paramref name="path"/> (empty: memory only). A missing file is no pickups; a
    /// broken one is no pickups too, with <see cref="Problem"/> set (and kept until the next
    /// pickup overwrites it).
    /// </summary>
    public void Load(string path, DateTime nowUtc)
    {
        Path = path ?? string.Empty;
        _rules.Clear();
        Problem = string.Empty;
        Revision++;
        if (Path.Length == 0 || !File.Exists(Path)) return;
        try
        {
            Parse(File.ReadAllText(Path));
            Prune(nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            _rules.Clear();
            Problem = $"{Path}: {ex.Message}";
        }
    }

    /// <summary>A pickup of <paramref name="units"/> (at least 1) for the rule <paramref name="key"/>, saved at once.</summary>
    public void Record(string key, int units, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (!_rules.TryGetValue(key, out List<Pick>? list))
            _rules[key] = list = new List<Pick>();
        list.Add(new Pick(nowUtc, Math.Max(1, units)));
        Prune(nowUtc);
        Revision++;
        Save();
    }

    /// <summary>Units picked up for <paramref name="key"/> in the last <paramref name="hours"/>.</summary>
    public int Count(string key, int hours, DateTime nowUtc)
    {
        if (!_rules.TryGetValue(key, out List<Pick>? list)) return 0;
        DateTime from = nowUtc - TimeSpan.FromHours(hours);
        int n = 0;
        foreach (Pick p in list)
            if (p.AtUtc > from) n += p.Units;
        return n;
    }

    /// <summary>
    /// How long until the count is under <paramref name="cap"/> again (zero when it is now); under
    /// the cap, how long until the oldest pickup in the window frees its place (null: none in it).
    /// </summary>
    public TimeSpan? NextFree(string key, int hours, int cap, DateTime nowUtc)
    {
        if (!_rules.TryGetValue(key, out List<Pick>? list)) return null;
        TimeSpan window = TimeSpan.FromHours(hours);
        var inWindow = new List<Pick>();
        foreach (Pick p in list)
            if (p.AtUtc > nowUtc - window) inWindow.Add(p);
        if (inWindow.Count == 0) return null;
        inWindow.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
        int total = 0;
        foreach (Pick p in inWindow) total += p.Units;
        if (total < cap)
            return Max0(inWindow[0].AtUtc + window - nowUtc);
        // Drop the oldest until the rest is under the cap: that pickup's expiry is when.
        int left = total;
        foreach (Pick p in inWindow)
        {
            left -= p.Units;
            if (left < cap) return Max0(p.AtUtc + window - nowUtc);
        }
        return TimeSpan.Zero;
    }

    private static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    /// <summary>Forgets the pickups of <paramref name="key"/> (null: of every rule). True when there were any.</summary>
    public bool Clear(string? key)
    {
        bool had = key == null ? _rules.Count > 0 : _rules.Remove(key);
        if (key == null) _rules.Clear();
        if (!had) return false;
        Revision++;
        Save();
        return true;
    }

    /// <summary>Every rule with pickups on record, with its pickups (oldest first).</summary>
    public IEnumerable<(string Key, IReadOnlyList<Pick> Picks)> Rules()
    {
        var keys = new List<string>(_rules.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string k in keys)
        {
            var list = new List<Pick>(_rules[k]);
            list.Sort((a, b) => a.AtUtc.CompareTo(b.AtUtc));
            yield return (k, list);
        }
    }

    /// <summary>"1 of 2 picked up in the last 24 h, next frees in 5 h 10 min".</summary>
    public string Status(string key, int hours, int cap, DateTime nowUtc)
    {
        int have = Count(key, hours, nowUtc);
        string s = $"{have} of {cap} picked up in the last {LootPerDay.WindowText(hours)}";
        TimeSpan? next = NextFree(key, hours, cap, nowUtc);
        if (have > 0 && next is TimeSpan t) s += (have >= cap ? ", none until " : ", next frees in ") + Duration(t);
        return s;
    }

    /// <summary>"5 h 10 min", "45 min", "under a minute".</summary>
    public static string Duration(TimeSpan t)
    {
        long minutes = (long)Math.Ceiling(t.TotalMinutes);
        if (minutes <= 0) return "under a minute";
        long h = minutes / 60, m = minutes % 60;
        if (h == 0) return m.ToString(CultureInfo.InvariantCulture) + " min";
        return h.ToString(CultureInfo.InvariantCulture) + " h" + (m > 0 ? " " + m.ToString(CultureInfo.InvariantCulture) + " min" : string.Empty);
    }

    private void Prune(DateTime nowUtc)
    {
        DateTime from = nowUtc - TimeSpan.FromHours(LootPerDay.MaxHours);
        var empty = new List<string>();
        foreach (var kv in _rules)
        {
            kv.Value.RemoveAll(p => p.AtUtc <= from);
            if (kv.Value.Count == 0) empty.Add(kv.Key);
        }
        foreach (string k in empty) _rules.Remove(k);
    }

    // ── File ──────────────────────────────────────────────────────────────────

    private void Save()
    {
        if (Path.Length == 0) return;
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, Serialize());
            File.Move(tmp, Path, overwrite: true);
            Problem = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = $"{Path}: {ex.Message}";
        }
    }

    internal string Serialize()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", FileVersion);
            w.WriteStartObject("rules");
            foreach ((string key, IReadOnlyList<Pick> picks) in Rules())
            {
                w.WriteStartArray(key);
                foreach (Pick p in picks)
                {
                    w.WriteStartObject();
                    w.WriteString("at", p.AtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                    w.WriteNumber("n", p.Units);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("rules", out JsonElement rules) || rules.ValueKind != JsonValueKind.Object)
            return;
        foreach (JsonProperty rule in rules.EnumerateObject())
        {
            if (rule.Value.ValueKind != JsonValueKind.Array || string.IsNullOrWhiteSpace(rule.Name)) continue;
            var list = new List<Pick>();
            foreach (JsonElement e in rule.Value.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("at", out JsonElement at) || at.ValueKind != JsonValueKind.String
                    || !DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                           DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when))
                    continue;
                int n = e.TryGetProperty("n", out JsonElement ne) && ne.ValueKind == JsonValueKind.Number && ne.TryGetInt32(out int v) ? v : 1;
                list.Add(new Pick(DateTime.SpecifyKind(when, DateTimeKind.Utc), Math.Max(1, n)));
            }
            if (list.Count == 0) continue;
            if (_rules.TryGetValue(rule.Name, out List<Pick>? have)) have.AddRange(list);   // same name, other case
            else _rules[rule.Name] = list;
        }
    }
}

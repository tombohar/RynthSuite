using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthInventory.Data;

namespace RynthCore.Plugin.RynthInventory;

/// <summary>
/// RynthInventory's settings, in %APPDATA%\RynthCore\rynthinventory.json. Written by hand and
/// read with JsonDocument (plugin types never go through System.Text.Json's serializer, which
/// roots them across hot reloads). Shared by every client; each re-reads it at login.
/// </summary>
internal sealed class InventorySettings
{
    public bool MainWindowOpen;
    /// <summary>A snapshot older than this many days is shown as stale.</summary>
    public int StaleDays = 7;
    /// <summary>An opened container whose name holds one of these words is recorded as storage (house storage always is).</summary>
    public string StorageWords = "storage, vault, bank, locker, stash";
    /// <summary>Containers recorded with /ginv keep: "server|objectId".</summary>
    public List<string> KeptStorage = new();
    public bool SearchSpells;
    public bool IncludeStorage = true;
    public bool IncludeEquipped = true;
    public bool GroupByName;
    public int Sort;
    public bool Descending;
    public bool ShowIcons = true;

    /// <summary>Tests point this at a scratch file so they never touch the real settings.</summary>
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", "rynthinventory.json");

    public string[] StorageWordList()
    {
        var list = new List<string>();
        foreach (string w in StorageWords.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = w.Trim().ToLowerInvariant();
            if (t.Length > 0) list.Add(t);
        }
        return list.ToArray();
    }

    public bool IsKept(string server, uint id) => KeptStorage.Contains(KeepKey(server, id));

    public static string KeepKey(string server, uint id) =>
        server.Trim().ToLowerInvariant() + "|" + id.ToString(CultureInfo.InvariantCulture);

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return;
            MainWindowOpen = Bool(r, "mainWindowOpen", MainWindowOpen);
            StaleDays = Math.Clamp(Int(r, "staleDays", StaleDays), 1, 3650);
            if (r.TryGetProperty("storageWords", out JsonElement sw) && sw.ValueKind == JsonValueKind.String)
                StorageWords = sw.GetString() ?? StorageWords;
            if (r.TryGetProperty("keptStorage", out JsonElement ks) && ks.ValueKind == JsonValueKind.Array)
            {
                KeptStorage.Clear();
                foreach (JsonElement e in ks.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is string k && k.Length > 0) KeptStorage.Add(k);
            }
            SearchSpells = Bool(r, "searchSpells", SearchSpells);
            IncludeStorage = Bool(r, "includeStorage", IncludeStorage);
            IncludeEquipped = Bool(r, "includeEquipped", IncludeEquipped);
            GroupByName = Bool(r, "groupByName", GroupByName);
            Sort = Int(r, "sort", Sort);
            Descending = Bool(r, "descending", Descending);
            ShowIcons = Bool(r, "showIcons", ShowIcons);
        }
        catch
        {
            // A broken file means defaults; the next save rewrites it.
        }
    }

    public void Save()
    {
        try
        {
            // Every client shares this file: keep containers another client added with /ginv keep.
            try
            {
                if (File.Exists(FilePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("keptStorage", out JsonElement ks) && ks.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement e in ks.EnumerateArray())
                            if (e.ValueKind == JsonValueKind.String && e.GetString() is string k && k.Length > 0 && !KeptStorage.Contains(k))
                                KeptStorage.Add(k);
                }
            }
            catch { }

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"mainWindowOpen\": ").Append(B(MainWindowOpen)).Append(",\n");
            sb.Append("  \"staleDays\": ").Append(StaleDays.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"storageWords\": "); InventoryJson.Str(sb, StorageWords); sb.Append(",\n");
            sb.Append("  \"keptStorage\": [");
            for (int i = 0; i < KeptStorage.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                InventoryJson.Str(sb, KeptStorage[i]);
            }
            sb.Append("],\n");
            sb.Append("  \"searchSpells\": ").Append(B(SearchSpells)).Append(",\n");
            sb.Append("  \"includeStorage\": ").Append(B(IncludeStorage)).Append(",\n");
            sb.Append("  \"includeEquipped\": ").Append(B(IncludeEquipped)).Append(",\n");
            sb.Append("  \"groupByName\": ").Append(B(GroupByName)).Append(",\n");
            sb.Append("  \"sort\": ").Append(Sort.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            sb.Append("  \"descending\": ").Append(B(Descending)).Append(",\n");
            sb.Append("  \"showIcons\": ").Append(B(ShowIcons)).Append('\n');
            sb.Append("}\n");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Settings are a convenience; a failed write keeps the old file.
        }
    }

    private static string B(bool v) => v ? "true" : "false";

    private static bool Bool(JsonElement r, string name, bool fallback) =>
        r.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;

    private static int Int(JsonElement r, string name, int fallback) =>
        r.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;
}

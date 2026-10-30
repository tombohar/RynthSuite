using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthInventory.Data;

/// <summary>
/// The file format (see the plugin's report for the field list). Written by hand, one item
/// per line so a file stays readable and diffable; read with JsonDocument. Never through
/// System.Text.Json's serializer: plugin types must not reach it (it roots them across hot
/// reloads). Unknown fields are ignored and missing ones keep their defaults, so newer and
/// older plugins read each other's files.
/// </summary>
internal static class InventoryJson
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ── Write ───────────────────────────────────────────────────────────────

    public static string Write(CharacterSnapshot s)
    {
        var sb = new StringBuilder(64 * 1024);
        sb.Append("{\n");
        sb.Append("  \"format\": \"").Append(CharacterSnapshot.FormatTag).Append("\",\n");
        sb.Append("  \"version\": ").Append(CharacterSnapshot.CurrentFormat.ToString(Inv)).Append(",\n");
        sb.Append("  \"server\": "); Str(sb, s.Server); sb.Append(",\n");
        sb.Append("  \"account\": "); Str(sb, s.Account); sb.Append(",\n");
        sb.Append("  \"character\": "); Str(sb, s.Character); sb.Append(",\n");
        sb.Append("  \"characterId\": ").Append(s.CharacterId.ToString(Inv)).Append(",\n");
        sb.Append("  \"scannedUtc\": "); Time(sb, s.ScannedUtc); sb.Append(",\n");
        sb.Append("  \"plugin\": "); Str(sb, s.PluginVersion); sb.Append(",\n");
        sb.Append("  \"items\": [");
        ItemList(sb, s.Items, "    ");
        sb.Append("],\n");
        sb.Append("  \"storage\": [");
        for (int i = 0; i < s.Storage.Count; i++)
        {
            StorageRecord st = s.Storage[i];
            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append("    {\"id\": ").Append(st.Id.ToString(Inv));
            sb.Append(", \"name\": "); Str(sb, st.Name);
            sb.Append(", \"label\": "); Str(sb, st.Label);
            sb.Append(", \"cell\": ").Append(st.Cell.ToString(Inv));
            sb.Append(", \"seenUtc\": "); Time(sb, st.SeenUtc);
            sb.Append(", \"items\": [");
            ItemList(sb, st.Items, "      ");
            sb.Append("]}");
        }
        if (s.Storage.Count > 0) sb.Append("\n  ");
        sb.Append("]\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static void ItemList(StringBuilder sb, List<ItemRecord> items, string indent)
    {
        for (int i = 0; i < items.Count; i++)
        {
            sb.Append(i == 0 ? "\n" : ",\n").Append(indent);
            Item(sb, items[i]);
        }
        if (items.Count > 0) sb.Append('\n').Append(indent, 0, indent.Length - 2);
    }

    private static void Item(StringBuilder sb, ItemRecord it)
    {
        sb.Append("{\"id\": ").Append(it.Id.ToString(Inv));
        sb.Append(", \"name\": "); Str(sb, it.Name);
        if (it.Wcid != 0) sb.Append(", \"wcid\": ").Append(it.Wcid.ToString(Inv));
        sb.Append(", \"icon\": ").Append(it.Icon.ToString(Inv));
        if (it.Underlay != 0) sb.Append(", \"underlay\": ").Append(it.Underlay.ToString(Inv));
        if (it.Overlay != 0) sb.Append(", \"overlay\": ").Append(it.Overlay.ToString(Inv));
        sb.Append(", \"stack\": ").Append(it.Stack.ToString(Inv));
        if (it.MaxStack != 0) sb.Append(", \"maxStack\": ").Append(it.MaxStack.ToString(Inv));
        sb.Append(", \"type\": ").Append(it.ItemType.ToString(Inv));
        sb.Append(", \"category\": "); Str(sb, it.Category);
        sb.Append(", \"place\": "); Str(sb, it.Place);
        sb.Append(", \"container\": ").Append(it.ContainerId.ToString(Inv));
        sb.Append(", \"path\": [");
        for (int i = 0; i < it.Path.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            Str(sb, it.Path[i]);
        }
        sb.Append(']');
        if (it.Slot != 0) sb.Append(", \"slot\": ").Append(it.Slot.ToString(Inv));
        if (it.Material != 0) sb.Append(", \"material\": ").Append(it.Material.ToString(Inv));
        if (it.Workmanship != 0) sb.Append(", \"workmanship\": ").Append(it.Workmanship.ToString(Inv));
        sb.Append(", \"value\": ").Append(it.Value.ToString(Inv));
        sb.Append(", \"burden\": ").Append(it.Burden.ToString(Inv));
        if (it.ArmorLevel != 0) sb.Append(", \"armor\": ").Append(it.ArmorLevel.ToString(Inv));
        if (it.MaxUses != 0)
        {
            sb.Append(", \"uses\": ").Append(it.Uses.ToString(Inv));
            sb.Append(", \"maxUses\": ").Append(it.MaxUses.ToString(Inv));
        }
        if (it.SetId != 0) sb.Append(", \"set\": ").Append(it.SetId.ToString(Inv));
        if (it.Spells.Length > 0)
        {
            sb.Append(", \"spells\": [");
            for (int i = 0; i < it.Spells.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(it.Spells[i].ToString(Inv));
            }
            sb.Append(']');
            if (it.SpellNames.Length == it.Spells.Length)
            {
                sb.Append(", \"spellNames\": [");
                for (int i = 0; i < it.SpellNames.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Str(sb, it.SpellNames[i]);
                }
                sb.Append(']');
            }
        }
        if (it.Identified) sb.Append(", \"identified\": true");
        sb.Append('}');
    }

    private static void Time(StringBuilder sb, DateTime utc)
    {
        sb.Append('"');
        if (utc != default)
            sb.Append(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", Inv));
        sb.Append('"');
    }

    /// <summary>A JSON string literal (quotes included), escaping what JSON requires.</summary>
    public static void Str(StringBuilder sb, string? s)
    {
        sb.Append('"');
        if (s != null)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
        sb.Append('"');
    }

    // ── Read ────────────────────────────────────────────────────────────────

    /// <summary>Parses a file's text. Null (with a reason) for anything that isn't one of ours.</summary>
    public static CharacterSnapshot? Read(string json, out string? error)
    {
        error = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || GetString(r, "format") != CharacterSnapshot.FormatTag)
            {
                error = "not a RynthInventory file";
                return null;
            }
            var s = new CharacterSnapshot
            {
                Format = GetInt(r, "version", CharacterSnapshot.CurrentFormat),
                Server = GetString(r, "server"),
                Account = GetString(r, "account"),
                Character = GetString(r, "character"),
                CharacterId = GetUInt(r, "characterId"),
                ScannedUtc = GetTime(r, "scannedUtc"),
                PluginVersion = GetString(r, "plugin"),
            };
            if (string.IsNullOrWhiteSpace(s.Character) || string.IsNullOrWhiteSpace(s.Server))
            {
                error = "no character or server name";
                return null;
            }
            if (r.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                foreach (JsonElement e in items.EnumerateArray())
                    if (ReadItem(e) is ItemRecord it) s.Items.Add(it);
            if (r.TryGetProperty("storage", out JsonElement storage) && storage.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in storage.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var st = new StorageRecord
                    {
                        Id = GetUInt(e, "id"),
                        Name = GetString(e, "name"),
                        Label = GetString(e, "label"),
                        Cell = GetUInt(e, "cell"),
                        SeenUtc = GetTime(e, "seenUtc"),
                    };
                    if (e.TryGetProperty("items", out JsonElement si) && si.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement ie in si.EnumerateArray())
                            if (ReadItem(ie) is ItemRecord it) st.Items.Add(it);
                    if (st.Id != 0) s.Storage.Add(st);
                }
            }
            return s;
        }
        catch (JsonException ex)
        {
            error = "broken JSON: " + ex.Message;
            return null;
        }
    }

    private static ItemRecord? ReadItem(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var it = new ItemRecord
        {
            Id = GetUInt(e, "id"),
            Name = GetString(e, "name"),
            Wcid = GetUInt(e, "wcid"),
            Icon = GetUInt(e, "icon"),
            Underlay = GetUInt(e, "underlay"),
            Overlay = GetUInt(e, "overlay"),
            Stack = Math.Max(1, GetInt(e, "stack", 1)),
            MaxStack = GetInt(e, "maxStack", 0),
            ItemType = GetUInt(e, "type"),
            Category = GetString(e, "category"),
            Place = GetString(e, "place"),
            ContainerId = GetUInt(e, "container"),
            Slot = GetUInt(e, "slot"),
            Material = GetInt(e, "material", 0),
            Workmanship = GetInt(e, "workmanship", 0),
            Value = GetInt(e, "value", 0),
            Burden = GetInt(e, "burden", 0),
            ArmorLevel = GetInt(e, "armor", 0),
            Uses = GetInt(e, "uses", 0),
            MaxUses = GetInt(e, "maxUses", 0),
            SetId = GetInt(e, "set", 0),
            Identified = e.TryGetProperty("identified", out JsonElement idf) && idf.ValueKind == JsonValueKind.True,
        };
        if (string.IsNullOrEmpty(it.Name)) return null;
        if (string.IsNullOrEmpty(it.Category)) it.Category = ItemCategory.FromItemType(it.ItemType);
        if (string.IsNullOrEmpty(it.Place)) it.Place = ItemPlace.Pack;
        it.Path = GetStrings(e, "path");
        if (e.TryGetProperty("spells", out JsonElement sp) && sp.ValueKind == JsonValueKind.Array)
        {
            var list = new List<uint>();
            foreach (JsonElement x in sp.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Number && x.TryGetUInt32(out uint id)) list.Add(id);
            it.Spells = list.ToArray();
        }
        string[] names = GetStrings(e, "spellNames");
        it.SpellNames = names.Length == it.Spells.Length ? names : Array.Empty<string>();
        return it;
    }

    private static string GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int GetInt(JsonElement e, string name, int fallback)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return fallback;
        if (v.TryGetInt32(out int i)) return i;
        if (v.TryGetInt64(out long l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        return fallback;
    }

    private static uint GetUInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetUInt32(out uint u)) return u;
        if (v.TryGetInt32(out int i)) return unchecked((uint)i);   // tolerate ids written signed
        return 0;
    }

    private static DateTime GetTime(JsonElement e, string name)
    {
        string s = GetString(e, name);
        return s.Length > 0 && DateTime.TryParse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : default;
    }

    private static string[] GetStrings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (JsonElement x in v.EnumerateArray())
            if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString() ?? string.Empty);
        return list.ToArray();
    }
}

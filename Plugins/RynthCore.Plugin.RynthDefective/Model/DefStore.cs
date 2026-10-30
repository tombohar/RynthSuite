using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthDefective.Model;

/// <summary>
/// Per-character files: &lt;SuiteDir&gt;\RynthDefective\characters\&lt;server_character&gt;.json, written by
/// hand and read with JsonDocument (plugin types never go through System.Text.Json's serializer,
/// which roots them across hot reloads). A broken or odd file reads as defaults for whatever is
/// wrong in it; the next save rewrites it.
/// </summary>
internal sealed class DefStore
{
    /// <summary>RynthDefective's folder (tests point it at a temp folder).</summary>
    public string Root;

    public DefStore()
    {
        Root = Path.Combine(RynthCore.Install.RynthInstallPaths.SuiteDir, "RynthDefective");
    }

    public DefStore(string root) => Root = root;

    /// <summary>"server_character" made safe as a file name (the same rule RynthSort and RynthLua use).</summary>
    public static string FileStem(string server, string character) => SafeFileName($"{server}_{character}");

    public string PathFor(string server, string character) => Path.Combine(Root, "characters", FileStem(server, character) + ".json");

    /// <summary>The character's data; defaults for a new character or a broken file (<paramref name="problem"/> says which).</summary>
    public DefData Load(string server, string character, out string? problem)
    {
        problem = null;
        string path = PathFor(server, character);
        if (!File.Exists(path)) return new DefData();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            problem = $"{Path.GetFileName(path)}: {ex.Message}";
            return new DefData();
        }
    }

    internal static DefData Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("the file isn't a JSON object");
        var d = new DefData();

        if (root.TryGetProperty("settings", out JsonElement s) && s.ValueKind == JsonValueKind.Object)
        {
            DefSettings st = d.Settings;
            st.SlotCount = Int(s, "slotCount", st.SlotCount);
            st.BarSlots = Int(s, "barSlots", st.BarSlots);
            st.SlotsPerRow = Int(s, "slotsPerRow", st.SlotsPerRow);
            st.ButtonSize = Int(s, "buttonSize", st.ButtonSize);
            st.PickerIconSize = Int(s, "pickerIconSize", st.PickerIconSize);
            st.ShowIcons = Bool(s, "showIcons", st.ShowIcons);
            st.ShowCounts = Bool(s, "showCounts", st.ShowCounts);
            st.PreferredKind = (KindFilter)Int(s, "preferredKind", (int)st.PreferredKind);
            st.SwapWhileBotRuns = Bool(s, "swapWhileBotRuns", st.SwapWhileBotRuns);
            st.WeaponsButtonOnBar = Bool(s, "weaponsButtonOnBar", st.WeaponsButtonOnBar);   // missing (a file from before 2026-10-29): off
            st.IndependentBar = Bool(s, "independentBar", st.IndependentBar);   // missing (a file from before 2026-10-27): off
            st.BarOpen = Bool(s, "barOpen", st.BarOpen);
            st.PickerOpen = Bool(s, "pickerOpen", st.PickerOpen);
            st.Clamp();
        }

        if (root.TryGetProperty("slots", out JsonElement slots) && slots.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (JsonElement e in slots.EnumerateArray())
            {
                if (i >= DefSettings.SlotsMax) break;
                DefSlot slot = d.Slots[i++];
                if (e.ValueKind != JsonValueKind.Object) continue;
                slot.Item = ParseRef(e, "item");
                int mode = Int(e, "mode", 0);
                slot.Mode = mode >= 0 && mode <= (int)UseMode.Equip ? (UseMode)mode : UseMode.Auto;
                // A spell (2026-10-28; a file from before has none): only in a slot without an item.
                if (slot.Item == null && e.TryGetProperty("spell", out JsonElement sp) && sp.ValueKind == JsonValueKind.Object)
                {
                    slot.SpellId = UInt(sp, "id");
                    slot.SpellName = slot.SpellId != 0 ? Str(sp, "name") : string.Empty;
                }
            }
        }
        ParseRefs(root, "favourites", d.Favourites, DefData.MaxFavourites);
        ParseRefs(root, "recent", d.Recent, DefData.MaxRecent);
        return d;
    }

    private static void ParseRefs(JsonElement root, string name, List<ItemRef> into, int max)
    {
        if (!root.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (into.Count >= max) break;
            ItemRef? r = ParseRefObject(e);
            if (r != null) into.Add(r);
        }
    }

    private static ItemRef? ParseRef(JsonElement owner, string name) =>
        owner.TryGetProperty(name, out JsonElement e) ? ParseRefObject(e) : null;

    private static ItemRef? ParseRefObject(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string name = Str(e, "name");
        if (name.Length == 0) return null;   // an item with no name can't be found again
        return new ItemRef
        {
            Id = UInt(e, "id"),
            Name = name,
            Wcid = UInt(e, "wcid"),
            Material = Int(e, "material", 0),
            Icon = UInt(e, "icon"),
            Underlay = UInt(e, "underlay"),
            Overlay = UInt(e, "overlay"),
        };
    }

    /// <summary>Writes the character's file (temp file, then moved over the old one). Returns an error or null.</summary>
    public string? Save(string server, string character, DefData d)
    {
        string path = PathFor(server, character);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    internal static string ToJson(DefData d)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteStartObject("settings");
            DefSettings s = d.Settings;
            w.WriteNumber("slotCount", s.SlotCount);
            w.WriteNumber("barSlots", s.BarSlots);
            w.WriteNumber("slotsPerRow", s.SlotsPerRow);
            w.WriteNumber("buttonSize", s.ButtonSize);
            w.WriteNumber("pickerIconSize", s.PickerIconSize);
            w.WriteBoolean("showIcons", s.ShowIcons);
            w.WriteBoolean("showCounts", s.ShowCounts);
            w.WriteNumber("preferredKind", (int)s.PreferredKind);
            w.WriteBoolean("swapWhileBotRuns", s.SwapWhileBotRuns);
            w.WriteBoolean("weaponsButtonOnBar", s.WeaponsButtonOnBar);
            w.WriteBoolean("independentBar", s.IndependentBar);
            w.WriteBoolean("barOpen", s.BarOpen);
            w.WriteBoolean("pickerOpen", s.PickerOpen);
            w.WriteEndObject();

            w.WriteStartArray("slots");
            foreach (DefSlot slot in d.Slots)
            {
                w.WriteStartObject();
                if (slot.Item != null) WriteRef(w, "item", slot.Item);
                else if (slot.SpellId != 0)
                {
                    w.WriteStartObject("spell");
                    w.WriteNumber("id", slot.SpellId);
                    w.WriteString("name", slot.SpellLabel);
                    w.WriteEndObject();
                }
                w.WriteNumber("mode", (int)slot.Mode);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("favourites");
            foreach (ItemRef r in d.Favourites) WriteRefValue(w, r);
            w.WriteEndArray();
            w.WriteStartArray("recent");
            foreach (ItemRef r in d.Recent) WriteRefValue(w, r);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WriteRef(Utf8JsonWriter w, string name, ItemRef r)
    {
        w.WritePropertyName(name);
        WriteRefValue(w, r);
    }

    private static void WriteRefValue(Utf8JsonWriter w, ItemRef r)
    {
        w.WriteStartObject();
        w.WriteNumber("id", r.Id);
        w.WriteString("name", r.Name);
        if (r.Wcid != 0) w.WriteNumber("wcid", r.Wcid);
        if (r.Material != 0) w.WriteNumber("material", r.Material);
        if (r.Icon != 0) w.WriteNumber("icon", r.Icon);
        if (r.Underlay != 0) w.WriteNumber("underlay", r.Underlay);
        if (r.Overlay != 0) w.WriteNumber("overlay", r.Overlay);
        w.WriteEndObject();
    }

    private static bool Bool(JsonElement o, string name, bool fallback) =>
        o.TryGetProperty(name, out JsonElement v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? string.Empty).Trim() : string.Empty;

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
        return v.TryGetDouble(out double dbl) && dbl >= int.MinValue && dbl <= int.MaxValue ? (int)Math.Round(dbl) : fallback;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>RynthLua's LibraryStorage.SafeFileName, so the suite names a character's file one way.</summary>
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

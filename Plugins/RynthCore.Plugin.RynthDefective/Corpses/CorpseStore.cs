// ============================================================================
//  RynthDefective - Corpses/CorpseStore.cs
//  The corpses, one file per character: <SuiteDir>\RynthDefective\corpses\
//  <server_character>.json. Each client writes only its own character's file
//  and reads the others' (the window lists every character's corpses on this
//  PC), so clients never write the same file. Written by hand and read with
//  JsonDocument, as DefStore does; a broken file reads as no corpses.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RynthCore.Plugin.RynthDefective.Model;

namespace RynthCore.Plugin.RynthDefective.Corpses;

/// <summary>Another character's corpses, as its file has them.</summary>
internal sealed record OtherCorpses(string Server, string Character, IReadOnlyList<CorpseRecord> Corpses);

internal sealed class CorpseStore
{
    public string Root;

    public CorpseStore(string defectiveRoot) => Root = Path.Combine(defectiveRoot, "corpses");

    public string PathFor(string server, string character) => Path.Combine(Root, DefStore.FileStem(server, character) + ".json");

    public List<CorpseRecord> Load(string server, string character, out string? problem)
    {
        problem = null;
        string path = PathFor(server, character);
        if (!File.Exists(path)) return new List<CorpseRecord>();
        try
        {
            return Parse(File.ReadAllBytes(path), out _, out _);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            problem = $"{Path.GetFileName(path)}: {ex.Message}";
            return new List<CorpseRecord>();
        }
    }

    public string? Save(string server, string character, IReadOnlyList<CorpseRecord> corpses)
    {
        string path = PathFor(server, character);
        try
        {
            Directory.CreateDirectory(Root);
            string tmp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(tmp, ToJson(server, character, corpses), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Every other character's corpses that haven't rotted (files that can't be read are skipped).</summary>
    public List<OtherCorpses> LoadOthers(string server, string character, DateTime now)
    {
        var result = new List<OtherCorpses>();
        if (!Directory.Exists(Root)) return result;
        string own = Path.GetFullPath(PathFor(server, character));
        foreach (string path in Directory.EnumerateFiles(Root, "*.json"))
        {
            if (string.Equals(Path.GetFullPath(path), own, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                List<CorpseRecord> list = Parse(File.ReadAllBytes(path), out string sv, out string ch);
                list.RemoveAll(c => CorpseBook.Expired(c, now));
                if (list.Count > 0 && ch.Length > 0) result.Add(new OtherCorpses(sv, ch, list));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
            {
            }
        }
        result.Sort((a, b) => string.Compare(a.Character, b.Character, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    internal static string ToJson(string server, string character, IReadOnlyList<CorpseRecord> corpses)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("server", server);
            w.WriteString("character", character);
            w.WriteStartArray("corpses");
            foreach (CorpseRecord c in corpses)
            {
                w.WriteStartObject();
                if (c.DiedUtc != DateTime.MinValue) w.WriteString("died", c.DiedUtc.ToString("o", CultureInfo.InvariantCulture));
                if (c.RotsUtc != DateTime.MinValue) w.WriteString("rots", c.RotsUtc.ToString("o", CultureInfo.InvariantCulture));
                if (c.SeenUtc != DateTime.MinValue) w.WriteString("seen", c.SeenUtc.ToString("o", CultureInfo.InvariantCulture));
                if (c.Cell != 0)
                {
                    w.WriteString("cell", c.Cell.ToString("X8", CultureInfo.InvariantCulture));
                    w.WriteNumber("x", Math.Round(c.X, 2));
                    w.WriteNumber("y", Math.Round(c.Y, 2));
                    w.WriteNumber("z", Math.Round(c.Z, 2));
                }
                if (c.ServerCoords)
                {
                    w.WriteNumber("ns", c.Ns);
                    w.WriteNumber("ew", c.Ew);
                }
                if (c.ObjectId != 0) w.WriteString("id", c.ObjectId.ToString("X8", CultureInfo.InvariantCulture));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static List<CorpseRecord> Parse(byte[] json, out string server, out string character)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("the file isn't a JSON object");
        server = Str(root, "server");
        character = Str(root, "character");
        var list = new List<CorpseRecord>();
        if (!root.TryGetProperty("corpses", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (JsonElement e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var c = new CorpseRecord
            {
                DiedUtc = Time(e, "died"), RotsUtc = Time(e, "rots"), SeenUtc = Time(e, "seen"),
                Cell = Hex(e, "cell"), X = (float)Num(e, "x"), Y = (float)Num(e, "y"), Z = (float)Num(e, "z"),
                ObjectId = Hex(e, "id"),
            };
            if (e.TryGetProperty("ns", out JsonElement ns) && ns.ValueKind == JsonValueKind.Number
                && e.TryGetProperty("ew", out JsonElement ew) && ew.ValueKind == JsonValueKind.Number)
            {
                c.Ns = ns.GetDouble(); c.Ew = ew.GetDouble(); c.ServerCoords = true;
            }
            if (c.Cell == 0 && !c.ServerCoords) continue;   // nowhere
            list.Add(c);
        }
        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static uint Hex(JsonElement e, string name) =>
        uint.TryParse(Str(e, name), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : 0;

    private static DateTime Time(JsonElement e, string name) =>
        DateTime.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t)
            ? t.ToUniversalTime() : DateTime.MinValue;
}

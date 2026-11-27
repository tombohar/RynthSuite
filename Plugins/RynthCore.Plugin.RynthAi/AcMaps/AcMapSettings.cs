// ============================================================================
//  RynthAi - AcMaps/AcMapSettings.cs
//  The ACMAPS maps' settings, for every character on this PC:
//  <RynthSuite>\RynthAi\acmaps.json - the maps folder, the chat line when a
//  dungeon has a map (off unless turned on), and each map's spots (keyed by
//  the image's path in the folder, so two parts of one dungeon keep their own).
//  Hand-written JSON (no reflection: RynthAi is NativeAOT).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using RynthCore.Install;

namespace RynthCore.Plugin.RynthAi.AcMaps;

internal sealed class AcMapSettings
{
    public const string FileName = "acmaps.json";
    public const string DefaultFolderName = "ACMaps";

    /// <summary>Where the maps are put when the player hasn't picked a folder: &lt;RynthSuite&gt;\RynthAi\ACMaps.</summary>
    public static string DefaultFolder => Path.Combine(RynthInstallPaths.RynthAiDir, DefaultFolderName);

    /// <summary>The settings file: &lt;RynthSuite&gt;\RynthAi\acmaps.json.</summary>
    public static string DefaultPath => Path.Combine(RynthInstallPaths.RynthAiDir, FileName);

    /// <summary>The maps folder; empty = <see cref="DefaultFolder"/>.</summary>
    public string Folder { get; set; } = string.Empty;
    /// <summary>Say in chat when you enter a dungeon that has a map. Off by default.</summary>
    public bool Announce { get; set; }
    /// <summary>Each map's spots, by the image's path relative to the folder (case-insensitive).</summary>
    public Dictionary<string, AcMapCalibration> Maps { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string FolderOrDefault => string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Folder;

    /// <summary>The map's calibration, made empty the first time.</summary>
    public AcMapCalibration For(string relativePath)
    {
        if (!Maps.TryGetValue(relativePath, out AcMapCalibration? c)) Maps[relativePath] = c = new AcMapCalibration();
        return c;
    }

    // ── JSON ─────────────────────────────────────────────────────────────

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteString("folder", Folder);
            w.WriteBoolean("announce", Announce);
            w.WriteStartObject("maps");
            foreach (var (path, cal) in Maps)
            {
                if (cal.IsEmpty && !cal.ScaleMeasuredOrSet) continue;
                w.WriteStartObject(path);
                w.WriteNumber("scale", Math.Round(cal.Scale, 4));
                w.WriteBoolean("scaleSet", cal.ScaleMeasuredOrSet);
                w.WriteStartArray("spots");
                foreach (MapSpot s in cal.Spots)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(Math.Round(s.X, 2));
                    w.WriteNumberValue(Math.Round(s.Y, 2));
                    w.WriteNumberValue(Math.Round(s.Z, 2));
                    w.WriteNumberValue(Math.Round(s.Px, 1));
                    w.WriteNumberValue(Math.Round(s.Py, 1));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The settings in <paramref name="json"/>; anything unreadable is left at its default.</summary>
    public static AcMapSettings FromJson(string json)
    {
        var s = new AcMapSettings();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return s;
            if (root.TryGetProperty("folder", out JsonElement f) && f.ValueKind == JsonValueKind.String) s.Folder = f.GetString() ?? string.Empty;
            if (root.TryGetProperty("announce", out JsonElement a) && (a.ValueKind == JsonValueKind.True || a.ValueKind == JsonValueKind.False))
                s.Announce = a.GetBoolean();
            if (root.TryGetProperty("maps", out JsonElement maps) && maps.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty map in maps.EnumerateObject())
                {
                    if (map.Value.ValueKind != JsonValueKind.Object) continue;
                    float scale = map.Value.TryGetProperty("scale", out JsonElement sc) && sc.TryGetDouble(out double d) ? (float)d : AcMapCalibration.DefaultScale;
                    bool set = map.Value.TryGetProperty("scaleSet", out JsonElement ss) && ss.ValueKind == JsonValueKind.True;
                    var spots = new List<MapSpot>();
                    if (map.Value.TryGetProperty("spots", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement e in arr.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 5) continue;
                            var v = new float[5];
                            bool ok = true;
                            for (int i = 0; i < 5 && ok; i++)
                            {
                                ok = e[i].ValueKind == JsonValueKind.Number && e[i].TryGetDouble(out double x);
                                if (ok) v[i] = (float)e[i].GetDouble();
                            }
                            if (ok) spots.Add(new MapSpot(v[0], v[1], v[2], v[3], v[4]));
                        }
                    s.Maps[map.Name] = new AcMapCalibration(scale, set, spots);
                }
            }
        }
        catch (JsonException) { }
        return s;
    }

    /// <summary>The settings file, or defaults when it isn't there or can't be read. Any thread.</summary>
    public static AcMapSettings Load(string path)
    {
        try { return File.Exists(path) ? FromJson(File.ReadAllText(path, Encoding.UTF8)) : new AcMapSettings(); }
        catch { return new AcMapSettings(); }
    }

    /// <summary>Writes the file (a temp file, then moved over). The error, or "" when saved.</summary>
    public static string Save(string path, string json)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return string.Empty;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>"3.00" for the window.</summary>
    public static string ScaleText(float scale) => scale.ToString("0.00", CultureInfo.InvariantCulture);
}

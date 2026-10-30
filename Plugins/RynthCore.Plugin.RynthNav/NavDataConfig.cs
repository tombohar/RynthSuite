using System;
using System.IO;
using System.Text.Json;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// Where RynthNav's data folder is: navmesh tiles (nav_XXXX.tile), portals.tsv, locations.json,
/// and the player's own atlas.txt / recalls.txt. In order:
///   1. the RYNTHNAV_NAVDATA environment variable (a test client, a second install);
///   2. "navDataDir" in %APPDATA%\RynthCore\rynthnav.json, e.g. {"navDataDir": "D:\\AC\\NavData"}
///      (the launcher's tile updater reads the same file, so both agree);
///   3. C:\Games\RynthCore\NavData, where the release zip and the launcher put the tiles.
/// A value that isn't a full path is ignored (and the source says so). Host-free, so the
/// route tests cover it.
/// </summary>
internal sealed class NavDataConfig
{
    public const string DefaultDir = @"C:\Games\RynthCore\NavData";
    public const string EnvVar = "RYNTHNAV_NAVDATA";
    public const string SettingsFileName = "rynthnav.json";

    public string Dir { get; }
    /// <summary>Where <see cref="Dir"/> came from, for the log and /rnav navdata.</summary>
    public string Source { get; }

    private NavDataConfig(string dir, string source) { Dir = dir; Source = source; }

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RynthCore", SettingsFileName);

    public static NavDataConfig Resolve()
    {
        string settings;
        try { settings = SettingsPath; } catch { settings = ""; }
        return Resolve(Environment.GetEnvironmentVariable(EnvVar), settings);
    }

    internal static NavDataConfig Resolve(string? env, string settingsPath)
    {
        string note = "";
        if (!string.IsNullOrWhiteSpace(env))
        {
            if (IsFullPath(env)) return new NavDataConfig(Clean(env), EnvVar);
            note = $"; {EnvVar} ignored (not a full path)";
        }
        try
        {
            if (settingsPath.Length > 0 && File.Exists(settingsPath))
            {
                string? dir = ReadSetting(File.ReadAllText(settingsPath));
                if (dir != null && IsFullPath(dir)) return new NavDataConfig(Clean(dir), settingsPath + note);
                if (dir != null) note += $"; navDataDir in {settingsPath} ignored (not a full path)";
            }
        }
        catch (Exception ex) { note += $"; {settingsPath} unreadable ({ex.GetType().Name})"; }
        return new NavDataConfig(DefaultDir, "default" + note);
    }

    /// <summary>The "navDataDir" string from rynthnav.json's text, or null.</summary>
    internal static string? ReadSetting(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("navDataDir", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>A true/false setting from rynthnav.json ("portals", "recalls"); <paramref name="fallback"/> when absent.</summary>
    internal static bool ReadSwitch(string name, bool fallback)
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return fallback;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return fallback;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return p.Value.GetBoolean();
        }
        catch { }
        return fallback;
    }

    /// <summary>A number setting from rynthnav.json; <paramref name="fallback"/> when absent or not a number.</summary>
    internal static double ReadNumber(string name, double fallback)
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return fallback;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return fallback;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
                    return p.Value.GetDouble();
        }
        catch { }
        return fallback;
    }

    /// <summary>Writes a number setting into rynthnav.json, keeping everything else in it.</summary>
    internal static void WriteNumber(string name, double value) => Write(name, System.Text.Json.Nodes.JsonValue.Create(value));

    /// <summary>Writes a true/false setting into rynthnav.json, keeping everything else in it (navDataDir, which the launcher reads too).</summary>
    internal static void WriteSwitch(string name, bool value) => Write(name, System.Text.Json.Nodes.JsonValue.Create(value));

    private static void Write(string name, System.Text.Json.Nodes.JsonNode? value)
    {
        try
        {
            string path = SettingsPath;
            System.Text.Json.Nodes.JsonObject root = new();
            if (File.Exists(path)
                && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) is System.Text.Json.Nodes.JsonObject existing)
                root = existing;
            root[name] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private static bool IsFullPath(string path)
    {
        try { return Path.IsPathFullyQualified(path.Trim()); } catch { return false; }
    }

    private static string Clean(string path) => path.Trim().TrimEnd('\\', '/');
}

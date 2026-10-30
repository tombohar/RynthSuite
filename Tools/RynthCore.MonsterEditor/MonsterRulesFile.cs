using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RynthCore.MonsterEditor;

/// <summary>
/// Reading and writing a monsters file (a JSON array of MonsterRule, the format RynthAi's
/// LegacyDashboardRenderer reads and writes), and finding the file the plugin uses. No UI:
/// Tools/RynthCore.RynthAiHostTests compiles this file in and round-trips a file through it and
/// the plugin's own serializer.
/// </summary>
public static class MonsterRulesFile
{
    private const string SettingsProfilesDir = "SettingsProfiles";
    private const string MonsterProfilesDir  = "MonsterProfiles";
    // RynthAi's LegacyServerFolder: its monsters live straight in MonsterProfiles\.
    private const string LegacyServerFolder  = "ACEmulator";
    private const string LegacyFileName      = "monsters.json";

    /// <summary>The rules in <paramref name="path"/>; empty when the file doesn't exist.
    /// Throws on a file that isn't a JSON array of rules (the caller shows the error and
    /// doesn't save over it).</summary>
    public static List<MonsterRule> Load(string path)
    {
        if (!File.Exists(path)) return new List<MonsterRule>();
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, EditorJsonContext.Default.ListMonsterRule) ?? new List<MonsterRule>();
    }

    /// <summary>Writes <paramref name="rules"/> in place (File.WriteAllText, as before: the
    /// plugin's FileSystemWatcher reloads on a LastWrite change).</summary>
    public static void Save(string path, IEnumerable<MonsterRule> rules)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(new List<MonsterRule>(rules), EditorJsonContext.Default.ListMonsterRule));
    }

    /// <summary>
    /// The monsters file for what the editor was given: a .json file is edited as is. A
    /// character's settings folder (RynthAi\SettingsProfiles\&lt;server&gt;\&lt;character&gt;, what the
    /// plugin passes) maps to the plugin's file, RynthAi\MonsterProfiles\&lt;server&gt;\&lt;character&gt;.json
    /// (MonsterProfiles\&lt;character&gt;.json for the ACEmulator folder). The old
    /// &lt;folder&gt;\monsters.json is used only when that file doesn't exist and the old one does
    /// (the plugin moves it across at its next load).
    /// </summary>
    public static string ResolveRulesPath(string pathOrFolder)
    {
        string p = pathOrFolder.Trim().Trim('"').TrimEnd('\\', '/');
        if (File.Exists(p) || p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return p;

        string legacy = Path.Combine(p, LegacyFileName);
        string? current = PluginRulesPathForCharFolder(p);
        if (current == null) return legacy;
        if (File.Exists(current)) return current;
        return File.Exists(legacy) ? legacy : current;
    }

    /// <summary>RynthAi's MonsterProfiles file for a SettingsProfiles character folder; null when
    /// the folder isn't laid out that way.</summary>
    public static string? PluginRulesPathForCharFolder(string charFolder)
    {
        var character = new DirectoryInfo(charFolder.TrimEnd('\\', '/'));
        DirectoryInfo? server = character.Parent;
        DirectoryInfo? profiles = server?.Parent;
        DirectoryInfo? root = profiles?.Parent;
        if (server == null || profiles == null || root == null) return null;
        if (!profiles.Name.Equals(SettingsProfilesDir, StringComparison.OrdinalIgnoreCase)) return null;

        string monsters = Path.Combine(root.FullName, MonsterProfilesDir);
        return server.Name.Equals(LegacyServerFolder, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(monsters, character.Name + ".json")
            : Path.Combine(monsters, server.Name, character.Name + ".json");
    }

    /// <summary>The character's settings folder (where active_profile.txt and the profile with
    /// the weapon list live) for a monsters file: the file's own folder for an old
    /// monsters.json, the matching SettingsProfiles folder for a MonsterProfiles file, else the
    /// file's own folder.</summary>
    public static string CharFolderForRulesPath(string rulesPath)
    {
        var file = new FileInfo(rulesPath);
        string folder = file.DirectoryName ?? "";
        if (file.Name.Equals(LegacyFileName, StringComparison.OrdinalIgnoreCase))
            return folder;

        string character = Path.GetFileNameWithoutExtension(file.Name);
        DirectoryInfo? dir = file.Directory;
        if (dir == null) return folder;
        if (dir.Name.Equals(MonsterProfilesDir, StringComparison.OrdinalIgnoreCase) && dir.Parent != null)
            return Path.Combine(dir.Parent.FullName, SettingsProfilesDir, LegacyServerFolder, character);
        if (dir.Parent is { } monsters && monsters.Name.Equals(MonsterProfilesDir, StringComparison.OrdinalIgnoreCase) && monsters.Parent != null)
            return Path.Combine(monsters.Parent.FullName, SettingsProfilesDir, dir.Name, character);
        return folder;
    }
}

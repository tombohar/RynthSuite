using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using RynthCore.MonsterEditor;
using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiTests.Harness;
using EditorRule = RynthCore.MonsterEditor.MonsterRule;
using PluginRule = RynthCore.Plugin.RynthAi.LegacyUi.MonsterRule;

namespace RynthCore.RynthAiHostTests.Tests;

// The standalone Monster Editor (Tools/RynthCore.MonsterEditor; Models.cs, JsonContext.cs and
// MonsterRulesFile.cs compiled in) reads and writes the plugin's monsters file. Before 2026-10-05
// its MonsterRule had no Category or MatchExpression (or CustomDebuffs), so an editor save
// dropped them and an expression rule fell back to matching by name only. Spotted by
// Silentkelpie (rynth PR #1); re-implemented here. Every file goes through the plugin's own
// serializer (RynthAiJsonContext) on the way in and out.
internal static class MonsterEditorTests
{
    public static void Register(Runner r)
    {
        r.Add("monster editor: a plugin file round-trips through the editor (every field kept)", RoundTripKeepsEveryField);
        r.Add("monster editor: Category and MatchExpression load, edit and save", CategoryAndMatchEditable);
        r.Add("monster editor: unknown fields survive, files without the new fields still load", UnknownAndOldFields);
        r.Add("monster editor: a character folder opens the plugin's MonsterProfiles file", FindsThePluginsFile);
    }

    private static string Dir(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "monster-editor", name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static List<PluginRule> PluginRules() => new()
    {
        new PluginRule { Name = "Default", Priority = 1 },
        new PluginRule
        {
            Name = "Olthoi", Category = "Olthoi nest", MatchExpression = "getcharintprop[25]>=200",
            Priority = 3, DamageType = "Acid", WeaponId = 0x50001234, Fester = true, Broadside = true,
            GravityWell = true, Imperil = true, Yield = true, Vuln = true,
            Corrosion = true, Corruption = true, DestructiveCurse = true, WeakeningCurse = true, FesteringCurse = true,
            UseArc = true, UseRing = true,
            UseStreak = true, UseBolt = false, UseBlast = true, UseWall = true, ExVuln = "Fire", OffhandId = 2,
            PetDamage = "Cold", CustomDebuffs = "Corrosion Vulnerability Other, Fire Vulnerability Other VII",
            Kite = "PullAway",
        },
        new PluginRule { Name = "", Category = "Expression only", MatchExpression = "wobjectfindnearestbytemplatetype[1]" },
    };

    private static string WriteAsPlugin(string dir, List<PluginRule> rules)
    {
        string path = Path.Combine(dir, "Toon.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rules, RynthAiJsonContext.Default.MonsterRuleList));
        return path;
    }

    private static List<PluginRule> ReadAsPlugin(string path) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), RynthAiJsonContext.Default.MonsterRuleList)!;

    /// <summary>Every public property of the plugin's rule, compared one by one.</summary>
    private static void SameRules(List<PluginRule> actual, List<PluginRule> expected, string what)
    {
        Check.Eq(actual.Count, expected.Count, what + ": rule count");
        PropertyInfo[] props = typeof(PluginRule).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Check.True(props.Any(p => p.Name == "MatchExpression") && props.Any(p => p.Name == "Category"), "the plugin's rule has both fields");
        for (int i = 0; i < Math.Min(actual.Count, expected.Count); i++)
            foreach (PropertyInfo p in props)
                Check.Eq(p.GetValue(actual[i])?.ToString(), p.GetValue(expected[i])?.ToString(), $"{what}: rule {i} {p.Name}");
    }

    private static void RoundTripKeepsEveryField()
    {
        List<PluginRule> original = PluginRules();
        string path = WriteAsPlugin(Dir("roundtrip"), original);

        // Open in the editor, change one thing (as a grid edit does), save.
        List<EditorRule> rules = MonsterRulesFile.Load(path);
        Check.Eq(rules.Count, 3, "editor loads every rule");
        rules[1].Priority = 4;
        MonsterRulesFile.Save(path, rules);

        List<PluginRule> back = ReadAsPlugin(path);
        original[1].Priority = 4;
        SameRules(back, original, "after an editor save");

        // A second save with no edits changes nothing more.
        MonsterRulesFile.Save(path, MonsterRulesFile.Load(path));
        SameRules(ReadAsPlugin(path), original, "after a second save");
        string json = File.ReadAllText(path);
        Check.Eq(CountOf(json, "\"MatchExpression\""), 3, "MatchExpression written once per rule");
        Check.Eq(CountOf(json, "\"CustomDebuffs\""), 3, "CustomDebuffs written once per rule");
    }

    private static void CategoryAndMatchEditable()
    {
        string path = WriteAsPlugin(Dir("edit"), PluginRules());
        List<EditorRule> rules = MonsterRulesFile.Load(path);
        Check.Eq(rules[1].Category, "Olthoi nest", "Category read");
        Check.Eq(rules[1].MatchExpression, "getcharintprop[25]>=200", "MatchExpression read");

        var changed = new List<string>();
        rules[1].PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        rules[1].MatchExpression = "";             // back to matching by name
        rules[1].Category = null!;                 // a cleared grid cell
        Check.True(changed.Contains("MatchExpression") && changed.Contains("Category"), "edits raise PropertyChanged (the editor saves on it)");
        rules[2].MatchExpression = "true";
        MonsterRulesFile.Save(path, rules);

        List<PluginRule> back = ReadAsPlugin(path);
        Check.Eq(back[1].MatchExpression, "", "cleared expression saved");
        Check.Eq(back[1].Category, "", "a null Category is saved as empty");
        Check.Eq(back[2].MatchExpression, "true", "edited expression saved");
        Check.Eq(back[2].Category, "Expression only", "untouched Category kept");

        // A rule added in the editor carries the plugin's defaults for the new fields.
        var added = new EditorRule { Name = "Drudge" };
        Check.Eq(added.Category, "", "new rule: empty Category");
        Check.Eq(added.MatchExpression, "", "new rule: empty MatchExpression (by name)");
    }

    private static void UnknownAndOldFields()
    {
        string dir = Dir("unknown");
        string path = Path.Combine(dir, "Toon.json");
        // A field neither side knows yet (a later plugin), a nested value, and a file from before
        // Category/MatchExpression existed (no such keys at all).
        File.WriteAllText(path, """
            [
              { "Name": "Default", "Priority": 1 },
              { "Name": "Mosswart", "Priority": 2, "FutureField": { "A": [1, 2], "B": null }, "Shout": "hi" }
            ]
            """);
        List<EditorRule> rules = MonsterRulesFile.Load(path);
        Check.Eq(rules[1].Category, "", "old file: Category empty");
        Check.Eq(rules[1].MatchExpression, "", "old file: MatchExpression empty");
        MonsterRulesFile.Save(path, rules);

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement mosswart = doc.RootElement[1];
        Check.True(mosswart.TryGetProperty("FutureField", out JsonElement future), "unknown field kept");
        Check.Eq(future.GetRawText().Replace(" ", "").Replace("\r", "").Replace("\n", ""), """{"A":[1,2],"B":null}""", "unknown field value unchanged");
        Check.Eq(mosswart.GetProperty("Shout").GetString(), "hi", "unknown string kept");
        Check.Eq(ReadAsPlugin(path)[1].Priority, 2, "the plugin still reads the file");
    }

    // The plugin launches the editor with the character's settings folder
    // (SettingsProfiles\<server>\<character>) but reads MonsterProfiles\<server>\<character>.json
    // (LegacyDashboardRenderer.MonstersFilePath); the editor used to open <folder>\monsters.json.
    private static void FindsThePluginsFile()
    {
        string root = Dir("paths");
        string Rel(params string[] parts) => Path.Combine(new[] { root }.Concat(parts).ToArray());
        string aelChar = Rel("SettingsProfiles", "Aelrynth", "Toon");
        string aceChar = Rel("SettingsProfiles", "ACEmulator", "Toon");
        Directory.CreateDirectory(aelChar);
        Directory.CreateDirectory(aceChar);
        string aelFile = Rel("MonsterProfiles", "Aelrynth", "Toon.json");
        string aceFile = Rel("MonsterProfiles", "Toon.json");

        // Not there yet: the plugin's path (a save creates it where the plugin reads).
        Check.Eq(MonsterRulesFile.ResolveRulesPath(aelChar), aelFile, "server folder -> MonsterProfiles\\<server>\\<char>.json");
        Check.Eq(MonsterRulesFile.ResolveRulesPath(aceChar + "\\"), aceFile, "ACEmulator -> MonsterProfiles\\<char>.json (trailing slash ok)");
        Check.Eq(MonsterRulesFile.ResolveRulesPath("\"" + aelChar + "\""), aelFile, "quoted argument");

        // Only the old file exists: that one (the plugin moves it across at its next load).
        string legacy = Path.Combine(aelChar, "monsters.json");
        File.WriteAllText(legacy, "[]");
        Check.Eq(MonsterRulesFile.ResolveRulesPath(aelChar), legacy, "only the old monsters.json: use it");

        // Both: the plugin's.
        Directory.CreateDirectory(Path.GetDirectoryName(aelFile)!);
        File.WriteAllText(aelFile, "[]");
        Check.Eq(MonsterRulesFile.ResolveRulesPath(aelChar), aelFile, "both exist: the plugin's file");

        // A file is edited as given; a folder outside SettingsProfiles keeps the old behaviour.
        Check.Eq(MonsterRulesFile.ResolveRulesPath(aelFile), aelFile, "a file opens as is");
        string loose = Rel("Elsewhere", "Toon");
        Check.Eq(MonsterRulesFile.ResolveRulesPath(loose), Path.Combine(loose, "monsters.json"), "unknown layout: <folder>\\monsters.json");

        // And back: the settings folder for the weapon list.
        Check.Eq(MonsterRulesFile.CharFolderForRulesPath(aelFile), aelChar, "MonsterProfiles\\<server> file -> its settings folder");
        Check.Eq(MonsterRulesFile.CharFolderForRulesPath(aceFile), aceChar, "MonsterProfiles file -> SettingsProfiles\\ACEmulator");
        Check.Eq(MonsterRulesFile.CharFolderForRulesPath(legacy), aelChar, "old monsters.json -> its own folder");
        Check.Eq(MonsterRulesFile.CharFolderForRulesPath(Rel("Elsewhere", "x.json")), Rel("Elsewhere"), "unknown layout -> the file's folder");

        // The plugin's own path for this character agrees.
        Check.Eq(MonsterRulesFile.PluginRulesPathForCharFolder(aelChar), aelFile, "PluginRulesPathForCharFolder");
        Check.Null(MonsterRulesFile.PluginRulesPathForCharFolder(loose), "not under SettingsProfiles: null");
    }

    private static int CountOf(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}

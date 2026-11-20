using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-18: the Monsters rules belong to the settings profile. Before, the MonsterProfiles
// file (one per character) overrode every profile at login, so after editing the Monsters list
// under one profile, a reload or relog on another profile brought those rules back: a Void
// profile's Default row (off hand Auto) replaced the main profile's (two hand wraps), and the
// character fought with one weapon while the Monsters tab showed the wrong list. Now the file is
// the active profile's copy, tagged "<file>.profile" with that profile's name.
internal static class MonsterProfileTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const int HandWraps = unchecked((int)0x801A1F0D);
    private const int OffhandAuto = 1;

    public static void Register(Runner r)
    {
        r.Add("monsters per profile: switching profiles switches the Monsters list", SwitchSwitchesList);
        r.Add("monsters per profile: a relog keeps the profile's own list, not another profile's file", RelogKeepsOwnList);
        r.Add("monsters per profile: an untagged Monsters file (from before) still loads, then is tagged", UntaggedFileLoads);
        r.Add("monsters per profile: a Monster Editor save still reloads into the profile in use", EditorSaveReloads);
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string? _buffs = BuffProfileStore.FolderOverride;
        public readonly string Root, CharDir, MonstersFile;

        public Scratch(string name)
        {
            Root = Path.Combine(Program.TempRoot, "monster-profiles", name);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            Directory.CreateDirectory(Root);
            LegacyDashboardRenderer.ProfilesBase = Path.Combine(Root, "SettingsProfiles");
            LegacyDashboardRenderer.MonstersBase = Path.Combine(Root, "MonsterProfiles");
            BuffProfileStore.FolderOverride = Path.Combine(Root, "BuffProfiles");
            CharDir = Path.Combine(LegacyDashboardRenderer.ProfilesBase, "TestWorld", "Tester");
            MonstersFile = Path.Combine(LegacyDashboardRenderer.MonstersBase, "TestWorld", "Tester.json");
        }

        public string Tag => File.Exists(MonstersFile + ".profile") ? File.ReadAllText(MonstersFile + ".profile").Trim() : "";

        public void Dispose() => BuffProfileStore.FolderOverride = _buffs;
    }

    /// <summary>A fresh dashboard logged in as Tester on TestWorld (a login or a reload).</summary>
    private static LegacyDashboardRenderer Login(out LegacyUiSettings s)
    {
        FakeHost.Reset();
        DataWriteGuard.Reset();
        FakeHost.WorldName = "TestWorld";
        var dash = new LegacyDashboardRenderer(FakeHost.Create());
        FakeHost.WorldName = "TestWorld";
        dash.LoadSettings("Tester");
        s = (LegacyUiSettings)typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.GetValue(dash)!;
        return dash;
    }

    private static MonsterRule DefaultRow(LegacyUiSettings s) =>
        s.MonsterRules.Single(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));

    private static void Select(LegacyDashboardRenderer dash, string profile) =>
        dash.SelectProfileAtIndex(3, Array.IndexOf(dash.ReadDashboardFiles().Profiles, profile));

    /// <summary>Default: Melee, the second hand wraps in the off hand, plus a Drudge rule. Void (Save As): off hand Auto, Void Magic, no Drudge.</summary>
    private static LegacyDashboardRenderer TwoProfiles(out LegacyUiSettings s)
    {
        var dash = Login(out s);
        DefaultRow(s).OffhandId = HandWraps;
        DefaultRow(s).PreferredDamage = "Melee";
        s.MonsterRules.Add(new MonsterRule { Name = "Drudge", Priority = 3 });
        dash.SaveMonsterRules();
        Check.True(dash.SaveProfileNow(ProfileKind.Settings).Ok, "Default saved");

        Check.True(dash.SaveProfileAs(ProfileKind.Settings, "Void", replace: false).Ok, "Save As Void");
        Check.Eq(s.SelectedProfile, "Void", "Void in use");
        DefaultRow(s).OffhandId = OffhandAuto;
        DefaultRow(s).PreferredDamage = "Void Magic";
        s.MonsterRules.RemoveAll(r => r.Name == "Drudge");
        dash.SaveMonsterRules();   // as the Monsters panel does after an edit
        Check.True(dash.SaveProfileNow(ProfileKind.Settings).Ok, "Void saved");
        return dash;
    }

    private static void SwitchSwitchesList()
    {
        using var t = new Scratch("switch");
        var dash = TwoProfiles(out var s);
        Check.Eq(t.Tag, "Void", "the Monsters file is Void's while Void is in use");

        Select(dash, "Default");
        Check.Eq(s.SelectedProfile, "Default", "Default in use");
        Check.Eq(DefaultRow(s).OffhandId, HandWraps, "Default's off hand (the hand wraps), not Void's Auto");
        Check.Eq(DefaultRow(s).PreferredDamage, "Melee", "Default's damage");
        Check.True(s.MonsterRules.Any(r => r.Name == "Drudge"), "Default's own Drudge rule is back");
        Check.Eq(t.Tag, "Default", "the Monsters file is tagged Default");
        Check.True(File.ReadAllText(t.MonstersFile).Contains("Drudge"), "and holds Default's rules (for the Monster Editor)");

        Select(dash, "Void");
        Check.Eq(DefaultRow(s).OffhandId, OffhandAuto, "Void's own off hand again");
        Check.False(s.MonsterRules.Any(r => r.Name == "Drudge"), "Void has no Drudge rule");
        Check.Eq(t.Tag, "Void", "tagged Void");
    }

    private static void RelogKeepsOwnList()
    {
        using var t = new Scratch("relog");
        var dash = TwoProfiles(out var s);
        Select(dash, "Default");

        // The report: the file still held Void's rules while Default was the profile in use.
        File.WriteAllText(t.MonstersFile, "[{\"Name\":\"Default\",\"Priority\":1,\"PreferredDamage\":\"Void Magic\",\"OffhandId\":1}]");
        File.WriteAllText(t.MonstersFile + ".profile", "Void");

        Login(out s);
        Check.Eq(s.SelectedProfile, "Default", "Default is the active profile");
        Check.Eq(DefaultRow(s).OffhandId, HandWraps, "a reload keeps Default's off hand");
        Check.True(s.MonsterRules.Any(r => r.Name == "Drudge"), "and Default's other rules");
        Check.Eq(t.Tag, "Default", "the file is Default's again");
        Check.True(File.ReadAllText(t.MonstersFile).Contains("Drudge"), "rewritten with Default's rules");
        Check.True(FakeHost.Logs.Any(l => l.Contains("held profile 'Void''s rules")), "logged why");
    }

    private static void UntaggedFileLoads()
    {
        using var t = new Scratch("untagged");
        var dash = Login(out var s);
        Check.True(dash.SaveProfileNow(ProfileKind.Settings).Ok, "Default saved");
        Directory.CreateDirectory(Path.GetDirectoryName(t.MonstersFile)!);
        File.Delete(t.MonstersFile + ".profile");
        // A file from before the tag (the one list every profile used): its rules are this profile's.
        File.WriteAllText(t.MonstersFile, "[{\"Name\":\"Default\",\"Priority\":1},{\"Name\":\"Banderling\",\"Priority\":4}]");

        Login(out s);
        Check.True(s.MonsterRules.Any(r => r.Name == "Banderling"), "the untagged file's rules load, as before");
        Check.Eq(t.Tag, "Default", "and the file is now tagged with the profile in use");
    }

    private static void EditorSaveReloads()
    {
        using var t = new Scratch("editor");
        var dash = TwoProfiles(out var s);
        Select(dash, "Default");

        // The Monster Editor writes the rules file only; the tag stays Default's.
        File.WriteAllText(t.MonstersFile, "[{\"Name\":\"Default\",\"Priority\":1,\"OffhandId\":" + HandWraps + "},{\"Name\":\"Mosswart\",\"Priority\":2}]");
        typeof(LegacyDashboardRenderer).GetField("_monsterFileChanged", Any)!.SetValue(dash, true);
        dash.TickMonsterReload();
        Check.True(s.MonsterRules.Any(r => r.Name == "Mosswart"), "the editor's rules are in use");
        Check.Eq(t.Tag, "Default", "still Default's file");

        Select(dash, "Void");
        Select(dash, "Default");
        Check.True(s.MonsterRules.Any(r => r.Name == "Mosswart"), "the editor's change was saved into Default (it survives a switch)");
    }
}

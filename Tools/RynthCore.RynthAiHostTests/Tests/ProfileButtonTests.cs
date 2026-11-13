using System;
using System.IO;
using System.Linq;
using System.Reflection;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-11: the Loaded files drawer's profile buttons (Save, Save As, New, Rename, Move to
// Deleted, Open folder) and their /ra verbs. The name and path helpers (ProfileFileOps), then each
// kind through the dashboard: settings profiles, navs, metas, loot profiles, and the Spells window's
// buff profile Delete. Nothing is ever deleted: a removed profile goes to the Deleted folder beside
// it, and a file replaced by Save As has its old copy kept there first. Scratch folders only (the
// dashboard's NavProfiles / MetaFiles / LootProfiles fields and ProfilesBase are pointed at them).
internal static class ProfileButtonTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("profile buttons: Save As names are tidied and unsafe ones refused", NameSanitising);
        r.Add("profile buttons: replace detection: another existing file only, never the file in use", OverwriteDetection);
        r.Add("profile buttons: Move to Deleted moves (never deletes), names never collide", MoveToDeleted);
        r.Add("profile buttons: Save As over another file keeps its old copy in Deleted", CopyToDeleted);
        r.Add("profile buttons: rename refuses a name taken, allows a change of case", RenameHelper);
        r.Add("profile buttons: settings Save As / replace / rename / Move to Deleted", SettingsProfiles);
        r.Add("profile buttons: nav New / Save As asks before replacing / rename / Move to Deleted", NavProfiles);
        r.Add("profile buttons: meta New / Save As / replace / rename / Move to Deleted", MetaProfiles);
        r.Add("profile buttons: loot Save As copies, New checks the name, rename, Move to Deleted", LootProfiles);
        r.Add("profile buttons: the Spells window's Delete moves the buff profile to Deleted", BuffDelete);
        r.Add("profile buttons: the /ra verbs reply (worked, or why and the usage)", CommandsReply);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Dir(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "profile-buttons", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int AllFiles(string dir) => Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;

    private static string[] DeletedFiles(string folder)
    {
        string d = Path.Combine(folder, ProfileFileOps.DeletedFolderName);
        return Directory.Exists(d) ? Directory.GetFiles(d).Select(Path.GetFileName).ToArray()! : Array.Empty<string>();
    }

    /// <summary>A dashboard whose nav, meta and loot folders are scratch folders under <paramref name="root"/>.</summary>
    private static LegacyDashboardRenderer Dash(string root, out LegacyUiSettings s)
    {
        FakeHost.Reset();
        DataWriteGuard.Reset();
        var dash = new LegacyDashboardRenderer(FakeHost.Create());
        foreach (var (field, sub) in new[] { ("_navFolder", "NavProfiles"), ("_metaFolder", "MetaFiles"), ("_lootFolder", "LootProfiles") })
        {
            string folder = Path.Combine(root, sub);
            Directory.CreateDirectory(folder);
            typeof(LegacyDashboardRenderer).GetField(field, Any)!.SetValue(dash, folder);
        }
        s = (LegacyUiSettings)typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.GetValue(dash)!;
        return dash;
    }

    // ── ProfileFileOps ───────────────────────────────────────────────────────

    private static void NameSanitising()
    {
        Check.Null(ProfileFileOps.CleanName("  \"Night Farm.nav\" ", LegacyDashboardRenderer.NavExtensions, out string n), "quotes, spaces and .nav are fine");
        Check.Eq(n, "Night Farm", "tidied");
        Check.Null(ProfileFileOps.CleanName("Loot.v2.utl", LegacyDashboardRenderer.LootExtensions, out n), "dots inside are fine");
        Check.Eq(n, "Loot.v2", "only the known extension is dropped");
        Check.Null(ProfileFileOps.CleanName("farm.AF", LegacyDashboardRenderer.MetaExtensions, out n), "extension case");
        Check.Eq(n, "farm", "farm");
        foreach (string bad in new[] { "", "   ", "a/b", "..\\x", "..", "a:b", "what?", "con", "LPT1", "x.", new string('a', 130) })
            Check.NotNull(ProfileFileOps.CleanName(bad, LegacyDashboardRenderer.NavExtensions, out _), $"refused: '{bad}'");
        Check.True(LegacyDashboardRenderer.IsReservedSettingsName("Monsters"), "monsters.json is RynthAi's own");
        Check.True(LegacyDashboardRenderer.IsReservedSettingsName("metamanager"), "metamanager.json is RynthAi's own");
        Check.False(LegacyDashboardRenderer.IsReservedSettingsName("Hunting"), "a plain name is not");
    }

    private static void OverwriteDetection()
    {
        string dir = Dir("overwrite");
        string a = Path.Combine(dir, "a.nav"), b = Path.Combine(dir, "b.nav"), c = Path.Combine(dir, "c.nav");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        Check.True(ProfileFileOps.WouldReplace(b, a), "another file that exists");
        Check.False(ProfileFileOps.WouldReplace(a, a), "the file in use is a plain save");
        Check.False(ProfileFileOps.WouldReplace(Path.Combine(dir, "A.NAV"), a), "the file in use, another case");
        Check.False(ProfileFileOps.WouldReplace(c, a), "a name not taken");
        Check.True(ProfileFileOps.WouldReplace(b, string.Empty), "nothing in use: an existing file is still replaced");
    }

    private static void MoveToDeleted()
    {
        string dir = Dir("move");
        string path = Path.Combine(dir, "farm.nav");
        File.WriteAllText(path, "route");
        var when = new DateTime(2026, 11, 11, 15, 30, 12);
        Check.Eq(Path.GetFileName(ProfileFileOps.DeletedPathFor(path, when)), "farm.2026-11-11_153012.nav", "the time before the extension");
        Check.True(ProfileFileOps.MoveToDeleted(path, when, out string moved, out string error), "moved: " + error);
        Check.False(File.Exists(path), "gone from its folder");
        Check.Eq(File.ReadAllText(moved), "route", "the same file in Deleted");
        Check.Eq(Path.GetDirectoryName(moved), Path.Combine(dir, "Deleted"), "in <folder>\\Deleted");
        Check.Eq(Directory.GetFiles(dir, "*.nav").Length, 0, "the picker's folder no longer lists it");

        // The same name at the same second: a second copy, never over the first.
        File.WriteAllText(path, "route 2");
        Check.True(ProfileFileOps.MoveToDeleted(path, when, out string moved2, out _), "second move");
        Check.Eq(Path.GetFileName(moved2), "farm.2026-11-11_153012 (2).nav", "numbered");
        Check.Eq(File.ReadAllText(moved), "route", "the first copy kept");
        Check.Eq(AllFiles(dir), 2, "nothing lost");

        Check.False(ProfileFileOps.MoveToDeleted(path, when, out _, out error), "a file that isn't there");
        Check.True(error.Contains("isn't there"), "says why: " + error);
    }

    private static void CopyToDeleted()
    {
        string dir = Dir("copy");
        string path = Path.Combine(dir, "farm.af");
        File.WriteAllText(path, "old");
        Check.True(ProfileFileOps.CopyToDeleted(path, new DateTime(2026, 11, 11, 9, 0, 0), out string copy, out string error), "kept: " + error);
        Check.Eq(File.ReadAllText(copy), "old", "the old copy");
        Check.True(File.Exists(path), "the file itself stays (it is saved over next)");
        Check.True(ProfileFileOps.CopyToDeleted(Path.Combine(dir, "none.af"), DateTime.Now, out copy, out _), "nothing to keep is fine");
        Check.Eq(copy, string.Empty, "and keeps nothing");
    }

    private static void RenameHelper()
    {
        string dir = Dir("rename");
        string a = Path.Combine(dir, "farm.nav"), b = Path.Combine(dir, "hunt.nav");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        Check.False(ProfileFileOps.Rename(a, b, out string error), "a name taken");
        Check.True(error.Contains("already exists"), error);
        Check.Eq(File.ReadAllText(b), "b", "the other file untouched");
        Check.True(ProfileFileOps.Rename(a, Path.Combine(dir, "Farm.nav"), out error), "a change of case: " + error);
        Check.Eq(Path.GetFileName(Directory.GetFiles(dir, "farm.nav").Single()), "Farm.nav", "the new case is on disk");
        Check.False(ProfileFileOps.Rename(Path.Combine(dir, "Farm.nav"), Path.Combine(dir, "Farm.nav"), out _), "the same name");
        Check.Eq(AllFiles(dir), 2, "nothing lost");
    }

    // ── Settings profiles ────────────────────────────────────────────────────

    private static void SettingsProfiles()
    {
        string root = Dir("settings");
        LegacyDashboardRenderer.ProfilesBase = Path.Combine(root, "SettingsProfiles");
        LegacyDashboardRenderer.MonstersBase = Path.Combine(root, "MonsterProfiles");
        string? buffs = BuffProfileStore.FolderOverride;
        BuffProfileStore.FolderOverride = Path.Combine(root, "BuffProfiles");
        try
        {
            FakeHost.WorldName = "TestWorld";
            var dash = Dash(root, out var s);
            FakeHost.WorldName = "TestWorld";
            dash.LoadSettings("Tester");
            string dir = Path.Combine(LegacyDashboardRenderer.ProfilesBase, "TestWorld", "Tester");

            ProfileResult r = dash.SaveProfileNow(ProfileKind.Settings);
            Check.True(r.Ok && File.Exists(Path.Combine(dir, "Default.json")), "Save writes Default.json: " + r.Text);

            r = dash.SaveProfileAs(ProfileKind.Settings, "monsters", replace: false);
            Check.False(r.Ok, "a name RynthAi keeps for itself is refused");
            Check.True(r.Text.Contains("monsters.json"), r.Text);
            Check.False(dash.SaveProfileAs(ProfileKind.Settings, "a/b", replace: false).Ok, "a folder in the name is refused");

            s.MonsterRange = 33;
            r = dash.SaveProfileAs(ProfileKind.Settings, "Farm", replace: false);
            Check.True(r.Ok, "Save As: " + r.Text);
            Check.Eq(s.SelectedProfile, "Farm", "Farm is in use");
            Check.True(File.Exists(Path.Combine(dir, "Farm.json")), "Farm.json");
            Check.True(dash.ReadDashboardFiles().Profiles.Contains("Farm"), "listed");

            // Back to Default; Save As Farm again asks first and writes nothing.
            dash.SelectProfileAtIndex(3, Array.IndexOf(dash.ReadDashboardFiles().Profiles, "Default"));
            Check.Eq(s.SelectedProfile, "Default", "Default again");
            string farmBefore = File.ReadAllText(Path.Combine(dir, "Farm.json"));
            r = dash.SaveProfileAs(ProfileKind.Settings, "Farm", replace: false);
            Check.True(r.NeedsReplace && !r.Ok, "asks before replacing: " + r.Text);
            Check.Eq(File.ReadAllText(Path.Combine(dir, "Farm.json")), farmBefore, "Farm untouched");
            Check.Eq(DeletedFiles(dir).Length, 0, "nothing kept yet");
            r = dash.SaveProfileAs(ProfileKind.Settings, "Farm", replace: true);
            Check.True(r.Ok, "replaced: " + r.Text);
            Check.Eq(DeletedFiles(dir).Length, 1, "the old Farm kept in Deleted");
            Check.Eq(File.ReadAllText(Path.Combine(dir, "Deleted", DeletedFiles(dir)[0])), farmBefore, "the old Farm's content");

            Check.Eq(s.SelectedProfile, "Farm", "Save As puts the replaced profile in use");
            dash.SelectProfileAtIndex(3, Array.IndexOf(dash.ReadDashboardFiles().Profiles, "Default"));
            Check.False(dash.RenameProfile(ProfileKind.Settings, "Other").Ok, "Default keeps its name");
            Check.False(File.Exists(Path.Combine(dir, "Other.json")), "nothing renamed");
            dash.SelectProfileAtIndex(3, Array.IndexOf(dash.ReadDashboardFiles().Profiles, "Farm"));
            Check.Eq(s.SelectedProfile, "Farm", "Farm again");
            Check.False(dash.RenameProfile(ProfileKind.Settings, "Default").Ok, "nothing can be renamed to Default");
            r = dash.RenameProfile(ProfileKind.Settings, "Hunt");
            Check.True(r.Ok, "rename: " + r.Text);
            Check.Eq(s.SelectedProfile, "Hunt", "Hunt in use");
            Check.False(File.Exists(Path.Combine(dir, "Farm.json")), "Farm.json renamed");
            Check.True(File.Exists(Path.Combine(dir, "Hunt.json")), "Hunt.json");
            Check.Eq(File.ReadAllText(Path.Combine(dir, "active_profile.txt")), "Hunt", "the active-profile marker follows");

            r = dash.DeleteProfile(ProfileKind.Settings);
            Check.True(r.Ok, "move to Deleted: " + r.Text);
            Check.Eq(s.SelectedProfile, "Default", "back on Default");
            Check.False(File.Exists(Path.Combine(dir, "Hunt.json")), "Hunt.json out of the list's folder");
            Check.True(DeletedFiles(dir).Any(f => f.StartsWith("Hunt.", StringComparison.Ordinal)), "Hunt in Deleted");
            Check.False(dash.ReadDashboardFiles().Profiles.Contains("Hunt"), "not listed");

            Check.False(dash.DeleteProfile(ProfileKind.Settings).Ok, "Default is never moved");
            Check.False(dash.RenameProfile(ProfileKind.Settings, "Main").Ok, "Default keeps its name");
            Check.True(File.Exists(Path.Combine(dir, "Default.json")), "Default.json stays");
        }
        finally { BuffProfileStore.FolderOverride = buffs; }
    }

    // ── Navs ─────────────────────────────────────────────────────────────────

    private static void NavProfiles()
    {
        string root = Dir("nav");
        var dash = Dash(root, out var s);
        string navs = Path.Combine(root, "NavProfiles");

        ProfileResult r = dash.NewProfile(ProfileKind.Nav, "Loop");
        Check.True(r.Ok, "New: " + r.Text);
        Check.Eq(s.CurrentNavPath, Path.Combine(navs, "Loop.nav"), "Loop is loaded");
        Check.False(dash.NewProfile(ProfileKind.Nav, "loop.nav").Ok, "New never opens an existing nav");

        // Save As onto another nav asks first; nothing is written. (The write itself is SaveRoute,
        // which saves to the install's NavProfiles, so it isn't run here.)
        File.WriteAllText(Path.Combine(navs, "Other.nav"), "keep me");
        r = dash.SaveProfileAs(ProfileKind.Nav, "Other", replace: false);
        Check.True(r.NeedsReplace, "asks: " + r.Text);
        Check.Eq(File.ReadAllText(Path.Combine(navs, "Other.nav")), "keep me", "Other untouched");
        Check.Eq(DeletedFiles(navs).Length, 0, "nothing kept yet");

        Check.False(dash.RenameProfile(ProfileKind.Nav, "Other").Ok, "rename onto a taken name is refused");
        r = dash.RenameProfile(ProfileKind.Nav, "Ring");
        Check.True(r.Ok, "rename: " + r.Text);
        Check.Eq(s.CurrentNavPath, Path.Combine(navs, "Ring.nav"), "Ring stays loaded");
        Check.True(dash.NavFileNames().Contains("Ring") && !dash.NavFileNames().Contains("Loop"), "the list follows");

        s.CurrentRoute.Points.Add(new NavPoint { NS = 1, EW = 2 });
        r = dash.DeleteProfile(ProfileKind.Nav);
        Check.True(r.Ok, "move to Deleted: " + r.Text);
        Check.Eq(s.CurrentNavPath, string.Empty, "no nav in use");
        Check.Eq(s.CurrentRoute.Points.Count, 0, "the route is cleared, as picking None");
        Check.False(dash.NavFileNames().Contains("Ring"), "not listed");
        Check.True(DeletedFiles(navs).Single().StartsWith("Ring.", StringComparison.Ordinal), "Ring in NavProfiles\\Deleted");
        Check.False(dash.DeleteProfile(ProfileKind.Nav).Ok, "nothing in use: nothing to move");
        Check.True(File.Exists(Path.Combine(navs, "Other.nav")), "Other is never touched");
    }

    // ── Metas ────────────────────────────────────────────────────────────────

    private static void MetaProfiles()
    {
        string root = Dir("meta");
        var dash = Dash(root, out var s);
        string metas = Path.Combine(root, "MetaFiles");

        ProfileResult r = dash.NewProfile(ProfileKind.Meta, "Farm");
        Check.True(r.Ok, "New: " + r.Text);
        Check.Eq(s.CurrentMetaPath, Path.Combine(metas, "Farm.af"), "Farm.af in use");
        Check.False(dash.NewProfile(ProfileKind.Meta, "Farm").Ok, "New never opens an existing meta");

        r = dash.SaveProfileAs(ProfileKind.Meta, "Copy", replace: false);
        Check.True(r.Ok, "Save As: " + r.Text);
        Check.Eq(s.CurrentMetaPath, Path.Combine(metas, "Copy.af"), "Copy.af in use");
        Check.True(File.Exists(Path.Combine(metas, "Copy.af")), "written");

        string farmBefore = File.ReadAllText(Path.Combine(metas, "Farm.af"));
        r = dash.SaveProfileAs(ProfileKind.Meta, "Farm", replace: false);
        Check.True(r.NeedsReplace, "asks before replacing Farm: " + r.Text);
        r = dash.SaveProfileAs(ProfileKind.Meta, "Farm", replace: true);
        Check.True(r.Ok, "replaced: " + r.Text);
        Check.Eq(File.ReadAllText(Path.Combine(metas, "Deleted", DeletedFiles(metas).Single())), farmBefore, "Farm's old copy kept");

        r = dash.SaveProfileNow(ProfileKind.Meta);
        Check.True(r.Ok, "Save: " + r.Text);

        r = dash.RenameProfile(ProfileKind.Meta, "Run.af");
        Check.True(r.Ok, "rename: " + r.Text);
        Check.Eq(s.CurrentMetaPath, Path.Combine(metas, "Run.af"), "Run.af in use");
        Check.True(dash.MetaFileNames().Contains("Run.af") && !dash.MetaFileNames().Contains("Farm.af"), "the list follows");

        r = dash.DeleteProfile(ProfileKind.Meta);
        Check.True(r.Ok, "move to Deleted: " + r.Text);
        Check.Eq(s.CurrentMetaPath, string.Empty, "no meta in use");
        Check.Eq(s.MetaRules.Count, 0, "unloaded, as picking None");
        Check.Eq(DeletedFiles(metas).Length, 2, "Farm's old copy and Run");
        Check.True(File.Exists(Path.Combine(metas, "Copy.af")), "Copy untouched");
        Check.False(dash.SaveProfileNow(ProfileKind.Meta).Ok, "no file: Save says to use Save As");
    }

    // ── Loot profiles ────────────────────────────────────────────────────────

    private static void LootProfiles()
    {
        string root = Dir("loot");
        var dash = Dash(root, out var s);
        string loot = Path.Combine(root, "LootProfiles");

        Check.False(dash.SaveProfileAs(ProfileKind.Loot, "Copy", replace: false).Ok, "no loot profile in use: nothing to copy");

        string snob = Path.Combine(loot, "Snob.utl");
        VTankLootWriter.Save(new VTankLootProfile(), snob);
        s.CurrentLootPath = snob;
        ProfileResult r = dash.SaveProfileAs(ProfileKind.Loot, "Snob Copy.utl", replace: false);
        Check.True(r.Ok, "Save As copies: " + r.Text);
        string copy = Path.Combine(loot, "Snob Copy.utl");
        Check.Eq(s.CurrentLootPath, copy, "the copy is in use");
        Check.Eq(File.ReadAllText(copy), File.ReadAllText(snob), "the same rules");
        Check.True(dash.ReadDashboardFiles().Loots.Contains("Snob Copy.utl"), "listed");
        Check.True(dash.SaveProfileAs(ProfileKind.Loot, "Snob", replace: false).NeedsReplace, "asks before replacing Snob");

        Check.NotNull(dash.NewLootProfilePath("Snob", out _), "New refuses a name taken");
        Check.Null(dash.NewLootProfilePath("Fresh", out string fresh), "a free name");
        Check.Eq(fresh, Path.Combine(loot, "Fresh.utl"), "a .utl in LootProfiles");
        Check.False(File.Exists(fresh), "nothing written (the Loot Editor's Save creates it)");

        r = dash.RenameProfile(ProfileKind.Loot, "Mine");
        Check.True(r.Ok, "rename: " + r.Text);
        Check.Eq(s.CurrentLootPath, Path.Combine(loot, "Mine.utl"), "Mine.utl in use");

        r = dash.DeleteProfile(ProfileKind.Loot);
        Check.True(r.Ok, "move to Deleted: " + r.Text);
        Check.Eq(s.CurrentLootPath, string.Empty, "no loot profile in use");
        Check.Eq(s.LootProfileIdx, 0, "the picker shows None");
        Check.True(DeletedFiles(loot).Single().StartsWith("Mine.", StringComparison.Ordinal), "Mine in LootProfiles\\Deleted");
        Check.True(File.Exists(snob), "Snob untouched");
    }

    // ── Buff profiles (the Spells window) ────────────────────────────────────

    private static void BuffDelete()
    {
        string root = Dir("buffs");
        string? previous = BuffProfileStore.FolderOverride;
        BuffProfileStore.FolderOverride = root;
        try
        {
            string path = BuffProfileStore.PathFor("Mage");
            BuffProfileStore.Save(new BuffProfile { Name = "Mage" }, path);
            Check.True(BuffProfileStore.Delete(path), "Delete works");
            Check.Eq(BuffProfileStore.ListFileNames().Count, 0, "off the list");
            Check.True(DeletedFiles(root).Single().StartsWith("Mage.", StringComparison.Ordinal), "kept in BuffProfiles\\Deleted");
            Check.False(BuffProfileStore.Delete(path), "a file that isn't there");
        }
        finally { BuffProfileStore.FolderOverride = previous; }
    }

    // ── The /ra verbs ────────────────────────────────────────────────────────

    private static void CommandsReply()
    {
        string root = Dir("commands");
        var dash = Dash(root, out var s);
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
        var plugin = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(plugin, new object[] { FakeHost.LastApi });
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(plugin, dash);
        MethodInfo dispatch = typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!;
        string Ra(string line)
        {
            FakeHost.Chat.Clear();
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            dispatch.Invoke(plugin, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
            return string.Join(" / ", FakeHost.Chat);
        }

        string chat = Ra("/ra nav new Loop");
        Check.True(chat.Contains("Created new nav 'Loop'"), "/ra nav new: " + chat);
        chat = Ra("/ra nav rename");
        Check.True(chat.Contains("needs a name") && chat.Contains("Usage: /ra nav"), "no name: why + usage: " + chat);
        chat = Ra("/ra nav rename Ring");
        Check.True(chat.Contains("Renamed Loop.nav to Ring.nav"), "/ra nav rename: " + chat);
        chat = Ra("/ra nav delete");
        Check.True(chat.Contains("Moved Ring.nav to NavProfiles\\Deleted"), "/ra nav delete: " + chat);
        chat = Ra("/ra nav delete");
        Check.True(chat.Contains("No nav file in use") && chat.Contains("Usage:"), "nothing to move: why + usage: " + chat);

        chat = Ra("/ra meta new Farm");
        Check.True(chat.Contains("Created new meta 'Farm'"), "/ra meta new: " + chat);
        File.WriteAllText(Path.Combine(root, "MetaFiles", "Taken.af"), RynthCore.Plugin.RynthAi.Meta.AfFileWriter.BlankMetaText());
        chat = Ra("/ra meta saveas Taken");
        Check.True(chat.Contains("commands never replace a file"), "saveas never replaces: " + chat);
        chat = Ra("/ra meta saveas Night Run");
        Check.True(chat.Contains("Saved the meta as Night Run.af"), "/ra meta saveas: " + chat);
        Check.Eq(s.CurrentMetaPath, Path.Combine(root, "MetaFiles", "Night Run.af"), "a name with a space");
        chat = Ra("/ra meta bogus");
        Check.True(chat.Contains("Unknown /ra meta verb") && chat.Contains("saveas <name>"), "the usage lists the new verbs: " + chat);

        chat = Ra("/ra loot saveas Copy");
        Check.True(chat.Contains("No loot profile in use to copy") && chat.Contains("Usage: /ra loot"), "/ra loot saveas: " + chat);
        chat = Ra("/ra settings rename X");
        Check.True(chat.Contains("No settings profile file in use") || chat.Contains("Default"), "/ra settings rename before login: " + chat);
        chat = Ra("/ra settings");
        Check.True(chat.Contains("Usage: /ra settings") && chat.Contains("saveas"), "/ra settings usage: " + chat);
    }
}

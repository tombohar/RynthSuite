using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using RynthCore.Loot.Editing;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-13: players reported metas and navs "not saving" and the plugin folders being
// "read only". Every save of a meta, nav, settings profile (and the loot editor's autosave
// path) swallowed its exception, so a folder the client can't write looked like a save that did
// nothing. Plugins/Shared/DataWriteGuard.cs now:
//  - clears the ReadOnly attribute on our own file before overwriting it;
//  - reports a failed save in chat once per folder (path, reason, fix) and logs every one
//    (throttled per folder);
//  - runs a startup self-check of the data folders.
// Only scratch files are written; nothing here changes permissions (a file held open with
// FileShare.None stands in for "Windows refused the write").
internal static class SaveFailureTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("save failure: exceptions are classified (access denied, read-only, in use, disk full, cloud, missing)", Classify);
        r.Add("save failure: reported in chat once per folder, with the path, the reason and the fix", OncePerFolder);
        r.Add("save failure: a folder inside a reported one isn't announced again", SubfolderQuiet);
        r.Add("save failure: the log is throttled per folder and counts what it skipped", LogThrottled);
        r.Add("save failure: an explicit save shows it itself (nothing queued, reason has the fix)", CallerShows);
        r.Add("save failure: a file open in another program is reported as in use (real file)", RealFileInUse);
        r.Add("save failure: a read-only .af meta is overwritten (attribute cleared)", ReadOnlyMetaSaved);
        r.Add("save failure: a read-only .nav is overwritten (attribute cleared)", ReadOnlyNavSaved);
        r.Add("save failure: a read-only settings profile is autosaved (attribute cleared)", ReadOnlySettingsSaved);
        r.Add("save failure: a settings autosave that fails is reported and retried later, not every tick", SettingsFailureReported);
        r.Add("save failure: /ra nav save to a file in use says NOT saved and why", NavSaveRouteReports);
        r.Add("save failure: a read-only loot profile is saved; a failed loot write hands over its exception", LootSession);
        r.Add("save failure: startup self-check passes writable folders and flags an unusable one", SelfCheck);
        r.Add("save failure: the data folders checked are under the resolved RynthAi folder", DataFoldersResolved);
    }

    private static string NewDir(string name)
    {
        string dir = Path.Combine(Program.TempRoot, "save-failure", name);
        if (Directory.Exists(dir)) { ClearAttributes(dir); Directory.Delete(dir, recursive: true); }
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void ClearAttributes(string dir)
    {
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
    }

    private static List<string> Fresh()
    {
        DataWriteGuard.Reset();
        DataWriteGuard.Clock = () => Environment.TickCount64;
        var log = new List<string>();
        DataWriteGuard.LogSink = log.Add;
        return log;
    }

    private static List<string> Chat()
    {
        var lines = new List<string>();
        DataWriteGuard.DrainChat(lines.Add);
        return lines;
    }

    private static IOException Io(int hr) => new IOException("io", hr);

    private static void Classify()
    {
        string dir = NewDir("classify");
        string ro = Path.Combine(dir, "ro.af");
        File.WriteAllText(ro, "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        try
        {
            Check.Eq(DataWriteGuard.Classify(new UnauthorizedAccessException(), ro), WriteFailureKind.ReadOnlyFile, "denied on a read-only file");
            Check.Eq(DataWriteGuard.Classify(new UnauthorizedAccessException(), Path.Combine(dir, "new.af")), WriteFailureKind.AccessDenied, "denied, no such file");
            Check.Eq(DataWriteGuard.Classify(new UnauthorizedAccessException(), null), WriteFailureKind.AccessDenied, "denied, no path");
        }
        finally { File.SetAttributes(ro, FileAttributes.Normal); }
        Check.Eq(DataWriteGuard.Classify(Io(unchecked((int)0x80070020)), null), WriteFailureKind.InUse, "sharing violation");
        Check.Eq(DataWriteGuard.Classify(Io(unchecked((int)0x80070021)), null), WriteFailureKind.InUse, "lock violation");
        Check.Eq(DataWriteGuard.Classify(Io(unchecked((int)0x80070070)), null), WriteFailureKind.DiskFull, "disk full");
        Check.Eq(DataWriteGuard.Classify(Io(unchecked((int)0x8007016A)), null), WriteFailureKind.CloudProvider, "OneDrive not running");
        Check.Eq(DataWriteGuard.Classify(Io(unchecked((int)0x80070005)), null), WriteFailureKind.AccessDenied, "E_ACCESSDENIED as IOException");
        Check.Eq(DataWriteGuard.Classify(new DirectoryNotFoundException(), null), WriteFailureKind.PathMissing, "missing folder");
        Check.Eq(DataWriteGuard.Classify(new InvalidOperationException("boom"), null), WriteFailureKind.Other, "anything else");
        Check.True(DataWriteGuard.Describe(WriteFailureKind.AccessDenied, null).Contains("Controlled folder access"), "access denied names Controlled folder access");
        Check.True(DataWriteGuard.Describe(WriteFailureKind.Other, new Exception("the text")) == "the text", "other: the exception's message");
    }

    private static void OncePerFolder()
    {
        var log = Fresh();
        string a = @"C:\Nowhere\RynthAi\MetaFiles\one.af";
        string b = @"C:\Nowhere\RynthAi\MetaFiles\two.af";
        string c = @"C:\Nowhere\RynthAi\NavProfiles\route.nav";
        Action denied = () => throw new UnauthorizedAccessException("Access to the path is denied.");

        Check.False(DataWriteGuard.TrySave("the meta", a, denied), "first save fails");
        Check.False(DataWriteGuard.TrySave("the meta", b, denied), "second save, same folder, fails");
        var lines = Chat();
        Check.Eq(lines.Count, 1, "one chat line for the folder");
        Check.True(lines[0].Contains(a), "names the file: " + lines[0]);
        Check.True(lines[0].Contains("the meta"), "says what wasn't saved");
        Check.True(lines[0].Contains("access denied"), "says why");
        Check.True(lines[0].Contains("Fix folder permissions"), "points at the launcher's repair");

        Check.False(DataWriteGuard.TrySave("the nav", c, denied), "nav folder fails");
        lines = Chat();
        Check.Eq(lines.Count, 1, "another folder gets its own line");
        Check.True(lines[0].Contains("the nav") && lines[0].Contains(c), "the nav line: " + (lines.Count > 0 ? lines[0] : ""));
        Check.Eq(DataWriteGuard.PendingCount, 0, "drained");
        Check.True(log.Count >= 2, "every folder logged");
    }

    private static void SubfolderQuiet()
    {
        Fresh();
        Action denied = () => throw new UnauthorizedAccessException();
        DataWriteGuard.TrySave("the settings profile", @"C:\Nowhere\RynthAi\SettingsProfiles\x.json", denied);
        DataWriteGuard.TrySave("the settings profile", @"C:\Nowhere\RynthAi\SettingsProfiles\Server\Toon\Default.json", denied);
        DataWriteGuard.TrySave("the settings profile", @"C:\Nowhere\RynthAi\SettingsProfilesOther\a.json", denied);
        Check.Eq(Chat().Count, 2, "the character folder is inside SettingsProfiles; a look-alike sibling is not");
    }

    private static void LogThrottled()
    {
        var log = Fresh();
        long now = 1_000_000;
        DataWriteGuard.Clock = () => now;
        Action denied = () => throw new UnauthorizedAccessException();
        string p = @"C:\Nowhere\RynthAi\MetaFiles\a.af";
        DataWriteGuard.TrySave("the meta", p, denied);
        now += 1000;
        DataWriteGuard.TrySave("the meta", p, denied);
        DataWriteGuard.TrySave("the meta", p, denied);
        Check.Eq(log.Count, 1, "one log line within the minute");
        now += DataWriteGuard.LogThrottleMs;
        DataWriteGuard.TrySave("the meta", p, denied);
        Check.Eq(log.Count, 2, "a minute later it logs again");
        Check.True(log[1].Contains("2 more failure(s)"), "and counts the skipped ones: " + log[1]);
        DataWriteGuard.Clock = () => Environment.TickCount64;
    }

    private static void CallerShows()
    {
        Fresh();
        bool ok = DataWriteGuard.TrySave("the nav", @"C:\Nowhere\RynthAi\NavProfiles\a.nav",
            () => throw new UnauthorizedAccessException(), out string why, callerShows: true);
        Check.False(ok, "fails");
        Check.Eq(DataWriteGuard.PendingCount, 0, "nothing queued: the caller shows it");
        Check.True(why.Contains("access denied") && why.Contains("Fix folder permissions"), "reason and fix: " + why);
        DataWriteGuard.TrySave("the nav", @"C:\Nowhere\RynthAi\NavProfiles\b.nav", () => throw new UnauthorizedAccessException());
        Check.Eq(DataWriteGuard.PendingCount, 0, "the folder already counts as reported");
    }

    private static void RealFileInUse()
    {
        Fresh();
        string dir = NewDir("inuse");
        string path = Path.Combine(dir, "farm.af");
        File.WriteAllText(path, "old");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool ok = DataWriteGuard.TrySave("the meta", path, () => File.WriteAllText(path, "new"), out string why);
            Check.False(ok, "write refused while another handle holds it");
            Check.True(why.Contains("open in another program"), "in use: " + why);
        }
        var lines = Chat();
        Check.Eq(lines.Count, 1, "one chat line");
        Check.True(lines.Count == 1 && lines[0].Contains("Close the program"), "with the in-use hint");
        Check.Eq(File.ReadAllText(path), "old", "file untouched");
    }

    private static void ReadOnlyMetaSaved()
    {
        Fresh();
        string dir = NewDir("ro-meta");
        string path = Path.Combine(dir, "farm.af");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var rules = new List<MetaRule> { new MetaRule { State = "Default" } };
        bool ok = DataWriteGuard.TrySave("the meta", path, () => AfFileWriter.Save(path, rules, new Dictionary<string, List<string>>()));
        Check.True(ok, "saved");
        Check.True(File.ReadAllText(path).Contains("STATE:"), "new content written");
        Check.Eq(File.GetAttributes(path) & FileAttributes.ReadOnly, (FileAttributes)0, "read-only cleared");
        Check.Eq(DataWriteGuard.PendingCount, 0, "nothing to report");
    }

    private static void ReadOnlyNavSaved()
    {
        string dir = NewDir("ro-nav");
        string path = Path.Combine(dir, "route.nav");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var route = new NavRouteParser();
        route.Points.Add(new NavPoint());
        route.Save(path);   // NavRouteParser clears it itself (it is linked into tools without the guard)
        Check.False(File.ReadAllText(path) == "old", "new content written");
        Check.Eq(File.GetAttributes(path) & FileAttributes.ReadOnly, (FileAttributes)0, "read-only cleared");
    }

    private static LegacyDashboardRenderer DashboardSavingTo(string path)
    {
        FakeHost.Reset();
        var dash = new LegacyDashboardRenderer(FakeHost.Create());
        typeof(LegacyDashboardRenderer).GetField("_settingsFilePath", Any)!.SetValue(dash, path);
        return dash;
    }

    private static void ReadOnlySettingsSaved()
    {
        Fresh();
        string dir = NewDir("ro-settings");
        string path = Path.Combine(dir, "Default.json");
        File.WriteAllText(path, "{}");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var dash = DashboardSavingTo(path);
        dash.TickAutoSave();
        Check.True(File.ReadAllText(path).Length > 2, "settings written");
        Check.Eq(File.GetAttributes(path) & FileAttributes.ReadOnly, (FileAttributes)0, "read-only cleared");
        Check.Eq(DataWriteGuard.PendingCount, 0, "nothing to report");
    }

    private static void SettingsFailureReported()
    {
        var log = Fresh();
        string dir = NewDir("settings-fail");
        string path = Path.Combine(dir, "Default.json");
        File.WriteAllText(path, "{}");
        var dash = DashboardSavingTo(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            dash.TickAutoSave();
            int logged = log.Count;
            dash.TickAutoSave();   // within the retry window: no second attempt
            Check.Eq(log.Count, logged, "no retry on the next tick");
            Check.True(log.Count == 1 && log[0].Contains(path), "logged with the path");
        }
        var lines = Chat();
        Check.Eq(lines.Count, 1, "one chat line");
        Check.True(lines.Count == 1 && lines[0].Contains("the settings profile") && lines[0].Contains(path), "names it: " + (lines.Count > 0 ? lines[0] : ""));
        Check.Eq(File.ReadAllText(path), "{}", "nothing written while held");
        // After the retry window it saves.
        typeof(LegacyDashboardRenderer).GetField("_autosaveRetryAt", Any)!.SetValue(dash, 0L);
        dash.TickAutoSave();
        Check.True(File.ReadAllText(path).Length > 2, "saved once the file is free");
    }

    private static void NavSaveRouteReports()
    {
        Fresh();
        string dir = NewDir("nav-saveroute");
        string path = Path.Combine(dir, "Route.nav");
        File.WriteAllText(path, "old");
        var settings = new LegacyUiSettings { CurrentNavPath = path };
        settings.CurrentRoute.Points.Add(new NavPoint());
        var ui = new LegacyNavigationUi(settings, FakeHost.Create());
        string msg;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            msg = ui.SaveRoute();
        Check.True(msg.Contains("NOT saved") && msg.Contains(path) && msg.Contains("open in another program"), "says so: " + msg);
        Check.Eq(DataWriteGuard.PendingCount, 0, "the command's reply is the report");
        msg = ui.SaveRoute();
        Check.True(msg.Contains("Saved 1 waypoints"), "saves once free: " + msg);
    }

    private static void LootSession()
    {
        string dir = NewDir("loot");
        string path = Path.Combine(dir, "profile.utl");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "salvage-combine.utl"), path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var s = new LootEditSession();
        Check.True(s.Open(path), "opens: " + s.Message);
        Check.True(s.Save(force: true), "saves a read-only profile: " + s.Message);
        Check.Eq(File.GetAttributes(path) & FileAttributes.ReadOnly, (FileAttributes)0, "read-only cleared");
        Check.Null(s.TakeWriteError(), "no write error");

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check.False(s.Save(force: true), "a held file fails");
        Exception? err = s.TakeWriteError();
        Check.NotNull(err, "the write's exception is handed over");
        Check.Null(s.TakeWriteError(), "once");
        if (err != null)
            Check.Eq(DataWriteGuard.Classify(err, path), WriteFailureKind.InUse, "classified as in use");
        foreach (string f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
    }

    private static void SelfCheck()
    {
        var log = Fresh();
        string dir = NewDir("selfcheck");
        string ok1 = Path.Combine(dir, "MetaFiles");
        string ok2 = Path.Combine(dir, "NavProfiles");   // created by the check
        string blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "a file where a folder should be");
        string bad = Path.Combine(blocker, "LootProfiles");
        Directory.CreateDirectory(ok1);

        var problems = DataWriteGuard.CheckFolders(new[] { ok1, ok2, bad });
        Check.Eq(problems.Count, 1, "one problem");
        Check.True(problems.Count == 1 && problems[0].Folder == bad, "the unusable folder");
        Check.True(Directory.Exists(ok2), "missing folder created");
        Check.Eq(Directory.GetFiles(ok1).Length + Directory.GetFiles(ok2).Length, 0, "no probe files left behind");
        var lines = Chat();
        Check.Eq(lines.Count, 1, "one chat line");
        Check.True(lines.Count == 1 && lines[0].Contains(bad) && lines[0].Contains("will NOT be kept"), "says what it means: " + (lines.Count > 0 ? lines[0] : ""));
        Check.True(log.Count == 1 && log[0].Contains(bad), "logged");

        DataWriteGuard.TrySave("the loot profile", Path.Combine(bad, "x.utl"), () => throw new UnauthorizedAccessException());
        Check.Eq(DataWriteGuard.PendingCount, 0, "a later save there isn't announced twice");
    }

    private static void DataFoldersResolved()
    {
        string root = RynthCore.Install.RynthInstallPaths.RynthAiDir;
        var folders = new List<string>(RynthAiPlugin.DataFolders());
        Check.Eq(folders.Count, 5, "root + MetaFiles, NavProfiles, LootProfiles, SettingsProfiles");
        foreach (string f in folders)
            Check.True(f.StartsWith(root, StringComparison.OrdinalIgnoreCase), f + " is under " + root);
        Check.Eq(LegacyNavigationUi.NavFolder, Path.Combine(root, "NavProfiles"), "/ra nav save uses the resolved folder");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// Where RynthAi finds navs (2026-11-22): NavFileList (the picker lists NavProfiles' subfolders as
// "Folder\Name"; not Deleted, not VirindiTank's "--" state files there; /ra nav load by either
// name), its folder stamp (a file added to a subfolder is seen), StrayNavs (VirindiTank navs in
// RynthNav's NavData or another folder the picker doesn't show: found, said, copied in, never over
// a file), and comma-decimal numbers in a nav. Scratch folders next to the exe; nothing else is
// written.
internal static class NavFolderTests
{
    public static void Register(Runner r)
    {
        r.Add("nav folder: subfolders listed as Folder\\Name after the top", ListsSubfolders);
        r.Add("nav folder: Deleted, dot folders and -- state files in subfolders are not listed", SkipsWhatItShould);
        r.Add("nav folder: every entry is NavProfiles\\<entry>.nav", EntryIsThePath);
        r.Add("nav folder: a file added to a subfolder changes the stamp", StampSeesSubfolders);
        r.Add("nav folder: /ra nav load finds Folder\\Name, a unique bare name, not an ambiguous one", FindByName);
        r.Add("stray navs: the folders looked in (NavData by setting, never NavProfiles)", CandidateFolders);
        r.Add("stray navs: VirindiTank navs found, not ones NavProfiles has, not -- or non-navs", FindsStrays);
        r.Add("stray navs: the line says where they are and where they go", NoticeText);
        r.Add("stray navs: copied in, never over a file, originals kept", CopiesIn);
        r.Add("nav numbers: a comma-decimal line reads as the decimal", CommaDecimals);
    }

    private static string Scratch(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "scratch-navfolder", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly string[] Nav =
    {
        "uTank2 NAV 1.2", "1", "2",
        "0", "12.5", "-30.25", "0.1", "0",
        "0", "13.5", "-31.25", "0.1", "0",
    };

    private static void WriteNav(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, Nav);
    }

    private static void ListsSubfolders()
    {
        string root = Scratch("list");
        WriteNav(Path.Combine(root, "Hunt.nav"));
        WriteNav(Path.Combine(root, "Alpha.nav"));
        WriteNav(Path.Combine(root, "VirindiTank", "Hive Loop.nav"));
        WriteNav(Path.Combine(root, "VirindiTank", "Town Run.nav"));
        WriteNav(Path.Combine(root, "Server", "Char", "Deep", "Too Deep", "Lost.nav"));   // past MaxDepth
        WriteNav(Path.Combine(root, "Server", "Char", "Ok.nav"));
        File.WriteAllText(Path.Combine(root, "notes.txt"), "not a nav");

        List<string> list = NavFileList.List(root);
        Check.Eq(string.Join("|", list),
            "Alpha|Hunt|Server\\Char\\Ok|VirindiTank\\Hive Loop|VirindiTank\\Town Run",
            "the top first (sorted), then the subfolders' as Folder\\Name, sorted, three levels at most");
        Check.Eq(NavFileList.List(Path.Combine(root, "missing")).Count, 0, "a missing folder lists nothing");
    }

    private static void SkipsWhatItShould()
    {
        string root = Scratch("skip");
        WriteNav(Path.Combine(root, "--Top_Server.nav"));                          // at the top: listed as before
        WriteNav(Path.Combine(root, "VirindiTank", "--Someone_Server.nav"));       // a VTank state file
        WriteNav(Path.Combine(root, "VirindiTank", "Real.nav"));
        WriteNav(Path.Combine(root, NavFileList.DeletedFolder, "Old.nav"));
        WriteNav(Path.Combine(root, ".git", "Hidden.nav"));

        List<string> list = NavFileList.List(root);
        Check.True(list.Contains("--Top_Server"), "a -- nav at the top is still listed (as before)");
        Check.False(list.Any(n => n.Contains("--Someone")), "a -- state file in a subfolder is not");
        Check.True(list.Contains("VirindiTank\\Real"), "the real nav beside it is");
        Check.False(list.Any(n => n.StartsWith("Deleted", StringComparison.OrdinalIgnoreCase)), "the Deleted folder is not listed");
        Check.False(list.Any(n => n.StartsWith(".git", StringComparison.Ordinal)), "a dot folder is not listed");
    }

    private static void EntryIsThePath()
    {
        string root = Scratch("paths");
        WriteNav(Path.Combine(root, "Top.nav"));
        WriteNav(Path.Combine(root, "A", "B", "Inner.nav"));
        foreach (string name in NavFileList.List(root))
        {
            string file = Path.Combine(root, name + ".nav");
            Check.True(File.Exists(file), $"'{name}' is a file");
            Check.Eq(NavRouteParser.Load(file).Points.Count, 2, $"'{name}' loads");
        }
    }

    private static void StampSeesSubfolders()
    {
        string root = Scratch("stamp");
        WriteNav(Path.Combine(root, "Sub", "First.nav"));
        DateTime before = NavFileList.Stamp(root);
        // Make the subfolder's own time older, then add a file to it: only it changes.
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "Sub"), before.AddMinutes(-10));
        Directory.SetLastWriteTimeUtc(root, before.AddMinutes(-10));
        DateTime old = NavFileList.Stamp(root);
        WriteNav(Path.Combine(root, "Sub", "Second.nav"));
        Directory.SetLastWriteTimeUtc(root, before.AddMinutes(-10));   // the top doesn't move
        DateTime after = NavFileList.Stamp(root);
        Check.True(after > old, "a nav added to a subfolder moves the stamp");
        Check.Eq(NavFileList.Stamp(Path.Combine(root, "missing")), DateTime.MinValue, "a missing folder: MinValue");
    }

    private static void FindByName()
    {
        var names = new List<string> { "None", "Hunt", "VirindiTank\\Hive Loop", "A\\Same", "B\\Same" };
        Check.Eq(NavFileList.Find(names, "VirindiTank\\Hive Loop", out _), "VirindiTank\\Hive Loop", "the entry itself");
        Check.Eq(NavFileList.Find(names, "virinditank/hive loop", out _), "VirindiTank\\Hive Loop", "any case, / works too");
        Check.Eq(NavFileList.Find(names, "Hive Loop", out int one), "VirindiTank\\Hive Loop", "a bare name only one subfolder has");
        Check.Eq(one, 1, "one match");
        Check.Null(NavFileList.Find(names, "Same", out int two), "a bare name two subfolders have: none picked");
        Check.Eq(two, 2, "two matches counted");
        Check.Eq(string.Join("|", NavFileList.SameName(names, "same")), "A\\Same|B\\Same", "the ones it could be");
        Check.Eq(NavFileList.Find(names, "Hunt", out _), "Hunt", "a top name");
        Check.Null(NavFileList.Find(names, "Nope", out int none), "no such nav");
        Check.Eq(none, 0, "no matches");
        Check.Null(NavFileList.Find(names, "Other\\Hive Loop", out _), "a folder that isn't there");
    }

    private static void CandidateFolders()
    {
        string root = Scratch("candidates");
        string json = Path.Combine(root, "rynthnav.json");
        File.WriteAllText(json, "{ \"navDataDir\": \"D:\\\\AC\\\\NavData\", // comment\n }");
        List<string> list = StrayNavs.CandidateFolders(@"C:\Games\RynthCore", @"C:\Games\RynthSuite", @"C:\Games\RynthSuite\RynthAi",
            navDataEnv: @"E:\Env\NavData", rynthNavSettingsPath: json);
        Check.Eq(list[0], @"E:\Env\NavData", "the RYNTHNAV_NAVDATA folder first");
        Check.Eq(list[1], @"D:\AC\NavData", "then rynthnav.json's navDataDir");
        Check.True(list.Contains(@"C:\Games\RynthCore\NavData"), "the default NavData");
        Check.True(list.Contains(@"C:\Games\RynthSuite\RynthAi") && list.Contains(@"C:\Games\RynthSuite\RynthNav"), "RynthAi's and RynthNav's folders");
        Check.Eq(list.Count(d => d.Equals(@"C:\Games\RynthCore\NavData", StringComparison.OrdinalIgnoreCase)), 1, "no duplicates");
        Check.False(list.Any(d => d.EndsWith("NavProfiles", StringComparison.OrdinalIgnoreCase)), "never NavProfiles");
        List<string> plain = StrayNavs.CandidateFolders(@"C:\Games\RynthCore", @"C:\Games\RynthSuite", @"C:\Games\RynthSuite\RynthAi", "relative\\dir", null);
        Check.Eq(plain[0], @"C:\Games\RynthCore\NavData", "a variable that isn't a full path is ignored");
        Check.Null(StrayNavs.ReadNavDataDir("not json"), "bad json: none");
    }

    private static void FindsStrays()
    {
        string root = Scratch("strays");
        string navData = Path.Combine(root, "NavData");
        string navProfiles = Path.Combine(root, "RynthAi", "NavProfiles");
        WriteNav(Path.Combine(navData, "Hive Loop.nav"));
        WriteNav(Path.Combine(navData, "Already Here.nav"));
        WriteNav(Path.Combine(navData, "--Someone_Server.nav"));
        File.WriteAllText(Path.Combine(navData, "nav_ABCD.nav"), "not a vtank nav");
        File.WriteAllText(Path.Combine(navData, "nav_ABCD.tile"), "tile");
        WriteNav(Path.Combine(navProfiles, "Already Here.nav"));
        WriteNav(Path.Combine(navProfiles, "Sub", "Inside.nav"));

        List<StrayNav> found = StrayNavs.Find(new[] { navData, navProfiles, Path.Combine(navProfiles, "Sub"), Path.Combine(root, "missing") }, navProfiles);
        Check.Eq(string.Join("|", found.Select(s => s.Name)), "Hive Loop", "only the VirindiTank nav NavProfiles doesn't have");
        Check.Eq(found.Count > 0 ? found[0].Folder : "", Path.GetFullPath(navData), "with its folder");
    }

    private static void NoticeText()
    {
        string profiles = @"C:\Games\RynthSuite\RynthAi\NavProfiles";
        Check.Eq(StrayNavs.Notice(new List<StrayNav>(), profiles), "", "none: no line");
        var one = new List<StrayNav> { new(@"C:\Games\RynthCore\NavData\A.nav", @"C:\Games\RynthCore\NavData", "A") };
        string text = StrayNavs.Notice(one, profiles);
        Check.True(text.StartsWith("1 VirindiTank nav is in C:\\Games\\RynthCore\\NavData (RynthNav's map data folder)", StringComparison.Ordinal), "one, where, and what that folder is: " + text);
        Check.True(text.Contains("RynthAi's navs go in " + profiles), "where they go");
        var many = new List<StrayNav>
        {
            new(@"C:\X\A.nav", @"C:\X", "A"), new(@"C:\X\B.nav", @"C:\X", "B"), new(@"C:\Y\C.nav", @"C:\Y", "C"),
        };
        string m = StrayNavs.Notice(many, profiles);
        Check.True(m.StartsWith("3 VirindiTank navs are in C:\\X (2) and 1 other folder,", StringComparison.Ordinal), "the folder with most, then the rest: " + m);
        Check.False(m.Contains("RynthNav's map data"), "not NavData: no note");
    }

    private static void CopiesIn()
    {
        string root = Scratch("copy");
        string from = Path.Combine(root, "NavData");
        string to = Path.Combine(root, "NavProfiles");
        WriteNav(Path.Combine(from, "One.nav"));
        WriteNav(Path.Combine(from, "Two.nav"));
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "Two.nav"), "mine");
        var strays = new List<StrayNav>
        {
            new(Path.Combine(from, "One.nav"), from, "One"),
            new(Path.Combine(from, "Two.nav"), from, "Two"),
        };
        StrayCopyResult r = StrayNavs.CopyInto(strays, to);
        Check.Eq(r.Copied, 1, "one copied");
        Check.Eq(r.AlreadyThere, 1, "one left alone");
        Check.Eq(File.ReadAllText(Path.Combine(to, "Two.nav")), "mine", "never over a file");
        Check.True(File.Exists(Path.Combine(from, "One.nav")), "the original is kept");
        Check.Eq(NavRouteParser.Load(Path.Combine(to, "One.nav")).Points.Count, 2, "the copy loads");
        string reply = StrayNavs.CopyReply(r, to);
        Check.True(reply.StartsWith("Copied 1 nav into ", StringComparison.Ordinal) && reply.Contains("1 left alone"), "the reply: " + reply);
        Check.True(StrayNavs.CopyReply(default, to).StartsWith("No VirindiTank navs outside NavProfiles", StringComparison.Ordinal), "nothing to copy: says so");

        var failing = StrayNavs.CopyInto(new List<StrayNav> { new(Path.Combine(from, "Three.nav"), from, "Three") }, to, (_, _) => "access denied");
        Check.Eq(failing.Failed, 1, "a failed copy is counted");
        Check.True(StrayNavs.CopyReply(failing, to).Contains("couldn't be copied: access denied"), "and said");
    }

    private static void CommaDecimals()
    {
        Check.Near(NavRouteParser.ParseNumber("12.5"), 12.5, 1e-12, "a dot decimal");
        Check.Near(NavRouteParser.ParseNumber("12,5"), 12.5, 1e-12, "a comma decimal (it read as 125)");
        Check.Near(NavRouteParser.ParseNumber(" -30,25 "), -30.25, 1e-12, "negative, spaces");
        Check.Near(NavRouteParser.ParseNumber("1E-05"), 0.00001, 1e-15, "an exponent");
        var lines = new List<string> { "uTank2 NAV 1.2", "1", "1", "0", "12,5", "-30,25", "0,001", "0" };
        var route = NavRouteParser.LoadFromLines(lines);
        Check.Null(route.LoadWarning, "loads clean");
        Check.Near(route.Points[0].EW, 12.5, 1e-9, "EW");
        Check.Near(route.Points[0].NS, -30.25, 1e-9, "NS");
        Check.Near(route.Points[0].Z, 0.001, 1e-12, "Z (it read as 1)");
    }
}

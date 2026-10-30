using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-13: "load makes a new one", as in VTank. /ra nav load <name> and /vt nav load create an
// empty Circular route when NavProfiles has no <name>.nav, and load it (so a route can be recorded
// into it); /ra meta load <name> and /vt meta load create a blank <name>.af (one state "Default", no
// rules) when MetaFiles has neither <name>.af nor <name>.met, and load it: the old rules are
// cleared and the meta is in Default. Existing files are loaded as before, never rewritten. Names
// that aren't plain file names are refused. Only scratch folders are used (the dashboard's
// NavProfiles / MetaFiles fields are pointed at them).
internal static class BlankLoadTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("blank load: /ra nav load <new name> creates an empty Circular nav and loads it", NavCreates);
        r.Add("blank load: /ra nav load of an existing nav loads it, file unchanged", NavExisting);
        r.Add("blank load: a blank nav round-trips through the nav parser (VTank header, 0 points)", NavRoundTrip);
        r.Add("blank load: /ra meta load <new name> creates a blank .af and loads it (rules cleared, state Default)", MetaCreates);
        r.Add("blank load: /ra meta load of an existing .af / .met loads it, files unchanged", MetaExisting);
        r.Add("blank load: /ra meta new creates only; an existing meta is left alone", MetaNew);
        r.Add("blank load: a blank .af round-trips through AfFileParser; a file with no state is still refused", MetaRoundTrip);
        r.Add("blank load: unsafe names are refused and nothing is written", UnsafeNames);
        r.Add("blank load: names are tidied (quotes, .nav/.af/.met)", Tidy);
        r.Add("blank load: /ra meta load|new|list reach the dashboard", MetaCommand);
    }

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new();
        public readonly LegacyDashboardRenderer Dash;
        public readonly string Root, Navs, Metas;

        public Rig(string name)
        {
            Root = Path.Combine(Program.TempRoot, "blank-load", name);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            Navs = Path.Combine(Root, "NavProfiles");
            Metas = Path.Combine(Root, "MetaFiles");
            Directory.CreateDirectory(Navs);
            Directory.CreateDirectory(Metas);

            Dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            var navFiles = new List<string>();
            Set("_settings", S);
            Set("_host", FakeHost.Create());
            Set("_navFolder", Navs);
            Set("_metaFolder", Metas);
            Set("_navFiles", navFiles);
            Set("_metaFiles", new List<string>());
            Set("_profileListsLock", new object());
            Set("_metaUi", new LegacyMetaUi(S, navFiles));
            // _settingsFilePath stays null: SaveSettings writes nothing.
        }

        private void Set(string f, object v) => typeof(LegacyDashboardRenderer).GetField(f, Any)!.SetValue(Dash, v);

        public string[] Files() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories);
    }

    private static List<MetaRule> HuntRules() => new()
    {
        new MetaRule { State = "Hunt", Condition = MetaConditionType.Always, Action = MetaActionType.ChatCommand, ActionData = "/ra start" },
        new MetaRule { State = "Hunt", Condition = MetaConditionType.Always, Action = MetaActionType.SetMetaState, ActionData = "Default" },
    };

    private static void NavCreates()
    {
        DataWriteGuard.Reset();
        var rig = new Rig("nav-create");
        rig.S.CurrentRoute.Points.Add(new NavPoint { NS = 1, EW = 2 });   // a route was loaded before
        rig.S.CurrentRoute.RouteType = NavRouteType.Linear;

        string msg = rig.Dash.LoadNavByName("Fresh Route");
        string path = Path.Combine(rig.Navs, "Fresh Route.nav");
        Check.Eq(msg, "[RynthAi] Created new nav 'Fresh Route' (empty, Circular) and loaded it.", "message");
        Check.True(File.Exists(path), "Fresh Route.nav written");
        Check.Eq(string.Join("|", File.ReadAllLines(path)), "uTank2 NAV 1.2|1|0", "VTank nav: header, Circular, 0 points");
        Check.Eq(rig.S.CurrentNavPath, path, "it is the loaded nav");
        Check.Eq(rig.S.CurrentRoute.Points.Count, 0, "the old route's points are gone");
        Check.Eq(rig.S.CurrentRoute.RouteType, NavRouteType.Circular, "Circular");
        Check.Eq(rig.S.ActiveNavIndex, 0, "at the start");
        Check.True(rig.Dash.NavFileNames().Contains("Fresh Route"), "listed by /ra nav list");

        // Loading it again finds it (no second create).
        msg = rig.Dash.LoadNavByName("fresh route");
        Check.Eq(msg, "[RynthAi] Loaded fresh route (0 waypoints).", "second load is a plain load: " + msg);
        Check.Eq(DataWriteGuard.PendingCount, 0, "nothing reported");
    }

    private static void NavExisting()
    {
        var rig = new Rig("nav-existing");
        string path = Path.Combine(rig.Navs, "Hive Loop.nav");
        var route = new NavRouteParser { RouteType = NavRouteType.Linear };
        route.Points.Add(new NavPoint { NS = 10.5, EW = -3.25 });
        route.Points.Add(new NavPoint { NS = 11.5, EW = -4.25 });
        route.Save(path);
        byte[] before = File.ReadAllBytes(path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);

        string msg = rig.Dash.LoadNavByName("\"Hive Loop.nav\"");
        Check.Eq(msg, "[RynthAi] Loaded Hive Loop (2 waypoints).", "loaded: " + msg);
        Check.Eq(rig.S.CurrentNavPath, path, "the existing file");
        Check.Eq(rig.S.CurrentRoute.RouteType, NavRouteType.Linear, "its own route type");
        Check.True(((ReadOnlySpan<byte>)File.ReadAllBytes(path)).SequenceEqual(before), "file bytes unchanged");
        Check.Eq(File.GetLastWriteTimeUtc(path), stamp, "file not rewritten");
        Check.Eq(Directory.GetFiles(rig.Navs).Length, 1, "no other file");
    }

    private static void NavRoundTrip()
    {
        NavRouteParser blank = NewProfileFiles.BlankNav();
        List<string> lines = blank.ToLines();
        Check.Eq(string.Join("|", lines), "uTank2 NAV 1.2|1|0", "lines");
        NavRouteParser back = NavRouteParser.LoadFromLines(lines);
        Check.Eq(back.RouteType, NavRouteType.Circular, "route type");
        Check.Eq(back.Points.Count, 0, "no points");
        Check.Null(back.LoadWarning, "no warning");

        string path = Path.Combine(Program.TempRoot, "blank-load", "roundtrip.nav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        blank.Save(path);
        NavRouteParser fromFile = NavRouteParser.Load(path);
        Check.Eq(fromFile.RouteType, NavRouteType.Circular, "file: route type");
        Check.Eq(fromFile.Points.Count, 0, "file: no points");
        Check.Null(fromFile.LoadWarning, "file: no warning");
        Check.Eq(string.Join("|", fromFile.ToLines()), "uTank2 NAV 1.2|1|0", "saves back the same");
    }

    private static void MetaCreates()
    {
        DataWriteGuard.Reset();
        var rig = new Rig("meta-create");
        rig.S.MetaRules = HuntRules();
        rig.S.EmbeddedNavs["old nav"] = new List<string> { "uTank2 NAV 1.2", "1", "0" };
        rig.S.CurrentState = "Hunt";
        rig.S.CurrentMetaPath = Path.Combine(rig.Metas, "old.af");

        string msg = rig.Dash.LoadMetaByName("'Blank Farm.af'");
        string path = Path.Combine(rig.Metas, "Blank Farm.af");
        Check.Eq(msg, "[RynthAi] Created new meta 'Blank Farm' (one state 'Default', no rules) and loaded it.", "message");
        Check.True(File.Exists(path), "Blank Farm.af written");
        Check.Eq(File.ReadAllText(path), AfFileWriter.BlankMetaText(), "the blank meta text");
        Check.Eq(rig.S.MetaRules.Count, 0, "old rules cleared");
        Check.Eq(rig.S.EmbeddedNavs.Count, 0, "old embedded navs cleared");
        Check.Eq(rig.S.CurrentState, "Default", "state Default");
        Check.True(rig.S.ForceStateReset, "state reset forced");
        Check.True(rig.S.MetaCallStackReset, "call stack reset");
        Check.Eq(rig.S.CurrentMetaPath, path, "it is the loaded meta");
        Check.True(rig.Dash.MetaFileNames().Contains("Blank Farm.af"), "listed by /ra meta list");
        Check.Eq(DataWriteGuard.PendingCount, 0, "nothing reported");

        // Without an extension, the same name is now found.
        msg = rig.Dash.LoadMetaByName("blank farm");
        Check.True(msg.StartsWith("[RynthAi] Loaded meta 'blank farm': 0 rules"), "second load is a plain load: " + msg);
        Check.Eq(Directory.GetFiles(rig.Metas).Length, 1, "one file");
    }

    private static void MetaExisting()
    {
        var rig = new Rig("meta-existing");
        string af = Path.Combine(rig.Metas, "Farm.af");
        AfFileWriter.Save(af, HuntRules(), new Dictionary<string, List<string>>());
        byte[] afBefore = File.ReadAllBytes(af);
        DateTime afStamp = File.GetLastWriteTimeUtc(af);

        string msg = rig.Dash.LoadMetaByName("Farm");
        Check.Eq(msg, "[RynthAi] Loaded meta 'Farm': 2 rules, 0 navs", "loaded: " + msg);
        Check.Eq(rig.S.MetaRules.Count, 2, "its rules");
        Check.Eq(rig.S.CurrentState, "Hunt", "its start state");
        Check.Eq(rig.S.CurrentMetaPath, af, "the .af");
        Check.True(((ReadOnlySpan<byte>)File.ReadAllBytes(af)).SequenceEqual(afBefore), ".af bytes unchanged");
        Check.Eq(File.GetLastWriteTimeUtc(af), afStamp, ".af not rewritten");

        // Only a .met by that name: loaded, no .af created beside it. (A .met that parses to no
        // rules is refused as before: "not loaded", and still no .af.)
        string met = Path.Combine(rig.Metas, "Old.met");
        File.WriteAllText(met, "garbage");
        byte[] metBefore = File.ReadAllBytes(met);
        msg = rig.Dash.LoadMetaByName("Old.met");
        Check.True(msg.StartsWith("[RynthAi] Meta 'Old' not loaded:"), "a .met with no rules is refused: " + msg);
        Check.False(File.Exists(Path.Combine(rig.Metas, "Old.af")), "no blank .af made for an existing .met");
        Check.True(((ReadOnlySpan<byte>)File.ReadAllBytes(met)).SequenceEqual(metBefore), ".met unchanged");
        Check.Eq(rig.S.MetaRules.Count, 2, "the loaded meta stays when a load is refused");
    }

    private static void MetaNew()
    {
        var rig = new Rig("meta-new");
        string af = Path.Combine(rig.Metas, "Farm.af");
        AfFileWriter.Save(af, HuntRules(), new Dictionary<string, List<string>>());
        byte[] before = File.ReadAllBytes(af);

        string msg = rig.Dash.LoadMetaByName("Farm", createOnly: true);
        Check.Eq(msg, "[RynthAi] Meta 'Farm.af' already exists. /ra meta load Farm loads it.", "refused: " + msg);
        Check.True(((ReadOnlySpan<byte>)File.ReadAllBytes(af)).SequenceEqual(before), "unchanged");
        Check.Eq(rig.S.MetaRules.Count, 0, "nothing loaded");

        msg = rig.Dash.LoadMetaByName("Next", createOnly: true);
        Check.Eq(msg, "[RynthAi] Created new meta 'Next' (one state 'Default', no rules) and loaded it.", "created: " + msg);
        Check.True(File.Exists(Path.Combine(rig.Metas, "Next.af")), "Next.af written");
    }

    private static void MetaRoundTrip()
    {
        string text = AfFileWriter.BlankMetaText();
        LoadedMeta m = AfFileParser.LoadFromText(text);
        Check.Eq(m.Rules.Count, 0, "no rules");
        Check.Eq(string.Join(",", m.DeclaredStates), "Default", "one state, Default");
        Check.Eq(m.Warnings.Count, 0, "no warnings");
        Check.True(m.IsBlank, "a blank meta");
        Check.Eq(m.StartState, "Default", "starts in Default");
        Check.Eq(AfFileWriter.SaveToString(m.Rules, m.EmbeddedNavs), text, "writes back the same (a meta whose last rule was deleted)");

        // A file with no state at all (a profile, an empty file) is still not a meta.
        LoadedMeta empty = AfFileParser.LoadFromText("");
        Check.False(empty.IsBlank, "an empty file isn't a blank meta");
        string dir = Path.Combine(Program.TempRoot, "blank-load", "roundtrip");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "empty.af");
        File.WriteAllText(path, "");
        var settings = new LegacyUiSettings { MetaRules = HuntRules() };
        var ui = new LegacyMetaUi(settings, new List<string>());
        Check.False(ui.LoadMacroFile(path), "an empty .af is refused");
        Check.Eq(settings.MetaRules.Count, 2, "rules kept");
        File.WriteAllText(path, text);
        Check.True(ui.LoadMacroFile(path), "the blank .af loads: " + ui.LastLoadStatus);
        Check.Eq(settings.MetaRules.Count, 0, "rules cleared");
        Check.Eq(settings.CurrentState, "Default", "Default");
    }

    private static void UnsafeNames()
    {
        var rig = new Rig("unsafe");
        string parent = Path.GetDirectoryName(rig.Root)!;
        int parentFiles = Directory.GetFiles(parent).Length;
        string[] bad =
        {
            "", "   ", "\"\"", @"..\evil", "../evil", "..", "sub/route", @"sub\route", "a:b", "what?", "star*",
            "pipe|x", "<x>", "CON", "nul", "com1.txt", "LPT9", "trailing.", "dots..inside", new string('x', 121),
        };
        foreach (string name in bad)
        {
            string nav = rig.Dash.LoadNavByName(name);
            Check.True(nav.StartsWith("[RynthAi] Can't use '") && nav.Contains("as a nav name"), $"nav '{name}' refused: {nav}");
            string meta = rig.Dash.LoadMetaByName(name);
            Check.True(meta.StartsWith("[RynthAi] Can't use '") && meta.Contains("as a meta name"), $"meta '{name}' refused: {meta}");
            string mnew = rig.Dash.LoadMetaByName(name, createOnly: true);
            Check.True(mnew.StartsWith("[RynthAi] Can't use '"), $"meta new '{name}' refused: {mnew}");
        }
        Check.Eq(rig.Files().Length, 0, "nothing written in the data folders");
        Check.Eq(Directory.GetFiles(parent).Length, parentFiles, "nothing written beside them");
        Check.False(File.Exists(Path.Combine(parent, "evil.nav")) || File.Exists(Path.Combine(parent, "evil.af")), "no escape from the folder");

        // Ordinary names with dots, spaces and dashes are fine.
        Check.Null(NewProfileFiles.Validate("Farm v2.1 - Hive"), "a normal name");
        Check.Null(NewProfileFiles.Validate("console"), "CON is reserved, console isn't");
    }

    private static void Tidy()
    {
        Check.Eq(NewProfileFiles.Tidy("  \"Hive Loop.nav\" ", NewProfileFiles.NavExtensions, out string e1), "Hive Loop", "quotes + .nav");
        Check.Eq(e1, ".nav", "extension seen");
        Check.Eq(NewProfileFiles.Tidy("'farm.MET'", NewProfileFiles.MetaExtensions, out string e2), "farm", "single quotes + .MET");
        Check.Eq(e2, ".met", "lower-cased");
        Check.Eq(NewProfileFiles.Tidy("farm.af", NewProfileFiles.MetaExtensions, out _), "farm", ".af");
        Check.Eq(NewProfileFiles.Tidy("route.nav", NewProfileFiles.MetaExtensions, out string e3), "route.nav", ".nav isn't a meta extension");
        Check.Eq(e3, "", "none");
        Check.Eq(NewProfileFiles.Tidy(".nav", NewProfileFiles.NavExtensions, out _), ".nav", "an extension alone isn't stripped to nothing");
    }

    private static void MetaCommand()
    {
        FakeHost.Reset();
        var rig = new Rig("meta-command");
        var plugin = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(plugin, new object[] { FakeHost.LastApi });
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(plugin, rig.Dash);
        MethodInfo dispatch = typeof(RynthAiPlugin).GetMethod("DispatchRaCommand", Any)!;
        void Ra(string line)
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            dispatch.Invoke(plugin, new object[] { parts[1].ToLowerInvariant(), parts, 0 });
        }

        Ra("/ra meta load Night Farm");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("Created new meta 'Night Farm' (one state 'Default', no rules) and loaded it.")),
            "/ra meta load creates: " + string.Join(" / ", FakeHost.Chat));
        Check.True(File.Exists(Path.Combine(rig.Metas, "Night Farm.af")), "file written");
        FakeHost.Chat.Clear();
        Ra("/ra meta new Night Farm");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("already exists")), "/ra meta new refuses an existing one");
        FakeHost.Chat.Clear();
        Ra("/ra meta list");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("Metas (1): Night Farm.af")), "/ra meta list: " + string.Join(" / ", FakeHost.Chat));
        FakeHost.Chat.Clear();
        Ra("/ra meta load");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("Usage: /ra meta load <name>")), "usage");
        FakeHost.Chat.Clear();
        Ra("/ra nav load New Route");
        Check.True(FakeHost.Chat.Exists(l => l.Contains("Created new nav 'New Route' (empty, Circular) and loaded it.")),
            "/ra nav load creates: " + string.Join(" / ", FakeHost.Chat));
    }
}

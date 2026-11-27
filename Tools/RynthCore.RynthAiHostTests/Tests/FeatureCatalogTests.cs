using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Features;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The feature catalog (Docs/features/*.json, embedded in RynthAi; Features/FeatureCatalog.cs), the
// Find a Feature window in the sections manifest, and /ra find.
//
// The catalog's rules (Docs/features schema): every file is embedded and parses; every entry has its
// required fields; ids are unique and carry their plugin's prefix; command and open start with '/';
// open never toggles anything; summaries are 200 characters at most; settings are LegacyUiSettings
// fields.
//
// Coverage, so the catalog can't fall behind the code: every /ra verb DispatchRaCommand handles,
// every window and view in RynthAi's sections manifest, and every LegacyUiSettings field is in some
// RynthAi entry, or in an ignore list below with its reason. The ignore lists are for what a player
// never uses (developer dumps, internal state); a player's command or setting goes in the catalog.
internal static class FeatureCatalogTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string AddIt = "add it to Docs/features/rynthai.json";
    /// <summary>The where of a feature with no button (the content's convention).</summary>
    private const string CommandOnlyWhere = "Chat command (no button)";

    public static void Register(Runner r)
    {
        r.Add("feature catalog: every Docs/features file is embedded and parses", EveryFileEmbeddedAndParses);
        r.Add("feature catalog: required fields, ids, commands, where, summary, settings", EntriesValid);
        r.Add("feature catalog: open commands only open (never start, stop or toggle)", OpenOnlyOpens);
        r.Add("feature catalog: a bad file is skipped, nulls and nameless entries cleaned", BadFilesSkipped);
        r.Add("feature catalog coverage: every /ra command is in the catalog", CoverageCommands);
        r.Add("feature catalog coverage: every RynthAi window and view is in the catalog", CoverageWindows);
        r.Add("feature catalog coverage: every RynthAi setting is in the catalog", CoverageSettings);
        r.Add("find a feature: the window is in the manifest with no bar button", Manifest);
        r.Add("find a feature: /ra find opens the window with the words and says the count", FindCommand);
        r.Add("find a feature: /ra find without plugin windows prints the best matches", FindCommandNoWindows);
        r.Add("find a feature: streaks and Sense's exact match are found by the words players use", PlayerWords);
    }

    // 2026-11-26 (#rynthsuite): a new player couldn't find how to cast streaks, and Sense gained Exact.
    // The words they'd type must bring the entry up near the top of the real catalog.
    private static void PlayerWords()
    {
        List<FeatureEntry> all = Catalog().Entries.ToList();
        void Finds(string query, string id, int within)
        {
            List<string> ids = FeatureSearch.Search(all, query).Select(h => h.Entry.Id).ToList();
            int at = ids.IndexOf(id);
            Check.True(at >= 0 && at < within, $"'{query}' finds {id} in the first {within} (at {at}: {string.Join(", ", ids.Take(5))})");
        }
        Finds("streak", "ai.use-streaks", 3);
        Finds("streaks instead of bolts", "ai.use-streaks", 1);
        Finds("bolt", "ai.use-streaks", 3);
        Finds("arc", "ai.use-streaks", 5);
        Finds("war spell type", "ai.use-streaks", 3);
        Finds("exact match", "core.sense-exact", 1);
        Finds("sense exact", "core.sense-exact", 1);
        Finds("whole name", "core.sense-exact", 3);
        FeatureEntry streaks = all.Single(e => e.Id == "ai.use-streaks");
        Check.True(streaks.Where.Any(w => w == "Settings > Spell Combat > Use streaks instead of bolts"), "it says where: Settings > Spell Combat");
        Check.Eq(streaks.Open, "/rc settings Spell Combat", "and opens that tab");
    }

    // ── Where things are ─────────────────────────────────────────────────

    private static string RepoRoot()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !(Directory.Exists(Path.Combine(root, "Plugins")) && Directory.Exists(Path.Combine(root, "Docs"))))
            root = Path.GetDirectoryName(root);
        return root ?? throw new InvalidOperationException("RynthSuite's root (Plugins and Docs) not found above " + AppContext.BaseDirectory);
    }

    private static string FeaturesDir => Path.Combine(RepoRoot(), "Docs", "features");

    private static FeatureCatalog Catalog() => FeatureCatalog.LoadEmbedded(null);

    private static List<FeatureEntry> RynthAiEntries() =>
        Catalog().Entries.Where(e => e.Plugin.Equals("RynthAi", StringComparison.Ordinal)).ToList();

    /// <summary>The id prefix of each plugin (the schema's list).</summary>
    private static readonly Dictionary<string, string> Prefixes = new(StringComparer.Ordinal)
    {
        ["RynthAi"] = "ai", ["RynthOracle"] = "oracle", ["RynthTracker"] = "tracker", ["RynthLua"] = "lua",
        ["RynthSort"] = "sort", ["RynthChat"] = "chat", ["RynthVision"] = "vision", ["RynthInventory"] = "inv",
        ["RynthNav"] = "nav", ["RynthNet"] = "net", ["RynthDefective"] = "defective", ["RynthCore"] = "core",
    };

    private static string List(IEnumerable<string> items) => string.Join(", ", items);

    // ── The files ────────────────────────────────────────────────────────

    private static void EveryFileEmbeddedAndParses()
    {
        string[] onDisk = Directory.GetFiles(FeaturesDir, "*.json").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
        List<string> embedded = FeatureCatalog.ResourceNames().Select(n => n.Substring(FeatureCatalog.ResourcePrefix.Length)).ToList();
        Check.True(onDisk.Length > 0, "Docs/features has catalog files");
        Check.True(onDisk.SequenceEqual(embedded, StringComparer.OrdinalIgnoreCase),
            $"every file is embedded (the csproj's wildcard): on disk {List(onDisk)}; embedded {List(embedded)}");
        Check.True(onDisk.Contains("rynthai.json"), "rynthai.json is there");

        var log = new List<string>();
        FeatureCatalog c = FeatureCatalog.LoadEmbedded(log.Add);
        Check.Eq(log.Count, 0, "no file skipped and no entry dropped: " + string.Join(" | ", log));
        foreach (FeatureFileRead f in c.Files)
        {
            Check.True(f.File != null, f.Resource + " parses: " + f.Error);
            Check.True(f.File == null || f.File.Area.Trim().Length > 0, f.Resource + " has an area");
            Check.True(f.File == null || f.File.Features.Count > 0, f.Resource + " has features");
        }
        // The files on disk are the embedded ones byte for byte (a stale build would test old content).
        foreach (string name in onDisk)
        {
            using Stream s = File.OpenRead(Path.Combine(FeaturesDir, name));
            FeatureFileRead disk = FeatureCatalog.Parse(name, s);
            FeatureFileRead? emb = c.Files.FirstOrDefault(f => f.Resource.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)
                                                          || f.Resource == FeatureCatalog.ResourcePrefix + name);
            Check.True(emb?.File != null && disk.File != null && emb.File.Features.Count == disk.File.Features.Count,
                name + ": the embedded copy matches the file (rebuild RynthAi)");
        }
        Check.True(c.Entries.Count > 0, "the catalog has entries");
        Check.True(ReferenceEquals(FeatureCatalog.Shared(null), FeatureCatalog.Shared(null)), "read once");
    }

    private static void EntriesValid()
    {
        FeatureCatalog c = Catalog();
        var settingNames = new HashSet<string>(SettingsMembers().Select(m => m.Name), StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idRule = new Regex("^([a-z]+)\\.[a-z0-9]+(-[a-z0-9]+)*$");
        var problems = new List<string>();
        foreach (FeatureFileRead f in c.Files)
        {
            if (f.File == null) continue;
            string file = f.Resource.Substring(FeatureCatalog.ResourcePrefix.Length);
            foreach (FeatureEntry e in f.File.Features)
            {
                string at = $"{file} {(e.Id.Length > 0 ? e.Id : "'" + e.Name + "'")}";
                if (e.Name.Trim().Length == 0) problems.Add(at + ": no name");
                if (!Prefixes.TryGetValue(e.Plugin, out string? prefix)) problems.Add($"{at}: plugin '{e.Plugin}' isn't one of {List(Prefixes.Keys)}");
                Match m = idRule.Match(e.Id);
                if (!m.Success) problems.Add($"{at}: id isn't <prefix>.<kebab-name>");
                else if (prefix != null && m.Groups[1].Value != prefix) problems.Add($"{at}: id prefix should be '{prefix}.' for {e.Plugin}");
                if (ids.TryGetValue(e.Id, out string? other)) problems.Add($"{at}: id also in {other}");
                else ids[e.Id] = file;
                if (e.Group.Trim().Length == 0) problems.Add(at + ": no group");
                if (e.Summary.Trim().Length == 0) problems.Add(at + ": no summary");
                if (e.Summary.Length > 200) problems.Add($"{at}: summary is {e.Summary.Length} characters (200 at most)");
                // A feature with no button says so: where ["Chat command (no button)"] (allowed; it counts as a where).
                if (e.Where.Count == 0) problems.Add(at + ": no where (a command-only feature says \"" + CommandOnlyWhere + "\")");
                foreach (string w in e.Where)
                    if (w.Trim().Length == 0) problems.Add(at + ": an empty where");
                if (e.Command.Length > 0 && !e.Command.StartsWith('/')) problems.Add($"{at}: command '{e.Command}' doesn't start with '/'");
                if (e.Open.Length > 0 && !e.Open.StartsWith('/')) problems.Add($"{at}: open '{e.Open}' doesn't start with '/'");
                if (e.Settings.Count > 0 && e.Plugin != "RynthAi") problems.Add($"{at}: settings are for RynthAi entries only");
                foreach (string s in e.Settings)
                    if (e.Plugin == "RynthAi" && !settingNames.Contains(s)) problems.Add($"{at}: setting '{s}' isn't a LegacyUiSettings field");
                if (Regex.IsMatch(e.Summary + " " + string.Join(" ", e.Where) + " " + e.Name, "\\bTom\\b"))
                    problems.Add(at + ": a person's name in player text");
            }
        }
        Check.True(problems.Count == 0, problems.Count + " problem(s):\n    " + string.Join("\n    ", problems));
    }

    /// <summary>
    /// Commands that start, stop or switch something, never only open a window: an entry's open must
    /// not be one (the window's Open button runs it). "/ra hunt" alone starts or stops the hunt; its
    /// window is "/ra hunt panel".
    /// </summary>
    private static readonly string[] NotOpenCommands =
    {
        "/ra hunt", "/ra hunt start", "/ra hunt stop", "/ra start", "/ra stop", "/ra pause", "/ra resume", "/ra panic",
        "/ra forcebuff", "/ra cancelforcebuff", "/ra clearbusy", "/ra quit", "/ra exit", "/ra fightselected",
        "/ra targetlock", "/ra dunnav-patrol", "/ra navprio", "/ra lootprio", "/ra navlootprio", "/ra lootthis",
        "/ra heal", "/ra stam", "/ra mana", "/ra recharge", "/ra follow", "/ra dfheight on", "/ra dfheight off",
    };

    private static void OpenOnlyOpens()
    {
        var bad = new List<string>();
        foreach (FeatureEntry e in Catalog().Entries)
        {
            if (e.Open.Length == 0) continue;
            string open = Regex.Replace(e.Open.Trim().ToLowerInvariant(), "\\s+", " ");
            if (NotOpenCommands.Contains(open)) bad.Add($"{e.Id}: open '{e.Open}' does more than open a window");
            if (open.EndsWith(" on") || open.EndsWith(" off") || open.EndsWith(" toggle") || open.EndsWith(" start") || open.EndsWith(" stop"))
                bad.Add($"{e.Id}: open '{e.Open}' switches something");
        }
        Check.True(bad.Count == 0, string.Join("\n    ", bad));
    }

    private static void BadFilesSkipped()
    {
        FeatureFileRead r = Parse("{ \"area\": \"X\", \"features\": [ { \"id\": \"ai.a\", \"name\": \"A\", } ] }");
        Check.True(r.File == null && r.Error.Length > 0, "a trailing comma doesn't parse: " + r.Error);
        r = Parse("{ \"area\": \"X\", // a comment\n \"features\": [] }");
        Check.True(r.File == null, "comments don't parse");
        r = Parse("not json");
        Check.True(r.File == null && r.Error.Contains("line"), "the error says where: " + r.Error);
        r = Parse("{ \"area\": \"RynthAi\", \"features\": [ { \"id\": \"ai.a\", \"name\": \"A\", \"where\": null, \"keywords\": [\"x\", null] },"
                  + " { \"id\": \"\", \"name\": \"No id\" }, { \"id\": \"ai.b\" }, null ] }");
        Check.True(r.File != null, "nulls read");
        Check.Eq(r.File?.Features.Count ?? -1, 1, "only the entry with an id and a name is kept");
        Check.Eq(r.Dropped, 3, "three dropped (no id, no name, null)");
        FeatureEntry? a = r.File?.Features.FirstOrDefault();
        Check.True(a != null && a.Where.Count == 0 && a.Keywords.SequenceEqual(new[] { "x" }) && a.Plugin == "RynthAi",
            "null lists are empty, null items gone, the plugin from the area");
        Check.True(a != null && a.Command == "" && a.Open == "" && a.Summary == "", "missing strings are empty");
        // Field names in any case, unknown fields ignored (the website may add its own).
        r = Parse("{ \"Area\": \"X\", \"Features\": [ { \"ID\": \"ai.c\", \"Name\": \"C\", \"extra\": 1 } ] }");
        Check.True(r.File?.Features.Count == 1 && r.File.Features[0].Id == "ai.c", "any case, unknown fields ignored");
    }

    private static FeatureFileRead Parse(string json) => FeatureCatalog.Parse("test.json", new MemoryStream(Encoding.UTF8.GetBytes(json)));

    // ── Coverage ─────────────────────────────────────────────────────────

    /// <summary>/ra verbs a player never types: developer dumps and tests. Each with why.</summary>
    private static readonly Dictionary<string, string> IgnoredCommands = new(StringComparer.Ordinal)
    {
        ["raycast"] = "the raycaster's status, for its developers",
        ["lostest"] = "a line-of-sight test for tuning the raycaster",
        ["landtest"] = "a terrain heightmap dump",
        ["rayland"] = "a landscape raycast test",
        ["cache"] = "an object cache dump",
        ["cache2"] = "an object cache id dump",
        ["attackable"] = "one flag of the selection, for combat's developers",
        ["buildinfo"] = "a nearby geometry dump",
        ["navdebug"] = "a nav coordinate dump",
        ["navstate"] = "the nav engine's state dump",
        ["salvstate"] = "the salvage state machine's dump",
        ["mapdump"] = "an object position dump",
        ["dumpinv"] = "an inventory dump to the log",
        ["combat"] = "the combat manager's state dump",
        ["busyinfo"] = "the busy-state counters",
        ["pace"] = "the action pacer's counters",
        ["swaptest"] = "a weapon swap test run",
        ["bufftest"] = "the buff cast registry diagnostic",
        ["debug"] = "echoes trace and exception lines to chat, for developers",
        ["trace"] = "log categories on and off, for developers",
        ["corpseinfo"] = "corpse range and open diagnostics",
        ["corpseopen"] = "forces the corpse-open flow, a looting test",
        ["lootparse"] = "dumps a loot profile's parsed rules",
        ["wielded"] = "a wielded-items dump (Active Gear is the player's view)",
        ["discord"] = "only says the Discord relay moved into RynthCore's Chat",
        ["cast"] = "casts a spell by id at the selection, a spell-data test tool",
        ["scan"] = "a nearby-objects line-of-sight dump",
        ["videopatch"] = "passed on to RynthCore's /rc videopatch (rynthcore.json lists it)",
        ["video"] = "passed on to RynthCore's /rc videopatch (rynthcore.json lists it)",
    };

    /// <summary>Verbs that are another verb by another name (separate case labels): covered when that one is.</summary>
    private static readonly Dictionary<string, string> SameAs = new(StringComparer.Ordinal)
    {
        ["fellowinfo"] = "fellow",       // /ra fellow status
        ["navaddpt"] = "addnavpt",       // VTank's name for it
        ["testitem"] = "lootcheck",      // VTank's TestItem
        ["meleemedium"] = "meleemid",    // the same height by its long name
        ["propertydump"] = "dumpprops",  // VTank's PropertyDump
    };

    /// <summary>
    /// The verbs of DispatchRaCommand's switch and of the VTank-style switch its default branch calls
    /// (TryHandleVTankHotkeyCommand), with "jump" (the default branch's jump* verbs). Case labels with
    /// nothing between them are one command's names (aliases): one group.
    /// </summary>
    internal static List<List<string>> RaVerbGroups()
    {
        var groups = new List<List<string>>();
        string dir = Path.Combine(RepoRoot(), "Plugins", "RynthCore.Plugin.RynthAi");
        ReadSwitch(File.ReadAllText(Path.Combine(dir, "RynthAiPlugin.cs")), "private void DispatchRaCommand(", groups);
        ReadSwitch(File.ReadAllText(Path.Combine(dir, "RynthAiPlugin.VTankHotkeys.cs")), "private bool TryHandleVTankHotkeyCommand(", groups);
        groups.Add(new List<string> { "jump" });
        // A verb listed twice (case "itemhud" when ...: before case "huds": case "itemhud": ...) joins its groups.
        var merged = new List<List<string>>();
        foreach (List<string> g in groups)
        {
            List<string>? into = merged.FirstOrDefault(m => m.Intersect(g, StringComparer.Ordinal).Any());
            if (into == null) merged.Add(new List<string>(g));
            else foreach (string v in g) if (!into.Contains(v)) into.Add(v);
        }
        return merged;
    }

    private static void ReadSwitch(string src, string method, List<List<string>> groups)
    {
        int a = src.IndexOf(method, StringComparison.Ordinal);
        Check.True(a >= 0, method + " found");
        if (a < 0) return;
        int b = src.IndexOf("\n    }", a, StringComparison.Ordinal);
        string body = src.Substring(a, (b > a ? b : src.Length) - a);
        List<string>? group = null;
        int lastEnd = -1;
        foreach (Match m in Regex.Matches(body, "case\\s+\"([a-z0-9_-]+)\"(?:\\s+when[^:]*)?\\s*:"))
        {
            string between = lastEnd >= 0 ? body.Substring(lastEnd, m.Index - lastEnd) : "x";
            between = Regex.Replace(between, "//[^\\n]*", "");
            if (group != null && between.Trim().Length == 0) group.Add(m.Groups[1].Value);
            else groups.Add(group = new List<string> { m.Groups[1].Value });
            lastEnd = m.Index + m.Length;
        }
    }

    /// <summary>
    /// "/ra verb" in a RynthAi entry's command or open. The verb may be written as /ra help writes its
    /// families: "/ra give[a][p|xp|pp|r]" or "/ra use[i|l][p]", each bracket an optional part with its
    /// choices (so that one names give, givea, givep, giveap, ...).
    /// </summary>
    private static bool Mentions(IEnumerable<FeatureEntry> entries, string verb)
    {
        foreach (FeatureEntry e in entries)
            foreach (string text in new[] { e.Command, e.Open })
                foreach (Match m in Regex.Matches(text, "/ra\\s+([a-z0-9_\\-\\[\\]|]+)", RegexOptions.IgnoreCase))
                    if (Expand(m.Groups[1].Value.ToLowerInvariant()).Contains(verb)) return true;
        return false;
    }

    /// <summary>"give[a][p|xp]" is give, givea, givep, givexp, giveap, giveaxp. A token without brackets is itself.</summary>
    internal static HashSet<string> Expand(string token)
    {
        var results = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        int i = 0;
        while (i < token.Length)
        {
            if (token[i] == '[')
            {
                int close = token.IndexOf(']', i);
                if (close < 0) break;
                string[] choices = token.Substring(i + 1, close - i - 1).Split('|');
                var next = new HashSet<string>(results, StringComparer.Ordinal);   // the part left out
                foreach (string r in results) foreach (string c in choices) next.Add(r + c);
                results = next;
                i = close + 1;
            }
            else if (token[i] == '|' || token[i] == ']') break;   // "on|off" after the verb: not part of it
            else
            {
                int end = i;
                while (end < token.Length && token[end] != '[' && token[end] != '|' && token[end] != ']') end++;
                string part = token.Substring(i, end - i);
                results = new HashSet<string>(results.Select(r => r + part), StringComparer.Ordinal);
                i = end;
            }
        }
        results.Remove(string.Empty);
        return results;
    }

    private static void CoverageCommands()
    {
        HashSet<string> family = Expand("give[a][p|xp]");
        Check.True(family.SetEquals(new[] { "give", "givea", "givep", "givexp", "giveap", "giveaxp" }), "a bracketed family: " + List(family));
        Check.True(Expand("hunt").SetEquals(new[] { "hunt" }), "a plain verb is itself");
        List<FeatureEntry> ai = RynthAiEntries();
        List<List<string>> groups = RaVerbGroups();
        Check.True(groups.Count > 50, $"the dispatcher's verbs were read ({groups.Count} commands)");
        var missing = new List<string>();
        foreach (List<string> g in groups)
        {
            if (g.Any(v => IgnoredCommands.ContainsKey(v))) continue;
            if (g.Any(v => Mentions(ai, v) || (SameAs.TryGetValue(v, out string? same) && Mentions(ai, same)))) continue;
            missing.Add("/ra " + string.Join(" | /ra ", g));
        }
        foreach (string v in IgnoredCommands.Keys)
            Check.True(groups.Any(g => g.Contains(v)), $"ignored /ra {v} is still a command (else take it off the ignore list)");
        Check.True(missing.Count == 0,
            $"{missing.Count} /ra command(s) in no RynthAi entry's command or open ({AddIt}, or to the test's ignore list if a player never uses it):\n    "
            + string.Join("\n    ", missing));
    }

    /// <summary>Every window title and section view label RynthAi declares (the manifest with every window on).</summary>
    internal static List<string> ManifestSurfaces()
    {
        string json = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections,
            dashboard: true, monsterStats: true, timedCommands: true, activeGear: true, featureSearch: true, acMaps: true, commandButtons: true, tradeShop: true);
        SectionManifest m = SectionManifestParser.Parse("RynthAi", json, out string error);
        Check.Eq(error, string.Empty, "the manifest parses");
        var names = new List<string>();
        foreach (SectionWindowDecl w in m.Windows) names.Add(w.Title);
        foreach (SectionContribution c in m.Contributions) names.Add(c.Label);
        foreach (SectionDecl s in m.Sections) names.Add(s.Label);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CoverageWindows()
    {
        List<FeatureEntry> ai = RynthAiEntries();
        List<string> surfaces = ManifestSurfaces();
        Check.True(surfaces.Count >= 8, "the manifest's windows and views: " + List(surfaces));
        var missing = surfaces.Where(s => !ai.Any(e =>
                e.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                || e.Where.Any(w => w.Contains(s, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        Check.True(missing.Count == 0,
            $"{missing.Count} RynthAi window(s) or view(s) in no RynthAi entry's name or where ({AddIt}):\n    " + string.Join("\n    ", missing));
    }

    /// <summary>Persisted LegacyUiSettings fields a player never sets: internal state. Each with why.</summary>
    private static readonly Dictionary<string, string> IgnoredSettings = new(StringComparer.Ordinal)
    {
        ["CurrentNavPath"] = "the loaded route's path (the Loaded files picker sets it)",
        ["CurrentLootPath"] = "the loaded loot profile's path (the Loaded files picker sets it)",
        ["CurrentMetaPath"] = "the loaded meta's path (the Loaded files picker sets it)",
        ["CurrentBuffProfilePath"] = "the buff profile in use's path (the Loaded files picker sets it)",
        ["MacroSettingsIdx"] = "a picker's position",
        ["NavProfileIdx"] = "a picker's position",
        ["LootProfileIdx"] = "a picker's position",
        ["MetaProfileIdx"] = "a picker's position",
        ["BuffProfileIdx"] = "a picker's position",
        ["SelectedProfile"] = "the settings profile's name (the Profile picker sets it)",
        ["MetaUnsaved"] = "a failed meta save's marker",
        ["ProfileItemsChecked"] = "the once-per-profile pack check's marker",
        ["ActiveNavIndex"] = "how far along the route is",
        ["EmbeddedNavs"] = "the loaded meta's own routes",
        ["ShowSpellsWindow"] = "whether a window is open",
        ["ShowAdvancedWindow"] = "whether a window is open",
        ["SelectedAdvancedTab"] = "a window's last tab",
        ["DashShowWeapons"] = "whether a window is open",
        ["DashShowLua"] = "whether a window is open",
        ["DashShowNavigation"] = "whether a window is open",
        ["DashShowMacroRules"] = "whether a window is open",
        ["DashShowMonsters"] = "whether a window is open",
        ["DashShowDungeonMap"] = "whether a window is open",
        ["DashboardMinimized"] = "the dashboard's minimize button's state",
        ["WindowPosX"] = "a window's place", ["WindowPosY"] = "a window's place",
        ["WindowSizeX"] = "a window's size", ["WindowSizeY"] = "a window's size",
        ["RadarPosX"] = "a window's place", ["RadarPosY"] = "a window's place",
        ["RadarSizeX"] = "a window's size", ["RadarSizeY"] = "a window's size",
        ["FeatureServerNames"] = "legacy: read once by RynthILT, no UI",
        ["ForceServerFeatures"] = "legacy: read once by RynthILT, no UI",
        ["HandlerRingEvade"] = "obsolete (RynthILT's now), kept so old profiles load",
        ["EnableVaultedDungeons"] = "obsolete (RynthILT's now), kept so old profiles load",
        ["T11ItemAugsOverride"] = "obsolete (RynthILT's now), kept so old profiles load",
        ["LootInterItemDelayMs"] = "not read by the looting, kept so old profiles load",
        ["LootClosingDelayMs"] = "not read by the looting, kept so old profiles load",
        ["LootOnlyRareCorpses"] = "not read by the looting, kept so old profiles load",
        ["LootJumpHeight"] = "kept for old profiles and metas (jump-loot's height is fixed)",
        ["ShowEditor"] = "not read by RynthAi (copied with the profile only)",
        ["AdvancedOptions"] = "not read by RynthAi (copied with the profile only)",
        ["DashboardVisible"] = "not read by RynthAi (copied with the profile only)",
        ["HealOthersAt"] = "saved but not acted on yet (RynthCore leaves its row out until it is)",
        ["RestamOthersAt"] = "saved but not acted on yet (RynthCore leaves its row out until it is)",
        ["InfuseOthersAt"] = "saved but not acted on yet (RynthCore leaves its row out until it is)",
        ["MapShowDoors"] = "the old plugin map's; RynthCore's Dungeon Map keeps its own settings",
        ["MapShowCreatures"] = "the old plugin map's; RynthCore's Dungeon Map keeps its own settings",
        ["MapShowToolbar"] = "the old plugin map's; RynthCore's Dungeon Map keeps its own settings",
        ["MapBgOpacity"] = "the old plugin map's; RynthCore's Dungeon Map keeps its own settings",
        ["MapRotateWithPlayer"] = "the old plugin map's; RynthCore's Dungeon Map keeps its own settings",
        ["ShowRynthRadar"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarRotateWithPlayer"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarOpacity"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarZoom"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarShowMonsters"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarShowNpcs"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarShowPortals"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarShowDoors"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarCircular"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarClickThrough"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["ShowRadarWalls"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarWallWorldRange"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["RadarWallPaintRadius"] = "RynthAi's own radar window is gone, kept so profiles load",
        ["ShowRynthChat"] = "RynthAi's own chat window is gone (RynthChat's now), kept so profiles load",
        ["ChatOpacity"] = "RynthAi's own chat window is gone (RynthChat's now), kept so profiles load",
        ["ChatMaxLines"] = "RynthAi's own chat window is gone (RynthChat's now), kept so profiles load",
        ["ChatShowTimestamps"] = "RynthAi's own chat window is gone (RynthChat's now), kept so profiles load",
        ["ChatClickThrough"] = "RynthAi's own chat window is gone (RynthChat's now), kept so profiles load",
        ["AmmoRules"] = "an old list with no editor (its window is gone), kept so profiles load",
        ["BuffRules"] = "an old list with no editor, kept so profiles load",
        // NO WAY IN (found 2026-11-13): these were only in RynthAi's own Advanced Settings window, which
        // isn't drawn since 2026-09-30, and RynthCore's Settings panel has no row for them. Give each a
        // Settings row, then catalogue it and take it off this list.
        ["HpFailsafeRecall"] = "NO WAY IN YET: needs a Settings row",
        ["MonsterNameBlacklist"] = "NO WAY IN YET: needs a Settings row (Never Attack by name)",
        ["AllowMetaKeystrokes"] = "NO WAY IN YET: needs a Settings row",
        ["StuckCastBreakMs"] = "NO WAY IN YET: needs a Settings row",
        ["RecoveryRetryMs"] = "NO WAY IN YET: needs a Settings row",
    };

    /// <summary>LegacyUiSettings' public instance fields and properties.</summary>
    private static List<MemberInfo> SettingsMembers() =>
        typeof(LegacyUiSettings).GetFields(BindingFlags.Instance | BindingFlags.Public).Cast<MemberInfo>()
            .Concat(typeof(LegacyUiSettings).GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .ToList();

    private static void CoverageSettings()
    {
        List<FeatureEntry> ai = RynthAiEntries();
        var named = new HashSet<string>(ai.SelectMany(e => e.Settings), StringComparer.Ordinal);
        List<MemberInfo> members = SettingsMembers();
        Check.True(members.Count > 300, $"LegacyUiSettings' fields were read ({members.Count})");
        // [JsonIgnore] members are never saved: runtime state (the macro running, the meta state), not settings.
        var missing = members
            .Where(m => m.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .Select(m => m.Name)
            .Where(n => !named.Contains(n) && !IgnoredSettings.ContainsKey(n))
            .ToList();
        var members2 = new HashSet<string>(members.Select(m => m.Name), StringComparer.Ordinal);
        foreach (string n in IgnoredSettings.Keys)
            Check.True(members2.Contains(n), $"ignored setting {n} is still a LegacyUiSettings field (else take it off the ignore list)");
        Check.True(missing.Count == 0,
            $"{missing.Count} LegacyUiSettings field(s) in no RynthAi entry's settings ({AddIt}, or to the test's ignore list if it is internal state):\n    "
            + string.Join("\n    ", missing));
    }

    // ── The window and /ra find ──────────────────────────────────────────

    private static void Manifest()
    {
        string json = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, featureSearch: true);
        SectionManifest m = SectionManifestParser.Parse("RynthAi", json, out string error);
        Check.Eq(error, string.Empty, "no parse error");
        Check.Eq(m.Windows.Length, 1, "the one window");
        SectionWindowDecl w = m.Windows.Single();
        Check.Eq(w.Id, "featuresearch", "id");
        Check.Eq(w.Title, RynthAiPlugin.FeatureSearchTitle, "title (its panel key)");
        Check.Eq(w.Title, "Find a Feature", "the title players see");
        Check.Eq(w.Surface, "window:featuresearch", "the surface PluginExports.RenderEmbed draws");
        Check.False(w.Bar, "no bar button (the bar is full)");
        Check.False(w.TakeOver || w.Frameless, "the engine's frame");
        Check.True(w.Width >= w.MinWidth && w.Height >= w.MinHeight, "sizes");
        Check.True(w.Icon.Length > 0, "an icon (the magnifier)");

        string all = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, dashboard: true, monsterStats: true, activeGear: true, featureSearch: true);
        SectionManifest m4 = SectionManifestParser.Parse("RynthAi", all, out error);
        Check.True(error.Length == 0 && m4.Windows.Length == 4, "with the other three");
        string none = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, dashboard: true, monsterStats: true, activeGear: true);
        Check.False(none.Contains("featuresearch"), "off unless asked");
    }

    private static readonly List<(string Key, int Mode)> PanelCalls = new();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint FakeUiColor(int token) => 0xFFFFFFFF;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr FakeUiFont(int font) => IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int FakeSetPanelOpen(byte* key, int mode)
    {
        PanelCalls.Add((Marshal.PtrToStringUTF8((IntPtr)key) ?? "", mode));
        return mode == 0 ? 0 : 1;
    }

    private static unsafe void PluginWindows(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 89u);
        api.GetUiColorFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, uint>)&FakeUiColor;
        api.GetUiFontFn = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr>)&FakeUiFont;
        api.SetPanelOpenFn = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&FakeSetPanelOpen;
    }

    private sealed class Rig
    {
        public readonly RynthAiPlugin P = new();

        public Rig(bool windows)
        {
            FakeHost.Reset();
            PanelCalls.Clear();
            if (windows) FakeHost.ConfigureApi = PluginWindows;
            FakeHost.Create(playerId: 0x50000F41);
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            Set("_playerId", 0x50000F41u);
            Set("_initialized", true);
            Set("_loginComplete", true);
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);

        public List<string> Typed(string line)
        {
            int before = FakeHost.Chat.Count;
            int eat = 0;
            P.OnChatBarEnter(line, ref eat);
            return FakeHost.Chat.Skip(before).ToList();
        }
    }

    private static string Show(List<string> lines) => string.Join(" | ", lines);

    private static void FindCommand()
    {
        var rig = new Rig(windows: true);
        IReadOnlyList<FeatureEntry> all = rig.P.FeatureList.Entries;
        int death = FeatureSearch.Search(all, "death").Count;
        Check.True(death > 0, "the catalog has a death feature");

        List<string> lines = rig.Typed("/ra find death");
        Check.Eq(lines.Count, 1, "one line: " + Show(lines));
        string count = FeatureSearch.CountLine(death, "death");
        Check.True(lines.Count == 1 && lines[0].Contains(count) && lines[0].Contains("Find a Feature open"), "the count and the window: " + Show(lines));
        Check.True(PanelCalls.Count > 0 && PanelCalls[^1] == (RynthAiPlugin.FeatureSearchTitle, (int)RynthPanelOpen.Open), "opened by its panel key");
        Check.Eq(rig.P.FeatureQueryPendingForTests, "death", "the words go to the window's box");

        lines = rig.Typed("/ra find");
        Check.True(lines.Count == 1 && lines[0].Contains(FeatureSearch.CountLine(all.Count, "")) && lines[0].Contains("browse"),
            "no words: how many to browse: " + Show(lines));
        Check.Eq(rig.P.FeatureQueryPendingForTests, "", "the box cleared");

        lines = rig.Typed("/ra find qqqzzzxxy");
        Check.True(lines.Count == 1 && lines[0].Contains("No feature matches 'qqqzzzxxy'") && lines[0].Contains(RynthAiPlugin.FeatureSearchUsage),
            "nothing matches: says so, and the usage: " + Show(lines));

        lines = rig.Typed("/ra find ???!!");
        Check.True(lines.Count == 1 && lines[0].Contains("no letters or numbers") && lines[0].Contains(RynthAiPlugin.FeatureSearchUsage),
            "punctuation only: why, and the usage: " + Show(lines));

        lines = rig.Typed("/ra find " + new string('a', RynthAiPlugin.FeatureQueryMax + 1));
        Check.True(lines.Count == 1 && lines[0].Contains("at most") && lines[0].Contains(RynthAiPlugin.FeatureSearchUsage), "too long: " + Show(lines));

        lines = rig.Typed("/ra find ?");
        Check.True(lines.Count == 2 && lines.All(l => l.Contains("/ra find")), "? says the usage: " + Show(lines));

        lines = rig.Typed("/ra help");
        Check.True(lines.Any(l => l.Contains("/ra find")), "/ra help lists it");
    }

    private static void FindCommandNoWindows()
    {
        var rig = new Rig(windows: false);
        IReadOnlyList<FeatureEntry> all = rig.P.FeatureList.Entries;
        List<FeatureHit> hits = FeatureSearch.Search(all, "hunt");
        Check.True(hits.Count > 0, "the catalog has hunting features");
        List<string> lines = rig.Typed("/ra find hunt");
        int shown = Math.Min(hits.Count, RynthAiPlugin.FeatureChatMatches);
        Check.Eq(lines.Count, 1 + shown, "the count, then the best few: " + Show(lines));
        Check.True(lines.Count > 0 && lines[0].Contains(FeatureSearch.CountLine(hits.Count, "hunt")) && !lines[0].Contains("open"), "no window to open: " + Show(lines));
        Check.True(lines.Count > 1 && lines[1].Contains(hits[0].Entry.Name), "the best match first: " + Show(lines));
        Check.Eq(PanelCalls.Count, 0, "no panel asked for");
    }
}

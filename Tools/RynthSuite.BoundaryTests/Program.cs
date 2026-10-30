using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RynthSuite.BoundaryTests;

/// <summary>
/// RynthCore is the hook layer, RynthAi is a plugin, and one server's features live only in that
/// server's plugin (RynthServers). These checks keep RynthSuite's side of that true: a feature for
/// one server that lands in a shared plugin fails here before it merges.
/// </summary>
internal static class Program
{
    private static int _checks, _failed;

    /// <summary>Every server with a plugin of its own (RynthServers), as code would name it.</summary>
    private static readonly Regex ServerNames = new(
        @"Ilt[A-Z]|RynthIlt|Leaftide|Conquest|Aelrynth|Levistras|DreamWeave|Coldeve|FrostACE|Reefcull|PortalStorm|" +
        @"Sundering|AChard|Derptide|Frostcull|Thistlecrown|Eversong|LeafDawn|Leafdawning",
        RegexOptions.CultureInvariant);

    /// <summary>Servers' own chat commands and markers: their plugins send and read them.</summary>
    private static readonly string[] ServerCommands =
        { "\"/bank", "\"/b\"", "\"/augs", "\"/enl augs", "\"/bonus", "\"/fship list", "\"/top", "\"/trial", "\"/mule-data", "\"~ael1" };

    /// <summary>
    /// Kept on purpose, each with its reason. A file here may name a server in code; nothing else may.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // The DreamWeave owners asked for the Threads patrol block to hold even without RynthDreamWeave,
        // and server plugins are opt-in (2026-10-15).
        ["Plugins/RynthCore.Plugin.RynthAi/ServerPolicy.cs"] = "DreamWeave's Threads patrol block (owners' request)",
    };

    /// <summary>Game words that are also servers' names: allowed in these files only.</summary>
    private static readonly (string File, string Word, string Why)[] GameWords =
    {
        ("Plugins/RynthCore.Plugin.RynthLua/LuaEnumData.cs", "PortalStorm", "AC's PlayScript effect of that name"),
        ("Plugins/RynthCore.Plugin.RynthAi/Meta/ExpressionEngine.cs", "Leafdawning", "a month of Dereth's calendar"),
        ("Plugins/RynthCore.Plugin.RynthAi/Meta/ExpressionEngine.cs", "Coldeve", "a month of Dereth's calendar"),
    };

    private static int Main()
    {
        string root = RepoRoot();
        Console.WriteLine("RynthSuite at " + root);
        NoServerPlugins(root);
        SharedCodeNamesNoServer(root);
        NoServerCommands(root);
        NoServerOnlyLinks(root);
        RynthAiTimesWithItsClock(root);
        Console.WriteLine();
        Console.WriteLine($"{_checks - _failed}/{_checks} checks passed.");
        Console.WriteLine(_failed == 0 ? "ALL BOUNDARY CHECKS PASSED." : $"{_failed} FAILED.");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// RynthAi times intervals with RynthClock, never DateTime.Now: the wall clock jumps an hour when
    /// daylight saving ends, and every "now - last &lt; interval" check stalled for that hour (no target
    /// scans or attacks; structural audit 2026-10-21). DateTime.Now stays only where the time of day is
    /// shown: RynthLog's timestamps and file names, a backup file's name, a meta's getdatetimelocal.
    /// </summary>
    private static void RynthAiTimesWithItsClock(string root)
    {
        string dir = Path.Combine(root, "Plugins", "RynthCore.Plugin.RynthAi");
        var found = new List<string>();
        int files = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput))
        {
            files++;
            string rel = Rel(root, file);
            if (rel.EndsWith("/Diagnostics/RynthLog.cs", StringComparison.Ordinal) || rel.EndsWith("/RynthClock.cs", StringComparison.Ordinal)) continue;   // the log and the clock's own anchor
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (!line.Contains("DateTime.Now", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("\"getdatetimelocal\"", StringComparison.Ordinal) || line.Contains("\".unreadable-\"", StringComparison.Ordinal)) continue;
                found.Add($"{rel}:{i + 1}");
            }
        }
        Check(files > 100, $"RynthAi's sources were found ({files})");
        Check(found.Count == 0, "RynthAi times with RynthClock.Now, not DateTime.Now" +
                                (found.Count > 0 ? ": " + string.Join(", ", found.Take(10)) : ""));
    }

    /// <summary>No project here is a server plugin: they are RynthServers' (2026-10-21).</summary>
    private static void NoServerPlugins(string root)
    {
        foreach (string proj in Projects(root))
        {
            string rel = Rel(root, proj);
            XDocument doc = XDocument.Load(proj);
            bool serverId = doc.Descendants().Any(e => e.Name.LocalName == "RynthPluginServerId");
            bool serverProps = doc.Descendants().Any(e => e.Name.LocalName == "Import"
                && (e.Attribute("Project")?.Value ?? "").Contains("ServerPlugin.props", StringComparison.OrdinalIgnoreCase));
            Check(!serverId && !serverProps, $"{rel} is not a server plugin (they live in RynthServers)");
        }
        Check(!Directory.Exists(Path.Combine(root, "Plugins", "Shared", "ServerPack")), "Plugins/Shared/ServerPack is RynthServers'");
    }

    /// <summary>The shared plugins' code (comments aside) names no server.</summary>
    private static void SharedCodeNamesNoServer(string root)
    {
        var files = Sources(root).ToList();
        Check(files.Count > 200, $"the plugin sources were found ({files.Count})");
        foreach (string file in files)
        {
            string rel = Rel(root, file);
            if (Allowed.ContainsKey(rel)) continue;
            var named = new List<string>();
            string[] lines = File.ReadAllLines(file);
            bool block = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string code = CodeOf(lines[i], ref block);
                foreach (Match m in ServerNames.Matches(code))
                    if (!GameWords.Any(g => g.File.Equals(rel, StringComparison.OrdinalIgnoreCase) && g.Word == m.Value))
                        named.Add($"line {i + 1} ({m.Value})");
            }
            Check(named.Count == 0, $"{rel} names no server in code: {string.Join(", ", named)}");
        }
    }

    /// <summary>No server's own command or chat marker is sent or read by a shared plugin.</summary>
    private static void NoServerCommands(string root)
    {
        foreach (string file in Sources(root))
        {
            string rel = Rel(root, file);
            if (Allowed.ContainsKey(rel)) continue;
            bool block = false;
            string code = string.Join("\n", File.ReadAllLines(file).Select(l => CodeOf(l, ref block)));
            foreach (string c in ServerCommands)
                Check(!code.Contains(c, StringComparison.Ordinal), $"{rel} sends no server command {c.TrimStart('"')}");
        }
    }

    /// <summary>No project links a server-only source (ILT's contracts, the ServerPack build).</summary>
    private static void NoServerOnlyLinks(string root)
    {
        var serverOnly = new Regex(@"RynthIlt[A-Za-z]*Api\.cs|PetCaptureLines\.cs|[\\/]ServerPack[\\/]|RynthCore\.Plugin\.Rynth(ILT|Threads|Aelrynth|Conquest|Levistras)[\\/]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (string proj in Projects(root))
        {
            string rel = Rel(root, proj);
            var links = XDocument.Load(proj).Descendants()
                .Select(e => e.Attribute("Include")?.Value ?? e.Attribute("Project")?.Value)
                .Where(v => v != null && serverOnly.IsMatch(v!)).ToList();
            Check(links.Count == 0, $"{rel} links no server-only file: {string.Join(", ", links)}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IEnumerable<string> Projects(string root) =>
        Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Where(NotBuildOutput);

    /// <summary>The plugins' and shared sources (Plugins/, Shared/), not tests or tools.</summary>
    private static IEnumerable<string> Sources(string root) =>
        new[] { "Plugins", "Shared" }.Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(NotBuildOutput);

    private static bool NotBuildOutput(string path)
    {
        string p = path.Replace('\\', '/');
        return !p.Contains("/obj/") && !p.Contains("/bin/");
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>A line without its comments (// and /* */ outside string and char literals).</summary>
    private static string CodeOf(string line, ref bool inBlock)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        bool str = false, chr = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inBlock)
            {
                if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { inBlock = false; i++; }
                continue;
            }
            if (str) { sb.Append(c); if (c == '\\' && i + 1 < line.Length) sb.Append(line[++i]); else if (c == '"') str = false; continue; }
            if (chr) { sb.Append(c); if (c == '\\' && i + 1 < line.Length) sb.Append(line[++i]); else if (c == '\'') chr = false; continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlock = true; i++; continue; }
            if (c == '"') str = true;
            else if (c == '\'') chr = true;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !(Directory.Exists(Path.Combine(dir, "Plugins")) && Directory.Exists(Path.Combine(dir, "Tools"))))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("RynthSuite (Plugins + Tools) not found above " + AppContext.BaseDirectory);
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (ok) return;
        _failed++;
        Console.WriteLine("FAIL: " + what);
    }
}

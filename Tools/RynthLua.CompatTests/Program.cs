using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using RynthCore.Plugin.RynthLua;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthLua.CompatTests;

// Runs every Scripts\*.lua through RynthLua's real Lua runtime against the fake engine
// (FakeHost / FakeWorld), one fresh plugin and world per script, and reports PASS/FAIL.
// A script passes when it called done(), every check() / eq() held, at least one ran, and
// its console shows no "error" line. Files go to a temp folder that is removed afterwards.
//
// Run: dotnet run -c Release [-- filter]   (exit 0 = pass, 1 = fail)
internal static unsafe class Program
{
    private const int TimeoutMs = 30_000;

    private static int Main(string[] args)
    {
        string filter = args.Length > 0 ? args[0] : string.Empty;
        string root = Path.Combine(Path.GetTempPath(), "RynthLuaCompat-" + Guid.NewGuid().ToString("N")[..8]);
        RynthLuaPaths.Root = root;
        RynthLuaPaths.LegacyScripts = Path.Combine(root, "no-legacy-scripts");
        RynthLuaPlugin.ExtraGlobals = Harness.Register;

        string scriptDir = ScriptFolder();
        var scripts = Directory.GetFiles(scriptDir, "*.lua")
            .Where(f => Path.GetFileNameWithoutExtension(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"=== RynthLua UtilityBelt compatibility tests ({scripts.Count} scripts) ===");
        int passed = 0, checks = 0;
        var failed = new List<string>();
        if (filter.Length == 0 && !CheckBuiltInVerbsMatchSwitch()) failed.Add("built-in /lua verbs");
        try
        {
            foreach (string file in scripts)
            {
                var (ok, n) = RunOne(file);
                checks += n;
                if (ok) passed++;
                else failed.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed}/{scripts.Count} scripts passed, {checks} checks.");
        if (scripts.Count == 0) { Console.WriteLine("ABORT: no scripts ran."); return 1; }
        if (failed.Count > 0) Console.WriteLine("FAILED: " + string.Join(", ", failed));
        else Console.WriteLine("ALL RYNTHLUA COMPAT TESTS PASSED.");
        return failed.Count == 0 ? 0 : 1;
    }

    private static (bool Ok, int Checks) RunOne(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        FakeWorld.W = new FakeWorld();
        Harness.Reset();

        RynthCoreApiNative api = FakeHost.Build();
        var runtime = new RynthPluginRuntime<RynthLuaPlugin>();
        int init = runtime.Init(&api);
        RynthLuaPlugin? plugin = runtime.Plugin;
        if (init != 0 || plugin == null)
        {
            Console.WriteLine($"FAIL {name}: the plugin didn't initialize ({init})");
            return (false, 0);
        }
        Harness.Plugin = plugin;

        var sw = Stopwatch.StartNew();
        string? startError = null;
        try
        {
            plugin.OnLoginComplete();
            for (int i = 0; i < 3; i++) Tick(plugin);

            Directory.CreateDirectory(RynthLuaPaths.Scripts);
            File.Copy(file, Path.Combine(RynthLuaPaths.Scripts, name + ".lua"), overwrite: true);
            startError = plugin.StartScript(name);

            while (startError == null && !Harness.Done && plugin.ScriptIsLoaded(name) && sw.ElapsedMilliseconds < TimeoutMs)
            {
                Tick(plugin);
                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            startError = "harness exception: " + ex;
        }

        string console = plugin.ScriptConsoleText(name);
        // A script may raise an error on purpose; it marks the message "(expected)".
        var errors = console.Split('\n')
            .Where(l => l.StartsWith("error", StringComparison.OrdinalIgnoreCase) && !l.Contains("(expected)", StringComparison.Ordinal))
            .ToList();
        var reasons = new List<string>();
        if (startError != null) reasons.Add(startError);
        if (!Harness.Done) reasons.Add(sw.ElapsedMilliseconds >= TimeoutMs ? "timed out before done()" : "stopped before done()");
        if (Harness.Checks == 0) reasons.Add("no checks ran");
        reasons.AddRange(Harness.Failures.Select(f => "check: " + f));
        reasons.AddRange(errors.Select(e => "console: " + e));

        try { plugin.StopScript(name); } catch { }
        runtime.Shutdown();

        bool ok = reasons.Count == 0;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name} ({Harness.Checks} checks, {sw.ElapsedMilliseconds} ms)");
        if (!ok)
        {
            foreach (string r in reasons.Take(25)) Console.WriteLine("    " + r);
            Console.WriteLine("    --- console (last 25 lines) ---");
            foreach (string l in console.Split('\n').TakeLast(25)) Console.WriteLine("    | " + l);
        }
        return (ok, Harness.Checks);
    }

    /// <summary>
    /// RynthLuaPlugin.BuiltInLuaVerbs is kept in step with the switch in HandleLuaChatCommand by hand: a verb in
    /// the switch but not the set would be shadowed by a script command of the same name (and listed as a script
    /// command in the usage); a verb in the set but not the switch would be unreachable from both. This compares
    /// the set with the case labels of that switch, read from the source file.
    /// </summary>
    private static bool CheckBuiltInVerbsMatchSwitch([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, "..", "..", "Plugins", "RynthCore.Plugin.RynthLua", "ScriptManager.cs"));
        if (!File.Exists(path)) { Console.WriteLine("SKIP built-in /lua verbs: ScriptManager.cs not found beside the project."); return true; }
        var field = typeof(RynthLuaPlugin).GetField("BuiltInLuaVerbs", BindingFlags.NonPublic | BindingFlags.Static);
        if (field?.GetValue(null) is not HashSet<string> verbs) { Console.WriteLine("FAIL built-in /lua verbs: RynthLuaPlugin.BuiltInLuaVerbs not found."); return false; }

        string text = File.ReadAllText(path);
        int start = text.IndexOf("switch (sub)", StringComparison.Ordinal);
        int end = start < 0 ? -1 : text.IndexOf("default:", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) { Console.WriteLine("FAIL built-in /lua verbs: the 'switch (sub)' in HandleLuaChatCommand was not found."); return false; }
        var cases = Regex.Matches(text.Substring(start, end - start), @"case\s+""([^""]+)""\s*:")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingFromSet = cases.Except(verbs, StringComparer.OrdinalIgnoreCase).ToList();
        var missingFromSwitch = verbs.Except(cases, StringComparer.OrdinalIgnoreCase).ToList();
        bool ok = cases.Count > 0 && missingFromSet.Count == 0 && missingFromSwitch.Count == 0;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} built-in /lua verbs match the dispatch switch ({cases.Count} verbs)");
        if (missingFromSet.Count > 0) Console.WriteLine("    in the switch but not in BuiltInLuaVerbs: " + string.Join(", ", missingFromSet));
        if (missingFromSwitch.Count > 0) Console.WriteLine("    in BuiltInLuaVerbs but not in the switch: " + string.Join(", ", missingFromSwitch));
        if (cases.Count == 0) Console.WriteLine("    no case labels found (did the switch change shape?)");
        return ok;
    }

    /// <summary>The project's own Scripts folder (so a deleted script can't linger in bin), else the copy in bin.</summary>
    private static string ScriptFolder([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        string beside = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, "Scripts");
        return Directory.Exists(beside) ? beside : Path.Combine(AppContext.BaseDirectory, "Scripts");
    }

    private static void Tick(RynthLuaPlugin plugin)
    {
        FakeWorld.W.Step();
        while (FakeWorld.W.Events.Count > 0) FakeWorld.W.Events.Dequeue()(plugin);
        plugin.OnTick();
    }
}

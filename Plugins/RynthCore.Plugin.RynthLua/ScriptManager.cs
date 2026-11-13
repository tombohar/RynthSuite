using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthLua;

/// <summary>RynthLua's own settings (C:\Games\RynthSuite\RynthLua\settings.json).</summary>
public sealed class LuaSettings
{
    /// <summary>Scripts that start on every character's login.</summary>
    public List<string> GlobalScripts { get; set; } = new();
    /// <summary>"server|character" → scripts that start on that character's login.</summary>
    public Dictionary<string, List<string>> CharacterScripts { get; set; } = new();
    /// <summary>Scripts whose manifest autostart was applied once (so turning it off sticks).</summary>
    public List<string> SeededFromManifest { get; set; } = new();
}

[JsonSerializable(typeof(ScriptManifest))]
[JsonSerializable(typeof(LuaSettings))]
internal partial class RynthLuaSettingsJsonContext : JsonSerializerContext { }

/// <summary>A script the panel and /lua list can show.</summary>
internal sealed record ScriptEntry(string Name, string? Folder, string EntryFile, ScriptManifest Manifest);

/// <summary>
/// Scripts: discovery (folder scripts with index.lua, single-file Name.lua), start / stop /
/// restart, several at once, autostart, the shared tick budget and the /lua commands.
/// </summary>
public sealed partial class RynthLuaPlugin
{
    private static string SettingsFile => RynthLuaPaths.Settings;
    // CPU per game tick across every script. Stopwatch, not TickCount64: that clock
    // moves in ~15.6 ms steps, which let a busy loop take a whole step.
    private static readonly long TickBudgetTicks = System.Diagnostics.Stopwatch.Frequency * 4 / 1000;

    private readonly Dictionary<string, ScriptContext> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private int _tickRotation;
    private LuaSettings _settings = new();
    private bool _settingsLoaded;

    /// <summary>The script the panel shows (its console, and the editor's run target).</summary>
    private string _selected = string.Empty;

    // ── Discovery ───────────────────────────────────────────────────────────

    internal static List<ScriptEntry> DiscoverScripts()
    {
        var list = new List<ScriptEntry>();
        try
        {
            if (!Directory.Exists(LuaScriptHost.ScriptFolder)) return list;
            foreach (string dir in Directory.GetDirectories(LuaScriptHost.ScriptFolder))
            {
                string index = Path.Combine(dir, "index.lua");
                if (!File.Exists(index)) continue;
                string name = Path.GetFileName(dir);
                list.Add(new ScriptEntry(name, dir, index, ReadManifest(dir)));
            }
            foreach (string f in Directory.GetFiles(LuaScriptHost.ScriptFolder, "*.lua"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (list.Exists(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new ScriptEntry(name, null, f, new ScriptManifest()));
            }
        }
        catch { }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static ScriptManifest ReadManifest(string dir)
    {
        try
        {
            string file = Path.Combine(dir, "script.json");
            if (File.Exists(file))
                return JsonSerializer.Deserialize(File.ReadAllText(file), RynthLuaSettingsJsonContext.Default.ScriptManifest) ?? new ScriptManifest();
        }
        catch { }
        return new ScriptManifest();
    }

    private static ScriptEntry? FindScript(string name)
    {
        string n = (name ?? string.Empty).Trim();
        if (n.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) n = n[..^4].TrimEnd();
        return DiscoverScripts().FirstOrDefault(e => e.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    // ── Start / stop ────────────────────────────────────────────────────────

    private ScriptContext NewContext(string name, string? folder, string entry, ScriptManifest manifest, bool transient)
    {
        ScriptContext? ctx = null;
        var host = new LuaScriptHost(s => Log(s), (s, h) => RegisterApi(s, h, ctx!))
        {
            MacroRunning = () => _ai.MacroRunning,
            // The memory guard's stop is said in chat in the failure colour (it's in the log via Reply too).
            MemoryStopped = msg => ChatFail(msg),
        };
        ctx = new ScriptContext(name, folder, entry, manifest, host) { Transient = transient };
        host.ResolveModule = (s, module) => ResolveModule(s, ctx, module);
        return ctx;
    }

    /// <summary>Starts (or restarts) a script from its file. Returns an error message, or null.</summary>
    internal string? StartScript(string name)
    {
        ScriptEntry? e = FindScript(name);
        if (e == null) return $"No script named '{name}'. /lua list shows them.";
        string code;
        try { code = File.ReadAllText(e.EntryFile); }
        catch (Exception ex) { return $"Can't read {e.EntryFile}: {ex.Message}"; }
        StopScript(e.Name, "restarted");
        var ctx = NewContext(e.Name, e.Folder, e.EntryFile, e.Manifest, transient: false);
        _scripts[e.Name] = ctx;
        _selected = e.Name;
        ctx.Host.Run(code, e.Name);
        return ctx.Host.Loaded ? null : $"{e.Name} failed to start; see its console in the Lua panel.";
    }

    /// <summary>Runs code as a script called <paramref name="name"/> (panel editor, /lua exec).</summary>
    internal ScriptContext RunCode(string name, string code)
    {
        string n = string.IsNullOrWhiteSpace(name) ? "editor" : name.Trim();
        StopScript(n, "replaced by a new run");
        ScriptEntry? e = FindScript(n);
        var ctx = NewContext(n, e?.Folder, e?.EntryFile ?? string.Empty, e?.Manifest ?? new ScriptManifest(), transient: true);
        _scripts[n] = ctx;
        _selected = n;
        ctx.Host.Run(code, n);
        return ctx;
    }

    internal bool StopScript(string name, string reason = "stopped")
    {
        if (!_scripts.TryGetValue(name, out var ctx) || !ctx.Host.Loaded) return false;
        FireScriptEnd(ctx);
        ctx.Host.Stop(reason);
        return true;
    }

    internal void StopAll(string reason, Func<ScriptContext, bool>? which = null)
    {
        foreach (var ctx in _scripts.Values.ToArray())
            if (ctx.Host.Loaded && (which == null || which(ctx)))
            {
                FireScriptEnd(ctx);
                ctx.Host.Stop(reason);
            }
    }

    internal IEnumerable<ScriptContext> RunningScripts() => _scripts.Values.Where(c => c.Host.Loaded);

    // ── Tick ────────────────────────────────────────────────────────────────

    private void TickScripts()
    {
        if (_scripts.Count == 0) return;
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + TickBudgetTicks;
        var list = _scripts.Values.Where(c => c.Host.Loaded).ToList();
        if (list.Count == 0) return;
        int start = _tickRotation++ % list.Count;
        for (int k = 0; k < list.Count; k++)
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline) break;
            var ctx = list[(start + k) % list.Count];
            try { ctx.Host.Tick(deadline); }
            catch (Exception ex) { ctx.Host.Print("error: " + ex.Message); }
        }
    }

    // ── Autostart ───────────────────────────────────────────────────────────

    private void LoadSettings()
    {
        if (_settingsLoaded) return;
        _settingsLoaded = true;
        try
        {
            if (File.Exists(SettingsFile))
                _settings = JsonSerializer.Deserialize(File.ReadAllText(SettingsFile), RynthLuaSettingsJsonContext.Default.LuaSettings) ?? new LuaSettings();
        }
        catch { _settings = new LuaSettings(); }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings, RynthLuaSettingsJsonContext.Default.LuaSettings));
        }
        catch { }
    }

    /// <summary>"server|character" for the logged-in character, or "" before login.</summary>
    internal string CharacterKey()
    {
        uint id = Host.GetPlayerId();
        string ch = id != 0 && Host.TryGetObjectName(id, out string n) ? n : string.Empty;
        string server = Host.TryGetWorldName(out string w) ? w : string.Empty;
        return ch.Length == 0 ? string.Empty : $"{server}|{ch}";
    }

    /// <summary>"global", "character" or "none" for this script and character.</summary>
    internal string AutostartOf(string name)
    {
        LoadSettings();
        if (_settings.GlobalScripts.Contains(name, StringComparer.OrdinalIgnoreCase)) return "global";
        string key = CharacterKey();
        if (key.Length > 0 && _settings.CharacterScripts.TryGetValue(key, out var l) && l.Contains(name, StringComparer.OrdinalIgnoreCase))
            return "character";
        return "none";
    }

    internal void SetAutostart(string name, string mode)
    {
        LoadSettings();
        string key = CharacterKey();
        _settings.GlobalScripts.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (key.Length > 0 && _settings.CharacterScripts.TryGetValue(key, out var l))
            l.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (mode == "global") _settings.GlobalScripts.Add(name);
        else if (mode == "character" && key.Length > 0)
        {
            if (!_settings.CharacterScripts.TryGetValue(key, out var list)) _settings.CharacterScripts[key] = list = new List<string>();
            list.Add(name);
        }
        if (!_settings.SeededFromManifest.Contains(name, StringComparer.OrdinalIgnoreCase)) _settings.SeededFromManifest.Add(name);
        SaveSettings();
    }

    /// <summary>On login: seed autostart from manifests (once per script), then start what's set.</summary>
    private void RunAutostart()
    {
        LoadSettings();
        var all = DiscoverScripts();
        foreach (var e in all)
        {
            if (_settings.SeededFromManifest.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) continue;
            string m = (e.Manifest.Autostart ?? "none").Trim().ToLowerInvariant();
            if (m is "global" or "character") SetAutostart(e.Name, m);
        }
        foreach (var e in all)
        {
            string mode = AutostartOf(e.Name);
            if (mode == "none") continue;
            if (_scripts.TryGetValue(e.Name, out var running) && running.Host.Loaded) continue;   // global ones keep running across logins
            string? err = StartScript(e.Name);
            Log(err == null ? $"[RynthLua] Autostarted {e.Name} ({mode})." : $"[RynthLua] Autostart {e.Name}: {err}");
        }
    }

    // ── /lua commands ───────────────────────────────────────────────────────

    /// <summary>
    /// Every built-in /lua verb. The one list behind both the dispatch gate in HandleLuaChatCommand
    /// (a verb never falls back to a script command) and the usage summary (a script command with
    /// one of these names is unreachable through /lua). A new verb goes in the switch AND here.
    /// </summary>
    private static readonly HashSet<string> BuiltInLuaVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "start", "restart", "stop", "exec", "execin", "autostart", "list", "hud",
    };

    private void HandleLuaChatCommand(string line)
    {
        string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
        if (sub.StartsWith('/')) sub = sub[1..];   // "/lua /demo": the usage lists names with a slash, one is accepted
        string rest = parts.Length > 2 ? parts[2].Trim() : "";
        // Not a built-in verb (those always win): in a Decal (bridge-mode) client the engine only
        // forwards /lua, so "/lua demo status" (or "/lua /demo status", as the usage text lists them) is
        // the way to reach a script's game.RegisterCommand("/demo"). Same queue as a typed "/demo status":
        // the handler runs once on the tick thread with args "status".
        if (!BuiltInLuaVerbs.Contains(sub))
        {
            if (sub.Length > 0 && TryQueueScriptCommand("/" + sub + (rest.Length > 0 ? " " + rest : ""))) return;
        }
        switch (sub)
        {
            case "run":
            case "start":
            {
                string? err = StartScript(rest);
                if (err != null) ChatFail(err);
                else ChatLine($"Running {FindScript(rest)?.Name ?? rest}.");
                break;
            }
            case "restart":
            {
                string? err = StartScript(rest);
                if (err != null) ChatFail(err);
                else ChatLine($"Restarted {FindScript(rest)?.Name ?? rest}.");
                break;
            }
            case "stop":
                if (rest.Length == 0 || rest.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    int n = RunningScripts().Count();
                    StopAll("stopped");
                    ChatLine(n == 0 ? "No script is running." : $"Stopped {n} script(s).");
                }
                else if (StopScript(FindScript(rest)?.Name ?? rest)) ChatLine($"Stopped {rest}.");
                else ChatFail($"{rest} isn't running.");
                break;
            case "exec":
            {
                if (rest.Length == 0) { ChatFail("Usage: /lua exec <code>"); break; }
                ScriptContext ran = RunCode("exec", rest);
                // Its output and errors go to the script console (the Lua window); chat says whether it started.
                if (ran.Host.Loaded) ChatLine("exec: started (its output is in the Lua window's console).");
                else ChatFail("exec: the code didn't run" + LastConsoleError(ran));
                break;
            }
            case "execin":
            {
                string[] p = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2) { ChatFail("Usage: /lua execin <script> <code>"); break; }
                if (!_scripts.TryGetValue(p[0], out var ctx) || !ctx.Host.Loaded) { ChatFail($"{p[0]} isn't running."); break; }
                if (ctx.Host.Exec(p[1])) ChatLine($"execin {ctx.Name}: started (its output is in {ctx.Name}'s console).");
                else ChatFail($"execin {ctx.Name}: the code didn't run" + LastConsoleError(ctx));
                break;
            }
            case "autostart":
            {
                string[] p = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                string mode = p.Length > 1 ? p[1].ToLowerInvariant() : "";
                var e = p.Length > 0 ? FindScript(p[0]) : null;
                if (e == null || mode is not ("none" or "global" or "character"))
                { ChatFail("Usage: /lua autostart <script> none|global|character"); break; }
                SetAutostart(e.Name, mode);
                ChatLine($"{e.Name}: autostart {mode}.");
                break;
            }
            case "list":
            {
                EnsureScripts();
                var all = DiscoverScripts();
                if (all.Count == 0) { ChatLine($"No scripts in {LuaScriptHost.ScriptFolder}."); break; }
                ChatLine($"Scripts ({all.Count}): " + string.Join(", ", all.Select(e =>
                    e.Name + (_scripts.TryGetValue(e.Name, out var c) && c.Host.Loaded ? " [running]" : "")
                           + (AutostartOf(e.Name) is var a && a != "none" ? $" [auto:{a}]" : ""))));
                break;
            }
            case "hud":
                HandleHudCommand(rest);
                break;
            default:
            {
                // Bare /lua and /lua help: the usage. A word that is neither a verb nor a script's command: why, then the usage.
                const string usage = "/lua list | start <script> | stop <script>|all | restart <script> | exec <code> | execin <script> <code> | autostart <script> none|global|character | hud list|show|hide|toggle <script>/<hud>|bench <n>|bench off";
                if (sub is "" or "help" or "?") ChatLine(usage);
                else ChatFail($"Unknown '/lua {sub}'. {usage}");
                ChatLine("/lua <command> [args] runs a script command (game.RegisterCommand) - " + ScriptCommandsSummary());
                break;
            }
        }
    }

    /// <summary>": " and the last error line in the script's console (why its code didn't run), or ".".</summary>
    private static string LastConsoleError(ScriptContext ctx)
    {
        string[] lines = ctx.Host.ConsoleText().Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].StartsWith("error", StringComparison.OrdinalIgnoreCase)) return ": " + lines[i].Trim();
        return ".";
    }

    /// <summary>The registered script commands for the /lua usage text.</summary>
    private string ScriptCommandsSummary()
    {
        // Names as /lua takes them (no slash); a name equal to a built-in verb can't be reached here.
        var names = _commandOwners.Keys
            .Select(k => k.TrimStart('/'))
            .Where(n => n.Length > 0 && !BuiltInLuaVerbs.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names.Count == 0 ? "none registered now." : "registered: " + string.Join(", ", names) + ".";
    }
}

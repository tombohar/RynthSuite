using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoonSharp.Interpreter;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthLua;

/// <summary>
/// Panel → plugin Lua command (RynthPluginSendLuaCommand). Cmd: run (Code as Name), stop,
/// clearConsole, load, save, delete (the editor's single-file scripts, as before), and
/// select, start, stopScript, restart, autostart (Code = none|global|character), execin (Code).
/// </summary>
public sealed class LuaCommand
{
    public string Cmd  { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

/// <summary>One script in the panel's list.</summary>
public sealed class LuaScriptInfo
{
    public string Name        { get; set; } = string.Empty;
    public bool   Running     { get; set; }
    public string Autostart   { get; set; } = "none";
    public string Description { get; set; } = string.Empty;
    /// <summary>True for a folder script (index.lua), false for a single Name.lua.</summary>
    public bool   IsFolder    { get; set; }
    /// <summary>True for a script with no file (the editor, /lua exec): no autostart, restart or load.</summary>
    public bool   Transient   { get; set; }
}

/// <summary>Plugin → panel Lua state (RynthPluginGetLuaJson). Console/Running/ScriptName are the selected script's.</summary>
public sealed class LuaBridgePayload
{
    public bool         Running    { get; set; }
    public string       ScriptName { get; set; } = string.Empty;
    public string       Status     { get; set; } = string.Empty;
    public List<string> Scripts    { get; set; } = new();
    public string       Console    { get; set; } = string.Empty;
    public int          ConsoleSeq { get; set; }
    public string       LoadedText { get; set; } = string.Empty;
    public string       LoadedName { get; set; } = string.Empty;
    public int          LoadSeq    { get; set; }
    public List<LuaScriptInfo> ScriptInfos { get; set; } = new();
    public string       Selected   { get; set; } = string.Empty;
    public int          RunningCount { get; set; }
    public bool         RynthAi    { get; set; }
    /// <summary>A character is logged in (per-character autostart needs one).</summary>
    public bool         LoggedIn   { get; set; }
}

[JsonSerializable(typeof(LuaCommand))]
[JsonSerializable(typeof(LuaBridgePayload))]
internal partial class RynthLuaJsonContext : JsonSerializerContext { }

/// <summary>
/// RynthLua: Lua scripting for RynthCore (moved out of RynthAi, 2026-09-29). Several scripts
/// run at once, each in its own Lua state, as coroutines on the plugin pump thread
/// (ScriptManager, LuaScriptHost). The API is UtilityBelt-shaped (GameApi and the area
/// files) plus the v1 globals (LuaApi). RynthAi's features come through its "RynthAi.Script"
/// interface (RynthAiBridge). Design: docs/RYNTHLUA.md in RynthSuite.
/// </summary>
public sealed partial class RynthLuaPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer    = Marshal.StringToHGlobalAnsi("RynthLua");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("2.0.0");

    private static string DataFolder => RynthLuaPaths.Root;
    private static string EditorFile => RynthLuaPaths.Editor;

    internal RynthAiBridge _ai = null!;
    private readonly ConcurrentQueue<LuaCommand> _panelCommands = new();
    private readonly ConcurrentQueue<string> _chatCommands = new();   // "/lua ..." lines, run on the next tick

    private string _editorText = string.Empty;
    private string _editorName = string.Empty;
    private int _loadSeq;
    private bool _editorSeeded;
    private List<ScriptEntry> _entries = new();
    private long _entriesAt;
    private bool _examplesChecked;

    public override int Initialize()
    {
        _ = Reply;
        _ai = new RynthAiBridge(Host);
        // Every use logged (Plugins/Shared/UseAudit.cs). Scripts are the player's: no guard.
        RynthCore.Plugin.Shared.UseAudit.Reset("RynthLua", null);
        Log("[RynthLua] Initialized.");
        try { LoadResume(); }   // a hot reload: the scripts started by hand come back at login
        catch (Exception ex) { Log("[RynthLua] resume: " + ex.Message); }
        return 0;
    }

    public override void Shutdown()
    {
        // Forget RynthAi's cached tables first: stopping the scripts releases their combat
        // handlers (ClearCombatHandlers -> CombatRelease), and RynthAi may have been unloaded or
        // reloaded since the last tick looked them up, so a cached pointer could be stale.
        // (Null when Initialize never ran: nothing is cached and nothing must throw here.)
        try { _ai?.Reset(); } catch { }
        // The resume list is taken while the scripts still run; the file is written last.
        List<string> resume = new();
        try { resume = CaptureResume(); } catch { }
        try { StopAll("stopped: RynthLua unloaded"); } catch (Exception ex) { Log("[RynthLua] shutdown: " + ex.Message); }
        try { ReleaseCombatHook(); } catch { }   // RynthAi goes native now, not when the lease lapses
        try { ShutdownHuds(); } catch { }
        FlushHudMemory();
        SaveEditor();
        try { WriteResumeFile(ResumeFile, resume, Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), s => Log(s)); }
        catch { }
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        // Plugins now unload on hot reload (collectible load contexts): never keep RynthAi's
        // table across ticks, look it up again on first use each tick.
        _ai.Reset();
        while (_panelCommands.TryDequeue(out var cmd))
        {
            try { ApplyPanelCommand(cmd); }
            catch (Exception ex) { PrintSelected("error: " + ex.Message); }
        }
        while (_chatCommands.TryDequeue(out string? line))
        {
            try { HandleLuaChatCommand(line); }
            catch (Exception ex) { ChatFail("error: " + ex.Message); }
        }
        Reply.Flush();
        // Script windows are kept apart from the scripts: a fault in the window steps is logged
        // (at most every 10 s) and can't stop every script's threads from running.
        try { PollHudEvents(); }   // script windows: input first, so this tick's passes see it
        catch (Exception ex) { LogHudFault("events", ex); }
        TickApi();
        // RynthAi's combat hook: renew the lease, start handlers for new requests (they run in
        // TickScripts just below, so a handler that doesn't yield answers in this same tick).
        try { PumpCombatHook(); }
        catch (Exception ex) { Log("[RynthLua] combat hook: " + ex.Message); }
        TickScripts();
        try { TickHuds(); }        // render passes that are due, then submit what changed
        catch (Exception ex) { LogHudFault("passes", ex); }
        TickHudMemory();   // the player's hud show/hide choices, written ~2 s after the last one
    }

    private long _lastHudFaultLog;

    private void LogHudFault(string step, Exception ex)
    {
        if (Environment.TickCount64 - _lastHudFaultLog < 10_000) return;
        _lastHudFaultLog = Environment.TickCount64;
        Log($"[RynthLua] script windows ({step}): {ex.GetType().Name}: {ex.Message}");
    }

    // ── Events → scripts (all on the pump thread) ───────────────────────────

    /// <summary>v1 on(evt, fn) handlers in every running script.</summary>
    private void FireV1(string evt, params DynValue[] args)
    {
        foreach (var ctx in _scripts.Values.ToArray())
            if (ctx.Host.HasHandlers(evt)) ctx.Host.Fire(evt, args);
    }

    public override void OnLoginComplete()
    {
        _ai.Reset();
        SetGameState("InGame");
        WorldOnLogin();
        CharacterOnLogin();
        FireV1("login");
        try { RunAutostart(); }
        catch (Exception ex) { Log("[RynthLua] autostart: " + ex.Message); }
        try { ResumeScripts(); }   // after autostart, so nothing starts twice
        catch (Exception ex) { Log("[RynthLua] resume: " + ex.Message); }
    }

    public override void OnLogout()
    {
        SetGameState("LoggingOut");
        FireV1("logout");
        // Global autostart scripts keep running across characters; everything else stops.
        StopAll("stopped: logged out", ctx => AutostartOf(ctx.Name) != "global");
        ActionsOnLogout();
        CharacterOnLogout();
        WorldOnLogout();
        _ai.Reset();
        FlushHudMemory();
        SaveEditor();
        SetGameState("CharacterSelect");
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (string.IsNullOrEmpty(text)) return;
        FireV1("chat", DynValue.NewString(text), DynValue.NewNumber(chatType));
        WorldOnChatText(text, chatType);
        CharacterOnChatText(text, chatType);
        ActionsOnChatText(text, chatType);
    }

    public override void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId)
    {
        FireV1("target", DynValue.NewNumber(currentTargetId), DynValue.NewNumber(previousTargetId));
        WorldOnSelected(currentTargetId, previousTargetId);
    }

    public override void OnCombatModeChange(int currentCombatMode, int previousCombatMode)
    {
        FireV1("combatmode", DynValue.NewNumber(currentCombatMode), DynValue.NewNumber(previousCombatMode));
        CharacterOnCombatModeChange(currentCombatMode, previousCombatMode);
    }

    public override void OnCreateObject(uint objectId)
    {
        FireV1("objectcreated", DynValue.NewNumber(objectId));
        WorldOnCreateObject(objectId);
    }

    public override void OnDeleteObject(uint objectId)
    {
        FireV1("objectdeleted", DynValue.NewNumber(objectId));
        WorldOnDeleteObject(objectId);
    }

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        FireV1("health", DynValue.NewNumber(targetId), DynValue.NewNumber(healthRatio),
            DynValue.NewNumber(currentHealth), DynValue.NewNumber(maxHealth));
        CharacterOnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);
    }

    public override void OnCombatDamage(uint damage, uint damageType, bool crit, bool isAttacker)
        => FireV1(isAttacker ? "damagedealt" : "damagetaken", DynValue.NewNumber(damage), DynValue.NewNumber(damageType), DynValue.NewBoolean(crit));

    public override void OnKillNotification(string? deathMessage)
        => FireV1("kill", DynValue.NewString(deathMessage ?? string.Empty));

    public override void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        FireV1("enchantmentadded", DynValue.NewNumber(spellId), DynValue.NewNumber(durationSeconds));
        CharacterOnEnchantmentAdded(spellId, durationSeconds);
    }

    public override void OnEnchantmentRemoved(uint enchantmentId)
    {
        FireV1("enchantmentremoved", DynValue.NewNumber(enchantmentId));
        CharacterOnEnchantmentRemoved(enchantmentId);
    }

    public override void OnVendorOpen(uint vendorId)
    {
        FireV1("vendoropen", DynValue.NewNumber(vendorId));
        WorldOnVendor(vendorId, open: true);
    }

    public override void OnVendorClose(uint vendorId)
    {
        FireV1("vendorclose", DynValue.NewNumber(vendorId));
        WorldOnVendor(vendorId, open: false);
    }

    // ── Chat commands ───────────────────────────────────────────────────────

    /// <summary>"/lua ..." and script commands typed in chat. AC's thread: only queue for the next tick.</summary>
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Equals("/lua", StringComparison.OrdinalIgnoreCase) || t.StartsWith("/lua ", StringComparison.OrdinalIgnoreCase))
        {
            _chatCommands.Enqueue(t);
            eat = 1;
            WorldQueueChatInput(t, alreadyEaten: true);
            return;
        }
        if (t.StartsWith('/') && TryQueueScriptCommand(t)) eat = 1;
        // World.OnChatInput (next tick); a command a handler ate before is eaten now.
        if (WorldQueueChatInput(t, alreadyEaten: eat != 0)) eat = 1;
    }

    /// <summary>RynthAi forwards "/ra lua ..." (typed or from a meta) here as ("lua", "run X").</summary>
    internal void ApplyRemoteCommand(string action, string value)
    {
        if (action.Equals("lua", StringComparison.OrdinalIgnoreCase))
            _chatCommands.Enqueue("/lua " + value);
    }

    // Chat lines wait in the shared reply queue (Plugins/Shared/CommandReply.cs): the host drops a line
    // within 100 ms of another plugin's, so /lua's two-line usage used to lose its second line.
    private RynthCore.Plugin.Shared.CommandReply? _reply;
    private RynthCore.Plugin.Shared.CommandReply Reply => _reply ??= new RynthCore.Plugin.Shared.CommandReply("[RynthLua]",
        (t, c) => Host.HasWriteToChat && Host.WriteToChat(t, c), t => Log(t));

    internal void ChatLine(string text) => Reply.Ok(text);

    /// <summary>A /lua command that didn't work: why, and the usage (the failure colour).</summary>
    internal void ChatFail(string text) => Reply.Fail(text);

    // ── Panel ───────────────────────────────────────────────────────────────

    internal void EnqueueLuaCommand(LuaCommand cmd) => _panelCommands.Enqueue(cmd);

    private ScriptContext? Selected => _scripts.TryGetValue(_selected, out var c) ? c : null;

    private void PrintSelected(string text)
    {
        var ctx = Selected ?? _scripts.Values.FirstOrDefault();
        if (ctx != null) ctx.Host.Print(text);
        Log("[RynthLua] " + text);
    }

    private void ApplyPanelCommand(LuaCommand cmd)
    {
        switch (cmd.Cmd)
        {
            case "run":
                SetEditor(cmd.Code, cmd.Name, bumpSeq: false);
                RunCode(cmd.Name, cmd.Code);
                break;
            case "stop":
                if (Selected != null) StopScript(Selected.Name);
                break;
            case "clearConsole":
                Selected?.Host.ClearConsole();
                break;
            case "select":
                _selected = cmd.Name;
                if (!_scripts.ContainsKey(cmd.Name))
                {
                    var e = DiscoverScripts().FirstOrDefault(x => x.Name.Equals(cmd.Name, StringComparison.OrdinalIgnoreCase));
                    if (e != null) _scripts[e.Name] = NewContext(e.Name, e.Folder, e.EntryFile, e.Manifest, transient: false);
                }
                break;
            case "start":
            case "restart":
            {
                string? err = StartScript(cmd.Name);
                if (err != null) PrintSelected(err);
                break;
            }
            case "stopScript":
                StopScript(cmd.Name);
                break;
            case "autostart":
                if (cmd.Code == "character" && CharacterKey().Length == 0) { PrintSelected("Log in first to set a script to start for this character."); break; }
                SetAutostart(cmd.Name, cmd.Code is "global" or "character" ? cmd.Code : "none");
                break;
            case "execin":
                if (_scripts.TryGetValue(cmd.Name, out var target) && target.Host.Loaded) target.Host.Exec(cmd.Code);
                else PrintSelected($"{cmd.Name} isn't running.");
                break;
            case "load":
            {
                var e = DiscoverScripts().FirstOrDefault(x => x.Name.Equals(cmd.Name, StringComparison.OrdinalIgnoreCase));
                if (e == null) { PrintSelected($"No script named '{cmd.Name}'."); break; }
                SetEditor(File.ReadAllText(e.EntryFile), e.Name, bumpSeq: true);
                break;
            }
            case "save":
            {
                // Folder scripts save their index.lua; everything else a single Name.lua.
                var e = DiscoverScripts().FirstOrDefault(x => x.Name.Equals(cmd.Name, StringComparison.OrdinalIgnoreCase));
                string? path = e?.EntryFile ?? LuaScriptHost.PathFor(cmd.Name);
                if (path == null) { PrintSelected($"'{cmd.Name}' can't be used as a file name."); break; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, cmd.Code);
                SetEditor(cmd.Code, e?.Name ?? Path.GetFileNameWithoutExtension(path), bumpSeq: false);
                _entriesAt = 0;
                PrintSelected($"Saved {path}.");
                break;
            }
            case "delete":
            {
                string? path = LuaScriptHost.PathFor(cmd.Name);
                if (path == null || !File.Exists(path)) { PrintSelected($"No single-file script named '{cmd.Name}' (folder scripts are deleted in Explorer)."); break; }
                // Kept as .bak, not destroyed: a click shouldn't lose someone's script.
                string bak = path + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
                _entriesAt = 0;
                PrintSelected($"Deleted {Path.GetFileName(path)} (kept as {Path.GetFileName(bak)}).");
                break;
            }
        }
    }

    private void SetEditor(string text, string name, bool bumpSeq)
    {
        _editorText = text ?? string.Empty;
        _editorName = name ?? string.Empty;
        _editorSeeded = true;
        if (bumpSeq) _loadSeq++;
        SaveEditor();
    }

    private void SeedEditor()
    {
        if (_editorSeeded) return;
        _editorSeeded = true;
        try { if (File.Exists(EditorFile)) _editorText = File.ReadAllText(EditorFile); }
        catch { }
        if (_editorText.Length == 0) _editorText = "-- Enter your Lua script here\nprint('Hello from RynthLua')";
    }

    private void SaveEditor()
    {
        if (!_editorSeeded) return;
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(EditorFile, _editorText);
        }
        catch { }
    }

    private void EnsureScripts()
    {
        if (_examplesChecked) return;
        _examplesChecked = true;
        LuaExamples.EnsureWritten(s => Log(s));
    }

    internal string BuildLuaJson()
    {
        SeedEditor();
        long now = Environment.TickCount64;
        if (now - _entriesAt > 2000)
        {
            EnsureScripts();
            _entries = DiscoverScripts();
            _entriesAt = now;
        }

        ScriptContext? sel = Selected;
        var payload = new LuaBridgePayload
        {
            Running    = sel?.Host.Loaded ?? false,
            ScriptName = sel?.Name ?? string.Empty,
            Status     = sel?.Host.Status ?? string.Empty,
            Scripts    = _entries.Select(e => e.Name).ToList(),
            Console    = sel?.Host.ConsoleText() ?? string.Empty,
            ConsoleSeq = sel?.Host.ConsoleSeq ?? 0,
            LoadedText = _editorText,
            LoadedName = _editorName,
            LoadSeq    = _loadSeq,
            Selected   = _selected,
            RunningCount = _scripts.Values.Count(c => c.Host.Loaded),
            RynthAi    = _ai.Available,
            LoggedIn   = CharacterKey().Length > 0,
        };
        foreach (var e in _entries)
            payload.ScriptInfos.Add(new LuaScriptInfo
            {
                Name = e.Name,
                Running = _scripts.TryGetValue(e.Name, out var c) && c.Host.Loaded,
                Autostart = AutostartOf(e.Name),
                Description = e.Manifest.Description ?? string.Empty,
                IsFolder = e.Folder != null,
            });
        // Scripts that aren't files (the editor, /lua exec) show while running, selected, or
        // while they still have console output to read.
        foreach (var c in _scripts.Values)
            if ((c.Host.Loaded || c.Transient && (c.Host.ConsoleSeq > 0 || c.Name.Equals(_selected, StringComparison.OrdinalIgnoreCase)))
                && !payload.ScriptInfos.Exists(i => i.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                payload.ScriptInfos.Add(new LuaScriptInfo { Name = c.Name, Running = c.Host.Loaded, Description = "(not saved)", Transient = true });
        return JsonSerializer.Serialize(payload, RynthLuaJsonContext.Default.LuaBridgePayload);
    }
}

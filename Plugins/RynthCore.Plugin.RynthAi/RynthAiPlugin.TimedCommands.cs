using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.Diagnostics;
using RynthCore.Plugin.RynthAi.TimedCommands;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

// Timed commands (2026-11-03): chat commands sent every N seconds, per character, per server and
// for every character (TimedCommands/TimedCommands.cs has the rules and the files). The lists load
// once the character's settings folder is known (after login, a hot reload or the late settings
// retry), the tick sends what is due and saves edits a second after the last one, and the Timed
// Commands window (a plugin window, API v89, with no bar button: the dashboard's options menu and
// /ra timed show open it) and /ra timed edit them.
public sealed partial class RynthAiPlugin
{
    internal const string TimedCommandsTitle = "Timed Commands", TimedCommandsSurface = "window:timedcommands";
    private const long TimedSaveAfterMs = 1000;

    private readonly TimedCommandSet _timed = new();
    private readonly TimedCommandScheduler _timedScheduler = new();
    private TimedCommandsWindow? _timedWindow;
    private string _timedLoadedFor = string.Empty;   // the settings folder the lists are for
    private int _timedFailures;

    /// <summary>The tick (TickStep.TimedCommands): load for this character, save edits, send what is due.</summary>
    private void TickTimedCommands()
    {
        if (!_loginComplete || _dashboard == null) return;
        string charFolder = _dashboard.CharFolder;
        if (string.IsNullOrEmpty(charFolder)) return;
        long now = Environment.TickCount64;
        if (!string.Equals(charFolder, _timedLoadedFor, StringComparison.OrdinalIgnoreCase))
        {
            SaveTimedCommands();
            var problems = new List<string>();
            _timed.Load(charFolder, problems);
            _timedLoadedFor = charFolder;
            _timedScheduler.OnLogin(_timed, now);
            foreach (string p in problems) ChatLine($"[RynthAi] Timed commands: couldn't read the list for {p}.");
            int n;
            lock (_timed.Sync) n = _timed.List(TimedScope.Character).Count + _timed.List(TimedScope.Server).Count + _timed.List(TimedScope.All).Count;
            if (n > 0) Host.Log($"[RynthAi] Timed commands: {n} loaded for {_timed.Character} on {_timed.Server}.");
        }

        if (_timed.AnyDirty && now - _timed.ChangedAtMs >= TimedSaveAfterMs) SaveTimedCommands();

        var st = new TimedRunState(now, InWorld: Host.GetPlayerId() != 0, Portaling: RynthCore.Plugin.Shared.ActionPacer.IsPortaling(Host),
            MacroRunning: _dashboard.Settings.IsMacroRunning);
        if (_timedScheduler.TryNext(_timed, in st, out string command, out TimedScope scope))
            SendTimedCommand(command, scope, "timed");
    }

    private void SendTimedCommand(string command, TimedScope scope, string why)
    {
        Host.Log($"[RynthAi] Timed command ({TimedCommandSet.ScopeName(scope)}, {why}): {command}");
        // As a script's line (a nav route's chat waypoint does the same): quiet when it works.
        Reply.AsScript(() => { HandleRynthChatSubmit(command); return true; });
    }

    private void SaveTimedCommands()
    {
        if (!_timed.AnyDirty) return;
        var problems = new List<string>();
        _timed.SaveDirty(problems);
        foreach (string p in problems) ChatLine($"[RynthAi] Timed commands: couldn't save the list for {p}.");
    }

    /// <summary>Logout: the lists are saved and forgotten (the next character loads its own).</summary>
    private void OnLogoutTimedCommands()
    {
        SaveTimedCommands();
        _timed.Unload();
        _timedLoadedFor = string.Empty;
    }

    /// <summary>The window's Run now (queued from the render thread): sent on the tick, next run one interval later.</summary>
    private void RunTimedNow(TimedScope scope, int index)
    {
        string command;
        lock (_timed.Sync)
        {
            List<TimedCommand> list = _timed.List(scope);
            if (index < 0 || index >= list.Count || list[index].Command.Length == 0) return;
            command = list[index].Command;
            _timedScheduler.RanNow(list[index], Environment.TickCount64);
        }
        SendTimedCommand(command, scope, "Run now");
    }

    /// <summary>The dashboard's ⋯ > Timed commands: the window open or closed. Any thread.</summary>
    internal void ToggleTimedCommandsWindow()
    {
        if (Host.HasSetPanelOpen) Host.SetPanelOpen(TimedCommandsTitle, RynthPanelOpen.Toggle);
    }

    // ── /ra timed ────────────────────────────────────────────────────────

    internal const string TimedUsage =
        "Usage: /ra timed [server|all] list | add <seconds> <command> | remove <n> | on <n> | off <n> | run <n>; /ra timed show | hide";

    /// <summary>
    /// "/ra timed ...": the lists by chat. The list word ("server" or "all"; none = this character)
    /// comes right after timed. Every form replies.
    /// </summary>
    private void HandleTimedCommand(string[] parts)
    {
        int at = 2;
        TimedScope scope = TimedScope.Character;
        if (parts.Length > at)
        {
            string s = parts[at].ToLowerInvariant();
            if (s is "server") { scope = TimedScope.Server; at++; }
            else if (s is "all" or "global") { scope = TimedScope.All; at++; }
            else if (s is "char" or "character" or "me") at++;
        }
        string verb = parts.Length > at ? parts[at].ToLowerInvariant() : "list";
        string listName = TimedCommandSet.ScopeName(scope);

        if (verb is "show" or "open" or "hide" or "close" or "toggle" or "window")
        {
            if (!Host.HasSetPanelOpen) { ReplyFail("[RynthAi] The Timed Commands window needs RynthCore with plugin windows (plugin API 89+); /ra timed list works."); return; }
            RynthPanelOpen mode = verb is "hide" or "close" ? RynthPanelOpen.Close : verb is "toggle" or "window" ? RynthPanelOpen.Toggle : RynthPanelOpen.Open;
            int open = Host.SetPanelOpen(TimedCommandsTitle, mode);
            if (open < 0) { ReplyFail("[RynthAi] The Timed Commands window isn't there yet (RynthCore adds it a few seconds after login). Try again in a moment."); return; }
            ReplyOk(open == 1 ? "[RynthAi] Timed Commands window open." : "[RynthAi] Timed Commands window closed.");
            return;
        }
        if (!_timed.Loaded) { ReplyFail("[RynthAi] Timed commands load once you are in the world (your settings folder isn't known yet)."); return; }

        long now = Environment.TickCount64;
        bool macro = _dashboard?.Settings.IsMacroRunning == true;
        switch (verb)
        {
            case "list":
            case "ls":
            {
                var lines = new List<string>();
                lock (_timed.Sync)
                {
                    List<TimedCommand> list = _timed.List(scope);
                    for (int i = 0; i < list.Count; i++)
                    {
                        TimedCommand c = list[i];
                        lines.Add($"[RynthAi]   {i + 1}. {(c.Command.Length > 0 ? c.Command : "(empty)")} - {c.EveryText()}" +
                                  $"{(c.MacroOnly ? ", macro only" : "")}, {TimedCommandScheduler.NextText(c, now, macro)}");
                    }
                }
                if (lines.Count == 0) { ReplyOk($"[RynthAi] No timed commands for {listName}. /ra timed{ScopeWord(scope)} add <seconds> <command> adds one."); return; }
                ReplyOk($"[RynthAi] Timed commands for {listName} ({lines.Count}):");
                foreach (string l in lines) ChatLine(l);
                return;
            }
            case "add":
            {
                if (parts.Length <= at + 2 || !int.TryParse(parts[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                    || seconds < TimedCommand.MinSeconds || seconds > TimedCommand.MaxSeconds)
                {
                    ReplyFail($"[RynthAi] Usage: /ra timed{ScopeWord(scope)} add <seconds {TimedCommand.MinSeconds}-{TimedCommand.MaxSeconds}> <command> (600 = every 10 minutes)");
                    return;
                }
                string text = TimedCommand.CleanCommand(string.Join(' ', parts, at + 2, parts.Length - at - 2));
                if (text.Length == 0) { ReplyFail($"[RynthAi] Usage: /ra timed{ScopeWord(scope)} add <seconds> <command>"); return; }
                int n;
                var c = new TimedCommand { Command = text, Seconds = seconds };
                lock (_timed.Sync)
                {
                    _timed.List(scope).Add(c);
                    n = _timed.List(scope).Count;
                    _timed.MarkChanged(scope, now);
                }
                SaveTimedCommands();
                ReplyOk($"[RynthAi] Timed command {n} for {listName}: {text} - {c.EveryText()}, first in {TimedCommand.Duration(seconds)}.");
                return;
            }
            case "remove":
            case "rm":
            case "delete":
            case "on":
            case "off":
            case "run":
            {
                if (parts.Length <= at + 1 || !int.TryParse(parts[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                {
                    ReplyFail($"[RynthAi] Usage: /ra timed{ScopeWord(scope)} {verb} <n> (the number in /ra timed{ScopeWord(scope)} list)");
                    return;
                }
                string? text = null;   // null: no such command
                bool remove = verb is "remove" or "rm" or "delete";
                lock (_timed.Sync)
                {
                    List<TimedCommand> list = _timed.List(scope);
                    if (n <= list.Count)
                    {
                        TimedCommand c = list[n - 1];
                        text = c.Command;
                        if (remove) list.RemoveAt(n - 1);
                        else if (verb == "on") { c.Enabled = true; c.NextDueMs = 0; }
                        else if (verb == "off") c.Enabled = false;
                        if (verb != "run") _timed.MarkChanged(scope, now);
                    }
                }
                if (text == null)
                {
                    ReplyFail($"[RynthAi] There is no timed command {n} for {listName} (/ra timed{ScopeWord(scope)} list).");
                    return;
                }
                if (verb == "run")
                {
                    if (text.Length == 0) { ReplyFail($"[RynthAi] Timed command {n} for {listName} has no command to run."); return; }
                    RunTimedNow(scope, n - 1);
                    ReplyOk($"[RynthAi] Ran timed command {n} for {listName}: {text}");
                    return;
                }
                SaveTimedCommands();
                ReplyOk(remove
                    ? $"[RynthAi] Removed timed command {n} for {listName}: {text}"
                    : $"[RynthAi] Timed command {n} for {listName} {verb}: {text}");
                return;
            }
            default:
                ReplyFail("[RynthAi] " + TimedUsage);
                return;
        }
    }

    private static string ScopeWord(TimedScope s) => s switch { TimedScope.Server => " server", TimedScope.All => " all", _ => string.Empty };

    // ── The window (render thread) ───────────────────────────────────────

    /// <summary>
    /// RynthPluginRenderEmbed "window:timedcommands": the Timed Commands window body, docked or popped
    /// out (the engine's current context). 1 drawn, 0 not ready, -2 failed (three draws in a row threw).
    /// </summary>
    internal int RenderTimedCommands(bool popped, out Vector2 want)
    {
        want = default;
        if (!_initialized || Host.ImGuiContext == IntPtr.Zero) return 0;
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            _timedWindow ??= new TimedCommandsWindow(_timed,
                (scope, index) => QueueDashAction(() => RunTimedNow(scope, index)),
                () => _dashboard?.Settings.IsMacroRunning == true);
            want = _timedWindow.Draw();
            _timedFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderTimedCommands");
            return ++_timedFailures >= 3 ? -2 : 0;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    /// <summary>The window goes in the sections manifest on an engine with plugin windows (v89).</summary>
    private bool TimedCommandsWindowWanted => Host.HasUiTheme && Host.HasSetPanelOpen;
}

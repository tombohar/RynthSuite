using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.CommandButtons;
using RynthCore.Plugin.RynthAi.Diagnostics;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

// Command Buttons (2026-11-26, a player's ask): a window of buttons that each send one chat line, per
// character, per server and for every character (CommandButtons/CommandButtons.cs has the files). The
// lists load once the character's settings folder is known, the tick saves edits a second after the
// last one, and a press is sent on the tick through RynthCore's chat parser, as a line typed in the
// chat box is (the game's commands, /rc, every plugin's commands, emotes, @ server commands, speech).
// The window (a plugin window, API v89, no bar button) opens from the dashboard's options menu,
// /ra buttons and Find a Feature.
public sealed partial class RynthAiPlugin
{
    internal const string CommandButtonsTitle = "Command Buttons", CommandButtonsSurface = "window:commandbuttons";
    private const long ButtonsSaveAfterMs = 1000;

    private readonly CommandButtonSet _buttons = new();
    private CommandButtonsWindow? _buttonsWindow;
    private string _buttonsLoadedFor = string.Empty;   // the settings folder the lists are for
    private int _buttonsFailures;

    /// <summary>The tick (TickStep.CommandButtons): load for this character, save edits.</summary>
    private void TickCommandButtons()
    {
        if (!_loginComplete || _dashboard == null) return;
        string charFolder = _dashboard.CharFolder;
        if (string.IsNullOrEmpty(charFolder)) return;
        if (!string.Equals(charFolder, _buttonsLoadedFor, StringComparison.OrdinalIgnoreCase))
        {
            SaveCommandButtons();
            var problems = new List<string>();
            _buttons.Load(charFolder, problems);
            _buttonsLoadedFor = charFolder;
            foreach (string p in problems) ChatLine($"[RynthAi] Command buttons: couldn't read the list for {p}.");
        }
        if (_buttons.AnyDirty && Environment.TickCount64 - _buttons.ChangedAtMs >= ButtonsSaveAfterMs) SaveCommandButtons();
    }

    private void SaveCommandButtons()
    {
        if (!_buttons.AnyDirty) return;
        var problems = new List<string>();
        _buttons.SaveDirty(problems);
        foreach (string p in problems) ChatLine($"[RynthAi] Command buttons: couldn't save the list for {p}.");
    }

    /// <summary>Logout: the lists are saved and forgotten (the next character loads its own).</summary>
    private void OnLogoutCommandButtons()
    {
        SaveCommandButtons();
        _buttons.Unload();
        _buttonsLoadedFor = string.Empty;
    }

    /// <summary>
    /// Sends button <paramref name="index"/> of <paramref name="scope"/>'s list. On the tick only (the window
    /// and /ra buttons press queue it): RynthCore runs the line as typed, so a /ra line comes back to RynthAi,
    /// which must not be inside the chat parser's plugin step then. False (and why, in the window's note and
    /// in chat) when it can't be sent.
    /// </summary>
    private bool PressCommandButton(ButtonScope scope, int index)
    {
        string label, line;
        lock (_buttons.Sync)
        {
            List<CommandButton> list = _buttons.List(scope);
            if (index < 0 || index >= list.Count) return false;
            label = list[index].Shown;
            line = list[index].Command;
        }
        CommandCheck check = CommandLine.Check(line);
        string? problem = check.Kind == CommandKind.Empty ? "it has no command yet"
            : check.Problem != null ? check.Problem.TrimEnd('.').ToLowerInvariant()
            : !Host.HasInvokeChatParser ? "this RynthCore can't send chat lines for a plugin"
            : null;
        if (problem != null)
        {
            _buttonsWindow?.Note($"{label}: not sent ({problem}).");
            ReplyFail($"[RynthAi] Command button '{label}' wasn't sent: {problem}.");
            return false;
        }
        // The parser's answer is the game's (speech and emotes can say 0 and still go): the log keeps it, the
        // line's own effect (a command's reply, the emote, the server's answer) is what the player sees.
        bool taken = Host.InvokeChatParser(check.Line);
        Host.Log($"[RynthAi] Command button ({CommandButtonSet.ScopeName(scope)}) '{label}': {check.Line}{(taken ? "" : " (the chat parser answered 0)")}");
        _buttonsWindow?.Note("Sent: " + check.Line);
        return true;
    }

    /// <summary>The dashboard's ⋯ > Command buttons: the window open or closed. Any thread.</summary>
    internal void ToggleCommandButtonsWindow()
    {
        if (Host.HasSetPanelOpen) Host.SetPanelOpen(CommandButtonsTitle, RynthPanelOpen.Toggle);
    }

    // ── /ra buttons ──────────────────────────────────────────────────────

    internal const string ButtonsUsage =
        "Usage: /ra buttons [server|all] list | add <label> = <command> | remove <n> | press <n or label>; /ra buttons [show|hide|edit]";

    /// <summary>
    /// "/ra buttons ...": the window and the lists by chat. The list word ("server" or "all"; none = this
    /// character) comes right after buttons. Every form replies.
    /// </summary>
    private void HandleButtonsCommand(string[] parts)
    {
        int at = 2;
        ButtonScope scope = ButtonScope.Character;
        if (parts.Length > at)
        {
            string s = parts[at].ToLowerInvariant();
            if (s is "server") { scope = ButtonScope.Server; at++; }
            else if (s is "all" or "global") { scope = ButtonScope.All; at++; }
            else if (s is "char" or "character" or "me") at++;
        }
        string verb = parts.Length > at ? parts[at].ToLowerInvariant() : "show";
        string listName = CommandButtonSet.ScopeName(scope);

        if (verb is "show" or "open" or "hide" or "close" or "toggle" or "window" or "edit")
        {
            if (!Host.HasSetPanelOpen) { ReplyFail("[RynthAi] The Command Buttons window needs RynthCore with plugin windows (plugin API 89+); /ra buttons list and press work."); return; }
            RynthPanelOpen mode = verb is "hide" or "close" ? RynthPanelOpen.Close : verb is "toggle" or "window" ? RynthPanelOpen.Toggle : RynthPanelOpen.Open;
            int open = Host.SetPanelOpen(CommandButtonsTitle, mode);
            if (open < 0) { ReplyFail("[RynthAi] The Command Buttons window isn't there yet (RynthCore adds it a few seconds after login). Try again in a moment."); return; }
            if (verb == "edit" && open == 1)
            {
                _buttonsWindow ??= NewButtonsWindow();
                _buttonsWindow.OpenEdit(scope);
                ReplyOk($"[RynthAi] Command Buttons window open, editing the buttons for {listName}.");
                return;
            }
            ReplyOk(open == 1 ? "[RynthAi] Command Buttons window open." : "[RynthAi] Command Buttons window closed.");
            return;
        }
        if (!_buttons.Loaded) { ReplyFail("[RynthAi] Command buttons load once you are in the world (your settings folder isn't known yet)."); return; }

        long now = Environment.TickCount64;
        string rest = parts.Length > at + 1 ? string.Join(' ', parts, at + 1, parts.Length - at - 1) : string.Empty;
        switch (verb)
        {
            case "list":
            case "ls":
            {
                var lines = new List<string>();
                lock (_buttons.Sync)
                {
                    List<CommandButton> list = _buttons.List(scope);
                    for (int i = 0; i < list.Count; i++)
                    {
                        CommandButton b = list[i];
                        string label = b.Label.Length > 0 ? b.Label : "(no label)";
                        lines.Add($"[RynthAi]   {i + 1}. {label}: {(b.Command.Length > 0 ? b.Command : "(no command)")}{(b.Confirm ? " (asks first)" : "")}");
                    }
                }
                if (lines.Count == 0) { ReplyOk($"[RynthAi] No command buttons for {listName}. /ra buttons{ScopeWord(scope)} add <label> = <command> adds one."); return; }
                ReplyOk($"[RynthAi] Command buttons for {listName} ({lines.Count}):");
                foreach (string l in lines) ChatLine(l);
                return;
            }
            case "add":
            {
                int eq = rest.IndexOf('=');
                string label = eq >= 0 ? rest[..eq] : string.Empty;
                string command = eq >= 0 ? rest[(eq + 1)..] : rest;
                CommandCheck check = CommandLine.Check(command);
                if (check.Kind == CommandKind.Empty) { ReplyFail($"[RynthAi] Usage: /ra buttons{ScopeWord(scope)} add <label> = <command> (or just add <command>)"); return; }
                if (check.Problem != null) { ReplyFail($"[RynthAi] Not added: {check.Problem}"); return; }
                CommandButton? b;
                int n;
                lock (_buttons.Sync)
                {
                    b = _buttons.Add(scope, label, check.Line, now);
                    n = _buttons.List(scope).Count;
                }
                if (b == null) { ReplyFail($"[RynthAi] The command buttons for {listName} are full ({CommandButtonSet.MaxPerList}): remove one first."); return; }
                SaveCommandButtons();
                ReplyOk($"[RynthAi] Command button {n} for {listName}: {b.Shown} sends {b.Command}");
                return;
            }
            case "remove":
            case "rm":
            case "delete":
            case "press":
            case "run":
            {
                int index = -1;
                string shown = string.Empty;
                lock (_buttons.Sync)
                {
                    List<CommandButton> list = _buttons.List(scope);
                    if (int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                        index = n >= 1 && n <= list.Count ? n - 1 : -1;
                    else if (rest.Length > 0)
                        index = list.FindIndex(b => b.Label.Equals(rest.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (index >= 0) shown = list[index].Shown;
                }
                if (rest.Length == 0) { ReplyFail($"[RynthAi] Usage: /ra buttons{ScopeWord(scope)} {verb} <n or label> (the number in /ra buttons{ScopeWord(scope)} list)"); return; }
                if (index < 0) { ReplyFail($"[RynthAi] There is no command button '{rest}' for {listName} (/ra buttons{ScopeWord(scope)} list)."); return; }
                if (verb is "press" or "run")
                {
                    // On the tick: this line is being handled inside the chat parser's plugin step.
                    ButtonScope sc = scope;
                    int ix = index;
                    QueueDashAction(() => PressCommandButton(sc, ix));
                    ReplyOk($"[RynthAi] Pressing command button {index + 1} for {listName}: {shown}");
                    return;
                }
                lock (_buttons.Sync)
                {
                    _buttons.List(scope).RemoveAt(index);
                    _buttons.MarkChanged(scope, now);
                }
                SaveCommandButtons();
                ReplyOk($"[RynthAi] Removed command button {index + 1} for {listName}: {shown}");
                return;
            }
            default:
                ReplyFail("[RynthAi] " + ButtonsUsage);
                return;
        }
    }

    private static string ScopeWord(ButtonScope s) => s switch { ButtonScope.Server => " server", ButtonScope.All => " all", _ => string.Empty };

    // ── The window (render thread) ───────────────────────────────────────

    private CommandButtonsWindow NewButtonsWindow() =>
        new(_buttons, (scope, index) => QueueDashAction(() => PressCommandButton(scope, index)));

    /// <summary>
    /// RynthPluginRenderEmbed "window:commandbuttons": the Command Buttons window body, docked or popped out
    /// (the engine's current context). 1 drawn, 0 not ready, -2 failed (three draws in a row threw).
    /// </summary>
    internal int RenderCommandButtons(bool popped, out Vector2 want)
    {
        want = default;
        if (!_initialized || Host.ImGuiContext == IntPtr.Zero) return 0;
        IntPtr previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            _buttonsWindow ??= NewButtonsWindow();
            want = _buttonsWindow.Draw();
            _buttonsFailures = 0;
            return 1;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderCommandButtons");
            return ++_buttonsFailures >= 3 ? -2 : 0;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    /// <summary>The window goes in the sections manifest on an engine with plugin windows (v89).</summary>
    private bool CommandButtonsWindowWanted => Host.HasUiTheme && Host.HasSetPanelOpen;
}

// ============================================================================
//  RynthAi - TimedCommands/TimedCommandsWindow.cs
//  The Timed Commands window (2026-11-03): the three lists (this character,
//  this server, all characters) as tabs, one row per command: on/off, the
//  command, every how many seconds, the first run after login (seconds), only
//  while the macro runs, when it runs next, Run now and Remove; Add command
//  under them. RynthCore hosts the window (the sections manifest's
//  "timedcommands" window, plugin API v89: header, pop-out, resize, remembered
//  place; no bar button, the dashboard's options menu and /ra timed show open
//  it) and calls Draw through RynthPluginRenderEmbed.
//
//  AC's render thread. Edits go straight into the lists under the set's lock
//  and mark them changed (the tick saves them a moment later); Run now is
//  queued for the tick. Looks: Shared/RynthUi (the player's theme).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.TimedCommands;

internal sealed class TimedCommandsWindow
{
    private readonly TimedCommandSet _set;
    private readonly Action<TimedScope, int> _runNow;     // queued for the tick
    private readonly Func<bool> _macroRunning;
    private TimedScope _scope = TimedScope.Character;
    private int _focusRow = -1;                            // a row just added: focus its command box

    private float _k = 1f;
    private float Px(float v) => v * _k;

    private const string HelpText =
        "Chat commands RynthAi sends every so many seconds, like MagTools' periodic commands.\n\n" +
        "Three lists: This character (only this character), This server (every character on this server) " +
        "and All characters. A character runs all three.\n\n" +
        "Every: how many seconds between runs (1 to 86400, a day; 600 = 10 minutes). First after: seconds " +
        "after login before the first run (0 = one interval). Macro only: it waits while the macro is stopped.\n\n" +
        "A command can be anything you'd type: /ra, /mt, /ub and /rc commands, emotes, channels. Commands " +
        "are sent at least a second apart, never in portal space, and a command that came due while it " +
        "couldn't run goes once when it can.\n\n" +
        "/ra timed list | add <seconds> <command> | remove <n> | on|off <n> | run <n> (add \"server\" or " +
        "\"all\" after timed for the other lists); /ra timed show opens this window, and so does " +
        "Timed commands in the RynthAi window's options menu.";

    public TimedCommandsWindow(TimedCommandSet set, Action<TimedScope, int> runNow, Func<bool> macroRunning)
    {
        _set = set;
        _runNow = runNow;
        _macroRunning = macroRunning;
    }

    /// <summary>Draws the window body into the current ImGui window and returns the size it would like. Render thread.</summary>
    public Vector2 Draw()
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui11).FontSize / 11f);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        var want = new Vector2(Px(640), Px(300));
        if (size.X < 60 || size.Y < 40) return want;
        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        using (PushFont(RynthUiFont.Ui11))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
            ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
            ImGui.Indent(6);
            float w = size.X - 12;
            lock (_set.Sync)
            {
                DrawTabs(w);
                if (!_set.Loaded)
                {
                    Label("Timed commands load once you are in the world.", Mute);
                }
                else
                {
                    DrawHeader(w);
                    float bodyH = Math.Max(Px(30), origin.Y + size.Y - 6 - ImGui.GetCursorScreenPos().Y - Px(34));
                    ImGui.PushStyleColor(ImGuiCol.ChildBg, Field);
                    ImGui.BeginChild("##tc_rows", new Vector2(w, bodyH));
                    DrawRows();
                    ImGui.EndChild();
                    ImGui.PopStyleColor();
                    DrawAdd(w);
                }
            }
            ImGui.Unindent(6);
            ImGui.PopStyleVar();
        }
        frame.AddRect(origin, origin + size, Border, 4);
        return want;
    }

    // ── Tabs: the three lists, and help ─────────────────────────────────

    private void DrawTabs(float w)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(22), x = p.X, iconW = Px(22);
        for (int s = 0; s < 3; s++)
        {
            var scope = (TimedScope)s;
            string who = scope switch
            {
                TimedScope.Character => _set.Character.Length > 0 ? _set.Character : "This character",
                TimedScope.Server => _set.Server.Length > 0 ? _set.Server : "This server",
                _ => "All characters",
            };
            int n = _set.List(scope).Count;
            string label = n > 0 ? $"{who} ({n})" : who;
            float bw = ButtonWidth(label) + Px(8);
            bool on = _scope == scope;
            if (Button("##tc_tab" + s.ToString(CultureInfo.InvariantCulture), label, new Vector2(x, p.Y), new Vector2(bw, h),
                    on ? Text : Mute, on ? Selected : ButtonFill, border: on ? Accent : 0))
                _scope = scope;
            ImGui.SetItemTooltip(scope switch
            {
                TimedScope.Character => "Commands only this character runs.",
                TimedScope.Server => "Commands every character on this server runs.",
                _ => "Commands every character runs, on every server.",
            });
            x += bw + Px(4);
        }
        IconButton("##tc_help", RynthIcons.Info, new Vector2(p.X + w - iconW, p.Y), new Vector2(iconW, h), Mute, ButtonFill, font: RynthUiFont.Ui11);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
            ImGui.TextUnformatted(HelpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(6)));
        ImGui.Dummy(Vector2.Zero);
    }

    // ── Columns ─────────────────────────────────────────────────────────

    private float OnW => Px(26);
    private float EveryW => Px(70);
    private float FirstW => Px(84);
    private float MacroW => Px(48);
    private float NextW => Px(96);
    private float BtnW => Px(24);

    /// <summary>The command column's width in a row <paramref name="w"/> wide.</summary>
    private float CommandW(float w) => Math.Max(Px(80), w - OnW - EveryW - FirstW - MacroW - NextW - 2 * BtnW - Px(36));

    private void DrawHeader(float w)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(16), x = p.X + Px(4);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        void Head(string text, float width, string tip)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, p.Y));
            ImGui.InvisibleButton("##tch_" + text, new Vector2(Math.Max(1, width), h));
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(tip);
            dl.AddText(new Vector2(x, p.Y + (h - ImGui.GetFontSize()) * 0.5f), Mute, text);
            x += width + Px(4);
        }
        Head("On", OnW, "Runs while ticked.");
        Head("Command", CommandW(w), "What is sent, as you would type it.");
        Head("Every (s)", EveryW, "Seconds between runs (1 to 86400 = a day; 600 = 10 minutes).");
        Head("First after (s)", FirstW, "Seconds after login before the first run (0 = one interval, up to 86400).");
        Head("Macro", MacroW, "Only while the macro runs.");
        Head("Next", NextW, "When it runs next.");
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(2)));
        ImGui.Dummy(Vector2.Zero);
    }

    // ── Rows (under the set's lock) ─────────────────────────────────────

    private void DrawRows()
    {
        List<TimedCommand> list = _set.List(_scope);
        float w = ImGui.GetContentRegionAvail().X;
        if (list.Count == 0)
        {
            ImGui.SetCursorPos(new Vector2(Px(8), Px(8)));
            ImGui.PushTextWrapPos(w - Px(8));
            Label(_scope switch
            {
                TimedScope.Character => "No timed commands for this character. Add command puts one here.",
                TimedScope.Server => "No timed commands for every character on this server. Add command puts one here.",
                _ => "No timed commands for every character. Add command puts one here.",
            }, Mute);
            ImGui.PopTextWrapPos();
            return;
        }
        long now = Environment.TickCount64;
        bool macro = _macroRunning();
        float rowH = Px(24);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        int remove = -1;
        for (int i = 0; i < list.Count; i++)
        {
            TimedCommand c = list[i];
            ImGui.PushID(i);
            Vector2 p = ImGui.GetCursorScreenPos();
            if (i % 2 == 1) dl.AddRectFilled(p, p + new Vector2(w, rowH), RowAlt);
            float x = p.X + Px(4), y = p.Y + Px(2);
            float fieldH = rowH - Px(4);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(4), Math.Max(1, (fieldH - ImGui.GetFontSize()) * 0.5f)));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Background);

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            bool en = c.Enabled;
            if (ImGui.Checkbox("##on", ref en)) { c.Enabled = en; c.NextDueMs = 0; Changed(); }
            x += OnW + Px(4);

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGui.SetNextItemWidth(CommandW(w));
            if (_focusRow == i) { ImGui.SetKeyboardFocusHere(); _focusRow = -1; }
            string cmd = c.Command;
            if (ImGui.InputTextWithHint("##cmd", "/ra ... or anything you'd type", ref cmd, (uint)TimedCommand.MaxLength))
            {
                c.Command = TimedCommand.CleanCommand(cmd);
                Changed();
            }
            x += CommandW(w) + Px(4);

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGui.SetNextItemWidth(EveryW);
            int every = c.Seconds;
            if (ImGui.InputInt("##every", ref every, 0, 0))
            {
                c.Seconds = every;   // clamped when the box is left, so typing "600" passes through "6" (the scheduler clamps too)
                Changed();
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) { c.Seconds = TimedCommand.ClampSeconds(c.Seconds); c.NextDueMs = 0; Changed(); }
            ImGui.SetItemTooltip(c.EveryText() + " (seconds, 1 to 86400)");
            x += EveryW + Px(4);

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGui.SetNextItemWidth(FirstW);
            int first = c.FirstAfter;
            if (ImGui.InputInt("##first", ref first, 0, 0))
            {
                c.FirstAfter = first;
                Changed();
            }
            if (ImGui.IsItemDeactivatedAfterEdit()) { c.FirstAfter = TimedCommand.ClampFirstAfter(c.FirstAfter); Changed(); }
            ImGui.SetItemTooltip(c.FirstAfter > 0
                ? "First run " + TimedCommand.Duration(c.FirstAfter) + " after login (seconds, 0 to 86400)"
                : "First run one interval after login (0; seconds, up to 86400)");
            x += FirstW + Px(4);

            ImGui.SetCursorScreenPos(new Vector2(x + (MacroW - ImGui.GetFrameHeight()) * 0.5f, y));
            bool mo = c.MacroOnly;
            if (ImGui.Checkbox("##macro", ref mo)) { c.MacroOnly = mo; Changed(); }
            x += MacroW + Px(4);

            ImGui.PopStyleColor();
            ImGui.PopStyleVar();

            string next = TimedCommandScheduler.NextText(c, now, macro);
            dl.AddText(new Vector2(x, p.Y + (rowH - ImGui.GetFontSize()) * 0.5f), c.Enabled ? Text : Mute, next);
            x += NextW + Px(4);

            if (IconButton("##run", RynthIcons.Play, new Vector2(x, y), new Vector2(BtnW, fieldH), Text, ButtonFill,
                    enabled: c.Command.Length > 0, font: RynthUiFont.Ui11))
                _runNow(_scope, i);
            ImGui.SetItemTooltip("Run now (the next run is one interval from now).");
            x += BtnW + Px(4);
            if (IconButton("##del", RynthIcons.Trash, new Vector2(x, y), new Vector2(BtnW, fieldH), Red, ButtonFill, font: RynthUiFont.Ui11))
                remove = i;
            ImGui.SetItemTooltip("Remove this command.");

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + rowH));
            ImGui.Dummy(Vector2.Zero);
            ImGui.PopID();
        }
        if (remove >= 0)
        {
            list.RemoveAt(remove);
            Changed();
        }
    }

    private void DrawAdd(float w)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(22);
        string label = "Add command";
        if (Button("##tc_add", label, new Vector2(p.X, p.Y + Px(2)), new Vector2(ButtonWidth(label) + Px(8), h), Text, ButtonFill))
        {
            List<TimedCommand> list = _set.List(_scope);
            list.Add(new TimedCommand());
            _focusRow = list.Count - 1;
            Changed();
        }
        ImGui.SetItemTooltip("A new command in this list: " + TimedCommand.Every(TimedCommand.DefaultSeconds) + ", on.");
        string scopeNote = "Saved for " + TimedCommandSet.ScopeName(_scope) + ".";
        ImGui.GetWindowDrawList().AddText(new Vector2(p.X + ButtonWidth(label) + Px(18), p.Y + Px(2) + (h - ImGui.GetFontSize()) * 0.5f), Mute, scopeNote);
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(4)));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>An edit to the shown list (under the set's lock): saved by the tick a moment later.</summary>
    private void Changed() => _set.MarkChanged(_scope, Environment.TickCount64);
}

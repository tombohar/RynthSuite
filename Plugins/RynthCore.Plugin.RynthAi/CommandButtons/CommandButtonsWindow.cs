// ============================================================================
//  RynthAi - CommandButtons/CommandButtonsWindow.cs
//  The Command Buttons window (2026-11-26): a grid of buttons, each sending one
//  chat line, and an edit view to make them. RynthCore hosts the window (the
//  sections manifest's "commandbuttons" window, plugin API v89: header,
//  pop-out, resize, remembered place; no bar button: the dashboard's options
//  menu, /ra buttons and Find a Feature open it) and calls Draw through
//  RynthPluginRenderEmbed.
//
//  Grid: this character's buttons, then this server's, then every character's,
//  as many columns as set (Auto: as many as fit). A click sends the line (asks
//  first for a button with "Ask" ticked); a right-click opens the edit view on
//  that button's list. The pencil in the header opens the edit view.
//
//  Edit view: the three lists as tabs; per button: move up / down, label,
//  command (with a mark saying what it is, or why it can't be sent), colour,
//  Ask, Send now and Remove; Add button; Copy from another character (this
//  character's tab); the grid's columns and button size.
//
//  AC's render thread. Edits go straight into the lists under the set's lock and
//  mark them changed (the tick saves them a moment later); presses are queued
//  for the tick. Looks: Shared/RynthUi (the player's theme).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.CommandButtons;

internal sealed class CommandButtonsWindow
{
    private readonly CommandButtonSet _set;
    private readonly Action<ButtonScope, int> _press;   // queued for the tick
    private bool _edit;
    private ButtonScope _scope = ButtonScope.Character;
    private int _focusRow = -1;                           // a row just added: focus its label box

    // A button that asks first: which, and the line it will send.
    private ButtonScope _confirmScope;
    private int _confirmIndex = -1;
    private string _confirmLine = string.Empty;
    private bool _openConfirm;

    // Copy from another character (read when the edit view opens and when the list is opened).
    private List<string> _others = new();
    private int _copyPick;

    // The header's note: the last press, or a problem (set from the tick).
    private volatile string _note = string.Empty;
    private long _noteAtMs;
    private const long NoteMs = 5000;

    private float _k = 1f;
    private float Px(float v) => v * _k;
    private const string ConfirmId = "##cb_confirm";

    private const string HelpText =
        "Buttons that each send one chat line, as if you typed it in the chat box: a command (/ra start, " +
        "/lua start threads, /rdef weapons, /loc), an emote (*wave*, *snow angel*), a server command (@home) " +
        "or plain text (said aloud).\n\n" +
        "The pencil opens the edit view: a label and a command for each button, its colour, and Ask (it asks " +
        "before it sends: for commands that are hard to take back). Columns and the button size are there too.\n\n" +
        "Three lists: This character, This server (every character on this server) and All characters; the " +
        "grid shows all three. Copy from takes another character's buttons.\n\n" +
        "/ra buttons opens this window; /ra buttons list | add <label> = <command> | remove <n> | press <n> " +
        "(add \"server\" or \"all\" after buttons for the other lists). Command buttons is also in the " +
        "RynthAi window's options menu.";

    public CommandButtonsWindow(CommandButtonSet set, Action<ButtonScope, int> press)
    {
        _set = set;
        _press = press;
    }

    /// <summary>The header's note for a few seconds ("Sent: Start"). Any thread.</summary>
    public void Note(string text)
    {
        _note = text;
        Volatile.Write(ref _noteAtMs, Environment.TickCount64);
    }

    /// <summary>Opens the edit view on <paramref name="scope"/>'s list (the /ra buttons edit command, a right-click).</summary>
    public void OpenEdit(ButtonScope scope)
    {
        _edit = true;
        _scope = scope;
        _others = _set.OtherCharacters();
    }

    /// <summary>Draws the window body into the current ImGui window and returns the size it would like. Render thread.</summary>
    public Vector2 Draw()
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui11).FontSize / 11f);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        Vector2 want = new(Px(_edit ? 680 : 360), Px(_edit ? 360 : 160));
        if (size.X < 60 || size.Y < 40) return want;
        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        using (PushFont(RynthUiFont.Ui11))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
            ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
            ImGui.Indent(6);
            float w = size.X - 12;
            float bottom = origin.Y + size.Y - 6;
            lock (_set.Sync)
            {
                if (!_set.Loaded)
                    Label("Command buttons load once you are in the world.", Mute);
                else if (_edit)
                    DrawEdit(w, bottom);
                else
                    want = DrawGrid(w, bottom, want);
            }
            DrawConfirm();
            ImGui.Unindent(6);
            ImGui.PopStyleVar();
        }
        frame.AddRect(origin, origin + size, Border, 4);
        return want;
    }

    // ── The grid ────────────────────────────────────────────────────────

    private Vector2 DrawGrid(float w, float bottom, Vector2 want)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(20), icon = Px(22);
        // Header: the note on the left, edit and help on the right.
        string note = _note;
        if (note.Length > 0 && Environment.TickCount64 - Volatile.Read(ref _noteAtMs) < NoteMs)
        {
            ImDrawListPtr hdl = ImGui.GetWindowDrawList();
            hdl.PushClipRect(p, new Vector2(p.X + w - 2 * icon - Px(8), p.Y + h), true);
            hdl.AddText(new Vector2(p.X, p.Y + (h - ImGui.GetFontSize()) * 0.5f), Mute, note);
            hdl.PopClipRect();
        }
        if (IconButton("##cb_edit", RynthIcons.PencilSimple, new Vector2(p.X + w - 2 * icon - Px(4), p.Y), new Vector2(icon, h), Text, ButtonFill, font: RynthUiFont.Ui11))
            OpenEdit(ButtonScope.Character);
        ImGui.SetItemTooltip("Edit the buttons: add, remove, move, label, command, colour; columns and size.");
        HelpIcon(new Vector2(p.X + w - icon, p.Y), new Vector2(icon, h));
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(4)));
        ImGui.Dummy(Vector2.Zero);

        int total = 0;
        for (int s = 0; s < 3; s++) total += _set.List((ButtonScope)s).Count;
        if (total == 0)
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + w);
            Label("No buttons yet. Each button sends one chat line: /ra start, *wave*, @home, /lua start <script>...", Mute);
            ImGui.PopTextWrapPos();
            Vector2 a = ImGui.GetCursorScreenPos();
            string label = "Add a button";
            if (Button("##cb_first", label, a, new Vector2(ButtonWidth(label) + Px(8), Px(22)), Text, ButtonFill, border: Accent))
            {
                OpenEdit(ButtonScope.Character);
                AddRow();
            }
            ImGui.SetCursorScreenPos(new Vector2(a.X, a.Y + Px(26)));
            ImGui.Dummy(Vector2.Zero);
            return want;
        }

        ButtonLayout layout = _set.Layout;
        float bw = Px(layout.Width), bh = Px(layout.Height), gap = Px(4);
        int cols = ButtonLayout.ColumnsFor(layout.Columns, w, bw, gap);
        int rows = (total + cols - 1) / cols;
        float gridW = cols * bw + (cols - 1) * gap, gridH = rows * bh + (rows - 1) * gap;
        float top = ImGui.GetCursorScreenPos().Y;
        float bodyH = Math.Max(bh, bottom - top);
        ImGui.BeginChild("##cb_grid", new Vector2(w, bodyH), ImGuiChildFlags.None, ImGuiWindowFlags.NoBackground);
        Vector2 g = ImGui.GetCursorScreenPos();
        int n = 0;
        for (int s = 0; s < 3; s++)
        {
            var scope = (ButtonScope)s;
            List<CommandButton> list = _set.List(scope);
            for (int i = 0; i < list.Count; i++, n++)
            {
                CommandButton b = list[i];
                var at = new Vector2(g.X + n % cols * (bw + gap), g.Y + n / cols * (bh + gap));
                uint fill = b.Color != 0 ? b.Color : ButtonFill;
                uint fg = b.Color != 0 ? CommandButton.TextOn(b.Color) : Text;
                bool can = b.Command.Length > 0;
                string id = "##cb_b" + s.ToString(CultureInfo.InvariantCulture) + "_" + i.ToString(CultureInfo.InvariantCulture);
                if (Button(id, b.Shown, at, new Vector2(bw, bh), fg, fill, enabled: can))
                    Press(scope, i, b);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    OpenEdit(scope);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    SafeImGui.SetTooltip(Tooltip(b, scope));
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(g.X, g.Y + gridH));
        ImGui.Dummy(new Vector2(gridW, 0));
        ImGui.EndChild();
        // The size it would like: the grid with fixed columns; the window's width when they fit to it.
        float wantW = layout.Columns > 0 ? gridW + 12 : w + 12;
        return new Vector2(Math.Max(Px(160), wantW), h + Px(4) + gridH + 16);
    }

    private static string Tooltip(CommandButton b, ButtonScope scope)
    {
        string t = b.Command.Length > 0 ? b.Command : "No command yet (right-click to edit).";
        if (b.Label.Length > 0 && b.Command.Length > 0) t = b.Label + "\n" + t;
        if (b.Confirm) t += "\nAsks before it sends.";
        return t + "\n(" + CommandButtonSet.ScopeName(scope) + "; right-click to edit)";
    }

    private void Press(ButtonScope scope, int index, CommandButton b)
    {
        if (!b.Confirm) { _press(scope, index); return; }
        _confirmScope = scope;
        _confirmIndex = index;
        _confirmLine = b.Command;
        _openConfirm = true;
    }

    private void DrawConfirm()
    {
        if (_openConfirm) { ImGui.OpenPopup(ConfirmId); _openConfirm = false; }
        if (!ImGui.BeginPopup(ConfirmId)) return;
        SafeImGui.Text("Send " + _confirmLine + " ?");
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(22), bw = Px(70);
        if (Button("##cb_yes", "Send", p, new Vector2(bw, h), Text, ButtonFill, border: Accent))
        {
            if (_confirmIndex >= 0) _press(_confirmScope, _confirmIndex);
            _confirmIndex = -1;
            ImGui.CloseCurrentPopup();
        }
        if (Button("##cb_no", "Cancel", new Vector2(p.X + bw + Px(6), p.Y), new Vector2(bw, h), Text, ButtonFill))
        {
            _confirmIndex = -1;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(4)));
        ImGui.Dummy(new Vector2(2 * bw + Px(6), 0));
        ImGui.EndPopup();
    }

    private void HelpIcon(Vector2 at, Vector2 size)
    {
        IconButton("##cb_help", RynthIcons.Info, at, size, Mute, ButtonFill, font: RynthUiFont.Ui11);
        if (!ImGui.IsItemHovered()) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
        ImGui.TextUnformatted(HelpText);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // ── The edit view (under the set's lock) ────────────────────────────

    private void DrawEdit(float w, float bottom)
    {
        DrawTabs(w);
        DrawLayoutRow(w);
        DrawHeader(w);
        float bodyH = Math.Max(Px(30), bottom - ImGui.GetCursorScreenPos().Y - Px(30));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Field);
        ImGui.BeginChild("##cb_rows", new Vector2(w, bodyH));
        DrawRows();
        ImGui.EndChild();
        ImGui.PopStyleColor();
        DrawAddRow(w);
    }

    private void DrawTabs(float w)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(22), x = p.X, icon = Px(22);
        for (int s = 0; s < 3; s++)
        {
            var scope = (ButtonScope)s;
            string who = scope switch
            {
                ButtonScope.Character => _set.Character.Length > 0 ? _set.Character : "This character",
                ButtonScope.Server => _set.Server.Length > 0 ? _set.Server : "This server",
                _ => "All characters",
            };
            int n = _set.List(scope).Count;
            string label = n > 0 ? $"{who} ({n})" : who;
            float bw = ButtonWidth(label) + Px(8);
            bool on = _scope == scope;
            if (Button("##cb_tab" + s.ToString(CultureInfo.InvariantCulture), label, new Vector2(x, p.Y), new Vector2(bw, h),
                    on ? Text : Mute, on ? Selected : ButtonFill, border: on ? Accent : 0))
            {
                _scope = scope;
                _focusRow = -1;
            }
            ImGui.SetItemTooltip(scope switch
            {
                ButtonScope.Character => "Buttons only this character has.",
                ButtonScope.Server => "Buttons every character on this server has.",
                _ => "Buttons every character has, on every server.",
            });
            x += bw + Px(4);
        }
        string done = "Done";
        float dw = ButtonWidth(done) + Px(8);
        if (Button("##cb_done", done, new Vector2(p.X + w - icon - Px(4) - dw, p.Y), new Vector2(dw, h), Text, ButtonFill, border: Accent))
            _edit = false;
        ImGui.SetItemTooltip("Back to the buttons (edits are saved as you make them).");
        HelpIcon(new Vector2(p.X + w - icon, p.Y), new Vector2(icon, h));
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(6)));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>Columns [-] Auto [+], Width and Height sliders: this character's layout.</summary>
    private void DrawLayoutRow(float w)
    {
        ButtonLayout l = _set.Layout;
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(20), x = p.X;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float ty = p.Y + (h - ImGui.GetFontSize()) * 0.5f;

        dl.AddText(new Vector2(x, ty), Mute, "Columns");
        x += ImGui.CalcTextSize("Columns").X + Px(6);
        if (IconButton("##cb_colless", RynthIcons.Minus, new Vector2(x, p.Y), new Vector2(Px(20), h), Text, ButtonFill,
                enabled: l.Columns > ButtonLayout.ColumnsMin, font: RynthUiFont.Ui11))
            SetLayout(() => l.Columns--);
        ImGui.SetItemTooltip("Fewer columns (Auto: as many as fit the window).");
        x += Px(22);
        string cols = ButtonLayout.ColumnsText(l.Columns);
        float cw = Px(36);
        dl.AddText(new Vector2(x + (cw - ImGui.CalcTextSize(cols).X) * 0.5f, ty), Text, cols);
        x += cw;
        if (IconButton("##cb_colmore", RynthIcons.Plus, new Vector2(x, p.Y), new Vector2(Px(20), h), Text, ButtonFill,
                enabled: l.Columns < ButtonLayout.ColumnsMax, font: RynthUiFont.Ui11))
            SetLayout(() => l.Columns++);
        ImGui.SetItemTooltip("More columns (up to " + ButtonLayout.ColumnsMax.ToString(CultureInfo.InvariantCulture) + ").");
        x += Px(32);

        ImGui.PushStyleColor(ImGuiCol.FrameBg, Field);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(4), Math.Max(1, (h - ImGui.GetFontSize()) * 0.5f)));
        float slider = Math.Clamp((w - (x - p.X)) * 0.5f - Px(50), Px(70), Px(150));
        x = Slider(dl, x, p.Y, ty, slider, "Width", "##cb_w", l.Width, ButtonLayout.WidthMin, ButtonLayout.WidthMax, v => l.Width = v,
            "Each button's width.");
        Slider(dl, x, p.Y, ty, slider, "Height", "##cb_h", l.Height, ButtonLayout.HeightMin, ButtonLayout.HeightMax, v => l.Height = v,
            "Each button's height.");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(6)));
        ImGui.Dummy(Vector2.Zero);
    }

    private float Slider(ImDrawListPtr dl, float x, float y, float ty, float width, string label, string id, int value, int min, int max,
        Action<int> set, string tip)
    {
        dl.AddText(new Vector2(x, ty), Mute, label);
        x += ImGui.CalcTextSize(label).X + Px(6);
        ImGui.SetCursorScreenPos(new Vector2(x, y));
        ImGui.SetNextItemWidth(width);
        int v = value;
        if (ImGui.SliderInt(id, ref v, min, max)) SetLayout(() => set(Math.Clamp(v, min, max)));
        ImGui.SetItemTooltip(tip);
        return x + width + Px(14);
    }

    private void SetLayout(Action change)
    {
        change();
        _set.Layout.Clamp();
        _set.MarkChanged(ButtonScope.Character, Environment.TickCount64);
    }

    // ── Columns of the edit rows ────────────────────────────────────────

    private float MoveW => Px(44);
    private float LabelW => Px(110);
    private float MarkW => Px(18);
    private float ColorW => Px(44);
    private float AskW => Px(30);
    private float BtnW => Px(24);

    private float CommandW(float w) => Math.Max(Px(100), w - MoveW - LabelW - MarkW - ColorW - AskW - 2 * BtnW - Px(40));

    private void DrawHeader(float w)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(16), x = p.X + Px(4);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        void Head(string text, float width, string tip)
        {
            if (text.Length > 0)
            {
                ImGui.SetCursorScreenPos(new Vector2(x, p.Y));
                ImGui.InvisibleButton("##cbh_" + text, new Vector2(Math.Max(1, width), h));
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(tip);
                dl.AddText(new Vector2(x, p.Y + (h - ImGui.GetFontSize()) * 0.5f), Mute, text);
            }
            x += width + Px(4);
        }
        Head("Order", MoveW, "Move a button earlier or later in the grid.");
        Head("Label", LabelW, "What the button says (empty: it shows its command).");
        Head("Command", CommandW(w) + MarkW + Px(4), "The one chat line it sends, as you would type it.");
        Head("Colour", ColorW, "The button's colour (the arrow puts the theme's back).");
        Head("Ask", AskW, "Asks before it sends: for commands that are hard to take back.");
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h + Px(2)));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawRows()
    {
        List<CommandButton> list = _set.List(_scope);
        float w = ImGui.GetContentRegionAvail().X;
        if (list.Count == 0)
        {
            ImGui.SetCursorPos(new Vector2(Px(8), Px(8)));
            ImGui.PushTextWrapPos(w - Px(8));
            Label(_scope switch
            {
                ButtonScope.Character => "No buttons for this character. Add button puts one here.",
                ButtonScope.Server => "No buttons for every character on this server. Add button puts one here.",
                _ => "No buttons for every character. Add button puts one here.",
            }, Mute);
            ImGui.PopTextWrapPos();
            return;
        }
        float rowH = Px(24);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        int remove = -1, moveFrom = -1, moveBy = 0;
        for (int i = 0; i < list.Count; i++)
        {
            CommandButton b = list[i];
            ImGui.PushID(i);
            Vector2 p = ImGui.GetCursorScreenPos();
            if (i % 2 == 1) dl.AddRectFilled(p, p + new Vector2(w, rowH), RowAlt);
            float x = p.X + Px(4), y = p.Y + Px(2);
            float fieldH = rowH - Px(4);
            var small = new Vector2(Px(20), fieldH);

            if (IconButton("##up", RynthIcons.CaretUp, new Vector2(x, y), small, Text, ButtonFill, enabled: i > 0, font: RynthUiFont.Ui11))
            { moveFrom = i; moveBy = -1; }
            ImGui.SetItemTooltip("Earlier in the grid.");
            if (IconButton("##dn", RynthIcons.CaretDown, new Vector2(x + Px(22), y), small, Text, ButtonFill, enabled: i < list.Count - 1, font: RynthUiFont.Ui11))
            { moveFrom = i; moveBy = 1; }
            ImGui.SetItemTooltip("Later in the grid.");
            x += MoveW + Px(4);

            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(4), Math.Max(1, (fieldH - ImGui.GetFontSize()) * 0.5f)));
            ImGui.PushStyleColor(ImGuiCol.FrameBg, Background);

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGui.SetNextItemWidth(LabelW);
            if (_focusRow == i) { ImGui.SetKeyboardFocusHere(); _focusRow = -1; }
            string label = b.Label;
            if (ImGui.InputTextWithHint("##label", "Label", ref label, (uint)CommandButton.MaxLabel + 1))
            {
                b.Label = CommandButton.CleanLabel(label);
                Changed();
            }
            x += LabelW + Px(4);

            ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGui.SetNextItemWidth(CommandW(w));
            string cmd = b.Command;
            if (ImGui.InputTextWithHint("##cmd", "/ra start, *wave*, @home ...", ref cmd, (uint)CommandLine.MaxLength + 1))
            {
                b.Command = CommandLine.Clean(cmd);
                Changed();
            }
            x += CommandW(w) + Px(4);

            ImGui.PopStyleColor();
            ImGui.PopStyleVar();

            // What the line is, or why it can't be sent.
            CommandCheck check = CommandLine.Check(b.Command);
            bool bad = check.Problem != null;
            string mark = bad ? RynthIcons.Warning : check.Kind == CommandKind.Empty ? RynthIcons.Info : RynthIcons.Check;
            uint markColor = bad ? Red : check.Kind == CommandKind.Empty ? Mute : check.Kind == CommandKind.Say ? Highlight : Green;
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            ImGui.InvisibleButton("##mark", new Vector2(MarkW, fieldH));
            DrawCentered(dl, Font(RynthUiFont.Ui11), mark, new Vector2(x, y), new Vector2(MarkW, fieldH), markColor);
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(check.Problem ?? CommandLine.Describe(check.Kind));
            x += MarkW + Px(4);

            // Colour: the picker's swatch, and the theme's colour back.
            uint shown = b.Color != 0 ? b.Color : ButtonFill;
            var rgb = new Vector3((shown & 0xFF) / 255f, ((shown >> 8) & 0xFF) / 255f, ((shown >> 16) & 0xFF) / 255f);
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            if (ImGui.ColorEdit3("##color", ref rgb, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
            {
                b.Color = 0xFF000000u | (Byte(rgb.Z) << 16) | (Byte(rgb.Y) << 8) | Byte(rgb.X);
                Changed();
            }
            ImGui.SetItemTooltip(b.Color != 0 ? "This button's colour: " + CommandButton.ColorText(b.Color) : "The theme's button colour: click to pick one.");
            if (b.Color != 0)
            {
                if (IconButton("##nocolor", RynthIcons.ArrowCounterClockwise, new Vector2(x + Px(24), y), small, Mute, ButtonFill, font: RynthUiFont.Ui11))
                {
                    b.Color = 0;
                    Changed();
                }
                ImGui.SetItemTooltip("The theme's colour again.");
            }
            x += ColorW + Px(4);

            ImGui.SetCursorScreenPos(new Vector2(x + (AskW - ImGui.GetFrameHeight()) * 0.5f, y));
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(3), Math.Max(1, (fieldH - ImGui.GetFontSize()) * 0.5f)));
            bool ask = b.Confirm;
            if (ImGui.Checkbox("##ask", ref ask)) { b.Confirm = ask; Changed(); }
            ImGui.PopStyleVar();
            ImGui.SetItemTooltip("Ask before it sends.");
            x += AskW + Px(4);

            if (IconButton("##send", RynthIcons.Play, new Vector2(x, y), new Vector2(BtnW, fieldH), Text, ButtonFill,
                    enabled: check.CanSend, font: RynthUiFont.Ui11))
                _press(_scope, i);
            ImGui.SetItemTooltip("Send it now (no question asked).");
            x += BtnW + Px(4);
            if (IconButton("##del", RynthIcons.Trash, new Vector2(x, y), new Vector2(BtnW, fieldH), Red, ButtonFill, font: RynthUiFont.Ui11))
                remove = i;
            ImGui.SetItemTooltip("Remove this button.");

            ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + rowH));
            ImGui.Dummy(Vector2.Zero);
            ImGui.PopID();
        }
        long now = Environment.TickCount64;
        if (remove >= 0)
        {
            list.RemoveAt(remove);
            Changed();
        }
        else if (moveFrom >= 0)
            _set.Move(_scope, moveFrom, moveBy, now);
    }

    private static uint Byte(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private void DrawAddRow(float w)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = Px(22), y = p.Y + Px(2);
        string label = "Add button";
        bool full = _set.List(_scope).Count >= CommandButtonSet.MaxPerList;
        float aw = ButtonWidth(label) + Px(8);
        if (Button("##cb_add", label, new Vector2(p.X, y), new Vector2(aw, h), Text, ButtonFill, enabled: !full))
            AddRow();
        ImGui.SetItemTooltip(full
            ? $"This list is full ({CommandButtonSet.MaxPerList} buttons)."
            : "A new button in this list: type its label and the chat line it sends.");
        float x = p.X + aw + Px(10);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float ty = y + (h - ImGui.GetFontSize()) * 0.5f;
        // The last press or copy for a few seconds, else where the list is saved.
        string note = _note;
        string saved = note.Length > 0 && Environment.TickCount64 - Volatile.Read(ref _noteAtMs) < NoteMs
            ? note
            : "Saved for " + CommandButtonSet.ScopeName(_scope) + ".";
        float textRight = p.X + w;

        // Copy from another character (this character's list only).
        if (_scope == ButtonScope.Character)
        {
            string copy = "Copy";
            float copyW = ButtonWidth(copy) + Px(8), comboW = Px(130);
            float cx = p.X + w - copyW - comboW - Px(4);
            if (cx > x + Px(200))
            {
                string from = "Copy from";
                float fw = ImGui.CalcTextSize(from).X;
                dl.AddText(new Vector2(cx - fw - Px(6), ty), Mute, from);
                if (_copyPick >= _others.Count) _copyPick = 0;
                string pick = _others.Count > 0 ? _others[_copyPick] : "(no other character)";
                ImGui.SetCursorScreenPos(new Vector2(cx, y));
                ImGui.SetNextItemWidth(comboW);
                ImGui.PushStyleColor(ImGuiCol.FrameBg, Field);
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Px(4), Math.Max(1, (h - ImGui.GetFontSize()) * 0.5f)));
                if (ImGui.BeginCombo("##cb_from", pick))
                {
                    if (ImGui.IsWindowAppearing()) _others = _set.OtherCharacters();
                    for (int i = 0; i < _others.Count; i++)
                        if (ImGui.Selectable(_others[i] + "##cbo" + i.ToString(CultureInfo.InvariantCulture), i == _copyPick))
                            _copyPick = i;
                    if (_others.Count == 0) Label("No other character on this server has buttons.", Mute);
                    ImGui.EndCombo();
                }
                ImGui.PopStyleVar();
                ImGui.PopStyleColor();
                ImGui.SetItemTooltip("Another character on this server whose own buttons you want here too.");
                if (Button("##cb_copy", copy, new Vector2(cx + comboW + Px(4), y), new Vector2(copyW, h), Text, ButtonFill,
                        enabled: _others.Count > 0 && !full))
                {
                    string who = _others[_copyPick];
                    int added = _set.CopyFrom(who, Environment.TickCount64, out string problem);
                    Note(added < 0 ? $"Couldn't copy {who}'s buttons: {problem}."
                        : added == 0 ? $"{who} has no buttons to copy (or this list is full)."
                        : $"Copied {added} button{(added == 1 ? "" : "s")} from {who}.");
                }
                ImGui.SetItemTooltip("Adds that character's own buttons after this character's (nothing is replaced).");
                textRight = cx - ImGui.CalcTextSize(from).X - Px(12);
            }
        }
        dl.PushClipRect(new Vector2(x, y), new Vector2(Math.Max(x, textRight), y + h), true);
        dl.AddText(new Vector2(x, ty), Mute, saved);
        dl.PopClipRect();
        ImGui.SetCursorScreenPos(new Vector2(p.X, y + h + Px(2)));
        ImGui.Dummy(Vector2.Zero);
    }

    private void AddRow()
    {
        if (_set.Add(_scope, null, null, Environment.TickCount64) != null)
            _focusRow = _set.List(_scope).Count - 1;
    }

    /// <summary>An edit to the shown list (under the set's lock): saved by the tick a moment later.</summary>
    private void Changed() => _set.MarkChanged(_scope, Environment.TickCount64);
}

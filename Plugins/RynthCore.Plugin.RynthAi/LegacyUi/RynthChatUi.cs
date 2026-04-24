using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// RynthChat — viewer for incoming chat lines. Subscribes to the plugin's
/// OnChatWindowText handler (via Push). The retail chatbox can be hidden via
/// SuppressRetailChat while AC's chat input/command pipeline still works in the
/// background — typed commands and tells continue to function unchanged.
/// </summary>
internal sealed class RynthChatUi
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;

    private readonly object _lock = new();
    private readonly List<ChatLine> _lines = new(512);

    private bool _open = true;
    private bool _wantScrollToBottom = true;

    // Input field state — Enter outside any widget focuses the input,
    // Enter inside the input submits, Esc clears the buffer. The input row
    // is always rendered so the scrollback area has a stable layout.
    private bool _focusInputNextFrame;
    private bool _swallowFirstEnter;
    private string _inputBuffer = string.Empty;

    public Action? OnSettingChanged { get; set; }

    /// <summary>
    /// Routes a submitted line. Set by the plugin so /ra, /mt, /ub etc. can be
    /// handled locally by OnChatBarEnter before falling back to InvokeChatParser.
    /// Without this, plugin slash commands would be keyboard-simulated into the
    /// (invisible) retail chatbar — unreliable with ImGui focus in the picture.
    /// </summary>
    public Action<string>? OnSubmit { get; set; }

    public RynthChatUi(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    private readonly record struct ChatLine(string Text, int ChatType, DateTime At);

    /// <summary>Append a chat line (called from RynthAiPlugin.OnChatWindowText).</summary>
    public void Push(string? text, int chatType)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_lock)
        {
            _lines.Add(new ChatLine(text, chatType, DateTime.Now));

            int max = Math.Max(50, _settings.ChatMaxLines);
            int overflow = _lines.Count - max;
            if (overflow > 0)
                _lines.RemoveRange(0, overflow);
        }

        _wantScrollToBottom = true;
    }

    public void Render()
    {
        if (!_settings.ShowRynthChat) return;

        bool snapShowTs = _settings.ChatShowTimestamps;
        float snapOp    = _settings.ChatOpacity;
        int   snapMax   = _settings.ChatMaxLines;

        ImGui.SetNextWindowSize(new Vector2(520, 220), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(220, 110), new Vector2(2400, 1400));
        ImGui.SetNextWindowBgAlpha(0f); // transparent — we paint our own bg

        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2, 2));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);

        // NoBringToFrontOnFocus keeps the gear sub-window (rendered after) on top
        // even when the user clicks into the main chat (to drag, resize, or select text).
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                                | ImGuiWindowFlags.NoCollapse
                                | ImGuiWindowFlags.NoBringToFrontOnFocus;
        if (_settings.ChatClickThrough)
        {
            flags |= ImGuiWindowFlags.NoInputs
                   | ImGuiWindowFlags.NoMove
                   | ImGuiWindowFlags.NoResize;
        }

        bool visible = ImGui.Begin("##RynthChat", ref _open, flags);
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();

        if (!visible) { ImGui.End(); return; }

        Vector2 winPos  = ImGui.GetWindowPos();
        Vector2 winSize = ImGui.GetWindowSize();

        // Painted background — the Opacity slider controls how opaque the fill is.
        // At 0 the background is fully clear (text floats over the game) and at 1
        // it's a dark panel. The border stays so the drag/resize handles are
        // discoverable regardless.
        var dl = ImGui.GetWindowDrawList();
        float op = Math.Clamp(_settings.ChatOpacity, 0f, 1f);
        uint bg = SetAlpha(ImGui.ColorConvertFloat4ToU32(new Vector4(0.05f, 0.07f, 0.10f, 1f)), op);
        uint br = ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.34f, 0.40f, 1f));
        if (op > 0.001f)
            dl.AddRectFilled(winPos, winPos + winSize, bg, 4f);
        dl.AddRect(winPos, winPos + winSize, br, 4f, ImDrawFlags.None, 1.5f);

        // Gear + popup hoisted into a separate always-interactive sub-window so
        // click-through on the main chat doesn't break the settings access.
        const float btn = 14f;
        Vector2 bPos = new Vector2(winPos.X + winSize.X - btn - 4f, winPos.Y + 4f);
        RenderGearWindow("##rchat_gear_win", "*##rchat_gear", "##rchat_settings", bPos, btn);

        // ── Enter-to-focus input ────────────────────────────────────────
        // When retail chat is suppressed and no ImGui widget is currently
        // active, pressing Enter focuses our input box. The `_swallowFirstEnter`
        // flag eats the same Enter so it doesn't immediately submit.
        if (_settings.SuppressRetailChat
            && !ImGui.IsAnyItemActive()
            && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)))
        {
            _focusInputNextFrame = true;
            _swallowFirstEnter = true;
        }

        // Input row is always rendered at the bottom so the scrollback has a
        // stable layout and never gets covered when typing.
        const float InputRowH = 24f;
        float reservedBottom = InputRowH + 4f;

        // Scrollback child below the gear. Width reserves room for the gear
        // button on the right; height reserves room for the input row below.
        ImGui.SetCursorScreenPos(new Vector2(winPos.X + 4f, winPos.Y + 4f));
        Vector2 childSize = new Vector2(winSize.X - 8f - btn - 6f, winSize.Y - 8f - reservedBottom);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, 0u);
        if (ImGui.BeginChild("##rchat_scroll", childSize,
                ImGuiChildFlags.None,
                ImGuiWindowFlags.NoBackground))
        {
            // Snapshot under the lock — the producer is on a different thread.
            ChatLine[] snap;
            lock (_lock) { snap = _lines.ToArray(); }

            for (int i = 0; i < snap.Length; i++)
            {
                var line = snap[i];
                var col4 = ColorForType(line.ChatType);
                if (snapShowTs)
                {
                    string ts = line.At.ToString("HH:mm:ss ");
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.62f, 0.70f, 1f));
                    ImGui.TextUnformatted(ts);
                    ImGui.PopStyleColor();
                    ImGui.SameLine(0, 0);
                }
                ImGui.PushStyleColor(ImGuiCol.Text, col4);
                ImGui.TextWrapped(line.Text);
                ImGui.PopStyleColor();
            }

            if (_wantScrollToBottom)
            {
                ImGui.SetScrollHereY(1.0f);
                _wantScrollToBottom = false;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();

        // ── Input row (always visible) ──────────────────────────────────
        {
            float inputY = winPos.Y + winSize.Y - InputRowH - 2f;
            ImGui.SetCursorScreenPos(new Vector2(winPos.X + 4f, inputY));
            ImGui.SetNextItemWidth(winSize.X - 8f);

            if (_focusInputNextFrame)
            {
                ImGui.SetKeyboardFocusHere();
                _focusInputNextFrame = false;
            }

            // Use the simple 3-arg overload (no flags) so ImGui.NET's internal
            // resize callback handles ref-string growth correctly. Enter detection
            // is done manually via IsKeyPressed while the input is focused — this
            // avoids both the "callback != NULL" assertion and the buffer-corruption
            // that comes from supplying a custom user-callback.
            //
            // FrameBg is tinted to match the chat body so the input looks like
            // part of the same panel at whatever opacity the user picked.
            Vector4 frameCol     = new Vector4(0.05f, 0.07f, 0.10f, op);
            Vector4 frameHovered = new Vector4(0.08f, 0.11f, 0.15f, op);
            Vector4 frameActive  = new Vector4(0.10f, 0.14f, 0.18f, op);
            ImGui.PushStyleColor(ImGuiCol.FrameBg,        frameCol);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, frameHovered);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  frameActive);
            ImGui.InputText("##rchat_input", ref _inputBuffer, 256u);
            ImGui.PopStyleColor(3);

            bool isFocused = ImGui.IsItemFocused();
            bool enterPressed = ImGui.IsKeyPressed(ImGuiKey.Enter, false)
                              || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false);

            // Eat the same Enter that activated the input, so we don't immediately
            // submit an empty message on first focus.
            if (_swallowFirstEnter)
            {
                _swallowFirstEnter = false;
                enterPressed = false;
            }

            if (isFocused && enterPressed)
            {
                string toSend = _inputBuffer.Trim();
                if (!string.IsNullOrEmpty(toSend))
                {
                    if (OnSubmit != null)
                        OnSubmit(toSend);
                    else if (_host.HasInvokeChatParser)
                        _host.InvokeChatParser(toSend);
                }
                _inputBuffer = string.Empty;
                // Defocus our window so WASD/key input flows back to the game.
                ImGui.SetWindowFocus(null);
            }

            // Esc clears the buffer.
            if (isFocused && ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                _inputBuffer = string.Empty;
        }

        ImGui.End();

        if (snapShowTs != _settings.ChatShowTimestamps
            || snapMax  != _settings.ChatMaxLines
            || MathF.Abs(snapOp - _settings.ChatOpacity) > 0.001f)
        {
            OnSettingChanged?.Invoke();
        }
    }

    /// <summary>
    /// Renders the gear button and its settings popup in a separate always-
    /// interactive sub-window, so the gear stays clickable even when the
    /// main chat window has NoInputs (click-through).
    /// </summary>
    private void RenderGearWindow(string winId, string btnId, string popupId, Vector2 pos, float size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(new Vector2(size, size));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, 0u);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                                      | ImGuiWindowFlags.NoResize
                                      | ImGuiWindowFlags.NoMove
                                      | ImGuiWindowFlags.NoCollapse
                                      | ImGuiWindowFlags.NoBackground
                                      | ImGuiWindowFlags.NoSavedSettings
                                      | ImGuiWindowFlags.NoBringToFrontOnFocus;

        bool open = true;
        if (ImGui.Begin(winId, ref open, flags))
        {
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0.45f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.30f, 0.40f, 0.85f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.20f, 0.25f, 0.35f, 1f));
            if (ImGui.Button(btnId, new Vector2(size, size)))
                ImGui.OpenPopup(popupId);
            ImGui.PopStyleColor(3);
            RenderSettingsPopup();
        }
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    private void RenderSettingsPopup()
    {
        if (!ImGui.BeginPopup("##rchat_settings")) return;

        ImGui.Text("Chat Settings");
        ImGui.Separator();

        ImGui.SetNextItemWidth(140);
        ImGui.SliderFloat("Opacity", ref _settings.ChatOpacity, 0.15f, 1f, "%.2f");

        ImGui.SetNextItemWidth(140);
        ImGui.SliderInt("Max Lines", ref _settings.ChatMaxLines, 50, 5000);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scrollback capacity. Older lines are dropped when this limit is hit.");

        ImGui.Checkbox("Timestamps", ref _settings.ChatShowTimestamps);

        ImGui.Checkbox("Click-Through", ref _settings.ChatClickThrough);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Mouse passes through to the game. The gear button stays clickable.\nTurn off again from the gear or Advanced Settings.");

        if (ImGui.Button("Clear##rchat_clear"))
        {
            lock (_lock) _lines.Clear();
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy Recent##rchat_copy"))
        {
            const int lastN = 100;
            ChatLine[] tail;
            lock (_lock)
            {
                int start = Math.Max(0, _lines.Count - lastN);
                tail = _lines.GetRange(start, _lines.Count - start).ToArray();
            }
            var sb = new System.Text.StringBuilder();
            foreach (var l in tail) sb.AppendLine(l.Text);
            ImGui.SetClipboardText(sb.ToString());
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy the last 100 lines to the clipboard.");

        ImGui.EndPopup();
    }

    /// <summary>Maps an AC eChatTypes value to a display colour.
    /// Values come from gmMainChatUI's incoming text dispatch — see
    /// Chorizite enum.cs eChatTypes for the canonical list.</summary>
    private static Vector4 ColorForType(int chatType) => chatType switch
    {
        // 0x02 Speech, 0x03 SpeechDirect (you-target), 0x04 SpeechDirectSend (you sending)
        2  => new Vector4(0.95f, 0.95f, 0.95f, 1f),  // Speech — white
        3  => new Vector4(1.00f, 0.55f, 0.85f, 1f),  // SpeechDirect (tells to you) — pink
        4  => new Vector4(1.00f, 0.55f, 0.85f, 1f),  // tells you sent — pink
        // 0x05 SystemEvent — soft white-blue
        5  => new Vector4(0.85f, 0.90f, 1.00f, 1f),
        // 0x06 Combat (general), 0x15 CombatEnemy, 0x16 CombatSelf
        6  => new Vector4(1.00f, 0.80f, 0.50f, 1f),  // combat — amber
        0x15 => new Vector4(1.00f, 0.55f, 0.45f, 1f), // enemy hits you — red-orange
        0x16 => new Vector4(0.55f, 1.00f, 0.55f, 1f), // your hits — green
        // 0x07 Magic (general), 0x11 MagicCasting
        7  => new Vector4(0.55f, 0.80f, 1.00f, 1f),  // magic — light blue
        0x11 => new Vector4(0.55f, 0.80f, 1.00f, 1f),
        // Channels
        0x08 => new Vector4(0.70f, 0.95f, 0.90f, 1f), // Channel — teal
        0x09 => new Vector4(0.70f, 0.95f, 0.90f, 1f),
        0x0A => new Vector4(0.85f, 0.85f, 0.55f, 1f), // SocialChannel — pale yellow
        0x0B => new Vector4(0.85f, 0.85f, 0.55f, 1f),
        // 0x0C Emote
        0x0C => new Vector4(0.75f, 1.00f, 0.75f, 1f),
        // 0x0D Advancement (level up etc.)
        0x0D => new Vector4(1.00f, 0.95f, 0.30f, 1f),
        // 0x0E Abuse, 0x0F Help
        0x0E => new Vector4(1.00f, 0.40f, 0.40f, 1f),
        0x0F => new Vector4(0.55f, 1.00f, 1.00f, 1f),
        // 0x10 Appraisal — soft cyan
        0x10 => new Vector4(0.65f, 0.90f, 1.00f, 1f),
        // 0x12 Allegiance, 0x13 Fellowship
        0x12 => new Vector4(1.00f, 0.55f, 1.00f, 1f), // allegiance — magenta
        0x13 => new Vector4(0.55f, 1.00f, 0.85f, 1f), // fellowship — mint
        // 0x14 World broadcast
        0x14 => new Vector4(1.00f, 0.45f, 0.65f, 1f),
        // 0x17 Recall
        0x17 => new Vector4(0.85f, 0.70f, 1.00f, 1f),
        // 0x18 Craft, 0x19 Salvaging
        0x18 => new Vector4(0.95f, 0.80f, 0.50f, 1f),
        0x19 => new Vector4(0.85f, 0.95f, 0.55f, 1f),
        // 0x1A Transient (mouseover etc.)
        0x1A => new Vector4(0.70f, 0.75f, 0.80f, 1f),
        // 0x1B General, 0x1C Trade, 0x1D LFG, 0x1E Roleplay
        0x1B => new Vector4(0.85f, 0.95f, 1.00f, 1f),
        0x1C => new Vector4(0.65f, 1.00f, 0.65f, 1f),
        0x1D => new Vector4(1.00f, 0.85f, 0.55f, 1f),
        0x1E => new Vector4(1.00f, 0.75f, 0.95f, 1f),
        // 0x1F AdminTell — bright orange so it pops
        0x1F => new Vector4(1.00f, 0.55f, 0.20f, 1f),
        // 0x20 Olthoi, 0x21 Society
        0x20 => new Vector4(0.85f, 0.45f, 0.25f, 1f),
        0x21 => new Vector4(0.85f, 0.95f, 1.00f, 1f),
        // Default / AllChannels / unknown
        _    => new Vector4(0.92f, 0.92f, 0.92f, 1f),
    };

    private static uint SetAlpha(uint col, float alpha)
    {
        uint a = (uint)(Math.Clamp(alpha, 0f, 1f) * 255f);
        return (col & 0x00FFFFFF) | (a << 24);
    }
}

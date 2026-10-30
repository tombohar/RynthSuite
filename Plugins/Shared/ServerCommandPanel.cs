// ============================================================================
//  RynthSuite - Plugins/Shared/ServerCommandPanel.cs
//  Draws a ServerCommandSet (ServerCommandShare.cs) as buttons: one name box for the
//  commands that take an argument, then a wrapped row of buttons. A command flagged 'c'
//  turns its button into "Confirm ..." on the first click and runs on the second (within
//  ConfirmMs). Clicks hand the chat line to the caller's send, which runs it on the plugin
//  tick. Compiled into each plugin that draws server commands. Render thread only.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.Shared;

internal sealed class ServerCommandPanel
{
    private const long ConfirmMs = 4000;

    private static readonly Vector4 ColConfirm = new(0.95f, 0.45f, 0.35f, 1f);

    private readonly string _id;
    private string _argument = string.Empty;
    // The command waiting for its second click, and when that offer ends.
    private string _armed = string.Empty;
    private long _armedUntil;
    private string _lastSent = string.Empty;

    /// <param name="id">ImGui id suffix; unique per panel drawn in the same window.</param>
    public ServerCommandPanel(string id) => _id = id;

    /// <summary>
    /// Draws <paramref name="set"/>'s commands into the current window. <paramref name="send"/>
    /// gets the chat line (it should queue it, not run it here) and returns false when it can't.
    /// </summary>
    public void Render(ServerCommandSet set, Func<string, bool> send, string argumentHint = "name (blank = selected player)")
    {
        if (set.Commands.Count == 0) return;
        long now = Environment.TickCount64;
        if (_armed.Length > 0 && now > _armedUntil) _armed = string.Empty;

        bool anyArgument = false;
        foreach (ServerCommand c in set.Commands)
            if (c.TakesArgument) { anyArgument = true; break; }
        if (anyArgument)
        {
            ImGui.SetNextItemWidth(MathF.Min(260f, ImGui.GetContentRegionAvail().X));
            ImGui.InputTextWithHint("##scarg" + _id, argumentHint, ref _argument, (uint)ServerCommandShare.MaxArgument);
        }

        ImGuiStylePtr style = ImGui.GetStyle();
        // The row's right edge, taken at the start of a line (ImGui 1.91 has no GetWindowContentRegionMax).
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        for (int i = 0; i < set.Commands.Count; i++)
        {
            ServerCommand c = set.Commands[i];
            bool armed = c.Confirm && _armed == c.Command;
            string label = armed ? "Confirm " + c.Label + "?" : c.Label;

            // Wraps to a new line when this button wouldn't fit after the previous one.
            if (i > 0)
            {
                float width = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2f;
                float lastRight = ImGui.GetItemRectMax().X;
                if (lastRight + style.ItemSpacing.X + width <= right) ImGui.SameLine();
            }

            string? line = ServerCommandShare.Compose(c, _argument);
            bool disabled = line == null;
            if (disabled) ImGui.BeginDisabled();
            if (armed) ImGui.PushStyleColor(ImGuiCol.Text, ColConfirm);
            bool clicked = ImGui.Button(label + "##sc" + _id + i);
            if (armed) ImGui.PopStyleColor();
            if (disabled) ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                string preview = line ?? "Type a name first.";
                SafeImGui.SetTooltip(c.Tooltip.Length > 0 ? c.Tooltip + "\n\n" + preview : preview);
            }
            if (!clicked || line == null) continue;

            if (c.Confirm && !armed)
            {
                _armed = c.Command;
                _armedUntil = now + ConfirmMs;
                continue;
            }
            _armed = string.Empty;
            _lastSent = send(line) ? "Sent: " + line : "Couldn't send: " + line;
        }

        if (_lastSent.Length > 0) SafeImGui.TextDisabled(_lastSent);
    }
}

// LegacyDashboardRenderer.LocalFeatures.cs — style shared with the plugin's own ImGui windows
// (the Mini Remote and its tabs, floating HUDs, chat translator).
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed partial class LegacyDashboardRenderer
{
    /// <summary>Pushes RynthAi's window colours; returns the count for PopStyleColor.</summary>
    internal static int PushDashboardStyle()
    {
        // Muted slate-blue accents. TitleBgActive replaces ImGui's default yellow on detached windows.
        ImGui.PushStyleColor(ImGuiCol.FrameBg,          new Vector4(0.18f, 0.22f, 0.28f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,   new Vector4(0.24f, 0.30f, 0.38f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,    new Vector4(0.30f, 0.38f, 0.48f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark,        new Vector4(0.85f, 0.90f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg,          new Vector4(0.10f, 0.14f, 0.20f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Header,           new Vector4(0.20f, 0.28f, 0.36f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,    new Vector4(0.26f, 0.36f, 0.46f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,     new Vector4(0.32f, 0.44f, 0.58f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg,          new Vector4(0.14f, 0.18f, 0.24f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,    new Vector4(0.22f, 0.30f, 0.40f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, new Vector4(0.10f, 0.14f, 0.20f, 0.85f));
        return 11;
    }
}

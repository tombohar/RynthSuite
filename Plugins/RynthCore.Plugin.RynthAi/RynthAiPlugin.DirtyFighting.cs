// RynthAiPlugin.DirtyFighting.cs — the Dirty Fighting rotation's window and /ra dfheight (2026-11-11).
//
// The rotation itself is CombatManager.DirtyFighting.cs (which height each attack aims at) and
// Combat/DirtyFightingHeights.cs (the pure choice). Here: the small "Dirty Fighting" window, opened
// from the dashboard's options menu (the ⋯ button, or right-click the header) or /ra dfheight window,
// with the on/off checkbox, the order, the height once all three effects are on, and what is on the
// current target; and the command. Render thread for the window: its controls write the settings
// straight into the live settings (as the Outdoor Hunt window does; the autosave picks them up).
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using Ui = RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    internal const string DfHeightUsage = "Usage: /ra dfheight [on|off|status|window]";

    private volatile bool _dfPanelOpen;
    private string _dfPanelStatus = string.Empty;   // render thread
    private long _dfPanelStatusAtMs;

    public void ToggleDfHeightPanel() => _dfPanelOpen = !_dfPanelOpen;

    internal bool DfHeightRotationOn => _dashboard?.Settings.DfHeightRotation == true;

    // ── /ra dfheight ─────────────────────────────────────────────────────

    private void HandleDfHeightCommand(string[] parts)
    {
        var s = _dashboard?.Settings;
        if (s == null) { ReplyFail("[RynthAi] Settings not ready."); return; }
        string verb = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
        switch (verb)
        {
            case "on":
            case "off":
                s.DfHeightRotation = verb == "on";
                _dashboard!.SaveSettings();
                ReplyOk($"[RynthAi] Dirty Fighting rotation {verb}. {DfHeightStatusLine()}");
                break;
            case "status":
                ChatLine($"[RynthAi] Dirty Fighting rotation: {DfHeightStatusLine()}");
                break;
            case "window":
            case "show":
                _dfPanelOpen = true;
                ReplyOk("[RynthAi] Dirty Fighting window open.");
                break;
            default:
                ReplyFail($"[RynthAi] dfheight: '{parts[2]}' isn't a choice. {DfHeightUsage}");
                break;
        }
    }

    private string DfHeightStatusLine()
    {
        if (_combatManager != null) return _combatManager.DirtyFightingStatus();
        var s = _dashboard?.Settings;
        return s == null ? "settings not ready." : (s.DfHeightRotation ? "On" : "Off") + " (not logged in yet).";
    }

    // ── The window (render thread) ───────────────────────────────────────

    private static readonly string[] DfOrderLabels = Array.ConvertAll(DirtyFightingHeights.OrderChoices, DirtyFightingHeights.OrderLabel);
    private static readonly string[] DfDefaultLabels = { "Attack Height setting", "Low", "Medium", "High" };

    private void RenderDfHeightPanel()
    {
        if (!_dfPanelOpen) return;
        var s = _dashboard?.Settings;
        if (s == null) return;
        float k = Math.Max(0.5f, Ui.Font(RynthUiFont.Ui14).FontSize / 14f);
        ImGui.SetNextWindowSize(new Vector2(420 * k, 330 * k), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(300 * k, 200 * k), new Vector2(1400 * k, 1000 * k));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Ui.WithAlpha(Ui.Background, 0xF2));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Ui.Color(RynthUiColor.TitleBar));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Ui.Color(RynthUiColor.TitleBar));
        ImGui.PushStyleColor(ImGuiCol.Border, Ui.Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, Ui.Text);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12 * k, 10 * k));
        bool open = true;
        bool visible = ImGui.Begin("Dirty Fighting##rynthdfheight", ref open, ImGuiWindowFlags.NoCollapse);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
        if (!open) _dfPanelOpen = false;
        if (!visible) { ImGui.End(); return; }
        try
        {
            using (Ui.PushFont(RynthUiFont.Ui14))
                DfHeightPanelBody(s, k);
        }
        finally { ImGui.End(); }
    }

    private void DfHeightPanelBody(LegacyUiSettings s, float k)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8 * k, 5 * k));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8 * k, 6 * k));
        try
        {
            ImGui.Checkbox("Dirty Fighting rotation##dfrot", ref s.DfHeightRotation);
            Wrapped("Melee and missile attacks aim at the height of the next Dirty Fighting effect the target "
                + "doesn't have: High lowers its attack skills and the heals it gets, Medium makes it bleed, "
                + "Low lowers its defense skills. Needs Dirty Fighting trained; off, or untrained, the Melee and "
                + "Missile Attack Height settings apply.", Ui.Text);
            ImGui.Separator();

            if (!s.DfHeightRotation) ImGui.BeginDisabled();
            float fieldW = Math.Max(120 * k, ImGui.GetContentRegionAvail().X * 0.55f);
            int orderIdx = Array.IndexOf(DirtyFightingHeights.OrderChoices, DirtyFightingHeights.NormalizeOrder(s.DfHeightOrder));
            if (orderIdx < 0) orderIdx = 0;
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.Combo("Order##dforder", ref orderIdx, DfOrderLabels, DfOrderLabels.Length))
                s.DfHeightOrder = DirtyFightingHeights.OrderChoices[orderIdx];
            int defIdx = Math.Clamp(s.DfDefaultHeight, -1, 2) + 1;
            ImGui.SetNextItemWidth(fieldW);
            if (ImGui.Combo("Then attack##dfdefault", ref defIdx, DfDefaultLabels, DfDefaultLabels.Length))
                s.DfDefaultHeight = defIdx - 1;
            if (!s.DfHeightRotation) ImGui.EndDisabled();
            Wrapped("Once all three are on, attacks use the Then attack height (Attack Height setting: the Melee "
                + "or Missile Attack Height), and go back to an effect about 5 seconds before it runs out. "
                + "Each effect lands on up to 1 hit in 4 and lasts 20 seconds; RynthAi sees it land from the "
                + "\"Dirty Fighting! ...\" combat line.", Ui.Mute);
            ImGui.Separator();

            // The status reads skills from the host: refreshed twice a second, not every frame.
            long now = Environment.TickCount64;
            if (now - _dfPanelStatusAtMs > 500 || _dfPanelStatus.Length == 0)
            {
                _dfPanelStatusAtMs = now;
                try { _dfPanelStatus = DfHeightStatusLine(); } catch { _dfPanelStatus = string.Empty; }
            }
            using (Ui.PushFont(RynthUiFont.UiBold14)) ImGui.TextUnformatted("Now");
            if (_dfPanelStatus.Length > 0) Wrapped(_dfPanelStatus, Ui.Text);
        }
        finally { ImGui.PopStyleVar(2); }
    }
}

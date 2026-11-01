// ============================================================================
//  RynthAi - Loot/LootWhyWindow.cs
//  "Why this loot rule?" (2026-10-31, the owner: buttons on the UI, not only
//  commands): /ra loot why's answer in a small window, opened by the Loot
//  Editor's and the Classic Loot tab's "Why this loot rule?" buttons, the
//  Selection window's right-click menu (RynthDefective) and /ra loot why window.
//
//    the item     its name, identified or not, and what the looter does with it
//    the rule     for a VTank profile: the rule that takes it (or none), then
//                 every rule above it that came close and the first condition
//                 each failed, with the value it read
//    Check again  asks again for whatever is selected now (an ID that came in,
//                 another item); Close.
//
//  AC's render thread: it draws the report the tick built (LootWhy.Report, an
//  immutable record) and queues Check again for the tick. Looks: Shared/RynthUi.
// ============================================================================
using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Loot;

internal sealed class LootWhyWindow
{
    public const string Title = "Why this loot rule?##rynthai_lootwhy";

    private readonly Action _checkAgain;

    public LootWhyWindow(Action checkAgain) => _checkAgain = checkAgain;

    /// <summary>Draws the window for <paramref name="r"/>; <paramref name="open"/> goes false when it is closed. Render thread.</summary>
    public void Render(LootWhy.Report r, ref bool open)
    {
        float k = Math.Max(0.5f, Font(RynthUiFont.Ui11).FontSize / 11f);
        float wrap = 440 * k;
        Vector2 mouse = ImGui.GetIO().MousePos, display = ImGui.GetIO().DisplaySize;
        // Where it was asked for (the button under the mouse), kept on the screen.
        ImGui.SetNextWindowPos(new Vector2(Math.Clamp(mouse.X + 12, 0, Math.Max(0, display.X - wrap - 30)),
            Math.Clamp(mouse.Y + 12, 0, Math.Max(0, display.Y - 260 * k))), ImGuiCond.Appearing);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, WithAlpha(Background, 0xF2));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Color(RynthUiColor.TitleBar));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Color(RynthUiColor.TitleBar));
        ImGui.PushStyleColor(ImGuiCol.Border, Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Field);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10 * k, 8 * k));
        bool visible = ImGui.Begin(Title, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(6);
        if (!visible) { ImGui.End(); return; }
        try
        {
            using (PushFont(RynthUiFont.Ui11))
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
                Body(r, wrap);
                ImGui.PopTextWrapPos();
                Buttons(ref open, k);
            }
        }
        finally { ImGui.End(); }
    }

    private static void Body(LootWhy.Report r, float wrap)
    {
        if (!r.Ok)
        {
            Label(Sentence(r.Error), Highlight);
            return;
        }
        using (PushFont(RynthUiFont.UiBold11)) Label(r.ItemName, Accent);
        Label(r.HasId ? "Identified." : "Not identified yet: rules that read its ID details can't decide.", Mute);
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.TextUnformatted("The looter: " + r.Looter);
        if (!r.RuleDetail)
        {
            Label(r.Decision.Length > 0 ? r.Decision + "."
                : "Rule-by-rule detail is for VTank .utl profiles; this profile is RynthAi's own (.json).", Mute);
            return;
        }
        ImGui.TextUnformatted("The profile: " + r.Decision);
        if (r.AskedForId) Label("Asked the server for its ID: Check again once it's in.", Highlight);
        if (r.SwitchedOff.Length > 0) Label(LootWhy.SwitchedOffLine(r.SwitchedOff), Highlight);
        ImGui.Dummy(new Vector2(0, 4));
        if (r.NearMisses.Count == 0)
        {
            Label("No rule above it came close (each failed on its first condition).", Mute);
            return;
        }
        Label(r.NearMisses.Count == 1
            ? "1 rule above it came close; the first condition it failed:"
            : r.NearMisses.Count.ToString(CultureInfo.InvariantCulture) + " rules above it came close; the first condition each failed:", Mute);
        // A long list scrolls; a short one is as tall as it needs.
        float lineH = ImGui.GetTextLineHeightWithSpacing();
        float h = Math.Min(r.NearMisses.Count * lineH * 1.6f + 8, lineH * 12);
        ImGui.BeginChild("##lootwhy_misses", new Vector2(wrap, h), ImGuiChildFlags.Borders);
        ImGui.PushTextWrapPos(0);
        foreach (LootWhy.NearMiss m in r.NearMisses)
            SafeImGui.TextWrapped(LootWhy.NearMissLine(m));
        ImGui.PopTextWrapPos();
        ImGui.EndChild();
    }

    private void Buttons(ref bool open, float k)
    {
        ImGui.Dummy(new Vector2(0, 4 * k));
        Vector2 p = ImGui.GetCursorScreenPos();
        float h = 22 * k;
        const string Again = "Check again", Close = "Close";
        float aw = ButtonWidth(Again), cw = ButtonWidth(Close);
        if (Button("##lootwhy_again", Again, p, new Vector2(aw, h), Text, AccentFill))
            _checkAgain();
        SafeImGui.SetItemTooltip("Ask again for the item selected now (select another one first, or wait for its ID). Also: /ra loot why");
        if (Button("##lootwhy_close", Close, new Vector2(p.X + aw + 6 * k, p.Y), new Vector2(cw, h), Text, ButtonFill))
            open = false;
        ImGui.SetCursorScreenPos(new Vector2(p.X, p.Y + h));
        ImGui.Dummy(new Vector2(aw + cw + 6 * k, 2 * k));
    }

    /// <summary>"select an item first" -> "Select an item first".</summary>
    private static string Sentence(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}

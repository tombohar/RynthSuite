// HudSetupUi.cs — "Inventory HUDs" setup window (UtilityBelt Item Hud tab equivalent).
//
//   * Show / hide the pack item count HUD and the Mini Remote.
//   * Two lists: items on the HUD (left) and carried items not on it (right).
//     "<" pins the highlighted right row, ">" unpins the highlighted left row,
//     Up / Down reorder the HUD. "Add from inventory" pins the item selected in the game.
//   * Mini Remote slots: pick a slot, then Set (selected game item) or Clear.
// Render thread only; list edits are posted to the pump thread.
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Huds;

internal sealed class HudSetupUi
{
    private const float ListIcon = 18f;

    private readonly HudController _hud;
    private string _selOnHud = string.Empty;
    private string _selInPack = string.Empty;
    private string _packFilter = string.Empty;
    private int _slot;

    public HudSetupUi(HudController hud) => _hud = hud;

    public void Render()
    {
        var s = _hud.State;
        ImGui.SetNextWindowSize(new Vector2(640, 520), ImGuiCond.FirstUseEver);
        bool open = true;
        bool visible = ImGui.Begin("Inventory HUDs##rynthhudsetup", ref open);
        if (!open) s.ShowSetup = false;
        if (!visible) { ImGui.End(); return; }

        RenderHudToggles(s);
        ImGui.Separator();
        RenderItemLists(s);
        ImGui.Separator();
        RenderSlotPicker(s);
        ImGui.End();
    }

    private static void RenderHudToggles(HudState s)
    {
        bool itemHud = s.ShowItemHud;
        if (ImGui.Checkbox("Show pack item count HUD", ref itemHud)) s.ShowItemHud = itemHud;
        ImGui.SameLine();
        bool names = s.ItemHudShowNames;
        if (ImGui.Checkbox("with names", ref names)) s.ItemHudShowNames = names;
        ImGui.SameLine();
        bool remote = s.ShowMiniRemote;
        if (ImGui.Checkbox("Show Mini Remote", ref remote)) s.ShowMiniRemote = remote;
        ImGui.TextDisabled("Both are separate windows: drag them anywhere, dock them into other RynthAi windows,");
        ImGui.TextDisabled("or pull them outside the game window. Right-click a HUD for its options.");
    }

    private void RenderItemLists(HudState s)
    {
        ImGui.TextWrapped("Left = shown on the HUD. Right = in your pack but not on the HUD. "
                          + "< pins the highlighted right row; > removes the highlighted left row. "
                          + "Add from inventory uses the item selected in the game.");

        if (ImGui.Button("Scan pack")) _hud.Post(_hud.RequestScan);
        ImGui.SameLine();
        if (ImGui.Button("Add from inventory")) _hud.Post(_hud.AddSelectedToItemHud);
        ImGui.SameLine();
        if (ImGui.Button("Drop missing")) _hud.Post(_hud.DropMissingFromItemHud);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove HUD items you no longer carry.");

        var entries = s.ItemHudItems.ToArray();
        var pack = _hud.Pack;
        float listH = 230f;
        float colW = Math.Max(180f, (ImGui.GetContentRegionAvail().X - 50f) * 0.5f);

        // ── Left: on the HUD ──
        ImGui.BeginGroup();
        ImGui.TextUnformatted($"On HUD ({entries.Length})");
        if (ImGui.BeginChild("##hudon", new Vector2(colW, listH), ImGuiChildFlags.Borders))
        {
            foreach (var e in entries)
            {
                long count = pack.CountOf(e.Name);
                if (Row(e.Name, e.IconDid, $"{e.Name} | {count:N0}", _selOnHud == e.Name, count == 0))
                    _selOnHud = e.Name;
            }
        }
        ImGui.EndChild();
        bool haveLeft = entries.Any(e => e.Name == _selOnHud);
        ImGui.BeginDisabled(!haveLeft);
        string upName = _selOnHud;
        if (ImGui.SmallButton("Up")) _hud.Post(() => _hud.MoveItemHudEntry(upName, -1));
        ImGui.SameLine();
        if (ImGui.SmallButton("Down")) _hud.Post(() => _hud.MoveItemHudEntry(upName, +1));
        ImGui.EndDisabled();
        ImGui.EndGroup();

        // ── Middle: move buttons ──
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Dummy(new Vector2(1, listH * 0.35f));
        ImGui.BeginDisabled(string.IsNullOrEmpty(_selInPack));
        string add = _selInPack;
        if (ImGui.Button("<", new Vector2(36, 0))) { _hud.Post(() => _hud.AddToItemHud(add)); _selInPack = string.Empty; }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(!haveLeft);
        string remove = _selOnHud;
        if (ImGui.Button(">", new Vector2(36, 0))) { _hud.Post(() => _hud.RemoveFromItemHud(remove)); _selOnHud = string.Empty; }
        ImGui.EndDisabled();
        ImGui.EndGroup();

        // ── Right: in pack, not on the HUD ──
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted("Not on HUD");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(80f, colW - 90f));
        ImGui.InputTextWithHint("##hudpackfilter", "filter", ref _packFilter, 64u);
        if (ImGui.BeginChild("##hudoff", new Vector2(colW, listH), ImGuiChildFlags.Borders))
        {
            var pinned = entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in pack.Rows)
            {
                if (pinned.Contains(r.Name)) continue;
                if (_packFilter.Length > 0 && !r.Name.Contains(_packFilter, StringComparison.OrdinalIgnoreCase)) continue;
                if (Row(r.Name, r.IconDid, $"{r.Name} | {r.Count:N0}", _selInPack == r.Name, false))
                    _selInPack = r.Name;
                // Double-click pins straight away.
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    string name = r.Name;
                    _hud.Post(() => _hud.AddToItemHud(name));
                }
            }
            if (pack.Rows.Length == 0) ImGui.TextDisabled("Scanning your pack...");
        }
        ImGui.EndChild();
        ImGui.EndGroup();
    }

    /// <summary>Icon + label selectable row; returns true when clicked.</summary>
    private bool Row(string name, uint iconDid, string label, bool selected, bool dim)
    {
        Vector2 start = ImGui.GetCursorPos();
        bool clicked = ImGui.Selectable($"##row{name}", selected, ImGuiSelectableFlags.AllowOverlap, new Vector2(0, ListIcon));
        ImGui.SetCursorPos(start);
        HudDraw.Icon(_hud.Icons, iconDid, name, ListIcon, dim);
        ImGui.SameLine();
        if (dim) SafeImGui.TextDisabled(label);
        else ImGui.TextUnformatted(label);
        return clicked;
    }

    private void RenderSlotPicker(HudState s)
    {
        var slots = s.MiniRemoteSlots.ToArray();
        if (slots.Length == 0) return;
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Mini Remote slots");
        SafeImGui.TextDisabled($"Slot 01 = top-left of the Mini Remote grid ({HudState.MiniRemoteColumns} per row). "
                           + "Select an item in your pack, pick the slot, then Set.");
        _slot = Math.Clamp(_slot, 0, slots.Length - 1);
        string Label(int i) => $"{i + 1:00} - {(slots[i].IsEmpty ? "(empty)" : slots[i].Name)}";

        ImGui.SetNextItemWidth(320);
        if (ImGui.BeginCombo("##mrslot", Label(_slot)))
        {
            for (int i = 0; i < slots.Length; i++)
                if (ImGui.Selectable(Label(i), i == _slot)) _slot = i;
            ImGui.EndCombo();
        }
        int slot = _slot;
        ImGui.SameLine();
        if (ImGui.Button("Set")) _hud.Post(() => _hud.SetSlotFromSelection(slot));
        ImGui.SameLine();
        ImGui.BeginDisabled(slots[slot].IsEmpty);
        if (ImGui.Button("Clear")) _hud.Post(() => _hud.ClearSlot(slot));
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh")) _hud.Post(_hud.RequestScan);
    }
}

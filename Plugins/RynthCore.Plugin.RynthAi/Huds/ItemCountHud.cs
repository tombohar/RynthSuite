// ItemCountHud.cs — Floating "pack item count" HUD: one row per pinned item with its
// icon, carried count and (optionally) name. Right-click for options. Render thread only.
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Huds;

internal sealed class ItemCountHud
{
    private const string OptionsPopup = "##itemhudopts";
    private static readonly Vector4 ColCount = new(0.95f, 0.95f, 0.95f, 1f);
    private static readonly Vector4 ColNone = new(0.95f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 ColName = new(0.70f, 0.75f, 0.80f, 1f);

    private readonly HudController _hud;
    private bool _rescueChecked; // off-screen check done for the current show

    public ItemCountHud(HudController hud) => _hud = hud;

    /// <summary>The HUD was not drawn this frame: check its position again on the next show.</summary>
    public void OnHidden() => _rescueChecked = false;

    public void Render()
    {
        var s = _hud.State;
        var entries = s.ItemHudItems.ToArray();
        var pack = _hud.Pack;

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        if (s.ItemHudLocked) flags |= ImGuiWindowFlags.NoMove;

        // Right of and above centre, clear of the character in the middle of the view.
        UiPlacement.CenterFirstUse(new Vector2(220, -160));
        ImGui.SetNextWindowBgAlpha(0.55f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2));
        bool visible = ImGui.Begin("Item HUD##rynthitemhud", flags);
        ImGui.PopStyleVar(2);
        if (!visible) { ImGui.End(); return; }
        UiPlacement.RescueOncePerShow(ref _rescueChecked);

        if (entries.Length == 0)
        {
            ImGui.TextDisabled("Item HUD is empty.");
            ImGui.TextDisabled("Right-click > Edit items...");
        }

        float size = s.ItemHudIconSize;
        foreach (var e in entries)
        {
            long count = pack.CountOf(e.Name);
            HudDraw.Icon(_hud.Icons, e.IconDid, e.Name, size, count == 0);
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"{e.Name}\n{count:N0} carried");
            ImGui.SameLine();
            // Vertically centre the text on the icon.
            float pad = (size - ImGui.GetTextLineHeight()) * 0.5f;
            if (pad > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + pad);
            SafeImGui.TextColored(count == 0 ? ColNone : ColCount, count.ToString("N0"));
            if (s.ItemHudShowNames)
            {
                ImGui.SameLine();
                SafeImGui.TextColored(ColName, e.Name);
            }
        }

        if (ImGui.IsWindowHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right)) ImGui.OpenPopup(OptionsPopup);
        RenderOptions(s);
        ImGui.End();
    }

    private void RenderOptions(HudState s)
    {
        if (!ImGui.BeginPopup(OptionsPopup)) return;
        ImGui.TextDisabled("Item HUD");
        bool names = s.ItemHudShowNames;
        if (ImGui.Checkbox("Show names", ref names)) s.ItemHudShowNames = names;
        bool locked = s.ItemHudLocked;
        if (ImGui.Checkbox("Lock position", ref locked)) s.ItemHudLocked = locked;
        float size = s.ItemHudIconSize;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("Icon size", ref size, 12f, 48f, "%.0f")) s.ItemHudIconSize = size;
        ImGui.Separator();
        if (ImGui.MenuItem("Edit items...")) s.ShowSetup = true;
        if (ImGui.MenuItem("Hide HUD")) s.ShowItemHud = false;
        ImGui.EndPopup();
    }
}

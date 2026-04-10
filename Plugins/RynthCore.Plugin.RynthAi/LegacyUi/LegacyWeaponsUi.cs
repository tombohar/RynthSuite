using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyWeaponsUi
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;
    private WorldObjectCache? _worldFilter;

    private static readonly string[] Elements =
        { "Slash", "Pierce", "Bludgeon", "Fire", "Cold", "Lightning", "Acid", "Nether" };

    public LegacyWeaponsUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
    }

    public void SetWorldFilter(WorldObjectCache cache) => _worldFilter = cache;

    public void Render()
    {
        if (!DashWindows.ShowWeapons) return;

        ImGui.SetNextWindowSize(new Vector2(480, 300), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Weapons & Items##RynthAiWeapons", ref DashWindows.ShowWeapons))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Configured Weapons / Wands");
        ImGui.Spacing();

        if (ImGui.BeginTable("WeaponsTable", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Element",   ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("",          ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _settings.ItemRules.Count; i++)
            {
                var rule = _settings.ItemRules[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                bool inCache = _worldFilter?[rule.Id] != null;
                if (inCache)
                {
                    ImGui.TextUnformatted(rule.Name);
                }
                else
                {
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), rule.Name);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 0.7f), "(Gone)");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Item not in inventory.\nRemove and re-add the correct weapon.");
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                int elemIdx = Array.IndexOf(Elements, rule.Element);
                if (elemIdx < 0) elemIdx = 0;
                if (ImGui.Combo($"##Elem{i}", ref elemIdx, Elements, Elements.Length))
                    rule.Element = Elements[elemIdx];

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Del##{i}"))
                {
                    // Clear any monster rule references to this weapon
                    foreach (var mr in _settings.MonsterRules)
                        if (mr.WeaponId == rule.Id) mr.WeaponId = 0;
                    _settings.ItemRules.RemoveAt(i);
                    ImGui.EndTable();
                    ImGui.End();
                    return;
                }
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        ImGui.Spacing();

        bool canAdd = _host.HasGetSelectedItemId;
        if (!canAdd) ImGui.BeginDisabled();

        if (ImGui.Button("Add Selected Item", new Vector2(160, 24)))
        {
            uint selId = _host.GetSelectedItemId();
            if (selId == 0)
            {
                _host.WriteToChat("[RynthAi] No item selected — click a weapon in your inventory first.", 1);
            }
            else if (_worldFilter == null)
            {
                _host.WriteToChat("[RynthAi] Object cache not ready.", 1);
            }
            else
            {
                var wo = _worldFilter[(int)selId];
                if (wo == null)
                    _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
                else if (!IsWeapon(wo))
                    _host.WriteToChat("[RynthAi] Selected item is not a weapon or wand.", 1);
                else if (_settings.ItemRules.Any(x => x.Id == wo.Id))
                    _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
                else
                {
                    _settings.ItemRules.Add(new ItemRule
                    {
                        Id      = wo.Id,
                        Name    = wo.Name,
                        Element = "Slash",
                        Action  = "Weapon",
                    });
                    _host.WriteToChat($"[RynthAi] Added: {wo.Name} (0x{(uint)wo.Id:X8})", 1);
                }
            }
        }

        if (!canAdd) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("(Click a weapon in inventory first)");

        ImGui.End();
    }

    private static bool IsWeapon(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.MeleeWeapon   ||
        wo.ObjectClass == AcObjectClass.MissileWeapon ||
        wo.ObjectClass == AcObjectClass.WandStaffOrb;
}

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

    private static readonly string[] ConsumableTypes =
        { "General", "Lockpick", "HealthKit", "ManaStone", "Stamina" };

    public LegacyWeaponsUi(LegacyUiSettings settings, RynthCoreHost host)
    {
        _settings = settings;
        _host = host;
    }

    public void SetWorldFilter(WorldObjectCache cache) => _worldFilter = cache;

    public void Render()
    {
        if (!DashWindows.ShowWeapons) return;

        ImGui.SetNextWindowSize(new Vector2(480, 440), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Items##RynthAiWeapons", ref DashWindows.ShowWeapons))
        {
            ImGui.End();
            return;
        }

        // ── Weapons Section ─────────────────────────────────────────────────
        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Weapons");
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
                if (ImGui.SmallButton($"Del##w{i}"))
                {
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

        bool canAdd = _host.HasGetSelectedItemId;
        if (!canAdd) ImGui.BeginDisabled();

        if (ImGui.Button("Add Selected Weapon", new Vector2(160, 24)))
        {
            AddSelectedWeapon();
        }

        if (!canAdd) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("(Click a weapon in inventory first)");

        // ── Consumable Items Section ────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Consumable Items");
        ImGui.Spacing();

        if (ImGui.BeginTable("ConsumablesTable", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Item Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Type",      ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("",          ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            for (int i = 0; i < _settings.ConsumableRules.Count; i++)
            {
                var rule = _settings.ConsumableRules[i];
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
                        ImGui.SetTooltip("Item not in inventory.\nRemove and re-add the correct item.");
                }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                int typeIdx = Array.IndexOf(ConsumableTypes, rule.Type);
                if (typeIdx < 0) typeIdx = 0;
                if (ImGui.Combo($"##CType{i}", ref typeIdx, ConsumableTypes, ConsumableTypes.Length))
                    rule.Type = ConsumableTypes[typeIdx];

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"Del##c{i}"))
                {
                    _settings.ConsumableRules.RemoveAt(i);
                    ImGui.EndTable();
                    ImGui.End();
                    return;
                }
            }
            ImGui.EndTable();
        }

        if (!canAdd) ImGui.BeginDisabled();

        if (ImGui.Button("Add Selected Consumable", new Vector2(180, 24)))
        {
            AddSelectedConsumable();
        }

        if (!canAdd) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled("(Click an item in inventory first)");

        // ── Mana Stone Tapping ──────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(LegacyDashboardRenderer.ColAmber, "Mana Stone Tapping");
        ImGui.Spacing();
        ImGui.Text("Keep up to:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70);
        ImGui.DragInt("##StoneKeep", ref _settings.ManaStoneKeepCount, 0.2f, 1, 999);
        if (_settings.ManaStoneKeepCount < 1)  _settings.ManaStoneKeepCount = 1;
        if (_settings.ManaStoneKeepCount > 999) _settings.ManaStoneKeepCount = 999;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum number of mana stones to keep in inventory.\nStones on corpses are skipped once this count is reached.");
        ImGui.SameLine(0, 16);
        ImGui.Checkbox("Enable Tapping", ref _settings.EnableManaTapping);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Use mana stones on inventory items with high MaxMana,\nthen apply the charged stone to the player to recharge gear.");
        if (_settings.EnableManaTapping)
        {
            ImGui.SameLine(0, 16);
            ImGui.Text("Min MaxMana:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            ImGui.DragInt("##TapThresh", ref _settings.ManaTapMinMana, 50f, 100, 99999);
            if (_settings.ManaTapMinMana < 100)  _settings.ManaTapMinMana = 100;
            if (_settings.ManaTapMinMana > 99999) _settings.ManaTapMinMana = 99999;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Only tap items whose MaxMana is at or above this value.");
        }

        ImGui.End();
    }

    private void AddSelectedWeapon()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click a weapon in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (!IsWeapon(wo))
            _host.WriteToChat("[RynthAi] Selected item is not a weapon or wand.", 1);
        else if (_settings.ItemRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
        else
        {
            string element = DetectWeaponElement(wo);
            _settings.ItemRules.Add(new ItemRule
            {
                Id      = wo.Id,
                Name    = wo.Name,
                Element = element,
                Action  = "Weapon",
            });
            _host.WriteToChat($"[RynthAi] Added weapon: {wo.Name} (0x{(uint)wo.Id:X8}) [{element}]", 1);
        }
    }

    private void AddSelectedConsumable()
    {
        uint selId = _host.GetSelectedItemId();
        if (selId == 0)
        {
            _host.WriteToChat("[RynthAi] No item selected — click an item in your inventory first.", 1);
            return;
        }
        if (_worldFilter == null) { _host.WriteToChat("[RynthAi] Object cache not ready.", 1); return; }

        var wo = _worldFilter[(int)selId];
        if (wo == null)
            _host.WriteToChat($"[RynthAi] Item 0x{selId:X8} not in cache — try again.", 1);
        else if (_settings.ConsumableRules.Any(x => x.Id == wo.Id))
            _host.WriteToChat($"[RynthAi] {wo.Name} is already in the list.", 1);
        else
        {
            string type = DetectConsumableType(wo);
            _settings.ConsumableRules.Add(new ConsumableRule
            {
                Id   = wo.Id,
                Name = wo.Name,
                Type = type,
            });
            _host.WriteToChat($"[RynthAi] Added consumable: {wo.Name} (0x{(uint)wo.Id:X8}) [{type}]", 1);
        }
    }

    private static bool IsWeapon(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.MeleeWeapon   ||
        wo.ObjectClass == AcObjectClass.MissileWeapon ||
        wo.ObjectClass == AcObjectClass.WandStaffOrb;

    /// <summary>Auto-detect weapon element from the DamageType int property.</summary>
    private static string DetectWeaponElement(WorldObject wo)
    {
        // DamageType (STypeInt 45) — AC damage type flags
        int dt = wo.Values(LongValueKey.DamageType, 0);
        if (dt == 0) return "Slash"; // no data — default

        // Check flags in order of specificity (elemental first, then physical)
        if ((dt & 1024) != 0) return "Nether";
        if ((dt & 16)   != 0) return "Fire";
        if ((dt & 8)    != 0) return "Cold";
        if ((dt & 64)   != 0) return "Lightning";
        if ((dt & 32)   != 0) return "Acid";
        if ((dt & 1)    != 0) return "Slash";
        if ((dt & 2)    != 0) return "Pierce";
        if ((dt & 4)    != 0) return "Bludgeon";

        return "Slash";
    }

    /// <summary>Auto-detect consumable type from item name.</summary>
    private static string DetectConsumableType(WorldObject wo)
    {
        string name = wo.Name;
        if (string.IsNullOrEmpty(name)) return "General";

        // Health items
        if (name.Contains("Heal", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Health", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Elixir of Healing", StringComparison.OrdinalIgnoreCase))
            return "HealthKit";

        // Mana items
        if (name.Contains("Mana", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mana Stone", StringComparison.OrdinalIgnoreCase))
            return "ManaStone";

        // Stamina items
        if (name.Contains("Stamina", StringComparison.OrdinalIgnoreCase))
            return "Stamina";

        // Lockpicks
        if (name.Contains("Lockpick", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Lock Pick", StringComparison.OrdinalIgnoreCase))
            return "Lockpick";

        return "General";
    }
}

using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyAdvancedSettingsUi
{
    private readonly LegacyUiSettings _settings;
    private MissileCraftingManager? _missileCraftingManager;

    private static readonly string[] AttackHeights = { "Low", "Medium", "High" };
    private static readonly string[] LootOwnershipModes = { "My Kills Only", "Fellowship Kills", "All Corpses" };
    private static readonly string[] MovementModes = { "Legacy (Autorun)", "Tier 1 (CM_Movement)", "Tier 2 (MoveToPosition)" };

    public LegacyAdvancedSettingsUi(LegacyUiSettings settings)
    {
        _settings = settings;
    }

    public void SetMissileCraftingManager(MissileCraftingManager mgr) => _missileCraftingManager = mgr;

    public void Render()
    {
        if (!_settings.ShowAdvancedWindow)
            return;

        ImGui.SetNextWindowSize(new Vector2(600, 400), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("RynthAi Advanced Settings", ref _settings.ShowAdvancedWindow))
        {
            ImGui.BeginChild("AdvancedSidebar", new Vector2(150, 0), ImGuiChildFlags.Borders);
            for (int i = 0; i < _settings.AdvancedTabs.Length; i++)
            {
                if (ImGui.Selectable(_settings.AdvancedTabs[i], _settings.SelectedAdvancedTab == i))
                    _settings.SelectedAdvancedTab = i;
            }
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginGroup();
            ImGui.BeginChild("AdvancedContent", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), ImGuiChildFlags.Borders);
            RenderTabContent(_settings.SelectedAdvancedTab);
            ImGui.EndChild();

            if (ImGui.Button("Close"))
                _settings.ShowAdvancedWindow = false;

            ImGui.EndGroup();
        }

        ImGui.End();
    }

    private void RenderTabContent(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _settings.AdvancedTabs.Length)
            return;

        string tabName = _settings.AdvancedTabs[tabIndex];
        ImGui.TextColored(new Vector4(0.4f, 0.7f, 1.0f, 1.0f), $"Advanced Settings > {tabName}");
        ImGui.Separator();
        ImGui.Spacing();

        switch (tabName)
        {
            case "Display":
                ImGui.Checkbox("Show Target Stamina / Mana", ref _settings.ShowTargetStaminaMana);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When enabled, displays stamina and mana bars\nfor the selected target (requires appraisal data).");
                break;

            case "Misc":
                if (ImGui.Checkbox("Enable FPS Limit", ref _settings.EnableFPSLimit)) { }
                if (_settings.EnableFPSLimit)
                {
                    ImGui.Indent();
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderInt("Focused FPS", ref _settings.TargetFPSFocused, 10, 240);
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderInt("Background FPS", ref _settings.TargetFPSBackground, 5, 60);
                    ImGui.Unindent();
                }

                ImGui.Separator();
                ImGui.Spacing();

                ImGui.Checkbox("Auto Cram", ref _settings.EnableAutocram);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Automatically moves items from your main pack into side packs.\nNote: recently used weapons stay in the main pack.");
                }

                ImGui.Spacing();
                ImGui.Checkbox("Peace Mode When Idle", ref _settings.PeaceModeWhenIdle);
                ImGui.Checkbox("Enable Raycasting", ref _settings.EnableRaycasting);

                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Text("Monster Blacklist");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Attempts Before Blacklist", ref _settings.BlacklistAttempts, 1, 20);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("How many failed attack attempts on a mob before it gets blacklisted.");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Blacklist Timeout (sec)", ref _settings.BlacklistTimeoutSec, 5, 120);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("How long a blacklisted mob is ignored before re-trying.");
                break;

            case "Recharge":
                ImGui.Text("Self Vitals (%)");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Heal At", ref _settings.HealAt, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Re-stam At", ref _settings.RestamAt, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Get Mana At", ref _settings.GetManaAt, 0, 100);

                ImGui.Spacing();
                ImGui.Text("Top-Off Vitals (%)");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Top HP", ref _settings.TopOffHP, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Top Stam", ref _settings.TopOffStam, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Top Mana", ref _settings.TopOffMana, 0, 100);

                ImGui.Spacing();
                ImGui.Text("Helper Settings (%)");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Heal Others", ref _settings.HealOthersAt, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Re-stam Others", ref _settings.RestamOthersAt, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Infuse Others", ref _settings.InfuseOthersAt, 0, 100);
                break;

            case "Melee Combat":
                ImGui.Text("Attack Power & Height");
                ImGui.Separator();
                ImGui.Checkbox("Use Recklessness", ref _settings.UseRecklessness);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When enabled and Recklessness is trained, auto power uses 80% instead of 100%.");

                ImGui.Spacing();
                bool meleeAuto = _settings.MeleeAttackPower < 0;
                if (ImGui.Checkbox("Melee Auto Power", ref meleeAuto))
                    _settings.MeleeAttackPower = meleeAuto ? -1 : 100;
                ImGui.Indent();
                ImGui.SetNextItemWidth(150);
                if (meleeAuto)
                {
                    int displayVal = 100;
                    ImGui.BeginDisabled();
                    ImGui.SliderInt("Melee Power %", ref displayVal, 0, 100);
                    ImGui.EndDisabled();
                }
                else
                {
                    ImGui.SliderInt("Melee Power %", ref _settings.MeleeAttackPower, 0, 100);
                }
                ImGui.Unindent();

                ImGui.SetNextItemWidth(120);
                ImGui.Combo("Melee Attack Height", ref _settings.MeleeAttackHeight, AttackHeights, AttackHeights.Length);

                ImGui.Spacing();
                bool missileAuto = _settings.MissileAttackPower < 0;
                if (ImGui.Checkbox("Missile Auto Power", ref missileAuto))
                    _settings.MissileAttackPower = missileAuto ? -1 : 100;
                ImGui.Indent();
                ImGui.SetNextItemWidth(150);
                if (missileAuto)
                {
                    int displayVal = 100;
                    ImGui.BeginDisabled();
                    ImGui.SliderInt("Missile Power %", ref displayVal, 0, 100);
                    ImGui.EndDisabled();
                }
                else
                {
                    ImGui.SliderInt("Missile Power %", ref _settings.MissileAttackPower, 0, 100);
                }
                ImGui.Unindent();

                ImGui.SetNextItemWidth(120);
                ImGui.Combo("Missile Attack Height", ref _settings.MissileAttackHeight, AttackHeights, AttackHeights.Length);

                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Checkbox("Use Native Attack", ref _settings.UseNativeAttack);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Uses the client's combat pipeline for attacks.\nThe client handles turn-to-face naturally (no backwards arrows).");

                ImGui.Spacing();
                ImGui.Checkbox("Summon Pets", ref _settings.SummonPets);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Pet Min Monsters", ref _settings.PetMinMonsters);
                break;

            case "Spell Combat":
                ImGui.Text("War/Void Casting Settings");
                ImGui.Separator();
                ImGui.Checkbox("Cast Dispel Self", ref _settings.CastDispelSelf);

                ImGui.Spacing();
                ImGui.Text("Ring Spell Override");
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Min Ring Targets", ref _settings.MinRingTargets);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("If this many monsters are within ring range, ring spells are used instead of arc/bolt/streak.");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Spell Difficulty (Min Buffed Skill)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Minimum buffed skill level required to cast each spell tier.\nRaise these above AC's hard minimums (1/50/100/150/200/250/300/350) to avoid fizzles.");

                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 1", ref _settings.MinSkillLevelTier1);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 2", ref _settings.MinSkillLevelTier2);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 3", ref _settings.MinSkillLevelTier3);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 4", ref _settings.MinSkillLevelTier4);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 5", ref _settings.MinSkillLevelTier5);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 6", ref _settings.MinSkillLevelTier6);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 7", ref _settings.MinSkillLevelTier7);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 8", ref _settings.MinSkillLevelTier8);
                break;

            case "Ranges":
                ImGui.Text("Standard Ranges (Yards)");
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Monster Range", ref _settings.MonsterRange);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Ring Range", ref _settings.RingRange);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Approach Range", ref _settings.ApproachRange);

                ImGui.Spacing();
                ImGui.Text("Corpse Acquisition (Yards)");
                ImGui.Separator();
                ImGui.SetNextItemWidth(180);
                ImGui.InputDouble("Corpse Max (yd)", ref _settings.CorpseApproachRangeMax, 0.5, 1.0, "%.1f");
                ImGui.SetNextItemWidth(180);
                ImGui.InputDouble("Corpse Min (yd)", ref _settings.CorpseApproachRangeMin, 0.5, 1.0, "%.1f");
                break;

            case "Navigation":
                ImGui.Checkbox("Boost Nav Priority", ref _settings.BoostNavPriority);
                ImGui.SetNextItemWidth(120);
                ImGui.InputFloat("Follow/Nav Min", ref _settings.FollowNavMin, 0.1f, 1.0f, "%.1f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Arrival distance in yards. Also sets the nav marker ring radius.");

                ImGui.Spacing();
                ImGui.Text("Nav Marker Display");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Ring Thickness", ref _settings.NavRingThickness, 1.0f, 16.0f, "%.0f");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Line Thickness", ref _settings.NavLineThickness, 1.0f, 16.0f, "%.0f");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Height Offset", ref _settings.NavHeightOffset, -5.0f, 5.0f, "%.2f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Vertical offset for nav markers above the ground. Negative = lower.");

                ImGui.Checkbox("Show Terrain Passability", ref _settings.ShowTerrainPassability);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Highlight impassable terrain triangles in red.");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Doors");

                ImGui.Checkbox("Open Doors While Navigating", ref _settings.OpenDoors);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Automatically open doors encountered during navigation.");

                if (_settings.OpenDoors)
                {
                    ImGui.SetNextItemWidth(180);
                    ImGui.SliderFloat("Door Detection Range (yd)", ref _settings.OpenDoorRange, 0.1f, 70.0f, "%.1f");

                    ImGui.Checkbox("Auto-Unlock Doors", ref _settings.AutoUnlockDoors);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("If a door is locked, try to use a lockpick from your Consumable Items list.");
                }

                ImGui.Spacing();
                ImGui.Text("Movement Engine");
                ImGui.SetNextItemWidth(200);
                ImGui.Combo("Mode", ref _settings.MovementMode, MovementModes, MovementModes.Length);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(
                        "Legacy: SetAutorun + TurnLeft/TurnRight\n" +
                        "Tier 1: Direct CM_Movement server events\n" +
                        "Tier 2: Client physics MoveToPosition");
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Steering");

                ImGui.SetNextItemWidth(80);
                ImGui.InputFloat("Stop & Turn Angle", ref _settings.NavStopTurnAngle, 1f, 5f, "%.0f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Stop forward motion and turn in place when heading error exceeds this.");

                ImGui.SetNextItemWidth(80);
                ImGui.InputFloat("Resume Run Angle", ref _settings.NavResumeTurnAngle, 1f, 5f, "%.0f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Resume running once the turn-in-place error drops below this.");

                ImGui.SetNextItemWidth(80);
                ImGui.InputFloat("Dead Zone", ref _settings.NavDeadZone, 0.5f, 1f, "%.1f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Ignore heading corrections smaller than this.");

                ImGui.SetNextItemWidth(80);
                ImGui.InputFloat("Sweep Detect Mult", ref _settings.NavSweepMult, 0.5f, 1f, "%.1f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Closest-approach detection radius multiplier.");

                ImGui.SetNextItemWidth(80);
                ImGui.InputFloat("Post-Portal Delay (s)", ref _settings.PostPortalDelaySec, 0.25f, 1f, "%.2f");
                if (_settings.PostPortalDelaySec < 0f) _settings.PostPortalDelaySec = 0f;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Seconds to settle after any portal/recall teleport before nav resumes.");

                if (_settings.MovementMode == 2)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Text("Tier 2 Tuning");

                    ImGui.SetNextItemWidth(100);
                    ImGui.InputFloat("Speed##t2", ref _settings.T2Speed, 0.1f, 0.5f, "%.1f");
                    ImGui.SetNextItemWidth(100);
                    ImGui.InputFloat("Walk Within (yd)", ref _settings.T2WalkWithinYd, 1f, 5f, "%.0f");
                    ImGui.SetNextItemWidth(100);
                    ImGui.InputFloat("Stop Distance (yd)", ref _settings.T2DistanceTo, 0.1f, 0.5f, "%.1f");
                    ImGui.SetNextItemWidth(100);
                    ImGui.InputFloat("Reissue Timeout (ms)", ref _settings.T2ReissueMs, 100f, 500f, "%.0f");
                    ImGui.SetNextItemWidth(100);
                    ImGui.InputFloat("Max Range (yd)", ref _settings.T2MaxRangeYd, 50f, 100f, "%.0f");
                    ImGui.SetNextItemWidth(100);
                    ImGui.InputInt("Max Landblock Dist", ref _settings.T2MaxLandblocks, 1);
                }
                break;

            case "Buffing":
                ImGui.Checkbox("Enable Buffing", ref _settings.EnableBuffing);
                ImGui.Checkbox("Rebuff When Idle", ref _settings.RebuffWhenIdle);
                break;

            case "Looting":
                ImGui.Checkbox("Enable Looting", ref _settings.EnableLooting);
                ImGui.Checkbox("Boost Loot Priority", ref _settings.BoostLootPriority);
                ImGui.Checkbox("Loot Only Rare Corpses", ref _settings.LootOnlyRareCorpses);

                ImGui.Spacing();
                ImGui.Text("Corpse Ownership");
                ImGui.SetNextItemWidth(180);
                ImGui.Combo("Loot From", ref _settings.LootOwnership, LootOwnershipModes, LootOwnershipModes.Length);

                ImGui.Spacing();
                ImGui.Text("Inventory Management");
                ImGui.Checkbox("Enable Autostack", ref _settings.EnableAutostack);
                ImGui.Checkbox("Enable Autocram", ref _settings.EnableAutocram);
                ImGui.Checkbox("Combine Salvage Bags", ref _settings.EnableCombineSalvage);

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Loot Timers (ms)");
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Inter-Item Delay", ref _settings.LootInterItemDelayMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Content Settle", ref _settings.LootContentSettleMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Empty Corpse Wait", ref _settings.LootEmptyCorpseMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Closing Delay", ref _settings.LootClosingDelayMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Assess Window", ref _settings.LootAssessWindowMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Loot Retry Timeout", ref _settings.LootRetryTimeoutMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Corpse Open Retry", ref _settings.LootOpenRetryMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Corpse Timeout", ref _settings.LootCorpseTimeoutMs);

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Salvage Timers (ms) - First / Fast");
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Open (First)", ref _settings.SalvageOpenDelayFirstMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Open (Fast)", ref _settings.SalvageOpenDelayFastMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Add Item (First)", ref _settings.SalvageAddDelayFirstMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Add Item (Fast)", ref _settings.SalvageAddDelayFastMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Salvage Click", ref _settings.SalvageSalvageDelayMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Result (First)", ref _settings.SalvageResultDelayFirstMs);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Result (Fast)", ref _settings.SalvageResultDelayFastMs);
                break;

            case "Crafting":
                ImGui.Text("Missile Ammo Crafting");
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Checkbox("Enable Missile Crafting", ref _settings.EnableMissileCrafting);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Auto-manage missile ammo when the ammo slot is empty or low.");
                }

                if (!_settings.EnableMissileCrafting)
                {
                    ImGui.TextDisabled("(Disabled)");
                    break;
                }

                ImGui.Spacing();
                if (_missileCraftingManager != null)
                {
                    string stateLabel = _missileCraftingManager.State.ToString();
                    Vector4 stateColor = _missileCraftingManager.IsCrafting
                        ? new Vector4(0.9f, 0.7f, 0.2f, 1.0f)
                        : new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
                    ImGui.Text("State:");
                    ImGui.SameLine();
                    ImGui.TextColored(stateColor, stateLabel);

                    if (!string.IsNullOrEmpty(_missileCraftingManager.StatusMessage))
                        ImGui.TextWrapped(_missileCraftingManager.StatusMessage);
                }
                break;

            default:
                ImGui.Text($"Settings for {tabName} are currently under development.");
                break;
        }
    }
}

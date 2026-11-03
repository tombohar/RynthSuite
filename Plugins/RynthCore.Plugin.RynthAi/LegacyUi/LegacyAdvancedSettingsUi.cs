using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyAdvancedSettingsUi
{
    private readonly LegacyUiSettings _settings;
    private MissileCraftingManager? _missileCraftingManager;

    // Scratch buffer for the "never attack" name entry box.
    private string _newBlacklistName = "";
    // Server Features list being edited (committed on Enter / focus loss); null = show the saved list.

    private static readonly string[] AttackHeights = { "Low", "Medium", "High" };

    /// <summary>
    /// The environment-impact throttle's shared numbers (one set for missiles and magic), drawn
    /// under each response combo. <paramref name="id"/> keeps the two copies' ImGui ids apart.
    /// </summary>
    private void DrawEnvironmentThrottleKnobs(string id)
    {
        ImGui.Indent();
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt($"Hits to trip##envlimit{id}", ref _settings.EnvironmentImpactLimit, 1, 10);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Environment hits on one target, within the window, before combat reacts.\nShared by missiles and magic.");
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt($"Window (s)##envwin{id}", ref _settings.EnvironmentImpactWindowSec, 5, 120);
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt($"Move closer by (m)##envstep{id}", ref _settings.EnvironmentApproachMeters, 1, 30);
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt($"Closest (m)##envmin{id}", ref _settings.EnvironmentMinDistance, 1, 30);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Never moves nearer than this. Already this close, a new target is picked instead.");
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt($"Skip target (s)##envskip{id}", ref _settings.EnvironmentSkipSec, 5, 300);
        ImGui.Unindent();
    }
    private static readonly string[] NavLabelModeNames = { "Off", "Numbers", "Numbers and names" };
    private const string NavResumeNearestTip =
        "On: starting the macro, loading a Circular or Linear route, and carrying on after a fight, loot, buff or door\n" +
        "that moved you 10 yd or more all head for the closest waypoint on the route. A waypoint you have already\n" +
        "walked past is skipped for the one after it, so nav doesn't turn back. Once routes only look ahead.\n" +
        "Off: nav carries on to the waypoint it was heading for, and a loaded route starts at its first point.";
    private static readonly string[] TargetSelectionNames = { "Lowest health", "Closest", "Farthest", "Least turning" };
    private static readonly string[] LootOwnershipModes = { "My Kills Only", "Fellowship Kills", "All Corpses" };

    public LegacyAdvancedSettingsUi(LegacyUiSettings settings)
    {
        _settings = settings;
    }

    public void SetMissileCraftingManager(MissileCraftingManager mgr) => _missileCraftingManager = mgr;

    /// <summary>The loot profile files ("None" first) and picking one: Settings > Looting's Loot Profile.</summary>
    public Func<string[]>? LootProfiles { get; init; }
    public Action<int>? SelectLootProfile { get; init; }

    private Func<string>? _autoVendorStatus;
    public void SetAutoVendorStatusProvider(Func<string> status) => _autoVendorStatus = status;

    // Buffing page: the Force Buff button's actions and the list of buffs the unusable-buff
    // filter is leaving out (wired by RynthAiPlugin once the BuffManager exists).
    private Action? _forceBuff, _cancelBuff;
    private Func<string>? _buffSkips;
    public void SetBuffActions(Action? force, Action? cancel, Func<string>? skips)
    {
        _forceBuff = force; _cancelBuff = cancel; _buffSkips = skips;
    }

    private Func<string>? _autoTradeStatus;
    public void SetAutoTradeStatusProvider(Func<string> status) => _autoTradeStatus = status;
    private string _newAutoAcceptPattern = "";

    public string MissileCraftingState  => _missileCraftingManager?.State.ToString() ?? string.Empty;
    public bool   MissileCraftingActive => _missileCraftingManager?.IsCrafting ?? false;
    public string MissileCraftingStatus => _missileCraftingManager?.StatusMessage ?? string.Empty;

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

    private void RenderVendoring()
    {
        ImGui.Text("AutoVendor (UtilityBelt)");
        ImGui.Separator();
        ImGui.Spacing();

        string status = _autoVendorStatus?.Invoke() ?? "Not logged in";
        SafeImGui.TextDisabled($"Status: {status}");
        ImGui.Spacing();

        ImGui.Checkbox("Enabled##AV", ref _settings.AutoVendorEnabled);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Buy and sell by a loot profile when a vendor opens (and allow /ub autovendor).\n" +
                             "Profiles: <Vendor Name>.utl or default.utl in your character's AutoVendor\n" +
                             "folder, the server folder, or " + Vendor.AutoVendorManager.MainProfileDir + ".");
        ImGui.Checkbox("Test Mode (only print what it would do)##AV", ref _settings.AutoVendorTestMode);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lists what would be bought and sold without trading. Leave this on until\n" +
                             "the list looks right: selling can't be undone.");
        ImGui.Checkbox("Buy##AV", ref _settings.AutoVendorEnableBuying);
        ImGui.SameLine();
        ImGui.Checkbox("Sell##AV", ref _settings.AutoVendorEnableSelling);
        ImGui.Checkbox("Only Sell From Main Pack##AV", ref _settings.AutoVendorOnlyFromMainPack);
        ImGui.Checkbox("Show Merchant Info##AV", ref _settings.AutoVendorShowMerchantInfo);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Print the vendor's buy/sell rates and max value when it opens.");
        ImGui.Checkbox("Think When Finished##AV", ref _settings.AutoVendorThink);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Send 'AutoVendor finished: <vendor>' (and failures) as a /tell to yourself,\n" +
                             "so a meta's chat condition can wait for it.");

        ImGui.Spacing();
        ImGui.Text("/ub vendor open");
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Tries##AV", ref _settings.AutoVendorTries))
            _settings.AutoVendorTries = Math.Clamp(_settings.AutoVendorTries, 1, 20);
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Time Between Tries (ms)##AV", ref _settings.AutoVendorTriesTime, 250))
            _settings.AutoVendorTriesTime = Math.Clamp(_settings.AutoVendorTriesTime, 500, 30000);

        ImGui.Spacing();
        ImGui.TextDisabled("Never sold: equipped, attuned, bonded, retained, tinkered, imbued,");
        ImGui.TextDisabled("inscribed, rare, zero value, packs, or anything a Keep rule could match.");

        RenderAutoTrade();
    }

    private void RenderAutoTrade()
    {
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Text("AutoTrade (UtilityBelt)");
        ImGui.Separator();
        ImGui.Spacing();

        string status = _autoTradeStatus?.Invoke() ?? "Not logged in";
        SafeImGui.TextDisabled($"Status: {status}");
        ImGui.Spacing();

        ImGui.Checkbox("Enabled##AT", ref _settings.AutoTradeEnabled);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("When a trade window opens, add the items that match Keep / Keep # rules in\n" +
                             "<Partner Name>.utl or default.utl (your character's AutoTrade folder, the\n" +
                             "server folder, or " + Trade.AutoTradeManager.MainProfileDir + ").");
        ImGui.Checkbox("Test Mode (only print what it would add)##AT", ref _settings.AutoTradeTestMode);
        ImGui.Checkbox("Only Trade From Main Pack##AT", ref _settings.AutoTradeOnlyFromMainPack);
        ImGui.Checkbox("Auto Accept After Adding##AT", ref _settings.AutoTradeAutoAccept);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Accept the trade once every item AutoTrade added shows in the window.\n" +
                             "Check what the other player offers first: nothing checks their side.");
        ImGui.Checkbox("Think When Finished##AT", ref _settings.AutoTradeThink);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Send 'AutoTrade finished: <partner>' and 'Trade accepted: <partner>' as a /tell\n" +
                             "to yourself, so a meta's chat condition can wait for it.");

        ImGui.Spacing();
        ImGui.Text("Auto-accept trades from (name patterns, .NET regex)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When one of these players accepts a trade, this character accepts too.\n" +
                             "The autoAcceptList.json files from /ub autotrade autoaccept add[g|s] count as well.");
        var list = _settings.AutoTradeAutoAcceptChars;
        int removeAt = -1;
        for (int i = 0; i < list.Count; i++)
        {
            ImGui.Bullet();
            ImGui.SameLine();
            ImGui.TextUnformatted(list[i]);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##ATaac{i}"))
                removeAt = i;
        }
        if (removeAt >= 0)
            list.RemoveAt(removeAt);
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("##ATnewpattern", ref _newAutoAcceptPattern, 128);
        ImGui.SameLine();
        if (ImGui.Button("Add##ATaddpattern"))
        {
            string p = _newAutoAcceptPattern.Trim();
            if (p.Length > 0 && !list.Contains(p) && IsValidRegex(p))
            {
                list.Add(p);
                _newAutoAcceptPattern = "";
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Never added: equipped, attuned, retained, tinkered, imbued, inscribed,");
        ImGui.TextDisabled("packs, unidentified items, or items on RynthAi's weapon/consumable lists.");
    }

    private static bool IsValidRegex(string pattern)
    {
        try { _ = System.Text.RegularExpressions.Regex.Match(string.Empty, pattern); return true; }
        catch { return false; }
    }

    /// <summary>One cell of the Protections and Banes grid: ticked = cast it.</summary>
    private void BuffToggle((string Buff, string Label) toggle)
    {
        bool on = !_settings.IsBuffDisabled(toggle.Buff);
        if (ImGui.Checkbox($"{toggle.Label}##buff_{toggle.Buff}", ref on))
            _settings.SetBuffEnabled(toggle.Buff, on);
    }

    /// <summary>
    /// One loot timer: the input, then "baseline" when it matches LootBaseline, or "baseline N" when
    /// it doesn't (hover for what the timer does).
    /// </summary>
    private static void LootTimerInput(string label, ref int value, int baseline, string tip)
    {
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt(label, ref value);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip(tip);
        ImGui.SameLine();
        if (value == baseline)
            ImGui.TextDisabled("baseline");
        else
            SafeImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), $"baseline {baseline}");
    }

    /// <summary>Writes the six loot timers' baseline values (LootBaseline.cs) into the settings.</summary>
    private void ApplyLootBaseline()
    {
        LootBaseline.Timers b = LootBaseline.Values;
        _settings.LootContentSettleMs = b.ContentSettleMs;
        _settings.LootEmptyCorpseMs   = b.EmptyCorpseMs;
        _settings.LootAssessWindowMs  = b.AssessWindowMs;
        _settings.LootRetryTimeoutMs  = b.RetryTimeoutMs;
        _settings.LootOpenRetryMs     = b.OpenRetryMs;
        _settings.LootCorpseTimeoutMs = b.CorpseTimeoutMs;
    }

    private void RenderTabContent(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _settings.AdvancedTabs.Length)
            return;

        string tabName = _settings.AdvancedTabs[tabIndex];
        SafeImGui.TextColored(new Vector4(0.4f, 0.7f, 1.0f, 1.0f), $"Advanced Settings > {tabName}");
        ImGui.Separator();
        ImGui.Spacing();

        switch (tabName)
        {
            case "Display":
                ImGui.Checkbox("Show Target Stamina / Mana", ref _settings.ShowTargetStaminaMana);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When enabled, displays stamina and mana bars\nfor the selected target (requires appraisal data).");
                break;

            case "UI":
                ImGui.Text("Radar");
                ImGui.Separator();
                ImGui.Checkbox("Hide retail radar", ref _settings.SuppressRetailRadar);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Suppress the game's built-in radar (bezel, compass, coords, blips).");

                ImGui.Checkbox("Show RynthRadar", ref _settings.ShowRynthRadar);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Render the custom square radar widget (indoor walls + dot markers, works outdoors).");

                ImGui.Checkbox("Radar Click-Through", ref _settings.RadarClickThrough);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Mouse events over the radar pass through to the game instead of the widget.");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Chat");
                ImGui.Checkbox("Show RynthChat", ref _settings.ShowRynthChat);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Render the custom chat viewer (scrollback + channel coloring).");

                ImGui.Checkbox("Chat Click-Through", ref _settings.ChatClickThrough);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Mouse events over the chat pass through to the game. The gear button stays clickable.");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Power Bar");
                ImGui.Checkbox("Hide retail power bar", ref _settings.SuppressRetailPowerbar);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Hide the vanilla attack/magic power bar that appears under the cursor while charging.\nThe bar's underlying combat state still works — only the on-screen widget is hidden.");
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
                ImGui.Checkbox("Weapons Back After Heals", ref _settings.RearmAfterVitals);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("After a heal, restam or remana spell with nothing to fight, put the last fight's weapons (main hand and off hand) back on.");
                ImGui.Checkbox("Fast Weapon Swap", ref _settings.FastWeaponSwap);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Switch weapons through peace mode, every step the moment the server allows:\nthe wand is in hand about 0.2 s after the switch begins (4-7 s without),\nand the weapons go back as fast, also when a monster comes while the wand\nis out (swinging again in about 1 s). On by default. Peace mode costs your weapon's defense\nfor about half a second; the slow way goes without a weapon for about 3 s.");
                ImGui.Checkbox("Start Macro On Login", ref _settings.StartMacroOnLogin);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Automatically starts the macro when RynthAi loads.");
                ImGui.Checkbox("Patrol On Login", ref _settings.PatrolOnLogin);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Automatically starts dungeon patrol when RynthAi loads.");
                ImGui.Checkbox("Escape Acid and Lava", ref _settings.EscapeHazards);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When acid or lava hurts you, stop everything (combat, buffs, healing, looting)\nand walk back out the way you came. The spot is remembered so the patrol avoids it.\nTurn off for a character that walks through acid on purpose.");
                ImGui.Checkbox("Enable Raycasting", ref _settings.EnableRaycasting);


                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Text("Missile Arc Velocities (m/s)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Per-weapon launch speed used for line-of-sight arc checks.\n" +
                                     "Lower velocity = higher arc. Tune until LoS matches in-game hits\n" +
                                     "(/ra lostest bow on a selected mob shows the arc and where it hits).\n" +
                                     "Indoors the arc is checked against ceilings too.");
                ImGui.Checkbox("Use Arcs for Missile LoS", ref _settings.UseArcs);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("On: a missile target must pass the straight line AND the arc the\n" +
                                     "missile flies (checked against ceilings in dungeons).\n" +
                                     "Off: all missile LoS checks are a straight line (eye-to-eye).");
                if (_settings.UseArcs)
                {
                    ImGui.Indent();
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat("Bow",      ref _settings.BowArcVelocity,      10.0f, 60.0f, "%.1f");
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat("Crossbow", ref _settings.CrossbowArcVelocity, 10.0f, 80.0f, "%.1f");
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat("Atlatl",   ref _settings.AtlatlArcVelocity,   10.0f, 60.0f, "%.1f");
                    ImGui.SetNextItemWidth(150);
                    ImGui.SliderFloat("Arc Clearance (m)", ref _settings.MissileArcClearance, 0.0f, 3.0f, "%.1f");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Extra headroom the arc must have at mid-flight. Raise it if shots\n" +
                                         "still hit ceilings; lower it if mobs the arrows can reach are skipped.\n" +
                                         "Default 0.5.");
                    ImGui.Unindent();
                }
                ImGui.SetNextItemWidth(150);
                ImGui.SliderFloat("Magic Arc", ref _settings.MagicArcVelocity, 10.0f, 60.0f, "%.1f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Arc spells' horizontal speed (m/s). ACE: 40. Lower = higher arc.\n" +
                                     "A rule with Arc on casts an arc only when this path reaches the target\n" +
                                     "(walls, and ceilings in dungeons); otherwise its other shape, or a bolt.\n" +
                                     "Needs Enable Raycasting. /ra lostest magic shows the arc to the selected mob.");
                ImGui.Checkbox("LoS Debug Log", ref _settings.LosDebugLog);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Log each in-range mob's LoS verdict ([LOS] lines: straight line or arc,\n" +
                                     "how high the arc rises, where it hits, distance and cells). For tuning.");

                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Text("Monster Blacklist");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Casts Before Blacklist", ref _settings.BlacklistAttempts, 1, 20);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("How many offensive casts that deal NO damage (each judged after the settle window) before a mob is blacklisted.");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Blacklist Timeout (sec)", ref _settings.BlacklistTimeoutSec, 5, 120);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("How long a blacklisted mob is ignored before re-trying.");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Cast Settle (ms)", ref _settings.BlacklistCastSettleMs, 250, 15000);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Grace after a cast before it counts as a no-damage miss — lets the damage/health packet arrive so good targets aren't blacklisted prematurely.");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("No Progress Timeout (sec)", ref _settings.TargetNoProgressTimeoutSec, 0, 300);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Blacklist a target after being engaged this many seconds without dealing damage.\n" +
                        "0 = disabled. Default 60s. Useful for targets stuck in geometry or unreachable.");

                ImGui.Spacing();
                ImGui.Text("Never Attack (by name)");
                ImGui.SetNextItemWidth(220);
                bool addByEnter = ImGui.InputTextWithHint("##blacklistName", "Monster name (e.g. Drudge)",
                    ref _newBlacklistName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Monsters whose name CONTAINS this text (case-insensitive) are never\n" +
                        "targeted, faced, or attacked. This manual do-not-attack list is\n" +
                        "separate from the automatic no-damage blacklist above.");
                ImGui.SameLine();
                if ((ImGui.Button("Add##blacklist") || addByEnter) && !string.IsNullOrWhiteSpace(_newBlacklistName))
                {
                    string entry = _newBlacklistName.Trim();
                    bool exists = false;
                    for (int i = 0; i < _settings.MonsterNameBlacklist.Count; i++)
                        if (string.Equals(_settings.MonsterNameBlacklist[i], entry, StringComparison.OrdinalIgnoreCase))
                        { exists = true; break; }
                    if (!exists) _settings.MonsterNameBlacklist.Add(entry);
                    _newBlacklistName = "";
                }

                if (_settings.MonsterNameBlacklist.Count == 0)
                {
                    ImGui.TextDisabled("(empty — any monster may be attacked)");
                }
                else
                {
                    int removeIdx = -1;
                    for (int i = 0; i < _settings.MonsterNameBlacklist.Count; i++)
                    {
                        ImGui.PushID(i);
                        if (ImGui.SmallButton("X")) removeIdx = i;
                        ImGui.SameLine();
                        ImGui.TextUnformatted(_settings.MonsterNameBlacklist[i]);
                        ImGui.PopID();
                    }
                    if (removeIdx >= 0) _settings.MonsterNameBlacklist.RemoveAt(removeIdx);
                }

                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Text("Give Queue");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Give Interval (ms)", ref _settings.GiveQueueIntervalMs, 0, 2000);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Minimum gap between two gives sent by /ra givea commands.\n0 = send the next give the moment the server answers the last (fastest).\nThe queue always waits for the server's answer first.");

                ImGui.Separator();
                ImGui.Spacing();
                ImGui.Text("Meta Compatibility");
                ImGui.Checkbox("Allow metas to send keystrokes (/mt send)", ref _settings.AllowMetaKeystrokes);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Mag-Tools metas press keys and click inside the game window with /mt send, /mt click,\n" +
                                     "/mt movement, /mt strafe, /mt client minimize|restore and the key form of /mt jump.\n" +
                                     "Off (the default): those verbs log once and do nothing. Turn it on only for a meta you trust:\n" +
                                     "the keys go to the game window as if you pressed them. /mt send /command (a chat command)\n" +
                                     "is not a key press and works either way.");
                break;

            case "Recharge":
                SafeImGui.Text("Self Vitals (%)");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Heal At", ref _settings.HealAt, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Emergency Heal At", ref _settings.EmergencyHealAt, 0, 100);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip("At or under this health %, a healing kit goes first out of Magic mode,\nand a kit or potion is used while a cast is pending. 0 = off.");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Stamina To Health At", ref _settings.StaminaToHealthAt, 0, 100);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip("At or under this health %, cast Stamina to Health Self ahead of Heal At\n(after a kit out of Magic mode). 0 = never cast it.");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Stamina To Health Min Stamina", ref _settings.StaminaToHealthMinStamina, 0, 100);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip("Don't cast Stamina to Health unless stamina is over this %.");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Re-stam At", ref _settings.RestamAt, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Re-stam To", ref _settings.RestamTo, 0, 100);
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip("Once a recharge starts under Re-stam At, keep recharging until stamina reaches this %.\n0 = one recharge, as before.");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Get Mana At", ref _settings.GetManaAt, 0, 100);

                ImGui.Spacing();
                SafeImGui.Text("Top-Off Vitals (%)");
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Top HP", ref _settings.TopOffHP, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Top Stam", ref _settings.TopOffStam, 0, 100);
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Top Mana", ref _settings.TopOffMana, 0, 100);

                ImGui.Spacing();
                SafeImGui.Text("Helper Settings (%)");
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
                    SafeImGui.SetTooltip("When enabled and Recklessness is trained, auto power uses 80% instead of 100%.");

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

                ImGui.Spacing();
                ImGui.Text("Variable Power (monster rules)");
                if (ImGui.IsItemHovered())
                    SafeImGui.SetTooltip("A monster rule's Power column picks per monster: Default (these settings),\n" +
                                     "High (100%), Reckless (80%, needs Recklessness trained), Variable (a new\n" +
                                     "random power each attack in this range), or Reckless + Variable (random,\n" +
                                     "kept inside the Recklessness band 20-80%).");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Variable Min %", ref _settings.VariablePowerMin, 0, 100);
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Variable Max %", ref _settings.VariablePowerMax, 0, 100);

                ImGui.Spacing();
                ImGui.Text("Missiles Hitting the Environment");
                ImGui.SetNextItemWidth(220);
                ImGui.Combo("Missile Response", ref _settings.MissileEnvironmentResponse,
                    EnvironmentImpactPlanner.ResponseNames, EnvironmentImpactPlanner.ResponseNames.Length);
                if (ImGui.IsItemHovered())
                    SafeImGui.SetTooltip("When arrows or bolts keep hitting walls or objects instead of the target\n" +
                                     "(\"Your missile attack hit the environment.\"), stop shooting and:\n" +
                                     "  Move closer - run toward the target for a clearer shot\n" +
                                     "  New target - skip this one for a while and fight another\n" +
                                     "  Move closer, then new target - one approach, then switch\n" +
                                     "The only monster in reach is never skipped.");
                DrawEnvironmentThrottleKnobs("missile");

                ImGui.Spacing();
                ImGui.Text("Pull Away (Kite)");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Pull Distance (m)", ref _settings.KitePullMeters, 3, 100);
                if (ImGui.IsItemHovered())
                    SafeImGui.SetTooltip("When a monster rule's Kite is Pull away (or Default follows the\n" +
                                     "DEFAULT row set to Pull away), tag the mob then run until it has\n" +
                                     "been dragged this many metres from where it stood, then kill it.\n" +
                                     "Set per mob on the Monsters / Damage panels.");
                ImGui.Checkbox("Tag Before Fleeing", ref _settings.KiteTagFirst);
                if (ImGui.IsItemHovered())
                    SafeImGui.SetTooltip("Land a hit before running so aggro sticks. Off = flee as soon as locked.");

                ImGui.Spacing();
                ImGui.Text("Weapon Swaps");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Wait After Target Switch (ms)", ref _settings.WeaponSwapSettleMs, 0, 3000);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("After switching from one live target to another, wait this long before a weapon\n" +
                                     "swap starts, so a target that changes again doesn't cost a wasted swap.\n" +
                                     "A new target after a kill swaps at once. 0 = never wait.");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("No Swap Under Target Health %", ref _settings.WeaponSwapMinHealthPct, 0, 100);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("A target at or under this health is finished with the weapon in hand\n" +
                                     "(same kind of weapon only). A swap under way always completes. 0 = off.");
                ImGui.SetNextItemWidth(150);
                ImGui.SliderInt("Weakness Swap Min Gain %", ref _settings.WeaponSwapMinGainPct, 0, 200);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Auto (weakness) picks only: swap to another element only when it does at least this\n" +
                                     "much more damage than the weapon in hand. A launcher swap with other ammo costs about 4 s.\n" +
                                     "Damage tab, slayer and Monsters rule picks always swap. 0 = off.");

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

                ImGui.SetNextItemWidth(180);
                ImGui.SliderInt("Spell Interval (ms)", ref _settings.SpellCastIntervalMs, 100, 1500);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Delay between BUFF / utility spell casts (not combat).\n" +
                        "Lower = faster buff chains. 400ms is a good balance.\n" +
                        "Below 200ms may cause fizzles or dropped casts on laggy servers.");

                ImGui.SetNextItemWidth(180);
                ImGui.SliderInt("Stuck Cast Break (ms)", ref _settings.StuckCastBreakMs, 0, 20000);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "The longest a cast may stay unanswered before it is broken\n" +
                        "with a jump (the server then fails the cast). It fires sooner,\n" +
                        "about 2 s past the spell's normal cycle, when that is shorter.\n" +
                        "After a 'too busy' answer nothing is cast until the server answers.\n" +
                        "0 = never break.");

                ImGui.SetNextItemWidth(180);
                ImGui.SliderInt("Recovery Retry (ms)", ref _settings.RecoveryRetryMs, 200, 5000);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Heal Self, Stamina to Health, Revitalize and Stamina to Mana refused\n" +
                        "for 'not enough mana' are tried again after this long: each try\n" +
                        "rolls Mana Conversion again, and a refusal costs nothing.\n" +
                        "At most 5 tries a second, 20 in a row.");

                ImGui.Checkbox("HP Failsafe Recall", ref _settings.HpFailsafeRecall);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "At or under Emergency Heal At with no heal landing for 5 s:\n" +
                        "break the stuck cast with a jump and cast Lifestone Recall at\n" +
                        "once (the spell, not /ls). The macro waits while it casts and\n" +
                        "comes back on if the recall can't go. Needs the spell and its\n" +
                        "mana; only a character that can heal (a heal spell, kit or\n" +
                        "potion) is ever recalled. Off: the jump only, after 7.5 s.");

                ImGui.SetNextItemWidth(180);
                ImGui.SliderInt("Attack Spell Delay (ms)", ref _settings.AttackSpellIntervalMs, 0, 5000);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Least time between offensive (war/void) COMBAT casts at one target.\n" +
                        "The next cast also waits until the last one is finished (the server's\n" +
                        "answer and the casting animation), so 0 = cast again as soon as it is.\n" +
                        "Does NOT affect buffing speed.");

                ImGui.Spacing();
                ImGui.Checkbox("Cast Dispel Self", ref _settings.CastDispelSelf);

                ImGui.Spacing();
                ImGui.Text("Ring Spell Override");
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Min Ring Targets", ref _settings.MinRingTargets);
                ImGui.InputInt("Blast Range (0 = off)", ref _settings.BlastRange);
                ImGui.InputInt("Min Blast Targets", ref _settings.MinBlastTargets);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("If this many monsters are within ring range, ring spells are used instead of arc/bolt/streak.");
                ImGui.InputInt("Wall Range (0 = off)", ref _settings.WallRange);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Monster rules with Wall on cast their element's wall spell when enough monsters\n" +
                                     "are within this many yards in the wall's path toward the target.\n" +
                                     "The character turns to face the target first. 0 = no walls.");
                ImGui.InputInt("Min Wall Targets", ref _settings.MinWallTargets);

                ImGui.Spacing();
                ImGui.Text("Spells Hitting the Environment");
                ImGui.SetNextItemWidth(220);
                ImGui.Combo("Magic Response", ref _settings.MagicEnvironmentResponse,
                    EnvironmentImpactPlanner.ResponseNames, EnvironmentImpactPlanner.ResponseNames.Length);
                if (ImGui.IsItemHovered())
                    SafeImGui.SetTooltip("When bolts, streaks, arcs or blasts keep stopping short of the target\n" +
                                     "(a wall, a pillar, the ceiling) with no hit or resist, stop casting and:\n" +
                                     "  Move closer - run toward the target for a clearer line\n" +
                                     "  New target - skip this one for a while and fight another\n" +
                                     "  Move closer, then new target - one approach, then switch\n" +
                                     "Rings and walls aren't judged. The only monster in reach is never skipped.");
                DrawEnvironmentThrottleKnobs("magic");

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
                ImGui.InputInt("Disengage Range", ref _settings.MonsterDisengageRange);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Drop a locked target only past this distance (hysteresis).\n0 or <= Monster Range = auto (Monster Range + 3).");
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Ring Range", ref _settings.RingRange);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Approach Range", ref _settings.ApproachRange);

                ImGui.Spacing();
                ImGui.Text("Combat Targeting");
                ImGui.Separator();
                ImGui.SetNextItemWidth(160);
                ImGui.Combo("Targeting", ref _settings.TargetSelection, TargetSelectionNames, TargetSelectionNames.Length);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Which monster to attack, among those of the highest priority inside Monster Range.\nLeast turning: the one needing the smallest turn to face. Unknown health counts as full.");
                ImGui.Checkbox("Target Lock", ref _settings.TargetLock);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("On: keeps the target it picked until it dies, leaves Monster Range, or a higher-priority monster shows up.\nOff: switches whenever another monster is clearly better by the Targeting rule.");
                ImGui.Checkbox("Fight the monster I select", ref _settings.FightSelectedTarget);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(LegacyUiSettings.FightSelectedTargetTip);

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
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("While a nav route runs (a loaded route or a follow), RynthAi only follows it: no hunting, no looting, no fighting back. With no route, at the end of a Once route, or during Outdoor Hunt it hunts and loots as usual. Buffing and healing always run.");
                ImGui.SetNextItemWidth(120);
                ImGui.InputFloat("Nav point reach (yd)", ref _settings.FollowNavMin, 0.1f, 1.0f, "%.1f");
                _settings.FollowNavMin = LegacyUiSettings.ClampFollowNavMin(_settings.FollowNavMin);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("How close to get to each nav point before moving on to the next (yards). VTank calls it Follow/Nav Min Distance. Up to 2 nav walks right up to each point before turning (tight dungeons); bigger values round corners. Follow stops this close to its leader; the nav marker ring shows it.");

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
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Slope Sink", ref _settings.NavSlopeSink, 0.0f, 8.0f, "%.2f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Extra downward offset on slopes only. 0 = off; flat ground unaffected.");

                ImGui.Checkbox("Show Terrain Passability", ref _settings.ShowTerrainPassability);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Highlight impassable terrain triangles in red.");
                ImGui.SetNextItemWidth(160);
                ImGui.Combo("Waypoint Labels", ref _settings.NavLabelMode, NavLabelModeNames, NavLabelModeNames.Length);
                ImGui.Checkbox("Guide Line", ref _settings.NavGuideLine);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("A line from your character to the waypoint nav is heading for.");

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

                    ImGui.Checkbox("Only Doors On My Path", ref _settings.OpenDoorsOnPathOnly);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Open only a closed door that is on the way: within the range along the route's next legs (or straight ahead when not on a route).\nOff: every closed door within the range is opened.");

                    ImGui.Checkbox("Auto-Unlock Doors", ref _settings.AutoUnlockDoors);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("If a door is locked, try to use a lockpick from your Consumable Items list.");
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Getting Back On Route");

                ImGui.Checkbox("Go To Closest Waypoint", ref _settings.NavResumeNearest);
                if (ImGui.IsItemHovered())
                    SafeImGui.SetTooltip(NavResumeNearestTip);

                ImGui.Checkbox("Find A Way Back When Stuck", ref _settings.NavRecoveryEnabled);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When a jump doesn't free a stuck character, or navigation wanders far off the route, plan a way back to the route around walls and obstacles (the dungeon map indoors, RynthNav outdoors) and open doors on the way. Off = only the jump and side-step escapes, as before.");

                if (_settings.NavRecoveryEnabled)
                {
                    ImGui.SetNextItemWidth(80);
                    ImGui.InputFloat("Off Route Distance (yd)", ref _settings.NavOffTrackYards, 5f, 20f, "%.0f");
                    _settings.NavOffTrackYards = Math.Clamp(_settings.NavOffTrackYards, 160f, 1000f);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Navigation counts as off the route only beyond this distance, and only if it got there itself. At least 160: metas can take the character up to about 159 yd away on purpose, and coming back from that is normal.");

                    ImGui.SetNextItemWidth(80);
                    ImGui.InputInt("Tries Per Waypoint", ref _settings.NavMaxDetourAttempts);
                    _settings.NavMaxDetourAttempts = Math.Clamp(_settings.NavMaxDetourAttempts, 1, 10);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("How many planned ways back to try for one waypoint before giving up and using only the jump and side-step escapes. Reaching a waypoint resets the count.");
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
                ImGui.InputFloat("Lookahead (yd)", ref _settings.NavLookaheadYards, 0.5f, 1f, "%.1f");
                if (_settings.NavLookaheadYards < 0f) _settings.NavLookaheadYards = 0f;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Within this distance of a waypoint, blend the aim point toward the next one to round corners. The nav point reach limits it to 2 yd per yard of reach over 1 yd (no rounding up to a 2 yd reach), never more than this. 0 = off (aim straight at each waypoint).");

                ImGui.SetNextItemWidth(80);
                ImGui.InputFloat("Turn Rate (deg/s)", ref _settings.NavTurnRateDegPerSec, 15f, 45f, "%.0f");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Max turn speed while navigating. Higher = snappier turns, lower = gentler.");

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
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Top buffs off early between fights. When the bot is idle (no target, no\n" +
                        "monster within Monster Range, no corpse to loot), it rebuffs as soon as a\n" +
                        "buff has 'Rebuff With' + 'Idle Margin' left, instead of waiting for\n" +
                        "'Rebuff With'. So fewer buffs run out in the next fight.\n" +
                        "It never stops a rebuff: a buff under 'Rebuff With' (or dispelled) is\n" +
                        "recast as always, in a fight too. An early top-off never starts in a\n" +
                        "fight, and one under way stops when a fight starts.");
                if (_settings.RebuffWhenIdle)
                {
                    ImGui.SetNextItemWidth(180);
                    ImGui.SliderInt("Idle Margin (minutes)", ref _settings.RebuffIdleMarginMinutes, 0, 60);
                    if (_settings.RebuffIdleMarginMinutes < 0) _settings.RebuffIdleMarginMinutes = 0;
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "How much earlier than 'Rebuff With' an idle top-off may start.\n" +
                            "Default 2: with 'Rebuff With' 300 s, an idle bot rebuffs at 7 minutes\n" +
                            "left instead of 5. 0 = Rebuff When Idle adds nothing.");
                }

                ImGui.Spacing();
                ImGui.SetNextItemWidth(180);
                ImGui.SliderInt("Rebuff With (seconds left)", ref _settings.RebuffSecondsRemaining, 30, 1800);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Recast a self buff when its remaining duration drops below this value.\n" +
                        "Default 300 (5 minutes). Lower values rebuff more eagerly; very low\n" +
                        "values (<60s) risk a gap between the old buff dropping and the new one\n" +
                        "landing.");

                ImGui.SetNextItemWidth(180);
                ImGui.SliderInt("Also Refresh Under (seconds left)", ref _settings.RebuffTopOffSecondsRemaining, 30, 3600);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "When a buff is due, also recast every other buff with less than this\n" +
                        "much time left, so they land together. Buffs with more time are left\n" +
                        "alone. Default 1200 (20 minutes). At or below 'Rebuff With', only the\n" +
                        "expiring buff is recast.");

                ImGui.Checkbox("Cast buffs even when an item gives them", ref _settings.CastBuffsOverItemBuffs);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Cast your own buff even when worn gear already gives that buff.\n" +
                        "For servers with buff augments, where your own cast is stronger or\n" +
                        "lasts longer. On by default: on retail it costs a few extra casts.");

                ImGui.Checkbox("Chain buffs on the server's answer", ref _settings.FastBuffChain);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Send the next buff as soon as the server finishes the last one,\n" +
                        "without waiting for the cast's recoil animation to end.\n" +
                        "A refused cast turns it off for the rest of that buff pass.\n" +
                        "On by default. Off = every buff waits for the animation.");
                ImGui.Checkbox("Skip buffs this character can't use", ref _settings.SkipUnusableBuffs);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Leave out Blood Drinker / Heart Seeker / Swift Killer when you have no melee or\n" +
                        "missile weapon (listed or in hand), Spirit Drinker / Hermetic Link when you have no\n" +
                        "wand, and Defender when you have neither. Masteries for untrained skills are\n" +
                        "always skipped. On by default. Off = weapon enchantments are cast regardless of\n" +
                        "listed weapons.");
                string skipped = _buffSkips?.Invoke() ?? "";
                if (_settings.SkipUnusableBuffs && skipped.Length > 0)
                    SafeImGui.TextWrapped("Skipping: " + skipped);

                ImGui.Spacing();
                if (ImGui.Button("Force Buff##forceBuff") && _forceBuff != null) _forceBuff();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Recast every buff now, whatever time is left on it, starting with\n" +
                        "Creature Enchantment, Focus, Willpower and Life Magic.\n" +
                        "Same as /ra buff force (and the FR button).");
                ImGui.SameLine();
                if (ImGui.Button("Cancel##cancelBuff") && _cancelBuff != null) _cancelBuff();

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Protections and Banes");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Untick a protection or bane to stop the bot casting it.\n" +
                        "Unticked buffs are skipped and never hold the bot in Buffing.\n" +
                        "All on by default.");
                if (ImGui.BeginTable("##protsBanes", 2, ImGuiTableFlags.SizingStretchSame))
                {
                    ImGui.TableSetupColumn("Protections");
                    ImGui.TableSetupColumn("Banes");
                    ImGui.TableHeadersRow();
                    int rows = Math.Max(BuffManager.ToggleProtections.Length, BuffManager.ToggleBanes.Length);
                    for (int i = 0; i < rows; i++)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        if (i < BuffManager.ToggleProtections.Length) BuffToggle(BuffManager.ToggleProtections[i]);
                        ImGui.TableSetColumnIndex(1);
                        if (i < BuffManager.ToggleBanes.Length) BuffToggle(BuffManager.ToggleBanes[i]);
                    }
                    ImGui.EndTable();
                }
                ImGui.Checkbox("Buff the shield", ref _settings.BuffShield);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Cast the ticked Impenetrability and Banes on your shield too, after\n" +
                        "the armor, renewed with it. The shield is the one in the Items list\n" +
                        "(Shield) that you wear: buffing puts it in a pack for the wand, so it\n" +
                        "is buffed there. On by default. No shield listed: nothing changes.");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Buff Difficulty (Min Buffed Skill)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(
                        "Minimum buffed skill level required to cast each self-buff tier.\n" +
                        "Separate from Spell Combat thresholds so you can tune buffing\n" +
                        "more aggressively — buff fizzles just retry on the next heartbeat.\n" +
                        "AC's hard minimums are 1/50/100/150/200/250/300/350.");

                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 1##Buff", ref _settings.BuffMinSkillLevelTier1);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 2##Buff", ref _settings.BuffMinSkillLevelTier2);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 3##Buff", ref _settings.BuffMinSkillLevelTier3);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 4##Buff", ref _settings.BuffMinSkillLevelTier4);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 5##Buff", ref _settings.BuffMinSkillLevelTier5);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 6##Buff", ref _settings.BuffMinSkillLevelTier6);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 7##Buff", ref _settings.BuffMinSkillLevelTier7);
                ImGui.SetNextItemWidth(120);
                ImGui.InputInt("Level 8##Buff", ref _settings.BuffMinSkillLevelTier8);
                break;

            case "Looting":
                ImGui.Checkbox("Enable Looting", ref _settings.EnableLooting);
                // The loot profile picker (the dashboard's LOOT list), and beside it Loot Mode, which
                // decides whether that profile is used at all.
                string[]? lootFiles = LootProfiles?.Invoke();
                if (lootFiles != null && lootFiles.Length > 0 && SelectLootProfile != null)
                {
                    int lootIdx = Math.Clamp(_settings.LootProfileIdx, 0, lootFiles.Length - 1);
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.Combo("Loot Profile", ref lootIdx, lootFiles, lootFiles.Length) && lootIdx != _settings.LootProfileIdx)
                        SelectLootProfile(lootIdx);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Loot.LootModes.IsRares(_settings.LootMode)
                            ? "The loot profile. Not used while Loot Mode is Rares and needed mana stones only."
                            : "The loot profile: what corpses, chests and ground items give up.");
                    ImGui.SameLine();
                }
                _settings.LootMode = Loot.LootModes.Clamp(_settings.LootMode);
                ImGui.SetNextItemWidth(240);
                ImGui.Combo("Loot Mode", ref _settings.LootMode, Loot.LootModes.Labels, Loot.LootModes.Labels.Length);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Profile: the loot profile picked on the dashboard (LOOT), with RynthAi's built-in keeps.\n" +
                                     "Rares and needed mana stones only: the loot profile is not used. Every rare is looted, and\n" +
                                     "mana stones while you have fewer than the Items panel's keep count (Mana Stone Tapping);\n" +
                                     "nothing else (no salvage, no Learn Unknown Spells, no Mana Tap). Recover My Corpse still works.\n" +
                                     "Also /ra loot mode profile|rares, and the meta setting LootMode (0 or 1).");
                ImGui.Checkbox("Boost Loot Priority", ref _settings.BoostLootPriority);
                // "Loot Only Rare Corpses" is hidden: nothing reads LootOnlyRareCorpses.
                ImGui.Checkbox("Learn Unknown Spells", ref _settings.ReadUnknownScrolls);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Loots scrolls of spells you don't know and can learn (magic school trained or\nspecialized, skill high enough), and reads them when it's safe. Scrolls already\nin your pack are read too.");
                ImGui.Checkbox("Pickup with macro off", ref _settings.LootChestsMacroOff);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("A chest you open is looted by the loaded loot profile\neven with the macro or Looting off.");
                if (_settings.LootChestsMacroOff)
                {
                    ImGui.Indent();
                    ImGui.Checkbox("Leave the chest open", ref _settings.LootChestsMacroOffLeaveOpen);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("When the pickup is done the chest stays open and chat says what was taken,\n" +
                                         "so you can check it and take anything your profile missed. Untick it and\n" +
                                         "RynthAi closes the chest when it's done (some chests lock again on close).");
                    ImGui.Unindent();
                }
                ImGui.Checkbox("Jump When Looting", ref _settings.LootJumpEnabled);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Hop (the keyboard jump) just before each pickup,\nso the server skips the bend-down. The hop has a fixed height.");

                ImGui.Spacing();
                ImGui.Text("Corpse Ownership");
                ImGui.SetNextItemWidth(180);
                ImGui.Combo("Loot From", ref _settings.LootOwnership, LootOwnershipModes, LootOwnershipModes.Length);
                ImGui.Checkbox("Recover My Corpse", ref _settings.LootOwnCorpse);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When your own corpse (\"Corpse of <you>\") is within loot range, open it and take\nevery item on it, whatever the loot profile and Loot From say.");
                ImGui.Checkbox("Item Info On Select", ref _settings.ItemInfoSettings.OnSelect);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Selecting an item prints its details to chat (/ra iteminfo).");
                ImGui.SameLine();
                if (ImGui.SmallButton("Options##iiOpts"))
                    _settings.ItemInfoSettings.ShowWindow = !_settings.ItemInfoSettings.ShowWindow;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Open the RynthAi Item Info window (fields, ratings, click trigger, layout).");
                LegacyItemInfoUi.RenderLootChat(_settings.ItemInfoSettings, "##advLoot");
                ImGui.Checkbox("Travel Back To My Corpse", ref _settings.TravelToOwnCorpse);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("After a death, travel back to where you died with RynthNav (outdoors only),\nfighting on the way, recover the corpse, then carry on. Needs the macro running.");

                ImGui.Spacing();
                ImGui.Text("Inventory Management");
                ImGui.Checkbox("Enable Autostack", ref _settings.EnableAutostack);
                ImGui.Checkbox("Enable Autocram", ref _settings.EnableAutocram);
                ImGui.Checkbox("Combine Salvage Bags", ref _settings.EnableCombineSalvage);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("After the salvage queue empties, move same-name bags together so the server merges them.");
                ImGui.Checkbox("Combine Bags During Salvage", ref _settings.CombineBagsDuringSalvage);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("When salvaging an item, also add any under-full salvage bag of the same material to the salvage panel — the salvage operation merges them into a single bag.");
                ImGui.Checkbox("Salvage Without The Panel", ref _settings.SalvageWithoutPanel);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Salvage (and combine bags) by sending the salvage request to the server directly: AC's salvage panel never opens. A request that changes nothing is tried once through the panel. Needs an engine with plugin API v82.");

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Text("Loot Timers (ms)");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("The baseline is measured from live looting on ACE (2026-10-14). The log's\n[LootSettings] line lists each timer as baseline or custom.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Use Baseline##lootTimers"))
                    ApplyLootBaseline();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Set all six loot timers to the baseline.");
                // Inter-Item Delay and Closing Delay are hidden: the corpse looting never reads them.
                LootTimerInput("Content Settle", ref _settings.LootContentSettleMs, LootBaseline.ContentSettleMs,
                    "Wait after an open for the container's items. Only used when the items\naren't already known; on ACE they always were.");
                LootTimerInput("Empty Corpse Wait", ref _settings.LootEmptyCorpseMs, LootBaseline.EmptyCorpseMs,
                    "Wait for a corpse's contents before probing it or calling it empty.");
                LootTimerInput("Assess Window", ref _settings.LootAssessWindowMs, LootBaseline.AssessWindowMs,
                    "Hold items for their appraisal. Measured replies: 127 ms typical,\n256 ms at the 95th percentile, 350 ms at most.");
                LootTimerInput("Loot Retry Timeout", ref _settings.LootRetryTimeoutMs, LootBaseline.RetryTimeoutMs,
                    "Wait, once the character is still, before resending a pickup that hasn't\nlanded. Too short resends pickups the server is still doing.");
                LootTimerInput("Corpse Open Retry", ref _settings.LootOpenRetryMs, LootBaseline.OpenRetryMs,
                    "Wait before sending another open to a corpse that hasn't opened.");
                LootTimerInput("Corpse Timeout", ref _settings.LootCorpseTimeoutMs, LootBaseline.CorpseTimeoutMs,
                    "Give up on a claimed corpse after this long. The clock stops during combat\nand buffing.");

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
                    SafeImGui.TextColored(stateColor, stateLabel);

                    if (!string.IsNullOrEmpty(_missileCraftingManager.StatusMessage))
                        SafeImGui.TextWrapped(_missileCraftingManager.StatusMessage);
                }
                break;

            case "Vendoring":
                RenderVendoring();
                break;

            default:
                SafeImGui.Text($"Settings for {tabName} are currently under development.");
                break;
        }
    }
}

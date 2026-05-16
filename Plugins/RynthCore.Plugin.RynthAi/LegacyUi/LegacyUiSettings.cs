using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

public static class DashWindows
{
    public static bool ShowMacroRules;
    public static bool ShowMonsters;
    public static bool ShowNavigation;
    public static bool ShowWeapons;
    public static bool ShowLua;
    public static bool ShowDungeonMap;
}

public sealed class LegacyUiSettings
{
    // ── Runtime-only state — never persisted ────────────────────────────────
    [JsonIgnore] public bool IsMacroRunning;
    /// <summary>Meta state — set only by meta actions (SetState, CallState, ReturnFromCall, UI, meta load).</summary>
    [JsonIgnore] public string CurrentState = "Default";
    /// <summary>Bot action state — what the bot is doing right now (Default, Combat, Looting, Navigating, Buffing).
    /// Separate from meta state so operational cycling doesn't corrupt meta rule matching.</summary>
    [JsonIgnore] public string BotAction = "Default";
    [JsonIgnore] public bool IsRecordingNav;

    public bool EnableBuffing = true;
    public bool EnableCombat;
    public bool EnableNavigation;
    public bool EnableLooting;
    public bool EnableMeta;
    public bool EnableRaycasting = true;
    public bool MetaDebug;

    public int MovementMode;
    public float NavStopTurnAngle = 20f;
    public float NavResumeTurnAngle = 10f;
    public float NavDeadZone = 4f;
    public float NavSweepMult = 2.5f;
    public float PostPortalDelaySec = 4.0f;
    public float T2Speed = 1.0f;
    public float T2DistanceTo = 0.5f;
    public float T2ReissueMs = 2000f;
    public float T2MaxRangeYd = 500f;
    public int T2MaxLandblocks = 3;
    public float T2WalkWithinYd = 5f;

    public string CurrentNavPath = string.Empty;
    public string CurrentLootPath = string.Empty;
    public string CurrentMetaPath = string.Empty;

    public int MacroSettingsIdx = 1;
    public int NavProfileIdx = 1;
    public int LootProfileIdx;
    public int MetaProfileIdx = 1;

    public bool EnableAutostack = true;
    public bool EnableAutocram = true;
    public bool EnableCombineSalvage = true;
    public bool CombineBagsDuringSalvage = true;

    public bool ShowTargetStaminaMana;

    public bool EnableMissileCrafting = true;
    public int MissileCraftAmmoThreshold = 1000;

    public int LootInterItemDelayMs = 50;
    public int LootContentSettleMs = 100;
    public int LootEmptyCorpseMs = 300;
    public int LootClosingDelayMs = 200;
    public int LootAssessWindowMs = 200;
    public int LootRetryTimeoutMs = 500;
    public int LootOpenRetryMs = 1500;
    public int LootCorpseTimeoutMs = 12000;
    public bool LootJumpEnabled = false;
    public int LootJumpHeight = 10; // 0–100; maps to 0.0–1.0 JumpNonAutonomous extent

    public int SalvageOpenDelayFirstMs = 400;
    public int SalvageOpenDelayFastMs = 50;
    public int SalvageAddDelayFirstMs = 600;
    public int SalvageAddDelayFastMs = 50;
    public int SalvageSalvageDelayMs = 50;
    public int SalvageResultDelayFirstMs = 1000;
    public int SalvageResultDelayFastMs = 250;
    public bool UseDispelItems;
    public bool CastDispelSelf;
    public bool AutoFellowMgmt;
    public bool MChargesWhenOff;

    public int HealAt = 60;
    public int RestamAt = 30;
    public int GetManaAt = 40;
    public int TopOffHP = 95;
    public int TopOffStam = 95;
    public int TopOffMana = 95;
    public int HealOthersAt = 50;
    public int RestamOthersAt = 10;
    public int InfuseOthersAt = 10;

    public int MonsterRange = 50;
    public int RingRange = 5;
    public int ApproachRange = 4;
    public int MinRingTargets = 4;
    public float FollowNavMin = 1.5f;
    public float NavRingThickness = 6.0f;
    public float NavLineThickness = 6.0f;
    public float NavHeightOffset = 0.05f;
    public bool  ShowTerrainPassability = true;
    public double MaxMonRange = 12.0;
    public bool SummonPets;
    public int CustomPetRange = 5;
    public int PetMinMonsters = 1;
    public bool AdvancedOptions;
    public bool MineOnly = true;
    public bool ShowEditor;
    public double CorpseApproachRangeMax = 10.0;
    public double CorpseApproachRangeMin = 2.0;

    public bool BoostNavPriority;
    public bool BoostLootPriority;
    public bool OpenDoors;
    public float OpenDoorRange = 5.0f;
    public bool AutoUnlockDoors;
    public int LootOwnership;
    public bool LootOnlyRareCorpses;
    public bool PeaceModeWhenIdle = true;
    public bool RebuffWhenIdle;
    /// <summary>
    /// Re-cast a self buff when its remaining duration drops below this many seconds.
    /// Default 300 (5 minutes). Lower values rebuff more eagerly; very low values
    /// (under ~60s) risk recasting between an old buff dropping and the new one
    /// landing.
    /// </summary>
    public int RebuffSecondsRemaining = 300;
    public bool StartMacroOnLogin;

    public int BlacklistAttempts = 3;
    public int BlacklistTimeoutSec = 30;
    /// <summary>
    /// Blacklist a target after being engaged with it for this many seconds without
    /// dealing any damage. 0 = disabled. Default 60s.
    /// </summary>
    public int TargetNoProgressTimeoutSec = 0; // 0 = disabled; set to e.g. 120 to blacklist stuck targets

    public int GiveQueueIntervalMs = 150;

    public int SpellCastIntervalMs = 400;

    public int MeleeAttackPower = -1;
    public int MissileAttackPower = -1;
    public bool UseNativeAttack = true;
    public bool UseRecklessness;
    public int MeleeAttackHeight = 1;
    public int MissileAttackHeight = 1;

    // Projectile arc velocities for missile-weapon LoS checks.
    // Lower velocity = higher arc. Must approximate the AC client trajectory
    // so dungeon-ceiling hits are detected.
    public bool  UseArcs              = true;
    public float BowArcVelocity       = 25.0f;
    public float CrossbowArcVelocity  = 40.0f;
    public float AtlatlArcVelocity    = 22.0f;
    public float MagicArcVelocity     = 25.0f;

    public bool EnableFPSLimit = true;
    public int TargetFPSFocused = 60;
    public int TargetFPSBackground = 30;

    // Minimum buffed skill level required to cast spells of each tier — for COMBAT casts.
    // Defaults are tuned above AC's hard minimums (1/50/100/150/200/250/300/350) to avoid fizzles.
    public int MinSkillLevelTier1 = 35;
    public int MinSkillLevelTier2 = 85;
    public int MinSkillLevelTier3 = 135;
    public int MinSkillLevelTier4 = 185;
    public int MinSkillLevelTier5 = 235;
    public int MinSkillLevelTier6 = 285;
    public int MinSkillLevelTier7 = 335;
    public int MinSkillLevelTier8 = 435;

    // Minimum buffed skill level required to cast self-buffs of each tier.
    // Defaults match the spell combat thresholds so new installs work consistently.
    public int BuffMinSkillLevelTier1 = 35;
    public int BuffMinSkillLevelTier2 = 85;
    public int BuffMinSkillLevelTier3 = 135;
    public int BuffMinSkillLevelTier4 = 185;
    public int BuffMinSkillLevelTier5 = 235;
    public int BuffMinSkillLevelTier6 = 285;
    public int BuffMinSkillLevelTier7 = 335;
    public int BuffMinSkillLevelTier8 = 435;

    public bool EnableManaTapping   = false;
    public int  ManaTapMinMana      = 2500;
    public int  ManaStoneKeepCount  = 5;

    public List<MonsterRule> MonsterRules { get; set; } = new();
    public List<ItemRule> ItemRules { get; set; } = new();
    public List<ConsumableRule> ConsumableRules { get; set; } = new();
    public List<BuffRule> BuffRules { get; set; } = new();
    public List<MetaRule> MetaRules { get; set; } = new();

    /// <summary>
    /// Guards every read/enumerate/mutate of <see cref="MetaRules"/>. The list
    /// is touched from the plugin-tick thread (MetaManager.Think) AND the
    /// Avalonia dispatcher thread (MetaPanel poll → BuildMetaJson /
    /// HandleMetaCommand). List&lt;T&gt; is not thread-safe; concurrent
    /// enumerate+mutate corrupts the heap. Hold this lock for the full duration
    /// of any MetaRules access.
    /// </summary>
    [JsonIgnore]
    public object MetaRulesLock { get; } = new();

    /// <summary>
    /// Bumped on an *in-place* rule edit (a slot replaced, possibly changing a
    /// rule's State) so MetaManager's per-state index rebuilds. Add/delete/load
    /// change the list ref or count and are detected without this.
    /// </summary>
    [JsonIgnore]
    public int MetaRulesStructuralVersion;

    private Dictionary<string, List<string>> _embeddedNavs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// NAV routes embedded inside the currently-loaded .af/.met macro,
    /// keyed by name. Persisted so meta EmbedNav actions still resolve
    /// after a client restart without reloading the source file.
    /// Setter re-wraps incoming dict with OrdinalIgnoreCase comparer so
    /// JSON round-trip keeps case-insensitive lookups working.
    /// </summary>
    public Dictionary<string, List<string>> EmbeddedNavs
    {
        get => _embeddedNavs;
        set => _embeddedNavs = value != null
            ? new Dictionary<string, List<string>>(value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }
    public string LuaScript = "-- Enter your Lua script here\nprint('RynthAi Lua Loaded')";
    [JsonIgnore] public string LuaConsoleOutput = "--- RynthAi Lua Console ---";

    public string SelectedProfile = "Default";
    [JsonIgnore] public NavRouteParser CurrentRoute { get; set; } = new();
    public int ActiveNavIndex;

    public bool ShowAdvancedWindow;
    public int SelectedAdvancedTab;

    // ── Persisted window/UI state ───────────────────────────────────────────
    public float WindowPosX = -1f;
    public float WindowPosY = -1f;
    public float WindowSizeX = 430f;
    public float WindowSizeY = 452f;
    public bool WindowLocked;
    public bool DashboardVisible = true;
    public bool DashboardMinimized;
    public float BgOpacity = 0.95f;
    public bool DashShowWeapons;
    public bool DashShowLua;
    public bool DashShowNavigation;
    public bool DashShowMacroRules;
    public bool DashShowMonsters;
    public bool DashShowDungeonMap;
    public bool  MapShowDoors         = true;
    public bool  MapShowCreatures     = true;
    public bool  MapShowToolbar       = true;
    public float MapBgOpacity         = 1.0f;
    public bool  MapRotateWithPlayer  = false;

    // Radar dungeon-wall overlay
    public bool  ShowRadarWalls        = false; // WIP — renders as black box, disabled by default
    public float RadarWallWorldRange   = 35f; // world units shown edge-to-edge on the radar

    // When true, the engine suppresses the vanilla retail radar entirely.
    public bool  SuppressRetailRadar   = false;

    // When true, the engine hides the vanilla retail chatbox.
    public bool  SuppressRetailChat    = false;

    // When true, the engine no-ops the gmPowerbarUI notices so the vanilla
    // attack/magic power bar never appears on screen.
    public bool  SuppressRetailPowerbar = false;

    // Custom chat viewer
    public bool  ShowRynthChat         = false;
    public float ChatOpacity           = 0.15f;
    public int   ChatMaxLines          = 500;
    public bool  ChatShowTimestamps    = false;
    public bool  ChatClickThrough      = false;   // mouse passes through to game; gear stays clickable

    // When true, the custom RynthRadar widget is rendered.
    public bool  ShowRynthRadar        = false;
    public bool  RadarRotateWithPlayer = false;   // false = north-up; true = player always faces up
    public float RadarOpacity          = 0.85f;
    public float RadarZoom             = 3.5f;    // pixels per world unit
    public bool  RadarShowMonsters     = true;
    public bool  RadarShowNpcs         = true;
    public bool  RadarShowPortals      = true;
    public bool  RadarShowDoors        = true;
    // Grid-cell radius (0.5m per cell) of the "visited" paint stamp placed each
    // frame around the player. Higher = walls light up from further away.
    public int   RadarWallPaintRadius  = 3;
    public bool  RadarCircular         = false;   // false = square frame, true = circular
    public bool  RadarClickThrough     = false;   // mouse passes through to the game

    [JsonIgnore]
    public readonly string[] AdvancedTabs =
    {
        "Display", "UI", "Misc", "Recharge", "Melee Combat", "Spell Combat",
        "Ranges", "Navigation", "Buffing", "Crafting", "Looting"
    };

    [JsonIgnore]
    public bool ForceStateReset { get; set; }

    [JsonIgnore]
    public string NavStatusLine = string.Empty;

    [JsonIgnore]
    public bool NavIsStuck = false;

    public LegacyUiSettings()
    {
    }

    public void EnsureDefaultRule()
    {
        List<MonsterRule> existingDefaults = MonsterRules.Where(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)).ToList();
        MonsterRule trueDefault;

        if (existingDefaults.Count > 0)
        {
            trueDefault = existingDefaults[0];
            MonsterRules.RemoveAll(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            trueDefault = new MonsterRule { Name = "Default", Priority = 1, DamageType = "Auto", WeaponId = 0 };
        }

        MonsterRules.Insert(0, trueDefault);
    }
}

public sealed class MonsterRule
{
    public string Name { get; set; } = "New Monster";
    public string Category { get; set; } = "";
    public string MatchExpression { get; set; } = "";
    public int Priority { get; set; } = 1;
    public string DamageType { get; set; } = "Auto";
    public int WeaponId { get; set; }
    public bool Fester { get; set; }
    public bool Broadside { get; set; }
    public bool GravityWell { get; set; }
    public bool Imperil { get; set; }
    public bool Yield { get; set; }
    public bool Vuln { get; set; }
    public bool UseArc { get; set; }
    public bool UseRing { get; set; }
    public bool UseStreak { get; set; }
    public bool UseBolt { get; set; } = true;
    public string ExVuln { get; set; } = "None";
    public int OffhandId { get; set; }
    public string PetDamage { get; set; } = "PAuto";
}

public sealed class BuffRule
{
    public string SpellName { get; set; } = string.Empty;
    public int SpellId { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ItemRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Action { get; set; } = "Loot";
    public string Element { get; set; } = "Slash";
    public bool KeepBuffed { get; set; } = true;
}

/// <summary>JSON wire-format types used by the engine-side Avalonia MonstersPanel.</summary>
public sealed class MonstersBridgePayload
{
    public List<MonsterRule> Rules { get; set; } = new();
    public List<MonsterBridgeItem> Items { get; set; } = new();
    public string CurrentTargetName { get; set; } = string.Empty;
    /// <summary>Captured creature data per rule name (uppercased), filled by the plugin.</summary>
    public Dictionary<string, MonsterCapturedInfo> Captured { get; set; } = new();
}

public sealed class MonsterBridgeItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class MonsterCapturedInfo
{
    public uint MaxHealth { get; set; }
    public int  ArmorLevel { get; set; }
    public string WeakestType { get; set; } = string.Empty;
    public double WeakestValue { get; set; } = 1.0;
    public int Samples { get; set; }
}

public sealed class ConsumableRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "General";
}

/// <summary>Bridge payload for the engine-side Avalonia ItemsPanel.</summary>
public sealed class ItemsBridgePayload
{
    public List<ItemRule>       Weapons           { get; set; } = new();
    public List<ConsumableRule> Consumables       { get; set; } = new();
    public bool                 EnableManaTapping { get; set; }
    public int                  ManaTapMinMana    { get; set; }
    public int                  ManaStoneKeepCount { get; set; }
    public string               CurrentTargetName { get; set; } = string.Empty;
}

// ── Nav bridge types ─────────────────────────────────────────────────────────

public sealed class NavBridgePoint
{
    public int    Idx    { get; set; }
    public string Type   { get; set; } = string.Empty;  // "Point", "Recall", "Pause", "Chat", "PortalNPC"
    public string Desc   { get; set; } = string.Empty;  // NavPoint.ToString()
    public double NS     { get; set; }
    public double EW     { get; set; }
    public double Z      { get; set; }
}

public sealed class NavBridgePayload
{
    public string            ActiveNavName    { get; set; } = string.Empty;
    public string            NavStatusLine    { get; set; } = string.Empty;
    public bool              NavIsStuck       { get; set; }
    public bool              MacroRunning     { get; set; }
    public bool              NavigationEnabled{ get; set; }
    public int               RouteType        { get; set; }  // 1=Circular,2=Linear,3=Follow,4=Once
    public int               ActiveNavIndex   { get; set; }
    public List<string>      NavFiles         { get; set; } = new();
    public List<NavBridgePoint> Points        { get; set; } = new();
}

/// <summary>One-shot command sent from the Avalonia NavPanel to the plugin.</summary>
public sealed class NavCommand
{
    public string Cmd       { get; set; } = string.Empty;
    public int    SpellId   { get; set; }
    public int    Index     { get; set; }
    public int    RouteType { get; set; }
    public int    AddMode   { get; set; }   // 0=End, 1=Above, 2=Below
    public int    InsertAt  { get; set; } = -1;
    public string NavName   { get; set; } = string.Empty;
}

/// <summary>Bridge payload for the engine-side Avalonia SettingsPanel.</summary>
public sealed class SettingsBridgePayload
{
    // Display
    public bool ShowTargetStaminaMana { get; set; }

    // UI
    public bool SuppressRetailRadar { get; set; }
    public bool ShowRynthRadar { get; set; }
    public bool RadarClickThrough { get; set; }
    public bool SuppressRetailChat { get; set; }
    public bool ShowRynthChat { get; set; }
    public bool ChatClickThrough { get; set; }
    public bool SuppressRetailPowerbar { get; set; }

    // Misc
    public bool EnableFPSLimit { get; set; }
    public int TargetFPSFocused { get; set; }
    public int TargetFPSBackground { get; set; }
    public bool EnableAutocram { get; set; }
    public bool PeaceModeWhenIdle { get; set; }
    public bool StartMacroOnLogin { get; set; }
    public bool EnableRaycasting { get; set; }
    public bool UseArcs { get; set; }
    public float BowArcVelocity { get; set; }
    public float CrossbowArcVelocity { get; set; }
    public float AtlatlArcVelocity { get; set; }
    public float MagicArcVelocity { get; set; }
    public int BlacklistAttempts { get; set; }
    public int BlacklistTimeoutSec { get; set; }
    public int TargetNoProgressTimeoutSec { get; set; }
    public int GiveQueueIntervalMs { get; set; }

    // Recharge
    public int HealAt { get; set; }
    public int RestamAt { get; set; }
    public int GetManaAt { get; set; }
    public int TopOffHP { get; set; }
    public int TopOffStam { get; set; }
    public int TopOffMana { get; set; }
    public int HealOthersAt { get; set; }
    public int RestamOthersAt { get; set; }
    public int InfuseOthersAt { get; set; }

    // Melee Combat
    public bool UseRecklessness { get; set; }
    public int MeleeAttackPower { get; set; }
    public int MeleeAttackHeight { get; set; }
    public int MissileAttackPower { get; set; }
    public int MissileAttackHeight { get; set; }
    public bool UseNativeAttack { get; set; }
    public bool SummonPets { get; set; }
    public int PetMinMonsters { get; set; }

    // Spell Combat
    public int SpellCastIntervalMs { get; set; }
    public bool CastDispelSelf { get; set; }
    public int MinRingTargets { get; set; }
    public int MinSkillLevelTier1 { get; set; }
    public int MinSkillLevelTier2 { get; set; }
    public int MinSkillLevelTier3 { get; set; }
    public int MinSkillLevelTier4 { get; set; }
    public int MinSkillLevelTier5 { get; set; }
    public int MinSkillLevelTier6 { get; set; }
    public int MinSkillLevelTier7 { get; set; }
    public int MinSkillLevelTier8 { get; set; }

    // Ranges
    public int MonsterRange { get; set; }
    public int RingRange { get; set; }
    public int ApproachRange { get; set; }
    public double CorpseApproachRangeMax { get; set; }
    public double CorpseApproachRangeMin { get; set; }

    // Navigation
    public bool BoostNavPriority { get; set; }
    public float FollowNavMin { get; set; }
    public float NavRingThickness { get; set; }
    public float NavLineThickness { get; set; }
    public float NavHeightOffset { get; set; }
    public bool ShowTerrainPassability { get; set; }
    public bool OpenDoors { get; set; }
    public float OpenDoorRange { get; set; }
    public bool AutoUnlockDoors { get; set; }
    public int MovementMode { get; set; }
    public float NavStopTurnAngle { get; set; }
    public float NavResumeTurnAngle { get; set; }
    public float NavDeadZone { get; set; }
    public float NavSweepMult { get; set; }
    public float PostPortalDelaySec { get; set; }
    public float T2Speed { get; set; }
    public float T2WalkWithinYd { get; set; }
    public float T2DistanceTo { get; set; }
    public float T2ReissueMs { get; set; }
    public float T2MaxRangeYd { get; set; }
    public int T2MaxLandblocks { get; set; }

    // Buffing
    public bool EnableBuffing { get; set; }
    public bool RebuffWhenIdle { get; set; }
    public int RebuffSecondsRemaining { get; set; }
    public int BuffMinSkillLevelTier1 { get; set; }
    public int BuffMinSkillLevelTier2 { get; set; }
    public int BuffMinSkillLevelTier3 { get; set; }
    public int BuffMinSkillLevelTier4 { get; set; }
    public int BuffMinSkillLevelTier5 { get; set; }
    public int BuffMinSkillLevelTier6 { get; set; }
    public int BuffMinSkillLevelTier7 { get; set; }
    public int BuffMinSkillLevelTier8 { get; set; }

    // Crafting (read-only state)
    public bool EnableMissileCrafting { get; set; }
    public string MissileCraftingState { get; set; } = string.Empty;
    public bool MissileCraftingActive { get; set; }
    public string MissileCraftingStatus { get; set; } = string.Empty;

    // Looting
    public bool EnableLooting { get; set; }
    public bool BoostLootPriority { get; set; }
    public bool LootOnlyRareCorpses { get; set; }
    public bool LootJumpEnabled { get; set; }
    public int LootJumpHeight { get; set; }
    public int LootOwnership { get; set; }
    public bool EnableAutostack { get; set; }
    public bool EnableCombineSalvage { get; set; }
    public bool CombineBagsDuringSalvage { get; set; }
    public int LootInterItemDelayMs { get; set; }
    public int LootContentSettleMs { get; set; }
    public int LootEmptyCorpseMs { get; set; }
    public int LootClosingDelayMs { get; set; }
    public int LootAssessWindowMs { get; set; }
    public int LootRetryTimeoutMs { get; set; }
    public int LootOpenRetryMs { get; set; }
    public int LootCorpseTimeoutMs { get; set; }
    public int SalvageOpenDelayFirstMs { get; set; }
    public int SalvageOpenDelayFastMs { get; set; }
    public int SalvageAddDelayFirstMs { get; set; }
    public int SalvageAddDelayFastMs { get; set; }
    public int SalvageSalvageDelayMs { get; set; }
    public int SalvageResultDelayFirstMs { get; set; }
    public int SalvageResultDelayFastMs { get; set; }
}

public enum MetaConditionType
{
    Never,
    Always,
    All,
    Any,
    ChatMessage,
    PackSlots_LE,
    SecondsInState_GE,
    CharacterDeath,
    AnyVendorOpen,
    VendorClosed,
    InventoryItemCount_LE,
    InventoryItemCount_GE,
    MonsterNameCountWithinDistance,
    MonsterPriorityCountWithinDistance,
    NeedToBuff,
    NoMonstersWithinDistance,
    Landblock_EQ,
    Landcell_EQ,
    PortalspaceEntered,
    PortalspaceExited,
    Not,
    SecondsInStateP_GE,
    TimeLeftOnSpell_GE,
    TimeLeftOnSpell_LE,
    BurdenPercentage_GE,
    DistAnyRoutePT_GE,
    Expression,
    ChatMessageCapture,
    NavrouteEmpty,
    MainHealthLE,
    MainHealthPHE,
    MainManaLE,
    MainManaPHE,
    MainStamLE,
    VitaePHE
}

public enum MetaActionType
{
    None,
    ChatCommand,
    SetMetaState,
    EmbeddedNavRoute,
    All,
    CallMetaState,
    ReturnFromCall,
    ExpressionAction,
    ChatExpression,
    SetWatchdog,
    ClearWatchdog,
    GetRAOption,
    SetRAOption,
    CreateView,
    DestroyView,
    DestroyAllViews
}

public sealed class MetaRule
{
    public string State { get; set; } = "Default";
    public MetaConditionType Condition { get; set; }
    public string ConditionData { get; set; } = string.Empty;
    public MetaActionType Action { get; set; }
    public string ActionData { get; set; } = string.Empty;
    public List<MetaRule> Children { get; set; } = new();
    public List<MetaRule> ActionChildren { get; set; } = new();

    [JsonIgnore]
    public bool HasFired { get; set; }

    /// <summary>Last time this rule's action executed — drives the red-flash UI.</summary>
    [JsonIgnore]
    public DateTime LastFiredAt { get; set; } = DateTime.MinValue;
}

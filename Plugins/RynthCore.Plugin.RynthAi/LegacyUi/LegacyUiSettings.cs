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

    public bool ShowTargetStaminaMana;

    public bool EnableMissileCrafting = true;
    public int MissileCraftAmmoThreshold = 1000;

    public int LootInterItemDelayMs = 100;
    public int LootContentSettleMs = 100;
    public int LootEmptyCorpseMs = 400;
    public int LootClosingDelayMs = 200;
    public int LootAssessWindowMs = 800;
    public int LootRetryTimeoutMs = 800;
    public int LootOpenRetryMs = 3000;
    public int LootCorpseTimeoutMs = 12000;

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

    public int BlacklistAttempts = 3;
    public int BlacklistTimeoutSec = 30;

    public int MeleeAttackPower = -1;
    public int MissileAttackPower = -1;
    public bool UseNativeAttack = true;
    public bool UseRecklessness;
    public int MeleeAttackHeight = 1;
    public int MissileAttackHeight = 1;

    public bool EnableFPSLimit = true;
    public int TargetFPSFocused = 60;
    public int TargetFPSBackground = 30;

    // Minimum buffed skill level required to cast spells of each tier.
    // Defaults are tuned above AC's hard minimums to avoid fizzles.
    public int MinSkillLevelTier1 = 35;
    public int MinSkillLevelTier2 = 85;
    public int MinSkillLevelTier3 = 135;
    public int MinSkillLevelTier4 = 185;
    public int MinSkillLevelTier5 = 235;
    public int MinSkillLevelTier6 = 285;
    public int MinSkillLevelTier7 = 335;
    public int MinSkillLevelTier8 = 435;

    public List<MonsterRule> MonsterRules { get; set; } = new();
    public List<ItemRule> ItemRules { get; set; } = new();
    public List<ConsumableRule> ConsumableRules { get; set; } = new();
    public List<BuffRule> BuffRules { get; set; } = new();
    public List<MetaRule> MetaRules { get; set; } = new();

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
    public bool WindowLocked;
    public float BgOpacity = 0.95f;
    public bool DashShowWeapons;
    public bool DashShowLua;
    public bool DashShowNavigation;
    public bool DashShowMacroRules;
    public bool DashShowMonsters;
    public bool DashShowDungeonMap;
    public bool  MapShowDoors     = true;
    public bool  MapShowCreatures = true;
    public bool  MapShowToolbar   = true;
    public float MapBgOpacity     = 1.0f;

    [JsonIgnore]
    public readonly string[] AdvancedTabs =
    {
        "Display", "Misc", "Recharge", "Melee Combat", "Spell Combat",
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

public sealed class ConsumableRule
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "General";
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

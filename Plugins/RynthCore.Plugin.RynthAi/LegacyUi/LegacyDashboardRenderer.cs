using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Meta;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

internal sealed class LegacyDashboardRenderer
{
    internal static readonly Vector4 ColTeal = new(0.15f, 0.85f, 0.90f, 1.00f);
    internal static readonly Vector4 ColAmber = new(0.91f, 0.70f, 0.20f, 1.00f);
    internal static readonly Vector4 ColGreen = new(0.25f, 0.85f, 0.45f, 1.00f);
    internal static readonly Vector4 ColTextDim = new(0.85f, 0.90f, 0.95f, 1.00f);
    internal static readonly Vector4 ColTextMute = new(0.55f, 0.65f, 0.75f, 1.00f);
    internal static readonly Vector4 ColHp = new(0.85f, 0.20f, 0.20f, 1.00f);
    internal static readonly Vector4 ColMana = new(0.15f, 0.55f, 0.95f, 1.00f);
    internal static readonly Vector4 ColBarBg = new(0.08f, 0.12f, 0.16f, 1.00f);
    internal static readonly Vector4 ColPanelBg = new(0.04f, 0.07f, 0.10f, 0.95f);
    internal static readonly Vector4 ColBtnOn = new(0.15f, 0.30f, 0.35f, 1.00f);
    internal static readonly Vector4 ColBtnFill = new(0.06f, 0.12f, 0.18f, 1.00f);
    internal static readonly Vector4 ColBtnHov = new(0.10f, 0.18f, 0.25f, 1.00f);
    internal static readonly Vector4 ColBtnAct = new(0.08f, 0.15f, 0.22f, 1.00f);
    internal static readonly Vector4 ColBtnBord = new(0.15f, 0.25f, 0.35f, 1.00f);

    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings = new();
    public LegacyUiSettings Settings => _settings;
    private readonly LegacyAdvancedSettingsUi _advancedSettingsUi;
    private readonly LegacyNavigationUi _navigationUi;
    private readonly LegacyLuaUi _luaUi;
    private readonly LegacyWeaponsUi _weaponsUi;
    private readonly LegacyMetaUi _metaUi;
    private readonly LegacyMonstersUi _monstersUi;
    public LegacyMonstersUi MonstersUi => _monstersUi;
    private readonly DungeonMapUi _dungeonMapUi;
    private readonly RynthRadarUi _rynthRadarUi;
    private readonly RynthChatUi _rynthChatUi;

    private readonly List<string> _profiles = new();
    private readonly List<string> _navFiles = new();
    private readonly List<string> _lootFiles = new();
    private readonly List<string> _metaFiles = new();
    private readonly string _navFolder = @"C:\Games\RynthSuite\RynthAi\NavProfiles";
    private readonly string _lootFolder = @"C:\Games\RynthSuite\RynthAi\LootProfiles";
    private readonly string _metaFolder = @"C:\Games\RynthSuite\RynthAi\MetaFiles";
    private readonly string _settingsRoot = @"C:\Games\RynthSuite\RynthAi\SettingsProfiles\ACEmulator";
    private readonly string _monstersFolder = @"C:\Games\RynthSuite\RynthAi\MonsterProfiles";

    private int _selectedNavIdx;
    private bool _isMinimized;
    private bool _isLocked;
    private bool _wasMinimized;
    private float _bgOpacity = 0.95f;

    public bool CloseRequested { get; internal set; }

    /// <summary>Wired by RynthAiPlugin to forward force-rebuff / cancel requests to BuffManager.</summary>
    public Action? OnForceRebuffRequested { get; set; }
    public Action? OnCancelForceRebuffRequested { get; set; }

    // ── Per-character settings persistence ───────────────────────────────────
    private string _charFolder = string.Empty;
    private string _settingsFilePath = string.Empty;
    private string _lastSavedJson = string.Empty;
    private int _saveCheckCounter;
    private const int SaveCheckIntervalFrames = 1; // check every frame for immediate save

    private Vector2 _lastWindowPos = new(-1, -1);
    private bool _windowPosRestored;
    private bool _windowSizeRestored;
    private Vector2 _expandedSize = new(430, 452);
    private string _targetLabel = "NO TARGET";
    private float _targetHealthPercent;
    private string _targetHealthDisplay = "0";
    private uint _targetHealth;
    private uint _targetMaxHealth;
    private uint _targetStamina;
    private uint _targetMaxStamina;
    private uint _targetMana;
    private uint _targetMaxMana;
    private uint _currentTargetId;
    private uint _playerHealth;
    private uint _playerMaxHealth;
    private uint _playerStamina;
    private uint _playerMaxStamina;
    private uint _playerMana;
    private uint _playerMaxMana;

    // ── Monster editor (external process) ────────────────────────────────────
    private FileSystemWatcher? _monsterWatcher;
    private volatile bool _monsterFileChanged;
    private System.Diagnostics.Process? _monsterEditorProcess;

    public LegacyDashboardRenderer(RynthCoreHost host)
    {
        _host = host;
        _advancedSettingsUi = new LegacyAdvancedSettingsUi(_settings);
        _navigationUi = new LegacyNavigationUi(_settings, host);
        _luaUi = new LegacyLuaUi(_settings, host);
        _weaponsUi = new LegacyWeaponsUi(_settings, host);
        _metaUi = new LegacyMetaUi(_settings, _navFiles);
        _monstersUi = new LegacyMonstersUi(
            _settings,
            host,
            onMonstersChanged: SaveMonstersFile,
            onLaunchExternalEditor: LaunchMonsterEditor,
            getCurrentTarget: GetCurrentTargetForMonsterAdd);
        _dungeonMapUi = new DungeonMapUi(host, _settings);
        _dungeonMapUi.OnSettingChanged = SaveSettings;
        _rynthRadarUi = new RynthRadarUi(host, _settings);
        _rynthRadarUi.OnSettingChanged = SaveSettings;
        _rynthRadarUi.SetMapData(_dungeonMapUi);
        _rynthChatUi = new RynthChatUi(host, _settings);
        _rynthChatUi.OnSettingChanged = SaveSettings;
        RefreshAllLists();
    }

    public void OnLoginComplete() => RefreshAllLists();

    /// <summary>
    /// Returns the current per-character folder (set during LoadSettings).
    /// Empty string until a character has logged in.
    /// </summary>
    public string CharFolder => _charFolder;

    public void SetWorldFilter(WorldObjectCache cache) => _weaponsUi.SetWorldFilter(cache);

    public void SetMissileCraftingManager(MissileCraftingManager mgr) => _advancedSettingsUi.SetMissileCraftingManager(mgr);

    public void SetRaycast(Raycasting.MainLogic raycast)
    {
        _dungeonMapUi.SetRaycast(raycast);
        _rynthRadarUi.SetRaycast(raycast);
    }

    public void PushChatLine(string? text, int chatType) => _rynthChatUi.Push(text, chatType);

    /// <summary>Set the handler invoked when the user submits a line from the
    /// custom chat widget. Wired by the plugin so slash commands can be routed
    /// through OnChatBarEnter before falling back to InvokeChatParser.</summary>
    public Action<string>? ChatSubmitHandler
    {
        get => _rynthChatUi.OnSubmit;
        set => _rynthChatUi.OnSubmit = value;
    }
    public void SetWorldObjectCache(WorldObjectCache cache)
    {
        _dungeonMapUi.SetWorldObjectCache(cache);
        _rynthRadarUi.SetWorldObjectCache(cache);
    }

    // ── Settings persistence ─────────────────────────────────────────────────

    public void LoadSettings(string charName)
    {
        if (string.IsNullOrWhiteSpace(charName)) return;

        string safeChar = SanitizeFileName(charName);
        _charFolder = Path.Combine(_settingsRoot, safeChar);

        // Migrate legacy settings.json → Default.json if needed
        string legacyPath = Path.Combine(_charFolder, "settings.json");
        string defaultProfilePath = GetProfileFilePath("Default");
        if (!File.Exists(defaultProfilePath) && File.Exists(legacyPath))
        {
            try { File.Copy(legacyPath, defaultProfilePath); } catch { }
        }

        // Determine which profile was last active
        string activeProfile = ReadActiveProfile();
        _settingsFilePath = GetProfileFilePath(activeProfile);

        // Fall back to Default if the profile file is missing
        if (!File.Exists(_settingsFilePath))
        {
            activeProfile = "Default";
            _settingsFilePath = GetProfileFilePath(activeProfile);
        }

        if (File.Exists(_settingsFilePath))
        {
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings);
                if (loaded != null)
                {
                    CopySettings(loaded, _settings);
                    _lastSavedJson = json;
                }
            }
            catch { }
        }

        _settings.SelectedProfile = activeProfile;
        ApplyUiStateFromSettings();

        _settings.IsMacroRunning = _settings.StartMacroOnLogin;
        _settings.CurrentState   = "Default";
        _settings.BotAction      = "Default";

        // Reload nav route from saved path
        if (!string.IsNullOrEmpty(_settings.CurrentNavPath) && File.Exists(_settings.CurrentNavPath))
        {
            try
            {
                _settings.CurrentRoute = NavRouteParser.Load(_settings.CurrentNavPath);
                _settings.ActiveNavIndex =
                    (_settings.CurrentRoute.RouteType == NavRouteType.Follow ||
                     _settings.CurrentRoute.RouteType == NavRouteType.Once)
                        ? 0
                        : FindNearestWaypoint(_settings.CurrentRoute);
            }
            catch { _settings.CurrentNavPath = string.Empty; }
        }

        // Auto-reload embedded navs from the last loaded meta source so
        // EmbedNav rules resolve after a restart without a manual reload.
        if (_settings.EmbeddedNavs.Count == 0 &&
            !string.IsNullOrEmpty(_settings.CurrentMetaPath) &&
            File.Exists(_settings.CurrentMetaPath))
        {
            try
            {
                string ext = Path.GetExtension(_settings.CurrentMetaPath).ToLowerInvariant();
                LoadedMeta reload = ext == ".met"
                    ? MetFileParser.Load(_settings.CurrentMetaPath)
                    : ext == ".af"
                        ? AfFileParser.Load(_settings.CurrentMetaPath)
                        : new LoadedMeta();
                foreach (var kvp in reload.EmbeddedNavs)
                    _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
            }
            catch { }
        }

        _windowPosRestored = false; // will apply saved position on next render
        _windowSizeRestored = false; // force size restore on first render after load
        RefreshAllLists();

        // Load MonsterRules from monsters.json (overrides what was in the profile)
        // and migrate existing rules to that file if it doesn't exist yet.
        MigrateMonstersToFile();
        LoadMonstersFromFile();
        // Always re-insert the catch-all "Default" rule at index 0; without it the
        // combat system has no fallback weapon/damage selection for unmatched mobs.
        _settings.EnsureDefaultRule();
        SetupMonsterWatcher();
    }

    public void SaveSettings()
    {
        if (string.IsNullOrEmpty(_settingsFilePath)) return;
        try
        {
            CaptureTransientUiState();
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
            string json = JsonSerializer.Serialize(_settings, RynthAiJsonContext.Default.LegacyUiSettings);
            File.WriteAllText(_settingsFilePath, json);
            _lastSavedJson = json;
            WriteActiveProfile(_settings.SelectedProfile);
        }
        catch { }
    }

    public string SaveAsProfile(string name)
    {
        if (string.IsNullOrEmpty(_charFolder)) return "Not logged in.";
        try
        {
            CaptureTransientUiState();
            Directory.CreateDirectory(_charFolder);
            string json = JsonSerializer.Serialize(_settings, RynthAiJsonContext.Default.LegacyUiSettings);
            string path = GetProfileFilePath(name);
            File.WriteAllText(path, json);
            _settings.SelectedProfile = name;
            _settingsFilePath = path;
            _lastSavedJson = json;
            WriteActiveProfile(name);
            RefreshProfilesList();
            return $"Saved profile '{name}'.";
        }
        catch (Exception ex) { return $"Save failed: {ex.Message}"; }
    }

    public string LoadProfile(string name)
    {
        if (string.IsNullOrEmpty(_charFolder)) return "Not logged in.";
        string path = GetProfileFilePath(name);
        if (!File.Exists(path))
            return SaveAsProfile(name);
        SwitchProfile(name);
        return $"Loaded profile '{name}'.";
    }

    private void CheckAndSave()
    {
        if (string.IsNullOrEmpty(_settingsFilePath)) return;
        try
        {
            CaptureTransientUiState();
            string json = JsonSerializer.Serialize(_settings, RynthAiJsonContext.Default.LegacyUiSettings);
            if (json != _lastSavedJson)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
                File.WriteAllText(_settingsFilePath, json);
                _lastSavedJson = json;
                WriteActiveProfile(_settings.SelectedProfile);
            }
        }
        catch { }
    }

    // ── monsters.json support ────────────────────────────────────────────────

    private string MonstersFilePath
    {
        get
        {
            if (string.IsNullOrEmpty(_charFolder)) return string.Empty;
            string charKey = Path.GetFileName(_charFolder); // e.g. "Toon Name"
            return Path.Combine(_monstersFolder, charKey + ".json");
        }
    }

    private string MonstersFilePathLegacy => string.IsNullOrEmpty(_charFolder)
        ? string.Empty
        : Path.Combine(_charFolder, "monsters.json");

    /// <summary>
    /// Moves any existing monsters.json from the old per-char settings folder into
    /// the new MonsterProfiles folder, then seeds from in-memory rules if still absent.
    /// </summary>
    private void MigrateMonstersToFile()
    {
        string path = MonstersFilePath;
        if (string.IsNullOrEmpty(path)) return;

        // Move legacy file if present and destination doesn't exist yet
        string legacyPath = MonstersFilePathLegacy;
        if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath) && !File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(_monstersFolder);
                File.Move(legacyPath, path);
                return; // moved; no need to seed from memory
            }
            catch { }
        }

        if (File.Exists(path)) return;
        if (_settings.MonsterRules.Count <= 1) return; // only Default, nothing to migrate

        try
        {
            Directory.CreateDirectory(_monstersFolder);
            string json = JsonSerializer.Serialize(_settings.MonsterRules, RynthAiJsonContext.Default.MonsterRuleList);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    /// <summary>Loads MonsterRules from monsters.json, overriding what came from the profile.</summary>
    private void LoadMonstersFromFile()
    {
        string path = MonstersFilePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            string json  = File.ReadAllText(path);
            var    rules = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MonsterRuleList);
            if (rules != null && rules.Count > 0)
                _settings.MonsterRules = rules;
        }
        catch { }
    }

    private void SetupMonsterWatcher()
    {
        _monsterWatcher?.Dispose();
        _monsterWatcher = null;

        string path = MonstersFilePath;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(_monstersFolder)) return;

        try
        {
            string charKey = Path.GetFileName(_charFolder);
            _monsterWatcher = new FileSystemWatcher(_monstersFolder, charKey + ".json")
            {
                NotifyFilter       = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _monsterWatcher.Changed += (_, _) => _monsterFileChanged = true;
        }
        catch { }
    }

    /// <summary>
    /// Call once per render frame. Hot-reloads MonsterRules when the external editor saves.
    /// </summary>
    public void TickMonsterReload()
    {
        if (!_monsterFileChanged) return;
        _monsterFileChanged = false;
        LoadMonstersFromFile();
    }

    /// <summary>Writes the in-memory MonsterRules to monsters.json so the external editor sees them.</summary>
    private void SaveMonstersFile()
    {
        string path = MonstersFilePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            // Suspend the watcher so our own write doesn't cause a redundant reload.
            bool wasEnabled = false;
            if (_monsterWatcher != null)
            {
                wasEnabled = _monsterWatcher.EnableRaisingEvents;
                _monsterWatcher.EnableRaisingEvents = false;
            }

            Directory.CreateDirectory(_monstersFolder);
            string json = JsonSerializer.Serialize(_settings.MonsterRules, RynthAiJsonContext.Default.MonsterRuleList);
            File.WriteAllText(path, json);

            if (_monsterWatcher != null)
                _monsterWatcher.EnableRaisingEvents = wasEnabled;
        }
        catch { }
    }

    // ── Radar bridge (used by the engine-side Avalonia radar panel) ─────────
    /// <summary>
    /// Builds a JSON snapshot for the Avalonia radar. Pass the engine's
    /// currently-cached MapVersion (0 on first call); when it matches the live
    /// landblock, walls/fills are omitted to keep the payload small.
    /// </summary>
    public string BuildRadarJson(uint engineKnownMapVersion)
        => _rynthRadarUi.BuildSnapshotJson(engineKnownMapVersion);

    // ── Monsters bridge (used by the engine-side Avalonia MonstersPanel) ────
    /// <summary>
    /// Builds a JSON payload describing the current MonsterRules + ItemRules
    /// (for weapon/offhand picker) + currently-selected target name (for the
    /// "Add Selected" button). Polled by the Avalonia panel.
    /// </summary>
    public string BuildMonstersJson()
    {
        try
        {
            var payload = new MonstersBridgePayload
            {
                Rules = _settings.MonsterRules ?? new List<MonsterRule>(),
                Items = (_settings.ItemRules ?? new List<ItemRule>())
                    .Select(r => new MonsterBridgeItem { Id = r.Id, Name = r.Name }).ToList(),
                CurrentTargetName = _currentTargetId != 0 ? (_targetLabel ?? string.Empty) : string.Empty,
            };

            // Annotate each rule with captured creature data when available.
            var store = CreatureLookupForRules;
            if (store != null && payload.Rules.Count > 0)
            {
                foreach (var rule in payload.Rules)
                {
                    if (string.IsNullOrEmpty(rule.Name)) continue;
                    var profile = store(rule.Name);
                    if (profile == null) continue;
                    var (weakType, weakVal) = CreatureData.CreatureProfileStore.GetWeakest(profile);
                    payload.Captured[rule.Name] = new MonsterCapturedInfo
                    {
                        MaxHealth   = profile.MaxHealth,
                        ArmorLevel  = profile.ArmorLevel,
                        WeakestType = weakType,
                        WeakestValue = weakVal,
                        Samples     = profile.Samples,
                    };
                }
            }
            return JsonSerializer.Serialize(payload, RynthAiJsonContext.Default.MonstersBridgePayload);
        }
        catch
        {
            return "{\"rules\":[],\"items\":[],\"currentTargetName\":\"\",\"captured\":{}}";
        }
    }

    /// <summary>Set by RynthAiPlugin so the snapshot can decorate rules with captured profile data.</summary>
    public Func<string, CreatureData.CreatureProfile?>? CreatureLookupForRules { get; set; }

    /// <summary>
    /// Replaces the in-memory MonsterRules from JSON sent by the Avalonia panel
    /// and saves to monsters.json. JSON shape: { "rules": [ MonsterRule, ... ] }.
    /// Default rule (Name="Default") is always preserved at index 0 — if absent,
    /// the existing one is kept; if present, it overrides.
    /// </summary>
    public void ApplyMonstersJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var payload = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MonstersBridgePayload);
            if (payload?.Rules == null) return;

            // Make sure a Default rule survives — every other code path assumes
            // it exists (CombatManager fallback at LegacyUiSettings:322).
            var incoming = payload.Rules;
            bool hasDefault = incoming.Any(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
            if (!hasDefault)
            {
                var existingDefault = _settings.MonsterRules
                    .FirstOrDefault(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
                if (existingDefault != null) incoming.Insert(0, existingDefault);
            }

            _settings.MonsterRules = incoming;
            SaveMonstersFile();
        }
        catch { }
    }

    // ── Settings bridge (engine-side Avalonia SettingsPanel) ─────────────────

    public string BuildSettingsJson()
    {
        try
        {
            var s = _settings;
            var payload = new SettingsBridgePayload
            {
                // Display
                ShowTargetStaminaMana      = s.ShowTargetStaminaMana,
                // UI
                SuppressRetailRadar        = s.SuppressRetailRadar,
                ShowRynthRadar             = s.ShowRynthRadar,
                RadarClickThrough          = s.RadarClickThrough,
                SuppressRetailChat         = s.SuppressRetailChat,
                ShowRynthChat              = s.ShowRynthChat,
                ChatClickThrough           = s.ChatClickThrough,
                SuppressRetailPowerbar     = s.SuppressRetailPowerbar,
                // Misc
                EnableFPSLimit             = s.EnableFPSLimit,
                TargetFPSFocused           = s.TargetFPSFocused,
                TargetFPSBackground        = s.TargetFPSBackground,
                EnableAutocram             = s.EnableAutocram,
                PeaceModeWhenIdle          = s.PeaceModeWhenIdle,
                StartMacroOnLogin          = s.StartMacroOnLogin,
                EnableRaycasting           = s.EnableRaycasting,
                UseArcs                    = s.UseArcs,
                BowArcVelocity             = s.BowArcVelocity,
                CrossbowArcVelocity        = s.CrossbowArcVelocity,
                AtlatlArcVelocity          = s.AtlatlArcVelocity,
                MagicArcVelocity           = s.MagicArcVelocity,
                BlacklistAttempts          = s.BlacklistAttempts,
                BlacklistTimeoutSec        = s.BlacklistTimeoutSec,
                TargetNoProgressTimeoutSec = s.TargetNoProgressTimeoutSec,
                GiveQueueIntervalMs        = s.GiveQueueIntervalMs,
                // Recharge
                HealAt                     = s.HealAt,
                RestamAt                   = s.RestamAt,
                GetManaAt                  = s.GetManaAt,
                TopOffHP                   = s.TopOffHP,
                TopOffStam                 = s.TopOffStam,
                TopOffMana                 = s.TopOffMana,
                HealOthersAt               = s.HealOthersAt,
                RestamOthersAt             = s.RestamOthersAt,
                InfuseOthersAt             = s.InfuseOthersAt,
                // Melee Combat
                UseRecklessness            = s.UseRecklessness,
                MeleeAttackPower           = s.MeleeAttackPower,
                MeleeAttackHeight          = s.MeleeAttackHeight,
                MissileAttackPower         = s.MissileAttackPower,
                MissileAttackHeight        = s.MissileAttackHeight,
                UseNativeAttack            = s.UseNativeAttack,
                SummonPets                 = s.SummonPets,
                PetMinMonsters             = s.PetMinMonsters,
                // Spell Combat
                SpellCastIntervalMs        = s.SpellCastIntervalMs,
                CastDispelSelf             = s.CastDispelSelf,
                MinRingTargets             = s.MinRingTargets,
                MinSkillLevelTier1         = s.MinSkillLevelTier1,
                MinSkillLevelTier2         = s.MinSkillLevelTier2,
                MinSkillLevelTier3         = s.MinSkillLevelTier3,
                MinSkillLevelTier4         = s.MinSkillLevelTier4,
                MinSkillLevelTier5         = s.MinSkillLevelTier5,
                MinSkillLevelTier6         = s.MinSkillLevelTier6,
                MinSkillLevelTier7         = s.MinSkillLevelTier7,
                MinSkillLevelTier8         = s.MinSkillLevelTier8,
                // Ranges
                MonsterRange               = s.MonsterRange,
                RingRange                  = s.RingRange,
                ApproachRange              = s.ApproachRange,
                CorpseApproachRangeMax     = s.CorpseApproachRangeMax,
                CorpseApproachRangeMin     = s.CorpseApproachRangeMin,
                // Navigation
                BoostNavPriority           = s.BoostNavPriority,
                FollowNavMin               = s.FollowNavMin,
                NavRingThickness           = s.NavRingThickness,
                NavLineThickness           = s.NavLineThickness,
                NavHeightOffset            = s.NavHeightOffset,
                ShowTerrainPassability     = s.ShowTerrainPassability,
                OpenDoors                  = s.OpenDoors,
                OpenDoorRange              = s.OpenDoorRange,
                AutoUnlockDoors            = s.AutoUnlockDoors,
                MovementMode               = s.MovementMode,
                NavStopTurnAngle           = s.NavStopTurnAngle,
                NavResumeTurnAngle         = s.NavResumeTurnAngle,
                NavDeadZone                = s.NavDeadZone,
                NavSweepMult               = s.NavSweepMult,
                PostPortalDelaySec         = s.PostPortalDelaySec,
                T2Speed                    = s.T2Speed,
                T2WalkWithinYd             = s.T2WalkWithinYd,
                T2DistanceTo               = s.T2DistanceTo,
                T2ReissueMs                = s.T2ReissueMs,
                T2MaxRangeYd               = s.T2MaxRangeYd,
                T2MaxLandblocks            = s.T2MaxLandblocks,
                // Buffing
                EnableBuffing              = s.EnableBuffing,
                RebuffWhenIdle             = s.RebuffWhenIdle,
                RebuffSecondsRemaining     = s.RebuffSecondsRemaining,
                BuffMinSkillLevelTier1     = s.BuffMinSkillLevelTier1,
                BuffMinSkillLevelTier2     = s.BuffMinSkillLevelTier2,
                BuffMinSkillLevelTier3     = s.BuffMinSkillLevelTier3,
                BuffMinSkillLevelTier4     = s.BuffMinSkillLevelTier4,
                BuffMinSkillLevelTier5     = s.BuffMinSkillLevelTier5,
                BuffMinSkillLevelTier6     = s.BuffMinSkillLevelTier6,
                BuffMinSkillLevelTier7     = s.BuffMinSkillLevelTier7,
                BuffMinSkillLevelTier8     = s.BuffMinSkillLevelTier8,
                // Crafting
                EnableMissileCrafting      = s.EnableMissileCrafting,
                MissileCraftingState       = _advancedSettingsUi.MissileCraftingState,
                MissileCraftingActive      = _advancedSettingsUi.MissileCraftingActive,
                MissileCraftingStatus      = _advancedSettingsUi.MissileCraftingStatus,
                // Looting
                EnableLooting              = s.EnableLooting,
                BoostLootPriority          = s.BoostLootPriority,
                LootOnlyRareCorpses        = s.LootOnlyRareCorpses,
                LootJumpEnabled            = s.LootJumpEnabled,
                LootJumpHeight             = s.LootJumpHeight,
                LootOwnership              = s.LootOwnership,
                EnableAutostack            = s.EnableAutostack,
                EnableCombineSalvage       = s.EnableCombineSalvage,
                CombineBagsDuringSalvage   = s.CombineBagsDuringSalvage,
                LootInterItemDelayMs       = s.LootInterItemDelayMs,
                LootContentSettleMs        = s.LootContentSettleMs,
                LootEmptyCorpseMs          = s.LootEmptyCorpseMs,
                LootClosingDelayMs         = s.LootClosingDelayMs,
                LootAssessWindowMs         = s.LootAssessWindowMs,
                LootRetryTimeoutMs         = s.LootRetryTimeoutMs,
                LootOpenRetryMs            = s.LootOpenRetryMs,
                LootCorpseTimeoutMs        = s.LootCorpseTimeoutMs,
                SalvageOpenDelayFirstMs    = s.SalvageOpenDelayFirstMs,
                SalvageOpenDelayFastMs     = s.SalvageOpenDelayFastMs,
                SalvageAddDelayFirstMs     = s.SalvageAddDelayFirstMs,
                SalvageAddDelayFastMs      = s.SalvageAddDelayFastMs,
                SalvageSalvageDelayMs      = s.SalvageSalvageDelayMs,
                SalvageResultDelayFirstMs  = s.SalvageResultDelayFirstMs,
                SalvageResultDelayFastMs   = s.SalvageResultDelayFastMs,
            };
            return JsonSerializer.Serialize(payload, RynthAiJsonContext.Default.SettingsBridgePayload);
        }
        catch
        {
            return "{}";
        }
    }

    public void ApplySettingsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var p = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.SettingsBridgePayload);
            if (p == null) return;
            var s = _settings;
            // Display
            s.ShowTargetStaminaMana      = p.ShowTargetStaminaMana;
            // UI
            s.SuppressRetailRadar        = p.SuppressRetailRadar;
            s.ShowRynthRadar             = p.ShowRynthRadar;
            s.RadarClickThrough          = p.RadarClickThrough;
            s.SuppressRetailChat         = p.SuppressRetailChat;
            s.ShowRynthChat              = p.ShowRynthChat;
            s.ChatClickThrough           = p.ChatClickThrough;
            s.SuppressRetailPowerbar     = p.SuppressRetailPowerbar;
            // Misc
            s.EnableFPSLimit             = p.EnableFPSLimit;
            s.TargetFPSFocused           = p.TargetFPSFocused;
            s.TargetFPSBackground        = p.TargetFPSBackground;
            s.EnableAutocram             = p.EnableAutocram;
            s.PeaceModeWhenIdle          = p.PeaceModeWhenIdle;
            s.StartMacroOnLogin          = p.StartMacroOnLogin;
            s.EnableRaycasting           = p.EnableRaycasting;
            s.UseArcs                    = p.UseArcs;
            s.BowArcVelocity             = p.BowArcVelocity;
            s.CrossbowArcVelocity        = p.CrossbowArcVelocity;
            s.AtlatlArcVelocity          = p.AtlatlArcVelocity;
            s.MagicArcVelocity           = p.MagicArcVelocity;
            s.BlacklistAttempts          = p.BlacklistAttempts;
            s.BlacklistTimeoutSec        = p.BlacklistTimeoutSec;
            s.TargetNoProgressTimeoutSec = p.TargetNoProgressTimeoutSec;
            s.GiveQueueIntervalMs        = p.GiveQueueIntervalMs;
            // Recharge
            s.HealAt                     = p.HealAt;
            s.RestamAt                   = p.RestamAt;
            s.GetManaAt                  = p.GetManaAt;
            s.TopOffHP                   = p.TopOffHP;
            s.TopOffStam                 = p.TopOffStam;
            s.TopOffMana                 = p.TopOffMana;
            s.HealOthersAt               = p.HealOthersAt;
            s.RestamOthersAt             = p.RestamOthersAt;
            s.InfuseOthersAt             = p.InfuseOthersAt;
            // Melee Combat
            s.UseRecklessness            = p.UseRecklessness;
            s.MeleeAttackPower           = p.MeleeAttackPower;
            s.MeleeAttackHeight          = p.MeleeAttackHeight;
            s.MissileAttackPower         = p.MissileAttackPower;
            s.MissileAttackHeight        = p.MissileAttackHeight;
            s.UseNativeAttack            = p.UseNativeAttack;
            s.SummonPets                 = p.SummonPets;
            s.PetMinMonsters             = p.PetMinMonsters;
            // Spell Combat
            s.SpellCastIntervalMs        = p.SpellCastIntervalMs;
            s.CastDispelSelf             = p.CastDispelSelf;
            s.MinRingTargets             = p.MinRingTargets;
            s.MinSkillLevelTier1         = p.MinSkillLevelTier1;
            s.MinSkillLevelTier2         = p.MinSkillLevelTier2;
            s.MinSkillLevelTier3         = p.MinSkillLevelTier3;
            s.MinSkillLevelTier4         = p.MinSkillLevelTier4;
            s.MinSkillLevelTier5         = p.MinSkillLevelTier5;
            s.MinSkillLevelTier6         = p.MinSkillLevelTier6;
            s.MinSkillLevelTier7         = p.MinSkillLevelTier7;
            s.MinSkillLevelTier8         = p.MinSkillLevelTier8;
            // Ranges
            s.MonsterRange               = p.MonsterRange;
            s.RingRange                  = p.RingRange;
            s.ApproachRange              = p.ApproachRange;
            s.CorpseApproachRangeMax     = p.CorpseApproachRangeMax;
            s.CorpseApproachRangeMin     = p.CorpseApproachRangeMin;
            // Navigation
            s.BoostNavPriority           = p.BoostNavPriority;
            s.FollowNavMin               = p.FollowNavMin;
            s.NavRingThickness           = p.NavRingThickness;
            s.NavLineThickness           = p.NavLineThickness;
            s.NavHeightOffset            = p.NavHeightOffset;
            s.ShowTerrainPassability     = p.ShowTerrainPassability;
            s.OpenDoors                  = p.OpenDoors;
            s.OpenDoorRange              = p.OpenDoorRange;
            s.AutoUnlockDoors            = p.AutoUnlockDoors;
            s.MovementMode               = p.MovementMode;
            s.NavStopTurnAngle           = p.NavStopTurnAngle;
            s.NavResumeTurnAngle         = p.NavResumeTurnAngle;
            s.NavDeadZone                = p.NavDeadZone;
            s.NavSweepMult               = p.NavSweepMult;
            s.PostPortalDelaySec         = p.PostPortalDelaySec;
            s.T2Speed                    = p.T2Speed;
            s.T2WalkWithinYd             = p.T2WalkWithinYd;
            s.T2DistanceTo               = p.T2DistanceTo;
            s.T2ReissueMs                = p.T2ReissueMs;
            s.T2MaxRangeYd               = p.T2MaxRangeYd;
            s.T2MaxLandblocks            = p.T2MaxLandblocks;
            // Buffing
            s.EnableBuffing              = p.EnableBuffing;
            s.RebuffWhenIdle             = p.RebuffWhenIdle;
            s.RebuffSecondsRemaining     = p.RebuffSecondsRemaining;
            s.BuffMinSkillLevelTier1     = p.BuffMinSkillLevelTier1;
            s.BuffMinSkillLevelTier2     = p.BuffMinSkillLevelTier2;
            s.BuffMinSkillLevelTier3     = p.BuffMinSkillLevelTier3;
            s.BuffMinSkillLevelTier4     = p.BuffMinSkillLevelTier4;
            s.BuffMinSkillLevelTier5     = p.BuffMinSkillLevelTier5;
            s.BuffMinSkillLevelTier6     = p.BuffMinSkillLevelTier6;
            s.BuffMinSkillLevelTier7     = p.BuffMinSkillLevelTier7;
            s.BuffMinSkillLevelTier8     = p.BuffMinSkillLevelTier8;
            // Crafting (EnableMissileCrafting is writable; state fields are read-only)
            s.EnableMissileCrafting      = p.EnableMissileCrafting;
            // Looting
            s.EnableLooting              = p.EnableLooting;
            s.BoostLootPriority          = p.BoostLootPriority;
            s.LootOnlyRareCorpses        = p.LootOnlyRareCorpses;
            s.LootJumpEnabled            = p.LootJumpEnabled;
            s.LootJumpHeight             = p.LootJumpHeight;
            s.LootOwnership              = p.LootOwnership;
            s.EnableAutostack            = p.EnableAutostack;
            s.EnableCombineSalvage       = p.EnableCombineSalvage;
            s.CombineBagsDuringSalvage   = p.CombineBagsDuringSalvage;
            s.LootInterItemDelayMs       = p.LootInterItemDelayMs;
            s.LootContentSettleMs        = p.LootContentSettleMs;
            s.LootEmptyCorpseMs          = p.LootEmptyCorpseMs;
            s.LootClosingDelayMs         = p.LootClosingDelayMs;
            s.LootAssessWindowMs         = p.LootAssessWindowMs;
            s.LootRetryTimeoutMs         = p.LootRetryTimeoutMs;
            s.LootOpenRetryMs            = p.LootOpenRetryMs;
            s.LootCorpseTimeoutMs        = p.LootCorpseTimeoutMs;
            s.SalvageOpenDelayFirstMs    = p.SalvageOpenDelayFirstMs;
            s.SalvageOpenDelayFastMs     = p.SalvageOpenDelayFastMs;
            s.SalvageAddDelayFirstMs     = p.SalvageAddDelayFirstMs;
            s.SalvageAddDelayFastMs      = p.SalvageAddDelayFastMs;
            s.SalvageSalvageDelayMs      = p.SalvageSalvageDelayMs;
            s.SalvageResultDelayFirstMs  = p.SalvageResultDelayFirstMs;
            s.SalvageResultDelayFastMs   = p.SalvageResultDelayFastMs;
            SaveSettings();
        }
        catch { }
    }

    // ── Items bridge (engine-side Avalonia ItemsPanel) ────────────────────────

    public string BuildItemsJson()
    {
        try
        {
            var payload = new ItemsBridgePayload
            {
                Weapons           = _settings.ItemRules ?? new List<ItemRule>(),
                Consumables       = _settings.ConsumableRules ?? new List<ConsumableRule>(),
                EnableManaTapping = _settings.EnableManaTapping,
                ManaTapMinMana    = _settings.ManaTapMinMana,
                ManaStoneKeepCount = _settings.ManaStoneKeepCount,
                CurrentTargetName = _currentTargetId != 0 ? (_targetLabel ?? string.Empty) : string.Empty,
            };
            return JsonSerializer.Serialize(payload, RynthAiJsonContext.Default.ItemsBridgePayload);
        }
        catch
        {
            return "{}";
        }
    }

    public void ApplyItemsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var p = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.ItemsBridgePayload);
            if (p == null) return;
            _settings.ItemRules          = p.Weapons;
            _settings.ConsumableRules    = p.Consumables;
            _settings.EnableManaTapping  = p.EnableManaTapping;
            _settings.ManaTapMinMana     = p.ManaTapMinMana;
            _settings.ManaStoneKeepCount = p.ManaStoneKeepCount;
            SaveSettings();
        }
        catch { }
    }

    public void AddSelectedWeapon()     { _weaponsUi.AddSelectedWeapon();     SaveSettings(); }
    public void AddSelectedConsumable() { _weaponsUi.AddSelectedConsumable(); SaveSettings(); }

    private (uint Id, string Name)? GetCurrentTargetForMonsterAdd()
    {
        if (_currentTargetId == 0) return null;
        string name = _targetLabel;
        if (_host.HasGetObjectName && _host.TryGetObjectName(_currentTargetId, out string resolved) && !string.IsNullOrWhiteSpace(resolved))
            name = resolved;
        if (string.IsNullOrWhiteSpace(name) || name == "NO TARGET") return null;
        return (_currentTargetId, name);
    }

    /// <summary>Launches the standalone Monster Rules editor for the current char folder.</summary>
    private void LaunchMonsterEditor()
    {
        if (string.IsNullOrEmpty(_charFolder))
        {
            _host.WriteToChat("[RynthAi] No character loaded — cannot open Monster Editor.", 4);
            return;
        }

        // Toggle: if the editor is already running, close it.
        if (_monsterEditorProcess != null && !_monsterEditorProcess.HasExited)
        {
            _monsterEditorProcess.CloseMainWindow();
            _monsterEditorProcess = null;
            return;
        }

        // Editor lives at: <RynthAi root>\MonsterEditor\RynthCore.MonsterEditor.exe
        string rynthAiRoot = Path.GetDirectoryName(Path.GetDirectoryName(_settingsRoot)!)!;
        string editorExe   = Path.Combine(rynthAiRoot, "MonsterEditor", "RynthCore.MonsterEditor.exe");

        if (!File.Exists(editorExe))
        {
            _host.WriteToChat($"[RynthAi] Monster Editor not found: {editorExe}", 4);
            return;
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName        = editorExe,
            Arguments       = $"\"{_charFolder}\"",
            UseShellExecute = true,
        };
        _monsterEditorProcess = System.Diagnostics.Process.Start(psi);
    }

    private void CaptureTransientUiState()
    {
        _settings.WindowLocked       = _isLocked;
        _settings.DashboardMinimized = _isMinimized;
        _settings.WindowSizeX        = _expandedSize.X;
        _settings.WindowSizeY        = _expandedSize.Y;
        _settings.BgOpacity          = _bgOpacity;
        _settings.DashShowWeapons    = DashWindows.ShowWeapons;
        _settings.DashShowLua        = DashWindows.ShowLua;
        _settings.DashShowNavigation = DashWindows.ShowNavigation;
        _settings.DashShowMacroRules = DashWindows.ShowMacroRules;
        _settings.DashShowMonsters   = DashWindows.ShowMonsters;
        _settings.DashShowDungeonMap = DashWindows.ShowDungeonMap;
    }

    private void ApplyUiStateFromSettings()
    {
        _isLocked     = _settings.WindowLocked;
        _isMinimized  = _settings.DashboardMinimized;
        _expandedSize = new Vector2(_settings.WindowSizeX, _settings.WindowSizeY);
        _bgOpacity    = _settings.BgOpacity;
        DashWindows.ShowWeapons     = _settings.DashShowWeapons;
        DashWindows.ShowLua         = _settings.DashShowLua;
        DashWindows.ShowNavigation  = _settings.DashShowNavigation;
        DashWindows.ShowMacroRules  = _settings.DashShowMacroRules;
        DashWindows.ShowMonsters    = _settings.DashShowMonsters;
        DashWindows.ShowDungeonMap = _settings.DashShowDungeonMap;
    }

    private string GetProfileFilePath(string profileName)
    {
        if (string.IsNullOrEmpty(_charFolder)) return string.Empty;
        return Path.Combine(_charFolder, profileName + ".json");
    }

    private string GetActiveProfileMarkerPath()
    {
        if (string.IsNullOrEmpty(_charFolder)) return string.Empty;
        return Path.Combine(_charFolder, "active_profile.txt");
    }

    private string ReadActiveProfile()
    {
        string markerPath = GetActiveProfileMarkerPath();
        if (!string.IsNullOrEmpty(markerPath) && File.Exists(markerPath))
        {
            try
            {
                string name = File.ReadAllText(markerPath).Trim();
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
        }
        return "Default";
    }

    private void WriteActiveProfile(string profileName)
    {
        string markerPath = GetActiveProfileMarkerPath();
        if (string.IsNullOrEmpty(markerPath)) return;
        try
        {
            Directory.CreateDirectory(_charFolder);
            File.WriteAllText(markerPath, profileName);
        }
        catch { }
    }

    private void SwitchProfile(string newProfile)
    {
        if (string.Equals(newProfile, _settings.SelectedProfile, StringComparison.OrdinalIgnoreCase))
            return;

        // Save current settings to current profile file
        SaveSettings();

        // Switch to the new profile
        _settings.SelectedProfile = newProfile;
        _settingsFilePath = GetProfileFilePath(newProfile);
        WriteActiveProfile(newProfile);

        // Load from new profile file if it exists
        if (File.Exists(_settingsFilePath))
        {
            try
            {
                string json = File.ReadAllText(_settingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings);
                if (loaded != null)
                {
                    CopySettings(loaded, _settings);
                    _settings.SelectedProfile = newProfile;
                    _lastSavedJson = json;
                }
            }
            catch { }
        }
        else
        {
            // New profile — force save current settings as its initial state
            _lastSavedJson = string.Empty;
        }

        ApplyUiStateFromSettings();

        // Reload nav route for the new profile
        _settings.CurrentRoute = new NavRouteParser();
        if (!string.IsNullOrEmpty(_settings.CurrentNavPath) && File.Exists(_settings.CurrentNavPath))
        {
            try
            {
                _settings.CurrentRoute = NavRouteParser.Load(_settings.CurrentNavPath);
                _settings.ActiveNavIndex =
                    (_settings.CurrentRoute.RouteType == NavRouteType.Follow ||
                     _settings.CurrentRoute.RouteType == NavRouteType.Once)
                        ? 0
                        : FindNearestWaypoint(_settings.CurrentRoute);
            }
            catch { _settings.CurrentNavPath = string.Empty; }
        }

        _windowPosRestored = false;
        _windowSizeRestored = false;
        RefreshAllLists();
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' || c == '-' ? c : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Copies all JSON-serializable fields from src into dst without replacing the
    /// object reference (so all existing bindings to _settings remain valid).
    /// </summary>
    private static void CopySettings(LegacyUiSettings src, LegacyUiSettings dst)
    {
        // Re-serialize src, then deserialize over dst's fields via reflection-free copy.
        // Simplest approach: copy field-by-field via JSON round-trip into dst.
        string json = JsonSerializer.Serialize(src, RynthAiJsonContext.Default.LegacyUiSettings);
        var tmp = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.LegacyUiSettings);
        if (tmp == null) return;

        // Copy all serialized fields manually (the source generator guarantees coverage)
        dst.EnableBuffing            = tmp.EnableBuffing;
        dst.EnableCombat             = tmp.EnableCombat;
        dst.EnableNavigation         = tmp.EnableNavigation;
        dst.EnableLooting            = tmp.EnableLooting;
        dst.EnableMeta               = tmp.EnableMeta;
        dst.EnableRaycasting         = tmp.EnableRaycasting;
        dst.MovementMode             = tmp.MovementMode;
        dst.NavStopTurnAngle         = tmp.NavStopTurnAngle;
        dst.NavResumeTurnAngle       = tmp.NavResumeTurnAngle;
        dst.NavDeadZone              = tmp.NavDeadZone;
        dst.NavSweepMult             = tmp.NavSweepMult;
        dst.PostPortalDelaySec       = tmp.PostPortalDelaySec;
        dst.T2Speed                  = tmp.T2Speed;
        dst.T2DistanceTo             = tmp.T2DistanceTo;
        dst.T2ReissueMs              = tmp.T2ReissueMs;
        dst.T2MaxRangeYd             = tmp.T2MaxRangeYd;
        dst.T2MaxLandblocks          = tmp.T2MaxLandblocks;
        dst.T2WalkWithinYd           = tmp.T2WalkWithinYd;
        dst.CurrentNavPath           = tmp.CurrentNavPath;
        dst.CurrentLootPath          = tmp.CurrentLootPath;
        dst.CurrentMetaPath          = tmp.CurrentMetaPath;
        dst.MacroSettingsIdx         = tmp.MacroSettingsIdx;
        dst.NavProfileIdx            = tmp.NavProfileIdx;
        dst.LootProfileIdx           = tmp.LootProfileIdx;
        dst.MetaProfileIdx           = tmp.MetaProfileIdx;
        dst.EnableAutostack          = tmp.EnableAutostack;
        dst.EnableAutocram           = tmp.EnableAutocram;
        dst.EnableCombineSalvage     = tmp.EnableCombineSalvage;
        dst.CombineBagsDuringSalvage = tmp.CombineBagsDuringSalvage;
        dst.ShowTargetStaminaMana    = tmp.ShowTargetStaminaMana;
        dst.EnableMissileCrafting    = tmp.EnableMissileCrafting;
        dst.MissileCraftAmmoThreshold= tmp.MissileCraftAmmoThreshold;
        dst.LootInterItemDelayMs     = tmp.LootInterItemDelayMs;
        dst.LootContentSettleMs      = tmp.LootContentSettleMs;
        dst.LootEmptyCorpseMs        = tmp.LootEmptyCorpseMs;
        dst.LootClosingDelayMs       = tmp.LootClosingDelayMs;
        dst.LootAssessWindowMs       = tmp.LootAssessWindowMs;
        dst.LootRetryTimeoutMs       = tmp.LootRetryTimeoutMs;
        dst.LootOpenRetryMs          = tmp.LootOpenRetryMs;
        dst.LootCorpseTimeoutMs      = tmp.LootCorpseTimeoutMs;
        dst.LootJumpEnabled          = tmp.LootJumpEnabled;
        dst.LootJumpHeight           = tmp.LootJumpHeight;
        dst.SalvageOpenDelayFirstMs  = tmp.SalvageOpenDelayFirstMs;
        dst.SalvageOpenDelayFastMs   = tmp.SalvageOpenDelayFastMs;
        dst.SalvageAddDelayFirstMs   = tmp.SalvageAddDelayFirstMs;
        dst.SalvageAddDelayFastMs    = tmp.SalvageAddDelayFastMs;
        dst.SalvageSalvageDelayMs    = tmp.SalvageSalvageDelayMs;
        dst.SalvageResultDelayFirstMs= tmp.SalvageResultDelayFirstMs;
        dst.SalvageResultDelayFastMs = tmp.SalvageResultDelayFastMs;
        dst.UseDispelItems           = tmp.UseDispelItems;
        dst.CastDispelSelf           = tmp.CastDispelSelf;
        dst.AutoFellowMgmt           = tmp.AutoFellowMgmt;
        dst.MChargesWhenOff          = tmp.MChargesWhenOff;
        dst.HealAt                   = tmp.HealAt;
        dst.RestamAt                 = tmp.RestamAt;
        dst.GetManaAt                = tmp.GetManaAt;
        dst.TopOffHP                 = tmp.TopOffHP;
        dst.TopOffStam               = tmp.TopOffStam;
        dst.TopOffMana               = tmp.TopOffMana;
        dst.HealOthersAt             = tmp.HealOthersAt;
        dst.RestamOthersAt           = tmp.RestamOthersAt;
        dst.InfuseOthersAt           = tmp.InfuseOthersAt;
        dst.MonsterRange             = tmp.MonsterRange;
        dst.RingRange                = tmp.RingRange;
        dst.ApproachRange            = tmp.ApproachRange;
        dst.MinRingTargets           = tmp.MinRingTargets;
        dst.FollowNavMin             = tmp.FollowNavMin;
        dst.NavRingThickness         = tmp.NavRingThickness;
        dst.NavLineThickness         = tmp.NavLineThickness;
        dst.NavHeightOffset          = tmp.NavHeightOffset;
        dst.MaxMonRange              = tmp.MaxMonRange;
        dst.SummonPets               = tmp.SummonPets;
        dst.CustomPetRange           = tmp.CustomPetRange;
        dst.PetMinMonsters           = tmp.PetMinMonsters;
        dst.AdvancedOptions          = tmp.AdvancedOptions;
        dst.MineOnly                 = tmp.MineOnly;
        dst.ShowEditor               = tmp.ShowEditor;
        dst.CorpseApproachRangeMax   = NormalizeCorpseRangeYards(tmp.CorpseApproachRangeMax, 10.0);
        dst.CorpseApproachRangeMin   = NormalizeCorpseRangeYards(tmp.CorpseApproachRangeMin, 2.0);
        dst.BoostNavPriority         = tmp.BoostNavPriority;
        dst.BoostLootPriority        = tmp.BoostLootPriority;
        dst.OpenDoors                = tmp.OpenDoors;
        dst.OpenDoorRange            = tmp.OpenDoorRange;
        dst.AutoUnlockDoors          = tmp.AutoUnlockDoors;
        dst.LootOwnership            = tmp.LootOwnership;
        dst.LootOnlyRareCorpses      = tmp.LootOnlyRareCorpses;
        dst.PeaceModeWhenIdle        = tmp.PeaceModeWhenIdle;
        dst.RebuffWhenIdle           = tmp.RebuffWhenIdle;
        dst.RebuffSecondsRemaining   = tmp.RebuffSecondsRemaining;
        dst.BlacklistAttempts             = tmp.BlacklistAttempts;
        dst.BlacklistTimeoutSec           = tmp.BlacklistTimeoutSec;
        dst.TargetNoProgressTimeoutSec    = tmp.TargetNoProgressTimeoutSec;
        dst.MeleeAttackPower         = tmp.MeleeAttackPower;
        dst.MissileAttackPower       = tmp.MissileAttackPower;
        dst.UseRecklessness          = tmp.UseRecklessness;
        dst.UseNativeAttack          = tmp.UseNativeAttack;
        dst.MeleeAttackHeight        = tmp.MeleeAttackHeight;
        dst.MissileAttackHeight      = tmp.MissileAttackHeight;
        dst.UseArcs                  = tmp.UseArcs;
        dst.BowArcVelocity           = tmp.BowArcVelocity;
        dst.CrossbowArcVelocity      = tmp.CrossbowArcVelocity;
        dst.AtlatlArcVelocity        = tmp.AtlatlArcVelocity;
        dst.MagicArcVelocity         = tmp.MagicArcVelocity;
        dst.EnableFPSLimit           = tmp.EnableFPSLimit;
        dst.TargetFPSFocused         = tmp.TargetFPSFocused;
        dst.TargetFPSBackground      = tmp.TargetFPSBackground;
        // MonsterRule deep-copy preserves Category + MatchExpression via JSON round-trip
        dst.MonsterRules             = tmp.MonsterRules;
        dst.ItemRules                = tmp.ItemRules;
        dst.ConsumableRules          = tmp.ConsumableRules;
        dst.BuffRules                = tmp.BuffRules;
        dst.MetaRules                = tmp.MetaRules;
        dst.LuaScript                = tmp.LuaScript;
        dst.SelectedProfile          = tmp.SelectedProfile;
        dst.ActiveNavIndex           = tmp.ActiveNavIndex;
        dst.ShowAdvancedWindow       = tmp.ShowAdvancedWindow;
        dst.SelectedAdvancedTab      = tmp.SelectedAdvancedTab;
        dst.WindowPosX               = tmp.WindowPosX;
        dst.WindowPosY               = tmp.WindowPosY;
        dst.WindowLocked             = tmp.WindowLocked;
        dst.DashboardVisible         = tmp.DashboardVisible;
        dst.DashboardMinimized       = tmp.DashboardMinimized;
        dst.WindowSizeX              = tmp.WindowSizeX;
        dst.WindowSizeY              = tmp.WindowSizeY;
        dst.BgOpacity                = tmp.BgOpacity;
        dst.DashShowWeapons          = tmp.DashShowWeapons;
        dst.DashShowLua              = tmp.DashShowLua;
        dst.DashShowNavigation       = tmp.DashShowNavigation;
        dst.DashShowMacroRules       = tmp.DashShowMacroRules;
        dst.DashShowMonsters         = tmp.DashShowMonsters;
        dst.DashShowDungeonMap       = tmp.DashShowDungeonMap;
        dst.MapShowDoors             = tmp.MapShowDoors;
        dst.MapShowCreatures         = tmp.MapShowCreatures;
        dst.MapShowToolbar           = tmp.MapShowToolbar;
        dst.MapBgOpacity             = tmp.MapBgOpacity;
        dst.MapRotateWithPlayer      = tmp.MapRotateWithPlayer;
        dst.ShowRadarWalls           = tmp.ShowRadarWalls;
        dst.RadarWallWorldRange      = tmp.RadarWallWorldRange;
        dst.MinSkillLevelTier1       = tmp.MinSkillLevelTier1;
        dst.MinSkillLevelTier2       = tmp.MinSkillLevelTier2;
        dst.MinSkillLevelTier3       = tmp.MinSkillLevelTier3;
        dst.MinSkillLevelTier4       = tmp.MinSkillLevelTier4;
        dst.MinSkillLevelTier5       = tmp.MinSkillLevelTier5;
        dst.MinSkillLevelTier6       = tmp.MinSkillLevelTier6;
        dst.MinSkillLevelTier7       = tmp.MinSkillLevelTier7;
        dst.MinSkillLevelTier8       = tmp.MinSkillLevelTier8;
        dst.BuffMinSkillLevelTier1   = tmp.BuffMinSkillLevelTier1;
        dst.BuffMinSkillLevelTier2   = tmp.BuffMinSkillLevelTier2;
        dst.BuffMinSkillLevelTier3   = tmp.BuffMinSkillLevelTier3;
        dst.BuffMinSkillLevelTier4   = tmp.BuffMinSkillLevelTier4;
        dst.BuffMinSkillLevelTier5   = tmp.BuffMinSkillLevelTier5;
        dst.BuffMinSkillLevelTier6   = tmp.BuffMinSkillLevelTier6;
        dst.BuffMinSkillLevelTier7   = tmp.BuffMinSkillLevelTier7;
        dst.BuffMinSkillLevelTier8   = tmp.BuffMinSkillLevelTier8;
        dst.EnableManaTapping        = tmp.EnableManaTapping;
        dst.ManaTapMinMana           = tmp.ManaTapMinMana;
        dst.ManaStoneKeepCount       = tmp.ManaStoneKeepCount;
        dst.MetaDebug                = tmp.MetaDebug;
        dst.StartMacroOnLogin        = tmp.StartMacroOnLogin;
        dst.ShowTerrainPassability   = tmp.ShowTerrainPassability;
        dst.GiveQueueIntervalMs      = tmp.GiveQueueIntervalMs;
        dst.SpellCastIntervalMs      = tmp.SpellCastIntervalMs;
        dst.EmbeddedNavs             = tmp.EmbeddedNavs;
        dst.SuppressRetailRadar      = tmp.SuppressRetailRadar;
        dst.ShowRynthRadar           = tmp.ShowRynthRadar;
        dst.RadarRotateWithPlayer    = tmp.RadarRotateWithPlayer;
        dst.RadarOpacity             = tmp.RadarOpacity;
        dst.RadarZoom                = tmp.RadarZoom;
        dst.RadarShowMonsters        = tmp.RadarShowMonsters;
        dst.RadarShowNpcs            = tmp.RadarShowNpcs;
        dst.RadarShowPortals         = tmp.RadarShowPortals;
        dst.RadarShowDoors           = tmp.RadarShowDoors;
        dst.RadarWallPaintRadius     = tmp.RadarWallPaintRadius;
        dst.RadarCircular            = tmp.RadarCircular;
        dst.RadarClickThrough        = tmp.RadarClickThrough;
        dst.SuppressRetailChat       = tmp.SuppressRetailChat;
        dst.SuppressRetailPowerbar   = tmp.SuppressRetailPowerbar;
        dst.ShowRynthChat            = tmp.ShowRynthChat;
        dst.ChatOpacity              = tmp.ChatOpacity;
        dst.ChatMaxLines             = tmp.ChatMaxLines;
        dst.ChatShowTimestamps       = tmp.ChatShowTimestamps;
        dst.ChatClickThrough         = tmp.ChatClickThrough;
    }

    private static double NormalizeCorpseRangeYards(double value, double fallbackYards)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            return fallbackYards;

        // Older settings stored corpse ranges in 240-based world units. Convert those
        // to direct yard values so the UI can use whole numbers like 5 or 6.
        return value <= 1.0 ? value * 240.0 : value;
    }


    public void SetSelectedTarget(uint targetId)
    {
        _currentTargetId = targetId;
        _targetLabel = "NO TARGET";
        if (targetId != 0)
        {
            _targetLabel = _host.HasGetObjectName && _host.TryGetObjectName(targetId, out string name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : $"TARGET 0x{targetId:X8}";
        }
        _targetHealthPercent = 0f;
        _targetHealth = 0;
        _targetMaxHealth = 0;
        _targetStamina = 0;
        _targetMaxStamina = 0;
        _targetMana = 0;
        _targetMaxMana = 0;
        _targetHealthDisplay = targetId == 0 ? "0" : "--";
        if (targetId != 0 && _host.HasQueryHealth) _host.QueryHealth(targetId);
    }

    public void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        if (targetId == 0 || targetId != _currentTargetId)
            return;

        _targetHealthPercent = Math.Clamp(healthRatio, 0f, 1f);
        if (maxHealth > 0)
        {
            _targetHealth = currentHealth;
            _targetMaxHealth = maxHealth;
            _targetHealthDisplay = $"{currentHealth}/{maxHealth}";
        }
        else
        {
            // Try to resolve absolute health from previously identified creature data.
            uint storedMax = 0;
            var lookup = CreatureLookupForRules;
            if (lookup != null && !string.IsNullOrEmpty(_targetLabel))
            {
                var profile = lookup(_targetLabel);
                if (profile != null && profile.MaxHealth > 0)
                    storedMax = profile.MaxHealth;
            }

            if (storedMax > 0)
            {
                _targetMaxHealth = storedMax;
                _targetHealth = (uint)Math.Round(_targetHealthPercent * storedMax);
                _targetHealthDisplay = $"{_targetHealth}/{storedMax}";
            }
            else
            {
                _targetHealth = 0;
                _targetMaxHealth = 0;
                _targetHealthDisplay = $"{(int)(_targetHealthPercent * 100)}%";
            }
        }

        // Refresh stamina/mana from the creature vitals cache
        if (_host.HasGetTargetVitals &&
            _host.TryGetTargetVitals(targetId, out _, out _, out uint st, out uint maxSt, out uint mn, out uint maxMn))
        {
            _targetStamina = st;
            _targetMaxStamina = maxSt;
            _targetMana = mn;
            _targetMaxMana = maxMn;
        }
    }

    public void Render()
    {
        RefreshPlayerVitals();

        // Periodic dirty-check: serialize settings every ~2s; save if changed
        if (++_saveCheckCounter >= SaveCheckIntervalFrames)
        {
            _saveCheckCounter = 0;
            CheckAndSave();
        }

        // Plugin-wide style overrides: muted slate-blue accents instead of the
        // previous vivid sky-blue, which was overpowering in the macro tab.
        ImGui.PushStyleColor(ImGuiCol.FrameBg,         new Vector4(0.18f, 0.22f, 0.28f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered,  new Vector4(0.24f, 0.30f, 0.38f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,   new Vector4(0.30f, 0.38f, 0.48f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark,       new Vector4(0.85f, 0.90f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg,         new Vector4(0.10f, 0.14f, 0.20f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Header,          new Vector4(0.20f, 0.28f, 0.36f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,   new Vector4(0.26f, 0.36f, 0.46f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,    new Vector4(0.32f, 0.44f, 0.58f, 1.00f));
        // Title bars — same slate-blue family. TitleBgActive replaces ImGui's
        // default vivid yellow that's especially jarring on detached viewports.
        ImGui.PushStyleColor(ImGuiCol.TitleBg,         new Vector4(0.14f, 0.18f, 0.24f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive,   new Vector4(0.22f, 0.30f, 0.40f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed,new Vector4(0.10f, 0.14f, 0.20f, 0.85f));

        try
        {
            RenderDashboard();

            _metaUi.Render();
            TickMonsterReload();
            _monstersUi.Render();
            _weaponsUi.Render();

            // Hooked up the new Lua UI here
            _luaUi.Render();

            if (DashWindows.ShowNavigation) _navigationUi.Render();
            if (_settings.ShowAdvancedWindow) _advancedSettingsUi.Render();
        }
        finally
        {
            ImGui.PopStyleColor(11);
        }
    }

    // Rendered every frame regardless of whether the main dashboard is visible.
    public void RenderMapWindow()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0.15f, 0.55f, 0.95f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.22f, 0.62f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0.10f, 0.45f, 0.82f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark,      new Vector4(0.00f, 0.00f, 0.00f, 1.00f));
        try
        {
            if (DashWindows.ShowDungeonMap || _dungeonMapUi.IsAutoHidden)
                _dungeonMapUi.Render();
            _rynthRadarUi.Render();
            _rynthChatUi.Render();
        }
        finally
        {
            ImGui.PopStyleColor(4);
        }
    }

    private void RenderDashboard()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6.0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.04f, 0.06f, 0.08f, _bgOpacity));
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        if (_isMinimized) { flags |= ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize; _wasMinimized = true; }
        else if (_isLocked) flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;
        // Restore saved window position on first render after login. Sanity-
        // check it against the current main viewport size — if the dashboard
        // was dragged partly off-screen on a previous session, or the AC
        // client window shrank since, the saved coords would render the
        // dashboard somewhere invisible. Snap back to (50, 50) in that case.
        if (!_windowPosRestored)
        {
            Vector2 vpSize = ImGui.GetMainViewport().Size;
            float maxX = vpSize.X > 0 ? vpSize.X - 100 : 8000;
            float maxY = vpSize.Y > 0 ? vpSize.Y - 100 : 6000;
            float posX = _settings.WindowPosX;
            float posY = _settings.WindowPosY;
            bool inBounds =
                posX >= 0 && posX <= maxX &&
                posY >= 0 && posY <= maxY;
            if (!inBounds)
            {
                posX = 50;
                posY = 50;
                _settings.WindowPosX = posX;
                _settings.WindowPosY = posY;
            }
            ImGui.SetNextWindowPos(new Vector2(posX, posY), ImGuiCond.Always);
            _windowPosRestored = true;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(400, 0), new Vector2(1200, 2000));
        if (!_isMinimized && !_isLocked)
        {
            // Force-apply the saved expanded size on the first frame after login —
            // imgui.ini may have a stale tiny size from a previous session.
            if (!_windowSizeRestored)
            {
                ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.Always);
                ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
                _windowSizeRestored = true;
            }
            else if (_wasMinimized) { ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.Always); _wasMinimized = false; }
            else ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.FirstUseEver);
        }
        else if (_isMinimized && !_windowSizeRestored)
        {
            // Even in minimized mode, defensively un-collapse so an old imgui.ini
            // Collapsed=1 entry can't keep the window iconified.
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
            _windowSizeRestored = true;
        }
        if (ImGui.Begin("RynthAi Dashboard##Main", flags))
        {
            if (!_isMinimized && !_isLocked) _expandedSize = ImGui.GetWindowSize();
            RenderDashHeader();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ColPanelBg);
            ImGui.PushStyleColor(ImGuiCol.Border, ColBtnBord);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
            if (ImGui.BeginChild("CombatPanel", new Vector2(-1, 200), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)) { RenderCombatPanel(); ImGui.Dummy(new Vector2(0, 2)); }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
            if (!_isMinimized) { ImGui.Spacing(); ImGui.Spacing(); RenderLauncherGrid(); }
        }
        // Capture window position for persistence
        Vector2 curPos = ImGui.GetWindowPos();
        if (curPos != _lastWindowPos)
        {
            _settings.WindowPosX = curPos.X;
            _settings.WindowPosY = curPos.Y;
            _lastWindowPos = curPos;
        }

        ImGui.End();
        ImGui.PopStyleColor(1);
        ImGui.PopStyleVar(3);
    }

    private void RenderDashHeader()
    {
        float width = ImGui.GetContentRegionAvail().X;
        float startY = ImGui.GetCursorPosY();
        ImGui.SetWindowFontScale(1.4f);
        ImGui.TextColored(ColTeal, "R");
        ImGui.SameLine(0, 2);
        ImGui.TextColored(new Vector4(1, 1, 1, 1), "YNTHAI DASHBOARD");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.SameLine();
        ImGui.SetCursorPosY(startY + 5);
        ImGui.TextColored(ColTextMute, "v4.0");
        ImGui.SameLine(width - 130);
        ImGui.SetCursorPosY(startY + 2);
        if (ImGui.SmallButton(_isLocked ? "Unlk" : "Lock")) _isLocked = !_isLocked;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(_isLocked ? "Unlock Window" : "Lock Window");
        ImGui.SameLine();
        if (ImGui.SmallButton("-")) _bgOpacity = Math.Max(0.1f, _bgOpacity - 0.1f);
        ImGui.SameLine();
        if (ImGui.SmallButton("+")) _bgOpacity = Math.Min(1.0f, _bgOpacity + 0.1f);
        ImGui.SameLine();
        if (ImGui.SmallButton(_isMinimized ? "^" : "_")) { _isMinimized = !_isMinimized; SaveSettings(); }
        ImGui.SameLine();
        if (ImGui.SmallButton("X")) CloseRequested = true;
        ImGui.Dummy(new Vector2(0, 2));
        if (_isMinimized) return;
        if (!ImGui.BeginTable("HeaderGrid", 2)) return;

        ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthFixed, width * 0.40f);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        // ── Left column: macro button + status ──────────────────────────
        ImGui.TableNextColumn();

        var btnColor = _settings.IsMacroRunning
            ? new Vector4(0.10f, 0.35f, 0.15f, 1.00f)
            : new Vector4(0.25f, 0.12f, 0.12f, 1.00f);
        var btnHover = _settings.IsMacroRunning
            ? new Vector4(0.15f, 0.50f, 0.22f, 1.00f)
            : new Vector4(0.40f, 0.18f, 0.18f, 1.00f);
        var btnActive = _settings.IsMacroRunning
            ? new Vector4(0.08f, 0.28f, 0.12f, 1.00f)
            : new Vector4(0.20f, 0.10f, 0.10f, 1.00f);

        ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
        ImGui.SetWindowFontScale(1.2f);

        Vector2 pos = ImGui.GetCursorScreenPos();
        string macroLabel = _settings.IsMacroRunning ? "RUNNING##ToggleMacro" : "STOPPED##ToggleMacro";
        if (ImGui.Button(macroLabel, new Vector2(120, 28)))
            _settings.IsMacroRunning = !_settings.IsMacroRunning;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to Start / Stop Macro");

        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);

        // Status circle beside the button
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        uint circleColor = _settings.IsMacroRunning ? ImGui.ColorConvertFloat4ToU32(ColGreen) : ImGui.ColorConvertFloat4ToU32(ColTextMute);
        Vector2 circlePos = pos + new Vector2(138, 14);
        dl.AddCircleFilled(circlePos, 5, circleColor);
        if (_settings.IsMacroRunning) dl.AddCircle(circlePos, 8, circleColor, 12, 1.5f);

        ImGui.Spacing();
        ImGui.TextColored(ColTextMute, "Meta State:");
        ImGui.SameLine(0, 8);
        ImGui.TextColored(ColAmber, _settings.CurrentState);
        ImGui.TextColored(ColTextMute, "Bot Activity:");
        ImGui.SameLine(0, 8);
        string botDisplay = string.IsNullOrEmpty(_settings.BotAction) || _settings.BotAction == "Default" ? "Idle" : _settings.BotAction;
        ImGui.TextColored(ColAmber, botDisplay);

        // ── Right column: file dropdowns (independent vertical layout) ──
        ImGui.TableNextColumn();

        ImGui.TextColored(ColTextMute, "Profile:");
        ImGui.SameLine(60);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##ProfCombo", TruncateName(_settings.SelectedProfile, 16)))
        {
            string? pendingSwitch = null;
            foreach (string profile in _profiles)
                if (ImGui.Selectable(profile, profile == _settings.SelectedProfile))
                    pendingSwitch = profile;
            ImGui.EndCombo();
            if (pendingSwitch != null) SwitchProfile(pendingSwitch);
        }

        ImGui.TextColored(ColTextMute, "Nav:");
        ImGui.SameLine(60);
        ImGui.SetNextItemWidth(-1);
        string navName = string.IsNullOrEmpty(_settings.CurrentNavPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentNavPath);
        if (ImGui.BeginCombo("##NavCombo", TruncateName(navName, 16)))
        {
            for (int i = 0; i < _navFiles.Count; i++)
                if (ImGui.Selectable(_navFiles[i], _selectedNavIdx == i)) { _selectedNavIdx = i; LoadSelectedNav(); }
            ImGui.EndCombo();
        }

        ImGui.TextColored(ColTextMute, "Loot:");
        ImGui.SameLine(60);
        ImGui.SetNextItemWidth(-1);
        string lootName = string.IsNullOrEmpty(_settings.CurrentLootPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentLootPath);
        if (ImGui.BeginCombo("##LootCombo", TruncateName(lootName, 16)))
        {
            for (int i = 0; i < _lootFiles.Count; i++)
                if (ImGui.Selectable(_lootFiles[i], _settings.LootProfileIdx == i))
                {
                    _settings.LootProfileIdx = i;
                    _settings.CurrentLootPath = i == 0 ? string.Empty : Path.Combine(_lootFolder, _lootFiles[i]);
                    SaveSettings();
                }
            ImGui.EndCombo();
        }

        ImGui.TextColored(ColTextMute, "Meta:");
        ImGui.SameLine(60);
        ImGui.SetNextItemWidth(-1);
        string metaName = string.IsNullOrEmpty(_settings.CurrentMetaPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentMetaPath);
        if (ImGui.BeginCombo("##MetaCombo", TruncateName(metaName, 16)))
        {
            for (int i = 0; i < _metaFiles.Count; i++)
                if (ImGui.Selectable(_metaFiles[i], _settings.MetaProfileIdx == i))
                {
                    _settings.MetaProfileIdx = i;
                    string path = i == 0 ? string.Empty : Path.Combine(_metaFolder, _metaFiles[i]);
                    _metaUi.LoadMacroFile(path);
                }
            ImGui.EndCombo();
        }

        ImGui.EndTable();
        ImGui.Spacing();
    }

    private void RenderMinimizedMacroButton()
    {
        var btnColor = _settings.IsMacroRunning
            ? new Vector4(0.10f, 0.35f, 0.15f, 1.00f)
            : new Vector4(0.25f, 0.12f, 0.12f, 1.00f);
        var btnHover = _settings.IsMacroRunning
            ? new Vector4(0.15f, 0.50f, 0.22f, 1.00f)
            : new Vector4(0.40f, 0.18f, 0.18f, 1.00f);
        var btnActive = _settings.IsMacroRunning
            ? new Vector4(0.08f, 0.28f, 0.12f, 1.00f)
            : new Vector4(0.20f, 0.10f, 0.10f, 1.00f);

        ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3.0f);

        string label = _settings.IsMacroRunning ? "Running##MinMacro" : "Stopped##MinMacro";
        if (ImGui.SmallButton(label))
            _settings.IsMacroRunning = !_settings.IsMacroRunning;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click to Start / Stop Macro");

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);
    }

    private void RenderCombatPanel()
    {
        if (!ImGui.BeginTable("CombatInnerTable", 2, ImGuiTableFlags.None)) return;
        ImGui.TableSetupColumn("Toggles", ImGuiTableColumnFlags.WidthFixed, 68);
        ImGui.TableSetupColumn("Vitals", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (_isMinimized)
        {
            var btnColor = _settings.IsMacroRunning
                ? new Vector4(0.10f, 0.35f, 0.15f, 1.00f)
                : new Vector4(0.25f, 0.12f, 0.12f, 1.00f);
            var btnHover = _settings.IsMacroRunning
                ? new Vector4(0.15f, 0.50f, 0.22f, 1.00f)
                : new Vector4(0.40f, 0.18f, 0.18f, 1.00f);
            var btnActive = _settings.IsMacroRunning
                ? new Vector4(0.08f, 0.28f, 0.12f, 1.00f)
                : new Vector4(0.20f, 0.10f, 0.10f, 1.00f);
            ImGui.PushStyleColor(ImGuiCol.Button, btnColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, btnHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, btnActive);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3.0f);
            string mLabel = _settings.IsMacroRunning ? "ON##MinMacro" : "OFF##MinMacro";
            if (ImGui.Button(mLabel, new Vector2(64, 20)))
                _settings.IsMacroRunning = !_settings.IsMacroRunning;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(_settings.IsMacroRunning ? "Macro Running - Click to Stop" : "Macro Stopped - Click to Start");
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);
        }
        Vector2 togglePos = ImGui.GetCursorScreenPos() + new Vector2(2, _isMinimized ? 6 : 28);

        // Right-click on each main toggle opens the matching settings window/tab —
        // a quick shortcut so users don't have to hunt through Advanced Settings.
        LegacyDashboardDrawing.DrawSquareToggle("sword", ref _settings.EnableCombat, togglePos, "CombatTgl");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) OpenAdvancedTab("Melee Combat");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Combat — left-click to toggle, right-click for settings");

        LegacyDashboardDrawing.DrawSquareToggle("buff", ref _settings.EnableBuffing, togglePos + new Vector2(34, 0), "BuffTgl");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) OpenAdvancedTab("Buffing");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Buffing — left-click to toggle, right-click for settings");

        LegacyDashboardDrawing.DrawSquareToggle("shoe", ref _settings.EnableNavigation, togglePos + new Vector2(0, 34), "NavTgl");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) DashWindows.ShowNavigation = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Navigation — left-click to toggle, right-click for routes");

        LegacyDashboardDrawing.DrawSquareToggle("bag", ref _settings.EnableLooting, togglePos + new Vector2(34, 34), "LootTgl");
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) OpenAdvancedTab("Looting");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Looting — left-click to toggle, right-click for settings");

        LegacyDashboardDrawing.DrawWideToggle("MACRO", "gear", ref _settings.EnableMeta, togglePos + new Vector2(0, 68), "MetaTgl", 64f, 20f);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) DashWindows.ShowMacroRules = true;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Macro/Meta — left-click to toggle, right-click for rules");

        // FR (Force Rebuff) — small button, below MACRO toggle
        Vector2 frPos = togglePos + new Vector2(0, 92);
        ImGui.SetCursorScreenPos(frPos);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.28f, 0.20f, 0.04f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.46f, 0.33f, 0.06f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.20f, 0.14f, 0.03f, 1.00f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3.0f);
        if (ImGui.Button("FR##ForceRebuff", new Vector2(64, 16)))
            OnForceRebuffRequested?.Invoke();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Force-recast all buffs.\nRight-click to cancel.");
        if (ImGui.BeginPopupContextItem("##FRCtx"))
        {
            if (ImGui.MenuItem("Cancel Force Rebuff"))
                OnCancelForceRebuffRequested?.Invoke();
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);

        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
        ImGui.TextColored(ColTextDim, TruncateName(_targetLabel, 32).ToUpperInvariant());
        ImGui.SameLine();
        float targetValueWidth = ImGui.CalcTextSize(_targetHealthDisplay).X;
        float targetLineX = ImGui.GetCursorPosX();
        float targetRegionWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(targetLineX + Math.Max(0, targetRegionWidth - targetValueWidth));
        ImGui.TextColored(new Vector4(1, 1, 1, 1), _targetHealthDisplay);
        LegacyDashboardDrawing.DrawSegmentedBar(_targetHealthPercent, ImGui.GetContentRegionAvail().X - 4);
        if (_settings.ShowTargetStaminaMana && _targetMaxStamina > 0)
        {
            float barWidth = ImGui.GetContentRegionAvail().X - 4;
            LegacyDashboardDrawing.DrawCompactVitalBar("ST", ToRatio(_targetStamina, _targetMaxStamina), ColGreen, FormatVital(_targetStamina, _targetMaxStamina), barWidth);
            LegacyDashboardDrawing.DrawCompactVitalBar("MN", ToRatio(_targetMana, _targetMaxMana), ColMana, FormatVital(_targetMana, _targetMaxMana), barWidth);
        }
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.TextColored(ColTextMute, "PLAYER VITALS");
        LegacyDashboardDrawing.DrawVitalRow("heart", "HP", ToRatio(_playerHealth, _playerMaxHealth), ColHp, FormatVital(_playerHealth, _playerMaxHealth));
        LegacyDashboardDrawing.DrawVitalRow("run", "ST", ToRatio(_playerStamina, _playerMaxStamina), ColGreen, FormatVital(_playerStamina, _playerMaxStamina));
        LegacyDashboardDrawing.DrawVitalRow("drop", "MN", ToRatio(_playerMana, _playerMaxMana), ColMana, FormatVital(_playerMana, _playerMaxMana));
        ImGui.EndTable();
    }

    private void OpenAdvancedTab(string tabName)
    {
        for (int i = 0; i < _settings.AdvancedTabs.Length; i++)
        {
            if (string.Equals(_settings.AdvancedTabs[i], tabName, StringComparison.OrdinalIgnoreCase))
            {
                _settings.SelectedAdvancedTab = i;
                _settings.ShowAdvancedWindow = true;
                return;
            }
        }
        // Tab not found — at least open the window so the user lands somewhere useful.
        _settings.ShowAdvancedWindow = true;
    }

    private void RefreshPlayerVitals()
    {
        if (!_host.HasGetPlayerVitals)
            return;

        if (_host.TryGetPlayerVitals(
            out uint health,
            out uint maxHealth,
            out uint stamina,
            out uint maxStamina,
            out uint mana,
            out uint maxMana))
        {
            _playerHealth = health;
            _playerMaxHealth = maxHealth != 0 ? maxHealth : _playerMaxHealth;
            _playerStamina = stamina;
            _playerMaxStamina = maxStamina != 0 ? maxStamina : _playerMaxStamina;
            _playerMana = mana;
            _playerMaxMana = maxMana != 0 ? maxMana : _playerMaxMana;
        }
    }

    private static float ToRatio(uint value, uint maxValue)
    {
        if (maxValue == 0)
            return 0f;

        return Math.Clamp((float)value / maxValue, 0f, 1f);
    }

    private static string FormatVital(uint value, uint maxValue)
    {
        if (maxValue == 0)
            return value == 0 ? "--/--" : $"{value}/--";

        return $"{value}/{maxValue}";
    }

    private void RenderLauncherGrid()
    {
        if (!ImGui.BeginTable("LauncherGridTable", 3, ImGuiTableFlags.SizingStretchSame)) return;
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); LegacyDashboardDrawing.GridBtn("Macro Rules", "gear", ref DashWindows.ShowMacroRules);
        ImGui.TableNextColumn();
        LegacyDashboardDrawing.GridBtn("Monsters", "target", ref DashWindows.ShowMonsters);
        ImGui.TableNextColumn(); LegacyDashboardDrawing.GridBtn("Settings", "wrench", ref _settings.ShowAdvancedWindow);
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); LegacyDashboardDrawing.GridBtn("Navigation", "map", ref DashWindows.ShowNavigation);
        ImGui.TableNextColumn(); LegacyDashboardDrawing.GridBtn("Items", "shield", ref DashWindows.ShowWeapons);
        ImGui.TableNextColumn(); LegacyDashboardDrawing.GridBtn("Lua Scripts", "code", ref DashWindows.ShowLua);
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); LegacyDashboardDrawing.GridBtn("Dungeon Map", "map", ref DashWindows.ShowDungeonMap);
        ImGui.TableNextColumn();
        ImGui.EndTable();
    }

    private static void RenderPlaceholderWindow(string title, ref bool open, string message)
    {
        ImGui.SetNextWindowSize(new Vector2(420, 260), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(title, ref open)) { ImGui.End(); return; }
        ImGui.TextWrapped(message);
        ImGui.End();
    }

    private void RefreshAllLists()
    {
        RefreshProfilesList();
        RefreshNavFiles();
        RefreshLootFiles();
        RefreshMetaFiles();
    }

    private void RefreshProfilesList()
    {
        _profiles.Clear();
        _profiles.Add("Default");
        try
        {
            if (!string.IsNullOrEmpty(_charFolder) && Directory.Exists(_charFolder))
            {
                foreach (string file in Directory.GetFiles(_charFolder, "*.json"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.Equals("settings", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("monsters", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!_profiles.Contains(name, StringComparer.OrdinalIgnoreCase))
                        _profiles.Add(name);
                }
            }
        }
        catch { }
        if (!_profiles.Contains(_settings.SelectedProfile, StringComparer.OrdinalIgnoreCase))
            _settings.SelectedProfile = _profiles[0];
    }

    private void RefreshNavFiles()
    {
        _navFiles.Clear(); _navFiles.Add("None"); _selectedNavIdx = 0;
        if (!Directory.Exists(_navFolder)) return;
        foreach (string file in Directory.GetFiles(_navFolder, "*.nav"))
        {
            _navFiles.Add(Path.GetFileNameWithoutExtension(file));
            if (file.Equals(_settings.CurrentNavPath, StringComparison.OrdinalIgnoreCase)) _selectedNavIdx = _navFiles.Count - 1;
        }
    }

    private void RefreshLootFiles()
    {
        _lootFiles.Clear(); _lootFiles.Add("None");
        if (!Directory.Exists(_lootFolder)) return;

        var files = new System.Collections.Generic.List<string>();
        files.AddRange(Directory.GetFiles(_lootFolder, "*.utl"));
        files.AddRange(Directory.GetFiles(_lootFolder, "*.json"));
        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            _lootFiles.Add(Path.GetFileName(file));
            if (file.Equals(_settings.CurrentLootPath, StringComparison.OrdinalIgnoreCase))
                _settings.LootProfileIdx = _lootFiles.Count - 1;
        }
    }

    private void RefreshMetaFiles()
    {
        _metaFiles.Clear();
        _metaFiles.Add("None");
        _settings.MetaProfileIdx = 0;
        if (!Directory.Exists(_metaFolder)) return;

        var files = new List<string>();
        foreach (string f in Directory.GetFiles(_metaFolder, "*.met"))
        {
            if (Path.GetFileName(f).StartsWith("--")) continue;
            files.Add(f);
        }
        files.AddRange(Directory.GetFiles(_metaFolder, "*.af"));
        files.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            _metaFiles.Add(Path.GetFileName(file));
            if (file.Equals(_settings.CurrentMetaPath, StringComparison.OrdinalIgnoreCase))
                _settings.MetaProfileIdx = _metaFiles.Count - 1;
        }
    }

    private void LoadSelectedNav()
    {
        if (_selectedNavIdx < 0 || _selectedNavIdx >= _navFiles.Count) return;
        string selection = _navFiles[_selectedNavIdx];
        if (selection == "None")
        {
            _settings.CurrentNavPath = string.Empty;
            _settings.CurrentRoute.Points.Clear();
            _settings.ActiveNavIndex = 0;
            SaveSettings();
            return;
        }
        string filePath = Path.Combine(_navFolder, selection + ".nav");
        _settings.CurrentNavPath = filePath;
        _settings.CurrentRoute = NavRouteParser.Load(filePath);
        // Follow and Once routes start from the top so opening Recall/Portal/Chat
        // actions always fire. Circular and Linear routes jump in at the nearest point.
        _settings.ActiveNavIndex =
            (_settings.CurrentRoute.RouteType == NavRouteType.Follow ||
             _settings.CurrentRoute.RouteType == NavRouteType.Once)
                ? 0
                : FindNearestWaypoint(_settings.CurrentRoute);
        SaveSettings();
    }

    // ── Nav bridge (engine-side Avalonia NavPanel) ────────────────────────────

    public string BuildNavJson()
    {
        try
        {
            var points = new List<NavBridgePoint>(_settings.CurrentRoute.Points.Count);
            for (int i = 0; i < _settings.CurrentRoute.Points.Count; i++)
            {
                var p = _settings.CurrentRoute.Points[i];
                points.Add(new NavBridgePoint
                {
                    Idx  = i,
                    Type = p.Type.ToString(),
                    Desc = p.ToString(),
                    NS   = p.NS,
                    EW   = p.EW,
                    Z    = p.Z,
                });
            }

            int routeTypeInt = _settings.CurrentRoute.RouteType switch
            {
                NavRouteType.Circular => 1,
                NavRouteType.Linear   => 2,
                NavRouteType.Follow   => 3,
                _                     => 4,  // Once
            };

            var payload = new NavBridgePayload
            {
                ActiveNavName     = string.IsNullOrEmpty(_settings.CurrentNavPath)
                                        ? "None (Unsaved)"
                                        : Path.GetFileName(_settings.CurrentNavPath),
                NavStatusLine     = _settings.NavStatusLine ?? string.Empty,
                NavIsStuck        = _settings.NavIsStuck,
                MacroRunning      = _settings.IsMacroRunning,
                NavigationEnabled = _settings.EnableNavigation,
                RouteType         = routeTypeInt,
                ActiveNavIndex    = _settings.ActiveNavIndex,
                NavFiles          = new List<string>(_navFiles),
                Points            = points,
            };
            return JsonSerializer.Serialize(payload, RynthAiJsonContext.Default.NavBridgePayload);
        }
        catch { return "{}"; }
    }

    public void HandleNavCommand(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var cmd = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.NavCommand);
            if (cmd == null) return;

            switch (cmd.Cmd)
            {
                case "startNav":
                    _settings.IsMacroRunning  = true;
                    _settings.EnableNavigation = true;
                    if (_settings.BotAction != "Navigating")
                        _settings.BotAction = "Default";
                    SaveSettings();
                    break;

                case "stopNav":
                    _settings.EnableNavigation = false;
                    SaveSettings();
                    break;

                case "addWaypoint":
                    _host.SetMotion(0x6500000D, false); // stop TurnRight
                    _host.SetMotion(0x6500000E, false); // stop TurnLeft
                    if (_host.HasGetPlayerPose &&
                        _host.TryGetPlayerPose(out _, out float wx, out float wy, out float wz, out _, out _, out _, out _) &&
                        NavCoordinateHelper.TryGetNavCoords(_host, out double wNS, out double wEW))
                    {
                        InsertNavPoint(new NavPoint { NS = wNS, EW = wEW, Z = wz }, cmd.AddMode, cmd.InsertAt);
                    }
                    break;

                case "addRecall":
                    if (_host.HasGetPlayerPose &&
                        _host.TryGetPlayerPose(out _, out float rx, out float ry, out float rz, out _, out _, out _, out _) &&
                        NavCoordinateHelper.TryGetNavCoords(_host, out double rNS, out double rEW))
                    {
                        InsertNavPoint(new NavPoint { Type = NavPointType.Recall, NS = rNS, EW = rEW, Z = rz, SpellId = cmd.SpellId }, cmd.AddMode, cmd.InsertAt);
                    }
                    break;

                case "deletePoint":
                    int di = cmd.Index;
                    if (di >= 0 && di < _settings.CurrentRoute.Points.Count)
                    {
                        _settings.CurrentRoute.Points.RemoveAt(di);
                        if (_settings.ActiveNavIndex == di) _settings.ActiveNavIndex = 0;
                        else if (_settings.ActiveNavIndex > di) _settings.ActiveNavIndex--;
                        _navigationUi.TryAutoSaveNav();
                    }
                    break;

                case "clearRoute":
                    _settings.CurrentRoute.Points.Clear();
                    _settings.ActiveNavIndex = 0;
                    _navigationUi.TryAutoSaveNav();
                    break;

                case "saveRoute":
                    if (!string.IsNullOrEmpty(_settings.CurrentNavPath))
                    {
                        try { _settings.CurrentRoute.Save(_settings.CurrentNavPath); } catch { }
                    }
                    break;

                case "setRouteType":
                    _settings.CurrentRoute.RouteType = cmd.RouteType switch
                    {
                        1 => NavRouteType.Circular,
                        2 => NavRouteType.Linear,
                        3 => NavRouteType.Follow,
                        _ => NavRouteType.Once,
                    };
                    _navigationUi.TryAutoSaveNav();
                    break;

                case "loadNav":
                    int ni = _navFiles.IndexOf(cmd.NavName);
                    if (ni >= 0) { _selectedNavIdx = ni; LoadSelectedNav(); SaveSettings(); }
                    break;
            }
        }
        catch { }
    }

    private void InsertNavPoint(NavPoint pt, int addMode, int insertAt)
    {
        if (addMode == 0 || _settings.CurrentRoute.Points.Count == 0 || insertAt < 0)
            _settings.CurrentRoute.Points.Add(pt);
        else if (addMode == 1)
            _settings.CurrentRoute.Points.Insert(insertAt, pt);
        else
            _settings.CurrentRoute.Points.Insert(Math.Min(insertAt + 1, _settings.CurrentRoute.Points.Count), pt);

        _navigationUi.TryAutoSaveNav();
    }

    private int FindNearestWaypoint(NavRouteParser route)
    {
        if (route.Points.Count == 0 || !NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew)) return 0;
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < route.Points.Count; i++)
        {
            NavPoint point = route.Points[i];
            if (point.Type != NavPointType.Point) continue;
            double distance = Math.Sqrt(Math.Pow(point.NS - ns, 2) + Math.Pow(point.EW - ew, 2));
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return best;
    }
    private static string TruncateName(string? value, int max) => string.IsNullOrEmpty(value) ? string.Empty : value.Length > max ? value[..(max - 1)] + "..." : value;

    // ── Snapshot bridge (read by the engine-side Avalonia RynthAi panel) ────
    // The Avalonia panel mirrors this dashboard. It pulls a JSON snapshot via
    // RynthPluginGetSnapshotJson every ~250ms and renders the same fields.

    public string BuildSnapshotJson()
    {
        // Pull a fresh read every snapshot poll. If the cache is cold (right
        // after hot reload), the host's TryGetPlayerVitals will still have
        // returned zero — but the next OnUpdateHealth event or live qualities
        // read will fix that within a tick or two.
        RefreshPlayerVitals();

        var sb = new System.Text.StringBuilder(2048);
        sb.Append('{');
        AppendBool(sb, "macroRunning", _settings.IsMacroRunning); sb.Append(',');
        AppendString(sb, "currentState", _settings.CurrentState ?? string.Empty); sb.Append(',');
        AppendString(sb, "botAction", _settings.BotAction ?? "Default"); sb.Append(',');
        AppendString(sb, "selectedProfile", _settings.SelectedProfile ?? "Default"); sb.Append(',');
        AppendStringArray(sb, "profiles", _profiles); sb.Append(',');
        AppendStringArray(sb, "navProfiles", _navFiles); sb.Append(',');
        AppendStringArray(sb, "lootProfiles", _lootFiles); sb.Append(',');
        AppendStringArray(sb, "metaProfiles", _metaFiles); sb.Append(',');
        AppendString(sb, "currentNavName",
            string.IsNullOrEmpty(_settings.CurrentNavPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentNavPath)); sb.Append(',');
        AppendString(sb, "currentLootName",
            string.IsNullOrEmpty(_settings.CurrentLootPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentLootPath)); sb.Append(',');
        AppendString(sb, "currentMetaName",
            string.IsNullOrEmpty(_settings.CurrentMetaPath) ? "None" : Path.GetFileNameWithoutExtension(_settings.CurrentMetaPath)); sb.Append(',');
        AppendInt(sb, "selectedNavIdx", _selectedNavIdx); sb.Append(',');
        AppendInt(sb, "selectedLootIdx", _settings.LootProfileIdx); sb.Append(',');
        AppendInt(sb, "selectedMetaIdx", _settings.MetaProfileIdx); sb.Append(',');
        AppendInt(sb, "selectedProfileIdx", Math.Max(0, _profiles.IndexOf(_settings.SelectedProfile ?? string.Empty))); sb.Append(',');
        AppendBool(sb, "combatEnabled", _settings.EnableCombat); sb.Append(',');
        AppendBool(sb, "buffingEnabled", _settings.EnableBuffing); sb.Append(',');
        AppendBool(sb, "navigationEnabled", _settings.EnableNavigation); sb.Append(',');
        AppendBool(sb, "lootingEnabled", _settings.EnableLooting); sb.Append(',');
        AppendBool(sb, "metaEnabled", _settings.EnableMeta); sb.Append(',');
        AppendUInt(sb, "currentTargetId", _currentTargetId); sb.Append(',');
        AppendString(sb, "targetLabel", _targetLabel ?? "NO TARGET"); sb.Append(',');
        AppendFloat(sb, "targetHealthPercent", _targetHealthPercent); sb.Append(',');
        AppendString(sb, "targetHealthDisplay", _targetHealthDisplay ?? "0"); sb.Append(',');
        AppendUInt(sb, "targetHealth", _targetHealth); sb.Append(',');
        AppendUInt(sb, "targetMaxHealth", _targetMaxHealth); sb.Append(',');
        AppendUInt(sb, "targetStamina", _targetStamina); sb.Append(',');
        AppendUInt(sb, "targetMaxStamina", _targetMaxStamina); sb.Append(',');
        AppendUInt(sb, "targetMana", _targetMana); sb.Append(',');
        AppendUInt(sb, "targetMaxMana", _targetMaxMana); sb.Append(',');
        AppendUInt(sb, "playerHealth", _playerHealth); sb.Append(',');
        AppendUInt(sb, "playerMaxHealth", _playerMaxHealth); sb.Append(',');
        AppendUInt(sb, "playerStamina", _playerStamina); sb.Append(',');
        AppendUInt(sb, "playerMaxStamina", _playerMaxStamina); sb.Append(',');
        AppendUInt(sb, "playerMana", _playerMana); sb.Append(',');
        AppendUInt(sb, "playerMaxMana", _playerMaxMana); sb.Append(',');
        AppendBool(sb, "showTargetStaminaMana", _settings.ShowTargetStaminaMana); sb.Append(',');
        AppendBool(sb, "isLocked", _isLocked); sb.Append(',');
        AppendBool(sb, "isMinimized", _isMinimized); sb.Append(',');
        AppendFloat(sb, "bgOpacity", _bgOpacity);
        sb.Append('}');
        return sb.ToString();
    }

    public void TogglePanelMacro() => _settings.IsMacroRunning = !_settings.IsMacroRunning;

    public void SetSubsystemEnabled(int id, bool enabled)
    {
        switch (id)
        {
            case 0: _settings.EnableCombat = enabled; break;
            case 1: _settings.EnableBuffing = enabled; break;
            case 2: _settings.EnableNavigation = enabled; break;
            case 3: _settings.EnableLooting = enabled; break;
            case 4: _settings.EnableMeta = enabled; break;
        }
        SaveSettings();
    }

    /// <summary>
    /// Profile kinds: 0 = nav, 1 = loot, 2 = meta, 3 = settings profile.
    /// Index is into the matching list returned in BuildSnapshotJson.
    /// </summary>
    public void SelectProfileAtIndex(int kind, int index)
    {
        switch (kind)
        {
            case 0:
                if (index >= 0 && index < _navFiles.Count) { _selectedNavIdx = index; LoadSelectedNav(); }
                break;
            case 1:
                if (index >= 0 && index < _lootFiles.Count)
                {
                    _settings.LootProfileIdx = index;
                    _settings.CurrentLootPath = index == 0 ? string.Empty : Path.Combine(_lootFolder, _lootFiles[index]);
                    SaveSettings();
                }
                break;
            case 2:
                if (index >= 0 && index < _metaFiles.Count)
                {
                    _settings.MetaProfileIdx = index;
                    string path = index == 0 ? string.Empty : Path.Combine(_metaFolder, _metaFiles[index]);
                    _metaUi.LoadMacroFile(path);
                    SaveSettings();
                }
                break;
            case 3:
                if (index >= 0 && index < _profiles.Count) SwitchProfile(_profiles[index]);
                break;
        }
    }

    public void RequestForceRebuff() => OnForceRebuffRequested?.Invoke();
    public void RequestCancelForceRebuff() => OnCancelForceRebuffRequested?.Invoke();
    public void AdjustOpacity(float delta) => _bgOpacity = Math.Clamp(_bgOpacity + delta, 0.1f, 1f);
    public void TogglePanelLock() => _isLocked = !_isLocked;
    public void TogglePanelMinimize() { _isMinimized = !_isMinimized; SaveSettings(); }
    public bool IsPanelLocked => _isLocked;
    public bool IsPanelMinimized => _isMinimized;

    private static void AppendString(System.Text.StringBuilder sb, string key, string value)
    {
        sb.Append('"').Append(key).Append("\":\"");
        AppendEscaped(sb, value);
        sb.Append('"');
    }

    private static void AppendBool(System.Text.StringBuilder sb, string key, bool value)
        => sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");

    private static void AppendInt(System.Text.StringBuilder sb, string key, int value)
        => sb.Append('"').Append(key).Append("\":").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void AppendUInt(System.Text.StringBuilder sb, string key, uint value)
        => sb.Append('"').Append(key).Append("\":").Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void AppendFloat(System.Text.StringBuilder sb, string key, float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
        sb.Append('"').Append(key).Append("\":").Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendStringArray(System.Text.StringBuilder sb, string key, IEnumerable<string> values)
    {
        sb.Append('"').Append(key).Append("\":[");
        bool first = true;
        foreach (string v in values)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"');
            AppendEscaped(sb, v ?? string.Empty);
            sb.Append('"');
        }
        sb.Append(']');
    }

    private static void AppendEscaped(System.Text.StringBuilder sb, string value)
    {
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }
    }

    // ── Meta bridge ───────────────────────────────────────────────────────────

    private static readonly string MetaFolder = @"C:\Games\RynthSuite\RynthAi\MetaFiles";

    public string BuildMetaJson()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{');
        AppendBool(sb, "enableMeta", _settings.EnableMeta); sb.Append(',');
        AppendBool(sb, "metaDebug", _settings.MetaDebug); sb.Append(',');
        AppendString(sb, "currentState", _settings.CurrentState ?? "Default"); sb.Append(',');
        AppendString(sb, "currentMetaPath", _settings.CurrentMetaPath ?? ""); sb.Append(',');

        sb.Append("\"rules\":");
        AppendMetaRuleArray(sb, _settings.MetaRules);
        sb.Append(',');

        var files = BuildMetaFileList();
        sb.Append("\"files\":[");
        for (int i = 0; i < files.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"path\":\""); AppendEscaped(sb, files[i].Item1); sb.Append("\",\"display\":\""); AppendEscaped(sb, files[i].Item2); sb.Append("\"}");
        }
        sb.Append("],");

        var states = _settings.MetaRules.Select(r => r.State).Distinct().ToList();
        if (!states.Contains("Default")) states.Insert(0, "Default");
        AppendStringArray(sb, "states", states); sb.Append(',');

        AppendStringArray(sb, "navFiles", _navFiles); sb.Append(',');
        AppendStringArray(sb, "embeddedNavKeys", _settings.EmbeddedNavs.Keys); sb.Append(',');

        string sourceText = "";
        try { sourceText = AfFileWriter.SaveToString(_settings.MetaRules, _settings.EmbeddedNavs); } catch { }
        AppendString(sb, "sourceText", sourceText);

        sb.Append('}');
        return sb.ToString();
    }

    private List<(string, string)> BuildMetaFileList()
    {
        var result = new List<(string, string)>();
        result.Add(("", "-- None --"));
        try
        {
            System.IO.Directory.CreateDirectory(MetaFolder);
            var entries = new List<(string, string)>();
            foreach (string f in System.IO.Directory.GetFiles(MetaFolder, "*.met"))
            {
                string name = System.IO.Path.GetFileName(f);
                if (!name.StartsWith("--")) entries.Add((f, $"[met] {name}"));
            }
            foreach (string f in System.IO.Directory.GetFiles(MetaFolder, "*.af"))
                entries.Add((f, $"[af]  {System.IO.Path.GetFileName(f)}"));
            entries.Sort((a, b) => string.Compare(a.Item2, b.Item2, System.StringComparison.OrdinalIgnoreCase));
            result.AddRange(entries);
        }
        catch { }
        return result;
    }

    private static void AppendMetaRuleArray(System.Text.StringBuilder sb, List<MetaRule> rules)
    {
        sb.Append('[');
        for (int i = 0; i < rules.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendMetaRule(sb, rules[i]);
        }
        sb.Append(']');
    }

    private static void AppendMetaRule(System.Text.StringBuilder sb, MetaRule r)
    {
        sb.Append('{');
        sb.Append("\"state\":\""); AppendEscaped(sb, r.State ?? "Default"); sb.Append("\",");
        sb.Append("\"condition\":").Append((int)r.Condition).Append(',');
        sb.Append("\"conditionData\":\""); AppendEscaped(sb, r.ConditionData ?? ""); sb.Append("\",");
        sb.Append("\"action\":").Append((int)r.Action).Append(',');
        sb.Append("\"actionData\":\""); AppendEscaped(sb, r.ActionData ?? ""); sb.Append("\",");
        sb.Append("\"children\":"); AppendMetaRuleArray(sb, r.Children ?? new List<MetaRule>()); sb.Append(',');
        sb.Append("\"actionChildren\":"); AppendMetaRuleArray(sb, r.ActionChildren ?? new List<MetaRule>()); sb.Append(',');
        long ms = r.LastFiredAt == DateTime.MinValue ? 99999L : (long)(DateTime.Now - r.LastFiredAt).TotalMilliseconds;
        sb.Append("\"lastFiredMs\":").Append(ms.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append('}');
    }

    public void HandleMetaCommand(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var cmd = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.MetaCommand);
            if (cmd == null) return;

            switch (cmd.Op)
            {
                case "set_enabled":
                    _settings.EnableMeta = string.Equals(cmd.Value, "true", System.StringComparison.OrdinalIgnoreCase);
                    SaveSettings();
                    break;

                case "set_debug":
                    _settings.MetaDebug = string.Equals(cmd.Value, "true", System.StringComparison.OrdinalIgnoreCase);
                    SaveSettings();
                    break;

                case "set_state":
                    if (!string.IsNullOrEmpty(cmd.Value))
                    {
                        _settings.CurrentState = cmd.Value;
                        _settings.ForceStateReset = true;
                    }
                    break;

                case "load_file":
                    _metaUi.LoadMacroFile(cmd.Path ?? "");
                    break;

                case "save_file":
                    if (!string.IsNullOrEmpty(cmd.Path))
                    {
                        AfFileWriter.Save(cmd.Path, _settings.MetaRules, _settings.EmbeddedNavs);
                        _settings.CurrentMetaPath = cmd.Path;
                        SaveSettings();
                    }
                    break;

                case "set_source":
                    if (!string.IsNullOrEmpty(cmd.Text))
                    {
                        try
                        {
                            var loaded = AfFileParser.LoadFromText(cmd.Text);
                            if (loaded.Rules.Count > 0)
                            {
                                _settings.MetaRules = loaded.Rules;
                                _settings.EmbeddedNavs.Clear();
                                foreach (var kvp in loaded.EmbeddedNavs) _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
                                _settings.ForceStateReset = true;
                                TryAutoSaveMetaCmd();
                            }
                        }
                        catch { }
                    }
                    break;

                case "add_rule":
                    if (cmd.Rule != null)
                    {
                        _settings.MetaRules.Add(DtoToMetaRule(cmd.Rule));
                        TryAutoSaveMetaCmd();
                    }
                    break;

                case "update_rule":
                    if (cmd.Rule != null && cmd.Index >= 0 && cmd.Index < _settings.MetaRules.Count)
                    {
                        _settings.MetaRules[cmd.Index] = DtoToMetaRule(cmd.Rule);
                        TryAutoSaveMetaCmd();
                    }
                    break;

                case "delete_rule":
                    if (cmd.Index >= 0 && cmd.Index < _settings.MetaRules.Count)
                    {
                        _settings.MetaRules.RemoveAt(cmd.Index);
                        TryAutoSaveMetaCmd();
                    }
                    break;

                case "move_up":
                    if (cmd.Index > 0 && cmd.Index < _settings.MetaRules.Count)
                    {
                        var tmp = _settings.MetaRules[cmd.Index - 1];
                        _settings.MetaRules[cmd.Index - 1] = _settings.MetaRules[cmd.Index];
                        _settings.MetaRules[cmd.Index] = tmp;
                        TryAutoSaveMetaCmd();
                    }
                    break;

                case "move_down":
                    if (cmd.Index >= 0 && cmd.Index < _settings.MetaRules.Count - 1)
                    {
                        var tmp = _settings.MetaRules[cmd.Index + 1];
                        _settings.MetaRules[cmd.Index + 1] = _settings.MetaRules[cmd.Index];
                        _settings.MetaRules[cmd.Index] = tmp;
                        TryAutoSaveMetaCmd();
                    }
                    break;
            }
        }
        catch { }
    }

    private void TryAutoSaveMetaCmd()
    {
        if (string.IsNullOrEmpty(_settings.CurrentMetaPath)) return;
        try
        {
            string path = _settings.CurrentMetaPath;
            if (System.IO.Path.GetExtension(path).Equals(".met", System.StringComparison.OrdinalIgnoreCase))
            {
                path = System.IO.Path.ChangeExtension(path, ".af");
                _settings.CurrentMetaPath = path;
            }
            AfFileWriter.Save(path, _settings.MetaRules, _settings.EmbeddedNavs);
        }
        catch { }
    }

    private static MetaRule DtoToMetaRule(MetaRuleDto dto)
    {
        var r = new MetaRule
        {
            State         = dto.State ?? "Default",
            Condition     = (MetaConditionType)dto.Condition,
            ConditionData = dto.ConditionData ?? "",
            Action        = (MetaActionType)dto.Action,
            ActionData    = dto.ActionData ?? "",
        };
        foreach (var c in dto.Children ?? new List<MetaRuleDto>())
            r.Children.Add(DtoToMetaRule(c));
        foreach (var a in dto.ActionChildren ?? new List<MetaRuleDto>())
            r.ActionChildren.Add(DtoToMetaRule(a));
        return r;
    }
}

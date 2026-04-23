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
    private readonly DungeonMapUi _dungeonMapUi;

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

    public void SetRaycast(Raycasting.MainLogic raycast) => _dungeonMapUi.SetRaycast(raycast);
    public void SetWorldObjectCache(WorldObjectCache cache) => _dungeonMapUi.SetWorldObjectCache(cache);

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
        RefreshAllLists();

        // Load MonsterRules from monsters.json (overrides what was in the profile)
        // and migrate existing rules to that file if it doesn't exist yet.
        MigrateMonstersToFile();
        LoadMonstersFromFile();
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
        _isLocked  = _settings.WindowLocked;
        _bgOpacity = _settings.BgOpacity;
        DashWindows.ShowWeapons     = _settings.DashShowWeapons;
        DashWindows.ShowLua         = _settings.DashShowLua;
        DashWindows.ShowNavigation  = _settings.DashShowNavigation;
        DashWindows.ShowMacroRules  = _settings.DashShowMacroRules;
        DashWindows.ShowMonsters    = _settings.DashShowMonsters;
        DashWindows.ShowDungeonMap  = _settings.DashShowDungeonMap;
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
        dst.BlacklistAttempts        = tmp.BlacklistAttempts;
        dst.BlacklistTimeoutSec      = tmp.BlacklistTimeoutSec;
        dst.MeleeAttackPower         = tmp.MeleeAttackPower;
        dst.MissileAttackPower       = tmp.MissileAttackPower;
        dst.UseRecklessness          = tmp.UseRecklessness;
        dst.UseNativeAttack          = tmp.UseNativeAttack;
        dst.MeleeAttackHeight        = tmp.MeleeAttackHeight;
        dst.MissileAttackHeight      = tmp.MissileAttackHeight;
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
        dst.MinSkillLevelTier1       = tmp.MinSkillLevelTier1;
        dst.MinSkillLevelTier2       = tmp.MinSkillLevelTier2;
        dst.MinSkillLevelTier3       = tmp.MinSkillLevelTier3;
        dst.MinSkillLevelTier4       = tmp.MinSkillLevelTier4;
        dst.MinSkillLevelTier5       = tmp.MinSkillLevelTier5;
        dst.MinSkillLevelTier6       = tmp.MinSkillLevelTier6;
        dst.MinSkillLevelTier7       = tmp.MinSkillLevelTier7;
        dst.MinSkillLevelTier8       = tmp.MinSkillLevelTier8;
        dst.EnableManaTapping        = tmp.EnableManaTapping;
        dst.ManaTapMinMana           = tmp.ManaTapMinMana;
        dst.ManaStoneKeepCount       = tmp.ManaStoneKeepCount;
        dst.MetaDebug                = tmp.MetaDebug;
        dst.StartMacroOnLogin        = tmp.StartMacroOnLogin;
        dst.ShowTerrainPassability   = tmp.ShowTerrainPassability;
        dst.GiveQueueIntervalMs      = tmp.GiveQueueIntervalMs;
        dst.SpellCastIntervalMs      = tmp.SpellCastIntervalMs;
        dst.EmbeddedNavs             = tmp.EmbeddedNavs;
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
            _targetHealthDisplay = $"{(int)(_targetHealthPercent * 100)}%";
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

        // Plugin-wide style overrides: mana-blue input/dropdown fields, black checkmarks
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0.15f, 0.55f, 0.95f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.22f, 0.62f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0.10f, 0.45f, 0.82f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark,      new Vector4(0.00f, 0.00f, 0.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg,        new Vector4(0.08f, 0.30f, 0.65f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.Header,         new Vector4(0.15f, 0.55f, 0.95f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,  new Vector4(0.22f, 0.62f, 1.00f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,   new Vector4(0.10f, 0.45f, 0.82f, 1.00f));

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
            ImGui.PopStyleColor(8);
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
        // Restore saved window position on first render after login
        if (!_windowPosRestored && _settings.WindowPosX >= 0 && _settings.WindowPosY >= 0)
        {
            ImGui.SetNextWindowPos(new Vector2(_settings.WindowPosX, _settings.WindowPosY), ImGuiCond.Always);
            _windowPosRestored = true;
        }
        else
        {
            _windowPosRestored = true;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(400, 0), new Vector2(1200, 2000));
        if (!_isMinimized && !_isLocked)
        {
            if (_wasMinimized) { ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.Always); _wasMinimized = false; }
            else ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.FirstUseEver);
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
        if (ImGui.SmallButton(_isMinimized ? "^" : "_")) _isMinimized = !_isMinimized;
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
        LegacyDashboardDrawing.DrawSquareToggle("sword", ref _settings.EnableCombat, togglePos, "CombatTgl");
        LegacyDashboardDrawing.DrawSquareToggle("buff", ref _settings.EnableBuffing, togglePos + new Vector2(34, 0), "BuffTgl");
        LegacyDashboardDrawing.DrawSquareToggle("shoe", ref _settings.EnableNavigation, togglePos + new Vector2(0, 34), "NavTgl");
        LegacyDashboardDrawing.DrawSquareToggle("bag", ref _settings.EnableLooting, togglePos + new Vector2(34, 34), "LootTgl");
        LegacyDashboardDrawing.DrawWideToggle("MACRO", "gear", ref _settings.EnableMeta, togglePos + new Vector2(0, 68), "MetaTgl", 64f, 20f);

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
        if (selection == "None") { _settings.CurrentNavPath = string.Empty; _settings.CurrentRoute.Points.Clear(); _settings.ActiveNavIndex = 0; return; }
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
}

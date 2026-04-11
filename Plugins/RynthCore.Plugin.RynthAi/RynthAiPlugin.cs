using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginCore;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("RynthAi");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.5.0-legacy-ui");

    private LegacyDashboardRenderer? _dashboard;
    private NavigationEngine? _navigationEngine;
    private NavMarkerRenderer? _navMarkerRenderer;
    private MainLogic? _raycast;
    private WorldObjectCache? _objectCache;
    private CharacterSkills? _charSkills;
    private SpellManager? _spellManager;
    private BuffManager? _buffManager;
    private CombatManager? _combatManager;
    private MissileCraftingManager? _missileCraftingManager;
    private FellowshipTracker? _fellowshipTracker;
    private MetaManager? _metaManager;
    private InventoryManager? _inventoryManager;
    private PlayerVitalsCache _vitals = new();
    private uint _playerId;
    private int _vitalsTickCounter;
    private bool _initialized;
    private bool _loginComplete;
    private bool _windowVisible;
    private int _currentCombatMode = 1; // 1=noncombat
    private uint _currentTargetId;
    private VTankLootProfile? _loadedLootProfile;
    private string _loadedLootProfilePath = string.Empty;
    private DateTime _loadedLootProfileTime = DateTime.MinValue;
    private static bool _imguiResolverConfigured;

    public override int Initialize()
    {
        if (Host.ImGuiContext == IntPtr.Zero)
            return 11;

        EnsureImGuiResolver();
        _dashboard = new LegacyDashboardRenderer(Host);
        _objectCache = new WorldObjectCache(Host); // must exist before CreateObject events fire during login
        _initialized = true;
        _loginComplete = false;
        _windowVisible = false;
        Log("RynthAi: legacy ImGui dashboard initialized.");
        return 0;
    }

    public override void Shutdown()
    {
        _navigationEngine?.Stop();
        _navigationEngine = null;
        _navMarkerRenderer = null;
        _raycast?.Dispose();
        _raycast = null;
        _combatManager?.Dispose();
        _combatManager = null;
        _fellowshipTracker?.Dispose();
        _fellowshipTracker = null;
        _metaManager = null;
        _inventoryManager = null;
        _buffManager?.Dispose();
        _buffManager = null;
        _spellManager = null;
        _objectCache = null;
        _playerId = 0;
        _initialized = false;
        _loginComplete = false;
        _windowVisible = false;
        _dashboard = null;
    }

    public override void OnLoginComplete()
    {
        if (!_initialized || _dashboard is null)
            return;

        _loginComplete = true;
        _dashboard.OnLoginComplete();
        _navigationEngine = new NavigationEngine(Host, _dashboard.Settings);
        _navMarkerRenderer = new NavMarkerRenderer(Host, _dashboard.Settings);
        Log($"RynthAi: NavMarkerRenderer created, HasNav3D={Host.HasNav3D}, version={Host.Version}");

        // Init raycast on background thread — .dat parsing takes ~700ms and blocks the client
        _raycast = new MainLogic();
        var raycastRef = _raycast;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                bool rayOk = raycastRef.Initialize(@"C:\Turbine\Asheron's Call");
                Log($"RynthAi: raycast init={rayOk} status={raycastRef.StatusMessage}");
                if (rayOk)
                {
                    _combatManager?.SetRaycastSystem(raycastRef);
                    _dashboard?.SetRaycast(raycastRef);
                }
            }
            catch (Exception ex)
            {
                Log($"RynthAi: raycast init error: {ex.Message}");
            }
        });

        // Cache player ID so we can filter self out of creature tracking and update vitals
        _playerId = Host.GetPlayerId();
        _objectCache?.SetPlayerId(_playerId);
        if (_objectCache != null) _dashboard.SetWorldFilter(_objectCache);
        if (_objectCache != null) _dashboard.SetWorldObjectCache(_objectCache);

        // Load per-character settings — character name comes from the player's object name
        if (_playerId != 0 && Host.HasGetObjectName && Host.TryGetObjectName(_playerId, out string charName) && !string.IsNullOrWhiteSpace(charName))
            _dashboard.LoadSettings(charName);

        // Query own health to get the ratio → derive true MaxHealth immediately
        if (_playerId != 0 && Host.HasQueryHealth)
            Host.QueryHealth(_playerId);

        // Wire combat subsystems
        _vitals = new PlayerVitalsCache();
        _charSkills = new CharacterSkills(Host);
        _charSkills.SetPlayerId(_playerId);
        _spellManager = new SpellManager(Host, _dashboard.Settings);
        _spellManager.SetCharacterSkills(_charSkills);
        _spellManager.SetPlayerId(_playerId);
        _spellManager.InitializeNatively();
        _buffManager = new BuffManager(Host, _dashboard.Settings, _spellManager, _vitals);
        _buffManager.SetCharacterSkills(_charSkills);
        if (_objectCache != null) _buffManager.SetWorldObjectCache(_objectCache);
        if (_playerId != 0 && Host.TryGetObjectName(_playerId, out string buffCharName) && !string.IsNullOrWhiteSpace(buffCharName))
        {
            string safeChar = buffCharName.Replace(" ", "_").Replace("'", "").Replace("-", "_");
            string charFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RynthCore", "Characters", safeChar);
            _buffManager.SetTimerPath(charFolder);
        }

        // Override disk timers with live client memory — gets accurate remaining times
        // including login-restored enchantments the event hook missed at startup.
        int liveCount = _buffManager.RefreshFromLiveMemory();
        if (liveCount >= 0)
            Host.WriteToChat($"[RynthAi] Loaded {liveCount} active buff timer(s) from client memory.", 1);

        _combatManager = new CombatManager(Host, _dashboard.Settings, _objectCache!, _spellManager);
        _combatManager.SetCharacterSkills(_charSkills);
        _combatManager.SetPlayerId(_playerId);
        _combatManager.CurrentCombatMode = _currentCombatMode;
        _buffManager.CurrentCombatMode = _currentCombatMode;

        _missileCraftingManager = new MissileCraftingManager(Host, _dashboard.Settings);
        _missileCraftingManager.SetObjectCache(_objectCache!);
        _missileCraftingManager.SetCharacterSkills(_charSkills);
        _missileCraftingManager.CurrentCombatMode = _currentCombatMode;
        _dashboard.SetMissileCraftingManager(_missileCraftingManager);

        _fellowshipTracker?.Dispose();
        _fellowshipTracker = new FellowshipTracker();

        _metaManager = new MetaManager(_dashboard.Settings, Host, _vitals);
        _metaManager.SetPlayerId(_playerId);
        if (_objectCache != null) _metaManager.SetObjectCache(_objectCache);

        if (_objectCache != null)
            _inventoryManager = new InventoryManager(Host, _dashboard.Settings, _objectCache);

        Log("RynthAi: login complete, legacy ImGui dashboard ready.");
    }

    private bool _combatDbgActive = false;
    private int _combatDbgFrames = 0;
    private bool _combatPausedNav;
    private bool _corpsePausedNav;
    private long _combatEndedAt;       // Timestamp when combat stopped blocking
    private const long LootGraceMs = 2000; // Hold nav after combat ends so corpses can spawn

    public override void OnTick()
    {
        try
        {
            _objectCache?.Tick();

            if (_loginComplete)
            {
                if (++_vitalsTickCounter >= 30)
                {
                    _vitalsTickCounter = 0;
                    if (Host.HasGetPlayerVitals &&
                        Host.TryGetPlayerVitals(out uint hp, out uint maxHp, out uint st, out uint maxSt, out uint mp, out uint maxMp))
                    {
                        _vitals.CurrentHealth = hp;
                        _vitals.MaxHealth = maxHp;
                        _vitals.CurrentStamina = st;
                        _vitals.MaxStamina = maxSt;
                        _vitals.CurrentMana = mp;
                        _vitals.MaxMana = maxMp;
                    }
                }

                _buffManager?.OnHeartbeat();

                // Combat runs first — it can claim priority over navigation
                var settings = _dashboard?.Settings;
                if (settings == null)
                    return;

                // AutoCram / AutoStack — only while idle (not looting a corpse, not crafting).
                // Gated on busy count inside the manager so it won't move items mid-cast.
                if (_inventoryManager != null
                    && _openedContainerId == 0
                    && _targetCorpseId == 0)
                {
                    _inventoryManager.OnHeartbeat(_busyCount);
                }

                // Missile crafting runs before combat — blocks everything while active
                if (_missileCraftingManager != null && settings.IsMacroRunning)
                {
                    _missileCraftingManager.ProcessCrafting();
                    if (_missileCraftingManager.IsCrafting)
                    {
                        _metaManager?.Think();
                        return; // Block combat, nav, looting until crafting finishes
                    }
                }

                if (settings.BoostNavPriority)
                {
                    if (string.Equals(settings.CurrentState, "Combat", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(settings.CurrentState, "Looting", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.CurrentState = "Idle";
                    }

                    _combatPausedNav = false;
                    _corpsePausedNav = false;
                    _navigationEngine?.Tick();
                    _metaManager?.Think();
                    return;
                }

                if (settings.BoostLootPriority)
                {
                    TickCorpseOpening();
                    if (!IsCorpseNavigationClaimActive(settings))
                        _combatManager?.OnHeartbeat();
                }
                else
                {
                    _combatManager?.OnHeartbeat();
                    TickCorpseOpening();
                }
                bool combatBlocking = settings.EnableCombat
                                   && _combatManager != null
                                   && _combatManager.HasTargets;
                bool corpseBlocking = IsCorpseNavigationClaimActive(settings);

                // Track when combat stops blocking so we can hold nav
                // for a grace period — corpse CreateObject events arrive
                // a tick or two after the kill, and nav would walk away.
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (combatBlocking)
                {
                    _combatEndedAt = 0; // still fighting
                }
                else if (_combatPausedNav && _combatEndedAt == 0)
                {
                    _combatEndedAt = now; // combat just ended this tick
                }

                bool lootGraceActive = settings.EnableLooting
                                    && _combatEndedAt != 0
                                    && (now - _combatEndedAt) < LootGraceMs;

                if (combatBlocking || corpseBlocking || lootGraceActive)
                {
                    // Stop nav movement immediately the first tick another controller takes over.
                    if (combatBlocking && !_combatPausedNav)
                    {
                        _navigationEngine?.Stop();
                        if (Host.HasStopCompletely)
                            Host.StopCompletely();
                        _combatPausedNav = true;
                    }

                    if ((corpseBlocking || lootGraceActive) && !_corpsePausedNav)
                    {
                        _navigationEngine?.Stop();
                        if (Host.HasStopCompletely)
                            Host.StopCompletely();
                        _corpsePausedNav = true;
                    }

                }
                else
                {
                    _combatPausedNav = false;
                    _corpsePausedNav = false;
                    _combatEndedAt = 0;
                    _navigationEngine?.Tick();
                }

                _metaManager?.Think();
            }
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] OnTick exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override void OnBarAction()
    {
        if (!_initialized || !_loginComplete)
            return;

        _windowVisible = !_windowVisible;
    }

    public override void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId)
    {
        _currentTargetId = currentTargetId;
        _dashboard?.SetSelectedTarget(currentTargetId);

        // Appraise the target so the server sends back its vitals (including MaxHealth).
        // Follow up with QueryHealth to resolve the ratio → absolute HP values.
        if (currentTargetId != 0)
        {
            if (Host.HasRequestId) Host.RequestId(currentTargetId);
            if (Host.HasQueryHealth) Host.QueryHealth(currentTargetId);
        }
    }

    public override void OnCombatModeChange(int currentCombatMode, int previousCombatMode)
    {
        _currentCombatMode = currentCombatMode;
        if (_buffManager is not null) _buffManager.CurrentCombatMode = currentCombatMode;
        if (_combatManager is not null) _combatManager.CurrentCombatMode = currentCombatMode;
        if (_missileCraftingManager is not null) _missileCraftingManager.CurrentCombatMode = currentCombatMode;
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (string.IsNullOrEmpty(text)) return;
        _buffManager?.OnChatWindowText(text, chatType);
        _combatManager?.HandleChatForDebuffs(text);
        _missileCraftingManager?.HandleChat(text);
        _metaManager?.HandleChat(text);
    }

    private int _createObjectCount;
    public override void OnCreateObject(uint objectId)
    {
        _createObjectCount++;
        if (_createObjectCount <= 3)
            Host.Log($"[RynthAi] OnCreateObject #{_createObjectCount}: id=0x{objectId:X8}, cache={(_objectCache != null ? "ok" : "NULL")}");
        _objectCache?.OnCreateObject(objectId);
    }

    public override void OnDeleteObject(uint objectId)
    {
        _objectCache?.OnDeleteObject(objectId);
        HandleCorpseObjectDeleted(objectId);
    }

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        _objectCache?.OnUpdateHealth(targetId);
        _dashboard?.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);
        if (_loginComplete && targetId == _playerId && maxHealth > 0)
        {
            _vitals.CurrentHealth = currentHealth;
            _vitals.MaxHealth = maxHealth;
        }
    }

    public override void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        _buffManager?.OnEnchantmentAdded(spellId, durationSeconds);
    }

    public override void OnEnchantmentRemoved(uint enchantmentId)
    {
        _buffManager?.OnEnchantmentRemoved(enchantmentId);
    }

    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (!_initialized || !_loginComplete || string.IsNullOrEmpty(text))
            return;

        string trimmed = text.Trim();
        if (!trimmed.StartsWith("/na", StringComparison.OrdinalIgnoreCase))
            return;

        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Bare "/na" or "/na help" — show command list
        if (parts.Length < 2 || parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            HandleHelpCommand();
            return;
        }

        string cmd = parts[1].ToLower();
        eat = 1;

        switch (cmd)
        {
            case "power":
                HandlePowerCommand(parts);
                break;
            case "raycast":
                HandleRaycastCommand(parts);
                break;
            case "lostest":
                HandleLosTestCommand();
                break;
            case "cast":
                HandleCastCommand(parts);
                break;
            case "cache":
                HandleCacheCommand();
                break;
            case "cache2":
                HandleCache2Command();
                break;
            case "buffs":
                if (_buffManager == null) ChatLine("[RynthAi] BuffManager not ready (not logged in yet).");
                else _buffManager.PrintBuffDebug();
                break;
            case "attackable":
                HandleAttackableCommand();
                break;
            case "mexec":
                HandleMexecCommand(parts);
                break;
            case "listvars":
                HandleListVarsCommand();
                break;
            case "dumpprops":
                HandleDumpPropsCommand();
                break;
            case "wielded":
                HandleWieldedCommand();
                break;
            case "scan":
                HandleScanCommand();
                break;
            case "buildinfo":
                HandleBuildInfoCommand();
                break;
            case "navdebug":
                HandleNavDebugCommand();
                break;
            case "lootparse":
                HandleLootParseCommand(trimmed);
                break;
            case "lootcheckinv":
                HandleLootCheckInventoryCommand(trimmed);
                break;
            case "corpseinfo":
                HandleCorpseInfoCommand();
                break;
            case "corpsecheck":
                HandleCorpseCheckCommand(parts);
                break;
            case "corpseopen":
                HandleCorpseOpenCommand();
                break;
            case "fellow":
            case "fellowship":
                HandleFellowshipCommand(parts);
                break;
            case "fellowinfo":
                HandleFellowshipInfoCommand();
                break;
            case "dumpinv":
                HandleDumpInventoryCommand();
                break;
            default:
                eat = 0; // not our command, let it through
                break;
        }
    }

    public override void OnRender()
    {
        if (!_initialized || !_loginComplete || Host.ImGuiContext == IntPtr.Zero)
            return;

        // ── Push FPS limit settings to engine each frame ──────────
        var settings = _dashboard?.Settings;
        if (settings != null)
            Host.SetFpsLimit(settings.EnableFPSLimit, settings.TargetFPSFocused, settings.TargetFPSBackground);

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            // Always render nav markers when logged in (route visible even with UI hidden)
            _navMarkerRenderer?.Render();

            if (_windowVisible && _dashboard is not null)
            {
                _dashboard.Render();
                if (_dashboard.CloseRequested)
                {
                    _windowVisible = false;
                    _dashboard.CloseRequested = false;
                }
            }
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] OnRender exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ImGui.SetCurrentContext(previousContext);
        }
    }

    private void EnsureImGuiResolver()
    {
        if (_imguiResolverConfigured)
            return;

        NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, ResolveImGuiNative);
        _imguiResolverConfigured = true;
        Log("RynthAi: ImGui native resolver bound to engine cimgui.");
    }

    private static IntPtr ResolveImGuiNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "cimgui", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(libraryName, "cimgui.dll", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        IntPtr module = GetModuleHandleA("RynthCore.cimgui.dll");
        if (module != IntPtr.Zero)
            return module;

        module = GetModuleHandleA("cimgui.dll");
        return module;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleA(string lpModuleName);
}

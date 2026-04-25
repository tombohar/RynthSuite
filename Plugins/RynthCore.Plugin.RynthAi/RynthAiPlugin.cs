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
    private RadarWallRenderer? _radarWallRenderer;
    private TerrainPassabilityOverlay? _terrainOverlay;
    private MainLogic? _raycast;
    private WorldObjectCache? _objectCache;
    private CharacterSkills? _charSkills;
    private SpellManager? _spellManager;
    private BuffManager? _buffManager;
    private CombatManager? _combatManager;
    private MissileCraftingManager? _missileCraftingManager;
    private FellowshipTracker? _fellowshipTracker;
    private MetaManager? _metaManager;
    private QuestTracker? _questTracker;
    private InventoryManager? _inventoryManager;
    private SalvageManager? _salvageManager;
    private ManaStoneManager? _manaStoneManager;
    private Jumper? _jumper;
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

    // Give queue — drained one item per tick with a cooldown (interval comes from settings)
    private readonly Queue<(uint itemId, uint targetId, int stackSize)> _pendingGives = new();
    private DateTime _lastGiveAt = DateTime.MinValue;
    private RynthCore.Loot.LootProfile? _nativeLootProfile;
    private string _nativeLootProfilePath = string.Empty;
    private DateTime _nativeLootProfileTime = DateTime.MinValue;
    private static bool _imguiResolverConfigured;

    public override int Initialize()
    {
        if (Host.ImGuiContext == IntPtr.Zero)
            return 11;

        EnsureImGuiResolver();
        ComponentDatabase.SetLog(msg => Log(msg));
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
        _radarWallRenderer?.Flush();
        _radarWallRenderer = null;
        _terrainOverlay = null;
        _raycast?.Dispose();
        _raycast = null;
        _combatManager?.Dispose();
        _combatManager = null;
        _fellowshipTracker?.Dispose();
        _fellowshipTracker = null;
        _metaManager = null;
        _questTracker = null;
        _inventoryManager = null;
        _salvageManager = null;
        _manaStoneManager = null;
        _buffManager?.Dispose();
        _buffManager = null;
        _spellManager = null;
        _jumper?.Cancel();
        _jumper = null;
        _objectCache = null;
        _playerId = 0;
        _initialized = false;
        _loginComplete = false;
        _windowVisible = false;
        _dashboard = null;
    }

    private DateTime _loginCompletedAt = DateTime.MinValue;

    public override void OnLoginComplete()
    {
        if (!_initialized || _dashboard is null)
            return;

        _loginCompletedAt = DateTime.Now;
        _loginComplete = true;
        _dashboard.OnLoginComplete();
        _dashboard.ChatSubmitHandler = HandleRynthChatSubmit;
        _navigationEngine = new NavigationEngine(Host, _dashboard.Settings);
        if (_objectCache != null) _navigationEngine.SetWorldObjectCache(_objectCache);
        _navMarkerRenderer = new NavMarkerRenderer(Host, _dashboard.Settings);
        _radarWallRenderer = new RadarWallRenderer(Host, _dashboard.Settings);
        _terrainOverlay = new TerrainPassabilityOverlay(Host);
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
                    _terrainOverlay?.SetRaycast(raycastRef);
                    _radarWallRenderer?.SetRaycast(raycastRef);
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
        _navigationEngine?.SetPlayerId(_playerId);
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
        // Use the same per-character folder as the dashboard settings so all
        // character data lives in one place and the directory is guaranteed to exist.
        if (!string.IsNullOrEmpty(_dashboard.CharFolder))
            _buffManager.SetTimerPath(_dashboard.CharFolder);

        _dashboard.OnForceRebuffRequested       = () => _buffManager?.ForceFullRebuff();
        _dashboard.OnCancelForceRebuffRequested = () => _buffManager?.CancelBuffing();

        // Override disk timers with live client memory — gets accurate remaining times
        // including login-restored enchantments the event hook missed at startup.
        int liveCount = _buffManager.RefreshFromLiveMemory();
        if (liveCount >= 0)
            Host.WriteToChat($"[RynthAi] Loaded {liveCount} active buff timer(s) from client memory.", 1);

        // Sync the actual current combat mode — _currentCombatMode defaults to NonCombat and
        // OnCombatModeChange doesn't re-fire on hot-reload, so read it directly from AC memory.
        if (Host.HasGetCurrentCombatMode)
            _currentCombatMode = Host.GetCurrentCombatMode();

        _combatManager = new CombatManager(Host, _dashboard.Settings, _objectCache!, _spellManager);
        _combatManager.SetCharacterSkills(_charSkills);
        _combatManager.SetPlayerId(_playerId);
        _combatManager.CurrentCombatMode = _currentCombatMode;
        _buffManager.CurrentCombatMode = _currentCombatMode;
        _navigationEngine?.SetCombatManager(_combatManager);

        _missileCraftingManager = new MissileCraftingManager(Host, _dashboard.Settings);
        _missileCraftingManager.SetObjectCache(_objectCache!);
        _missileCraftingManager.SetCharacterSkills(_charSkills);
        _missileCraftingManager.CurrentCombatMode = _currentCombatMode;
        _dashboard.SetMissileCraftingManager(_missileCraftingManager);

        _fellowshipTracker?.Dispose();
        _fellowshipTracker = new FellowshipTracker();

        _questTracker = new QuestTracker(Host);
        _questTracker.Refresh(); // auto-populate quest flags on login

        _metaManager = new MetaManager(_dashboard.Settings, Host, _vitals);
        _metaManager.SetPlayerId(_playerId);
        _metaManager.SetMtCommandHandler(HandleMtCommand);
        _metaManager.SetRaCommandHandler(HandleRaCommand);
        if (_objectCache != null) _metaManager.SetObjectCache(_objectCache);
        _metaManager.SetFellowshipTracker(_fellowshipTracker);
        _metaManager.SetQuestTracker(_questTracker);
        if (_buffManager != null) _metaManager.SetBuffManager(_buffManager);

        if (_objectCache != null)
        {
            _inventoryManager  = new InventoryManager(Host, _dashboard.Settings, _objectCache);
            _manaStoneManager  = new ManaStoneManager(Host, _dashboard.Settings, _objectCache);
        }

        _salvageManager = new SalvageManager(Host, _dashboard.Settings, _objectCache);

        _jumper = new Jumper(Host, _dashboard.Settings, s => Host.WriteToChat(s, 1));

        Log("RynthAi: login complete, legacy ImGui dashboard ready.");
    }

    private bool _combatDbgActive = false;
    private int _combatDbgFrames = 0;
    private bool _buffingPausedNav;
    private bool _combatPausedNav;
    private bool _corpsePausedNav;
    private long _combatEndedAt;       // Timestamp when combat stopped blocking
    private const long LootGraceMs = 2000; // Hold nav after combat ends so corpses can spawn

    public override void OnTick()
    {
        try
        {
            _objectCache?.Tick();
            _questTracker?.Tick();
            DrainGiveQueue();
            _jumper?.Tick();

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

                CheckBusyTimeout();
                _buffManager?.OnHeartbeat();

                var settings = _dashboard?.Settings;
                if (settings == null)
                    return;

                // Gap-fill: BuffManager sets BotAction = "Buffing" while casting, but
                // resets it to "Default" between casts. If buffs are still needed,
                // keep it locked to "Buffing" so nothing sneaks in between casts.
                if (_buffManager != null
                    && settings.EnableBuffing
                    && _buffManager.NeedsAnyBuff()
                    && settings.BotAction != "Buffing")
                {
                    settings.BotAction = "Buffing";
                }

                // ── Priority 1: Buffing ───────────────────────────────────────────
                // Blocks combat, looting, and navigation entirely.
                if (settings.IsMacroRunning
                    && string.Equals(settings.BotAction, "Buffing", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_buffingPausedNav)
                    {
                        _navigationEngine?.Stop();
                        if (Host.HasStopCompletely) Host.StopCompletely();
                        _buffingPausedNav   = true;
                        _combatPausedNav    = false;
                        _corpsePausedNav    = false;
                        _combatEndedAt      = 0;
                    }
                    _metaManager?.Think();
                    return;
                }
                _buffingPausedNav = false;

                // AutoCram / AutoStack — only while idle (not looting a corpse, not crafting).
                // Gated on busy count inside the manager so it won't move items mid-cast.
                // Skip InventoryManager for a short settle window after login.
                // Its first call does a forced GetDirectInventory which walks every
                // container via native calls — when the cache is still classifying
                // hundreds of pending CreateObjects (RL hot-reload, fresh login),
                // the concurrent native walk has been causing intermittent crashes.
                bool inventorySettled = (DateTime.Now - _loginCompletedAt).TotalMilliseconds > 3000;
                if (inventorySettled
                    && _inventoryManager != null
                    && _openedContainerId == 0
                    && _targetCorpseId == 0)
                {
                    _inventoryManager.OnHeartbeat(_busyCount);
                }

                _salvageManager?.OnTick(_busyCount);

                // Salvage-priority gap-fill: while a container is open for active
                // looting OR while the salvage queue has items waiting, hold
                // BotAction at "Salvaging" so CombatManager.Think() won't fire
                // and pull the bot away mid-session. We DO override "Combat"
                // here — finishing the salvage queue takes priority over
                // engaging a new monster, otherwise items meant for salvage
                // pile up in inventory after combat resumes navigation.
                // "Buffing" still wins (buffs dropping = death).
                if (settings.IsMacroRunning
                    && settings.BotAction != "Buffing"
                    && (_openedContainerId != 0 || _salvageManager?.IsBusy == true))
                {
                    settings.BotAction = "Salvaging";
                }
                else if (settings.BotAction == "Salvaging"
                         && _openedContainerId == 0
                         && _salvageManager?.IsBusy != true)
                {
                    settings.BotAction = "Default";
                }

                // Mana stone tapping — runs after salvage, independent of looting state.
                _manaStoneManager?.OnHeartbeat(_busyCount);

                // Missile crafting runs before combat — blocks everything while active.
                // Gated on the same settle window as InventoryManager: ProcessCrafting
                // also does a forced GetDirectInventory walk which races with cache
                // classification right after login/RL.
                if (inventorySettled && _missileCraftingManager != null && settings.IsMacroRunning)
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
                    if (string.Equals(settings.BotAction, "Combat", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(settings.BotAction, "Looting", StringComparison.OrdinalIgnoreCase))
                    {
                        settings.BotAction = "Default";
                    }

                    _combatPausedNav = false;
                    _corpsePausedNav = false;
                    bool doorBlocking = TickDoorInteraction();
                    if (!doorBlocking)
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

                // Nav must keep ticking during portal/recall actions so teleport
                // detection works — combat and looting must not suppress it.
                bool navInPortal = _navigationEngine?.IsInPortalAction == true;

                if ((combatBlocking || corpseBlocking || lootGraceActive) && !navInPortal)
                {
                    // Stop nav movement immediately the first tick another controller takes over.
                    if (combatBlocking && !_combatPausedNav)
                    {
                        _navigationEngine?.Stop();
                        if (Host.HasStopCompletely)
                            Host.StopCompletely();
                        _combatPausedNav = true;
                        ResetDoorState();
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

                    bool doorBlocking = TickDoorInteraction();
                    if (!doorBlocking)
                        _navigationEngine?.Tick();
                }

                TickPendingMtLoot();
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

    private bool _lootInspectMode = true; // always on; /ra lootcheck off to disable

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

        if (_lootInspectMode && currentTargetId != 0)
        {
            int sid = unchecked((int)currentTargetId);
            WorldObject? obj = _objectCache?[sid];
            // Skip non-items (monsters, players, NPCs, doors, corpses, portals, etc.)
            if (obj != null && IsLootableClass(obj.ObjectClass))
                InspectLootRuleForItem(sid, quiet: true);
        }
    }

    private static bool IsLootableClass(AcObjectClass cls) => cls is not (
        AcObjectClass.Unknown  or AcObjectClass.Monster   or AcObjectClass.Player  or
        AcObjectClass.Vendor   or AcObjectClass.Door      or AcObjectClass.Corpse  or
        AcObjectClass.Lifestone or AcObjectClass.Portal   or AcObjectClass.Housing or
        AcObjectClass.Npc      or AcObjectClass.CombatPet or AcObjectClass.Sign);

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
        _dashboard?.PushChatLine(text, chatType);
        _buffManager?.OnChatWindowText(text, chatType);
        _manaStoneManager?.OnChatWindowText(text);
        _combatManager?.HandleChatForDebuffs(text);
        _missileCraftingManager?.HandleChat(text);
        _metaManager?.HandleChat(text);
        _questTracker?.OnChatLine(text);
    }

    public override void OnCreateObject(uint objectId)
    {
        try { _objectCache?.OnCreateObject(objectId); }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] OnCreateObject EXCEPTION on id=0x{objectId:X8}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override void OnDeleteObject(uint objectId)
    {
        try
        {
            _objectCache?.OnDeleteObject(objectId);
            HandleCorpseObjectDeleted(objectId);
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] OnDeleteObject EXCEPTION on id=0x{objectId:X8}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
    {
        _objectCache?.OnUpdateHealth(targetId, healthRatio);
        _dashboard?.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);
        if (_loginComplete && targetId == _playerId && maxHealth > 0)
        {
            _vitals.CurrentHealth = currentHealth;
            _vitals.MaxHealth = maxHealth;
        }

        // When a creature's health changes, something hit it — reset its miss counter
        // so the blacklist doesn't trigger on valid in-combat targets.
        if (targetId != _playerId)
            _combatManager?.ReportDamageOnTarget((int)targetId);
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

        // Mag-Tools /mt command compatibility
        if (trimmed.StartsWith("/mt ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/mt", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            if (!HandleMtCommand(trimmed))
                ChatLine($"[RynthAi] Unrecognized /mt command. Try /mt opt list");
            return;
        }

        // UtilityBelt /ub command compatibility
        if (trimmed.StartsWith("/ub ", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            if (!HandleUbCommand(trimmed))
                ChatLine("[RynthAi] Unrecognized /ub command.");
            return;
        }

        if (!trimmed.StartsWith("/ra", StringComparison.OrdinalIgnoreCase))
            return;

        string[] parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Bare "/ra" or "/ra help" — show command list
        if (parts.Length < 2 || parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            HandleHelpCommand();
            return;
        }

        string cmd = parts[1].ToLower();
        eat = 1;
        DispatchRaCommand(cmd, parts, ref eat);
    }

    /// <summary>
    /// Called by RynthChatUi when the user submits a line. Slash commands we
    /// own (/ra, /mt, /ub) are routed through OnChatBarEnter so they run the
    /// same code path as the retail chatbar. Anything else falls through to
    /// Host.InvokeChatParser (say/tell/channels via direct Event_* calls).
    /// </summary>
    private void HandleRynthChatSubmit(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        int eat = 0;
        try { OnChatBarEnter(text, ref eat); }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] RynthChat OnChatBarEnter threw: {ex.GetType().Name}: {ex.Message}");
        }

        if (eat != 0)
            return; // handled locally (/ra, /mt, /ub, etc.)

        if (Host.HasInvokeChatParser)
            Host.InvokeChatParser(text);
    }

    internal void EnqueueGive(uint itemId, uint targetId, int stackSize)
        => _pendingGives.Enqueue((itemId, targetId, stackSize));

    internal void CancelGiveQueue()
    {
        int count = _pendingGives.Count;
        _pendingGives.Clear();
        ChatLine(count > 0
            ? $"[RynthAi] Give queue cancelled ({count} item(s) remaining)."
            : "[RynthAi] Give queue is already empty.");
    }

    private void DrainGiveQueue()
    {
        if (_pendingGives.Count == 0) return;
        int intervalMs = _dashboard?.Settings.GiveQueueIntervalMs ?? 150;
        if ((DateTime.UtcNow - _lastGiveAt).TotalMilliseconds < intervalMs) return;

        var (itemId, targetId, stackSize) = _pendingGives.Dequeue();
        Host.MoveItemExternal(itemId, targetId, stackSize);
        _lastGiveAt = DateTime.UtcNow;

        if (_pendingGives.Count == 0)
            ChatLine("[RynthAi] Give queue complete.");
    }

    /// <summary>
    /// Called by MetaManager for ChatCommand actions that start with /ra,
    /// so meta scripts can dispatch /ra commands without going through InvokeChatParser.
    /// </summary>
    private void HandleRaCommand(string fullCommand)
    {
        if (!_initialized || !_loginComplete) return;

        string[] parts = fullCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;

        string cmd = parts[1].ToLower();
        int eat = 0; // not used for meta dispatch, but needed for shared helpers
        DispatchRaCommand(cmd, parts, ref eat);
    }

    private void DispatchRaCommand(string cmd, string[] parts, ref int eat)
    {
        // Need trimmed original for commands that use it
        string trimmed = string.Join(" ", parts);
        switch (cmd)
        {
            case "power":        HandlePowerCommand(parts); break;
            case "raycast":      HandleRaycastCommand(parts); break;
            case "lostest":      HandleLosTestCommand(parts); break;
            case "landtest":     HandleLandTestCommand(); break;
            case "rayland":      HandleRaylandCommand(); break;
            case "cast":         HandleCastCommand(parts); break;
            case "cache":        HandleCacheCommand(); break;
            case "cache2":       HandleCache2Command(); break;
            case "buffs":
                if (_buffManager == null) { ChatLine("[RynthAi] BuffManager not ready (not logged in yet)."); break; }
                if (parts.Length >= 3 && parts[2].Equals("item", StringComparison.OrdinalIgnoreCase))
                    _buffManager.PrintItemBuffDebug();
                else
                    _buffManager.PrintBuffDebug();
                break;
            case "attackable":   HandleAttackableCommand(); break;
            case "mexec":        HandleMexecCommand(parts); break;
            case "listvars":     HandleListVarsCommand(); break;
            case "listpvars":    HandleListPvarsCommand(); break;
            case "listgvars":    HandleListGvarsCommand(); break;
            case "dumpprops":    HandleDumpPropsCommand(); break;
            case "wielded":      HandleWieldedCommand(); break;
            case "scan":         HandleScanCommand(); break;
            case "buildinfo":    HandleBuildInfoCommand(); break;
            case "navdebug":     HandleNavDebugCommand(); break;
            case "addnavpt":     HandleAddNavPointCommand(); break;
            case "lootparse":    HandleLootParseCommand(trimmed); break;
            case "lootcheckinv": HandleLootCheckInventoryCommand(trimmed); break;
            case "lootcheck":    HandleLootCheckSelectedCommand(parts); break;
            case "corpseinfo":   HandleCorpseInfoCommand(); break;
            case "corpsecheck":  HandleCorpseCheckCommand(parts); break;
            case "corpseopen":   HandleCorpseOpenCommand(); break;
            case "fellow":
            case "fellowship":   HandleFellowshipCommand(parts); break;
            case "fellowinfo":   HandleFellowshipInfoCommand(); break;
            case "dumpinv":      HandleDumpInventoryCommand(); break;
            case "combat":       HandleCombatStateCommand(); break;
            case "mapdump":      HandleMapDumpCommand(); break;
            case "clearbusy":    HandleClearBusyCommand(); break;
            case "forcebuff":        HandleForceBuff(); break;
            case "cancelforcebuff":  HandleCancelForceBuff(); break;
            case "settings":     HandleSettingsCommand(parts); break;
            case "busyinfo":     HandleBusyInfoCommand(); break;
            // give variants — first-match (with optional count prefix)
            case "give":         HandleGiveCommand(parts, GiveItemMatch.Exact,   partialPlayer: false); break;
            case "givep":        HandleGiveCommand(parts, GiveItemMatch.Partial, partialPlayer: false); break;
            case "givexp":       HandleGiveCommand(parts, GiveItemMatch.Exact,   partialPlayer: true);  break;
            case "givepp":       HandleGiveCommand(parts, GiveItemMatch.Partial, partialPlayer: true);  break;
            case "giver":        HandleGiveCommand(parts, GiveItemMatch.Regex,   partialPlayer: false); break;
            // give-All variants — give every matching stack (sub-command "stop" cancels the queue)
            case "givea":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Exact,   partialPlayer: false, allItems: true);
                break;
            case "giveap":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Partial, partialPlayer: false, allItems: true);
                break;
            case "giveaxp":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Exact,   partialPlayer: true,  allItems: true);
                break;
            case "giveapp":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Partial, partialPlayer: true,  allItems: true);
                break;
            case "givear":
                if (parts.Length >= 3 && parts[2].Equals("stop", StringComparison.OrdinalIgnoreCase)) CancelGiveQueue();
                else HandleGiveCommand(parts, GiveItemMatch.Regex,   partialPlayer: false, allItems: true);
                break;
            case "ig":           HandleGiveProfileCommand(parts, partialPlayer: false); break;
            case "igp":          HandleGiveProfileCommand(parts, partialPlayer: true); break;
            // use variants
            case "use":          HandleUseCommand(parts, inv: true,  land: true,  partial: false); break;
            case "usei":         HandleUseCommand(parts, inv: true,  land: false, partial: false); break;
            case "usel":         HandleUseCommand(parts, inv: false, land: true,  partial: false); break;
            case "usepi": case "useip": HandleUseCommand(parts, inv: true,  land: false, partial: true); break;
            case "uselp": case "usepl": HandleUseCommand(parts, inv: false, land: true,  partial: true); break;
            case "usep":         HandleUseCommand(parts, inv: true,  land: true,  partial: true); break;
            // select variants
            case "select":       HandleSelectCommand(parts, inv: true,  land: true,  partial: false); break;
            case "selecti":      HandleSelectCommand(parts, inv: true,  land: false, partial: false); break;
            case "selectl":      HandleSelectCommand(parts, inv: false, land: true,  partial: false); break;
            case "selectpi": case "selectip": HandleSelectCommand(parts, inv: true,  land: false, partial: true); break;
            case "selectlp": case "selectpl": HandleSelectCommand(parts, inv: false, land: true,  partial: true); break;
            case "selectp":      HandleSelectCommand(parts, inv: true,  land: true,  partial: true); break;
            default:
                if (cmd.StartsWith("jump", StringComparison.Ordinal))
                {
                    HandleJumpCommand(cmd, parts);
                    break;
                }
                eat = 0;
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
        {
            Host.SetFpsLimit(settings.EnableFPSLimit, settings.TargetFPSFocused, settings.TargetFPSBackground);
            if (Host.HasSetRadarSuppressed)
                Host.SetRadarSuppressed(settings.SuppressRetailRadar);
            if (Host.HasSetChatSuppressed)
                Host.SetChatSuppressed(settings.SuppressRetailChat);
            if (Host.HasSetPowerbarSuppressed)
                Host.SetPowerbarSuppressed(settings.SuppressRetailPowerbar);
        }

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            // Clear 3D geometry once per frame, then let all renderers populate it
            if (_loginComplete && Host.HasNav3D)
                Host.Nav3DClear();

            // Always render nav markers and terrain overlay when logged in
            _navMarkerRenderer?.Render();
            _radarWallRenderer?.Render();
            if (_dashboard?.Settings.ShowTerrainPassability == true)
                _terrainOverlay?.Render();

            // Map renders independently of whether the main dashboard is visible.
            _dashboard?.RenderMapWindow();

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

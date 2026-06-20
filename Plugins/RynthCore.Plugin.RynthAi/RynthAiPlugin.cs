using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginCore;
using RynthCore.Loot.VTank;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin : RynthPluginBase
{
    internal static readonly IntPtr NamePointer = Marshal.StringToHGlobalAnsi("RynthAi");
    internal static readonly IntPtr VersionPointer = Marshal.StringToHGlobalAnsi("0.5.0-legacy-ui");

    private LegacyDashboardRenderer? _dashboard;

    /// <summary>Snapshot bridge accessor for engine-side panels.</summary>
    internal LegacyDashboardRenderer? DashboardRenderer => _dashboard;
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
    private WeaponSwapGate? _weaponSwapGate;
    private MissileCraftingManager? _missileCraftingManager;
    private FellowshipTracker? _fellowshipTracker;
    private MetaManager? _metaManager;
    private QuestTracker? _questTracker;
    private InventoryManager? _inventoryManager;
    private SalvageManager? _salvageManager;
    private ManaStoneManager? _manaStoneManager;
    private PetManager? _petManager;
    private Jumper? _jumper;
    private ActivityArbiter? _arbiter; // STEP 1: shadow-mode only (ACTIVITY_ARBITER_PLAN.md)
    private PlayerVitalsCache _vitals = new();
    private uint _playerId;
    private int _vitalsTickCounter;
    private bool _initialized;
    private bool _loginComplete;
    private bool _patrolOnLoginPending;
    // ── Dungeon-patrol hazard tracking ───────────────────────────────────────
    // A dunnav-patrol route is built once from the hazard cells known at that
    // instant — but lava/acid hotspots are usually sighted only as the bot walks
    // up to them, after the route is already running. These fields let OnTick
    // notice a newly-sighted hazard (HazardVersion bumped) and rebuild the patrol
    // around it, so the route treats the hotspot as a wall instead of looping
    // through it. _dunPatrolLandblock guards against rebuilding after the bot has
    // portalled to a different dungeon.
    private bool _dunPatrolActive;
    private uint _dunPatrolLandblock;
    private int  _dunPatrolHazardVersion;
    private DateTime _notInWorldSince = DateTime.MinValue;
    private bool _windowVisible;
    private int _tickDiag;
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

    private CreatureData.CreatureProfileStore? _creatureStore;
    internal CreatureData.CreatureProfileStore? CreatureStore => _creatureStore;
    // Per-character learned combat damage (avg damage by wcid/element/tier +
    // learned HP-to-kill), used by CombatManager for kill-shot prediction.
    private CreatureData.MonsterDamageStore? _damageStore;
    // wcids appraised this session (AutoId of nearby mobs). Surfaced as bare rows in the
    // Damage table so monsters populate as you encounter them, before you've fought them.
    private readonly HashSet<uint> _seenMonstersThisSession = new();
    private int _creatureSaveTickCounter;
    private int _settingsSaveTickCounter;
    private int _settingsLoadRetryCounter;

    public override int Initialize()
    {
        // ImGuiContext is null when the engine is in Decal coexistence mode
        // (no EndScene hook, no ImGui). The legacy ImGui dashboard's
        // constructor is pure object setup — it doesn't call any ImGui
        // APIs. Only Render() does, and the render-side guard already
        // checks Host.ImGuiContext != IntPtr.Zero. So it's safe to build
        // the dashboard regardless: settings storage, sub-UI managers,
        // and all the downstream wiring (Settings, callbacks, etc.) work
        // identically; only on-screen ImGui rendering is skipped.
        bool hasImGui = Host.ImGuiContext != IntPtr.Zero;

        if (hasImGui)
            EnsureImGuiResolver();

        ComponentDatabase.SetLog(msg => Log(msg));
        _dashboard = new LegacyDashboardRenderer(Host);
        _objectCache = new WorldObjectCache(Host); // must exist before CreateObject events fire during login
        _creatureStore = new CreatureData.CreatureProfileStore();
        try { _creatureStore.Load(); } catch { }
        _damageStore = new CreatureData.MonsterDamageStore();
        Func<string, CreatureData.CreatureProfile?> lookup = ruleName =>
        {
            if (_creatureStore == null || string.IsNullOrEmpty(ruleName)) return null;
            return _creatureStore.TryGetByName(ruleName, out var p) ? p : null;
        };
        _dashboard.MonstersUi.CreatureLookup = lookup;
        _dashboard.CreatureLookupForRules = lookup;
        _initialized = true;
        _loginComplete = false;
        _windowVisible = false;
        // ⚠ Wording matters: this branch keys on ImGuiContext==0, which is true
        // for the normal EnableImGuiBackend=false config — it does NOT mean
        // Decal is present. The old "Decal coexistence mode" text here misled
        // two crash investigations (2026-06-11) and weeks of session notes.
        Log(hasImGui
            ? "RynthAi: legacy ImGui dashboard initialized."
            : "RynthAi: initialized in ImGui-less mode (no ImGui context — Avalonia panels drive the UI). NOTE: this does not imply Decal is present.");
        return 0;
    }

    public override void Shutdown()
    {
        long t0 = Environment.TickCount64;
        try { _dashboard?.SaveSettings(); } catch { }
        long tAfterSettings = Environment.TickCount64;
        TeardownSession();
        long tAfterTeardown = Environment.TickCount64;
        try { _creatureStore?.SaveIfDirty(); } catch { }
        try { _damageStore?.SaveIfDirty(); } catch { }
        long tAfterStore = Environment.TickCount64;
        _creatureStore = null;
        _damageStore = null;
        _objectCache = null;
        _initialized = false;
        _dashboard = null;
        Log($"RynthAi: Shutdown done — SaveSettings={tAfterSettings - t0} ms, TeardownSession={tAfterTeardown - tAfterSettings} ms, SaveCreatureStore={tAfterStore - tAfterTeardown} ms, total={tAfterStore - t0} ms");
    }

    /// <summary>
    /// Called by the engine when the player leaves the world (RecvNotice_Logoff).
    /// Stops every per-session subsystem the way Shutdown does, but leaves
    /// _initialized / _dashboard / _objectCache intact so the next OnLoginComplete
    /// can rebuild a fresh session without going through plugin Init again.
    /// </summary>
    public override void OnLogout()
    {
        Log("RynthAi: logout — tearing down session.");
        try { _dashboard?.SaveSettings(); } catch { }
        TeardownSession();
    }

    /// <summary>
    /// Releases every component that depends on being in-world. Idempotent.
    /// Used by both Shutdown (full plugin unload) and OnLogout (session-only).
    /// </summary>
    private void TeardownSession()
    {
        _navigationEngine?.Stop();
        _navigationEngine = null;
        _navMarkerRenderer = null;
        long tFlush0 = Environment.TickCount64;
        _radarWallRenderer?.Flush();
        long tFlushMs = Environment.TickCount64 - tFlush0;
        _radarWallRenderer = null;
        _terrainOverlay = null;
        long tRay0 = Environment.TickCount64;
        _raycast?.Dispose();
        long tRayMs = Environment.TickCount64 - tRay0;
        _raycast = null;
        _combatManager?.Dispose();
        _combatManager = null;
        Log($"RynthAi: TeardownSession — RadarWallFlush={tFlushMs} ms, RaycastDispose={tRayMs} ms");
        _fellowshipTracker?.Dispose();
        _fellowshipTracker = null;
        _metaManager = null;
        _questTracker = null;
        _inventoryManager = null;
        _salvageManager = null;
        _manaStoneManager = null;
        _petManager = null;
        _buffManager?.Dispose();
        _buffManager = null;
        _spellManager = null;
        _missileCraftingManager = null;
        _jumper?.Cancel();
        _jumper = null;
        _playerId = 0;
        _loginComplete = false;
        _windowVisible = false;
        _pendingGives.Clear();
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
                long rayT0 = Environment.TickCount64;
                // We're injected into acclient.exe; its own directory is the AC
                // install with the dats alongside it — correct on any machine.
                // Only pass it if a portal dat is actually present, else null so
                // GeometryLoader.FindACFolder() falls back to path/registry search.
                string? acDir = null;
                try
                {
                    string? exePath = Environment.ProcessPath;
                    string? exeDir = string.IsNullOrEmpty(exePath)
                        ? null : System.IO.Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(exeDir) &&
                        (System.IO.File.Exists(System.IO.Path.Combine(exeDir, "client_portal.dat")) ||
                         System.IO.File.Exists(System.IO.Path.Combine(exeDir, "portal.dat"))))
                        acDir = exeDir;
                }
                catch { }
                bool rayOk = raycastRef.Initialize(acDir);
                long rayMs = Environment.TickCount64 - rayT0;
                Log($"RynthAi: raycast init={rayOk} in {rayMs}ms acDir={acDir ?? "(auto)"} status={raycastRef.StatusMessage}");
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
        {
            _dashboard.LoadSettings(charName);
            _patrolOnLoginPending = _dashboard.Settings.PatrolOnLogin;
        }

        // Restore last-known dashboard visibility so the window reopens where it was after RL/restart.
        _windowVisible = _dashboard.Settings.DashboardVisible;

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
        // Shared weapon-swap serializer — both managers consult it so a buff
        // wand-equip and a combat weapon-equip can't fire within ~3s of each
        // other (the collision behind "you can only move or use one item at a
        // time" and the object-teardown AV).
        _weaponSwapGate ??= new WeaponSwapGate();

        _buffManager = new BuffManager(Host, _dashboard.Settings, _spellManager, _vitals);
        _buffManager.SetWeaponSwapGate(_weaponSwapGate);
        _buffManager.SetCastResolvedCallback(OnBuffCastResolved);
        _buffManager.SetCharacterSkills(_charSkills);
        if (_objectCache != null) _buffManager.SetWorldObjectCache(_objectCache);
        // Use the same per-character folder as the dashboard settings so all
        // character data lives in one place and the directory is guaranteed to exist.
        if (!string.IsNullOrEmpty(_dashboard.CharFolder))
        {
            _buffManager.SetTimerPath(_dashboard.CharFolder);
            _damageStore?.SetCharacter(_dashboard.CharFolder);
            lock (_seenMonstersThisSession) _seenMonstersThisSession.Clear();
        }

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
        _combatManager.SetWeaponSwapGate(_weaponSwapGate);
        _combatManager.SetCharacterSkills(_charSkills);
        _combatManager.SetPlayerId(_playerId);
        _combatManager.SetDamageStores(_creatureStore, _damageStore);
        _navigationEngine?.SetCombatManager(_combatManager);
        // BuffManager.CheckVitals consults CombatManager.HasCloseThreat to pick
        // between in-combat and idle top-off recharge thresholds. Wire here
        // because BuffManager is constructed before CombatManager.
        _buffManager?.SetCombatManager(_combatManager);

        _missileCraftingManager = new MissileCraftingManager(Host, _dashboard.Settings);
        _missileCraftingManager.SetObjectCache(_objectCache!);
        _missileCraftingManager.SetCharacterSkills(_charSkills);
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
        _metaManager.SetCreatureStore(_creatureStore);
        if (_dashboard != null)
            _dashboard.MetaSnapshotProvider = () => _metaManager?.GetStateSnapshot();

        if (_objectCache != null)
        {
            _inventoryManager  = new InventoryManager(Host, _dashboard.Settings, _objectCache);
            _manaStoneManager  = new ManaStoneManager(Host, _dashboard.Settings, _objectCache);
            _petManager        = new PetManager(Host, _dashboard.Settings, _objectCache, _combatManager, _charSkills);
        }

        _salvageManager = new SalvageManager(Host, _dashboard.Settings, _objectCache);
        // Hand the salvage manager a live accessor for the loot profile's
        // SalvageCombine config so combining respects per-material workmanship
        // bands when the profile defines them. Prefer the VTank profile (loaded
        // from .utl) but fall back to the native JSON LootProfile if that's the
        // active source.
        _salvageManager.CombineConfigProvider = () =>
            _loadedLootProfile?.SalvageCombine ?? _nativeLootProfile?.SalvageCombine;

        _jumper = new Jumper(Host, _dashboard.Settings, s => Host.WriteToChat(s, 1));

        Log("RynthAi: login complete, legacy ImGui dashboard ready.");
    }

    private bool _combatDbgActive = false;
    private int _combatDbgFrames = 0;
    private bool _buffingPausedNav;
    private bool _combatPausedNav;
    private bool _corpsePausedNav;
    // Buffing-coma watchdog (see the Priority-1 block): how long BotAction has
    // been continuously 'Buffing', and the once-per-interval recovery bypass.
    private DateTime _buffingHeldSince = DateTime.MinValue;
    private DateTime _lastBuffComaBypassAt = DateTime.MinValue;
    private bool _buffComaWarned;
    private const double BuffComaThresholdMs = 5 * 60_000;  // 5 min continuous Buffing = pathological
    private const double BuffComaBypassEveryMs = 10_000;    // let recovery tick through every ~10s
    private long _combatEndedAt;       // Timestamp when combat stopped blocking
    private const long LootGraceMs = 2000; // Hold nav after combat ends so corpses can spawn

    public override void OnTick()
    {
        bool diag = ++_tickDiag <= 3;
        try
        {
            _dashboard?.DrainMetaCommands();   // apply queued meta edits on this (plugin-tick) thread

            // ── Push settings to engine each tick ──────────────────────
            // OnRender only runs when the engine has an active ImGui pipeline
            // (PluginManager.RenderAll is called from ImGuiController only).
            // OnTick runs unconditionally via PluginManager.TickAll, including
            // in Decal-coexistence mode and when ImGui is disabled. Push the
            // suppression toggles here so the launcher's Avalonia settings
            // panel actually controls the radar/chat/powerbar regardless of
            // whether the in-game ImGui shell is up.
            // (Renamed local to `pushSettings` to avoid collision with the
            // `settings` local declared further down in OnTick.)
            var pushSettings = _dashboard?.Settings;
            if (diag)
                Host.Log($"[RynthAi] OnTick: settings push entry — settings null? {pushSettings == null}, HasSetRadarSuppressed={Host.HasSetRadarSuppressed}");
            if (pushSettings != null)
            {
                if (diag)
                    Host.Log($"[RynthAi] OnTick: pushSettings.SuppressRetailRadar={pushSettings.SuppressRetailRadar}, SuppressRetailPowerbar={pushSettings.SuppressRetailPowerbar}");
                Host.SetFpsLimit(pushSettings.EnableFPSLimit, pushSettings.TargetFPSFocused, pushSettings.TargetFPSBackground);
                if (Host.HasSetRadarSuppressed)
                    Host.SetRadarSuppressed(pushSettings.SuppressRetailRadar);
                if (Host.HasSetPowerbarSuppressed)
                    Host.SetPowerbarSuppressed(pushSettings.SuppressRetailPowerbar);

                // Fellowship-follow: publish the leader's object id so the nav
                // engine can steer toward their live position. 0 = idle (not in a
                // fellowship, or we ARE the leader — a leader shouldn't follow itself).
                if (pushSettings.FollowMode && _fellowshipTracker != null)
                {
                    int leader = _fellowshipTracker.LeaderId;
                    pushSettings.FollowTargetId =
                        (leader != 0 && !_fellowshipTracker.IsLeader) ? unchecked((uint)leader) : 0u;
                }
                else
                {
                    pushSettings.FollowTargetId = 0;
                }
            }

            _objectCache?.Tick();
            if (diag) Host.Log("[RynthAi] OnTick: after cache tick");

            // Periodically flush the creature profile store (~ every 5 seconds at 60Hz).
            if (++_creatureSaveTickCounter >= 300)
            {
                _creatureSaveTickCounter = 0;
                _creatureStore?.SaveIfDirty();
                _damageStore?.SaveIfDirty();
            }

            // Deferred per-character settings load. OnLoginComplete tries
            // LoadSettings once, gated on Host.GetPlayerId()/TryGetObjectName
            // being readable at that instant. In Decal-coexistence / off-thread
            // pump mode the player object often isn't materialised that early,
            // so that one shot fails, _charFolder/_settingsFilePath stay empty,
            // and EVERY SaveSettings()/CheckAndSave() silently no-ops for the
            // whole session (incl. the Avalonia SettingsPanel write-back path).
            // Retry on the unconditional tick until the name resolves — the
            // player object always comes good once the bot is actually running.
            if (_loginComplete && _dashboard != null
                && string.IsNullOrEmpty(_dashboard.CharFolder)
                && ++_settingsLoadRetryCounter >= 30)
            {
                _settingsLoadRetryCounter = 0;
                uint pid = _playerId != 0 ? _playerId : Host.GetPlayerId();
                if (pid != 0 && Host.HasGetObjectName
                    && Host.TryGetObjectName(pid, out string lateName)
                    && !string.IsNullOrWhiteSpace(lateName))
                {
                    _dashboard.LoadSettings(lateName);
                    if (!string.IsNullOrEmpty(_dashboard.CharFolder))
                    {
                        _buffManager?.SetTimerPath(_dashboard.CharFolder);
                        _damageStore?.SetCharacter(_dashboard.CharFolder);
                        _patrolOnLoginPending = _dashboard.Settings.PatrolOnLogin;
                        Log($"RynthAi: per-character settings established late for '{lateName}' (early OnLoginComplete read had failed).");
                    }
                }
            }

            // Render-independent settings/profile autosave (~every 2s at 60Hz).
            // The dashboard's own dirty-check save runs inside Render(), which
            // never fires when the in-AC ImGui shell is disabled — drive it from
            // the unconditional tick instead. TickAutoSave self-throttles by
            // content hash, so this only writes when settings actually changed.
            if (++_settingsSaveTickCounter >= 120)
            {
                _settingsSaveTickCounter = 0;
                _dashboard?.TickAutoSave();
            }
            _questTracker?.Tick();
            if (diag) Host.Log("[RynthAi] OnTick: after quest tracker");
            DrainGiveQueue();
            if (diag) Host.Log("[RynthAi] OnTick: after drain give queue");
            _jumper?.Tick();
            if (diag) Host.Log("[RynthAi] OnTick: after jumper tick");

            // ── Affirmative in-world gate ────────────────────────────────
            // _loginComplete is cleared only by the engine's one-shot logout
            // hook (CPlayerSystem::ExecuteLogOff / RecvNotice_Logoff). That
            // hook does not fire for every way an in-world session ends — a
            // server disconnect, link death, or world-server bounce (routine
            // on ACE) drops the client to char-select without it, leaving the
            // bot ticking against torn-down world data. AC's native
            // GetPlayerId returns 0 whenever no player is in world, so a
            // sustained 0 is treated as logout and runs the same teardown the
            // hook would have. Debounced so a one-frame transient 0 (it does
            // not go 0 across portals) can never tear down a healthy session.
            if (_loginComplete && Host.HasGetPlayerId)
            {
                if (Host.GetPlayerId() == 0)
                {
                    if (_notInWorldSince == DateTime.MinValue)
                        _notInWorldSince = DateTime.UtcNow;
                    else if ((DateTime.UtcNow - _notInWorldSince).TotalMilliseconds >= 1500)
                    {
                        _notInWorldSince = DateTime.MinValue;
                        Log("RynthAi: player not in world (GetPlayerId=0 ≥1.5s) — treating as logout.");
                        OnLogout();
                    }
                }
                else
                {
                    _notInWorldSince = DateTime.MinValue;
                }
            }

            if (_loginComplete)
            {
                // try/finally so the Nav3D marker submit runs on every tick the
                // login is complete — even when the Buffing / missile-crafting /
                // BoostNavPriority branches `return` early. The engine clears
                // the Nav3D buffer at the start of each TickAll, so any tick we
                // skip the submit blanks the rings until the next clean tick.
                // That is the cause of markers vanishing while the macro runs.
                try
                {
                if (diag) Host.Log("[RynthAi] OnTick: entering loginComplete block");
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

                if (diag) Host.Log("[RynthAi] OnTick: before CheckBusyTimeout");
                CheckBusyTimeout();
                if (diag) Host.Log("[RynthAi] OnTick: before buffManager");
                _buffManager?.OnHeartbeat();
                if (diag) Host.Log("[RynthAi] OnTick: after buffManager");

                var settings = _dashboard?.Settings;
                if (settings == null)
                    return;
                if (diag) Host.Log($"[RynthAi] OnTick: settings ok, macro={settings.IsMacroRunning} action={settings.BotAction}");

                if (_patrolOnLoginPending && _raycast?.GeometryLoader?.CellDat?.IsLoaded == true)
                {
                    _patrolOnLoginPending = false;
                    HandleDungeonNavPatrol();
                }

                // Reroute the active dungeon patrol around any lava/acid hotspot sighted
                // since the route was built — treats the hotspot as a wall instead of
                // looping through it. Cheap no-op unless a new hazard cell was registered.
                TickDunPatrolHazardReroute();

                // ── Activity arbiter — STEP 2: AUTHORITATIVE for Combat↔Nav ──
                // The arbiter is now the SOLE writer of the "Combat" /
                // "Navigating" / "Default" BotAction strings (CombatManager's
                // own writes were removed in the same step). Buffing/Looting/
                // Salvaging strings are still written by their legacy managers
                // (migrated in steps 3-4). wantCombat uses the tight
                // HasEngageableTarget predicate — NOT HasTargets — so far-off
                // mobs no longer latch the action lock while nav is blocked.
                // See ACTIVITY_ARBITER_PLAN.md.
                try
                {
                    _arbiter ??= new ActivityArbiter(m => Host.Log($"[RynthAi] {m}"));
                    bool wantBuff = settings.EnableBuffing && _buffManager != null && _buffManager.NeedsAnyBuff();
                    bool wantCombat = settings.EnableCombat && _combatManager != null && _combatManager.HasEngageableTarget;
                    bool wantLoot = _openedContainerId != 0 || _targetCorpseId != 0;
                    bool wantSalvage = _salvageManager != null && _salvageManager.IsBusy;
                    bool routeLoaded = settings.CurrentRoute != null && settings.CurrentRoute.Points.Count > 0;
                    bool wantNav = settings.IsMacroRunning && settings.EnableNavigation && routeLoaded;
                    var inputs = new ArbiterInputs(
                        settings.IsMacroRunning, wantBuff, wantCombat, wantLoot, wantSalvage, wantNav);
                    _arbiter.ApplyStep2(in inputs, settings);
                }
                catch { /* arbiter must never throw out of the live tick */ }

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
                    // Buffing-coma watchdog. Buffing stays TOP priority — but on
                    // 2026-06-12 a stance deadlock made NeedsAnyBuff() hold
                    // Buffing for 1h54m while BuffManager silently retried mode
                    // flips: the bot stood unbuffed and dormant, and the ONE
                    // component with stance recovery (CombatManager) never got a
                    // tick. A normal full rebuff cycle is 1-2 min; >5 min of
                    // CONTINUOUS Buffing is pathological regardless of cause.
                    // Past that, warn once and let one tick per ~10s fall
                    // through to the combat heartbeat so its stance recovery can
                    // run. Buffing reclaims priority on the very next tick.
                    if (_buffingHeldSince == DateTime.MinValue)
                        _buffingHeldSince = DateTime.Now;
                    double heldMs = (DateTime.Now - _buffingHeldSince).TotalMilliseconds;
                    bool comaBypass = heldMs > BuffComaThresholdMs
                        && (DateTime.Now - _lastBuffComaBypassAt).TotalMilliseconds > BuffComaBypassEveryMs;
                    if (comaBypass)
                    {
                        _lastBuffComaBypassAt = DateTime.Now;
                        if (!_buffComaWarned)
                        {
                            _buffComaWarned = true;
                            Host.Log($"[RynthAi] BUFFING COMA: BotAction has been 'Buffing' continuously for {heldMs / 60000:0.0} min with buffs still needed — letting combat/stance recovery tick through every ~10s. Check for a stance wedge.");
                            Host.WriteToChat($"[RynthAi] Buffing has been stuck for {heldMs / 60000:0} min (stance wedge?) — engaging recovery. /ra clearbusy or relog if it persists.", 2);
                        }
                        // fall through — combat heartbeat below gets one shot
                    }
                    else
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
                }
                else
                {
                    _buffingPausedNav = false;
                    _buffingHeldSince = DateTime.MinValue;
                    _buffComaWarned = false;
                }

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

                if (diag) Host.Log("[RynthAi] OnTick: before salvageManager");

                // Combat preempts salvage when a mob is engageable. The old
                // design deliberately let salvage hold BotAction over Combat
                // (finish the queue), but that strands the bot doing inventory
                // combine-sweeps next to a mob attacking it: the legacy
                // 'Salvaging' string blocks CombatManager.canRun and the
                // arbiter doesn't override it (Salvaging isn't arbiter-
                // authoritative yet). HasEngageableTarget = actively engaged OR
                // a scanned mob within MonsterRange — the same predicate the
                // arbiter uses for Combat-vs-Nav (pure, no side effects;
                // reflects last tick's scan, which is fine to yield on).
                bool combatThreat = _combatManager?.HasEngageableTarget == true;

                // Same settle gate as InventoryManager: BeginCombineSalvage walks
                // GetDirectInventory which races with cache classification during
                // the post-login / hot-reload CreateObject burst. Also pause the
                // combine work while a threat is up so it doesn't contend with
                // combat over SelectItem; it resumes the moment the threat clears.
                if (inventorySettled && !combatThreat)
                    _salvageManager?.OnTick(_busyCount);
                if (diag) Host.Log("[RynthAi] OnTick: before manaStoneManager");

                // Salvage-priority gap-fill: hold BotAction at "Salvaging" while
                // a container is open OR the salvage queue has items — UNLESS a
                // mob is engageable, in which case combat must win (don't stand
                // there getting hit). "Buffing" still wins (buffs = survival).
                if (settings.IsMacroRunning
                    && settings.BotAction != "Buffing"
                    && !combatThreat
                    && (_openedContainerId != 0 || _salvageManager?.IsBusy == true))
                {
                    settings.BotAction = "Salvaging";
                }
                else if (settings.BotAction == "Salvaging"
                         && (combatThreat
                             || (_openedContainerId == 0 && _salvageManager?.IsBusy != true)))
                {
                    // Release the legacy 'Salvaging' lock so CombatManager.canRun
                    // is true and the arbiter can take Combat. Salvage re-grabs
                    // it next tick once the threat is gone.
                    settings.BotAction = "Default";
                }

                // Mana stone tapping — runs after salvage, independent of looting state.
                _manaStoneManager?.OnHeartbeat(_busyCount);
                _petManager?.OnHeartbeat(_busyCount);
                if (diag) Host.Log("[RynthAi] OnTick: before combatManager");

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

                // BoostNavPriority must only suppress combat/loot when there is
                // actually navigation to prioritise. With nav disabled or no
                // route points there is nothing to boost, so an unconditional
                // return here just starves combat and the bot stands among mobs
                // without hunting. Same "nav active" predicate the arbiter uses
                // for wantNav (~line 404): IsMacroRunning && EnableNavigation &&
                // route-has-points.
                bool navActiveForBoost = settings.IsMacroRunning
                                      && settings.EnableNavigation
                                      && settings.CurrentRoute != null
                                      && settings.CurrentRoute.Points.Count > 0;
                if (settings.BoostNavPriority && navActiveForBoost)
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
                    if (diag) Host.Log("[RynthAi] OnTick: after combatManager.OnHeartbeat");
                    TickCorpseOpening();
                }
                if (diag) Host.Log("[RynthAi] OnTick: before nav");
                // Mirror the arbiter's wantCombat predicate: only pause nav when
                // combat can actually engage. LOS-blocked mobs would freeze the bot
                // (arbiter picks Nav, but combatBlocking paused it) — instead let
                // nav close distance until HasEngageableTarget flips true.
                bool combatBlocking = settings.EnableCombat
                                   && _combatManager != null
                                   && _combatManager.HasEngageableTarget;
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
                finally
                {
                    // Submit nav-marker 3D geometry here (not in OnRender) so it
                    // keeps working when EnableImGuiShell=false. OnRender is gated
                    // by PluginManager.RenderAll, which the engine skips when the
                    // ImGui shell is off; OnTick runs unconditionally. The engine
                    // clears the Nav3D buffer at the start of each TickAll, so we
                    // just submit our geometry on top of whatever other plugins
                    // have already added this frame. The try/finally above ensures
                    // this fires even when an early return short-circuits the body
                    // (Buffing / missile-crafting / BoostNavPriority branches).
                    if (Host.HasNav3D)
                        _navMarkerRenderer?.SubmitNav3D();
                }
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
        if (_dashboard is not null)
        {
            _dashboard.Settings.DashboardVisible = _windowVisible;
            _dashboard.SaveSettings();
        }
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
        // BuffManager and MissileCraftingManager read CurrentCombatMode live from AC,
        // so no push is needed — they always see the truth, hot-reload-safe and event-loss-safe.
    }

    public override void OnChatWindowText(string? text, int chatType, ref int eat)
    {
        if (string.IsNullOrEmpty(text)) return;
        _dashboard?.PushChatLine(text, chatType);
        _buffManager?.OnChatWindowText(text, chatType);
        _manaStoneManager?.OnChatWindowText(text);
        _petManager?.OnChatWindowText(text);
        _combatManager?.HandleChatForDebuffs(text);
        _combatManager?.HandleChatForDamage(text);
        _missileCraftingManager?.HandleChat(text);
        _metaManager?.HandleChat(text);
        _questTracker?.OnChatLine(text);
    }

    // ACE sends GameEventKillerNotification (0x01AD) to the killer at the
    // lethal hit — earlier than the health=0 / corpse signals combat otherwise
    // waits on. The engine parses the death string out of the packet and hands
    // it here so combat can drop the dead target before burning another cast.
    public override void OnKillNotification(string? deathMessage)
    {
        if (string.IsNullOrEmpty(deathMessage)) return;
        _combatManager?.OnKillNotification(deathMessage);
        _dashboard?.RecordKill();   // feeds the kills/hour session stat
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

        // Capture observed creature data into the persistent store. maxHealth>0 means
        // we just got a successful CreatureProfile (Assess succeeded) — the only time
        // we have authoritative max vitals + resists.
        if (targetId != _playerId && maxHealth > 0)
            CaptureCreatureSample(targetId, maxHealth);
    }

    // Exact per-hit damage from the engine (AttackerNotification 0x01B1). This is
    // the only selection-free source of real damage numbers — combat uses it to
    // learn per-monster damage and predict kill shots. isAttacker==true = our hit.
    public override void OnCombatDamage(uint damage, uint damageType, bool crit, bool isAttacker)
    {
        if (isAttacker)
            _combatManager?.OnCombatDamage((double)damage, damageType, crit, isAttacker);
    }

    /// <summary>Human-readable table of the learned per-monster casts-to-kill data
    /// (joined with creatures.json for names + appraised HP). Polled live by the
    /// engine-side MonsterDamagePanel via RynthPluginGetMonsterDamageText.</summary>
    public string BuildMonsterDamageText()
    {
        var store = _damageStore;
        if (store == null) return "Monster-damage learning not ready.";
        var rows = store.Snapshot();
        if (rows.Count == 0)
            return "No kills recorded yet.\n\nFight monsters with magic and this fills in:\n"
                 + "one row per monster type + spell, with the average\n"
                 + "number of casts it takes to kill them.";

        var sb = new System.Text.StringBuilder();
        sb.Append("Monster                      Elem   Tier  Casts/Kill  Kills    HP*\n");
        sb.Append("-----------------------------------------------------------------\n");
        foreach (var r in rows.OrderByDescending(x => x.KillSamples)
                               .ThenBy(x => x.Element, StringComparer.OrdinalIgnoreCase))
        {
            string name = "wcid " + r.Wcid.ToString();
            double hp = 0;
            if (_creatureStore != null && _creatureStore.TryGetByWcid(r.Wcid, out var prof) && prof != null)
            {
                if (!string.IsNullOrEmpty(prof.Name)) name = prof.Name;
                hp = prof.MaxHealth;
            }
            if (name.Length > 28) name = name.Substring(0, 28);
            string hpStr = hp > 0 ? hp.ToString("0") : "?";
            sb.Append(name.PadRight(28)).Append(' ')
              .Append((r.Element ?? "").PadRight(6)).Append(' ')
              .Append(r.Tier.ToString().PadLeft(3)).Append("   ")
              .Append(r.AvgCastsToKill.ToString("0.00").PadLeft(8)).Append("  ")
              .Append(r.KillSamples.ToString().PadLeft(5)).Append("  ")
              .Append(hpStr.PadLeft(5)).Append('\n');
        }
        sb.Append("\n* HP = appraised; skill-gated on ACE, often reads low.\n");
        sb.Append("  Casts/Kill is measured from real kills — that's the reliable number.\n");
        return sb.ToString();
    }

    /// <summary>Structured per-monster learning for the interactive Damage panel.
    /// One JSON object per (monster, weapon, spell) row. Polled live by the engine
    /// MonsterDamagePanel via RynthPluginGetMonsterDamageJson.</summary>
    public string BuildMonsterDamageJson()
    {
        var store = _damageStore;
        if (store == null) return "[]";
        var rows = store.Snapshot();

        // Per-wcid weapon recommendation is identical for every row of a monster — memoize it.
        var bestCache = new Dictionary<uint, uint>();
        uint BestFor(uint w)
        {
            if (!bestCache.TryGetValue(w, out uint b)) { b = store.GetBestWeapon(w); bestCache[w] = b; }
            return b;
        }
        string NameOf(uint id, string prefix)
        {
            if (id == 0) return "";
            string n = "";
            if (Host.HasGetObjectName) { try { Host.TryGetObjectName(id, out n); } catch { n = ""; } }
            return string.IsNullOrEmpty(n) ? prefix + " " + id : n;
        }

        // Group the per-(weapon,element,tier) stat rows by MONSTER (wcid). The Damage tab now
        // shows ONE collapsed row per monster (latest tier used + total kills); the per-tier
        // breakdown rides along in a nested "tiers" array for the expand drawer.
        var byWcid = new Dictionary<uint, List<CreatureData.MonsterDamageStore.DamageRow>>();
        var order = new List<uint>();
        foreach (var r in rows)
        {
            if (!byWcid.TryGetValue(r.Wcid, out var glist)) { glist = new(); byWcid[r.Wcid] = glist; order.Add(r.Wcid); }
            glist.Add(r);
        }

        string ResolveName(uint wcid, string rowName)
        {
            string name = rowName;
            if (string.IsNullOrEmpty(name) && _creatureStore != null
                && _creatureStore.TryGetByWcid(wcid, out var prof) && prof != null && !string.IsNullOrEmpty(prof.Name))
                name = prof.Name;
            return string.IsNullOrEmpty(name) ? "wcid " + wcid : name;
        }
        double DbHp(uint wcid) =>
            (_creatureStore != null && _creatureStore.TryGetByWcid(wcid, out var p) && p != null) ? p.MaxHealth : 0;

        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        bool first = true;
        var emittedWcids = new HashSet<uint>();

        // The DEFAULT line (top row). Carries the per-character default weapon; the panel renders
        // it specially (weapon picker + chevron → the Default debuff/shape rule). isDefault=true.
        uint defWeapon = store.GetDefaultWeapon();
        sb.Append('{')
          .Append("\"wcid\":0,\"isDefault\":true,\"name\":\"Default\",")
          .Append("\"wid\":").Append(defWeapon).Append(',')
          .Append("\"weapon\":").Append(JsonStr(NameOf(defWeapon, "Weapon"))).Append(',')
          .Append("\"elem\":\"\",\"tier\":0,\"hp\":0,\"hpManual\":false,")
          .Append("\"crit\":0,\"critN\":0,\"noncrit\":0,\"noncritN\":0,\"casts\":0,\"kills\":0,")
          .Append("\"assignedWid\":").Append(defWeapon).Append(',')
          .Append("\"assignedWeapon\":").Append(JsonStr(NameOf(defWeapon, "Weapon"))).Append(',')
          .Append("\"bestWid\":0,\"bestWeapon\":\"\",\"assignedOff\":0,\"assignedOffName\":\"\",")
          .Append("\"key\":\"__default__\",\"tiers\":[]")
          .Append('}');
        first = false;

        // STABLE order by monster name (then wcid) so rows don't reshuffle as counters tick.
        foreach (uint wcid in order.OrderBy(w => ResolveName(w, byWcid[w].Count > 0 ? byWcid[w][0].Name : ""),
                                            StringComparer.OrdinalIgnoreCase).ThenBy(w => w))
        {
            var glist = byWcid[wcid];
            emittedWcids.Add(wcid);
            string name = ResolveName(wcid, glist.Count > 0 ? glist[0].Name : "");
            double dbHp = DbHp(wcid);

            double manual = store.GetManualHp(wcid);
            bool hpManual = manual > 0;
            double poolHp = 0; foreach (var x in glist) if (x.HpPool > poolHp) poolHp = x.HpPool;
            double hp = hpManual ? manual : (dbHp > 0 ? dbHp : poolHp);

            int latestTier = store.GetLastTier(wcid);          // negative = ring
            int totalKills = 0; foreach (var x in glist) totalKills += x.KillSamples;

            // Representative entry for the collapsed row's crit/casts = the latest-tier one,
            // else the most-killed one.
            CreatureData.MonsterDamageStore.DamageRow m = default; bool haveM = false;
            foreach (var x in glist) if (x.Tier == latestTier) { m = x; haveM = true; break; }
            if (!haveM) { int bk = -1; foreach (var x in glist) if (x.KillSamples > bk) { bk = x.KillSamples; m = x; haveM = true; } }

            uint assignedWid = store.GetManualWeapon(wcid);
            uint bestWid     = BestFor(wcid);
            uint assignedOff = store.GetManualOffhand(wcid);

            if (!first) sb.Append(',');
            first = false;
            sb.Append('{')
              .Append("\"wcid\":").Append(wcid).Append(',')
              .Append("\"name\":").Append(JsonStr(name)).Append(',')
              .Append("\"wid\":").Append(haveM ? m.WeaponId : 0).Append(',')
              .Append("\"weapon\":").Append(JsonStr(haveM ? NameOf(m.WeaponId, "Weapon") : "")).Append(',')
              .Append("\"elem\":").Append(JsonStr(haveM ? (m.Element ?? "") : "")).Append(',')
              .Append("\"tier\":").Append(latestTier).Append(',')
              .Append("\"hp\":").Append((int)Math.Round(hp)).Append(',')
              .Append("\"hpManual\":").Append(hpManual ? "true" : "false").Append(',')
              .Append("\"crit\":").Append(JsonNum(haveM ? m.AvgCritDamage : 0)).Append(',')
              .Append("\"critN\":").Append(haveM ? m.CritSamples : 0).Append(',')
              .Append("\"noncrit\":").Append(JsonNum(haveM ? m.AvgNonCritDamage : 0)).Append(',')
              .Append("\"noncritN\":").Append(haveM ? m.NonCritSamples : 0).Append(',')
              .Append("\"casts\":").Append(JsonNum(haveM ? m.AvgCastsToKill : 0)).Append(',')
              .Append("\"kills\":").Append(totalKills).Append(',')
              .Append("\"assignedWid\":").Append(assignedWid).Append(',')
              .Append("\"assignedWeapon\":").Append(JsonStr(NameOf(assignedWid, "Weapon"))).Append(',')
              .Append("\"bestWid\":").Append(bestWid).Append(',')
              .Append("\"bestWeapon\":").Append(JsonStr(NameOf(bestWid, "Weapon"))).Append(',')
              .Append("\"assignedOff\":").Append(assignedOff).Append(',')
              .Append("\"assignedOffName\":").Append(JsonStr(NameOf(assignedOff, "Offhand"))).Append(',')
              .Append("\"key\":").Append(JsonStr(wcid.ToString())).Append(',')
              .Append("\"tiers\":[");
            bool tf = true;
            foreach (var x in glist.OrderByDescending(z => z.KillSamples).ThenByDescending(z => z.Tier))
            {
                if (!tf) sb.Append(',');
                tf = false;
                sb.Append('{')
                  .Append("\"tier\":").Append(x.Tier).Append(',')
                  .Append("\"elem\":").Append(JsonStr(x.Element ?? "")).Append(',')
                  .Append("\"weapon\":").Append(JsonStr(NameOf(x.WeaponId, "Weapon"))).Append(',')
                  .Append("\"crit\":").Append(JsonNum(x.AvgCritDamage)).Append(',')
                  .Append("\"critN\":").Append(x.CritSamples).Append(',')
                  .Append("\"noncrit\":").Append(JsonNum(x.AvgNonCritDamage)).Append(',')
                  .Append("\"noncritN\":").Append(x.NonCritSamples).Append(',')
                  .Append("\"casts\":").Append(JsonNum(x.AvgCastsToKill)).Append(',')
                  .Append("\"kills\":").Append(x.KillSamples)
                  .Append('}');
            }
            sb.Append("]}");
        }

        // Bare rows for monsters appraised this session but not yet fought (one per wcid), so the
        // table populates as you ID nearby mobs. HP from creatures.json (appraised); empty tiers.
        uint[] seen;
        lock (_seenMonstersThisSession) seen = System.Linq.Enumerable.ToArray(_seenMonstersThisSession);
        foreach (uint wcid in seen)
        {
            if (emittedWcids.Contains(wcid)) continue;
            emittedWcids.Add(wcid);

            string name = ResolveName(wcid, "");
            double dbHp = DbHp(wcid);
            double manual = store.GetManualHp(wcid);
            bool hpManual = manual > 0;
            double hp = hpManual ? manual : dbHp;
            uint assignedWid = store.GetManualWeapon(wcid);
            uint bestWid     = BestFor(wcid);
            uint assignedOff = store.GetManualOffhand(wcid);
            int latestTier = store.GetLastTier(wcid);

            if (!first) sb.Append(',');
            first = false;
            sb.Append('{')
              .Append("\"wcid\":").Append(wcid).Append(',')
              .Append("\"name\":").Append(JsonStr(name)).Append(',')
              .Append("\"wid\":0,\"weapon\":\"\",\"elem\":\"\",")
              .Append("\"tier\":").Append(latestTier).Append(',')
              .Append("\"hp\":").Append((int)Math.Round(hp)).Append(',')
              .Append("\"hpManual\":").Append(hpManual ? "true" : "false").Append(',')
              .Append("\"crit\":0,\"critN\":0,\"noncrit\":0,\"noncritN\":0,\"casts\":0,\"kills\":0,")
              .Append("\"assignedWid\":").Append(assignedWid).Append(',')
              .Append("\"assignedWeapon\":").Append(JsonStr(NameOf(assignedWid, "Weapon"))).Append(',')
              .Append("\"bestWid\":").Append(bestWid).Append(',')
              .Append("\"bestWeapon\":").Append(JsonStr(NameOf(bestWid, "Weapon"))).Append(',')
              .Append("\"assignedOff\":").Append(assignedOff).Append(',')
              .Append("\"assignedOffName\":").Append(JsonStr(NameOf(assignedOff, "Offhand"))).Append(',')
              .Append("\"key\":").Append(JsonStr(wcid.ToString())).Append(',')
              .Append("\"tiers\":[]")
              .Append('}');
        }

        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>Set (or clear, hp&lt;=0) the manual HP override for a wcid, then persist.</summary>
    public void SetMonsterHp(uint wcid, int hp)
    {
        if (_damageStore == null) return;
        _damageStore.SetManualHp(wcid, hp);
        _damageStore.SaveIfDirty();
    }

    /// <summary>Set (or clear, wid==0) the per-monster weapon override from the Damage panel, then persist.</summary>
    public void SetMonsterWeapon(uint wcid, uint weaponId)
    {
        if (_damageStore == null) return;
        _damageStore.SetManualWeapon(wcid, weaponId);
        _damageStore.SaveIfDirty();
    }

    /// <summary>Set (or clear, wid==0) the per-character DEFAULT weapon — the sweeping fallback every
    /// monster without its own weapon override uses. From the Damage panel's Default line. Persists.</summary>
    public void SetDefaultWeapon(uint weaponId)
    {
        if (_damageStore == null) return;
        _damageStore.SetDefaultWeapon(weaponId);
        _damageStore.SaveIfDirty();
    }

    /// <summary>Set (or clear, off==0) the per-monster offhand override from the Damage panel, then persist.</summary>
    public void SetMonsterOffhand(uint wcid, uint offhandId)
    {
        if (_damageStore == null) return;
        _damageStore.SetManualOffhand(wcid, offhandId);
        _damageStore.SaveIfDirty();
    }

    /// <summary>Master reset from the Damage panel: zero all learned stats, keep names + manual overrides. Persists.</summary>
    public void ClearMonsterStats()
    {
        if (_damageStore == null) return;
        _damageStore.ClearAllStats();
        _damageStore.SaveIfDirty();
    }

    /// <summary>Selectable weapons for the Damage-panel weapon/offhand pickers, as a JSON array
    /// [{"id":..,"name":..}], from the same configured-weapons (ItemRules) source the Monsters tab uses.</summary>
    public string BuildCombatWeaponsJson() => DashboardRenderer?.BuildCombatWeaponsJson() ?? "[]";

    /// <summary>Delete one learned row. key = "wcid:weaponId:element:tier" (from the JSON). Persists.</summary>
    public bool DeleteMonsterRow(string key)
    {
        if (_damageStore == null || string.IsNullOrEmpty(key)) return false;
        string[] p = key.Split(':');
        if (p.Length < 4) return false;
        if (!uint.TryParse(p[0], out uint wcid)) return false;
        if (!uint.TryParse(p[1], out uint wid)) return false;
        if (!int.TryParse(p[3], out int tier)) return false;
        bool ok = _damageStore.DeleteRow(wcid, wid, p[2], tier);
        _damageStore.SaveIfDirty();
        return ok;
    }

    private static string JsonNum(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string JsonStr(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "\"\"";
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }


    // Stable: PropertyInt indices used by the AC client appraisal table.
    private const uint PROP_INT_CREATURE_TYPE = 2;
    private const uint PROP_INT_ARMOR_LEVEL   = 28;

    // PropertyFloat indices for elemental resistance multipliers (1.0 = neutral).
    private const uint PROP_FLOAT_RESIST_SLASH    = 168;
    private const uint PROP_FLOAT_RESIST_PIERCE   = 169;
    private const uint PROP_FLOAT_RESIST_BLUDGEON = 170;
    private const uint PROP_FLOAT_RESIST_FIRE     = 171;
    private const uint PROP_FLOAT_RESIST_COLD     = 172;
    private const uint PROP_FLOAT_RESIST_ACID     = 173;
    private const uint PROP_FLOAT_RESIST_ELECTRIC = 174;

    private void CaptureCreatureSample(uint targetId, uint maxHealth)
    {
        if (_creatureStore == null) return;
        try
        {
            string name = string.Empty;
            if (Host.HasGetObjectName) Host.TryGetObjectName(targetId, out name);
            if (string.IsNullOrEmpty(name)) return;

            uint wcid = 0;
            if (Host.HasGetObjectWcid) Host.TryGetObjectWcid(targetId, out wcid);

            var sample = new CreatureData.CreatureProfile
            {
                Name = name,
                Wcid = wcid,
                MaxHealth = maxHealth,
            };

            if (Host.TryGetTargetVitals(targetId, out _, out _, out _, out uint maxStam, out _, out uint maxMana))
            {
                sample.MaxStamina = maxStam;
                sample.MaxMana = maxMana;
            }

            if (Host.TryGetObjectIntProperty(targetId, PROP_INT_CREATURE_TYPE, out int ctype))
                sample.CreatureType = ctype;
            if (Host.TryGetObjectIntProperty(targetId, PROP_INT_ARMOR_LEVEL, out int armor))
                sample.ArmorLevel = armor;

            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_SLASH, out double rs))    sample.ResistSlash = rs;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_PIERCE, out double rp))   sample.ResistPierce = rp;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_BLUDGEON, out double rb)) sample.ResistBludgeon = rb;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_FIRE, out double rf))     sample.ResistFire = rf;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_COLD, out double rc))     sample.ResistCold = rc;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_ACID, out double ra))     sample.ResistAcid = ra;
            if (Host.TryGetObjectDoubleProperty(targetId, PROP_FLOAT_RESIST_ELECTRIC, out double re)) sample.ResistElectric = re;

            if (Host.HasGetObjectSpellIds)
            {
                var buf = new uint[64];
                int n = Host.GetObjectSpellIds(targetId, buf, buf.Length);
                for (int i = 0; i < n && i < buf.Length; i++)
                {
                    if (buf[i] != 0) sample.KnownSpellIds.Add(buf[i]);
                }
            }

            _creatureStore.Upsert(sample);

            // Remember this monster type so the Damage table shows it (as a bare row with
            // its appraised HP) even before we've fought it — populated as nearby mobs are ID'd.
            if (wcid != 0)
                lock (_seenMonstersThisSession) _seenMonstersThisSession.Add(wcid);
        }
        catch (Exception ex)
        {
            Host.Log($"[RynthAi] CaptureCreatureSample exception: {ex.Message}");
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

        // VirindiTank /vt command compatibility — option toggles (combat/doors/
        // looting/navboost), meta/nav/loot load, setmetastate. Translated
        // natively to RynthAi settings; no VTank/Decal required. Previously only
        // reachable from meta execution, so /vt chat WAYPOINTS silently no-op'd.
        if (trimmed.StartsWith("/vt ", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("/vt", StringComparison.OrdinalIgnoreCase))
        {
            eat = 1;
            if (!(_metaManager?.TryHandleVtCommand(trimmed) ?? false))
                ChatLine("[RynthAi] Unrecognized /vt command.");
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
                else if (parts.Length >= 3 && parts[2].Equals("tiers", StringComparison.OrdinalIgnoreCase))
                    _buffManager.PrintBuffTierDebug();
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
            case "follow":       HandleFollowCommand(parts); break;
            case "dunnav":        HandleDungeonNavCommand(parts); break;
            case "dunnav-patrol": HandleDungeonNavPatrolCommand(parts); break;
            case "hazard":        HandleHazardCommand(parts); break;
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
            case "panic":        HandlePanicCommand(); break;
            case "forcebuff":        HandleForceBuff(); break;
            case "cancelforcebuff":  HandleCancelForceBuff(); break;
            case "bufftest":
                if (_buffManager == null) { ChatLine("[RynthAi] BuffManager not ready (not logged in yet)."); break; }
                _buffManager.EnableCastRegistryDiagnostic = !_buffManager.EnableCastRegistryDiagnostic;
                if (_buffManager.EnableCastRegistryDiagnostic)
                {
                    ChatLine("[RynthAi] BuffTest ON — fires on next item-spell cast.");
                    ChatLine("[RynthAi]   Cast an Impen/Bane (via macro or manually). Diff logs to RynthCore.log with [BuffTest] tag.");
                    ChatLine("[RynthAi]   Auto-disables after one cast/diff.");
                }
                else
                {
                    ChatLine("[RynthAi] BuffTest OFF.");
                }
                break;
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

        // Settings push moved to OnTick — see note there. OnRender only runs
        // when ImGui is up, but the suppression toggles must work regardless.

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            // Nav3D: in 3D mode, geometry is submitted from OnTick (so it
            // survives EnableImGuiShell=false). The clear+submit for nav
            // markers happens there. Here we only need to handle the ImGui
            // fallback (engines without HasNav3D) and any submitters that
            // still live in OnRender.
            _navMarkerRenderer?.RenderImGuiFallback();
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
                    _dashboard.Settings.DashboardVisible = false;
                    _dashboard.CloseRequested = false;
                    _dashboard.SaveSettings();
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

using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.Combat;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi;

public class CombatManager : IDisposable
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private SpellManager? _spellManager;

    private MainLogic? _raycastSystem;
    public bool RaycastInitialized { get; private set; }

    // Set on the raycast-init bg thread when RaycastInitialized flips
    // false→true; consumed once on the next main-thread ScanNearbyTargets to
    // clear warmup-era blacklist/timer penalties (the bg thread must not touch
    // combat state directly).
    private volatile bool _raycastReadyResetPending;

    public int activeTargetId
    {
        get => _activeTargetId;
        set { _activeTargetId = value; if (value == 0) _nativeAttackTargetId = 0; }
    }
    private int _activeTargetId;
    private DateTime lastAttackCmd = DateTime.MinValue;
    private DateTime lastStanceAttempt = DateTime.MinValue;
    private DateTime _lastPeaceAttempt = DateTime.MinValue;

    // ── Face-before-attack state ─────────────────────────────────────────
    // For ranged/magic attacks, face the target with smooth turn before firing.
    // ── Native attack state ──────────────────────────────────────
    // StartAttackRequest auto-repeats — only call once per target.
    private uint _nativeAttackTargetId;

    private bool _facingTarget;
    private DateTime _faceStartTime = DateTime.MinValue;
    private const double FACE_TIMEOUT_MS = 1000.0; // give up waiting and fire anyway
    private const double FACE_TOLERANCE_DEG = 15.0; // heading error threshold to fire

    // Smooth turn motions — same codes as NavigationEngine
    private const uint MotionTurnRight = 0x6500000D;
    private const uint MotionTurnLeft  = 0x6500000E;

    // Grace period: keep activeTargetId alive for this long after it disappears from scan,
    // so a single bad LOS result or scan gap doesn't hand control to navigation.
    private DateTime _targetLostScanTime = DateTime.MinValue;
    private const double TARGET_SCAN_GRACE_MS = 1500.0;

    // Target lock — once committed to a mob, hold it until confirmed dead/gone.
    // Prevents spinning caused by target thrashing when world-filter has a transient null
    // or when a second mob briefly becomes slightly closer between scan ticks.
    private int _lockedTargetId = 0;

    private bool _wasMacroRunning;

    private DateTime _lastSpellCast = DateTime.MinValue;
    private bool _lastCastWasRing = false;
    private const double ATTACK_SPELL_COOLDOWN_MS = 100.0;

    private bool _returnToPhysicalCombat = false;
    private int _savedWeaponId = 0;

    /// <summary>Current combat mode — read live from AC client each access to avoid event-drop drift.</summary>
    public int CurrentCombatMode =>
        _host.HasGetCurrentCombatMode ? _host.GetCurrentCombatMode() : CombatMode.NonCombat;

    /// <summary>Client busy count — set by plugin from OnBusyCountIncremented/Decremented.
    /// When > 0, combat must not send any game actions (SelectItem, attack, cast).</summary>
    public int BusyCount { get; set; }

    private CharacterSkills? _charSkills;

    private readonly WorldObjectCache _worldFilter;

    private static readonly Dictionary<string, string[]> VulnSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Fire Vulnerability Other" } },
        { "Cold",      new[] { "Cold Vulnerability Other" } },
        { "Lightning", new[] { "Lightning Vulnerability Other" } },
        { "Acid",      new[] { "Acid Vulnerability Other" } },
        { "Blade",     new[] { "Blade Vulnerability Other" } },
        { "Slash",     new[] { "Blade Vulnerability Other" } },
        { "Pierce",    new[] { "Piercing Vulnerability Other" } },
        { "Bludgeon",  new[] { "Bludgeoning Vulnerability Other" } },
    };

    private readonly BlacklistManager _blacklistManager = new();
    private int _lastAttackedTargetId = 0;
    private int _consecutiveMisses = 0;
    private DateTime _targetLockedAt     = DateTime.MinValue;
    private DateTime _lastDamageDealtAt  = DateTime.MinValue;

    public int RaycastBlockCount { get; private set; }
    public int RaycastCheckCount { get; private set; }

    // ── Monster scanner ──────────────────────────────────────────────────
    // Pre-scans nearby creatures for distance, IsAttackable, and LOS.
    // Combat picks from this list — no SelectItem needed until attack time.
    private readonly List<ScannedTarget> _scannedTargets = new();
    private DateTime _lastScanTime = DateTime.MinValue;
    private const int SCAN_INTERVAL_MS = 50;

    // Utility-AI target switching: every candidate is scored each tick.
    // The currently-locked target gets this bonus so we don't flap on near-ties —
    // an alternative must beat (locked_score + STICKINESS) to take over.
    private const double TARGET_SWITCH_STICKINESS = 25.0;

    public struct ScannedTarget
    {
        public int Id;
        public double Distance;
        public double Angle;   // absolute facing error to target, degrees (0 = directly ahead)
        public string Name;
    }

    /// <summary>List of nearby, attackable, LOS-clear creatures. Re-scanned every tick (≤50 ms).
    /// Sorted by distance — selection itself is score-based via HandleCombatTrigger.</summary>
    public IReadOnlyList<ScannedTarget> ScannedTargets => _scannedTargets;

    /// <summary>True when combat has an active target or viable targets nearby. Used to block navigation.</summary>
    public bool HasTargets => activeTargetId != 0 || _scannedTargets.Count > 0;

    // Monsters that passed range/attackable checks but were LOS-blocked by walls.
    // Non-zero means a monster is nearby but not yet visible — nav should stop.
    private int _nearbyNoLos;

    /// <summary>True when any monster is in range, regardless of LOS. Use this for nav-blocking so
    /// the bot stops before walking through a portal into a room with monsters.</summary>
    public bool HasNearbyMonsters => HasTargets || _nearbyNoLos > 0;

    /// <summary>True if any scanned target is within <paramref name="yd"/> yards. Used by loot/salvage gates to decide when to yield to combat.</summary>
    public bool HasCloseThreat(double yd)
    {
        for (int i = 0; i < _scannedTargets.Count; i++)
            if (_scannedTargets[i].Distance <= yd) return true;
        return false;
    }

    /// <summary>True only when actively attacking a specific target. Unlike HasTargets, this is false between kills even when more monsters are scanned nearby.</summary>
    public bool IsActivelyEngaged => activeTargetId != 0;

    /// <summary>
    /// Pure predicate for the ActivityArbiter: should Combat claim this tick?
    /// True iff we're already locked on a target, OR a scanned target (the
    /// scan is already filtered to attackable + LOS-clear) is within actual
    /// engage range (MonsterRange — the same distance Think() uses for its
    /// attack gate). Deliberately NOT HasTargets: HasTargets is true for any
    /// scanned creature including unreachable/out-of-range ones, which made
    /// Combat squat on the action lock without ever attacking while nav was
    /// blocked — the "stands there surrounded by far-off mobs" freeze. With
    /// this predicate, far mobs → Combat doesn't claim → nav runs and closes
    /// distance → once within MonsterRange this flips true → Combat takes over.
    /// No side effects; safe to call from the arbiter's pure decision path.
    /// </summary>
    public bool HasEngageableTarget =>
        IsActivelyEngaged || HasCloseThreat(System.Math.Max(1, _settings.MonsterRange));

    /// <summary>Diagnostic snapshot for /ra combat — exposes the internal state machine fields.</summary>
    public CombatStateSnapshot GetStateSnapshot()
    {
        // Pick the same weapon EquipWeaponAndSetStance would pick for the active target,
        // so the dump shows which weapon combat is trying to swing/cast with.
        WorldObject? targetObj = activeTargetId != 0 ? _worldFilter[activeTargetId] : null;
        int    pickedWeaponId    = 0;
        string pickedWeaponName  = "";
        int    pickedWeaponMode  = 0;
        int    weaponWieldLoc    = -1;
        if (targetObj != null)
        {
            var rule = _settings.MonsterRules.FirstOrDefault(
                r => targetObj.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? _settings.MonsterRules.FirstOrDefault(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));

            if (rule != null && rule.WeaponId != 0)
                pickedWeaponId = rule.WeaponId;
            else
            {
                var bestWeapon = _settings.ItemRules.FirstOrDefault();
                if (bestWeapon != null) pickedWeaponId = bestWeapon.Id;
            }
            if (pickedWeaponId == 0) pickedWeaponId = FindWandInItems();

            if (pickedWeaponId != 0)
            {
                var w = _worldFilter[pickedWeaponId];
                if (w != null)
                {
                    pickedWeaponName = w.Name ?? "";
                    weaponWieldLoc   = w.Values(LongValueKey.CurrentWieldedLocation, 0);
                    pickedWeaponMode = w.ObjectClass switch
                    {
                        AcObjectClass.WandStaffOrb  => CombatMode.Magic,
                        AcObjectClass.MissileWeapon => CombatMode.Missile,
                        AcObjectClass.MeleeWeapon   => CombatMode.Melee,
                        _                           => CombatMode.Melee,
                    };
                }
            }
        }

        int liveMode = -1;
        if (_host.HasGetCurrentCombatMode)
        {
            try { liveMode = _host.GetCurrentCombatMode(); } catch { liveMode = -1; }
        }

        bool hasAmmo = false;
        try { hasAmmo = HasWieldedAmmo(); } catch { hasAmmo = false; }

        return new()
        {
            ActiveTargetId       = activeTargetId,
            LockedTargetId       = _lockedTargetId,
            ScannedCount         = _scannedTargets.Count,
            ClosestScannedId     = _scannedTargets.Count > 0 ? _scannedTargets[0].Id        : 0,
            ClosestScannedName   = _scannedTargets.Count > 0 ? _scannedTargets[0].Name ?? "" : "",
            ClosestScannedDist   = _scannedTargets.Count > 0 ? _scannedTargets[0].Distance  : 0.0,
            BusyCount            = BusyCount,
            FacingTarget         = _facingTarget,
            TargetLostScanTime   = _targetLostScanTime,
            LastAttackCmd        = lastAttackCmd,
            LastStanceAttempt    = lastStanceAttempt,
            LastEquipTime        = _lastEquipTime,
            CurrentCombatMode    = CurrentCombatMode,
            LiveCombatMode       = liveMode,
            BotAction            = _settings.BotAction ?? "",
            EnableCombat         = _settings.EnableCombat,
            IsMacroRunning       = _settings.IsMacroRunning,
            PickedWeaponId       = pickedWeaponId,
            PickedWeaponName     = pickedWeaponName,
            PickedWeaponMode     = pickedWeaponMode,
            PickedWeaponWieldLoc = weaponWieldLoc,
            HasWieldedAmmoFlag   = hasAmmo,
        };
    }

    public struct CombatStateSnapshot
    {
        public int      ActiveTargetId;
        public int      LockedTargetId;
        public int      ScannedCount;
        public int      ClosestScannedId;
        public string   ClosestScannedName;
        public double   ClosestScannedDist;
        public int      BusyCount;
        public bool     FacingTarget;
        public DateTime TargetLostScanTime;
        public DateTime LastAttackCmd;
        public DateTime LastStanceAttempt;
        public DateTime LastEquipTime;
        public int      CurrentCombatMode;
        public int      LiveCombatMode;
        public string   BotAction;
        public bool     EnableCombat;
        public bool     IsMacroRunning;
        public int      PickedWeaponId;
        public string   PickedWeaponName;
        public int      PickedWeaponMode;
        public int      PickedWeaponWieldLoc;
        public bool     HasWieldedAmmoFlag;
    }

    /// <summary>
    /// Periodic scan of all creatures. Filters: distance, self, blacklist, IsAttackable, raycast LOS.
    /// Call from OnHeartbeat or Think.
    /// </summary>
    public void ScanNearbyTargets()
    {
        if ((DateTime.Now - _lastScanTime).TotalMilliseconds < SCAN_INTERVAL_MS)
            return;
        _lastScanTime = DateTime.Now;

        // Raycast just became ready (warmup→ready edge, flagged on the bg
        // thread): one-time clean slate. Whatever got blacklisted or had its
        // miss/no-progress timers run up while targeting was degraded is
        // forgiven, so mobs that were present at login are re-evaluated with
        // real LOS/attack-type instead of being sidelined for 5 minutes.
        if (_raycastReadyResetPending)
        {
            _raycastReadyResetPending = false;
            _blacklistManager.ClearAll();
            _consecutiveMisses = 0;
            _lastAttackedTargetId = 0;
            _targetLockedAt = DateTime.MinValue;
            _lastDamageDealtAt = DateTime.MinValue;
            _host.Log("[RynthAi] Raycast ready — combat clean slate (blacklist cleared, target timers reset).");
        }

        _scannedTargets.Clear();
        _nearbyNoLos = 0;
        int playerId = (int)_playerId;
        if (playerId == 0) return;

        // Cache player pose once for angle tiebreaker — same math as GetFacingError.
        bool hasPose = _host.TryGetPlayerPose(out _, out float ppx, out float ppy, out _,
            out float pqw, out _, out _, out float pqz);
        double playerHeadingDeg = 0;
        if (hasPose)
        {
            double physYaw = 2.0 * Math.Atan2(pqz, pqw) * (180.0 / Math.PI);
            playerHeadingDeg = ((-physYaw) % 360.0 + 720.0) % 360.0;
        }

        double maxDist = _settings.MonsterRange;
        TargetingFSM.AttackType attackType = TargetingFSM.AttackType.Linear;
        if (RaycastInitialized && _raycastSystem?.TargetingFSM != null)
            attackType = _raycastSystem.GetAttackType(CurrentCombatMode, "");

        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.Id == playerId) continue;
            if ((int)wo.ObjectClass != (int)AcObjectClass.Monster) continue;

            double dist = _worldFilter.Distance(playerId, wo.Id);

            if (_blacklistManager.IsBlacklisted(wo.Id)) continue;

            if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id)) continue;

            // Dead but not yet reclassified as corpse — skip
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f) continue;

            if (dist > maxDist) continue;

            bool losBlocked = false;
            if (_settings.EnableRaycasting && RaycastInitialized && _raycastSystem != null)
            {
                RaycastCheckCount++;
                if (_raycastSystem.IsTargetBlocked(_host, (uint)wo.Id, attackType))
                {
                    RaycastBlockCount++;
                    losBlocked = true;
                }
            }

            // Don't blacklist from scan — just exclude from this result.
            // Blacklisting only happens when an active target fails LOS during attack.
            if (losBlocked)
            {
                _nearbyNoLos++; // in range + attackable but wall-blocked — still stops nav
                continue;
            }

            double angle = 180.0;
            if (hasPose && _host.TryGetObjectPosition((uint)wo.Id, out _, out float tx, out float ty, out _))
            {
                double desired = Math.Atan2(tx - ppx, ty - ppy) * (180.0 / Math.PI);
                if (desired < 0) desired += 360.0;
                double err = desired - playerHeadingDeg;
                while (err > 180.0) err -= 360.0;
                while (err < -180.0) err += 360.0;
                angle = Math.Abs(err);
            }
            _scannedTargets.Add(new ScannedTarget { Id = wo.Id, Distance = dist, Angle = angle, Name = wo.Name });
        }

        // Primary: closest first. Tiebreaker within 0.5yd: smallest facing angle first.
        _scannedTargets.Sort((a, b) =>
        {
            double dd = a.Distance - b.Distance;
            if (Math.Abs(dd) > 0.5) return dd < 0 ? -1 : 1;
            return a.Angle.CompareTo(b.Angle);
        });
    }

    public CombatManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache worldFilter, SpellManager? spellManager = null)
    {
        _host = host;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _worldFilter = worldFilter ?? throw new ArgumentNullException(nameof(worldFilter));
        _spellManager = spellManager;
        // Diagnostic: surface every blacklist (any path) with id + reason so a
        // single login test is conclusive instead of another guess.
        _blacklistManager.Log = m => _host.Log($"[Blacklist] {m}");
    }

    public void SetSpellManager(SpellManager spellManager) => _spellManager = spellManager;
    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;
    public void SetPlayerId(uint playerId)
    {
        _playerId = playerId;
        _monsterMatchEval = new MonsterMatchEvaluator(_worldFilter, playerId);
    }
    public void SetRaycastSystem(MainLogic raycast)
    {
        bool wasReady = RaycastInitialized;
        _raycastSystem = raycast;
        RaycastInitialized = raycast?.IsInitialized ?? false;

        // Warmup→ready edge. This runs on the raycast-init bg thread, so do
        // NOT mutate combat state here — defer the clean slate to the next
        // main-thread scan. Anything sidelined while targeting was degraded
        // (no LOS, Linear attack-type) gets re-evaluated now it's real.
        if (!wasReady && RaycastInitialized)
            _raycastReadyResetPending = true;
    }

    private uint _playerId;
    private MonsterMatchEvaluator? _monsterMatchEval;

    private Dictionary<string, string> _monsterWeaknesses = new(StringComparer.OrdinalIgnoreCase);
    public void SetMonsterWeaknesses(Dictionary<string, string> weaknesses)
        => _monsterWeaknesses = weaknesses ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _confirmedDebuffs = new();
    private int _lastDebuffTargetId = 0;

    private string? _pendingDebuffKey = null;
    private int _pendingDebuffTargetId = 0;
    private int _pendingDebuffTier = 0;
    private bool _waitingForDebuffResult = false;
    private DateTime _pendingDebuffCastTime = DateTime.MinValue;
    private const double DEBUFF_RESULT_TIMEOUT_MS = 3000.0;

    private static readonly Dictionary<string, string[]> DebuffSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fester",    new[] { "Fester Other",                    "Decrepitude's Grasp" } },
        { "Broadside", new[] { "Missile Weapon Ineptitude Other", "Broadside of a Barn" } },
        { "Gravity",   new[] { "Vulnerability Other",             "Gravity Well" } },
        { "Imperil",   new[] { "Imperil Other",                   "Gossamer Flesh" } },
        { "Yield",     new[] { "Magic Yield Other",               "Yield" } },
    };

    private static readonly Dictionary<string, string[]> SpellShapes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Flame Arc", "Ring of Fire", "Flame Streak", "Flame Bolt" } },
        { "Cold",      new[] { "Frost Arc", "Frost Ring", "Frost Streak", "Frost Bolt" } },
        { "Lightning", new[] { "Lightning Arc", "Shock Ring", "Lightning Streak", "Shock Wave" } },
        { "Acid",      new[] { "Acid Arc", "Acid Ring", "Acid Streak", "Acid Stream" } },
        { "Blade",     new[] { "Blade Arc", "Blade Ring", "Blade Streak", "Whirling Blade" } },
        { "Pierce",    new[] { "Force Arc", "Force Ring", "Force Streak", "Force Bolt" } },
        { "Bludgeon",  new[] { "Bludgeoning Arc", "Bludgeoning Ring", "Bludgeoning Streak", "Shock Wave" } },
        { "Slash",     new[] { "Blade Arc", "Blade Ring", "Blade Streak", "Whirling Blade" } },
    };

    private static readonly Dictionary<string, string[]> RingLoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Cassius' Ring of Fire", "Cassius' Ring of Fire II" } },
        { "Cold",      new[] { "Halo of Frost", "Halo of Frost II" } },
        { "Lightning", new[] { "Eye of the Storm", "Eye of the Storm II" } },
        { "Acid",      new[] { "Searing Disc", "Searing Disc II" } },
        { "Blade",     new[] { "Horizon's Blades", "Horizon's Blades II" } },
        { "Slash",     new[] { "Horizon's Blades", "Horizon's Blades II" } },
        { "Pierce",    new[] { "Nuhmudira's Spines", "Nuhmudira's Spines II" } },
        { "Bludgeon",  new[] { "Tectonic Rifts", "Tectonic Rifts II" } },
    };

    private static readonly Dictionary<string, string[]> VoidSpellShapes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Nether", new[] { "Nether Arc", "Nether Ring", "Nether Streak", "Nether Bolt" } },
        { "Fire",   new[] { "Corrosion Arc", "Corrosion Ring", "Corrosion Streak", "Nether Bolt" } },
    };

    public void InitializeRaycasting(string? acFolderPath = null)
    {
        try
        {
            _raycastSystem = new MainLogic();
            if (string.IsNullOrEmpty(acFolderPath)) acFolderPath = @"C:\Turbine\Asheron's Call";
            RaycastInitialized = _raycastSystem.Initialize(acFolderPath);
        }
        catch { RaycastInitialized = false; }
    }

    public void HandleChatForDebuffs(string text)
    {
        if (!_waitingForDebuffResult || string.IsNullOrEmpty(text)) return;

        // AC strips the leading 'Y' from cast-confirmation chat — text arrives as
        // "ou cast …". IndexOf matches both "You cast" and "ou cast".
        if (text.IndexOf("ou cast ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            string key = $"{_pendingDebuffTargetId}_{_pendingDebuffKey}";
            _confirmedDebuffs.Add(key);
            _waitingForDebuffResult = false;
            _lastSpellCast = DateTime.Now;
            return;
        }

        if (text.IndexOf("fizzle", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _host.WriteToChat($"[RynthAi] Fizzled: {_pendingDebuffKey} — recasting", 2);
            _waitingForDebuffResult = false;
            _lastSpellCast = DateTime.Now;
            return;
        }

        if (text.IndexOf("resists your spell", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _host.WriteToChat($"[RynthAi] Resisted: {_pendingDebuffKey} — recasting", 2);
            _waitingForDebuffResult = false;
            _lastSpellCast = DateTime.Now;
            return;
        }
    }

    public void ReportDamageOnTarget(int targetId)
    {
        if (targetId == _lastAttackedTargetId)
            _consecutiveMisses = 0;
        _blacklistManager.ClearFailure(targetId);
        if (targetId == activeTargetId || targetId == _lockedTargetId)
            _lastDamageDealtAt = DateTime.Now;
    }

    /// <summary>
    /// True during the post-login window where raycasting is enabled but the
    /// DAT parse hasn't finished, so combat targeting runs degraded (no LOS,
    /// Linear attack-type fallback). Attacks miss for reasons unrelated to the
    /// target — blacklist accrual must be suppressed here, otherwise the first
    /// mobs after login get sidelined for BlacklistTimeoutSec (default 300s)
    /// and nav runs the bot straight past them.
    /// </summary>
    private bool RaycastWarmingUp => _settings.EnableRaycasting && !RaycastInitialized;

    private void TrackAttackAttempt(int targetId)
    {
        if (_lastAttackedTargetId != targetId)
        {
            // Switched targets — reset miss counter
            _lastAttackedTargetId = targetId;
            _consecutiveMisses = 0;
            return;
        }

        // Raycast not ready yet — don't count these as the target's fault.
        // Misses here are our own degraded targeting; resume accrual once up.
        if (RaycastWarmingUp)
            return;

        _consecutiveMisses++;
        if (_consecutiveMisses >= _settings.BlacklistAttempts)
        {
            _blacklistManager.ReportFailure(targetId);
            if (_blacklistManager.IsBlacklisted(targetId))
            {
                _host.WriteToChat($"[RynthAi] No damage feedback after {_consecutiveMisses} attacks — blacklisting 0x{(uint)targetId:X8}", 2);
                _consecutiveMisses = 0;
                DropTarget($"blacklisted after {_settings.BlacklistAttempts} no-damage attacks");
            }
        }
    }

    private void DropTarget(string reason)
    {
        if (activeTargetId != 0)
            _host.Log($"[RynthAi] DropTarget 0x{activeTargetId:X8}: {reason}");
        activeTargetId = 0;
        _lockedTargetId = 0;
        _facingTarget = false;
        _returnToPhysicalCombat = false;
        _targetLockedAt    = DateTime.MinValue;
        _lastDamageDealtAt = DateTime.MinValue;
        _targetLostScanTime = DateTime.MinValue;
        ClearCombatTurnMotions();
    }

    public bool Think()
    {
        if (!_settings.EnableCombat) return false;

        double acDistanceLimit = _settings.MonsterRange;
        // Note: _blacklistManager self-expires entries on read (IsBlacklisted), so no
        // explicit cleanup pass is needed. The old vestigial `blacklistedTargets` dict
        // and its CleanupExpiredBlacklist helper were removed — they were never populated.
        _blacklistManager.AttemptThreshold = 1; // one report = blacklist; count is controlled by TrackAttackAttempt
        _blacklistManager.TimeoutSeconds   = _settings.BlacklistTimeoutSec;

        if (_raycastSystem?.TargetingFSM != null)
        {
            var fsm = _raycastSystem.TargetingFSM;
            fsm.MaxScanDistanceMeters = _settings.MonsterRange + 40.0f;
            fsm.UseArcs               = _settings.UseArcs;
            fsm.BowArcVelocity        = _settings.BowArcVelocity;
            fsm.CrossbowArcVelocity   = _settings.CrossbowArcVelocity;
            fsm.AtlatlArcVelocity     = _settings.AtlatlArcVelocity;
            fsm.MagicArcVelocity      = _settings.MagicArcVelocity;
        }

        // Update the pre-scanned target list (distance, attackable, LOS — no selection)
        try { ScanNearbyTargets(); } catch (Exception ex) { _host.Log($"[RynthAi] ScanNearbyTargets crashed: {ex.Message}"); }

        // Validate current target — restore from lock first so transient world-filter nulls
        // don't cause HandleCombatTrigger to pick a different mob on the same tick.
        if (_lockedTargetId != 0 && activeTargetId == 0)
            activeTargetId = _lockedTargetId;

        if (activeTargetId != 0)
        {
            var target = _worldFilter[activeTargetId];
            bool blacklisted = _blacklistManager.IsBlacklisted(activeTargetId);

            if (blacklisted)
                DropTarget("blacklisted");
            else if (target != null && (int)target.ObjectClass != (int)AcObjectClass.Monster)
                DropTarget("became corpse");
            else if (_worldFilter.GetHealthRatio(activeTargetId) == 0f)
                DropTarget("hp=0");
            else if (target != null &&
                     _worldFilter.Distance(_host.GetPlayerId() == 0 ? 0 : (int)_host.GetPlayerId(), activeTargetId) > acDistanceLimit)
                DropTarget("out of range");
            // If target == null here it's a transient world-filter miss — keep the lock,
            // the stillScanned grace period below will handle a truly dead/vanished mob.
        }

        // Score every candidate every tick — handles initial pick AND switching when
        // a meaningfully better target appears (stickiness bonus prevents flapping).
        HandleCombatTrigger();

        if (activeTargetId == 0)
        {
            // No valid target — go to Peace immediately to stop the client
            // from auto-running toward a distant monster.  The 500ms cooldown
            // prevents ChangeCombatMode spam while idling between spawns.
            if (_settings.PeaceModeWhenIdle && CurrentCombatMode != CombatMode.NonCombat
                && _scannedTargets.Count == 0 && BusyCount == 0)
            {
                if ((DateTime.Now - _lastPeaceAttempt).TotalMilliseconds > 500)
                {
                    _host.ChangeCombatMode(CombatMode.NonCombat);
                    _lastPeaceAttempt = DateTime.Now;
                }
            }
            return true;
        }

        // Verify active target is still in the scanned list (still visible + LOS clear)
        bool stillScanned = false;
        for (int i = 0; i < _scannedTargets.Count; i++)
        {
            if (_scannedTargets[i].Id == activeTargetId) { stillScanned = true; break; }
        }
        if (!stillScanned)
        {
            // Give it a grace period before dropping — one bad LOS result or scan gap shouldn't
            // hand control to navigation mid-fight.
            if (_targetLostScanTime == DateTime.MinValue)
                _targetLostScanTime = DateTime.Now;

            if ((DateTime.Now - _targetLostScanTime).TotalMilliseconds > TARGET_SCAN_GRACE_MS)
                DropTarget("scan grace expired");
            return true;
        }
        _targetLostScanTime = DateTime.MinValue; // back in scan — reset grace timer

        var targetObj = _worldFilter[activeTargetId];
        if (targetObj == null) return true; // transient miss — skip tick, keep lock

        // Time-based blacklist: if we've been engaged with this target for longer than
        // TargetNoProgressTimeoutSec without dealing any damage, give up and blacklist it.
        // Uses last-damage time when available; falls back to lock time.
        // Skip the no-progress blacklist while raycast is warming up — the bot
        // can't deal damage with degraded targeting, so this timer would
        // otherwise sideline a perfectly good target through no fault of its
        // own. Warmup is sub-second to ~2s, far under TargetNoProgressTimeoutSec.
        int noProgressTimeoutSec = _settings.TargetNoProgressTimeoutSec;
        if (noProgressTimeoutSec > 0 && _targetLockedAt != DateTime.MinValue && !RaycastWarmingUp)
        {
            DateTime refTime = _lastDamageDealtAt != DateTime.MinValue ? _lastDamageDealtAt : _targetLockedAt;
            if ((DateTime.Now - refTime).TotalSeconds > noProgressTimeoutSec)
            {
                _host.WriteToChat($"[RynthAi] No progress on {targetObj.Name} after {noProgressTimeoutSec}s — blacklisting", 2);
                _blacklistManager.TimeoutSeconds = _settings.BlacklistTimeoutSec;
                _blacklistManager.ReportFailure(activeTargetId);
                DropTarget("no-progress timeout");
                return true;
            }
        }

        // Distance gate: don't issue any attack commands (SelectItem, attack, cast)
        // when the target is beyond MonsterRange. The lock stays so we re-engage if
        // it comes back in range, but no commands means no client auto-run toward it.
        double currentDist = _worldFilter.Distance(
            _host.GetPlayerId() == 0 ? 0 : (int)_host.GetPlayerId(), activeTargetId);
        if (currentDist > acDistanceLimit)
            return true;

        if (!EquipWeaponAndSetStance(targetObj, "Auto"))
            return true;

        bool isRanged = CurrentCombatMode == CombatMode.Missile || CurrentCombatMode == CombatMode.Magic;
        bool useNative = _settings.UseNativeAttack && _host.HasNativeAttack;

        // Melee: clear turn motions every tick. AC handles melee facing during the
        // attack animation — we must not run any turn motion alongside it or anything
        // left over from navigation will spin the character indefinitely.
        if (!isRanged && !useNative)
            ClearCombatTurnMotions();

        double attackCmdIntervalMs = CurrentCombatMode == CombatMode.Magic
            ? _settings.SpellCastIntervalMs
            : 1000.0;
        if ((DateTime.Now - lastAttackCmd).TotalMilliseconds >= attackCmdIntervalMs)
        {
            // Client is busy processing a previous action — don't queue more
            if (BusyCount > 0)
                return true;

            // Native attack handles facing internally — skip manual facing
            if (!useNative)
            {
                // For ranged/magic attacks, confirm we're facing the target before firing.
                // Melee turn motions are already cleared above every tick.
                if (isRanged)
                {
                    double facingError = GetFacingError(activeTargetId);
                    if (facingError > FACE_TOLERANCE_DEG)
                    {
                        FaceTarget(activeTargetId);
                        if (!_facingTarget)
                        {
                            _facingTarget = true;
                            _faceStartTime = DateTime.Now;
                        }
                        if ((DateTime.Now - _faceStartTime).TotalMilliseconds < FACE_TIMEOUT_MS)
                            return true; // not facing yet, keep waiting
                    }
                    _facingTarget = false;
                    ClearCombatTurnMotions();
                }
            }

            // Don't attack in missile mode without ammo
            if (CurrentCombatMode == CombatMode.Missile && !HasWieldedAmmo())
                return true;

            _host.SelectItem((uint)activeTargetId);

            if (CurrentCombatMode == CombatMode.Magic && _spellManager != null)
            {
                // Don't issue a combat spell while the previous cast GESTURE is
                // still animating. AC refuses a mid-gesture cast with the
                // server-driven "You're too busy!" notice; sustained refusals
                // re-enter AC's AddTextToScroll → 0x00460D1D AV. CanCastNow is
                // the engine's CMotionInterp gesture gate (the SAME gate
                // BuffManager uses). It degrades to true on an engine without
                // the gate, where the SpellCastIntervalMs attack throttle still
                // bounds retries. Melee/missile (AttackTarget, below) is
                // deliberately NOT gated on this — a weapon swing isn't a spell
                // cast and AC paces the swing animation itself.
                if (!_host.CanCastNow)
                    return true;

                AttackWithMagic(targetObj);

                if (_returnToPhysicalCombat)
                {
                    var rule2 = GetRuleForTarget(targetObj);
                    string elem2 = GetPreferredElement(targetObj, rule2);
                    if (rule2 != null && !HasPendingDebuffs(rule2, elem2))
                    {
                        _returnToPhysicalCombat = false;
                        if (_savedWeaponId != 0)
                        {
                            _host.UseObject((uint)_savedWeaponId);
                            _savedWeaponId = 0;
                        }
                        EquipWeaponAndSetStance(targetObj, "Auto");
                    }
                }
            }
            else
            {
                var rule = GetRuleForTarget(targetObj);

                if (rule != null && _spellManager != null && !_returnToPhysicalCombat)
                {
                    string elem = GetPreferredElement(targetObj, rule);
                    if (HasPendingDebuffs(rule, elem))
                    {
                        int wandId = FindWandInItems();
                        if (wandId != 0)
                        {
                            // TODO: Save current equipped weapon when inventory API is available.
                            _savedWeaponId = 0;
                            _returnToPhysicalCombat = true;
                            _host.UseObject((uint)wandId);
                            _lastEquipTime = DateTime.Now; // gate AttackWithMagic until wand is wielded
                            _host.ChangeCombatMode(CombatMode.Magic);
                            lastAttackCmd = DateTime.Now;
                            return true;
                        }
                    }
                }

                // Physical combat always attacks — spell shape flags (UseArc/Bolt/Ring/Streak)
                // are only relevant in magic mode and must not gate melee/missile attacks.
                AttackTarget();
            }

            // Ring spells hit an area — no per-target damage feedback is generated,
            // so they must not count toward the blacklist miss counter.
            if (!_lastCastWasRing)
                TrackAttackAttempt(activeTargetId);
            lastAttackCmd = DateTime.Now;
        }
        return true;
    }

    // Diagnostic: log the combat manager state when meaningful inputs change.
    // We bucket msSinceAttack into Recent (<3s = "in active engagement") vs
    // Stale (>=3s) so this doesn't fire on every tick — the raw ms ticks up
    // continuously and would flood the log at ~30 lines/sec otherwise (which
    // it did before this fix — bot lived ~64s under that load and the file-
    // write contention may have helped trigger AC's idle-exit timeout).
    private string _lastCombatStateKey = "";
    private void LogCombatStateIfChanged()
    {
        long msSinceAttack = lastAttackCmd == DateTime.MinValue
            ? -1
            : (long)(DateTime.Now - lastAttackCmd).TotalMilliseconds;
        string attackBucket = msSinceAttack < 0 ? "never"
                            : msSinceAttack < 3000 ? "recent"
                            : msSinceAttack < 10_000 ? "stale"
                            : "cold";
        string key = $"enableCombat={_settings.EnableCombat} scanned={_scannedTargets.Count} active=0x{activeTargetId:X8} busy={BusyCount} mode={CurrentCombatMode} attack={attackBucket} action='{_settings.BotAction}'";
        if (key == _lastCombatStateKey) return;
        _lastCombatStateKey = key;
        _host.Log($"Combat: state {key} (msSinceAttack={msSinceAttack})");
    }

    public void OnHeartbeat()
    {
        if (!_settings.IsMacroRunning)
        {
            // Clear turn motions once on the transition from running → stopped,
            // so the character doesn't spin indefinitely after a mid-turn stop.
            // Do NOT clear every frame — that blocks manual keyboard turning.
            if (_wasMacroRunning)
            {
                _wasMacroRunning = false;
                ClearCombatTurnMotions();
            }
            return;
        }
        _wasMacroRunning = true;

        LogCombatStateIfChanged();

        // Always run the scan and BotAction state update, even when combat can't
        // take actions. ScanNearbyTargets has no side-effects (no game commands)
        // and must stay fresh so HasTargets is accurate for nav-blocking decisions.
        // Without this, stale scan data keeps combatBlocking = true after a kill,
        // preventing navigation from resuming while the bot is buffing, etc.
        if (_settings.EnableCombat)
        {
            try { ScanNearbyTargets(); }
            catch (Exception ex) { _host.Log($"[RynthAi] ScanNearbyTargets CRASH: {ex.Message}"); }

            // Only hold the "Combat" BotAction lock while actively engaging.
            // "Actively engaging" = an attack command was issued recently.
            // Without this, having anything visible in scan range latched
            // BotAction = "Combat" and blocked NavigationEngine.Tick from
            // running, even when CombatManager couldn't actually attack
            // (out of range, BusyCount stuck, etc.) — symptom was bot
            // standing still surrounded by far-off mobs while nav refused
            // to move. By tying the lock to recent attack activity, nav
            // gets to run between engagement windows and bot can chase /
            // reposition.
            // BotAction is no longer written here. STEP 2 (ACTIVITY_ARBITER_PLAN.md):
            // the ActivityArbiter in RynthAiPlugin.OnTick is the sole writer of
            // the "Combat"/"Navigating" strings, driven by the pure
            // HasEngageableTarget predicate. CombatManager only reads BotAction
            // (via canRun below) and acts. Removing these writes is what kills
            // the squat-without-fighting freeze: Combat can no longer latch the
            // lock based on broad/stale scan state.
        }

        // Combat can run in Default/Combat, can interrupt navigation unless nav boost is on,
        // and can interrupt looting unless loot boost is on.
        // Buffing always blocks combat — if buffs drop, the character dies.
        bool canRun = _settings.BotAction == "Default"
                   || _settings.BotAction == "Combat"
                   || (_settings.BotAction == "Navigating" && !_settings.BoostNavPriority)
                   || (_settings.BotAction == "Looting" && !_settings.BoostLootPriority);
        if (!canRun) return;

        if (_settings.EnableCombat)
        {
            try { Think(); }
            catch (Exception ex) { _host.Log($"[RynthAi] Think CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }

            // BotAction no longer written post-Think either — the arbiter
            // recomputes from HasEngageableTarget every tick (33ms lag, picks
            // up activeTargetId set by the Think() above on the next pass).
            // See STEP 2 note in the pre-Think branch.
        }
    }

    public void HandleCombatTrigger()
    {
        // Utility-AI selection: score every visible candidate, pick the highest.
        // The locked target carries a stickiness bonus so we only switch when a
        // genuinely better option appears — no flapping on near-ties.
        if (_scannedTargets.Count == 0) return;

        int bestId = 0;
        double bestScore = double.MinValue;
        foreach (var c in _scannedTargets)
        {
            double s = ScoreCandidate(c);
            if (c.Id == _lockedTargetId) s += TARGET_SWITCH_STICKINESS;
            if (s > bestScore) { bestScore = s; bestId = c.Id; }
        }

        if (bestId == 0 || bestId == _lockedTargetId) return;

        activeTargetId      = bestId;
        _lockedTargetId     = bestId;
        _targetLockedAt     = DateTime.Now;
        _lastDamageDealtAt  = DateTime.MinValue;
        _consecutiveMisses  = 0;
        _facingTarget       = false;
        _returnToPhysicalCombat = false;
        ClearCombatTurnMotions();

        // Internal lock is set unconditionally so combat is ready to swing the
        // moment busy clears. SelectItem during a combat-mode transition can
        // wedge the client action queue, so only fire it when not busy.
        if (BusyCount == 0)
            _host.SelectItem((uint)bestId);
    }

    private double ScoreCandidate(in ScannedTarget c)
    {
        double maxDist = Math.Max(1.0, _settings.MonsterRange);
        double distScore   = Math.Clamp((maxDist - c.Distance) / maxDist, 0.0, 1.0) * 100.0;
        float  hpRatio     = _worldFilter.GetHealthRatio(c.Id);
        double hpScore     = (1.0 - Math.Clamp(hpRatio, 0f, 1f)) * 50.0;
        double threatScore = c.Distance < 3.0 ? 30.0 : 0.0;
        double facingScore = (1.0 - Math.Min(1.0, c.Angle / 180.0)) * 10.0;

        // Monster-rule priority: default scoring already prefers "fastest to attack"
        // (close/low-hp/in-melee/in-front), which is right for normal grind dungeons.
        // The user uses MonsterRules.Priority to elevate threats they want focused
        // first — so only apply a bonus when Priority is *above* the default of 1.
        // Priority 10 → +45 (about half a max-distance score, enough to switch
        // targets unless the alternative is much closer/lower-HP).
        string targetName = c.Name;
        var rule = _settings.MonsterRules.FirstOrDefault(
            r => !r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                 targetName.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0);
        double priorityScore = rule != null ? Math.Max(0, rule.Priority - 1) * 5.0 : 0;

        return distScore + hpScore + threatScore + facingScore + priorityScore;
    }

    private DateTime _lastEquipTime = DateTime.MinValue;
    private DateTime _lastStanceTime = DateTime.MinValue;

    // Returns true when the correct weapon is wielded and combat mode matches — safe to attack.
    // Returns false when a weapon swap or stance change is in progress — caller should skip this tick.
    private bool EquipWeaponAndSetStance(WorldObject target, string monsterWeakness = "Auto")
    {
        if (target == null) return true;

        int targetWeaponId = 0;

        var rule = _settings.MonsterRules.FirstOrDefault(
            r => target.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0);
        if (rule == null)
            rule = _settings.MonsterRules.FirstOrDefault(m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));

        string desired = (rule != null && rule.DamageType != "Auto") ? rule.DamageType : monsterWeakness;

        if (rule != null && rule.WeaponId != 0)
            targetWeaponId = rule.WeaponId;
        else
        {
            var bestWeapon = _settings.ItemRules.FirstOrDefault(i => i.Element.Equals(desired, StringComparison.OrdinalIgnoreCase))
                             ?? _settings.ItemRules.FirstOrDefault();
            if (bestWeapon != null) targetWeaponId = bestWeapon.Id;
        }

        if (targetWeaponId == 0)
            targetWeaponId = FindWandInItems();
        if (targetWeaponId == 0) return true;

        var weaponObj = _worldFilter[targetWeaponId];
        if (weaponObj == null) return true;

        int desiredMode = (weaponObj.ObjectClass) switch
        {
            AcObjectClass.WandStaffOrb  => CombatMode.Magic,
            AcObjectClass.MissileWeapon => CombatMode.Missile,
            AcObjectClass.MeleeWeapon   => CombatMode.Melee,
            _                           => CombatMode.Melee,
        };

        // Use CurrentWieldedLocation (stype=10) — has an InqInt fallback that works even
        // when the phys-obj offset probe hasn't fired yet (unlike TryGetObjectWielderInfo).
        bool alreadyWielded = weaponObj.Values(LongValueKey.CurrentWieldedLocation, 0) > 0;

        if (alreadyWielded)
        {
            // Don't enter missile mode without ammo — AC rejects it and cycles stance
            if (desiredMode == CombatMode.Missile && !HasWieldedAmmo())
                return false;

            if (CurrentCombatMode != desiredMode &&
                (DateTime.Now - lastStanceAttempt).TotalMilliseconds > 1000)
            {
                _host.ChangeCombatMode(desiredMode);
                lastStanceAttempt = DateTime.Now;
            }
            return CurrentCombatMode == desiredMode;
        }

        // Wield-location probe hasn't confirmed this item yet. Two cases:
        //
        // A) Already in the correct combat mode — AC enforces "weapon wielded ↔ mode matches",
        //    so trust it. Calling UseObject on an already-wielded wand is a no-op in AC
        //    (it opens the wand's properties), so we must NOT call it here.
        if (CurrentCombatMode == desiredMode)
            return true;

        // B) Mode doesn't match. Either the weapon genuinely isn't wielded, or CurrentCombatMode
        //    is stale (e.g. hot-reload didn't re-fire OnCombatModeChange). Request both a mode
        //    change and an equip. ChangeCombatMode succeeds if the wand is already wielded
        //    (OnCombatModeChange fires → fixes hot-reload next tick). UseObject equips it if
        //    it truly isn't wielded yet.
        if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > 1000)
        {
            _host.ChangeCombatMode(desiredMode);
            lastStanceAttempt = DateTime.Now;
        }
        if ((DateTime.Now - _lastEquipTime).TotalMilliseconds > 2000)
        {
            _host.UseObject((uint)targetWeaponId);
            _lastEquipTime = DateTime.Now;
        }
        return false;
    }

    /// <summary>
    /// Ensure a wand is wielded and the player is in Magic combat mode.
    /// Used by NavigationEngine to prepare for recall casts. Returns true only when
    /// ready to cast; when false, caller should re-tick (equip/stance are throttled
    /// internally so calling every tick is safe).
    /// </summary>
    public bool EnsureMagicReady()
    {
        int wandId = FindWandInItems();
        if (wandId == 0) return false;

        var wand = _worldFilter[wandId];
        if (wand == null) return false;

        bool alreadyWielded = wand.Values(LongValueKey.CurrentWieldedLocation, 0) > 0;

        if (alreadyWielded)
        {
            if (CurrentCombatMode == CombatMode.Magic) return true;
            if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > 1000)
            {
                _host.ChangeCombatMode(CombatMode.Magic);
                lastStanceAttempt = DateTime.Now;
            }
            return false;
        }

        if (CurrentCombatMode == CombatMode.Magic) return true;

        if ((DateTime.Now - lastStanceAttempt).TotalMilliseconds > 1000)
        {
            _host.ChangeCombatMode(CombatMode.Magic);
            lastStanceAttempt = DateTime.Now;
        }
        if ((DateTime.Now - _lastEquipTime).TotalMilliseconds > 2000)
        {
            _host.UseObject((uint)wandId);
            _lastEquipTime = DateTime.Now;
        }
        return false;
    }

    public string GetRaycastStatus()
    {
        if (_raycastSystem == null) return "Raycasting: NOT INITIALIZED";
        string status = _settings.EnableRaycasting ? "ACTIVE" : "DISABLED";
        return $"Raycasting: {status}\n  Status: {_raycastSystem.StatusMessage}\n  Checks: {RaycastCheckCount}, Blocks: {RaycastBlockCount}";
    }

    public List<string> GetRaycastDiagLog()
    {
        var lines = new List<string>();
        if (_raycastSystem?.GeometryLoader?.DiagLog != null)
            foreach (var line in _raycastSystem.GeometryLoader.DiagLog) lines.Add(line);
        return lines;
    }

    private TargetingFSM.AttackType DetermineAttackTypeForLOS()
    {
        if (CurrentCombatMode == CombatMode.Magic)
        {
            // Get rule from current target name if available
            var targetObj = _worldFilter[activeTargetId];
            var rule = GetRuleForTarget(targetObj);
            if (rule != null && rule.UseArc) return TargetingFSM.AttackType.MagicArc;
            return TargetingFSM.AttackType.Linear;
        }

        if (_raycastSystem?.TargetingFSM != null)
            return _raycastSystem.GetAttackType(CurrentCombatMode, "");

        return TargetingFSM.AttackType.Linear;
    }

    private void FaceTarget(int targetId)
    {
        try
        {
            if (!_host.TryGetPlayerPose(out _, out float px, out float py, out _,
                    out float qw, out _, out _, out float qz))
                return;
            if (!_host.TryGetObjectPosition((uint)targetId, out _, out float tx, out float ty, out _))
                return;

            double dx = tx - px;
            double dy = ty - py;
            double desiredDeg = Math.Atan2(dx, dy) * (180.0 / Math.PI);
            if (desiredDeg < 0) desiredDeg += 360.0;

            double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
            double currentDeg = ((-physYawDeg) % 360.0 + 720.0) % 360.0;

            double error = desiredDeg - currentDeg;
            while (error >  180.0) error -= 360.0;
            while (error < -180.0) error += 360.0;

            if (Math.Abs(error) <= FACE_TOLERANCE_DEG)
            {
                ClearCombatTurnMotions();
            }
            else if (error > 0)
            {
                _host.SetMotion(MotionTurnRight, true);
                _host.SetMotion(MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotion(MotionTurnLeft,  true);
                _host.SetMotion(MotionTurnRight, false);
            }
        }
        catch { }
    }

    private void ClearCombatTurnMotions()
    {
        _host.SetMotion(MotionTurnRight, false);
        _host.SetMotion(MotionTurnLeft,  false);
    }

    /// <summary>
    /// Returns the absolute heading error (degrees) between the player's current
    /// facing and the direction to the target. Used to decide whether we need to
    /// wait for a heading change before firing a ranged attack.
    /// </summary>
    private double GetFacingError(int targetId)
    {
        try
        {
            if (!_host.TryGetPlayerPose(out _, out float px, out float py, out _,
                    out float qw, out _, out _, out float qz))
                return 180.0; // can't read pose — assume worst case

            if (!_host.TryGetObjectPosition((uint)targetId, out _, out float tx, out float ty, out _))
                return 180.0;

            // Desired heading to target (0=North CW)
            double dx = tx - px;
            double dy = ty - py;
            double desiredDeg = Math.Atan2(dx, dy) * (180.0 / Math.PI);
            if (desiredDeg < 0) desiredDeg += 360.0;

            // Current heading from quaternion (same formula as NavigationEngine)
            double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
            double currentDeg = ((-physYawDeg) % 360.0 + 720.0) % 360.0;

            double error = desiredDeg - currentDeg;
            while (error > 180.0) error -= 360.0;
            while (error < -180.0) error += 360.0;
            return Math.Abs(error);
        }
        catch { return 180.0; }
    }

    private bool HasWieldedAmmo()
    {
        int playerId = unchecked((int)_playerId);

        // Walk via GetDirectInventory(forceRefresh:true). This is the same path
        // MissileCraftingManager uses successfully — it triggers per-item
        // wielder-info lookups on the cache, which populates Wielder /
        // WieldedLocation. AllKnownObjects() doesn't trigger those probes, so
        // arrows that arrived via OnCreateObject keep WieldedLocation=0 and
        // never match. The forced refresh adds ~one InqInt call per pack item
        // but is cheap and fixes detection definitively.
        foreach (var item in _worldFilter.GetDirectInventory(forceRefresh: true))
            if (LooksLikeWieldedAmmo(item, playerId)) return true;

        return false;
    }

    // EquipMask bit for the ammunition slot. Items wielded in this slot are ammo
    // by definition — far more reliable than name or ItemType inspection because
    // some servers type their arrows as MissileWeapon (0x100) rather than the
    // MissileAmmo bit (0x400) that AC's vanilla data has.
    private const int AmmunitionSlot = 0x00800000;

    private bool LooksLikeWieldedAmmo(WorldObject item, int playerId)
    {
        if (item == null) return false;

        // Authoritative: ask AC's runtime for the wielder + slot directly.
        // The cached WieldedLocation/Wielder fields can be 0 forever if the
        // item arrived via OnCreateObject and never went through the
        // GetDirectInventory walk that probes wielder info. Querying the
        // host API per-candidate side-steps that.
        int loc = 0;
        bool slotKnown = false;
        if (_host.HasGetObjectWielderInfo &&
            _host.TryGetObjectWielderInfo(unchecked((uint)item.Id), out uint wielder, out uint locFromApi))
        {
            if (playerId != 0 && wielder != 0 && wielder != (uint)playerId) return false;
            if (locFromApi > 0) { loc = unchecked((int)locFromApi); slotKnown = true; }
        }

        // Fall back to InqInt and the cache field if the wielder API didn't answer.
        if (!slotKnown)
        {
            int locInq   = item.Values(LongValueKey.CurrentWieldedLocation, 0);
            int locCache = item.WieldedLocation;
            loc = locInq > 0 ? locInq : locCache;
            if (loc <= 0) return false;
            if (playerId != 0 && item.Wielder != 0 && item.Wielder != playerId) return false;
        }

        // Authoritative: ammunition slot bit.
        if ((loc & AmmunitionSlot) != 0)
            return true;

        // Name-based fallback for items in non-ammo slots that still match
        // ammo names (rare server-custom configurations).
        string n = item.Name;
        if (string.IsNullOrEmpty(n)) return false;
        if (n.Contains("Bundle") || n.Contains("Wrapped")) return false;
        return n.Contains("Arrow") || n.Contains("Quarrel") || n.Contains("Bolt") || n.Contains("Dart");
    }

    private void AttackTarget()
    {
        try
        {
            bool isMissile = CurrentCombatMode == CombatMode.Missile;
            uint targetId  = (uint)activeTargetId;

            // Don't fire in missile mode without ammo — let crafting manager handle it
            if (isMissile && !HasWieldedAmmo())
                return;

            float power;
            int powerPct = isMissile ? _settings.MissileAttackPower : _settings.MeleeAttackPower;
            if (powerPct < 0)
            {
                power = 1.0f;
                if (_settings.UseRecklessness && (_charSkills == null || _charSkills[AcSkillType.Recklessness].Training >= 2))
                    power = 0.8f;
            }
            else
            {
                power = powerPct / 100f;
            }

            int uiHeight = isMissile ? _settings.MissileAttackHeight : _settings.MeleeAttackHeight;
            int acHeight = uiHeight switch { 0 => 3, 2 => 1, _ => 2 }; // Low=3, Med=2, High=1

            // Native attack: select target, Start fills power bar, End fires the attack.
            // Called each attack cycle — the client handles turn-to-face naturally.
            if (_settings.UseNativeAttack && _host.HasNativeAttack)
            {
                _host.SelectItem(targetId);
                _host.NativeAttack(acHeight, power);
                return;
            }

            // Direct attack: raw game action (bypasses client facing)
            if (isMissile)
                _host.MissileAttack(targetId, acHeight, power);
            else
                _host.MeleeAttack(targetId, acHeight, power);
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  MAGIC COMBAT SYSTEM
    // ══════════════════════════════════════════════════════════════════════════

    private MonsterRule? GetRuleForTarget(WorldObject? target)
    {
        if (target == null) return null;

        string metaState = _settings.CurrentState ?? "Default";

        foreach (var r in _settings.MonsterRules)
        {
            if (r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;

            bool matches;
            if (!string.IsNullOrWhiteSpace(r.MatchExpression))
            {
                // Expression match: eval first; if expression is true AND name is non-empty, also check name.
                bool exprTrue = _monsterMatchEval?.Evaluate(r.MatchExpression, target, metaState) ?? false;
                if (!string.IsNullOrWhiteSpace(r.Name))
                    matches = exprTrue && target.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0;
                else
                    matches = exprTrue;
            }
            else
            {
                matches = !string.IsNullOrWhiteSpace(r.Name) &&
                          target.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            if (matches) return r;
        }

        return _settings.MonsterRules.FirstOrDefault(
            m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
    }

    private void AttackWithMagic(WorldObject target)
    {
        if (_spellManager == null || target == null) return;
        // Don't cast while a weapon equip is in progress — the wand may not be registered
        // as wielded yet. _lastEquipTime is set whenever UseObject is called for a wand swap.
        if ((DateTime.Now - _lastEquipTime).TotalMilliseconds < 3000)
            return;
        if ((DateTime.Now - _lastSpellCast).TotalMilliseconds < _settings.SpellCastIntervalMs) return;

        if (_waitingForDebuffResult)
        {
            if ((DateTime.Now - _pendingDebuffCastTime).TotalMilliseconds > DEBUFF_RESULT_TIMEOUT_MS)
            {
                _confirmedDebuffs.Add($"{_pendingDebuffTargetId}_{_pendingDebuffKey}");
                _waitingForDebuffResult = false;
            }
            else return;
        }

        if (activeTargetId != _lastDebuffTargetId)
        {
            _confirmedDebuffs.Clear();
            _lastDebuffTargetId = activeTargetId;
        }

        var rule = GetRuleForTarget(target);
        string element = GetPreferredElement(target, rule);

        if (rule != null)
        {
            var pendingDebuffs = BuildDebuffList(rule, element);
            foreach (var debuffKey in pendingDebuffs)
            {
                string key = $"{activeTargetId}_{debuffKey}";
                if (_confirmedDebuffs.Contains(key)) continue;

                int spellId = 0;
                int castTier = 0;
                if (debuffKey.StartsWith("Vuln:"))
                    spellId = FindBestVulnSpellWithTier(debuffKey.Substring(5), out castTier);
                else
                    spellId = FindBestDebuffSpellWithTier(debuffKey, out castTier);

                if (spellId == 0) continue;

                try
                {
                    _host.CastSpell((uint)activeTargetId, spellId);
                    _lastSpellCast = DateTime.Now;
                    _lastCastWasRing = false;
                    _pendingDebuffKey = debuffKey;
                    _pendingDebuffTargetId = activeTargetId;
                    _pendingDebuffTier = castTier;
                    _waitingForDebuffResult = true;
                    _pendingDebuffCastTime = DateTime.Now;
                    _host.WriteToChat($"[RynthAi] Casting: {debuffKey} (T{castTier}) on {target.Name}", 5);
                }
                catch { }
                return;
            }
        }

        if ((DateTime.Now - _lastSpellCast).TotalMilliseconds < ATTACK_SPELL_COOLDOWN_MS) return;

        if (rule != null && !rule.UseArc && !rule.UseRing && !rule.UseStreak && !rule.UseBolt) return;

        int warTier  = _spellManager?.GetHighestSpellTier(AcSkillType.WarMagic)  ?? 0;
        int voidTier = _spellManager?.GetHighestSpellTier(AcSkillType.VoidMagic) ?? 0;
        int offensiveSpellId = FindBestShapedSpell(element, rule, out bool isRing);
        if (offensiveSpellId != 0)
        {
            try
            {
                _host.CastSpell((uint)activeTargetId, offensiveSpellId);
                _lastSpellCast = DateTime.Now;
                _lastCastWasRing = isRing;
            }
            catch { }
        }
        else
        {
            _host.WriteToChat($"[RynthAi] No spell found: elem={element} warTier={warTier} voidTier={voidTier} pid={_playerId}", 2);
            _lastSpellCast = DateTime.Now; // suppress repeated spam
        }
    }

    private string GetPreferredElement(WorldObject? target, MonsterRule? rule)
    {
        if (rule != null && !string.IsNullOrEmpty(rule.DamageType) &&
            !rule.DamageType.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return rule.DamageType;

        if (target != null && _monsterWeaknesses.Count > 0)
            foreach (var entry in _monsterWeaknesses)
                if (target.Name.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return entry.Value;

        return "Fire";
    }

    private bool HasPendingDebuffs(MonsterRule rule, string element)
    {
        if (rule == null) return false;
        foreach (var debuffKey in BuildDebuffList(rule, element))
            if (!_confirmedDebuffs.Contains($"{activeTargetId}_{debuffKey}")) return true;
        return false;
    }

    private int FindWandInItems()
    {
        // Prefer explicitly configured wand from item rules
        foreach (var item in _settings.ItemRules)
        {
            var wo = _worldFilter[item.Id];
            if (wo != null && IsWandObject(wo)) return item.Id;
        }
        // Inventory cache scan — ObjectClass first, name fallback for unclassified items
        foreach (var wo in _worldFilter.GetInventory())
        {
            if (IsWandObject(wo)) return wo.Id;
        }
        return 0;
    }

    private static bool IsWandObject(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.WandStaffOrb || IsWandName(wo.Name);

    private static bool IsWandName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("Orb",      StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Staff",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Wand",     StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Scepter",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Sceptre",  StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Baton",    StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Crozier",  StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static List<string> BuildDebuffList(MonsterRule rule, string element)
    {
        var list = new List<string>();
        if (rule.Imperil)   list.Add("Imperil");
        if (rule.Vuln)      list.Add("Vuln:" + element);
        if (!string.IsNullOrEmpty(rule.ExVuln) && !rule.ExVuln.Equals("None", StringComparison.OrdinalIgnoreCase))
            list.Add("Vuln:" + rule.ExVuln);
        if (rule.Fester)    list.Add("Fester");
        if (rule.Yield)     list.Add("Yield");
        if (rule.Broadside) list.Add("Broadside");
        if (rule.GravityWell) list.Add("Gravity");
        return list;
    }

    private int FindBestDebuffSpellWithTier(string debuffType, out int tier)
    {
        tier = 0;
        if (_spellManager == null) return 0;

        if (!DebuffSpells.TryGetValue(debuffType, out string[]? spellInfo) || spellInfo.Length < 2) return 0;

        string tieredBase = spellInfo[0];
        string loreName   = spellInfo[1];

        int maxTier = _spellManager.GetHighestSpellTier(AcSkillType.CreatureEnchantment);

        if (maxTier >= 8) { int id = TrySpellByName($"Incantation of {tieredBase}"); if (id != 0) { tier = 8; return id; } }
        if (maxTier >= 7 && !string.IsNullOrEmpty(loreName)) { int id = TrySpellByName(loreName); if (id != 0) { tier = 7; return id; } }

        for (int t = Math.Min(maxTier, 7); t >= 1; t--)
        {
            int id = TrySpellByName($"{tieredBase} {GetRomanNumeral(t)}");
            if (id != 0) { tier = t; return id; }
        }
        return 0;
    }

    private int FindBestVulnSpellWithTier(string element, out int tier)
    {
        tier = 0;
        if (_spellManager == null) return 0;
        if (!VulnSpells.TryGetValue(element, out string[]? vulnBases)) return 0;

        int maxTier = _spellManager.GetHighestSpellTier(AcSkillType.CreatureEnchantment);

        foreach (string baseName in vulnBases)
        {
            if (maxTier >= 8) { int id = TrySpellByName($"Incantation of {baseName}"); if (id != 0) { tier = 8; return id; } }
            for (int t = Math.Min(maxTier, 7); t >= 1; t--)
            {
                int id = TrySpellByName($"{baseName} {GetRomanNumeral(t)}");
                if (id != 0) { tier = t; return id; }
            }
        }
        return 0;
    }

    private int TrySpellByName(string name)
    {
        if (_spellManager == null) return 0;
        if (_spellManager.SpellDictionary.TryGetValue(name, out int id))
        {
            if (_playerId != 0 && _host.HasIsSpellKnown)
            {
                _host.IsSpellKnown(_playerId, unchecked((uint)id), out bool known);
                if (known) return id;
            }
            else if (SpellDatabase.IsSpellKnown(id)) return id;
        }
        return 0;
    }

    private int CountMonstersInRange(double rangeYards)
    {
        if (_playerId == 0) return 0;
        int pid = (int)_playerId;
        int count = 0;
        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.ObjectClass != AcObjectClass.Monster) continue;
            float hp = _worldFilter.GetHealthRatio(wo.Id);
            if (hp == 0f || hp < 0f) continue;
            if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id)) continue;
            if (_worldFilter.Distance(pid, wo.Id) <= rangeYards)
                count++;
        }
        return count;
    }

    private int FindBestShapedSpell(string element, MonsterRule? rule) =>
        FindBestShapedSpell(element, rule, out _);

    private int FindBestShapedSpell(string element, MonsterRule? rule, out bool isRing)
    {
        isRing = false;
        if (_spellManager == null) return 0;

        bool useVoid = element.Equals("Nether", StringComparison.OrdinalIgnoreCase);
        bool warTrained  = _charSkills == null || _charSkills[AcSkillType.WarMagic].Training >= 2;
        bool voidTrained = _charSkills == null || _charSkills[AcSkillType.VoidMagic].Training >= 2;

        if (useVoid && !voidTrained && warTrained)  useVoid = false;
        if (!useVoid && !warTrained && voidTrained) useVoid = true;

        AcSkillType skill = useVoid ? AcSkillType.VoidMagic : AcSkillType.WarMagic;

        int shapeIdx = 3; // default = Bolt
        if (rule != null)
        {
            if (rule.UseArc)         shapeIdx = 0;
            else if (rule.UseStreak) shapeIdx = 2;
            else if (rule.UseBolt)   shapeIdx = 3;
            else if (!rule.UseRing)  return 0; // no shape enabled at all
        }

        // Ring override: when UseRing is enabled, check if enough monsters are within
        // ring range. If so, upgrade to ring; otherwise keep the base shape (bolt/arc/streak).
        if (rule != null && rule.UseRing && _settings.RingRange > 0)
        {
            int nearbyCount = CountMonstersInRange(_settings.RingRange);
            if (nearbyCount >= Math.Max(1, _settings.MinRingTargets))
                shapeIdx = 1; // ring
        }

        var shapes = useVoid ? VoidSpellShapes : SpellShapes;
        if (!shapes.TryGetValue(element, out string[]? elementShapes))
        {
            if (useVoid)
            {
                // Void Magic only damages with Nether — fall back to Nether shapes for
                // ANY element not in VoidSpellShapes (Cold/Lightning/Acid/Blade/Pierce/
                // Bludgeon/Slash). Previously this fell through to War Magic Fire, which
                // forced War on Void-only casters whose War skill was untrained.
                if (!VoidSpellShapes.TryGetValue("Nether", out elementShapes)) return 0;
            }
            else
            {
                if (!SpellShapes.TryGetValue("Fire", out elementShapes)) return 0;
                skill = AcSkillType.WarMagic;
            }
        }

        if (shapeIdx >= elementShapes.Length) shapeIdx = elementShapes.Length - 1;

        if (shapeIdx == 1)
        {
            int ringId = FindBestRingSpell(element, skill);
            if (ringId != 0) { isRing = true; return ringId; }
        }

        int id = FindBestOffensiveSpellId(elementShapes[shapeIdx], skill);
        if (id != 0) return id;

        for (int i = elementShapes.Length - 1; i >= 0; i--)
        {
            if (i == shapeIdx) continue;
            id = FindBestOffensiveSpellId(elementShapes[i], skill);
            if (id != 0) return id;
        }
        return 0;
    }

    private int FindBestRingSpell(string element, AcSkillType skill)
    {
        // Void Magic doesn't have lore-named rings (Cassius'/Halo/etc. are all War
        // Magic spells). Returning 0 here makes FindBestShapedSpell fall through to
        // FindBestOffensiveSpellId(elementShapes[1], skill) where elementShapes comes
        // from VoidSpellShapes — e.g. "Nether Ring" or "Corrosion Ring" — and the
        // Void caster gets the right Void ring tier instead of an unknown War spell.
        if (skill == AcSkillType.VoidMagic)
            return 0;

        if (!RingLoreNames.TryGetValue(element, out string[]? loreNames) || loreNames.Length < 2) return 0;

        int maxTier = _spellManager!.GetHighestSpellTier(skill);

        if (maxTier >= 7)
        {
            int id = TrySpellByName(loreNames[1]); if (id != 0) return id;
            id = TrySpellByName(loreNames[1].Replace("'", "\u2019")); if (id != 0) return id;
            id = TrySpellByName(loreNames[1].Replace("'", "`")); if (id != 0) return id;
        }
        if (maxTier >= 6)
        {
            int id = TrySpellByName(loreNames[0]); if (id != 0) return id;
            id = TrySpellByName(loreNames[0].Replace("'", "\u2019")); if (id != 0) return id;
            id = TrySpellByName(loreNames[0].Replace("'", "`")); if (id != 0) return id;
        }
        if (maxTier >= 8)
        {
            int id = TrySpellByName($"Incantation of {loreNames[0]}"); if (id != 0) return id;
        }

        if (SpellShapes.TryGetValue(element, out string[]? genericRingBases) && genericRingBases.Length > 1)
        {
            string ringBase = genericRingBases[1];
            for (int t = Math.Min(maxTier, 5); t >= 1; t--)
            {
                int id = TrySpellByName($"{ringBase} {GetRomanNumeral(t)}"); if (id != 0) return id;
            }
        }

        _host.WriteToChat($"[RynthAi] Ring spell not found for {element} (tried: {loreNames[1]}, {loreNames[0]})", 2);
        return 0;
    }

    private int FindBestOffensiveSpellId(string baseName, AcSkillType skill)
    {
        if (_spellManager == null) return 0;
        int maxTier = _spellManager.GetHighestSpellTier(skill);

        if (maxTier >= 8)
        {
            int id = TrySpellByName($"Incantation of {baseName}"); if (id != 0) return id;
            id = TrySpellByName(baseName + " VIII"); if (id != 0) return id;
        }

        for (int tier = Math.Min(maxTier, 7); tier >= 1; tier--)
        {
            int id = TrySpellByName($"{baseName} {GetRomanNumeral(tier)}"); if (id != 0) return id;
        }
        return 0;
    }

    private static string GetRomanNumeral(int tier) => tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV",
        5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        _ => "I"
    };

    public void Dispose() => _raycastSystem?.Dispose();
}

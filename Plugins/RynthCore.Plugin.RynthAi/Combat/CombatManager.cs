using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;
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

    public int activeTargetId
    {
        get => _activeTargetId;
        set { _activeTargetId = value; if (value == 0) _nativeAttackTargetId = 0; }
    }
    private int _activeTargetId;
    private DateTime lastAttackCmd = DateTime.MinValue;
    private DateTime lastStanceAttempt = DateTime.MinValue;
    private DateTime _lastPeaceAttempt = DateTime.MinValue;
    private DateTime lastTargetSearchTime = DateTime.MinValue;
    private const int TARGET_SEARCH_INTERVAL_MS = 150;

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

    private DateTime _lastSpellCast = DateTime.MinValue;
    private const double SPELL_CAST_COOLDOWN_MS = 1500.0;
    private const double ATTACK_SPELL_COOLDOWN_MS = 100.0;

    private bool _returnToPhysicalCombat = false;
    private int _savedWeaponId = 0;

    /// <summary>Current combat mode — updated from OnCombatModeChange in RynthAiPlugin.</summary>
    public int CurrentCombatMode { get; set; } = CombatMode.NonCombat;

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

    private readonly Dictionary<int, BlacklistedTarget> blacklistedTargets = new();

    private readonly BlacklistManager _blacklistManager = new();
    private int _lastAttackedTargetId = 0;
    private int _consecutiveMisses = 0;

    public int RaycastBlockCount { get; private set; }
    public int RaycastCheckCount { get; private set; }

    // ── Monster scanner ──────────────────────────────────────────────────
    // Pre-scans nearby creatures for distance, IsAttackable, and LOS.
    // Combat picks from this list — no SelectItem needed until attack time.
    private readonly List<ScannedTarget> _scannedTargets = new();
    private DateTime _lastScanTime = DateTime.MinValue;
    private const int SCAN_INTERVAL_MS = 500;

    public struct ScannedTarget
    {
        public int Id;
        public double Distance;
        public string Name;
    }

    /// <summary>Sorted list of nearby, attackable, LOS-clear creatures. Updated every 500ms.</summary>
    public IReadOnlyList<ScannedTarget> ScannedTargets => _scannedTargets;

    /// <summary>True when combat has an active target or viable targets nearby. Used to block navigation.</summary>
    public bool HasTargets => activeTargetId != 0 || _scannedTargets.Count > 0;

    /// <summary>True only when actively attacking a specific target. Unlike HasTargets, this is false between kills even when more monsters are scanned nearby.</summary>
    public bool IsActivelyEngaged => activeTargetId != 0;

    /// <summary>
    /// Periodic scan of all creatures. Filters: distance, self, blacklist, IsAttackable, raycast LOS.
    /// Call from OnHeartbeat or Think.
    /// </summary>
    public void ScanNearbyTargets()
    {
        if ((DateTime.Now - _lastScanTime).TotalMilliseconds < SCAN_INTERVAL_MS)
            return;
        _lastScanTime = DateTime.Now;

        _scannedTargets.Clear();
        int playerId = (int)_playerId;
        if (playerId == 0) return;

        double maxDist = _settings.MonsterRange;
        TargetingFSM.AttackType attackType = TargetingFSM.AttackType.Linear;
        if (RaycastInitialized && _raycastSystem?.TargetingFSM != null)
            attackType = _raycastSystem.GetAttackType(CurrentCombatMode, "");

        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.Id == playerId) continue;
            if ((int)wo.ObjectClass != (int)AcObjectClass.Monster) continue;
            if (blacklistedTargets.ContainsKey(wo.Id) || _blacklistManager.IsBlacklisted(wo.Id)) continue;

            if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id))
                continue;

            // Dead but not yet reclassified as corpse — skip
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f)
                continue;

            double dist = _worldFilter.Distance(playerId, wo.Id);
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
            if (!losBlocked)
                _scannedTargets.Add(new ScannedTarget { Id = wo.Id, Distance = dist, Name = wo.Name });
        }

        _scannedTargets.Sort((a, b) => a.Distance.CompareTo(b.Distance));
    }

    private class BlacklistedTarget
    {
        public int TargetId { get; set; }
        public DateTime BlacklistedTime { get; set; }
        public bool IsLosBlocked { get; set; }
        public bool IsExpired()
        {
            double duration = IsLosBlocked ? 5000 : 20000;
            return (DateTime.Now - BlacklistedTime).TotalMilliseconds > duration;
        }
    }

    public CombatManager(RynthCoreHost host, LegacyUiSettings settings, WorldObjectCache worldFilter, SpellManager? spellManager = null)
    {
        _host = host;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _worldFilter = worldFilter ?? throw new ArgumentNullException(nameof(worldFilter));
        _spellManager = spellManager;
    }

    public void SetSpellManager(SpellManager spellManager) => _spellManager = spellManager;
    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;
    public void SetPlayerId(uint playerId) => _playerId = playerId;
    public void SetRaycastSystem(MainLogic raycast)
    {
        _raycastSystem = raycast;
        RaycastInitialized = raycast?.IsInitialized ?? false;
    }

    private uint _playerId;

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
        { "Pierce",    new[] { "Nuhumudira's Spines", "Nuhumudira's Spines II" } },
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

        if (text.StartsWith("You cast ", StringComparison.OrdinalIgnoreCase))
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
    }

    private void TrackAttackAttempt(int targetId)
    {
        if (_lastAttackedTargetId != targetId)
        {
            // Switched targets — reset miss counter
            _lastAttackedTargetId = targetId;
            _consecutiveMisses = 0;
            return;
        }

        _consecutiveMisses++;
        if (_consecutiveMisses >= _settings.BlacklistAttempts)
        {
            _blacklistManager.ReportFailure(targetId);
            if (_blacklistManager.IsBlacklisted(targetId))
            {
                _host.WriteToChat($"[RynthAi] No damage feedback after {_consecutiveMisses} attacks — blacklisting 0x{(uint)targetId:X8}", 2);
                _consecutiveMisses = 0;
                activeTargetId = 0;
                _lockedTargetId = 0;
            }
        }
    }

    public bool Think()
    {
        if (!_settings.EnableCombat) return false;

        double acDistanceLimit = _settings.MonsterRange;
        try { CleanupExpiredBlacklist(); } catch (Exception ex) { _host.Log($"[RynthAi] CleanupExpiredBlacklist crashed: {ex.Message}"); }

        _blacklistManager.AttemptThreshold = 1; // one report = blacklist; count is controlled by TrackAttackAttempt
        _blacklistManager.TimeoutSeconds   = _settings.BlacklistTimeoutSec;

        if (_raycastSystem?.TargetingFSM != null)
            _raycastSystem.TargetingFSM.MaxScanDistanceMeters = _settings.MonsterRange + 40.0f;

        // Update the pre-scanned target list (distance, attackable, LOS — no selection)
        try { ScanNearbyTargets(); } catch (Exception ex) { _host.Log($"[RynthAi] ScanNearbyTargets crashed: {ex.Message}"); }

        // Validate current target — restore from lock first so transient world-filter nulls
        // don't cause HandleCombatTrigger to pick a different mob on the same tick.
        if (_lockedTargetId != 0 && activeTargetId == 0)
            activeTargetId = _lockedTargetId;

        if (activeTargetId != 0)
        {
            var target = _worldFilter[activeTargetId];
            bool blacklisted = blacklistedTargets.ContainsKey(activeTargetId) || _blacklistManager.IsBlacklisted(activeTargetId);

            if (blacklisted)
            {
                // Hard drop — mob is on the blacklist.
                activeTargetId = 0;
                _lockedTargetId = 0;
                _facingTarget = false;
                ClearCombatTurnMotions();
            }
            else if (target != null && (int)target.ObjectClass != (int)AcObjectClass.Monster)
            {
                // Confirmed no longer a creature (became a corpse, etc.) — release lock.
                activeTargetId = 0;
                _lockedTargetId = 0;
                _facingTarget = false;
                ClearCombatTurnMotions();
            }
            else if (_worldFilter.GetHealthRatio(activeTargetId) == 0f)
            {
                // Health hit zero — dead, move on immediately instead of waiting
                // for the object class to change to corpse.
                activeTargetId = 0;
                _lockedTargetId = 0;
                _facingTarget = false;
                ClearCombatTurnMotions();
            }
            else if (target != null &&
                     _worldFilter.Distance(_host.GetPlayerId() == 0 ? 0 : (int)_host.GetPlayerId(), activeTargetId) > acDistanceLimit)
            {
                // Out of configured range — release lock immediately.
                // Previous 1.5× buffer caused the bot to run toward distant mobs.
                activeTargetId = 0;
                _lockedTargetId = 0;
                _facingTarget = false;
                ClearCombatTurnMotions();
            }
            // If target == null here it's a transient world-filter miss — keep the lock,
            // the stillScanned grace period below will handle a truly dead/vanished mob.
        }

        if (activeTargetId == 0)
        {
            if ((DateTime.Now - lastTargetSearchTime).TotalMilliseconds > TARGET_SEARCH_INTERVAL_MS)
            {
                HandleCombatTrigger();
                lastTargetSearchTime = DateTime.Now;
            }

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
                return true; // No target found — nothing to do
            }
            // HandleCombatTrigger found a target — fall through to combat logic
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
            {
                // Still not in scan after grace period — truly gone (died, moved away, permLOS)
                activeTargetId = 0;
                _lockedTargetId = 0;
                _facingTarget = false;
                ClearCombatTurnMotions();
                _targetLostScanTime = DateTime.MinValue;
            }
            return true;
        }
        _targetLostScanTime = DateTime.MinValue; // back in scan — reset grace timer

        var targetObj = _worldFilter[activeTargetId];
        if (targetObj == null) return true; // transient miss — skip tick, keep lock

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

        if ((DateTime.Now - lastAttackCmd).TotalMilliseconds >= 1000)
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

            TrackAttackAttempt(activeTargetId);
            lastAttackCmd = DateTime.Now;
        }
        return true;
    }

    public void OnHeartbeat()
    {
        if (!_settings.IsMacroRunning) return;

        // Always run the scan and BotAction state update, even when combat can't
        // take actions. ScanNearbyTargets has no side-effects (no game commands)
        // and must stay fresh so HasTargets is accurate for nav-blocking decisions.
        // Without this, stale scan data keeps combatBlocking = true after a kill,
        // preventing navigation from resuming while the bot is buffing, etc.
        if (_settings.EnableCombat)
        {
            try { ScanNearbyTargets(); }
            catch (Exception ex) { _host.Log($"[RynthAi] ScanNearbyTargets CRASH: {ex.Message}"); }

            if (activeTargetId != 0 || _scannedTargets.Count > 0)
                _settings.BotAction = "Combat";
            else if (_settings.BotAction == "Combat")
                _settings.BotAction = "Default";
        }

        // Combat can run in Default/Combat, can interrupt navigation unless nav boost is on,
        // and can interrupt looting unless loot boost is on.
        bool canRun = _settings.BotAction == "Default"
                   || _settings.BotAction == "Combat"
                   || (_settings.BotAction == "Navigating" && !_settings.BoostNavPriority)
                   || (_settings.BotAction == "Looting" && !_settings.BoostLootPriority);
        if (!canRun) return;

        if (_settings.EnableCombat)
        {
            try { Think(); }
            catch (Exception ex) { _host.Log($"[RynthAi] Think CRASH: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }

            // Think() already called ScanNearbyTargets internally — update BotAction
            // again with the post-Think scan results (target selection may have changed).
            if (activeTargetId != 0 || _scannedTargets.Count > 0)
                _settings.BotAction = "Combat";
            else if (_settings.BotAction == "Combat")
                _settings.BotAction = "Default";
        }
    }

    public void HandleCombatTrigger()
    {
        // Never override an existing lock — only pick a new target when the previous one
        // has been fully released (both activeTargetId and _lockedTargetId are 0).
        if (_lockedTargetId != 0) return;

        // Pick the best target from the pre-scanned list (already filtered + LOS-checked)
        if (_scannedTargets.Count == 0) return;

        int targetId = _scannedTargets[0].Id;
        if (targetId != 0 && targetId != activeTargetId)
        {
            activeTargetId = targetId;
            _lockedTargetId = targetId;
            // Override the client's auto-target — but only when the client isn't busy.
            // Queuing SelectItem while a combat-mode transition is in progress can
            // corrupt the client's action queue and permanently freeze the cursor.
            if (BusyCount == 0)
                _host.SelectItem((uint)targetId);
        }
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
        foreach (var item in _worldFilter.GetDirectInventory())
        {
            if (item.WieldedLocation <= 0) continue;
            if (playerId != 0 && item.Wielder != 0 && item.Wielder != playerId) continue;
            string n = item.Name;
            if (string.IsNullOrEmpty(n)) continue;
            if (n.Contains("Bundle") || n.Contains("Wrapped")) continue;
            if (n.Contains("Arrow") || n.Contains("Quarrel") || n.Contains("Bolt") || n.Contains("Dart"))
                return true;
        }
        return false;
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
        var rule = _settings.MonsterRules.FirstOrDefault(
            r => !r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                 target.Name.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0);
        if (rule != null) return rule;
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
        if ((DateTime.Now - _lastSpellCast).TotalMilliseconds < SPELL_CAST_COOLDOWN_MS) return;

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
        int offensiveSpellId = FindBestShapedSpell(element, rule);
        if (offensiveSpellId != 0)
        {
            try
            {
                _host.CastSpell((uint)activeTargetId, offensiveSpellId);
                _lastSpellCast = DateTime.Now;
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
            if (wo != null && wo.ObjectClass == AcObjectClass.WandStaffOrb) return item.Id;
        }
        // Fall back to inventory cache scan
        foreach (var wo in _worldFilter.GetInventory())
        {
            if (wo.ObjectClass == AcObjectClass.WandStaffOrb) return wo.Id;
        }
        return 0;
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
        // TODO: Implement when GetLandscape() is available
        return 0; // STUB
    }

    private int FindBestShapedSpell(string element, MonsterRule? rule)
    {
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
            if (rule.UseArc)    shapeIdx = 0;
            else if (rule.UseRing)   shapeIdx = 1;
            else if (rule.UseStreak) shapeIdx = 2;
            else if (rule.UseBolt)   shapeIdx = 3;
            else return 0;
        }

        if (rule != null && rule.UseRing && _settings.MinRingTargets > 0 && _settings.RingRange > 0)
        {
            int nearbyCount = CountMonstersInRange(_settings.RingRange);
            if (nearbyCount >= _settings.MinRingTargets) shapeIdx = 1;
        }

        var shapes = useVoid ? VoidSpellShapes : SpellShapes;
        if (!shapes.TryGetValue(element, out string[]? elementShapes))
        {
            if (!SpellShapes.TryGetValue("Fire", out elementShapes)) return 0;
            skill = AcSkillType.WarMagic;
        }

        if (shapeIdx >= elementShapes.Length) shapeIdx = elementShapes.Length - 1;

        if (shapeIdx == 1)
        {
            int ringId = FindBestRingSpell(element, skill);
            if (ringId != 0) return ringId;
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

    private void CleanupExpiredBlacklist()
    {
        var expired = new List<int>();
        foreach (var kvp in blacklistedTargets) if (kvp.Value.IsExpired()) expired.Add(kvp.Key);
        foreach (var id in expired) blacklistedTargets.Remove(id);
    }

    public void Dispose() => _raycastSystem?.Dispose();
}

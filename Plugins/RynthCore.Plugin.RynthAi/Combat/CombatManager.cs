using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.Combat;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager : IDisposable
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
    // Exponential backoff for stance flips. A persistent wedge used to re-send
    // ChangeCombatMode every second forever — the 2026-05-25 stance-spam crash
    // class (256 consecutive sends before the client died); BuffManager got
    // backoff then, combat never did. Delay doubles every 4 failed sends,
    // capped at 15s, with a one-shot warning once clearly wedged. Reset
    // wherever the stance is reached or the target drops.
    private int _stanceFlipAttempts;
    private bool _stanceFlipWarned;

    private double StanceRetryDelayMs()
        => Math.Min(15_000, 1000 * Math.Pow(2, Math.Min(4, _stanceFlipAttempts / 4)));

    private void NoteStanceFlipSent()
    {
        _stanceFlipAttempts++;
        if (_stanceFlipAttempts == 16 && !_stanceFlipWarned)
        {
            _stanceFlipWarned = true;
            _host.Log($"[EquipDiag] WARNING: {_stanceFlipAttempts} consecutive ChangeCombatMode sends without reaching the stance — wedge persists despite recovery; backing off to {StanceRetryDelayMs() / 1000:0}s retries.");
        }
    }

    private void ResetStanceFlipBackoff()
    {
        _stanceFlipAttempts = 0;
        _stanceFlipWarned = false;
    }
    // Stuck-stance deadlock recovery (2026-06-10). When the wand reads as wielded
    // client-side but AC refuses to complete the NonCombat→Magic stance change, the
    // equip path would only re-send ChangeCombatMode forever (it never re-equips a
    // "wielded" wand) — a hours-long wedge with NO m_cBusy pin so no watchdog fires.
    private DateTime _stanceStuckSince   = DateTime.MinValue;
    private long     _stanceStuckStartMs;      // ActionPacer.NowMs when the clock started (a later engine refusal restarts it)
    private DateTime _lastStanceRecoverAt = DateTime.MinValue;
    private const double StanceStuckRecoverMs = 8000;
    // Re-equip attempts spent in the CURRENT deadlock episode. ⚠ UseObject on an
    // ALREADY-WIELDED wand is treated by AC as an unwield/MOVE — spamming it
    // every 8s for ~17 min (2026-06-11) jammed AC's item-action queue into a
    // permanent "you can only move one item at a time" state (character
    // unusable, no recovery but relog). The re-equip only helps the rare
    // stale-wield read, so it's capped; past the cap the recovery is
    // mode-change-only and warns once.
    private int _stanceReEquipAttempts;
    private const int StanceReEquipMaxAttempts = 2;

    // ── Combat wand wield gate ───────────────────────────────────────────────
    // The wand-equip path used to re-issue UseObject every 2s forever with no
    // confirmation step and no cap. That is the exact pattern both this file and
    // BuffManager already warn about: "UseObject on a genuinely wielded wand is a
    // MOVE to AC, and unbounded re-equip jams the item queue." Hammering it does
    // not just fail to help — it keeps the item-action queue jammed, so the wield
    // that would have succeeded never lands. Observed 2026-09-02: 47 UseObject
    // sends over 10 minutes, hands empty the whole time, combat never engaging.
    //
    // It went unnoticed because BuffManager.EnsureMagicMode also wields a wand and
    // puts the character in Magic mode; while the rebuff loop was running
    // constantly it re-established the stance every cycle and combat rode along on
    // it. Fixing the buff loop removed that accidental cover and exposed this.
    //
    // Same gate as BuffManager: issue ONE UseObject, wait for the wield to
    // confirm, cool down, and cap the attempts — so a wand that genuinely cannot
    // be wielded degrades loudly instead of wedging the queue silently.
    private int      _wandPendingWieldId;
    private DateTime _wandPendingWieldAt    = DateTime.MinValue;
    private DateTime _wandWieldCooldownUntil = DateTime.MinValue;
    private int      _wandWieldFailCount;
    private bool     _wandWieldWedgeWarned;
    private const double WandWieldResolveTimeoutMs = 2500;   // mirrors BuffManager.WieldResolveTimeoutMs
    private const double WandWieldCooldownMs       = 5000;   // mirrors BuffManager.WieldCooldownMs
    private const int    WandWieldFailMax          = 3;      // mirrors BuffManager.WieldGateFailMax
    private bool _stanceWedgeWarned;
    private DateTime _lastPeaceAttempt = DateTime.MinValue;
    private int _idlePeaceAttempts;   // idle peace requests since peace was last seen (backoff)

    // ── Bow→wand dequip-first swap state (2026-06-27) ────────────────────
    // Stock ACE will NOT auto-dequip a main-hand bow for a Held-slot wand
    // (CheckWeaponCollision refuses while mainhand != null), and it DENIES
    // ChangeCombatMode(Magic) while the bow is wielded ("GetEquippedWand()==null").
    // Combat must stow the bow into an open pack FIRST, then UseObject(wand), and
    // only request Magic once the wand is actually wielded (handled by the
    // alreadyWielded branch). Mirrors the proven BuffManager.EnsureMagicMode path.
    // Switch like a player (2026-10-06, see BuffManager._directWandSwapFailed): the wand's use
    // puts the weapon in hand away by itself; the pack move first is the fallback once a use
    // hasn't done it in DirectWandSwapFallbackMs.
    private bool _combatDirectWandSwapFailed;
    private DateTime _combatDirectSwapSince = DateTime.MinValue;
    private const double DirectWandSwapFallbackMs = 2000;   // SwapTracker retries at 2500: that retry is the pack move
    private int _combatBowDequipPendingId;
    private int _combatBowDequipAttempts;
    private DateTime _combatBowDequipAt = DateTime.MinValue;
    private bool _combatSwapTeardownDone;
    private const int CombatBowDequipMaxAttempts = 3;
    private const double WandSwapWieldResolveMs = 4000;

    // Clear all stance-deadlock episode state. Called wherever the stance is
    // reached or the target drops, so the next episode starts fresh.
    private void ResetStanceRecovery()
    {
        _stanceStuckSince = DateTime.MinValue;
        _stanceReEquipAttempts = 0;
        _stanceWedgeWarned = false;
        ResetWandSwapState();
        ResetStanceFlipBackoff();
    }

    // Clear the bow→wand swap episode (called once the desired stance is reached).
    private void ResetCombatWandWieldGate()
    {
        _wandPendingWieldId     = 0;
        _wandPendingWieldAt     = DateTime.MinValue;
        _wandWieldCooldownUntil = DateTime.MinValue;
        _wandWieldFailCount     = 0;
        _wandWieldWedgeWarned   = false;
    }

    private void ResetWandSwapState()
    {
        _combatBowDequipPendingId = 0;
        _combatBowDequipAttempts = 0;
        _combatSwapTeardownDone = false;
        _combatDirectSwapSince = DateTime.MinValue;
    }

    // ── Face-before-attack state ─────────────────────────────────────────
    // For ranged/magic attacks, face the target with smooth turn before firing.
    // ── Native attack state ──────────────────────────────────────
    // StartAttackRequest auto-repeats — only call once per target.
    private uint _nativeAttackTargetId;
    // Native attack wedge (2026-11-06, see NativeForThisAttack): the client's own attack state can
    // be left stuck by a stance change mid-fight, and then every native attack sends nothing.
    private bool     _nativeResetPending;                          // the next swing goes out as the regular attack
    private DateTime _lastNativeAttackAt = DateTime.MinValue;
    private DateTime _nativeQuietSince   = DateTime.MinValue;      // first native attack since the last hit, miss or kill
    private int      _nativeQuietAttacks;
    private int      _nativeFallbackTargetId;                      // regular attacks until the target changes
    private bool     _nativeStanceSeen = true;                     // last tick was in Melee/Missile (WatchNativeStance)
    internal const double NativeResetWindowMs  = 5000;   // a native attack this recent when the stance left = mid-fight
    internal const double NativeFallbackMs     = 10_000; // native attacks with no hit, miss or kill this long = fall back
    internal const int    NativeFallbackAttacks = 4;     // ... and at least this many of them
    internal const double NativeQuietGapMs     = 5000;   // no native attack this long: the quiet clock starts over

    private bool _facingTarget;
    // True only on attack cycles where an OFFENSIVE damage spell was actually
    // cast. The blacklist miss-counter is gated on this in magic mode so it
    // counts confirmed casts, not wall-clock interval ticks (equip waits,
    // cast-gate, "no spell found", tier-down learning) which used to blacklist
    // a perfectly good target in ~3 ticks (~1.2s at SpellCastIntervalMs=400).
    private bool _offensiveCastThisCycle;
    private DateTime _faceStartTime = DateTime.MinValue;
    private const double FACE_TIMEOUT_MS = 1000.0; // give up waiting and swing anyway (non-native MELEE only)
    private const double FACE_TOLERANCE_DEG = 15.0; // heading error threshold to fire

    // ── Magic targeted-cast settle gate (root-cause fix 2026-06-19) ──────────
    // On the ACE server a TARGETED (combat) cast defers its windup behind a
    // turn-to-face; a turn/stop MoveToState that lands during that deferred
    // windup ORPHANS the cast (DoSpellWords/CreatePlayerSpell never runs) → the
    // gesture animates but 0 damage / "You're too busy!" forever. Self-buffs are
    // immune (untargeted → synchronous, no turn). The 2026-06-19 fix turned the
    // bot itself until within angle, then released and settled before the cast;
    // since 2026-09-27 the bot doesn't turn for magic at all (the server's own
    // Rotate does it), and this only settles after releasing a turn melee left held.
    private DateTime _faceSettledAt = DateTime.MinValue;
    private const double FACE_SETTLE_MS   = 140.0;   // let the turn-stop reach the server (≥ one 30Hz tick) before the cast packet

    // ── Wall facing (2026-10-08) ─────────────────────────────────────────────
    // A wall is untargeted: ACE launches it along the caster's facing and never turns the caster
    // for it (see CountMonstersInWallPath). So before a wall the bot turns the character itself:
    // an instant heading snap (TurnToHeading) when it is more than WallFaceToleranceDeg off the
    // target, then no cast until WallFaceSettleMs later so the server has the new heading. The
    // snap changes only the client's heading; the client tells the server in its next position
    // event, which goes out at most once per second while the position or heading keeps
    // changing (CommandInterpreter::ShouldSendPositionEvent 0x006B5520; the 1.0 s interval is set
    // in the constructor 0x006B4D80), so the settle covers a full second plus a frame. Only ever
    // inside AttackWithMagic, i.e. after the cadence guard and the gesture gate: never while a
    // cast is resolving, and nothing turns during the wall's windup. Targeted shapes are unchanged.
    // Always face the target exactly before a wall (owner, 2026-10-08: "for walls he always has to
    // turn to the target"): a wall flies straight, so a few degrees off misses at range. Only a
    // facing within WallFaceToleranceDeg casts without a turn.
    internal const double WallFaceToleranceDeg = 1.0;
    internal const double WallFaceSettleMs     = 1100.0;
    // After the settle the wall goes if the target is still within this of the snapped heading;
    // further (it moved during the second) the bot turns again, at most WallFaceMaxSnaps times
    // per wall, then casts anyway rather than chase a running monster forever.
    internal const double WallFaceDriftDeg     = 5.0;   // the most allowed (close in)
    internal const int    WallFaceMaxSnaps     = 3;
    // How far to the side of the wall's centre line the target may have moved during the settle
    // (metres): at 70 yd, 5 degrees is 6 m off - wider than half the wall (owner, 2026-10-08).
    internal const double WallAimSlackMeters   = 1.8;

    /// <summary>The drift allowed after the settle for a target this far away: the angle that puts
    /// it <see cref="WallAimSlackMeters"/> to the side, between the tolerance and WallFaceDriftDeg.</summary>
    internal static double WallDriftAllowedDeg(double distanceMeters)
    {
        if (distanceMeters <= 0.1) return WallFaceDriftDeg;
        double deg = Math.Atan2(WallAimSlackMeters, distanceMeters) * 180.0 / Math.PI;
        return Math.Clamp(deg, WallFaceToleranceDeg, WallFaceDriftDeg);
    }

    private double WallDriftAllowedDeg(int targetId)
    {
        try
        {
            if (_host.TryGetPlayerPose(out _, out float px, out float py, out _, out _, out _, out _, out _)
                && _host.TryGetObjectPosition((uint)targetId, out _, out float tx, out float ty, out _))
                return WallDriftAllowedDeg(Math.Sqrt((tx - px) * (tx - px) + (ty - py) * (ty - py)));
        }
        catch { }
        return WallFaceToleranceDeg;
    }
    private int _wallFaceSnaps;
    private int _wallFaceTarget;
    private DateTime _wallFaceAt = DateTime.MinValue;
    // Set by AttackWithMagic when it held a wall for its turn: Think then ends the tick without
    // counting it as an attack (no attack interval starts, so the wall goes once settled).
    private bool _wallFaceHeld;
    /// <summary>Tests: the clock the wall facing reads.</summary>
    internal Func<DateTime> WallClock { get; set; } = () => RynthClock.Now;

    // ── Magic cast cadence guard ────────────────────────────────────────────
    // Don't issue the next combat cast until the previous one's server windup
    // has resolved (a UseDone arrived, or a hard timeout). Re-casting into the
    // window makes the next cast's stop-thunk/turn orphan the prior cast.
    private bool _awaitingCastResolution;
    private DateTime _castResolutionDeadline = DateTime.MinValue;
    private int _useDoneSeqAtCast;
    private const double CAST_RESOLUTION_TIMEOUT_MS = 2500.0; // covers a tier-7/8 war windup+recoil; also the sole gate on an engine without UseDone observation

    // Smooth turn motions — same codes as NavigationEngine
    private const uint MotionTurnRight = 0x6500000D;
    private const uint MotionTurnLeft  = 0x6500000E;

    // Grace period: keep activeTargetId alive for this long after it disappears from scan,
    // so a single bad LOS result or scan gap doesn't hand control to navigation.
    private DateTime _targetLostScanTime = DateTime.MinValue;
    private const double TARGET_SCAN_GRACE_MS = 1500.0;

    // The locked target (see HandleCombatTrigger: with Target Lock on it is held until it dies,
    // leaves Monster Range or a higher-priority monster shows up). Prevents spinning caused by
    // target thrashing when the world filter has a transient null.
    private int _lockedTargetId = 0;

    private bool _wasMacroRunning;

    private DateTime _lastSpellCast = DateTime.MinValue;
    // Chat throttles for "No spell found" and "Ring spell not found" (per element).
    private DateTime _lastNoSpellChatAt = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _ringMissingChatAt = new(StringComparer.OrdinalIgnoreCase);
    private const double NoSpellChatIntervalMs = 30_000;
    private bool _lastCastWasRing = false;   // the last attack hit an area: a ring, or a wall (2026-10-08)
    private const double ATTACK_SPELL_COOLDOWN_MS = 100.0;

    private bool _returnToPhysicalCombat = false;
    private int _savedWeaponId = 0;
    private bool _physicalDebuffSkipWarned;   // once per session: melee/missile debuffs skipped

    // Shared cross-subsystem weapon-swap serializer (set by RynthAiPlugin).
    // Prevents combat weapon equips from racing the buff wand-equip, and also
    // serializes CombatManager's own re-equip + EquipWeaponAndSetStance so two
    // equips can't fire in one tick.
    private WeaponSwapGate? _weaponSwapGate;
    public void SetWeaponSwapGate(WeaponSwapGate gate) => _weaponSwapGate = gate;

    /// <summary>
    /// True = hold the idle peace-mode swap (PeaceModeWhenIdle) for now. RynthAi answers true while
    /// looting has work: on ACE a combat-mode change cancels the server's walk/turn to a corpse
    /// being opened (2026-10-06). Null = never hold.
    /// </summary>
    public Func<bool>? HoldIdlePeace { get; set; }
    private bool _idlePeaceHeldLogged;

    /// <summary>
    /// True = don't lock a new target yet. RynthAi answers true with Boost Loot Priority on while
    /// our kill's corpse forms or is looted (RynthAiPlugin.BoostLootHoldsNewTarget): one kill, one
    /// loot. A monster within CloseAttackYards is still fought at once. Null = never hold.
    /// </summary>
    public Func<bool>? HoldNewTarget { get; set; }
    /// <summary>Why HoldNewTarget holds, for its one log line (null: Boost Loot Priority's words).</summary>
    public Func<string>? HoldNewTargetWhy { get; set; }
    private bool _newTargetHeldLogged;

    /// <summary>
    /// RynthAi's mana reserve (BuffManager.OffensiveCastHold): given the attack or debuff spell
    /// about to be cast, a reason to hold it (mana under its cost, or under the reserve kept for
    /// Stamina to Mana), or null to cast. Null hook = never hold (2026-10-11).
    /// </summary>
    public Func<int, string?>? ManaReserveHold { get; set; }
    /// <summary>
    /// BuffManager.CanEverAfford, for the script hook: false when a spell costs more than max mana,
    /// so ManaReserveHold would hold it forever. Null hook = always affordable.
    /// </summary>
    public Func<int, bool>? ManaCanEverAfford { get; set; }
    private bool _manaCastHeld;        // AttackWithMagic held its cast for mana this tick
    private bool _manaHoldLogged;

    // The kind of weapon EquipWeaponAndSetStance last chose (a CombatMode), -1 before it chose.
    private int _chosenWeaponMode = -1;

    /// <summary>Combat fights with magic: its chosen weapon is a wand (before it chose, the stance).
    /// BuffManager's mana reserve holds only a caster's attacks.</summary>
    public bool FightsWithMagic => _chosenWeaponMode >= 0
        ? _chosenWeaponMode == CombatMode.Magic
        : CurrentCombatMode == CombatMode.Magic;

    /// <summary>True (and logged once per hold) when the mana reserve holds this cast.</summary>
    private bool ManaHoldsCast(int spellId)
    {
        string? why = null;
        try { why = ManaReserveHold?.Invoke(spellId); } catch { why = null; }
        if (why == null)
        {
            if (_manaHoldLogged) _host.Log("[CombatCast] mana is back: attacks resume");
            _manaHoldLogged = false;
            return false;
        }
        _manaCastHeld = true;
        LastCombatSkipReason = "mana-reserve"; // D4 record-only
        if (!_manaHoldLogged)
        {
            _manaHoldLogged = true;
            _host.Log($"[CombatCast] attacks wait: {why}");
        }
        return true;
    }

    /// <summary>Current combat mode — read live from AC client each access to avoid event-drop drift.</summary>
    public int CurrentCombatMode =>
        _host.HasGetCurrentCombatMode ? _host.GetCurrentCombatMode() : CombatMode.NonCombat;

    /// <summary>Client busy count — set by plugin from OnBusyCountIncremented/Decremented.
    /// When > 0, combat must not send any game actions (SelectItem, attack, cast) — unless
    /// <see cref="_busyGate"/> shows the count is a leftover the server already finished.</summary>
    public int BusyCount
    {
        get => _busyCount;
        set
        {
            _busyCount = value;
            _busyGate.NoteCount(value, _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0);
        }
    }
    private int _busyCount;
    private readonly CombatBusyGate _busyGate = new();

    /// <summary>Resets the plugin's shared busy mirror (not AC's real field) once combat finds the
    /// count is a leftover: the same thing BuffManager's cast-resolved callback does for buffs.
    /// Set by RynthAiPlugin; the argument says why.</summary>
    private Action<string>? _onStaleBusy;
    public void SetStaleBusyCallback(Action<string> cb) => _onStaleBusy = cb;

    private readonly CombatLatencyTracker _latency = new();
    private CombatLatencyTracker.Wait _tickWait = CombatLatencyTracker.Wait.Other;
    private bool _attackedThisTick;
    private int _lastAttackCmdTargetId;   // the target the last attack command (lastAttackCmd) was for

    /// <summary>D4 (record-only): human-readable reason combat did NOT cast on the
    /// most recent tick (or "cast" when it did). Set at every magic skip site; read
    /// by /ra why and the status feed. Purely diagnostic — never gates behavior.</summary>
    public string LastCombatSkipReason { get; private set; } = "";

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
    // Blacklist is driven by CONFIRMED no-damage casts, not wall-clock time.
    // A cast is queued in _pendingJudge* when issued and only judged a "miss"
    // after BlacklistCastSettleMs with no damage on the target (so the damage
    // packet has time to arrive). BlacklistAttempts consecutive misses → drop.
    private int _consecutiveCastMisses = 0;
    private DateTime _pendingJudgeCastAt = DateTime.MinValue;
    private int _pendingJudgeTargetId = 0;
    private bool _damageSincePendingCast;
    private DateTime _targetLockedAt     = DateTime.MinValue;
    // The current lock replaced another live target (a switch), not a lock after a kill or drop.
    private bool     _lockWasSwitch;
    private DateTime _lastDamageDealtAt  = DateTime.MinValue;

    // ── Server attack binding (the 2026-09-29 melee stall) ───────────────────
    // ACE binds a melee attack to Player.MeleeTarget and, while that creature is
    // alive, drops every further TargetedMeleeAttack ("already in melee loop?" in
    // HandleActionTargetedMeleeAttack) — it never switches targets on its own. When
    // the bound mob walked out of reach (the Olthoi in the next cell, 4-6 m away) the
    // server sat in its move-to for it and ignored the bot's attacks on the mob in
    // front of her: combat mode, action Combat, no swings, until the wand heal's
    // CancelAttack + mode change ended the server's attack. So whenever the bot moves
    // its physical attack to a new target while the old one may still be alive, it
    // sends CancelAttack first and attacks the new target on the next cycle.
    private int      _serverAttackTargetId;       // target of the last melee/missile attack request
    private int      _attackRebinds;               // CancelAttacks sent for a target change (status)
    private DateTime _lastRebindLogAt = DateTime.MinValue;
    private int      _rebindLogSuppressed;

    // Swing evidence for the stall watchdog: our hits (0x01B1), "X evaded your
    // attack." and kills. Normal melee produces one every 1-2 s.
    private DateTime _lastSwingEvidenceAt = DateTime.MinValue;
    private DateTime _lastHitAt  = DateTime.MinValue;
    private DateTime _lastMissAt = DateTime.MinValue;
    private DateTime _lastKillAt = DateTime.MinValue;   // stall-watchdog evidence only; metas and the status feed read the plugin's KillClock
    private string   _lastSwingEvidenceKind = "none";
    // Missile at point blank with the target behind: the server doesn't rotate, so one heading snap.
    private readonly PointBlankFacing _pointBlankFacing = new();

    private readonly CombatStallWatchdog _stallWatch = new();

    // ── Early stall step and stale objects (Drakkon 2026-10-12, Olthoi in 0x6146) ────────────
    // The flight recorder showed the stall: the server answered a MeleeAttack with a MoveToObject
    // to the target 2 m away, the character took a step and stopped, and the server waited for it
    // to arrive; every further attack was dropped ("already in melee loop") until the watchdog's
    // CancelAttack 6 s later, after which the server turned us and swung at once. Three such
    // stalls cost 7-9 s each and one good target got blacklisted a second after the cancel. Now
    // step 1 goes out after QuietStallMs when the stall is plain to see: attacks sent, no swing
    // playing, the character standing still (see SampleMeleeQuiet).
    // From 21:00 the same client fought four "Olthoi" for 25+ minutes without one swing: ghosts,
    // objects the client still drew but the server no longer had for him (see GhostCheck.cs). Each
    // was blacklisted for 30 s at a time, round and round, with navigation never getting a turn.
    // Now a target that shows no swing after the step-1 re-issue, hasn't moved and that the server
    // has said nothing about for GhostCheck.NoUpdateMs is a ghost: skipped until the server shows
    // it is real (an update, a create, it moves) or the character changes landblock, and removed
    // from the client when the engine can (plugin API v83 RemoveClientObject).
    private readonly GhostCheck _ghostCheck = new();
    internal readonly GhostTracker Ghosts = new();
    private int _ghostsSkippedInScan;                    // ghosts the last scan passed over (lock log)
    private GhostSpot? _lockSpot;                        // where the locked target was when locked
    private uint _ghostLandblock;
    private int      _attacksSinceEvidence;              // melee/missile attacks sent since the last hit, miss or kill
    private bool     _rebindThisTick;                    // AttackTarget sent a rebind CancelAttack this tick
    private DateTime _lastAnimatingAt = DateTime.MinValue;   // the motion queue last read busy (a swing playing)
    private DateTime _lastPlayerMovedAt = DateTime.MinValue; // the character last moved QuietMoveM
    private DateTime _lastQuietSampleAt = DateTime.MinValue;
    private double   _moveAnchorX, _moveAnchorY, _moveAnchorZ;
    private const double QuietNoAnimMs = 1500;           // no swing playing for this long
    private const double QuietStillMs  = 1000;           // and standing still this long
    private const double QuietMoveM    = 0.3;
    private DateTime _stallToggleAt = DateTime.MinValue;        // step 3 sent peace; the flip back follows
    private DateTime _lastMissileNoAmmoAt = DateTime.MinValue;   // missile mode waiting on ammo is not a stall
    private int      _stallRewieldId;                            // step 4 in progress (weapon id)
    private bool     _stallRewieldWieldSent;
    private int      _stallRewieldWieldTries;
    private DateTime _stallRewieldAt = DateTime.MinValue;
    private DateTime _stallRewieldCheckAt = DateTime.MinValue;
    private const double StallRewieldStepTimeoutMs = 5000;
    private const int    StallRewieldMaxWieldTries = 3;

    // Ids to skip in the scanner for a short window — both confirmed kills
    // (KillerNotification 0x01AD) and predicted kill-shot swaps. Value = the
    // EXPIRY time, so the longer confirmed window and the shorter predicted
    // window can coexist. The death signal reaches us at the lethal hit —
    // before the mob's health reads 0 or it reclassifies to a corpse (ACE
    // delays CreateCorpse by the full death-animation length) — so without this
    // the scanner would re-lock the dead-but-not-yet-corpse mob and burn another
    // cast on it. Self-prunes at the top of ScanNearbyTargets.
    private readonly Dictionary<int, DateTime> _recentlyKilled = new();
    private readonly List<int> _killPruneScratch = new();
    private const double RECENTLY_KILLED_SUPPRESS_MS = 4000.0;  // confirmed kill
    private const double PREDICTED_SWAP_SUPPRESS_MS  = 2000.0;  // predicted kill (re-acquirable if it survived)

    // ── Kill-shot prediction (damage-based, selection-free) ──────────────────
    // Combat can't read an unselected mob's live health (ACE only pushes vitals
    // for the SELECTED target, and combat is selection-free). Instead we learn
    // from the EXACT per-hit damage the engine pushes (AttackerNotification
    // 0x01B1): accumulate damage dealt this fight, compare to the mob's HP
    // (appraised MaxHealth from the shared CreatureProfileStore, else learned
    // damage-to-kill per wcid), and when the cast we just fired is expected to
    // finish it, swap to the next target instead of wasting the following cast
    // during this projectile's flight.
    private CreatureProfileStore? _creatureStore;   // shared: MaxHealth + resists by wcid
    private MonsterDamageStore?   _damageStore;     // per-character: avg damage + learned HP by wcid
    private int    _fightTargetId;          // the id _fightDamage is being accumulated for
    private uint   _fightTargetWcid;        // its weenie-class id (stable monster-type key)
    private string _fightTargetName = "";   // its name (stored in the learning file for readability)
    private double _fightTargetMaxHp;       // 0 = not yet known
    private double _fightDamage;            // cumulative confirmed damage dealt this fight
    private int    _fightCastCount;         // offensive casts fired this fight (for casts-to-kill learning)
    private bool   _fightAppraiseRequested; // one appraisal attempt per fight to fill unknown HP
    private bool   _fightHpEstimated;       // _fightTargetMaxHp is tier 0's scaled by the tier, not measured
    private DifficultyTier? _difficultyTier; // the server plugin's difficulty tier where we stand (0 without one)

    // Predicted one-shots are swapped away BEFORE their own KillerNotification
    // arrives, so OnKillNotification (which keys off the CURRENT target) can't
    // attribute them — they'd go uncounted and bias casts-to-kill HIGH (the
    // recorded average ends up built only from the messier non-swap kills).
    // Stash each predicted kill here; record it to the store only when its death
    // is CONFIRMED (so a wrong prediction expires unrecorded), and consume the
    // matching death-notification so it doesn't drop the (different) live target.
    private readonly List<PendingKill> _predictedKillPending = new();
    private readonly record struct PendingKill(
        string Name, uint Wcid, int CastCount, uint WeaponId, string Element, int Tier, double Damage, DateTime Expiry,
        DateTime Started = default, string? Summon = null, int Id = 0);

    // Predicted kills that never happened (player log, 2026-10-11): two Rampagers side by side,
    // each cast landing nothing. The casts-to-kill average said one Nether Arc kills a Rampager, so
    // every cast#1 was a "predicted kill" and the bot swapped to the other one, and back, for 30
    // minutes (~460 swaps, 533 casts since the last kill). Each swap drops the target, which wipes
    // the no-damage judge (DropTarget clears the miss streak and the pending verdict, and a new
    // target restarts the streak), so the NO DAMAGE report, the flight recorder and the blacklist
    // never ran. A monster we swapped off as dead that is fought again is alive: the prediction
    // was wrong, so no more predicted swaps off it — the bot stays on it and the judge can count.
    private readonly Dictionary<int, DateTime> _predictedSwapAt = new();      // id -> when we swapped off it as a predicted kill
    private readonly Dictionary<int, DateTime> _predictionFailedAt = new();   // id -> when it turned out alive: never predicted again
    private const double PredictionMemoryMs = 10 * 60_000;                    // both forget after 10 min

    // Ring of recently-fought objects (by id). The single _lastFight* slot only credits a kill
    // that lands one-back; when we've advanced PAST the dying mob (rapid multi-mob melee) its
    // death message matches neither the current target nor _lastFight, and the kill is lost
    // (KillDbg how=miss). This ring credits it by name across the last few fights.
    private readonly List<(int Id, PendingKill Pk)> _recentFights = new();
    private const int RecentFightsCap = 12;
    private const double RecentFightTtlMs = 8000.0;

    // name -> wcid for monsters seen by the scanner this session. AOE/area casts kill mobs we
    // never directly fought (ACE streams health only for the selected target, so tiers 1-4 never
    // saw them) — tier 5 credits those kills by matching the death message against these names.
    // Same pump thread as the scanner and OnKillNotification, so no lock needed.
    private readonly Dictionary<string, uint> _seenMonsterNameToWcid =
        new(StringComparer.OrdinalIgnoreCase);

    // Persistent snapshot of the most-recently-cast-at fight — survives DropTarget
    // (which zeroes _fightTarget*). When a kill arrives after the bot has already
    // dropped/advanced (common when one-shotting fast: the corpse's KillerNotification
    // lands a beat after we moved on), this lets the kill still be credited by name.
    private uint   _lastFightWcid;
    private string _lastFightName = "";
    private uint   _lastFightWeaponId;
    private string _lastFightElement = "";
    private int    _lastFightTier;
    private int    _lastFightCount;
    private int    _killDbgCount;
    private string _lastCastElement = "Fire"; // element of the most recent offensive cast
    private int    _lastCastTier;             // tier (spell level) of the most recent offensive cast
    private uint   _lastCastWeaponId;         // weapon (wand) guid the most recent cast used — damage varies by weapon
    private DateTime _fightStartedAt;         // when BeginFight engaged the current target (seconds-to-kill)
    private string?  _fightSummon;            // summon element out during this fight (null = none, "" = unknown element)

    /// <summary>The summon out right now: null = none, "" = one of unknown element, else its element. Set by the plugin (PetManager).</summary>
    internal Func<string?>? SummonOut;

    private int    _equippedWeaponId;         // weapon EquipWeaponAndSetStance last wielded (any mode) — drives melee/missile damage learning
    private bool   _predictKillSwap;          // set by AttackWithMagic, consumed in Think after the cast
    // Predicted kill shot is ARMED here but only executed once the cast is
    // CONFIRMED accepted (ACE: no "You're too busy!" within the refusal window).
    // The old code dropped the target 1ms after issuing the cast — so a REFUSED
    // cast still abandoned a live mob and advanced to the next, churning the
    // swarm and pinning AC's m_cBusy (the "can't enter combat mode" wedge).
    private bool   _predictKillSwapArmed;
    private bool   _predictSwapGestureSeen;       // observed the cast windup since arming (gate the swap on it)
    private int      _predictArmTargetId;         // target of the cast that armed the swap
    private DateTime _predictArmCastAt = DateTime.MinValue;   // when that cast was issued
    private const double PredictKillSwapMaxWaitMs = 3000; // hard cap: a tier-8 cast fully resolves well within this
    private const double KILL_CONFIDENCE = 0.80; // predict kill if remaining HP <= avg cast damage * this
    private const int    KILL_MIN_SAMPLES = 3;    // need this many learned hits before trusting the avg

    // A boss swapped off as dead with a third of its health left (report R-20261025-183343-727b,
    // Conquest, 2026-10-25): "Predicted kill shot 0x80621EBC 'Curator of Torment' (cast#37
    // hp~450250 dealt~294989)". Path A (casts-to-kill) fires on the cast COUNT alone: the learned
    // average said ~37 Flame Bolts kill a Curator (kills other players helped with teach a low
    // count), so cast #37 was "the kill" although the damage math said ~155,000 HP were left and
    // one bolt does 5-12k, and the server's own health (34% on the re-lock) agreed. Now either
    // prediction is called off when the evidence we have says this cast can't finish it: the
    // fight target's fresh server health, or (once damage has landed) the HP pool minus the
    // damage dealt, leaves more than KILL_VETO_REACH casts' worth: what is left after this cast
    // must be under half a cast (the margin for the roll: earlier low rolls, this one high). So a
    // normal mob's finishing cast still swaps, and a one-shot (nothing landed yet) keeps path A.
    private const double KILL_VETO_REACH = 1.5;
    private int _killShotVetoLoggedFor;            // fight target the "not predicted" line was logged for (once a fight)
    // The fight target's last SERVER health (OnUpdateHealth via the plugin; ACE sends it only for
    // the selected target) and when it came, and when our last hit on it landed: a reading older
    // than the last hit doesn't count that hit, and one from before the fight isn't this fight's.
    private float    _fightHealth = -1f;
    private DateTime _fightHealthAt = DateTime.MinValue;
    private DateTime _fightLastHitAt = DateTime.MinValue;
    private const double FightHealthHitSlackMs = 500;   // health and the hit's chat line arrive in either order

    // The armed cast's own hit (same report): the swap waits for the cast gesture to release,
    // and a Flame Bolt's gesture released 800 ms AFTER its bolt had hit ("You singe Curator of
    // Torment for 7883 points" at 58.107, swap at 58.920). ACE sends the killer the death
    // message instead of a damage line on a killing blow, so a damage line on the armed target
    // means it survived the cast that was to kill it: the swap is called off. A line under
    // ArmedHitMinMs after the cast is the previous cast's projectile (no windup is that short).
    private const double ArmedHitMinMs = 1000;

    // A predicted kill whose death isn't confirmed yet still counts as a monster in range (same
    // report): the swap hid the Curator from the scan for PREDICTED_SWAP_SUPPRESS_MS, the
    // Rynthid it swapped to died at once, and with the scan empty HasTargets went false, so the
    // corpse gate let looting claim a corpse and open it for 4.6 s while the live Curator, 1.9 m
    // away, hit the player. Until the kill notice, health 0 or the corpse confirms its death,
    // or the suppression ends and the scan has it again, it counts for HasTargets,
    // HasCloseThreat (so HasEngageableTarget: combat keeps the tick, no corpse is claimed, no
    // starved loot turn) and the idle peace-mode swap. id -> when it was swapped off (TargetClock).
    // A kill notice matched by name to a monster other than the fight target is held here the
    // same way (SuppressGuessedDeath): the name can match the wrong one of a same-named pack.
    private readonly Dictionary<int, DateTime> _predictedDeathPending = new();
    private readonly List<int> _predictedDeathScratch = new();

    internal void SetDamageStores(CreatureProfileStore? creatures, MonsterDamageStore? damage)
    {
        _creatureStore = creatures;
        _damageStore = damage;
    }

    internal void SetDifficultyTier(DifficultyTier? tier) => _difficultyTier = tier;

    /// <summary>The difficulty tier where we stand, as the server plugin reports it; always 0 without one.</summary>
    private int Difficulty => _difficultyTier?.Current ?? 0;

    public int RaycastBlockCount { get; private set; }
    public int RaycastCheckCount { get; private set; }

    // D2 three-tier target telemetry, refreshed each ScanNearbyTargets() so a
    // "scanned=0 vs live mobs" wedge shows WHICH stage zeroed the candidate set:
    //   Total>0,Ring=0      → mobs exist but all out of engage range (nav/spawn issue)
    //   Ring>0,Possible=0   → over-filtering (blacklist / not-attackable / LOS)
    //   Total=0             → genuinely blind (cache empty / respawn-blind)
    // -1 = no scan has run yet this session.
    public int LastScanTotalMonsters { get; private set; } = -1;  // real monsters the client renders
    public int LastScanInRing { get; private set; } = -1;         // of those, within MonsterRange
    public int LastScanPossible { get; private set; } = -1;       // survive all combat filters (= candidates)
    public int LastScanLosBlocked { get; private set; } = -1;     // in range + attackable but wall-blocked

    // D6 offensive attack-cast counters. CastsSinceLastKill climbing while kills stay
    // flat is the early signal of the "animates but 0 damage / too busy forever" orphan.
    public int SessionAttackCasts { get; private set; }
    public int CastsSinceLastKill { get; private set; }

    // ── Monster scanner ──────────────────────────────────────────────────
    // Pre-scans nearby creatures for distance, IsAttackable, and LOS.
    // Combat picks from this list — no SelectItem needed until attack time.
    private readonly List<ScannedTarget> _scannedTargets = new();
    private DateTime _lastScanTime = DateTime.MinValue;
    private const int SCAN_INTERVAL_MS = 50;

    // Target selection (2026-10-10): the Targeting setting (TargetSelection) ranks the
    // candidates by one rule (lowest health, closest, farthest, least turning) and Target
    // Lock keeps a pick until it is no longer valid. It replaced a blended utility score
    // (distance + health + threat + facing, plus a stickiness and a commitment bonus for the
    // incumbent), which still switched targets mid-fight. See HandleCombatTrigger.
    //
    // With Target Lock off, a monster must beat the incumbent by these margins under the
    // Targeting rule to take over, so near-ties don't flap.
    private const float  MoreWoundedMargin    = 0.05f;   // Lowest health: 5 percentage points
    private const double CloserFartherMarginM = 1.5;     // Closest / Farthest: metres
    private const double LessTurningMarginDeg = 20.0;    // Least turning: degrees of facing change

    /// <summary>The Targeting setting's choices (LegacyUiSettings.TargetSelection).</summary>
    internal enum TargetScheme { LowestHealth = 0, Closest = 1, Farthest = 2, LeastTurning = 3 }

    // Monster priority is a strict tier, not a score bonus: when a monster of a higher
    // Priority is in range (MonsterRange), only monsters of that Priority are candidates,
    // and a lower-priority target is left for it at once, Target Lock or not. As a
    // +5-per-level score bonus it lost to the incumbent's bonuses and did "little to
    // nothing" (2026-10-03).
    // Priority comes from GetRuleForTarget (name, match expression, else the DEFAULT row),
    // cached per monster for PriorityCacheMs because match expressions aren't free and
    // HandleCombatTrigger runs every tick over every scanned monster.
    private readonly Dictionary<int, (int Priority, DateTime At)> _priorityCache = new();
    private List<MonsterRule>? _priorityCacheRules;
    private const double PriorityCacheMs = 1000;
    private int _lockedPriority;
    private (int Locked, int Other) _lockHeldOverLogged;   // the last "Target Lock keeps" line (HandleCombatTrigger)
    private bool _lockByPlayer;   // the lock came from the player's pick (TryLockForcedTarget), not the bot's

    /// <summary>With Target Lock off, an attack at the locked target this recent, with no
    /// damage landed since, is still on its way: no switch until it lands or this passes.
    /// Covers a projectile's flight (Force Arc has ~2 s of travel; switching with a bolt in
    /// the air walked away from the mob it was about to hit, 2026-09-03). See
    /// <see cref="AttackInFlightAtLocked"/>.</summary>
    private const double CastCommitmentWindowMs = 4000.0;

    /// <summary>Ceiling on the in-flight hold: once nothing has landed on the locked target
    /// for this long (since its last damage, or since the lock when it never took any), an
    /// attack at it no longer holds off a switch. Without a ceiling the hold refreshes on
    /// every cast, so a target that never takes damage could keep it forever (the
    /// 2026-09-05 stall: one Olthoi, 5+ hours, ~3,100 casts, no damage).</summary>
    private const double UndamagedCommitmentMaxMs = 15000.0;

    /// <summary>When the last offensive action was issued at
    /// <see cref="_lastAttackedTargetId"/>. Bounds the in-flight hold.</summary>
    private DateTime _lastAttackedAt = DateTime.MinValue;

    /// <summary>The last offensive cast sent, at whom and when (TargetClock), before any judgement:
    /// the in-flight hold's view of a cast that has just gone out.</summary>
    private int _castSentTargetId;
    private DateTime _castSentAt = DateTime.MinValue;

    /// <summary>An offensive cast went out at <paramref name="targetId"/> (internal for tests).</summary>
    internal void NoteCastSent(int targetId)
    {
        _castSentTargetId = targetId;
        _castSentAt = TargetClock();
    }

    // Incumbent continuity across scan gaps. ScanNearbyTargets drops a target on
    // a single bad LOS result or a transient miss — the candidate list has been
    // observed oscillating 14<->17 entries tick to tick. Picking only from what is
    // in the list handed the fight to whoever was visible that tick and back again
    // the next (the A->B->A flapping of 2026-09-03, logged as "score=gone"). The
    // incumbent stays valid for TARGET_SCAN_GRACE_MS at its last seen values instead.
    private ScannedTarget _lockedLastSeen;
    private DateTime _lockedLastSeenInScan = DateTime.MinValue;

    /// <summary>Tests: the clock target selection reads (scan grace, in-flight hold).</summary>
    internal Func<DateTime> TargetClock { get; set; } = () => RynthClock.Now;

    public struct ScannedTarget
    {
        public int Id;
        public double Distance;
        public double Angle;   // absolute facing error to target, degrees (0 = directly ahead)
        public double Bearing; // heading from the player to it, degrees (0 = north, clockwise); NaN = unknown
        public string Name;
    }

    /// <summary>List of nearby, attackable, LOS-clear creatures. Re-scanned every tick (≤50 ms).
    /// Sorted by distance — HandleCombatTrigger picks from it by the Targeting setting.</summary>
    public IReadOnlyList<ScannedTarget> ScannedTargets => _scannedTargets;
    /// <summary>Tests: replace the scan list (distances and facing angles as given).</summary>
    internal void SetScannedTargetsForTests(IEnumerable<ScannedTarget> targets)
    {
        _scannedTargets.Clear();
        _scannedTargets.AddRange(targets);
    }
    internal MonsterDamageStore?   DamageStore   => _damageStore;    // per-monster pet choice (PetManager)
    internal CreatureProfileStore? CreatureStore => _creatureStore;  // learned resists (PetManager)

    /// <summary>True when combat has an active target or viable targets nearby. Used to block navigation
    /// and by the corpse gates. A predicted kill still alive in MonsterRange counts until its death is
    /// confirmed (see <see cref="_predictedDeathPending"/>).</summary>
    public bool HasTargets => activeTargetId != 0 || _scannedTargets.Count > 0
                              || PredictedDeathPendingWithin(System.Math.Max(1, _settings.MonsterRange));

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
        return PredictedDeathPendingWithin(yd);
    }

    /// <summary>
    /// True when a monster swapped off as a predicted kill is, as far as we know, still alive within
    /// <paramref name="yd"/>: its death isn't confirmed (kill notice, health 0, corpse, deleted) and
    /// the scan suppression from the swap hasn't ended (after that the scan itself has it again).
    /// No side effects (the arbiter's pure path); ScanNearbyTargets prunes the map.
    /// </summary>
    private bool PredictedDeathPendingWithin(double yd)
    {
        if (_predictedDeathPending.Count == 0) return false;
        DateTime now = TargetClock();
        int me = unchecked((int)_playerId);
        foreach (var kv in _predictedDeathPending)
        {
            if ((now - kv.Value).TotalMilliseconds > PREDICTED_SWAP_SUPPRESS_MS) continue;
            if (!IsLiveMonster(kv.Key)) continue;
            if (me == 0 || _worldFilter.Distance(me, kv.Key) > yd) continue;
            return true;
        }
        return false;
    }

    /// <summary>The object is a monster the server hasn't said is dead (health 0) and isn't a corpse.</summary>
    private bool IsLiveMonster(int id)
    {
        var wo = _worldFilter[id];
        return wo != null && (int)wo.ObjectClass == (int)AcObjectClass.Monster
               && _worldFilter.GetHealthRatio(id) != 0f;
    }

    /// <summary>
    /// ScanNearbyTargets: drop pending predicted kills that are confirmed dead or whose window is
    /// over. One that is still alive at the end of its window is back in the scan from this tick
    /// (its suppression ended), which the log says, so combat goes back to it at once.
    /// </summary>
    private void PrunePredictedDeaths()
    {
        if (_predictedDeathPending.Count == 0) return;
        DateTime now = TargetClock();
        _predictedDeathScratch.Clear();
        foreach (var kv in _predictedDeathPending)
        {
            bool alive = IsLiveMonster(kv.Key);
            bool over = (now - kv.Value).TotalMilliseconds > PREDICTED_SWAP_SUPPRESS_MS;
            if (alive && !over) continue;
            _predictedDeathScratch.Add(kv.Key);
            bool guessed = _guessedDeaths.Contains(kv.Key);
            if (!alive) continue;   // health 0 or the corpse: dead after all, nothing to say
            // Alive at the end of the window: lift the swap's suppression now, whatever RynthClock
            // says (TargetClock is that clock outside tests). A confirmed kill took the id out of
            // this map in OnKillNotification, so its longer suppression is never lifted here.
            _recentlyKilled.Remove(kv.Key);
            _host.Log(guessed
                ? $"[RynthAi] Kill notice matched by name to 0x{(uint)kv.Key:X8} '{_worldFilter[kv.Key]?.Name}' not confirmed " +
                  $"{(now - kv.Value).TotalSeconds:0.0} s later and it is still alive: it is a target again."
                : $"[RynthAi] Predicted kill of 0x{(uint)kv.Key:X8} '{_worldFilter[kv.Key]?.Name}' not confirmed " +
                  $"{(now - kv.Value).TotalSeconds:0.0} s after the swap and it is still alive: it is a target again.");
        }
        for (int i = 0; i < _predictedDeathScratch.Count; i++)
        {
            _predictedDeathPending.Remove(_predictedDeathScratch[i]);
            _guessedDeaths.Remove(_predictedDeathScratch[i]);
        }
    }

    /// <summary>A predicted kill was hit after the swap (a damage line, which a killing blow never
    /// sends): it is alive. Back in the scan now, not at the end of the suppression window.</summary>
    private void NotePredictedKillSurvived(int id, int amount)
    {
        if (id == 0 || !_predictedDeathPending.Remove(id)) return;
        _recentlyKilled.Remove(id);
        bool guessed = _guessedDeaths.Remove(id);
        _host.Log(guessed
            ? $"[RynthAi] Kill notice matched by name to 0x{(uint)id:X8} '{_worldFilter[id]?.Name}', but it took a {amount}-point hit: " +
              "it is alive, a target again."
            : $"[RynthAi] Predicted kill of 0x{(uint)id:X8} '{_worldFilter[id]?.Name}' took a {amount}-point hit after the swap: " +
              "it is alive, a target again.");
    }

    /// <summary>True only when actively attacking a specific target. Unlike HasTargets, this is false between kills even when more monsters are scanned nearby.</summary>
    public bool IsActivelyEngaged => activeTargetId != 0;

    /// <summary>Yards inside which a scanned mob counts as "on top of me".</summary>
    private const double CloseAttackYards = 5.0;

    /// <summary>
    /// "A fight is actually happening to me" — the arbiter's escape hatch that
    /// lets Combat preempt BoostLootPriority (2026-06-03 audit P1#1).
    ///
    /// Deliberately NOT HasEngageableTarget: that is true for any scanned mob
    /// inside MonsterRange, which would make BoostLootPriority meaningless.
    /// And deliberately not IsActivelyEngaged alone: while BoostLoot holds the
    /// Looting decision, canRun blocks Think(), so activeTargetId never gets
    /// set — keying the escape on it would be circular (blocked → no lock →
    /// still blocked → mob keeps hitting a bot that never fights back). The
    /// scan always runs and has no side effects, so a close-range scanned mob
    /// is the non-circular signal.
    /// </summary>
    public bool IsUnderCloseAttack => IsActivelyEngaged || HasCloseThreat(CloseAttackYards);

    /// <summary>
    /// HoldNewTarget says wait (Boost Loot Priority, a kill's corpse to loot) and no monster is
    /// within CloseAttackYards. One log line per hold.
    /// </summary>
    private bool NewTargetHeldForLoot()
    {
        bool held;
        try { held = HoldNewTarget?.Invoke() == true && !HasCloseThreat(CloseAttackYards); }
        catch { held = false; }
        if (!held)
        {
            _newTargetHeldLogged = false;
            return false;
        }
        if (!_newTargetHeldLogged)
        {
            _newTargetHeldLogged = true;
            string why = "Boost Loot Priority loots the kill first";
            try { if (HoldNewTargetWhy?.Invoke() is { Length: > 0 } w) why = w; } catch { }
            _host.Log("[CombatTarget] next target waits: " + why);
        }
        return true;
    }

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

    /// <summary>
    /// Distance at which an already-locked target is dropped / stops being
    /// attacked. Larger than MonsterRange (the acquire distance) to form a
    /// hysteresis deadband around the combat-mode step-back. Configurable via
    /// MonsterDisengageRange; 0 or any value not exceeding MonsterRange falls
    /// back to MonsterRange + 3.
    /// </summary>
    private double DisengageDistance =>
        _settings.MonsterDisengageRange > _settings.MonsterRange
            ? _settings.MonsterDisengageRange
            : _settings.MonsterRange + 3;

    /// <summary>Diagnostic snapshot for /ra combat — exposes the internal state machine fields.</summary>
    public CombatStateSnapshot GetStateSnapshot()
    {
        // Pick the same weapon EquipWeaponAndSetStance would pick for the active target,
        // so the dump shows which weapon combat is trying to swing/cast with.
        WorldObject? targetObj = activeTargetId != 0 ? _worldFilter[activeTargetId] : null;
        int    pickedWeaponId    = 0;
        string pickedWeaponName  = "";
        string pickedWeaponWhy   = "";
        int    pickedWeaponMode  = 0;
        int    weaponWieldLoc    = -1;
        if (targetObj != null)
        {
            // The same choice EquipWeaponAndSetStance makes, else any wand.
            var dumpRule = GetRuleForTarget(targetObj);
            string dumpDesired = "Auto";
            pickedWeaponId = ChooseWeapon(targetObj, dumpRule, ref dumpDesired, out pickedWeaponWhy);
            if (pickedWeaponId == 0) { pickedWeaponId = FindWandInItems(); pickedWeaponWhy = "FindWandInItems"; }

            if (pickedWeaponId != 0)
            {
                var w = _worldFilter[pickedWeaponId];
                if (w != null)
                {
                    pickedWeaponName = w.Name ?? "";
                    weaponWieldLoc   = w.Values(LongValueKey.CurrentWieldedLocation, 0);
                    pickedWeaponMode = IsWandObject(w)                                  ? CombatMode.Magic
                                    : w.ObjectClass == AcObjectClass.MissileWeapon ? CombatMode.Missile
                                    : CombatMode.Melee;
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
            PickedWeaponWhy      = pickedWeaponWhy ?? "",
            PickedWeaponMode     = pickedWeaponMode,
            PickedWeaponWieldLoc = weaponWieldLoc,
            HasWieldedAmmoFlag   = hasAmmo,
            LastCombatSkipReason = LastCombatSkipReason,
            ScriptHookOwner      = CombatHook.IsActive(RynthAiCombat.KindsAll) ? CombatHook.Owner : "",
            ScriptHookKinds      = CombatHook.IsActive(RynthAiCombat.KindsAll) ? CombatHook.Kinds : 0,
            ScriptHook           = CombatHook.Describe(),
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
        /// <summary>Why that weapon (the plan's source, e.g. "slayer (Olthoi x2)").</summary>
        public string   PickedWeaponWhy;
        public int      PickedWeaponMode;
        public int      PickedWeaponWieldLoc;
        public bool     HasWieldedAmmoFlag;
        public string   LastCombatSkipReason;
        /// <summary>The script holding the combat hook (RynthAi.Combat), "" when none; and its kinds (RynthAiCombat.Kind* bits).</summary>
        public string   ScriptHookOwner;
        public uint     ScriptHookKinds;
        /// <summary>The hook's one-line summary (owner, lease, per-kind counters).</summary>
        public string   ScriptHook;
    }

    /// <summary>
    /// Periodic scan of all creatures. Filters: distance, self, blacklist, IsAttackable, raycast LOS.
    /// Call from OnHeartbeat or Think.
    /// </summary>
    public void ScanNearbyTargets()
    {
        if ((RynthClock.Now - _lastScanTime).TotalMilliseconds < SCAN_INTERVAL_MS)
            return;
        _lastScanTime = RynthClock.Now;

        // Raycast just became ready (warmup→ready edge, flagged on the bg
        // thread): one-time clean slate. Whatever got blacklisted or had its
        // miss/no-progress timers run up while targeting was degraded is
        // forgiven, so mobs that were present at login are re-evaluated with
        // real LOS/attack-type instead of being sidelined for 5 minutes.
        if (_raycastReadyResetPending)
        {
            _raycastReadyResetPending = false;
            _blacklistManager.ClearAll();
            _consecutiveCastMisses = 0;
            _pendingJudgeCastAt = DateTime.MinValue;
            _lastAttackedTargetId = 0;
            _targetLockedAt = DateTime.MinValue;
            _lastDamageDealtAt = DateTime.MinValue;
            _host.Log("[RynthAi] Raycast ready — combat clean slate (blacklist cleared, target timers reset).");
        }

        // Drop expired kill-suppression entries so the set can't grow unbounded
        // over a long session (a suppressed id may never re-enter scan to be
        // cleared lazily). Allocation-free: scratch list is reused.
        if (_recentlyKilled.Count > 0)
        {
            DateTime now = RynthClock.Now;
            _killPruneScratch.Clear();
            foreach (var kv in _recentlyKilled)
                if (now >= kv.Value)            // Value = expiry time
                    _killPruneScratch.Add(kv.Key);
            for (int i = 0; i < _killPruneScratch.Count; i++)
                _recentlyKilled.Remove(_killPruneScratch[i]);
        }
        PrunePredictedDeaths();   // before the loop: a predicted kill still alive is scanned again this tick

        _scannedTargets.Clear();
        _passedOver.Clear();
        _beyondRange.Clear();
        _nearbyNoLos = 0;
        int scanTotal = 0, scanRing = 0;   // D2 telemetry, published after the loop
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
            attackType = DetermineAttackTypeForLOS();
        bool losDebug = _settings.LosDebugLog;
        if (losDebug) LogLosDebugWeapon();

        // The player's position once per scan: Distance(player, mob) re-read it for every
        // creature, two host reads per monster 20 times a second (2026-10-02 flood work).
        bool hasPlayerPos = _host.TryGetObjectPosition((uint)playerId, out uint meCell,
            out float meX, out float meY, out float meZ);

        // Ghosts that moved are real after all; a new landblock lets them all go.
        _ghostsSkippedInScan = 0;
        if (Ghosts.Count > 0)
        {
            if (hasPlayerPos) Ghosts.OnPlayerLandblock(meCell >> 16);
            Ghosts.CheckMoved(TargetSpot);
        }

        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.Id == playerId) continue;
            if ((int)wo.ObjectClass != (int)AcObjectClass.Monster) continue;

            // Never acquire our own spell projectiles (mis-classified as
            // Monster, no health record). A real monster is never named
            // "Flame Bolt"/"Frost Streak"/etc.
            if (IsSpellProjectileName(wo.Name)) continue;

            // D2 telemetry: every real monster the client renders counts toward Total, and (by
            // pure proximity) toward Ring. dist is hoisted up here from below the blacklist gate
            // so Ring reflects how many mobs are physically near us regardless of the gameplay
            // filters below — that's what distinguishes "out of range" from "all filtered out".
            scanTotal++;
            bool hasMobPos = false;
            float tx = 0f, ty = 0f;
            double dist = hasPlayerPos
                ? _worldFilter.DistanceFrom(meCell, meX, meY, meZ, wo.Id, out hasMobPos, out tx, out ty)
                : double.MaxValue;
            if (dist <= maxDist) scanRing++;

            // Cache name->wcid (once per type) for AOE kill attribution (tier 5): a mob wiped
            // by an area cast we never directly fought is credited by matching its death message.
            // Attackable only — this runs before the attackable filter below, so an NPC the
            // cache misclassifies as a monster could otherwise be credited kills (Damage tab).
            if (!string.IsNullOrEmpty(wo.Name) && _seenMonsterNameToWcid.Count < 256
                && !_seenMonsterNameToWcid.ContainsKey(wo.Name)
                && (!_host.HasObjectIsAttackable || _host.ObjectIsAttackable((uint)wo.Id))
                && _host.HasGetObjectWcid && _host.TryGetObjectWcid((uint)wo.Id, out uint scanWcid) && scanWcid != 0
                && !CreatureData.PlayerWeenies.IsPlayerWcid(scanWcid))   // players the cache classed as monsters
                _seenMonsterNameToWcid[wo.Name] = scanWcid;

            // Allow the currently-engaged target out to DisengageDistance: AC's
            // combat-mode step-back routinely pushes the active target just past
            // MonsterRange, and dropping it from the scan defeated the disengage
            // hysteresis — it vanished from _scannedTargets and was dropped after
            // the 1.5s scan grace despite the distance gate intending to keep
            // attacking out to DisengageDistance.
            // The range gate comes before the other filters (all of them only drop a monster,
            // so the order doesn't change the result): in a crowded dungeon most monsters are
            // out of range and now cost only their distance, not the name list, blacklist,
            // attackable and health reads as well.
            bool isEngaged = wo.Id != 0 && (wo.Id == activeTargetId || wo.Id == _lockedTargetId);
            // Target lock (/ra targetlock): the player picked this one; the name list and
            // Priority 0 don't keep it out (attackable, alive and LOS still do).
            bool isForced = wo.Id != 0 && wo.Id == ForcedTargetId;
            if (dist > (isEngaged || isForced ? Math.Max(maxDist, DisengageDistance) : maxDist))
            {
                // Just past Monster Range: noted (no other reads here) so the fight can say when a
                // higher-priority monster is left only because of the Monster Range setting.
                if (!isEngaged && dist <= maxDist + PassedOverBeyondRangeM)
                    _beyondRange.Add(new PassedOverMonster { Id = wo.Id, Distance = dist, Why = "" });
                continue;
            }
            // Left out below for a reason that can hide a monster worth fighting: noted for the
            // lock line's "passed over" (CombatManager.PassedOver.cs). In range, not the target.
            bool notePassed = !isEngaged && dist <= maxDist;

            // User-configured "never attack" list — excluded from acquisition
            // entirely (an already-engaged match drops out of _scannedTargets
            // here and is released by the scan-grace timer in Think).
            if (!isForced && IsUserBlacklistedName(wo.Name))
            {
                if (notePassed) NotePassedOver(wo.Id, dist, PassedOverNeverAttack);
                continue;
            }

            if (_blacklistManager.IsBlacklisted(wo.Id))
            {
                if (notePassed) NotePassedOver(wo.Id, dist, PassedOverBlacklisted);
                continue;
            }

            // A ghost (the server doesn't have it for us): skipped until it shows it is real.
            if (Ghosts.IsGhost(wo.Id))
            {
                _ghostsSkippedInScan++;
                if (notePassed) NotePassedOver(wo.Id, dist, PassedOverGhost);
                continue;
            }

            // Left for hitting the environment (New target): skipped for EnvironmentSkipSec.
            if (IsEnvironmentSkipped(wo.Id))
            {
                if (notePassed) NotePassedOver(wo.Id, dist, PassedOverEnvironment);
                continue;
            }

            // Just killed (KillerNotification) but not yet a corpse — skip so we
            // don't re-lock and cast at it during its death animation.
            if (_recentlyKilled.ContainsKey(wo.Id)) continue;

            if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id))
            {
                if (notePassed) NotePassedOver(wo.Id, dist, PassedOverNotAttackable);
                continue;
            }

            // Dead but not yet reclassified as corpse — skip
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f) continue;

            // Monster rule Priority 0 = IGNORE, with no exception: even one attacking us is never
            // attacked (a monster worth fighting back needs a rule above 0). Left out of the
            // scan entirely, so it is neither a target nor a reason to hold nav,
            // combat, looting or buffs: every consumer reads this list or _nearbyNoLos, and an
            // ignored monster is not counted in either (it is dropped before the LOS check).
            if (!isForced && PriorityOf(wo.Id) <= 0) continue;

            bool losBlocked = false;
            if (LosBlockedOverride != null)
                losBlocked = LosBlockedOverride(wo.Id);
            else if (_settings.EnableRaycasting && RaycastInitialized && _raycastSystem?.TargetingFSM != null)
            {
                RaycastCheckCount++;
                losBlocked = _raycastSystem.TargetingFSM.IsTargetBlocked(_host, (uint)wo.Id, attackType, out var losDetail);
                if (losBlocked) RaycastBlockCount++;
                if (losDebug) LogLosDebug(wo, losBlocked, losDetail);
            }

            // Melee reaches through walls, on both sides: a monster this close is a target when
            // melee is, or is about to be, the attack (the owner, 2026-11-06). It was left out while
            // hitting the character, who then read as having nothing to fight.
            if (losBlocked && dist <= MeleeIgnoresLosWithin && MeleeIsTheAttackOn(wo, attackType))
                losBlocked = false;

            // Don't blacklist from scan — just exclude from this result.
            // Blacklisting only happens when an active target fails LOS during attack.
            if (losBlocked)
            {
                _nearbyNoLos++; // in range + attackable but wall-blocked — still stops nav
                if (notePassed) NotePassedOver(wo.Id, dist, PassedOverLos);
                continue;
            }

            double angle = 180.0;
            double bearing = double.NaN;
            if (hasPose && hasMobPos) // the mob's position was read for the distance above
            {
                double desired = Math.Atan2(tx - ppx, ty - ppy) * (180.0 / Math.PI);
                if (desired < 0) desired += 360.0;
                bearing = desired;
                double err = desired - playerHeadingDeg;
                while (err > 180.0) err -= 360.0;
                while (err < -180.0) err += 360.0;
                angle = Math.Abs(err);
            }
            _scannedTargets.Add(new ScannedTarget { Id = wo.Id, Distance = dist, Angle = angle, Bearing = bearing, Name = wo.Name });
        }

        // Primary: closest first. Tiebreaker within 0.5yd: smallest facing angle first.
        _scannedTargets.Sort((a, b) =>
        {
            double dd = a.Distance - b.Distance;
            if (Math.Abs(dd) > 0.5) return dd < 0 ? -1 : 1;
            return a.Angle.CompareTo(b.Angle);
        });

        // Publish D2 telemetry (whole-int writes are atomic; the plugin tick reads these on the
        // same pump thread before pushing them to the status feed).
        LastScanTotalMonsters = scanTotal;
        LastScanInRing = scanRing;
        LastScanPossible = _scannedTargets.Count;
        LastScanLosBlocked = _nearbyNoLos;
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
        Ghosts.Cleared = (id, why) => _host.Log($"[Ghost] 0x{(uint)id:X8} '{_worldFilter[id]?.Name}' is real after all ({why}) - targetable again.");
        ElementTracker = new WeaponElementTracker(host);
    }

    /// <summary>Reads listed weapons' elements and identifies them (pumped by the plugin).</summary>
    internal WeaponElementTracker ElementTracker { get; }
    /// <summary>Combat's weapon swaps: one in flight, confirmed, capped, no flip-flop.</summary>
    internal WeaponSwapTracker SwapTracker { get; } = new();
    /// <summary>Tests: which elements the character can cast (default: the spellbook).</summary>
    internal Func<string, bool>? CanCastOverride { get; set; }
    /// <summary>Tests: the weapon in hand (default: a live read of the inventory).</summary>
    internal Func<int>? InHandOverride { get; set; }

    public void SetSpellManager(SpellManager spellManager) => _spellManager = spellManager;
    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;

    /// <summary>Plugin forwards every object deletion here (D7/D8). For a genuinely-despawned
    /// object (a reliable engine event, unlike a transient scan gap) we: (D8) drop its stale
    /// per-id state — kill-suppression + blacklist/failure entries — so a respawn reusing the GUID
    /// starts clean instead of inheriting the old 300s timeout; and (D7) if it was the target we
    /// were attacking or mid-cast on, clear the cast-resolution/debuff wait so we don't sit out the
    /// 2.5s windup timeout casting at nothing. Writes are atomic flag/int sets — safe on any thread.</summary>
    public void OnObjectDeleted(uint objectId)
    {
        int id = (int)objectId;
        _recentlyKilled.Remove(id);
        _predictedDeathPending.Remove(id);   // gone (died and despawned, or out of view): not a monster in range
        _guessedDeaths.Remove(id);
        _hitAt.Remove(id);
        _blacklistManager.ForgetTarget(id);
        Ghosts.NoteDeleted(id);
        OnEnvironmentObjectDeleted(objectId);

        if (id == activeTargetId || id == _lockedTargetId || id == _lastAttackedTargetId)
            _awaitingCastResolution = false;
        if (id == _pendingDebuffTargetId)
        {
            _waitingForDebuffResult = false;
            _pendingDebuffTargetId = 0;
        }
        _debuffs.Forget(id);
    }
    public void SetPlayerId(uint playerId)
    {
        if (playerId != _playerId)
        {
            // A new character (or a relog): its weapons are new objects.
            SwapTracker.Reset();
            ResetAmmoState();
            ResetOffhandState();
            ElementTracker.Reset();
            _unlistedNoted.Clear();
            _inHandId = 0;
            _inHandReadAt = DateTime.MinValue;
        }
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

    // The rule's debuffs per target: landed ones renewed before they wear off, failed tries counted.
    private readonly DebuffTimers _debuffs = new();
    private int _lastDebuffTargetId = 0;

    private string? _pendingDebuffKey = null;
    private int _pendingDebuffTargetId = 0;
    private int _pendingDebuffSpellId = 0;
    private int _pendingDebuffTier = 0;
    private bool _waitingForDebuffResult = false;
    private DateTime _pendingDebuffCastTime = DateTime.MinValue;
    private const double DEBUFF_RESULT_TIMEOUT_MS = 3000.0;

    // Last offensive (war/void) spell we fired and when. If AC never answers it
    // (char doesn't know it, or no components) the no-chat valve in
    // AttackWithMagic marks it unresolvable so FindBestOffensiveSpellId tiers
    // down to an alternative the char actually has — same empirical signal the
    // buff path uses (the engine IsSpellKnown oracle lies "true" for unknowns).
    private int _pendingOffensiveSpellId = 0;
    private DateTime _pendingOffensiveCastAt = DateTime.MinValue;
    // Target id captured when the offensive cast was *issued*, so the
    // chat-confirmation handler can record the cast against the right target
    // for blacklist judgement — even if activeTargetId has since changed.
    private int _pendingOffensiveTargetId = 0;
    // The UseDone count at the pending offensive cast's send, and its answer (-1 = not seen yet).
    private int _pendingOffensiveSeqAtSend;
    private long _pendingOffensiveErr = -1;
    /// <summary>Casts the server refused 'too busy' that were kept out of the no-damage count (tests, status).</summary>
    internal int RefusedCastsNotJudged { get; private set; }

    private void NoteOffensiveSeq()
    {
        _pendingOffensiveSeqAtSend = _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0;
        _pendingOffensiveErr = -1;
    }

    /// <summary>The first UseDone after the pending offensive cast's send is its answer.</summary>
    private void ObserveOffensiveAnswer()
    {
        if (_pendingOffensiveErr >= 0 || !_host.HasUseDoneSeq) return;
        try
        {
            if (_host.TryGetLastUseDone(out int seq, out uint err) && seq == _pendingOffensiveSeqAtSend + 1)
                _pendingOffensiveErr = err;
        }
        catch { }
    }
    private const double OFFENSIVE_NOCHAT_TIMEOUT_MS = 5000.0;
    // ACE doesn't emit "You cast X" chat for offensive war magic, so we infer
    // a successful server cast by NEGATION: if no "You're too busy!" arrives
    // within this many ms of issuance, the gesture proceeded — queue the cast
    // for blacklist judgement. Server "too busy" responses arrive within
    // ~100–200ms; 500ms is a comfortable margin without delaying the next
    // attempt noticeably.
    private const double OffensiveRefusalWindowMs = 500.0;

    // Throttled lowercased inventory-name set for the predictive component
    // gate (TrySpellByName is called many times per resolution; rebuilding the
    // set every call would be wasteful). _compSkipLogged dedupes the skip log
    // within a cache window so a tiered-down spell doesn't spam the log.
    private readonly HashSet<string> _invNamesLower = new();
    private DateTime _invNamesBuiltAt = DateTime.MinValue;
    private const double InvNameCacheMs = 1000.0;
    private readonly HashSet<int> _compSkipLogged = new();

    // Predictive component gate is OFF: the dat formula is the full historical
    // recipe, not ACE's actual (reduced) requirement, so it false-rejected
    // every spell. Empirical no-components learning + persistence handles it
    // reliably instead. See the long note in TrySpellByName.
    private const bool EnablePredictiveComponentGate = false;

    private static readonly Dictionary<string, string[]> DebuffSpells = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fester",    new[] { "Fester Other",                    "Decrepitude's Grasp" } },
        { "Broadside", new[] { "Missile Weapon Ineptitude Other", "Broadside of a Barn" } },
        { "Gravity",   new[] { "Vulnerability Other",             "Gravity Well" } },
        { "Imperil",   new[] { "Imperil Other",                   "Gossamer Flesh" } },
        { "Yield",     new[] { "Magic Yield Other",               "Yield" } },
    };

    // The Void debuffs (2026-10-27): "{base} I".."VII" and "Incantation of {base}" in SpellData.txt,
    // cast with Void Magic. Corrosion and Corruption are projectiles that put their Nether damage
    // over time on what they hit (Corruption flies as a blast); Destructive Curse, Weakening Curse
    // and Festering Curse land directly. Keyed "Void:{base}" in the debuff list.
    internal static readonly string[] VoidDebuffBases =
        { "Corrosion", "Corruption", "Destructive Curse", "Weakening Curse", "Festering Curse" };

    // Shape index into the SpellShapes / VoidSpellShapes rows. ShapeWall (2026-10-08) has no row:
    // walls have no tiered base names and are found by spell category (WallCategories).
    internal const int ShapeArc = 0, ShapeRing = 1, ShapeStreak = 2, ShapeBolt = 3, ShapeBlast = 4, ShapeWall = 5;

    // Wall spells per element: ACE SpellCategory AcidWall 229, BludgeoningWall 230, ColdWall 231,
    // ElectricWall 232, FireWall 233, PiercingWall 234, SlashingWall 235 (column 3 of SpellData.txt).
    // Retail has one per element (Blistering Creeper, Hammering Crawler, Foon-Ki's Glacial Floe,
    // Os' Wall, Slithering Flames, Spike Strafe, Bed of Blades: power 200, so level 5 by ACE's
    // server formula); the categories also hold other spells (Demon's Tongues, Burning Earth, the
    // "... Whip" spells) that a character on some server may know, so a wall is picked by category,
    // not by name. Void (Nether) has no walls.
    private static readonly Dictionary<string, int> WallCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Acid", 229 }, { "Bludgeon", 230 }, { "Cold", 231 }, { "Lightning", 232 },
        { "Fire", 233 }, { "Pierce", 234 }, { "Blade", 235 }, { "Slash", 235 },
    };

    // Base names per element, by shape: Arc, Ring, Streak, Bolt, Blast. Every non-ring base has
    // "{base} I".."VI" (blasts: III-VI only, retail has no blast I/II) and "Incantation of {base}"
    // in SpellData.txt (checked by the host test "attack spells: every element and shape").
    // Fixed 2026-10-03: Lightning's bolt was "Shock Wave" (the bludgeon bolt), Bludgeon's arc
    // "Bludgeoning Arc" (is "Shock Arc"), the Blade/Slash streak "Blade Streak" (is "Whirling
    // Blade Streak") and the Bludgeon streak "Bludgeoning Streak" (is "Shock Wave Streak"): none
    // of those names exist, so those shapes cast the wrong element or only their tier-7 lore.
    private static readonly Dictionary<string, string[]> SpellShapes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Flame Arc", "Ring of Fire", "Flame Streak", "Flame Bolt", "Flame Blast" } },
        { "Cold",      new[] { "Frost Arc", "Frost Ring", "Frost Streak", "Frost Bolt", "Frost Blast" } },
        { "Lightning", new[] { "Lightning Arc", "Shock Ring", "Lightning Streak", "Lightning Bolt", "Lightning Blast" } },
        { "Acid",      new[] { "Acid Arc", "Acid Ring", "Acid Streak", "Acid Stream", "Acid Blast" } },
        { "Blade",     new[] { "Blade Arc", "Blade Ring", "Whirling Blade Streak", "Whirling Blade", "Blade Blast" } },
        { "Pierce",    new[] { "Force Arc", "Force Ring", "Force Streak", "Force Bolt", "Force Blast" } },
        { "Bludgeon",  new[] { "Shock Arc", "Bludgeoning Ring", "Shock Wave Streak", "Shock Wave", "Shock Blast" } },
        { "Slash",     new[] { "Blade Arc", "Blade Ring", "Whirling Blade Streak", "Whirling Blade", "Blade Blast" } },
    };

    // War Streak/Bolt/Blast lines have NO "{base} VII" — their tier-7 is a
    // lore-named spell (Arc uses Roman "VII"; Ring uses RingLoreNames).
    // [0] = Streak VII lore, [1] = Bolt VII lore, [2] = Blast VII lore. Names
    // verified against SpellData.txt skill-300 entries 2026-05-17 (e.g. Force
    // Streak VII = "Outlander's Insolence" id 2133); blasts 2026-10-03 (power
    // 325, the blast category: e.g. Flame Blast VII = "Silencia's Scorn" id 2127).
    // Slash shares the Blade family.
    private static readonly Dictionary<string, string[]> WarTier7Lore = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire",      new[] { "Sizzling Fury",          "Ilservian's Flame", "Silencia's Scorn" } },
        { "Cold",      new[] { "Sudden Frost",           "Icy Torment",       "Winter's Embrace" } },
        { "Lightning", new[] { "Lhen's Flare",           "Alset's Coil",      "Luminous Wrath" } },
        { "Acid",      new[] { "Corrosive Flash",        "Disintegration",    "Dissolving Vortex" } },
        { "Blade",     new[] { "Rending Wind",           "Evisceration",      "Sau Kolin's Sword" } },
        { "Slash",     new[] { "Rending Wind",           "Evisceration",      "Sau Kolin's Sword" } },
        { "Pierce",    new[] { "Outlander's Insolence",  "The Spike",         "Stinging Needles" } },
        { "Bludgeon",  new[] { "Cameron's Curse",        "Crushing Shame",    "Pummeling Storm" } },
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

    /// <summary>The level-5 rings without a lore name (SpellData 3805-3808); Cold, Blade and Bludgeon have none.</summary>
    private static readonly Dictionary<string, string> PlainRings = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Fire", "Flame Ring" }, { "Acid", "Acid Ring" }, { "Pierce", "Force Ring" }, { "Lightning", "Lightning Ring" },
    };

    private static readonly Dictionary<string, string[]> VoidSpellShapes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Nether", new[] { "Nether Arc", "Nether Ring", "Nether Streak", "Nether Bolt", "Nether Blast" } },
        { "Fire",   new[] { "Corrosion Arc", "Corrosion Ring", "Corrosion Streak", "Nether Bolt", "Nether Blast" } },
    };

    // In-world war/void projectiles are named exactly the spell's shape base
    // (e.g. "Flame Bolt", "Frost Streak"). The object cache sometimes mis-
    // classifies these transient objects as Monster and they carry no health
    // record, so the combat scanner would lock onto the bot's OWN projectile,
    // burn casts on it (no damage possible) and blacklist it. No real
    // attackable monster is ever named one of these — they are excluded from
    // target selection. Built once from the authoritative shape tables.
    private static readonly HashSet<string> ProjectileNames = BuildProjectileNames();

    private static HashSet<string> BuildProjectileNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arr in SpellShapes.Values)
            foreach (var n in arr) set.Add(n);
        foreach (var arr in VoidSpellShapes.Values)
            foreach (var n in arr) set.Add(n);
        return set;
    }

    private static bool IsSpellProjectileName(string? name)
        => !string.IsNullOrEmpty(name) && ProjectileNames.Contains(name);

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

    // Crit detection: AC may send the crit indicator inline ("...critically...")
    // or as a separate line just before the damage line — track the last crit
    // line so a damage line arriving within the window is credited as a crit.
    private DateTime _lastCritChatAt = DateTime.MinValue;
    private const double CRIT_WINDOW_MS = 600;
    private int _dmgDbgCount;

    /// <summary>
    /// Parse OUR outgoing damage out of the combat-log chat and feed it to the
    /// per-monster crit/non-crit learning. War magic sends NO AttackerNotification
    /// (0x01B1), so the chat line "You blast X for N points of fire damage!" is the
    /// only damage source for casters — this is what fills the Crit/NonCrit columns.
    /// MAGIC-ONLY: melee/missile get the structured 0x01B1 event (OnCombatDamage),
    /// so parsing chat for them too would double-count.
    /// </summary>
    // "Olthoi Swarm Harvester blisters your lower leg for 12 points of acid damage!"
    internal static readonly System.Text.RegularExpressions.Regex IncomingDamage = new(
        @"^(?:Critical hit!\s*)?(?:The )?(?<name>.+?) \S+ (?:you|your [a-z ]+?) for (?<n>\d+) points? of (?<type>[a-z]+) damage",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static string ElementFromWord(string w) => w.ToLowerInvariant() switch
    {
        "slashing" or "slash" => "Slash",
        "piercing" or "pierce" => "Pierce",
        "bludgeoning" or "bludgeon" => "Bludgeon",
        "fire" => "Fire",
        "cold" => "Cold",
        "acid" => "Acid",
        "electric" or "lightning" => "Lightning",
        "nether" => "Nether",
        _ => "Physical",
    };

    /// <summary>The wcid of a monster we've seen by <paramref name="name"/>, else the current fight's when the name matches it.</summary>
    private uint WcidForName(string name)
    {
        if (_seenMonsterNameToWcid.TryGetValue(name, out uint w)) return w;
        if (_fightTargetWcid != 0 && name.Equals(_fightTargetName, StringComparison.OrdinalIgnoreCase)) return _fightTargetWcid;
        return 0;
    }

    /// <summary>
    /// Misses and damage taken, for the Damage tab's detail panel. A miss is an attack the
    /// monster evaded ("X evaded your attack.") or a spell it resisted ("X resists your
    /// spell"), counted against the weapon in hand. Damage taken comes from the monster's
    /// hit lines, which name it (the engine's defender event doesn't).
    /// </summary>
    private void LearnFromCombatChat(string text)
    {
        int i = text.IndexOf(" evaded your attack", StringComparison.OrdinalIgnoreCase);
        bool resisted = false;
        if (i < 0) { i = text.IndexOf(" resists your spell", StringComparison.OrdinalIgnoreCase); resisted = i >= 0; }
        if (i > 0)
        {
            string who = text.Substring(0, i).Trim();
            if (who.StartsWith("The ", StringComparison.Ordinal)) who = who.Substring(4);
            uint wcid = WcidForName(who);
            uint weapon = resisted || CurrentCombatMode == CombatMode.Magic
                ? _lastCastWeaponId : unchecked((uint)_equippedWeaponId);
            if (wcid != 0) _damageStore!.RecordMiss(weapon, wcid, who);
            return;
        }

        if (text.StartsWith("You ", StringComparison.Ordinal)) return;   // our own hits and evades
        var m = IncomingDamage.Match(text);
        if (!m.Success) return;
        string name = m.Groups["name"].Value.Trim();
        uint src = WcidForName(name);
        if (src == 0) return;
        if (int.TryParse(m.Groups["n"].Value, out int amount))
        {
            _damageStore!.RecordTaken(src, name, ElementFromWord(m.Groups["type"].Value), amount);
            LearnTakenSplit(src, name, amount);   // by shield and facing (CombatManager.ShieldFacing.cs)
        }
    }

    /// <summary>A chat line for the damage meter, the stall watchdog and the environment throttle.
    /// <paramref name="chatType"/> is the line's eChatTypes (-1 = not known).</summary>
    public void HandleChatForDamage(string text, int chatType = -1)
    {
        if (string.IsNullOrEmpty(text)) return;
        // Combat lines come from the server; one in a player's chat type is someone's imitation.
        if (RynthCore.Plugin.Shared.ChatSource.IsPlayerAuthored(chatType)) return;
        // An arrow into a wall: the environment-impact throttle's missile signal.
        if (NoteMissileEnvironmentChat(text)) return;
        // A miss is a swing too (stall watchdog). "You evaded X!" is the monster's miss, not ours.
        if (text.IndexOf(" evaded your attack", StringComparison.OrdinalIgnoreCase) > 0)
            NoteSwingEvidence("miss");
        NoteAttackReachedFromChat(text);
        if (_damageStore == null) return;
        try { LearnFromCombatChat(text); } catch { }

        DateTime now = RynthClock.Now;
        bool critLine = text.IndexOf("critical", StringComparison.OrdinalIgnoreCase) >= 0;
        if (critLine) _lastCritChatAt = now;

        if (!CombatText.TryParseOutgoingHit(text, out string victim, out int amount)) return;
        NoteEnvironmentEvidence();   // a cast that hit something didn't only hit the environment
        bool crit = critLine || (now - _lastCritChatAt).TotalMilliseconds <= CRIT_WINDOW_MS;

        // Which monster the line is about. One spell can hurt several (rings, walls, blasts,
        // chain spells): each sends its own line naming its victim, and only the selected target
        // gets health from the server. A line naming the fight target is the fight's; any other
        // goes to the monster it names (CreditHitToOther), never to the fight target.
        int victimId = ResolveHitVictim(victim, now);
        bool cur = _fightTargetWcid != 0;
        bool onFight = cur && (victimId != 0
            ? victimId == _fightTargetId
            : victim.Equals(_fightTargetName, StringComparison.OrdinalIgnoreCase));
        // Fight already cleared (we one-shot and moved on before the line landed): the line
        // belongs to the last fight when it names it.
        bool onLastFight = !onFight && !cur && _lastFightWcid != 0
            && victim.Equals(_lastFightName, StringComparison.OrdinalIgnoreCase);

        if (_dmgDbgCount < 15)
        {
            _dmgDbgCount++;
            _host.Log($"[DmgDbg] amt={amount} crit={crit} victim='{victim}' id=0x{(uint)victimId:X8} fight={(onFight ? 1 : onLastFight ? 2 : 0)} mode={CurrentCombatMode} text='{text}'");
        }

        // A hit on the fight target is damage for the no-damage judge and the no-progress timer,
        // as a hit on any other monster is (CreditHitToOther). The server sends health only for
        // the client's selection: once the player selects something else (a corpse, themselves,
        // an item), the fight target's health stops coming, and this line is the only sign a war
        // spell landed. Without it, three casts that hit after the player clicked a corpse were
        // "no damage": the live monster was blacklisted and combat stopped (2026-10-21, Conquest).
        if (onFight && amount > 0)
        {
            ReportDamageOnTarget(_fightTargetId);
            NoteFightHit(amount);   // the kill-shot health check's clock; calls off a swap the hit disproves
        }
        // A damage line is never a killing blow's (ACE sends the death message then), so a hit on
        // a monster we swapped off as a predicted kill says it is alive (R-20261025-183343-727b).
        if (amount > 0 && victimId != 0 && victimId != _fightTargetId)
            NotePredictedKillSurvived(victimId, amount);

        if (!onFight && !onLastFight)
        {
            CreditHitToOther(victimId, victim, amount, crit);
            return;
        }

        uint wcid = onFight ? _fightTargetWcid : _lastFightWcid;

        // MAGIC-ONLY: melee/missile get the structured 0x01B1 event (OnCombatDamage),
        // so parsing chat for them too would double-count.
        if (CurrentCombatMode != CombatMode.Magic || wcid == 0) return;

        // Feed the kill-shot predictor. War magic gets no 0x01B1, so this chat
        // line is the ONLY damage signal for casters — without accumulating it,
        // _fightDamage stayed 0: EvaluateKillShot path B always saw a full-HP
        // target and RecordKill(totalDamage:0) never learned HP pools.
        if (onFight)
            _fightDamage += amount;

        uint   weapon = onFight ? _lastCastWeaponId : _lastFightWeaponId;
        string nm     = onFight ? _fightTargetName  : _lastFightName;
        string elem   = onFight ? _lastCastElement  : _lastFightElement;
        int    tier   = onFight ? _lastCastTier     : _lastFightTier;
        _damageStore.RecordHit(weapon, wcid, nm, elem, tier, amount, crit);
    }

    // ── Hits on monsters other than the fight target ─────────────────────────
    // A ring, wall, blast or chain spell hurts several monsters with one cast; each gets a
    // damage line naming it. Same-named monsters (a swarm) are told apart by order: within one
    // volley (lines no more than VolleyWindowMs apart) a monster is hit once, so the fight
    // target takes the first line with its name and every further one goes to the nearest
    // other monster of that name not yet hit in the volley.
    private readonly Dictionary<int, DateTime> _hitAt = new();   // last line credited to each id
    private const double VolleyWindowMs = 800;
    private const double HitMemoryMs = 15000;                    // hits older than this are forgotten
    private const double KillMatchRecentHitMs = 5000;            // a kill notice is matched to hits this recent

    /// <summary>
    /// The object a damage line naming <paramref name="name"/> is about, or 0 when no live monster
    /// of that name is known. Stamps the hit so the next same-named line of the volley goes elsewhere.
    /// </summary>
    internal int ResolveHitVictim(string name, DateTime now)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        PruneHits(now);
        int fight = _fightTargetId;
        // A swing or a shot hits the one monster it was aimed at, so in Melee and Missile mode
        // every line naming the fight target is the fight target's. The volley rule below is for
        // spells that hurt several monsters at once. Applied to melee, a dual-wield or multi-strike
        // weapon's second hit (about 0.3 s after the first, inside the volley window) was handed
        // to the nearest other monster of the same name: that monster was marked hurt and "hit by
        // us", and the next kill notice was matched to it. It was kept out of the scan for 4 s
        // while it was alive and fighting, so the bot looted mid-fight or turned to lesser
        // monsters (2026-11-01, a same-named pack).
        bool singleTarget = IsSingleTargetMode();
        if (fight != 0 && (singleTarget || !HitInVolley(fight, now))
            && string.Equals(_worldFilter[fight]?.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            _hitAt[fight] = now;
            return fight;
        }
        // Between fights (the target just died) a melee or missile line naming the last fight's
        // monster is that fight's last hit, not a neighbour's.
        if (singleTarget && fight == 0 && _lastFightName.Length > 0
            && string.Equals(_lastFightName, name, StringComparison.OrdinalIgnoreCase))
            return 0;

        int anchor = fight != 0 ? fight : unchecked((int)_playerId);
        int best = 0, any = 0;
        double bestD = double.MaxValue, anyD = double.MaxValue;
        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.ObjectClass != AcObjectClass.Monster) continue;
            if (!string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f) continue;   // the server said dead
            double d = anchor != 0 ? _worldFilter.Distance(anchor, wo.Id) : 0.0;
            if (any == 0 || d < anyD) { any = wo.Id; anyD = d; }
            if (wo.Id == fight || HitInVolley(wo.Id, now)) continue;
            if (best == 0 || d < bestD) { best = wo.Id; bestD = d; }
        }
        int id = best != 0 ? best : any;
        if (id != 0) _hitAt[id] = now;
        return id;
    }

    private bool HitInVolley(int id, DateTime now)
        => _hitAt.TryGetValue(id, out DateTime t) && (now - t).TotalMilliseconds <= VolleyWindowMs;

    /// <summary>Melee or Missile mode: each attack hits only the monster it was aimed at.</summary>
    private bool IsSingleTargetMode()
    {
        int mode = CurrentCombatMode;
        return mode == CombatMode.Melee || mode == CombatMode.Missile;
    }

    private readonly List<int> _hitPruneScratch = new();
    private void PruneHits(DateTime now)
    {
        if (_hitAt.Count < 64) return;
        _hitPruneScratch.Clear();
        foreach (var kv in _hitAt)
            if ((now - kv.Value).TotalMilliseconds > HitMemoryMs) _hitPruneScratch.Add(kv.Key);
        foreach (int id in _hitPruneScratch) _hitAt.Remove(id);
    }

    /// <summary>
    /// A hit on a monster that isn't the fight target (another target of a ring, wall or chain
    /// spell): lower its health estimate (the server sends it none), count it as damage for the
    /// blacklist, and learn the hit against its own monster type.
    /// </summary>
    private void CreditHitToOther(int victimId, string victim, int amount, bool crit)
    {
        uint wcid = 0;
        if (victimId != 0)
        {
            if (_host.HasGetObjectWcid) _host.TryGetObjectWcid(unchecked((uint)victimId), out wcid);
            double maxHp = ResolveMaxHpFor(victimId, wcid, victim);
            _worldFilter.ApplyDamageEstimate(victimId, maxHp > 0 ? amount / maxHp : 0.0);
            ReportDamageOnTarget(victimId);
        }
        if (wcid == 0) wcid = WcidForName(victim);
        if (_damageStore != null && wcid != 0 && CurrentCombatMode == CombatMode.Magic)
            _damageStore.RecordHit(_lastCastWeaponId, wcid, victim, _lastCastElement, _lastCastTier, amount, crit);
    }

    /// <summary>A monster's max health from what we know: the manual override, the engine's vitals,
    /// the shared creature profiles, the learned damage-to-kill. 0 when none says.</summary>
    private double ResolveMaxHpFor(int id, uint wcid, string name)
    {
        if (_damageStore != null && wcid != 0)
        {
            double manual = _damageStore.GetManualHp(wcid);
            if (manual > 0) return manual;
        }
        if (_host.HasGetTargetVitals
            && _host.TryGetTargetVitals(unchecked((uint)id), out _, out uint maxHealth, out _, out _, out _, out _)
            && maxHealth > 0)
            return maxHealth;
        if (_creatureStore != null && !string.IsNullOrEmpty(name))
        {
            CreatureProfile? prof = null;
            bool found = wcid != 0 ? _creatureStore.TryGet(name, wcid, out prof) : _creatureStore.TryGetByName(name, out prof);
            if (found && prof != null && prof.MaxHealth > 0) return prof.MaxHealth;
        }
        if (_damageStore != null && wcid != 0)
        {
            double hp = _damageStore.GetLearnedHp(wcid);
            if (hp > 0) return hp;
        }
        return 0;
    }

    /// <summary>
    /// The monster a kill notice is about, among those we hit in the last few seconds (other than
    /// <paramref name="exclude"/>): the longest name the message contains, then the most hurt,
    /// then the most recently hit. 0 when none fits.
    /// </summary>
    private int FindRecentlyHitVictim(string deathMessage, int exclude, DateTime now)
    {
        int best = 0, bestLen = 0;
        float bestHp = 2f;
        DateTime bestAt = DateTime.MinValue;
        foreach (var kv in _hitAt)
        {
            if (kv.Key == exclude || (now - kv.Value).TotalMilliseconds > KillMatchRecentHitMs) continue;
            if (_recentlyKilled.ContainsKey(kv.Key)) continue;
            string n = _worldFilter[kv.Key]?.Name ?? "";
            if (n.Length == 0 || deathMessage.IndexOf(n, StringComparison.OrdinalIgnoreCase) < 0) continue;
            float hp = _worldFilter.GetHealthRatio(kv.Key);
            if (hp < 0f) hp = 1f;
            bool better = n.Length > bestLen
                || (n.Length == bestLen && (hp < bestHp || (hp == bestHp && kv.Value > bestAt)));
            if (!better) continue;
            best = kv.Key; bestLen = n.Length; bestHp = hp; bestAt = kv.Value;
        }
        return best;
    }

    /// <summary>
    /// Extract our outgoing damage amount from a combat-log line. Two wordings reach us:
    ///   • Retail combat log:  "You blast X for 153 points of fire damage!"
    ///   • ACE war/void magic: "You blast X for 153 points with Whirling Blade VII."
    ///       (ACEmulator's SpellProjectile.DamageTarget emits "for {amount} points with
    ///        {Spell.Name}" — NOT "points of <element> damage" — so the old retail-only
    ///        parser matched nothing on ACE and the Crit/NonCrit columns stayed empty.)
    /// Returns false for incoming lines ("... you for N points ...", "... your lower leg for ..."):
    /// the sentence must start with "You" after any "Critical hit!"-style prefix. Also false for
    /// non-damage "points of &lt;vital&gt;" lines (heals / mana / stamina drains).
    /// Shared with RynthTracker (Plugins/Shared/CombatText.cs), which also reads the victim's name.
    /// </summary>
    internal static bool TryParseOutgoingDamage(string text, out int amount)
        => CombatText.TryParseOutgoingHit(text, out _, out amount);

    public void HandleChatForDebuffs(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Offensive (war/void) cast resolution — runs independent of the debuff
        // gate (this handler is called for every chat line). Mirrors
        // BuffManager's categorisation so a war spell the char can't actually
        // cast gets marked unresolvable and FindBestOffensiveSpellId drops to
        // an alternative, instead of silently re-firing the dead spell forever.
        // A script's cast (RynthAi.Combat) is remembered on its own: it may not have armed the
        // pending judgement (an area, nojudge or off-target cast) yet must not be re-cast forever.
        NoteScriptCastRefusal(text);
        if (_pendingOffensiveSpellId != 0)
        {
            string lo = text.ToLowerInvariant();

            // "You're too busy!" — server refused the cast mid-gesture (a
            // previous cast is still animating). NOT a real cast: must NOT
            // count toward the blacklist miss streak. Clear pending silently
            // so JudgePendingOffensiveCast's 500ms no-too-busy window stays
            // honest, and the next attempt can be issued under the throttle.
            if (lo.Contains("you're too busy") || lo.Contains("you are too busy"))
            {
                _pendingOffensiveSpellId   = 0;
                _pendingOffensiveTargetId  = 0;
                // The cast was REFUSED (a prior gesture is still animating) — it
                // never fired, so undo the casts-to-kill increment that issuance
                // optimistically added. The blacklist counter already excludes
                // refusals; without this, casts-to-kill reads >1 on pure one-shots.
                if (_fightCastCount > 0) _fightCastCount--;
                // Cancel any armed predicted kill-shot swap — the cast that armed
                // it was refused, so the mob is still alive: keep + retry it
                // instead of advancing. This is the core of the anti-wedge fix.
                _predictKillSwapArmed = false;
            }
            // Hard reject: no components / unknown / no target. Same phrases the
            // buff path treats as a hard rejection. Mark unresolvable so the
            // tier loop drops down (faster than waiting out the no-chat valve).
            else if (lo.Contains("missing some required") ||
                lo.Contains("you do not have the") ||
                lo.Contains("have all the components for this spell"))
            {
                _spellManager?.MarkSpellUnresolvable(_pendingOffensiveSpellId);
                _host.Log($"[CombatCast] NO-COMPONENTS id={_pendingOffensiveSpellId} " +
                          $"'{SpellTableStub.GetById(_pendingOffensiveSpellId)?.Name}' — '{text.Trim()}'. " +
                          $"Marked unresolvable → tiering down to an alternative.");
                _pendingOffensiveSpellId = 0;
                _predictKillSwapArmed = false; // the cast never went out — don't swap off a live mob
            }
            else
            {
                // Success / fizzle / resisted ⇒ the char DOES know it and has
                // components — clear pending, never mark. Match the cast id to
                // ours so a debuff's "ou cast" can't falsely clear it; on a
                // name-lookup miss, leave pending (the no-chat valve backstops).
                int ci = text.IndexOf("ou cast ", StringComparison.OrdinalIgnoreCase);
                if (ci >= 0)
                {
                    string after = text.Substring(ci + 8);
                    int onI = after.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
                    int cI  = after.IndexOf(',');
                    int e   = after.Length;
                    if (onI > 0) e = onI;
                    if (cI > 0 && cI < e) e = cI;
                    int castId = SpellDatabase.GetIdByName(after.Substring(0, e).Trim());
                    if (castId == _pendingOffensiveSpellId)
                    {
                        // ACE doesn't emit "You cast X" for offensive war
                        // magic, so on ACE this branch is effectively dead —
                        // the successful-cast detection happens via
                        // JudgePendingOffensiveCast (no "too busy" within
                        // OffensiveRefusalWindowMs of the issue). This branch
                        // is kept so forks/retail that DO emit chat still
                        // clear pending; it must NOT itself call
                        // RecordOffensiveCast or the cast would double-count.
                        _pendingOffensiveSpellId   = 0;
                        _pendingOffensiveTargetId  = 0;
                    }
                }
                else if (lo.Contains("fizzle") || lo.Contains("resists your spell"))
                {
                    _pendingOffensiveSpellId = 0;
                    _predictKillSwapArmed = false; // cast didn't land — don't swap off a live mob
                }
            }
        }

        if (!_waitingForDebuffResult) return;

        // AC strips the leading 'Y' from cast-confirmation chat — text arrives as
        // "ou cast …". IndexOf matches both "You cast" and "ou cast".
        if (text.IndexOf("ou cast ", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            NoteDebuffLanded();
            _waitingForDebuffResult = false;
            _lastSpellCast = RynthClock.Now;
            return;
        }

        if (text.IndexOf("fizzle", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            NoteDebuffFailed("Fizzled");
            return;
        }

        if (text.IndexOf("resists your spell", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            NoteDebuffFailed("Resisted");
            return;
        }

        // Refused mid-gesture: the debuff never went out, so don't let the result timeout
        // mark it as landed. Retry it (counted, so a refusal loop can't run forever).
        if (text.IndexOf("you're too busy", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("you are too busy", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            NoteDebuffFailed("Refused");
            return;
        }
    }

    /// <summary>
    /// A debuff didn't land (fizzle, resist, refusal). Recast it, but only up to
    /// DebuffTimers.MaxFailures times per target: a mob the character's Creature Magic can't land
    /// on used to get the same debuff forever, and the bot never reached its attack spells
    /// (debuffs don't feed the no-damage blacklist either).
    /// </summary>
    private void NoteDebuffFailed(string what)
    {
        if (_debuffs.Failed(_pendingDebuffTargetId, _pendingDebuffKey ?? ""))   // skip it for this target
        {
            _host.WriteToChat($"[RynthAi] {what}: {_pendingDebuffKey} — {DebuffTimers.MaxFailures} tries on this target, skipping it", 2);
        }
        else
        {
            _host.WriteToChat($"[RynthAi] {what}: {_pendingDebuffKey} — recasting", 2);
        }
        _waitingForDebuffResult = false;
        _lastSpellCast = RynthClock.Now;
    }

    /// <summary>
    /// The pending debuff landed (or its result never came, taken as landed): timed by the
    /// spell's duration so it is cast again just before it wears off; a spell with no duration
    /// is cast once per target, as all of them were before 2026-10-20. Archmage's Endurance
    /// isn't counted, so with it a renewal comes early rather than late.
    /// </summary>
    private void NoteDebuffLanded()
    {
        int seconds = SpellDatabase.DurationSeconds(_pendingDebuffSpellId);
        _debuffs.Landed(_pendingDebuffTargetId, _pendingDebuffKey ?? "", seconds, RynthClock.Now);
        if (seconds > 0)
            _host.Log($"[CombatCast] debuff {_pendingDebuffKey} on 0x{(uint)_pendingDebuffTargetId:X8}: lasts {seconds} s, renewed in {seconds - DebuffTimers.Lead(seconds):0} s");
    }

    // ── Position re-sync after casts that land nothing (2026-10-12) ──────────────────────────
    // Flight 63260: after a stray stop burst rode with a cast, every projectile cast (Corruption
    // VII, Nether Arc VII) was answered - components burned, UseDone - and nothing landed, until a
    // real sidestep (12:32:13) ended it. ACE launches projectiles from the caster's SERVER physics
    // position and drops a spawn that fails or collides at once without a word; it re-syncs that
    // position only when a reported position differs from the last one (Player_Tick
    // UpdatePlayerPosition), and standing still reports the same spot. So after
    // ResyncAfterMagicMisses magic casts in a row with no damage (any target: swaps between two
    // monsters don't reset it), RynthAi takes one short real sidestep - a movement key down and
    // up through the client (MoveToState + AutonomousPositions), alternating sides - once the
    // server has answered the last cast, and casts nothing until it is done. At most once per
    // ResyncCooldownMs.
    internal const int ResyncAfterMagicMisses = 3;
    internal const double ResyncStepMs = 300;
    internal const double ResyncSettleMs = 400;
    internal const double ResyncCooldownMs = 30_000;
    private const uint MotionSideStepRight = 0x6500000F;
    private const uint MotionSideStepLeft = 0x65000010;
    private int _magicMissesSinceHit;
    private int _resyncState;                 // 0 idle, 1 wanted, 2 stepping, 3 settling
    private DateTime _resyncAt = DateTime.MinValue;
    private DateTime _lastResyncAt = DateTime.MinValue;
    private uint _resyncMotion;
    private bool _resyncLeftNext;
    internal int ResyncSteps { get; private set; }
    internal bool ResyncActive => _resyncState != 0;

    private void NoteMagicMissForResync(int tid)
    {
        _magicMissesSinceHit++;
        if (_resyncState != 0 || _magicMissesSinceHit < ResyncAfterMagicMisses) return;
        DateTime now = TargetClock();
        if (_lastResyncAt != DateTime.MinValue && (now - _lastResyncAt).TotalMilliseconds < ResyncCooldownMs) return;
        if (!_host.HasSetMotion) return;
        _resyncState = 1;
        _resyncAt = now;
        _host.Log($"[CombatCast] {_magicMissesSinceHit} magic casts in a row landed nothing (last at 0x{(uint)tid:X8}) - " +
                  "one short sidestep to re-sync the server's copy of our position before the next cast");
    }

    /// <summary>
    /// The re-sync sidestep, one step per call (Think, before a cast). True while it runs: no cast.
    /// Starts only when no server action is unanswered and the character isn't animating.
    /// </summary>
    internal bool TickResyncStep()
    {
        if (_resyncState == 0) return false;
        DateTime now = TargetClock();
        switch (_resyncState)
        {
            case 1:
                if (ActionPacer.Enabled
                    && (ActionPacer.Current(_host) != null || ActionPacer.GestureHolds(_host, "Combat/Resync", HealNeed.None, out _)))
                {
                    if ((now - _resyncAt).TotalMilliseconds > 10_000) { _resyncState = 0; _host.Log("[CombatCast] re-sync sidestep given up: the server never went idle"); }
                    return true;
                }
                _resyncMotion = _resyncLeftNext ? MotionSideStepLeft : MotionSideStepRight;
                _resyncLeftNext = !_resyncLeftNext;
                if (!_host.SetMotionBy("Combat/Resync", _resyncMotion, true)) { _resyncState = 0; return false; }
                _resyncState = 2;
                _resyncAt = now;
                return true;
            case 2:
                if ((now - _resyncAt).TotalMilliseconds < ResyncStepMs) return true;
                _host.SetMotionBy("Combat/Resync", _resyncMotion, false);
                _resyncState = 3;
                _resyncAt = now;
                return true;
            default:
                if ((now - _resyncAt).TotalMilliseconds < ResyncSettleMs) return true;
                _resyncState = 0;
                _lastResyncAt = now;
                _magicMissesSinceHit = 0;
                ResyncSteps++;
                _host.Log("[CombatCast] re-sync sidestep done");
                return false;
        }
    }

    /// <summary>Test seam: one judged magic cast with no damage.</summary>
    internal void MagicMissForTest(int tid) => NoteMagicMissForResync(tid);

    // ── Back away from a danger point (RynthAi.Hub BackAwayFrom) ──────────────
    // A generic "get out of this radius now", asked for by a server plugin that knows its server's
    // dangers: face the object, run-back jump away from it (Jumper "xs"; facing away and jumping back
    // would leap into it), hold attacks until the jump settles, then re-engage the same target.
    // RynthILT asks for it when Infinite Leaftide's Empyrean Handler channels its lethal ring (the
    // detection and its setting moved there 2026-10-20; Silentkelpie's PR 12). Only while the macro is
    // running with combat on: an idle character, or one driven by hand or by a meta, never has the
    // keyboard taken.

    /// <summary>BackAwayFrom results (RynthAi.Hub BackAwayFrom passes them on as they are).</summary>
    internal const int BackAwayStarted = 1, BackAwayNotNow = 0, BackAwayOutside = 2, BackAwayUnknownObject = 3, BackAwayJumpRefused = -1;

    /// <summary>The jump charge range a caller may ask for (ms).</summary>
    internal const int BackAwayMinHoldMs = 100, BackAwayMaxHoldMs = 3000;

    /// <summary>After the jump: turn, land and one re-engage tick before attacks resume (ms).</summary>
    private const int BackAwaySettleMs = 2_200;

    private DateTime _backAwayUntil = DateTime.MinValue;
    private bool _backAwayJumpStarted;

    /// <summary>
    /// Plugin wires this to <c>Jumper.Start("xs", faceHeading, ms)</c> — face the danger, then
    /// run-back jump (x+s) so the body moves away. Returns false if the jumper refused.
    /// </summary>
    public Func<float, int, bool>? RequestBackJump { get; set; }

    /// <summary>True while the wired jumper is mid-turn / charge / settle.</summary>
    public Func<bool>? JumpBusy { get; set; }

    /// <summary>True while a back-away is holding combat (jump or settle).</summary>
    public bool BackAwayActive =>
        _backAwayUntil != DateTime.MinValue && RynthClock.Now < _backAwayUntil;

    /// <summary>
    /// Backs the character away from <paramref name="objectId"/> when it is within the danger radius:
    /// <paramref name="radiusYards"/>, or Ring Range when that is larger (anything a ring reaches).
    /// <paramref name="holdMs"/> is the jump's charge (clamped to 100-3000 ms). Cancels the swing or
    /// cast in progress, keeps the target. <paramref name="reason"/> is only for the log. Returns a
    /// BackAway* result: started, not now (macro stopped, combat off, already backing away), outside
    /// the radius, unknown object (no position), or the jump didn't start. Pump thread.
    /// </summary>
    public int BackAwayFrom(int objectId, double radiusYards, int holdMs, string reason)
    {
        string why = string.IsNullOrWhiteSpace(reason) ? "a server plugin's request" : reason.Trim();
        if (!_settings.IsMacroRunning || !_settings.EnableCombat) return BackAwayNotNow;
        if (BackAwayActive) return BackAwayNotNow;
        uint me = _host.GetPlayerId();
        if (me == 0 || objectId == 0) return BackAwayUnknownObject;
        double dist = _worldFilter.Distance(unchecked((int)me), objectId);
        if (dist < 0 || dist == double.MaxValue || double.IsNaN(dist))
        {
            _host.Log($"[CombatEvade] Back away ({why}): 0x{objectId:X8} has no position - no jump.");
            return BackAwayUnknownObject;
        }
        string name = _worldFilter[objectId]?.Name ?? $"0x{objectId:X8}";
        double danger = Math.Max(double.IsNaN(radiusYards) ? 0 : radiusYards, Math.Max(1, _settings.RingRange));
        if (dist > danger)
        {
            _host.Log($"[CombatEvade] Back away ({why}): '{name}' at {dist:0.0} m (outside {danger:0.#} m) - staying put.");
            return BackAwayOutside;
        }

        // Face the danger, then Jumper "xs" (back + run) moves the body away from it.
        float face = 0f;
        if (TryGetHeadingTo(objectId, out double toIt, out _))
            face = (float)(toIt % 360.0);
        else if (PlayerCompass.TryGet(_host, out float curr))
            face = curr;

        int hold = Math.Clamp(holdMs, BackAwayMinHoldMs, BackAwayMaxHoldMs);
        _backAwayJumpStarted = false;
        // Hold combat long enough for turn + charge + land + one re-engage tick.
        _backAwayUntil = RynthClock.Now.AddMilliseconds(hold + BackAwaySettleMs);

        // Stop the bound melee/missile attack so the jump isn't eaten by the swing servo.
        try
        {
            if (_host.HasCancelAttack) _host.CancelAttack();
            ReleaseHeldTurn();
        }
        catch { }

        if (RequestBackJump?.Invoke(face, hold) == true)
        {
            _backAwayJumpStarted = true;
            _host.Log($"[CombatEvade] Back away ({why}): back-jump away from '{name}' " +
                      $"at {dist:0.0} m (danger {danger:0.#} m), face {face:0} then xs.");
            return BackAwayStarted;
        }
        _backAwayUntil = DateTime.MinValue;
        _host.Log($"[CombatEvade] Back away ({why}): '{name}' in range, but the back-jump did not start.");
        return BackAwayJumpRefused;
    }

    /// <summary>
    /// Holds Think from attacking while a back-away jump runs. Keeps the target so combat
    /// re-engages as soon as the jumper settles.
    /// </summary>
    private bool TickBackAway()
    {
        if (_backAwayUntil == DateTime.MinValue) return false;
        DateTime now = RynthClock.Now;
        bool jumperBusy = JumpBusy?.Invoke() == true;

        // Jump never started / failed: clear early.
        if (_backAwayJumpStarted && !jumperBusy && now >= _backAwayUntil)
        {
            _backAwayUntil = DateTime.MinValue;
            _backAwayJumpStarted = false;
            _host.Log("[CombatEvade] Back away done - re-engaging.");
            return false;
        }

        if (jumperBusy || now < _backAwayUntil)
        {
            LastCombatSkipReason = "back-away";
            _tickWait = CombatLatencyTracker.Wait.Other;
            return true;
        }

        _backAwayUntil = DateTime.MinValue;
        _backAwayJumpStarted = false;
        _host.Log("[CombatEvade] Back away done - re-engaging.");
        return false;
    }

    public void ReportDamageOnTarget(int targetId)
    {
        _magicMissesSinceHit = 0;   // something we cast landed: the server's position is fine
        // Damage / health change on the cast we're judging → that cast HIT.
        if (targetId == _pendingJudgeTargetId)
            _damageSincePendingCast = true;
        NoteEnvironmentEvidence();
        // Immediate positive feedback on the engaged target clears the streak.
        if (targetId == _lastAttackedTargetId)
            _consecutiveCastMisses = 0;
        _blacklistManager.ClearFailure(targetId);
        if (targetId == activeTargetId || targetId == _lockedTargetId)
            _lastDamageDealtAt = RynthClock.Now;
    }

    /// <summary>
    /// "X evaded your attack." and "X resists your spell" mean the attack reached X, so they
    /// count as feedback for the no-damage blacklist (not as damage: no commitment bonus, no
    /// no-progress reset). Before this an evasive or resistant mob that was fighting the bot
    /// got blacklisted and dropped after three windows of evades (~6 s of melee), and with
    /// nothing else in range the bot went idle and walked on while it kept hitting her.
    /// </summary>
    private void NoteAttackReachedFromChat(string text)
    {
        int i = text.IndexOf(" evaded your attack", StringComparison.OrdinalIgnoreCase);
        if (i < 0) i = text.IndexOf(" resists your spell", StringComparison.OrdinalIgnoreCase);
        if (i <= 0) return;
        NoteEnvironmentEvidence();   // the spell or swing reached a monster
        string who = text.Substring(0, i).Trim();
        if (who.StartsWith("The ", StringComparison.Ordinal)) who = who.Substring(4);
        if (who.Length == 0) return;

        if (_pendingJudgeCastAt != DateTime.MinValue && NameIs(_pendingJudgeTargetId, who))
            _damageSincePendingCast = true;
        if (NameIs(_lastAttackedTargetId, who))
            _consecutiveCastMisses = 0;
    }

    private bool NameIs(int id, string name)
        => id != 0 && string.Equals(_worldFilter[id]?.Name, name, StringComparison.OrdinalIgnoreCase);

    private void NoteSwingEvidence(string kind)
    {
        DateTime now = RynthClock.Now;
        _lastSwingEvidenceAt = now;
        _lastSwingEvidenceKind = kind;
        _attacksSinceEvidence = 0;
        _nativeQuietSince = DateTime.MinValue;   // the native attack's quiet clock (NativeForThisAttack)
        _nativeQuietAttacks = 0;
        switch (kind)
        {
            case "hit":  _lastHitAt  = now; break;
            case "miss": _lastMissAt = now; break;
            case "kill": _lastKillAt = now; break;
        }
    }

    /// <summary>True only when the mob is known to be dead (kill notice, 0 health, corpse).
    /// A mob that is merely out of view may still hold the server's attack, so it's not.</summary>
    private bool IsKnownDead(int id)
    {
        if (id == 0 || _recentlyKilled.ContainsKey(id)) return true;
        if (_worldFilter.GetHealthRatio(id) == 0f) return true;
        var wo = _worldFilter[id];
        return wo != null && (int)wo.ObjectClass != (int)AcObjectClass.Monster;
    }

    /// <summary>
    /// End the server's melee/missile attack (CancelAttack → ACE HandleActionCancelAttack →
    /// OnAttackDone clears MeleeTarget; mid-swing it ends after the swing). The next attack
    /// request then starts a fresh loop on the target the bot actually wants.
    /// </summary>
    private void ReleaseServerAttack(string why, bool log)
    {
        int old = _serverAttackTargetId;
        _serverAttackTargetId = 0;
        if (!_host.HasCancelAttack) return;
        _host.CancelAttack();
        if (!log) return;

        DateTime now = RynthClock.Now;
        if ((now - _lastRebindLogAt).TotalMilliseconds < 2000) { _rebindLogSuppressed++; return; }
        string more = _rebindLogSuppressed > 0 ? $" (+{_rebindLogSuppressed} more since last line)" : "";
        _rebindLogSuppressed = 0;
        _lastRebindLogAt = now;
        string oldDesc = old == 0 ? "none" : $"0x{old:X8} '{_worldFilter[old]?.Name}' {DescribeTargetPos(old)}";
        _host.Log($"[AttackRebind] CancelAttack — server attack was on {oldDesc}; {why}; attacking again next cycle{more}");
    }

    /// <summary>
    /// True if a monster's name matches the user-configured never-attack list
    /// (case-insensitive substring, so "Drudge" excludes every drudge). Blank
    /// entries are ignored. This is the manual do-not-attack list and is distinct
    /// from the automatic no-damage blacklist.
    /// </summary>
    private bool IsUserBlacklistedName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var list = _settings.MonsterNameBlacklist;
        if (list == null || list.Count == 0) return false;
        for (int i = 0; i < list.Count; i++)
        {
            string entry = list[i];
            if (!string.IsNullOrWhiteSpace(entry) &&
                name.IndexOf(entry.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
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

    /// <summary>
    /// Judge the cast awaiting a verdict. A cast becomes a "miss" only once
    /// BlacklistCastSettleMs has elapsed with no damage on the target — so the
    /// damage/health packet has time to land and blacklisting is driven by
    /// confirmed no-damage casts, never wall-clock time. BlacklistAttempts
    /// consecutive misses → blacklist. Called every combat cycle and before
    /// recording a new cast so the last cast is still judged.
    /// </summary>
    // ── No-damage casts: evidence for an intermittent report (2026-10-09) ─────────────
    // A player's mage cast Nether Arc VII again and again (components used, every cast answered)
    // and the target's health never moved; it comes and goes, so it can't be caught by asking
    // for a dump afterwards. When the judge counts BlacklistAttempts no-damage casts in a row,
    // this writes the flight recorder (the casts sent, their targets and every server answer)
    // and one line with what the casts were and where things stood, before the blacklist moves on.
    private string _lastOffensiveCastLine = "";
    private DateTime _lastNoDamageReportAt = DateTime.MinValue;
    private const double NoDamageReportGapMs = 10 * 60_000;   // one report per 10 min per client

    // ── What the server said about each combat cast (2026-10-11) ─────────────────────────────
    // A player log with 30 minutes of casts that landed nothing could not show what the server
    // answered: chat is logged only while a buff is pending ([BuffChat]), never around an attack or
    // debuff cast. A resist, a fizzle, an out-of-range or a silent nothing all looked the same.
    // Now every combat cast logs the lines that arrive within CastChatTraceMs of its send (the
    // incantation, the hit or resist, components, refusals), capped per cast; the monsters'
    // swings at us and our evades of them are left out.
    private int _castTraceSpellId;
    private int _castTraceTargetId;
    private DateTime _castTraceAt = DateTime.MinValue;
    private int _castTraceLines;
    private const double CastChatTraceMs = 4000;
    private const int CastChatTraceMaxLines = 8;

    internal void StartCastChatTrace(int spellId, int targetId)
    {
        _castTraceSpellId = spellId;
        _castTraceTargetId = targetId;
        _castTraceAt = TargetClock();
        _castTraceLines = 0;
    }

    /// <summary>Every chat line goes through here (RynthAiPlugin.OnChatWindowText); logs it when a
    /// combat cast went out in the last <see cref="CastChatTraceMs"/>.</summary>
    internal void TraceCastChat(string? text, int chatType)
    {
        if (_castTraceAt == DateTime.MinValue || string.IsNullOrEmpty(text)) return;
        double ms = (TargetClock() - _castTraceAt).TotalMilliseconds;
        if (ms > CastChatTraceMs) { _castTraceAt = DateTime.MinValue; return; }
        if (_castTraceLines >= CastChatTraceMaxLines) return;
        string line = text.Trim();
        if (line.StartsWith("You evaded ", StringComparison.Ordinal)) return;               // their swing, our evade
        if (!line.StartsWith("You ", StringComparison.Ordinal) && IncomingDamage.IsMatch(line)) return;   // their hit on us
        _castTraceLines++;
        string spell = SpellTableStub.GetById(_castTraceSpellId)?.Name ?? $"spell {_castTraceSpellId}";
        _host.Log($"[CombatChat] +{ms:0} ms after '{spell}' at 0x{(uint)_castTraceTargetId:X8}: type={chatType} '{line}'");
    }

    private void ReportNoDamageCasts(int tid)
    {
        DateTime now = RynthClock.Now;
        if ((now - _lastNoDamageReportAt).TotalMilliseconds < NoDamageReportGapMs) return;
        _lastNoDamageReportAt = now;
        try
        {
            int me = (int)_host.GetPlayerId();
            WorldObject? t = tid != 0 ? _worldFilter[tid] : null;
            double dist = me != 0 && tid != 0 ? _worldFilter.Distance(me, tid) : -1;
            _host.Log($"[CombatCast] NO DAMAGE: {_consecutiveCastMisses} casts in a row at 0x{(uint)tid:X8} '{t?.Name ?? "?"}' " +
                      $"landed nothing (no hit, resist or evade line within {_settings.BlacklistCastSettleMs} ms each). " +
                      $"Last: {_lastOffensiveCastLine}; distance {dist:0.0} m, target {(t == null ? "gone from the client" : "still known")}, " +
                      $"active=0x{(uint)activeTargetId:X8} mode={CurrentCombatMode} busy={BusyCount} castsSinceKill={CastsSinceLastKill}. " +
                      "Flight recorder written (Logs/flight-*.txt).");
        }
        catch { }
        FlightRecorderSignal.Dump(_host, $"casts with no damage at 0x{(uint)tid:X8}");
    }

    /// <summary>
    /// For the no-damage blacklist: <paramref name="tid"/> is the locked target, no swing evidence
    /// came since it was locked (not a hit or evade on anything), it hasn't moved since, and the
    /// server has said nothing about it for <see cref="GhostCheck.NoUpdateMs"/>.
    /// </summary>
    private bool LooksLikeGhost(int tid, out double silentMs)
    {
        silentMs = -1;
        if (tid == 0 || tid != _lockedTargetId || _targetLockedAt == DateTime.MinValue) return false;
        if (_lastSwingEvidenceAt >= _targetLockedAt) return false;
        if (_lockSpot == null || TargetSpot(tid) is not GhostSpot now || now.MovedFrom(_lockSpot.Value)) return false;
        silentMs = ServerSilentMs(tid, RynthClock.Now);
        return silentMs < 0 || silentMs >= GhostCheck.NoUpdateMs;
    }

    private void JudgePendingCast()
    {
        if (_pendingJudgeCastAt == DateTime.MinValue) return;
        if (RaycastWarmingUp) { _pendingJudgeCastAt = DateTime.MinValue; return; }
        if ((RynthClock.Now - _pendingJudgeCastAt).TotalMilliseconds < _settings.BlacklistCastSettleMs)
            return; // not settled yet — give the damage/health packet time

        int tid  = _pendingJudgeTargetId;
        bool hit = _damageSincePendingCast;
        _pendingJudgeCastAt = DateTime.MinValue;

        if (hit) { _consecutiveCastMisses = 0; _magicMissesSinceHit = 0; return; }

        // A swing or shot ACE refused in the air (WeenieError 0x4EB since it went out, Player_Melee /
        // Player_Missile: IsJumping) never happened: not a no-damage attack. A cast's 0x4EB comes as
        // its UseDone and is dropped before it gets here (JudgePendingOffensiveCast).
        if (CurrentCombatMode != CombatMode.Magic && InTheAirErrorSince(_pendingJudgeErrSeq))
        {
            RefusedCastsNotJudged++;
            _host.Log($"[Blacklist] attack at 0x{(uint)tid:X8} refused in the air (0x4EB): not counted as a no-damage attack");
            return;
        }

        if (CurrentCombatMode == CombatMode.Magic) NoteMagicMissForResync(tid);
        _consecutiveCastMisses++;
        if (_consecutiveCastMisses >= _settings.BlacklistAttempts)
        {
            if (CurrentCombatMode == CombatMode.Magic) ReportNoDamageCasts(tid);
            if (CurrentCombatMode != CombatMode.Magic && LooksLikeGhost(tid, out double silentMs))
            {
                _consecutiveCastMisses = 0;
                MarkGhost(tid, _worldFilter[tid]?.Name, silentMs, $"{_settings.BlacklistAttempts} attacks with no swing, no hit and no evade");
                return;
            }
            _blacklistManager.ReportFailure(tid);
            if (_blacklistManager.IsBlacklisted(tid))
            {
                string what = CurrentCombatMode == CombatMode.Magic ? "casts" : "attacks";
                _host.WriteToChat($"[RynthAi] {_consecutiveCastMisses} {what} with no damage — blacklisting 0x{(uint)tid:X8}", 2);
                _consecutiveCastMisses = 0;
                if (tid == activeTargetId)
                    DropTarget($"blacklisted after {_settings.BlacklistAttempts} no-damage {what}");
            }
        }
    }

    /// <summary>
    /// ACE doesn't emit "You cast X" chat for offensive war magic, so the
    /// offensive cast-confirmation works by NEGATION: cast issuance sets
    /// _pendingOffensiveSpellId/CastAt/TargetId; if a "You're too busy!" chat
    /// hits within OffensiveRefusalWindowMs the chat handler clears pending
    /// silently (server refused, don't count); otherwise this method runs
    /// once that window has elapsed and queues the cast for blacklist
    /// judgement (the BlacklistCastSettleMs damage-wait then runs in
    /// JudgePendingCast as usual). Effect: refused spam attempts never reach
    /// _consecutiveCastMisses.
    /// </summary>
    private void JudgePendingOffensiveCast()
    {
        if (_pendingOffensiveSpellId == 0) return;
        if (_pendingOffensiveTargetId == 0) return;
        if (_pendingOffensiveCastAt == DateTime.MinValue) return;
        ObserveOffensiveAnswer();
        if ((RynthClock.Now - _pendingOffensiveCastAt).TotalMilliseconds < OffensiveRefusalWindowMs)
            return;

        // A refusal is not a cast (in a client log, three healthy targets were blacklisted 'after 3
        // no-damage casts' whose casts had all come back UseDone 0x1D):
        // the answer 0x1D, or no answer yet while the server is taken as busy, is not judged.
        // So is 0x4EB, "You can't do that while in the air" (a cast sent in a jump).
        bool inAir = _pendingOffensiveErr == InTheAirErr;
        bool refused = _pendingOffensiveErr == ActionPacer.TooBusyErr || inAir || (_pendingOffensiveErr < 0 && ActionPacer.ServerBusy);
        if (refused)
        {
            RefusedCastsNotJudged++;
            _host.Log($"[Blacklist] cast at 0x{(uint)_pendingOffensiveTargetId:X8} {(inAir ? "refused in the air (UseDone 0x4EB)" : _pendingOffensiveErr >= 0 ? "refused 'too busy' (UseDone 0x1D)" : "unanswered while the server is busy")}: not counted as a no-damage cast");
            _pendingOffensiveSpellId  = 0;
            _pendingOffensiveTargetId = 0;
            _pendingOffensiveCastAt   = DateTime.MinValue;
            return;
        }

        // Refusal window passed without a "too busy" — the server accepted
        // the gesture. Queue this cast for blacklist judgement.
        int targetId = _pendingOffensiveTargetId;
        _pendingOffensiveSpellId   = 0;
        _pendingOffensiveTargetId  = 0;
        _pendingOffensiveCastAt    = DateTime.MinValue;
        RecordOffensiveCast(targetId);
    }

    /// <summary>
    /// Record that an attack (offensive spell cast / weapon swing) was just
    /// issued at <paramref name="targetId"/>. The prior attack is judged first;
    /// a new judgement window is then armed ONLY if none is still in flight.
    /// Not clobbering an unsettled window is what makes melee/missile count at
    /// all: those modes re-attack every 1000ms (attackCmdIntervalMs) while the
    /// settle window is BlacklistCastSettleMs (default 1500ms), so resetting the
    /// timer every cycle meant the pending attack never settled and the miss
    /// streak never advanced — only magic (≥1500ms cadence) ever blacklisted.
    /// Now each attack window runs to settle, so the blacklist is driven by
    /// "N real attacks with no damage" for casts, missile shots, and melee
    /// swings alike — never raw interval ticks.
    /// </summary>
    internal void RecordOffensiveCast(int targetId)   // internal for tests
    {
        if (_lastAttackedTargetId != targetId)
        {
            // New target — reset streak + drop any stale pending judgement.
            _lastAttackedTargetId = targetId;
            _consecutiveCastMisses = 0;
            _pendingJudgeCastAt = DateTime.MinValue;
        }

        // Stamped on EVERY offensive action, not just a target change, so the
        // in-flight hold (Target Lock off, AttackInFlightAtLocked) tracks the most
        // recent cast. Stamping only on change would end the hold mid-fight while
        // still casting at the same mob — the in-flight abandonment it exists to stop.
        _lastAttackedAt = TargetClock();   // RynthClock.Now outside tests

        JudgePendingCast(); // verdict on the prior attack if its settle window elapsed

        // An attack is still awaiting its verdict — let it settle instead of
        // restarting the clock (the fast-cadence starvation fix). JudgePendingCast
        // clears _pendingJudgeCastAt on settle, so the next attack re-arms here.
        if (_pendingJudgeCastAt != DateTime.MinValue) return;

        _pendingJudgeCastAt   = RynthClock.Now;
        _pendingJudgeTargetId = targetId;
        _damageSincePendingCast = false;
        _pendingJudgeErrSeq   = WeenieErrorSeqNow();
    }

    /// <summary>WeenieError 0x4EB, "You can't do that while in the air" (ACE WeenieError.YouCantDoThatWhileInTheAir).</summary>
    internal const uint InTheAirErr = 0x4EB;

    private int _pendingJudgeErrSeq;   // the engine's refusal counter when the judged attack was armed

    private int WeenieErrorSeqNow()
    {
        try { return _host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int seq, out _, out _, out _) ? seq : 0; }
        catch { return 0; }
    }

    /// <summary>The last refusal the engine saw is 0x4EB and came after <paramref name="seqAtArm"/>.</summary>
    private bool InTheAirErrorSince(int seqAtArm)
    {
        try
        {
            return _host.HasGetLastWeenieError
                && _host.TryGetLastWeenieError(out int seq, out uint err, out _, out _)
                && seq != seqAtArm && err == InTheAirErr;
        }
        catch { return false; }
    }

    /// <summary>
    /// Take a just-killed (or predicted-killed) monster out of the current scan list. The scan
    /// skips <see cref="_recentlyKilled"/> ids, but it runs at most every SCAN_INTERVAL_MS and the
    /// kill notice arrives between ticks, so the next tick's target pick still saw the dead mob
    /// in the old list and locked it again (score ~140: closest and wounded) — a "lock" of the
    /// corpse-to-be right after "DropTarget ... killed", dropped one tick later as "hp=0". On
    /// every kill in the 2026-10-01/10-04 Olthoi logs; a cast or swing could go at it.
    /// </summary>
    private void ForgetScanned(int id)
    {
        for (int i = _scannedTargets.Count - 1; i >= 0; i--)
            if (_scannedTargets[i].Id == id) _scannedTargets.RemoveAt(i);
    }

    private void DropTarget(string reason)
    {
        if (activeTargetId != 0)
            _host.Log($"[RynthAi] DropTarget 0x{activeTargetId:X8}: {reason} [{DescribeTargetPos(activeTargetId)}]");
        activeTargetId = 0;
        _lockedTargetId = 0;
        EndKite("target dropped");
        EndEnvironmentApproach("target dropped");
        _envPendingTargetId = 0;
        // A turn held toward the dropped target must be let go, or the character keeps
        // spinning until something else happens to release it.
        if (_facingTarget) ClearCombatTurnMotions();
        _facingTarget = false;
        _returnToPhysicalCombat = false;
        _targetLockedAt    = DateTime.MinValue;
        _lastDamageDealtAt = DateTime.MinValue;
        _lockedLastSeen       = default;
        _lockedLastSeenInScan = DateTime.MinValue;
        _targetLostScanTime = DateTime.MinValue;
        _pendingJudgeCastAt = DateTime.MinValue;
        _consecutiveCastMisses = 0;
        // Reset kill-shot fight tracking — the next acquired target starts fresh.
        _fightTargetId = 0;
        _fightTargetWcid = 0;
        _fightTargetName = "";
        _fightTargetMaxHp = 0;
        _fightHpEstimated = false;
        _fightDamage = 0;
        _fightCastCount = 0;
        _fightAppraiseRequested = false;
        _fightHealth = -1f;
        _fightHealthAt = DateTime.MinValue;
        _fightLastHitAt = DateTime.MinValue;
        _predictKillSwap = false;
        _predictKillSwapArmed = false;
        // Clear the stance-stuck timer — it arms during the normal NonCombat→Magic
        // window at fight start, and if the target drops in that window a surviving
        // timer makes the NEXT engagement's first equip tick see minutes of "stuck"
        // and fire a spurious recovery (busy force-clear + UseObject on a wielded wand).
        ResetStanceRecovery();
        // Defer the turn-stop while a targeted cast is still resolving: SetMotion(turn,false)
        // here truncates the in-flight cast gesture → ACE never finishes the cast → server
        // Player.IsBusy stranded (relog-only wedge). The target drop above still happens; only
        // the stop-thunk waits, bounded by IsCastInFlight (UseDone-seq or 2.5s); re-issued next tick.
        if (!IsCastInFlight()) ClearCombatTurnMotions();
    }

    /// <summary>
    /// Execute a predicted kill-shot swap that was ARMED at cast time and is now
    /// CONFIRMED accepted (ACE: refusal window elapsed with no "You're too
    /// busy!"). Deferring the drop until confirmation is what stops a refused
    /// cast from abandoning a live mob and churning the swarm — the behaviour
    /// that pinned AC's m_cBusy and wedged combat-mode / cast input. Body is the
    /// old immediate-swap path, unchanged.
    /// </summary>
    private void ExecuteDeferredKillSwap()
    {
        _predictKillSwapArmed = false;
        int swapId = activeTargetId;
        if (swapId == 0) return;

        _recentlyKilled[swapId] = RynthClock.Now.AddMilliseconds(PREDICTED_SWAP_SUPPRESS_MS);
        ForgetScanned(swapId);
        _predictedSwapAt[swapId] = TargetClock();   // fought again later = it didn't die (PredictionFailed)
        _predictedDeathPending[swapId] = TargetClock();   // still a monster in range until its death is confirmed
        _guessedDeaths.Remove(swapId);
        // Queue this predicted one-shot so it's CREDITED when its death confirms —
        // we may swap away before its KillerNotification. Capture the name LIVE
        // from the world filter (_fightTargetName is often empty this early).
        string pkName = _worldFilter[swapId]?.Name ?? _fightTargetName;
        if (_fightTargetWcid != 0 && _fightCastCount > 0 && !string.IsNullOrEmpty(pkName))
        {
            DateTime nowSwap = RynthClock.Now;
            _predictedKillPending.RemoveAll(p => p.Expiry < nowSwap);
            _predictedKillPending.Add(new PendingKill(
                pkName, _fightTargetWcid, _fightCastCount,
                _lastCastWeaponId, _lastCastElement, _lastCastTier, _fightDamage,
                RynthClock.Now.AddSeconds(5), _fightStartedAt, _fightSummon, swapId));
        }
        _host.Log($"[RynthAi] Predicted kill shot 0x{swapId:X8} '{_worldFilter[swapId]?.Name}' (cast#{_fightCastCount} hp~{_fightTargetMaxHp:0} dealt~{_fightDamage:0}) — confirmed accepted, swapping to next target.");
        // The pending-offensive judge state belongs to the mob being dropped —
        // clearing it stops JudgePendingOffensiveCast from recording this cast
        // against the dead target on the next attack block.
        _pendingOffensiveSpellId = 0;
        _pendingOffensiveTargetId = 0;
        DropTarget("predicted kill — swap (confirmed)");
    }

    /// <summary>
    /// Handle ACE's KillerNotification (GameEvent 0x01AD), forwarded by the
    /// engine with the formatted death message. The server sends it to the
    /// killer at the lethal hit — earlier than the health=0 update and the
    /// Monster→Corpse flip combat otherwise waits on (ACE delays CreateCorpse
    /// by the death-animation length, so those land 1–3s later). If the death
    /// message names our active target — or we can't read its name — drop it
    /// now and briefly suppress re-acquiring that id, so combat advances to the
    /// next target instead of firing a second full cast at a corpse-to-be.
    /// The notification is sent ONLY to the killer, so receiving it means WE
    /// killed something; combat engages one target at a time, so the active
    /// target is the victim. A non-matching name means a different creature
    /// died (AoE/DoT) — we leave the live target alone.
    /// Runs on the same pump thread as Think()/ScanNearbyTargets (no locking).
    /// </summary>
    public void OnKillNotification(string deathMessage)
    {
        NoteSwingEvidence("kill");
        CastsSinceLastKill = 0;   // D6: a kill landed (KillerNotification → the killer) — reset the orphan signal.
        // Record EXACTLY ONE kill per death message, attributed by name (→ correct
        // wcid) from the best available source, so rapid one-shots aren't lost when
        // the fight state has already been cleared/advanced by the time the corpse's
        // KillerNotification lands. Three tiers, most-specific first.
        if (_damageStore == null) return;
        DateTime nowKill = RynthClock.Now;
        _predictedKillPending.RemoveAll(p => p.Expiry < nowKill);

        // One death, two notices: the second is an echo of the first (no kill, nobody hidden).
        if (IsKillEcho(deathMessage, nowKill))
        {
            if (_killDbgCount < 1000)
            {
                _killDbgCount++;
                _host.Log($"[KillDbg] how=echo of 0x{(uint)_lastConfirmedKillId:X8} " +
                          $"(+{(nowKill - _lastConfirmedKillAt).TotalMilliseconds:0} ms) msg='{deathMessage}'");
            }
            return;
        }

        bool recorded = false;
        string how = "miss";
        int pendingKillId = 0;   // id of the predicted one-shot this notice was credited to (tier 1)

        // (1) A predicted one-shot we swapped away from — captured wcid + cast count.
        for (int i = 0; i < _predictedKillPending.Count; i++)
        {
            if (!string.IsNullOrEmpty(_predictedKillPending[i].Name)
                && deathMessage.IndexOf(_predictedKillPending[i].Name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                PendingKill pk = _predictedKillPending[i];
                _predictedKillPending.RemoveAt(i);
                pendingKillId = pk.Id;
                // A summon's damage isn't in pk.Damage, so that total would learn the HP too low.
                _damageStore.RecordKill(pk.WeaponId, pk.Wcid, pk.Name, pk.Element, pk.Tier, pk.CastCount,
                                        pk.Summon != null ? 0 : pk.Damage);
                if (pk.Started != default)
                    _damageStore.RecordFightTime(pk.WeaponId, pk.Wcid, pk.Name, (nowKill - pk.Started).TotalSeconds, pk.Summon ?? "none");
                recorded = true; how = "pending";
                break;
            }
        }

        int tid = activeTargetId;
        string curName = tid != 0 ? (_worldFilter[tid]?.Name ?? "") : "";
        bool curMatches = curName.Length > 0
                          && deathMessage.IndexOf(curName, StringComparison.OrdinalIgnoreCase) >= 0;

        // The predicted kill's death is confirmed (R-20261025-183343-727b): no longer a monster in
        // range for the corpse gate, and the scan stays off it for a confirmed kill's window while
        // the corpse forms (the swap gave it 2 s). Not when the notice names the current target
        // too (a same-named swarm): it may be the current target's, so the predicted one stays
        // pending until its own health, corpse or window says.
        if (pendingKillId != 0 && !(curMatches && tid != pendingKillId)
            && _predictedDeathPending.Remove(pendingKillId))
        {
            _guessedDeaths.Remove(pendingKillId);
            _recentlyKilled[pendingKillId] = nowKill.AddMilliseconds(RECENTLY_KILLED_SUPPRESS_MS);
            NoteConfirmedKill(pendingKillId, nowKill);
        }

        // (2) The current target died (its name is in the message).
        if (!recorded && curMatches && _fightTargetWcid != 0 && _fightCastCount > 0)
        {
            string? summon = _fightSummon ?? SummonOut?.Invoke();
            _damageStore.RecordKill(_lastCastWeaponId, _fightTargetWcid, curName, _lastCastElement, _lastCastTier,
                                    _fightCastCount, summon != null ? 0 : _fightDamage);
            if (_fightStartedAt != default)
                _damageStore.RecordFightTime(_lastCastWeaponId, _fightTargetWcid, curName,
                                             (nowKill - _fightStartedAt).TotalSeconds, summon ?? "none");
            recorded = true; how = "current";
        }

        // (3) A mob we cast at then moved on from (state already cleared) — credit
        //     it from the persistent snapshot when the name matches. This is the
        //     fix for the rapid-kill under-count.
        if (!recorded && _lastFightWcid != 0 && _lastFightName.Length > 0
            && deathMessage.IndexOf(_lastFightName, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _damageStore.RecordKill(_lastFightWeaponId, _lastFightWcid, _lastFightName, _lastFightElement, _lastFightTier,
                                    _lastFightCount > 0 ? _lastFightCount : 1, 0);
            recorded = true; how = "snapshot";
        }

        // (4) A mob fought further back than one-ago (advanced past several targets) — the
        //     recent-fights ring credits it by name. Closes the post-advance under-count that
        //     the single _lastFight slot drops (KillDbg how=miss). Consumes the matched entry.
        if (!recorded)
        {
            _recentFights.RemoveAll(e => e.Pk.Expiry < nowKill);
            for (int i = _recentFights.Count - 1; i >= 0; i--)
            {
                PendingKill pk = _recentFights[i].Pk;
                if (!string.IsNullOrEmpty(pk.Name)
                    && deathMessage.IndexOf(pk.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _recentFights.RemoveAt(i);
                    _damageStore.RecordKill(pk.WeaponId, pk.Wcid, pk.Name, pk.Element, pk.Tier,
                                            pk.CastCount > 0 ? pk.CastCount : 1, 0);
                    recorded = true; how = "recent";
                    break;
                }
            }
        }

        // (5) AOE/area kill: a mob wiped by a splash cast we never directly fought (tiers 1-4
        //     have no record of it). Credit by matching its death message against the names of
        //     monsters the scanner has seen this session. Longest name wins (most specific), so
        //     "Olthoi Swarm Soldier" isn't mis-credited to a shorter overlapping name.
        if (!recorded && _seenMonsterNameToWcid.Count > 0)
        {
            uint bestWcid = 0; string bestName = ""; int bestLen = 0;
            foreach (var kv in _seenMonsterNameToWcid)
            {
                if (kv.Key.Length > bestLen
                    && deathMessage.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                { bestWcid = kv.Value; bestName = kv.Key; bestLen = kv.Key.Length; }
            }
            if (bestWcid != 0)
            {
                // Attribute to the last cast's shape (the AOE/ring that wiped the pack) so these
                // kills group under that tier (e.g. "R2") instead of an unattributed row. Per-cast
                // damage/casts-to-kill stays meaningless for AOE — we only count the kill.
                _damageStore.RecordKill(_lastCastWeaponId, bestWcid, bestName, _lastCastElement, _lastCastTier, 1, 0);
                recorded = true; how = "aoe";
            }
        }

        if (_killDbgCount < 1000)
        {
            _killDbgCount++;
            _host.Log($"[KillDbg] how={how} curMatch={curMatches} tid=0x{(uint)tid:X8} fightWcid={_fightTargetWcid} cnt={_fightCastCount} lastWcid={_lastFightWcid} pend={_predictedKillPending.Count} msg='{deathMessage}'");
        }

        // Drop the current target only if IT is the one that died. The name alone can't
        // tell same-named mobs apart (a swarm is all "Olthoi Swarm Soldier"), so keep the
        // live target when the notice belongs to the predicted one-shot we swapped away
        // from, or when we haven't attacked the current target at all yet — then a ring or
        // a late death notice killed a neighbour. Dropping it also hid it from the scan for
        // 4 s, and with no other mob in range the bot went to peace mid-fight.
        bool noticeForSwappedMob = pendingKillId != 0 && pendingKillId != tid;
        // A damage line naming the target (ResolveHitVictim stamped it) is an attack too.
        bool curAttacked = _fightTargetId != tid || _fightCastCount > 0 || _pendingOffensiveTargetId == tid
                           || (tid != 0 && _hitAt.ContainsKey(tid));
        // A ring, wall or chain spell kills neighbours too. The server's health for the fight
        // target lands before its kill notice (ACE sends the 0 with the lethal hit), so a fight
        // target the server still says is alive didn't die: the notice is a same-named
        // neighbour's we hit in the last few seconds.
        int otherVictim = (!curMatches || noticeForSwappedMob) ? 0 : FindRecentlyHitVictim(deathMessage, tid, nowKill);
        float curHp = tid != 0 ? _worldFilter.GetHealthRatio(tid) : -1f;
        bool curServerAlive = curHp > 0f && !_worldFilter.IsHealthEstimated(tid);
        bool noticeForNeighbour = otherVictim != 0 && curServerAlive;
        if (tid != 0 && curMatches && (noticeForSwappedMob || !curAttacked || noticeForNeighbour))
        {
            _host.Log($"[RynthAi] Kill notice names 0x{tid:X8} '{curName}' but " +
                      (noticeForSwappedMob ? $"belongs to predicted kill 0x{pendingKillId:X8}"
                       : noticeForNeighbour ? $"it is alive ({curHp:0.00}); a neighbour we hit died (0x{otherVictim:X8})"
                       : "it hasn't been attacked yet") +
                      " — keeping the target");
            if (noticeForNeighbour)
                SuppressGuessedDeath(otherVictim, nowKill);
        }
        else if (tid != 0 && curMatches)
        {
            _recentlyKilled[tid] = nowKill.AddMilliseconds(RECENTLY_KILLED_SUPPRESS_MS);
            NoteConfirmedKill(tid, nowKill);
            ForgetScanned(tid);
            DropTarget("killed (KillerNotification)");
        }
        else if (pendingKillId == 0)
        {
            // Another monster died, not the fight target: one we hit with a ring, wall or chain.
            // Keep the scan off it while its corpse forms.
            int victim = FindRecentlyHitVictim(deathMessage, tid, nowKill);
            if (victim != 0)
                SuppressGuessedDeath(victim, nowKill);
        }
    }

    // ── Kill notices that name no one for sure ───────────────────────────────
    // The notice carries only the monster's name. For the fight target that is enough (it is the
    // monster we were hitting); for any other monster it is a guess by name among those we hit
    // lately, and in a same-named pack the guess can be wrong.
    //
    // 2026-11-01 (DreamWeave, a melee character, a pack of same-named monsters): some kills
    // brought two KillerNotifications 1-2 ms apart with different wording ("You killed X!" and
    // "X suffers a frozen fate!") and one corpse. The second was matched by name to a live
    // neighbour, which was kept out of the scan for 4 s: with no other monster scanned, combat
    // let the tick go and the bot opened a corpse with that monster 3 m away hitting it, then
    // locked it again 4 s later. If the neighbour had been the top-priority monster, lesser ones
    // would have been fought instead.
    //
    // Now: a second notice for the same monster right after a confirmed kill is an echo, and a
    // death credited by guess keeps the monster counted as a monster in range (no looting, no
    // peace mode) until the server confirms it (corpse, deleted, health 0), for at most
    // PREDICTED_SWAP_SUPPRESS_MS, the way a predicted kill does; a damage line on it ends that.

    /// <summary>How soon after a confirmed kill a notice naming the same monster is its echo.</summary>
    internal const double KillEchoMs = 300;

    private int _lastConfirmedKillId;
    private string _lastConfirmedKillName = "";
    private DateTime _lastConfirmedKillAt = DateTime.MinValue;
    // Ids in _predictedDeathPending whose death came from a kill notice matched by name (not a
    // predicted kill-shot swap): for the log's wording only.
    private readonly HashSet<int> _guessedDeaths = new();

    private void NoteConfirmedKill(int id, DateTime now)
    {
        _lastConfirmedKillId = id;
        _lastConfirmedKillName = _worldFilter[id]?.Name ?? "";
        _lastConfirmedKillAt = now;
    }

    /// <summary>
    /// True when <paramref name="deathMessage"/> repeats the death confirmed just before: it names
    /// that monster, came within <see cref="KillEchoMs"/>, and no other monster of that name could
    /// have died (none hit lately, no attack on its way at a same-named target, no predicted kill
    /// of that name pending).
    /// </summary>
    private bool IsKillEcho(string deathMessage, DateTime now)
    {
        if (_lastConfirmedKillId == 0 || _lastConfirmedKillName.Length == 0) return false;
        if ((now - _lastConfirmedKillAt).TotalMilliseconds > KillEchoMs) return false;
        if (deathMessage.IndexOf(_lastConfirmedKillName, StringComparison.OrdinalIgnoreCase) < 0) return false;
        for (int i = 0; i < _predictedKillPending.Count; i++)
            if (!string.IsNullOrEmpty(_predictedKillPending[i].Name)
                && deathMessage.IndexOf(_predictedKillPending[i].Name, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        int tid = activeTargetId;
        if (tid != 0 && tid != _lastConfirmedKillId && _pendingOffensiveTargetId == tid
            && string.Equals(_worldFilter[tid]?.Name, _lastConfirmedKillName, StringComparison.OrdinalIgnoreCase))
            return false;
        return FindRecentlyHitVictim(deathMessage, _lastConfirmedKillId, now) == 0;
    }

    /// <summary>
    /// A death credited by name to a monster that isn't the fight target: off the scan for the
    /// predicted window, but still a monster in range for the fight and loot gates until its death
    /// is confirmed (see <see cref="PredictedDeathPendingWithin"/>).
    /// </summary>
    private void SuppressGuessedDeath(int id, DateTime nowKill)
    {
        _recentlyKilled[id] = nowKill.AddMilliseconds(PREDICTED_SWAP_SUPPRESS_MS);
        _predictedDeathPending[id] = TargetClock();
        _guessedDeaths.Add(id);
    }

    /// <summary>The highest Priority among guessed deaths still pending and alive within
    /// <paramref name="range"/>, or int.MinValue when there is none.</summary>
    private int TopGuessedDeathPriority(double range)
    {
        if (_guessedDeaths.Count == 0) return int.MinValue;
        DateTime now = TargetClock();
        int me = unchecked((int)_playerId);
        int top = int.MinValue;
        foreach (int id in _guessedDeaths)
        {
            if (!_predictedDeathPending.TryGetValue(id, out DateTime at)
                || (now - at).TotalMilliseconds > PREDICTED_SWAP_SUPPRESS_MS) continue;
            if (!IsLiveMonster(id)) continue;
            if (me == 0 || _worldFilter.Distance(me, id) > range) continue;
            int p = PriorityOf(id);
            if (p > top) top = p;
        }
        return top;
    }

    /// <summary>Test seam: engage <paramref name="targetId"/> as the fight target, as a target lock does.</summary>
    internal void EngageForTest(int targetId)
    {
        activeTargetId = targetId;
        _lockedTargetId = targetId;
        BeginFight(targetId);
    }

    /// <summary>Test seam: true while the scan is kept off <paramref name="id"/> after a kill.</summary>
    internal bool IsKillSuppressed(int id) => _recentlyKilled.ContainsKey(id);

    /// <summary>Record the just-stamped _lastFight* context into the recent-fights ring,
    /// keyed by the live object id (refreshed per hit, capped, TTL-pruned). Lets a kill that
    /// lands after we've advanced past the dying mob still be credited by name (tier 4).</summary>
    private void StampRecentFight()
    {
        if (_lastFightWcid == 0) return;
        int id = activeTargetId;
        if (id == 0) return;
        var pk = new PendingKill(_lastFightName, _lastFightWcid, _lastFightCount > 0 ? _lastFightCount : 1,
                                 _lastFightWeaponId, _lastFightElement, _lastFightTier, 0,
                                 RynthClock.Now.AddMilliseconds(RecentFightTtlMs));
        for (int i = _recentFights.Count - 1; i >= 0; i--)
            if (_recentFights[i].Id == id) _recentFights.RemoveAt(i);
        _recentFights.Add((id, pk));
        if (_recentFights.Count > RecentFightsCap) _recentFights.RemoveAt(0);
    }

    /// <summary>
    /// Exact per-hit damage from the engine (AttackerNotification 0x01B1),
    /// attributed to the active target (combat engages one mob at a time). This
    /// is the only selection-free source of real damage numbers: ACE pushes
    /// creature health only for the SELECTED target, so combat — which never
    /// selects — otherwise can't see how hurt a mob is. Feeds kill-shot
    /// prediction and doubles as reliable blacklist damage feedback.
    /// </summary>
    public void OnCombatDamage(double damage, uint damageType, bool crit, bool isAttacker)
    {
        if (!isAttacker || damage <= 0) return;
        NoteSwingEvidence("hit");   // any target: a swing landing anywhere means the attack loop is alive
        int tid = activeTargetId;
        if (tid == 0 || tid != _fightTargetId) return;

        NoteFightHit((int)Math.Round(damage));
        _fightDamage += damage;

        // War magic sends NO 0x01B1 (its damage comes from the chat parse), so a structured
        // hit event in a NON-magic mode is a MELEE or MISSILE hit. Magic sets its cast context
        // in AttackWithMagic; for melee/missile derive the per-hit context here so the Damage
        // tab keys it correctly: weapon = the equipped weapon, element = the packet damage type
        // (accurate per hit), tier = 0 (no spell tier). The packet crit flag is reliable too.
        if (CurrentCombatMode != CombatMode.Magic)
        {
            _lastCastWeaponId = unchecked((uint)_equippedWeaponId);
            _lastCastElement  = ElementFromDamageType(damageType);
            _lastCastTier     = 0;
            _lastCastWasRing  = false;   // melee/missile are single-target
            _fightCastCount++;           // landed hits → "casts/attacks to kill" learning
            // Snapshot the fight so a kill landing after we've advanced is still credited
            // (OnKillNotification path 3) with the right melee/missile weapon/element.
            if (_fightTargetWcid != 0)
            {
                _lastFightWcid     = _fightTargetWcid;
                _lastFightName     = _fightTargetName;
                _lastFightWeaponId = _lastCastWeaponId;
                _lastFightElement  = _lastCastElement;
                _lastFightTier     = _lastCastTier;
                _lastFightCount    = _fightCastCount;
                StampRecentFight();
            }
        }

        // Don't learn per-cast damage from ring/AoE casts — they hit multiple
        // mobs, so the damage can't be attributed to this single target.
        if (_damageStore != null && _fightTargetWcid != 0 && !_lastCastWasRing)
            _damageStore.RecordHit(_lastCastWeaponId, _fightTargetWcid, _fightTargetName, _lastCastElement, _lastCastTier, damage, crit);

        ReportDamageOnTarget(tid); // clears the blacklist miss streak + stamps last-damage time
    }

    // Map an AC DAMAGE_TYPE flag (from the 0x01B1 packet) to the Damage-tab element string.
    // A weapon hit carries one primary type; if multiple bits are set, prefer the physical base.
    internal static string ElementFromDamageType(uint dt)
    {
        if ((dt & 0x1)   != 0) return "Slash";
        if ((dt & 0x2)   != 0) return "Pierce";
        if ((dt & 0x4)   != 0) return "Bludgeon";
        if ((dt & 0x10)  != 0) return "Fire";
        if ((dt & 0x8)   != 0) return "Cold";
        if ((dt & 0x40)  != 0) return "Lightning";   // DamageType.Electric
        if ((dt & 0x20)  != 0) return "Acid";
        if ((dt & 0x400) != 0) return "Nether";
        return "Physical";                            // Undef / vital drains
    }

    /// <summary>Start tracking a fresh fight against <paramref name="targetId"/>: zero the damage tally, capture its wcid, resolve its HP.</summary>
    private void BeginFight(int targetId)
    {
        _fightTargetId = targetId;
        _fightDamage = 0;
        _fightCastCount = 0;
        _fightTargetWcid = 0;
        _fightTargetMaxHp = 0;
        _fightHpEstimated = false;
        _fightAppraiseRequested = false;
        _fightHealth = -1f;
        _fightHealthAt = DateTime.MinValue;
        _fightLastHitAt = DateTime.MinValue;
        _predictKillSwap = false;
        _fightTargetName = "";
        _fightStartedAt = default;
        _fightSummon = null;
        if (targetId == 0) return;
        _fightStartedAt = RynthClock.Now;
        _fightSummon = SummonOut?.Invoke();
        _fightTargetName = _worldFilter[targetId]?.Name ?? "";
        if (_host.HasGetObjectWcid && _host.TryGetObjectWcid((uint)targetId, out uint wcid))
            _fightTargetWcid = wcid;
        ResolveFightHp();
    }

    /// <summary>
    /// Resolve the active target's HP pool for prediction. Priority:
    ///   1) appraised MaxHealth from the shared creature store (instant once any
    ///      character has assessed this monster type; persists across sessions),
    ///   2) learned damage-to-kill for this wcid (per character),
    ///   3) one appraisal request to populate (1) for next time (best-effort).
    /// Re-called each tick until HP is known (the appraisal lands async).
    /// </summary>
    private void ResolveFightHp()
    {
        if (_fightTargetId == 0) return;

        // Once per fight, query the live target's health. The 0x01C0 response is
        // handled on AC's main thread, where the engine reads the creature's REAL
        // MaxHealth from its qualities (SEH-guarded) and corrects the bogus
        // appraisal stub in creatures.json. Fire this EVEN when we already have a
        // cached (stub) value — otherwise the stub suppresses the query and the
        // real number never gets read.
        if (!_fightAppraiseRequested)
        {
            _fightAppraiseRequested = true;
            try { if (_host.HasQueryHealth) _host.QueryHealth((uint)_fightTargetId); } catch { }
        }

        if (_fightTargetMaxHp > 0 && !_fightHpEstimated) return;

        // A server's scaled worlds and copies: the same wcid, its health percent a tier (the server
        // plugin's numbers, DifficultyTier). 0 everywhere else, and then this is the old resolution exactly.
        int difficulty = Difficulty;
        double scale = _difficultyTier?.HealthScale(difficulty) ?? 1.0;

        // User-entered HP override (set in the Damage panel) is authoritative —
        // it's the manual fix for ACE's skill-gated appraisal reading low.
        // It is the real-Dereth value; a scaled tier scales it like the server does.
        if (_damageStore != null && _fightTargetWcid != 0)
        {
            double manual = _damageStore.GetManualHp(_fightTargetWcid);
            if (manual > 0) { _fightTargetMaxHp = manual * scale; _fightHpEstimated = false; return; }
        }

        var obj = _worldFilter[_fightTargetId];
        string name = obj?.Name ?? "";

        CreatureProfile? prof = null;
        if (_creatureStore != null && !string.IsNullOrEmpty(name))
        {
            bool found = _fightTargetWcid != 0
                ? _creatureStore.TryGet(name, _fightTargetWcid, out prof)
                : _creatureStore.TryGetByName(name, out prof);
            if (!found) prof = null;
            uint appraised = _creatureStore.MaxHealthAt(prof, difficulty);
            if (appraised > 0)
            {
                _fightTargetMaxHp = appraised;
                _fightHpEstimated = false;
                return;
            }
        }

        if (_damageStore != null && _fightTargetWcid != 0)
        {
            double hp = _damageStore.GetLearnedHp(_fightTargetWcid, difficulty);
            if (hp > 0) { _fightTargetMaxHp = hp; _fightHpEstimated = false; return; }
        }

        // A scaled tier never seen yet: tier 0's HP x (1 + 5% x tier) is what the server does
        // (Scaling.cs raises the written-in health and the attributes it derives from by that
        // factor), so it stands in until this tier's own appraisal lands (QueryHealth above,
        // usually within the first second) and replaces it. Casts-to-kill is not estimated:
        // only this tier's own kills count for that (MonsterDamageStore).
        if (difficulty > 0)
        {
            double baseHp = prof != null && prof.MaxHealth > 0 ? prof.MaxHealth
                          : _damageStore != null && _fightTargetWcid != 0 ? _damageStore.GetLearnedHp(_fightTargetWcid, 0) : 0;
            if (baseHp > 0)
            {
                _fightTargetMaxHp = baseHp * scale;
                _fightHpEstimated = true;
                return;
            }
        }
        if (_fightHpEstimated) return;   // keep the estimate while the appraisal is pending

        // Unknown — appraise once so the shared store fills for next time. Costs
        // one busy-count tick; only the first encounter of a wcid pays it.
        if (!_fightAppraiseRequested)
        {
            // Done only once the engine took the request (it refuses around a death or a portal; a refused one
            // is asked again next tick). No RequestId at all: nothing to wait for.
            bool idTaken = true;
            try { if (_host.HasRequestId) idTaken = _host.RequestId((uint)_fightTargetId); } catch { }
            if (idTaken)
            {
                _fightAppraiseRequested = true;
                try { if (_host.HasQueryHealth) _host.QueryHealth((uint)_fightTargetId); } catch { }
            }
        }
    }

    /// <summary>
    /// A damage line landed on the fight target. Stamps the hit (a server health reading older
    /// than it doesn't count it), and when the swap armed by a predicted kill shot is waiting on
    /// this target and the line comes ArmedHitMinMs or more after the arming cast, that cast hit
    /// without killing (a killing blow sends the death message, not a damage line): the swap is
    /// called off and the bot keeps casting at it (R-20261025-183343-727b).
    /// </summary>
    private void NoteFightHit(int amount)
    {
        _fightLastHitAt = TargetClock();
        if (!_predictKillSwapArmed || _fightTargetId == 0 || _predictArmTargetId != _fightTargetId) return;
        double sinceCast = (RynthClock.Now - _predictArmCastAt).TotalMilliseconds;
        if (sinceCast < ArmedHitMinMs) return;
        _predictKillSwapArmed = false;
        _host.Log($"[RynthAi] Predicted kill shot 0x{(uint)_fightTargetId:X8} '{_worldFilter[_fightTargetId]?.Name}' called off: " +
                  $"its cast hit for {amount} {sinceCast:0} ms after it went out and the monster is still alive " +
                  $"(cast#{_fightCastCount} hp~{_fightTargetMaxHp:0} dealt~{_fightDamage + amount:0}).");
    }

    /// <summary>The plugin's OnUpdateHealth: the server's health for <paramref name="id"/>. Kept for the
    /// fight target only (the kill-shot check), stamped with when it came.</summary>
    internal void NoteServerHealth(int id, float ratio)
    {
        if (id == 0 || id != _fightTargetId || float.IsNaN(ratio)) return;
        _fightHealth = Math.Clamp(ratio, 0f, 1f);
        _fightHealthAt = TargetClock();
    }

    /// <summary>The fight target's server health when it counts every hit landed so far: it came during
    /// this fight and no hit landed more than FightHealthHitSlackMs after it.</summary>
    private bool TryFreshFightHealth(out float ratio)
    {
        ratio = _fightHealth;
        if (_fightHealthAt == DateTime.MinValue || ratio <= 0f) return false;
        return _fightLastHitAt == DateTime.MinValue
               || _fightHealthAt >= _fightLastHitAt.AddMilliseconds(-FightHealthHitSlackMs);
    }

    /// <summary>
    /// The evidence says the cast just fired can't finish the fight target: what is left of it is more
    /// than KILL_VETO_REACH casts of <paramref name="perCast"/>. Two sources, either one enough:
    /// the fresh server health (with the HP pool, or with the damage dealt when the pool is unknown:
    /// dealt / (1 - ratio) is the pool the health implies), and the HP pool minus the damage dealt,
    /// once damage has landed (a one-shot has nothing landed and is left to casts-to-kill as before;
    /// an HP pool that is off then can't hold it back). Without a per-cast figure there is no check.
    /// </summary>
    private bool KillShotContradicted(double perCast, out string why)
    {
        why = "";
        if (perCast <= 0) return false;
        double reach = perCast * KILL_VETO_REACH;
        if (TryFreshFightHealth(out float ratio))
        {
            double left = _fightTargetMaxHp > 0 ? ratio * _fightTargetMaxHp
                        : _fightDamage > 0 && ratio < 1f ? _fightDamage * ratio / (1.0 - ratio)
                        : -1;
            if (left > reach)
            {
                why = $"the server says its health is {ratio * 100f:0}% (~{left:0} HP left)";
                return true;
            }
        }
        if (_fightTargetMaxHp > 0 && _fightDamage > 0)
        {
            double left = _fightTargetMaxHp - _fightDamage;
            if (left > reach)
            {
                why = $"~{left:0} of its ~{_fightTargetMaxHp:0} HP are left after ~{_fightDamage:0} dealt" +
                      (_fightHpEstimated ? " (the HP is the tier estimate)" : "");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// After firing an offensive cast at the active target, decide whether that
    /// cast is expected to finish the mob. If so — and another target is in
    /// range — flag a swap so Think drops this mob instead of wasting the next
    /// cast during the projectile's flight. Ring/AoE casts are excluded (their
    /// damage hits multiple mobs, so single-target accounting doesn't apply).
    /// </summary>
    private void EvaluateKillShot(uint weaponId, string element, int tier, bool isRing)
    {
        _predictKillSwap = false;
        if (isRing || _damageStore == null || _fightTargetWcid == 0) return;
        if (activeTargetId == 0 || activeTargetId != _fightTargetId) return;
        if (PredictionFailed(activeTargetId)) return;    // survived a predicted kill — stay on it
        if (!HasAlternateTarget(activeTargetId)) return; // nothing to swap to — keep casting

        // Both paths need the evidence not to say otherwise (KillShotContradicted, 2026-10-25): one
        // cast's damage is the learned average, else this fight's own average over the casts that
        // have landed (the one just fired hasn't).
        double expected = _damageStore.GetAvgDamage(weaponId, _fightTargetWcid, element, tier, out int dmgSamples);
        bool learned = dmgSamples >= KILL_MIN_SAMPLES && expected > 0;
        double perCast = learned ? expected
                       : _fightCastCount > 1 && _fightDamage > 0 ? _fightDamage / (_fightCastCount - 1)
                       : 0;

        // (A) Casts-to-kill: we've fired the number of casts this wcid typically
        //     takes to die from this weapon+spell. Catches ONE-SHOTS (avg≈1 → swap
        //     right after cast 1), which the damage math below can't — nothing has
        //     landed when we predict, so it has no remaining-HP signal yet. On its own
        //     it is only a count (the Curator's cast #37), hence the evidence check.
        double avgCasts = _damageStore.GetAvgCastsToKill(weaponId, _fightTargetWcid, element, tier, out int killSamples);
        bool byCount = killSamples >= KILL_MIN_SAMPLES && avgCasts > 0
                       && _fightCastCount >= (int)Math.Round(avgCasts);

        // (B) Damage-based: remaining HP is within one comfortable cast. Precise
        //     for multi-cast fights once some damage has landed.
        bool byDamage = _fightTargetMaxHp > 0 && learned
                        && _fightTargetMaxHp - _fightDamage <= expected * KILL_CONFIDENCE;

        if (!byCount && !byDamage) return;
        if (KillShotContradicted(perCast, out string why))
        {
            if (_killShotVetoLoggedFor != _fightTargetId)
            {
                _killShotVetoLoggedFor = _fightTargetId;
                _host.Log($"[RynthAi] Kill shot not predicted for 0x{(uint)_fightTargetId:X8} '{_worldFilter[_fightTargetId]?.Name}' " +
                          $"(cast#{_fightCastCount}" + (byCount ? $", ~{avgCasts:0.#} casts kill one" : "") +
                          $", one cast ~{perCast:0}): {why}. Staying on it.");
            }
            return;
        }
        _predictKillSwap = true;
    }

    /// <summary>True if a scanned, non-suppressed target other than <paramref name="excludeId"/> is available to swap to.</summary>
    private bool HasAlternateTarget(int excludeId)
    {
        for (int i = 0; i < _scannedTargets.Count; i++)
        {
            int id = _scannedTargets[i].Id;
            if (id == excludeId) continue;
            if (_recentlyKilled.ContainsKey(id)) continue;
            if (IsEnvironmentSkipped(id)) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="id"/> survived a predicted kill: we swapped off it as dead and are
    /// now casting at it again (the kill-notice match is by name only, so being fought again is the
    /// one sure sign). Logs once when that is first seen. Called after each cast at the fight target.
    /// </summary>
    private bool PredictionFailed(int id)
    {
        DateTime now = TargetClock();
        if (_predictionFailedAt.Count > 0 || _predictedSwapAt.Count > 0)
        {
            PruneOlderThan(_predictionFailedAt, now);
            PruneOlderThan(_predictedSwapAt, now);
        }
        if (_predictionFailedAt.ContainsKey(id)) return true;
        if (!_predictedSwapAt.TryGetValue(id, out DateTime swappedAt)) return false;

        _predictedSwapAt.Remove(id);
        _predictedDeathPending.Remove(id);
        _predictionFailedAt[id] = now;
        float hp = _worldFilter.GetHealthRatio(id);
        string health = hp >= 0f ? $"health {hp * 100f:0}%" : "health unknown";
        _host.Log($"[RynthAi] Predicted kill of 0x{(uint)id:X8} '{_worldFilter[id]?.Name}' didn't happen: fought again " +
                  $"{(now - swappedAt).TotalSeconds:0.0} s after the swap ({health}, dealt~{_fightDamage:0} this fight). " +
                  "No more predicted swaps off it, so its casts are judged for damage.");
        return true;
    }

    private static void PruneOlderThan(Dictionary<int, DateTime> map, DateTime now)
    {
        List<int>? old = null;
        foreach (var kv in map)
            if ((now - kv.Value).TotalMilliseconds > PredictionMemoryMs) (old ??= new()).Add(kv.Key);
        if (old != null) foreach (int k in old) map.Remove(k);
    }

    /// <summary>Test seam: a cast at the fight target, then the kill-shot decision (as AttackWithMagic does).</summary>
    internal bool CastAndPredictForTest(uint weaponId, string element, int tier)
    {
        _fightCastCount++;
        _lastCastWeaponId = weaponId;
        _lastCastElement = element;
        _lastCastTier = tier;
        EvaluateKillShot(weaponId, element, tier, isRing: false);
        bool predicted = _predictKillSwap;
        _predictKillSwap = false;
        return predicted;
    }

    /// <summary>Test seam: the confirmed predicted-kill swap off the fight target.</summary>
    internal void PredictedSwapForTest() => ExecuteDeferredKillSwap();

    /// <summary>Test seam: a predicted kill shot armed by a cast at the fight target <paramref name="castAgoMs"/> ago
    /// (as the attack block arms it), waiting for the cast to be confirmed.</summary>
    internal void ArmPredictedSwapForTest(double castAgoMs)
    {
        _predictKillSwapArmed = true;
        _predictSwapGestureSeen = false;
        _predictArmTargetId = activeTargetId;
        _predictArmCastAt = RynthClock.Now.AddMilliseconds(-castAgoMs);
    }

    /// <summary>Tests: a predicted kill-shot swap is armed and waiting.</summary>
    internal bool PredictedSwapArmed => _predictKillSwapArmed;

    /// <summary>Floor between attack commands for the first cast at a new target when the server
    /// gates are available (see <see cref="AttackIntervalMs"/>). Only bounds how fast a refused
    /// cast could be retried; a refusal sets the target as the last attacked, so the retry waits
    /// the full interval anyway.</summary>
    internal const double NewTargetCastFloorMs = 300;

    /// <summary>The least Attack Spell Delay on an engine without the server gates.</summary>
    internal const double NoGatesAttackFloorMs = 250;

    /// <summary>Melee/missile: the first attack at a new target, with the server gates (see
    /// <see cref="AttackIntervalMs"/>).</summary>
    internal const double NewTargetPhysicalFloorMs = 200;

    /// <summary>Melee/missile: the attack after a CancelAttack (attack rebind or stall re-issue).
    /// ACE answers the cancel within a server tick or two; a swing still playing holds the attack
    /// through the busy count, so the full 1000 ms interval is not needed here.</summary>
    internal const double AttackAfterCancelMs = 300;

    /// <summary>True when <see cref="BusyCount"/> is positive only because AC lowered it inline at a
    /// server UseDone the busy hook can't see (see <see cref="CombatBusyGate"/>).</summary>
    internal bool BusyIsLeftover()
    {
        bool gesture = _host.HasGetCastBusyState && !_host.CanCastNow;
        return _busyGate.IsStale(BusyCount, _host.HasUseDoneSeq,
            _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0, gesture);
    }

    /// <summary>Times combat found the busy count was a leftover and went ahead (status/tests).</summary>
    internal int StaleBusyResets => _staleBusyResets;
    private int _staleBusyResets;

    /// <summary>
    /// The interval the combat tick waits after an attack command before the next one.
    /// Melee/missile: 1000 ms (AC paces swings and shots itself). Magic: the Attack Spell Delay
    /// setting (≤0 = 1500), except for the first cast at a new target when the engine has the
    /// server gates (UseDone and the cast gesture): those already hold the cast until the
    /// previous one is finished, so the new target only waits <see cref="NewTargetCastFloorMs"/>.
    /// Casting at the same target keeps the full setting.
    /// Melee/missile at a new target with the server gates: <see cref="NewTargetPhysicalFloorMs"/>.
    /// The 1000 ms is the re-assert cadence for the target being fought; the first swing at the
    /// next one only waited out what was left of it ([CombatLatency] "interval 9xx" on most locks,
    /// Drakkon 2026-10-12), while the busy count already holds the attack until the last swing's
    /// AttackDone and an attack still bound to a live old target is released first (rebind).
    /// </summary>
    internal static double AttackIntervalMs(int combatMode, int attackSpellIntervalMs, bool hasServerGates, bool newTarget)
    {
        if (combatMode != CombatMode.Magic)
            return newTarget && hasServerGates ? NewTargetPhysicalFloorMs : 1000.0;
        // 0 is allowed (2026-10-08): no delay of its own, the server gates (UseDone and the cast
        // gesture) alone hold the next cast until the last one is done. A file without the setting
        // keeps 1500 (the payload is nullable). An engine without those gates keeps a 250 ms floor,
        // the old slider minimum, so casts can't be sent back to back into "You're too busy!".
        double interval = attackSpellIntervalMs >= 0 ? attackSpellIntervalMs : 1500;
        if (!hasServerGates) interval = Math.Max(interval, NoGatesAttackFloorMs);
        if (newTarget && hasServerGates) return Math.Min(interval, NewTargetCastFloorMs);
        return interval;
    }

    public bool Think()
    {
        _tickWait = CombatLatencyTracker.Wait.Other;
        _attackedThisTick = false;
        bool result = ThinkCore();

        // [CombatLatency]: charge this tick to what it waited on, or log the engagement once
        // its first attack/cast went out (see CombatLatencyTracker).
        if (_latency.Pending)
        {
            double nowMs = Environment.TickCount64;
            if (activeTargetId == 0)
                _latency.OnDrop();
            else if (_attackedThisTick)
            {
                string? line = _latency.OnAttack(activeTargetId, nowMs, CombatModeName(CurrentCombatMode),
                    _worldFilter[activeTargetId]?.Name);
                if (line != null) _host.Log(line);
            }
            else
                _latency.OnTick(activeTargetId, nowMs, _tickWait);
        }
        return result;
    }

    private bool ThinkCore()
    {
        _rebindThisTick = false;
        if (!_settings.EnableCombat) return false;

        // A stall-watchdog re-wield must finish (weapon back in hand) before anything
        // attacks again, target or not — see ContinueStallRewield.
        if (_stallRewieldId != 0 && ContinueStallRewield())
        {
            _stallWatch.Hold(RynthClock.Now);
            return true;
        }

        // A back-away (a server plugin's danger, e.g. RynthILT's Empyrean Handler ring): hold
        // casts/swings until the jump settles, then fall through and re-engage the same target.
        if (TickBackAway())
            return true;

        double acDistanceLimit = _settings.MonsterRange;
        // Hysteresis: a locked target is retained (and kept under attack) out to
        // the larger disengage distance, while new targets are still only
        // acquired within MonsterRange (ScanNearbyTargets). The gap absorbs the
        // step-back AC applies on combat-mode entry so a mob at the edge doesn't
        // oscillate engage<->peace.
        double disengageLimit = DisengageDistance;
        // Note: _blacklistManager self-expires entries on read (IsBlacklisted), so no
        // explicit cleanup pass is needed. The old vestigial `blacklistedTargets` dict
        // and its CleanupExpiredBlacklist helper were removed — they were never populated.
        _blacklistManager.AttemptThreshold = 1; // one report = blacklist; the N-cast count is controlled by JudgePendingCast
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
            fsm.MissileArcClearance   = _settings.MissileArcClearance;
        }

        // Deferred predicted kill-shot swap: a cast that armed it is confirmed
        // accepted once OffensiveRefusalWindowMs elapses with no "You're too
        // busy!" (ACE has no positive "You cast X" for war magic). Run BEFORE the
        // scan so the dropped mob is suppressed this same tick and acquisition
        // picks the next target — preserving the old swap cadence on real kills,
        // while a REFUSED cast (which cleared the arm) now keeps + retries its
        // target instead of churning the swarm and pinning AC's busy count.
        if (_predictKillSwapArmed)
        {
            // Observe the cast WINDUP: CanCastNow goes false while the cast
            // gesture animates ([CMI+0x80] non-empty), true once it releases.
            bool gestureInFlight = _host.HasGetCastBusyState && !_host.CanCastNow;
            if (gestureInFlight)
                _predictSwapGestureSeen = true;

            // Uses the arm's own copy of the cast target/time: the pending-offensive fields are
            // consumed by JudgePendingOffensiveCast at the next attack cycle (1.5 s), which
            // used to discard the arm as "stale" before a ~2 s windup ever released, so the
            // swap never fired for high-tier casts and the 3 s timeout was unreachable. A
            // refused/fizzled/resisted cast still cancels the arm in HandleChatForDebuffs.
            if (activeTargetId == 0 || _predictArmTargetId != activeTargetId)
                _predictKillSwapArmed = false; // target already advanced/dropped — stale arm
            else if ((RynthClock.Now - _predictArmCastAt).TotalMilliseconds >= OffensiveRefusalWindowMs)
            {
                // ⚠ Do NOT drop the target while the cast gesture is still
                // animating. A high-tier cast (Force Arc VII) winds up ~2s,
                // FAR longer than the 500ms refusal window — swapping at 500ms
                // dropped the target mid-windup and ABORTED the cast before it
                // reached the server (full gesture + bolt played, but no
                // "You cast", zero damage; two mobs ping-ponged, nothing died —
                // 2026-06-13). Wait until the windup has been SEEN and then
                // completed (released): then the cast has actually landed its
                // damage and the predicted swap is correct. Bounded by a
                // timeout so a missing/forever gesture can't arm-lock combat.
                bool released = _predictSwapGestureSeen && (!_host.HasGetCastBusyState || _host.CanCastNow);
                bool timedOut = (RynthClock.Now - _predictArmCastAt).TotalMilliseconds >= PredictKillSwapMaxWaitMs;
                if (released || timedOut)
                    ExecuteDeferredKillSwap();
            }
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
            else if (target != null && activeTargetId != ForcedTargetId && PriorityOf(activeTargetId) <= 0)
            {
                // The rule says ignore (it changed, or the rule cache caught up).
                ForgetScanned(activeTargetId);
                DropTarget("ignored (monster rule Priority 0)");
            }
            else if (target != null && IsSpellProjectileName(target.Name))
                DropTarget("spell projectile (not a monster)");
            else if (target != null && (int)target.ObjectClass != (int)AcObjectClass.Monster)
            {
                ForgetScanned(activeTargetId);
                DropTarget("became corpse");
            }
            else if (_worldFilter.GetHealthRatio(activeTargetId) == 0f)
            {
                ForgetScanned(activeTargetId);
                DropTarget("hp=0");
            }
            else if (target != null && !KiteActive &&
                     _worldFilter.Distance(_host.GetPlayerId() == 0 ? 0 : (int)_host.GetPlayerId(), activeTargetId) > disengageLimit)
                DropTarget("out of range");
            // If target == null here it's a transient world-filter miss — keep the lock,
            // the stillScanned grace period below will handle a truly dead/vanished mob.
        }

        // Pick every tick: the initial pick, and a switch when the target is no longer valid,
        // a higher-priority monster is in range, or (Target Lock off) another is clearly better.
        HandleCombatTrigger();
        // A higher-priority monster in range that isn't a candidate (behind a wall, blacklisted):
        // one log line saying why the target stays (R-20261031-201214-8be7).
        NotePassedOverWhileEngaged();

        if (activeTargetId == 0)
        {
            if (RearmAfterVitalsStep()) return true;
            // No valid target — go to Peace immediately to stop the client
            // from auto-running toward a distant monster.  The 500ms cooldown
            // prevents ChangeCombatMode spam while idling between spawns.
            if (CurrentCombatMode == CombatMode.NonCombat)
                _idlePeaceAttempts = 0;   // reached peace — the next idle request starts fresh
            bool peaceHeld = false;
            // !HasTargets, not an empty scan: a predicted kill still alive and in range (its death
            // unconfirmed, scan-suppressed for a moment) is fought again in a second or two, and a
            // peace swap now would be undone at once (R-20261025-183343-727b).
            if (_settings.PeaceModeWhenIdle && CurrentCombatMode != CombatMode.NonCombat
                && !HasTargets && BusyCount == 0
                && RynthClock.Now >= _navMagicWantedUntil)   // a nav recall wants Magic mode
            {
                try { peaceHeld = HoldIdlePeace?.Invoke() == true; } catch { peaceHeld = false; }
                if (peaceHeld && !_idlePeaceHeldLogged)
                {
                    _idlePeaceHeldLogged = true;
                    _host.Log($"[EquipDiag] idle peace-mode swap held: looting is working (a mode change would cancel the server's walk/turn to the corpse); it goes out once looting is done.");
                }
            }
            if (!peaceHeld) _idlePeaceHeldLogged = false;
            if (!peaceHeld && _settings.PeaceModeWhenIdle && CurrentCombatMode != CombatMode.NonCombat
                && !HasTargets && BusyCount == 0
                && RynthClock.Now >= _navMagicWantedUntil)   // a nav recall wants Magic mode
            {
                // Defer idle peace-swap while a cast at the just-dropped target is still
                // resolving: ChangeCombatMode truncates the in-flight gesture → strands
                // Player.IsBusy. Bounded by IsCastInFlight (UseDone-seq or 2.5s).
                // Backoff like the stance flips: 500 ms for the first 4 sends, doubling every
                // 4 after that up to 15 s. An unbounded ChangeCombatMode stream is the
                // 2026-05-25 stance-spam crash class; this used to retry every 500 ms forever.
                double peaceDelayMs = Math.Min(15_000, 500 * Math.Pow(2, Math.Min(5, _idlePeaceAttempts / 4)));
                if ((RynthClock.Now - _lastPeaceAttempt).TotalMilliseconds > peaceDelayMs && !IsCastInFlight()
                    && _host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "idle: no target (Peace Mode When Idle)"))
                {
                    _lastPeaceAttempt = RynthClock.Now;
                    _idlePeaceAttempts++;
                    if (_idlePeaceAttempts == 16)
                        _host.Log($"[EquipDiag] WARNING: {_idlePeaceAttempts} idle peace-mode requests without reaching peace (mode={CurrentCombatMode}) — backing off to {Math.Min(15_000, 500 * Math.Pow(2, Math.Min(5, _idlePeaceAttempts / 4))) / 1000:0.#}s retries.");
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
                _targetLostScanTime = RynthClock.Now;

            if ((RynthClock.Now - _targetLostScanTime).TotalMilliseconds > TARGET_SCAN_GRACE_MS)
                DropTarget("scan grace expired");
            return true;
        }
        _targetLostScanTime = DateTime.MinValue; // back in scan — reset grace timer

        var targetObj = _worldFilter[activeTargetId];
        if (targetObj == null) return true; // transient miss — skip tick, keep lock

        // Pin kill-shot fight tracking to the active target (covers initial lock,
        // target switches, and lock-restore after a transient miss), and keep
        // trying to resolve its HP until known (the appraisal lands async).
        if (activeTargetId != _fightTargetId)
            BeginFight(activeTargetId);
        else if (_fightTargetMaxHp <= 0 || _fightHpEstimated)
            ResolveFightHp();
        // Keep trying to capture the mob's name (the world filter fills it in
        // asynchronously, so it's often empty at lock time) — by kill time the
        // learning file then has a real name, not a blank.
        if (_fightTargetName.Length == 0)
        {
            string? nm = _worldFilter[activeTargetId]?.Name;
            if (!string.IsNullOrEmpty(nm)) _fightTargetName = nm;
        }

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
            if ((RynthClock.Now - refTime).TotalSeconds > noProgressTimeoutSec)
            {
                _host.WriteToChat($"[RynthAi] No progress on {targetObj.Name} after {noProgressTimeoutSec}s — blacklisting", 2);
                _blacklistManager.TimeoutSeconds = _settings.BlacklistTimeoutSec;
                _blacklistManager.ReportFailure(activeTargetId);
                DropTarget("no-progress timeout");
                return true;
            }
        }

        // Distance gate: don't issue any attack commands (SelectItem, attack, cast)
        // when the target is beyond the disengage distance. Within the deadband
        // (MonsterRange..disengage) we KEEP issuing commands so AC's auto-run
        // pulls the character back into engage range after the combat-mode
        // step-back — instead of dropping to peace and restarting the approach.
        double currentDist = _worldFilter.Distance(
            _host.GetPlayerId() == 0 ? 0 : (int)_host.GetPlayerId(), activeTargetId);
        // Pull-away kite runs before the disengage gate: fleeing intentionally stretches
        // range, and the kite itself waits for catch-up rather than idling here.
        if (RunKite(targetObj, currentDist))
            return true;
        if (currentDist > disengageLimit)
            return true;

        // Environment-impact throttle: too many missiles/spells into walls short of this target
        // stop the attack and move closer or switch target (CombatManager.EnvironmentImpact.cs).
        if (RunEnvironmentImpact(targetObj, currentDist))
            return true;

        // Melee/missile stall safety net (see CombatStallWatchdog). Holds the attack while
        // one of its steps is settling.
        if (RunStallWatchdog(targetObj, currentDist))
            return true;

        // One server action at a time (2026-10-06, Plugins/Shared/ActionPacer.cs): no stance change,
        // wield, attack or cast while the server is still answering the last action (a kit, a corpse
        // use, a buff, the last stance change). On ACE a mode change cancels a pending use's walk
        // and fails a cast in progress; that interleaving began every stuck-use stall on record.
        // Melee/missile attack repeats skip the animation and settle gates and the 'too busy' latch (ACE
        // sends no UseDone for a swing, so a latch set by a refused pickup would starve them): the server swings
        // on its own timers, a swing can't land inside a use's server turn because the use above holds
        // every attack until it is answered, and waiting for each swing animation would cost DPS. A
        // stance change still waits (ChangeCombatModeFor), and so does everything in peace/magic.
        // The movement settle is for casts (a cast beside a stop or a turn was held 40.8 s by the server):
        // only in Magic mode. From peace the next step is a stance change or a wield, and waiting out the
        // nav stop first cost every fight's first swing up to 0.6 s (2026-10-14).
        bool attackRepeat = CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile;
        if (!ActionPacer.MayStart(_host, "Combat", HealNeed.None, out _, animationGate: !attackRepeat,
                settleGate: CurrentCombatMode == CombatMode.Magic, attackRepeat: attackRepeat))
        {
            LastCombatSkipReason = "server-answering"; // D4 record-only
            _tickWait = CombatLatencyTracker.Wait.Busy;
            return true;
        }

        // While a targeted combat cast is still resolving on the server, do NOT re-assert
        // stance/weapon here: EquipWeaponAndSetStance can emit ChangeCombatMode/UseObject,
        // motion-replacing actions that truncate the in-flight cast gesture → orphan the cast
        // → strand Player.IsBusy (relog-only wedge). Magic-only (melee re-equips every tick and
        // never arms the flag); bounded by IsCastInFlight (UseDone-seq or 2.5s); re-runs next tick.
        if (!(CurrentCombatMode == CombatMode.Magic && IsCastInFlight()))
        {
            if (!EquipWeaponAndSetStance(targetObj, "Auto"))
            {
                _tickWait = CombatLatencyTracker.Wait.Stance;
                return true;
            }
        }

        bool useNative = _settings.UseNativeAttack && _host.HasNativeAttack;

        // Melee turn motions are managed by the facing gate below (direct-attack
        // path) — it clears them the moment heading is within tolerance, which
        // also mops up anything navigation left running. The native path never
        // sets them and lets AC turn the avatar itself.

        // Attack magic uses its OWN interval (AttackSpellIntervalMs, default
        // 1500ms) so war/void combat casts can be spaced ~1-2s without slowing
        // buff chains — buffs keep the faster SpellCastIntervalMs. Spacing the
        // offensive casts stops back-to-back "You're too busy!" refusals that
        // silently drop casts and cost kills. ≤0 (e.g. a pre-existing settings
        // file saved before this field existed) falls back to 1500ms.
        // The first cast at a NEW target (after a kill or a predicted-kill swap) doesn't wait out
        // the interval left over from the last target when the engine reports UseDone: the
        // cast-resolution gate (UseDone) and the gesture gate (CanCastNow) below already hold it
        // until the previous cast is finished, which is what the interval stood in for before
        // those gates existed (it predates them, 2026-06-06 vs 06-20). See AttackIntervalMs.
        double attackCmdIntervalMs = AttackIntervalMs(CurrentCombatMode, _settings.AttackSpellIntervalMs,
            hasServerGates: _host.HasUseDoneSeq && _host.HasGetCastBusyState,
            newTarget: activeTargetId != _lastAttackCmdTargetId);
        if ((RynthClock.Now - lastAttackCmd).TotalMilliseconds < attackCmdIntervalMs)
            _tickWait = CombatLatencyTracker.Wait.Interval;
        else
        {
            _offensiveCastThisCycle = false;
            // Convert any pending offensive cast whose refusal window has
            // elapsed into a queued judgement (ACE has no "You cast X" for
            // war magic; this is our successful-cast detector).
            JudgePendingOffensiveCast();
            JudgePendingCast(); // verdict on the last cast once its window elapses

            // Client is busy processing a previous action — don't queue more. Unless the count
            // is a leftover: the server finished every action behind it (a UseDone arrived after
            // it last rose) and no gesture is animating — AC lowered it inline, where the busy
            // hook can't see it, and it would otherwise hold combat until the 5 s force-clear.
            // The shared mirror is reset (not AC's real field — the engine reconciler owns that),
            // the same as BuffManager does when a buff resolves. See CombatBusyGate.
            if (BusyCount > 0)
            {
                if (!BusyIsLeftover())
                {
                    LastCombatSkipReason = "busy-count"; // D4 record-only
                    _tickWait = CombatLatencyTracker.Wait.Busy;
                    return true;
                }
                int was = BusyCount;
                _staleBusyResets++;
                _onStaleBusy?.Invoke($"combat: the server finished the action (UseDone) — was {was}");
                if (BusyCount > 0) BusyCount = 0;   // no callback, or the shared mirror was already 0
                _busyGate.Reset();
            }

            // In the air after a jump-loot pickup's jump: ACE refuses a cast, swing or shot in the
            // air (YouCantDoThatWhileInTheAir), so the attack waits for the landing (capped at
            // ActionPacer.JumpLandMaxMs). Only the attack: target, approach and stance above go on;
            // nothing is stamped, counted or judged for the held tick.
            if (ActionPacer.LootJumpAirborne(_host, out _))
            {
                LastCombatSkipReason = "loot-jump-air"; // D4 record-only
                _tickWait = CombatLatencyTracker.Wait.Busy;
                return true;
            }

            // Magic cadence guard: while a previous combat cast is still
            // resolving on the ACE server, do NOT turn or issue another cast.
            // A turn/stop MoveToState (or the next cast's free-hands stop-thunk)
            // landing during the server's deferred targeted-cast windup orphans
            // the prior cast → 0 damage. Resolves on a server UseDone (poll) or
            // a hard timeout. Magic only; melee/missile are AC-paced.
            if (CurrentCombatMode == CombatMode.Magic && IsAwaitingCastResolution())
            {
                LastCombatSkipReason = "cast-cadence"; // D4 record-only (IsCastInFlight)
                _tickWait = CombatLatencyTracker.Wait.Cast;
                return true;
            }

            // Casts landing nothing: one short real sidestep re-syncs the server's copy of our
            // position (TickResyncStep, 2026-10-12); nothing is cast while it runs.
            if (CurrentCombatMode == CombatMode.Magic && TickResyncStep())
            {
                LastCombatSkipReason = "position-resync";
                _tickWait = CombatLatencyTracker.Wait.Cast;
                return true;
            }

            // MAGIC and MISSILE: the server turns the player to the target itself,
            // as retail did — ACE Rotate(target) before a targeted cast (and again
            // after the windup if needed) and before the first missile shot and
            // between repeat shots. So the bot doesn't turn at all. A client-side
            // turn servo on top only fought that rotation: missile characters spun
            // in circles between shots (2026-09-27, Lucy, Olthoi swarm at close
            // range), and for magic the server waits while the client holds a turn
            // (PendingTurnRelease), which is where the "animates but 0 damage / too
            // busy" orphaned windups came from. Applies with or without
            // UseNativeAttack. Only a turn another mode left held is released — with one
            // settle tick before a cast, so the stop doesn't ride with the packet.
            // MELEE the same: ACE's HandleActionTargetedMeleeAttack_Inner rotates to the
            // target (past melee_max_angle) before the swing, so the servo made the same
            // swing-spin-swing with a knife in an Olthoi swarm (2026-09-29, Lucy).
            // The one exception is a WALL spell (2026-10-08): untargeted, so the server doesn't
            // turn for it. AttackWithMagic snaps the heading before a wall (HoldForWallFacing),
            // behind the cadence guard above, and never during a windup.
            if (CurrentCombatMode == CombatMode.Magic || CurrentCombatMode == CombatMode.Missile
                || CurrentCombatMode == CombatMode.Melee)
            {
                if (_facingTarget)
                {
                    ClearCombatTurnMotions();
                    _facingTarget = false;
                    _faceSettledAt = RynthClock.Now;
                    LastCombatSkipReason = "face-settle-release"; // D4 record-only
                    _tickWait = CombatLatencyTracker.Wait.Face;
                    return true; // settle tick
                }
                if (CurrentCombatMode == CombatMode.Magic
                    && (RynthClock.Now - _faceSettledAt).TotalMilliseconds < FACE_SETTLE_MS)
                {
                    LastCombatSkipReason = "face-settle-wait"; // D4 record-only
                    _tickWait = CombatLatencyTracker.Wait.Face;
                    return true; // let the turn-stop settle on the server first
                }
            }
            // Native attack handles facing internally — skip manual facing.
            // Otherwise turn to face before swinging or firing: the direct
            // MeleeAttack/MissileAttack game actions bypass the client's
            // turn-to-face, so without this a non-native melee box swings at
            // whatever heading nav left it on. AC paces the swing/shot itself,
            // so the cast-windup orphaning that Magic guards against above
            // doesn't apply — a plain servo is enough here.
            else if (!useNative)
            {
                double facingError = GetFacingError(activeTargetId);
                if (facingError > FACE_TOLERANCE_DEG)
                {
                    FaceTarget(activeTargetId);
                    if (!_facingTarget)
                    {
                        _facingTarget = true;
                        _faceStartTime = RynthClock.Now;
                    }
                    if ((RynthClock.Now - _faceStartTime).TotalMilliseconds < FACE_TIMEOUT_MS)
                    {
                        _tickWait = CombatLatencyTracker.Wait.Face;
                        return true; // not facing yet, keep waiting
                    }
                }
                _facingTarget = false;
                ClearCombatTurnMotions();
            }

            // The exception to "missile never turns": a target at point blank with the character's
            // back to it (PointBlankFacing). One snap, then the attack waits for it to settle.
            if (HoldForPointBlankFacing(currentDist))
            {
                LastCombatSkipReason = "point-blank-face"; // D4 record-only
                _tickWait = CombatLatencyTracker.Wait.Face;
                return true;
            }

            // Don't attack in missile mode without ammo
            if (CurrentCombatMode == CombatMode.Missile && !HasWieldedAmmo())
            {
                _lastMissileNoAmmoAt = RynthClock.Now;   // waiting on ammo is not a stall
                LastCombatSkipReason = "no-ammo";      // D4 record-only
                return true;
            }

            // SelectItem removed 2026-06-03: targeted casts now use the explicit-target
            // FreeHandsAndCastSpell path and direct attacks pass targetId explicitly, so
            // AC's global selection no longer needs setting here. Setting it stole the
            // user's inventory selection every combat tick (AC has a single selection) and
            // was an off-thread SetSelectedObject mutation (not marshalled by P1). Matches
            // RC2's RynthBot, which never selects. (Native physical attack in AttackTarget
            // still selects — it genuinely requires AC's selection.)

            bool physicalSent = true;   // false when AttackTarget sent nothing this cycle
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
                if (!CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s)))
                {
                    if ((RynthClock.Now - lastAttackCmd).TotalMilliseconds > 5000)
                        _host.Log($"[CombatCast] CanCastNow=false — gesture gate blocking cast (last attack {(RynthClock.Now - lastAttackCmd).TotalMilliseconds:0}ms ago, target=0x{activeTargetId:X8})");
                    LastCombatSkipReason = "cast-gate"; // D4 record-only (CanCastNow=false)
                    _tickWait = CombatLatencyTracker.Wait.Cast;
                    return true;
                }

                AttackWithMagic(targetObj);

                // A wall held for its turn (2026-10-08): nothing was sent but a heading snap. End
                // the tick here so it doesn't count as an attack: the attack interval doesn't start
                // and the wall goes the first tick after the settle (HoldForWallFacing).
                if (_wallFaceHeld)
                {
                    _tickWait = CombatLatencyTracker.Wait.Face;
                    return true;
                }
                // Held for mana (the reserve, or under the spell's cost): nothing was sent, so
                // it isn't an attack either; the next tick asks again.
                if (_manaCastHeld)
                {
                    _tickWait = CombatLatencyTracker.Wait.Cast;
                    return true;
                }
                // The first cast after a wield waits for the stance to settle (WandSwapRules): nothing
                // was sent, so the attack interval doesn't start; the next tick asks again.
                if (_equipGateHeld)
                {
                    _tickWait = CombatLatencyTracker.Wait.Cast;
                    return true;
                }

                // Waiting on a script's choice of spell (RynthAi.Combat): nothing was sent. The same
                // as the wall hold: the attack interval doesn't start, the target and scan stay as they
                // are, and the next tick asks again (bounded by the request's deadline).
                if (_scriptHeld)
                {
                    _tickWait = CombatLatencyTracker.Wait.Other;
                    return true;
                }

                // Don't restore the physical weapon on top of a cast just issued this tick
                // (AttackWithMagic → MarkCombatCastIssued): the UseObject + re-equip below would
                // truncate the in-flight gesture and strand Player.IsBusy. Defer one windup
                // (bounded); _returnToPhysicalCombat stays true so the restore still runs after.
                if (_returnToPhysicalCombat && !IsCastInFlight())
                {
                    var rule2 = GetRuleForTarget(targetObj);
                    string elem2 = GetPreferredElement(targetObj, rule2);
                    if (rule2 != null && !HasPendingDebuffs(rule2, elem2))
                    {
                        _returnToPhysicalCombat = false;
                        if (_savedWeaponId != 0
                            && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("combat-restore")))
                        {
                            _host.UseFor((uint)_savedWeaponId, "Combat", "wield the weapon again after casting");
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
                        // This quick swap (UseObject wand + Magic in one go) can't work with a
                        // melee/missile weapon in hand: ACE won't wield the wand over it and
                        // denies Magic while it's wielded (see the dequip-first notes at the
                        // top), and EquipWeaponAndSetStance would switch straight back anyway.
                        // It only cost each new target a refused swap, a lost attack cycle and
                        // the swap gate. Skip it then; it still runs when the hands are empty.
                        // A shield or an off-hand weapon blocks the wand the same way (ACE refuses a caster beside it).
                        if (wandId != 0 && (FindWieldedNonWandWeapon(wandId) != 0 || OffhandInSlot() != 0))
                        {
                            if (!_physicalDebuffSkipWarned)
                            {
                                _physicalDebuffSkipWarned = true;
                                _host.Log($"[CombatCast] debuffs for '{rule.Name}' skipped in {CombatModeName(CurrentCombatMode)} mode — can't swap to the wand with a weapon in hand");
                                _host.WriteToChat($"[RynthAi] Debuffs aren't cast in melee/missile mode with a weapon in hand (the wand swap doesn't work there). Fighting without them.", 2);
                            }
                        }
                        else if (wandId != 0
                            && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("combat-debuff-wand")))
                        {
                            // TODO: Save current equipped weapon when inventory API is available.
                            _savedWeaponId = 0;
                            _returnToPhysicalCombat = true;
                            _host.UseFor((uint)wandId, "Combat", "wield the wand to cast");
                            _lastEquipTime = RynthClock.Now; // gate AttackWithMagic until wand is wielded
                            // Held by the pacing until the wield is answered; the wielded branch of
                            // EquipWeaponAndSetStance sends it then (2026-10-06).
                            _host.ChangeCombatModeFor(CombatMode.Magic, "Combat", "debuff: Magic mode after the wand");
                            lastAttackCmd = RynthClock.Now;
                            _lastAttackCmdTargetId = activeTargetId;
                            return true;
                        }
                    }
                }

                // Physical combat always attacks — spell shape flags (UseArc/Bolt/Ring/Streak)
                // are only relevant in magic mode and must not gate melee/missile attacks.
                physicalSent = AttackTarget();
                if (physicalSent) _attackedThisTick = true;
            }

            // Ring spells hit an area — no per-target damage feedback is generated,
            // so they must not count toward the blacklist miss counter.
            // Magic mode no longer counts from here at all: RecordOffensiveCast
            // is invoked from the chat-confirmation path ("ou cast X" matching
            // _pendingOffensiveSpellId) so a server-refused attempt ("too busy",
            // equip waits, "no spell found" etc.) never queues a judgement.
            // Melee/missile attack every cycle and get prompt damage feedback,
            // so they keep per-cycle counting (the magicMode branch is false).
            bool magicMode = CurrentCombatMode == CombatMode.Magic && _spellManager != null;
            if (!_lastCastWasRing && (!magicMode || _offensiveCastThisCycle) && physicalSent)
                RecordOffensiveCast(activeTargetId);
            lastAttackCmd = RynthClock.Now;
            _lastAttackCmdTargetId = activeTargetId;
            // A rebind sent only the CancelAttack: the attack follows as soon as the cancel can
            // have landed, not a whole interval later ("lock->attack 1750 ms (interval 1671)").
            if (!physicalSent && _rebindThisTick)
                lastAttackCmd = AttackAfterCancel(lastAttackCmd);

            // Predicted kill shot (set in AttackWithMagic): the cast we just fired
            // is expected to finish this mob. DON'T swap yet — on ACE a war cast is
            // only confirmed accepted ~OffensiveRefusalWindowMs later (the absence
            // of "You're too busy!"). Dropping the target 1ms after issuing meant a
            // REFUSED cast still abandoned a live mob and advanced to the next,
            // churning the whole swarm and pinning AC's busy count (the wedge).
            // Arm it; the deferred-swap check at the top of Think executes it once
            // the cast is confirmed, and the "too busy"/fizzle handlers cancel it.
            if (_predictKillSwap)
            {
                _predictKillSwap = false;
                if (activeTargetId != 0)
                {
                    _predictKillSwapArmed = true;
                    _predictSwapGestureSeen = false; // must observe the cast windup before swapping
                    // AttackWithMagic just set these for the cast that predicted the kill.
                    _predictArmTargetId = _pendingOffensiveTargetId != 0 ? _pendingOffensiveTargetId : activeTargetId;
                    _predictArmCastAt   = _pendingOffensiveCastAt != DateTime.MinValue ? _pendingOffensiveCastAt : RynthClock.Now;
                }
            }
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
            : (long)(RynthClock.Now - lastAttackCmd).TotalMilliseconds;
        string attackBucket = msSinceAttack < 0 ? "never"
                            : msSinceAttack < 3000 ? "recent"
                            : msSinceAttack < 10_000 ? "stale"
                            : "cold";
        string key = $"enableCombat={_settings.EnableCombat} scanned={_scannedTargets.Count} active=0x{activeTargetId:X8} busy={BusyCount} mode={CurrentCombatMode} attack={attackBucket} action='{_settings.BotAction}'";
        if (key == _lastCombatStateKey) return;
        _lastCombatStateKey = key;
        _host.Log($"Combat: state {key} (msSinceAttack={msSinceAttack})");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  STALL WATCHDOG (melee/missile) — see CombatStallWatchdog
    // ══════════════════════════════════════════════════════════════════════════

    private const double StallReselectSuppressMs = 3000;   // step 2: skip the stuck mob this long if another is in reach
    private const double StallToggleHoldMs       = 5000;   // step 3: peace→back window that still counts as eligible

    /// <summary>
    /// Called from Think once a live target is validated and within disengage distance.
    /// Returns true when this tick's attack must be held (a recovery step was just sent).
    /// Runs only while the arbiter has given combat the tick and BotAction is Combat, so it
    /// never competes with buffing, looting or navigation.
    /// </summary>
    private bool RunStallWatchdog(WorldObject targetObj, double currentDist)
    {
        DateTime now = RynthClock.Now;
        int mode = CurrentCombatMode;
        bool physical = mode == CombatMode.Melee || mode == CombatMode.Missile;
        bool toggling = _stallToggleAt != DateTime.MinValue
                        && (now - _stallToggleAt).TotalMilliseconds < StallToggleHoldMs;
        bool rewielding = _stallRewieldId != 0;
        double range = Math.Max(2.0, _settings.MonsterRange);

        bool eligible = _settings.BotAction == "Combat"
                        && activeTargetId != 0
                        && (physical || toggling)
                        && currentDist <= range
                        && _worldFilter.GetHealthRatio(activeTargetId) != 0f
                        && !(mode == CombatMode.Missile && (now - _lastMissileNoAmmoAt).TotalMilliseconds < 3000);

        // While the client is busy combat sends nothing (the attack block's busy gate), so no
        // swing evidence can arrive — and the steps (mode change, moving the weapon to a pack)
        // are exactly the actions that must not go out then. Don't fire a step; keep the
        // episode (Hold, so this isn't read as a tick gap) so brief busy pulses between
        // attacks change nothing and the ladder resumes once busy clears.
        if (eligible && BusyCount > 0)
        {
            _stallWatch.Hold(now);
            return false;
        }

        bool quiet = SampleMeleeQuiet(now, eligible && mode == CombatMode.Melee);

        // After the step-1 re-issue: still no swing, not moved, nothing from the server = a ghost.
        if (_ghostCheck.IsArmed)
        {
            int tid = activeTargetId;
            double silent = ServerSilentMs(tid, now);
            if (_ghostCheck.Check(tid, now, _lastSwingEvidenceAt, TargetSpot(tid), silent) == GhostCheck.Verdict.Ghost)
            {
                MarkGhost(tid, targetObj.Name, silent, $"no swing after {_attacksSinceEvidence} attack(s) and a re-issue");
                lastAttackCmd = now;
                return true;
            }
        }

        double stallAfter = mode == CombatMode.Missile && currentDist <= CombatStallWatchdog.PointBlankMissileM
            ? CombatStallWatchdog.PointBlankStallMs : CombatStallWatchdog.StallMs;
        var act = _stallWatch.Tick(now, eligible, _lastSwingEvidenceAt, out double stalledMs, quiet, stallAfter);
        switch (act)
        {
            case CombatStallWatchdog.StallAction.None:
                return false;

            case CombatStallWatchdog.StallAction.Recovered:
                _host.Log($"[StallWatch] recovered — {_lastSwingEvidenceKind} after step {LastStallStepName} " +
                          $"({stalledMs / 1000:0.0}s without swings; episodes={_stallWatch.Episodes} recovered={_stallWatch.Recoveries})");
                return false;

            case CombatStallWatchdog.StallAction.GaveUp:
                _host.Log($"[StallWatch] all {CombatStallWatchdog.StepCount} steps done, still no swings on 0x{activeTargetId:X8} '{targetObj.Name}' — " +
                          $"resting {CombatStallWatchdog.CooldownMs / 1000:0}s (targeting and no-progress rules carry on)");
                return false;
        }

        if (rewielding)
            return true; // a step-4 re-wield is still settling; the ladder waits for it

        _lastStallStep = act;
        if (act == CombatStallWatchdog.StallAction.Reissue)
            LogStallSnapshot(targetObj, currentDist, stalledMs);

        string head = $"[StallWatch] step {_stallWatch.CurrentStep}/{CombatStallWatchdog.StepCount}";
        switch (act)
        {
            case CombatStallWatchdog.StallAction.Reissue:
            {
                // A re-issue alone never fixed a point-blank target behind the character.
                HoldForPointBlankFacing(currentDist, force: true);
                ReleaseServerAttack("stall watchdog", log: false);
                lastAttackCmd = AttackAfterCancel(now); // the attack goes out again once the cancel has landed
                _ghostCheck.Arm(activeTargetId, now, TargetSpot(activeTargetId));
                _reachWatch.NoteReissue(activeTargetId, currentDist);
                // With native attack: as the regular attack, which a stuck client pipeline can't hold (NativeForThisAttack).
                bool nativeReset = _settings.UseNativeAttack && _host.HasNativeAttack;
                if (nativeReset) _nativeResetPending = true;
                _host.Log($"{head} re-issue: CancelAttack, attack 0x{activeTargetId:X8} again next cycle" +
                          (nativeReset ? " as the regular attack" : "") +
                          (_stallWatch.LastFireWasEarly ? $" (early: {stalledMs / 1000:0.0}s with no swing playing and the character standing still)" : ""));
                return true;
            }

            case CombatStallWatchdog.StallAction.Reselect:
            {
                ReleaseServerAttack("stall watchdog", log: false);
                int old = activeTargetId;
                // The walk to it isn't getting anywhere (a wall between us): leave it, and spare the
                // next target the rest of the ladder. See MeleeReachWatch.
                if (mode == CombatMode.Melee && _reachWatch.CantReach(old, currentDist, _lastSwingEvidenceAt))
                {
                    int skipSec = _reachWatch.NoteFailure(old, now);
                    _blacklistManager.BlacklistFor(old, skipSec);
                    _stallWatch.EndEpisode();
                    _host.Log($"[StallWatch] can't reach 0x{old:X8} '{targetObj.Name}': still {currentDist:0.0} m away after the re-issue (a wall between us?); leaving it {skipSec}s.");
                    DropTarget($"can't reach it for {skipSec}s");
                    lastAttackCmd = now;
                    return true;
                }
                bool alt = HasAlternateTarget(old);
                if (alt) _recentlyKilled[old] = now.AddMilliseconds(StallReselectSuppressMs);
                _host.Log($"{head} re-select: dropping 0x{old:X8} '{targetObj.Name}'" +
                          (alt ? $" and skipping it {StallReselectSuppressMs / 1000:0}s (another mob is in reach)" : " (no other mob in reach, it may be picked again)"));
                DropTarget("stall watchdog: re-select");
                lastAttackCmd = now;
                return true;
            }

            case CombatStallWatchdog.StallAction.ToggleMode:
                // Same effect on the server as the wand heal: a mode change runs
                // HandleActionCancelAttack. EquipWeaponAndSetStance flips back after its
                // stance retry delay (~1 s with the flip backoff reset).
                // Only a toggle the engine accepted starts the flip-back bookkeeping. A refusal (a teleport,
                // a death) leaves it untouched and takes the ladder step back (Unfire), so it spends no step
                // and the toggle is asked for again next tick.
                if (_host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat/Stall", "stall watchdog: toggle the mode", force: true))
                {
                    _stallToggleAt = now;
                    ResetStanceFlipBackoff();
                    lastStanceAttempt = now;
                    _serverAttackTargetId = 0;
                    _host.Log($"{head} toggle mode: peace, then back to {CombatModeName(mode)} on the next stance pass");
                    lastAttackCmd = now;
                }
                else
                {
                    _stallWatch.Unfire();
                }
                return true;

            case CombatStallWatchdog.StallAction.Rewield:
                StartStallRewield(head);
                lastAttackCmd = now;
                return true;
        }
        return false;
    }

    private CombatStallWatchdog.StallAction _lastStallStep = CombatStallWatchdog.StallAction.None;
    private readonly MeleeReachWatch _reachWatch = new();

    /// <summary>How long the server has said nothing about <paramref name="id"/>: the engine's
    /// record (plugin API v83) when it has one, else the plugin's own OnUpdateObject/OnCreateObject
    /// record; -1 when neither has any.</summary>
    internal double ServerSilentMs(int id, DateTime now)
    {
        if (_host.HasGetObjectServerAge)
        {
            int age = _host.GetObjectServerAge((uint)id);
            if (age >= 0) return age;
        }
        return Ghosts.SilentMs(id, now);
    }

    /// <summary>
    /// <paramref name="id"/> is a ghost: logged once, skipped by targeting until the server shows
    /// it is real or the landblock changes, dropped as the target, and removed from the client
    /// when the engine can (v83). Its no-damage blacklist entry, if any, is dropped (the ghost
    /// mark outlives it).
    /// </summary>
    internal void MarkGhost(int id, string? name, double silentMs, string why)
    {
        if (id == 0) return;
        uint pid = _playerId != 0 ? _playerId : _host.GetPlayerId();
        uint lb = pid != 0 && _host.TryGetObjectPosition(pid, out uint pc, out _, out _, out _) ? pc >> 16 : 0;
        GhostSpot spot = TargetSpot(id) ?? new GhostSpot(double.NaN, double.NaN, double.NaN);
        Ghosts.Mark(id, name ?? "", spot, lb, RynthClock.Now);
        _blacklistManager.ClearFailure(id);
        string silent = silentMs < 0 ? "nothing from the server about it since it was first seen" : $"nothing from the server about it for {silentMs / 1000:0}s";
        _host.Log($"[Ghost] 0x{id:X8} '{name}' is a ghost: {why}, {silent}, it hasn't moved [{DescribeTargetPos(id)}]. " +
                  $"Skipped until the server sends anything about it or the landblock changes (ghosts now: {Ghosts.Count}).");
        if (id == activeTargetId || id == _lockedTargetId)
            DropTarget("ghost (the server doesn't have it)");
        if (_host.HasRemoveClientObject)
        {
            int r = _host.RemoveClientObject((uint)id);
            _host.Log($"[Ghost] 0x{id:X8}: " + r switch
            {
                1 => "removed from the client",
                2 => "removal queued (AC's main thread)",
                -1 => "not removed: the client's DeleteObject isn't bound yet (no delete since the engine loaded)",
                _ => "not removed: the engine refused (see the engine log)",
            });
        }
    }

    /// <summary>The server sent an update for <paramref name="objectId"/> (RynthAiPlugin.OnUpdateObject).</summary>
    public void OnServerObjectUpdate(uint objectId) => Ghosts.NoteServerUpdate((int)objectId, RynthClock.Now);

    /// <summary>The server created <paramref name="objectId"/> (RynthAiPlugin.OnCreateObject).</summary>
    public void OnObjectCreated(uint objectId) => Ghosts.NoteServerUpdate((int)objectId, RynthClock.Now, "the server created it again");

    /// <summary>
    /// Samples the swing animation and the character's position every stall-watch tick and says
    /// whether melee is plainly stuck: at least two attacks sent since the last hit, miss or kill,
    /// nothing animating for <see cref="QuietNoAnimMs"/> and the character not moving for
    /// <see cref="QuietStillMs"/>. A swing in progress animates and a server move-to to the target
    /// moves the character, so neither reads as quiet. False without the engine's motion read.
    /// </summary>
    private bool SampleMeleeQuiet(DateTime now, bool melee)
    {
        if (!_host.HasGetCastBusyState) return false;
        // A gap (combat paused, a loot run): start the clocks over rather than read the gap as quiet.
        if (_lastQuietSampleAt == DateTime.MinValue || (now - _lastQuietSampleAt).TotalMilliseconds > CombatStallWatchdog.TickGapResetMs)
        {
            _lastAnimatingAt = now;
            _lastPlayerMovedAt = now;
            _moveAnchorX = double.NaN;
        }
        _lastQuietSampleAt = now;

        bool animating;
        try { animating = _host.GetCastBusyState() != 0; } catch { animating = true; }
        if (animating) _lastAnimatingAt = now;

        uint pid = _playerId != 0 ? _playerId : _host.GetPlayerId();
        if (pid != 0 && _host.TryGetObjectPosition(pid, out uint pc, out float px, out float py, out float pz))
        {
            double gx = ((pc >> 24) & 0xFF) * 192.0 + px, gy = ((pc >> 16) & 0xFF) * 192.0 + py;
            if (double.IsNaN(_moveAnchorX))
            {
                _moveAnchorX = gx; _moveAnchorY = gy; _moveAnchorZ = pz;
            }
            else
            {
                double dx = gx - _moveAnchorX, dy = gy - _moveAnchorY, dz = pz - _moveAnchorZ;
                if (dx * dx + dy * dy + dz * dz > QuietMoveM * QuietMoveM)
                {
                    _moveAnchorX = gx; _moveAnchorY = gy; _moveAnchorZ = pz;
                    _lastPlayerMovedAt = now;
                }
            }
        }
        else _lastPlayerMovedAt = now;   // no position: never call it standing still

        return melee
               && _attacksSinceEvidence >= 2
               && (now - _lastAnimatingAt).TotalMilliseconds >= QuietNoAnimMs
               && (now - _lastPlayerMovedAt).TotalMilliseconds >= QuietStillMs;
    }

    /// <summary>Where a target is (landblock-global x/y and z), or null.</summary>
    private GhostSpot? TargetSpot(int id)
    {
        if (id == 0 || !_host.TryGetObjectPosition((uint)id, out uint c, out float x, out float y, out float z)) return null;
        return new GhostSpot(((c >> 24) & 0xFF) * 192.0 + x, ((c >> 16) & 0xFF) * 192.0 + y, z);
    }

    /// <summary>The lastAttackCmd stamp that lets the next melee/missile attack go
    /// <see cref="AttackAfterCancelMs"/> after <paramref name="now"/> (the interval is 1000 ms there).</summary>
    private static DateTime AttackAfterCancel(DateTime now) => now.AddMilliseconds(AttackAfterCancelMs - 1000.0);

    /// <summary>Stall watchdog / attack-rebind counters for /ra why.</summary>
    public int StallEpisodes   => _stallWatch.Episodes;
    public int StallRecoveries => _stallWatch.Recoveries;
    public int AttackRebinds   => _attackRebinds;
    public string LastSwingEvidenceDescription => _lastSwingEvidenceAt == DateTime.MinValue
        ? "never"
        : $"{_lastSwingEvidenceKind} {(RynthClock.Now - _lastSwingEvidenceAt).TotalSeconds:0.0}s ago";
    private string LastStallStepName => _lastStallStep switch
    {
        CombatStallWatchdog.StallAction.Reissue    => "1 (re-issue)",
        CombatStallWatchdog.StallAction.Reselect   => "2 (re-select)",
        CombatStallWatchdog.StallAction.ToggleMode => "3 (toggle mode)",
        CombatStallWatchdog.StallAction.Rewield    => "4 (re-wield)",
        _ => "?",
    };

    private static string CombatModeName(int mode) => mode switch
    {
        CombatMode.NonCombat => "Peace",
        CombatMode.Melee     => "Melee",
        CombatMode.Missile   => "Missile",
        CombatMode.Magic     => "Magic",
        _ => mode.ToString(),
    };

    /// <summary>Step 4: put the weapon away and wield it again (what the heal's wand swap
    /// does to the knife). Finished by ContinueStallRewield; bounded and swap-gated.</summary>
    private void StartStallRewield(string head)
    {
        int wid = _equippedWeaponId;
        var wo = wid != 0 ? _worldFilter[wid] : null;
        if (wid == 0 || wo == null)
        {
            _host.Log($"{head} re-wield: skipped — no known weapon in hand");
            return;
        }
        if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("stall-rewield"))
        {
            _host.Log($"{head} re-wield: skipped — another weapon swap ran {_weaponSwapGate.MsSinceLastSwap}ms ago ({_weaponSwapGate.LastSwapBy})");
            return;
        }

        if (!IsWieldedLive(wid))
        {
            // Not actually in hand: just wield it.
            _host.UseFor((uint)wid, "Combat", "wield the weapon (not in hand)");
            _stallRewieldWieldSent = true;
            _stallRewieldWieldTries = 1;
            _host.Log($"{head} re-wield: 0x{wid:X8} '{wo.Name}' is not in hand — UseObject to wield it");
        }
        else
        {
            int pack = WorldObjectCache.FindPackFor(_host, _worldFilter, includeMainPack: true, requireFree: 1);
            if (pack == 0 || !_host.HasMoveItemInternal)
            {
                _host.Log($"{head} re-wield: skipped — no pack with room to put 0x{wid:X8} '{wo.Name}' in");
                return;
            }
            if (_host.HasCancelAttack) _host.CancelAttack();
            _serverAttackTargetId = 0;
            _host.MoveItemInternal((uint)wid, (uint)pack, 0, 1);
            _stallRewieldWieldSent = false;
            _stallRewieldWieldTries = 0;
            _host.Log($"{head} re-wield: 0x{wid:X8} '{wo.Name}' -> pack 0x{pack:X8}, then wield it again");
        }
        _stallRewieldId = wid;
        _stallRewieldAt = RynthClock.Now;
        _stallRewieldCheckAt = DateTime.MinValue;
    }

    /// <summary>
    /// Finish a step-4 re-wield: once the weapon is out of hand, wield it (through the swap
    /// gate); done when it reads wielded. Runs at the top of Think whether or not a target
    /// is locked, because EquipWeaponAndSetStance trusts a matching combat mode and would
    /// not put a weapon left in the pack back in hand. Returns true while attacks must wait.
    /// </summary>
    private bool ContinueStallRewield()
    {
        DateTime now = RynthClock.Now;
        int mode = CurrentCombatMode;
        if (mode == CombatMode.Magic)
        {
            // Buffing/healing took the hands; its wand swap and combat's own equip path
            // (mode ≠ Melee → ChangeCombatMode + UseObject) bring the weapon back.
            _host.Log($"[StallWatch] re-wield of 0x{_stallRewieldId:X8} handed over — mode is Magic");
            _stallRewieldId = 0;
            return false;
        }
        if ((now - _stallRewieldCheckAt).TotalMilliseconds < 250) return true;
        _stallRewieldCheckAt = now;

        bool wielded = IsWieldedLive(_stallRewieldId);
        double ms = (now - _stallRewieldAt).TotalMilliseconds;

        if (_stallRewieldWieldSent && wielded)
        {
            _host.Log($"[StallWatch] re-wield done: 0x{_stallRewieldId:X8} back in hand ({ms:0}ms after the last step)");
            _stallRewieldId = 0;
            lastStanceAttempt = DateTime.MinValue; // let the stance pass re-assert the mode now
            return false;
        }

        if (!wielded && (!_stallRewieldWieldSent || ms > StallRewieldStepTimeoutMs))
        {
            if (_stallRewieldWieldTries >= StallRewieldMaxWieldTries)
            {
                _host.Log($"[StallWatch] re-wield FAILED: 0x{_stallRewieldId:X8} not back in hand after {_stallRewieldWieldTries} tries");
                _host.WriteToChat($"[RynthAi] Couldn't put the weapon back in hand after the stall recovery. Please wield it by hand.", 2);
                _stallRewieldId = 0;
                return false;
            }
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("stall-rewield"))
                return true; // the dequip holds the slot ~3 s; wield when it frees
            _host.UseFor((uint)_stallRewieldId, "Combat", "stalled: wield the weapon again");
            _stallRewieldWieldSent = true;
            _stallRewieldWieldTries++;
            _stallRewieldAt = now;
            return true;
        }

        if (!_stallRewieldWieldSent && wielded && ms > StallRewieldStepTimeoutMs)
        {
            _host.Log($"[StallWatch] re-wield: 0x{_stallRewieldId:X8} never left the hand in {StallRewieldStepTimeoutMs:0}ms — leaving it wielded");
            _stallRewieldId = 0;
            return false;
        }
        return true;
    }

    /// <summary>One detailed look at every gate on the attack path, logged when a stall is
    /// first declared (once per episode).</summary>
    private void LogStallSnapshot(WorldObject targetObj, double dist, double stalledMs)
    {
        DateTime now = RynthClock.Now;
        string Ago(DateTime t) => t == DateTime.MinValue ? "never" : $"{(now - t).TotalMilliseconds:0}ms ago";
        int tid = activeTargetId;

        _host.Log($"[StallWatch] STALL {stalledMs / 1000:0.0}s: action Combat, mode {CombatModeName(CurrentCombatMode)}, " +
                  $"no hit/miss/kill while attacking 0x{tid:X8} '{targetObj.Name}' — snapshot (episode {_stallWatch.Episodes}):");

        float hp = _worldFilter.GetHealthRatio(tid);
        bool scanned = false;
        for (int i = 0; i < _scannedTargets.Count; i++)
            if (_scannedTargets[i].Id == tid) { scanned = true; break; }
        _host.Log($"[StallWatch]   target: d={dist:0.0}m range={_settings.MonsterRange:0.#} hp={(hp < 0 ? "?" : hp.ToString("0.00"))} class={targetObj.ObjectClass} " +
                  $"scanned={scanned} locked={(_lockedTargetId == tid)} lockedFor={Ago(_targetLockedAt)} lastDamage={Ago(_lastDamageDealtAt)} " +
                  $"facingErr={GetFacingError(tid):0}deg scan={_scannedTargets.Count} alt={HasAlternateTarget(tid)} {DescribeTargetPos(tid)}");

        string serverTarget = _serverAttackTargetId == 0 ? "none"
            : _serverAttackTargetId == tid ? "same"
            : $"0x{_serverAttackTargetId:X8} '{_worldFilter[_serverAttackTargetId]?.Name}' dead={IsKnownDead(_serverAttackTargetId)} {DescribeTargetPos(_serverAttackTargetId)}";
        _host.Log($"[StallWatch]   attack: lastCmd={Ago(lastAttackCmd)} interval=1000ms lastSkip='{LastCombatSkipReason}' serverTarget={serverTarget} " +
                  $"rebinds={_attackRebinds} native={(_settings.UseNativeAttack && _host.HasNativeAttack)}" +
                  (_nativeFallbackTargetId != 0 ? " (regular attack this fight)" : "") + " " +
                  $"evidence: hit={Ago(_lastHitAt)} miss={Ago(_lastMissAt)} kill={Ago(_lastKillAt)} (last={_lastSwingEvidenceKind})");

        string castGate = _host.HasGetCastBusyState ? (_host.CanCastNow ? "open" : "closed") : "n/a";
        _host.Log($"[StallWatch]   gates: busy={BusyCount} castGate={castGate} castInFlight={IsCastInFlight()} faceHeld={_facingTarget} " +
                  $"returnToPhysical={_returnToPhysicalCombat} stanceFlips={_stanceFlipAttempts} stanceStuck={Ago(_stanceStuckSince)} " +
                  $"wandWield(pending=0x{_wandPendingWieldId:X8} fails={_wandWieldFailCount}) " +
                  $"swapGate={(_weaponSwapGate == null ? "n/a" : $"{_weaponSwapGate.MsSinceLastSwap}ms ago by '{_weaponSwapGate.LastSwapBy}'")}");

        int wid = _equippedWeaponId;
        var wo = wid != 0 ? _worldFilter[wid] : null;
        string weapon = wo == null ? $"0x{wid:X8} (unknown)"
            : $"0x{wid:X8} '{wo.Name}' class={wo.ObjectClass} wieldedLive={IsWieldedLive(wid)} loc={wo.Values(LongValueKey.CurrentWieldedLocation, 0)}";
        string useDone = "n/a", weenie = "n/a";
        if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out uint uErr))
            useDone = $"seq={uSeq} err=0x{uErr:X}";
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out uint wErr, out uint wEvt, out uint wObj))
            weenie = $"seq={wSeq} err=0x{wErr:X4} evt=0x{wEvt:X3} obj=0x{wObj:X8}";
        _host.Log($"[StallWatch]   weapon: {weapon} | server: lastUseDone {useDone} | lastWeenieError {weenie}");
    }

    public void OnHeartbeat()
    {
        FlushTakenSummary();   // the [Taken] line every 30 s while monsters hit us, macro running or not
        NoteSwapPortal();
        NoteSwapWieldsSeen();
        // The hands swap's steps (weapons back, or the fight's weapons) go before the gates; the macro stopping ends it (freeing the gate).
        TickRearmSwap();

        if (!_settings.IsMacroRunning)
        {
            // Clear turn motions once on the transition from running → stopped,
            // so the character doesn't spin indefinitely after a mid-turn stop.
            // Do NOT clear every frame — that blocks manual keyboard turning.
            if (_wasMacroRunning)
            {
                _wasMacroRunning = false;
                ClearCombatTurnMotions();
                ResetEnvironmentImpact("macro stopped");
                ResetKite("macro stopped");
            }
            return;
        }
        _wasMacroRunning = true;

        NotePriorityZeroRules();
        LogCombatStateIfChanged();
        WatchNativeStance();   // a stance change mid-fight: the first swing back is the regular attack

        // Environment-impact throttle: follow our spell projectiles, judge finished casts, and
        // let go of an approach Think stopped driving.
        try { PollEnvironmentImpact(); }
        catch (Exception ex) { _host.Log($"[EnvImpact] poll crashed: {ex.Message}"); }
        try { PollKite(); }
        catch (Exception ex) { _host.Log($"[Kite] poll crashed: {ex.Message}"); }

        // A combat mode change ends the server's attack (HandleActionChangeCombatMode →
        // HandleActionCancelAttack), so out of Melee/Missile there is nothing bound to release.
        if (_serverAttackTargetId != 0)
        {
            int m = CurrentCombatMode;
            if (m != CombatMode.Melee && m != CombatMode.Missile) _serverAttackTargetId = 0;
        }

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
        else if (activeTargetId != 0 || _scannedTargets.Count > 0 || _nearbyNoLos > 0)
        {
            // Combat switched off (the UI, /vt opt or a meta's SetOpt): the scan stops, so
            // drop what it last saw. Kept, HasEngageableTarget stayed true for good: salvage
            // never ticked (it yields to a threat), the arbiter kept picking Salvaging, and
            // the bot stood still; BuffManager also stayed on its in-combat thresholds.
            _scannedTargets.Clear();
            _nearbyNoLos = 0;
            DropTarget("combat disabled");
        }

        // Boost Nav Priority (owner's corrected rule, 2026-11-02, R-20261103-000159-a65e): while a
        // nav route runs RynthAi only follows it. No hunting and no fighting back, whatever
        // BotAction says. With no route (or a Once route at its end, or the Outdoor Hunt) the flag
        // does nothing and combat runs as usual: BoostNavRule.Active is the one test. The scan
        // above still runs: it is what tells the arbiter (and the once-per-session notice) a fight
        // is being skipped, and what picks the fight back up the moment the route is gone.
        if (BoostNavRule.Active(_settings))
        {
            StandDownForBoostNav();
            return;
        }
        if (_boostNavStoodDown)
        {
            _boostNavStoodDown = false;
            _host.Log(_settings.BoostNavPriority
                ? "[RynthAi] Combat: Boost Nav Priority is on but no nav route runs - hunting again."
                : "[RynthAi] Combat: Boost Nav Priority is off - hunting again.");
        }

        // Combat can run in Default/Combat, can interrupt navigation, and can interrupt looting
        // unless loot boost is on. Buffing always blocks combat — if buffs drop, the character dies.
        bool canRun = _settings.BotAction == "Default"
                   || _settings.BotAction == "Combat"
                   || _settings.BotAction == "Navigating"
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

    // True while Boost Nav Priority holds combat down (logged once each way).
    private bool _boostNavStoodDown;

    /// <summary>Host tests: combat is standing down for Boost Nav Priority.</summary>
    internal bool BoostNavStoodDown => _boostNavStoodDown;

    /// <summary>
    /// Boost Nav Priority (owner's corrected rule, 2026-11-02), every combat tick while the flag is
    /// on and a nav route runs (BoostNavRule.Active): nothing is attacked, and a fight the flag (or
    /// a route loaded with it on) came in the middle of is let go cleanly. The
    /// target is dropped (which ends a kite or an environment approach), the server's melee or
    /// missile attack is cancelled (or it keeps swinging and walking us to the mob), a held turn
    /// is let go, and with Peace Mode When Idle the stance goes back to Peace as it does between
    /// fights, so nav does not run in a combat stance. No mode change while buffing (it would
    /// fizzle the cast), while a cast resolves, or while a nav recall wants Magic mode.
    /// </summary>
    private void StandDownForBoostNav()
    {
        int mode = CurrentCombatMode;
        if (!_boostNavStoodDown)
        {
            _boostNavStoodDown = true;
            _host.Log("[RynthAi] Combat: Boost Nav Priority is on and a nav route runs - no hunting and no fighting back, RynthAi only follows the route.");
            // The flag came on mid-fight: end the server's attack even if our record of it is gone.
            if (mode == CombatMode.Melee || mode == CombatMode.Missile)
                ReleaseServerAttack("Boost Nav Priority", log: false);
        }
        if (activeTargetId != 0 || _lockedTargetId != 0)
            DropTarget("Boost Nav Priority is on and a nav route runs (RynthAi only follows it)");
        if (_serverAttackTargetId != 0)
            ReleaseServerAttack("Boost Nav Priority", log: false);
        ReleaseHeldTurn();

        if (mode == CombatMode.NonCombat) { _idlePeaceAttempts = 0; return; }
        if (!_settings.PeaceModeWhenIdle || _settings.BotAction == "Buffing" || BusyCount != 0
            || RynthClock.Now < _navMagicWantedUntil || IsCastInFlight())
            return;
        bool peaceHeld;
        try { peaceHeld = HoldIdlePeace?.Invoke() == true; } catch { peaceHeld = false; }
        if (peaceHeld) return;
        // The idle swap's backoff (ThinkCore): 500 ms for the first 4 sends, then doubling to 15 s.
        double peaceDelayMs = Math.Min(15_000, 500 * Math.Pow(2, Math.Min(5, _idlePeaceAttempts / 4)));
        if ((RynthClock.Now - _lastPeaceAttempt).TotalMilliseconds > peaceDelayMs
            && _host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", "Boost Nav Priority: RynthAi only follows the nav route (Peace Mode When Idle)"))
        {
            _lastPeaceAttempt = RynthClock.Now;
            _idlePeaceAttempts++;
        }
    }

    /// <summary>
    /// Target selection, every tick. Candidates are the top monster-rule Priority among monsters
    /// inside MonsterRange (or among all scanned ones when none is inside it), ranked by the
    /// Targeting setting (<see cref="TargetScheme"/>). Target Lock on: a valid incumbent is kept,
    /// full stop, a higher priority in range or not (2026-11-07). Off: a higher-priority monster in
    /// range takes over at once, and otherwise the incumbent is kept unless another candidate beats
    /// it by the scheme's margin, and never while an attack at it is still in flight.
    /// </summary>
    public void HandleCombatTrigger()
    {
        if (_scannedTargets.Count == 0) return;
        if (TryLockForcedTarget()) return;   // Target lock (/ra targetlock): CombatManager.TargetLock.cs
        // Pull-away in progress: keep the incumbent until the kite ends (do not switch mid-flee).
        if (KiteActive && _lockedTargetId != 0) return;
        // Boost Loot Priority: the next target waits for the last kill's corpse. Without this the
        // next lock went out a few ms after each kill, 1-3 s before ACE creates the corpse, so the
        // arbiter always saw combat engaged and corpses were looted in batches (2026-10-14).
        if (_lockedTargetId == 0 && NewTargetHeldForLoot()) return;

        DateTime now = TargetClock();
        double acquireRange = Math.Max(1.0, _settings.MonsterRange);
        var scheme = (TargetScheme)Math.Clamp(_settings.TargetSelection, 0, 3);

        // Strict priority: the highest Priority among monsters inside MonsterRange (or among
        // all scanned ones when none is inside it) is the only tier that may be picked.
        int topPriority = int.MinValue, topAnyRange = int.MinValue;
        foreach (var c in _scannedTargets)
        {
            int p = PriorityOf(c.Id);
            if (p > topAnyRange) topAnyRange = p;
            if (c.Distance <= acquireRange && p > topPriority) topPriority = p;
        }
        // A monster whose kill notice was matched to it by name only (SuppressGuessedDeath) is off
        // the scan for a moment but may be alive: its tier still stands, so a lesser monster
        // isn't locked in the meantime (with nothing of that tier to pick, no lock this tick).
        int guessedTop = TopGuessedDeathPriority(acquireRange);
        if (guessedTop > topPriority) topPriority = guessedTop;
        bool anyInRange = topPriority != int.MinValue;
        if (!anyInRange) topPriority = topAnyRange;

        // The best candidate by the scheme. Only monsters inside MonsterRange when any is: the
        // scan holds a locked target out to DisengageDistance (MonsterRange+3 by default) so a
        // lone mob at the edge doesn't flip engage/peace, not so it can hold off monsters inside
        // the range the user set (2026-09-27, Olthoi swarm: a target at 5-8 m kept for over a
        // minute while mobs stood at 3-5 m with Monster Range 5).
        ScannedTarget best = default;
        double bestKey = 0;
        bool haveBest = false;
        ScannedTarget? incInScan = null;
        foreach (var c in _scannedTargets)
        {
            if (c.Id == _lockedTargetId) incInScan = c;
            if (PriorityOf(c.Id) < topPriority) continue;   // a higher-priority monster is in range
            if (anyInRange && c.Distance > acquireRange) continue;
            double key = SchemeKey(scheme, c);
            if (!haveBest || BetterCandidate(key, c, bestKey, best)) { best = c; bestKey = key; haveBest = true; }
        }
        if (!haveBest) return;

        // Is the incumbent still valid? In the scan (or briefly out of it, within the scan
        // grace, at its last seen values), in the top tier, inside MonsterRange, not
        // blacklisted or suppressed by a watchdog.
        string? invalidWhy = null;
        ScannedTarget inc = default;
        if (_lockedTargetId != 0)
        {
            bool gone = false;
            if (incInScan is { } seen)
            {
                inc = seen;
                _lockedPriority = PriorityOf(seen.Id);
                _lockedLastSeen = seen;
                _lockedLastSeenInScan = now;
            }
            else
            {
                inc = _lockedLastSeen;   // its priority stays the last one seen (_lockedPriority)
                gone = _lockedLastSeenInScan == DateTime.MinValue || inc.Id != _lockedTargetId
                    || (now - _lockedLastSeenInScan).TotalMilliseconds > TARGET_SCAN_GRACE_MS;
            }
            // Target Lock on: a higher priority doesn't take the target either. The lock holds until
            // the target dies or leaves Monster Range (the owner, 2026-11-07); with the lock off a
            // higher priority in range takes over at once, as before. A lock left over from the
            // player's own pick (fight selected, /ra targetlock) isn't the bot's: once that pick ends,
            // priority decides again.
            bool priorityTakesOver = (!_settings.TargetLock || _lockByPlayer) && _lockedPriority < topPriority;
            invalidWhy = gone ? "gone"
                : priorityTakesOver ? $"priority {_lockedPriority} < {topPriority}"
                : inc.Distance > acquireRange ? $"out of Monster Range, {inc.Distance:0.0} m"
                : _blacklistManager.IsBlacklisted(_lockedTargetId) ? "blacklisted"
                : _recentlyKilled.ContainsKey(_lockedTargetId) ? "suppressed"
                : null;
        }

        string? switchWhy = null;
        if (_lockedTargetId != 0 && invalidWhy == null)
        {
            // Target Lock on: keep it until it is no longer valid. A higher priority it holds off is
            // logged once per pair, so a report shows the lock, not a priority ignored.
            if (_settings.TargetLock)
            {
                if (_lockedPriority < topPriority && best.Id != _lockedTargetId
                    && _lockHeldOverLogged != (_lockedTargetId, best.Id))
                {
                    _lockHeldOverLogged = (_lockedTargetId, best.Id);
                    _host.Log($"[CombatTarget] Target Lock keeps 0x{_lockedTargetId:X8} '{_worldFilter[_lockedTargetId]?.Name}' priority {_lockedPriority} " +
                              $"over 0x{best.Id:X8} '{_worldFilter[best.Id]?.Name}' priority {PriorityOf(best.Id)} (it holds until the target dies or leaves Monster Range)");
                }
                return;
            }
            if (best.Id == _lockedTargetId) return;

            // Target Lock off: switch only to a clearly better one, and never with an attack
            // at the incumbent still on its way.
            // Not while the incumbent is out of this tick's scan (inside the scan grace): its
            // values are the last ones seen, and judging fresh candidates against them switched
            // away from a monster closing in. R-20261031-201214-8be7: a Brier Wasp Swarm last
            // scanned at 4.3 m, live at 1.9 m, lost to a wasp at 2.4 m as "clearly better",
            // 124 ms after a bolt went out at it.
            if (incInScan == null) return;
            double incKey = SchemeKey(scheme, inc);
            if (incKey - bestKey < SchemeMargin(scheme)) return;
            if (AttackInFlightAtLocked(now)) return;
            switchWhy = $"{SchemeName(scheme)}: {DescribeSchemeValue(scheme, inc)}, another is clearly better";
        }
        else if (_lockedTargetId != 0)
        {
            if (best.Id == _lockedTargetId) return;   // its own tier's only choice (e.g. alone past the range)
            switchWhy = invalidWhy;
        }

        // One line per real switch. A switch should be a genuine event (target died, left,
        // or a higher priority showed up; with the lock off, something clearly better). If
        // this floods the log, selection is thrashing again.
        int bestPriority = PriorityOf(best.Id);
        LockTarget(best, bestPriority, $"{SchemeName(scheme)}: {DescribeSchemeValue(scheme, best)}, priority {bestPriority}, {DescribeTargetPos(best.Id)}", switchWhy);
    }

    /// <summary>Makes <paramref name="best"/> the fight target (one log line; the switch says why).</summary>
    private void LockTarget(ScannedTarget best, int bestPriority, string picked, string? switchWhy)
    {
        int bestId = best.Id;
        _lockByPlayer = false;   // TryLockForcedTarget sets it after its own LockTarget
        _lockWasSwitch = _lockedTargetId != 0 && _lockedTargetId != bestId;
        // A higher-priority monster that wasn't a candidate (out of sight, past Monster Range,
        // blacklisted...): named with why, so a report shows whether the priority was ignored.
        string passedOver = PassedOverNoteForLock(bestId, bestPriority);
        if (_lockedTargetId != 0)
            _host.Log($"[CombatTarget] switch 0x{_lockedTargetId:X8} '{_worldFilter[_lockedTargetId]?.Name}' ({switchWhy}, {DescribeTargetPos(_lockedTargetId)}) " +
                      $"-> 0x{bestId:X8} '{_worldFilter[bestId]?.Name}' ({picked})" + passedOver);
        else
            _host.Log($"[CombatTarget] lock 0x{bestId:X8} '{_worldFilter[bestId]?.Name}' ({picked})" +
                      (_ghostsSkippedInScan > 0 ? $" [{_ghostsSkippedInScan} ghost(s) in range skipped]" : "") + passedOver);

        // [CombatLatency]: time this engagement from the lock to its first attack.
        double lockMs = Environment.TickCount64;
        double sinceKillMs = _lastKillAt == DateTime.MinValue ? double.MaxValue : (RynthClock.Now - _lastKillAt).TotalMilliseconds;
        _latency.OnLock(bestId, lockMs, sinceKillMs <= 2000 ? lockMs - sinceKillMs : double.NaN);

        activeTargetId      = bestId;
        _lockedTargetId     = bestId;
        _lockedPriority     = bestPriority;
        _targetLockedAt     = RynthClock.Now;
        _lockSpot           = TargetSpot(bestId);
        _lastDamageDealtAt  = DateTime.MinValue;
        _lockedLastSeen       = best;   // it was in this tick's scan: the grace covers a blink next tick
        _lockedLastSeenInScan = TargetClock();
        _consecutiveCastMisses = 0;
        _pendingJudgeCastAt = DateTime.MinValue;
        _facingTarget       = false;
        _returnToPhysicalCombat = false;
        // Target-switch turn-stop: defer while a cast at the PREVIOUS target is still resolving
        // (nothing clears the in-flight flag on target acquire), else this SetMotion(turn,false)
        // truncates that gesture → strands Player.IsBusy. The lock switch above still happens;
        // the stop re-issues next tick. Bounded by IsCastInFlight (UseDone-seq or 2.5s).
        if (!IsCastInFlight()) ClearCombatTurnMotions();

        // Internal lock is set unconditionally so combat is ready to swing the
        // moment busy clears. SelectItem during a combat-mode transition can
        // wedge the client action queue, so only fire it when not busy.
        // SelectItem removed 2026-06-03 (see the combat-tick note): the target lock is
        // purely internal (activeTargetId / _lockedTargetId set above). Casts use the
        // explicit-target FreeHandsAndCastSpell path and attacks pass targetId, so AC's
        // selection needn't track the combat target. Removing it frees the user's
        // inventory selection and drops an off-thread SetSelectedObject mutation.
    }

    /// <summary>
    /// A candidate's rank under <paramref name="scheme"/>: lower is better. Health that isn't
    /// known (-1) counts as full. Combat is selection-free (SelectItem removed 2026-06-03), so
    /// ACE pushes health only for the mob we are fighting and every other candidate reads -1;
    /// reading that as 0 ("at death's door") made every untouched mob look nearly dead, and a
    /// target's rank collapsed the moment we hit it and its real health arrived: pick, turn,
    /// hit once, re-pick, turn again (the spin). An un-engaged mob is almost certainly at full
    /// health. Since 2026-11-07 the engine's monster plates fill the gaps where they can (API v93,
    /// <see cref="RankingHealth"/>): they probe the other monsters' health from the server.
    /// </summary>
    internal double SchemeKey(TargetScheme scheme, in ScannedTarget c) => scheme switch
    {
        TargetScheme.LowestHealth => HealthOrFull(c.Id),
        TargetScheme.Farthest     => -c.Distance,
        TargetScheme.LeastTurning => c.Angle,
        _                         => c.Distance,
    };

    private double HealthOrFull(int id)
    {
        float r = RankingHealth(id, out _);
        return r < 0f ? 1.0 : Math.Clamp(r, 0f, 1f);
    }

    /// <summary>The engine plates' readings older than this (ms) are not used to rank.</summary>
    internal const uint PlateHealthMaxAgeMs = 10_000;
    /// <summary>A plate reading takes over from RynthAi's own server value only when it is this much
    /// fresher (ms): the two are the same 0x01C0 for the streamed target, a few ms apart.</summary>
    internal const double PlateHealthFresherByMs = 500;

    /// <summary>
    /// The health Lowest health ranks <paramref name="id"/> by, -1 = unknown (counted as full). RynthAi's
    /// own value (GetHealthRatio: the server's stream for the target it queried, or an estimate from its
    /// own splash hits) unless the engine's monster plates (API v93 TryGetMonsterHealth: the server's
    /// reading from the plates' round-robin probes, 2026-11-07) know better:
    ///   * own unknown: the plate reading;
    ///   * own an estimate: the lower of the two (each misses the damage the other saw);
    ///   * own a server value: the plate reading when it is clearly fresher.
    /// Plate readings older than <see cref="PlateHealthMaxAgeMs"/>, or 0 (dead to the plates, not yet to
    /// RynthAi), are ignored, and RynthAi's own 0 (the server said dead) always stands. RANKING ONLY:
    /// nothing here goes into the cache, fight tracking, kill prediction, damage rows or the dashboard.
    /// <paramref name="fromPlates"/>: the plates' reading decided it (for the log line).
    /// </summary>
    internal float RankingHealth(int id, out bool fromPlates)
    {
        fromPlates = false;
        float own = _worldFilter.GetHealthRatio(id);
        if (own == 0f || !_host.HasMonsterHealth) return own;
        if (!_host.TryGetMonsterHealth(unchecked((uint)id), out float plate, out uint ageMs)) return own;
        if (ageMs > PlateHealthMaxAgeMs || !(plate > 0f)) return own;

        float pick;
        if (own < 0f)
            pick = plate;
        else if (_worldFilter.IsHealthEstimated(id))
            pick = Math.Min(own, plate);
        else if (!_worldFilter.TryGetServerHealthTime(id, out DateTime ownAt)
                 || ageMs + PlateHealthFresherByMs < (TargetClock() - ownAt).TotalMilliseconds)
            pick = plate;
        else
            pick = own;
        fromPlates = pick != own;
        return pick;
    }

    /// <summary>Ranks <paramref name="c"/> ahead of the best so far: a lower key, ties to the
    /// closer one, then the lower id (so the pick is stable tick to tick).</summary>
    private static bool BetterCandidate(double key, in ScannedTarget c, double bestKey, in ScannedTarget best)
    {
        if (key != bestKey) return key < bestKey;
        if (c.Distance != best.Distance) return c.Distance < best.Distance;
        return (uint)c.Id < (uint)best.Id;
    }

    /// <summary>With Target Lock off, how much better (in <see cref="SchemeKey"/> units) another
    /// candidate must be than the incumbent to take over.</summary>
    private static double SchemeMargin(TargetScheme scheme) => scheme switch
    {
        TargetScheme.LowestHealth => MoreWoundedMargin,
        TargetScheme.LeastTurning => LessTurningMarginDeg,
        _                         => CloserFartherMarginM,
    };

    internal static string SchemeName(TargetScheme scheme) => scheme switch
    {
        TargetScheme.LowestHealth => "lowest health",
        TargetScheme.Closest      => "closest",
        TargetScheme.Farthest     => "farthest",
        _                         => "least turning",
    };

    private string DescribeSchemeValue(TargetScheme scheme, in ScannedTarget c)
    {
        switch (scheme)
        {
            case TargetScheme.LowestHealth:
                float r = RankingHealth(c.Id, out bool fromPlates);
                return r < 0f ? "health unknown (as full)"
                    : $"health {Math.Clamp(r, 0f, 1f) * 100:0}%" + (fromPlates ? " (the plates' reading)" : "");
            case TargetScheme.LeastTurning:
                return $"turn {c.Angle:0} deg";
            default:
                return $"{c.Distance:0.0} m";
        }
    }

    /// <summary>
    /// True while an attack at the locked target is still on its way: issued at it within
    /// <see cref="CastCommitmentWindowMs"/> with no damage landed since. Capped by
    /// <see cref="UndamagedCommitmentMaxMs"/> since the last damage (or the lock, when it never
    /// took any), so continuous fire at a target that never takes damage can't hold it forever.
    /// Only Target Lock off asks: with the lock on nothing switches a valid target anyway.
    /// </summary>
    private bool AttackInFlightAtLocked(DateTime now)
    {
        if (_lockedTargetId == 0) return false;
        // The last attack at it: a judged one (RecordOffensiveCast), or a cast just sent. A cast
        // is judged only after its refusal window, so for its first half second or more the hold
        // didn't know it had gone out, and a switch walked away from the bolt in the air
        // (R-20261031-201214-8be7: the switch came 124 ms after the cast).
        DateTime sent = _lastAttackedTargetId == _lockedTargetId ? _lastAttackedAt : DateTime.MinValue;
        if (_castSentTargetId == _lockedTargetId && _castSentAt > sent) sent = _castSentAt;
        if (sent == DateTime.MinValue) return false;
        if ((now - sent).TotalMilliseconds > CastCommitmentWindowMs) return false;
        if (_lastDamageDealtAt != DateTime.MinValue && _lastDamageDealtAt >= sent) return false;   // it landed
        DateTime progress = _lastDamageDealtAt != DateTime.MinValue ? _lastDamageDealtAt : _targetLockedAt;
        return progress != DateTime.MinValue && (now - progress).TotalMilliseconds <= UndamagedCommitmentMaxMs;
    }

    private List<MonsterRule>? _priorityZeroNoticeRules;
    private string _priorityZeroNoticeKey = "";

    /// <summary>
    /// Priority 0 on a monster rule now means "never attacked or tracked" (before, it was a lowest
    /// priority that still fought back). A profile written under the old meaning would silently stop
    /// fighting those monsters, so the first time a rule list holds any, one log line and one chat
    /// notice say what it does now (again only if the set of Priority 0 rules changes, e.g. another
    /// profile loads). Behavior is not changed.
    /// </summary>
    internal void NotePriorityZeroRules()
    {
        var rules = _settings.MonsterRules;
        if (ReferenceEquals(rules, _priorityZeroNoticeRules)) return;
        _priorityZeroNoticeRules = rules;
        List<string>? zero = null;
        foreach (var rule in rules)
            if (rule.Priority <= 0) (zero ??= new List<string>()).Add(string.IsNullOrWhiteSpace(rule.Name) ? "(unnamed)" : rule.Name);
        if (zero == null) { _priorityZeroNoticeKey = ""; return; }
        string key = string.Join("|", zero).ToLowerInvariant();
        if (key == _priorityZeroNoticeKey) return;
        _priorityZeroNoticeKey = key;
        string names = zero.Count <= 5 ? string.Join(", ", zero) : string.Join(", ", zero.GetRange(0, 5)) + $" and {zero.Count - 5} more";
        string line = $"[RynthAi] Monsters: Priority 0 now means 'never attacked or tracked', even if the monster attacks you " +
                      $"(rows with Priority 0: {names}). Raise the row's Priority to fight it.";
        _host.Log(line);
        _host.WriteToChat(line, 2);
    }

    /// <summary>True when the monster rule for <paramref name="id"/> says Priority 0 (never attacked).</summary>
    internal bool IsIgnored(int id) => id != ForcedTargetId && PriorityOf(id) <= 0;

    /// <summary>The Priority of the monster rule that applies to <paramref name="id"/> (its own
    /// row by name or match expression, else the DEFAULT row; 1 when neither exists). Cached per
    /// monster for <see cref="PriorityCacheMs"/>; the cache empties when the rule list changes.</summary>
    private int PriorityOf(int id)
    {
        var rules = _settings.MonsterRules;
        if (!ReferenceEquals(rules, _priorityCacheRules)) { _priorityCache.Clear(); _priorityCacheRules = rules; }
        DateTime now = RynthClock.Now;
        if (_priorityCache.TryGetValue(id, out var hit) && (now - hit.At).TotalMilliseconds < PriorityCacheMs)
            return hit.Priority;
        if (_priorityCache.Count > 512) _priorityCache.Clear();
        int p = GetRuleForTarget(_worldFilter[id])?.Priority ?? 1;
        _priorityCache[id] = (p, now);
        return p;
    }

    private DateTime _lastEquipTime = DateTime.MinValue;
    private DateTime _lastStanceTime = DateTime.MinValue;
    private DateTime _lastEquipDiagAt = DateTime.MinValue;

    /// <summary>
    /// The weapon to fight <paramref name="target"/> with (0 = none), and the element in
    /// <paramref name="desired"/>. See WeaponPlanner for the order: this monster's Damage tab
    /// weapon, else its Monsters rule's weapon (listed or not), a slayer of the monster, the Damage
    /// tab DEFAULT row's weapon, else the Monsters Default row's, the
    /// Monsters rule's damage type, the weakness (weakest element a listed
    /// weapon of the main kind has), else the weapon in hand, the learned best, the first.
    /// Casters are skipped when the character has no War/Void Magic (IsUsableCombatWeapon).
    /// </summary>
    internal int ChooseWeapon(WorldObject target, MonsterRule? rule, ref string desired, out string source)
    {
        var plan = PlanFor(target, rule);
        source = plan.Source;
        if (plan.Element.Length > 0) desired = plan.Element;
        return plan.WeaponId;
    }

    /// <summary>
    /// The weapon and element for <paramref name="target"/> (WeaponPlanner's rule). The candidates
    /// are the Items list's weapons: a Damage-tab pick or a learned "best" weapon that is no
    /// longer listed is ignored (logged once), which is how an unlisted fire wand used to be
    /// wielded after the user removed it from the list (2026-09-30). The one exception is a
    /// weapon set on the Monsters tab, used listed or not (2026-10-22).
    /// </summary>
    internal WeaponPlan PlanFor(WorldObject target, MonsterRule? rule)
    {
        // The equip path and the spell path both ask every tick; the answer only moves when
        // the target, the rule, the list or the hand does, so reuse it for a quarter second.
        DateTime now = RynthClock.Now;
        int hand = InHandWeaponId();
        var list = _settings.ItemRules;
        if (_planCache is { } pc && pc.TargetId == target.Id && ReferenceEquals(pc.Rule, rule) && ReferenceEquals(pc.List, list)
            && pc.Count == list.Count && pc.Hand == hand && (now - pc.At).TotalMilliseconds < PlanCacheMs)
            return pc.Plan;
        var plan = ComputePlan(target, rule);
        _planCache = (target.Id, rule, list, list.Count, hand, now, plan);
        return plan;
    }

    private (int TargetId, MonsterRule? Rule, List<ItemRule> List, int Count, int Hand, DateTime At, WeaponPlan Plan)? _planCache;
    /// <summary>The character can wield <paramref name="id"/> now: no unmet requirement, not refused lately.</summary>
    private bool CanWieldNow(int id) =>
        !SkillTooLowFor(id) && WieldRequirements.Unmet(_host, _charSkills, unchecked((uint)id)) == null;

    // Listed weapons whose wield requirement isn't met (logged once each).
    private readonly HashSet<int> _wieldReqNoted = new();
    // Listed weapons found not to be ours (logged once each).
    private readonly HashSet<int> _notCarriedNoted = new();
    internal const double NotCarriedGraceMs = 5000;
    private const double PlanCacheMs = 250;

    private WeaponPlan ComputePlan(WorldObject target, MonsterRule? rule)
    {
        var listed = ListedCandidates();
        string ruleElement = rule != null && !string.IsNullOrEmpty(rule.DamageType)
                             && !rule.DamageType.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? rule.DamageType : "";

        uint wcid = 0;
        if (_host.HasGetObjectWcid && _host.TryGetObjectWcid((uint)target.Id, out uint tw)) wcid = tw;

        // The weapon the player fixed, the first usable of (owner's rule, 2026-10-22, problem report
        // R-20261022-193411-fb72 "ignoring monster rules, specifically which weapon is set for the
        // mob type": a Monsters rule's weapon was imported, shown and kept from trade/sell, but
        // combat never read it, so the weakness pick wielded something else):
        //   1. this monster's own Damage tab pick (by wcid)            - WeaponPlanner step 1
        //   2. the matched Monsters rule's weapon (not the Default row) - step 1
        //   3. the Damage tab DEFAULT row's weapon                      - step 3 (a slayer beats it)
        //   4. the Monsters tab Default row's weapon                    - step 3
        // Only 1 needs the wcid: a rule weapon still applies to a monster whose wcid isn't known.
        // One the character can't wield now (a requirement, or refused) is left out quietly, it is
        // logged where the list drops it. A Damage tab pick must be in the Items list (an unlisted
        // fire wand was wielded that way, 2026-09-30); one that isn't is logged once. A Monsters tab
        // weapon (2 and 4) need not be (owner, 2026-10-22: "if it is in the monsters tab but not
        // item list it should still use it"): it joins the candidates when carried and usable.
        // Either way, when one can't be used the next is tried. A listed weapon for another build
        // stays listed.
        int fixedId = 0, learnedId = 0;
        string fixedSource = "Damage tab";
        bool fixedIsDefault = false;
        var defaultRule = DefaultMonsterRule();
        bool ownRule = rule != null && !ReferenceEquals(rule, defaultRule)
                       && !rule.Name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        bool TryFixed(int id, string source, string noteAs, bool isDefault, bool listOnly)
        {
            if (fixedId != 0 || id == 0 || !CanWieldNow(id)) return false;
            if (!listed.Any(c => c.Id == id))
            {
                if (listOnly) { NoteUnlisted(id, noteAs); return false; }
                var extra = CandidateFor(id, null, InHandWeaponId(), admitCasters: false, noteAs + " weapon", out bool caster);
                if (extra == null)
                {
                    if (caster && _unlistedNoted.Add(id))
                        _host.Log($"[EquipDiag] {noteAs} weapon 0x{(uint)id:X8} '{WeaponName(id)}' is a caster and the character can't fight with magic: {CanMagicDiag()}");
                    return false;
                }
                if (_unlistedNoted.Add(id))
                    _host.Log($"[EquipDiag] {noteAs} weapon 0x{(uint)id:X8} '{extra.Name}' isn't in the Items list — using it (set on the Monsters tab)");
                listed.Add(extra);
            }
            fixedId = id; fixedSource = source; fixedIsDefault = isDefault;
            return true;
        }
        if (wcid != 0 && _damageStore != null)
            TryFixed((int)_damageStore.GetManualWeapon(wcid), "Damage tab", "Damage tab", false, listOnly: true);
        if (ownRule)
            TryFixed(rule!.WeaponId, "Monsters rule weapon", $"Monsters rule '{rule.Name}'", false, listOnly: false);
        if (_damageStore != null)
            TryFixed((int)_damageStore.GetDefaultWeapon(), "Damage tab default", "Damage tab default", true, listOnly: true);
        if (defaultRule != null)
            TryFixed(defaultRule.WeaponId, "Monsters Default weapon", "Monsters Default row", true, listOnly: false);

        if (wcid != 0 && _damageStore != null)
        {
            uint best = _damageStore.GetBestWeapon(wcid);
            if (best != 0 && !CanWieldNow((int)best)) best = 0;
            if (best != 0 && !listed.Any(c => c.Id == (int)best)) { NoteUnlisted((int)best, "Damage tab learned"); best = 0; }
            learnedId = (int)best;
        }

        // Launchers: their element is their ammo's, and one with no ammo to shoot isn't wielded
        // (CombatManager.Ammo.cs). Lists without a launcher come back unchanged.
        var weak = WeaknessFor(target);
        listed = ApplyAmmo(listed, ruleElement, weak);
        // Slayers (PropertyInt 166 = the monster's PropertyInt 2) only when a listed weapon has one.
        int ctype = 0;
        if (listed.Any(c => c.SlayerType > 0))
        {
            ctype = CreatureTypeOf(wcid, target.Name, target.Id);
            if (ctype == 0 && _slayerTypeUnknownNoted.TryAdd(wcid != 0 ? wcid.ToString() : target.Name ?? "", 0))
                _host.Log($"[EquipDiag] '{target.Name}' (wcid {wcid}): creature type not known yet (not appraised, not in the creature data) - no slayer check until it is");
        }
        // A weakness swap must beat the weapon in hand by WeaponSwapMinGainPct, but only while no
        // swap is under way: a launcher whose ammo already went into the pack is never kept.
        double minGain = _settings.WeaponSwapMinGainPct > 0 && SwapHoldsAllowed(InHandWeaponId())
            ? _settings.WeaponSwapMinGainPct / 100.0 : 0;
        var plan = WeaponPlanner.Choose(listed, ruleElement, weak, e => CanCastElement(e, rule),
            fixedId, fixedSource, learnedId, ctype, fixedIsDefault, minGain, PreferredDamageFor(rule));
        // A damage type on the Default row is every monster's, not a choice for this one.
        if (plan.ForThisMonster && !ownRule && (fixedId == 0 || fixedIsDefault))
            plan = plan with { ForThisMonster = false };
        return plan;
    }

    /// <summary>The preferred damage type for a fight: the rule's own, else (Auto) the Default row's.</summary>
    internal string PreferredDamageFor(MonsterRule? rule)
    {
        if (rule != null && DamageStyles.Parse(rule.PreferredDamage) != DamageStyle.Auto) return rule.PreferredDamage;
        var def = _settings.MonsterRules.Find(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        return def?.PreferredDamage ?? "";
    }

    // PropertyInt 166 SlayerCreatureType, PropertyFloat 138 SlayerDamageBonus (ACE.Entity).
    private const uint PropSlayerCreatureType = 166, PropSlayerDamageBonus = 138;

    /// <summary>
    /// A weapon's slayer (creature type, damage multiplier); (0, 0) when it has none or isn't
    /// identified yet (the weapon tracker identifies listed weapons). The multiplier is 0 when
    /// not known: ACE sends SlayerCreatureType (int 166, an assessment property) on appraisal
    /// but never SlayerDamageBonus (float 138), so requiring both (as before 2026-10-05) meant no
    /// slayer was ever seen on an ACE server. A server that does send float 138 gets it used.
    /// </summary>
    internal (int Type, double Bonus) SlayerOf(int weaponId)
    {
        uint uid = unchecked((uint)weaponId);
        if (!_host.HasGetObjectIntProperty || !_host.TryGetObjectIntProperty(uid, PropSlayerCreatureType, out int t) || t <= 0)
            return (0, 0);
        double bonus = _host.HasGetObjectDoubleProperty && _host.TryGetObjectDoubleProperty(uid, PropSlayerDamageBonus, out double b) && b > 0 ? b : 0;
        if (_slayerNoted.TryAdd((weaponId, t), 0))
            _host.Log($"[EquipDiag] weapon 0x{uid:X8} '{WeaponName(weaponId)}' slays {CreatureTypeNames.Name(t)} ({(bonus > 0 ? "x" + bonus.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "bonus not sent")})");
        return (t, bonus);
    }

    // Logged once each: a listed weapon's slayer; a monster whose type isn't known while a slayer is listed.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte> _slayerNoted = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _slayerTypeUnknownNoted = new();

    // Creature type by wcid, once seen: a wcid's type never changes, so the next spawn is instant.
    // Concurrent: the Damage tab's JSON export asks for weaknesses off the combat tick.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, int> _creatureTypeByWcid = new();

    /// <summary>
    /// The monster's CreatureType (PropertyInt 2; 0 = unknown): this session's per-wcid cache,
    /// else the creature store (appraisals saved by name and wcid), else the live appraisal
    /// (the engine identifies every new object), else the shipped world-database table
    /// (CreatureWeakness: wcid and exact name). Never guessed from name keywords.
    /// </summary>
    internal int CreatureTypeOf(uint wcid, string? name, int objectId = 0)
    {
        if (wcid != 0 && _creatureTypeByWcid.TryGetValue(wcid, out int known)) return known;
        int ctype = 0;
        name ??= "";
        if (_creatureStore != null && name.Length > 0)
        {
            CreatureProfile? prof = null;
            bool got = wcid != 0 ? _creatureStore.TryGet(name, wcid, out prof) : _creatureStore.TryGetByName(name, out prof);
            if (got && prof != null) ctype = prof.CreatureType;
        }
        if (ctype == 0 && objectId != 0 && _host.HasGetObjectIntProperty
            && _host.TryGetObjectIntProperty(unchecked((uint)objectId), 2 /* CreatureType */, out int ct))
            ctype = ct;
        if (ctype > 0 && wcid != 0)
        {
            if (_creatureTypeByWcid.Count > 4096) _creatureTypeByWcid.Clear();
            _creatureTypeByWcid[wcid] = ctype;
        }
        // Not seen yet on this server: the shipped world-database table (wcid and name must both
        // match, as for its resists). Not cached, so this monster's own appraisal still wins once
        // it lands. 4,506 of the ~4,900 creature profiles are seeded without a type (2026-10-05),
        // so before this the first fight with a monster had no slayer step.
        if (ctype <= 0) ctype = CreatureWeakness.TableCreatureType(wcid, name);
        return ctype > 0 ? ctype : 0;
    }

    private readonly HashSet<int> _unlistedNoted = new();

    private void NoteUnlisted(int id, string from)
    {
        if (!_unlistedNoted.Add(id)) return;
        var wo = _worldFilter[id];
        if (wo != null && IsShieldItem(wo) && _settings.ItemRules.Any(r => r.Id == id))
        {
            _host.Log($"[EquipDiag] {from} weapon 0x{(uint)id:X8} '{wo.Name}' is a shield — it only goes in the off hand");
            return;
        }
        _host.Log($"[EquipDiag] {from} weapon 0x{(uint)id:X8} '{wo?.Name ?? "?"}' isn't in the Items list — not wielding it");
    }

    /// <summary>The Items list's usable weapons, in list order, with their kind, element, rending and whether each is in hand.</summary>
    internal List<WeaponCandidate> ListedCandidates()
    {
        var list = ListedCandidates(admitCasters: false, out List<int>? leftOut);
        // A script choosing the attack spells (RynthAi.Combat) can attack with a caster whatever
        // War/Void training says, but only when the list has no melee/missile weapon to use:
        // a fighter's listed buff wand must not pull it out of melee.
        if (list.Count == 0 && leftOut != null && ScriptPicksAttack)
            return ListedCandidates(admitCasters: true, out _);
        if (list.Count == 0 && leftOut != null)
            foreach (int id in leftOut)
                if (_wandRejectedNoted.Add(id))
                    _host.Log($"[EquipDiag] caster 0x{(uint)id:X8} '{_worldFilter[id]?.Name}' is not a combat weapon: {CanMagicDiag()}");
        return list;
    }

    /// <param name="admitCasters">Take listed casters even with War and Void untrained.</param>
    /// <param name="leftOut">The casters skipped for that (null when none).</param>
    private List<WeaponCandidate> ListedCandidates(bool admitCasters, out List<int>? leftOut)
    {
        leftOut = null;
        int inHand = InHandWeaponId();
        var list = new List<WeaponCandidate>();
        foreach (var r in _settings.ItemRules)
        {
            if (r.Id == 0 || list.Any(c => c.Id == r.Id)) continue;
            var c = CandidateFor(r.Id, r.Element, inHand, admitCasters, "listed weapon", out bool casterLeftOut);
            if (casterLeftOut) (leftOut ??= new List<int>()).Add(r.Id);
            if (c != null) list.Add(c);
        }
        return list;
    }

    /// <summary>
    /// One weapon as a candidate for the weapon choice, or null when it isn't one now: not known,
    /// not carried, a caster the character can't fight with (<paramref name="casterLeftOut"/>),
    /// a requirement not met, ammo or a shield. <paramref name="what"/> names it in the log
    /// ("listed weapon", "Monsters rule 'X' weapon").
    /// </summary>
    private WeaponCandidate? CandidateFor(int id, string? setElement, int inHand, bool admitCasters, string what, out bool casterLeftOut)
    {
        casterLeftOut = false;
        var wo = _worldFilter[id];
        if (wo == null) return null;
        // Sold, dropped or traded: still known to the client, but not ours to wield (a sold
        // Flaming Greataxe was tried three times, the character standing in the fight, 2026-10-14).
        if (id != inHand && _worldFilter.PlayerHas(id) == false)
        {
            // Not within NotCarriedGraceMs of a wield or move of ours: mid-swap a weapon is
            // briefly in neither hand nor the pack walk. Drakkon's Acid Hand Wraps were dropped
            // from the choice that way on 2026-10-17 and he fought bare-handed.
            if ((RynthClock.Now - _lastEquipTime).TotalMilliseconds >= NotCarriedGraceMs)
            {
                if (_notCarriedNoted.Add(id))
                    _host.Log($"[EquipDiag] {what} 0x{(uint)id:X8} '{wo.Name}' isn't in your pack (sold, dropped or traded?): skipped");
                return null;
            }
        }
        else _notCarriedNoted.Remove(id);
        if (!admitCasters && !IsUsableCombatWeapon(id)) { casterLeftOut = true; return null; }
        if (id != inHand && SkillTooLowFor(id)) return null;
        // A wield requirement the character doesn't meet: the client refuses the wield without
        // asking the server, so no "skill too low" answer ever comes (the Tetsubo, 2026-10-18).
        if (id != inHand && WieldRequirements.Unmet(_host, _charSkills, unchecked((uint)id)) is string unmet)
        {
            if (_wieldReqNoted.Add(id))
            {
                _host.Log($"[EquipDiag] {what} 0x{(uint)id:X8} '{wo.Name}' can't be wielded ({unmet}): left out of the weapon choice");
                _host.WriteToChat($"[RynthAi] {wo.Name} can't be wielded ({unmet}), so combat doesn't use it.", 1);
            }
            return null;
        }
        if (wo.ObjectClass == AcObjectClass.MissileWeapon && IsAmmoItem(wo)) return null;   // listed arrows are the ammo choice, never a weapon to wield
        if (IsShieldItem(wo)) return null;                                                  // listed shields are the off hand's (CombatManager.Offhand.cs)
        var info = ElementTracker.Read(id, wo.Name);
        string elem = WeaponElements.Normalize(setElement);
        if (elem.Length == 0) elem = info.Element;
        var (slayType, slayBonus) = SlayerOf(id);
        return new WeaponCandidate(id, wo.Name, WeaponKind(id), elem, info.Rending, id == inHand, slayType, slayBonus);
    }

    /// <summary>
    /// A weapon the player picked on the Monsters tab (any rule's, the Default row's included).
    /// It is theirs to fight with even when it isn't in the Items list (owner, 2026-10-22).
    /// </summary>
    internal bool IsMonstersTabWeapon(int id) =>
        id != 0 && _settings.MonsterRules.Exists(m => m.WeaponId == id);

    // The weapon in hand, read live at most every InHandReadMs (the read walks the inventory).
    private int _inHandId;
    private DateTime _inHandReadAt = DateTime.MinValue;
    private const double InHandReadMs = 1000;

    /// <summary>The melee/missile weapon or caster the character holds (0 = none readable).</summary>
    internal int InHandWeaponId(bool fresh = false)
    {
        if (InHandOverride != null) return InHandOverride();
        DateTime now = RynthClock.Now;
        if (!fresh && (now - _inHandReadAt).TotalMilliseconds < InHandReadMs) return _inHandId;
        _inHandReadAt = now;
        int found = 0;
        // The server's word when the engine has it: a weapon in a main-hand slot.
        if (EquippedState.TryGet(_host, out var eqMain))
        {
            foreach (var kv in eqMain)
            {
                if ((kv.Value & MainHandSlots) == 0) continue;
                var wo = _worldFilter[kv.Key];
                if (wo == null || WorldObjectCache.IsAmmo(wo)) continue;
                bool weapon = wo.ObjectClass == AcObjectClass.MeleeWeapon || wo.ObjectClass == AcObjectClass.MissileWeapon || IsWandObject(wo);
                if (!weapon) continue;
                if (found == 0 || _settings.ItemRules.Any(r => r.Id == wo.Id)) found = wo.Id;
            }
            _inHandId = found;
            return found;
        }
        try
        {
            var wielded = new List<WorldObject>(2);
            foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: true))
            {
                if (WorldObjectCache.IsAmmo(wo)) continue;
                // The off hand (a dual-wielded weapon in the Shield slot) is not the weapon in hand.
                // CurrentWieldedLocation can read 0: the weenie's own wielder record says where it is.
                // Drakkon dual wielding two Hand Wraps (2026-10-16) had the off-hand pair read as the
                // weapon in hand, and the swaps went from it ("Frost Hand Wraps -> Nodachi").
                if ((WieldedSlotOf(wo) & OffhandRules.ShieldSlot) != 0) continue;
                bool weapon = wo.ObjectClass == AcObjectClass.MeleeWeapon
                           || wo.ObjectClass == AcObjectClass.MissileWeapon
                           || IsWandObject(wo);
                if (!weapon || !WorldObjectCache.IsWieldedByPlayer(_host, wo)) continue;
                wielded.Add(wo);
            }
            // Still two (the slot read nothing for either): the one RynthAi put in the off hand is
            // the off hand's. The slot reads aren't reliable: on 2026-10-17 the off-hand Frost Hand
            // Wraps read as the main weapon again, a swap went out for the Acid pair already in the
            // main hand, and Drakkon fought bare-handed.
            if (wielded.Count > 1 && _offhandSentId != 0 && wielded.Exists(w => w.Id == _offhandSentId))
            {
                NoteTwoInHand(wielded);
                wielded.RemoveAll(w => w.Id == _offhandSentId);
            }
            foreach (var wo in wielded)
            {
                // Two in hand (a wand and a sword mid-swap): a listed one wins.
                if (found == 0 || _settings.ItemRules.Any(r => r.Id == wo.Id)) found = wo.Id;
            }
        }
        catch { }
        _inHandId = found;
        return found;
    }

    // ── Weapons Back After Heals (RearmAfterVitals) ─────────────────────────────
    // The weapons of the last swing (main hand, off hand; 0 = none), and the vitals spell re-armed for.
    private int _fightMainId, _fightOffId;
    private DateTime _rearmedFor = DateTime.MinValue, _rearmStartedAt = DateTime.MinValue;
    private const double RearmSettleMs = 1000, RearmGiveUpMs = 15_000;

    /// <summary>When the last heal, restam or remana spell went out (BuffManager); set by the plugin.</summary>
    public Func<DateTime>? LastVitalCastAt { get; set; }
    /// <summary>True while buffing wants the tick (a cast pending, a vital to refill, buffs to cast); set by the plugin.</summary>
    public Func<bool>? BuffingWantsTick { get; set; }

    /// <summary>A swing went out: what is in the hands is the fight's weapons (casters aside).</summary>
    private void NoteFightWeapons()
    {
        int main = InHandWeaponId();
        if (main == 0 || WeaponKind(main) == CombatMode.Magic) return;
        _fightMainId = main;
        _fightOffId = OffhandInSlot();
    }

    /// <summary>
    /// No target, and a heal / restam / remana spell went out since the last re-arm: put the last
    /// fight's weapons back on (main hand, then off hand) once buffing is done with the tick. True
    /// while a step of it is in flight (the idle branch waits). Bounded by RearmGiveUpMs.
    /// </summary>
    private bool RearmAfterVitalsStep()
    {
        if (!_settings.RearmAfterVitals || _fightMainId == 0) return false;
        DateTime vital = LastVitalCastAt?.Invoke() ?? DateTime.MinValue;
        if (vital == DateTime.MinValue || vital <= _rearmedFor) return false;
        DateTime now = RynthClock.Now;
        if ((now - vital).TotalMilliseconds < RearmSettleMs) return false;   // another vitals spell may follow
        bool buffing;
        try { buffing = BuffingWantsTick?.Invoke() == true; } catch { buffing = true; }
        if (buffing || IsCastInFlight() || BusyCount != 0) return false;

        if (_rearmStartedAt < vital) _rearmStartedAt = now;
        if ((now - _rearmStartedAt).TotalMilliseconds > RearmGiveUpMs)
        {
            _rearmedFor = vital;
            _host.Log($"[EquipDiag] weapons back after heals: gave up after {RearmGiveUpMs / 1000:0} s (main 0x{(uint)_fightMainId:X8} '{WeaponName(_fightMainId)}', off hand 0x{(uint)_fightOffId:X8} '{WeaponName(_fightOffId)}')");
            return false;
        }
        // Still ours to wield: listed (or set on the Monsters tab), carried, not refused for skill.
        if (!(_settings.ItemRules.Any(r => r.Id == _fightMainId) || IsMonstersTabWeapon(_fightMainId))
            || _worldFilter.PlayerHas(_fightMainId) == false || SkillTooLowFor(_fightMainId))
        {
            _rearmedFor = vital;
            return false;
        }

        // Fast Weapon Swap off: as before 2026-10-22 (CombatManager.LegacySwap.cs).
        if (!_settings.FastWeaponSwap) return LegacyRearmSwap(now, vital);
        var hands = EquippedState.ReadFresh(_host, _worldFilter);
        bool offWanted = _fightOffId != 0 && _fightOffId != _fightMainId
            && _worldFilter.PlayerHas(_fightOffId) != false && _settings.ItemRules.Any(r => r.Id == _fightOffId);
        const int mainHand = HandSwapSequence.HandSlots & ~HandSwapSequence.ShieldSlot;
        bool mainIn = hands.TryGetValue(_fightMainId, out int mainAt) && (mainAt & mainHand) != 0;
        bool offIn = !offWanted || (hands.TryGetValue(_fightOffId, out int offAt) && (offAt & HandSwapSequence.ShieldSlot) != 0);

        // The weapons are back: the fight's stance, once, when the swap went through peace (a weapon
        // wielded in a combat mode is put into its stance by the server itself; in peace it isn't).
        if (!_rearmSwap.Active && mainIn && offIn)
        {
            if (_rearmStanceWanted != 0 && !RearmStanceStep(now)) return true;   // held a moment (the pacer): next tick
            _rearmedFor = vital;
            _host.Log($"[EquipDiag] weapons back after heals: 0x{(uint)_fightMainId:X8} '{WeaponName(_fightMainId)}'" +
                      (offWanted ? $" and 0x{(uint)_fightOffId:X8} '{WeaponName(_fightOffId)}'" : "") + " in hand");
            return false;
        }

        // Weapons back as fast as the server allows (2026-10-22, HandSwapSequence): from Magic, peace
        // first (one stance leg), the wand into a pack, the main weapon and the off hand, each the moment
        // the server answers the last, then the fight's stance. Drakkon's Sceptre -> two Hand Wraps took
        // 2.3 s: the use of the main weapon (the client puts the wand away after a forced stance change,
        // 1.2 s), then the off hand once the gate let it go (a corpse open went first). The gate is held
        // for the whole swap, so nothing else swaps between its steps.
        if (!_rearmSwap.Active)
        {
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("rearm-after-vitals")) return true;
            var wants = new List<(int Id, int Slot)> { (_fightMainId, MainSlotFor(_fightMainId)) };
            if (offWanted) wants.Add((_fightOffId, HandSwapSequence.ShieldSlot));
            _rearmWants = wants;
            int mode = CurrentCombatMode;
            _rearmSwap.Start("weapons back", now, wants, peaceFirst: mode != CombatMode.NonCombat);
            _handsSwapForFight = false;
            _handsGateBy = "rearm-after-vitals";
            _weaponSwapGate?.Hold(_handsGateBy, (long)_rearmSwap.GiveUpMs + 1000);
            _rearmSwapOpen = true;
            _rearmLastMoved = 0;
            _host.Log($"[EquipDiag] weapons back after heals: 0x{(uint)_fightMainId:X8} '{WeaponName(_fightMainId)}'" +
                      (offWanted ? $" + off hand 0x{(uint)_fightOffId:X8} '{WeaponName(_fightOffId)}'" : "") + $" (mode {mode})");
        }

        _rearmVital = vital;
        return RearmSwapAdvance(now);
    }

    /// <summary>
    /// The weapons-back swap's next step. Called from RearmAfterVitalsStep and, so the steps don't wait
    /// on the combat tick's gates (the pacer holds them while our own peace request settles, and a
    /// corpse open slipped in between on Drakkon, 10-22 22:45), from the top of each heartbeat.
    /// True while it is under way.
    /// </summary>
    private bool RearmSwapAdvance(DateTime now)
    {
        var hands = EquippedState.ReadFresh(_host, _worldFilter);
        var step = _rearmSwap.Next(hands, CurrentCombatMode, now);
        switch (step.Act)
        {
            case HandSwapSequence.Act.Wait:
                return true;

            case HandSwapSequence.Act.Stance:
            {
                int burst = SwapBurstEnabled?.Invoke() == true ? TryRearmBurst(hands, now) : 0;
                if (burst != 0) return true;
                if (_host.ChangeCombatModeFor(step.Slot, "Combat", "weapons back: peace first, then the hands"))
                    _rearmSwap.Sent(step, now);
                return true;
            }

            case HandSwapSequence.Act.MoveToPack:
            {
                int pack = OpenPackOverride?.Invoke() ?? WorldObjectCache.FindPackFor(_host, _worldFilter, includeMainPack: true, requireFree: 1);
                if (pack == 0)
                {
                    WorldObjectCache.WarnPackFull(_host, "can't put the wand away to put the weapons back");
                    bool fight = _handsSwapForFight;
                    FinishRearmSwap(now, done: false, why: "no pack with room");
                    if (fight) NoteFightSwapFailed(now); else _rearmedFor = _rearmVital;
                    return false;
                }
                _host.MoveItemInternal((uint)step.Id, (uint)pack, 0, 1);   // amount>=1 (engine rejects 0)
                _rearmSwap.Sent(step, now);
                _rearmLastMoved = step.Id;
                EquippedState.Invalidate();
                _host.Log($"[EquipDiag] {HandsSwapLabel}: 0x{(uint)step.Id:X8} '{WeaponName(step.Id)}' -> pack 0x{(uint)pack:X8} {(now - _rearmSwap.StartedAt).TotalMilliseconds:0} ms in");
                return true;
            }

            case HandSwapSequence.Act.Wield:
            {
                bool off = step.Slot == HandSwapSequence.ShieldSlot;
                if (_host.HasWieldItem) _host.WieldItem((uint)step.Id, (uint)step.Slot);
                else _host.UseFor((uint)step.Id, "Combat", $"wield: {HandsSwapLabel}");
                _rearmSwap.Sent(step, now);
                _rearmLastMoved = step.Id;
                NoteRearmWield(step.Id, off, now);
                _host.Log($"[EquipDiag] {HandsSwapLabel}: wielding 0x{(uint)step.Id:X8} '{WeaponName(step.Id)}' ({(off ? "off hand" : "main hand")}) {(now - _rearmSwap.StartedAt).TotalMilliseconds:0} ms in");
                return true;
            }

            case HandSwapSequence.Act.Done:
                if (_handsSwapForFight)
                {
                    FightSwapDone(now);
                    return true;
                }
                _rearmStanceWanted = _rearmSwap.WentThroughPeace ? WeaponKind(_fightMainId) : 0;
                if (_rearmStanceWanted == CombatMode.Magic) _rearmStanceWanted = 0;
                _rearmStanceSince = now;
                _rearmStanceForFight = false;
                FinishRearmSwap(now, done: true);
                RearmStanceStep(now);   // at once: the server starts it when the peace animation ends
                return true;

            default:
            {
                bool fight = _handsSwapForFight;
                FinishRearmSwap(now, done: false, why: step.Why);
                if (fight) NoteFightSwapFailed(now);
                else _rearmedFor = _rearmVital;   // combat's own equip step takes it from here
                return false;
            }
        }
    }

    private void NoteRearmWield(int id, bool off, DateTime now)
    {
        if (off) { _offhandSentId = id; _lastEquipTime = now; EquippedState.Invalidate(); }
        else MarkWieldSent(id, _handsSwapForFight && _handsSwapFrom != 0 ? _handsSwapFrom : InHandWeaponId());
    }

    /// <summary>
    /// The fight's stance after a weapons-back swap through peace, once (past our own peace request
    /// only: the server holds it until the peace animation ends). True when it went or isn't wanted.
    /// </summary>
    private bool RearmStanceStep(DateTime now)
    {
        if (_rearmStanceWanted == 0 || (_settings.PeaceModeWhenIdle && !_rearmStanceForFight) || CurrentCombatMode != CombatMode.NonCombat
            || (now - _rearmStanceSince).TotalMilliseconds >= RearmStanceWaitMs)
        {
            _rearmStanceWanted = 0;
            return true;
        }
        var a = ActionPacer.Current(_host);
        bool onlyOurPeace = a == null || (a.Kind == PacedKind.CombatMode && a.ModeTarget == CombatMode.NonCombat && a.Who == "Combat");
        if (!_host.ChangeCombatModeFor(_rearmStanceWanted, "Combat", "weapons back: the fight's stance", force: onlyOurPeace))
            return false;
        _host.Log($"[EquipDiag] {(_rearmStanceForFight ? "weapons for the fight" : "weapons back after heals")}: {CombatModeName(_rearmStanceWanted)} mode");
        if (_rearmStanceForFight)
        {
            // Combat's own stance request (the wielded branch) waits its retry delay behind this one.
            lastStanceAttempt = RynthClock.Now;
            NoteStanceFlipSent();
        }
        _rearmStanceWanted = 0;
        return true;
    }

    /// <summary>
    /// The weapons-back burst (test toggle, /ra swaptest burst on): peace, the wand out (raw
    /// PutItemInContainer) and the main hand and the off hand wielded, queued in one tick so they go
    /// out in one frame. Only with no combat-mode change for BurstQuietMs. 0 = not tried, 1 = held this
    /// tick, 2 = sent. See BuffManager.TryWandBurst.
    /// </summary>
    private int TryRearmBurst(IReadOnlyDictionary<int, int> hands, DateTime now)
    {
        if (!_host.HasPutItemInContainer || !_host.HasWieldItem) return 0;
        // BuffManager's read when the plugin wired it (every heartbeat); this manager's own tick doesn't run
        // while buffing has the tick, so its read saw the switch to Magic only at the weapons-back tick
        // and held every burst (Drakkon, 10-22 23:06-23:21).
        DateTime changed = LastModeChangeAt?.Invoke() ?? _cmModeChangedAt;
        if ((now - changed).TotalMilliseconds < RearmBurstQuietMs) return 0;
        var moves = _rearmSwap.Blockers(hands);
        if (moves.Count > 2) return 0;
        int pack = 0;
        if (moves.Count > 0)
        {
            pack = OpenPackOverride?.Invoke() ?? WorldObjectCache.FindPackFor(_host, _worldFilter, includeMainPack: true, requireFree: moves.Count);
            if (pack == 0) return 0;
        }
        if (!_host.ChangeCombatModeFor(CombatMode.NonCombat, "Combat", $"{HandsSwapLabel} burst: peace, the wand out and the weapons on at once")) return 1;
        foreach (int id in moves) _host.PutItemInContainer(unchecked((uint)id), unchecked((uint)pack), 0);
        var wants = _rearmWants;
        foreach (var (id, slot) in wants)
        {
            _host.WieldItem(unchecked((uint)id), unchecked((uint)slot));
            NoteRearmWield(id, slot == HandSwapSequence.ShieldSlot, now);
        }
        _rearmSwap.SentBurst(moves, now);
        _rearmLastMoved = wants.Count > 0 ? wants[^1].Id : 0;
        _host.Log($"[EquipDiag] {HandsSwapLabel} burst {(now - _rearmSwap.StartedAt).TotalMilliseconds:0} ms in: peace, " +
                  (moves.Count > 0 ? string.Join(", ", moves.ConvertAll(id => $"0x{(uint)id:X8} '{WeaponName(id)}'")) + $" -> pack 0x{(uint)pack:X8}, " : "") +
                  string.Join(", ", wants.ConvertAll(w => $"0x{(uint)w.Id:X8} '{WeaponName(w.Id)}' on")) + ", in one frame");
        return 2;
    }

    /// <summary>The test toggle for the burst (/ra swaptest burst on), from BuffManager; set by the plugin.</summary>
    public Func<bool>? SwapBurstEnabled { get; set; }
    /// <summary>When the combat mode last changed (BuffManager's every-heartbeat read); set by the plugin.</summary>
    public Func<DateTime>? LastModeChangeAt { get; set; }
    private const double RearmBurstQuietMs = 1300;
    private int _cmLastSeenMode = -1;
    private DateTime _cmModeChangedAt = DateTime.MinValue;
    private DateTime _rearmVital = DateTime.MinValue;
    private List<(int Id, int Slot)> _rearmWants = new();

    /// <summary>Each heartbeat: the weapons-back swap's steps, and when the combat mode last changed.</summary>
    private void TickRearmSwap()
    {
        DateTime now = RynthClock.Now;
        int m = CurrentCombatMode;
        if (m != _cmLastSeenMode)
        {
            if (_cmLastSeenMode != -1) _cmModeChangedAt = now;
            _cmLastSeenMode = m;
        }
        if (_rearmSwap.Active)
        {
            if (!_settings.IsMacroRunning)
            {
                bool fight = _handsSwapForFight;
                FinishRearmSwap(now, done: false, why: "the macro stopped");
                if (fight) NoteFightSwapFailed(now, counted: false);
            }
            // A target arriving doesn't end it: combat's equip points it at the fight's weapons, or ends
            // it for a wand (FightJoinsHandsSwap). Ending it freed the gate only after the full 3 s.
            else RearmSwapAdvance(now);
        }
        else if (_rearmStanceWanted != 0 && (_activeTargetId == 0 || _rearmStanceForFight) && _settings.IsMacroRunning)
            RearmStanceStep(now);
    }

    // The weapons-back swap (HandSwapSequence) and what it owes once it ends.
    private readonly HandSwapSequence _rearmSwap = new();
    private bool _rearmSwapOpen;
    private int _rearmLastMoved;
    private int _rearmStanceWanted;
    private DateTime _rearmStanceSince = DateTime.MinValue;
    private const double RearmStanceWaitMs = 3000;

    /// <summary>The slot a weapon goes back into: its own (two-handed, missile, held), else the main hand.</summary>
    private int MainSlotFor(int id)
    {
        int valid = _worldFilter[id]?.Values(LongValueKey.Locations, 0) ?? 0;
        if ((valid & HandSwapSequence.TwoHandedSlot) != 0) return HandSwapSequence.TwoHandedSlot;
        if ((valid & HandSwapSequence.MissileSlot) != 0) return HandSwapSequence.MissileSlot;
        if ((valid & HandSwapSequence.MeleeSlot) != 0) return HandSwapSequence.MeleeSlot;
        if ((valid & HandSwapSequence.HeldSlot) != 0) return HandSwapSequence.HeldSlot;
        return HandSwapSequence.MeleeSlot;
    }

    private void FinishRearmSwap(DateTime now, bool done, string why = "")
    {
        if (!_rearmSwapOpen) return;
        _rearmSwapOpen = false;
        double ms = (now - _rearmSwap.StartedAt).TotalMilliseconds;
        string trail = _rearmSwap.Trail;
        string what = HandsSwapLabel;
        _rearmSwap.Cancel();
        _handsSwapForFight = false;
        _handsSwapFrom = 0;
        _weaponSwapGate?.Release(_handsGateBy, _rearmLastMoved);
        LastRearmSwapMs = ms;
        _host.Log(done
            ? $"[EquipDiag] {what} in hand {ms:0} ms after the swap began ({trail})"
            : $"[EquipDiag] {what} stopped after {ms:0} ms: {why} ({trail})");
    }

    /// <summary>The last weapons-back swap's length (ms).</summary>
    internal double LastRearmSwapMs { get; private set; }

    // Logged once per pair: what the slot reads say for two wielded weapons (the off-hand diagnosis).
    private readonly HashSet<string> _twoInHandNoted = new();

    private void NoteTwoInHand(List<WorldObject> wielded)
    {
        var parts = new List<string>();
        foreach (var w in wielded)
        {
            int cur = w.Values(LongValueKey.CurrentWieldedLocation, 0);
            uint loc = 0;
            try { if (_host.HasGetObjectWielderInfo) _host.TryGetObjectWielderInfo(unchecked((uint)w.Id), out _, out loc); } catch { }
            parts.Add($"0x{(uint)w.Id:X8} '{w.Name}' slot=0x{cur:X} wielderLoc=0x{loc:X}");
        }
        string key = string.Join("|", parts);
        if (_twoInHandNoted.Count > 64) _twoInHandNoted.Clear();
        if (_twoInHandNoted.Add(key))
            _host.Log($"[EquipDiag] two weapons read as in hand ({string.Join(", ", parts)}): 0x{(uint)_offhandSentId:X8} '{WeaponName(_offhandSentId)}' went into the off hand, so it is the off hand's");
    }

    /// <summary>
    /// The client's container walk (at most 1 s old) found <paramref name="id"/> inside a pack. The
    /// weenie's wielder record can still name the player after a weapon went into a pack (buffing's
    /// wand swap): every wielder-record read checks this first. Drakkon attacked bare-handed with
    /// his Acid Hand Wraps "wielded" in the pack (2026-10-17 12:09, died; again 10-18 00:07).
    /// </summary>
    private bool InPackNow(int id)
    {
        try { _worldFilter.GetDirectInventory(forceRefresh: false); } catch { }
        return _worldFilter.InPackWalk(id);
    }

    // EquipMask main-hand slots: MeleeWeapon, MissileWeapon, Held (casters), TwoHanded.
    private const int MainHandSlots = 0x00100000 | 0x00400000 | 0x01000000 | 0x02000000;

    /// <summary>Where the player wields <paramref name="wo"/>: the tracker's slot, else CurrentWieldedLocation, else the weenie's wielder record (0 = not known).</summary>
    private int WieldedSlotOf(WorldObject wo)
    {
        if (EquippedState.SlotOf(_host, wo.Id) is int tracked) return tracked;
        int slot = wo.Values(LongValueKey.CurrentWieldedLocation, 0);
        if (slot != 0 || !_host.HasGetObjectWielderInfo) return slot;
        try
        {
            uint pid = _host.GetPlayerId();
            if (pid != 0 && _host.TryGetObjectWielderInfo(unchecked((uint)wo.Id), out uint wielder, out uint loc) && wielder == pid
                && !InPackNow(wo.Id))
                return unchecked((int)loc);
        }
        catch { }
        return 0;
    }

    /// <summary>The combat mode a weapon puts the character in (0 = unknown).</summary>
    private int WeaponKind(int id)
    {
        var wo = _worldFilter[id];
        if (wo == null) return 0;
        return IsWandObject(wo) ? CombatMode.Magic
             : wo.ObjectClass == AcObjectClass.MissileWeapon ? CombatMode.Missile
             : CombatMode.Melee;
    }

    /// <summary>What hurts <paramref name="target"/> most (server data, learned, creature type); null = unknown.</summary>
    internal CreatureWeakness.Ranking? WeaknessFor(WorldObject? target)
    {
        if (target == null) return null;
        // The creature's own appraisal resists win when the server sent any (stock ACE doesn't).
        var appraised = AppraisedWeakness(target.Id);
        if (appraised != null) return appraised;
        uint wcid = 0;
        if (_fightTargetWcid != 0 && target.Id == _fightTargetId) wcid = _fightTargetWcid;
        else if (_host.HasGetObjectWcid && _host.TryGetObjectWcid((uint)target.Id, out uint w)) wcid = w;
        return WeaknessFor(wcid, target.Name, target.Id);
    }

    // ResistSlash, Pierce, Bludgeon, Fire, Cold, Acid, Electric (64-70), ResistNether (166),
    // in CreatureWeakness.Elements order.
    private static readonly uint[] ResistFloats = { 64, 65, 66, 67, 68, 69, 70, 166 };

    private CreatureWeakness.Ranking? AppraisedWeakness(int objectId)
    {
        if (objectId == 0 || !_host.HasGetObjectDoubleProperty) return null;
        var m = new double?[8];
        bool any = false;
        for (int i = 0; i < 8; i++)
            if (_host.TryGetObjectDoubleProperty(unchecked((uint)objectId), ResistFloats[i], out double v)) { m[i] = v; any = true; }
        return any ? CreatureWeakness.FromAppraisal(m) : null;
    }

    /// <summary>
    /// Optional UtilityBelt damage seed lookup (wcid, name) → damage types best first, or null.
    /// Set by the plugin when the seed store loads; null (default) leaves the ranking as before.
    /// Called on the combat thread.
    /// </summary>
    internal Func<uint, string, IReadOnlyList<string>?>? MobSeedLookup { get; set; }

    /// <summary>The same by wcid and name; <paramref name="objectId"/> (optional) reads a live creature type.</summary>
    internal CreatureWeakness.Ranking? WeaknessFor(uint wcid, string? name, int objectId = 0)
    {
        name ??= "";
        int ctype = CreatureTypeOf(wcid, name, objectId);
        string learned = wcid != 0 ? _damageStore?.GetBestElement(wcid) ?? "" : "";
        IReadOnlyList<string>? seeded = null;
        try { seeded = MobSeedLookup?.Invoke(wcid, name); }
        catch { seeded = null; }   // a bad seed file must never stop element choice
        return CreatureWeakness.Rank(wcid, name, ctype, learned, seeded);
    }

    // Server replies seen when the last combat wield was sent (UseDone / WeenieError sequence).
    private int _swapUseDoneSeq, _swapWeenieSeq;

    // ACE's WeenieError SkillTooLow: the character can't wield the weapon (its wield requirement).
    internal const uint SkillTooLowErr = 0x0468;
    /// <summary>A weapon refused with SkillTooLow is left out of the choice this long (a buff or a
    /// skill raise may let it in later).</summary>
    internal const double SkillTooLowSkipMin = 30;
    private readonly Dictionary<int, DateTime> _skillTooLowUntil = new();

    /// <summary>
    /// True while <paramref name="id"/> sits out a SkillTooLow refusal. Drakkon (2026-10-16) was
    /// sent to a Nodachi he lacked the skill for whenever a monster was weak to Slash: the off hand
    /// went into the pack for it, three wields were refused, and it began again two minutes later.
    /// </summary>
    internal bool SkillTooLowFor(int id)
    {
        if (!_skillTooLowUntil.TryGetValue(id, out DateTime until)) return false;
        if (RynthClock.Now < until) return true;
        _skillTooLowUntil.Remove(id);
        return false;
    }

    private void NoteSkillTooLow(int id, string? why = null)
    {
        if (id == 0) return;
        bool first = !_skillTooLowUntil.ContainsKey(id);
        _skillTooLowUntil[id] = RynthClock.Now.AddMinutes(SkillTooLowSkipMin);
        _planCache = null;
        if (first)
        {
            string msg = $"0x{(uint)id:X8} '{WeaponName(id)}': {why ?? "the server says your skill is too low to wield it"} - left out of the weapon choice for {SkillTooLowSkipMin:0} min";
            _host.Log($"[EquipDiag] {msg}");
            _host.WriteToChat($"[RynthAi] {msg}.", 1);
        }
    }

    /// <summary>
    /// Asks the swap tracker about <paramref name="chosenId"/> with <paramref name="inHandId"/>
    /// in hand, after folding in a server refusal of the wield in flight. Logs a give-up once.
    /// </summary>
    private WeaponSwapTracker.Step SwapStep(int chosenId, int inHandId) => SwapStepFor(chosenId, inHandId, false, null);

    /// <summary>
    /// <see cref="SwapStep"/> for the fight's chosen weapon. <paramref name="forThisMonster"/>: the
    /// player set it for the target (see WeaponSwapTracker.Next); <paramref name="source"/> (the
    /// plan's reason) goes on a hold's log line.
    /// </summary>
    private WeaponSwapTracker.Step SwapStepFor(int chosenId, int inHandId, bool forThisMonster, string? source)
    {
        DateTime now = RynthClock.Now;
        FoldServerAnswer(SwapTracker, now);
        var step = SwapTracker.Next(chosenId, inHandId, now, WeaponName, forKey: activeTargetId, forThisMonster: forThisMonster);
        if (SwapTracker.Notice != null)
        {
            _host.Log($"[EquipDiag] {SwapTracker.Notice}");
            SwapTracker.Notice = null;
            // The server's own list (v87) says it never reached the hand: the wield can't work
            // (the client refused it unasked). Out of the choice like a "skill too low" answer,
            // not retried every 2 minutes with the off hand taken off each time (the Tetsubo, 2026-10-18).
            // Only when the list shows it missing now AND it was never seen in a main-hand slot while
            // its wields were under way (NeverReachedHand).
            if (SwapTracker.GaveUpId is int gaveUp && NeverReachedHand(gaveUp))
                NoteSkillTooLow(gaveUp, "it never reached your hand (the client refused the wield)");
        }
        SwapTracker.GaveUpId = null;
        if (step == WeaponSwapTracker.Step.Hold && (RynthClock.Now - _lastSwapHoldLogAt).TotalSeconds > 10)
        {
            _lastSwapHoldLogAt = RynthClock.Now;
            // The chosen weapon's source too (2026-11-08): R-20261107-141028-b33e's log couldn't say
            // whether the held Fire Staff was the monster's own pick or the weakness's.
            string wanted = string.IsNullOrEmpty(source) ? "" : $"; wanted for: {source}";
            _host.Log($"[EquipDiag] keeping 0x{(uint)inHandId:X8} '{WeaponName(inHandId)}' instead of 0x{(uint)chosenId:X8} '{WeaponName(chosenId)}' ({SwapTracker.HoldReason}{wanted})");
        }
        return step;
    }

    private DateTime _lastSwapHoldLogAt = DateTime.MinValue;

    /// <summary>
    /// True only when the engine's equipment list can be read, does not hold
    /// <paramref name="id"/> now, and the swap tracker never saw it in a main-hand slot while its
    /// wields were under way (<see cref="NoteSwapWieldsSeen"/>). A readable list alone used to be
    /// enough, so a weapon that did reach the hand, and that buffing then swapped out for the wand
    /// before combat looked, was left out of the weapon choice for 30 min. A weapon that truly never
    /// shows in the list (refused by the client or the server) is still left out.
    /// </summary>
    private bool NeverReachedHand(int id)
    {
        if (SwapTracker.WasSeenWielded(id)) return false;
        EquippedState.Invalidate();
        return EquippedState.TryGet(_host, out var map) && !map.ContainsKey(id);
    }

    /// <summary>
    /// While a combat wield is in flight or has recent tries counted, read the engine's equipment
    /// list (API v87, the server's own wield messages) and tell the swap tracker about every item
    /// in a main-hand slot (the slots <see cref="InHandWeaponId"/> reads; a weapon in the shield
    /// slot is not the wield combat asked for). Called every tick by the plugin ahead of its
    /// buffing hold, which skips <see cref="OnHeartbeat"/>, and from OnHeartbeat itself. Reads
    /// only, no game command, and never throws.
    /// </summary>
    internal void NoteSwapWieldsSeen()
    {
        try
        {
            if (!SwapTracker.HasAttemptsAt(RynthClock.Now)) return;
            if (!EquippedState.TryGet(_host, out var map)) return;
            foreach (var kv in map)
                if ((kv.Value & MainHandSlots) != 0) SwapTracker.SeenWielded(kv.Key);
        }
        catch { /* a failed read must never stop the tick */ }
    }

    /// <summary>
    /// Why a swap the tracker allows should not start now (null = start it). Settling: the
    /// target was switched to (it replaced another live target) under WeaponSwapSettleMs ago;
    /// wait (Kelpie 2026-10-14 11:41:06: the crossbow swap sent, then a switch to another monster
    /// 230 ms later). A lock after a kill or a drop swaps at once: in Kelpie's 2026-10-14/15 logs
    /// 2-4% of those were followed by a quick switch, and waiting on every one put 0.75 s in front
    /// of 22 of 25 swaps that went ahead anyway. Not settling: keep the weapon in hand, because
    /// the target is at or under WeaponSwapMinHealthPct (same kind of weapon only). Never once a
    /// swap is under way (SwapHoldsAllowed), so a launcher is never kept after its ammo went into
    /// the pack.
    /// </summary>
    private string? SwapDeferReason(WorldObject target, int chosenId, int inHand, out bool settling)
    {
        settling = false;
        if (!SwapHoldsAllowed(inHand)) return null;

        int settleMs = _settings.WeaponSwapSettleMs;
        if (settleMs > 0 && _lockWasSwitch && _lockedTargetId == target.Id && _targetLockedAt != DateTime.MinValue)
        {
            double lockedMs = (RynthClock.Now - _targetLockedAt).TotalMilliseconds;
            if (lockedMs >= 0 && lockedMs < settleMs)
            {
                settling = true;
                return $"target switched {lockedMs:0} ms ago";
            }
        }

        int minHealth = _settings.WeaponSwapMinHealthPct;
        if (minHealth > 0 && WeaponKind(inHand) != 0 && WeaponKind(inHand) == WeaponKind(chosenId))
        {
            float ratio = _worldFilter.GetHealthRatio(target.Id);
            if (ratio >= 0f && ratio * 100f <= minHealth)
                return $"target at {ratio * 100f:0}% health";
        }
        return null;
    }

    /// <summary>
    /// True when a swap rule may keep or delay with <paramref name="inHand"/>: nothing of a swap
    /// is under way (no wield in flight, no old ammo or off hand on its way into the pack, no
    /// ammo follow-up armed), and a launcher in hand has fitting ammo on, so it can shoot.
    /// </summary>
    private bool SwapHoldsAllowed(int inHand)
    {
        if (inHand == 0 || SwapTracker.PendingId != 0) return false;
        if (_ammoDequipPendingId != 0 || AmmoSwapInProgress || _offDequipPendingId != 0) return false;
        int lt = LauncherTypeOf(_worldFilter[inHand]);
        if (lt == 0) return true;
        var wielded = PackAmmo().FirstOrDefault(a => a.Wielded);
        return wielded != null && MissileAmmo.CanShoot(lt, wielded.AmmoType);
    }

    // Last swap deferral logged: (chosen, in hand, reason kind) and when.
    private (int Chosen, int Hand, bool Settle) _lastSwapDeferLogged;
    private DateTime _lastSwapDeferLogAt = DateTime.MinValue;

    private void LogSwapDefer(int chosenId, int inHand, string why)
    {
        var key = (chosenId, inHand, why.StartsWith("target switched", StringComparison.Ordinal));
        DateTime now = RynthClock.Now;
        if (key == _lastSwapDeferLogged && (now - _lastSwapDeferLogAt).TotalSeconds < 10) return;
        _lastSwapDeferLogged = key;
        _lastSwapDeferLogAt = now;
        _host.Log($"[EquipDiag] not swapping 0x{(uint)inHand:X8} '{WeaponName(inHand)}' for 0x{(uint)chosenId:X8} '{WeaponName(chosenId)}' yet ({why})");
    }

    private string WeaponName(int id) => id == 0 ? "" : (_worldFilter[id]?.Name ?? "");

    /// <summary>Send one combat wield of <paramref name="weaponId"/> and tell the swap tracker.</summary>
    private void SendWield(int weaponId, int inHandId, string why)
    {
        _host.UseFor((uint)weaponId, "Combat", $"wield: {why}");
        MarkWieldSent(weaponId, inHandId);
        _host.Log($"[EquipDiag] {why}: 0x{(uint)inHandId:X8} '{WeaponName(inHandId)}' -> 0x{(uint)weaponId:X8} '{WeaponName(weaponId)}' (try {SwapTracker.TriesFor(weaponId)}/{SwapTracker.MaxTries})");
    }

    /// <summary>A combat wield of <paramref name="weaponId"/> was just sent: the swap tracker
    /// holds further swaps until it lands, is refused, or times out.</summary>
    private void MarkWieldSent(int weaponId, int inHandId)
    {
        DateTime now = RynthClock.Now;
        if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out _)) _swapUseDoneSeq = uSeq;
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out _, out _, out _)) _swapWeenieSeq = wSeq;
        SwapTracker.Sent(weaponId, inHandId, now);
        EquippedState.Invalidate();
        if (weaponId == _offhandSentId) _offhandSentId = 0;
        _inHandReadAt = DateTime.MinValue;   // read the hand fresh next time
        _offSlotReadAt = DateTime.MinValue;  // and the off hand
        _lastEquipTime = now;
    }

    // The character was in portal space at the last heartbeat (a portal, recall or teleport).
    private bool _swapSawPortal;

    /// <summary>On leaving portal space, the swap tracker forgets tries and rests from the old place.</summary>
    private void NoteSwapPortal()
    {
        bool inPortal;
        try { inPortal = _host.HasIsPortaling && _host.IsPortaling(); }
        catch { return; }   // a failed read must never stop the heartbeat
        if (inPortal) { _swapSawPortal = true; return; }
        if (!_swapSawPortal) return;
        _swapSawPortal = false;
        SwapTracker.NewPlace();
        AmmoTracker.NewPlace();
        OffhandTracker.NewPlace();
    }

    private enum WieldAnswer { None, Refused, Busy }

    /// <summary>
    /// Folds the server's answer to the wield <paramref name="tracker"/> has in flight into it: a
    /// refusal is a failed try, 'too busy' is not (logged).
    /// </summary>
    private void FoldServerAnswer(WeaponSwapTracker tracker, DateTime now)
    {
        int pending = tracker.PendingId;
        if (pending == 0) return;
        switch (ServerAnswerSinceSend(out uint err))
        {
            case WieldAnswer.Refused:
                tracker.Refused(now, WeaponName);
                if (err == SkillTooLowErr && ReferenceEquals(tracker, SwapTracker)) NoteSkillTooLow(pending);
                break;
            case WieldAnswer.Busy:
                if (tracker.Busy(now, WeaponName))
                    _host.Log($"[EquipDiag] wield of 0x{(uint)pending:X8} '{WeaponName(pending)}' answered 'too busy': not counted, again in {tracker.BusyRetryMs / 1000:0.#} s");
                break;
        }
    }

    // GameEvent InventoryServerSaveFailed: the server refused an inventory change.
    private const uint InventorySaveFailedEvent = 0x00A0;

    /// <summary>
    /// What the server said to the wield since it was sent. Busy: 'too busy' (0x1D), or an
    /// InventoryServerSaveFailed with no error. ACE answers a wield while busy (a recall, a
    /// portal, an animation) with a WeenieError 0x1D and then that SaveFailed, and the engine
    /// keeps only the last record, so the 0x1D is usually read as "error 0" (flight recorder
    /// 2026-10-15 00:44:23-31: three of these, read as no answer, cost all 3 tries).
    /// </summary>
    private WieldAnswer ServerAnswerSinceSend(out uint err)
    {
        err = 0;
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out uint wErr, out uint wEvt, out _)
            && wSeq != _swapWeenieSeq)
        {
            _swapWeenieSeq = wSeq;
            if (wErr == ActionPacer.TooBusyErr || (wEvt == InventorySaveFailedEvent && wErr == 0)) return WieldAnswer.Busy;
            if (wErr != 0) { err = wErr; return WieldAnswer.Refused; }
        }
        if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out uint uErr) && uSeq != _swapUseDoneSeq)
        {
            _swapUseDoneSeq = uSeq;
            if (uErr == ActionPacer.TooBusyErr) return WieldAnswer.Busy;
            if (uErr != 0) { err = uErr; return WieldAnswer.Refused; }
        }
        return WieldAnswer.None;
    }

    /// <summary>
    /// The combat mode already matches the chosen weapon's kind, but the chosen one isn't
    /// confirmed in hand: if another weapon of the same kind is in hand (a sword for a sword,
    /// a bow for a bow, a wand for a wand), wield the chosen one. One swap in flight, capped,
    /// no flip-flop (WeaponSwapTracker). True while the attack should wait for the wield.
    /// Needs a positive read of the other weapon in hand, so a stale wield read on the chosen
    /// weapon can't turn into a UseObject that takes it off. Wand-for-wand used to never
    /// happen: Magic mode was trusted, so whatever wand was in hand stayed there (2026-09-30).
    /// </summary>
    private bool SwapToChosenSameKind(int weaponId, int desiredMode, bool forThisMonster = false, string? source = null)
    {
        int inHand = InHandWeaponId();
        // Empty hands in the stance already, the chosen weapon positively in a pack: wield it. The
        // positive-read rule below guards against a UseObject on a weapon that is really wielded
        // (it would take it off); a weapon the container walk finds in a pack can't be. Without
        // this Drakkon fought bare-handed in Melee stance after buffing's wand swap (2026-10-17/18).
        if (inHand == 0 && weaponId != 0 && (EquippedState.IsWielded(_host, weaponId) is bool trk ? !trk : InPackNow(weaponId)))
        {
            switch (SwapStep(weaponId, 0))
            {
                case WeaponSwapTracker.Step.Wait: return true;
                case WeaponSwapTracker.Step.Swap:
                    var offPrep = ClearOffhandFor(weaponId, _curOffhandMode);
                    if (offPrep == OffPrep.Busy) return true;
                    if (offPrep == OffPrep.Blocked) return false;
                    if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-empty-hands")) return true;
                    SendWield(weaponId, 0, "empty hands: wield");
                    return true;
                default: return false;
            }
        }
        if (inHand == 0 || inHand == weaponId) { SwapTracker.Confirmed(weaponId, RynthClock.Now); if (inHand == weaponId) _equippedWeaponId = weaponId; return false; }
        if (WeaponKind(inHand) != desiredMode) return false;   // a cross-kind case; the branches below handle it

        _equippedWeaponId = inHand;
        switch (SwapStepFor(weaponId, inHand, forThisMonster, source))
        {
            case WeaponSwapTracker.Step.Wait: return true;
            case WeaponSwapTracker.Step.Swap:
                // A two-hander or a launcher: the shield (or off-hand weapon) comes off first.
                var off = ClearOffhandFor(weaponId, _curOffhandMode);
                if (off == OffPrep.Busy) return true;
                if (off == OffPrep.Blocked) return false;     // fight on with what's in hand
                // A launcher for another ammo type: the wielded ammo goes into the pack first.
                if (desiredMode == CombatMode.Missile)
                {
                    var prep = PrepareAmmoFor(weaponId);
                    if (prep == AmmoPrep.Busy) return true;
                    if (prep == AmmoPrep.Blocked) return false;   // fight on with the launcher in hand
                }
                if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-weapon-swap")) return false;
                SendWield(weaponId, inHand, "swap");
                return true;
            default: return false;   // Hold / InHand: fight with what's in hand
        }
    }

    /// <summary>
    /// No weapon to wield: fight unarmed. Any combat mode already set is kept (as before).
    /// From peace mode, ask for Melee (with the stance backoff) and hold the attack until it
    /// lands: ACE drops a melee attack sent outside Melee mode, and the no-damage blacklist
    /// then sidelined every target one after another.
    /// </summary>
    private bool EnsureUnarmedStance()
    {
        if (CurrentCombatMode != CombatMode.NonCombat)
        {
            ResetStanceFlipBackoff();
            return true;
        }
        if ((RynthClock.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs()
            && _host.ChangeCombatModeFor(CombatMode.Melee, "Combat", "stance"))
        {
            lastStanceAttempt = RynthClock.Now;
            NoteStanceFlipSent();
        }
        return false;
    }

    // Returns true when the correct weapon is wielded and combat mode matches — safe to attack.
    // Returns false when a weapon swap or stance change is in progress — caller should skip this tick.
    private bool EquipWeaponAndSetStance(WorldObject target, string monsterWeakness = "Auto")
    {
        if (target == null) return true;

        int targetWeaponId = 0;

        // Same rule the spell/debuff path uses (expression + meta-state aware). A raw name
        // substring match made a blank-named expression rule (IndexOf("") == 0) match every
        // monster and drive weapon choice for all of them.
        var rule = GetRuleForTarget(target);

        string desired = monsterWeakness;
        targetWeaponId = ChooseWeapon(target, rule, ref desired, out string weaponSource);

        if (targetWeaponId == 0 && CanAttackWithMagic)
        {
            targetWeaponId = FindWandInItems();
            if (targetWeaponId != 0) weaponSource = "FindWandInItems";
        }
        if (targetWeaponId == 0)
        {
            if ((RynthClock.Now - _lastEquipDiagAt).TotalSeconds > 5)
            {
                _lastEquipDiagAt = RynthClock.Now;
                _host.Log($"[EquipDiag] no weapon found (source=none, desired='{desired}', ItemRules={_settings.ItemRules.Count}, MonsterRule='{rule?.Name ?? "null"}', {CanMagicDiag()}) - proceeding unarmed");
            }
            return EnsureUnarmedStance();
        }

        var weaponObj = _worldFilter[targetWeaponId];
        if (weaponObj == null)
        {
            if ((RynthClock.Now - _lastEquipDiagAt).TotalSeconds > 5)
            {
                _lastEquipDiagAt = RynthClock.Now;
                _host.Log($"[EquipDiag] weapon 0x{targetWeaponId:X8} not in WorldFilter (source={weaponSource}) — proceeding unarmed");
            }
            return EnsureUnarmedStance();
        }

        // Fast Weapon Swap: a hands swap is under way (weapons back after heals, or this one): it goes on
        // with this fight's weapons, or ends for a wand. True = wait for it.
        if (_settings.FastWeaponSwap && _rearmSwap.Active && FightJoinsHandsSwap(target, rule, targetWeaponId, weaponObj))
        {
            LastCombatSkipReason = "hand-swap";
            return false;
        }

        // Another weapon in hand: one swap at a time, capped, no flip-flop. When the tracker
        // says hold (the chosen one failed its tries, or it is the weapon just swapped away
        // from), carry on with the one in hand.
        // With nothing readable in hand, a held swap sends no wield (the stance logic below
        // still runs, so a mage can cast bare-handed).
        int inHand = InHandWeaponId();
        bool allowWield = true;
        _curOffhandMode = OffhandChoiceFor(target, rule).Mode;
        // The player set this weapon for this monster (R-20261107-141028-b33e): the swap tracker's
        // window cap lets it in for a new target. Only while it is still the plan's weapon (not a
        // dual-wield stand-in or the wand from FindWandInItems).
        var ownPlan = PlanFor(target, rule);
        bool ForThisMonster(int id) => ownPlan.ForThisMonster && ownPlan.WeaponId == id;

        // Dual wield: the chosen weapon already swings in the off hand, beside another listed
        // one-handed weapon in the main hand. Fight with both rather than moving it across.
        if (inHand != 0 && inHand != targetWeaponId
            && (WieldedSlotOf(weaponObj) & OffhandRules.ShieldSlot) != 0   // the slot can read 0: WieldedSlotOf asks the weenie
            && _worldFilter[inHand] is WorldObject mainObj && MainKindOf(mainObj) == MainHandKind.OneHandedMelee
            && _settings.ItemRules.Any(r => r.Id == inHand)
            && DualWieldPartnerFor(targetWeaponId, inHand) is not WorldObject)
        {
            if (_offNoted.Add($"dual:{targetWeaponId}:{inHand}"))
                _host.Log($"[EquipDiag] 0x{(uint)targetWeaponId:X8} '{weaponObj.Name}' is in the off hand beside 0x{(uint)inHand:X8} '{mainObj.Name}' — fighting with both (dual wield)");
            weaponSource = $"dual wield (wanted 0x{(uint)targetWeaponId:X8} is in the off hand)";
            targetWeaponId = inHand;
            weaponObj = mainObj;
        }
        // Dual wield, the chosen weapon in the off hand beside one of another element, its partner
        // listed: the partner goes in the main hand and the pair fights together (the off hand stays).
        else if (inHand != 0 && inHand != targetWeaponId
            && (WieldedSlotOf(weaponObj) & OffhandRules.ShieldSlot) != 0
            && DualWieldPartnerFor(targetWeaponId, inHand) is WorldObject partnerObj)
        {
            if (_offNoted.Add($"pair:{targetWeaponId}:{partnerObj.Id}"))
                _host.Log($"[EquipDiag] 0x{(uint)targetWeaponId:X8} '{weaponObj.Name}' is in the off hand: its partner 0x{(uint)partnerObj.Id:X8} '{partnerObj.Name}' goes in the main hand (dual wield pair)");
            weaponSource = $"dual wield pair (wanted 0x{(uint)targetWeaponId:X8} is in the off hand)";
            targetWeaponId = partnerObj.Id;
            weaponObj = partnerObj;
        }

        if (inHand != targetWeaponId && OffhandBlocksMain(targetWeaponId, _curOffhandMode))
        {
            // The off hand can't come off (Offhand None, or resting after failed tries) and the
            // server refuses this weapon with it: keep what's in hand, as for a held swap.
            if (inHand != 0 && _worldFilter[inHand] is WorldObject keepObj)
            {
                weaponSource = $"in hand (off hand stays); wanted 0x{(uint)targetWeaponId:X8}";
                targetWeaponId = inHand;
                weaponObj = keepObj;
            }
            else allowWield = false;
        }
        else if (inHand != targetWeaponId)
        {
            var step = SwapStepFor(targetWeaponId, inHand, ForThisMonster(targetWeaponId), weaponSource);
            if (step == WeaponSwapTracker.Step.Wait) { if (inHand != 0) _equippedWeaponId = inHand; return false; }
            if (step == WeaponSwapTracker.Step.Swap && inHand != 0
                && SwapDeferReason(target, targetWeaponId, inHand, out bool settling) is string defer)
            {
                LogSwapDefer(targetWeaponId, inHand, defer);
                if (settling)
                {
                    _equippedWeaponId = inHand;
                    LastCombatSkipReason = "swap-settle";
                    return false;
                }
                if (_worldFilter[inHand] is WorldObject deferKeep)
                {
                    weaponSource = $"in hand ({defer}); wanted 0x{(uint)targetWeaponId:X8}";
                    targetWeaponId = inHand;
                    weaponObj = deferKeep;
                }
            }
            if (step == WeaponSwapTracker.Step.Hold)
            {
                if (inHand != 0 && _worldFilter[inHand] is WorldObject handObj)
                {
                    weaponSource = $"in hand ({SwapTracker.HoldReason}); wanted 0x{(uint)targetWeaponId:X8}";
                    targetWeaponId = inHand;
                    weaponObj = handObj;
                }
                else allowWield = false;
            }
        }

        // Remember the weapon in hand (now confirmed in-world) so melee/missile damage
        // (0x01B1) learns under the real weapon, not a resolved-but-unwielded id.
        _equippedWeaponId = inHand != 0 ? inHand : targetWeaponId;

        // Use IsWandObject (ObjectClass + name fallback) so wands with
        // ObjectClass=Unknown (stale WorldFilter classification) still get
        // Magic mode instead of falling through to the Melee default.
        int desiredMode = IsWandObject(weaponObj)                                  ? CombatMode.Magic
                        : weaponObj.ObjectClass == AcObjectClass.MissileWeapon ? CombatMode.Missile
                        : CombatMode.Melee;
        _chosenWeaponMode = desiredMode;

        // Fast Weapon Swap: a caster in hand and a melee weapon or launcher wanted (a monster came while
        // healing): peace, the wand out, the weapons on (one frame when the server is quiet), then the
        // fight's stance. The client's own use took 1.4-1.7 s and paid a forced stance change.
        // Another caster wanted (2026-11-11, WandSwapRules): the same, then Magic. The client's own use
        // went Magic, peace, bare hands, peace, Magic.
        if (WandSwapRules.UseHandsSwap(_settings.FastWeaponSwap, allowWield, inHand, targetWeaponId, WeaponKind(inHand))
            && TryStartFightHandsSwap(target, rule, targetWeaponId, desiredMode))
        {
            LastCombatSkipReason = "hand-swap";
            return false;
        }

        bool diagNow = (RynthClock.Now - _lastEquipDiagAt).TotalSeconds > 5;

        // Use CurrentWieldedLocation (stype=10) — has an InqInt fallback that works even
        // when the phys-obj offset probe hasn't fired yet (unlike TryGetObjectWielderInfo).
        bool? trackedWield = EquippedState.IsWielded(_host, targetWeaponId);   // the server's word, when the engine has it
        bool alreadyWielded = trackedWield ?? weaponObj.Values(LongValueKey.CurrentWieldedLocation, 0) > 0;

        // Secondary check — BuffManager.EnsureMagicMode has always had this and this
        // path never did, which is the whole reason buffing could flip the stance and
        // combat could not. CurrentWieldedLocation reads 0 for a weapon that IS wielded
        // whenever the cached property hasn't been refreshed, and a mage's wand is
        // permanently in hand, so the primary read is exactly the one that goes stale.
        // Falling through to the not-wielded branch on a wielded wand sends UseObject
        // (a no-op MOVE) forever and never sends the stance flip that was the only
        // thing actually missing. Confirmed live 2026-09-02: entering Magic mode by
        // hand immediately unstuck combat.
        if (trackedWield == null && !alreadyWielded && _host.HasGetObjectWielderInfo)
        {
            uint pid = _host.GetPlayerId();
            if (pid != 0 && _host.TryGetObjectWielderInfo((uint)targetWeaponId, out uint wielder, out _)
                && wielder == pid
                && !InPackNow(targetWeaponId))   // the record can still name the player after the weapon went into a pack
            {
                alreadyWielded = true;
                // Must re-arm the throttle, as every other [EquipDiag] site does.
                // diagNow is computed from _lastEquipDiagAt; logging without
                // updating it leaves the gate permanently open on any path that
                // returns before reaching one of those sites — this logged at the
                // full 30Hz tick rate, 26,410 lines in one rotation (~70% of the
                // log), which churned the log every ~30min and destroyed
                // diagnostic history that was needed to investigate other issues.
                if (diagNow)
                {
                    _lastEquipDiagAt = RynthClock.Now;
                    _host.Log($"[EquipDiag] wielded via wielder-info fallback (CurrentWieldedLocation read 0) 0x{targetWeaponId:X8} '{weaponObj.Name}'");
                }
            }
        }

        if (alreadyWielded)
        {
            // A launcher in hand: ammo it can shoot goes on (after a swap, or whenever the slot holds none
            // it can shoot), one in flight, confirmed, capped. Don't enter missile mode without it: AC
            // rejects it and cycles stance, and a shot would be a dry fire.
            if (desiredMode == CombatMode.Missile
                && (AmmoFollowUp(targetWeaponId, target, rule, out bool canShoot) || !canShoot))
            {
                // Waiting on ammo is not a stall. The attack block's stamp is never reached
                // from here (Think returns on false), so the stall watchdog's no-ammo
                // exemption never applied and it ran its whole ladder on an empty quiver.
                _lastMissileNoAmmoAt = RynthClock.Now;
                LastCombatSkipReason = "no-ammo";   // D4 record-only
                return false;
            }

            // The wand read as wielded, so whatever UseObject we had in flight landed.
            // Clear the gate here (not only on reaching the stance) so a slow stance
            // flip doesn't leave a stale pending-wield behind it.
            ResetCombatWandWieldGate();
            SwapTracker.Confirmed(targetWeaponId, RynthClock.Now);

            if (CurrentCombatMode == desiredMode)
            {
                ResetStanceRecovery(); // reached the stance — clear the whole episode
                // The main weapon is in hand: now the off hand (a shield, or dual wield).
                if (OffhandFollowUp(target, rule, targetWeaponId)) return false;
                return true;
            }

            // Deadlock recovery: AC is refusing the stance change despite the wand
            // reading as wielded. After a stuck window, STOP trusting the stale wield
            // read — flush any jammed command interpreter (no m_cBusy pin → the
            // watchdog won't) and RE-EQUIP the wand (UseObject resyncs the wield; on a
            // truly-unwielded wand it wields it, which is the actual fix). Without this
            // the bot sat in NonCombat for 5h, buffs expired, never fighting (2026-06-10).
            // The clock starts when a stance request has actually gone out (below), never on refusals
            // (see BuffManager's _buffStanceStuckSince): a clock counting through them ends in a re-equip.
            // A refusal newer than the clock's start: the request that started it was dropped (a teleport), so restart the clock.
            if (_stanceStuckSince != DateTime.MinValue && ActionPacer.LastEngineRefusalMs > _stanceStuckStartMs)
            {
                _stanceStuckSince = RynthClock.Now;
                _stanceStuckStartMs = ActionPacer.NowMs();
            }
            double stuckMs = _stanceStuckSince == DateTime.MinValue ? 0 : (RynthClock.Now - _stanceStuckSince).TotalMilliseconds;
            // RE-EQUIP recovery — only for the first couple of attempts in an
            // episode (handles a stale "wielded reads true" on a wand that
            // actually isn't wielded). Capped: UseObject on a genuinely wielded
            // wand is a MOVE to AC, and unbounded re-equip jams the item queue.
            if (_stanceReEquipAttempts < StanceReEquipMaxAttempts
                && stuckMs > StanceStuckRecoverMs
                && (RynthClock.Now - _lastStanceRecoverAt).TotalMilliseconds > StanceStuckRecoverMs
                // Recovery equips like any other path — must hold the swap gate or
                // it can collide with a buff wand-equip inside the ±3s window.
                // On refusal the && short-circuits: fall through to the throttled
                // ChangeCombatMode below and retry next tick.
                && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("stance-recovery")))
            {
                _lastStanceRecoverAt = RynthClock.Now;
                _stanceReEquipAttempts++;
                _host.Log($"[EquipDiag] STANCE STUCK {stuckMs:0}ms mode={CurrentCombatMode}≠{desiredMode} (wielded reads true) — re-equip attempt {_stanceReEquipAttempts}/{StanceReEquipMaxAttempts} 0x{targetWeaponId:X8}");
                _host.UseFor((uint)targetWeaponId, "Combat", "stance stuck: equip again");
                _lastEquipTime = RynthClock.Now;
                lastStanceAttempt = DateTime.MinValue; // let ChangeCombatMode re-fire next tick
                return false;
            }
            // Past the re-equip cap: the wand really is wielded and AC just
            // won't flip the mode. Do NOT keep UseObject-ing (that's what jams
            // the item queue) — fall through to mode-change-only, and warn once
            // so a genuinely wedged stance is visible instead of silent.
            if (_stanceReEquipAttempts >= StanceReEquipMaxAttempts && !_stanceWedgeWarned
                && stuckMs > StanceStuckRecoverMs * 3)
            {
                _stanceWedgeWarned = true;
                _host.WriteToChat($"[RynthAi] Stance wedged: wand wielded but AC won't enter {(desiredMode == CombatMode.Magic ? "Magic" : "combat")} mode after {stuckMs / 1000:0}s. Re-equip stopped (was jamming item actions). Try /ra clearbusy, or relog if it persists.", 2);
            }

            if ((RynthClock.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs()
                && _host.ChangeCombatModeFor(desiredMode, "Combat", "stance"))
            {
                if (_stanceStuckSince == DateTime.MinValue) { _stanceStuckSince = RynthClock.Now; _stanceStuckStartMs = ActionPacer.NowMs(); }
                if (diagNow) { _lastEquipDiagAt = RynthClock.Now; _host.Log($"[EquipDiag] wielded=true mode={CurrentCombatMode}→{desiredMode} (weapon=0x{targetWeaponId:X8} '{weaponObj.Name}' src={weaponSource}) — sending ChangeCombatMode"); }
                lastStanceAttempt = RynthClock.Now;
                NoteStanceFlipSent();
            }
            else if (diagNow)
            {
                _lastEquipDiagAt = RynthClock.Now;
                _host.Log($"[EquipDiag] wielded=true mode={CurrentCombatMode}≠{desiredMode} throttled (weapon=0x{targetWeaponId:X8} '{weaponObj.Name}') — waiting for mode change");
            }
            return false;
        }

        // Wield-location probe hasn't confirmed this item yet. Two cases:
        //
        // A) Already in the correct combat mode — AC enforces "weapon wielded ↔ mode matches",
        //    so trust it. Calling UseObject on an already-wielded wand is a no-op in AC
        //    (it opens the wand's properties), so we must NOT call it here.
        if (CurrentCombatMode == desiredMode)
        {
            _stanceStuckSince = DateTime.MinValue; ResetWandSwapState(); ResetStanceFlipBackoff(); // reached the stance — clear stuck timer + swap + flip backoff
            ResetCombatWandWieldGate();
            // The matching mode can come from ANOTHER weapon of the same kind: the weakness
            // pick and the Damage tab choose between weapons of one kind, and trusting the
            // mode here meant the chosen one was never put in hand. That covered melee only;
            // a wand in hand in Magic mode was kept whatever the list said (2026-09-30).
            if (SwapToChosenSameKind(targetWeaponId, desiredMode, ForThisMonster(targetWeaponId), weaponSource))
                return false;
            // The chosen weapon read in hand (the wield-location read was only late): a launcher's ammo,
            // then the off hand.
            if (InHandWeaponId() == targetWeaponId)
            {
                if (desiredMode == CombatMode.Missile
                    && (AmmoFollowUp(targetWeaponId, target, rule, out bool shootable) || !shootable))
                {
                    _lastMissileNoAmmoAt = RynthClock.Now;
                    LastCombatSkipReason = "no-ammo";
                    return false;
                }
                if (OffhandFollowUp(target, rule, targetWeaponId)) return false;
            }
            return true;
        }

        // B-wand) Held-slot wand, not yet wielded. The wand CANNOT wield while a
        //    melee/missile weapon occupies the main hand — stock ACE's CheckWeaponCollision
        //    refuses it, AND the server DENIES ChangeCombatMode(Magic) while the bow is
        //    wielded ("GetEquippedWand()==null"), which is the Missile↔NonCombat↔Magic flap.
        //    So: tear down the in-flight attack, stow the bow into an open pack FIRST, then
        //    UseObject(wand). Do NOT request Magic here — the alreadyWielded branch above
        //    flips the stance once CurrentWieldedLocation confirms the wand. Mirrors the
        //    proven BuffManager.EnsureMagicMode dequip-first path (the missing 4th site).
        if (desiredMode == CombatMode.Magic)
        {
            // The wand is resting after failed wields and the hands are empty: ask for Magic
            // mode bare-handed (throttled) and send no wield.
            if (!allowWield)
            {
                if ((RynthClock.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs()
                    && _host.ChangeCombatModeFor(desiredMode, "Combat", "stance"))
                {
                    lastStanceAttempt = RynthClock.Now;
                    NoteStanceFlipSent();
                }
                return false;
            }

            // Claim the shared swap slot FIRST so a concurrent buff/combat equip can't
            // collide; only then tear down / move items (don't cancel an attack then fail
            // to claim the slot). The 3s gate interval paces the dequip→wield steps.
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-wand-swap"))
                return false;

            if (!_combatSwapTeardownDone
                && (CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile))
            {
                if (_host.HasCancelAttack)   _host.CancelAttack();
                if (_host.HasStopCompletely) _host.StopCompletelyBy("Combat");
                _combatSwapTeardownDone = true;
                _host.Log($"[EquipDiag] CancelAttack+StopCompletely before wand equip (mode was {CurrentCombatMode})");
            }

            // Clear the main hand. Yields (false) while a dequip is in flight or blocked;
            // returns true only once the bow is confirmed out of the main hand.
            if (!EnsureHandClearForWand(targetWeaponId, diagNow))
                return false;

            // Main hand is clear (no bow), so the reason this branch withheld the
            // stance request — "the server DENIES ChangeCombatMode(Magic) while the bow
            // is wielded" — no longer applies. Send it. If the wand is in fact already
            // wielded and only the cached wield-location read is stale, this flip IS the
            // entire fix and no amount of UseObject would ever have produced it. Costs
            // nothing when the wand genuinely isn't wielded: ACE just denies it, and the
            // gated UseObject below still does the wielding. Mirrors the non-wand branch,
            // which has always sent both.
            // A weapon still in hand: the wand's use swaps it and the server then puts us in Magic.
            if (FindWieldedNonWandWeapon(targetWeaponId) == 0
                && (RynthClock.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs()
                && _host.ChangeCombatModeFor(desiredMode, "Combat", "stance"))
            {
                if (diagNow) _host.Log($"[EquipDiag] hand clear — ChangeCombatMode({desiredMode}) alongside wand equip (mode={CurrentCombatMode})");
                lastStanceAttempt = RynthClock.Now;
                NoteStanceFlipSent();
            }

            // Main hand is clear — wield the wand through the wield gate. Stance flip
            // happens next tick in the alreadyWielded branch once the wand reads as
            // wielded. Do NOT re-send UseObject on a fixed 2s tick: each send is an
            // item MOVE to AC, and a continuous stream of them keeps the item-action
            // queue jammed so the wield never resolves. Issue one, wait for it,
            // cool down, cap.
            DateTime wnow = RynthClock.Now;

            if (wnow < _wandWieldCooldownUntil)
                return false;

            if (_wandPendingWieldId != 0)
            {
                if ((wnow - _wandPendingWieldAt).TotalMilliseconds < WandWieldResolveTimeoutMs)
                    return false; // still resolving — give the server time, send nothing

                _wandWieldFailCount++;
                _host.Log($"[EquipDiag] wand UseObject(0x{_wandPendingWieldId:X8}) not confirmed in {WandWieldResolveTimeoutMs:0}ms — " +
                          $"cooling down {WandWieldCooldownMs:0}ms (fail {_wandWieldFailCount}/{WandWieldFailMax})");
                _wandPendingWieldId     = 0;
                _wandWieldCooldownUntil = wnow.AddMilliseconds(WandWieldCooldownMs);

                // Past the cap the wand provably won't wield right now. Say so once,
                // loudly, instead of standing in peace mode issuing item actions
                // forever — that silence is what made this take an hour to spot.
                if (_wandWieldFailCount >= WandWieldFailMax && !_wandWieldWedgeWarned)
                {
                    _wandWieldWedgeWarned = true;
                    _host.Log($"[EquipDiag] WAND WIELD WEDGED: 0x{targetWeaponId:X8} '{weaponObj.Name}' would not wield after {WandWieldFailMax} attempts " +
                              $"(mode={CurrentCombatMode}, busy={BusyCount}) — combat cannot enter Magic. Retrying on the cooldown cadence.");
                    _host.WriteToChat($"[RynthAi] Can't wield '{weaponObj.Name}' — combat is stuck out of Magic mode. Check the wand is reachable (not in a closed pack) or re-equip it manually.", 2);
                }
                return false;
            }

            if (diagNow) { _lastEquipDiagAt = RynthClock.Now; _host.Log($"[EquipDiag] hand clear — UseObject(0x{targetWeaponId:X8} '{weaponObj.Name}') wand equip"); }
            _host.UseFor((uint)targetWeaponId, "Combat", "equip the wand");
            MarkWieldSent(targetWeaponId, inHand);
            _wandPendingWieldId = targetWeaponId;
            _wandPendingWieldAt = wnow;
            _lastEquipTime      = wnow;
            return false;
        }

        // B) Non-wand (melee/missile) weapon not yet confirmed wielded. Either it genuinely
        //    isn't wielded, or CurrentCombatMode is stale (e.g. hot-reload didn't re-fire
        //    OnCombatModeChange). Request both a mode change and an equip — these go in the
        //    main hand, which AC swaps in place (no held-slot collision). ChangeCombatMode
        //    succeeds if already wielded (fixes hot-reload next tick); UseObject equips it if not.
        //    The wield goes first (2026-10-06): both went out within 3 ms (07:13:16.446/.453), a
        //    mode change on top of an unanswered action. With the pacing on, the mode change waits
        //    for the wield's answer (and once the weapon reads wielded, the wielded branch above
        //    sends it); off, both still go in this tick, as before.
        if (allowWield && (RynthClock.Now - _lastEquipTime).TotalMilliseconds > 2000
            && ClearOffhandFor(targetWeaponId, _curOffhandMode) == OffPrep.Ready   // a shield off before a two-hander / launcher
            && (desiredMode != CombatMode.Missile || PrepareAmmoFor(targetWeaponId) == AmmoPrep.Ready)
            && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("combat-equip")))
        {
            if (diagNow) { _lastEquipDiagAt = RynthClock.Now; _host.Log($"[EquipDiag] wielded=FALSE UseObject(0x{targetWeaponId:X8} '{weaponObj.Name}') — equip attempt"); }
            _host.UseFor((uint)targetWeaponId, "Combat", "equip the weapon");
            MarkWieldSent(targetWeaponId, inHand);
            _lastEquipTime = RynthClock.Now;
        }
        if ((RynthClock.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs())
        {
            // A launcher swap: no Missile request until the launcher and its ammo are on (the
            // wielded branch above sends it then). See LauncherSwapHoldsStance.
            if (desiredMode == CombatMode.Missile && LauncherSwapHoldsStance(targetWeaponId))
            {
                if (diagNow) { _lastEquipDiagAt = RynthClock.Now; _host.Log($"[EquipDiag] stance held: 0x{targetWeaponId:X8} '{weaponObj.Name}' or its ammo isn't on yet (mode={CurrentCombatMode})"); }
            }
            else if (_host.ChangeCombatModeFor(desiredMode, "Combat", "stance"))
            {
                if (diagNow) { _lastEquipDiagAt = RynthClock.Now; _host.Log($"[EquipDiag] wielded=FALSE mode={CurrentCombatMode}→{desiredMode} (weapon=0x{targetWeaponId:X8} '{weaponObj.Name}' src={weaponSource} class={weaponObj.ObjectClass}) — ChangeCombatMode"); }
                lastStanceAttempt = RynthClock.Now;
                NoteStanceFlipSent();
            }
        }
        return false;
    }

    // Stow the wielded non-wand weapon (the bow) blocking the Held-slot wand into a
    // capacity-verified open pack (AutoCram-pattern, AV-safe), fully bounded. Returns
    // true only when the main hand is confirmed clear (safe to wield the wand); false
    // while a dequip is resolving, blocked, or just issued — caller yields this tick.
    private bool EnsureHandClearForWand(int wandId, bool diagNow)
    {
        int bowId = FindWieldedNonWandWeapon(wandId);
        DateTime now = RynthClock.Now;
        if (bowId != 0 && !_combatDirectWandSwapFailed && IsWieldedLive(bowId))
        {
            if (_combatDirectSwapSince == DateTime.MinValue) _combatDirectSwapSince = now;
            if ((now - _combatDirectSwapSince).TotalMilliseconds <= DirectWandSwapFallbackMs)
                return true;   // the wand's use swaps
            _combatDirectWandSwapFailed = true;
            _host.Log($"[EquipDiag] using the wand didn't put 0x{(uint)bowId:X8} away in {DirectWandSwapFallbackMs:0} ms; from now on the weapon goes into a pack first");
        }
        _combatDirectSwapSince = DateTime.MinValue;
        if (bowId == 0)
        {
            _combatBowDequipPendingId = 0; _combatBowDequipAttempts = 0;
            // The main hand is clear; a caster also needs an empty off hand (ACE refuses a
            // Held-slot caster beside a shield), so the shield goes into the pack too.
            // Both callers claim the swap gate before coming here.
            return ClearOffhandFor(wandId, _curOffhandMode, gateHeld: true) == OffPrep.Ready;
        }

        if (_combatBowDequipPendingId == bowId
            && (now - _combatBowDequipAt).TotalMilliseconds < WandSwapWieldResolveMs
            && IsWieldedLive(bowId))
            return false; // dequip still resolving

        if (IsWieldedLive(bowId))
        {
            int openPack = OpenPackOverride?.Invoke() ?? WorldObjectCache.FindPackFor(_host, _worldFilter, includeMainPack: true, requireFree: 1);
            if (openPack == 0 || _combatBowDequipAttempts >= CombatBowDequipMaxAttempts)
            {
                // No verified-open pack to receive the bow, or repeated failures — the swap
                // can't complete now. Reset the attempt counter and yield; combat keeps
                // running with the bow (missile) until a slot frees (e.g. after looting).
                if (diagNow)
                {
                    _lastEquipDiagAt = now;
                    _host.Log($"[EquipDiag] bow 0x{(uint)bowId:X8} dequip blocked (openPack=0x{(uint)openPack:X8}, attempts={_combatBowDequipAttempts}/{CombatBowDequipMaxAttempts}) — cannot swap to wand yet");
                }
                if (openPack == 0)
                    WorldObjectCache.WarnPackFull(_host, "can't put the weapon away to switch to the wand for combat spells");
                _combatBowDequipPendingId = 0;
                _combatBowDequipAttempts = 0;
                return false;
            }
            _combatBowDequipAttempts++;
            _host.MoveItemInternal((uint)bowId, (uint)openPack, 0, 1); // amount>=1 (engine rejects 0)
            _weaponSwapGate?.NoteMove(bowId, wieldedNow: true);
            _combatBowDequipPendingId = bowId;
            _combatBowDequipAt = now;
            _host.Log($"[EquipDiag] dequip bow 0x{(uint)bowId:X8} -> pack 0x{(uint)openPack:X8} (attempt {_combatBowDequipAttempts}/{CombatBowDequipMaxAttempts}) before wand equip");
            return false; // yield until the bow is out of hand
        }

        _combatBowDequipPendingId = 0;
        _combatBowDequipAttempts = 0;
        return true; // bow confirmed unwielded — main hand clear
    }

    // The currently-wielded non-wand weapon (the bow) that blocks the Held-slot wand.
    private int FindWieldedNonWandWeapon(int wandId)
    {
        foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: true))
        {
            if (wo.Id == wandId) continue;
            if (IsWandObject(wo)) continue;
            if (WorldObjectCache.IsAmmo(wo)) continue;   // arrows stay on; only the bow blocks the wand
            if ((wo.ObjectClass == AcObjectClass.MeleeWeapon
                 || wo.ObjectClass == AcObjectClass.MissileWeapon)
                && WorldObjectCache.IsWieldedByPlayer(_host, wo))
                return wo.Id;
        }
        return 0;
    }

    // Live (forceRefresh) wielded check — never trusts a stale cache snapshot.
    private bool IsWieldedLive(int id)
    {
        if (id == 0) return false;
        if (EquippedState.IsWielded(_host, id) is bool tracked) return tracked;
        foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: true))
            if (wo.Id == id)
                return WorldObjectCache.IsWieldedByPlayer(_host, wo);
        if (_host.HasGetObjectWielderInfo)
        {
            uint pid = _host.GetPlayerId();
            if (pid != 0 && _host.TryGetObjectWielderInfo((uint)id, out uint wielder, out _))
                return wielder == pid && !InPackNow(id);
        }
        return false;
    }

    /// <summary>
    /// Ensure a wand is wielded and the player is in Magic combat mode.
    /// Used by NavigationEngine to prepare for recall casts. Returns true only when
    /// ready to cast; when false, caller should re-tick (equip/stance are throttled
    /// internally so calling every tick is safe).
    /// </summary>
    // Nav calls EnsureMagicReady every tick while a Recall waypoint is firing. Until
    // this lapses, the idle peace-mode switch leaves the stance alone: it used to send
    // NonCombat on the tick Magic landed, right before nav's recall cast.
    private DateTime _navMagicWantedUntil = DateTime.MinValue;

    public bool EnsureMagicReady()
    {
        _navMagicWantedUntil = RynthClock.Now.AddMilliseconds(2000);
        int wandId = FindWandInItems();
        // No target here: the global off-hand setting. With Offhand None and a shield on, the
        // wand can't be wielded (the server refuses a caster beside it): cast bare-handed.
        _curOffhandMode = OffhandRules.Parse(_settings.OffhandDefault, OffhandMode.Auto);
        if (wandId != 0 && _worldFilter[wandId] is WorldObject w0 && !WorldObjectCache.IsWieldedByPlayer(_host, w0)
            && OffhandBlocksMain(wandId, _curOffhandMode))
            wandId = 0;
        if (wandId == 0)
        {
            // No caster anywhere: recalls cast bare-handed. This used to return false
            // for good, even in Magic mode, so a Recall waypoint never cast and sat
            // out the whole portal timeout.
            if (CurrentCombatMode == CombatMode.Magic) { ResetStanceFlipBackoff(); return true; }
            if ((RynthClock.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs()
                && _host.ChangeCombatModeFor(CombatMode.Magic, "Combat", "stance"))
            {
                lastStanceAttempt = RynthClock.Now;
                NoteStanceFlipSent();
            }
            return false;
        }

        var wand = _worldFilter[wandId];
        if (wand == null) return false;

        // Location, else the live wielder id: a wand in hand whose location read 0 was
        // used again and again (equip/unequip) and never got to magic mode (2026-09-29).
        bool alreadyWielded = WorldObjectCache.IsWieldedByPlayer(_host, wand);

        if (alreadyWielded)
        {
            if (CurrentCombatMode == CombatMode.Magic) { ResetWandSwapState(); ResetStanceFlipBackoff(); return true; }
            if ((RynthClock.Now - lastStanceAttempt).TotalMilliseconds > StanceRetryDelayMs()
                && _host.ChangeCombatModeFor(CombatMode.Magic, "Combat", "stance"))
            {
                lastStanceAttempt = RynthClock.Now;
                NoteStanceFlipSent();
            }
            return false;
        }

        if (CurrentCombatMode == CombatMode.Magic) { ResetWandSwapState(); ResetStanceFlipBackoff(); return true; }

        // Wand not wielded. Dequip the blocking bow FIRST (stock ACE won't auto-dequip a
        // main-hand weapon for the Held-slot wand, and denies ChangeCombatMode(Magic) while
        // it's wielded). Do NOT request Magic until the wand is wielded — the alreadyWielded
        // branch above handles the stance flip. Mirrors EquipWeaponAndSetStance / BuffManager.
        if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-magicready-wand"))
            return false;
        if (!_combatSwapTeardownDone
            && (CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile))
        {
            if (_host.HasCancelAttack)   _host.CancelAttack();
            if (_host.HasStopCompletely) _host.StopCompletelyBy("Combat");
            _combatSwapTeardownDone = true;
        }
        if (!EnsureHandClearForWand(wandId, diagNow: true))
            return false;
        if ((RynthClock.Now - _lastEquipTime).TotalMilliseconds > 2000)
        {
            _host.UseFor((uint)wandId, "Combat", "equip the wand");
            _lastEquipTime = RynthClock.Now;
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

    /// <summary>
    /// Attack type for the scan's LOS test. Missile mode names the weapon in hand so the
    /// bow/crossbow/atlatl arc and its velocity apply (the scan used to pass "", which made
    /// every missile LOS test a straight line). Peace mode with a missile weapon picked counts
    /// as missile, so a target acquired from peace is tested with the arc it will be shot on.
    /// Melee (or peace with a melee weapon) is one chest-to-chest ray. Magic stays a straight
    /// line with the dungeon silhouette rays, as before.
    /// </summary>
    /// <summary>The line-of-sight check is skipped for melee within this (same units as Monster Range).</summary>
    internal const double MeleeIgnoresLosWithin = 4.0;

    /// <summary>
    /// This monster gets melee: the attack type says so (Melee, or peace with a melee weapon in hand),
    /// or the fight's plan picks a melee weapon (a wand still in hand from buffing, about to swap).
    /// </summary>
    private bool MeleeIsTheAttackOn(WorldObject target, TargetingFSM.AttackType attackType)
    {
        int mode = CurrentCombatMode;
        if (mode == CombatMode.Melee || attackType == TargetingFSM.AttackType.Melee) return true;
        if (mode == CombatMode.Missile) return false;
        int planned = PlanFor(target, GetRuleForTarget(target)).WeaponId;
        return planned != 0 && _worldFilter[planned]?.ObjectClass == AcObjectClass.MeleeWeapon;
    }

    private TargetingFSM.AttackType DetermineAttackTypeForLOS()
    {
        if (_raycastSystem?.TargetingFSM == null) return TargetingFSM.AttackType.Linear;

        int mode = CurrentCombatMode;
        WorldObject? weapon = _equippedWeaponId != 0 ? _worldFilter[_equippedWeaponId] : null;
        bool missileWeapon = weapon != null && weapon.ObjectClass == AcObjectClass.MissileWeapon;
        if (mode == CombatMode.NonCombat && missileWeapon)
            mode = CombatMode.Missile;
        // Same for melee: a target picked from peace is judged as the melee attack will be,
        // so the verdict doesn't change when the stance does.
        else if (mode == CombatMode.NonCombat && weapon != null && weapon.ObjectClass == AcObjectClass.MeleeWeapon)
            mode = CombatMode.Melee;

        string weaponName = mode == CombatMode.Missile && missileWeapon ? weapon!.Name ?? "" : "";
        return _raycastSystem.GetAttackType(mode, weaponName);
    }

    // ── LOS / target-distance diagnostics ────────────────────────────────────
    // "Far away" can't be judged from the log without positions, so target lock, switch and
    // drop lines carry the 3D distance, its horizontal and vertical parts and both cells.
    // With LosDebugLog on, the scan also logs each target's LOS verdict (line / arc, rise,
    // hit point) when it changes and at most every LosDebugRepeatMs otherwise.
    private readonly Dictionary<int, (DateTime At, bool Blocked)> _losDebugLast = new();
    private const double LosDebugRepeatMs = 5000;
    private const double LosDebugMinGapMs = 1000;
    private int _losDebugWeaponId;

    /// <summary>"d=6.2m (h=5.9 dz=+1.8) cell=0x61450123 me=0x61450119", or why it can't be measured.</summary>
    private string DescribeTargetPos(int targetId)
    {
        uint pid = _host.GetPlayerId();
        if (pid == 0 || targetId == 0) return "pos=?";
        if (!_host.TryGetObjectPosition(pid, out uint pc, out float px, out float py, out float pz))
            return "pos=? (no player position)";
        if (!_host.TryGetObjectPosition((uint)targetId, out uint tc, out float tx, out float ty, out float tz))
            return "pos=? (no target position)";
        double dx = (((tc >> 24) & 0xFF) * 192.0 + tx) - (((pc >> 24) & 0xFF) * 192.0 + px);
        double dy = (((tc >> 16) & 0xFF) * 192.0 + ty) - (((pc >> 16) & 0xFF) * 192.0 + py);
        double dz = tz - pz;
        double h = Math.Sqrt(dx * dx + dy * dy);
        double d = Math.Sqrt(h * h + dz * dz);
        return $"d={d:0.0}m (h={h:0.0} dz={dz:+0.0;-0.0;0.0}) cell=0x{tc:X8} me=0x{pc:X8}";
    }

    private void LogLosDebug(WorldObject wo, bool blocked, in TargetingFSM.LosDetail det)
    {
        DateTime now = RynthClock.Now;
        if (_losDebugLast.TryGetValue(wo.Id, out var last))
        {
            double ms = (now - last.At).TotalMilliseconds;
            bool changed = last.Blocked != blocked;
            if (ms < LosDebugMinGapMs || (!changed && ms < LosDebugRepeatMs)) return;
        }
        if (_losDebugLast.Count > 256) _losDebugLast.Clear();
        _losDebugLast[wo.Id] = (now, blocked);

        string verdict;
        if (!det.Checked) verdict = "clear (not tested: no geometry near the path)";
        else if (det.LineBlocked) verdict = "BLOCKED line";
        else if (det.ArcChecked && det.Arc.OutOfReach) verdict = "BLOCKED arc: out of reach at this velocity";
        else if (det.ArcChecked && det.Arc.Blocked) verdict = $"BLOCKED arc: hit {det.Arc.HitAlong:0.0}m out, {det.Arc.HitZ:+0.0;-0.0;0.0}m vs launch";
        else verdict = "clear";

        string arcInfo = det.Velocity > 0
            ? $" v={det.Velocity:0.0} rise={det.Arc.Sag:0.00} apex={det.Arc.Apex:0.00} clr={_settings.MissileArcClearance:0.0}{(det.ArcChecked ? "" : " (flat: line only)")}"
            : "";
        _host.Log($"[LOS] 0x{(uint)wo.Id:X8} '{wo.Name}' {verdict} | {det.Type}{(det.Dungeon ? " dungeon" : "")}{arcInfo} | {DescribeTargetPos(wo.Id)}");
    }

    /// <summary>LOS debug: once per missile weapon, what the client knows of its launch speed.</summary>
    private void LogLosDebugWeapon()
    {
        int wid = _equippedWeaponId;
        if (wid == 0 || wid == _losDebugWeaponId) return;
        var weapon = _worldFilter[wid];
        if (weapon == null || weapon.ObjectClass != AcObjectClass.MissileWeapon) return;
        _losDebugWeaponId = wid;
        double maxVel = _worldFilter.GetDoubleProperty(wid, (uint)DoubleValueKey.MaximumVelocity, 0);
        var type = DetermineAttackTypeForLOS();
        float setting = _raycastSystem?.TargetingFSM?.VelocityFor(type) ?? 0f;
        _host.Log($"[LOS] missile weapon 0x{(uint)wid:X8} '{weapon.Name}': MaximumVelocity={(maxVel > 0 ? maxVel.ToString("0.0") : "unknown (not appraised)")}, " +
                  $"LOS uses {type} v={setting:0.0}");
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
                _host.SetMotionBy("Combat", MotionTurnRight, true);
                _host.SetMotionBy("Combat", MotionTurnLeft,  false);
            }
            else
            {
                _host.SetMotionBy("Combat", MotionTurnLeft,  true);
                _host.SetMotionBy("Combat", MotionTurnRight, false);
            }
        }
        catch { }
    }

    /// <summary>
    /// Before a wall at <paramref name="targetId"/>: true = don't cast this tick. Facing within
    /// <see cref="WallFaceToleranceDeg"/>: false, cast (no turn). Further off: snap the heading to
    /// the target and hold for <see cref="WallFaceSettleMs"/>, then cast, unless the target has
    /// since drifted more than <see cref="WallFaceDriftDeg"/> off (then snap again).
    /// </summary>
    internal bool HoldForWallFacing(int targetId)
    {
        DateTime now = WallClock();
        if (_wallFaceAt != DateTime.MinValue && _wallFaceTarget == targetId)
        {
            if ((now - _wallFaceAt).TotalMilliseconds < WallFaceSettleMs) return true;   // the server catching up
            _wallFaceAt = DateTime.MinValue;
            if (GetFacingError(targetId) <= WallDriftAllowedDeg(targetId) || _wallFaceSnaps >= WallFaceMaxSnaps)
            {
                _wallFaceSnaps = 0;
                return false;                                                              // turned and settled: cast
            }
        }
        else _wallFaceSnaps = 0;
        _wallFaceAt = DateTime.MinValue;

        if (!TryGetHeadingTo(targetId, out double desiredDeg, out double error)) return false;   // can't tell: cast as is
        if (error <= WallFaceToleranceDeg) return false;
        if (!_host.HasTurnToHeading) return false;     // FindBestShapedSpell doesn't pick a wall then
        if (!_host.TurnToHeadingBy("Combat", (float)desiredDeg)) return true;   // held by the pacer: next tick
        _wallFaceTarget = targetId;
        _wallFaceAt = now;
        _wallFaceSnaps++;
        _host.Log($"[CombatCast] wall: facing 0x{(uint)targetId:X8} first ({error:0} deg off), casting in {WallFaceSettleMs:0} ms");
        return true;
    }

    /// <summary>
    /// Missile only: snap the heading to a point-blank target the character faces away from
    /// (<see cref="PointBlankFacing"/>). <paramref name="force"/> skips the no-shot wait (the
    /// stall watchdog's re-issue already waited longer). True = a snap was just sent or is still
    /// settling, so don't attack this tick.
    /// </summary>
    private bool HoldForPointBlankFacing(double currentDist, bool force = false)
    {
        if (CurrentCombatMode != CombatMode.Missile || !_host.HasTurnToHeading) return false;
        DateTime now = RynthClock.Now;
        int tid = activeTargetId;
        if (_pointBlankFacing.Settling(now, tid)) return true;
        if (!TryGetHeadingTo(tid, out double desiredDeg, out double error)) return false;
        DateTime evidence = force ? DateTime.MinValue : _lastSwingEvidenceAt;
        if (!_pointBlankFacing.ShouldSnap(now, tid, currentDist, error, evidence)) return false;
        if (!_host.TurnToHeadingBy("Combat/PointBlank", (float)desiredDeg)) return false;   // held by the pacer: attack as is
        _pointBlankFacing.Snapped(now, tid);
        _host.Log($"[Combat] point blank: turning to 0x{(uint)tid:X8} ({error:0} deg off at {currentDist:0.0} m, " +
                  $"last shot {(_lastSwingEvidenceAt == DateTime.MinValue ? "never" : $"{(now - _lastSwingEvidenceAt).TotalSeconds:0.0}s ago")}" +
                  $"{(force ? ", stall re-issue" : "")}; snaps={_pointBlankFacing.Snaps})");
        return true;
    }

    /// <summary>The heading from the player to <paramref name="targetId"/> (0 = north, clockwise)
    /// and how far the player's facing is off it (absolute degrees).</summary>
    private bool TryGetHeadingTo(int targetId, out double desiredDeg, out double absError)
    {
        desiredDeg = 0; absError = 180.0;
        try
        {
            if (!_host.TryGetPlayerPose(out _, out float px, out float py, out _,
                    out float qw, out _, out _, out float qz))
                return false;
            if (!_host.TryGetObjectPosition((uint)targetId, out _, out float tx, out float ty, out _))
                return false;
            desiredDeg = Math.Atan2(tx - px, ty - py) * (180.0 / Math.PI);
            if (desiredDeg < 0) desiredDeg += 360.0;
            double physYawDeg = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
            double currentDeg = ((-physYawDeg) % 360.0 + 720.0) % 360.0;
            double error = desiredDeg - currentDeg;
            while (error > 180.0) error -= 360.0;
            while (error < -180.0) error += 360.0;
            absError = Math.Abs(error);
            return true;
        }
        catch { return false; }
    }

    private void ClearCombatTurnMotions()
    {
        _host.SetMotionBy("Combat", MotionTurnRight, false);
        _host.SetMotionBy("Combat", MotionTurnLeft,  false);
    }

    /// <summary>Let go of a turn the facing servo is holding. Called when combat stops
    /// ticking (macro off), which would otherwise leave the turn held for good.</summary>
    public void ReleaseHeldTurn()
    {
        if (!_facingTarget) return;
        _facingTarget = false;
        ClearCombatTurnMotions();
    }

    /// <summary>
    /// True while a previously-issued combat cast is still resolving on the ACE
    /// server. Serializes magic casts so the next cast's turn/stop motion can't
    /// orphan the prior cast's deferred windup. Cleared when a server UseDone
    /// (0x1C7) is observed since the cast (the server finished the action —
    /// completed or refused) or a hard timeout elapses. On an engine without
    /// UseDone observation (HasUseDoneSeq == false) the timeout is the sole gate.
    /// </summary>
    private bool IsAwaitingCastResolution()
    {
        if (!_awaitingCastResolution) return false;
        if (_host.HasUseDoneSeq && _host.GetUseDoneSeq() != _useDoneSeqAtCast)
        {
            _awaitingCastResolution = false;
            return false;
        }
        if (RynthClock.Now >= _castResolutionDeadline)
        {
            _awaitingCastResolution = false;
            return false;
        }
        return true;
    }

    /// <summary>True while a targeted combat cast is still resolving on the server (windup
    /// in flight). Shares the bounded IsAwaitingCastResolution state, so it self-clears on
    /// the UseDone-seq advance or the 2.5s timeout — used to gate motion emitters that would
    /// otherwise truncate the cast gesture and strand server Player.IsBusy (relog-only wedge).</summary>
    private bool IsCastInFlight() => IsAwaitingCastResolution();

    /// <summary>Record that a targeted combat cast was just issued, so the next
    /// cast waits for it to resolve on the server (see IsAwaitingCastResolution).</summary>
    private void MarkCombatCastIssued()
    {
        _attackedThisTick = true;   // [CombatLatency]: a debuff or attack cast went out
        _awaitingCastResolution = true;
        _castResolutionDeadline = RynthClock.Now.AddMilliseconds(CAST_RESOLUTION_TIMEOUT_MS);
        _useDoneSeqAtCast = _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0;
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
        // A launcher of a known ammo type in hand: only ammo it can shoot counts (2026-11-07). Arrows
        // left on with a crossbow in hand counted, and a shot went out with nothing it could fire.
        int lt = LauncherTypeOf(_worldFilter[InHandWeaponId()]);
        bool any = false;
        foreach (var item in _worldFilter.GetDirectInventory(forceRefresh: true))
        {
            if (!LooksLikeWieldedAmmo(item, playerId)) continue;
            if (lt == 0) return true;
            if (!IsAmmoItem(item))
            {
                // In the ammunition slot but nothing else says ammo: its type can't be told, so it counts
                // as before. A name match in another slot (a shield named for arrows) doesn't.
                if ((WieldedSlotOf(item) & AmmunitionSlot) != 0) any = true;
                continue;
            }
            // Not returned false at once: a stowed stack whose stale record still names the ammunition
            // slot must not hide the stack really on.
            if (MissileAmmo.CanShoot(lt, AmmoTypeOfItem(item))) return true;
        }
        return any;
    }

    // EquipMask bit for the ammunition slot. Items wielded in this slot are ammo
    // by definition — far more reliable than name or ItemType inspection because
    // some servers type their arrows as MissileWeapon (0x100) rather than the
    // MissileAmmo bit (0x400) that AC's vanilla data has.
    private const int AmmunitionSlot = 0x00800000;
    private const int MissileWeaponSlot = 0x00400000;   // bow, crossbow, atlatl — or a thrown weapon

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

        // A thrown weapon (throwing axe/club, shuriken, javelin, phial) sits in the
        // missile-weapon slot and is its own ammo. Launchers don't stack; thrown weapons do.
        // Without this, a thrown-weapon character never counted as having ammo and combat
        // held its target in the wrong mode forever.
        if ((loc & MissileWeaponSlot) != 0 && item.ObjectClass == AcObjectClass.MissileWeapon
            && item.Values(LongValueKey.MaxStackSize, 0) > 1)
            return true;

        // Name-based fallback for items in non-ammo slots that still match
        // ammo names (rare server-custom configurations). Never a launcher in hand: one named
        // for bolts or arrows is not its own ammo.
        if ((loc & MainHandSlots) != 0 && LauncherTypeOf(item) != 0) return false;
        string n = item.Name;
        if (string.IsNullOrEmpty(n)) return false;
        if (n.Contains("Bundle") || n.Contains("Wrapped")) return false;
        return n.Contains("Arrow") || n.Contains("Quarrel") || n.Contains("Bolt") || n.Contains("Dart");
    }

    /// <summary>Send one melee/missile attack at the active target. False when nothing was
    /// sent this cycle (no ammo, or the server's attack had to be released first).</summary>
    private bool AttackTarget()
    {
        try
        {
            bool isMissile = CurrentCombatMode == CombatMode.Missile;
            uint targetId  = (uint)activeTargetId;

            // Don't fire in missile mode without ammo — let crafting manager handle it
            if (isMissile && !HasWieldedAmmo())
                return false;

            // The server's attack is still bound to another mob that may be alive: ACE
            // would drop this request and keep chasing that one (see _serverAttackTargetId).
            // Release it and attack on the next cycle — sent together, the cancel of an
            // in-progress swing lands after the swing, so the attack would be dropped too.
            if (_serverAttackTargetId != 0 && _serverAttackTargetId != activeTargetId
                && !IsKnownDead(_serverAttackTargetId))
            {
                _attackRebinds++;
                _rebindThisTick = true;
                ReleaseServerAttack($"target is now 0x{targetId:X8} '{_worldFilter[activeTargetId]?.Name}' {DescribeTargetPos(activeTargetId)}", log: true);
                LastCombatSkipReason = "attack-rebind";
                return false;
            }

            // Power: the monster rule's Attack Power (High / Reckless / Variable / Reckless +
            // Variable), else the global Melee/Missile Power (AttackPowerPlanner.cs).
            int powerPct = isMissile ? _settings.MissileAttackPower : _settings.MeleeAttackPower;
            bool recklessTrained = _charSkills == null || _charSkills[AcSkillType.Recklessness].Training >= 2;
            // No rule asking for a power (every rule on Default, as saved before this existed):
            // the rule lookup is skipped, the global setting applies exactly as it always did.
            WorldObject? powerTarget = null;
            string powerMode = AttackPowerModes.Default;
            if (AnyAttackPowerRule())
            {
                powerTarget = _worldFilter[activeTargetId];
                powerMode = AttackPowerModes.Effective(GetRuleForTarget(powerTarget), DefaultMonsterRule());
            }
            float power = AttackPowerModes.Resolve(powerMode, powerPct, _settings.UseRecklessness, recklessTrained,
                _settings.VariablePowerMin, _settings.VariablePowerMax, PowerRandom);
            if (powerMode != AttackPowerModes.Default && _powerLoggedTargetId != activeTargetId)
            {
                _powerLoggedTargetId = activeTargetId;
                _host.Log($"[AttackPower] 0x{targetId:X8} '{powerTarget?.Name}': {powerMode}" +
                          (recklessTrained ? "" : " (Recklessness untrained: no reckless band)") + $", first attack at {power * 100:0}%");
            }

            // The fixed Attack Height, or the Dirty Fighting rotation's pick (CombatManager.DirtyFighting.cs).
            int uiHeight = AttackHeightForTarget(isMissile);
            int acHeight = uiHeight switch { 0 => 3, 2 => 1, _ => 2 }; // Low=3, Med=2, High=1

            // Native attack: select target, Start fills power bar, End fires the attack.
            // Called each attack cycle — the client handles turn-to-face naturally.
            // NativeForThisAttack can send one as the regular attack (the setting stays on).
            if (_settings.UseNativeAttack && _host.HasNativeAttack && NativeForThisAttack(RynthClock.Now))
            {
                // Marked as RynthAi's own pick: "Fight the monster I select" follows the player's only.
                ItemInfo.SelfSelect.SelectAsBot(_host, targetId);
                _host.NativeAttack(acHeight, power);
                _lastNativeAttackAt = RynthClock.Now;
                _nativeQuietAttacks++;
            }
            // Direct attack: raw game action (bypasses client facing)
            else if (isMissile)
                _host.MissileAttack(targetId, acHeight, power);
            else
                _host.MeleeAttack(targetId, acHeight, power);

            RynthCore.Plugin.Shared.ActionPacer.NoteAttackSent();   // nothing else starts before this swing plays
            NoteFightWeapons();
            _serverAttackTargetId = activeTargetId;
            _attacksSinceEvidence++;
            LastCombatSkipReason = "attack"; // D4 record-only: physical attack sent this tick
            return true;
        }
        catch { return false; }
    }

    // ── Native attack: reset after a stance change, fall back when it stalls ──────────
    // 2026-11-06, DreamWeave (a melee character dual-wielding two Hand Wraps): a buff-wand swap
    // came 0.5 s after a native attack (BuffPre CancelAttack+StopCompletely, Staff, Magic, casts,
    // Hand Wraps back, Melee), and from then on the character never swung again: 45 StallWatch
    // episodes in 37 minutes, native=True, "no hit/miss/kill", facing errors up to 180 degrees,
    // and the client's attacking flag (the heartbeat's atk=1) never set once. Turning Use Native
    // Attack off fought at once. The native attack is the client's own pipeline
    // (ClientCombatSystem): its attack-in-progress flag is cleared only by the server's
    // AttackDone, and while it is set the client queues every attack instead of sending it. The
    // client sets it even when it "attacks" in Peace or Magic, where it sends nothing (a power bar
    // or a queued attack finishing after the stance changed), so no AttackDone ever came: every
    // native attack after the swap was queued forever. Nothing reached the server, so it never
    // swung and never turned the character to the mob (in melee the server does the turning, for
    // both paths). The regular attack is the raw game action: the server swings and its AttackDone
    // clears the client's flag, which is what turning the setting off did. The engine now clears a
    // stuck flag itself (ClientCombatHooks, NativeAttackRearm); this side covers an engine without
    // that and anything else that leaves the client's pipeline stuck:
    //   * a stance change that came mid-fight (a native attack under NativeResetWindowMs before the
    //     stance left Melee/Missile): the first swing back goes out as the regular attack. Its
    //     AttackDone resets the client's attack state, and native carries on from the next swing;
    //   * the stall watchdog's step 1 re-issue goes out as the regular attack the same way;
    //   * native attacks with no hit, miss or kill for NativeFallbackMs (NativeFallbackAttacks or
    //     more, across targets): the regular attack for the rest of this fight (this target),
    //     native again from the next target. Logged once each way. Use Native Attack itself is
    //     never changed (only the player changes settings).

    /// <summary>True: this attack goes out native. False: as the regular attack (see above).</summary>
    private bool NativeForThisAttack(DateTime now)
    {
        if (_nativeFallbackTargetId != 0)
        {
            if (activeTargetId == _nativeFallbackTargetId) return false;
            _nativeFallbackTargetId = 0;
            _nativeQuietSince = DateTime.MinValue;
            _nativeQuietAttacks = 0;
            _host.Log($"[Combat] native attack again: a new fight (0x{activeTargetId:X8} '{_worldFilter[activeTargetId]?.Name}')");
        }

        if (_nativeResetPending)
        {
            _nativeResetPending = false;
            _host.Log($"[Combat] native attack: this swing at 0x{activeTargetId:X8} goes out as the regular attack " +
                      "(after a stance change mid-fight or a stall re-issue; the server's answer resets the client's attack state)");
            return false;
        }

        if (_nativeQuietSince == DateTime.MinValue || (now - _lastNativeAttackAt).TotalMilliseconds > NativeQuietGapMs)
        {
            _nativeQuietSince = now;
            _nativeQuietAttacks = 0;
        }
        double quietMs = (now - _nativeQuietSince).TotalMilliseconds;
        if (_nativeQuietAttacks >= NativeFallbackAttacks && quietMs >= NativeFallbackMs)
        {
            _nativeFallbackTargetId = activeTargetId;
            _host.Log($"[Combat] native attack isn't landing (no hit, miss or kill in {quietMs / 1000:0} s over {_nativeQuietAttacks} attacks); " +
                      "using the regular attack until the next fight");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Every combat tick (OnHeartbeat): a stance change out of Melee/Missile that came mid-fight
    /// (a native attack under <see cref="NativeResetWindowMs"/> before it) makes the first swing
    /// back the regular attack (see NativeForThisAttack).
    /// </summary>
    private void WatchNativeStance()
    {
        int mode = CurrentCombatMode;
        bool attackStance = mode == CombatMode.Melee || mode == CombatMode.Missile;
        if (attackStance == _nativeStanceSeen) return;
        _nativeStanceSeen = attackStance;
        if (attackStance || !_settings.UseNativeAttack || _lastNativeAttackAt == DateTime.MinValue) return;
        if ((RynthClock.Now - _lastNativeAttackAt).TotalMilliseconds < NativeResetWindowMs)
            _nativeResetPending = true;
    }

    /// <summary>Host tests: the native attack has fallen back to the regular one for this fight.</summary>
    internal bool NativeFallbackActive => _nativeFallbackTargetId != 0;
    /// <summary>Host tests: the next swing goes out as the regular attack.</summary>
    internal bool NativeResetPending => _nativeResetPending;

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

        return DefaultMonsterRule();
    }

    /// <summary>The Monsters tab's DEFAULT line (the catch-all rule), or null.</summary>
    private MonsterRule? DefaultMonsterRule() => _settings.MonsterRules.FirstOrDefault(
        m => m.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when some monster rule has an Attack Power other than Default.</summary>
    private bool AnyAttackPowerRule()
    {
        foreach (MonsterRule r in _settings.MonsterRules)
            if (AttackPowerModes.Normalize(r.AttackPower) != AttackPowerModes.Default) return true;
        return false;
    }

    /// <summary>Random source for Variable attack power, [0, 1); tests replace it.</summary>
    internal Func<double> PowerRandom { get; set; } = Random.Shared.NextDouble;
    private int _powerLoggedTargetId;

    private void AttackWithMagic(WorldObject target)
    {
        _wallFaceHeld = false;
        _manaCastHeld = false;
        _scriptHeld = false;
        _equipGateHeld = false;
        if (_spellManager == null || target == null) return;

        // Hard safety veto: never cast offensive magic at something AC's combat
        // system says is not attackable. AC NPCs/vendors are ItemType=Creature,
        // so classification (the ItemType-flag rescue path in WorldObjectCache)
        // can still promote them to "Monster" even after the engine fix cleans
        // the primary attackable-gated path. The attackable check is the game's
        // own authority for "can I fight this". The engine serves the REAL
        // ObjectIsAttackable from a main-thread snapshot to this off-thread
        // pump; if it says not-attackable, bail before any cast/select.
        if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)target.Id))
            return;

        // Keep the authoritative known-spell snapshot warm for the combat
        // resolver even when the char isn't buffing (GetDynamicSelfBuffId is
        // the only other pump). Throttled internally — cheap to call per tick.
        _spellManager.RefreshKnownSpells();

        // Combat spell selection is now purely predictive (known ∧ scarab ∧
        // skill-window tier ∧ configured shape). No empirical no-chat →
        // blacklist valve: it falsely poisoned KNOWN spells whenever a cast
        // didn't execute, which is what collapsed war to Force Bolt I and
        // re-poisoned bufftimers.txt every few seconds.

        // Don't cast while a weapon equip is still settling. _lastEquipTime is set by every wield.
        // This was a flat 3 s, and each held tick counted as an attack (the next try a whole attack
        // interval later); now the cast goes once the stance has settled after the wield, at most
        // the old 3 s (WandSwapRules.EquipCastHoldMs). Combat only gets here with the chosen wand in
        // hand and the client reading Magic.
        if (EquipCastHoldsNow())
            return;
        // Cadence is set in Think before this is called: the previous cast must have resolved
        // (UseDone, IsAwaitingCastResolution), its gesture must be over (CanCastNow), and the
        // Attack Spell Delay must have passed since the last cast at this same target (the first
        // cast at a new target skips that delay when the two server gates exist — AttackIntervalMs).

        if (_waitingForDebuffResult)
        {
            if ((RynthClock.Now - _pendingDebuffCastTime).TotalMilliseconds > DEBUFF_RESULT_TIMEOUT_MS)
            {
                NoteDebuffLanded();
                _waitingForDebuffResult = false;
            }
            else
            {
                LastCombatSkipReason = "awaiting-debuff-result"; // D4 record-only
                return;
            }
        }

        if (activeTargetId != _lastDebuffTargetId)
        {
            _debuffs.NewTarget(RynthClock.Now);
            _lastDebuffTargetId = activeTargetId;
        }

        var rule = GetRuleForTarget(target);
        string element = GetPreferredElement(target, rule);

        if (rule != null)
        {
            var pendingDebuffs = BuildDebuffList(rule, element);
            DateTime now = RynthClock.Now;
            foreach (var debuffKey in pendingDebuffs)
            {
                if (_debuffs.IsOn(activeTargetId, debuffKey, now)) continue;
                bool renewal = _debuffs.RenewAt(activeTargetId, debuffKey) != null;

                int spellId = 0;
                int castTier = 0;
                if (debuffKey.StartsWith("Vuln:"))
                    spellId = FindBestVulnSpellWithTier(debuffKey.Substring(5), out castTier);
                else if (debuffKey.StartsWith("Custom:"))
                    spellId = FindBestCustomDebuffWithTier(debuffKey.Substring(7), out castTier);
                else if (debuffKey.StartsWith("Void:"))
                    spellId = FindBestVoidDebuffWithTier(debuffKey.Substring(5), out castTier);
                else
                    spellId = FindBestDebuffSpellWithTier(debuffKey, out castTier);

                if (spellId == 0) continue;
                if (ManaHoldsCast(spellId)) return;   // mana reserve: Stamina to Mana first, or regenerate

                try
                {
                    _host.CastSpellFor((uint)activeTargetId, spellId, "Combat", SpellTableStub.GetById(spellId)?.Name ?? $"spell {spellId}");
                    MarkCombatCastIssued();
                    StartCastChatTrace(spellId, activeTargetId);
                    _lastSpellCast = RynthClock.Now;
                    _lastCastWasRing = false;
                    _pendingDebuffKey = debuffKey;
                    _pendingDebuffTargetId = activeTargetId;
                    _pendingDebuffSpellId = spellId;
                    _pendingDebuffTier = castTier;
                    _waitingForDebuffResult = true;
                    _pendingDebuffCastTime = RynthClock.Now;
                    _host.WriteToChat($"[RynthAi] {(renewal ? "Renewing" : "Casting")}: {debuffKey} (T{castTier}) on {target.Name}", 5);
                }
                catch { }
                return;
            }
        }

        // A rule with no attack shape on means no offensive cast at this monster: a script's choice
        // is no exception (it used to be asked first, and cast at a monster the owner turned casting off for).
        if (rule != null && PickBaseShape(rule) < 0)
        {
            _host.Log($"[CombatCast] rule '{rule.Name}' has no attack shapes enabled (UseArc/Ring/Streak/Bolt/Blast/Wall all false) — no offensive cast");
            LastCombatSkipReason = "no-attack-shapes"; // D4 record-only
            return;
        }

        // A script chooses this cast (RynthAi.Combat, CombatManager.ScriptHook.cs): ask it, wait for
        // its answer (held, not an attack yet), cast what it chose; a decline, a bad answer or no
        // answer in time falls through to RynthAi's own choice below, this same tick.
        if (TryScriptAttack(target, element)) return;

        int warTier  = _spellManager?.GetHighestSpellTier(AcSkillType.WarMagic)  ?? 0;
        int voidTier = _spellManager?.GetHighestSpellTier(AcSkillType.VoidMagic) ?? 0;
        int offensiveSpellId = FindBestShapedSpell(element, rule, out bool isRing, out int castShape, arcTargetId: activeTargetId);
        // No spell of that element known (a low caster whose listed knife says Slash, with no
        // Slash war spell): cast an element the character does know rather than nothing every
        // cycle (2026-10-02 log; found 2026-10-04). Not when an arc gave way to an obstacle.
        if (offensiveSpellId == 0 && !_lastShapeArcGaveWay)
        {
            foreach (string alt in WeaponPlanner.FallbackOrder)
            {
                if (alt.Equals(element, StringComparison.OrdinalIgnoreCase)) continue;
                int altId = FindBestShapedSpell(alt, rule, out bool altRing, out int altShape, arcTargetId: activeTargetId);
                if (altId == 0) continue;
                if (_noElementFallbackLogged.Add(element))
                    _host.Log($"[CombatCast] no {element} attack spell known: casting {alt} instead");
                element = alt; offensiveSpellId = altId; isRing = altRing; castShape = altShape;
                break;
            }
        }
        LogArcChoice(rule, target, castShape);
        // A wall flies along the caster's facing and the server doesn't turn the caster for it:
        // face the target first and let the new heading reach the server (2026-10-08).
        if (offensiveSpellId != 0 && castShape == ShapeWall && HoldForWallFacing(activeTargetId))
        {
            _wallFaceHeld = true;
            LastCombatSkipReason = "wall-face"; // D4 record-only
            return;
        }
        // A ring or a wall hits several monsters: its damage isn't the fight target's alone.
        bool multiTarget = isRing || castShape == ShapeWall;
        if (offensiveSpellId != 0 && ManaHoldsCast(offensiveSpellId)) return;   // mana reserve
        if (offensiveSpellId != 0)
        {
            LastCombatSkipReason = "cast"; // D4 record-only: offensive cast issued this tick
            // Visibility: log exactly what war/void spell we're about to cast
            // (id + resolved name + element/tier/target). Diagnostic only.
            _lastOffensiveCastLine = $"offensive id={offensiveSpellId} " +
                      $"'{SpellTableStub.GetById(offensiveSpellId)?.Name}' elem={element} " +
                      $"shape={ShapeName(castShape)} ring={isRing} warTier={warTier} voidTier={voidTier} target='{target.Name}'";
            _host.Log("[CombatCast] " + _lastOffensiveCastLine);
            try
            {
                _host.CastSpellFor((uint)activeTargetId, offensiveSpellId, "Combat", SpellTableStub.GetById(offensiveSpellId)?.Name ?? $"spell {offensiveSpellId}");
                MarkCombatCastIssued();
                StartCastChatTrace(offensiveSpellId, activeTargetId);
                SessionAttackCasts++;      // D6: offensive attack-cast tally for the casts-per-kill signal
                CastsSinceLastKill++;      //     reset in OnKillNotification when a kill lands
                _lastSpellCast = RynthClock.Now;
                _lastCastWasRing = multiTarget;   // walls too: area casts (2026-10-08)
                _wallFaceAt = DateTime.MinValue;  // the next wall checks its facing afresh
                // _offensiveCastThisCycle is NOT set here anymore — a cast
                // attempt that the server refuses (e.g. "You're too busy!")
                // must not count toward the blacklist miss streak. The
                // RecordOffensiveCast trigger moved to the chat-confirmation
                // path so only chat-confirmed casts queue a judgement.
                _pendingOffensiveSpellId   = offensiveSpellId;
                _pendingOffensiveCastAt    = RynthClock.Now;
                _pendingOffensiveTargetId  = activeTargetId;
                NoteCastSent(activeTargetId);
                NoteOffensiveSeq();
                // Environment-impact throttle: follow this cast's projectiles (rings and walls
                // don't fly at the target, so they aren't judged).
                if (!multiTarget)
                    RegisterCastWatch(activeTargetId, SpellTableStub.GetById(offensiveSpellId)?.Name ?? $"spell {offensiveSpellId}");

                // Tag this cast's weapon + element + tier so the pushed damage event
                // can be attributed to it, count it for casts-to-kill learning, then
                // decide if it's a predicted kill shot.
                // The wand really in hand (it was the first listed wand, so the Damage tab
                // filed every cast under that one whatever was wielded).
                int castWand = InHandWeaponId();
                if (castWand == 0 || !IsWandObject(_worldFilter[castWand] ?? new WorldObject(0, ""))) castWand = FindWandInItems();
                _lastCastWeaponId = unchecked((uint)castWand);
                _lastCastElement  = element;
                // Tier = the LEVEL of the spell actually cast (1-8), not the caster's highest
                // castable tier (which made every magic kill record tier 8). Fall back to the
                // old max-tier only if the level can't be derived, so it never regresses to blank.
                int castLevel = SpellTableStub.GetById(offensiveSpellId)?.Level ?? 0;
                // Walls have no tier in their names: their level from their power (2026-10-08).
                if (castLevel <= 0 && SpellDatabase.TryGetCategoryAndPower(offensiveSpellId, out _, out int castPower))
                    castLevel = SpellDatabase.LevelFromPower(castPower);
                int castTierMag = castLevel > 0 ? castLevel : Math.Max(warTier, voidTier);
                // Ring spells are stored as a NEGATIVE tier so the Damage tab can render them as
                // "R<level>" (e.g. a level-2 ring = -2 -> "R2"); non-ring stays positive; 0 = none.
                // Walls are filed with the rings (2026-10-08): an area cast's kills and casts-to-kill
                // must not mix with the single-target tier of the same level. (The Damage tab shows
                // them as "R<level>" until it has a label of its own for walls.)
                _lastCastTier     = multiTarget ? -castTierMag : castTierMag;
                // Record the latest tier used vs this monster so the Damage tab's collapsed row
                // reflects it immediately (covers ring casts, which skip RecordHit).
                if (_fightTargetWcid != 0) _damageStore?.NoteCast(_fightTargetWcid, _lastCastTier, _fightTargetName);
                _fightCastCount++;
                // Snapshot this fight so a kill that lands after we've dropped/advanced
                // can still be credited by name (see OnKillNotification step 3).
                if (_fightTargetWcid != 0)
                {
                    _lastFightWcid     = _fightTargetWcid;
                    _lastFightName     = _worldFilter[activeTargetId]?.Name ?? _fightTargetName;
                    _lastFightWeaponId = _lastCastWeaponId;
                    _lastFightElement  = _lastCastElement;
                    _lastFightTier     = _lastCastTier;
                    _lastFightCount    = _fightCastCount;
                    StampRecentFight();
                }
                EvaluateKillShot(_lastCastWeaponId, element, _lastCastTier, multiTarget);
            }
            catch { }
        }
        else
        {
            bool snapWarm = _spellManager?.IsKnownSnapshotWarm == true;
            // Chat at most every 30 s (it used to go out every attack cycle; sustained chat is
            // the AddTextToScroll crash path). The file log keeps every occurrence.
            if ((RynthClock.Now - _lastNoSpellChatAt).TotalMilliseconds >= NoSpellChatIntervalMs)
            {
                _lastNoSpellChatAt = RynthClock.Now;
                _host.WriteToChat($"[RynthAi] No spell found: elem={element} warTier={warTier} voidTier={voidTier} snapshotWarm={snapWarm} pid={_playerId}", 2);
            }
            _host.Log($"[CombatCast] no offensive spell: elem={element} warTier={warTier} voidTier={voidTier} snapshotWarm={snapWarm} rule={rule?.Name ?? "null"} target='{target.Name}'" +
                      (_lastShapeArcGaveWay ? " (arc blocked, and no other shape or bolt known: no arc cast into the obstacle)" : ""));
            LastCombatSkipReason = "no-offensive-spell"; // D4 record-only (FindBestShapedSpell==0)
            _lastSpellCast = RynthClock.Now;
        }
    }

    private int _autoElemDiagCount;
    private readonly HashSet<string> _noElementFallbackLogged = new(StringComparer.OrdinalIgnoreCase);

    private string GetPreferredElement(WorldObject? target, MonsterRule? rule)
    {
        if (rule != null && !string.IsNullOrEmpty(rule.DamageType) &&
            !rule.DamageType.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            return rule.DamageType;

        // Auto: the same plan the weapon choice makes (WeaponPlanner), so the spell and the
        // wand agree: the weakest element a listed wand has (ties go to the wand in hand),
        // else the weakest the character can cast; weakness unknown: the wand in hand's
        // element; nothing known: Slash. It used to ignore the wand, and ties in the weakness
        // table went to Slash (listed first), so a fire wand cast Blades (2026-09-30).
        if (target == null) return WeaponPlanner.FallbackOrder[0];
        var plan = PlanFor(target, rule);
        // The plan's wand isn't the one in hand (the swap was held: the target is nearly dead, a
        // swap is resting, the off hand blocks it): the wand in hand casts its own element (owner's
        // rule, 2026-09-30: "the wand should always cast its element"). The plan's element went
        // out through the other wand: Acid Blasts from a Frost Sceptre while "not swapping Frost
        // Sceptre for Acid Staff yet (target at 23% health)" (R-20261029-125901-e22d, Conquest).
        if (HeldWandElement(plan, rule) is string held)
            return held;
        if (_autoElemDiagCount < 20)
        {
            _autoElemDiagCount++;
            var weak = WeaknessFor(target);
            _host.Log($"[CombatCast] auto-element '{target.Name}': {plan.Element} (weapon 0x{(uint)plan.WeaponId:X8}, {plan.Source}; {weak?.Source ?? "weakness unknown"}: {weak?.Describe() ?? ""})");
        }
        return plan.Element.Length > 0 ? plan.Element : WeaponPlanner.FallbackOrder[0];
    }

    /// <summary>
    /// This character can cast <paramref name="element"/> itself (FindBestShapedSpell falls
    /// back to Fire for anything it can't, so a non-zero result alone doesn't say that).
    /// </summary>
    // Logged once per (plan wand, wand in hand, element): the cast follows the wand in hand.
    private (int Plan, int Hand, string Elem) _heldWandElemLogged;

    /// <summary>
    /// The element of the wand in hand when it isn't the plan's weapon and the character can cast
    /// that element (null = cast the plan's element). Only a wand: a melee or missile weapon in
    /// hand doesn't choose the spell. A wand whose element isn't known yet, or isn't castable
    /// (Nether without Void), leaves the plan's element.
    /// </summary>
    private string? HeldWandElement(WeaponPlan plan, MonsterRule? rule)
    {
        int hand = InHandWeaponId();
        if (hand == 0 || plan.WeaponId == 0 || hand == plan.WeaponId) return null;
        if (_worldFilter[hand] is not WorldObject wo || !IsWandObject(wo)) return null;
        var c = ListedCandidates().FirstOrDefault(x => x.Id == hand);
        string elem = c?.Element ?? WeaponElements.Normalize(ElementTracker.Read(hand, wo.Name).Element);
        if (elem.Length == 0 || elem.Equals(plan.Element, StringComparison.OrdinalIgnoreCase) || !CanCastElement(elem, rule)) return null;
        var key = (plan.WeaponId, hand, elem);
        if (key != _heldWandElemLogged)
        {
            _heldWandElemLogged = key;
            _host.Log($"[CombatCast] the plan's wand 0x{(uint)plan.WeaponId:X8} '{WeaponName(plan.WeaponId)}' ({plan.Element}) isn't in hand: casting {elem}, the element of 0x{(uint)hand:X8} '{wo.Name}' in hand");
        }
        return elem;
    }

    private bool CanCastElement(string element, MonsterRule? rule)
    {
        if (CanCastOverride != null) return CanCastOverride(element);
        bool isVoid = element.Equals("Nether", StringComparison.OrdinalIgnoreCase);
        if (isVoid)
        {
            if (_charSkills != null && _charSkills[AcSkillType.VoidMagic].Training < 2) return false;
            if (!VoidSpellShapes.ContainsKey("Nether")) return false;
        }
        else
        {
            if (_charSkills != null && _charSkills[AcSkillType.WarMagic].Training < 2) return false;
            if (!SpellShapes.ContainsKey(element)) return false;
        }
        return FindBestShapedSpell(element, rule, out _) != 0;
    }

    private bool HasPendingDebuffs(MonsterRule rule, string element)
    {
        if (rule == null) return false;
        DateTime now = RynthClock.Now;
        foreach (var debuffKey in BuildDebuffList(rule, element))
            if (!_debuffs.IsOn(activeTargetId, debuffKey, now)) return true;
        return false;
    }

    /// <summary>
    /// The wand to cast with: a listed one (the one in hand first). The pack is searched only
    /// when the Items list has no wand at all and WieldUnlistedWandWhenNoneListed is on, and
    /// then by object class only. It used to take any wand-named item from the pack.
    /// </summary>
    private int FindWandInItems()
    {
        int inHand = InHandWeaponId();
        int id = WeaponList.FindWand(_settings.ItemRules, i => _worldFilter[i], wo => wo.Id == inHand,
            _worldFilter.GetInventory(), _settings.WieldUnlistedWandWhenNoneListed, out bool unlisted);
        if (unlisted && _unlistedNoted.Add(id))
            _host.Log($"[EquipDiag] no wand in the Items list — using 0x{(uint)id:X8} '{_worldFilter[id]?.Name}' from the pack (WieldUnlistedWandWhenNoneListed)");
        return id;
    }

    private static bool IsWandObject(WorldObject wo) => WeaponList.IsWand(wo);

    /// <summary>War or Void Magic trained. Skills not read yet count as trained, like the
    /// other skill gates here.</summary>
    private bool AttackMagicTrained =>
        _charSkills == null
        || _charSkills[AcSkillType.WarMagic].Training >= 2
        || _charSkills[AcSkillType.VoidMagic].Training >= 2;

    /// <summary>A script holds the attack kind (RynthAi.Combat): it picks the attack spell, from
    /// any school, so a caster is the combat weapon whatever War/Void training says.</summary>
    private static bool ScriptPicksAttack => CombatHook.IsActive(RynthAiCombat.KindAttack);

    /// <summary>The character can attack with a caster: War or Void Magic trained, or a script
    /// choosing the attack spells (a Life-only caster whose script drains, for one). Gates the
    /// wand fallback when no weapon was chosen; the Items list itself admits a caster under the
    /// hook only when it has no melee/missile weapon (ListedCandidates).</summary>
    private bool CanAttackWithMagic => AttackMagicTrained || ScriptPicksAttack;

    /// <summary>For the [EquipDiag] lines: canMagic and whether the script hook made it true.</summary>
    private string CanMagicDiag()
    {
        bool trained = AttackMagicTrained, hook = ScriptPicksAttack;
        return $"canMagic={trained || hook}" + (hook && !trained ? " (script attack hook)" : "");
    }

    private readonly HashSet<int> _wandRejectedNoted = new();

    /// <summary>A weapon combat may fight with: anything the character has (in the world
    /// cache) but a caster when the character has no attack magic. Debuff casting picks its
    /// wand separately. An id missing from the cache is not a candidate: a weapon that was
    /// traded, destroyed or copied in with another character's profile used to win the
    /// Items list, shadow every real weapon below it, and send combat in "unarmed" from
    /// peace mode, where the server drops the attacks and every target got blacklisted.</summary>
    private bool IsUsableCombatWeapon(int id)
    {
        var wo = _worldFilter[id];
        if (wo == null) return false;
        return AttackMagicTrained || !IsWandObject(wo);
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
        if (rule.Corrosion)        list.Add("Void:Corrosion");
        if (rule.Corruption)       list.Add("Void:Corruption");
        if (rule.DestructiveCurse) list.Add("Void:Destructive Curse");
        if (rule.WeakeningCurse)   list.Add("Void:Weakening Curse");
        if (rule.FesteringCurse)   list.Add("Void:Festering Curse");
        if (!string.IsNullOrWhiteSpace(rule.CustomDebuffs))
            foreach (string part in rule.CustomDebuffs.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string nm = part.Trim();
                if (nm.Length > 0) list.Add("Custom:" + nm);
            }
        return list;
    }

    /// <summary>
    /// A debuff the player typed in: a full spell name casts as is; a base name casts the best
    /// tier the character knows ("Incantation of X", then X VII..I), like the built-in ones.
    /// </summary>
    private int FindBestCustomDebuffWithTier(string name, out int tier)
    {
        tier = 0;
        if (_spellManager == null || string.IsNullOrWhiteSpace(name)) return 0;
        name = name.Trim();
        int id = TrySpellByName(name);
        if (id != 0) return id;
        // Void too (2026-10-08): "Corruption" with Corruption V and VII known cast V, because only
        // Creature and Life set the cap and the search started below VII. Debuffs come from these
        // three schools; a level the character doesn't know is never picked.
        int maxTier = Math.Max(Math.Max(EffectiveMaxTier(AcSkillType.CreatureEnchantment), EffectiveMaxTier(AcSkillType.LifeMagic)),
                               EffectiveMaxTier(AcSkillType.VoidMagic));
        if (maxTier >= 8) { id = TrySpellByName($"Incantation of {name}"); if (id != 0) { tier = 8; return id; } }
        for (int t = Math.Min(maxTier, 7); t >= 1; t--)
        {
            id = TrySpellByName($"{name} {GetRomanNumeral(t)}");
            if (id != 0) { tier = t; return id; }
        }
        return 0;
    }

    /// <summary>
    /// The best level of a Void debuff the character knows ("Incantation of X", then X VII..I),
    /// capped by Void Magic alone: a character without it gets 0 and the debuff is passed over.
    /// </summary>
    private int FindBestVoidDebuffWithTier(string baseName, out int tier)
    {
        tier = 0;
        if (_spellManager == null) return 0;
        int maxTier = EffectiveMaxTier(AcSkillType.VoidMagic);
        if (maxTier >= 8) { int id = TrySpellByName($"Incantation of {baseName}"); if (id != 0) { tier = 8; return id; } }
        for (int t = Math.Min(maxTier, 7); t >= 1; t--)
        {
            int id = TrySpellByName($"{baseName} {GetRomanNumeral(t)}");
            if (id != 0) { tier = t; return id; }
        }
        return 0;
    }

    private int FindBestDebuffSpellWithTier(string debuffType, out int tier)
    {
        tier = 0;
        if (_spellManager == null) return 0;

        if (!DebuffSpells.TryGetValue(debuffType, out string[]? spellInfo) || spellInfo.Length < 2) return 0;

        string tieredBase = spellInfo[0];
        string loreName   = spellInfo[1];

        int maxTier = EffectiveMaxTier(AcSkillType.CreatureEnchantment);

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

        int maxTier = EffectiveMaxTier(AcSkillType.CreatureEnchantment);

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
        // Single source of truth shared with the buff path: unresolvable skip +
        // the engine's main-thread known-spell snapshot. Combat used to roll
        // its own check that trusted the mis-bound IsSpellKnown oracle (lies
        // "true" for unknown spells) and never saw _knownSpellIds, so an
        // unknown tier-8 (e.g. Incantation of Flame Bolt) resolved as castable
        // and never tiered down. Delegating fixes that for war/void/debuff/ring
        // resolution alike. RefreshKnownSpells() is pumped on the attack path.
        // Deterministic: name → id, REQUIRE the char knows it (authoritative
        // warm spellbook snapshot). No empirical unresolvable blacklist, no
        // mis-bound engine oracle — selection is purely predictive so nothing
        // self-poisons.
        if (!_spellManager.TryResolveKnownSpellId(name, out int id)) return 0;

        // Scarab gate DISABLED (EnablePredictiveComponentGate=false). Field-
        // tested 2026-05-17, pid 15372: it rejected every KNOWN Force Arc
        // tier ("NO-SCARAB" 2724/2723) while the char had the scarab — the
        // dat-decoded SpellComponentTable scarab name does not match the ACE
        // inventory item name. This is the documented predictive-components
        // failure on ACE (see rynthai_predictive_components.md): only the
        // server knows its real component rules. Selection therefore stays
        // known ∧ skill-window tier ∧ configured shape; the player keeps
        // components stocked. Code retained behind the flag for a future
        // retry IF a verified scarab-name↔inventory-name mapping exists.
        if (EnablePredictiveComponentGate)
        {
            EnsureInventoryNameCache();
            if (!ComponentDatabase.HasRequiredScarab(id, _invNamesLower))
            {
                if (_compSkipLogged.Add(id))
                    _host.Log($"[CombatCast] NO-SCARAB id={id} " +
                              $"'{SpellTableStub.GetById(id)?.Name}' — required scarab " +
                              $"not in inventory; tiering down.");
                return 0;
            }
        }
        return id;
    }

    private void EnsureInventoryNameCache()
    {
        if ((RynthClock.Now - _invNamesBuiltAt).TotalMilliseconds < InvNameCacheMs) return;
        _invNamesBuiltAt = RynthClock.Now;
        _invNamesLower.Clear();
        _compSkipLogged.Clear();
        foreach (var wo in _worldFilter.GetInventory())
            if (!string.IsNullOrEmpty(wo.Name))
                _invNamesLower.Add(wo.Name.ToLowerInvariant());
    }

    private int CountMonstersInRange(double rangeYards)
    {
        if (_playerId == 0) return 0;
        int pid = (int)_playerId;
        int count = 0;
        foreach (var wo in _worldFilter.GetLandscape())
        {
            if (wo.ObjectClass != AcObjectClass.Monster) continue;
            // Only a known-dead monster is skipped. An unknown health (-1) is a living monster we
            // haven't selected: ACE sends health only for the selected target, so skipping those
            // left the count at about 1 and a ring was never due (2026-10-14).
            if (_worldFilter.GetHealthRatio(wo.Id) == 0f || _recentlyKilled.ContainsKey(wo.Id)) continue;
            if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)wo.Id)) continue;
            if (_worldFilter.Distance(pid, wo.Id) <= rangeYards)
                count++;
        }
        return count;
    }

    private int FindBestShapedSpell(string element, MonsterRule? rule) =>
        FindBestShapedSpell(element, rule, out _);

    private int FindBestShapedSpell(string element, MonsterRule? rule, out bool isRing) =>
        FindBestShapedSpell(element, rule, out isRing, out _);

    /// <summary>
    /// The base shape a rule asks for when no ring is due: Arc, then Streak, then Blast, then
    /// Bolt (the first one on wins; Blast beats Bolt so turning Blast on next to the default Bolt
    /// does something). A ring-only rule casts Bolt between rings, and a wall-only rule (or ring and
    /// wall only) Bolt between walls. -1 = no shape on at all.
    /// </summary>
    internal static int PickBaseShape(MonsterRule? rule)
    {
        if (rule == null) return ShapeBolt;
        if (rule.UseArc)    return ShapeArc;
        if (rule.UseStreak) return ShapeStreak;
        if (rule.UseBlast)  return ShapeBlast;
        if (rule.UseBolt)   return ShapeBolt;
        return rule.UseRing || rule.UseWall ? ShapeBolt : -1;
    }

    /// <summary>
    /// The base shape when Blast is conditional (Blast Range on): Blast is then an override like
    /// the ring, so the base is the rule's other shape (Arc, Streak, Bolt), else Bolt.
    /// </summary>
    internal static int PickBaseShapeWithoutBlast(MonsterRule? rule)
    {
        if (rule == null) return ShapeBolt;
        if (rule.UseArc)    return ShapeArc;
        if (rule.UseStreak) return ShapeStreak;
        return ShapeBolt;
    }

    // A blast is 3 projectiles over a 90 degree fan (spell table: num_Projectiles 3,
    // spread_Angle 90), centred on the target we face before casting. Counted with a margin.
    internal const double BlastFanHalfAngle = 55.0;

    /// <summary>Monsters (the target included) within <paramref name="rangeYards"/> and inside
    /// the blast's fan ahead (we face the target to cast). From the combat scan: LOS-clear and
    /// attackable.</summary>
    private int CountMonstersInBlastFan(double rangeYards)
    {
        int count = 0;
        foreach (var c in _scannedTargets)
            if (c.Distance <= rangeYards && c.Angle <= BlastFanHalfAngle && !_recentlyKilled.ContainsKey(c.Id))
                count++;
        return count;
    }

    // ── Wall spells (2026-10-08) ─────────────────────────────────────────────────
    // A wall is 10 projectiles in a block 5 wide and 2 high (ACE spell table: dims 5x1x2, padding
    // 0.8, created 2 m ahead of the caster) that fly straight along the caster's facing: it is an
    // UNTARGETED spell (the client sends CastUntargetedSpell for it even with a target selected,
    // and ACE launches it along the caster's forward vector), so the server never turns the caster
    // to the target as it does for bolts and blasts. Its swath is therefore a corridor a few metres
    // wide, not a fan: monsters are counted when they are ahead of the caster, within the range,
    // and within WallHalfWidth of the line to the target (the target included). We turn to face
    // the target before the cast (HoldForWallFacing), so that line is where the wall goes.
    internal const double WallHalfWidth = 4.0;

    /// <summary>Tests: the wall-path count instead of the scan (the scan needs a live world).</summary>
    internal Func<int>? WallPathCountOverride { get; set; }
    /// <summary>Tests: the monsters in ring range instead of the landscape count.</summary>
    internal Func<int>? RingCountOverride { get; set; }

    /// <summary>Monsters (the target included) in a wall's path toward <paramref name="targetId"/>:
    /// ahead of the caster, within <paramref name="rangeYards"/>, and within <see cref="WallHalfWidth"/>
    /// of the line to the target. From the combat scan: LOS-clear and attackable. 0 when the target
    /// isn't in the scan (its bearing is unknown).</summary>
    private int CountMonstersInWallPath(double rangeYards, int targetId)
    {
        if (WallPathCountOverride != null) return WallPathCountOverride();
        double toTarget = double.NaN;
        foreach (var c in _scannedTargets)
            if (c.Id == targetId) { toTarget = c.Bearing; break; }
        if (double.IsNaN(toTarget)) return 0;
        int count = 0;
        foreach (var c in _scannedTargets)
        {
            if (c.Distance > rangeYards || double.IsNaN(c.Bearing) || _recentlyKilled.ContainsKey(c.Id)) continue;
            if (InWallPath(c.Distance, c.Bearing - toTarget)) count++;
        }
        return count;
    }

    /// <summary>Is a monster <paramref name="distance"/> away, <paramref name="offDeg"/> degrees off the
    /// line to the target, in the wall's path: ahead of the caster and within <see cref="WallHalfWidth"/>
    /// of that line.</summary>
    internal static bool InWallPath(double distance, double offDeg)
    {
        double rad = offDeg * (Math.PI / 180.0);
        double ahead = distance * Math.Cos(rad);
        double aside = Math.Abs(distance * Math.Sin(rad));
        return ahead >= 0 && aside <= WallHalfWidth;
    }

    /// <summary>
    /// The wall of <paramref name="element"/> to cast: among the known spells of its wall category
    /// (<see cref="WallCategories"/>), the highest level the War tier allows (the level by ACE's
    /// server formula from the spell's power, <see cref="SpellDatabase.LevelFromPower"/>: the same
    /// tier limit the tiered shapes use), then the highest power, then the lowest id (the retail
    /// wall). 0 = none: Nether, an element without walls, War untrained, or no wall known.
    /// </summary>
    internal int FindBestWallSpell(string element)
    {
        if (_spellManager == null || !_spellManager.IsKnownSnapshotWarm) return 0;   // cold: don't guess
        if (!WallCategories.TryGetValue(element, out int category)) return 0;
        if (_charSkills != null && _charSkills[AcSkillType.WarMagic].Training < 2) return 0;
        int maxTier = EffectiveMaxTier(AcSkillType.WarMagic);
        int best = 0, bestLevel = 0, bestPower = -1;
        foreach (int id in SpellDatabase.IdsInCategory(category))
        {
            if (!_spellManager.IsKnownSpellId(id)) continue;
            SpellDatabase.TryGetCategoryAndPower(id, out _, out int power);
            int level = SpellDatabase.LevelFromPower(power);
            if (level < 1 || level > maxTier) continue;
            if (level > bestLevel || (level == bestLevel && power > bestPower))
            {
                best = id; bestLevel = level; bestPower = power;
            }
        }
        return best;
    }

    internal static string ShapeName(int shape) => shape switch
    {
        ShapeArc => "Arc", ShapeRing => "Ring", ShapeStreak => "Streak", ShapeBolt => "Bolt", ShapeBlast => "Blast",
        ShapeWall => "Wall", _ => "?",
    };

    /// <summary>
    /// The spell base name for <paramref name="element"/> in <paramref name="shape"/> and, for war
    /// streaks, bolts and blasts, the lore name of its tier 7. Void looks in the void rows (any
    /// element it has no row for casts Nether); war casts Fire for an element it has no row for
    /// (<paramref name="forcedWar"/> is then true: the caller casts with War Magic).
    /// </summary>
    internal static bool TryGetShapeBase(string element, int shape, bool useVoid,
        out string baseName, out string? tier7Lore, out bool forcedWar)
    {
        baseName = ""; tier7Lore = null; forcedWar = false;
        var shapes = useVoid ? VoidSpellShapes : SpellShapes;
        if (!shapes.TryGetValue(element, out string[]? elementShapes))
        {
            if (useVoid)
            {
                // Void Magic only damages with Nether — fall back to Nether shapes for
                // ANY element not in VoidSpellShapes (Cold/Lightning/Acid/Blade/Pierce/
                // Bludgeon/Slash). Previously this fell through to War Magic Fire, which
                // forced War on Void-only casters whose War skill was untrained.
                if (!VoidSpellShapes.TryGetValue("Nether", out elementShapes)) return false;
            }
            else
            {
                if (!SpellShapes.TryGetValue("Fire", out elementShapes)) return false;
                forcedWar = true;
            }
        }
        if (shape < 0) return false;
        if (shape >= elementShapes.Length) shape = elementShapes.Length - 1;
        baseName = elementShapes[shape];

        // Streak / Bolt / Blast war lines have a lore-named tier-7 (no "{base} VII").
        // Arc uses Roman "VII" (exists); Void has no lore tier-7 in this table.
        if (!useVoid && WarTier7Lore.TryGetValue(forcedWar ? "Fire" : element, out string[]? w7))
        {
            if (shape == ShapeStreak)     tier7Lore = w7[0];
            else if (shape == ShapeBolt)  tier7Lore = w7[1];
            else if (shape == ShapeBlast) tier7Lore = w7[2];
        }
        return true;
    }

    internal int FindBestShapedSpell(string element, MonsterRule? rule, out bool isRing, out int castShape)
        => FindBestShapedSpell(element, rule, out isRing, out castShape, arcTargetId: 0);

    /// <summary>
    /// Which shape this cast is, before the spell is looked up. In order:
    ///   1. the rule's base shape: Arc, Streak, Blast, Bolt (<see cref="PickBaseShape"/>);
    ///   2. Blast override (Blast Range on): Blast when its fan count is met, else the rule's
    ///      other shape (<paramref name="blastConditional"/>, <paramref name="blastFanMet"/>);
    ///   3. Ring override (Ring Range on): Ring when its count is met (<paramref name="ringDue"/>);
    ///   3b. Wall override (Wall Range on, 2026-10-08): Wall when its path count is met and a wall
    ///      is known (<paramref name="wallDue"/>), unless a ring is due. So with several due: Ring,
    ///      then Wall, then Blast, then the base shape. A ring hits all around and needs no turn;
    ///      a wall reaches further along a line but costs a turn, and outdoes a blast's 3 bolts;
    ///   4. Arc when clear: an Arc whose flight path to the target is blocked (a wall, or a
    ///      ceiling the arc rises into) gives way to the rule's other shape: Streak, then Blast
    ///      (when Blast isn't conditional), then Bolt. An Arc-only rule casts Bolt, as a ring-only
    ///      rule does between rings.
    /// <paramref name="arcBlocked"/> is asked only in step 4, so a ring, a wall or a non-arc rule
    /// costs no ray test; null (no test, or it can't tell: raycasting off or not ready) casts the
    /// Arc as before. <paramref name="arcGaveWay"/> says step 4 replaced the Arc.
    /// </summary>
    internal static int ChooseCastShape(MonsterRule? rule, bool blastConditional, bool blastFanMet, bool ringDue,
        Func<bool?>? arcBlocked, out bool arcGaveWay)
        => ChooseCastShape(rule, blastConditional, blastFanMet, ringDue, wallDue: false, arcBlocked, out arcGaveWay);

    internal static int ChooseCastShape(MonsterRule? rule, bool blastConditional, bool blastFanMet, bool ringDue,
        bool wallDue, Func<bool?>? arcBlocked, out bool arcGaveWay)
    {
        arcGaveWay = false;
        int shape = PickBaseShape(rule);
        if (shape < 0) return -1;
        if (blastConditional)
            shape = blastFanMet ? ShapeBlast : PickBaseShapeWithoutBlast(rule);
        if (ringDue)
            return ShapeRing;
        if (wallDue)
            return ShapeWall;
        if (shape == ShapeArc && arcBlocked != null && arcBlocked() == true)
        {
            arcGaveWay = true;
            shape = ArcFallbackShape(rule, blastConditional);
        }
        return shape;
    }

    /// <summary>The shape a blocked Arc gives way to: the rule's other shape (Streak, then Blast
    /// unless Blast is conditional, then Bolt); Bolt for an Arc-only rule.</summary>
    internal static int ArcFallbackShape(MonsterRule? rule, bool blastConditional)
    {
        if (rule == null) return ShapeBolt;
        if (rule.UseStreak) return ShapeStreak;
        if (rule.UseBlast && !blastConditional) return ShapeBlast;
        return ShapeBolt;
    }

    // ── Arc when clear ───────────────────────────────────────────────────────────
    // An Arc is cast only when the arc ACE flies (fixed 40 m/s horizontal speed, from the
    // caster's head, gravity 9.8; TargetingFSM.IsMagicArcBlocked) reaches the target: target
    // selection in magic mode tests only the straight line, so an arc into a low ceiling used to
    // be cast anyway. Tested per cast (not per frame), and the verdict is kept 300 ms per target.
    internal const double ArcVerdictCacheMs = 300;
    private int _arcVerdictTarget;
    private DateTime _arcVerdictAt = DateTime.MinValue;
    private bool? _arcVerdictBlocked;
    private TargetingFSM.LosDetail _arcVerdictDetail;
    private bool _lastShapeArcGaveWay;
    private int _arcLogTarget;
    private string _arcLogState = "";
    private DateTime _arcLogAt = DateTime.MinValue;
    private const double ArcLogRepeatMs = 10000;
    private readonly HashSet<string> _arcRuleLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests: the arc verdict per target instead of the ray test (null = can't tell).</summary>
    internal Func<int, bool?>? ArcBlockedOverride { get; set; }
    /// <summary>How many arc verdicts were worked out (not served from the 300 ms cache).</summary>
    internal int ArcVerdictComputeCount { get; private set; }

    /// <summary>Is the arc to <paramref name="targetId"/> blocked? null = can't tell (raycasting
    /// off or not ready): the arc is cast as before.</summary>
    internal bool? IsArcBlockedTo(int targetId)
    {
        if (targetId == 0) return null;
        DateTime now = DateTime.UtcNow;
        if (targetId == _arcVerdictTarget && (now - _arcVerdictAt).TotalMilliseconds < ArcVerdictCacheMs)
            return _arcVerdictBlocked;

        bool? blocked = null;
        TargetingFSM.LosDetail det = default;
        if (ArcBlockedOverride != null)
            blocked = ArcBlockedOverride(targetId);
        else if (_settings.EnableRaycasting && RaycastInitialized && _raycastSystem?.TargetingFSM != null)
            blocked = _raycastSystem.TargetingFSM.IsMagicArcBlocked(_host, (uint)targetId, out det);

        ArcVerdictComputeCount++;
        _arcVerdictTarget = targetId;
        _arcVerdictAt = now;
        _arcVerdictBlocked = blocked;
        _arcVerdictDetail = det;
        return blocked;
    }

    /// <summary>Drops the cached arc verdict: the next cast tests the arc again.</summary>
    internal void ForgetArcVerdict() => _arcVerdictAt = DateTime.MinValue;

    /// <summary>"ceiling 2.1 m out", "wall in the straight line": why an arc verdict is blocked.</summary>
    internal static string DescribeArcBlock(in TargetingFSM.LosDetail d)
    {
        if (d.LineBlocked) return "wall in the straight line";
        if (d.Arc.OutOfReach) return "out of reach";
        if (d.Arc.Blocked)
            return d.Arc.HitZ > 0.3f
                ? $"{(d.Dungeon ? "ceiling" : "overhead")} {d.Arc.HitAlong:0.0} m out"
                : $"obstacle {d.Arc.HitAlong:0.0} m out";
        return "blocked";
    }

    /// <summary>Logs the Arc-when-clear choice: once per rule how it behaves, then per target
    /// when the verdict changes (and at most every 10 s while it stays blocked).</summary>
    private void LogArcChoice(MonsterRule? rule, WorldObject target, int castShape)
    {
        if (rule == null || !rule.UseArc) return;
        if (_arcRuleLogged.Add(rule.Name ?? ""))
        {
            string other = ShapeName(ArcFallbackShape(rule, rule.UseBlast && _settings.BlastRange > 0));
            _host.Log($"[CombatCast] rule '{rule.Name}': Arc when its flight path is clear, else {other}" +
                      (_settings.EnableRaycasting ? "" : " (raycasting off: arcs are always cast)"));
        }
        if (castShape == ShapeRing || castShape == ShapeWall) return;   // an override won: no arc question this cast
        string state = _lastShapeArcGaveWay
            ? $"arc blocked ({(ArcBlockedOverride != null ? "test" : DescribeArcBlock(in _arcVerdictDetail))}): {(castShape >= 0 ? ShapeName(castShape) : "nothing")} instead"
            : "arc clear";
        DateTime now = DateTime.UtcNow;
        bool changed = target.Id != _arcLogTarget || state != _arcLogState;
        if (!changed && (!_lastShapeArcGaveWay || (now - _arcLogAt).TotalMilliseconds < ArcLogRepeatMs)) return;
        bool firstClear = !_lastShapeArcGaveWay && (target.Id != _arcLogTarget || _arcLogState.Length == 0);
        _arcLogTarget = target.Id;
        _arcLogState = state;
        _arcLogAt = now;
        if (firstClear) return;   // a clear arc at a new target is the normal case: not logged
        _host.Log($"[CombatCast] {state} -> '{target.Name}'");
    }

    /// <param name="arcTargetId">The target this cast goes to. An Arc is then cast only when its
    /// flight path there is clear (see <see cref="ChooseCastShape"/>). 0 = no test (element
    /// probes, tests of the plain shape choice).</param>
    internal int FindBestShapedSpell(string element, MonsterRule? rule, out bool isRing, out int castShape, int arcTargetId)
    {
        isRing = false;
        castShape = -1;
        if (arcTargetId != 0) _lastShapeArcGaveWay = false;
        if (_spellManager == null) return 0;

        bool useVoid = element.Equals("Nether", StringComparison.OrdinalIgnoreCase);
        bool warTrained  = _charSkills == null || _charSkills[AcSkillType.WarMagic].Training >= 2;
        bool voidTrained = _charSkills == null || _charSkills[AcSkillType.VoidMagic].Training >= 2;
        if (!warTrained && !voidTrained) return 0; // no attack magic — used to fall through to War anyway

        if (useVoid && !voidTrained && warTrained)  useVoid = false;
        if (!useVoid && !warTrained && voidTrained) useVoid = true;

        AcSkillType skill = useVoid ? AcSkillType.VoidMagic : AcSkillType.WarMagic;

        if (PickBaseShape(rule) < 0) return 0; // no shape enabled at all

        // Blast override (Blast Range on): a blast only when enough monsters are in its fan.
        bool blastConditional = rule != null && rule.UseBlast && _settings.BlastRange > 0;
        bool blastFanMet = blastConditional
            && CountMonstersInBlastFan(_settings.BlastRange) >= Math.Max(1, _settings.MinBlastTargets);

        // Ring override: when UseRing is enabled, check if enough monsters are within
        // ring range. If so, upgrade to ring; otherwise keep the base shape (arc/streak/blast/bolt).
        bool ringOn = rule != null && rule.UseRing;
        int ringNear = -1;
        bool ringDue = false;
        if (ringOn && _settings.RingRange > 0)
        {
            ringNear = RingCountOverride?.Invoke() ?? CountMonstersInRange(_settings.RingRange);
            ringDue = ringNear >= Math.Max(1, _settings.MinRingTargets);
        }
        // Like a wall, a ring is due only when one is known: a caster with Use Ring on and no ring
        // learned used to cast nothing once a pack came into ring range and stood in the fight
        // ("No spell found: elem=Nether", #rynth-issues 2026-10-23, DreamWeave). Without one, the
        // rule's other shape is cast. Void's rings are Clouded Soul / Ring of Skulls (2026-11-01).
        int ringId = 0;
        if (ringDue)
        {
            ringId = FindRingSpell(element, skill, useVoid);
            if (ringId == 0)
            {
                ringDue = false;
                // Said for the cast itself only: the element probes (Auto's weakness pick, the
                // wand-in-hand check) ask every element, and used to report rings of elements
                // that were never going to be cast.
                if (arcTargetId != 0) ReportNoRingKnown(element, skill, useVoid);
            }
        }
        // A rule with Ring on that casts no ring says why in the log (2026-11-06: "it doesn't like
        // to use rings even when set" - nothing said that too few monsters were near).
        if (ringOn && arcTargetId != 0 && ringId == 0)
            NoteRingNotCast(rule!, ringNear, ringDue);

        // Wall override (Wall Range on): a wall when enough monsters are in its path toward the
        // target and the character knows a wall of this element (none for Nether). Asked only for a
        // real cast (an element probe has no target, so no path), and only when no ring is due.
        // Without the host's heading snap the character can't be turned, so a wall is cast only
        // when it already faces the target.
        int wallId = 0;
        bool wallDue = false;
        if (!ringDue && arcTargetId != 0 && rule != null && rule.UseWall && _settings.WallRange > 0
            && CountMonstersInWallPath(_settings.WallRange, arcTargetId) >= Math.Max(1, _settings.MinWallTargets)
            && (_host.HasTurnToHeading || GetFacingError(arcTargetId) <= WallFaceToleranceDeg))
        {
            wallId = FindBestWallSpell(element);
            wallDue = wallId != 0;
        }

        int shapeIdx = ChooseCastShape(rule, blastConditional, blastFanMet, ringDue, wallDue,
            arcTargetId != 0 ? () => IsArcBlockedTo(arcTargetId) : null, out bool arcGaveWay);
        if (arcTargetId != 0) _lastShapeArcGaveWay = arcGaveWay;

        if (shapeIdx == ShapeWall)
        {
            castShape = ShapeWall;
            return wallId;
        }

        if (shapeIdx == ShapeRing)
        {
            isRing = true; castShape = ShapeRing;
            return ringId;
        }

        if (!TryGetShapeBase(element, shapeIdx, useVoid, out string baseName, out string? t7, out bool forcedWar)) return 0;
        if (forcedWar) skill = AcSkillType.WarMagic;

        // Strict TYPE adherence: cast ONLY the shape configured in the
        // Monsters tab (Arc/Bolt/Streak/Blast) — plus the ring override handled
        // above. No cross-shape fallthrough; that was casting Streak when
        // Arc was configured but its tiers weren't resolvable.
        // One exception: Blast falls back to Bolt of the same element when the
        // character knows no blast at all — retail has no blast I or II, so a low
        // character set to Blast would otherwise never attack.
        int id = FindBestOffensiveSpellId(baseName, skill, t7);
        castShape = id != 0 ? shapeIdx : -1;
        // A blocked Arc whose other shape isn't known casts the same element's Bolt (never the arc).
        if (id == 0 && arcGaveWay && shapeIdx == ShapeStreak
            && TryGetShapeBase(element, ShapeBolt, useVoid, out string arcBoltBase, out string? arcBoltT7, out _))
        {
            id = FindBestOffensiveSpellId(arcBoltBase, skill, arcBoltT7);
            if (id != 0) castShape = ShapeBolt;
        }
        if (id == 0 && shapeIdx == ShapeBlast
            && TryGetShapeBase(element, ShapeBolt, useVoid, out string boltBase, out string? boltT7, out _))
        {
            id = FindBestOffensiveSpellId(boltBase, skill, boltT7);
            if (id != 0)
            {
                castShape = ShapeBolt;
                if (_blastFallbackLogged.Add(element))
                    _host.Log($"[CombatCast] no {baseName} known (blasts start at III): casting {boltBase} instead");
            }
        }
        return id;
    }

    private readonly HashSet<string> _blastFallbackLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noRingLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The best ring of <paramref name="element"/> the character knows (0 = none): War's lore
    /// rings, Void's Clouded Soul / Ring of Skulls, else the generic "{ring base} {tier}" of the
    /// shape table.
    /// </summary>
    private int FindRingSpell(string element, AcSkillType skill, bool useVoid)
    {
        if (!TryGetShapeBase(element, ShapeRing, useVoid, out string ringBase, out string? ringT7, out bool forcedWar)) return 0;
        // War has no row for this element (Nether for a caster without Void Magic): its bolts are
        // Fire's (TryGetShapeBase), and so are its rings. The ring used to be looked up under the
        // element itself, which has no War ring, so such a caster never rang (2026-11-06).
        if (forcedWar) { skill = AcSkillType.WarMagic; element = "Fire"; }
        int id = FindBestRingSpell(element, skill);
        return id != 0 ? id : FindBestOffensiveSpellId(ringBase, skill, ringT7);
    }

    /// <summary>
    /// A ring was due but none of <paramref name="element"/> is known: the file log says so once
    /// per element, with the reason (the tier, the spellbook not read yet, or the rings tried);
    /// the chat at most every <see cref="NoSpellChatIntervalMs"/> per element. For the cast
    /// itself only, never an element probe.
    /// </summary>
    private void ReportNoRingKnown(string element, AcSkillType skill, bool useVoid)
    {
        string why = RingMissingReason(element, skill, useVoid);
        if (_noRingLogged.Add(element + (useVoid ? "/void" : "")))
            _host.Log($"[CombatCast] no {element} ring spell known ({why}): casting the rule's other shape when a ring is due");
        DateTime nowRing = RynthClock.Now;
        if (!_ringMissingChatAt.TryGetValue(element, out DateTime lastRing)
            || (nowRing - lastRing).TotalMilliseconds >= NoSpellChatIntervalMs)
        {
            _ringMissingChatAt[element] = nowRing;
            _host.WriteToChat($"[RynthAi] Ring spell not found for {element} ({why})", 2);
        }
    }

    /// <summary>Why no ring of <paramref name="element"/> was found: "War Magic tier 4: rings are
    /// level 5 and 7", "the spellbook isn't read yet", or "tried: Halo of Frost II, Halo of Frost".</summary>
    internal string RingMissingReason(string element, AcSkillType skill, bool useVoid)
    {
        if (!useVoid && !SpellShapes.ContainsKey(element)) { skill = AcSkillType.WarMagic; element = "Fire"; }
        string school = skill == AcSkillType.VoidMagic ? "Void Magic" : "War Magic";
        int tier = EffectiveMaxTier(skill);
        if (tier < 5) return $"{school} tier {tier}: rings are level 5 and 7";
        if (_spellManager != null && !_spellManager.IsKnownSnapshotWarm) return "the spellbook isn't read yet";
        if (skill == AcSkillType.VoidMagic)
            return $"tried: {string.Join(", ", VoidRingsLevel7)}, {VoidRingLevel5}";
        if (!RingLoreNames.TryGetValue(element, out string[]? lore) || lore.Length < 2) return "War has no ring of this element";
        string plain = PlainRings.TryGetValue(element, out string? p) ? ", " + p : "";
        return tier >= 7 ? $"tried: {lore[1]}, {lore[0]}{plain}" : $"tried: {lore[0]}{plain}; {lore[1]} is level 7";
    }

    // Why a rule with Ring on cast no ring (2026-11-06): the log says it when the reason changes and
    // then at most every RingNotCastLogMs per rule and reason, never every attack.
    internal const double RingNotCastLogMs = 30_000;
    private readonly Dictionary<string, DateTime> _ringNotCastLoggedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ringOffLogged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A real cast under a rule with Ring on, and no ring this time: Ring Range 0 (rings off, said
    /// once per rule), or fewer than Min Ring Targets monsters within Ring Range (the count is
    /// said). A ring that was due but isn't known is <see cref="ReportNoRingKnown"/>'s.
    /// </summary>
    private void NoteRingNotCast(MonsterRule rule, int near, bool countMet)
    {
        string name = rule.Name ?? "";
        if (_settings.RingRange <= 0)
        {
            if (_ringOffLogged.Add(name))
                _host.Log($"[CombatCast] rule '{name}' has Ring on, but Ring Range is {_settings.RingRange}: rings are off (Settings > Ring Range)");
            return;
        }
        int need = Math.Max(1, _settings.MinRingTargets);
        if (countMet || near >= need) return;   // the count was met: no ring known (ReportNoRingKnown)
        string key = name + "|count";
        DateTime now = TargetClock();
        if (_ringNotCastLoggedAt.TryGetValue(key, out DateTime last) && (now - last).TotalMilliseconds < RingNotCastLogMs) return;
        _ringNotCastLoggedAt[key] = now;
        _host.Log($"[CombatCast] rule '{name}' has Ring on: no ring yet, {near} monster{(near == 1 ? "" : "s")} within Ring Range " +
                  $"{_settings.RingRange} and Min Ring Targets is {need} (counted from the character, the target included)");
    }

    private int FindBestRingSpell(string element, AcSkillType skill)
    {
        if (skill == AcSkillType.VoidMagic)
            return FindBestVoidRingSpell();

        if (!RingLoreNames.TryGetValue(element, out string[]? loreNames) || loreNames.Length < 2) return 0;

        int maxTier = EffectiveMaxTier(skill);

        // The rings are level 5 and 7 (SpellData: power 200 and 300): the named one (Cassius' Ring of
        // Fire, Halo of Frost, ...) and the plain one (Flame Ring, Acid Ring, Force Ring, Lightning Ring)
        // at 5, the named "II" at 7. The named ring used to wait for tier 6, and the plain ones were
        // looked for with numerals ("Ring of Fire V"), names no server has: a caster at tier 5 never
        // got a ring (2026-10-25).
        if (maxTier >= 7)
        {
            int id = TrySpellByName(loreNames[1]); if (id != 0) return id;
            id = TrySpellByName(loreNames[1].Replace("'", "\u2019")); if (id != 0) return id;
            id = TrySpellByName(loreNames[1].Replace("'", "`")); if (id != 0) return id;
        }
        if (maxTier >= 5)
        {
            int id = TrySpellByName(loreNames[0]); if (id != 0) return id;
            id = TrySpellByName(loreNames[0].Replace("'", "\u2019")); if (id != 0) return id;
            id = TrySpellByName(loreNames[0].Replace("'", "`")); if (id != 0) return id;
            if (PlainRings.TryGetValue(element, out string? plain))
            {
                id = TrySpellByName(plain); if (id != 0) return id;
            }
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

        // None known: the cast's caller says why (ReportNoRingKnown). This is reached from every
        // element probe as well, which used to chat about rings of elements never cast.
        return 0;
    }

    // Void Magic's rings (SpellData category 641, ACE's NetherRing): Clouded Soul is level 5 (power
    // 200), Clouded Soul II and Ring of Skulls II level 7 (power 300). They have lore names only, so
    // the old "Nether Ring {tier}" lookup never found one and a Void caster never cast a ring
    // (2026-11-01).
    private static readonly string[] VoidRingsLevel7 = { "Clouded Soul II", "Ring of Skulls II" };
    private const string VoidRingLevel5 = "Clouded Soul";

    private int FindBestVoidRingSpell()
    {
        int maxTier = EffectiveMaxTier(AcSkillType.VoidMagic);
        if (maxTier >= 7)
            foreach (string name in VoidRingsLevel7)
            {
                int id = TrySpellByName(name); if (id != 0) return id;
            }
        if (maxTier >= 5)
            return TrySpellByName(VoidRingLevel5);
        return 0;
    }

    private int FindBestOffensiveSpellId(string baseName, AcSkillType skill, string? tier7Lore = null)
    {
        if (_spellManager == null) return 0;
        int maxTier = EffectiveMaxTier(skill);

        if (maxTier >= 8)
        {
            int id = TrySpellByName($"Incantation of {baseName}"); if (id != 0) return id;
            id = TrySpellByName(baseName + " VIII"); if (id != 0) return id;
        }

        for (int tier = Math.Min(maxTier, 7); tier >= 1; tier--)
        {
            // Streak/Bolt war lines have no "{base} VII" — tier-7 is a lore
            // name (e.g. Force Streak VII = "Outlander's Insolence"). Try it
            // at the tier-7 step so the highest tier still wins; Arc falls
            // through to the Roman "{base} VII" below (which exists).
            if (tier == 7 && !string.IsNullOrEmpty(tier7Lore))
            {
                int loreId = TrySpellByName(tier7Lore); if (loreId != 0) return loreId;
            }
            int id = TrySpellByName($"{baseName} {GetRomanNumeral(tier)}"); if (id != 0) return id;
        }
        return 0;
    }

    /// <summary>
    /// Combat tier ceiling. When the authoritative known-spell snapshot is
    /// cold the resolver can't trust IsSpellKnown (it lies "true" for unknown
    /// spells), so refuse to blind-pick the tier-8 Incantation — clamp to 7
    /// until the snapshot warms. A char who really knows L8 loses Incantations
    /// only during the brief cold window; one who doesn't no longer spams an
    /// uncastable L8 every fight.
    /// </summary>
    private int EffectiveMaxTier(AcSkillType skill)
    {
        if (_spellManager == null) return 0;
        int t = _spellManager.GetHighestSpellTier(skill);
        if (t > 7 && !_spellManager.IsKnownSnapshotWarm) t = 7;
        return t;
    }

    private static string GetRomanNumeral(int tier) => tier switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV",
        5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII",
        _ => "I"
    };

    public void Dispose() => _raycastSystem?.Dispose();
}

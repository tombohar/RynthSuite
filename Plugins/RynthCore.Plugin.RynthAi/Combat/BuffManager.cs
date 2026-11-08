using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager : IDisposable
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly SpellManager _spellManager;
    private readonly PlayerVitalsCache _vitals;

    private CharacterSkills? _charSkills;
    private WorldObjectCache? _worldObjectCache;

    private DateTime _lastCastAttempt = DateTime.MinValue;
    /// <summary>When buffing last sent a cast (buff, vital or refill). The buffing-coma watchdog's sign of progress.</summary>
    public DateTime LastCastAttemptAt => _lastCastAttempt;
    /// <summary>When the last heal, restam or remana spell went out (Weapons Back After Heals).</summary>
    public DateTime LastVitalCastAt => _lastVitalCastAt;
    private DateTime _lastVitalCastAt = DateTime.MinValue;
    // Throttles ChangeCombatMode(Magic) from EnsureMagicMode's "wand wielded but
    // mode != Magic" branch. Without this, when the server rejects/silently-drops
    // mode flips (e.g. wedged-target combat state), the branch fires every tick
    // (~2.5Hz) and spams AC with thousands of stance-change packets, eventually
    // crashing the client. 2026-05-25 AcActionTrace caught 256 consecutive
    // ChangeCombatMode(Magic) calls at ~400ms intervals immediately before pid
    // 39584 died.
    //
    // 2026-05-25 update: a fixed 1s cooldown only paced the spam at 1Hz (pid
    // 16656 still crashed after ~48min on engine-side AV at 0x04F24E0 ref 0x18).
    // Switched to exponential backoff that doubles wait on each consecutive
    // failed flip: 1s → 2s → 4s → 8s → 16s → 30s cap. The bot KEEPS TRYING
    // forever (per user requirement — bot must continue) but at a rate that
    // decays as failures accumulate. Counter resets to 0 as soon as
    // EnsureMagicMode is entered with mode == Magic (i.e. any successful flip
    // re-enables fast cadence for the next problem). Worst-case wedge load:
    // ~100 calls over 48min instead of ~2,880.
    private DateTime _lastBuffStanceAttempt = DateTime.MinValue;
    private int _buffStanceConsecutiveFails = 0;
    private bool _isForceRebuffing = false;
    // Set with _isForceRebuffing when the pass is the automatic batch (one buff fell
    // under RebuffSecondsRemaining), not the Force Rebuff button. The batch only tops
    // off buffs under RebuffTopOffSecondsRemaining; Force Rebuff recasts everything.
    private bool _isAutoBatchRebuff = false;
    private int _lastLivePlayerEnchantCount = -1;   // previous live registry read; -1 = none yet
    private int _pendingSpellId = 0;
    private Action<string>? _onCastResolved;

    // ARMOR/ITEM enchants only: a cast that gets ZERO chat back (unknown tier /
    // no comps — AC silently drops it) never resolves, so pending never clears
    // and the cycle wedges. Last-resort bound: abandon after this long. Self-
    // buffs do NOT use this path — they confirm via the live registry below.
    private const double NoChatResolveTimeoutMs = 5000;

    // SELF-BUFFS confirm via the live player-enchantment registry, not chat
    // (ReadPlayerEnchantments returns player buffs in real time; item enchants
    // are NOT in it). Wait SelfBuffConfirmMs for the cast to settle server-side,
    // then poll the registry; if still absent by SelfBuffGiveUpMs the tier
    // didn't take (drop a tier only when the known-spell snapshot is cold).
    private const double SelfBuffConfirmMs = 600;
    private const double SelfBuffGiveUpMs  = 2500;
    private DateTime _lastSelfBuffPollAt = DateTime.MinValue;

    // Wired by RynthAiPlugin. Invoked the instant a buff cast resolves in chat
    // (success / fizzle / hard-fail / too-busy) so the owner can clear the
    // leaked busy-count immediately instead of waiting out the 10s watchdog —
    // that post-cast stall is the entire "slow buffing" symptom.
    public void SetCastResolvedCallback(Action<string> cb) => _onCastResolved = cb;

    private class RamTimerInfo
    {
        public DateTime Expiration;
        public int SpellLevel;
        public string SpellName = "";
        // True when this is a permanent player enchantment (server reports no
        // expiry). It is a presence-only marker re-derived from the live
        // registry on every RefreshFromLiveMemory — NOT a synthetic timer and
        // NEVER persisted (a relog after death/dispel must not resurrect it).
        public bool IsPermanent;
    }

    // "Never expires" sentinel for permanent player enchantments. Far enough
    // from DateTime.MaxValue that IsBuffActive's Expiration.AddSeconds(-rebufferSec)
    // (rebufferSec ≤ 1800) can't underflow.
    private static readonly DateTime PermanentSentinel = DateTime.MaxValue.AddDays(-1);

    // Permanent-enchant families already logged this session (once-per-family
    // diagnostic so the 30s refresh doesn't spam the log).
    private readonly HashSet<int> _loggedPermanentFamilies = new();

    /// <summary>
    /// Buff timer for an enchantment on a specific equipped item (armor/weapon).
    /// </summary>
    private class ItemBuffTimerInfo
    {
        public uint ObjectId;
        public string ObjectName = "";
        public DateTime Expiration;
        public int SpellLevel;
        public string SpellName = "";
    }

    /// <summary>
    /// Timer for an item spell (armor bane / Impenetrability) cast by this plugin.
    /// Recorded immediately on cast — independent of chat parsing and enchantment hooks.
    /// </summary>
    private class ItemSpellRecord
    {
        public DateTime CastAt;
        public DateTime ExpiresAt;
        public string SpellName = "";
        public int SpellLevel;
    }

    private readonly Dictionary<int, RamTimerInfo> _ramBuffTimers = new();
    // Highest tier we've actually seen LAND for a family. Incantations (nominal
    // tier 8) land at a skill-capped lower tier; without this the upgrade rule
    // in IsBuffActive recasts forever. Updated whenever a family's timer is
    // recorded (live refresh or post-cast). Max-of-observed so it converges to
    // the character's true ceiling after the first cast.
    private readonly Dictionary<int, int> _familyAchievedTier = new();
    // Every live player enchantment per family from the last RefreshFromLiveMemory
    // (_ramBuffTimers keeps only the strongest). Lets a family read "on" when the
    // strongest entry is running out but another one at the tier we'd cast is under it:
    // a buff bot's level 8 expiring over our own fresh VI (2026-10-05). Permanent marks an
    // item-granted entry (no expiry), which CastBuffsOverItemBuffs leaves out.
    private readonly Dictionary<int, List<(int Level, DateTime Expiration, bool Permanent)>> _familyLiveEntries = new();
    // Highest tier CONFIRMED per family since load (nominal level; written on the cast's
    // confirmation, so a fizzled or refused try doesn't count). The achieved-tier cap applies
    // only once the target tier has gone through, so a family deliberately cast low first (the
    // tier-7 creature bootstrap before 8s) still upgrades, and a failed upgrade is retried.
    private readonly Dictionary<int, int> _familyAttemptedTier = new();
    /// <summary>Keyed by (objectId, spellFamily) packed as long.</summary>
    private readonly Dictionary<long, ItemBuffTimerInfo> _itemBuffTimers = new();
    /// <summary>Keyed by spell family. Tracks item-enchantment casts (armor banes, Impenetrability) directly.</summary>
    private readonly Dictionary<int, ItemSpellRecord> _itemSpellTimers = new();
    private string _buffTimerPath = "";

    // === Item-enchantment duration experiment (BuffManager_Review.md §headline #3) ===
    // Toggled via `/ra bufftest`. Stays ON until toggled OFF — captures every cast.
    public bool EnableCastRegistryDiagnostic = false;
    private readonly Queue<CastDiagnostic> _pendingDiagnostics = new();

    // Force Rebuff cycle tracking — when set, IsBuffActive consults this instead
    // of the live timer dicts so FR re-casts every spell to align all timers.
    private readonly HashSet<int> _forceRebuffCastFamilies = new();
    // The level each family CONFIRMED at in this run (the highest), written only where the family
    // joins _forceRebuffCastFamilies; an upgrade that fizzles or is refused leaves it alone.
    private readonly Dictionary<int, int> _runConfirmedLevelByFamily = new();
    // The buff cast in flight that is an upgrade (a lower level of its family was on), 0 = none.
    // A failed upgrade is not counted or rested; it is cast again at the next opportunity, paced
    // like any cast.
    private int _pendingUpgradeSpellId;

    // Consecutive SILENT no-shows of an upgrade, per family (the registry never moved / no chat for an
    // item spell). An upgrade's no-show is no strike on the family (see the no-show branches), so
    // without a bound a cast the server silently drops loops forever: an item enchant whose lower
    // record is still on the books after the armor was lost or the weapon swapped (nothing to bind
    // to), or a self-buff the server ignores. In a force/batch run that holds _isForceRebuffing, and
    // with it buffing's priority over fighting, for good. After UpgradeNoShowThreshold of them the
    // UPGRADE alone is parked for UpgradeParkCooldown: the family is not blacklisted or put in
    // _buffFailCooldownUntil, and the level it was cast over keeps refreshing as a normal cast.
    // Cleared by any confirmed cast of the family (NoteBuffConfirmed) and by Force Rebuff.
    private readonly Dictionary<int, int> _upgradeNoShowCounts = new();
    private readonly Dictionary<int, DateTime> _upgradeParkedUntil = new();
    private const int UpgradeNoShowThreshold = 3;
    private static readonly TimeSpan UpgradeParkCooldown = TimeSpan.FromMinutes(5);

    // Per-family diagnostic throttle — last reason string we logged. Suppresses
    // dup spam when IsBuffActive is polled every tick by NeedsAnyBuff + CheckAndCastSelfBuffs.
    private readonly Dictionary<int, string> _lastArmorRecastReason = new();

    // Per-family failure cooldown. A buff the server deterministically rejects
    // (no components, must specify a target, etc.) must NOT be re-picked every
    // buff cycle — sustained re-cast → sustained AC AddTextToScroll re-entry →
    // AC access-violation (the same AV class the cast-gate work targeted).
    // Hard rejections park that family for BuffFailCooldownSec; soft random
    // fizzles are NOT cooled (they should retry promptly).
    private readonly Dictionary<int, DateTime> _buffFailCooldownUntil = new();
    private const double BuffFailCooldownSec = 120.0;

    // A vital spell LEVEL the server refused for missing components (Revitalize, Heal, Stamina
    // to Mana...): that level is passed over for NoComponentsLevelSec and the next lower level
    // with components is cast instead. It used to park the whole family, then retry the same
    // top level the character had no components for (2026-10-08).
    private readonly Dictionary<int, DateTime> _noComponentsUntil = new();
    private const double NoComponentsLevelSec = 600.0;
    private bool LevelLacksComponents(int spellId)
        => _noComponentsUntil.TryGetValue(spellId, out DateTime until) && RynthClock.Now < until;

    // Per-family SILENT no-show counter. Distinct from _buffFailCooldownUntil
    // (which catches chat-explicit hard rejects). This catches the /god case:
    // skill is Trained → IsSkillUsable says yes → BuildDynamicBuffList includes
    // the buff → cast is issued → AC silently does nothing because the spell
    // isn't in the spellbook → no chat, no enchantment-added event, no _ramBuffTimers
    // entry → IsBuffActive returns false → loop forever. The existing no-show
    // branch in TickBuffing only blacklists when IsKnownSnapshotWarm == false;
    // with /god the snapshot stays warm (knowledge bits exist) so retry never
    // exits. This counter bounds the retry: after SilentNoShowThreshold misses
    // we park the family in _buffFailCooldownUntil for SilentNoShowCooldown so
    // CheckAndCastSelfBuffs skips it on subsequent cycles. Counter + cooldown
    // are cleared in OnEnchantmentAdded when the family actually lands later
    // (character learns the spell, components arrive, server-side fix, etc.).
    private readonly Dictionary<int, int> _silentNoShowCounts = new();
    private const int SilentNoShowThreshold = 2;
    private static readonly TimeSpan SilentNoShowCooldown = TimeSpan.FromMinutes(30);

    // Family → the spell id we last ISSUED a buff cast for. Lets the post-batch
    // audit re-check exactly what it tried instead of re-resolving the tier.
    private readonly Dictionary<int, int> _lastCastSpellIdByFamily = new();

    // Per-family "cast landed but the buff STILL reads inactive" strikes.
    // Distinct from both existing park mechanisms: _buffFailCooldownUntil catches
    // chat-explicit hard rejects, _silentNoShowCounts catches casts that produce
    // no confirmation at all. This catches the nastier third case — AC confirms
    // the cast, the enchantment really is on the character, and IsBuffActive
    // still returns false. Nothing in the old code bounded that: the family
    // re-triggers the auto-batch on the very next tick, and since a single buff
    // below threshold escalates to a FULL rebuff, one such family makes the bot
    // recast all ~65 spells back-to-back forever. Two consecutive unsatisfied
    // batches park the family exactly like a no-show does — time-boxed and
    // self-recovering, so a genuine transient can't shelve a buff permanently.
    private readonly Dictionary<int, int> _unsatisfiedStrikes = new();
    private const int UnsatisfiedStrikeThreshold = 2;

    // When each family's cast was issued, and what its stored expiry was at that
    // moment. The audit needs both to avoid punishing a perfectly healthy buff:
    // a refresh takes time to reach the registry, so a family cast near the end
    // of a batch can still read its OLD timer when the batch ends. Judging it
    // there produced a false strike on 'Magic Item Tinkering Expertise Self VI'
    // (observed 2026-09-02 22:34) that read active again a minute later. A family
    // is only judged once AuditSettleMs has passed since its cast, and only
    // counts as unsatisfied if its expiry never moved forward. A genuinely stuck
    // family recurs in every batch, so it still accumulates strikes across them;
    // a healthy one is simply skipped this round.
    // ── "You're too busy!" backoff ───────────────────────────────────────────
    // A refusal means AC is mid-gesture and would not accept the cast. The PARK
    // path clears _pendingSpellId and says it will "re-issue when the cast gate
    // reopens", but nothing actually waits for the gate — the next tick re-issues
    // under SpellCastIntervalMs (400ms). A cast gesture runs ~2s, so under load
    // that is ~2 refusals/second for as long as the condition lasts: 675 of them
    // in two episodes on 2026-09-03, with CanCastNow reporting clear and
    // BusyCount 0 throughout (so neither existing gate saw it).
    //
    // Every refusal also pushes a chat line through AC's text pipeline, which is
    // the documented AddTextToScroll re-entry AV pressure this file already
    // guards against elsewhere — so a refusal storm is not merely wasted work.
    //
    // The refusal is authoritative: AC just told us it is busy. Back off with an
    // exponential delay and reset the moment a cast is accepted, so a transient
    // busy window costs a few attempts instead of thousands.
    // ── Server-paced cast serialization (UseDone / GameEvent 0x01C7) ────────
    // The server sends UseDone when it FINISHES an action — completed, or refused
    // with an error (0x1D = YoureTooBusy). It is the authoritative ACE
    // Player.IsBusy lifecycle signal, and the engine already exposes a monotonic
    // counter for it. CombatManager has serialized its casts on this since it was
    // added; BuffManager never did, and paced instead on a flat
    // SpellCastIntervalMs (400ms) measured from cast ISSUE.
    //
    // That interval is shorter than a cast actually takes. Measured over 612
    // paired buff casts on 2026-09-03, refusal rate by gap since the previous
    // issue: 400-450ms → 54%, 450-600ms → 100%, 600-900ms → 33%, >900ms → 9.5%,
    // with "You cast X" arriving at a median 652ms and "too busy" at 307ms. So
    // the next cast was routinely issued while the previous one was still
    // resolving. The local CanCastNow gate cannot catch this: it reads
    // CMotionInterp's pending-motions head, which empties when the LOCAL
    // animation ends, well before the server is done.
    //
    // Waiting for UseDone is self-tuning and needs no per-spell duration table —
    // a slower tier-8 cast simply produces a later UseDone. Bounded by a timeout
    // so a dropped event can't wedge buffing, and inert (never waits) on an
    // engine build without UseDone observation.
    //
    // 2026-10-05 (Drakkon, DreamWeave): "finished" used to be ANY new UseDone, and the refusal
    // of the next cast is a UseDone too (err 0x1D), so under a refusal storm the wait cleared at
    // once. The wait now belongs to the cast: see CastTracker.AwaitingServer.
    private const double CastResolutionTimeoutMs = 2500; // from the send; later when the incantation is late
    // Sends, incantations, results and the server's UseDones for the casts this class sends.
    private readonly CastTracker _cast;

    /// <summary>Clock for the cast-safety timing (give-up, server wait, held-cast nudge, stance
    /// settle). The host tests replace it; everything else here reads RynthClock.Now.</summary>
    internal Func<DateTime> Clock { get => _cast.Clock; set => _cast.Clock = value; }
    private DateTime Now => _cast.Now;

    // Buff cast chaining (Combat/BuffCastChain.cs): the next buff of a pass goes out
    // on the last buff's UseDone(0) without waiting for the recoil animation; refusals counted.
    private readonly BuffCastChain _chain = new();
    internal BuffCastChain Chain => _chain;
    /// <summary>This tick skipped the animation gate (the fast path): only a buff cast may go out.</summary>
    private bool _fastChainTick;
    private string _fastChainWhy = "";

    // Held-cast nudge (2026-10-05): a cast the server holds open (incantation, no result) is
    // only let go by a movement packet; StopCompletely from a standing character may send none.
    // Once per held cast: autorun on and straight off again, then no cast for this long.
    private const double NudgeHoldMs = 2000;
    /// <summary>Movement nudges sent for held casts this session (/ra status, tests).</summary>
    internal int CastNudges { get; private set; }

    private int _busyRefusalStreak;
    private DateTime _busyBackoffUntil = DateTime.MinValue;
    private const double BusyBackoffBaseMs = 500;
    private const double BusyBackoffMaxMs  = 8000;
    // During a too-busy backoff a heal may still retry, but no faster than this.
    private const double BusyHealRetryMs   = 1500;
    // Refusals in a row (with backoff, about 3.5 s or more) before StopCompletely: longer than any real gesture.
    private const int BusyStopAtStreak     = 4;

    // "Not enough mana" refusal (see OnChatWindowText): the refused family rests
    // briefly and buffing pauses; neither is a silent-no-show strike.
    private DateTime _lowManaPauseUntil = DateTime.MinValue;
    private const double LowManaBuffPauseMs = 5000;
    private const double LowManaHealRestSec = 2;
    private const double LowManaFamilyRestSec = 15;

    // ── Mana reserve (2026-10-11) ──
    // A void mage at 1/383 mana kept casting Corruption at Rampagers and never Stamina to Mana.
    // Combat drained mana under what Stamina to Mana itself costs; the server refused it ("You
    // don't have enough Mana"), which rested the family 15 s; no mana potion was listed, so no
    // vitals flag held the arbiter, and combat spent every point that regenerated on attacks
    // that were refused too. Now, for a character that fights with magic:
    //  - the refill line is the larger of Get Mana At (Top Off Mana idle) and ManaReserveFactor
    //    times the cost of the best Stamina to Mana it can cast (ManaUnderLine);
    //  - under it, while Stamina to Mana or a listed mana potion can refill, vitals hold the tick
    //    (WantsManaNow, part of WantsVitalRecharge, read every tick so combat can't take the
    //    tick that follows a UseDone) and combat holds its casts (OffensiveCastHold);
    //  - under Stamina to Mana's cost, the highest level it can pay for goes; with none, mana
    //    regenerates while attacks wait, and a vital refused for mana rests only LowManaHealRestSec.
    // No peace-stance switch to regenerate faster: ACE's stance modifier skips mana
    // (Creature.GetStanceMod returns 1.0 for MaxMana), and the switch back would cost the swap.
    internal const double ManaReserveFactor = 1.5;
    private const double ManaUnavailableMs = 5000;
    private const double ManaRefusalMemoryMs = 5000;
    private DateTime _manaUnavailableUntil = DateTime.MinValue;
    private DateTime _lastManaRefusalAt = DateTime.MinValue;
    private bool _manaStarvedLogged;
    private int _topS2MId;
    private DateTime _topS2MAt = DateTime.MinValue;

    /// <summary>Tests: a spell's mana cost instead of the dat's (0 = unknown). FakeHost.Reset sets
    /// it to "unknown" so tests don't depend on a portal.dat on the PC.</summary>
    internal static Func<int, int>? ManaCostOverride;

    /// <summary>Tests: whether a monster is within Monster Range (else CombatManager decides).</summary>
    internal Func<bool>? InCombatOverride { get; set; }

    private bool InCombatNow()
    {
        bool beingHit = HealthFellRecently();   // first: it keeps its record on every pass
        return InCombatOverride?.Invoke()
            ?? (_combatManager?.HasCloseThreat(System.Math.Max(1, _settings.MonsterRange)) == true || beingHit);
    }

    // Health falling means a fight, whatever the scan says. A mob behind a wall isn't in the scan,
    // so a character it was hitting read as idle: every hit put him under Top Off HP, the heal took
    // the tick, and nothing else ran until it stopped (2026-11-06: 45 s of kits with the mob hitting
    // him once a second). With the fight lines he heals at Heal At and otherwise gets on with it.
    private const double HealthFellCountsMs = 5000;
    private int _healthFellLastPct = -1;
    private DateTime _healthFellAt = DateTime.MinValue;

    private bool HealthFellRecently()
    {
        int pct = _vitals.HealthPct;
        DateTime now = RynthClock.Now;
        if (_healthFellLastPct >= 0 && pct < _healthFellLastPct) _healthFellAt = now;
        _healthFellLastPct = pct;
        return _healthFellAt != DateTime.MinValue && (now - _healthFellAt).TotalMilliseconds < HealthFellCountsMs;
    }

    // ── Buffs wait while hurt in a fight (2026-11-07) ──
    // Drakkon cast four weapon auras (2.4 s each) one after another with a golem's Force Bolts hitting him
    // and his health under Heal At; the emergency heal then waited for the last aura's cast and he died.
    // Under Heal At in a fight no new buff cast starts (one already sent finishes) and the arbiter doesn't
    // give Buffing the tick for buffs: the heals (vital recharge keeps its slot) and combat go first. The
    // buffs carry on once health is back over Heal At or the fight is over.
    private bool _buffHurtWaitOn;

    /// <summary>True while health is under Heal At in a fight: no new buff cast. Logs the start and end of each wait.</summary>
    public bool BuffsWaitForHealth()
    {
        bool wait = _settings.HealAt > 0 && _vitals.HealthPct > 0 && _vitals.HealthPct < _settings.HealAt && InCombatNow();
        if (wait != _buffHurtWaitOn)
        {
            _host.Log(wait
                ? $"[Buff] buffs wait: health {_vitals.HealthPct}% is under Heal At {_settings.HealAt}% in a fight; heals and combat go first."
                : $"[Buff] buffs go on: health {_vitals.HealthPct}%, {(InCombatNow() ? "back over Heal At" : "the fight is over")}.");
            _buffHurtWaitOn = wait;
        }
        return wait;
    }

    // ── Rebuff When Idle (RebuffWhenIdle + RebuffIdleMarginMinutes; VTank's "idlebufftopoff") ──
    // The checkbox was saved and shown since the first version but nothing read it (a player asked,
    // 2026-10-30, whether "buff only when idle" was implemented). What it means (the owner): an earlier
    // timer for idle moments. The regular rebuff is unchanged: a buff at or under Rebuff With
    // (RebuffSecondsRemaining), or dispelled, is recast as always, in a fight or not. With the option
    // on and the bot idle (IdleForTopOff: no fight target, no monster within Monster Range, no corpse
    // to loot, no salvage), a rebuff run also starts once a buff is at or under Rebuff With plus the
    // margin (default 2 minutes). Such an early run never starts in a fight, and one under way stops
    // at the next safe point (no cast in flight) when the bot stops being idle; the regular rules take
    // over. Margin 0: nothing added. Force Buff, heals and vitals are not touched.

    /// <summary>The bot is idle enough for an early top-off (set by RynthAiPlugin.IdleForBuffTopOff;
    /// tests set it). Null = never: no early top-off.</summary>
    internal Func<bool>? IdleForTopOff { get; set; }

    /// <summary>Rebuff When Idle's margin in seconds (0 when off or set to 0).</summary>
    internal int IdleMarginSec => _settings.RebuffWhenIdle ? Math.Max(0, _settings.RebuffIdleMarginMinutes) * 60 : 0;

    /// <summary>An early top-off may run now: the option is on with a margin, and the bot is idle.</summary>
    internal bool IdleTopOffNow()
    {
        if (IdleMarginSec <= 0 || IdleForTopOff == null) return false;
        try { return IdleForTopOff(); } catch { return false; }
    }

    /// <summary>The line a buff is rebuffed at now: Rebuff With, plus the idle margin while an idle
    /// top-off may run.</summary>
    internal int RebuffLineSec() => _settings.RebuffSecondsRemaining + (IdleTopOffNow() ? IdleMarginSec : 0);

    /// <summary>The rebuff run under way was started by the idle margin alone (nothing was at or under
    /// Rebuff With): it stops when the bot stops being idle.</summary>
    private bool _isIdleTopOffRun;

    /// <summary>The line an automatic run tops off under (with RebuffTopOffSecondsRemaining): Rebuff With,
    /// plus the margin for an early top-off run, so the buff that started it is always in it.</summary>
    private int BatchRebuffLineSec => _settings.RebuffSecondsRemaining + (_isIdleTopOffRun ? IdleMarginSec : 0);

    /// <summary>Early top-off runs started (tests, status).</summary>
    internal int IdleTopOffRuns { get; private set; }

    /// <summary>An early top-off run stops (a fight, a corpse, salvage): the regular rules take over.</summary>
    private void EndIdleTopOffRun()
    {
        _isForceRebuffing = false;
        _isAutoBatchRebuff = false;
        _isIdleTopOffRun = false;
        ClearRunFamilies();
        _chain.PassEnd("idle top-off stopped", s => _host.Log(s));
        _host.Log("[Buff] Rebuff When Idle: the bot is no longer idle (a fight, a corpse or salvage): the early top-off stops; buffs under Rebuff With are still rebuffed as usual.");
    }

    /// <summary>
    /// A spell's mana cost: its BaseMana from the portal.dat SpellTable, the most it can cost
    /// (ACE Creature.CalculateManaUsage; Mana Conversion only lowers it on a skill roll, so the
    /// base is the one number that is sure to be enough). No dat: an estimate from the spell's
    /// level. 0 = unknown.
    /// </summary>
    internal static int SpellManaCost(int spellId)
    {
        if (spellId == 0) return 0;
        if (ManaCostOverride != null) return Math.Max(0, ManaCostOverride(spellId));
        if (ComponentDatabase.TryGetSpellBaseMana(spellId, out int mana)) return mana;
        int level = SpellTableStub.GetById(spellId)?.Level ?? 0;
        return EstimatedManaCost(level);
    }

    /// <summary>Retail's base mana by spell level (I 10 ... VI 50, VII 70, VIII 80; the dat can differ
    /// per spell). Used only when the dat can't be read.</summary>
    internal static int EstimatedManaCost(int level) => level switch
    {
        <= 0 => 0,
        <= 7 => level * 10,
        _ => 80,
    };

    /// <summary>The Stamina to Mana level vitals would cast now (0 = none: unknown, or Life Magic
    /// not usable). Cached for a second: the arbiter asks every tick.</summary>
    private int TopStaminaToManaId()
    {
        if ((RynthClock.Now - _topS2MAt).TotalMilliseconds < 1000) return _topS2MId;
        _topS2MAt = RynthClock.Now;
        _topS2MId = IsSkillUsable(AcSkillType.LifeMagic)
            ? _spellManager.GetDynamicSelfBuffId("Stamina to Mana Self", AcSkillType.LifeMagic, LevelLacksComponents)
            : 0;
        return _topS2MId;
    }

    /// <summary>The mana the reserve keeps for Stamina to Mana: ManaReserveFactor x its cost, never
    /// above halfway between the cost and max mana (a small pool would refill when nearly full).
    /// 0 = no reserve (no Stamina to Mana, cost unknown, or more than max mana).</summary>
    internal int ManaReserve()
    {
        int cost = SpellManaCost(TopStaminaToManaId());
        int max = (int)_vitals.MaxMana;
        if (cost <= 0 || max <= 0 || cost >= max) return 0;
        int reserve = (int)Math.Ceiling(cost * ManaReserveFactor);
        return Math.Min(reserve, cost + (max - cost) / 2);
    }

    /// <summary>Logged once per stretch under Get Mana At: buffing goes on because nothing can refill
    /// mana (CheckBuffs' mana floor).</summary>
    private bool _manaFloorNoRefillLogged;

    /// <summary>Heals the mana floor keeps mana for when nothing can refill it.</summary>
    internal const int ManaFloorHeals = 2;

    /// <summary>
    /// The mana the Get Mana At floor keeps when nothing can refill it: ManaFloorHeals casts of the
    /// Heal Self vitals would cast now (0 when none is known or Life Magic isn't usable).
    /// </summary>
    internal int ManaFloorHealReserve()
    {
        int heal = IsSkillUsable(AcSkillType.LifeMagic)
            ? _spellManager.GetDynamicSelfBuffId("Heal Self", AcSkillType.LifeMagic, LevelLacksComponents)
            : 0;
        int cost = heal != 0 ? SpellManaCost(heal) : 0;
        return cost > 0 ? ManaFloorHeals * cost : 0;
    }

    /// <summary>True when no mana potion could be drunk at all (Use Potions off, or none listed or
    /// in the pack); false while one exists but waits (drunk under 2.5 s ago, a cast holds it).</summary>
    private bool NoManaPotionToDrink()
    {
        if (!_settings.UsePotions || _worldObjectCache == null) return true;
        if (HasListedPotion("ManaPotion")) return false;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
            if (!IsKitName(wo.Name) && string.Equals(RynthCore.Plugin.RynthAi.LegacyUi.LegacyWeaponsUi.PotionType(wo), "ManaPotion", StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// <summary>Combat fights with magic (CombatManager's chosen weapon is a wand; before it chose,
    /// the stance). A melee character's attacks don't spend mana, so the reserve doesn't hold them.</summary>
    private bool FightsWithMagic() => _combatManager?.FightsWithMagic ?? CurrentCombatMode == CombatMode.Magic;

    /// <summary>Mana is under the line vitals refill at: the % line (Get Mana At in combat, Top Off
    /// Mana idle; 0 = off) or the reserve. <paramref name="line"/>: the line in points.</summary>
    internal bool ManaUnderLine(bool inCombat, out int line)
    {
        line = 0;
        int pctLine = inCombat ? _settings.GetManaAt : _settings.TopOffMana;
        int max = (int)_vitals.MaxMana;
        if (pctLine <= 0 || max <= 0) return false;
        line = (int)Math.Ceiling(max * pctLine / 100.0);
        bool under = _vitals.ManaPct < pctLine;
        // Every character, not only casters (2026-10-14): a melee character's buff chain spends mana
        // too (Drakkon: 4% after a run of auras, under Stamina to Mana's own cost, so nothing could
        // refill it and buffing sat waiting for regeneration).
        int reserve = ManaReserve();
        if (reserve > line) line = reserve;
        if (reserve > 0 && _vitals.CurrentMana < reserve) under = true;
        return under;
    }

    /// <summary>Stamina to Mana (not parked by a hard refusal; the short rest after a mana refusal
    /// doesn't count) or a listed mana potion can refill mana.</summary>
    private bool ManaRefillPossible()
    {
        if (_settings.UsePotions && HasListedPotion("ManaPotion")) return true;
        int id = TopStaminaToManaId();
        if (id == 0 || !StaminaAllowsStaminaToMana()) return false;
        int family = SpellTableStub.GetById(id)?.Family ?? 0;
        return !(family != 0 && _buffFailCooldownUntil.TryGetValue(family, out DateTime until)
                 && (until - RynthClock.Now).TotalSeconds > LowManaHealRestSec);
    }

    /// <summary>Stamina lets Stamina to Mana go: over 15%, or a stamina kit or listed stamina potion
    /// can bring it there. Not Revitalize: it costs mana itself, and trading mana for stamina and
    /// back while attacks wait could keep a caster from attacking for minutes.</summary>
    private bool StaminaAllowsStaminaToMana() => _vitals.StaminaPct > StaminaToManaFloorPct()
        || BestPackStaminaKit() != null
        || (_settings.UsePotions && HasListedPotion("StaminaPotion", "Stamina"));

    /// <summary>
    /// Stamina to Mana goes only over this stamina %: Re-stam At (when on), at least 15. Under it,
    /// stamina is refilled first (kit, potion, Revitalize). Was a flat 15%: each Stamina to Mana took
    /// 110-126 stamina and Drakkon's stamina was run nearly out before anything refilled it (2026-10-14).
    /// </summary>
    internal int StaminaToManaFloorPct() => Math.Max(15, _settings.RestamAt);

    /// <summary>
    /// Mana can't pay for Stamina to Mana: under the cost of its lowest known level when costs are
    /// known (the highest level it can pay for is cast first, see AttemptVitalCast), or refused
    /// for mana in the last ManaRefusalMemoryMs when they aren't.
    /// </summary>
    internal bool ManaStarved(out int cost, out string spellName)
    {
        int id = TopStaminaToManaId();
        cost = SpellManaCost(id);
        spellName = id != 0 ? SpellTableStub.GetById(id)?.Name ?? "Stamina to Mana" : "Stamina to Mana";
        if (id == 0 || _vitals.MaxMana == 0) return false;
        if (cost > 0) return _vitals.CurrentMana < cost && AffordableVitalId("Stamina to Mana Self") == 0;
        return (RynthClock.Now - _lastManaRefusalAt).TotalMilliseconds < ManaRefusalMemoryMs;
    }

    /// <summary>The highest level of a vital spell whose known cost current mana can pay (a level
    /// with an unknown cost counts as payable). 0 = none.</summary>
    private int AffordableVitalId(string baseName)
    {
        uint cur = _vitals.CurrentMana;
        return _spellManager.GetDynamicSelfBuffId(baseName, AcSkillType.LifeMagic,
            id => LevelLacksComponents(id) || SpellManaCost(id) > cur);
    }

    /// <summary>
    /// In combat, a caster's mana under the refill line with something able to refill it (now, or
    /// once mana regenerates to Stamina to Mana's cost): vitals want the tick. Read by
    /// WantsVitalRecharge every tick, like WantsHealNow, so it doesn't depend on CheckVitals having
    /// run (it doesn't while a gesture animates or the client is busy). Backs off ManaUnavailableMs
    /// after a check that found nothing able to refill.
    /// </summary>
    internal bool WantsManaNow()
    {
        if (!_settings.IsMacroRunning || _vitals.MaxMana == 0) return false;
        if (RynthClock.Now < _manaUnavailableUntil) return false;
        if (!InCombatNow() || !FightsWithMagic()) return false;
        return ManaUnderLine(true, out _) && ManaRefillPossible();
    }

    /// <summary>
    /// For CombatManager's script hook, before <see cref="OffensiveCastHold"/>: false when
    /// <paramref name="spellId"/> costs more than the character's max mana, so the cost hold would
    /// never lift. Same cost source and the same guards as OffensiveCastHold (no hold, no veto:
    /// macro off, or max mana unknown); an unknown cost (0) is affordable.
    /// </summary>
    internal bool CanEverAfford(int spellId)
    {
        if (!_settings.IsMacroRunning || _vitals.MaxMana == 0) return true;
        int cost = SpellManaCost(spellId);
        return cost <= 0 || cost <= _vitals.MaxMana;
    }

    /// <summary>
    /// For CombatManager, before an attack or debuff cast: a reason to hold it, or null. Held when
    /// mana is under the spell's own cost (it would be refused), or under the refill line while
    /// vitals can refill it (Stamina to Mana goes first).
    /// </summary>
    internal string? OffensiveCastHold(int spellId)
    {
        if (!_settings.IsMacroRunning || _vitals.MaxMana == 0) return null;
        uint cur = _vitals.CurrentMana;
        int cost = SpellManaCost(spellId);
        if (cost > 0 && cur < cost)
            return $"mana {cur}/{_vitals.MaxMana} is under {SpellTableStub.GetById(spellId)?.Name ?? "the spell"}'s cost ({cost})";
        if (WantsManaNow())
        {
            ManaUnderLine(true, out int line);
            return $"mana {cur}/{_vitals.MaxMana} is under the reserve ({line}): Stamina to Mana goes first";
        }
        return null;
    }

    private DateTime _buffManaWaitLoggedAt = DateTime.MinValue;
    // Since when buffs have waited for Stamina to Mana that can't go yet (BuffWaitsForMana).
    private DateTime _reserveWaitSince = DateTime.MinValue;
    private DateTime _reserveOverLoggedAt = DateTime.MinValue;
    /// <summary>The longest buffs wait under the reserve for a Stamina to Mana that can't go yet
    /// (stamina under its line with nothing to refill it): then the buff goes, as before.</summary>
    internal const double ReserveWaitMaxSec = 60;

    // A mana reading that does not move from one buff cast to the next: every buff costs mana, so
    // the reading is stale and the floor and the reserve would judge a number that is gone. Drakkon's
    // rebuff after a death (2026-10-15) sent a dozen buffs with neither of them holding, then read
    // 5%. After StuckManaCasts casts at the same reading, buffs wait for it to move (at most
    // StuckManaHoldSec).
    private uint _lastBuffCastMana = uint.MaxValue;
    private int _sameManaCasts;
    private DateTime _stuckManaSince = DateTime.MinValue;
    internal const int StuckManaCasts = 2;
    internal const double StuckManaHoldSec = 30;

    /// <summary>Notes the mana reading a buff cast goes out at (see _lastBuffCastMana).</summary>
    internal void NoteBuffCastMana(int cost)
    {
        uint cur = _vitals.CurrentMana;
        if (cost > 0 && cur == _lastBuffCastMana && cur < _vitals.MaxMana)
        {
            _sameManaCasts++;
            if (_sameManaCasts == StuckManaCasts)
            {
                _stuckManaSince = RynthClock.Now;
                _host.Log($"[RynthAi] Vitals: the mana reading has not moved over {StuckManaCasts + 1} buff casts ({cur}/{_vitals.MaxMana}); buffs wait for a fresh reading (up to {StuckManaHoldSec:0} s).");
            }
        }
        else
            _sameManaCasts = 0;
        _lastBuffCastMana = cur;
    }

    /// <summary>True while buffs wait for a stale mana reading to move (NoteBuffCastMana).</summary>
    internal bool ManaReadingStuck()
    {
        if (_sameManaCasts < StuckManaCasts) return false;
        if (_vitals.CurrentMana != _lastBuffCastMana
            || (RynthClock.Now - _stuckManaSince).TotalSeconds >= StuckManaHoldSec)
        {
            _host.Log($"[RynthAi] Vitals: mana reading {(_vitals.CurrentMana != _lastBuffCastMana ? "moved" : "still the same after " + StuckManaHoldSec.ToString("0") + " s")}: {_lastBuffCastMana} -> {_vitals.CurrentMana}/{_vitals.MaxMana}; buffs go on.");
            _sameManaCasts = 0;
            _lastBuffCastMana = uint.MaxValue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Before a buff: true when it waits for mana. Casting it would leave mana under the reserve
    /// (ManaReserve, from Stamina to Mana's cost) and Stamina to Mana (or a mana potion) is cast
    /// now instead; or mana can't pay for the buff or for Stamina to Mana, and buffing waits.
    /// </summary>
    internal bool BuffWaitsForMana(int spellId, string buffName)
    {
        if (!BuffNeedsManaFirst(spellId, out bool cantPay, out int cost)) { _reserveWaitSince = DateTime.MinValue; return false; }
        uint cur = _vitals.CurrentMana;

        _vitalWhy = "";
        if ((StaminaAllowsStaminaToMana() && AttemptVitalCast("Stamina to Mana Self")) || AttemptPotion("ManaPotion"))
        {
            _isRechargingMana = true;
            _reserveWaitSince = DateTime.MinValue;
            LastBuffSkipReason = $"mana {cur}/{_vitals.MaxMana} before {buffName}: Stamina to Mana first";
            return true;
        }
        if (!cantPay)
        {
            // Under the reserve after it and Stamina to Mana can't go right now (stamina under its line
            // with nothing to refill it, the server finishing a cast). Spending on regardless is how a
            // rebuff ran mana under Stamina to Mana's own cost (2026-10-15): wait for it instead, up to
            // ReserveWaitMaxSec. With no Stamina to Mana at all nothing refills: the buff goes.
            if (TopStaminaToManaId() == 0) return false;
            if (_reserveWaitSince == DateTime.MinValue) _reserveWaitSince = RynthClock.Now;
            if ((RynthClock.Now - _reserveWaitSince).TotalSeconds >= ReserveWaitMaxSec)
            {
                if ((RynthClock.Now - _reserveOverLoggedAt).TotalSeconds >= 30)
                {
                    _reserveOverLoggedAt = RynthClock.Now;
                    _host.Log($"[RynthAi] Vitals: {buffName} goes under the mana reserve ({cur}/{_vitals.MaxMana}, reserve {ManaReserve()}): Stamina to Mana could not go for {ReserveWaitMaxSec:0} s ({Why(_vitalWhy)}).");
                }
                return false;
            }
            LastBuffSkipReason = $"mana {cur}/{_vitals.MaxMana}: {buffName} would leave it under the reserve ({ManaReserve()}); waiting for Stamina to Mana";
            if ((RynthClock.Now - _buffManaWaitLoggedAt).TotalSeconds >= 30)
            {
                _buffManaWaitLoggedAt = RynthClock.Now;
                _host.Log($"[RynthAi] Vitals: buffing waits: {LastBuffSkipReason} (Stamina to Mana: {Why(_vitalWhy)})");
            }
            return true;
        }
        LastBuffSkipReason = $"mana {cur}/{_vitals.MaxMana} is under {buffName}'s cost ({cost}); waiting for regen";
        if ((RynthClock.Now - _buffManaWaitLoggedAt).TotalSeconds >= 30)
        {
            _buffManaWaitLoggedAt = RynthClock.Now;
            _host.Log($"[RynthAi] Vitals: buffing waits: {LastBuffSkipReason} (Stamina to Mana: {Why(_vitalWhy)})");
        }
        return true;
    }

    /// <summary>The reserve test BuffWaitsForMana acts on, with no side effect: true when mana can't
    /// pay for the buff (<paramref name="cantPay"/>) or would be under the reserve after it.</summary>
    private bool BuffNeedsManaFirst(int spellId, out bool cantPay, out int cost)
    {
        cantPay = false;
        cost = 0;
        if (_vitals.MaxMana == 0) return false;
        int reserve = ManaReserve();
        cost = SpellManaCost(spellId);
        uint cur = _vitals.CurrentMana;
        cantPay = cost > 0 && cur < cost;
        bool underReserveAfter = reserve > 0 && (long)cur - Math.Max(cost, 0) < reserve;
        return cantPay || underReserveAfter;
    }

    private readonly Dictionary<int, DateTime> _castIssuedAtByFamily = new();
    private readonly Dictionary<int, DateTime> _expiryAtCastByFamily = new();
    private const double AuditSettleMs = 15_000.0;

    private sealed class RegistrySnapshot
    {
        public uint OwnerId;
        public string OwnerName = "";
        public bool IsPlayer;
        public uint[] SpellIds = Array.Empty<uint>();
        public double[] ExpiryTimes = Array.Empty<double>();
        public int Count;
        public double ServerTime;
    }

    private sealed class CastDiagnostic
    {
        public DateTime CastedAt;
        public string SpellName = "";
        public int SpellId;
        public int SpellFamily;
        public List<RegistrySnapshot> PreSnapshots = new();
    }

    private bool _isRechargingMana = false;
    private bool _isRechargingStamina = false;
    private bool _isHealingSelf = false;

    // Pre-buff combat teardown: one-shot flag set the first tick we issue
    // UseObject(wand) while in a physical combat mode. Reset on reaching Magic.
    // Without the teardown, AC sees the equip request while a melee/missile
    // attack is still pending server-side and the resulting "you can only
    // move or use one item at a time" notice can wedge all item actions
    // until relog.
    private bool _combatTeardownDoneForCurrentBuffCycle;

    // Wield gate: suppress duplicate UseObject(wand) calls while a prior
    // equip is in flight. Cleared the moment the wielded check confirms;
    // if it never does, the cooldown short-circuits further retries so we
    // don't compound the same desync the missing CancelAttack causes.
    private int _pendingWieldId;
    private DateTime _pendingWieldAt = DateTime.MinValue;
    private DateTime _wieldCooldownUntil = DateTime.MinValue;
    private const double WieldResolveTimeoutMs = 2500;
    private const double WieldCooldownMs = 5000;

    // FIX (2026-06-24, bow->wand buff swap): stock ACE refuses to wield a Held-slot wand
    // while the bow is in the main hand and does NOT auto-dequip it (CheckWeaponCollision),
    // so the bare UseObject(wand) below never confirmed -> permanent buffing coma. We must
    // stow the bow into a capacity-verified open pack FIRST (AutoCram pattern), then wield
    // the wand. All paths are bounded so a stale/no-op dequip or unwieldable wand can never
    // loop forever; on exhaustion the buff family is parked and the bot fights unbuffed.
    // The wand swap, as fast as the server allows (2026-10-22, HandSwapSequence): the off hand into
    // a pack, then the main hand, then the wand wielded into the Held slot, each the moment the
    // server's wielded list says the last step landed. One claim of the swap gate is held for all
    // of it. Before, Drakkon's two Hand Wraps -> Sceptre took 7.2 s to the first cast: each pack
    // move held the next step the gate's flat 3 s, and the client's own use of the wand (the route
    // tried first) takes the main hand first, which costs the off hand a second stance change.
    // The why, step by step, is at the top of HandSwapSequence.
    private readonly HandSwapSequence _wandSwap = new();
    private bool _wandSwapOpen;            // started and not yet finished (its log line and the gate's release are owed)
    private int _wandSwapWandId;
    private bool _wandSwapForBuff;
    private int _wandSwapMainAtStart;      // the main hand's weapon when it began (put back if the swap fails)
    private int _wandSwapLastMoved;        // its last pack move or wield: the gate holds the next swap after it
    private DateTime _wandSwapTouchedAt = DateTime.MinValue;   // EnsureMagicMode last asked for it
    // Not asked for this long (the heal or buff no longer needs it): the swap stops, not a failure.
    // Longer than the pacer holds the vitals step for our own peace request (ModeMaxMs 2 s).
    private const double WandSwapAbandonMs = 3000;
    // The wand went on in a combat mode: the server switches to Magic itself (ACE TryShuffleStance),
    // after the stance change of the last unwield. Our own ChangeCombatMode(Magic) waits this long for
    // it: a second stance change queued behind the server's is how a cast gets held open.
    private DateTime _serverMagicDueUntil = DateTime.MinValue;
    private const double ServerMagicWaitMs = 2500;
    // A swap that went through peace just put the wand in hand: Magic is asked for at once (see the request).
    private DateTime _magicAfterPeaceUntil = DateTime.MinValue;
    private const double MagicAfterPeaceMs = 2500;
    /// <summary>The last wand swap: how long it took and its steps with the server's answer times.</summary>
    internal double LastWandSwapMs { get; private set; }
    internal string LastWandSwapTrail { get; private set; } = "";
    private int _wieldGateFailCount;
    private const int WieldGateFailMax = 3;
    private bool _wandSwapExhausted;   // per-EnsureMagicMode(forBuff) signal: swap can't succeed now
    // Per-EnsureMagicMode signal for EVERY caller (vitals included): the switch to Magic
    // can't complete right now. A vital cast used to report "handled" here, so the potion
    // after it never ran and the heal flag held Buffing over combat - an archer with a
    // full pack stood still at low health, drinking nothing and not shooting back.
    private bool _magicModeBlocked;
    // After a blocked switch, vital casts skip the swap for this long (kits and potions
    // still run), so the failing swap isn't retried and logged every tick.
    private DateTime _vitalSwapBlockedUntil = DateTime.MinValue;
    private const double VitalSwapBlockedRetrySec = 10;

    // Shared cross-subsystem weapon-swap serializer (set by RynthAiPlugin).
    // Prevents the buff wand-equip from racing CombatManager's weapon equip.
    private WeaponSwapGate? _weaponSwapGate;
    public void SetWeaponSwapGate(WeaponSwapGate gate) => _weaponSwapGate = gate;

    // CombatManager handle (set by RynthAiPlugin after CombatManager is built).
    // CheckVitals consults HasCloseThreat(MonsterRange) to pick between the
    // in-combat thresholds (HealAt / RestamAt / GetManaAt) and the idle top-off
    // thresholds (TopOffHP / TopOffStam / TopOffMana). Null = treat as idle.
    private CombatManager? _combatManager;
    public void SetCombatManager(CombatManager cm) => _combatManager = cm;

    /// <summary>Live combat mode read from AC each access — never drifts if OnCombatModeChange is missed.</summary>
    public int CurrentCombatMode =>
        _host.HasGetCurrentCombatMode ? _host.GetCurrentCombatMode() : CombatMode.NonCombat;

    /// <summary>Client busy count — when > 0, don't send any game actions (but see
    /// <see cref="BusyHoldsHeal"/>: a heal goes ahead of a leftover count).</summary>
    public int BusyCount
    {
        get => _busyCount;
        set
        {
            if (value > 0 && _busyCount <= 0) _busyRoseAt = Now;   // the heal grace counts from the rise (BusyHoldsHeal)
            _busyCount = value;
            _busyGate.NoteCount(value, UseDoneSeqNow());
        }
    }
    private int _busyCount;
    // When the busy count last rose from 0 (the heal grace in BusyHoldsHeal).
    private DateTime _busyRoseAt = DateTime.MinValue;

    // A heal and a leftover busy count (2026-10-05, a mage's heals "sometimes several seconds
    // late"): a war or void cast raises the plugin's busy count, and AC lowers its own count
    // inline when the server's UseDone arrives, where the busy hook can't see it, so the plugin's
    // count sits at 1 until CheckBusyTimeout force-clears it 5 s after it rose (CombatBusyGate).
    // Combat checks for that leftover before each attack. A heal never did: once health crossed a
    // line the arbiter gave the tick to vitals, combat stopped running, nobody cleared the count,
    // and the heal waited out the rest of the 5 s. How late depended on how long before the drop
    // the last war spell went out. Heals now use the same rule combat does.
    private readonly CombatBusyGate _busyGate = new();
    private Action<string>? _onStaleBusy;
    /// <summary>Resets the plugin's shared busy mirror when a heal finds the count is a leftover
    /// (the same callback combat uses). Set by RynthAiPlugin.</summary>
    public void SetStaleBusyCallback(Action<string> cb) => _onStaleBusy = cb;

    /// <summary>
    /// True while the busy count should hold a heal: it is positive and not a leftover. A leftover
    /// (a UseDone arrived after the count last rose and no cast gesture is animating: the server
    /// finished every action behind it) is cleared here, as combat does, and lets the heal go.
    /// So is a count up ActionPacer.HealGraceMs since it rose (the heal grace, below).
    /// </summary>
    internal bool BusyHoldsHeal()
    {
        if (BusyCount <= 0) return false;
        bool gesture = _host.HasGetCastBusyState && !_host.CanCastNow;
        string why;
        if (_busyGate.IsStale(BusyCount, _host.HasUseDoneSeq, UseDoneSeqNow(), gesture))
            why = "the server finished the action (UseDone)";
        else
        {
            // The heal grace: the count can't say what it waits for (a lost UseDone leaves it up until
            // the 5 s force-clear), so it holds a heal ActionPacer.HealGraceMs at most, timed from its
            // rise so it does not add to the pacer's own hold. The pacer and the cast gate still hold
            // a heal for an action the server hasn't answered and for a gesture that is animating.
            double upMs = (Now - _busyRoseAt).TotalMilliseconds;
            if (upMs < ActionPacer.HealGraceMs) return true;
            why = $"the count has been up {upMs:0} ms (heal grace)";
        }
        int was = BusyCount;
        StaleBusyClears++;
        _onStaleBusy?.Invoke($"vitals: health is under a heal line and {why} - was {was}");
        if (BusyCount > 0) BusyCount = 0;   // no callback, or the shared mirror was already 0
        _busyGate.Reset();
        return false;
    }
    /// <summary>Times a heal found the busy count was a leftover and went ahead (tests, status).</summary>
    internal int StaleBusyClears { get; private set; }

    /// <summary>
    /// Record-only diagnostic (D4): the reason the most recent buff-decision cycle
    /// did NOT issue a self/player buff cast (or the site that yielded the tick).
    /// Purely additive — set at each skip site, never read by control flow.
    /// Surfaced through GetStateSnapshot()/BuffStateSnapshot for /ra why + arbiter.
    /// </summary>
    public string LastBuffSkipReason { get; private set; } = "";

    private readonly List<string> BaseCreatureBuffs = new()
    {
        "Strength Self", "Endurance Self", "Coordination Self",
        "Quickness Self", "Focus Self", "Willpower Self",
        "Magic Resistance Self", "Invulnerability Self", "Impregnability Self"
    };

    private readonly Dictionary<AcSkillType, string> CreatureSkillBuffs = new()
    {
        { AcSkillType.MeleeDefense, "Invulnerability Self" },
        { AcSkillType.MissileDefense, "Impregnability Self" },
        { AcSkillType.MagicDefense, "Magic Resistance Self" },
        { AcSkillType.HeavyWeapons, "Heavy Weapon Mastery" },
        { AcSkillType.LightWeapons, "Light Weapon Mastery" },
        { AcSkillType.FinesseWeapons, "Finesse Weapon Mastery" },
        // Missile Weapons was missing entirely, so archers never got their mastery buff.
        { AcSkillType.MissileWeapons, "Missile Weapon Mastery" },
        { AcSkillType.TwoHandedCombat, "Two Handed Combat Mastery" },
        { AcSkillType.Shield, "Shield Mastery" },
        { AcSkillType.DualWield, "Dual Wield Mastery" },
        { AcSkillType.Recklessness, "Recklessness Mastery" },
        { AcSkillType.SneakAttack, "Sneak Attack Mastery" },
        { AcSkillType.DirtyFighting, "Dirty Fighting Mastery" },
        { AcSkillType.AssessCreature, "Monster Attunement" },
        { AcSkillType.AssessPerson, "Person Attunement" },
        { AcSkillType.ArcaneLore, "Arcane Enlightenment" },
        { AcSkillType.ArmorTinkering, "Armor Tinkering Expertise" },
        { AcSkillType.ItemTinkering, "Item Tinkering Expertise" },
        { AcSkillType.MagicItemTinkering, "Magic Item Tinkering Expertise" },
        { AcSkillType.WeaponTinkering, "Weapon Tinkering Expertise" },
        { AcSkillType.Salvaging, "Arcanum Salvaging" },
        { AcSkillType.Run, "Sprint" },
        { AcSkillType.Jump, "Jumping Mastery" },
        { AcSkillType.Loyalty, "Fealty" },
        { AcSkillType.Leadership, "Leadership Mastery" },
        { AcSkillType.Deception, "Deception Mastery" },
        { AcSkillType.Healing, "Healing Mastery" },
        { AcSkillType.Lockpick, "Lockpick Mastery" },
        { AcSkillType.Cooking, "Cooking Mastery" },
        { AcSkillType.Fletching, "Fletching Mastery" },
        { AcSkillType.Alchemy, "Alchemy Mastery" },
        { AcSkillType.ManaConversion, "Mana Conversion Mastery" },
        { AcSkillType.CreatureEnchantment, "Creature Enchantment Mastery" },
        { AcSkillType.ItemEnchantment, "Item Enchantment Mastery" },
        { AcSkillType.LifeMagic, "Life Magic Mastery" },
        { AcSkillType.WarMagic, "War Magic Mastery" },
        { AcSkillType.VoidMagic, "Void Magic Mastery" },
        { AcSkillType.Summoning, "Summoning Mastery" },
    };

    /// <summary>
    /// The prots and banes the Buffing settings let the player untick (LegacyUiSettings.DisabledBuffs),
    /// paired by element for the two-column grid: (buff base name, label). Armor Self and
    /// Impenetrability head the columns. The cast order stays BuildDynamicBuffList's.
    /// </summary>
    internal static readonly (string Buff, string Label)[] ToggleProtections =
    {
        ("Armor Self", "Armor"), ("Fire Protection Self", "Fire"), ("Cold Protection Self", "Cold"),
        ("Acid Protection Self", "Acid"), ("Lightning Protection Self", "Lightning"),
        ("Blade Protection Self", "Blade"), ("Piercing Protection Self", "Piercing"),
        ("Bludgeoning Protection Self", "Bludgeoning"),
    };

    internal static readonly (string Buff, string Label)[] ToggleBanes =
    {
        ("Impenetrability", "Impenetrability"), ("Flame Bane", "Flame"), ("Frost Bane", "Frost"),
        ("Acid Bane", "Acid"), ("Lightning Bane", "Lightning"),
        ("Blade Bane", "Blade"), ("Piercing Bane", "Piercing"),
        ("Bludgeoning Bane", "Bludgeoning"),
    };

    public BuffManager(RynthCoreHost host, LegacyUiSettings settings, SpellManager spellManager, PlayerVitalsCache vitals)
    {
        _host = host;
        _settings = settings;
        _spellManager = spellManager;
        _vitals = vitals;
        _cast = new CastTracker(s => _host.Log(s));
        _healLatency = new HealLatencyTracker(s => _host.Log(s));
    }

    public void SetCharacterSkills(CharacterSkills skills) => _charSkills = skills;
    public void SetWorldObjectCache(WorldObjectCache cache) => _worldObjectCache = cache;

    public void SetTimerPath(string charFolder)
    {
        _buffTimerPath = System.IO.Path.Combine(charFolder, "bufftimers.txt");
        LoadBuffTimers();
    }

    public void SaveBuffTimers()
    {
        if (string.IsNullOrEmpty(_buffTimerPath)) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_buffTimerPath)!);
            var lines = new List<string>();
            foreach (var kvp in _ramBuffTimers)
            {
                var info = kvp.Value;
                // Never persist permanent player enchants: they are a live-read
                // presence cache. After a death/dispel + relog the buff is gone
                // server-side, so it must be re-proven by a fresh live read,
                // not restored from disk as still-active.
                if (info.IsPermanent) continue;
                lines.Add($"ram|{kvp.Key}|{info.Expiration.Ticks}|{info.SpellLevel}|{info.SpellName}");
            }
            foreach (var kvp in _itemSpellTimers)
            {
                var info = kvp.Value;
                lines.Add($"item|{kvp.Key}|{info.CastAt.Ticks}|{info.ExpiresAt.Ticks}|{info.SpellLevel}|{info.SpellName}");
            }
            // Last: a build from before the shield's lines reads them as old-style lines that
            // expired long ago (no exception, so the lines above are loaded either way).
            SaveShieldTimers(lines);
            // The "char doesn't know this spell" blacklist is NO LONGER persisted.
            // Persisting it meant one lag-induced no-chat timeout permanently
            // poisoned a KNOWN spell across all future sessions, collapsing the
            // tier walk down to level 1. It is now session-only: re-proven each
            // login, cleared on relog.
            System.IO.File.WriteAllLines(_buffTimerPath, lines);
        }
        catch { }
    }

    public void LoadBuffTimers()
    {
        if (string.IsNullOrEmpty(_buffTimerPath)) return;
        if (!System.IO.File.Exists(_buffTimerPath)) return;
        try
        {
            _ramBuffTimers.Clear();
            _itemSpellTimers.Clear();
            _shieldSpellTimers.Clear();
            foreach (string line in System.IO.File.ReadAllLines(_buffTimerPath))
            {
                string[] p = line.Split('|');
                if (LoadShieldTimer(p)) continue;

                if (p[0] == "item" && p.Length >= 6)
                {
                    DateTime castAt    = new DateTime(long.Parse(p[2]));
                    DateTime expiresAt = new DateTime(long.Parse(p[3]));
                    int level = int.Parse(p[4]);
                    string name = p[5];
                    if (expiresAt > RynthClock.Now)
                    {
                        int family = new SpellInfo(0, name).Family;
                        _itemSpellTimers[family] = new ItemSpellRecord
                        {
                            CastAt = castAt, ExpiresAt = expiresAt,
                            SpellLevel = level, SpellName = name,
                        };
                    }
                    continue;
                }

                // Legacy "unknown|<id>" blacklist lines are intentionally ignored
                // (no longer restored — see SaveBuffTimers). Skip so they don't
                // fall through and misparse. Existing files self-clean on next save.
                if (p[0] == "unknown") continue;

                // Legacy single-prefix "family|ticks|level|name" or new "ram|family|ticks|level|name"
                int offset = (p[0] == "ram") ? 1 : 0;
                if (p.Length < offset + 4) continue;
                {
                    DateTime expiration = new DateTime(long.Parse(p[offset + 1]));
                    int level = int.Parse(p[offset + 2]);
                    string name = p[offset + 3];
                    int family = new SpellInfo(0, name).Family;
                    if (expiration > RynthClock.Now)
                        _ramBuffTimers[family] = new RamTimerInfo { Expiration = expiration, SpellLevel = level, SpellName = name };
                }
            }
            int total = _ramBuffTimers.Count + _itemSpellTimers.Count + _shieldSpellTimers.Count;
            if (total > 0)
                _host.WriteToChat($"[RynthAi] Restored {_ramBuffTimers.Count} player + {_itemSpellTimers.Count} item spell timer(s)" +
                                  (_shieldSpellTimers.Count > 0 ? $" + {_shieldSpellTimers.Count} on the shield." : "."), 1);
        }
        catch { }
    }

    public void Dispose() { }

    // Force Rebuff / Cancel requested from another thread (the ImGui FR button runs on
    // the render thread, /ra chat commands on AC's main thread, the UI exports on the
    // caller's). ForceFullRebuff clears and rewrites the timer dictionaries that
    // OnHeartbeat enumerates on the plugin pump thread, so the request is only recorded
    // here and applied at the top of the next OnHeartbeat.
    private int _forceRebuffRequested;
    private int _cancelBuffingRequested;
    public void RequestForceFullRebuff() => System.Threading.Interlocked.Exchange(ref _forceRebuffRequested, 1);
    public void RequestCancelBuffing()   => System.Threading.Interlocked.Exchange(ref _cancelBuffingRequested, 1);

    // /ra buff skipped runs on AC's chat-command thread; building the list there would write the pump's
    // kit and skip fields from a second thread. The command only sets this; the pump builds and reports.
    private int _skipReportRequested;
    public void RequestSkipReport() => System.Threading.Interlocked.Exchange(ref _skipReportRequested, 1);

    private void ReportFilterSkips()
    {
        BuildDynamicBuffList();   // refreshes the skip list, even with buffing off
        string summary = _filterSummary;
        _host.WriteToChat(summary.Length == 0
            ? "[RynthAi] Buff filter: nothing is being skipped."
            : $"[RynthAi] Buff filter skips: {summary}", 1);
    }

    public void ForceFullRebuff()
    {
        _isForceRebuffing = true;
        _isAutoBatchRebuff = false;
        _isIdleTopOffRun = false;
        ClearRunFamilies();
        _buffFailCooldownUntil.Clear(); // explicit recast-all must not be blocked by stale cooldowns
        _silentNoShowCounts.Clear();    // give parked families a fresh shot on FR too
        _unsatisfiedStrikes.Clear();    // ditto for the post-batch satisfaction audit
        _castIssuedAtByFamily.Clear();
        _expiryAtCastByFamily.Clear();
        _upgradeNoShowCounts.Clear();   // and a fresh try at parked upgrades
        _upgradeParkedUntil.Clear();
        _ramBuffTimers.Clear();
        _itemSpellTimers.Clear();
        ResetShieldBuffs();             // the shield's timers, rests and park too
        EndPendingUpgrade(_pendingSpellId, "a new Force Rebuff");
        _pendingSpellId = 0;
        _lastCastAttempt = DateTime.MinValue;
        SaveBuffTimers();
        _chain.PassStart();
        _host.WriteToChat("[RynthAi] Starting Force Rebuff...", 5);

        var list = BuildDynamicBuffList();
        LogFilterSkips();   // the skips of the pass just made
        _host.Log($"[FR] buff list ({list.Count}): {string.Join(", ", list)}");
        _host.Log($"[FR] macroRunning={_settings.IsMacroRunning} liveRefreshed={_liveBuffsRefreshed}");
    }

    public void CancelBuffing()
    {
        _isForceRebuffing = false;
        _isAutoBatchRebuff = false;
        _isIdleTopOffRun = false;
        _isRechargingMana = false;
        _isRechargingStamina = false;
        _isHealingSelf = false;
        EndPendingUpgrade(_pendingSpellId, "buffing cancelled");
        _pendingSpellId = 0;
        _chain.PassEnd("cancelled", s => _host.Log(s));
        _host.WriteToChat("[RynthAi] Sequence cancelled.", 5);
    }

    /// <summary>
    /// Spell id of a cast awaiting server confirmation, or 0. Read by the activity
    /// arbiter: a cast in flight must keep Buffing winning so CombatManager can't
    /// slip a peace-mode switch in mid-cast and fizzle it.
    /// </summary>
    public int PendingSpellId => _pendingSpellId;

    /// <summary>
    /// True when the last vitals evaluation wanted a heal / restam / remana. Also
    /// read by the arbiter — vital recharges run even with buffing switched off,
    /// and they need the same mid-cast protection a buff gets. Side-effect free:
    /// CheckVitals repopulates these flags every cycle it reaches.
    /// </summary>
    public bool WantsVitalRecharge => (!HealHoldReleased && (_isHealingSelf || WantsHealNow()))   // the heal hold (BuffManager.HealHold.cs)
                                      || _isRechargingMana || _isRechargingStamina || WantsManaNow();

    // Health below a heal line wins the arbiter's tick by itself. The flags above are only set on
    // a tick where CheckVitals actually ran and acted, and it doesn't run while the client is busy
    // (CanCastNow / BusyCount): looting keeps it busy almost constantly, so in a pack the arbiter
    // never saw "wants to heal", looting and nav kept the tick, and the character nearly died
    // (2026-10-04). Backs off for HealUnavailableMs after a check that found nothing able to
    // heal, so an unhealable state can't hold the bot (the old reason the flags were strict).
    private DateTime _healUnavailableUntil = DateTime.MinValue;
    private const double HealUnavailableMs = 5000;
    private const double HealInterruptMinMs = 600;   // a buff gesture is never shorter; CanCastNow also gates

    internal bool WantsHealNow()
    {
        if (!_settings.IsMacroRunning || _vitals.MaxHealth == 0) return false;
        if (RynthClock.Now < _healUnavailableUntil) return false;
        return HealthUnderAnyLine(_vitals.HealthPct, _vitals.StaminaPct);
    }

    private bool HealthUnderAnyLine(int hp, int stamPct) => ActiveHealLine(hp, stamPct, out _, out _).Length > 0;

    /// <summary>For the engine's reload deferral (ReloadSafety): the heal line health is under now
    /// ("" = none, or vitals not read yet), with the health and the line's value.</summary>
    internal string HealLineNow(out int healthPct, out int lineValue)
    {
        healthPct = _vitals.HealthPct;
        lineValue = 0;
        if (_vitals.MaxHealth == 0) return "";
        return ActiveHealLine(healthPct, _vitals.StaminaPct, out lineValue, out _);
    }

    /// <summary>For the engine's reload deferral: the cast awaiting its result (pending, or held
    /// open by the server), or "".</summary>
    internal string CastInFlightName
    {
        get
        {
            if (_pendingSpellId != 0) return SpellTableStub.GetById(_pendingSpellId)?.Name ?? $"spell {_pendingSpellId}";
            return _cast.Held?.Name ?? "";
        }
    }

    /// <summary>
    /// The heal line health is under, by name ("" = none): Emergency Heal At, Stamina To Health At,
    /// Heal At (a monster within MonsterRange) or Top Off HP (idle). The same test HealthUnderAnyLine
    /// always made; the name and value are for [HealLatency].
    /// </summary>
    private string ActiveHealLine(int hp, int stamPct, out int value, out bool inCombat)
    {
        inCombat = InCombatNow();
        value = 0;
        if (_settings.EmergencyHealAt > 0 && hp <= _settings.EmergencyHealAt) { value = _settings.EmergencyHealAt; return "Emergency Heal At"; }
        if (StaminaToHealthAllowed(_settings, hp, stamPct)) { value = _settings.StaminaToHealthAt; return "Stamina To Health At"; }
        value = inCombat ? _settings.HealAt : _settings.TopOffHP;
        if (hp < value) return inCombat ? "Heal At" : "Top Off HP";
        return "";
    }

    // ── What the activity display says while the arbiter's slot is "Buffing" ──
    // BotAction stays "Buffing" for every vitals job: it is a control string (CombatManager's
    // canRun and CorpseOpenController read it). The dashboard and the phone showed it as is, so a
    // heal read "Buffing" (2026-10-05). The display label says which job it is.
    public const string LabelHealing = "Healing";
    public const string LabelMana = "Restoring mana";
    public const string LabelStamina = "Restoring stamina";
    public const string LabelBuffing = "Buffing";

    /// <summary>
    /// Why the vitals/buff slot is held, for display: Healing, Restoring mana, Restoring stamina or
    /// Buffing. The cast or kit in flight says it first; then health under a heal line; then what
    /// the last vitals check started. Side-effect free (KitPending is not called).
    /// </summary>
    public string BuffingLabel
    {
        get
        {
            if (_pendingSpellId != 0 && _pendingSpellId == _pendingVitalSpellId && _pendingVitalLabel.Length > 0)
                return _pendingVitalLabel;
            if (_kitUsedAt != DateTime.MinValue && (RynthClock.Now - _kitUsedAt).TotalMilliseconds < KitResultTimeoutMs)
                return _kitIsStamina ? LabelStamina : LabelHealing;
            if (_isHealingSelf || WantsHealNow()) return LabelHealing;
            if (_pendingSpellId != 0) return LabelBuffing;
            if (_isRechargingMana || WantsManaNow()) return LabelMana;
            if (_isRechargingStamina) return LabelStamina;
            return LabelBuffing;
        }
    }

    /// <summary>The display label for a vital spell's base name (AttemptVitalCast's argument).</summary>
    internal static string VitalLabel(string baseName) => baseName switch
    {
        "Heal Self" or "Stamina to Health Self" => LabelHealing,
        "Stamina to Mana Self" => LabelMana,
        "Revitalize Self" => LabelStamina,
        _ => LabelBuffing,
    };
    private string _pendingVitalLabel = "";
    private bool _kitIsStamina;

    public BuffStateSnapshot GetStateSnapshot() => new()
    {
        EnableBuffing       = _settings.EnableBuffing,
        IsForceRebuffing    = _isForceRebuffing,
        IsRechargingMana    = _isRechargingMana,
        IsRechargingStamina = _isRechargingStamina,
        IsHealingSelf       = _isHealingSelf,
        PendingSpellId      = _pendingSpellId,
        LastCastAttempt     = _lastCastAttempt,
        RamBuffTimerCount   = _ramBuffTimers.Count,
        ItemSpellTimerCount = _itemSpellTimers.Count,
        NeedsAnyBuffNow     = NeedsAnyBuff(),
        LastBuffSkipReason  = LastBuffSkipReason,
        HealthPct           = _vitals.HealthPct,
        ManaPct             = _vitals.ManaPct,
        StaminaPct          = _vitals.StaminaPct,
    };

    public struct BuffStateSnapshot
    {
        public bool     EnableBuffing;
        public bool     IsForceRebuffing;
        public bool     IsRechargingMana;
        public bool     IsRechargingStamina;
        public bool     IsHealingSelf;
        public int      PendingSpellId;
        public DateTime LastCastAttempt;
        public int      RamBuffTimerCount;
        public int      ItemSpellTimerCount;
        public bool     NeedsAnyBuffNow;
        public int      HealthPct;
        public int      ManaPct;
        public int      StaminaPct;
        public string   LastBuffSkipReason;
    }

    private bool _liveBuffsRefreshed;
    /// <summary>Host tests: skip the login refresh wait so OnHeartbeat runs at once.</summary>
    internal void MarkLiveBuffsReadyForTests() => _liveBuffsRefreshed = true;
    /// <summary>The login refresh has a trusted snapshot of the live buffs (buff casting may start).</summary>
    public bool LiveBuffsReady => _liveBuffsRefreshed;
    /// <summary>
    /// Set by RynthAiPlugin when this login is a hot reload (engine or plugin) of a character
    /// that was already in game in this client: the enchantment registry is complete, so the
    /// login refresh trusts its first read instead of waiting for two equal reads (5.8 s after
    /// the 2026-10-05 reload).
    /// </summary>
    public bool CarriedOverSession { get; set; }
    private DateTime _lastLiveRefreshAttempt = DateTime.MinValue;
    private DateTime _lastPeriodicRefreshAt = DateTime.MinValue;
    private const int PeriodicRefreshIntervalMs = 30_000;

    // Login-refresh stabilization: the server streams the active-enchantment
    // registry over a few seconds after login, so an early read returns a
    // partial/empty set. Opening the gate on that wipes timers → full rebuff
    // every login. Wait until the count is the SAME across two consecutive 1s
    // reads (registry done streaming — works for 0 buffs or 50), or a timeout.
    private DateTime _loginRefreshStartAt = DateTime.MinValue;
    private int _lastLoginRefreshCount = -1;
    private const int LoginRefreshMaxWaitMs = 20_000;

    // ── [HealLatency] (see HealLatencyTracker) ─────────────────────────────────
    private readonly HealLatencyTracker _healLatency;
    internal HealLatencyTracker HealLatency => _healLatency;
    // Why this heartbeat sent no heal (the tracker files the tick's time under it), plus a detail.
    private string _healHold = "";
    private string _healHoldDetail = "";
    // What a vitals step that "handled" the tick without sending anything was waiting on.
    private string _healWaitKind = "";
    /// <summary>The arbiter's last decision (set by RynthAiPlugin after it decides), for [HealLatency].</summary>
    internal string ArbiterActivity { get; set; } = "";

    private void Hold(string why, string detail = "")
    {
        _healHold = why;
        _healHoldDetail = detail;
    }

    /// <summary>A heal (kit or spell) sent earlier hasn't resolved yet.</summary>
    private bool HealInFlight() =>
        (_pendingSpellId != 0 && _pendingSpellId == _pendingVitalSpellId && _pendingVitalLabel == LabelHealing)
        || (_kitUsedAt != DateTime.MinValue && !_kitIsStamina && (RynthClock.Now - _kitUsedAt).TotalMilliseconds < KitResultTimeoutMs);

    public void OnHeartbeat()
    {
        bool track = _settings.IsMacroRunning && _vitals.MaxHealth > 0;
        NoteKitProgress();   // every heartbeat: the vitals step is not reached while the kit's heal motion plays
        _healHold = "";
        _healHoldDetail = "";
        _healWaitKind = "";
        if (track)
        {
            string line = ActiveHealLine(_vitals.HealthPct, _vitals.StaminaPct, out int lineValue, out bool inCombat);
            _healLatency.Observe(Now, _vitals.HealthPct, line, lineValue, inCombat, HealInFlight());
        }
        try { HeartbeatCore(); }
        finally
        {
            NoteStanceLateHolder();
            if (track)
            {
                string hold = _healHold;
                if (hold.Length == 0 && RynthClock.Now < _healUnavailableUntil) hold = "heal backoff (nothing could heal)";
                _healLatency.EndTick(Now, hold, _healHoldDetail, ArbiterActivity);
            }
        }
    }

    private void HeartbeatCore()
    {
        // Requests queued from other threads (see RequestForceFullRebuff), in click order
        // as far as it matters: a cancel after a rebuff request wins.
        if (System.Threading.Interlocked.Exchange(ref _forceRebuffRequested, 0) != 0)
            ForceFullRebuff();
        if (System.Threading.Interlocked.Exchange(ref _cancelBuffingRequested, 0) != 0)
            CancelBuffing();
        if (System.Threading.Interlocked.Exchange(ref _skipReportRequested, 0) != 0)
            ReportFilterSkips();

        // Post-cast diffs for the bufftest experiment run unconditionally — even if the
        // user toggled the macro off mid-experiment, we still want to capture and log them.
        // Drain any pending diagnostics whose 2s post-cast window has elapsed.
        while (_pendingDiagnostics.Count > 0 &&
               (RynthClock.Now - _pendingDiagnostics.Peek().CastedAt).TotalSeconds >= 2.0)
        {
            var diag = _pendingDiagnostics.Dequeue();
            LogPostCastDiff(diag);
        }

        // A recall the HP failsafe asked for goes out after the macro stop (BuffManager.HpFailsafe.cs).
        TickFailsafeRecall();
        TickRechargeRequest();   // /ra recharge: one vitals pass, macro on or off (BuffManager.Recharge.cs)
        TickOneTouchRequest();   // /ra heal, /ra stam, /ra mana: one-touch recharge, macro on or off (BuffManager.OneTouch.cs)

        if (!_settings.IsMacroRunning)
        {
            DropWandSwap("the macro stopped");
            ResetHealHold("the macro stopped");
            ClearKitRefusedStreaks();            // the vital wand gate's refusal streaks end
            EndWandGateEpisodes("the macro stopped");
            return;
        }
        TickWandGateLog();   // the vital wand gate: says when a refusal episode is over (BuffManager.VitalWandGate.cs)

        // A wand swap's next step goes as soon as the server has answered the last (before the cast gates).
        NoteModeChange();
        TickWandSwap();

        // Close casts the server reports finished, and say once when one is being held open.
        PollCastTracker();
        ObserveBuffChain();
        _fastChainTick = false;

        // Heals failing at Emergency Heal At: break the cast, then recall (BuffManager.HpFailsafe.cs).
        TickHpFailsafe();
        if (!_settings.IsMacroRunning) { ResetHealHold("the HP failsafe stopped the macro for a recall"); return; }
        // No heal landing under a heal line: combat gets the tick for a while (BuffManager.HealHold.cs).
        TickHealHold();

        // A Magic stance we asked for: watch the client's mode every tick (see ObserveStance).
        ObserveStance();

        // Rebuff When Idle: an early top-off run stops once the bot isn't idle (a fight, a corpse,
        // salvage), at the first heartbeat with no cast of ours in flight (a sent cast resolves first),
        // and before the arbiter reads NeedsAnyBuff this tick. The regular rules take over.
        if (_isIdleTopOffRun && _isForceRebuffing && _pendingSpellId == 0 && !IdleTopOffNow())
            EndIdleTopOffRun();

        // RefreshFromLiveMemory at OnLoginComplete fails when the server-time
        // packet hasn't landed yet (GetServerTime returns 0). On those
        // logins we have NO timers and would recast every buff that has
        // hours left server-side. Retry every second until we get the
        // live snapshot, and hold BUFF casts in the meantime.
        //
        // Heals are not held (2026-10-05, Drakkon died after an engine hot reload mid-fight:
        // "health 12% under Emergency Heal At 30% ... held by: login buff refresh 5737 ms").
        // The refresh only protects buff timers; a heal, a kit or a potion never reads them,
        // so the vitals step below runs during the refresh and only the buff casting after it
        // waits (buffsHeldForRefresh).
        bool buffsHeldForRefresh = false;
        if (!_liveBuffsRefreshed)
        {
            if ((RynthClock.Now - _lastLiveRefreshAttempt).TotalMilliseconds > 1000)
            {
                _lastLiveRefreshAttempt = RynthClock.Now;
                int n = RefreshFromLiveMemory();
                if (n >= 0)
                {
                    if (_loginRefreshStartAt == DateTime.MinValue) _loginRefreshStartAt = RynthClock.Now;
                    // Only trust the snapshot once the registry stops growing
                    // (two equal 1s reads), so we don't open the gate on a
                    // half-streamed set and rebuff buffs that are still landing.
                    // After a hot reload (engine or plugin) the client never left the world and
                    // its registry is complete: the first read is trusted (CarriedOverSession), unless
                    // it is empty (a read that found nothing yet must not wipe every timer).
                    bool stable    = (n == _lastLoginRefreshCount);
                    bool timedOut  = (RynthClock.Now - _loginRefreshStartAt).TotalMilliseconds > LoginRefreshMaxWaitMs;
                    bool carried   = CarriedOverSession && n > 0;
                    _lastLoginRefreshCount = n;
                    if (stable || timedOut || carried)
                    {
                        _liveBuffsRefreshed = true;
                        string why = carried && !stable && !timedOut
                            ? "hot reload: still in game, the first read is complete"
                            : $"stable={stable} timedOut={timedOut}";
                        _host.Log($"[BuffDiag] login refresh ready: {n} enchantment(s) ({why})");
                        _host.WriteToChat($"[RynthAi] Live buff timers ready ({n} loaded).", 1);
                    }
                }
            }
            // Don't cast a buff until we have a real snapshot — otherwise
            // we'd recast over a 1-hour-old buff at the start of every login.
            buffsHeldForRefresh = !_liveBuffsRefreshed;
            if (buffsHeldForRefresh && !HealthUnderAnyLine(_vitals.HealthPct, _vitals.StaminaPct))
            {
                Hold("login buff refresh");
                return;
            }
        }

        // Periodic re-sync: if the character died and lost all enchantments, the
        // in-memory timers still show buffs as "active" until their timestamps
        // expire normally — causing NeedsAnyBuff() to return false while the char
        // is completely unbuffed. Re-read from live AC memory every 30s to detect
        // this gap and clear stale timers so rebuffing resumes promptly.
        if ((RynthClock.Now - _lastPeriodicRefreshAt).TotalMilliseconds > PeriodicRefreshIntervalMs)
        {
            _lastPeriodicRefreshAt = RynthClock.Now;
            int n = RefreshFromLiveMemory();
            if (n >= 0)
                _host.Log($"[BuffDiag] periodic sync: {n} active enchantment(s) in RAM timers.");
        }

        // ── Pending-cast resolution — self-buffs and armor use DIFFERENT signals ──
        // Self-buffs ARE in the live player-enchantment registry, so we confirm
        // them by reading it (no chat, no no-chat blacklisting that poisons the
        // tier walk). Armor/item enchants are NOT in the player registry — they
        // live on the item — so they still resolve via chat + the no-chat valve.
        // Healing interrupts buffing: a BUFF awaiting confirmation (up to 2.5 s, 5 s for armor)
        // held every heal but an emergency kit/potion. Once its gesture is done the server has
        // the cast; with health under a heal line, stop waiting for the confirmation and let
        // CheckVitals heal now. A late confirmation still lands through the enchantment events;
        // at worst a buff is recast later (2026-10-04).
        if (_pendingSpellId != 0 && _pendingSpellId != _pendingVitalSpellId && WantsHealNow()
            && (RynthClock.Now - _lastCastAttempt).TotalMilliseconds >= HealInterruptMinMs
            && CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s)) && !BusyHoldsHeal())
        {
            _host.Log($"[BuffDiag] health {_vitals.HealthPct}% under a heal line: released pending buff id={_pendingSpellId} to heal first.");
            EndPendingUpgrade(_pendingSpellId, "released to heal");
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("released to heal");
        }

        if (_pendingSpellId != 0)
        {
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            bool pendingIsArmor = pendingSpell != null && IsItemEnchantment(pendingSpell.Name);

            // VITAL (Heal Self, Stamina to Health, Revitalize, Stamina to Mana): instant, never
            // in the enchantment registry. Its "You cast ... and restore N points" line clears
            // it; when that line is late, the registry poll below counted a no-show, and two
            // of those parked Heal Self for 30 minutes, so a mage healed nothing between
            // Emergency Heal At and Heal At (tester's log, 2026-10-01). Now a late line just
            // releases the pending cast, with no strike.
            if (pendingSpell != null && _pendingSpellId == _pendingVitalSpellId)
            {
                // Timed from the incantation when one arrived (2026-10-05): a healthy DreamWeave
                // vital takes 2.2-2.5 s after its incantation, and 2.5 s from the send gave up on
                // casts the server was still finishing.
                if (PendingGiveUpReached(SelfBuffGiveUpMs))
                {
                    _host.Log($"[BuffDiag] vital cast '{pendingSpell.Name}' (id={_pendingSpellId}) not confirmed by chat ({GiveUpWhy(SelfBuffGiveUpMs)}) — released, no strike.");
                    _pendingSpellId = 0;
                    _pendingVitalSpellId = 0;
                    _onCastResolved?.Invoke("vital cast unconfirmed");
                }
                else
                {
                    TryEmergencyHealWhilePending();
                    Hold(_pendingVitalLabel == LabelHealing ? "heal cast in flight" : "vital cast in flight", pendingSpell.Name);
                    return;
                }
            }
            else if (pendingSpell != null && !pendingIsArmor)
            {
                // SELF-BUFF: confirm against the live registry once the cast has
                // settled. (OnEnchantmentAdded usually clears pending faster; this
                // poll is the authoritative fallback when the hook doesn't fire.)
                double sinceCastMs = (RynthClock.Now - _lastCastAttempt).TotalMilliseconds;
                if (sinceCastMs > SelfBuffConfirmMs
                    && (RynthClock.Now - _lastSelfBuffPollAt).TotalMilliseconds > 250)
                {
                    _lastSelfBuffPollAt = RynthClock.Now;
                    RefreshFromLiveMemory();
                    bool active = _ramBuffTimers.TryGetValue(pendingSpell.Family, out RamTimerInfo? st)
                                  && st.Expiration > RynthClock.Now;
                    // A top-off (auto batch), Force Rebuff or tier upgrade casts over a
                    // buff that is still on, so "active" is true at the first poll
                    // whatever happened: a fizzle counted as landed, the buff wasn't
                    // refreshed, and the late fizzle line then cleared the NEXT cast.
                    // While the old entry hasn't moved, wait for the chat line or the
                    // enchantment event; at the give-up it is judged exactly as before.
                    // Give-up timed from the incantation when one arrived (see the vital path).
                    bool pastGiveUp = PendingGiveUpReached(SelfBuffGiveUpMs);
                    if (active && !pastGiveUp
                        && _castIssuedAtByFamily.TryGetValue(pendingSpell.Family, out DateTime issuedAt)
                        && _expiryAtCastByFamily.TryGetValue(pendingSpell.Family, out DateTime expiryThen)
                        && expiryThen > issuedAt                          // it was on when we cast
                        && st!.Expiration <= expiryThen.AddSeconds(2))    // and hasn't been refreshed
                        active = false;
                    // An upgrade is cast over its own lower level, so the family is "on" whatever
                    // happened, at the give-up as well: it landed only when the entry MOVED (a later
                    // expiry, or the pending level or higher; an Incantation landing a level low shows
                    // a lower level, so for one it is the expiry that moves). Unmoved: a no-show.
                    if (active && _pendingSpellId == _pendingUpgradeSpellId)
                    {
                        bool later = _expiryAtCastByFamily.TryGetValue(pendingSpell.Family, out DateTime upThen)
                                     && st!.Expiration > upThen.AddSeconds(2);
                        bool higher = st!.SpellLevel >= GetSpellLevel(pendingSpell);
                        active = later || higher;
                    }
                    if (active)
                    {
                        // During an auto-batch / force-rebuff, IsBuffActive gates on
                        // _forceRebuffCastFamilies (not timers), so the family MUST be
                        // marked cast here or the batch respins this buff forever.
                        NoteBuffConfirmed(pendingSpell);
                        _cast.Confirmed(_pendingSpellId, "confirmed in the enchantment registry");
                        _pendingSpellId = 0; // landed — registry confirms it; advance
                        NoteCastAccepted();  // it went through — end any too-busy backoff
                        _onCastResolved?.Invoke("self-buff confirmed (registry)");
                    }
                    else if (pastGiveUp)
                    {
                        // Not in the registry after settling → this tier didn't take.
                        // A WARM snapshot already excludes unknown tiers, so a no-show
                        // is lag/fizzle → retry (NO blacklist). A COLD snapshot has no
                        // other tier-down signal → blacklist this id to drop a tier.
                        // An upgrade's no-show ends that attempt and nothing else: no strike, no 30 min park,
                        // no blacklist (any of them would delay the next try and the lower level it was cast
                        // over). Read before EndPendingUpgrade clears the pending upgrade.
                        bool isUpgrade = _pendingSpellId == _pendingUpgradeSpellId;
                        int fam = pendingSpell.Family;
                        if (isUpgrade)
                        {
                            _host.Log($"[BuffDiag] upgrade '{pendingSpell.Name}' (id={_pendingSpellId}, fam={fam}) not in the live registry after {sinceCastMs:0}ms (the lower level is unchanged): no strike, tried again.");
                            NoteUpgradeNoShow(_pendingSpellId);   // bounded: parks the upgrade (only) after UpgradeNoShowThreshold in a row
                        }
                        else
                        {
                            bool cold = !_spellManager.IsKnownSnapshotWarm;
                            if (cold) _spellManager.MarkSpellUnresolvable(_pendingSpellId);

                            // Per-family silent-no-show bookkeeping (the /god loop break).
                            // Increment regardless of warmth — even the "warm → retry" path
                            // needs an upper bound, otherwise a buff whose family is in the
                            // dynamic list but can never actually land loops forever.
                            int noShowCount = _silentNoShowCounts.TryGetValue(fam, out int prev) ? prev + 1 : 1;
                            _silentNoShowCounts[fam] = noShowCount;
                            bool parked = false;
                            if (noShowCount >= SilentNoShowThreshold)
                            {
                                _buffFailCooldownUntil[fam] = RynthClock.Now + SilentNoShowCooldown;
                                parked = true;
                            }

                            _host.Log($"[BuffDiag] self-buff '{pendingSpell.Name}' (id={_pendingSpellId}, fam={fam}) absent from live registry after {sinceCastMs:0}ms — " +
                                      (cold ? "cold snapshot → blacklisted, tier-down." : "warm snapshot → retry (lag/fizzle).") +
                                      $" noShows={noShowCount}/{SilentNoShowThreshold}" +
                                      (parked ? $" — family parked {SilentNoShowCooldown.TotalMinutes:0}min." : ""));
                        }
                        EndPendingUpgrade(_pendingSpellId, "no-show");
                        _pendingSpellId = 0;
                        _onCastResolved?.Invoke("self-buff no-show (registry)");
                    }
                }
                // Yield while the self-buff cast is in flight. PendingSpellId keeps
                // the arbiter on Buffing, so CombatManager can't sneak a peace-mode
                // switch in mid-cast.
                if (_pendingSpellId != 0) { TryEmergencyHealWhilePending(); Hold("buff cast in flight", pendingSpell.Name); return; }
            }
            else if (pendingIsArmor && PendingIsShield)
            {
                // An armor spell aimed at the shield (BuffManager.Shield.cs): chat is authoritative,
                // as for the armor, but a give-up counts against the shield only. No blacklist (the
                // same spell just went on the armor) and no family park for the armor.
                if (PendingGiveUpReached(NoChatResolveTimeoutMs))
                {
                    ShieldCastNoShow(pendingSpell);
                    _pendingSpellId = 0;
                    _pendingShieldId = 0;
                    _onCastResolved?.Invoke("no-chat timeout (shield)");
                }
                if (_pendingSpellId != 0) { TryEmergencyHealWhilePending(); Hold("item spell in flight", pendingSpell?.Name ?? ""); return; }
            }
            else if (pendingIsArmor)
            {
                // ARMOR/ITEM: chat is authoritative. No-chat valve abandons a cast
                // AC never answers so the cycle can't wedge; the chat handlers do
                // the cooldown. Blacklist (unless snapshot confirms known) drops tier.
                // From the send, or later when the incantation came late (never earlier than before).
                if (PendingGiveUpReached(NoChatResolveTimeoutMs))
                {
                    int stuckId = _pendingSpellId;
                    var stuck = SpellTableStub.GetById(stuckId);
                    // An upgrade: no strike, park or blacklist, as on the self-buff path.
                    bool isUpgrade = stuckId == _pendingUpgradeSpellId;
                    bool confirmedKnown = _spellManager.IsKnownSnapshotWarm && _spellManager.IsKnownSpellId(stuckId);
                    if (!confirmedKnown && !isUpgrade)
                        _spellManager.MarkSpellUnresolvable(stuckId);
                    if (isUpgrade)
                        NoteUpgradeNoShow(stuckId);   // bounded: parks the upgrade (only) after UpgradeNoShowThreshold in a row

                    // 2026-05-25 — per-family silent-no-show bookkeeping on the
                    // ARMOR path, mirroring the self-buff path above.
                    //
                    // Without this, an armor enchant whose target item isn't
                    // wielded — for example after the character died and lost
                    // her armor — loops forever at the 5s no-chat timeout:
                    // AC accepts the cast attempt ("Casting Impenetrability
                    // VI"), can't bind it to an item, silently drops it, never
                    // produces success chat. confirmedKnown=True (Impen is in
                    // the spellbook), so the existing path logged "NOT
                    // blacklisted — lag/busy" and ForceRebuff re-fired the same
                    // cast 12+ times/minute. Every retry pushes "Casting <X>"
                    // through AC's text pipeline, feeding the documented text-
                    // parser singleton race
                    // (rynthcore_crash_investigation.md 2026-05-25 entry).
                    //
                    // Park after SilentNoShowThreshold consecutive misses; the
                    // selectors at lines 763/823 honour _buffFailCooldownUntil
                    // so the family is skipped during the cooldown window. The
                    // bot keeps trying eventually — just not 12×/min while
                    // there's no armor to bind to.
                    int fam = stuck?.Family ?? 0;
                    bool parked = false;
                    if (fam != 0 && !isUpgrade)
                    {
                        int noShowCount = _silentNoShowCounts.TryGetValue(fam, out int prev) ? prev + 1 : 1;
                        _silentNoShowCounts[fam] = noShowCount;
                        if (noShowCount >= SilentNoShowThreshold)
                        {
                            _buffFailCooldownUntil[fam] = RynthClock.Now + SilentNoShowCooldown;
                            parked = true;
                        }
                    }

                    _host.Log($"[BuffChat] NO-CHAT TIMEOUT (armor) pending={stuckId} ('{stuck?.Name}') — no chat in " +
                              $"{NoChatResolveTimeoutMs:0}ms. confirmedKnown={confirmedKnown}. " +
                              (isUpgrade ? "An upgrade: no strike, tried again." : confirmedKnown ? "Lag/busy — NOT blacklisted." : "Blacklisted → tier-down.") +
                              (fam != 0 && !isUpgrade ? $" noShows={_silentNoShowCounts[fam]}/{SilentNoShowThreshold}" : "") +
                              (parked ? $" — family parked {SilentNoShowCooldown.TotalMinutes:0}min." : ""));
                    EndPendingUpgrade(_pendingSpellId, "no-chat timeout");
                    _pendingSpellId = 0;
                    _onCastResolved?.Invoke("no-chat timeout (armor)");
                }
                // Hold the cycle until chat resolves the item cast.
                if (_pendingSpellId != 0) { TryEmergencyHealWhilePending(); Hold("item spell in flight", pendingSpell?.Name ?? ""); return; }
            }
        }

        // A due recovery retry (BuffManager.RecoveryRetry.cs) gets past Spell Interval, for the vitals
        // step only: the buffs after it still wait (intervalHeld below). A stale episode ends first.
        ExpireRecoveryEpisode();
        bool intervalHeld = (RynthClock.Now - _lastCastAttempt).TotalMilliseconds < _settings.SpellCastIntervalMs;
        if (intervalHeld && !RecoveryRetryDue())
        {
            Hold("cast interval");
            return;
        }

        // One server action at a time (2026-10-06, Plugins/Shared/ActionPacer.cs): no kit, potion,
        // wand, stance change or cast while the server hasn't answered the last one (its UseDone;
        // a buff's enchantment lands before that). Heals keep their priority (ActionPacer's heal
        // rules). And not while RynthAi still has the character running: the arbiter's stop goes
        // out this tick, the cast the next one (a stop mid-windup disturbs the cast).
        // Buff cast chaining (BuffCastChain): right after the server's UseDone(0) for our own last
        // buff, the next buff skips the animation gate only (the pacer's gesture hold and the
        // engine's cast gate below). Every other gate stays.
        _fastChainTick = FastChainNow();
        if (!PacerAllows(out string paceWhy, gestureGate: !_fastChainTick))
        {
            LastBuffSkipReason = "server answering the last action: " + paceWhy;
            Hold("server answering the last action", paceWhy);
            return;
        }

        // Don't issue a cast while the previous cast GESTURE is still animating.
        // CanCastNow is the engine's CMotionInterp gesture gate (the REAL cast
        // gate). AC refuses a cast issued mid-gesture with a server-driven
        // "You're too busy!" notice; the old code gated on BusyCount (the
        // ClientUISystem hourglass) which reads 0 during a cast, so it never
        // blocked the retry → tight refusal loop → AddTextToScroll re-entry AV
        // at 0x00460D1D. On an engine without the gate, CanCastNow defaults to
        // true and the SpellCastIntervalMs throttle + parked-pending still bound
        // retries. BusyCount stays as a secondary guard so we also don't queue
        // casts/UseObject while the hourglass action is mid-flight.
        // Buffing priority (including "hold it while our cast is in flight so
        // CombatManager can't sneak in a peace-mode switch mid-cast") is the
        // arbiter's call now — it reads PendingSpellId / WantsVitalRecharge /
        // NeedsAnyBuff and is the sole writer of the "Buffing" string.
        // A heal goes ahead of a busy count the server has already finished (BusyHoldsHeal);
        // everything else still waits for the count to drop.
        bool gateOpen = _fastChainTick || CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s));
        bool busyHolds = BusyCount > 0
            && !_pacerLetHealAhead   // the pacing let this heal go ahead of an unanswered corpse use (its rules)
            && (ActiveHealLine(_vitals.HealthPct, _vitals.StaminaPct, out _, out _).Length == 0 || BusyHoldsHeal());
        if (!gateOpen || busyHolds)
        {
            LastBuffSkipReason = busyHolds ? "busy (BusyCount>0)" : "cast gate closed (CanCastNow=false / gesture animating)";
            if (!gateOpen) Hold("cast gate (a gesture is animating)");
            else Hold("busy count", $"busy {BusyCount}");
            return;
        }

        if (CheckVitals())
        {
            if (_healHold.Length == 0) Hold(_healWaitKind.Length > 0 ? _healWaitKind : "vitals step busy", _vitalWhy);
            return;
        }

        // /ra swaptest: the wand swap and one cast, timed (BuffManager.SwapTest.cs). After the vitals.
        if (TickSwapTest())
        {
            Hold("swap test");
            return;
        }

        // Only a recovery retry got past Spell Interval; nothing else goes inside it.
        if (intervalHeld)
        {
            Hold("cast interval");
            return;
        }

        // The login refresh above held buffs only: the heal had its turn, the buffs wait.
        if (buffsHeldForRefresh)
        {
            Hold("login buff refresh");
            return;
        }

        // Health floor: no buff while health is under a heal line that nothing could heal
        // (Drakkon, 2026-10-06 04:51: recasting Swift Killer at 19% health, dead 10 s later).
        if (_vitals.MaxHealth > 0 && HealthUnderAnyLine(_vitals.HealthPct, _vitals.StaminaPct))
        {
            Hold("health under a heal line");
            return;
        }

        // A stale mana reading: the floor below would judge a number that is gone.
        if (ManaReadingStuck())
        {
            LastBuffSkipReason = $"mana reading stuck at {_vitals.CurrentMana}/{_vitals.MaxMana}";
            Hold("mana reading stuck");
            return;
        }

        // Mana floor: no buff while mana is under Get Mana At. CheckVitals above already tried
        // Stamina to Mana and a mana potion; when neither could run (stamina too low, none
        // carried) buffing used to carry on and run mana to 0. Wait for it instead.
        int manaFloor = _settings.GetManaAt;
        if (manaFloor > 0 && _vitals.MaxMana > 0 && _vitals.ManaPct < manaFloor)
        {
            // The floor refills on its own: Stamina to Mana (or a potion) right here, then buffing
            // carries on. CheckVitals only refills below Top Off Mana out of combat, so a Top Off
            // Mana under Get Mana At left buffing waiting on natural regen - one buff every 1-2
            // minutes (tester, 10-01: 85 minutes of "buffing waits", Stamina to Mana never cast).
            // A low stamina is topped up first (kit, Revitalize, potion) so Stamina to Mana can run.
            // Each source's reason, for the log line below: it used to show only the last one tried
            // (the potion's), so a missing Stamina to Mana read as "none in the Items list or the pack".
            string s2mWhy, stamWhy = "-", potWhy;
            _vitalWhy = "";
            if (_vitals.StaminaPct > StaminaToManaFloorPct())
            {
                if (AttemptVitalCast("Stamina to Mana Self"))
                {
                    _isRechargingMana = true;
                    return;
                }
                s2mWhy = Why(_vitalWhy);
            }
            else s2mWhy = $"stamina {_vitals.StaminaPct}% is under its line ({StaminaToManaFloorPct()}%)";
            if (_vitals.StaminaPct <= 15)
            {
                _vitalWhy = "";
                if (AttemptStaminaRestore())
                {
                    _isRechargingStamina = true;
                    return;
                }
                stamWhy = Why(_vitalWhy);
            }
            _vitalWhy = "";
            if (AttemptPotion("ManaPotion"))
            {
                _isRechargingMana = true;
                return;
            }
            potWhy = Why(_vitalWhy);

            // Nothing can ever refill it (no Stamina to Mana known, no mana potion to drink): waiting
            // only means natural regeneration, so buffing crawled one buff per regen climb. Worse, a
            // rebuff raises max mana: the character logged in with full mana (209/207), the first
            // buffs took max mana to 536, the same 209 read 39% under Get Mana At 40%, and every buff
            // after waited (report R-20261029-103248-ce48, DreamWeave, 2026-10-29: "Buffing seems to
            // be getting stuck"). The floor is there to keep mana for healing, so with no refill the
            // buffs go on while mana keeps ManaFloorHealReserve after one (BuffWaitsForMana still
            // holds any buff mana can't pay for). The same rule as the reserve: "With no Stamina to
            // Mana at all nothing refills: the buff goes."
            bool canRefill = TopStaminaToManaId() != 0 || !NoManaPotionToDrink();
            int keep = ManaFloorHealReserve();
            if (!canRefill && _vitals.CurrentMana >= keep + EstimatedManaCost(8))
            {
                if (!_manaFloorNoRefillLogged)
                {
                    _manaFloorNoRefillLogged = true;
                    _host.Log($"[RynthAi] Vitals: mana {_vitals.CurrentMana}/{_vitals.MaxMana} ({_vitals.ManaPct}%) is under Get Mana At {manaFloor}%, but nothing can refill it (Stamina to Mana: {s2mWhy}; mana potion: {potWhy}): buffing goes on, keeping {keep} mana for healing.");
                }
            }
            else
            {
                LastBuffSkipReason = $"mana {_vitals.ManaPct}% under Get Mana At {manaFloor}%";
                if ((RynthClock.Now - _lastManaFloorLogAt).TotalSeconds >= 30)
                {
                    _lastManaFloorLogAt = RynthClock.Now;
                    _host.Log($"[RynthAi] Vitals: buffing waits: mana {_vitals.CurrentMana}/{_vitals.MaxMana} ({_vitals.ManaPct}%) is under Get Mana At {manaFloor}%, stamina {_vitals.StaminaPct}%, and nothing refilled it (Stamina to Mana: {s2mWhy}; stamina: {stamWhy}; mana potion: {potWhy})" +
                              (canRefill ? "" : $"; keeping {keep} mana for healing"));
                }
                return;
            }
        }
        else _manaFloorNoRefillLogged = false;

        // Too-busy backoff gates BUFFING only, and sits after CheckVitals on
        // purpose: a refused heal is worth retrying hard, a refused buff is not.
        // Blocking vitals here would have delayed emergency heals by up to 8s,
        // and the observed refusal episode happened while the character was being
        // swarmed by Olthoi — exactly when that would be most dangerous.
        // Wait for the server to finish the previous cast before issuing another.
        // Sits with the backoff, after CheckVitals, so it paces BUFFING only — a
        // heal must never queue behind a buff's resolution.
        if (IsAwaitingCastResolution())
        {
            LastBuffSkipReason = "awaiting server UseDone for the previous cast";
            return;
        }

        if (RynthClock.Now < _busyBackoffUntil)
        {
            LastBuffSkipReason = $"too-busy backoff ({(_busyBackoffUntil - RynthClock.Now).TotalMilliseconds:0}ms left, streak={_busyRefusalStreak})";
            return;
        }

        if (RynthClock.Now < _lowManaPauseUntil)
        {
            LastBuffSkipReason = $"not enough mana ({(_lowManaPauseUntil - RynthClock.Now).TotalMilliseconds:0}ms left)";
            return;
        }

        // Buff items and field rations sit behind the same gates as a buff cast (no cast
        // pending, server finished the last one, client not busy). They ran first, gated
        // only on the local gesture, so their UseObject landed while a buff was still
        // resolving server-side; the "You're too busy!" that drew cleared that buff's
        // pending state. When one of them acts, no buff cast follows in the same tick.
        // Not on a fast-path tick (the animation gate was skipped for a buff cast only): they
        // get their turn on the next tick that waited for the animation.
        DateTime beforeItems = _lastCastAttempt;
        if (!_fastChainTick)
            try { TryUseBuffItems(); TryMakeRations(); } catch { }
        if (_lastCastAttempt != beforeItems) return;

        if (_settings.EnableBuffing)
        {
            if (BuffsWaitForHealth())
                return;   // hurt in a fight: no new buff cast (a run in progress resumes after)
            if (CheckAndCastSelfBuffs())
                return;

            if (_isForceRebuffing)
            {
                bool wasAutoBatch = _isAutoBatchRebuff;
                _isForceRebuffing = false;
                _isAutoBatchRebuff = false;
                _isIdleTopOffRun = false;
                _host.Log($"[FR] complete — cast {_forceRebuffCastFamilies.Count} spell families{(wasAutoBatch ? " (auto batch)" : "")}");
                // Order matters: the flag must already be false (IsBuffActive
                // short-circuits on _forceRebuffCastFamilies while it is set, so
                // every family would read "active"), and the set must still be
                // populated — it is what the audit iterates.
                AuditBatchSatisfied();
                ClearRunFamilies();
                _chain.PassEnd("complete", s => _host.Log(s));
                _host.WriteToChat(wasAutoBatch ? "[RynthAi] Rebuff complete." : "[RynthAi] Force Rebuff Complete.", 1);
            }
        }
    }

    /// <summary>
    /// Post-batch audit. Every family the batch actually CAST should read active
    /// now that the batch is over. One that doesn't is not going to be fixed by
    /// casting it again: the cast goes out, AC confirms it, the enchantment is
    /// genuinely on the character, and IsBuffActive still says false.
    ///
    /// The case this was written for: a permanent item-granted lower tier living
    /// in the same spell family as the tier we want, which keeps the tier-upgrade
    /// rule firing no matter how many times the higher tier lands. Because ANY
    /// single buff below threshold escalates to a full rebuff, one such family is
    /// enough to make the bot recast every buff it owns, back-to-back, forever.
    ///
    /// Park the family after UnsatisfiedStrikeThreshold consecutive batches. This
    /// is a backstop, not the cure — it bounds the damage from any future
    /// "lands but never reads active" bug rather than one specific cause.
    ///
    /// Must be called with _isForceRebuffing already false: while it is set,
    /// IsBuffActive answers from _forceRebuffCastFamilies and every family in
    /// that set reads active by construction.
    /// </summary>
    /// <summary>Stored expiry for a family across BOTH timer dictionaries
    /// (player buffs in _ramBuffTimers, item enchants in _itemSpellTimers), or
    /// DateTime.MinValue when the family has no timer at all.</summary>
    private DateTime CurrentExpiryFor(int family)
    {
        // An item-granted permanent isn't ours when CastBuffsOverItemBuffs is on (only possible
        // between the setting being switched on and the next live read).
        if (_ramBuffTimers.TryGetValue(family, out RamTimerInfo? t) && !(t.IsPermanent && _settings.CastBuffsOverItemBuffs))
            return t.Expiration;
        if (_itemSpellTimers.TryGetValue(family, out ItemSpellRecord? i)) return i.ExpiresAt;
        return DateTime.MinValue;
    }

    private void AuditBatchSatisfied()
    {
        foreach (int family in _forceRebuffCastFamilies)
        {
            if (!_lastCastSpellIdByFamily.TryGetValue(family, out int spellId) || spellId == 0)
                continue;

            if (IsBuffActive(spellId))
            {
                _unsatisfiedStrikes.Remove(family); // satisfied — forget past strikes
                continue;
            }

            // Give the refresh time to reach the registry before judging. A cast
            // issued seconds ago legitimately still reads its old timer.
            if (!_castIssuedAtByFamily.TryGetValue(family, out DateTime castAt)
                || (RynthClock.Now - castAt).TotalMilliseconds < AuditSettleMs)
                continue;

            // The expiry moved forward, so the cast DID land — this family is
            // simply still inside the rebuff window for some other reason (a
            // duration shorter than RebuffSecondsRemaining, say). Not "cast that
            // never takes effect", so it must not accumulate strikes.
            DateTime expiryNow = CurrentExpiryFor(family);
            if (_expiryAtCastByFamily.TryGetValue(family, out DateTime expiryThen)
                && expiryNow > expiryThen)
            {
                _unsatisfiedStrikes.Remove(family);
                continue;
            }

            string name = SpellTableStub.GetById(spellId)?.Name ?? spellId.ToString();
            int strikes = _unsatisfiedStrikes.TryGetValue(family, out int prev) ? prev + 1 : 1;
            _unsatisfiedStrikes[family] = strikes;

            if (strikes < UnsatisfiedStrikeThreshold)
            {
                _host.Log($"[BuffDiag] batch audit: '{name}' (id={spellId}, fam={family}) was cast this batch but still reads inactive — strike {strikes}/{UnsatisfiedStrikeThreshold}.");
                continue;
            }

            _unsatisfiedStrikes.Remove(family);
            _buffFailCooldownUntil[family] = RynthClock.Now + SilentNoShowCooldown;

            // Spell out the stored timer: the two causes that produce this look
            // identical from the outside. Either a lower-tier enchantment is
            // stuck in the family (storedLvl below the tier we cast), or
            // RebuffSecondsRemaining is set above this spell's own duration, so
            // it is "due for recast" the instant it lands. The numbers below
            // separate them at a glance.
            string stored = _ramBuffTimers.TryGetValue(family, out RamTimerInfo? t)
                ? $"stored='{t.SpellName}' lvl={t.SpellLevel} remainSec={(t.IsPermanent ? "permanent" : (t.Expiration - RynthClock.Now).TotalSeconds.ToString("F0"))}"
                : "no timer entry";
            _host.Log($"[BuffDiag] batch audit: '{name}' (id={spellId}, fam={family}) lands but never reads active after {UnsatisfiedStrikeThreshold} batches — " +
                      $"parking {SilentNoShowCooldown.TotalMinutes:0}min so it can't drive continuous rebuff cycles. " +
                      $"{stored}, rebuffThresholdSec={_settings.RebuffSecondsRemaining}.");
        }
    }

    internal bool CheckVitals()   // internal for the host tests (VitalsTests)
    {
        // Combat has the tick (BuffManager.HealHold.cs): no stance change or wield for a vital this pass.
        _vitalsNoStanceChange = HealHoldReleased && CurrentCombatMode != CombatMode.Magic;
        _vitalsNoPeaceSwitch = HealHoldReleased;   // no peace switch for a kit either, Magic stance included
        try { return CheckVitalsCore(); }
        finally { _vitalsNoStanceChange = false; _vitalsNoPeaceSwitch = false; }
    }

    private bool CheckVitalsCore()
    {
        int curHealthPct = _vitals.HealthPct;
        int curManaPct   = _vitals.ManaPct;
        int curStamPct   = _vitals.StaminaPct;

        // Reset per-tick recharge flags; they reflect "would recharge right now"
        // and are repopulated from the predicates below. The /ra buff snapshot
        // reads these for diagnostics.
        _isHealingSelf       = false;
        _isRechargingMana    = false;
        _isRechargingStamina = false;

        // A kit's heal hasn't landed yet: nothing else for vitals until it has (see KitPending).
        if (KitPending())
        {
            _isHealingSelf = true;
            Hold(_kitIsStamina ? "stamina kit in flight" : "healing kit in flight");
            return true;
        }

        // Emergency override regardless of mode, ahead of the configurable Heal At /
        // Top Off thresholds so even a "do nothing" recharge config still saves the
        // character. Two independent lines (each 0 = off):
        //   Emergency Heal At (EmergencyHealAt, default 30): healing kits (and, while a
        //     cast is pending, kits and potions - TryEmergencyHealWhilePending).
        //   Stamina To Health At (StaminaToHealthAt, default 30) with stamina over
        //     Stamina To Health Min Stamina (StaminaToHealthMinStamina, default 20):
        //     burn stamina for health with Stamina to Health Self.
        // Order:
        //   1. Out of Magic mode, a kit first: the spell needs a wand swap (seconds with
        //      no heal and no attack at critical health). Used when either line is crossed.
        //   2. Stamina to Health Self, when its own line and stamina floor allow.
        //   3. Otherwise fall through to the normal chain below (kit, Heal Self, potion)
        //      rather than giving up at critical health.
        if (TryEmergencyVitals(curHealthPct, curStamPct))
        {
            _isHealingSelf = true;
            return true;
        }

        // Pick the threshold set based on hunting state. A target within
        // MonsterRange = active combat → react at the LOW (HealAt / RestamAt /
        // GetManaAt) thresholds so we don't bloat cast traffic mid-fight.
        // Otherwise idle → top up to the HIGH (TopOffHP / TopOffStam /
        // TopOffMana) thresholds so we re-engage at full. Thresholds are
        // strict "<": set a value to 0 to disable that vital in that mode.
        bool inCombat = InCombatNow();
        int hpThreshold   = inCombat ? _settings.HealAt    : _settings.TopOffHP;
        int manaThreshold = inCombat ? _settings.GetManaAt : _settings.TopOffMana;
        int stamThreshold = StaminaLine(inCombat, curStamPct, _settings, ref _restamRun, ref _restamRunCasts);

        // A flag is set only when something was actually done about the vital.
        // WantsVitalRecharge holds the arbiter on Buffing, above combat and nav, so
        // flagging a heal that can't happen — spell unknown, Life Magic untrained,
        // or parked after the server refused it (no components) — stalled the bot:
        // no route, no fighting back, until natural regen topped it up.
        // Kits, then spells, then potions (a consumable only when nothing free
        // works: no spell, Life Magic untrained, or out of components).
        if (curHealthPct < hpThreshold)
        {
            // A script holding the heal kind (RynthAi.Combat) is asked first, before kits: a class
            // that heals by draining monsters must not have a kit pre-empt it. The emergency step
            // above stays native (the safety floor). Waiting for the answer counts as handled; a
            // decline, a bad answer or no answer in time falls through to kit, Heal Self, potion.
            if (CombatHook.IsActive(RynthAiCombat.KindHeal) && TryScriptHeal(hpThreshold, inCombat))
            {
                _isHealingSelf = true;
                return true;
            }
            bool healed = AttemptHealChain(itemsBeforeSpell: false, allowSpell: true, out string kitWhy, out string spellWhy, out string potionWhy);
            if (healed)
            {
                _isHealingSelf = true;
                return true;
            }
            _healUnavailableUntil = RynthClock.Now.AddMilliseconds(HealUnavailableMs);
            Hold("nothing could heal", $"kit: {Why(kitWhy)}; Heal Self: {Why(spellWhy)}; potion: {Why(potionWhy)}");
            LogNoHeal($"health {curHealthPct}% < {(inCombat ? "Heal At" : "Top Off HP")} {hpThreshold}% but nothing healed: " +
                      $"kit: {Why(kitWhy)}; Heal Self: {Why(spellWhy)}; potion: {Why(potionWhy)}");
        }
        else if (!inCombat && curHealthPct < _settings.HealAt)
        {
            LogNoHeal($"health {curHealthPct}% < Heal At {_settings.HealAt}% but no monster within MonsterRange " +
                      $"{_settings.MonsterRange} yd, so the idle Top Off HP {hpThreshold}% applies");
        }
        // Reaching here means nothing healed this check. If health is still under a line
        // (e.g. an emergency line above Heal At whose kit failed), back off so WantsHealNow
        // doesn't hold the tick for a heal that can't happen.
        if (HealthUnderAnyLine(curHealthPct, curStamPct))
            _healUnavailableUntil = RynthClock.Now.AddMilliseconds(HealUnavailableMs);

        // Mana: under Get Mana At / Top Off Mana, or (a caster) under the reserve kept for Stamina
        // to Mana (ManaUnderLine; see "Mana reserve" at the top).
        bool manaLow = curManaPct < manaThreshold || ManaUnderLine(inCombat, out _);
        if (manaLow)
        {
            _vitalWhy = "";
            if (AttemptManaRefill(curStamPct, out bool refilledStamina))
            {
                if (refilledStamina) _isRechargingStamina = true; else _isRechargingMana = true;
                return true;
            }
            // Mana can't pay for any Stamina to Mana and nothing else refilled it: a caster's
            // attacks wait while it regenerates (WantsManaNow holds the arbiter, OffensiveCastHold
            // the casts), then Stamina to Mana goes. Not for a melee fighter: its attacks cost none.
            if (FightsWithMagic() && ManaStarved(out int s2mCost, out string s2mName))
            {
                _isRechargingMana = true;
                Hold("mana regenerating", $"mana {_vitals.CurrentMana}/{_vitals.MaxMana}, {s2mName} costs {s2mCost}");
                if (!_manaStarvedLogged)
                {
                    _manaStarvedLogged = true;
                    string costText = s2mCost > 0 ? $"under {s2mName}'s cost ({s2mCost})" : $"too low for {s2mName} (refused for mana)";
                    _host.Log($"[RynthAi] Vitals: mana {_vitals.CurrentMana}/{_vitals.MaxMana} is {costText}: attacks wait, regenerating.");
                }
                return true;
            }
            // Nothing can refill it (no Stamina to Mana, parked, no potion): don't hold the bot for it.
            if (inCombat) _manaUnavailableUntil = RynthClock.Now.AddMilliseconds(ManaUnavailableMs);
        }
        else _manaStarvedLogged = false;
        // Stamina kits first, like healing kits for health: Lucy carried Greater Stamina Kits
        // and cast Revitalize/Robustification instead, because stamina never looked for a kit.
        // Melee first (Tom, 2026-10-06): out of Magic stance, Revitalize means switching the weapon
        // for the wand and back (slow, and the 10-06 stalls began at those switches), so a stamina
        // potion listed in Consumables goes before it. A potion drunk under 2.5 s ago: no Revitalize
        // on top of it. In Magic stance (a caster) Revitalize stays first.
        // No wand swap for the between-fights top-off (the owner, 2026-11-07): with melee or missile
        // weapons in hand, Revitalize means weapons -> peace -> wand -> Magic -> cast -> back, about
        // 5.5 s, and a melee character (Top Off Stam 60, no kit or potion) did it every 2 minutes, often between
        // two corpses or with the next monster walking up. Kits and potions still top off; stamina
        // regenerates otherwise. Under Re-stam At, or in a Re-stam To run, the cast goes as before.
        bool castNeedsSwap = !inCombat && !_restamRun && curStamPct >= _settings.RestamAt
                             && CurrentCombatMode != CombatMode.Magic && !FightsWithMagic();
        int sentBefore = _vitalActionsSent;
        if (curStamPct < stamThreshold && AttemptStaminaChain(allowCast: !castNeedsSwap))
        {
            _isRechargingStamina = true;
            // Only a cast, kit or potion actually sent counts toward the run's cap. The chain also
            // answers "handled" while it waits for the last cast to finish, and counting those ticks
            // ended every run after one Robustification (2.3 s of waiting = the 4-cast cap): Re-stam To
            // 90 stopped near 60 (Drakkon, 2026-10-20).
            if (_restamRun && _vitalActionsSent != sentBefore) _restamRunCasts++;
            return true;
        }
        if (curStamPct < stamThreshold && castNeedsSwap && (RynthClock.Now - _staminaNoSwapLoggedAt).TotalSeconds >= 60)
        {
            _staminaNoSwapLoggedAt = RynthClock.Now;
            _host.Log($"[RynthAi] Vitals: stamina {curStamPct}% < Top Off Stamina {stamThreshold}%, no stamina kit or potion: " +
                      $"no wand swap for it out of a fight (it regenerates); under Re-stam At {_settings.RestamAt}% Revitalize is cast.");
        }

        return false;
    }

    private DateTime _staminaNoSwapLoggedAt = DateTime.MinValue;

    /// <summary>
    /// The heal chain of the vitals step (CheckVitals, and the one-touch heal): a kit, Heal Self and a
    /// potion, or with <paramref name="itemsBeforeSpell"/> the kit and potion before the spell (no wand swap
    /// first, as the emergency step does), then the kit Kit Min Success skipped as the last resort.
    /// <paramref name="allowSpell"/> false: no cast (one is pending and a Heal Self would replace it).
    /// True also while a stance step (peace for the kit, the wand swap) is in flight; the why strings
    /// say what each option did when nothing healed.
    /// </summary>
    private bool AttemptHealChain(bool itemsBeforeSpell, bool allowSpell, out string kitWhy, out string spellWhy, out string potionWhy)
    {
        spellWhy = "";
        string potWhy = "";
        _vitalWhy = "";
        bool healed = AttemptHealthKitUse();
        kitWhy = _vitalWhy;
        if (itemsBeforeSpell && !healed) { _vitalWhy = ""; healed = AttemptPotion("HealthPotion"); potWhy = _vitalWhy; }
        if (!healed)
        {
            _vitalWhy = "";
            healed = allowSpell ? AttemptVitalCast("Heal Self") : VitalNo("a cast is pending");
            spellWhy = _vitalWhy;
        }
        if (!itemsBeforeSpell && !healed) { _vitalWhy = ""; healed = AttemptPotion("HealthPotion"); potWhy = _vitalWhy; }
        // Last resort: the kit Kit Min Success skipped. A low chance beats no heal at all
        // (Drakkon, 2026-10-06: 29% kit skipped, no spell, no potion, dead 6 s later).
        if (!healed && kitWhy.EndsWith("under Kit Min Success %", StringComparison.Ordinal))
        {
            _vitalWhy = "";
            healed = AttemptHealthKitUse(ignoreMinSuccess: true);
            if (healed) _host.Log("[RynthAi] Vitals: no spell or potion could heal; using the kit despite Kit Min Success %.");
        }
        if (!healed) _vitalWhy = potWhy;
        potionWhy = potWhy;
        return healed;
    }

    /// <summary>
    /// The mana step: Stamina to Mana Self or a mana potion; with stamina at or under the Stamina to Mana
    /// floor, stamina is refilled first (kit, Revitalize, potion) so Stamina to Mana can run next.
    /// <paramref name="refilledStamina"/>: it was stamina that was started, not mana.
    /// </summary>
    private bool AttemptManaRefill(int curStamPct, out bool refilledStamina)
    {
        refilledStamina = false;
        if ((curStamPct > StaminaToManaFloorPct() && AttemptVitalCast("Stamina to Mana Self")) || AttemptPotion("ManaPotion"))
            return true;
        // Stamina too low for Stamina to Mana (StaminaToManaFloorPct: Re-stam At, at least 15): a
        // stamina kit, Revitalize or a potion first.
        if (curStamPct <= StaminaToManaFloorPct() && TopStaminaToManaId() != 0
            && AttemptStaminaRestore())
        {
            refilledStamina = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The stamina step: a kit, a listed potion first out of Magic stance, Revitalize, then any potion
    /// (the reasons are in the comment above CheckVitals' stamina line). <paramref name="allowCast"/>
    /// false: kits and potions only (no wand swap for the idle top-off).
    /// </summary>
    private bool AttemptStaminaChain(bool allowCast = true)
    {
        bool potionFirst = CurrentCombatMode != CombatMode.Magic;
        bool potionJustDrunk = potionFirst && _settings.UsePotions
            && (RynthClock.Now - _lastPotionAt).TotalMilliseconds < 2500
            && HasListedPotion("StaminaPotion", "Stamina");
        return !potionJustDrunk && AttemptStaminaRestore(listedPotionFirst: potionFirst, allowCast: allowCast);
    }

    // Re-stam To (2026-10-06): a recharge started under Re-stam At carries on until stamina
    // reaches Re-stam To; one Revitalize often doesn't get far. Capped so a recharge that
    // never gains can't loop: it ends after RestamRunMaxCasts.
    private bool _restamRun;
    private int _restamRunCasts;
    internal const int RestamRunMaxCasts = 4;
    // Vital casts, kits and potions actually sent (a wait for the last one isn't one).
    private int _vitalActionsSent;

    /// <summary>The stamina % to recharge under now (see the fields above).</summary>
    internal static int StaminaLine(bool inCombat, int stamPct, LegacyUiSettings s, ref bool run, ref int casts)
    {
        if (!inCombat)
        {
            // A recharge started in the fight finishes to Re-stam To when the fight ends, instead of
            // dropping to the top-off line (Top Stam can be lower than Re-stam To).
            if (run && s.RestamTo > s.RestamAt && stamPct < s.RestamTo && casts < RestamRunMaxCasts)
                return Math.Max(s.RestamTo, s.TopOffStam);
            // Under Re-stam At with no monster in range starts one too (Drakkon, 10-23): the last swing, or a
            // Stamina to Health just before the kill (174 stamina at once), took it there, so the fight never
            // started the recharge, and the top-off stopped at Top Stam 60 instead of Re-stam To 90.
            if (s.RestamTo > s.RestamAt && stamPct < s.RestamAt)
            {
                run = true;
                casts = 0;
                return Math.Max(s.RestamTo, s.TopOffStam);
            }
            run = false;
            return s.TopOffStam;
        }
        if (s.RestamTo <= s.RestamAt) { run = false; return s.RestamAt; }
        if (stamPct < s.RestamAt)
        {
            if (!run) { run = true; casts = 0; }
        }
        else if (stamPct >= s.RestamTo || casts >= RestamRunMaxCasts)
        {
            run = false;
        }
        return run ? s.RestamTo : s.RestamAt;
    }

    /// <summary>True when Stamina To Health At / Min Stamina allow Stamina to Health Self now.</summary>
    internal static bool StaminaToHealthAllowed(LegacyUiSettings s, int healthPct, int staminaPct)
        => s.StaminaToHealthAt > 0 && healthPct <= s.StaminaToHealthAt && staminaPct > s.StaminaToHealthMinStamina;

    /// <summary>The emergency step of CheckVitals (order in the comment there). True = handled.</summary>
    private bool TryEmergencyVitals(int curHealthPct, int curStamPct)
    {
        int emergencyAt = _settings.EmergencyHealAt;
        bool emergencyKit = emergencyAt > 0 && curHealthPct <= emergencyAt;
        bool stamToHealth = StaminaToHealthAllowed(_settings, curHealthPct, curStamPct);
        if (!emergencyKit && !stamToHealth) return false;

        // 1. Out of Magic mode a kit first (no wand swap at critical health), then a health
        //    potion: Heal Self from melee is a wand swap, the Magic stance and the cast, 4-5 s
        //    with nothing healing. Drakkon, 2026-10-14: 69% to 15% in 0.2 s among Depraved
        //    Shadows, the kit at 3%, and the swap was still unanswered when he died 3.2 s later;
        //    the potion came after Heal Self in the chain, so it was never reached.
        if (CurrentCombatMode != CombatMode.Magic && (AttemptHealthKitUse() || AttemptPotion("HealthPotion"))) return true;
        // 2. Stamina to Health Self, by its own line and stamina floor.
        if (stamToHealth && AttemptVitalCast("Stamina to Health Self")) return true;
        // 3. Not handled: the normal Heal At chain (kit, Heal Self, potion) gets its turn.
        return false;
    }

    private DateTime _lastPendingEmergencyHealAt = DateTime.MinValue;

    /// <summary>
    /// A cast awaiting confirmation holds the tick for up to 2.5 s (self-buff give-up)
    /// or 5 s (armor with no chat), and CheckVitals sits after that hold, so a character
    /// at critical health mid-batch got no kit or potion until the cast resolved. At
    /// Emergency Heal At (EmergencyHealAt, not Stamina To Health At), use a kit or potion (not a spell: that
    /// would replace the pending cast) once the server has finished the cast (UseDone)
    /// and the client isn't busy, so it doesn't draw "You're too busy!".
    /// </summary>
    private void TryEmergencyHealWhilePending()
    {
        int emergencyAt = _settings.EmergencyHealAt;
        if (emergencyAt <= 0 || _vitals.HealthPct > emergencyAt) return;
        if ((RynthClock.Now - _lastPendingEmergencyHealAt).TotalMilliseconds < 2500) return;
        if (IsAwaitingCastResolution() || BusyHoldsHeal() || KitPending()) return;
        if (!CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s))) return;
        if (!ActionPacer.MayStart(_host, "Buff", HealNeed.Emergency, out _)) return;
        if (AttemptHealthKitUse() || AttemptPotion("HealthPotion"))
        {
            _lastPendingEmergencyHealAt = RynthClock.Now;
            _isHealingSelf = true;
        }
    }

    // Why the last kit / vital spell / potion attempt did nothing (the "no heal" log line).
    private string _vitalWhy = "";
    private DateTime _lastNoHealLogAt = DateTime.MinValue;

    private bool VitalNo(string why) { _vitalWhy = why; return false; }

    // The pending cast is a vital one when _pendingSpellId equals this (set by AttemptVitalCast).
    private int _pendingVitalSpellId;
    private DateTime _lastManaFloorLogAt = DateTime.MinValue;
    private static string Why(string why) => why.Length > 0 ? why : "-";

    /// <summary>
    /// One line every 10 s while health is low and nothing heals: which of kit, spell and
    /// potion was skipped and why (a mage stuck between Stamina to Health's 30% and Heal At
    /// healed nothing, and every skip returned silently, 2026-09-30).
    /// </summary>
    private void LogNoHeal(string line)
    {
        if ((RynthClock.Now - _lastNoHealLogAt).TotalSeconds < 10) return;
        _lastNoHealLogAt = RynthClock.Now;
        _host.Log("[RynthAi] Vitals: " + line);
    }

    private bool AttemptVitalCast(string baseName)
    {
        if (!IsSkillUsable(AcSkillType.LifeMagic)) return VitalNo("Life Magic not usable");
        // AC just said "You're too busy!": a cast now draws another refusal and can keep the
        // gesture from finishing (Drakkon, 2026-10-05: Stamina to Mana every 0.4 s until he was
        // moved). Mana and stamina wait out the backoff; a heal still retries, but no faster
        // than BusyHealRetryMs. Waiting counts as handled, so no potion is drunk meanwhile.
        if (RynthClock.Now < _busyBackoffUntil)
        {
            bool healthSpell = baseName is "Heal Self" or "Stamina to Health Self";
            if (!healthSpell || (RynthClock.Now - _lastCastAttempt).TotalMilliseconds < BusyHealRetryMs)
            {
                _vitalWhy = $"AC too busy, waiting {(_busyBackoffUntil - RynthClock.Now).TotalMilliseconds:0} ms";
                _healWaitKind = "too-busy backoff";
                return true;
            }
        }
        // Mana and stamina also wait until the server has finished the last cast (its result, or
        // its UseDone after the incantation; bounded by its give-up time). A heal never waits.
        if (baseName is not ("Heal Self" or "Stamina to Health Self") && IsAwaitingCastResolution())
        {
            _vitalWhy = "the server hasn't finished the last cast";
            _healWaitKind = "server finishing the last cast";
            return true;
        }
        int spellId = _spellManager.GetDynamicSelfBuffId(baseName, AcSkillType.LifeMagic, LevelLacksComponents);
        // A heal with nothing known at the allowed tiers: the lowest known tier above them.
        // Vitae can drop Life Magic under a tier line while only higher tiers are known.
        if (spellId == 0 && baseName is "Heal Self" or "Stamina to Health Self")
        {
            spellId = _spellManager.GetSelfSpellIdAboveAllowedTier(baseName, AcSkillType.LifeMagic);
            if (spellId != 0 && (RynthClock.Now - _lastAboveTierLog).TotalSeconds >= 30)
            {
                _lastAboveTierLog = RynthClock.Now;
                _host.Log($"[RynthAi] Vitals: no '{baseName}' at the tiers Life Magic allows now; " +
                          $"trying {SpellTableStub.GetById(spellId)?.Name ?? spellId.ToString()} (may fizzle).");
            }
        }
        if (spellId == 0) return VitalNo($"no known '{baseName}' spell at the allowed tiers");
        // Stamina to Mana / Revitalize under their cost (the mana reserve, 2026-10-11): the server
        // would refuse it. The highest level mana can pay for goes instead; with none, not handled
        // (a potion gets its turn, or mana regenerates - CheckVitals). Mana Conversion can lower a
        // cost only on a skill roll, so the base cost is the line. A heal still tries: it falls to
        // its potion within LowManaHealRestSec if refused.
        if (baseName is not ("Heal Self" or "Stamina to Health Self") && _vitals.MaxMana > 0)
        {
            int cost = SpellManaCost(spellId);
            if (cost > 0 && _vitals.CurrentMana < cost)
            {
                int payable = AffordableVitalId(baseName);
                if (payable == 0)
                    return VitalNo($"mana {_vitals.CurrentMana} is under {SpellTableStub.GetById(spellId)?.Name ?? baseName}'s cost ({cost})");
                spellId = payable;
            }
        }
        return CastVital(spellId, (uint)_host.GetPlayerId(), VitalLabel(baseName), baseName, out _);
    }

    /// <summary>
    /// The cast step of a vital spell, shared by AttemptVitalCast (self vitals) and the script
    /// hook's heal (a drain at a monster id goes through the same steps): the family park, the
    /// wand swap / Magic stance, the pending-vital bookkeeping (confirmed by chat; released with
    /// no strike when the line is late) and the heal latency. Returns "handled" as
    /// AttemptVitalCast does (true also while a swap step is in flight); <paramref name="sent"/>
    /// says whether the cast actually went out.
    /// </summary>
    private bool CastVital(int spellId, uint targetId, string label, string what, out bool sent)
    {
        sent = false;
        // Parked after a hard server refusal (see OnChatWindowText) — the same
        // per-family cooldown the buff selectors honour.
        int family = SpellTableStub.GetById(spellId)?.Family ?? 0;
        if (family != 0
            && _buffFailCooldownUntil.TryGetValue(family, out DateTime coolUntil)
            && RynthClock.Now < coolUntil)
            return VitalNo($"parked for {(coolUntil - RynthClock.Now).TotalSeconds:0} s more after a refusal or no-show");
        // A recovery vital retried after a mana refusal: its retry gap and the per-second ceiling.
        if (IsRecoveryVital(what) && RecoveryRetryHolds(family) is { Length: > 0 } recoveryWhy)
            return VitalNo(recoveryWhy);
        // No wand swap for a vital spell while a kit or potion heals (BuffManager.VitalWandGate.cs).
        if (VitalSpellWouldSwap(label, what, out string swapWhy))
            return VitalNo(swapWhy);
        if (CurrentCombatMode != CombatMode.Magic && RynthClock.Now < _vitalSwapBlockedUntil)
            return VitalNo("wand swap blocked, retrying shortly");
        if (_vitalsNoStanceChange)
        {
            NoteHealHoldRefusal(peace: false, what);
            return VitalNo("combat has the tick (no heal landed): no wand swap for a spell");
        }
        bool urgentHeal = label == LabelHealing && (_oneTouchHealthScope || HealIsUrgent(_vitals.HealthPct, _vitals.StaminaPct));
        if (!EnsureMagicMode(urgentHeal: urgentHeal))
        {
            if (!_magicModeBlocked) { _healWaitKind = "wand swap / Magic stance"; return true; }   // a swap step is in flight: yield the tick
            // The switch can't complete now (no room to put the bow away, the wand won't
            // wield): not handled, so the kit/potion after this gets its turn.
            _vitalSwapBlockedUntil = RynthClock.Now.AddSeconds(VitalSwapBlockedRetrySec);
            return VitalNo("couldn't switch to Magic mode");
        }
        // A locally rejected cast sends nothing, so no chat or registry change will
        // ever come; setting pending anyway waited 2.5 s and counted a no-show
        // strike. Mirror the buff path: not handled, so the potion gets its turn.
        _pendingSpellId = spellId;
        _pendingShieldId = 0;   // on the player, not the shield
        _pendingVitalSpellId = spellId;   // instant: confirmed by chat, never by the registry
        _pendingVitalLabel = label;
        _pendingVitalBase = what;
        bool castOk = _host.CastSpellFor(targetId, spellId, "Buff",
            SpellTableStub.GetById(spellId)?.Name ?? what);
        _lastCastAttempt = RynthClock.Now;
        if (!castOk)
        {
            _pendingSpellId = 0;
            return VitalNo($"the client refused the cast (spell {spellId})");
        }
        sent = true;
        _vitalActionsSent++;
        _lastVitalCastAt = RynthClock.Now;
        if (IsRecoveryVital(what)) NoteRecoveryAttempt();
        NoteCastIssued(spellId);
        if (_pendingVitalLabel == LabelMana) _manaStarvedLogged = false;   // the next starved spell logs again
        if (_pendingVitalLabel == LabelHealing)
        {
            _healSpellIds.Add(spellId);
            NoteHealSent(what);
        }
        return true;
    }

    private static bool IsHealthPotionType(string[] types) =>
        Array.FindIndex(types, t => t.Equals("HealthPotion", StringComparison.OrdinalIgnoreCase)) >= 0;

    /// <summary>
    /// Drinks the first potion of <paramref name="types"/> from the Items panel.
    /// "Stamina" is the older name for stamina potions. Off with UsePotions.
    /// </summary>
    private bool AttemptPotion(params string[] types) => AttemptPotion(false, types);

    /// <summary>A potion of one of <paramref name="types"/> listed in Consumables and in the pack.</summary>
    private bool HasListedPotion(params string[] types)
    {
        if (_worldObjectCache == null) return false;
        foreach (var rule in _settings.ConsumableRules)
            if (!IsKitName(rule.Name)
                && ConsumableLookup.Resolve(_worldObjectCache, rule) is WorldObject wo
                && Array.FindIndex(types, t => t.Equals(DrinkType(rule, wo), StringComparison.OrdinalIgnoreCase)) >= 0)
                return true;
        return false;
    }

    /// <summary>
    /// A healing or stamina kit: used on yourself (the kit path), never drunk. A Greater Stamina
    /// Kit on the Items list was typed "Stamina", the old name for stamina potions, and the potion
    /// step used it with no target every 3 s, the server never answering, Revitalize never cast
    /// (Drakkon, 2026-10-17).
    /// </summary>
    internal static bool IsKitName(string? name) =>
        !string.IsNullOrEmpty(name) && name.TrimEnd().EndsWith(" Kit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A healing kit, used on yourself: the kit class, or a name ending in "Kit". Any other item
    /// listed as a healing item is used with no target, like a potion. Conquest's Health Tincture
    /// (no kit, not Food) was typed HealthKit for the "Health" in its name, used on the player
    /// and refused by the server ("can't use Health Tincture on ...") every 8 s, the bot never
    /// healing until the player did it by hand (report R-20261103-022318-418c, 2026-11-02).
    /// </summary>
    internal static bool IsUseOnSelfKit(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.HealingKit || IsKitName(wo.Name);

    /// <summary>The potion type <paramref name="rule"/> is drunk as: a HealthKit rule on an item that is no kit is a health potion.</summary>
    internal static string DrinkType(ConsumableRule rule, WorldObject wo) =>
        rule.Type.Equals("HealthKit", StringComparison.OrdinalIgnoreCase) && !IsUseOnSelfKit(wo) ? "HealthPotion" : rule.Type;

    /// <summary><paramref name="listedOnly"/>: only the Consumables list, not any potion in the pack.</summary>
    private bool AttemptPotion(bool listedOnly, params string[] types)
    {
        if (!_settings.UsePotions || _worldObjectCache == null) return VitalNo("Use Potions is off");
        if ((RynthClock.Now - _lastPotionAt).TotalMilliseconds < 2500) return VitalNo("one drunk under 2.5 s ago");
        foreach (var rule in _settings.ConsumableRules)
        {
            if (IsKitName(rule.Name)) continue;   // the kit path uses it, on yourself
            if (ConsumableLookup.Resolve(_worldObjectCache, rule) is not WorldObject potion) continue;
            if (IsKitName(potion.Name)) continue;
            if (Array.FindIndex(types, t => t.Equals(DrinkType(rule, potion), StringComparison.OrdinalIgnoreCase)) < 0) continue;
            if (HeldCastBlocksItemUse(out string heldWhy)) return VitalNo(heldWhy);
            _host.Log($"[RynthAi] Vitals: drinking {rule.Name} ({rule.Type})" + (potion.Id != rule.Id ? " from a new stack." : "."));
            _host.UseFor(unchecked((uint)potion.Id), "Buff", $"vitals: drink {rule.Name} ({rule.Type}) from the Items list");
            _lastPotionAt = RynthClock.Now;
            _vitalActionsSent++;
            _lastCastAttempt = RynthClock.Now;
            if (IsHealthPotionType(types)) NoteHealSent(rule.Name);
            return true;
        }
        if (listedOnly) return VitalNo("none listed in Consumables");
        // Nothing on the Items panel: anything in the pack of that kind (Field Rations
        // weren't used unless someone added them by hand, 2026-09-29).
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string? kind = RynthCore.Plugin.RynthAi.LegacyUi.LegacyWeaponsUi.PotionType(wo);
            if (kind == null || Array.FindIndex(types, t => t.Equals(kind, StringComparison.OrdinalIgnoreCase)) < 0) continue;
            if (IsKitName(wo.Name)) continue;
            if (HeldCastBlocksItemUse(out string heldWhy)) return VitalNo(heldWhy);
            _host.Log($"[RynthAi] Vitals: using {wo.Name} from the pack ({kind}).");
            _host.UseFor(unchecked((uint)wo.Id), "Buff", $"vitals: {kind} potion from the pack");
            _lastPotionAt = RynthClock.Now;
            _vitalActionsSent++;
            _lastCastAttempt = RynthClock.Now;
            if (IsHealthPotionType(types)) NoteHealSent(wo.Name ?? "health potion");
            return true;
        }
        return VitalNo("none in the Items list or the pack");
    }

    // ── Buff items: unlimited gems that cast a buff when used ─────────────
    // name -> the spell ids that mean its buff is already on (Benediction and the
    // Lesser one give the same +10% max health, so either counts for both).
    private static readonly (string Name, uint[] Spells)[] BuffItems =
    {
        ("Asheron's Benediction",        new uint[] { 3810, 4024 }),
        ("Asheron's Lesser Benediction", new uint[] { 3810, 4024 }),
        ("Blackmoor's Favor",            new uint[] { 3811 }),
    };
    private readonly Dictionary<string, DateTime> _buffItemUsedAt = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastBuffItemCheck = DateTime.MinValue;

    /// <summary>
    /// Uses a buff item whose buff isn't on the player (Asheron's Benediction, Blackmoor's
    /// Favor). They were never used. One item per check, 3 s apart; 15 s before the same
    /// item again so its buff can land. Off with UseBuffItems.
    /// </summary>
    private void TryUseBuffItems()
    {
        if (!_settings.UseBuffItems || _worldObjectCache == null || !_host.HasReadPlayerEnchantments) return;
        if ((RynthClock.Now - _lastBuffItemCheck).TotalMilliseconds < 3000) return;
        _lastBuffItemCheck = RynthClock.Now;
        if (!_host.CanCastNow) return;

        var active = new HashSet<uint>();
        var sids = new uint[256];
        var exp = new double[256];
        int n = _host.ReadPlayerEnchantments(sids, exp, sids.Length);
        for (int i = 0; i < n && i < sids.Length; i++) active.Add(sids[i]);

        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            foreach (var (name, spells) in BuffItems)
            {
                if (!wo.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                bool on = false;
                foreach (uint s in spells) if (active.Contains(s)) { on = true; break; }
                if (on) break;
                if (_buffItemUsedAt.TryGetValue(name, out DateTime at) && (RynthClock.Now - at).TotalSeconds < 15) break;
                _buffItemUsedAt[name] = RynthClock.Now;
                // The same buff from the other Benediction counts as done too.
                foreach (var (other, os) in BuffItems) if (os == spells || os[0] == spells[0]) _buffItemUsedAt[other] = RynthClock.Now;
                _host.Log($"[RynthAi] Buff item: using {wo.Name} (its buff isn't on).");
                _host.UseFor(unchecked((uint)wo.Id), "Buff", $"buff item: the buff of {wo.Name} is not on");
                _lastCastAttempt = RynthClock.Now;
                return;
            }
        }
    }

    // ── Field rations: Cooking Pot on (Infinite) Dried Rations, 25 at a time ──
    private DateTime _lastRationAttempt = DateTime.MinValue, _rationRestUntil = DateTime.MinValue;
    private int _rationTries, _rationCountBefore = -1;

    /// <summary>
    /// Cooks field rations when the pack holds fewer than MakeRationsBelow: Elaborate from
    /// (Infinite) Elaborate Dried Rations, else Simple from Simple ones. Not in a fight.
    /// Three tries that don't raise the count (Cooking too low, missing pot) rest it 10 min.
    /// </summary>
    private void TryMakeRations()
    {
        int want = _settings.MakeRationsBelow;
        if (want <= 0 || _worldObjectCache == null || !_host.HasUseObjectOn) return;
        if (RynthClock.Now < _rationRestUntil) return;
        if ((RynthClock.Now - _lastRationAttempt).TotalSeconds < 6) return;
        if (_combatManager?.HasCloseThreat(Math.Max(1, _settings.MonsterRange)) == true) return;
        if (!_host.CanCastNow) return;

        int pot = 0, elaborateDried = 0, simpleDried = 0, elaborate = 0, simple = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string nm = wo.Name ?? "";
            int count = Math.Max(1, wo.Values(LongValueKey.StackCount, 1));
            if (nm.Equals("Cooking Pot", StringComparison.OrdinalIgnoreCase)) pot = wo.Id;
            else if (nm.EndsWith("Elaborate Dried Rations", StringComparison.OrdinalIgnoreCase))
            { if (elaborateDried == 0 || nm.StartsWith("Infinite", StringComparison.OrdinalIgnoreCase)) elaborateDried = wo.Id; }
            else if (nm.EndsWith("Simple Dried Rations", StringComparison.OrdinalIgnoreCase))
            { if (simpleDried == 0 || nm.StartsWith("Infinite", StringComparison.OrdinalIgnoreCase)) simpleDried = wo.Id; }
            else if (nm.Equals("Elaborate Field Rations", StringComparison.OrdinalIgnoreCase)) elaborate += count;
            else if (nm.Equals("Simple Field Rations", StringComparison.OrdinalIgnoreCase)) simple += count;
        }
        if (pot == 0) return;
        int dried = elaborateDried != 0 ? elaborateDried : simpleDried;
        int have = elaborateDried != 0 ? elaborate : simple;
        if (dried == 0 || have >= want) { _rationTries = 0; _rationCountBefore = -1; return; }

        if (_rationCountBefore >= 0 && have <= _rationCountBefore) _rationTries++;
        else _rationTries = 0;
        if (_rationTries >= 3)
        {
            _rationTries = 0;
            _rationCountBefore = -1;
            _rationRestUntil = RynthClock.Now.AddMinutes(10);
            _host.WriteToChat("[RynthAi] Couldn't cook field rations (3 tries). Is your Cooking high enough? Trying again in 10 minutes.", 2);
            return;
        }
        _rationCountBefore = have;
        _lastRationAttempt = RynthClock.Now;
        _lastCastAttempt = RynthClock.Now;
        _host.Log($"[RynthAi] Rations: {have} left (< {want}), cooking with the Cooking Pot.");
        _host.UseOnFor(unchecked((uint)pot), unchecked((uint)dried), "Buff", $"rations: {have} left, cooking more");
    }
    private DateTime _lastPotionAt = DateTime.MinValue;

    private DateTime _lastAboveTierLog = DateTime.MinValue;

    private bool AttemptHealthKitUse(bool ignoreMinSuccess = false)
    {
        if (_worldObjectCache == null) return false;
        // VTank's kit options: skip kits in magic mode unless allowed; optionally
        // drop to peace mode first (kits work best out of combat stance).
        int mode = CurrentCombatMode;
        if (mode == CombatMode.Magic && !_settings.UseKitsInMagicMode) return VitalNo("in Magic mode (Use Kits in Magic Mode is off)");
        // The first kit on the Items panel that's still there, else the best healing kit in the
        // pack. Rules name one exact kit, which vanishes when its uses run out (Lucy carried
        // Peerless, Gifted and Adept kits with none listed and healed with spells, 2026-09-29).
        WorldObject? kit = null;
        bool fromPack = false;
        foreach (var r in _settings.ConsumableRules)
            if (r.Type.Equals("HealthKit", StringComparison.OrdinalIgnoreCase)
                && !(r.Name ?? "").Contains("Stamina Kit", StringComparison.OrdinalIgnoreCase)   // a stamina kit heals no health
                && ConsumableLookup.Resolve(_worldObjectCache, r, KitUsedUp) is WorldObject wo
                && IsUseOnSelfKit(wo)) { kit = wo; break; }   // no kit: the potion step drinks it
        if (kit == null) { kit = BestPackHealingKit(); fromPack = true; }
        if (kit == null) return VitalNo("no healing kit");
        if (KitRetryWait(out string kitWait)) return VitalNo(kitWait);
        // Too likely to fail on what's missing (KitMinSuccessPct): let the spell have it.
        uint missing = _vitals.MaxHealth > _vitals.CurrentHealth ? _vitals.MaxHealth - _vitals.CurrentHealth : 0;
        // Combat has the tick (BuffManager.HealHold.cs): no peace switch goes out, so the chance is the stance it is in.
        if (!ignoreMinSuccess && !KitChanceOk(kit.Id, kit.Name ?? "", missing, _settings.PeaceModeForKits && !_vitalsNoPeaceSwitch, _settings.KitMinSuccessPct)) return VitalNo($"{kit.Name} under Kit Min Success %");
        if (_settings.PeaceModeForKits && mode != CombatMode.NonCombat && _host.HasChangeCombatMode && _vitalsNoPeaceSwitch)
            NoteHealHoldRefusal(peace: true, "the healing kit");
        if (_settings.PeaceModeForKits && mode != CombatMode.NonCombat && _host.HasChangeCombatMode && !_vitalsNoPeaceSwitch)
        {
            // Only a request that went out starts the wait: a refusal (the engine turns stance changes away
            // around a teleport) leaves no pacing state, so the next tick asks again.
            if (_host.ChangeCombatModeFor(CombatMode.NonCombat, "Buff", "peace mode for the healing kit"))
            {
                _lastCastAttempt = RynthClock.Now;
                _healWaitKind = "peace mode for the kit";
            }
            return true;   // use the kit next tick, once in peace mode
        }

        uint playerId = _host.GetPlayerId();
        if (playerId == 0) return false;
        if (HeldCastBlocksItemUse(out string heldWhy)) return VitalNo(heldWhy);
        if (fromPack) _host.Log($"[RynthAi] Vitals: using {kit.Name} from the pack.");
        _host.UseOnFor(unchecked((uint)kit.Id), playerId, "Buff", "vitals: healing kit on yourself");
        _lastCastAttempt = RynthClock.Now;
        MarkKitUsed();
        NoteKitInFlight(kit);
        NoteHealSent(kit.Name ?? "healing kit");
        return true;
    }

    /// <summary>
    /// Uses the best stamina kit in the pack on the player, with the same rules as healing
    /// kits: not in Magic mode unless UseKitsInMagicMode, peace mode first if PeaceModeForKits.
    /// </summary>
    private bool AttemptStaminaKitUse() => AttemptStaminaKit(false, out _);

    /// <summary><paramref name="underMinSuccess"/>: a kit was there and skipped only for Stamina Kit Min Success %;
    /// <paramref name="ignoreMinSuccess"/> uses it anyway (AttemptStaminaRestore's last resort).</summary>
    private bool AttemptStaminaKit(bool ignoreMinSuccess, out bool underMinSuccess)
    {
        underMinSuccess = false;
        if (_worldObjectCache == null || !_host.HasUseObjectOn) return false;
        int mode = CurrentCombatMode;
        if (mode == CombatMode.Magic && !_settings.UseKitsInMagicMode) return false;
        WorldObject? kit = BestPackStaminaKit();
        if (kit == null) return false;
        if (KitRetryWait(out _)) return false;
        uint missing = _vitals.MaxStamina > _vitals.CurrentStamina ? _vitals.MaxStamina - _vitals.CurrentStamina : 0;
        // Its own line (Stamina Kit Min Success %): below it Revitalize Self or a potion goes.
        // Combat has the tick (BuffManager.HealHold.cs): no peace switch goes out, so the chance is the stance it is in.
        if (!ignoreMinSuccess && !KitChanceOk(kit.Id, kit.Name ?? "", missing, _settings.PeaceModeForKits && !_vitalsNoPeaceSwitch, _settings.StaminaKitMinSuccessPct))
        {
            underMinSuccess = true;
            return false;
        }
        if (_settings.PeaceModeForKits && mode != CombatMode.NonCombat && _host.HasChangeCombatMode && _vitalsNoPeaceSwitch)
            NoteHealHoldRefusal(peace: true, "the stamina kit");
        if (_settings.PeaceModeForKits && mode != CombatMode.NonCombat && _host.HasChangeCombatMode && !_vitalsNoPeaceSwitch)
        {
            if (_host.ChangeCombatModeFor(CombatMode.NonCombat, "Buff", "peace mode for the stamina kit"))
                _lastCastAttempt = RynthClock.Now;
            return true;   // use the kit next tick, once in peace mode
        }
        uint playerId = _host.GetPlayerId();
        if (playerId == 0) return false;
        if (HeldCastBlocksItemUse(out _)) return false;
        _host.Log($"[RynthAi] Vitals: using {kit.Name} from the pack.");
        _host.UseOnFor(unchecked((uint)kit.Id), playerId, "Buff", "vitals: stamina kit on yourself");
        _vitalActionsSent++;
        _lastCastAttempt = RynthClock.Now;
        MarkKitUsed(stamina: true);
        NoteKitInFlight(kit);
        return true;
    }

    // ── Kit result wait ──────────────────────────────────────────────────────
    // ACE (Healer.cs) updates the vital and says "You heal yourself for N ..." before UseDone
    // clears the busy state, but _vitals can lag that, so the next tick still saw low health
    // and used a second kit, every time (2026-10-03). After a kit, vitals wait for the
    // kit's own result line (heal, fail, movement disrupted, already at full), plus
    // KitLandGraceMs for the vitals to catch up, or KitResultTimeoutMs with no line at all.
    //
    // The heal grace (2026-11-07): a kit with no result line held every vital for 8 s, the old
    // KitResultTimeoutMs, with the label reading Healing and nothing healing. ACE answers a kit it
    // won't play (an item the player no longer has, a target it can't heal) with a bare UseDone and
    // no chat, and sends a kit's result line before its UseDone. So (KitWaitVerdict):
    //   - a UseDone(0) for the kit with no line ends the wait KitLandGraceMs later (none is coming),
    //     unless the heal motion is still playing (that UseDone was another action's);
    //   - a kit the server shows no sign of having taken (no heal motion since the send, no UseDone,
    //     no line) is let go after ActionPacer.HealGraceMs, and the next tick may send it again;
    //   - a started kit is waited for KitResultTimeoutMs at most (a healthy kit answers in 2.5 s).
    private DateTime _kitUsedAt = DateTime.MinValue;
    private DateTime _kitLandedAt = DateTime.MinValue;
    private DateTime _kitAnsweredAt = DateTime.MinValue;   // the kit's own UseDone(0), no line yet
    private bool _kitSawAnimating;                          // the motion queue read busy since the send
    // 3.5 s: the longest healthy kit answer is 2.5 s (ActionPacer's time limits). Stamina kits share it.
    internal const double KitResultTimeoutMs = 3500;
    private const double KitLandGraceMs = 700;
    /// <summary>Kits let go with no result line (heal grace or the time limit) (tests, status).</summary>
    internal int KitRetries { get; private set; }

    internal enum KitWaitEnd { Waiting, Landed, Answered, NotTaken, TimedOut }

    /// <summary>
    /// The kit wait, from what has been seen since the send (pure; a ms value below 0 = not seen): the
    /// result line plus KitLandGraceMs; the kit's UseDone(0) with no line plus KitLandGraceMs, with no
    /// motion playing; nothing at all from the server (<paramref name="serverTookIt"/> false) and no
    /// motion playing after ActionPacer.HealGraceMs; or KitResultTimeoutMs whatever happened.
    /// </summary>
    internal static KitWaitEnd KitWaitVerdict(double msSinceUse, double msSinceLanded, double msSinceAnswered,
        bool serverTookIt, bool animatingNow)
    {
        if (msSinceLanded >= 0) return msSinceLanded >= KitLandGraceMs ? KitWaitEnd.Landed : KitWaitEnd.Waiting;
        if (msSinceAnswered >= KitLandGraceMs && !animatingNow) return KitWaitEnd.Answered;
        if (!serverTookIt && !animatingNow && msSinceUse >= ActionPacer.HealGraceMs) return KitWaitEnd.NotTaken;
        if (msSinceUse >= KitResultTimeoutMs) return KitWaitEnd.TimedOut;
        return KitWaitEnd.Waiting;
    }

    // A refused kit (2026-10-05): ACE answers a kit used while the character is busy (a swing, a
    // cast still resolving), in the air, or on something it can't heal with a UseDone error and
    // no chat result (Healer.cs: YoureTooBusy, YouCantDoThatWhileInTheAir, YouCantHealThat). No
    // result line ever came, so every vital waited the full KitResultTimeoutMs, 8 s with no heal
    // at all (CheckVitals returns at KitPending, Heal Self included). Now the kit's refusal ends
    // the wait: a UseDone with an error after the kit went out, or "You're too busy!" within
    // KitRefusalWindowMs of it. The next kit waits KitRetryAfterRefusalMs, so Heal Self or a
    // potion gets the turn first and a refusal can't become a kit every tick.
    private int _kitSeqAtUse;
    private DateTime _kitRefusedAt = DateTime.MinValue;
    private const double KitRefusalWindowMs = 3000;
    private const double KitRetryAfterRefusalMs = 1000;
    /// <summary>Kit uses the server refused (tests, status).</summary>
    internal int KitRefusals { get; private set; }

    internal void MarkKitUsed(bool stamina = false)   // internal for the host tests (VitalsTests)
    {
        _kitUsedAt = RynthClock.Now;
        _kitLandedAt = DateTime.MinValue;
        _kitAnsweredAt = DateTime.MinValue;
        _kitSawAnimating = false;
        _kitIsStamina = stamina;
        _kitSeqAtUse = UseDoneSeqNow();
    }

    private bool MotionQueueBusy => _host.HasGetCastBusyState && !_host.CanCastNow;

    /// <summary>
    /// The server has shown it took the kit in flight (its heal motion, or its UseDone(0)). An engine
    /// with neither GetCastBusyState nor the UseDone sequence can show neither, so there the kit counts
    /// as taken and only KitResultTimeoutMs ends the wait (no resend at the heal grace).
    /// </summary>
    private bool KitServerTookIt() =>
        (!_host.HasGetCastBusyState && !_host.HasUseDoneSeq) || _kitSawAnimating || _kitAnsweredAt != DateTime.MinValue;

    /// <summary>
    /// Records what the server has done with the kit in flight: the heal motion playing (the client's
    /// motion queue busy) or the kit's UseDone(0), the first UseDone after the send (as the pacer pairs
    /// a cast). Every heartbeat, and from KitPending.
    /// </summary>
    private void NoteKitProgress()
    {
        if (_kitUsedAt == DateTime.MinValue || _kitLandedAt != DateTime.MinValue) return;
        if (!_kitSawAnimating && MotionQueueBusy) _kitSawAnimating = true;
        if (_kitAnsweredAt == DateTime.MinValue && _host.HasUseDoneSeq && _host.TryGetLastUseDone(out int seq, out uint err)
            && seq == _kitSeqAtUse + 1 && err == 0)
            _kitAnsweredAt = RynthClock.Now;
    }

    internal bool KitPending()
    {
        if (_kitUsedAt == DateTime.MinValue) return false;
        // Refused by the server: a UseDone carrying an error since the kit went out.
        if (_kitLandedAt == DateTime.MinValue && _host.TryGetLastUseDone(out int seq, out uint err)
            && seq != _kitSeqAtUse && err != 0)
        {
            KitRefused($"UseDone error 0x{err:X}");
            return false;
        }
        NoteKitProgress();
        DateTime now = RynthClock.Now;
        double sinceUse = (now - _kitUsedAt).TotalMilliseconds;
        var end = KitWaitVerdict(sinceUse,
            _kitLandedAt != DateTime.MinValue ? (now - _kitLandedAt).TotalMilliseconds : -1,
            _kitAnsweredAt != DateTime.MinValue ? (now - _kitAnsweredAt).TotalMilliseconds : -1,
            KitServerTookIt(), MotionQueueBusy);
        if (end == KitWaitEnd.Waiting) return true;
        if (end != KitWaitEnd.Landed)
        {
            KitRetries++;
            string what = end switch
            {
                KitWaitEnd.Answered => "was answered (UseDone) with no result line",
                KitWaitEnd.NotTaken => "shows no sign the server took it (no motion, no UseDone, no result line)",
                _ => "has no result line yet",
            };
            _host.Log($"[RynthAi] Vitals: the {(_kitIsStamina ? "stamina" : "healing")} kit {what} {sinceUse:0} ms after use: " +
                      "vitals carry on (heal grace), the next tick may send it again.");
        }
        _kitUsedAt = DateTime.MinValue;
        _kitLandedAt = DateTime.MinValue;
        _kitAnsweredAt = DateTime.MinValue;
        return false;
    }

    /// <summary>The kit in flight was refused: no result line will come, so stop waiting for one.</summary>
    private void KitRefused(string why)
    {
        if (_kitUsedAt == DateTime.MinValue || _kitLandedAt != DateTime.MinValue) return;
        double ms = (RynthClock.Now - _kitUsedAt).TotalMilliseconds;
        _kitUsedAt = DateTime.MinValue;
        _kitRefusedAt = RynthClock.Now;
        KitRefusals++;
        NoteKitRefusedStreak(_kitInFlightId);   // the vital wand gate stops counting a kit refused again and again
        _host.Log($"[RynthAi] Vitals: the {(_kitIsStamina ? "stamina" : "healing")} kit was refused {ms:0} ms after use ({why}): " +
                  $"no result will come, vitals carry on (next kit in {KitRetryAfterRefusalMs:0} ms).");
    }

    /// <summary>True (with why) while a kit must wait after its last refusal.</summary>
    private bool KitRetryWait(out string why)
    {
        double since = (RynthClock.Now - _kitRefusedAt).TotalMilliseconds;
        if (since < KitRetryAfterRefusalMs)
        {
            why = $"kit refused {since:0} ms ago, retrying shortly";
            return true;
        }
        why = "";
        return false;
    }

    /// <summary>True for the chat lines that end a kit use on yourself (ACE Healer.cs).</summary>
    internal static bool IsKitResultLine(string lower) =>
        lower.Contains("heal yourself")                 // "You heal yourself for ...", "You critically heal ...", "You fail to heal yourself."
        || lower.Contains("disrupted healing")          // "Your movement disrupted healing!"
        || lower.Contains("is already at full");        // full health / stamina / mana

    // ── Kit success chance (VTank's "kit chance" option) ─────────────────────
    // ACE Healer.DoSkillCheck: (Healing + kit BoostValue) x (1.5 specialized, else 1.1)
    // against missing vital x 2 x (1.0 in peace mode, else 1.1), through
    // SkillCheck.GetSkillChance's 1 - 1/(1 + e^(0.03 x (skill - difficulty))).
    private const uint PropIntBoostValue = 90;

    // BoostValue by kit name, from the live world database (2026-09-30), for kits whose
    // property the client doesn't have (not identified). Unknown kits count as 0.
    private static readonly (string Name, int Boost)[] KitBoosts =
    {
        ("Light Infused Healing Kit", 250), ("Plentiful Healing Kit", 100), ("Gifted Healing Kit", 100),
        ("Adept Healing Kit", 75), ("Handy Healing Kit", 50), ("Treated Healing Kit", 25),
        ("Peerless Healing Kit", 20), ("Excellent Healing Kit", 10), ("Plain Healing Kit", 0),
        ("Medicated Stamina Kit", 500), ("Gauntlet Stamina Kit", 225), ("Greater Stamina Kit", 200),
        ("Lesser Stamina Kit", 100), ("Eternal Stamina Kit", 100),
    };
    private DateTime _lastKitChanceLog = DateTime.MinValue;
    /// <summary>Kit Min Success % with a melee or missile weapon out, when Kit Min Success is higher.</summary>
    internal const int WeaponOutKitMinPct = 30;

    /// <summary>
    /// True when a kit may be used: <paramref name="minPct"/> (Kit Min Success % for healing kits,
    /// Stamina Kit Min Success % for stamina kits) is 0, or the kit's chance of working on
    /// <paramref name="missing"/> points is at least that. Below it the caller falls through
    /// to the spell, as VTank does.
    /// </summary>
    private bool KitChanceOk(int kitId, string kitName, uint missing, bool willBePeace, int minPct)
    {
        // A weapon out: the spell is a wand swap, the Magic stance and the cast first (4 s and more
        // from melee), so a kit gets a lower bar. Drakkon, 2026-10-14 13:50: a 51% kit was skipped
        // for Heal Self from melee and he died before the cast went out.
        if (CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile)
            minPct = Math.Min(minPct, WeaponOutKitMinPct);
        if (minPct <= 0 || _charSkills == null) return true;

        int boost = 0;
        if (_host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(unchecked((uint)kitId), PropIntBoostValue, out int b))
            boost = b;
        else
            foreach (var (n, v) in KitBoosts)
                if (kitName.Equals(n, StringComparison.OrdinalIgnoreCase)) { boost = v; break; }

        var healing = _charSkills[AcSkillType.Healing];
        double trainedMod = healing.Training == 3 ? 1.5 : 1.1;
        double combatMod = willBePeace || CurrentCombatMode == CombatMode.NonCombat ? 1.0 : 1.1;
        int skill = (int)Math.Round((Math.Max(0, healing.Buffed) + boost) * trainedMod);
        int difficulty = (int)Math.Round(missing * 2 * combatMod);
        double chance = 1.0 - 1.0 / (1.0 + Math.Exp(0.03 * (skill - difficulty)));
        int pct = (int)Math.Floor(Math.Clamp(chance, 0, 1) * 100);
        bool ok = pct >= minPct;
        if (!ok && (RynthClock.Now - _lastKitChanceLog).TotalSeconds >= 10)
        {
            _lastKitChanceLog = RynthClock.Now;
            _host.Log($"[RynthAi] Vitals: {kitName} would work {pct}% of the time on {missing} missing (skill {skill} vs {difficulty}), under {minPct}%: using the spell.");
        }
        return ok;
    }

    // Greater restores the most, Lesser the least.
    private WorldObject? BestPackStaminaKit()
    {
        if (_worldObjectCache == null) return null;
        WorldObject? best = null;
        int bestRank = int.MaxValue;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string name = wo.Name ?? "";
            if (!name.EndsWith("Stamina Kit", StringComparison.OrdinalIgnoreCase)) continue;
            if (KitUsedUp(wo.Id)) continue;   // its line said used up; the walk may not show it yet
            int rank = name.Contains("Greater", StringComparison.OrdinalIgnoreCase) ? 0
                     : name.Contains("Lesser", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            if (rank < bestRank) { best = wo; bestRank = rank; }
        }
        return best;
    }

    // Better kits heal more per use: the best one in the pack goes first.
    private static readonly string[] KitRanks = { "Peerless", "Excellent", "Gifted", "Adept", "Treated" };

    private WorldObject? BestPackHealingKit()
    {
        if (_worldObjectCache == null) return null;
        WorldObject? best = null;
        int bestRank = int.MaxValue;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: false))
        {
            string name = wo.Name ?? "";
            if (!name.EndsWith("Healing Kit", StringComparison.OrdinalIgnoreCase)) continue;
            if (KitUsedUp(wo.Id)) continue;   // its line said used up; the walk may not show it yet
            int rank = Array.FindIndex(KitRanks, k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (rank < 0) rank = name.Contains("Crude", StringComparison.OrdinalIgnoreCase) ? KitRanks.Length + 1 : KitRanks.Length;
            if (rank < bestRank) { best = wo; bestRank = rank; }
        }
        return best;
    }

    /// <summary>
    /// Returns true if any self-buff in the current buff list is missing/expired,
    /// without actually casting. Used by NeedToBuff meta condition.
    /// </summary>
    public bool NeedsAnyBuff()
    {
        if (!_settings.EnableBuffing) return false;
        // RebuffLineSec: Rebuff With, plus Rebuff When Idle's margin while the bot is idle. An early
        // top-off run ends at the heartbeat (before the arbiter) once the bot isn't idle.
        return _isForceRebuffing || AnyBuffBelowThreshold(BuildDynamicBuffList(), RebuffLineSec());
    }

    // Returns true if any castable buff in the list has < thresholdSec remaining.
    // Used both as the arbiter query (NeedsAnyBuff) and as the batch-rebuff trigger
    // inside CheckAndCastSelfBuffs — keeps the two in sync on when buffing is needed.
    // Families currently parked in _buffFailCooldownUntil are skipped so a parked
    // family can't keep retriggering the batch (which would otherwise loop:
    // batch starts → clears cooldown → cast again → silent no-show → re-park).
    private bool AnyBuffBelowThreshold(List<string> desiredBuffs, int thresholdSec)
    {
        DateTime now = RynthClock.Now;
        foreach (string buffBaseName in desiredBuffs)
        {
            AcSkillType castSkill = SkillFor(buffBaseName);
            if (!IsSkillUsable(castSkill)) continue;
            int spellId = FindBestSpellId(buffBaseName, castSkill);
            if (spellId == 0) continue;
            int family = SpellTableStub.GetById(spellId)?.Family ?? 0;
            if (family != 0
                && _buffFailCooldownUntil.TryGetValue(family, out DateTime coolUntil)
                && now < coolUntil)
                continue;
            if (!IsBuffActive(spellId, thresholdSec)) return true;
        }
        // The shield's armor spells start a batch the same way (BuffManager.Shield.cs).
        return ShieldBuffBelowThreshold(desiredBuffs, thresholdSec);
    }

    // "[FR] skip ..." lines while a rebuff runs: the selector runs every tick (~30 ms), and a
    // parked buff logged on each one - 47,000 lines in 10 minutes on 2026-10-11, so the log
    // rotated every few minutes and held nothing from before. Once per buff and reason a minute.
    private readonly Dictionary<string, DateTime> _diagSkipLoggedAt = new();
    private void DiagSkip(string buff, string line)
    {
        int cut = line.IndexOf(" — ", StringComparison.Ordinal);
        string reason = cut >= 0 ? line.Substring(cut, Math.Min(18, line.Length - cut)) : line;
        string key = buff + "|" + reason;
        DateTime now = RynthClock.Now;
        if (_diagSkipLoggedAt.TryGetValue(key, out DateTime at) && (now - at).TotalSeconds < 60) return;
        _diagSkipLoggedAt[key] = now;
        _host.Log(line);
    }

    private bool CheckAndCastSelfBuffs()
    {
        // Auto batch-rebuff: when any buff drops below the configured threshold,
        // trigger a full rebuff so ALL timers re-align in one session rather than
        // each buff trickling in one at a time as it individually expires.
        int rebuffLine = _isForceRebuffing ? 0 : RebuffLineSec();
        if (!_isForceRebuffing && AnyBuffBelowThreshold(BuildDynamicBuffList(), rebuffLine))
        {
            // Rebuff When Idle: a run the margin alone started (nothing at or under Rebuff With) is an
            // early top-off, which stops if the bot stops being idle.
            _isIdleTopOffRun = rebuffLine > _settings.RebuffSecondsRemaining
                               && !AnyBuffBelowThreshold(BuildDynamicBuffList(), _settings.RebuffSecondsRemaining);
            if (_isIdleTopOffRun)
            {
                IdleTopOffRuns++;
                _host.Log($"[Buff] Rebuff When Idle: idle, and a buff has under {rebuffLine} s left (Rebuff With {_settings.RebuffSecondsRemaining} s + {_settings.RebuffIdleMarginMinutes} min): topping off early.");
            }
            // Batch trigger — reuse FR family-tracking but do NOT clear timers.
            // ForceFullRebuff clears _ramBuffTimers, which causes an immediate
            // re-trigger: any spell that can't be resolved (spellId=0) never
            // gets a timer recorded, so the next AnyBuffBelowThreshold call sees
            // "no timer = below threshold" and loops forever.
            // Also do NOT clear _buffFailCooldownUntil here. Auto-batch must
            // respect parked families (chat hard-rejects + silent no-shows);
            // only an explicit ForceFullRebuff resets them. Without this,
            // /god kicks off auto-batch → cooldown cleared → cast → silent
            // no-show → re-park → another auto-batch → loop. Hard-reject
            // cooldowns are short-lived (120s) so they expire on their own.
            _isForceRebuffing = true;
            _isAutoBatchRebuff = true;
            ClearRunFamilies();
            _chain.PassStart();
            LogFilterSkips();
        }

        List<string> desiredBuffs = BuildDynamicBuffList();
        bool diagnose = _isForceRebuffing;

        foreach (string buffBaseName in desiredBuffs)
        {
            AcSkillType castSkill = SkillFor(buffBaseName);
            if (!IsSkillUsable(castSkill))
            {
                LastBuffSkipReason = $"skill not usable: {buffBaseName} ({castSkill})";
                if (diagnose) DiagSkip(buffBaseName, $"[FR] skip '{buffBaseName}' — skill {castSkill} not usable");
                continue;
            }

            int spellId = FindBestSpellId(buffBaseName, castSkill);
            if (spellId == 0)
            {
                LastBuffSkipReason = $"not known / unresolvable: {buffBaseName}";
                if (diagnose) DiagSkip(buffBaseName, $"[FR] skip '{buffBaseName}' — FindBestSpellId returned 0");
                continue;
            }

            if (IsBuffActive(spellId))
            {
                LastBuffSkipReason = $"already active: {buffBaseName} (id={spellId})";
                if (diagnose) DiagSkip(buffBaseName, $"[FR] skip '{buffBaseName}' (id={spellId}) — already active");
                continue;
            }

            // Skip a family the server just hard-rejected (no components, etc.)
            // so an uncastable buff isn't re-cast every cycle (→ AC AV). Auto-
            // recovers when the cooldown expires or a cast of it later confirms.
            int buffFamily = SpellTableStub.GetById(spellId)?.Family ?? 0;
            if (buffFamily != 0
                && _buffFailCooldownUntil.TryGetValue(buffFamily, out DateTime coolUntil)
                && RynthClock.Now < coolUntil)
            {
                LastBuffSkipReason = $"fail-cooldown: {buffBaseName} (fam={buffFamily}, {(coolUntil - RynthClock.Now).TotalSeconds:0}s)";
                if (diagnose) DiagSkip(buffBaseName, $"[FR] skip '{buffBaseName}' (id={spellId}) — hard-fail cooldown {(coolUntil - RynthClock.Now).TotalSeconds:0}s");
                continue;
            }

            if (!EnsureMagicMode(forBuff: true))
            {
                if (_wandSwapExhausted)
                {
                    // FIX: the bow->wand swap provably can't succeed right now (no open
                    // pack / repeated fails). Park THIS family in the existing per-family
                    // cooldown so AnyBuffBelowThreshold + this selector skip it ->
                    // NeedsAnyBuff() goes false -> the arbiter stops picking Buffing
                    // -> combat runs. Buffing stays top priority; only the impossible
                    // family is shelved, time-boxed, and auto-retried.
                    if (buffFamily != 0)
                        _buffFailCooldownUntil[buffFamily] = RynthClock.Now.AddSeconds(BuffFailCooldownSec);
                    LastBuffSkipReason = $"wand-swap exhausted: {buffBaseName} (fam={buffFamily})";
                    _host.Log($"[WieldGate] wand-swap exhausted for '{buffBaseName}' (fam={buffFamily}) — parking {BuffFailCooldownSec:0}s, fighting unbuffed");
                    continue;
                }
                LastBuffSkipReason = "magic-mode swap in progress (yielding tick)";
                return true; // normal in-progress swap — yield the tick (NeedsAnyBuff still holds Buffing)
            }

            // The mana reserve holds buffs too, melee or caster (2026-10-12, Drakkon: a full rebuff
            // spent mana spell by spell down to 6%, under Stamina to Mana's cost, and he stood there
            // until regen reached it). A buff that would leave mana under the reserve waits for
            // Stamina to Mana first; when even that can't be paid for, buffing waits for regen.
            // On the fast path (no animation gate) nothing but the buff may go out: when the reserve
            // would send Stamina to Mana instead, the fast path is dropped for this buff and the next
            // tick goes through the animation gate, where BuffWaitsForMana sends it.
            if (_fastChainTick && BuffNeedsManaFirst(spellId, out _, out _))
            {
                _fastChainTick = false;
                _chain.HoldForAnimation();
                LastBuffSkipReason = "mana reserve before " + buffBaseName + ": Stamina to Mana waits for the animation";
                return true;
            }
            if (BuffWaitsForMana(spellId, buffBaseName))
                return true;

            // An upgrade replaces this family's active lower level (read before the cast is recorded).
            var spellInfo = SpellTableStub.GetById(spellId);
            int upgradeTo = spellInfo == null ? 0 : IsItemEnchantment(spellInfo.Name) ? GetSpellLevel(spellInfo) : EffectiveTargetLevel(spellInfo);
            int upgradeFrom = spellInfo != null && buffFamily != 0 ? ActiveLevelBelow(buffFamily, upgradeTo) : 0;

            _pendingSpellId = spellId;
            _pendingShieldId = 0;   // on the player, not the shield
            if (buffFamily != 0)
            {
                _lastCastSpellIdByFamily[buffFamily] = spellId;
                _castIssuedAtByFamily[buffFamily]    = RynthClock.Now;
                _expiryAtCastByFamily[buffFamily]    = CurrentExpiryFor(buffFamily);
            }

            if (spellInfo != null)
                _host.WriteToChat($"[RynthAi] Casting: {spellInfo.Name}", 5);
            if (upgradeFrom > 0 && spellInfo != null)
            {
                BuffUpgrades++;
                _host.Log($"[Buff] upgrade: {buffBaseName} level {upgradeFrom} -> {upgradeTo} ({spellInfo.Name}){(_isForceRebuffing ? " in this buff run" : "")}");
            }
            // NOTE: timers are recorded ONLY on chat confirmation ("you cast X"),
            // never optimistically here. An optimistic item-timer at cast time
            // left a phantom 1h "active" buff whenever a cast silently failed
            // (e.g. an unknown higher tier), so the bot thought armor was
            // buffed when it wasn't and never retried the known tier.

            if (diagnose) _host.Log($"[FR] CAST '{buffBaseName}' resolvedSpellId={spellId} (pending now set)");
            // The mana each buff went out at: the reading the floor and the reserve judged.
            NoteBuffCastMana(SpellManaCost(spellId));
            _host.Log($"[Buff] mana {_vitals.CurrentMana}/{_vitals.MaxMana} ({_vitals.ManaPct}%) before {spellInfo?.Name ?? buffBaseName} (cost {SpellManaCost(spellId)}, reserve {ManaReserve()}, Get Mana At {_settings.GetManaAt}%)");
            bool fast = _fastChainTick;
            if (fast)
                _host.Log($"[BuffChain] '{spellInfo?.Name ?? buffBaseName}' goes without waiting for the animation: {_fastChainWhy}");
            int seqAtSend = UseDoneSeqNow();
            bool castOk = _host.CastSpellFor((uint)_host.GetPlayerId(), spellId, "Buff", spellInfo?.Name ?? buffBaseName);
            _lastCastAttempt = RynthClock.Now;
            if (castOk)
            {
                NoteCastIssued(spellId);
                _chain.Sent(spellId, spellInfo?.Name ?? buffBaseName, seqAtSend, ActionPacer.Started, ActionPacer.AttacksSent, MoveAudit.MovementSends, fast);
            }
            else
            {
                _chain.ClientRefused(spellId, spellInfo?.Name ?? buffBaseName, fast, s => _host.Log(s));
            }
            // An upgrade in flight (its registry check and no-show handling differ; EndPendingUpgrade
            // ends it on a failure).
            _pendingUpgradeSpellId = upgradeFrom > 0 ? spellId : 0;
            if (!castOk)
            {
                EndPendingUpgrade(spellId, "the client refused it");   // retried after SpellCastIntervalMs (_lastCastAttempt is set above)
                // Local CastSpell rejected — packet never went out, so no chat
                // will ever arrive. Clear the gate immediately so the cycle
                // doesn't hang on this spell. (No timer to undo — we no longer
                // record optimistically.)
                _pendingSpellId = 0;
                LastBuffSkipReason = $"local CastSpell rejected: {buffBaseName} (id={spellId})";
                if (diagnose) _host.Log($"[FR] cast '{buffBaseName}' returned false — pending cleared");
            }
            return true;
        }
        // Everything on the character is on: the shield's armor spells (BuffManager.Shield.cs).
        return CastShieldBuffs(desiredBuffs, diagnose);
    }

    /// <summary>Casts that replaced an active buff of the same family at a lower level (tests, status).</summary>
    internal int BuffUpgrades { get; private set; }

    internal int FindBestSpellId(string baseName, AcSkillType skill)
    {
        int id = _spellManager.GetDynamicSelfBuffId(baseName, skill);
        return id != 0 ? id : ResolveProfileFallback(baseName, skill);
    }

    // True for enchantments that live on an ITEM (armor or weapon), NOT on the
    // player. These do NOT appear in ReadPlayerEnchantments, so they must be
    // confirmed via chat, stored in _itemSpellTimers, and PRESERVED across a
    // RefreshFromLiveMemory (which rebuilds player buffs from the live registry
    // and would otherwise wipe them → perpetual recast = "buffing in circles").
    // Matches base names AND tier-7 lore / "Aura of" forms.
    internal static bool IsItemEnchantment(string name)
    {
        string[] itemSpells = {
            // Armor: Impenetrability + Brogard's, and the elemental/physical Banes
            "Impenetrability", "Brogard's Defiance", "Acid Bane", "Olthoi's Bane",
            "Blade Bane", "Swordsman's Bane", "Swordman's Bane", "Bludgeoning Bane", "Bludgeon Bane", "Tusker's Bane",
            "Flame Bane", "Inferno's Bane", "Frost Bane", "Gelidite's Bane",
            "Lightning Bane", "Astyrrian's Bane", "Piercing Bane", "Archer's Bane",
            // Weapon auras: base names cover "Aura of X Self N" / "Incantation of X Self";
            // the explicit lore names cover the tier-7 forms that drop the base word.
            "Blood Drinker", "Aura of Infected Caress",
            "Hermetic Link", "Aura of Mystic's Blessing",
            "Heart Seeker", "Aura of Elysa's Sight",
            "Spirit Drinker", "Aura of Infected Spirit Carress", "Aura of Infected Spirit Caress",
            "Swift Killer", "Aura of Atlan's Alacrity",
            "Defender", "Aura of Cragstone's Will",
        };
        foreach (string s in itemSpells)
            if (name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // Buff profiles can hold server-custom item spells the names above don't cover.
        return SpellCatalog.IsItemSchoolBuff(name);
    }

    /// <summary>
    /// Called immediately when CheckAndCastSelfBuffs decides to cast an item spell.
    /// Records the timer optimistically — removed on fizzle.
    /// </summary>
    private void RecordItemSpellCast(SpellInfo spellInfo)
    {
        int level = GetSpellLevel(spellInfo);
        double duration = GetCustomSpellDuration(level);
        var now = RynthClock.Now;
        _itemSpellTimers[spellInfo.Family] = new ItemSpellRecord
        {
            CastAt    = now,
            ExpiresAt = now.AddSeconds(duration),
            SpellName = spellInfo.Name,
            SpellLevel = level,
        };
        // Pair with [BuffDiag] armor-recast logs so we can match record vs lookup.
        _lastArmorRecastReason.Remove(spellInfo.Family); // allow next recast to re-log
        _host.Log($"[BuffDiag] RECORD '{spellInfo.Name}' (id={spellInfo.Id}, fam={spellInfo.Family}, lvl={level}, durSec={duration:F0}, expiresAt={now.AddSeconds(duration):HH:mm:ss})");
        SaveBuffTimers();

        if (EnableCastRegistryDiagnostic)
            CapturePreCastDiagnostic(spellInfo);
    }

    private void CapturePreCastDiagnostic(SpellInfo spellInfo)
    {
        try
        {
            var diag = new CastDiagnostic
            {
                CastedAt    = RynthClock.Now,
                SpellName   = spellInfo.Name,
                SpellId     = spellInfo.Id,
                SpellFamily = spellInfo.Family,
                PreSnapshots = SnapshotRegistries(),
            };
            _pendingDiagnostics.Enqueue(diag);
            _host.Log($"[BuffTest] PRE-CAST '{spellInfo.Name}' (id={spellInfo.Id}, fam={spellInfo.Family}) " +
                      $"snapshots={diag.PreSnapshots.Count} queued={_pendingDiagnostics.Count}");
        }
        catch (Exception ex)
        {
            _host.Log($"[BuffTest] Pre-cast snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private List<RegistrySnapshot> SnapshotRegistries()
    {
        var snaps = new List<RegistrySnapshot>();
        double serverNow = _host.HasGetServerTime ? _host.GetServerTime() : 0;

        // Player registry — known-safe path.
        if (_host.HasReadPlayerEnchantments)
        {
            const int Max = 256;
            var sids = new uint[Max];
            var exp  = new double[Max];
            int n = _host.ReadPlayerEnchantments(sids, exp, Max);
            snaps.Add(new RegistrySnapshot
            {
                OwnerId     = (uint)_host.GetPlayerId(),
                OwnerName   = "Player",
                IsPlayer    = true,
                SpellIds    = sids,
                ExpiryTimes = exp,
                Count       = Math.Max(0, n),
                ServerTime  = serverNow,
            });
        }

        // Equipped-item registries — opt-in via the diagnostic flag.
        // Equipped items are stable in the cache (filter avoids the freshly-arrived
        // inventory items that triggered the AV in earlier RefreshEquippedItemEnchantments runs).
        if (_worldObjectCache != null && _host.HasReadObjectEnchantments)
        {
            var equipped = new List<WorldObject>();
            foreach (var item in _worldObjectCache.GetDirectInventory())
            {
                if (item.WieldedLocation == 0) continue;
                equipped.Add(item);
            }

            const int Max = 64;
            foreach (var item in equipped)
            {
                uint oid = unchecked((uint)item.Id);
                var sids = new uint[Max];
                var exp  = new double[Max];
                int n;
                try
                {
                    n = _host.ReadObjectEnchantments(oid, sids, exp, Max);
                }
                catch
                {
                    continue;
                }
                snaps.Add(new RegistrySnapshot
                {
                    OwnerId     = oid,
                    OwnerName   = item.Name,
                    IsPlayer    = false,
                    SpellIds    = sids,
                    ExpiryTimes = exp,
                    Count       = Math.Max(0, n),
                    ServerTime  = serverNow,
                });
            }
        }

        return snaps;
    }

    private void LogPostCastDiff(CastDiagnostic diag)
    {
        try
        {
            var post = SnapshotRegistries();
            _host.Log($"[BuffTest] === POST-CAST DIFF for '{diag.SpellName}' (id={diag.SpellId}, fam={diag.SpellFamily}) ===");
            _host.WriteToChat($"[BuffTest] Post-cast diff captured for '{diag.SpellName}' — see RynthCore.log", 1);

            foreach (var pre in diag.PreSnapshots)
            {
                RegistrySnapshot? match = null;
                foreach (var p in post)
                    if (p.OwnerId == pre.OwnerId) { match = p; break; }

                if (match == null)
                {
                    _host.Log($"[BuffTest]   {pre.OwnerName} (0x{pre.OwnerId:X8}): post-snapshot missing");
                    continue;
                }

                var preIds = new HashSet<uint>();
                for (int i = 0; i < pre.Count; i++) preIds.Add(pre.SpellIds[i]);

                int newCount = 0;
                int matchedTargetSpell = -1;
                _host.Log($"[BuffTest]   {match.OwnerName} (0x{match.OwnerId:X8}): pre={pre.Count} post={match.Count} serverNow={match.ServerTime:F1}");

                for (int i = 0; i < match.Count; i++)
                {
                    uint sid = match.SpellIds[i];
                    if (preIds.Contains(sid)) continue;
                    newCount++;
                    var sp = SpellTableStub.GetById((int)sid);
                    string nm = sp?.Name ?? "?";
                    int fam = sp?.Family ?? -1;
                    double remaining = match.ExpiryTimes[i] - match.ServerTime;
                    _host.Log($"[BuffTest]     +new spell={sid} ('{nm}') fam={fam} expiry={match.ExpiryTimes[i]:F1} remaining={remaining:F1}s");
                    if (sid == (uint)diag.SpellId || fam == diag.SpellFamily) matchedTargetSpell = i;
                }

                if (newCount == 0)
                    _host.Log($"[BuffTest]     (no new entries)");
                else if (matchedTargetSpell >= 0)
                {
                    double remaining = match.ExpiryTimes[matchedTargetSpell] - match.ServerTime;
                    bool hasDuration = remaining > 0.5 && remaining < (86400 * 365);
                    _host.Log($"[BuffTest]     >>> TARGET SPELL MATCHED: remaining={remaining:F1}s hasRealDuration={hasDuration}");
                }
            }

            _host.Log("[BuffTest] === END DIFF ===");
        }
        catch (Exception ex)
        {
            _host.Log($"[BuffTest] Post-cast diff failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool IsBuffActive(int spellId, int? rebufferSecOverride = null)
    {
        var targetSpell = SpellTableStub.GetById(spellId);
        if (targetSpell == null) return false;

        // Force Rebuff: ignore live timers entirely — only spells we've already
        // cast THIS rebuff cycle count as "active". The whole point of FR is to
        // recast every spell so all timers align to the same start time.
        // The automatic batch shares FR's bookkeeping but must NOT recast
        // everything: one buff under the threshold used to refresh every buff,
        // even ones with 30+ minutes left (2026-09-27). It tops off only buffs
        // under RebuffTopOffSecondsRemaining (never less than RebuffSecondsRemaining,
        // so the buff that started the batch is always in it) and leaves the rest
        // to their own timers.
        if (_isForceRebuffing)
        {
            // Cast this run: done, unless a strictly higher level is castable now than the one this
            // run cast (the priority buffs landed and raised the skill, or a higher spell was
            // learned). Never lower: a skill that dropped leaves the run's cast alone.
            if (_forceRebuffCastFamilies.Contains(targetSpell.Family))
                return UpgradeParked(targetSpell.Family)
                       || !(CastLevelThisRun(targetSpell.Family) is int ran && ran > 0 && GetSpellLevel(targetSpell) > ran);
            if (!_isAutoBatchRebuff) return false;
            rebufferSecOverride = Math.Max(_settings.RebuffTopOffSecondsRemaining, BatchRebuffLineSec);
        }

        int targetLevel = GetSpellLevel(targetSpell);
        // Recast when remaining duration drops below this many seconds.
        // User-configurable via Advanced Settings → Buffing; overridden during
        // batch-rebuff passes (CheckAndCastSelfBuffs) by RebuffTopOffSecondsRemaining.
        int rebufferSec = Math.Max(0, rebufferSecOverride ?? _settings.RebuffSecondsRemaining);

        // Tier-upgrade rule (applies to both item enchantments and player buffs):
        // if a better-tier spell is now available than what's currently active,
        // treat the buff as inactive and recast immediately — regardless of how
        // much time is left. Silent: callers like NeedsAnyBuff poll IsBuffActive
        // every tick, so logging here would spam. The actual cast announces
        // itself via the existing "Casting: X" line in CheckAndCastSelfBuffs.

        // Item spells (armor banes, Impenetrability) tracked in their own dictionary.
        if (IsItemEnchantment(targetSpell.Name))
        {
            if (_itemSpellTimers.TryGetValue(targetSpell.Family, out ItemSpellRecord? itemTimer))
            {
                if (itemTimer.SpellLevel < targetLevel && !UpgradeParked(targetSpell.Family))
                {
                    LogArmorRecast(targetSpell, $"tier-upgrade recordedLvl={itemTimer.SpellLevel} < targetLvl={targetLevel} (recorded='{itemTimer.SpellName}')");
                    return false;
                }
                if (RynthClock.Now < itemTimer.ExpiresAt.AddSeconds(-rebufferSec))
                {
                    _lastArmorRecastReason.Remove(targetSpell.Family); // clear so next recast re-logs
                    return true;
                }
                double remainSec = (itemTimer.ExpiresAt - RynthClock.Now).TotalSeconds;
                LogArmorRecast(targetSpell, $"expiry-window remainSec={remainSec:F0} threshold={rebufferSec} (recorded='{itemTimer.SpellName}' lvl={itemTimer.SpellLevel} expiresAt={itemTimer.ExpiresAt:HH:mm:ss})");
            }
            else
            {
                LogArmorRecast(targetSpell, $"NO timer entry for family={targetSpell.Family} (targetLvl={targetLevel}, _itemSpellTimers.Count={_itemSpellTimers.Count})");
            }
            return false;
        }

        // Player buffs — use RAM timers. With CastBuffsOverItemBuffs on, a permanent
        // (item-granted) entry is not ours and never satisfies the family. The live refresh
        // already keeps permanents out of the timers then; this covers the setting being
        // switched on between refreshes.
        if (_ramBuffTimers.TryGetValue(targetSpell.Family, out RamTimerInfo? timer)
            && !(timer.IsPermanent && _settings.CastBuffsOverItemBuffs))
        {
            // Tier-upgrade flap guard: many high-tier buffs are Incantations
            // (nominal tier 8) that LAND at a lower, skill-capped tier — e.g.
            // "Incantation of Sprint Self" (target 8) lands as "Sprint Self VI"
            // (6). Comparing the landed 6 against the nominal 8 makes the
            // upgrade rule fire forever → endless recast. Cap the target at the
            // highest tier we've actually observed land for this family, so once
            // the Incantation lands at its real ceiling we stop chasing 8.
            // The cap only counts once this session has actually CAST the target tier (or
            // higher) for the family and seen it land lower. A family bootstrapped at a lower
            // tier on purpose (Creature Enchantment Mastery / Focus / Willpower cast at 7 so the
            // skill reaches 8) landed at exactly what was cast; capping it there kept those
            // three at 7 while everything after them went to 8. No attempt recorded (fresh
            // login) keeps the cap, so a relog doesn't recast every capped Incantation.
            int effectiveTarget = EffectiveTargetLevel(targetSpell);

            if (timer.SpellLevel < effectiveTarget && !UpgradeParked(targetSpell.Family))
            {
                LogPlayerRecast(targetSpell, $"tier-upgrade storedLvl={timer.SpellLevel} < effTarget={effectiveTarget} (nominal={targetLevel}, achievedCap={(_familyAchievedTier.TryGetValue(targetSpell.Family, out int a2) ? a2 : -1)}, stored='{timer.SpellName}')");
                return false;
            }
            if (RynthClock.Now < timer.Expiration.AddSeconds(-rebufferSec))
            {
                _lastArmorRecastReason.Remove(targetSpell.Family);
                return true;
            }
            // The strongest entry is running out, but another one in the family at the
            // tier we'd cast (or better) still has time: AC moves to it when the strong one
            // ends, so casting again changes nothing. Without this a buff bot's expiring 8
            // made our own fresh VI under it recast every pass until the audit parked it.
            if (CoveredByAnotherEntry(targetSpell.Family, effectiveTarget, rebufferSec))
            {
                _lastArmorRecastReason.Remove(targetSpell.Family);
                return true;
            }
            double remainSec = (timer.Expiration - RynthClock.Now).TotalSeconds;
            LogPlayerRecast(targetSpell, $"expiry-window remainSec={remainSec:F0} threshold={rebufferSec} storedLvl={timer.SpellLevel} exp={timer.Expiration:HH:mm:ss} (stored='{timer.SpellName}')");
        }
        else if (timer != null)
        {
            LogPlayerRecast(targetSpell, $"only an item-granted '{timer.SpellName}' (permanent, lvl={timer.SpellLevel}) and CastBuffsOverItemBuffs is on: cast our own");
        }
        else
        {
            LogPlayerRecast(targetSpell, $"NO ram-timer entry for family={targetSpell.Family} (targetLvl={targetLevel}, _ramBuffTimers.Count={_ramBuffTimers.Count})");
        }

        if (_isForceRebuffing) return false;

        return false;
    }

    /// <summary>The level this force/batch run CONFIRMED <paramref name="family"/> at, 0 = none confirmed.</summary>
    private int CastLevelThisRun(int family)
        => _runConfirmedLevelByFamily.TryGetValue(family, out int level) ? level : 0;

    private void ClearRunFamilies()
    {
        _forceRebuffCastFamilies.Clear();
        _runConfirmedLevelByFamily.Clear();
        _shieldRunLevels.Clear();
    }

    /// <summary>
    /// A buff cast went through (chat, enchantment event or registry): the highest level confirmed
    /// per family since load (the landed-tier cap's "tried" mark, so a failed upgrade is retried),
    /// and in a force/batch run the family is done at that level.
    /// </summary>
    private void NoteBuffConfirmed(SpellInfo spell)
    {
        int level = GetSpellLevel(spell);
        if (!_familyAttemptedTier.TryGetValue(spell.Family, out int prev) || level > prev)
            _familyAttemptedTier[spell.Family] = level;
        // A cast of the family landed: the server takes it, so no upgrade no-show streak or park stands.
        ClearUpgradeNoShows(spell.Family);
        // The pending upgrade (or better) landed.
        if (_pendingUpgradeSpellId != 0 && SpellTableStub.GetById(_pendingUpgradeSpellId) is { } up
            && up.Family == spell.Family && level >= GetSpellLevel(up))
            _pendingUpgradeSpellId = 0;
        if (!_isForceRebuffing) return;
        _forceRebuffCastFamilies.Add(spell.Family);
        if (!_runConfirmedLevelByFamily.TryGetValue(spell.Family, out int ran) || level > ran)
            _runConfirmedLevelByFamily[spell.Family] = level;
    }

    /// <summary>
    /// The pending upgrade cast <paramref name="spellId"/> did not take (fizzle, refusal, no mana,
    /// no-show): it is no longer pending, and it is cast again at the next opportunity. No try cap,
    /// and no rest after a fizzle or a client refusal; a server mana or component refusal still rests
    /// the family as it does for any buff. A SILENT no-show is the exception to "no cap": after
    /// UpgradeNoShowThreshold in a row the upgrade (only) is parked (NoteUpgradeNoShow).
    /// </summary>
    private void EndPendingUpgrade(int spellId, string why)
    {
        if (spellId == 0 || spellId != _pendingUpgradeSpellId) return;
        _pendingUpgradeSpellId = 0;
        _host.Log($"[Buff] upgrade to {SpellTableStub.GetById(spellId)?.Name ?? spellId.ToString()} did not take ({why}); cast again at the next opportunity.");
    }

    /// <summary>True while upgrades of <paramref name="family"/> are parked after repeated silent no-shows.</summary>
    private bool UpgradeParked(int family)
        => family != 0 && _upgradeParkedUntil.TryGetValue(family, out DateTime until) && RynthClock.Now < until;

    /// <summary>
    /// The pending upgrade <paramref name="spellId"/> was a silent no-show (nothing showed in the
    /// registry / no chat). Counts it; at UpgradeNoShowThreshold in a row the family's upgrades are
    /// parked for UpgradeParkCooldown (and the count starts over for the next window). Nothing else
    /// is touched: no strike, no family park, no blacklist.
    /// </summary>
    private void NoteUpgradeNoShow(int spellId)
    {
        int family = SpellTableStub.GetById(spellId)?.Family ?? 0;
        if (family == 0) return;
        int count = _upgradeNoShowCounts.TryGetValue(family, out int prev) ? prev + 1 : 1;
        if (count >= UpgradeNoShowThreshold)
        {
            _upgradeNoShowCounts.Remove(family);
            _upgradeParkedUntil[family] = RynthClock.Now + UpgradeParkCooldown;
            _host.Log($"[Buff] upgrade of family {family} ({SpellTableStub.GetById(spellId)?.Name}) never showed {count} times in a row: upgrades of it parked {UpgradeParkCooldown.TotalMinutes:0}min " +
                      "(the level it was cast over still refreshes as a normal cast; the family is not blacklisted).");
        }
        else
        {
            _upgradeNoShowCounts[family] = count;
            _host.Log($"[Buff] upgrade of family {family} did not show ({count}/{UpgradeNoShowThreshold} before it is parked).");
        }
    }

    private void ClearUpgradeNoShows(int family)
    {
        _upgradeNoShowCounts.Remove(family);
        _upgradeParkedUntil.Remove(family);
    }
    /// <summary>
    /// The level IsBuffActive holds a player buff to: the target spell's level, capped at the
    /// highest level that family has landed at, once the target level has been confirmed and
    /// saw it land lower (Incantations that land skill-capped).
    /// </summary>
    private int EffectiveTargetLevel(SpellInfo targetSpell)
    {
        int targetLevel = GetSpellLevel(targetSpell);
        bool triedLowerOnly = _familyAttemptedTier.TryGetValue(targetSpell.Family, out int attempted) && attempted < targetLevel;
        if (!triedLowerOnly && _familyAchievedTier.TryGetValue(targetSpell.Family, out int achieved) && achieved < targetLevel)
            return achieved;
        return targetLevel;
    }

    /// <summary>
    /// The level of <paramref name="family"/> that is on now (this run's confirmed cast during a
    /// force/batch run, else a live timer or an item spell with time left), when it is below
    /// <paramref name="newLevel"/>; 0 otherwise. A cast that replaces it is an upgrade.
    /// </summary>
    private int ActiveLevelBelow(int family, int newLevel)
    {
        // Upgrades parked after repeated silent no-shows: a cast of the family now is the normal
        // refresh of what is on (no upgrade line, and its no-show is judged like any other buff's).
        if (UpgradeParked(family)) return 0;
        int on = 0;
        if (_isForceRebuffing && _forceRebuffCastFamilies.Contains(family)) on = CastLevelThisRun(family);
        else if (_ramBuffTimers.TryGetValue(family, out RamTimerInfo? t)
                 && !(t.IsPermanent && _settings.CastBuffsOverItemBuffs)
                 && (t.IsPermanent || t.Expiration > RynthClock.Now))
            on = t.SpellLevel;
        else if (_itemSpellTimers.TryGetValue(family, out ItemSpellRecord? it) && it.ExpiresAt > RynthClock.Now)
            on = it.SpellLevel;
        return on > 0 && on < newLevel ? on : 0;
    }

    /// <summary>True when the last live read holds an entry in <paramref name="family"/> at
    /// <paramref name="minLevel"/> or above with more than <paramref name="rebufferSec"/> left
    /// (a permanent one counts unless CastBuffsOverItemBuffs is on: then only our timed
    /// entries do).</summary>
    private bool CoveredByAnotherEntry(int family, int minLevel, int rebufferSec)
    {
        if (!_familyLiveEntries.TryGetValue(family, out var entries)) return false;
        bool ignorePermanent = _settings.CastBuffsOverItemBuffs;
        DateTime due = RynthClock.Now.AddSeconds(rebufferSec);
        foreach (var (level, expiration, permanent) in entries)
            if (level >= minLevel && expiration > due && !(permanent && ignorePermanent))
                return true;
        return false;
    }

    // Player-buff counterpart to LogArmorRecast — explains why a character
    // self-buff (Impregnability/Aura of Deflection, masteries, etc.) is judged
    // inactive. Throttled per-family (IsBuffActive is polled every tick).
    private void LogPlayerRecast(SpellInfo targetSpell, string reason)
    {
        if (_lastArmorRecastReason.TryGetValue(targetSpell.Family, out string? prev) && prev == reason)
            return;
        _lastArmorRecastReason[targetSpell.Family] = reason;
        _host.Log($"[BuffDiag] player-recast '{targetSpell.Name}' (id={targetSpell.Id}, fam={targetSpell.Family}): {reason}");
    }

    /// <summary>
    /// Remember the highest tier a family has actually LANDED at (read from the
    /// live registry, never from the cast name — an Incantation echoes its
    /// nominal tier in chat but lands skill-capped). Capping the upgrade target
    /// at this in IsBuffActive stops the endless "tier 6 < tier 8" recast flap.
    /// </summary>
    private void RecordAchievedTier(int family, int landedLevel)
    {
        if (landedLevel <= 0) return;
        if (!_familyAchievedTier.TryGetValue(family, out int cur) || landedLevel > cur)
            _familyAchievedTier[family] = landedLevel;
    }

    private void LogArmorRecast(SpellInfo targetSpell, string reason)
    {
        // Throttle: only log when reason changes for this family — IsBuffActive
        // is polled every tick, so unconditional logging would flood RynthCore.log.
        if (_lastArmorRecastReason.TryGetValue(targetSpell.Family, out string? prev) && prev == reason)
            return;
        _lastArmorRecastReason[targetSpell.Family] = reason;
        _host.Log($"[BuffDiag] armor-recast '{targetSpell.Name}' (id={targetSpell.Id}, fam={targetSpell.Family}): {reason}");
    }

    internal static int GetSpellLevel(SpellInfo spell)
    {
        string n = spell.Name;
        // "Aura of Incantation of Blood Drinker Self" etc. are tier 8 too (5400 s); they
        // fell to the "Aura of" rule below, got a 3600 s timer and recast 30 min early.
        if (n.StartsWith("Incantation") || n.Contains("Incantation of")) return 8;
        if (n.Contains(" VIII")) return 8;  // must precede " VII" — "X VIII".Contains(" VII") is true
        if (n.Contains(" VII")) return 7;
        if (n.Contains(" VI")) return 6;
        if (n.Contains(" V")) return 5;
        if (n.Contains(" IV")) return 4;
        if (n.Contains(" III")) return 3;
        if (n.Contains(" II")) return 2;
        if (n.EndsWith(" I") || n.Contains(" I ")) return 1;

        if (n.Contains("Mastery") || n.Contains("Blessing") || n.Contains("Aura of") ||
            n.Contains("Intervention") || n.Contains("Trance") || n.Contains("Recovery") ||
            n.Contains("Robustify") || n.Contains("Persistence") || n.Contains("Robustification") ||
            n.Contains("Might of the Lugians") || n.Contains("Preservance") || n.Contains("Perseverance") ||
            n.Contains("Honed Control") || n.Contains("Hastening") || n.Contains("Inner Calm") ||
            n.Contains("Mind Blossom") || n.Contains("Infected Caress") || n.Contains("Elysa's Sight") ||
            n.Contains("Infected Spirit") || n.Contains("Atlan's Alacrity") || n.Contains("Cragstone's Will") ||
            n.Contains("Brogard's Defiance") || n.Contains("Olthoi's Bane") || n.Contains("Swordsman's Bane") ||
            n.Contains("Swordman's Bane") || n.Contains("Tusker's Bane") || n.Contains("Inferno's Bane") ||
            n.Contains("Gelidite's Bane") || n.Contains("Astyrrian's Bane") || n.Contains("Archer's Bane"))
            return 7;

        return 1;
    }

    private int _archmageAugs = -1;      // -1 = not read yet
    private long _archmageAugsReadAt;

    /// <summary>
    /// Ranks of Archmage's Endurance (AUGMENTATION_INCREASED_SPELL_DURATION, 5x,
    /// +20% enchantment duration each). Was hardcoded to 0, so every duration on an
    /// augmented character was 20-100% short and armor banes/auras recast early
    /// (2026-06-03 audit P1#3).
    ///
    /// Fails closed to 0 — the previous behaviour — whenever the property can't be
    /// read, and clamps to the game's 5-rank cap so a bad read can't inflate a
    /// duration past 2x and leave the character silently unbuffed.
    /// </summary>
    private int GetArchmageEnduranceCount()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Augs change at most once per purchase; re-read occasionally so buying one
        // mid-session takes effect without paying for a property read per call.
        if (_archmageAugs >= 0 && now - _archmageAugsReadAt < 60000) return _archmageAugs;

        if (!_host.HasGetObjectIntProperty) return _archmageAugs > 0 ? _archmageAugs : 0;

        uint playerId = _host.GetPlayerId();
        if (playerId == 0) return _archmageAugs > 0 ? _archmageAugs : 0;

        if (!_host.TryGetObjectIntProperty(
                playerId, (uint)LongValueKey.AugmentationIncreasedSpellDuration, out int augs))
            return _archmageAugs > 0 ? _archmageAugs : 0;

        augs = Math.Clamp(augs, 0, 5);
        if (augs != _archmageAugs)
            _host.Log($"[Buff] Archmage's Endurance rank {augs}/5 — enchantment durations x{1.0 + augs * 0.20:0.00}");

        _archmageAugs       = augs;
        _archmageAugsReadAt = now;
        return augs;
    }

    internal double GetCustomSpellDuration(int spellLevel)
    {
        double baseSeconds = 1800;
        if (spellLevel == 6) baseSeconds = 2700;
        else if (spellLevel == 7) baseSeconds = 3600;
        else if (spellLevel == 8) baseSeconds = 5400;

        int augs = GetArchmageEnduranceCount();
        return baseSeconds * (1.0 + (augs * 0.20));
    }

    /// <summary>
    /// Reads active enchantments directly from client memory and refreshes _ramBuffTimers.
    /// Requires both HasReadPlayerEnchantments and HasGetServerTime to be available.
    /// Returns the number of timers updated, or -1 if the API is unavailable.
    /// </summary>
    public int RefreshFromLiveMemory()
    {
        if (!_host.HasReadPlayerEnchantments || !_host.HasGetServerTime)
            return -1;

        double serverNow = _host.GetServerTime();
        if (serverNow <= 0)
            return -1; // No time sync received yet

        const int MaxEnchantments = 512;
        uint[] spellIds    = new uint[MaxEnchantments];
        double[] expiryTimes = new double[MaxEnchantments];

        int count = _host.ReadPlayerEnchantments(spellIds, expiryTimes, MaxEnchantments);
        if (count < 0)
            return -1;

        // Item/weapon spells (Blood Drinker, Heart Seeker, banes...) are tracked from their
        // cast lines, not this registry, so a death - which wipes every enchantment at once -
        // left them "active" for up to an hour and the bot fought unbuffed (reported as
        // "buffing skips missile weapons"). The player's registry dropping to zero (a death,
        // or logging in after one) is the signal: forget them so they get recast.
        if (count == 0 && _lastLivePlayerEnchantCount != 0 && (_itemSpellTimers.Count > 0 || _shieldSpellTimers.Count > 0))
        {
            _host.Log($"[BuffDiag] player enchantments dropped to 0 (was {_lastLivePlayerEnchantCount}) - clearing {_itemSpellTimers.Count} item-spell timer(s)" +
                      $"{(_shieldSpellTimers.Count > 0 ? $" and {_shieldSpellTimers.Count} on the shield" : "")} so weapon/armor buffs recast");
            _itemSpellTimers.Clear();
            _shieldSpellTimers.Clear();
        }
        _lastLivePlayerEnchantCount = count;

        // Preserve item enchantment timers (armor banes, Impenetrability, etc.)
        // that live on the item, not the player — ReadPlayerEnchantments won't
        // return them, so clearing would lose them every login.
        var preservedItemTimers = new Dictionary<int, RamTimerInfo>();
        foreach (var kvp in _ramBuffTimers)
        {
            if (kvp.Value.Expiration > RynthClock.Now && IsItemEnchantment(kvp.Value.SpellName))
                preservedItemTimers[kvp.Key] = kvp.Value;
        }

        _ramBuffTimers.Clear();
        _familyLiveEntries.Clear();
        for (int i = 0; i < count; i++)
        {
            var spellInfo = SpellTableStub.GetById((int)spellIds[i]);
            if (spellInfo == null)
            {
                _host.Log($"[BuffDiag] refresh DROP: id={spellIds[i]} unresolved by SpellTableStub (no name) — would never re-add to RAM timers");
                continue;
            }

            double remainingSeconds = expiryTimes[i] - serverNow;
            if (remainingSeconds <= 0)
            {
                _host.Log($"[BuffDiag] refresh DROP: id={spellIds[i]} '{spellInfo.Name}' fam={spellInfo.Family} remainSec={remainingSeconds:F0} (expiry={expiryTimes[i]:F0} serverNow={serverNow:F0}) — treated as expired");
                continue;
            }

            // A permanent player enchantment: the engine reports no expiry
            // (double.MaxValue). It IS active right now — record it as a
            // presence-only entry instead of dropping it (the old code
            // skipped it here, so the family never recorded → IsBuffActive
            // false → recast every refresh forever). This is rebuilt from the
            // live registry every cycle: on death or a dispel trap the enchant
            // leaves the registry, the next read won't return it, the family
            // drops, and the bot rebuffs. IsPermanent keeps it out of
            // SaveBuffTimers so a relog can't resurrect a stripped buff.
            bool isPermanent = remainingSeconds > 86400 * 365;

            int level = GetSpellLevel(spellInfo);
            var candidate = new RamTimerInfo
            {
                Expiration  = isPermanent ? PermanentSentinel : RynthClock.Now.AddSeconds(remainingSeconds),
                SpellLevel  = level,
                SpellName   = spellInfo.Name,
                IsPermanent = isPermanent,
            };

            // A family can hold MORE THAN ONE live enchantment at once. The real
            // case: a permanent item-granted "Armor Tinkering Expertise Self VI"
            // (id=707, no expiry) sitting in the same family as the tier-7
            // "Jibril's Blessing" (id=2197) we cast. AC applies the STRONGEST, so
            // that is what the timer must reflect.
            //
            // This used to assign unconditionally, so whichever entry the registry
            // happened to return LAST won. When that was the lower permanent tier,
            // the stored level (6) stayed below the target (7) forever and the
            // tier-upgrade rule in IsBuffActive fired on every poll — and because
            // the recast lands, confirms, and is then immediately clobbered again
            // by the next refresh, nothing ever broke the cycle. One family stuck
            // like this drags the whole auto-batch with it (a single buff below
            // threshold escalates to a full rebuff), which is how a ~65-spell
            // rebuff ended up running back-to-back indefinitely.
            //
            // Keep the higher tier; on a tie keep whichever lasts longer
            // (PermanentSentinel naturally sorts last, so a permanent entry wins
            // a tie against a timed one of the same tier).
            //
            // CastBuffsOverItemBuffs (default on, 2026-10-05): a permanent entry is item-granted,
            // not ours, and on a server with buff augments our own cast is stronger or longer.
            // It never takes the family timer then, so the timer tracks the highest TIMED entry
            // (ours) and the buff is cast, then recast when it runs low. Once our cast lands
            // the family reads on until it runs low, so no recast loop: the 09-27 loop came
            // from a permanent entry OWNING the timer at a tier below the target, which can't
            // happen when permanents are left out.
            bool ignoredAsItemBuff = isPermanent && _settings.CastBuffsOverItemBuffs;
            bool keepExisting =
                ignoredAsItemBuff
                || (_ramBuffTimers.TryGetValue(spellInfo.Family, out RamTimerInfo? seen)
                    && (seen.SpellLevel > level
                        || (seen.SpellLevel == level && seen.Expiration >= candidate.Expiration)));

            if (!keepExisting)
                _ramBuffTimers[spellInfo.Family] = candidate;

            if (!_familyLiveEntries.TryGetValue(spellInfo.Family, out var familyEntries))
                _familyLiveEntries[spellInfo.Family] = familyEntries = new List<(int, DateTime, bool)>();
            familyEntries.Add((level, candidate.Expiration, isPermanent));

            // Learn the real ceiling this family lands at (Incantations cap below
            // their nominal tier) so IsBuffActive stops chasing an unreachable tier.
            // Recorded for every timed entry read, including one the merge above
            // discarded — the ceiling is about what this character can land, not
            // about which entry currently wins the family slot. NOT for permanent
            // entries: those are item-granted ("Willpower Other I" on gear), not
            // something this character cast, and learning their tier as the
            // ceiling made a permanent level-1 enchant satisfy the family forever
            // (effective target 1, never expires): after a relog with the real
            // buff gone, Lucy went 17 minutes without Willpower (2026-09-27).
            if (!isPermanent)
                RecordAchievedTier(spellInfo.Family, level);

            if (isPermanent && _loggedPermanentFamilies.Add(spellInfo.Family))
                _host.Log($"[BuffDiag] permanent player enchant tracked: id={spellInfo.Id} '{spellInfo.Name}' (fam={spellInfo.Family}, lvl={level}) — presence-only, not persisted" +
                          (ignoredAsItemBuff
                              ? " — item-granted, CastBuffsOverItemBuffs is on: our own cast owns the timer"
                              : keepExisting && _ramBuffTimers.TryGetValue(spellInfo.Family, out RamTimerInfo? owner)
                                  ? $" — outranked in this family by '{owner.SpellName}' (lvl={owner.SpellLevel}), which owns the timer"
                                  : ""));
        }

        // Restore item enchantment timers that weren't covered by player enchantments
        foreach (var kvp in preservedItemTimers)
            _ramBuffTimers.TryAdd(kvp.Key, kvp.Value);

        SaveBuffTimers();
        // NOTE: do NOT set _liveBuffsRefreshed here. The login gate in OnHeartbeat
        // owns that flag and only opens it once the enchantment count STABILIZES
        // across consecutive reads. Setting it here let the first partial/empty
        // login read open the gate early → timers wiped → full rebuff every login.
        return _ramBuffTimers.Count;
    }

    /// <summary>
    /// Scans all equipped items and reads their enchantment registries.
    /// Tracks item-specific buffs (Impenetrability, Banes, etc.) separately from player buffs.
    /// </summary>
    private void RefreshEquippedItemEnchantments(double serverNow)
    {
        _itemBuffTimers.Clear();

        if (_worldObjectCache == null || !_host.HasReadObjectEnchantments)
            return;

        try
        {
            // Snapshot the inventory to avoid iterating a changing collection
            var inventorySnapshot = new List<WorldObject>();
            foreach (var item in _worldObjectCache.GetDirectInventory())
                inventorySnapshot.Add(item);

            const int MaxEnchantments = 64;
            uint[] spellIds    = new uint[MaxEnchantments];
            double[] expiryTimes = new double[MaxEnchantments];
            int equippedCount = 0;

            foreach (var item in inventorySnapshot)
            {
                int wieldedSlot = item.WieldedLocation;
                if (wieldedSlot == 0) continue; // not equipped

                equippedCount++;
                uint objectId = unchecked((uint)item.Id);
                int count;
                try
                {
                    count = _host.ReadObjectEnchantments(objectId, spellIds, expiryTimes, MaxEnchantments);
                }
                catch
                {
                    continue; // skip items whose weenie can't be read
                }
                if (count <= 0) continue;

                for (int i = 0; i < count; i++)
                {
                    var spellInfo = SpellTableStub.GetById((int)spellIds[i]);
                    if (spellInfo == null) continue;

                    double remainingSeconds = expiryTimes[i] - serverNow;
                    if (remainingSeconds <= 0) continue;
                    if (remainingSeconds > 86400 * 365) continue; // permanent

                    long key = ((long)objectId << 32) | (uint)spellInfo.Family;
                    _itemBuffTimers[key] = new ItemBuffTimerInfo
                    {
                        ObjectId = objectId,
                        ObjectName = item.Name,
                        Expiration = RynthClock.Now.AddSeconds(remainingSeconds),
                        SpellLevel = GetSpellLevel(spellInfo),
                        SpellName  = spellInfo.Name,
                    };
                }
            }

            _host.Log($"[RynthAi] Item enchant scan: {equippedCount} equipped, {_itemBuffTimers.Count} timed buffs");
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthAi] Item enchant scan failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns the number of tracked item buff timers (equipped items only).
    /// </summary>
    public int ItemBuffCount => _itemBuffTimers.Count;

    /// <summary>
    /// Checks whether a specific equipped item has a given buff family active.
    /// </summary>
    public bool HasItemBuff(uint objectId, string spellBaseName)
    {
        int family = new SpellInfo(0, spellBaseName).Family;
        long key = ((long)objectId << 32) | (uint)family;
        return _itemBuffTimers.TryGetValue(key, out var info) && info.Expiration > RynthClock.Now;
    }

    public void PrintBuffDebug()
    {
        int augs = GetArchmageEnduranceCount();
        _host.WriteToChat($"[RynthAi] Archmage endurance count: {augs}", 1);

        // Try a live refresh of player enchantments
        double serverNow = _host.HasGetServerTime ? _host.GetServerTime() : 0;
        if (serverNow > 0 && _host.HasReadPlayerEnchantments)
        {
            int refreshed = RefreshFromLiveMemory();
            _host.WriteToChat($"[RynthAi] Live read: {(refreshed >= 0 ? $"{refreshed} enchantments" : "unavailable (no qualities ptr)")}", 1);

            // TODO: Item enchantment scanning disabled — crashes when reading inventory item
            // memory via ReadObjectEnchantments. Needs investigation (AV in native InqInt or
            // GetWeenieObject for certain inventory items). See memory note for details.
        }
        else
        {
            _host.WriteToChat($"[RynthAi] ServerTime={Math.Round(serverNow)} (no live read: serverTime=0 or API missing)", 1);
        }

        if (_ramBuffTimers.Count == 0 && _itemSpellTimers.Count == 0 && _shieldSpellTimers.Count == 0)
        {
            _host.WriteToChat("[RynthAi] No buff timers active.", 1);
            return;
        }

        if (_itemSpellTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Item spells ({_itemSpellTimers.Count}) --", 1);
            PrintItemSpellTimers();
        }
        PrintShieldSpellTimers();

        if (_ramBuffTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Player buffs ({_ramBuffTimers.Count}) --", 1);
            foreach (var kvp in _ramBuffTimers)
            {
                var info = kvp.Value;
                double total = GetCustomSpellDuration(info.SpellLevel);
                TimeSpan left = info.Expiration - RynthClock.Now;
                double passed = total - left.TotalSeconds;
                _host.WriteToChat(
                    $"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): {Math.Round(passed / 60, 1)}m passed, " +
                    $"{Math.Round(left.TotalMinutes, 1)}m left. Total: {total / 60}m", 1);
            }
        }
    }

    /// <summary>
    /// Diagnostic dump of buff-tier resolution. For each desired buff, shows the
    /// skill, current buffed level, the max tier the threshold settings allow,
    /// and the spell that actually got resolved (with its tier). Lets the user
    /// see exactly why a low-tier spell is being cast — usually because the
    /// higher-tier Lore spell isn't in their spellbook yet.
    /// </summary>
    public void PrintBuffTierDebug()
    {
        List<string> desiredBuffs = BuildDynamicBuffList();
        var seen = new HashSet<AcSkillType>();

        foreach (string baseName in desiredBuffs)
        {
            AcSkillType skill = SkillFor(baseName);
            if (!IsSkillUsable(skill)) continue;

            // Skill summary line — print once per unique skill.
            if (seen.Add(skill))
            {
                int buffed = _charSkills != null ? _charSkills[skill].Buffed : -1;
                int maxTier = _spellManager.GetHighestBuffSpellTier(skill);
                _host.WriteToChat($"[RynthAi] -- {skill} (buffed={buffed}, max tier T{maxTier}) --", 1);
            }

            int spellId = FindBestSpellId(baseName, skill);
            if (spellId == 0)
            {
                _host.WriteToChat($"[RynthAi]   {baseName}: no learnable spell at any tier", 1);
                continue;
            }

            var info = SpellTableStub.GetById(spellId);
            int level = info != null ? GetSpellLevel(info) : 0;
            string activeTimer = "no timer";
            if (info != null && _ramBuffTimers.TryGetValue(info.Family, out RamTimerInfo? timer))
                activeTimer = $"timer T{timer.SpellLevel}, {Math.Round((timer.Expiration - RynthClock.Now).TotalMinutes, 1)}m left";
            else if (info != null && _itemSpellTimers.TryGetValue(info.Family, out ItemSpellRecord? itemTimer))
                activeTimer = $"item T{itemTimer.SpellLevel}, {Math.Round((itemTimer.ExpiresAt - RynthClock.Now).TotalMinutes, 1)}m left";

            _host.WriteToChat($"[RynthAi]   {baseName} → {info?.Name ?? "?"} (T{level}, {activeTimer})", 1);
        }
    }

    public void PrintItemBuffDebug()
    {
        if (_itemSpellTimers.Count == 0 && _shieldSpellTimers.Count == 0)
        {
            _host.WriteToChat("[RynthAi] No item spell timers recorded yet.", 1);
            return;
        }
        if (_itemSpellTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Item spells ({_itemSpellTimers.Count}) --", 1);
            PrintItemSpellTimers();
        }
        PrintShieldSpellTimers();
    }

    private void PrintItemSpellTimers()
    {
        foreach (var kvp in _itemSpellTimers)
        {
            var info = kvp.Value;
            TimeSpan left  = info.ExpiresAt - RynthClock.Now;
            TimeSpan spent = RynthClock.Now - info.CastAt;
            double totalMin = (info.ExpiresAt - info.CastAt).TotalMinutes;
            if (left.TotalSeconds <= 0)
                _host.WriteToChat($"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): EXPIRED", 1);
            else
                _host.WriteToChat(
                    $"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): " +
                    $"{Math.Round(spent.TotalMinutes, 1)}m elapsed, {Math.Round(left.TotalMinutes, 1)}m left / {Math.Round(totalMin, 0)}m total", 1);
        }
    }

    // What the character's weapons allow (BuildDynamicBuffList's weapon-aura filter).
    // Physical = a melee or missile weapon is listed or wielded; Caster = a wand/staff/orb is listed,
    // wielded, or (WieldUnlistedWandWhenNoneListed) in the pack; Unknown = something listed that the
    // object cache can't classify (nothing is filtered then).
    internal readonly record struct ListedWeaponKit(bool Physical, bool Caster, bool Unknown);

    // The kit is read from the object cache (a pack walk), so it is kept for KitTtlMs and
    // recomputed early when the Items list (its reference or length), the unlisted-wand setting or
    // the cache changes. A weapon swap shows within the TTL.
    private ListedWeaponKit _kit;
    private long _kitAt = long.MinValue / 2;
    private List<ItemRule>? _kitRules;
    private int _kitRuleCount;
    private bool _kitUnlisted;
    private WorldObjectCache? _kitCache;
    private const long KitTtlMs = 1000;
    /// <summary>Clock (ms) for the kit's TTL; tests replace it.</summary>
    internal Func<long> KitClockMs = () => Environment.TickCount64;
    /// <summary>Times the kit was actually computed (tests).</summary>
    internal int KitComputations { get; private set; }

    internal ListedWeaponKit ListedWeapons()
    {
        long now = KitClockMs();
        var rules = _settings.ItemRules;
        bool unlisted = _settings.WieldUnlistedWandWhenNoneListed;
        if (ReferenceEquals(rules, _kitRules) && rules.Count == _kitRuleCount && unlisted == _kitUnlisted
            && ReferenceEquals(_worldObjectCache, _kitCache) && now >= _kitAt && now - _kitAt < KitTtlMs)
            return _kit;
        _kit = ComputeListedWeapons();
        _kitAt = now;
        _kitRules = rules;
        _kitRuleCount = rules.Count;
        _kitUnlisted = unlisted;
        _kitCache = _worldObjectCache;
        KitComputations++;
        return _kit;
    }

    private ListedWeaponKit ComputeListedWeapons()
    {
        var cache = _worldObjectCache;
        if (cache == null) return new ListedWeaponKit(false, false, true);   // no cache yet: can't tell, don't filter
        bool physical = false, caster = false, unknown = false;
        List<ItemRule> rules;
        try { rules = new List<ItemRule>(_settings.ItemRules); }
        catch { return new ListedWeaponKit(false, false, true); }   // list edited mid-copy: don't filter this pass
        foreach (var r in rules)
        {
            if (WeaponList.IsShieldRule(r)) continue;   // a shield is neither
            var wo = cache[r.Id];
            if (wo == null) { unknown = true; continue; }
            if (!IsKnownWeaponClass(wo)) wo = cache.RefreshIdentity(wo);                       // blank name / Unknown class
            if (!IsKnownWeaponClass(wo)) wo = cache.ReclassifyFromItemType(r.Id) ?? wo;        // a stuck name-based guess
            if (WorldObjectCache.IsAmmo(wo)) continue;   // arrows are no weapon
            if (wo.ObjectClass == AcObjectClass.MeleeWeapon || wo.ObjectClass == AcObjectClass.MissileWeapon) physical = true;
            else if (WeaponList.IsWand(wo)) caster = true;
            else if (wo.ObjectClass == AcObjectClass.Armor) continue;   // a shield filed without the Shield action
            else unknown = true;                                        // still unclassified: don't filter on it
        }
        try
        {
            // What is in hand counts too (CombatManager.InHandWeaponId's rules: not ammo, not the off hand).
            foreach (var wo in cache.GetDirectInventory(forceRefresh: true))
            {
                if (WorldObjectCache.IsAmmo(wo)) continue;
                if ((wo.Values(LongValueKey.CurrentWieldedLocation, 0) & OffhandRules.ShieldSlot) != 0) continue;
                bool phys = wo.ObjectClass == AcObjectClass.MeleeWeapon || wo.ObjectClass == AcObjectClass.MissileWeapon;
                bool wand = !phys && WeaponList.IsWand(wo);
                if (!phys && !wand) continue;
                if (!WorldObjectCache.IsWieldedByPlayer(_host, wo)) continue;
                if (phys) physical = true; else caster = true;
            }
            // No wand listed or in hand, but buffing may wield one from the pack (WieldUnlistedWandWhenNoneListed,
            // WeaponList.FindWand's unlisted branch: a WandStaffOrb by object class): that wand counts as a caster.
            if (!caster && _settings.WieldUnlistedWandWhenNoneListed)
                foreach (var wo in cache.GetInventory())
                    if (wo.ObjectClass == AcObjectClass.WandStaffOrb) { caster = true; break; }
        }
        catch { unknown = true; }
        return new ListedWeaponKit(physical, caster, unknown);
    }

    private static bool IsKnownWeaponClass(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.MeleeWeapon || wo.ObjectClass == AcObjectClass.MissileWeapon
        || wo.ObjectClass == AcObjectClass.WandStaffOrb || wo.ObjectClass == AcObjectClass.Armor
        || WorldObjectCache.IsAmmo(wo);

    /// <summary>Why a weapon aura is left out for this kit, or "" when it stays.</summary>
    internal static string WeaponAuraSkipReason(string buff, ListedWeaponKit kit)
    {
        if (kit.Unknown) return "";
        switch (buff)
        {
            case "Blood Drinker Self":
            case "Heart Seeker Self":
            case "Swift Killer Self":
                return kit.Physical ? "" : "no melee or missile weapon listed";
            case "Spirit Drinker Self":
            case "Hermetic Link Self":
                return kit.Caster ? "" : "no wand listed";
            case "Defender Self":
                return kit.Physical || kit.Caster ? "" : "no weapon or wand listed";
            default:
                return "";
        }
    }

    // Buffs BuildDynamicBuffList left out on its last pass, with the reason. Replaced (never edited)
    // only when the content changes, so a reader on another thread (the settings face) never sees it
    // change under it, and the summary text is rebuilt only then.
    private IReadOnlyList<(string Buff, string Reason)> _filterSkips = Array.Empty<(string, string)>();
    private string _filterSummary = "";
    public IReadOnlyList<(string Buff, string Reason)> FilterSkips => _filterSkips;
    private DateTime _filterSkipLoggedAt = DateTime.MinValue;
    private string _filterSkipLoggedSummary = "";
    private const double FilterSkipLogMinSec = 30;
    // "skill untrained" is true of nearly every character (an unspecialised melee skill has no mastery to cast),
    // so it would make the chat line print at every buff run. It stays in the summary (/ra buff skipped, the
    // Buffing page) and the log, and is left out of the chat line.
    internal const string SkillUntrainedReason = "skill untrained";
    private string _filterChatSummary = "";
    private int _filterChatCount;
    private DateTime _filterChatLoggedAt = DateTime.MinValue;
    private string _filterChatLoggedSummary = "";
    private const int SummaryNamesPerReason = 5;

    private void SetFilterSkips(List<(string Buff, string Reason)> skips)
    {
        var old = _filterSkips;
        bool same = old.Count == skips.Count;
        for (int i = 0; same && i < skips.Count; i++) same = old[i] == skips[i];
        if (same) return;
        _filterSkips = skips.Count == 0 ? Array.Empty<(string, string)>() : skips;
        _filterSummary = BuildSummary(skips);
        var notable = skips.FindAll(s => s.Reason != SkillUntrainedReason);
        _filterChatCount = notable.Count;
        _filterChatSummary = notable.Count == skips.Count ? _filterSummary : BuildSummary(notable);
    }

    /// <summary>"skill untrained: A, B, C, D, E (+12 more); no melee or missile weapon listed: Blood Drinker, Heart Seeker".</summary>
    internal static string BuildSummary(IReadOnlyList<(string Buff, string Reason)> skips)
    {
        if (skips.Count == 0) return "";
        var order = new List<string>();
        var byReason = new Dictionary<string, List<string>>();
        foreach (var (buff, reason) in skips)
        {
            if (!byReason.TryGetValue(reason, out var names)) { byReason[reason] = names = new List<string>(); order.Add(reason); }
            names.Add(ShortName(buff));
        }
        var groups = new List<string>(order.Count);
        foreach (string reason in order)
        {
            var names = byReason[reason];
            string shown = string.Join(", ", names.GetRange(0, Math.Min(SummaryNamesPerReason, names.Count)));
            if (names.Count > SummaryNamesPerReason) shown += $" (+{names.Count - SummaryNamesPerReason} more)";
            groups.Add($"{reason}: {shown}");
        }
        return string.Join("; ", groups);
    }

    private static string ShortName(string buff)
    {
        foreach (string suffix in new[] { " Self", " Mastery" })
            if (buff.EndsWith(suffix, StringComparison.Ordinal)) buff = buff.Substring(0, buff.Length - suffix.Length);
        return buff;
    }

    /// <summary>The skipped buffs grouped by reason, or "".</summary>
    public string FilterSkipSummary() => _filterSummary;

    // One log line and (when something besides untrained skills is skipped) one chat line per buff run
    // (Force Rebuff, or the automatic batch starting). Same content as the last line is held back for
    // FilterSkipLogMinSec; different content prints at once. Reads the skips of the BuildDynamicBuffList
    // pass the caller just made.
    private void LogFilterSkips()
    {
        var now = RynthClock.Now;
        string summary = _filterSummary;
        if (summary.Length == 0) return;
        if (summary != _filterSkipLoggedSummary || (now - _filterSkipLoggedAt).TotalSeconds >= FilterSkipLogMinSec)
        {
            _filterSkipLoggedAt = now;
            _filterSkipLoggedSummary = summary;
            _host.Log($"[Buff] filter skips: {summary}");
        }
        string chat = _filterChatSummary;
        if (chat.Length == 0) return;
        if (chat == _filterChatLoggedSummary && (now - _filterChatLoggedAt).TotalSeconds < FilterSkipLogMinSec) return;
        _filterChatLoggedAt = now;
        _filterChatLoggedSummary = chat;
        _host.WriteToChat($"[RynthAi] Buff run skips {_filterChatCount} buff(s) this character can't use - {chat}", 1);
    }
    internal List<string> BuildDynamicBuffList()
    {
        // A chosen buff profile (Loaded Files -> Buffs) replaces the built-in list. The player
        // picked every buff in it, so the unusable-buff filter (SkipUnusableBuffs) leaves it alone
        // and there are no skips to report.
        var profile = ProfileBuffList();
        if (profile != null)
        {
            SetFilterSkips(new List<(string Buff, string Reason)>());
            return profile;
        }
        var builtIn = BuildBuiltInBuffList(out var skips);
        SetFilterSkips(skips);
        return builtIn;
    }

    /// <summary>
    /// Buffing's built-in list (no buff profile chosen): creature mastery, Focus, Willpower and
    /// Life Magic Mastery first, the other creature buffs for usable skills, life protections,
    /// weapon auras, armor banes. Pump thread: it reads the character's skills.
    /// </summary>
    internal List<string> BuildBuiltInBuffList() => BuildBuiltInBuffList(out _);

    /// <param name="skips">What SkipUnusableBuffs (and untrained skills) left out, with the reason.</param>
    private List<string> BuildBuiltInBuffList(out List<(string Buff, string Reason)> skips)
    {
        bool filter = _settings.SkipUnusableBuffs;
        skips = new List<(string Buff, string Reason)>();
        var step1_CreatureMastery = new List<string>();
        var step2_Focus           = new List<string>();
        var step3_Willpower       = new List<string>();
        var step3b_LifeMastery    = new List<string>();
        var step4_OtherCreature   = new List<string>();
        var step5_LifeAndItem     = new List<string>();
        var step6_WeaponAuras     = new List<string>();
        var step7_ArmorBanes      = new List<string>();

        foreach (string attr in BaseCreatureBuffs)
        {
            if (attr == "Focus Self") step2_Focus.Add(attr);
            else if (attr == "Willpower Self") step3_Willpower.Add(attr);
            else step4_OtherCreature.Add(attr);
        }

        foreach (var kvp in CreatureSkillBuffs)
        {
            if (!IsSkillUsable(kvp.Key))
            {
                // Always left out (no skill, no mastery to cast); the setting only decides whether it is reported.
                if (filter && !BaseCreatureBuffs.Contains(kvp.Value)) skips.Add((kvp.Value, SkillUntrainedReason));
                continue;
            }
            if (kvp.Value.Contains("Creature Enchantment Mastery"))
                step1_CreatureMastery.Add(kvp.Value);
            else if (kvp.Value == "Life Magic Mastery")
                step3b_LifeMastery.Add(kvp.Value);
            else if (!BaseCreatureBuffs.Contains(kvp.Value))
                step4_OtherCreature.Add(kvp.Value);
        }

        step5_LifeAndItem.AddRange(new List<string> {
            "Regeneration Self", "Rejuvenation Self", "Mana Renewal Self",
            "Armor Self", "Acid Protection Self", "Fire Protection Self",
            "Cold Protection Self", "Lightning Protection Self",
            "Blade Protection Self", "Piercing Protection Self",
            "Bludgeoning Protection Self"
        });

        var allAuras = new[] {
            "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self",
            "Spirit Drinker Self", "Swift Killer Self", "Defender Self"
        };
        if (filter)
        {
            var kit = ListedWeapons();
            foreach (string aura in allAuras)
            {
                string why = WeaponAuraSkipReason(aura, kit);
                if (why.Length == 0) step6_WeaponAuras.Add(aura);
                else skips.Add((aura, why));
            }
        }
        else
            step6_WeaponAuras.AddRange(allAuras);

        step7_ArmorBanes.AddRange(new List<string> {
            "Impenetrability", "Acid Bane", "Blade Bane", "Bludgeoning Bane",
            "Flame Bane", "Frost Bane", "Lightning Bane", "Piercing Bane"
        });

        var final = new List<string>();
        final.AddRange(step1_CreatureMastery);
        final.AddRange(step2_Focus);
        final.AddRange(step3_Willpower);
        final.AddRange(step3b_LifeMastery);   // the four that raise casting skill and mana come first
        final.AddRange(step4_OtherCreature);
        final.AddRange(step5_LifeAndItem);
        final.AddRange(step6_WeaponAuras);
        final.AddRange(step7_ArmorBanes);

        // Buffs the player unticked (Buffing settings: prots and banes) leave the list here, so
        // the cast loop never casts them and NeedsAnyBuff never waits on them: a skipped buff
        // can't hold the bot in Buffing.
        if (_settings.DisabledBuffs.Count > 0)
        {
            final.RemoveAll(_settings.IsBuffDisabled);
            skips.RemoveAll(s => _settings.IsBuffDisabled(s.Buff));   // the player already turned these off
        }
        return final;
    }

    /// <summary>
    /// Called when the engine's enchantment hook fires for an enchantment applied to the player
    /// (engine API v70+; older engines never call it). The engine raises it after AC applied the
    /// enchantment, so the live registry already holds it. Refreshes timers from live memory
    /// when possible; falls back to the hook-supplied duration.
    /// </summary>
    public void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        var spellInfo = SpellTableStub.GetById((int)(spellId & 0xFFFF));
        if (spellInfo == null)
            return;

        // Only clear pending if this enchantment matches what we were casting.
        // Armor/item enchantments fire on the item, not the player — clearing
        // _pendingSpellId unconditionally would lose track of pending item casts
        // and prevent the chat handler from recording their timers. A cast at the shield never
        // lands on the player: only its chat line resolves it.
        if (_pendingSpellId != 0 && !PendingIsShield)
        {
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            if (pendingSpell != null && pendingSpell.Family == spellInfo.Family)
            {
                // Same bookkeeping as the registry-confirmed path in the tick: the engine
                // dispatches enchantment events BEFORE the "You cast ..." chat line of the
                // same pump tick, and that chat handler only marks the force-rebuff family
                // while _pendingSpellId is still set. Clearing pending here without marking
                // it would make an auto-batch respin this buff forever.
                NoteBuffConfirmed(pendingSpell);
                _cast.Confirmed(_pendingSpellId, "enchantment landed");
                _pendingSpellId = 0;
                NoteCastAccepted();
                _onCastResolved?.Invoke("self-buff confirmed (enchantment event)");
            }
        }

        // The family did land — clear any silent-no-show state (counter +
        // cooldown park) so future expiries trigger casts normally again.
        // Important for the /god case: if the user later learns the spell
        // the buff system should resume casting without a manual reset.
        // Also clears the chat-driven hard-reject cooldown if the family
        // somehow lands despite a recent reject (server-side fix etc.).
        // (Both, not `||`: a short-circuit left the cooldown in place whenever
        // the no-show counter was also set.)
        _silentNoShowCounts.Remove(spellInfo.Family);
        _buffFailCooldownUntil.Remove(spellInfo.Family);

        // Prefer live memory read — gives accurate remaining time for all enchantments.
        // But only trust it if the just-added buff actually shows up there yet.
        if (RefreshFromLiveMemory() >= 0 &&
            _ramBuffTimers.TryGetValue(spellInfo.Family, out RamTimerInfo? liveTimer) &&
            liveTimer.Expiration > RynthClock.Now)
        {
            return;
        }

        RecordSpellTimer(spellInfo, durationSeconds);
    }

    /// <summary>
    /// Called when the engine's enchantment hook fires for an enchantment removed from the player
    /// (engine API v70+: the id is the spell id, layer stripped). The registry already reflects
    /// the removal, so the timers are rebuilt from it: a family that still holds another
    /// enchantment (a second layer, a permanent item-granted tier) stays active instead of
    /// being dropped and recast. Falls back to clearing the family's timer.
    /// </summary>
    public void OnEnchantmentRemoved(uint enchantmentId)
    {
        var spellInfo = SpellTableStub.GetById((int)(enchantmentId & 0xFFFF));
        if (spellInfo == null) return;

        if (RefreshFromLiveMemory() >= 0)
            return;   // (it saves the timers itself)

        if (_ramBuffTimers.Remove(spellInfo.Family))
            SaveBuffTimers();
    }

    /// <summary>
    /// Call from RynthAiPlugin.OnChatWindowText when chat arrives.
    /// Handles fizzle/fail → resets pending cast state.
    /// </summary>
    public void OnChatWindowText(string text, int chatType)
    {
        string lower = text.ToLowerInvariant();

        // A scroll read that taught a spell (ACE: "The scroll is destroyed."): read the spellbook on
        // the next buff check, so a newly learned higher level is cast (an upgrade) at once.
        if (text.Trim() == "The scroll is destroyed.") _spellManager.InvalidateKnownSpells();

        // Diagnostic: log every chat seen while waiting on a cast confirmation,
        // so we can see what AC is actually emitting and adjust our matchers.
        if (_pendingSpellId != 0)
            _host.Log($"[BuffChat] pending={_pendingSpellId} type={chatType} text='{text}'");

        // Our own incantation ('You say, "Puish Zharil"') is the server STARTING the last cast we
        // sent: its give-up and the server wait are timed from here (CastTracker). It is speech,
        // so it never reaches the result matchers below.
        if (IsOwnIncantation(text))
        {
            _cast.Incantation(UseDoneSeqNow());
            return;
        }

        // Speech and our own lines are never cast results. Every chat line reaches
        // this handler, so "Bob tells you, "did you cast banes?"" matched "ou cast ",
        // fell back to the pending spell and wrote a phantom 1-hour armor timer; a
        // quoted "fizzle" cleared the pending cast. Speech always carries ', "'
        // (says / tells you / You say, "<incantation>"); no cast result does.
        if (text.Contains(", \"", StringComparison.Ordinal)
            || lower.StartsWith("[rynthai]", StringComparison.Ordinal)
            || lower.StartsWith("rynthai]", StringComparison.Ordinal))
            return;

        // A kit on yourself finished (see KitPending). After the speech filter: "heal yourself" in a tell is not one.
        if (_kitUsedAt != DateTime.MinValue && _kitLandedAt == DateTime.MinValue && IsKitResultLine(lower))
        {
            _kitLandedAt = RynthClock.Now;
            NoteKitResultLine(text, lower);   // "... is used up." naming it: kit picks skip it (BuffManager.KitUsedUp.cs)
            ClearKitRefusedStreaks();         // a kit landed: the refusal streaks end (BuffManager.VitalWandGate.cs)
        }

        // Real failure phrases. Note: bare "component" was removed — it false-matches
        // the "consumed the following components" line, which fires for both success
        // AND fizzle, so it's NOT a reliable signal either direction. Ignore it
        // entirely; wait for the explicit "ou cast" success or "fizzle" failure.
        // "You're too busy!" is NOT a cast failure — it's AC refusing because
        // the previous cast gesture is still animating. The old code lumped it
        // into the fizzle/fail block below, which set _lastCastAttempt =
        // DateTime.MinValue (defeating the SpellCastIntervalMs throttle) AND
        // cleared _pendingSpellId, so the same cast was re-issued on the very
        // next ~90ms tick → ~11 refusals/sec → AC's AddTextToScroll re-entry
        // AV at 0x00460D1D. Handle it separately: roll back the optimistic
        // item-spell timer (the bane genuinely didn't land) and clear pending
        // so the buff loop re-evaluates — but DO NOT zero _lastCastAttempt, so
        // the interval throttle stays active. The CanCastNow gesture gate is
        // the primary bound; the throttle is the backstop. No tight loop by
        // construction → 0x00460D1D killed regardless of gate precision.
        if (lower.Contains("you're too busy") || lower.Contains("you are too busy"))
        {
            // A kit in flight with no result yet: this is its refusal (see KitRefused). Our bot
            // sends nothing else while a kit is pending (CheckVitals returns at KitPending).
            if (_kitUsedAt != DateTime.MinValue && _kitLandedAt == DateTime.MinValue
                && (RynthClock.Now - _kitUsedAt).TotalMilliseconds <= KitRefusalWindowMs)
                KitRefused("You're too busy!");
            // A refusal, never "done" (2026-10-05). When the pending cast's incantation has come,
            // the server is running it and this refused something else (a loot use, a kit): the
            // pending cast is kept and still resolves on its own result or give-up time.
            ObserveLastUseDone();
            var cur = _cast.Current;
            bool pendingRunning = _pendingSpellId != 0 && cur != null && cur.SpellId == _pendingSpellId
                                  && cur.HasIncantation && !cur.Done;
            bool urgentNudge = HeldNudgeUrgent();
            bool nudge = _cast.Refusal(UseDoneSeqNow(), urgentNudge ? CastTracker.UrgentHeldAfterIncantMs : CastTracker.HeldAfterIncantMs);
            if (pendingRunning)
            {
                _host.Log($"[BuffChat] too busy while pending={_pendingSpellId} is running on the server (incantation seen) — something else was refused; pending kept.");
            }
            else if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null && IsItemEnchantment(pendingSpell.Name) && !PendingIsShield)   // the shield's refusal leaves the armor's timer
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
                _host.Log($"[BuffChat] PARKED pending={_pendingSpellId}{(PendingIsShield ? " (on the shield)" : "")} — AC too busy (cast gesture in progress); throttle kept, backing off before re-issue.");
                if (_pendingSpellId != _pendingVitalSpellId)
                    _chain.TooBusyLine(_pendingSpellId, s => _host.Log(s));
            }
            if (!pendingRunning) _pendingSpellId = 0;

            // AC is authoritatively busy — wait, escalating, rather than retrying
            // on the 400ms tick. Cleared by NoteCastAccepted on the next success.
            _busyRefusalStreak++;
            double backoffMs = Math.Min(BusyBackoffBaseMs * Math.Pow(2, _busyRefusalStreak - 1), BusyBackoffMaxMs);
            _busyBackoffUntil = RynthClock.Now.AddMilliseconds(backoffMs);
            if (_busyRefusalStreak == 1 || _busyRefusalStreak % 5 == 0)
                _host.Log($"[BuffChat] too-busy streak={_busyRefusalStreak} — backing off {backoffMs:0}ms before the next cast attempt.");

            // A run of refusals with nothing landing is a gesture that won't finish on its own;
            // moving the character cleared it (Drakkon, 2026-10-05). Do that once per streak.
            // Not while one of our casts is open on the server (incantation, no result): a stop
            // mid-windup can cancel a healthy cast, and a held one gets the nudge below instead.
            if (_busyRefusalStreak == BusyStopAtStreak && _host.HasStopCompletely && _cast.HeldSeconds == 0)
            {
                _host.StopCompletelyBy("Buff/TooBusy");
                _host.Log($"[BuffChat] too-busy streak={_busyRefusalStreak} — the cast gesture looks stuck: StopCompletely to clear it.");
            }

            // A cast the server has held open past CastTracker.HeldAfterIncantMs while it refuses
            // the next ones: one movement nudge, once per held cast (what moving him by hand did).
            if (nudge && _settings.IsMacroRunning)
                SendCastNudge(urgentNudge);

            if (!pendingRunning) _onCastResolved?.Invoke("too busy");
            return;
        }

        // HARD rejection: the server can't cast this at all and the next
        // attempt fails identically (missing components, no target, etc.).
        // Clear pending AND park the spell's family on a cooldown so the buff
        // loop stops re-picking it every cycle. Without this an uncastable buff
        // (e.g. "Battlemage's Blessing" with no components) re-casts every tick
        // → sustained AC AddTextToScroll re-entry → AC access-violation.
        // "have all the components for this spell" is matched specifically so
        // it does NOT collide with the success line "consumed the following
        // components".
        if (lower.Contains("missing some required") ||
            lower.Contains("you do not have the") ||
            lower.Contains("you must specify") ||
            lower.Contains("have all the components for this spell"))
        {
            _cast.Result(null, "refused by the server", atStart: true);
            if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                bool noCompsVital = _pendingSpellId == _pendingVitalSpellId
                    && (lower.Contains("components") || lower.Contains("missing some required"));
                if (pendingSpell != null && noCompsVital)
                {
                    // A vital spell: pass over this level only; the next lower one goes next.
                    _noComponentsUntil[_pendingSpellId] = RynthClock.Now.AddSeconds(NoComponentsLevelSec);
                    _host.WriteToChat($"[RynthAi] No components for {pendingSpell.Name}: using a lower level for {NoComponentsLevelSec / 60:0} min.", 2);
                }
                else if (pendingSpell != null && PendingIsShield)
                {
                    // Aimed at the shield: the family rests on the shield only; the armor's is on.
                    bool noComps = lower.Contains("components") || lower.Contains("missing some required");
                    ShieldCastRefused(pendingSpell, "hard rejection", BuffFailCooldownSec);
                    _host.WriteToChat($"[RynthAi] Skipping {pendingSpell.Name} on the shield for {BuffFailCooldownSec / 60:0} min " +
                                      $"({(noComps ? "no components for it" : "the server refused it")}).", 2);
                }
                else if (pendingSpell != null)
                {
                    if (IsItemEnchantment(pendingSpell.Name))
                    {
                        _itemSpellTimers.Remove(pendingSpell.Family);
                        SaveBuffTimers();
                    }
                    _buffFailCooldownUntil[pendingSpell.Family] =
                        RynthClock.Now.AddSeconds(BuffFailCooldownSec);
                    // AC's refusal doesn't name the spell and vital casts print no
                    // "Casting:" line, so say which one. The server's text is not
                    // echoed: this line comes back through this handler, and it must
                    // not match the refusal phrases above.
                    bool noComps = lower.Contains("components") || lower.Contains("missing some required");
                    _host.WriteToChat($"[RynthAi] Skipping {pendingSpell.Name} for {BuffFailCooldownSec / 60:0} min " +
                                      $"({(noComps ? "no components for it" : "the server refused it")}).", 2);
                }
                _host.Log($"[BuffChat] CLEARED+COOLED pending={_pendingSpellId} ({BuffFailCooldownSec:0}s) — hard rejection in '{text}'");
            }
            EndPendingUpgrade(_pendingSpellId, "refused");
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("hard-reject");
            return;
        }

        // OUT OF MANA: ACE refuses with "You don't have enough Mana to cast this
        // spell." It matched nothing, so the pending cast waited out the 2.5 s
        // registry give-up and counted a silent no-show; two of those parked the
        // family for 30 min, and a batch at low mana walked the list parking every
        // buff (a vital cast parked Heal Self the same way). Not a strike: clear
        // pending, rest this family briefly (a vital then falls to its potion) and
        // pause buffing for a few seconds. Vitals still run, so mana can recover.
        // A zeroed _lastCastAttempt (the soft-fail path) would re-cast at tick rate.
        if (lower.Contains("enough mana"))
        {
            _cast.Result(null, "not enough mana", atStart: true);
            _lastManaRefusalAt = RynthClock.Now;   // a combat cast's refusal too: ManaStarved with costs unknown
            if (_pendingSpellId == 0) return;
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            // A recovery vital tries again soon for a better Mana Conversion roll (RecoveryRetry).
            if (OnRecoveryManaRefusal(pendingSpell))
            {
                _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} - not enough mana; retrying in {RecoveryRetryMs} ms, buffs {LowManaBuffPauseMs / 1000:0}s.");
                _pendingSpellId = 0;
                _lowManaPauseUntil = RynthClock.Now.AddMilliseconds(LowManaBuffPauseMs);
                _onCastResolved?.Invoke("not enough mana");
                return;
            }
            double restSec = LowManaFamilyRestSec;
            if (pendingSpell != null && PendingIsShield)
                ShieldCastRefused(pendingSpell, "not enough mana", restSec);   // rests on the shield; the armor's timer stays
            else if (pendingSpell != null)
            {
                if (IsItemEnchantment(pendingSpell.Name))
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
                // A vital rests only LowManaHealRestSec. A heal (2026-10-05): long enough for the
                // mana step after it to cast Stamina to Mana or drink, not 15 s with no Heal Self
                // after the mana was back. Stamina to Mana and Revitalize (2026-10-11): 15 s parked
                // the only thing that refills mana, and combat spent what regenerated meanwhile; now
                // mana regenerates to its cost while attacks wait (the mana reserve), then it goes.
                bool vitalSpell = _pendingSpellId == _pendingVitalSpellId;
                restSec = vitalSpell ? LowManaHealRestSec : LowManaFamilyRestSec;
                _buffFailCooldownUntil[pendingSpell.Family] = RynthClock.Now.AddSeconds(restSec);
            }
            _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} - not enough mana; resting the family {restSec:0}s, buffs {LowManaBuffPauseMs / 1000:0}s.");
            EndPendingUpgrade(_pendingSpellId, "not enough mana");
            _pendingSpellId = 0;
            _lowManaPauseUntil = RynthClock.Now.AddMilliseconds(LowManaBuffPauseMs);
            _onCastResolved?.Invoke("not enough mana");
            return;
        }

        // SOFT failure: random fizzle or transient (out of mana). Clear pending
        // and allow a prompt retry — do NOT cooldown the family.
        if (lower.Contains("fizzle") ||
            lower.Contains("your spell failed") ||
            lower.Contains("lack the mana"))
        {
            _cast.Result(null, "fizzled");
            if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null && IsItemEnchantment(pendingSpell.Name) && !PendingIsShield)   // a fizzle at the shield leaves the armor's timer
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
                _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId}{(PendingIsShield ? " (on the shield)" : "")} via soft-fail in '{text}'");
            }
            _lastCastAttempt = DateTime.MinValue;
            EndPendingUpgrade(_pendingSpellId, "fizzled");
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("soft-fail");
            return;
        }

        // "Flame Bane VI cannot be cast on Kite Shield." (ACE IsInvalidTarget: an item that takes no
        // spells). Read only for a cast at the shield: shield buffing rests; the armor carries on.
        if (PendingIsShield && lower.Contains(" cannot be cast on "))
        {
            _cast.Result(null, "refused by the server", atStart: true);
            _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} on the shield 0x{(uint)_pendingShieldId:X8} - '{text}'");
            MarkShieldUnbuffable(_pendingShieldId, text);   // never again, not just 30 min (BuffManager.Shield.cs)
            _pendingSpellId = 0;
            _pendingShieldId = 0;
            _onCastResolved?.Invoke("can't be cast on the shield");
            return;
        }

        // A script heal (a transfer such as Vitality Siphon) cast at a monster that resists it:
        // "Doorheld Surveyor resists your spell" is its only result, no "due to casting" line
        // follows. Closed here as CombatManager reads the same line for its own casts.
        if (lower.Contains("resists your spell"))
        {
            var c = _cast.Open is { Done: false } o ? o
                  : _cast.Current is { Done: false, Refused: false } cur ? cur : null;
            if (c != null && c.Script)
            {
                _cast.Result(null, "resisted");
                _host.Log($"[BuffChat] script heal '{c.Name}' resisted; pending={_pendingSpellId} cleared.");
                _pendingSpellId = 0;
                _onCastResolved?.Invoke("resisted");
                return;
            }
        }

        // "You cast Incantation of Flame Bane on Gelidite Robe, refreshing ..."
        // "You cast Strength Self VIII"
        // The leading 'Y' is stripped by AC's chat-glyph layer before our handler
        // sees it — text actually arrives as "ou cast …". IndexOf works for both.
        // A transfer to the caster names the spell after "due to casting " instead.
        const string DueToCasting = "due to casting ";
        int castIdx = lower.IndexOf("ou cast ", StringComparison.Ordinal);
        int skip = 8;   // "ou cast "
        bool viaDueToCasting = false;
        if (castIdx < 0)
        {
            // A transfer to the caster names the spell after "due to casting": "You gain 51 points
            // of health due to casting Vitality Siphon on Doorheld Surveyor" (a heal the script
            // hook casts at a monster). Unread, the heal stayed "held" and its early nudge (autorun
            // on/off) went out mid-cast: the character stepped forward (2026-10-10 log, 13 of 14
            // nudges).
            castIdx = lower.IndexOf(DueToCasting, StringComparison.Ordinal);
            skip = DueToCasting.Length;
            viaDueToCasting = castIdx >= 0;
        }
        if (castIdx < 0) return;

        // Extract spell name: everything after "ou cast " (or "due to casting ") up to " on " or ","
        string afterCast = text.Substring(castIdx + skip);
        int onIdx = afterCast.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        int commaIdx = afterCast.IndexOf(',');
        int endIdx = afterCast.Length;
        if (onIdx > 0) endIdx = onIdx;
        if (commaIdx > 0 && commaIdx < endIdx) endIdx = commaIdx;
        string spellName = afterCast.Substring(0, endIdx).Trim();

        // Which of our casts this result belongs to, by the spell name it starts with (a vital's
        // line, "You cast Revitalize Self V and restore 70 points...", has no " on " or comma).
        var tracked = _cast.Result(afterCast, "result");

        // A "due to casting" line that matches no open cast is only a result when it names the
        // pending spell (its cast closed first, on a UseDone). Otherwise it is not ours to read:
        // one transfer can print several lines (Crimson Harvest, 2026-10-10), and RynthAi's own
        // drain attacks (CombatManager, never tracked here) print one each. Read as results they
        // recorded a timer, ended a too-busy backoff and resolved a cast that was still running.
        if (viaDueToCasting && tracked == null)
        {
            string? pendingName = _pendingSpellId != 0 ? SpellTableStub.GetById(_pendingSpellId)?.Name : null;
            if (pendingName == null || !string.Equals(spellName, pendingName, StringComparison.OrdinalIgnoreCase))
                return;
        }

        // Try to find the spell by name first (authoritative — this is what was actually cast)
        int spellId = SpellDatabase.GetIdByName(spellName);
        SpellInfo? spellInfo = spellId > 0 ? SpellTableStub.GetById(spellId) : null;

        // Then the cast the line names (a late result of an earlier cast is credited to that cast,
        // not to whatever is pending now: Stamina to Mana was credited to Creature Enchantment
        // Mastery, 2026-10-05), then the pending spell.
        if (spellInfo == null && tracked != null)
            spellInfo = SpellTableStub.GetById(tracked.SpellId);
        if (spellInfo == null && _pendingSpellId != 0)
            spellInfo = SpellTableStub.GetById(_pendingSpellId);

        // An armor spell's line for the shield (its own cast, a late line, or a self-cast that
        // reached a worn shield) goes on the shield's timer, not the armor's (BuffManager.Shield.cs).
        if (spellInfo != null && !viaDueToCasting && ShieldCastLine(spellInfo, afterCast))
            return;

        // A result that names another spell than the pending one is not the pending cast's
        // result: the pending cast keeps waiting for its own (or its give-up time).
        var pendingInfo = _pendingSpellId != 0 ? SpellTableStub.GetById(_pendingSpellId) : null;
        if (pendingInfo != null && spellInfo != null && spellInfo.Family != pendingInfo.Family)
        {
            _buffFailCooldownUntil.Remove(spellInfo.Family);
            _silentNoShowCounts.Remove(spellInfo.Family);
            NoteCastAccepted();
            if (IsItemEnchantment(spellInfo.Name)) RecordItemSpellCast(spellInfo);
            else RecordSpellTimer(spellInfo);
            _host.Log($"[BuffChat] result for '{spellInfo.Name}' is not pending={_pendingSpellId} ('{pendingInfo.Name}') — recorded, pending kept.");
            return;
        }

        // A result naming a LOWER level of the pending family, or naming an earlier cast of ours
        // (the cast tracker matched it to another send), is a late line: recorded for that spell,
        // and the pending cast keeps waiting for its own result. An Incantation's line names the
        // Incantation (BuffTests "You cast Incantation of Strength Self"), so a pending one is not
        // exempt; one that never reports is judged by the registry poll at its give-up.
        if (pendingInfo != null && spellInfo != null && spellInfo.Family == pendingInfo.Family
            && (GetSpellLevel(spellInfo) < GetSpellLevel(pendingInfo)
                || (tracked != null && tracked.SpellId != _pendingSpellId)))
        {
            if (IsItemEnchantment(spellInfo.Name)) RecordItemSpellCast(spellInfo);
            else RecordSpellTimer(spellInfo);
            _host.Log($"[BuffChat] result for '{spellInfo.Name}' is a lower level than pending={_pendingSpellId} ('{pendingInfo.Name}') - a late line, recorded; pending kept.");
            return;
        }

        if (spellInfo != null)
        {
            _buffFailCooldownUntil.Remove(spellInfo.Family); // it cast — clear any stale hard-fail cooldown
            // ...and its silent-no-show strikes. Only OnEnchantmentAdded cleared them,
            // which never fires for instant vitals (Heal/Revitalize/Stamina to Mana), so
            // two slow confirmations hours apart parked Heal Self for 30 minutes.
            _silentNoShowCounts.Remove(spellInfo.Family);
            OnRecoveryCastResult(spellInfo.Family, spellInfo.Name);
            NoteCastAccepted();                              // AC accepted a cast — end any too-busy backoff
            // Chat-authoritative record: only NOW (AC confirmed "you cast X").
            // Item/armor enchants live in _itemSpellTimers (what IsBuffActive
            // checks for them); player buffs in _ramBuffTimers.
            if (IsItemEnchantment(spellInfo.Name))
                RecordItemSpellCast(spellInfo);
            else
                RecordSpellTimer(spellInfo);
        }

        if (_pendingSpellId != 0)
        {
            var ps = SpellTableStub.GetById(_pendingSpellId);
            if (ps != null) NoteBuffConfirmed(ps);
            _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} via {(viaDueToCasting ? "due-to-casting" : "ou-cast")} match name='{spellName}' resolvedId={spellId}");
        }
        _pendingSpellId = 0;
        _onCastResolved?.Invoke($"cast '{spellName}'");
    }

    /// <summary>What a heal needs from the pacing now: none, a heal line, or Emergency Heal At.</summary>
    internal HealNeed HealNeedNow()
    {
        if (_vitals.MaxHealth <= 0) return HealNeed.None;
        int hp = _vitals.HealthPct;
        int emergencyAt = _settings.EmergencyHealAt;
        if (emergencyAt > 0 && hp <= emergencyAt) return HealNeed.Emergency;
        // A one-touch heal the player asked for counts as a heal whatever the lines say (BuffManager.OneTouch.cs).
        return HealthUnderAnyLine(hp, _vitals.StaminaPct) || _oneTouchHealthScope ? HealNeed.Heal : HealNeed.None;
    }

    /// <summary>Times the vitals/buff step waited for the server's answer (tests, status).</summary>
    internal int PacerHolds { get; private set; }

    /// <summary>
    /// The pacing gate of the vitals/buff step (see HeartbeatCore): false while the server is still
    /// answering the last action (ActionPacer.MayStart, with the heal rules) or while RynthAi still
    /// has the character running (the arbiter's stop goes out first).
    /// </summary>
    private bool _pacerLetHealAhead;

    /// <summary>Each tick: the first UseDone after our last buff cast (a server refusal is counted there).</summary>
    private void ObserveBuffChain()
    {
        if (!_host.HasUseDoneSeq) return;
        bool hasLast = _host.TryGetLastUseDone(out int lastSeq, out uint lastErr);
        _chain.Observe(hasLast, lastSeq, lastErr, _host.GetUseDoneSeq(), Now, s => _host.Log(s));
    }

    /// <summary>
    /// The buff cast chain's fast path for this tick (see BuffCastChain): the setting is on, the
    /// pacing is on, the character is in Magic stance, no vital is under any of its lines (so the
    /// vitals step has nothing to send on a tick that skips the animation gate), and the last
    /// action was our own buff cast answered by UseDone(0) moments ago.
    /// </summary>
    private bool FastChainNow()
    {
        _fastChainWhy = "";
        if (!_settings.FastBuffChain || !_settings.EnableBuffing || !ActionPacer.Enabled) return false;
        if (!_host.HasUseDoneSeq || CurrentCombatMode != CombatMode.Magic) return false;
        if (!VitalsClearOfEveryLine()) return false;
        // A held stop is still to be sent: CastSpellFor would flush it inside this very cast (after the
        // movement count the chain compares), so the buff waits for the animation gate instead.
        if (MoveAudit.HasPendingStop) return false;
        bool hasLast = _host.TryGetLastUseDone(out int lastSeq, out uint lastErr);
        return _chain.Eligible(ActionPacer.Started, ActionPacer.AttacksSent, MoveAudit.MovementSends, hasLast, lastSeq, lastErr,
            _host.GetUseDoneSeq(), Now, out _fastChainWhy);
    }

    /// <summary>True when health, stamina and mana are at or over every line the vitals step acts on
    /// (combat and idle, emergency included), and no kit or stamina run is in progress.</summary>
    private bool VitalsClearOfEveryLine()
    {
        if (_vitals.MaxHealth == 0) return false;
        if (KitPending() || _restamRun) return false;
        int hp = _vitals.HealthPct, st = _vitals.StaminaPct, mp = _vitals.ManaPct;
        if (HealthUnderAnyLine(hp, st)) return false;
        if (hp < Math.Max(_settings.HealAt, _settings.TopOffHP)) return false;
        if (_settings.EmergencyHealAt > 0 && hp <= _settings.EmergencyHealAt) return false;
        if (_settings.StaminaToHealthAt > 0 && hp <= _settings.StaminaToHealthAt) return false;
        if (mp < Math.Max(_settings.GetManaAt, _settings.TopOffMana)) return false;
        if (ManaUnderLine(true, out _) || ManaUnderLine(false, out _)) return false;   // the mana reserve too
        if (st < Math.Max(_settings.RestamAt, _settings.TopOffStam)) return false;
        return true;
    }

    private bool PacerAllows(out string why, bool gestureGate = true)
    {
        why = "";
        _pacerLetHealAhead = false;
        if (!ActionPacer.Enabled) return true;
        if (MoveAudit.OwnMovementActive)
        {
            why = "the character is still moving (the stop goes out first)";
            PacerHolds++;
            return false;
        }
        HealNeed need = HealNeedNow();
        bool outstanding = ActionPacer.Current(_host) != null;
        if (ActionPacer.MayStart(_host, "Buff", need, out why, gestureGate))
        {
            // A heal let ahead of an unanswered action: the busy count still counts that action,
            // so the busy gate below must not hold the heal again.
            _pacerLetHealAhead = outstanding && need != HealNeed.None;
            return true;
        }
        PacerHolds++;
        return false;
    }

    /// <summary>Record that a cast just went out, so the next one waits for the
    /// server to report the action finished rather than a blind interval.</summary>
    private void NoteCastIssued(int spellId)
    {
        string name = SpellTableStub.GetById(spellId)?.Name ?? $"spell {spellId}";
        _cast.Sent(spellId, name, UseDoneSeqNow());
    }

    /// <summary>
    /// True while the server has not finished the last cast sent: until its own result line, a
    /// UseDone with no error after its incantation, or (after a refusal) a UseDone(0) showing
    /// the server free, bounded by its give-up time (CastResolutionTimeoutMs from the send,
    /// later with a late incantation). A refusal (0x1D) is never "finished" (2026-10-05).
    /// Inert on an engine without UseDone observation, as before. Internal for the host tests.
    /// </summary>
    internal bool IsAwaitingCastResolution()
    {
        if (!_host.HasUseDoneSeq) return false;
        bool hasLast = _host.TryGetLastUseDone(out int lastSeq, out uint lastErr);
        return _cast.AwaitingServer(CastResolutionTimeoutMs, _host.GetUseDoneSeq(), hasLast, lastSeq, lastErr);
    }

    private int UseDoneSeqNow() => _host.HasUseDoneSeq ? _host.GetUseDoneSeq() : 0;

    private void ObserveLastUseDone()
    {
        if (_host.TryGetLastUseDone(out int seq, out uint err))
            _cast.ObserveUseDone(seq, err);
    }

    /// <summary>Each tick: casts the server finished (UseDone(0) after the incantation) close,
    /// and a cast held open past CastTracker.HeldAfterIncantMs is logged once.</summary>
    private void PollCastTracker()
    {
        if ((_cast.Current == null || _cast.Current.Done) && (_cast.Open == null || _cast.Open.Done)) return;
        ObserveLastUseDone();
        _cast.Poll();
        // A held heal, or any held cast while health is under a heal line: nudge at
        // UrgentHeldAfterIncantMs on its own, without waiting for AC to refuse something
        // (2026-10-05: the held Heal Self was nudged 11.2 s after its incantation).
        if (_settings.IsMacroRunning && HeldNudgeUrgent() && _cast.TakeNudge(CastTracker.UrgentHeldAfterIncantMs))
            SendCastNudge(urgent: true);
    }

    /// <summary>The held cast is a heal, or health is under a heal line: nudge it early. Never a
    /// cast the script hook chose (CastTracker.Cast.Script): those wait HeldAfterIncantMs.</summary>
    private bool HeldNudgeUrgent()
    {
        var held = _cast.Held;
        if (held == null || held.Script) return false;
        return IsHealCast(held.SpellId)
            || (_vitals.MaxHealth > 0 && HealthUnderAnyLine(_vitals.HealthPct, _vitals.StaminaPct));
    }

    // Spell ids this session sent as health spells (Heal Self, Stamina to Health Self at any tier:
    // the high tiers have their own names, e.g. Adja's Intervention). Set by AttemptVitalCast.
    private readonly HashSet<int> _healSpellIds = new();
    private bool IsHealCast(int spellId) => _healSpellIds.Contains(spellId);

    // Kits and potions while a cast is held open (2026-10-05): ACE (Healer.cs, Food.cs) answers a
    // use while the player is busy with "You're too busy!", and every kit Drakkon used while a Heal
    // Self was held open came back 0x1D (13:42:26, 13:42:34, 13:42:47). So while a cast is held:
    // none before its nudge has gone out (the nudge is what may free it), then one try every
    // HeldItemRetryMs (the nudge does not always free it at once: 9 s at 13:42:34).
    private const double HeldItemRetryMs = 3000;
    private DateTime _lastHeldItemTryAt = DateTime.MinValue;
    /// <summary>Kit / potion attempts skipped because a cast was held open (tests, status).</summary>
    internal int HeldItemSkips { get; private set; }

    /// <summary>True (with why) when a kit or potion would only be refused: a cast is held open.
    /// Otherwise false, and an attempt now is counted against HeldItemRetryMs.</summary>
    internal bool HeldCastBlocksItemUse(out string why)   // internal for the host tests (CastSafetyTests)
    {
        why = "";
        var held = _cast.Held;
        // Only a cast past its normal result window: before that the vital is still pending
        // and the server is simply finishing it.
        if (held == null || (Now - held.IncantAt).TotalMilliseconds <= CastTracker.IncantToResultMs) return false;
        if (!held.Nudged)
        {
            HeldItemSkips++;
            why = $"'{held.Name}' is held open by the server: ACE refuses kits and potions until it ends (nudge first)";
            return true;
        }
        double since = (Now - _lastHeldItemTryAt).TotalMilliseconds;
        if (since < HeldItemRetryMs)
        {
            HeldItemSkips++;
            why = $"'{held.Name}' still held open after the nudge: next kit/potion try in {HeldItemRetryMs - since:0} ms";
            return true;
        }
        _lastHeldItemTryAt = Now;
        return false;
    }

    /// <summary>The pending cast is past its give-up time: from its incantation when one came
    /// (CastTracker.GiveUpAt), else <paramref name="fallbackMs"/> from the send.</summary>
    private bool PendingGiveUpReached(double fallbackMs) =>
        _cast.GiveUpReached(_pendingSpellId, fallbackMs)
        ?? (RynthClock.Now - _lastCastAttempt).TotalMilliseconds > fallbackMs;

    private string GiveUpWhy(double fallbackMs)
    {
        var c = _cast.Current;
        if (c == null || c.SpellId != _pendingSpellId) return $"in {fallbackMs:0} ms";
        return c.HasIncantation
            ? $"{(Now - c.IncantAt).TotalMilliseconds:0} ms after its incantation, {(Now - c.SentAt).TotalMilliseconds:0} ms after the send"
            : $"no incantation in {(Now - c.SentAt).TotalMilliseconds:0} ms";
    }

    /// <summary>Our own speech line: 'You say, "..."' (the leading Y may be stripped).</summary>
    internal static bool IsOwnIncantation(string text) =>
        text.StartsWith("You say, \"", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("ou say, \"", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One movement nudge for a cast the server holds open (CastTracker.Refusal said so, once per
    /// held cast). ACE ends a cast when the cast gesture's motion completes on the server, and a
    /// movement packet from the client is what let Drakkon's held casts finish (2026-10-05).
    /// StopCompletely from a standing character may send no movement at all, so this sends
    /// autorun on and straight off: two movement-state changes, no real step. Then casts wait
    /// NudgeHoldMs for the held cast's result.
    /// </summary>
    private void SendCastNudge(bool urgent = false)
    {
        var held = _cast.Open;
        string name = held?.Name ?? "?";
        double secs = _cast.HeldSeconds;
        FlightRecorderSignal.Dump(_host, $"held cast nudge: '{name}' {secs:0.0} s");
        if (!_host.HasSetAutoRun)
        {
            _host.Log($"[CastSafe] '{name}' held open {secs:0.0} s after its incantation, but this engine has no SetAutoRun: no nudge.");
            return;
        }
        _host.SetAutoRunBy("Buff/Nudge", true);
        _host.SetAutoRunBy("Buff/Nudge", false);
        CastNudges++;
        if (urgent) UrgentNudges++;
        DateTime hold = RynthClock.Now.AddMilliseconds(NudgeHoldMs);
        if (_busyBackoffUntil < hold) _busyBackoffUntil = hold;
        string why = urgent
            ? (held != null && IsHealCast(held.SpellId) ? "it is a heal" : $"health {_vitals.HealthPct}% is under a heal line") +
              $": nudged at {CastTracker.UrgentHeldAfterIncantMs / 1000:0.0} s, not {CastTracker.HeldAfterIncantMs / 1000:0} s"
            : "AC answers 'too busy'";
        _host.Log($"[CastSafe] '{name}' held open {secs:0.0} s after its incantation and {why}: " +
                  $"one movement nudge (autorun on, off; #{CastNudges} this session). Casts wait {NudgeHoldMs:0} ms for its result.");
    }
    /// <summary>Nudges sent early because the held cast was a heal or health was under a heal line.</summary>
    internal int UrgentNudges { get; private set; }

    /// <summary>A cast was accepted by AC, so whatever it was busy with has
    /// cleared — drop the too-busy backoff immediately rather than serving out a
    /// stale delay.</summary>
    private void NoteCastAccepted()
    {
        if (_busyRefusalStreak == 0 && _busyBackoffUntil == DateTime.MinValue) return;
        if (_busyRefusalStreak > 0)
            _host.Log($"[BuffChat] cast accepted after {_busyRefusalStreak} too-busy refusal(s) — backoff cleared.");
        _busyRefusalStreak = 0;
        _busyBackoffUntil  = DateTime.MinValue;
    }

    private void RecordSpellTimer(SpellInfo spellInfo, double durationSeconds = -1)
    {
        if (durationSeconds < 0)
            durationSeconds = GetCustomSpellDuration(GetSpellLevel(spellInfo));

        if (durationSeconds <= 0)
            return;

        int level = GetSpellLevel(spellInfo);
        _ramBuffTimers[spellInfo.Family] = new RamTimerInfo
        {
            Expiration = RynthClock.Now.AddSeconds(durationSeconds),
            SpellLevel = level,
            SpellName = spellInfo.Name,
        };
        SaveBuffTimers();
    }

    // TODO: Replace with real skill training lookup from AC memory
    private bool IsSkillUsable(AcSkillType s) => _charSkills != null ? _charSkills[s].Training >= 2 : true;

    internal static AcSkillType SkillForBuff(string name)
    {
        // Impregnability Self = Missile Defense (Creature Enchantment), NOT Item Enchantment.
        // Impenetrability / Bane / weapon auras = Item Enchantment.
        if (name.Contains("Blood Drinker") ||
            name.Contains("Hermetic Link") || name.Contains("Heart Seeker") ||
            name.Contains("Spirit Drinker") || name.Contains("Swift Killer") ||
            name.Contains("Defender") || name.Contains("Impenetrability") ||
            name.Contains("Bane"))
            return AcSkillType.ItemEnchantment;

        // Tinkering skill buffs (Armor Tinkering Expertise, Prodigal Armor Expertise) are Creature
        // Enchantment; checked before the Life Magic "Armor" match below.
        if (name.Contains("Tinkering") || name.Contains("Armor Expertise"))
            return AcSkillType.CreatureEnchantment;

        if (name.Contains("Protection") || name.Contains("Armor") ||
            name.Contains("Regeneration") || name.Contains("Rejuvenation") ||
            name.Contains("Renewal") || name == "Harlune's Blessing" ||
            name.Contains("Stamina to Mana") || name.Contains("Revitalize") ||
            name.Contains("Stamina to Health") || name == "Heal Self")
            return AcSkillType.LifeMagic;

        return AcSkillType.CreatureEnchantment;
    }

    // Stance-deadlock episode state for THIS path. ⚠ EnsureMagicMode is the
    // stance path that owns the bot whenever Buffing holds priority — and on
    // 2026-06-12 it silently retried ChangeCombatMode for 107 MINUTES (zero
    // log lines, no recovery) while the variant-2 wand/stance deadlock kept
    // the character unbuffed and standing in a spawn field. CombatManager's
    // capped recovery never ran because Buffing starves the combat tick.
    // Mirror that recovery here: visible retries, ≤2 capped re-equips per
    // episode, one-shot chat warn when wedged.
    private DateTime _buffStanceStuckSince = DateTime.MinValue;
    private long     _buffStanceStuckStartMs;   // ActionPacer.NowMs when the clock started (a later engine refusal restarts it)
    private DateTime _buffStanceLastRecoverAt = DateTime.MinValue;
    private int _buffStanceReEquips;
    private bool _buffStanceWedgeWarned;
    private const double BuffStanceStuckRecoverMs = 8000;
    private const int BuffStanceReEquipMax = 2;
    // No-wand branch: bare-handed flips sent without reaching Magic before it stops holding the bot.
    private const int NoWandFlipMaxFails = 4;

    // Stance settle (2026-10-05): the first hung cast went out 0.55 s after ChangeCombatMode(Magic)
    // and the wand use. EnsureMagicMode trusted the client's combat-mode field, which flips before
    // the server's stance animation has run. After a stance request or wand use from here, the
    // first cast waits until the client reports Magic for StanceSettleMs AND StanceMinAfterRequestMs
    // have passed since the request (the stance animation, about 1 s). A request older than
    // StanceWaitStaleMs is not waited on.
    private const double StanceSettleMs = 300;
    private const double StanceMinAfterRequestMs = 1000;
    private const double StanceWaitStaleMs = 10_000;
    private DateTime _stanceRequestedAt = DateTime.MinValue;
    private DateTime _magicSeenAt = DateTime.MinValue;
    private bool _stanceWaitLogged;
    // Emergency heals (2026-10-05): a heal under Emergency Heal At or Stamina To Health At, or under
    // Heal At with a monster in range, waits only for the client to read Magic plus StanceSettleMs:
    // no StanceMinAfterRequestMs floor. Drakkon's last Heal Self waited 4.1 s after the request.
    private bool _stanceUrgent;
    // What held the first cast after the stance was ready (logged with the settle; see StanceSettled).
    private string _stanceLateHolder = "";
    /// <summary>Times the first cast after a stance change used the emergency-heal settle (tests, status).</summary>
    internal int UrgentStanceSettles { get; private set; }

    private void NoteStanceRequest()
    {
        _stanceRequestedAt = Now;
        _magicSeenAt = DateTime.MinValue;
        _stanceWaitLogged = false;
        _stanceLateHolder = "";
    }

    /// <summary>
    /// Each heartbeat while a stance request is open: when the client first reads Magic, and a
    /// drop out of Magic restarts it. StanceSettled used to start the 300 ms clock only when it
    /// was first CALLED in Magic, which every other gate in front of it (cast interval, a busy
    /// count, a kit in flight) could put off; it also measured the "settled" time at that call.
    /// Root cause of the 4.1 s logged on 2026-10-05: the stance was ready 1 s after the request,
    /// but a healing kit went out 0.9 s after the request and the vitals step held the spell for
    /// the kit's result and grace (KitPending) until 3.1 s later; the log blamed the stance.
    /// </summary>
    private void ObserveStance()
    {
        if (_stanceRequestedAt == DateTime.MinValue) return;
        if (CurrentCombatMode == CombatMode.Magic)
        {
            if (_magicSeenAt == DateTime.MinValue) _magicSeenAt = Now;
        }
        else
            _magicSeenAt = DateTime.MinValue;   // not Magic (yet, or again): the streak restarts
    }

    /// <summary>When the stance we asked for counts as settled (MinValue: not reading Magic yet).</summary>
    private DateTime StanceReadyAt(bool urgent)
    {
        if (_magicSeenAt == DateTime.MinValue) return DateTime.MinValue;
        DateTime ready = _magicSeenAt.AddMilliseconds(StanceSettleMs);
        if (urgent) return ready;
        DateTime minAfterRequest = _stanceRequestedAt.AddMilliseconds(StanceMinAfterRequestMs);
        return minAfterRequest > ready ? minAfterRequest : ready;
    }

    /// <summary>OnHeartbeat's end: while the stance is ready but no cast has used it, remember
    /// what held the tick, for the settle line.</summary>
    private void NoteStanceLateHolder()
    {
        if (_stanceRequestedAt == DateTime.MinValue) return;
        DateTime ready = StanceReadyAt(_stanceUrgent);
        if (ready == DateTime.MinValue || Now < ready) return;
        string h = _healHold.Length > 0 ? _healHold : LastBuffSkipReason;
        if (h.Length > 0 && !h.StartsWith("Magic stance settling", StringComparison.Ordinal)) _stanceLateHolder = h;
    }

    /// <summary>False while the Magic stance we asked for is still settling (see the fields).
    /// <paramref name="urgentHeal"/>: an emergency heal, which skips the 1 s floor.</summary>
    private bool StanceSettled(bool urgentHeal = false)
    {
        if (_stanceRequestedAt == DateTime.MinValue) return true;
        DateTime now = Now;
        if ((now - _stanceRequestedAt).TotalMilliseconds > StanceWaitStaleMs)
        {
            _stanceRequestedAt = DateTime.MinValue;
            return true;
        }
        _stanceUrgent = urgentHeal;
        if (_magicSeenAt == DateTime.MinValue) _magicSeenAt = now;   // the client reads Magic (the caller checked)
        DateTime ready = StanceReadyAt(urgentHeal);
        if (now < ready)
        {
            LastBuffSkipReason = $"Magic stance settling ({(ready - now).TotalMilliseconds:0} ms left)";
            if (!_stanceWaitLogged)
            {
                _stanceWaitLogged = true;
                _host.Log($"[BuffStance] client reads Magic {(now - _stanceRequestedAt).TotalMilliseconds:0} ms after the stance request; first cast waits {(ready - now).TotalMilliseconds:0} ms more for the stance to settle" +
                          (urgentHeal ? " (emergency heal: no 1 s floor)." : "."));
            }
            return false;
        }
        double late = (now - ready).TotalMilliseconds;
        string lateText = late > 250
            ? $"; the stance was ready {(ready - _stanceRequestedAt).TotalMilliseconds:0} ms after the request and the cast then waited {late:0} ms" +
              (_stanceLateHolder.Length > 0 ? $" for: {_stanceLateHolder}" : "")
            : "";
        _host.Log($"[BuffStance] Magic stance settled: first cast {(now - _stanceRequestedAt).TotalMilliseconds:0} ms after the stance request ({(now - _magicSeenAt).TotalMilliseconds:0} ms after the client read Magic)" +
                  (urgentHeal ? ", emergency heal" : "") + lateText + ".");
        if (urgentHeal) UrgentStanceSettles++;
        _stanceRequestedAt = DateTime.MinValue;
        _magicSeenAt = DateTime.MinValue;
        _stanceLateHolder = "";
        return true;
    }

    /// <summary>
    /// A health spell that must not wait for the full stance settle: health under Emergency Heal
    /// At or Stamina To Health At, or under Heal At with a monster within MonsterRange.
    /// </summary>
    private bool HealIsUrgent(int hp, int stamPct)
    {
        string line = ActiveHealLine(hp, stamPct, out _, out bool inCombat);
        return line is "Emergency Heal At" or "Stamina To Health At" || (line == "Heal At" && inCombat);
    }

    private bool EnsureMagicMode(bool forBuff = false, bool urgentHeal = false)
    {
        // Per-call signal; only the buff path may set it (vitals/self-heals NEVER degrade).
        _wandSwapExhausted = false;
        _magicModeBlocked = false;
        if (CurrentCombatMode == CombatMode.Magic)
        {
            // Successfully reached magic mode — clear per-cycle teardown flag
            // and wield gate so the next switch out of magic restarts cleanly.
            _combatTeardownDoneForCurrentBuffCycle = false;
            _pendingWieldId = 0;
            _wieldCooldownUntil = DateTime.MinValue;
            // Reset the exponential-backoff counter so the next problem starts
            // at the fast 1s cadence again. See field comment for rationale.
            _buffStanceConsecutiveFails = 0;
            // Episode over — clear the deadlock-recovery state too.
            _buffStanceStuckSince = DateTime.MinValue;
            _buffStanceReEquips = 0;
            _buffStanceWedgeWarned = false;
            // FIX: reached Magic — clear the new swap-confirm / resilience state so a
            // later recoverable episode (e.g. after a slot frees from looting) starts clean.
            _bowDequipPendingId = 0;
            _bowDequipAttempts = 0;
            _wieldGateFailCount = 0;
            _wandWieldFails.Clear();
            _serverMagicDueUntil = DateTime.MinValue;
            if (_wandSwap.Active) FinishWandSwap(Now, done: true);   // the wand went on and Magic came: the swap is over
            _magicAfterPeaceUntil = DateTime.MinValue;
            // In Magic, but the stance we asked for may still be settling: yield the tick.
            return StanceSettled(urgentHeal);
        }

        int wandId = FindWandInItems();
        if (wandId == 0)
        {
            // No wand available — try to switch mode anyway (bare-handed magic).
            // Same exponential backoff as the wand-wielded branch below: this used to
            // send ChangeCombatMode every SpellCastIntervalMs (2.5 Hz) forever when the
            // flip didn't take, the stance spam that crashed clients (field comment at
            // _lastBuffStanceAttempt), while buffing held the bot.
            int noWandShift = Math.Min(_buffStanceConsecutiveFails, 5);
            int noWandGateMs = Math.Min(1000 << noWandShift, 30000);
            if ((Now - _lastBuffStanceAttempt).TotalMilliseconds > noWandGateMs
                && _host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "no wand: Magic mode bare-handed"))
            {
                NoteStanceRequest();
                _lastBuffStanceAttempt = Now;
                _lastCastAttempt = Now;
                if (_buffStanceConsecutiveFails < 6) _buffStanceConsecutiveFails++;
                if (_buffStanceConsecutiveFails > 1)
                    _host.Log($"[BuffStance] no wand: ChangeCombatMode(Magic) retry #{_buffStanceConsecutiveFails} (next retry in {noWandGateMs / 1000}s)");
                return false;
            }
            // Several flips haven't taken: stop holding the bot. Buff families park
            // (the caller's wand-swap-exhausted path) and vitals fall through to kits
            // and potions, so combat runs; the flip is retried on the backoff.
            if (_buffStanceConsecutiveFails >= NoWandFlipMaxFails)
            {
                _magicModeBlocked = true;
                if (forBuff) _wandSwapExhausted = true;
                if (!_buffStanceWedgeWarned)
                {
                    _buffStanceWedgeWarned = true;
                    _host.WriteToChat($"[RynthAi] Can't enter Magic mode (no wand found) after {_buffStanceConsecutiveFails} tries. Buffs and spell heals wait and retry; add a wand to Items if this character should cast.", 2);
                }
            }
            return false;
        }

        bool alreadyWielded = IsBuffWandWielded(wandId);   // shared with the vital wand gate (BuffManager.VitalWandGate.cs)

        if (!_settings.FastWeaponSwap)
        {
            // Fast Weapon Swap off: the swap as before 2026-10-22 (BuffManager.LegacySwap.cs).
            if (!alreadyWielded) return LegacyWandSwap(wandId, forBuff);
            LegacyWandWielded(wandId);
        }
        else if (_wandSwap.Active || !alreadyWielded)
        {
            // A cooldown after a swap that didn't finish: no new swap until it ends.
            if (!_wandSwap.Active && Now < _wieldCooldownUntil)
                return false;
            // The wand swap's next step (HandSwapSequence): the off hand, the main hand, then the
            // wand, each as soon as the server answers the last. True once the wand is in hand.
            _wandSwapTouchedAt = Now;
            if (!WandSwapStep(wandId, forBuff, Now))
                return false;
        }

        // Wand is wielded — clear the gate and switch stance.
        if (_pendingWieldId == wandId)
        {
            _pendingWieldId = 0;
            _wieldCooldownUntil = DateTime.MinValue;
        }

        // The wand went on in a combat mode: the server switches to Magic by itself once the stance
        // change of the last unwield is done (TryShuffleStance). Wait for that rather than queue a
        // second stance change behind it.
        if (Now < _serverMagicDueUntil)
        {
            LastBuffSkipReason = "the server is switching to Magic";
            return false;
        }

        // Deadlock episode tracking: wand wielded but mode refuses to flip.
        // The clock starts when a stance request has actually gone out (below), never on refusals: the
        // engine turns stance changes away around a teleport or a death, and a clock counting through
        // that would end in a re-equip UseObject on the wielded wand (a MOVE) and a burnt re-equip.
        // A refusal newer than the clock's start means the request that started it was dropped (a teleport right after it went out):
        // the clock restarts, so the refusals that follow are not counted as a wedged stance.
        if (_buffStanceStuckSince != DateTime.MinValue && ActionPacer.LastEngineRefusalMs > _buffStanceStuckStartMs)
        {
            _buffStanceStuckSince = Now;
            _buffStanceStuckStartMs = ActionPacer.NowMs();
        }
        // No Magic request for a minute (buffing gave up or was taken over): that episode is over, this is a new one.
        if (_buffStanceStuckSince != DateTime.MinValue && (Now - _lastBuffStanceAttempt).TotalMilliseconds > StanceEpisodeGapMs)
            EndStanceEpisode();
        double stuckMs = _buffStanceStuckSince == DateTime.MinValue ? 0 : (Now - _buffStanceStuckSince).TotalMilliseconds;

        // Capped re-equip recovery (mirrors CombatManager): handles the rare
        // stale "wielded reads true" by resyncing the wield once or twice.
        // HARD-CAPPED — UseObject on a genuinely wielded wand is a MOVE to AC,
        // and unbounded re-equip jams the item-action queue permanently.
        if (_buffStanceReEquips < BuffStanceReEquipMax
            && stuckMs > BuffStanceStuckRecoverMs
            && (Now - _buffStanceLastRecoverAt).TotalMilliseconds > BuffStanceStuckRecoverMs
            && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("buff-stance-recovery")))
        {
            _buffStanceLastRecoverAt = Now;
            _buffStanceReEquips++;
            _host.Log($"[BuffStance] STUCK {stuckMs:0}ms (wielded, mode={CurrentCombatMode}≠Magic) — re-equip attempt {_buffStanceReEquips}/{BuffStanceReEquipMax} 0x{(uint)wandId:X8}");
            _host.UseFor((uint)wandId, "Buff", "buffing: stance stuck, wield the wand again");
            NoteStanceRequest();
            _lastCastAttempt = Now;
            return false;
        }
        // Past the cap and clearly wedged: warn ONCE so a multi-hour silent
        // coma can never happen again, then keep mode-change-only retries.
        if (_buffStanceReEquips >= BuffStanceReEquipMax && !_buffStanceWedgeWarned
            && stuckMs > BuffStanceStuckRecoverMs * 3)
        {
            _buffStanceWedgeWarned = true;
            _host.WriteToChat($"[RynthAi] Buff stance wedged: wand wielded but AC won't enter Magic mode after {stuckMs / 1000:0}s. Buffs cannot cast. Try /ra clearbusy, or relog if it persists.", 2);
        }

        // Exponential backoff: 1s → 2s → 4s → 8s → 16s → 30s cap. Counter
        // increments only when this branch fires AND the previous flip didn't
        // stick (we got back here with mode != Magic). Reset to 0 at the top
        // of EnsureMagicMode when mode == Magic. See field comment.
        int shift = _buffStanceConsecutiveFails;
        if (shift > 5) shift = 5;
        int gateMs = 1000 << shift;        // 1s, 2s, 4s, 8s, 16s, 32s
        if (gateMs > 30000) gateMs = 30000; // cap at 30s
        // Right after a swap through peace, Magic goes as soon as the wand is in hand: the server holds a
        // Magic change until its peace animation ends by itself (NextUseTime), and on that route no
        // other stance change can be queued. Only past our own peace request, never past anything else.
        bool magicAfterPeace = Now < _magicAfterPeaceUntil && OnlyOurPeaceRequestPending();
        if ((Now - _lastBuffStanceAttempt).TotalMilliseconds > gateMs
            && _host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand wielded: Magic mode", force: magicAfterPeace))
        {
            if (_buffStanceStuckSince == DateTime.MinValue) { _buffStanceStuckSince = Now; _buffStanceStuckStartMs = ActionPacer.NowMs(); }
            NoteStanceRequest();
            _lastBuffStanceAttempt = Now;
            _lastCastAttempt = Now;
            if (_buffStanceConsecutiveFails < 6) _buffStanceConsecutiveFails++;
            // Visible retry line (throttled by the backoff itself): the
            // 2026-06-12 coma produced ZERO log output from this path.
            _host.Log($"[BuffStance] ChangeCombatMode(Magic) retry #{_buffStanceConsecutiveFails} (stuck {stuckMs / 1000:0}s, next retry in {gateMs / 1000:0}s)");
        }
        return false; // Yield — let stance animation finish
    }

    /// <summary>Told the id of an off-hand item (a shield) buffing put into a pack for its wand,
    /// so combat puts it back on (CombatManager.NoteOffhandStowed).</summary>
    public Action<int>? OffhandStowed { get; set; }
    private bool _offhandNoneNoted;

    // Live (forceRefresh) wielded check — never trusts a stale cache snapshot.
    private bool IsWieldedLive(int id)
    {
        if (id == 0 || _worldObjectCache == null) return false;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
            if (wo.Id == id)
                return WorldObjectCache.IsWieldedByPlayer(_host, wo);
        if (_host.HasGetObjectWielderInfo)
        {
            uint pid = _host.GetPlayerId();
            if (EquippedState.IsWielded(_host, id) is bool tracked) return tracked;
            if (pid != 0 && _host.TryGetObjectWielderInfo((uint)id, out uint wielder, out _))
                return wielder == pid && _worldObjectCache?.InPackWalk(id) != true;
        }
        return false;
    }

    // FindOpenPackForDequip DELETED (P2a) — replaced by the shared
    // WorldObjectCache.FindPackFor(host, cache, includeMainPack:true, requireFree:1).
    // includeMainPack:true preserves the prior main-pack (cap-102) fallback so the
    // just-shipped wand-swap dequip behaviour is unchanged; requireFree:1 because a
    // single genuinely-free slot is enough for the bow (anti-churn is for the auto-
    // loops, not the user/buff-driven swap). Call sites: EnsureMagicMode + SignalSwapFailure.

    // Bounded degrade. Re-arms the bow for combat and, for the BUFF path only, raises the
    // exhaustion signal so the buff selector parks the family and the bot fights unbuffed.
    // Vitals (forBuff=false) never park a family; they see _magicModeBlocked, fall through to
    // kits/potions, and retry the swap after VitalSwapBlockedRetrySec.
    private bool SignalSwapFailure(bool forBuff, int bowId, int wandId)
    {
        // If the wand ended up wielded but mode never flipped, dequip it so the bow can
        // re-wield (stock ACE won't auto-dequip the wand for a MissileWeapon slot). Only
        // one of these two branches fires per call — no same-tick wield/unwield race.
        if (wandId != 0 && IsWieldedLive(wandId))
        {
            int openPack = WorldObjectCache.FindPackFor(_host, _worldObjectCache, includeMainPack: true, requireFree: 1);
            if (openPack != 0) _host.MoveItemInternal((uint)wandId, (uint)openPack, 0, 1);
        }
        else if (bowId != 0 && !IsWieldedLive(bowId))
        {
            _host.UseFor((uint)bowId, "Buff", "buffing done: wield the bow again"); // re-wield the bow for missile combat
        }
        _pendingWieldId = 0;
        _bowDequipPendingId = 0;
        _bowDequipAttempts = 0;
        _wieldGateFailCount = 0;
        _magicModeBlocked = true;
        if (forBuff) _wandSwapExhausted = true;
        return false;
    }

    // ── The wand swap's steps (HandSwapSequence) ────────────────────────────

    /// <summary>
    /// The wand swap's next step. True once the wand is in hand (the caller then sees to the Magic
    /// stance); false while a step is on its way, or when the swap stopped (_magicModeBlocked then
    /// lets a vital's kit or potion go, and _wandSwapExhausted parks a buff).
    /// </summary>
    private bool WandSwapStep(int wandId, bool forBuff, DateTime now)
    {
        if (_wandSwap.Active && _wandSwapWandId != wandId)
            FinishWandSwap(now, done: false, why: "another wand was chosen");
        if (!_wandSwap.Active)
        {
            // Shared weapon-swap gate: one claim for the whole swap, held until it ends, so combat
            // can't put a weapon back between its steps (two item actions in flight collide).
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("buff-wand"))
                return false;
            var start = ReadHands();
            int off = HandAt(start, HandSwapSequence.ShieldSlot);
            // With the global Offhand setting None the off hand is never touched, and the server won't
            // wield a caster beside it: no swap, said once (the kit or potion gets its turn).
            if (off != 0 && OffhandRules.Parse(_settings.OffhandDefault, OffhandMode.Auto) == OffhandMode.None)
            {
                if (!_offhandNoneNoted)
                {
                    _offhandNoneNoted = true;
                    _host.Log($"[WieldGate] 0x{(uint)off:X8} '{ItemName(off)}' is in the off hand and Offhand is None — leaving it; the server won't wield the wand beside it");
                }
                return SignalSwapFailure(forBuff, 0, wandId);
            }
            // Tear down any in-flight physical combat BEFORE the first step: an item action while
            // m_bAttacking is still set wedges the client's item-action gate.
            if (!_combatTeardownDoneForCurrentBuffCycle &&
                (CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile))
            {
                if (_host.HasCancelAttack)   _host.CancelAttack();
                if (_host.HasStopCompletely) _host.StopCompletelyBy("Buff/WandSwap");
                _combatTeardownDoneForCurrentBuffCycle = true;
                _host.Log($"[BuffPre] CancelAttack+StopCompletely before wand equip (mode was {CurrentCombatMode})");
            }
            // From a combat mode, peace first (HandSwapSequence: one stance leg, then every move is
            // answered at once, and no stance change is ever queued behind another).
            int mode0 = CurrentCombatMode;
            _wandSwap.Start("wand", now, new[] { (wandId, (int)HeldSlot) },
                peaceFirst: mode0 == CombatMode.Melee || mode0 == CombatMode.Missile);
            _weaponSwapGate?.Hold("buff-wand", (long)_wandSwap.GiveUpMs + 1000);
            _wandSwapOpen = true;
            _wandSwapWandId = wandId;
            _wandSwapForBuff = forBuff;
            _wandSwapMainAtStart = MainHandIn(start, wandId);
            _wandSwapLastMoved = 0;
            _host.Log($"[WieldGate] switching to 0x{(uint)wandId:X8} '{ItemName(wandId)}' ({DescribeHands(start)}, mode {CurrentCombatMode})");
        }

        var hands = ReadHands();
        var step = _wandSwap.Next(hands, CurrentCombatMode, now);
        switch (step.Act)
        {
            case HandSwapSequence.Act.Wait:
                return false;

            case HandSwapSequence.Act.Stance:
            {
                // The burst (test toggle /ra swaptest burst on): peace, both hands and the wand in one frame.
                int burst = SwapBurst ? TryWandBurst(hands, wandId, now) : 0;
                if (burst != 0) return false;
                // Refused while the server owes an answer or the character animates (ActionPacer): next tick.
                if (_host.ChangeCombatModeFor(step.Slot, "Buff", "wand swap: peace first, then the hands"))
                {
                    _wandSwap.Sent(step, now);
                    _host.Log($"[WieldGate] peace first {(now - _wandSwap.StartedAt).TotalMilliseconds:0} ms into the switch (the moves are then answered at once)");
                }
                return false;
            }

            case HandSwapSequence.Act.MoveToPack:
            {
                int pack = WorldObjectCache.FindPackFor(_host, _worldObjectCache, includeMainPack: true, requireFree: 1);
                if (pack == 0)
                {
                    WorldObjectCache.WarnPackFull(_host, "can't put the weapon away to switch to the wand, so buffs and spell heals can't cast");
                    return WandSwapFailed(_wandSwap.Abandon("no pack with room"), forBuff, wandId, now);
                }
                bool offhand = hands.TryGetValue(step.Id, out int slot) && (slot & HandSwapSequence.ShieldSlot) != 0;
                if (offhand) OffhandPutAway(step.Id);   // combat puts it back after (and it is buffed in the pack)
                _host.MoveItemInternal((uint)step.Id, (uint)pack, 0, 1);   // amount>=1 (engine rejects 0)
                _wandSwap.Sent(step, now);
                _wandSwapLastMoved = step.Id;
                _host.Log($"[WieldGate] 0x{(uint)step.Id:X8} '{ItemName(step.Id)}' ({(offhand ? "off hand" : "main hand")}) -> pack 0x{(uint)pack:X8} " +
                          $"{(now - _wandSwap.StartedAt).TotalMilliseconds:0} ms into the switch");
                return false;
            }

            case HandSwapSequence.Act.Wield:
            {
                // GetAndWieldItem into the Held slot (the paperdoll drag). A use only wields a plain
                // caster: one with a spell of its own (Electric Baton, Acid Staff) takes the use as
                // "cast it" (Drakkon, 2026-10-18 14:00).
                if (_host.HasWieldItem) _host.WieldItem((uint)step.Id, (uint)step.Slot);
                else _host.UseFor((uint)step.Id, "Buff", "buffing: wield the wand");
                _wandSwap.Sent(step, now);
                _wandSwapLastMoved = step.Id;
                NoteStanceRequest();
                _pendingWieldId = step.Id;
                _pendingWieldAt = now;
                // No _lastCastAttempt here: the cast interval would hold the Magic request 400 ms (seen on
                // Freya, 10-22 22:36); the first cast waits for the stance to settle anyway.
                int mode = CurrentCombatMode;
                _serverMagicDueUntil = mode == CombatMode.Melee || mode == CombatMode.Missile
                    ? now.AddMilliseconds(ServerMagicWaitMs) : DateTime.MinValue;
                _host.Log($"[WieldGate] wielding 0x{(uint)step.Id:X8} '{ItemName(step.Id)}' into the Held slot " +
                          $"{(now - _wandSwap.StartedAt).TotalMilliseconds:0} ms into the switch");
                return false;
            }

            case HandSwapSequence.Act.Done:
                FinishWandSwap(now, done: true);
                MagicAfterPeace(now);
                return true;

            default:
                return WandSwapFailed(step, forBuff, wandId, now);
        }
    }

    /// <summary>The swap stopped short: bounded retries, then the degrade (kits and potions for vitals, parked buffs).</summary>
    private bool WandSwapFailed(HandSwapSequence.Step step, bool forBuff, int wandId, DateTime now)
    {
        int mainAtStart = _wandSwapMainAtStart;
        bool wandUnanswered = _wandSwap.FailedId == wandId;
        FinishWandSwap(now, done: false, why: step.Why);
        if (step.Why == "no pack with room") return SignalSwapFailure(forBuff, mainAtStart, wandId);
        if (wandUnanswered) NoteWandWieldFailed(wandId);
        _pendingWieldId = 0;
        _serverMagicDueUntil = DateTime.MinValue;
        _wieldCooldownUntil = now.AddMilliseconds(WieldCooldownMs);
        // A vital's kit or potion gets its turn now, not after the retries.
        _magicModeBlocked = true;
        if (++_wieldGateFailCount >= WieldGateFailMax)
            return SignalSwapFailure(forBuff, mainAtStart, wandId);
        return false;
    }

    /// <summary>The swap is over (<paramref name="done"/>: the wand is in hand): the gate is free again, and the log says how long it took.</summary>
    private void FinishWandSwap(DateTime now, bool done, string why = "")
    {
        if (!_wandSwapOpen) return;   // once per swap (a failed sequence has already stopped itself)
        _wandSwapOpen = false;
        double ms = (now - _wandSwap.StartedAt).TotalMilliseconds;
        string trail = _wandSwap.Trail;
        _magicAfterPeaceUntil = done && _wandSwap.WentThroughPeace ? now.AddMilliseconds(MagicAfterPeaceMs) : DateTime.MinValue;
        _wandSwap.Cancel();
        _weaponSwapGate?.Release("buff-wand", _wandSwapLastMoved);
        LastWandSwapMs = ms;
        LastWandSwapTrail = trail;
        _host.Log(done
            ? $"[WieldGate] wand in hand {ms:0} ms after the switch began ({trail})"
            : $"[WieldGate] the switch to the wand stopped after {ms:0} ms: {why} ({trail})");
    }

    /// <summary>
    /// Each heartbeat, before the cast gates: a swap step the server has answered is followed at once
    /// (the cast interval, a gesture or a busy count hold casts, not item moves), and a swap nothing
    /// asks for any more (the heal or buff went away) stops, without counting as a failure.
    /// </summary>
    private void TickWandSwap()
    {
        if (!_wandSwap.Active) return;
        DateTime now = Now;
        if ((now - _wandSwapTouchedAt).TotalMilliseconds > WandSwapAbandonMs)
        {
            FinishWandSwap(now, done: false, why: "nothing needs the wand now");
            return;
        }
        WandSwapStep(_wandSwapWandId, _wandSwapForBuff, now);
    }

    /// <summary>A death, a portal or the macro stopping ended the swap: not a failed swap.</summary>
    private void DropWandSwap(string why)
    {
        if (!_wandSwap.Active) return;
        FinishWandSwap(Now, done: false, why: why);
        _pendingWieldId = 0;
        _serverMagicDueUntil = DateTime.MinValue;
    }

    /// <summary>What the server says is wielded (item id -> EquipMask), read fresh.</summary>
    private Dictionary<int, int> ReadHands() => EquippedState.ReadFresh(_host, _worldObjectCache);

    /// <summary>
    /// The burst: peace, the moves out of the hands (raw PutItemInContainer, which the client doesn't
    /// gate on a pending item request) and the wand's wield, queued in one tick so they go out in one
    /// frame, before the client starts the peace animation that holds item actions (0.6 s a step on
    /// the step route). In peace the server answers every one at once. Only when the server can't be
    /// mid-animation: no combat-mode change for BurstQuietMs (ACE NextUseTime is set by stance changes,
    /// uses and kits; the pacer refuses the peace request while a use is unanswered). Anything that
    /// hasn't landed in HandSwapSequence.BurstResolveMs goes on one step at a time.
    /// 0 = not tried (the step route), 1 = held this tick, 2 = sent.
    /// </summary>
    private int TryWandBurst(IReadOnlyDictionary<int, int> hands, int wandId, DateTime now)
    {
        if (!_host.HasPutItemInContainer || !_host.HasWieldItem) return 0;
        if ((now - _modeChangedAt).TotalMilliseconds < BurstQuietMs) return 0;
        var moves = _wandSwap.Blockers(hands);
        if (moves.Count == 0 || moves.Count > 2) return 0;
        int pack = WorldObjectCache.FindPackFor(_host, _worldObjectCache, includeMainPack: true, requireFree: moves.Count);
        if (pack == 0) return 0;
        if (!_host.ChangeCombatModeFor(CombatMode.NonCombat, "Buff", "wand swap burst: peace, the hands and the wand at once")) return 1;
        foreach (int id in moves)
        {
            if (hands.TryGetValue(id, out int slot) && (slot & HandSwapSequence.ShieldSlot) != 0) OffhandPutAway(id);
            _host.PutItemInContainer(unchecked((uint)id), unchecked((uint)pack), 0);
        }
        _host.WieldItem(unchecked((uint)wandId), HeldSlot);
        _wandSwap.SentBurst(moves, now);
        _wandSwapLastMoved = wandId;
        NoteStanceRequest();
        _pendingWieldId = wandId;
        _pendingWieldAt = now;
        _host.Log($"[WieldGate] burst {(now - _wandSwap.StartedAt).TotalMilliseconds:0} ms into the switch: peace, " +
                  string.Join(", ", moves.ConvertAll(id => $"0x{(uint)id:X8} '{ItemName(id)}'")) +
                  $" -> pack 0x{(uint)pack:X8}, 0x{(uint)wandId:X8} '{ItemName(wandId)}' -> Held, in one frame");
        return 2;
    }

    /// <summary>
    /// The burst goes with Fast Weapon Swap (when the server is quiet; else the step route). /ra swaptest
    /// burst on|off overrides it until the next reload, to compare the two routes.
    /// </summary>
    internal bool SwapBurst { get => _swapBurstOverride ?? _settings.FastWeaponSwap; set => _swapBurstOverride = value; }
    private bool? _swapBurstOverride;
    private const double BurstQuietMs = 1300;
    private int _lastSeenMode = -1;
    private DateTime _modeChangedAt = DateTime.MinValue;
    /// <summary>When the combat mode last changed, read every heartbeat (combat's tick doesn't run while buffing has it).</summary>
    internal DateTime LastModeChangeAt => _modeChangedAt;

    /// <summary>Each heartbeat: when the combat mode last changed (the burst waits out a stance change's animation).</summary>
    private void NoteModeChange()
    {
        int m = CurrentCombatMode;
        if (m == _lastSeenMode) return;
        bool first = _lastSeenMode == -1;   // the first read after a load is not a change
        _lastSeenMode = m;
        if (!first) _modeChangedAt = Now;
        // In Magic: a stance episode is over, whoever got there. EnsureMagicMode ends it only when it runs in
        // Magic itself; when the cast went out from another step, or combat took the stance back to its weapons
        // first, the clock kept running and the next buff, 18 minutes later, logged "stuck 1123s". The
        // launcher's wedge rule (a stance stuck over 300 s = an item-action hard lock) killed Drakkon on it (10-23).
        if (m == CombatMode.Magic) EndStanceEpisode();
    }

    /// <summary>The stance-stuck bookkeeping back to "no episode" (Magic was reached).</summary>
    private void EndStanceEpisode()
    {
        _buffStanceConsecutiveFails = 0;
        _buffStanceStuckSince = DateTime.MinValue;
        _buffStanceReEquips = 0;
        _buffStanceWedgeWarned = false;
    }

    /// <summary>A stance request after this long without one starts a new episode (the backoff's longest wait is 30 s).</summary>
    private const double StanceEpisodeGapMs = 60_000;

    /// <summary>
    /// The wand is in hand after a swap through peace: Magic is asked for now, from the swap itself (it
    /// can run before the cast gates, which the pacer holds while our own peace request settles: 0.75 s
    /// on Freya, 10-22 22:49). The server holds the change until the peace animation ends by itself.
    /// The same bookkeeping as the Magic request in EnsureMagicMode, which won't send it twice.
    /// </summary>
    private void MagicAfterPeace(DateTime now)
    {
        if (now >= _magicAfterPeaceUntil || CurrentCombatMode != CombatMode.NonCombat || !OnlyOurPeaceRequestPending()) return;
        if (!_host.ChangeCombatModeFor(CombatMode.Magic, "Buff", "wand in hand after peace: Magic mode", force: true)) return;
        if (_buffStanceStuckSince == DateTime.MinValue) { _buffStanceStuckSince = now; _buffStanceStuckStartMs = ActionPacer.NowMs(); }
        NoteStanceRequest();
        _lastBuffStanceAttempt = now;
        _lastCastAttempt = now;
        if (_buffStanceConsecutiveFails < 6) _buffStanceConsecutiveFails++;
        _host.Log($"[WieldGate] Magic asked for {(now - _wandSwap.StartedAt).TotalMilliseconds:0} ms into the switch (the server starts it when the peace animation ends)");
    }

    /// <summary>Nothing the pacer waits on but our own request for peace (or nothing at all).</summary>
    private bool OnlyOurPeaceRequestPending()
    {
        var a = ActionPacer.Current(_host);
        return a == null || (a.Kind == PacedKind.CombatMode && a.ModeTarget == CombatMode.NonCombat && a.Who == "Buff");
    }

    private static int HandAt(IReadOnlyDictionary<int, int> hands, int slot)
    {
        foreach (var kv in hands) if ((kv.Value & slot) != 0) return kv.Key;
        return 0;
    }

    /// <summary>The weapon in the main hand (not the wand being wielded, not the off hand); 0 = none.</summary>
    private static int MainHandIn(IReadOnlyDictionary<int, int> hands, int wandId)
    {
        const int main = HandSwapSequence.MeleeSlot | HandSwapSequence.MissileSlot | HandSwapSequence.TwoHandedSlot | HandSwapSequence.HeldSlot;
        foreach (var kv in hands) if (kv.Key != wandId && (kv.Value & main) != 0) return kv.Key;
        return 0;
    }

    private string DescribeHands(IReadOnlyDictionary<int, int> hands)
    {
        var parts = new List<string>();
        foreach (var kv in hands)
        {
            if ((kv.Value & HandSwapSequence.HandSlots) == 0) continue;
            string where = (kv.Value & HandSwapSequence.ShieldSlot) != 0 ? "off hand" : "main hand";
            parts.Add($"{where} 0x{(uint)kv.Key:X8} '{ItemName(kv.Key)}'");
        }
        return parts.Count == 0 ? "hands empty" : string.Join(", ", parts);
    }

    private string ItemName(int id) => _worldObjectCache?[id]?.Name ?? "?";

    /// <summary>
    /// Locates a wand to equip: a listed wand, the one already in hand first (so buffing
    /// doesn't swap one listed wand for another). The pack is searched only when the Items list
    /// has no wand at all and WieldUnlistedWandWhenNoneListed is on, by object class only.
    /// Shares WeaponList.FindWand with CombatManager.FindWandInItems.
    /// </summary>
    private int FindWandInItems()
    {
        if (_worldObjectCache == null) return 0;
        var cache = _worldObjectCache;
        return WeaponList.FindWand(_settings.ItemRules, i => cache[i], wo => WorldObjectCache.IsWieldedByPlayer(_host, wo),
            cache.GetInventory(), _settings.WieldUnlistedWandWhenNoneListed, out _, SkipBuffWand, BuffWandRank);
    }

    /// <summary>0 = no wield requirement (a plain caster: the surest), 1 = a requirement it meets.</summary>
    private int BuffWandRank(WorldObject wo) => WieldRequirements.Has(_host, unchecked((uint)wo.Id)) ? 1 : 0;

    // Identify requests for listed casters whose requirements aren't known yet: (count, last ask).
    private readonly Dictionary<int, (int Count, DateTime At)> _wandIdAsked = new();
    private const int WandIdMaxAsks = 3;
    private const double WandIdRetrySec = 10;

    // ── Buff wands that can't be wielded ────────────────────────────────────
    // 2026-10-18 01:00-01:24: Drakkon's buff wand was gone, the next caster on his Items list was
    // an Electric Baton that needs War Magic (untrained). Buffing sent its wield every ~8 s for
    // 24 minutes, the server never answered, every buff was parked "fighting unbuffed" in turn,
    // and Heal Self (which never gives up on the wand) couldn't go when he needed it: he died.
    private readonly Dictionary<int, int> _wandWieldFails = new();
    private readonly Dictionary<int, DateTime> _wandSkipUntil = new();
    private readonly HashSet<int> _wandReqNoted = new();
    internal const int WandWieldFailMax = 2;
    internal const double WandSkipMin = 30;

    // PropertyInt WieldRequirements (158), WieldSkillType (159), WieldDifficulty (160); ACE
    // WieldRequirement 1 = Skill (buffed), 2 = RawSkill (base), 8 = Training.
    private const uint PropWieldReq = 158, PropWieldSkill = 159, PropWieldDiff = 160;
    // EquipMask Held: the casters' slot.
    private const uint HeldSlot = 0x01000000;

    /// <summary>A caster buffing must not try: its wield requirement isn't met, or its wields kept failing.</summary>
    private bool SkipBuffWand(WorldObject wo)
    {
        if (_wandSkipUntil.TryGetValue(wo.Id, out DateTime until))
        {
            if (RynthClock.Now < until) return true;
            _wandSkipUntil.Remove(wo.Id);
            _wandWieldFails.Remove(wo.Id);
        }
        // Not identified yet: its requirements aren't known (the Acid Staff was tried unidentified and
        // the client refused it, 2026-10-18). Ask, and skip it until the answer is in; after
        // WandIdMaxAsks unanswered asks it is tried anyway (a server refusal still rules it out).
        uint uid = unchecked((uint)wo.Id);
        if (_host.HasRequestId && !_host.HasAppraisalData(uid))
        {
            _wandIdAsked.TryGetValue(wo.Id, out var asked);
            if (asked.Count < WandIdMaxAsks)
            {
                if ((RynthClock.Now - asked.At).TotalSeconds >= WandIdRetrySec)
                {
                    // Counted only when the engine took it: a refused ask (a death or a portal) is not one of the
                    // WandIdMaxAsks, and the wand is held back and asked about again.
                    if (_host.RequestId(uid))
                    {
                        _wandIdAsked[wo.Id] = (asked.Count + 1, RynthClock.Now);
                        _host.Log($"[WieldGate] identifying 0x{uid:X8} '{wo.Name}' before using it to buff (its wield requirement isn't known yet)");
                    }
                }
                return true;
            }
        }
        string? why = UnmetWandRequirement(wo.Id);
        if (why == null) return false;
        if (_wandReqNoted.Add(wo.Id))
        {
            _host.Log($"[WieldGate] not using 0x{(uint)wo.Id:X8} '{wo.Name}' to buff: {why}");
            _host.WriteToChat($"[RynthAi] Buffing: {wo.Name} can't be wielded ({why}), so it isn't used to buff.", 1);
        }
        return true;
    }

    /// <summary>The caster's wield requirement the character doesn't meet (null = met, none, or not known).</summary>
    private string? UnmetWandRequirement(int id) => WieldRequirements.Unmet(_host, _charSkills, unchecked((uint)id));

    /// <summary>A wield of <paramref name="wandId"/> went unanswered: after WandWieldFailMax, leave it out for WandSkipMin.</summary>
    private void NoteWandWieldFailed(int wandId)
    {
        if (wandId == 0) return;
        int n = _wandWieldFails.TryGetValue(wandId, out int f) ? f + 1 : 1;
        _wandWieldFails[wandId] = n;
        // The server said "skill too low" (WeenieError 0x0468) for this very wand: no second try.
        // Unbuffed after a death every caster on the list can be under its requirement; one try
        // each gets to the bare-handed Magic stance (AC casts without a wand) in seconds, not minutes.
        // Any refusal naming this wand is final (with the Held-slot wield the server answers a refused
        // wield with InventoryServerSaveFailed for the item); "too busy" is not a refusal.
        bool tooLow = _host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out _, out uint wErr, out _, out uint wObj)
                      && wObj == unchecked((uint)wandId) && wErr != 0 && wErr != 0x1D;
        if (n < WandWieldFailMax && !tooLow) return;
        _wandSkipUntil[wandId] = RynthClock.Now.AddMinutes(WandSkipMin);
        _wandWieldFails.Remove(wandId);
        string name = _worldObjectCache?[wandId]?.Name ?? "?";
        _host.Log($"[WieldGate] 0x{(uint)wandId:X8} '{name}' didn't wield after {n} tries: left out of buffing for {WandSkipMin:0} min");
        _host.WriteToChat(tooLow
            ? $"[RynthAi] Buffing: your skill is too low to wield {name}. Left out for {WandSkipMin:0} min (with no caster left, buffs are cast bare-handed)."
            : $"[RynthAi] Buffing: {name} wouldn't wield ({n} tries, no answer from the server). Left out for {WandSkipMin:0} min; check its wield requirement.", 1);
    }

    private static bool IsWandObject(WorldObject wo) => WeaponList.IsWand(wo);
}

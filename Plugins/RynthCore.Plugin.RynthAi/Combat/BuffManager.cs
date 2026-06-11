using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public class BuffManager : IDisposable
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private readonly SpellManager _spellManager;
    private readonly PlayerVitalsCache _vitals;

    private CharacterSkills? _charSkills;
    private WorldObjectCache? _worldObjectCache;

    private DateTime _lastCastAttempt = DateTime.MinValue;
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

    /// <summary>Client busy count — when > 0, don't send any game actions.</summary>
    public int BusyCount { get; set; }

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

    public BuffManager(RynthCoreHost host, LegacyUiSettings settings, SpellManager spellManager, PlayerVitalsCache vitals)
    {
        _host = host;
        _settings = settings;
        _spellManager = spellManager;
        _vitals = vitals;
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
            foreach (string line in System.IO.File.ReadAllLines(_buffTimerPath))
            {
                string[] p = line.Split('|');

                if (p[0] == "item" && p.Length >= 6)
                {
                    DateTime castAt    = new DateTime(long.Parse(p[2]));
                    DateTime expiresAt = new DateTime(long.Parse(p[3]));
                    int level = int.Parse(p[4]);
                    string name = p[5];
                    if (expiresAt > DateTime.Now)
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
                    if (expiration > DateTime.Now)
                        _ramBuffTimers[family] = new RamTimerInfo { Expiration = expiration, SpellLevel = level, SpellName = name };
                }
            }
            int total = _ramBuffTimers.Count + _itemSpellTimers.Count;
            if (total > 0)
                _host.WriteToChat($"[RynthAi] Restored {_ramBuffTimers.Count} player + {_itemSpellTimers.Count} item spell timer(s).", 1);
        }
        catch { }
    }

    public void Dispose() { }

    public void ForceFullRebuff()
    {
        _isForceRebuffing = true;
        _forceRebuffCastFamilies.Clear();
        _buffFailCooldownUntil.Clear(); // explicit recast-all must not be blocked by stale cooldowns
        _silentNoShowCounts.Clear();    // give parked families a fresh shot on FR too
        _ramBuffTimers.Clear();
        _itemSpellTimers.Clear();
        _pendingSpellId = 0;
        _lastCastAttempt = DateTime.MinValue;
        SaveBuffTimers();
        _host.WriteToChat("[RynthAi] Starting Force Rebuff...", 5);

        var list = BuildDynamicBuffList();
        _host.Log($"[FR] buff list ({list.Count}): {string.Join(", ", list)}");
        _host.Log($"[FR] macroRunning={_settings.IsMacroRunning} liveRefreshed={_liveBuffsRefreshed}");
    }

    public void CancelBuffing()
    {
        _isForceRebuffing = false;
        _isRechargingMana = false;
        _isRechargingStamina = false;
        _isHealingSelf = false;
        _pendingSpellId = 0;
        _host.WriteToChat("[RynthAi] Sequence cancelled.", 5);
    }

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
    }

    private bool _liveBuffsRefreshed;
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

    public void OnHeartbeat()
    {
        // Post-cast diffs for the bufftest experiment run unconditionally — even if the
        // user toggled the macro off mid-experiment, we still want to capture and log them.
        // Drain any pending diagnostics whose 2s post-cast window has elapsed.
        while (_pendingDiagnostics.Count > 0 &&
               (DateTime.Now - _pendingDiagnostics.Peek().CastedAt).TotalSeconds >= 2.0)
        {
            var diag = _pendingDiagnostics.Dequeue();
            LogPostCastDiff(diag);
        }

        if (!_settings.IsMacroRunning) return;

        // RefreshFromLiveMemory at OnLoginComplete fails when the server-time
        // packet hasn't landed yet (GetServerTime returns 0). On those
        // logins we have NO timers and would recast every buff that has
        // hours left server-side. Retry every second until we get the
        // live snapshot, and BLOCK any cast attempts in the meantime.
        if (!_liveBuffsRefreshed)
        {
            if ((DateTime.Now - _lastLiveRefreshAttempt).TotalMilliseconds > 1000)
            {
                _lastLiveRefreshAttempt = DateTime.Now;
                int n = RefreshFromLiveMemory();
                if (n >= 0)
                {
                    if (_loginRefreshStartAt == DateTime.MinValue) _loginRefreshStartAt = DateTime.Now;
                    // Only trust the snapshot once the registry stops growing
                    // (two equal 1s reads), so we don't open the gate on a
                    // half-streamed set and rebuff buffs that are still landing.
                    bool stable    = (n == _lastLoginRefreshCount);
                    bool timedOut  = (DateTime.Now - _loginRefreshStartAt).TotalMilliseconds > LoginRefreshMaxWaitMs;
                    _lastLoginRefreshCount = n;
                    if (stable || timedOut)
                    {
                        _liveBuffsRefreshed = true;
                        _host.Log($"[BuffDiag] login refresh ready: {n} enchantment(s) (stable={stable} timedOut={timedOut})");
                        _host.WriteToChat($"[RynthAi] Live buff timers ready ({n} loaded).", 1);
                    }
                }
            }
            // Don't cast anything until we have a real snapshot — otherwise
            // we'd recast over a 1-hour-old buff at the start of every login.
            return;
        }

        // Periodic re-sync: if the character died and lost all enchantments, the
        // in-memory timers still show buffs as "active" until their timestamps
        // expire normally — causing NeedsAnyBuff() to return false while the char
        // is completely unbuffed. Re-read from live AC memory every 30s to detect
        // this gap and clear stale timers so rebuffing resumes promptly.
        if ((DateTime.Now - _lastPeriodicRefreshAt).TotalMilliseconds > PeriodicRefreshIntervalMs)
        {
            _lastPeriodicRefreshAt = DateTime.Now;
            int n = RefreshFromLiveMemory();
            if (n >= 0)
                _host.Log($"[BuffDiag] periodic sync: {n} active enchantment(s) in RAM timers.");
        }

        // ── Pending-cast resolution — self-buffs and armor use DIFFERENT signals ──
        // Self-buffs ARE in the live player-enchantment registry, so we confirm
        // them by reading it (no chat, no no-chat blacklisting that poisons the
        // tier walk). Armor/item enchants are NOT in the player registry — they
        // live on the item — so they still resolve via chat + the no-chat valve.
        if (_pendingSpellId != 0)
        {
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            bool pendingIsArmor = pendingSpell != null && IsItemEnchantment(pendingSpell.Name);

            if (pendingSpell != null && !pendingIsArmor)
            {
                // SELF-BUFF: confirm against the live registry once the cast has
                // settled. (OnEnchantmentAdded usually clears pending faster; this
                // poll is the authoritative fallback when the hook doesn't fire.)
                double sinceCastMs = (DateTime.Now - _lastCastAttempt).TotalMilliseconds;
                if (sinceCastMs > SelfBuffConfirmMs
                    && (DateTime.Now - _lastSelfBuffPollAt).TotalMilliseconds > 250)
                {
                    _lastSelfBuffPollAt = DateTime.Now;
                    RefreshFromLiveMemory();
                    bool active = _ramBuffTimers.TryGetValue(pendingSpell.Family, out RamTimerInfo? st)
                                  && st.Expiration > DateTime.Now;
                    if (active)
                    {
                        // During an auto-batch / force-rebuff, IsBuffActive gates on
                        // _forceRebuffCastFamilies (not timers), so the family MUST be
                        // marked cast here or the batch respins this buff forever.
                        if (_isForceRebuffing) _forceRebuffCastFamilies.Add(pendingSpell.Family);
                        _pendingSpellId = 0; // landed — registry confirms it; advance
                        _onCastResolved?.Invoke("self-buff confirmed (registry)");
                    }
                    else if (sinceCastMs > SelfBuffGiveUpMs)
                    {
                        // Not in the registry after settling → this tier didn't take.
                        // A WARM snapshot already excludes unknown tiers, so a no-show
                        // is lag/fizzle → retry (NO blacklist). A COLD snapshot has no
                        // other tier-down signal → blacklist this id to drop a tier.
                        bool cold = !_spellManager.IsKnownSnapshotWarm;
                        if (cold) _spellManager.MarkSpellUnresolvable(_pendingSpellId);

                        // Per-family silent-no-show bookkeeping (the /god loop break).
                        // Increment regardless of warmth — even the "warm → retry" path
                        // needs an upper bound, otherwise a buff whose family is in the
                        // dynamic list but can never actually land loops forever.
                        int fam = pendingSpell.Family;
                        int noShowCount = _silentNoShowCounts.TryGetValue(fam, out int prev) ? prev + 1 : 1;
                        _silentNoShowCounts[fam] = noShowCount;
                        bool parked = false;
                        if (noShowCount >= SilentNoShowThreshold)
                        {
                            _buffFailCooldownUntil[fam] = DateTime.Now + SilentNoShowCooldown;
                            parked = true;
                        }

                        _host.Log($"[BuffDiag] self-buff '{pendingSpell.Name}' (id={_pendingSpellId}, fam={fam}) absent from live registry after {sinceCastMs:0}ms — " +
                                  (cold ? "cold snapshot → blacklisted, tier-down." : "warm snapshot → retry (lag/fizzle).") +
                                  $" noShows={noShowCount}/{SilentNoShowThreshold}" +
                                  (parked ? $" — family parked {SilentNoShowCooldown.TotalMinutes:0}min." : ""));
                        _pendingSpellId = 0;
                        _onCastResolved?.Invoke("self-buff no-show (registry)");
                    }
                }
                // Hold buffing state while the self-buff cast is in flight so
                // CombatManager can't sneak a peace-mode switch in mid-cast.
                if (_pendingSpellId != 0) { _settings.BotAction = "Buffing"; return; }
            }
            else if (pendingIsArmor)
            {
                // ARMOR/ITEM: chat is authoritative. No-chat valve abandons a cast
                // AC never answers so the cycle can't wedge; the chat handlers do
                // the cooldown. Blacklist (unless snapshot confirms known) drops tier.
                if ((DateTime.Now - _lastCastAttempt).TotalMilliseconds > NoChatResolveTimeoutMs)
                {
                    int stuckId = _pendingSpellId;
                    var stuck = SpellTableStub.GetById(stuckId);
                    bool confirmedKnown = _spellManager.IsKnownSnapshotWarm && _spellManager.IsKnownSpellId(stuckId);
                    if (!confirmedKnown)
                        _spellManager.MarkSpellUnresolvable(stuckId);

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
                    if (fam != 0)
                    {
                        int noShowCount = _silentNoShowCounts.TryGetValue(fam, out int prev) ? prev + 1 : 1;
                        _silentNoShowCounts[fam] = noShowCount;
                        if (noShowCount >= SilentNoShowThreshold)
                        {
                            _buffFailCooldownUntil[fam] = DateTime.Now + SilentNoShowCooldown;
                            parked = true;
                        }
                    }

                    _host.Log($"[BuffChat] NO-CHAT TIMEOUT (armor) pending={stuckId} ('{stuck?.Name}') — no chat in " +
                              $"{NoChatResolveTimeoutMs:0}ms. confirmedKnown={confirmedKnown}. " +
                              (confirmedKnown ? "Lag/busy — NOT blacklisted." : "Blacklisted → tier-down.") +
                              (fam != 0 ? $" noShows={_silentNoShowCounts[fam]}/{SilentNoShowThreshold}" : "") +
                              (parked ? $" — family parked {SilentNoShowCooldown.TotalMinutes:0}min." : ""));
                    _pendingSpellId = 0;
                    _onCastResolved?.Invoke("no-chat timeout (armor)");
                }
                // Hold the cycle until chat resolves the item cast.
                if (_pendingSpellId != 0) { _settings.BotAction = "Buffing"; return; }
            }
        }

        if ((DateTime.Now - _lastCastAttempt).TotalMilliseconds < _settings.SpellCastIntervalMs)
        {
            _settings.BotAction = "Buffing";
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
        // Keep BotAction pinned to "Buffing" while our cast is in flight so
        // CombatManager can't sneak in a peace-mode switch mid-cast.
        if (!CastGateWatchdog.CanCastNow(_host.CanCastNow, s => _host.Log(s)) || BusyCount > 0)
        {
            if (_pendingSpellId != 0) _settings.BotAction = "Buffing";
            return;
        }

        if (CheckVitals())
        {
            _settings.BotAction = "Buffing";
            return;
        }

        if (_settings.EnableBuffing)
        {
            if (CheckAndCastSelfBuffs())
            {
                _settings.BotAction = "Buffing";
                return;
            }

            if (_isForceRebuffing)
            {
                _isForceRebuffing = false;
                _host.Log($"[FR] complete — cast {_forceRebuffCastFamilies.Count} spell families");
                _forceRebuffCastFamilies.Clear();
                _host.WriteToChat("[RynthAi] Force Rebuff Complete.", 1);
            }
        }

        // Don't release the "Buffing" state while a cast is still pending server
        // confirmation — otherwise CombatManager.OnHeartbeat runs in the window
        // between cast-issued and "You cast X" chat arriving, and the peace-mode
        // switch fizzles the spell.
        if (_settings.BotAction == "Buffing" && _pendingSpellId == 0)
            _settings.BotAction = "Default";
    }

    private bool CheckVitals()
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

        // Emergency override regardless of mode: HP critical + stam available →
        // burn stam for HP. Sits below the configurable thresholds so even a
        // "do nothing" recharge config still saves the character.
        if (curHealthPct <= 30 && curStamPct > 20)
        {
            _isHealingSelf = true;
            return AttemptVitalCast("Stamina to Health Self");
        }

        // Pick the threshold set based on hunting state. A target within
        // MonsterRange = active combat → react at the LOW (HealAt / RestamAt /
        // GetManaAt) thresholds so we don't bloat cast traffic mid-fight.
        // Otherwise idle → top up to the HIGH (TopOffHP / TopOffStam /
        // TopOffMana) thresholds so we re-engage at full. Thresholds are
        // strict "<": set a value to 0 to disable that vital in that mode.
        bool inCombat = _combatManager?.HasCloseThreat(System.Math.Max(1, _settings.MonsterRange)) == true;
        int hpThreshold   = inCombat ? _settings.HealAt    : _settings.TopOffHP;
        int manaThreshold = inCombat ? _settings.GetManaAt : _settings.TopOffMana;
        int stamThreshold = inCombat ? _settings.RestamAt  : _settings.TopOffStam;

        _isHealingSelf       = curHealthPct < hpThreshold;
        _isRechargingMana    = curManaPct   < manaThreshold;
        _isRechargingStamina = curStamPct   < stamThreshold;

        if (_isHealingSelf)
        {
            if (AttemptHealthKitUse()) return true;
            return AttemptVitalCast("Heal Self");
        }
        if (_isRechargingMana && curStamPct > 15) return AttemptVitalCast("Stamina to Mana Self");
        if (_isRechargingStamina) return AttemptVitalCast("Revitalize Self");

        return false;
    }

    private bool AttemptVitalCast(string baseName)
    {
        int spellId = FindBestSpellId(baseName, AcSkillType.LifeMagic);
        if (spellId == 0) return false;
        if (!EnsureMagicMode()) return true;
        _pendingSpellId = spellId;
        _host.CastSpell((uint)_host.GetPlayerId(), spellId);
        _lastCastAttempt = DateTime.Now;
        return true;
    }

    private bool AttemptHealthKitUse()
    {
        if (_worldObjectCache == null) return false;

        foreach (var rule in _settings.ConsumableRules)
        {
            if (!rule.Type.Equals("HealthKit", StringComparison.OrdinalIgnoreCase))
                continue;

            if (_worldObjectCache[rule.Id] == null)
                continue;

            uint playerId = _host.GetPlayerId();
            if (playerId == 0) return false;
            _host.UseObjectOn(unchecked((uint)rule.Id), playerId);
            _lastCastAttempt = DateTime.Now;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if any self-buff in the current buff list is missing/expired,
    /// without actually casting. Used by NeedToBuff meta condition.
    /// </summary>
    public bool NeedsAnyBuff()
    {
        if (!_settings.EnableBuffing) return false;
        return _isForceRebuffing || AnyBuffBelowThreshold(BuildDynamicBuffList(), _settings.RebuffSecondsRemaining);
    }

    // Returns true if any castable buff in the list has < thresholdSec remaining.
    // Used both as the arbiter query (NeedsAnyBuff) and as the batch-rebuff trigger
    // inside CheckAndCastSelfBuffs — keeps the two in sync on when buffing is needed.
    // Families currently parked in _buffFailCooldownUntil are skipped so a parked
    // family can't keep retriggering the batch (which would otherwise loop:
    // batch starts → clears cooldown → cast again → silent no-show → re-park).
    private bool AnyBuffBelowThreshold(List<string> desiredBuffs, int thresholdSec)
    {
        DateTime now = DateTime.Now;
        foreach (string buffBaseName in desiredBuffs)
        {
            AcSkillType castSkill = SkillForBuff(buffBaseName);
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
        return false;
    }

    private bool CheckAndCastSelfBuffs()
    {
        // Auto batch-rebuff: when any buff drops below the configured threshold,
        // trigger a full rebuff so ALL timers re-align in one session rather than
        // each buff trickling in one at a time as it individually expires.
        if (!_isForceRebuffing && AnyBuffBelowThreshold(BuildDynamicBuffList(), _settings.RebuffSecondsRemaining))
        {
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
            _forceRebuffCastFamilies.Clear();
        }

        List<string> desiredBuffs = BuildDynamicBuffList();
        bool diagnose = _isForceRebuffing;

        foreach (string buffBaseName in desiredBuffs)
        {
            AcSkillType castSkill = SkillForBuff(buffBaseName);
            if (!IsSkillUsable(castSkill))
            {
                if (diagnose) _host.Log($"[FR] skip '{buffBaseName}' — skill {castSkill} not usable");
                continue;
            }

            int spellId = FindBestSpellId(buffBaseName, castSkill);
            if (spellId == 0)
            {
                if (diagnose) _host.Log($"[FR] skip '{buffBaseName}' — FindBestSpellId returned 0");
                continue;
            }

            if (IsBuffActive(spellId))
            {
                if (diagnose) _host.Log($"[FR] skip '{buffBaseName}' (id={spellId}) — already active");
                continue;
            }

            // Skip a family the server just hard-rejected (no components, etc.)
            // so an uncastable buff isn't re-cast every cycle (→ AC AV). Auto-
            // recovers when the cooldown expires or a cast of it later confirms.
            int buffFamily = SpellTableStub.GetById(spellId)?.Family ?? 0;
            if (buffFamily != 0
                && _buffFailCooldownUntil.TryGetValue(buffFamily, out DateTime coolUntil)
                && DateTime.Now < coolUntil)
            {
                if (diagnose) _host.Log($"[FR] skip '{buffBaseName}' (id={spellId}) — hard-fail cooldown {(coolUntil - DateTime.Now).TotalSeconds:0}s");
                continue;
            }

            if (!EnsureMagicMode()) return true;

            _pendingSpellId = spellId;

            var spellInfo = SpellTableStub.GetById(spellId);
            if (spellInfo != null)
                _host.WriteToChat($"[RynthAi] Casting: {spellInfo.Name}", 5);
            // NOTE: timers are recorded ONLY on chat confirmation ("you cast X"),
            // never optimistically here. An optimistic item-timer at cast time
            // left a phantom 1h "active" buff whenever a cast silently failed
            // (e.g. an unknown higher tier), so the bot thought armor was
            // buffed when it wasn't and never retried the known tier.

            if (diagnose) _host.Log($"[FR] CAST '{buffBaseName}' resolvedSpellId={spellId} (pending now set)");
            bool castOk = _host.CastSpell((uint)_host.GetPlayerId(), spellId);
            _lastCastAttempt = DateTime.Now;
            if (!castOk)
            {
                // Local CastSpell rejected — packet never went out, so no chat
                // will ever arrive. Clear the gate immediately so the cycle
                // doesn't hang on this spell. (No timer to undo — we no longer
                // record optimistically.)
                _pendingSpellId = 0;
                if (diagnose) _host.Log($"[FR] cast '{buffBaseName}' returned false — pending cleared");
            }
            return true;
        }
        return false;
    }

    private int FindBestSpellId(string baseName, AcSkillType skill)
        => _spellManager.GetDynamicSelfBuffId(baseName, skill);

    // True for enchantments that live on an ITEM (armor or weapon), NOT on the
    // player. These do NOT appear in ReadPlayerEnchantments, so they must be
    // confirmed via chat, stored in _itemSpellTimers, and PRESERVED across a
    // RefreshFromLiveMemory (which rebuilds player buffs from the live registry
    // and would otherwise wipe them → perpetual recast = "buffing in circles").
    // Matches base names AND tier-7 lore / "Aura of" forms.
    private static bool IsItemEnchantment(string name)
    {
        string[] itemSpells = {
            // Armor: Impenetrability + Brogard's, and the elemental/physical Banes
            "Impenetrability", "Brogard's Defiance", "Acid Bane", "Olthoi's Bane",
            "Blade Bane", "Swordsman's Bane", "Swordman's Bane", "Bludgeoning Bane", "Tusker's Bane",
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
        return false;
    }

    /// <summary>
    /// Called immediately when CheckAndCastSelfBuffs decides to cast an item spell.
    /// Records the timer optimistically — removed on fizzle.
    /// </summary>
    private void RecordItemSpellCast(SpellInfo spellInfo)
    {
        int level = GetSpellLevel(spellInfo);
        double duration = GetCustomSpellDuration(level);
        var now = DateTime.Now;
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
                CastedAt    = DateTime.Now,
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
        if (_isForceRebuffing)
            return _forceRebuffCastFamilies.Contains(targetSpell.Family);

        int targetLevel = GetSpellLevel(targetSpell);
        // Recast when remaining duration drops below this many seconds.
        // User-configurable via Advanced Settings → Buffing; overridden during
        // batch-rebuff passes (CheckAndCastSelfBuffs) to 1200s (20 min).
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
                if (itemTimer.SpellLevel < targetLevel)
                {
                    LogArmorRecast(targetSpell, $"tier-upgrade recordedLvl={itemTimer.SpellLevel} < targetLvl={targetLevel} (recorded='{itemTimer.SpellName}')");
                    return false;
                }
                if (DateTime.Now < itemTimer.ExpiresAt.AddSeconds(-rebufferSec))
                {
                    _lastArmorRecastReason.Remove(targetSpell.Family); // clear so next recast re-logs
                    return true;
                }
                double remainSec = (itemTimer.ExpiresAt - DateTime.Now).TotalSeconds;
                LogArmorRecast(targetSpell, $"expiry-window remainSec={remainSec:F0} threshold={rebufferSec} (recorded='{itemTimer.SpellName}' lvl={itemTimer.SpellLevel} expiresAt={itemTimer.ExpiresAt:HH:mm:ss})");
            }
            else
            {
                LogArmorRecast(targetSpell, $"NO timer entry for family={targetSpell.Family} (targetLvl={targetLevel}, _itemSpellTimers.Count={_itemSpellTimers.Count})");
            }
            return false;
        }

        // Player buffs — use RAM timers
        if (_ramBuffTimers.TryGetValue(targetSpell.Family, out RamTimerInfo? timer))
        {
            // Tier-upgrade flap guard: many high-tier buffs are Incantations
            // (nominal tier 8) that LAND at a lower, skill-capped tier — e.g.
            // "Incantation of Sprint Self" (target 8) lands as "Sprint Self VI"
            // (6). Comparing the landed 6 against the nominal 8 makes the
            // upgrade rule fire forever → endless recast. Cap the target at the
            // highest tier we've actually observed land for this family, so once
            // the Incantation lands at its real ceiling we stop chasing 8.
            int effectiveTarget = targetLevel;
            if (_familyAchievedTier.TryGetValue(targetSpell.Family, out int achieved) && achieved < effectiveTarget)
                effectiveTarget = achieved;

            if (timer.SpellLevel < effectiveTarget)
            {
                LogPlayerRecast(targetSpell, $"tier-upgrade storedLvl={timer.SpellLevel} < effTarget={effectiveTarget} (nominal={targetLevel}, achievedCap={(_familyAchievedTier.TryGetValue(targetSpell.Family, out int a2) ? a2 : -1)}, stored='{timer.SpellName}')");
                return false;
            }
            if (DateTime.Now < timer.Expiration.AddSeconds(-rebufferSec))
            {
                _lastArmorRecastReason.Remove(targetSpell.Family);
                return true;
            }
            double remainSec = (timer.Expiration - DateTime.Now).TotalSeconds;
            LogPlayerRecast(targetSpell, $"expiry-window remainSec={remainSec:F0} threshold={rebufferSec} storedLvl={timer.SpellLevel} exp={timer.Expiration:HH:mm:ss} (stored='{timer.SpellName}')");
        }
        else
        {
            LogPlayerRecast(targetSpell, $"NO ram-timer entry for family={targetSpell.Family} (targetLvl={targetLevel}, _ramBuffTimers.Count={_ramBuffTimers.Count})");
        }

        if (_isForceRebuffing) return false;

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

    private static int GetSpellLevel(SpellInfo spell)
    {
        string n = spell.Name;
        if (n.StartsWith("Incantation")) return 8;
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

    private int GetArchmageEnduranceCount()
    {
        // TODO: Read augmentation count from AC object memory (key 238 on player object)
        return 0; // STUB
    }

    private double GetCustomSpellDuration(int spellLevel)
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

        // Preserve item enchantment timers (armor banes, Impenetrability, etc.)
        // that live on the item, not the player — ReadPlayerEnchantments won't
        // return them, so clearing would lose them every login.
        var preservedItemTimers = new Dictionary<int, RamTimerInfo>();
        foreach (var kvp in _ramBuffTimers)
        {
            if (kvp.Value.Expiration > DateTime.Now && IsItemEnchantment(kvp.Value.SpellName))
                preservedItemTimers[kvp.Key] = kvp.Value;
        }

        _ramBuffTimers.Clear();
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
            _ramBuffTimers[spellInfo.Family] = new RamTimerInfo
            {
                Expiration  = isPermanent ? PermanentSentinel : DateTime.Now.AddSeconds(remainingSeconds),
                SpellLevel  = level,
                SpellName   = spellInfo.Name,
                IsPermanent = isPermanent,
            };
            // Learn the real ceiling this family lands at (Incantations cap below
            // their nominal tier) so IsBuffActive stops chasing an unreachable tier.
            RecordAchievedTier(spellInfo.Family, level);

            if (isPermanent && _loggedPermanentFamilies.Add(spellInfo.Family))
                _host.Log($"[BuffDiag] permanent player enchant tracked: id={spellInfo.Id} '{spellInfo.Name}' (fam={spellInfo.Family}, lvl={level}) — presence-only, not persisted");
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
                        Expiration = DateTime.Now.AddSeconds(remainingSeconds),
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
        return _itemBuffTimers.TryGetValue(key, out var info) && info.Expiration > DateTime.Now;
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

        if (_ramBuffTimers.Count == 0 && _itemSpellTimers.Count == 0)
        {
            _host.WriteToChat("[RynthAi] No buff timers active.", 1);
            return;
        }

        if (_itemSpellTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Item spells ({_itemSpellTimers.Count}) --", 1);
            PrintItemSpellTimers();
        }

        if (_ramBuffTimers.Count > 0)
        {
            _host.WriteToChat($"[RynthAi] -- Player buffs ({_ramBuffTimers.Count}) --", 1);
            foreach (var kvp in _ramBuffTimers)
            {
                var info = kvp.Value;
                double total = GetCustomSpellDuration(info.SpellLevel);
                TimeSpan left = info.Expiration - DateTime.Now;
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
            AcSkillType skill = SkillForBuff(baseName);
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
                activeTimer = $"timer T{timer.SpellLevel}, {Math.Round((timer.Expiration - DateTime.Now).TotalMinutes, 1)}m left";
            else if (info != null && _itemSpellTimers.TryGetValue(info.Family, out ItemSpellRecord? itemTimer))
                activeTimer = $"item T{itemTimer.SpellLevel}, {Math.Round((itemTimer.ExpiresAt - DateTime.Now).TotalMinutes, 1)}m left";

            _host.WriteToChat($"[RynthAi]   {baseName} → {info?.Name ?? "?"} (T{level}, {activeTimer})", 1);
        }
    }

    public void PrintItemBuffDebug()
    {
        if (_itemSpellTimers.Count == 0)
        {
            _host.WriteToChat("[RynthAi] No item spell timers recorded yet.", 1);
            return;
        }
        _host.WriteToChat($"[RynthAi] -- Item spells ({_itemSpellTimers.Count}) --", 1);
        PrintItemSpellTimers();
    }

    private void PrintItemSpellTimers()
    {
        foreach (var kvp in _itemSpellTimers)
        {
            var info = kvp.Value;
            TimeSpan left  = info.ExpiresAt - DateTime.Now;
            TimeSpan spent = DateTime.Now - info.CastAt;
            double totalMin = (info.ExpiresAt - info.CastAt).TotalMinutes;
            if (left.TotalSeconds <= 0)
                _host.WriteToChat($"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): EXPIRED", 1);
            else
                _host.WriteToChat(
                    $"[RynthAi]   {info.SpellName} (Lvl {info.SpellLevel}): " +
                    $"{Math.Round(spent.TotalMinutes, 1)}m elapsed, {Math.Round(left.TotalMinutes, 1)}m left / {Math.Round(totalMin, 0)}m total", 1);
        }
    }

    private List<string> BuildDynamicBuffList()
    {
        var step1_CreatureMastery = new List<string>();
        var step2_Focus           = new List<string>();
        var step3_Willpower       = new List<string>();
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
            if (IsSkillUsable(kvp.Key))
            {
                if (kvp.Value.Contains("Creature Enchantment Mastery"))
                    step1_CreatureMastery.Add(kvp.Value);
                else if (!BaseCreatureBuffs.Contains(kvp.Value))
                    step4_OtherCreature.Add(kvp.Value);
            }
        }

        step5_LifeAndItem.AddRange(new List<string> {
            "Regeneration Self", "Rejuvenation Self", "Mana Renewal Self",
            "Armor Self", "Acid Protection Self", "Fire Protection Self",
            "Cold Protection Self", "Lightning Protection Self",
            "Blade Protection Self", "Piercing Protection Self",
            "Bludgeoning Protection Self"
        });

        step6_WeaponAuras.AddRange(new List<string> {
            "Blood Drinker Self", "Hermetic Link Self", "Heart Seeker Self",
            "Spirit Drinker Self", "Swift Killer Self", "Defender Self"
        });

        step7_ArmorBanes.AddRange(new List<string> {
            "Impenetrability", "Acid Bane", "Blade Bane", "Bludgeoning Bane",
            "Flame Bane", "Frost Bane", "Lightning Bane", "Piercing Bane"
        });

        var final = new List<string>();
        final.AddRange(step1_CreatureMastery);
        final.AddRange(step2_Focus);
        final.AddRange(step3_Willpower);
        final.AddRange(step4_OtherCreature);
        final.AddRange(step5_LifeAndItem);
        final.AddRange(step6_WeaponAuras);
        final.AddRange(step7_ArmorBanes);
        return final;
    }

    /// <summary>
    /// Called when the engine's enchantment hook fires for an enchantment applied to the player.
    /// Refreshes timers from live memory when possible; falls back to the hook-supplied duration.
    /// </summary>
    public void OnEnchantmentAdded(uint spellId, double durationSeconds)
    {
        var spellInfo = SpellTableStub.GetById((int)spellId);
        if (spellInfo == null)
            return;

        // Only clear pending if this enchantment matches what we were casting.
        // Armor/item enchantments fire on the item, not the player — clearing
        // _pendingSpellId unconditionally would lose track of pending item casts
        // and prevent the chat handler from recording their timers.
        if (_pendingSpellId != 0)
        {
            var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
            if (pendingSpell != null && pendingSpell.Family == spellInfo.Family)
                _pendingSpellId = 0;
        }

        // The family did land — clear any silent-no-show state (counter +
        // cooldown park) so future expiries trigger casts normally again.
        // Important for the /god case: if the user later learns the spell
        // the buff system should resume casting without a manual reset.
        // Also clears the chat-driven hard-reject cooldown if the family
        // somehow lands despite a recent reject (server-side fix etc.).
        if (_silentNoShowCounts.Remove(spellInfo.Family) ||
            _buffFailCooldownUntil.Remove(spellInfo.Family))
        {
            // No log spam in the common case — only the rare "park-then-recover" path.
        }

        // Prefer live memory read — gives accurate remaining time for all enchantments.
        // But only trust it if the just-added buff actually shows up there yet.
        if (RefreshFromLiveMemory() >= 0 &&
            _ramBuffTimers.TryGetValue(spellInfo.Family, out RamTimerInfo? liveTimer) &&
            liveTimer.Expiration > DateTime.Now)
        {
            return;
        }

        RecordSpellTimer(spellInfo, durationSeconds);
    }

    /// <summary>
    /// Called when the engine's enchantment hook fires for an enchantment removed from the player.
    /// Clears the buff timer for the corresponding spell family.
    /// </summary>
    public void OnEnchantmentRemoved(uint enchantmentId)
    {
        var spellInfo = SpellTableStub.GetById((int)enchantmentId);
        if (spellInfo == null) return;

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

        // Diagnostic: log every chat seen while waiting on a cast confirmation,
        // so we can see what AC is actually emitting and adjust our matchers.
        if (_pendingSpellId != 0)
            _host.Log($"[BuffChat] pending={_pendingSpellId} type={chatType} text='{text}'");

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
            if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null && IsItemEnchantment(pendingSpell.Name))
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
                _host.Log($"[BuffChat] PARKED pending={_pendingSpellId} — AC too busy (cast gesture in progress); throttle kept, re-issue when cast gate reopens.");
            }
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("too busy");
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
            if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null)
                {
                    if (IsItemEnchantment(pendingSpell.Name))
                    {
                        _itemSpellTimers.Remove(pendingSpell.Family);
                        SaveBuffTimers();
                    }
                    _buffFailCooldownUntil[pendingSpell.Family] =
                        DateTime.Now.AddSeconds(BuffFailCooldownSec);
                }
                _host.Log($"[BuffChat] CLEARED+COOLED pending={_pendingSpellId} ({BuffFailCooldownSec:0}s) — hard rejection in '{text}'");
            }
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("hard-reject");
            return;
        }

        // SOFT failure: random fizzle or transient (out of mana). Clear pending
        // and allow a prompt retry — do NOT cooldown the family.
        if (lower.Contains("fizzle") ||
            lower.Contains("your spell failed") ||
            lower.Contains("lack the mana"))
        {
            if (_pendingSpellId != 0)
            {
                var pendingSpell = SpellTableStub.GetById(_pendingSpellId);
                if (pendingSpell != null && IsItemEnchantment(pendingSpell.Name))
                {
                    _itemSpellTimers.Remove(pendingSpell.Family);
                    SaveBuffTimers();
                }
                _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} via soft-fail in '{text}'");
            }
            _lastCastAttempt = DateTime.MinValue;
            _pendingSpellId = 0;
            _onCastResolved?.Invoke("soft-fail");
            return;
        }

        // "You cast Incantation of Flame Bane on Gelidite Robe, refreshing ..."
        // "You cast Strength Self VIII"
        // The leading 'Y' is stripped by AC's chat-glyph layer before our handler
        // sees it — text actually arrives as "ou cast …". IndexOf works for both.
        int castIdx = lower.IndexOf("ou cast ", StringComparison.Ordinal);
        if (castIdx < 0) return;

        // Extract spell name: everything after "ou cast " up to " on " or ","
        string afterCast = text.Substring(castIdx + 8); // skip "ou cast "
        int onIdx = afterCast.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        int commaIdx = afterCast.IndexOf(',');
        int endIdx = afterCast.Length;
        if (onIdx > 0) endIdx = onIdx;
        if (commaIdx > 0 && commaIdx < endIdx) endIdx = commaIdx;
        string spellName = afterCast.Substring(0, endIdx).Trim();

        // Try to find the spell by name first (authoritative — this is what was actually cast)
        int spellId = SpellDatabase.GetIdByName(spellName);
        SpellInfo? spellInfo = spellId > 0 ? SpellTableStub.GetById(spellId) : null;

        // Fall back to pending spell if name lookup fails
        if (spellInfo == null && _pendingSpellId != 0)
            spellInfo = SpellTableStub.GetById(_pendingSpellId);

        if (spellInfo != null)
        {
            _buffFailCooldownUntil.Remove(spellInfo.Family); // it cast — clear any stale hard-fail cooldown
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
            if (_isForceRebuffing)
            {
                var ps = SpellTableStub.GetById(_pendingSpellId);
                if (ps != null) _forceRebuffCastFamilies.Add(ps.Family);
            }
            _host.Log($"[BuffChat] CLEARED pending={_pendingSpellId} via ou-cast match name='{spellName}' resolvedId={spellId}");
        }
        _pendingSpellId = 0;
        _onCastResolved?.Invoke($"cast '{spellName}'");
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
            Expiration = DateTime.Now.AddSeconds(durationSeconds),
            SpellLevel = level,
            SpellName = spellInfo.Name,
        };
        SaveBuffTimers();
    }

    // TODO: Replace with real skill training lookup from AC memory
    private bool IsSkillUsable(AcSkillType s) => _charSkills != null ? _charSkills[s].Training >= 2 : true;

    private static AcSkillType SkillForBuff(string name)
    {
        // Impregnability Self = Missile Defense (Creature Enchantment), NOT Item Enchantment.
        // Impenetrability / Bane / weapon auras = Item Enchantment.
        if (name.Contains("Blood Drinker") ||
            name.Contains("Hermetic Link") || name.Contains("Heart Seeker") ||
            name.Contains("Spirit Drinker") || name.Contains("Swift Killer") ||
            name.Contains("Defender") || name.Contains("Impenetrability") ||
            name.Contains("Bane"))
            return AcSkillType.ItemEnchantment;

        if (name.Contains("Protection") || name.Contains("Armor") ||
            name.Contains("Regeneration") || name.Contains("Rejuvenation") ||
            name.Contains("Renewal") || name == "Harlune's Blessing" ||
            name.Contains("Stamina to Mana") || name.Contains("Revitalize") ||
            name.Contains("Stamina to Health") || name == "Heal Self")
            return AcSkillType.LifeMagic;

        return AcSkillType.CreatureEnchantment;
    }

    private bool EnsureMagicMode()
    {
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
            return true;
        }

        int wandId = FindWandInItems();
        if (wandId == 0)
        {
            // No wand available — try to switch mode anyway (bare-handed magic).
            _host.ChangeCombatMode(CombatMode.Magic);
            _lastCastAttempt = DateTime.Now;
            return false;
        }

        // Primary check: CurrentWieldedLocation (stype=10) — works even before the
        // phys-obj offset probe fires, mirrors CombatManager.EquipWeaponAndSetStance.
        bool alreadyWielded = false;
        var wandObj = _worldObjectCache?[wandId];
        if (wandObj != null)
            alreadyWielded = wandObj.Values(LongValueKey.CurrentWieldedLocation, 0) > 0;

        // Secondary check via API if primary didn't confirm
        if (!alreadyWielded && _host.HasGetObjectWielderInfo)
        {
            uint playerId = _host.GetPlayerId();
            if (playerId != 0 &&
                _host.TryGetObjectWielderInfo((uint)wandId, out uint wielder, out _) &&
                wielder == playerId)
                alreadyWielded = true;
        }

        if (!alreadyWielded)
        {
            // Wield gate: don't spam UseObject. Hold off after the first issue
            // until either the wielded check confirms (cleared on the Magic-mode
            // branch above) or the resolve timeout elapses, then cool down
            // before retrying. Mirrors the no-chat-timeout pattern used for
            // spell casts at the top of OnHeartbeat. Checked BEFORE the shared
            // swap gate so a denied attempt never claims the cross-subsystem slot.
            DateTime now = DateTime.Now;
            if (now < _wieldCooldownUntil)
                return false;

            if (_pendingWieldId != 0)
            {
                if ((now - _pendingWieldAt).TotalMilliseconds < WieldResolveTimeoutMs)
                    return false;

                _host.Log($"[WieldGate] UseObject(0x{(uint)_pendingWieldId:X8}) not confirmed in " +
                          $"{WieldResolveTimeoutMs:0}ms — cooling down {WieldCooldownMs:0}ms");
                _pendingWieldId = 0;
                _wieldCooldownUntil = now.AddMilliseconds(WieldCooldownMs);
                return false;
            }

            // Shared weapon-swap gate: if CombatManager (or anything else) swapped
            // a weapon within the last few seconds, wait — two equips in flight
            // collide ("you can only move or use one item at a time" / AV).
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("buff-wand"))
                return false;

            // Tear down any in-flight physical combat BEFORE the UseObject. The
            // transition Melee/Missile → Magic-via-wand must explicitly cancel the
            // pending attack, or AC gets a UseObject while m_bAttacking is still
            // set and wedges the item-action gate. Done only after we've claimed
            // the swap slot above, so we don't cancel an attack then fail to swap.
            if (!_combatTeardownDoneForCurrentBuffCycle &&
                (CurrentCombatMode == CombatMode.Melee || CurrentCombatMode == CombatMode.Missile))
            {
                if (_host.HasCancelAttack)   _host.CancelAttack();
                if (_host.HasStopCompletely) _host.StopCompletely();
                _combatTeardownDoneForCurrentBuffCycle = true;
                _host.Log($"[BuffPre] CancelAttack+StopCompletely before wand equip (mode was {CurrentCombatMode})");
            }

            _host.UseObject((uint)wandId);
            _pendingWieldId = wandId;
            _pendingWieldAt = now;
            _lastCastAttempt = now;
            return false; // Yield — let the server equip the wand
        }

        // Wand is wielded — clear the gate and switch stance.
        if (_pendingWieldId == wandId)
        {
            _pendingWieldId = 0;
            _wieldCooldownUntil = DateTime.MinValue;
        }
        // Exponential backoff: 1s → 2s → 4s → 8s → 16s → 30s cap. Counter
        // increments only when this branch fires AND the previous flip didn't
        // stick (we got back here with mode != Magic). Reset to 0 at the top
        // of EnsureMagicMode when mode == Magic. See field comment.
        int shift = _buffStanceConsecutiveFails;
        if (shift > 5) shift = 5;
        int gateMs = 1000 << shift;        // 1s, 2s, 4s, 8s, 16s, 32s
        if (gateMs > 30000) gateMs = 30000; // cap at 30s
        if ((DateTime.Now - _lastBuffStanceAttempt).TotalMilliseconds > gateMs)
        {
            _host.ChangeCombatMode(CombatMode.Magic);
            _lastBuffStanceAttempt = DateTime.Now;
            _lastCastAttempt = DateTime.Now;
            if (_buffStanceConsecutiveFails < 6) _buffStanceConsecutiveFails++;
        }
        return false; // Yield — let stance animation finish
    }

    /// <summary>
    /// Locates a wand to equip. Prefers a configured ItemRule classified as WandStaffOrb,
    /// falling back to the first WandStaffOrb found in the inventory cache.
    /// Mirrors CombatManager.FindWandInItems.
    /// </summary>
    private int FindWandInItems()
    {
        if (_worldObjectCache == null) return 0;

        // Prefer explicitly configured wand from item rules
        foreach (var rule in _settings.ItemRules)
        {
            var wo = _worldObjectCache[rule.Id];
            if (wo != null && IsWandObject(wo)) return rule.Id;
        }

        // Inventory cache scan — ObjectClass first, name fallback for unclassified items
        foreach (var wo in _worldObjectCache.GetInventory())
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
}

using System;
using System.Collections.Generic;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Combat-pet summoning state machine.
///
/// AC/ACE combat pets are summoned from "essence" items (PetDevices). Using an
/// essence with no target spawns a pet that fights alongside the player for a
/// short, data-driven lifespan, then despawns. Each successful use consumes one
/// charge (the item's <c>Structure</c> property); when a device runs dry it is
/// refilled by using an "Encapsulated Spirit" on it (which restores it to
/// MaxStructure and consumes one spirit). Only ONE pet may be active at a time —
/// the server names the summoned creature "<c>&lt;PlayerName&gt;'s &lt;PetName&gt;</c>",
/// which is how we detect whether a pet is already up.
///
/// Behaviour (per user spec):
///   * Summon only when mobs are near — at least <c>PetMinMonsters</c> scanned,
///     attackable monsters within <c>CustomPetRange</c> — and no pet is currently
///     active. Re-summon when the pet despawns mid-fight (lifespan / death), after
///     the essence's retail ~45 s use cooldown (Summoning wiki / ACE PetDevice).
///   * Auto-refill an empty essence from an Encapsulated Spirit in inventory
///     (gated on <c>PetAutoRefill</c>).
///
/// Mirrors <see cref="ManaStoneManager"/>: busy-gated, chat-driven, timeout-
/// guarded, one in-flight action at a time. Entirely plugin-side; the only AC
/// actions issued are <c>Host.UseObject</c> (summon) and <c>Host.UseObjectOn</c>
/// (refill), both selection-free.
///
/// Stuck/fail (Kelpie 2026-10-16): a Use that the server refused (cooldown, incline,
/// busy) never drops Structure and never spawns a pet. The old path still assumed a
/// pet for 10 s and parked the essence only 30 s — short of the 45 s item cooldown —
/// so the next try timed out again. Failed summons no longer assume a pet; every
/// Use parks the essence for <see cref="DeviceUseCooldownMs"/>.
/// </summary>
internal sealed class PetManager
{
    private readonly RynthCoreHost    _host;
    private readonly LegacyUiSettings _settings;
    private readonly WorldObjectCache _objectCache;
    private readonly CombatManager?   _combat;
    private readonly CharacterSkills? _skills;

    private enum PetState { Idle, Summoning, Refilling }

    private PetState _state = PetState.Idle;
    private int  _activeDeviceId;
    private string _summonElement = "";   // element of the essence last summoned from
    private long   _petOutCheckedAt;      // CurrentSummon's landscape scan, at most once a second
    private bool   _petOutCached;
    private int  _activeSpiritId;
    private int  _preActionCharges = -1;  // device charges read just before issuing a summon (-1 = unreadable)
    private long _actionIssuedAt;
    private long _lastThinkAt;

    // After issuing a summon (or learning a pet is already up via chat) assume a
    // pet is active for at least this long, so we don't re-issue before the new
    // pet's CreateObject lands in the cache. Name-detection takes over once it does.
    private long _assumePetActiveUntil;
    private bool _petSeenSinceSummon;   // the pet has shown up in the cache during this assume window

    // Devices that just failed (skill too low, timed out, empty with no spirit)
    // get parked so we advance to the next configured essence instead of looping.
    private readonly Dictionary<int, long> _deviceCooldownUntil = new();

    // The server's word over a stale Structure read (2026-10-05). A read
    // can say 0 while the server holds a full essence; the server then answers a refill with
    // "already full" (or refills it and the read still lags), and deciding from the read alone
    // re-issued the refill every think tick: peace mode and a clap each time, a spirit each time
    // the refill was real. Essence -> until when its read of 0 is not believed; cleared when a
    // summon reports "not enough charges".
    private readonly Dictionary<int, long> _serverSaidFullUntil = new();
    // Refills issued per essence: how many since the window opened, and when it opened.
    private readonly Dictionary<int, (int Count, long Since)> _refills = new();

    // Encapsulated Spirit — the fixed retail recharge item (WCID 49485). Detected
    // by name; it is never itself a summon device.
    private const string EncapsulatedSpiritName = "Encapsulated Spirit";

    private const long ThinkIntervalMs           = 1000;
    private const long ActionTimeoutMs           = 8000;
    /// <summary>Hold after a confirmed summon until the pet lands in the landscape cache (kept at the base's 10 s).</summary>
    private const long AssumeActiveAfterSummonMs = 10_000;
    /// <summary>
    /// Retail / ACE PetDevice use cooldown (~45 s). Applies after every Use we fire — success
    /// or fail — so we do not re-Use while the server still refuses the essence.
    /// </summary>
    private const long DeviceUseCooldownMs        = 45_000;
    private const long DeviceFailCooldownMs      = 30_000;   // a refill that timed out (a summon is parked from IssueSummon)
    private const long DeviceNoSpiritCooldownMs  = 15_000;
    private const long ServerSaidFullTrustMs     = 30 * 60_000;
    private const long RefillWindowMs            = 120_000;  // two refills inside this and the
    private const int  RefillsPerWindow          = 2;        // essence still reads empty:
    private const long RefillLoopParkMs          = 300_000;  // park it this long

    private string _playerName = string.Empty;
    private long   _playerNameAt;

    /// <summary>Clock (ms); tests replace it.</summary>
    internal static Func<long> Clock { get; set; } = static () => Environment.TickCount64;
    private static long NowMs => Clock();

    /// <summary>
    /// Optional Mini Remote Pets-tab hook: when it returns true an EMPTY essence is used anyway, because
    /// the server's Summon Essence Refill Charm (ACECustom) refills it from banked pyreals on use.
    /// Null (default) keeps the retail behaviour (spirit refill or park).
    /// </summary>
    public Func<bool>? AllowSummonOnEmpty { get; set; }

    /// <summary>
    /// Optional Pets-tab hook: while it returns true no combat summon is started (e.g. the
    /// healing-pet helper has its heal pet out). Null (default) never holds.
    /// </summary>
    public Func<bool>? HoldSummons { get; set; }

    /// <summary>
    /// Optional Pets-tab hook, called when a pet already holds the slot and monsters are near.
    /// Returns true after dismissing a non-combat (cosmetic) pet so a combat pet can follow.
    /// Null (default) leaves whatever pet is out alone.
    /// </summary>
    public Func<bool>? YieldPetForCombat { get; set; }

    public PetManager(RynthCoreHost host, LegacyUiSettings settings,
                      WorldObjectCache objectCache, CombatManager? combat,
                      CharacterSkills? skills)
    {
        _host        = host;
        _settings    = settings;
        _objectCache = objectCache;
        _combat      = combat;
        _skills      = skills;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public void OnHeartbeat(int busyCount)
    {
        if (!_settings.SummonPets)     { Reset(); return; }
        if (!_settings.IsMacroRunning) { Reset(); return; }

        long now = NowMs;

        // Positive completion checks for the in-flight action (don't wait for chat).
        if (_state == PetState.Summoning)
        {
            // Success once the server decremented the device's charge (Structure)
            // or the summoned creature is now visible in the cache.
            if (SummonLanded() || IsPetActive())
            {
                _summonElement = PetChoice.ElementOf(_host, unchecked((uint)_activeDeviceId),
                    _objectCache[_activeDeviceId]?.Name ?? "");
                AssumePetFor(now, AssumeActiveAfterSummonMs);
                GoIdle();
            }
        }
        else if (_state == PetState.Refilling)
        {
            bool spiritGone = _activeSpiritId != 0 && _objectCache[_activeSpiritId] == null;
            if (spiritGone || DeviceHasCharges(_activeDeviceId))
            {
                RynthLog.Write(LogCat.Pets, $"[RynthAi] Pet: refill complete (essence 0x{(uint)_activeDeviceId:X8}).");
                GoIdle();
            }
        }

        // Timeout BEFORE the busy gate — busyCount can stick > 0 (a server-side
        // decrement that never lands), and waiting on it would wedge the subsystem.
        if (_state != PetState.Idle && now - _actionIssuedAt > ActionTimeoutMs)
        {
            bool summonConfirmed = _state == PetState.Summoning
                && (SummonLanded() || IsPetActive());
            // Host.Log so FakeHost / in-game logs see it; RynthLog for the Pets category file.
            string timeoutMsg =
                $"[RynthAi] Pet: action timeout in {_state} after {now - _actionIssuedAt}ms (essence 0x{(uint)_activeDeviceId:X8})"
                + (summonConfirmed
                    ? "; summon confirmed late."
                    : "; no charge drop / no pet — treating as failed Use.");
            _host.Log(timeoutMsg);
            RynthLog.Write(LogCat.Pets, timeoutMsg);
            // Do NOT restart DeviceUseCooldownMs here — IssueSummon already parked the essence
            // from the Use instant. Re-arming from timeout would wait ~53 s and leave gaps
            // after a refused Use (Kelpie stuck/fail).
            // Only assume a pet when the Use clearly landed; a failed Use must not block
            // resummon after the cooldown (false assume + short park left gaps).
            if (_state != PetState.Summoning && _activeDeviceId != 0)
                _deviceCooldownUntil[_activeDeviceId] = now + DeviceFailCooldownMs;   // refill timeout: park as before
            if (summonConfirmed)
                AssumePetFor(now, AssumeActiveAfterSummonMs);
            else if (_state == PetState.Summoning)
                _assumePetActiveUntil = 0;
            Reset();
            return;
        }

        if (busyCount > 0) return;
        if (now - _lastThinkAt < ThinkIntervalMs) return;
        _lastThinkAt = now;

        if (_state == PetState.Idle)
            TryBeginAction(now);
    }

    public void OnChatWindowText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (_state == PetState.Summoning)
        {
            // "<pet> is already active" — a pet is up but our detection lagged. Back off.
            if (text.Contains("is already active", StringComparison.OrdinalIgnoreCase))
            {
                AssumePetFor(NowMs, AssumeActiveAfterSummonMs);
                GoIdle();
                return;
            }
            // "... does not have enough charges to function!" — essence is empty.
            // Drop to Idle; the next think tick reads Structure==0 and refills it.
            if (text.Contains("enough charges", StringComparison.OrdinalIgnoreCase))
            {
                _host.Log($"[RynthAi] Pet: essence 0x{(uint)_activeDeviceId:X8} reports empty; will refill.");
                _serverSaidFullUntil.Remove(_activeDeviceId);   // really empty: the read is right again
                // Empty Use does not burn the retail 45 s spawn cooldown the way a real summon
                // does; drop the Use park so the next think can refill (or re-try pyreal path).
                if (_activeDeviceId != 0)
                {
                    if (AllowSummonOnEmpty?.Invoke() == true)
                        _deviceCooldownUntil[_activeDeviceId] = NowMs + DeviceNoSpiritCooldownMs;
                    else
                        _deviceCooldownUntil.Remove(_activeDeviceId);
                }
                GoIdle();
                return;
            }
            return;
        }

        if (_state == PetState.Refilling)
        {
            // "You add the spirit to the essence." (success) / "This essence is
            // already full." (our Structure read was stale — also done). Either way the
            // essence is full now, whatever its read says until the next update.
            bool added = text.Contains("add the spirit to the essence", StringComparison.OrdinalIgnoreCase);
            bool full  = !added && text.Contains("essence is already full", StringComparison.OrdinalIgnoreCase);
            if (added || full)
            {
                OnServerSaidFull(_activeDeviceId, added);
                GoIdle();
            }
        }
    }

    // ── Decision ───────────────────────────────────────────────────────────────

    /// <summary>Treat a pet as out until <paramref name="now"/> + <paramref name="ms"/> (it may not be in the cache yet).</summary>
    private void AssumePetFor(long now, long ms)
    {
        _assumePetActiveUntil = now + ms;
        _petSeenSinceSummon = false;
    }

    private void TryBeginAction(long now)
    {
        // A pet is already up (just summoned, or detected in the world) — done.
        // If the assume window is still open but the pet is gone (despawn / death), clear it
        // so we can wait on the essence cooldown and resummon.
        bool petOut = IsPetActive();
        if (now < _assumePetActiveUntil)
        {
            if (petOut) { _petSeenSinceSummon = true; return; }
            // The pet came and went (died / lifespan ended): don't wait out the window. A pet that has not
            // landed in the cache yet keeps the window, or a second essence would be summoned beside it.
            if (!_petSeenSinceSummon) return;
            _assumePetActiveUntil = 0;
        }
        if (petOut)
        {
            // A cosmetic pet in the slot steps aside for the fight; wait for the dismiss to land.
            if (YieldPetForCombat != null && MonstersNearby() && YieldPetForCombat())
                AssumePetFor(now, 3000);
            return;
        }
        if (HoldSummons?.Invoke() == true) return;

        // Only summon when mobs are near (the user's trigger).
        if (!MonstersNearby()) return;

        // Skill gate: must have Summoning trained to use any essence.
        if (!IsSummoningTrained()) return;

        if (!_host.HasUseObject) return;

        // Best essence for the monster being fought first (Damage panel pet choice:
        // a specific essence, an element, or Auto = lowest learned resist), then the
        // rest in Items-panel order as fallbacks.
        foreach (int deviceId in OrderDevicesForTarget(EnumeratePetDevices()))
        {
            if (_deviceCooldownUntil.TryGetValue(deviceId, out long until) && now < until)
                continue;

            int charges = ReadCharges(deviceId); // -1 = unreadable

            // The server called this essence full after the read last changed: the 0 is stale.
            // Summon as for an unreadable one (an empty essence is refused harmlessly, and that
            // "not enough charges" ends the trust).
            if (charges == 0 && ServerSaidFull(deviceId, now))
                charges = -1;

            if (charges != 0)
            {
                // Has charges (or unreadable → attempt; an empty one is rejected
                // harmlessly with no charge cost and we'll refill on the chat).
                IssueSummon(deviceId, charges);
                return;
            }

            // charges == 0 → empty. ILT shards with an active pyreal-refill charm refill
            // the essence server-side on use, so summon straight away.
            if (AllowSummonOnEmpty?.Invoke() == true)
            {
                IssueSummon(deviceId, charges);
                return;
            }

            // Otherwise auto-refill from an Encapsulated Spirit.
            if (_settings.PetAutoRefill)
            {
                int spiritId = FindEncapsulatedSpirit();
                if (spiritId != 0 && !RefillLooping(deviceId, now))
                {
                    IssueRefill(deviceId, spiritId);
                    return;
                }
                if (spiritId != 0)
                    continue;   // parked by RefillLooping; try the next essence
            }

            // Out of charges and no spirit (or refill off): park this essence
            // briefly and move on to the next configured one.
            _deviceCooldownUntil[deviceId] = now + DeviceNoSpiritCooldownMs;
        }
    }

    private void IssueSummon(int deviceId, int charges)
    {
        // Don't fire the essence-use while a cast gesture is animating — AC
        // refuses item use mid-gesture, which would burn the 8s timeout and the
        // 45 s essence cooldown for nothing. No state change; caller retries next tick.
        if (!_host.CanCastNow) return;
        long now = NowMs;
        _state            = PetState.Summoning;
        _activeDeviceId   = deviceId;
        _activeSpiritId   = 0;
        _preActionCharges = charges;
        _actionIssuedAt   = now;
        // Server starts the PetDevice cooldown on Use, whether the pet spawns or not.
        _deviceCooldownUntil[deviceId] = now + DeviceUseCooldownMs;
        RynthLog.Write(LogCat.Pets, $"[RynthAi] Pet: summoning from essence 0x{(uint)deviceId:X8} (charges={charges}); essence cooldown {DeviceUseCooldownMs / 1000}s.");
        _host.UseFor(unchecked((uint)deviceId), "Pet", "summon a pet from the essence");
    }

    private void IssueRefill(int deviceId, int spiritId)
    {
        if (!_host.HasUseObjectOn) return;
        if (!_host.CanCastNow) return; // same gesture gate as IssueSummon
        _state          = PetState.Refilling;
        _activeDeviceId = deviceId;
        _activeSpiritId = spiritId;
        _actionIssuedAt = NowMs;
        CountRefill(deviceId, _actionIssuedAt);
        _host.Log($"[RynthAi] Pet: refilling essence 0x{(uint)deviceId:X8} with spirit 0x{(uint)spiritId:X8}.");
        _host.UseOnFor(unchecked((uint)spiritId), unchecked((uint)deviceId), "Pet", "refill the essence with a spirit");
    }

    /// <summary>
    /// The server said the essence is full ("You add the spirit..." / "...already full"): its
    /// read of 0 is not believed for a while, and it is re-identified so the read catches up.
    /// </summary>
    private void OnServerSaidFull(int deviceId, bool refilled)
    {
        if (deviceId == 0) return;
        _serverSaidFullUntil[deviceId] = NowMs + ServerSaidFullTrustMs;
        if (_host.HasRequestId) _host.RequestId(unchecked((uint)deviceId));
        _host.Log(refilled
            ? $"[RynthAi] Pet: refill complete (essence 0x{(uint)deviceId:X8}, server confirmed)."
            : $"[RynthAi] Pet: essence 0x{(uint)deviceId:X8} is already full (its charge read was stale); summoning from it.");
    }

    private bool ServerSaidFull(int deviceId, long now)
    {
        if (!_serverSaidFullUntil.TryGetValue(deviceId, out long until)) return false;
        if (now < until) return true;
        _serverSaidFullUntil.Remove(deviceId);
        return false;
    }

    private void CountRefill(int deviceId, long now)
    {
        if (_refills.TryGetValue(deviceId, out var r) && now - r.Since < RefillWindowMs)
            _refills[deviceId] = (r.Count + 1, r.Since);
        else
            _refills[deviceId] = (1, now);
    }

    /// <summary>
    /// True, and the essence parked, when it was refilled <see cref="RefillsPerWindow"/> times
    /// inside <see cref="RefillWindowMs"/> and still reads empty: another refill would only cost
    /// a spirit and force peace mode again. An essence holds dozens of summons, so two real
    /// refills that close together don't happen.
    /// </summary>
    private bool RefillLooping(int deviceId, long now)
    {
        if (!_refills.TryGetValue(deviceId, out var r) || now - r.Since >= RefillWindowMs || r.Count < RefillsPerWindow)
            return false;
        _refills.Remove(deviceId);
        _deviceCooldownUntil[deviceId] = now + RefillLoopParkMs;
        _host.Log($"[RynthAi] Pet: essence 0x{(uint)deviceId:X8} was refilled {r.Count}x in {(now - r.Since) / 1000}s and still reads empty; parking it for {RefillLoopParkMs / 60_000} min.");
        return true;
    }

    private void GoIdle()
    {
        _state            = PetState.Idle;
        _actionIssuedAt   = 0;
        _preActionCharges = -1;
    }

    private void Reset()
    {
        _state            = PetState.Idle;
        _activeDeviceId   = 0;
        _activeSpiritId   = 0;
        _preActionCharges = -1;
        _actionIssuedAt   = 0;
    }

    // ── Signals ──────────────────────────────────────────────────────────────────

    /// <summary>True once a successful summon has decremented the device's charges.</summary>
    private bool SummonLanded()
    {
        if (_activeDeviceId == 0 || _preActionCharges < 0) return false;
        int cur = ReadCharges(_activeDeviceId);
        return cur >= 0 && cur < _preActionCharges;
    }

    private bool DeviceHasCharges(int deviceId) => ReadCharges(deviceId) > 0;

    /// <summary>Device remaining charges (Structure). -1 if unreadable.</summary>
    private int ReadCharges(int deviceId)
    {
        if (!_host.HasGetObjectIntProperty) return -1;
        return _host.TryGetObjectIntProperty(unchecked((uint)deviceId),
                   (uint)AcIntProperty.Structure, out int s) ? s : -1;
    }

    /// <summary>At least PetMinMonsters attackable monsters within CustomPetRange.</summary>
    private bool MonstersNearby()
    {
        if (_combat == null) return false;
        int    need  = Math.Max(1, _settings.PetMinMonsters);
        double range = Math.Max(1, _settings.CustomPetRange);
        int count = 0;
        var scanned = _combat.ScannedTargets;
        for (int i = 0; i < scanned.Count; i++)
        {
            if (scanned[i].Distance <= range && ++count >= need)
                return true;
        }
        return false;
    }

    private bool IsSummoningTrained()
    {
        if (_skills == null) return true; // can't read skills → don't block
        // Training: 0=UNDEF, 1=UNTRAINED, 2=TRAINED, 3=SPECIALIZED.
        return _skills[AcSkillType.Summoning].Training >= 2;
    }

    /// <summary>
    /// A combat pet is up if a live creature named "&lt;PlayerName&gt;'s …" exists in
    /// the cache — the server names summoned pets exactly that (Pet.Init).
    /// </summary>
    /// <summary>
    /// The summon out now, for the Damage tab's summon stats: null = none, else the element of
    /// the essence it came from ("" when that isn't known, e.g. summoned by hand).
    /// </summary>
    internal string? CurrentSummon()
    {
        long now = NowMs;
        if (now - _petOutCheckedAt > 1000)
        {
            _petOutCheckedAt = now;
            _petOutCached = IsPetActive();
        }
        return _petOutCached ? _summonElement : null;
    }

    private bool IsPetActive()
    {
        string me = PlayerName();
        if (me.Length == 0) return false;
        string prefix = me + "'s ";
        foreach (var wo in _objectCache.GetLandscape())
        {
            if (wo == null) continue;
            // Corpses keep the pet's name briefly; don't treat them as a live summon.
            if (wo.ObjectClass == AcObjectClass.Corpse) continue;
            string n = wo.Name;
            if (string.IsNullOrEmpty(n) || !n.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (n.Contains("Corpse", StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }
        return false;
    }

    private string PlayerName()
    {
        long now = NowMs;
        if (_playerName.Length > 0 && now - _playerNameAt < 30_000) return _playerName;
        uint pid = _host.GetPlayerId();
        if (pid != 0 && _host.TryGetObjectName(pid, out string n) && !string.IsNullOrEmpty(n))
        {
            _playerName   = n;
            _playerNameAt = now;
        }
        return _playerName;
    }

    // ── Inventory lookups ─────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve each configured Type=="Pet" rule to a concrete in-inventory device
    /// id: prefer the exact instance the user added, else any same-named essence
    /// (handles a re-looted/replaced essence after the original was consumed).
    /// </summary>
    private IEnumerable<int> EnumeratePetDevices()
    {
        int playerId = unchecked((int)_host.GetPlayerId());
        foreach (var rule in _settings.ConsumableRules)
        {
            if (!rule.Type.Equals("Pet", StringComparison.OrdinalIgnoreCase)) continue;

            var byId = _objectCache[rule.Id];
            if (byId != null && IsOwnedByPlayer(byId, playerId)) { yield return rule.Id; continue; }

            if (string.IsNullOrWhiteSpace(rule.Name)) continue;
            foreach (var wo in _objectCache.AllKnownObjects())
            {
                if (wo == null) continue;
                if (wo.Name.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)
                    && IsOwnedByPlayer(wo, playerId))
                {
                    yield return wo.Id;
                    break;
                }
            }
        }
    }

    private int _lastChoiceTarget;
    private string _lastChoiceReason = "";

    private List<int> OrderDevicesForTarget(IEnumerable<int> ids)
    {
        var raw = new List<(int Id, string Name)>();
        var seen = new HashSet<int>();
        foreach (int id in ids)
            if (seen.Add(id)) raw.Add((id, _objectCache[id]?.Name ?? ""));
        if (raw.Count <= 1 || _combat == null) return raw.ConvertAll(r => r.Id);

        int target = _combat.activeTargetId;
        if (target == 0)
        {
            double best = double.MaxValue;
            foreach (var t in _combat.ScannedTargets)
                if (t.Distance < best) { best = t.Distance; target = t.Id; }
        }
        if (target == 0) return raw.ConvertAll(r => r.Id);

        uint wcid = 0;
        if (_host.HasGetObjectWcid) _host.TryGetObjectWcid(unchecked((uint)target), out wcid);
        string tname = _objectCache[target]?.Name ?? "";
        string choice = wcid != 0 ? _combat.DamageStore?.GetManualPet(wcid) ?? "" : "";

        // Monsters-tab rule for this monster: its pet damage (PetDamage) wins over its
        // weapon damage type. Same name match combat uses.
        var rule = _settings.MonsterRules.Find(r => r.Name.Length > 0 && !r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                                                   && tname.IndexOf(r.Name, StringComparison.OrdinalIgnoreCase) >= 0);
        // No rule of its own, or one left on Auto: the Default row's pet damage, when it names one.
        var defaultRow = _settings.MonsterRules.Find(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        if ((rule == null || (PetChoice.NormalizeElement(rule.PetDamage).Length == 0 && PetChoice.NormalizeElement(rule.DamageType).Length == 0))
            && defaultRow != null && PetChoice.NormalizeElement(defaultRow.PetDamage).Length > 0)
            rule = defaultRow;
        string ruleElem = rule == null ? "" :
            (PetChoice.NormalizeElement(rule.PetDamage).Length > 0 ? rule.PetDamage : rule.DamageType);
        var weak = _combat.WeaknessFor(wcid, tname, target);
        var ordered = PetChoice.Order(_host, raw, choice, weak, out string why, ruleElem);
        if (target != _lastChoiceTarget || why != _lastChoiceReason)
        {
            _lastChoiceTarget = target;
            _lastChoiceReason = why;
            _host.Log($"[RynthAi] Pet: for {tname}: {why} -> {(ordered.Count > 0 ? ordered[0].Name : "none")}");
        }
        return ordered.ConvertAll(d => d.Id);
    }

    private int FindEncapsulatedSpirit()
    {
        int playerId = unchecked((int)_host.GetPlayerId());
        foreach (var wo in _objectCache.AllKnownObjects())
        {
            if (wo == null) continue;
            if (!IsOwnedByPlayer(wo, playerId)) continue;
            if (!string.IsNullOrEmpty(wo.Name)
                && wo.Name.Contains(EncapsulatedSpiritName, StringComparison.OrdinalIgnoreCase))
                return wo.Id;
        }
        return 0;
    }

    /// <summary>True if the item's container chain roots at the player's pack (not a corpse).</summary>
    private bool IsOwnedByPlayer(WorldObject item, int playerId)
    {
        if (item.ObjectClass == AcObjectClass.Corpse) return false;
        if (item.Wielder != 0 && playerId != 0 && item.Wielder != playerId) return false;
        if (item.Container != 0 && playerId != 0 && item.Container != playerId)
        {
            var owner = _objectCache[item.Container];
            if (owner == null || owner.ObjectClass == AcObjectClass.Corpse) return false;
            if (owner.Wielder != playerId && owner.Container != playerId) return false;
        }
        return true;
    }
}

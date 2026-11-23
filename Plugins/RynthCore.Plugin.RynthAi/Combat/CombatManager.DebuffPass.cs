// ============================================================================
//  RynthSuite - Combat/CombatManager.DebuffPass.cs
//  "Debuff every monster in range before attacking" (2026-11-22, setting
//  DebuffAllBeforeAttacking, off by default; /ra debuffall [on|off]). With
//  several monsters in Monster Range, each one's own debuffs (its Monsters
//  rule: Imperil, vulns, Void debuffs, typed-in ones) are cast on all of them,
//  nearest first, before anything is attacked, so pets and the attack spells
//  that follow land on debuffed monsters. The order and the caps are
//  DebuffPass's (pure, tested); this file feeds it the scan, casts what it
//  picks and holds the pet summon while the pass runs.
//
//  Debuffs go out through the same cast path as the fight target's own: one
//  at a time, its result read from chat (landed, resisted, fizzled, refused).
//  Melee and missile with a weapon in hand cast no debuffs at all (the wand
//  swap doesn't work there), so the pass doesn't run in those modes.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

public partial class CombatManager
{
    private DebuffPass? _passState;
    private readonly List<int> _passCandidates = new();
    private readonly List<int> _passInRange = new();
    private bool _pendingDebuffFromPass;
    private bool _petHoldLogged;

    // A monster's melee hits on the character, by name (the hit lines name it, not its object).
    private readonly Dictionary<string, DateTime> _hitOnMeAt = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>A monster this close that hit the character this recently is "on you".</summary>
    internal const double OnYouYards = 6.0;
    internal const double OnYouSeconds = 4.0;

    /// <summary>The pass's state (tests read it).</summary>
    internal DebuffPass DebuffPassState => _passState ??= new DebuffPass
    {
        Log = s => _host.Log(s),
        NameOf = id => _worldFilter[id]?.Name ?? "",
    };

    /// <summary>
    /// The pet summon waits while the first pass runs (PetManager), so the pet's first hits land on
    /// debuffed monsters. Bounded by the pass's time cap; never while a monster is on you.
    /// </summary>
    internal bool DebuffPassHoldsPets
    {
        get
        {
            if (!_settings.DebuffAllBeforeAttacking || !_settings.EnableCombat || _passState == null) return false;
            bool hold = _passState.Holding(RynthClock.Now);
            // Before the fight's first cast: only when the pass can run (no melee or missile weapon
            // out, nothing hitting you) and some monster in range has a debuff to get.
            if (hold && _passState.Phase == DebuffPass.Stage.Idle)
            {
                int mode = CurrentCombatMode;
                hold = mode != CombatMode.Melee && mode != CombatMode.Missile && !MonsterOnYou() && AnyInRangeNeedsDebuff();
            }
            if (hold && !_petHoldLogged) _host.Log("[DebuffPass] the pet summon waits for the debuff pass");
            _petHoldLogged = hold;
            return hold;
        }
    }

    private bool AnyInRangeNeedsDebuff()
    {
        FillPassCandidates();
        for (int i = 0; i < _passCandidates.Count; i++)
            if (PassDebuffFor(_passCandidates[i], out _, out _, out _, out _)) return true;
        return false;
    }

    private void FillPassCandidates()
    {
        _passCandidates.Clear();
        double range = Math.Max(1, _settings.MonsterRange);
        for (int i = 0; i < _scannedTargets.Count; i++)   // sorted nearest first
            if (_scannedTargets[i].Distance <= range) _passCandidates.Add(_scannedTargets[i].Id);
    }

    /// <summary>AttackWithMagic, before the fight target's own debuffs: the fight's first cast begins the pass.</summary>
    private void BeginDebuffPass()
    {
        if (!_settings.DebuffAllBeforeAttacking || _spellManager == null) return;
        if (_passState != null && _passState.Phase != DebuffPass.Stage.Idle) return;
        FillPassCandidates();
        DebuffPassState.Begin(_passCandidates, RynthClock.Now);
    }

    /// <summary>ScanNearbyTargets, after the sort: the pass sees who is in range.</summary>
    private void ObserveDebuffPass()
    {
        if (!_settings.DebuffAllBeforeAttacking)
        {
            if (_passState != null && _passState.Phase != DebuffPass.Stage.Idle) _passState.Reset();
            return;
        }
        _passInRange.Clear();
        double range = Math.Max(1, _settings.MonsterRange);
        for (int i = 0; i < _scannedTargets.Count; i++)
            if (_scannedTargets[i].Distance <= range) _passInRange.Add(_scannedTargets[i].Id);
        DebuffPassState.Observe(_passInRange, activeTargetId != 0, RynthClock.Now);
    }

    /// <summary>LearnFromCombatChat: a monster's hit on the character.</summary>
    private void NoteHitOnMe(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (_hitOnMeAt.Count >= 64 && !_hitOnMeAt.ContainsKey(name)) _hitOnMeAt.Clear();
        _hitOnMeAt[name] = RynthClock.Now;
    }

    /// <summary>
    /// A monster is hitting the character in melee: one within <see cref="OnYouYards"/> whose name
    /// hit us in the last <see cref="OnYouSeconds"/>.
    /// </summary>
    internal bool MonsterOnYou()
    {
        if (_hitOnMeAt.Count == 0) return false;
        DateTime now = RynthClock.Now;
        for (int i = 0; i < _scannedTargets.Count; i++)
        {
            var t = _scannedTargets[i];
            if (t.Distance > OnYouYards) continue;
            string name = t.Name ?? _worldFilter[t.Id]?.Name ?? "";
            if (name.Length > 0 && _hitOnMeAt.TryGetValue(name, out DateTime at) && (now - at).TotalSeconds <= OnYouSeconds)
                return true;
        }
        return false;
    }

    /// <summary>Tests: a hit line from <paramref name="name"/> just now.</summary>
    internal void NoteHitOnMeForTests(string name) => NoteHitOnMe(name);

    /// <summary>The rule's next debuff for this monster (not on it, castable), or false.</summary>
    private bool PassDebuffFor(int id, out string key, out int spellId, out int tier, out bool renewal)
    {
        key = ""; spellId = 0; tier = 0; renewal = false;
        if (!IsLiveMonster(id)) return false;
        if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable((uint)id)) return false;
        var wo = _worldFilter[id];
        if (wo == null) return false;
        var rule = GetRuleForTarget(wo);
        if (rule == null) return false;
        string element = GetPreferredElement(wo, rule);
        return NextDebuffFor(id, rule, element, RynthClock.Now, out key, out spellId, out tier, out renewal);
    }

    /// <summary>The monster the pass would debuff now, or 0 (attack). Setting off: 0.</summary>
    private int PassPick()
    {
        if (!_settings.DebuffAllBeforeAttacking || _spellManager == null) return 0;
        FillPassCandidates();
        return DebuffPassState.Next(_passCandidates, activeTargetId,
            id => PassDebuffFor(id, out _, out _, out _, out _), MonsterOnYou(), RynthClock.Now);
    }

    /// <summary>The pass has a debuff to cast before the next attack.</summary>
    private bool PassWantsDebuff() => PassPick() != 0;

    /// <summary>
    /// AttackWithMagic, after the fight target's own debuffs: cast the pass's next debuff (at another
    /// monster) instead of attacking. True when a debuff went out or it waits for mana.
    /// </summary>
    private bool TryDebuffPassCast()
    {
        int id = PassPick();
        if (id == 0) return false;
        if (!PassDebuffFor(id, out string key, out int spellId, out int tier, out bool renewal)) return false;
        if (ManaHoldsCast(spellId)) return true;   // mana reserve: Stamina to Mana first, or regenerate
        var wo = _worldFilter[id];
        double dist = DistanceInScan(id);
        _host.Log($"[DebuffPass] {(renewal ? "renewing" : "casting")} {key} (T{tier}) on 0x{(uint)id:X8} '{wo?.Name}' at {dist:0.0} yd" +
                  (DebuffPassState.Phase == DebuffPass.Stage.Attacking ? " (came into range mid-fight)" : "") +
                  $", the fight target 0x{(uint)activeTargetId:X8} waits");
        if (!CastDebuffAt(id, wo?.Name ?? "", key, spellId, tier, renewal)) return false;
        _pendingDebuffFromPass = true;
        DebuffPassState.NoteCast(id, RynthClock.Now);
        LastCombatSkipReason = "debuff-pass"; // D4 record-only
        return true;
    }

    private double DistanceInScan(int id)
    {
        for (int i = 0; i < _scannedTargets.Count; i++)
            if (_scannedTargets[i].Id == id) return _scannedTargets[i].Distance;
        return double.NaN;
    }

    /// <summary>An attack went out at the fight target: an arrival may get the next debuff.</summary>
    private void NotePassAttack() => _passState?.NoteAttack();

    /// <summary>The pending debuff's result: the pass counts failures on its own monsters.</summary>
    private void NotePassDebuffResult(bool failed)
    {
        if (!_pendingDebuffFromPass) return;
        _pendingDebuffFromPass = false;
        if (failed) _passState?.NoteFailed(_pendingDebuffTargetId);
    }
}

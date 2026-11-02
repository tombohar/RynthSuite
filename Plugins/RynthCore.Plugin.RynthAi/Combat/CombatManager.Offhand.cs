using System;
using System.Collections.Generic;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The off hand (the Shield slot): a shield, or a second weapon (dual wield). Rules in
/// <see cref="OffhandRules"/>, the choice in <see cref="OffhandPlanner"/>.
///
///   1. The choice for a target: the Monsters rule's OffhandId (Auto / Shield / Offhand weapon /
///      None, or an item), else the global LegacyUiSettings.OffhandDefault. The Damage tab's
///      per-monster offhand item wins over the mode (not over None). Listed items only.
///   2. Before a main weapon that can't share the hands (a two-hander, a launcher, a caster, or a
///      thrown weapon with an off-hand weapon) is wielded, the off hand goes into an open pack:
///      the server refuses the main weapon otherwise. One move in flight, 3 tries, then a rest.
///      With None the off hand is never touched: that main weapon isn't wielded (combat keeps
///      what's in hand) rather than sending a wield the server would refuse.
///   3. Once the main weapon is confirmed in hand (and the stance reached), the planned off hand
///      is wielded into the Shield slot: one in flight, confirmed in the slot, server refusals
///      counted, 3 tries then a rest, no flip-flop (<see cref="OffhandTracker"/>, the same tracker
///      as weapons and ammo). Something else in the off hand comes off first.
///   4. What RynthAi itself took off (for a two-hander, or buffing's wand) goes back on once the
///      hands allow it, when nothing listed is planned there.
/// </summary>
public partial class CombatManager
{
    /// <summary>Combat's off-hand wields: one in flight, confirmed, capped, no flip-flop.</summary>
    internal WeaponSwapTracker OffhandTracker { get; } = new();
    /// <summary>How long a read of the off hand is reused (it walks the inventory). Tests set 0.</summary>
    internal double OffhandReadMs { get; set; } = 1000;

    // Taking the off hand off (into a pack): one move in flight, 3 tries, then a rest.
    private int _offDequipPendingId, _offDequipTries;
    private DateTime _offDequipAt = DateTime.MinValue, _offDequipRestUntil = DateTime.MinValue;
    private const double OffDequipResolveMs = 2500;
    private const int OffDequipMaxTries = 3;

    // The off hand that was in the slot when the current swap was decided (for no flip-flop).
    private int _offSwapFrom;
    // The last item RynthAi wielded into the off hand (0 = none, or it has since gone to the main
    // hand or the pack). The slot reads can miss it (2026-10-17): with two weapons wielded, this one
    // is the off hand's.
    private int _offhandSentId;
    // An off hand RynthAi took off itself (for a main weapon, or buffing's wand): put back later.
    private int _stowedOffhandId;
    // The off-hand mode for the target combat is equipping for (the wand path reads it).
    private OffhandMode _curOffhandMode = OffhandMode.Auto;

    private int _offSlotId;
    private DateTime _offSlotReadAt = DateTime.MinValue;
    private (int Id, string Source) _lastOffPlan = (-1, "");
    private DateTime _lastOffHoldLogAt = DateTime.MinValue;
    private readonly HashSet<string> _offNoted = new();
    private const string DefaultOffhandSource = "Default offhand";

    private void ResetOffhandState()
    {
        OffhandTracker.Reset();
        _offDequipPendingId = _offDequipTries = 0;
        _offDequipAt = _offDequipRestUntil = DateTime.MinValue;
        _offSwapFrom = _stowedOffhandId = 0;
        _offhandSentId = 0;
        _offSlotId = 0;
        _offSlotReadAt = DateTime.MinValue;
        _lastOffPlan = (-1, "");
        _offNoted.Clear();
    }

    /// <summary>Buffing took <paramref name="id"/> out of the off hand for its wand: combat puts it back.</summary>
    public void NoteOffhandStowed(int id)
    {
        if (id != 0) _stowedOffhandId = id;
    }

    // ── What things are ──────────────────────────────────────────────────────

    private static int WieldedSlot(WorldObject wo) => wo.Values(LongValueKey.CurrentWieldedLocation, 0);

    private static bool IsTwoHandedItem(WorldObject wo) =>
        OffhandRules.IsTwoHanded(wo.Values((int)OffhandRules.PropCombatUse, 0), wo.Values(LongValueKey.Locations, 0),
                                 wo.Values((int)OffhandRules.PropWeaponSkill, 0));

    /// <summary>A shield (CombatUse Shield, or a non-weapon that goes in the Shield slot).</summary>
    internal static bool IsShieldItem(WorldObject wo)
    {
        bool weaponClass = wo.ObjectClass == AcObjectClass.MeleeWeapon || wo.ObjectClass == AcObjectClass.MissileWeapon
                        || wo.ObjectClass == AcObjectClass.WandStaffOrb;
        return OffhandRules.IsShield(wo.Values((int)OffhandRules.PropCombatUse, 0), wo.Values(LongValueKey.Locations, 0), weaponClass);
    }

    /// <summary>A melee weapon the server lets into the Shield slot (dual wield).</summary>
    internal static bool IsOffhandWeaponItem(WorldObject wo) =>
        wo.ObjectClass == AcObjectClass.MeleeWeapon
        && OffhandRules.CanBeOffhandWeapon(wo.Values(LongValueKey.Locations, 0), IsTwoHandedItem(wo));

    /// <summary>What a main weapon is, by ACE's wield rules.</summary>
    internal static MainHandKind MainKindOf(WorldObject? wo)
    {
        if (wo == null) return MainHandKind.None;
        if (WeaponList.IsWand(wo)) return MainHandKind.Caster;
        if (wo.ObjectClass == AcObjectClass.MissileWeapon)
        {
            if (IsAmmoItem(wo)) return MainHandKind.None;
            return wo.Values(LongValueKey.MaxStackSize, 0) > 1 ? MainHandKind.Thrown : MainHandKind.Launcher;
        }
        if (wo.ObjectClass == AcObjectClass.MeleeWeapon)
            return IsTwoHandedItem(wo) ? MainHandKind.TwoHanded : MainHandKind.OneHandedMelee;
        return MainHandKind.None;
    }

    /// <summary>The item in the off hand (the Shield slot), 0 = empty. Read live at most every
    /// <see cref="OffhandReadMs"/>, always live while an off-hand action is in flight.</summary>
    internal int OffhandInSlot(bool fresh = false)
    {
        DateTime now = RynthClock.Now;
        if (!fresh && OffhandTracker.PendingId == 0 && _offDequipPendingId == 0
            && (now - _offSlotReadAt).TotalMilliseconds < OffhandReadMs)
            return _offSlotId;
        _offSlotReadAt = now;
        int found = 0;
        // The server's word when the engine has it: what is in the Shield slot.
        if (EquippedState.TryGet(_host, out var eqOff))
        {
            foreach (var kv in eqOff)
                if ((kv.Value & OffhandRules.ShieldSlot) != 0) { found = kv.Key; break; }
            _offSlotId = found;
            return found;
        }
        try
        {
            foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: true))
                if ((WieldedSlot(wo) & OffhandRules.ShieldSlot) != 0) { found = wo.Id; break; }
            // CurrentWieldedLocation can read 0 (always under the Decal bridge): ask the weenie
            // itself who wields the listed off-hand items and where.
            if (found == 0 && _host.HasGetObjectWielderInfo)
            {
                uint pid = _host.GetPlayerId();
                foreach (var r in _settings.ItemRules)
                {
                    if (r.Id == 0 || pid == 0) continue;
                    if (_host.TryGetObjectWielderInfo(unchecked((uint)r.Id), out uint wielder, out uint loc)
                        && wielder == pid && (loc & OffhandRules.ShieldSlot) != 0 && !InPackNow(r.Id)) { found = r.Id; break; }
                }
            }
            // Nothing reads as in the Shield slot: what RynthAi put there, while it is still wielded
            // beside another weapon (alone it may have become the main hand's).
            if (found == 0 && _offhandSentId != 0)
            {
                var sentWo = _worldFilter[_offhandSentId];
                if (sentWo == null || !WorldObjectCache.IsWieldedByPlayer(_host, sentWo)) _offhandSentId = 0;
                else
                {
                    foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: false))
                    {
                        if (wo.Id == _offhandSentId || WorldObjectCache.IsAmmo(wo)) continue;
                        bool weapon = wo.ObjectClass == AcObjectClass.MeleeWeapon || wo.ObjectClass == AcObjectClass.MissileWeapon || IsWandObject(wo);
                        if (weapon && WorldObjectCache.IsWieldedByPlayer(_host, wo)) { found = _offhandSentId; break; }
                    }
                }
            }
        }
        catch { }
        _offSlotId = found;
        return found;
    }

    private bool DualWieldTrained => _charSkills != null && _charSkills[AcSkillType.DualWield].Training >= 2;

    // ── The choice ───────────────────────────────────────────────────────────

    /// <summary>
    /// The off-hand mode for <paramref name="target"/> (the rule's OffhandId, else the global
    /// setting) and an explicit item: the Damage tab's per-monster offhand, else the rule's old
    /// item pick; listed only.
    /// </summary>
    internal (OffhandMode Mode, int ExplicitId, string Source) OffhandChoiceFor(WorldObject? target, MonsterRule? rule)
    {
        var global = OffhandRules.Parse(_settings.OffhandDefault, OffhandMode.Auto);
        int ruleVal = rule?.OffhandId ?? 0;
        // A rule left on Default follows the Default row; the Default row left unset is the global setting (Auto).
        var defaultRow = _settings.MonsterRules.Find(r => r.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        var mode = OffhandRules.RuleMode(ruleVal) ?? OffhandRules.RuleMode(defaultRow?.OffhandId ?? 0) ?? global;
        if (mode == OffhandMode.None || target == null) return (mode, 0, "");

        int pick = 0;
        string source = "";
        if (_damageStore != null && _host.HasGetObjectWcid && _host.TryGetObjectWcid(unchecked((uint)target.Id), out uint wcid) && wcid != 0)
        {
            uint off = _damageStore.GetManualOffhand(wcid);
            if (off != 0) { pick = unchecked((int)off); source = "Damage tab offhand"; }
        }
        if (pick == 0 && OffhandRules.IsRuleItem(ruleVal)) { pick = ruleVal; source = $"Monsters rule '{rule!.Name}' offhand"; }
        if (pick == 0 && ruleVal == 0 && defaultRow != null && OffhandRules.IsRuleItem(defaultRow.OffhandId))
        { pick = defaultRow.OffhandId; source = DefaultOffhandSource; }
        if (pick != 0 && !_settings.ItemRules.Exists(r => r.Id == pick))
        {
            NoteUnlisted(pick, source);
            pick = 0;
            source = "";
        }
        return (mode, pick, source);
    }

    /// <summary>The Items list's shields and off-hand-capable one-handed melee weapons, in list order.</summary>
    internal List<OffhandCandidate> ListedOffhand(int offInSlot)
    {
        var list = new List<OffhandCandidate>();
        var seen = new HashSet<int>();
        foreach (var r in _settings.ItemRules)
        {
            if (r.Id == 0 || !seen.Add(r.Id)) continue;
            var wo = _worldFilter[r.Id];
            if (wo == null) continue;
            if (IsShieldItem(wo)) list.Add(new OffhandCandidate(r.Id, wo.Name, true, r.Id == offInSlot));
            else if (IsOffhandWeaponItem(wo)) list.Add(new OffhandCandidate(r.Id, wo.Name, false, r.Id == offInSlot, ListedElementOf(r.Id)));
        }
        return list;
    }

    /// <summary>A weapon's element as the weapon choice reads it: the one set in the Items list, else its properties.</summary>
    internal string ListedElementOf(int id)
    {
        var r = _settings.ItemRules.Find(x => x.Id == id);
        string elem = WeaponElements.Normalize(r?.Element);
        if (elem.Length == 0 && id != 0) elem = ElementTracker.Read(id, _worldFilter[id]?.Name).Element;
        return elem;
    }

    /// <summary>The off hand to go with <paramref name="mainId"/> against <paramref name="target"/>.</summary>
    internal OffhandPlan OffhandPlanFor(WorldObject target, MonsterRule? rule, int mainId)
    {
        var (mode, pick, source) = OffhandChoiceFor(target, rule);
        int off = OffhandInSlot();
        var kind = MainKindOf(_worldFilter[mainId]);
        // A dual-wield pair goes on together: the off-hand weapon of the main weapon's element.
        string mainElement = kind == MainHandKind.OneHandedMelee ? ListedElementOf(mainId) : "";
        var plan = OffhandPlanner.Choose(mode, kind, mainId, ListedOffhand(off), DualWieldTrained, _settings.PreferDualWield,
            _host.HasWieldItem, pick, source, mainElement, explicitIsDefault: source == DefaultOffhandSource);

        // Nothing listed planned: put back what RynthAi took off itself, when the hands allow it.
        if (plan.Id == 0 && mode != OffhandMode.None && _stowedOffhandId != 0 && _stowedOffhandId != mainId)
        {
            var stowed = _worldFilter[_stowedOffhandId];
            if (stowed == null || _stowedOffhandId == off) _stowedOffhandId = 0;
            else if ((kind == MainHandKind.OneHandedMelee
                      && (IsShieldItem(stowed) || (IsOffhandWeaponItem(stowed) && _host.HasWieldItem)))
                     || (kind == MainHandKind.Thrown && IsShieldItem(stowed)))   // a thrown weapon keeps a shield
                plan = new OffhandPlan(_stowedOffhandId, "putting back what was taken off");
        }
        return plan;
    }

    /// <summary>
    /// Dual wield: <paramref name="wantedId"/> (the weapon choice) already swings in the off hand
    /// beside <paramref name="inHand"/>, of another element. Its partner: another listed one-handed
    /// weapon of the wanted element the character can wield now, for the main hand (null = none,
    /// or the hands already hold the wanted element).
    /// </summary>
    private WorldObject? DualWieldPartnerFor(int wantedId, int inHand)
    {
        string elem = ListedElementOf(wantedId);
        if (elem.Length == 0 || elem.Equals(ListedElementOf(inHand), StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var c in ListedCandidates())
        {
            if (c.Id == wantedId || c.Id == inHand || c.Kind != CombatMode.Melee
                || !c.Element.Equals(elem, StringComparison.OrdinalIgnoreCase)) continue;
            if (_worldFilter[c.Id] is WorldObject wo && MainKindOf(wo) == MainHandKind.OneHandedMelee && IsOffhandWeaponItem(wo))
                return wo;
        }
        return null;
    }

    // ── Before a main wield ──────────────────────────────────────────────────

    private enum OffPrep { Ready, Busy, Blocked }

    /// <summary>
    /// Would what is in the off hand stop <paramref name="mainId"/> being wielded, with RynthAi
    /// not allowed to take it off (Offhand None) or resting after failed tries? Combat then keeps
    /// what's in hand instead of sending a wield the server would refuse.
    /// </summary>
    private bool OffhandBlocksMain(int mainId, OffhandMode mode)
    {
        int off = OffhandInSlot();
        if (off == 0) return false;
        var offWo = _worldFilter[off];
        var kind = MainKindOf(_worldFilter[mainId]);
        if (OffhandRules.OffhandAllowed(kind, offWo != null && IsShieldItem(offWo))) return false;
        string why;
        if (mode == OffhandMode.None) why = "Offhand is None, so it stays";
        else if (RynthClock.Now < _offDequipRestUntil) why = "it didn't come off; resting";
        else return false;
        if ((RynthClock.Now - _lastOffHoldLogAt).TotalSeconds > 10)
        {
            _lastOffHoldLogAt = RynthClock.Now;
            _host.Log($"[EquipDiag] not wielding 0x{(uint)mainId:X8} '{WeaponName(mainId)}' ({OffhandRules.KindLabel(kind)}): 0x{(uint)off:X8} '{offWo?.Name ?? "?"}' is in the off hand and {why} — the server refuses it otherwise");
        }
        return true;
    }

    /// <summary>
    /// Right before a main wield of <paramref name="mainId"/>: Ready when the off hand may stay
    /// (or is empty); Busy while it goes into a pack (wait); Blocked when it can't come off now
    /// (Offhand None, no pack room, resting): don't send the main wield. <paramref name="gateHeld"/>:
    /// the caller already holds the shared swap gate for this step (the wand paths).
    /// </summary>
    private OffPrep ClearOffhandFor(int mainId, OffhandMode mode, bool gateHeld = false)
    {
        int off = OffhandInSlot(fresh: true);
        if (off == 0) { _offDequipPendingId = 0; _offDequipTries = 0; return OffPrep.Ready; }
        var offWo = _worldFilter[off];
        var kind = MainKindOf(_worldFilter[mainId]);
        if (OffhandRules.OffhandAllowed(kind, offWo != null && IsShieldItem(offWo))) return OffPrep.Ready;
        if (mode == OffhandMode.None) return OffPrep.Blocked;
        var prep = DequipOffhand(off, $"before wielding 0x{(uint)mainId:X8} '{WeaponName(mainId)}' ({OffhandRules.KindLabel(kind)})", gateHeld);
        if (prep == OffPrep.Busy && _stowedOffhandId == 0) _stowedOffhandId = off;
        return prep;
    }

    /// <summary>Put <paramref name="offId"/> (in the off hand) into an open pack: one move in flight, capped.</summary>
    private OffPrep DequipOffhand(int offId, string why, bool gateHeld = false)
    {
        DateTime now = RynthClock.Now;
        if (_offDequipPendingId != offId) _offDequipTries = 0;
        if (_offDequipPendingId == offId && (now - _offDequipAt).TotalMilliseconds < OffDequipResolveMs)
            return OffPrep.Busy;
        if (now < _offDequipRestUntil) return OffPrep.Blocked;
        string name = _worldFilter[offId]?.Name ?? "";
        if (_offDequipTries >= OffDequipMaxTries)
        {
            _offDequipTries = 0;
            _offDequipPendingId = 0;
            _offDequipRestUntil = now.AddMinutes(2);
            _host.Log($"[EquipDiag] off hand 0x{(uint)offId:X8} '{name}' didn't come off after {OffDequipMaxTries} tries — leaving the hands as they are for 2 min");
            return OffPrep.Blocked;
        }
        int packId = OpenPackOverride?.Invoke() ?? WorldObjectCache.FindPackFor(_host, _worldFilter, includeMainPack: true, requireFree: 1);
        if (packId == 0 || !_host.HasMoveItemInternal)
        {
            if (packId == 0) WorldObjectCache.WarnPackFull(_host, "can't put the shield away to change weapons");
            return OffPrep.Blocked;
        }
        if (!gateHeld && _weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-offhand-dequip")) return OffPrep.Busy;

        _host.MoveItemInternal(unchecked((uint)offId), unchecked((uint)packId), 0, 1);
        if (offId == _offhandSentId) _offhandSentId = 0;
        _weaponSwapGate?.NoteMove(offId, wieldedNow: true);
        _offDequipPendingId = offId;
        _offDequipAt = now;
        _offDequipTries++;
        _lastEquipTime = now;
        _host.Log($"[EquipDiag] off hand 0x{(uint)offId:X8} '{name}' -> pack 0x{(uint)packId:X8} {why} (try {_offDequipTries}/{OffDequipMaxTries})");
        return OffPrep.Busy;
    }

    // ── After the main weapon is in hand ─────────────────────────────────────

    /// <summary>
    /// <paramref name="mainId"/> is confirmed in hand and the stance reached: wield the planned
    /// off hand. True while the attack should wait (a wield or a move in flight).
    /// </summary>
    private bool OffhandFollowUp(WorldObject target, MonsterRule? rule, int mainId)
    {
        DateTime now = RynthClock.Now;
        FoldServerAnswer(OffhandTracker, now);
        var plan = OffhandPlanFor(target, rule, mainId);
        int off = OffhandInSlot();

        if (plan.Id != _lastOffPlan.Id || plan.Source != _lastOffPlan.Source)
        {
            _lastOffPlan = (plan.Id, plan.Source);
            if (plan.Id != 0 || off != 0)
                _host.Log(plan.Id != 0
                    ? $"[EquipDiag] off hand for '{target.Name}': 0x{(uint)plan.Id:X8} '{WeaponName(plan.Id)}' ({plan.Source})"
                    : $"[EquipDiag] off hand for '{target.Name}': leaving 0x{(uint)off:X8} '{WeaponName(off)}' as it is ({plan.Source})");
        }

        if (plan.Id == 0 || plan.Id == off)
        {
            if (plan.Id != 0)
            {
                OffhandTracker.Confirmed(plan.Id, now);
                if (plan.Id == _stowedOffhandId) _stowedOffhandId = 0;
            }
            if (off == 0 || off == plan.Id) { _offDequipPendingId = 0; _offDequipTries = 0; }
            return false;
        }

        var step = OffhandTracker.Next(plan.Id, off, now, WeaponName);
        if (OffhandTracker.Notice != null)
        {
            _host.Log($"[EquipDiag] off hand: {OffhandTracker.Notice}");
            OffhandTracker.Notice = null;
        }
        switch (step)
        {
            case WeaponSwapTracker.Step.Wait:
                return true;
            case WeaponSwapTracker.Step.Hold:
                if ((now - _lastOffHoldLogAt).TotalSeconds > 10)
                {
                    _lastOffHoldLogAt = now;
                    _host.Log($"[EquipDiag] off hand: keeping 0x{(uint)off:X8} '{WeaponName(off)}' instead of 0x{(uint)plan.Id:X8} '{WeaponName(plan.Id)}' ({OffhandTracker.HoldReason})");
                }
                return false;
            case WeaponSwapTracker.Step.Swap:
                break;
            default:
                return false;
        }

        // Something else in the off hand: into the pack first (the server refuses an occupied slot).
        if (off != 0)
        {
            var prep = DequipOffhand(off, $"to wield 0x{(uint)plan.Id:X8} '{WeaponName(plan.Id)}' there");
            if (prep == OffPrep.Busy) { _offSwapFrom = off; return true; }
            return false;
        }

        var wo = _worldFilter[plan.Id];
        if (wo == null) return false;
        bool shield = IsShieldItem(wo);
        if (!_host.HasWieldItem && !shield)
        {
            if (_offNoted.Add("noslotwield")) _host.Log("[EquipDiag] off hand: this engine can't wield into the off hand — no dual wield");
            return false;
        }
        // Another swap just went out (the main weapon, say): fight on and try again after the gate.
        if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-offhand")) return false;

        bool sent = _host.HasWieldItem
            ? _host.WieldItem(unchecked((uint)plan.Id), OffhandRules.ShieldSlot)
            : _host.UseFor(unchecked((uint)plan.Id), "Combat", "offhand: wield the shield or offhand weapon");
        if (!sent)
        {
            if (_offNoted.Add("send:" + plan.Id)) _host.Log($"[EquipDiag] off hand: the wield of 0x{(uint)plan.Id:X8} '{wo.Name}' couldn't be sent");
            return false;
        }
        if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out _)) _swapUseDoneSeq = uSeq;
        if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out _, out _, out _)) _swapWeenieSeq = wSeq;
        OffhandTracker.Sent(plan.Id, _offSwapFrom, now);
        _offhandSentId = plan.Id;
        _weaponSwapGate?.NoteMove(plan.Id, wieldedNow: false);
        _offSwapFrom = 0;
        _lastEquipTime = now;
        if (plan.Id != _stowedOffhandId) _stowedOffhandId = 0;   // a listed one replaces what was taken off
        _host.Log($"[EquipDiag] off hand: wielding 0x{(uint)plan.Id:X8} '{wo.Name}' for '{target.Name}' ({plan.Source}) (try {OffhandTracker.TriesFor(plan.Id)}/{OffhandTracker.MaxTries})");
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Missile swaps between launchers that shoot different ammo (bow / crossbow / atlatl), rules in
/// <see cref="MissileAmmo"/>. Melee, magic and thrown weapons never come here.
///
///   1. The plan: a launcher with no fitting ammo in the pack is not a candidate (logged once),
///      unless it is the one in hand (running out is ammo crafting's job, as before) or no
///      launcher at all has ammo (then nothing changes). A launcher's element is the element it
///      does with the ammo it would shoot: the stack wielded when it fits, else the stack
///      <see cref="MissileAmmo.Choose"/> picks.
///   2. Before wielding a launcher, ammo of another type in the ammunition slot goes into the
///      pack (the server refuses the launcher otherwise). One move in flight, 3 tries, then a rest.
///   3. Once the launcher is in hand, the chosen stack is wielded: one in flight, confirmed in
///      the ammunition slot, retry cap and no flip-flop (<see cref="AmmoTracker"/>, the same
///      tracker as weapons). Ammo crafting holds off while this runs (<see cref="AmmoSwapInProgress"/>).
/// </summary>
public partial class CombatManager
{
    /// <summary>Combat's ammo wields after a launcher swap: one in flight, confirmed, capped.</summary>
    internal WeaponSwapTracker AmmoTracker { get; } = new();
    /// <summary>Tests: the pack an ammo stack is put into (default: an open pack with room).</summary>
    internal Func<int>? OpenPackOverride { get; set; }

    // The launcher whose ammo combat wields once it lands, and until when that holds crafting off.
    private int _ammoFollowUpFor;
    private DateTime _ammoFollowUpUntil = DateTime.MinValue;
    private const double AmmoFollowUpMs = 30_000;

    // Putting ammo of the wrong type away before the launcher wield.
    private int _ammoDequipPendingId, _ammoDequipTries;
    private DateTime _ammoDequipAt = DateTime.MinValue, _ammoDequipRestUntil = DateTime.MinValue;
    private const double AmmoDequipResolveMs = 2500;
    private const int AmmoDequipMaxTries = 3;
    // When the old ammo first read out of the ammunition slot (MinValue = not seen yet).
    private DateTime _ammoOffSeenAt = DateTime.MinValue;

    /// <summary>
    /// How long the launcher waits once the old ammo reads off (2026-10-12). Before, the dequip
    /// held the launcher for the swap gate's full 3 s (a pack move) and then the equip pacing's
    /// 2 s, so a mixed-launcher swap took 5-6 s. Ammo has no unwield motion, and the server's own
    /// drop to peace mode is waited out by the combat tick's animation gate. Settable for tests.
    /// </summary>
    internal double AmmoOffSettleMs { get; set; } = 400;

    private readonly HashSet<int> _noAmmoNoted = new();

    /// <summary>
    /// A launcher swap's ammo step is under way (the old stack going into the pack, or the new
    /// one being wielded). Ammo crafting waits: it would otherwise equip ammo for the launcher
    /// being swapped away from, or start a craft in the middle of the swap.
    /// </summary>
    public bool AmmoSwapInProgress => _ammoFollowUpFor != 0 && RynthClock.Now < _ammoFollowUpUntil;

    private void ResetAmmoState()
    {
        AmmoTracker.Reset();
        _ammoFollowUpFor = 0;
        _ammoFollowUpUntil = DateTime.MinValue;
        _ammoDequipPendingId = _ammoDequipTries = 0;
        _ammoDequipRestUntil = DateTime.MinValue;
        _ammoOffSeenAt = DateTime.MinValue;
        _noAmmoNoted.Clear();
    }

    /// <summary>Arrows, quarrels, darts: ValidLocations has the ammunition slot; by name when that isn't known.</summary>
    internal static bool IsAmmoItem(WorldObject wo)
    {
        int valid = wo.Values(LongValueKey.Locations, 0);
        if (valid != 0) return (valid & MissileAmmo.AmmoSlot) != 0;
        return WorldObjectCache.IsAmmo(wo);
    }

    /// <summary>The ammo type a weapon needs; 0 for anything but a launcher whose type is known (thrown, melee, wand).</summary>
    private static int LauncherTypeOf(WorldObject? wo)
    {
        if (wo == null || wo.ObjectClass != AcObjectClass.MissileWeapon || IsAmmoItem(wo)) return 0;
        bool stackable = wo.Values(LongValueKey.MaxStackSize, 0) > 1;
        return MissileAmmo.LauncherAmmoType(wo.Values((int)MissileAmmo.PropAmmoType, 0), wo.Name, stackable);
    }

    private static int AmmoTypeOfItem(WorldObject wo) => MissileAmmo.AmmoTypeOf(wo.Values((int)MissileAmmo.PropAmmoType, 0), wo.Name);

    /// <summary>Every ammo stack the character carries (wielded one included), in pack order.</summary>
    internal List<AmmoCandidate> PackAmmo()
    {
        var list = new List<AmmoCandidate>();
        var rules = _settings.ItemRules;
        foreach (var wo in _worldFilter.GetDirectInventory(forceRefresh: true))
        {
            if (!IsAmmoItem(wo)) continue;
            int type = AmmoTypeOfItem(wo);
            if (type == 0) continue;
            uint uid = unchecked((uint)wo.Id);
            int? dt = _host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(uid, WeaponElements.PropDamageType, out int d) ? d : null;
            int? fx = _host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(uid, WeaponElements.PropUiEffects, out int f) ? f : null;
            bool prismatic = MissileAmmo.IsPrismatic(dt, wo.Name);
            string elem = prismatic ? "" : WeaponElements.Detect(dt, null, fx, wo.Name).Element;
            int listIdx = rules.FindIndex(r => r.Id == wo.Id);
            int quality = AmmoRecipes.OutputRank(wo.Name, MissileAmmo.CategoryOf(type));
            bool wielded = WorldObjectCache.IsWieldedByPlayer(_host, wo);
            list.Add(new AmmoCandidate(wo.Id, wo.Name, type, elem, prismatic, quality, listIdx, wielded));
        }
        return list;
    }

    private static string RuleElementOf(MonsterRule? rule) =>
        rule != null && !string.IsNullOrEmpty(rule.DamageType) && !rule.DamageType.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? WeaponElements.Normalize(rule.DamageType) : "";

    private static Func<string, int> AmmoRank(string ruleElement, CreatureWeakness.Ranking? weak) =>
        MissileAmmo.RankBy(WeaponElements.Normalize(ruleElement), weak != null && weak.Order.Count > 0 ? WeaponPlanner.TieGroups(weak) : null);

    /// <summary>A launcher's own element: the Items list's, else what its properties say.</summary>
    private string LauncherElement(int id, WorldObject wo)
    {
        var r = _settings.ItemRules.FirstOrDefault(x => x.Id == id);
        string e = WeaponElements.Normalize(r?.Element);
        return e.Length > 0 ? e : ElementTracker.Read(id, wo.Name).Element;
    }

    /// <summary>
    /// The plan's view of the launchers (step 1 in the class note). Lists without a launcher come
    /// back as they are, so melee and magic plans don't change.
    /// </summary>
    internal List<WeaponCandidate> ApplyAmmo(List<WeaponCandidate> listed, string ruleElement, CreatureWeakness.Ranking? weak)
    {
        if (listed.Count == 0 || !listed.Any(c => c.Kind == CombatMode.Missile)) return listed;

        List<AmmoCandidate>? pack = null;
        var rank = AmmoRank(ruleElement, weak);
        var result = new List<WeaponCandidate>(listed.Count);
        var dropped = new List<(WeaponCandidate C, int Type)>();
        foreach (var c in listed)
        {
            if (c.Kind != CombatMode.Missile) { result.Add(c); continue; }
            int lt = LauncherTypeOf(_worldFilter[c.Id]);
            if (lt == 0) { result.Add(c); continue; }        // thrown, or a type not known: as before

            pack ??= PackAmmo();
            var wielded = pack.FirstOrDefault(a => a.Wielded);
            var use = wielded != null && MissileAmmo.Fits(lt, wielded.AmmoType)
                ? wielded
                : MissileAmmo.Choose(lt, c.Element, pack, rank);
            if (use == null)
            {
                if (c.InHand) { result.Add(c); continue; }   // ran out in hand: crafting's job, as before
                dropped.Add((c, lt));
                continue;
            }
            _noAmmoNoted.Remove(c.Id);
            // An element the user picked in the Items panel wins over the ammo's (owner's call,
            // 2026-10-01), although the server uses the ammo's element for the damage.
            if (_settings.ItemRules.FirstOrDefault(x => x.Id == c.Id)?.ElementSource == WeaponList.SourceSet)
            {
                result.Add(c);
                continue;
            }
            string e = MissileAmmo.EffectiveElement(use, c.Element);
            result.Add(e.Length > 0 && e != c.Element ? c with { Element = e } : c);
        }

        if (dropped.Count == 0) return result;
        // No launcher of the character's main kind is left: change nothing (the first launcher is
        // still wielded and ammo crafting makes ammo for it), rather than turning an archer into a
        // swordsman or a mage because the pack ran dry.
        if (listed[0].Kind == CombatMode.Missile && !result.Any(c => c.Kind == CombatMode.Missile))
            return listed;
        foreach (var (c, lt) in dropped)
            if (_noAmmoNoted.Add(c.Id))
                _host.Log($"[EquipDiag] 0x{(uint)c.Id:X8} '{c.Name}' has no {MissileAmmo.Label(lt)} in the pack — not swapping to it");
        return result;
    }

    private enum AmmoPrep { Ready, Busy, Blocked }

    /// <summary>
    /// Step 2: called right before a launcher wield is sent. Ready: send it. Busy: ammo of the
    /// wrong type is on its way into the pack (wait). Blocked: it can't be put away now (no pack
    /// room, or resting after failed tries); don't send the launcher, the server would refuse it.
    /// Also arms step 3 when a fitting stack is in the pack.
    /// </summary>
    private AmmoPrep PrepareAmmoFor(int launcherId)
    {
        int lt = LauncherTypeOf(_worldFilter[launcherId]);
        if (lt == 0) return AmmoPrep.Ready;

        DateTime now = RynthClock.Now;
        var pack = PackAmmo();
        var wielded = pack.FirstOrDefault(a => a.Wielded);
        if (wielded != null && MissileAmmo.Fits(lt, wielded.AmmoType))
        {
            _ammoDequipPendingId = 0; _ammoDequipTries = 0; _ammoOffSeenAt = DateTime.MinValue;
            return AmmoPrep.Ready;                            // same ammo type: it stays
        }
        if (pack.Any(a => !a.Wielded && MissileAmmo.Fits(lt, a.AmmoType)))
            ArmAmmoFollowUp(launcherId, now);
        if (wielded == null)
        {
            // Our own dequip just landed: a short settle, then the swap gate is told the move is
            // done so the launcher goes on the server's answers rather than its full 3 s.
            if (_ammoDequipPendingId != 0)
            {
                if (_ammoOffSeenAt == DateTime.MinValue) _ammoOffSeenAt = now;
                if ((now - _ammoOffSeenAt).TotalMilliseconds < AmmoOffSettleMs) return AmmoPrep.Busy;
                _weaponSwapGate?.MoveDone(_ammoDequipPendingId);
            }
            _ammoDequipPendingId = 0; _ammoDequipTries = 0; _ammoOffSeenAt = DateTime.MinValue;
            return AmmoPrep.Ready;
        }

        // Ammo of another type is wielded: into the pack first.
        if (_ammoDequipPendingId == wielded.Id && (now - _ammoDequipAt).TotalMilliseconds < AmmoDequipResolveMs)
            return AmmoPrep.Busy;
        if (now < _ammoDequipRestUntil) return AmmoPrep.Blocked;
        if (_ammoDequipTries >= AmmoDequipMaxTries)
        {
            _ammoDequipTries = 0;
            _ammoDequipPendingId = 0;
            _ammoDequipRestUntil = now.AddMinutes(2);
            _host.Log($"[EquipDiag] {MissileAmmo.Label(wielded.AmmoType)} 0x{(uint)wielded.Id:X8} '{wielded.Name}' didn't come off after {AmmoDequipMaxTries} tries — not swapping to 0x{(uint)launcherId:X8} '{WeaponName(launcherId)}' for 2 min");
            return AmmoPrep.Blocked;
        }
        int packId = OpenPackOverride?.Invoke() ?? WorldObjectCache.FindPackFor(_host, _worldFilter, includeMainPack: true, requireFree: 1);
        if (packId == 0 || !_host.HasMoveItemInternal)
        {
            if (packId == 0) WorldObjectCache.WarnPackFull(_host, $"can't put the {MissileAmmo.Label(wielded.AmmoType)} away to switch launchers");
            return AmmoPrep.Blocked;
        }
        if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-ammo-dequip")) return AmmoPrep.Busy;

        var wo = _worldFilter[wielded.Id];
        int amount = Math.Max(1, wo?.Values(LongValueKey.StackCount, 1) ?? 1);   // the whole stack, not one arrow
        _host.MoveItemInternal(unchecked((uint)wielded.Id), unchecked((uint)packId), 0, amount);
        _weaponSwapGate?.NoteMove(wielded.Id, wieldedNow: true);
        _ammoDequipPendingId = wielded.Id;
        _ammoDequipAt = now;
        _ammoDequipTries++;
        _ammoOffSeenAt = DateTime.MinValue;
        // No _lastEquipTime stamp: its 2 s equip pacing would hold the launcher after the ammo is
        // already off. Busy above (until it reads off and settles) does the waiting instead.
        _host.Log($"[EquipDiag] {MissileAmmo.Label(wielded.AmmoType)} 0x{(uint)wielded.Id:X8} '{wielded.Name}' x{amount} -> pack 0x{(uint)packId:X8} before wielding 0x{(uint)launcherId:X8} '{WeaponName(launcherId)}' (try {_ammoDequipTries}/{AmmoDequipMaxTries})");
        return AmmoPrep.Busy;
    }

    private void ArmAmmoFollowUp(int launcherId, DateTime now)
    {
        if (_ammoFollowUpFor == launcherId && now < _ammoFollowUpUntil) return;
        _ammoFollowUpFor = launcherId;
        _ammoFollowUpUntil = now.AddMilliseconds(AmmoFollowUpMs);
    }

    private void EndAmmoFollowUp() { _ammoFollowUpFor = 0; _ammoFollowUpUntil = DateTime.MinValue; }

    /// <summary>
    /// True while ChangeCombatMode(Missile) must wait for <paramref name="launcherId"/> (not yet
    /// read in hand): combat is swapping to it and it, or its ammo, isn't on yet. Each request in
    /// that window was bounced by the server (no launcher, or no ammo) and flickered the stance
    /// Missile/peace/Melee for the whole swap; it also stayed the outstanding server action for
    /// 2 s, which held the swap gate at its full 3 s per step (2026-10-11 14:45:11-17). Once the
    /// launcher reads in hand, the wielded branch sends the stance after the ammo is on.
    /// Thrown weapons and launchers of an unknown type: false (as before).
    /// </summary>
    private bool LauncherSwapHoldsStance(int launcherId)
    {
        int lt = LauncherTypeOf(_worldFilter[launcherId]);
        if (lt == 0) return false;
        if (_ammoDequipPendingId != 0) return true;                                // old ammo going into the pack
        if (_ammoFollowUpFor == launcherId && AmmoSwapInProgress) return true;     // its ammo goes on once it lands
        if (SwapTracker.PendingId == launcherId) return true;                      // its wield is in flight
        var wielded = PackAmmo().FirstOrDefault(a => a.Wielded);
        return wielded != null && !MissileAmmo.Fits(lt, wielded.AmmoType);        // the wrong ammo is still on
    }

    /// <summary>
    /// Step 3, with <paramref name="launcherId"/> confirmed in hand. True while the attack should
    /// wait for the ammo wield. Only after combat's own launcher swap: an empty quiver otherwise is
    /// left to ammo crafting (which may craft better ammo first), as before.
    /// </summary>
    private bool AmmoFollowUp(int launcherId, WorldObject target, MonsterRule? rule)
    {
        if (_ammoFollowUpFor == 0) return false;
        DateTime now = RynthClock.Now;
        if (_ammoFollowUpFor != launcherId || now >= _ammoFollowUpUntil) { EndAmmoFollowUp(); return false; }
        var wo = _worldFilter[launcherId];
        int lt = LauncherTypeOf(wo);
        if (wo == null || lt == 0) { EndAmmoFollowUp(); return false; }

        SwapTracker.Confirmed(launcherId, now);              // the launcher landed
        FoldServerAnswer(AmmoTracker, now);

        var pack = PackAmmo();
        var wielded = pack.FirstOrDefault(a => a.Wielded);
        if (wielded != null && MissileAmmo.Fits(lt, wielded.AmmoType))
        {
            AmmoTracker.Confirmed(wielded.Id, now);
            _host.Log($"[EquipDiag] ammo in: 0x{(uint)wielded.Id:X8} '{wielded.Name}' for 0x{(uint)launcherId:X8} '{wo.Name}'");
            EndAmmoFollowUp();
            return false;
        }

        var pick = MissileAmmo.Choose(lt, LauncherElement(launcherId, wo), pack, AmmoRank(RuleElementOf(rule), WeaknessFor(target)));
        if (pick == null)
        {
            _host.Log($"[EquipDiag] no {MissileAmmo.Label(lt)} left for 0x{(uint)launcherId:X8} '{wo.Name}' — leaving it to ammo crafting");
            EndAmmoFollowUp();
            return false;
        }

        var step = AmmoTracker.Next(pick.Id, wielded?.Id ?? 0, now, WeaponName);
        if (AmmoTracker.Notice != null)
        {
            _host.Log($"[EquipDiag] {AmmoTracker.Notice}");
            AmmoTracker.Notice = null;
        }
        switch (step)
        {
            case WeaponSwapTracker.Step.Wait:
                return true;
            case WeaponSwapTracker.Step.Swap:
                if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-ammo")) return true;
                _host.UseFor(unchecked((uint)pick.Id), "Combat", "ammo for the missile weapon");
                if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out _)) _swapUseDoneSeq = uSeq;
                if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out _, out _, out _)) _swapWeenieSeq = wSeq;
                AmmoTracker.Sent(pick.Id, wielded?.Id ?? 0, now);
                _lastEquipTime = now;
                _host.Log($"[EquipDiag] ammo: wielding 0x{(uint)pick.Id:X8} '{pick.Name}' for 0x{(uint)launcherId:X8} '{wo.Name}' (try {AmmoTracker.TriesFor(pick.Id)}/{AmmoTracker.MaxTries})");
                return true;
            case WeaponSwapTracker.Step.Hold:
                _host.Log($"[EquipDiag] ammo: not wielding 0x{(uint)pick.Id:X8} '{pick.Name}' ({AmmoTracker.HoldReason}) — leaving it to ammo crafting");
                EndAmmoFollowUp();
                return false;
            default:
                EndAmmoFollowUp();
                return false;
        }
    }
}

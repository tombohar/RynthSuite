using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Fast Weapon Swap, combat's own equip (2026-10-23): a monster to fight with a caster still in hand
/// (it came while healing). Before, combat used the main weapon (the client's own swap): the wand went
/// into the pack after a forced stance change in Magic mode (about 1.2 s), the main weapon landed at
/// about 1.4 s and the off hand at 1.7 s (Drakkon, 10-22 night, about half his swaps back). Now the same
/// hands swap as weapons back after heals (HandSwapSequence): peace, the wand out, the main weapon and
/// the off hand on (one frame when the server is quiet), then the fight's stance at once.
///
/// A weapons-back swap under way when the target comes goes on, pointed at the fight's weapons when they
/// differ; a fight with the wand ends it. A failed fight swap leaves that weapon to the client's own swap
/// for <see cref="FightSwapFallbackSec"/>. Setting off: none of this runs.
/// </summary>
public partial class CombatManager
{
    // The hands swap (_rearmSwap) is for the fight's weapons, not weapons back after heals.
    private bool _handsSwapForFight;
    private int _handsSwapFightMode;
    private string _handsGateBy = "rearm-after-vitals";
    // The stance owed after the swap is the fight's (asked for with a target, PeaceModeWhenIdle or not).
    private bool _rearmStanceForFight;
    // A fight swap that failed: the client's own swap for that weapon for a while.
    private int _fightSwapFallbackFor;
    private DateTime _fightSwapFallbackUntil = DateTime.MinValue;
    private const double FightSwapFallbackSec = 20;

    private string HandsSwapLabel => _handsSwapForFight ? "weapons for the fight" : "weapons back";

    /// <summary>
    /// The fight's main weapon and the off hand to go with it: the planned one, else what is in the off
    /// hand already when it may stay (a hands swap takes off everything it doesn't want).
    /// </summary>
    private List<(int Id, int Slot)> FightWants(WorldObject target, MonsterRule? rule, int mainId)
    {
        var wants = new List<(int Id, int Slot)> { (mainId, MainSlotFor(mainId)) };
        int off = OffhandPlanFor(target, rule, mainId).Id;
        if (off != 0 && off != mainId && _worldFilter[off] is WorldObject offWo && (_host.HasWieldItem || IsShieldItem(offWo)))
        {
            wants.Add((off, HandSwapSequence.ShieldSlot));
            return wants;
        }
        int inOff = OffhandInSlot(fresh: true);
        if (inOff != 0 && inOff != mainId && _worldFilter[inOff] is WorldObject inOffWo
            && OffhandRules.OffhandAllowed(MainKindOf(_worldFilter[mainId]), IsShieldItem(inOffWo)))
            wants.Add((inOff, HandSwapSequence.ShieldSlot));
        return wants;
    }

    private static bool SameWants(IReadOnlyList<(int Id, int Slot)> a, IReadOnlyList<(int Id, int Slot)> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>
    /// A caster in hand and <paramref name="mainId"/> (melee or a launcher) wanted: start the hands swap.
    /// True = started, or waiting to (the gate, ammo going into the pack); false = the old route this tick.
    /// </summary>
    private bool TryStartFightHandsSwap(WorldObject target, MonsterRule? rule, int mainId, int desiredMode)
    {
        DateTime now = RynthClock.Now;
        if (_fightSwapFallbackFor == mainId && now < _fightSwapFallbackUntil) return false;
        if (!_host.HasMoveItemInternal) return false;
        if (desiredMode == CombatMode.Missile)
        {
            var prep = PrepareAmmoFor(mainId);   // the wrong ammo into the pack first; arms the ammo follow-up
            if (prep == AmmoPrep.Busy) return true;
            if (prep == AmmoPrep.Blocked) return false;
        }
        if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("combat-hands-swap")) return true;

        StartHandsSwapForFight(now, FightWants(target, rule, mainId), desiredMode, target);
        RearmSwapAdvance(now);
        return true;
    }

    private void StartHandsSwapForFight(DateTime now, List<(int Id, int Slot)> wants, int desiredMode, WorldObject target)
    {
        int mode = CurrentCombatMode;
        _rearmWants = wants;
        _rearmSwap.Start("weapons for the fight", now, wants, peaceFirst: mode != CombatMode.NonCombat);
        _handsSwapForFight = true;
        _handsSwapFightMode = desiredMode;
        _handsGateBy = "combat-hands-swap";
        _weaponSwapGate?.Hold(_handsGateBy, (long)_rearmSwap.GiveUpMs + 1000);
        _rearmSwapOpen = true;
        _rearmLastMoved = 0;
        _rearmStanceWanted = 0;
        _host.Log($"[EquipDiag] weapons for the fight ('{target.Name}'): " +
                  string.Join(" + ", wants.Select(w => $"0x{(uint)w.Id:X8} '{WeaponName(w.Id)}'{(w.Slot == HandSwapSequence.ShieldSlot ? " (off hand)" : "")}")) +
                  $" (mode {mode})");
    }

    /// <summary>
    /// A hands swap is under way and combat has a target: true = wait for it (it goes on, now with the
    /// fight's weapons); false = it ended (the fight wants a caster, or another launcher) and combat's
    /// own equip goes on this tick.
    /// </summary>
    private bool FightJoinsHandsSwap(WorldObject target, MonsterRule? rule, int mainId, WorldObject mainObj)
    {
        DateTime now = RynthClock.Now;
        bool wasFight = _handsSwapForFight;
        if (IsWandObject(mainObj))
        {
            FinishRearmSwap(now, done: false, why: $"'{target.Name}' is fought with the wand");
            if (!wasFight) _rearmedFor = _rearmVital;
            return false;
        }
        _curOffhandMode = OffhandChoiceFor(target, rule).Mode;
        var wants = FightWants(target, rule, mainId);
        int mode = WeaponKind(mainId);
        if (SameWants(_rearmSwap.Wants, wants))
        {
            if (!wasFight) _host.Log($"[EquipDiag] weapons back: the same weapons for '{target.Name}', going on");
            _handsSwapForFight = true;
            _handsSwapFightMode = mode;
            return true;
        }
        if (_rearmSwap.InFlight) return true;   // the step on its way lands first, then the new weapons
        if (mode == CombatMode.Missile)
        {
            // A launcher needs its ammo seen to first: combat's own launcher swap does that.
            FinishRearmSwap(now, done: false, why: $"a launcher for '{target.Name}'");
            if (!wasFight) _rearmedFor = _rearmVital;
            return false;
        }
        _host.Log($"[EquipDiag] {HandsSwapLabel}: other weapons for '{target.Name}' ({_rearmSwap.Trail})");
        StartHandsSwapForFight(now, wants, mode, target);
        RearmSwapAdvance(now);
        return true;
    }

    /// <summary>Everything the fight wants is in hand: its stance at once (past our own peace request).</summary>
    private void FightSwapDone(DateTime now)
    {
        int main = _rearmWants.Count > 0 ? _rearmWants[0].Id : 0;
        int mode = _handsSwapFightMode;
        if (main != 0) SwapTracker.Confirmed(main, now);
        _inHandReadAt = DateTime.MinValue;   // read the hands fresh next tick
        _offSlotReadAt = DateTime.MinValue;
        // A launcher: only with fitting ammo on; otherwise the wielded branch puts it on, then asks.
        bool ammoReady = mode != CombatMode.Missile || (HasWieldedAmmo() && !LauncherSwapHoldsStance(main));
        _rearmStanceWanted = mode != 0 && mode != CombatMode.Magic && CurrentCombatMode != mode && ammoReady ? mode : 0;
        _rearmStanceSince = now;
        _rearmStanceForFight = true;
        FinishRearmSwap(now, done: true);
        RearmStanceStep(now);
    }

    /// <summary>The fight swap stopped: the client's own swap for that weapon for a while.</summary>
    private void NoteFightSwapFailed(DateTime now, bool counted = true)
    {
        if (!counted) return;
        int main = _rearmWants.Count > 0 ? _rearmWants[0].Id : 0;
        if (main == 0) return;
        _fightSwapFallbackFor = main;
        _fightSwapFallbackUntil = now.AddSeconds(FightSwapFallbackSec);
        _host.Log($"[EquipDiag] weapons for the fight: the client's own swap for 0x{(uint)main:X8} '{WeaponName(main)}' for {FightSwapFallbackSec:0} s");
    }
}

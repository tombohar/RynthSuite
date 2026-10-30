using System;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;
using System.Linq;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Weapons Back After Heals as it was before 2026-10-22 (RynthSuite e613280), kept for Fast Weapon
/// Swap off: a use of the main weapon (the client puts the wand away), then the off hand once the
/// swap gate lets it go. Fast Weapon Swap on goes through HandSwapSequence (RearmAfterVitalsStep).
/// </summary>
public partial class CombatManager
{
    private bool LegacyRearmSwap(DateTime now, DateTime vital)
    {
        int inHand = InHandWeaponId();
        if (inHand != _fightMainId)
        {
            if (SwapTracker.PendingId != 0) { FoldServerAnswer(SwapTracker, now); return true; }   // our wield is on its way
            var step = SwapStep(_fightMainId, inHand);
            if (step == WeaponSwapTracker.Step.Wait) return true;
            if (step != WeaponSwapTracker.Step.Swap) { _rearmedFor = vital; return false; }
            if (_weaponSwapGate != null && !_weaponSwapGate.TryBeginSwap("rearm-after-vitals")) return true;
            var prep = ClearOffhandFor(_fightMainId, OffhandMode.Auto, gateHeld: true);
            if (prep == OffPrep.Busy) return true;
            if (prep == OffPrep.Blocked) { _rearmedFor = vital; return false; }
            SendWield(_fightMainId, inHand, "weapons back after heals");
            return true;
        }

        int off = OffhandInSlot();
        if (_fightOffId != 0 && off != _fightOffId && _fightOffId != _fightMainId
            && _worldFilter.PlayerHas(_fightOffId) != false && _settings.ItemRules.Any(r => r.Id == _fightOffId))
        {
            FoldServerAnswer(OffhandTracker, now);
            if (OffhandTracker.PendingId != 0) return true;
            if (off != 0) { DequipOffhand(off, $"to put 0x{(uint)_fightOffId:X8} '{WeaponName(_fightOffId)}' back"); return true; }
            var wo = _worldFilter[_fightOffId];
            if (wo != null && _host.HasWieldItem && (_weaponSwapGate == null || _weaponSwapGate.TryBeginSwap("rearm-after-vitals-offhand")))
            {
                var stepOff = OffhandTracker.Next(_fightOffId, off, now, WeaponName);
                if (stepOff == WeaponSwapTracker.Step.Swap && _host.WieldItem(unchecked((uint)_fightOffId), OffhandRules.ShieldSlot))
                {
                    if (_host.HasGetLastUseDone && _host.TryGetLastUseDone(out int uSeq, out _)) _swapUseDoneSeq = uSeq;
                    if (_host.HasGetLastWeenieError && _host.TryGetLastWeenieError(out int wSeq, out _, out _, out _)) _swapWeenieSeq = wSeq;
                    OffhandTracker.Sent(_fightOffId, 0, now);
                    _weaponSwapGate?.NoteMove(_fightOffId, wieldedNow: false);
                    _offhandSentId = _fightOffId;
                    _lastEquipTime = now;
                    _host.Log($"[EquipDiag] weapons back after heals: off hand 0x{(uint)_fightOffId:X8} '{wo.Name}'");
                    return true;
                }
                if (stepOff == WeaponSwapTracker.Step.Wait) return true;
            }
            _rearmedFor = vital;   // the off hand can't go on now: the main hand is back, that will do
            return false;
        }

        _rearmedFor = vital;
        _host.Log($"[EquipDiag] weapons back after heals: 0x{(uint)_fightMainId:X8} '{WeaponName(_fightMainId)}'" +
                  (_fightOffId != 0 ? $" and 0x{(uint)_fightOffId:X8} '{WeaponName(_fightOffId)}'" : "") + " in hand");
        return false;
    }
}

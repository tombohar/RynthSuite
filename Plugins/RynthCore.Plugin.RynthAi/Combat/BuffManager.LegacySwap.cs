using System;
using RynthCore.PluginSdk;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The wand swap as it was before 2026-10-22 (RynthSuite e613280), kept for Fast Weapon Swap off: a
/// use of the wand while a weapon is in hand (the client puts the weapon away), the pack route after
/// a miss, and the swap gate's flat 3 s after every pack move. Fast Weapon Swap on goes through
/// HandSwapSequence instead (BuffManager.WandSwapStep). Off is the default until the new swap has run
/// a night on Drakkon; this file goes once it is the only way.
/// </summary>
public partial class BuffManager
{
    private int _directSwapMisses;      // direct swaps in a row that didn't get the wand into the hand
    private bool _packFirstNext;        // the next swap goes through the pack (one miss)
    private bool _pendingWieldDirect;   // the wand's use in flight was a direct swap (a weapon in hand)
    internal const int DirectSwapMissesToStick = 2;
    private bool PackFirst => _packFirstNext || _directSwapMisses >= DirectSwapMissesToStick;
    // The swap gate's claim count when the wand's use went out: a later claim means another swap
    // (combat putting the axe back, 12:39 on 10-06) changed the hands, so the result says nothing.
    private long _pendingWieldClaim;
    private int _bowDequipPendingId;
    private DateTime _bowDequipAt = DateTime.MinValue;
    private int _bowDequipAttempts;
    private const int BowDequipMaxAttempts = 3;

    /// <summary>EnsureMagicMode's branch for a wand not yet in hand, before 2026-10-22.</summary>
    private bool LegacyWandSwap(int wandId, bool forBuff)
    {
        // Wield gate: don't spam UseObject. Hold off after the first issue
        // until either the wielded check confirms (cleared on the Magic-mode
        // branch above) or the resolve timeout elapses, then cool down
        // before retrying. Mirrors the no-chat-timeout pattern used for
        // spell casts at the top of OnHeartbeat. Checked BEFORE the shared
        // swap gate so a denied attempt never claims the cross-subsystem slot.
        DateTime now = Now;
        if (now < _wieldCooldownUntil)
            return false;

        if (_pendingWieldId != 0)
        {
            if ((now - _pendingWieldAt).TotalMilliseconds < WieldResolveTimeoutMs)
                return false;

            if (_weaponSwapGate != null && _weaponSwapGate.Claims != _pendingWieldClaim)
            {
                _pendingWieldId = 0;   // another swap since ours: not a failed wield
                return false;
            }
            int stillInHand = _pendingWieldDirect ? FindWieldedNonWandWeapon(wandId) : 0;
            if (stillInHand != 0 && IsWieldedLive(stillInHand))
            {
                _directSwapMisses++;
                _packFirstNext = true;
                _pendingWieldId = 0;
                _host.Log($"[WieldGate] using the wand didn't put 0x{(uint)stillInHand:X8} away in {WieldResolveTimeoutMs:0} ms; " +
                          (_directSwapMisses >= DirectSwapMissesToStick
                              ? $"{_directSwapMisses} in a row: from now on the weapon goes into a pack first"
                              : "the next swap puts the weapon into a pack first, then the use is tried again"));
                return false;
            }
            _host.Log($"[WieldGate] UseObject(0x{(uint)_pendingWieldId:X8}) not confirmed in " +
                      $"{WieldResolveTimeoutMs:0}ms — cooling down {WieldCooldownMs:0}ms");
            NoteWandWieldFailed(_pendingWieldId);
            _pendingWieldId = 0;
            _wieldCooldownUntil = now.AddMilliseconds(WieldCooldownMs);
            // FIX: bound the previously-infinite wield loop. If the wand still won't
            // wield after a few cooldowns (e.g. no pack to free the bow, or an
            // unresolvable collision), degrade so buffing yields and combat runs.
            if (++_wieldGateFailCount >= WieldGateFailMax)
                return SignalSwapFailure(forBuff, FindWieldedNonWandWeapon(wandId), wandId);
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
            if (_host.HasStopCompletely) _host.StopCompletelyBy("Buff/WandSwap");
            _combatTeardownDoneForCurrentBuffCycle = true;
            _host.Log($"[BuffPre] CancelAttack+StopCompletely before wand equip (mode was {CurrentCombatMode})");
        }

        // FIX: stock ACE will NOT auto-dequip the bow for a Held-slot wand
        // (CheckWeaponCollision refuses while mainhand != null). Stow the wielded
        // non-wand weapon into a capacity-verified open pack FIRST (AutoCram pattern,
        // AV-safe), then UseObject(wand) once the hand is free. Fully bounded.
        int bowId = FindWieldedNonWandWeapon(wandId);
        if (bowId != 0 && (PackFirst || !IsWieldedLive(bowId)))   // else the use swaps (see _directSwapMisses)
        {
            if (_bowDequipPendingId == bowId
                && (now - _bowDequipAt).TotalMilliseconds < WieldResolveTimeoutMs
                && IsWieldedLive(bowId))
                return false; // dequip still resolving

            if (IsWieldedLive(bowId))
            {
                int openPack = WorldObjectCache.FindPackFor(_host, _worldObjectCache, includeMainPack: true, requireFree: 1);
                if (openPack == 0 || _bowDequipAttempts >= BowDequipMaxAttempts)
                {
                    // No verified-open pack to receive the bow, or repeated dequip
                    // failures -> the swap provably can't complete now. Degrade.
                    _host.Log($"[WieldGate] bow 0x{(uint)bowId:X8} dequip blocked (openPack=0x{(uint)openPack:X8}, attempts={_bowDequipAttempts}/{BowDequipMaxAttempts}) — degrading");
                    if (openPack == 0)
                        WorldObjectCache.WarnPackFull(_host, "can't put the weapon away to switch to the wand, so buffs and spell heals can't cast");
                    return SignalSwapFailure(forBuff, bowId, wandId);
                }
                _bowDequipAttempts++;
                if (_worldObjectCache?[bowId] is WorldObject blocker && IsInOffhandSlot(blocker))
                    OffhandPutAway(bowId);   // a shield: combat puts it back after (and it is buffed in the pack)
                _host.MoveItemInternal((uint)bowId, (uint)openPack, 0, 1); // amount>=1 (engine rejects 0)
                _weaponSwapGate?.NoteMove(bowId, wieldedNow: true);
                _bowDequipPendingId = bowId;
                _bowDequipAt = now;
                _lastCastAttempt = now;
                _host.Log($"[WieldGate] dequip bow 0x{(uint)bowId:X8} -> pack 0x{(uint)openPack:X8} (attempt {_bowDequipAttempts}/{BowDequipMaxAttempts}) before wand equip");
                return false; // yield until the bow is out of hand
            }
            _bowDequipPendingId = 0; // bow confirmed unwielded — fall through to wield the wand
        }

        _pendingWieldDirect = bowId != 0 && !PackFirst;
        // Hands free: wield the caster into the Held slot (GetAndWieldItem, the paperdoll drag),
        // as the off hand does. A use only wields a plain caster: a caster with a spell of its own
        // (Electric Baton, Acid Staff) takes the use as "cast it" and the client says it must be
        // wielded first (Drakkon, 2026-10-18 14:00). With a weapon still in hand the use stays
        // (the client swaps); when that misses, the next swap empties the hands first.
        if (!_pendingWieldDirect && _host.HasWieldItem)
        {
            _host.WieldItem((uint)wandId, HeldSlot);
            _host.Log($"[WieldGate] wielding 0x{(uint)wandId:X8} '{_worldObjectCache?[wandId]?.Name}' into the Held slot");
        }
        else
            _host.UseFor((uint)wandId, "Buff", _pendingWieldDirect ? "buffing: switch to the wand" : "buffing: wield the wand");
        NoteStanceRequest();
        _pendingWieldClaim = _weaponSwapGate?.Claims ?? 0;
        _pendingWieldId = wandId;
        _pendingWieldAt = now;
        _lastCastAttempt = now;
        return false; // Yield — let the server equip the wand
    }

    /// <summary>EnsureMagicMode's first step once the wand reads wielded, before 2026-10-22.</summary>
    private void LegacyWandWielded(int wandId)
    {
        if (_pendingWieldId == wandId)
        {
            _pendingWieldId = 0;
            _wieldCooldownUntil = DateTime.MinValue;
            if (_pendingWieldDirect) _directSwapMisses = 0;   // the use swapped: back to direct swaps
            _packFirstNext = false;                           // a pack-route swap done: the next one tries the use
        }
    }


    // The currently-wielded non-wand weapon (the bow) that blocks the Held-slot wand; else
    // what is in the off hand (a shield, or an off-hand weapon): ACE refuses a Held-slot caster
    // beside either. With the global Offhand setting None the off hand is never touched (the
    // wand then can't be wielded; logged once).
    private int FindWieldedNonWandWeapon(int wandId)
    {
        if (_worldObjectCache == null) return 0;
        int offhand = 0;
        foreach (var wo in _worldObjectCache.GetDirectInventory(forceRefresh: true))
        {
            if (wo.Id == wandId) continue;
            if (IsWandObject(wo)) continue;
            if (WorldObjectCache.IsAmmo(wo)) continue;   // arrows stay on; only the bow blocks the wand
            if (IsInOffhandSlot(wo)) { offhand = wo.Id; continue; }
            if ((wo.ObjectClass == AcObjectClass.MeleeWeapon
                 || wo.ObjectClass == AcObjectClass.MissileWeapon)
                && WorldObjectCache.IsWieldedByPlayer(_host, wo))
                return wo.Id;
        }
        if (offhand != 0 && OffhandRules.Parse(_settings.OffhandDefault, OffhandMode.Auto) == OffhandMode.None)
        {
            if (!_offhandNoneNoted)
            {
                _offhandNoneNoted = true;
                _host.Log($"[WieldGate] 0x{(uint)offhand:X8} '{_worldObjectCache[offhand]?.Name}' is in the off hand and Offhand is None — leaving it; the server won't wield the wand beside it");
            }
            return 0;
        }
        return offhand;
    }

    private static bool IsInOffhandSlot(WorldObject wo) =>
        (wo.Values(LongValueKey.CurrentWieldedLocation, 0) & OffhandRules.ShieldSlot) != 0;
}

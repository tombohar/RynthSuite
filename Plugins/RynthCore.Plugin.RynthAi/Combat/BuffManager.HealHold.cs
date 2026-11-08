// ============================================================================
//  RynthSuite - Combat/BuffManager.HealHold.cs
//  The Buffing hold for a heal, capped when no heal is landing.
//
//  Health under a heal line wins the arbiter by itself (WantsVitalRecharge, ahead of combat and
//  nav). That is right while heals land, and wrong when none does: a melee character with a shield
//  went to Buffing at 48% health, stopped attacking the boss, and stood there 17 s re-sending a
//  healing kit the server no longer had until it died. Nothing healed, and nothing fought either.
//
//  VirindiTank's give-up for one kit: re-sent until the heal motion starts, given up after 3 s,
//  then up to 3 s for its result. So: HealHoldGiveUpMs (6 s) under a heal line with no heal landing
//  (health up FailsafeRiseMinPct, the HP failsafe's own test) hands combat the tick for
//  HealHoldReleaseMs (5 s, HealUnavailableMs, the backoff after "nothing could heal"), then the
//  vitals hold it again. A heal landing, or health back over the line, ends the episode. Only in a
//  fight (InCombatNow): with no monster in range there is nothing for combat to do with the tick.
//
//  The vitals step still runs every tick meanwhile (OnHeartbeat runs whatever the arbiter decides):
//  kits and potions keep going. What it does not do while combat has the tick is change stance or
//  wield for a heal (a wand swap for Heal Self, peace mode for a kit, in any stance): combat would
//  take the weapon back on its next tick and the two would trade the hands for nothing. A caster
//  already in Magic stance casts as before. The cast in flight (PendingSpellId) still holds the
//  arbiter by itself.
//
//  The safety floor is untouched: the kit despite Kit Min Success (AttemptHealChain's last resort)
//  still goes, in the stance the character is in, and the HP failsafe's recall casts with the macro
//  off, where this hold is reset and never applies.
//
//  Every start and end of a release, and the first stance change it refuses in each release, is
//  logged with "heal hold" in the line.
// ============================================================================

using System;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    internal const double HealHoldGiveUpMs = 6000;
    internal const double HealHoldReleaseMs = 5000;

    private DateTime _hhSince = DateTime.MinValue;
    private int _hhLow;
    private DateTime _hhReleasedUntil = DateTime.MinValue;
    // Set for one CheckVitals while combat has the tick and the character is out of Magic stance.
    private bool _vitalsNoStanceChange;
    // Set for one CheckVitals while combat has the tick, in any stance: a kit sends no peace switch.
    private bool _vitalsNoPeaceSwitch;
    // What the current release already logged refusing (one line per kind per release).
    private bool _hhSwapRefusedLogged, _hhPeaceRefusedLogged;

    /// <summary>Times the hold was handed to combat (tests, status).</summary>
    internal int HealHoldReleases { get; private set; }

    /// <summary>True while combat has the tick because no heal has been landing (see the header).</summary>
    internal bool HealHoldReleased => _hhReleasedUntil != DateTime.MinValue && Now < _hhReleasedUntil;

    /// <summary>
    /// A heal already under way that a release would starve: a kit in flight whose heal motion has
    /// played (NoteKitProgress), or the wand swap for a heal spell in flight (TickWandSwap's state, or
    /// the wand's wield sent and not answered).
    /// </summary>
    private bool HealUnderWay() =>
        (_kitUsedAt != DateTime.MinValue && _kitLandedAt == DateTime.MinValue && _kitSawAnimating)
        || _wandSwap.Active || _pendingWieldId != 0;

    /// <summary>Ends a hold episode: the next heal line starts a fresh HealHoldGiveUpMs count.</summary>
    private void ResetHealHold(string why)
    {
        if (_hhReleasedUntil != DateTime.MinValue && Now < _hhReleasedUntil)
            _host.Log($"[RynthAi] Vitals: heal hold ended ({why}): the vitals hold the tick again.");
        _hhSince = DateTime.MinValue;
        _hhReleasedUntil = DateTime.MinValue;
    }

    /// <summary>The vitals step refused a stance change because combat has the tick (logged once per release).</summary>
    private void NoteHealHoldRefusal(bool peace, string what)
    {
        if (peace ? _hhPeaceRefusedLogged : _hhSwapRefusedLogged) return;
        if (peace) _hhPeaceRefusedLogged = true; else _hhSwapRefusedLogged = true;
        _host.Log($"[RynthAi] Vitals: heal hold: no {(peace ? "peace switch" : "wand swap")} for {what} while combat has the tick " +
                  $"({(_hhReleasedUntil - Now).TotalSeconds:0.#} s left).");
    }

    /// <summary>Once per heartbeat while the macro runs, before the vitals step.</summary>
    private void TickHealHold()
    {
        DateTime now = Now;
        int hp = _vitals.HealthPct;
        // Only in a fight (a monster within Monster Range): with nothing to fight, handing the tick on
        // would give it to nav or looting at low health, walking on into the next pull.
        // The macro being off is handled by the caller: HeartbeatCore returns before this tick then, and
        // calls ResetHealHold instead, so a restart in a fight starts a fresh count.
        if (_vitals.MaxHealth == 0 || Portaling()) { ResetHealHold("portal space or no vitals"); return; }
        if (!HealthUnderAnyLine(hp, _vitals.StaminaPct)) { ResetHealHold($"health {hp}% is over the heal lines"); return; }
        if (!InCombatNow()) { ResetHealHold("no monster within Monster Range"); return; }
        if (_hhSince == DateTime.MinValue)
        {
            _hhSince = now;
            _hhLow = hp;
            return;
        }
        if (hp >= _hhLow + FailsafeRiseMinPct)
        {
            // A heal landed: the heals work, the vitals hold the tick again and the count starts over.
            if (HealHoldReleased)
                _host.Log($"[RynthAi] Vitals: heal hold ended (a heal landed, {_hhLow}% -> {hp}%): the vitals hold the tick again.");
            _hhSince = now;
            _hhLow = hp;
            _hhReleasedUntil = DateTime.MinValue;
            return;
        }
        if (hp < _hhLow) _hhLow = hp;
        if (_hhReleasedUntil != DateTime.MinValue)
        {
            if (now < _hhReleasedUntil) return;
            // The window is over: hold again, and count the next HealHoldGiveUpMs from here.
            _host.Log($"[RynthAi] Vitals: heal hold ended (its {HealHoldReleaseMs / 1000:0} s are over, health {hp}%): the vitals hold the tick again.");
            _hhReleasedUntil = DateTime.MinValue;
            _hhSince = now;
            return;
        }
        double heldMs = (now - _hhSince).TotalMilliseconds;
        if (heldMs < HealHoldGiveUpMs) return;
        // A heal the server is playing is not cut off: combat would re-stance or re-wield over it. The
        // release waits for it (a removed kit never animates, so that case still releases).
        if (HealUnderWay()) return;
        _hhReleasedUntil = now.AddMilliseconds(HealHoldReleaseMs);
        _hhSwapRefusedLogged = _hhPeaceRefusedLogged = false;
        HealHoldReleases++;
        _host.Log($"[RynthAi] Vitals: heal hold started: health {hp}% under a heal line and no heal landed in {heldMs / 1000:0.#} s: " +
                  $"combat gets the tick for {HealHoldReleaseMs / 1000:0} s (kits and potions keep going; no stance change for a heal).");
    }
}

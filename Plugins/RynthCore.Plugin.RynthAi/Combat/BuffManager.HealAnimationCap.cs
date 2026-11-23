// ============================================================================
//  RynthSuite - Combat/BuffManager.HealAnimationCap.cs
//  A heal under a heal line waits on the character's animation for HealAnimationCapMs at most.
//
//  The vitals step waits while the client's motion queue reads busy (ActionPacer.GestureHolds) and
//  while the engine's cast gate is closed (CastGateWatchdog). Both limits (4 s, 1.5 s at Emergency
//  Heal At) count one unbroken busy spell. A monster hitting the character plays a recoil on every
//  hit, so the queue reads busy, clear for a moment, busy again: every spell starts the count from
//  0 and the limit never comes, while a tick that happens to read busy holds the heal each time.
//
//  Here the time a heal is held by the animation counts all told, across those clear moments, from
//  the first such hold until the vitals step is reached (a gate let it through) or health is over
//  every heal line. Past HealAnimationCapMs the heal skips both animation gates (logged once per
//  hold). Every other gate keeps its rules: an unanswered action of ours, the busy count, a cast in
//  flight, the player's turn.
// ============================================================================

using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    /// <summary>The longest a heal under a heal line waits on the character's animation, all told.</summary>
    internal const double HealAnimationCapMs = 1000;

    private DateTime _healAnimHeldSince = DateTime.MinValue;
    private bool _healAnimCapLogged;

    /// <summary>Times a heal went on without the animation gates (tests, status).</summary>
    internal int HealAnimationCaps { get; private set; }

    /// <summary>The pacer's hold reasons that are the character's animation (ActionPacer.GestureHolds / SwingHolds).</summary>
    internal static bool IsAnimationHold(string why) =>
        why.StartsWith("the character is still animating", StringComparison.Ordinal)
        || why.StartsWith("a swing sent", StringComparison.Ordinal);

    /// <summary>True when a heal is due and the animation has held it HealAnimationCapMs or more.</summary>
    private bool HealAnimationCapReached()
    {
        if (HealNeedNow() == HealNeed.None) { EndHealAnimationHold(); return false; }
        if (_healAnimHeldSince == DateTime.MinValue) return false;
        double ms = (RynthClock.Now - _healAnimHeldSince).TotalMilliseconds;
        if (ms < HealAnimationCapMs) return false;
        if (!_healAnimCapLogged)
        {
            _healAnimCapLogged = true;
            HealAnimationCaps++;
            string line = ActiveHealLine(_vitals.HealthPct, _vitals.StaminaPct, out int value, out _);
            _host.Log($"[RynthAi] Vitals: the heal has waited {ms:0} ms on the character's animation " +
                      $"(health {_vitals.HealthPct}% under {line} {value}%): it goes on without the animation gate.");
        }
        return true;
    }

    /// <summary>The vitals step was held this tick; <paramref name="animation"/>: by the character's animation.</summary>
    private void NoteHealAnimationHold(bool animation)
    {
        if (!animation || _healAnimHeldSince != DateTime.MinValue) return;
        if (HealNeedNow() == HealNeed.None) return;
        _healAnimHeldSince = RynthClock.Now;
    }

    /// <summary>The vitals step was reached, or no heal is due: the count starts again at the next hold.</summary>
    private void EndHealAnimationHold()
    {
        _healAnimHeldSince = DateTime.MinValue;
        _healAnimCapLogged = false;
    }
}

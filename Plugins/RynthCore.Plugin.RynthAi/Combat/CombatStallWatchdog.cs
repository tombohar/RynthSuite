using System;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Safety net for the melee/missile "stands there in combat mode, not attacking" stall
/// (2026-09-29, Lucy in the Olthoi swarm). The root cause is fixed in CombatManager
/// (the server's attack loop stayed bound to an old, unreachable target and ACE drops every
/// new attack request while that target lives); this catches anything else that leaves
/// the bot issuing attacks that never become swings.
///
/// Pure timing/ladder state. CombatManager decides whether the situation is eligible
/// (action Combat, live target locked and in range, Melee or Missile mode), feeds the time
/// of the last swing evidence (a hit, a miss or a kill), and carries out the step returned.
/// Pump thread only; no timers — it only advances when Tick is called.
///
/// Escalation, one step per call that returns it:
///   1 Reissue    — CancelAttack, the attack goes out again on the next cycle
///   2 Reselect   — CancelAttack, drop the lock so targeting picks again
///   3 ToggleMode — peace and back (what the wand heal does: the mode change ends the
///                  server's attack)
///   4 Rewield    — put the weapon away and wield it again
/// After step 4 with still no swings the ladder rests for <see cref="CooldownMs"/>.
/// </summary>
internal sealed class CombatStallWatchdog
{
    /// <summary>No swing evidence for this long while eligible = stalled. Normal melee in
    /// the 2026-09-29 log swings every 1-2 s (busy pulses, hits, "evaded your attack"); the
    /// real stalls ran 12-49 s.</summary>
    public const double StallMs = 6000;

    /// <summary>Melee that is plainly stuck - attacks sent, no swing playing, the character
    /// standing still (CombatManager.SampleMeleeQuiet) - gets step 1 after this instead (Drakkon
    /// 2026-10-12: the server's move-to the target stalled 2 m short; the cancel 6 s later fixed
    /// it at once every time, so most of the 7-9 s per stall was the wait).</summary>
    public const double QuietStallMs = 2500;

    /// <summary>
    /// The stall time for a missile attack at point blank (target within
    /// <see cref="PointBlankMissileM"/>). Silentkelpie 2026-10-14: 22 of 40 missile stalls were
    /// at 1.5-2.5 m, and step 1 (re-issue) brought the swings back within half a second in most
    /// of them, so the 6 s wait was the whole cost. A bow or crossbow there shows a hit or miss
    /// every 1.5-2 s.
    /// </summary>
    public const double PointBlankStallMs = 3500;

    /// <summary>Missile targets this close (metres) use <see cref="PointBlankStallMs"/>.</summary>
    public const double PointBlankMissileM = 3.0;

    /// <summary>Time allowed for each step to show a swing before the next one.</summary>
    public const double StepMs = 3000;

    /// <summary>Rest after a full ladder that didn't help, so the watchdog can't churn.</summary>
    public const double CooldownMs = 30000;

    /// <summary>Tick gap that means combat wasn't running (buffing, looting, nav took the
    /// arbiter, or combat went ineligible): start the episode over instead of counting the
    /// gap as stalled time.</summary>
    public const double TickGapResetMs = 1500;

    public enum StallAction { None, Reissue, Reselect, ToggleMode, Rewield, Recovered, GaveUp }

    private DateTime _eligibleSince = DateTime.MinValue;
    private DateTime _lastTick      = DateTime.MinValue;
    private DateTime _lastStepAt    = DateTime.MinValue;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private int      _step;          // steps fired in the current episode (0 = none)
    private DateTime _prevStepAt    = DateTime.MinValue;   // _lastStepAt before the newest Fire (for Unfire)
    private DateTime _stallRef      = DateTime.MinValue;   // last swing (or episode start) before the stall

    /// <summary>Episodes where the watchdog acted (diagnostics / status).</summary>
    public int Episodes { get; private set; }
    /// <summary>Episodes that ended with swings resuming after a step.</summary>
    public int Recoveries { get; private set; }
    /// <summary>The last step fired in the current episode (0 = none).</summary>
    public int CurrentStep => _step;
    /// <summary>The episode's step 1 fired on <see cref="QuietStallMs"/> rather than <see cref="StallMs"/>.</summary>
    public bool LastFireWasEarly { get; private set; }
    public static int StepCount => 4;

    /// <summary>
    /// Advance the watchdog. <paramref name="eligible"/> is the full precondition set;
    /// <paramref name="lastEvidenceAt"/> the last hit, miss or kill. <paramref name="quiet"/>:
    /// the caller sees no swing playing and the character standing still (step 1 after
    /// <see cref="QuietStallMs"/>). <paramref name="stallAfterMs"/>: how long without evidence
    /// starts an episode otherwise (0 = <see cref="StallMs"/>). Returns the step to carry out now
    /// (or Recovered / GaveUp for logging), with the stalled time.
    /// </summary>
    public StallAction Tick(DateTime now, bool eligible, DateTime lastEvidenceAt, out double stalledMs,
                            bool quiet = false, double stallAfterMs = 0)
    {
        if (stallAfterMs <= 0) stallAfterMs = StallMs;
        stalledMs = 0;
        bool gap = _lastTick != DateTime.MinValue
                   && (now - _lastTick).TotalMilliseconds > TickGapResetMs;
        _lastTick = now;

        if (!eligible || gap)
        {
            ResetEpisode();
            if (!eligible) return StallAction.None;
        }

        if (_eligibleSince == DateTime.MinValue)
            _eligibleSince = now;

        DateTime refAt = lastEvidenceAt > _eligibleSince ? lastEvidenceAt : _eligibleSince;
        stalledMs = (now - refAt).TotalMilliseconds;

        // Swings came back after a step: the episode is over.
        if (_step > 0 && lastEvidenceAt > _lastStepAt)
        {
            stalledMs = (lastEvidenceAt - _stallRef).TotalMilliseconds;
            Recoveries++;
            ResetEpisode();
            _eligibleSince = now;
            return StallAction.Recovered;
        }

        if (now < _cooldownUntil)
            return StallAction.None;

        if (_step == 0)
        {
            bool early = quiet && stalledMs >= QuietStallMs;
            if (stalledMs < stallAfterMs && !early) return StallAction.None;
            LastFireWasEarly = stalledMs < stallAfterMs;
            Episodes++;
            _stallRef = refAt;
            return Fire(now);
        }

        if ((now - _lastStepAt).TotalMilliseconds < StepMs)
            return StallAction.None;

        if (_step >= StepCount)
        {
            _cooldownUntil = now.AddMilliseconds(CooldownMs);
            ResetEpisode();
            _eligibleSince = now;
            return StallAction.GaveUp;
        }
        return Fire(now);
    }

    /// <summary>Keep the episode alive through ticks where combat holds on purpose for a
    /// step still settling (the step-4 re-wield), so the hold isn't read as a tick gap.</summary>
    public void Hold(DateTime now) => _lastTick = now;

    private StallAction Fire(DateTime now)
    {
        _step++;
        _prevStepAt = _lastStepAt;
        _lastStepAt = now;
        return _step switch
        {
            1 => StallAction.Reissue,
            2 => StallAction.Reselect,
            3 => StallAction.ToggleMode,
            _ => StallAction.Rewield,
        };
    }

    /// <summary>Take back the step just returned because the caller could not carry it out (the engine refused the mode change):
    /// a refusal spends no ladder step. The ladder returns to where it was, so the step fires again on the next tick.</summary>
    public void Unfire()
    {
        if (_step <= 0) return;
        _step--;
        _lastStepAt = _prevStepAt;
        if (_step == 0 && Episodes > 0) Episodes--;
    }

    private void ResetEpisode()
    {
        _eligibleSince = DateTime.MinValue;
        _lastStepAt    = DateTime.MinValue;
        _step          = 0;
    }
}

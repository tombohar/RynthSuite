using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiTests.Harness;
using Act = RynthCore.Plugin.RynthAi.CombatStallWatchdog.StallAction;

namespace RynthCore.RynthAiTests.Tests;

// Smoke tests for the melee/missile stall ladder: 6 s with no swing evidence starts it, one
// step per 3 s (Reissue, Reselect, ToggleMode, Rewield), then GaveUp and a 30 s rest; a swing
// after a step ends the episode (Recovered); a tick gap over 1.5 s starts the count over.
// Time is passed in, so the whole ladder runs in microseconds.
internal static class CombatStallWatchdogTests
{
    private static readonly DateTime T0 = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime NoEvidence = DateTime.MinValue;
    private const int TickMs = 500;

    public static void Register(Runner r)
    {
        r.Add("stall: full ladder, give up, then rest", FullLadder);
        r.Add("stall: a swing after a step is a recovery", Recovery);
        r.Add("stall: a tick gap starts the count over", TickGapResets);
        r.Add("stall: ineligible never acts", Ineligible);
        r.Add("stall: Unfire takes a step back - the same step is due again next tick, not 3 s later", UnfireRetries);
        r.Add("stall: a missile target at point blank starts the ladder at 3.5 s", PointBlank);
        r.Add("point blank facing: one snap only for a close target behind, after a full shot cycle", PointBlankFacingRules);
    }

    /// <summary>
    /// PointBlankFacing (Kelpie 2026-10-14/15: crossbow at 1.7 m, target 177 degrees behind, no
    /// shots until the target was dropped): snap only within 3 m, more than 90 degrees off, after
    /// 2.5 s with no shot, once per 3 s per target, then hold 300 ms.
    /// </summary>
    private static void PointBlankFacingRules()
    {
        var f = new PointBlankFacing();
        DateTime shot = T0;
        DateTime now = T0.AddMilliseconds(2600);
        Check.True(f.ShouldSnap(now, 7, 1.7, 177, shot), "1.7 m, 177 degrees off, 2.6 s since a shot: snap");
        Check.False(f.ShouldSnap(T0.AddMilliseconds(2000), 7, 1.7, 177, shot), "a shot 2 s ago may still be in flight: wait");
        Check.False(f.ShouldSnap(now, 7, 3.5, 177, shot), "beyond 3 m the server turns the character itself");
        Check.False(f.ShouldSnap(now, 7, 1.7, 80, shot), "80 degrees off: left to the server");
        Check.False(f.ShouldSnap(now, 0, 1.7, 177, shot), "no target");
        Check.True(f.ShouldSnap(now, 7, 1.7, 177, NoEvidence), "no shot ever: snap");

        f.Snapped(now, 7);
        Check.True(f.Settling(now.AddMilliseconds(200), 7), "the attack waits 300 ms after the snap");
        Check.False(f.Settling(now.AddMilliseconds(350), 7), "then goes");
        Check.False(f.Settling(now.AddMilliseconds(200), 8), "another target isn't held");
        Check.False(f.ShouldSnap(now.AddMilliseconds(2000), 7, 1.7, 177, shot), "no second snap for the same target within 3 s");
        Check.True(f.ShouldSnap(now.AddMilliseconds(3100), 7, 1.7, 177, shot), "after 3 s it may snap again");
        Check.True(f.ShouldSnap(now.AddMilliseconds(500), 8, 1.7, 177, shot), "a new target may snap at once");
        Check.Eq(f.Snaps, 1, "one snap counted");
    }

    /// <summary>The caller passes PointBlankStallMs for a missile target within 3 m (Kelpie 2026-10-14).</summary>
    private static void PointBlank()
    {
        var w = new CombatStallWatchdog();
        Act first = Act.None;
        int at = -1;
        for (int t = 0; t <= 10_000 && first == Act.None; t += TickMs)
        {
            first = w.Tick(T0.AddMilliseconds(t), eligible: true, NoEvidence, out _,
                           stallAfterMs: CombatStallWatchdog.PointBlankStallMs);
            if (first != Act.None) at = t;
        }
        Check.Eq((first, at), (Act.Reissue, 3500), "step 1 after 3.5 s at point blank");

        var normal = new CombatStallWatchdog();
        Check.Eq(RunUntilAction(normal, 0, 10_000, NoEvidence), (Act.Reissue, 6000), "the default is still 6 s");
    }

    /// <summary>Ticks every 500 ms from <paramref name="fromMs"/> to <paramref name="toMs"/>
    /// inclusive; returns the first non-None action and when it happened (-1 = none).</summary>
    private static (Act Action, int AtMs) RunUntilAction(CombatStallWatchdog w, int fromMs, int toMs, DateTime evidence)
    {
        for (int t = fromMs; t <= toMs; t += TickMs)
        {
            var a = w.Tick(T0.AddMilliseconds(t), eligible: true, evidence, out _);
            if (a != Act.None) return (a, t);
        }
        return (Act.None, -1);
    }

    private static void FullLadder()
    {
        var w = new CombatStallWatchdog();

        Check.Eq(RunUntilAction(w, 0, 20_000, NoEvidence), (Act.Reissue, 6000), "step 1 after 6 s without swings");
        Check.Eq(w.Episodes, 1, "one episode");
        Check.Eq(w.CurrentStep, 1, "current step 1");
        Check.Eq(RunUntilAction(w, 6500, 20_000, NoEvidence), (Act.Reselect, 9000), "step 2 three seconds later");
        Check.Eq(RunUntilAction(w, 9500, 20_000, NoEvidence), (Act.ToggleMode, 12000), "step 3");
        Check.Eq(RunUntilAction(w, 12500, 20_000, NoEvidence), (Act.Rewield, 15000), "step 4");
        Check.Eq(RunUntilAction(w, 15500, 20_000, NoEvidence), (Act.GaveUp, 18000), "gave up after step 4");
        Check.Eq(w.CurrentStep, 0, "episode reset after giving up");

        // Rest: nothing for 30 s, then the next episode starts at once (it has been stalled since 18 s).
        Check.Eq(RunUntilAction(w, 18500, 47_500, NoEvidence), (Act.None, -1), "resting for 30 s");
        Check.Eq(RunUntilAction(w, 48_000, 48_000, NoEvidence), (Act.Reissue, 48_000), "new episode after the rest");
        Check.Eq(w.Episodes, 2, "two episodes");
        Check.Eq(w.Recoveries, 0, "no recoveries");
    }

    private static void Recovery()
    {
        var w = new CombatStallWatchdog();
        Check.Eq(RunUntilAction(w, 0, 10_000, NoEvidence), (Act.Reissue, 6000), "step 1");

        DateTime swing = T0.AddMilliseconds(7000);
        var a = w.Tick(swing, eligible: true, swing, out double stalledMs);
        Check.Eq(a, Act.Recovered, "a hit after the step ends the episode");
        Check.Near(stalledMs, 7000, 0.001, "stalled time runs from the episode start to the swing");
        Check.Eq(w.Recoveries, 1, "one recovery");
        Check.Eq(w.CurrentStep, 0, "episode reset");

        // Regular swings keep it quiet.
        int acted = 0;
        for (int t = 7500; t <= 30_000; t += TickMs)
        {
            DateTime now = T0.AddMilliseconds(t);
            if (w.Tick(now, true, now.AddMilliseconds(-1000), out _) != Act.None) acted++;
        }
        Check.Eq(acted, 0, "no steps while swinging every second");
    }

    private static void TickGapResets()
    {
        var w = new CombatStallWatchdog();
        Check.Eq(RunUntilAction(w, 0, 5000, NoEvidence), (Act.None, -1), "5 s: not yet stalled");
        // 2 s without a tick (combat paused for looting): the count starts over at 7 s.
        Check.Eq(RunUntilAction(w, 7000, 12_500, NoEvidence), (Act.None, -1), "a gap restarts the 6 s count");
        Check.Eq(RunUntilAction(w, 13_000, 13_000, NoEvidence), (Act.Reissue, 13_000), "stalled 6 s after the gap");
    }

    private static void Ineligible()
    {
        var w = new CombatStallWatchdog();
        int acted = 0;
        for (int t = 0; t <= 60_000; t += TickMs)
            if (w.Tick(T0.AddMilliseconds(t), eligible: false, NoEvidence, out _) != Act.None) acted++;
        Check.Eq(acted, 0, "no action in a minute of ineligible ticks");
        Check.Eq(w.Episodes, 0, "no episodes");
    }

    private static void UnfireRetries()
    {
        var w = new CombatStallWatchdog();
        Check.Eq(RunUntilAction(w, 0, 20_000, NoEvidence), (Act.Reissue, 6000), "step 1");
        Check.Eq(RunUntilAction(w, 6500, 20_000, NoEvidence), (Act.Reselect, 9000), "step 2");
        Check.Eq(RunUntilAction(w, 9500, 20_000, NoEvidence), (Act.ToggleMode, 12000), "step 3 fires");

        w.Unfire();   // the engine refused the toggle
        Check.Eq(w.CurrentStep, 2, "Unfire: back at step 2");
        Check.Eq(RunUntilAction(w, 12_500, 20_000, NoEvidence), (Act.ToggleMode, 12_500), "the toggle is due again on the very next tick");
        Check.Eq(RunUntilAction(w, 13_000, 20_000, NoEvidence), (Act.Rewield, 15_500), "an accepted toggle then spaces step 4 a full 3 s after it");

        // Unfire at step 1 gives the episode back too.
        var v = new CombatStallWatchdog();
        Check.Eq(RunUntilAction(v, 0, 10_000, NoEvidence), (Act.Reissue, 6000), "step 1");
        Check.Eq(v.Episodes, 1, "one episode");
        v.Unfire();
        Check.Eq(v.CurrentStep, 0, "back to no step");
        Check.Eq(v.Episodes, 0, "and the episode is not counted until a step really goes out");
        Check.Eq(RunUntilAction(v, 6500, 10_000, NoEvidence), (Act.Reissue, 6500), "step 1 again next tick");
        Check.Eq(v.Episodes, 1, "one episode");
    }
}
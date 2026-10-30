using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiTests.Harness;
using Step = RynthCore.Plugin.RynthAi.DeathRecallRun.Step;

namespace RynthCore.RynthAiHostTests.Tests;

// Recall On Death (DeathRecallRun): fires at once, retries a fizzle on its own clock (4 s spells,
// 6 s items), stops firing once portal space or a landblock change shows the teleport, finishes
// 3 s after landing, and gives up after 5 tries or 60 s. Not ready (no wand / not in magic mode)
// never fires.
internal static class DeathRecallTests
{
    private const uint Lifestone = 0xA9B4;

    public static void Register(Runner r)
    {
        r.Add("death recall: fires at once, then retries a fizzle every 4 s", RetriesSpell);
        r.Add("death recall: portal space stops the firing, done 3 s after landing", PortalThenDone);
        r.Add("death recall: a landblock change also counts as the teleport", LandblockChange);
        r.Add("death recall: gives up after 5 tries without a teleport; an item retries every 6 s", GivesUp);
        r.Add("death recall: not ready never fires, and times out after 60 s", NotReadyTimesOut);
    }

    private static void RetriesSpell()
    {
        var run = new DeathRecallRun(useItem: false, 0, Lifestone);
        Check.Eq(run.Tick(0, false, Lifestone, true), Step.Fire, "first tick fires");
        Check.Eq(run.Tick(3_999, false, Lifestone, true), Step.Wait, "no second cast before 4 s");
        Check.Eq(run.Tick(4_000, false, Lifestone, true), Step.Fire, "a fizzle is retried at 4 s");
        Check.Eq(run.Fires, 2, "two casts");
    }

    private static void PortalThenDone()
    {
        var run = new DeathRecallRun(useItem: false, 0, Lifestone);
        run.Tick(0, false, Lifestone, true);
        Check.Eq(run.Tick(2_000, true, 0, true), Step.Wait, "in portal space: wait");
        Check.Eq(run.Tick(9_000, true, 0, true), Step.Wait, "still in portal space: no recast");
        Check.Eq(run.Tick(10_000, false, Lifestone, true), Step.Wait, "landed: settle (same landblock is fine after portal space)");
        Check.Eq(run.Tick(12_999, false, Lifestone, true), Step.Wait, "still settling");
        Check.Eq(run.Tick(13_000, false, Lifestone, true), Step.Done, "done 3 s after landing");
        Check.Eq(run.Fires, 1, "cast once");
    }

    private static void LandblockChange()
    {
        var run = new DeathRecallRun(useItem: true, 0, Lifestone);
        run.Tick(0, false, Lifestone, true);
        Check.Eq(run.Tick(4_000, false, 0x7D64, true), Step.Wait, "moved landblock: settling, no second use");
        Check.Eq(run.Tick(7_000, false, 0x7D64, true), Step.Done, "done 3 s later");
        Check.True(run.Teleported, "teleported");
    }

    private static void GivesUp()
    {
        var run = new DeathRecallRun(useItem: true, 0, Lifestone);
        long t = 0;
        int fires = 0;
        for (int i = 0; i < 5; i++, t += 6_000)
            if (run.Tick(t, false, Lifestone, true) == Step.Fire) fires++;
        Check.Eq(fires, 5, "five uses, 6 s apart");
        Check.Eq(run.Tick(29_000, false, Lifestone, true), Step.Wait, "waits out the last try");
        Check.Eq(run.Tick(30_000, false, Lifestone, true), Step.Failed, "then gives up");
        Check.True(run.FailReason.Contains("5 tries"), "says why");
    }

    private static void NotReadyTimesOut()
    {
        var run = new DeathRecallRun(useItem: false, 0, Lifestone);
        Check.Eq(run.Tick(0, false, Lifestone, false), Step.Wait, "not ready: no cast");
        Check.Eq(run.Tick(30_000, false, Lifestone, false), Step.Wait, "still not ready");
        Check.Eq(run.Tick(60_001, false, Lifestone, false), Step.Failed, "60 s: gives up");
        Check.Eq(run.Fires, 0, "never cast");
        Check.True(run.FailReason.Contains("60 s"), "says why");
    }
}

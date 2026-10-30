using System;
using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-20: the Monsters tab's debuffs (custom ones too) were cast once per target and never
// again, so a long fight with a high-HP monster ran on without them once they wore off. A
// landed debuff is now timed by its spell's duration and cast again just before the end.
internal static class DebuffRenewTests
{
    private const int Mob = 0x7A001234, Other = 0x7A005678;

    public static void Register(Runner r)
    {
        r.Add("debuff renewal: spell durations come from the spell data (Imperil I 60 s, Incantation 300 s)", Durations);
        r.Add("debuff renewal: a landed debuff is due again just before it wears off", RenewsBeforeEnd);
        r.Add("debuff renewal: no duration casts once per target; three failures give up; a target switch keeps timers", OnceAndFailures);
        r.Add("debuff renewal: a monster that goes takes its timers with it", ForgetTarget);
    }

    private static void Durations()
    {
        Check.Eq(SpellDatabase.DurationSeconds(SpellDatabase.GetIdByName("Imperil Other I")), 60, "Imperil Other I lasts 60 s");
        Check.Eq(SpellDatabase.DurationSeconds(SpellDatabase.GetIdByName("Incantation of Imperil Other")), 300, "Incantation of Imperil Other 300 s");
        Check.Eq(SpellDatabase.DurationSeconds(SpellDatabase.GetIdByName("Corrosion VII")), 16, "a damage-over-time debuff (Corrosion VII) 16 s");
        Check.Eq(SpellDatabase.DurationSeconds(SpellDatabase.GetIdByName("Lifestone Recall")), 0, "a recall has none");
        Check.Eq(SpellDatabase.DurationSeconds(-1), 0, "an unknown id has none");
    }

    private static void RenewsBeforeEnd()
    {
        var t = new DebuffTimers();
        var now = new DateTime(2026, 10, 20, 20, 0, 0);
        Check.False(t.IsOn(Mob, "Imperil", now), "nothing cast yet: due");
        t.Landed(Mob, "Imperil", 240, now);
        Check.True(t.IsOn(Mob, "Imperil", now.AddSeconds(200)), "on at 200 s of 240");
        Check.True(t.IsOn(Mob, "Imperil", now.AddSeconds(234.9)), "still on 5 s before the end");
        Check.False(t.IsOn(Mob, "Imperil", now.AddSeconds(235)), "due 5 s before it wears off");
        Check.True(t.RenewAt(Mob, "Imperil") != null, "a renewal, not a first cast");
        Check.False(t.IsOn(Other, "Imperil", now), "another monster has its own");

        // A short damage-over-time debuff renews a fifth of its length early, not 5 s.
        t.Landed(Mob, "Custom:Corrosion", 16, now);
        Check.True(t.IsOn(Mob, "Custom:Corrosion", now.AddSeconds(12.7)), "Corrosion on at 12.7 s");
        Check.False(t.IsOn(Mob, "Custom:Corrosion", now.AddSeconds(12.8)), "and due at 12.8 s of 16");

        // The renewal lands: the timer starts over.
        t.Landed(Mob, "Imperil", 240, now.AddSeconds(236));
        Check.True(t.IsOn(Mob, "Imperil", now.AddSeconds(400)), "renewed at 236 s: on until 471 s");
        Check.False(t.IsOn(Mob, "Imperil", now.AddSeconds(471)), "due again at 471 s");
    }

    private static void OnceAndFailures()
    {
        var t = new DebuffTimers();
        var now = new DateTime(2026, 10, 20, 20, 0, 0);
        t.Landed(Mob, "Custom:Odd", 0, now);
        Check.True(t.IsOn(Mob, "Custom:Odd", now.AddHours(1)), "no duration: never again on this target");
        Check.True(t.RenewAt(Mob, "Custom:Odd") == null, "and no timer");

        Check.False(t.Failed(Mob, "Fester"), "first resist: try again");
        Check.False(t.Failed(Mob, "Fester"), "second: again");
        Check.False(t.IsOn(Mob, "Fester", now), "still due after two");
        Check.True(t.Failed(Mob, "Fester"), "third: given up");
        Check.True(t.IsOn(Mob, "Fester", now), "not cast at this target again");

        t.Landed(Mob, "Yield", 240, now);
        Check.False(t.Failed(Mob, "Yield"), "a renewal that fizzles once");
        t.Landed(Mob, "Yield", 240, now);
        Check.Eq(t.Failures(Mob, "Yield"), 0, "landing clears the count: each renewal gets three tries");

        // Fighting something else and coming back: the given-up and once-only marks start over,
        // the timers stay (the monster still has the debuff).
        t.NewTarget(now.AddSeconds(10));
        Check.False(t.IsOn(Mob, "Fester", now.AddSeconds(10)), "given up only for that engagement");
        Check.False(t.IsOn(Mob, "Custom:Odd", now.AddSeconds(10)), "once-only too");
        Check.True(t.IsOn(Mob, "Yield", now.AddSeconds(10)), "Yield's timer kept across the switch");
        t.NewTarget(now.AddSeconds(300));
        Check.Eq(t.TimerCount, 0, "a switch drops timers that fell due");
    }

    private static void ForgetTarget()
    {
        var t = new DebuffTimers();
        var now = new DateTime(2026, 10, 20, 20, 0, 0);
        t.Landed(Mob, "Imperil", 240, now);
        t.Landed(Mob, "Vuln:Fire", 240, now);
        t.Landed(Other, "Imperil", 240, now);
        t.Forget(Mob);
        Check.Eq(t.TimerCount, 1, "the gone monster's two timers dropped");
        Check.True(t.IsOn(Other, "Imperil", now), "the other monster keeps its own");
    }
}

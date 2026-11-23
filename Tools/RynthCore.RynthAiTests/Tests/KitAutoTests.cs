using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// Make kits when low (MiniRemote/Crafting/KitAuto.cs): when a run may start with the macro on (every
// blocker on its own), getting ready (peace mode, standing still, the timeout), what stops a run, and
// the waits after one (60 s after danger, 10 min after a run that made nothing or the player's Stop,
// a pack that can't make a kit said once).
internal static class KitAutoTests
{
    public static void Register(Runner r)
    {
        r.Add("kits auto: defaults - off, under 2 make up to 5, 35 yd, 60 s, 10 min", Defaults);
        r.Add("kits auto: a start needs every condition (each blocker on its own)", EachBlocker);
        r.Add("kits auto: getting ready - peace mode asked for, then standing still, then the start", Readying);
        r.Add("kits auto: getting ready gives up after the timeout, and on danger waits 60 s", ReadyTimeout);
        r.Add("kits auto: a running run stops on buffing, a fight, a heal line, a monster within the radius", StopReasons);
        r.Add("kits auto: the waits after a run (danger 60 s, nothing made or Stop 10 min, kits made 30 s)", Waits);
        r.Add("kits auto: a pack that can't make a kit is said once per reason, not every look", SaidOnce);
        r.Add("kits auto: the facts are read only when a look is due", LooksThrottled);
    }

    private sealed class Clock
    {
        public long Now = 10_000_000;
        public readonly List<string> Said = new();
        public readonly KitAuto A;
        public int Reads;
        public Clock()
        {
            A = new KitAuto();
            A.NowMs = () => Now;
            A.Say = Said.Add;
        }

        public KitAutoStep Step(KitAutoFacts f) => A.Decide(() => { Reads++; return f; });

        /// <summary>Ticks every 100 ms until a Start (or the limit); the steps seen.</summary>
        public List<KitAutoStep> RunToStart(KitAutoFacts f, int maxTicks = 100)
        {
            var steps = new List<KitAutoStep>();
            for (int i = 0; i < maxTicks; i++)
            {
                var s = Step(f);
                steps.Add(s);
                if (s == KitAutoStep.Start) break;
                Now += 100;
            }
            return steps;
        }
    }

    /// <summary>Everything clear: a start is due.</summary>
    private static KitAutoFacts Clear() => new()
    {
        Enabled = true,
        MacroOn = true,
        Kits = 1,
        Below = 2,
        InPeace = true,
        NearestMonsterYd = double.PositiveInfinity,
        RadiusYd = 35,
        SinceHurtMs = long.MaxValue,
    };

    private static void Defaults()
    {
        Check.Eq(KitAuto.DefaultBelow, 2, "starts under 2 kits");
        Check.Eq(KitAuto.DefaultUpTo, 5, "makes up to 5");
        Check.Eq(KitAuto.DefaultRadiusYd, 35.0, "no monster within 35 yd");
        Check.Eq(KitAuto.CooldownMs, 60_000L, "60 s after danger");
        Check.Eq(KitAuto.BackoffMs, 600_000L, "10 min after a run that made nothing");
        Check.Eq(KitAuto.HurtQuietMs, 20_000L, "no hit for 20 s");
        var c = new Clock();
        Check.Eq(c.A.Status, "off", "a new one is off");
        Check.Eq(KitAuto.Blocker(new KitAutoFacts()).Block, KitBlock.Off, "facts default to the setting off");
        Check.Eq(KitAuto.Blocker(Clear()).Block, KitBlock.None, "the clear facts start");
    }

    private static void EachBlocker()
    {
        var cases = new (string Name, Action<KitAutoFacts> Set, KitBlock Want, string Text)[]
        {
            ("setting off", f => f.Enabled = false, KitBlock.Off, "off"),
            ("macro off", f => f.MacroOn = false, KitBlock.MacroOff, "macro is off"),
            ("enough kits", f => f.Kits = 2, KitBlock.Enough, "2 kits carried"),
            ("more than enough", f => f.Kits = 9, KitBlock.Enough, "makes more under 2"),
            ("buffing", f => f.BusyWhy = "buffing or healing", KitBlock.Busy, "buffing"),
            ("looting", f => f.BusyWhy = "looting", KitBlock.Busy, "looting"),
            ("portal space", f => f.BusyWhy = "in portal space", KitBlock.Busy, "portal"),
            ("a heal line", f => f.VitalsWhy = "health 50% (Heal At 60%)", KitBlock.Vitals, "health 50%"),
            ("a monster at 22 yd", f => f.NearestMonsterYd = 22.4, KitBlock.Monster, "monster 22 yd away"),
            ("a monster at the radius", f => f.NearestMonsterYd = 35, KitBlock.Monster, "35 yd"),
            ("hit 5 s ago", f => f.SinceHurtMs = 5_000, KitBlock.Hurt, "hit 5 s ago"),
            ("out of bandages", f => f.PackWhy = "out of Soft Bandages", KitBlock.Pack, "Soft Bandages"),
            ("under the floor", f => f.PackWhy = "Pack the kit: 50% at Healing 200 is under your 90% floor", KitBlock.Pack, "floor"),
            ("untrained", f => f.PackWhy = "Healing isn't trained (the server refuses the combine)", KitBlock.Pack, "trained"),
        };
        foreach (var (name, set, want, text) in cases)
        {
            var f = Clear();
            set(f);
            var (block, why) = KitAuto.Blocker(f);
            Check.Eq(block, want, name + ": the blocker");
            Check.True(why.Contains(text), $"{name}: says why ('{why}')");
            var c = new Clock();
            Check.Eq(c.Step(f), KitAutoStep.Wait, name + ": no hold, no start");
            Check.False(c.A.Readying, name + ": not getting ready");
        }
        // Not blockers: a monster just outside the radius, an old hit, out of peace (that is getting ready).
        var ok = Clear();
        ok.NearestMonsterYd = 35.5;
        ok.SinceHurtMs = 20_000;
        Check.Eq(KitAuto.Blocker(ok).Block, KitBlock.None, "a monster at 35.5 yd and a hit 20 s ago don't stop a start");
        ok.InPeace = false;
        Check.Eq(KitAuto.Blocker(ok).Block, KitBlock.None, "combat mode isn't a blocker (it goes to peace)");
        // Below is clamped (a hand-edited 0 means 1).
        var zero = Clear();
        zero.Below = 0;
        zero.Kits = 0;
        Check.Eq(KitAuto.Blocker(zero).Block, KitBlock.None, "below 0 reads as 1: 0 kits starts");
    }

    private static void Readying()
    {
        var c = new Clock();
        var f = Clear();
        f.InPeace = false;
        Check.Eq(c.Step(f), KitAutoStep.GoPeace, "out of peace: ask for peace mode");
        Check.True(c.A.Readying, "getting ready");
        Check.Eq(c.A.Status, "going to peace mode", "says so");
        c.Now += 100;
        Check.Eq(c.Step(f), KitAutoStep.Hold, "asked: hold while it switches");
        c.Now += KitAuto.PeaceResendMs;
        Check.Eq(c.Step(f), KitAutoStep.GoPeace, "still not in peace after 2 s: asked again");
        f.InPeace = true;
        f.Moving = true;
        c.Now += 100;
        Check.Eq(c.Step(f), KitAutoStep.Hold, "in peace but still moving: hold");
        Check.Eq(c.A.Status, "stopping to make kits", "says so");
        f.Moving = false;
        c.Now += 100;
        Check.Eq(c.Step(f), KitAutoStep.Hold, "standing: hold for the settle second");
        c.Now += KitAuto.SettleMs - 200;
        Check.Eq(c.Step(f), KitAutoStep.Hold, "not a full second yet");
        c.Now += 200;
        Check.Eq(c.Step(f), KitAutoStep.Start, "a second still in peace: start");
        Check.False(c.A.Readying, "ready is over");
        c.A.Running(0, 5);
        Check.Eq(c.A.Status, "running 0/5", "running made/limit");
        c.A.Running(2, 5);
        Check.Eq(c.A.Status, "running 2/5", "running 2/5");

        // Already in peace and still: one second, then the start (no peace request).
        var c2 = new Clock();
        var steps = c2.RunToStart(Clear());
        Check.False(steps.Contains(KitAutoStep.GoPeace), "in peace already: no peace request");
        Check.Eq(steps.Last(), KitAutoStep.Start, "it starts");
        Check.True(steps.Count >= KitAuto.SettleMs / 100, $"after the settle second ({steps.Count} ticks)");
    }

    private static void ReadyTimeout()
    {
        var c = new Clock();
        var f = Clear();
        f.InPeace = false;   // never reaches peace
        var steps = new List<KitAutoStep>();
        for (int i = 0; i < 100 && (i == 0 || c.A.Readying); i++) { steps.Add(c.Step(f)); c.Now += 200; }
        Check.Eq(steps.Last(), KitAutoStep.Wait, "gives up");
        Check.False(steps.Contains(KitAutoStep.Start), "never started");
        Check.True(c.A.Status.Contains("couldn't go to peace mode"), "says why: " + c.A.Status);
        Check.True(c.A.NextCheckAt - c.Now >= KitAuto.CooldownMs - 400, "then waits 60 s");

        // A monster walks up while getting ready: the hold ends now, and the danger wait follows.
        var c2 = new Clock();
        var g = Clear();
        g.InPeace = false;
        Check.Eq(c2.Step(g), KitAutoStep.GoPeace, "getting ready");
        g.NearestMonsterYd = 20;
        c2.Now += 100;
        Check.Eq(c2.Step(g), KitAutoStep.Wait, "a monster: no more hold");
        Check.False(c2.A.Readying, "ready ended");
        Check.Eq(c2.A.NextCheckAt - c2.Now, KitAuto.CooldownMs, "60 s before the next look");
        g.NearestMonsterYd = double.PositiveInfinity;
        c2.Now += 30_000;
        Check.Eq(c2.Step(g), KitAutoStep.Wait, "30 s later, monster gone: still waiting");
        Check.True(c2.A.Status.Contains("next check in 30 s"), "the status says when: " + c2.A.Status);
        c2.Now += 30_000;
        Check.Eq(c2.Step(g), KitAutoStep.GoPeace, "60 s later: getting ready again");

        // Something else (looting) while getting ready: the short wait, not the cooldown.
        var c3 = new Clock();
        var h = Clear();
        h.InPeace = false;
        c3.Step(h);
        h.BusyWhy = "looting";
        c3.Now += 100;
        Check.Eq(c3.Step(h), KitAutoStep.Wait, "looting: no hold");
        Check.Eq(c3.A.NextCheckAt - c3.Now, KitAuto.CheckMs, "looked at again in 5 s");
    }

    private static void StopReasons()
    {
        var f = Clear();
        Check.Eq(KitAuto.StopReason(f), "", "nothing: goes on");
        f.NearestMonsterYd = 36;
        Check.Eq(KitAuto.StopReason(f), "", "a monster at 36 yd: goes on");
        f.NearestMonsterYd = 30.2;
        Check.Eq(KitAuto.StopReason(f), "a monster came within 30 yd", "a monster at 30 yd: stops");
        f = Clear();
        f.BusyWhy = "buffing or healing";
        Check.Eq(KitAuto.StopReason(f), "buffing or healing", "buffing: stops");
        f = Clear();
        f.BusyWhy = "a fight";
        Check.Eq(KitAuto.StopReason(f), "a fight", "a fight: stops");
        f = Clear();
        f.VitalsWhy = "health 55% (Heal At 60%)";
        Check.Eq(KitAuto.StopReason(f), "health 55% (Heal At 60%)", "a heal line: stops");
        f = Clear();
        f.Kits = 9;
        f.PackWhy = "out of Soft Bandages";
        Check.Eq(KitAuto.StopReason(f), "", "the count and the pack are KitRunner's (it finishes the step)");
    }

    private static void Waits()
    {
        // Danger: 60 s, said in the summary's tail.
        var c = new Clock();
        string tail = c.A.RunEnded(KitStopKind.Danger, 1);
        Check.Eq(c.A.NextCheckAt - c.Now, KitAuto.CooldownMs, "danger: 60 s");
        Check.True(tail.Contains("60 s"), "the summary says when: " + tail);
        Check.Eq(c.Step(Clear()), KitAutoStep.Wait, "no look during the cooldown");
        c.Now += KitAuto.CooldownMs;
        Check.Eq(c.Step(Clear()), KitAutoStep.Hold, "after it: getting ready again");

        // A run that made nothing (every combine failed, a refusal, the server's chance under the floor).
        foreach (var kind in new[] { KitStopKind.Finished, KitStopKind.Refused })
        {
            var n = new Clock();
            tail = n.A.RunEnded(kind, 0);
            Check.Eq(n.A.NextCheckAt - n.Now, KitAuto.BackoffMs, kind + " with nothing made: 10 min");
            Check.True(tail.Contains("10 min"), "the summary says 10 min: " + tail);
            n.Now += KitAuto.BackoffMs - 1;
            Check.Eq(n.Step(Clear()), KitAutoStep.Wait, "still waiting just before");
            n.Now += 1;
            Check.Eq(n.Step(Clear()), KitAutoStep.Hold, "then a look");
        }

        // The player's Stop: 10 min too.
        var u = new Clock();
        tail = u.A.RunEnded(KitStopKind.User, 2);
        Check.Eq(u.A.NextCheckAt - u.Now, KitAuto.BackoffMs, "Stop: 10 min");
        Check.True(tail.Contains("turn Make kits when low off"), "says how to stop it for good: " + tail);

        // Kits made: the next look in 30 s, nothing added to the line.
        var m = new Clock();
        tail = m.A.RunEnded(KitStopKind.Finished, 3);
        Check.Eq(tail, "", "a good run's line is the summary only");
        Check.Eq(m.A.NextCheckAt - m.Now, KitAuto.PackCheckMs, "the next look in 30 s");

        // The macro off: no wait.
        var o = new Clock();
        o.A.RunEnded(KitStopKind.MacroOff, 0);
        Check.Eq(o.A.NextCheckAt, 0L, "macro off: no wait");
        Check.Eq(o.A.Status, "waiting: the macro is off", "says so");

        // LookNow (the setting turned on, a number changed) skips a wait.
        var l = new Clock();
        l.A.RunEnded(KitStopKind.Refused, 0);
        l.A.LookNow();
        Check.Eq(l.Step(Clear()), KitAutoStep.Hold, "LookNow: a look on the next tick");
    }

    private static void SaidOnce()
    {
        var c = new Clock();
        var f = Clear();
        f.PackWhy = "out of Soft Bandages";
        for (int i = 0; i < 10; i++) { c.Step(f); c.Now += KitAuto.PackCheckMs; }
        Check.Eq(c.Said.Count, 1, "ten looks, one line: " + string.Join(" | ", c.Said));
        Check.True(c.Said[0].Contains("1 kit left") && c.Said[0].Contains("out of Soft Bandages"), "the line: " + c.Said[0]);
        f.PackWhy = "out of Treated Mandrake";
        c.Step(f);
        Check.Eq(c.Said.Count, 2, "a new reason: said once more");
        c.Now += KitAuto.PackCheckMs;
        c.Step(f);
        Check.Eq(c.Said.Count, 2, "and not again");
        // After a run that made kits, the same reason may be said again (the pack changed).
        c.A.RunEnded(KitStopKind.Finished, 1);
        c.Now += KitAuto.PackCheckMs;
        c.Step(f);
        Check.Eq(c.Said.Count, 3, "said again after a run made kits");
        // Danger, buffing, the count: never said in chat (the status line has them).
        var q = new Clock();
        foreach (Action<KitAutoFacts> set in new Action<KitAutoFacts>[]
                 { g => g.NearestMonsterYd = 10, g => g.BusyWhy = "looting", g => g.Kits = 5, g => g.SinceHurtMs = 1 })
        {
            var g = Clear();
            set(g);
            q.Step(g);
            q.Now += KitAuto.CheckMs;
        }
        Check.Eq(q.Said.Count, 0, "only the pack's reasons are said");
    }

    private static void LooksThrottled()
    {
        var c = new Clock();
        var f = Clear();
        f.Kits = 3;   // enough
        c.Step(f);
        Check.Eq(c.Reads, 1, "the first look reads the facts");
        for (int i = 0; i < 40; i++) { c.Now += 100; c.Step(f); }
        Check.Eq(c.Reads, 1, "4 s of ticks: no read (the next look is 5 s away)");
        Check.True(c.A.Status.Contains("next check in 1 s"), "the status counts down: " + c.A.Status);
        c.Now += 1_000;
        c.Step(f);
        Check.Eq(c.Reads, 2, "5 s on: the next look");
        // While getting ready the facts are read every tick.
        var r = new Clock();
        var g = Clear();
        g.InPeace = false;
        for (int i = 0; i < 5; i++) { r.Step(g); r.Now += 100; }
        Check.Eq(r.Reads, 5, "getting ready: every tick");
    }
}

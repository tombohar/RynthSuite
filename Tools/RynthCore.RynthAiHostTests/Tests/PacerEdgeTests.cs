using System;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Bounds on the pacer's holds: the 0x1D latch must not throttle melee and missile (ACE sends no
// UseDone for a swing), StuckCastMs = 0 must not hold for ever, a teleport's gap must not break a
// cast by jump, the settle gate stands apart from the animation gate, and only 'too busy' answers
// count toward the refusal burst.
internal static class PacerEdgeTests
{
    private const uint Player = 0x50000F21;
    private const uint Monster = 0x80116791;
    private const int DrainHealthOtherI = 1237;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x6347028Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("pacer edge: after a lone 'too busy' answer, attack repeats go (over 50 swings in 60 s); a cast with ours open is still held", AttackRepeatsPastLatch);
        r.Add("pacer edge: a 'too busy' latch with no cast of ours open ends after LatchMaxMs (logged once)", LatchExpires);
        r.Add("pacer edge: StuckCastMs 0 never deadlocks: a hung cast is dropped as stale by StaleCastMs (inside 120 s)", StuckCastZeroBounded);
        r.Add("pacer edge: in portal space nothing is broken (no jump, ForceBreak refused); a cast sent before a 20 s gap is dropped, not jumped", PortalAndGap);
        r.Add("pacer edge: 5 'too busy' answers in one second hold every start but a heal, attack repeats too, for the rest of that second", RefusalBurst);
        r.Add("pacer edge: the settle gate holds a start with the animation gate off", SettleGateSplit);
        r.Add("pacer edge: portal-enter and the death hold forget the open cast and the latch", PortalAndDeathForget);
        r.Add("pacer edge: a client-refused forced jump with no cast open does not hold starts for good (latch expiry, probe)", RefusedForcedJumpNoDeadlock);
        r.Add("pacer edge: the refusal burst counts 'too busy' only and never holds a heal", BurstCountsTooBusyAndSparesHeals);
    }

    private static long _t;

    private static RynthCoreHost Setup()
    {
        FakeHost.Reset();
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeCalls = true;
        FakeHost.TapJumpCalls = true;
        FakeHost.CombatModeValue = ActionPacer.MagicMode;
        FakeHost.Names[Player] = "Mira";
        FakeHost.PlayerPose = Me0;
        FakeHost.Positions[Player] = Me0;
        FakeHost.Positions[Monster] = (Me0.Cell, Me0.X + 10f, Me0.Y, Me0.Z);
        var host = FakeHost.Create(Player);
        _t = 70_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _t;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _t;
        ActionPacer.StuckCastMs = 8000;
        FakeHost.UseDone(0);
        ActionPacer.MayStart(host, "Setup", HealNeed.None, out _);   // the UseDone observer's baseline
        return host;
    }

    private static void Done()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
        ActionPacer.StuckCastMs = 8000;
        FakeHost.Portaling = false;
    }

    private static bool May(RynthCoreHost host) => ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _);
    private static bool Swing(RynthCoreHost host, out string why) =>
        ActionPacer.MayStart(host, "Combat", HealNeed.None, out why, animationGate: false, settleGate: false, attackRepeat: true);
    private static void Cast(RynthCoreHost host) =>
        Check.True(host.CastSpellFor(Monster, DrainHealthOtherI, "Buff", "Drain Health Other I"), "cast sent");
    private static int Jumps() => FakeHost.TapJumps + FakeHost.Jumps;

    private static void AttackRepeatsPastLatch()
    {
        var host = Setup();
        try
        {
            FakeHost.CombatModeValue = 2;   // melee
            FakeHost.UseDone(ActionPacer.TooBusyErr);   // a lone 0x1D (a pickup, a player command)
            Check.False(May(host), "a gated start is held by the latch");
            Check.True(ActionPacer.ServerBusy, "latched");
            int swings = 0;
            for (int i = 0; i < 60; i++)
            {
                _t += 1000;
                if (Swing(host, out _)) { swings++; ActionPacer.NoteAttackSent(); }
            }
            Check.True(swings > 55, $"a swing a second for 60 s after the 0x1D (no UseDone ever comes for a swing): {swings}");

            // Our cast open (hung past CastMaxMs) and another send refused 'too busy': a cast is still held.
            var h2 = Setup();
            ActionPacer.StuckCastMs = 0;
            Cast(h2);
            _t += ActionPacer.CastMaxMs + 100;
            Check.False(May(h2), "our cast hung");
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            bool any = false;
            for (int i = 0; i < 50; i++) { _t += 100; any |= May(h2); }
            Check.False(any, "a cast with ours open and refused: held");
        }
        finally { Done(); }
    }

    private static void LatchExpires()
    {
        var host = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            May(host);                                   // observed here: the latch starts
            long t0 = _t;
            while (_t + 100 < t0 + ActionPacer.LatchMaxMs) { _t += 100; May(host); }
            Check.True(ActionPacer.ServerBusy, "still latched just under LatchMaxMs");
            Check.Eq(ActionPacer.LatchExpiries, 0L, "no expiry yet");
            _t = t0 + ActionPacer.LatchMaxMs;
            Check.True(May(host), "at LatchMaxMs: free");
            Check.False(ActionPacer.ServerBusy, "the latch is gone");
            Check.Eq(ActionPacer.LatchExpiries, 1L, "one expiry");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("no answer cleared the 'too busy' latch in")), 1, "logged once");
        }
        finally { Done(); }
    }

    private static void StuckCastZeroBounded()
    {
        var host = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Cast(host);
            long t0 = _t;
            _t += ActionPacer.CastMaxMs + 100;
            Check.False(May(host), "our cast hung");
            FakeHost.UseDone(ActionPacer.TooBusyErr);   // another send refused while it is open
            long freeAt = -1;
            while (_t < t0 + 120_000) { _t += 100; if (May(host)) { freeAt = _t - t0; break; } }
            Check.True(freeAt > 0, "free within 120 s");
            Check.True(freeAt >= ActionPacer.StaleCastMs - 100 && freeAt <= ActionPacer.StaleCastMs + 100, $"free at StaleCastMs ({ActionPacer.StaleCastMs}): {freeAt}");
            Check.Eq(Jumps(), 0, "StuckCastMs 0: no jump");
            Check.True(FakeHost.Logs.Any(l => l.Contains("is stale (") && l.Contains("dropped, the server is taken as free")), "logged");
        }
        finally { Done(); }
    }

    private static void PortalAndGap()
    {
        var host = Setup();
        try
        {
            Cast(host);
            FakeHost.Portaling = true;
            for (int i = 0; i < 150; i++) { _t += 100; May(host); }   // 15 s in portal space
            Check.Eq(Jumps(), 0, "portal space: no breaker jump");
            Check.False(ActionPacer.ForceBreak(host, "test"), "portal space: ForceBreak refused");
            Check.Eq(Jumps(), 0, "still no jump");
            FakeHost.Portaling = false;

            // A 20 s gap with no check (a freeze, a long portal with the macro off): stale, dropped.
            // The tick's frame, then the cast 5 ms later in the same tick, then the freeze.
            var g = Setup();
            ActionPacer.NoteFrame();
            _t += 5;
            Cast(g);
            _t += 20_000;
            ActionPacer.NoteFrame();               // the first frame after the freeze
            Check.True(May(g), "after the gap: the cast is dropped and starts go");
            Check.Eq(Jumps(), 0, "no jump for a cast sent before the gap");
            Check.Eq(ActionPacer.StaleDrops, 1L, "one stale drop");
            Check.True(FakeHost.Logs.Any(l => l.Contains("is stale (") && l.Contains("ms gap with no check")), "the gap is in the line");

            // Control: the same 20 s with frames every 100 ms is a stuck cast, broken by the jump.
            var c = Setup();
            Cast(c);
            for (int i = 0; i < 200; i++) { _t += 100; ActionPacer.NoteFrame(); May(c); }
            Check.True(Jumps() >= 1, "no gap: the stuck cast is broken (control)");
            Check.Eq(ActionPacer.StaleDrops, 0L, "and not dropped");
        }
        finally { Done(); }
    }

    private static void RefusalBurst()
    {
        var host = Setup();
        try
        {
            FakeHost.CombatModeValue = 2;
            string why = "";
            bool[] free = new bool[ActionPacer.RefusalBurstMax];
            for (int i = 0; i < ActionPacer.RefusalBurstMax; i++)
            {
                FakeHost.UseDone(ActionPacer.TooBusyErr);
                _t += 50;
                free[i] = Swing(host, out why);
            }
            Check.True(free.Take(ActionPacer.RefusalBurstMax - 1).All(b => b), "4 refusals: still free");
            Check.False(free[^1], "the 5th refusal inside a second: held");
            Check.True(why.Contains("refusals from the server in the last second"), "why: " + why);
            Check.False(May(host), "a gated start too");
            Check.True(ActionPacer.BurstHolds >= 1, "counted (the heal start above is held by the latch, not the burst)");
            _t += 1000;
            Check.True(Swing(host, out _), "a second later: free");
        }
        finally { Done(); }
    }

    private static void SettleGateSplit()
    {
        var host = Setup();
        try
        {
            FakeHost.AutoRun = true;
            host.SetAutoRunBy("Nav", true);
            _t += 1000;
            host.SetAutoRunBy("Nav", false);   // the stop goes out this tick
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.None, out string why, animationGate: false),
                "animation gate off, settle gate on: held in the tick of the stop");
            Check.True(why.Length > 0, "why: " + why);
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _, animationGate: false, settleGate: false),
                "both off: free (only attack repeats do this)");
            _t += MoveAudit.MoveSettleMinMs;
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out _, animationGate: false), "settled: free");
        }
        finally { Done(); }
    }

    // ForceBreak with no cast open and the client refusing the jump must not leave the breaker stage
    // at Landed for nothing, which would hold every start for good.
    private static void RefusedForcedJumpNoDeadlock()
    {
        var host = Setup();
        try
        {
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            May(host);                                         // the latch starts here
            long t0 = _t;
            FakeHost.TapJumpRefuse = true;
            Check.True(ActionPacer.ForceBreak(host, "test"), "the forced break is tried");
            Check.Eq(FakeHost.TapJumps, 1, "the jump was sent (and refused)");
            long firstFree = -1;
            while (_t < t0 + ActionPacer.LatchMaxMs + 500) { _t += 100; if (May(host) && firstFree < 0) firstFree = _t - t0; }
            Check.True(firstFree > 0 && firstFree <= ActionPacer.LatchMaxMs + 100, $"a start goes by LatchMaxMs (first at {firstFree} ms)");
            Check.False(ActionPacer.ServerBusy, "the latch is gone by then");
            Check.Eq(ActionPacer.LatchExpiries, 1L, "it expired");
        }
        finally { Done(); }
    }

    // 0x401 retries (5 a second by design) do not trip the burst, and the burst never holds a heal.
    private static void BurstCountsTooBusyAndSparesHeals()
    {
        var host = Setup();
        try
        {
            for (int i = 0; i < ActionPacer.RefusalBurstMax + 1; i++) { FakeHost.UseDone(0x401); _t += 50; May(host); }
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.None, out string w1), "6 'not enough mana' answers in a second: no burst hold (" + w1 + ")");
            Check.Eq(ActionPacer.BurstHolds, 0L, "none counted");

            var h = Setup();
            for (int i = 0; i < ActionPacer.RefusalBurstMax; i++)
            {
                FakeHost.UseDone(ActionPacer.TooBusyErr);
                _t += 50;
                ActionPacer.MayStart(h, "Combat", HealNeed.None, out _, animationGate: false, settleGate: false, attackRepeat: true);
            }
            FakeHost.UseDone(0);                                // the server answers: the latch is gone, the burst is not
            _t += 50;
            Check.False(ActionPacer.MayStart(h, "Buff", HealNeed.None, out string why), "inside the second: a buff is held by the burst");
            Check.True(why.Contains("refusals from the server in the last second"), "why: " + why);
            Check.True(ActionPacer.MayStart(h, "Buff", HealNeed.Emergency, out string hw), "an emergency heal is not (" + hw + ")");
            Check.True(ActionPacer.MayStart(h, "Buff", HealNeed.Heal, out _), "nor a heal");
        }
        finally { Done(); }
    }

    internal static void SetHost(RynthAiPlugin p, RynthCoreHost host)
    {
        for (Type? t = p.GetType(); t != null; t = t.BaseType)
        {
            var prop = t.GetProperty("Host", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (prop?.SetMethod != null) { prop.SetValue(p, host); return; }
            var f = t.GetField("<Host>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    ?? t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).FirstOrDefault(x => x.FieldType == typeof(RynthCoreHost));
            if (f != null) { f.SetValue(p, host); return; }
        }
        throw new InvalidOperationException("no Host to set on RynthAiPlugin");
    }

    private static void Call(object o, string name) =>
        o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, null);

    private static void PortalAndDeathForget()
    {
        var host = Setup();
        try
        {
            ActionPacer.StuckCastMs = 0;
            var p = new RynthAiPlugin();
            SetHost(p, host);
            Cast(host);
            _t += ActionPacer.CastMaxMs + 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            May(host);
            Check.True(ActionPacer.ServerBusy, "our cast hung, latched");
            Call(p, "NotePortalForPacer");
            Check.True(ActionPacer.ServerBusy, "not portaling: nothing forgotten");
            FakeHost.Portaling = true;
            Call(p, "NotePortalForPacer");
            Check.False(ActionPacer.ServerBusy, "portal-enter: the latch is forgotten");
            Check.True(May(host), "and starts go");
            FakeHost.Portaling = false;
            Call(p, "NotePortalForPacer");

            Cast(host);
            _t += ActionPacer.CastMaxMs + 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            May(host);
            Check.True(ActionPacer.ServerBusy, "latched again");
            FakeHost.Vitals = (0, 100, 0, 100, 0, 100);
            Call(p, "HoldForDeath");
            Check.False(ActionPacer.ServerBusy, "the death hold forgets the latch");
            Check.True(May(host), "and starts go");
        }
        finally { Done(); }
    }
}

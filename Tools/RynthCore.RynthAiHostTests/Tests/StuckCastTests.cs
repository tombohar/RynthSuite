using System;
using System.Linq;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// In a client log, two casts were held open by ACE's fast-tick turn-to-cast wait (40.8 s and 37 s);
// every cast after them came back UseDone 0x1D and the vitals path re-sent a heal every 2.5 s. A
// client mode change ended the second. ActionPacer now holds every start after a 0x1D until the
// server answers, turns to face a held cast's target, breaks a stuck cast with a jump (then Peace
// mode), waits until landed, and holds starts until a movement change has settled.
internal static class StuckCastTests
{
    private const uint Player = 0x50000F01;
    private const uint Monster = 0x80116781;
    private const int DrainHealthOtherI = 1237;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x6347028Au, 100f, 100f, 0f);

    public static void Register(Runner r)
    {
        r.Add("stuck cast: after a 'too busy' UseDone nothing starts until the server answers; no timer re-send while our cast is open", NoResendAfterTooBusy);
        r.Add("stuck cast: with no cast of ours open, one probe after 1.5 s, then doubling; a UseDone(0) clears", ProbeWhenNothingOpen);
        r.Add("too busy (Conquest 10-25): a try held by another check (animating) is not spent; the next asker (combat) gets it", TryNotSpentWhenHeld);
        r.Add("stuck cast: a held cast is turned to its target at cycle + 1 s, broken by a jump TurnGraceMs after the turn; a normal 4 s cast trips neither", TurnThenJump);
        r.Add("stuck cast: already facing the target (no turn), the jump is at cycle + 2 s", JumpWithoutTurn);
        r.Add("stuck cast: no starts in the air; after landing, no UseDone in 3 s -> Peace mode, 3 s more -> going on", AfterTheJump);
        r.Add("stuck cast: a 'too busy' answer while our cast is open past its cycle + 1 s (and 3 s) breaks it at once; before, it does not", RefusalWhileOpen);
        r.Add("stuck cast: StuckCastMs 0 turns nothing and breaks nothing; no TapJump -> Peace mode", BreakerOffAndNoJump);
        r.Add("move settle: no start in the tick of a movement change; 150 ms and a clear motion queue (or 600 ms) let it go", MoveSettle);
        r.Add("stuck cast: the HP failsafe's forced break jumps once and holds starts until landed", ForcedBreak);
        r.Add("pacer: an emergency heal is not held by the 'too busy' latch (a refused pickup, a hung cast); a plain heal and other starts still are", EmergencyHealThroughLatch);
        r.Add("pacer: an emergency heal does not wait for the movement settle, and still obeys an unanswered action in flight", EmergencyHealSkipsSettle);
        r.Add("pacer: while a plain heal waits on the 'too busy' latch, a start that is no heal does not take the probe slot", HealKeepsProbeSlot);
        r.Add("pacer: a 'too busy' UseDone that came while the macro was off does not latch the server as busy at the macro start", StaleUseDoneAtMacroStart);
        r.Add("stuck cast: a script's cast is not jumped before ScriptCastMinBreakMs (6 s): answered at 5.5 s, no jump; RynthAi's own is jumped at 5 s", ScriptCastGetsNudgeWindow);
    }

    private static long _t;

    private static RynthCoreHost Setup(int mode = ActionPacer.MagicMode, bool tapJump = true)
    {
        FakeHost.Reset();
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.WeaponCalls = true;      // GetCurrentCombatMode
        FakeHost.CombatModeCalls = true;  // ChangeCombatMode
        FakeHost.TapJumpCalls = tapJump;
        FakeHost.CombatModeValue = mode;
        FakeHost.Names[Player] = "Mira";
        FakeHost.PlayerPose = Me0;
        FakeHost.Positions[Player] = Me0;
        FakeHost.HeadingDeg = 90;                                      // facing east: no held-cast turn (TurnThenJump faces north)
        FakeHost.Positions[Monster] = (Me0.Cell, Me0.X + 10f, Me0.Y, Me0.Z);   // due east
        var host = FakeHost.Create(Player);
        _t = 90_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _t;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _t;
        ActionPacer.StuckCastMs = 8000;
        FakeHost.UseDone(0);               // an earlier action's answer: the observer's baseline
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
    }

    private static bool May(RynthCoreHost host, HealNeed heal = HealNeed.Heal) =>
        ActionPacer.MayStart(host, "Buff", heal, out _);

    private static void Cast(RynthCoreHost host, string label = "Drain Health Other I") =>
        Check.True(host.CastSpellFor(Monster, DrainHealthOtherI, "Buff", label), "cast sent");

    private static int JumpLines() => FakeHost.Logs.Count(l => l.Contains("stuck cast broken by jump after"));

    private static void Run(RynthCoreHost host, long ms) { for (long s = 0; s < ms; s += 100) { _t += 100; May(host); } }

    private static bool MayAs(RynthCoreHost host, string who, HealNeed heal) =>
        ActionPacer.MayStart(host, who, heal, out _);

    // Emergency Heal At: the heal is not held by the 'too busy' latch, whatever set it. A door/corpse use or a
    // pickup the server refused (UseDone 0x1D) latched every start before, the heal included.
    private static void EmergencyHealThroughLatch()
    {
        var host = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Check.True(May(host), "free");                       // the observer's baseline
            Cast(host, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);            // any refused action latches the server as busy
            Check.False(MayAs(host, "Loot", HealNeed.None), "a corpse use is held by the latch");
            Check.False(MayAs(host, "Buff", HealNeed.Heal), "a plain heal is held too (it waits for the probe)");
            Check.True(MayAs(host, "Buff", HealNeed.Emergency), "the emergency heal goes at once");
            Check.True(MayAs(host, "Buff", HealNeed.Emergency), "and keeps going (not a probe slot)");
            Check.False(MayAs(host, "Loot", HealNeed.None), "the latch still holds the others");
            Check.True(ActionPacer.ServerBusy, "still taken as busy");
            Check.True(FakeHost.Logs.Any(l => l.Contains("the emergency heal goes ahead of the server's 'too busy' answer")), "logged");
            Check.True(ActionPacer.EmergencyThroughLatch >= 2, "counted");
        }
        finally { Done(); }

        // A hung cast (no UseDone in CastMaxMs) is a latch too.
        var hung = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Cast(hung);
            _t += ActionPacer.CastMaxMs + 100;
            Check.False(MayAs(hung, "Loot", HealNeed.None), "a hung cast holds a corpse use");
            Check.True(MayAs(hung, "Buff", HealNeed.Emergency), "but not the emergency heal");
        }
        finally { Done(); }

        // The in-flight rules still apply: our own unanswered cast (inside CastMaxMs) holds even an emergency heal.
        var open = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Cast(open);
            _t += 500;
            Check.False(MayAs(open, "Buff", HealNeed.Emergency), "an unanswered cast of ours holds the heal (no result yet)");
        }
        finally { Done(); }
    }

    // The 150-600 ms wait after any movement call RynthAi sends does not delay an emergency heal.
    private static void EmergencyHealSkipsSettle()
    {
        var host = Setup();
        try
        {
            FakeHost.AutoRun = true;
            host.SetAutoRunBy("Nav", true);
            _t += 1000;
            host.SetAutoRunBy("Nav", false);                      // nav's stop goes out this tick
            Check.False(MayAs(host, "Buff", HealNeed.Heal), "a plain heal waits for the settle");
            Check.False(MayAs(host, "Loot", HealNeed.None), "other starts too");
            Check.True(MayAs(host, "Buff", HealNeed.Emergency), "the emergency heal does not");
            FakeHost.CastBusy = 1;                                // the client still animates the stop
            Check.False(MayAs(host, "Buff", HealNeed.Heal), "the plain heal: still held while the queue is busy");
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Emergency, out _, animationGate: false), "the emergency heal (animation gate off): goes");
            FakeHost.CastBusy = 0;
        }
        finally { Done(); }

        // It still waits for an action the server has not answered.
        var inflight = Setup();
        try
        {
            Cast(inflight);
            _t += 100;
            Check.False(MayAs(inflight, "Buff", HealNeed.Emergency), "an unanswered cast in flight: the emergency heal waits (pacing in-flight rule)");
        }
        finally { Done(); }
    }

    // The probe after a 'too busy' latch is one start per 1.5 s, doubling. A plain heal waiting on the latch gets
    // it; a corpse or door use that happens to be evaluated first no longer takes it.
    private static void HealKeepsProbeSlot()
    {
        var host = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Check.True(May(host), "free");                          // the observer's baseline
            Cast(host, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            Check.False(MayAs(host, "Buff", HealNeed.Heal), "latched");
            _t += ActionPacer.BusyProbeMs - 100;
            Check.False(MayAs(host, "Buff", HealNeed.Heal), "the heal waits (marks it as waiting)");
            _t += 100;                                            // the probe is due
            Check.False(MayAs(host, "Door", HealNeed.None), "the door use does not take the slot while a heal waits");
            Check.True(MayAs(host, "Buff", HealNeed.Heal), "the heal gets the probe");
            Check.False(MayAs(host, "Door", HealNeed.None), "one probe only");
        }
        finally { Done(); }

        // No heal waiting: the first start of any kind still takes the probe (the latch is not left to the 15 s expiry).
        var plain = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Check.True(May(plain), "free");                          // the observer's baseline
            Cast(plain, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            Check.False(MayAs(plain, "Door", HealNeed.None), "latched");
            _t += ActionPacer.BusyProbeMs;
            Check.True(MayAs(plain, "Door", HealNeed.None), "no heal waiting: a door use takes the probe");
        }
        finally { Done(); }

        // The mark goes stale: a heal that stopped asking does not hold the slot from the others.
        var stale = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Check.True(May(stale), "free");                          // the observer's baseline
            Cast(stale, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            Check.False(MayAs(stale, "Buff", HealNeed.Heal), "latched, a heal waited");
            _t += ActionPacer.BusyProbeMs;
            Check.True(MayAs(stale, "Door", HealNeed.None), "the heal has not asked for over HealWaitFreshMs: the door use takes the probe");
        }
        finally { Done(); }
    }

    // The macro was off (nothing checked UseDones); the player's own action was refused 0x1D. At the macro start the
    // pacer's observer took that for a fresh answer and held every start for the probe wait.
    private static void StaleUseDoneAtMacroStart()
    {
        var host = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Check.True(May(host), "macro running: the observer's baseline");
            FakeHost.UseDone(ActionPacer.TooBusyErr);            // while the macro is off: a refused player action
            _t += 60_000;
            ActionPacer.NoteMacroStart();                         // the macro is switched on
            Check.True(May(host), "the stale 0x1D is not news: nothing is held");
            Check.False(ActionPacer.ServerBusy, "not latched");
            FakeHost.UseDone(ActionPacer.TooBusyErr);            // a real refusal after the start still latches
            Check.False(May(host), "a 0x1D after the start latches as before");
            Check.True(ActionPacer.ServerBusy, "latched");
        }
        finally { Done(); }

        // Control: without the macro-start note the same stale 0x1D latches (what the review found).
        var ctl = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            Check.True(May(ctl), "baseline");
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            _t += 60_000;
            Check.False(May(ctl), "no note: held (the old behaviour)");
        }
        finally { Done(); }

        // The plugin notes the off-to-on edge of IsMacroRunning (and only that).
        var plugin = new RynthCore.Plugin.RynthAi.RynthAiPlugin();
        var note = typeof(RynthCore.Plugin.RynthAi.RynthAiPlugin).GetMethod("NoteMacroForPacer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var edge = Setup();
        ActionPacer.StuckCastMs = 0;
        try
        {
            note.Invoke(plugin, new object[] { false });
            Check.True(May(edge), "macro off: baseline");
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            note.Invoke(plugin, new object[] { true });          // switched on
            Check.True(May(edge), "off to on: the stale 0x1D is dropped");
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            note.Invoke(plugin, new object[] { true });          // still running: no edge
            Check.False(May(edge), "running on: a 0x1D is a 0x1D");
        }
        finally { Done(); }
    }

    private static void ScriptCastGetsNudgeWindow()
    {
        // Control: RynthAi's own cast of a spell with no learned cycle (3 s) is jumped at cycle + 2 s.
        var own = Setup();
        try
        {
            Cast(own);
            Run(own, 5500);
            Check.Eq(JumpLines(), 1, "RynthAi's own cast: jumped by 5.5 s (at 5 s)");
        }
        finally { Done(); }

        // The same cast chosen by a script, answered at 5.5 s: no jump.
        var host = Setup();
        try
        {
            Cast(host);
            ActionPacer.MarkScriptCast(DrainHealthOtherI);
            Run(host, 5500);
            Check.Eq(JumpLines(), 0, "a script's cast: no jump in the first 5.5 s");
            FakeHost.UseDone(0);   // its answer
            Run(host, 3000);
            Check.Eq(JumpLines(), 0, "answered: never jumped");
        }
        finally { Done(); }

        // A script's cast with no answer is still broken, once ScriptCastMinBreakMs has passed.
        var held = Setup();
        try
        {
            Cast(held);
            ActionPacer.MarkScriptCast(DrainHealthOtherI);
            Run(held, ActionPacer.ScriptCastMinBreakMs + 200);
            Check.Eq(JumpLines(), 1, "unanswered: jumped after ScriptCastMinBreakMs");
        }
        finally { Done(); }
    }

    private static void NoResendAfterTooBusy()
    {
        var host = Setup();
        ActionPacer.StuckCastMs = 0;           // the hold alone (the breaker has its own tests)
        try
        {
            Check.True(May(host), "free at first");
            Cast(host);                        // the cast the server will hold open: no UseDone
            _t += 500;
            Check.False(May(host), "our cast is unanswered");

            // Past CastMaxMs (7 s) the pacer used to "go on" and the heal went out into a busy server.
            _t += ActionPacer.CastMaxMs - 500;
            Check.False(May(host), "unanswered past CastMaxMs: still held (the cast is hung, the server is busy with it)");
            Check.True(ActionPacer.ServerBusy, "the server is taken as busy");

            // A cast the player sent by hand is refused: 0x1D. Still nothing goes for 13 s (no timer re-send).
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            bool any = false;
            for (int i = 0; i < 130; i++) { _t += 100; any |= May(host); }
            Check.False(any, "nothing goes for 13 s after the 0x1D while our cast is hung");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Buff waits:") && l.Contains("has had no answer")), "the hold is logged");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("Buff waits:") && l.Contains("has had no answer")), 1, "once");

            // The server finally answers (the cast's own UseDone): everything goes again.
            FakeHost.UseDone(0x402);
            Check.True(May(host), "a UseDone with another error clears the hold");
            Check.False(ActionPacer.ServerBusy, "not busy any more");
            Check.True(FakeHost.Logs.Any(l => l.Contains("the server answered again (UseDone 0x402)")), "logged");
        }
        finally { Done(); }
    }

    // Conquest report R-20261025-183343 (Dargoth Hera): a corpse pickup and a cast went out together, the
    // cast was refused 'too busy', and the buff step took all three tries after it while the character was
    // still animating, so it sent nothing; combat never got a try and the fight stood for the latch's 15 s.
    private static void TryNotSpentWhenHeld()
    {
        var host = Setup();
        try
        {
            Check.True(MayAs(host, "Combat", HealNeed.None), "free");
            Cast(host, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);   // refused next to a pickup (no UseDone of its own)
            Check.False(MayAs(host, "Combat", HealNeed.None), "held right after the 0x1D");
            _t += ActionPacer.BusyProbeMs;
            FakeHost.CastBusy = 1;                       // the client still animates
            Check.False(MayAs(host, "Buff", HealNeed.None), "the buff step's try is held by the animation");
            FakeHost.CastBusy = 0;
            Check.True(MayAs(host, "Combat", HealNeed.None), "combat gets the try the buff step couldn't use");
            Check.False(MayAs(host, "Buff", HealNeed.None), "and only one start has it");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains("one try (#1)")), 1, "one try logged");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Combat: one try (#1)")), "and it was combat's");
            // The next try is counted from the one that went (doubling as before).
            _t += ActionPacer.BusyProbeMs * 2 - 1;
            Check.False(MayAs(host, "Combat", HealNeed.None), "the next try waits twice as long");
            _t += 1;
            Check.True(MayAs(host, "Combat", HealNeed.None), "then goes");
        }
        finally { Done(); }
    }

    private static void ProbeWhenNothingOpen()
    {
        var host = Setup();
        try
        {
            Check.True(May(host), "free");
            Cast(host, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);   // refused: the server is busy with something of no UseDone (a pickup)
            Check.False(May(host), "held right after the 0x1D");
            _t += ActionPacer.BusyProbeMs - 1;
            Check.False(May(host), "held until BusyProbeMs");
            _t += 1;
            Check.True(May(host), "one probe at BusyProbeMs");
            Check.False(May(host), "only one");
            Cast(host, "Heart Rend");
            _t += 100;
            FakeHost.UseDone(ActionPacer.TooBusyErr);   // refused again
            _t += ActionPacer.BusyProbeMs * 2 - 101;
            Check.False(May(host), "the next probe waits twice as long");
            _t += 1;
            Check.True(May(host), "second probe at 2 x BusyProbeMs");
            Check.Eq(FakeHost.TapJumps + FakeHost.CombatModeRequests.Count, 0, "no breaker: no cast of ours was left open");
            Cast(host, "Heart Rend");
            _t += 900;
            FakeHost.UseDone(0);
            Check.True(May(host), "a UseDone(0) clears it");
        }
        finally { Done(); }
    }

    private static void TurnThenJump()
    {
        var host = Setup();
        try
        {
            // A normal 4 s cast: answered; it sets the spell's cycle (4 s) and trips nothing.
            Check.True(May(host), "free");
            Cast(host);
            _t += 4000;
            FakeHost.UseDone(0);
            May(host);                         // the answer seen at 4000 ms
            Run(host, 10_000);
            Check.Eq(FakeHost.Turns + FakeHost.TapJumps + FakeHost.CombatModeRequests.Count, 0, "a normal 4 s cast: no turn, no jump, no mode change");
            Check.Eq(ActionPacer.NormalCycleMs(DrainHealthOtherI), 4000L, "the cycle learned from it");

            // The next one is held open, facing north (90 deg off): turn at 4 + 1 s, jump TurnGraceMs later.
            FakeHost.HeadingDeg = 0;
            Cast(host);
            long sent = _t;
            while (_t - sent < 4900) { _t += 100; May(host); }
            Check.Eq(FakeHost.Turns, 0, "no turn before cycle + 1 s");
            _t += 100; May(host);
            Check.Eq(FakeHost.Turns, 1, "turned at cycle + 1 s");
            Check.Near(FakeHost.HeadingDeg, 90.0, 0.5, "to face the target (east)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("held cast:") && l.Contains("turning 90 deg to face 0x80116781")), "logged");
            // 2026-10-16: the jump came 1 s after a 91 deg turn, 0.2 s before the cast's answer.
            while (_t - sent < 5000 + ActionPacer.TurnGraceMs - 100) { _t += 100; Check.False(May(host), "held"); }
            Check.Eq(FakeHost.TapJumps, 0, "no jump while the turn has its time (cycle + 2 s is past)");
            _t += 100; May(host);
            Check.Eq(FakeHost.TapJumps, 1, "the breaker jump TurnGraceMs after the turn");
            Check.True(FakeHost.Logs.Any(l => l.Contains($"[Pace] stuck cast broken by jump after {5000 + ActionPacer.TurnGraceMs} ms")), "logged with the time");
            Run(host, 2000);
            Check.Eq(FakeHost.TapJumps, 1, "one jump");
            Check.Eq(FakeHost.Turns, 1, "one turn");

            // The fizzle (0x402): free again once landed.
            FakeHost.UseDone(0x402);
            Check.True(May(host), "free after the cast's UseDone");
        }
        finally { Done(); }
    }

    private static void JumpWithoutTurn()
    {
        var host = Setup();
        try
        {
            // Facing the monster (Setup): no turn goes out.
            Check.True(May(host), "free");
            Cast(host);
            long sent = _t;
            while (FakeHost.TapJumps == 0 && _t - sent < 10_000) { _t += 100; May(host); }
            Check.Eq(FakeHost.Turns, 0, "no turn");
            Check.Eq(_t - sent, ActionPacer.DefaultCycleMs + ActionPacer.JumpAfterMs, "the jump at cycle + 2 s");
        }
        finally { Done(); }
    }

    private static void AfterTheJump()
    {
        var host = Setup();
        try
        {
            Check.True(May(host), "free");
            Cast(host);
            long sent = _t;
            while (FakeHost.TapJumps == 0 && _t - sent < 10_000) { _t += 100; May(host); }
            Check.Eq(_t - sent, ActionPacer.DefaultCycleMs + ActionPacer.JumpAfterMs, "unknown spell cycle: the jump at 3 + 2 s");
            long jumped = _t;
            // In the air: even with the cast's UseDone in, nothing starts.
            FakeHost.PlayerPose = (Me0.Cell, Me0.X, Me0.Y, Me0.Z + 0.6f);
            FakeHost.UseDone(0x402);
            _t += 200;
            Check.False(May(host), "in the air: held even with the UseDone in");
            Check.True(ActionPacer.BreakerAirborne, "airborne");
            _t += 300;
            Check.False(May(host), "still above the take-off height at 500 ms");
            FakeHost.PlayerPose = Me0;
            Check.True(May(host), "landed: free (the cast was answered)");

            // A second hung cast: jump, land, no answer: Peace mode 3 s after landing, then going on 3 s later.
            Cast(host);
            sent = _t;
            while (FakeHost.TapJumps < 2 && _t - sent < 10_000) { _t += 100; May(host); }
            Check.Eq(FakeHost.TapJumps, 2, "jumped");
            _t += ActionPacer.JumpLandMinMs; May(host);         // landed (height unchanged)
            long landed = _t;
            while (_t - landed < ActionPacer.BreakerWaitMaxMs - 100) { _t += 100; Check.False(May(host), "held after landing, no answer"); }
            Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no Peace yet");
            _t += 100; May(host);
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), ActionPacer.PeaceMode.ToString(), "Peace requested once, 3 s after landing");
            Check.True(FakeHost.Logs.Any(l => l.Contains("stuck cast broken by Peace mode after")), "logged");
            FakeHost.CombatModeValue = ActionPacer.PeaceMode;
            Run(host, ActionPacer.BreakerWaitMaxMs);
            Check.True(FakeHost.Logs.Any(l => l.Contains("no UseDone after the stuck-cast break")), "going on, logged");
            _t += ActionPacer.ModeSettleMs;
            Check.True(May(host), "free after it");
            Check.Eq(FakeHost.CombatModeRequests.Count, 1, "one Peace request");
        }
        finally { Done(); }
    }

    private static void RefusalWhileOpen()
    {
        var host = Setup();
        try
        {
            Check.True(May(host), "free");
            Cast(host);
            _t += 2000;
            Cast(host, "Rushed Recovery");          // a heal let ahead of it: refused, 2 s in (a recoil refusal is normal)
            FakeHost.UseDone(ActionPacer.TooBusyErr);
            _t += 100; May(host);
            Check.Eq(FakeHost.TapJumps, 0, "a 0x1D at 2 s: no jump");
            _t += 900; May(host);
            Check.Eq(FakeHost.TapJumps, 0, "3 s with a 0x1D in, not yet RefusalPastCycleMs past its cycle: no jump (it may have landed, its UseDone due)");
            _t += ActionPacer.RefusalPastCycleMs; May(host);
            Check.Eq(FakeHost.TapJumps, 1, "past cycle + 1 s with a 0x1D in: jump at once (before cycle + 2 s)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("was refused 'too busy' while it was open")), "the reason is logged");
        }
        finally { Done(); }
    }

    private static void BreakerOffAndNoJump()
    {
        var host = Setup();
        try
        {
            ActionPacer.StuckCastMs = 0;
            Check.True(May(host), "free");
            Cast(host);
            Run(host, 30_000);
            Check.Eq(FakeHost.Turns + FakeHost.TapJumps + FakeHost.CombatModeRequests.Count, 0, "StuckCastMs 0: nothing");
        }
        finally { Done(); }

        host = Setup(tapJump: false);
        try
        {
            Check.True(May(host), "free");
            Cast(host);
            Run(host, 6000);
            Check.Eq(string.Join(",", FakeHost.CombatModeRequests), ActionPacer.PeaceMode.ToString(), "no TapJump: Peace mode at the jump time");
            Check.True(FakeHost.Logs.Any(l => l.Contains("no TapJump on this engine")), "logged");
        }
        finally { Done(); }
    }

    private static void MoveSettle()
    {
        var host = Setup();
        try
        {
            FakeHost.AutoRun = true;
            Check.True(May(host), "free");
            host.SetAutoRunBy("Nav", true);
            _t += 1000;
            host.SetAutoRunBy("Nav", false);          // nav's stop goes out this tick
            Check.False(May(host), "no cast in the tick of the stop");
            Check.True(ActionPacer.MayStart(host, "Combat", HealNeed.None, out _, animationGate: false, settleGate: false, attackRepeat: true), "an attack repeat is not held");
            _t += MoveAudit.MoveSettleMinMs - 1;
            Check.False(May(host), "held under MoveSettleMinMs");
            _t += 1;
            FakeHost.CastBusy = 1;                     // the client still animates the stop
            Check.False(May(host), "the motion queue still busy: held");
            FakeHost.CastBusy = 0;
            Check.True(May(host), "settled: free");
            Check.True(FakeHost.Logs.Any(l => l.Contains("SetAutoRun(False) sent") && l.Contains("has not settled")), "logged");

            host.TurnToHeadingBy("Nav", 45f);
            FakeHost.CastBusy = 1;
            _t += MoveAudit.MoveSettleMaxMs - 1;
            Check.False(May(host), "busy queue: held under MoveSettleMaxMs");
            _t += 1;
            // The gesture gate itself would still hold a busy queue; the settle gate lets go at its max.
            Check.True(!MoveAudit.SettleHolds(host, out _), "the settle gate lets go at MoveSettleMaxMs");
            FakeHost.CastBusy = 0;
        }
        finally { Done(); }
    }

    private static void ForcedBreak()
    {
        var host = Setup();
        try
        {
            Check.True(ActionPacer.ForceBreak(host, "HP failsafe"), "the forced break goes");
            Check.Eq(FakeHost.TapJumps, 1, "one jump");
            Check.False(ActionPacer.ForceBreak(host, "HP failsafe"), "not twice while in the air");
            FakeHost.PlayerPose = (Me0.Cell, Me0.X, Me0.Y, Me0.Z + 0.5f);
            _t += 300;
            Check.False(May(host), "in the air: held");
            FakeHost.PlayerPose = Me0;
            _t += 200;
            Check.True(May(host), "landed: free (nothing of ours was open)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("stuck cast broken by jump after 0 ms") && l.Contains("HP failsafe")), "logged");
        }
        finally { Done(); }
    }
}

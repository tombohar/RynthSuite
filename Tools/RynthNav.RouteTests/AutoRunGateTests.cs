using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthNav;

namespace RynthNav.RouteTests;

// The goto's autorun (AutoRunGate): on 10-19 a /rnav go "Matron Hive East" sent SetAutoRun about
// 55 times a second (settling after the recall, turning at the portal, walking the Town Network)
// and filled the engine's action ring in portal space. Now sent when it changes.
internal static partial class Program
{
    private static void RunAutoRunGate()
    {
        Run(nameof(TurnInPlaceSendsOffOnce), TurnInPlaceSendsOffOnce);
        Run(nameof(RunningReassertsTwiceASecond), RunningReassertsTwiceASecond);
        Run(nameof(SettleAfterATeleportSendsNothingMore), SettleAfterATeleportSendsNothingMore);
        Run(nameof(HoldHaltsOnce), HoldHaltsOnce);
        Run(nameof(TurnGateFlipsSendEachChange), TurnGateFlipsSendEachChange);
        Run(nameof(RefusedCallIsRetriedNotEveryTick), RefusedCallIsRetriedNotEveryTick);
        Run(nameof(NewGotoSendsAgain), NewGotoSendsAgain);
        Run(nameof(MatronHiveTripReplay), MatronHiveTripReplay);
    }

    private const long GateTickMs = 18;   // the plugin tick on 10-19: ~55 a second

    /// <summary>The host's SetAutoRun/StopCompletely, counted; Accept = false refuses them (a full action ring).</summary>
    private sealed class FakeMover
    {
        public long Now = 1_000_000;
        public bool Accept = true;
        public int On, Off, Stops, Tries;
        public readonly AutoRunGate Gate;

        public FakeMover()
        {
            Gate = new AutoRunGate(
                on => { Tries++; if (!Accept) return false; if (on) On++; else Off++; return true; },
                () => { Tries++; if (!Accept) return false; Stops++; return true; },
                () => Now);
        }

        public int Sent => On + Off + Stops;

        /// <summary><paramref name="each"/> once a tick for <paramref name="ms"/>.</summary>
        public void For(long ms, Action each)
        {
            for (long end = Now + ms; Now < end; Now += GateTickMs) each();
        }
    }

    private static void TurnInPlaceSendsOffOnce()
    {
        // At the Town Network portal (16:38:46): turning in place, SetAutoRun(False) every tick.
        var m = new FakeMover();
        m.For(4000, () => m.Gate.Run(false));
        Check(m.Off == 1 && m.On == 0 && m.Stops == 0, $"4 s turning in place: one SetAutoRun(False) (sent {m.Off}/{m.On}/{m.Stops})");
    }

    private static void RunningReassertsTwiceASecond()
    {
        // Running: "on" goes out once, then again every ReassertMs (a cast or a server stop can
        // turn autorun off without telling the goto).
        var m = new FakeMover();
        m.For(3000, () => m.Gate.Run(true));
        long ticks = 3000 / GateTickMs + 1;
        Check(m.On >= 3000 / AutoRunGate.ReassertMs && m.On <= 3000 / AutoRunGate.ReassertMs + 1,
            $"3 s running: {m.On} SetAutoRun(True), one every {AutoRunGate.ReassertMs} ms (was {ticks}, one a tick)");
        Check(m.Off == 0 && m.Stops == 0, "nothing else sent while running");
    }

    private static void SettleAfterATeleportSendsNothingMore()
    {
        // The recall lands (Stop), then portal space and the settle: SetAutoRun(False) every tick
        // for 7.5 s (16:38:38-16:38:46).
        var m = new FakeMover();
        m.Gate.Stop();
        m.For(7500, () => m.Gate.Run(false));
        Check(m.Off == 1 && m.Stops == 1 && m.On == 0, $"teleport + 7.5 s settle: just the Stop (sent {m.Off}/{m.Stops}/{m.On})");
    }

    private static void HoldHaltsOnce()
    {
        // The Town Network landing wait and the exit hold: SetAutoRun(False) + StopCompletely every tick.
        var m = new FakeMover();
        m.For(2000, () => m.Gate.Halt());
        Check(m.Off == 1 && m.Stops == 1, $"2 s holding: one halt (sent {m.Off}/{m.Stops})");
        m.Gate.Run(true);
        Check(m.On == 1, "running after the hold goes out");
        m.Gate.Halt();
        Check(m.Off == 2 && m.Stops == 2, "a hold after running halts again");
        m.Gate.Run(false);
        Check(m.Off == 2, "autorun is already off after a halt");
        m.Gate.Stop();
        Check(m.Off == 3 && m.Stops == 3, "Stop always sends (a goto's end, a teleport)");
    }

    private static void TurnGateFlipsSendEachChange()
    {
        // DriveTo's stop-to-turn hysteresis: every flip is a change and goes out at once.
        var m = new FakeMover();
        foreach (bool on in new[] { true, true, false, false, false, true, false })
        {
            m.Gate.Run(on);
            m.Now += GateTickMs;
        }
        Check(m.On == 2 && m.Off == 2, $"run, turn, run, turn: four sends (sent {m.On} on, {m.Off} off)");
    }

    private static void RefusedCallIsRetriedNotEveryTick()
    {
        // The engine refuses a call (its action ring full): not taken as sent, tried again after
        // RetryMs instead of every tick.
        var m = new FakeMover { Accept = false };
        m.For(1000, () => m.Gate.Run(false));
        Check(m.Off == 0, "nothing went through");
        Check(m.Tries >= 2 && m.Tries <= 1000 / AutoRunGate.RetryMs + 1, $"1 s refused: {m.Tries} tries, not one a tick");
        m.Accept = true;
        m.For(1000, () => m.Gate.Run(false));
        Check(m.Off == 1, $"once it's accepted it goes out once (sent {m.Off})");

        var h = new FakeMover { Accept = false };
        h.For(1000, () => h.Gate.Halt());
        Check(h.Tries >= 2 && h.Tries <= 2 * (1000 / AutoRunGate.RetryMs + 1), $"refused halt: {h.Tries} tries in 1 s");
        h.Accept = true;
        h.For(1000, () => h.Gate.Halt());
        Check(h.Off == 1 && h.Stops == 1, "the halt goes out once accepted");
    }

    private static void NewGotoSendsAgain()
    {
        // Between gotos the d-pad or another plugin may have moved the character: Forget, and the
        // first call of the new goto goes out even if it's the same as the last one sent.
        var m = new FakeMover();
        m.Gate.Run(false);
        m.Gate.Forget();
        m.Gate.Run(false);
        Check(m.Off == 2, "after Forget the same state is sent again");
        m.Gate.Halt();
        m.Gate.Forget();
        m.Gate.Halt();
        Check(m.Stops == 2, "after Forget a halt is sent again");
    }

    private static void MatronHiveTripReplay()
    {
        // The 10-19 trip's movement calls in order (RynthNav's tick, ~55 a second), from the
        // goto's start to the Baishi landing. Each phase is what the code asks for every tick.
        var m = new FakeMover();
        int asks = 0;
        var phases = new List<(long ms, Action tick)>
        {
            (0, () => m.Gate.Stop()),                // recall step: stop before the cast
            (2300, () => { }),                       // casting (no movement calls)
            (0, () => m.Gate.Stop()),                // the recall's teleport
            (7900, () => m.Gate.Run(false)),         // settle: portal space and the quiet second
            (4000, () => m.Gate.Run(false)),         // turning to the Town Network portal
            (600, () => m.Gate.Run(true)),           // running at it
            (1800, () => m.Gate.Run(false)),         // within reach: standing, using it
            (0, () => m.Gate.Stop()),                // its teleport
            (8000, () => m.Gate.Halt()),             // landing in the Town Network (portal space, settle)
            (5000, () => m.Gate.Run(true)),          // the walk to the Baishi portal
            (300, () => m.Gate.Run(false)),          // a turn in place on the way
            (5000, () => m.Gate.Run(true)),
            (300, () => m.Gate.Halt()),              // at the exit
            (0, () => m.Gate.Stop()),                // Baishi's teleport
            (6000, () => m.Gate.Run(false)),         // its portal space
        };
        foreach (var (ms, tick) in phases)
        {
            if (ms == 0) { tick(); asks++; continue; }
            for (long end = m.Now + ms; m.Now < end; m.Now += GateTickMs) { tick(); asks++; }
        }
        Check(asks > 2000, $"the replay asks every tick ({asks} asks)");
        Check(m.Sent < 60, $"the trip sent {m.Sent} movement calls for {asks} asks (10-19: ~4,460 SetAutoRun and 97 StopCompletely)");
        Check(m.On <= 2 + 2 * (10000 / AutoRunGate.ReassertMs + 1), $"running sent {m.On} SetAutoRun(True)");
    }
}

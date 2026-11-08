using System;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The heal grace (ActionPacer.HealGraceMs, 2 s): a heal under a heal line no longer waits on a kit the
// server shows no sign of taking, a kit answered with a bare UseDone, a kit nobody answered, or a busy
// count nobody cleared. A kit with no result line used to hold every vital for 8 s (the kit wait's old
// time limit). The animation gate and the cast gate are unchanged: a heal still waits for a gesture.
internal static class HealGraceTests
{
    private const uint Player = 0x50000E41;
    private const uint Corpse = 0x80068AD1;
    private const uint Kit = 0x8004BF01;
    private const uint Potion = 0x8004BF02;
    private const uint Pack = Player;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    public static void Register(Runner r)
    {
        r.Add("heal grace: the kit wait ends on its line, its UseDone(0) with no line, 2 s with no sign the server took it, or 3.5 s", KitWaitVerdicts);
        r.Add("heal grace: a heal goes ahead of a kit on yourself nobody answered after 2 s; a corpse use, a potion and a non-heal still wait", PacerItemUseGrace);
        r.Add("heal grace: a plain heal still waits for the character's animation past 2 s (the animation gate is unchanged)", PacerGestureUnchanged);
        r.Add("heal grace: a live busy count holds a heal 2 s at most from its rise (not on top of the pacer's hold)", BusyCountGrace);
        r.Add("heal grace: KitPending lets a kit with no sign from the server go at 2 s (a retry)", KitNoProgressRetried);
        r.Add("heal grace: KitPending waits for a kit the server started up to 3.5 s", KitStartedWaitsToLimit);
        r.Add("heal grace: with neither GetCastBusyState nor the UseDone sequence, a kit waits the 3.5 s limit (no retry at 2 s)", KitNoSignalsWaitsToLimit);
        r.Add("heal grace: KitPending ends 700 ms after the kit's own UseDone(0) with no line; a later UseDone or a playing motion keeps it", KitAnsweredNoLine);
    }

    private static void KitWaitVerdicts()
    {
        const double G = ActionPacer.HealGraceMs;
        Check.Eq(BuffManager.KitWaitVerdict(500, -1, -1, false, false), BuffManager.KitWaitEnd.Waiting, "500 ms, nothing yet: wait");
        Check.Eq(BuffManager.KitWaitVerdict(G - 1, -1, -1, false, false), BuffManager.KitWaitEnd.Waiting, "just under the grace: wait");
        Check.Eq(BuffManager.KitWaitVerdict(G, -1, -1, false, false), BuffManager.KitWaitEnd.NotTaken, "the grace with no sign from the server: let go (sent again)");
        Check.Eq(BuffManager.KitWaitVerdict(G + 500, -1, -1, false, true), BuffManager.KitWaitEnd.Waiting, "animating now: the kit may be playing, wait");
        Check.Eq(BuffManager.KitWaitVerdict(G + 500, -1, -1, true, false), BuffManager.KitWaitEnd.Waiting, "the heal motion was seen: wait for its line");
        Check.Eq(BuffManager.KitWaitVerdict(3500, -1, -1, true, true), BuffManager.KitWaitEnd.TimedOut, "3.5 s whatever happened: let go");
        Check.Eq(BuffManager.KitWaitVerdict(1200, -1, 699, true, false), BuffManager.KitWaitEnd.Waiting, "UseDone(0) 699 ms ago, no line: wait");
        Check.Eq(BuffManager.KitWaitVerdict(1200, -1, 700, true, false), BuffManager.KitWaitEnd.Answered, "UseDone(0) 700 ms ago, no line: none is coming");
        Check.Eq(BuffManager.KitWaitVerdict(1200, -1, 700, true, true), BuffManager.KitWaitEnd.Waiting, "UseDone(0) but a motion plays: another action's answer, wait");
        Check.Eq(BuffManager.KitWaitVerdict(1200, 300, -1, true, false), BuffManager.KitWaitEnd.Waiting, "line seen 300 ms ago: the vitals grace runs");
        Check.Eq(BuffManager.KitWaitVerdict(9000, 300, -1, true, false), BuffManager.KitWaitEnd.Waiting, "a line seen: its grace runs out even past the limit");
        Check.Eq(BuffManager.KitWaitVerdict(1200, 700, -1, true, false), BuffManager.KitWaitEnd.Landed, "line seen 700 ms ago: done");
    }

    private static long _t;

    private static RynthCoreHost PacedWorld()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.SalvageCalls = true;
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.UseOnCalls = true;
        FakeHost.CombatModeCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.BusyCalls = true;
        FakeHost.CombatModeValue = 2;
        FakeHost.Names[Player] = "Tester";
        FakeHost.Names[Corpse] = "Corpse of Drumtaken Ursuin";
        FakeHost.Names[Kit] = "Treated Healing Kit";
        FakeHost.Names[Potion] = "Health Draught";
        FakeHost.Bitfields[Corpse] = 0x2000;
        FakeHost.Containers[Kit] = Pack;
        FakeHost.Containers[Potion] = Pack;
        _t = 20_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => _t;
        UseAudit.Reset("RynthAi", () => true);
        UseAudit.NowMs = () => _t;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => _t;
        var rec = typeof(RynthAiPlugin).GetMethod("RecordUseForPacing", Any)!;
        UseAudit.AfterSend = (h, u) => rec.Invoke(null, new object[] { h, u });
        return FakeHost.Create(Player);
    }

    private static void PaceOff()
    {
        ActionPacer.Enabled = false;
        ActionPacer.Reset();
        ActionPacer.NowMs = () => Environment.TickCount64;
        UseAudit.Reset("?", null);
        UseAudit.NowMs = () => Environment.TickCount64;
        MoveAudit.Reset();
        MoveAudit.NowMs = () => Environment.TickCount64;
        FakeHost.CastBusy = 0;
    }

    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));

    private static void PacerItemUseGrace()
    {
        try
        {
            var host = PacedWorld();
            host.UseOnFor(Kit, Player, "Buff", "vitals: healing kit on yourself");
            Check.True(ActionPacer.Current(host)?.Kind == PacedKind.ItemUse, "the kit is the outstanding action");
            _t += ActionPacer.HealGraceMs - 1;
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "1 ms under the grace: the heal waits for the kit's answer");
            _t += 1;
            Check.False(ActionPacer.MayStart(host, "Loot", HealNeed.None, out _), "at the grace: a non-heal still waits");
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "at the grace: the heal goes (the kit is sent again)");
            Check.True(Logged("heal grace"), "logged");

            // A potion (an item use with no target) keeps the pacer's own limit.
            FakeHost.UseDone(0);
            host.UseFor(Potion, "Buff", "vitals: drink");
            Check.True(ActionPacer.Current(host)?.Kind == PacedKind.ItemUse, "the potion is the outstanding action");
            _t += ActionPacer.HealGraceMs + 100;
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "a potion at 2.1 s: the heal still waits (not on yourself)");
            FakeHost.UseDone(0);
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "answered: the heal goes");

            // A corpse use keeps its rule: a plain heal waits for its answer (the server's turn to it).
            host.UseFor(Corpse, "Loot", "open");
            _t += 3900;
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "a corpse use at 3.9 s: the plain heal still waits");
            FakeHost.UseDone(0);
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "answered: the heal goes");
        }
        finally { PaceOff(); }
    }

    private static void PacerGestureUnchanged()
    {
        try
        {
            var host = PacedWorld();
            FakeHost.CastBusy = 1;   // the client's motion queue reads busy
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "animating: the heal waits");
            _t += ActionPacer.HealGraceMs + 500;
            Check.False(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "2.5 s of animation: a plain heal still waits (a send into a gesture draws 'too busy')");
            FakeHost.CastBusy = 0;
            Check.True(ActionPacer.MayStart(host, "Buff", HealNeed.Heal, out _), "the queue reads clear: the heal goes");
        }
        finally { PaceOff(); }
    }

    private static void BusyCountGrace()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.ServerGates = true;
        FakeHost.LastUseDoneCalls = true;
        FakeHost.CombatModeValue = CombatMode.Magic;
        var host = FakeHost.Create();
        var s = new LegacyUiSettings { IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0, UsePotions = false };
        var sm = new SpellManager(host, s);
        sm.InitializeNatively();
        var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 40, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
        var b = new BuffManager(host, s, sm, v);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        DateTime now = t0;
        b.Clock = () => now;
        var resets = new System.Collections.Generic.List<string>();
        b.SetStaleBusyCallback(why => resets.Add(why));

        FakeHost.UseDoneSeq = 5;
        b.BusyCount = 1;              // rises at t0: a count with no UseDone since, real as far as anyone can tell
        Check.True(b.BusyHoldsHeal(), "t=0: the count holds the heal");
        now = t0.AddMilliseconds(ActionPacer.HealGraceMs - 1);
        Check.True(b.BusyHoldsHeal(), "1 ms under the grace from the rise: held");
        now = t0.AddMilliseconds(ActionPacer.HealGraceMs);
        Check.False(b.BusyHoldsHeal(), "2 s after the rise: the heal goes");
        Check.Eq(b.StaleBusyClears, 1, "cleared once");
        Check.Eq(b.BusyCount, 0, "the count is reset");
        Check.True(resets.Count == 1 && resets[0].Contains("heal grace", StringComparison.Ordinal), "through the stale-busy callback, saying why");

        // First asked 1.9 s after the rise (the pacer's hold): the count holds it only 0.1 s more.
        now = t0.AddMilliseconds(10_000);
        b.BusyCount = 1;
        now = t0.AddMilliseconds(11_900);
        Check.True(b.BusyHoldsHeal(), "first asked 1.9 s after the rise: held");
        now = t0.AddMilliseconds(12_000);
        Check.False(b.BusyHoldsHeal(), "2.0 s after the rise: goes (no 2 s more on top of the pacer's hold)");

        // A count that only goes up further (1 -> 2) keeps its rise time.
        now = t0.AddMilliseconds(20_000);
        b.BusyCount = 1;
        now = t0.AddMilliseconds(21_000);
        b.BusyCount = 2;
        now = t0.AddMilliseconds(22_000);
        Check.False(b.BusyHoldsHeal(), "2 s after the first rise, 1 s after 1 -> 2: goes");
    }

    // -- KitPending and NoteKitProgress, driven through BuffManager (real time: the kit wait reads RynthClock)

    private sealed class Rig
    {
        public readonly BuffManager B;
        public readonly LegacyUiSettings S;
        public readonly RynthCoreHost H;
        public Rig(bool serverGates = true)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.ServerGates = serverGates;        // GetCastBusyState and the UseDone sequence
            FakeHost.LastUseDoneCalls = serverGates;
            FakeHost.CombatModeValue = CombatMode.Magic;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, 33)] = (400, 2);   // Life Magic
            var host = FakeHost.Create();
            H = host;
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false, EnableBuffing = false,
            };
            var skills = new CharacterSkills(host);
            skills.SetPlayerId(Player);
            var sm = new SpellManager(host, S);
            sm.InitializeNatively();
            sm.SetCharacterSkills(skills);
            var v = new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 40, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 };
            B = new BuffManager(host, S, sm, v);
            B.SetCharacterSkills(skills);
            B.MarkLiveBuffsReadyForTests();
        }
    }

    private static void SleepUntil(System.Diagnostics.Stopwatch sw, long ms)
    {
        long left = ms - sw.ElapsedMilliseconds;
        if (left > 0) System.Threading.Thread.Sleep((int)left);
    }

    private static void KitNoProgressRetried()
    {
        var r = new Rig();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.B.MarkKitUsed();
        Check.True(r.B.KitPending(), "kit just used: waiting");
        SleepUntil(sw, 1700);
        Check.True(r.B.KitPending(), "1.7 s, nothing from the server: still waiting");
        SleepUntil(sw, ActionPacer.HealGraceMs + 100);
        Check.False(r.B.KitPending(), "past the heal grace with no motion, UseDone or line: let go");
        Check.Eq(r.B.KitRetries, 1, "counted as a retry");
        Check.True(FakeHost.Logs.Any(l => l.Contains("shows no sign the server took it", StringComparison.Ordinal)), "logged");
    }

    // An engine with neither GetCastBusyState nor the UseDone sequence can't show the server took a kit:
    // no retry at the heal grace (the kit may be playing), only the 3.5 s limit.
    private static void KitNoSignalsWaitsToLimit()
    {
        var r = new Rig(serverGates: false);
        Check.False(r.H.HasGetCastBusyState || r.H.HasUseDoneSeq, "the rig: neither capability");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.B.MarkKitUsed();
        SleepUntil(sw, ActionPacer.HealGraceMs + 300);
        Check.True(r.B.KitPending(), "past the heal grace: still waiting (nothing could show the server took it)");
        Check.Eq(r.B.KitRetries, 0, "no retry at 2 s");
        SleepUntil(sw, 3500 + 150);
        Check.False(r.B.KitPending(), "past 3.5 s: let go");
    }

    private static void KitStartedWaitsToLimit()
    {
        var r = new Rig();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.B.MarkKitUsed();
        FakeHost.CastBusy = 1;                   // the heal motion plays
        r.B.OnHeartbeat();                       // the heartbeat records it (NoteKitProgress)
        FakeHost.CastBusy = 0;
        SleepUntil(sw, 3300);
        Check.True(r.B.KitPending(), "the server took it: 3.3 s, still waiting for its line");
        SleepUntil(sw, 3500 + 150);   // the 3.5 s limit, as a literal: the test must not follow the constant
        Check.False(r.B.KitPending(), "past 3.5 s: let go");
        Check.Eq(r.B.KitRetries, 1, "counted");
    }

    private static void KitAnsweredNoLine()
    {
        var r = new Rig();
        FakeHost.UseDoneSeq = 10;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        r.B.MarkKitUsed();
        FakeHost.UseDone(0);                     // the kit's own answer (seq + 1), no result line
        Check.True(r.B.KitPending(), "answered just now: the line could still be in flight");
        SleepUntil(sw, 550);
        Check.True(r.B.KitPending(), "550 ms after the answer: waiting");
        SleepUntil(sw, 850);
        Check.False(r.B.KitPending(), "700 ms after a UseDone(0) with no line: none is coming, let go");
        Check.True(FakeHost.Logs.Any(l => l.Contains("was answered (UseDone) with no result line", StringComparison.Ordinal)), "logged");

        // Not the kit's own answer: two UseDones since the send (another action's, then one more).
        var o = new Rig();
        FakeHost.UseDoneSeq = 20;
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        o.B.MarkKitUsed();
        FakeHost.UseDone(0);
        FakeHost.UseDone(0);
        Check.True(o.B.KitPending(), "seq + 2 is not the kit's answer");
        SleepUntil(sw2, 850);
        Check.True(o.B.KitPending(), "850 ms: still waiting (not taken as answered)");

        // A UseDone(0) while a motion plays (the heal motion of this kit; the UseDone was another action's).
        var m = new Rig();
        FakeHost.UseDoneSeq = 30;
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        m.B.MarkKitUsed();
        FakeHost.UseDone(0);
        FakeHost.CastBusy = 1;
        Check.True(m.B.KitPending(), "answered, the motion playing: waiting");
        SleepUntil(sw3, 900);
        Check.True(m.B.KitPending(), "900 ms, the motion still playing: still waiting for the line");
        FakeHost.CastBusy = 0;
        Check.False(m.B.KitPending(), "the motion over, no line: let go");
    }
}

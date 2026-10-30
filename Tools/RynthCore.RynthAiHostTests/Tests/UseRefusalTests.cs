using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-06, Drakkon on DreamWeave: from 06:18:44 every corpse/door use got the server's UseDone
// (no error) about one round trip later and nothing opened; a relog fixed it. The bot kept
// hammering corpses for 13 minutes. These tests cover:
//  - UseRefusalWatch: answered-but-nothing-opened opens -> one clean stop and retry -> "the server
//    isn't letting this character use objects" once, then a back-off with quiet probes;
//  - the same through RynthAi's corpse looting (fake clock, fake server);
//  - the trigger side: a corpse open waits for the arbiter's Looting tick, RynthAi's moves and
//    turns wait while a door/corpse use is in flight, and Combat's idle peace swap waits while
//    looting has work (on ACE a move or a mode change cancels the server's walk/turn to the corpse).
internal static class UseRefusalTests
{
    private const uint Player = 0x50000D01;
    private const uint CorpseA = 0x80068ACE;
    private const uint CorpseB = 0x80072023;
    private const uint ItemTypeContainer = 0x200;
    private const uint LongDescKey = 16;
    private const string Me = "Drakkon";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string StuckLine = "isn't letting this character use objects";

    public static void Register(Runner r)
    {
        r.Add("use check: three answered opens with nothing opened -> clean stop; a second corpse refused too -> stuck; probes back off; an open recovers", WatchEscalates);
        r.Add("use check: one corpse that never opens is dropped, not reported as a stuck server", WatchDropsSingleCorpse);
        r.Add("use check: a UseDone with an error, or no UseDone at all, counts as no refusal", WatchIgnoresErrorsAndSilence);
        r.Add("use check: the next open settles an answered one that opened nothing; an open resets", WatchSettlesOnNextOpen);
        r.Add("use check (looting): Drakkon's stall - stop once, retry, say it once in chat, stop opening corpses, probe, recover", LootingStall);
        r.Add("use check (looting): normal opens never trip it", LootingNormalOpens);
        r.Add("flight recorder: the use check asks the engine for a dump (clean stop, stuck); normal opens never do", FlightDumpRequests);
        r.Add("corpse open: a corpse claimed while Combat owns the tick is opened only on the Looting tick", OpenWaitsForLootingTick);
        r.Add("move hold: while a corpse use is in flight RynthAi's moves and turns wait, stops go through, UseDone or 1.5 s releases", MoveHoldWhileUseInFlight);
        r.Add("move hold: off unless the plugin opts in (RynthNav, RynthLua unchanged)", MoveHoldOptIn);
        r.Add("idle peace: Combat's idle peace swap waits while looting has work", IdlePeaceHeldWhileLooting);
        r.Add("idle peace: looting has work = a corpse claimed, a corpse open, a corpse in range, or a use in flight", LootHoldsIdlePeaceInputs);
        r.Add("use check jump: one jump per back-off, before the probe; none while normal or during the back-off", WatchProbeJump);
        r.Add("use check jump (looting): the clean stop jumps (TapJump, not JumpNonAutonomous), the retry waits the pause, an open after it is logged as cleared", LootingStallJumpClears);
        r.Add("use check jump (looting): still stuck after the jump -> back-off; the probe goes out only after a jump and the pause", LootingStallJumpBeforeProbe);
        r.Add("use check jump: no jump while the server still owes an answer to a cast (a jump would fail it)", NoJumpWhilePacerOutstanding);
    }

    // ── UseRefusalWatch alone ─────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly UseRefusalWatch W = new();
        public long T = 1_000_000;
        public int Seq;

        /// <summary>One open of <paramref name="id"/>: answered (UseDone err 0) after 60 ms, nothing opens.
        /// Returns the verdict of the tick after the grace.</summary>
        public UseRefusalWatch.Verdict RefusedOpen(int id)
        {
            var settle = W.SettleBeforeOpen(T);
            if (settle != UseRefusalWatch.Verdict.None) return settle;
            W.OnOpenSent(id, T, Seq);
            T += 60; Seq++;
            Check.Eq(W.OnTick(T, Seq, 0, true), UseRefusalWatch.Verdict.None, "answered: no verdict yet");
            T += UseRefusalWatch.AnswerGraceMs;
            var v = W.OnTick(T, Seq, 0, true);
            T += 500;
            return v;
        }
    }

    private static void WatchEscalates()
    {
        var r = new Rig();
        int a = unchecked((int)CorpseA), b = unchecked((int)CorpseB);
        Check.Eq(r.RefusedOpen(a), UseRefusalWatch.Verdict.None, "refusal 1");
        Check.Eq(r.RefusedOpen(a), UseRefusalWatch.Verdict.None, "refusal 2");
        Check.Eq(r.RefusedOpen(a), UseRefusalWatch.Verdict.CleanStopAndRetry, "refusal 3: clean stop and retry");
        Check.Eq(r.W.State, UseRefusalWatch.Phase.Retrying, "retrying");
        Check.Eq(r.RefusedOpen(a), UseRefusalWatch.Verdict.DropCorpse, "the retry refused on the same corpse: drop that corpse");
        Check.Eq(r.RefusedOpen(b), UseRefusalWatch.Verdict.Stuck, "a second corpse refused: the server refuses every use");
        Check.Eq(r.W.State, UseRefusalWatch.Phase.Stuck, "stuck");
        Check.Eq(r.W.BackoffMs, UseRefusalWatch.FirstBackoffMs, "first back-off");
        Check.True(r.W.HoldsOpens(r.T), "no opens during the back-off");
        Check.False(r.W.HoldsOpens(r.W.BackoffUntil), "the back-off ends");

        r.T = r.W.BackoffUntil;
        Check.Eq(r.RefusedOpen(b), UseRefusalWatch.Verdict.StillStuck, "the probe refused: still stuck");
        Check.Eq(r.W.BackoffMs, UseRefusalWatch.FirstBackoffMs * 2, "the back-off doubles");
        for (int i = 0; i < 5; i++) { r.T = r.W.BackoffUntil; r.RefusedOpen(a); }
        Check.Eq(r.W.BackoffMs, UseRefusalWatch.MaxBackoffMs, "capped");

        Check.Eq(r.W.OnOpened(r.T), UseRefusalWatch.Verdict.Recovered, "a corpse opened: recovered");
        Check.Eq(r.W.State, UseRefusalWatch.Phase.Normal, "back to normal");
        Check.Eq(r.W.Refusals, 0, "count cleared");
        Check.Eq(r.W.OnOpened(r.T), UseRefusalWatch.Verdict.None, "an open while normal says nothing");
    }

    private static void WatchDropsSingleCorpse()
    {
        var r = new Rig();
        int a = unchecked((int)CorpseA);
        r.RefusedOpen(a); r.RefusedOpen(a);
        Check.Eq(r.RefusedOpen(a), UseRefusalWatch.Verdict.CleanStopAndRetry, "three refusals");
        Check.Eq(r.RefusedOpen(a), UseRefusalWatch.Verdict.DropCorpse, "same corpse: dropped");
        Check.Eq(r.RefusedOpen(a), UseRefusalWatch.Verdict.DropCorpse, "still only that corpse: dropped again, never stuck");
        Check.Eq(r.W.OnOpened(r.T), UseRefusalWatch.Verdict.None, "another corpse opens: nothing to announce");
        Check.Eq(r.W.State, UseRefusalWatch.Phase.Normal, "normal");
        Check.Eq(r.W.RefusedCorpses.Count, 0, "the refused list is cleared by an open");
    }

    private static void WatchIgnoresErrorsAndSilence()
    {
        var w = new UseRefusalWatch();
        long t = 5_000; int seq = 7;
        for (int i = 0; i < 5; i++)
        {
            w.OnOpenSent(1, t, seq);
            seq++; t += 60;
            w.OnTick(t, seq, 0x1D, true);                   // "you're too busy"
            t += 3000;
            Check.Eq(w.OnTick(t, seq, 0x1D, true), UseRefusalWatch.Verdict.None, "a too-busy answer is no refusal");
            Check.Eq(w.SettleBeforeOpen(t), UseRefusalWatch.Verdict.None, "and settles nothing");
        }
        Check.Eq(w.Refusals, 0, "no refusals from errors");

        for (int i = 0; i < 5; i++)
        {
            w.OnOpenSent(1, t, seq);                        // the server never answers (a long walk, a lag)
            t += 2000;
            Check.Eq(w.OnTick(t, seq, 0, true), UseRefusalWatch.Verdict.None, "unanswered: no verdict");
            Check.Eq(w.SettleBeforeOpen(t), UseRefusalWatch.Verdict.None, "an unanswered open is not settled as refused");
        }
        w.OnOpenSent(1, t, seq);
        t += UseRefusalWatch.UnansweredForgetMs + 1;
        w.OnTick(t, seq, 0, true);
        seq++; t += 60;
        w.OnTick(t, seq, 0, true);                          // a late UseDone for something else
        t += 5000;
        Check.Eq(w.OnTick(t, seq, 0, true), UseRefusalWatch.Verdict.None, "a forgotten open is not judged by a later UseDone");
        Check.Eq(w.Refusals, 0, "no refusals from silence");
    }

    private static void WatchSettlesOnNextOpen()
    {
        var w = new UseRefusalWatch();
        long t = 10_000; int seq = 0;
        for (int i = 0; i < 2; i++)
        {
            Check.Eq(w.SettleBeforeOpen(t), UseRefusalWatch.Verdict.None, "below the threshold the open goes ahead");
            w.OnOpenSent(1, t, seq);
            seq++; t += 60;
            w.OnTick(t, seq, 0, true);
            t += 400;                                       // the next attempt comes before the grace
        }
        Check.Eq(w.Refusals, 1, "the first answered open was settled as refused by the second");
        Check.Eq(w.SettleBeforeOpen(t), UseRefusalWatch.Verdict.None, "the second one now");
        Check.Eq(w.Refusals, 2, "two");
        w.OnOpenSent(1, t, seq); seq++; t += 60; w.OnTick(t, seq, 0, true); t += 400;
        Check.Eq(w.SettleBeforeOpen(t), UseRefusalWatch.Verdict.CleanStopAndRetry, "the third: hold this open, clean stop first");
        w.OnOpened(t);
        Check.Eq(w.Refusals, 0, "an open resets");
        Check.Eq(w.State, UseRefusalWatch.Phase.Normal, "normal");
    }

    // ── Through RynthAi's corpse looting ──────────────────────────────────────

    private static (uint Cell, float X, float Y, float Z) Me0 => (0x6347028Au, 100f, 100f, 0f);

    private static void World()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;   // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;    // RequestId, HasAppraisalData, GetCurrentCombatMode
        FakeHost.ServerGates = true;    // GetUseDoneSeq
        FakeHost.LastUseDoneCalls = true;
        FakeHost.StopCalls = true;
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        foreach ((uint id, float dx) in new[] { (CorpseA, 1.0f), (CorpseB, 1.5f) })
        {
            FakeHost.Names[id] = "Corpse of Olthoi Swarm Worker";
            FakeHost.ItemTypes[id] = ItemTypeContainer;
            FakeHost.Strings[(id, LongDescKey)] = $"Killed by {Me}.";
            FakeHost.NotAttackable.Add(id);
            FakeHost.Positions[id] = (Me0.Cell, Me0.X + dx, Me0.Y, Me0.Z);
        }
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true,
            EnableLooting = true,
            EnableCombat = false,
            LootOwnership = 0, // My Kills Only
            LootContentSettleMs = 0,
            LootEmptyCorpseMs = 0,
            StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;

        public Bot(RynthCoreHost host)
        {
            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Cache.OnCreateObject(CorpseA);
            Cache.OnCreateObject(CorpseB);
            Cache.Tick();

            string profile = Path.Combine(Program.TempRoot, "use-refusal-test.json");
            var p = new LootProfile { Name = "nothing" };
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);
        public void Tick(BotActivity act)
        {
            Set("_activity", act);
            Call("TickCorpseOpening");
        }
    }

    /// <summary>Runs the fake clock with RynthAiPlugin.CorpseClock pointed at it.</summary>
    private static void WithClock(Action<Func<long>, Action<long>> body)
    {
        long now = 50_000_000;
        var saved = RynthAiPlugin.CorpseClock;
        RynthAiPlugin.CorpseClock = () => now;
        try { body(() => now, ms => now += ms); }
        finally { RynthAiPlugin.CorpseClock = saved; }
    }

    private static int ChatCount(string text) => FakeHost.Chat.Count(c => c.Contains(text));

    private static void LootingStall()
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        WithClock((now, advance) =>
        {
            // The server of 06:18:44 on: every open is answered (UseDone, no error) 60 ms later, nothing opens.
            long answerAt = 0;
            void Step()
            {
                int uses = FakeHost.Uses.Count;
                bot.Tick(BotActivity.Looting);
                if (FakeHost.Uses.Count > uses) answerAt = now() + 60;
                advance(100);
                if (answerAt != 0 && now() >= answerAt) { FakeHost.UseDone(0); answerAt = 0; }
            }

            int steps = 0;
            while (ChatCount(StuckLine) == 0 && steps++ < 600) Step();
            Check.Eq(ChatCount(StuckLine), 1, "the player is told, once (after " + steps / 10.0 + " s of looting)");
            Check.True(steps <= 400, "within 40 s of the first refused open (took " + steps / 10.0 + " s)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("one clean stop")), "the clean stop and retry came first");
            Check.True(FakeHost.Logs.Any(l => l.Contains("/Loot/UseCheck StopCompletely()")), "one StopCompletely from the use check");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Use check: facing 0x80068ACE")), "and the corpse is faced for the retry");
            Check.Near(FakeHost.HeadingDeg, 90.0, 0.5, "facing east, where the corpse lies");
            Check.True(FakeHost.Logs.Any(l => l.Contains("giving up on it")), "the first corpse, retried alone, was dropped before the verdict");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Use check: the server answers every use but nothing opens")), "the verdict is logged");
            Check.Eq(bot.Get<int>("_targetCorpseId"), 0, "no corpse held");

            int usesAtStuck = FakeHost.Uses.Count;
            Check.False((bool)bot.Call("HasLootWork", bot.S, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())!,
                "no loot work during the back-off: nav and combat run on");
            for (int i = 0; i < 2400; i++) Step();          // 4 minutes
            Check.Eq(FakeHost.Uses.Count, usesAtStuck, "no corpse is opened during the back-off");
            Check.Eq(ChatCount(StuckLine), 1, "and nothing more is said");

            for (int i = 0; i < 700 && FakeHost.Uses.Count == usesAtStuck; i++) Step();
            Check.Eq(FakeHost.Uses.Count, usesAtStuck + 1, "one probe after the back-off");
            for (int i = 0; i < 40; i++) Step();
            Check.True(FakeHost.Logs.Any(l => l.Contains("still refused")), "the probe refused: logged");
            Check.Eq(ChatCount(StuckLine), 1, "not said again");
            Check.Eq(FakeHost.Uses.Count, usesAtStuck + 1, "and backed off again");

            // A relog, or the server recovers: the next probe opens.
            int probeUses = FakeHost.Uses.Count;
            for (int i = 0; i < 6100 && FakeHost.Uses.Count == probeUses; i++)
            {
                bot.Tick(BotActivity.Looting);
                advance(100);
            }
            Check.Eq(FakeHost.Uses.Count, probeUses + 1, "the next probe after the longer back-off");
            uint probed = FakeHost.Uses.Last();
            bot.P.OnViewObjectContents(probed);
            Check.Eq(ChatCount("Objects open again"), 1, "the player is told uses work again");
            Check.False(bot.Get<UseRefusalWatch>("_useRefusals").HoldsOpens(now()), "looting is back on");
        });
    }

    private static void LootingNormalOpens()
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        WithClock((now, advance) =>
        {
            for (int round = 0; round < 6; round++)
            {
                uint id = round % 2 == 0 ? CorpseA : CorpseB;
                // Not completed yet: a fresh claim each round.
                bot.Get<System.Collections.Generic.Dictionary<int, long>>("_completedCorpses").Clear();
                int uses = FakeHost.Uses.Count;
                for (int i = 0; i < 30 && FakeHost.Uses.Count == uses; i++) { bot.Tick(BotActivity.Looting); advance(100); }
                Check.True(FakeHost.Uses.Count > uses, $"round {round}: an open went out");
                advance(200);
                FakeHost.UseDone(0);
                bot.P.OnViewObjectContents(FakeHost.Uses.Last());
                for (int i = 0; i < 30; i++) { bot.Tick(BotActivity.Looting); advance(100); }
            }
            var w = bot.Get<UseRefusalWatch>("_useRefusals");
            Check.Eq(w.Refusals, 0, "no refusals");
            Check.Eq(w.State, UseRefusalWatch.Phase.Normal, "normal");
            Check.Eq(ChatCount(StuckLine), 0, "nothing said");
            Check.False(FakeHost.Logs.Any(l => l.Contains("Use check:")), "nothing logged by the use check");
        });
    }

    /// <summary>The engine's flight recorder answers SendPluginCommand("RynthCore.FlightRecorder", "dump", reason).</summary>
    private static void FlightDumpRequests()
    {
        World();
        FakeHost.PluginCommandCalls = true;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        WithClock((now, advance) =>
        {
            long answerAt = 0;
            int steps = 0;
            while (ChatCount(StuckLine) == 0 && steps++ < 600)
            {
                int uses = FakeHost.Uses.Count;
                bot.Tick(BotActivity.Looting);
                if (FakeHost.Uses.Count > uses) answerAt = now() + 60;
                advance(100);
                if (answerAt != 0 && now() >= answerAt) { FakeHost.UseDone(0); answerAt = 0; }
            }
            var dumps = FakeHost.PluginCommands.Where(c => c.Plugin == "RynthCore.FlightRecorder").ToList();
            Check.True(dumps.Count >= 2, $"a dump asked at the clean stop and at the verdict ({dumps.Count})");
            Check.True(dumps.All(d => d.Action == "dump"), "all are dump requests");
            Check.True(dumps.Any(d => d.Value.StartsWith("RynthAi use check: corpse opens answered")), "the clean stop's reason");
            Check.True(dumps.Any(d => d.Value.StartsWith("RynthAi use check: server refuses every use")), "the verdict's reason");
        });

        // Normal opens: nothing asked.
        World();
        FakeHost.PluginCommandCalls = true;
        var host2 = FakeHost.Create(Player);
        var bot2 = new Bot(host2);
        WithClock((now, advance) =>
        {
            for (int round = 0; round < 4; round++)
            {
                bot2.Get<System.Collections.Generic.Dictionary<int, long>>("_completedCorpses").Clear();
                int uses = FakeHost.Uses.Count;
                for (int i = 0; i < 30 && FakeHost.Uses.Count == uses; i++) { bot2.Tick(BotActivity.Looting); advance(100); }
                advance(200);
                FakeHost.UseDone(0);
                bot2.P.OnViewObjectContents(FakeHost.Uses.Last());
                for (int i = 0; i < 30; i++) { bot2.Tick(BotActivity.Looting); advance(100); }
            }
            Check.False(FakeHost.PluginCommands.Any(c => c.Plugin == "RynthCore.FlightRecorder"), "no dump asked");
        });
    }

    private static void OpenWaitsForLootingTick()
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        WithClock((now, advance) =>
        {
            bot.Tick(BotActivity.Combat);       // the kill tick: Combat still owns it
            Check.Eq(bot.Get<int>("_targetCorpseId"), unchecked((int)CorpseA), "the corpse is claimed");
            Check.Eq(FakeHost.Uses.Count, 0, "but not opened while Combat owns the tick");
            advance(30);
            bot.Tick(BotActivity.Looting);      // the arbiter hands the tick to Looting (its nav stop goes first)
            Check.Eq(FakeHost.Uses.Count, 1, "opened on the Looting tick");
            Check.Eq(FakeHost.Uses[0], CorpseA, "the claimed corpse");
        });
    }

    // ── MoveAudit hold ────────────────────────────────────────────────────────

    private static void WithAuditClock(Action<Action<long>> body)
    {
        long now = 7_000_000;
        var savedUse = UseAudit.NowMs;
        var savedMove = MoveAudit.NowMs;
        UseAudit.NowMs = () => now;
        MoveAudit.NowMs = () => now;
        try { body(ms => now += ms); }
        finally
        {
            UseAudit.NowMs = savedUse;
            MoveAudit.NowMs = savedMove;
            UseAudit.TrackWorldUses = false;
            UseAudit.ClearWorldUse();
            MoveAudit.Reset();
            UseAudit.Reset("?", null);
        }
    }

    private static void MoveHoldWhileUseInFlight()
    {
        World();
        var host = FakeHost.Create(Player);
        WithAuditClock(advance =>
        {
            UseAudit.Reset("RynthAi", () => true);
            MoveAudit.Reset();
            UseAudit.TrackWorldUses = true;

            Check.True(host.UseFor(CorpseA, "Loot", "test open"), "the corpse use goes out");
            advance(10);
            Check.False(host.SetAutoRunBy("Nav", true), "autorun on is held");
            Check.False(host.TurnToHeadingBy("Nav", 90f), "a heading snap is held");
            Check.Eq(FakeHost.AutoRunCalls.Count, 0, "no autorun reached the client");
            Check.Eq(FakeHost.Turns, 0, "no turn reached the client");
            Check.True(host.StopCompletelyBy("Arbiter"), "a stop goes through");
            Check.Eq(FakeHost.Stops, 1, "one stop");
            host.SetAutoRunBy("Nav", false);
            Check.Eq(string.Join(",", FakeHost.AutoRunCalls), "False", "autorun off goes through");
            Check.Eq(FakeHost.Logs.Count(l => l.Contains(" held: the server is still answering the use of 0x80068ACE")), 1, "one 'held' line for the use");

            FakeHost.UseDone(0);                            // the server answered
            Check.True(host.SetAutoRunBy("Nav", true), "after the UseDone autorun goes out");
            Check.True(host.TurnToHeadingBy("Nav", 45f), "and turns");

            Check.True(host.UseFor(CorpseB, "Loot", "test open 2"), "a second use");
            Check.False(host.TurnToHeadingBy("Nav", 50f), "held again");
            advance(UseAudit.WorldUseInFlightMaxMs);        // no answer: the cap releases it
            Check.True(host.TurnToHeadingBy("Nav", 55f), "released after the cap");

            FakeHost.Uses.Clear();
            Check.True(host.UseFor(0x80001234, "Loot", "a non-world use (an item)"), "an item use");
            Check.True(host.TurnToHeadingBy("Nav", 60f), "an item use holds nothing");
        });
    }

    private static void MoveHoldOptIn()
    {
        World();
        var host = FakeHost.Create(Player);
        WithAuditClock(advance =>
        {
            UseAudit.Reset("RynthNav", null);
            MoveAudit.Reset();
            Check.False(UseAudit.TrackWorldUses, "off by default");
            host.UseFor(CorpseA, "Nav", "test open");
            Check.True(host.SetAutoRunBy("Nav", true), "nothing held without the opt-in");
            Check.True(host.TurnToHeadingBy("Nav", 90f), "turns too");
            Check.False(UseAudit.WorldUseInFlight(host, out _), "no use is tracked");
        });
    }

    // ── Combat's idle peace swap ──────────────────────────────────────────────

    private static void IdlePeaceHeldWhileLooting()
    {
        FakeHost.Reset();
        FakeHost.WeaponCalls = true;
        FakeHost.CombatModeCalls = true;
        FakeHost.CombatModeValue = 2;   // melee stance, no target: the idle swap would go to peace
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        var host = FakeHost.Create(Player);
        var s = new LegacyUiSettings { IsMacroRunning = true, EnableCombat = true, PeaceModeWhenIdle = true, MonsterRange = 50 };
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        var combat = new CombatManager(host, s, cache);
        combat.SetPlayerId(Player);
        bool looting = true;
        combat.HoldIdlePeace = () => looting;

        for (int i = 0; i < 5; i++) combat.Think();
        Check.Eq(FakeHost.CombatModeRequests.Count, 0, "no peace swap while looting has work");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("idle peace-mode swap held")), 1, "said once in the log");

        looting = false;
        combat.Think();
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "looting done: the idle swap to peace goes out");

        combat.HoldIdlePeace = null;
        FakeHost.CombatModeRequests.Clear();
        typeof(CombatManager).GetField("_lastPeaceAttempt", Any)!.SetValue(combat, DateTime.MinValue);
        combat.Think();
        Check.Eq(string.Join(",", FakeHost.CombatModeRequests), "1", "no hold set: as before");
    }

    private static void LootHoldsIdlePeaceInputs()
    {
        World();
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bool Holds() => (bool)bot.Call("LootHoldsIdlePeace")!;
        Check.False(Holds(), "nothing to loot: no hold");
        bot.Set("_targetCorpseId", unchecked((int)CorpseA));
        Check.True(Holds(), "a claimed corpse");
        bot.Set("_targetCorpseId", 0);
        bot.Set("_openedContainerId", unchecked((int)CorpseA));
        Check.True(Holds(), "an open corpse");
        bot.Set("_openedContainerId", 0);
        bot.Set("_lastWantLoot", true);
        Check.True(Holds(), "a corpse waiting in range (the arbiter's wantLoot)");
        bot.Set("_lastWantLoot", false);
        WithAuditClock(_ =>
        {
            UseAudit.Reset("RynthAi", () => true);
            UseAudit.TrackWorldUses = true;
            host.UseFor(CorpseB, "Loot", "test open");
            Check.True(Holds(), "a corpse use in flight");
        });
        Check.False(Holds(), "all clear: no hold");
    }

    // ── The jump (2026-10-06 afternoon) ───────────────────────────────────────
    // On ACE the player's jump (Player.HandleActionJump) is the one in-world action that empties the
    // server's animation queues (the stuck state); the use check jumps at the clean stop and before
    // each probe.

    private static void WatchProbeJump()
    {
        var r = new Rig();
        int a = unchecked((int)CorpseA), b = unchecked((int)CorpseB);
        Check.False(r.W.TakeProbeJump(r.T), "normal: no jump");
        r.RefusedOpen(a); r.RefusedOpen(a); r.RefusedOpen(a);
        Check.False(r.W.TakeProbeJump(r.T), "retrying: no probe jump (the clean stop jumps itself)");
        r.RefusedOpen(a);
        Check.Eq(r.RefusedOpen(b), UseRefusalWatch.Verdict.Stuck, "stuck");
        Check.False(r.W.TakeProbeJump(r.T), "during the back-off: no jump");
        r.T = r.W.BackoffUntil;
        Check.True(r.W.TakeProbeJump(r.T), "the back-off is over: jump before the probe");
        Check.False(r.W.TakeProbeJump(r.T + 100), "once");
        Check.Eq(r.RefusedOpen(b), UseRefusalWatch.Verdict.StillStuck, "the probe refused");
        Check.False(r.W.TakeProbeJump(r.T), "a new back-off: no jump yet");
        r.T = r.W.BackoffUntil;
        Check.True(r.W.TakeProbeJump(r.T), "again once the longer back-off is over");
        r.W.OnOpened(r.T);
        Check.False(r.W.TakeProbeJump(r.T + UseRefusalWatch.MaxBackoffMs), "after an open: never");
    }

    /// <summary>The 14:29 server: every open is answered 60 ms later and nothing opens, until the
    /// first open after a jump (when <paramref name="jumpClears"/>).</summary>
    private static void StallServer(Bot bot, Func<long> now, Action<long> advance, bool jumpClears, int maxSteps, Func<bool> until)
    {
        long answerAt = 0; uint answerFor = 0;
        for (int steps = 0; steps < maxSteps && !until(); steps++)
        {
            int uses = FakeHost.Uses.Count;
            bot.Tick(BotActivity.Looting);
            if (FakeHost.Uses.Count > uses) { answerAt = now() + 60; answerFor = FakeHost.Uses.Last(); }
            advance(100);
            if (answerAt != 0 && now() >= answerAt)
            {
                FakeHost.UseDone(0);
                if (jumpClears && FakeHost.TapJumps > 0)
                    bot.P.OnViewObjectContents(answerFor);
                answerAt = 0;
            }
        }
    }

    private static void LootingStallJumpClears()
    {
        World();
        FakeHost.TapJumpCalls = true;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        WithClock((now, advance) =>
        {
            int usesAtJump = -1; long jumpAt = 0;
            StallServer(bot, now, advance, jumpClears: false, maxSteps: 400, until: () =>
            {
                if (FakeHost.TapJumps > 0 && usesAtJump < 0) { usesAtJump = FakeHost.Uses.Count; jumpAt = now(); }
                return usesAtJump >= 0;
            });
            Check.Eq(FakeHost.TapJumps, 1, "the clean stop jumps once");
            Check.Eq(FakeHost.Jumps, 0, "with the keyboard jump, not JumpNonAutonomous (ACE ignores 0xF7C9)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("one clean stop, a jump")), "logged");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Use check: jump for the retry")), "the jump is logged");
            Check.False(FakeHost.Logs.Any(l => l.Contains("Use check: facing")), "no facing turn when it jumps");

            // The retry waits for the pause, then the server (cleared by the jump) opens it.
            long retryAt = 0;
            StallServer(bot, now, advance, jumpClears: true, maxSteps: 100, until: () =>
            {
                if (retryAt == 0 && FakeHost.Uses.Count > usesAtJump) retryAt = now();
                return bot.Get<int>("_openedContainerId") != 0;
            });
            Check.True(retryAt != 0, "a retry went out");
            Check.True(retryAt - jumpAt >= UseRefusalWatch.CleanStopPauseMs, $"after the pause ({retryAt - jumpAt} ms)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("after the use check's jump - the jump cleared")), "the open after the jump is logged as the fix");
            Check.Eq(ChatCount(StuckLine), 0, "the player is never told to relog");
            Check.Eq(bot.Get<UseRefusalWatch>("_useRefusals").State, UseRefusalWatch.Phase.Normal, "back to normal");
        });
    }

    private static void LootingStallJumpBeforeProbe()
    {
        World();
        FakeHost.TapJumpCalls = true;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        WithClock((now, advance) =>
        {
            StallServer(bot, now, advance, jumpClears: false, maxSteps: 600, until: () => ChatCount(StuckLine) > 0);
            Check.Eq(ChatCount(StuckLine), 1, "the jump didn't help here: the player is told once");
            Check.Eq(FakeHost.TapJumps, 1, "one jump so far (the clean stop)");
            Check.True(FakeHost.Logs.Any(l => l.Contains("the clean stop, the jump and retry didn't help")), "the verdict says the jump was tried");

            var w = bot.Get<UseRefusalWatch>("_useRefusals");
            int usesAtStuck = FakeHost.Uses.Count;
            for (int i = 0; i < 2400; i++) { bot.Tick(BotActivity.Looting); advance(100); }   // 4 minutes
            Check.Eq(FakeHost.TapJumps, 1, "no jump during the back-off");
            Check.Eq(FakeHost.Uses.Count, usesAtStuck, "no opens during the back-off");

            long probeJumpAt = 0;
            for (int i = 0; i < 700 && FakeHost.Uses.Count == usesAtStuck; i++)
            {
                bot.Tick(BotActivity.Looting);
                if (probeJumpAt == 0 && FakeHost.TapJumps == 2) probeJumpAt = now();
                advance(100);
            }
            Check.Eq(FakeHost.TapJumps, 2, "a jump before the probe");
            Check.Eq(FakeHost.Uses.Count, usesAtStuck + 1, "then the probe");
            Check.True(now() - 100 - probeJumpAt >= UseRefusalWatch.CleanStopPauseMs, "the probe waits the pause after the jump");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Use check: jump before the probe")), "logged");

            // The jump cleared it: the probe opens.
            bot.P.OnViewObjectContents(FakeHost.Uses.Last());
            Check.Eq(ChatCount("Objects open again"), 1, "the player is told uses work again");
            Check.True(FakeHost.Logs.Any(l => l.Contains("the jump cleared")), "and the log says the jump did it");
            Check.False(w.HoldsOpens(now()), "looting is back on");
        });
    }

    private static void NoJumpWhilePacerOutstanding()
    {
        World();
        FakeHost.TapJumpCalls = true;
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        long t = 10_000_000;
        ActionPacer.Reset();
        ActionPacer.Enabled = true;
        ActionPacer.NowMs = () => t;
        try
        {
            ActionPacer.Begin(host, PacedKind.Cast, 0, "Revitalize Self VI", "Buff", FakeHost.UseDoneSeq);
            Check.False((bool)bot.Call("TryUseCheckJump", t, "test")!, "no jump while the cast is unanswered");
            Check.Eq(FakeHost.TapJumps, 0, "nothing sent");
            Check.True(FakeHost.Logs.Any(l => l.Contains("Use check: no jump test - the server still owes an answer")), "logged");
            FakeHost.UseDone(0);
            t += 50;
            Check.True((bool)bot.Call("TryUseCheckJump", t, "test")!, "answered: the jump goes");
            Check.Eq(FakeHost.TapJumps, 1, "one jump");
        }
        finally
        {
            ActionPacer.Enabled = false;
            ActionPacer.Reset();
            ActionPacer.NowMs = () => Environment.TickCount64;
        }
    }
}

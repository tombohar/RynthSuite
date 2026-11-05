using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Structural audit 2026-10-21, finding 4: OnTick had one try/catch round the whole tick, so a step
// that threw every tick skipped everything after it, healing and combat included ("the bot just
// stood there and died"; one log line every 10 s). Each step now runs under its own guard
// (Diagnostics/TickGuard.cs). These drive the real OnTick with a step made to throw every tick
// (an object left half-built, as a null after a reload would) and check the rest still runs.
internal static class TickIsolationTests
{
    internal const uint Player = 0x50000F01;
    private const uint STypeLife = 33;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("tick isolation: a step before healing that throws every tick (quest tracker) doesn't stop the heal", HealDespiteEarlierFault);
        r.Add("tick isolation: chores that throw every tick (mana stones, AutoCram) don't stop combat's heartbeat", CombatDespiteChoreFault);
        r.Add("tick isolation: a chore failing every tick is switched off with one chat line, retried every 60 s, back on when it works", ChoreTripsAndRecovers);
        r.Add("tick isolation: healing failing every tick is never switched off; one chat line, and combat still runs", HealingNeverTrips);
        r.Add("tick guard: one log line per step per 10 s (with the count since), and nothing logged while nothing throws", GuardLogThrottle);
        r.Add("tick guard: trips only after 30 failures in a row over 5 s; a success in between starts the count again", GuardTripNeedsAStreak);
    }

    // ── The rig: the real plugin, logged in, macro on (ChoresInFightTests uses it too) ──

    internal sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S;
        public readonly PlayerVitalsCache V;
        public readonly WorldObjectCache Cache;
        public readonly RynthCoreHost Host;
        public long Now = 50_000_000;

        /// <param name="arrange">The test's own objects and host options, before the host exists.</param>
        public Bot(int healthPct = 100, bool combat = false, bool heal = true, Action? arrange = null)
        {
            PaceOff();
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.ServerGates = true;
            FakeHost.LastUseDoneCalls = true;
            FakeHost.CombatModeValue = CombatMode.Magic;
            foreach (var row in SpellTable.Rows) FakeHost.Spellbook.Add((uint)row.Id);
            FakeHost.Skills[(Player, STypeLife)] = (400, 2);
            FakeHost.Names[Player] = "Isolation Tester";
            FakeHost.Vitals = ((uint)healthPct, 100, 100, 100, 100, 100);
            arrange?.Invoke();
            Host = FakeHost.Create(Player);
            RynthLog.Init(Host, Path.Combine(Program.TempRoot, "TickIsolationLogs"));

            // No combat manager unless asked: the idle line applies, Heal At = Top Off HP = 60.
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, EnableCombat = combat, EnableLooting = false, EnableNavigation = false,
                EnableBuffing = false, HealAt = 60, TopOffHP = 60, EmergencyHealAt = 0, StaminaToHealthAt = 0,
                GetManaAt = 0, TopOffMana = 0, RestamAt = 0, TopOffStam = 0, UsePotions = false,
            };

            typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(P, new object[] { FakeHost.LastApi });
            var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
            typeof(LegacyDashboardRenderer).GetField("_profileLoaded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(dash, true);   // a stand-in with its profile in memory
            typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, S);
            // As UseAtLoginTests: every empty collection/lock field of the dashboard gets an instance.
            foreach (var f in typeof(LegacyDashboardRenderer).GetFields(Any))
            {
                if (f.GetValue(dash) != null || f.FieldType.IsValueType || f.FieldType == typeof(string)) continue;
                bool fillable = f.FieldType == typeof(object)
                    || (f.FieldType.Namespace?.StartsWith("System.Collections") == true && !f.FieldType.IsInterface && !f.FieldType.IsAbstract);
                if (fillable && f.FieldType.GetConstructor(Type.EmptyTypes) != null)
                    f.SetValue(dash, Activator.CreateInstance(f.FieldType));
            }
            Set("_dashboard", dash);
            Cache = new WorldObjectCache(Host);
            Cache.SetPlayerId(Player);
            Set("_objectCache", Cache);
            Set("_playerId", Player);
            Set("_loginComplete", true);
            Set("_loginCompletedAt", DateTime.Now.AddSeconds(-10));   // past the inventory settle window
            UseAudit.Reset("RynthAi", () => S.IsMacroRunning);

            V = new PlayerVitalsCache();
            Set("_vitals", V);
            if (heal)
            {
                var skills = new CharacterSkills(Host);
                skills.SetPlayerId(Player);
                var sm = new SpellManager(Host, S);
                sm.InitializeNatively();
                sm.SetCharacterSkills(skills);
                var b = new BuffManager(Host, S, sm, V);
                b.SetCharacterSkills(skills);
                b.MarkLiveBuffsReadyForTests();
                Set("_buffManager", b);
            }
            if (combat)
                Set("_combatManager", new CombatManager(Host, S, Cache));
            P.TickGuards.Clock = () => Now;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public object? Get(string f) => typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P);
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        /// <summary>The plugin field's own type, built without its constructor: every call into it throws.</summary>
        public object Broken(string field)
        {
            var type = typeof(RynthAiPlugin).GetField(field, Any)!.FieldType;
            object o = RuntimeHelpers.GetUninitializedObject(type);
            Set(field, o);
            return o;
        }

        public void Ticks(int n, long msEach = 100)
        {
            for (int i = 0; i < n; i++)
            {
                Now += msEach;
                P.OnTick();
            }
        }

        public TickGuard G => P.TickGuards;
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
    }

    internal static void Done() => UseAudit.Reset("?", null);

    private static int HealSelfCasts()
    {
        int cat = SpellTable.Named("Heal Self I").First().Category;
        return FakeHost.Casts.Count(c => SpellTable.ById(c.SpellId)?.Category == cat);
    }

    /// <summary>The quest tracker left mid-refresh with no record table: its Tick throws every call.</summary>
    private static void BreakQuestTracker(Bot bot)
    {
        object qt = bot.Broken("_questTracker");
        qt.GetType().GetField("_refreshing", Any)!.SetValue(qt, true);
        qt.GetType().GetField("_gotFirstQuest", Any)!.SetValue(qt, true);
    }

    private static string Steps(Bot bot) =>
        string.Join(", ", Enum.GetValues<TickStep>().Where(s => bot.G.Failures(s) > 0).Select(s => $"{s}={bot.G.Failures(s)}"));

    // ── The tests ────────────────────────────────────────────────────────────

    private static void HealDespiteEarlierFault()
    {
        try
        {
            var bot = new Bot(healthPct: 40);
            BreakQuestTracker(bot);
            bot.Ticks(5);
            Check.True(bot.G.Failures(TickStep.QuestTracker) >= 5, "the quest tracker threw every tick (" + Steps(bot) + ")");
            Check.Eq(HealSelfCasts(), 1, "Heal Self went out anyway (before the guards the whole tick was skipped)");
            Check.Eq(bot.G.Failures(TickStep.BuffsAndVitals), 0L, "healing itself ran through");
            Check.True(bot.G.Runs(TickStep.Meta) >= 5, "and the tick ran on to its end every time");
            Check.False(FakeHost.Logs.Any(l => l.Contains("OnTick exception")), "nothing reached the whole-tick catch");
            Check.True(FakeHost.Logs.Any(l => l.Contains("tick step quest tracker error: NullReferenceException")), "the fault is logged by step name");
        }
        finally { Done(); }
    }

    private static void CombatDespiteChoreFault()
    {
        try
        {
            var bot = new Bot(combat: true, heal: false);
            bot.Broken("_manaStoneManager");
            bot.Broken("_inventoryManager");
            bot.Ticks(10);
            Check.True(bot.G.Failures(TickStep.ManaStones) >= 10, "mana stones threw every tick");
            Check.True(bot.G.Failures(TickStep.CramStack) >= 10, "AutoCram/AutoStack threw every tick");
            Check.Eq(bot.G.Runs(TickStep.Combat), 10L, "combat's heartbeat ran on every tick after them");
            Check.Eq(bot.G.Failures(TickStep.Combat), 0L, "and ran through");
            Check.True(FakeHost.Logs.Any(l => l.StartsWith("Combat: state enableCombat=True", StringComparison.Ordinal)), "the real heartbeat ran (its state line)");
            Check.True(bot.G.Runs(TickStep.Meta) >= 10, "the tick ran to its end");
        }
        finally { Done(); }
    }

    private static void ChoreTripsAndRecovers()
    {
        try
        {
            var bot = new Bot(combat: true, heal: false);
            bot.Broken("_manaStoneManager");
            bot.Ticks(29);
            Check.False(bot.G.IsOff(TickStep.ManaStones), "29 failures: still on");
            bot.Ticks(30);   // 59 in a row, 5.9 s
            Check.True(bot.G.IsOff(TickStep.ManaStones), "failing every tick for 5 s: switched off");
            Check.Eq(FakeHost.Chat.Count(l => l.Contains("mana stones is off")), 1, "one chat line says so");
            Check.True(FakeHost.Chat.Any(l => l.Contains("mana stones is off") && l.Contains("NullReferenceException")), "with the error");

            long failures = bot.G.Failures(TickStep.ManaStones);
            long combatRuns = bot.G.Runs(TickStep.Combat);
            bot.Ticks(100);   // 10 s
            Check.Eq(bot.G.Failures(TickStep.ManaStones), failures, "while off it isn't called");
            Check.Eq(bot.G.Runs(TickStep.Combat), combatRuns + 100, "combat runs on every tick");

            bot.Ticks(1, msEach: TickGuard.RetryMs);   // the retry is due
            Check.Eq(bot.G.Failures(TickStep.ManaStones), failures + 1, "tried again after 60 s");
            Check.True(bot.G.IsOff(TickStep.ManaStones), "still broken: still off");
            Check.Eq(FakeHost.Chat.Count(l => l.Contains("mana stones")), 1, "no second chat line for a failed retry");

            bot.Set("_manaStoneManager", null);   // a reload rebuilt it (here: none, so the call is a no-op)
            bot.Ticks(1, msEach: TickGuard.RetryMs);
            Check.False(bot.G.IsOff(TickStep.ManaStones), "the retry ran through: back on");
            Check.True(FakeHost.Chat.Any(l => l.Contains("mana stones works again")), "and chat says so");
        }
        finally { Done(); }
    }

    private static void HealingNeverTrips()
    {
        try
        {
            var bot = new Bot(healthPct: 40, combat: true, heal: false);
            bot.Broken("_buffManager");
            bot.Ticks(100);   // 10 s
            Check.Eq(bot.G.Failures(TickStep.BuffsAndVitals), 100L, "healing and buffs failed on every tick");
            Check.False(bot.G.IsOff(TickStep.BuffsAndVitals), "and are never switched off");
            Check.Eq(FakeHost.Chat.Count(l => l.Contains("healing and buffs keeps failing")), 1, "one chat line says it keeps failing");
            Check.Eq(bot.G.Runs(TickStep.Combat), 100L, "combat ran on every tick");
            bot.Ticks(50);
            Check.Eq(bot.G.Failures(TickStep.BuffsAndVitals), 150L, "still tried every tick");
            Check.Eq(FakeHost.Chat.Count(l => l.Contains("healing and buffs")), 1, "and still one chat line");
        }
        finally { Done(); }
    }

    // ── TickGuard on its own ────────────────────────────────────────────────

    private static (TickGuard G, List<string> Log, List<string> Chat, Func<long> Advance) Guard(long stepMs = 100)
    {
        FakeHost.Reset();
        RynthLog.Init(FakeHost.Create(), Path.Combine(Program.TempRoot, "TickIsolationLogs"));
        var log = new List<string>();
        var chat = new List<string>();
        long now = 1_000_000;
        var g = new TickGuard(log.Add, chat.Add) { Clock = () => now };
        return (g, log, chat, () => now += stepMs);
    }

    private static void GuardLogThrottle()
    {
        var (g, log, chat, advance) = Guard();
        var ex = new InvalidOperationException("bad spell data");
        for (int i = 0; i < 100; i++) { g.Failed(TickStep.Salvage, ex); advance(); }   // one every 100 ms, the last at 9.9 s
        int summaries = FakeHost.Logs.Count(l => l.Contains("tick step salvage error: InvalidOperationException: bad spell data"));
        Check.Eq(summaries, 1, "100 failures inside 10 s: one summary line");
        Check.Eq(log.Count(l => l.Contains("since its last line")), 0, "and no count line yet (the trip at 5 s has its own line)");
        g.Failed(TickStep.Salvage, ex);   // at 10 s
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("tick step salvage error")), 2, "the next one 10 s after the first");
        Check.True(log.Any(l => l.Contains("'salvage' failed 100 time(s) since its last line (101 in a row)")), "with the count since the last line: " + string.Join(" | ", log));

        int before = FakeHost.Logs.Count;
        for (int i = 0; i < 50; i++) { g.Succeeded(TickStep.Combat); advance(); }
        Check.Eq(FakeHost.Logs.Count, before, "a step that runs through logs nothing");
        Check.Eq(g.Runs(TickStep.Combat), 50L, "but is counted");
        Check.False(chat.Any(l => l.Contains("combat")), "and says nothing in chat");
    }

    private static void GuardTripNeedsAStreak()
    {
        var (g, _, chat, advance) = Guard(stepMs: 200);
        var ex = new NullReferenceException();
        // Fails on every other call: never a streak, never off.
        for (int i = 0; i < 200; i++)
        {
            if (i % 2 == 0) g.Failed(TickStep.Pets, ex); else g.Succeeded(TickStep.Pets);
            advance();
        }
        Check.False(g.IsOff(TickStep.Pets), "failing on every other tick: never switched off");
        Check.Eq(chat.Count, 0, "no chat");

        // One every 100 ms: 30 in a row is not yet 5 s of failing.
        var (g2, _, chat2, advance2) = Guard(stepMs: 100);
        for (int i = 0; i < 30; i++) { g2.Failed(TickStep.Pets, ex); advance2(); }
        Check.False(g2.IsOff(TickStep.Pets), "30 in a row over 2.9 s: not yet");
        for (int i = 0; i < 21; i++) { g2.Failed(TickStep.Pets, ex); advance2(); }
        Check.True(g2.IsOff(TickStep.Pets), "51 in a row over 5 s: off");
        Check.False(g2.MayRun(TickStep.Pets), "and not run");
        Check.Eq(chat2.Count, 1, "one chat line");

        // An essential step: warned once per streak, never off; a success ends the streak.
        for (int i = 0; i < 60; i++) { g2.Failed(TickStep.Combat, ex); advance2(); }
        Check.False(g2.IsOff(TickStep.Combat), "combat is never switched off");
        Check.True(g2.MayRun(TickStep.Combat), "and always runs");
        Check.Eq(chat2.Count(l => l.Contains("combat keeps failing")), 1, "one warning");
        g2.Succeeded(TickStep.Combat);
        Check.Eq(g2.FailuresInARow(TickStep.Combat), 0, "a success ends the streak");
        for (int i = 0; i < 60; i++) { g2.Failed(TickStep.Combat, ex); advance2(); }
        Check.Eq(chat2.Count(l => l.Contains("combat keeps failing")), 2, "a new streak warns again");
    }
}

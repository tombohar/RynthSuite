using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using A = RynthCore.Plugin.RynthAi.LegacyUi.MetaActionType;
using C = RynthCore.Plugin.RynthAi.LegacyUi.MetaConditionType;

namespace RynthCore.RynthAiHostTests.Tests;

// The in-game self-test meta (GameTests\RynthSelfTest.af, 2026-10-22): RynthAi tests itself on a
// character by running this meta, and RynthCore's scripts\Run-GameTest.ps1 reads the results it saves
// (rt_<check> = PASS / FAIL / SKIP in the character's pvars). These check offline that it parses as
// written and that it walks its states and records results the way the script expects, on the fake
// clock with RynthAi's own chat lines fed in. Nothing here plays the game: the real run does that.
internal static class SelfTestMetaTests
{
    private const uint Player = 0x50000A01;
    private static readonly string[] States = { "Default", "T1 Buff", "T2 Fight", "T3 Loot", "T4 Craft", "T4 Craft run", "Done" };

    public static void Register(Runner r)
    {
        r.Add("self-test meta: RynthSelfTest.af parses cleanly and every SetState goes to one of its states", ParsesCleanly);
        r.Add("self-test meta: a run with nothing to fight or craft passes buffing, skips the rest, puts options back", PlaysThrough);
        r.Add("self-test meta: buffing fails after 300 s without \"Force Rebuff Complete\"", BuffTimesOut);
        r.Add("self-test meta: missile crafting passes on \"Equipped ammo\", fails on a crafting error", Crafting);
        r.Add("self-test meta: ratargetcount[] is combat's target count; the fight is a SKIP when combat finds nothing to attack", FightTargets);
    }

    // The first run on Freya (2026-10-22) failed the fight beside a buff bot: wobjectfindnearestmonster[]
    // found it (attackable), combat rightly left it alone, and after 180 s that read as a FAIL.
    private static void FightTargets()
    {
        var bare = new Run(Host());
        Check.Eq(bare.M.Expressions.Evaluate("ratargetcount[]"), "-1", "not wired: -1");
        bare.M.SetCombatTargetCount(() => 3);
        Check.Eq(bare.M.Expressions.Evaluate("ratargetcount[]"), "3", "wired: combat's count");
        Check.Eq(bare.M.Expressions.Evaluate("ralastchestlooted[]"), "0", "ralastchestlooted[] not wired: 0");
        bare.M.SetLastChestLooted(() => 0x80061193u);
        Check.Eq(bare.M.Expressions.Evaluate("ralastchestlooted[]"), "2147881363", "wired: the chest's id, as wobjectgetid[] gives it");

        Run Beside(int targets)
        {
            FakeHost.Reset();
            FakeHost.Names[Player] = "Selftest";
            FakeHost.Positions[Player] = (0xA9B4001Cu, 50f, 50f, 0f);
            const uint buffBot = 0x80000021;
            FakeHost.Names[buffBot] = "Pumpkin Buffer";
            FakeHost.Positions[buffBot] = (0xA9B4001Cu, 60f, 50f, 0f);
            FakeHost.ItemTypes[buffBot] = 0x10;
            var host = FakeHost.Create();
            var cache = FakeHost.MakeCache(host, Player, new[] { buffBot }, new Dictionary<uint, float> { [buffBot] = 1f });
            var r = new Run(host, cache, "T2 Fight");
            r.M.SetCombatTargetCount(() => targets);
            // What the Default state sets before the run reaches the fight. It came from an earlier test's
            // pvars file, which reached the disk only when its real-time save throttle allowed (a flake).
            r.M.Expressions.Evaluate("setpvar[rt_fight,`PENDING`]");
            return r;
        }

        var idle = Beside(0);
        idle.Tick();
        idle.Tick(2);
        Check.Eq(idle.P("rt_fight"), "PENDING", "something attackable in view: no SKIP at 2 s");
        Check.True(idle.S.EnableCombat, "combat on");
        idle.Tick(27);
        Check.Eq(idle.P("rt_fight"), "PENDING", "still waiting at 29 s");
        idle.Tick(2);
        Check.Eq(idle.P("rt_fight"), "SKIP", "SKIP at 30 s when combat's scan has no target");
        Check.True(idle.P("rt_fight_why").Contains("Pumpkin Buffer"), "names what was in view: " + idle.P("rt_fight_why"));

        var stuck = Beside(2);
        stuck.Tick();
        stuck.Tick(2);
        stuck.Tick(30);
        Check.Eq(stuck.P("rt_fight"), "PENDING", "combat has targets: no SKIP at 30 s");
        stuck.Tick(150);
        Check.Eq(stuck.P("rt_fight"), "FAIL", "targets but no kill in 180 s: FAIL");
        Check.True(stuck.P("rt_fight_why").Contains("combat sees 2 target(s)"), "says what combat saw: " + stuck.P("rt_fight_why"));
    }

    private static LoadedMeta Load()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "GameTests", "RynthSelfTest.af");
            if (File.Exists(path)) return AfFileParser.LoadFromText(File.ReadAllText(path));
        }
        throw new FileNotFoundException("GameTests\\RynthSelfTest.af not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<MetaRule> AllRules(IEnumerable<MetaRule> rules)
    {
        foreach (MetaRule r in rules)
        {
            yield return r;
            foreach (MetaRule c in AllRules(r.Children)) yield return c;
            foreach (MetaRule c in AllRules(r.ActionChildren)) yield return c;
        }
    }

    private static void ParsesCleanly()
    {
        LoadedMeta meta = Load();
        Check.Eq(string.Join(" | ", meta.Warnings), "", "no parse warnings");
        Check.Eq(string.Join(", ", meta.DeclaredStates), string.Join(", ", States), "the states, in order");
        Check.Eq(meta.StartState, "Default", "starts in Default");
        var all = AllRules(meta.Rules).ToList();
        // A DoAll's children are actions only (their Condition stays at the default, Never).
        var conditions = meta.Rules.Concat(meta.Rules.SelectMany(r => AllRules(r.Children)));
        Check.False(conditions.Any(r => r.Condition == C.Never), "no condition read as Never (an unknown keyword)");
        // An All's children are conditions only (their Action stays None); a DoAll's are the actions.
        var actions = meta.Rules.Concat(meta.Rules.SelectMany(r => AllRules(r.ActionChildren)));
        Check.False(actions.Any(r => r.Action == A.None), "no action read as None (an unknown keyword)");
        foreach (MetaRule r in all.Where(r => r.Action == A.SetMetaState))
            Check.True(meta.DeclaredStates.Contains(r.ActionData), $"SetState {{{r.ActionData}}} (in {r.State}) is a declared state");
        Check.True(all.Any(r => r.Action == A.ChatCommand && r.ActionData == "/ra stop"), "it stops the macro at the end");
    }

    private sealed class Run
    {
        public readonly LegacyUiSettings S;
        public readonly MetaManager M;
        public readonly List<string> Ra = new();
        public DateTime Now = new(2026, 10, 22, 12, 0, 0);

        public Run(RynthCoreHost host, WorldObjectCache? cache = null, string startState = "Default")
        {
            S = new LegacyUiSettings
            {
                IsMacroRunning = true, EnableMeta = true, CurrentState = startState, ForceStateReset = true,
                EnableCombat = true, EnableBuffing = true, EnableLooting = true, EnableNavigation = true,
                MetaRules = Load().Rules,
            };
            M = new MetaManager(S, host, new PlayerVitalsCache()) { Clock = () => Now };
            M.SetPlayerId(Player);
            M.SetRaCommandHandler(Ra.Add);
            if (cache != null) M.SetObjectCache(cache);
        }

        public void Tick(double seconds = 0) { Now = Now.AddSeconds(seconds); M.Think(); }

        /// <summary>Ticks a second at a time until the meta is in <paramref name="state"/>, at most <paramref name="max"/> times.</summary>
        public bool TickUntil(string state, int max = 20)
        {
            for (int i = 0; i < max && !string.Equals(S.CurrentState, state, StringComparison.OrdinalIgnoreCase); i++) Tick(1);
            return string.Equals(S.CurrentState, state, StringComparison.OrdinalIgnoreCase);
        }

        public void Chat(string line) { M.HandleChat(line); Tick(); }
        public string P(string key) => M.Expressions.Evaluate($"getpvar[{key}]");
    }

    private static RynthCoreHost Host()
    {
        FakeHost.Reset();
        FakeHost.Names[Player] = "Selftest";
        return FakeHost.Create();
    }

    private static void PlaysThrough()
    {
        var run = new Run(Host());
        run.Tick();
        Check.False(run.S.EnableCombat || run.S.EnableBuffing || run.S.EnableLooting || run.S.EnableNavigation, "Default turns combat, buffing, looting and nav off");
        Check.Eq(run.P("rt_status"), "RUNNING", "rt_status RUNNING");
        Check.Eq(run.P("rt_meta"), "PASS", "rt_meta PASS");
        Check.Eq(run.P("rt_buff"), "PENDING", "rt_buff PENDING until tested");

        Check.True(run.TickUntil("T1 Buff"), "on to T1 Buff");
        run.Tick();
        Check.True(run.S.EnableBuffing, "buffing on for the test");
        Check.True(run.Ra.Any(c => c.Contains("buff force")), "asks for a force rebuff");
        run.Chat("[RynthAi] Force Rebuff Complete.");
        Check.Eq(run.P("rt_buff"), "PASS", "buffing PASS on \"Force Rebuff Complete\"");
        Check.True(run.P("rt_buff_why").StartsWith("a force rebuff finished in "), "says how long it took: " + run.P("rt_buff_why"));

        Check.True(run.TickUntil("Done"), "no monster, no kill, no bundles: on to Done");
        Check.Eq(run.P("rt_fight"), "SKIP", "fight SKIP with no monster in view");
        Check.Eq(run.P("rt_loot"), "SKIP", "looting SKIP without a kill");
        Check.Eq(run.P("rt_craft"), "SKIP", "missile crafting SKIP without bundles");
        run.Tick();
        Check.Eq(run.P("rt_status"), "DONE", "rt_status DONE");
        Check.True(run.S.EnableCombat && run.S.EnableBuffing && run.S.EnableLooting && run.S.EnableNavigation, "options put back as they were");
        run.Tick(3);
        Check.True(run.Ra.Any(c => c.TrimEnd().EndsWith("stop")), "stops the macro");
    }

    private static void BuffTimesOut()
    {
        var run = new Run(Host());
        Check.True(run.TickUntil("T1 Buff"), "on to T1 Buff");
        run.Tick();
        run.Tick(299);
        Check.Eq(run.P("rt_buff"), "PENDING", "still waiting at 299 s");
        run.Tick(2);
        Check.Eq(run.P("rt_buff"), "FAIL", "FAIL after 300 s");
        Check.True(run.P("rt_buff_why").Contains("300 s"), "says why: " + run.P("rt_buff_why"));

        var wedged = new Run(Host());
        wedged.TickUntil("T1 Buff");
        wedged.Tick();
        wedged.Chat("[RynthAi] Can't enter Magic mode (no wand found) - buffing paused.");
        Check.Eq(wedged.P("rt_buff"), "FAIL", "FAIL at once on a buffing error");
        Check.True(wedged.P("rt_buff_why").StartsWith("Can't enter Magic mode"), "the error is the reason: " + wedged.P("rt_buff_why"));
    }

    private static (RynthCoreHost, WorldObjectCache) Archer()
    {
        var host = Host();
        FakeHost.Names[0x60000001] = "Bundle of Deadly Prismatic Arrowheads";
        FakeHost.Names[0x60000002] = "Bundle of Arrowshafts";
        FakeHost.Containers[0x60000001] = Player;
        FakeHost.Containers[0x60000002] = Player;
        host = FakeHost.Create();
        return (host, FakeHost.MakeCache(host, Player, new uint[] { 0x60000001, 0x60000002 }));
    }

    private static void Crafting()
    {
        var (host, cache) = Archer();
        var run = new Run(host, cache, "T4 Craft");
        Check.True(run.TickUntil("T4 Craft run"), "bundles carried, no melee weapon or wand in hand: the craft runs");
        run.Tick();
        run.Chat("[RynthAi Craft] No wielded Arrows. Evaluating...");
        run.Chat("[RynthAi Craft] Crafting Deadly Prismatic Arrow x2");
        Check.Eq(run.P("rt_craft"), "PENDING", "not decided while crafting");
        run.Chat("[RynthAi Craft] Equipped ammo (id=0x80001234)");
        Check.Eq(run.P("rt_craft"), "PASS", "PASS on \"Equipped ammo\"");
        Check.True(run.P("rt_craft_why").StartsWith("made ammo from the bundles"), "says ammo was made: " + run.P("rt_craft_why"));
        Check.Eq(run.S.CurrentState, "Done", "then Done");

        var (host2, cache2) = Archer();
        var rewield = new Run(host2, cache2, "T4 Craft");
        rewield.TickUntil("T4 Craft run");
        rewield.Tick();
        rewield.Chat("[RynthAi Craft] Equipping Deadly Prismatic Arrow");
        rewield.Chat("[RynthAi Craft] Equipped ammo (id=0x80001234)");
        Check.True(rewield.P("rt_craft_why").StartsWith("put carried ammo back on"), "says carried ammo went back on: " + rewield.P("rt_craft_why"));

        var (host3, cache3) = Archer();
        var broken = new Run(host3, cache3, "T4 Craft");
        broken.TickUntil("T4 Craft run");
        broken.Tick();
        broken.Chat("[RynthAi Craft] Craft timed out — no response from server");
        Check.Eq(broken.P("rt_craft"), "FAIL", "FAIL on a crafting error");
        Check.True(broken.P("rt_craft_why").StartsWith("Craft timed out"), "the error is the reason: " + broken.P("rt_craft_why"));

        var (host4, cache4) = Archer();
        var silent = new Run(host4, cache4, "T4 Craft");
        silent.TickUntil("T4 Craft run");
        silent.Tick();
        silent.Tick(76);
        Check.Eq(silent.P("rt_craft"), "FAIL", "FAIL when Missile Crafting says nothing for 75 s (the 10.19.1 break)");
    }
}

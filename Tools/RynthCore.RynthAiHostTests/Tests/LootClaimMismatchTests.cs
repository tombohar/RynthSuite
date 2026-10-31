using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// R-20261031-203155-cea0 (Conquest, indoors, RynthAi 2026.10.31.1): "Plugin says its looting but it
// doesn't." The arbiter logged Looting with want[loot=True] for 12 s after a kill, and not one
// "Corpse loot:" line followed (no [LootSettings] line the whole session either). Boost Nav Priority
// was on with no route to run: the arbiter's BoostNav clause is guarded by WantNav, so Looting won,
// while TickCorpseOpening's full stop took the flag alone and reset every claim. Owner's rule
// (2026-10-31): Boost Nav Priority means no looting at all, so HasLootWork offers nothing either. The kill's corpse
// ('Treasure of ...', no corpse name) was first filed as a no-position inventory Container and
// rescued to Corpse by its weenie flag 3 s later; it must be claimed and looted like any other.
// The loot clock (CorpseOpenController.CorpseClock) is moved by hand; the server answers at once.
internal static class LootClaimMismatchTests
{
    private const uint Player = 0x50000D51;
    private const uint Corpse = 0x80265100;
    private const uint Gem = 0x80265101;
    private const uint ItemTypeContainer = 0x200, ItemTypeGem = 0x800;
    private const uint BfCorpse = 0x2000;
    private const uint LongDescKey = 16;
    private const string Me = "Test Hunter";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const long TickMs = 25;
    private static (uint Cell, float X, float Y, float Z) Me0 => (0x482D0102u, 40f, 60f, 0f);

    public static void Register(Runner r)
    {
        r.Add("loot claim (R-20261031-203155-cea0): a corpse first filed as a no-position inventory Container, then rescued, is claimed and looted", RescuedCorpseLooted);
        r.Add("loot claim (R-20261031-203155-cea0): Boost Nav Priority with no route: no loot work, never in Looting; looted once the flag is off", BoostNavNoRouteLoots);
        r.Add("loot claim (R-20261031-203155-cea0): Boost Nav Priority with a route, then none: nav has the tick, nothing claimed either way", BoostNavWithRouteYields);
        r.Add("loot claim (R-20261031-203155-cea0): Looting with nothing claimed is logged with the reason after 3 s, then throttled", LootStallLogged);
    }

    // ── Clock and world ───────────────────────────────────────────────────────

    private static long _now;

    private static void Clocked(Action body)
    {
        var old = RynthAiPlugin.CorpseClock;
        _now = 80_000_000;
        RynthAiPlugin.CorpseClock = () => _now;
        try { body(); }
        finally { RynthAiPlugin.CorpseClock = old; }
    }

    /// <summary>The report's corpse: its name is no corpse name, and it is created before its
    /// position and weenie flag read (the "nopos-item" branch files it as an inventory Container).</summary>
    private static void World()
    {
        FakeHost.Reset();
        FakeHost.SalvageCalls = true;   // UseObject + MoveItemExternal
        FakeHost.WeaponCalls = true;    // RequestId, HasAppraisalData
        FakeHost.Names[Player] = Me;
        FakeHost.Positions[Player] = Me0;
        FakeHost.PlayerPose = Me0;
        FakeHost.Bitfields[Player] = 0;  // installs GetObjectBitfield; the corpse's flag reads 0 for now
        FakeHost.Names[Corpse] = "Treasure of Corrupted Dread";
        FakeHost.ItemTypes[Corpse] = ItemTypeContainer;
        FakeHost.Strings[(Corpse, LongDescKey)] = $"Killed by {Me}.";
        FakeHost.NotAttackable.Add(Corpse);
        FakeHost.Names[Gem] = "Black Opal";
        FakeHost.ItemTypes[Gem] = ItemTypeGem;
        FakeHost.Containers[Gem] = Corpse;   // also installs GetObjectOwnershipInfo
        FakeHost.Appraised.Add(Gem);
    }

    private sealed class Bot
    {
        public readonly RynthAiPlugin P = new();
        public readonly LegacyUiSettings S = new()
        {
            IsMacroRunning = true, EnableLooting = true, EnableCombat = false, LootOwnership = 0,
            LootContentSettleMs = 0, LootEmptyCorpseMs = 0, StopLootingWhenPackFull = false,
        };
        public readonly WorldObjectCache Cache;
        private int _seenMoves;

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
            Set("_vitals", new PlayerVitalsCache { MaxHealth = 100, CurrentHealth = 100, MaxStamina = 100, CurrentStamina = 100, MaxMana = 100, CurrentMana = 100 });

            string profile = Path.Combine(Program.TempRoot, "loot-claim-mismatch.json");
            var p = new LootProfile { Name = "gems" };
            var rule = new LootRule { Name = "Gems", Action = LootAction.Keep };
            rule.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
            p.Rules.Add(rule);
            p.Save(profile);
            S.CurrentLootPath = profile;
        }

        public void Set(string f, object? v) => typeof(RynthAiPlugin).GetField(f, Any)!.SetValue(P, v);
        public T Get<T>(string f) => (T)typeof(RynthAiPlugin).GetField(f, Any)!.GetValue(P)!;
        public object? Call(string m, params object[] a) => typeof(RynthAiPlugin).GetMethod(m, Any)!.Invoke(P, a);

        public bool HasLootWork() => (bool)Call("HasLootWork", S, _now)!;
        public bool NavWants() => (bool)Call("NavWantsTick", S)!;

        /// <summary>The arbiter's decision from the real wants, as OnTick builds it (no buffing, no combat).</summary>
        public BotActivity Decide()
        {
            var inputs = new ArbiterInputs(S.IsMacroRunning, false, false, HasLootWork(), false, NavWants(),
                boostNav: S.BoostNavPriority, boostLoot: S.BoostLootPriority);
            return ActivityArbiter.Decide(in inputs);
        }

        /// <summary>One plugin tick: the clock moves on, the arbiter decides, the looter runs, and
        /// the server lands each move in the pack at once.</summary>
        public BotActivity Tick()
        {
            _now += TickMs;
            BotActivity act = Decide();
            Set("_activity", act);
            Call("TickCorpseOpening");
            while (_seenMoves < FakeHost.ExternalMoves.Count)
            {
                var m = FakeHost.ExternalMoves[_seenMoves++];
                FakeHost.Containers[m.Id] = m.Target;
            }
            return act;
        }

        public bool Completed => Get<Dictionary<int, long>>("_completedCorpses").ContainsKey(unchecked((int)Corpse));
        public int Claimed => Get<int>("_targetCorpseId");
    }

    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));
    private static int LoggedCount(string part) => FakeHost.Logs.Count(l => l.Contains(part, StringComparison.Ordinal));

    /// <summary>The corpse created without a position or flag (filed as inventory), then its
    /// position and flag land and the 2 s pass rescues it.</summary>
    private static Bot RescuedCorpse()
    {
        var host = FakeHost.Create(Player);
        var bot = new Bot(host);
        bot.Cache.OnCreateObject(Corpse);
        bot.Cache.OnCreateObject(Gem);
        for (int i = 0; i < 20; i++) bot.Cache.Tick();
        Check.Eq(bot.Cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Container, "first filed as a Container (the report's state)");
        Check.True(bot.Cache.GetInventory().Any(o => o.Id == unchecked((int)Corpse)), "in the inventory");
        Check.False(bot.HasLootWork(), "no loot work while it sits in the inventory");

        FakeHost.Positions[Corpse] = (Me0.Cell, Me0.X + 1.0f, Me0.Y, Me0.Z);
        FakeHost.Bitfields[Corpse] = BfCorpse;
        typeof(WorldObjectCache).GetField("_lastReclassifyTime", Any)!.SetValue(bot.Cache, DateTime.MinValue);
        bot.Cache.Tick();
        Check.True(Logged("was filed as Container (inventory, no position when classified); now a Corpse"), "the rescue is logged as in the report");
        Check.Eq(bot.Cache[unchecked((int)Corpse)]?.ObjectClass, AcObjectClass.Corpse, "rescued to Corpse");
        Check.True(bot.Cache.GetLandscapeObjects().Any(o => o.Id == unchecked((int)Corpse)), "on the landscape");
        return bot;
    }

    /// <summary>Ticks to the end of the corpse; the server opens it on the first use.</summary>
    private static void LootToTheEnd(Bot bot)
    {
        long end = _now + 30_000;
        bool opened = false;
        while (_now < end && !bot.Completed)
        {
            bot.Tick();
            if (!opened && FakeHost.Uses.Contains(Corpse))
            {
                opened = true;
                FakeHost.UseDone(0);
                bot.P.OnViewObjectContents(Corpse);
            }
        }
    }

    // ── 1. The rescued corpse ─────────────────────────────────────────────────

    private static void RescuedCorpseLooted() => Clocked(() =>
    {
        World();
        var bot = RescuedCorpse();
        Check.True(bot.HasLootWork(), "the arbiter counts it as loot work");
        Check.Eq(bot.Tick(), BotActivity.Looting, "Looting has the tick");
        Check.Eq(bot.Claimed, unchecked((int)Corpse), "and the claim takes the rescued corpse");
        Check.True(Logged("claimed corpse target 0x80265100"), "claim logged");

        LootToTheEnd(bot);
        Check.True(FakeHost.ExternalMoves.Any(m => m.Id == Gem && m.Target == Player), "the gem is moved into the pack");
        Check.True(bot.Completed, "the corpse is finished");
        Check.False(bot.HasLootWork(), "no loot work left: the bot leaves Looting");
    });

    // ── 2. The report: Boost Nav Priority on, nothing to navigate ─────────────

    // Owner, 2026-10-31: Boost Nav Priority means no looting at all, route or not.
    private static void BoostNavNoRouteLoots() => Clocked(() =>
    {
        World();
        var bot = RescuedCorpse();
        bot.S.BoostNavPriority = true;
        bot.S.BoostLootPriority = true;   // both on, as in the report: nav wins
        bot.S.EnableNavigation = true;    // on, but no route loaded (want[nav=False] in the report)
        Check.False(bot.NavWants(), "nav has nothing to run");
        Check.False(bot.HasLootWork(), "Boost Nav Priority: no loot work, the corpse is passed by");
        Check.True(bot.Decide() != BotActivity.Looting, "the arbiter never gives Looting the tick");

        for (int i = 0; i < 200; i++) Check.True(bot.Tick() != BotActivity.Looting, "never in Looting (5 s)");
        Check.Eq(bot.Claimed, 0, "nothing claimed");
        Check.False(FakeHost.Uses.Contains(Corpse), "nothing opened");
        Check.False(Logged("Looting has held the tick"), "no stall: Looting never had the tick");

        bot.S.BoostNavPriority = false;   // the flag off: the corpse is looted as usual
        Check.True(bot.HasLootWork(), "loot work again");
        bot.Tick();
        Check.Eq(bot.Claimed, unchecked((int)Corpse), "the corpse is claimed");
        LootToTheEnd(bot);
        Check.True(bot.Completed, "looted to the end");
        Check.True(FakeHost.ExternalMoves.Any(m => m.Id == Gem && m.Target == Player), "the gem is in the pack");
    });

    // ── 3. Boost Nav Priority with a route still yields to nav ────────────────

    private static void BoostNavWithRouteYields() => Clocked(() =>
    {
        World();
        var bot = RescuedCorpse();
        bot.S.BoostNavPriority = true;
        bot.S.EnableNavigation = true;
        var route = new NavRouteParser { RouteType = NavRouteType.Linear };
        route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 10.5, EW = -3.25 });
        route.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 11.5, EW = -4.25 });
        bot.S.CurrentRoute = route;
        Check.True(bot.NavWants(), "nav has a route to run");
        Check.False(bot.HasLootWork(), "no loot work to offer: the looter stands down for the route");
        Check.Eq(bot.Decide(), BotActivity.Navigating, "nav has the tick");

        for (int i = 0; i < 200; i++) bot.Tick();   // 5 s
        Check.Eq(bot.Claimed, 0, "nothing claimed");
        Check.False(FakeHost.Uses.Contains(Corpse), "nothing opened");
        Check.False(Logged("Looting has held the tick"), "not a Looting stall: Looting never had the tick");

        bot.S.CurrentRoute = null;   // the route is unloaded: still no looting while the flag is on
        Check.True(bot.Tick() != BotActivity.Looting, "no route, flag on: still no Looting");
        Check.Eq(bot.Claimed, 0, "still nothing claimed");
    });

    // ── 4. A stall is never silent ────────────────────────────────────────────

    private static void LootStallLogged() => Clocked(() =>
    {
        World();
        var bot = RescuedCorpse();
        bot.Set("_lootHaltUntil", _now + 60_000);   // the loot-wedge halt: no claim while the corpse waits in range
        const string Stall = "Looting has held the tick";

        Check.Eq(bot.Tick(), BotActivity.Looting, "the corpse in range gives Looting the tick");
        Check.Eq(bot.Claimed, 0, "nothing claimed");
        for (int i = 0; i < (int)(RynthAiPlugin.LootStallLogMs / TickMs) - 10; i++) bot.Tick();
        Check.False(Logged(Stall), "a short gap is not a stall");
        for (int i = 0; i < 20; i++) bot.Tick();
        Check.Eq(LoggedCount(Stall), 1, "past LootStallLogMs the stall is logged once");
        string line = FakeHost.Logs.First(l => l.Contains(Stall, StringComparison.Ordinal));
        Check.True(line.Contains("loot-wedge halt"), $"with the claim's skip reason ({line})");
        Check.True(line.Contains("loot work: 1 lootable corpse(s) in range, e.g. 0x80265100"), $"and what the arbiter counted as loot work ({line})");

        for (int i = 0; i < 200; i++) bot.Tick();   // 5 s more
        Check.Eq(LoggedCount(Stall), 1, "throttled: not again within LootStallRepeatMs");
        for (int i = 0; i < (int)(RynthAiPlugin.LootStallRepeatMs / TickMs); i++) bot.Tick();
        Check.Eq(LoggedCount(Stall), 2, "logged again after LootStallRepeatMs");

        bot.Set("_lootHaltUntil", 0L);
        bot.Tick();
        Check.Eq(bot.Claimed, unchecked((int)Corpse), "once the halt ends the corpse is claimed");
        Check.True(Logged("the Looting stall is over"), "and the end of the stall is logged");
    });
}

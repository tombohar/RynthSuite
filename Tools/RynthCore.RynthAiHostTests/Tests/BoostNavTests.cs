using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginCore;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Boost Nav Priority, owner's corrected rule (2026-11-02, R-20261103-000159-a65e): "Boost nav
// priority should only run the nav route, not from mob to mob. So when no nav route is loaded it
// should fight." And: "When a 'once' nav route is at the end it should hunt as well." While a nav
// route (or a follow) runs, RynthAi only follows it: no hunting, no looting, no fighting back. With
// no route, a Once route at its end, or the Outdoor Hunt (its walks go from monster to monster),
// the flag does nothing: RynthAi hunts, fights and loots as with it off. Buffing stays on top.
// The first fight or corpse the flag skips along a route is said in chat, once per session
// (R-20261102-041824-3f7c: the flag came on quietly through NavLootPrio).
internal static class BoostNavTests
{
    private const uint Player = 0x50000F51;
    private const uint Lb = 0xA9B40000;
    private const uint Mob = 0x80000451;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("boost nav (R-20261103-000159-a65e): with a route running the arbiter never picks Combat or Looting; without, the usual order; buffing on top", ArbiterRule);
        r.Add("boost nav (R-20261103-000159-a65e): a nav route running: loaded, follow; not: none, empty, nav off, a Once route at its end, an Outdoor Hunt walk", RouteRunsRule);
        r.Add("boost nav: no route, a monster attacking: it fights (Combat, attack sent, target locked)", NoRouteFights);
        r.Add("boost nav: Outdoor Hunt walking to a monster: it fights (the hunt is no nav route)", OutdoorHuntFights);
        r.Add("boost nav: with a route: Navigating every tick, no attack sent even with a monster on top", RouteNavigatesOnly);
        r.Add("boost nav: a route loaded mid-fight: the target is dropped, the server attack cancelled, the stance back to Peace", RouteMidFightDisengages);
        r.Add("boost nav: the route unloaded: hunting resumes (target locked, attack sent)", UnloadResumesHunting);
        r.Add("boost nav: a Once route navigates only until its last point, then it fights; Circular and Linear keep running", OnceRouteEndFights);
        r.Add("boost nav: turned off on a route: hunting resumes", OffResumesHunting);
        r.Add("boost nav: the skipped fight is said in chat and the log once per session, only while a route runs", NoticeOncePerSession);
    }

    // ── Routes ────────────────────────────────────────────────────────────────

    private static NavRouteParser Route(NavRouteType type = NavRouteType.Circular, int points = 3, bool huntWalk = false)
    {
        var r = new NavRouteParser { RouteType = type, OutdoorHuntWalk = huntWalk };
        for (int i = 0; i < points; i++)
            r.Points.Add(new NavPoint { Type = NavPointType.Point, NS = 10.5 + i, EW = -3.25 - i });
        return r;
    }

    /// <summary>Loads a route the way a nav file load does: nav on, from [0].</summary>
    private static void Load(LegacyUiSettings s, NavRouteParser route)
    {
        s.CurrentRoute = route;
        s.ActiveNavIndex = 0;
        s.EnableNavigation = true;
    }

    // ── 1. The pure rule ──────────────────────────────────────────────────────

    private static BotActivity D(bool buff = false, bool combat = false, bool loot = false, bool salv = false, bool nav = false,
                                 bool engaged = false, bool boostNav = true, bool boostLoot = false, bool starved = false,
                                 bool forced = false, bool follow = false)
        => ActivityArbiter.Decide(new ArbiterInputs(true, buff, combat, loot, salv, nav,
            combatEngaged: engaged, boostNav: boostNav, boostLoot: boostLoot, followActive: follow,
            lootStarved: starved, forcedLoot: forced));

    private static void ArbiterRule()
    {
        // The arbiter's boostNav input is BoostNavRule.Active: the flag AND a route running. Every
        // combination of combat / engaged / loot / loot-starved / Boost Loot along a route: Navigating.
        for (int bits = 0; bits < 32; bits++)
        {
            bool combat = (bits & 1) != 0, engaged = (bits & 2) != 0, loot = (bits & 4) != 0,
                 starved = (bits & 8) != 0, boostLoot = (bits & 16) != 0;
            var route = D(combat: combat, engaged: engaged, loot: loot, starved: starved, boostLoot: boostLoot, nav: true);
            Check.Eq(route, BotActivity.Navigating, $"a route (combat={combat} engaged={engaged} loot={loot}): Navigating");
        }
        Check.Eq(D(combat: true, boostNav: false), BotActivity.Combat, "no route (input false): Combat, as with the flag off");
        Check.Eq(D(loot: true, boostNav: false), BotActivity.Looting, "no route: Looting, as with the flag off");
        Check.Eq(D(combat: true, engaged: true, nav: true, follow: true), BotActivity.Navigating, "a follow is navigating too");
        Check.Eq(D(buff: true, combat: true, nav: true), BotActivity.Buffing, "buffing still outranks it");
        Check.Eq(D(loot: true, forced: true, nav: true), BotActivity.Navigating, "with a route nav keeps the tick over a /ra lootthis");
        Check.Eq(D(combat: true), BotActivity.Idle, "a route loaded but nav waiting (corpse travel): Idle, no fight");
        Check.Eq(D(combat: true, nav: true, boostNav: false), BotActivity.Combat, "flag off: combat outranks nav as usual");
        Check.Eq(ActivityArbiter.ToBotAction(BotActivity.Idle), "Default", "Idle shows as Default (the dashboard reads Idle)");
    }

    private static void RouteRunsRule()
    {
        var s = new LegacyUiSettings { IsMacroRunning = true, BoostNavPriority = true };
        Check.False(BoostNavRule.Active(s), "nothing loaded");
        s.EnableNavigation = true;
        Check.False(BoostNavRule.Active(s), "nav on, no route");
        Load(s, Route(points: 0));
        Check.False(BoostNavRule.Active(s), "an empty route");

        foreach (var type in new[] { NavRouteType.Circular, NavRouteType.Linear })
        {
            Load(s, Route(type));
            Check.True(BoostNavRule.Active(s), $"{type} route: running");
            s.ActiveNavIndex = 2;
            Check.True(BoostNavRule.Active(s), $"{type} route at its last point: still running (it repeats)");
        }

        Load(s, Route(NavRouteType.Once, points: 2));
        Check.True(BoostNavRule.Active(s), "Once route at [0]: running");
        s.ActiveNavIndex = 1;
        Check.True(BoostNavRule.Active(s), "Once route at its last point: still being walked");
        s.ActiveNavIndex = 2;
        Check.False(BoostNavRule.Active(s), "Once route past its last point: finished, no route runs");
        s.EnableNavigation = false;   // what the nav engine's CompleteOnceRoute does
        Check.False(BoostNavRule.Active(s), "and with nav switched off by the engine");
        Check.True(s.RearmFinishedNavRoute(), "rig: /ra nav restart rewinds it");
        s.EnableNavigation = true;
        Check.True(BoostNavRule.Active(s), "restarted: running again");
        Load(s, Route(NavRouteType.Once, points: 2));
        Check.True(BoostNavRule.Active(s), "loaded again: running again");

        Load(s, Route(NavRouteType.Once, huntWalk: true));
        Check.False(BoostNavRule.Active(s), "the Outdoor Hunt's walk to a monster: no nav route");
        s.CurrentNavPath = "saved-walk.nav";
        Check.True(BoostNavRule.Active(s), "the same walk saved to a nav file is a loaded route");
        s.CurrentNavPath = string.Empty;

        Load(s, new NavRouteParser { RouteType = NavRouteType.Follow, FollowTargetName = "Leader" });
        Check.True(BoostNavRule.Active(s), "a Follow route counts (as in VTank)");
        Load(s, new NavRouteParser());
        s.FollowMode = true;
        s.FollowTargetId = 0x50000001;
        Check.True(BoostNavRule.Active(s), "fellowship follow counts too");
        s.FollowMode = false;
        s.FollowTargetId = 0;

        Load(s, Route());
        s.EnableNavigation = false;
        Check.False(BoostNavRule.Active(s), "a route loaded with nav off: not running");
        s.EnableNavigation = true;
        s.IsMacroRunning = false;
        Check.False(BoostNavRule.Active(s), "macro off: not running");
        s.IsMacroRunning = true;
        s.BoostNavPriority = false;
        Check.False(BoostNavRule.Active(s), "flag off: never");
        Check.True(BoostNavRule.NavRouteRuns(s), "(the route itself still runs)");
    }

    // ── A melee rig, ticked the way OnTick ticks it ───────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly CombatManager C;
        public readonly List<BotActivity> Decisions = new();

        public Rig(float mobY)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;
            FakeHost.AttackSendCalls = true;
            FakeHost.CancelAttackCalls = true;
            FakeHost.CombatModeCalls = true;
            FakeHost.CombatModeValue = CombatMode.Melee;
            Place(Player, "Me", 96, 96, creature: false);
            Place(Mob, "Drudge Skulker", 96, mobY);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.HeadingDeg = 0;
            S = new LegacyUiSettings
            {
                MonsterRange = 50, IsMacroRunning = true, EnableCombat = true, EnableLooting = true,
                EnableNavigation = true,
                MonsterRules = new List<MonsterRule> { new() { Name = "Default" } },
            };
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, new[] { Mob });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            C.ScanNearbyTargets();
        }

        /// <summary>One tick: the arbiter decides from combat's own wants and the loaded route (as
        /// OnTick does: NavWantsTick's route test, BoostNavRule.Active), writes BotAction, and combat
        /// ticks. The attack cadence runs on the real clock, so it is reset.</summary>
        public BotActivity Tick()
        {
            bool wantCombat = S.EnableCombat && C.HasEngageableTarget;
            bool wantNav = S.IsMacroRunning && S.EnableNavigation && S.CurrentRoute != null && S.CurrentRoute.Points.Count > 0;
            var inputs = new ArbiterInputs(S.IsMacroRunning, false, wantCombat, false, false, wantNav,
                combatEngaged: C.IsUnderCloseAttack, boostNav: BoostNavRule.Active(S), boostLoot: S.BoostLootPriority);
            BotActivity act = ActivityArbiter.Decide(in inputs);
            S.BotAction = ActivityArbiter.ToBotAction(act);
            LastAttackCmd.SetValue(C, DateTime.MinValue);
            C.OnHeartbeat();
            Decisions.Add(act);
            return act;
        }

        public void Ticks(int n) { for (int i = 0; i < n; i++) Tick(); }

        /// <summary>Ticks until an attack reaches the server (false if none does in 40 ticks).</summary>
        public bool TickToAttack()
        {
            int before = FakeHost.AttackCalls.Count;
            for (int i = 0; i < 40 && FakeHost.AttackCalls.Count == before; i++) Tick();
            return FakeHost.AttackCalls.Count > before;
        }

        private static readonly FieldInfo LastAttackCmd = typeof(CombatManager).GetField("lastAttackCmd", Any)!;
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }

    private static bool Logged(string part) => FakeHost.Logs.Any(l => l.Contains(part, StringComparison.Ordinal));

    // ── 2. No route: it fights ────────────────────────────────────────────────

    private static void NoRouteFights()
    {
        var rig = new Rig(mobY: 97.5f);   // 1.5 m: on top of us, the "attacking me" case
        rig.S.BoostNavPriority = true;    // NavLootPrio turned it on; no route loaded
        Check.True(rig.C.HasEngageableTarget && rig.C.IsUnderCloseAttack, "rig: a monster close enough to be attacking");
        Check.True(rig.TickToAttack(), "an attack goes out");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "the monster is locked");
        Check.True(rig.Decisions.Contains(BotActivity.Combat), "the arbiter gives Combat the tick");
        Check.False(rig.C.BoostNavStoodDown, "combat does not stand down");
        Check.False(Logged("no hunting and no fighting back"), "nothing logged about standing down");
    }

    // ── 3. The Outdoor Hunt: it fights ────────────────────────────────────────

    private static void OutdoorHuntFights()
    {
        var rig = new Rig(mobY: 99f);
        rig.S.BoostNavPriority = true;
        Load(rig.S, Route(NavRouteType.Once, points: 2, huntWalk: true));   // the hunt walking to the monster
        Check.True(rig.TickToAttack(), "an attack goes out");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "the monster is locked");
        Check.True(rig.Decisions.Contains(BotActivity.Combat), "Combat over the hunt's walk, as with the flag off");
        Check.False(rig.Decisions.Contains(BotActivity.Navigating), "never held in Navigating with a monster to fight");
        Check.False(rig.C.BoostNavStoodDown, "combat does not stand down");
    }

    // ── 4. A route: navigate only ─────────────────────────────────────────────

    private static void RouteNavigatesOnly()
    {
        var rig = new Rig(mobY: 97.5f);
        rig.S.BoostNavPriority = true;
        Load(rig.S, Route());
        rig.Ticks(60);
        Check.True(rig.Decisions.All(a => a == BotActivity.Navigating), "Navigating every tick");
        Check.Eq(rig.S.BotAction, "Navigating", "BotAction says Navigating");
        Check.Eq(FakeHost.AttackCalls.Count, 0, "no attack sent");
        Check.Eq(rig.C.activeTargetId, 0, "no target locked");
        Check.True(rig.C.BoostNavStoodDown, "combat stands down for the route");
        Check.True(Logged("Boost Nav Priority is on and a nav route runs - no hunting and no fighting back"), "and the log says why");
        Check.True(rig.C.HasEngageableTarget, "the scan still runs (it is what the notice reads)");
    }

    // ── 5. A route loaded mid-fight ───────────────────────────────────────────

    private static void RouteMidFightDisengages()
    {
        var rig = new Rig(mobY: 99f);   // 3 m north
        rig.S.BoostNavPriority = true;  // on, but no route: it fights
        Check.True(rig.TickToAttack(), "no route: the fight starts (an attack goes out)");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "the Drudge is the target");
        Check.Eq(rig.Decisions[^1], BotActivity.Combat, "in Combat");
        int attacks = FakeHost.AttackCalls.Count;
        int cancels = FakeHost.CancelAttacks;
        FakeHost.CombatModeRequests.Clear();

        Load(rig.S, Route());   // a nav file loaded (or a meta's route)
        Check.Eq(rig.Tick(), BotActivity.Navigating, "the next tick: Navigating, not Combat");
        Check.Eq(rig.C.activeTargetId, 0, "the target is dropped");
        Check.True(FakeHost.CancelAttacks > cancels, "the server's attack is cancelled (no more swings, no walk to the mob)");
        Check.True(Logged("DropTarget 0x80000451: Boost Nav Priority is on and a nav route runs"), "the drop is logged with the reason");
        Check.True(FakeHost.CombatModeRequests.Contains(CombatMode.NonCombat), "Peace Mode When Idle: back to Peace, no stance left up");

        FakeHost.CombatModeValue = CombatMode.NonCombat;   // the server lands the swap
        int modeAsks = FakeHost.CombatModeRequests.Count;
        rig.Ticks(60);
        Check.Eq(FakeHost.AttackCalls.Count, attacks, "no attack while the route runs");
        Check.Eq(rig.C.activeTargetId, 0, "nothing re-locked");
        Check.Eq(FakeHost.CombatModeRequests.Count, modeAsks, "in Peace: no more mode changes");
        Check.False(rig.Decisions.Skip(rig.Decisions.Count - 60).Any(a => a == BotActivity.Combat), "never Combat again while it runs");
    }

    // ── 6. The route unloaded ─────────────────────────────────────────────────

    private static void UnloadResumesHunting()
    {
        var rig = new Rig(mobY: 99f);
        rig.S.BoostNavPriority = true;
        Load(rig.S, Route());
        rig.Ticks(20);
        Check.Eq(FakeHost.AttackCalls.Count, 0, "route: nothing attacked");

        rig.S.CurrentRoute = new NavRouteParser();   // unloaded; the flag stays on
        Check.True(rig.TickToAttack(), "no route: an attack goes out again");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "the Drudge is locked");
        Check.True(rig.Decisions.Contains(BotActivity.Combat), "the arbiter gives Combat the tick again");
        Check.False(rig.C.BoostNavStoodDown, "combat no longer stands down");
        Check.True(Logged("Boost Nav Priority is on but no nav route runs - hunting again"), "logged once the route is gone");
    }

    // ── 7. A Once route at its end ────────────────────────────────────────────

    private static void OnceRouteEndFights()
    {
        var rig = new Rig(mobY: 99f);
        rig.S.BoostNavPriority = true;
        Load(rig.S, Route(NavRouteType.Once, points: 2));
        rig.Ticks(10);
        rig.S.ActiveNavIndex = 1;   // walking to the last point
        rig.Ticks(10);
        Check.True(rig.Decisions.All(a => a == BotActivity.Navigating), "while the Once route is walked: Navigating only");
        Check.Eq(FakeHost.AttackCalls.Count, 0, "no attack on the way");

        // The last point reached: the nav engine's CompleteOnceRoute leaves the index past the end
        // and switches nav off.
        rig.S.ActiveNavIndex = 2;
        rig.S.EnableNavigation = false;
        Check.True(rig.TickToAttack(), "at the end: an attack goes out (it hunts)");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "the Drudge is locked");
        Check.False(rig.C.BoostNavStoodDown, "combat no longer stands down");

        // A meta turning nav back on over the finished route: still finished, still hunting.
        rig.S.EnableNavigation = true;
        rig.Ticks(5);
        Check.Eq(rig.Decisions[^1], BotActivity.Combat, "nav on over a finished Once route: still Combat");

        foreach (var type in new[] { NavRouteType.Circular, NavRouteType.Linear })
        {
            var r2 = new Rig(mobY: 99f);
            r2.S.BoostNavPriority = true;
            Load(r2.S, Route(type, points: 2));
            r2.S.ActiveNavIndex = 1;   // at the last point: it goes round (or back) again
            r2.Ticks(30);
            Check.True(r2.Decisions.All(a => a == BotActivity.Navigating), $"{type} route at its last point: still Navigating only");
            Check.Eq(FakeHost.AttackCalls.Count, 0, $"{type}: no attack");
        }
    }

    // ── 8. The flag off on a route ────────────────────────────────────────────

    private static void OffResumesHunting()
    {
        var rig = new Rig(mobY: 99f);
        rig.S.BoostNavPriority = true;
        Load(rig.S, Route());
        rig.Ticks(20);
        Check.Eq(FakeHost.AttackCalls.Count, 0, "on: nothing attacked");

        rig.S.BoostNavPriority = false;
        Check.True(rig.TickToAttack(), "off: an attack goes out again");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "the Drudge is locked");
        Check.True(rig.Decisions.Contains(BotActivity.Combat), "the arbiter gives Combat the tick again");
        Check.False(rig.C.BoostNavStoodDown, "combat no longer stands down");
        Check.True(Logged("Boost Nav Priority is off - hunting again"), "logged once it is off");
        // Looting along and off a route: LootClaimMismatchTests' Boost Nav tests.
    }

    // ── 9. The notice ─────────────────────────────────────────────────────────

    private static RynthAiPlugin Plugin()
    {
        var p = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
        return p;
    }

    private static void Note(RynthAiPlugin p, LegacyUiSettings s, bool wantCombat)
        => typeof(RynthAiPlugin).GetMethod("NoteBoostNavSkip", Any)!.Invoke(p, new object[] { s, wantCombat });

    private static int NoticeCount() => FakeHost.Chat.Count(c => c == RynthAiPlugin.BoostNavNotice);

    private static void NoticeOncePerSession()
    {
        FakeHost.Reset();
        FakeHost.Create(Player);
        var s = new LegacyUiSettings { IsMacroRunning = true, EnableCombat = true, EnableLooting = false };
        var p = Plugin();

        Note(p, s, wantCombat: true);
        Check.Eq(NoticeCount(), 0, "flag off: nothing said");

        s.BoostNavPriority = true;
        s.EnableNavigation = true;
        for (int i = 0; i < 20; i++) Note(p, s, wantCombat: true);
        Check.Eq(NoticeCount(), 0, "flag on, no route: it fights, nothing is skipped, nothing said");

        Load(s, Route(NavRouteType.Once, huntWalk: true));
        Note(p, s, wantCombat: true);
        Check.Eq(NoticeCount(), 0, "the Outdoor Hunt: nothing said");

        Load(s, Route());
        Note(p, s, wantCombat: false);
        Check.Eq(NoticeCount(), 0, "a route, nothing skipped (no monster, looting off): nothing said");

        s.IsMacroRunning = false;
        Note(p, s, wantCombat: true);
        Check.Eq(NoticeCount(), 0, "macro off: nothing said");
        s.IsMacroRunning = true;

        Note(p, s, wantCombat: true);
        Check.Eq(NoticeCount(), 1, "the first fight skipped along a route: said in chat");
        Check.Eq(RynthAiPlugin.BoostNavNotice,
            "[RynthAi] Boost Nav Priority is on: while a nav route runs, RynthAi only follows it (no hunting, no looting). Turn it off to hunt along the route.",
            "the wording");
        Check.True(Logged(RynthAiPlugin.BoostNavNotice + " (skipped a fight)"), "and in the log, with what was skipped");
        Check.Eq(FakeHost.ChatTypes[FakeHost.Chat.IndexOf(RynthAiPlugin.BoostNavNotice)], 2, "in the warning colour");

        for (int i = 0; i < 50; i++) Note(p, s, wantCombat: true);
        s.BoostNavPriority = false; Note(p, s, true);
        s.BoostNavPriority = true; Note(p, s, true);
        Check.Eq(NoticeCount(), 1, "once per session: not again, not even after the flag goes off and on");

        var next = Plugin();   // a new session (a login or a reload starts a new plugin instance)
        s.CurrentRoute = new NavRouteParser();
        Note(next, s, wantCombat: true);
        Check.Eq(NoticeCount(), 1, "a new session with no route: nothing said");
        Load(s, Route());
        Note(next, s, wantCombat: true);
        Check.Eq(NoticeCount(), 2, "a new session along a route says it again, once");
    }
}

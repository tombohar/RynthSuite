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

// Boost Nav Priority, owner's rule (2026-11-02): with the flag on RynthAi ONLY navigates. No hunting,
// no looting, and no fighting back even with a monster attacking it; with a route (or a follow) it
// navigates, with nothing to navigate it stands idle. Buffing and vitals still run, buffing on top.
// R-20261102-041824-3f7c: the flag came on through NavLootPrio (or a hunting meta), there was no
// route, and the arbiter's WantNav-guarded clause let combat win, so the bot hunted as usual and the
// player never noticed the flag; they only saw that it would not loot. The first fight or corpse
// the flag skips now says so in chat, once per session.
internal static class BoostNavTests
{
    private const uint Player = 0x50000F51;
    private const uint Lb = 0xA9B40000;
    private const uint Mob = 0x80000451;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("boost nav (owner, 2026-11-02): the arbiter never picks Combat or Looting with the flag on, route or not; buffing stays on top", ArbiterRule);
        r.Add("boost nav: no route, a monster attacking: never Combat or Looting, no attack sent, no target locked", NoRouteNeverFights);
        r.Add("boost nav: with a route: Navigating every tick, no attack sent even with a monster on top", RouteNavigatesOnly);
        r.Add("boost nav: turned on mid-fight: the target is dropped, the server attack cancelled, the stance back to Peace", MidFightDisengages);
        r.Add("boost nav: turned off again: hunting resumes (target locked, attack sent)", OffResumesHunting);
        r.Add("boost nav: the skipped fight is said in chat and the log once per session, never with the flag off", NoticeOncePerSession);
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
        // Every combination of combat / engaged / loot / loot-starved / Boost Loot, no route: Idle.
        for (int bits = 0; bits < 32; bits++)
        {
            bool combat = (bits & 1) != 0, engaged = (bits & 2) != 0, loot = (bits & 4) != 0,
                 starved = (bits & 8) != 0, boostLoot = (bits & 16) != 0;
            var noRoute = D(combat: combat, engaged: engaged, loot: loot, starved: starved, boostLoot: boostLoot);
            Check.Eq(noRoute, BotActivity.Idle, $"no route (combat={combat} engaged={engaged} loot={loot} starved={starved} boostLoot={boostLoot}): Idle");
            var route = D(combat: combat, engaged: engaged, loot: loot, starved: starved, boostLoot: boostLoot, nav: true);
            Check.Eq(route, BotActivity.Navigating, $"a route (combat={combat} engaged={engaged} loot={loot}): Navigating");
        }
        Check.Eq(D(combat: true, engaged: true, nav: true, follow: true), BotActivity.Navigating, "a follow is navigating too");
        Check.Eq(D(buff: true, combat: true, nav: true), BotActivity.Buffing, "buffing still outranks it");
        Check.Eq(D(buff: true, combat: true), BotActivity.Buffing, "buffing with no route too");
        Check.Eq(D(loot: true, forced: true), BotActivity.Looting, "a /ra lootthis container is still looted (no route)");
        Check.Eq(D(loot: true, forced: true, nav: true), BotActivity.Navigating, "with a route nav keeps the tick, as before");
        Check.Eq(D(salv: true, combat: true), BotActivity.Salvaging, "the salvage queue (an inventory chore) still runs");
        Check.Eq(D(combat: true, nav: true, boostNav: false), BotActivity.Combat, "flag off: combat outranks nav as usual");
        Check.Eq(D(loot: true, boostNav: false), BotActivity.Looting, "flag off: looting as usual");
        Check.Eq(ActivityArbiter.ToBotAction(BotActivity.Idle), "Default", "Idle shows as Default (the dashboard reads Idle)");
    }

    // ── A melee rig, ticked the way OnTick ticks it ───────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S;
        public readonly CombatManager C;
        public bool Route;
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
                MonsterRules = new List<MonsterRule> { new() { Name = "Default" } },
            };
            var host = FakeHost.Create(Player);
            var cache = FakeHost.MakeCache(host, Player, new[] { Mob });
            C = new CombatManager(host, S, cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            C.ScanNearbyTargets();
        }

        /// <summary>One tick: the arbiter decides from combat's own wants (as OnTick does), writes
        /// BotAction, and combat ticks. The attack cadence runs on the real clock, so it is reset.</summary>
        public BotActivity Tick()
        {
            bool wantCombat = S.EnableCombat && C.HasEngageableTarget;
            var inputs = new ArbiterInputs(S.IsMacroRunning, false, wantCombat, false, false, Route,
                combatEngaged: C.IsUnderCloseAttack, boostNav: S.BoostNavPriority, boostLoot: S.BoostLootPriority);
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

    // ── 2. No route ───────────────────────────────────────────────────────────

    private static void NoRouteNeverFights()
    {
        var rig = new Rig(mobY: 97.5f);   // 1.5 m: on top of us, the "attacking me" case
        rig.S.BoostNavPriority = true;
        Check.True(rig.C.HasEngageableTarget && rig.C.IsUnderCloseAttack, "rig: a monster close enough to be attacking");
        rig.Ticks(60);
        Check.False(rig.Decisions.Any(a => a is BotActivity.Combat or BotActivity.Looting), "never Combat or Looting");
        Check.True(rig.Decisions.All(a => a == BotActivity.Idle), "Idle the whole time (nothing to navigate)");
        Check.Eq(FakeHost.AttackCalls.Count, 0, "no attack sent");
        Check.Eq(rig.C.activeTargetId, 0, "no target locked");
        Check.True(rig.C.BoostNavStoodDown, "combat stands down for the flag");
        Check.True(Logged("Boost Nav Priority is on - no hunting and no fighting back"), "and the log says why");
        Check.True(rig.C.HasEngageableTarget, "the scan still runs (it is what the notice reads)");
    }

    // ── 3. A route ────────────────────────────────────────────────────────────

    private static void RouteNavigatesOnly()
    {
        var rig = new Rig(mobY: 97.5f);
        rig.S.BoostNavPriority = true;
        rig.Route = true;
        rig.Ticks(60);
        Check.True(rig.Decisions.All(a => a == BotActivity.Navigating), "Navigating every tick");
        Check.Eq(rig.S.BotAction, "Navigating", "BotAction says Navigating");
        Check.Eq(FakeHost.AttackCalls.Count, 0, "no attack sent");
        Check.Eq(rig.C.activeTargetId, 0, "no target locked");
    }

    // ── 4. Mid-fight ──────────────────────────────────────────────────────────

    private static void MidFightDisengages()
    {
        var rig = new Rig(mobY: 99f);   // 3 m north
        Check.True(rig.TickToAttack(), "flag off: the fight starts (an attack goes out)");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "the Drudge is the target");
        Check.Eq(rig.Decisions[^1], BotActivity.Combat, "in Combat");
        int attacks = FakeHost.AttackCalls.Count;
        int cancels = FakeHost.CancelAttacks;
        FakeHost.CombatModeRequests.Clear();

        rig.S.BoostNavPriority = true;   // NavLootPrio, or a meta's SetOpt navpriorityboost
        Check.Eq(rig.Tick(), BotActivity.Idle, "the next tick: Idle, not Combat");
        Check.Eq(rig.C.activeTargetId, 0, "the target is dropped");
        Check.True(FakeHost.CancelAttacks > cancels, "the server's attack is cancelled (no more swings, no walk to the mob)");
        Check.True(Logged("DropTarget 0x80000451: Boost Nav Priority is on"), "the drop is logged with the reason");
        Check.True(FakeHost.CombatModeRequests.Contains(CombatMode.NonCombat), "Peace Mode When Idle: back to Peace, no stance left up");

        FakeHost.CombatModeValue = CombatMode.NonCombat;   // the server lands the swap
        int modeAsks = FakeHost.CombatModeRequests.Count;
        rig.Ticks(60);
        Check.Eq(FakeHost.AttackCalls.Count, attacks, "no attack after the flag came on");
        Check.Eq(rig.C.activeTargetId, 0, "nothing re-locked");
        Check.Eq(FakeHost.CombatModeRequests.Count, modeAsks, "in Peace: no more mode changes");
        Check.False(rig.Decisions.Skip(rig.Decisions.Count - 60).Any(a => a == BotActivity.Combat), "never Combat again while it is on");
    }

    // ── 5. Off again ──────────────────────────────────────────────────────────

    private static void OffResumesHunting()
    {
        var rig = new Rig(mobY: 99f);
        rig.S.BoostNavPriority = true;
        rig.Ticks(20);
        Check.Eq(FakeHost.AttackCalls.Count, 0, "on: nothing attacked");

        rig.S.BoostNavPriority = false;
        Check.True(rig.TickToAttack(), "off: an attack goes out again");
        Check.Eq((uint)rig.C.activeTargetId, Mob, "the Drudge is locked");
        Check.True(rig.Decisions.Contains(BotActivity.Combat), "the arbiter gives Combat the tick again");
        Check.False(rig.C.BoostNavStoodDown, "combat no longer stands down");
        Check.True(Logged("Boost Nav Priority is off - hunting again"), "logged once it is off");
        // Looting coming back once the flag is off: LootClaimMismatchTests' Boost Nav test.
    }

    // ── 6. The notice ─────────────────────────────────────────────────────────

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
        Note(p, s, wantCombat: false);
        Check.Eq(NoticeCount(), 0, "flag on, nothing skipped (no monster, looting off): nothing said");

        s.IsMacroRunning = false;
        Note(p, s, wantCombat: true);
        Check.Eq(NoticeCount(), 0, "macro off: nothing said");
        s.IsMacroRunning = true;

        Note(p, s, wantCombat: true);
        Check.Eq(NoticeCount(), 1, "the first skipped fight: said in chat");
        Check.Eq(RynthAiPlugin.BoostNavNotice,
            "[RynthAi] Boost Nav Priority is on: RynthAi only navigates (no hunting, no looting). Turn it off to hunt and loot.",
            "the owner's wording");
        Check.True(Logged(RynthAiPlugin.BoostNavNotice + " (skipped a fight)"), "and in the log, with what was skipped");
        Check.Eq(FakeHost.ChatTypes[FakeHost.Chat.IndexOf(RynthAiPlugin.BoostNavNotice)], 2, "in the warning colour");

        for (int i = 0; i < 50; i++) Note(p, s, wantCombat: true);
        s.BoostNavPriority = false; Note(p, s, true);
        s.BoostNavPriority = true; Note(p, s, true);
        Check.Eq(NoticeCount(), 1, "once per session: not again, not even after the flag goes off and on");

        var next = Plugin();   // a new session (a login or a reload starts a new plugin instance)
        Note(next, s, wantCombat: true);
        Check.Eq(NoticeCount(), 2, "a new session says it again, once");
    }
}

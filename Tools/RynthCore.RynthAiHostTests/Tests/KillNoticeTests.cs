using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-11-01: looting mid-fight, and lesser monsters fought while a top-priority one stood by.
// A kill notice carries only the dead monster's name. In a pack of same-named monsters two things
// matched it to the wrong one:
//  - in Melee mode a dual-wield or multi-strike weapon's second hit (about 0.3 s after the first)
//    was handed to the nearest other monster of that name by the spell "volley" rule, so a
//    neighbour we never touched counted as hit by us;
//  - some kills brought two notices 1-2 ms apart (one corpse); the second was matched to that
//    neighbour.
// The neighbour was kept out of the scan for 4 s while alive: with no other monster scanned the
// arbiter gave Looting the tick (a corpse opened with the monster 3 m away), and in a mixed pack
// the top tier was empty, so a lower-priority monster was locked.
internal static class KillNoticeTests
{
    private const uint Player = 0x50000F01;
    private const uint Lb = 0xA9B40000;
    private const uint SentryWcid = 2001, LesserWcid = 2002;
    private const string Sentry = "Drumtaken Sentry";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("kill notice (melee): a dual-wield second hit stays on the fight target, not a same-named neighbour", MeleeSecondHitStaysOnTarget);
        r.Add("kill notice (melee): the fight target's notice drops it even with stale server health", MeleeKillDropsTargetNotNeighbour);
        r.Add("kill notice: a second notice for the same death is an echo - no one hidden, one kill", EchoHidesNoOne);
        r.Add("kill notice: a death matched by name holds combat (no looting) until confirmed or 2 s", GuessedDeathHoldsCombat);
        r.Add("kill notice: the fight is over when the last monster dies - looting gets the tick", FightOverLoots);
        r.Add("targeting: a top-priority monster's tier holds while its death is only a guess", GuessedDeathHoldsTier);
        r.Add("targeting (melee): after a kill and its echo the same-named top priority is next, not a lesser one", EchoKeepsTopPriorityNext);
    }

    // ── The rig ──────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly LegacyUiSettings S = new() { MonsterRange = 10, TargetSelection = 1, TargetLock = true, EnableCombat = true };
        public readonly MonsterDamageStore Store = new();
        public CombatManager C = null!;
        public WorldObjectCache Cache = null!;
        public DateTime Now = RynthClock.Now;
        private readonly List<uint> _ids = new();
        private uint _next = 0x80000501;

        public Rig(int mode)
        {
            FakeHost.Reset();
            FakeHost.WeaponCalls = true;   // installs GetCurrentCombatMode
            FakeHost.CombatModeValue = mode;
            FakeHost.Names[Player] = "Me";
            FakeHost.Positions[Player] = (Lb | 0x1C, 96f, 96f, 0f);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
        }

        public uint Mob(string name, uint wcid, float north, float east = 0)
        {
            uint id = _next++;
            FakeHost.Names[id] = name;
            FakeHost.Positions[id] = (Lb | 0x1C, 96f + east, 96f + north, 0f);
            FakeHost.ItemTypes[id] = 0x10;
            FakeHost.Wcids[id] = wcid;
            _ids.Add(id);
            return id;
        }

        public void Build(uint fightTarget = 0)
        {
            var host = FakeHost.Create();
            Cache = FakeHost.MakeCache(host, Player, _ids);
            C = new CombatManager(host, S, Cache);
            C.SetPlayerId(Player);
            C.SetDamageStores(null, Store);
            C.TargetClock = () => Now;
            if (fightTarget != 0) C.EngageForTest(unchecked((int)fightTarget));
        }

        public void Rescan()
        {
            typeof(CombatManager).GetField("_lastScanTime", Any)!.SetValue(C, DateTime.MinValue);
            C.ScanNearbyTargets();
        }

        public bool Scanned(uint id) => C.ScannedTargets.Any(t => t.Id == unchecked((int)id));
        public bool Suppressed(uint id) => C.IsKillSuppressed(unchecked((int)id));
        public int Kills(uint wcid) => Store.Snapshot().Where(r => r.Wcid == wcid).Sum(r => r.KillSamples);

        /// <summary>The arbiter's pick with this combat state, a lootable corpse and a route.</summary>
        public BotActivity Decide()
        {
            var inputs = new ArbiterInputs(true, false, S.EnableCombat && C.HasEngageableTarget, true, false, true,
                                           combatEngaged: C.IsUnderCloseAttack);
            return ActivityArbiter.Decide(in inputs);
        }
    }

    private static int Id(uint id) => unchecked((int)id);

    // ── Melee: one swing, one monster ────────────────────────────────────────

    private static void MeleeSecondHitStaysOnTarget()
    {
        var rig = new Rig(CombatMode.Melee);
        uint target = rig.Mob(Sentry, SentryWcid, 2);
        uint twin = rig.Mob(Sentry, SentryWcid, 3);
        rig.Build(target);
        rig.Store.SetManualHp(SentryWcid, 200);

        // Main hand, then off hand 0.3 s later (inside the 0.8 s volley window).
        rig.C.HandleChatForDamage("You blister Drumtaken Sentry for 10 points of acid damage!");
        rig.C.HandleChatForDamage("You numb Drumtaken Sentry for 12 points of cold damage!");

        Check.False(rig.Cache.IsHealthEstimated(Id(twin)), "the neighbour gets no damage estimate (it was never hit)");
        Check.Near(rig.Cache.GetHealthRatio(Id(twin)), -1, 1e-6, "its health stays unknown");

        // The same two lines in Magic mode still go to two monsters (a ring or chain: one line each).
        var magic = new Rig(CombatMode.Magic);
        uint mt = magic.Mob(Sentry, SentryWcid, 2);
        uint mtwin = magic.Mob(Sentry, SentryWcid, 3);
        magic.Build(mt);
        magic.Store.SetManualHp(SentryWcid, 200);
        magic.C.HandleChatForDamage("You blast Drumtaken Sentry for 100 points with Flame Ring VI.");
        magic.C.HandleChatForDamage("You blast Drumtaken Sentry for 50 points with Flame Ring VI.");
        Check.True(magic.Cache.IsHealthEstimated(Id(mtwin)), "magic: the second line of a volley is the neighbour's");
    }

    private static void MeleeKillDropsTargetNotNeighbour()
    {
        var rig = new Rig(CombatMode.Melee);
        uint target = rig.Mob(Sentry, SentryWcid, 2);
        uint twin = rig.Mob(Sentry, SentryWcid, 3);
        rig.Build(target);
        rig.Cache.OnUpdateHealth(target, 0.38f);   // the last health the server sent; no 0 came with the killing blow
        rig.C.OnCombatDamage(40, 1, false, true);
        rig.C.HandleChatForDamage("You blister Drumtaken Sentry for 40 points of acid damage!");
        rig.C.HandleChatForDamage("You numb Drumtaken Sentry for 30 points of cold damage!");

        rig.C.OnKillNotification("You knock Drumtaken Sentry into next Morningthaw!");

        Check.Eq(rig.C.activeTargetId, 0, "the fight target is the one that died: dropped");
        Check.True(rig.Suppressed(target), "and kept off the scan while its corpse forms");
        Check.False(rig.Suppressed(twin), "the neighbour (never hit) is not taken for it");
        Check.False(FakeHost.Logs.Any(l => l.Contains("keeping the target")), "no 'a neighbour we hit died' guess");
        rig.Rescan();
        Check.True(rig.Scanned(twin), "the neighbour is still a candidate");
    }

    // ── Two notices, one death ───────────────────────────────────────────────

    private static void EchoHidesNoOne()
    {
        var rig = new Rig(CombatMode.Melee);
        uint target = rig.Mob(Sentry, SentryWcid, 2);
        uint twin = rig.Mob(Sentry, SentryWcid, 3);
        rig.Build(target);
        rig.C.OnCombatDamage(40, 1, false, true);   // one landed hit: the kill is credited to this fight
        rig.C.HandleChatForDamage("You blister Drumtaken Sentry for 40 points of acid damage!");

        rig.C.OnKillNotification("You killed Drumtaken Sentry!");
        rig.C.OnKillNotification("Drumtaken Sentry suffers a frozen fate!");   // the same death, 1-2 ms later

        Check.False(rig.Suppressed(twin), "the echo hides no one");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[KillDbg] how=echo")), "it is logged as an echo");
        Check.Eq(rig.Kills(SentryWcid), 1, "one death, one kill counted");

        rig.Rescan();
        Check.True(rig.Scanned(twin), "the live neighbour is scanned");
        Check.True(rig.C.HasEngageableTarget, "combat wants the tick");
        Check.Eq(rig.Decide(), BotActivity.Combat, "the arbiter keeps Combat: no looting with the neighbour 3 m away");

        // Later than the echo window, a notice is a death of its own.
        rig.Now = rig.Now.AddMilliseconds(KillNoticeWindow + 50);
        rig.C.EngageForTest(Id(twin));
        rig.C.OnCombatDamage(40, 1, false, true);
        rig.C.HandleChatForDamage("You blister Drumtaken Sentry for 40 points of acid damage!");
        System.Threading.Thread.Sleep((int)KillNoticeWindow + 50);   // the echo window runs on RynthClock
        rig.C.OnKillNotification("Drumtaken Sentry died!");
        Check.Eq(rig.C.activeTargetId, 0, "the neighbour's own death later drops it");
        Check.Eq(rig.Kills(SentryWcid), 2, "and counts");
    }

    private static double KillNoticeWindow => CombatManager.KillEchoMs;

    // ── A death matched by name only ─────────────────────────────────────────

    /// <summary>Magic: a ring hits the target and its twin; the target dies (health 0), then a
    /// notice of that name comes that may be the twin's.</summary>
    private static Rig RingKillThenGuess(out uint target, out uint twin)
    {
        var rig = new Rig(CombatMode.Magic);
        target = rig.Mob(Sentry, SentryWcid, 2);
        twin = rig.Mob(Sentry, SentryWcid, 3);
        rig.Build(target);
        rig.C.HandleChatForDamage("You blast Drumtaken Sentry for 100 points with Flame Ring VI.");
        rig.C.HandleChatForDamage("You blast Drumtaken Sentry for 100 points with Flame Ring VI.");
        rig.Cache.OnUpdateHealth(target, 0f);
        rig.C.OnKillNotification("Drumtaken Sentry died!");
        rig.C.OnKillNotification("Drumtaken Sentry's death is preceded by a sharp, stabbing pain!");
        return rig;
    }

    private static void GuessedDeathHoldsCombat()
    {
        var rig = RingKillThenGuess(out _, out uint twin);
        Check.True(rig.Suppressed(twin), "the twin was hit by the ring: the notice is taken for it");
        rig.Rescan();
        Check.False(rig.Scanned(twin), "it is kept off the scan for now");
        Check.True(rig.C.HasTargets, "but still counts as a monster in range");
        Check.True(rig.C.HasCloseThreat(RynthAiPlugin.LootStarveCloseYards), "and as a close threat (no starved loot turn)");
        Check.Eq(rig.Decide(), BotActivity.Combat, "so the arbiter keeps Combat, not Looting");

        // No word of its death in 2 s: it is alive, a target again.
        rig.Now = rig.Now.AddMilliseconds(2100);
        rig.Rescan();
        Check.True(rig.Scanned(twin), "after 2 s it is scanned again");
        Check.True(FakeHost.Logs.Any(l => l.Contains("matched by name") && l.Contains("still alive: it is a target again")),
            "and the log says the guess was wrong");

        // A hit on it ends the hold at once.
        rig = RingKillThenGuess(out _, out twin);
        rig.C.HandleChatForDamage("You blast Drumtaken Sentry for 80 points with Flame Ring VI.");
        Check.False(rig.Suppressed(twin), "a damage line on it says it is alive");
    }

    private static void FightOverLoots()
    {
        // The guessed twin really died: its corpse (health 0) ends the hold, and the fight is over.
        var rig = RingKillThenGuess(out _, out uint twin);
        rig.Cache.OnUpdateHealth(twin, 0f);
        rig.Rescan();
        Check.False(rig.C.HasTargets, "both dead: nothing in range");
        Check.False(rig.C.HasEngageableTarget, "combat lets the tick go");
        Check.Eq(rig.Decide(), BotActivity.Looting, "looting gets the tick");

        // A lone kill: looting right after it.
        var lone = new Rig(CombatMode.Melee);
        uint mob = lone.Mob(Sentry, SentryWcid, 2);
        lone.Build(mob);
        lone.C.OnCombatDamage(40, 1, false, true);
        lone.C.OnKillNotification("You killed Drumtaken Sentry!");
        lone.C.OnKillNotification("Drumtaken Sentry suffers a frozen fate!");
        lone.Rescan();
        Check.False(lone.C.HasEngageableTarget, "the last monster died: no combat");
        Check.Eq(lone.Decide(), BotActivity.Looting, "the arbiter loots");
    }

    // ── Priority ─────────────────────────────────────────────────────────────

    private static void UseRules(Rig rig)
        => rig.S.MonsterRules = new List<MonsterRule>
        {
            new MonsterRule { Name = "Default", Priority = 1 },
            new MonsterRule { Name = Sentry, Priority = 5 },
        };

    private static void GuessedDeathHoldsTier()
    {
        var rig = new Rig(CombatMode.Magic);
        UseRules(rig);
        uint target = rig.Mob(Sentry, SentryWcid, 2);
        uint twin = rig.Mob(Sentry, SentryWcid, 4);
        uint lesser = rig.Mob("Drumtaken Lesser Siraluun", LesserWcid, 3);
        rig.Build(target);
        rig.C.HandleChatForDamage("You blast Drumtaken Sentry for 100 points with Flame Ring VI.");
        rig.C.HandleChatForDamage("You blast Drumtaken Sentry for 100 points with Flame Ring VI.");
        rig.Cache.OnUpdateHealth(target, 0f);
        rig.C.OnKillNotification("Drumtaken Sentry died!");
        rig.C.OnKillNotification("Drumtaken Sentry's death is preceded by a sharp, stabbing pain!");

        rig.Rescan();
        Check.True(rig.Scanned(lesser) && !rig.Scanned(twin), "the scan has only the Priority 1 monster");
        rig.C.HandleCombatTrigger();
        Check.Eq(rig.C.activeTargetId, 0, "the Priority 1 monster isn't locked while the Priority 5 twin may be alive");

        rig.Now = rig.Now.AddMilliseconds(2100);
        rig.Rescan();
        rig.C.HandleCombatTrigger();
        Check.Eq(rig.C.activeTargetId, Id(twin), "the twin was alive: it is the target, not the lesser one");
    }

    private static void EchoKeepsTopPriorityNext()
    {
        var rig = new Rig(CombatMode.Melee);
        UseRules(rig);
        uint target = rig.Mob(Sentry, SentryWcid, 2);
        uint twin = rig.Mob(Sentry, SentryWcid, 4);
        uint lesser = rig.Mob("Drumtaken Lesser Siraluun", LesserWcid, 3);
        rig.Build(target);
        rig.C.OnCombatDamage(40, 1, false, true);
        rig.C.HandleChatForDamage("You blister Drumtaken Sentry for 40 points of acid damage!");
        rig.C.HandleChatForDamage("You numb Drumtaken Sentry for 30 points of cold damage!");
        rig.C.OnKillNotification("You killed Drumtaken Sentry!");
        rig.C.OnKillNotification("Drumtaken Sentry suffers a frozen fate!");

        rig.Rescan();
        rig.C.HandleCombatTrigger();
        Check.Eq(rig.C.activeTargetId, Id(twin), "the Priority 5 twin is next (it was 4 s out of the scan before)");
        Check.True(rig.Scanned(lesser), "the lesser one was a candidate too");
    }
}

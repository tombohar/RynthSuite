using System;
using System.Linq;
using System.Threading;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Choppy melee, Drakkon 2026-10-12 (Olthoi Slayers/Slashers, landblock 0x6146, Tetsubo):
//  * the server's move-to the target stalled 2 m short and the bot waited 6 s for the watchdog's
//    CancelAttack, which fixed it at once every time (flight recorder 20:47:35 and 20:48:20);
//  * four ghosts (objects the client drew, the server no longer had for him) were attacked round
//    and round for 25+ minutes: every attack answered by AttackDone ActionCancelled, they never
//    moved, the server sent nothing about them, each got a 30 s blacklist and came back;
//  * after a kill or a rebind the next swing waited out the 1000 ms re-attack interval.
internal static class MeleeStallTests
{
    public static void Register(Runner r)
    {
        r.Add("melee stall: step 1 after 2.5 s when no swing plays and the character stands still, else 6 s", EarlyStepWhenQuiet);
        r.Add("ghost: no swing after the re-issue, not moved, the server silent 8 s+ = a ghost", GhostVerdict);
        r.Add("ghost: a swing, a move, a server message or another target means it is alive", GhostAlive);
        r.Add("ghost: skipped until the server updates or creates it, it moves, or the landblock changes", GhostTrackerClears);
        r.Add("ghost: combat skips a marked ghost, counts it in the lock line, and takes it back when the server updates it", GhostSkippedByCombat);
        r.Add("melee interval: the first swing at a new target doesn't wait out the old 1000 ms", PhysicalNewTargetInterval);
    }

    private static readonly DateTime T0 = new(2026, 10, 12, 20, 47, 35);

    private static void EarlyStepWhenQuiet()
    {
        // Quiet (the caller's read: attacks sent, nothing animating, standing still).
        var w = new CombatStallWatchdog();
        DateTime evidence = T0;                       // the last hit
        DateTime t = T0;
        CombatStallWatchdog.StallAction act = CombatStallWatchdog.StallAction.None;
        double stalled = 0;
        for (int ms = 0; ms <= 3000; ms += 100)
        {
            t = T0.AddMilliseconds(ms);
            act = w.Tick(t, eligible: true, evidence, out stalled, quiet: ms >= 1500);
            if (act != CombatStallWatchdog.StallAction.None) break;
        }
        Check.Eq(act, CombatStallWatchdog.StallAction.Reissue, "quiet: step 1 (re-issue)");
        Check.True(stalled >= CombatStallWatchdog.QuietStallMs && stalled < CombatStallWatchdog.StallMs,
            $"fired at {stalled:0} ms, between 2.5 s and 6 s");
        Check.True(w.LastFireWasEarly, "flagged early for the log");

        // A swing comes back: recovered, as before.
        act = w.Tick(t.AddMilliseconds(400), true, t.AddMilliseconds(350), out _, quiet: false);
        Check.Eq(act, CombatStallWatchdog.StallAction.Recovered, "a hit after the step: recovered");

        // Not quiet (a swing animating, or the server walking us to the mob): the old 6 s.
        var w2 = new CombatStallWatchdog();
        act = CombatStallWatchdog.StallAction.None;
        int firedAt = -1;
        for (int ms = 0; ms <= 7000; ms += 100)
        {
            act = w2.Tick(T0.AddMilliseconds(ms), true, T0, out _, quiet: false);
            if (act != CombatStallWatchdog.StallAction.None) { firedAt = ms; break; }
        }
        Check.Eq(act, CombatStallWatchdog.StallAction.Reissue, "not quiet: step 1 still comes");
        Check.True(firedAt >= 6000, $"but only after 6 s (fired at {firedAt} ms)");
        Check.False(w2.LastFireWasEarly, "not early");

        // Quiet but the stall is younger than 2.5 s: nothing yet.
        var w3 = new CombatStallWatchdog();
        act = w3.Tick(T0, true, T0, out _, quiet: true);
        act = w3.Tick(T0.AddMilliseconds(2000), true, T0, out _, quiet: true);
        Check.Eq(act, CombatStallWatchdog.StallAction.None, "2 s: too early even when quiet");
    }

    private static readonly GhostSpot Mob = new(162.36, -168.80, -6.0);

    private static void GhostVerdict()
    {
        var c = new GhostCheck();
        const int Ghost = unchecked((int)0x800B87EC);
        c.Arm(Ghost, T0, Mob);
        Check.True(c.IsArmed, "armed by the step-1 re-issue");
        Check.Eq(c.Check(Ghost, T0.AddMilliseconds(500), DateTime.MinValue, Mob, 900_000), GhostCheck.Verdict.None, "0.5 s: still waiting");
        Check.Eq(c.Check(Ghost, T0.AddMilliseconds(1600), DateTime.MinValue, Mob, 900_000), GhostCheck.Verdict.Ghost,
            "1.6 s, no swing, not moved, the server silent 15 min: ghost");
        Check.False(c.IsArmed, "one verdict per arm");

        c.Arm(Ghost, T0, Mob);
        Check.Eq(c.Check(Ghost, T0.AddMilliseconds(1600), DateTime.MinValue, Mob, -1), GhostCheck.Verdict.Ghost,
            "no server record at all (the client had it before anyone watched): ghost");

        c.Arm(Ghost, T0, null);
        Check.False(c.IsArmed, "no target position: no verdicts");
    }

    private static void GhostAlive()
    {
        const int Mob1 = 0x10001, Mob2 = 0x10002;
        var c = new GhostCheck();

        c.Arm(Mob1, T0, Mob);
        Check.Eq(c.Check(Mob1, T0.AddMilliseconds(900), T0.AddMilliseconds(800), Mob, 900_000), GhostCheck.Verdict.Alive, "a hit since the re-issue: alive");

        c.Arm(Mob1, T0, Mob);
        var moved = new GhostSpot(Mob.X + 0.4, Mob.Y, Mob.Z);
        Check.Eq(c.Check(Mob1, T0.AddMilliseconds(1600), DateTime.MinValue, moved, 900_000), GhostCheck.Verdict.Alive, "it moved: alive");

        c.Arm(Mob1, T0, Mob);
        Check.Eq(c.Check(Mob1, T0.AddMilliseconds(1600), DateTime.MinValue, Mob, 300), GhostCheck.Verdict.Alive,
            "the server sent something about it 0.3 s ago (after the re-issue): alive");

        c.Arm(Mob1, T0, Mob);
        Check.Eq(c.Check(Mob1, T0.AddMilliseconds(1600), DateTime.MinValue, Mob, 5000), GhostCheck.Verdict.Alive,
            "the server talked about it 5 s ago (under 8 s): not called a ghost");

        c.Arm(Mob1, T0, Mob);
        Check.Eq(c.Check(Mob2, T0.AddMilliseconds(1600), DateTime.MinValue, Mob, 900_000), GhostCheck.Verdict.Alive, "another target now: no verdict on the old one");

        c.Arm(Mob1, T0, Mob);
        Check.Eq(c.Check(Mob1, T0.AddMilliseconds(1600), DateTime.MinValue, null, 900_000), GhostCheck.Verdict.Alive, "target position gone: no verdict");
    }

    private static void GhostTrackerClears()
    {
        var g = new GhostTracker();
        string? cleared = null;
        g.Cleared = (id, why) => cleared = $"{id:X}:{why}";
        const uint Lb = 0x6146;
        g.Mark(1, "Olthoi Slayer", Mob, Lb, T0);
        g.Mark(2, "Olthoi Slasher", Mob, Lb, T0);
        g.Mark(3, "Olthoi Slayer", Mob, Lb, T0);
        Check.Eq(g.Count, 3, "three ghosts");
        Check.True(g.IsGhost(1), "marked");

        g.NoteServerUpdate(1, T0.AddMinutes(2));
        Check.False(g.IsGhost(1), "a server update: real again");
        Check.True(cleared != null && cleared.StartsWith("1:"), $"logged: {cleared}");
        Check.Eq(g.SilentMs(1, T0.AddMinutes(2).AddSeconds(3)), 3000.0, "the plugin's own update clock");

        g.CheckMoved(id => id == 2 ? new GhostSpot(Mob.X, Mob.Y + 1, Mob.Z) : Mob);
        Check.False(g.IsGhost(2), "it moved: real");
        Check.True(g.IsGhost(3), "the one that didn't move stays a ghost (no fixed timeout)");

        g.OnPlayerLandblock(Lb);
        Check.True(g.IsGhost(3), "same landblock: kept");
        g.OnPlayerLandblock(0x6346);
        Check.False(g.IsGhost(3), "another landblock: let go");

        g.Mark(4, "Olthoi Slayer", Mob, Lb, T0);
        g.NoteDeleted(4);
        Check.False(g.IsGhost(4), "deleted (e.g. removed from the client): forgotten");
        Check.Eq(g.MarkedTotal, 4, "four marked in all");
    }

    private const uint Player = 0x50000180;
    private const uint Lb = 0x61460000;

    private static void GhostSkippedByCombat()
    {
        FakeHost.Reset();
        Place(Player, "Drakkon", 96, 96, creature: false);
        FakeHost.PlayerPose = (Lb | 0x0329, 96f, 96f, 0f);
        uint ghost = 0x800B87E3, real = 0x800F2516;
        Place(ghost, "Olthoi Slasher", 96, 97.4f);     // 1.4 m: the pick, but a ghost
        Place(real, "Olthoi Slasher", 96, 99.0f);      // 3 m
        var s = new LegacyUiSettings { MonsterRange = 20 };
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { ghost, real });
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        c.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());

        c.ScanNearbyTargets();
        c.HandleCombatTrigger();
        Check.Eq((uint)c.activeTargetId, ghost, "first pick: the closer one (not known to be a ghost yet)");

        c.MarkGhost((int)ghost, "Olthoi Slasher", 900_000, "test");
        Check.Eq(c.activeTargetId, 0, "marking the target drops it");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[Ghost] 0x800B87E3") && l.Contains("is a ghost")), "logged");
        Thread.Sleep(80);   // the scan runs at most every 50 ms
        c.ScanNearbyTargets();
        Check.False(c.ScannedTargets.Any(t => (uint)t.Id == ghost), "the ghost isn't scanned");
        c.HandleCombatTrigger();
        Check.Eq((uint)c.activeTargetId, real, "the real one is fought");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] lock 0x800F2516") && l.Contains("1 ghost(s) in range skipped")),
            "the lock line counts the skipped ghost");

        // The server sends an update for it: real again, targetable.
        c.OnServerObjectUpdate(ghost);
        Check.False(c.Ghosts.IsGhost((int)ghost), "an update: no longer a ghost");
        Thread.Sleep(80);
        c.ScanNearbyTargets();
        Check.True(c.ScannedTargets.Any(t => (uint)t.Id == ghost), "scanned again");
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x0329, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }

    private static void PhysicalNewTargetInterval()
    {
        const int Melee = CombatMode.Melee, Missile = CombatMode.Missile;
        Check.Eq(CombatManager.AttackIntervalMs(Melee, 1500, true, false), 1000.0, "melee, same target: the 1000 ms re-attack cadence");
        Check.Eq(CombatManager.AttackIntervalMs(Melee, 1500, true, true), CombatManager.NewTargetPhysicalFloorMs, "melee, new target: the short floor");
        Check.Eq(CombatManager.AttackIntervalMs(Missile, 1500, true, true), CombatManager.NewTargetPhysicalFloorMs, "missile, new target: the short floor");
        Check.Eq(CombatManager.AttackIntervalMs(Melee, 1500, false, true), 1000.0, "no server gates: 1000 ms as before");
        Check.True(CombatManager.AttackAfterCancelMs < 1000.0, "after a CancelAttack the attack follows sooner than a full interval");
    }
}

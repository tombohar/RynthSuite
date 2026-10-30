using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Attack latency (2026-10-04, "it does a little thinking before it attacks"). Measured from the
// client logs: a caster waited for a busy count AC had already lowered inline at the server's
// UseDone (the busy hook never sees that decrement), up to the 5 s force-clear, after every cast
// and after corpse opens; a killed monster was locked again for one tick from the old scan list;
// and the first cast at a new target waited out the Attack Spell Delay left from the last target
// although the UseDone and gesture gates already hold it. Plus the [CombatLatency] log line.
internal static class AttackLatencyTests
{
    private const uint Player = 0x50000E01;
    private const uint Lb = 0xA9B40000;

    public static void Register(Runner r)
    {
        r.Add("attack latency: a busy count the server finished (UseDone since it rose, no gesture) is a leftover", BusyLeftover);
        r.Add("attack latency: without the engine's UseDone counter the busy count always holds", BusyWithoutServerGates);
        r.Add("attack latency: the first cast at a new target skips the Attack Spell Delay only with the server gates", NewTargetInterval);
        r.Add("attack latency: a killed monster isn't locked again from the old scan list", NoRelockOfTheDead);
        r.Add("attack latency: [CombatLatency] charges each wait, one line per engagement, throttled", LatencyLine);
    }

    // ── Busy leftovers ────────────────────────────────────────────────────────

    private static void BusyLeftover()
    {
        // The pure rule.
        var g = new CombatBusyGate();
        g.NoteCount(1, useDoneSeqNow: 5);
        Check.False(g.IsStale(1, true, 5, false), "just rose, no UseDone since: real");
        Check.True(g.IsStale(1, true, 6, false), "a UseDone since it rose: leftover");
        Check.False(g.IsStale(1, true, 6, true), "a gesture still animating: real");
        g.NoteCount(2, useDoneSeqNow: 6);
        Check.False(g.IsStale(2, true, 6, false), "rose again after that UseDone (a new action): real");
        g.NoteCount(1, useDoneSeqNow: 6);
        Check.False(g.IsStale(1, true, 6, false), "a hooked decrement doesn't count as the server finishing");
        Check.True(g.IsStale(1, true, 7, false), "the next UseDone: leftover");
        g.NoteCount(0, useDoneSeqNow: 7);
        Check.False(g.IsStale(0, true, 7, false), "zero is never busy");
        g.NoteCount(1, useDoneSeqNow: 7);
        Check.False(g.IsStale(1, true, 7, false), "a fresh rise from zero: real until its UseDone");

        // Through CombatManager and the host.
        FakeHost.Reset();
        FakeHost.ServerGates = true;
        FakeHost.UseDoneSeq = 40;
        var c = MakeCombat(new LegacyUiSettings());
        c.BusyCount = 1;                 // a cast went out: the hooked increment
        Check.False(c.BusyIsLeftover(), "cast in flight (no UseDone yet): combat waits");
        FakeHost.CastBusy = 1;
        FakeHost.UseDoneSeq = 41;        // the server finished it, AC lowered the field inline
        Check.False(c.BusyIsLeftover(), "UseDone in, gesture still animating: combat waits");
        FakeHost.CastBusy = 0;
        Check.True(c.BusyIsLeftover(), "UseDone in, gesture over: the count is a leftover");
        c.BusyCount = 0;                 // the shared mirror reset (RynthAiPlugin.OnCombatBusyStale)
        Check.False(c.BusyIsLeftover(), "reset to 0: nothing to wait for");
        c.BusyCount = 1;                 // a corpse open (UseObject) after that
        Check.False(c.BusyIsLeftover(), "a new action after the last UseDone: combat waits");
    }

    private static void BusyWithoutServerGates()
    {
        var g = new CombatBusyGate();
        g.NoteCount(1, 0);
        Check.False(g.IsStale(1, hasUseDoneSeq: false, 0, false), "no UseDone counter: never a leftover");

        FakeHost.Reset();                // ServerGates off: an engine without GetUseDoneSeq
        var c = MakeCombat(new LegacyUiSettings());
        c.BusyCount = 1;
        Check.False(c.BusyIsLeftover(), "old engine: the busy count holds as before");
    }

    // ── Cast interval ─────────────────────────────────────────────────────────

    private static void NewTargetInterval()
    {
        const int Magic = CombatMode.Magic, Melee = CombatMode.Melee, Missile = CombatMode.Missile;
        Check.Eq(CombatManager.AttackIntervalMs(Melee, 1500, true, false), 1000.0, "melee, same target: 1000 ms");
        Check.Eq(CombatManager.AttackIntervalMs(Melee, 1500, true, true), CombatManager.NewTargetPhysicalFloorMs, "melee, new target: the short floor (2026-10-12)");
        Check.Eq(CombatManager.AttackIntervalMs(Missile, 1500, true, false), 1000.0, "missile, same target: 1000 ms");
        Check.Eq(CombatManager.AttackIntervalMs(Magic, 1500, true, false), 1500.0, "magic, same target: the setting");
        Check.Eq(CombatManager.AttackIntervalMs(Magic, 2500, true, false), 2500.0, "magic, same target: a slower setting is kept");
        Check.Eq(CombatManager.AttackIntervalMs(Magic, 0, true, false), 0.0, "magic, setting 0: no delay of its own (the server gates hold the cast)");
        Check.Eq(CombatManager.AttackIntervalMs(Magic, 0, false, false), CombatManager.NoGatesAttackFloorMs, "setting 0 without the server gates: the 250 ms floor");
        Check.Eq(CombatManager.AttackIntervalMs(Magic, -1, true, false), 1500.0, "unset: 1500");
        Check.Eq(CombatManager.AttackIntervalMs(Magic, 1500, true, true), CombatManager.NewTargetCastFloorMs,
            "magic, new target, server gates: the short floor (UseDone + gesture hold the cast)");
        Check.Eq(CombatManager.AttackIntervalMs(Magic, 1500, false, true), 1500.0,
            "magic, new target, no server gates: the setting (it is the only pacing then)");
        Check.Eq(CombatManager.AttackIntervalMs(Magic, 250, true, true), 250.0, "a setting under the floor stays as set");
    }

    // ── No re-lock of a killed monster ────────────────────────────────────────

    private static void NoRelockOfTheDead()
    {
        FakeHost.Reset();
        Place(Player, "Me", 96, 96, creature: false);
        FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
        uint a = 0x80000301, b = 0x80000302;
        Place(a, "Olthoi Swarm Nymph", 96, 97.5f);       // 1.5 m north: the pick
        Place(b, "Olthoi Swarm Gardener", 96, 98.0f);    // 2.0 m north

        var s = new LegacyUiSettings { MonsterRange = 20 };
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, new[] { a, b });
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        c.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
        c.ScanNearbyTargets();
        c.HandleCombatTrigger();
        Check.Eq((uint)c.activeTargetId, a, "the closer Nymph is locked");

        // The kill notice lands between ticks; the next tick picks again before the scan has
        // run again (it runs at most every 50 ms), so the old list is all it has.
        c.OnKillNotification("Olthoi Swarm Nymph's perforated corpse falls before you!");
        Check.Eq(c.activeTargetId, 0, "the kill drops the Nymph");
        Check.False(c.ScannedTargets.Any(t => (uint)t.Id == a), "and takes it out of the current scan list");
        c.HandleCombatTrigger();
        Check.Eq((uint)c.activeTargetId, b, "the next pick is the Gardener, not the dead Nymph again");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[CombatTarget] lock") && l.Contains("0x80000301")), 1,
            "the Nymph was locked once (before the kill), not again after it");
    }

    // ── [CombatLatency] ───────────────────────────────────────────────────────

    private static void LatencyLine()
    {
        var t = new CombatLatencyTracker();
        Check.False(t.Pending, "nothing pending before a lock");
        t.OnLock(1, 1000, killAtMs: 900);
        t.OnTick(1, 1033, CombatLatencyTracker.Wait.Stance);
        t.OnTick(1, 1066, CombatLatencyTracker.Wait.Busy);
        t.OnTick(1, 1100, CombatLatencyTracker.Wait.Interval);
        Check.Eq(t.Waited(CombatLatencyTracker.Wait.Busy), 33.0, "busy charged with its tick");
        string? line = t.OnAttack(1, 1133, "Melee", "Olthoi Swarm Nymph");
        Check.True(line != null, "the first attack logs the engagement");
        Check.Eq(line, "[CombatLatency] lock->attack 133 ms (stance 33, face 0, busy 33, cast 0, interval 34, other 33) Melee 'Olthoi Swarm Nymph', kill->attack 233 ms",
            "the line: total, each wait, mode, target, time since the kill");
        Check.False(t.Pending, "logged once: the next attack at it is not an engagement");
        Check.True(t.OnAttack(1, 1200, "Melee", null) == null, "a second attack logs nothing");

        t.OnLock(2, 1500);
        Check.True(t.OnAttack(2, 1600, "Magic", "B") == null, "a second engagement within 1 s is counted, not logged");
        t.OnLock(3, 2600);
        string? third = t.OnAttack(3, 2700, "Magic", "C");
        Check.True((third ?? "").Contains("lock->attack 100 ms"), "the next one logs");
        Check.True((third ?? "").Contains("(+1 engagements not logged)"), "and says one wasn't");
        Check.False((third ?? "").Contains("kill->attack"), "a lock that didn't follow a kill has no kill->attack");

        t.OnLock(4, 5000);
        t.OnTick(5, 5033, CombatLatencyTracker.Wait.Busy);   // the target changed without a new lock
        Check.False(t.Pending, "another target: the engagement is abandoned");
        Check.True(t.OnAttack(4, 5100, "Melee", null) == null, "and logs nothing");
        t.OnLock(6, 6000);
        t.OnDrop();
        Check.True(t.OnAttack(6, 6100, "Melee", null) == null, "dropped before attacking: nothing");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CombatManager MakeCombat(LegacyUiSettings s)
    {
        var host = FakeHost.Create();
        var cache = FakeHost.MakeCache(host, Player, Array.Empty<uint>());
        var c = new CombatManager(host, s, cache);
        c.SetPlayerId(Player);
        return c;
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }
}

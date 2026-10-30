using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Combat target selection (2026-10-10): the Targeting setting (lowest health, closest, farthest,
// least turning) and Target Lock. Combat used to switch targets mid-fight on a blended score; now
// a locked target is kept until it dies or goes, leaves Monster Range, or a higher-priority
// monster is in range, and with the lock off a switch needs a clear margin under the rule.
internal static class TargetingTests
{
    private const uint Player = 0x50000E01;
    private const uint Lb = 0xA9B40000;
    private const uint A = 0x80000401, B = 0x80000402, C = 0x80000403, Boss = 0x80000404;

    private const int LowestHealth = 0, Closest = 1, Farthest = 2, LeastTurning = 3;

    public static void Register(Runner r)
    {
        r.Add("targeting: each scheme picks its monster", SchemesPick);
        r.Add("targeting: unknown health counts as full; ties go to the closest, then the lower id", UnknownHealthAndTies);
        r.Add("targeting: Target Lock keeps the target when a better one of its tier appears", LockKeeps);
        r.Add("targeting: Target Lock lets go when the target goes (after the scan grace)", LockReleasesWhenGone);
        r.Add("targeting: Target Lock lets go when the target leaves Monster Range", LockReleasesOutOfRange);
        r.Add("targeting: a higher-priority monster in range takes over, lock or not", HigherPriorityWins);
        r.Add("targeting: lock off switches only past each scheme's margin", LockOffMargins);
        r.Add("targeting: lock off never switches with an attack at the target in flight", NoSwitchInFlight);
        r.Add("targeting: settings default to lowest health with the lock on", Defaults);
    }

    // ── Schemes ───────────────────────────────────────────────────────────────

    private static void SchemesPick()
    {
        // A: close, facing away, lightly wounded. B: mid, nearly ahead, badly wounded.
        // C: far, health unknown.
        var health = new Dictionary<uint, float> { [A] = 0.8f, [B] = 0.3f };
        (uint, double, double)[] scan = { (A, 2, 90), (B, 4, 10), (C, 6, 50) };

        Check.Eq(new Rig(LowestHealth, true, health).Pick(scan), B, "lowest health: B at 30%");
        Check.Eq(new Rig(Closest, true, health).Pick(scan), A, "closest: A at 2 m");
        Check.Eq(new Rig(Farthest, true, health).Pick(scan), C, "farthest: C at 6 m");
        Check.Eq(new Rig(LeastTurning, true, health).Pick(scan), B, "least turning: B, 10 degrees off");

        var t = new Rig(LeastTurning, true, health);
        t.Pick((A, 2, 90), (B, 4, 60), (C, 6, 5));
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] lock 0x80000403") && l.Contains("least turning: turn 5 deg")),
            "the lock line names the scheme and the deciding value");
    }

    private static void UnknownHealthAndTies()
    {
        // B is at 95%, A unknown: unknown counts as full, so B is the more wounded.
        var t = new Rig(LowestHealth, true, new Dictionary<uint, float> { [B] = 0.95f });
        Check.Eq(t.Pick((A, 2, 0), (B, 5, 0)), B, "a known 95% beats an unknown (full) health");

        // Every health unknown: all tie at full, so the closest.
        t = new Rig(LowestHealth, true);
        Check.Eq(t.Pick((A, 3, 0), (B, 1.5, 0), (C, 2, 0)), B, "all unknown: the closest");
        Check.True(FakeHost.Logs.Any(l => l.Contains("lowest health: health unknown (as full)")), "logged as unknown");

        // Same distance: the lower id.
        t = new Rig(Closest, true);
        Check.Eq(t.Pick((B, 2, 0), (A, 2, 0)), A, "a tie in distance: the lower id");

        // Lock off: an unknown-health monster never looks more wounded than the incumbent.
        t = new Rig(LowestHealth, false, new Dictionary<uint, float> { [A] = 0.5f });
        Check.Eq(t.Pick((A, 3, 0), (B, 1, 0)), A, "A at 50% is picked over an unknown B");
        Check.Eq(t.Pick((A, 3, 0), (B, 1, 0), (C, 1, 0)), A, "unknown B and C don't take over");
    }

    // ── Target Lock ───────────────────────────────────────────────────────────

    private static void LockKeeps()
    {
        var t = new Rig(Closest, true);
        Check.Eq(t.Pick((A, 4, 0)), A, "A, alone, is locked");
        int before = FakeHost.Logs.Count(l => l.Contains("[CombatTarget]"));
        Check.Eq(t.Pick((A, 4, 0), (B, 1, 0)), A, "B walks up to 1 m: the lock keeps A");
        Check.Eq(t.Pick((A, 4.5, 30), (B, 0.5, 0), (C, 0.8, 0)), A, "and keeps it with more around");
        Check.Eq(FakeHost.Logs.Count(l => l.Contains("[CombatTarget]")), before, "nothing new is logged while the lock holds");

        t = new Rig(LowestHealth, true, new Dictionary<uint, float> { [A] = 0.7f, [B] = 0.9f });
        Check.Eq(t.Pick((A, 3, 0), (B, 2, 0)), A, "lowest health: A at 70%");
        t.Health(B, 0.1f);
        Check.Eq(t.Pick((A, 3, 0), (B, 2, 0)), A, "B drops to 10%: the lock keeps A");
    }

    private static void LockReleasesWhenGone()
    {
        var t = new Rig(Closest, true);
        Check.Eq(t.Pick((A, 2, 0), (B, 3, 0)), A, "A is locked");
        Check.Eq(t.Pick((B, 3, 0)), A, "A blinks out of the scan: kept within the scan grace");
        t.Advance(1000);
        Check.Eq(t.Pick((B, 3, 0)), A, "still within the 1.5 s grace");
        t.Advance(1000);
        Check.Eq(t.Pick((B, 3, 0)), B, "gone past the grace: B");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] switch 0x80000401") && l.Contains("(gone,")),
            "the switch says why: gone");

        // A kill drops the target outright (DropTarget), and the next pick is fresh.
        t = new Rig(Closest, true);
        t.Pick((A, 2, 0), (B, 3, 0));
        t.Combat.OnKillNotification("Mob A's perforated corpse falls before you!");
        Check.Eq(t.Combat.activeTargetId, 0, "the kill drops A");
        Check.Eq(t.Pick((B, 3, 0)), B, "and B is locked");
    }

    private static void LockReleasesOutOfRange()
    {
        var t = new Rig(Closest, true, monsterRange: 5);
        Check.Eq(t.Pick((A, 4, 0), (B, 4.5, 0)), A, "A is locked at 4 m");
        Check.Eq(t.Pick((A, 5.5, 0)), A, "A steps past Monster Range, nothing else in range: kept");
        Check.Eq(t.Pick((A, 6.5, 0), (B, 4.8, 0)), B, "past Monster Range with B inside it: B");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] switch 0x80000401") && l.Contains("out of Monster Range, 6.5 m")),
            "the switch says why: out of Monster Range");
        Check.Eq(t.Pick((A, 3, 0), (B, 4.8, 0)), B, "A coming back closer doesn't take it back (B is locked now)");
    }

    private static void HigherPriorityWins()
    {
        foreach (bool lockOn in new[] { true, false })
        {
            string mode = lockOn ? "lock on" : "lock off";
            var t = new Rig(Closest, lockOn, monsterRange: 10);
            t.S.MonsterRules.Add(new MonsterRule { Name = "Boss", Priority = 2 });
            Check.Eq(t.Pick((A, 2, 0), (B, 3, 0)), A, $"{mode}: A is locked");
            Check.Eq(t.Pick((A, 2, 0), (B, 3, 0), (Boss, 12, 0)), A, $"{mode}: a Boss outside Monster Range doesn't count");
            Check.Eq(t.Pick((A, 2, 0), (B, 3, 0), (Boss, 9, 0)), Boss, $"{mode}: the Boss inside Monster Range takes over at once, farther or not");
            Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] switch 0x80000401") && l.Contains("priority 1 < 2")),
                $"{mode}: the switch says why: priority");
            Check.Eq(t.Pick((A, 1, 0), (Boss, 9, 0)), Boss, $"{mode}: the Boss is kept over a closer, lower-priority A");
        }
    }

    // ── Lock off ──────────────────────────────────────────────────────────────

    private static void LockOffMargins()
    {
        // Closest: 1.5 m.
        var t = new Rig(Closest, false);
        Check.Eq(t.Pick((A, 3, 0)), A, "closest: A at 3 m");
        Check.Eq(t.Pick((A, 3, 0), (B, 2, 0)), A, "B 1 m closer: kept");
        Check.Eq(t.Pick((A, 3, 0), (B, 1.2, 0)), B, "B 1.8 m closer: switch");
        Check.True(FakeHost.Logs.Any(l => l.Contains("[CombatTarget] switch 0x80000401") && l.Contains("closest: 3.0 m, another is clearly better")
                                       && l.Contains("-> 0x80000402") && l.Contains("closest: 1.2 m")),
            "the switch names the scheme and both values");

        // Farthest: 1.5 m.
        t = new Rig(Farthest, false, monsterRange: 10);
        Check.Eq(t.Pick((A, 8, 0)), A, "farthest: A at 8 m");
        Check.Eq(t.Pick((A, 8, 0), (B, 9, 0)), A, "B 1 m farther: kept");
        Check.Eq(t.Pick((A, 8, 0), (B, 9.8, 0)), B, "B 1.8 m farther: switch");

        // Lowest health: 5 percentage points.
        t = new Rig(LowestHealth, false, new Dictionary<uint, float> { [A] = 0.5f, [B] = 0.9f });
        Check.Eq(t.Pick((A, 3, 0), (B, 2, 0)), A, "lowest health: A at 50%");
        t.Health(B, 0.47f);
        Check.Eq(t.Pick((A, 3, 0), (B, 2, 0)), A, "B at 47% (3 points lower): kept");
        t.Health(B, 0.40f);
        Check.Eq(t.Pick((A, 3, 0), (B, 2, 0)), B, "B at 40% (10 points lower): switch");

        // Least turning: 20 degrees.
        t = new Rig(LeastTurning, false);
        Check.Eq(t.Pick((A, 3, 30)), A, "least turning: A");
        Check.Eq(t.Pick((A, 3, 40), (B, 3, 25)), A, "B needs 15 degrees less: kept");
        Check.Eq(t.Pick((A, 3, 40), (B, 3, 15)), B, "B needs 25 degrees less: switch");
    }

    private static void NoSwitchInFlight()
    {
        // An attack at A with no damage since: held for the flight window.
        var t = new Rig(Closest, false);
        Check.Eq(t.Pick((A, 3, 0)), A, "A is locked");
        t.Combat.RecordOffensiveCast(unchecked((int)A));
        Check.Eq(t.Pick((A, 3, 0), (B, 0.5, 0)), A, "a cast at A in flight: B (2.5 m closer) waits");
        t.Advance(2000);
        Check.Eq(t.Pick((A, 3, 0), (B, 0.5, 0)), A, "still in the 4 s window");
        t.Advance(2500);
        Check.Eq(t.Pick((A, 3, 0), (B, 0.5, 0)), B, "the window passed with nothing landed: switch");

        // Damage landing ends the hold.
        t = new Rig(Closest, false);
        t.Pick((A, 3, 0));
        t.Combat.RecordOffensiveCast(unchecked((int)A));
        Check.Eq(t.Pick((A, 3, 0), (B, 0.5, 0)), A, "cast in flight: kept");
        t.Combat.ReportDamageOnTarget(unchecked((int)A));
        Check.Eq(t.Pick((A, 3, 0), (B, 0.5, 0)), B, "the cast landed: switch");

        // A target that never takes damage can't hold it forever under continuous fire.
        t = new Rig(Closest, false);
        t.Pick((A, 3, 0));
        t.Advance(16000);
        t.Combat.RecordOffensiveCast(unchecked((int)A));
        Check.Eq(t.Pick((A, 3, 0), (B, 0.5, 0)), B, "16 s locked without damage: a fresh cast doesn't hold it");
    }

    private static void Defaults()
    {
        var s = new LegacyUiSettings();
        Check.Eq(s.TargetSelection, 0, "Targeting: lowest health");
        Check.True(s.TargetLock, "Target Lock: on");
        var loaded = System.Text.Json.JsonSerializer.Deserialize("{\"MonsterRange\":7}", RynthAiJsonContext.Default.LegacyUiSettings);
        Check.True(loaded != null && loaded.TargetLock, "a profile saved before the setting loads the lock as on");

        // Profile switch/load copies them (CopySettings is the hand-written field list).
        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        copy.Invoke(null, new object[] { new LegacyUiSettings { TargetSelection = 3, TargetLock = false }, dst });
        Check.Eq(dst.TargetSelection, 3, "a profile's Targeting is copied");
        Check.False(dst.TargetLock, "and its lock off");
        copy.Invoke(null, new object[] { loaded!, dst });
        Check.True(dst.TargetLock && dst.TargetSelection == 0, "an old profile loads the defaults");

        // Engine Settings face: the payload carries them; an engine without the fields leaves them alone.
        s = new LegacyUiSettings { TargetSelection = 2, TargetLock = false };
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dash, s);
        typeof(LegacyDashboardRenderer).GetField("_advancedSettingsUi", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dash, new LegacyAdvancedSettingsUi(s));   // BuildSettingsJson reads its crafting state
        var sent = System.Text.Json.JsonSerializer.Deserialize(dash.BuildSettingsJson(), RynthAiJsonContext.Default.SettingsBridgePayload);
        Check.True(sent != null && sent.TargetSelection == 2 && sent.TargetLock == false, "sent to the engine face");
        dash.ApplySettingsJson("{\"PatrolOnLogin\":false}");
        Check.True(s.TargetSelection == 2 && !s.TargetLock, "a save without the fields leaves them alone");
        dash.ApplySettingsJson("{\"TargetSelection\":1,\"TargetLock\":true}");
        Check.True(s.TargetSelection == 1 && s.TargetLock, "a save with them sets them");
        dash.ApplySettingsJson("{\"TargetSelection\":9}");
        Check.Eq(s.TargetSelection, 3, "an out-of-range choice is clamped");
    }

    // ── Rig ───────────────────────────────────────────────────────────────────

    private sealed class Rig
    {
        public readonly CombatManager Combat;
        public readonly LegacyUiSettings S;
        private readonly WorldObjectCache _cache;
        private DateTime _now = RynthClock.Now;   // the clock RynthAi times with

        public Rig(int scheme, bool targetLock, Dictionary<uint, float>? health = null, int monsterRange = 20)
        {
            FakeHost.Reset();
            Place(Player, "Me", 96, 96, creature: false);
            FakeHost.PlayerPose = (Lb | 0x1C, 96f, 96f, 0f);
            Place(A, "Mob A", 96, 98);
            Place(B, "Mob B", 96, 99);
            Place(C, "Mob C", 96, 100);
            Place(Boss, "Boss", 96, 101);
            S = new LegacyUiSettings { MonsterRange = monsterRange, TargetSelection = scheme, TargetLock = targetLock };
            var host = FakeHost.Create();
            _cache = FakeHost.MakeCache(host, Player, new[] { A, B, C, Boss }, health);
            Combat = new CombatManager(host, S, _cache);
            Combat.SetPlayerId(Player);
            Combat.SetDamageStores(null, new RynthCore.Plugin.RynthAi.CreatureData.MonsterDamageStore());
            Combat.TargetClock = () => _now;
        }

        public void Health(uint id, float ratio) => _cache.OnUpdateHealth(id, ratio);
        public void Advance(double ms) => _now = _now.AddMilliseconds(ms);

        /// <summary>One tick: the scan holds these (id, distance m, facing angle deg); returns the target.</summary>
        public uint Pick(params (uint Id, double Dist, double Angle)[] scan)
        {
            Combat.SetScannedTargetsForTests(scan.Select(t => new CombatManager.ScannedTarget
            {
                Id = unchecked((int)t.Id), Distance = t.Dist, Angle = t.Angle, Bearing = double.NaN, Name = FakeHost.Names[t.Id],
            }));
            Combat.HandleCombatTrigger();
            return unchecked((uint)Combat.activeTargetId);
        }
    }

    private static void Place(uint id, string name, float x, float y, bool creature = true)
    {
        FakeHost.Names[id] = name;
        FakeHost.Positions[id] = (Lb | 0x1C, x, y, 0f);
        if (creature) FakeHost.ItemTypes[id] = 0x10;
    }
}

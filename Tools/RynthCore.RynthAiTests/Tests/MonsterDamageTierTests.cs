using System;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// MonsterDamageStore and difficulty tiers (2026-10-03; a server plugin reports them, DifficultyTier).
// Scaled worlds and copies hold the same monsters (same wcids) with more health, skills and damage a tier, so the
// store keeps the HP pool, casts-to-kill, accuracy, fight times and damage taken per (wcid,
// tier). Tier 0 must stay today's data in today's rows: on every other server the tier is
// always 0, and a file written there must be what the old code wrote.
internal static class MonsterDamageTierTests
{
    private const uint Drudge = 7;          // any wcid
    private const uint Wand = 0x80001234;
    private const string Fire = "Fire";

    public static void Register(Runner r)
    {
        r.Add("damage tiers: tier 0 file round-trips unchanged", Tier0RoundTrip);
        r.Add("damage tiers: a tier's numbers are kept apart from real Dereth", TierKeptApart);
        r.Add("damage tiers: tier rows are extra lines; the tier-0 lines don't change", TierRowsAreExtra);
        r.Add("damage tiers: snapshot, known tiers, delete and reset", SnapshotAndHousekeeping);
    }

    private static string NewFolder(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "scratch-damage-tiers", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A store with some real-Dereth learning in it, saved.</summary>
    private static (MonsterDamageStore Store, string Folder, string File) Seeded(string name)
    {
        string dir = NewFolder(name);
        var s = new MonsterDamageStore();
        s.SetCharacter(dir);
        s.RecordHit(Wand, Drudge, "Drudge Skulker", Fire, 7, 300, crit: false);
        s.RecordHit(Wand, Drudge, "Drudge Skulker", Fire, 7, 600, crit: true);
        s.RecordKill(Wand, Drudge, "Drudge Skulker", Fire, 7, 2, 900);
        s.RecordMiss(Wand, Drudge, "Drudge Skulker");
        s.RecordFightTime(Wand, Drudge, "Drudge Skulker", 4.0, "none");
        s.RecordTaken(Drudge, "Drudge Skulker", "Slash", 40);
        s.SetManualWeapon(Drudge, Wand);
        s.SaveIfDirty();
        return (s, dir, Path.Combine(dir, "monster_damage.txt"));
    }

    private static void Tier0RoundTrip()
    {
        var (_, dir, file) = Seeded("roundtrip");
        string before = File.ReadAllText(file);
        Check.False(before.Contains("\nA"), "no tier rows in a tier-0-only file");
        Check.False(before.Contains("difficulty tiers"), "no tier header in a tier-0-only file");

        var again = new MonsterDamageStore();
        again.SetCharacter(dir);
        again.SetManualWeapon(Drudge, Wand);   // same value, but marks the store dirty
        again.SaveIfDirty();
        Check.Eq(File.ReadAllText(file), before, "load + save writes the same file");
    }

    private static void TierKeptApart()
    {
        var (s, _, _) = Seeded("apart");
        double hp0 = s.GetLearnedHp(Drudge, 0);
        Check.Near(hp0, 900, 0.01, "real Dereth HP learned");

        s.Difficulty = 10;
        s.RecordKill(Wand, Drudge, "Drudge Skulker", Fire, 7, 3, 1350);
        s.RecordHit(Wand, Drudge, "Drudge Skulker", Fire, 7, 450, crit: false);
        s.RecordMiss(Wand, Drudge, "Drudge Skulker");
        s.RecordMiss(Wand, Drudge, "Drudge Skulker");

        Check.Near(s.GetLearnedHp(Drudge, 0), 900, 0.01, "tier 10 left real Dereth's HP alone");
        Check.Near(s.GetLearnedHp(Drudge, 10), 1350, 0.01, "tier 10 has its own HP");
        Check.Near(s.GetLearnedHp(Drudge), 1350, 0.01, "the tier-less reader answers for the current tier");

        double c10 = s.GetAvgCastsToKill(Wand, Drudge, Fire, 7, out int k10);
        Check.Eq((k10, c10), (1, 3.0), "casts to kill at tier 10");
        s.Difficulty = 0;
        double c0 = s.GetAvgCastsToKill(Wand, Drudge, Fire, 7, out int k0);
        Check.Eq((k0, c0), (1, 2.0), "casts to kill at tier 0 unchanged");

        // Damage per cast is shared: resistances and armour do not scale.
        double avg = s.GetAvgDamage(Wand, Drudge, Fire, 7, out int n);
        Check.Eq(n, 3, "the tier-10 hit counts toward the shared damage average");
        Check.True(avg > 300, "shared average moved");

        var (_, hit0) = s.GetSummary(Drudge, 0);
        var (_, hit10) = s.GetSummary(Drudge, 10);
        Check.Near(hit0, 2.0 / 3.0, 0.001, "real Dereth hit rate (2 hits, 1 miss)");
        Check.Near(hit10, 1.0 / 3.0, 0.001, "tier 10 hit rate (1 hit, 2 misses)");

        // A tier never fought: nothing, not real Dereth's numbers.
        Check.Eq(s.GetLearnedHp(Drudge, 15), 0.0, "an unfought tier has no HP");
        s.Difficulty = 15;
        s.GetAvgCastsToKill(Wand, Drudge, Fire, 7, out int k15);
        Check.Eq(k15, 0, "an unfought tier has no casts to kill (no one-shot guess)");

        // Rules stay shared.
        Check.Eq(s.GetManualWeapon(Drudge), Wand, "the weapon rule is the same at every tier");
    }

    private static void TierRowsAreExtra()
    {
        var (s, dir, file) = Seeded("extra");
        string[] tier0 = File.ReadAllLines(file);

        // Only tier-scaled observations (no hits: those also feed the shared damage average).
        s.Difficulty = 10;
        s.RecordKill(Wand, Drudge, "Drudge Skulker", Fire, 7, 3, 1350);
        s.RecordMiss(Wand, Drudge, "Drudge Skulker");
        s.RecordFightTime(Wand, Drudge, "Drudge Skulker", 6.5, "Fire");
        s.RecordTaken(Drudge, "Drudge Skulker", "Slash", 60);
        s.SaveIfDirty();

        string[] all = File.ReadAllLines(file);
        bool IsTierLine(string l) => l.StartsWith("A") || l.StartsWith("# A");
        Check.Eq(string.Join("\n", all.Where(l => !IsTierLine(l))), string.Join("\n", tier0), "tier-0 lines identical");
        Check.True(all.Any(l => l.StartsWith("# Above tier 0: the server's difficulty tiers")), "the tier rows' header");
        Check.True(all.Any(l => l.StartsWith("AH|10|7|Drudge Skulker|1350|1")), "AH row written");
        Check.True(all.Any(l => l.StartsWith("AD|10|7|Drudge Skulker|" + Wand + "|Fire|7|3|1")), "AD row written");
        Check.True(all.Any(l => l.StartsWith("AU|10|7|")), "AU row written");
        Check.True(all.Any(l => l.StartsWith("APK|10|7|Drudge Skulker|Fire|1|6.5")), "APK row written");
        Check.True(all.Any(l => l.StartsWith("AT|10|7|Drudge Skulker|Slash|1|60|60")), "AT row written");

        // Reload: tier numbers come back, tier 0 untouched.
        var again = new MonsterDamageStore();
        again.SetCharacter(dir);
        Check.Near(again.GetLearnedHp(Drudge, 10), 1350, 0.01, "tier HP reloaded");
        Check.Near(again.GetLearnedHp(Drudge, 0), 900, 0.01, "tier-0 HP reloaded");
        again.Difficulty = 10;
        Check.Eq(again.GetAvgCastsToKill(Wand, Drudge, Fire, 7, out int k), 3.0, "tier casts reloaded");
        Check.Eq(k, 1, "tier kill samples reloaded");
        var (w10, p10, t10) = again.GetDetail(Drudge, 10);
        Check.Eq((w10.Count, w10[0].Misses), (1, 1), "tier weapon use reloaded");
        Check.Eq((p10.Count, p10[0].Element, p10[0].Kills), (1, "Fire", 1), "tier summon kills reloaded");
        Check.Eq((t10.Count, t10[0].Max), (1, 60.0), "tier damage taken reloaded");
        var (_, p0, t0) = again.GetDetail(Drudge, 0);
        Check.Eq((p0.Count, p0[0].Element), (1, ""), "tier-0 summon row unchanged (no summon)");
        Check.Eq(t0[0].Max, 40.0, "tier-0 damage taken unchanged");
    }

    private static void SnapshotAndHousekeeping()
    {
        var (s, _, _) = Seeded("snapshot");
        s.Difficulty = 10;
        s.RecordKill(Wand, Drudge, "Drudge Skulker", Fire, 7, 3, 1350);
        s.Difficulty = 5;
        s.RecordKill(Wand, Drudge, "Drudge Skulker", Fire, 7, 2, 1125);

        Check.Eq(string.Join(",", s.KnownDifficulties()), "5,10", "known tiers");

        var r0 = s.Snapshot(0).Single();
        var r10 = s.Snapshot(10).Single();
        var r20 = s.Snapshot(20).Single();
        Check.Eq((r0.HpPool, r0.AvgCastsToKill, r0.KillSamples), (900.0, 2.0, 1), "tier-0 row");
        Check.Eq((r10.HpPool, r10.AvgCastsToKill, r10.KillSamples), (1350.0, 3.0, 1), "tier-10 row");
        Check.Eq((r20.HpPool, r20.KillSamples), (0.0, 0), "an unfought tier lists the row with nothing learned");
        Check.Eq(r10.AvgDamage, r0.AvgDamage, "damage per cast is the same row at every tier");

        s.ClearAllStats();
        Check.Eq(s.KnownDifficulties().Count, 0, "reset stats clears every tier");
        Check.Eq(s.GetManualWeapon(Drudge), Wand, "reset stats keeps the rules");

        s.RecordKill(Wand, Drudge, "Drudge Skulker", Fire, 7, 3, 1125);   // Difficulty is still 5
        Check.True(s.DeleteWcid(Drudge), "monster removed");
        Check.Eq(s.KnownDifficulties().Count, 0, "removing a monster removes its tiers");
    }
}

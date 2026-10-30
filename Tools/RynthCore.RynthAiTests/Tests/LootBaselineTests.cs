using RynthCore.Plugin.RynthAi;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// The loot-timer baseline (LootBaseline.cs) and the [LootSettings] line the corpse looting logs:
// each timer marked baseline or custom, floors noted, and the approach range's Min-above-Max note.
// Kelpie's and Storm's saved timers on 2026-10-14 are the real-world cases.
internal static class LootBaselineTests
{
    public static void Register(Runner r)
    {
        r.Add("loot baseline: the measured values", Values);
        r.Add("loot baseline: all baseline is said once", AllBaseline);
        r.Add("loot baseline: Kelpie's custom timers and his Min-above-Max range", Kelpie);
        r.Add("loot baseline: a timer below the code's floor says what it acts as", Storm);
        r.Add("loot baseline: legacy fractional ranges are read as 240 yd fractions", FractionalRange);
    }

    private static void Values()
    {
        LootBaseline.Timers b = LootBaseline.Values;
        Check.Eq(b, new LootBaseline.Timers(100, 300, 300, 1000, 1500, 12000), "baseline timers");
        // Above the measured 95th percentile of appraisal replies (256 ms).
        Check.True(LootBaseline.AssessWindowMs > 256, "assess window covers the 95th percentile");
        foreach (LootBaseline.Entry e in LootBaseline.Compare(b))
            Check.False(e.BelowFloor, $"{e.Label} baseline is not below its floor");
    }

    private static void AllBaseline()
    {
        string line = LootBaseline.Describe(LootBaseline.Values, 10, 2);
        Check.True(line.StartsWith("[LootSettings] Content Settle 100 ms baseline"), line);
        Check.True(line.Contains("-- all baseline"), "summary says all baseline: " + line);
        Check.False(line.Contains("custom"), "no timer is custom: " + line);
        Check.False(line.Contains("Approach Range"), "a sane range adds no note: " + line);
    }

    private static void Kelpie()
    {
        var kelpie = new LootBaseline.Timers(125, 550, 450, 1000, 1200, 12000);
        string line = LootBaseline.Describe(kelpie, 40, 240);
        Check.True(line.Contains("Content Settle 125 ms custom (baseline 100)"), line);
        Check.True(line.Contains("Empty Corpse Wait 550 ms custom (baseline 300)"), line);
        Check.True(line.Contains("Assess Window 450 ms custom (baseline 300)"), line);
        Check.True(line.Contains("Loot Retry Timeout 1000 ms baseline"), line);
        Check.True(line.Contains("Corpse Open Retry 1200 ms custom (baseline 1500)"), line);
        Check.True(line.Contains("Corpse Timeout 12000 ms baseline"), line);
        Check.True(line.Contains("-- 4 of 6 custom"), line);
        Check.True(line.Contains("Approach Range Min 240 yd is above Max 40 yd, so Min acts as 40 yd"), line);
    }

    /// <summary>Storm's saved timers, with Empty Corpse Wait lowered from 200 to 150 to go under its floor.</summary>
    private static void Storm()
    {
        var storm = new LootBaseline.Timers(100, 150, 150, 300, 1500, 8500);
        string line = LootBaseline.Describe(storm, 6.5, 2);
        Check.True(line.Contains("Empty Corpse Wait 150 ms custom (baseline 300) [acts as 200]"), line);
        Check.False(line.Contains("Assess Window 150 ms custom (baseline 300) [acts"), "150 is above the 100 floor: " + line);
        Check.True(line.Contains("-- 4 of 6 custom"), line);
    }

    private static void FractionalRange()
    {
        // 0.05 of 240 yd = 12 yd Max; Min 20 yd is above it.
        string line = LootBaseline.Describe(LootBaseline.Values, 0.05, 20);
        Check.True(line.Contains("Min 20 yd is above Max 12 yd"), line);
        // Unset values fall back to 10 / 2 yd, which is fine.
        Check.False(LootBaseline.Describe(LootBaseline.Values, 0, 0).Contains("Approach Range"), "unset range adds no note");
    }
}

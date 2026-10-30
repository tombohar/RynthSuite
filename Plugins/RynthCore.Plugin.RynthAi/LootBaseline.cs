using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// The baseline loot timers: what the Loot Timers in Advanced Settings should be unless a server or
/// connection needs otherwise. Measured from live corpse looting on ACE (Silentkelpie on
/// InfiniteLeaftide, 2026-10-14 12:38-13:53: 226 corpses opened, 39 pickups):
/// <list type="bullet">
/// <item>Every open found its items already in the object cache, so Content Settle and Empty Corpse
///   Wait were never waited on.</item>
/// <item>Appraisals (ID replies) for a corpse arrived 127 ms after the request at the median, 256 ms
///   at the 95th percentile, 350 ms at most.</item>
/// <item>No pickup needed a retry with a 1000 ms Loot Retry Timeout (it counts from when the
///   character stops animating); every pickup was confirmed 1.7-2.2 s after the send.</item>
/// <item>No corpse reached the 12 s Corpse Timeout, and 199 of 203 corpses opened on the first try.</item>
/// </list>
/// Dependency-free so the RynthAi tests compile it directly.
/// </summary>
internal static class LootBaseline
{
    /// <summary>Wait after an open for the client to fill the container (used only when the cache is empty).</summary>
    public const int ContentSettleMs = 100;
    /// <summary>Wait for the container's contents before probing it, and before calling a corpse empty.</summary>
    public const int EmptyCorpseMs = 300;
    /// <summary>Hold items for their appraisal; above the 256 ms 95th percentile of the measured replies.</summary>
    public const int AssessWindowMs = 300;
    /// <summary>Wait, once the character is still, before resending a pickup that hasn't landed.</summary>
    public const int RetryTimeoutMs = 1000;
    /// <summary>Wait before sending another open to a corpse that hasn't opened.</summary>
    public const int OpenRetryMs = 1500;
    /// <summary>Give up on a claimed corpse after this long (the clock stops during combat and buff holds).</summary>
    public const int CorpseTimeoutMs = 12000;

    /// <summary>The six loot timers that the corpse looting reads.</summary>
    public readonly record struct Timers(
        int ContentSettleMs, int EmptyCorpseMs, int AssessWindowMs,
        int RetryTimeoutMs, int OpenRetryMs, int CorpseTimeoutMs);

    /// <summary>The baseline as a set of timers (what "Use Baseline" writes into the settings).</summary>
    public static Timers Values => new(ContentSettleMs, EmptyCorpseMs, AssessWindowMs,
                                       RetryTimeoutMs, OpenRetryMs, CorpseTimeoutMs);

    /// <summary>
    /// One timer compared with its baseline. <paramref name="FloorMs"/> is the lowest value the loot
    /// code honours; a setting below it behaves as the floor.
    /// </summary>
    public readonly record struct Entry(string Label, int Value, int Baseline, int FloorMs)
    {
        public bool IsBaseline => Value == Baseline;
        public bool BelowFloor => Value < FloorMs;
    }

    /// <summary>
    /// The timers next to their baselines, in the order the Advanced Settings show them. The floors
    /// are the Math.Max minimums in CorpseOpenController (Empty Corpse Wait is floored at 200 ms for
    /// the empty check and at 300 ms for the contents wait; the lower one is reported).
    /// </summary>
    public static IReadOnlyList<Entry> Compare(Timers t) => new[]
    {
        new Entry("Content Settle",     t.ContentSettleMs, ContentSettleMs, 50),
        new Entry("Empty Corpse Wait",  t.EmptyCorpseMs,   EmptyCorpseMs,   200),
        new Entry("Assess Window",      t.AssessWindowMs,  AssessWindowMs,  100),
        new Entry("Loot Retry Timeout", t.RetryTimeoutMs,  RetryTimeoutMs,  150),
        new Entry("Corpse Open Retry",  t.OpenRetryMs,     OpenRetryMs,     500),
        new Entry("Corpse Timeout",     t.CorpseTimeoutMs, CorpseTimeoutMs, 2000),
    };

    /// <summary>
    /// The log line for a character's loot timers: each one marked "baseline" or "custom (baseline N)",
    /// plus a count, and a note when the approach range's Min is above its Max (Min is then used as Max).
    /// Ranges are in yards as stored in the settings.
    /// </summary>
    public static string Describe(Timers t, double approachMaxYards, double approachMinYards)
    {
        IReadOnlyList<Entry> entries = Compare(t);
        var sb = new StringBuilder("[LootSettings] ");
        int custom = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (i > 0) sb.Append(" | ");
            sb.Append(e.Label).Append(' ').Append(e.Value).Append(" ms ");
            if (e.IsBaseline)
            {
                sb.Append("baseline");
            }
            else
            {
                custom++;
                sb.Append("custom (baseline ").Append(e.Baseline).Append(')');
            }
            if (e.BelowFloor)
                sb.Append(" [acts as ").Append(e.FloorMs).Append(']');
        }
        sb.Append(custom == 0 ? " -- all baseline" : $" -- {custom} of {entries.Count} custom");
        double max = RangeYards(approachMaxYards, 10.0), min = RangeYards(approachMinYards, 2.0);
        if (min > max)
            sb.Append($" | Approach Range Min {min:0.#} yd is above Max {max:0.#} yd, so Min acts as {max:0.#} yd");
        return sb.ToString();
    }

    /// <summary>
    /// A stored approach range in yards, read the way CorpseOpenController.NormalizeCorpseRangeYards
    /// reads it: unset falls back, and a value of 1 or less is a legacy fraction of 240 yards.
    /// </summary>
    private static double RangeYards(double value, double fallbackYards)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            return fallbackYards;
        return value <= 1.0 ? value * 240.0 : value;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Loot.T11;

/// <summary>
/// The player counters T11 wield gates are checked against. ACECustom doesn't send these at
/// login (they aren't [SendOnLogin]), so they come from the "/aug" reply, or from a manual
/// item-aug override. Thread-safe: chat writes it, loot evaluation reads it.
/// </summary>
public sealed class T11PlayerAugs
{
    private readonly object _gate = new();
    private readonly Dictionary<T11Counter, long> _counts = new();
    private long? _itemAugOverride;

    /// <summary>When the counters were last updated from "/aug" (UTC), or null.</summary>
    public DateTime? UpdatedUtc { get; private set; }

    /// <summary>The manual item-aug count that replaces the "/aug" value, or null for automatic.</summary>
    public long? ItemAugOverride
    {
        get { lock (_gate) return _itemAugOverride; }
        set { lock (_gate) _itemAugOverride = value is < 0 ? null : value; }
    }

    /// <summary>Records one counter read from "/aug".</summary>
    public void Set(T11Counter counter, long value)
    {
        lock (_gate)
        {
            _counts[counter] = Math.Max(0, value);
            UpdatedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// The player's count for <paramref name="counter"/>, or null when unknown. Item
    /// augmentations honour the manual override.
    /// </summary>
    public long? Get(T11Counter counter)
    {
        lock (_gate)
        {
            if (counter == T11Counter.ItemAugmentations && _itemAugOverride.HasValue) return _itemAugOverride;
            return _counts.TryGetValue(counter, out long v) ? v : null;
        }
    }

    /// <summary>Forgets one counter (it reads as unknown again).</summary>
    public void Forget(T11Counter counter)
    {
        lock (_gate) _counts.Remove(counter);
    }

    /// <summary>Forgets the "/aug" counts (logout). The manual override is kept.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _counts.Clear();
            UpdatedUtc = null;
        }
    }

    /// <summary>
    /// Whether the player meets every wield gate of <paramref name="item"/>: 1 = yes (or the
    /// item has no gate), 0 = a gate is not met, -1 = a needed counter is unknown. Mirrors the
    /// server's check (Player_Inventory, WieldRequirement.Int64Stat): raw counter &gt;= amount.
    /// </summary>
    public int CanWield(T11ItemInfo item)
    {
        bool unknown = false;
        foreach (var gate in item.WieldGates)
        {
            long? have = Get(gate.Counter);
            if (have == null) { unknown = true; continue; }
            if (have.Value < gate.Amount) return 0;
        }
        return unknown ? -1 : 1;
    }
}

/// <summary>
/// Reads ACECustom's "/aug" reply into <see cref="T11PlayerAugs"/>. The reply is one chat line
/// per counter ("Item:1,234", "Triune Weave: 5 (adds ...)"), so a bare "Item:" line is only
/// trusted shortly after the "Advanced Augmentation Levels:" header.
/// </summary>
public sealed class T11AugReportParser
{
    /// <summary>How long after the header (or the last accepted line) reply lines are trusted.</summary>
    public const long WindowMs = 5000;

    private const RegexOptions Opt = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly Regex Header = new(@"^Advanced Augmentation Levels:\s*$", Opt);
    private static readonly Regex Line = new(@"^(?<what>[A-Za-z' ]+?)\s*:\s*(?<n>\d[\d,.\s']*)", Opt);

    private long _openUntil = long.MinValue;

    /// <summary>
    /// Feeds one chat line. Returns true when it updated a counter. <paramref name="nowMs"/>
    /// is any monotonic millisecond clock.
    /// </summary>
    public bool Feed(string? text, long nowMs, T11PlayerAugs into)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string line = text.Trim();
        if (Header.IsMatch(line))
        {
            _openUntil = nowMs + WindowMs;
            return false;
        }
        if (nowMs > _openUntil) return false;

        Match m = Line.Match(line);
        if (!m.Success) return false;
        T11Counter? counter = CounterFor(m.Groups["what"].Value.Trim());
        if (counter == null) return false;

        long value = 0;
        foreach (char c in m.Groups["n"].Value)
        {
            if (c < '0' || c > '9') { if (c == ' ' || c == ',' || c == '.' || c == '\'') continue; break; }
            value = value * 10 + (c - '0');
        }
        into.Set(counter.Value, value);
        _openUntil = nowMs + WindowMs;
        return true;
    }

    /// <summary>The counter a "/aug" line label stands for, or null for the ones T11 gates don't use.</summary>
    private static T11Counter? CounterFor(string label) => label.ToLowerInvariant() switch
    {
        "item" => T11Counter.ItemAugmentations,
        "triune weave" => T11Counter.TriuneWeave,
        "battlemage's wrath charm" => T11Counter.BattlemagesWrath,
        "nether veil charm" => T11Counter.NetherVeil,
        "crashing steel charm" => T11Counter.CrashingSteel,
        "true shot charm" => T11Counter.TrueShot,
        _ => null,
    };
}

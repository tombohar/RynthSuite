// Trade Shop prices: listings are priced in pyreals; a buyer pays in pyreals, trade notes (at face
// value) or single-use Legendary Keys (at the seller's pyreals-per-key rate).
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here. His rates table
// also took currencies that only one server has; those have no retail WCID, so they aren't payment
// here (TradeShopCurrencyTable.cs).
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>Which currencies the shop takes, and what a Legendary Key is worth to it.</summary>
public sealed class TradeShopRates
{
    /// <summary>Default: 2 MMD a key.</summary>
    public const long DefaultPyrealsPerLegendaryKey = 2 * TradeShopCurrencyTable.PyrealsPerMmd;

    /// <summary>What one single-use Legendary Key counts for, in pyreals (0 or less: keys not taken).</summary>
    public long PyrealsPerLegendaryKey = DefaultPyrealsPerLegendaryKey;

    public bool AcceptPyreals = true;
    public bool AcceptTradeNotes = true;
    public bool AcceptLegendaryKeys = true;

    /// <summary>True when keys are taken and have a rate.</summary>
    public bool TakesKeys => AcceptLegendaryKeys && PyrealsPerLegendaryKey > 0;

    /// <summary>Keys that pay <paramref name="pricePyreals"/> (rounded up; 0 when keys aren't taken).</summary>
    public long KeysFor(long pricePyreals)
    {
        if (pricePyreals <= 0 || !TakesKeys) return 0;
        return (pricePyreals + PyrealsPerLegendaryKey - 1) / PyrealsPerLegendaryKey;
    }

    /// <summary>MMD notes that pay <paramref name="pricePyreals"/> (rounded up; 0 when notes aren't taken).</summary>
    public long MmdFor(long pricePyreals)
    {
        if (pricePyreals <= 0 || !AcceptTradeNotes) return 0;
        return (pricePyreals + TradeShopCurrencyTable.PyrealsPerMmd - 1) / TradeShopCurrencyTable.PyrealsPerMmd;
    }

    /// <summary>The pyreal worth of what the shop takes in <paramref name="tally"/> (refused currencies count 0).</summary>
    public long AcceptedPyreals(TradeShopTally tally)
    {
        if (tally == null) return 0;
        long total = 0;
        if (AcceptPyreals) total = Add(total, tally.Pyreals);
        if (AcceptTradeNotes) total = Add(total, tally.NotePyreals);
        if (TakesKeys) total = Add(total, Mul(tally.LegendaryKeys, PyrealsPerLegendaryKey));
        return total;
    }

    /// <summary>True when <paramref name="tally"/> covers <paramref name="pricePyreals"/> (and every item in it is known).</summary>
    public bool Covers(long pricePyreals, TradeShopTally tally)
        => pricePyreals > 0 && tally != null && tally.Unknown == 0 && AcceptedPyreals(tally) >= pricePyreals;

    /// <summary>The ways to pay a listing: "1,000,000 py (pyreals or notes, 4 MMD) / 2 LK".</summary>
    public string FormatAlternatives(long pricePyreals)
    {
        if (pricePyreals <= 0) return "(no price)";
        var parts = new List<string>();
        if (AcceptPyreals || AcceptTradeNotes)
        {
            string how = AcceptPyreals && AcceptTradeNotes ? "pyreals or notes" : AcceptPyreals ? "pyreals" : "notes";
            string py = N(pricePyreals) + " py (" + how;
            long mmd = MmdFor(pricePyreals);
            if (AcceptTradeNotes && pricePyreals >= TradeShopCurrencyTable.PyrealsPerMmd && mmd > 0) py += ", " + N(mmd) + " MMD";
            parts.Add(py + ")");
        }
        long keys = KeysFor(pricePyreals);
        if (keys > 0) parts.Add(N(keys) + " LK");
        return parts.Count == 0 ? "(the shop takes no payment)" : string.Join(" / ", parts);
    }

    /// <summary>One line for !rates and the window.</summary>
    public string RatesHelpLine()
    {
        var take = new List<string>();
        if (AcceptPyreals) take.Add("pyreals");
        if (AcceptTradeNotes) take.Add("trade notes at face value (1 MMD = " + N(TradeShopCurrencyTable.PyrealsPerMmd) + " py)");
        if (TakesKeys) take.Add("single-use Legendary Keys (1 LK = " + N(PyrealsPerLegendaryKey) + " py)");
        return take.Count == 0 ? "The shop takes no payment right now." : "Takes " + string.Join(", ", take) + ".";
    }

    /// <summary>A copy (the window edits one, the command line another).</summary>
    public TradeShopRates Clone() => new()
    {
        PyrealsPerLegendaryKey = PyrealsPerLegendaryKey,
        AcceptPyreals = AcceptPyreals,
        AcceptTradeNotes = AcceptTradeNotes,
        AcceptLegendaryKeys = AcceptLegendaryKeys,
    };

    internal static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    private static long Add(long a, long b) => b > 0 && a > long.MaxValue - b ? long.MaxValue : a + Math.Max(0, b);

    private static long Mul(long a, long b)
    {
        if (a <= 0 || b <= 0) return 0;
        return a > long.MaxValue / b ? long.MaxValue : a * b;
    }
}

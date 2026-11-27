// Trade Shop: add up the payment on the buyer's side of the trade window, by WCID.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here: an item counts
// only through TradeShopCurrencyTable (WCID), never through its name or its Value property, and an
// item the client can't read yet (no WCID, or a currency stack with no stack size) is Unknown, which
// never counts as paid.
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>
/// One object as the shop sees it. <see cref="Known"/> false: the client has no WCID for it (yet).
/// <see cref="Stack"/>: the stack size; 0 = not stackable (one item), -1 = not readable.
/// </summary>
public readonly record struct TradeShopItem(uint Id, uint Wcid, string Name, int Stack, bool Known, bool Equipped = false);

/// <summary>What one side of the trade window adds up to.</summary>
public sealed class TradeShopTally
{
    public long Pyreals;
    /// <summary>Trade notes, at their WCID's face value.</summary>
    public long NotePyreals;
    public long LegendaryKeys;
    /// <summary>Items that are not payment (worth 0).</summary>
    public int OtherItems;
    /// <summary>Items the client can't read yet: the payment isn't known until this is 0.</summary>
    public int Unknown;
    /// <summary>Item count per kind, for the sale line ("3x Trade Note (250,000)").</summary>
    public readonly List<string> Lines = new();

    /// <summary>"1,000,000 py (pyreals 0, notes 1,000,000), 2 LK, 1 other item worth 0".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Pyreals > 0) parts.Add(TradeShopRates.N(Pyreals) + " pyreals");
        if (NotePyreals > 0) parts.Add(TradeShopRates.N(NotePyreals) + " py in trade notes");
        if (LegendaryKeys > 0) parts.Add(TradeShopRates.N(LegendaryKeys) + " Legendary Key" + (LegendaryKeys == 1 ? "" : "s"));
        if (OtherItems > 0) parts.Add(OtherItems + " other item" + (OtherItems == 1 ? "" : "s") + " (worth 0)");
        if (Unknown > 0) parts.Add(Unknown + " item" + (Unknown == 1 ? "" : "s") + " not read yet");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}

/// <summary>Adds up payment items by WCID.</summary>
public static class TradeShopPayment
{
    /// <summary>
    /// Tally <paramref name="itemIds"/>; <paramref name="lookup"/> reads one object (null: the client
    /// doesn't have it). Unknown objects and currency stacks without a readable size count as Unknown.
    /// </summary>
    public static TradeShopTally Tally(IEnumerable<uint> itemIds, Func<uint, TradeShopItem?> lookup)
    {
        var t = new TradeShopTally();
        if (itemIds == null) return t;
        foreach (uint id in itemIds)
        {
            if (id == 0) continue;
            TradeShopItem? read = lookup?.Invoke(id);
            if (read is not { } item || !item.Known || item.Wcid == 0)
            {
                t.Unknown++;
                continue;
            }
            TradeShopCurrency kind = TradeShopCurrencyTable.Classify(item.Wcid);
            switch (kind)
            {
                case TradeShopCurrency.Pyreals:
                    if (item.Stack <= 0) { t.Unknown++; break; }
                    t.Pyreals += item.Stack;
                    t.Lines.Add(N(item.Stack) + " pyreals");
                    break;
                case TradeShopCurrency.TradeNote:
                {
                    if (item.Stack <= 0) { t.Unknown++; break; }
                    long face = TradeShopCurrencyTable.NoteFaceValue(item.Wcid);
                    t.NotePyreals += face * item.Stack;
                    t.Lines.Add(item.Stack + "x Trade Note (" + N(face) + ")");
                    break;
                }
                case TradeShopCurrency.LegendaryKey:
                {
                    int n = Math.Max(1, item.Stack);
                    t.LegendaryKeys += n;
                    t.Lines.Add(n + "x Legendary Key");
                    break;
                }
                default:
                    t.OtherItems++;
                    t.Lines.Add((string.IsNullOrWhiteSpace(item.Name) ? "an item" : item.Name.Trim()) + " (not payment)");
                    break;
            }
        }
        return t;
    }

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
}

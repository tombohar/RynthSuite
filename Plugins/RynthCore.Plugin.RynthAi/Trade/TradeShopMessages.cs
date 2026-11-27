// Trade Shop: the lines it tells and advertises, and finding a listing from a buyer's selector.
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here. A price sheet is
// cut into tell-sized chunks and capped at MaxSheetChunks; the rest is one "and N more" line.
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RynthCore.Plugin.RynthAi.Trade;

public static class TradeShopMessages
{
    /// <summary>Longest line the shop sends (AC drops longer tells; "/tell Name, " needs room too).</summary>
    public const int MaxLineChars = 200;

    /// <summary>Most chunks one price sheet sends.</summary>
    public const int MaxSheetChunks = 4;

    public static string HelpLine()
        => "Trade Shop: !list (price sheet), !rates, !price <#slot|name>, !buy <#slot|name>. Example: !buy #2";

    public static string RatesLine(TradeShopRates? rates) => "Rates: " + (rates ?? new TradeShopRates()).RatesHelpLine();

    public static string SheetRow(int slot, TradeShopListing l, TradeShopRates? rates)
        => $"#{slot} {l.Label()}: {(rates ?? new TradeShopRates()).FormatAlternatives(l.PricePyreals)}";

    /// <summary>The price sheet in at most <see cref="MaxSheetChunks"/> chunks of at most <see cref="MaxLineChars"/>.</summary>
    public static List<string> PriceSheet(IReadOnlyList<TradeShopListing> listings, TradeShopRates? rates,
        bool includeRates, string? header = null, int maxChunks = MaxSheetChunks)
    {
        maxChunks = Math.Max(1, maxChunks);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(header)) parts.Add(Clip(header.Trim()));
        if (includeRates) parts.Add(Clip(RatesLine(rates)));
        if (listings == null || listings.Count == 0) parts.Add("Price sheet: (empty)");
        else
        {
            parts.Add($"Price sheet ({listings.Count}):");
            for (int i = 0; i < listings.Count; i++)
                if (listings[i] != null) parts.Add(Clip(SheetRow(i + 1, listings[i], rates)));
        }

        // Greedy chunks, remembering how many parts each holds.
        var chunks = new List<string>();
        var counts = new List<int>();
        var sb = new StringBuilder();
        int inChunk = 0;
        foreach (string part in parts)
        {
            if (sb.Length > 0 && sb.Length + 3 + part.Length > MaxLineChars)
            {
                chunks.Add(sb.ToString());
                counts.Add(inChunk);
                sb.Clear();
                inChunk = 0;
            }
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(part);
            inChunk++;
        }
        if (sb.Length > 0) { chunks.Add(sb.ToString()); counts.Add(inChunk); }
        if (chunks.Count <= maxChunks) return chunks;

        // Too long: the first maxChunks - 1 chunks, then one line saying how many rows weren't sent.
        int sent = 0;
        for (int i = 0; i < maxChunks - 1; i++) sent += counts[i];
        var capped = chunks.GetRange(0, maxChunks - 1);
        capped.Add($"...and {parts.Count - sent} more. Ask !price <name> for one item.");
        return capped;
    }

    /// <summary>Find a listing: #slot / slot:N / bare small number, a price (exact, else the nearest at or above), or a name.</summary>
    public static TradeShopListing? ResolveListing(IReadOnlyList<TradeShopListing> list, string? arg)
    {
        if (list == null || list.Count == 0) return null;
        var (slot, valuePy, text) = TradeShopTellParse.ParseSelector(arg);
        if (slot.HasValue)
            return slot.Value >= 1 && slot.Value <= list.Count ? list[slot.Value - 1] : null;
        if (valuePy.HasValue)
        {
            long v = valuePy.Value;
            return list.FirstOrDefault(l => l.PricePyreals == v)
                   ?? list.Where(l => l.PricePyreals >= v).OrderBy(l => l.PricePyreals).FirstOrDefault();
        }
        if (text.Length == 0) return null;
        return list.FirstOrDefault(l => string.Equals(l.Name, text, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(l => l.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>One line, no line breaks, at most <see cref="MaxLineChars"/>.</summary>
    public static string Clip(string? s)
    {
        string t = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return t.Length <= MaxLineChars ? t : t[..(MaxLineChars - 3)] + "...";
    }
}

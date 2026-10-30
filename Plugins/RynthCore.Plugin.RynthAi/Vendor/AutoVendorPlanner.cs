using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RynthCore.Loot;

namespace RynthCore.Plugin.RynthAi.Vendor;

// Pure planning for AutoVendor, a port of UtilityBelt's AutoVendor.DoVendoring /
// GetBuyItems / GetSellItems / ItemIsSafeToGetRidOf / AddItemToSellList. No host calls:
// the manager fills these views from the engine's vendor snapshot and the inventory,
// executes the plan, waits for the server, and plans again. One transaction per plan,
// buying before selling, exactly as UB does.

/// <summary>The open vendor (engine VendorInfo).</summary>
internal sealed class VendorView
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Vendor pays value x BuyRate when you sell.</summary>
    public float BuyRate { get; init; } = 1f;
    /// <summary>Vendor charges value x SellRate when you buy.</summary>
    public float SellRate { get; init; } = 1f;
    public uint ItemTypes { get; init; }
    public int MaxValue { get; init; }
    public bool UsesAltCurrency { get; init; }

    public bool Buys(uint itemType) => (ItemTypes & itemType) != 0;
}

/// <summary>One item on the vendor's list (engine VendorItem).</summary>
internal sealed class Listing
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public AcObjectClass Class { get; init; }
    public uint ItemType { get; init; }
    /// <summary>How many the vendor has; negative = unlimited.</summary>
    public int Amount { get; init; } = -1;
    public int StackSize { get; init; } = 1;
    public int MaxStackSize { get; init; } = 1;
    /// <summary>Value of the listed stack.</summary>
    public int Value { get; init; }
    /// <summary>Engine's per-unit price at this vendor (0 = unknown).</summary>
    public int UnitPrice { get; init; }
    /// <summary>Burden of the listed stack.</summary>
    public int Burden { get; init; }
    public bool NeedsContainerSlot { get; init; }

    public bool Unlimited => Amount < 0;
    public bool IsNote => Class == AcObjectClass.TradeNote || (ItemType & AutoVendorPlanner.ItemTypePromissoryNote) != 0;
    public int UnitValue => StackSize > 1 ? Value / StackSize : Value;
}

/// <summary>A vendor item the profile wants, with how many (int.MaxValue = all you can afford).</summary>
internal sealed class BuyWant
{
    public Listing Item { get; init; } = null!;
    public int Amount { get; init; }
    public string RuleName { get; init; } = string.Empty;
}

/// <summary>An inventory item that passed the safety checks and a Sell rule.</summary>
internal sealed class SellItem
{
    public uint Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public AcObjectClass Class { get; init; }
    public uint ItemType { get; init; }
    public int StackSize { get; init; } = 1;
    /// <summary>Value of the whole stack.</summary>
    public int Value { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public int SortSlot { get; init; }
    /// <summary>The item's max stack size (0 = unknown); a stack below it is a partial.</summary>
    public int MaxStack { get; init; }
    /// <summary>Weenie class id (0 = unknown): only the same weenie merges.</summary>
    public uint Wcid { get; init; }

    public bool IsNote => Class == AcObjectClass.TradeNote || (ItemType & AutoVendorPlanner.ItemTypePromissoryNote) != 0;
    public int UnitValue => StackSize > 1 ? Value / StackSize : Value;
}

/// <summary>Money and room, read fresh before each plan.</summary>
internal sealed class PackState
{
    /// <summary>Pyreals, or the vendor's alt currency.</summary>
    public long Funds { get; init; }
    /// <summary>Free item slots in the MAIN pack (UB: Weenie(player).FreeSpace).</summary>
    public int FreeMainSlots { get; init; }
    public int FreeContainerSlots { get; init; }
    public int PyrealStackSize { get; init; } = AutoVendorPlanner.DefaultPyrealStackSize;
}

internal enum PlanKind : byte
{
    Done = 0,
    Buy = 1,
    Sell = 2,
    Split = 3,
    Fatal = 4,
    Merge = 5,
}

internal readonly record struct BuyLine(Listing Item, int Amount);

internal sealed class VendorPlan
{
    public PlanKind Kind { get; init; }
    public List<BuyLine> Buys { get; init; } = new();
    public List<SellItem> Sells { get; init; } = new();
    /// <summary>Buy: estimated cost. Sell: estimated payout.</summary>
    public long Total { get; init; }
    public SellItem? SplitItem { get; init; }
    public int SplitAmount { get; init; }
    /// <summary>Merge: SplitItem goes onto MergeTarget.</summary>
    public SellItem? MergeTarget { get; init; }
    public string Message { get; init; } = string.Empty;

    public static VendorPlan Done() => new() { Kind = PlanKind.Done };
    public static VendorPlan Fatal(string msg) => new() { Kind = PlanKind.Fatal, Message = msg };
    public static VendorPlan Split(SellItem item, int amount, string why) =>
        new() { Kind = PlanKind.Split, SplitItem = item, SplitAmount = amount, Message = why };
    public static VendorPlan Merge(SellItem source, SellItem target, string why) =>
        new() { Kind = PlanKind.Merge, SplitItem = source, MergeTarget = target, Message = why };
}

/// <summary>What the manager read about an inventory item for UB's "safe to get rid of" checks.</summary>
internal sealed class SafetyFacts
{
    /// <summary>Has appraisal data: without it the checks below can't be trusted, so never sell.</summary>
    public bool Appraised { get; init; }
    public AcObjectClass Class { get; init; }
    public int Value { get; init; }
    /// <summary>ACE IsSellable (69); null = unknown (treated as sellable, like UB's default).</summary>
    public bool? CanBeSold { get; init; }
    public bool IsRare { get; init; }
    public bool InMainPack { get; init; }
    public bool Equipped { get; init; }
    public int Attuned { get; init; }
    public int Bonded { get; init; }
    public bool Retained { get; init; }
    public int TimesTinkered { get; init; }
    public int Imbued { get; init; }
    public string Inscription { get; init; } = string.Empty;
    /// <summary>A pack or focus (holds items).</summary>
    public bool HoldsItems { get; init; }
}

internal static class AutoVendorPlanner
{
    public const int MaxVendorBuyCount = 5000;      // UB MAX_VENDOR_BUY_COUNT
    public const int MaxSellLines = 99;             // UB: GDLE rejects 100+ items per transaction
    public const int DefaultPyrealStackSize = 25000;
    public const uint PyrealWcid = 273;
    public const uint ItemTypePromissoryNote = 0x00040000;

    // UB ShopItemListTypes: shop category names for the test-mode report.
    private static readonly (uint Bit, string Name)[] ShopCategories =
    {
        (0x00000001, "Weapons"), (0x00000002, "Armor"), (0x00000004, "Clothing"), (0x00000008, "Jewelry"),
        (0x00000010, "Miscellaneous"), (0x00000020, "Food"), (0x00000080, "Miscellaneous"), (0x00000100, "Weapons"),
        (0x00000200, "Containers"), (0x00000400, "Miscellaneous"), (0x00000800, "Gems"), (0x00001000, "Spell Components"),
        (0x00002000, "Books, Paper"), (0x00004000, "Keys, Tools"), (0x00008000, "Magic Items"), (0x00040000, "Trade Notes"),
        (0x00080000, "Mana Stones"), (0x00100000, "Services"), (0x00400000, "Cooking Items"), (0x00800000, "Alchemical Items"),
        (0x01000000, "Fletching Items"), (0x04000000, "Alchemical Items"), (0x08000000, "Fletching Items"), (0x20000000, "Keys, Tools"),
    };

    public static string CategoryName(uint itemType)
    {
        foreach (var (bit, name) in ShopCategories)
            if ((itemType & bit) != 0) return name;
        return $"Unknown Category 0x{itemType:X8}";
    }

    // ── Prices (ACE Vendor.GetSellCost / GetBuyCost, as the engine checks them) ──────────

    /// <summary>What you pay for one created stack worth <paramref name="stackValue"/>.</summary>
    public static long PlayerPays(VendorView v, bool isNote, long stackValue)
    {
        float rate = isNote ? 1.15f : v.SellRate;
        float product = rate * stackValue;
        return Math.Max(1L, (long)Math.Ceiling(product - 0.1));
    }

    /// <summary>What the vendor pays for one item (whole stack) worth <paramref name="value"/>.</summary>
    public static long VendorPays(VendorView v, bool isNote, long value)
    {
        float rate = isNote ? 1.0f : v.BuyRate;
        float product = rate * value;
        return Math.Max(1L, (long)Math.Floor(product + 0.1));
    }

    /// <summary>Per-unit price: the engine's figure, else UB's (int)(Value/Stack) x rate rounded like ACE.</summary>
    public static long UnitPrice(VendorView v, Listing item)
    {
        if (item.UnitPrice > 0) return item.UnitPrice;
        if (item.UnitValue <= 0) return 0;
        return PlayerPays(v, item.IsNote, item.UnitValue);
    }

    /// <summary>The engine's EstimateBuy: stackables in full stacks, limited listings at least the whole listing.</summary>
    public static long EstimateCost(VendorView v, Listing item, int amount)
    {
        if (amount <= 0) return 0;
        long unitValue = Math.Max(0, item.UnitValue);
        long cost;
        if (item.MaxStackSize > 1)
        {
            long max = item.MaxStackSize;
            long full = amount / max;
            long rem = amount % max;
            cost = full * PlayerPays(v, item.IsNote, unitValue * max) + (rem > 0 ? PlayerPays(v, item.IsNote, unitValue * rem) : 0);
        }
        else
        {
            cost = amount * PlayerPays(v, item.IsNote, unitValue);
        }
        if (!item.Unlimited)
            cost = Math.Max(cost, PlayerPays(v, item.IsNote, Math.Max(0, item.Value)));
        return cost;
    }

    /// <summary>Largest amount in [0, max] whose EstimateCost fits <paramref name="money"/> (cost only grows with amount).</summary>
    public static int MostAffordable(VendorView v, Listing item, int max, long money)
    {
        int lo = 0, hi = Math.Max(0, max);
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (EstimateCost(v, item, mid) <= money) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    public static int SlotsFor(Listing item, int amount)
    {
        int max = Math.Max(1, item.MaxStackSize);
        return (int)((amount + (long)max - 1) / max);
    }

    /// <summary>UB PyrealsWillFitInMainPack: always leaves one main-pack slot free.</summary>
    public static bool PyrealsFit(PackState p, double amount)
    {
        int stack = Math.Max(1, p.PyrealStackSize);
        return p.FreeMainSlots - Math.Ceiling(amount / stack) > 0;
    }

    // ── Safety (UB ItemIsSafeToGetRidOf + Util.IsItemSafeToGetRidOf, stricter) ──────────

    /// <summary>Null when the item may be sold; otherwise the reason it never will be.</summary>
    public static string? UnsafeReason(SafetyFacts f, bool onlyFromMainPack)
    {
        if (!f.Appraised) return "not identified";
        if (f.Equipped) return "equipped";
        if (f.Class == AcObjectClass.Money) return "money";
        if (f.Class == AcObjectClass.Container || f.HoldsItems) return "container";
        if (f.Class == AcObjectClass.Foci) return "focus";
        if (f.Value <= 0) return "no value";
        if (f.CanBeSold == false) return "not sellable";
        if (f.IsRare) return "rare";
        if (onlyFromMainPack && !f.InMainPack) return "not in main pack";
        if (f.Attuned != 0) return "attuned";
        // Not Bonded: on ACE it only decides what a death keeps (PropertyInt.Bonded, Player_Death);
        // it never stops a sale. Peas, trade notes and salvage bags are bonded so a death keeps
        // them, and AutoVendor held them all back (2026-10-07). Quest items are not sellable anyway.
        if (f.Retained) return "retained";
        if (f.TimesTinkered > 0) return "tinkered";
        if (f.Imbued != 0) return "imbued";
        if (!string.IsNullOrEmpty(f.Inscription)) return "inscribed";
        return null;
    }

    /// <summary>
    /// UB VendorInfo.WillBuyItem. Trade notes skip the value cap like UB, but the vendor must
    /// still list the note item type: the engine refuses anything outside the vendor's types.
    /// </summary>
    public static bool VendorWillBuy(VendorView v, uint itemType, bool isNote, int unitValue)
    {
        if (!v.Buys(itemType)) return false;
        if (isNote) return true;
        return v.MaxValue > 0 && unitValue <= v.MaxValue;
    }

    // ── Buy list (UB GetBuyItems) ────────────────────────────────────────────

    /// <summary>
    /// Keep # buys (count - have), where have is the inventory count by exact name (containers:
    /// by object class, and only the first container rule). Keep buys int.MaxValue. Sorted
    /// trade notes last, then cheapest unit price first.
    /// </summary>
    public static List<BuyWant> BuildBuyWants(
        VendorView v,
        IEnumerable<(Listing Item, BuyDecision Decision)> judged,
        Func<string, int> countByName,
        Func<AcObjectClass, int> countByClass)
    {
        var list = judged.ToList();
        var wants = new List<BuyWant>();
        bool buyingContainers = false;

        foreach (var (item, d) in list)
        {
            if (d.Kind != BuyKind.KeepUpTo) continue;
            if (item.Class == AcObjectClass.Container)
            {
                int have = countByClass(AcObjectClass.Container);
                if (!buyingContainers && d.KeepCount > have)
                {
                    buyingContainers = true;
                    wants.Add(new BuyWant { Item = item, Amount = d.KeepCount - have, RuleName = d.RuleName });
                }
            }
            else
            {
                int have = countByName(item.Name);
                if (d.KeepCount > have)
                    wants.Add(new BuyWant { Item = item, Amount = d.KeepCount - have, RuleName = d.RuleName });
            }
        }

        foreach (var (item, d) in list)
            if (d.Kind == BuyKind.Keep)
                wants.Add(new BuyWant { Item = item, Amount = int.MaxValue, RuleName = d.RuleName });

        return wants
            .OrderBy(w => w.Item.IsNote ? 1 : 0)
            .ThenBy(w => UnitPrice(v, w.Item))
            .ToList();
    }

    /// <summary>UB GetSellItems sort: trade notes last, then cheapest stack payout first.</summary>
    public static List<SellItem> SortSells(VendorView v, IEnumerable<SellItem> items) =>
        items.OrderBy(s => s.IsNote ? 1 : 0)
             .ThenBy(s => VendorPays(v, s.IsNote, s.Value))
             .ToList();

    // ── One transaction (UB DoVendoring) ─────────────────────────────────────

    /// <param name="wants">From BuildBuyWants (already sorted).</param>
    /// <param name="sells">From SortSells.</param>
    public static VendorPlan PlanNext(VendorView v, IReadOnlyList<BuyWant> wants, IReadOnlyList<SellItem> sells, PackState p)
    {
        // ── Buy first ──
        var buys = new List<BuyLine>();
        long totalCost = 0;
        int totalSlots = 0, totalContainerSlots = 0;

        // UB re-orders by the listing's raw Value here (a stable sort over the price order).
        foreach (var want in wants.OrderBy(w => w.Item.Value))
        {
            Listing item = want.Item;
            long price = UnitPrice(v, item);
            if (price <= 0)
                return VendorPlan.Fatal(string.Format(CultureInfo.InvariantCulture,
                    "AutoVendor Fatal - No vendor price found while adding {0:n0}x {1}[0x{2:X8}] Value: {3:n}",
                    want.Amount, item.Name, item.Id, (double)price));

            bool fits;
            if (item.NeedsContainerSlot)
                fits = p.FreeContainerSlots - totalContainerSlots > 0;
            else
                fits = p.FreeMainSlots - totalSlots > (item.IsNote ? 0 : 1);

            if (totalCost + price <= p.Funds && fits)
            {
                long affordable = (p.Funds - totalCost) / price;
                long count = Math.Min(affordable, want.Amount);
                count = Math.Min(count, MaxVendorBuyCount);
                if (!item.Unlimited) count = Math.Min(count, item.Amount);

                if (item.NeedsContainerSlot)
                {
                    count = Math.Min(count, p.FreeContainerSlots - totalContainerSlots);
                }
                else if (p.FreeMainSlots < SlotsFor(item, (int)count) + totalSlots)
                {
                    // UB: limit to what fits, keeping two slots spare (never below zero).
                    long fit = (long)Math.Max(1, item.MaxStackSize) * Math.Max(0, (p.FreeMainSlots - 2) - totalSlots);
                    count = Math.Min(count, fit);
                }

                // The engine's exact cost can exceed the per-unit estimate (a limited listing
                // costs at least the whole listing): take the most that still fits the money.
                if (count > 0 && totalCost + EstimateCost(v, item, (int)count) > p.Funds)
                    count = MostAffordable(v, item, (int)count, p.Funds - totalCost);
                if (count <= 0)
                    break;

                int n = (int)count;
                buys.Add(new BuyLine(item, n));
                if (item.NeedsContainerSlot) totalContainerSlots += n;
                else totalSlots += SlotsFor(item, n);
                totalCost += EstimateCost(v, item, n);

                if (want.Amount > n)
                    break;   // couldn't take all of it: don't reach for the next item
            }
            else if (buys.Count > 0)
            {
                break;
            }
        }

        if (buys.Count > 0)
            return new VendorPlan { Kind = PlanKind.Buy, Buys = buys, Total = totalCost };

        // ── Then sell ──
        // nextBuy: the item we want but couldn't afford (notes are sold only to pay for it).
        // An alt-currency vendor isn't paid in pyreals, so selling can't fund its items.
        Listing? nextBuy = !v.UsesAltCurrency && wants.Count > 0 ? wants[0].Item : null;

        var chosen = new List<SellItem>();
        long totalValue = 0;
        for (int i = 0; i < sells.Count && chosen.Count < MaxSellLines; i++)
        {
            SellItem item = sells[i];
            int stack = Math.Max(1, item.StackSize);
            long stackPayout = VendorPays(v, item.IsNote, item.Value);
            double unit = (double)stackPayout / stack;

            // Never sell notes to buy notes, and never sell notes when there's nothing to buy.
            if (item.IsNote && (nextBuy == null || nextBuy.IsNote))
                break;

            // Selling notes to afford something: one note at a time, and only on its own.
            if (nextBuy != null && item.IsNote)
            {
                if (chosen.Count > 0) break;
                if (!PyrealsFit(p, unit))
                    return VendorPlan.Fatal($"AutoVendor Fatal - No inventory room to sell {item.Name}");

                SellItem? single = null;
                foreach (var s in sells)
                    if (s.IsNote && s.StackSize <= 1 && string.Equals(s.Name, item.Name, StringComparison.Ordinal))
                    { single = s; break; }
                if (single != null)
                    return new VendorPlan { Kind = PlanKind.Sell, Sells = new List<SellItem> { single }, Total = VendorPays(v, true, single.Value) };

                return SplitClean(v, sells, item, 1, $"one {item.Name} to afford {nextBuy.Name}");
            }

            // Can't sell the whole stack? Split off what the pyreals will fit.
            if (!PyrealsFit(p, totalValue + stackPayout))
            {
                if (chosen.Count < 1)
                {
                    int newStack = unit > 0 ? (int)Math.Floor((p.FreeMainSlots - 2) * (double)Math.Max(1, p.PyrealStackSize) / unit) : 0;
                    newStack = Math.Min(newStack, stack - 1);
                    if (newStack > 0)
                        return SplitClean(v, sells, item, newStack, "the pyreals for the whole stack won't fit");
                    return VendorPlan.Fatal($"AutoVendor Fatal - No inventory room to sell {item.Name}");
                }
                break;
            }

            chosen.Add(item);
            totalValue += stackPayout;
        }

        if (chosen.Count > 0)
            return new VendorPlan { Kind = PlanKind.Sell, Sells = chosen, Total = totalValue };

        return VendorPlan.Done();
    }

    /// <summary>
    /// A split lands on another partial stack of the same item when there is one, instead of making
    /// a new stack (the stack split from doesn't count), so the split-off amount never shows up to be
    /// sold. Drakkon's Gold Peas looped split-wait-split for good once two partial stacks existed
    /// (2026-10-19). So: first merge partials down to one; with one partial left, sell it whole if it
    /// fits, else split from it (nothing else partial for the new stack to join); with none, split as
    /// asked. An unknown max stack size keeps the old plain split.
    /// </summary>
    internal static VendorPlan SplitClean(VendorView v, IReadOnlyList<SellItem> sells, SellItem item, int amount, string why)
    {
        if (item.MaxStack <= 1)
            return VendorPlan.Split(item, amount, why);

        var partials = sells
            .Where(s => SameItem(s, item) && s.StackSize > 0 && s.StackSize < item.MaxStack)
            .OrderByDescending(s => s.StackSize)
            .ToList();

        // A partial that fits is sold whole first: the stack a split just made, or any leftover. (Merging
        // it back instead looped split-merge-split three times a second, 2026-10-19.)
        SellItem? fits = partials.FirstOrDefault(s => s.StackSize <= amount);
        if (fits != null)
            return new VendorPlan { Kind = PlanKind.Sell, Sells = new List<SellItem> { fits }, Total = VendorPays(v, fits.IsNote, fits.Value) };

        // Every partial is too big to sell whole: merge them down to one before splitting.
        if (partials.Count >= 2)
            return VendorPlan.Merge(partials[^1], partials[0], $"one partial {item.Name} stack, so the split makes a new stack");

        if (partials.Count == 1 && partials[0].Id != item.Id)
            return VendorPlan.Split(partials[0], amount, why);

        return VendorPlan.Split(item, amount, why);
    }

    /// <summary>The unit value carrying the most units (ties: the higher value, the safe side for a vendor's max).</summary>
    internal static int MostCarriedUnit(IReadOnlyDictionary<int, long> unitsByValue)
    {
        int best = 0;
        long bestUnits = -1;
        foreach (var (unit, units) in unitsByValue)
            if (units > bestUnits || (units == bestUnits && unit > best))
            {
                best = unit;
                bestUnits = units;
            }
        return best;
    }

    private static bool SameItem(SellItem a, SellItem b) =>
        a.Wcid != 0 && b.Wcid != 0 ? a.Wcid == b.Wcid : string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    // ── /ub vendor addsell (UB AddItemToSellList) ────────────────────────────

    /// <summary>
    /// Whole stacks that fit in the remaining count are taken; if units are still missing,
    /// the smallest stack bigger than what's missing is returned to be split.
    /// </summary>
    public static (List<SellItem> Whole, int Missing, SellItem? SplitFrom) PlanAddSell(IReadOnlyList<SellItem> matches, int count)
    {
        var whole = new List<SellItem>();
        int needed = Math.Max(0, count);
        SellItem? oversized = null;
        foreach (var m in matches)
        {
            if (needed == 0) break;
            int stack = Math.Max(1, m.StackSize);
            if (stack <= needed)
            {
                whole.Add(m);
                needed -= stack;
            }
            else if (oversized == null || oversized.StackSize > stack)
            {
                oversized = m;
            }
        }
        return (whole, needed, needed > 0 ? oversized : null);
    }

    // ── Shared parsing ───────────────────────────────────────────────────────

    /// <summary>"[count] name" as UB's addbuy/addsell read it; count defaults to 1.</summary>
    public static (int Count, string Name) ParseCountAndName(string args)
    {
        string s = (args ?? string.Empty).Trim();
        int sp = s.IndexOf(' ');
        if (sp > 0 && int.TryParse(s.Substring(0, sp), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return (n, s.Substring(sp + 1).Trim());
        return (1, s);
    }
}

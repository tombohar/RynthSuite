// ============================================================================
//  RynthDefective - Model/Quantity.cs
//  The quantity box's rules (2026-10-30): how many of a stack a split or a
//  give takes, how many of a vendor's item a buy asks for, what that costs and
//  why it can't go. Pure (no host), so RynthDefective.Tests checks every rule.
//
//  Retail AC keeps this number in its toolbar: the stack slider and the number
//  beside the selected item's name (the client's split_amount global, which a
//  drag and the vendor's buy list read). RynthCore's "Hide retail toolbar"
//  hides it with the rest of the toolbar, and the plugin API can't read or set
//  it (API 92), so the box keeps a number of its own and acts through the
//  engine's calls: SplitStackInternal, GiveObjectTo and VendorBuy.
//
//  The counts:
//    split  takes 1 .. stack-1 (one always stays: the whole stack is a move);
//           "max" is stack-1, "half" is half the stack.
//    give   hands over 1 .. the whole stack; "max" is the whole stack.
//    buy    asks for exactly the number; "max" is the most the vendor has, you
//           can pay for, and your packs and burden take (VendorQuote.MaxBuy).
//    box    a typed number stays as typed (1 .. MaxAmount); "max" and "half"
//           are of what is selected: the whole stack, or the most you can buy.
//
//  A server's bank (2026-10-31): some servers pay a pyreal vendor from the
//  bank when the packs are short. The active server's plugin says so in its
//  profile values (VendorBank.Read; RynthDefective names no server):
//    no bank             the pack's pyreals, as before;
//    a bank that's read  pack + bank pay ("price 1,200; pack 300 + bank 5,000"),
//                        and "max" counts both;
//    a bank that isn't   money isn't held against a buy: it goes, and the
//                        server decides ("the server's bank may cover it");
//                        "max" is the vendor's stock, your room and burden.
// ============================================================================

using System;
using System.Globalization;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective.Model;

internal enum QtyWord { Number, Max, Half }

/// <summary>A quantity as typed: a number, "max" or "half".</summary>
internal readonly record struct QtyAmount(QtyWord Word, int Number = 0)
{
    public static QtyAmount Of(int n) => new(QtyWord.Number, n);
    public static readonly QtyAmount Max = new(QtyWord.Max);
    public static readonly QtyAmount Half = new(QtyWord.Half);

    public override string ToString() => Word switch
    {
        QtyWord.Max => "max",
        QtyWord.Half => "half",
        _ => Number.ToString(CultureInfo.InvariantCulture),
    };
}

internal static class QuantityRules
{
    /// <summary>The most one request carries: the engine's vendor limit (an ItemProfile packs 24 bits).</summary>
    public const int MaxAmount = 0x7FFFFF;

    public const string Usage = "a number (25, 1,000), max or half";

    /// <summary>"25", "1,000", "max" / "all", "half" / "1/2". False with the reason and the usage.</summary>
    public static bool TryParse(string? text, out QtyAmount amount, out string why)
    {
        amount = default;
        why = string.Empty;
        string t = (text ?? string.Empty).Trim();
        switch (t.ToLowerInvariant())
        {
            case "max": case "all": case "most":
                amount = QtyAmount.Max;
                return true;
            case "half": case "1/2":
                amount = QtyAmount.Half;
                return true;
        }
        string digits = t.Replace(",", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (digits.Length > 0 && (char.IsDigit(digits[0]) || digits[0] == '-' || digits[0] == '+')
            && long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n))
        {
            if (n < 1) { why = $"The quantity is at least 1 (not {t})."; return false; }
            if (n > MaxAmount) { why = $"{t} is more than one request can carry (at most {MaxAmount.ToString("N0", CultureInfo.InvariantCulture)})."; return false; }
            amount = QtyAmount.Of((int)n);
            return true;
        }
        why = t.Length == 0 ? $"How many? {Usage}." : $"'{t}' isn't a quantity: {Usage}.";
        return false;
    }

    /// <summary>Whether a word reads as a quantity (a number of any size, or max / half), so "25 into Sack" splits off the 25.</summary>
    public static bool LooksLikeAmount(string word)
    {
        string w = word.Trim().ToLowerInvariant();
        if (w is "max" or "all" or "most" or "half" or "1/2") return true;
        return w.Length > 0 && (char.IsDigit(w[0]) || ((w[0] == '-' || w[0] == '+') && w.Length > 1 && char.IsDigit(w[1])));
    }

    /// <summary>
    /// The arguments of split / give / buy: an optional quantity first, then the rest ("into Sack", an
    /// item name). False only when the first word reads as a quantity and is a wrong one ("0", "-3").
    /// </summary>
    public static bool TryParseLeading(string? args, out QtyAmount? amount, out string rest, out string why)
    {
        amount = null;
        why = string.Empty;
        string a = (args ?? string.Empty).Trim();
        int sp = a.IndexOf(' ');
        string first = sp < 0 ? a : a[..sp];
        rest = sp < 0 ? string.Empty : a[(sp + 1)..].Trim();
        if (first.Length == 0 || !LooksLikeAmount(first))
        {
            rest = a;
            return true;
        }
        if (!TryParse(first, out QtyAmount q, out why)) return false;
        amount = q;
        return true;
    }

    /// <summary><paramref name="n"/> within 1 .. <paramref name="max"/> (1 when max is below 1).</summary>
    public static int Clamp(int n, int max) => max < 1 ? 1 : Math.Clamp(n, 1, max);

    /// <summary>Half of <paramref name="of"/>, at least 1.</summary>
    public static int HalfOf(int of) => Math.Max(1, of / 2);

    /// <summary>How many a split takes from a stack of <paramref name="stack"/>: at least one stays; 0 = not a stack.</summary>
    public static int SplitCount(int n, int stack) => stack < 2 ? 0 : Clamp(n, stack - 1);

    /// <summary>How many a give hands over: up to the whole stack; 0 = nothing there.</summary>
    public static int GiveCount(int n, int stack) => stack < 1 ? 0 : Clamp(n, stack);

    /// <summary>A split's count from what was typed (or the box's number): "max" stack-1, "half" half the stack.</summary>
    public static int ForSplit(QtyAmount a, int stack) => stack < 2 ? 0 : a.Word switch
    {
        QtyWord.Max => stack - 1,
        QtyWord.Half => SplitCount(HalfOf(stack), stack),
        _ => SplitCount(a.Number, stack),
    };

    /// <summary>A give's count: "max" the whole stack, "half" half of it.</summary>
    public static int ForGive(QtyAmount a, int stack) => stack < 1 ? 0 : a.Word switch
    {
        QtyWord.Max => stack,
        QtyWord.Half => HalfOf(stack),
        _ => GiveCount(a.Number, stack),
    };

    /// <summary>
    /// The box's number after /rdef qty or a button: a typed number as typed (1 .. MaxAmount); "max"
    /// and "half" of <paramref name="max"/> (the whole stack, or the most you can buy). 0 when max or
    /// half has nothing to be of.
    /// </summary>
    public static int ForBox(QtyAmount a, int max) => a.Word switch
    {
        QtyWord.Max => max >= 1 ? Math.Min(max, MaxAmount) : 0,
        QtyWord.Half => max >= 1 ? HalfOf(Math.Min(max, MaxAmount)) : 0,
        _ => Math.Clamp(a.Number, 1, MaxAmount),
    };

    /// <summary>A buy's count: exactly the number typed (VendorQuote.Refusal says when it can't go); "max" the most you can buy.</summary>
    public static int ForBuy(QtyAmount a, int maxBuy) => a.Word switch
    {
        QtyWord.Max => Math.Max(0, maxBuy),
        QtyWord.Half => maxBuy >= 1 ? HalfOf(maxBuy) : 0,
        _ => a.Number,
    };

    public static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>
/// The active server's bank, as its plugin says in its profile values: whether it pays a pyreal vendor
/// what the packs are short of (RynthServerProfile.ValueVendorBank), and the pyreals it can add
/// (ValueVendorBankPyreals; -1 = the balance can't be read). <see cref="None"/>: no bank (or no server
/// plugin saying so), and a buy counts the pack only.
/// </summary>
internal readonly record struct VendorBank(bool Pays, long Pyreals = -1)
{
    /// <summary>The most a bank is taken to hold (anything above reads as this): far past any server's, and exact in the profile's numbers.</summary>
    public const long MaxPyreals = 1_000_000_000_000_000;

    public static readonly VendorBank None = new(false);

    /// <summary>The bank's balance is read.</summary>
    public bool Known => Pays && Pyreals >= 0;

    /// <summary>A profile JSON's bank values; <see cref="None"/> without "vendorBank" above 0 (or for unusable text). Pure.</summary>
    public static VendorBank Read(string? profileJson)
    {
        double pays = ServerProfileView.Value(profileJson, RynthServerProfile.ValueVendorBank, 0);
        if (!(pays > 0)) return None;
        double p = ServerProfileView.Value(profileJson, RynthServerProfile.ValueVendorBankPyreals, -1);
        long pyreals = p >= 0 ? (long)Math.Floor(Math.Min(p, MaxPyreals)) : -1;
        return new VendorBank(true, pyreals);
    }
}

/// <summary>
/// One vendor item and what the player has: the box's price, its "max" and why a buy can't go. The
/// rules are ACE's, as the engine's VendorTrade.EstimateBuy applies them (the engine checks again
/// before it sends): a stackable item comes in stacks of MaxStack, each priced on its own
/// (max(1, ceil(value x rate - 0.1)), promissory notes at 1.15), anything else one per slot; a
/// limited listing (usually what another player sold, handed over whole) costs at least the whole
/// listing. Unknown funds, slots or burden (-1) aren't held against the buy. A server's bank that
/// pays pyreal vendors (<see cref="BankPays"/>) adds its balance to the pack's, or, when the balance
/// can't be read, leaves the money to the server.
/// </summary>
internal sealed record VendorQuote
{
    public const uint ItemTypePromissoryNote = 0x00040000;

    public string Name { get; init; } = string.Empty;
    public uint ItemType { get; init; }
    public int UnitValue { get; init; }
    /// <summary>What one costs at this vendor (the engine's server-rounded figure; shown, not summed).</summary>
    public int UnitPrice { get; init; }
    public int ListingValue { get; init; }
    public int UnitBurden { get; init; }
    public int ListingBurden { get; init; }
    /// <summary>The largest stack (above 0 = stackable, ACE's rule).</summary>
    public int MaxStack { get; init; }
    public bool Unlimited { get; init; }
    /// <summary>How many the vendor has (a limited listing).</summary>
    public int Stock { get; init; }
    public float SellRate { get; init; } = 1f;
    public bool NeedsContainerSlot { get; init; }
    public string Currency { get; init; } = "pyreals";
    /// <summary>Pyreals (or the vendor's own currency) you have; -1 = unknown.</summary>
    public long Funds { get; init; } = -1;
    public int FreeItemSlots { get; init; } = -1;
    public int FreeContainerSlots { get; init; } = -1;
    public long FreeBurden { get; init; } = -1;
    /// <summary>The server's bank pays this (pyreal) vendor what the pack is short of.</summary>
    public bool BankPays { get; init; }
    /// <summary>The pyreals the bank can add (with <see cref="BankPays"/>); -1 = its balance can't be read.</summary>
    public long BankFunds { get; init; } = -1;

    public bool Stackable => MaxStack > 0;

    /// <summary>A bank pays, and its balance is read: pack + bank count.</summary>
    public bool BankKnown => BankPays && BankFunds >= 0;

    /// <summary>A bank pays, but its balance can't be read: the server decides about the money.</summary>
    public bool BankUnknown => BankPays && BankFunds < 0;

    /// <summary>What a buy may spend: the pack, plus a bank that's read; -1 = money isn't held against a buy (unknown, or an unread bank).</summary>
    public long Spendable => Funds < 0 || BankUnknown ? -1 : BankKnown ? Funds + Math.Min(BankFunds, VendorBank.MaxPyreals) : Funds;

    /// <summary>From the engine's vendor snapshot (API v67), what the player has, and the server's bank (pyreal vendors only).</summary>
    public static VendorQuote From(VendorInfo info, VendorItem item, int freeItemSlots, int freeContainerSlots, long freeBurden,
        VendorBank bank = default)
    {
        int stack = Math.Max(1, item.StackSize);
        bool alt = info.UsesAltCurrency;
        bool bankPays = !alt && bank.Pays;
        return new VendorQuote
        {
            Name = item.Name,
            ItemType = item.ItemType,
            UnitValue = item.UnitValue,
            UnitPrice = item.UnitPrice,
            ListingValue = item.Value,
            UnitBurden = stack > 1 ? item.Burden / stack : item.Burden,
            ListingBurden = item.Burden,
            MaxStack = item.MaxStackSize,
            Unlimited = item.Unlimited || item.Amount < 0,
            Stock = Math.Max(0, item.Amount),
            SellRate = info.SellRate,
            NeedsContainerSlot = item.NeedsContainerSlot,
            Currency = alt ? (info.AltCurrencyName.Length > 0 ? info.AltCurrencyName : "the vendor's currency") : "pyreals",
            Funds = alt ? info.AltCurrencyHave : info.PlayerCoins,
            FreeItemSlots = freeItemSlots,
            FreeContainerSlots = freeContainerSlots,
            FreeBurden = freeBurden,
            BankPays = bankPays,
            BankFunds = bankPays ? bank.Pyreals : -1,
        };
    }

    /// <summary>What the player pays for one created stack worth <paramref name="stackValue"/> (ACE Vendor.GetSellCost).</summary>
    public long PaysForStack(long stackValue)
    {
        float rate = ItemType == ItemTypePromissoryNote ? 1.15f : SellRate;
        float product = rate * stackValue;
        return Math.Max(1L, (long)Math.Ceiling(product - 0.1));
    }

    /// <summary>Cost, pack slots and burden of buying <paramref name="n"/>.</summary>
    public void Estimate(int n, out long cost, out long slots, out long burden)
    {
        long unitValue = Math.Max(0, UnitValue), unitBurden = Math.Max(0, UnitBurden);
        if (n < 1) { cost = slots = burden = 0; return; }
        if (Stackable)
        {
            long max = MaxStack, full = n / max, rem = n % max;
            cost = full * PaysForStack(unitValue * max) + (rem > 0 ? PaysForStack(unitValue * rem) : 0);
            slots = full + (rem > 0 ? 1 : 0);
        }
        else
        {
            cost = n * PaysForStack(unitValue);
            slots = n;
        }
        burden = n * unitBurden;
        if (!Unlimited)
        {
            cost = Math.Max(cost, PaysForStack(Math.Max(0, ListingValue)));
            slots = Math.Max(slots, 1);
            burden = Math.Max(burden, Math.Max(0, ListingBurden));
        }
    }

    public long Cost(int n)
    {
        Estimate(n, out long cost, out _, out _);
        return cost;
    }

    private int FreeSlots => NeedsContainerSlot ? FreeContainerSlots : FreeItemSlots;

    /// <summary>Money, room and burden allow <paramref name="n"/> (the vendor's stock aside).</summary>
    private bool Fits(int n)
    {
        Estimate(n, out long cost, out long slots, out long burden);
        long spend = Spendable;
        if (spend >= 0 && cost > spend) return false;
        if (FreeSlots >= 0 && slots > FreeSlots) return false;
        if (FreeBurden >= 0 && burden > FreeBurden) return false;
        return true;
    }

    /// <summary>
    /// The most you can buy now: what the vendor has, you can pay for, and your packs and burden take
    /// (cost, slots and burden only grow with the count, so a halving search finds it). With nothing
    /// known to limit an unlimited item, one full stack (one, for an item that doesn't stack). 0 = not even one.
    /// With a bank: pack + bank pay when the bank is read; an unread bank leaves only stock, room and burden.
    /// </summary>
    public int MaxBuy()
    {
        int cap = Unlimited ? QuantityRules.MaxAmount : Math.Min(Stock, QuantityRules.MaxAmount);
        if (Unlimited && Spendable < 0 && FreeSlots < 0 && FreeBurden < 0) cap = Stackable ? Math.Max(1, MaxStack) : 1;
        if (cap < 1 || !Fits(1)) return 0;
        int lo = 1, hi = cap;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (Fits(mid)) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Why buying <paramref name="n"/> can't go, or null when it can (as far as the plugin can tell).</summary>
    public string? Refusal(int n)
    {
        if (n < 1) return "The quantity is at least 1.";
        if (n > QuantityRules.MaxAmount) return $"{QuantityRules.N(n)} is more than one buy can carry (at most {QuantityRules.N(QuantityRules.MaxAmount)}).";
        if (!Unlimited && n > Stock)
            return Stock < 1 ? $"The vendor has no {Name} left." : $"The vendor has only {QuantityRules.N(Stock)} {Name}.";
        Estimate(n, out long cost, out long slots, out long burden);
        string what = $"{QuantityRules.N(n)} {Name}";
        long spend = Spendable;
        if (spend >= 0 && cost > spend)
            return BankKnown
                ? $"Not enough {Currency}: {what} cost {QuantityRules.N(cost)}, you have {QuantityRules.N(Funds)} in your pack + {QuantityRules.N(BankFunds)} in the bank{CanBuy()}."
                : $"Not enough {Currency}: {what} cost {QuantityRules.N(cost)}, you have {QuantityRules.N(Funds)}{CanBuy()}.";
        int free = FreeSlots;
        if (free >= 0 && slots > free)
        {
            if (NeedsContainerSlot)
                return free == 0 ? $"No room for another pack: {what} need a container slot of their own."
                                 : $"Not enough container slots: {what} need {QuantityRules.N(slots)}, you have {free}{CanBuy()}.";
            return free == 0 ? "The pack is full: no free slot for " + what + "."
                             : $"Not enough room in your packs: {what} need {QuantityRules.N(slots)} free slots, you have {free}{CanBuy()}.";
        }
        if (FreeBurden >= 0 && burden > FreeBurden)
            return $"Too heavy: {what} weigh {QuantityRules.N(burden)} burden, you can carry {QuantityRules.N(FreeBurden)} more{CanBuy()}.";
        return null;
    }

    private string CanBuy()
    {
        int max = MaxBuy();
        return max >= 1 ? $" (you can buy {QuantityRules.N(max)})" : string.Empty;
    }

    /// <summary>
    /// The price line beside the Buy button: "300 pyreals · you have 1,250"; with a bank that's read
    /// "price 1,200; pack 300 + bank 5,000"; with one that isn't "1,200 pyreals · pack 300 + the bank".
    /// </summary>
    public string PriceLine(int n)
    {
        string price = n >= 1 ? QuantityRules.N(Cost(n)) : "-";
        if (Funds >= 0 && BankKnown) return $"price {price}; pack {QuantityRules.N(Funds)} + bank {QuantityRules.N(BankFunds)}";
        string cost = n >= 1 ? price + " " + Currency : "-";
        if (Funds >= 0 && BankUnknown) return $"{cost} · pack {QuantityRules.N(Funds)} + the bank";
        return Funds >= 0 ? $"{cost} · you have {QuantityRules.N(Funds)}" : cost;
    }

    /// <summary>
    /// What the bank does for buying <paramref name="n"/>, when the pack alone is short (null otherwise,
    /// and always without a bank): the box's one-line note.
    /// </summary>
    public string? BankNote(int n)
    {
        if (!BankPays || Funds < 0 || n < 1) return null;
        long cost = Cost(n);
        if (cost <= Funds) return null;
        if (BankUnknown)
            return $"Your pack has {QuantityRules.N(Funds)} of the {QuantityRules.N(cost)} {Currency}: the server's bank may cover it (the server decides).";
        return cost <= Spendable ? $"The bank pays the {QuantityRules.N(cost - Funds)} {Currency} your pack is short." : null;
    }

    /// <summary>What "max" counts, when that isn't plain: "" without a bank, or with one that's read.</summary>
    public string MaxNote => BankUnknown ? "max counts the vendor's stock, your pack space and burden only (the bank's balance can't be read)" : string.Empty;
}

/// <summary>What the quantity box's actions say (chat and the Selection window). Pure.</summary>
internal static class QtyReplies
{
    public const string NothingSelected = "Nothing is selected. Select a stack in your pack (or an item in a vendor's list) first.";
    public const string Busy = "Wait a moment: the last split, give or buy is still waiting for the server.";
    public const string NoSplitApi = "This RynthCore can't split stacks for a plugin.";
    public const string NoGiveApi = "This RynthCore can't give items for a plugin (it needs plugin API v62).";
    public const string NoTradeApi = "This RynthCore can't trade with vendors for a plugin (it needs plugin API v67).";
    public const string NoVendor = "No vendor is open: talk to a vendor first.";
    public const string NoVendorItem = "Select the item in the vendor's list first (or type its name: /rdef buy 25 Mana Scarab).";
    public const string VendorUnreadable = "RynthCore can't trade with this vendor (its list couldn't be read): use the game's own Buy.";
    public const string VendorBusy = "A trade with the vendor is still going: wait a moment.";
    public const string NoGiveTarget = "Who to? Select the player or NPC first, then the stack: Give goes to the last one you selected.";
    public const string MaxNeedsSelection = "max and half are of what is selected: select a stack or a vendor's item first. Usage: /rdef qty <number|max|half>";

    public static string NotAStack(string name) => $"That's not a stack: {name} is a single item.";
    public static string Wielded(string name) => $"{name} is wielded: take it off first.";
    public static string NotReachable(string name) => $"{name} isn't in your packs or in the container you have open.";
    public static string NotCarried(string name) => $"{name} isn't in your packs: pick it up first.";
    public static string VendorsItem(string name) => $"{name} is the vendor's: Buy takes it (/rdef buy N).";
    public static string PackFull(string where) =>
        $"{Cap(where)} is full. Pick another pack for the split (the 'into' button, or /rdef split 25 into <pack>).";
    public static string NoSuchPack(string typed, string packs) => $"No pack called '{typed}'. Your packs: {packs}.";
    public static string NotSent(string what) => $"The {what} couldn't be sent (the game isn't ready for it): nothing changed.";

    public static string Splitting(int n, string name, string into) => $"Split {QuantityRules.N(n)} {name} into {into}.";
    public static string Gave(int n, string name, string to) => $"Gave {QuantityRules.N(n)} {name} to {to}.";
    public static string Bought(int n, string name, long cost, string currency) =>
        cost > 0 ? $"Bought {QuantityRules.N(n)} {name} for {QuantityRules.N(cost)} {currency}." : $"Bought {QuantityRules.N(n)} {name}.";
    public static string Refused(string what, uint error) =>
        error != 0 ? $"The server refused the {what} (error 0x{error:X4}): nothing changed." : $"The server refused the {what}: nothing changed.";
    public static string NoAnswer(string what) => $"No answer from the server for the {what}: check your pack before trying again.";

    /// <summary>
    /// The engine's refusal of a buy (VendorTradeStatus.Message, Compatibility/VendorTrade.cs) in the
    /// player's words. Unknown messages are passed on as they are.
    /// </summary>
    public static string VendorRefusal(string? message)
    {
        string m = (message ?? string.Empty).Trim();
        if (m.Length == 0) return "The vendor didn't sell it.";
        if (m.StartsWith("costs ", StringComparison.Ordinal))
        {
            // "costs 300 pyreals, you have 200" / "costs 3 Ancient Mattekar Claws, you have 1"
            int comma = m.IndexOf(", you have ", StringComparison.Ordinal);
            string head = comma > 0 ? m[6..comma] : m[6..];
            int sp = head.IndexOf(' ');
            string currency = sp > 0 ? head[(sp + 1)..] : "pyreals";
            return $"Not enough {currency}: it {m}.";
        }
        if (m.StartsWith("needs ", StringComparison.Ordinal) && m.Contains("free pack slots", StringComparison.Ordinal))
            return m.EndsWith("you have 0", StringComparison.Ordinal) ? $"The pack is full: it {m}." : $"Not enough room in your packs: it {m}.";
        if (m.StartsWith("needs ", StringComparison.Ordinal) && m.Contains("container slots", StringComparison.Ordinal))
            return $"No room for another pack: it {m}.";
        if (m.StartsWith("adds ", StringComparison.Ordinal) && m.Contains("burden", StringComparison.Ordinal))
            return $"Too heavy: it {m}.";
        if (m.Contains("vendor only has", StringComparison.Ordinal) || m.StartsWith("bad amount", StringComparison.Ordinal))
            return "The vendor doesn't have that many.";
        if (m.Contains("no vendor open", StringComparison.Ordinal) || m.Contains("vendor window is closed", StringComparison.Ordinal)
            || m.Contains("vendor closed", StringComparison.Ordinal) || m.Contains("is not the open vendor", StringComparison.Ordinal))
            return "The vendor is closed: talk to it again.";
        if (m.Contains("not on this vendor's list", StringComparison.Ordinal) || m.Contains("no longer on the vendor's list", StringComparison.Ordinal))
            return "The vendor doesn't sell that any more.";
        if (m.Contains("requests already waiting", StringComparison.Ordinal))
            return VendorBusy;
        if (m.Contains("could not read your", StringComparison.Ordinal) || m.Contains("could not count your", StringComparison.Ordinal))
            return $"RynthCore couldn't count your money ({m}): try again in a moment.";
        return $"The vendor didn't sell it: {m}.";
    }

    /// <summary>
    /// A buy's refusal when the server's bank was to pay part of it (<paramref name="bankMayPay"/>):
    /// RynthCore's own money check ("costs ..., you have ...") counts only the pyreals the game shows,
    /// so it says that and what to do; anything else as <see cref="VendorRefusal"/>.
    /// </summary>
    public static string BuyRefusal(string? message, bool bankMayPay)
    {
        string m = (message ?? string.Empty).Trim();
        if (bankMayPay && m.StartsWith("costs ", StringComparison.Ordinal))
            return $"RynthCore didn't send it: it {m}. RynthCore counts the pyreals the game shows, and the bank's aren't in them: take the rest out of the bank, then buy again.";
        return VendorRefusal(m);
    }

    /// <summary>The server answered a buy without selling it.</summary>
    public static string NotSold(int n, string name, bool bankMayPay, long cost, string currency) => bankMayPay
        ? $"The vendor didn't sell {QuantityRules.N(n)} {name}: the server said no (your pack and the bank may not cover {QuantityRules.N(cost)} {currency}, or there's no room)."
        : $"The vendor didn't sell {QuantityRules.N(n)} {name}: the server said no (money, room, or too busy).";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

// Trade Shop settings and catalog rows (LegacyUiSettings.TradeShop).
// Ported from SilentKelpie's Trade Shop (RynthSuite PR #12); re-implemented here.
//
// Pure (System only): RynthCore.RynthAiTests links it.
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.Trade;

/// <summary>How a sale is fulfilled.</summary>
public enum TradeShopMode
{
    /// <summary>The item is in this character's pack: the shop puts it in the trade window.</summary>
    Mode1Stock = 0,
    /// <summary>
    /// A text listing (another account holds the item): the buyer pays in the window, the shop checks
    /// the payment and the player accepts by hand (the shop never accepts a Mode 2 sale itself), then
    /// hands the item over from wherever it is.
    /// </summary>
    Mode2Text = 1,
}

/// <summary>One sellable row.</summary>
public sealed class TradeShopListing
{
    /// <summary>Display name (Mode 1: the item's name when it was listed; Mode 2: typed).</summary>
    public string Name = "";
    /// <summary>Mode 2: what the buyer is told after paying (where and how they get the item).</summary>
    public string Description = "";
    public long PricePyreals;
    /// <summary>Mode 1: the item's object id when it was listed (0: find it by WCID and name).</summary>
    public uint ObjectId;
    /// <summary>Mode 1: the item's WCID when it was listed (0: unknown, then the name must match).</summary>
    public uint Wcid;
    /// <summary>Mode 1: the stack size when it was listed (0: not a stack). The stack sold must still be this size.</summary>
    public int Stack;
    /// <summary>Mode 2: which character holds it (shown to the seller only).</summary>
    public string HeldBy = "";
    /// <summary>False: listed but not for sale.</summary>
    public bool Enabled = true;

    public string Label() => (Name.Length > 0 ? Name : "(unnamed)") + (Stack > 1 ? " (stack of " + Stack + ")" : "");
}

/// <summary>Every Trade Shop setting (one LegacyUiSettings field).</summary>
public sealed class TradeShopSettings
{
    /// <summary>Shortest advertise interval: 5 minutes.</summary>
    public const int MinAdvertiseSec = 300;
    public const int MaxAdvertiseSec = 3600;
    public const int DefaultAdvertiseSec = 600;

    /// <summary>
    /// The shop is on. Never saved: every login starts with the shop off, and the player turns it on
    /// (the window's checkbox or /ra tradeshop on), which says what it does.
    /// </summary>
    [JsonIgnore] public bool Enabled;

    public TradeShopMode Mode = TradeShopMode.Mode1Stock;

    /// <summary>
    /// On (the default): the shop gets each sale ready (the item in the window, the payment checked by
    /// WCID) and waits for the player's Accept sale. Off: it accepts a checked Mode 1 sale itself.
    /// </summary>
    public bool ConfirmEachSale = true;

    public TradeShopRates Rates { get; set; } = new();
    public List<TradeShopListing> Listings { get; set; } = new();

    public bool AdvertiseEnabled;
    public TradeShopAdvertiseChannel AdvertiseChannel = TradeShopAdvertiseChannel.Trade;
    public int AdvertiseIntervalSec = DefaultAdvertiseSec;
    public string AdvertisePrefix = "";

    /// <summary>Guards <see cref="Listings"/> (the window edits it on the render thread, the shop reads it on the tick).</summary>
    [JsonIgnore] public readonly object Sync = new();

    public static int ClampAdvertiseSec(int sec) => sec < MinAdvertiseSec ? MinAdvertiseSec : sec > MaxAdvertiseSec ? MaxAdvertiseSec : sec;

    /// <summary>The rows for sale now (enabled, priced, named), copied under the lock.</summary>
    public List<TradeShopListing> ForSale()
    {
        var list = new List<TradeShopListing>();
        lock (Sync)
        {
            foreach (TradeShopListing? l in Listings ?? new List<TradeShopListing>())
                if (l != null && l.Enabled && l.PricePyreals > 0 && !string.IsNullOrWhiteSpace(l.Name))
                    list.Add(l);
        }
        return list;
    }
}

using RynthCore.Plugin.RynthAi.Vendor;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

/// <summary>
/// AutoVendor's prices for a free vendor (buy=0 sell=0, a storage vault): 0 both ways, as the engine
/// prices it since AwfulWaffle's RynthCore PR #10, so the planner never thinks a free trade costs money.
/// </summary>
internal static class AutoVendorZeroRateTests
{
    public static void Register(Runner r)
    {
        r.Add("autovendor prices: a zero-rate (free) vendor costs and pays 0; any rate above 0 keeps ACE's 1 floor", ZeroRatePrices);
    }

    private static void ZeroRatePrices()
    {
        var free = new VendorView { BuyRate = 0f, SellRate = 0f };
        Check.Eq(AutoVendorPlanner.PlayerPays(free, isNote: false, 1), 0L, "free vendor: a 1-value stack costs 0");
        Check.Eq(AutoVendorPlanner.PlayerPays(free, isNote: false, 250_000), 0L, "free vendor: a big stack costs 0");
        Check.Eq(AutoVendorPlanner.PlayerPays(free, isNote: true, 250_000), 0L, "free vendor: a note costs 0");
        Check.Eq(AutoVendorPlanner.VendorPays(free, isNote: false, 250_000), 0L, "free vendor: pays 0");
        Check.Eq(AutoVendorPlanner.VendorPays(free, isNote: true, 250_000), 0L, "free vendor: pays 0 for a note");

        var shop = new VendorView { BuyRate = 0.75f, SellRate = 1.15f };
        Check.Eq(AutoVendorPlanner.PlayerPays(shop, isNote: false, 100), 115L, "rate 1.15, value 100: 115");
        Check.Eq(AutoVendorPlanner.VendorPays(shop, isNote: false, 100), 75L, "rate 0.75, value 100: 75");
        Check.Eq(AutoVendorPlanner.PlayerPays(shop, isNote: false, 0), 1L, "a rate above 0 keeps the 1-per-stack floor");
        var tiny = new VendorView { BuyRate = 0.001f, SellRate = 0.001f };
        Check.Eq(AutoVendorPlanner.VendorPays(tiny, isNote: false, 1), 1L, "rate 0.001 still floors at 1");
    }
}

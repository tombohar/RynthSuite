using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Vendor;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

/// <summary>
/// The engine trades with zero-rate vendors (a free storage vault, buy=0 sell=0). A vendor that pays
/// nothing for items (buy rate 0) met with default.utl gets nothing sold to it: that profile's Sell
/// rules turn loot into pyreals, and would hand every match over for nothing. The vendor's own
/// profile (or one named on /ub autovendor) sells as written, so a vault can still be filled.
/// TestMode shows the plan without trading.
/// </summary>
internal static unsafe class AutoVendorZeroPayTests
{
    private const uint Player = 0x50000D01;
    private const uint Vendor = 0x70000D01;
    private const string PaysNothing = "pays nothing for items";

    private static float _buyRate, _sellRate;

    public static void Register(Runner r)
    {
        r.Add("autovendor zero pay: a vendor paying nothing, met with default.utl, gets nothing sold (said once, held back in TestMode)", DefaultProfileSellsNothing);
        r.Add("autovendor zero pay: the vendor's own profile still sells to a zero-rate vault", OwnProfileSells);
        r.Add("autovendor zero pay: a profile named on /ub autovendor counts as the vendor's own", NamedProfileSells);
        r.Add("autovendor zero pay: a vendor that pays (buy rate above 0) is as before with default.utl", PayingVendorUnchanged);
    }

    private static void VendorCalls(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 82u);
        api.GetVendorInfoFn        = (IntPtr)(delegate* unmanaged[Cdecl]<VendorInfoNative*, int>)&GetVendorInfo;
        api.GetVendorItemsFn       = (IntPtr)(delegate* unmanaged[Cdecl]<VendorItemNative*, int, int>)&NoVendorItems;
        api.VendorBuyFn            = (IntPtr)(delegate* unmanaged[Cdecl]<uint, VendorTradeEntryNative*, int, uint>)&NoBuy;
        api.VendorSellFn           = (IntPtr)(delegate* unmanaged[Cdecl]<uint, uint*, int, uint>)&NoSell;
        api.GetVendorTradeStatusFn = (IntPtr)(delegate* unmanaged[Cdecl]<VendorTradeStatusNative*, int>)&NoTradeStatus;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetVendorInfo(VendorInfoNative* info)
    {
        *info = default;
        info->VendorId = Vendor;
        info->ItemTypes = 0xFFFFFFFF;
        info->MaxValue = 1_000_000;
        info->BuyRate = _buyRate;
        info->SellRate = _sellRate;
        info->Flags = 1;   // trading available
        byte[] name = Encoding.ASCII.GetBytes("Town Vendor");
        for (int i = 0; i < name.Length; i++) info->Name[i] = name[i];
        return 1;
    }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NoVendorItems(VendorItemNative* items, int max) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint NoBuy(uint vendor, VendorTradeEntryNative* entries, int count) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint NoSell(uint vendor, uint* ids, int count) => 0;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int NoTradeStatus(VendorTradeStatusNative* status) => 0;

    /// <summary>Runs one TestMode session at the vendor with <paramref name="profileFile"/> in the main folder.</summary>
    private static void Session(float buy, float sell, string profileFile, Action<AutoVendorManager>? start = null)
    {
        _buyRate = buy;
        _sellRate = sell;
        FakeHost.Reset();
        FakeHost.ConfigureApi = VendorCalls;
        FakeHost.Names[Player] = "Test Seller";
        FakeHost.Names[Vendor] = "Town Vendor";
        string root = Path.Combine(Program.TempRoot, "avzero-" + Guid.NewGuid().ToString("N")[..8]);
        string main = Path.Combine(root, "AutoVendor");
        Directory.CreateDirectory(main);
        File.WriteAllText(Path.Combine(main, profileFile), "UTL\n1\n0\n");
        AutoVendorManager.MainProfileDirOverride = main;
        var host = FakeHost.Create(Player);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        var settings = new LegacyUiSettings
        {
            AutoVendorEnabled = start == null, AutoVendorEnableSelling = true, AutoVendorTestMode = true, AutoVendorShowMerchantInfo = false,
        };
        string charFolder = Path.Combine(root, "Char");
        var m = new AutoVendorManager(host, settings, cache, Player, () => charFolder);
        long now = 1_000_000;
        AutoVendorManager.Clock = () => now;
        m.OnVendorOpen(Vendor);
        if (start != null) { settings.AutoVendorEnabled = true; start(m); }
        for (int i = 0; i < 10; i++) { now += 250; m.Tick(0); }
    }

    private static void Scoped(Action body)
    {
        string? oldDir = AutoVendorManager.MainProfileDirOverride;
        var oldClock = AutoVendorManager.Clock;
        try { body(); }
        finally { AutoVendorManager.MainProfileDirOverride = oldDir; AutoVendorManager.Clock = oldClock; FakeHost.ConfigureApi = null; }
    }

    private static string AllChat() => string.Join(" / ", FakeHost.Chat);
    private static bool Said(string text) => FakeHost.Chat.Any(c => c.Contains(text, StringComparison.Ordinal));

    private static void DefaultProfileSellsNothing() => Scoped(() =>
    {
        foreach (float sell in new[] { 1.15f, 0f })   // a shop that pays nothing, and a vault
        {
            Session(0f, sell, "default.utl");
            Check.True(Said("TestMode is on"), $"sell {sell}: the TestMode session ran: " + AllChat());
            Check.Eq(FakeHost.Chat.Count(c => c.Contains(PaysNothing, StringComparison.Ordinal)), 1, $"sell {sell}: said once, at the start: " + AllChat());
            Check.True(Said("Town Vendor.utl"), $"sell {sell}: and names the profile to make");
            Check.True(Said("Held back (never sold):") && Said("everything - Town Vendor pays nothing"), $"sell {sell}: TestMode shows everything held back");
            Check.True(FakeHost.Logs.Any(l => l.Contains(PaysNothing, StringComparison.Ordinal)), $"sell {sell}: in the log too");
        }
    });

    private static void OwnProfileSells() => Scoped(() =>
    {
        Session(0f, 0f, "Town Vendor.utl");
        Check.True(Said("TestMode is on"), "the TestMode session ran: " + AllChat());
        Check.False(Said(PaysNothing), "the vendor's own profile: no 'pays nothing' line");
        Check.False(Said("everything - "), "nothing held back for the rate");
    });

    private static void NamedProfileSells() => Scoped(() =>
    {
        Session(0f, 0f, "default.utl", m => m.CommandAutoVendor("default"));
        Check.True(Said("TestMode is on"), "the TestMode session ran: " + AllChat());
        Check.False(Said(PaysNothing), "a profile named on the command: no 'pays nothing' line");
    });

    private static void PayingVendorUnchanged() => Scoped(() =>
    {
        Session(0.7f, 1.15f, "default.utl");
        Check.True(Said("TestMode is on"), "the TestMode session ran: " + AllChat());
        Check.False(Said(PaysNothing), "a vendor that pays: no 'pays nothing' line");
        Check.False(Said("everything - "), "nothing held back for the rate");
    });
}

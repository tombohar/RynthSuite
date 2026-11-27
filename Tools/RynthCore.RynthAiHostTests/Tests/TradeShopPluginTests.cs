using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Huds;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Trade;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Trade Shop in RynthAi (RynthAiPlugin.TradeShop.cs): its window in the manifest without a bar
// button, its settings in the profile (the on/off never saved), when it pauses, and /ra tradeshop
// (every form replies). The sale flow itself is tested offline in RynthCore.RynthAiTests (TradeShopTests).
internal static class TradeShopPluginTests
{
    private const uint Player = 0x50000F51;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    public static void Register(Runner r)
    {
        r.Add("trade shop: the window is in the manifest with no bar button", Manifest);
        r.Add("trade shop: settings save with the profile, the on/off never does; a reload keeps it", Settings);
        r.Add("trade shop: pauses while the macro runs, while AutoTrade is on, and in combat mode", Pauses);
        r.Add("trade shop: /ra tradeshop - every form replies, and the edits land", Commands);
    }

    private static void Manifest()
    {
        string json = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, tradeShop: true);
        SectionManifest m = SectionManifestParser.Parse("RynthAi", json, out string error);
        Check.Eq(error, string.Empty, "no parse error");
        Check.Eq(m.Windows.Length, 1, "the one window");
        SectionWindowDecl w = m.Windows.Single();
        Check.Eq(w.Id, "tradeshop", "id");
        Check.Eq(w.Title, "Trade Shop", "title");
        Check.Eq(w.Surface, RynthAiPlugin.TradeShopSurface, "the surface PluginExports.RenderEmbed draws");
        Check.False(w.Bar, "no bar button (the bar is full)");
        Check.True(w.Width >= w.MinWidth && w.Height >= w.MinHeight, "sizes");
        string none = SectionsManifest.Build(SectionsManifest.Views(), SectionsManifest.OwnSections, dashboard: true);
        Check.False(none.Contains("tradeshop"), "off unless asked");
    }

    private static void Settings()
    {
        var fresh = new LegacyUiSettings();
        Check.True(fresh.TradeShop != null && !fresh.TradeShop.Enabled && fresh.TradeShop.ConfirmEachSale && !fresh.TradeShop.AdvertiseEnabled,
            "a new profile: off, confirm each sale on, no advertising");

        var s = new LegacyUiSettings();
        s.TradeShop.Enabled = true;
        s.TradeShop.ConfirmEachSale = false;
        s.TradeShop.Mode = TradeShopMode.Mode2Text;
        s.TradeShop.Rates.PyrealsPerLegendaryKey = 400_000;
        s.TradeShop.Listings.Add(new TradeShopListing { Name = "Hat", PricePyreals = 5000, Wcid = 123, ObjectId = 0x80001234, Stack = 0 });
        string text = JsonSerializer.Serialize(s, RynthAiJsonContext.Default.LegacyUiSettings);
        LegacyUiSettings back = JsonSerializer.Deserialize(text, RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.False(back.TradeShop.Enabled, "the on/off is never saved (off at every login)");
        Check.False(back.TradeShop.ConfirmEachSale, "confirm each sale saves");
        Check.Eq(back.TradeShop.Mode, TradeShopMode.Mode2Text, "mode saves");
        Check.Eq(back.TradeShop.Rates.PyrealsPerLegendaryKey, 400_000L, "the key rate saves");
        Check.True(back.TradeShop.Listings.Count == 1 && back.TradeShop.Listings[0].Name == "Hat" && back.TradeShop.Listings[0].Wcid == 123,
            "the catalog saves");
        Check.False(text.Contains("\"Sync\""), "the lock isn't saved");

        LegacyUiSettings old = JsonSerializer.Deserialize("{\"EnableAutostack\":true}", RynthAiJsonContext.Default.LegacyUiSettings)!;
        Check.True(old.TradeShop != null && old.TradeShop.ConfirmEachSale, "a profile from before the shop: the defaults");

        var copy = typeof(LegacyDashboardRenderer).GetMethod("CopySettings", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dst = new LegacyUiSettings();
        dst.TradeShop.Enabled = true;
        copy.Invoke(null, new object[] { back, dst });
        Check.True(dst.TradeShop.Enabled, "a reload keeps the shop on");
        Check.True(dst.TradeShop.Listings.Count == 1, "and copies the catalog");
    }

    private static (RynthAiPlugin P, LegacyUiSettings S) Plugin()
    {
        FakeHost.Reset();
        FakeHost.Names[Player] = "Tester";
        RynthCoreHost host = FakeHost.Create(playerId: Player);
        var p = new RynthAiPlugin();
        typeof(RynthPluginBase).GetMethod("Attach", Any)!.Invoke(p, new object[] { FakeHost.LastApi });
        var s = new LegacyUiSettings();
        var dash = (LegacyDashboardRenderer)RuntimeHelpers.GetUninitializedObject(typeof(LegacyDashboardRenderer));
        typeof(LegacyDashboardRenderer).GetField("_profileLoaded", Any)!.SetValue(dash, true);
        typeof(LegacyDashboardRenderer).GetField("_settings", Any)!.SetValue(dash, s);
        typeof(RynthAiPlugin).GetField("_dashboard", Any)!.SetValue(p, dash);
        var cache = new WorldObjectCache(host);
        cache.SetPlayerId(Player);
        typeof(RynthAiPlugin).GetField("_objectCache", Any)!.SetValue(p, cache);
        typeof(RynthAiPlugin).GetField("_playerId", Any)!.SetValue(p, Player);
        typeof(RynthAiPlugin).GetMethod("CreateTradeShop", Any)!.Invoke(p, null);
        return (p, s);
    }

    private static string Say(RynthAiPlugin p, string line, bool tick = false)
    {
        FakeHost.Chat.Clear();
        typeof(RynthAiPlugin).GetMethod("HandleTradeShopRaCommand", Any)!.Invoke(p, new object[] { line.Split(' ') });
        if (tick) Shop(p).Tick();
        return string.Join(" | ", FakeHost.Chat);
    }

    private static TradeShopManager Shop(RynthAiPlugin p) => (TradeShopManager)typeof(RynthAiPlugin).GetField("_tradeShop", Any)!.GetValue(p)!;

    private static string Pause(RynthAiPlugin p) => (string)typeof(RynthAiPlugin).GetMethod("TradeShopPauseReason", Any)!.Invoke(p, null)!;

    private static void Pauses()
    {
        var (p, s) = Plugin();
        Check.Eq(Pause(p), "", "idle, macro off: free");
        s.IsMacroRunning = true;
        Check.Eq(Pause(p), "the macro is running", "the macro");
        s.IsMacroRunning = false;
        s.AutoTradeEnabled = true;
        Check.True(Pause(p).StartsWith("AutoTrade is on"), "AutoTrade");
        s.AutoTradeEnabled = false;
        Check.Eq(Pause(p), "", "free again");
    }

    private static void Commands()
    {
        var (p, s) = Plugin();
        TradeShopSettings c = s.TradeShop;

        string r = Say(p, "/ra tradeshop status");
        Check.True(r.Contains("Trade Shop: OFF") && r.Contains("confirm each sale ON"), "status: " + r);
        r = Say(p, "/ra tradeshop");
        Check.True(r.Contains("Trade Shop:") && r.Contains("window"), "bare: the status and the window: " + r);
        r = Say(p, "/ra tradeshop bogus");
        Check.True(r.Contains("unknown option") && r.Contains("Usage"), "a wrong option says the usage: " + r);

        r = Say(p, "/ra tradeshop on", tick: true);
        Check.True(c.Enabled && r.Contains("Trade Shop ON") && r.Contains("Confirm each sale is ON"), "on says what it does: " + r);
        r = Say(p, "/ra tradeshop off", tick: true);
        Check.True(!c.Enabled && r.Contains("Trade Shop OFF"), "off: " + r);

        r = Say(p, "/ra tradeshop confirm");
        Check.True(r.Contains("Usage: /ra tradeshop confirm on|off"), "confirm without on/off: " + r);
        r = Say(p, "/ra tradeshop confirm off", tick: true);
        Check.True(!c.ConfirmEachSale && r.Contains("Warning"), "confirm off warns: " + r);
        r = Say(p, "/ra tradeshop confirm on", tick: true);
        Check.True(c.ConfirmEachSale && r.Contains("Confirm each sale ON"), "confirm on: " + r);

        r = Say(p, "/ra tradeshop mode2");
        Check.True(c.Mode == TradeShopMode.Mode2Text && r.Contains("Mode 2"), "mode2: " + r);
        r = Say(p, "/ra tradeshop mode1");
        Check.True(c.Mode == TradeShopMode.Mode1Stock && r.Contains("Mode 1"), "mode1: " + r);

        r = Say(p, "/ra tradeshop add 1,500 Fancy Hat");
        Check.True(c.Listings.Count == 1 && c.Listings[0].Name == "Fancy Hat" && c.Listings[0].PricePyreals == 1500 && r.Contains("listed Fancy Hat"),
            "add a typed listing: " + r);
        r = Say(p, "/ra tradeshop add");
        Check.True(r.Contains("Usage: /ra tradeshop add"), "add without a price: " + r);
        r = Say(p, "/ra tradeshop price 1 2500");
        Check.True(c.Listings[0].PricePyreals == 2500 && r.Contains("now"), "price: " + r);
        r = Say(p, "/ra tradeshop price 9 2500");
        Check.True(r.Contains("there is no #9"), "price of a missing row: " + r);
        r = Say(p, "/ra tradeshop list");
        Check.True(r.Contains("#1 Fancy Hat"), "list: " + r);
        r = Say(p, "/ra tradeshop keys 300000");
        Check.True(c.Rates.PyrealsPerLegendaryKey == 300_000 && r.Contains("1 LK = 300,000 py"), "keys: " + r);
        r = Say(p, "/ra tradeshop remove 1");
        Check.True(c.Listings.Count == 0 && r.Contains("removed #1 Fancy Hat"), "remove: " + r);

        r = Say(p, "/ra tradeshop accept");
        Check.True(r.Contains("no sale is waiting"), "accept with nothing waiting: " + r);
        r = Say(p, "/ra tradeshop decline", tick: true);
        Check.True(r.Contains("no sale to decline"), "decline with nothing: " + r);

        r = Say(p, "/ra tradeshop advertise");
        Check.True(r.Contains("advertising OFF"), "advertising off by default: " + r);
        r = Say(p, "/ra tradeshop advertise every 1");
        Check.True(c.AdvertiseIntervalSec == TradeShopSettings.MinAdvertiseSec && r.Contains("every 5 min"), "a 1-minute interval clamps to 5: " + r);
        r = Say(p, "/ra tradeshop advertise channel general");
        Check.True(c.AdvertiseChannel == TradeShopAdvertiseChannel.General && r.Contains("General"), "channel: " + r);
        r = Say(p, "/ra tradeshop advertise channel allegiance");
        Check.True(r.Contains("Channels:"), "a channel the shop doesn't use: " + r);
        r = Say(p, "/ra tradeshop advertise now", tick: true);
        Check.True(r.Contains("turn the shop on first"), "advertise now while off: " + r);
        r = Say(p, "/ra tradeshop help");
        Check.True(r.Contains("/ra tradeshop") && r.Contains("Usage"), "help: " + r);
    }
}

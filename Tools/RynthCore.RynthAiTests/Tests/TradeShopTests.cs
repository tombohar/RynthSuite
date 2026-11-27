using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using RynthCore.Plugin.RynthAi.Trade;
using RynthCore.RynthAiTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// The Trade Shop (Plugins/RynthCore.Plugin.RynthAi/Trade). Ported from SilentKelpie's Trade Shop
// tests (RynthSuite PR #12: tell parsing, selectors, the price sheet, advertise prefixes), plus the
// safety rules: payment by WCID only, the accept-time re-check, no accept without !buy, Confirm each
// sale both ways, Mode 2 never accepted by the shop, the tell limits and the defaults.
internal static class TradeShopTests
{
    public static void Register(Runner r)
    {
        // Ported (adapted).
        r.Add("tradeshop tell: plain Name tells you", PlainTell);
        r.Add("tradeshop tell: IIDString link, and a quoted 'X tells you' can't change the sender", LinkedTell);
        r.Add("tradeshop tell: shop verbs need a '!' (a lone greeting doesn't)", Verbs);
        r.Add("tradeshop selector: slot, value and name forms", Selectors);
        r.Add("tradeshop messages: resolve by slot, name and value", Resolve);
        r.Add("tradeshop messages: the sheet has the rates and every row", SheetRows);
        r.Add("tradeshop advertise: channel prefixes and names", AdvertisePrefixes);
        // New: payment by WCID.
        r.Add("tradeshop payment: the WCID table (pyreals 273, 14 trade notes at face value, single-use LK)", WcidTable);
        r.Add("tradeshop payment: junk with a high Value or a currency's name is worth 0", JunkWorthZero);
        r.Add("tradeshop payment: an item not read yet is never paid", UnknownNotPaid);
        r.Add("tradeshop payment: rates, refused currencies and price alternatives", Rates);
        // New: the sale flow.
        r.Add("tradeshop sale: confirm ON - ready, prompt, Accept sale re-checks then accepts, sale logged", ConfirmOnSale);
        r.Add("tradeshop sale: confirm ON - a change while the prompt is up clears it and checks again", PromptClearsOnChange);
        r.Add("tradeshop sale: confirm ON - Decline closes the trade and tells the buyer", DeclineCloses);
        r.Add("tradeshop sale: confirm OFF - accepts a checked sale itself, and the warning when turned off", ConfirmOffSale);
        r.Add("tradeshop sale: the accept-time re-check refuses when the trade changes between the reads", AcceptTimeRecheck);
        r.Add("tradeshop sale: a change after the accept that fails the check withdraws the accept", ChangeAfterAccept);
        r.Add("tradeshop sale: our side must be exactly the listed item", OurSideExact);
        r.Add("tradeshop sale: no accept without !buy (greeted only), in either mode", NoAcceptWithoutBuy);
        r.Add("tradeshop sale: Mode 2 is never accepted by the shop, only by the player's Accept sale", Mode2HandAccept);
        r.Add("tradeshop sale: paused in combat / macro / hunt: tells dropped, a sale called off", Paused);
        r.Add("tradeshop sale: turning it on says what it does; off means no answers", OnOff);
        // New: limits and defaults.
        r.Add("tradeshop limits: one command per 2 s, 20 lines a minute, ignore after spam", InboundLimits);
        r.Add("tradeshop limits: 12 lines a minute per buyer, 30 in all, queues capped", OutboundLimits);
        r.Add("tradeshop limits: the price sheet is chunked and capped", SheetCapped);
        r.Add("tradeshop defaults: off, confirm on, no advertising, Trade, 5 min minimum, on/off not saved", Defaults);
        r.Add("tradeshop advertise: off by default, never before a full interval, no leading slash", Advertising);
    }

    // ── Ported ──────────────────────────────────────────────────────────

    private static void PlainTell()
    {
        Check.True(TradeShopTellParse.TryParseTell("Bob tells you, \"!list\"", out string who, out string msg), "parse");
        Check.Eq(who, "Bob", "sender");
        Check.Eq(msg, "!list", "body");
        Check.False(TradeShopTellParse.TryParseTell("Bob says, \"!list\"", out _, out _), "local chat isn't a tell");
    }

    private static void LinkedTell()
    {
        Check.True(TradeShopTellParse.TryParseTell("<Tell:IIDString:1342177281:Alice>Alice<\\Tell> tells you, \"!buy 2\"",
            out string who, out string msg), "parse link");
        Check.Eq(who, "Alice", "link name");
        Check.Eq(msg, "!buy 2", "body");
        Check.True(TradeShopTellParse.TryParseTell("Bob tells you, \"Eve tells you, \"!buy 1\"\"", out who, out _), "nested");
        Check.Eq(who, "Bob", "the first speaker is the sender");
        Check.False(TradeShopTellParse.IsSafeName("Bob, /quit"), "a name with a comma or slash isn't sent a tell");
    }

    private static void Verbs()
    {
        Check.Eq(TradeShopTellParse.SplitCommand("!buy #2"), ("buy", "#2"), "buy");
        Check.Eq(TradeShopTellParse.SplitCommand("!sheet").verb, "list", "sheet = list");
        Check.Eq(TradeShopTellParse.SplitCommand("!rate").verb, "rates", "rate = rates");
        Check.Eq(TradeShopTellParse.SplitCommand("hi").verb, "help", "a lone greeting");
        Check.Eq(TradeShopTellParse.SplitCommand("hi there, how are you").verb, "", "chat isn't a command");
        Check.Eq(TradeShopTellParse.SplitCommand("buy 2").verb, "", "no ! no buy");
        Check.Eq(TradeShopTellParse.SplitCommand("!dance").verb, "", "unknown verb");
    }

    private static void Selectors()
    {
        Check.Eq(TradeShopTellParse.ParseSelector("#3").slot, 3, "#3");
        Check.Eq(TradeShopTellParse.ParseSelector("slot:4").slot, 4, "slot:");
        Check.Eq(TradeShopTellParse.ParseSelector("id:5").slot, 5, "id:");
        Check.Eq(TradeShopTellParse.ParseSelector("2").slot, 2, "bare small = slot");
        Check.Eq(TradeShopTellParse.ParseSelector("250,000").valuePy, 250000L, "bare large = value");
        Check.Eq(TradeShopTellParse.ParseSelector("value:1000").valuePy, 1000L, "value:");
        var q = TradeShopTellParse.ParseSelector("\"Sword of Lost Light\"");
        Check.Null(q.slot, "quoted is a name");
        Check.Eq(q.text, "Sword of Lost Light", "quoted text");
    }

    private static List<TradeShopListing> Three() => new()
    {
        new() { Name = "Alpha", PricePyreals = 100 },
        new() { Name = "Sword of Lost Light", PricePyreals = 250_000 },
        new() { Name = "Beta Key", PricePyreals = 500_000 },
    };

    private static void Resolve()
    {
        var list = Three();
        Check.Eq(TradeShopMessages.ResolveListing(list, "#2")!.Name, "Sword of Lost Light", "slot");
        Check.Eq(TradeShopMessages.ResolveListing(list, "lost light")!.Name, "Sword of Lost Light", "partial name");
        Check.Eq(TradeShopMessages.ResolveListing(list, "250000")!.Name, "Sword of Lost Light", "value exact");
        Check.Eq(TradeShopMessages.ResolveListing(list, "value:400000")!.Name, "Beta Key", "value at or above");
        Check.Null(TradeShopMessages.ResolveListing(list, "#9"), "no slot 9");
    }

    private static void SheetRows()
    {
        var chunks = TradeShopMessages.PriceSheet(Three(), new TradeShopRates(), includeRates: true);
        Check.True(chunks.Count >= 1, "a chunk");
        Check.True(chunks[0].StartsWith("Rates:", StringComparison.Ordinal), "rates first");
        string all = string.Join(" ", chunks);
        Check.True(all.Contains("#1 Alpha") && all.Contains("#2 Sword of Lost Light") && all.Contains("#3 Beta Key"), "every row: " + all);
        Check.True(chunks.All(c => c.Length <= TradeShopMessages.MaxLineChars), "tell-sized");
    }

    private static void AdvertisePrefixes()
    {
        Check.Eq(TradeShopAdvertise.SendPrefix(TradeShopAdvertiseChannel.Trade), "/ct ", "trade");
        Check.Eq(TradeShopAdvertise.SendPrefix(TradeShopAdvertiseChannel.General), "/cg ", "general");
        Check.Eq(TradeShopAdvertise.SendPrefix(TradeShopAdvertiseChannel.Local), "", "local");
        Check.True(TradeShopAdvertise.TryParseChannel("ct", out var ch) && ch == TradeShopAdvertiseChannel.Trade, "ct = Trade");
        Check.False(TradeShopAdvertise.TryParseChannel("allegiance", out _), "not a shop channel");
    }

    // ── Payment ─────────────────────────────────────────────────────────

    private static TradeShopItem? Lookup(Dictionary<uint, TradeShopItem> d, uint id) => d.TryGetValue(id, out var it) ? it : null;

    private static void WcidTable()
    {
        Check.Eq(TradeShopCurrencyTable.Classify(273), TradeShopCurrency.Pyreals, "273 pyreals");
        var faces = new Dictionary<uint, long>
        {
            [2621] = 100, [2622] = 500, [2623] = 1000, [2624] = 5000, [2625] = 10000, [7374] = 15000, [7375] = 20000,
            [7376] = 25000, [2626] = 50000, [7377] = 75000, [2627] = 100000, [20628] = 150000, [20629] = 200000, [20630] = 250000,
        };
        foreach (var kv in faces)
        {
            Check.Eq(TradeShopCurrencyTable.Classify(kv.Key), TradeShopCurrency.TradeNote, $"{kv.Key} is a note");
            Check.Eq(TradeShopCurrencyTable.NoteFaceValue(kv.Key), kv.Value, $"{kv.Key} face");
        }
        foreach (uint lk in new uint[] { 48747, 48914, 51558, 72048, 72600 })
            Check.Eq(TradeShopCurrencyTable.Classify(lk), TradeShopCurrency.LegendaryKey, $"{lk} single-use LK");
        foreach (uint not in new uint[] { 48746, 48748, 51954, 72474, 46414, 0, 1, 999999 })
            Check.Eq(TradeShopCurrencyTable.Classify(not), TradeShopCurrency.None, $"{not} isn't payment");
        Check.Eq(TradeShopCurrencyTable.AllWcids().Count(), 20, "20 WCIDs in all");

        var d = new Dictionary<uint, TradeShopItem>
        {
            [1] = new(1, 273, "Pyreal", 25_000, true),
            [2] = new(2, 20630, "Trade Note (250,000)", 3, true),
            [3] = new(3, 2623, "Trade Note (1,000)", 5, true),
            [4] = new(4, 48747, "Legendary Key", 0, true),
        };
        TradeShopTally t = TradeShopPayment.Tally(new uint[] { 1, 2, 3, 4 }, id => Lookup(d, id));
        Check.Eq(t.Pyreals, 25_000L, "pyreals by stack");
        Check.Eq(t.NotePyreals, 755_000L, "notes at face x stack");
        Check.Eq(t.LegendaryKeys, 1L, "one key");
        Check.Eq(t.Unknown, 0, "all read");
        Check.Eq(new TradeShopRates().AcceptedPyreals(t), 25_000L + 755_000L + TradeShopRates.DefaultPyrealsPerLegendaryKey, "worth");
    }

    private static void JunkWorthZero()
    {
        var d = new Dictionary<uint, TradeShopItem>
        {
            // A junk item with a huge Value: Value is never read; its WCID isn't in the table.
            [10] = new(10, 30_000, "Gold Chunk", 0, true),
            // Items named like currency, with the wrong WCID.
            [11] = new(11, 12_345, "Pyreal", 1_000_000, true),
            [12] = new(12, 54_321, "Trade Note (250,000)", 100, true),
            [13] = new(13, 48748, "Legendary Key", 0, true),   // a multi-use key: not one LK
        };
        TradeShopTally t = TradeShopPayment.Tally(new uint[] { 10, 11, 12, 13 }, id => Lookup(d, id));
        Check.Eq(new TradeShopRates().AcceptedPyreals(t), 0L, "worth 0");
        Check.Eq(t.OtherItems, 4, "four non-payment items");
        Check.False(new TradeShopRates().Covers(1, t), "doesn't pay even 1 py");
    }

    private static void UnknownNotPaid()
    {
        var d = new Dictionary<uint, TradeShopItem>
        {
            [1] = new(1, 20630, "Trade Note (250,000)", 100, true),
            [2] = new(2, 0, "", -1, false),                    // the client has no WCID for it yet
            [3] = new(3, 273, "Pyreal", 0, true),              // pyreals with no stack size read
        };
        TradeShopTally t = TradeShopPayment.Tally(new uint[] { 1, 2, 3, 4 }, id => Lookup(d, id));   // 4: not in the cache at all
        Check.Eq(t.Unknown, 3, "three unknown");
        Check.False(new TradeShopRates().Covers(1_000, t), "unknown items block the payment, however much else is there");
    }

    private static void Rates()
    {
        var r = new TradeShopRates();
        Check.Eq(r.KeysFor(1_000_000), 2L, "2 LK at 500k");
        Check.Eq(r.KeysFor(1_000_001), 3L, "rounded up");
        Check.Eq(r.MmdFor(1_000_000), 4L, "4 MMD");
        string alt = r.FormatAlternatives(1_000_000);
        Check.True(alt.Contains("1,000,000 py") && alt.Contains("4 MMD") && alt.Contains("2 LK"), alt);
        var t = new TradeShopTally { Pyreals = 600_000, NotePyreals = 500_000, LegendaryKeys = 1 };
        Check.True(r.Covers(1_000_000, t), "mixed payment covers");
        r.AcceptPyreals = false;
        Check.Eq(r.AcceptedPyreals(t), 1_000_000L, "pyreals refused: notes + key");
        r.AcceptLegendaryKeys = false;
        Check.False(r.Covers(1_000_000, t), "keys refused too: short");
        Check.Eq(r.KeysFor(1_000_000), 0L, "no key price when refused");
        Check.False(new TradeShopRates().Covers(0, t), "no price, no sale");
    }

    // ── The sale flow ───────────────────────────────────────────────────

    private const uint Bob = 0x50000BB0, Eve = 0x50000EE0, Sword = 0x7000_0001, SwordWcid = 5000;
    private const long Price = 1_000_000;

    private sealed class Shop
    {
        public readonly FakeShopGame G = new();
        public readonly TradeShopSettings Cfg = new() { Enabled = true };
        public readonly TradeShopManager M;
        private uint _nextItem = 0x6000_0000;

        public Shop(bool confirm = true, TradeShopMode mode = TradeShopMode.Mode1Stock)
        {
            Cfg.ConfirmEachSale = confirm;
            Cfg.Mode = mode;
            Cfg.Listings.Add(new TradeShopListing { Name = "Sword", PricePyreals = Price, ObjectId = Sword, Wcid = SwordWcid, Enabled = true });
            G.Items[Sword] = new TradeShopItem(Sword, SwordWcid, "Sword", 0, true);
            G.Pack.Add(G.Items[Sword]);
            G.Players["Bob"] = Bob; G.Names[Bob] = "Bob";
            G.Players["Eve"] = Eve; G.Names[Eve] = "Eve";
            M = new TradeShopManager(G, () => Cfg);
            Tick();   // baseline
        }

        public void Tick(int n = 1)
        {
            for (int i = 0; i < n; i++) { M.Tick(); G.Now += 1_300; }
        }

        public void TellFrom(string who, string body)
        {
            G.Now += TradeShopTellLimiter.MinCommandGapMs + 100;
            M.OnTell($"{who} tells you, \"{body}\"", isIncomingTell: true);
            Tick();
        }

        public uint Pay(uint wcid, int stack, string name = "x", bool known = true)
        {
            uint id = _nextItem++;
            G.BuyerAdds(id, wcid, stack, name, known);
            return id;
        }

        /// <summary>!buy, Bob opens the trade, the sword goes in the window.</summary>
        public void ReadyForPayment(string who = "Bob", uint whoId = Bob)
        {
            TellFrom(who, "!buy #1");
            G.OpenTradeWith(whoId);
            Tick();
        }

        public bool TellTo(string who, string part) => G.Tells.Any(t => t.To == who && t.Text.Contains(part, StringComparison.OrdinalIgnoreCase));
        public bool Chatted(string part) => G.Chats.Any(c => c.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static void ConfirmOnSale()
    {
        var s = new Shop(confirm: true);
        s.ReadyForPayment();
        Check.True(s.G.Actions.Contains("reset") && s.G.Actions.Contains("add:" + Sword.ToString("X8")), "window cleared, sword added: " + string.Join(",", s.G.Actions));
        Check.True(s.G.Mine.SequenceEqual(new[] { Sword }), "our side is the sword");
        s.Pay(TradeShopCurrencyTable.MmdWcid, 4, "Trade Note (250,000)");
        s.Tick();
        Check.Eq(s.G.Count("accept"), 0, "confirm on: no accept by itself");
        TradeShopPrompt? p = s.M.Prompt;
        Check.NotNull(p, "the prompt is up");
        Check.True(p != null && p.Buyer == "Bob" && p.Item == "Sword" && p.PricePyreals == Price && p.Paid.Contains("4x Trade Note (250,000)"), "prompt: buyer, item, price, paid");
        Check.True(s.Chatted("sale ready") && s.Chatted("Bob") && s.Chatted("1,000,000"), "a chat line with the sale");
        s.Tick(5);
        Check.Eq(s.G.Count("accept"), 0, "still waiting for the player");

        s.M.RequestAccept();
        s.Tick();
        Check.Eq(s.G.Count("accept"), 1, "Accept sale accepts");
        Check.Null(s.M.Prompt, "prompt gone");
        s.G.BuyerAccepts();
        s.Tick();
        Check.Eq(s.G.Completed, 1u, "trade completed");
        Check.True(s.G.Given.SequenceEqual(new[] { Sword }), "we gave the sword");
        Check.Eq(s.G.Sales.Count, 1, "one sale in the sales log");
        Check.True(s.G.Sales[0].Contains("Sword") && s.G.Sales[0].Contains("Bob") && s.G.Sales[0].Contains("1,000,000") && s.G.Sales[0].Contains("Trade Note"),
            "sale line: item, buyer, price, paid: " + s.G.Sales[0]);
        Check.True(s.Chatted("Sold Sword to Bob"), "the sale in chat");
        Check.True(s.G.Logs.Any(l => l.Contains("Sold Sword to Bob")), "the sale in the log");
        Check.True(s.M.RecentSales().Count == 1, "in the window's last sales");
        Check.True(s.G.Actions.Last() == "close", "trade window closed for the next buyer");
    }

    private static void PromptClearsOnChange()
    {
        var s = new Shop(confirm: true);
        s.ReadyForPayment();
        s.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        s.Tick();
        Check.NotNull(s.M.Prompt, "prompt up");
        s.Pay(0, -1, "?", known: false);   // the buyer adds something the client can't read yet
        s.Tick();
        Check.Null(s.M.Prompt, "the change cleared the prompt");
        Check.True(s.Chatted("changed; checking it again"), "said so");
        s.Tick(3);
        Check.Null(s.M.Prompt, "an unknown item keeps it from coming back");
        s.M.RequestAccept();
        s.Tick();
        Check.Eq(s.G.Count("accept"), 0, "no accept while an item is unknown");
        Check.True(s.Chatted("no sale is waiting"), "Accept sale says why");
    }

    private static void DeclineCloses()
    {
        var s = new Shop(confirm: true);
        s.ReadyForPayment();
        s.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        s.Tick();
        s.M.RequestDecline();
        s.Tick(3);
        Check.Eq(s.G.Count("accept"), 0, "not accepted");
        Check.True(s.G.Actions.Contains("close") && !s.G.Open, "trade closed");
        Check.Null(s.M.Prompt, "prompt gone");
        Check.True(s.TellTo("Bob", "declined"), "the buyer is told");
        Check.Eq(s.G.Sales.Count, 0, "no sale");
    }

    private static void ConfirmOffSale()
    {
        var s = new Shop(confirm: true);
        s.M.SetConfirmEachSale(false);
        s.Tick();
        Check.True(s.Chatted("Confirm each sale OFF. Warning"), "one-line warning when turned off");
        Check.False(s.Cfg.ConfirmEachSale, "off");
        s.ReadyForPayment();
        s.Pay(273, 999_999, "Pyreal");
        s.Tick(2);
        Check.Eq(s.G.Count("accept"), 0, "short by 1: no accept");
        s.Pay(273, 1, "Pyreal");
        s.Tick();
        Check.Eq(s.G.Count("accept"), 1, "covered: accepted without a prompt");
        Check.Null(s.M.Prompt, "no prompt");
        s.G.BuyerAccepts();
        s.Tick();
        Check.Eq(s.G.Sales.Count, 1, "sale logged");
    }

    private static void AcceptTimeRecheck()
    {
        // A trade event lands between the accept-time reads (only the state's sequence moves): the shop
        // doesn't accept on that read, and accepts on the next tick when nothing changed.
        var s = new Shop(confirm: false);
        s.ReadyForPayment();
        s.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        // The next tick reads the buyer's side twice: the wait check, then the accept-time check.
        s.G.HookOnPartnerRead = s.G.PartnerReads + 2;
        s.G.Hook = () => s.G.Sequence++;
        s.Tick();
        Check.Eq(s.G.Count("accept"), 0, "the state moved between the reads: not accepted");
        s.Tick();
        Check.Eq(s.G.Count("accept"), 1, "re-read, unchanged: accepted");

        // The payment shrinks during the accept-time check: never accepted.
        var t = new Shop(confirm: false);
        t.ReadyForPayment();
        uint note = t.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        t.G.HookOnPartnerRead = t.G.PartnerReads + 2;
        t.G.Hook = () =>
        {
            t.G.Items[note] = new TradeShopItem(note, TradeShopCurrencyTable.MmdWcid, "Trade Note (250,000)", 1, true);
            t.G.Sequence++;
        };
        t.Tick(4);
        Check.Eq(t.G.Count("accept"), 0, "short at the accept-time check: not accepted");
        Check.True(t.M.Status.Contains("paid 250,000 of 1,000,000"), "status says it's short: " + t.M.Status);
    }

    private static void ChangeAfterAccept()
    {
        var s = new Shop(confirm: false);
        s.ReadyForPayment();
        uint note = s.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        s.Tick();
        Check.Eq(s.G.Count("accept"), 1, "accepted");
        // A stale or out-of-order update: the window now shows less, and our accept still stands.
        s.G.Items[note] = new TradeShopItem(note, TradeShopCurrencyTable.MmdWcid, "Trade Note (250,000)", 1, true);
        s.G.YouAccepted = true;
        s.G.Sequence++;
        s.Tick();
        Check.Eq(s.G.Count("decline"), 1, "the accept is withdrawn");
        Check.True(s.Chatted("accept withdrawn"), "said so");
        s.G.BuyerAccepts();
        s.Tick();
        Check.Eq(s.G.Completed, 0u, "no trade");
        Check.Eq(s.G.Sales.Count, 0, "no sale");

        // The buyer adding more (ACE clears both accepts) is accepted again when it still passes.
        var t = new Shop(confirm: false);
        t.ReadyForPayment();
        t.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        t.Tick();
        t.G.Now += TradeShopManager.AcceptEchoWaitMs;
        t.Pay(273, 10, "Pyreal");
        t.Tick();
        Check.Eq(t.G.Count("accept"), 2, "accepted again after the buyer added more");
    }

    private static void OurSideExact()
    {
        var s = new Shop(confirm: false);
        s.ReadyForPayment();
        s.G.Items[0x7000_0002] = new TradeShopItem(0x7000_0002, 6000, "Shield", 0, true);
        s.G.Mine.Add(0x7000_0002);   // something else on our side too
        s.G.Sequence++;
        s.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        s.Tick(3);
        Check.Eq(s.G.Count("accept"), 0, "our side isn't exactly the sword: no accept");
        Check.True(s.M.Status.Contains("isn't exactly Sword"), s.M.Status);

        // The item in the window turned into something else (its WCID no longer the listing's).
        var t = new Shop(confirm: false);
        t.ReadyForPayment();
        t.G.Items[Sword] = new TradeShopItem(Sword, 9999, "Sword", 0, true);
        t.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        t.Tick(3);
        Check.Eq(t.G.Count("accept"), 0, "not the listed item: no accept");
    }

    private static void NoAcceptWithoutBuy()
    {
        foreach (TradeShopMode mode in new[] { TradeShopMode.Mode1Stock, TradeShopMode.Mode2Text })
        {
            var s = new Shop(confirm: false, mode: mode);
            s.G.OpenTradeWith(Eve);
            s.Tick();
            s.Pay(TradeShopCurrencyTable.MmdWcid, 100);
            s.G.BuyerAccepts();
            s.Tick(10);
            Check.Eq(s.G.Count("accept"), 0, $"{mode}: never accepted");
            Check.Null(s.M.Prompt, $"{mode}: no prompt");
            Check.False(s.G.Actions.Any(a => a.StartsWith("add:", StringComparison.Ordinal)), $"{mode}: nothing put in the window");
            Check.True(s.TellTo("Eve", "only accept a trade after !buy"), $"{mode}: greeted");
            Check.Eq(s.G.Tells.Count(t => t.To == "Eve"), 1, $"{mode}: greeted once");
        }

        // Bob says !buy, but Eve opens the trade: Eve is not the buyer.
        var b = new Shop(confirm: false);
        b.TellFrom("Bob", "!buy #1");
        b.G.OpenTradeWith(Eve);
        b.Tick();
        b.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        b.Tick(5);
        Check.Eq(b.G.Count("accept"), 0, "someone else's trade isn't the sale");
        Check.False(b.G.Actions.Any(a => a.StartsWith("add:", StringComparison.Ordinal)), "the sword stays in the pack");
    }

    private static void Mode2HandAccept()
    {
        var s = new Shop(confirm: false, mode: TradeShopMode.Mode2Text);
        s.TellFrom("Bob", "!buy #1");
        s.G.OpenTradeWith(Bob);
        s.Tick();
        Check.False(s.G.Actions.Any(a => a.StartsWith("add:", StringComparison.Ordinal) || a == "reset"), "Mode 2 puts nothing in the window");
        s.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        s.Tick(5);
        Check.Eq(s.G.Count("accept"), 0, "Mode 2: never accepted by the shop, even with confirm off");
        Check.NotNull(s.M.Prompt, "the prompt is up");
        Check.True(s.M.Prompt!.Mode == TradeShopMode.Mode2Text, "a Mode 2 prompt");
        s.M.RequestAccept();
        s.Tick();
        Check.Eq(s.G.Count("accept"), 1, "the player's Accept sale accepts");
        s.G.BuyerAccepts();
        s.Tick();
        Check.True(s.G.Sales.Count == 1 && s.G.Sales[0].Contains("hand it over"), "sale logged, with the hand-over note");
    }

    private static void Paused()
    {
        var s = new Shop(confirm: true);
        s.G.Pause = "the macro is running";
        s.TellFrom("Bob", "!list");
        s.Tick(3);
        Check.Eq(s.G.Tells.Count, 0, "no answers while paused");
        Check.True(s.M.Status.StartsWith("Paused: the macro is running"), s.M.Status);

        var t = new Shop(confirm: true);
        t.ReadyForPayment();
        t.Pay(TradeShopCurrencyTable.MmdWcid, 4);
        t.Tick();
        Check.NotNull(t.M.Prompt, "prompt up");
        t.G.Pause = "in combat mode";
        t.Tick(3);
        Check.Null(t.M.Prompt, "sale called off");
        Check.True(t.G.Actions.Contains("close"), "trade closed");
        Check.True(t.TellTo("Bob", "called off"), "the buyer is told");
        t.M.RequestAccept();
        t.Tick();
        Check.Eq(t.G.Count("accept"), 0, "nothing to accept");
    }

    private static void OnOff()
    {
        var s = new Shop();
        s.Cfg.Enabled = false;
        s.TellFrom("Bob", "!list");
        s.Tick(3);
        Check.Eq(s.G.Tells.Count, 0, "off: no answers");
        s.M.SetEnabled(true);
        s.Tick();
        Check.True(s.Cfg.Enabled, "on");
        Check.True(s.Chatted("Trade Shop ON") && s.Chatted("checks their payment by item type") && s.Chatted("Confirm each sale is ON"),
            "turning it on says what it does");
        s.TellFrom("Bob", "!list");
        s.Tick(3);
        Check.True(s.TellTo("Bob", "Price sheet"), "on: the price sheet");
    }

    // ── Limits ──────────────────────────────────────────────────────────

    private static void InboundLimits()
    {
        var l = new TradeShopTellLimiter();
        long t = 1_000_000;
        Check.Eq(l.OnIncoming("Bob", t), TradeShopTellLimiter.Verdict.Accept, "first");
        Check.Eq(l.OnIncoming("Bob", t + 1_000), TradeShopTellLimiter.Verdict.TooFast, "1 s later: too fast");
        Check.Eq(l.OnIncoming("Bob", t + 2_100), TradeShopTellLimiter.Verdict.Accept, "2 s after the last accepted one");
        Check.Eq(l.OnIncoming("Eve", t + 2_200), TradeShopTellLimiter.Verdict.Accept, "another buyer has their own gap");

        // 20 lines a minute: the 21st starts a 2-minute ignore.
        var m = new TradeShopTellLimiter();
        TradeShopTellLimiter.Verdict v = default;
        for (int i = 0; i < 21; i++) v = m.OnIncoming("Bob", t + i * 2_500);
        Check.Eq(v, TradeShopTellLimiter.Verdict.NowIgnored, "21 lines in a minute: ignored");
        Check.True(m.IsIgnored("Bob", t + 60_000), "still ignored");
        Check.Eq(m.OnIncoming("Bob", t + 60_000), TradeShopTellLimiter.Verdict.Ignored, "dropped");
        Check.Eq(m.OnIncoming("Bob", t + 21 * 2_500 + TradeShopTellLimiter.SpamIgnoreMs), TradeShopTellLimiter.Verdict.Accept, "after the ignore");

        // Five too-fast lines in a minute: ignored.
        var n = new TradeShopTellLimiter();
        n.OnIncoming("Bob", t);
        for (int i = 1; i <= 5; i++) v = n.OnIncoming("Bob", t + i * 100);
        Check.Eq(v, TradeShopTellLimiter.Verdict.NowIgnored, "spam: ignored");

        // Through the shop: a flood gets one price sheet and one notice, not one per line.
        var s = new Shop();
        for (int i = 0; i < 30; i++) s.M.OnTell("Bob tells you, \"!list\"", isIncomingTell: true);
        s.Tick(40);
        int sheets = s.G.Tells.Count(x => x.To == "Bob" && x.Text.StartsWith("Rates:", StringComparison.Ordinal));
        Check.Eq(sheets, 1, "one sheet for a flood");
        Check.True(s.G.Tells.Count(x => x.To == "Bob") <= TradeShopTellLimiter.MaxOutPerBuyerPerMinute + 1, "per-buyer cap");
        Check.True(s.TellTo("Bob", "ignore you"), "told once that they're ignored");
        Check.Eq(s.G.Tells.Count(x => x.Text.Contains("ignore you")), 1, "once");
    }

    private static void OutboundLimits()
    {
        var l = new TradeShopTellLimiter();
        long t = 5_000_000;
        int bob = 0;
        for (int i = 0; i < 20; i++) if (l.TryTakeOut("Bob", t + i)) bob++;
        Check.Eq(bob, TradeShopTellLimiter.MaxOutPerBuyerPerMinute, "12 a minute to one buyer");
        Check.True(l.TryTakeOut("Bob", t + 30, overBuyerCap: true), "a sale line still goes");
        int all = bob + 1;
        for (int i = 0; i < 100; i++) if (l.TryTakeOut("P" + (char)('a' + i % 26) + (char)('a' + i / 26), t + 40 + i)) all++;
        Check.Eq(all, TradeShopTellLimiter.MaxOutPerMinute, "30 a minute in all");
        Check.True(l.TryTakeOut("Zed", t + 60_100), "a minute later there's room again");

        // Through the shop: lines go 1.2 s apart, and never more than 30 in a minute.
        var s = new Shop();
        for (int b = 0; b < 15; b++)
        {
            string who = "Buyer" + (char)('a' + b);
            s.M.OnTell($"{who} tells you, \"!list\"", isIncomingTell: true);
        }
        Check.True(CountQueuedIn(s) <= TradeShopTellLimiter.MaxInQueue, "inbound queue capped");
        long start = s.G.Now;
        s.Tick(60);   // 60 ticks of 1.3 s = 78 s
        int firstMinute = s.G.Tells.Count;
        Check.True(firstMinute <= TradeShopTellLimiter.MaxOutPerMinute * 2, "at most 30 a minute: " + firstMinute);

        var q = new Shop();
        for (int i = 0; i < 100; i++) q.M.OnTell("Bob tells you, \"!list\"", isIncomingTell: true);
        Check.True(CountQueuedIn(q) == TradeShopTellLimiter.MaxInQueue, "20 queued, the rest dropped");
        _ = start;
    }

    private static int CountQueuedIn(Shop s)
    {
        var f = typeof(TradeShopManager).GetField("_inCount", BindingFlags.NonPublic | BindingFlags.Instance);
        return (int)f!.GetValue(s.M)!;
    }

    private static void SheetCapped()
    {
        var list = new List<TradeShopListing>();
        for (int i = 0; i < 60; i++) list.Add(new TradeShopListing { Name = "Item number " + i + " with a long name", PricePyreals = 1000 + i });
        var chunks = TradeShopMessages.PriceSheet(list, new TradeShopRates(), includeRates: true);
        Check.Eq(chunks.Count, TradeShopMessages.MaxSheetChunks, "capped at 4");
        Check.True(chunks.All(c => c.Length <= TradeShopMessages.MaxLineChars), "each tell-sized");
        Check.True(chunks[^1].Contains("more") && chunks[^1].Contains("!price"), "the last line says how many more: " + chunks[^1]);
        Check.Eq(TradeShopMessages.Clip(new string('x', 500)).Length, TradeShopMessages.MaxLineChars, "a long line is clipped");
        Check.False(TradeShopMessages.Clip("a\nb").Contains('\n'), "no line breaks");
    }

    private static void Defaults()
    {
        var c = new TradeShopSettings();
        Check.False(c.Enabled, "off");
        Check.True(c.ConfirmEachSale, "confirm each sale on");
        Check.False(c.AdvertiseEnabled, "no advertising");
        Check.Eq(c.AdvertiseChannel, TradeShopAdvertiseChannel.Trade, "Trade channel");
        Check.True(c.AdvertiseIntervalSec >= 300, "interval at least 5 min");
        Check.Eq(TradeShopSettings.ClampAdvertiseSec(30), 300, "30 s clamps to 300");
        Check.Eq(c.Mode, TradeShopMode.Mode1Stock, "Mode 1");
        Check.True(typeof(TradeShopSettings).GetField(nameof(TradeShopSettings.Enabled))!.GetCustomAttribute<JsonIgnoreAttribute>() != null,
            "on/off is never saved");
        var m = new TradeShopManager(new FakeShopGame(), () => c);
        m.Tick();
        Check.Eq(m.Status, "Off", "a new shop is off");
    }

    private static void Advertising()
    {
        var s = new Shop();
        s.Tick(500);   // ~11 minutes
        Check.Eq(s.G.Said.Count, 0, "off by default: never advertises");
        s.Cfg.AdvertiseEnabled = true;
        s.Cfg.AdvertiseIntervalSec = 30;   // clamped to 300
        s.Tick(200);   // ~4.3 minutes
        Check.Eq(s.G.Said.Count, 0, "nothing before a full (5 min) interval");
        s.Tick(40);
        Check.Eq(s.G.Said.Count, 1, "one advert after 5 minutes");
        Check.True(s.G.Said[0].StartsWith("/ct [Trade Shop] Selling: Sword"), s.G.Said[0]);
        s.Cfg.AdvertisePrefix = "/quit WTS";
        string line = TradeShopManager.AdvertiseLine(s.Cfg, s.Cfg.ForSale());
        Check.True(line.StartsWith("quit WTS"), "a leading slash is dropped: " + line);
        s.M.RequestAdvertise();
        s.Tick();
        s.M.RequestAdvertise();
        s.Tick();
        Check.Eq(s.G.Said.Count, 2, "Advertise now, then not again within a minute");
    }
}

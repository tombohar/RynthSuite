using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.RynthDefective.Model;
using RynthCore.Plugin.RynthDefective.Quantity;
using RynthCore.PluginSdk;

namespace RynthDefective.Tests
{
    // The quantity box (2026-10-30): how many of a stack or of a vendor's item, and Split / Give / Buy
    // that many (Model/Quantity.cs, Quantity/QuantityController.cs). The controller runs against a
    // scripted world, so every refusal and every answer is checked without the game.
    internal static partial class Program
    {
        private const uint QSack = 0x50000100, QSack2 = 0x50000101, QScarabs = 0x50000200, QGem = 0x50000201, QArrows = 0x50000202,
            QNpc = 0x50000300, QOther = 0x50000301, QChest = 0x50000400, QChestStack = 0x50000401, QGround = 0x50000402,
            QVendor = 0x50000500, QVScarab = 0x50000501, QVNote = 0x50000502, QVPack = 0x50000503, QVScroll = 0x50000504,
            QMonster = 0x50000302;

        internal sealed class FakeQtyWorld : IQtyWorld
        {
            public sealed class Obj
            {
                public string Name = string.Empty;
                public uint Container, Wielder;
                public int Stack;
                public bool Creature, Monster;
            }

            public long NowMs { get; set; } = 1_000_000;
            public uint PlayerId { get; set; } = Me;
            public uint SelectedId { get; set; }
            public uint PreviousSelectedId { get; set; }
            public uint OpenContainerId { get; set; }
            public readonly Dictionary<uint, Obj> Objects = new();
            public readonly List<QtyPack> PackList = new();
            public bool CanSplit { get; set; } = true;
            public bool CanGive { get; set; } = true;
            public bool CanTrade { get; set; } = true;
            public bool SendFails;
            public readonly List<string> Sent = new();
            public int RefusalSeq { get; set; }
            public uint RefusalError, RefusalEvent, RefusalObject;
            public VendorInfo? Vendor;
            public readonly List<VendorItem> Items = new();
            public uint NextRequest = 40;
            public VendorTradeStatus? Status;
            public string? BuyRefusedWith;
            public int FreeContainers = 3;
            public long Burden = -1;
            public VendorBank BankInfo = VendorBank.None;

            public bool TryGetName(uint id, out string name)
            {
                name = Objects.TryGetValue(id, out Obj? o) ? o.Name : string.Empty;
                return name.Length > 0;
            }
            public bool Exists(uint id) => Objects.ContainsKey(id);
            public bool CanReceive(uint id) => Objects.TryGetValue(id, out Obj? o) && o.Creature && !o.Monster;
            public bool TryGetOwner(uint id, out uint container, out uint wielder)
            {
                container = wielder = 0;
                if (!Objects.TryGetValue(id, out Obj? o)) return false;
                container = o.Container;
                wielder = o.Wielder;
                return true;
            }
            public int StackOf(uint id) => Objects.TryGetValue(id, out Obj? o) ? o.Stack : 0;
            public IReadOnlyList<QtyPack> Packs()
            {
                var list = new List<QtyPack>(PackList);
                if (OpenContainerId != 0 && Objects.TryGetValue(OpenContainerId, out Obj? c))
                    list.Add(new QtyPack(OpenContainerId, c.Name, 120, 30, QtyPackKind.Open));
                return list;
            }
            public bool Split(uint id, uint into, int n)
            {
                if (SendFails) return false;
                Sent.Add($"split {id:X} into {into:X} {n}");
                return true;
            }
            public bool Give(uint id, uint to, int n)
            {
                if (SendFails) return false;
                Sent.Add($"give {id:X} to {to:X} {n}");
                return true;
            }
            public bool LastRefusal(out uint error, out uint eventType, out uint objectId)
            {
                error = RefusalError;
                eventType = RefusalEvent;
                objectId = RefusalObject;
                return RefusalSeq != 0;
            }
            public void Refuse(uint error, uint eventType, uint objectId)
            {
                RefusalSeq++;
                RefusalError = error;
                RefusalEvent = eventType;
                RefusalObject = objectId;
            }
            public bool TryGetVendor(out VendorInfo info)
            {
                info = Vendor!;
                return CanTrade && Vendor != null;
            }
            public IReadOnlyList<VendorItem> VendorItems() => Vendor != null ? Items : Array.Empty<VendorItem>();
            public uint Buy(uint vendorId, uint itemId, int n)
            {
                if (BuyRefusedWith != null)
                {
                    Status = new VendorTradeStatus { RequestId = 0, State = VendorTradeState.Refused, Message = BuyRefusedWith, IsBuy = true };
                    return 0;
                }
                uint id = NextRequest++;
                Sent.Add($"buy {itemId:X} from {vendorId:X} {n}");
                Status = new VendorTradeStatus { RequestId = id, State = VendorTradeState.Queued, IsBuy = true, VendorId = vendorId, EntryCount = 1 };
                return id;
            }
            public bool TryGetTradeStatus(out VendorTradeStatus status)
            {
                status = Status!;
                return Status != null;
            }
            public int FreeContainerSlots() => FreeContainers;
            public long FreeBurden() => Burden;
            public VendorBank Bank() => BankInfo;
            public void Advance(long ms) => NowMs += ms;
        }

        /// <summary>A character with a main pack (52 free), a sack (14 free), 100 scarabs, a gem, wielded arrows and an NPC nearby.</summary>
        private static FakeQtyWorld QWorld()
        {
            var w = new FakeQtyWorld();
            w.PackList.Add(new QtyPack(Me, "Main pack", 102, 50, QtyPackKind.Main));
            w.PackList.Add(new QtyPack(QSack, "Sack", 24, 10, QtyPackKind.Side));
            w.Objects[Me] = new FakeQtyWorld.Obj { Name = "Me", Creature = true };
            w.Objects[QSack] = new FakeQtyWorld.Obj { Name = "Sack", Container = Me };
            w.Objects[QScarabs] = new FakeQtyWorld.Obj { Name = "Mana Scarab", Container = QSack, Stack = 100 };
            w.Objects[QGem] = new FakeQtyWorld.Obj { Name = "Sunstone", Container = Me, Stack = 1 };
            w.Objects[QArrows] = new FakeQtyWorld.Obj { Name = "Arrow", Container = 0, Wielder = Me, Stack = 200 };
            w.Objects[QNpc] = new FakeQtyWorld.Obj { Name = "Shopkeeper", Creature = true };
            w.Objects[QOther] = new FakeQtyWorld.Obj { Name = "Guard", Creature = true };
            w.Objects[QMonster] = new FakeQtyWorld.Obj { Name = "Drudge Skulker", Creature = true, Monster = true };
            w.Objects[QGround] = new FakeQtyWorld.Obj { Name = "Pyreal", Container = 0, Stack = 50 };
            w.Objects[QChest] = new FakeQtyWorld.Obj { Name = "Chest" };
            w.Objects[QChestStack] = new FakeQtyWorld.Obj { Name = "Prismatic Taper", Container = QChest, Stack = 40 };
            return w;
        }

        /// <summary>A vendor (sell rate 1.2) with scarabs (10 each, stacks of 100), a promissory note, a pack and 5 scrolls a player sold.</summary>
        private static void QVendorOpen(FakeQtyWorld w, int coins = 1000)
        {
            w.Vendor = new VendorInfo { VendorId = QVendor, Name = "Shopkeeper", SellRate = 1.2f, BuyRate = 0.8f, PlayerCoins = coins,
                AltCurrencyHave = -1, TradingAvailable = true, Generation = 1, ItemCount = 4 };
            w.Items.Clear();
            w.Items.Add(new VendorItem { ObjectId = QVScarab, Name = "Mana Scarab", ItemType = 0x80, Amount = -1, StackSize = 1, MaxStackSize = 100,
                Value = 10, UnitValue = 10, UnitPrice = 12, Burden = 5, Unlimited = true, Stackable = true });
            w.Items.Add(new VendorItem { ObjectId = QVNote, Name = "Trade Note (100)", ItemType = VendorQuote.ItemTypePromissoryNote, Amount = -1,
                StackSize = 1, MaxStackSize = 100, Value = 100, UnitValue = 100, UnitPrice = 115, Unlimited = true, Stackable = true });
            w.Items.Add(new VendorItem { ObjectId = QVPack, Name = "Sack", ItemType = 0x200, Amount = -1, StackSize = 1, MaxStackSize = 0,
                Value = 50, UnitValue = 50, UnitPrice = 60, Burden = 20, Unlimited = true, NeedsContainerSlot = true });
            w.Items.Add(new VendorItem { ObjectId = QVScroll, Name = "Scroll of Sneak Attack", ItemType = 0x8000, Amount = 5, StackSize = 5, MaxStackSize = 0,
                Value = 400, UnitValue = 80, UnitPrice = 96, Burden = 25 });
        }

        private static (QuantityController C, List<(bool Ok, string Text)> Said) QController()
        {
            var said = new List<(bool, string)>();
            var c = new QuantityController();
            c.Report = (ok, text) => said.Add((ok, text));
            return (c, said);
        }

        // ── The rules ───────────────────────────────────────────────────────

        private static void QtyParse()
        {
            Check(QuantityRules.TryParse("25", out QtyAmount a, out _) && a == QtyAmount.Of(25), "25");
            Check(QuantityRules.TryParse(" 1,000 ", out a, out _) && a.Number == 1000, "1,000 with a comma");
            Check(QuantityRules.TryParse("MAX", out a, out _) && a == QtyAmount.Max, "max");
            Check(QuantityRules.TryParse("all", out a, out _) && a == QtyAmount.Max, "all is max");
            Check(QuantityRules.TryParse("half", out a, out _) && a == QtyAmount.Half, "half");
            Check(QuantityRules.TryParse("1/2", out a, out _) && a == QtyAmount.Half, "1/2 is half");
            Check(!QuantityRules.TryParse("0", out _, out string why) && why.Contains("at least 1", StringComparison.Ordinal), "0: at least 1");
            Check(!QuantityRules.TryParse("-3", out _, out why) && why.Contains("at least 1", StringComparison.Ordinal), "negative: at least 1");
            Check(!QuantityRules.TryParse("99999999", out _, out why) && why.Contains("8,388,607", StringComparison.Ordinal), "too many for one request: the limit said");
            Check(!QuantityRules.TryParse("lots", out _, out why) && why.Contains("max or half", StringComparison.Ordinal), "a word: what it takes");
            Check(!QuantityRules.TryParse("", out _, out why) && why.StartsWith("How many", StringComparison.Ordinal), "nothing: how many");
            Eq(QtyAmount.Of(25).ToString(), "25", "shown as typed");
            Eq(QtyAmount.Half.ToString(), "half", "half shown");

            Check(QuantityRules.LooksLikeAmount("25") && QuantityRules.LooksLikeAmount("max") && QuantityRules.LooksLikeAmount("-3")
                  && !QuantityRules.LooksLikeAmount("Sack") && !QuantityRules.LooksLikeAmount("into"), "what reads as a quantity");
            Check(QuantityRules.TryParseLeading("25 into Sack", out QtyAmount? q, out string rest, out _) && q == QtyAmount.Of(25) && rest == "into Sack",
                "a quantity, then the rest");
            Check(QuantityRules.TryParseLeading("Mana Scarab", out q, out rest, out _) && q == null && rest == "Mana Scarab", "no quantity: all of it is the rest");
            Check(QuantityRules.TryParseLeading("", out q, out rest, out _) && q == null && rest.Length == 0, "nothing at all");
            Check(!QuantityRules.TryParseLeading("0 Sack", out _, out _, out why) && why.Contains("at least 1", StringComparison.Ordinal), "a wrong quantity first: why");
        }

        private static void QtyCounts()
        {
            // Split: one always stays.
            Eq(QuantityRules.SplitCount(25, 100), 25, "split 25 of 100");
            Eq(QuantityRules.SplitCount(100, 100), 99, "split the whole stack: one stays");
            Eq(QuantityRules.SplitCount(500, 100), 99, "more than the stack: clamped");
            Eq(QuantityRules.SplitCount(0, 100), 1, "below one: one");
            Eq(QuantityRules.SplitCount(5, 1), 0, "one item: no split");
            Eq(QuantityRules.SplitCount(1, 2), 1, "a stack of two: one");
            Eq(QuantityRules.ForSplit(QtyAmount.Max, 100), 99, "split max: stack - 1");
            Eq(QuantityRules.ForSplit(QtyAmount.Half, 100), 50, "split half of 100");
            Eq(QuantityRules.ForSplit(QtyAmount.Half, 3), 1, "split half of 3");
            Eq(QuantityRules.ForSplit(QtyAmount.Half, 2), 1, "split half of 2");
            Eq(QuantityRules.ForSplit(QtyAmount.Max, 1), 0, "split max of one item: nothing");
            // Give: up to the whole stack.
            Eq(QuantityRules.GiveCount(150, 100), 100, "give more than the stack: the stack");
            Eq(QuantityRules.ForGive(QtyAmount.Max, 100), 100, "give max: the whole stack");
            Eq(QuantityRules.ForGive(QtyAmount.Half, 7), 3, "give half of 7");
            Eq(QuantityRules.ForGive(QtyAmount.Of(1), 1), 1, "give the one item");
            // The box: a typed number stays; max and half are of the selection.
            Eq(QuantityRules.ForBox(QtyAmount.Of(500), 100), 500, "the box keeps a typed number");
            Eq(QuantityRules.ForBox(QtyAmount.Max, 100), 100, "box max: the whole stack");
            Eq(QuantityRules.ForBox(QtyAmount.Half, 100), 50, "box half");
            Eq(QuantityRules.ForBox(QtyAmount.Half, 1), 1, "half of one is one");
            Eq(QuantityRules.ForBox(QtyAmount.Max, 0), 0, "max of nothing");
            Eq(QuantityRules.ForBuy(QtyAmount.Of(30), 10), 30, "a buy asks for exactly what was typed (the quote says why not)");
            Eq(QuantityRules.ForBuy(QtyAmount.Max, 83), 83, "buy max");
            Eq(QuantityRules.ForBuy(QtyAmount.Half, 83), 41, "buy half");
            Eq(QuantityRules.ForBuy(QtyAmount.Max, 0), 0, "buy max with nothing affordable");
            Eq(QuantityRules.Clamp(7, 0), 1, "clamp with nothing to clamp to");

            var (c, _) = QController();
            c.Value = 0;
            Eq(c.Value, 1, "the box is at least 1");
            c.Value = int.MaxValue;
            Eq(c.Value, QuantityRules.MaxAmount, "and at most one request");
        }

        private static VendorQuote Scarabs(long funds = 1000, int freeSlots = 50, long freeBurden = -1) => new()
        {
            Name = "Mana Scarab", ItemType = 0x80, UnitValue = 10, UnitPrice = 12, ListingValue = 10, UnitBurden = 5, ListingBurden = 5,
            MaxStack = 100, Unlimited = true, SellRate = 1.2f, Funds = funds, FreeItemSlots = freeSlots, FreeContainerSlots = 3, FreeBurden = freeBurden,
        };

        private static void QtyVendorPrice()
        {
            // Stackable: each created stack is priced on its own (ACE: max(1, ceil(value x rate - 0.1))).
            VendorQuote q = Scarabs();
            q.Estimate(250, out long cost, out long slots, out long burden);
            Eq(cost, 3000L, "250 scarabs: two stacks of 100 (1,200 each) and one of 50 (600)");
            Eq(slots, 3L, "three stacks, three slots");
            Eq(burden, 1250L, "burden by the unit");
            Eq(q.Cost(25), 300L, "25 at 12");
            Eq(q.Cost(1), 12L, "one");
            Eq(q.PriceLine(25), "300 pyreals · you have 1,000", "the price line");
            Eq(q.Cost(0), 0L, "nothing costs nothing");

            // Not stackable: one per slot.
            var wand = new VendorQuote { Name = "Wand", UnitValue = 50, ListingValue = 50, MaxStack = 0, Unlimited = true, SellRate = 1.15f, Funds = 1000, FreeItemSlots = 10 };
            wand.Estimate(3, out cost, out slots, out _);
            Eq(cost, 174L, "three wands at ceil(57.5 - 0.1) = 58");
            Eq(slots, 3L, "a slot each");

            // A promissory note costs 1.15 of its value whatever the vendor's rate.
            var note = new VendorQuote { Name = "Trade Note", ItemType = VendorQuote.ItemTypePromissoryNote, UnitValue = 100, MaxStack = 100,
                Unlimited = true, SellRate = 2.0f, Funds = 10_000, FreeItemSlots = 10 };
            Eq(note.Cost(1), 115L, "a note at 1.15");

            // Rounding: never below one, and the -0.1 before the ceiling.
            Eq(new VendorQuote { UnitValue = 1, MaxStack = 0, Unlimited = true, SellRate = 0.5f }.Cost(1), 1L, "at least one pyreal");
            Eq(new VendorQuote { UnitValue = 9, MaxStack = 0, Unlimited = true, SellRate = 1.1f }.Cost(1), 10L, "9 x 1.1 = 9.9 -> 10");

            // A limited listing (another player's sale, handed over whole) costs at least the whole listing.
            var scrolls = new VendorQuote { Name = "Scroll", UnitValue = 80, ListingValue = 400, UnitBurden = 5, ListingBurden = 25, MaxStack = 0,
                Unlimited = false, Stock = 5, SellRate = 1.0f, Funds = 1000, FreeItemSlots = 10 };
            Eq(scrolls.Cost(2), 400L, "two of a limited five: the whole listing's price");
            scrolls.Estimate(2, out _, out _, out burden);
            Eq(burden, 25L, "and its whole burden");
        }

        private static void QtyVendorMax()
        {
            Eq(Scarabs(funds: 1000).MaxBuy(), 83, "1,000 pyreals at 12: 83 (996), not 84 (1,008)");
            Eq(Scarabs(funds: 1_000_000, freeSlots: 2).MaxBuy(), 200, "two free slots: two stacks of 100");
            Eq(Scarabs(funds: 1_000_000, freeSlots: 0).MaxBuy(), 0, "a full pack: none");
            Eq(Scarabs(funds: 11).MaxBuy(), 0, "11 pyreals: not even one");
            Eq(Scarabs(funds: 1_000_000, freeSlots: 50, freeBurden: 55).MaxBuy(), 11, "burden 55 at 5 each: 11");
            var unknown = Scarabs(funds: -1, freeSlots: -1);
            Eq(unknown.MaxBuy(), 100, "nothing known: one full stack");
            Eq((unknown with { MaxStack = 0 }).MaxBuy(), 1, "nothing known, not stackable: one");
            var scrolls = new VendorQuote { Name = "Scroll", UnitValue = 80, ListingValue = 400, MaxStack = 0, Unlimited = false, Stock = 5,
                SellRate = 1.0f, Funds = 100_000, FreeItemSlots = 50 };
            Eq(scrolls.MaxBuy(), 5, "the vendor's five");
            Eq((scrolls with { Stock = 0 }).MaxBuy(), 0, "none left");
            var big = Scarabs(funds: long.MaxValue / 4, freeSlots: int.MaxValue);
            Eq(big.MaxBuy(), QuantityRules.MaxAmount, "no limit but one request's");
        }

        private static void QtyVendorRefusals()
        {
            VendorQuote q = Scarabs(funds: 1000);
            Eq(q.Refusal(25), null, "25 fit");
            Check(q.Refusal(0)!.Contains("at least 1", StringComparison.Ordinal), "zero");
            string? why = q.Refusal(100);
            Check(why != null && why.StartsWith("Not enough pyreals: 100 Mana Scarab cost 1,200, you have 1,000", StringComparison.Ordinal)
                  && why.Contains("you can buy 83", StringComparison.Ordinal), $"not enough pyreals, with how many can be bought: {why}");
            why = Scarabs(funds: 1_000_000, freeSlots: 0).Refusal(10);
            Check(why != null && why.StartsWith("The pack is full", StringComparison.Ordinal), $"the pack is full: {why}");
            why = Scarabs(funds: 1_000_000, freeSlots: 1).Refusal(250);
            Check(why != null && why.StartsWith("Not enough room in your packs: 250 Mana Scarab need 3 free slots, you have 1", StringComparison.Ordinal)
                  && why.Contains("you can buy 100", StringComparison.Ordinal), $"not enough room: {why}");
            why = Scarabs(funds: 1_000_000, freeBurden: 10).Refusal(5);
            Check(why != null && why.StartsWith("Too heavy", StringComparison.Ordinal) && why.Contains("you can buy 2", StringComparison.Ordinal), $"too heavy: {why}");
            var pack = new VendorQuote { Name = "Sack", UnitValue = 50, MaxStack = 0, Unlimited = true, SellRate = 1f, Funds = 1000,
                FreeItemSlots = 50, FreeContainerSlots = 0, NeedsContainerSlot = true };
            why = pack.Refusal(1);
            Check(why != null && why.StartsWith("No room for another pack", StringComparison.Ordinal), $"no container slot: {why}");
            Eq((pack with { FreeContainerSlots = 2 }).Refusal(2), null, "two packs in two container slots");
            var scrolls = new VendorQuote { Name = "Scroll", UnitValue = 80, ListingValue = 400, MaxStack = 0, Unlimited = false, Stock = 5,
                SellRate = 1.0f, Funds = 100_000, FreeItemSlots = 50 };
            Eq(scrolls.Refusal(25), "The vendor has only 5 Scroll.", "more than the vendor has");
            Eq((scrolls with { Stock = 0 }).Refusal(1), "The vendor has no Scroll left.", "none left");
            Check(Scarabs(funds: -1, freeSlots: -1).Refusal(5000) == null, "unknown money and room aren't held against it (the engine checks)");

            // The vendor's own currency.
            var info = new VendorInfo { VendorId = QVendor, SellRate = 1f, AltCurrencyWcid = 1234, AltCurrencyName = "Ancient Coin", AltCurrencyHave = 3, PlayerCoins = 99_999 };
            var item = new VendorItem { ObjectId = QVScarab, Name = "Relic", UnitValue = 2, Value = 2, StackSize = 1, MaxStackSize = 0, Amount = -1, Unlimited = true };
            VendorQuote alt = VendorQuote.From(info, item, 10, 2, -1);
            Eq(alt.Currency, "Ancient Coin", "the vendor's currency by name");
            Eq(alt.Funds, 3L, "counted in your packs, not pyreals");
            Check(alt.Refusal(2)!.StartsWith("Not enough Ancient Coin: 2 Relic cost 4, you have 3", StringComparison.Ordinal), "not enough of it");
            var pyInfo = new VendorInfo { VendorId = QVendor, SellRate = 1f, AltCurrencyHave = -1, PlayerCoins = 99_999 };
            VendorQuote py = VendorQuote.From(pyInfo, item, 10, 2, -1);
            Eq(py.Currency, "pyreals", "a pyreal vendor");
            Eq(py.Funds, 99_999L, "your pyreals");
            var stacked = new VendorItem { Name = "Bundle", StackSize = 5, Burden = 50, Value = 100, UnitValue = 20, Amount = 2 };
            VendorQuote sq = VendorQuote.From(info, stacked, 10, 2, -1);
            Eq(sq.UnitBurden, 10, "burden per unit from the listed stack");
            Check(!sq.Unlimited && sq.Stock == 2, "a limited listing's stock");

            // The engine's refusals, in the player's words.
            Check(QtyReplies.VendorRefusal("costs 300 pyreals, you have 200").StartsWith("Not enough pyreals: it costs 300 pyreals, you have 200", StringComparison.Ordinal), "engine: pyreals");
            Check(QtyReplies.VendorRefusal("costs 3 Ancient Coin, you have 1").StartsWith("Not enough Ancient Coin", StringComparison.Ordinal), "engine: the vendor's currency");
            Check(QtyReplies.VendorRefusal("needs 3 free pack slots, you have 0").StartsWith("The pack is full", StringComparison.Ordinal), "engine: the pack is full");
            Check(QtyReplies.VendorRefusal("needs 3 free pack slots, you have 1").StartsWith("Not enough room", StringComparison.Ordinal), "engine: not enough room");
            Check(QtyReplies.VendorRefusal("needs 1 free container slots, you have 0").StartsWith("No room for another pack", StringComparison.Ordinal), "engine: container slots");
            Check(QtyReplies.VendorRefusal("adds 500 burden, you can carry 20 more").StartsWith("Too heavy", StringComparison.Ordinal), "engine: burden");
            Eq(QtyReplies.VendorRefusal("vendor only has 5 of 0x80001234, asked for 25"), "The vendor doesn't have that many.", "engine: stock");
            Check(QtyReplies.VendorRefusal("no vendor open").Contains("closed", StringComparison.Ordinal), "engine: no vendor");
            Check(QtyReplies.VendorRefusal("vendor window is closed").Contains("closed", StringComparison.Ordinal), "engine: closed before sending");
            Eq(QtyReplies.VendorRefusal("0x80001234 is not on this vendor's list"), "The vendor doesn't sell that any more.", "engine: not on the list");
            Eq(QtyReplies.VendorRefusal("4 requests already waiting"), QtyReplies.VendorBusy, "engine: busy");
            Eq(QtyReplies.VendorRefusal("something new"), "The vendor didn't sell it: something new.", "engine: anything else as it is");
            Eq(QtyReplies.VendorRefusal(null), "The vendor didn't sell it.", "engine: no message");
        }

        // ── The commands ────────────────────────────────────────────────────

        private static void QtyCommands()
        {
            DefCommand c = DefCommandParser.Parse("qty");
            Check(c.Verb == DefVerb.Quantity && c.Amount == null, "qty alone: say it");
            c = DefCommandParser.Parse("qty 25");
            Check(c.Verb == DefVerb.Quantity && c.Amount == QtyAmount.Of(25), "qty 25");
            Check(DefCommandParser.Parse("quantity max").Amount == QtyAmount.Max, "quantity max");
            c = DefCommandParser.Parse("qty 0");
            Check(c.Verb == DefVerb.Error && c.Error.Contains("Usage", StringComparison.Ordinal), "qty 0: why and the usage");
            c = DefCommandParser.Parse("qty 25 30");
            Check(c.Verb == DefVerb.Error, "qty takes one quantity");

            c = DefCommandParser.Parse("split");
            Check(c.Verb == DefVerb.Split && c.Amount == null && c.Text.Length == 0, "split: the box's number into the chosen pack");
            c = DefCommandParser.Parse("split 25 into Sack");
            Check(c.Verb == DefVerb.Split && c.Amount == QtyAmount.Of(25) && c.Text == "Sack", "split 25 into Sack");
            c = DefCommandParser.Parse("split half to main");
            Check(c.Amount == QtyAmount.Half && c.Text == "main", "split half to main");
            c = DefCommandParser.Parse("split into Inventory Bag");
            Check(c.Amount == null && c.Text == "Inventory Bag", "split into a pack whose name starts like 'in'");
            Check(DefCommandParser.Parse("split -2").Verb == DefVerb.Error, "split -2: why");

            c = DefCommandParser.Parse("give 10");
            Check(c.Verb == DefVerb.Give && c.Amount == QtyAmount.Of(10), "give 10");
            Check(DefCommandParser.Parse("give").Amount == null, "give: the box's number");
            c = DefCommandParser.Parse("give someone");
            Check(c.Verb == DefVerb.Error && c.Error.Contains("Usage", StringComparison.Ordinal), "give a name: the usage (it goes to the last one selected)");

            c = DefCommandParser.Parse("buy 25 Mana Scarab");
            Check(c.Verb == DefVerb.Buy && c.Amount == QtyAmount.Of(25) && c.Text == "Mana Scarab", "buy 25 Mana Scarab");
            c = DefCommandParser.Parse("buy Mana Scarab");
            Check(c.Verb == DefVerb.Buy && c.Amount == null && c.Text == "Mana Scarab", "buy by name, the box's number");
            Check(DefCommandParser.Parse("buy 25 of Mana Scarab").Text == "Mana Scarab", "buy 25 of ...");
            Check(DefCommandParser.Parse("buy max").Amount == QtyAmount.Max && DefCommandParser.Parse("buy").Amount == null, "buy max, buy");
            Check(DefCommandParser.Parse("buy 0 Mana Scarab").Verb == DefVerb.Error, "buy 0: why");
            Check(DefCommandParser.Usage.Contains("qty", StringComparison.Ordinal) && DefCommandParser.Usage.Contains("buy", StringComparison.Ordinal), "in the usage");
        }

        // ── Split ───────────────────────────────────────────────────────────

        private static void QtySplit()
        {
            var w = QWorld();
            var (c, said) = QController();

            // What can't be split, each with its reason.
            c.Split(w, null, "");
            Eq(said.Last(), (false, QtyReplies.NothingSelected), "nothing selected");
            w.SelectedId = QGem;
            c.Split(w, null, "");
            Eq(said.Last(), (false, "That's not a stack: Sunstone is a single item."), "a single item");
            w.SelectedId = QArrows;
            c.Split(w, null, "");
            Eq(said.Last(), (false, "Arrow is wielded: take it off first."), "wielded arrows");
            w.SelectedId = QGround;
            c.Split(w, null, "");
            Eq(said.Last(), (false, "Pyreal isn't in your packs or in the container you have open."), "a stack on the ground");
            Eq(w.Sent.Count, 0, "nothing sent for any of them");

            // 25 of 100 into the main pack, answered by the smaller stack.
            w.SelectedId = QScarabs;
            c.Value = 25;
            c.Split(w, null, "");
            Eq(w.Sent.Last(), $"split {QScarabs:X} into {Me:X} 25", "the box's 25 into the main pack");
            Check(c.Busy, "waiting for the game");
            c.Split(w, null, "");
            Eq(said.Last(), (false, QtyReplies.Busy), "one at a time");
            w.Advance(300);
            c.Tick(w);
            Check(c.Busy, "no change yet");
            w.Objects[QScarabs].Stack = 75;
            c.Tick(w);
            Eq(said.Last(), (true, "Split 25 Mana Scarab into the main pack."), "the answer");
            Check(!c.Busy, "done");
            Check(c.StatusAt(w.NowMs) is { Ok: true }, "the window's line says it");
            Check(c.StatusAt(w.NowMs + QuantityController.StatusShownMs) == null, "and stops after a while");

            // More than the stack: one stays.
            c.Value = 500;
            c.Split(w, null, "");
            Eq(w.Sent.Last(), $"split {QScarabs:X} into {Me:X} 74", "500 of 75: 74 (one stays)");
            w.Objects[QScarabs].Stack = 1;
            c.Tick(w);
            Eq(said.Last(), (true, "Split 74 Mana Scarab into the main pack."), "74 split");
            w.Objects[QScarabs].Stack = 100;

            // A typed amount and a named pack.
            c.Split(w, QtyAmount.Half, "sack");
            Eq(w.Sent.Last(), $"split {QScarabs:X} into {QSack:X} 50", "half into the sack (by name, any case)");
            w.Objects[QScarabs].Stack = 50;
            c.Tick(w);
            Eq(said.Last(), (true, "Split 50 Mana Scarab into Sack."), "into Sack");
            w.Objects[QScarabs].Stack = 100;
            c.Split(w, QtyAmount.Of(5), "Bag");
            Eq(said.Last(), (false, "No pack called 'Bag'. Your packs: Main pack, Sack."), "no such pack: the packs");
            c.Split(w, QtyAmount.Of(5), "open");
            Check(!said.Last().Ok && said.Last().Text.StartsWith("No chest", StringComparison.Ordinal), "no container open");

            // The pack chosen in the window; a full one refuses before anything is sent.
            c.Into = QSack;
            c.Split(w, QtyAmount.Of(5), "");
            Eq(w.Sent.Last(), $"split {QScarabs:X} into {QSack:X} 5", "the window's pack");
            c.Tick(w);   // not answered yet
            w.Advance(QuantityController.SplitAnswerMs);
            c.Tick(w);
            Eq(said.Last(), (false, QtyReplies.NoAnswer("split")), "no answer in time");
            c.Into = 0;
            w.PackList[0] = new QtyPack(Me, "Main pack", 102, 102, QtyPackKind.Main);
            int sent = w.Sent.Count;
            c.Split(w, QtyAmount.Of(5), "");
            Check(!said.Last().Ok && said.Last().Text.StartsWith("The main pack is full.", StringComparison.Ordinal), "the main pack is full");
            Eq(w.Sent.Count, sent, "nothing sent to a full pack");
            c.Into = 0xDEAD;   // a pack that's gone: the main pack
            c.Split(w, QtyAmount.Of(5), "");
            Check(said.Last().Text.StartsWith("The main pack is full.", StringComparison.Ordinal), "a gone pack is the main pack");
            w.PackList[0] = new QtyPack(Me, "Main pack", 102, 50, QtyPackKind.Main);
            c.Into = 0;

            // From the open chest into the pack.
            w.OpenContainerId = QChest;
            w.SelectedId = QChestStack;
            c.Split(w, QtyAmount.Of(10), "");
            Eq(w.Sent.Last(), $"split {QChestStack:X} into {Me:X} 10", "10 of the chest's 40 into the main pack");
            w.Objects[QChestStack].Stack = 30;
            c.Tick(w);
            Check(said.Last().Ok, "taken");
            w.SelectedId = QScarabs;
            c.Split(w, QtyAmount.Of(10), "chest");
            Eq(w.Sent.Last(), $"split {QScarabs:X} into {QChest:X} 10", "and 10 of yours into the open chest");
            w.Objects[QScarabs].Stack = 90;
            c.Tick(w);
            Eq(said.Last(), (true, "Split 10 Mana Scarab into Chest."), "into the chest");
            w.Objects[QScarabs].Stack = 100;
            w.OpenContainerId = 0;

            // The server's refusals: one naming the item at once; another item's is not ours; any other after a grace.
            c.Split(w, QtyAmount.Of(5), "");
            w.Refuse(0x0001, 0x00A0, QScarabs);
            c.Tick(w);
            Eq(said.Last(), (false, "The server refused the split (error 0x0001): nothing changed."), "refused, naming the item");
            c.Split(w, QtyAmount.Of(5), "");
            w.Refuse(0x0001, 0x00A0, QGem);
            c.Tick(w);
            Check(c.Busy, "another item's refusal isn't ours");
            w.Refuse(0x0402, 0x028A, 0);
            c.Tick(w);
            Check(c.Busy, "a refusal that names nothing waits for the grace");
            w.Advance(QuantityController.RefusalGraceMs);
            c.Tick(w);
            Eq(said.Last(), (false, "The server refused the split (error 0x0402): nothing changed."), "then it's the split's");

            // Can't send, or no engine call.
            w.SendFails = true;
            c.Split(w, QtyAmount.Of(5), "");
            Eq(said.Last(), (false, QtyReplies.NotSent("split")), "the send failed");
            Check(!c.Busy, "nothing waits");
            w.SendFails = false;
            w.CanSplit = false;
            c.Split(w, QtyAmount.Of(5), "");
            Eq(said.Last(), (false, QtyReplies.NoSplitApi), "an engine without the call");
            w.CanSplit = true;

            // A vendor's item isn't split.
            QVendorOpen(w);
            w.SelectedId = QVScarab;
            c.Split(w, null, "");
            Check(!said.Last().Ok && said.Last().Text.Contains("Buy", StringComparison.Ordinal), "a vendor's item: Buy");
        }

        // ── Give ────────────────────────────────────────────────────────────

        private static void QtyGive()
        {
            var w = QWorld();
            var (c, said) = QController();
            w.SelectedId = QScarabs;
            c.Tick(w);
            c.Give(w, QtyAmount.Of(10));
            Eq(said.Last(), (false, QtyReplies.NoGiveTarget), "nobody selected before: who to");

            // Select the NPC, then the stack: the NPC is the target.
            w.SelectedId = QNpc;
            c.Tick(w);
            w.SelectedId = Me;   // selecting yourself doesn't change it
            c.Tick(w);
            w.SelectedId = QMonster;   // nor does a monster selected in a fight
            c.Tick(w);
            w.SelectedId = QScarabs;
            c.Tick(w);
            Check(c.TryGiveTarget(w, QScarabs, out uint to, out string toName) && to == QNpc && toName == "Shopkeeper", "the last creature selected");
            c.Give(w, QtyAmount.Of(10));
            Eq(w.Sent.Last(), $"give {QScarabs:X} to {QNpc:X} 10", "10 to the NPC");
            w.Objects[QScarabs].Stack = 90;
            c.Tick(w);
            Eq(said.Last(), (true, "Gave 10 Mana Scarab to Shopkeeper."), "given");

            // The whole stack goes as "all of it" (0), and is answered by the item leaving your packs.
            c.Give(w, QtyAmount.Max);
            Eq(w.Sent.Last(), $"give {QScarabs:X} to {QNpc:X} 0", "the whole stack: amount 0");
            w.Objects.Remove(QScarabs);
            c.Tick(w);
            Eq(said.Last(), (true, "Gave 90 Mana Scarab to Shopkeeper."), "all 90 given");
            w.Objects[QScarabs] = new FakeQtyWorld.Obj { Name = "Mana Scarab", Container = QSack, Stack = 100 };

            // More than the stack: the stack.
            c.Value = 999;
            c.Give(w, null);
            Eq(w.Sent.Last(), $"give {QScarabs:X} to {QNpc:X} 0", "999 of 100: the stack");
            w.Objects[QScarabs].Container = QNpc;
            c.Tick(w);
            Check(said.Last().Ok, "it left your packs");
            w.Objects[QScarabs].Container = QSack;

            // No answer: the server may walk you there first, so it waits longer.
            c.Give(w, QtyAmount.Of(1));
            w.Advance(QuantityController.SplitAnswerMs);
            c.Tick(w);
            Check(c.Busy, "a give waits longer than a split");
            w.Advance(QuantityController.GiveAnswerMs);
            c.Tick(w);
            Eq(said.Last(), (false, QtyReplies.NoAnswer("give")), "then gives up");

            // Refusals before sending.
            w.SelectedId = QChestStack;
            w.OpenContainerId = QChest;
            c.Give(w, null);
            Eq(said.Last(), (false, "Prismatic Taper isn't in your packs: pick it up first."), "not yours yet");
            w.SelectedId = QArrows;
            c.Give(w, null);
            Eq(said.Last(), (false, "Arrow is wielded: take it off first."), "wielded");
            w.SelectedId = QGem;
            c.Give(w, null);
            Eq(w.Sent.Last(), $"give {QGem:X} to {QNpc:X} 0", "a single item can be given (all of it)");
            w.Objects.Remove(QGem);
            c.Tick(w);
            Check(said.Last().Ok, "given");

            // The target gone: AC's previous selection if it's a creature; else nobody.
            var (c2, said2) = QController();
            w.SelectedId = QScarabs;
            w.PreviousSelectedId = QOther;
            Check(c2.TryGiveTarget(w, QScarabs, out to, out toName) && to == QOther && toName == "Guard", "AC's previous selection");
            w.PreviousSelectedId = QSack;
            Check(!c2.TryGiveTarget(w, QScarabs, out _, out _), "a pack isn't someone to give to");
            w.PreviousSelectedId = QMonster;
            Check(!c2.TryGiveTarget(w, QScarabs, out _, out _), "nor is a monster");
            w.SelectedId = QNpc;
            c2.Tick(w);
            w.SelectedId = QScarabs;
            w.Objects.Remove(QNpc);
            Check(!c2.TryGiveTarget(w, QScarabs, out _, out _), "the NPC is gone");
            w.Objects[QNpc] = new FakeQtyWorld.Obj { Name = "Shopkeeper", Creature = true };
            Check(c2.TryGiveTarget(w, QScarabs, out _, out _), "back in view");
            w.Advance(QuantityController.GiveTargetMs);
            Check(!c2.TryGiveTarget(w, QScarabs, out _, out _), "forgotten after a while");
            w.CanGive = false;
            c2.Give(w, null);
            Eq(said2.Last(), (false, QtyReplies.NoGiveApi), "an engine without the call");
        }

        // ── Buy ─────────────────────────────────────────────────────────────

        private static void QtyBuy()
        {
            var w = QWorld();
            var (c, said) = QController();
            c.Buy(w, QtyAmount.Of(25), "");
            Eq(said.Last(), (false, QtyReplies.NoVendor), "no vendor open");
            w.CanTrade = false;
            c.Buy(w, QtyAmount.Of(25), "");
            Eq(said.Last(), (false, QtyReplies.NoTradeApi), "an engine without vendor trading");
            w.CanTrade = true;

            QVendorOpen(w, coins: 1000);
            w.SelectedId = QScarabs;   // your own stack, not the vendor's
            c.Buy(w, QtyAmount.Of(25), "");
            Eq(said.Last(), (false, QtyReplies.NoVendorItem), "select the vendor's item");

            // 25 of the selected scarabs: the price checked, the request sent, the answer said.
            w.SelectedId = QVScarab;
            c.Value = 25;
            c.Buy(w, null, "");
            Eq(w.Sent.Last(), $"buy {QVScarab:X} from {QVendor:X} 25", "the box's 25");
            Check(c.Busy, "waiting");
            c.Tick(w);
            Check(c.Busy, "queued is still waiting");
            w.Status = new VendorTradeStatus { RequestId = 40, State = VendorTradeState.Done, Result = VendorTradeResult.Accepted, Estimate = 300, IsBuy = true };
            c.Tick(w);
            Eq(said.Last(), (true, "Bought 25 Mana Scarab for 300 pyreals."), "bought, with what it cost");

            // Not enough pyreals: refused before anything is sent, with how many can be bought.
            int sent = w.Sent.Count;
            c.Buy(w, QtyAmount.Of(100), "");
            Check(!said.Last().Ok && said.Last().Text.StartsWith("Not enough pyreals", StringComparison.Ordinal)
                  && said.Last().Text.Contains("you can buy 83", StringComparison.Ordinal), $"not enough pyreals: {said.Last().Text}");
            Eq(w.Sent.Count, sent, "nothing sent");

            // max: the most you can buy.
            c.Buy(w, QtyAmount.Max, "");
            Eq(w.Sent.Last(), $"buy {QVScarab:X} from {QVendor:X} 83", "buy max: 83");
            w.Status = new VendorTradeStatus { RequestId = 41, State = VendorTradeState.Refused, Message = "costs 996 pyreals, you have 990" };
            c.Tick(w);
            Check(!said.Last().Ok && said.Last().Text.StartsWith("Not enough pyreals: it costs 996", StringComparison.Ordinal), "the engine's own refusal, in words");

            // By name: whole, start, part; several or none ask again.
            c.Buy(w, QtyAmount.Of(2), "trade note");
            Eq(w.Sent.Last(), $"buy {QVNote:X} from {QVendor:X} 2", "by its whole name");
            w.Status = new VendorTradeStatus { RequestId = 42, State = VendorTradeState.Done, Result = VendorTradeResult.AnsweredNoRefresh };
            c.Tick(w);
            Check(!said.Last().Ok && said.Last().Text.Contains("the server said no", StringComparison.Ordinal), "the server answered without selling");
            c.Buy(w, QtyAmount.Of(1), "Scroll");
            Eq(w.Sent.Last(), $"buy {QVScroll:X} from {QVendor:X} 1", "by the start of its name");
            w.Status = new VendorTradeStatus { RequestId = 43, State = VendorTradeState.Done, Result = VendorTradeResult.TimedOut };
            c.Tick(w);
            Check(!said.Last().Ok && said.Last().Text.StartsWith("No answer from the vendor", StringComparison.Ordinal), "the engine's time-out");
            w.Items.Add(new VendorItem { ObjectId = 0x50000599, Name = "Mana Stone", Amount = -1, Unlimited = true, UnitValue = 5, Value = 5, StackSize = 1 });
            c.Buy(w, QtyAmount.Of(1), "Mana");
            Check(!said.Last().Ok && said.Last().Text.Contains("type more of the name", StringComparison.Ordinal), $"several match: {said.Last().Text}");
            c.Buy(w, QtyAmount.Of(1), "Dragon Egg");
            Eq(said.Last(), (false, "The vendor doesn't sell 'Dragon Egg'."), "not sold here");

            // The vendor's stock and the pack's room.
            c.Buy(w, QtyAmount.Of(6), "Scroll of Sneak Attack");
            Eq(said.Last(), (false, "The vendor has only 5 Scroll of Sneak Attack."), "only five");
            w.FreeContainers = 0;
            c.Buy(w, QtyAmount.Of(1), "Sack");
            Check(!said.Last().Ok && said.Last().Text.StartsWith("No room for another pack", StringComparison.Ordinal), "no container slot for a pack");
            w.FreeContainers = 3;
            w.PackList[0] = new QtyPack(Me, "Main pack", 102, 102, QtyPackKind.Main);
            w.PackList[1] = new QtyPack(QSack, "Sack", 24, 24, QtyPackKind.Side);
            c.Buy(w, QtyAmount.Of(1), "Mana Scarab");
            Check(!said.Last().Ok && said.Last().Text.StartsWith("The pack is full", StringComparison.Ordinal), $"the pack is full: {said.Last().Text}");
            w.PackList[0] = new QtyPack(Me, "Main pack", 102, 50, QtyPackKind.Main);
            w.PackList[1] = new QtyPack(QSack, "Sack", 24, 10, QtyPackKind.Side);

            // The engine refuses at once (0): its reason; another plugin's trade after ours; no answer.
            w.BuyRefusedWith = "vendor window is closed";
            c.Buy(w, QtyAmount.Of(1), "Mana Scarab");
            Check(!said.Last().Ok && said.Last().Text.Contains("closed", StringComparison.Ordinal), "refused at once: why");
            w.BuyRefusedWith = null;
            c.Buy(w, QtyAmount.Of(1), "Mana Scarab");
            w.Status = new VendorTradeStatus { RequestId = 99, State = VendorTradeState.Queued };
            c.Tick(w);
            Check(said.Last().Text.Contains("another trade followed", StringComparison.Ordinal), "another trade's status replaced ours");
            c.Buy(w, QtyAmount.Of(1), "Mana Scarab");
            w.Status = null;
            w.Advance(QuantityController.BuyAnswerMs);
            c.Tick(w);
            Check(!said.Last().Ok && said.Last().Text.StartsWith("No answer about 1 Mana Scarab", StringComparison.Ordinal), "no answer at all");

            // A vendor RynthCore can't read, or one with a trade going.
            w.Vendor = new VendorInfo { VendorId = QVendor, SellRate = 1.2f, PlayerCoins = 1000, TradingAvailable = false };
            c.Buy(w, QtyAmount.Of(1), "Mana Scarab");
            Eq(said.Last(), (false, QtyReplies.VendorUnreadable), "a vendor the engine can't read");
            w.Vendor = new VendorInfo { VendorId = QVendor, SellRate = 1.2f, PlayerCoins = 1000, TradingAvailable = true, TradeInFlight = true };
            c.Buy(w, QtyAmount.Of(1), "Mana Scarab");
            Eq(said.Last(), (false, QtyReplies.VendorBusy), "a trade still going");
        }

        // ── The box: /rdef qty and the window's view ────────────────────────

        private static void QtyBoxAndView()
        {
            var w = QWorld();
            var (c, said) = QController();
            c.SetBox(w, null);
            Eq(said.Last(), (true, "Quantity 1."), "/rdef qty: the number");
            c.SetBox(w, QtyAmount.Max);
            Eq(said.Last(), (false, QtyReplies.MaxNeedsSelection), "max of nothing: why");
            c.SetBox(w, QtyAmount.Of(25));
            Eq(c.Value, 25, "set");
            w.SelectedId = QScarabs;
            c.SetBox(w, QtyAmount.Half);
            Eq(c.Value, 50, "half of the selected 100");
            Eq(said.Last(), (true, "Quantity 50 (the selected Mana Scarab: a stack of 100; a split takes up to 99)."), "said with the stack");
            c.SetBox(w, QtyAmount.Max);
            Eq(c.Value, 100, "max: the whole stack");
            c.SetBox(w, QtyAmount.Of(500));
            Eq(c.Value, 500, "a typed number stays (each action clamps)");

            QVendorOpen(w, coins: 1000);
            w.SelectedId = QVScarab;
            c.SetBox(w, QtyAmount.Max);
            Eq(c.Value, 83, "max of a vendor's item: the most you can buy");
            Check(said.Last().Text.Contains("you can buy 83", StringComparison.Ordinal) && said.Last().Text.Contains("996 pyreals", StringComparison.Ordinal), $"said with the price: {said.Last().Text}");
            QVendorOpen(w, coins: 5);
            c.SetBox(w, QtyAmount.Max);
            Check(!said.Last().Ok && said.Last().Text.StartsWith("Not enough pyreals", StringComparison.Ordinal), "max with nothing affordable: why");

            // The window's view: only a stack or a vendor's item.
            w.Vendor = null;
            w.SelectedId = 0;
            Check(c.View(w) == null, "nothing selected: no box");
            w.SelectedId = QGem;
            Check(c.View(w) == null, "a single item: no box");
            w.SelectedId = QNpc;
            Check(c.View(w) == null, "a creature: no box");
            w.SelectedId = QGround;
            Check(c.View(w) == null, "a stack on the ground: no box (the split command still says why)");
            w.SelectedId = QScarabs;
            w.OpenContainerId = QChest;
            QuantityView? v = c.View(w);
            Check(v != null && v.Subject.Mode == QtyMode.Stack && v.Subject.Where == QtyWhere.Carried && v.Subject.Stack == 100, "a stack in the sack");
            Eq(string.Join(", ", v!.Targets.Select(t => t.Label)), "Main pack, Sack, Chest (open)", "the packs, the open chest last");
            Eq(v.TargetIndex, 0, "the main pack first");
            Eq(v.Target!.Value.Free, 52, "its free slots");
            c.Into = QChest;
            Eq(c.View(w)!.TargetIndex, 2, "the chosen pack");
            c.Into = 0;
            w.SelectedId = QChestStack;
            Eq(c.View(w)!.Subject.Where, QtyWhere.Open, "a stack in the open chest");
            w.SelectedId = QArrows;
            Eq(c.View(w)!.Subject.Where, QtyWhere.Wielded, "wielded arrows show the box (and why it can't split)");
            QVendorOpen(w);
            w.SelectedId = QVScarab;
            v = c.View(w);
            Check(v != null && v.Subject.Mode == QtyMode.Vendor && v.Subject.MaxBuy == 83 && v.Subject.Quote != null && v.Targets.Length == 0,
                "a vendor's item: its price and max, no packs");
            Eq(v!.Subject.BoxMax, 83, "the box's max is the most you can buy");

            c.Reset();
            Check(c.Into == 0 && !c.Busy && c.StatusAt(w.NowMs) == null, "logout forgets the action, the pack and the line");
        }

        // ── A server's bank (2026-10-31) ────────────────────────────────────
        // A player report: on a server whose bank pays vendors, the box refused a buy the pack alone
        // couldn't pay for. The server plugin's profile values say whether a bank pays and how much.

        private static void QtyBankRules()
        {
            // The values, as the active server's plugin publishes them.
            Eq(VendorBank.Read(null), VendorBank.None, "no profile: no bank");
            Eq(VendorBank.Read("{\"values\":{}}"), VendorBank.None, "no bank values: no bank");
            Eq(VendorBank.Read("{\"values\":{\"vendorBank\":0,\"vendorBankPyreals\":5000}}"), VendorBank.None, "vendorBank 0: no bank, whatever the balance");
            Eq(VendorBank.Read("not json"), VendorBank.None, "unusable text: no bank");
            VendorBank read = VendorBank.Read("{\"values\":{\"vendorBank\":1,\"vendorBankPyreals\":5000}}");
            Check(read.Pays && read.Known && read.Pyreals == 5000, "a bank that pays, with its balance");
            read = VendorBank.Read("{\"values\":{\"vendorBank\":1}}");
            Check(read.Pays && !read.Known && read.Pyreals == -1, "a bank that pays, balance not said: unread");
            Check(!VendorBank.Read("{\"values\":{\"vendorBank\":1,\"vendorBankPyreals\":-3}}").Known, "a negative balance: unread");
            Eq(VendorBank.Read("{\"values\":{\"vendorBank\":1,\"vendorBankPyreals\":1e30}}").Pyreals, VendorBank.MaxPyreals, "a huge balance is capped");
            Eq(VendorBank.Read("{\"values\":{\"vendorBank\":1,\"vendorBankPyreals\":12.9}}").Pyreals, 12L, "whole pyreals");

            // No bank: today's check, word for word.
            VendorQuote none = Scarabs(funds: 300);
            Eq(none.Refusal(100), "Not enough pyreals: 100 Mana Scarab cost 1,200, you have 300 (you can buy 25).", "no bank: the pack's pyreals");
            Eq(none.MaxBuy(), 25, "no bank: max is what the pack pays for");
            Eq(none.PriceLine(25), "300 pyreals · you have 300", "no bank: the price line as before");
            Eq(none.BankNote(100), null, "no bank: no note");
            Eq(none.MaxNote, string.Empty, "no bank: max needs no note");
            Eq(none.Spendable, 300L, "no bank: the pack");

            // A bank that's read and covers it: pack + bank, said both ways.
            VendorQuote covers = Scarabs(funds: 300) with { BankPays = true, BankFunds = 5000 };
            Eq(covers.Refusal(100), null, "pack 300 + bank 5,000 pay 1,200");
            Eq(covers.PriceLine(100), "price 1,200; pack 300 + bank 5,000", "the price line says both");
            Eq(covers.BankNote(100), "The bank pays the 900 pyreals your pack is short.", "the bank's part");
            Eq(covers.BankNote(25), null, "the pack alone pays 25: no note");
            Eq(covers.MaxBuy(), 441, "max counts pack + bank: 441 (5,292), not 442 (5,304)");
            Eq(covers.MaxNote, string.Empty, "a bank that's read: max needs no note");

            // A bank that's read and doesn't cover it.
            VendorQuote shortOf = Scarabs(funds: 300) with { BankPays = true, BankFunds = 500 };
            Eq(shortOf.Refusal(100), "Not enough pyreals: 100 Mana Scarab cost 1,200, you have 300 in your pack + 500 in the bank (you can buy 66).",
                "pack + bank short: both said, and how many they pay for");
            Eq(shortOf.MaxBuy(), 66, "max: 800 at 12 is 66");
            Eq(shortOf.BankNote(100), null, "no note for a buy that can't go");

            // A bank that can't be read: money isn't held against a buy; max is stock, room and burden.
            VendorQuote unread = Scarabs(funds: 300) with { BankPays = true, BankFunds = -1 };
            Eq(unread.Refusal(100), null, "the server decides about the money");
            Eq(unread.Spendable, -1L, "nothing to count against");
            Eq(unread.BankNote(100), "Your pack has 300 of the 1,200 pyreals: the server's bank may cover it (the server decides).", "the one-line note");
            Eq(unread.BankNote(25), null, "the pack alone pays: no note");
            Eq(unread.PriceLine(100), "1,200 pyreals · pack 300 + the bank", "the price line");
            Eq(unread.MaxBuy(), 5000, "max: 50 free slots of 100");
            Eq((unread with { FreeBurden = 55 }).MaxBuy(), 11, "max: the burden");
            Eq((unread with { FreeItemSlots = -1 }).MaxBuy(), 100, "max with nothing known: one stack");
            Check(unread.MaxNote.Contains("can't be read", StringComparison.Ordinal), "max says what it counts");
            string? why = unread.Refusal(6000);
            Check(why != null && why.StartsWith("Not enough room in your packs", StringComparison.Ordinal) && why.Contains("you can buy 5,000", StringComparison.Ordinal),
                $"room is still checked: {why}");
            Check((unread with { Unlimited = false, Stock = 5, ListingValue = 50 }).Refusal(6)!.StartsWith("The vendor has only 5", StringComparison.Ordinal), "and the stock");

            // The bank pays pyreal vendors only.
            var altInfo = new VendorInfo { VendorId = QVendor, SellRate = 1f, AltCurrencyWcid = 1234, AltCurrencyName = "Ancient Coin", AltCurrencyHave = 3, PlayerCoins = 300 };
            var relic = new VendorItem { ObjectId = QVScarab, Name = "Relic", UnitValue = 2, Value = 2, StackSize = 1, MaxStackSize = 0, Amount = -1, Unlimited = true };
            VendorQuote alt = VendorQuote.From(altInfo, relic, 10, 2, -1, new VendorBank(true, 5000));
            Check(!alt.BankPays && alt.Funds == 3 && alt.Refusal(2) != null, "the vendor's own currency: no bank");
            var pyInfo = new VendorInfo { VendorId = QVendor, SellRate = 1f, AltCurrencyHave = -1, PlayerCoins = 300 };
            VendorQuote py = VendorQuote.From(pyInfo, relic, 10, 2, -1, new VendorBank(true, 5000));
            Check(py.BankKnown && py.BankFunds == 5000 && py.Spendable == 5300, "a pyreal vendor: pack + bank");
            Check(!VendorQuote.From(pyInfo, relic, 10, 2, -1).BankPays, "and without a bank, none");

            // The answers.
            Eq(QtyReplies.BuyRefusal("costs 300 pyreals, you have 200", false), QtyReplies.VendorRefusal("costs 300 pyreals, you have 200"), "no bank: the engine's refusal as before");
            Check(QtyReplies.BuyRefusal("costs 1200 pyreals, you have 300", true).StartsWith("RynthCore didn't send it: it costs 1200 pyreals, you have 300.", StringComparison.Ordinal),
                "a bank was to pay: RynthCore's own money check said so");
            Eq(QtyReplies.BuyRefusal("vendor window is closed", true), QtyReplies.VendorRefusal("vendor window is closed"), "other refusals as before");
            Check(QtyReplies.NotSold(25, "Mana Scarab", true, 300, "pyreals").Contains("the bank may not cover 300 pyreals", StringComparison.Ordinal), "the server said no, with a bank");
            Check(QtyReplies.NotSold(25, "Mana Scarab", false, 300, "pyreals").EndsWith("(money, room, or too busy).", StringComparison.Ordinal), "and without one, as before");
        }

        private static void QtyBankBuy()
        {
            // The controller: the box's max and Buy, from the window and from /rdef buy, in each case.
            // The world: 66 free pack slots (52 + 14), burden unknown, 300 pyreals in the pack.
            var w = QWorld();
            var (c, said) = QController();
            QVendorOpen(w, coins: 300);
            w.SelectedId = QVScarab;
            uint Last() => w.NextRequest - 1;

            // No bank: refused before sending, as before; max 25.
            int sent = w.Sent.Count;
            c.Buy(w, QtyAmount.Of(100), "");
            Eq(said.Last(), (false, "Not enough pyreals: 100 Mana Scarab cost 1,200, you have 300 (you can buy 25)."), "no bank: refused");
            Eq(w.Sent.Count, sent, "nothing sent");
            c.SetBox(w, QtyAmount.Max);
            Eq(c.Value, 25, "no bank: the box's max");
            c.Buy(w, QtyAmount.Max, "");
            Eq(w.Sent.Last(), $"buy {QVScarab:X} from {QVendor:X} 25", "no bank: buy max sends 25");
            w.Status = new VendorTradeStatus { RequestId = Last(), State = VendorTradeState.Done, Result = VendorTradeResult.Accepted, Estimate = 300, IsBuy = true };
            c.Tick(w);
            Eq(said.Last(), (true, "Bought 25 Mana Scarab for 300 pyreals."), "bought");

            // A bank that's read and covers it.
            w.BankInfo = new VendorBank(true, 5000);
            c.SetBox(w, QtyAmount.Max);
            Eq(c.Value, 441, "the box's max counts pack + bank");
            Check(said.Last().Text.Contains("price 5,292; pack 300 + bank 5,000", StringComparison.Ordinal), $"/rdef qty says both: {said.Last().Text}");
            Check(c.View(w)!.Subject.MaxBuy == 441, "the window's max too");
            c.Buy(w, QtyAmount.Of(100), "");
            Eq(w.Sent.Last(), $"buy {QVScarab:X} from {QVendor:X} 100", "100 for 1,200: sent");
            w.Status = new VendorTradeStatus { RequestId = Last(), State = VendorTradeState.Done, Result = VendorTradeResult.Accepted, Estimate = 1200, IsBuy = true };
            c.Tick(w);
            Eq(said.Last(), (true, "Bought 100 Mana Scarab for 1,200 pyreals."), "bought with the bank's help");
            DefCommand cmd = DefCommandParser.Parse("buy max Mana Scarab");
            c.Buy(w, cmd.Amount, cmd.Text);
            Eq(w.Sent.Last(), $"buy {QVScarab:X} from {QVendor:X} 441", "/rdef buy max: pack + bank");
            w.Status = new VendorTradeStatus { RequestId = Last(), State = VendorTradeState.Refused, Message = "costs 5292 pyreals, you have 300" };
            c.Tick(w);
            Check(!said.Last().Ok && said.Last().Text.StartsWith("RynthCore didn't send it", StringComparison.Ordinal)
                  && said.Last().Text.Contains("out of the bank", StringComparison.Ordinal), $"RynthCore's own money check, said: {said.Last().Text}");

            // A bank that's read and doesn't cover it: refused before sending, both said.
            w.BankInfo = new VendorBank(true, 500);
            sent = w.Sent.Count;
            c.Buy(w, QtyAmount.Of(100), "");
            Eq(said.Last(), (false, "Not enough pyreals: 100 Mana Scarab cost 1,200, you have 300 in your pack + 500 in the bank (you can buy 66)."), "pack + bank short: refused");
            Eq(w.Sent.Count, sent, "nothing sent");
            c.SetBox(w, QtyAmount.Max);
            Eq(c.Value, 66, "the box's max: 66");
            cmd = DefCommandParser.Parse("buy 100 Mana Scarab");
            c.Buy(w, cmd.Amount, cmd.Text);
            Check(!said.Last().Ok && said.Last().Text.Contains("in the bank", StringComparison.Ordinal), "/rdef buy 100 replies the same");

            // A bank that can't be read: sent, and the server decides.
            w.BankInfo = new VendorBank(true);
            c.SetBox(w, QtyAmount.Max);
            Eq(c.Value, 6600, "the box's max: 66 free slots of 100 (money not counted)");
            Check(said.Last().Ok && said.Last().Text.Contains("max counts the vendor's stock, your pack space and burden only", StringComparison.Ordinal),
                $"with the note: {said.Last().Text}");
            c.Buy(w, QtyAmount.Of(100), "");
            Eq(w.Sent.Last(), $"buy {QVScarab:X} from {QVendor:X} 100", "100 for 1,200 with 300 in the pack: sent");
            w.Status = new VendorTradeStatus { RequestId = Last(), State = VendorTradeState.Done, Result = VendorTradeResult.Accepted, Estimate = 1200, IsBuy = true };
            c.Tick(w);
            Eq(said.Last(), (true, "Bought 100 Mana Scarab for 1,200 pyreals."), "the server's bank covered it");
            c.Buy(w, QtyAmount.Of(100), "");
            w.Status = new VendorTradeStatus { RequestId = Last(), State = VendorTradeState.Done, Result = VendorTradeResult.AnsweredNoRefresh, IsBuy = true };
            c.Tick(w);
            Check(!said.Last().Ok && said.Last().Text.Contains("the bank may not cover 1,200 pyreals", StringComparison.Ordinal), $"the server's own no, reported: {said.Last().Text}");
            c.Buy(w, QtyAmount.Of(100), "");
            w.Status = new VendorTradeStatus { RequestId = Last(), State = VendorTradeState.Refused, Message = "costs 1200 pyreals, you have 300" };
            c.Tick(w);
            Check(!said.Last().Ok && said.Last().Text.StartsWith("RynthCore didn't send it", StringComparison.Ordinal), "RynthCore's own money check, said");
            cmd = DefCommandParser.Parse("buy max Mana Scarab");
            c.Buy(w, cmd.Amount, cmd.Text);
            Eq(w.Sent.Last(), $"buy {QVScarab:X} from {QVendor:X} 6600", "/rdef buy max: room only");
            w.Status = null;
            w.Advance(QuantityController.BuyAnswerMs);
            c.Tick(w);
            Check(!said.Last().Ok && said.Last().Text.StartsWith("No answer about 6,600 Mana Scarab", StringComparison.Ordinal), "and it always answers");
            c.Buy(w, QtyAmount.Of(7000), "");
            Check(!said.Last().Ok && said.Last().Text.StartsWith("Not enough room in your packs", StringComparison.Ordinal), "room is still checked first");

            // The pack alone pays: the same with any bank.
            c.Buy(w, QtyAmount.Of(20), "");
            Eq(w.Sent.Last(), $"buy {QVScarab:X} from {QVendor:X} 20", "20 for 240: sent");
            w.Status = new VendorTradeStatus { RequestId = Last(), State = VendorTradeState.Done, Result = VendorTradeResult.AnsweredNoRefresh, IsBuy = true };
            c.Tick(w);
            Check(said.Last().Text.EndsWith("(money, room, or too busy).", StringComparison.Ordinal), "the pack paid: no bank in the answer");
        }

        private static void QtyStore()
        {
            var store = new DefStore(System.IO.Path.Combine(_temp, "qty"));
            var d = new DefData();
            Check(d.Settings.QuantityBox, "on by default");
            d.Settings.QuantityBox = false;
            Check(store.Save("Qty Server", "Qty Test", d) == null, "saved");
            Check(!store.Load("Qty Server", "Qty Test", out _).Settings.QuantityBox, "off read back");
            string path = store.PathFor("Qty Server", "Qty Old");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "{\"version\":1,\"settings\":{\"slotCount\":8}}");
            Check(store.Load("Qty Server", "Qty Old", out _).Settings.QuantityBox, "a file from before: on");
        }
    }
}

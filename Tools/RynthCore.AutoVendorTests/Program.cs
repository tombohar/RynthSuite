using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi.Vendor;

namespace RynthCore.AutoVendorTests;

// Tests for AutoVendor's pure logic: three-valued rule judging, UB's buy-up-to-N math,
// one-transaction planning (buy first, sell second, notes, pyreal room, splits) and the
// "never sell" checks. Self-contained fixtures only; fails on zero assertions.
//
// Run: dotnet run -c Release  (exit 0 = pass, 1 = fail)
internal static class Program
{
    private static int _asserts;
    private static int _fails;

    private static void Check(bool cond, string msg)
    {
        _asserts++;
        if (!cond) { _fails++; Console.WriteLine($"  [FAIL] {msg}"); }
    }

    private static void Eq<T>(T actual, T expected, string msg)
    {
        _asserts++;
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        { _fails++; Console.WriteLine($"  [FAIL] {msg}: expected <{expected}>, got <{actual}>"); }
    }

    private static int Main()
    {
        Console.WriteLine("=== AutoVendor tests ===");
        TestJudgeRule();
        TestCanJudge();
        TestObjectClass();
        TestDecideAndSell();
        TestDecideFromUtl();
        TestJudgeBuy();
        TestBuildBuyWants();
        TestPlanBuy();
        TestPlanSell();
        TestPlanNotes();
        TestPrices();
        TestSafety();
        TestVendorWillBuy();
        TestAddSell();
        TestParse();

        Console.WriteLine($"\n{_asserts} assertions, {_fails} failed.");
        if (_asserts == 0) { Console.WriteLine("ABORT: zero assertions ran."); return 1; }
        Console.WriteLine(_fails == 0 ? "ALL AUTOVENDOR TESTS PASSED." : $"{_fails} FAILURE(S).");
        return _fails == 0 ? 0 : 1;
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static VTankLootCondition Cond(int type, params string[] data) => new(type, "0", data);

    private static VTankLootRule Rule(string name, VTankLootAction action, int? keep, params VTankLootCondition[] conds)
        => new() { Name = name, Action = action, KeepCount = keep, Conditions = conds.ToList() };

    private static Func<VTankLootCondition, Tri> Fixed(params Tri[] answers)
    {
        int i = 0;
        return _ => answers[i++ % answers.Length];
    }

    /// <summary>A stand-in for VTankLootEvaluator: name regex (key 1), long equality, disabled flag.</summary>
    private static bool FakeMatch(VTankLootCondition c, string name, Dictionary<int, int> ints)
    {
        var d = c.DataLines;
        return c.NodeType switch
        {
            VTankNodeTypes.StringValueMatch => Regex.IsMatch(name, d[0], RegexOptions.IgnoreCase),
            VTankNodeTypes.LongValKeyE => ints.TryGetValue(int.Parse(d[1], CultureInfo.InvariantCulture), out int v)
                                          && v == int.Parse(d[0], CultureInfo.InvariantCulture),
            VTankNodeTypes.DisabledRule => !string.Equals(d[0].Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => throw new InvalidOperationException("fake matcher: unsupported node"),
        };
    }

    private static Func<VTankLootCondition, Tri> JudgeFake(ItemFacts f, string name, Dictionary<int, int>? ints = null)
    {
        ints ??= new Dictionary<int, int>();
        return c => AutoVendorRules.JudgeCondition(c, f, cc => FakeMatch(cc, name, ints));
    }

    private static readonly ItemFacts AppraisedSalvage = new() { Appraised = true, ObjectClass = AcObjectClass.Salvage, HasCharacterSkills = true, HasCharacterLevel = true };
    private static readonly ItemFacts ListingComp = new() { IsVendorListing = true, ObjectClass = AcObjectClass.SpellComponent, HasCharacterSkills = true, HasCharacterLevel = true };

    private static VendorView Vendor(float buy = 0.5f, float sell = 1.5f, uint types = 0xFFFFFFFF, int maxValue = 50000, bool alt = false)
        => new() { Id = 1, Name = "Test Vendor", BuyRate = buy, SellRate = sell, ItemTypes = types, MaxValue = maxValue, UsesAltCurrency = alt };

    private static Listing L(uint id, string name, int unitPrice, int value = 10, int maxStack = 1, int amount = -1,
                             AcObjectClass cls = AcObjectClass.SpellComponent, uint type = 0x1000, bool packSlot = false)
        => new() { Id = id, Name = name, UnitPrice = unitPrice, Value = value, MaxStackSize = maxStack, Amount = amount, Class = cls, ItemType = type, NeedsContainerSlot = packSlot };

    private static SellItem S(uint id, string name, int value, int stack = 1, AcObjectClass cls = AcObjectClass.Salvage, uint type = 0x40000000)
        => new() { Id = id, Name = name, Value = value, StackSize = stack, Class = cls, ItemType = type, RuleName = "S" };

    private static PackState Pack(long funds, int free = 50, int freePacks = 2, int pyrealStack = 25000)
        => new() { Funds = funds, FreeMainSlots = free, FreeContainerSlots = freePacks, PyrealStackSize = pyrealStack };

    // ── rules ──────────────────────────────────────────────────────────────

    private static void TestJudgeRule()
    {
        Console.WriteLine("\n-- JudgeRule --");
        var r2 = Rule("r", VTankLootAction.Sell, null, Cond(1, "x", "1"), Cond(1, "y", "1"));
        Eq(AutoVendorRules.JudgeRule(r2, Fixed(Tri.Yes, Tri.Yes), out _), Tri.Yes, "yes+yes");
        Eq(AutoVendorRules.JudgeRule(r2, Fixed(Tri.Yes, Tri.Unknown), out string why), Tri.Unknown, "yes+unknown");
        Eq(why, VTankNodeTypes.DisplayName(1), "unknown reason names the node type");
        Eq(AutoVendorRules.JudgeRule(r2, Fixed(Tri.Unknown, Tri.No), out _), Tri.No, "unknown+no = no (AND)");
        Eq(AutoVendorRules.JudgeRule(Rule("empty", VTankLootAction.Sell, null), Fixed(Tri.No), out _), Tri.Yes, "no conditions = unconditional");
    }

    private static void TestCanJudge()
    {
        Console.WriteLine("\n-- CanJudge --");
        Check(AutoVendorRules.CanJudge(Cond(1, "Taper", "1"), ListingComp), "listing: name match");
        Check(!AutoVendorRules.CanJudge(Cond(1, "x", "16"), ListingComp), "listing: other string key unknown");
        Check(AutoVendorRules.CanJudge(Cond(12, "250000", "19"), ListingComp), "listing: Value");
        Check(AutoVendorRules.CanJudge(Cond(3, "5", "12"), ListingComp), "listing: StackSize");
        Check(!AutoVendorRules.CanJudge(Cond(12, "6", "131"), ListingComp), "listing: Material unknown");
        Check(!AutoVendorRules.CanJudge(Cond(0, "Blood"), ListingComp), "listing: spell name unknown");
        Check(!AutoVendorRules.CanJudge(Cond(5, "0.5", "22"), ListingComp), "listing: double unknown");

        Check(AutoVendorRules.CanJudge(Cond(12, "6", "131"), AppraisedSalvage), "appraised: Material");
        Check(!AutoVendorRules.CanJudge(Cond(12, "6", "218103842"), AppraisedSalvage), "Decal synthetic key unknown");
        Check(!AutoVendorRules.CanJudge(Cond(0, "Blood"), AppraisedSalvage), "spells need spell data");
        Check(AutoVendorRules.CanJudge(Cond(0, "Blood"), AppraisedSalvage with { HasSpellData = true }), "spells with data");

        var unappraised = AppraisedSalvage with { Appraised = false };
        Check(!AutoVendorRules.CanJudge(Cond(12, "250", "19"), unappraised), "unappraised: Value unknown");
        Check(AutoVendorRules.CanJudge(Cond(12, "6", "131"), unappraised), "unappraised: Material is in PWD");
        Check(AutoVendorRules.CanJudge(Cond(1, "x", "1"), unappraised), "unappraised: name");

        foreach (int t in new[] { 10, 14, 15, 16, 1004, 2000, 2001, 2003, 2005, 2006, 2008, 424242 })
            Check(!AutoVendorRules.CanJudge(Cond(t, "0", "0", "0", "0", "0", "0"), AppraisedSalvage with { HasSpellData = true, HasPalettes = true }), $"approximated/unknown node {t} never judged");

        Check(!AutoVendorRules.CanJudge(Cond(1000, "200", "39"), AppraisedSalvage with { HasCharacterSkills = false }), "skill needs skill reads");
        Check(AutoVendorRules.CanJudge(Cond(9999, "false"), ListingComp), "disabled flag");

        // A matcher that throws (malformed line) reads as Unknown, not No.
        Eq(AutoVendorRules.JudgeCondition(Cond(1, "x", "1"), ListingComp, _ => throw new FormatException()), Tri.Unknown, "throwing matcher = unknown");
        Eq(AutoVendorRules.JudgeCondition(Cond(12, "abc", "notakey"), AppraisedSalvage, _ => true), Tri.Unknown, "unparseable key = unknown");
    }

    private static void TestObjectClass()
    {
        Console.WriteLine("\n-- ObjectClass --");
        Eq(AutoVendorRules.JudgeObjectClass(Cond(7, "39"), AcObjectClass.Salvage), Tri.Yes, "salvage = salvage");
        Eq(AutoVendorRules.JudgeObjectClass(Cond(7, "12"), AcObjectClass.Salvage), Tri.No, "salvage != comps");
        Eq(AutoVendorRules.JudgeObjectClass(Cond(7, "29"), AcObjectClass.Misc), Tri.Unknown, "Misc may be a healing kit");
        Eq(AutoVendorRules.JudgeObjectClass(Cond(7, "8"), AcObjectClass.Misc), Tri.Unknown, "Misc vs Misc still unknown");
        Eq(AutoVendorRules.JudgeObjectClass(Cond(7, "42"), AcObjectClass.Book), Tri.Unknown, "Book may be a scroll");
        Eq(AutoVendorRules.JudgeObjectClass(Cond(7, "38"), AcObjectClass.Container), Tri.Unknown, "Container may be a focus");
        Eq(AutoVendorRules.JudgeObjectClass(Cond(7, "15"), AcObjectClass.TradeNote), Tri.Yes, "trade note");
    }

    private static void TestDecideAndSell()
    {
        Console.WriteLine("\n-- Decide / JudgeSell --");
        var keepGranite = Rule("K: Granite", VTankLootAction.Keep, null, Cond(12, "67", "131"));
        var sellSalvage = Rule("S: Salvage", VTankLootAction.Sell, null, Cond(1, "Salvage", "1"));
        var rules = new List<VTankLootRule> { keepGranite, sellSalvage };

        var granite = new Dictionary<int, int> { [131] = 67 };
        var steel = new Dictionary<int, int> { [131] = 64 };

        var v1 = AutoVendorRules.Decide(rules, JudgeFake(AppraisedSalvage, "Salvage (100)", granite));
        Eq(v1.Rule?.Name, "K: Granite", "granite: keep rule decides");
        Check(!AutoVendorRules.JudgeSell(v1).Sell, "granite not sold");

        var v2 = AutoVendorRules.Decide(rules, JudgeFake(AppraisedSalvage, "Salvage (100)", steel));
        Eq(v2.Rule?.Name, "S: Salvage", "steel: sell rule decides");
        Check(v2.Certain && !v2.KeepPossible, "steel: certain, no keep");
        Check(AutoVendorRules.JudgeSell(v2).Sell, "steel sold");

        // Sell rule first, keep rule later: UB (first match) would sell; we don't.
        var sellFirst = new List<VTankLootRule> { sellSalvage, keepGranite };
        var v3 = AutoVendorRules.Decide(sellFirst, JudgeFake(AppraisedSalvage, "Salvage (100)", granite));
        Eq(v3.Rule?.Name, "S: Salvage", "sell-first: sell rule decides");
        Check(v3.KeepPossible, "sell-first: later keep rule noticed");
        var sd3 = AutoVendorRules.JudgeSell(v3);
        Check(!sd3.Sell && sd3.Reason.Contains("K: Granite"), "sell-first: held back by keep rule");

        // A keep rule we can't judge also blocks selling.
        var keepBuffed = Rule("K: Good weapons", VTankLootAction.Keep, null, Cond(2003, "50", "160"));
        var v4 = AutoVendorRules.Decide(new List<VTankLootRule> { keepBuffed, sellSalvage }, JudgeFake(AppraisedSalvage, "Salvage (100)", steel));
        Check(v4.Rule == keepBuffed && !v4.Certain, "unjudgeable keep rule stops the scan");
        Check(!AutoVendorRules.JudgeSell(v4).Sell, "not sold behind an unjudgeable keep rule");

        // A sell rule we can't judge never sells.
        var sellBuffed = Rule("S: Weak weapons", VTankLootAction.Sell, null, Cond(2003, "10", "160"));
        var v5 = AutoVendorRules.Decide(new List<VTankLootRule> { sellBuffed }, JudgeFake(AppraisedSalvage, "Sword", steel));
        Check(!v5.Certain && !AutoVendorRules.JudgeSell(v5).Sell, "unjudgeable sell rule never sells");

        // Disabled rules are skipped.
        var disabledKeep = Rule("K: off", VTankLootAction.Keep, null, Cond(1, "Salvage", "1"), Cond(9999, "true"));
        var v6 = AutoVendorRules.Decide(new List<VTankLootRule> { disabledKeep, sellSalvage }, JudgeFake(AppraisedSalvage, "Salvage (100)", steel));
        Check(AutoVendorRules.JudgeSell(v6).Sell, "disabled keep rule doesn't protect");

        var none = AutoVendorRules.Decide(rules, JudgeFake(AppraisedSalvage, "Sword", steel));
        Check(none.Rule == null && none.Certain, "no rule matches");
        Check(!AutoVendorRules.JudgeSell(none).Sell, "unmatched not sold");
    }

    private const string FixtureUtl =
        "UTL\n1\n5\n" +
        "K: Granite\n\n0;1;12\n9\n67\n131\n" +
        "Taper stock\n\n0;10;7;1\n100\n4\n12\n20\nPrismatic Taper\n1\n" +
        "S: Salvage\n\n0;3;7\n4\n39\n" +
        "S: Peas\n\n0;3;1\n19\nLead Pea|Copper Pea\n1\n" +
        "S: Buffed\n\n0;3;2003\n3\n50\n160\n";

    private static void TestDecideFromUtl()
    {
        Console.WriteLine("\n-- Decide from a parsed .utl --");
        var profile = VTankLootParser.LoadFromText(FixtureUtl);
        Eq(profile.Rules.Count, 5, "rule count");
        Eq(profile.Rules[1].Action, VTankLootAction.KeepUpTo, "keep # action");
        Eq(profile.Rules[1].KeepCount, 100, "keep # count");

        // "K: Granite" (Material) can't be judged on a listing, but it's a keep rule: either
        // way the taper is bought, at the taper rule's count.
        var buy = AutoVendorRules.DecideBuy(profile.Rules, JudgeFake(ListingComp, "Prismatic Taper"));
        Eq(buy.Kind, BuyKind.KeepUpTo, "taper listing: keep #");
        Eq(buy.KeepCount, 100, "taper listing: count");
        Eq(buy.RuleName, "Taper stock", "taper listing: rule");
        var peaListing = AutoVendorRules.DecideBuy(profile.Rules, JudgeFake(ListingComp, "Lead Pea"));
        Eq(peaListing.Kind, BuyKind.None, "pea listing: a sell rule, not bought");

        var pea = AutoVendorRules.Decide(profile.Rules, JudgeFake(ListingComp with { IsVendorListing = false, Appraised = true }, "Copper Pea"));
        Check(AutoVendorRules.JudgeSell(pea).Sell, "copper pea sold");

        var sword = AutoVendorRules.Decide(profile.Rules, JudgeFake(AppraisedSalvage with { ObjectClass = AcObjectClass.MeleeWeapon }, "Sword"));
        Eq(sword.Rule?.Name, "S: Buffed", "sword falls to the buffed rule");
        Check(!sword.Certain && !AutoVendorRules.JudgeSell(sword).Sell, "buffed rule unjudged: sword kept");
    }

    private static void TestJudgeBuy()
    {
        Console.WriteLine("\n-- JudgeBuy --");
        var keepAll = Rule("B: notes", VTankLootAction.Keep, null);
        var all = AutoVendorRules.DecideBuy(new List<VTankLootRule> { keepAll }, Fixed(Tri.Yes));
        Eq(all.Kind, BuyKind.Keep, "keep = buy all");
        Eq(all.KeepCount, int.MaxValue, "keep amount");

        var keep5 = Rule("u", VTankLootAction.KeepUpTo, 5, Cond(0, "x"));
        Eq(AutoVendorRules.DecideBuy(new List<VTankLootRule> { keep5 }, Fixed(Tri.Unknown)).Kind, BuyKind.None, "only an unjudgeable rule: not bought");

        var sell = Rule("s", VTankLootAction.Sell, null, Cond(0, "x"));
        Eq(AutoVendorRules.DecideBuy(new List<VTankLootRule> { sell }, Fixed(Tri.Yes)).Kind, BuyKind.None, "sell rule never buys");

        // An unjudgeable SELL rule ahead of a keep rule might be the match: don't buy.
        var keep100 = Rule("k100", VTankLootAction.KeepUpTo, 100, Cond(1, "x", "1"));
        Eq(AutoVendorRules.DecideBuy(new List<VTankLootRule> { sell, keep100 }, Fixed(Tri.Unknown, Tri.Yes)).Kind, BuyKind.None, "unjudgeable sell rule first: no buy");

        // Unjudgeable keep rules ahead: buy, at the smallest count seen.
        var r = AutoVendorRules.DecideBuy(new List<VTankLootRule> { keep5, keep100 }, Fixed(Tri.Unknown, Tri.Yes));
        Eq(r.Kind, BuyKind.KeepUpTo, "unjudgeable keep # then keep #");
        Eq(r.KeepCount, 5, "smallest count");
        var keepAllIf = Rule("all?", VTankLootAction.Keep, null, Cond(0, "x"));
        var r2 = AutoVendorRules.DecideBuy(new List<VTankLootRule> { keepAllIf, keep100 }, Fixed(Tri.Unknown, Tri.Yes));
        Eq(r2.Kind, BuyKind.KeepUpTo, "unjudgeable keep-all then keep #: capped");
        Eq(r2.KeepCount, 100, "keep # count");
    }

    // ── buy-up-to-N ────────────────────────────────────────────────────────

    private static void TestBuildBuyWants()
    {
        Console.WriteLine("\n-- BuildBuyWants --");
        var v = Vendor();
        var taper = L(10, "Prismatic Taper", 5);
        var scarab = L(11, "Mana Scarab", 40);
        var note = L(12, "Trade Note (250,000)", 287500, 250000, cls: AcObjectClass.TradeNote, type: 0x40000);
        var packA = L(13, "Pack", 100, cls: AcObjectClass.Container, type: 0x200, packSlot: true);
        var packB = L(14, "Bag", 50, cls: AcObjectClass.Container, type: 0x200, packSlot: true);

        var have = new Dictionary<string, int> { ["Prismatic Taper"] = 250, ["Mana Scarab"] = 60 };
        var judged = new List<(Listing, BuyDecision)>
        {
            (note, new BuyDecision(BuyKind.Keep, int.MaxValue, "B: notes")),
            (taper, new BuyDecision(BuyKind.KeepUpTo, 1000, "B: tapers")),
            (scarab, new BuyDecision(BuyKind.KeepUpTo, 50, "B: scarabs")),        // have 60 >= 50
            (packA, new BuyDecision(BuyKind.KeepUpTo, 3, "B: packs")),
            (packB, new BuyDecision(BuyKind.KeepUpTo, 5, "B: bags")),             // second container rule ignored
        };
        var wants = AutoVendorPlanner.BuildBuyWants(v, judged, n => have.TryGetValue(n, out int c) ? c : 0, c => c == AcObjectClass.Container ? 2 : 0);

        Eq(wants.Count, 3, "tapers + one pack rule + notes");
        Eq(wants[0].Item.Name, "Prismatic Taper", "cheapest first");
        Eq(wants[0].Amount, 750, "keep # 1000 with 250 held = 750");
        Eq(wants[1].Item.Name, "Pack", "first container rule");
        Eq(wants[1].Amount, 1, "packs: 3 wanted, 2 containers held");
        Eq(wants[2].Item.Name, "Trade Note (250,000)", "notes last");
        Eq(wants[2].Amount, int.MaxValue, "keep = all");
        Check(!wants.Any(w => w.Item.Name == "Mana Scarab"), "scarabs already stocked");
    }

    private static void TestPlanBuy()
    {
        Console.WriteLine("\n-- PlanNext (buy) --");
        var v = Vendor(sell: 1.0f);
        var taper = L(10, "Prismatic Taper", 10, value: 10, maxStack: 5000);
        var want = new List<BuyWant> { new() { Item = taper, Amount = 750, RuleName = "t" } };

        var p1 = AutoVendorPlanner.PlanNext(v, want, new List<SellItem>(), Pack(100_000));
        Eq(p1.Kind, PlanKind.Buy, "buys");
        Eq(p1.Buys.Single().Amount, 750, "all 750 affordable");

        var p2 = AutoVendorPlanner.PlanNext(v, want, new List<SellItem>(), Pack(1_005));
        Eq(p2.Buys.Single().Amount, 100, "funds limit: 1005 / 10 = 100");
        Check(p2.Total <= 1_005, "estimated cost within funds");

        var big = new List<BuyWant> { new() { Item = taper, Amount = int.MaxValue } };
        var p3 = AutoVendorPlanner.PlanNext(v, big, new List<SellItem>(), Pack(10_000_000));
        Eq(p3.Buys.Single().Amount, AutoVendorPlanner.MaxVendorBuyCount, "5000 per transaction");

        var limited = L(20, "Rare Thing", 10, value: 10, amount: 3);
        var p4 = AutoVendorPlanner.PlanNext(v, new List<BuyWant> { new() { Item = limited, Amount = 10 } }, new List<SellItem>(), Pack(1_000));
        Eq(p4.Buys.Single().Amount, 3, "limited listing: only what the vendor has");

        // Slots: 500 units, stacks of 100, 3 free main slots -> 100 * (3 - 2) = 100.
        var arrows = L(30, "Arrow", 1, value: 1, maxStack: 100);
        var p5 = AutoVendorPlanner.PlanNext(v, new List<BuyWant> { new() { Item = arrows, Amount = 500 } }, new List<SellItem>(), Pack(1_000_000, free: 3));
        Eq(p5.Buys.Single().Amount, 100, "slot limit keeps two free");

        var p6 = AutoVendorPlanner.PlanNext(v, new List<BuyWant> { new() { Item = arrows, Amount = 5 } }, new List<SellItem>(), Pack(1_000_000, free: 1));
        Eq(p6.Kind, PlanKind.Done, "one free slot: nothing bought (UB keeps one free)");

        // Partial buy stops the batch: the second item waits for the next transaction.
        var a = L(40, "A", 10, value: 1);
        var b = L(41, "B", 10, value: 2);
        var p7 = AutoVendorPlanner.PlanNext(v, new List<BuyWant> { new() { Item = a, Amount = 50 }, new() { Item = b, Amount = 5 } }, new List<SellItem>(), Pack(300));
        Eq(p7.Buys.Count, 1, "partial A ends the batch");
        Eq(p7.Buys[0].Amount, 30, "A limited by funds");

        // An unaffordable first item is skipped while nothing is on the list.
        var dear = L(50, "Dear", 1_000, value: 1);
        var cheap = L(51, "Cheap", 10, value: 5);
        var p8 = AutoVendorPlanner.PlanNext(v, new List<BuyWant> { new() { Item = dear, Amount = 1 }, new() { Item = cheap, Amount = 2 } }, new List<SellItem>(), Pack(100));
        Eq(p8.Buys.Single().Item.Name, "Cheap", "skips the unaffordable item");

        // Packs need a container slot, not a main-pack slot.
        var pack = L(60, "Pack", 100, value: 50, cls: AcObjectClass.Container, type: 0x200, packSlot: true);
        var p9 = AutoVendorPlanner.PlanNext(v, new List<BuyWant> { new() { Item = pack, Amount = 5 } }, new List<SellItem>(), Pack(10_000, free: 0, freePacks: 2));
        Eq(p9.Buys.Single().Amount, 2, "packs limited by container slots");

        var zero = L(70, "Broken", 0, value: 0);
        var p10 = AutoVendorPlanner.PlanNext(v, new List<BuyWant> { new() { Item = zero, Amount = 1 } }, new List<SellItem>(), Pack(100));
        Eq(p10.Kind, PlanKind.Fatal, "no price is fatal (UB)");
        Check(p10.Message.StartsWith("AutoVendor Fatal - No vendor price found", StringComparison.Ordinal), "UB fatal text");

        // Buying comes before selling.
        var p11 = AutoVendorPlanner.PlanNext(v, want, new List<SellItem> { S(1, "Salvage", 100) }, Pack(100_000));
        Eq(p11.Kind, PlanKind.Buy, "buy first");
    }

    private static void TestPlanSell()
    {
        Console.WriteLine("\n-- PlanNext (sell) --");
        var v = Vendor(buy: 1.0f);
        var none = new List<BuyWant>();

        var sells = Enumerable.Range(1, 120).Select(i => S((uint)i, "Salvage", 100)).ToList();
        var p1 = AutoVendorPlanner.PlanNext(v, none, sells, Pack(0, free: 50));
        Eq(p1.Kind, PlanKind.Sell, "sells");
        Eq(p1.Sells.Count, AutoVendorPlanner.MaxSellLines, "99 items per transaction");

        // Pyreal room: 3 free slots, 25000 per stack; a 100-unit stack worth 1000 each won't fit.
        var p2 = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { S(1, "Gem", 100_000, stack: 100) }, Pack(0, free: 3));
        Eq(p2.Kind, PlanKind.Split, "split when the pyreals won't fit");
        Eq(p2.SplitAmount, 25, "(3 - 2) * 25000 / 1000 = 25");

        var p3 = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { S(1, "Gem", 100_000, stack: 100) }, Pack(0, free: 2));
        Eq(p3.Kind, PlanKind.Fatal, "no room at all is fatal");
        Check(p3.Message.Contains("No inventory room to sell Gem"), "UB fatal text");

        // Later items that won't fit wait for the next batch.
        var p4 = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { S(1, "A", 20_000), S(2, "B", 20_000), S(3, "C", 20_000) }, Pack(0, free: 3));
        Eq(p4.Sells.Count, 2, "stop adding when the payout stops fitting");

        var p5 = AutoVendorPlanner.PlanNext(v, none, new List<SellItem>(), Pack(0));
        Eq(p5.Kind, PlanKind.Done, "nothing to do");

        // A split lands on another partial stack of the same item, so the planner never splits while one
        // exists (Drakkon's Gold Peas looped split-wait-split, 2026-10-19). Peas: 25,000 each, max 100.
        SellItem Pea(uint id, int stack) => new() { Id = id, Name = "Gold Pea", Value = 25_000 * stack, StackSize = stack,
                                                    Class = AcObjectClass.Misc, ItemType = 0x40000000, RuleName = "S: Peas", MaxStack = 100, Wcid = 9001 };
        // 88 free main slots: (88 - 2) * 25000 / 25000 = 86 peas fit.
        var twoPartials = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { Pea(1, 100), Pea(2, 91), Pea(3, 88) }, Pack(0, free: 88));
        Eq(twoPartials.Kind, PlanKind.Merge, "two partials: merge first");
        Eq(twoPartials.SplitItem!.Id, 3u, "the smaller partial goes...");
        Eq(twoPartials.MergeTarget!.Id, 2u, "...onto the bigger one");

        // Right after a split: the new stack (38) fits and is sold, never merged back onto the source (62).
        var justSplit = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { Pea(1, 62), Pea(2, 38), Pea(3, 100) }, Pack(0, free: 40));
        Eq(justSplit.Kind, PlanKind.Sell, "the freshly split stack is sold...");
        Eq(justSplit.Sells.Single().Id, 2u, "...not merged back");
        var justSplit2 = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { Pea(1, 100), Pea(2, 62), Pea(3, 38) }, Pack(0, free: 40));
        Eq(justSplit2.Kind, PlanKind.Sell, "same with a full stack first");
        Eq(justSplit2.Sells.Single().Id, 3u, "the 38");

        var onePartialFits = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { Pea(1, 100), Pea(2, 79) }, Pack(0, free: 88));
        Eq(onePartialFits.Kind, PlanKind.Sell, "one partial that fits: sold whole, no split");
        Eq(onePartialFits.Sells.Single().Id, 2u, "the partial");

        var onePartialBig = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { Pea(1, 100), Pea(2, 95) }, Pack(0, free: 88));
        Eq(onePartialBig.Kind, PlanKind.Split, "one partial too big: split...");
        Eq(onePartialBig.SplitItem!.Id, 2u, "...from the partial itself (nothing else for it to join)");
        Eq(onePartialBig.SplitAmount, 86, "86 fit");

        var allFull = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { Pea(1, 100), Pea(2, 100) }, Pack(0, free: 88));
        Eq(allFull.Kind, PlanKind.Split, "all full: plain split");
        Eq(allFull.SplitItem!.Id, 1u, "from the first");

        var otherItem = new SellItem { Id = 5, Name = "Silver Pea", Value = 50, StackSize = 50, MaxStack = 100, Wcid = 9002, Class = AcObjectClass.Misc, ItemType = 0x40000000 };
        var notSame = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { Pea(1, 100), otherItem }, Pack(0, free: 88));
        Eq(notSame.Kind, PlanKind.Split, "another item's partial doesn't count");
        Eq(notSame.SplitItem!.Id, 1u, "gold split as asked");

        // Stale stack values: a split leaves 100 peas' value on a small stack, a merge leaves a small value on a big one.
        Eq(AutoVendorPlanner.MostCarriedUnit(new Dictionary<int, long> { [25_000] = 800, [1_250_000] = 2, [3_000] = 100 }), 25_000, "most peas carry the true price");
        Eq(AutoVendorPlanner.MostCarriedUnit(new Dictionary<int, long> { [25_000] = 50, [30_000] = 50 }), 30_000, "tie: the higher value");
        Eq(AutoVendorPlanner.MostCarriedUnit(new Dictionary<int, long>()), 0, "nothing: 0 (no override)");

        var unknownMax = AutoVendorPlanner.PlanNext(v, none, new List<SellItem> { S(1, "Gem", 100_000, stack: 100), S(2, "Gem", 50_000, stack: 50) }, Pack(0, free: 3));
        Eq(unknownMax.Kind, PlanKind.Split, "unknown max stack: the old plain split");

        // Payout estimate uses the vendor's buy rate (ACE floor(value * rate + 0.1)).
        var half = Vendor(buy: 0.5f);
        var p6 = AutoVendorPlanner.PlanNext(half, none, new List<SellItem> { S(1, "Salvage", 101) }, Pack(0));
        Eq(p6.Total, 50L, "floor(101 * 0.5 + 0.1)");
    }

    private static void TestPlanNotes()
    {
        Console.WriteLine("\n-- PlanNext (trade notes) --");
        var v = Vendor(buy: 0.5f);
        SellItem Note(uint id, int stack) => S(id, "Trade Note (100)", 100 * stack, stack, AcObjectClass.TradeNote, 0x40000);

        var sorted = AutoVendorPlanner.SortSells(v, new[] { Note(1, 3), S(2, "Salvage", 5000) });
        Eq(sorted[0].Name, "Salvage", "notes sort last");

        // Nothing to buy: notes are never sold.
        var p1 = AutoVendorPlanner.PlanNext(v, new List<BuyWant>(), new List<SellItem> { Note(1, 3) }, Pack(0));
        Eq(p1.Kind, PlanKind.Done, "notes kept when nothing to buy");

        var dear = new List<BuyWant> { new() { Item = L(10, "Pack", 500, value: 400), Amount = 1 } };
        // Want something unaffordable: split one note off a stack...
        var p2 = AutoVendorPlanner.PlanNext(v, dear, new List<SellItem> { Note(1, 3) }, Pack(0));
        Eq(p2.Kind, PlanKind.Split, "split one note");
        Eq(p2.SplitAmount, 1, "one note");
        // ...or sell a single note already on its own.
        var p3 = AutoVendorPlanner.PlanNext(v, dear, new List<SellItem> { Note(1, 3), Note(2, 1) }, Pack(0));
        Eq(p3.Kind, PlanKind.Sell, "sell the single note");
        Eq(p3.Sells.Single().Id, 2u, "the single one");
        Eq(p3.Total, 100L, "notes sell at face value");

        // Other items go first; the note waits.
        var p4 = AutoVendorPlanner.PlanNext(v, dear, new List<SellItem> { S(5, "Salvage", 100), Note(2, 1) }, Pack(0));
        Eq(p4.Sells.Single().Id, 5u, "regular items before notes");

        // Never sell notes to buy notes.
        var noteWant = new List<BuyWant> { new() { Item = L(11, "Trade Note (250,000)", 287500, 250000, cls: AcObjectClass.TradeNote, type: 0x40000), Amount = int.MaxValue } };
        var p5 = AutoVendorPlanner.PlanNext(v, noteWant, new List<SellItem> { Note(2, 1) }, Pack(0));
        Eq(p5.Kind, PlanKind.Done, "no notes for notes");

        // An alt-currency vendor isn't paid in pyreals: selling notes can't help.
        var alt = Vendor(buy: 0.5f, alt: true);
        var p6 = AutoVendorPlanner.PlanNext(alt, dear, new List<SellItem> { Note(2, 1) }, Pack(0));
        Eq(p6.Kind, PlanKind.Done, "alt currency: notes kept");
    }

    private static void TestPrices()
    {
        Console.WriteLine("\n-- prices --");
        var v = Vendor(buy: 0.5f, sell: 1.5f);
        Eq(AutoVendorPlanner.PlayerPays(v, false, 10), 15L, "ceil(10 * 1.5 - 0.1)");
        Eq(AutoVendorPlanner.PlayerPays(v, true, 100), 115L, "notes cost 1.15x");
        Eq(AutoVendorPlanner.PlayerPays(v, false, 0), 1L, "at least 1");
        Eq(AutoVendorPlanner.VendorPays(v, false, 101), 50L, "floor(101 * 0.5 + 0.1)");
        Eq(AutoVendorPlanner.VendorPays(v, true, 250000), 250000L, "notes pay face value");

        var one = Vendor(sell: 1.0f);
        var stackable = L(1, "Arrow", 0, value: 10, maxStack: 100);
        Eq(AutoVendorPlanner.EstimateCost(one, stackable, 250), 2500L, "2 full stacks + a partial");
        var limited = L(2, "Whole Lot", 0, value: 500, amount: 5);
        Eq(AutoVendorPlanner.EstimateCost(one, limited, 1), 500L, "limited listing costs at least the listing");
        Eq(AutoVendorPlanner.MostAffordable(one, limited, 5, 1200), 2, "most that fits 1200 at 500 each");
        Eq(AutoVendorPlanner.MostAffordable(one, limited, 5, 499), 0, "can't afford one");
        var lot = new Listing { Id = 9, Name = "Lot of 10", UnitPrice = 1, Value = 1000, StackSize = 10, MaxStackSize = 10, Amount = 10 };
        // The per-unit price says 3 units cost 3, but ACE charges the whole listing (1000).
        var p = AutoVendorPlanner.PlanNext(one, new List<BuyWant> { new() { Item = lot, Amount = 3 } }, new List<SellItem>(), Pack(500));
        Eq(p.Kind, PlanKind.Done, "limited listing: whole-listing price over funds, nothing bought");
        Eq(AutoVendorPlanner.UnitPrice(v, L(3, "X", 0, value: 10)), 15L, "unit price falls back to the rate");
        Eq(AutoVendorPlanner.UnitPrice(v, L(4, "Y", 7, value: 10)), 7L, "engine unit price wins");

        Check(AutoVendorPlanner.PyrealsFit(Pack(0, free: 2), 25_000), "one stack, two free slots");
        Check(!AutoVendorPlanner.PyrealsFit(Pack(0, free: 1), 1), "one free slot is kept");
        Eq(AutoVendorPlanner.SlotsFor(stackable, 250), 3, "250 arrows = 3 slots");
        Eq(AutoVendorPlanner.CategoryName(0x1000), "Spell Components", "category");
    }

    private static void TestSafety()
    {
        Console.WriteLine("\n-- never-sell checks --");
        var ok = new SafetyFacts { Appraised = true, Class = AcObjectClass.Salvage, Value = 100, InMainPack = true };
        Eq(AutoVendorPlanner.UnsafeReason(ok, false), null, "plain item is sellable");

        (SafetyFacts, string)[] cases =
        {
            (new SafetyFacts { Appraised = false, Value = 100 }, "not identified"),
            (new SafetyFacts { Appraised = true, Value = 100, Equipped = true }, "equipped"),
            (new SafetyFacts { Appraised = true, Value = 0 }, "no value"),
            (new SafetyFacts { Appraised = true, Value = 100, CanBeSold = false }, "not sellable"),
            (new SafetyFacts { Appraised = true, Value = 100, IsRare = true }, "rare"),
            (new SafetyFacts { Appraised = true, Value = 100, Attuned = 1 }, "attuned"),
            (new SafetyFacts { Appraised = true, Value = 100, Retained = true }, "retained"),
            (new SafetyFacts { Appraised = true, Value = 100, TimesTinkered = 1 }, "tinkered"),
            (new SafetyFacts { Appraised = true, Value = 100, Imbued = 1 }, "imbued"),
            (new SafetyFacts { Appraised = true, Value = 100, Inscription = "mine" }, "inscribed"),
            (new SafetyFacts { Appraised = true, Value = 100, Class = AcObjectClass.Container }, "container"),
            (new SafetyFacts { Appraised = true, Value = 100, HoldsItems = true }, "container"),
            (new SafetyFacts { Appraised = true, Value = 100, Class = AcObjectClass.Foci }, "focus"),
            (new SafetyFacts { Appraised = true, Value = 100, Class = AcObjectClass.Money }, "money"),
        };
        foreach (var (f, why) in cases)
            Eq(AutoVendorPlanner.UnsafeReason(f, false), why, $"never sell: {why}");

        var side = new SafetyFacts { Appraised = true, Class = AcObjectClass.Salvage, Value = 100, InMainPack = false };
        Eq(AutoVendorPlanner.UnsafeReason(side, false), null, "side pack ok by default");
        // Bonded only decides what a death keeps on ACE: peas, trade notes and salvage are bonded and sell (2026-10-07).
        Eq(AutoVendorPlanner.UnsafeReason(new SafetyFacts { Appraised = true, Value = 12500, Bonded = 1 }, false), null, "bonded (kept on death) still sells");
        Eq(AutoVendorPlanner.UnsafeReason(side, true), "not in main pack", "OnlyFromMainPack");
    }

    private static void TestVendorWillBuy()
    {
        Console.WriteLine("\n-- VendorWillBuy --");
        var v = Vendor(types: 0x40000000 | 0x40000, maxValue: 1000);
        Check(AutoVendorPlanner.VendorWillBuy(v, 0x40000000, false, 500), "salvage under the cap");
        Check(!AutoVendorPlanner.VendorWillBuy(v, 0x40000000, false, 1500), "over MaxValue");
        Check(!AutoVendorPlanner.VendorWillBuy(v, 0x1, false, 10), "type the vendor doesn't buy");
        Check(AutoVendorPlanner.VendorWillBuy(v, 0x40000, true, 250000), "notes skip the cap");
        Check(!AutoVendorPlanner.VendorWillBuy(Vendor(types: 0x40000000), 0x40000, true, 100), "notes still need the vendor's type (engine rule)");
        Check(!AutoVendorPlanner.VendorWillBuy(Vendor(maxValue: 0), 0x1000, false, 1), "unknown MaxValue: don't sell");
    }

    private static void TestAddSell()
    {
        Console.WriteLine("\n-- addsell split planning --");
        var stacks = new List<SellItem> { S(1, "Pea", 5, 5), S(2, "Pea", 3, 3), S(3, "Pea", 10, 10) };

        var (w1, m1, s1) = AutoVendorPlanner.PlanAddSell(stacks, 8);
        Eq(string.Join(",", w1.Select(x => x.Id)), "1,2", "5 + 3 = 8");
        Eq(m1, 0, "nothing missing");
        Check(s1 == null, "no split");

        var (w2, m2, s2) = AutoVendorPlanner.PlanAddSell(stacks, 7);
        Eq(string.Join(",", w2.Select(x => x.Id)), "1", "take 5");
        Eq(m2, 2, "2 missing");
        Eq(s2?.Id, 2u, "split the smallest bigger stack (3)");

        var (w3, m3, s3) = AutoVendorPlanner.PlanAddSell(stacks, 30);
        Eq(w3.Count, 3, "all stacks");
        Eq(m3, 12, "12 missing");
        Check(s3 == null, "nothing big enough to split");
    }

    private static void TestParse()
    {
        Console.WriteLine("\n-- parsing --");
        Eq(AutoVendorPlanner.ParseCountAndName("10 Mana Scarab"), (10, "Mana Scarab"), "count + name");
        Eq(AutoVendorPlanner.ParseCountAndName("Mana Scarab"), (1, "Mana Scarab"), "name only");
        Eq(AutoVendorPlanner.ParseCountAndName("  3   Lead Pea "), (3, "Lead Pea"), "extra spaces");
    }
}

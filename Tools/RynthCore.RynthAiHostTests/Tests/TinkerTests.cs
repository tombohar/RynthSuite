using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// The Crafting tab's tinkering (SilentKelpie's, 2026-10-12): ACE's retail chance
// (RecipeManager.GetTinkerChance), the planner's caps and order (imbue once, rend last), bag
// matching / choice, and the universal Crafting tab id. Server rows come from the server plugin's
// profile ("salvage", ServerSalvageView) and carry their own rules, checked here on rows shaped
// like Infinite Leaftide's (RynthILT serves those; RynthILT.Tests checks its data): no tink slot,
// applied ints, stack groups (Steel + Serpentine 10), after-cap rows (Leather), an own order, and
// prepared rows (tier bags, Ammunition).
internal static class TinkerTests
{
    public static void Register(Runner r)
    {
        r.Add("tinker: retail difficulty and chance (ACE GetTinkerChance)", Chance);
        r.Add("tinker: slot cap, one imbue, attempts this run", SlotsAndImbue);
        r.Add("tinker: damage-type rend and rend last", Rend);
        r.Add("tinker: server weapon rows (no slot, an after-cap row last only when the slots are full)", AfterCap);
        r.Add("tinker: server jewelry rows (applied ints skip, a stack group shares one cap)", StackAndApplied);
        r.Add("tinker: item rows (Moonstone, Leather, Sandstone)", ItemRows);
        r.Add("tinker: scan filters", Filters);
        r.Add("tinker: bag matching (full, Abyssal, Foolproof)", Bags);
        r.Add("tinker: bag choice by chance and minimum", BagChoice);
        r.Add("tinker: Crafting is a universal Mini Remote tab", Section);
        r.Add("tinker: prepared server rows (applied ints, order, tier bags, Ammunition)", Trinkets);
        r.Add("tinker: a server plugin's salvage (per tab, own keys, own order, no rules unless asked)", PluginSalvage);
    }

    private static TinkerBenchSettings Settings(params (string Key, int Count)[] counts)
    {
        var s = new TinkerBenchSettings { MaxTinks = 10 };
        foreach (var (k, n) in counts) s.Counts[k] = n;
        return s;
    }

    private static TinkerItemFacts Item(AcObjectClass oc, int tinks = 0, bool imbued = false, int damageType = 0,
                                        double work = 6, Func<int, int>? flags = null)
        => new() { Id = 1, Name = "Test Item", Class = oc, Tinks = tinks, Imbued = imbued, DamageType = damageType,
                   Workmanship = work, FlagInt = flags ?? (_ => 0) };

    private static List<int> Mats(List<TinkerStep> plan) => plan.Select(p => p.Material).ToList();

    private static ServerSalvageView Row(string bench, int material, string kind, bool slot = false, string nameMatch = "",
                                         int applied = 0, bool prepared = false, int order = 0, bool afterCap = false,
                                         string group = "", int cap = 0)
        => new(bench, material, kind, "server row", slot, nameMatch, applied, prepared, order, afterCap, group, cap);

    /// <summary>The tab's rows with these server rows (as the Crafting tab builds them).</summary>
    private static List<TinkerMaterial> WithServer(TinkerBench bench, params ServerSalvageView[] server)
        => TinkerPlanner.Rows(bench, TinkerCatalog.FromServerPlugin(bench, server), null);

    // ILT-shaped rows (RynthILT's Hub/IltSalvage.cs has the real ones).
    private const int JewelSilverInt = 374, TrinketDiamondInt = 384, TrinketGromnieInt = 386;

    private static readonly ServerSalvageView[] Weapon =
    {
        Row("melee", TinkerCatalog.YellowGarnet, "once", order: 70),
        Row("melee", TinkerCatalog.BlackGarnet, "once", slot: true, nameMatch: "Abyssal", order: 100),
        Row("melee", TinkerCatalog.Leather, "once", order: 995, afterCap: true),
    };

    private static readonly ServerSalvageView[] Jewelry =
    {
        Row("jewelry", TinkerCatalog.Silver, "once", applied: JewelSilverInt, order: 10),
        Row("jewelry", TinkerCatalog.Copper, "once", applied: 370, order: 20),
        Row("jewelry", TinkerCatalog.Serpentine, "tink", order: 50, group: "jewelry-stack", cap: 10),
        Row("jewelry", TinkerCatalog.Steel, "tink", order: 60, group: "jewelry-stack", cap: 10),
    };

    private static readonly ServerSalvageView[] TrinketRows =
    {
        Row("trinkets", TinkerCatalog.Diamond, "once", applied: TrinketDiamondInt, prepared: true, order: 10),
        Row("trinkets", TinkerCatalog.Ruby, "once", applied: 385, prepared: true, order: 20),
        Row("trinkets", TinkerCatalog.GromnieHide, "once", applied: TrinketGromnieInt, prepared: true, order: 30),
        Row("trinkets", TinkerCatalog.Pyreal, "once", applied: 387, prepared: true, order: 40),
    };

    /// <summary>A plan's materials as "a,b,c" (Check.Eq compares lists by reference).</summary>
    private static string Seq(List<TinkerStep> plan) => string.Join(",", Mats(plan));

    private static string Seq(params int[] ids) => string.Join(",", ids);

    private static void Chance()
    {
        // Steel (mod 12), item 5, bag 6 (>= item: wkMod 2): 60 + 120 - 28.8 = 151.2.
        Check.Eq(TinkerMath.Difficulty(TinkerCatalog.Steel, 5, 6, 0), 151, "steel difficulty, first tink");
        Check.Eq(TinkerMath.Difficulty(TinkerCatalog.Steel, 5, 6, 3), 241, "fourth tink: x1.6");
        // Gold (mod 10), item 10, bag 3 (< item: wkMod 1): 50 + 200 - 6 = 244.
        Check.Eq(TinkerMath.Difficulty(TinkerCatalog.Gold, 10, 3, 0), 244, "gold, weaker bag");
        Check.Eq(TinkerMath.Difficulty(TinkerCatalog.Iron, 5, 5, 10), int.MaxValue, "no 11th tink");
        Check.True(Math.Abs(TinkerMath.SkillChance(200, 200) - 0.5) < 1e-9, "skill = difficulty is 50%");
        Check.Eq(TinkerMath.Percent(TinkerMath.Chance(TinkerCatalog.Steel, 300, 5, 6, 0, false, false)), 99, "300 skill vs 151");
        double plain = TinkerMath.Chance(TinkerCatalog.BlackOpal, 250, 6, 6, 0, false, false);
        double imbue = TinkerMath.Chance(TinkerCatalog.BlackOpal, 250, 6, 6, 0, true, false);
        Check.True(Math.Abs(imbue - plain / 3) < 1e-9, "imbues are a third");
        Check.Eq(TinkerMath.Chance(TinkerCatalog.BlackOpal, 1, 10, 1, 9, true, true), 1.0, "foolproof always lands");
        Check.Eq(TinkerMath.SalvageMod(TinkerCatalog.RoseQuartz), 25.0, "gem cantrips are mod 25");
        Check.Eq(TinkerMath.SalvageMod(TinkerCatalog.WhiteQuartz), 20.0, "default mod 20");
        Check.Eq(TinkerMath.BagWorkmanship(600, 100), 6.0, "bag workmanship is the average");
    }

    private static void SlotsAndImbue()
    {
        var rows = TinkerPlanner.Rows(TinkerBench.Melee, null, null);
        var s = Settings(("r" + TinkerCatalog.Iron, 10), ("r" + TinkerCatalog.BlackOpal, 1), ("r" + TinkerCatalog.FireOpal, 1));
        var plan = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 6), s, rows);
        Check.Eq(Mats(plan).Count(m => m == TinkerCatalog.BlackOpal), 1, "first imbue in order");
        Check.Eq(Mats(plan).Count(m => m == TinkerCatalog.FireOpal), 0, "second imbue refused");
        Check.Eq(Mats(plan).Count(m => m == TinkerCatalog.Iron), 3, "imbue takes a slot: 4 free - 1");
        Check.Eq(plan[0].Material, TinkerCatalog.BlackOpal, "imbue before stat tinks");

        var imbued = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 6, imbued: true), s, rows);
        Check.Eq(Mats(imbued).Count(m => m == TinkerCatalog.BlackOpal), 0, "already imbued");
        Check.Eq(imbued.Count, 4, "four Irons fill the free slots");

        var tried = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 0, imbued: true),
            Settings(("r" + TinkerCatalog.Iron, 3)), rows, k => k == "r" + TinkerCatalog.Iron ? 2 : 0);
        Check.Eq(tried.Count, 1, "two of three Irons already tried this run");
        Check.Eq(TinkerPlanner.Summary(imbued), "Iron x4", "summary groups repeats");
    }

    private static void Rend()
    {
        Check.Eq(TinkerCatalog.RendFor(3, false), TinkerCatalog.ImperialTopaz, "slash/pierce: slash");
        Check.Eq(TinkerCatalog.RendFor(3, true), TinkerCatalog.BlackGarnet, "slash/pierce: pierce");
        Check.Eq(TinkerCatalog.RendFor(4, false), TinkerCatalog.WhiteSapphire, "bludgeon");
        Check.Eq(TinkerCatalog.RendFor(16, false), TinkerCatalog.RedGarnet, "fire");
        Check.Eq(TinkerCatalog.RendFor(0x400, false), TinkerCatalog.Onyx, "nether");
        Check.Eq(TinkerCatalog.RendFor(0, false), 0, "no damage type, no rend");

        var rows = TinkerPlanner.Rows(TinkerBench.Melee, null, null);
        var s = Settings(("r" + TinkerCatalog.RendId, 1), ("r" + TinkerCatalog.Iron, 2));
        var first = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, damageType: 32), s, rows);
        Check.Eq(first[0].Material, TinkerCatalog.Emerald, "acid weapon: Emerald first");
        s.RendLast = true;
        var last = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, damageType: 32), s, rows);
        Check.Eq(last[^1].Material, TinkerCatalog.Emerald, "rend last");
        Check.Eq(last.Count, 3, "rend + 2 Iron");
    }

    /// <summary>
    /// Rows from a server plugin's profile (ServerPackInfo.Salvage): only the tab's own, in kind order
    /// unless a row gives its own, keyed "p" (never sharing a count with retail "r"), and with no rule
    /// the row doesn't ask for (a Leather without "afterCap" doesn't wait for the 10 ordinary tinks).
    /// </summary>
    private static void PluginSalvage()
    {
        var salvage = new List<RynthCore.Plugin.Shared.ServerSalvageView>
        {
            new("melee", TinkerCatalog.Leather, "once", "server Leather", false, ""),
            new("melee", TinkerCatalog.Iron, "tink", "server Iron", true, ""),
            new("melee", TinkerCatalog.Emerald, "imbue", "server imbue", true, ""),
            new("melee", TinkerCatalog.Steel, "bogus", "unknown kind", true, ""),
            new("armor", TinkerCatalog.Steel, "tink", "other tab", true, ""),
        };
        var server = TinkerCatalog.FromServerPlugin(TinkerBench.Melee, salvage);
        Check.Eq(server.Count, 3, "this tab's rows; the unknown kind is left out");
        Check.True(server.All(r => r.Origin == TinkerOrigin.Plugin), "plugin origin");
        Check.Eq(TinkerCatalog.Key(server[0]), "p" + TinkerCatalog.Leather, "own key");
        Check.Eq(server[0].Name, TinkerCatalog.MaterialName(TinkerCatalog.Leather), "material name");
        Check.Eq(TinkerCatalog.FromServerPlugin(TinkerBench.Melee, null).Count, 0, "nothing defined, no rows");

        var rows = TinkerPlanner.Rows(TinkerBench.Melee, server, null);
        var s = Settings(("p" + TinkerCatalog.Leather, 1), ("p" + TinkerCatalog.Iron, 2), ("p" + TinkerCatalog.Emerald, 1));
        var plan = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 5), s, rows);
        Check.Eq(Seq(plan), Seq(TinkerCatalog.Emerald, TinkerCatalog.Iron, TinkerCatalog.Iron, TinkerCatalog.Leather),
                 "imbue, tinks, then once; Leather doesn't wait for the cap");
        Check.True(server.All(r => !r.AfterCap && r.StackGroup.Length == 0 && r.AppliedInt == 0 && !r.Prepared), "no rules unless asked");

        // A row's own order: this once-row runs before the retail imbues (101) and the plugin's tinks.
        var ordered = TinkerCatalog.FromServerPlugin(TinkerBench.Melee, new[]
        {
            Row("melee", TinkerCatalog.Iron, "tink", slot: true),
            Row("melee", TinkerCatalog.Turquoise, "once", order: 80),
        });
        Check.Eq(ordered[1].Order, 80, "the row's order");
        Check.Eq(ordered[0].Order, 400, "no order: the kind's default");
        var first = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon),
            Settings(("r" + TinkerCatalog.BlackOpal, 1), ("p" + TinkerCatalog.Iron, 1), ("p" + TinkerCatalog.Turquoise, 1)),
            TinkerPlanner.Rows(TinkerBench.Melee, ordered, null));
        Check.Eq(Seq(first), Seq(TinkerCatalog.Turquoise, TinkerCatalog.BlackOpal, TinkerCatalog.Iron), "order 80 goes first");

        // The optional fields map onto the planner's row.
        var mapped = TinkerCatalog.FromServerPlugin(TinkerBench.Jewelry, Jewelry.Concat(new[]
        {
            Row("jewelry", TinkerCatalog.Moonstone, "tink", group: "no-cap"),
        }));
        Check.Eq(mapped[0].AppliedInt, JewelSilverInt, "applied int");
        Check.Eq(mapped[2].StackGroup + "/" + mapped[2].StackCap, "jewelry-stack/10", "stack group and cap");
        Check.Eq(mapped[4].StackGroup, "", "a group without a cap is no group");
        Check.True(TinkerCatalog.FromServerPlugin(TinkerBench.Melee, Weapon)[2].AfterCap, "after cap");
        Check.True(TinkerCatalog.FromServerPlugin(TinkerBench.Trinkets, TrinketRows).All(r => r.Prepared), "prepared");
    }

    private static void AfterCap()
    {
        var rows = WithServer(TinkerBench.Melee, Weapon);
        var s = Settings(("p" + TinkerCatalog.YellowGarnet, 1), ("r" + TinkerCatalog.Iron, 10), ("p" + TinkerCatalog.Leather, 1));
        var full = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 10), s, rows);
        Check.Eq(Seq(full), Seq(TinkerCatalog.YellowGarnet, TinkerCatalog.Leather), "capped item: no-slot rows, then Leather");

        var some = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 5), s, rows);
        Check.Eq(some[^1].Material, TinkerCatalog.Leather, "Irons fill the last 5 slots, then Leather");
        Check.Eq(Mats(some).Count(m => m == TinkerCatalog.Iron), 5, "five Irons");
        Check.Eq(Mats(some).Count(m => m == TinkerCatalog.Leather), 1, "Leather once");

        var open = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 5),
            Settings(("p" + TinkerCatalog.Leather, 1), ("r" + TinkerCatalog.Iron, 2)), rows);
        Check.True(!Mats(open).Contains(TinkerCatalog.Leather), "Leather waits for the 10 ordinary tinks");

        var retailOnly = TinkerPlanner.Plan(TinkerBench.Melee, Item(AcObjectClass.MeleeWeapon, tinks: 10), s,
            TinkerPlanner.Rows(TinkerBench.Melee, null, null));
        Check.Eq(retailOnly.Count, 0, "no server rows: nothing offered past the cap");
    }

    private static void StackAndApplied()
    {
        var rows = WithServer(TinkerBench.Jewelry, Jewelry);
        var s = Settings(("p" + TinkerCatalog.Serpentine, 8), ("p" + TinkerCatalog.Steel, 8),
                         ("p" + TinkerCatalog.Silver, 1), ("p" + TinkerCatalog.Copper, 1));
        var plan = TinkerPlanner.Plan(TinkerBench.Jewelry, Item(AcObjectClass.Jewelry, tinks: 0,
            flags: k => k == JewelSilverInt ? 2 : 0), s, rows);
        var m = Mats(plan);
        Check.Eq(m.Count(x => x == TinkerCatalog.Serpentine), 8, "8 Serpentine");
        Check.Eq(m.Count(x => x == TinkerCatalog.Steel), 2, "Steel stops at 10 combined");
        Check.True(!m.Contains(TinkerCatalog.Silver), "Silver's applied int already reads 2");
        Check.Eq(m[0], TinkerCatalog.Copper, "flag rows first (their order)");

        // Two groups don't share; a row outside any group isn't capped.
        var split = WithServer(TinkerBench.Jewelry,
            Row("jewelry", TinkerCatalog.Serpentine, "tink", order: 50, group: "a", cap: 3),
            Row("jewelry", TinkerCatalog.Steel, "tink", order: 60, group: "b", cap: 4),
            Row("jewelry", TinkerCatalog.Iron, "tink", order: 70));
        var p2 = Mats(TinkerPlanner.Plan(TinkerBench.Jewelry, Item(AcObjectClass.Jewelry),
            Settings(("p" + TinkerCatalog.Serpentine, 9), ("p" + TinkerCatalog.Steel, 9), ("p" + TinkerCatalog.Iron, 9)), split));
        Check.Eq(p2.Count(x => x == TinkerCatalog.Serpentine), 3, "group a: its cap");
        Check.Eq(p2.Count(x => x == TinkerCatalog.Steel), 4, "group b: its own cap");
        Check.Eq(p2.Count(x => x == TinkerCatalog.Iron), 9, "no group, no slot: all 9");
        // Which jewelry is a trinket is the server plugin's (its profile's "trinkets"; RynthILT's Leaftide list).
        Check.False(TinkerCatalog.IsTrinket("Ornate Compass", null), "no server plugin: no trinkets (retail jewelry is all Jewelry)");
        var leaftide = ServerTrinketRule.Parse("{\"schema\":1,\"trinkets\":{\"names\":[\"compass\",\"top\"],\"except\":[\"topaz\"]}}");
        Check.True(TinkerCatalog.IsTrinket("Ornate Compass", leaftide) && !TinkerCatalog.IsTrinket("Yellow Topaz Ring", leaftide),
                   "with the server's rule: trinkets left to their tab, a topaz ring is no top");
        Check.True(TinkerCatalog.IsTrinket("Spinning Top", leaftide), "a top is");
        Check.Eq(ServerTrinketRule.Parse("{\"schema\":1,\"salvage\":[]}"), ServerTrinketRule.None, "a profile without trinkets: none");
        Check.Eq(ServerTrinketRule.Parse("not json"), ServerTrinketRule.None, "unusable text: none");
    }

    private static void ItemRows()
    {
        var rows = TinkerPlanner.Rows(TinkerBench.Items, null, null);
        var s = Settings(("r" + TinkerCatalog.Moonstone, 2), ("r" + TinkerCatalog.Leather, 1), ("r" + TinkerCatalog.Sandstone, 1));
        var noMana = TinkerPlanner.Plan(TinkerBench.Items, Item(AcObjectClass.Armor), s, rows);
        Check.Eq(Seq(noMana), Seq(TinkerCatalog.Leather), "no mana: no Moonstone; not retained: Leather, not Sandstone");

        var mana = new TinkerItemFacts { Class = AcObjectClass.Armor, Workmanship = 5, MaxMana = 800, Retained = true, Tinks = 10 };
        var retained = TinkerPlanner.Plan(TinkerBench.Items, mana, s, rows);
        Check.Eq(Seq(retained), Seq(TinkerCatalog.Sandstone), "capped + retained: only Sandstone (no slot)");
    }

    private static void Filters()
    {
        var s = Settings();
        s.MaxTinks = 8;
        s.MinFreeTinks = 3;
        s.SkipImbued = true;
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, tinks: 9), s), "9 tinks (max 8)", "max tinks");
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, tinks: 8), s), "fewer than 3 free tinks", "min free");
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, imbued: true), s), "imbued", "skip imbued");
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, work: 0), s), "no workmanship", "untinkerable");
        var worn = Item(AcObjectClass.Armor);
        worn.Equipped = true;
        Check.Eq(TinkerPlanner.Excluded(worn, s), "equipped", "worn items left alone");
        Check.Eq(TinkerPlanner.Excluded(Item(AcObjectClass.Armor, tinks: 2), s), "", "eligible");
        Check.True(TinkerCatalog.Targets(TinkerBench.Caster, AcObjectClass.WandStaffOrb)
                   && !TinkerCatalog.Targets(TinkerBench.Items, AcObjectClass.Salvage), "bench targets");
    }

    private static void Bags()
    {
        var melee = WithServer(TinkerBench.Melee, Weapon);
        var rend = new TinkerStep(melee.First(r => r.Id == TinkerCatalog.RendId), TinkerCatalog.BlackGarnet);
        var abyssal = new TinkerStep(melee.First(r => r.NameMatch == "Abyssal"), TinkerCatalog.BlackGarnet);
        var iron = new TinkerStep(melee.First(r => r.Id == TinkerCatalog.Iron), TinkerCatalog.Iron);

        Check.True(TinkerPlanner.BagMatches(iron, "Salvage (100)", TinkerCatalog.Iron, 100), "full Iron bag");
        Check.True(!TinkerPlanner.BagMatches(iron, "Salvage (60)", TinkerCatalog.Iron, 60), "partial bag refused");
        Check.True(TinkerPlanner.BagMatches(rend, "Salvage (100)", TinkerCatalog.BlackGarnet, 100), "Black Garnet rend bag");
        Check.True(!TinkerPlanner.BagMatches(rend, "Bag of Abyssal-Touched Gems", TinkerCatalog.BlackGarnet, 100), "Abyssal isn't a rend");
        Check.True(TinkerPlanner.BagMatches(abyssal, "Bag of Abyssal-Touched Gems", TinkerCatalog.BlackGarnet, 100), "Abyssal row by name");
        Check.True(TinkerPlanner.BagMatches(rend, "Foolproof Black Garnet", 0, 100), "Foolproof rend by name");
        Check.True(!TinkerPlanner.BagMatches(rend, "Foolproof Black Opal", 0, 100), "Black Opal isn't Black Garnet");
        Check.True(TinkerPlanner.IsFoolproof("Foolproof Imperial Topaz"), "foolproof name");
    }

    private static void BagChoice()
    {
        var bags = new List<TinkerBagChoice> { new(1, 4, 0.60), new(2, 7, 0.85), new(3, 9, 0.95) };
        Check.Eq(TinkerPlanner.SelectBag(bags, 0), 2, "no minimum: best chance");
        Check.Eq(TinkerPlanner.SelectBag(bags, 80), 1, "minimum 80: the cheapest bag that reaches it");
        Check.Eq(TinkerPlanner.SelectBag(bags, 99), -1, "nothing reaches 99");
        var server = new List<TinkerBagChoice> { new(1, 5, null), new(2, 8, null) };
        Check.Eq(TinkerPlanner.SelectBag(server, 50), 1, "unknown chance: best workmanship");
        var tie = new List<TinkerBagChoice> { new(1, 9, 1.0), new(2, 3, 1.0) };
        Check.Eq(TinkerPlanner.SelectBag(tie, 0), 1, "tie: keep the better bag");
    }

    private static void Section()
    {
        // Crafting is one of the Mini Remote's universal tabs (every server), after Quests and Pets.
        Check.Eq(MiniRemoteTabs.Parse("tinker"), MiniRemoteTabs.Crafting, "/ra hub open tinker");
        Check.Eq(MiniRemoteTabs.Parse("crafting"), MiniRemoteTabs.Crafting, "crafting id");
        Check.Eq(MiniRemoteTabs.Crafting, "crafting", "tab id the engine menu sends");
        Check.True(MiniRemoteTabs.Universal.Contains(MiniRemoteTabs.Crafting), "on the tab bar, every server");
        Check.Eq(MiniRemoteTabs.Label(MiniRemoteTabs.Crafting), "Crafting", "tab label");
    }

    private static void Trinkets()
    {
        Check.Eq(TinkerPlanner.Rows(TinkerBench.Trinkets, null, null).Count, 0, "retail has no trinket rows");
        var rows = WithServer(TinkerBench.Trinkets, TrinketRows);
        var s = Settings(("p" + TinkerCatalog.Pyreal, 1), ("p" + TinkerCatalog.Diamond, 1), ("p" + TinkerCatalog.Ruby, 1),
                         ("p" + TinkerCatalog.GromnieHide, 1));
        var all = TinkerPlanner.Plan(TinkerBench.Trinkets, Item(AcObjectClass.Jewelry), s, rows);
        Check.Eq(Seq(all), Seq(TinkerCatalog.Diamond, TinkerCatalog.Ruby, TinkerCatalog.GromnieHide, TinkerCatalog.Pyreal),
            "the rows' order: Diamond, Ruby, Gromnie Hide, Pyreal");
        var some = TinkerPlanner.Plan(TinkerBench.Trinkets, Item(AcObjectClass.Jewelry,
            flags: k => k is TrinketDiamondInt or TrinketGromnieInt ? 2 : 0), s, rows);
        Check.Eq(Seq(some), Seq(TinkerCatalog.Ruby, TinkerCatalog.Pyreal), "applied ints 384 / 386 already 2");
        var capped = TinkerPlanner.Plan(TinkerBench.Trinkets, Item(AcObjectClass.Jewelry, tinks: 10), s, rows);
        Check.Eq(capped.Count, 4, "no-slot rows ignore the tink cap");

        var diamond = new TinkerStep(rows.First(r => r.Id == TinkerCatalog.Diamond), TinkerCatalog.Diamond);
        Check.True(TinkerPlanner.BagMatches(diamond, "Major Diamond Salvage", TinkerCatalog.Diamond, 100, 2), "Major bag at Major");
        Check.True(!TinkerPlanner.BagMatches(diamond, "Minor Diamond Salvage", TinkerCatalog.Diamond, 100, 2), "Minor bag at Major");
        Check.True(!TinkerPlanner.BagMatches(diamond, "Salvage (100)", TinkerCatalog.Diamond, 100, 0), "raw bag isn't prepared");
        Check.True(!TinkerPlanner.BagMatches(diamond, "Major Ruby Salvage", TinkerCatalog.Ruby, 100, 2), "other material");
        Check.True(TinkerPlanner.IsRawTrinketBag(TinkerCatalog.Diamond, "Salvage (100)", TinkerCatalog.Diamond, 100), "raw bag");
        Check.True(!TinkerPlanner.IsRawTrinketBag(TinkerCatalog.Diamond, "Moderate Diamond Salvage", TinkerCatalog.Diamond, 100), "prepared isn't raw");
        Check.True(!TinkerPlanner.IsRawTrinketBag(TinkerCatalog.Diamond, "Salvage (55)", TinkerCatalog.Diamond, 55), "partial raw bag");
        Check.True(TinkerPlanner.IsTierAmmunition("Moderate Ammunition", 1), "Moderate Ammunition");
        Check.True(!TinkerPlanner.IsTierAmmunition("Major Ammunition", 1), "wrong tier");
        Check.True(!TinkerPlanner.IsTierAmmunition("Moderate Diamond Salvage", 1), "not Ammunition");
        Check.Eq(TinkerPlanner.TierOf("Minor Pyreal Salvage"), 0, "Minor");
        Check.Eq(TinkerPlanner.TierOf("Salvage (100)"), -1, "no tier");

        // The server plugin's rule (RynthILT's Leaftide list, as its profile sends it).
        var rule = new ServerTrinketRule(new[] { "compass", "goggles", "scarab", "watch", "puzzle box", "top" }, new[] { "topaz" });
        Check.True(TinkerCatalog.IsTrinket("Spinning Top", rule) && TinkerCatalog.IsTrinket("Copper Pocket Watch", rule)
                   && !TinkerCatalog.IsTrinket("Imperial Topaz Necklace", rule), "trinket names (topaz isn't a top)");
        Check.False(TinkerCatalog.IsTrinket("Copper Pocket Watch", ServerTrinketRule.None), "without a server rule nothing is a trinket");
        Check.True(TinkerCatalog.Targets(TinkerBench.Trinkets, AcObjectClass.Jewelry), "trinkets are jewelry");
    }
}

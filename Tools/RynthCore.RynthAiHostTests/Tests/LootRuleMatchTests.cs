using System;
using System.IO;
using System.Linq;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using N = RynthCore.Loot.VTank.VTankNodeTypes;

namespace RynthCore.RynthAiHostTests.Tests;

// 2026-10-31: "a +9 bludg xbow that rynth sees as gold salvage", with LootSnob, which VTank keeps.
// Fixtures\lootsnob-excerpt.utl is LootSnobV4's melee head, missile and wand (No Wield, Void, War)
// sections and six of its Salvage rules (switched on, as players have them), in the file's own order.
// Two causes:
//  1. "Calc'd Buffed Missile Damage" read max damage x the wand elemental modifier (152). A launcher
//     has no max damage, so it was 0 and every one of LootSnob's 33 missile damage keeps failed; the
//     crossbow fell to "(Salv) Gold". VTank: max damage + (mod - 1) x 100 / 3 + elemental bonus,
//     buffed by the item's cantrips (Legendary Blood Thirst +10): 0 + 54 + 22 + 10 = 86, "Xbow (OD +9)".
//  2. An item whose ID was late was judged without it after the wait ("best-effort"): its stats read
//     0, the keep rules failed and a Salvage rule on its material took it. Now it waits, and is left.
internal static class LootRuleMatchTests
{
    private const uint Player = 0x50000A01;

    public static void Register(Runner r)
    {
        r.Add("loot rule match: a +9 crossbow (LootSnob 'Xbow (OD +9)') is kept, not Gold salvage", PlusNineCrossbow);
        r.Add("loot rule match: without its ID the crossbow waits for it, never the Salvage rule", CrossbowWithoutId);
        r.Add("loot rule match: missile damage of bows, crossbows and atlatls at LootSnob's thresholds", MissileDamageThresholds);
        r.Add("loot rule match: buffed keys add the item's cantrips (Blood Thirst, Defender, Heart/Spirit Thirst, Impen, Hermetic)", BuffedCantrips);
        r.Add("loot rule match: double keys compare as floats (1.20 + 0.09 >= 1.29)", FloatCompare);
        r.Add("loot rule match: tinked damage and tinked target melee (VTank's worked examples)", TinkedNodes);
        r.Add("loot rule match: which conditions need the ID", ConditionsNeedingId);
        r.Add("loot rule match: Decide passes over a rule a known condition fails, in any order", DecideOrder);
        r.Add("loot rule match (R-20261101-133624-7eeb): a name rule 'Trade Note' first keeps none of a corpse's plain items, ID or not", TradeNoteRuleFirst);
        r.Add("loot rule match: /ra loot why lists the rules above that came close, with the value read", LootWhyExplains);
        r.Add("loot rule match: /ra loot why's words, in chat and in the 'Why this loot rule?' window", LootWhyReportWords);
    }

    private static VTankLootProfile Excerpt()
        => VTankLootParser.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lootsnob-excerpt.utl"));

    private static int Spell(string name)
    {
        int id = SpellDatabase.GetIdByName(name);
        if (id == 0) throw new InvalidOperationException($"spell '{name}' is not in SpellData.txt");
        return id;
    }

    private static VTankLootContext Ctx() => new(FakeHost.Create(), Player);

    private static void WithSpells(Item item, params string[] names)
        => FakeHost.SpellIds[unchecked((uint)item.Wo.Id)] = names.Select(n => (uint)Spell(n)).ToArray();

    // A 385 crossbow as ACE's crossbow_elemental mutation makes it: +22, damage mod 2.62, gold,
    // workmanship 6, Legendary Blood Thirst. No max damage (launchers have none).
    private static Item PlusNineXbow(bool withId = true)
    {
        var x = new Item("Blunt Crossbow", AcObjectClass.MissileWeapon)
            .Int(1, 0x100).Int(9, 0x00400000).Int(19, 12000).Int(5, 600).Int(131, 60);
        if (withId)
        {
            x.Int(105, 6).Int(353, 9).Int(158, 2).Int(159, 47).Int(160, 385).Int(204, 22).Int(45, 4).Int(48, 47)
             .Dbl(63, 2.62).Dbl(29, 1.15).Dbl(149, 1.0).Dbl(150, 1.0);
            WithSpells(x, "Legendary Blood Thirst");
        }
        return x;
    }

    private static void PlusNineCrossbow()
    {
        FakeHost.Reset();
        VTankLootProfile p = Excerpt();
        var xbow = PlusNineXbow();
        var ctx = Ctx();
        VTankLootRule? rule = VTankLootEvaluator.FirstMatch(p, xbow, ctx, out int idx);
        Check.NotNull(rule, "a rule takes it");
        Check.Eq(rule?.Name.Trim(), "(M) Xbow (OD +9)", "LootSnob's Xbow (OD +9), as VTank");
        Check.Eq(rule?.Action, VTankLootAction.Keep, "kept");
        Check.Near(VTankLootEvaluator.BuffedMissileDamage(xbow, unchecked((uint)xbow.Wo.Id), ctx), 86.0, 1e-9, "0 + 162/3 + 22 + 10");

        // Without the cantrip it's 76, under every crossbow damage keep, and its 1.15 melee defense is
        // under the 20MD rules: VTank salvages that one too.
        FakeHost.Reset();
        var plain = PlusNineXbow();
        FakeHost.SpellIds.Remove(unchecked((uint)plain.Wo.Id));
        Check.Eq(VTankLootEvaluator.FirstMatch(p, plain, Ctx(), out _)?.Name.Trim(), "(Salv) Gold", "76 with no cantrip: Gold salvage, in VTank too");
    }

    private static void CrossbowWithoutId()
    {
        FakeHost.Reset();
        VTankLootProfile p = Excerpt();
        var bare = PlusNineXbow(withId: false);   // only what the CreateObject shows: no workmanship, stats or spells
        Check.Eq(VTankLootEvaluator.Decide(p, bare, Ctx(), hasIdData: false, out int idx), VTankLootVerdict.NeedsId, "waits for the ID");
        Check.Eq(p.Rules[idx].Name.Trim(), "(M) Bow (OD +10)", "at the first missile rule: its weapon type needs the ID");
        // Judged as if the ID were in (the old best-effort), it read 0s: no keep, and since the
        // workmanship is ID data too it was left; with a Salvage rule on material only it was salvaged.
        Check.Null(VTankLootEvaluator.FirstMatch(p, bare, Ctx(), out _), "judged with 0s nothing keeps it");

        var materialOnly = new VTankLootProfile();
        materialOnly.Rules.AddRange(p.Rules.Where(r => r.Action == VTankLootAction.Keep));
        var salvGold = new VTankLootRule { Name = "Gold any", Action = VTankLootAction.Salvage };
        salvGold.Conditions.Add(new VTankLootCondition(N.LongValKeyE, "0", new[] { "60", "131" }));
        materialOnly.Rules.Add(salvGold);
        Check.Eq(VTankLootEvaluator.FirstMatch(materialOnly, bare, Ctx(), out _)?.Name, "Gold any", "the old best-effort: the material rule took it");
        Check.Eq(VTankLootEvaluator.Decide(materialOnly, bare, Ctx(), hasIdData: false, out _), VTankLootVerdict.NeedsId, "now it waits for the ID");

        // A gem with no rule above that could want it is decided without an ID.
        var gem = new Item("Opal", AcObjectClass.Gem).Int(1, 0x800).Int(19, 300);
        Check.Eq(VTankLootEvaluator.Decide(p, gem, Ctx(), hasIdData: false, out _), VTankLootVerdict.NoMatch, "a gem needs no ID here");
    }

    private static Item Launcher(int weaponType, double mod, int elem)
        => new Item("Launcher", AcObjectClass.MissileWeapon).Int(353, weaponType).Int(159, 47).Int(204, elem).Dbl(63, mod).Dbl(29, 1.0);

    private static string? First(VTankLootProfile p, Item item) => VTankLootEvaluator.FirstMatch(p, item, Ctx(), out _)?.Name.Trim();

    private static void MissileDamageThresholds()
    {
        FakeHost.Reset();
        VTankLootProfile p = Excerpt();
        // LootSnob's maxima at wield 385: crossbow 77, bow 68.6, atlatl 75.3 (2.65 / 2.40 / 2.60 and +22).
        Check.Eq(First(p, Launcher(9, 2.65, 22)), "(M) Xbow (Max 385)", "crossbow 165/3 + 22 = 77");
        Check.Eq(First(p, Launcher(8, 2.40, 22)), "(M) Bow (Max 385)", "bow 140/3 + 22 = 68.67");
        Check.Null(First(p, Launcher(10, 2.60, 22)), "atlatl 75.33: LootSnob's TW (Max 385) is switched off, OD +1 wants 76.3");
        Check.Null(First(p, Launcher(9, 2.65, 21)), "a 76 crossbow (and no material to salvage): no rule");
        Check.Eq(First(p, Launcher(9, 2.65, 22).Int(131, 60).Int(105, 5)), "(M) Xbow (Max 385)", "a gold one at 77 is kept, not salvaged");
        Check.Eq(First(p, Launcher(9, 2.65, 21).Int(131, 60).Int(105, 5)), "(Salv) Gold", "a gold one at 76 is salvaged");
        var epicAtlatl = Launcher(10, 2.60, 22);
        WithSpells(epicAtlatl, "Epic Blood Thirst");
        Check.Eq(First(p, epicAtlatl), "(M) TW (OD +7)", "atlatl 75.33 + Epic Blood Thirst 7 = 82.33");
    }

    private static void BuffedCantrips()
    {
        FakeHost.Reset();
        static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);
        // A fresh context each time: one caches the spells it read for its evaluation pass.
        static bool M(Item i, VTankLootCondition c) => VTankLootEvaluator.Match(new VTankLootRule { Conditions = { c } }, i, Ctx());

        var axe = new Item("Axe").Int(44, 74);
        Check.False(M(axe, C(N.BuffedLongValKeyGE, "84", "218103842")), "74 alone");
        WithSpells(axe, "Legendary Blood Thirst");
        Check.True(M(axe, C(N.BuffedLongValKeyGE, "84", "218103842")), "74 + Legendary Blood Thirst 10 (Decal MaxDamage key)");
        Check.False(M(axe, C(N.BuffedLongValKeyGE, "85", "218103842")), "and no more");
        Check.False(M(axe, C(N.LongValKeyGE, "75", "218103842")), "the plain key is not buffed");

        var coat = new Item("Coat").Int(28, 500);
        WithSpells(coat, "Epic Impenetrability", "Impenetrability VI");
        Check.True(M(coat, C(N.BuffedLongValKeyGE, "560", "28")), "armor 500 + Epic Impen 60; Impen VI (castable) adds nothing");
        Check.False(M(coat, C(N.BuffedLongValKeyGE, "561", "28")), "no more");

        var bow = new Item("Bow").Dbl(29, 1.20);
        WithSpells(bow, "Legendary Defender");
        Check.True(M(bow, C(N.BuffedDoubleValKeyGE, "1.29", "29")), "melee defense 1.20 + Legendary Defender 0.09");
        var sword = new Item("Sword").Dbl(62, 1.10);
        WithSpells(sword, "Major Heart Thirst");
        Check.True(M(sword, C(N.BuffedDoubleValKeyGE, "1.15", "167772172")), "attack (VTank AttackBonus key) 1.10 + Major Heart Thirst 0.05");
        var wand = new Item("Nether Baton").Dbl(152, 1.18).Dbl(144, 0.10);
        WithSpells(wand, "Legendary Spirit Thirst", "Legendary Hermetic Link");
        Check.True(M(wand, C(N.BuffedDoubleValKeyGE, "1.25", "152")), "elemental vs monsters 1.18 + Legendary Spirit Thirst 0.07");
        Check.True(M(wand, C(N.BuffedDoubleValKeyGE, "0.13", "144")), "mana conversion 0.10 x Legendary Hermetic Link 1.30");
        Check.False(M(wand, C(N.BuffedDoubleValKeyGE, "0.131", "144")), "no more");

        var noCtx = new Item("Axe").Int(44, 74);
        Check.True(VTankLootEvaluator.Match(new VTankLootRule { Conditions = { C(N.BuffedLongValKeyGE, "74", "44") } }, noCtx, null), "no context: the plain value");
    }

    private static void FloatCompare()
    {
        var item = new Item("x").Dbl(29, 1.2 + 0.09);   // 1.2899999999999998 as a double
        static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);
        Check.True(VTankLootEvaluator.Match(new VTankLootRule { Conditions = { C(N.DoubleValKeyGE, "1.29", "29") } }, item), "GE as floats");
        Check.True(VTankLootEvaluator.Match(new VTankLootRule { Conditions = { C(N.DoubleValKeyLE, "1.29", "29") } }, item), "LE as floats");
        Check.False(VTankLootEvaluator.Match(new VTankLootRule { Conditions = { C(N.DoubleValKeyGE, "1.2901", "29") } }, item), "a real difference still counts");
    }

    private static void TinkedNodes()
    {
        FakeHost.Reset();
        static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);
        // VTank's worked example: 50 max damage, variance 0.2, unimbued, untinkered: 9 tinks
        // (2 granite, 7 iron) -> 84.4344.
        var w = new Item("Sword", AcObjectClass.MeleeWeapon).Int(44, 50).Dbl(22, 0.2).Int(131, 61);
        Check.Near(VTankLootEvaluator.BuffedTinkedDamage(w, unchecked((uint)w.Wo.Id), null), 84.4344, 1e-4, "tinked damage 84.4344");
        Check.True(VTankLootEvaluator.Match(new VTankLootRule { Conditions = { C(N.CalcdBuffedTinkedDamageGE, "84.43") } }, w), "node 2006 at 84.43");
        var imbued = new Item("Sword", AcObjectClass.MeleeWeapon).Int(44, 50).Dbl(22, 0.2).Int(131, 61).Int(179, 1).Int(171, 3);
        Check.True(VTankLootEvaluator.BuffedTinkedDamage(imbued, unchecked((uint)imbued.Wo.Id), null) < 84.4344, "imbued with 3 tinks done: 7 left, less damage");

        // Target melee: the same item with no bonuses spends its 9 tinks on melee defense to 0.05,
        // attack to 0.04, and none on damage: 74.74 (= (50 + 24) x 1.01).
        var noBonus = new Item("Sword", AcObjectClass.MeleeWeapon).Int(44, 50).Dbl(22, 0.2).Int(131, 61);
        uint id = unchecked((uint)noBonus.Wo.Id);
        Check.True(VTankLootEvaluator.MatchTinkedTargetMelee(noBonus, id, null, new[] { "74.74", "0.05", "0.04" }, out double dot), "reaches 0.05 / 0.04 / 74.74");
        Check.Near(dot, 74.74, 1e-9, "damage with no tinks left");
        Check.False(VTankLootEvaluator.MatchTinkedTargetMelee(noBonus, id, null, new[] { "1", "0.06", "0.04" }, out _), "melee 0.06 is out of reach");
        Check.False(VTankLootEvaluator.MatchTinkedTargetMelee(noBonus, id, null, new[] { "1", "0.05", "0.05" }, out _), "attack 0.05 is out of reach");
        // LootSnob's "(H) 430 Insane": 109 at 1.17 / 1.17.
        var mace = new Item("Mace", AcObjectClass.MeleeWeapon).Int(44, 80).Dbl(22, 0.25).Dbl(29, 1.17).Dbl(62, 1.17).Int(131, 57);
        Check.True(VTankLootEvaluator.Match(new VTankLootRule { Conditions = { C(N.CalcedBuffedTinkedTargetMeleeGE, "109", "1.17", "1.17") } }, mace), "an 80 mace at 1.17/1.17 reaches 109");
    }

    private static void ConditionsNeedingId()
    {
        static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.ObjectClass, "9")), "class");
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.StringValueMatch, "Bow", "1")), "name");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(N.StringValueMatch, "x", "16")), "long description");
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.LongValKeyE, "60", "131")), "material");
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.LongValKeyGE, "1000", "19")), "value");
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.LongValKeyE, "27704", "218103849")), "icon overlay (Decal key)");
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.LongValKeyE, "104", "218103821")), "coverage (Decal key)");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(N.LongValKeyGE, "3", "105")), "workmanship comes with the ID");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(N.LongValKeyE, "9", "353")), "weapon type");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(N.LongValKeyE, "47", "159")), "wield skill");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(N.LongValKeyGE, "70", "218103842")), "max damage (Decal key)");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(N.DoubleValKeyGE, "1.2", "29")), "melee defense");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(N.BuffedMissileDamageGE, "77")), "missile damage");
        Check.True(VTankLootEvaluator.ConditionNeedsId(C(N.SpellNameMatch, "Legendary")), "spells");
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.CharacterMainPackEmptySlotsGE, "7")), "pack slots");
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.DisabledRule, "true")), "disabled");
        Check.False(VTankLootEvaluator.ConditionNeedsId(C(N.SlotExactPalette, "1", "1682")), "palette");
    }

    private static void DecideOrder()
    {
        static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);
        var bowRule = new VTankLootRule { Name = "bow keep", Action = VTankLootAction.Keep };
        bowRule.Conditions.Add(C(N.LongValKeyE, "8", "353"));      // ID first
        bowRule.Conditions.Add(C(N.ObjectClass, "9"));             // class after
        var name = new VTankLootRule { Name = "trade note", Action = VTankLootAction.Keep };
        name.Conditions.Add(C(N.StringValueMatch, "Trade Note", "1"));
        var p = new VTankLootProfile();
        p.Rules.AddRange(new[] { bowRule, name });

        var note = new Item("Trade Note (250,000)", AcObjectClass.TradeNote);
        Check.Eq(VTankLootEvaluator.Decide(p, note, null, hasIdData: false, out int i1), VTankLootVerdict.Match, "the bow rule's class rules a note out, though it comes second");
        Check.Eq(i1, 1, "the name rule takes it with no ID");
        var bow = new Item("Bow", AcObjectClass.MissileWeapon);
        Check.Eq(VTankLootEvaluator.Decide(p, bow, null, hasIdData: false, out int i2), VTankLootVerdict.NeedsId, "a bow waits for the bow rule's ID");
        Check.Eq(i2, 0, "at the bow rule");
        Check.Eq(VTankLootEvaluator.Decide(p, bow, null, hasIdData: true, out _), VTankLootVerdict.NoMatch, "with the ID (no weapon type 8) nothing takes it");
        Check.Eq(VTankLootEvaluator.Decide(new VTankLootProfile(), bow, null, hasIdData: false, out int i3), VTankLootVerdict.NoMatch, "empty profile");
        Check.Eq(i3, -1, "index -1");
    }

    // R-20261101-133624-7eeb (Conquest, 2026.10.31.3): every item on a corpse was kept by a rule named
    // 'Trade Note' (Imperial Topaz, Frost Child Essence and Pyreal with no ID; Gloves and Flagon with
    // it). LootSnob's 'Trade Note' is one StringValueMatch on the Name ("Trade Note", key 1): it keeps
    // none of them, with or without the ID, here and through the whole fixture. A rule with no
    // conditions keeps everything (VTank's unconditional rule), so a 'Trade Note' that lost its
    // condition in the player's own profile would do exactly what the report shows.
    private static void TradeNoteRuleFirst()
    {
        FakeHost.Reset();
        static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);
        var tradeNote = new VTankLootRule { Name = "Trade Note", Action = VTankLootAction.Keep };
        tradeNote.Conditions.Add(C(N.StringValueMatch, "Trade Note", "1"));
        var p = new VTankLootProfile();
        p.Rules.Add(tradeNote);
        p.Rules.AddRange(Excerpt().Rules);

        var items = new (Item Item, bool Id)[]
        {
            (new Item("Imperial Topaz", AcObjectClass.Gem).Int(1, 0x800).Int(19, 6094), false),
            (new Item("Frost Child Essence (180)", AcObjectClass.Misc).Int(1, 0x80).Int(19, 9000), false),
            (new Item("Pyreal", AcObjectClass.Money).Int(1, 0x40).Int(19, 693).Int(12, 693), false),
            (new Item("Gloves", AcObjectClass.Clothing).Int(1, 0x4).Int(19, 24429).Int(105, 5), true),
            (new Item("Flagon", AcObjectClass.MissileWeapon).Int(1, 0x100).Int(19, 12741).Int(105, 4), true),
        };
        foreach (var (item, id) in items)
        {
            VTankLootVerdict v = VTankLootEvaluator.Decide(p, item, Ctx(), hasIdData: id, out int idx);
            Check.True(!(v == VTankLootVerdict.Match && idx == 0), $"{item.Wo.Name}: not kept by 'Trade Note' ({v}{(idx >= 0 ? " at " + p.Rules[idx].Name.Trim() : "")})");
            Check.True(VTankLootEvaluator.FirstMatch(p, item, Ctx(), out int fi)?.Name != "Trade Note", $"{item.Wo.Name}: FirstMatch not 'Trade Note' ({fi})");
        }
        var note = new Item("Trade Note (250,000)", AcObjectClass.TradeNote).Int(1, 0x80);
        Check.Eq(VTankLootEvaluator.Decide(p, note, Ctx(), hasIdData: false, out int ni), VTankLootVerdict.Match, "a trade note is kept with no ID");
        Check.Eq(ni, 0, "by 'Trade Note'");

        // The same rule with its condition gone keeps everything: what the report looks like.
        var empty = new VTankLootRule { Name = "Trade Note", Action = VTankLootAction.Keep };
        var broken = new VTankLootProfile();
        broken.Rules.Add(empty);
        Check.Eq(VTankLootEvaluator.FirstMatch(broken, items[0].Item, Ctx(), out _)?.Name, "Trade Note", "a 'Trade Note' with no condition keeps a topaz (unconditional rule)");
    }

    private static void LootWhyExplains()
    {
        FakeHost.Reset();
        VTankLootProfile p = Excerpt();
        var plain = PlusNineXbow();
        FakeHost.SpellIds.Remove(unchecked((uint)plain.Wo.Id));   // 76: Gold salvage
        LootWhy.Result why = LootWhy.Explain(p, plain, Ctx(), hasIdData: true);
        Check.Eq(why.Verdict, VTankLootVerdict.Match, "matched");
        Check.Eq(why.RuleName, "(Salv) Gold", "Gold salvage");
        var max = why.NearMisses.FirstOrDefault(m => m.RuleName == "(M) Xbow (Max 385)");
        Check.Eq(max.RuleName, "(M) Xbow (Max 385)", "Xbow (Max 385) came close");
        Check.True(max.Failed.StartsWith("Buffed Missile Damage >= 77: read 76", StringComparison.Ordinal), $"its failed condition and the value read ({max.Failed})");
        Check.True(max.Failed.Contains("mod 2.62") && max.Failed.Contains("elemental 22"), "and how it was made");
        Check.False(why.NearMisses.Any(m => m.RuleName.StartsWith("(M) Bow", StringComparison.Ordinal)), "bow rules failed on their first condition: not listed");
        Check.False(why.NearMisses.Any(m => m.RuleName.Contains("Max 375")), "switched-off rules are not listed");
        Check.True(why.NearMisses.Any(m => m.RuleName == "(M) Xbow (20MD)" && m.Failed.Contains("read 1.15")), "the 20MD rules read its 1.15 melee defense");

        var bare = PlusNineXbow(withId: false);
        LootWhy.Result wait = LootWhy.Explain(p, bare, Ctx(), hasIdData: false);
        Check.Eq(wait.Verdict, VTankLootVerdict.NeedsId, "no ID: waits");
        Check.Eq(wait.RuleName, "(M) Bow (OD +10)", "names the rule that needs it");
    }

    // 2026-10-31: the same answer as a report the chat lines and the window are both made from.
    private static void LootWhyReportWords()
    {
        FakeHost.Reset();
        VTankLootProfile p = Excerpt();
        var plain = PlusNineXbow();
        FakeHost.SpellIds.Remove(unchecked((uint)plain.Wo.Id));
        LootWhy.Result why = LootWhy.Explain(p, plain, Ctx(), hasIdData: true);
        LootWhy.Report r = LootWhy.Describe("Blunt Crossbow", true, "[Salvage] (Salv) Gold", "lootsnob.utl", p.Rules.Count, why, askedForId: false);
        Check.True(r.Ok && r.RuleDetail, "a VTank profile: rule-by-rule");
        Check.Eq(r.Headline, "'Blunt Crossbow' (identified) -> [Salvage] (Salv) Gold", "the headline: the item, its ID, the looter");
        Check.True(r.Decision.StartsWith($"lootsnob.utl: rule {why.Index + 1} of {p.Rules.Count} '(Salv) Gold' [", StringComparison.Ordinal), $"the decision names the rule ({r.Decision})");
        Check.Eq(r.NearMisses.Count, why.NearMisses.Count, "every near miss is in the report (the window lists them all)");

        var lines = LootWhy.ChatLines(r, 2);
        Check.Eq(lines[0], "[RynthAi] Loot why: " + r.Headline, "chat line 1: the headline");
        Check.Eq(lines[1], "[RynthAi]   " + r.Decision, "chat line 2: the decision");
        Check.True(lines[2].Contains($"{r.NearMisses.Count} rule(s) above it came close"), "then how many came close");
        Check.Eq(lines[3], "[RynthAi]   " + LootWhy.NearMissLine(r.NearMisses[0]), "then each, as #N 'name': failed");
        Check.True(r.NearMisses.Count <= 2 || lines[^1].Contains($"{r.NearMisses.Count - 2} more in the log"), "past the chat's limit: the rest are counted (the log has them)");
        Check.Eq(lines.Count, 3 + Math.Min(2, r.NearMisses.Count) + (r.NearMisses.Count > 2 ? 1 : 0), "no other lines");
        Check.True(LootWhy.NearMissLine(new LootWhy.NearMiss(4, "Rule", "x: read 1")) == "#5 'Rule': x: read 1", "a near miss counts from 1");

        LootWhy.Result wait = LootWhy.Explain(p, PlusNineXbow(withId: false), Ctx(), hasIdData: false);
        LootWhy.Report w = LootWhy.Describe("Blunt Crossbow", false, "left", "lootsnob.utl", p.Rules.Count, wait, askedForId: true);
        Check.True(w.Decision.EndsWith("needs the item's ID to decide", StringComparison.Ordinal) && w.Headline.Contains("(not identified yet)"), "no ID: which rule waits for it");
        Check.True(LootWhy.ChatLines(w, 10).Contains("[RynthAi]   Asked for its ID; /ra loot why again once it's in."), "and that it asked for it");

        LootWhy.Report native = LootWhy.Describe("Blunt Crossbow", true, "[Keep] Xbows", "", 0, null, false);
        Check.False(native.RuleDetail, "RynthAi's own (.json) profile: no rule-by-rule detail");
        var one = LootWhy.ChatLines(native, 10);
        Check.Eq(one.Count, 1, "one chat line");
        Check.True(one[0].EndsWith("(Rule-by-rule detail is for VTank .utl profiles.)", StringComparison.Ordinal), "that says so");

        LootWhy.Report rares = LootWhy.ModeOnly("Blunt Crossbow", true, "left", "Loot Mode: Rares and needed mana stones only (the loot profile isn't used)");
        Check.True(rares.Ok && !rares.RuleDetail, "rares mode: the mode decides, no rule detail");
        Check.Eq(LootWhy.ChatLines(rares, 10).Single(),
            "[RynthAi] Loot why: 'Blunt Crossbow' (identified) -> left. Loot Mode: Rares and needed mana stones only (the loot profile isn't used).",
            "chat: one line naming the Loot Mode, not the .utl note");

        LootWhy.Report fail = LootWhy.Report.Fail("select an item first");
        Check.False(fail.Ok, "a failed report");
        Check.Eq(fail.Headline, "select an item first", "its headline is the reason");
        Check.Eq(LootWhy.ChatLines(fail, 10).Single(), "[RynthAi] loot why: select an item first", "chat: one line, the reason");
    }
}

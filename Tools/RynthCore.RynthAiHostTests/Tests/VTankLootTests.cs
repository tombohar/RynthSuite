using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Loot;
using RynthCore.Loot.VTank;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;
using N = RynthCore.Loot.VTank.VTankNodeTypes;

namespace RynthCore.RynthAiHostTests.Tests;

// VTank (.utl) loot rules: VTankLootEvaluator per node type, first-match rule order
// (FirstMatch, which corpse looting uses), the character/spell context read through the
// fake host, and whole .utl texts parsed and then evaluated.
internal static class VTankLootTests
{
    private const uint Player = 0x50000A01;

    public static void Register(Runner r)
    {
        r.Add("loot vtank: no item never matches; no conditions always does", NullAndEmpty);
        r.Add("loot vtank: first match in list order, Priority ignored", RuleOrder);
        r.Add("loot vtank: DisabledRule node (9999)", DisabledRuleNode);
        r.Add("loot vtank: string value match", StringValueMatch);
        r.Add("loot vtank: long keys LE/GE/E/NE and flag, at the boundary", LongKeys);
        r.Add("loot vtank: Decal's Type key (0x0D000000) is the WCID - 'loot everything that isn't retail'", WcidKey);
        r.Add("loot vtank: Decal's long keys (MaxDamage, Coverage, EquipType, IconOverlay) read the game property", DecalLongKeys);
        r.Add("loot vtank: double keys LE/GE", DoubleKeys);
        r.Add("loot vtank: VTank's 0x0A0000xx double keys (AttackBonus, DamageBonus, Variance) read 62/63/22", VTankDoubleKeys);
        r.Add("loot vtank: object class", ObjectClass);
        r.Add("loot vtank: value and workmanship requirements", ValueAndWorkmanship);
        r.Add("loot vtank: damage nodes (min, median, missile, tinked, percent)", DamageNodes);
        r.Add("loot vtank: buffed long/double keys and total ratings", BuffedKeysAndRatings);
        r.Add("loot vtank: colour nodes pass, not-implemented nodes fail", ColourAndUnimplemented);
        r.Add("loot vtank: spell nodes pass with no context", SpellNodesNoContext);
        r.Add("loot vtank: spell name match / spell match / spell count through the host", SpellNodesWithContext);
        r.Add("loot vtank: spell nodes with a host that cannot list spells", SpellNodesNoSpellApi);
        r.Add("loot vtank: character skill, base skill, level, pack slots", CharacterNodes);
        r.Add("loot vtank: slot exact palette", SlotExactPalette);
        r.Add("loot vtank: malformed data lines make the rule not match", MalformedData);
        r.Add("loot vtank: unknown node type", UnknownNodeType);
        r.Add("loot vtank: .utl text parsed then evaluated (keep, keep #, salvage, sell, read)", ParsedProfile);
        r.Add("loot vtank: fixture file with a SalvageCombine block", FixtureFile);
        r.Add("loot vtank: malformed .utl text", MalformedUtl);
        r.Add("loot keep#: counts whole stacks of what the rule matches", KeepCapCountsStacks);
        r.Add("loot keep#: pending approvals and merged stacks", KeepCapPending);
        r.Add("loot keep#: the cap boundary, zero and missing counts", KeepCapBoundary);
        r.Add("loot keep#: a pack stack is judged by one unit's value and burden", KeepCapOneUnit);
        r.Add("loot keep#: the one-unit view of a stack", KeepCapOneUnitView);
    }

    private static VTankLootCondition C(int node, params string[] data) => new(node, "0", data);

    private static VTankLootRule Rule(string name, VTankLootAction action, params VTankLootCondition[] conds)
    {
        var r = new VTankLootRule { Name = name, Action = action };
        r.Conditions.AddRange(conds);
        return r;
    }

    private static bool M(WorldObject item, params VTankLootCondition[] conds)
        => VTankLootEvaluator.Match(Rule("r", VTankLootAction.Keep, conds), item);

    private static bool MC(WorldObject item, VTankLootContext ctx, params VTankLootCondition[] conds)
        => VTankLootEvaluator.Match(Rule("r", VTankLootAction.Keep, conds), item, ctx);

    private static void NullAndEmpty()
    {
        Check.False(VTankLootEvaluator.Match(Rule("r", VTankLootAction.Keep), null), "null item");
        Check.True(VTankLootEvaluator.Match(Rule("r", VTankLootAction.Keep), new Item("x")), "no conditions = unconditional");
        Check.True(VTankLootEvaluator.Match(Rule("r", VTankLootAction.Keep), new Item("")), "even with an empty name");
    }

    private static void RuleOrder()
    {
        var sell = Rule("sell junk", VTankLootAction.Sell, C(N.ObjectClass, "11"));
        sell.Priority = 99;
        var keep = Rule("keep gems", VTankLootAction.Keep, C(N.ObjectClass, "11"));
        keep.Priority = 1;
        var profile = new VTankLootProfile();
        profile.Rules.AddRange(new[] { sell, keep });
        var gem = new Item("Opal", AcObjectClass.Gem);

        var hit = VTankLootEvaluator.FirstMatch(profile, gem, null, out int idx);
        Check.True(ReferenceEquals(hit, sell), "the first rule in the list wins even with a worse priority");
        Check.Eq(idx, 0, "index of the winner");

        var miss = VTankLootEvaluator.FirstMatch(profile, new Item("Plate", AcObjectClass.Armor), null, out int idx2);
        Check.Null(miss, "no rule for armor");
        Check.Eq(idx2, -1, "index -1 when nothing matches");

        var none = VTankLootEvaluator.FirstMatch(new VTankLootProfile(), gem, null, out int idx3);
        Check.Null(none, "empty profile");
        Check.Eq(idx3, -1, "empty profile index");

        var nullItem = VTankLootEvaluator.FirstMatch(profile, null, null, out _);
        Check.Null(nullItem, "null item matches nothing");

        // A rule that throws inside (bad data) does not stop later rules from matching.
        var broken = Rule("broken", VTankLootAction.Keep, C(N.LongValKeyGE, "abc", "19"));
        var profile2 = new VTankLootProfile();
        profile2.Rules.AddRange(new[] { broken, keep });
        Check.True(ReferenceEquals(VTankLootEvaluator.FirstMatch(profile2, gem, null, out int i4), keep) && i4 == 1, "broken rule skipped");
    }

    private static void DisabledRuleNode()
    {
        var item = new Item("x");
        Check.False(M(item, C(N.DisabledRule, "true")), "true disables");
        Check.False(M(item, C(N.DisabledRule, " TRUE ")), "any case, trimmed");
        Check.True(M(item, C(N.DisabledRule, "false")), "false leaves it enabled");
        Check.True(M(item, C(N.DisabledRule, "yes")), "anything but true leaves it enabled");
        Check.True(M(item, new VTankLootCondition(N.DisabledRule, "0", Array.Empty<string>())), "no data line: enabled");

        var r = Rule("r", VTankLootAction.Keep, C(N.ObjectClass, "8"));
        r.Enabled = false;
        Check.False(VTankLootEvaluator.Match(r, new Item("x", AcObjectClass.Misc)), "Enabled=false adds a disabling node");
        r.Enabled = true;
        Check.True(VTankLootEvaluator.Match(r, new Item("x", AcObjectClass.Misc)), "Enabled=true removes it again");
    }

    private static void StringValueMatch()
    {
        var kit = new Item("Peerless Healing Kit");
        Check.True(M(kit, C(N.StringValueMatch, "healing kit", "1")), "name via the object-name fallback, any case");
        Check.False(M(kit, C(N.StringValueMatch, "^Healing", "1")), "anchored mismatch");
        Check.False(M(kit, C(N.StringValueMatch, "Healing Kit (", "1")), "a bad regex is no match, not an exception");
        var scroll = new Item("Scroll").Str(16, "Inscribed");
        Check.True(M(scroll, C(N.StringValueMatch, "^inscribed$", "16")), "long description key");
        Check.True(M(scroll, C(N.StringValueMatch, "^$", "15")), "missing string reads empty");
    }

    private static void LongKeys()
    {
        var item = new Item("x").Int(19, 1000).Int(9, 0x6);
        Check.True(M(item, C(N.LongValKeyGE, "1000", "19")), "GE equal");
        Check.False(M(item, C(N.LongValKeyGE, "1001", "19")), "GE above");
        Check.True(M(item, C(N.LongValKeyLE, "1000", "19")), "LE equal");
        Check.False(M(item, C(N.LongValKeyLE, "999", "19")), "LE below");
        Check.True(M(item, C(N.LongValKeyE, "1000", "19")), "E");
        Check.False(M(item, C(N.LongValKeyNE, "1000", "19")), "NE same");
        Check.True(M(item, C(N.LongValKeyNE, "0", "19")), "NE different");
        Check.True(M(item, C(N.LongValKeyFlagExists, "2", "9")), "flag set");
        Check.False(M(item, C(N.LongValKeyFlagExists, "8", "9")), "flag not set");
        Check.True(M(item, C(N.LongValKeyLE, "5", "105")), "missing key reads 0 and passes LE");
        Check.True(M(item, C(N.LongValKeyGE, " 1000 ", " 19 ")), "surrounding spaces in data lines are accepted");
    }

    // Tom (2026-10-04): a rule that loots everything not retail by weenie id. Retail WCIDs on
    // Aelrynth end at 53488; custom content is above. VTank writes it as LongValKeyGE on Decal's
    // synthetic "Type" key, which RynthAi now reads as the WCID through the object cache.
    private static void WcidKey()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var cache = new WorldObjectCache(host);
        const uint Custom = 0x80001001, Retail = 0x80001002;
        FakeHost.Wcids[Custom] = 3000105;
        FakeHost.Wcids[Retail] = 20646;
        var custom = new WorldObject(unchecked((int)Custom), "Driftwarden Greataxe", AcObjectClass.MeleeWeapon) { Cache = cache };
        var retail = new WorldObject(unchecked((int)Retail), "Ust", AcObjectClass.Ust) { Cache = cache };
        string type = WorldObjectCache.DecalTypeKey.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Check.Eq(custom.Values(unchecked((int)WorldObjectCache.DecalTypeKey), 0), 3000105, "the Type key reads the WCID");
        Check.True(M(custom, C(N.LongValKeyGE, "53489", type)), "custom WCID 3000105 >= 53489: looted");
        Check.False(M(retail, C(N.LongValKeyGE, "53489", type)), "retail WCID 20646: not");
        Check.True(M(retail, C(N.LongValKeyE, "20646", type)), "an exact WCID match works too");
    }

    // 2026-10-07: LootSnobV4's "(2H) Cleaving MAX 350" (Buffed Long Key >= on 218103842) never kept
    // anything: Decal's synthetic keys were read as STypeInt ids no object has. 336 of its rules
    // use MaxDamage, 109 Coverage, 83 IconOverlay.
    private static void DecalLongKeys()
    {
        var axe = new Item("Greataxe", AcObjectClass.MeleeWeapon).Int(44, 30).Int(51, 5).Int(48, 41);
        Check.True(M(axe, C(N.BuffedLongValKeyGE, "25", "218103842")), "MaxDamage 30 >= 25");
        Check.False(M(axe, C(N.BuffedLongValKeyGE, "31", "218103842")), "MaxDamage 30 < 31");
        Check.True(M(axe, C(N.LongValKeyGE, "30", "218103842")), "plain Long Key too");
        Check.True(M(axe, C(N.LongValKeyE, "5", "218103823")), "EquipType = CombatUse (5, two-handed)");
        Check.True(M(axe, C(N.LongValKeyE, "41", "218103840")), "EquipSkill = WeaponSkill");
        var shirt = new Item("Shirt", AcObjectClass.Clothing).Int(4, 0x1A);
        Check.True(M(shirt, C(N.LongValKeyFlagExists, "2", "218103821")), "Coverage flag reads ClothingPriority");
        Check.False(M(shirt, C(N.LongValKeyFlagExists, "1", "218103821")), "a flag it lacks");
        Check.Eq(new Item("Stick").Wo.Values(218103820, -7), -7, "an unmapped Decal key (Monarch) answers the default");

        FakeHost.Reset();
        var host = FakeHost.Create();
        var cache = new WorldObjectCache(host);
        const uint Rare = 0x80001101;
        FakeHost.DataIds[(Rare, 50)] = 0x06006C38;
        var rare = new WorldObject(unchecked((int)Rare), "Pearl of Blood Drinking", AcObjectClass.Misc) { Cache = cache };
        Check.Eq(rare.Values(218103849, 0), 27704, "IconOverlay is the overlay's portal index, as Decal gives it");
        Check.True(M(rare, C(N.LongValKeyE, "27704", "218103849")), "an IconOverlay rule matches");
        Check.Eq(rare.Values(218103850, -1), -1, "no underlay: the default");
    }

    private static void DoubleKeys()
    {
        var item = new Item("x").Dbl(29, 1.25);
        Check.True(M(item, C(N.DoubleValKeyGE, "1.25", "29")), "GE equal");
        Check.False(M(item, C(N.DoubleValKeyGE, "1.26", "29")), "GE above");
        Check.True(M(item, C(N.DoubleValKeyLE, "1.25", "29")), "LE equal");
        Check.False(M(item, C(N.DoubleValKeyLE, "1.24", "29")), "LE below");
        Check.True(M(item, C(N.DoubleValKeyGE, "1e0", "29")), "exponent notation parses");
    }

    // VTank's editor writes the weapon profile's attack/damage mods under Decal's synthetic keys
    // (uTank2 LootPlugins.DoubleValueKey AttackBonus 0x0A00000C, DamageBonus 0x0A00000E,
    // Variance 0x0A00000B). LootSnobV4's "(H) Axe (AT +8)" rules are BuffedDoubleValKeyGE
    // 1.31 on 167772172; read raw the key was never set, so they could not match.
    private static void VTankDoubleKeys()
    {
        var axe = new Item("Axe").Dbl(62, 1.31).Dbl(63, 1.2).Dbl(22, 0.4);
        const string attack = "167772172", damage = "167772174", variance = "167772171";
        Check.True(M(axe, C(N.BuffedDoubleValKeyGE, "1.31", attack)), "LootSnob AT rule: attack 1.31 >= 1.31");
        Check.False(M(axe, C(N.BuffedDoubleValKeyGE, "1.32", attack)), "attack just above");
        Check.True(M(axe, C(N.DoubleValKeyGE, "1.2", damage)), "DamageBonus reads DamageMod 63");
        Check.False(M(axe, C(N.DoubleValKeyGE, "1.21", damage)), "not WeaponOffense");
        Check.True(M(axe, C(N.DoubleValKeyLE, "0.4", variance)), "Variance reads DamageVariance 22");
        Check.False(M(axe, C(N.DoubleValKeyLE, "0.39", variance)), "variance below");
        Check.False(M(new Item("Stick"), C(N.DoubleValKeyGE, "0.1", attack)), "no attack mod: reads 0");

        var armor = new Item("Coat").Dbl(13, 1.6).Dbl(19, 0.8);
        Check.True(M(armor, C(N.DoubleValKeyGE, "1.6", "167772160")), "SlashProt reads ArmorModVsSlash 13");
        Check.True(M(armor, C(N.DoubleValKeyLE, "0.8", "167772164")), "LightningProt reads ArmorModVsElectric 19");
    }

    private static void ObjectClass()
    {
        Check.True(M(new Item("x", AcObjectClass.Salvage), C(N.ObjectClass, "39")), "salvage is class 39");
        Check.False(M(new Item("x", AcObjectClass.Gem), C(N.ObjectClass, "39")), "gem is not");
        Check.True(M(new Item("x", AcObjectClass.MeleeWeapon), C(N.ObjectClass, "1")), "melee weapon is 1");
    }

    private static void ValueAndWorkmanship()
    {
        // "Keep anything worth 5000+"; "salvage workmanship 7+ granite"
        var valuable = Rule("value", VTankLootAction.Keep, C(N.LongValKeyGE, "5000", "19"));
        Check.True(VTankLootEvaluator.Match(valuable, new Item("x").Int(19, 5000)), "value at the threshold");
        Check.False(VTankLootEvaluator.Match(valuable, new Item("x").Int(19, 4999)), "value one below");
        Check.False(VTankLootEvaluator.Match(valuable, new Item("x")), "unappraised value reads 0");

        var granite = Rule("granite", VTankLootAction.Salvage,
            C(N.ObjectClass, "39"), C(N.LongValKeyE, "67", "131"), C(N.LongValKeyGE, "7", "105"));
        Check.True(VTankLootEvaluator.Match(granite, new Item("Salvage", AcObjectClass.Salvage).Int(131, 67).Int(105, 7)), "wk 7 granite bag");
        Check.False(VTankLootEvaluator.Match(granite, new Item("Salvage", AcObjectClass.Salvage).Int(131, 67).Int(105, 6)), "wk 6");
        Check.False(VTankLootEvaluator.Match(granite, new Item("Salvage", AcObjectClass.Salvage).Int(131, 64).Int(105, 9)), "steel");
    }

    private static void DamageNodes()
    {
        var sword = new Item("Sword").Int(44, 40).Dbl(22, 0.5);   // min 20, median 30
        Check.True(M(sword, C(N.MinDamageGE, "20")), "min 20");
        Check.False(M(sword, C(N.MinDamageGE, "20.5")), "min above");
        Check.True(M(sword, C(N.BuffedMedianDamageGE, "30")), "median 30");
        Check.False(M(sword, C(N.BuffedMedianDamageGE, "30.1")), "median above");
        // No material: it can't be tinked, so the damage over time is max + Blood Drinker's 24,
        // 10% crits at double: (40 + 24) x (0.9 x 1.5 / 2 + 0.2) = 56
        Check.True(M(sword, C(N.CalcdBuffedTinkedDamageGE, "56")), "tinked damage: no material, no tinks");
        Check.False(M(sword, C(N.CalcdBuffedTinkedDamageGE, "56.01")), "tinked damage just above");
        Check.False(M(sword, C(N.DamagePercentGE, "0")), "damage percent always fails (as VTank)");
        var nodmg = new Item("Stick");
        Check.False(M(nodmg, C(N.MinDamageGE, "0")), "no max damage: min damage fails even at 0");
        Check.False(M(nodmg, C(N.BuffedMedianDamageGE, "0")), "no max damage: median fails");
        Check.False(M(nodmg, C(N.BuffedMissileDamageGE, "0")), "no damage modifier (not a launcher): missile fails");

        // Missile damage = max damage + (damage mod - 1) x 100 / 3 + elemental bonus (204); a
        // launcher has no max damage. VTank's own example: a 50 max damage bow with mod 1.15 is 55.
        var bow = new Item("Bow").Int(44, 50).Dbl(63, 1.15);
        Check.True(M(bow, C(N.BuffedMissileDamageGE, "55")), "50 + 15/3 = 55");
        Check.False(M(bow, C(N.BuffedMissileDamageGE, "55.01")), "just above");
        var xbow = new Item("Crossbow").Dbl(63, 2.65).Int(204, 22);
        Check.True(M(xbow, C(N.BuffedMissileDamageGE, "77")), "a 385 crossbow: 165/3 + 22 = 77");
        Check.False(M(xbow, C(N.BuffedMissileDamageGE, "77.01")), "and no more");
        var noElem = new Item("Crossbow").Dbl(63, 2.2);
        Check.True(M(noElem, C(N.BuffedMissileDamageGE, "40")), "no elemental bonus reads 0: 120/3 = 40");
    }

    private static void BuffedKeysAndRatings()
    {
        var item = new Item("x").Int(28, 300).Dbl(29, 1.3);
        Check.True(M(item, C(N.BuffedLongValKeyGE, "300", "28")), "buffed long reads the same key");
        Check.True(M(item, C(N.BuffedLongValKeyGE, "299.5", "28")), "value may be fractional");
        Check.False(M(item, C(N.BuffedLongValKeyGE, "300.5", "28")), "fractional above");
        Check.True(M(item, C(N.BuffedDoubleValKeyGE, "1.3", "29")), "buffed double");
        var gear = new Item("Gear").Int(370, 3).Int(379, 2).Int(377, 10);
        Check.True(M(gear, C(N.TotalRatingsGE, "5")), "370 + 379");
        Check.False(M(gear, C(N.TotalRatingsGE, "6")), "377 not counted");
    }

    private static void ColourAndUnimplemented()
    {
        var item = new Item("x");
        Check.True(M(item, C(N.AnySimilarColor, "1", "2", "3", "4", "5")), "any similar colour passes");
        Check.True(M(item, C(N.SimilarColorArmorType, "1", "2", "3", "4", "5", "6")), "armor-type colour passes");
        Check.True(M(item, C(N.SlotSimilarColor, "1", "2", "3", "4", "5", "6")), "slot colour passes");
        Check.False(M(item, C(N.CalcedBuffedTinkedTargetMeleeGE, "1", "1", "1")), "tinked target melee: no max damage, fails");
    }

    private static void SpellNodesNoContext()
    {
        var item = new Item("x");
        Check.True(M(item, C(N.SpellNameMatch, "Blood Drinker")), "spell name match with no context");
        Check.True(M(item, C(N.SpellMatch, "Blood", "", "1")), "spell match with no context");
        Check.True(M(item, C(N.SpellCountGE, "5")), "spell count with no context");
        Check.True(M(item, C(N.CharacterSkillGE, "999", "34")), "character skill with no context");
        Check.True(M(item, C(N.CharacterLevelGE, "999")), "level GE with no context");
        Check.True(M(item, C(N.CharacterLevelLE, "0")), "level LE with no context");
        Check.True(M(item, C(N.CharacterMainPackEmptySlotsGE, "999")), "pack slots with no context");
        Check.True(M(item, C(N.CharacterBaseSkill, "34", "5", "6")), "base skill with no context");
    }

    private static int Spell(string name)
    {
        int id = SpellDatabase.GetIdByName(name);
        if (id == 0) throw new InvalidOperationException($"spell '{name}' is not in SpellData.txt");
        return id;
    }

    private static void SpellNodesWithContext()
    {
        FakeHost.Reset();
        var host = FakeHost.Create();
        var ctx = new VTankLootContext(host, Player);
        var wand = new Item("Wand", AcObjectClass.WandStaffOrb);
        uint wandId = unchecked((uint)wand.Wo.Id);
        FakeHost.SpellIds[wandId] = new[]
        {
            (uint)Spell("Aura of Blood Drinker Self VI"),
            (uint)Spell("Strength Other VI"),
            (uint)Spell("Strength Self VI"),
        };

        Check.True(MC(wand, ctx, C(N.SpellNameMatch, "blood drinker")), "name regex, any case");
        Check.False(MC(wand, ctx, C(N.SpellNameMatch, "^Heart Seeker")), "spell it does not have");
        Check.False(MC(wand, ctx, C(N.SpellNameMatch, "Blood (")), "bad regex: no match");
        Check.True(MC(wand, ctx, C(N.SpellCountGE, "3")), "three spells");
        Check.False(MC(wand, ctx, C(N.SpellCountGE, "4")), "not four");
        Check.True(MC(wand, ctx, C(N.SpellMatch, "Strength", "", "2")), "two Strength spells");
        Check.False(MC(wand, ctx, C(N.SpellMatch, "Strength", "Other", "2")), "only one once Other is excluded");
        Check.True(MC(wand, ctx, C(N.SpellMatch, "Strength", "Other", "1")), "one Strength Self");
        Check.True(MC(wand, ctx, C(N.SpellMatch, "Strength", "   ", "1")), "a blank exclude pattern excludes nothing");
        Check.False(MC(wand, ctx, C(N.SpellMatch, "Strength (", "", "1")), "bad include regex: no match");

        var plain = new Item("Stick");
        Check.False(MC(plain, ctx, C(N.SpellNameMatch, ".")), "an item with no spells");
        Check.True(MC(plain, ctx, C(N.SpellCountGE, "0")), "count >= 0 holds for no spells");
        Check.True(MC(plain, ctx, C(N.SpellMatch, ".", "", "0")) == false, "spell match with count 0 still needs one matching spell");
    }

    private static void SpellNodesNoSpellApi()
    {
        var ctx = new VTankLootContext(default, Player);   // null host: no spell list at all
        var wand = new Item("Wand");
        Check.False(MC(wand, ctx, C(N.SpellNameMatch, ".")), "a host that cannot list spells: name match fails");
        Check.False(MC(wand, ctx, C(N.SpellCountGE, "1")), "count reads 0");
        Check.True(MC(wand, ctx, C(N.SlotExactPalette, "0", "123")), "no palette data passes");
    }

    private static void CharacterNodes()
    {
        FakeHost.Reset();
        FakeHost.Skills[(Player, 34)] = (Buffed: 400, Training: 3);
        FakeHost.SkillLevels[(Player, 34, 1)] = 320;   // base (no enchantments)
        FakeHost.Ints[(Player, 25)] = 275;   // LEVEL
        var ctx = new VTankLootContext(FakeHost.Create(), Player);
        var item = new Item("x");

        Check.True(MC(item, ctx, C(N.CharacterSkillGE, "400", "34")), "skill 400 >= 400");
        Check.False(MC(item, ctx, C(N.CharacterSkillGE, "401", "34")), "skill 400 < 401");
        Check.False(MC(item, ctx, C(N.CharacterSkillGE, "1", "33")), "a skill the host has no value for reads 0");
        // "Base skill" is the unbuffed skill level (it was the training class, 2 or 3, until 2026-10-31,
        // so LootSnob's "Heavy Weapon 300" = base heavy in [300, 999] never matched).
        Check.True(MC(item, ctx, C(N.CharacterBaseSkill, "34", "300", "999")), "base 320 in [300,999]");
        Check.False(MC(item, ctx, C(N.CharacterBaseSkill, "34", "330", "999")), "base 320 not in [330,999] (buffed is 400)");
        Check.False(MC(item, ctx, C(N.CharacterBaseSkill, "34", "3", "3")), "not the training class any more");
        Check.True(MC(item, ctx, C(N.CharacterLevelGE, "275")), "level GE at the value");
        Check.False(MC(item, ctx, C(N.CharacterLevelGE, "276")), "level GE above");
        Check.True(MC(item, ctx, C(N.CharacterLevelLE, "275")), "level LE at the value");
        Check.False(MC(item, ctx, C(N.CharacterLevelLE, "274")), "level LE below");
        Check.True(MC(item, ctx, C(N.CharacterMainPackEmptySlotsGE, "102")), "no cache: all 102 main-pack slots count as empty");
        Check.False(MC(item, ctx, C(N.CharacterMainPackEmptySlotsGE, "103")), "never more than 102");

        var noPlayer = new VTankLootContext(FakeHost.Create(), 0);
        Check.False(MC(item, noPlayer, C(N.CharacterLevelGE, "1")), "no player id: level reads 0");
        Check.True(MC(item, noPlayer, C(N.CharacterLevelLE, "0")), "no player id: level 0 passes LE 0");
    }

    private static void SlotExactPalette()
    {
        FakeHost.Reset();
        var ctx = new VTankLootContext(FakeHost.Create(), Player);
        var robe = new Item("Robe");
        FakeHost.Palettes[unchecked((uint)robe.Wo.Id)] = new uint[] { 0x04001234, 0x04005678 };
        // The palette id in the rule is decimal; only the low 24 bits are compared.
        Check.True(MC(robe, ctx, C(N.SlotExactPalette, "1", "22136")), "slot 1 = 0x005678 = 22136");
        Check.True(MC(robe, ctx, C(N.SlotExactPalette, "0", "4660")), "slot 0 = 0x001234 = 4660");
        Check.True(MC(robe, ctx, C(N.SlotExactPalette, "0", "117445172")), "the high byte is ignored (0x07001234)");
        Check.False(MC(robe, ctx, C(N.SlotExactPalette, "0", "22136")), "wrong palette for the slot");
        Check.False(MC(robe, ctx, C(N.SlotExactPalette, "2", "4660")), "slot past the end");
        Check.False(MC(robe, ctx, C(N.SlotExactPalette, "-1", "4660")), "negative slot");
        var plain = new Item("Plain");
        Check.True(MC(plain, ctx, C(N.SlotExactPalette, "5", "1")), "an item with no palette data passes");
    }

    private static void MalformedData()
    {
        var item = new Item("x").Int(19, 10);
        Check.False(M(item, C(N.LongValKeyGE, "ten", "19")), "non-numeric value");
        Check.False(M(item, C(N.LongValKeyGE, "5")), "missing key line");
        Check.False(M(item, new VTankLootCondition(N.ObjectClass, "0", Array.Empty<string>())), "no data lines at all");
        Check.False(M(item, C(N.DoubleValKeyGE, "1.5.5", "29")), "garbled double");
        Check.False(M(item, C(N.LongValKeyGE, "99999999999", "19")), "value out of int range");
        Check.Throws<FormatException>(() => VTankLootEvaluator.MatchCondition(C(N.LongValKeyGE, "ten", "19"), item, null),
            "MatchCondition lets the parse error out (AutoVendor treats it as 'cannot tell')");
    }

    private static void UnknownNodeType()
    {
        var item = new Item("x");
        Check.False(M(item, C(4242, "a")), "unknown node type: the rule does not match");
        Check.Throws<InvalidOperationException>(() => VTankLootEvaluator.MatchCondition(C(4242, "a"), item, null),
            "MatchCondition reports an unsupported node type");
    }

    // A small hand-written v1 profile: Keep, Keep # 3, Salvage, Sell, Read.
    internal const string SampleUtl =
        "UTL\n1\n6\n" +
        "Disabled keep all\n\n0;1;9999\n0\ntrue\n" +
        "Keep valuables\n\n0;1;3\n0\n5000\n19\n" +
        "Keep 3 kits\n\n0;10;1\n3\n0\nHealing Kit$\n1\n" +
        "Salvage granite 7+\n\n0;2;7;12;3\n0\n39\n0\n67\n131\n0\n7\n105\n" +
        "Sell weapons\n\n0;3;7\n0\n1\n" +
        "Read books\n\n0;4;7\n0\n33\n";

    private static void ParsedProfile()
    {
        var p = VTankLootParser.LoadFromText(SampleUtl);
        Check.Eq(p.Rules.Count, 6, "six rules parsed");
        Check.Eq(p.Rules[2].Action, VTankLootAction.KeepUpTo, "rule 3 is Keep #");
        Check.Eq(p.Rules[2].KeepCount ?? -1, 3, "Keep # count");
        Check.False(p.Rules[0].Enabled, "rule 1 is disabled");

        (VTankLootAction? Action, int Index) Classify(WorldObject wo)
        {
            var hit = VTankLootEvaluator.FirstMatch(p, wo, null, out int i);
            return (hit?.Action, i);
        }

        Check.Eq(Classify(new Item("Pearl").Int(19, 6000)), ((VTankLootAction?)VTankLootAction.Keep, 1), "valuable -> Keep (the disabled catch-all is skipped)");
        Check.Eq(Classify(new Item("Peerless Healing Kit")), ((VTankLootAction?)VTankLootAction.KeepUpTo, 2), "kit -> Keep #");
        Check.Eq(Classify(new Item("Salvage", AcObjectClass.Salvage).Int(131, 67).Int(105, 8)), ((VTankLootAction?)VTankLootAction.Salvage, 3), "granite 8 -> Salvage");
        Check.Eq(Classify(new Item("Salvage", AcObjectClass.Salvage).Int(131, 67).Int(105, 6)), ((VTankLootAction?)null, -1), "granite 6 -> nothing");
        Check.Eq(Classify(new Item("Sword", AcObjectClass.MeleeWeapon).Int(19, 6000)), ((VTankLootAction?)VTankLootAction.Keep, 1), "a valuable sword is kept before the sell rule");
        Check.Eq(Classify(new Item("Sword", AcObjectClass.MeleeWeapon).Int(19, 100)), ((VTankLootAction?)VTankLootAction.Sell, 4), "a cheap sword -> Sell");
        Check.Eq(Classify(new Item("Tome", AcObjectClass.Book)), ((VTankLootAction?)VTankLootAction.Read, 5), "book -> Read");
        Check.Eq(Classify(new Item("Kit of healing")), ((VTankLootAction?)null, -1), "the $ anchor keeps a near-miss name out");
    }

    private static void FixtureFile()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "salvage-combine.utl");
        var p = VTankLootParser.Load(path);
        Check.Eq(p.FileVersion, 1, "v1 file");
        Check.Eq(p.Rules.Count, 2, "two rules");
        Check.NotNull(p.SalvageCombine, "SalvageCombine block read");
        if (p.SalvageCombine != null)
        {
            Check.True(p.SalvageCombine.Enabled, "combine enabled");
            Check.Eq(p.SalvageCombine.DefaultBands, "1-6,7-8,9,10", "default bands");
            Check.Eq(p.SalvageCombine.PerMaterial.Count, 1, "one per-material override");
            Check.Eq(p.SalvageCombine.PerMaterial.TryGetValue(67, out string? g) ? g : "", "1-10", "granite combines everything");
            Check.Eq(p.SalvageCombine.GetBandKey(64, 7), "7-8", "steel wk 7 -> default band 7-8");
            Check.Eq(p.SalvageCombine.GetBandKey(64, 10), "10-10", "a single-number band");
            Check.Eq(p.SalvageCombine.GetBandKey(67, 3), "1-10", "granite uses its own band");
            Check.Null(p.SalvageCombine.GetBandKey(64, 11), "outside every band: do not combine");
            Check.Null(p.SalvageCombine.GetBandKey(64, 0), "workmanship 0 is outside the bands");
        }
        Check.Eq(SalvageCombineSettings.ParseBands("a-b, 5, 3-, 7 - 8").Count, 2, "only '5' and '7 - 8' parse");
        Check.Eq(SalvageCombineSettings.ParseBands("").Count, 0, "blank bands");
        var reversed = new SalvageCombineSettings { DefaultBands = "8-7" };
        Check.Null(reversed.GetBandKey(1, 7), "a reversed band matches nothing");
        var hit = VTankLootEvaluator.FirstMatch(p, new Item("Salvage", AcObjectClass.Salvage).Int(131, 64).Int(105, 3), null, out int i);
        Check.Eq(hit?.Action, VTankLootAction.Salvage, "any salvage bag -> Salvage");
        Check.Eq(i, 1, "second rule");
        Check.Eq(VTankLootParser.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nope.utl")).Rules.Count, 0, "a missing file loads as an empty profile");
    }

    private static void MalformedUtl()
    {
        Check.Throws<InvalidOperationException>(() => VTankLootParser.LoadFromText(""), "empty text");
        Check.Throws<InvalidOperationException>(() => VTankLootParser.LoadFromText("UTL\n1"), "too short");
        Check.Throws<InvalidOperationException>(() => VTankLootParser.LoadFromText("UTL\nx\n1\n"), "bad version");
        Check.Throws<InvalidOperationException>(() => VTankLootParser.LoadFromText("UTL\n1\nmany\n"), "bad rule count");
        Check.Throws<InvalidOperationException>(() => VTankLootParser.LoadFromText("{\n\"Rules\": []\n}"), "a JSON profile is not a .utl");

        // Declares 3 rules but holds 1: the one that is there still works.
        var p = VTankLootParser.LoadFromText("UTL\n1\n3\nKeep gems\n\n0;1;7\n0\n11\n");
        Check.Eq(p.Rules.Count, 1, "truncated file: rules that exist are kept");
        Check.True(VTankLootEvaluator.Match(p.Rules[0], new Item("Opal", AcObjectClass.Gem)), "and they match");

        // A rule cut off in the middle of its data lines does not match anything.
        var cut = VTankLootParser.LoadFromText("UTL\n1\n1\nValue\n\n0;1;3\n0\n5000\n");
        Check.Eq(cut.Rules.Count, 1, "cut rule parsed");
        Check.False(VTankLootEvaluator.Match(cut.Rules[0], new Item("x").Int(19, 9000)), "missing key line: no match");

        // Legacy v0 (no header): rule count first, no custom expression or length-code lines.
        var v0 = VTankLootParser.LoadFromText("1\nKeep gems\n0;1;7\n11\n");
        Check.Eq(v0.FileVersion, 0, "v0 file");
        Check.True(v0.Rules.Count == 1 && VTankLootEvaluator.Match(v0.Rules[0], new Item("Opal", AcObjectClass.Gem)), "v0 rule works");

        // Unknown action number and unknown node type survive parsing; the rule just never matches.
        var odd = VTankLootParser.LoadFromText("UTL\n1\n2\nOdd\n\n0;7;4242\n0\nwhatever\nKeep gems\n\n0;1;7\n0\n11\n");
        Check.Eq(odd.Rules.Count, 2, "unknown node type slurped up to the next rule");
        Check.Eq((int)odd.Rules[0].Action, 7, "unknown action kept as a number");
        Check.False(VTankLootEvaluator.Match(odd.Rules[0], new Item("x")), "unknown node: no match");
        Check.True(ReferenceEquals(VTankLootEvaluator.FirstMatch(odd, new Item("Opal", AcObjectClass.Gem), null, out _), odd.Rules[1]), "next rule still reached");

        // CRLF line endings read the same as LF.
        var crlf = VTankLootParser.LoadFromText(SampleUtl.Replace("\n", "\r\n"));
        Check.Eq(crlf.Rules.Count, 6, "CRLF file");
        Check.Eq(crlf.Rules[2].KeepCount ?? -1, 3, "CRLF Keep # count");
    }

    // ── Keep # arithmetic (LootKeepCap, used by corpse looting) ──────────────

    private static readonly IReadOnlyDictionary<int, int> NoPending = new Dictionary<int, int>();

    private static bool IsKit(WorldObject wo) => wo.Name.Contains("Healing Kit", StringComparison.OrdinalIgnoreCase);

    private static void KeepCapCountsStacks()
    {
        var pack = new List<WorldObject>
        {
            new Item("Healing Kit").Int(12, 5),     // a stack of 5
            new Item("Healing Kit"),                // no stack count: counts 1
            new Item("Healing Kit").Int(12, 0),     // stack 0 still counts 1
            new Item("Mana Stone").Int(12, 50),     // not matched
            new Item(""),                           // nameless: skipped before matching
        };
        Check.Eq(LootKeepCap.CountHave(pack, IsKit, NoPending, _ => true), 7, "5 + 1 + 1");
        Check.Eq(LootKeepCap.CountHave(null, IsKit, NoPending, _ => true), 0, "no cache: nothing counted");
        Check.Eq(LootKeepCap.CountHave(new List<WorldObject>(), IsKit, NoPending, _ => true), 0, "empty pack");
        Check.Eq(LootKeepCap.CountHave(pack, _ => throw new InvalidOperationException(), NoPending, _ => true), 0, "a matcher that throws counts as no match");
        Check.Eq(LootKeepCap.StackOf(new Item("x").Int(12, 25)), 25, "stack of 25");
        Check.Eq(LootKeepCap.StackOf(new Item("x").Int(12, -3)), 1, "negative stack counts 1");
    }

    private static void KeepCapPending()
    {
        var inPack = new Item("Healing Kit", id: 0x70000001).Int(12, 2);
        var pack = new List<WorldObject> { inPack };
        var pending = new Dictionary<int, int>
        {
            [0x70000001] = 2,   // approved earlier and already in the pack: not counted twice
            [0x70000002] = 3,   // approved, still on its way
            [0x70000003] = 4,   // approved, then merged into a pack stack and deleted
        };
        bool Exists(int id) => id != 0x70000003;
        Check.Eq(LootKeepCap.CountHave(pack, IsKit, pending, Exists), 5, "2 in the pack + 3 on the way");
        Check.Eq(LootKeepCap.CountHave(null, IsKit, pending, _ => false), 0, "no cache: pending ones cannot be confirmed");
    }

    private static void KeepCapBoundary()
    {
        Check.True(LootKeepCap.UnderCap(4, 5), "4 of 5: loot");
        Check.False(LootKeepCap.UnderCap(5, 5), "5 of 5: leave");
        Check.False(LootKeepCap.UnderCap(9, 5), "over the cap: leave");
        Check.False(LootKeepCap.UnderCap(0, 0), "Keep # 0 never loots");
        Check.False(LootKeepCap.UnderCap(0, -1), "a negative count never loots");
        // A .utl Keep # rule with no count line reads KeepCount null, which corpse looting turns into 0.
        var p = VTankLootParser.LoadFromText("UTL\n1\n1\nKeep kits\n\n0;10;1\n");
        Check.Null(p.Rules[0].KeepCount, "no count line: null");
        Check.False(LootKeepCap.UnderCap(0, p.Rules[0].KeepCount ?? 0), "so it loots nothing");
        // Note the cap compares what the player has BEFORE the new item: with 4 of 5, a stack of 10 is still looted.
        Check.True(LootKeepCap.UnderCap(4, 5) && LootKeepCap.StackOf(new Item("Healing Kit").Int(12, 10)) == 10,
            "a whole stack is taken while under the cap, even if it overshoots");
    }

    private static void KeepCapOneUnit()
    {
        // A sellables rule: Keep # 25 of anything worth over 10,000 that weighs under 400. A stack's
        // value is the whole stack's, so a pack of pyreals and components used to read as "have
        // 25128/25" with none of the rule's items in it; each stack is now judged by one unit.
        var sellables = Rule("Sellables", VTankLootAction.KeepUpTo,
            C(N.LongValKeyGE, "10001", "19"), C(N.LongValKeyLE, "399", "5"));
        sellables.KeepCount = 25;
        bool Sells(WorldObject wo) => VTankLootEvaluator.Match(sellables, wo);

        var pyreals = new Item("Pyreal", AcObjectClass.Money).Int(12, 25000).Int(19, 25000).Int(5, 0);
        var tapers  = new Item("Prismatic Taper", AcObjectClass.SpellComponent).Int(12, 1500).Int(19, 15000).Int(5, 300);
        var notes   = new Item("Trade Note (250,000)", AcObjectClass.TradeNote).Int(12, 3).Int(19, 750000).Int(5, 6);
        var pack = new List<WorldObject>
        {
            pyreals,                                                                        // 1 each
            tapers,                                                                         // 10 each
            notes,                                                                          // 250,000 each
            new Item("Leather Gauntlets", AcObjectClass.Armor).Int(19, 12000).Int(5, 350),  // one, worth 12,000
            new Item("Leather Gauntlets", AcObjectClass.Armor).Int(19, 900).Int(5, 350),    // too cheap
            new Item("Leather Gauntlets", AcObjectClass.Armor).Int(19, 15000).Int(5, 500),  // too heavy
        };
        Check.True(Sells(pyreals) && Sells(tapers), "as whole stacks, the pyreals and tapers pass the rule");
        Check.Eq(LootKeepCap.CountHave(pack, Sells, NoPending, _ => true), 4, "3 notes + 1 pair of gauntlets; no pyreals, no tapers");
        Check.True(LootKeepCap.UnderCap(LootKeepCap.CountHave(pack, Sells, NoPending, _ => true), sellables.KeepCount ?? 0),
            "4 of 25: the rule loots again");

        // A rule by name still counts every unit of the stack.
        Check.Eq(LootKeepCap.CountHave(pack, wo => wo.Name == "Prismatic Taper", NoPending, _ => true), 1500, "Keep # tapers by name: 1500");
        Check.Eq(LootKeepCap.CountHave(pack, wo => wo.ObjectClass == AcObjectClass.Money, NoPending, _ => true), 25000, "by class: the whole stack");

        // A value floor a single unit still clears counts the whole stack.
        var cheap = Rule("Notes", VTankLootAction.KeepUpTo, C(N.ObjectClass, "15"), C(N.LongValKeyGE, "100000", "19"));
        Check.Eq(LootKeepCap.CountHave(pack, wo => VTankLootEvaluator.Match(cheap, wo), NoPending, _ => true), 3, "three 250,000 notes");

        // Approved items not in the pack yet are counted as before (they were judged on the corpse).
        var pending = new Dictionary<int, int> { [0x7000AAAA] = 2 };
        Check.Eq(LootKeepCap.CountHave(pack, Sells, pending, _ => true), 6, "4 in the pack + 2 on the way");
    }

    private static void KeepCapOneUnitView()
    {
        var single = new Item("Leather Gauntlets", AcObjectClass.Armor).Int(19, 12000).Int(5, 350);
        Check.True(ReferenceEquals(LootKeepCap.OneUnit(single), single.Wo), "a single item is itself");
        var unpriced = new Item("Prismatic Taper", AcObjectClass.SpellComponent).Int(12, 40);
        Check.True(ReferenceEquals(LootKeepCap.OneUnit(unpriced), unpriced.Wo), "a stack with no value or burden is itself");

        var stack = new Item("Prismatic Taper", AcObjectClass.SpellComponent, id: 0x7000BBBB)
            .Int(12, 1500).Int(19, 15000).Int(5, 1500).Int(105, 3).Str(16, "A taper.");
        var unit = LootKeepCap.OneUnit(stack);
        Check.False(ReferenceEquals(unit, stack.Wo), "a stack gets a view");
        Check.Eq(unit.Id, 0x7000BBBB, "same id");
        Check.Eq(unit.Name, "Prismatic Taper", "same name");
        Check.Eq((int)unit.ObjectClass, (int)AcObjectClass.SpellComponent, "same class");
        Check.Eq(unit.Values(19, 0), 10, "value of one");
        Check.Eq(unit.Values(5, 0), 1, "burden of one");
        Check.Eq(unit.Values(LongValueKey.StackCount, 0), 1500, "stack size kept");
        Check.Eq(unit.Values(105, 0), 3, "other keys kept");
        Check.Eq(unit.Values((StringValueKey)16, ""), "A taper.", "strings kept");
        Check.Eq(unit.Values(131, -7), -7, "a key the item doesn't have still reads the default");
        Check.Eq(stack.Wo.Values(19, 0), 15000, "the pack item itself is unchanged");

        var valueOnly = LootKeepCap.OneUnit(new Item("Pyreal", AcObjectClass.Money).Int(12, 100).Int(19, 100));
        Check.Eq(valueOnly.Values(19, 0), 1, "value divided");
        Check.Eq(valueOnly.Values(5, -1), -1, "no burden stays absent");
        var rounding = LootKeepCap.OneUnit(new Item("x").Int(12, 3).Int(19, 10));
        Check.Eq(rounding.Values(19, 0), 3, "rounds down, as UB's Value / Stack");
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Loot;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests.Tests;

// Native (.json) loot profiles: LootEvaluator.Classify / Matches / Evaluate against items
// whose properties come from a fixed overlay. Classify is what corpse looting calls first
// when the selected profile is a .json file.
internal static class NativeLootTests
{
    private const int MaxDamage = 44, Value = 19, ArmorLevel = 28, Workmanship = 105, Material = 131;
    // ACE PropertyFloat ids (WeaponOffense 62 / DamageMod 63; 61 is EconRecoveryInterval).
    private const int DamageVariance = 22, WeaponOffense = 62, DamageMod = 63, WeaponDefense = 29;

    public static void Register(Runner r)
    {
        r.Add("loot native: empty profile leaves everything (Sell, no rule)", EmptyProfile);
        r.Add("loot native: first matching rule wins, in list order", FirstMatchWins);
        r.Add("loot native: a disabled rule never matches", DisabledRules);
        r.Add("loot native: a rule with no conditions matches everything", NoConditions);
        r.Add("loot native: every condition of a rule must hold", AllConditionsMustHold);
        r.Add("loot native: object class", ObjectClass);
        r.Add("loot native: long key GE/LE/E/NE at the boundary (value)", LongKeyBoundaries);
        r.Add("loot native: a missing long key reads as 0", MissingLongKey);
        r.Add("loot native: long key flag", LongKeyFlag);
        r.Add("loot native: double key GE/LE", DoubleKeys);
        r.Add("loot native: string regex, case-insensitive, falls back to the name", StringValue);
        r.Add("loot native: min damage = max - variance * max", MinDamage);
        r.Add("loot native: damage percent reads DamageMod * 100", DamagePercent);
        r.Add("loot native: DoubleValueKey ids are ACE's PropertyFloat (DamageMod 63, not WeaponOffense 62)", DoubleValueKeyIds);
        r.Add("loot native: total ratings sum keys 370-376 and 379 only", TotalRatings);
        r.Add("loot native: character skill (null, stub and live skills)", CharacterSkill);
        r.Add("loot native: unknown condition type passes", UnknownConditionPasses);
        r.Add("loot native: Keep # rule reports KeepUpTo with its count", KeepUpToAction);
        r.Add("loot native: salvage rule by material and workmanship", SalvageRule);
        r.Add("loot native: profile JSON round trip keeps rules and order", JsonRoundTrip);
        r.Add("loot native: malformed or missing JSON profile", MalformedJson);
        r.Add("loot native: an invalid regex in a String rule does not match", InvalidRegexDoesNotThrow);   // was a known failure; fixed by a2902c3
        r.Add("loot native: Keep # with a count of 0 survives a save and load", KeepCountZeroRoundTrip);   // was a known failure
        r.Add("loot native: salvage combine turned off survives a save and load", SalvageCombineOffRoundTrip);
    }

    private static LootRule Rule(string name, LootAction action, params LootCondition[] conds)
    {
        var r = new LootRule { Name = name, Action = action };
        r.Conditions.AddRange(conds);
        return r;
    }

    private static LootProfile Profile(params LootRule[] rules)
    {
        var p = new LootProfile { Name = "test" };
        p.Rules.AddRange(rules);
        return p;
    }

    private static bool Eval(LootCondition c, WorldObject item) => LootEvaluator.Evaluate(c, item, null);

    private static void EmptyProfile()
    {
        var (action, rule) = LootEvaluator.Classify(new LootProfile(), new Item("Pyreal"), null);
        Check.Null(rule, "no rule matched");
        Check.Eq(action, LootAction.Sell, "action reported when nothing matched (callers look at the null rule)");
    }

    private static void FirstMatchWins()
    {
        var gems = new ObjectClassCondition { ObjectClass = AcObjectClass.Gem };
        var keep = Rule("Keep gems", LootAction.Keep, gems);
        var salvage = Rule("Salvage gems", LootAction.Salvage, new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        var gem = new Item("Black Opal", AcObjectClass.Gem);

        var (a1, r1) = LootEvaluator.Classify(Profile(keep, salvage), gem, null);
        Check.Eq(a1, LootAction.Keep, "Keep listed first");
        Check.True(ReferenceEquals(r1, keep), "the first rule object is returned");

        var (a2, r2) = LootEvaluator.Classify(Profile(salvage, keep), gem, null);
        Check.Eq(a2, LootAction.Salvage, "Salvage listed first");
        Check.True(ReferenceEquals(r2, salvage), "order decides, not action");

        // A non-matching rule in front does not block a later match.
        var armorOnly = Rule("Armor", LootAction.Keep, new ObjectClassCondition { ObjectClass = AcObjectClass.Armor });
        var (a3, r3) = LootEvaluator.Classify(Profile(armorOnly, salvage), gem, null);
        Check.Eq(a3, LootAction.Salvage, "skips the rule that does not match");
        Check.True(ReferenceEquals(r3, salvage), "second rule returned");
    }

    private static void DisabledRules()
    {
        var off = Rule("off", LootAction.Keep);
        off.Enabled = false;
        var sell = Rule("sell", LootAction.Sell);
        var item = new Item("Anything");

        Check.False(LootEvaluator.Matches(off, item, null), "disabled rule with no conditions still does not match");
        var (a, r) = LootEvaluator.Classify(Profile(off, sell), item, null);
        Check.True(ReferenceEquals(r, sell), "falls through the disabled rule");
        Check.Eq(a, LootAction.Sell, "second rule's action");

        var (_, none) = LootEvaluator.Classify(Profile(off), item, null);
        Check.Null(none, "only disabled rules: nothing matches");
    }

    private static void NoConditions()
    {
        Check.True(LootEvaluator.Matches(Rule("all", LootAction.Keep), new Item("x"), null), "empty condition list matches");
        Check.True(LootEvaluator.Matches(Rule("all", LootAction.Keep), new Item(""), null), "even an item with no name");
    }

    private static void AllConditionsMustHold()
    {
        var rule = Rule("good armor", LootAction.Keep,
            new ObjectClassCondition { ObjectClass = AcObjectClass.Armor },
            new LongValKeyGECondition { Key = ArmorLevel, Value = 200 });
        Check.True(LootEvaluator.Matches(rule, new Item("Plate", AcObjectClass.Armor).Int(ArmorLevel, 250), null), "both hold");
        Check.False(LootEvaluator.Matches(rule, new Item("Plate", AcObjectClass.Armor).Int(ArmorLevel, 150), null), "AL too low");
        Check.False(LootEvaluator.Matches(rule, new Item("Robe", AcObjectClass.Clothing).Int(ArmorLevel, 250), null), "wrong class");
    }

    private static void ObjectClass()
    {
        var c = new ObjectClassCondition { ObjectClass = AcObjectClass.Salvage };
        Check.True(Eval(c, new Item("Salvaged Granite", AcObjectClass.Salvage)), "same class");
        Check.False(Eval(c, new Item("Granite", AcObjectClass.Gem)), "other class");
        Check.False(Eval(c, new Item("?", AcObjectClass.Unknown)), "an unclassified item never matches a class rule");
    }

    private static void LongKeyBoundaries()
    {
        var item = new Item("Coin Purse").Int(Value, 1000);
        Check.True(Eval(new LongValKeyGECondition { Key = Value, Value = 1000 }, item), "GE at the value");
        Check.False(Eval(new LongValKeyGECondition { Key = Value, Value = 1001 }, item), "GE one above");
        Check.True(Eval(new LongValKeyLECondition { Key = Value, Value = 1000 }, item), "LE at the value");
        Check.False(Eval(new LongValKeyLECondition { Key = Value, Value = 999 }, item), "LE one below");
        Check.True(Eval(new LongValKeyECondition { Key = Value, Value = 1000 }, item), "E equal");
        Check.False(Eval(new LongValKeyECondition { Key = Value, Value = 999 }, item), "E not equal");
        Check.True(Eval(new LongValKeyNECondition { Key = Value, Value = 999 }, item), "NE different");
        Check.False(Eval(new LongValKeyNECondition { Key = Value, Value = 1000 }, item), "NE same");

        var big = new Item("Big").Int(Value, int.MaxValue);
        Check.True(Eval(new LongValKeyGECondition { Key = Value, Value = int.MaxValue }, big), "GE at int.MaxValue");
        var neg = new Item("Neg").Int(Value, -5);
        Check.True(Eval(new LongValKeyLECondition { Key = Value, Value = -5 }, neg), "negative values compare as signed");
    }

    private static void MissingLongKey()
    {
        var bare = new Item("Unappraised");
        Check.True(Eval(new LongValKeyGECondition { Key = Workmanship, Value = 0 }, bare), "GE 0 passes on a missing key");
        Check.False(Eval(new LongValKeyGECondition { Key = Workmanship, Value = 1 }, bare), "GE 1 fails on a missing key");
        Check.True(Eval(new LongValKeyLECondition { Key = Workmanship, Value = 5 }, bare), "LE passes on a missing key (reads 0)");
        Check.True(Eval(new LongValKeyNECondition { Key = Workmanship, Value = 7 }, bare), "NE passes on a missing key");
        Check.True(Eval(new LongValKeyECondition { Key = Workmanship, Value = 0 }, bare), "E 0 passes on a missing key");
    }

    private static void LongKeyFlag()
    {
        var item = new Item("Ring").Int(9, 0x000C0000);   // LOCATIONS: both finger slots
        Check.True(Eval(new LongValKeyFlagCondition { Key = 9, FlagValue = 0x00040000 }, item), "one bit set");
        Check.True(Eval(new LongValKeyFlagCondition { Key = 9, FlagValue = 0x000F0000 }, item), "any overlapping bit");
        Check.False(Eval(new LongValKeyFlagCondition { Key = 9, FlagValue = 0x00000001 }, item), "bit not set");
        Check.False(Eval(new LongValKeyFlagCondition { Key = 9, FlagValue = 0 }, item), "flag 0 never matches");
    }

    private static void DoubleKeys()
    {
        var item = new Item("Sword").Dbl(WeaponDefense, 1.2);
        Check.True(Eval(new DoubleValKeyGECondition { Key = WeaponDefense, Value = 1.2 }, item), "GE equal");
        Check.False(Eval(new DoubleValKeyGECondition { Key = WeaponDefense, Value = 1.2000001 }, item), "GE just above");
        Check.True(Eval(new DoubleValKeyLECondition { Key = WeaponDefense, Value = 1.2 }, item), "LE equal");
        Check.False(Eval(new DoubleValKeyLECondition { Key = WeaponDefense, Value = 1.1999999 }, item), "LE just below");
        Check.True(Eval(new DoubleValKeyLECondition { Key = 999, Value = 0.0 }, item), "missing double reads 0.0");
    }

    private static void StringValue()
    {
        var kit = new Item("Peerless Healing Kit", AcObjectClass.HealingKit);
        Check.True(Eval(new StringValueCondition { Key = 1, Pattern = "healing kit" }, kit), "Name rule falls back to the object name, any case");
        Check.True(Eval(new StringValueCondition { Key = 1, Pattern = "^Peerless" }, kit), "anchored");
        Check.False(Eval(new StringValueCondition { Key = 1, Pattern = "^Healing" }, kit), "anchor that does not fit");
        Check.True(Eval(new StringValueCondition { Key = 1, Pattern = "" }, kit), "empty pattern matches anything");

        var described = new Item("Scroll").Str(16, "Inscribed by Asheron");
        Check.True(Eval(new StringValueCondition { Key = 16, Pattern = "asheron" }, described), "other string keys are read as given");
        Check.False(Eval(new StringValueCondition { Key = 15, Pattern = "." }, described), "a missing non-name string reads empty");
        Check.True(Eval(new StringValueCondition { Key = 15, Pattern = "^$" }, described), "empty string matches ^$");

        var renamed = new Item("Object Name").Str(1, "Appraised Name");
        Check.True(Eval(new StringValueCondition { Key = 1, Pattern = "^Appraised" }, renamed), "an appraised name wins over the object name");
    }

    private static void MinDamage()
    {
        var sword = new Item("Sword", AcObjectClass.MeleeWeapon).Int(MaxDamage, 20).Dbl(DamageVariance, 0.25);
        Check.True(Eval(new MinDamageGECondition { Value = 15 }, sword), "20 - 0.25*20 = 15");
        Check.False(Eval(new MinDamageGECondition { Value = 15.01 }, sword), "just above 15");
        var noVariance = new Item("Club").Int(MaxDamage, 20);
        Check.True(Eval(new MinDamageGECondition { Value = 20 }, noVariance), "no variance: min = max");
        var bare = new Item("Stick");
        Check.True(Eval(new MinDamageGECondition { Value = 0 }, bare), "no damage data: min damage 0 passes a 0 threshold");
        Check.False(Eval(new MinDamageGECondition { Value = 1 }, bare), "no damage data fails a real threshold");
    }

    private static void DamagePercent()
    {
        var bow = new Item("Bow").Dbl(DamageMod, 1.5);
        Check.True(Eval(new DamagePercentGECondition { Value = 150 }, bow), "1.5 -> 150%");
        Check.False(Eval(new DamagePercentGECondition { Value = 151 }, bow), "above");

        // A launcher with a big attack mod and a small damage mod: the rule must read the damage
        // mod (63). With DamageMod = 62 in the enum (before 2026-10-05) it read the attack mod.
        var launcher = new Item("Bow").Dbl(WeaponOffense, 1.5).Dbl(DamageMod, 1.1).Dbl(61, 2.0);
        Check.True(Eval(new DamagePercentGECondition { Value = 110 }, launcher), "damage mod 1.1 -> 110%");
        Check.False(Eval(new DamagePercentGECondition { Value = 111 }, launcher), "the 1.5 attack mod is not read");
        Check.False(Eval(new DamagePercentGECondition { Value = 150 }, new Item("Bow").Dbl(WeaponOffense, 1.5)),
            "attack mod alone: no damage percent");
    }

    private static void DoubleValueKeyIds()
    {
        Check.Eq((int)DoubleValueKey.DamageVariance, 22, "DamageVariance");
        Check.Eq((int)DoubleValueKey.MaximumVelocity, 26, "MaximumVelocity");
        Check.Eq((int)DoubleValueKey.WeaponDefense, 29, "WeaponDefense");
        Check.Eq((int)DoubleValueKey.UseRadius, 54, "UseRadius");
        Check.Eq((int)DoubleValueKey.WeaponOffense, 62, "WeaponOffense");
        Check.Eq((int)DoubleValueKey.DamageMod, 63, "DamageMod");
        Check.Eq((int)DoubleValueKey.PowerLevel, 92, "PowerLevel");
        Check.Eq((int)DoubleValueKey.AccuracyLevel, 93, "AccuracyLevel");
        Check.Eq((int)AcFloatProperty.AttackBonus, 62, "LootSdk AttackBonus = WeaponOffense");
        Check.Eq((int)AcFloatProperty.DamageMod, 63, "LootSdk DamageMod");
        Check.Eq((int)AcFloatProperty.ManaConvMod, 144, "LootSdk ManaConvMod");
        Check.Eq((int)AcFloatProperty.SlayerDamageBonus, 138, "LootSdk SlayerDamageBonus");

        // The double-key condition reads the id it is given: 63 is the damage mod.
        var bow = new Item("Bow").Dbl(WeaponOffense, 1.25).Dbl(DamageMod, 1.05);
        Check.True(Eval(new DoubleValKeyGECondition { Key = 63, Value = 1.05 }, bow), "key 63 reads DamageMod");
        Check.False(Eval(new DoubleValKeyGECondition { Key = 63, Value = 1.06 }, bow), "not WeaponOffense");
    }

    private static void TotalRatings()
    {
        var gear = new Item("Gear");
        foreach (int k in new[] { 370, 371, 372, 373, 374, 375, 376, 379 }) gear.Int(k, 1);
        gear.Int(377, 50).Int(378, 50);   // not ratings the total uses
        Check.True(Eval(new TotalRatingsGECondition { Value = 8 }, gear), "eight rating keys summed");
        Check.False(Eval(new TotalRatingsGECondition { Value = 9 }, gear), "377 and 378 are not counted");
    }

    private static void CharacterSkill()
    {
        var c = new CharacterSkillGECondition { Skill = AcSkillType.WarMagic, Value = 300 };
        var item = new Item("Wand");
        Check.True(LootEvaluator.Evaluate(c, item, null), "no skills object: passes");

        var stub = new CharacterSkills(default);   // no player: every skill reads the 250 stub
        Check.False(LootEvaluator.Evaluate(c, item, stub), "stub 250 < 300");
        Check.True(LootEvaluator.Evaluate(new CharacterSkillGECondition { Skill = AcSkillType.WarMagic, Value = 250 }, item, stub), "stub 250 >= 250");

        FakeHost.Reset();
        const uint player = 0x50000A01;
        FakeHost.Skills[(player, 34)] = (Buffed: 410, Training: 3);   // war magic is STypeSkill 34
        var live = new CharacterSkills(FakeHost.Create());
        live.SetPlayerId(player);
        Check.True(LootEvaluator.Evaluate(c, item, live), "live 410 >= 300");
        Check.False(LootEvaluator.Evaluate(new CharacterSkillGECondition { Skill = AcSkillType.WarMagic, Value = 411 }, item, live), "live 410 < 411");
    }

    private sealed class FutureCondition : LootCondition { }

    private static void UnknownConditionPasses()
    {
        Check.True(Eval(new FutureCondition(), new Item("x")), "unknown type evaluates to true");
        var rule = Rule("r", LootAction.Keep, new FutureCondition(), new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        Check.False(LootEvaluator.Matches(rule, new Item("x", AcObjectClass.Armor), null), "the other conditions still decide");
    }

    private static void KeepUpToAction()
    {
        var rule = Rule("Keep 5 kits", LootAction.KeepUpTo, new StringValueCondition { Key = 1, Pattern = "Healing Kit" });
        rule.KeepCount = 5;
        var (a, r) = LootEvaluator.Classify(Profile(rule), new Item("Treated Healing Kit"), null);
        Check.Eq(a, LootAction.KeepUpTo, "Keep # action");
        Check.Eq(r?.KeepCount ?? -1, 5, "count travels with the rule");
    }

    private static void SalvageRule()
    {
        // Salvage granite of workmanship 6 or better; everything else falls through to Sell.
        var granite = Rule("Salvage granite 6+", LootAction.Salvage,
            new LongValKeyECondition { Key = Material, Value = 67 },
            new LongValKeyGECondition { Key = Workmanship, Value = 6 });
        var sellRest = Rule("Sell the rest", LootAction.Sell);
        var p = Profile(granite, sellRest);

        Check.Eq(LootEvaluator.Classify(p, new Item("Granite Axe").Int(Material, 67).Int(Workmanship, 6), null).action, LootAction.Salvage, "wk 6 granite");
        Check.Eq(LootEvaluator.Classify(p, new Item("Granite Axe").Int(Material, 67).Int(Workmanship, 10), null).action, LootAction.Salvage, "wk 10 granite");
        Check.Eq(LootEvaluator.Classify(p, new Item("Granite Axe").Int(Material, 67).Int(Workmanship, 5), null).action, LootAction.Sell, "wk 5 falls through");
        Check.Eq(LootEvaluator.Classify(p, new Item("Iron Axe").Int(Material, 61).Int(Workmanship, 9), null).action, LootAction.Sell, "other material falls through");
    }

    private static string TempFile(string name) => Path.Combine(Program.TempRoot, name);

    private static void JsonRoundTrip()
    {
        var p = Profile(
            Rule("A", LootAction.Keep, new LongValKeyGECondition { Key = Value, Value = 5000 }),
            Rule("B", LootAction.Salvage, new StringValueCondition { Key = 1, Pattern = "^Salvaged" }),
            Rule("C", LootAction.KeepUpTo, new ObjectClassCondition { ObjectClass = AcObjectClass.HealingKit }));
        p.Rules[1].Enabled = false;
        p.Rules[2].KeepCount = 7;
        string path = TempFile("roundtrip.json");
        p.Save(path);
        var back = LootProfile.Load(path);

        Check.Eq(back.Rules.Count, 3, "rule count");
        Check.Eq(back.Rules[0].Name + back.Rules[1].Name + back.Rules[2].Name, "ABC", "order kept");
        Check.Eq(back.Rules[1].Enabled, false, "disabled flag kept");
        Check.Eq(back.Rules[2].Action, LootAction.KeepUpTo, "Keep # action kept");
        Check.Eq(back.Rules[2].KeepCount, 7, "Keep # count kept");
        Check.True(back.Rules[0].Conditions[0] is LongValKeyGECondition { Key: Value, Value: 5000 }, "condition type and values kept");
        Check.Eq(LootEvaluator.Classify(back, new Item("Salvaged Iron"), null).action, LootAction.Sell,
            "the reloaded profile classifies the same (B is disabled, A needs value)");
    }

    private static void MalformedJson()
    {
        Check.Null(LootProfile.TryLoad(TempFile("does-not-exist.json")), "missing file");
        string bad = TempFile("bad.json");
        File.WriteAllText(bad, "{ \"Name\": \"x\", \"Rules\": [ { \"Name\": ");
        Check.Null(LootProfile.TryLoad(bad), "truncated JSON");
        Check.Throws<System.Text.Json.JsonException>(() => LootProfile.Load(bad), "Load (what corpse looting calls) throws on truncated JSON");

        string empty = TempFile("empty.json");
        File.WriteAllText(empty, "{}");
        var p = LootProfile.Load(empty);
        Check.Eq(p.Rules.Count, 0, "{} loads as an empty profile");
        Check.Null(LootEvaluator.Classify(p, new Item("x"), null).rule, "which loots nothing");

        string unknownType = TempFile("unknown-cond.json");
        File.WriteAllText(unknownType, "{\"Rules\":[{\"Name\":\"r\",\"Conditions\":[{\"$t\":\"NoSuchCondition\"}]}]}");
        Check.Null(LootProfile.TryLoad(unknownType), "an unknown condition discriminator fails the whole load");
    }

    private static void InvalidRegexDoesNotThrow()
    {
        var rule = Rule("bad", LootAction.Keep, new StringValueCondition { Key = 1, Pattern = "Healing Kit (" });
        bool threw = false, matched = true;
        try { matched = LootEvaluator.Matches(rule, new Item("Healing Kit"), null); }
        catch (ArgumentException) { threw = true; }
        Check.False(threw, "Matches threw ArgumentException on the pattern 'Healing Kit ('");
        Check.False(matched, "a bad pattern should simply not match");
    }

    private static void KeepCountZeroRoundTrip()
    {
        var rule = Rule("none of these", LootAction.KeepUpTo);
        rule.KeepCount = 0;
        string path = TempFile("keep0.json");
        Profile(rule).Save(path);
        Check.Eq(LootProfile.Load(path).Rules[0].KeepCount, 0, "KeepCount after reload");
    }

    /// <summary>LootJsonContext skips default values: SalvageCombine.Enabled = false was left out and loaded back as true.</summary>
    private static void SalvageCombineOffRoundTrip()
    {
        var p = Profile(Rule("any", LootAction.Salvage));
        p.SalvageCombine = new SalvageCombineSettings { Enabled = false };
        string path = TempFile("combine-off.json");
        p.Save(path);
        var loaded = LootProfile.Load(path);
        Check.NotNull(loaded.SalvageCombine, "SalvageCombine after reload");
        Check.False(loaded.SalvageCombine?.Enabled ?? true, "SalvageCombine.Enabled stays off");
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// Named values for long keys (LootRuleText.ValueTableFor): the tables, the
// lookup, the vocabulary's LongValueTables, and that a value picked by name is
// saved as the same number (the .utl format is unchanged).
internal static partial class Program
{
    private static void TestEditValueNames()
    {
        Console.WriteLine("\n-- LootEdit: named values for long keys --");

        // Tables: the keys that have one, and the ones that don't.
        Check(ReferenceEquals(LootRuleText.ValueTableFor(159), LootRuleText.Skills), "159 WieldSkilltype -> skills");
        Check(ReferenceEquals(LootRuleText.ValueTableFor(48), LootRuleText.Skills), "48 WeaponSkill -> skills");
        Check(ReferenceEquals(LootRuleText.ValueTableFor(158), LootRuleText.WieldRequirements), "158 -> wield requirements");
        Check(ReferenceEquals(LootRuleText.ValueTableFor(131), LootRuleText.Materials), "131 -> materials");
        Check(ReferenceEquals(LootRuleText.ValueTableFor(45), LootRuleText.DamageTypes), "45 -> damage types");
        Check(LootRuleText.ValueTableFor(160) == null, "160 WieldDifficulty is a plain number");
        Check(LootRuleText.ValueTableFor(19) == null, "19 Value is a plain number");
        Check(LootRuleText.ValueTableIsFlags(45) && !LootRuleText.ValueTableIsFlags(159), "only DamageType is flags");
        foreach (int key in LootRuleText.KeysWithValueTables)
        {
            Check(LootRuleText.ValueTableFor(key) != null, $"key {key} listed with a table");
            Check(LootRuleText.LongKeys.Any(k => k.Id == key), $"key {key} is in LongKeys (its picker can choose it)");
        }

        // Every table: unique ids, ASCII names (the in-game font), no empty names.
        foreach (var (label, table) in new[]
                 {
                     ("skills", LootRuleText.Skills), ("wield requirements", LootRuleText.WieldRequirements),
                     ("materials", LootRuleText.Materials), ("damage types", LootRuleText.DamageTypes),
                     ("creature types", LootRuleText.CreatureTypes),
                 })
        {
            Eq(table.Select(t => t.Id).Distinct().Count(), table.Length, $"{label}: ids unique");
            Check(table.All(t => t.Name.Length > 0 && t.Name.All(ch => ch >= 32 && ch < 128)), $"{label}: names ASCII");
            Eq(table.Select(t => t.Name).Distinct().Count(), table.Length, $"{label}: names unique");
        }
        Check(ReferenceEquals(LootRuleText.ValueTableFor(166), LootRuleText.CreatureTypes), "166 Slayer Type -> creature types");
        Eq(LootRuleText.LongKeys.First(k => k.Id == 166).Name, "Slayer Type", "166 player-facing name");
        Eq(LootRuleText.ValueName(166, "1"), "Olthoi", "ValueName 166/1 Olthoi");
        Check(LootRuleText.CreatureTypes.Select(t => t.Name)
                .SequenceEqual(LootRuleText.CreatureTypes.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
            "creature types listed alphabetically");

        // Retail / ACE numbering (the old table had Light Weapons at 47, which is Missile Weapons).
        string Skill(int id) => LootRuleText.Skills.First(s => s.Id == id).Name;
        Eq(Skill(45), "Light Weapons", "skill 45");
        Eq(Skill(44), "Heavy Weapons", "skill 44");
        Eq(Skill(46), "Finesse Weapons", "skill 46");
        Eq(Skill(47), "Missile Weapons", "skill 47");
        Eq(Skill(41), "Two Handed", "skill 41");
        Eq(Skill(34), "War Magic", "skill 34");
        Eq(Skill(43), "Void Magic", "skill 43");
        Eq(Skill(33), "Life Magic", "skill 33");
        Eq(Skill(49), "Dual Wield", "skill 49");
        Eq(Skill(48), "Shield", "skill 48");
        Eq(Skill(54), "Summoning", "skill 54");
        Eq(LootRuleText.Skills.Length, 54, "skills 1..54");
        Check(LootRuleText.Skills.Select(s => s.Name).SequenceEqual(LootRuleText.Skills.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal)),
            "skills listed alphabetically");
        Eq(LootRuleText.Materials.First(m => m.Id == 64).Name, "Steel", "material 64");
        Eq(LootRuleText.Materials.First(m => m.Id == 51).Name, "Ivory", "material 51");
        Eq(LootRuleText.Materials.First(m => m.Id == 15).Name, "Black Garnet", "material 15");
        Eq(LootRuleText.Materials.Length, 78, "materials 0..77");
        Eq(LootRuleText.WieldRequirements.First(w => w.Id == 2).Name, "Base Skill", "wield requirement 2 (RawSkill)");
        Eq(LootRuleText.WieldRequirements.Length, 14, "wield requirements 0..13 (13 = ACECustom Int64 Property)");
        Eq(LootRuleText.DamageTypes.First(d => d.Id == 64).Name, "Electric", "damage type 64");

        // Lookup and labels; unknown and odd values stay expressible.
        Eq(LootRuleText.ValueName(159, "45"), "Light Weapons", "ValueName 159/45");
        Eq(LootRuleText.ValueName(159, " 45 "), "Light Weapons", "ValueName trims");
        Eq(LootRuleText.ValueName(159, "45.0"), "Light Weapons", "ValueName reads a whole double");
        Eq(LootRuleText.ValueName(159, "999"), null, "ValueName unknown value");
        Eq(LootRuleText.ValueName(160, "45"), null, "ValueName key without a table");
        Eq(LootRuleText.ValueName(159, "abc"), null, "ValueName not a number");
        Eq(LootRuleText.ValueLabel(159, "45"), "Light Weapons (45)", "ValueLabel named");
        Eq(LootRuleText.ValueLabel(131, "999"), "value 999", "ValueLabel unknown");
        Eq(LootRuleText.ValueLabel(45, "7"), "value 7", "ValueLabel flag combination not named");
        Eq(LootRuleText.ValueLabel(160, "300"), "value 300", "ValueLabel plain key");
        Check(LootRuleText.TryParseValue("-3", out int neg) && neg == -3, "TryParseValue negative");
        Check(!LootRuleText.TryParseValue("4.5", out _), "TryParseValue refuses a fraction");

        // Which conditions pick values by name.
        Check(LootRuleText.UsesValueNames(VTankNodeTypes.LongValKeyE), "= uses names");
        Check(LootRuleText.UsesValueNames(VTankNodeTypes.LongValKeyGE), ">= uses names");
        Check(LootRuleText.UsesValueNames(VTankNodeTypes.BuffedLongValKeyGE), "buffed >= uses names");
        Check(!LootRuleText.UsesValueNames(VTankNodeTypes.LongValKeyFlagExists), "has flag does not");
        Check(!LootRuleText.UsesValueNames(VTankNodeTypes.DoubleValKeyGE), "double keys do not");

        // Vocabulary: the tables reach the face, and survive JSON.
        LootEditVocab v = LootRuleText.BuildVocab();
        Eq(v.LongValueTables.Count, LootRuleText.KeysWithValueTables.Length, "vocab has every value table");
        LootEditVocab? back = JsonSerializer.Deserialize(JsonSerializer.Serialize(v, LootEditJsonContext.Default.LootEditVocab),
            LootEditJsonContext.Default.LootEditVocab);
        LootEditValueTable? wst = back?.LongValueTables.FirstOrDefault(t => t.Key == 159);
        Check(wst != null && wst.Values.Any(n => n.Id == 45 && n.Name == "Light Weapons"), "vocab JSON keeps 159's names");
        Check(back?.LongValueTables.First(t => t.Key == 45).Flags == true, "vocab JSON keeps the flags mark");
        Check(back?.NodeTypes.First(n => n.Id == VTankNodeTypes.LongValKeyE).NamedValues == true, "vocab marks = as named");
        Check(back?.NodeTypes.First(n => n.Id == VTankNodeTypes.LongValKeyFlagExists).NamedValues == false, "vocab leaves has flag unnamed");
        // An older RynthAi's vocabulary (no LongValueTables) still reads: the face falls back to the text box.
        LootEditVocab? old = JsonSerializer.Deserialize("{\"LongKeys\":[{\"Id\":159,\"Name\":\"WieldSkilltype\"}]}",
            LootEditJsonContext.Default.LootEditVocab);
        Check(old != null && old.LongValueTables.Count == 0, "old vocab JSON: no value tables, no error");

        // Summaries name the value.
        var cond = new VTankLootCondition(VTankNodeTypes.LongValKeyE, "0", new[] { "45", "159" });
        Eq(LootRuleText.Condition(cond), "WieldSkilltype = Light Weapons", "summary names the skill");
        Eq(LootRuleText.Condition(new VTankLootCondition(VTankNodeTypes.LongValKeyGE, "0", new[] { "300", "160" })),
            "WieldDifficulty >= 300", "summary of a plain key unchanged");
        Eq(LootRuleText.Condition(new VTankLootCondition(VTankNodeTypes.LongValKeyE, "0", new[] { "999", "131" })),
            "MaterialType = 999", "summary of an unknown value is the number");

        // Round trip: values picked by name through the wire, saved, reopened: the same numbers.
        string Pick((int Id, string Name)[] table, string name) =>
            table.First(t => t.Name == name).Id.ToString(CultureInfo.InvariantCulture);
        LootEditSession s = OpenOk(TempFile("values.utl", Encoding.UTF8.GetBytes(EditFixture)));
        Eq(Cmd(s, "add", -1), LootEditSession.Outcome.Changed, "add a rule for the values");
        int idx = s.RuleCount - 1;
        LootEditRule dto = s.BuildRule(idx)!;
        dto.Name = "Light weapons, steel";
        dto.Conditions.Clear();
        dto.Conditions.Add(new LootEditCondition { NodeType = VTankNodeTypes.LongValKeyE, Lines = new List<string> { Pick(LootRuleText.Skills, "Light Weapons"), "159" } });
        dto.Conditions.Add(new LootEditCondition { NodeType = VTankNodeTypes.LongValKeyE, Lines = new List<string> { Pick(LootRuleText.WieldRequirements, "Base Skill"), "158" } });
        dto.Conditions.Add(new LootEditCondition { NodeType = VTankNodeTypes.LongValKeyNE, Lines = new List<string> { Pick(LootRuleText.Materials, "Steel"), "131" } });
        dto.Conditions.Add(new LootEditCondition { NodeType = VTankNodeTypes.LongValKeyE, Lines = new List<string> { Pick(LootRuleText.DamageTypes, "Electric"), "45" } });
        dto.Conditions.Add(new LootEditCondition { NodeType = VTankNodeTypes.LongValKeyE, Lines = new List<string> { "12345", "131" } });   // typed raw
        string json = JsonSerializer.Serialize(new LootEditCommand { Op = "update_rule", Index = idx, Expect = s.BuildRule(idx)!.Name, Rule = dto },
            LootEditJsonContext.Default.LootEditCommand);
        Eq(s.Apply(JsonSerializer.Deserialize(json, LootEditJsonContext.Default.LootEditCommand)!), LootEditSession.Outcome.Changed, "update_rule with named values");
        Check(s.Save(), "save named values");

        LootEditSession re = OpenOk(s.Path);
        VTankLootRule saved = re.Profile!.Rules[idx];
        Eq(saved.Name, "Light weapons, steel", "reopened rule");
        string Lines(int c) => string.Join(",", saved.Conditions[c].DataLines);
        Eq(Lines(0), "45,159", "Light Weapons saved as 45");
        Eq(Lines(1), "2,158", "Base Skill saved as 2");
        Eq(Lines(2), "64,131", "Steel saved as 64");
        Eq(Lines(3), "64,45", "Electric saved as 64");
        Eq(Lines(4), "12345,131", "a raw number saved as typed");
        string text = File.ReadAllText(s.Path).Replace("\r\n", "\n");
        Check(text.Contains("\n45\n159\n") && text.Contains("\n64\n131\n"), "file holds plain numbers (format unchanged)");
        Check(re.Encode().AsSpan().SequenceEqual(File.ReadAllBytes(s.Path)), "reopened file re-encodes byte for byte");
        string sum = LootRuleText.Summary(saved, 400);
        Check(sum.Contains("WieldSkilltype = Light Weapons") && sum.Contains("MaterialType != Steel") && sum.Contains("WieldRequirements = Base Skill"),
            "summary: " + sum);
    }
}

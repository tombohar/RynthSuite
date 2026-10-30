using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// "Add to loot profile" from a clicked item (2026-10-04): the rule an item
// makes (LootItemRules), its placement, and LootEditSession.InsertAndSave on
// both formats. The safety tests check the saved file byte for byte: the
// original with one rule spliced in, the old file kept as .bak, and the result
// opening editable in the in-game editor and loading in the parser the external
// editor and the evaluator use. Only temp copies are ever written.
internal static partial class Program
{
    private static string _addDir = string.Empty;

    private static void RunLootAddTests(string[] args)
    {
        _addDir = Path.Combine(Path.GetTempPath(), "RynthLootAddTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_addDir);
        try
        {
            TestAddDefaults();
            TestAddExactPattern();
            TestAddLikeThis();
            TestAddPreviewAndOrder();
            TestAddNative();
            TestAddUtlInsert(crlf: false);
            TestAddUtlInsert(crlf: true);
            TestAddUtlRefusals();
            TestAddUtlV0();
            TestAddJsonInsert(crlf: false);
            TestAddJsonInsert(crlf: true);
            TestAddJsonEdgeCases();
            TestAddBuiltInReasonWire();
            TestAddDeployedCopies(args);
        }
        finally
        {
            try { Directory.Delete(_addDir, recursive: true); } catch { }
        }
    }

    // The popup's "why RynthAi takes it anyway" (2026-10-05): on the wire next to OrderNote,
    // and a payload from a RynthAi without it reads as empty.
    private static void TestAddBuiltInReasonWire()
    {
        Console.WriteLine("\n-- LootAdd: built-in keep reason on the wire --");
        var st = new LootEditState { ItemDraft = new LootEditItemDraft { Seq = 3, OrderNote = "Goes in at the end. Why.", BuiltInReason = "Why." } };
        string json = System.Text.Json.JsonSerializer.Serialize(st, LootEditJsonContext.Default.LootEditState);
        LootEditState? back = System.Text.Json.JsonSerializer.Deserialize(json, LootEditJsonContext.Default.LootEditState);
        Eq(back?.ItemDraft?.BuiltInReason, "Why.", "BuiltInReason round-trips");
        Eq(back?.ItemDraft?.OrderNote, "Goes in at the end. Why.", "OrderNote too");
        LootEditState? old = System.Text.Json.JsonSerializer.Deserialize("{\"ItemDraft\":{\"Seq\":1,\"OrderNote\":\"x\"}}", LootEditJsonContext.Default.LootEditState);
        Eq(old?.ItemDraft?.BuiltInReason, "", "an older RynthAi's draft: empty");
    }

    private static string AddFile(string name, byte[] bytes)
    {
        string path = Path.Combine(_addDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static LootItemFacts Pea() => new() { Id = 1, Name = "Copper Pea", ObjectClass = (int)AcObjectClass.Misc, StackSize = 5, MaxStackSize = 100 };
    private static LootItemFacts Opal() => new() { Id = 2, Name = "Black Opal", ObjectClass = (int)AcObjectClass.Gem, Ints = { [131] = 16, [105] = 6 } };

    private static LootItemRuleDraft BuildOk(LootItemFacts f, LootItemRuleOptions? o = null)
    {
        LootItemRuleDraft? d = LootItemRules.Build(f, o ?? new LootItemRuleOptions(), out string error);
        Check(d != null, $"build {f.Name}: {error}");
        return d ?? new LootItemRuleDraft();
    }

    private static string Lines(VTankLootCondition c) => c.NodeType + ":" + string.Join("|", c.DataLines);

    private static void TestAddDefaults()
    {
        Console.WriteLine("\n-- LootAdd: rule from an item, defaults --");
        LootItemRuleDraft opal = BuildOk(Opal());
        Eq(opal.Rule.Action, VTankLootAction.Keep, "gem: Keep");
        Eq(opal.Rule.KeepCount, (int?)null, "Keep has no count line");
        Eq(opal.Rule.Name, "Keep Black Opal", "default name");
        Eq(string.Join(" ; ", opal.Rule.Conditions.Select(Lines)), "7:11 ; 1:^Black Opal$|1", "name + class conditions");
        Eq(opal.Rule.CustomExpression, "", "v1 custom expression line (empty)");
        Check(opal.Rule.Enabled, "enabled");

        LootItemRuleDraft pea = BuildOk(Pea());
        Eq(pea.Rule.Action, VTankLootAction.KeepUpTo, "stack: Keep #");
        Eq(pea.Rule.KeepCount, (int?)100, "Keep # defaults to one full stack");
        Eq(pea.Rule.Name, "Keep 100 Copper Pea", "Keep # name says the count");

        var scroll = new LootItemFacts { Name = "Scroll of Fire Bolt VI", ObjectClass = (int)AcObjectClass.Scroll };
        Eq(BuildOk(scroll).Rule.Action, VTankLootAction.Read, "spell scroll: Read");

        var bag = new LootItemFacts { Name = "Salvaged Iron", ObjectClass = (int)AcObjectClass.Salvage, Ints = { [131] = 61 } };
        Eq(BuildOk(bag).Rule.Action, VTankLootAction.Keep, "salvage bag: Keep (it is salvage already)");

        var unstacked = new LootItemFacts { Name = "Mana Stone", ObjectClass = (int)AcObjectClass.ManaStone, StackSize = 3 };
        Eq(BuildOk(unstacked).Rule.KeepCount, (int?)3, "no max stack known: its own stack");

        LootItemRuleDraft named = BuildOk(Pea(), new LootItemRuleOptions
        {
            Match = LootItemMatch.Name, Action = VTankLootAction.Sell, RuleName = "  Sell peas\nplease ",
        });
        Eq(named.Rule.Action, VTankLootAction.Sell, "action chosen");
        Eq(named.Rule.KeepCount, (int?)null, "Sell: no count");
        Eq(named.Rule.Name, "Sell peas please", "rule name: one line, trimmed");
        Eq(string.Join(" ; ", named.Rule.Conditions.Select(Lines)), "1:^Copper Pea$|1", "name only");

        LootItemRuleDraft kc = BuildOk(Pea(), new LootItemRuleOptions { Action = VTankLootAction.KeepUpTo, KeepCount = 25 });
        Eq(kc.Rule.KeepCount, (int?)25, "Keep # count chosen");
        Eq(kc.DefaultName, "Keep 25 Copper Pea", "default name follows the count");

        var unknown = new LootItemFacts { Name = "Odd Thing" };
        LootItemRuleDraft u = BuildOk(unknown);
        Eq(u.Match, LootItemMatch.Name, "unknown class: name only");
        Check(u.Notes.Count == 1, "and says so");

        Check(LootItemRules.Build(new LootItemFacts { ObjectClass = 11 }, new LootItemRuleOptions(), out string e1) == null && e1.Length > 0, "no name: refused");
        Check(LootItemRules.Build(Pea(), new LootItemRuleOptions { Action = (VTankLootAction)7 }, out string e2) == null && e2.Length > 0, "unknown action: refused");
        Check(LootItemRules.Build(Pea(), new LootItemRuleOptions { Action = VTankLootAction.KeepUpTo, KeepCount = -1 }, out _) == null, "negative Keep #: refused");
    }

    private static void TestAddExactPattern()
    {
        Console.WriteLine("\n-- LootAdd: exact name patterns --");
        string name = "Scroll of Fire Bolt (III). [x] a+b? $5 ^ | {1} #9 \\";
        string pattern = LootItemRules.ExactPattern(name);
        Check(Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase), "matches its own name");
        Check(Regex.IsMatch(name.ToUpperInvariant(), pattern, RegexOptions.IgnoreCase), "case-insensitive (as both evaluators match)");
        Check(!Regex.IsMatch(name + "x", pattern, RegexOptions.IgnoreCase), "not a longer name");
        Check(!Regex.IsMatch("x" + name, pattern, RegexOptions.IgnoreCase), "not a name with a prefix");
        Eq(LootItemRules.ExactPattern("Copper Pea"), "^Copper Pea$", "plain names stay readable (spaces not escaped)");
        Check(!Regex.IsMatch("Copper Peas", LootItemRules.ExactPattern("Copper Pea"), RegexOptions.IgnoreCase), "Copper Pea is not Copper Peas");
    }

    private static void TestAddLikeThis()
    {
        Console.WriteLine("\n-- LootAdd: items like this --");
        var armor = new LootItemFacts
        {
            Name = "Iron Breastplate", ObjectClass = (int)AcObjectClass.Armor,
            Ints = { [131] = 61, [105] = 7, [28] = 300, [9] = 512, [370] = 2, [374] = 1 },
            Spells = { "Strength Self VI", "Impenetrability VI", "", "Blade Bane VI", "Fire Bane VI", "Acid Bane VI" },
        };
        LootItemRuleDraft d = BuildOk(armor, new LootItemRuleOptions { Match = LootItemMatch.Like });
        string conds = string.Join(" ; ", d.Rule.Conditions.Select(Lines));
        Eq(conds, "7:2 ; 12:61|131 ; 3:7|105 ; 12:512|9 ; 3:300|28 ; 2007:3 ; 0:^Strength Self VI$ ; 0:^Impenetrability VI$ ; 0:^Blade Bane VI$ ; 0:^Fire Bane VI$",
            "class, material, workmanship >=, slot, armor >=, ratings >=, the first 4 spells");
        Check(!d.Rule.Conditions.Any(c => c.NodeType == VTankNodeTypes.StringValueMatch), "no name condition");
        Check(d.Notes.Any(n => n.Contains("more than 4 spells")), "says spells were left out");
        Eq(d.Rule.Name, "Keep like Iron Breastplate", "like name");

        var mace = new LootItemFacts { Name = "Iron Mace", ObjectClass = (int)AcObjectClass.MeleeWeapon, Ints = { [159] = 45, [44] = 20, [48] = 5 } };
        Eq(string.Join(" ; ", BuildOk(mace, new() { Match = LootItemMatch.Like }).Rule.Conditions.Select(Lines)),
            "7:1 ; 12:45|159 ; 3:20|44", "melee: wield skill and damage >= (wield skill preferred to weapon skill)");
        var wand = new LootItemFacts { Name = "Wand", ObjectClass = (int)AcObjectClass.WandStaffOrb, Doubles = { [152] = 1.12 } };
        Eq(string.Join(" ; ", BuildOk(wand, new() { Match = LootItemMatch.Like }).Rule.Conditions.Select(Lines)),
            "7:31 ; 5:1.12|152", "caster: elemental damage >=");

        LootItemRuleDraft bare = BuildOk(new LootItemFacts { Name = "Gem", ObjectClass = 11 }, new() { Match = LootItemMatch.Like });
        Eq(bare.Rule.Conditions.Count, 1, "nothing known: class only");
        Check(bare.Notes.Any(n => n.Contains("every Gem") && n.Contains("Assess")), "warns that it matches every Gem");

        Check(LootItemRules.Build(new LootItemFacts { Name = "x" }, new() { Match = LootItemMatch.Like }, out string e) == null && e.Length > 0,
            "like without a class: refused");
    }

    private static void TestAddPreviewAndOrder()
    {
        Console.WriteLine("\n-- LootAdd: preview and placement --");
        List<string> p = LootItemRules.PreviewLines(BuildOk(Opal(), new() { Match = LootItemMatch.Like }).Rule);
        Eq(string.Join(" / ", p), "Rule: Keep like Black Opal / Action: Keep / If: Class is Gem / and MaterialType = Black Opal / and ItemWorkmanship >= 6",
            "preview lines (values by name)");
        List<string> k = LootItemRules.PreviewLines(BuildOk(Pea()).Rule);
        Eq(k[1], "Action: Keep # 100", "Keep # preview");
        Eq(k[3], "and Name matches \"^Copper Pea$\"", "name line");

        Eq(LootItemRules.ChooseIndex(5, i => i == 3 || i == 4), 3, "before the first rule that matches");
        Eq(LootItemRules.ChooseIndex(5, _ => false), 5, "nothing matches: the end");
        Eq(LootItemRules.ChooseIndex(0, _ => true), 0, "empty profile: 0");

        VTankLootRule a = BuildOk(Opal()).Rule, b = BuildOk(Opal(), new() { RuleName = "other" }).Rule;
        Check(LootItemRules.SameRule(a, b), "same rule, names aside");
        Check(!LootItemRules.SameRule(a, BuildOk(Opal(), new() { Action = VTankLootAction.Sell }).Rule), "another action differs");
        Check(!LootItemRules.SameRule(a, BuildOk(Opal(), new() { Match = LootItemMatch.Name }).Rule), "other conditions differ");
    }

    private static void TestAddNative()
    {
        Console.WriteLine("\n-- LootAdd: native (JSON) form --");
        var dropped = new List<string>();
        LootRule? n = LootItemRules.ToNative(BuildOk(Pea()).Rule, dropped);
        Check(n != null && dropped.Count == 0, "name + class converts whole");
        Eq(n?.Action, LootAction.KeepUpTo, "Keep #");
        Eq(n?.KeepCount, 100, "count");
        Eq(n == null ? "" : string.Join(" ; ", n.Conditions), "ObjectClass == Misc ; StringKey[1] matches \"^Copper Pea$\"", "conditions");
        Eq(LootItemRules.ToNative(BuildOk(Opal()).Rule, dropped)?.KeepCount, 1, "Keep: count 1 (the native default)");

        var armor = new LootItemFacts { Name = "Robe", ObjectClass = (int)AcObjectClass.Clothing, Ints = { [28] = 50 }, Spells = { "Epic Strength" } };
        dropped.Clear();
        LootRule? like = LootItemRules.ToNative(BuildOk(armor, new() { Match = LootItemMatch.Like }).Rule, dropped);
        Eq(like == null ? "" : string.Join(" ; ", like.Conditions), "ObjectClass == Clothing ; LongKey[28] >= 50", "spell left out");
        Eq(dropped.Count, 1, "and named");
        var onlySpell = new VTankLootRule { Conditions = { new VTankLootCondition(VTankNodeTypes.SpellNameMatch, "0", new[] { "x" }) } };
        Check(LootItemRules.ToNative(onlySpell, new List<string>()) == null, "a rule left with no conditions is refused (it would match everything)");
    }

    // ── .utl ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="after"/> is <paramref name="before"/> with the rule count line
    /// bumped and one block of <paramref name="blockLines"/> lines inserted, nothing else.
    /// </summary>
    private static void CheckSplicedUtl(string before, string after, int blockLines, string label)
    {
        string nl = before.Contains("\r\n") ? "\r\n" : "\n";
        Check(after.Contains("\r\n") == (nl == "\r\n"), $"{label}: line endings kept");
        var a = before.Split(nl).ToList();
        var b = after.Split(nl).ToList();
        Eq(b.Count, a.Count + blockLines, $"{label}: one block of {blockLines} lines added");
        int countLine = a[0] == "UTL" ? 2 : 0;
        Eq(int.Parse(b[countLine]), int.Parse(a[countLine]) + 1, $"{label}: rule count line +1");
        b[countLine] = a[countLine];
        int first = 0;
        while (first < a.Count && a[first] == b[first]) first++;
        b.RemoveRange(first, Math.Min(blockLines, b.Count - first));
        Check(a.SequenceEqual(b), $"{label}: every other line unchanged");
    }

    private static int RuleLineCount(VTankLootRule r, int version)
    {
        string text = VTankLootWriter.Serialize(new VTankLootProfile { FileVersion = version, Rules = { r } });
        return text.Split('\n').Length - 1 - (version >= 1 ? 3 : 1);
    }

    private static void TestAddUtlInsert(bool crlf)
    {
        string label = crlf ? "utl CRLF" : "utl LF";
        Console.WriteLine($"\n-- LootAdd: insert into a .utl ({label}) --");
        string text = crlf ? EditFixture.Replace("\n", "\r\n") : EditFixture;
        byte[] original = Encoding.UTF8.GetBytes(text);
        string path = AddFile(crlf ? "add-crlf.utl" : "add.utl", original);
        var s = new LootEditSession();
        Check(s.Open(path) && !s.IsReadOnly && s.CanInsert, $"{label}: opens editable");
        var before = VTankLootParser.LoadFromText(text);

        VTankLootRule rule = BuildOk(Pea()).Rule;
        Check(s.InsertAndSave(rule, null, 2), $"{label}: inserted at 3: {s.Message}");
        Check(s.Message.Contains("Added \"Keep 100 Copper Pea\" at 3"), $"{label}: message says what and where ({s.Message})");
        Check(!s.IsDirty, $"{label}: saved, nothing pending");
        Eq(s.BuildState("", null).Focus, 2, $"{label}: focus on the new rule (the editor selects it)");
        byte[] saved = File.ReadAllBytes(path);
        Check(File.ReadAllBytes(path + ".bak").AsSpan().SequenceEqual(original), $"{label}: .bak == the original bytes");
        Check(!File.Exists(path + ".tmp"), $"{label}: no .tmp left");
        CheckSplicedUtl(text, Encoding.UTF8.GetString(saved), RuleLineCount(rule, 1), label);

        // Loads everywhere: the parser (external editor, evaluator, /ra lootparse) and the in-game editor.
        VTankLootProfile back = VTankLootParser.Load(path);
        Eq(back.Rules.Count, 6, $"{label}: parser reads 6 rules");
        Eq(back.Rules[2].Name, "Keep 100 Copper Pea", $"{label}: new rule at 3");
        Eq(back.Rules[2].KeepCount, (int?)100, $"{label}: its count");
        Eq(string.Join(" ; ", back.Rules[2].Conditions.Select(Lines)), "7:8 ; 1:^Copper Pea$|1", $"{label}: its conditions");
        for (int i = 0, j = 0; i < back.Rules.Count; i++)
        {
            if (i == 2) continue;
            VTankLootRule o = before.Rules[j++], n = back.Rules[i];
            Check(o.Name == n.Name && o.Action == n.Action && o.KeepCount == n.KeepCount && o.Priority == n.Priority
                  && o.CustomExpression == n.CustomExpression && o.Conditions.Select(Lines).SequenceEqual(n.Conditions.Select(Lines))
                  && o.Conditions.Select(c => c.LengthCode).SequenceEqual(n.Conditions.Select(c => c.LengthCode)),
                $"{label}: rule '{o.Name}' unchanged");
        }
        Check(back.SalvageCombine != null && back.SalvageCombine.PerMaterial.Count == before.SalvageCombine!.PerMaterial.Count,
            $"{label}: salvage combine block kept");
        var again = new LootEditSession();
        Check(again.Open(path) && !again.IsReadOnly, $"{label}: reopens editable in the in-game editor (round-trips)");
        Check(again.Encode().AsSpan().SequenceEqual(saved), $"{label}: and would save it byte-identical");

        // At the end, then at the top.
        Check(s.InsertAndSave(BuildOk(Opal()).Rule, null, -1), $"{label}: append");
        Eq(VTankLootParser.Load(path).Rules[^1].Name, "Keep Black Opal", $"{label}: appended last");
        Check(s.InsertAndSave(BuildOk(Opal(), new() { RuleName = "Top" }).Rule, null, 0), $"{label}: insert at the top");
        Eq(VTankLootParser.Load(path).Rules[0].Name, "Top", $"{label}: first");
        Eq(VTankLootParser.Load(path).Rules.Count, 8, $"{label}: 8 rules");
    }

    private static void TestAddUtlRefusals()
    {
        Console.WriteLine("\n-- LootAdd: .utl refusals leave the file alone --");
        byte[] original = Encoding.UTF8.GetBytes(EditFixture);
        string path = AddFile("refuse.utl", original);
        var s = new LootEditSession();
        s.Open(path);
        Cmd(s, "rename", 0, "half-done edit");
        Check(s.IsDirty, "an unsaved edit");
        Check(!s.InsertAndSave(BuildOk(Pea()).Rule, null, 0), "refused while there are unsaved edits");
        Check(s.Message.Contains("Unsaved changes"), "says why");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "file untouched");
        Eq(s.RuleCount, 5, "no phantom rule in the session");

        string odd = EditFixture.Replace("3;1;7", "3;1;7;abc");
        string opath = AddFile("refuse-odd.utl", Encoding.UTF8.GetBytes(odd));
        var o = new LootEditSession();
        o.Open(opath);
        Check(o.IsReadOnly && !o.CanInsert, "non-round-tripping .utl: no insert");
        Check(!o.InsertAndSave(BuildOk(Pea()).Rule, null, 0), "refused");
        Eq(File.ReadAllText(opath), odd, "file untouched");

        // Changed on disk since it was opened: refused, the file reloaded, then it works.
        string cpath = AddFile("refuse-changed.utl", original);
        var c = new LootEditSession();
        c.Open(cpath);
        string changed = EditFixture.Replace("Keep Coins", "Keep Coins!");
        File.WriteAllText(cpath, changed);
        Check(!c.InsertAndSave(BuildOk(Pea()).Rule, null, 0), "file changed on disk: refused");
        Check(c.Message.Contains("changed on disk"), $"says so ({c.Message})");
        Eq(File.ReadAllText(cpath), changed, "the other writer's file untouched");
        Check(!c.IsDirty && c.Profile?.Rules[0].Name == "Keep Coins!", "reloaded their version");
        Check(c.InsertAndSave(BuildOk(Pea()).Rule, null, 0), "then the add works");
        Eq(VTankLootParser.Load(cpath).Rules[1].Name, "Keep Coins!", "on top of their change");

        var none = new LootEditSession();
        Check(!none.InsertAndSave(BuildOk(Pea()).Rule, null, 0), "nothing open: refused");
        var fresh = new LootEditSession();
        fresh.Open(Path.Combine(_addDir, "not-there.utl"));
        Check(!fresh.InsertAndSave(BuildOk(Pea()).Rule, null, 0), "a profile that doesn't exist yet: refused");
        Check(!File.Exists(Path.Combine(_addDir, "not-there.utl")), "and not created");
    }

    private static void TestAddUtlV0()
    {
        Console.WriteLine("\n-- LootAdd: v0 (headerless) .utl --");
        string v0 = string.Join("\n", new[] { "1", "Keep Coins", "3;1;7", "7" }) + "\n";
        string path = AddFile("v0.utl", Encoding.UTF8.GetBytes(v0));
        var s = new LootEditSession();
        Check(s.Open(path) && !s.IsReadOnly, "v0 opens editable");
        VTankLootRule rule = BuildOk(Opal()).Rule;
        Check(s.InsertAndSave(rule, null, -1), $"append: {s.Message}");
        string after = File.ReadAllText(path);
        CheckSplicedUtl(v0, after, RuleLineCount(rule, 0), "v0");
        Check(!after.Contains("\n\n"), "no custom-expression line in a v0 file");
        Eq(VTankLootParser.Load(path).Rules.Count, 2, "2 rules");
        Check(new LootEditSession() is var r && r.Open(path) && !r.IsReadOnly, "reopens editable");
    }

    // ── .json ────────────────────────────────────────────────────────────

    private static LootProfile JsonFixture()
    {
        var p = new LootProfile { Name = "native" };
        var gems = new LootRule { Name = "Keep gems", Action = LootAction.Keep };
        gems.Conditions.Add(new ObjectClassCondition { ObjectClass = AcObjectClass.Gem });
        var off = new LootRule { Name = "Off", Enabled = false, Action = LootAction.Sell };
        off.Conditions.Add(new LongValKeyGECondition { Key = 19, Value = 5000 });
        var peas = new LootRule { Name = "Peas", Action = LootAction.KeepUpTo, KeepCount = 0 };
        peas.Conditions.Add(new StringValueCondition { Key = 1, Pattern = "Pea$" });
        peas.Conditions.Add(new CharacterSkillGECondition { Skill = AcSkillType.Salvaging, Value = 100 });
        p.Rules.AddRange(new[] { gems, off, peas });
        p.SalvageCombine = new SalvageCombineSettings { Enabled = true, DefaultBands = "1-6, 7-10" };
        return p;
    }

    private static void TestAddJsonInsert(bool crlf)
    {
        string label = crlf ? "json CRLF" : "json LF";
        Console.WriteLine($"\n-- LootAdd: insert into a native .json ({label}) --");
        string json = System.Text.Json.JsonSerializer.Serialize(JsonFixture(), LootJsonContext.Default.LootProfile).Replace("\r\n", "\n");
        // A property this build doesn't know: the splice keeps it (LootProfile.Save would drop it).
        json = json.Replace("\"Name\": \"native\",", "\"Name\": \"native\",\n  \"FutureField\": { \"a\": [1, 2] },");
        if (crlf) json = json.Replace("\n", "\r\n");
        byte[] original = Encoding.UTF8.GetBytes(json);
        string path = AddFile(crlf ? "add-crlf.json" : "add.json", original);

        var s = new LootEditSession();
        Check(s.Open(path) && s.IsReadOnly && s.CanInsert, $"{label}: opens (read-only for edits) and takes an insert");
        Eq(s.Format, "json", $"{label}: format");
        var dropped = new List<string>();
        LootRule native = LootItemRules.ToNative(BuildOk(Pea()).Rule, dropped)!;
        Check(s.InsertAndSave(BuildOk(Pea()).Rule, native, 1), $"{label}: inserted at 2: {s.Message}");
        Eq(s.RuleCount, 4, $"{label}: session shows 4 rules");
        Eq(s.JsonProfile?.Rules[1].Name, "Keep 100 Copper Pea", $"{label}: session's copy has it");
        byte[] saved = File.ReadAllBytes(path);
        string after = Encoding.UTF8.GetString(saved);
        Check(File.ReadAllBytes(path + ".bak").AsSpan().SequenceEqual(original), $"{label}: .bak == original");
        Check(after.Contains("\"FutureField\": { \"a\": [1, 2] }"), $"{label}: unknown property kept");
        Check(after.Contains("\r\n") == crlf && (crlf || !after.Contains('\r')), $"{label}: line endings kept");

        // Byte-level: a common prefix and suffix, the middle is the new rule only.
        int pre = 0;
        while (pre < json.Length && json[pre] == after[pre]) pre++;
        int suf = 0;
        while (suf < json.Length - pre && json[json.Length - 1 - suf] == after[after.Length - 1 - suf]) suf++;
        Eq(pre + suf, json.Length, $"{label}: the original is all there, in order");
        string added = after.Substring(pre, after.Length - json.Length);
        Check(added.Contains("Copper Pea") && !added.Contains("Keep gems") && !added.Contains("\"Peas\""), $"{label}: only the new rule was added");

        LootProfile back = LootProfile.Load(path);
        Eq(back.Rules.Count, 4, $"{label}: loads with 4 rules (RynthAi's loader, the external editor's)");
        Eq(back.Rules[1].Name, "Keep 100 Copper Pea", $"{label}: at 2");
        Eq(back.Rules[1].Action, LootAction.KeepUpTo, $"{label}: action");
        Eq(back.Rules[1].KeepCount, 100, $"{label}: count");
        Eq(string.Join(" ; ", back.Rules[1].Conditions), "ObjectClass == Misc ; StringKey[1] matches \"^Copper Pea$\"", $"{label}: conditions");
        LootProfile orig = JsonFixture();
        Eq(back.Rules[0].Name, "Keep gems", $"{label}: rule 1 kept");
        Check(!back.Rules[2].Enabled && back.Rules[2].Action == LootAction.Sell && back.Rules[2].Conditions[0].ToString() == orig.Rules[1].Conditions[0].ToString(),
            $"{label}: disabled rule kept as it was");
        Eq(back.Rules[3].KeepCount, 0, $"{label}: KeepCount 0 is written and reads back as 0, before and after alike");
        Eq(LootProfile.Load(path + ".bak").Rules[2].KeepCount, back.Rules[3].KeepCount, $"{label}: same as the original reads");
        Eq(string.Join(" ; ", back.Rules[3].Conditions), string.Join(" ; ", orig.Rules[2].Conditions), $"{label}: rule 3's conditions kept");
        Eq(back.SalvageCombine?.DefaultBands, "1-6, 7-10", $"{label}: salvage combine kept");

        // The end, then the top.
        Check(s.InsertAndSave(BuildOk(Opal()).Rule, LootItemRules.ToNative(BuildOk(Opal()).Rule, dropped), -1), $"{label}: append");
        Eq(LootProfile.Load(path).Rules[^1].Name, "Keep Black Opal", $"{label}: last");
        Check(s.InsertAndSave(BuildOk(Opal()).Rule, LootItemRules.ToNative(BuildOk(Opal(), new() { RuleName = "Top" }).Rule, dropped), 0), $"{label}: top");
        Eq(LootProfile.Load(path).Rules[0].Name, "Top", $"{label}: first");
        Eq(LootProfile.Load(path).Rules.Count, 6, $"{label}: 6 rules");
        Check(new LootEditSession() is var r && r.Open(path) && r.RuleCount == 6, $"{label}: the in-game editor opens it");
    }

    private static void TestAddJsonEdgeCases()
    {
        Console.WriteLine("\n-- LootAdd: native .json edge cases --");
        LootRule native = LootItemRules.ToNative(BuildOk(Opal()).Rule, new List<string>())!;

        string empty = "{\n  \"Name\": \"e\",\n  \"Rules\": []\n}";
        string epath = AddFile("empty.json", Encoding.UTF8.GetBytes(empty));
        var e = new LootEditSession();
        e.Open(epath);
        Check(e.InsertAndSave(BuildOk(Opal()).Rule, native, 0), $"empty Rules list: {e.Message}");
        Eq(LootProfile.Load(epath).Rules.Count, 1, "one rule");
        Check(File.ReadAllText(epath).StartsWith("{\n  \"Name\": \"e\",\n  \"Rules\": [\n    {"), "indented like the writer");

        byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"Rules\":[{\"Name\":\"a\",\"Enabled\":true}]}")).ToArray();
        string bpath = AddFile("bom.json", bom);
        var b = new LootEditSession();
        b.Open(bpath);
        Check(b.InsertAndSave(BuildOk(Opal()).Rule, native, 0), $"BOM, one line: {b.Message}");
        byte[] bsaved = File.ReadAllBytes(bpath);
        Check(bsaved[0] == 0xEF && bsaved[1] == 0xBB && bsaved[2] == 0xBF, "BOM kept");
        Eq(LootProfile.Load(bpath).Rules[1].Name, "a", "old rule now second");

        foreach (var (name, text) in new[]
        {
            ("norules.json", "{\"Name\":\"x\"}"),
            ("twice.json", "{\"Rules\":[],\"Rules\":[]}"),
            ("notlist.json", "{\"Rules\":{}}"),
        })
        {
            string path = AddFile(name, Encoding.UTF8.GetBytes(text));
            var s = new LootEditSession();
            if (!s.Open(path)) { Check(true, $"{name}: doesn't even open"); Eq(File.ReadAllText(path), text, $"{name}: untouched"); continue; }
            Check(!s.InsertAndSave(BuildOk(Opal()).Rule, native, 0), $"{name}: refused ({s.Message})");
            Eq(File.ReadAllText(path), text, $"{name}: untouched");
        }

        // Changed on disk: refused and reloaded, then fine.
        string json = "{\"Rules\":[{\"Name\":\"a\",\"Enabled\":true}]}";
        string cpath = AddFile("changed.json", Encoding.UTF8.GetBytes(json));
        var c = new LootEditSession();
        c.Open(cpath);
        string theirs = "{\"Rules\":[{\"Name\":\"b\",\"Enabled\":true}]}";
        File.WriteAllText(cpath, theirs);
        Check(!c.InsertAndSave(BuildOk(Opal()).Rule, native, 0), "changed on disk: refused");
        Eq(File.ReadAllText(cpath), theirs, "theirs untouched");
        Check(c.InsertAndSave(BuildOk(Opal()).Rule, native, -1), "then fine");
        Eq(LootProfile.Load(cpath).Rules[0].Name, "b", "on top of theirs");

        Check(!c.InsertAndSave(BuildOk(Opal()).Rule, null, 0), "no native form: refused");
    }

    /// <summary>Bonus: every deployed loot profile, as a temp copy (the originals are only read).</summary>
    private static void TestAddDeployedCopies(string[] args)
    {
        const string folder = @"C:\Games\RynthSuite\RynthAi\LootProfiles";
        if (!Directory.Exists(folder)) return;
        var files = Directory.GetFiles(folder, "*.utl").Concat(Directory.GetFiles(folder, "*.json")).ToList();
        if (files.Count == 0) return;
        Console.WriteLine($"\n-- LootAdd: deployed profiles, temp copies (bonus, {files.Count}) --");
        foreach (string src in files)
        {
            byte[] original = File.ReadAllBytes(src);
            string copy = AddFile("deployed-" + Path.GetFileName(src), original);
            var s = new LootEditSession();
            if (!s.Open(copy)) { Console.WriteLine($"  [skip] {Path.GetFileName(src)}: {s.Message}"); continue; }
            if (!s.CanInsert)
            {
                Check(!s.InsertAndSave(BuildOk(Pea()).Rule, null, 0), $"{Path.GetFileName(src)}: read-only, refused");
                Check(File.ReadAllBytes(copy).AsSpan().SequenceEqual(original), $"{Path.GetFileName(src)}: untouched");
                continue;
            }
            int count = s.RuleCount;
            VTankLootRule rule = BuildOk(Pea()).Rule;
            LootRule? native = LootItemRules.ToNative(rule, new List<string>());
            int at = count / 2;
            Check(s.InsertAndSave(rule, native, at), $"{Path.GetFileName(src)}: insert at {at + 1}: {s.Message}");
            var again = new LootEditSession();
            Check(again.Open(copy) && again.RuleCount == count + 1, $"{Path.GetFileName(src)}: reopens with {count + 1} rules");
            if (s.Format == "utl")
            {
                Check(!again.IsReadOnly, $"{Path.GetFileName(src)}: still editable");
                CheckSplicedUtl(LootEditSession_Decode(original), LootEditSession_Decode(File.ReadAllBytes(copy)), RuleLineCount(rule, s.Profile!.FileVersion),
                    Path.GetFileName(src));
            }
            Check(File.ReadAllBytes(src).AsSpan().SequenceEqual(original), $"{Path.GetFileName(src)}: the deployed original is untouched");
        }
    }

    private static string LootEditSession_Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}

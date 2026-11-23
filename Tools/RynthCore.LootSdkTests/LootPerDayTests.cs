using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RynthCore.Loot;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// Keep # per day (LootPerDay, 2026-11-22): the option is saved in a .utl's RynthKeepPerDay extra
// block (VTank skips it: checked here with a reader that follows VTClassic's own steps) and in a
// .json rule's PerDayHours (left out when off); both round trip, through the editor session too.
internal static partial class Program
{
    // A profile with a plain Keep #, a Keep # per day and a SalvageCombine block, in VTank's CRLF.
    private static VTankLootProfile PerDayProfile()
    {
        var p = new VTankLootProfile { FileVersion = 1 };
        p.Rules.Add(new VTankLootRule
        {
            Name = "Keep 5 Peas", CustomExpression = "", Action = VTankLootAction.KeepUpTo, KeepCount = 5,
            Conditions = { new VTankLootCondition(VTankNodeTypes.ObjectClass, "3", new[] { "12" }) },
        });
        p.Rules.Add(new VTankLootRule
        {
            Name = "(T) Treasure Maps", CustomExpression = "", Action = VTankLootAction.KeepUpTo, KeepCount = 2, PerDayHours = 24,
            Conditions = { new VTankLootCondition(VTankNodeTypes.StringValueMatch, "17", new[] { "Treasure Map", "1" }) },
        });
        p.Rules.Add(new VTankLootRule
        {
            Name = "Keep Coins", CustomExpression = "", Action = VTankLootAction.Keep,
            Conditions = { new VTankLootCondition(VTankNodeTypes.ObjectClass, "3", new[] { "7" }) },
        });
        return p;
    }

    private static void RunPerDayTests()
    {
        Console.WriteLine("\n-- Keep # per day: .utl block --");
        TestPerDayUtlRoundTrip();
        TestPerDayVTankSkipsBlock();
        TestPerDayStaleEntryIgnored();
        Console.WriteLine("\n-- Keep # per day: .json --");
        TestPerDayJson();
        Console.WriteLine("\n-- Keep # per day: editor session --");
        string dir = Path.Combine(Path.GetTempPath(), "RynthLootPerDayTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { TestPerDaySession(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        Eq(LootPerDay.Key("  Treasure Maps ", 4), "Treasure Maps", "a rule's count key is its trimmed name");
        Eq(LootPerDay.Key("", 4), "#5", "an unnamed rule's is #N, 1-based");
        Eq(LootPerDay.Clamp(0), 0, "0 h is off");
        Eq(LootPerDay.Clamp(-3), 0, "negative is off");
        Eq(LootPerDay.Clamp(500), 168, "at most a week");
    }

    private static void TestPerDayUtlRoundTrip()
    {
        VTankLootProfile p = PerDayProfile();
        string text = VTankLootWriter.Serialize(p);
        string[] lines = text.Split('\n');
        int at = Array.IndexOf(lines, LootPerDay.UtlBlockName);
        Check(at > 0, "the block is written");
        Eq(lines[at + 2], "1", "block format 1");
        Eq(lines[at + 3], "1", "one entry");
        Eq(lines[at + 4], "1", "for rule index 1");
        Eq(lines[at + 5], "(T) Treasure Maps", "by its name");
        Eq(lines[at + 6], "24", "24 hours");
        // VTank's length: the data lines' characters plus 2 for each CRLF.
        int expectLen = new[] { "1", "1", "1", "(T) Treasure Maps", "24" }.Sum(l => l.Length + 2);
        Eq(lines[at + 1], expectLen.ToString(), "the length is VTank's character count");
        Eq(text.Length - text.IndexOf(LootPerDay.UtlBlockName, StringComparison.Ordinal),
            LootPerDay.UtlBlockName.Length + 1 + lines[at + 1].Length + 1 + expectLen - 5,
            "it is the last thing in the file (LF here, so 1 per line end)");

        VTankLootProfile back = VTankLootParser.LoadFromText(text);
        Eq(back.Rules.Count, 3, "rules read back");
        Eq(back.Rules[1].PerDayHours, 24, "the per-day rule's window reads back");
        Eq(back.Rules[1].KeepCount, 2, "and its count");
        Eq(back.Rules[0].PerDayHours, 0, "the plain Keep # stays plain");
        Eq(VTankLootWriter.Serialize(back), text, "written again: identical");
        Eq(VTankLootParser.LoadFromText(text.Replace("\n", "\r\n")).Rules[1].PerDayHours, 24, "CRLF file reads the same");

        // With a SalvageCombine block, the block comes after it, and both read back.
        p.SalvageCombine = new SalvageCombineSettings { RawVersion = "0", Enabled = true, DefaultBands = "1-10" };
        p.SalvageCombine.PerMaterial[60] = "1-9, 10";
        string withSalvage = VTankLootWriter.Serialize(p);
        Check(withSalvage.IndexOf("SalvageCombine", StringComparison.Ordinal) < withSalvage.IndexOf(LootPerDay.UtlBlockName, StringComparison.Ordinal),
            "SalvageCombine first, the per-day block last");
        VTankLootProfile back2 = VTankLootParser.LoadFromText(withSalvage);
        Check(back2.SalvageCombine != null && back2.SalvageCombine.PerMaterial.TryGetValue(60, out string? b) && b == "1-9, 10", "salvage combine still reads");
        Eq(back2.Rules[1].PerDayHours, 24, "and so does the window");
        Eq(VTankLootWriter.Serialize(back2), withSalvage, "identical written again");

        // No rule with the option: no block, the file as before.
        VTankLootProfile plain = PerDayProfile();
        plain.Rules[1].PerDayHours = 0;
        Check(!VTankLootWriter.Serialize(plain).Contains(LootPerDay.UtlBlockName), "no per-day rule: no block");
        // The option only means something on a Keep #.
        plain.Rules[2].PerDayHours = 24;
        Check(!VTankLootWriter.Serialize(plain).Contains(LootPerDay.UtlBlockName), "a Keep rule's window is not written");
    }

    /// <summary>
    /// A file with the block, read the way VTClassic reads a profile (cLootRules.Read, its rule and
    /// requirement reads, UTLFileExtraBlockManager.Read and the SalvageCombine handler), must load:
    /// every rule, no exception, the unknown block skipped by its length to the end of the file.
    /// </summary>
    private static void TestPerDayVTankSkipsBlock()
    {
        foreach (bool salvage in new[] { false, true })
            foreach (bool crlf in new[] { true, false })
            {
                VTankLootProfile p = PerDayProfile();
                if (salvage)
                {
                    p.SalvageCombine = new SalvageCombineSettings { RawVersion = "0", Enabled = true, DefaultBands = "1-10" };
                    p.SalvageCombine.PerMaterial[60] = "1-9, 10";
                }
                string text = VTankLootWriter.Serialize(p);
                if (crlf) text = text.Replace("\n", "\r\n");
                string label = (salvage ? "with SalvageCombine" : "rules only") + (crlf ? ", CRLF" : ", LF");
                string? error = VTClassicLikeRead(text, out int rules, out List<string> blocks, out List<int> actions);
                Check(error == null, $"VTank-style read loads ({label}): {error}");
                Eq(rules, 3, $"VTank-style read: every rule ({label})");
                Check(actions.SequenceEqual(new[] { 10, 10, 1 }), $"VTank-style read: the per-day rule is a Keep # there ({label})");
                Check(blocks.SequenceEqual(salvage ? new[] { "SalvageCombine", LootPerDay.UtlBlockName } : new[] { LootPerDay.UtlBlockName }),
                    $"VTank-style read: blocks {string.Join(",", blocks)} ({label})");
            }
    }

    private static string? VTClassicLikeRead(string text, out int ruleCount, out List<string> blocks, out List<int> actions)
    {
        ruleCount = 0;
        blocks = new List<string>();
        actions = new List<int>();
        try
        {
            using var inf = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
            string first = inf.ReadLine()!;
            int version = 0, count;
            if (first == "UTL") { version = int.Parse(inf.ReadLine()!); count = int.Parse(inf.ReadLine()!); }
            else count = int.Parse(first);
            for (int i = 0; i < count; i++)
            {
                inf.ReadLine();                                   // name
                if (version >= 1) inf.ReadLine();                 // custom expression
                string[] big = inf.ReadLine()!.Split(';');
                int act = int.Parse(big[1]);
                actions.Add(act);
                if (act == 10) int.Parse(inf.ReadLine()!);        // keep count
                for (int c = 2; c < big.Length; c++)
                {
                    int type = int.Parse(big[c]);
                    if (version >= 1) int.Parse(inf.ReadLine()!); // length code
                    int n = VTankNodeTypes.GetDataLineCount(type);
                    if (n < 0) throw new InvalidOperationException("the test profile has only known nodes");
                    for (int d = 0; d < n; d++) if (inf.ReadLine() == null) throw new EndOfStreamException("rule data");
                }
                ruleCount++;
            }
            while (!inf.EndOfStream)                              // UTLFileExtraBlockManager.Read
            {
                string type = inf.ReadLine()!;
                int len = int.Parse(inf.ReadLine()!);
                blocks.Add(type);
                if (type == "SalvageCombine")
                {
                    inf.ReadLine();                                // format version
                    inf.ReadLine();                                // default combine string
                    int n = int.Parse(inf.ReadLine()!);
                    for (int k = 0; k < n; k++) { int.Parse(inf.ReadLine()!); inf.ReadLine(); }
                    if (inf.EndOfStream) continue;
                    int m = int.Parse(inf.ReadLine()!);
                    for (int k = 0; k < m; k++) { int.Parse(inf.ReadLine()!); int.Parse(inf.ReadLine()!); }
                }
                else
                {
                    char[] skip = new char[len];
                    inf.Read(skip, 0, len);
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static void TestPerDayStaleEntryIgnored()
    {
        string text = VTankLootWriter.Serialize(PerDayProfile());
        // Another tool renamed the rule and kept the block: the entry no longer names it.
        string renamed = text.Replace("(T) Treasure Maps\n\n", "Maps\n\n");
        VTankLootProfile p = VTankLootParser.LoadFromText(renamed);
        Eq(p.Rules[1].Name, "Maps", "the rule was renamed");
        Eq(p.Rules[1].PerDayHours, 0, "an entry whose name doesn't match is not applied");
        Check(VTankLootWriter.Serialize(p) != renamed, "so the file doesn't write back the same (the editor opens it read-only)");
        // A block that isn't ours by format is not read.
        string[] lines = text.Split('\n');
        string head = LootPerDay.UtlBlockName + "\n" + lines[Array.IndexOf(lines, LootPerDay.UtlBlockName) + 1] + "\n";
        Check(text.Contains(head + "1\n"), "the block's head as written");
        VTankLootProfile q = VTankLootParser.LoadFromText(text.Replace(head + "1\n", head + "9\n"));
        Eq(q.Rules[1].PerDayHours, 0, "an unknown block format is ignored");
    }

    private static void TestPerDayJson()
    {
        var p = new LootProfile { Name = "per day" };
        var maps = new LootRule { Name = "Treasure Maps", Action = LootAction.KeepUpTo, KeepCount = 2, PerDayHours = 24 };
        maps.Conditions.Add(new StringValueCondition { Key = 1, Pattern = "Treasure Map" });
        p.Rules.Add(maps);
        p.Rules.Add(new LootRule { Name = "Gems", Action = LootAction.KeepUpTo, KeepCount = 5 });
        string json = JsonSerializer.Serialize(p, LootJsonContext.Default.LootProfile);
        Check(json.Contains("\"PerDayHours\": 24"), "PerDayHours is written");
        Eq(json.Split("PerDayHours").Length - 1, 1, "only for the rule that has it (0 is left out, old files unchanged)");
        LootProfile back = JsonSerializer.Deserialize(json, LootJsonContext.Default.LootProfile)!;
        Eq(back.Rules[0].PerDayHours, 24, "it reads back");
        Eq(back.Rules[1].PerDayHours, 0, "a plain Keep # reads back plain");
        Eq(JsonSerializer.Serialize(back, LootJsonContext.Default.LootProfile), json, "round trip identical");
        LootProfile old = JsonSerializer.Deserialize("{\"Name\":\"old\",\"Rules\":[{\"Name\":\"Gems\",\"Action\":\"KeepUpTo\",\"KeepCount\":3}]}", LootJsonContext.Default.LootProfile)!;
        Eq(old.Rules[0].PerDayHours, 0, "a file from before has none");

        // "Add to loot profile" splices a per-day rule into a .json profile, every other byte kept.
        byte[] file = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new LootProfile { Name = "x", Rules = { new LootRule { Name = "Gems", Action = LootAction.Keep } } }, LootJsonContext.Default.LootProfile));
        byte[]? spliced = LootJsonSplice.Insert(file, maps, 0, out int at, out string error);
        Check(spliced != null, "splice a per-day rule: " + error);
        if (spliced != null)
            Eq(JsonSerializer.Deserialize(Encoding.UTF8.GetString(spliced), LootJsonContext.Default.LootProfile)!.Rules[at].PerDayHours, 24, "the spliced rule keeps its window");
    }

    private static void TestPerDaySession(string dir)
    {
        string text = VTankLootWriter.Serialize(PerDayProfile()).Replace("\n", "\r\n");
        string path = Path.Combine(dir, "perday.utl");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        var s = new LootEditSession();
        Check(s.Open(path), "open a profile with a per-day rule: " + s.Message);
        Check(!s.IsReadOnly, "it is editable (round-trips byte for byte)");
        Check(s.Encode().AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(text)), "unedited Encode is the file");

        LootEditState st = s.BuildState("", null);
        Eq(st.Rules[1].PerDayHours, 24, "the list row has the window");
        Eq(st.Rules[0].PerDayHours, 0, "a plain Keep # row has 0");
        LootEditRule rule = s.BuildRule(1)!;
        Eq(rule.PerDayHours, 24, "the rule view has it");

        // An engine without the option sends no PerDayHours: the rule keeps its own.
        string wire = JsonSerializer.Serialize(rule, LootEditJsonContext.Default.LootEditRule);
        LootEditRule fromOld = JsonSerializer.Deserialize(wire.Replace("\"PerDayHours\":24,", ""), LootEditJsonContext.Default.LootEditRule)!;
        Check(fromOld.PerDayHours == null, "an older engine's rule has no PerDayHours");
        fromOld.KeepCount = 3;
        Eq(s.Apply(new LootEditCommand { Op = "update_rule", Index = 1, Expect = rule.Name, Rule = fromOld }), LootEditSession.Outcome.Changed, "update from an older engine");
        Eq(s.Profile!.Rules[1].PerDayHours, 24, "keeps the window");
        Eq(s.Profile!.Rules[1].KeepCount, 3, "and takes the count");

        // Change the window, save, read the file back.
        LootEditRule edit = s.BuildRule(1)!;
        edit.PerDayHours = 48;
        s.Apply(new LootEditCommand { Op = "update_rule", Index = 1, Expect = edit.Name, Rule = edit });
        Check(s.Save(), "save: " + s.Message);
        VTankLootProfile saved = VTankLootParser.Load(path);
        Eq(saved.Rules[1].PerDayHours, 48, "the saved file has the new window");
        Check(File.ReadAllText(path).Contains("\r\n" + LootPerDay.UtlBlockName + "\r\n"), "in the file's own CRLF");

        // Out of range is clamped; another action drops it.
        edit = s.BuildRule(1)!;
        edit.PerDayHours = 1000;
        s.Apply(new LootEditCommand { Op = "update_rule", Index = 1, Expect = edit.Name, Rule = edit });
        Eq(s.Profile!.Rules[1].PerDayHours, 168, "a window over a week is a week");
        edit = s.BuildRule(1)!;
        edit.Action = (int)VTankLootAction.Keep;
        s.Apply(new LootEditCommand { Op = "update_rule", Index = 1, Expect = edit.Name, Rule = edit });
        Eq(s.Profile!.Rules[1].PerDayHours, 0, "made a plain Keep, the window goes");

        // Duplicate and rename carry it (the block names the rule by its current name and place).
        s.Apply(new LootEditCommand { Op = "reload", Force = true });
        Eq(s.Profile!.Rules[1].PerDayHours, 48, "reloaded from the saved file");
        s.Apply(new LootEditCommand { Op = "duplicate", Index = 1, Expect = s.Profile.Rules[1].Name });
        Eq(s.Profile!.Rules[2].PerDayHours, 48, "a duplicate has the window");
        s.Apply(new LootEditCommand { Op = "rename", Index = 0, Expect = s.Profile.Rules[0].Name, Value = "Peas" });
        s.Apply(new LootEditCommand { Op = "move", Index = 2, To = 0, Expect = s.Profile.Rules[2].Name });
        Check(s.Save(), "save after duplicate, rename and move");
        VTankLootProfile again = VTankLootParser.Load(path);
        Eq(again.Rules[0].PerDayHours, 48, "the moved copy keeps its window");
        Eq(again.Rules[2].PerDayHours, 48, "and the original");
        Eq(again.Rules[1].PerDayHours, 0, "the renamed plain Keep # stays plain");
        Check(VTClassicLikeRead(File.ReadAllText(path), out int n, out _, out _) == null && n == 4, "and VTank still reads it");

        // Add to loot profile into a .utl with the block: verified and saved.
        var add = new VTankLootRule { Name = "Keep Gems", Action = VTankLootAction.Keep, Conditions = { new VTankLootCondition(VTankNodeTypes.ObjectClass, "0", new[] { "11" }) } };
        Check(s.InsertAndSave(add, null, 1), "add a rule above the per-day ones: " + s.Message);
        VTankLootProfile afterAdd = VTankLootParser.Load(path);
        Eq(afterAdd.Rules[0].PerDayHours, 48, "the per-day rule above is untouched");
        Eq(afterAdd.Rules[3].PerDayHours, 48, "the one that moved down keeps its window (written by its new index)");
    }
}

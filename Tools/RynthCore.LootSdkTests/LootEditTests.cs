using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using RynthCore.Loot.Editing;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// LootEditSession: the plugin-side logic behind the in-game Loot Editor
// (RynthCore docs/IMGUI_LOOT_EDITOR.md). Load, the edit commands, validation,
// the no-clobber save, and above all that a round trip (open, edit through the
// wire DTOs, save) loses no rule data. Files go to a temp folder only.
internal static partial class Program
{
    // Every shape the editor must carry: v1 header, custom expression, priority,
    // Keep # with its count line, a DisabledRule node in the middle, typed nodes,
    // raw nodes (SpellMatch, colours), an unknown node type, odd length codes,
    // and a SalvageCombine block.
    private static readonly string EditFixture = string.Join("\n", new[]
    {
        "UTL", "1", "5",
        "Keep Coins", "", "3;1;7", "5", "7",
        "Keep 25 Peas", "expr here", "0;10;1;12", "25", "0", "Pea$", "1", "0", "5", "19",
        "Disabled Junk", "", "0;3;2;9999;4", "0", "250", "28", "0", "true", "0", "0.5", "5",
        "Unknown Node", "", "0;4;4242", "0", "mystery 1", "mystery 2",
        "Spells and Colours", "", "7;2;9;14;1000", "12", "Legendary", "Epic", "2", "0", "10", "20", "30", "0.1", "0.2",
            "0", "300", "34",
        "SalvageCombine", "0", "1", "1-6, 7-8, 9, 10", "1", "60", "1-9, 10", "0",
    }) + "\n";

    private static string _tempDir = string.Empty;

    private static void RunLootEditTests(string[] args)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RynthLootEditTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        try
        {
            TestEditUneditedSaveIsByteIdentical();
            TestEditEncodingsPreserved();
            TestEditWireRoundTripEveryRule();
            TestEditCommands();
            TestEditValidation();
            TestEditNoClobber();
            TestEditDiskWatch();
            TestEditReadOnlyCases();
            TestEditNewProfile();
            TestEditVocab();
            TestEditValueNames();
            TestEditDeployedProfile(args);
        }
        finally
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private static string TempFile(string name, byte[] bytes)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static LootEditSession OpenOk(string path)
    {
        var s = new LootEditSession();
        Check(s.Open(path), $"open {Path.GetFileName(path)}: {s.BuildState("", null).Message}");
        return s;
    }

    private static LootEditSession.Outcome Cmd(LootEditSession s, string op, int index = -1, string value = "", int to = -1,
        string? expect = null, bool force = false, LootEditRule? rule = null)
        => s.Apply(new LootEditCommand { Op = op, Index = index, Value = value, To = to, Expect = expect, Force = force, Rule = rule });

    private static void TestEditUneditedSaveIsByteIdentical()
    {
        Console.WriteLine("\n-- LootEdit: unedited save is byte-identical --");
        byte[] original = Encoding.UTF8.GetBytes(EditFixture);
        string path = TempFile("fixture.utl", original);
        LootEditSession s = OpenOk(path);
        Eq(s.RuleCount, 5, "fixture rule count");
        Check(!s.IsReadOnly, "fixture is editable (round-trips)");
        Check(s.Encode().AsSpan().SequenceEqual(original), "Encode() == original bytes");
        Check(s.Save(), "save");
        Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "saved file == original bytes");
        Check(File.Exists(path + ".bak"), ".bak kept");
        Check(!File.Exists(path + ".tmp"), "no .tmp left");
    }

    private static void TestEditEncodingsPreserved()
    {
        Console.WriteLine("\n-- LootEdit: encoding, CRLF, BOM preserved --");
        // CRLF + UTF-8 BOM + a non-ASCII name.
        string crlf = EditFixture.Replace("Keep Coins", "Keep Coins \u00e9\u2605").Replace("\n", "\r\n");
        byte[] bomBytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(crlf)).ToArray();
        LootEditSession s = OpenOk(TempFile("bom_crlf.utl", bomBytes));
        Check(s.Encode().AsSpan().SequenceEqual(bomBytes), "UTF-8 BOM + CRLF round-trips byte for byte");
        Eq(s.Profile!.Rules[0].Name, "Keep Coins \u00e9\u2605", "non-ASCII name read as UTF-8");

        // Windows-1252/Latin-1 bytes (not valid UTF-8): kept byte for byte.
        byte[] latin = Encoding.Latin1.GetBytes(EditFixture.Replace("Keep Coins", "Keep Caf\u00e9 \u00a3"));
        LootEditSession l = OpenOk(TempFile("latin1.utl", latin));
        Check(l.Encode().AsSpan().SequenceEqual(latin), "Latin-1 file round-trips byte for byte");
        Eq(l.Profile!.Rules[0].Name, "Keep Caf\u00e9 \u00a3", "Latin-1 name decoded");

        // No final newline stays that way.
        byte[] noNl = Encoding.UTF8.GetBytes(EditFixture.TrimEnd('\n'));
        LootEditSession n = OpenOk(TempFile("nonl.utl", noNl));
        Check(n.Encode().AsSpan().SequenceEqual(noNl), "missing final newline preserved");
    }

    private static void TestEditWireRoundTripEveryRule()
    {
        Console.WriteLine("\n-- LootEdit: every rule through the wire DTOs and back --");
        byte[] original = Encoding.UTF8.GetBytes(EditFixture);
        LootEditSession s = OpenOk(TempFile("wire.utl", original));
        WireRoundTripAll(s, "fixture");
        Check(s.Encode().AsSpan().SequenceEqual(original), "after update_rule of every rule, bytes unchanged");
        Check(s.IsDirty, "update_rule marks dirty");

        // The state payload survives JSON too.
        LootEditState st = s.BuildState(Path.Combine(_tempDir, "wire.utl"), new List<LootEditFile> { new() { Path = "x", Display = "x" } });
        string json = JsonSerializer.Serialize(st, LootEditJsonContext.Default.LootEditState);
        LootEditState? back = JsonSerializer.Deserialize(json, LootEditJsonContext.Default.LootEditState);
        Check(back != null && back.Rules.Count == 5 && back.InUse && back.Dirty, "state JSON round-trip");
        Eq(back?.Rules[2].Enabled, false, "row shows the disabled rule");
        Eq(back?.Rules[2].Conditions, 2, "row condition count leaves out DisabledRule");
        Eq(back?.Rules[1].KeepCount, 25, "row keep count");
    }

    /// <summary>BuildRule(i) -> JSON -> update_rule(i) for every rule.</summary>
    private static void WireRoundTripAll(LootEditSession s, string label)
    {
        int failed = 0;
        for (int i = 0; i < s.RuleCount; i++)
        {
            LootEditRule? dto = s.BuildRule(i);
            if (dto == null) { failed++; continue; }
            string json = JsonSerializer.Serialize(dto, LootEditJsonContext.Default.LootEditRule);
            LootEditRule? back = JsonSerializer.Deserialize(json, LootEditJsonContext.Default.LootEditRule);
            var cmd = new LootEditCommand { Op = "update_rule", Index = i, Expect = dto.Name, Rule = back };
            string cmdJson = JsonSerializer.Serialize(cmd, LootEditJsonContext.Default.LootEditCommand);
            LootEditCommand? cmdBack = JsonSerializer.Deserialize(cmdJson, LootEditJsonContext.Default.LootEditCommand);
            if (cmdBack == null || s.Apply(cmdBack) != LootEditSession.Outcome.Changed)
            {
                failed++;
                if (failed <= 5) Console.WriteLine($"  [FAIL] {label} rule {i} '{dto.Name}': {s.BuildState("", null).Message}");
            }
        }
        _asserts++;
        if (failed > 0) { _fails++; Console.WriteLine($"  [FAIL] {label}: {failed} rule(s) refused update_rule"); }
    }

    private static void TestEditCommands()
    {
        Console.WriteLine("\n-- LootEdit: commands --");
        LootEditSession s = OpenOk(TempFile("cmds.utl", Encoding.UTF8.GetBytes(EditFixture)));
        List<VTankLootRule> r = s.Profile!.Rules;

        // Disable a rule that has no DisabledRule node: one is appended; enable removes it.
        Eq(Cmd(s, "set_enabled", 0, "false", expect: "Keep Coins"), LootEditSession.Outcome.Changed, "disable");
        Check(!r[0].Enabled && r[0].Conditions.Last().NodeType == VTankNodeTypes.DisabledRule, "disable appends 9999 true");
        Eq(Cmd(s, "set_enabled", 0, "true"), LootEditSession.Outcome.Changed, "enable");
        Check(r[0].Enabled && r[0].Conditions.All(c => c.NodeType != VTankNodeTypes.DisabledRule), "enable removes 9999");

        // Enable/disable a rule whose DisabledRule node sits mid-list: disable reuses it in place.
        Cmd(s, "set_enabled", 2, "true");
        Check(r[2].Enabled, "rule 2 enabled");
        Eq(r[2].Conditions.Count, 2, "rule 2 lost its 9999 node");
        LootEditSession s2 = OpenOk(TempFile("cmds2.utl", Encoding.UTF8.GetBytes(EditFixture)));
        var c9999 = s2.Profile!.Rules[2].Conditions[1];
        c9999.DataLines[0] = "false";
        Cmd(s2, "set_enabled", 2, "false");
        Eq(s2.Profile.Rules[2].Conditions[1].DataLines[0], "true", "disable flips the existing node in place");
        Eq(s2.Profile.Rules[2].Conditions.Count, 3, "no extra node");

        // Move, add, duplicate, delete, rename.
        Eq(Cmd(s, "move", 0, to: 3), LootEditSession.Outcome.Changed, "move 0 -> 3");
        Eq(r[3].Name, "Keep Coins", "moved rule at 3");
        Eq(r[0].Name, "Keep 25 Peas", "rule 1 shifted up");
        Eq(s.BuildState("", null).Focus, 3, "focus follows the moved rule");

        Eq(Cmd(s, "add", 0), LootEditSession.Outcome.Changed, "add after 0");
        Eq(r[1].Name, "New Rule", "new rule at 1");
        Eq(r[1].CustomExpression, "", "new rule has the v1 custom expression line");
        Eq(s.RuleCount, 6, "count after add");

        Eq(Cmd(s, "duplicate", 0), LootEditSession.Outcome.Changed, "duplicate 0");
        Eq(r[1].Name, "Keep 25 Peas (copy)", "copy name");
        Eq(r[1].KeepCount, 25, "copy keep count");
        Check(!ReferenceEquals(r[1].Conditions[0].DataLines, r[0].Conditions[0].DataLines), "copy has its own lines");

        Eq(Cmd(s, "delete", 1, expect: "Keep 25 Peas (copy)"), LootEditSession.Outcome.Changed, "delete copy");
        Eq(s.RuleCount, 6, "count after delete");

        Eq(Cmd(s, "rename", 1, "Renamed\nrule", expect: "New Rule"), LootEditSession.Outcome.Changed, "rename");
        Eq(r[1].Name, "Renamed rule", "newline in name became a space");

        // A stale index is refused, not applied to the wrong rule.
        Eq(Cmd(s, "delete", 0, expect: "Something Else"), LootEditSession.Outcome.Refused, "Expect mismatch refused");
        Eq(s.RuleCount, 6, "nothing deleted");
        Eq(Cmd(s, "delete", 99), LootEditSession.Outcome.Refused, "out of range refused");

        // Saved and reopened, the edits are there.
        Check(s.Save(), "save edits");
        LootEditSession re = OpenOk(s.Path);
        Eq(re.RuleCount, 6, "reopened count");
        Eq(re.Profile!.Rules[1].Name, "Renamed rule", "reopened rename");
        Eq(re.Profile.Rules[4].Name, "Keep Coins", "reopened order");
        Check(re.Profile.Rules[2].Enabled, "reopened enable");
    }

    private static void TestEditValidation()
    {
        Console.WriteLine("\n-- LootEdit: validation --");
        LootEditSession s = OpenOk(TempFile("valid.utl", Encoding.UTF8.GetBytes(EditFixture)));

        LootEditRule dto = s.BuildRule(0)!;
        dto.Conditions[0].Lines[0] = "abc";   // ObjectClass must be a whole number
        Eq(Cmd(s, "update_rule", 0, rule: dto), LootEditSession.Outcome.Refused, "non-numeric class refused");
        Eq(s.Profile!.Rules[0].Conditions[0].DataLines[0], "7", "rule untouched after refusal");

        dto = s.BuildRule(0)!;
        dto.Action = 99;
        Eq(Cmd(s, "update_rule", 0, rule: dto), LootEditSession.Outcome.Refused, "unknown action refused");

        dto = s.BuildRule(0)!;
        dto.Conditions[0].Lines.Add("extra");
        Eq(Cmd(s, "update_rule", 0, rule: dto), LootEditSession.Outcome.Refused, "too many lines refused");

        dto = s.BuildRule(0)!;
        dto.Conditions.Add(new LootEditCondition { NodeType = VTankNodeTypes.LongValKeyGE, Lines = new List<string> { "300" } });
        Eq(Cmd(s, "update_rule", 0, rule: dto), LootEditSession.Outcome.Changed, "short lines padded");
        Eq(s.Profile.Rules[0].Conditions[1].DataLines.Count, 2, "padded to arity");

        dto = s.BuildRule(0)!;
        dto.Name = "Two\r\nLines";
        dto.Action = (int)VTankLootAction.KeepUpTo;
        dto.KeepCount = 7;
        dto.Conditions[0].Lines[0] = " 12 ";
        Eq(Cmd(s, "update_rule", 0, rule: dto), LootEditSession.Outcome.Changed, "update with Keep #");
        VTankLootRule r0 = s.Profile.Rules[0];
        Eq(r0.Name, "Two Lines", "CRLF in name flattened");
        Eq(r0.KeepCount, 7, "keep count set");
        Eq(r0.Conditions[0].DataLines[0], "12", "number trimmed");
        dto = s.BuildRule(0)!;
        dto.Action = (int)VTankLootAction.Sell;
        Cmd(s, "update_rule", 0, rule: dto);
        Eq(s.Profile.Rules[0].KeepCount, null, "keep count line dropped for Sell");

        // The unknown node type (4242) passes through untouched but can't be invented.
        dto = s.BuildRule(3)!;
        Eq(dto.Conditions[0].NodeType, 4242, "rule 3 has the unknown node");
        Eq(Cmd(s, "update_rule", 3, rule: dto), LootEditSession.Outcome.Changed, "unknown node kept as-is");
        dto.Conditions[0].Lines[0] = "changed";
        Eq(Cmd(s, "update_rule", 3, rule: dto), LootEditSession.Outcome.Refused, "edited unknown node refused");

        // Enabled on the DTO reconciles the DisabledRule node.
        dto = s.BuildRule(2)!;
        dto.Enabled = true;
        Cmd(s, "update_rule", 2, rule: dto);
        Check(s.Profile.Rules[2].Enabled, "DTO Enabled=true enables");
    }

    private static void TestEditNoClobber()
    {
        Console.WriteLine("\n-- LootEdit: save never clobbers a file changed on disk --");
        string path = TempFile("clobber.utl", Encoding.UTF8.GetBytes(EditFixture));
        LootEditSession s = OpenOk(path);
        Cmd(s, "rename", 0, "Mine");

        string theirs = EditFixture.Replace("Keep Coins", "Theirs");
        File.WriteAllText(path, theirs);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Eq(Cmd(s, "save"), LootEditSession.Outcome.Refused, "save refused after an outside change");
        Check(s.BuildState("", null).ChangedOnDisk, "ChangedOnDisk flagged");
        Eq(File.ReadAllText(path), theirs, "their file untouched");

        Eq(Cmd(s, "open", force: false) , LootEditSession.Outcome.Refused, "open with no path refused");
        Eq(Cmd(s, "reload"), LootEditSession.Outcome.Refused, "reload without force refused (unsaved edits)");
        Eq(Cmd(s, "save", force: true), LootEditSession.Outcome.Saved, "Save anyway");
        Check(File.ReadAllText(path).Contains("Mine\n"), "our edit written");
        Eq(File.ReadAllText(path + ".bak"), theirs, "their version kept as .bak");
        Check(!s.BuildState("", null).ChangedOnDisk && !s.IsDirty, "clean after save");

        // Same-content rewrite (write time moves, bytes the same) doesn't block a save.
        Cmd(s, "rename", 0, "Mine again");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        Eq(Cmd(s, "save"), LootEditSession.Outcome.Saved, "touch-only change doesn't block save");
    }

    private static void TestEditDiskWatch()
    {
        Console.WriteLine("\n-- LootEdit: disk watch --");
        string path = TempFile("watch.utl", Encoding.UTF8.GetBytes(EditFixture));
        LootEditSession s = OpenOk(path);
        Check(!s.CheckDisk(), "no change, nothing to do");

        File.WriteAllText(path, EditFixture.Replace("Keep Coins", "Outside Edit"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Check(s.CheckDisk(), "change noticed");
        Eq(s.Profile!.Rules[0].Name, "Outside Edit", "clean session reloaded");

        Cmd(s, "rename", 1, "Local");
        File.WriteAllText(path, EditFixture);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        Check(s.CheckDisk(), "change under edits noticed");
        Eq(s.Profile.Rules[1].Name, "Local", "dirty session keeps its edits");
        Check(s.BuildState("", null).ChangedOnDisk, "and flags the conflict");
        Eq(Cmd(s, "reload", force: true), LootEditSession.Outcome.Opened, "Reload (discard)");
        Eq(s.Profile!.Rules[1].Name, "Keep 25 Peas", "reloaded from disk");
    }

    private static void TestEditReadOnlyCases()
    {
        Console.WriteLine("\n-- LootEdit: read-only cases --");
        string json = "{\"Name\":\"j\",\"Rules\":[{\"Name\":\"Keep gems\",\"Enabled\":true,\"Action\":\"Keep\",\"Conditions\":[{\"$t\":\"ObjectClass\",\"ObjectClass\":\"Gem\"}]}]}";
        string jpath = TempFile("legacy.json", Encoding.UTF8.GetBytes(json));
        LootEditSession j = OpenOk(jpath);
        Check(j.IsReadOnly, "JSON opens read-only");
        Eq(j.RuleCount, 1, "JSON rule count");
        Eq(Cmd(j, "rename", 0, "x"), LootEditSession.Outcome.Refused, "JSON edit refused");
        Eq(Cmd(j, "save"), LootEditSession.Outcome.Refused, "JSON save refused");
        Eq(File.ReadAllText(jpath), json, "JSON file untouched");
        Eq(j.BuildRule(0)?.Conditions[0].NodeType, -1, "JSON condition shown as text");
        Eq(j.BuildState("", null).Format, "json", "format json");

        // An info line with a part the parser skips: writing back would drop it, so read-only.
        string odd = EditFixture.Replace("3;1;7", "3;1;7;abc");
        string opath = TempFile("odd.utl", Encoding.UTF8.GetBytes(odd));
        LootEditSession o = OpenOk(opath);
        Check(o.IsReadOnly, "non-round-tripping .utl opens read-only");
        Eq(o.RuleCount, 5, "still lists its rules");
        Eq(Cmd(o, "delete", 0), LootEditSession.Outcome.Refused, "edits refused");
        Eq(File.ReadAllText(opath), odd, "file untouched");

        // An unknown node in the last rule swallows the file's tail (the parser can't
        // tell where it ends): writing back would change the file, so read-only.
        string tail = string.Join("\n", new[] { "UTL", "1", "1", "Last", "", "0;1;4242", "0", "a", "b" }) + "\n";
        LootEditSession t = OpenOk(TempFile("tail.utl", Encoding.UTF8.GetBytes(tail)));
        Check(t.IsReadOnly, "unknown node in the last rule: read-only");

        var bad = new LootEditSession();
        Check(!bad.Open(TempFile("bad.utl", Encoding.UTF8.GetBytes("UTL\nx\n"))), "garbage refused cleanly");
        Check(!bad.Open(Path.Combine(_tempDir, "missing.json")), "missing JSON refused");
        Check(!bad.Open(Path.Combine(_tempDir, "notes.txt")), "other extension refused");
    }

    private static void TestEditNewProfile()
    {
        Console.WriteLine("\n-- LootEdit: new profile (vendor's first) --");
        string path = Path.Combine(_tempDir, "AutoVendor", "Some Vendor.utl");
        var s = new LootEditSession();
        Check(s.Open(path), "open a path that doesn't exist");
        Check(!s.BuildState("", null).Exists, "state says new");
        Eq(Cmd(s, "add"), LootEditSession.Outcome.Changed, "add a rule");
        Check(s.Save(), "save creates it (and its folder)");
        LootEditSession re = OpenOk(path);
        Eq(re.RuleCount, 1, "created file has the rule");
        Eq(re.Profile!.FileVersion, 1, "v1 file");
    }

    private static void TestEditVocab()
    {
        Console.WriteLine("\n-- LootEdit: vocabulary --");
        LootEditVocab v = LootRuleText.BuildVocab();
        Eq(v.Actions.Count, 5, "5 actions");
        Eq(v.NodeTypes.Count, VTankNodeTypes.All.Length, "every node type listed");
        Check(v.NodeTypes.All(n => n.Defaults.Count == n.Lines), "defaults match arity");
        Check(v.NodeTypes.All(n => n.Name.All(ch => ch < 128)), "node names are ASCII");
        Check(v.ObjectClasses.Any(c => c.Id == 11 && c.Name == "Gem"), "object classes use the Decal/VTank numbering");
        string json = JsonSerializer.Serialize(v, LootEditJsonContext.Default.LootEditVocab);
        Check(JsonSerializer.Deserialize(json, LootEditJsonContext.Default.LootEditVocab)?.NodeTypes.Count == v.NodeTypes.Count, "vocab JSON round-trip");
        foreach (LootEditNodeType n in v.NodeTypes)
            if (n.Editor != "raw")
                Check(n.Labels.Count == n.Lines, $"typed editor {n.Name} labels every line");
    }

    /// <summary>Bonus: the real deployed profile through the session (read only, never written).</summary>
    private static void TestEditDeployedProfile(string[] args)
    {
        string path = args.FirstOrDefault(a => a.EndsWith("LootSnobV4.utl", StringComparison.OrdinalIgnoreCase))
                      ?? @"C:\Games\RynthSuite\RynthAi\LootProfiles\LootSnobV4.utl";
        if (!File.Exists(path)) return;
        Console.WriteLine("\n-- LootEdit: deployed profile (bonus, read only) --");
        byte[] original = File.ReadAllBytes(path);
        var s = new LootEditSession();
        Check(s.Open(path), "open deployed profile");
        Check(!s.IsReadOnly, "deployed profile is editable");
        Check(s.Encode().AsSpan().SequenceEqual(original), "deployed: Encode() == file bytes");
        WireRoundTripAll(s, "deployed");
        Check(s.Encode().AsSpan().SequenceEqual(original), "deployed: every rule through the wire, bytes unchanged");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LootEditState st = s.BuildState(path, null);
        string json = JsonSerializer.Serialize(st, LootEditJsonContext.Default.LootEditState);
        long buildMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"  ok  {Path.GetFileName(path)}: {s.RuleCount} rules, state JSON {json.Length / 1024} KB built in {buildMs} ms");
    }
}

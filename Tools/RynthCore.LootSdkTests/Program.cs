using System;
using System.IO;
using RynthCore.Loot;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

internal static class Program
{
    private static int Main(string[] args)
    {
        var paths = args.Length > 0 ? args : new[]
        {
            @"C:\Games\RynthSuite\RynthAi\LootProfiles\SalvageTest.utl",
            @"C:\Games\RynthSuite\RynthAi\LootProfiles\Loot Everything.utl",
            @"C:\Games\RynthSuite\RynthAi\LootProfiles\LootSnobV4.utl",
        };

        int failures = 0;
        foreach (string path in paths)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"SKIP   {path} (not found)");
                continue;
            }

            try
            {
                string original = File.ReadAllText(path);
                var profile = VTankLootParser.LoadFromText(original);
                string emitted = VTankLootWriter.Serialize(profile);

                // Normalise both sides to LF so we ignore CRLF differences.
                string normOrig = original.Replace("\r\n", "\n").Replace("\r", "\n");
                string normEmit = emitted.Replace("\r\n", "\n").Replace("\r", "\n");

                if (normOrig == normEmit)
                {
                    Console.WriteLine($"OK     {Path.GetFileName(path)} ({profile.Rules.Count} rules)");
                }
                else
                {
                    failures++;
                    Console.WriteLine($"DIFF   {Path.GetFileName(path)} ({profile.Rules.Count} rules)");
                    PrintFirstDiff(normOrig, normEmit);
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"ERROR  {Path.GetFileName(path)} — {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ── Editor-flow simulation ──────────────────────────────────────────
        // The editor wraps each rule in a VM whose FlushConditionsToData pushes
        // edits back into the underlying VTankLootRule before saving. Make sure
        // that path is also byte-stable when no edits were made.
        Console.WriteLine();
        Console.WriteLine("--- Editor-flow simulation ---");
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                string original = File.ReadAllText(path);
                var profile = VTankLootParser.LoadFromText(original);

                // Mimic editor wrap+flush.
                foreach (var rule in profile.Rules)
                {
                    var copyConds = new System.Collections.Generic.List<VTankLootCondition>(rule.Conditions);
                    rule.Conditions.Clear();
                    foreach (var c in copyConds) rule.Conditions.Add(c);
                }

                string emitted = VTankLootWriter.Serialize(profile);
                string normOrig = original.Replace("\r\n", "\n").Replace("\r", "\n");
                string normEmit = emitted.Replace("\r\n", "\n").Replace("\r", "\n");

                if (normOrig == normEmit)
                    Console.WriteLine($"OK     edit-flow {Path.GetFileName(path)}");
                else
                {
                    failures++;
                    Console.WriteLine($"DIFF   edit-flow {Path.GetFileName(path)}");
                    PrintFirstDiff(normOrig, normEmit);
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"ERROR  edit-flow {Path.GetFileName(path)} — {ex.Message}");
            }
        }

        // ── Mutate-and-reload ───────────────────────────────────────────────
        // Tweak a rule's name + add a SalvageCombine entry, save, reload, and
        // verify the change took.
        string lootSnob = @"C:\Games\RynthSuite\RynthAi\LootProfiles\LootSnobV4.utl";
        if (File.Exists(lootSnob))
        {
            try
            {
                var p = VTankLootParser.Load(lootSnob);
                if (p.Rules.Count > 0)
                {
                    string originalName = p.Rules[0].Name;
                    p.Rules[0].Name = "EDITED — " + originalName;

                    p.SalvageCombine ??= new SalvageCombineSettings();
                    p.SalvageCombine.PerMaterial[60] = "1-9, 10";

                    string tmp = Path.Combine(Path.GetTempPath(), "lootsnob_edit_test.utl");
                    VTankLootWriter.Save(p, tmp);

                    var reloaded = VTankLootParser.Load(tmp);
                    bool nameOk = reloaded.Rules.Count > 0
                                  && reloaded.Rules[0].Name == "EDITED — " + originalName;
                    bool salvageOk = reloaded.SalvageCombine != null
                                     && reloaded.SalvageCombine.PerMaterial.TryGetValue(60, out string? v)
                                     && v == "1-9, 10";

                    if (nameOk && salvageOk)
                        Console.WriteLine("OK     mutate-and-reload (name edit + salvage tweak survived)");
                    else
                    { failures++; Console.WriteLine($"FAIL   mutate-and-reload  nameOk={nameOk} salvageOk={salvageOk}"); }

                    File.Delete(tmp);
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"ERROR  mutate-and-reload — {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} failure(s).");
        return failures == 0 ? 0 : 1;
    }

    private static void PrintFirstDiff(string a, string b)
    {
        string[] aLines = a.Split('\n');
        string[] bLines = b.Split('\n');
        int n = Math.Min(aLines.Length, bLines.Length);
        for (int i = 0; i < n; i++)
        {
            if (aLines[i] != bLines[i])
            {
                int from = Math.Max(0, i - 2);
                Console.WriteLine($"  first diff at line {i + 1}:");
                for (int k = from; k <= i; k++)
                {
                    Console.WriteLine($"    [{k + 1}] -orig: {Trunc(aLines[k])}");
                    Console.WriteLine($"    [{k + 1}] +emit: {Trunc(bLines[k])}");
                }
                int aRem = aLines.Length - i;
                int bRem = bLines.Length - i;
                Console.WriteLine($"  (orig has {aRem} more lines, emit has {bRem} more)");
                return;
            }
        }
        if (aLines.Length != bLines.Length)
            Console.WriteLine($"  same content for first {n} lines but lengths differ: orig={aLines.Length} emit={bLines.Length}");
    }

    private static string Trunc(string s) => s.Length > 80 ? s.Substring(0, 80) + "…" : s;
}

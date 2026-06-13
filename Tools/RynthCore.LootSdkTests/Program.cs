using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Loot;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// Golden-file / unit tests for the pure LootSdk cores. Self-contained: every
// test below uses COMMITTED fixtures (embedded strings / programmatic models),
// so it runs identically on a clean checkout and in CI — no dependency on
// deployed C:\Games files. The old harness SKIPped everything when those files
// were absent and still printed "All checks passed" (a vacuous green); this
// fails on ZERO assertions so that can't recur. Deployed-file round-trip checks
// are kept as an optional bonus pass at the end.
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

    private static int Main(string[] args)
    {
        Console.WriteLine("=== LootSdk golden tests (committed fixtures) ===");
        TestParseBands();
        TestGetBandKey();
        TestNodeArity();
        TestVTankRoundTripProgrammatic();
        TestVTankFixtureParse();
        TestMalformedInputs();

        Console.WriteLine($"\nGolden tests: {_asserts} assertions, {_fails} failed.");

        // ── Vacuous-pass guard ──────────────────────────────────────────────
        // The whole reason this file was rewritten: a test run that asserts
        // nothing must NOT report success.
        if (_asserts == 0)
        {
            Console.WriteLine("ABORT: zero assertions ran — the test harness is broken.");
            return 1;
        }

        // ── Optional: round-trip real deployed profiles if present ───────────
        RunDeployedRoundTrip(args);

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ALL GOLDEN TESTS PASSED." : $"{_fails} GOLDEN FAILURE(S).");
        return _fails == 0 ? 0 : 1;
    }

    // ── Pure: SalvageCombineSettings.ParseBands ─────────────────────────────
    private static void TestParseBands()
    {
        Console.WriteLine("\n-- ParseBands --");
        void Bands(string? raw, (int, int)[] expected, string label)
        {
            var got = SalvageCombineSettings.ParseBands(raw);
            Eq(got.Count, expected.Length, $"ParseBands('{raw}') count [{label}]");
            int n = Math.Min(got.Count, expected.Length);
            for (int i = 0; i < n; i++)
                Eq((got[i].Min, got[i].Max), expected[i], $"ParseBands('{raw}')[{i}] [{label}]");
        }

        Bands("1-6, 7-8, 9, 10", new[] { (1, 6), (7, 8), (9, 9), (10, 10) }, "canonical");
        Bands(null, Array.Empty<(int, int)>(), "null");
        Bands("", Array.Empty<(int, int)>(), "empty");
        Bands("   ", Array.Empty<(int, int)>(), "whitespace");
        Bands("5", new[] { (5, 5) }, "single value");
        Bands("3-3", new[] { (3, 3) }, "explicit single-range");
        Bands("1-6,,9", new[] { (1, 6), (9, 9) }, "empty segment skipped");
        Bands("abc", Array.Empty<(int, int)>(), "non-numeric skipped");
        Bands("1 - 6", new[] { (1, 6) }, "internal whitespace tolerated");
        Bands("1-6, junk, 10", new[] { (1, 6), (10, 10) }, "mixed valid/invalid");
    }

    // ── Pure: SalvageCombineSettings.GetBandKey ─────────────────────────────
    private static void TestGetBandKey()
    {
        Console.WriteLine("\n-- GetBandKey --");
        var s = new SalvageCombineSettings { DefaultBands = "1-6, 7-8, 9, 10" };
        Eq(s.GetBandKey(0, 3), "1-6", "default band, wk3");
        Eq(s.GetBandKey(0, 6), "1-6", "default band, wk6 (upper edge)");
        Eq(s.GetBandKey(0, 7), "7-8", "default band, wk7");
        Eq(s.GetBandKey(0, 9), "9-9", "default band, wk9 (single)");
        Eq(s.GetBandKey(0, 10), "10-10", "default band, wk10");
        Eq(s.GetBandKey(0, 11), null, "above all bands -> null (do not combine)");
        Eq(s.GetBandKey(0, 0), null, "below all bands -> null");

        // Per-material override beats the default.
        s.PerMaterial[5] = "1-10";
        Eq(s.GetBandKey(5, 10), "1-10", "per-material override applies");
        Eq(s.GetBandKey(5, 11), null, "per-material override, above range -> null");
        Eq(s.GetBandKey(99, 10), "10-10", "non-overridden material still uses default");
    }

    private static void TestNodeArity()
    {
        Console.WriteLine("\n-- VTankNodeTypes arity --");
        Eq(VTankNodeTypes.GetDataLineCount(VTankNodeTypes.ObjectClass), 1, "ObjectClass arity");
        Eq(VTankNodeTypes.GetDataLineCount(VTankNodeTypes.LongValKeyGE), 2, "LongValKeyGE arity");
        Eq(VTankNodeTypes.GetDataLineCount(VTankNodeTypes.SpellMatch), 3, "SpellMatch arity");
        Eq(VTankNodeTypes.GetDataLineCount(VTankNodeTypes.DisabledRule), 1, "DisabledRule arity");
        Eq(VTankNodeTypes.GetDataLineCount(123456), -1, "unknown node type -> -1");
    }

    // ── VTank round-trip is byte-idempotent on a programmatically-built model ─
    private static void TestVTankRoundTripProgrammatic()
    {
        Console.WriteLine("\n-- VTank round-trip (programmatic) --");
        var profile = new VTankLootProfile { FileVersion = 1 };

        var keep = new VTankLootRule { Name = "Keep Coins", Priority = 1, Action = VTankLootAction.Keep };
        keep.Conditions.Add(new VTankLootCondition(VTankNodeTypes.ObjectClass, "5", new[] { "7" }));
        profile.Rules.Add(keep);

        var keepUpTo = new VTankLootRule { Name = "Keep Salvage Wk6+", Priority = 2, Action = VTankLootAction.KeepUpTo, KeepCount = 100 };
        keepUpTo.Conditions.Add(new VTankLootCondition(VTankNodeTypes.LongValKeyGE, "9", new[] { "6", "367" }));
        profile.Rules.Add(keepUpTo);

        profile.SalvageCombine = new SalvageCombineSettings { Enabled = true, DefaultBands = "1-6, 7-8, 9, 10", RawVersion = "0" };
        profile.SalvageCombine.PerMaterial[60] = "1-9, 10";

        string s1 = VTankLootWriter.Serialize(profile);
        var p2 = VTankLootParser.LoadFromText(s1);
        string s2 = VTankLootWriter.Serialize(p2);
        Eq(Norm(s2), Norm(s1), "serialize->parse->serialize is byte-idempotent");

        // Structural fidelity of the reparse.
        Eq(p2.Rules.Count, 2, "round-trip rule count");
        Eq(p2.Rules[0].Name, "Keep Coins", "rule0 name");
        Eq(p2.Rules[0].Action, VTankLootAction.Keep, "rule0 action");
        Eq(p2.Rules[1].Action, VTankLootAction.KeepUpTo, "rule1 action");
        Eq(p2.Rules[1].KeepCount, 100, "rule1 keepcount");
        Eq(p2.Rules[1].Conditions.Count, 1, "rule1 condition count");
        Eq(p2.Rules[1].Conditions[0].NodeType, VTankNodeTypes.LongValKeyGE, "rule1 cond node type");
        Eq(p2.Rules[1].Conditions[0].DataLines.Count, 2, "rule1 cond data line count");
        Check(p2.SalvageCombine != null, "salvage block survived");
        if (p2.SalvageCombine != null)
        {
            Eq(p2.SalvageCombine.GetBandKey(60, 10), "10-10", "per-material band reparse (wk10)");
            Eq(p2.SalvageCombine.GetBandKey(60, 5), "1-9", "per-material band reparse (wk5)");
        }
    }

    // ── A hand-written v1 .utl fixture parses to the documented structure ────
    private static void TestVTankFixtureParse()
    {
        Console.WriteLine("\n-- VTank fixture parse (wire format) --");
        // UTL / version / ruleCount, then per rule: name, customExpr, info
        // (priority;action;nodeType...), [keepCount if action 10], then per
        // condition: lengthCode, dataLines.
        string fixture = string.Join("\n", new[]
        {
            "UTL", "1", "2",
            "Disabled Junk Rule", "",
            "0;1;9999",          // Keep, one DisabledRule node
            "0", "true",         // lengthCode, DisabledRule payload = true
            "Read Scrolls", "",
            "5;4;7",             // priority 5, action Read(4), ObjectClass node
            "0", "29",           // lengthCode, class id
        }) + "\n";

        var p = VTankLootParser.LoadFromText(fixture);
        Eq(p.FileVersion, 1, "fixture FileVersion");
        Eq(p.Rules.Count, 2, "fixture rule count");
        Eq(p.Rules[0].Action, VTankLootAction.Keep, "fixture rule0 action");
        Check(!p.Rules[0].Enabled, "fixture rule0 is disabled (9999 true)");
        Eq(p.Rules[1].Name, "Read Scrolls", "fixture rule1 name");
        Eq(p.Rules[1].Action, VTankLootAction.Read, "fixture rule1 action = Read");
        Eq(p.Rules[1].Priority, 5, "fixture rule1 priority");
        Eq(p.Rules[1].Conditions[0].NodeType, VTankNodeTypes.ObjectClass, "fixture rule1 node type");
        Eq(p.Rules[1].Conditions[0].DataLines.Count, 1, "fixture rule1 data line count");
    }

    // ── Malformed / edge inputs must fail gracefully, never corrupt or hang ──
    private static void TestMalformedInputs()
    {
        Console.WriteLine("\n-- Malformed inputs --");
        SafeParse("", "empty string");
        SafeParse("UTL\n1\n0\n", "header with zero rules");
        SafeParse("UTL\n", "truncated header");
        SafeParse("garbage\nmore garbage\n\n", "non-UTL garbage");
        SafeParse("0\n", "v0 header, zero rules");

        // An unknown node type id must not be misread as a known arity.
        Eq(VTankNodeTypes.GetDataLineCount(2999), -1, "unknown node arity stays -1");
    }

    private static void SafeParse(string text, string label)
    {
        // "Fails gracefully" = the parser either parses the input OR rejects it
        // with a CLEAN, controlled exception (InvalidOperationException — the
        // parser's documented bad-file signal, which the plugin's loot loader
        // catches). What must NEVER happen is an uncontrolled failure
        // (NullReference, IndexOutOfRange, overflow, hang) — those indicate a
        // real parsing bug that could destabilize the bot.
        _asserts++;
        try
        {
            var p = VTankLootParser.LoadFromText(text);
            _ = VTankLootWriter.Serialize(p); // a successful parse must also re-serialize cleanly
        }
        catch (InvalidOperationException)
        {
            // Controlled rejection of malformed input — the desired behavior.
        }
        catch (Exception ex)
        {
            _fails++;
            Console.WriteLine($"  [FAIL] malformed '{label}' threw UNCONTROLLED {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string Norm(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    // ── Optional bonus: round-trip real deployed .utl files when present ─────
    private static void RunDeployedRoundTrip(string[] args)
    {
        var paths = args.Length > 0 ? args : new[]
        {
            @"C:\Games\RynthSuite\RynthAi\LootProfiles\SalvageTest.utl",
            @"C:\Games\RynthSuite\RynthAi\LootProfiles\Loot Everything.utl",
            @"C:\Games\RynthSuite\RynthAi\LootProfiles\LootSnobV4.utl",
        };
        bool any = false;
        foreach (string path in paths)
        {
            if (!File.Exists(path)) continue;
            any = true;
            try
            {
                string original = File.ReadAllText(path);
                var profile = VTankLootParser.LoadFromText(original);
                string emitted = VTankLootWriter.Serialize(profile);
                _asserts++;
                if (Norm(original) != Norm(emitted))
                {
                    _fails++;
                    Console.WriteLine($"  [FAIL] deployed round-trip DIFF: {Path.GetFileName(path)} ({profile.Rules.Count} rules)");
                }
                else
                {
                    Console.WriteLine($"  ok  deployed round-trip {Path.GetFileName(path)} ({profile.Rules.Count} rules)");
                }
            }
            catch (Exception ex)
            {
                _asserts++; _fails++;
                Console.WriteLine($"  [FAIL] deployed round-trip {Path.GetFileName(path)} threw {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (any) Console.WriteLine("\n-- deployed-profile round-trip (bonus) ran --");
    }
}

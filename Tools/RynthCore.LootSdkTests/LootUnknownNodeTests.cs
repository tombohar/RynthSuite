using System;
using System.Linq;
using RynthCore.Loot.VTank;

namespace RynthCore.LootSdkTests;

// Condition types the parser has no arity for (2009/2011/2016 from a third-party editor):
// their data must be bounded by the length code, not by guessing where the next rule
// starts. The fixture mirrors T10locky.utl on 2026-10-06, where a leading unknown node
// swallowed two following rules and a trailing one ate the SalvageCombine block.
internal static partial class Program
{
    private const string UnknownNodeUtl =
        "UTL\n1\n4\n" +
        // Unknown node, a known LongValKeyGE, then another unknown node.
        "Mixed\n\n0;1;2011;3;2011\n" +
        "3\n0\n" +
        "9\n15\n374\n" +
        "3\n0\n" +
        // Plain name rule right after it: was swallowed by the old heuristic.
        "Coins\n\n0;1;1\n" +
        "9\nCoin\n1\n" +
        // Unknown node whose length code is not a number: heuristic fallback still applies.
        "Bogus\n\n0;1;2010\n" +
        "x\nabc\n" +
        // Trailing unknown node directly before SalvageCombine.
        "Spirit\n\n0;1;2016\n" +
        "21\nEncapsulated Spirit\n" +
        "SalvageCombine\n1\n1\n1-6, 7-8, 9, 10\n1\n25\n1-10\n";

    private static void RunUnknownNodeTests()
    {
        Console.WriteLine("\n-- unknown VTank node types --");

        VTankLootProfile p = VTankLootParser.LoadFromText(UnknownNodeUtl);
        Eq(p.Rules.Count, 4, "unknown nodes: every rule parsed");
        Eq(string.Join("|", p.Rules.Select(r => r.Name)), "Mixed|Coins|Bogus|Spirit", "unknown nodes: rule names in order");

        VTankLootRule mixed = p.Rules[0];
        Eq(mixed.Conditions.Count, 3, "unknown nodes: Mixed keeps three conditions");
        Eq(string.Join(",", mixed.Conditions[0].DataLines), "0", "unknown nodes: leading 2011 reads only its own line");
        Eq(mixed.Conditions[1].NodeType, VTankNodeTypes.LongValKeyGE, "unknown nodes: known node after an unknown one");
        Eq(string.Join(",", mixed.Conditions[1].DataLines), "15,374", "unknown nodes: known node data aligned");
        Eq(string.Join(",", mixed.Conditions[2].DataLines), "0", "unknown nodes: trailing 2011 reads only its own line");

        Eq(string.Join(",", p.Rules[1].Conditions[0].DataLines), "Coin,1", "unknown nodes: following name rule intact");
        Eq(string.Join(",", p.Rules[2].Conditions[0].DataLines), "abc", "unknown nodes: non-numeric length code falls back");
        Eq(string.Join(",", p.Rules[3].Conditions[0].DataLines), "Encapsulated Spirit", "unknown nodes: 2016 reads one line");

        Check(p.SalvageCombine != null, "unknown nodes: SalvageCombine block survives a trailing unknown node");
        if (p.SalvageCombine != null)
        {
            Check(p.SalvageCombine.Enabled, "unknown nodes: SalvageCombine enabled");
            Eq(p.SalvageCombine.PerMaterial.TryGetValue(25, out string? bands) ? bands : null, "1-10",
               "unknown nodes: SalvageCombine per-material band");
        }

        // Unknown nodes keep their exact lines, so a re-save does not corrupt the profile.
        VTankLootProfile back = VTankLootParser.LoadFromText(VTankLootWriter.Serialize(p));
        Eq(string.Join("|", back.Rules.Select(r => r.Name)), "Mixed|Coins|Bogus|Spirit", "unknown nodes: round-trip rule names");
        Eq(string.Join(",", back.Rules[0].Conditions[2].DataLines), "0", "unknown nodes: round-trip keeps unknown data");
        Eq(p.DeclaredRuleCount, 4, "unknown nodes: declared count recorded");

        // A header that undercounts: every rule present is still read, SalvageCombine too,
        // and a re-save writes the true count.
        string undercounted = UnknownNodeUtl.Replace("UTL\n1\n4\n", "UTL\n1\n2\n");
        VTankLootProfile u = VTankLootParser.LoadFromText(undercounted);
        Eq(u.DeclaredRuleCount, 2, "undercounted header: declared count");
        Eq(string.Join("|", u.Rules.Select(r => r.Name)), "Mixed|Coins|Bogus|Spirit", "undercounted header: all rules read");
        Check(u.SalvageCombine != null, "undercounted header: SalvageCombine still parsed");
        Eq(VTankLootParser.LoadFromText(VTankLootWriter.Serialize(u)).DeclaredRuleCount, 4, "undercounted header: re-save fixes the count");
    }
}

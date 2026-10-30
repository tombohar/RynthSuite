using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiTests.Tests;

// Smoke tests for weapon weakness selection's data side (CreatureWeakness): which elements
// hurt a monster most, from the shipped server table (wcid + name), what the Damage tab
// learned, or the creature type / whole-word name keyword. Assertions are derived from the
// embedded data where they depend on it, so regenerating the tables doesn't break them.
internal static class CreatureWeaknessTests
{
    // Names that are in no table and contain no type keyword.
    private const string Unknown = "Qzxv Blorpington";
    // "Olthoi" is a type keyword (type 1); this exact name is not in the server table.
    private const string OlthoiByKeyword = "Grand Qzxv Olthoi Tyrant";

    public static void Register(Runner r)
    {
        r.Add("weakness: element names normalise", Normalize);
        r.Add("weakness: server table ranks most damage first", ServerTable);
        r.Add("weakness: type keyword is a whole word", TypeKeyword);
        r.Add("weakness: learned element goes first, then the type order", LearnedFirst);
        r.Add("weakness: UB seed ranks after server data and learned, before the type", SeedBeforeType);
    }

    private static void Normalize()
    {
        Check.Eq(CreatureWeakness.Normalize("Electric"), "Lightning", "Electric -> Lightning");
        Check.Eq(CreatureWeakness.Normalize("blade"), "Slash", "Blade -> Slash");
        Check.Eq(CreatureWeakness.Normalize("  fire "), "Fire", "case and spaces");
        Check.Eq(CreatureWeakness.Normalize("Holy"), "", "not an element");
        Check.Eq(CreatureWeakness.Normalize(null), "", "null");
        Check.Eq(CreatureWeakness.Elements.Length, 8, "eight elements");
    }

    private static (uint Wcid, string Name, double[] Mult)? FirstServerRow()
    {
        using Stream? s = typeof(CreatureWeaknessTests).Assembly
            .GetManifestResourceStream("RynthCore.Plugin.RynthAi.CreatureData.creature_resists.tsv");
        if (s == null) return null;
        using var reader = new StreamReader(s);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('\t');
            if (f.Length < 11 || !uint.TryParse(f[0], out uint wcid)) continue;
            var m = new double[8];
            for (int i = 0; i < 8; i++) m[i] = double.Parse(f[3 + i], CultureInfo.InvariantCulture);
            return (wcid, f[1], m);
        }
        return null;
    }

    private static void ServerTable()
    {
        var row = FirstServerRow();
        Check.NotNull(row, "creature_resists.tsv is embedded and has a data row");
        if (row == null) return;
        var (wcid, name, mult) = row.Value;

        var r = CreatureWeakness.Rank(wcid, name, 0, null);
        Check.NotNull(r, $"ranking for {name} ({wcid})");
        if (r == null) return;
        Check.Eq(r.Source, "server data", "source");
        Check.Eq(r.Order.Count, 8, "all eight elements ranked");
        for (int i = 1; i < r.Order.Count; i++)
        {
            var (pe, pm) = r.Order[i - 1];
            var (e, m) = r.Order[i];
            Check.True(pm >= m, $"most damage first: {pe} {pm} before {e} {m}");
            if (Math.Round(pm, 3) == Math.Round(m, 3))
                Check.True(Array.IndexOf(CreatureWeakness.Elements, pe) < Array.IndexOf(CreatureWeakness.Elements, e),
                    $"ties keep element order: {pe} before {e}");
        }
        foreach (var (e, m) in r.Order)
            Check.Near(m, mult[Array.IndexOf(CreatureWeakness.Elements, e)], 1e-9, $"{e} multiplier from the table");

        // By name alone (a meta asking by name): the same row.
        Check.Eq(CreatureWeakness.Rank(0, name, 0, null)?.Source, "server data", "found by name without a wcid");
        // The wcid with another name is not that creature (a custom monster reusing the wcid).
        Check.True(CreatureWeakness.Rank(wcid, Unknown, 0, null)?.Source != "server data", "wcid with a different name is not trusted");

        Check.True(CreatureWeakness.TryGetMultiplier(wcid, name, 0, "Electric", out double lm), "multiplier by alias");
        Check.Near(lm, mult[6], 1e-9, "Lightning multiplier");
    }

    private static void TypeKeyword()
    {
        var r = CreatureWeakness.Rank(0, OlthoiByKeyword, 0, null);
        Check.NotNull(r, "the Olthoi keyword finds a type");
        Check.Eq(r?.Source, "Olthoi type", "source names the type");
        Check.Eq(r?.Order.Count, 8, "the type lists all eight elements");
        if (r != null)
            for (int i = 1; i < r.Order.Count; i++)
                Check.True(r.Order[i - 1].Mult >= r.Order[i].Mult, $"type order is most damage first at {i}");

        // Same type by id (creature type 1 = Olthoi), with a name that has no keyword.
        var byId = CreatureWeakness.Rank(0, Unknown, 1, null);
        Check.Eq(byId?.Source, "Olthoi type", "creature type id wins over the name");
        Check.Eq(byId?.Order[0].Element, r?.Order[0].Element, "same ranking by id and by keyword");

        Check.Null(CreatureWeakness.Rank(0, Unknown, 0, null), "no data at all: null");
        Check.Null(CreatureWeakness.Rank(0, "Olthoiling Qzxv", 0, null), "keyword inside a longer word does not match");
        Check.False(CreatureWeakness.TryGetMultiplier(0, Unknown, 0, "Fire", out double m), "no multiplier when unknown");
        Check.Near(m, 1.0, 0, "unknown multiplier reads 1.0");
    }

    private static void LearnedFirst()
    {
        var type = CreatureWeakness.Rank(0, OlthoiByKeyword, 0, null);
        var r = CreatureWeakness.Rank(0, OlthoiByKeyword, 0, "Electric");
        Check.NotNull(r, "ranking with a learned element");
        if (r == null || type == null) return;
        Check.Eq(r.Source, "learned, then Olthoi type", "source");
        Check.Eq(r.Order[0].Element, "Lightning", "learned element first (normalised)");
        Check.True(double.IsNaN(r.Order[0].Mult), "learned element has no multiplier");
        Check.Eq(r.Order.Count, 8, "learned element is not listed twice");
        var rest = new List<string>();
        foreach (var (e, _) in type.Order) if (e != "Lightning") rest.Add(e);
        for (int i = 0; i < rest.Count; i++)
            Check.Eq(r.Order[i + 1].Element, rest[i], $"then the type order at {i + 1}");

        var alone = CreatureWeakness.Rank(0, Unknown, 0, "Fire");
        Check.Eq(alone?.Source, "learned", "learned with no type");
        Check.Eq(alone?.Order.Count, 1, "only the learned element");
        Check.Eq(alone?.Describe(), "Fire", "describe without a multiplier");
    }

    private static void SeedBeforeType()
    {
        var type = CreatureWeakness.Rank(0, OlthoiByKeyword, 0, null);
        var r = CreatureWeakness.Rank(0, OlthoiByKeyword, 0, null, new[] { "Electric", "fire", "Holy", "Fire" });
        Check.NotNull(r, "ranking with a seed");
        if (r == null || type == null) return;
        Check.Eq(r.Source, "UB seed, then Olthoi type", "source");
        Check.Eq(r.Order[0].Element, "Lightning", "seed order first (normalised)");
        Check.Eq(r.Order[1].Element, "Fire", "second seed element");
        Check.True(double.IsNaN(r.Order[1].Mult), "seed elements have no multiplier");
        Check.Eq(r.Order.Count, 8, "unknown names dropped, no element listed twice");
        var rest = new List<string>();
        foreach (var (e, _) in type.Order) if (e != "Lightning" && e != "Fire") rest.Add(e);
        for (int i = 0; i < rest.Count; i++)
            Check.Eq(r.Order[i + 2].Element, rest[i], $"then the type order at {i + 2}");

        var learned = CreatureWeakness.Rank(0, OlthoiByKeyword, 0, "Cold", new[] { "Fire" });
        Check.Eq(learned?.Source, "learned, then Olthoi type", "learned element beats the seed");
        Check.Eq(learned?.Order[0].Element, "Cold", "learned element first");

        var row = FirstServerRow();
        if (row is { } sr)
            Check.Eq(CreatureWeakness.Rank(sr.Wcid, sr.Name, 0, null, new[] { "Nether" })?.Source, "server data", "server table beats the seed");

        var alone = CreatureWeakness.Rank(0, Unknown, 0, null, new[] { "Acid" });
        Check.Eq(alone?.Source, "UB seed", "seed with no type");
        Check.Eq(alone?.Order.Count, 1, "only the seed element");
        Check.True(CreatureWeakness.Rank(0, Unknown, 0, null, new[] { "Holy" }) == null, "a seed with no element is ignored");
    }
}

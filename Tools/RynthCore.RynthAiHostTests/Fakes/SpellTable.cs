using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RynthCore.Plugin.RynthAi;

namespace RynthCore.RynthAiHostTests.Fakes;

/// <summary>
/// The plugin's embedded SpellData.txt, read independently of SpellDatabase so tests can check
/// the buff code against the data's own columns: id, name, category, duration (s) and power
/// (the tier's difficulty: 1/50/100/150/200/250 for tiers 1-6, 300 for 7, 400 for 8).
/// </summary>
internal static class SpellTable
{
    public sealed record Row(int Id, string Name, int Category, double Duration, int Power);

    private static List<Row>? _rows;

    public static IReadOnlyList<Row> Rows => _rows ??= Load();

    public static Row? ById(int id)
    {
        foreach (var r in Rows) if (r.Id == id) return r;
        return null;
    }

    public static IEnumerable<Row> Named(string name)
    {
        foreach (var r in Rows) if (r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) yield return r;
    }

    /// <summary>The power (difficulty) a spell of this tier has in the data.</summary>
    public static int PowerOfTier(int tier) => tier switch
    {
        1 => 1, 2 => 50, 3 => 100, 4 => 150, 5 => 200, 6 => 250, 7 => 300, 8 => 400, _ => -1,
    };

    private static List<Row> Load()
    {
        var rows = new List<Row>();
        using Stream? s = typeof(SpellDatabase).Assembly.GetManifestResourceStream("RynthCore.Plugin.RynthAi.Combat.SpellData.txt");
        if (s == null) return rows;
        using var reader = new StreamReader(s, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string[] f = line.Split('\t');
            if (f.Length < 7 || !int.TryParse(f[0].Trim(), out int id)) continue;
            int.TryParse(f[2].Trim(), out int cat);
            double.TryParse(f[5].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dur);
            int.TryParse(f[6].Trim(), out int power);
            rows.Add(new Row(id, f[1].Trim(), cat, dur, power));
        }
        return rows;
    }
}

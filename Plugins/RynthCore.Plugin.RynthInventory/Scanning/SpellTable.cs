using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace RynthCore.Plugin.RynthInventory.Scanning;

internal readonly record struct SpellInfo(string Name, double Duration, bool IsDebuff);

/// <summary>
/// Spell names and base durations from RynthAi's embedded spell table (SpellData.txt, linked
/// into this plugin): tab separated, id, name, category, family, words, duration (s, -1 none),
/// difficulty, then three flags, the second of which marks a debuff. Loaded on first use.
/// </summary>
internal static class SpellTable
{
    private static Dictionary<uint, SpellInfo>? _spells;

    public static bool TryGet(uint id, out SpellInfo info)
    {
        EnsureLoaded();
        return _spells!.TryGetValue(id, out info);
    }

    public static string Name(uint id) => TryGet(id, out SpellInfo s) ? s.Name : $"Spell {id}";

    public static int Count
    {
        get { EnsureLoaded(); return _spells!.Count; }
    }

    private static void EnsureLoaded()
    {
        if (_spells != null) return;
        var map = new Dictionary<uint, SpellInfo>();
        try
        {
            using Stream? s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RynthInventory.SpellData.txt");
            if (s != null)
            {
                using var reader = new StreamReader(s, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is string line)
                {
                    string[] c = line.Split('\t');
                    if (c.Length < 2 || !uint.TryParse(c[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) continue;
                    double duration = c.Length > 5 && double.TryParse(c[5].Trim(), NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double d) ? d : -1;
                    bool debuff = c.Length > 8 && c[8].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
                    map[id] = new SpellInfo(c[1].Trim(), duration, debuff);
                }
            }
        }
        catch
        {
            // A missing table only costs names ("Spell 1234").
        }
        _spells = map;
    }
}

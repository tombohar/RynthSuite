// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>A row of titles.csv: a character title and how to earn it.</summary>
internal sealed class Title
{
    public int Number;
    public string Name = "";
    public int TitleId;
    public string Type = "";
    public string Category = "";
    public int Level;
    public string Url = "";
    public string Hint = "";

    public bool Unavailable => Category == "Unavailable";
    public string Group => Type.Length > 0 ? Type : Category;
}

/// <summary>
/// The title list. Which titles the character holds comes from the server's title events
/// (GameEvent 0x0029 at login, 0x002B for each new title), which upstream reads with Decal's
/// network filter and RynthCore's engine serves through API v75 GetCharacterTitles
/// (OracleContext.EarnedTitles).
/// </summary>
internal static class TitleList
{
    public static List<Title> All { get; } = Load();

    private static List<Title> Load()
    {
        var list = new List<Title>();
        using StreamReader r = Csv.OpenEmbedded("titles.csv");
        r.ReadLine(); // Number,Name,Id,Type,Category,Level,Url,Hint
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            list.Add(new Title
            {
                Number = int.TryParse(Csv.Field(f, 0), out int n) ? n : 0,
                Name = Csv.Field(f, 1),
                TitleId = int.TryParse(Csv.Field(f, 2), out int id) ? id : 0,
                Type = Csv.Field(f, 3),
                Category = Csv.Field(f, 4),
                Level = int.TryParse(Csv.Field(f, 5), out int lv) ? lv : 0,
                Url = Csv.Field(f, 6),
                Hint = Csv.Field(f, 7),
            });
        }
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = a.Level.CompareTo(b.Level);
            return c != 0 ? c : a.Number.CompareTo(b.Number);
        });
        return list;
    }
}

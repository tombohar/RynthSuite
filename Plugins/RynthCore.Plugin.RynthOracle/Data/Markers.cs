// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// One of the 100 Dereth exploration markers (markers.csv). Each group of ten shares a quest
/// flag whose solve count is a bit field (explorationmarkersfoundingroupa..j, max 1023 on
/// Aelrynth's quest table); a marker is found when its bit is set (upstream's rule).
/// </summary>
internal sealed class Marker
{
    public int Number;
    public string Name = "";
    public string Location = "";
    public int BitMask;
    public string Flag = "";
    public string Hint = "";

    public bool IsFound(QuestFlagStore flags) =>
        flags.TryGet(Flag, out QuestFlag q) && (q.Solves & BitMask) == BitMask;

    public string Url => "https://acportalstorm.com/wiki/Dereth_Exploration/Markers_by_Efficiency#" + Location.Replace(" ", "_");
}

internal static class MarkerList
{
    /// <summary>The flag counting all markers found.</summary>
    public const string CountFlag = "explorationmarkersfound";

    public static List<Marker> All { get; } = Load();

    private static List<Marker> Load()
    {
        var list = new List<Marker>();
        using StreamReader r = Csv.OpenEmbedded("markers.csv");
        r.ReadLine(); // Number,Name,Location,BitMask,QuestFlag,Hint
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            list.Add(new Marker
            {
                Number = int.TryParse(Csv.Field(f, 0), out int n) ? n : 0,
                Name = Csv.Field(f, 1),
                Location = Csv.Field(f, 2),
                BitMask = int.TryParse(Csv.Field(f, 3), out int b) ? b : 0,
                Flag = Csv.Field(f, 4).ToLowerInvariant(),
                Hint = Csv.Field(f, 5),
            });
        }
        return list;
    }
}

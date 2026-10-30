using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// One Legendary Key quest (legendary.csv; list and notes from the "Arwic John Route" sheet).
/// The quest's own /myquests flags say whether it was ever done and when it can be run again:
/// the first flag is its completion stamp, any others only add their repeat timers (a key pickup
/// timer, say). John in Arwic counts 30 different ones (legendaryquestcounter_0913).
/// </summary>
internal sealed class LegendaryQuest
{
    public string Group = "";
    public string Name = "";
    /// <summary>The short name players use ("RTW", "HMD"), or "".</summary>
    public string CommonName = "";
    public int Luminance;
    public string[] Flags = Array.Empty<string>();
    public string Url = "";
    public string Note = "";
    public string Video = "";

    /// <summary>The short name when the sheet has one, else the quest name.</summary>
    public string Label => CommonName.Length > 0 ? $"{Name} ({CommonName})" : Name;

    /// <summary>Done at least once: any of its flags is on this character.</summary>
    public bool Have(QuestFlagStore flags)
    {
        foreach (string f in Flags)
            if (flags.TryGet(f, out _)) return true;
        return false;
    }

    /// <summary>
    /// Time until it can be run again: the longest wait among its flags that have a repeat timer;
    /// zero when every timer is up (or it has none). Null when it was never done.
    /// </summary>
    public TimeSpan? TimeLeft(QuestFlagStore flags, DateTime utcNow)
    {
        bool any = false;
        TimeSpan left = TimeSpan.Zero;
        foreach (string f in Flags)
        {
            if (!flags.TryGet(f, out QuestFlag q)) continue;
            any = true;
            if (q.RepeatTime <= TimeSpan.Zero || q.CompletedOnUtc == DateTime.MinValue) continue;
            TimeSpan d = q.NextAvailableTime(utcNow);
            if (d > left) left = d;
        }
        return any ? left : null;
    }

    /// <summary>"missing", "ready" or the time left ("14h 3m").</summary>
    public string Status(QuestFlagStore flags, DateTime utcNow)
    {
        TimeSpan? left = TimeLeft(flags, utcNow);
        if (left == null) return "missing";
        return left.Value.TotalSeconds > 0 ? TimeText.Friendly(left.Value) : "ready";
    }
}

internal static class LegendaryList
{
    /// <summary>John's count of different Legendary Key quests done (needs 30).</summary>
    public const string CounterFlag = "legendaryquestcounter_0913";
    /// <summary>Legendary Key quests John asks for.</summary>
    public const int JohnTarget = 30;

    public static List<LegendaryQuest> All { get; } = Load();

    /// <summary>The groups in list order (Graveyard, Rynthid, ...).</summary>
    public static List<string> Groups { get; } = BuildGroups();

    /// <summary>John's counter for this character; null when /myquests doesn't have it (not started).</summary>
    public static int? JohnCount(QuestFlagStore flags) =>
        flags.TryGet(CounterFlag, out QuestFlag q) ? q.Solves : null;

    private static List<LegendaryQuest> Load()
    {
        try
        {
            using StreamReader r = Csv.OpenEmbedded("legendary.csv");
            return Parse(r);
        }
        catch (Exception)
        {
            // A missing or unreadable list must not take the Quests page down with it.
            return new List<LegendaryQuest>();
        }
    }

    /// <summary>
    /// Reads a legendary.csv (a header row, then one quest a row). A row without a quest name is
    /// skipped; a missing column or a luminance that isn't a number reads as empty / 0, so one bad
    /// line never costs the rest of the list. Internal for the tests.
    /// </summary>
    internal static List<LegendaryQuest> Parse(TextReader r)
    {
        var list = new List<LegendaryQuest>();
        Dictionary<string, int> col = Csv.MapColumns(r.ReadLine() ?? "");
        int cGroup = Csv.Column(col, "Group"), cName = Csv.Column(col, "Quest"), cCommon = Csv.Column(col, "CommonName"),
            cLum = Csv.Column(col, "Luminance"), cFlags = Csv.Column(col, "Flags"), cUrl = Csv.Column(col, "Url"),
            cNote = Csv.Column(col, "Note"), cVideo = Csv.Column(col, "Video");
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            string name = Csv.Field(f, cName);
            if (name.Length == 0) continue;
            list.Add(new LegendaryQuest
            {
                Group = Csv.Field(f, cGroup),
                Name = name,
                CommonName = Csv.Field(f, cCommon),
                Luminance = int.TryParse(Csv.Field(f, cLum), out int lum) ? lum : 0,
                Flags = Csv.Field(f, cFlags).ToLowerInvariant()
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Url = Csv.Field(f, cUrl),
                Note = Csv.Field(f, cNote),
                Video = Csv.Field(f, cVideo),
            });
        }
        return list;
    }

    private static List<string> BuildGroups()
    {
        var groups = new List<string>();
        foreach (LegendaryQuest q in All)
            if (!groups.Contains(q.Group)) groups.Add(q.Group);
        return groups;
    }
}

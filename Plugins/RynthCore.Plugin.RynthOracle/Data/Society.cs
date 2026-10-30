// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// Society membership and rank from the rank properties (PropertyInt 287 Celestial Hand,
/// 288 Eldrytch Web, 289 Radiant Blood: the ribbon count, sent at login), and the daily ribbon
/// count from the societyribbonsperday* flags. Upstream's thresholds and rules.
/// </summary>
internal readonly struct SocietyStanding
{
    public readonly int Value;
    public readonly string SocietyName;

    public SocietyStanding(int celhan, int eldweb, int radblo)
    {
        if (celhan > 0) { Value = celhan; SocietyName = "Celestial Hand"; }
        else if (eldweb > 0) { Value = eldweb; SocietyName = "Eldrytch Web"; }
        else if (radblo > 0) { Value = radblo; SocietyName = "Radiant Blood"; }
        else { Value = 0; SocietyName = "None"; }
    }

    public bool IsMember => Value > 0;

    public string RankName =>
        Value > 1000 ? "Master" : Value > 600 ? "Lord" : Value > 300 ? "Knight" : Value > 100 ? "Adept" : Value > 0 ? "Initiate" : "None";

    public string NextRankName =>
        Value > 994 ? "" : Value > 600 ? "Master" : Value > 300 ? "Lord" : Value > 100 ? "Knight" : Value > 0 ? "Adept" : "";

    /// <summary>Ribbons per day by rank, up to Lord; a Master has no daily limit.</summary>
    public int DailyLimit => Value > 1000 ? 0 : Value > 600 ? 200 : Value > 300 ? 150 : Value > 100 ? 100 : Value > 0 ? 50 : 0;

    private int NextThreshold => Value > 994 ? 0 : Value > 600 ? 995 : Value > 300 ? 595 : Value > 100 ? 295 : Value > 0 ? 95 : 0;

    /// <summary>At the floor of a rank (1, 101, 301, 601, 1001): the +1 came from the promotion.</summary>
    public bool FreshlyPromoted => Value is 1 or 101 or 301 or 601 or 1001;

    /// <summary>Ribbons turned in (the rank value without a promotion's +1).</summary>
    public int Progress => FreshlyPromoted ? Value - 1 : Value;

    public int RibbonsToNextRank => NextThreshold == 0 ? 0 : NextThreshold - Progress;

    /// <summary>Displayed cap for the rank bar (ribbons go in five at a time: 95/295/595/995).</summary>
    public int RankMax => Value > 1000 ? 1000 : Value > 600 ? 995 : Value > 300 ? 595 : Value > 100 ? 295 : Value > 0 ? 95 : 0;

    public bool HasReachedRank(string rank) => rank switch
    {
        "Initiate" => Value > 0,
        "Adept" => Value > 100,
        "Knight" => Value > 300,
        "Lord" => Value > 600,
        "Master" => Value > 1000,
        _ => false,
    };

    /// <summary>
    /// Ribbons turned in today. The counter only resets on the next turn-in, so once the 20-hour
    /// timer is ready it's stale and today's count is 0.
    /// </summary>
    public int RibbonsToday(QuestFlagStore flags, DateTime utcNow)
    {
        if (FreshlyPromoted) return 0;
        if (!flags.TryGet("societyribbonsperdaytimer", out QuestFlag timer) || timer.Ready(utcNow)) return 0;
        return flags.TryGet("societyribbonsperdaycounter", out QuestFlag counter) ? counter.Solves : 0;
    }
}

/// <summary>A row of society.csv: a quest, a "Rank: ..." heading, or "Blank" (a gap).</summary>
internal sealed class SocietyQuest
{
    public string Name = "";
    public string Flag = "";
    public string Area = "";
    public string Url = "";
    public string Hint = "";

    public bool IsBlank => Name == "Blank";
    public bool IsHeading => Name.StartsWith("Rank:", StringComparison.Ordinal);
    public bool IsRankTest => Name is "Adept Test" or "Knight Test" or "Lord Test" or "Master Test";

    private string RankTestTarget => Name switch
    {
        "Adept Test" => "Adept",
        "Knight Test" => "Knight",
        "Lord Test" => "Lord",
        "Master Test" => "Master",
        _ => "",
    };

    private static bool IsMembershipFlag(string flag) =>
        flag is "celestialhandmember" or "eldrytchwebmember" or "radiantbloodmember";

    /// <summary>A rank test once the rank is reached; membership by the rank property; one-time by a solve; repeatables while on cooldown.</summary>
    public bool IsComplete(SocietyStanding s, QuestFlagStore flags, DateTime utcNow)
    {
        if (IsRankTest) return s.HasReachedRank(RankTestTarget);
        if (IsMembershipFlag(Flag))
            return s.IsMember && Name.StartsWith(s.SocietyName, StringComparison.Ordinal);
        if (!flags.TryGet(Flag, out QuestFlag q)) return false;
        if (q.RepeatTime == TimeSpan.Zero) return q.Solves > 0;
        return !q.Ready(utcNow);
    }

    public string Status(SocietyStanding s, QuestFlagStore flags, DateTime utcNow)
    {
        if (IsRankTest || IsMembershipFlag(Flag)) return IsComplete(s, flags, utcNow) ? "completed" : "";
        if (!flags.TryGet(Flag, out QuestFlag q)) return "ready";
        if (q.RepeatTime == TimeSpan.Zero) return q.Solves > 0 ? "completed" : "ready";
        return q.NextAvailable(utcNow);
    }
}

internal static class SocietyQuestList
{
    public static List<SocietyQuest> All { get; } = Load();

    /// <summary>In a society, the precursor and the other societies' initiations are hidden (upstream's rule).</summary>
    public static IEnumerable<SocietyQuest> Visible(SocietyStanding s)
    {
        if (!s.IsMember) return All;
        return All.Where(q => q.Name != "Investigating the Societies"
            && (!q.Name.EndsWith(" Initiation", StringComparison.Ordinal) || q.Name.StartsWith(s.SocietyName, StringComparison.Ordinal)));
    }

    private static List<SocietyQuest> Load()
    {
        var list = new List<SocietyQuest>();
        using StreamReader r = Csv.OpenEmbedded("society.csv");
        r.ReadLine(); // Name,QuestFlag,Area,Url,Hint
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            list.Add(new SocietyQuest
            {
                Name = Csv.Field(f, 0),
                Flag = Csv.Field(f, 1).ToLowerInvariant(),
                Area = Csv.Field(f, 2),
                Url = Csv.Field(f, 3),
                Hint = Csv.Field(f, 4),
            });
        }
        return list;
    }
}

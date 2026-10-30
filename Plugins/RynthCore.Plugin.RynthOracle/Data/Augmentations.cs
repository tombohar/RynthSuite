// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// One row of augmentations.csv: an XP augmentation (bought with experience) or a luminance
/// aura. Id is the PropertyInt that counts it, or a negative marker for the special rows
/// (upstream's scheme: -1 innate attributes, -2 innate resistances, -3 Asheron's Lesser
/// Benediction, -4..-8 luminance seers, -333..-336 the Falatacot/Dericost auras); 0 = a heading.
/// </summary>
internal sealed class Augmentation
{
    public string Name = "";
    public string Category = "";
    public int Id;
    public string Effect = "";
    public string Cost = "";
    public int TimesTotal;
    public string Flag = "";
    public string Url = "";
    public string Hint = "";

    private static readonly int[] InnateAttributeIds = { 218, 219, 220, 221, 222, 223 };
    private static readonly int[] InnateResistanceIds = { 240, 241, 242, 243, 244, 245, 246 };

    public bool IsHeading => Id == 0;
    public bool IsXp => Category == "XP";
    public bool IsLuminance => Category == "Luminance";

    private bool IsInnateAttributes => Id == -1;
    private bool IsInnateResistances => Id == -2;
    private bool IsBenediction => Id == -3;
    private bool IsSeer => Id <= -4 && Id >= -8;
    private bool IsInnateAttribute => InnateAttributeIds.Contains(Id);
    private bool IsInnateResistance => InnateResistanceIds.Contains(Id);
    private bool IsLumSpecialization => Id <= -333 && Id >= -336;

    /// <summary>The seers and the Falatacot/Dericost auras need a quest flag first (any solve).</summary>
    public bool IsQuestComplete(QuestFlagStore flags) =>
        Flag.Length > 0 && flags.TryGet(Flag, out QuestFlag q) && q.Solves > 0;

    public int Times(Func<uint, int> prop, QuestFlagStore flags)
    {
        if (Flag.Length > 0 && !IsQuestComplete(flags)) return 0;
        if (IsInnateAttributes) return InnateAttributeIds.Sum(id => prop((uint)id));
        if (IsInnateResistances) return InnateResistanceIds.Sum(id => prop((uint)id));
        if (IsBenediction) return 0; // upstream counts the item in inventory; not ported (see IsUnknown)
        if (IsLumSpecialization) return Math.Max(prop((uint)Math.Abs(Id)) - 5, 0);
        if (Id <= 0) return 0;
        return Math.Min(prop((uint)Id), TimesTotal);
    }

    /// <summary>Rows RynthOracle can't read (Asheron's Lesser Benediction is an item, not a property).</summary>
    public bool IsUnknown => IsBenediction;

    public bool IsComplete(Func<uint, int> prop, QuestFlagStore flags)
    {
        if (TimesTotal == 0) return false;
        if (IsInnateAttribute) return InnateAttributeIds.Sum(id => prop((uint)id)) >= 10;
        if (IsInnateResistance) return InnateResistanceIds.Sum(id => prop((uint)id)) >= 2;
        if (IsSeer) return IsQuestComplete(flags);
        return Times(prop, flags) >= TimesTotal;
    }

    public string Text(Func<uint, int> prop, QuestFlagStore flags)
    {
        if (IsSeer) return "";
        int t = Times(prop, flags);
        if (TimesTotal == 0 || IsInnateAttribute || IsInnateResistance) return t.ToString();
        return $"{t}/{TimesTotal}";
    }

    /// <summary>The next aura's cost in luminance (upstream's formula); blank when maxed or locked.</summary>
    public string CostText(Func<uint, int> prop, QuestFlagStore flags)
    {
        if (IsXp) return Cost;
        if (Id == 0 || IsSeer) return "";
        int t = Times(prop, flags);
        if (t >= TimesTotal) return "";
        if (Flag.Length > 0 && !IsQuestComplete(flags)) return "";
        if (Id == 365) return (100 + t * 100) + "k";                     // World
        if (Id == 344 || IsLumSpecialization) return (350 + t * 50) + "k"; // Specialization
        return (100 + t * 50) + "k";
    }

    /// <summary>Luminance spent on this aura so far.</summary>
    public long LuminanceSpent(Func<uint, int> prop, QuestFlagStore flags)
    {
        long t = Times(prop, flags);
        if (t == 0) return 0;
        if (Id == 365) return 100_000 * t * (t + 1) / 2;
        if (Id == 344 || IsLumSpecialization) return t * (700_000 + (t - 1) * 50_000) / 2;
        return t * (200_000 + (t - 1) * 50_000) / 2;
    }
}

/// <summary>An aug-gem quest from augquests.csv (its timer is the flag's).</summary>
internal sealed class AugQuest
{
    public string Name = "";
    public string Flag = "";
    public string Url = "";
    public string Hint = "";
}

internal static class AugmentationList
{
    /// <summary>All luminance auras maxed (upstream's figure).</summary>
    public const long TotalLuminance = 19_000_000;

    public static List<Augmentation> All { get; } = Load();
    public static List<AugQuest> Quests { get; } = LoadQuests();

    private static List<Augmentation> Load()
    {
        var list = new List<Augmentation>();
        using StreamReader r = Csv.OpenEmbedded("augmentations.csv");
        r.ReadLine(); // Name,Category,Id,Effect,Cost,Times,Flag,Url,Hint
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            list.Add(new Augmentation
            {
                Name = Csv.Field(f, 0),
                Category = Csv.Field(f, 1),
                Id = int.TryParse(Csv.Field(f, 2), out int id) ? id : 0,
                Effect = Csv.Field(f, 3),
                Cost = Csv.Field(f, 4),
                TimesTotal = int.TryParse(Csv.Field(f, 5), out int t) ? t : 0,
                Flag = Csv.Field(f, 6).ToLowerInvariant(),
                Url = Csv.Field(f, 7),
                Hint = Csv.Field(f, 8),
            });
        }
        return list;
    }

    private static List<AugQuest> LoadQuests()
    {
        var list = new List<AugQuest>();
        using StreamReader r = Csv.OpenEmbedded("augquests.csv");
        r.ReadLine(); // Name,QuestFlag,Url,Hint
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            list.Add(new AugQuest
            {
                Name = Csv.Field(f, 0),
                Flag = Csv.Field(f, 1).ToLowerInvariant(),
                Url = Csv.Field(f, 2),
                Hint = Csv.Field(f, 3),
            });
        }
        return list;
    }

    public static long TotalLuminanceSpent(Func<uint, int> prop, QuestFlagStore flags) =>
        All.Where(a => a.IsLuminance).Sum(a => a.LuminanceSpent(prop, flags));

    public static Func<uint, int> PlayerProps(RynthCoreHost host, CharacterSnapshot c) => id => c.Int(host, id);
}

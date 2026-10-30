using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// What one quest pays this character, as the player recorded it (UtilityBelt ILT's
/// QuestRewardEntry, Lib/Quests/QuestRewardsFile.cs). XP and luminance are percentages of a
/// level's worth, so they are kept with the level they were noted at.
/// </summary>
internal sealed class QuestReward
{
    /// <summary>The largest percentage accepted (UB's clamp).</summary>
    public const double MaxPercent = 1000;

    public double XpPercent;
    public double LumPercent;
    /// <summary>The character level the percentages were noted at; 0 = not recorded.</summary>
    public int RecordedLevel;
    public int Coins;
    public int Trinkets;
    public int Medallions;
    public int YellowPowder;
    public int RedPowder;
    public int BluePowder;
    public int Mmd;

    /// <summary>Nothing recorded (an empty entry is dropped instead of saved).</summary>
    public bool IsEmpty =>
        XpPercent <= 0 && LumPercent <= 0 && Coins <= 0 && Trinkets <= 0 && Medallions <= 0
        && YellowPowder <= 0 && RedPowder <= 0 && BluePowder <= 0 && Mmd <= 0;

    public QuestReward Clone() => (QuestReward)MemberwiseClone();

    /// <summary>
    /// The compact column text, UB's format: "1.5%XP 2%Lum 3C 1T 2M 1Y 1R 1B 2D L275". Zero fields
    /// are left out; " L&lt;level&gt;" follows when XP or luminance is set and the level is known.
    /// </summary>
    public string ShortSummary()
    {
        var parts = new List<string>(10);
        if (XpPercent > 0) parts.Add(Percent(XpPercent) + "%XP");
        if (LumPercent > 0) parts.Add(Percent(LumPercent) + "%Lum");
        if (Coins > 0) parts.Add(Coins.ToString(CultureInfo.InvariantCulture) + "C");
        if (Trinkets > 0) parts.Add(Trinkets.ToString(CultureInfo.InvariantCulture) + "T");
        if (Medallions > 0) parts.Add(Medallions.ToString(CultureInfo.InvariantCulture) + "M");
        if (YellowPowder > 0) parts.Add(YellowPowder.ToString(CultureInfo.InvariantCulture) + "Y");
        if (RedPowder > 0) parts.Add(RedPowder.ToString(CultureInfo.InvariantCulture) + "R");
        if (BluePowder > 0) parts.Add(BluePowder.ToString(CultureInfo.InvariantCulture) + "B");
        if (Mmd > 0) parts.Add(Mmd.ToString(CultureInfo.InvariantCulture) + "D");
        if ((XpPercent > 0 || LumPercent > 0) && RecordedLevel > 0) parts.Add("L" + RecordedLevel.ToString(CultureInfo.InvariantCulture));
        return string.Join(" ", parts);
    }

    /// <summary>The long form for tooltips and chat: "1.5% XP, 2% luminance (at level 275), 3 coins, ...".</summary>
    public string LongSummary()
    {
        var parts = new List<string>(10);
        if (XpPercent > 0) parts.Add(Percent(XpPercent) + "% XP");
        if (LumPercent > 0) parts.Add(Percent(LumPercent) + "% luminance");
        if ((XpPercent > 0 || LumPercent > 0) && RecordedLevel > 0) parts[parts.Count - 1] += $" (at level {RecordedLevel})";
        Add(parts, Coins, "coin");
        Add(parts, Trinkets, "trinket");
        Add(parts, Medallions, "medallion");
        Add(parts, YellowPowder, "yellow powder");
        Add(parts, RedPowder, "red powder");
        Add(parts, BluePowder, "blue powder");
        Add(parts, Mmd, "MMD");
        return parts.Count == 0 ? "no rewards recorded" : string.Join(", ", parts);

        static void Add(List<string> list, int n, string what)
        {
            if (n <= 0) return;
            bool noPlural = what.EndsWith("powder", StringComparison.Ordinal) || what == "MMD";
            list.Add($"{n} {what}{(n == 1 || noPlural ? "" : "s")}");
        }
    }

    /// <summary>UB's percent format: one decimal from 10 up, two from 1 up, three below 1.</summary>
    public static string Percent(double v) =>
        v.ToString(v >= 10 ? "0.#" : v >= 1 ? "0.##" : "0.###", CultureInfo.InvariantCulture);

    /// <summary>A typed percentage ("1.5", "1,5", "2%"); blank = 0. Clamped to 0..<see cref="MaxPercent"/>. False when it isn't a number.</summary>
    public static bool TryParsePercent(string? text, out double value)
    {
        value = 0;
        string t = (text ?? "").Trim().TrimEnd('%').Trim().Replace(',', '.');
        if (t.Length == 0) return true;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) return false;
        value = Math.Clamp(v, 0, MaxPercent);
        return true;
    }

    /// <summary>A typed count; blank = 0, negatives become 0. False when it isn't a whole number.</summary>
    public static bool TryParseCount(string? text, out int value)
    {
        value = 0;
        string t = (text ?? "").Trim().Replace(",", "");
        if (t.Length == 0) return true;
        if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) return false;
        value = (int)Math.Clamp(v, 0, int.MaxValue);
        return true;
    }
}

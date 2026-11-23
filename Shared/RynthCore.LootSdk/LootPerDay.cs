using System;

namespace RynthCore.Loot;

/// <summary>
/// "Keep # per day" (2026-11-22): a Keep # rule that counts what the character picked up in a
/// rolling window (24 hours by default) instead of what the pack holds, for items with a pick-up
/// timer that get used or sold (treasure maps, two a day). Each pickup frees its place the given
/// number of hours after it was made. The counts are RynthAi's, per character and server.
///
/// Where the option is saved:
/// - .json: the rule's PerDayHours (left out when 0, so older files are unchanged).
/// - .utl: an extra block at the end of the file, after any SalvageCombine block:
///   <code>
///   RynthKeepPerDay
///   &lt;length: the characters of the lines below, 2 per line end, as VTank counts them&gt;
///   1                  (block format)
///   &lt;n&gt;
///   &lt;rule index, 0-based&gt;   } n times
///   &lt;rule name&gt;            }
///   &lt;hours&gt;                }
///   </code>
///   VTank (VTClassic's UTLFileExtraBlockManager) reads "type, length, data" blocks after the
///   rules and skips a type it doesn't know, so the profile still loads there and the rule is a
///   plain Keep # of the same count. VTank keeps only its own blocks when it saves, so a profile
///   saved in VTank loses the option (its rules stay as they are). An entry whose rule isn't a
///   Keep # of that name at that index is not applied.
/// </summary>
public static class LootPerDay
{
    public const int DefaultHours = 24, MinHours = 1, MaxHours = 168;

    /// <summary>The .utl block's type line.</summary>
    public const string UtlBlockName = "RynthKeepPerDay";

    /// <summary>The block's format line.</summary>
    public const string UtlBlockFormat = "1";

    /// <summary>A window in hours: 0 or less is off (0), else 1 to 168.</summary>
    public static int Clamp(int hours) => hours <= 0 ? 0 : Math.Clamp(hours, MinHours, MaxHours);

    /// <summary>
    /// What a rule's pickups are counted under: its name (trimmed, any case), or "#N" (1-based)
    /// for an unnamed rule. Renaming a rule starts its count again.
    /// </summary>
    public static string Key(string? name, int index)
    {
        string n = (name ?? string.Empty).Trim();
        return n.Length > 0 ? n : "#" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>"24 h", "2 days" (48), "1 week" (168): the window in words.</summary>
    public static string WindowText(int hours)
    {
        if (hours == 24) return "24 h";
        if (hours == 168) return "1 week";
        if (hours % 24 == 0) return (hours / 24).ToString(System.Globalization.CultureInfo.InvariantCulture) + " days";
        return hours.ToString(System.Globalization.CultureInfo.InvariantCulture) + " h";
    }
}

// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Globalization;

namespace RynthCore.Plugin.RynthOracle.Data;

internal static class TimeText
{
    /// <summary>
    /// "2d 5h 0m", "12m 4s". Seconds only inside the last hour; once a unit shows, every smaller
    /// one follows even at zero, so a column doesn't change shape as it counts down (upstream's rule).
    /// </summary>
    public static string Friendly(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        bool showSeconds = d.TotalHours < 1;
        string o = "";
        if (d.Days > 0) o += d.Days + "d ";
        if (o.Length > 0 || d.Hours > 0) o += d.Hours + "h ";
        if (o.Length > 0 || d.Minutes > 0) o += d.Minutes + "m ";
        if (showSeconds && (o.Length > 0 || d.Seconds > 0)) o += d.Seconds + "s ";
        return o.Length == 0 ? "0s" : o.Trim();
    }

    /// <summary>"1:02:03" (hours unbounded), for buff timers.</summary>
    public static string Clock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    public static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

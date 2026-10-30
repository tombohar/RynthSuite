using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// /rnav's lines for the words that used to answer nothing (2026-10-28: every command answers,
/// Plugins/Shared/CommandReply.cs): "recent" without "clear", "arrive" with no or a wrong number,
/// a word /rnav doesn't know. Pure, so RynthNav.RouteTests checks them.
/// </summary>
internal static class NavReplies
{
    public const double ArriveMin = 2, ArriveMax = 200;

    /// <summary>"/rnav arrive &lt;yards&gt;": the distance when it is one the arrow takes.</summary>
    public static bool TryArrive(string rest, out double yards) =>
        double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out yards) && yards >= ArriveMin && yards <= ArriveMax;

    public static string Arrived(double yards) => $"the arrow counts you as arrived within {yards:F0} yd";

    /// <summary>"/rnav arrive" alone or with a wrong number: the value now and the usage.</summary>
    public static string ArriveUsage(string rest, double now) =>
        (rest.Trim().Length == 0 ? "" : $"'{rest.Trim()}' isn't {ArriveMin:F0}-{ArriveMax:F0} yd. ")
        + $"{Arrived(now)} — /rnav arrive <yards {ArriveMin:F0}-{ArriveMax:F0}>";

    /// <summary>"/rnav recent": the recent places, newest first.</summary>
    public static string Recent(IReadOnlyList<SavedPlace> recent) => recent.Count == 0
        ? "no recent places (arrow, route and go targets land here)"
        : $"recent places ({recent.Count}): {string.Join(", ", recent.Select(p => p.Name))} — /rnav recent clear forgets them";

    /// <summary>A word /rnav doesn't know.</summary>
    public static string Unknown(string word) => $"unknown '{word}' — /rnav help";
}

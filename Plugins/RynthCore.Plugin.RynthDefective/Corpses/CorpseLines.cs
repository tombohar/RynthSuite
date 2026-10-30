// ============================================================================
//  RynthDefective - Corpses/CorpseLines.cs
//  The server's two lines about a death's corpse (ACE, Creature_Death):
//    "Your corpse is located at (12.3N, 45.6E)."   outdoor deaths that dropped something
//    "You have retained all your items. You do not need to recover your corpse!"
//  Pure.
// ============================================================================

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthDefective.Corpses;

internal static class CorpseLines
{
    private static readonly Regex Located = new(
        @"Your corpse is located at \(\s*(\d+(?:\.\d+)?)\s*([NS])\s*,\s*(\d+(?:\.\d+)?)\s*([EW])\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsCorpseLine(string text) => Located.IsMatch(text) || IsNothingDropped(text);

    public static bool IsNothingDropped(string text) =>
        text.IndexOf("You do not need to recover your corpse", StringComparison.OrdinalIgnoreCase) >= 0;

    public static bool TryLocated(string text, out double ns, out double ew)
    {
        ns = ew = 0;
        Match m = Located.Match(text);
        if (!m.Success) return false;
        ns = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * (m.Groups[2].Value.Equals("S", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
        ew = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) * (m.Groups[4].Value.Equals("W", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
        return true;
    }
}

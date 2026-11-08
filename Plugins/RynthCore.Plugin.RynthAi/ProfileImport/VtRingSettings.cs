using System;
using System.Collections.Generic;
using System.Globalization;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.ProfileImport;

/// <summary>
/// VTank's two ring options in a text .usd profile, MinimumRingTargets and RingDistance, as
/// RynthAi's Min Ring Targets and Ring Range (2026-11-06: an imported profile brought the
/// monsters' Ring ticks but not the ring options that go with them, so a player who rang at 2
/// targets in VTank waited for RynthAi's default 4). Applied only by an import the player starts,
/// never on a load. A value the .usd doesn't have leaves the setting alone (VTank writes only the
/// options changed from its defaults).
///
/// The options are rows of four lines after the tables: "s", the option name, its type tag
/// ("i", "d", "b"), the value. RingDistance is in VTank's distance unit, a fraction of a landblock
/// (0.0208333 is 5 / 240), converted as every VTank distance in RynthAi is (MetaManager's
/// VtDistanceToYards: x240, whole yards, the same value a meta's RingDistance option gives), then
/// held to the settings window's range.
/// </summary>
internal sealed class VtRingSettings
{
    // The settings window's and the /ra set ranges (RynthAiPlugin's setting limits, the engine's Int rows).
    internal const int MinTargetsLow = 1, MinTargetsHigh = 20;
    internal const int RangeLow = 1, RangeHigh = 50;

    /// <summary>Min Ring Targets from the profile, or null when it has none.</summary>
    public int? MinRingTargets { get; private set; }
    /// <summary>Ring Range (yards) from the profile, or null when it has none.</summary>
    public int? RingRange { get; private set; }
    /// <summary>The profile's raw RingDistance (landblock units), for the notes.</summary>
    public double? RawRingDistance { get; private set; }
    /// <summary>A value was held to the allowed range ("Ring Range 60 -> 50").</summary>
    public List<string> Clamped { get; } = new();

    public bool Any => MinRingTargets != null || RingRange != null;

    /// <summary>Reads the two options from a .usd's lines (the first of each).</summary>
    public static VtRingSettings Read(IReadOnlyList<string> lines)
    {
        var r = new VtRingSettings();
        for (int i = 1; i + 2 < lines.Count; i++)
        {
            if (!string.Equals(lines[i - 1].Trim(), "s", StringComparison.Ordinal)) continue;
            string name = lines[i].Trim();
            string value = lines[i + 2].Trim();
            if (r.MinRingTargets == null && name.Equals("MinimumRingTargets", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                int v = (int)Math.Round(n);
                int c = Math.Clamp(v, MinTargetsLow, MinTargetsHigh);
                if (c != v) r.Clamped.Add($"Min Ring Targets {v} -> {c}");
                r.MinRingTargets = c;
            }
            else if (r.RingRange == null && name.Equals("RingDistance", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                string yards = Meta.MetaManager.VtDistanceToYards(value, wholeYards: true);
                if (!int.TryParse(yards, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) continue;
                int c = Math.Clamp(y, RangeLow, RangeHigh);
                if (c != y) r.Clamped.Add($"Ring Range {y} -> {c}");
                r.RingRange = c;
                r.RawRingDistance = d;
            }
        }
        return r;
    }

    /// <summary>"Min Ring Targets 2, Ring Range 5 (from the profile)"; "" when the profile has neither.</summary>
    public string Describe()
    {
        if (!Any) return string.Empty;
        var parts = new List<string>(2);
        if (MinRingTargets is int t) parts.Add($"Min Ring Targets {t}");
        if (RingRange is int y) parts.Add($"Ring Range {y}");
        return string.Join(", ", parts) + " (from the profile)";
    }

    /// <summary>What applying would change, against <paramref name="s"/>: "Min Ring Targets 2 (now 4), Ring Range 5 (unchanged)".</summary>
    public string DescribeAgainst(LegacyUiSettings s)
    {
        if (!Any) return string.Empty;
        var parts = new List<string>(2);
        if (MinRingTargets is int t) parts.Add($"Min Ring Targets {t} ({(t == s.MinRingTargets ? "unchanged" : $"now {s.MinRingTargets}")})");
        if (RingRange is int y) parts.Add($"Ring Range {y} ({(y == s.RingRange ? "unchanged" : $"now {s.RingRange}")})");
        return string.Join(", ", parts);
    }

    /// <summary>Writes the values the profile has into <paramref name="s"/>; the others are left alone.</summary>
    public void ApplyTo(LegacyUiSettings s)
    {
        if (MinRingTargets is int t) s.MinRingTargets = t;
        if (RingRange is int y) s.RingRange = y;
    }

    public bool SameAs(VtRingSettings o) => MinRingTargets == o.MinRingTargets && RingRange == o.RingRange;
}

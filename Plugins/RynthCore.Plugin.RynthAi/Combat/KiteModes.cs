using System;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// ============================================================================
//  Per-monster kite / pull-away (2026-10-16): MonsterRule.Kite.
//
//    Default    the DEFAULT line's choice; on the DEFAULT line, Off (fight in place)
//    Off        engage and kill where the mob stands
//    PullAway   after a brief tag, run away until the mob has been pulled
//               KitePullMeters from its lock-spot origin, then kill it alone
//
//  Resolution matches AttackPowerModes: a rule's own value wins; "Default" falls
//  through to the DEFAULT row; blank/unknown is Default (→ Off via the DEFAULT row).
// ============================================================================

/// <summary>Names and resolution of <see cref="MonsterRule.Kite"/>.</summary>
public static class KiteModes
{
    public const string Default = "Default";
    public const string Off = "Off";
    public const string PullAway = "PullAway";

    /// <summary>Every choice, in the order the editors list them.</summary>
    public static readonly string[] All = { Default, Off, PullAway };

    /// <summary>Combo labels (plugin Advanced + engine Monsters/Damage panels).</summary>
    public static readonly string[] DisplayNames = { "Default", "Off", "Pull away" };

    /// <summary>A saved value as one of <see cref="All"/>; blank or unknown is Default.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        string v = value.Trim();
        foreach (string m in All)
            if (m.Equals(v, StringComparison.OrdinalIgnoreCase)) return m;
        // Hand-typed / UI spellings: "Pull away", "pull-away", "Kite".
        string squeezed = v.Replace(" ", "").Replace("-", "").Replace("_", "");
        if (squeezed.Equals("PullAway", StringComparison.OrdinalIgnoreCase)
            || squeezed.Equals("Kite", StringComparison.OrdinalIgnoreCase)
            || squeezed.Equals("Pull", StringComparison.OrdinalIgnoreCase))
            return PullAway;
        if (squeezed.Equals("None", StringComparison.OrdinalIgnoreCase)
            || squeezed.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            return Off;
        return Default;
    }

    /// <summary>
    /// The mode that applies to a monster: its own rule's, else the DEFAULT line's.
    /// On the DEFAULT line, Default resolves to Off (safe: no pull until the player opts in).
    /// </summary>
    public static string Effective(MonsterRule? rule, MonsterRule? defaultRule)
    {
        string own = Normalize(rule?.Kite);
        if (own != Default) return own;
        // The DEFAULT row itself: Default means Off.
        if (rule != null && defaultRule != null
            && ReferenceEquals(rule, defaultRule))
            return Off;
        if (rule != null && string.Equals(rule.Name, "Default", StringComparison.OrdinalIgnoreCase))
            return Off;
        string fromDefault = Normalize(defaultRule?.Kite);
        return fromDefault == Default ? Off : fromDefault;
    }

    /// <summary>True when combat should pull this monster before finishing it.</summary>
    public static bool WantsPullAway(MonsterRule? rule, MonsterRule? defaultRule)
        => Effective(rule, defaultRule) == PullAway;

    /// <summary>Label for logs and tooltips.</summary>
    public static string Name(string? value) => Normalize(value) switch
    {
        PullAway => "Pull away",
        Off      => "Off",
        _        => "Default",
    };
}

using System;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

// ============================================================================
//  Per-monster attack power (2026-10-12): MonsterRule.AttackPower.
//
//    Default             the DEFAULT line's choice; on the DEFAULT line, the global
//                        Melee/Missile Power settings (Auto = 100%, or 80% with Use
//                        Recklessness and the skill trained)
//    High                100% every swing or shot
//    Reckless            80%: inside the band where Recklessness adds damage (ACE:
//                        10%-90% of the bar, retail wiki: 20%-80%, so 80% is in both)
//    Variable            a new random power each attack, Variable Power Min..Max
//    Reckless + Variable random each attack, inside the Recklessness band (20%-80%)
//                        and the Variable range
//
//  Recklessness only adds damage when the skill is trained or specialized; with it
//  untrained a reckless choice falls back to High (or Variable), since a short bar
//  would only cost damage. Magic doesn't use attack power.
// ============================================================================

/// <summary>Names and resolution of <see cref="MonsterRule.AttackPower"/>.</summary>
public static class AttackPowerModes
{
    public const string Default = "Default";
    public const string High = "High";
    public const string Reckless = "Reckless";
    public const string Variable = "Variable";
    public const string RecklessVariable = "Reckless + Variable";

    /// <summary>Every choice, in the order the editors list them.</summary>
    public static readonly string[] All = { Default, High, Reckless, Variable, RecklessVariable };

    /// <summary>The bar fraction a fixed reckless attack uses.</summary>
    public const float RecklessPower = 0.8f;
    /// <summary>The Recklessness band used for a varied reckless attack (retail wiki: 20%-80%).</summary>
    public const int RecklessBandMinPct = 20;
    public const int RecklessBandMaxPct = 80;

    /// <summary>A saved value as one of <see cref="All"/>; blank or unknown is Default.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        string v = value.Trim();
        foreach (string m in All)
            if (m.Equals(v, StringComparison.OrdinalIgnoreCase)) return m;
        // Hand-typed and meta spellings: "RecklessVariable", "reckless+variable".
        string squeezed = v.Replace(" ", "").Replace("+", "");
        if (squeezed.Equals("RecklessVariable", StringComparison.OrdinalIgnoreCase)) return RecklessVariable;
        return Default;
    }

    /// <summary>
    /// The mode that applies to a monster: its own rule's, else the DEFAULT line's, else
    /// Default (the global settings). <paramref name="defaultRule"/> may be the same object.
    /// </summary>
    public static string Effective(MonsterRule? rule, MonsterRule? defaultRule)
    {
        string own = Normalize(rule?.AttackPower);
        if (own != Default) return own;
        return Normalize(defaultRule?.AttackPower);
    }

    /// <summary>
    /// The power bar fraction (0..1) for one attack.
    /// <paramref name="globalPct"/> is the global Melee/Missile Power (-1 = Auto);
    /// <paramref name="random01"/> returns a value in [0, 1).
    /// </summary>
    public static float Resolve(string mode, int globalPct, bool useRecklessness, bool recklessTrained,
        int variableMinPct, int variableMaxPct, Func<double> random01)
    {
        switch (Normalize(mode))
        {
            case High:
                return 1.0f;

            case Reckless:
                return recklessTrained ? RecklessPower : 1.0f;

            case Variable:
            {
                var (lo, hi) = VariableRange(variableMinPct, variableMaxPct);
                return Pick(lo, hi, random01);
            }

            case RecklessVariable:
            {
                var (lo, hi) = VariableRange(variableMinPct, variableMaxPct);
                if (!recklessTrained) return Pick(lo, hi, random01);
                int rlo = Math.Max(lo, RecklessBandMinPct);
                int rhi = Math.Min(hi, RecklessBandMaxPct);
                // The Variable range doesn't overlap the band (e.g. 85-100): the whole band.
                if (rlo > rhi) { rlo = RecklessBandMinPct; rhi = RecklessBandMaxPct; }
                return Pick(rlo, rhi, random01);
            }

            default:
                if (globalPct < 0)
                    return useRecklessness && recklessTrained ? RecklessPower : 1.0f;
                return Math.Clamp(globalPct, 0, 100) / 100f;
        }
    }

    /// <summary>The Variable range in percent, clamped to 0..100 and put in order.</summary>
    public static (int Lo, int Hi) VariableRange(int minPct, int maxPct)
    {
        int lo = Math.Clamp(minPct, 0, 100), hi = Math.Clamp(maxPct, 0, 100);
        return lo <= hi ? (lo, hi) : (hi, lo);
    }

    private static float Pick(int loPct, int hiPct, Func<double> random01)
    {
        double r = Math.Clamp(random01(), 0.0, 1.0);
        double pct = loPct + (hiPct - loPct) * r;
        return (float)(Math.Round(pct) / 100.0);
    }
}

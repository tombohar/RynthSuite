using System.Text.RegularExpressions;
using RynthCore.Loot;

namespace RynthCore.Plugin.RynthAi.Loot;

/// <summary>
/// Evaluates SDK LootCondition/LootRule/LootProfile data against live AC WorldObjects.
/// Kept separate from the SDK so the editor has zero AC dependencies.
/// </summary>
public static class LootEvaluator
{
    private static readonly int[] RatingKeys = { 370, 371, 372, 373, 374, 375, 376, 379 };

    public static bool Evaluate(LootCondition condition, WorldObject item, CharacterSkills? skills)
        => condition switch
        {
            ObjectClassCondition c      => item.ObjectClass == (AcObjectClass)(int)c.ObjectClass,
            LongValKeyGECondition c     => item.Values(c.Key, 0) >= c.Value,
            LongValKeyLECondition c     => item.Values(c.Key, 0) <= c.Value,
            LongValKeyECondition c      => item.Values(c.Key, 0) == c.Value,
            LongValKeyNECondition c     => item.Values(c.Key, 0) != c.Value,
            LongValKeyFlagCondition c   => (item.Values(c.Key, 0) & c.FlagValue) != 0,
            DoubleValKeyGECondition c   => item.Values((DoubleValueKey)c.Key, 0.0) >= c.Value,
            DoubleValKeyLECondition c   => item.Values((DoubleValueKey)c.Key, 0.0) <= c.Value,
            StringValueCondition c      => EvalString(item, c),
            TotalRatingsGECondition c   => EvalTotalRatings(item) >= c.Value,
            MinDamageGECondition c      => EvalMinDamage(item) >= c.Value,
            DamagePercentGECondition c  => item.Values(DoubleValueKey.DamageMod, 0.0) * 100.0 >= c.Value,
            CharacterSkillGECondition c => skills == null || skills[(AcSkillType)(int)c.Skill].Buffed >= c.Value,
            _                           => true,  // unknown condition type — pass optimistically
        };

    public static bool Matches(LootRule rule, WorldObject item, CharacterSkills? skills)
    {
        if (!rule.Enabled) return false;
        foreach (var cond in rule.Conditions)
            if (!Evaluate(cond, item, skills)) return false;
        return true;
    }

    /// <summary>
    /// Returns the first matching rule's action, or null if nothing matched.
    /// Callers treat null as "leave on corpse" — do not pick up.
    /// </summary>
    public static (LootAction action, LootRule? rule) Classify(
        LootProfile profile, WorldObject item, CharacterSkills? skills)
    {
        foreach (var rule in profile.Rules)
            if (Matches(rule, item, skills)) return (rule.Action, rule);
        return (LootAction.Sell, null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool EvalString(WorldObject item, StringValueCondition c)
    {
        string value = item.Values((StringValueKey)c.Key, string.Empty);
        return Regex.IsMatch(value, c.Pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static int EvalTotalRatings(WorldObject item)
    {
        int total = 0;
        foreach (int k in RatingKeys) total += item.Values(k, 0);
        return total;
    }

    private static double EvalMinDamage(WorldObject item)
    {
        int maxDamage    = item.Values(54, 0); // STypeInt MAX_DAMAGE
        double variance  = item.Values(DoubleValueKey.DamageVariance, 0.0);
        return maxDamage - variance * maxDamage;
    }
}

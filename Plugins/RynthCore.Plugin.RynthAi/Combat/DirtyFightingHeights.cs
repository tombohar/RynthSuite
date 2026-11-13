// ============================================================================
//  RynthSuite - Combat/DirtyFightingHeights.cs
//  Dirty Fighting rotation: which attack height the next melee swing or missile
//  shot uses, so all three Dirty Fighting effects get onto the target.
//
//  How the server does it (ACE, read for behaviour only): with Dirty Fighting
//  at least trained, every melee or missile hit that does damage has a 25%
//  chance (scaled down by Dirty Fighting / weapon skill when Dirty Fighting is
//  the lower one) to land the effect of the height it was aimed at:
//    High    attack skills down, and heals on the target down (two spells)
//    Medium  a bleed (damage over time)
//    Low     defense skills down
//  Each lasts 20 seconds. The same effect from the same attacker refreshes to a
//  full 20 s rather than stacking; the three heights stack with each other.
//  Each landing is announced to the attacker in combat chat:
//    "Dirty Fighting! <attacker> delivers a <spell> to <target>!"
//  with the spell names in SpellNames below (Blow = trained, Assault =
//  specialized). That line is the only signal the attacker gets, so the
//  tracker keys on it and the target's name.
//
//  Pure logic (System only, time passed in), so the tests can run it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.RynthAi;

internal static class DirtyFightingHeights
{
    // Heights in the settings' numbering (MeleeAttackHeight / MissileAttackHeight).
    public const int Low = 0;
    public const int Medium = 1;
    public const int High = 2;

    /// <summary>DfDefaultHeight: the regular Melee/Missile Attack Height setting.</summary>
    public const int DefaultIsFixedHeight = -1;

    /// <summary>How long each effect lasts on the server (every Dirty Fighting spell is 20 s).</summary>
    public const double EffectSeconds = 20.0;

    /// <summary>
    /// An effect this close to running out counts as missing, so the rotation goes back to
    /// its height while it is still on (a landing refreshes it to the full 20 s).
    /// </summary>
    public const double RenewLeadSeconds = 5.0;

    /// <summary>The server's best chance per damaging hit (Dirty Fighting at or above the weapon skill).</summary>
    public const double BaseChance = 0.25;

    public const string DefaultOrder = "HML";

    /// <summary>Every order the setting can hold, in the order the UI lists them.</summary>
    public static readonly string[] OrderChoices = { "HML", "HLM", "MHL", "MLH", "LHM", "LMH" };

    public static readonly string[] HeightNames = { "Low", "Medium", "High" };

    /// <summary>The spell words after "delivers a" and the height each comes from.</summary>
    private static readonly (string Name, int Height)[] SpellNames =
    {
        ("Blinding Blow", High), ("Blinding Assault", High),
        ("Traumatic Blow", High), ("Traumatic Assault", High),
        ("Bleeding Blow", Medium), ("Bleeding Assault", Medium),
        ("Unbalancing Blow", Low), ("Unbalancing Assault", Low),
    };

    private const string Prefix = "Dirty Fighting! ";

    public static string HeightName(int height) =>
        height >= 0 && height < HeightNames.Length ? HeightNames[height] : "?";

    /// <summary>A valid order ("HML" and the like: H, M and L once each), else <see cref="DefaultOrder"/>.</summary>
    public static string NormalizeOrder(string? order)
    {
        if (string.IsNullOrWhiteSpace(order)) return DefaultOrder;
        string o = order.Trim().ToUpperInvariant();
        foreach (string c in OrderChoices)
            if (c == o) return c;
        return DefaultOrder;
    }

    /// <summary>The order as heights, first to last.</summary>
    public static int[] ParseOrder(string? order)
    {
        string o = NormalizeOrder(order);
        var heights = new int[3];
        for (int i = 0; i < 3; i++)
            heights[i] = o[i] switch { 'H' => High, 'M' => Medium, _ => Low };
        return heights;
    }

    /// <summary>"High, Medium, Low".</summary>
    public static string OrderLabel(string? order)
    {
        int[] h = ParseOrder(order);
        return $"{HeightName(h[0])}, {HeightName(h[1])}, {HeightName(h[2])}";
    }

    /// <summary>
    /// A Dirty Fighting landing line from <paramref name="attackerName"/>: the height whose
    /// effect landed and the target's name. False for anything else, a landing by someone else
    /// (a monster's on this character included) and the same words said in a chat channel.
    /// </summary>
    public static bool TryParseLanding(string? text, string? attackerName, out int height, out string targetName)
    {
        height = -1;
        targetName = string.Empty;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(attackerName)) return false;
        string t = text.Trim();
        if (!t.StartsWith(Prefix, StringComparison.Ordinal)) return false;

        string who = Prefix + attackerName + " delivers ";
        if (!t.StartsWith(who, StringComparison.Ordinal)) return false;
        string rest = t.Substring(who.Length);
        if (rest.StartsWith("a ", StringComparison.Ordinal)) rest = rest.Substring(2);
        else if (rest.StartsWith("an ", StringComparison.Ordinal)) rest = rest.Substring(3);
        else return false;

        foreach (var (name, h) in SpellNames)
        {
            string lead = name + " to ";
            if (!rest.StartsWith(lead, StringComparison.Ordinal)) continue;
            string target = rest.Substring(lead.Length);
            if (target.EndsWith('!')) target = target.Substring(0, target.Length - 1);
            target = target.Trim();
            if (target.Length == 0) return false;
            height = h;
            targetName = target;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The server's chance per damaging hit: 25%, scaled by Dirty Fighting / weapon skill when
    /// Dirty Fighting is the lower one. 0 when Dirty Fighting is not trained.
    /// </summary>
    public static double ChancePerHit(bool dfTrained, int dfSkill, int weaponSkill)
    {
        if (!dfTrained) return 0;
        if (weaponSkill <= 0 || dfSkill >= weaponSkill) return BaseChance;
        return BaseChance * Math.Max(0, dfSkill) / weaponSkill;
    }

    /// <summary>
    /// The height for the next attack. The fixed height (the Melee/Missile Attack Height setting)
    /// when the rotation is off or Dirty Fighting isn't trained. Otherwise the first height in
    /// <paramref name="order"/> whose effect is missing or due to run out within
    /// <see cref="RenewLeadSeconds"/>; with all three on, <paramref name="defaultHeight"/>
    /// (<see cref="DefaultIsFixedHeight"/> = the fixed height).
    /// <paramref name="effects"/> may be null (nothing known about this target: a fresh one).
    /// </summary>
    public static int Choose(bool rotationOn, bool dfTrained, int fixedHeight, string? order, int defaultHeight,
        DfTargetEffects? effects, DateTime now)
    {
        int fixedH = Math.Clamp(fixedHeight, Low, High);
        if (!rotationOn || !dfTrained) return fixedH;
        foreach (int h in ParseOrder(order))
            if (effects == null || !effects.IsOn(h, now, RenewLeadSeconds))
                return h;
        return defaultHeight >= Low && defaultHeight <= High ? defaultHeight : fixedH;
    }
}

/// <summary>When each of one target's three effects runs out (MinValue = never landed).</summary>
internal sealed class DfTargetEffects
{
    private readonly DateTime[] _until = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };

    public DateTime Until(int height) => _until[height];

    public void Landed(int height, DateTime now) =>
        _until[height] = now.AddSeconds(DirtyFightingHeights.EffectSeconds);

    public void SetUntil(int height, DateTime until) => _until[height] = until;

    /// <summary>On, with more than <paramref name="leadSeconds"/> left.</summary>
    public bool IsOn(int height, DateTime now, double leadSeconds = 0) =>
        (_until[height] - now).TotalSeconds > leadSeconds;

    public bool AnyOn(DateTime now) => IsOn(0, now) || IsOn(1, now) || IsOn(2, now);

    /// <summary>"High 14 s, Medium off, Low 3 s", in the given order.</summary>
    public string Describe(string? order, DateTime now)
    {
        var sb = new StringBuilder();
        foreach (int h in DirtyFightingHeights.ParseOrder(order))
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(DirtyFightingHeights.HeightName(h)).Append(' ');
            double left = (_until[h] - now).TotalSeconds;
            sb.Append(left > 0 ? $"{Math.Ceiling(left):0} s" : "off");
        }
        return sb.ToString();
    }
}

/// <summary>
/// The effects this character's landing lines put on each target, by object id. Entries go when
/// every effect on them has run out. Locked: chat, the combat tick and the UI read it.
/// </summary>
internal sealed class DirtyFightingTracker
{
    private const int MaxTargets = 64;
    private readonly Dictionary<int, DfTargetEffects> _targets = new();
    private readonly object _lock = new();

    public void Landed(int targetId, int height, DateTime now)
    {
        if (targetId == 0 || height < DirtyFightingHeights.Low || height > DirtyFightingHeights.High) return;
        lock (_lock)
        {
            if (!_targets.TryGetValue(targetId, out var fx))
            {
                if (_targets.Count >= MaxTargets) PruneLocked(now);
                fx = new DfTargetEffects();
                _targets[targetId] = fx;
            }
            fx.Landed(height, now);
        }
    }

    /// <summary>A copy of the target's effects, or null when none are known.</summary>
    public DfTargetEffects? Get(int targetId)
    {
        lock (_lock)
        {
            if (!_targets.TryGetValue(targetId, out var fx)) return null;
            var copy = new DfTargetEffects();
            for (int h = 0; h < 3; h++) copy.SetUntil(h, fx.Until(h));
            return copy;
        }
    }

    public void Forget(int targetId)
    {
        lock (_lock) _targets.Remove(targetId);
    }

    public void Clear()
    {
        lock (_lock) _targets.Clear();
    }

    public int Count
    {
        get { lock (_lock) return _targets.Count; }
    }

    private void PruneLocked(DateTime now)
    {
        var gone = new List<int>();
        foreach (var kv in _targets)
            if (!kv.Value.AnyOn(now)) gone.Add(kv.Key);
        foreach (int id in gone) _targets.Remove(id);
        // Still full (64 targets all carrying effects): drop the one that runs out first.
        while (_targets.Count >= MaxTargets)
        {
            int oldest = 0;
            DateTime best = DateTime.MaxValue;
            foreach (var kv in _targets)
            {
                DateTime last = Max(kv.Value.Until(0), Max(kv.Value.Until(1), kv.Value.Until(2)));
                if (last < best) { best = last; oldest = kv.Key; }
            }
            _targets.Remove(oldest);
        }
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}

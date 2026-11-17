// ============================================================================
//  RynthSuite - Combat/BuffManager.HealBeforeStamina.cs
//  Health falling: a heal before the next stamina kit or Revitalize.
//
//  A stamina kit takes about 2.4 s to land and nothing else happens for the vitals meanwhile (KitPending);
//  Revitalize out of Magic is a wand swap and a cast. The vitals only asked "is health under a heal
//  line now?" before each one, so a Re-stam To run took kit after kit with a monster hitting: Drakkon
//  (DreamWeave, 2026-11-11 16:24:54 - 16:25:04: Heal At 1, Emergency Heal At 30, Re-stam To 90) went
//  from 100% to dead over three stamina kits in a row, each started while health was still over 30%
//  (the owner, 2026-11-17: "it will ignore healing and use the stamina kit ... a second time").
//
//  Now health is sampled every vitals check (the last HealthTrendWindowMs). Before a stamina use,
//  health is projected over the use (StaminaUseBlindMs) at the rate it has been falling since its
//  latest high; at or under the highest heal line (Heal At, Emergency Heal At, Stamina To Health At)
//  the heal chain goes first. Health steady, or nothing that can heal: the stamina use goes as before.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    /// <summary>How far back the health samples go.</summary>
    internal const int HealthTrendWindowMs = 4000;
    /// <summary>A stamina use with nothing else for the vitals: a kit's 2.4 s and the pass after it.</summary>
    internal const int StaminaUseBlindMs = 3000;
    /// <summary>The fall's rate is never taken over less than this (one hit just now is no rate).</summary>
    internal const int HealthTrendMinSpanMs = 1000;
    /// <summary>Nothing could heal first: the stamina uses go for this long without asking again.</summary>
    private const int HealFirstUnavailableMs = 3000;

    private readonly List<(DateTime At, int Pct)> _healthSamples = new();
    private DateTime _healFirstUnavailableUntil = DateTime.MinValue;
    private DateTime _healFirstLoggedAt = DateTime.MinValue;

    /// <summary>One health reading per vitals check (CheckVitalsCore, before anything can return).</summary>
    private void RecordHealthSample(int pct, DateTime now)
    {
        if (_vitals.MaxHealth == 0 || _vitals.CurrentHealth == 0)
        {
            _healthSamples.Clear();   // no reading, or dead: a fall from before the death is no trend
            return;
        }
        _healthSamples.Add((now, pct));
        int old = 0;
        while (old < _healthSamples.Count && (now - _healthSamples[old].At).TotalMilliseconds > HealthTrendWindowMs) old++;
        if (old > 0) _healthSamples.RemoveRange(0, old);
        if (_healthSamples.Count > 512) _healthSamples.RemoveRange(0, _healthSamples.Count - 512);
    }

    /// <summary>
    /// Health in <paramref name="horizonMs"/> at the rate it has fallen since its latest high in
    /// <paramref name="samples"/> (that rate over at least <see cref="HealthTrendMinSpanMs"/>). Not
    /// falling: <paramref name="cur"/>. Pure (host tests).
    /// </summary>
    internal static int ProjectedHealthPct(IReadOnlyList<(DateTime At, int Pct)> samples, DateTime now, int cur, int horizonMs)
    {
        int peak = cur;
        DateTime peakAt = now;
        foreach (var s in samples)
        {
            if ((now - s.At).TotalMilliseconds > HealthTrendWindowMs || s.At > now) continue;
            // The latest time health stood at its highest: the fall from there is the one under way.
            if (s.Pct > peak || (s.Pct == peak && s.Pct > cur && s.At > peakAt)) { peak = s.Pct; peakAt = s.At; }
        }
        int drop = peak - cur;
        if (drop <= 0) return cur;
        double spanMs = Math.Max(HealthTrendMinSpanMs, (now - peakAt).TotalMilliseconds);
        return (int)Math.Floor(cur - drop * horizonMs / spanMs);
    }

    /// <summary>The highest heal line set: Heal At (in a fight), Emergency Heal At, Stamina To Health At.</summary>
    private int HealLineBeforeStamina(bool inCombat)
        => Math.Max(inCombat ? _settings.HealAt : 0, Math.Max(_settings.EmergencyHealAt, _settings.StaminaToHealthAt));

    /// <summary>
    /// Called right before a stamina use (a kit, Revitalize, a potion). Health falling to a heal line
    /// within the use: the heal chain goes now instead. True = it healed (or a heal is under way).
    /// </summary>
    private bool HealBeforeStaminaUse(int curHealthPct, bool inCombat)
    {
        DateTime now = Clock();
        if (now < _healFirstUnavailableUntil) return false;
        int line = HealLineBeforeStamina(inCombat);
        if (line <= 0 || curHealthPct >= 100) return false;
        int projected = ProjectedHealthPct(_healthSamples, now, curHealthPct, StaminaUseBlindMs);
        if (projected > line) return false;

        bool healed = AttemptHealChain(itemsBeforeSpell: CurrentCombatMode != CombatMode.Magic, allowSpell: true,
                                       out string kitWhy, out string spellWhy, out string potionWhy);
        bool log = (now - _healFirstLoggedAt).TotalSeconds >= 10;
        if (healed)
        {
            if (log)
            {
                _healFirstLoggedAt = now;
                _host.Log($"[RynthAi] Vitals: health {curHealthPct}% is falling (about {Math.Max(0, projected)}% after a stamina kit's {StaminaUseBlindMs / 1000} s, " +
                          $"at or under the heal line {line}%): a heal first, the stamina after it.");
            }
            return true;
        }
        _healFirstUnavailableUntil = now.AddMilliseconds(HealFirstUnavailableMs);
        if (log)
        {
            _healFirstLoggedAt = now;
            _host.Log($"[RynthAi] Vitals: health {curHealthPct}% is falling to about {Math.Max(0, projected)}% within a stamina use, but nothing could heal first " +
                      $"(kit: {Why(kitWhy)}; Heal Self: {Why(spellWhy)}; potion: {Why(potionWhy)}): the stamina goes.");
        }
        return false;
    }
}

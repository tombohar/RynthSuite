// ============================================================================
//  RynthSuite - RechargeHold.cs
//  No recharge for now: health, stamina and mana recharge held off, the settings kept (2026-11-24).
//
//  R-20261124-121710-7876 (DreamWeave, Threads): "When running Threads ... you generally don't want
//  your character to pull out a wand to recharge vitals while fighting"; a quick way to turn the
//  Recharge lines off and "restore the previous settings with a single click".
//  Tried as a button players can turn on and off.
//
//  A hold sets a vital's recharge lines to 0 (the settings' own "off") and keeps the real values:
//    Health   Heal At, Top Off HP, Stamina To Health At (Emergency Heal At stays: the safety floor)
//    Stamina  Re-stam At, Re-stam To, Top Off Stam
//    Mana     Get Mana At, Top Off Mana
//  Zeroing the lines themselves, not one decision, keeps every reader in step (the vitals pass, the
//  arbiter's "wants a recharge", the mana reserve, the idle top-off). Releasing puts the real values
//  back, except a line the player set to something else while it was held (theirs wins). Every save
//  writes the real values (WithRealValues), so a hold never reaches the profile, and a profile
//  loaded while held keeps the hold over its own values. Not saved: a reload or relog ends it.
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>The vitals a recharge hold can cover.</summary>
[Flags]
internal enum RechargeVital
{
    None = 0,
    Health = 1,
    Stamina = 2,
    Mana = 4,
    All = Health | Stamina | Mana,
}

internal sealed class RechargeHold
{
    private sealed record Line(RechargeVital Vital, string Name, Func<LegacyUiSettings, int> Get, Action<LegacyUiSettings, int> Set);

    private static readonly Line[] Lines =
    {
        new(RechargeVital.Health,  "Heal At",              s => s.HealAt,            (s, v) => s.HealAt = v),
        new(RechargeVital.Health,  "Top Off HP",           s => s.TopOffHP,          (s, v) => s.TopOffHP = v),
        new(RechargeVital.Health,  "Stamina To Health At", s => s.StaminaToHealthAt, (s, v) => s.StaminaToHealthAt = v),
        new(RechargeVital.Stamina, "Re-stam At",           s => s.RestamAt,          (s, v) => s.RestamAt = v),
        new(RechargeVital.Stamina, "Re-stam To",           s => s.RestamTo,          (s, v) => s.RestamTo = v),
        new(RechargeVital.Stamina, "Top Off Stam",         s => s.TopOffStam,        (s, v) => s.TopOffStam = v),
        new(RechargeVital.Mana,    "Get Mana At",          s => s.GetManaAt,         (s, v) => s.GetManaAt = v),
        new(RechargeVital.Mana,    "Top Off Mana",         s => s.TopOffMana,        (s, v) => s.TopOffMana = v),
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, int> _real = new();   // a held line's real value

    /// <summary>The vitals held now.</summary>
    public RechargeVital Held { get; private set; }

    /// <summary>Holds (<paramref name="on"/>) or releases the recharge of <paramref name="vitals"/>.</summary>
    public void Set(LegacyUiSettings s, RechargeVital vitals, bool on)
    {
        lock (_sync)
        {
            foreach (Line line in Lines)
            {
                if ((line.Vital & vitals) == 0) continue;
                bool held = _real.ContainsKey(line.Name);
                if (on && !held)
                {
                    _real[line.Name] = line.Get(s);
                    line.Set(s, 0);
                }
                else if (!on && held)
                {
                    if (line.Get(s) == 0) line.Set(s, _real[line.Name]);   // a value the player set meanwhile stays
                    _real.Remove(line.Name);
                }
            }
            Held = on ? Held | vitals : Held & ~vitals;
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> (a save) with the held lines at their real values, then holds them
    /// again: a hold never reaches the profile.
    /// </summary>
    public T WithRealValues<T>(LegacyUiSettings s, Func<T> read)
    {
        lock (_sync)
        {
            if (_real.Count == 0) return read();
            var swapped = new List<Line>();
            foreach (Line line in Lines)
            {
                if (!_real.TryGetValue(line.Name, out int real) || line.Get(s) != 0) continue;
                line.Set(s, real);
                swapped.Add(line);
            }
            try { return read(); }
            finally { foreach (Line line in swapped) line.Set(s, 0); }
        }
    }

    /// <summary>
    /// A profile was copied over the settings: its values are the real ones now, held again (the
    /// hold stays, over the new profile's lines).
    /// </summary>
    public void Reapply(LegacyUiSettings s)
    {
        RechargeVital held;
        lock (_sync)
        {
            held = Held;
            _real.Clear();
            Held = RechargeVital.None;
        }
        if (held != RechargeVital.None) Set(s, held, true);
    }

    /// <summary>The real value of a held line (tests, the tooltip); null when that line isn't held.</summary>
    public int? RealValue(string lineName)
    {
        lock (_sync) return _real.TryGetValue(lineName, out int v) ? v : null;
    }

    /// <summary>"health, stamina and mana", "stamina", ...</summary>
    public static string Describe(RechargeVital v)
    {
        var parts = new List<string>(3);
        if ((v & RechargeVital.Health) != 0) parts.Add("health");
        if ((v & RechargeVital.Stamina) != 0) parts.Add("stamina");
        if ((v & RechargeVital.Mana) != 0) parts.Add("mana");
        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            2 => parts[0] + " and " + parts[1],
            _ => parts[0] + ", " + parts[1] + " and " + parts[2],
        };
    }
}

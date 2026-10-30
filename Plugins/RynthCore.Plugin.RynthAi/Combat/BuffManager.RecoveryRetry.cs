// ============================================================================
//  RynthSuite - Combat/BuffManager.RecoveryRetry.cs
//  Fast retry of the recovery vitals (Heal Self, Stamina to Health, Revitalize,
//  Stamina to Mana) after the server's "not enough mana" answer.
//
//  ACE (origin/master, Creature_Magic.GetManaCost) rolls Mana Conversion on every
//  cast attempt: a trained caster pays between the full cost and 1 mana. The roll
//  and the check run before the windup (Player_Magic.CalculateManaUsage), so a
//  refusal costs nothing: no mana, no busy period, no recoil, only UseDone 0x401.
//  At low mana the cast goes through on a good roll, so trying again soon is the
//  way to get it off. Before this, the refusal rested the spell's family (LowManaHealRestSec).
//
//  Bounds (the refusal-spam crash class, see the "too busy" handler): the retry
//  gap is never under RecoveryRetryMinMs, at most RecoveryRetryMaxPerSecond
//  recovery casts go out in any second while an episode is open, and an episode
//  stops after RecoveryRetryMaxRefusals mana refusals in a row (any recovery
//  spell); the refused family then rests as before (a vital LowManaHealRestSec, 2 s).
// ============================================================================

using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

public partial class BuffManager
{
    internal const int RecoveryRetryMinMs = 200;
    internal const int RecoveryRetryMaxMs = 5000;
    internal const int RecoveryRetryMaxPerSecond = 5;
    internal const int RecoveryRetryMaxRefusals = 20;
    // A refusal this long after the last one starts a new episode; an open episode with no refusal
    // for this long ends. Above the slowest retry gap (the setting's 5000 ms maximum) with room for
    // the pacing in between, so a slow setting never ends its own episode between two retries.
    internal const double RecoveryEpisodeStaleMs = RecoveryRetryMaxMs + 2000;
    // Stamina to Mana is attempted only above this stamina % (the CheckVitals and mana floor gates).
    private const int RecoveryMinStaminaToConvert = 15;

    private bool _recoveryOpen;
    private DateTime _recoveryStartedAt;
    private DateTime _recoveryLastRefusalAt;
    private int _recoveryRefusals;
    private readonly Dictionary<int, DateTime> _recoveryNextAt = new();
    private readonly List<string> _recoveryNames = new();
    // Recovery casts sent (any time), for the per-second ceiling.
    private readonly Queue<DateTime> _recoveryAttempts = new();
    // The base name of the pending vital ("Stamina to Mana Self"...), set by CastVital.
    private string _pendingVitalBase = "";

    /// <summary>Recovery casts held by the per-second ceiling (tests, status).</summary>
    internal int RecoveryRateHolds { get; private set; }
    /// <summary>The last episode summary line (tests, status).</summary>
    internal string LastRecoverySummary { get; private set; } = "";
    /// <summary>True while a fast-retry episode is open (tests).</summary>
    internal bool RecoveryRetryOpen => _recoveryOpen;

    internal static bool IsRecoveryVital(string baseName) =>
        baseName is "Heal Self" or "Stamina to Health Self" or "Revitalize Self" or "Stamina to Mana Self";

    private int RecoveryRetryMs => Math.Clamp(_settings.RecoveryRetryMs, RecoveryRetryMinMs, RecoveryRetryMaxMs);

    /// <summary>
    /// Why a fast retry can't help now, or "" when it can. Untrained Mana Conversion pays the full
    /// cost every time (GetManaCost returns the base cost), so a retry would only be refused again;
    /// trained, the least a cast can cost is 1 mana.
    /// </summary>
    private string RecoveryRetryBlocked(string baseName)
    {
        if (!IsSkillUsable(AcSkillType.ManaConversion)) return "Mana Conversion not trained (no cost roll)";
        if (_vitals.MaxMana > 0 && _vitals.CurrentMana < 1) return "no mana at all";
        if (baseName == "Stamina to Mana Self" && _vitals.StaminaPct <= RecoveryMinStaminaToConvert)
            return $"stamina {_vitals.StaminaPct}% too low to convert";
        if (baseName == "Stamina to Health Self" && _vitals.StaminaPct <= _settings.StaminaToHealthMinStamina)
            return $"stamina {_vitals.StaminaPct}% too low to convert";
        return "";
    }

    /// <summary>
    /// "Not enough mana" for the pending cast. Returns true when it was a recovery vital that will be
    /// retried soon (the caller then skips the family rest); false leaves the old handling.
    /// </summary>
    private bool OnRecoveryManaRefusal(SpellInfo? pending)
    {
        if (pending == null || _pendingSpellId != _pendingVitalSpellId || !IsRecoveryVital(_pendingVitalBase))
            return false;
        DateTime now = Now;
        if (_recoveryOpen && (now - _recoveryLastRefusalAt).TotalMilliseconds > RecoveryEpisodeStaleMs)
            EndRecoveryEpisode(now, "no refusal for a while", success: false, "");
        if (!_recoveryOpen)
        {
            _recoveryOpen = true;
            _recoveryStartedAt = now;
            _recoveryRefusals = 0;
            _recoveryNames.Clear();
        }
        _recoveryRefusals++;
        _recoveryLastRefusalAt = now;
        if (!_recoveryNames.Contains(pending.Name)) _recoveryNames.Add(pending.Name);
        string blocked = RecoveryRetryBlocked(_pendingVitalBase);
        if (blocked.Length > 0)
        {
            EndRecoveryEpisode(now, blocked, success: false, "");
            return false;
        }
        if (_recoveryRefusals >= RecoveryRetryMaxRefusals)
        {
            EndRecoveryEpisode(now, $"{_recoveryRefusals} refusals in a row", success: false, "");
            return false;
        }
        _recoveryNextAt[pending.Family] = now.AddMilliseconds(RecoveryRetryMs);
        if (_recoveryRefusals == 1)
            _host.Log($"[Vitals] {pending.Name}: not enough mana (mana {_vitals.CurrentMana}); retrying every {RecoveryRetryMs} ms for a Mana Conversion roll (at most {RecoveryRetryMaxPerSecond}/s, {RecoveryRetryMaxRefusals} refusals).");
        return true;
    }

    /// <summary>A result line for <paramref name="family"/>: a retried cast went through.</summary>
    private void OnRecoveryCastResult(int family, string name)
    {
        if (_recoveryOpen && _recoveryNextAt.ContainsKey(family))
            EndRecoveryEpisode(Now, "", success: true, name);
    }

    private void EndRecoveryEpisode(DateTime now, string why, bool success, string name)
    {
        if (!_recoveryOpen) return;
        _recoveryOpen = false;
        _recoveryNextAt.Clear();
        double ms = (now - _recoveryStartedAt).TotalMilliseconds;
        string names = string.Join(", ", _recoveryNames);
        LastRecoverySummary = success
            ? $"[Vitals] {name} went through after {_recoveryRefusals + 1} attempts ({_recoveryRefusals} refused for mana: {names}) in {ms:0} ms."
            : $"[Vitals] stopped retrying {names} after {_recoveryRefusals} mana refusals in {ms:0} ms ({why}).";
        _host.Log(LastRecoverySummary);
    }

    /// <summary>
    /// Ends an episode with no refusal for RecoveryEpisodeStaleMs (the cast went through unseen, the
    /// vital is no longer needed, or something else took over), so a stale one never keeps letting
    /// casts past Spell Interval. Once per heartbeat.
    /// </summary>
    private void ExpireRecoveryEpisode()
    {
        if (_recoveryOpen && (Now - _recoveryLastRefusalAt).TotalMilliseconds > RecoveryEpisodeStaleMs)
            EndRecoveryEpisode(Now, $"no refusal for {RecoveryEpisodeStaleMs / 1000:0} s", success: false, "");
    }

    /// <summary>
    /// The cast interval gate lets a due recovery retry through: the retry gap (RecoveryRetryMs)
    /// replaces Spell Interval for it, and the per-second ceiling still applies in CastVital.
    /// </summary>
    private bool RecoveryRetryDue()
    {
        if (!_recoveryOpen) return false;
        DateTime now = Now;
        foreach (var at in _recoveryNextAt.Values)
            if (now >= at) return true;
        return false;
    }

    /// <summary>CastVital's gate for a recovery vital while an episode is open: "" to cast, else why it waits.</summary>
    private string RecoveryRetryHolds(int family)
    {
        if (!_recoveryOpen) return "";
        DateTime now = Now;
        if (_recoveryNextAt.TryGetValue(family, out DateTime next) && now < next)
            return $"retrying after a mana refusal in {(next - now).TotalMilliseconds:0} ms";
        while (_recoveryAttempts.Count > 0 && (now - _recoveryAttempts.Peek()).TotalMilliseconds >= 1000)
            _recoveryAttempts.Dequeue();
        if (_recoveryAttempts.Count >= RecoveryRetryMaxPerSecond)
        {
            RecoveryRateHolds++;
            return $"{RecoveryRetryMaxPerSecond} recovery casts in the last second";
        }
        return "";
    }

    private void NoteRecoveryAttempt()
    {
        DateTime now = Now;
        while (_recoveryAttempts.Count > 0 && (now - _recoveryAttempts.Peek()).TotalMilliseconds >= 1000)
            _recoveryAttempts.Dequeue();
        _recoveryAttempts.Enqueue(now);
    }
}

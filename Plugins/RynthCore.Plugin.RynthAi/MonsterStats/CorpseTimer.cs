// ============================================================================
//  RynthAi - MonsterStats/CorpseTimer.cs
//  How long each kind of monster takes to die (2026-11-08, Tom: "the bot is
//  running away from a monster it's killed and then turning around and running
//  back because there is a second or two for the corpse to show up due to a
//  death animation"). ACE creates the corpse once the death animation has
//  played, so the time from our kill notice to its "Corpse of <name>" object
//  is the death animation's length.
//
//  Each credited kill is queued with its time; a corpse object named "Corpse
//  of <name>" created within WindowMs is paired with the oldest kill of that
//  name still waiting. Pure: every call takes the time (ms), so the tests drive
//  it. One thread (the plugin pump).
// ============================================================================
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.MonsterStats;

internal sealed class CorpseTimer
{
    public const string CorpsePrefix = "Corpse of ";
    /// <summary>A kill waits this long for its corpse; later, it's no death animation we can time.</summary>
    public long WindowMs { get; set; } = 8000;
    /// <summary>Kills kept waiting at most; past it the oldest goes.</summary>
    public const int MaxWaiting = 32;

    private readonly List<(string Name, long At)> _kills = new();

    internal int Waiting => _kills.Count;

    /// <summary>The monster a corpse's name says ("Corpse of Drudge Skulker" -> "Drudge Skulker"), or null for any other name.</summary>
    public static string? KilledName(string? corpseName)
    {
        if (string.IsNullOrEmpty(corpseName) || !corpseName.StartsWith(CorpsePrefix, StringComparison.Ordinal)) return null;
        string n = corpseName.Substring(CorpsePrefix.Length).Trim();
        return n.Length > 0 ? n : null;
    }

    /// <summary>A kill notice credited <paramref name="name"/> at <paramref name="nowMs"/>.</summary>
    public void OnKill(string? name, long nowMs)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Expire(nowMs);
        if (_kills.Count >= MaxWaiting) _kills.RemoveAt(0);
        _kills.Add((name.Trim(), nowMs));
    }

    /// <summary>
    /// An object named <paramref name="corpseName"/> was created: when it is the corpse of a kill
    /// still waiting, that kill's name and the milliseconds from the kill to the corpse; else null.
    /// </summary>
    public (string Name, long Ms)? OnCorpse(string? corpseName, long nowMs)
    {
        Expire(nowMs);
        string? name = KilledName(corpseName);
        if (name == null) return null;
        for (int i = 0; i < _kills.Count; i++)
        {
            if (!_kills[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            long ms = Math.Max(0, nowMs - _kills[i].At);
            string killed = _kills[i].Name;
            _kills.RemoveAt(i);
            return (killed, ms);
        }
        return null;
    }

    public void Reset() => _kills.Clear();

    private void Expire(long nowMs)
    {
        while (_kills.Count > 0 && nowMs - _kills[0].At > WindowMs) _kills.RemoveAt(0);
    }
}

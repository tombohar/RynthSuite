// ============================================================================
//  RynthAi - MonsterStats/KillXpMatcher.cs
//  Which kill an experience rise belongs to (2026-11-08, Tom: "The monster tab
//  should record how much experience you get for killing x mob").
//
//  The server says nothing that ties XP to a kill: the kill notice names the
//  monster, and the player's TotalExperience (quad property 1) goes up a moment
//  before or after it. So: each kill notice that credits a monster is queued
//  with its time; the plugin hands in TotalExperience every tick; a rise is
//  paired with the oldest kill still waiting, one rise per kill. A kill waits
//  up to KillWaitSec for its rise; a rise that comes first waits up to
//  RiseWaitSec for its kill notice. A rise with no kill (a quest, a fellow's
//  kill shared with us, a turn-in) finds none and is dropped.
//
//  Pure: every call takes the time, so the tests drive it. One thread (the
//  plugin pump).
// ============================================================================
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.MonsterStats;

/// <summary>One kill with the experience it gave.</summary>
internal readonly record struct KillXp(string Name, long Xp, DateTime At);

internal sealed class KillXpMatcher
{
    /// <summary>A kill notice waits this long for its experience rise.</summary>
    public double KillWaitSec { get; set; } = 3.0;
    /// <summary>A rise that came before its kill notice waits this long for it.</summary>
    public double RiseWaitSec { get; set; } = 1.0;
    /// <summary>Kills (and rises) kept waiting at most; past it the oldest goes (a ring spell's ten kills fit).</summary>
    public const int MaxWaiting = 32;

    private readonly List<(string Name, DateTime At)> _kills = new();
    private readonly List<(long Xp, DateTime At)> _rises = new();
    private long? _lastTotal;

    /// <summary>Kills waiting for their rise (tests).</summary>
    internal int WaitingKills => _kills.Count;
    /// <summary>Rises waiting for their kill (tests).</summary>
    internal int WaitingRises => _rises.Count;

    /// <summary>A kill notice credited <paramref name="name"/>. Returns the kills paired now (a rise that came first).</summary>
    public IReadOnlyList<KillXp> OnKill(string? name, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<KillXp>();
        if (_kills.Count >= MaxWaiting) _kills.RemoveAt(0);
        _kills.Add((name.Trim(), now));
        return Match(now);
    }

    /// <summary>
    /// The player's TotalExperience now. The first value read is the starting point (a login, a
    /// reload); a value lower than the last (another character) starts over from it. Returns the
    /// kills paired now.
    /// </summary>
    public IReadOnlyList<KillXp> OnTotal(long total, DateTime now)
    {
        if (_lastTotal is not long last || total < last)
        {
            _lastTotal = total;
            return Match(now);
        }
        if (total > last)
        {
            if (_rises.Count >= MaxWaiting) _rises.RemoveAt(0);
            _rises.Add((total - last, now));
            _lastTotal = total;
        }
        return Match(now);
    }

    /// <summary>A logout: nothing waits on, and the next value read is a new starting point.</summary>
    public void Reset()
    {
        _kills.Clear();
        _rises.Clear();
        _lastTotal = null;
    }

    private IReadOnlyList<KillXp> Match(DateTime now)
    {
        List<KillXp>? done = null;
        while (_kills.Count > 0 && _rises.Count > 0)
        {
            var k = _kills[0];
            var r = _rises[0];
            double riseAfterKill = (r.At - k.At).TotalSeconds;
            if (riseAfterKill > KillWaitSec) { _kills.RemoveAt(0); continue; }    // the kill gave nothing we saw
            if (-riseAfterKill > RiseWaitSec) { _rises.RemoveAt(0); continue; }   // the rise was no kill's
            _kills.RemoveAt(0);
            _rises.RemoveAt(0);
            (done ??= new List<KillXp>()).Add(new KillXp(k.Name, r.Xp, k.At > r.At ? k.At : r.At));
        }
        // What waited too long goes: a kill with no rise (a kill worth nothing), a rise with no kill (a quest).
        while (_kills.Count > 0 && (now - _kills[0].At).TotalSeconds > KillWaitSec) _kills.RemoveAt(0);
        while (_rises.Count > 0 && (now - _rises[0].At).TotalSeconds > RiseWaitSec) _rises.RemoveAt(0);
        return done ?? (IReadOnlyList<KillXp>)Array.Empty<KillXp>();
    }
}

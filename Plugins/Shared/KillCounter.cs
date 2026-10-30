using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.Shared;

/// <summary>
/// Counts your kills, including the extra monsters a ring, wall, blast or chain spell kills.
/// Shared by RynthTracker and RynthAi's Mini Remote through <see cref="SessionStats"/>.
///
/// Two ways a kill is counted:
///   1. The server's killer notification (it goes only to whoever landed the killing blow).
///      One notification is one kill, as before.
///   2. A corpse ("Corpse of X") with no notification for it, when you hit an X with a
///      multi-target spell in the last <see cref="TokenLifeMs"/>. Some servers send no
///      notification for the extra monsters a spell kills (chain spells on Conquest), and on
///      ACE a killing blow sends no damage line either, so the corpse is the one sign left.
///
/// "Multi-target" is read from the combat log: one cast's damage lines (and kill notices)
/// arrive together, so lines no more than <see cref="VolleyGapMs"/> apart are one volley, and
/// a volley with two or more of them hit several monsters. Each name hit in such a volley may
/// be counted from its corpse as many times as that volley hit it (tokens). A single-target
/// hit never makes a corpse count: a monster you hit once that someone else then finished
/// isn't yours, the same as before.
///
/// A notification is matched to its corpse later (the corpse forms after the death animation),
/// so a kill counted by its notification isn't counted again by its corpse.
///
/// Pure logic, no host: the plugin passes every event with its time, so tests can drive it.
/// </summary>
internal sealed class KillCounter
{
    /// <summary>Damage lines and kill notices this close together belong to one cast.</summary>
    public const double VolleyGapMs = 800;
    /// <summary>How long after a multi-target hit a monster's corpse still counts as your kill.</summary>
    public const double TokenLifeMs = 15000;
    /// <summary>How long a kill notice waits for its corpse.</summary>
    public const double NoticeLifeMs = 20000;
    /// <summary>How long a single-target hit is remembered (only to explain an uncounted corpse in the log).</summary>
    public const double HitMemoryMs = 15000;

    public int Kills { get; private set; }
    public int NoticeKills { get; private set; }
    public int CorpseKills { get; private set; }

    private readonly List<(string Message, DateTime At)> _notices = new();
    private readonly Dictionary<string, (int Count, DateTime Until)> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastHit = new(StringComparer.OrdinalIgnoreCase);

    // The volley in progress: hits per name, and how many members (lines + notices) it has.
    private readonly Dictionary<string, int> _volley = new(StringComparer.OrdinalIgnoreCase);
    private int _volleyMembers;
    private DateTime _volleyLast = DateTime.MinValue;

    /// <summary>The server's killer notification: always one kill.</summary>
    public void OnKillNotice(string? message, DateTime now)
    {
        Kills++;
        NoticeKills++;
        Prune(now);
        _notices.Add((message ?? "", now));
        JoinVolley(now);
        // A notice is one of the volley's monsters too (the killing blow sends no damage line),
        // under the name of a monster we hit that the message contains.
        string? name = LongestRecentHitIn(message ?? "", now);
        if (name != null) _volley[name] = _volley.TryGetValue(name, out int n) ? n + 1 : 1;
        GrantTokens(now);
    }

    private string? LongestRecentHitIn(string message, DateTime now)
    {
        string? best = null;
        foreach (var kv in _lastHit)
        {
            if ((now - kv.Value).TotalMilliseconds > HitMemoryMs) continue;
            if (message.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (best == null || kv.Key.Length > best.Length) best = kv.Key;
        }
        return best;
    }

    /// <summary>One of your damage lines, naming the monster it hurt.</summary>
    public void OnHit(string victim, DateTime now)
    {
        if (string.IsNullOrEmpty(victim)) return;
        JoinVolley(now);
        _volley[victim] = _volley.TryGetValue(victim, out int n) ? n + 1 : 1;
        _lastHit[victim] = now;
        GrantTokens(now);
    }

    /// <summary>What a corpse turned out to be.</summary>
    public enum CorpseResult
    {
        /// <summary>Its kill notice already counted it.</summary>
        Notified,
        /// <summary>No notice, but you hit it with a multi-target spell: counted now.</summary>
        Counted,
        /// <summary>You hit a monster of that name, but only single-target: not counted (someone else's kill?).</summary>
        HitNotCounted,
        /// <summary>Nothing of yours.</summary>
        NotOurs,
    }

    /// <summary>A corpse appeared; <paramref name="monsterName"/> is the X of "Corpse of X".</summary>
    public CorpseResult OnCorpse(string monsterName, DateTime now)
    {
        if (string.IsNullOrEmpty(monsterName)) return CorpseResult.NotOurs;
        Prune(now);

        // The oldest notice that names this monster.
        int match = -1;
        for (int i = 0; i < _notices.Count; i++)
            if (_notices[i].Message.IndexOf(monsterName, StringComparison.OrdinalIgnoreCase) >= 0) { match = i; break; }
        if (match >= 0)
        {
            _notices.RemoveAt(match);
            // It was one of the monsters a multi-target volley hit: it no longer vouches for another.
            if (_tokens.TryGetValue(monsterName, out var used) && used.Count > 0)
                _tokens[monsterName] = (used.Count - 1, used.Until);
            return CorpseResult.Notified;
        }

        if (_tokens.TryGetValue(monsterName, out var t) && t.Count > 0 && t.Until >= now)
        {
            _tokens[monsterName] = (t.Count - 1, t.Until);
            Kills++;
            CorpseKills++;
            return CorpseResult.Counted;
        }

        return _lastHit.TryGetValue(monsterName, out DateTime h) && (now - h).TotalMilliseconds <= HitMemoryMs
            ? CorpseResult.HitNotCounted
            : CorpseResult.NotOurs;
    }

    public void Reset()
    {
        Kills = NoticeKills = CorpseKills = 0;
        _notices.Clear();
        _tokens.Clear();
        _lastHit.Clear();
        _volley.Clear();
        _volleyMembers = 0;
        _volleyLast = DateTime.MinValue;
    }

    private void JoinVolley(DateTime now)
    {
        if ((now - _volleyLast).TotalMilliseconds > VolleyGapMs)
        {
            _volley.Clear();
            _volleyMembers = 0;
        }
        _volleyMembers++;
        _volleyLast = now;
    }

    // A volley that hit several monsters makes each name it hit countable from its corpse, as
    // many times as the volley hit that name. Max, not sum: the next cast hits the same monsters
    // again, and two casts at two monsters are still two monsters.
    private void GrantTokens(DateTime now)
    {
        if (_volleyMembers < 2) return;
        DateTime until = now.AddMilliseconds(TokenLifeMs);
        foreach (var kv in _volley)
        {
            int have = _tokens.TryGetValue(kv.Key, out var t) && t.Until >= now ? t.Count : 0;
            _tokens[kv.Key] = (Math.Max(have, kv.Value), until);
        }
    }

    private readonly List<string> _pruneScratch = new();
    private void Prune(DateTime now)
    {
        _notices.RemoveAll(n => (now - n.At).TotalMilliseconds > NoticeLifeMs);
        if (_tokens.Count > 32)
        {
            _pruneScratch.Clear();
            foreach (var kv in _tokens) if (kv.Value.Until < now) _pruneScratch.Add(kv.Key);
            foreach (string k in _pruneScratch) _tokens.Remove(k);
        }
        if (_lastHit.Count > 64)
        {
            _pruneScratch.Clear();
            foreach (var kv in _lastHit) if ((now - kv.Value).TotalMilliseconds > HitMemoryMs) _pruneScratch.Add(kv.Key);
            foreach (string k in _pruneScratch) _lastHit.Remove(k);
        }
    }
}

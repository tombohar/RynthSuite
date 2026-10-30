// ============================================================================
//  RynthSuite - Plugins/Shared/SessionStats.cs
//  One hunting session's numbers: experience, luminance (total, last five
//  minutes, allegiance pass-up), kills, XP per kill, deaths and Radiance.
//  Compiled into RynthTracker and RynthAi (linked source), so the Tracker
//  panel and the Mini Remote count every number the same way (2026-10-11).
//
//  Pure logic, no host: SessionStatsTracker feeds it property reads and
//  events with their time, so tests can drive it.
//
//  Luminance, by server:
//    retail ACE   lands on the character: AvailableLuminance (6) rises.
//    Aelrynth     the Bank mod's session totals: auto-banked (9102) adds what
//                 never landed, drawn (9103) takes out what the bank put on
//                 the character; Radiance earned is 9101.
//    ACECustom    every grant goes to the bank, BankedLuminance (9005), which
//                 the server pushes with the XP batch. AvailableLuminance only
//                 rises on a bank withdrawal there, so once 9005 is read its
//                 rises are ignored. "Deposited N luminance" and "Received N
//                 luminance from X" move lum into the bank without earning it:
//                 they are taken out of the next bank rises.
//    Pass-up      ACECustom never sends the allegiance counters (9013/9014).
//                 A vassal's pass-up lands in the bank silently, while your own
//                 kill and quest lum prints "You've banked N Luminance.", so
//                 pass-up = bank income minus that chat. Fellowship and Zone
//                 Share splits are silent too and fall into it.
//    Fallback     "You've banked N Luminance." counts on its own only until a
//                 bank property (9005 or 9102) is read; after that the property
//                 covers every source and the chat line would double-count.
// ============================================================================
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.Shared;

/// <summary>One poll of the player's properties; null = not readable this poll.</summary>
internal readonly record struct SessionSample(
    long? TotalXp,
    long? AvailableLum,
    long? BankLum,
    long? AelBanked,
    long? AelDrawn,
    long? Radiance);

/// <summary>A session's numbers, computed once per poll for any thread to read.</summary>
internal sealed record SessionStatsView(
    DateTime SessionStartUtc,
    double SessionSeconds,
    long Xp,
    double XpPerHour,
    long Lum,
    double LumPerHour,
    long LumLast5Min,
    long LumPassUp,
    double PassUpPerHour,
    bool PassUpTracked,
    int Kills,
    double KillsPerHour,
    long XpPerKill,
    int Deaths,
    long Rad,
    double RadPerHour,
    bool MaxLevel)
{
    public static readonly SessionStatsView Empty =
        new(DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0, 0, false);
}

/// <summary>The server's luminance chat lines that matter to the session.</summary>
internal static class SessionLumChat
{
    /// <summary>"You've banked N Luminance." (ACECustom: your own kill / quest lum).</summary>
    public static bool TryOwn(string? text, out long amount)
        => TryAmount(text, "You've banked ", " Luminance", out amount);

    /// <summary>"Deposited N luminance" (character to bank) or "Received N luminance from X" (a transfer).</summary>
    public static bool TryMovedIntoBank(string? text, out long amount)
        => TryAmount(text, "Deposited ", " luminance", out amount)
        || TryAmount(text, "Received ", " luminance from ", out amount);

    // The line starts with the prefix (a player quoting it in a tell doesn't), then "1,234,567", then the suffix.
    private static bool TryAmount(string? text, string prefix, string suffix, out long amount)
    {
        amount = 0;
        if (string.IsNullOrEmpty(text)) return false;
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (string.Compare(text, i, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        i += prefix.Length;
        int digits = 0;
        long value = 0;
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (c == ',') continue;
            if (c < '0' || c > '9') break;
            if (value > (long.MaxValue - 9) / 10) return false;
            value = value * 10 + (c - '0');
            digits++;
        }
        if (digits == 0) return false;
        if (string.Compare(text, i, suffix, 0, suffix.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        amount = value;
        return true;
    }
}

internal sealed class SessionStats
{
    /// <summary>XP rises seen this long after one of your kill notices belong to it.</summary>
    public const double KillWindowSec = 2.5;
    /// <summary>A kill found by its corpse takes the unattributed XP of this many seconds before it.</summary>
    public const double CorpseXpLookbackSec = 8.0;
    /// <summary>Retail's level-275 total. Past it a character may still be earning (awakened caps).</summary>
    public const long MaxLevelXp = 191_226_310_247;
    /// <summary>The "Lum (5 min)" window.</summary>
    public static readonly TimeSpan RecentLumWindow = TimeSpan.FromMinutes(5);
    /// <summary>Recent-lum entries closer together than this share one bucket (bounded memory and save size).</summary>
    private static readonly TimeSpan RecentLumBucket = TimeSpan.FromSeconds(5);
    /// <summary>A deposit / transfer line whose bank rise hasn't arrived by then is forgotten.</summary>
    private static readonly TimeSpan MovedIntoBankLife = TimeSpan.FromSeconds(60);

    private readonly KillCounter _killCounter = new();

    private DateTime _sessionStart;
    private long _xpTotal, _lumTotal, _radTotal;
    private int _kills, _deaths;
    private bool _maxLevel;

    // XP per kill counts only XP that lands just after one of YOUR kills (the killer
    // notification goes only to whoever landed the killing blow), plus the XP before a kill
    // found by its corpse (a multi-target spell's extra monsters, KillCounter).
    private long _killXp;
    private int _xpKills;               // kills that closed their window (the per-kill divisor)
    private int _openKills;             // kills whose window is still open
    private DateTime _killWindowUntil;
    private readonly List<(DateTime At, long Xp)> _unattributedXp = new();

    // Luminance.
    private TrackerRise _xpRise, _lumRise, _radRise, _bankedRise, _drawnRise, _bankRise;
    private long _lumOwn;               // "You've banked" amounts, all session
    private long _bankIncome;           // bank rises, deposits and transfers taken out
    private long _ownSinceBank;         // "You've banked" amounts since the bank was first read
    private long _movedIntoBank;        // deposit / transfer amounts waiting for their bank rise
    private DateTime _movedIntoBankUntil;
    private readonly List<(DateTime At, long Amount)> _recentLum = new();
    private long _recentLumSum;

    public SessionStats() => Reset(DateTime.UtcNow);

    public DateTime SessionStartUtc => _sessionStart;
    public long XpTotal => _xpTotal;
    public long LumTotal => _lumTotal;
    public long LumOwn => _lumOwn;
    public long RadTotal => _radTotal;
    public int Kills => _kills;
    public int Deaths => _deaths;
    public bool MaxLevel => _maxLevel;

    /// <summary>True once the ACECustom bank (9005) is read: only then can pass-up be told apart.</summary>
    public bool BankTracked => _bankRise.Baseline.HasValue;

    /// <summary>Silent bank income: allegiance pass-up (and fellowship / Zone Share splits).</summary>
    public long LumPassUp => Math.Max(0, _bankIncome - _ownSinceBank);

    /// <summary>Your kills' average XP (0 before a kill window has closed).</summary>
    public long XpPerKill => _xpKills > 0 ? _killXp / _xpKills : 0;

    /// <summary>A fresh session starting at <paramref name="now"/>; the next read of each property is its baseline.</summary>
    public void Reset(DateTime now)
    {
        _sessionStart = now;
        _xpTotal = _lumTotal = _radTotal = 0;
        _kills = _deaths = 0;
        _maxLevel = false;
        _killXp = 0;
        _xpKills = _openKills = 0;
        _killWindowUntil = DateTime.MinValue;
        _unattributedXp.Clear();
        _killCounter.Reset();
        _xpRise.Clear(); _lumRise.Clear(); _radRise.Clear(); _bankedRise.Clear(); _drawnRise.Clear(); _bankRise.Clear();
        _lumOwn = _bankIncome = _ownSinceBank = 0;
        _movedIntoBank = 0;
        _movedIntoBankUntil = DateTime.MinValue;
        _recentLum.Clear();
        _recentLumSum = 0;
    }

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>The server's killer notification: you landed the killing blow.</summary>
    public void OnKillNotice(string? deathMessage, DateTime now)
    {
        _kills++;
        _openKills++;
        _killWindowUntil = now.AddSeconds(KillWindowSec);
        _killCounter.OnKillNotice(deathMessage, now);
    }

    /// <summary>A chat line: your damage lines (multi-target hits) and the luminance lines.</summary>
    public void OnChat(string? text, DateTime now)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (CombatText.TryParseOutgoingHit(text, out string victim, out _))
        {
            _killCounter.OnHit(victim, now);
            return;
        }
        if (SessionLumChat.TryOwn(text, out long own))
        {
            _lumOwn += own;
            if (BankTracked) _ownSinceBank += own;
            else if (!_bankedRise.Baseline.HasValue) AddLum(own, now);   // no bank property yet: the line is all we have
            return;
        }
        if (SessionLumChat.TryMovedIntoBank(text, out long moved))
        {
            _movedIntoBank += moved;
            _movedIntoBankUntil = now + MovedIntoBankLife;
        }
    }

    /// <summary>A corpse ("Corpse of X", X = <paramref name="monsterName"/>): counts a multi-target spell's extra kill.</summary>
    public KillCounter.CorpseResult OnCorpse(string monsterName, DateTime now)
    {
        var result = _killCounter.OnCorpse(monsterName, now);
        if (result == KillCounter.CorpseResult.Counted) CountCorpseKill(now);
        return result;
    }

    /// <summary>The player's health reached 0.</summary>
    public void OnPlayerDied() => _deaths++;

    private void CountCorpseKill(DateTime now)
    {
        _kills++;
        bool inKillWindow = _openKills > 0 && now <= _killWindowUntil;
        if (inKillWindow) _openKills++;   // closes with the window, its XP already in it
        else _xpKills++;
        for (int i = _unattributedXp.Count - 1; i >= 0; i--)
        {
            if ((now - _unattributedXp[i].At).TotalSeconds > CorpseXpLookbackSec) continue;
            _killXp += _unattributedXp[i].Xp;
            _unattributedXp.RemoveAt(i);
        }
    }

    // ── Polling ──────────────────────────────────────────────────────────────

    /// <summary>One read of the player's properties: adds what went up since the last read.</summary>
    public void Sample(in SessionSample s, DateTime now)
    {
        bool inKillWindow = _openKills > 0 && now <= _killWindowUntil;
        if (s.TotalXp is long xp && xp > 0)
        {
            long gained = _xpRise.Take(xp);
            _xpTotal += gained;
            if (inKillWindow) _killXp += gained;
            else if (gained > 0)
            {
                _unattributedXp.RemoveAll(e => (now - e.At).TotalSeconds > CorpseXpLookbackSec);
                _unattributedXp.Add((now, gained));
            }
            // Capped only when past retail's 275 total AND kills keep bringing nothing: an
            // awakened character's cap is higher (Aelrynth), so the total alone can't say.
            _maxLevel = xp >= MaxLevelXp && _xpKills >= 3 && _xpTotal == 0;
        }
        // Close the window once it has run out: its kills join the per-kill average.
        if (_openKills > 0 && !inKillWindow)
        {
            _xpKills += _openKills;
            _openKills = 0;
        }

        if (now > _movedIntoBankUntil) _movedIntoBank = 0;

        long lumUp = 0;
        // ACECustom's bank first, so the character rise below already knows the bank is tracked.
        if (s.BankLum is long bank && bank >= 0)
        {
            long up = _bankRise.Take(bank);
            long moved = Math.Min(up, _movedIntoBank);
            _movedIntoBank -= moved;
            up -= moved;
            _bankIncome += up;
            lumUp += up;
        }
        // Character luminance: the rise, minus what Aelrynth's bank put there, plus what it took
        // before it landed. On ACECustom a rise is a withdrawal, not income.
        long charUp = 0;
        if (s.AvailableLum is long lum && lum >= 0)
        {
            long up = _lumRise.Take(lum);
            if (!BankTracked) charUp += up;
        }
        if (s.AelDrawn is long drawn) charUp -= _drawnRise.Take(drawn);
        if (s.AelBanked is long banked) charUp += _bankedRise.Take(banked);
        if (charUp > 0) lumUp += charUp;
        if (lumUp > 0) AddLum(lumUp, now);

        if (s.Radiance is long rad) _radTotal += _radRise.Take(rad);
    }

    private void AddLum(long amount, DateTime now)
    {
        _lumTotal += amount;
        _recentLumSum += amount;
        int last = _recentLum.Count - 1;
        if (last >= 0 && now - _recentLum[last].At < RecentLumBucket)
            _recentLum[last] = (_recentLum[last].At, _recentLum[last].Amount + amount);
        else
            _recentLum.Add((now, amount));
        PruneRecent(now);
    }

    /// <summary>Luminance earned in the last five minutes.</summary>
    public long LumLast5Min(DateTime now)
    {
        PruneRecent(now);
        return _recentLumSum;
    }

    private void PruneRecent(DateTime now)
    {
        int drop = 0;
        while (drop < _recentLum.Count && now - _recentLum[drop].At > RecentLumWindow)
        {
            _recentLumSum -= _recentLum[drop].Amount;
            drop++;
        }
        if (drop > 0) _recentLum.RemoveRange(0, drop);
    }

    // ── View ─────────────────────────────────────────────────────────────────

    /// <summary>Totals and per-hour figures at <paramref name="now"/> (the clock floors at one second).</summary>
    public SessionStatsView View(DateTime now)
    {
        double seconds = Math.Max(0, (now - _sessionStart).TotalSeconds);
        double hours = Math.Max(seconds, 1) / 3600.0;
        long passUp = LumPassUp;
        return new SessionStatsView(
            _sessionStart, seconds,
            _xpTotal, _xpTotal / hours,
            _lumTotal, _lumTotal / hours,
            LumLast5Min(now),
            passUp, passUp / hours, BankTracked,
            _kills, _kills / hours,
            XpPerKill,
            _deaths,
            _radTotal, _radTotal / hours,
            _maxLevel);
    }

    // ── Persistence (TrackerSessionStore) ────────────────────────────────────

    /// <summary>Copies the session into <paramref name="s"/> (identity fields are the caller's).</summary>
    public void CaptureInto(TrackerSessionState s, DateTime now)
    {
        PruneRecent(now);
        s.SessionStartUtcTicks = _sessionStart.Ticks;
        s.XpTotal = _xpTotal;
        s.LumTotal = _lumTotal;
        s.RadTotal = _radTotal;
        s.KillsTotal = _kills;
        s.DeathsTotal = _deaths;
        s.KillXp = _killXp;
        s.XpKills = _xpKills;
        s.OpenKills = _openKills;
        s.KillWindowUntilUtcTicks = _killWindowUntil.Ticks;
        s.MaxLevel = _maxLevel;
        s.XpBase = _xpRise.Baseline;
        s.LumBase = _lumRise.Baseline;
        s.RadBase = _radRise.Baseline;
        s.BankedBase = _bankedRise.Baseline;
        s.DrawnBase = _drawnRise.Baseline;
        s.BankLumBase = _bankRise.Baseline;
        s.LumOwn = _lumOwn;
        s.BankIncome = _bankIncome;
        s.OwnSinceBank = _ownSinceBank;
        s.RecentLum.Clear();
        foreach (var (at, amount) in _recentLum) s.RecentLum.Add((at.Ticks, amount));
    }

    /// <summary>Carries on a saved session: the next read counts everything above the saved baselines.</summary>
    public void ApplyFrom(TrackerSessionState s)
    {
        Reset(new DateTime(s.SessionStartUtcTicks, DateTimeKind.Utc));
        _xpTotal = s.XpTotal;
        _lumTotal = s.LumTotal;
        _radTotal = s.RadTotal;
        _kills = s.KillsTotal;
        _deaths = s.DeathsTotal;
        _killXp = s.KillXp;
        _xpKills = s.XpKills;
        _openKills = s.OpenKills;   // an expired window closes on the first poll, as usual
        _killWindowUntil = new DateTime(s.KillWindowUntilUtcTicks, DateTimeKind.Utc);
        _maxLevel = s.MaxLevel;
        _xpRise.Restore(s.XpBase);
        _lumRise.Restore(s.LumBase);
        _radRise.Restore(s.RadBase);
        _bankedRise.Restore(s.BankedBase);
        _drawnRise.Restore(s.DrawnBase);
        _bankRise.Restore(s.BankLumBase);
        _lumOwn = s.LumOwn;
        _bankIncome = s.BankIncome;
        _ownSinceBank = s.OwnSinceBank;
        foreach (var (at, amount) in s.RecentLum)
        {
            _recentLum.Add((new DateTime(at, DateTimeKind.Utc), amount));
            _recentLumSum += amount;
        }
    }
}

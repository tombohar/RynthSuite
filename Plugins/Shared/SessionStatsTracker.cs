// ============================================================================
//  RynthSuite - Plugins/Shared/SessionStatsTracker.cs
//  Feeds a SessionStats from the game: the player's properties every 0.5 s,
//  kill notices, chat, corpses and the player's health, and keeps the session
//  across plugin reloads (TrackerSessionStore). RynthTracker and RynthAi's
//  Mini Remote each own one, so both show the same numbers (2026-10-11).
//
//  Threading: every call except RequestReset and View runs on the pump
//  thread. RequestReset only sets a flag (any thread); View is an immutable
//  record swapped in after each poll (any thread).
// ============================================================================
using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared;

internal sealed class SessionStatsTracker
{
    // The player's own Int64 properties (the engine serves them to the pump since 2026.9.29.3).
    private const uint Int64TotalExperience = 1;
    private const uint Int64AvailableLuminance = 6;
    private const uint Int64BankedLuminance = 9005;    // ACECustom bank
    private const uint Int64RadianceEarned = 9101;     // Aelrynth Bank mod session totals
    private const uint Int64LuminanceBanked = 9102;
    private const uint Int64LuminanceDrawn = 9103;

    private const long PollIntervalMs = 500;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(30);
    // Corpses waiting for their name (the client may not have it on the create tick).
    private const double CorpseNameWaitSec = 3.0;
    private const int MaxPendingCorpses = 256;
    private const int MaxCorpseLogLines = 40;

    private readonly RynthCoreHost _host;
    private readonly string _tag;
    private readonly string _saveDirectory;
    private readonly SessionStats _stats = new();
    private readonly List<(uint Id, DateTime Seen)> _pendingCorpses = new();

    private uint _playerId;
    private bool _loggedIn;         // between OnLoginComplete and OnLogout
    private bool _active;           // logged in and the session started (player id known)
    private volatile bool _pendingReset;
    private volatile SessionStatsView _view = SessionStatsView.Empty;
    private long _nextPollMs;
    private DateTime _nextSaveUtc;
    private bool _saveFailedLogged;
    private int _corpseLogCount;

    /// <param name="tag">Log prefix, e.g. "[RynthTracker]".</param>
    /// <param name="saveDirectory">This plugin's own session folder (TrackerSessionStore.DirectoryFor).</param>
    public SessionStatsTracker(RynthCoreHost host, string tag, string saveDirectory)
    {
        _host = host;
        _tag = tag;
        _saveDirectory = saveDirectory;
    }

    /// <summary>The latest numbers (any thread). Zeros before login and after logout.</summary>
    public SessionStatsView View => _view;

    /// <summary>True while a session is running (pump thread).</summary>
    public bool Active => _active;

    /// <summary>Starts a fresh session on the next tick (any thread).</summary>
    public void RequestReset() => _pendingReset = true;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// The engine calls OnLoginComplete on a real login AND on a freshly (re)loaded plugin while
    /// already in game, so the saved file is what tells a reload from a new login. A player id
    /// that isn't readable yet starts the session on the first tick that reads it.
    /// </summary>
    public void OnLoginComplete()
    {
        _loggedIn = true;
        _active = false;
        _playerId = _host.GetPlayerId();
        if (_playerId != 0) Start();
    }

    /// <summary>A real logout ends the session: the next login starts fresh, never picks this up.</summary>
    public void OnLogout()
    {
        if (_active) DeleteSavedSession();
        _loggedIn = false;
        _active = false;
        _pendingCorpses.Clear();
        _view = SessionStatsView.Empty;
        _host.Log($"{_tag} Session ended.");
    }

    /// <summary>
    /// Plugin unload (hot reload, RynthSuite update, client shutting down). Saves only while a
    /// session runs, never deletes: a copy unloaded before its OnLoginComplete must not wipe the
    /// save its predecessor left.
    /// </summary>
    public void Shutdown()
    {
        if (_active) SaveSession();
    }

    private void Start()
    {
        DateTime now = DateTime.UtcNow;
        if (TryRestoreSession())
        {
            _host.Log($"{_tag} Session carried over from before the reload.");
        }
        else
        {
            _stats.Reset(now);
            CleanupSavedSessions();
            _host.Log($"{_tag} Session started.");
        }
        _active = true;
        _nextSaveUtc = now + SaveInterval;
        _nextPollMs = 0;
        _view = _stats.View(now);
    }

    // ── Events (pump thread) ─────────────────────────────────────────────────

    /// <summary>The server's killer notification (not chat): you landed the killing blow.</summary>
    public void OnKillNotification(string? deathMessage)
    {
        if (_active) _stats.OnKillNotice(deathMessage, DateTime.UtcNow);
    }

    /// <summary>Damage lines (multi-target hits) and the luminance lines.</summary>
    public void OnChatWindowText(string? text)
    {
        if (_active) _stats.OnChat(text, DateTime.UtcNow);
    }

    /// <summary>Corpses are read on the tick (their name may not be readable yet on the create).</summary>
    public void OnCreateObject(uint objectId)
    {
        if (!_active || objectId == 0) return;
        if (_pendingCorpses.Count >= MaxPendingCorpses) _pendingCorpses.RemoveAt(0);
        _pendingCorpses.Add((objectId, DateTime.UtcNow));
    }

    public void OnDeleteObject(uint objectId)
    {
        for (int i = _pendingCorpses.Count - 1; i >= 0; i--)
            if (_pendingCorpses[i].Id == objectId) _pendingCorpses.RemoveAt(i);
    }

    /// <summary>The player's health reaching 0 is a death (maxHealth &gt; 0 skips spurious 0/0 packets at login).</summary>
    public void OnUpdateHealth(uint targetId, uint currentHealth, uint maxHealth)
    {
        if (_active && targetId == _playerId && currentHealth == 0 && maxHealth > 0)
            _stats.OnPlayerDied();
    }

    // ── Tick (pump thread) ───────────────────────────────────────────────────

    public void Tick()
    {
        DateTime now = DateTime.UtcNow;
        if (_pendingReset)
        {
            _pendingReset = false;
            _stats.Reset(now);
            _pendingCorpses.Clear();
            _view = _active ? _stats.View(now) : SessionStatsView.Empty;
            // Save the zeros now, so a reload right after a reset can't bring the old totals back.
            if (_active) { SaveSession(); _nextSaveUtc = now + SaveInterval; }
            _host.Log($"{_tag} Session reset.");
        }

        if (_loggedIn && !_active)
        {
            _playerId = _host.GetPlayerId();
            if (_playerId == 0) return;
            Start();
        }
        if (!_active) return;

        CheckPendingCorpses(now);

        long nowMs = Environment.TickCount64;
        if (nowMs < _nextPollMs) return;
        _nextPollMs = nowMs + PollIntervalMs;
        Poll(now);
        _view = _stats.View(now);

        // Saved right after a poll, so totals and baselines in the file always match.
        if (now >= _nextSaveUtc)
        {
            _nextSaveUtc = now + SaveInterval;
            SaveSession();
        }
    }

    private void Poll(DateTime now)
    {
        if (_playerId == 0 || !_host.HasGetObjectQuadProperty) return;
        _stats.Sample(new SessionSample(
            Read(Int64TotalExperience),
            Read(Int64AvailableLuminance),
            Read(Int64BankedLuminance),
            Read(Int64LuminanceBanked),
            Read(Int64LuminanceDrawn),
            Read(Int64RadianceEarned)), now);
    }

    private long? Read(uint stype)
        => _host.TryGetObjectQuadProperty(_playerId, stype, out long value) ? value : null;

    /// <summary>Reads the names of newly created objects; a "Corpse of X" goes to the kill counter.</summary>
    private void CheckPendingCorpses(DateTime now)
    {
        if (_pendingCorpses.Count == 0) return;
        if (!_host.HasGetObjectName) { _pendingCorpses.Clear(); return; }
        for (int i = _pendingCorpses.Count - 1; i >= 0; i--)
        {
            var (id, seen) = _pendingCorpses[i];
            if (!_host.TryGetObjectName(id, out string name) || string.IsNullOrEmpty(name))
            {
                if ((now - seen).TotalSeconds > CorpseNameWaitSec) _pendingCorpses.RemoveAt(i);
                continue;
            }
            _pendingCorpses.RemoveAt(i);
            string? monster = CombatText.CorpseOwnerName(name);
            if (monster == null) continue;
            var result = _stats.OnCorpse(monster, now);
            if ((result == KillCounter.CorpseResult.Counted || result == KillCounter.CorpseResult.HitNotCounted)
                && _corpseLogCount < MaxCorpseLogLines)
            {
                _corpseLogCount++;
                _host.Log(result == KillCounter.CorpseResult.Counted
                    ? $"{_tag} Kill counted from its corpse (no kill notice): '{name}' 0x{id:X8}, hit by a multi-target spell. Kills {_stats.Kills}."
                    : $"{_tag} Not counted: '{name}' 0x{id:X8} - you hit it, single-target, and got no kill notice (someone else's kill?).");
            }
        }
    }

    // ── Session persistence ──────────────────────────────────────────────────
    //
    // Reload-gap rule: the Rise baselines are restored as saved, so the first read after a
    // reload counts everything from the last save up to now, which includes what was earned
    // while the plugin was unloaded. Totals and baselines are saved together after the same
    // poll, so that span is counted exactly once. (Kills in the gap aren't seen, so the gap's
    // XP joins the totals and XP/hour but not XP per kill.)

    // The client's start time, so a new client that happens to get a closed one's process
    // id never picks up its save. 0 if Windows won't say (both sides then match on id alone).
    private static long? _processStartTicks;
    private static long ProcessStartTicks
    {
        get
        {
            if (_processStartTicks is long t) return t;
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                t = p.StartTime.ToUniversalTime().Ticks;
            }
            catch { t = 0; }
            _processStartTicks = t;
            return t;
        }
    }

    private void SaveSession()
    {
        if (_playerId == 0) return;
        try
        {
            DateTime now = DateTime.UtcNow;
            var s = new TrackerSessionState
            {
                ProcessId = Environment.ProcessId,
                ProcessStartUtcTicks = ProcessStartTicks,
                CharacterId = _playerId,
                SavedUtcTicks = now.Ticks,
            };
            _stats.CaptureInto(s, now);
            TrackerSessionStore.Save(_saveDirectory, s);
            _saveFailedLogged = false;
        }
        catch (Exception ex)
        {
            // Every 30 s otherwise; one line until a save works again.
            if (!_saveFailedLogged)
            {
                _saveFailedLogged = true;
                _host.Log($"{_tag} Couldn't save the session: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private bool TryRestoreSession()
    {
        if (_playerId == 0) return false;
        try
        {
            int pid = Environment.ProcessId;
            if (!TrackerSessionStore.TryLoad(_saveDirectory, pid, _playerId, out TrackerSessionState s))
                return false;
            if (!TrackerSessionStore.ShouldRestore(s, pid, ProcessStartTicks, _playerId, DateTime.UtcNow))
                return false;
            _stats.ApplyFrom(s);
            return true;
        }
        catch (Exception ex)
        {
            _host.Log($"{_tag} Couldn't read the saved session, starting fresh: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void DeleteSavedSession()
    {
        if (_playerId == 0) return;
        try { TrackerSessionStore.Delete(_saveDirectory, Environment.ProcessId, _playerId); }
        catch (Exception ex) { _host.Log($"{_tag} Couldn't delete the saved session: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>A fresh session: this client's older saves (any character) and files left by clients closed long ago go.</summary>
    private void CleanupSavedSessions()
    {
        try { TrackerSessionStore.Cleanup(_saveDirectory, Environment.ProcessId, DateTime.UtcNow); }
        catch { }
    }
}

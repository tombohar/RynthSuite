using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

// ── Ghost creatures (Drakkon 2026-10-12, Olthoi in 0x6146) ───────────────────────────────────
// A ghost is an object the client still draws but the server no longer has for this player.
// Drakkon stood for 25+ minutes swinging at four of them: the engine's flight recorder shows every
// MeleeAttack answered within ~35 ms by AttackDone ActionCancelled, no CombatCommenceAttack, no
// motion; the "monsters" never moved, never died, and the server sent nothing about them after the
// character walked past at 20:58:15. A real Olthoi that walked up at 21:27 was killed in 6 s. The
// no-damage blacklist dropped each ghost for 30 s, so with four of them around the bot went round
// and round and navigation never got a turn.

/// <summary>A point in landblock-global coordinates (x/y across landblocks, z as is).</summary>
internal readonly record struct GhostSpot(double X, double Y, double Z)
{
    /// <summary>Movement (m) that means the object is alive on the server.</summary>
    public const double MovedM = 0.05;

    public bool MovedFrom(GhostSpot other)
    {
        double dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
        return dx * dx + dy * dy + dz * dz > MovedM * MovedM;
    }
}

/// <summary>
/// The verdict on one stalled target, armed when the stall watchdog's step 1 (CancelAttack + attack
/// again) goes out. Ghost once <see cref="AnswerWaitMs"/> passed with no swing evidence since the
/// re-issue, the same target, not moved, and the server silent about it for <see cref="NoUpdateMs"/>
/// (a monster fighting anyone nearby gets motion updates every few seconds; even an idle one
/// answers an attack with a turn and a swing). Pure.
/// </summary>
internal sealed class GhostCheck
{
    public const double AnswerWaitMs = 1500;
    public const double NoUpdateMs = 8000;

    public enum Verdict { None, Alive, Ghost }

    private int _target;
    private DateTime _at;
    private GhostSpot? _spot;

    public bool IsArmed => _target != 0;

    public void Arm(int targetId, DateTime now, GhostSpot? spot)
    {
        if (targetId == 0 || spot == null) { Disarm(); return; }
        _target = targetId;
        _at = now;
        _spot = spot;
    }

    public void Disarm()
    {
        _target = 0;
        _spot = null;
    }

    /// <summary>
    /// <paramref name="serverSilentMs"/>: how long the server has said nothing about the target
    /// (-1 = unknown: no record since the engine and the plugin started watching, which for an
    /// object the client already had means nothing either).
    /// </summary>
    public Verdict Check(int targetId, DateTime now, DateTime lastEvidenceAt, GhostSpot? spot, double serverSilentMs)
    {
        if (_target == 0) return Verdict.None;
        if (targetId != _target || lastEvidenceAt > _at || spot == null || spot.Value.MovedFrom(_spot!.Value)
            || (serverSilentMs >= 0 && serverSilentMs < (now - _at).TotalMilliseconds))
        {
            Disarm();
            return Verdict.Alive;   // a swing, a move, a server message since the re-issue, or another target
        }
        if ((now - _at).TotalMilliseconds < AnswerWaitMs) return Verdict.None;
        Disarm();
        return serverSilentMs < 0 || serverSilentMs >= NoUpdateMs ? Verdict.Ghost : Verdict.Alive;
    }
}

/// <summary>
/// The ghosts found this session. A ghost is skipped by targeting until the server shows it is
/// real after all (any update or create for its id, or it moves) or the character changes
/// landblock - not for a fixed time. Its server-update clock is kept here too, for engines before
/// plugin API v83 (no GetObjectServerAge). Pump thread only.
/// </summary>
internal sealed class GhostTracker
{
    public readonly record struct Ghost(int Id, string Name, GhostSpot Spot, uint Landblock, DateTime MarkedAt);

    private readonly Dictionary<int, Ghost> _ghosts = new();
    private readonly Dictionary<int, DateTime> _lastUpdate = new();
    private readonly List<int> _scratch = new();
    private const int MaxUpdateEntries = 8192;

    /// <summary>Called with (id, why) when a ghost turns out real or is forgotten.</summary>
    public Action<int, string>? Cleared;

    public int Count => _ghosts.Count;
    public IEnumerable<Ghost> All => _ghosts.Values;
    public int MarkedTotal { get; private set; }

    public bool IsGhost(int id) => id != 0 && _ghosts.ContainsKey(id);

    public void Mark(int id, string name, GhostSpot spot, uint landblock, DateTime now)
    {
        if (id == 0) return;
        if (!_ghosts.ContainsKey(id)) MarkedTotal++;
        _ghosts[id] = new Ghost(id, name, spot, landblock, now);
    }

    /// <summary>The server sent something about <paramref name="id"/> (OnUpdateObject / OnCreateObject).</summary>
    public void NoteServerUpdate(int id, DateTime now, string what = "the server sent an update for it")
    {
        if (id == 0) return;
        if (_lastUpdate.Count >= MaxUpdateEntries && !_lastUpdate.ContainsKey(id)) _lastUpdate.Clear();
        _lastUpdate[id] = now;
        if (_ghosts.Remove(id)) Cleared?.Invoke(id, what);
    }

    public void NoteDeleted(int id)
    {
        _lastUpdate.Remove(id);
        _ghosts.Remove(id);
    }

    /// <summary>Milliseconds since the plugin last saw a server update for <paramref name="id"/>, -1 none.</summary>
    public double SilentMs(int id, DateTime now)
        => _lastUpdate.TryGetValue(id, out DateTime t) ? Math.Max(0, (now - t).TotalMilliseconds) : -1;

    /// <summary>Ghosts that moved are real: cleared. <paramref name="spotOf"/> null = no position.</summary>
    public void CheckMoved(Func<int, GhostSpot?> spotOf)
    {
        if (_ghosts.Count == 0) return;
        _scratch.Clear();
        foreach (Ghost g in _ghosts.Values)
            if (spotOf(g.Id) is GhostSpot s && s.MovedFrom(g.Spot)) _scratch.Add(g.Id);
        foreach (int id in _scratch)
        {
            _ghosts.Remove(id);
            Cleared?.Invoke(id, "it moved");
        }
    }

    /// <summary>The character is in <paramref name="landblock"/> (high 16 bits of the cell): ghosts
    /// marked in another landblock are let go.</summary>
    public void OnPlayerLandblock(uint landblock)
    {
        if (_ghosts.Count == 0 || landblock == 0) return;
        _scratch.Clear();
        foreach (Ghost g in _ghosts.Values)
            if (g.Landblock != landblock) _scratch.Add(g.Id);
        foreach (int id in _scratch)
        {
            _ghosts.Remove(id);
            Cleared?.Invoke(id, "the character changed landblock");
        }
    }

    public void Clear()
    {
        _ghosts.Clear();
        _lastUpdate.Clear();
    }
}

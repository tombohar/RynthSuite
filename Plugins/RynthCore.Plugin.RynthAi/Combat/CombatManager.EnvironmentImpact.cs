using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Environment-impact throttle (2026-10-12). Rules and bookkeeping in EnvironmentImpact.cs.
///
///   missile  "Your missile attack hit the environment." in Missile mode counts one hit on the
///            target the server's attack is bound to.
///   magic    each single-target war/void cast is watched: our projectiles (named like the
///            shape, "Flame Bolt") are claimed as they appear next to the player, followed until
///            they stop or vanish, and the cast counts one hit when every projectile stopped
///            short of the target (ProjectileImpactJudge) and no hit, resist or evade line came
///            back for it. Rings and walls aren't watched: they don't fly at the target.
///
/// At EnvironmentImpactLimit hits within the window the throttle trips: the server's attack is
/// released (a magic cast in flight finishes first) and the chosen response runs: an approach
/// (autorun toward the target, at most EnvironmentApproachMeters, never nearer than
/// EnvironmentMinDistance, bounded in time and by progress), or a new target (this one skipped
/// for EnvironmentSkipSec). Off = no detection at all.
/// </summary>
public partial class CombatManager
{
    /// <summary>Clock for the throttle and the projectile watch; tests replace it.</summary>
    internal Func<DateTime> EnvClock { get; set; } = () => RynthClock.Now;

    internal EnvironmentImpactThrottle EnvThrottle { get; } = new();

    /// <summary>The start of the chat line ACE sends the shooter (ProjectileCollisionHelper.OnCollideEnvironment,
    /// a Broadcast line). Matched at the line's start only: a player's copy has their name in front.</summary>
    internal const string MissileEnvironmentText = "Your missile attack hit the environment";

    // A cast's projectiles appear once its windup ends (up to ~2.5 s for a high tier).
    private const double CastClaimWindowMs = 4000;
    // Our projectile spawns at the caster; farther than this at its first sight it isn't ours.
    private const double ClaimRadiusMeters = 8.0;
    // Still this long after flying counts as stopped (ACE zeroes its velocity on impact).
    private const double StoppedForMs = 250;
    private const double StillMeters = 0.05;
    // A projectile still flying after this is dropped from the watch (no verdict).
    private const double ProjectileMaxFlightMs = 8000;
    // After the last projectile stops, a hit/resist/evade line still has this long to arrive.
    private const double ImpactEvidenceGraceMs = 1000;
    private const int MaxCastWatches = 4;
    // An approach that hasn't gained this much ground in ApproachNoProgressMs is blocked.
    private const double ApproachProgressMeters = 0.5;
    private const double ApproachNoProgressMs = 2500;
    private const double ApproachMaxMs = 8000;
    private const double ApproachRunRefreshMs = 1000;
    private const double ApproachTurnRefreshMs = 300;
    private const double ApproachFaceToleranceDeg = 10;
    // Think stopped driving the approach (buffing took over, out of range): let go of autorun.
    private const double ApproachOrphanMs = 1500;

    private enum EnvKind { Missile, Magic }

    private sealed class TrackedProjectile
    {
        public uint Id;
        public double StartX, StartY, StartZ;
        public double LastX, LastY, LastZ;
        public DateTime FirstSeen, LastMoved;
        public double Travel;
        public bool Resolved, Short;
    }

    private sealed class CastWatch
    {
        public int TargetId;
        public string SpellName = "";
        public DateTime CastAt;
        public double CasterX, CasterY, CasterZ;
        public bool HasCasterPos;
        public readonly List<TrackedProjectile> Projectiles = new();
        public DateTime LastImpactAt = DateTime.MinValue;
    }

    // Creates are queued and claimed by the heartbeat poll (the name lands a moment later).
    private readonly ConcurrentQueue<(uint Id, DateTime At)> _envCreated = new();
    private readonly List<(uint Id, DateTime At)> _envCandidates = new();
    private readonly List<CastWatch> _castWatches = new();
    private readonly Dictionary<int, DateTime> _envSkipUntil = new();
    private DateTime _envLastEvidenceAt = DateTime.MinValue;

    // The trip waiting for Think, and the approach in progress.
    private int _envPendingTargetId;
    private EnvKind _envPendingKind;
    private int _envApproachTargetId;
    private EnvKind _envApproachKind;
    private double _envApproachGoal, _envApproachBestDist;
    private DateTime _envApproachStartedAt, _envApproachProgressAt, _envApproachRunAt, _envApproachTurnAt, _envApproachTickAt;
    private bool _envApproachUsedMotions;

    /// <summary>True while combat is walking toward a target for its environment hits.</summary>
    internal bool EnvironmentApproachActive => _envApproachTargetId != 0;

    private EnvironmentResponse ResponseFor(EnvKind kind) => EnvironmentImpactPlanner.Parse(
        kind == EnvKind.Missile ? _settings.MissileEnvironmentResponse : _settings.MagicEnvironmentResponse);

    // ── Signals ────────────────────────────────────────────────────────────────

    /// <summary>A new object. Only queued while the macro runs with magic detection on
    /// (the heartbeat poll that drains the queue runs only then). Separate from OnObjectCreated,
    /// which feeds the ghost tracker on every create.</summary>
    public void OnEnvironmentObjectCreated(uint objectId)
    {
        if (objectId == 0 || _settings.MagicEnvironmentResponse == 0 || !_settings.IsMacroRunning) return;
        _envCreated.Enqueue((objectId, EnvClock()));
    }

    /// <summary>From OnObjectDeleted: a projectile that vanished has hit something, and a
    /// monster id that's gone forgets its hits, approaches and skip.</summary>
    private void OnEnvironmentObjectDeleted(uint objectId)
    {
        int id = (int)objectId;
        EnvThrottle.Forget(id);
        _envSkipUntil.Remove(id);
        if (_castWatches.Count == 0) return;
        DateTime now = EnvClock();
        foreach (var w in _castWatches)
            foreach (var p in w.Projectiles)
                if (p.Id == objectId && !p.Resolved) ResolveProjectile(w, p, now);
    }

    /// <summary>The missile line, from HandleChatForDamage. True when it was that line.</summary>
    private bool NoteMissileEnvironmentChat(string text)
    {
        if (!ChatSource.StartsWithMarker(text, MissileEnvironmentText, StringComparison.OrdinalIgnoreCase)) return false;
        if (ResponseFor(EnvKind.Missile) == EnvironmentResponse.Off) return true;
        if (CurrentCombatMode != CombatMode.Missile) return true;
        int target = _serverAttackTargetId != 0 ? _serverAttackTargetId : activeTargetId;
        if (target != 0) RecordEnvironmentImpact(target, EnvKind.Missile, "missile hit the environment");
        return true;
    }

    /// <summary>Any sign an attack reached a monster (hit, resist, evade, a health drop):
    /// a cast with one of these after it didn't only hit the environment.</summary>
    private void NoteEnvironmentEvidence() => _envLastEvidenceAt = EnvClock();

    /// <summary>A single-target cast just went out (AttackWithMagic): watch its projectiles.</summary>
    internal void RegisterCastWatch(int targetId, string spellName)
    {
        if (ResponseFor(EnvKind.Magic) == EnvironmentResponse.Off || targetId == 0) return;
        DateTime now = EnvClock();
        var w = new CastWatch { TargetId = targetId, SpellName = spellName, CastAt = now };
        if (TryGetPlayerGlobal(out double px, out double py, out double pz))
        {
            w.CasterX = px; w.CasterY = py; w.CasterZ = pz; w.HasCasterPos = true;
        }
        if (_castWatches.Count >= MaxCastWatches) _castWatches.RemoveAt(0);
        _castWatches.Add(w);
    }

    /// <summary>Counts one environment hit; at the limit, arms the response for Think.</summary>
    private void RecordEnvironmentImpact(int targetId, EnvKind kind, string detail)
    {
        DateTime now = EnvClock();
        int limit = Math.Max(1, _settings.EnvironmentImpactLimit);
        int window = Math.Max(1, _settings.EnvironmentImpactWindowSec);
        int n = EnvThrottle.Record(targetId, now, window);
        string name = _worldFilter[targetId]?.Name ?? "";
        _host.Log($"[EnvImpact] {kind.ToString().ToLowerInvariant()}: {detail} â€” 0x{(uint)targetId:X8} '{name}' {n}/{limit} in {window}s");
        if (n < limit || targetId != activeTargetId) return;
        if (_envApproachTargetId == targetId) return;   // already moving closer to it
        _envPendingTargetId = targetId;
        _envPendingKind = kind;
    }

    // ── Heartbeat: projectile watch and the approach's safety net ─────────────

    /// <summary>Every heartbeat while the macro runs (pump thread).</summary>
    internal void PollEnvironmentImpact()
    {
        DateTime now = EnvClock();

        if (_settings.MagicEnvironmentResponse == 0)
        {
            while (_envCreated.TryDequeue(out _)) { }
            _envCandidates.Clear();
            _castWatches.Clear();
        }
        else
        {
            while (_envCreated.TryDequeue(out var c)) _envCandidates.Add(c);
            ClaimProjectiles(now);
            TrackProjectiles(now);
            JudgeCastWatches(now);
        }

        // Think drives the approach; when it stops (another activity, out of range, combat off)
        // or the deadline passes, autorun must not be left on.
        if (_envApproachTargetId != 0
            && ((now - _envApproachTickAt).TotalMilliseconds > ApproachOrphanMs
                || (now - _envApproachStartedAt).TotalMilliseconds > ApproachMaxMs + ApproachOrphanMs
                || !_settings.EnableCombat))
            EndEnvironmentApproach("combat stopped driving it");

        // Same for the re-sync sidestep's movement key: Think releases it after ResyncStepMs,
        // but only while it keeps ticking in magic mode.
        if (_resyncState == 2 && (TargetClock() - _resyncAt).TotalMilliseconds > ResyncStepMs + ApproachOrphanMs)
            AbandonResync("combat stopped driving it");
    }

    private void ClaimProjectiles(DateTime now)
    {
        if (_envCandidates.Count == 0) return;
        bool havePlayer = TryGetPlayerGlobal(out double px, out double py, out double pz);
        for (int i = _envCandidates.Count - 1; i >= 0; i--)
        {
            var (id, at) = _envCandidates[i];
            string name = _worldFilter[(int)id]?.Name ?? "";
            if (name.Length == 0 && _host.TryGetObjectName(id, out string n2)) name = n2;
            if (name.Length == 0)
            {
                // The name lands a moment after the create; give it a second.
                if ((now - at).TotalMilliseconds > 1000) _envCandidates.RemoveAt(i);
                continue;
            }
            _envCandidates.RemoveAt(i);
            if (!IsWatchedProjectileName(name)) continue;
            if (!havePlayer || !TryGetObjectGlobal(id, out double x, out double y, out double z)) continue;
            if (Dist(px, py, pz, x, y, z) > ClaimRadiusMeters) continue;

            // The newest cast still taking projectiles (a blast's several share one cast).
            CastWatch? owner = null;
            for (int w = _castWatches.Count - 1; w >= 0; w--)
            {
                double age = (at - _castWatches[w].CastAt).TotalMilliseconds;
                if (age >= -50 && age <= CastClaimWindowMs) { owner = _castWatches[w]; break; }
            }
            if (owner == null) continue;
            owner.Projectiles.Add(new TrackedProjectile
            {
                Id = id, StartX = x, StartY = y, StartZ = z, LastX = x, LastY = y, LastZ = z,
                FirstSeen = now, LastMoved = now,
            });
        }
    }

    /// <summary>A war/void projectile: the shape names (SpellShapes/VoidSpellShapes), or any
    /// name ending in a flying shape (lore-named projectiles of the same shapes).</summary>
    internal static bool IsWatchedProjectileName(string name)
    {
        if (IsSpellProjectileName(name)) return !name.EndsWith(" Ring", StringComparison.OrdinalIgnoreCase);
        return name.EndsWith(" Bolt", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(" Streak", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(" Arc", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(" Blast", StringComparison.OrdinalIgnoreCase);
    }

    private void TrackProjectiles(DateTime now)
    {
        foreach (var w in _castWatches)
        {
            foreach (var p in w.Projectiles)
            {
                if (p.Resolved) continue;
                if ((now - p.FirstSeen).TotalMilliseconds > ProjectileMaxFlightMs)
                {
                    p.Resolved = true;   // no verdict: treated as not short
                    continue;
                }
                if (!TryGetObjectGlobal(p.Id, out double x, out double y, out double z)) continue;
                if (Dist(x, y, z, p.LastX, p.LastY, p.LastZ) > StillMeters)
                {
                    p.LastX = x; p.LastY = y; p.LastZ = z;
                    p.LastMoved = now;
                    p.Travel = Math.Max(p.Travel, Dist(x, y, z, p.StartX, p.StartY, p.StartZ));
                    continue;
                }
                if (p.Travel >= ProjectileImpactJudge.MinTravelMeters
                    && (now - p.LastMoved).TotalMilliseconds >= StoppedForMs)
                    ResolveProjectile(w, p, now);
            }
        }
    }

    private void ResolveProjectile(CastWatch w, TrackedProjectile p, DateTime now)
    {
        p.Resolved = true;
        w.LastImpactAt = now;
        if (p.Travel < ProjectileImpactJudge.MinTravelMeters) return;
        if (!TryGetObjectGlobal((uint)w.TargetId, out double tx, out double ty, out double tz)) return;
        double cx = w.CasterX, cy = w.CasterY, cz = w.CasterZ;
        if (!w.HasCasterPos) { cx = p.StartX; cy = p.StartY; cz = p.StartZ; }
        p.Short = ProjectileImpactJudge.StoppedShort(
            Dist(p.LastX, p.LastY, p.LastZ, tx, ty, tz),
            Dist(cx, cy, cz, p.LastX, p.LastY, p.LastZ),
            Dist(cx, cy, cz, tx, ty, tz));
    }

    private void JudgeCastWatches(DateTime now)
    {
        for (int i = _castWatches.Count - 1; i >= 0; i--)
        {
            var w = _castWatches[i];
            double age = (now - w.CastAt).TotalMilliseconds;
            if (w.Projectiles.Count == 0)
            {
                if (age > CastClaimWindowMs + 500) _castWatches.RemoveAt(i);   // refused or never seen
                continue;
            }
            // A blast's projectiles all appear together; wait out the claim window only briefly.
            bool allResolved = w.Projectiles.TrueForAll(p => p.Resolved);
            if (!allResolved) continue;
            if ((now - w.LastImpactAt).TotalMilliseconds < ImpactEvidenceGraceMs) continue;
            _castWatches.RemoveAt(i);

            bool reached = _envLastEvidenceAt >= w.CastAt;
            bool allShort = w.Projectiles.TrueForAll(p => p.Short);
            if (!reached && allShort)
                RecordEnvironmentImpact(w.TargetId, EnvKind.Magic,
                    $"'{w.SpellName}' stopped short of the target ({w.Projectiles.Count} projectile{(w.Projectiles.Count == 1 ? "" : "s")})");
        }
    }

    // ── Think: the response ───────────────────────────────────────────────────

    /// <summary>
    /// In ThinkCore once the target is valid and in reach. True = this tick belongs to the
    /// response (an approach under way, or the target just switched): no attack.
    /// </summary>
    internal bool RunEnvironmentImpact(WorldObject targetObj, double currentDist)
    {
        DateTime now = EnvClock();
        if (_envApproachTargetId != 0)
        {
            if (_envApproachTargetId == activeTargetId)
                return ContinueEnvironmentApproach(targetObj, currentDist, now);
            EndEnvironmentApproach("the target changed");
        }

        if (_envPendingTargetId == 0) return false;
        if (_envPendingTargetId != activeTargetId) { _envPendingTargetId = 0; return false; }
        EnvKind kind = _envPendingKind;
        // The cast in flight finishes first: turning or running now would cancel it.
        if (kind == EnvKind.Magic && IsCastInFlight()) return true;
        // A re-sync sidestep already holding a movement key finishes first: running or turning
        // over it would send two movement states at once.
        if (_resyncState == 2) return true;

        int id = activeTargetId;
        _envPendingTargetId = 0;
        EnvThrottle.ResetHits(id);
        EnvironmentResponse response = ResponseFor(kind);
        int minDist = Math.Max(1, _settings.EnvironmentMinDistance);
        EnvironmentAction action = EnvironmentImpactPlanner.Decide(response, EnvThrottle.Approaches(id),
            currentDist, minDist, HasAlternateTarget(id));
        string who = $"0x{(uint)id:X8} '{targetObj.Name}' at {currentDist:0.0}m";
        string why = kind == EnvKind.Missile ? "missiles" : "spells";

        switch (action)
        {
            case EnvironmentAction.NewTarget:
            {
                if (_serverAttackTargetId != 0) ReleaseServerAttack("environment hits", log: false);
                int skip = Math.Max(1, _settings.EnvironmentSkipSec);
                _envSkipUntil[id] = now.AddSeconds(skip);
                _host.Log($"[EnvImpact] {why} keep hitting the environment: leaving {who}, skipping it {skip}s ({EnvironmentImpactPlanner.Name((int)response)})");
                SupersedeResync("a new target");
                ForgetScanned(id);
                DropTarget("environment hits: new target");
                return true;
            }
            case EnvironmentAction.Approach:
            {
                if (_serverAttackTargetId != 0) ReleaseServerAttack("environment hits", log: false);
                double goal = EnvironmentImpactPlanner.ApproachGoal(currentDist, _settings.EnvironmentApproachMeters, minDist);
                EnvThrottle.NoteApproach(id);
                _envApproachTargetId = id;
                _envApproachKind = kind;
                _envApproachGoal = goal;
                _envApproachBestDist = currentDist;
                _envApproachStartedAt = _envApproachProgressAt = _envApproachTickAt = now;
                _envApproachRunAt = _envApproachTurnAt = DateTime.MinValue;
                _envApproachUsedMotions = false;
                _host.Log($"[EnvImpact] {why} keep hitting the environment: moving closer to {who}, stopping at {goal:0.0}m ({EnvironmentImpactPlanner.Name((int)response)})");
                SupersedeResync("moving closer");
                return ContinueEnvironmentApproach(targetObj, currentDist, now);
            }
            default:
                _host.Log($"[EnvImpact] {why} keep hitting the environment on {who}, but it's the only monster in reach and already close: attacking on");
                return false;
        }
    }

    /// <summary>
    /// The throttle acted on misses the magic re-sync (TickResyncStep) was also counting. The
    /// misses are explained (a wall in the way), so the re-sync's count starts over, and a
    /// sidestep that is wanted but not yet started is dropped. Moving closer is itself real
    /// movement, which re-syncs the server's copy of our position anyway.
    /// </summary>
    private void SupersedeResync(string by)
    {
        _magicMissesSinceHit = 0;
        if (_resyncState != 1) return;
        _resyncState = 0;
        _host.Log($"[EnvImpact] re-sync sidestep dropped: {by} answers the misses");
    }

    /// <summary>Stops a re-sync sidestep where it is, letting go of its movement key if held.</summary>
    private void AbandonResync(string why)
    {
        if (_resyncState == 0) return;
        if (_resyncState == 2) _host.SetMotionBy("Combat/Resync", _resyncMotion, false);
        _resyncState = 0;
        _host.Log($"[CombatCast] re-sync sidestep stopped: {why}");
    }

    private bool ContinueEnvironmentApproach(WorldObject targetObj, double currentDist, DateTime now)
    {
        _envApproachTickAt = now;
        if (currentDist <= _envApproachGoal)
        {
            EndEnvironmentApproach($"reached {currentDist:0.0}m");
            return false;
        }
        if ((now - _envApproachStartedAt).TotalMilliseconds > ApproachMaxMs)
        {
            EndEnvironmentApproach($"timed out at {currentDist:0.0}m");
            return false;
        }
        if (currentDist < _envApproachBestDist - ApproachProgressMeters)
        {
            _envApproachBestDist = currentDist;
            _envApproachProgressAt = now;
        }
        else if ((now - _envApproachProgressAt).TotalMilliseconds > ApproachNoProgressMs)
        {
            EndEnvironmentApproach($"no progress (blocked?) at {currentDist:0.0}m");
            return false;
        }

        // Face the target, then run. A held call (the pacer, a use) is simply tried next tick.
        if ((now - _envApproachTurnAt).TotalMilliseconds >= ApproachTurnRefreshMs
            && TryGetHeadingTo(activeTargetId, out double desiredDeg, out double err)
            && err > ApproachFaceToleranceDeg)
        {
            if (_host.HasTurnToHeading)
            {
                if (_host.TurnToHeadingBy("Combat/Approach", (float)desiredDeg)) _envApproachTurnAt = now;
            }
            else
            {
                FaceTarget(activeTargetId);
                _envApproachUsedMotions = true;
                _envApproachTurnAt = now;
            }
        }
        if ((now - _envApproachRunAt).TotalMilliseconds >= ApproachRunRefreshMs
            && _host.SetAutoRunBy("Combat/Approach", true))
            _envApproachRunAt = now;

        _stallWatch.Hold(now);
        LastCombatSkipReason = "env-approach";   // D4 record-only
        return true;
    }

    /// <summary>Stops an approach in progress (autorun off). Safe to call when none is.</summary>
    private void EndEnvironmentApproach(string why)
    {
        if (_envApproachTargetId == 0) return;
        int id = _envApproachTargetId;
        _envApproachTargetId = 0;
        if (_envApproachRunAt != DateTime.MinValue) _host.SetAutoRunBy("Combat/Approach", false);
        if (_envApproachUsedMotions) ClearCombatTurnMotions();
        _host.Log($"[EnvImpact] {_envApproachKind.ToString().ToLowerInvariant()} approach to 0x{(uint)id:X8} ended: {why}");
    }

    /// <summary>True while <paramref name="id"/> is being skipped for its environment hits.</summary>
    private bool IsEnvironmentSkipped(int id)
    {
        if (_envSkipUntil.Count == 0 || !_envSkipUntil.TryGetValue(id, out DateTime until)) return false;
        if (EnvClock() < until) return true;
        _envSkipUntil.Remove(id);
        return false;
    }

    /// <summary>Clears every environment-impact state (macro stop, new character).</summary>
    private void ResetEnvironmentImpact(string why)
    {
        EndEnvironmentApproach(why);
        AbandonResync(why);
        _envPendingTargetId = 0;
        _castWatches.Clear();
        _envCandidates.Clear();
        while (_envCreated.TryDequeue(out _)) { }
        EnvThrottle.Clear();
        _envSkipUntil.Clear();
    }

    // ── Positions (global metres: landblock x/y * 192 + the in-block offset) ──

    private bool TryGetObjectGlobal(uint id, out double x, out double y, out double z)
    {
        x = y = z = 0;
        if (!_host.TryGetObjectPosition(id, out uint cell, out float lx, out float ly, out float lz)) return false;
        x = ((cell >> 24) & 0xFF) * 192.0 + lx;
        y = ((cell >> 16) & 0xFF) * 192.0 + ly;
        z = lz;
        return true;
    }

    private bool TryGetPlayerGlobal(out double x, out double y, out double z)
    {
        x = y = z = 0;
        if (!_host.TryGetPlayerPose(out uint cell, out float lx, out float ly, out float lz,
                out _, out _, out _, out _)) return false;
        x = ((cell >> 24) & 0xFF) * 192.0 + lx;
        y = ((cell >> 16) & 0xFF) * 192.0 + ly;
        z = lz;
        return true;
    }

    private static double Dist(double ax, double ay, double az, double bx, double by, double bz)
    {
        double dx = ax - bx, dy = ay - by, dz = az - bz;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

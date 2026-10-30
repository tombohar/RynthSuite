// CombatHudTracker.cs — Pump-thread feed for the Mini Remote's Target and Summon rows.
//
//   Target  the creature combat is attacking (CombatManager.activeTargetId): name, health
//           ratio from the object cache, distance.
//   Summon  the player's pet ("<Player>'s <Pet>" in the landscape): health ratio (polled with
//           QueryHealth) and time left (RemainingLifespan from one appraisal, then counted
//           down locally).
//
// The render thread only reads the published immutable snapshot.
using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Huds;

/// <summary>Immutable view of the attack target and the active summon (render thread safe).</summary>
internal sealed record CombatHudSnapshot(
    int TargetId,
    string TargetName,
    float TargetHealth,       // 0..1, -1 = no health received yet
    double TargetDistance,    // yards, < 0 = unknown
    int PetId,
    string PetName,
    float PetHealth,          // 0..1, -1 = no health received yet
    long PetExpiresAtMs,      // Environment.TickCount64 deadline, 0 = lifespan unknown
    int PetLifespanSec,       // total lifespan, 0 = unknown
    long PetSeenAtMs)         // when this pet first appeared
{
    public static readonly CombatHudSnapshot Empty = new(0, string.Empty, -1f, -1, 0, string.Empty, -1f, 0, 0, 0);
}

internal sealed class CombatHudTracker
{
    private const uint StypeLifespan = 267;
    private const uint StypeRemainingLifespan = 268;
    private const long ThinkIntervalMs = 250;
    // QueryHealth makes the server stream that creature's health to us instead of the previous
    // query's, so each pet poll is followed by a re-query of the combat / selected target.
    private const long PetHealthPollMs = 3000;
    private const long AppraiseRetryMs = 5000;
    private const int AppraiseMaxAttempts = 3;
    private const long PlayerNameRefreshMs = 30_000;

    private readonly RynthCoreHost _host;
    private readonly Func<WorldObjectCache?> _cache;
    private readonly Func<int> _attackTargetId;

    private volatile CombatHudSnapshot _snapshot = CombatHudSnapshot.Empty;
    private long _lastThinkAt;

    private string _playerName = string.Empty;
    private long _playerNameAt;

    // Current pet bookkeeping.
    private int _petId;
    private long _petSeenAt;
    private long _petExpiresAt;
    private int _petLifespanSec;
    private int _appraiseAttempts;
    private long _lastAppraiseAt;
    private long _lastPetHealthAt;

    public CombatHudTracker(RynthCoreHost host, Func<WorldObjectCache?> cache, Func<int> attackTargetId)
    {
        _host = host;
        _cache = cache;
        _attackTargetId = attackTargetId;
    }

    /// <summary>Latest target / summon view.</summary>
    public CombatHudSnapshot Snapshot => _snapshot;

    private static long NowMs => Environment.TickCount64;

    /// <summary>Refreshes the snapshot (pump thread). Cheap when called every tick.</summary>
    public void Tick()
    {
        long now = NowMs;
        if (now - _lastThinkAt < ThinkIntervalMs) return;
        _lastThinkAt = now;

        var cache = _cache();
        if (cache == null) { _snapshot = CombatHudSnapshot.Empty; return; }

        int playerId = unchecked((int)_host.GetPlayerId());
        int targetId = _attackTargetId();

        // ── Attack target ──
        string targetName = string.Empty;
        float targetHealth = -1f;
        double targetDist = -1;
        if (targetId != 0)
        {
            targetName = cache[targetId]?.Name ?? string.Empty;
            if (targetName.Length == 0 && _host.TryGetObjectName(unchecked((uint)targetId), out string n)) targetName = n;
            if (targetName.Length == 0) targetName = $"0x{(uint)targetId:X8}";
            targetHealth = cache.GetHealthRatio(targetId);
            if (playerId != 0)
            {
                double d = cache.Distance(playerId, targetId);
                targetDist = d < double.MaxValue ? d : -1;
            }
        }

        // ── Summon ──
        var pet = FindOwnPet(cache);
        string petName = string.Empty;
        float petHealth = -1f;
        if (pet == null)
        {
            ForgetPet();
        }
        else
        {
            if (pet.Id != _petId) StartTrackingPet(pet.Id, now);
            petName = StripOwner(pet.Name);
            TickPetLifespan(now);
            TickPetHealth(now, targetId);
            petHealth = cache.GetHealthRatio(_petId);
        }

        _snapshot = new CombatHudSnapshot(targetId, targetName, targetHealth, targetDist,
            _petId, petName, petHealth, _petExpiresAt, _petLifespanSec, _petSeenAt);
    }

    public void OnLogout()
    {
        ForgetPet();
        _playerName = string.Empty;
        _snapshot = CombatHudSnapshot.Empty;
    }

    // ── Pet ─────────────────────────────────────────────────────────────────

    private WorldObject? FindOwnPet(WorldObjectCache cache)
    {
        string me = PlayerName();
        if (me.Length == 0) return null;
        string prefix = me + "'s ";
        foreach (var wo in cache.GetLandscape())
            if (wo != null && !string.IsNullOrEmpty(wo.Name) && wo.Name.StartsWith(prefix, StringComparison.Ordinal))
                return wo;
        return null;
    }

    private string PlayerName()
    {
        long now = NowMs;
        if (_playerName.Length > 0 && now - _playerNameAt < PlayerNameRefreshMs) return _playerName;
        uint pid = _host.GetPlayerId();
        if (pid != 0 && _host.TryGetObjectName(pid, out string n) && !string.IsNullOrEmpty(n))
        {
            _playerName = n;
            _playerNameAt = now;
        }
        return _playerName;
    }

    /// <summary>"Silent's Fire Banshee" → "Fire Banshee".</summary>
    private string StripOwner(string name)
    {
        string prefix = _playerName + "'s ";
        return _playerName.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    private void StartTrackingPet(int id, long now)
    {
        _petId = id;
        _petSeenAt = now;
        _petExpiresAt = 0;
        _petLifespanSec = 0;
        _appraiseAttempts = 0;
        _lastAppraiseAt = 0;
        _lastPetHealthAt = 0;
        RynthLog.Trace(LogCat.Huds, $"summon tracked 0x{(uint)id:X8}");
    }

    private void ForgetPet()
    {
        _petId = 0;
        _petSeenAt = 0;
        _petExpiresAt = 0;
        _petLifespanSec = 0;
        _appraiseAttempts = 0;
    }

    /// <summary>
    /// Appraises the new pet (at most <see cref="AppraiseMaxAttempts"/> times) and anchors the
    /// countdown on the first RemainingLifespan the server returns. The pet's guid is new, so
    /// any cached value belongs to this pet.
    /// </summary>
    private void TickPetLifespan(long now)
    {
        if (_petExpiresAt != 0) return;
        uint uid = unchecked((uint)_petId);

        if (_host.HasGetObjectIntProperty && _host.TryGetObjectIntProperty(uid, StypeRemainingLifespan, out int remaining) && remaining > 0)
        {
            _petExpiresAt = now + remaining * 1000L;
            if (_host.TryGetObjectIntProperty(uid, StypeLifespan, out int total) && total > 0)
                _petLifespanSec = Math.Max(total, remaining);
            RynthLog.Trace(LogCat.Huds, $"summon 0x{uid:X8}: {remaining}s left of {_petLifespanSec}s");
            return;
        }

        if (_appraiseAttempts >= AppraiseMaxAttempts || !_host.HasRequestId) return;
        if (_lastAppraiseAt != 0 && now - _lastAppraiseAt < AppraiseRetryMs) return;
        // Counted only when the engine took it (it refuses around a death or a portal).
        if (!_host.RequestId(uid)) return;
        _appraiseAttempts++;
        _lastAppraiseAt = now;
    }

    /// <summary>Polls the pet's health, then hands the server's health stream back to the fight target.</summary>
    private void TickPetHealth(long now, int combatTargetId)
    {
        if (!_host.HasQueryHealth || now - _lastPetHealthAt < PetHealthPollMs) return;
        _lastPetHealthAt = now;
        _host.QueryHealth(unchecked((uint)_petId));

        uint restore = combatTargetId != 0 ? unchecked((uint)combatTargetId) : 0;
        if (restore == 0 && _host.HasGetSelectedItemId) restore = _host.GetSelectedItemId();
        if (restore != 0 && restore != unchecked((uint)_petId)) _host.QueryHealth(restore);
    }
}

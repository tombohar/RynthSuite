using System;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;   // TurnToHeadingBy / SetAutoRunBy

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Kite / pull-away (2026-10-16). Rules in KiteModes.cs; distance in LegacyUiSettings.KitePullMeters.
///
/// When the effective monster rule is PullAway, combat tags the mob (one hit window), then
/// autoruns away from its lock-spot origin until the mob has been dragged that many metres
/// clear of where it stood, then stops and kills it. Default and per-mob via MonsterRule.Kite.
/// </summary>
public partial class CombatManager
{
    /// <summary>Clock for the kite state machine; tests replace it.</summary>
    internal Func<DateTime> KiteClock { get; set; } = () => RynthClock.Now;

    // Face-away + autorun cadence (mirrors EnvironmentApproach).
    private const double KiteTurnRefreshMs = 300;
    private const double KiteRunRefreshMs = 1000;
    // Think stopped driving the kite (buffing took over): let go of autorun.
    private const double KiteOrphanMs = 1500;
    // Tag window: allow attacks so aggro sticks before we turn and run.
    private const double KiteTagMaxMs = 2500;
    // A pull that never gets far enough (blocked, mob leashed) gives up and fights in place.
    private const double KiteMaxMs = 20000;
    // Don't outrun the chase: stop autorun when farther than this from the mob.
    private const double KiteCatchUpSlackMeters = 1.5;
    // Progress toward the pull goal; no gain in this long ⇒ blocked / leashed.
    private const double KiteProgressMeters = 0.75;
    private const double KiteNoProgressMs = 4000;

    private enum KitePhase { None, Tag, Flee, Done }

    private int _kiteTargetId;
    private KitePhase _kitePhase = KitePhase.None;
    private double _kiteOriginX, _kiteOriginY, _kiteOriginZ;
    private bool _kiteHasOrigin;
    private double _kiteBestPull;
    private DateTime _kiteStartedAt, _kiteProgressAt, _kiteRunAt, _kiteTurnAt, _kiteTickAt;
    private bool _kiteUsedMotions;
    // Targets already pulled this engagement (object id): do not re-kite mid-fight.
    private readonly HashSet<int> _kiteDoneIds = new();

    /// <summary>True while combat is tagging or running a pull-away for the locked target.</summary>
    internal bool KiteActive => _kitePhase is KitePhase.Tag or KitePhase.Flee;

    /// <summary>
    /// In ThinkCore once the target is valid. True = this tick belongs to the kite (flee under
    /// way): no attack. Tag phase returns false so the first hit(s) can land.
    /// </summary>
    internal bool RunKite(WorldObject targetObj, double currentDist)
    {
        DateTime now = KiteClock();
        int id = activeTargetId;
        if (id == 0) { EndKite("no target"); return false; }

        // Already pulled this object id for this engagement: fight in place.
        if (_kiteDoneIds.Contains(id))
        {
            if (_kiteTargetId == id) EndKite("already pulled");
            return false;
        }

        var rule = GetRuleForTarget(targetObj);
        if (!KiteModes.WantsPullAway(rule, DefaultMonsterRule()))
        {
            if (_kiteTargetId == id) EndKite("rule Off");
            return false;
        }

        // Start (or retarget) a pull for this lock.
        if (_kiteTargetId != id || _kitePhase == KitePhase.None)
        {
            if (_kiteTargetId != 0 && _kiteTargetId != id)
                EndKite("the target changed");
            BeginKite(id, targetObj, now);
        }

        _kiteTickAt = now;
        double pullMeters = Math.Max(3.0, _settings.KitePullMeters);

        if (_kitePhase == KitePhase.Tag)
        {
            bool tagged = _lastDamageDealtAt != DateTime.MinValue && _lastDamageDealtAt >= _kiteStartedAt;
            bool tagTimedOut = (now - _kiteStartedAt).TotalMilliseconds >= KiteTagMaxMs;
            if (!_settings.KiteTagFirst || tagged || tagTimedOut)
            {
                _kitePhase = KitePhase.Flee;
                _kiteBestPull = PullDistance(id);
                _kiteProgressAt = now;
                _kiteRunAt = _kiteTurnAt = DateTime.MinValue;
                string why = !_settings.KiteTagFirst ? "tag skipped"
                    : tagged ? "tagged"
                    : "tag timed out";
                _host.Log($"[Kite] pull-away on 0x{(uint)id:X8} '{targetObj.Name}': fleeing ({why}), goal {pullMeters:0}m from origin");
                EndEnvironmentApproach("kite pull-away");
            }
            else
            {
                LastCombatSkipReason = "kite-tag";
                return false;   // let Think attack once so aggro sticks
            }
        }

        if (_kitePhase != KitePhase.Flee) return false;

        double pulled = PullDistance(id);
        if (pulled >= pullMeters)
        {
            FinishKite(id, $"pulled {pulled:0.0}m");
            return false;
        }
        if ((now - _kiteStartedAt).TotalMilliseconds > KiteMaxMs)
        {
            FinishKite(id, $"timed out at {pulled:0.0}m");
            return false;
        }
        if (pulled > _kiteBestPull + KiteProgressMeters)
        {
            _kiteBestPull = pulled;
            _kiteProgressAt = now;
        }
        else if ((now - _kiteProgressAt).TotalMilliseconds > KiteNoProgressMs)
        {
            FinishKite(id, $"no progress (blocked/leashed?) at {pulled:0.0}m");
            return false;
        }

        // Don't outrun the chase past disengage: wait for the mob to catch up.
        double catchUp = Math.Max(1.0, DisengageDistance - KiteCatchUpSlackMeters);
        bool waiting = currentDist > catchUp;

        // Face away from the mob while fleeing; face it while waiting for catch-up.
        if ((now - _kiteTurnAt).TotalMilliseconds >= KiteTurnRefreshMs
            && TryGetHeadingTo(id, out double towardDeg, out _))
        {
            double faceDeg = waiting ? towardDeg : (towardDeg + 180.0) % 360.0;
            if (_host.HasTurnToHeading)
            {
                if (_host.TurnToHeadingBy("Combat/Kite", (float)faceDeg)) _kiteTurnAt = now;
            }
            else
            {
                // No heading snap: FaceTarget only faces toward the mob (useful while waiting).
                if (waiting) { FaceTarget(id); _kiteUsedMotions = true; }
                _kiteTurnAt = now;
            }
        }

        if (waiting)
        {
            if (_kiteRunAt != DateTime.MinValue)
            {
                _host.SetAutoRunBy("Combat/Kite", false);
                _kiteRunAt = DateTime.MinValue;
            }
            LastCombatSkipReason = "kite-wait";
            _stallWatch.Hold(now);
            return true;
        }

        if ((now - _kiteRunAt).TotalMilliseconds >= KiteRunRefreshMs
            && _host.SetAutoRunBy("Combat/Kite", true))
            _kiteRunAt = now;

        _stallWatch.Hold(now);
        LastCombatSkipReason = "kite-flee";
        return true;
    }

    private void BeginKite(int id, WorldObject targetObj, DateTime now)
    {
        _kiteTargetId = id;
        _kitePhase = _settings.KiteTagFirst ? KitePhase.Tag : KitePhase.Flee;
        _kiteHasOrigin = TryGetObjectGlobal((uint)id, out _kiteOriginX, out _kiteOriginY, out _kiteOriginZ);
        if (!_kiteHasOrigin)
            _kiteHasOrigin = TryGetPlayerGlobal(out _kiteOriginX, out _kiteOriginY, out _kiteOriginZ);
        _kiteBestPull = 0;
        _kiteStartedAt = _kiteProgressAt = _kiteTickAt = now;
        _kiteRunAt = _kiteTurnAt = DateTime.MinValue;
        _kiteUsedMotions = false;
        string phase = _kitePhase == KitePhase.Tag ? "tag then flee" : "flee";
        _host.Log($"[Kite] pull-away start 0x{(uint)id:X8} '{targetObj.Name}' ({phase}, {_settings.KitePullMeters}m)");
        if (_kitePhase == KitePhase.Flee)
            EndEnvironmentApproach("kite pull-away");
    }

    /// <summary>How far the mob has been dragged from its lock-spot origin (metres).</summary>
    private double PullDistance(int id)
    {
        if (!_kiteHasOrigin) return 0;
        if (!TryGetObjectGlobal((uint)id, out double x, out double y, out double z))
            return _kiteBestPull;
        return Dist(x, y, z, _kiteOriginX, _kiteOriginY, _kiteOriginZ);
    }

    /// <summary>Pull succeeded or gave up: remember the id and fight in place.</summary>
    private void FinishKite(int id, string why)
    {
        _kiteDoneIds.Add(id);
        if (_kiteDoneIds.Count > 64) _kiteDoneIds.Clear();
        EndKite(why);
        _host.Log($"[Kite] pull-away done 0x{(uint)id:X8}: {why} — attacking");
    }

    /// <summary>Stops a kite in progress (autorun off). Safe to call when none is.</summary>
    private void EndKite(string why)
    {
        if (_kitePhase == KitePhase.None && _kiteTargetId == 0) return;
        int id = _kiteTargetId;
        bool wasRunning = _kiteRunAt != DateTime.MinValue;
        _kiteTargetId = 0;
        _kitePhase = KitePhase.None;
        _kiteHasOrigin = false;
        if (wasRunning) _host.SetAutoRunBy("Combat/Kite", false);
        if (_kiteUsedMotions) ClearCombatTurnMotions();
        _kiteRunAt = _kiteTurnAt = DateTime.MinValue;
        if (id != 0)
            _host.Log($"[Kite] ended 0x{(uint)id:X8}: {why}");
    }

    /// <summary>Clears kite state (macro stop, new character).</summary>
    private void ResetKite(string why)
    {
        EndKite(why);
        _kiteDoneIds.Clear();
    }

    /// <summary>
    /// Pump-thread orphan watch: Think drives the flee; when it stops (another activity,
    /// combat off) autorun must not be left on.
    /// </summary>
    internal void PollKite()
    {
        if (_kitePhase == KitePhase.None) return;
        DateTime now = KiteClock();
        if ((now - _kiteTickAt).TotalMilliseconds > KiteOrphanMs
            || !_settings.EnableCombat
            || !_settings.IsMacroRunning)
            EndKite("combat stopped driving it");
    }
}

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi;

// ============================================================================
//  Environment-impact throttle (2026-10-12): pure rules and bookkeeping.
//
//  A missile or a war/void projectile that hits a wall, a pillar, the ceiling
//  or another object short of the target does no damage, and before this the bot
//  kept firing into it until the no-damage blacklist (several settle windows) or
//  the no-progress timeout gave up. Now, once a target has taken
//  EnvironmentImpactLimit such hits within EnvironmentImpactWindowSec, combat
//  stops attacking it and does what the player chose for that kind of combat:
//  move closer, pick another target, or move closer once and then pick another.
//
//  The signals (CombatManager.EnvironmentImpact.cs):
//    missile  ACE ProjectileCollisionHelper.OnCollideEnvironment sends the shooter
//             "Your missile attack hit the environment." (also when the arrow hits
//             an object that isn't its target).
//    magic    ACE sends nothing (SpellProjectile.OnCollideEnvironment only
//             explodes the projectile), so the bot follows its own projectiles and
//             judges where they stopped (ProjectileImpactJudge).
//
//  Everything here is pure (no host, no clock of its own) so the host tests can
//  drive it directly.
// ============================================================================

/// <summary>What combat does once a target has taken too many environment hits.
/// Stored as an int in the settings (MissileEnvironmentResponse / MagicEnvironmentResponse).</summary>
public enum EnvironmentResponse
{
    /// <summary>No detection; keep attacking (the behaviour before 2026-10-12).</summary>
    Off = 0,
    /// <summary>Move toward the target each time; once already close, pick another target.</summary>
    MoveCloser = 1,
    /// <summary>Skip the target for a while and pick another (moves closer when there is no other).</summary>
    NewTarget = 2,
    /// <summary>Move closer once; if the shots still hit the environment, pick another target.</summary>
    CloserThenNewTarget = 3,
}

/// <summary>The step combat takes for one throttle trip.</summary>
public enum EnvironmentAction { None, Approach, NewTarget }

/// <summary>Choices and the decision for one throttle trip.</summary>
public static class EnvironmentImpactPlanner
{
    /// <summary>Combo labels, in <see cref="EnvironmentResponse"/> order (plugin and engine settings panels).</summary>
    public static readonly string[] ResponseNames =
        { "Off", "Move closer", "New target", "Move closer, then new target" };

    /// <summary>Approaching is pointless within this much of the closest allowed distance.</summary>
    public const double ApproachSlackMeters = 0.5;

    /// <summary>A saved int as a response; anything out of range is Off.</summary>
    public static EnvironmentResponse Parse(int value)
        => value is >= 0 and <= 3 ? (EnvironmentResponse)value : EnvironmentResponse.Off;

    /// <summary>The label for a saved value (logs and the settings panels).</summary>
    public static string Name(int value) => ResponseNames[(int)Parse(value)];

    /// <summary>
    /// The step for a throttle trip on a target <paramref name="distance"/> metres away that
    /// has already been approached <paramref name="approachesDone"/> times for this reason.
    /// Never approaches nearer than <paramref name="minDistance"/>. A new target needs another
    /// monster in reach: skipping the only one would leave it free to hit the bot while combat
    /// idles, so then the bot moves closer if it still can, or keeps attacking (None).
    /// </summary>
    public static EnvironmentAction Decide(EnvironmentResponse response, int approachesDone,
        double distance, double minDistance, bool hasAlternateTarget)
    {
        bool canApproach = double.IsFinite(distance) && distance > minDistance + ApproachSlackMeters;
        EnvironmentAction approachElseNone = canApproach ? EnvironmentAction.Approach : EnvironmentAction.None;
        EnvironmentAction newTargetElse = hasAlternateTarget ? EnvironmentAction.NewTarget : approachElseNone;
        return response switch
        {
            EnvironmentResponse.MoveCloser => canApproach ? EnvironmentAction.Approach : newTargetElse,
            EnvironmentResponse.NewTarget => newTargetElse,
            EnvironmentResponse.CloserThenNewTarget => approachesDone == 0 && canApproach
                ? EnvironmentAction.Approach : newTargetElse,
            _ => EnvironmentAction.None,
        };
    }

    /// <summary>
    /// Where an approach stops: <paramref name="stepMeters"/> nearer than now, never nearer
    /// than <paramref name="minDistance"/>.
    /// </summary>
    public static double ApproachGoal(double distance, double stepMeters, double minDistance)
        => Math.Max(Math.Max(0.5, minDistance), distance - Math.Max(1.0, stepMeters));
}

/// <summary>
/// Environment hits per target inside a sliding window, and how many times each target has
/// been approached for them. Pump thread only.
/// </summary>
public sealed class EnvironmentImpactThrottle
{
    private readonly Dictionary<int, List<DateTime>> _hits = new();
    private readonly Dictionary<int, int> _approaches = new();
    // A long session meets thousands of monsters; ids are forgotten on delete, this is the backstop.
    private const int MaxTracked = 256;

    /// <summary>Records one hit on <paramref name="targetId"/> and returns the hits within the window.</summary>
    public int Record(int targetId, DateTime now, double windowSec)
    {
        if (targetId == 0) return 0;
        if (_hits.Count >= MaxTracked && !_hits.ContainsKey(targetId)) _hits.Clear();
        if (!_hits.TryGetValue(targetId, out var list)) _hits[targetId] = list = new List<DateTime>(4);
        list.Add(now);
        Prune(list, now, windowSec);
        return list.Count;
    }

    /// <summary>Hits on <paramref name="targetId"/> within the window (no new hit).</summary>
    public int Count(int targetId, DateTime now, double windowSec)
    {
        if (!_hits.TryGetValue(targetId, out var list)) return 0;
        Prune(list, now, windowSec);
        return list.Count;
    }

    /// <summary>Starts the count again (after the bot acted on it).</summary>
    public void ResetHits(int targetId) => _hits.Remove(targetId);

    public int Approaches(int targetId) => _approaches.TryGetValue(targetId, out int n) ? n : 0;

    public void NoteApproach(int targetId)
    {
        if (_approaches.Count >= MaxTracked && !_approaches.ContainsKey(targetId)) _approaches.Clear();
        _approaches[targetId] = Approaches(targetId) + 1;
    }

    /// <summary>The object is gone (deleted): a respawn reusing the id starts clean.</summary>
    public void Forget(int targetId)
    {
        _hits.Remove(targetId);
        _approaches.Remove(targetId);
    }

    public void Clear()
    {
        _hits.Clear();
        _approaches.Clear();
    }

    private static void Prune(List<DateTime> list, DateTime now, double windowSec)
    {
        double window = Math.Max(1.0, windowSec);
        list.RemoveAll(t => (now - t).TotalSeconds > window);
    }
}

/// <summary>
/// Where a spell projectile stopped, against where the target is. A projectile that reaches
/// the target stops at its surface; one that hits the environment stops short of it, toward
/// the caster. Both conditions must hold, so a large monster (its surface a few metres from its
/// centre) isn't read as a wall. The caller also requires that no hit, resist or evade line
/// arrived for the cast: that is the final word for a large monster or an odd stop.
/// </summary>
public static class ProjectileImpactJudge
{
    /// <summary>A stop farther than this from the target's centre didn't touch it.</summary>
    public const double MissRadiusMeters = 4.0;
    /// <summary>A stop at least this much nearer to the caster than the target is "short".</summary>
    public const double ShortByMeters = 2.0;
    /// <summary>A projectile that flew less than this never really left (or was never ours).</summary>
    public const double MinTravelMeters = 1.0;

    /// <summary>True when the projectile stopped short of the target and away from it.</summary>
    public static bool StoppedShort(double impactToTarget, double casterToImpact, double casterToTarget)
    {
        if (!double.IsFinite(impactToTarget) || !double.IsFinite(casterToImpact) || !double.IsFinite(casterToTarget))
            return false;
        return impactToTarget > MissRadiusMeters && casterToImpact + ShortByMeters < casterToTarget;
    }
}

using System;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// One heading snap for a missile target the character has its back to at point blank.
///
/// Missile combat normally never turns the character: the server rotates to the target before
/// each shot, and a client turn servo on top of that spun archers in circles (2026-09-27, Lucy).
/// But with the target inside <see cref="MaxDistanceM"/> the server's rotate doesn't happen, so
/// the character stands facing away and fires nothing. Silentkelpie 2026-10-14/15: 7 of 13
/// crossbow stalls were at 1.7 m with the target 177 degrees behind; the stall watchdog's
/// re-issue never fixed one of them (only dropping the target did, at step 2 or later).
///
/// So, only when all of these hold, one instant heading snap (TurnToHeading, as for walls):
///   * the target is within <see cref="MaxDistanceM"/>,
///   * it is more than <see cref="MinErrorDeg"/> off the character's facing,
///   * no hit, miss or kill for <see cref="NoShotMs"/> (a full crossbow cycle: a shot in
///     progress is never interrupted),
///   * no snap for this target within <see cref="RetryMs"/>.
/// Combat then holds the attack for <see cref="SettleMs"/> so the turn reaches the server first.
/// Pure: the caller passes the time and readings in.
/// </summary>
internal sealed class PointBlankFacing
{
    /// <summary>Targets this close (metres) count as point blank (the stall watchdog's distance).</summary>
    public double MaxDistanceM { get; set; } = CombatStallWatchdog.PointBlankMissileM;
    /// <summary>Facing error (degrees) above which the character is turned.</summary>
    public double MinErrorDeg { get; set; } = 90;
    /// <summary>No swing evidence for this long before a snap.</summary>
    public double NoShotMs { get; set; } = 2500;
    /// <summary>At most one snap per target this often.</summary>
    public double RetryMs { get; set; } = 3000;
    /// <summary>Attack held this long after a snap.</summary>
    public double SettleMs { get; set; } = 300;

    private int _target;
    private DateTime _snapAt = DateTime.MinValue;

    /// <summary>Snaps done (diagnostics).</summary>
    public int Snaps { get; private set; }

    /// <summary>True when the character should be turned to <paramref name="targetId"/> now.</summary>
    public bool ShouldSnap(DateTime now, int targetId, double distanceM, double facingErrorDeg, DateTime lastEvidenceAt)
    {
        if (targetId == 0 || distanceM < 0 || distanceM > MaxDistanceM) return false;
        if (facingErrorDeg <= MinErrorDeg) return false;
        if (lastEvidenceAt != DateTime.MinValue && (now - lastEvidenceAt).TotalMilliseconds < NoShotMs) return false;
        if (targetId == _target && _snapAt != DateTime.MinValue && (now - _snapAt).TotalMilliseconds < RetryMs) return false;
        return true;
    }

    /// <summary>The snap for <paramref name="targetId"/> was sent.</summary>
    public void Snapped(DateTime now, int targetId)
    {
        _target = targetId;
        _snapAt = now;
        Snaps++;
    }

    /// <summary>True while the attack should wait for the last snap to settle.</summary>
    public bool Settling(DateTime now, int targetId)
        => targetId == _target && _snapAt != DateTime.MinValue && (now - _snapAt).TotalMilliseconds < SettleMs;
}

using System;

namespace RynthCore.Plugin.RynthAi.CreatureData;

/// <summary>Where a monster stood when it hit us, as ACE's shield rule sees it.</summary>
internal enum HitFacing
{
    /// <summary>Not measurable: no heading or position, the attacker not found, or several of
    /// its name around that disagree. Never a guess.</summary>
    Unknown = 0,
    /// <summary>Within 90 degrees either side of our facing: a shield counts.</summary>
    Front = 1,
    /// <summary>Outside that arc: a shield does nothing.</summary>
    Behind = 2,
}

/// <summary>
/// The facing half of "does the shield help?" (2026-11-08): ACE counts a shield only when the
/// attacker is within +-90 degrees of the defender's facing (Creature_Combat.GetShieldMod,
/// effectiveAngle 180; SpellProjectile.GetShieldMod the same for spells), and only in a combat
/// stance. Facing is the 2D angle between the defender's forward vector (the orientation
/// applied to +Y) and the direction to the attacker; |angle| &lt;= 90 is in front.
///
/// Headings here are compass bearings (0 = North, clockwise) from the pose quaternion, the basis
/// of <see cref="PlayerCompass"/>; never Host.TryGetPlayerHeading (0 = East, counter-clockwise).
/// Positions are landblock-local (cell + x/y) and made global the way WorldObjectCache.Distance
/// does (192 m per landblock). Pure: the host tests call it directly.
/// </summary>
internal static class ShieldFacing
{
    /// <summary>ACE's shield arc in degrees (half of it either side of the facing).</summary>
    public const double ShieldArcDeg = 180.0;

    /// <summary>Attackers farther than this are not the one that just hit (stale or another of the name).</summary>
    public const double MaxAttackerRange = 80.0;

    public static string Word(HitFacing f) => f switch
    {
        HitFacing.Front => "front",
        HitFacing.Behind => "behind",
        _ => "unknown",
    };

    public static bool TryParse(string? s, out HitFacing f)
    {
        switch ((s ?? "").Trim().ToLowerInvariant())
        {
            case "front": f = HitFacing.Front; return true;
            case "behind": f = HitFacing.Behind; return true;
            case "unknown": f = HitFacing.Unknown; return true;
            default: f = HitFacing.Unknown; return false;
        }
    }

    /// <summary>The compass heading (0 = North, clockwise) of the yaw quaternion (w, z), as PlayerCompass.</summary>
    public static double CompassFromQuaternion(double qw, double qz)
    {
        double yaw = 2.0 * Math.Atan2(qz, qw) * (180.0 / Math.PI);
        return ((-yaw) % 360.0 + 720.0) % 360.0;
    }

    /// <summary>A cell's landblock-local x/y as global metres (192 m per landblock).</summary>
    public static (double X, double Y) Global(uint cell, float x, float y)
        => (((cell >> 24) & 0xFF) * 192.0 + x, ((cell >> 16) & 0xFF) * 192.0 + y);

    /// <summary>A dungeon or building cell (cell index 0x100 and up): its own frame.</summary>
    public static bool IsIndoorCell(uint cell) => (cell & 0xFFFF) >= 0x100;

    /// <summary>
    /// Signed angle in degrees (-180..180] from <paramref name="headingDeg"/> to the bearing of the
    /// point (dx east, dy north); NaN when the point is on top of us or a number isn't finite.
    /// </summary>
    public static double AngleTo(double headingDeg, double dx, double dy)
    {
        if (!double.IsFinite(headingDeg) || !double.IsFinite(dx) || !double.IsFinite(dy)) return double.NaN;
        if (dx * dx + dy * dy < 0.0001) return double.NaN;   // under a centimetre: no direction
        double bearing = Math.Atan2(dx, dy) * (180.0 / Math.PI);   // compass: 0 = North, clockwise
        double diff = (bearing - headingDeg) % 360.0;
        if (diff > 180.0) diff -= 360.0;
        if (diff <= -180.0) diff += 360.0;
        return diff;
    }

    /// <summary>In front (|angle| &lt;= 90, ACE's "not greater than half the arc"), behind, or unknown.</summary>
    public static HitFacing Classify(double headingDeg, double px, double py, double ax, double ay)
    {
        double a = AngleTo(headingDeg, ax - px, ay - py);
        if (double.IsNaN(a)) return HitFacing.Unknown;
        return Math.Abs(a) > ShieldArcDeg / 2.0 ? HitFacing.Behind : HitFacing.Front;
    }

    /// <summary>
    /// <see cref="Classify(double,double,double,double,double)"/> from two landblock-local positions.
    /// Different landblocks with a dungeon or building cell on either side have no shared frame:
    /// unknown. So is an attacker beyond <see cref="MaxAttackerRange"/>.
    /// </summary>
    public static HitFacing Classify(double headingDeg, uint playerCell, float px, float py, uint attackerCell, float ax, float ay)
    {
        double d = Distance2D(playerCell, px, py, attackerCell, ax, ay);
        if (double.IsNaN(d) || d > MaxAttackerRange) return HitFacing.Unknown;
        var (gpx, gpy) = Global(playerCell, px, py);
        var (gax, gay) = Global(attackerCell, ax, ay);
        return Classify(headingDeg, gpx, gpy, gax, gay);
    }

    /// <summary>Flat distance between two landblock-local positions; NaN when they share no frame
    /// (different landblocks with a dungeon or building cell on either side) or a number isn't finite.</summary>
    public static double Distance2D(uint cellA, float xa, float ya, uint cellB, float xb, float yb)
    {
        if ((cellA >> 16) != (cellB >> 16) && (IsIndoorCell(cellA) || IsIndoorCell(cellB))) return double.NaN;
        var (gax, gay) = Global(cellA, xa, ya);
        var (gbx, gby) = Global(cellB, xb, yb);
        double dx = gbx - gax, dy = gby - gay;
        double d = Math.Sqrt(dx * dx + dy * dy);
        return double.IsFinite(d) ? d : double.NaN;
    }

    /// <summary>Several monsters of the attacker's name: the facing when every one agrees, else unknown.</summary>
    /// <remarks>None at all is unknown too. The caller leaves out the ones beyond range.</remarks>
    public static HitFacing Agree(System.Collections.Generic.IEnumerable<HitFacing> each)
    {
        HitFacing? seen = null;
        foreach (HitFacing f in each)
        {
            if (f == HitFacing.Unknown) return HitFacing.Unknown;
            if (seen == null) seen = f;
            else if (seen != f) return HitFacing.Unknown;
        }
        return seen ?? HitFacing.Unknown;
    }
}

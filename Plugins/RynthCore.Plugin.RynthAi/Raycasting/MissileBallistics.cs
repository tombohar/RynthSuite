using System;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// The flight path of an AC missile (arrow, bolt, dart) or arc spell, as pure math: no host, no
    /// geometry, no allocation. Kept dependency-free so Tools/RynthCore.MissileArcTests
    /// compiles it directly.
    ///
    /// The server launches every missile at the weapon's fixed speed on the LOW solution of
    /// the ballistic arc to the aim point, under gravity (ACE GetProjectileVelocity →
    /// Trajectory.solve_ballistic_arc, first solution; PhysicsGlobals.Gravity = 9.8). So the
    /// arc height is not a constant: it grows with distance and shrinks with launch speed.
    /// At 5 m a bow shot is a straight line; at 40 m with 25 m/s it rises about 3.5 m above
    /// the launch point, which is enough to hit a dungeon ceiling the straight line clears.
    ///
    /// Units are meters, Z up. "t" is the horizontal fraction of the shot, 0 at the launch
    /// point and 1 at the aim point.
    /// </summary>
    public readonly struct MissileArc
    {
        /// <summary>Launch point.</summary>
        public readonly float OX, OY, OZ;
        /// <summary>Unit horizontal direction from launch point to aim point.</summary>
        public readonly float DirX, DirY;
        /// <summary>Horizontal distance from launch point to aim point.</summary>
        public readonly float HorizDist;
        /// <summary>Aim point Z minus launch point Z.</summary>
        public readonly float Rise;
        /// <summary>Tangent of the launch elevation (low solution).</summary>
        public readonly float TanTheta;
        /// <summary>Curvature: height(x) = x·TanTheta − K·x², K = g(1+tan²θ)/(2v²).</summary>
        public readonly float K;
        /// <summary>False when the aim point is out of reach at this launch speed.</summary>
        public readonly bool Valid;
        /// <summary>True for a (near) vertical shot, flown as a straight line.</summary>
        public readonly bool Vertical;

        internal MissileArc(float ox, float oy, float oz, float dirX, float dirY, float horizDist,
                            float rise, float tanTheta, float k, bool valid, bool vertical)
        {
            OX = ox; OY = oy; OZ = oz;
            DirX = dirX; DirY = dirY;
            HorizDist = horizDist; Rise = rise;
            TanTheta = tanTheta; K = k;
            Valid = valid; Vertical = vertical;
        }

        /// <summary>Height above the launch point at horizontal fraction t (no clearance).</summary>
        public float HeightAt(float t)
        {
            if (!Valid) return 0f;
            if (Vertical) return Rise * t;
            float x = t * HorizDist;
            return x * TanTheta - K * x * x;
        }

        /// <summary>
        /// The point at horizontal fraction t, raised by <paramref name="clearance"/> at
        /// mid-flight (see <see cref="MissileBallistics.ClearanceBump"/>). The ends never
        /// move: t=0 is the launch point and t=1 the aim point.
        /// </summary>
        public void PointAt(float t, float clearance, out float x, out float y, out float z)
        {
            float h = t * HorizDist;
            x = OX + DirX * h;
            y = OY + DirY * h;
            z = OZ + HeightAt(t) + MissileBallistics.ClearanceBump(t, clearance);
        }

        /// <summary>
        /// How far the arc rises above the straight line from launch point to aim point, at
        /// its highest. This is the part a straight line-of-sight check never sees.
        /// </summary>
        public float MaxRiseAboveChord
        {
            get
            {
                if (!Valid || Vertical || HorizDist <= 0f || K <= 0f) return 0f;
                // height(x) − Rise·x/d = x·(tanθ − Rise/d) − K·x². Its maximum is at
                // x* = a/(2K), value a²/(4K), where a = tanθ − Rise/d.
                float a = TanTheta - Rise / HorizDist;
                if (a <= 0f) return 0f;
                float xStar = a / (2f * K);
                if (xStar >= HorizDist) xStar = HorizDist;
                return a * xStar - K * xStar * xStar;
            }
        }

        /// <summary>Highest point of the flight above the launch point (never below the ends).</summary>
        public float ApexAboveLaunch
        {
            get
            {
                if (!Valid) return 0f;
                if (Vertical) return Math.Max(0f, Rise);
                if (K <= 0f) return Math.Max(0f, Rise);
                float xApex = TanTheta / (2f * K);
                float apex = xApex > 0f && xApex < HorizDist
                    ? TanTheta * xApex - K * xApex * xApex
                    : 0f;
                return Math.Max(apex, Math.Max(0f, Rise));
            }
        }
    }

    public static class MissileBallistics
    {
        /// <summary>AC's gravity (PhysicsGlobals.Gravity = -9.8).</summary>
        public const float AcGravity = 9.8f;

        /// <summary>Below this horizontal distance a shot is treated as vertical.</summary>
        private const float VerticalEpsilon = 0.05f;

        /// <summary>
        /// Solves the low ballistic arc from the launch point to the aim point at a fixed launch
        /// speed. Out of reach (the speed can't carry that far or that high) gives Valid=false.
        /// </summary>
        public static MissileArc Solve(float ox, float oy, float oz,
                                       float tx, float ty, float tz,
                                       float speed, float gravity = AcGravity)
        {
            float dx = tx - ox, dy = ty - oy;
            float rise = tz - oz;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);

            if (float.IsNaN(d) || float.IsNaN(rise) || speed <= 0f || gravity <= 0f)
                return new MissileArc(ox, oy, oz, 0f, 0f, 0f, 0f, 0f, 0f, valid: false, vertical: false);

            if (d < VerticalEpsilon)
                return new MissileArc(ox, oy, oz, 0f, 0f, d, rise, 0f, 0f, valid: true, vertical: true);

            // Low-angle solution of  rise = d·tanθ − g·d²·(1+tan²θ)/(2v²):
            //   tanθ = (v² − √(v⁴ − g(g·d² + 2·rise·v²))) / (g·d)
            double v2 = (double)speed * speed;
            double disc = v2 * v2 - gravity * (gravity * (double)d * d + 2.0 * rise * v2);
            if (disc < 0.0)
                return new MissileArc(ox, oy, oz, dx / d, dy / d, d, rise, 0f, 0f, valid: false, vertical: false);

            double tan = (v2 - Math.Sqrt(disc)) / (gravity * (double)d);
            double k = gravity * (1.0 + tan * tan) / (2.0 * v2);
            return new MissileArc(ox, oy, oz, dx / d, dy / d, d, rise, (float)tan, (float)k, valid: true, vertical: false);
        }

        /// <summary>ACE's launch speed for player arc spells (Flame Arc I-VII, the Incantations,
        /// Nether Arc): the projectile weenies (flameboltgravity 20974 and its siblings,
        /// ace43232-netherarc) all have MaximumVelocity 40.</summary>
        public const float AceArcSpellSpeed = 40f;

        /// <summary>
        /// The path of an ARC SPELL, which ACE flies differently from a missile: a fixed
        /// HORIZONTAL speed (the projectile's MaximumVelocity, 40 m/s for every player arc) and
        /// a vertical launch speed chosen so the arc lands on the aim point under gravity 9.8
        /// (WorldObject_Magic.CalculateProjectileVelocity → Trajectory.solve_ballistic_arc_lateral,
        /// gravity only for ProjectileSpellType.Arc). So it always reaches, and rises
        /// g·d²/(8·v²) above the straight line at mid-flight: 0.3 m at 20 m, 1.2 m at 40 m,
        /// 2.8 m at 60 m. Same shape as <see cref="Solve"/> (height = x·tanθ − K·x²) with
        /// K = g/(2v²) and tanθ = rise/d + K·d.
        /// </summary>
        public static MissileArc SolveLateral(float ox, float oy, float oz,
                                              float tx, float ty, float tz,
                                              float lateralSpeed, float gravity = AcGravity)
        {
            float dx = tx - ox, dy = ty - oy;
            float rise = tz - oz;
            float d = (float)Math.Sqrt(dx * dx + dy * dy);

            if (float.IsNaN(d) || float.IsNaN(rise) || !(lateralSpeed > 0f) || !(gravity > 0f))
                return new MissileArc(ox, oy, oz, 0f, 0f, 0f, 0f, 0f, 0f, valid: false, vertical: false);

            if (d < VerticalEpsilon)
                return new MissileArc(ox, oy, oz, 0f, 0f, d, rise, 0f, 0f, valid: true, vertical: true);

            double k = gravity / (2.0 * lateralSpeed * lateralSpeed);
            double tan = rise / (double)d + k * d;
            return new MissileArc(ox, oy, oz, dx / d, dy / d, d, rise, (float)tan, (float)k, valid: true, vertical: false);
        }

        /// <summary>
        /// Extra height added to the tested path: <paramref name="clearance"/> at mid-flight,
        /// tapering to zero at both ends (4t(1−t)), so the launch and aim points stay put. It
        /// covers what the model can't know exactly: launch height, the real launch speed, the
        /// projectile's own size.
        /// </summary>
        public static float ClearanceBump(float t, float clearance)
        {
            if (clearance <= 0f || t <= 0f || t >= 1f) return 0f;
            return clearance * 4f * t * (1f - t);
        }

        /// <summary>Number of straight segments used to test an arc: about one per 2.5 m, 6 to 24.</summary>
        public static int SegmentCount(float horizDist)
        {
            if (float.IsNaN(horizDist) || horizDist <= 0f) return 6;
            int n = (int)Math.Ceiling(horizDist / 2.5f);
            return Math.Clamp(n, 6, 24);
        }

        /// <summary>
        /// Highest Z the tested path reaches (arc plus clearance), sampled like the geometry
        /// test samples it. Lets a caller compare the path against a known ceiling height.
        /// </summary>
        public static float MaxPathZ(in MissileArc arc, float clearance)
        {
            if (!arc.Valid) return float.NaN;
            int n = SegmentCount(arc.HorizDist) * 4;
            float best = Math.Max(arc.OZ, arc.OZ + arc.Rise);
            for (int i = 1; i < n; i++)
            {
                arc.PointAt((float)i / n, clearance, out _, out _, out float z);
                if (z > best) best = z;
            }
            return best;
        }
    }
}

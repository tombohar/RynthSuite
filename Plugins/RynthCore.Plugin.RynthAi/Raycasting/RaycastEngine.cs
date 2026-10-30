using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// Core raycasting engine for line-of-sight checks.
    /// Tests linear (spell/bolt) and parabolic (bow/thrown) trajectories
    /// against the loaded collision geometry.
    /// 
    /// NOTE: Uses custom Vector3 from this namespace, NOT System.Numerics.
    /// </summary>
    public static class RaycastEngine
    {
        /// <summary>
        /// Gravity constant for parabolic trajectory calculations.
        /// AC's gravity is approximately 9.81 m/s² but may need empirical tuning.
        /// </summary>
        private const float GRAVITY = 9.81f;

        /// <summary>
        /// Number of sample points along an arc trajectory for collision testing.
        /// Higher = more accurate but slower. 10 is a good balance.
        /// </summary>
        private const int ARC_SAMPLE_COUNT = 10;

        // Shoulder / height offsets used for multi-ray dungeon LOS checks.
        // Covers the approximate silhouette of a standing player character.
        private const float RayLateralOffset = 0.35f; // left/right shoulder spread (meters)
        private const float RayVerticalOffset = 0.30f; // up/down spread (meters)

        // ── Box pre-filter (2026-10-02 flood work) ─────────────────────────────────────────
        // Every ray used to call RayIntersect on every volume of the landblock (883 in the
        // Matron Hive, x5 rays per spell target in dungeons, for every monster in range 20
        // times a second). A TriangleMesh or AxisAlignedBox volume can only be hit inside its
        // Min/Max box (RayIntersect tests that box first), so a volume whose box doesn't touch
        // the segment's box is skipped without the call. Same verdicts: LosProof's 4281 pairs
        // give the same counts. Other volume types (sphere, cylinder, polygon) are always tested.
        private const float BoxSlack = 0.001f; // 1 mm, so float rounding can never skip a real hit

        // One spare candidate list, borrowed and given back (no allocation per check, safe if two
        // threads ever cast at once: the second just makes its own). Not [ThreadStatic]: the
        // plugin can live in a collectible context.
        private static List<BoundingVolume>? s_spareCandidates;

        private static bool BoxBounded(BoundingVolume v)
            => v.Type == BoundingVolume.VolumeType.TriangleMesh || v.Type == BoundingVolume.VolumeType.AxisAlignedBox;

        /// <summary>True when <paramref name="v"/> cannot touch anything inside [lo, hi].</summary>
        private static bool OutsideBox(BoundingVolume v, Vector3 lo, Vector3 hi)
        {
            if (!BoxBounded(v)) return false;
            Vector3 mn = v.Min, mx = v.Max;
            return mx.X < lo.X || mn.X > hi.X || mx.Y < lo.Y || mn.Y > hi.Y || mx.Z < lo.Z || mn.Z > hi.Z;
        }

        private static void SegmentBox(Vector3 a, Vector3 b, out Vector3 lo, out Vector3 hi)
        {
            Vector3 slack = new Vector3(BoxSlack, BoxSlack, BoxSlack);
            lo = Vector3.Min(a, b) - slack;
            hi = Vector3.Max(a, b) + slack;
        }

        /// <summary>The non-door volumes that could touch anything inside [lo, hi], into <paramref name="list"/>.</summary>
        private static void Candidates(List<BoundingVolume> geometry, Vector3 lo, Vector3 hi, List<BoundingVolume> list)
        {
            list.Clear();
            foreach (var v in geometry)
            {
                if (v.IsDoor || OutsideBox(v, lo, hi)) continue;
                list.Add(v);
            }
        }

        /// <summary>
        /// Tests if a straight-line path (magic spells, crossbow bolts) is blocked
        /// by any collision geometry.
        ///
        /// In dungeon mode (multiRay=true) casts 5 rays — center plus left/right/up/down
        /// offsets covering the player silhouette.  Any single blocked ray returns blocked.
        /// This catches thin corner geometry that a single center ray slips through.
        ///
        /// Returns true if the path IS blocked (an obstacle exists between origin and target).
        /// Returns false if the path is clear.
        /// </summary>
        public static bool IsLinearPathBlocked(Vector3 origin, Vector3 target,
                                               List<BoundingVolume> geometry,
                                               bool multiRay = false)
        {
            if (geometry == null || geometry.Count == 0)
                return false;

            if (float.IsNaN(origin.X) || float.IsNaN(origin.Y) || float.IsNaN(origin.Z) ||
                float.IsNaN(target.X) || float.IsNaN(target.Y) || float.IsNaN(target.Z))
                return false;

            if (multiRay)
            {
                // Build perpendicular axes for offset rays.
                // Lateral = direction × world-up, then re-derive true up from lateral × direction.
                Vector3 dir = (target - origin);
                float len = dir.Length();
                if (len < 1e-4f) return false;
                dir = dir / len;

                Vector3 worldUp = new Vector3(0, 0, 1);
                Vector3 lateral = Vector3.Cross(dir, worldUp);
                float latLen = lateral.Length();
                if (latLen < 1e-4f) lateral = new Vector3(1, 0, 0); // dir is vertical — use arbitrary lateral
                else lateral = lateral / latLen;

                Vector3 up = Vector3.Cross(lateral, dir);
                float upLen = up.Length();
                if (upLen > 1e-4f) up = up / upLen;

                Vector3 L = lateral * RayLateralOffset;
                Vector3 U = up       * RayVerticalOffset;

                // One pass picks the volumes whose boxes touch the box around all five rays;
                // the five rays then test only those (was five passes over every volume).
                Vector3 reach = new Vector3(
                    Math.Abs(L.X) + Math.Abs(U.X), Math.Abs(L.Y) + Math.Abs(U.Y), Math.Abs(L.Z) + Math.Abs(U.Z));
                SegmentBox(origin, target, out Vector3 lo, out Vector3 hi);
                var near = System.Threading.Interlocked.Exchange(ref s_spareCandidates, null) ?? new List<BoundingVolume>(256);
                try
                {
                    Candidates(geometry, lo - reach, hi + reach, near);
                    if (near.Count == 0) return false;

                    // 5 rays: center, left shoulder, right shoulder, slightly up, slightly down
                    if (IsSingleRayBlocked(origin,     target,     near)) return true;
                    if (IsSingleRayBlocked(origin - L, target - L, near)) return true;
                    if (IsSingleRayBlocked(origin + L, target + L, near)) return true;
                    if (IsSingleRayBlocked(origin + U, target + U, near)) return true;
                    if (IsSingleRayBlocked(origin - U, target - U, near)) return true;
                    return false;
                }
                finally
                {
                    near.Clear();
                    s_spareCandidates = near;
                }
            }

            return IsSingleRayBlocked(origin, target, geometry);
        }

        private static bool IsSingleRayBlocked(Vector3 origin, Vector3 target, List<BoundingVolume> geometry)
        {
            Vector3 delta = target - origin;
            float distanceToTarget = delta.Length();
            if (distanceToTarget < 1e-4f) return false;

            Vector3 direction = delta / distanceToTarget;
            SegmentBox(origin, target, out Vector3 lo, out Vector3 hi);

            foreach (var volume in geometry)
            {
                if (volume.IsDoor || OutsideBox(volume, lo, hi)) continue;

                float hitDist;
                if (volume.RayIntersect(origin, direction, distanceToTarget, out hitDist))
                {
                    // Ignore self-intersection at origin and hits within 0.3m of target
                    // (reduced from 1.0m — the old 1m zone was hiding corner walls next to mobs).
                    if (hitDist > 0.5f && hitDist < distanceToTarget - 0.3f)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Tests if a parabolic arc path (bows, thrown weapons) is blocked by geometry.
        /// 
        /// This allows attacks to arc over low obstacles like walls and rocks.
        /// The arc is sampled at discrete points and each segment is tested for
        /// intersection with geometry.
        /// 
        /// Returns true if the arc IS blocked.
        /// Returns false if the arc clears all obstacles.
        /// </summary>
        public static bool IsArcPathBlocked(Vector3 origin, Vector3 target, float initialVelocity,
                                             List<BoundingVolume> geometry)
        {
            if (geometry == null || geometry.Count == 0)
                return false;

            if (initialVelocity <= 0)
                return true; // Can't fire with no velocity

            // Validate inputs
            if (float.IsNaN(origin.X) || float.IsNaN(target.X))
                return true;

            Vector3 delta = target - origin;
            float horizontalDist = delta.Length2D();
            float verticalDist = delta.Z;

            // Check maximum range: v² / g
            float maxRange = (initialVelocity * initialVelocity) / GRAVITY;
            if (horizontalDist > maxRange)
                return true; // Out of range

            if (horizontalDist < 0.1f)
                return false; // Basically on top of target

            // Calculate launch angle for the desired range
            // Using the optimal angle that clears obstacles
            // θ = 0.5 * arcsin(g * d / v²) for flat terrain
            float sinArg = (GRAVITY * horizontalDist) / (initialVelocity * initialVelocity);
            sinArg = Math.Min(sinArg, 1.0f); // Clamp for float precision

            // Use the high arc (π/2 - θ) for better obstacle clearance
            float launchAngle = (float)(0.5 * Math.Asin(sinArg));
            if (launchAngle < 0.1f)
                launchAngle = (float)(Math.PI / 4); // Default to 45° if calculation fails

            float cosAngle = (float)Math.Cos(launchAngle);
            float sinAngle = (float)Math.Sin(launchAngle);

            // Calculate time of flight
            float vHorizontal = initialVelocity * cosAngle;
            float vVertical = initialVelocity * sinAngle;

            if (vHorizontal < 0.01f)
                return true; // Basically firing straight up

            float totalTime = horizontalDist / vHorizontal;

            // Horizontal direction (unit vector in XY plane)
            float hdx = delta.X / horizontalDist;
            float hdy = delta.Y / horizontalDist;

            // Sample the arc at discrete points and test each segment
            Vector3 prevPoint = origin;

            for (int i = 1; i <= ARC_SAMPLE_COUNT; i++)
            {
                float t = (float)i / ARC_SAMPLE_COUNT;
                float time = t * totalTime;

                // Kinematic equations.
                // Deep-audit finding #31 (2026-06-18): the old code computed
                // the flat-range parabola (assumes origin.Z == target.Z) as
                // `height`, then bolted on a SEPARATE `heightAdjust*(1-t)`
                // blend for the actual elevation difference — double-applying
                // the vertical offset. Neither the true ballistic curve nor a
                // straight line resulted; at t=1 the arc's own height term
                // (origin.Z + vVertical*totalTime - 0.5g*totalTime²) is
                // exactly origin.Z (that's what makes totalTime =
                // horizontalDist/vHorizontal the correct equal-height
                // time-of-flight), so the sample landed at origin.Z instead
                // of target.Z — biasing the LOS verdict for sloped shots.
                //
                // Fix: `parabolicSag` is that SAME flat-range height term
                // relative to origin.Z — by construction it is exactly 0 at
                // t=0 AND at t=1 (proven via the time-of-flight identity
                // above), so adding it to a straight lerp(origin.Z, target.Z)
                // never disturbs either endpoint, while still producing a
                // real parabolic bump in between. This anchors t=1 at
                // target.Z exactly (the final explicit segment below no
                // longer has to patch just the endpoint).
                float hDist = vHorizontal * time;
                float parabolicSag = vVertical * time - 0.5f * GRAVITY * time * time;

                Vector3 arcPoint = new Vector3(
                    origin.X + hdx * hDist,
                    origin.Y + hdy * hDist,
                    origin.Z + verticalDist * t + parabolicSag
                );

                // Test this segment for collisions
                if (IsSegmentBlocked(prevPoint, arcPoint, geometry))
                    return true;

                prevPoint = arcPoint;
            }

            // Test final segment to target
            if (IsSegmentBlocked(prevPoint, target, geometry))
                return true;

            return false; // Arc clears all obstacles
        }

        /// <summary>What <see cref="IsBallisticArcBlocked"/> found, for the LOS debug log and /ra lostest.</summary>
        public struct ArcLosResult
        {
            public bool  Blocked;
            public bool  OutOfReach;   // the launch speed can't carry the shot that far / that high
            public float Sag;          // how far the arc rises above the straight line, at most (m)
            public float Apex;         // highest point of the flight above the launch point (m)
            public float HitAlong;     // horizontal distance from the shooter to the hit (m)
            public float HitZ;         // height of the hit above the launch point (m)
        }

        /// <summary>
        /// Tests the path a missile really flies: the low ballistic arc at the weapon's launch
        /// speed (see <see cref="MissileBallistics"/>), raised by <paramref name="clearance"/>
        /// at mid-flight. Unlike <see cref="IsArcPathBlocked"/> (the old flat-ground
        /// approximation, no longer used by combat), the arc passes exactly through the aim point
        /// for any height difference. Floors and ceilings are part of the dungeon geometry, so
        /// an arc that rises into a ceiling reports blocked. Out of reach counts as blocked.
        /// Hits within 0.5 m of the shooter or 0.3 m of the target are ignored, as in the
        /// straight-line test.
        /// </summary>
        public static bool IsBallisticArcBlocked(Vector3 origin, Vector3 target, float speed, float clearance,
                                                 List<BoundingVolume> geometry, out ArcLosResult result)
        {
            result = default;
            if (float.IsNaN(origin.X) || float.IsNaN(origin.Y) || float.IsNaN(origin.Z) ||
                float.IsNaN(target.X) || float.IsNaN(target.Y) || float.IsNaN(target.Z))
                return false;

            var arc = MissileBallistics.Solve(origin.X, origin.Y, origin.Z, target.X, target.Y, target.Z, speed);
            return IsBallisticArcBlocked(origin, target, in arc, clearance, geometry, out result);
        }

        /// <summary>
        /// As above for an arc already solved from <paramref name="origin"/> to
        /// <paramref name="target"/>: a missile's (<see cref="MissileBallistics.Solve"/>) or an
        /// arc spell's (<see cref="MissileBallistics.SolveLateral"/>).
        /// </summary>
        public static bool IsBallisticArcBlocked(Vector3 origin, Vector3 target, in MissileArc arc, float clearance,
                                                 List<BoundingVolume> geometry, out ArcLosResult result)
        {
            result = default;
            if (float.IsNaN(origin.X) || float.IsNaN(origin.Y) || float.IsNaN(origin.Z) ||
                float.IsNaN(target.X) || float.IsNaN(target.Y) || float.IsNaN(target.Z))
                return false;

            result.Sag  = arc.MaxRiseAboveChord;
            result.Apex = arc.ApexAboveLaunch;
            if (!arc.Valid)
            {
                result.OutOfReach = true;
                result.Blocked = true;
                return true;
            }
            if (geometry == null || geometry.Count == 0)
                return false;

            clearance = Math.Max(0f, clearance);
            int n = MissileBallistics.SegmentCount(arc.HorizDist);
            Vector3 prev = origin;
            float travelled = 0f;

            for (int i = 1; i <= n; i++)
            {
                float t = (float)i / n;
                Vector3 cur;
                if (i == n) cur = target;
                else
                {
                    arc.PointAt(t, clearance, out float px, out float py, out float pz);
                    cur = new Vector3(px, py, pz);
                }

                Vector3 seg = cur - prev;
                float len = seg.Length();
                if (len > 1e-4f)
                {
                    Vector3 dir = seg / len;
                    SegmentBox(prev, cur, out Vector3 lo, out Vector3 hi);
                    foreach (var volume in geometry)
                    {
                        if (volume.IsDoor || OutsideBox(volume, lo, hi)) continue;
                        if (!volume.RayIntersect(prev, dir, len, out float hd)) continue;
                        if (hd < 0f || hd > len) continue;
                        if (travelled + hd < 0.5f) continue;          // at the shooter
                        if (i == n && hd > len - 0.3f) continue;      // at the target
                        Vector3 hit = prev + dir * hd;
                        float hx = hit.X - origin.X, hy = hit.Y - origin.Y;
                        result.Blocked  = true;
                        result.HitAlong = (float)Math.Sqrt(hx * hx + hy * hy);
                        result.HitZ     = hit.Z - origin.Z;
                        return true;
                    }
                }
                travelled += len;
                prev = cur;
            }
            return false;
        }

        /// <summary>
        /// Tests if a line segment between two points intersects any geometry.
        /// Used internally for arc sampling.
        /// </summary>
        private static bool IsSegmentBlocked(Vector3 start, Vector3 end, List<BoundingVolume> geometry)
        {
            Vector3 delta = end - start;
            float dist = delta.Length();

            if (dist < 0.01f) return false;

            Vector3 dir = delta / dist;
            SegmentBox(start, end, out Vector3 lo, out Vector3 hi);

            foreach (var volume in geometry)
            {
                if (volume.IsDoor || OutsideBox(volume, lo, hi)) continue;

                float hitDist;
                if (volume.RayIntersect(start, dir, dist, out hitDist))
                {
                    if (hitDist > 0.05f && hitDist < dist - 0.05f)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Quick distance-based pre-filter: checks if ANY geometry exists near the line
        /// between origin and target. Used to skip the full raycast for wide-open areas.
        /// Returns true if there is nearby geometry worth testing.
        /// </summary>
        public static bool HasNearbyGeometry(Vector3 origin, Vector3 target, List<BoundingVolume> geometry, float margin = 5.0f)
        {
            if (geometry == null || geometry.Count == 0)
                return false;

            // Compute a rough bounding box for the path
            Vector3 pathMin = Vector3.Min(origin, target) - new Vector3(margin, margin, margin);
            Vector3 pathMax = Vector3.Max(origin, target) + new Vector3(margin, margin, margin);

            foreach (var vol in geometry)
            {
                if (vol.IsDoor) continue;

                // Quick AABB overlap test
                if (vol.Max.X >= pathMin.X && vol.Min.X <= pathMax.X &&
                    vol.Max.Y >= pathMin.Y && vol.Min.Y <= pathMax.Y &&
                    vol.Max.Z >= pathMin.Z && vol.Min.Z <= pathMax.Z)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

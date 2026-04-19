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

                // 5 rays: center, left shoulder, right shoulder, slightly up, slightly down
                if (IsSingleRayBlocked(origin,     target,     geometry)) return true;
                if (IsSingleRayBlocked(origin - L, target - L, geometry)) return true;
                if (IsSingleRayBlocked(origin + L, target + L, geometry)) return true;
                if (IsSingleRayBlocked(origin + U, target + U, geometry)) return true;
                if (IsSingleRayBlocked(origin - U, target - U, geometry)) return true;
                return false;
            }

            return IsSingleRayBlocked(origin, target, geometry);
        }

        private static bool IsSingleRayBlocked(Vector3 origin, Vector3 target, List<BoundingVolume> geometry)
        {
            Vector3 delta = target - origin;
            float distanceToTarget = delta.Length();
            if (distanceToTarget < 1e-4f) return false;

            Vector3 direction = delta / distanceToTarget;

            foreach (var volume in geometry)
            {
                if (volume.IsDoor) continue;

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

                // Kinematic equations
                float hDist = vHorizontal * time;
                float height = origin.Z + vVertical * time - 0.5f * GRAVITY * time * time;

                // Adjust height to interpolate between origin.Z and target.Z
                // (accounts for height difference between origin and target)
                float heightAdjust = verticalDist * t;

                Vector3 arcPoint = new Vector3(
                    origin.X + hdx * hDist,
                    origin.Y + hdy * hDist,
                    height + heightAdjust * (1.0f - t) // Blend in height difference
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

            foreach (var volume in geometry)
            {
                if (volume.IsDoor) continue;

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

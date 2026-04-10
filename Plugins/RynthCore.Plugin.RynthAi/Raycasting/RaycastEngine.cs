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

        /// <summary>
        /// Tests if a straight-line path (magic spells, crossbow bolts) is blocked
        /// by any collision geometry.
        /// 
        /// Returns true if the path IS blocked (an obstacle exists between origin and target).
        /// Returns false if the path is clear.
        /// </summary>
        public static bool IsLinearPathBlocked(Vector3 origin, Vector3 target, List<BoundingVolume> geometry)
        {
            if (geometry == null || geometry.Count == 0)
                return false;

            // Validate inputs
            if (float.IsNaN(origin.X) || float.IsNaN(origin.Y) || float.IsNaN(origin.Z) ||
                float.IsNaN(target.X) || float.IsNaN(target.Y) || float.IsNaN(target.Z))
                return false;

            // Calculate direction and distance
            Vector3 delta = target - origin;
            float distanceToTarget = delta.Length();

            // Origin and target are the same point — no obstruction possible
            if (distanceToTarget < 1e-4f)
                return false;

            // Normalize direction
            Vector3 direction = delta / distanceToTarget;

            // Test ray against each bounding volume
            foreach (var volume in geometry)
            {
                // Skip doors (state is unreliable)
                if (volume.IsDoor)
                    continue;

                // Test intersection
                float hitDist;
                if (volume.RayIntersect(origin, direction, distanceToTarget, out hitDist))
                {
                    // Ignore hits right at the player origin (self-intersection) and
                    // hits right at the target (walls behind/adjacent to the target).
                    if (hitDist > 0.5f && hitDist < distanceToTarget - 1.0f)
                    {
                        return true; // Path is blocked
                    }
                }
            }

            return false; // Path is clear
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

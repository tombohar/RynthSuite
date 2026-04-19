using System;
using System.Collections.Generic;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// Provides line-of-sight checking for the combat system.
    /// Converts AC client coordinates to global meter space and
    /// performs raycasting against loaded landblock geometry.
    ///
    /// Coordinate system:
    ///   AC uses a landblock grid where each block is 192x192 meters.
    ///   Landcell ID format: 0xXXYYnnnn where XX=east-west block, YY=north-south block.
    ///   LocationX/Y are local offsets within the landblock (0-192 meters).
    ///
    ///   We convert everything to "global meters" for raycasting:
    ///     GlobalX = (XX * 192) + LocationX
    ///     GlobalY = (YY * 192) + LocationY
    ///     GlobalZ = LocationZ (altitude, 0 = sea level)
    /// </summary>
    public class TargetingFSM
    {
        private readonly GeometryLoader _geoLoader;
        private readonly BlacklistManager _blacklist;

        // Attack type determines which raycast to use
        public enum AttackType
        {
            Linear,    // Bolts, streaks, crossbow bolts, melee
            BowArc,    // Bows — moderate arc, arrows go higher than you'd think
            ThrownArc, // Thrown weapons, atlatls — similar arc to bows
            MagicArc   // War/Void magic Arc spells — same trajectory as missile weapons
        }

        // AC arrows arc noticeably — they go HIGH. A lower velocity = higher arc.
        // At 70 yard range, arrows visibly arc 3-4 meters above the direct line.
        // This must match the actual game trajectory to detect ceiling hits in dungeons.
        public float BowArcVelocity { get; set; } = 25.0f;

        // Thrown weapons arc similarly to bows
        public float ThrownArcVelocity { get; set; } = 22.0f;

        // Magic arc spells (War/Void Arc) have the same trajectory as missile weapons
        public float MagicArcVelocity { get; set; } = 25.0f;

        // If true, use arc checks for missile weapons. If false, treat all as linear.
        public bool UseArcs { get; set; } = true;

        // Max scan distance in meters. Only check LOS for targets within this range.
        // Set from CombatManager based on MonsterRange + buffer.
        public float MaxScanDistanceMeters { get; set; } = 120.0f;

        public TargetingFSM(GeometryLoader geoLoader, BlacklistManager blacklist)
        {
            _geoLoader = geoLoader ?? throw new ArgumentNullException(nameof(geoLoader));
            _blacklist = blacklist ?? throw new ArgumentNullException(nameof(blacklist));
        }

        /// <summary>
        /// Checks if line-of-sight to a target is blocked by geometry.
        ///
        /// Returns true if the target IS blocked (should skip this target).
        /// Returns false if the target is clear to attack.
        /// </summary>
        public bool IsTargetBlocked(RynthCoreHost host, uint targetId, AttackType attackType)
        {
            if (!_geoLoader.IsInitialized)
                return false;

            try
            {
                Vector3 origin = GetPlayerPosition(host);
                if (origin == Vector3.Zero)
                    return false;

                Vector3 targetPos = GetObjectPosition(host, targetId);
                if (targetPos == Vector3.Zero)
                    return false;

                // Early-out: skip raycast if target is beyond scan distance
                float dx = targetPos.X - origin.X;
                float dy = targetPos.Y - origin.Y;
                float flatDist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (flatDist > MaxScanDistanceMeters)
                    return false; // Too far to bother checking, let combat handle range

                // Offset to chest height
                origin.Z += 1.0f;
                targetPos.Z += 1.0f;

                uint landcell = GetPlayerLandcell(host);
                uint cellPart = landcell & 0xFFFF;
                bool isDungeon = cellPart >= 0x0100;

                // Force linear checks indoors — arc trajectories hit ceilings
                if (isDungeon)
                    attackType = AttackType.Linear;

                var geometry = _geoLoader.GetLandblockGeometry(landcell);

                if (geometry == null || geometry.Count == 0)
                    return false;

                // Pre-filter: skip full ray test if no geometry near the path
                float margin = Math.Min(flatDist * 0.3f, 15.0f);
                margin = Math.Max(margin, 3.0f);
                if (!RaycastEngine.HasNearbyGeometry(origin, targetPos, geometry, margin))
                    return false;

                // In dungeons use multi-ray checks — 5 rays covering the player silhouette
                // catch thin corner geometry that a single center ray slips through.
                switch (attackType)
                {
                    case AttackType.Linear:
                        return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);

                    case AttackType.BowArc:
                        if (UseArcs)
                        {
                            if (!RaycastEngine.IsArcPathBlocked(origin, targetPos, BowArcVelocity, geometry))
                                return false;
                            return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);
                        }
                        return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);

                    case AttackType.ThrownArc:
                        if (UseArcs)
                        {
                            if (!RaycastEngine.IsArcPathBlocked(origin, targetPos, ThrownArcVelocity, geometry))
                                return false;
                            return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);
                        }
                        return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);

                    case AttackType.MagicArc:
                        if (UseArcs && !isDungeon)
                        {
                            if (!RaycastEngine.IsArcPathBlocked(origin, targetPos, MagicArcVelocity, geometry))
                                return false;
                            return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: false);
                        }
                        return RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);

                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Targeting] Error checking LOS: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Finds the best unblocked target from a list of candidates.
        /// Returns the target ID, or 0 if no clear target is found.
        /// </summary>
        public uint FindBestTarget(RynthCoreHost host, List<uint> candidateIds, AttackType attackType)
        {
            if (candidateIds == null || candidateIds.Count == 0)
                return 0;

            foreach (uint id in candidateIds)
            {
                try
                {
                    // Skip blacklisted targets
                    if (_blacklist.IsBlacklisted((int)id))
                        continue;

                    // Skip blocked targets
                    if (IsTargetBlocked(host, id, attackType))
                    {
                        System.Diagnostics.Debug.WriteLine($"[Targeting] Target 0x{id:X8} blocked by geometry, skipping");
                        continue;
                    }

                    return id; // Found a clear target
                }
                catch
                {
                    continue;
                }
            }

            return 0; // No clear targets
        }

        /// <summary>
        /// Gets the player's position in global meter coordinates.
        /// Uses RynthCoreHost.TryGetPlayerPose for landcell + local position.
        /// </summary>
        private Vector3 GetPlayerPosition(RynthCoreHost host)
        {
            try
            {
                if (!host.TryGetPlayerPose(out uint objCellId, out float localX, out float localY, out float localZ,
                        out _, out _, out _, out _))
                    return Vector3.Zero;

                uint blockX = (objCellId >> 24) & 0xFF;
                uint blockY = (objCellId >> 16) & 0xFF;

                float globalX = blockX * 192.0f + localX;
                float globalY = blockY * 192.0f + localY;

                return new Vector3(globalX, globalY, localZ);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Targeting] Error getting player pos: {ex.Message}");
                return Vector3.Zero;
            }
        }

        /// <summary>
        /// Gets an object's position in global meter coordinates.
        /// Uses RynthCoreHost.TryGetObjectPosition for landcell + local position.
        /// </summary>
        private Vector3 GetObjectPosition(RynthCoreHost host, uint objectId)
        {
            try
            {
                if (!host.TryGetObjectPosition(objectId, out uint objCellId, out float localX, out float localY, out float localZ))
                    return Vector3.Zero;

                uint blockX = (objCellId >> 24) & 0xFF;
                uint blockY = (objCellId >> 16) & 0xFF;

                float globalX = blockX * 192.0f + localX;
                float globalY = blockY * 192.0f + localY;

                return new Vector3(globalX, globalY, localZ);
            }
            catch
            {
                return Vector3.Zero;
            }
        }

        /// <summary>
        /// Gets the player's current Landcell value for geometry lookup.
        /// </summary>
        private uint GetPlayerLandcell(RynthCoreHost host)
        {
            try
            {
                if (host.TryGetPlayerPose(out uint objCellId, out _, out _, out _, out _, out _, out _, out _))
                    return objCellId;
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Determines the attack type based on combat mode and wielded weapon name.
        /// Magic mode ALWAYS returns Linear (spells travel straight).
        /// Bows get a flat arc (high velocity). Thrown weapons arc more.
        /// Melee and crossbows are Linear.
        ///
        /// Combat modes: 1=noncombat, 2=melee, 4=missile, 8=magic
        /// </summary>
        public AttackType DetermineAttackType(int currentCombatMode, string wieldedWeaponName)
        {
            try
            {
                // MAGIC MODE: Always linear — spells (bolts, streaks, arcs, rings)
                // all use straight-line LOS in AC, regardless of equipped weapon
                if (currentCombatMode == 8)
                    return AttackType.Linear;

                // PEACE MODE: Default to linear
                if (currentCombatMode == 1)
                    return AttackType.Linear;

                // MELEE MODE: Linear (range check only, no projectile arc)
                if (currentCombatMode == 2)
                    return AttackType.Linear;

                // MISSILE MODE: Check the weapon name for bow vs crossbow vs thrown
                if (currentCombatMode == 4 && !string.IsNullOrEmpty(wieldedWeaponName))
                {
                    string name = wieldedWeaponName.ToLower();

                    // Crossbows fire bolts in a straight line
                    if (name.Contains("crossbow"))
                        return AttackType.Linear;

                    // Atlatls and thrown weapons arc more
                    if (name.Contains("atlatl") || name.Contains("thrown") || name.Contains("dart"))
                        return UseArcs ? AttackType.ThrownArc : AttackType.Linear;

                    // Regular bows — fast, flat arc
                    if (name.Contains("bow"))
                        return UseArcs ? AttackType.BowArc : AttackType.Linear;

                    // Unknown missile weapon — treat as bow arc
                    return UseArcs ? AttackType.BowArc : AttackType.Linear;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Targeting] Error determining attack type: {ex.Message}");
            }

            return AttackType.Linear; // Default to linear
        }
    }
}

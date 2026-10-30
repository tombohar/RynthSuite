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
            Linear,       // Peace, magic, or arcs-disabled missiles
            BowArc,       // Bows — moderate arc, arrows go higher than you'd think
            CrossbowArc,  // Crossbows — fastest projectile, flattest arc
            AtlatlArc,    // Atlatls, thrown weapons, darts — similar arc to bows
            MagicArc,     // War/Void magic Arc spells: fixed horizontal speed, gravity (MissileBallistics.SolveLateral)
            Melee         // Melee: one straight ray chest to chest, no silhouette rays (see IsPathBlocked)
        }

        // Lower velocity = higher arc. Defaults tuned against in-game trajectories;
        // CombatManager overrides these from LegacyUiSettings each tick so the user
        // can fine-tune per-weapon in the Misc advanced-settings tab.
        public float BowArcVelocity      { get; set; } = 25.0f;
        public float CrossbowArcVelocity { get; set; } = 40.0f;
        public float AtlatlArcVelocity   { get; set; } = 22.0f;
        // Arc spells: the HORIZONTAL speed (ACE flies them at a fixed lateral speed, 40 m/s for
        // every player arc; see MissileBallistics.SolveLateral). Lower = higher arc.
        public float MagicArcVelocity    { get; set; } = MissileBallistics.AceArcSpellSpeed;

        /// <summary>
        /// How far above the chest-height line (feet + 1 m) an arc spell leaves the caster. ACE
        /// spawns arc projectiles at the caster's full height (CalculatePreOffset startFactor 1.0
        /// for Arc, 2/3 for every other shape), about 1.8 m for a character, and aims them at
        /// 5/6 of the target's height (taken as the usual chest line, feet + 1 m).
        /// </summary>
        public const float MagicArcLaunchAboveChest = 0.8f;

        // If true, use arc checks for missile weapons. If false, treat all as linear.
        public bool UseArcs { get; set; } = true;

        // Extra headroom (m) the missile arc must have at mid-flight, on top of the modelled
        // flight path. Covers launch height, the real launch speed and the projectile's size.
        // CombatManager overrides it from LegacyUiSettings.MissileArcClearance each tick.
        public float MissileArcClearance { get; set; } = 0.5f;

        // An arc that rises less than this above the straight line is flown as that line:
        // the straight-line test (multi-ray in dungeons, ±0.3 m) already covers it, and the
        // clearance bump is not applied to short, flat shots.
        private const float ArcSagIgnoreMeters = 0.15f;

        /// <summary>How a LOS verdict was reached, for the LOS debug log.</summary>
        public struct LosDetail
        {
            public AttackType Type;
            public bool  Dungeon;
            public bool  Checked;       // false = no verdict computed (no geometry near, too far, no position)
            public float Velocity;      // launch speed used for a missile arc (0 = straight line)
            public bool  LineBlocked;
            public bool  ArcChecked;
            public RaycastEngine.ArcLosResult Arc;
        }

        private static bool IsMissileArc(AttackType t) =>
            t == AttackType.BowArc || t == AttackType.CrossbowArc || t == AttackType.AtlatlArc;

        /// <summary>
        /// The path an arc spell flies from <paramref name="chestOrigin"/> to
        /// <paramref name="chestTarget"/> (both at chest height, feet + 1 m): it leaves
        /// <see cref="MagicArcLaunchAboveChest"/> higher, at <paramref name="lateralSpeed"/>
        /// horizontally, and lands on the target. <paramref name="launch"/> is that start point.
        /// </summary>
        public static MissileArc MagicArcPath(Vector3 chestOrigin, Vector3 chestTarget, float lateralSpeed, out Vector3 launch)
        {
            launch = new Vector3(chestOrigin.X, chestOrigin.Y, chestOrigin.Z + MagicArcLaunchAboveChest);
            return MissileBallistics.SolveLateral(launch.X, launch.Y, launch.Z,
                                                  chestTarget.X, chestTarget.Y, chestTarget.Z, lateralSpeed);
        }

        /// <summary>Launch speed configured for a missile attack type.</summary>
        public float VelocityFor(AttackType t) => t switch
        {
            AttackType.BowArc      => BowArcVelocity,
            AttackType.CrossbowArc => CrossbowArcVelocity,
            AttackType.AtlatlArc   => AtlatlArcVelocity,
            AttackType.MagicArc    => MagicArcVelocity,
            _                      => 0f,
        };

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
            => IsTargetBlocked(host, targetId, attackType, out _);

        /// <summary>
        /// As <see cref="IsTargetBlocked(RynthCoreHost, uint, AttackType)"/>, and reports how the
        /// verdict was reached.
        ///
        /// Missile weapons (bow, crossbow, atlatl) with UseArcs on: the target must pass the
        /// straight line (walls, corner seams) AND the arc the missile really flies, low
        /// ballistic solution at the weapon's launch speed plus MissileArcClearance headroom.
        /// Indoors this is what catches ceilings: the old code forced a straight-line test in
        /// dungeons, so a target 20-50 m down a corridor passed LOS while the arrow, rising
        /// 2-6 m on its way there, hit the ceiling (2026-09-27, Longbow, Olthoi swarm).
        /// </summary>
        public bool IsTargetBlocked(RynthCoreHost host, uint targetId, AttackType attackType, out LosDetail detail)
        {
            detail = default;
            detail.Type = attackType;
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

                return IsPathBlocked(GetPlayerLandcell(host), origin, targetPos, attackType, out detail);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Targeting] Error checking LOS: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Would an ARC SPELL cast at <paramref name="targetId"/> now reach it? The straight line
        /// (walls) and the arc the spell really flies (ceilings, in dungeons too), whatever
        /// UseArcs says (that setting is about missile target selection). True = blocked.
        /// No geometry or no positions: false (clear), the arc is cast as before.
        /// </summary>
        public bool IsMagicArcBlocked(RynthCoreHost host, uint targetId, out LosDetail detail)
        {
            detail = default;
            detail.Type = AttackType.MagicArc;
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
                return IsPathBlockedCore(GetPlayerLandcell(host), origin, targetPos, AttackType.MagicArc, forceArc: true, out detail);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Targeting] Error checking magic arc: {ex.Message}");
                return false;
            }
        }

        /// <summary>As <see cref="IsMagicArcBlocked(RynthCoreHost, uint, out LosDetail)"/> for
        /// known feet positions (global meters), for tools running against the dats offline.</summary>
        public bool IsMagicArcPathBlocked(uint landcell, Vector3 origin, Vector3 targetPos, out LosDetail detail)
            => IsPathBlockedCore(landcell, origin, targetPos, AttackType.MagicArc, forceArc: true, out detail);

        /// <summary>
        /// The LOS verdict for known positions: <paramref name="origin"/> is the player's feet
        /// and <paramref name="targetPos"/> the target's feet, both in global meters
        /// (landblock * 192 + landblock-local origin), <paramref name="landcell"/> the
        /// player's cell. No client reads, so tools can run it against the dat files offline
        /// (Tools/RynthCore.LosProof).
        /// </summary>
        public bool IsPathBlocked(uint landcell, Vector3 origin, Vector3 targetPos, AttackType attackType, out LosDetail detail)
            => IsPathBlockedCore(landcell, origin, targetPos, attackType, forceArc: false, out detail);

        private bool IsPathBlockedCore(uint landcell, Vector3 origin, Vector3 targetPos, AttackType attackType, bool forceArc, out LosDetail detail)
        {
            detail = default;
            detail.Type = attackType;
            if (!_geoLoader.IsInitialized)
                return false;

            try
            {
                // Early-out: skip raycast if target is beyond scan distance
                float dx = targetPos.X - origin.X;
                float dy = targetPos.Y - origin.Y;
                float flatDist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (flatDist > MaxScanDistanceMeters)
                    return false; // Too far to bother checking, let combat handle range

                // Offset to chest height
                origin.Z += 1.0f;
                targetPos.Z += 1.0f;

                uint cellPart = landcell & 0xFFFF;
                bool isDungeon = cellPart >= 0x0100;
                detail.Dungeon = isDungeon;

                // Arc spells fly the arc ACE gives them (fixed horizontal speed, from the
                // caster's head), tested like a missile's: straight line, then the arc, ceilings
                // included. They used to get the old flat-ground arc outdoors and a straight line
                // in dungeons, so an arc into a low ceiling passed.
                bool magicArc = attackType == AttackType.MagicArc && (UseArcs || forceArc);
                bool missileArc = (UseArcs && IsMissileArc(attackType)) || magicArc;

                // Arcs off: a straight line indoors. Melee keeps its own test.
                if (isDungeon && !missileArc && attackType != AttackType.Melee)
                    attackType = AttackType.Linear;
                detail.Type = attackType;

                var geometry = _geoLoader.GetLandblockGeometry(landcell);

                if (geometry == null || geometry.Count == 0)
                    return false;

                // The missile's real path: solved up front so the pre-filter box covers its apex.
                MissileArc arc = default;
                Vector3 launch = origin;
                float arcHeadroom = 0f;
                if (missileArc)
                {
                    detail.Velocity = VelocityFor(attackType);
                    arc = magicArc
                        ? MagicArcPath(origin, targetPos, detail.Velocity, out launch)
                        : MissileBallistics.Solve(origin.X, origin.Y, origin.Z,
                                                  targetPos.X, targetPos.Y, targetPos.Z, detail.Velocity);
                    arcHeadroom = arc.ApexAboveLaunch + (launch.Z - origin.Z) + Math.Max(0f, MissileArcClearance) + 0.5f;
                }

                // Pre-filter: skip full ray test if no geometry near the path
                float margin = Math.Min(flatDist * 0.3f, 15.0f);
                margin = Math.Max(margin, 3.0f);
                margin = Math.Max(margin, arcHeadroom);
                if (!RaycastEngine.HasNearbyGeometry(origin, targetPos, geometry, margin))
                    return false;

                detail.Checked = true;

                if (missileArc)
                {
                    // Walls first (cheap, and most blocked targets stop here). In dungeons use
                    // multi-ray: 5 rays covering the player silhouette catch thin corner
                    // geometry that a single center ray slips through.
                    detail.LineBlocked = RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);
                    if (detail.LineBlocked)
                        return true;

                    // A short, flat shot is the straight line just tested.
                    if (arc.Valid && arc.MaxRiseAboveChord < ArcSagIgnoreMeters)
                    {
                        detail.Arc.Sag  = arc.MaxRiseAboveChord;
                        detail.Arc.Apex = arc.ApexAboveLaunch;
                        return false;
                    }

                    detail.ArcChecked = true;
                    return RaycastEngine.IsBallisticArcBlocked(launch, targetPos, in arc,
                        MissileArcClearance, geometry, out detail.Arc);
                }

                // In dungeons use multi-ray checks — 5 rays covering the player silhouette
                // catch thin corner geometry that a single center ray slips through.
                switch (attackType)
                {
                    case AttackType.Linear:
                    case AttackType.BowArc:       // arcs off (UseArcs=false) → straight line
                    case AttackType.CrossbowArc:
                    case AttackType.AtlatlArc:
                    case AttackType.MagicArc:
                        detail.LineBlocked = RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: isDungeon);
                        return detail.LineBlocked;

                    // Melee: nothing flies, so the only question is whether a wall stands
                    // between the two bodies: one ray, chest to chest. The five silhouette rays
                    // (0.35 m to each side, 0.3 m up and down) are there for spells slipping
                    // through corner seams; for melee they clip door jambs and hide a monster
                    // seen through a doorway at an angle. Offline in landblock 0x6346
                    // (Tools/RynthCore.LosProof) they blocked 297 of 1404 such open lines, the
                    // single ray 88, and both block every one of 199 walls, also with the
                    // player and the monster pressed against it.
                    case AttackType.Melee:
                        detail.LineBlocked = RaycastEngine.IsLinearPathBlocked(origin, targetPos, geometry, multiRay: false);
                        return detail.LineBlocked;

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
        /// Missile weapons each get their own arc when UseArcs is true: bow, crossbow,
        /// or atlatl. Melee and peace modes are Linear.
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

                // MELEE MODE: one straight ray, no projectile arc
                if (currentCombatMode == 2)
                    return AttackType.Melee;

                // MISSILE MODE: Check the weapon name for bow vs crossbow vs thrown.
                // No name (weapon not resolved yet) is still a missile shot: bow arc. The
                // combat scan used to pass "" here, which fell through to Linear, so no missile
                // LOS check ever used an arc or the per-weapon velocities.
                if (currentCombatMode == 4 && string.IsNullOrEmpty(wieldedWeaponName))
                    return UseArcs ? AttackType.BowArc : AttackType.Linear;

                if (currentCombatMode == 4)
                {
                    string name = wieldedWeaponName.ToLower();

                    // Crossbows — flattest arc, high velocity
                    if (name.Contains("crossbow"))
                        return UseArcs ? AttackType.CrossbowArc : AttackType.Linear;

                    // Atlatls and thrown weapons arc more
                    if (name.Contains("atlatl") || name.Contains("thrown") || name.Contains("dart"))
                        return UseArcs ? AttackType.AtlatlArc : AttackType.Linear;

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

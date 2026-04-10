using System;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// Bounding volume for collision detection
    /// Supports ray-volume intersection testing for raycasting
    /// </summary>
    public class BoundingVolume
    {
        public enum VolumeType
        {
            Sphere = 1,
            Cylinder = 2,
            Ellipsoid = 3,
            Polygon = 4,
            AxisAlignedBox = 5,
            Torus = 6,
            TriangleMesh = 7
        }

        public VolumeType Type { get; set; }
        public Vector3 Center { get; set; } // X, Y, Z
        public Vector3 Dimensions { get; set; } // Radius, Height, etc
        public Vector3[] Vertices { get; set; } // For polygons

        /// <summary>
        /// Triangle mesh data — groups of 3 consecutive Vector3 vertices.
        /// Used by TriangleMesh type for precise building collision.
        /// AABB (Min/Max) is used as a pre-filter before testing triangles.
        /// </summary>
        public Vector3[] MeshTriangles { get; set; }

        // Additional properties for geometry loading
        public Vector3 Min { get; set; } // Minimum bounds
        public Vector3 Max { get; set; } // Maximum bounds
        public bool IsDoor { get; set; } // Whether this is a door (passable)
        public bool NoAabbFallback { get; set; } // If true, only count actual triangle hits (no AABB fallback)

        public BoundingVolume()
        {
            Center = new Vector3(0, 0, 0);
            Dimensions = new Vector3(0, 0, 0);
            Min = new Vector3(0, 0, 0);
            Max = new Vector3(0, 0, 0);
            IsDoor = false;
        }

        /// <summary>
        /// Test if a ray intersects this volume
        /// Returns true if collision detected
        /// </summary>
        public bool RayIntersect(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;

            try
            {
                switch (Type)
                {
                    case VolumeType.Sphere:
                        return RayIntersectSphere(rayStart, rayDir, maxDist, out hitDist);
                    case VolumeType.Cylinder:
                        return RayIntersectCylinder(rayStart, rayDir, maxDist, out hitDist);
                    case VolumeType.AxisAlignedBox:
                        return RayIntersectAABB(rayStart, rayDir, maxDist, out hitDist);
                    case VolumeType.Polygon:
                        return RayIntersectPolygon(rayStart, rayDir, maxDist, out hitDist);
                    case VolumeType.TriangleMesh:
                        if (MeshTriangles != null && MeshTriangles.Length >= 3)
                            return RayIntersectTriangleMesh(rayStart, rayDir, maxDist, out hitDist);
                        return RayIntersectAABB(rayStart, rayDir, maxDist, out hitDist);
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Overload to accept float arrays for backwards compatibility
        /// </summary>
        public bool RayIntersect(float[] rayStart, float[] rayDir, float maxDist, out float hitDist)
        {
            return RayIntersect(
                Vector3.FromArray(rayStart),
                Vector3.FromArray(rayDir),
                maxDist,
                out hitDist
            );
        }

        /// <summary>
        /// Ray-Sphere intersection
        /// </summary>
        private bool RayIntersectSphere(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;

            Vector3 oc = rayStart - Center;

            float a = Vector3.Dot(rayDir, rayDir);
            float b = 2.0f * Vector3.Dot(oc, rayDir);
            float c = Vector3.Dot(oc, oc) - (Dimensions.X * Dimensions.X);

            float discriminant = b * b - 4 * a * c;
            if (discriminant < 0)
                return false;

            float sqrt_disc = (float)Math.Sqrt(discriminant);
            float t1 = (-b - sqrt_disc) / (2 * a);
            float t2 = (-b + sqrt_disc) / (2 * a);

            if (t1 > 0 && t1 < maxDist)
            {
                hitDist = t1;
                return true;
            }

            // Ray starts inside sphere (t1 < 0, t2 > 0)
            if (t1 <= 0 && t2 > 0 && t2 < maxDist)
            {
                hitDist = 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ray-Cylinder intersection (vertical cylinder along Z axis).
        /// Tests the curved surface and top/bottom caps.
        /// </summary>
        private bool RayIntersectCylinder(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;

            float radius = Dimensions.X;
            float bottomZ = Min.Z;
            float topZ = Max.Z;

            // Project onto XY plane for infinite cylinder test
            float dx = rayStart.X - Center.X;
            float dy = rayStart.Y - Center.Y;

            float a = rayDir.X * rayDir.X + rayDir.Y * rayDir.Y;
            float b = 2.0f * (dx * rayDir.X + dy * rayDir.Y);
            float c = dx * dx + dy * dy - radius * radius;

            // Nearly vertical ray — check if inside cylinder radius, then test caps
            if (a < 1e-6f)
            {
                if (c > 0) return false; // Outside cylinder radius
                if (Math.Abs(rayDir.Z) < 1e-6f) { hitDist = 0; return true; } // Inside, horizontal

                float tBot = (bottomZ - rayStart.Z) / rayDir.Z;
                float tTop = (topZ - rayStart.Z) / rayDir.Z;
                float tNear = Math.Min(tBot, tTop);
                float tFar = Math.Max(tBot, tTop);
                if (tNear > maxDist || tFar < 0) return false;
                hitDist = tNear > 0 ? tNear : 0;
                return true;
            }

            float disc = b * b - 4 * a * c;
            if (disc < 0)
            {
                // No side hit — but caps may still intersect if ray enters from top/bottom
                goto CheckCaps;
            }

            float sqrtDisc = (float)Math.Sqrt(disc);
            float t1 = (-b - sqrtDisc) / (2 * a);
            float t2 = (-b + sqrtDisc) / (2 * a);

            // Check side hits against height bounds
            float z1 = rayStart.Z + t1 * rayDir.Z;
            if (t1 > 0 && t1 < maxDist && z1 >= bottomZ && z1 <= topZ)
            {
                hitDist = t1;
                return true;
            }

            float z2 = rayStart.Z + t2 * rayDir.Z;
            if (t2 > 0 && t2 < maxDist && z2 >= bottomZ && z2 <= topZ)
            {
                hitDist = t2;
                return true;
            }

            // Ray starts inside cylinder
            if (t1 <= 0 && t2 > 0 && rayStart.Z >= bottomZ && rayStart.Z <= topZ)
            {
                hitDist = 0;
                return true;
            }

        CheckCaps:
            // Test top and bottom cap discs
            if (Math.Abs(rayDir.Z) > 1e-6f)
            {
                float tBot = (bottomZ - rayStart.Z) / rayDir.Z;
                if (tBot > 0 && tBot < maxDist)
                {
                    float hx = rayStart.X + tBot * rayDir.X - Center.X;
                    float hy = rayStart.Y + tBot * rayDir.Y - Center.Y;
                    if (hx * hx + hy * hy <= radius * radius)
                    {
                        hitDist = tBot;
                        return true;
                    }
                }

                float tTop = (topZ - rayStart.Z) / rayDir.Z;
                if (tTop > 0 && tTop < maxDist)
                {
                    float hx = rayStart.X + tTop * rayDir.X - Center.X;
                    float hy = rayStart.Y + tTop * rayDir.Y - Center.Y;
                    if (hx * hx + hy * hy <= radius * radius)
                    {
                        hitDist = tTop;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Ray-AABB intersection
        /// </summary>
        private bool RayIntersectAABB(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;

            float tmin = 0, tmax = maxDist;

            for (int i = 0; i < 3; i++)
            {
                float rayStartCoord = i == 0 ? rayStart.X : (i == 1 ? rayStart.Y : rayStart.Z);
                float rayDirCoord = i == 0 ? rayDir.X : (i == 1 ? rayDir.Y : rayDir.Z);
                float minCoord = i == 0 ? Min.X : (i == 1 ? Min.Y : Min.Z);
                float maxCoord = i == 0 ? Max.X : (i == 1 ? Max.Y : Max.Z);

                if (Math.Abs(rayDirCoord) < 0.00001f)
                {
                    if (rayStartCoord < minCoord || rayStartCoord > maxCoord)
                        return false;
                }
                else
                {
                    float t1 = (minCoord - rayStartCoord) / rayDirCoord;
                    float t2 = (maxCoord - rayStartCoord) / rayDirCoord;

                    if (t1 > t2)
                    {
                        float temp = t1;
                        t1 = t2;
                        t2 = temp;
                    }

                    tmin = Math.Max(tmin, t1);
                    tmax = Math.Min(tmax, t2);

                    if (tmin > tmax)
                        return false;
                }
            }

            if (tmin > 0 && tmin < maxDist)
            {
                hitDist = tmin;
                return true;
            }

            // Ray starts inside the box (tmin <= 0 but tmax > 0)
            // This means the player is inside/overlapping the obstacle
            if (tmin <= 0 && tmax > 0 && tmax < maxDist)
            {
                hitDist = 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Ray-Polygon intersection
        /// </summary>
        private bool RayIntersectPolygon(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;
            if (Vertices == null || Vertices.Length < 3)
                return false;

            for (int i = 0; i < Vertices.Length - 2; i++)
            {
                if (RayIntersectTriangle(rayStart, rayDir, Vertices[0], Vertices[i + 1], Vertices[i + 2], out float t))
                {
                    if (t >= 0 && t < maxDist && t < hitDist)
                    {
                        hitDist = t;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Ray-TriangleMesh intersection.
        /// Uses AABB as a pre-filter, then tests each triangle (Möller-Trumbore).
        /// MeshTriangles stores groups of 3 consecutive vertices per triangle.
        /// </summary>
        private bool RayIntersectTriangleMesh(Vector3 rayStart, Vector3 rayDir, float maxDist, out float hitDist)
        {
            hitDist = float.MaxValue;

            // AABB test — the AABB covers the full building extent (all vertices, not just physics)
            if (!RayIntersectAABB(rayStart, rayDir, maxDist, out float aabbDist))
                return false;

            // Test physics triangles for precise hit
            for (int i = 0; i + 2 < MeshTriangles.Length; i += 3)
            {
                if (RayIntersectTriangle(rayStart, rayDir, MeshTriangles[i], MeshTriangles[i + 1], MeshTriangles[i + 2], out float t))
                {
                    if (t >= 0 && t < maxDist && t < hitDist)
                        hitDist = t;
                }
            }

            if (hitDist < float.MaxValue)
                return true;

            // No triangle hit but AABB hit — for buildings with incomplete physics mesh,
            // fall back to AABB. For dungeon physics polygons (NoAabbFallback=true),
            // no triangle hit means the ray passed through an opening (doorway/corridor).
            if (NoAabbFallback)
                return false;

            hitDist = aabbDist;
            return true;
        }

        /// <summary>
        /// Ray-Triangle intersection (Möller-Trumbore algorithm)
        /// </summary>
        private bool RayIntersectTriangle(Vector3 rayStart, Vector3 rayDir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = float.MaxValue;
            const float EPSILON = 0.0000001f;

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;

            Vector3 h = Vector3.Cross(rayDir, edge2);
            float a = Vector3.Dot(edge1, h);

            if (a > -EPSILON && a < EPSILON)
                return false;

            float f = 1.0f / a;
            Vector3 s = rayStart - v0;

            float u = f * Vector3.Dot(s, h);
            if (u < 0 || u > 1)
                return false;

            Vector3 q = Vector3.Cross(s, edge1);
            float v = f * Vector3.Dot(rayDir, q);

            if (v < 0 || u + v > 1)
                return false;

            t = f * Vector3.Dot(edge2, q);
            return t >= 0;
        }
    }
}

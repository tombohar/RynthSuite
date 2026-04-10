using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// A position + rotation in AC's 3D world.
    /// Position is in local landblock meters (0-192 for outdoors).
    /// Rotation is a quaternion (W, X, Y, Z).
    /// </summary>
    public class Frame
    {
        public float OriginX, OriginY, OriginZ;
        public float RotW, RotX, RotY, RotZ;

        public Frame()
        {
            RotW = 1.0f; // Identity quaternion
        }

        public void Unpack(BinaryReader reader)
        {
            OriginX = reader.ReadSingle();
            OriginY = reader.ReadSingle();
            OriginZ = reader.ReadSingle();
            RotW = reader.ReadSingle();
            RotX = reader.ReadSingle();
            RotY = reader.ReadSingle();
            RotZ = reader.ReadSingle();
        }

        /// <summary>
        /// Rotates a point by this frame's quaternion.
        /// q * v * q^-1 (for unit quaternion, q^-1 = conjugate)
        /// </summary>
        public Vector3 RotatePoint(Vector3 point)
        {
            // Quaternion rotation: v' = q * v * q*
            float qw = RotW, qx = RotX, qy = RotY, qz = RotZ;

            // Cross product of quaternion xyz with point
            float cx = qy * point.Z - qz * point.Y;
            float cy = qz * point.X - qx * point.Z;
            float cz = qx * point.Y - qy * point.X;

            // Result = point + 2 * (qw * cross + qxyz x cross)
            float cx2 = qy * cz - qz * cy;
            float cy2 = qz * cx - qx * cz;
            float cz2 = qx * cy - qy * cx;

            return new Vector3(
                point.X + 2.0f * (qw * cx + cx2),
                point.Y + 2.0f * (qw * cy + cy2),
                point.Z + 2.0f * (qw * cz + cz2)
            );
        }

        /// <summary>
        /// Transforms a point: rotate then translate.
        /// </summary>
        public Vector3 TransformPoint(Vector3 localPoint)
        {
            Vector3 rotated = RotatePoint(localPoint);
            return new Vector3(
                rotated.X + OriginX,
                rotated.Y + OriginY,
                rotated.Z + OriginZ
            );
        }

        public override string ToString()
        {
            return $"Pos({OriginX:F2}, {OriginY:F2}, {OriginZ:F2}) Rot({RotW:F3}, {RotX:F3}, {RotY:F3}, {RotZ:F3})";
        }
    }

    /// <summary>
    /// A static object placed on a landblock (building, tree, rock, structure, etc.)
    /// </summary>
    public class StaticObject
    {
        public uint InstanceId;   // Unique placement ID
        public uint SetupId;      // Reference to 0x02xxxxxx Setup in portal.dat
        public Frame Placement;   // Position and rotation in landblock space

        public StaticObject()
        {
            Placement = new Frame();
        }
    }

    /// <summary>
    /// Parsed landblock info from cell.dat (file ID = 0xXXYYFFFE).
    /// Contains static object placements (Stabs) and building info for an outdoor landblock.
    /// 
    /// Format (from ACE.DatLoader.FileTypes.LandblockInfo):
    ///   uint32 Id
    ///   uint32 NumCells
    ///   uint32 NumObjects              (List&lt;Stab&gt; count)
    ///   Stab[NumObjects]:
    ///     uint32 Id                    (model/setup reference, 0x01xx or 0x02xx)
    ///     Frame  (7 floats: pos.xyz + rot.wxyz = 28 bytes)
    ///   ushort NumBuildings
    ///   ushort PackMask
    ///   BuildInfo[NumBuildings]:
    ///     uint32 ModelId
    ///     Frame  (28 bytes)
    ///     uint32 NumLeaves
    ///     List&lt;CBldPortal&gt; Portals
    ///   if (PackMask &amp; 1):
    ///     PackedHashTable RestrictionTables
    /// </summary>
    public class LandblockInfo
    {
        public uint Id;
        public uint NumCells;
        public List<StaticObject> StaticObjects = new List<StaticObject>();
        public bool HasObjects;

        /// <summary>
        /// Parse landblock info from raw .dat file data using the ACE format.
        /// </summary>
        public bool Unpack(byte[] data)
        {
            if (data == null || data.Length < 12)
                return false;

            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    Id = reader.ReadUInt32();
                    NumCells = reader.ReadUInt32();

                    // Read List<Stab> — uint32 count + count × Stab entries
                    uint numObjects = reader.ReadUInt32();
                    
                    if (numObjects > 5000) numObjects = 5000; // Sanity

                    for (uint i = 0; i < numObjects; i++)
                    {
                        if (ms.Position + 32 > ms.Length) break; // Need 4 (Id) + 28 (Frame)

                        var obj = new StaticObject();
                        obj.SetupId = reader.ReadUInt32();
                        obj.Placement.Unpack(reader);
                        StaticObjects.Add(obj);
                    }

                    HasObjects = StaticObjects.Count > 0;
                    Log($"Landblock 0x{Id:X8}: {StaticObjects.Count} static objects");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Error parsing landblock 0x{Id:X8}: {ex.Message}");
                return false;
            }
        }

        private static void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine($"[LandblockInfo] {msg}");
        }
    }

    /// <summary>
    /// Parsed environment cell from cell.dat (file ID = 0xXXYY0001 to 0xXXYYFFFE).
    /// Represents an indoor dungeon room with walls, portals, and static objects.
    /// </summary>
    public class EnvCellInfo
    {
        public uint Id;
        public uint Flags;
        public List<Vector3> Vertices = new List<Vector3>();
        public List<CellPolygon> Polygons = new List<CellPolygon>();
        public List<ushort> PortalPolygonIndices = new List<ushort>();
        public List<StaticObject> StaticObjects = new List<StaticObject>();
        public List<uint> ConnectedCells = new List<uint>();
        public uint EnvironmentId;  // Reference to portal.dat environment
        public uint StructureId;    // Reference to portal.dat cell structure

        /// <summary>
        /// Parse environment cell from raw data.
        /// 
        /// Format (simplified):
        ///   uint32 id
        ///   uint32 flags
        ///   uint32 environmentId (0x0Dxxxxxx)
        ///   uint16 cellStructId
        ///   uint16 position (index into structure)
        ///   Frame placement
        ///   uint32 numPortals
        ///   for each portal:
        ///     uint16 polygonId (portal polygon index)
        ///     uint16 flags
        ///     uint32 connectedCell
        ///   uint32 numStaticObjects
        ///   for each static object:
        ///     uint32 setupId
        ///     Frame placement
        /// </summary>
        public bool Unpack(byte[] data)
        {
            if (data == null || data.Length < 12)
                return false;

            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    Id = reader.ReadUInt32();
                    Flags = reader.ReadUInt32();

                    // Read environment/structure references
                    if (ms.Position + 8 > ms.Length) return false;

                    // The exact format depends on flags, but typically:
                    EnvironmentId = reader.ReadUInt32();

                    if (ms.Position + 4 > ms.Length) return false;
                    StructureId = reader.ReadUInt32();

                    // Read cell frame (position + rotation)
                    if ((Flags & 0x02) != 0 && ms.Position + 28 <= ms.Length)
                    {
                        var frame = new Frame();
                        frame.Unpack(reader);
                    }

                    // Read portals
                    if ((Flags & 0x04) != 0 || ms.Position + 4 <= ms.Length)
                    {
                        if (ms.Position + 4 <= ms.Length)
                        {
                            uint numPortals = reader.ReadUInt32();
                            if (numPortals > 1000) numPortals = 0; // Sanity

                            for (uint i = 0; i < numPortals && ms.Position + 8 <= ms.Length; i++)
                            {
                                ushort polyId = reader.ReadUInt16();
                                ushort portalFlags = reader.ReadUInt16();
                                uint connCell = reader.ReadUInt32();

                                PortalPolygonIndices.Add(polyId);
                                ConnectedCells.Add(connCell);
                            }
                        }
                    }

                    // Read static objects
                    if (ms.Position + 4 <= ms.Length)
                    {
                        uint numObjects = reader.ReadUInt32();
                        if (numObjects > 10000) numObjects = 0;

                        for (uint i = 0; i < numObjects && ms.Position + 32 <= ms.Length; i++)
                        {
                            var obj = new StaticObject();
                            obj.SetupId = reader.ReadUInt32();
                            obj.Placement.Unpack(reader);
                            StaticObjects.Add(obj);
                        }
                    }

                    Log($"EnvCell 0x{Id:X8}: {StaticObjects.Count} objects, " +
                        $"{PortalPolygonIndices.Count} portals");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Error parsing EnvCell 0x{Id:X8}: {ex.Message}");
                return false;
            }
        }

        private static void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine($"[EnvCell] {msg}");
        }
    }

    /// <summary>
    /// A polygon in a cell structure (wall, floor, ceiling).
    /// </summary>
    public class CellPolygon
    {
        public ushort[] VertexIndices;
        public bool IsPortal; // Portal polygons are openings, not solid walls
    }

    /// <summary>
    /// Parsed Setup file from portal.dat (file ID = 0x02xxxxxx).
    /// Matches ACE.DatLoader.FileTypes.SetupModel.Unpack() exactly.
    /// 
    /// Format:
    ///   uint32 Id
    ///   uint32 Flags (SetupFlags)
    ///   uint32 numParts
    ///   uint32[numParts] Parts (GfxObj references, 0x01xxxxxx)
    ///   if (Flags & 0x01): uint32[numParts] ParentIndex
    ///   if (Flags & 0x02): Vector3[numParts] DefaultScale
    ///   Dictionary&lt;int,LocationType&gt; HoldingLocations  (uint32 count + entries)
    ///   Dictionary&lt;int,LocationType&gt; ConnectionPoints   (uint32 count + entries)
    ///   int32 placementsCount
    ///   PlacementType[placementsCount]: key(int32) + AnimFrame(numParts × Frame(28) + uint32 numHooks + hooks)
    ///   List&lt;CylSphere&gt; CylSpheres (uint32 count + count × 20 bytes)
    ///   List&lt;Sphere&gt; Spheres (uint32 count + count × 16 bytes)
    ///   float Height, Radius, StepUpHeight, StepDownHeight
    ///   Sphere SortingSphere (Vector3 + float = 16 bytes)
    ///   Sphere SelectionSphere (16 bytes)
    ///   Dictionary&lt;int,LightInfo&gt; Lights
    ///   uint32 × 5 defaults
    /// </summary>
    public class SetupInfo
    {
        public uint Id;
        public uint Flags;
        public int NumParts;
        public uint[] PartIds;

        // Bounding sphere (from SortingSphere)
        public float BoundingSphereRadius;
        public Vector3 BoundingSphereCenter;

        // Physics dimensions
        public float Height;
        public float Radius; // Physics collision radius

        // Collision spheres
        public List<CollisionSphere> Spheres = new List<CollisionSphere>();

        // Collision cylinders
        public List<CollisionCylinder> Cylinders = new List<CollisionCylinder>();

        // Default pose part frames (from first placement in Setup)
        // Used to compute composite building bounding volumes.
        public Frame[] PartFrames;

        public bool HasBounds;

        public bool Unpack(byte[] data)
        {
            if (data == null || data.Length < 16)
                return false;

            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    // --- Header ---
                    Id = reader.ReadUInt32();
                    Flags = reader.ReadUInt32();

                    uint numParts = reader.ReadUInt32();
                    if (numParts > 500) return false;
                    NumParts = (int)numParts;

                    PartIds = new uint[numParts];
                    for (int i = 0; i < numParts; i++)
                        PartIds[i] = reader.ReadUInt32();

                    // --- Optional arrays based on flags ---
                    // HasParent = 0x01
                    if ((Flags & 0x01) != 0)
                        ms.Seek(numParts * 4, SeekOrigin.Current);

                    // HasDefaultScale = 0x02
                    if ((Flags & 0x02) != 0)
                        ms.Seek(numParts * 12, SeekOrigin.Current); // Vector3 = 12 bytes

                    // --- Skip HoldingLocations: uint32 count + count × (key(4) + PartId(4) + Frame(28)) ---
                    if (!SkipHashTable(reader, ms, 36)) return TryFallbackParse(data);

                    // --- Skip ConnectionPoints: same format ---
                    if (!SkipHashTable(reader, ms, 36)) return TryFallbackParse(data);

                    // --- Skip PlacementFrames ---
                    if (ms.Position + 4 > ms.Length) return TryFallbackParse(data);
                    int placementsCount = reader.ReadInt32();
                    if (placementsCount < 0 || placementsCount > 100) return TryFallbackParse(data);

                    for (int p = 0; p < placementsCount; p++)
                    {
                        if (ms.Position + 4 > ms.Length) return TryFallbackParse(data);
                        reader.ReadInt32(); // key

                        // AnimationFrame: numParts × Frame(28 bytes) + uint32 numHooks
                        for (int part = 0; part < numParts; part++)
                        {
                            if (ms.Position + 28 > ms.Length) return TryFallbackParse(data);
                            if (p == 0) // Store default pose part frames for composite bounds
                            {
                                if (PartFrames == null) PartFrames = new Frame[numParts];
                                PartFrames[part] = new Frame();
                                PartFrames[part].Unpack(reader);
                            }
                            else
                            {
                                ms.Seek(28, SeekOrigin.Current);
                            }
                        }

                        if (ms.Position + 4 > ms.Length) return TryFallbackParse(data);
                        uint numHooks = reader.ReadUInt32();
                        if (numHooks > 0)
                        {
                            // AnimationHooks are variable-size; can't easily skip
                            // But we already stored PartFrames from the default placement above
                            return TryFallbackParse(data);
                        }
                    }

                    // --- NOW we're at the good stuff! ---
                    return ReadBoundsData(reader, ms, data);
                }
            }
            catch
            {
                return TryFallbackParse(data);
            }
        }

        /// <summary>
        /// Reads CylSpheres, Spheres, Height, Radius, SortingSphere from current position.
        /// </summary>
        private bool ReadBoundsData(BinaryReader reader, MemoryStream ms, byte[] data)
        {
            try
            {
                // CylSpheres: uint32 count + count × (Vector3 origin(12) + float radius(4) + float height(4) = 20)
                if (ms.Position + 4 > ms.Length) return false;
                uint numCylSpheres = reader.ReadUInt32();
                if (numCylSpheres > 50) return TryFallbackParse(data);

                for (uint i = 0; i < numCylSpheres; i++)
                {
                    if (ms.Position + 20 > ms.Length) break;
                    var cyl = new CollisionCylinder();
                    cyl.BottomCenter = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    cyl.Radius = reader.ReadSingle();
                    cyl.Height = reader.ReadSingle();
                    if (cyl.Radius > 0 && cyl.Height > 0 && !float.IsNaN(cyl.Radius))
                        Cylinders.Add(cyl);
                }

                // Spheres: uint32 count + count × (Vector3 origin(12) + float radius(4) = 16)
                if (ms.Position + 4 > ms.Length) return false;
                uint numSpheres = reader.ReadUInt32();
                if (numSpheres > 50) return TryFallbackParse(data);

                for (uint i = 0; i < numSpheres; i++)
                {
                    if (ms.Position + 16 > ms.Length) break;
                    var sphere = new CollisionSphere();
                    sphere.Center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    sphere.Radius = reader.ReadSingle();
                    if (sphere.Radius > 0 && !float.IsNaN(sphere.Radius))
                        Spheres.Add(sphere);
                }

                // Height, Radius, StepUpHeight, StepDownHeight
                if (ms.Position + 16 > ms.Length) return false;
                Height = reader.ReadSingle();
                Radius = reader.ReadSingle();
                float stepUp = reader.ReadSingle();
                float stepDown = reader.ReadSingle();

                // SortingSphere: Vector3 center + float radius
                if (ms.Position + 16 > ms.Length) return false;
                float sx = reader.ReadSingle(), sy = reader.ReadSingle(), sz = reader.ReadSingle();
                float sr = reader.ReadSingle();

                BoundingSphereCenter = new Vector3(sx, sy, sz);
                BoundingSphereRadius = sr;
                HasBounds = sr > 0.001f || Radius > 0.001f;

                // If SortingSphere radius is 0, use the physics Radius
                if (BoundingSphereRadius < 0.001f && Radius > 0.001f)
                    BoundingSphereRadius = Radius;

                Log($"Setup 0x{Id:X8}: bounds OK — radius={BoundingSphereRadius:F2}, height={Height:F2}, " +
                    $"{Cylinders.Count} cyls, {Spheres.Count} spheres");
                return true;
            }
            catch
            {
                return TryFallbackParse(data);
            }
        }

        /// <summary>
        /// Skip a hash table: uint32 count + count × entries of entrySize bytes.
        /// </summary>
        private bool SkipHashTable(BinaryReader reader, MemoryStream ms, int entrySize)
        {
            if (ms.Position + 4 > ms.Length) return false;
            uint count = reader.ReadUInt32();
            if (count > 1000) return false;
            long skip = (long)count * entrySize;
            if (ms.Position + skip > ms.Length) return false;
            ms.Seek(skip, SeekOrigin.Current);
            return true;
        }

        /// <summary>
        /// Fallback: scan from the end of the file for the known tail structure.
        /// The tail is always: 
        ///   ... SortingSphere(16) + SelectionSphere(16) + Lights(uint32 count + entries) + 5×uint32(20)
        /// If Lights is empty (count=0 → 4 bytes), tail is 56 bytes from end.
        /// </summary>
        private bool TryFallbackParse(byte[] data)
        {
            try
            {
                // Read header to get PartIds at minimum
                if (PartIds == null && data.Length >= 16)
                {
                    using (var ms2 = new MemoryStream(data))
                    using (var r2 = new BinaryReader(ms2))
                    {
                        Id = r2.ReadUInt32();
                        Flags = r2.ReadUInt32();
                        uint np = r2.ReadUInt32();
                        if (np <= 500)
                        {
                            NumParts = (int)np;
                            PartIds = new uint[np];
                            for (int i = 0; i < np && ms2.Position + 4 <= ms2.Length; i++)
                                PartIds[i] = r2.ReadUInt32();
                        }
                    }
                }

                // Try to find SortingSphere by scanning from end
                // Known tail (if Lights empty): 5×uint32(20) + Lights(4) + SelectionSphere(16) + SortingSphere(16) + 4×float(16)
                // = 72 bytes from end for start of Height/Radius/StepUp/StepDown

                int[] tailOffsets = { 72, 78, 84 }; // Try different Lights sizes (4, 10, 16 bytes)

                foreach (int tailOff in tailOffsets)
                {
                    int offset = data.Length - tailOff;
                    if (offset < 16 || offset + 48 > data.Length) continue;

                    float h = BitConverter.ToSingle(data, offset);
                    float r = BitConverter.ToSingle(data, offset + 4);
                    float sx = BitConverter.ToSingle(data, offset + 16);
                    float sy = BitConverter.ToSingle(data, offset + 20);
                    float sz = BitConverter.ToSingle(data, offset + 24);
                    float sr = BitConverter.ToSingle(data, offset + 28);

                    // Validate: Height and Radius should be positive, sphere radius positive, center reasonable
                    if (h >= 0 && h < 100 && r > 0 && r < 200 &&
                        sr > 0 && sr < 500 &&
                        !float.IsNaN(h) && !float.IsNaN(r) && !float.IsNaN(sr) &&
                        Math.Abs(sx) < 1000 && Math.Abs(sy) < 1000 && Math.Abs(sz) < 1000)
                    {
                        Height = h;
                        Radius = r;
                        BoundingSphereCenter = new Vector3(sx, sy, sz);
                        BoundingSphereRadius = sr;
                        HasBounds = true;

                        Log($"Setup 0x{Id:X8}: FALLBACK found bounds at tailOff={tailOff} — radius={sr:F2}, height={h:F2}");
                        return true;
                    }
                }

                Log($"Setup 0x{Id:X8}: FALLBACK failed, no valid bounds found");
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine($"[Setup] {msg}");
        }
    }

    /// <summary>
    /// Parsed GfxObj from portal.dat (file ID = 0x01xxxxxx).
    /// Contains 3D mesh data. We extract the bounding sphere and AABB.
    /// </summary>
    public class GfxObjInfo
    {
        public uint Id;
        public uint Flags;

        // Bounding data (what we need for raycasting)
        public Vector3 SortingCenter;
        public float SortingRadius;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public bool HasBounds;

        // Vertex data (for precise bounds calculation)
        public List<Vector3> Vertices = new List<Vector3>();

        /// <summary>
        /// Parse GfxObj to extract bounding information.
        /// We don't need the full mesh/texture data for raycasting.
        /// 
        /// Format (simplified):
        ///   uint32 id
        ///   uint32 flags
        ///   -- surface data (skip) --
        ///   uint32 numVertices
        ///   for each vertex:
        ///     float3 position
        ///     float3 normal
        ///     (optional UV data based on flags)
        ///   -- polygon data (skip) --
        ///   -- BSP data (skip for now) --
        ///   float3 sortingCenter
        ///   float sortingRadius
        /// </summary>
        public bool Unpack(byte[] data)
        {
            if (data == null || data.Length < 12)
                return false;

            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    Id = reader.ReadUInt32();
                    Flags = reader.ReadUInt32();

                    // Surfaces use SmartArray: compressed uint32 count + uint32[] IDs
                    if (ms.Position + 1 > ms.Length) return false;
                    uint numSurfaces = ReadCompressedUInt32(reader);
                    if (numSurfaces > 10000) return false;
                    ms.Seek(numSurfaces * 4, SeekOrigin.Current);

                    // CVertexArray: int32 vertexType + uint32 numVertices
                    if (ms.Position + 8 > ms.Length) return false;
                    int vertexType = reader.ReadInt32();
                    uint numVertices = reader.ReadUInt32();
                    if (numVertices > 100000 || vertexType != 1) return false;

                    // Read vertices and compute AABB
                    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

                    for (uint i = 0; i < numVertices; i++)
                    {
                        // Per vertex: ushort key, ushort numUVs, float3 pos, float3 normal, numUVs*float2 UVs
                        if (ms.Position + 28 > ms.Length) break;

                        reader.ReadUInt16(); // vertex key
                        ushort numUVs = reader.ReadUInt16();

                        float x = reader.ReadSingle();
                        float y = reader.ReadSingle();
                        float z = reader.ReadSingle();

                        if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)) continue;

                        Vertices.Add(new Vector3(x, y, z));

                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                        minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z);

                        // Skip normal (3 floats) + UV data (numUVs * 2 floats)
                        long skip = 12 + (long)numUVs * 8;
                        if (ms.Position + skip > ms.Length) break;
                        ms.Seek(skip, SeekOrigin.Current);
                    }

                    if (Vertices.Count > 0)
                    {
                        BoundsMin = new Vector3(minX, minY, minZ);
                        BoundsMax = new Vector3(maxX, maxY, maxZ);
                        HasBounds = true;

                        // Compute bounding sphere from AABB
                        SortingCenter = new Vector3(
                            (minX + maxX) * 0.5f,
                            (minY + maxY) * 0.5f,
                            (minZ + maxZ) * 0.5f
                        );
                        SortingRadius = (BoundsMax - SortingCenter).Length();
                    }

                    // Also try to find the explicit sorting sphere at the end of file
                    TryReadSortingSphere(data);

                    Log($"GfxObj 0x{Id:X8}: {Vertices.Count} verts, radius={SortingRadius:F2}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"Error parsing GfxObj 0x{Id:X8}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads a variable-length compressed uint32 (AC SmartArray count format).
        /// </summary>
        private static uint ReadCompressedUInt32(BinaryReader reader)
        {
            byte b0 = reader.ReadByte();
            if ((b0 & 0x80) == 0) return b0;
            byte b1 = reader.ReadByte();
            if ((b0 & 0x40) == 0) return (uint)(((b0 & 0x7F) << 8) | b1);
            ushort s = reader.ReadUInt16();
            return (uint)((((b0 & 0x3F) << 8) | b1) << 16 | s);
        }

        /// <summary>
        /// Scans the end of the file for the sorting sphere data.
        /// The sorting center + radius are typically near the end of the GfxObj.
        /// </summary>
        private void TryReadSortingSphere(byte[] data)
        {
            // The last 16 bytes before any trailing data often contain the sorting sphere
            if (data.Length < 20) return;

            // Try reading from the last 16 bytes
            for (int offset = data.Length - 16; offset >= Math.Max(0, data.Length - 64); offset -= 4)
            {
                if (offset + 16 > data.Length) continue;

                float cx = BitConverter.ToSingle(data, offset);
                float cy = BitConverter.ToSingle(data, offset + 4);
                float cz = BitConverter.ToSingle(data, offset + 8);
                float r = BitConverter.ToSingle(data, offset + 12);

                if (r > 0.01f && r < 500f && !float.IsNaN(r) && !float.IsInfinity(r) &&
                    !float.IsNaN(cx) && !float.IsNaN(cy) && !float.IsNaN(cz) &&
                    Math.Abs(cx) < 1000 && Math.Abs(cy) < 1000 && Math.Abs(cz) < 1000)
                {
                    SortingCenter = new Vector3(cx, cy, cz);
                    SortingRadius = r;
                    break;
                }
            }
        }

        private static void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine($"[GfxObj] {msg}");
        }
    }

    /// <summary>
    /// A spherical collision volume from a Setup file.
    /// </summary>
    public class CollisionSphere
    {
        public Vector3 Center;
        public float Radius;
    }

    /// <summary>
    /// A cylindrical collision volume from a Setup file.
    /// </summary>
    public class CollisionCylinder
    {
        public Vector3 BottomCenter;
        public float Radius;
        public float Height;
    }
}

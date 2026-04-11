using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// Extracts dungeon wall geometry from cell.dat Environment files for raycasting.
    /// 
    /// Pipeline:
    ///   EnvCell (0xXXYY01nn) → EnvironmentId (0x0Dxxxx) + CellStructure index
    ///   Environment (0x0Dxxxx) → CellStruct at index → Vertices + Polygons
    ///   Transform polygon vertices by cell position → BoundingVolumes for raycasting
    /// 
    /// Binary formats (from ACE source):
    ///   SWVertex: ushort numUVs, float3 Origin, float3 Normal, Vec2Duv[numUVs]
    ///   CVertexArray: int32 type=1, uint32 count, (ushort key + SWVertex)[count]
    ///   Polygon: byte numPts, byte stippling, int32 sidesType, short posSurf, short negSurf,
    ///            short[numPts] vertexIds, byte[numPts] posUV (if !NoPos), byte[numPts] negUV (if clockwise+!NoNeg)
    ///   CellStruct: uint32 numPolygons, uint32 numPhysicsPolygons, uint32 numPortals,
    ///               CVertexArray, Dictionary{ushort,Polygon}[numPolygons], ...
    /// </summary>
    public class DungeonLOS
    {
        private DatDatabase _portalDat;
        private DatDatabase _cellDat;

        // Cache: EnvironmentId → physics CellStruct geometry (for raycasting)
        private readonly Dictionary<uint, Dictionary<uint, CellGeometry>> _envCache =
            new Dictionary<uint, Dictionary<uint, CellGeometry>>();

        // Cache: EnvironmentId → render CellStruct geometry (for map, portals tagged)
        private readonly Dictionary<uint, Dictionary<uint, CellGeometry>> _renderEnvCache =
            new Dictionary<uint, Dictionary<uint, CellGeometry>>();

        // Cache: landblock → dungeon wall volumes
        private readonly Dictionary<uint, List<BoundingVolume>> _wallCache =
            new Dictionary<uint, List<BoundingVolume>>();

        // Cache: landblock → map polygons (wall + portal, with Z layer)
        private readonly Dictionary<uint, List<MapPolygon>> _mapCache =
            new Dictionary<uint, List<MapPolygon>>();

        // Cache: landblock → one MapCell per EnvCell (for gap-free floor fills)
        private readonly Dictionary<uint, List<MapCell>> _mapCellCache =
            new Dictionary<uint, List<MapCell>>();

        private const int MAX_ENV_CACHE = 50;
        private const int MAX_WALL_CACHE = 20;

        /// <summary>
        /// A single dungeon polygon in world space, tagged with its cell's Z position
        /// so the map renderer can group by floor level.
        /// IsPortal = true means this is a doorway opening (render differently or skip).
        /// </summary>
        public class MapPolygon
        {
            public Vector3[] Vertices;
            public float CellX, CellY, CellZ; // world-space cell centre
            public bool IsPortal;
        }

        /// <summary>
        /// World-space centre of an EnvCell, used to draw a filled 10×10-unit floor tile.
        /// AC dungeon cells are always 10×10 units; filling by cell center eliminates all
        /// inter-polygon gaps without needing polygon edge geometry.
        /// </summary>
        public class MapCell
        {
            public float WorldX, WorldY, CellZ;
            public uint  EnvironmentId;   // 0x0D.... portal.dat id — selects the tile image
            public float Rotation;        // yaw in radians (CCW, from quaternion RotW/RotZ)
        }

        public void Initialize(DatDatabase portalDat, DatDatabase cellDat)
        {
            _portalDat = portalDat;
            _cellDat = cellDat;
        }

        /// <summary>
        /// Gets dungeon wall collision volumes for a landblock.
        /// Returns empty list for outdoor-only landblocks.
        /// </summary>
        public List<BoundingVolume> GetDungeonWalls(uint landblockKey)
        {
            if (_wallCache.TryGetValue(landblockKey, out var cached))
                return cached;

            var walls = LoadDungeonWalls(landblockKey);

            if (_wallCache.Count >= MAX_WALL_CACHE)
            {
                foreach (var key in _wallCache.Keys) { _wallCache.Remove(key); break; }
            }
            _wallCache[landblockKey] = walls;
            return walls;
        }

        /// <summary>
        /// Gets dungeon polygons for 2D map rendering.
        /// Each polygon carries its cell's Z so the UI can offer per-floor views.
        /// Portal polygons are included and flagged so doorways render as openings.
        /// </summary>
        public List<MapPolygon> GetDungeonMapPolygons(uint landblockKey)
        {
            if (_mapCache.TryGetValue(landblockKey, out var cached))
                return cached;

            var polys = LoadDungeonMapPolygons(landblockKey);

            if (_mapCache.Count >= MAX_WALL_CACHE)
            {
                foreach (var key in _mapCache.Keys) { _mapCache.Remove(key); break; }
            }
            _mapCache[landblockKey] = polys;
            return polys;
        }

        /// <summary>
        /// Returns one MapCell per EnvCell in the landblock.
        /// Each cell centre is at (PosX + globalOffsetX, PosY + globalOffsetY).
        /// Drawing a 10×10-unit filled square at each centre produces gap-free floor coverage.
        /// </summary>
        public List<MapCell> GetDungeonMapCells(uint landblockKey)
        {
            if (_mapCellCache.TryGetValue(landblockKey, out var cached)) return cached;

            var cells = LoadDungeonMapCells(landblockKey);

            if (_mapCellCache.Count >= MAX_WALL_CACHE)
            {
                foreach (var key in _mapCellCache.Keys) { _mapCellCache.Remove(key); break; }
            }
            _mapCellCache[landblockKey] = cells;
            return cells;
        }

        private List<MapCell> LoadDungeonMapCells(uint landblockKey)
        {
            var result = new List<MapCell>();
            if (_cellDat == null) return result;

            float gx = ((landblockKey >> 8) & 0xFF) * 192.0f;
            float gy =  (landblockKey        & 0xFF) * 192.0f;

            var cellIds = _cellDat.GetLandblockCellIds(landblockKey);
            foreach (uint cellId in cellIds)
            {
                byte[] cellData = _cellDat.GetFileData(cellId);
                if (cellData == null || cellData.Length < 20) continue;
                try
                {
                    var envCell = ParseEnvCellHeader(cellData);
                    if (envCell == null) continue;
                    result.Add(new MapCell
                    {
                        WorldX        = envCell.PosX + gx,
                        WorldY        = envCell.PosY + gy,
                        CellZ         = envCell.PosZ,
                        EnvironmentId = envCell.EnvironmentId,
                        Rotation      = 2f * MathF.Atan2(envCell.RotZ, envCell.RotW)
                    });
                }
                catch { }
            }
            return result;
        }

        private List<MapPolygon> LoadDungeonMapPolygons(uint landblockKey)
        {
            var result = new List<MapPolygon>();
            if (_cellDat == null || _portalDat == null) return result;

            float globalOffsetX = ((landblockKey >> 8) & 0xFF) * 192.0f;
            float globalOffsetY = (landblockKey & 0xFF) * 192.0f;

            var cellIds = _cellDat.GetLandblockCellIds(landblockKey);
            foreach (uint cellId in cellIds)
            {
                byte[] cellData = _cellDat.GetFileData(cellId);
                if (cellData == null || cellData.Length < 20) continue;

                try
                {
                    var envCell = ParseEnvCellHeader(cellData);
                    if (envCell == null) continue;

                    // Use render geometry so IsPortal tags are preserved (physics upgrade loses them)
                    var cellGeo = GetCellRenderGeometry(envCell.EnvironmentId, envCell.CellStructureIndex);
                    if (cellGeo == null || cellGeo.Polygons.Count == 0) continue;

                    foreach (var poly in cellGeo.Polygons)
                    {
                        if (poly.Vertices.Count < 3) continue;

                        var worldVerts = new Vector3[poly.Vertices.Count];
                        for (int i = 0; i < poly.Vertices.Count; i++)
                            worldVerts[i] = TransformVertex(poly.Vertices[i], envCell, globalOffsetX, globalOffsetY);

                        result.Add(new MapPolygon
                        {
                            Vertices  = worldVerts,
                            CellX     = envCell.PosX + globalOffsetX,
                            CellY     = envCell.PosY + globalOffsetY,
                            CellZ     = envCell.PosZ,
                            IsPortal  = poly.IsPortal
                        });
                    }
                }
                catch { }
            }

            return result;
        }

        private List<BoundingVolume> LoadDungeonWalls(uint landblockKey)
        {
            var volumes = new List<BoundingVolume>();
            if (_cellDat == null || _portalDat == null) return volumes;

            float globalOffsetX = ((landblockKey >> 8) & 0xFF) * 192.0f;
            float globalOffsetY = (landblockKey & 0xFF) * 192.0f;

            // Use dat index to find ALL cells — no sequential scan with gap cutoff
            var cellIds = _cellDat.GetLandblockCellIds(landblockKey);
            int totalTriangles = 0;

            foreach (uint cellId in cellIds)
            {
                byte[] cellData = _cellDat.GetFileData(cellId);
                if (cellData == null || cellData.Length < 20) continue;

                try
                {
                    var envCell = ParseEnvCellHeader(cellData);
                    if (envCell == null) continue;

                    var cellGeo = GetCellGeometry(envCell.EnvironmentId, envCell.CellStructureIndex);
                    if (cellGeo == null || cellGeo.Polygons.Count == 0) continue;

                    // Collect all physics triangles for this cell
                    var cellTriangles = new List<Vector3>();
                    var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                    foreach (var poly in cellGeo.Polygons)
                    {
                        if (poly.IsPortal) continue; // Portals are openings — not solid walls
                        if (poly.Vertices.Count < 3) continue;

                        // Transform vertices to world space
                        var worldVerts = new List<Vector3>();
                        foreach (var localVert in poly.Vertices)
                            worldVerts.Add(TransformVertex(localVert, envCell, globalOffsetX, globalOffsetY));

                        // Triangulate polygon (fan from vertex 0) for exact ray-triangle intersection
                        for (int i = 1; i < worldVerts.Count - 1; i++)
                        {
                            cellTriangles.Add(worldVerts[0]);
                            cellTriangles.Add(worldVerts[i]);
                            cellTriangles.Add(worldVerts[i + 1]);
                        }

                        // Update cell AABB for pre-filter
                        foreach (var wv in worldVerts)
                        {
                            min = Vector3.Min(min, wv);
                            max = Vector3.Max(max, wv);
                        }
                    }

                    if (cellTriangles.Count >= 3)
                    {
                        totalTriangles += cellTriangles.Count / 3;
                        volumes.Add(new BoundingVolume
                        {
                            Type = BoundingVolume.VolumeType.TriangleMesh,
                            Center = (min + max) * 0.5f,
                            Dimensions = max - min,
                            Min = min,
                            Max = max,
                            MeshTriangles = cellTriangles.ToArray(),
                            IsDoor = false,
                            NoAabbFallback = true // Only count actual triangle hits — doorways are openings
                        });
                    }
                }
                catch { }
            }

            if (volumes.Count > 0)
                Log($"Landblock 0x{landblockKey:X4}: {cellIds.Count} cells, {volumes.Count} collision volumes, {totalTriangles} physics triangles");

            return volumes;
        }

        private Vector3 TransformVertex(Vector3 local, EnvCellHeader cell, float gx, float gy)
        {
            Vector3 rotated = RotateByQuat(local, cell.RotW, cell.RotX, cell.RotY, cell.RotZ);
            return new Vector3(
                rotated.X + cell.PosX + gx,
                rotated.Y + cell.PosY + gy,
                rotated.Z + cell.PosZ
            );
        }

        private Vector3 RotateByQuat(Vector3 v, float qw, float qx, float qy, float qz)
        {
            float cx = qy * v.Z - qz * v.Y;
            float cy = qz * v.X - qx * v.Z;
            float cz = qx * v.Y - qy * v.X;
            float cx2 = qy * cz - qz * cy;
            float cy2 = qz * cx - qx * cz;
            float cz2 = qx * cy - qy * cx;
            return new Vector3(
                v.X + 2f * (qw * cx + cx2),
                v.Y + 2f * (qw * cy + cy2),
                v.Z + 2f * (qw * cz + cz2)
            );
        }

        // ===================================================================
        // EnvCell header parsing
        // ===================================================================

        private class EnvCellHeader
        {
            public uint EnvironmentId;
            public uint CellStructureIndex;
            public float PosX, PosY, PosZ;
            public float RotW, RotX, RotY, RotZ;
        }

        private EnvCellHeader ParseEnvCellHeader(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    uint id = reader.ReadUInt32();
                    uint flags = reader.ReadUInt32();
                    reader.ReadUInt32(); // duplicate CellId

                    byte numSurfaces = reader.ReadByte();
                    byte numPortals = reader.ReadByte();
                    ushort numVisibleCells = reader.ReadUInt16();

                    ms.Seek(numSurfaces * 2, SeekOrigin.Current);

                    ushort envIdShort = reader.ReadUInt16();
                    ushort cellStructure = reader.ReadUInt16();

                    return new EnvCellHeader
                    {
                        EnvironmentId = 0x0D000000u | envIdShort,
                        CellStructureIndex = cellStructure,
                        PosX = reader.ReadSingle(),
                        PosY = reader.ReadSingle(),
                        PosZ = reader.ReadSingle(),
                        RotW = reader.ReadSingle(),
                        RotX = reader.ReadSingle(),
                        RotY = reader.ReadSingle(),
                        RotZ = reader.ReadSingle()
                    };
                }
            }
            catch { return null; }
        }

        // ===================================================================
        // Environment + CellStruct parsing
        // ===================================================================

        private class CellGeometry
        {
            public List<CellPolygon> Polygons = new List<CellPolygon>();
        }

        private class CellPolygon
        {
            public List<Vector3> Vertices = new List<Vector3>();
            public bool IsPortal;
        }

        private CellGeometry GetCellGeometry(uint environmentId, uint cellStructIndex)
        {
            if (_envCache.TryGetValue(environmentId, out var envCells))
            {
                if (envCells.TryGetValue(cellStructIndex, out var cached))
                    return cached;
            }

            byte[] envData = _portalDat.GetFileData(environmentId);
            if (envData == null || envData.Length < 8) return null;

            if (envCells == null)
            {
                envCells = ParseEnvironment(envData);
                if (_envCache.Count >= MAX_ENV_CACHE)
                {
                    foreach (var key in _envCache.Keys) { _envCache.Remove(key); break; }
                }
                _envCache[environmentId] = envCells;
            }

            envCells.TryGetValue(cellStructIndex, out var result);
            return result;
        }

        /// <summary>
        /// Returns render-polygon geometry with IsPortal tags preserved.
        /// Skips the physics-polygon upgrade so portal openings appear as tagged
        /// line geometry instead of literal holes in the mesh.
        /// </summary>
        private CellGeometry GetCellRenderGeometry(uint environmentId, uint cellStructIndex)
        {
            if (_renderEnvCache.TryGetValue(environmentId, out var envCells))
            {
                if (envCells.TryGetValue(cellStructIndex, out var cached))
                    return cached;
            }

            byte[] envData = _portalDat.GetFileData(environmentId);
            if (envData == null || envData.Length < 8) return null;

            if (envCells == null)
            {
                envCells = ParseEnvironment(envData, renderOnly: true);
                if (_renderEnvCache.Count >= MAX_ENV_CACHE)
                {
                    foreach (var key in _renderEnvCache.Keys) { _renderEnvCache.Remove(key); break; }
                }
                _renderEnvCache[environmentId] = envCells;
            }

            envCells.TryGetValue(cellStructIndex, out var result);
            return result;
        }

        private Dictionary<uint, CellGeometry> ParseEnvironment(byte[] data, bool renderOnly = false)
        {
            var result = new Dictionary<uint, CellGeometry>();

            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    uint id = reader.ReadUInt32();

                    // Dictionary<uint, CellStruct>
                    uint numCells = reader.ReadUInt32();
                    if (numCells > 200) return result;

                    for (uint i = 0; i < numCells; i++)
                    {
                        if (ms.Position + 4 > ms.Length) break;
                        uint key = reader.ReadUInt32();

                        var geo = ParseCellStruct(reader, ms, renderOnly);
                        if (geo != null)
                            result[key] = geo;
                        else
                        {
                            Log($"Environment 0x{id:X8}: failed parsing CellStruct {i} (key={key}), stopping");
                            break; // Stream position unknown — can't continue
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Environment parse error: {ex.Message}");
            }

            if (result.Count > 0)
                Log($"Environment: parsed {result.Count} CellStructs successfully");

            return result;
        }

        /// <summary>
        /// Parses CellStruct with hybrid approach:
        ///   1. Parse rendering polygons first (guaranteed to succeed, used as fallback)
        ///   2. Attempt Cell BSP skip to reach physics polygons
        ///   3. If reachable, replace rendering data with physics data (more accurate)
        ///   4. If BSP skip fails, keep rendering polygon data (portal-filtered)
        ///
        /// This ensures every cell has collision geometry even when BSP parsing fails,
        /// while using the precise physics polygons when available.
        ///
        /// CellStruct layout:
        ///   Header (3 × uint32) → VertexArray → RenderPolygons → PortalIndices → [align]
        ///   → CellBSP → PhysicsPolygons → PhysicsBSP → [DrawingBSP] → [align]
        /// </summary>
        private CellGeometry ParseCellStruct(BinaryReader reader, MemoryStream ms, bool renderOnly = false)
        {
            var geo = new CellGeometry();

            try
            {
                uint numPolygons = reader.ReadUInt32();
                uint numPhysicsPolygons = reader.ReadUInt32();
                uint numPortals = reader.ReadUInt32();

                if (numPolygons > 5000 || numPhysicsPolygons > 5000) return null;

                // Parse shared vertex array (used by both rendering and physics polygons)
                var vertices = ParseVertexArray(reader, ms);
                if (vertices == null) return null;

                // Parse rendering polygons (always — serves as fallback if BSP skip fails)
                var renderPolygons = ParsePolygons(reader, ms, numPolygons);
                if (renderPolygons == null) return null;

                // Read portal polygon indices (for filtering rendering polys)
                var portalIndices = new HashSet<ushort>();
                for (uint p = 0; p < numPortals && ms.Position + 2 <= ms.Length; p++)
                    portalIndices.Add(reader.ReadUInt16());

                // Align to 4-byte boundary
                long aligned = (ms.Position + 3) & ~3L;
                if (aligned <= ms.Length) ms.Position = aligned;

                // Build geometry from rendering polygons; tag portals rather than skipping them
                // so the map renderer can draw doorways as openings.
                // The raycasting path still excludes portals via IsDoor on BoundingVolume.
                for (int i = 0; i < renderPolygons.Count; i++)
                {
                    var poly = renderPolygons[i];
                    if (poly.VertexIds == null || poly.VertexIds.Count < 3) continue;

                    var cellPoly = new CellPolygon { IsPortal = portalIndices.Contains(poly.Key) };
                    foreach (var vid in poly.VertexIds)
                    {
                        if (vertices.ContainsKey(vid))
                            cellPoly.Vertices.Add(vertices[vid]);
                    }
                    if (cellPoly.Vertices.Count >= 3)
                        geo.Polygons.Add(cellPoly);
                }

                // For map rendering, render polygons with portal tags are all we need.
                if (renderOnly)
                    return geo;

                // Now attempt to skip Cell BSP and reach physics polygons
                bool canContinue = SkipBSPTree(reader, ms, BSPTreeType.Cell);

                // If BSP skip succeeded and physics polygons exist, upgrade to physics data
                if (canContinue && numPhysicsPolygons > 0)
                {
                    var physicsPolys = ParsePolygons(reader, ms, numPhysicsPolygons);
                    if (physicsPolys != null && physicsPolys.Count > 0)
                    {
                        // Replace rendering fallback with precise physics collision surfaces
                        geo.Polygons.Clear();
                        foreach (var poly in physicsPolys)
                        {
                            if (poly.VertexIds == null || poly.VertexIds.Count < 3) continue;
                            var cellPoly = new CellPolygon();
                            foreach (var vid in poly.VertexIds)
                            {
                                if (vertices.ContainsKey(vid))
                                    cellPoly.Vertices.Add(vertices[vid]);
                            }
                            if (cellPoly.Vertices.Count >= 3)
                                geo.Polygons.Add(cellPoly);
                        }
                    }
                }

                // Skip PhysicsBSP
                if (canContinue && !SkipBSPTree(reader, ms, BSPTreeType.Physics))
                    canContinue = false;

                // Skip optional DrawingBSP
                if (canContinue && ms.Position + 4 <= ms.Length)
                {
                    uint hasDrawingBSP = reader.ReadUInt32();
                    if (hasDrawingBSP != 0)
                    {
                        if (!SkipBSPTree(reader, ms, BSPTreeType.Drawing))
                            canContinue = false;
                    }
                }

                // Final alignment
                if (canContinue)
                {
                    aligned = (ms.Position + 3) & ~3L;
                    if (aligned <= ms.Length) ms.Position = aligned;
                }

                return geo;
            }
            catch
            {
                return geo.Polygons.Count > 0 ? geo : null;
            }
        }

        // ===================================================================
        // BSP Tree Skipping — matches ACE BSPNode/BSPLeaf exactly
        // ===================================================================

        private enum BSPTreeType { Cell, Physics, Drawing }

        // ACE's uint32 constants for BSP node types (read as LE uint32, stored reversed in file)
        private const uint BSP_PORT = 0x504F5254;
        private const uint BSP_LEAF = 0x4C454146;
        private const uint BSP_BPnn = 0x42506E6E;
        private const uint BSP_BPIn = 0x4250496E;
        private const uint BSP_BpIN = 0x4270494E;
        private const uint BSP_BpnN = 0x42706E4E;
        private const uint BSP_BPIN = 0x4250494E;
        private const uint BSP_BPnN = 0x42506E4E;

        private bool SkipBSPTree(BinaryReader reader, MemoryStream ms, BSPTreeType treeType)
        {
            return SkipBSPNode(reader, ms, treeType);
        }

        private bool SkipBSPNode(BinaryReader reader, MemoryStream ms, BSPTreeType treeType)
        {
            if (ms.Position + 4 > ms.Length) return false;
            uint typeTag = reader.ReadUInt32();

            if (typeTag == BSP_LEAF)
            {
                return SkipBSPLeaf(reader, ms, treeType);
            }

            if (typeTag == BSP_PORT)
            {
                // Portal node — same as leaf for our purposes
                return SkipBSPLeaf(reader, ms, treeType);
            }

            // Internal node: read splitting plane (float3 normal + float dist = 16 bytes)
            if (ms.Position + 16 > ms.Length) return false;
            ms.Seek(16, SeekOrigin.Current);

            // Read children based on type — matches ACE's switch exactly
            switch (typeTag)
            {
                case BSP_BPnn:
                case BSP_BPIn:
                    // Pos child only
                    if (!SkipBSPNode(reader, ms, treeType)) return false;
                    break;
                case BSP_BpIN:
                case BSP_BpnN:
                    // Neg child only
                    if (!SkipBSPNode(reader, ms, treeType)) return false;
                    break;
                case BSP_BPIN:
                case BSP_BPnN:
                    // Both children: Pos then Neg
                    if (!SkipBSPNode(reader, ms, treeType)) return false;
                    if (!SkipBSPNode(reader, ms, treeType)) return false;
                    break;
                default:
                    return false; // Unknown type
            }

            // Cell BSP internal nodes have NO sphere — done
            if (treeType == BSPTreeType.Cell)
                return true;

            // Physics and Drawing: read Sphere (float3 center + float radius = 16 bytes)
            if (ms.Position + 16 > ms.Length) return false;
            ms.Seek(16, SeekOrigin.Current);

            // Physics: done after sphere
            if (treeType == BSPTreeType.Physics)
                return true;

            // Drawing: also read InPolys (uint32 count + ushort[count])
            if (ms.Position + 4 > ms.Length) return false;
            uint numPolys = reader.ReadUInt32();
            if (numPolys > 100000) return false;
            long skip = numPolys * 2L; // ushort per poly
            if (ms.Position + skip > ms.Length) return false;
            ms.Seek(skip, SeekOrigin.Current);

            return true;
        }

        /// <summary>
        /// BSPLeaf: uint32 type + int32 leafIndex + (Physics only: int32 solid + Sphere(16) + uint32 numPolys + ushort[numPolys])
        /// Note: Physics leaf ALWAYS reads Sphere even when solid=0 (per ACE source).
        /// Cell and Drawing leaves are just type + leafIndex.
        /// </summary>
        private bool SkipBSPLeaf(BinaryReader reader, MemoryStream ms, BSPTreeType treeType)
        {
            // Type tag already read by caller. Read leafIndex.
            if (ms.Position + 4 > ms.Length) return false;
            reader.ReadInt32(); // leafIndex

            if (treeType == BSPTreeType.Physics)
            {
                // int32 Solid
                if (ms.Position + 4 > ms.Length) return false;
                reader.ReadInt32();

                // Sphere: ALWAYS read (16 bytes), even when solid=0
                if (ms.Position + 16 > ms.Length) return false;
                ms.Seek(16, SeekOrigin.Current);

                // uint32 numPolys + ushort[numPolys]
                if (ms.Position + 4 > ms.Length) return false;
                uint numPolys = reader.ReadUInt32();
                if (numPolys > 100000) return false;
                long skip = numPolys * 2L;
                if (ms.Position + skip > ms.Length) return false;
                ms.Seek(skip, SeekOrigin.Current);
            }
            // Cell and Drawing leaves: nothing more after leafIndex

            return true;
        }

        /// <summary>
        /// Skips a Dictionary{ushort, Polygon} block — same format as ParsePolygons
        /// but doesn't store results.
        /// </summary>
        private bool SkipPolygons(BinaryReader reader, MemoryStream ms, uint count)
        {
            for (uint i = 0; i < count; i++)
            {
                if (ms.Position + 12 > ms.Length) return false;
                ushort polyKey = reader.ReadUInt16();
                byte numPts = reader.ReadByte();
                byte stippling = reader.ReadByte();
                int sidesType = reader.ReadInt32();
                ms.Seek(4, SeekOrigin.Current); // posSurf + negSurf

                if (numPts == 0 || numPts > 50) return false;

                // Vertex IDs
                long vertBytes = numPts * 2L;
                if (ms.Position + vertBytes > ms.Length) return false;
                ms.Seek(vertBytes, SeekOrigin.Current);

                // PosUV
                if ((stippling & 0x04) == 0)
                {
                    if (ms.Position + numPts > ms.Length) return false;
                    ms.Seek(numPts, SeekOrigin.Current);
                }

                // NegUV
                if (sidesType == 2 && (stippling & 0x08) == 0)
                {
                    if (ms.Position + numPts > ms.Length) return false;
                    ms.Seek(numPts, SeekOrigin.Current);
                }
            }
            return true;
        }

        /// <summary>
        /// CVertexArray: int32 type(=1) + uint32 count + (ushort key + SWVertex)[count]
        /// SWVertex: ushort numUVs + float3 Origin(12) + float3 Normal(12) + numUVs × 8 bytes
        /// </summary>
        private Dictionary<ushort, Vector3> ParseVertexArray(BinaryReader reader, MemoryStream ms)
        {
            var verts = new Dictionary<ushort, Vector3>();

            int vertexType = reader.ReadInt32();
            if (vertexType != 1) return null;

            uint numVerts = reader.ReadUInt32();
            if (numVerts > 50000) return null;

            for (uint i = 0; i < numVerts; i++)
            {
                if (ms.Position + 28 > ms.Length) break; // key(2) + numUVs(2) + origin(12) + normal(12)
                ushort key = reader.ReadUInt16();
                ushort numUVs = reader.ReadUInt16();

                float ox = reader.ReadSingle();
                float oy = reader.ReadSingle();
                float oz = reader.ReadSingle();
                reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); // normal

                long uvBytes = numUVs * 8L;
                if (uvBytes > 0 && ms.Position + uvBytes <= ms.Length)
                    ms.Seek(uvBytes, SeekOrigin.Current);

                verts[key] = new Vector3(ox, oy, oz);
            }

            return verts.Count > 0 ? verts : null;
        }

        /// <summary>
        /// Dictionary{ushort, Polygon}: count × (ushort key + Polygon)
        /// Polygon: byte numPts, byte stippling, int32 sidesType, short posSurf, short negSurf,
        ///          short[numPts] vertexIds, optional UV index arrays
        /// </summary>
        private List<ParsedPolygon> ParsePolygons(BinaryReader reader, MemoryStream ms, uint count)
        {
            var polys = new List<ParsedPolygon>();

            for (uint i = 0; i < count; i++)
            {
                if (ms.Position + 12 > ms.Length) return polys; // key(2) + header(10 min)
                ushort polyKey = reader.ReadUInt16();

                byte numPts = reader.ReadByte();
                byte stippling = reader.ReadByte();
                int sidesType = reader.ReadInt32();
                short posSurf = reader.ReadInt16();
                short negSurf = reader.ReadInt16();

                if (numPts == 0 || numPts > 50) return polys;

                var poly = new ParsedPolygon { Key = polyKey, VertexIds = new List<ushort>() };

                for (int v = 0; v < numPts; v++)
                {
                    if (ms.Position + 2 > ms.Length) return polys;
                    poly.VertexIds.Add((ushort)reader.ReadInt16());
                }

                // Skip PosUVIndices (byte per point) unless NoPos flag (0x04)
                if ((stippling & 0x04) == 0)
                {
                    if (ms.Position + numPts > ms.Length) return polys;
                    ms.Seek(numPts, SeekOrigin.Current);
                }

                // Skip NegUVIndices if Clockwise(2) and not NoNeg(0x08)
                if (sidesType == 2 && (stippling & 0x08) == 0)
                {
                    if (ms.Position + numPts > ms.Length) return polys;
                    ms.Seek(numPts, SeekOrigin.Current);
                }

                polys.Add(poly);
            }

            return polys;
        }

        private class ParsedPolygon
        {
            public ushort Key;
            public List<ushort> VertexIds;
        }

        public void FlushCache()
        {
            _wallCache.Clear();
            _mapCache.Clear();
            _mapCellCache.Clear();
            _envCache.Clear();
            _renderEnvCache.Clear();
        }

        private static void Log(string msg)
        {
            System.Diagnostics.Debug.WriteLine($"[DungeonLOS] {msg}");
        }
    }
}

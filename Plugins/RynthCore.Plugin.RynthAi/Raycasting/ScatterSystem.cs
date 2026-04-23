using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// Implements AC's deterministic scatter placement system for terrain objects (trees, rocks, etc).
    /// Ported from ACE's Landblock.get_land_scenes() + ObjectDesc.Displace/Rotate/Scale.
    /// 
    /// Pipeline:
    ///   1. Parse RegionDesc (0x13000000) from portal.dat → terrain-to-scene mapping tables
    ///   2. Parse CellLandblock (0xXXYYFFFF) from cell.dat → 81 terrain values + 81 heights
    ///   3. For each terrain cell, deterministically select and place scatter objects
    ///   4. Look up bounding volumes for each placed object
    /// </summary>
    public class ScatterSystem
    {
        // LandDefs constants (from ACE)
        private const int VertexDim = 9;        // 9×9 vertices per landblock
        private const int BlockSide = 8;        // 8×8 cells per landblock
        private const float CellLength = 24.0f; // Each cell is 24m
        private const float BlockLength = 192.0f;

        // RegionDesc data (parsed once)
        private List<ScatterTerrainType> _terrainTypes;
        private List<ScatterSceneType> _sceneTypes;
        private float[] _landHeightTable;
        private bool _initialized;

        // AC's walkable surface threshold — normal.Z must be >= this value.
        // ACE server uses 0.664 (cos 48°), but the coarse 9×9 terrain mesh over-marks gentle
        // slopes, so we use 0.5 (cos 60°) to reduce false positives in the overlay.
        public const float FloorZ = 0.5f;

        // Scene cache (portal.dat scenes)
        private Dictionary<uint, List<ScatterObjectDesc>> _sceneCache = new Dictionary<uint, List<ScatterObjectDesc>>();

        // Landblock cache: landblockKey → parsed CellLandblock (terrain words, height indices, decoded Zs).
        // Replaces the old raw-byte[] height cache; LoadHeightGrid now delegates here.
        private readonly Dictionary<uint, LandblockData> _landblockCache = new Dictionary<uint, LandblockData>();
        private const int MAX_HEIGHT_GRID_CACHE = 30;

        // Reference to dat files
        private DatDatabase _portalDat;
        private DatDatabase _cellDat;

        public bool IsInitialized => _initialized;
        public List<string> DiagLog { get; } = new List<string>();

        public bool Initialize(DatDatabase portalDat, DatDatabase cellDat)
        {
            _portalDat = portalDat;
            _cellDat = cellDat;

            try
            {
                // Parse RegionDesc (0x13000000) from portal.dat
                byte[] regionData = portalDat.GetFileData(0x13000000);
                if (regionData == null || regionData.Length < 100)
                {
                    Log($"RegionDesc 0x13000000 not found in portal.dat (data={regionData?.Length ?? 0})");
                    return false;
                }

                Log($"RegionDesc: {regionData.Length} bytes loaded");
                
                // Dump first few uint32s for debugging
                string header = "RegionDesc header: ";
                for (int i = 0; i < Math.Min(32, regionData.Length); i += 4)
                    header += $"0x{BitConverter.ToUInt32(regionData, i):X8} ";
                Log(header);

                if (!ParseRegionDesc(regionData))
                {
                    Log("Failed to parse RegionDesc");
                    return false;
                }

                _initialized = true;
                Log($"Scatter system ready: {_terrainTypes?.Count ?? 0} terrain types, {_sceneTypes?.Count ?? 0} scene types");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Scatter init error: {ex.Message}");
                return false;
            }
        }

        // ===================================================================
        // Terrain height and passability API
        // ===================================================================

        /// <summary>
        /// Returns the parsed CellLandblock (terrain + heights + decoded Zs) for a landblock, cached after first load.
        /// Returns null if the landblock data is unavailable or the height table wasn't loaded.
        /// </summary>
        public LandblockData LoadLandblockData(uint landblockKey)
        {
            if (_landHeightTable == null || _cellDat == null) return null;

            if (_landblockCache.TryGetValue(landblockKey, out var cached))
                return cached;

            var data = LandblockData.Load(_cellDat, landblockKey, _landHeightTable);
            if (data == null) return null;

            if (_landblockCache.Count >= MAX_HEIGHT_GRID_CACHE)
                _landblockCache.Clear();
            _landblockCache[landblockKey] = data;
            return data;
        }

        /// <summary>
        /// Returns the 9×9 height grid (81 bytes) for a landblock. Thin shim over <see cref="LoadLandblockData"/>
        /// kept for existing callers that still pass raw byte[] into the passability helpers.
        /// </summary>
        public byte[] LoadHeightGrid(uint landblockKey)
            => LoadLandblockData(landblockKey)?.HeightIndices;

        /// <summary>
        /// World-space Z of vertex (ix, iy) in the 9×9 grid (ix and iy are 0–8).
        /// </summary>
        public float GetVertexHeight(byte[] heights, int ix, int iy)
        {
            if (heights == null || _landHeightTable == null) return 0f;
            int idx = ix * VertexDim + iy;
            if ((uint)idx >= 81) return 0f;
            byte h = heights[idx];
            return h < _landHeightTable.Length ? _landHeightTable[h] : 0f;
        }

        /// <summary>
        /// Returns the minimum normal.Z of the two triangles making up terrain cell (cellX, cellY).
        /// Lower values = steeper slope. Values below FloorZ are impassable.
        /// cellX and cellY are 0–7 (8×8 grid).
        /// </summary>
        public float GetCellMinNormalZ(byte[] heights, int cellX, int cellY)
        {
            float h00 = GetVertexHeight(heights, cellX,     cellY);
            float h10 = GetVertexHeight(heights, cellX + 1, cellY);
            float h01 = GetVertexHeight(heights, cellX,     cellY + 1);
            float h11 = GetVertexHeight(heights, cellX + 1, cellY + 1);

            float nz = CellLength * CellLength; // 576 — constant Z component before normalization

            // Triangle 1: (0,0,h00), (24,0,h10), (24,24,h11)
            // normal = cross((24,0,h10-h00), (24,24,h11-h00)) = (-24*(h10-h00), 24*(h10-h11), 576)
            float n1x = -CellLength * (h10 - h00);
            float n1y =  CellLength * (h10 - h11);
            float z1  = nz / (float)Math.Sqrt(n1x * n1x + n1y * n1y + nz * nz);

            // Triangle 2: (0,0,h00), (24,24,h11), (0,24,h01)
            // normal = cross((24,24,h11-h00), (0,24,h01-h00)) = (24*(h01-h11), -24*(h01-h00), 576)
            float n2x =  CellLength * (h01 - h11);
            float n2y = -CellLength * (h01 - h00);
            float z2  = nz / (float)Math.Sqrt(n2x * n2x + n2y * n2y + nz * nz);

            return Math.Min(z1, z2);
        }

        /// <summary>
        /// Returns true if terrain cell (cellX, cellY) is walkable per AC's FloorZ threshold.
        /// cellX and cellY are 0–7.
        /// </summary>
        public bool IsCellPassable(byte[] heights, int cellX, int cellY)
            => GetCellMinNormalZ(heights, cellX, cellY) >= FloorZ;

        /// <summary>
        /// Returns whether each individual triangle in the cell is passable.
        /// Triangle 1: vertices (cx,cy),(cx+1,cy),(cx+1,cy+1) — SE triangle.
        /// Triangle 2: vertices (cx,cy),(cx+1,cy+1),(cx,cy+1) — NW triangle.
        /// </summary>
        public void GetTrianglePassability(byte[] heights, int cellX, int cellY,
            out bool tri1Passable, out bool tri2Passable)
        {
            float h00 = GetVertexHeight(heights, cellX,     cellY);
            float h10 = GetVertexHeight(heights, cellX + 1, cellY);
            float h01 = GetVertexHeight(heights, cellX,     cellY + 1);
            float h11 = GetVertexHeight(heights, cellX + 1, cellY + 1);

            float nz = CellLength * CellLength;

            float n1x = -CellLength * (h10 - h00);
            float n1y =  CellLength * (h10 - h11);
            float z1  = nz / (float)Math.Sqrt(n1x * n1x + n1y * n1y + nz * nz);

            float n2x =  CellLength * (h01 - h11);
            float n2y = -CellLength * (h01 - h00);
            float z2  = nz / (float)Math.Sqrt(n2x * n2x + n2y * n2y + nz * nz);

            tri1Passable = z1 >= FloorZ;
            tri2Passable = z2 >= FloorZ;
        }

        /// <summary>
        /// Generates scatter object bounding volumes for a landblock.
        /// Replicates ACE's Landblock.get_land_scenes() algorithm.
        /// </summary>
        public List<BoundingVolume> GetScatterVolumes(uint landblockKey, GeometryLoader geoLoader)
        {
            var volumes = new List<BoundingVolume>();
            if (!_initialized || _terrainTypes == null || _sceneTypes == null)
                return volumes;

            try
            {
                // Parsed CellLandblock (cached) — terrain words + height indices + decoded Zs.
                var lbData = LoadLandblockData(landblockKey);
                if (lbData == null) return volumes;

                ushort[] terrain = lbData.TerrainWords;
                byte[] height = lbData.HeightIndices;

                // Compute block offsets (global cell coordinates)
                uint blockX = ((landblockKey >> 8) & 0xFF) * 8;
                uint blockY = (landblockKey & 0xFF) * 8;

                float globalOffsetX = lbData.WorldOriginX;
                float globalOffsetY = lbData.WorldOriginY;

                int scatterCount = 0;

                // Iterate all 81 terrain cells (matching ACE's get_land_scenes)
                for (uint i = 0; i < 81; i++)
                {
                    ushort t = terrain[i];
                    int terrainType = (t >> 2) & 0x1F;      // 5 bits, 0-31
                    int sceneType = t >> 11;                  // 5 bits, 0-31

                    if (terrainType >= _terrainTypes.Count) continue;
                    var tt = _terrainTypes[terrainType];
                    if (sceneType >= tt.SceneTypeIndices.Count) continue;

                    int sceneInfoIdx = (int)tt.SceneTypeIndices[sceneType];
                    if (sceneInfoIdx >= _sceneTypes.Count) continue;

                    var scenes = _sceneTypes[sceneInfoIdx].SceneFileIds;
                    if (scenes.Count == 0) continue;

                    uint cellX = i / (uint)VertexDim;
                    uint cellY = i % (uint)VertexDim;

                    uint globalCellX = (uint)(cellX + blockX);
                    uint globalCellY = (uint)(cellY + blockY);

                    // Deterministic scene selection — uses uint32 overflow arithmetic
                    // matching AC client's C++ unsigned int behavior
                    uint cellMat = (uint)((long)globalCellY * (712977289L * globalCellX + 1813693831L) - 1109124029L * globalCellX + 2139937281L);
                    double offset = cellMat * 2.3283064e-10;
                    int sceneIdx = (int)(scenes.Count * offset);
                    if (sceneIdx < 0 || sceneIdx >= scenes.Count) sceneIdx = 0;

                    uint sceneId = scenes[sceneIdx];

                    // Load the Scene file
                    var sceneObjects = LoadScene(sceneId);
                    if (sceneObjects == null || sceneObjects.Count == 0) continue;

                    // Per-object noise seeds — uint32 overflow
                    uint cellXMat = (uint)(-1109124029L * globalCellX);
                    uint cellYMat = (uint)(1813693831L * globalCellY);
                    uint cellMat2 = (uint)(1360117743L * globalCellX * globalCellY + 1888038839L);

                    for (uint j = 0; j < sceneObjects.Count; j++)
                    {
                        var obj = sceneObjects[(int)j];

                        // Frequency check — uint32 overflow
                        double noise = (uint)(cellXMat + cellYMat - cellMat2 * (23399 + j)) * 2.3283064e-10;
                        if (noise >= obj.Freq || obj.WeenieObj != 0)
                            continue;

                        // Displace
                        var position = Displace(obj, globalCellX, globalCellY, j);

                        float lx = cellX * CellLength + position.X;
                        float ly = cellY * CellLength + position.Y;

                        // Bounds check
                        if (lx < 0 || ly < 0 || lx >= BlockLength || ly >= BlockLength)
                            continue;

                        // Get terrain height at this position
                        float z = GetTerrainHeight(height, lx, ly);

                        // World coordinates
                        float worldX = globalOffsetX + lx;
                        float worldY = globalOffsetY + ly;

                        // Get bounding volume for this object
                        var setupVolumes = geoLoader.GetSetupBoundingVolumesPublic(obj.ObjId);
                        foreach (var vol in setupVolumes)
                        {
                            // Create a frame for the scatter object
                            var frame = new Frame
                            {
                                OriginX = lx, OriginY = ly, OriginZ = z,
                                RotW = 1, RotX = 0, RotY = 0, RotZ = 0
                            };

                            // Apply rotation from scatter algorithm
                            ApplyScatterRotation(frame, obj, globalCellX, globalCellY, j);

                            var transformed = TransformScatterVolume(vol, frame, globalOffsetX, globalOffsetY);
                            if (transformed != null)
                            {
                                // Extend scatter volumes vertically to account for:
                                // 1. Terrain height imprecision (nearest-vertex vs polygon, can be 10-15m off)
                                // 2. Trees/rocks extend upward from ground level
                                // Use generous range to ensure ray intersection works
                                float extendBelow = 20.0f;
                                float extendAbove = 20.0f;
                                transformed.Min = new Vector3(transformed.Min.X, transformed.Min.Y, transformed.Min.Z - extendBelow);
                                transformed.Max = new Vector3(transformed.Max.X, transformed.Max.Y, transformed.Max.Z + extendAbove);
                                transformed.Center = new Vector3(transformed.Center.X, transformed.Center.Y, 
                                    (transformed.Min.Z + transformed.Max.Z) * 0.5f);
                                // Force AABB type so Min/Max Z extension is used by ray intersection
                                transformed.Type = BoundingVolume.VolumeType.AxisAlignedBox;
                                transformed.Dimensions = transformed.Max - transformed.Min;
                                
                                volumes.Add(transformed);
                            }
                        }
                        scatterCount++;
                    }
                }

                if (scatterCount > 0)
                    Log($"Landblock 0x{landblockKey:X4}: {scatterCount} scatter objects -> {volumes.Count} volumes");
            }
            catch (Exception ex)
            {
                Log($"Scatter error for 0x{landblockKey:X4}: {ex.Message}");
            }

            return volumes;
        }

        // ===================================================================
        // ObjectDesc.Displace — pseudo-random placement from ACE
        // ===================================================================

        private Vector3 Displace(ScatterObjectDesc obj, uint ix, uint iy, uint iq)
        {
            float x, y, z;
            float locX = obj.BaseLocX, locY = obj.BaseLocY, locZ = obj.BaseLocZ;

            // uint32 overflow arithmetic matching AC client C++ unsigned int
            if (obj.DisplaceX <= 0)
                x = locX;
            else
                x = (float)((uint)(1813693831L * iy - (iq + 45773) * (1360117743L * iy * ix + 1888038839L) - 1109124029L * ix)
                    * 2.3283064e-10 * obj.DisplaceX + locX);

            if (obj.DisplaceY <= 0)
                y = locY;
            else
                y = (float)((uint)(1813693831L * iy - (iq + 72719) * (1360117743L * iy * ix + 1888038839L) - 1109124029L * ix)
                    * 2.3283064e-10 * obj.DisplaceY + locY);

            z = locZ;

            double quadrant = (uint)(1813693831L * iy - ix * (1870387557L * iy + 1109124029L) - 402451965L) * 2.3283064e-10;

            if (quadrant >= 0.75) return new Vector3(y, -x, z);
            if (quadrant >= 0.5) return new Vector3(-x, -y, z);
            if (quadrant >= 0.25) return new Vector3(-y, x, z);
            return new Vector3(x, y, z);
        }

        private void ApplyScatterRotation(Frame frame, ScatterObjectDesc obj, uint x, uint y, uint k)
        {
            if (obj.MaxRotation > 0.0f)
            {
                float degrees = (float)((uint)(1813693831L * y - (k + 63127) * (1360117743L * y * x + 1888038839L) - 1109124029L * x) * 2.3283064e-10 * obj.MaxRotation);
                float radians = degrees * (float)(Math.PI / 180.0);
                float halfRad = radians * 0.5f;
                frame.RotW = (float)Math.Cos(halfRad);
                frame.RotZ = (float)Math.Sin(halfRad);
            }
        }

        private float GetTerrainHeight(byte[] heights, float lx, float ly)
        {
            // Bilinear interpolation of height values
            // LandHeightTable already contains actual in-game Z heights
            // (the *2 factor from CellLandblock comments refers to raw bytes, not table values)
            int ix = Math.Min((int)(lx / CellLength), BlockSide);
            int iy = Math.Min((int)(ly / CellLength), BlockSide);

            int idx = ix * VertexDim + iy;
            if (idx >= 0 && idx < 81 && _landHeightTable != null)
            {
                byte h = heights[idx];
                if (h < _landHeightTable.Length)
                    return _landHeightTable[h];
            }
            return 0;
        }

        private BoundingVolume TransformScatterVolume(BoundingVolume localVol, Frame placement,
                                                       float globalOffsetX, float globalOffsetY)
        {
            if (localVol == null) return null;

            try
            {
                Vector3 worldCenter = placement.TransformPoint(localVol.Center);
                worldCenter.X += globalOffsetX;
                worldCenter.Y += globalOffsetY;

                if (localVol.Type == BoundingVolume.VolumeType.Sphere)
                {
                    float r = localVol.Dimensions.X;
                    return new BoundingVolume
                    {
                        Type = BoundingVolume.VolumeType.Sphere,
                        Center = worldCenter,
                        Dimensions = localVol.Dimensions,
                        Min = worldCenter - new Vector3(r, r, r),
                        Max = worldCenter + new Vector3(r, r, r),
                        IsDoor = false
                    };
                }

                // For non-sphere: transform corners to get new AABB
                Vector3[] corners = {
                    new Vector3(localVol.Min.X, localVol.Min.Y, localVol.Min.Z),
                    new Vector3(localVol.Max.X, localVol.Min.Y, localVol.Min.Z),
                    new Vector3(localVol.Min.X, localVol.Max.Y, localVol.Min.Z),
                    new Vector3(localVol.Max.X, localVol.Max.Y, localVol.Min.Z),
                    new Vector3(localVol.Min.X, localVol.Min.Y, localVol.Max.Z),
                    new Vector3(localVol.Max.X, localVol.Min.Y, localVol.Max.Z),
                    new Vector3(localVol.Min.X, localVol.Max.Y, localVol.Max.Z),
                    new Vector3(localVol.Max.X, localVol.Max.Y, localVol.Max.Z),
                };

                Vector3 newMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 newMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                for (int c = 0; c < 8; c++)
                {
                    Vector3 w = placement.TransformPoint(corners[c]);
                    w.X += globalOffsetX;
                    w.Y += globalOffsetY;
                    newMin = Vector3.Min(newMin, w);
                    newMax = Vector3.Max(newMax, w);
                }

                return new BoundingVolume
                {
                    Type = BoundingVolume.VolumeType.AxisAlignedBox,
                    Center = (newMin + newMax) * 0.5f,
                    Dimensions = newMax - newMin,
                    Min = newMin,
                    Max = newMax,
                    IsDoor = false
                };
            }
            catch { return null; }
        }

        // ===================================================================
        // Scene loading
        // ===================================================================

        private List<ScatterObjectDesc> LoadScene(uint sceneId)
        {
            if (_sceneCache.TryGetValue(sceneId, out var cached))
                return cached;

            var objects = new List<ScatterObjectDesc>();

            try
            {
                byte[] data = _portalDat.GetFileData(sceneId);
                if (data == null || data.Length < 8)
                {
                    _sceneCache[sceneId] = objects;
                    return objects;
                }

                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    uint id = reader.ReadUInt32(); // Scene ID

                    // List<ObjectDesc>: uint32 count + count × ObjectDesc
                    uint numObjects = reader.ReadUInt32();
                    if (numObjects > 200) numObjects = 200;

                    for (uint i = 0; i < numObjects; i++)
                    {
                        if (ms.Position + 72 > ms.Length) break; // ObjectDesc is 72 bytes

                        var obj = new ScatterObjectDesc();
                        obj.ObjId = reader.ReadUInt32();

                        // Frame (BaseLoc): 7 floats
                        obj.BaseLocX = reader.ReadSingle();
                        obj.BaseLocY = reader.ReadSingle();
                        obj.BaseLocZ = reader.ReadSingle();
                        obj.BaseLocW = reader.ReadSingle();
                        obj.BaseLocRX = reader.ReadSingle();
                        obj.BaseLocRY = reader.ReadSingle();
                        obj.BaseLocRZ = reader.ReadSingle();

                        obj.Freq = reader.ReadSingle();
                        obj.DisplaceX = reader.ReadSingle();
                        obj.DisplaceY = reader.ReadSingle();
                        obj.MinScale = reader.ReadSingle();
                        obj.MaxScale = reader.ReadSingle();
                        obj.MaxRotation = reader.ReadSingle();
                        obj.MinSlope = reader.ReadSingle();
                        obj.MaxSlope = reader.ReadSingle();
                        obj.Align = reader.ReadUInt32();
                        obj.Orient = reader.ReadUInt32();
                        obj.WeenieObj = reader.ReadUInt32();

                        objects.Add(obj);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Scene 0x{sceneId:X8} parse error: {ex.Message}");
            }

            _sceneCache[sceneId] = objects;
            return objects;
        }

        // ===================================================================
        // RegionDesc parsing (0x13000000)
        // ===================================================================

        private bool ParseRegionDesc(byte[] data)
        {
            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    uint id = reader.ReadUInt32();           // 0x13000000
                    uint regionNumber = reader.ReadUInt32();
                    uint version = reader.ReadUInt32();

                    Log($"RegionDesc: id=0x{id:X8}, region={regionNumber}, version={version}");

                    // PString RegionName ("Dereth")
                    SkipPString(reader, ms);
                    Log($"After RegionName: pos={ms.Position}");

                    // LandDefs
                    if (!ParseLandDefs(reader, ms))
                    {
                        Log($"LandDefs parse failed at pos={ms.Position}");
                        return ScanForTables(data);
                    }
                    Log($"After LandDefs: pos={ms.Position}");

                    // GameTime
                    if (!SkipGameTime(reader, ms))
                    {
                        Log($"GameTime skip failed at pos={ms.Position}");
                        return ScanForTables(data);
                    }
                    Log($"After GameTime: pos={ms.Position}");

                    // PartsMask
                    uint partsMask = reader.ReadUInt32();
                    Log($"PartsMask=0x{partsMask:X8}, pos={ms.Position}");

                    // Skip SkyDesc if present
                    if ((partsMask & 0x10) != 0)
                    {
                        Log("Skipping SkyDesc...");
                        if (!SkipSkyDesc(reader, ms))
                        {
                            Log($"SkyDesc skip failed at pos={ms.Position}, trying fallback");
                            return ScanForTables(data);
                        }
                        Log($"After SkyDesc: pos={ms.Position}");
                    }

                    // Skip SoundDesc if present
                    if ((partsMask & 0x01) != 0)
                    {
                        Log("Skipping SoundDesc...");
                        if (!SkipSoundDesc(reader, ms))
                        {
                            Log($"SoundDesc skip failed at pos={ms.Position}, trying fallback");
                            return ScanForTables(data);
                        }
                        Log($"After SoundDesc: pos={ms.Position}");
                    }

                    // Parse SceneDesc
                    if ((partsMask & 0x02) != 0)
                    {
                        Log("Parsing SceneDesc...");
                        if (!ParseSceneDesc(reader, ms))
                        {
                            Log($"SceneDesc parse failed at pos={ms.Position}, trying fallback");
                            return ScanForTables(data);
                        }
                        Log($"After SceneDesc: pos={ms.Position}");
                    }

                    // Parse TerrainDesc
                    Log("Parsing TerrainDesc...");
                    if (!ParseTerrainDesc(reader, ms))
                    {
                        Log($"TerrainDesc parse failed at pos={ms.Position}, trying fallback");
                        return ScanForTables(data);
                    }

                    Log($"RegionDesc parsed: {_terrainTypes?.Count} terrainTypes, {_sceneTypes?.Count} sceneTypes");
                    return _terrainTypes != null && _sceneTypes != null;
                }
            }
            catch (Exception ex)
            {
                Log($"RegionDesc parse error at unknown pos: {ex.Message}");
                return ScanForTables(data);
            }
        }

        private bool ParseLandDefs(BinaryReader reader, MemoryStream ms)
        {
            if (ms.Position + 1056 > ms.Length) return false;

            reader.ReadInt32();  // NumBlockLength
            reader.ReadInt32();  // NumBlockWidth
            reader.ReadSingle(); // SquareLength
            reader.ReadInt32();  // LBlockLength
            reader.ReadInt32();  // VertexPerCell
            reader.ReadSingle(); // MaxObjHeight
            reader.ReadSingle(); // SkyHeight
            reader.ReadSingle(); // RoadWidth

            _landHeightTable = new float[256];
            for (int i = 0; i < 256; i++)
                _landHeightTable[i] = reader.ReadSingle();

            return true;
        }

        private bool SkipGameTime(BinaryReader reader, MemoryStream ms)
        {
            try
            {
                reader.ReadDouble();   // ZeroTimeOfYear
                reader.ReadUInt32();   // ZeroYear
                reader.ReadSingle();   // DayLength
                reader.ReadUInt32();   // DaysPerYear
                SkipPString(reader, ms); // YearSpec

                // List<TimeOfDay> — uint32 count + entries
                uint numTOD = reader.ReadUInt32();
                if (numTOD > 100) return false;
                for (uint i = 0; i < numTOD; i++)
                {
                    reader.ReadSingle(); // Begin time
                    reader.ReadUInt32(); // IsNight flag
                    SkipPString(reader, ms); // Name
                }

                // DaysOfTheWeek — uint32 count + PStrings
                uint numDays = reader.ReadUInt32();
                if (numDays > 20) return false;
                for (uint i = 0; i < numDays; i++)
                    SkipPString(reader, ms);

                // List<Season> — uint32 count + entries
                uint numSeasons = reader.ReadUInt32();
                if (numSeasons > 20) return false;
                for (uint i = 0; i < numSeasons; i++)
                {
                    reader.ReadUInt32(); // StartDate
                    SkipPString(reader, ms); // Name
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool SkipSkyDesc(BinaryReader reader, MemoryStream ms)
        {
            // SkyDesc is complex (sky objects, colors, etc). Skip by searching for next recognizable section.
            // Store position and try to skip it.
            try
            {
                // SkyDesc format: TickSize(double) + LightTickSize(double) + DayGroup(SkyObjectReplace) + NightGroup + ...
                // This is very complex. We'll use a heuristic: skip forward looking for SceneDesc signature.
                // For now, try to skip a known minimum size
                reader.ReadDouble(); // TickSize
                reader.ReadDouble(); // LightTickSize

                // DayGroup
                SkipSkyObjectGroup(reader, ms);
                // NightGroup
                SkipSkyObjectGroup(reader, ms);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void SkipSkyObjectGroup(BinaryReader reader, MemoryStream ms)
        {
            // SkyObjectReplace: uint32 count + entries
            uint count = reader.ReadUInt32();
            for (uint i = 0; i < count && ms.Position < ms.Length; i++)
            {
                reader.ReadUInt32(); // object index
                reader.ReadUInt32(); // GfxObj ID
                reader.ReadSingle(); // rotate
                reader.ReadSingle(); // transparent
                reader.ReadSingle(); // luminosity
                reader.ReadSingle(); // max bright
            }
        }

        private bool SkipSoundDesc(BinaryReader reader, MemoryStream ms)
        {
            try
            {
                // SoundDesc: List<AmbientSoundDesc>
                uint count = reader.ReadUInt32();
                if (count > 200) return false;
                for (uint i = 0; i < count && ms.Position < ms.Length; i++)
                {
                    reader.ReadUInt32(); // SType
                    reader.ReadSingle(); // Volume
                    reader.ReadSingle(); // BaseChance
                    reader.ReadSingle(); // MinRate
                    reader.ReadSingle(); // MaxRate
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ParseSceneDesc(BinaryReader reader, MemoryStream ms)
        {
            try
            {
                // SceneDesc: List<SceneType>
                uint numSceneTypes = reader.ReadUInt32();
                if (numSceneTypes > 500) return false;

                _sceneTypes = new List<ScatterSceneType>();

                for (uint i = 0; i < numSceneTypes; i++)
                {
                    var st = new ScatterSceneType();
                    st.StbIndex = reader.ReadUInt32();

                    // List<uint> Scenes
                    uint numScenes = reader.ReadUInt32();
                    if (numScenes > 500) return false;

                    st.SceneFileIds = new List<uint>();
                    for (uint s = 0; s < numScenes; s++)
                        st.SceneFileIds.Add(reader.ReadUInt32());

                    _sceneTypes.Add(st);
                }

                Log($"SceneDesc: {_sceneTypes.Count} scene types parsed");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ParseTerrainDesc(BinaryReader reader, MemoryStream ms)
        {
            try
            {
                // TerrainDesc: List<TerrainType> + LandSurf
                uint numTerrainTypes = reader.ReadUInt32();
                if (numTerrainTypes > 100) return false;

                _terrainTypes = new List<ScatterTerrainType>();

                for (uint i = 0; i < numTerrainTypes; i++)
                {
                    var tt = new ScatterTerrainType();

                    // PString TerrainName
                    SkipPString(reader, ms);

                    // uint32 TerrainColor
                    reader.ReadUInt32();

                    // List<uint> SceneTypes
                    uint numSceneTypes = reader.ReadUInt32();
                    if (numSceneTypes > 200) return false;

                    tt.SceneTypeIndices = new List<uint>();
                    for (uint s = 0; s < numSceneTypes; s++)
                        tt.SceneTypeIndices.Add(reader.ReadUInt32());

                    _terrainTypes.Add(tt);
                }

                Log($"TerrainDesc: {_terrainTypes.Count} terrain types parsed");
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ===================================================================
        // Fallback: scan RegionDesc data for recognizable table patterns
        // ===================================================================

        private bool ScanForTables(byte[] data)
        {
            Log("Attempting fallback scan for scatter tables...");

            // Strategy: Scan for SceneDesc (uint32 count followed by entries with 0x12xxxxxx scene IDs)
            // The real AC SceneDesc has ~89 entries. We require at least 20 to avoid false positives.
            // TerrainDesc (~32 entries with PString names) follows right after SceneDesc.

            for (int pos = 100; pos < data.Length - 100; pos += 4)
            {
                uint count = BitConverter.ToUInt32(data, pos);
                if (count < 20 || count > 200) continue;

                // Validate: try to read all entries as SceneType { uint32 StbIndex, uint32 numScenes, uint32[] sceneIds }
                bool valid = true;
                int scanPos = pos + 4;
                int totalSceneIds = 0;

                for (int i = 0; i < count && valid; i++)
                {
                    if (scanPos + 8 > data.Length) { valid = false; break; }

                    uint stbIndex = BitConverter.ToUInt32(data, scanPos);
                    scanPos += 4;

                    uint numScenes = BitConverter.ToUInt32(data, scanPos);
                    scanPos += 4;

                    if (numScenes > 200) { valid = false; break; }

                    for (int s = 0; s < numScenes && valid; s++)
                    {
                        if (scanPos + 4 > data.Length) { valid = false; break; }
                        uint sceneId = BitConverter.ToUInt32(data, scanPos);
                        scanPos += 4;

                        // Scene files are 0x12xxxxxx
                        if ((sceneId & 0xFF000000) != 0x12000000) { valid = false; break; }
                        totalSceneIds++;
                    }
                }

                if (!valid || totalSceneIds < 10) continue;

                // We found a candidate SceneDesc! Parse it fully.
                _sceneTypes = new List<ScatterSceneType>();
                int fullPos = pos + 4;

                for (int i = 0; i < count; i++)
                {
                    if (fullPos + 8 > data.Length) break;
                    uint stbIdx = BitConverter.ToUInt32(data, fullPos);
                    fullPos += 4;
                    uint nScenes = BitConverter.ToUInt32(data, fullPos);
                    fullPos += 4;

                    var st = new ScatterSceneType { StbIndex = stbIdx, SceneFileIds = new List<uint>() };
                    for (int s = 0; s < nScenes; s++)
                    {
                        if (fullPos + 4 > data.Length) break;
                        st.SceneFileIds.Add(BitConverter.ToUInt32(data, fullPos));
                        fullPos += 4;
                    }
                    _sceneTypes.Add(st);
                }

                Log($"Fallback found SceneDesc at offset {pos}: {_sceneTypes.Count} types, {totalSceneIds} total scene IDs");

                // Now parse TerrainDesc starting right after SceneDesc
                if (fullPos + 4 < data.Length)
                {
                    if (ParseTerrainDescFromOffset(data, fullPos))
                    {
                        Log($"Fallback TerrainDesc succeeded: {_terrainTypes.Count} terrain types");
                        return true;
                    }
                    else
                    {
                        Log($"TerrainDesc failed at offset {fullPos}, continuing scan...");
                        _sceneTypes = null;
                        continue; // Try finding another SceneDesc
                    }
                }
            }

            Log("Fallback scan failed to find scatter tables");
            return false;
        }

        private bool ParseTerrainDescFromOffset(byte[] data, int startPos)
        {
            try
            {
                _terrainTypes = new List<ScatterTerrainType>();
                int pos = startPos;

                if (pos + 4 > data.Length) { _terrainTypes = null; return false; }
                uint count = BitConverter.ToUInt32(data, pos);
                pos += 4;

                if (count < 5 || count > 100) { _terrainTypes = null; return false; }

                for (int i = 0; i < count; i++)
                {
                    var tt = new ScatterTerrainType();

                    // PString: ushort length + chars + align to 4
                    if (pos + 2 > data.Length) { _terrainTypes = null; return false; }
                    ushort strLen = BitConverter.ToUInt16(data, pos);
                    pos += 2;

                    if (strLen > 200 || pos + strLen > data.Length) { _terrainTypes = null; return false; }

                    // Verify it looks like a text string (ASCII printable)
                    if (i < 3 && strLen > 0)
                    {
                        bool isPrintable = true;
                        for (int c = 0; c < Math.Min((int)strLen, 10); c++)
                        {
                            byte b = data[pos + c];
                            if (b < 32 || b > 126) { isPrintable = false; break; }
                        }
                        if (!isPrintable) { _terrainTypes = null; return false; }

                        string name = System.Text.Encoding.ASCII.GetString(data, pos, strLen);
                        Log($"  TerrainType[{i}]: \"{name}\"");
                    }

                    pos += strLen;
                    pos = (pos + 3) & ~3; // Align to 4

                    // uint32 TerrainColor
                    if (pos + 4 > data.Length) { _terrainTypes = null; return false; }
                    pos += 4;

                    // List<uint> SceneTypes
                    if (pos + 4 > data.Length) { _terrainTypes = null; return false; }
                    uint numST = BitConverter.ToUInt32(data, pos);
                    pos += 4;

                    if (numST > 200) { _terrainTypes = null; return false; }

                    tt.SceneTypeIndices = new List<uint>();
                    for (int s = 0; s < numST; s++)
                    {
                        if (pos + 4 > data.Length) { _terrainTypes = null; return false; }
                        tt.SceneTypeIndices.Add(BitConverter.ToUInt32(data, pos));
                        pos += 4;
                    }

                    _terrainTypes.Add(tt);
                }

                return _terrainTypes.Count >= 5;
            }
            catch
            {
                _terrainTypes = null;
                return false;
            }
        }

        // ===================================================================
        // Helpers
        // ===================================================================

        private void SkipPString(BinaryReader reader, MemoryStream ms)
        {
            ushort len = reader.ReadUInt16();
            if (len > 0 && ms.Position + len <= ms.Length)
                ms.Seek(len, SeekOrigin.Current);
            // Align to 4 bytes
            long alignedPos = (ms.Position + 3) & ~3L;
            if (alignedPos <= ms.Length)
                ms.Position = alignedPos;
        }

        private void Log(string msg)
        {
            string line = $"[Scatter] {msg}";
            System.Diagnostics.Debug.WriteLine(line);
            if (DiagLog.Count < 100) DiagLog.Add(msg);
        }
    }

    // ===================================================================
    // Data structures for scatter system
    // ===================================================================

    public class ScatterTerrainType
    {
        public List<uint> SceneTypeIndices = new List<uint>();
    }

    public class ScatterSceneType
    {
        public uint StbIndex;
        public List<uint> SceneFileIds = new List<uint>();
    }

    public class ScatterObjectDesc
    {
        public uint ObjId;
        public float BaseLocX, BaseLocY, BaseLocZ;
        public float BaseLocW, BaseLocRX, BaseLocRY, BaseLocRZ;
        public float Freq;
        public float DisplaceX, DisplaceY;
        public float MinScale, MaxScale;
        public float MaxRotation;
        public float MinSlope, MaxSlope;
        public uint Align, Orient, WeenieObj;
    }
}

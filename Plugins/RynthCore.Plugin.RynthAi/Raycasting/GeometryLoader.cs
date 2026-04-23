using System;
using System.Collections.Generic;
using System.IO;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    /// <summary>
    /// Loads collision geometry from Asheron's Call's .dat files for raycasting.
    /// 
    /// Pipeline:
    ///   1. cell.dat   → LandblockInfo (0xXXYYFFFF) → list of static objects + placements
    ///   2. portal.dat → Setup (0x02xxxxxx)          → bounding sphere + collision volumes
    ///   3. portal.dat → GfxObj (0x01xxxxxx)         → vertex AABB (fallback if Setup has no bounds)
    ///   4. Transform collision volumes by placement frame → BoundingVolume list for raycasting
    /// </summary>
    public class GeometryLoader : IDisposable
    {
        private DatDatabase _portalDat;
        private DatDatabase _cellDat;
        private bool _initialized;

        /// <summary>
        /// Public accessor for the cell.dat database, used by DungeonLOS.
        /// </summary>
        public DatDatabase CellDat => _cellDat;

        /// <summary>
        /// Public accessor for the portal.dat database, used by DungeonLOS.
        /// </summary>
        public DatDatabase PortalDat => _portalDat;

        /// <summary>
        /// Cache for loaded landblock geometry.
        /// Key: Landblock ID (upper 16 bits, e.g., 0xXXYY), Value: BoundingVolumes.
        /// </summary>
        private readonly Dictionary<uint, List<BoundingVolume>> _landblockCache =
            new Dictionary<uint, List<BoundingVolume>>();

        /// <summary>
        /// Cache for parsed Setup files to avoid re-reading portal.dat.
        /// </summary>
        private readonly Dictionary<uint, SetupInfo> _setupCache =
            new Dictionary<uint, SetupInfo>();

        private const int MAX_LANDBLOCK_CACHE = 20;
        private const int MAX_SETUP_CACHE = 1000;

        /// <summary>
        /// Cache for parsed GfxObj mesh data (vertices + faces) for triangle-level raycasting.
        /// </summary>
        private readonly Dictionary<uint, GfxObjMeshData> _meshCache =
            new Dictionary<uint, GfxObjMeshData>();

        private const int MAX_MESH_CACHE = 500;
        private const int MAX_BUILDING_TRIANGLES = 10000;

        /// <summary>
        /// Scatter system for procedurally placed terrain objects (trees, rocks, etc.)
        /// </summary>
        private ScatterSystem _scatterSystem;

        /// <summary>
        /// Dungeon wall geometry extractor. Exposed for DungeonMapUI.
        /// </summary>
        private DungeonLOS _dungeonLOS;
        public DungeonLOS DungeonLOS => _dungeonLOS;

        // Statistics
        public int LandblocksCached => _landblockCache.Count;
        public int SetupsCached => _setupCache.Count;
        public bool IsInitialized => _initialized;
        public string StatusMessage { get; private set; } = "Not initialized";
        public List<string> DiagLog { get; } = new List<string>();

        /// <summary>
        /// Initializes the geometry loader by opening the .dat files.
        /// Call this once during plugin startup.
        /// </summary>
        public bool Initialize(string acFolderPath = null)
        {
            try
            {
                string folder = acFolderPath ?? FindACFolder();
                if (string.IsNullOrEmpty(folder))
                {
                    StatusMessage = "AC installation folder not found";
                    Log(StatusMessage);
                    return false;
                }

                Log($"Using AC folder: {folder}");

                // Find portal.dat — try all known filenames
                string portalPath = FindDatFile(folder, new[] {
                    "client_portal.dat", "portal.dat", "client_portal_dat"
                });
                string cellPath = FindDatFile(folder, new[] {
                    "client_cell_1.dat", "cell.dat", "client_cell_1_dat"
                });

                if (portalPath == null)
                {
                    // List what files ARE in the folder for diagnostics
                    string files = "";
                    try
                    {
                        foreach (var f in Directory.GetFiles(folder, "*.dat"))
                            files += Path.GetFileName(f) + ", ";
                    }
                    catch { }
                    StatusMessage = $"No portal.dat found in {folder}. Files: {files}";
                    Log(StatusMessage);
                    return false;
                }

                if (cellPath == null)
                {
                    StatusMessage = $"No cell.dat found in {folder}";
                    Log(StatusMessage);
                    return false;
                }

                Log($"Portal: {portalPath}");
                Log($"Cell: {cellPath}");

                // Open portal.dat
                _portalDat = new DatDatabase();
                if (!_portalDat.Open(portalPath))
                {
                    StatusMessage = $"Failed to open {Path.GetFileName(portalPath)}";
                    // Include ALL diagnostic info so user can report it
                    foreach (var msg in _portalDat.DiagLog)
                        Log(msg);
                    if (_portalDat.DiagLog.Count > 0)
                        StatusMessage += " — " + string.Join(" | ", _portalDat.DiagLog);
                    Log(StatusMessage);
                    return false;
                }

                // Open cell.dat
                _cellDat = new DatDatabase();
                if (!_cellDat.Open(cellPath))
                {
                    StatusMessage = $"Failed to open {Path.GetFileName(cellPath)}";
                    if (_cellDat.DiagLog.Count > 0)
                        StatusMessage += " — " + _cellDat.DiagLog[_cellDat.DiagLog.Count - 1];
                    Log(StatusMessage);
                    _portalDat.Dispose();
                    return false;
                }

                _initialized = true;
                StatusMessage = $"portal={_portalDat.RecordCount} entries (BS={_portalDat.BlockSize}), " +
                                $"cell={_cellDat.RecordCount} entries (BS={_cellDat.BlockSize})";
                Log($"SUCCESS: {StatusMessage}");
                Log($"Portal root=0x{_portalDat.BTreeRoot:X8}, Cell root=0x{_cellDat.BTreeRoot:X8}");

                // Initialize scatter system for procedural terrain objects
                _scatterSystem = new ScatterSystem();
                if (_scatterSystem.Initialize(_portalDat, _cellDat))
                    Log("Scatter system initialized");
                else
                    Log("Scatter system failed to initialize (tree/rock LOS may be limited)");

                // Initialize dungeon wall geometry extractor
                _dungeonLOS = new DungeonLOS();
                _dungeonLOS.Initialize(_portalDat, _cellDat);
                Log("Dungeon wall system initialized");

                // Copy scatter diagnostic logs
                if (_scatterSystem.DiagLog.Count > 0)
                    foreach (var line in _scatterSystem.DiagLog)
                        Log("[Scatter] " + line);

                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Init error: {ex.Message}";
                Log(StatusMessage);
                return false;
            }
        }

        /// <summary>
        /// Finds a .dat file by trying multiple possible filenames.
        /// </summary>
        private string FindDatFile(string folder, string[] names)
        {
            foreach (var name in names)
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        /// <summary>
        /// Retrieves collision geometry for a landblock AND its 8 neighbors (3x3 grid).
        /// This ensures obstacles near landblock boundaries are always loaded.
        /// The landblockId should be the AC Landcell value (format: 0xXXYYnnnn).
        /// </summary>
        public List<BoundingVolume> GetLandblockGeometry(uint landcellId)
        {
            uint landblockKey = (landcellId >> 16) & 0xFFFF;
            uint cellPart = landcellId & 0xFFFF;
            bool isDungeon = cellPart >= 0x0100;

            // Cache key encodes indoor/outdoor to prevent stale data after portalling.
            // Without this, outdoor geometry (no dungeon walls) cached during the brief
            // portal transition poisons all subsequent indoor LOS checks.
            uint cacheKey = isDungeon ? (landblockKey | 0x80000000u) : landblockKey;

            if (_landblockCache.TryGetValue(cacheKey, out var cached))
                return cached;

            // Evict the opposite entry if it exists — same landblock, different mode
            uint oppositeKey = isDungeon ? landblockKey : (landblockKey | 0x80000000u);
            _landblockCache.Remove(oppositeKey);

            var volumes = new List<BoundingVolume>();

            if (isDungeon)
            {
                // Dungeons: only load the current landblock (walls contain everything)
                var lbVolumes = LoadLandblockGeometry(landblockKey);
                volumes.AddRange(lbVolumes);
            }
            else
            {
                // Outdoors: load 3×3 grid for boundary coverage
                uint blockX = (landblockKey >> 8) & 0xFF;
                uint blockY = landblockKey & 0xFF;

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int nx = (int)blockX + dx;
                        int ny = (int)blockY + dy;
                        if (nx < 0 || nx > 0xFE || ny < 0 || ny > 0xFE) continue;

                        uint neighborKey = (uint)((nx << 8) | ny);
                        var neighborVolumes = LoadLandblockGeometry(neighborKey);
                        volumes.AddRange(neighborVolumes);
                    }
                }
            }

            Log($"{(isDungeon ? "Dungeon" : "3x3 grid")} for 0x{landblockKey:X4}: {volumes.Count} total collision volumes");

            if (_landblockCache.Count >= MAX_LANDBLOCK_CACHE)
                EvictOldestLandblock();

            _landblockCache[cacheKey] = volumes;
            return volumes;
        }

        /// <summary>
        /// Loads collision geometry for a landblock from cell.dat and portal.dat.
        /// </summary>
        private List<BoundingVolume> LoadLandblockGeometry(uint landblockKey)
        {
            var volumes = new List<BoundingVolume>();

            if (!_initialized)
            {
                Log("LoadLandblockGeometry called but not initialized!");
                return volumes;
            }

            try
            {
                Log($"--- Loading landblock 0x{landblockKey:X4} ---");

                // Dump sample IDs from cell.dat root to understand the ID format (first time only)
                if (_landblockCache.Count <= 1 && _cellDat.IsLoaded)
                {
                    DumpSampleCellIds();
                }

                // LandblockInfo is always 0xXXYYFFFE — this is where static objects live
                // 0xXXYYFFFF is just terrain heightmap data (no objects)
                uint landblockInfoId = (landblockKey << 16) | 0xFFFE;
                
                byte[] landblockData = _cellDat.GetFileData(landblockInfoId);
                uint foundId = landblockInfoId;

                if (landblockData == null || landblockData.Length <= 8)
                {
                    Log($"No LandblockInfo found for 0x{landblockInfoId:X8}");
                    
                    // Some landblocks legitimately have no FFFE entry (empty ocean tiles etc.)
                    landblockData = null;
                }

                if (landblockData != null)
                {
                    DumpBytes(landblockData, $"Landblock 0x{foundId:X8}");
                    int objectCount = ParseLandblockObjects(landblockData, landblockKey, volumes);
                    Log($"Parsed: {objectCount} static objects -> {volumes.Count} collision volumes");
                }
                else
                {
                    Log($"No LandblockInfo data for 0x{landblockKey:X4}");

                    // Pull search diagnostics from cell.dat
                    foreach (var msg in _cellDat.DiagLog)
                    {
                        if (msg.Contains("FindFile") || msg.Contains("depth=") || msg.Contains("Block[") || msg.Contains("Winner"))
                            Log($"  cell.dat: {msg}");
                    }
                }

                // Also try loading indoor cells (static objects in dungeon rooms)
                LoadIndoorCells(landblockKey, volumes);

                // Load dungeon wall geometry (EnvCell → Environment → CellStruct polygons)
                if (_dungeonLOS != null)
                {
                    var wallVolumes = _dungeonLOS.GetDungeonWalls(landblockKey);
                    volumes.AddRange(wallVolumes);
                }

                // Load scatter objects (trees, rocks) — always load for outdoor terrain
                if (_scatterSystem != null && _scatterSystem.IsInitialized)
                {
                    var scatterVolumes = _scatterSystem.GetScatterVolumes(landblockKey, this);
                    volumes.AddRange(scatterVolumes);
                }

                Log($"Landblock 0x{landblockKey:X4}: TOTAL {volumes.Count} collision volumes");
            }
            catch (Exception ex)
            {
                Log($"Error loading landblock 0x{landblockKey:X4}: {ex.Message}");
            }

            return volumes;
        }

        /// <summary>
        /// Dumps sample file IDs from the cell.dat B-tree root node for diagnostics.
        /// This tells us what format the IDs use.
        /// </summary>
        private bool _cellIdsDumped = false;
        private void DumpSampleCellIds()
        {
            if (_cellIdsDumped) return;
            _cellIdsDumped = true;

            try
            {
                Log("=== Sampling cell.dat file IDs ===");

                var sampleIds = _cellDat.GetSampleIds(30);
                if (sampleIds.Count > 0)
                {
                    string idList = $"Cell.dat samples ({sampleIds.Count}): ";
                    foreach (uint id in sampleIds)
                        idList += $"0x{id:X8} ";
                    Log(idList);

                    uint first = sampleIds[0];
                    uint last = sampleIds[sampleIds.Count - 1];
                    Log($"ID range: 0x{first:X8} to 0x{last:X8}");
                    Log($"First: high=0x{(first >> 16):X4} low=0x{(first & 0xFFFF):X4}");
                }
                else
                {
                    Log("WARNING: No sample IDs from cell.dat B-tree!");
                }

                var portalSamples = _portalDat.GetSampleIds(10);
                if (portalSamples.Count > 0)
                {
                    string pList = "Portal.dat samples: ";
                    foreach (uint id in portalSamples)
                        pList += $"0x{id:X8} ";
                    Log(pList);
                }
            }
            catch (Exception ex)
            {
                Log($"Cell sampling error: {ex.Message}");
            }
        }

        /// <summary>
        /// Dumps first bytes of data for diagnostics.
        /// </summary>
        private void DumpBytes(byte[] data, string label)
        {
            int len = Math.Min(64, data.Length);
            string vals = $"{label} uint32s: ";
            for (int i = 0; i < len; i += 4)
            {
                uint v = BitConverter.ToUInt32(data, i);
                vals += $"[{i}]=0x{v:X8} ";
                if (i == 28)
                {
                    Log(vals);
                    vals = $"{label} +32: ";
                }
            }
            Log(vals);
        }

        /// <summary>
        /// Parses static object placements from a LandblockInfo (0xFFFE) file.
        /// 
        /// Exact format from ACE.DatLoader.FileTypes.LandblockInfo:
        ///   uint32 Id                        (file ID, e.g. 0xAAB3FFFE)
        ///   uint32 NumCells                  (number of EnvCells in landblock)
        ///   uint32 NumObjects                (count of Stab entries)
        ///   Stab[NumObjects]:                (static object placements)
        ///     uint32 Id                      (Setup/model ID, e.g. 0x02xxxxxx or 0x01xxxxxx)
        ///     Frame:                         (7 floats = 28 bytes)
        ///       float OriginX, OriginY, OriginZ
        ///       float RotW, RotX, RotY, RotZ
        ///   ushort NumBuildings
        ///   ushort PackMask
        ///   BuildInfo[NumBuildings]:
        ///     uint32 ModelId
        ///     Frame  (7 floats)
        ///     uint32 NumLeaves
        ///     List{CBldPortal} Portals
        ///   if (PackMask & 1):
        ///     PackedHashTable RestrictionTables
        /// 
        /// Each Stab = 32 bytes (4 + 28). NO InstanceId field.
        /// </summary>
        private int ParseLandblockObjects(byte[] data, uint landblockKey, List<BoundingVolume> volumes)
        {
            if (data == null || data.Length < 12)
            {
                Log($"Landblock data too small ({data?.Length ?? 0} bytes)");
                return 0;
            }

            // --- Parse header ---
            // [0] uint32 Id
            // [4] uint32 NumCells
            // [8] uint32 NumObjects (count for List<Stab>)
            uint fileId = BitConverter.ToUInt32(data, 0);
            uint numCells = BitConverter.ToUInt32(data, 4);
            uint numObjects = BitConverter.ToUInt32(data, 8);

            Log($"LandblockInfo 0x{fileId:X8}: NumCells={numCells}, NumObjects={numObjects}");

            if (numObjects == 0)
            {
                Log("No static objects in this landblock");
                return 0;
            }

            if (numObjects > 5000)
            {
                Log($"Suspicious NumObjects={numObjects}, clamping to 5000");
                numObjects = 5000;
            }

            // --- Parse Stab entries ---
            // Each Stab = uint32 Id (4) + Frame (7 floats = 28) = 32 bytes
            const int STAB_SIZE = 32; // 4 + 28
            int stabStart = 12; // After Id + NumCells + NumObjects
            long neededBytes = stabStart + (long)numObjects * STAB_SIZE;

            if (neededBytes > data.Length)
            {
                Log($"Data too small for {numObjects} Stabs: need {neededBytes}, have {data.Length}");
                // Parse as many as we can fit
                numObjects = (uint)((data.Length - stabStart) / STAB_SIZE);
                if (numObjects == 0) return 0;
                Log($"Adjusted to {numObjects} Stabs");
            }

            // Global offset for world coordinates
            uint blockX = (landblockKey >> 8) & 0xFF;
            uint blockY = landblockKey & 0xFF;
            float globalOffsetX = blockX * 192.0f;
            float globalOffsetY = blockY * 192.0f;

            int parsedCount = 0;

            for (uint i = 0; i < numObjects; i++)
            {
                int off = stabStart + (int)i * STAB_SIZE;
                if (off + STAB_SIZE > data.Length) break;

                // Stab.Unpack: Id then Frame
                uint stabId = BitConverter.ToUInt32(data, off);

                int frameOff = off + 4;
                float px = BitConverter.ToSingle(data, frameOff);
                float py = BitConverter.ToSingle(data, frameOff + 4);
                float pz = BitConverter.ToSingle(data, frameOff + 8);
                float rw = BitConverter.ToSingle(data, frameOff + 12);
                float rx = BitConverter.ToSingle(data, frameOff + 16);
                float ry = BitConverter.ToSingle(data, frameOff + 20);
                float rz = BitConverter.ToSingle(data, frameOff + 24);

                // Validate position
                if (float.IsNaN(px) || float.IsInfinity(px) || Math.Abs(px) > 50000)
                {
                    Log($"  Obj[{i}]: SKIPPED invalid position ({px},{py},{pz})");
                    continue;
                }

                // Log first few for verification
                if (parsedCount < 5)
                    Log($"  Obj[{i}]: Id=0x{stabId:X8} Pos=({px:F1},{py:F1},{pz:F1}) Rot=({rw:F3},{rx:F3},{ry:F3},{rz:F3})");

                var frame = new Frame
                {
                    OriginX = px, OriginY = py, OriginZ = pz,
                    RotW = rw, RotX = rx, RotY = ry, RotZ = rz
                };

                var setupVolumes = GetSetupBoundingVolumes(stabId);
                foreach (var vol in setupVolumes)
                {
                    var transformed = TransformVolume(vol, frame, globalOffsetX, globalOffsetY);
                    if (transformed != null)
                        volumes.Add(transformed);
                }

                parsedCount++;
            }

            // --- Also parse Buildings ---
            int buildingOffset = stabStart + (int)numObjects * STAB_SIZE;
            if (buildingOffset + 4 <= data.Length)
            {
                ushort numBuildings = BitConverter.ToUInt16(data, buildingOffset);
                ushort packMask = BitConverter.ToUInt16(data, buildingOffset + 2);
                Log($"Buildings: {numBuildings}, PackMask=0x{packMask:X4}");

                int bOff = buildingOffset + 4;
                for (int b = 0; b < numBuildings && bOff + 36 <= data.Length; b++)
                {
                    // BuildInfo: uint32 ModelId + Frame(28) + uint32 NumLeaves + List<CBldPortal>
                    uint modelId = BitConverter.ToUInt32(data, bOff);
                    float bpx = BitConverter.ToSingle(data, bOff + 4);
                    float bpy = BitConverter.ToSingle(data, bOff + 8);
                    float bpz = BitConverter.ToSingle(data, bOff + 12);
                    float brw = BitConverter.ToSingle(data, bOff + 16);
                    float brx = BitConverter.ToSingle(data, bOff + 20);
                    float bry = BitConverter.ToSingle(data, bOff + 24);
                    float brz = BitConverter.ToSingle(data, bOff + 28);

                    if (b < 3)
                        Log($"  Building[{b}]: Model=0x{modelId:X8} Pos=({bpx:F1},{bpy:F1},{bpz:F1})");

                    var bFrame = new Frame
                    {
                        OriginX = bpx, OriginY = bpy, OriginZ = bpz,
                        RotW = brw, RotX = brx, RotY = bry, RotZ = brz
                    };

                    // Use composite AABB for buildings — covers the full visual extent
                    var bVolumes = GetBuildingCompositeVolumes(modelId);
                    foreach (var vol in bVolumes)
                    {
                        var transformed = TransformVolume(vol, bFrame, globalOffsetX, globalOffsetY);
                        if (transformed != null)
                            volumes.Add(transformed);
                    }

                    // Skip past: ModelId(4) + Frame(28) + NumLeaves(4) = 36
                    uint numLeaves = BitConverter.ToUInt32(data, bOff + 32);
                    bOff += 36;

                    // Skip portal list: uint32 count, then count × portal entries
                    if (bOff + 4 <= data.Length)
                    {
                        uint numPortals = BitConverter.ToUInt32(data, bOff);
                        bOff += 4;
                        // Each CBldPortal is variable size; skip conservatively
                        // We got the building model, which is the main thing we need
                        // Portal data is complex (flags + cell lists); skip the rest
                        // for now by breaking out of building loop if we can't parse further
                        for (uint p = 0; p < numPortals && bOff + 8 <= data.Length; p++)
                        {
                            ushort portalFlags = BitConverter.ToUInt16(data, bOff);
                            bOff += 2;
                            // Skip remaining portal data (OtherCellId + OtherPortalId + StabList)
                            // This is complex; break if we hit something unexpected
                            if (bOff + 6 <= data.Length)
                            {
                                bOff += 6; // Approximate skip for simple portal entries
                            }
                        }
                    }

                    parsedCount++;
                }
            }

            if (parsedCount == 0)
                Log($"No static objects found in landblock data ({data.Length} bytes)");

            return parsedCount;
        }

        /// <summary>
        /// Loads static objects from the LandblockInfo entry in cell.dat.
        /// These are outdoor objects: buildings, trees, rocks, terrain features.
        /// </summary>
        private void LoadOutdoorObjects(uint landblockInfoId, uint landblockKey, List<BoundingVolume> volumes)
        {
            // This method is now unused - logic moved into LoadLandblockGeometry
            // Kept for API compatibility
        }

        /// <summary>
        /// Loads static objects from dungeon/interior cells in cell.dat.
        /// Indoor cells are numbered 0xXXYY0100 through 0xXXYYFFFD.
        /// Uses ACE's EnvCell format for correct parsing.
        /// </summary>
        private void LoadIndoorCells(uint landblockKey, List<BoundingVolume> volumes)
        {
            float globalOffsetX = ((landblockKey >> 8) & 0xFF) * 192.0f;
            float globalOffsetY = (landblockKey & 0xFF) * 192.0f;

            // Use dat index to find ALL cells — no sequential scan with gap cutoff
            var cellIds = _cellDat.GetLandblockCellIds(landblockKey);

            foreach (uint cellId in cellIds)
            {
                byte[] data = _cellDat.GetFileData(cellId);
                if (data == null || data.Length < 20) continue;

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

                        // Skip surfaces
                        for (int i = 0; i < numSurfaces; i++)
                            reader.ReadUInt16();

                        ushort envId = reader.ReadUInt16();
                        ushort cellStructure = reader.ReadUInt16();

                        // Cell Position (Frame)
                        float cellX = reader.ReadSingle();
                        float cellY = reader.ReadSingle();
                        float cellZ = reader.ReadSingle();
                        reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); // rotation

                        // Skip portals (8 bytes each)
                        for (int i = 0; i < numPortals; i++)
                        {
                            if (ms.Position + 8 > ms.Length) break;
                            reader.ReadUInt64(); // 4 × ushort
                        }

                        // Skip visible cells
                        for (int i = 0; i < numVisibleCells; i++)
                        {
                            if (ms.Position + 2 > ms.Length) break;
                            reader.ReadUInt16();
                        }

                        // Read static objects if present (flag 0x02 = HasStaticObjs)
                        if ((flags & 0x02) != 0 && ms.Position + 4 <= ms.Length)
                        {
                            uint numStabs = reader.ReadUInt32();
                            if (numStabs > 500) numStabs = 500;

                            for (uint i = 0; i < numStabs; i++)
                            {
                                if (ms.Position + 32 > ms.Length) break;

                                uint setupId = reader.ReadUInt32();
                                float px = reader.ReadSingle();
                                float py = reader.ReadSingle();
                                float pz = reader.ReadSingle();
                                float rw = reader.ReadSingle();
                                float rx = reader.ReadSingle();
                                float ry = reader.ReadSingle();
                                float rz = reader.ReadSingle();

                                var frame = new Frame
                                {
                                    // Static object position is in cell-local space
                                    // Cell position is already in landblock-local space
                                    OriginX = px + cellX,
                                    OriginY = py + cellY,
                                    OriginZ = pz + cellZ,
                                    RotW = rw, RotX = rx, RotY = ry, RotZ = rz
                                };

                                var objVolumes = GetSetupBoundingVolumes(setupId);
                                foreach (var vol in objVolumes)
                                {
                                    var transformed = TransformVolume(vol, frame, globalOffsetX, globalOffsetY);
                                    if (transformed != null)
                                        volumes.Add(transformed);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Retrieves bounding volumes for a model ID from portal.dat.
        /// Handles both 0x02xxxxxx (Setup) and 0x01xxxxxx (GfxObj) IDs.
        /// Falls back through: collision spheres → cylinders → SortingSphere → Height+Radius cylinder → GfxObj AABB.
        /// </summary>
        private int _setupLogCount = 0;

        /// <summary>
        /// Public accessor for use by ScatterSystem.
        /// </summary>
        public List<BoundingVolume> GetSetupBoundingVolumesPublic(uint modelId)
        {
            return GetSetupBoundingVolumes(modelId);
        }

        // ===================================================================
        // Terrain height and passability API
        // ===================================================================

        /// <summary>
        /// Returns the parsed CellLandblock (terrain words, height indices, decoded Zs, world origin)
        /// for the given landblock, cached after first load. Returns null if unavailable.
        /// </summary>
        public LandblockData GetLandblockData(uint landblockKey)
            => _scatterSystem?.LoadLandblockData(landblockKey);

        /// <summary>
        /// Returns the 9×9 height grid (81 bytes) for the given landblock, cached after first load.
        /// Returns null if scatter system is unavailable or data is missing.
        /// </summary>
        public byte[] GetTerrainHeightGrid(uint landblockKey)
            => _scatterSystem?.LoadHeightGrid(landblockKey);

        /// <summary>
        /// World-space Z height of terrain vertex (ix, iy) in the 9×9 grid for the given landblock.
        /// ix and iy are 0–8.
        /// </summary>
        public float GetTerrainVertexHeight(uint landblockKey, int ix, int iy)
        {
            var heights = _scatterSystem?.LoadHeightGrid(landblockKey);
            return _scatterSystem?.GetVertexHeight(heights, ix, iy) ?? 0f;
        }

        /// <summary>
        /// Returns true if terrain cell (cellX, cellY) within the landblock is walkable
        /// per AC's FloorZ threshold (0.664). cellX/cellY are 0–7 (8×8 grid).
        /// Returns true if data is unavailable (fail open).
        /// </summary>
        public bool IsTerrainCellPassable(uint landblockKey, int cellX, int cellY)
        {
            if (_scatterSystem == null) return true;
            var heights = _scatterSystem.LoadHeightGrid(landblockKey);
            if (heights == null) return true;
            return _scatterSystem.IsCellPassable(heights, cellX, cellY);
        }

        /// <summary>
        /// Returns per-triangle passability for a cell. Triangle 1 = SE half, Triangle 2 = NW half.
        /// Both default to true (passable) if data is unavailable.
        /// </summary>
        public void GetTerrainTrianglePassability(uint landblockKey, int cellX, int cellY,
            out bool tri1Passable, out bool tri2Passable)
        {
            tri1Passable = true;
            tri2Passable = true;
            if (_scatterSystem == null) return;
            var heights = _scatterSystem.LoadHeightGrid(landblockKey);
            if (heights == null) return;
            _scatterSystem.GetTrianglePassability(heights, cellX, cellY, out tri1Passable, out tri2Passable);
        }

        /// <summary>
        /// Plane-interpolated terrain Z at world (X, Y). Returns NaN if the landblock is
        /// unavailable (e.g. dungeon) or the point is outside the world grid.
        /// </summary>
        public float GetTerrainZWorld(float worldX, float worldY)
        {
            if (_scatterSystem == null) return float.NaN;
            int lbX = (int)(worldX / LandblockData.BlockLength);
            int lbY = (int)(worldY / LandblockData.BlockLength);
            if (lbX < 0 || lbY < 0 || lbX > 255 || lbY > 255) return float.NaN;
            uint key = (uint)((lbX << 8) | lbY);
            var lb = _scatterSystem.LoadLandblockData(key);
            if (lb == null) return float.NaN;
            return lb.GetTerrainZWorld(worldX, worldY);
        }

        /// <summary>
        /// World-space ray vs. landscape. Walks landblocks along the ray (192 m DDA) and
        /// delegates to <see cref="LandblockData.RaycastTerrainLocal"/> inside each.
        /// Returns the nearest hit within <paramref name="maxDist"/>.
        /// <paramref name="hitNormal"/> is the unit upward normal of the triangle hit.
        /// </summary>
        public bool RaycastLandscape(Vector3 worldOrigin, Vector3 dir, float maxDist,
            out float hitDist, out Vector3 hitPoint, out Vector3 hitNormal)
        {
            hitDist = 0f;
            hitPoint = Vector3.Zero;
            hitNormal = Vector3.Zero;
            if (_scatterSystem == null) return false;

            const float L = LandblockData.BlockLength;
            float px = worldOrigin.X, py = worldOrigin.Y;
            float vx = dir.X, vy = dir.Y;

            int lbX = (int)Math.Floor(px / L);
            int lbY = (int)Math.Floor(py / L);

            int stepX = vx > 0 ? 1 : (vx < 0 ? -1 : 0);
            int stepY = vy > 0 ? 1 : (vy < 0 ? -1 : 0);

            float tMaxX = float.PositiveInfinity, tDeltaX = float.PositiveInfinity;
            float tMaxY = float.PositiveInfinity, tDeltaY = float.PositiveInfinity;
            if (stepX != 0)
            {
                float nextX = (stepX > 0 ? (lbX + 1) : lbX) * L;
                tMaxX = (nextX - px) / vx;
                tDeltaX = L / Math.Abs(vx);
            }
            if (stepY != 0)
            {
                float nextY = (stepY > 0 ? (lbY + 1) : lbY) * L;
                tMaxY = (nextY - py) / vy;
                tDeltaY = L / Math.Abs(vy);
            }

            float tStart = 0f;
            float bestT = maxDist;
            Vector3 bestNormal = Vector3.Zero;
            bool found = false;
            int iterLimit = 32; // >6 km — more than any sane ray

            while (iterLimit-- > 0 && tStart < bestT)
            {
                if (lbX < 0 || lbY < 0 || lbX > 255 || lbY > 255) break;

                uint key = (uint)((lbX << 8) | lbY);
                var lb = _scatterSystem.LoadLandblockData(key);
                if (lb != null)
                {
                    Vector3 entry = worldOrigin + dir * tStart;
                    Vector3 localOrigin = new Vector3(
                        entry.X - lb.WorldOriginX,
                        entry.Y - lb.WorldOriginY,
                        entry.Z);

                    float localMax = bestT - tStart;
                    if (lb.RaycastTerrainLocal(localOrigin, dir, localMax,
                            out float tLocal, out Vector3 n))
                    {
                        float tWorld = tStart + tLocal;
                        if (tWorld < bestT)
                        {
                            bestT = tWorld;
                            bestNormal = n;
                            found = true;
                        }
                    }
                }

                if (tMaxX < tMaxY)
                {
                    tStart = tMaxX;
                    tMaxX += tDeltaX;
                    lbX += stepX;
                }
                else
                {
                    tStart = tMaxY;
                    tMaxY += tDeltaY;
                    lbY += stepY;
                }

                if (stepX == 0 && stepY == 0) break; // vertical ray: single landblock
            }

            if (found)
            {
                hitDist = bestT;
                hitPoint = worldOrigin + dir * bestT;
                hitNormal = bestNormal;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Loads a building's actual triangle mesh from its Setup parts for precise raycasting.
        /// Parses each GfxObj part's physics polygons, transforms by part placement frames,
        /// and returns a TriangleMesh volume.
        /// Falls back to composite AABB (from part GfxObj bounds) if mesh parsing fails,
        /// then to GetSetupBoundingVolumes as a last resort.
        /// </summary>
        private List<BoundingVolume> GetBuildingCompositeVolumes(uint modelId)
        {
            // GfxObj building — load mesh directly (no Setup/PartFrames needed)
            if ((modelId & 0xFF000000) == 0x01000000)
            {
                var mesh = LoadGfxObjMesh(modelId);
                if (mesh != null && mesh.Faces.Count > 0)
                {
                    var gfxTris = new List<Vector3>();
                    var identity = new Frame(); // Identity transform — GfxObj is already in building-local space
                    foreach (var face in mesh.Faces)
                    {
                        TriangulateAndTransform(face, mesh.Vertices, identity, gfxTris);
                        if (gfxTris.Count / 3 >= MAX_BUILDING_TRIANGLES) break;
                    }

                    if (gfxTris.Count >= 9)
                    {
                        // Use full-vertex AABB (includes drawing vertices: walls, roof)
                        // Physics polygons may only cover the floor
                        var bMin = mesh.BoundsMin;
                        var bMax = mesh.BoundsMax;

                        Log($"[BLDG] 0x{modelId:X8}: GfxObj MESH {gfxTris.Count / 3} triangles, " +
                            $"size=({bMax.X - bMin.X:F1} x {bMax.Y - bMin.Y:F1} x {bMax.Z - bMin.Z:F1})");

                        var result = new List<BoundingVolume>();
                        result.Add(new BoundingVolume
                        {
                            Type = BoundingVolume.VolumeType.TriangleMesh,
                            Center = new Vector3((bMin.X + bMax.X) * 0.5f, (bMin.Y + bMax.Y) * 0.5f, (bMin.Z + bMax.Z) * 0.5f),
                            Dimensions = new Vector3(bMax.X - bMin.X, bMax.Y - bMin.Y, bMax.Z - bMin.Z),
                            Min = bMin,
                            Max = bMax,
                            MeshTriangles = gfxTris.ToArray(),
                            IsDoor = false
                        });
                        return result;
                    }
                }

                Log($"[BLDG] 0x{modelId:X8}: GfxObj mesh failed — fallback to bounds");
                return GetSetupBoundingVolumes(modelId);
            }

            // Load or get cached Setup
            if (!_setupCache.TryGetValue(modelId, out var setup))
            {
                byte[] data = _portalDat.GetFileData(modelId);
                if (data == null)
                {
                    Log($"[BLDG] 0x{modelId:X8}: NOT FOUND in portal.dat");
                    return GetSetupBoundingVolumes(modelId);
                }

                setup = new SetupInfo();
                setup.Unpack(data);

                if (_setupCache.Count < MAX_SETUP_CACHE)
                    _setupCache[modelId] = setup;
            }

            if (setup.PartFrames == null || setup.PartIds == null || setup.PartIds.Length == 0)
            {
                Log($"[BLDG] 0x{modelId:X8}: {setup.NumParts} parts, PartFrames={setup.PartFrames != null}, PartIds={setup.PartIds?.Length ?? 0} — fallback (no frames)");
                return GetSetupBoundingVolumes(modelId);
            }

            // ── Try triangle mesh first ──────────────────────────────────────
            Log($"[BLDG] 0x{modelId:X8}: trying mesh, {setup.PartIds.Length} parts, {setup.PartFrames.Length} frames");
            var triangles = new List<Vector3>();
            int meshParts = 0;
            int meshFails = 0;
            int count = Math.Min(setup.PartIds.Length, setup.PartFrames.Length);

            for (int i = 0; i < count && triangles.Count / 3 < MAX_BUILDING_TRIANGLES; i++)
            {
                if (setup.PartFrames[i] == null) { meshFails++; continue; }

                var mesh = LoadGfxObjMesh(setup.PartIds[i]);
                if (mesh == null) { meshFails++; continue; }

                foreach (var face in mesh.Faces)
                {
                    TriangulateAndTransform(face, mesh.Vertices, setup.PartFrames[i], triangles);
                    if (triangles.Count / 3 >= MAX_BUILDING_TRIANGLES) break;
                }
                meshParts++;
            }

            Log($"[BLDG] 0x{modelId:X8}: mesh result: {meshParts} parts OK, {meshFails} failed, {triangles.Count / 3} triangles");

            if (triangles.Count >= 9) // At least 3 triangles
            {
                // Compute AABB from all triangle vertices
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                for (int i = 0; i < triangles.Count; i++)
                {
                    var v = triangles[i];
                    if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                    if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                    if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
                }

                Log($"Building 0x{modelId:X8}: MESH {triangles.Count / 3} triangles from {meshParts}/{count} parts, " +
                    $"size=({maxX - minX:F1} x {maxY - minY:F1} x {maxZ - minZ:F1})");

                var result = new List<BoundingVolume>();
                result.Add(new BoundingVolume
                {
                    Type = BoundingVolume.VolumeType.TriangleMesh,
                    Center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f),
                    Dimensions = new Vector3(maxX - minX, maxY - minY, maxZ - minZ),
                    Min = new Vector3(minX, minY, minZ),
                    Max = new Vector3(maxX, maxY, maxZ),
                    MeshTriangles = triangles.ToArray(),
                    IsDoor = false
                });
                return result;
            }

            // ── Fallback: composite AABB from part GfxObj bounds ─────────────
            float cMinX = float.MaxValue, cMinY = float.MaxValue, cMinZ = float.MaxValue;
            float cMaxX = float.MinValue, cMaxY = float.MinValue, cMaxZ = float.MinValue;
            int validParts = 0;

            for (int i = 0; i < count; i++)
            {
                if (setup.PartFrames[i] == null) continue;
                var gfxVol = GetGfxObjBounds(setup.PartIds[i]);
                if (gfxVol == null) continue;

                Vector3[] corners = new Vector3[8]
                {
                    new Vector3(gfxVol.Min.X, gfxVol.Min.Y, gfxVol.Min.Z),
                    new Vector3(gfxVol.Max.X, gfxVol.Min.Y, gfxVol.Min.Z),
                    new Vector3(gfxVol.Min.X, gfxVol.Max.Y, gfxVol.Min.Z),
                    new Vector3(gfxVol.Max.X, gfxVol.Max.Y, gfxVol.Min.Z),
                    new Vector3(gfxVol.Min.X, gfxVol.Min.Y, gfxVol.Max.Z),
                    new Vector3(gfxVol.Max.X, gfxVol.Min.Y, gfxVol.Max.Z),
                    new Vector3(gfxVol.Min.X, gfxVol.Max.Y, gfxVol.Max.Z),
                    new Vector3(gfxVol.Max.X, gfxVol.Max.Y, gfxVol.Max.Z),
                };

                foreach (var corner in corners)
                {
                    Vector3 t = setup.PartFrames[i].TransformPoint(corner);
                    if (t.X < cMinX) cMinX = t.X; if (t.X > cMaxX) cMaxX = t.X;
                    if (t.Y < cMinY) cMinY = t.Y; if (t.Y > cMaxY) cMaxY = t.Y;
                    if (t.Z < cMinZ) cMinZ = t.Z; if (t.Z > cMaxZ) cMaxZ = t.Z;
                }
                validParts++;
            }

            if (validParts == 0)
                return GetSetupBoundingVolumes(modelId);

            Log($"Building 0x{modelId:X8}: AABB fallback from {validParts}/{count} parts, " +
                $"size=({cMaxX - cMinX:F1} x {cMaxY - cMinY:F1} x {cMaxZ - cMinZ:F1})");

            var aabbResult = new List<BoundingVolume>();
            aabbResult.Add(new BoundingVolume
            {
                Type = BoundingVolume.VolumeType.AxisAlignedBox,
                Center = new Vector3((cMinX + cMaxX) * 0.5f, (cMinY + cMaxY) * 0.5f, (cMinZ + cMaxZ) * 0.5f),
                Dimensions = new Vector3(cMaxX - cMinX, cMaxY - cMinY, cMaxZ - cMinZ),
                Min = new Vector3(cMinX, cMinY, cMinZ),
                Max = new Vector3(cMaxX, cMaxY, cMaxZ),
                IsDoor = false
            });
            return aabbResult;
        }

        private List<BoundingVolume> GetSetupBoundingVolumes(uint modelId)
        {
            var result = new List<BoundingVolume>();

            // If it's a GfxObj (0x01xxxxxx), try to get bounds from it directly
            if ((modelId & 0xFF000000) == 0x01000000)
            {
                var gfxVol = GetGfxObjBounds(modelId);
                if (gfxVol != null)
                {
                    result.Add(gfxVol);
                }
                else
                {
                    // GfxObj parse failed — use a default building bounding box.
                    // Buildings are solid structures; better to have an approximate volume
                    // than no volume (which lets rays pass through walls).
                    float defaultR = 5.0f;
                    float defaultH = 8.0f;
                    result.Add(new BoundingVolume
                    {
                        Type = BoundingVolume.VolumeType.AxisAlignedBox,
                        Center = new Vector3(0, 0, defaultH * 0.5f),
                        Dimensions = new Vector3(defaultR * 2, defaultR * 2, defaultH),
                        Min = new Vector3(-defaultR, -defaultR, 0),
                        Max = new Vector3(defaultR, defaultR, defaultH),
                        IsDoor = false
                    });
                }
                return result;
            }

            // Check cache first
            if (!_setupCache.TryGetValue(modelId, out var setup))
            {
                // Load and parse the Setup file
                byte[] data = _portalDat.GetFileData(modelId);
                if (data == null)
                {
                    if (_setupLogCount < 5)
                    {
                        Log($"Setup 0x{modelId:X8}: NOT FOUND in portal.dat");
                        _setupLogCount++;
                    }
                    return result;
                }

                if (_setupLogCount < 5)
                {
                    Log($"Setup 0x{modelId:X8}: {data.Length} bytes loaded");
                    _setupLogCount++;
                }

                setup = new SetupInfo();
                if (!setup.Unpack(data))
                {
                    if (_setupLogCount < 10)
                        Log($"Setup 0x{modelId:X8}: PARSE FAILED");
                    
                    // Even if parse failed, try GfxObj from Parts if we got them
                    if (setup.PartIds != null && setup.PartIds.Length > 0)
                    {
                        var gfxVol = GetGfxObjBounds(setup.PartIds[0]);
                        if (gfxVol != null)
                            result.Add(gfxVol);
                    }
                    return result;
                }

                if (_setupLogCount < 10)
                    Log($"Setup 0x{modelId:X8}: {setup.NumParts} parts, radius={setup.BoundingSphereRadius:F2}, " +
                        $"{setup.Spheres.Count} spheres, {setup.Cylinders.Count} cylinders");

                // Cache it
                if (_setupCache.Count >= MAX_SETUP_CACHE)
                    _setupCache.Clear();
                _setupCache[modelId] = setup;
            }

            // Priority 1: Use collision spheres from Setup
            if (setup.Spheres.Count > 0)
            {
                foreach (var sphere in setup.Spheres)
                {
                    result.Add(new BoundingVolume
                    {
                        Type = BoundingVolume.VolumeType.Sphere,
                        Center = sphere.Center,
                        Dimensions = new Vector3(sphere.Radius, sphere.Radius, sphere.Radius),
                        Min = sphere.Center - new Vector3(sphere.Radius, sphere.Radius, sphere.Radius),
                        Max = sphere.Center + new Vector3(sphere.Radius, sphere.Radius, sphere.Radius),
                        IsDoor = false
                    });
                }
                return result;
            }

            // Priority 2: Use collision cylinders from Setup
            if (setup.Cylinders.Count > 0)
            {
                foreach (var cyl in setup.Cylinders)
                {
                    result.Add(new BoundingVolume
                    {
                        Type = BoundingVolume.VolumeType.Cylinder,
                        Center = new Vector3(
                            cyl.BottomCenter.X,
                            cyl.BottomCenter.Y,
                            cyl.BottomCenter.Z + cyl.Height * 0.5f
                        ),
                        Dimensions = new Vector3(cyl.Radius, cyl.Radius, cyl.Height),
                        Min = new Vector3(
                            cyl.BottomCenter.X - cyl.Radius,
                            cyl.BottomCenter.Y - cyl.Radius,
                            cyl.BottomCenter.Z
                        ),
                        Max = new Vector3(
                            cyl.BottomCenter.X + cyl.Radius,
                            cyl.BottomCenter.Y + cyl.Radius,
                            cyl.BottomCenter.Z + cyl.Height
                        ),
                        IsDoor = false
                    });
                }
                return result;
            }

            // Priority 3: Use bounding sphere from Setup
            if (setup.BoundingSphereRadius > 0.1f)
            {
                float r = setup.BoundingSphereRadius;
                result.Add(new BoundingVolume
                {
                    Type = BoundingVolume.VolumeType.Sphere,
                    Center = setup.BoundingSphereCenter,
                    Dimensions = new Vector3(r, r, r),
                    Min = setup.BoundingSphereCenter - new Vector3(r, r, r),
                    Max = setup.BoundingSphereCenter + new Vector3(r, r, r),
                    IsDoor = false
                });
                return result;
            }

            // Priority 4: Use Height + Radius from Setup to create a cylinder
            if (setup.Radius > 0.1f && setup.Height > 0.1f)
            {
                float r = setup.Radius;
                float h = setup.Height;
                result.Add(new BoundingVolume
                {
                    Type = BoundingVolume.VolumeType.Cylinder,
                    Center = new Vector3(0, 0, h * 0.5f),
                    Dimensions = new Vector3(r, r, h),
                    Min = new Vector3(-r, -r, 0),
                    Max = new Vector3(r, r, h),
                    IsDoor = false
                });
                return result;
            }

            // Priority 5: Fall back to GfxObj bounding data from first part
            if (setup.PartIds != null && setup.PartIds.Length > 0)
            {
                var gfxVol = GetGfxObjBounds(setup.PartIds[0]);
                if (gfxVol != null)
                    result.Add(gfxVol);
            }

            return result;
        }

        // ── Triangle mesh loading ───────────────────────────────────────────

        /// <summary>
        /// Reads a variable-length compressed uint32 from the dat file format.
        /// AC's "SmartArray" uses this encoding for array lengths:
        ///   - If first byte &lt; 0x80: value = byte (1 byte total)
        ///   - If first byte has bit7 set but bit6 clear: value = 2 bytes
        ///   - Otherwise: value = 4 bytes total
        /// </summary>
        private static uint ReadCompressedUInt32(BinaryReader reader)
        {
            byte b0 = reader.ReadByte();
            if ((b0 & 0x80) == 0)
                return b0;

            byte b1 = reader.ReadByte();
            if ((b0 & 0x40) == 0)
                return (uint)(((b0 & 0x7F) << 8) | b1);

            ushort s = reader.ReadUInt16();
            return (uint)((((b0 & 0x3F) << 8) | b1) << 16 | s);
        }

        /// <summary>Cached mesh data for a single GfxObj (vertices + polygon faces).</summary>
        private class GfxObjMeshData
        {
            public Dictionary<int, Vector3> Vertices;
            public List<int[]> Faces; // Each face is an array of vertex indices
            // AABB from ALL vertices (physics + drawing) — captures full building extent
            public Vector3 BoundsMin;
            public Vector3 BoundsMax;
        }

        /// <summary>
        /// Loads a GfxObj's physics mesh (vertices + polygon faces) for precise raycasting.
        /// Parses the correct CVertexArray format (ushort key, ushort numUVs per vertex)
        /// and the physics polygon section (flag 0x01).
        /// Returns null if the GfxObj has no physics polygons or parsing fails.
        /// </summary>
        private int _meshLogCount = 0;

        private GfxObjMeshData LoadGfxObjMesh(uint gfxObjId)
        {
            if (_meshCache.TryGetValue(gfxObjId, out var cached))
                return cached;

            byte[] data = _portalDat.GetFileData(gfxObjId);
            if (data == null || data.Length < 20)
            {
                if (_meshLogCount < 30) { Log($"[MESH] 0x{gfxObjId:X8}: not found or too small ({data?.Length ?? 0}b)"); _meshLogCount++; }
                return null;
            }

            bool doLog = _meshLogCount < 30;

            try
            {
                using (var ms = new MemoryStream(data))
                using (var reader = new BinaryReader(ms))
                {
                    uint id = reader.ReadUInt32();
                    uint flags = reader.ReadUInt32();

                    // Surfaces use SmartArray format: compressed uint32 count + uint32[] IDs
                    if (ms.Position + 1 > ms.Length) { if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: truncated before surfaces"); _meshLogCount++; } return null; }
                    uint numSurfaces = ReadCompressedUInt32(reader);
                    if (numSurfaces > 10000) { if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: bad numSurfaces={numSurfaces}"); _meshLogCount++; } return null; }
                    ms.Seek(numSurfaces * 4, SeekOrigin.Current);

                    // CVertexArray
                    if (ms.Position + 8 > ms.Length) { if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: truncated before vertex array"); _meshLogCount++; } return null; }
                    int vertexType = reader.ReadInt32();
                    uint numVertices = reader.ReadUInt32();

                    if (doLog) Log($"[MESH] 0x{gfxObjId:X8}: {data.Length}b, flags=0x{flags:X2}, surfs={numSurfaces}, vtxType={vertexType}, vtxCount={numVertices}");

                    if (numVertices > 100000 || numVertices == 0)
                    {
                        if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: bad vertex count"); _meshLogCount++; }
                        return null;
                    }

                    var vertices = new Dictionary<int, Vector3>((int)numVertices);

                    for (uint i = 0; i < numVertices; i++)
                    {
                        if (ms.Position + 28 > ms.Length)
                        {
                            if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: truncated at vertex {i}/{numVertices}, pos={ms.Position}/{ms.Length}"); _meshLogCount++; }
                            return null;
                        }
                        ushort vertexKey = reader.ReadUInt16();
                        ushort numUVs = reader.ReadUInt16();

                        float x = reader.ReadSingle();
                        float y = reader.ReadSingle();
                        float z = reader.ReadSingle();
                        vertices[vertexKey] = new Vector3(x, y, z);

                        long skip = 12 + (long)numUVs * 8;
                        if (ms.Position + skip > ms.Length)
                        {
                            if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: truncated at vertex {i} UVs, numUVs={numUVs}, need={skip}, have={ms.Length - ms.Position}"); _meshLogCount++; }
                            return null;
                        }
                        ms.Seek(skip, SeekOrigin.Current);
                    }

                    if (doLog) Log($"[MESH] 0x{gfxObjId:X8}: verts OK, pos after verts={ms.Position}/{ms.Length}");

                    // Compute AABB from ALL vertices — captures full building extent
                    // (physics polygons may only reference floor vertices)
                    Vector3 allMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 allMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    foreach (var v in vertices.Values)
                    {
                        if (v.X < allMin.X) allMin.X = v.X; if (v.X > allMax.X) allMax.X = v.X;
                        if (v.Y < allMin.Y) allMin.Y = v.Y; if (v.Y > allMax.Y) allMax.Y = v.Y;
                        if (v.Z < allMin.Z) allMin.Z = v.Z; if (v.Z > allMax.Z) allMax.Z = v.Z;
                    }

                    // Physics polygons require flag 0x01 (HasPhysics)
                    if ((flags & 0x01) == 0)
                    {
                        if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: flags=0x{flags:X2} — no HasPhysics bit"); _meshLogCount++; }
                        return null;
                    }

                    // Physics polygons use SmartArray format: compressed count + (ushort key + polygon data)[]
                    if (ms.Position + 1 > ms.Length) { if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: truncated before polygon count"); _meshLogCount++; } return null; }
                    uint numPolygons = ReadCompressedUInt32(reader);
                    if (doLog) Log($"[MESH] 0x{gfxObjId:X8}: numPolygons={numPolygons}");

                    if (numPolygons == 0 || numPolygons > 50000) { if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: bad polygon count"); _meshLogCount++; } return null; }

                    var faces = new List<int[]>((int)numPolygons);

                    for (uint p = 0; p < numPolygons; p++)
                    {
                        if (ms.Position + 12 > ms.Length) { if (doLog) Log($"[MESH] 0x{gfxObjId:X8}: truncated at poly {p}"); break; }

                        reader.ReadUInt16(); // polygon key (SmartArray dictionary key)
                        byte numPts = reader.ReadByte();
                        byte stipplingType = reader.ReadByte();
                        int sidesType = reader.ReadInt32();
                        reader.ReadInt16(); // posSurface
                        reader.ReadInt16(); // negSurface

                        if (numPts < 3 || numPts > 50) { if (doLog) Log($"[MESH] 0x{gfxObjId:X8}: bad numPts={numPts} at poly {p}"); break; }
                        if (ms.Position + numPts * 2 > ms.Length) break;

                        int[] vertexIds = new int[numPts];
                        for (int v = 0; v < numPts; v++)
                            vertexIds[v] = reader.ReadInt16();

                        faces.Add(vertexIds);

                        bool hasNoPos = (stipplingType & 0x04) != 0;
                        bool hasNoNeg = (stipplingType & 0x08) != 0;

                        if (!hasNoPos)
                        {
                            if (ms.Position + numPts > ms.Length) break;
                            ms.Seek(numPts, SeekOrigin.Current);
                        }

                        if (!hasNoNeg && sidesType == 2)
                        {
                            if (ms.Position + numPts > ms.Length) break;
                            ms.Seek(numPts, SeekOrigin.Current);
                        }
                    }

                    if (doLog) Log($"[MESH] 0x{gfxObjId:X8}: parsed {faces.Count} faces");
                    if (doLog) _meshLogCount++;

                    if (faces.Count == 0) return null;

                    var mesh = new GfxObjMeshData { Vertices = vertices, Faces = faces, BoundsMin = allMin, BoundsMax = allMax };

                    if (_meshCache.Count >= MAX_MESH_CACHE)
                        _meshCache.Clear();
                    _meshCache[gfxObjId] = mesh;

                    return mesh;
                }
            }
            catch (Exception ex)
            {
                if (doLog) { Log($"[MESH] 0x{gfxObjId:X8}: EXCEPTION {ex.GetType().Name}: {ex.Message}"); _meshLogCount++; }
                return null;
            }
        }

        /// <summary>
        /// Fan-triangulates a polygon's vertex indices and transforms each vertex
        /// by the given frame, appending the resulting triangles to the output list.
        /// Returns the number of triangles added.
        /// </summary>
        private static int TriangulateAndTransform(
            int[] faceIndices,
            Dictionary<int, Vector3> vertices,
            Frame partFrame,
            List<Vector3> outTriangles)
        {
            if (faceIndices.Length < 3) return 0;

            int added = 0;
            // Fan triangulation: vertex 0 is the hub
            if (!vertices.TryGetValue(faceIndices[0], out var v0)) return 0;
            Vector3 tv0 = partFrame.TransformPoint(v0);

            for (int i = 1; i < faceIndices.Length - 1; i++)
            {
                if (!vertices.TryGetValue(faceIndices[i], out var v1)) continue;
                if (!vertices.TryGetValue(faceIndices[i + 1], out var v2)) continue;

                outTriangles.Add(tv0);
                outTriangles.Add(partFrame.TransformPoint(v1));
                outTriangles.Add(partFrame.TransformPoint(v2));
                added++;
            }

            return added;
        }

        /// <summary>
        /// Loads a GfxObj from portal.dat and extracts its bounding volume.
        /// </summary>
        private BoundingVolume GetGfxObjBounds(uint gfxObjId)
        {
            byte[] data = _portalDat.GetFileData(gfxObjId);
            if (data == null) return null;

            var gfxObj = new GfxObjInfo();
            if (!gfxObj.Unpack(data) || !gfxObj.HasBounds)
                return null;

            if (gfxObj.SortingRadius > 0.1f)
            {
                return new BoundingVolume
                {
                    Type = BoundingVolume.VolumeType.Sphere,
                    Center = gfxObj.SortingCenter,
                    Dimensions = new Vector3(gfxObj.SortingRadius, gfxObj.SortingRadius, gfxObj.SortingRadius),
                    Min = gfxObj.BoundsMin,
                    Max = gfxObj.BoundsMax,
                    IsDoor = false
                };
            }

            // Use AABB from vertices
            return new BoundingVolume
            {
                Type = BoundingVolume.VolumeType.AxisAlignedBox,
                Center = (gfxObj.BoundsMin + gfxObj.BoundsMax) * 0.5f,
                Dimensions = gfxObj.BoundsMax - gfxObj.BoundsMin,
                Min = gfxObj.BoundsMin,
                Max = gfxObj.BoundsMax,
                IsDoor = false
            };
        }

        /// <summary>
        /// Transforms a local-space bounding volume by a placement frame,
        /// producing a world-space AABB for raycasting.
        /// </summary>
        private BoundingVolume TransformVolume(BoundingVolume localVol, Frame placement, 
                                                float globalOffsetX, float globalOffsetY)
        {
            if (localVol == null) return null;

            try
            {
                // For spheres: rotate center, keep radius (spheres are rotation-invariant)
                if (localVol.Type == BoundingVolume.VolumeType.Sphere)
                {
                    Vector3 worldCenter = placement.TransformPoint(localVol.Center);
                    worldCenter.X += globalOffsetX;
                    worldCenter.Y += globalOffsetY;

                    float r = localVol.Dimensions.X; // Radius
                    return new BoundingVolume
                    {
                        Type = BoundingVolume.VolumeType.Sphere,
                        Center = worldCenter,
                        Dimensions = localVol.Dimensions,
                        Min = worldCenter - new Vector3(r, r, r),
                        Max = worldCenter + new Vector3(r, r, r),
                        IsDoor = localVol.IsDoor
                    };
                }

                // For triangle meshes: transform every vertex, preserve full-extent AABB
                if (localVol.Type == BoundingVolume.VolumeType.TriangleMesh &&
                    localVol.MeshTriangles != null && localVol.MeshTriangles.Length >= 3)
                {
                    var tris = new Vector3[localVol.MeshTriangles.Length];
                    for (int i = 0; i < localVol.MeshTriangles.Length; i++)
                    {
                        Vector3 w = placement.TransformPoint(localVol.MeshTriangles[i]);
                        w.X += globalOffsetX;
                        w.Y += globalOffsetY;
                        tris[i] = w;
                    }

                    // Transform the full-vertex AABB corners (covers walls/roof, not just physics floor)
                    // Transform all 8 corners of the local AABB and recompute world AABB
                    Vector3 bMin = localVol.Min;
                    Vector3 bMax = localVol.Max;
                    Vector3 worldMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 worldMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                    for (int cx = 0; cx < 2; cx++)
                    for (int cy = 0; cy < 2; cy++)
                    for (int cz = 0; cz < 2; cz++)
                    {
                        Vector3 corner = new Vector3(
                            cx == 0 ? bMin.X : bMax.X,
                            cy == 0 ? bMin.Y : bMax.Y,
                            cz == 0 ? bMin.Z : bMax.Z);
                        Vector3 wc = placement.TransformPoint(corner);
                        wc.X += globalOffsetX;
                        wc.Y += globalOffsetY;
                        worldMin = Vector3.Min(worldMin, wc);
                        worldMax = Vector3.Max(worldMax, wc);
                    }

                    return new BoundingVolume
                    {
                        Type = BoundingVolume.VolumeType.TriangleMesh,
                        Center = (worldMin + worldMax) * 0.5f,
                        Dimensions = worldMax - worldMin,
                        Min = worldMin,
                        Max = worldMax,
                        MeshTriangles = tris,
                        IsDoor = localVol.IsDoor
                    };
                }

                // For AABBs and other types: transform the 8 corners and compute new AABB
                Vector3[] corners = new Vector3[8]
                {
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

                for (int i = 0; i < 8; i++)
                {
                    Vector3 world = placement.TransformPoint(corners[i]);
                    world.X += globalOffsetX;
                    world.Y += globalOffsetY;

                    newMin = Vector3.Min(newMin, world);
                    newMax = Vector3.Max(newMax, world);
                }

                return new BoundingVolume
                {
                    Type = BoundingVolume.VolumeType.AxisAlignedBox,
                    Center = (newMin + newMax) * 0.5f,
                    Dimensions = newMax - newMin,
                    Min = newMin,
                    Max = newMax,
                    IsDoor = localVol.IsDoor
                };
            }
            catch (Exception ex)
            {
                Log($"Error transforming volume: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Searches common installation paths for Asheron's Call.
        /// </summary>
        private string FindACFolder()
        {
            string[] searchPaths = new string[]
            {
                @"C:\Turbine\Asheron's Call",                    // User's known path
                @"C:\Program Files\Turbine\Asheron's Call",
                @"C:\Program Files (x86)\Turbine\Asheron's Call",
                @"C:\Games\Asheron's Call",
                @"C:\AC",
                @"C:\Asheron's Call",
                @"C:\Games\AC",
                @"D:\Turbine\Asheron's Call",
                @"D:\Games\Asheron's Call",
                @"D:\AC",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Turbine", "Asheron's Call"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Turbine", "Asheron's Call"),
            };

            // Check for both original (portal.dat) and ToD (client_portal.dat) filenames
            string[] portalNames = { "client_portal.dat", "portal.dat" };
            string[] cellNames = { "client_cell_1.dat", "cell.dat" };

            foreach (var path in searchPaths)
            {
                try
                {
                    if (!Directory.Exists(path)) continue;

                    bool hasPortal = false, hasCell = false;
                    foreach (var pn in portalNames)
                        if (File.Exists(Path.Combine(path, pn))) { hasPortal = true; break; }
                    foreach (var cn in cellNames)
                        if (File.Exists(Path.Combine(path, cn))) { hasCell = true; break; }

                    if (hasPortal && hasCell)
                    {
                        Log($"Found AC folder: {path}");
                        return path;
                    }
                    else if (hasPortal || hasCell)
                    {
                        Log($"Partial match at {path} (portal={hasPortal}, cell={hasCell})");
                    }
                }
                catch { }
            }

            // Also try to find via registry
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Turbine\Asheron's Call"))
                {
                    if (key != null)
                    {
                        string installPath = key.GetValue("InstallPath") as string;
                        if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                            return installPath;
                    }
                }
            }
            catch { }

            Log("Could not find Asheron's Call installation");
            return null;
        }

        private void EvictOldestLandblock()
        {
            if (_landblockCache.Count == 0) return;

            // Simple eviction: remove the first key
            foreach (var key in _landblockCache.Keys)
            {
                _landblockCache.Remove(key);
                break;
            }
        }

        public void FlushCache()
        {
            _landblockCache.Clear();
            _setupCache.Clear();
            Log("All caches flushed");
        }

        public void FlushLandblock(uint landblockKey)
        {
            _landblockCache.Remove(landblockKey);
            _landblockCache.Remove(landblockKey | 0x80000000u);
        }

        public void Dispose()
        {
            _portalDat?.Dispose();
            _cellDat?.Dispose();
            _landblockCache.Clear();
            _setupCache.Clear();
            _initialized = false;
        }

        private void Log(string msg)
        {
            string line = $"[GeoLoader] {msg}";
            System.Diagnostics.Debug.WriteLine(line);
            if (DiagLog.Count < 200) DiagLog.Add(msg);
        }
    }
}

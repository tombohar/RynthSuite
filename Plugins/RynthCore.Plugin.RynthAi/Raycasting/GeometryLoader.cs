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
        // Deep-audit finding #5 (2026-06-18): these three caches are read/written
        // from both the OnTick pump thread (combat/nav raycasts) and the render/UI
        // path (RynthVision's Submit + InspectTerrain button), with zero
        // synchronization — plain Dictionary Add/Remove/Clear from two threads at
        // once is classic bucket-array corruption on whichever thread lands on it,
        // frequently AC's own main thread. One gate covers all three; per the
        // audit's fix note, only the cache read/insert/evict/Clear calls are
        // guarded (the expensive .dat parse/build stays outside the lock).
        private readonly object _cacheGate = new();

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
        public int LandblocksCached { get { lock (_cacheGate) return _landblockCache.Count; } }
        public int SetupsCached { get { lock (_cacheGate) return _setupCache.Count; } }
        public bool IsInitialized => _initialized;
        public string StatusMessage { get; private set; } = "Not initialized";
        public List<string> DiagLog { get; } = new List<string>();

        /// <summary>
        /// Initializes the geometry loader by opening the .dat files.
        /// Call this once during plugin startup.
        /// </summary>
        public bool Initialize(string? acFolderPath = null)
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

            lock (_cacheGate)
            {
                if (_landblockCache.TryGetValue(cacheKey, out var cached))
                    return cached;

                // Evict the opposite entry if it exists — same landblock, different mode
                uint oppositeKey = isDungeon ? landblockKey : (landblockKey | 0x80000000u);
                _landblockCache.Remove(oppositeKey);
            }

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

            lock (_cacheGate)
            {
                if (_landblockCache.Count >= MAX_LANDBLOCK_CACHE)
                    EvictOldestLandblock();

                _landblockCache[cacheKey] = volumes;
            }
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
                bool dumpSample;
                lock (_cacheGate) dumpSample = _landblockCache.Count <= 1;
                if (dumpSample && _cellDat.IsLoaded)
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
                        // CBldPortal: Flags, OtherCellId, OtherPortalId, NumStabs (ushorts), then NumStabs ushorts,
                        // aligned to 4 (RynthNav.Baker CollisionGeometry.ReadLandBlockInfo reads it the same way).
                        // The old 8-byte skip lost its place after the first building with a stab list (2026-11-26):
                        // B470 kept 1 of its 28 buildings, the world 2,425 of 6,979, and B470's town wall
                        // (0x01002CDE) was never loaded, so melee ran into it.
                        for (uint p = 0; p < numPortals && bOff + 8 <= data.Length; p++)
                        {
                            ushort numStabs = BitConverter.ToUInt16(data, bOff + 6);
                            int size = 8 + numStabs * 2;
                            bOff += (size + 3) & ~3;
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
        /// Adds the static objects of a landblock's dungeon/interior cells (0xXXYY0100 and up) to the LOS
        /// volumes: GetStaticCollision's cell stabs, as AC collides with them.
        /// </summary>
        private void LoadIndoorCells(uint landblockKey, List<BoundingVolume> volumes)
        {
            // A stab's frame is already in the landblock's space; this added its cell's origin to it, so
            // combat LOS had nothing at 0x0190's table, 0x00C1's statue or 0x000A's altar, and blockers a
            // cell's offset away from where anything stands (2026-11-18). The volumes are now the shapes AC
            // collides with (physics polygons, else cylinders, else spheres, triangle hits only), not a
            // bounding sphere or box round the model: a shot over a 0.9 m table is clear, and a light or a
            // banner with no collision at all no longer stops one.
            foreach (var s in GetStaticCollision(landblockKey))
                if (s.Source == StaticSource.CellStab)
                    volumes.AddRange(s.Parts);
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
                            IsDoor = false,
                            // Triangle hits only: the box round a building's drawing is no wall (doorways,
                            // courtyards); its rooms' walls come from DungeonLOS, which loads every EnvCell.
                            NoAabbFallback = true
                        });
                        return result;
                    }
                }

                Log($"[BLDG] 0x{modelId:X8}: GfxObj mesh failed — fallback to bounds");
                return GetSetupBoundingVolumes(modelId);
            }

            // Load or get cached Setup
            bool haveCached;
            SetupInfo setup;
            lock (_cacheGate) haveCached = _setupCache.TryGetValue(modelId, out setup);
            if (!haveCached)
            {
                byte[] data = _portalDat.GetFileData(modelId);
                if (data == null)
                {
                    Log($"[BLDG] 0x{modelId:X8}: NOT FOUND in portal.dat");
                    return GetSetupBoundingVolumes(modelId);
                }

                setup = new SetupInfo();
                setup.Unpack(data);

                lock (_cacheGate)
                {
                    if (_setupCache.Count < MAX_SETUP_CACHE)
                        _setupCache[modelId] = setup;
                }
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
                    TriangulateAndTransform(face, mesh.Vertices, setup, i, triangles);
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
                    IsDoor = false,
                    NoAabbFallback = true
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
                    Vector3 t = setup.PartToSetup(i, corner);
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
                // A big GfxObj placed on its own (Neftet's rock spires and mesas, R-20261120-203229-771e):
                // its sorting sphere, 9-75 m round, is its render bound. A character and a monster 50 m
                // apart both stood inside one, so the line between them crossed no surface and read as
                // clear, and every cast went into the rock. Its physics polygons are what AC collides with;
                // with none (no HasPhysics) nothing stops a spell there. As a setup's part spheres are
                // (GetSetupCollision: a GfxObj on its own is one part at the object's origin, size 1).
                if (gfxVol != null && gfxVol.Type == BoundingVolume.VolumeType.Sphere && gfxVol.Dimensions.X > LargePartSphereRadius)
                {
                    var collision = GetSetupCollision(modelId);
                    if (collision.Kind == StaticCollisionKind.Polygons) { result.Add(MeshOf(collision.Triangles)); return result; }
                    if (GfxObjDrawingOnly(modelId)) return result;
                }
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
            bool haveCachedSetup;
            SetupInfo setup;
            lock (_cacheGate) haveCachedSetup = _setupCache.TryGetValue(modelId, out setup);
            if (!haveCachedSetup)
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
                        var gfxVol = GetPartBounds(setup, 0);
                        if (gfxVol != null)
                            result.Add(gfxVol);
                    }
                    return result;
                }

                if (_setupLogCount < 10)
                    Log($"Setup 0x{modelId:X8}: {setup.NumParts} parts, radius={setup.BoundingSphereRadius:F2}, " +
                        $"{setup.Spheres.Count} spheres, {setup.Cylinders.Count} cylinders");

                // Cache it
                lock (_cacheGate)
                {
                    if (_setupCache.Count >= MAX_SETUP_CACHE)
                        _setupCache.Clear();
                    _setupCache[modelId] = setup;
                }
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
                var gfxVol = GetPartBounds(setup, 0);
                // A big object's first-part sphere is its render bound, not a wall (R-20261119-114214-b9ed,
                // landblock 0xB64A): wall panel 0x02001C33 is 24 x 0.4 m but its part sphere 28 m round,
                // and 0x02001C39 (29 parts, not one with physics polygons) got a 13 m sphere. They stand
                // every 24 m along the landblock edge, so the spheres filled the corner: a line between a
                // character and a monster 1.5 m off crossed a sphere's surface and read as blocked, and
                // the selected monster was locked and dropped every 2 s without a cast (and a line through
                // a panel, both ends inside a sphere, read as clear). Its parts' physics polygons are what
                // AC collides with; with none at all nothing stops a spell there.
                // Tools/RynthCore.LosProof checks this spot.
                if (gfxVol != null && gfxVol.Type == BoundingVolume.VolumeType.Sphere && gfxVol.Dimensions.X > LargePartSphereRadius)
                {
                    // GetSetupCollision: the parts' physics polygons, posed and scaled as AC places them.
                    var collision = GetSetupCollision(modelId);
                    if (collision.Kind == StaticCollisionKind.Polygons) { result.Add(MeshOf(collision.Triangles)); return result; }
                    if (NoPartHasPhysics(setup)) return result;
                }
                if (gfxVol != null)
                    result.Add(gfxVol);
            }

            return result;
        }

        // A part's GfxObj bounds where the setup puts that part: scaled by its DefaultScale and posed by its
        // frame in the Resting/Default placement (SetupInfo.PartToSetup). They were the GfxObj's own,
        // as if every part sat at the setup's origin at size 1.
        private BoundingVolume GetPartBounds(SetupInfo setup, int part)
        {
            var vol = GetGfxObjBounds(setup.PartIds[part]);
            if (vol == null || (setup.PartFrames == null && setup.DefaultScale == null)) return vol;
            if (vol.Type == BoundingVolume.VolumeType.Sphere)
            {
                var sc = setup.PartScale(part);
                float r = vol.Dimensions.X * Math.Max(Math.Abs(sc.X), Math.Max(Math.Abs(sc.Y), Math.Abs(sc.Z)));
                var c = setup.PartToSetup(part, vol.Center);
                return new BoundingVolume
                {
                    Type = BoundingVolume.VolumeType.Sphere,
                    Center = c,
                    Dimensions = new Vector3(r, r, r),
                    Min = c - new Vector3(r, r, r),
                    Max = c + new Vector3(r, r, r),
                    IsDoor = vol.IsDoor
                };
            }
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int k = 0; k < 8; k++)
            {
                var corner = new Vector3((k & 1) == 0 ? vol.Min.X : vol.Max.X, (k & 2) == 0 ? vol.Min.Y : vol.Max.Y, (k & 4) == 0 ? vol.Min.Z : vol.Max.Z);
                var t = setup.PartToSetup(part, corner);
                min = Vector3.Min(min, t);
                max = Vector3.Max(max, t);
            }
            return new BoundingVolume
            {
                Type = BoundingVolume.VolumeType.AxisAlignedBox,
                Center = (min + max) * 0.5f,
                Dimensions = max - min,
                Min = min,
                Max = max,
                IsDoor = vol.IsDoor
            };
        }

        /// <summary>Past this radius a part's sorting sphere is too coarse to stand for the object (meters).</summary>
        internal const float LargePartSphereRadius = 4.0f;

        /// <summary>Every part of <paramref name="setup"/> was read and none has physics polygons (the GfxObj's
        /// HasPhysics flag, 0x01): nothing in the object collides. False when a part couldn't be read.</summary>
        private bool NoPartHasPhysics(SetupInfo setup)
        {
            foreach (uint part in setup.PartIds)
            {
                byte[] head = _portalDat.GetFileData(part);
                if (head == null || head.Length < 8) return false;
                if ((BitConverter.ToUInt32(head, 4) & 0x01) != 0) return false;
            }
            return true;
        }

        /// <summary>The GfxObj was read and has no physics polygons (no HasPhysics flag, 0x01).</summary>
        private bool GfxObjDrawingOnly(uint gfxObjId)
        {
            byte[] head = _portalDat.GetFileData(gfxObjId);
            return head != null && head.Length >= 8 && (BitConverter.ToUInt32(head, 4) & 0x01) == 0;
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
            lock (_cacheGate)
            {
                if (_meshCache.TryGetValue(gfxObjId, out var cached))
                    return cached;
            }

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

                    lock (_cacheGate)
                    {
                        if (_meshCache.Count >= MAX_MESH_CACHE)
                            _meshCache.Clear();
                        _meshCache[gfxObjId] = mesh;
                    }

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
        // A setup's part: its GfxObj's vertices scaled by the part's DefaultScale, then posed by its frame
        // (SetupInfo.PartToSetup), as ACE PartArray poses the parts of a placed object.
        private static int TriangulateAndTransform(
            int[] faceIndices,
            Dictionary<int, Vector3> vertices,
            SetupInfo setup,
            int part,
            List<Vector3> outTriangles)
        {
            if (faceIndices.Length < 3) return 0;
            if (!vertices.TryGetValue(faceIndices[0], out var v0)) return 0;
            Vector3 tv0 = setup.PartToSetup(part, v0);
            int added = 0;
            for (int i = 1; i < faceIndices.Length - 1; i++)
            {
                if (!vertices.TryGetValue(faceIndices[i], out var v1)) continue;
                if (!vertices.TryGetValue(faceIndices[i + 1], out var v2)) continue;
                outTriangles.Add(tv0);
                outTriangles.Add(setup.PartToSetup(part, v1));
                outTriangles.Add(setup.PartToSetup(part, v2));
                added++;
            }
            return added;
        }

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

        // ── Static collision: what AC itself collides with ─────────────────────────────────────────────
        //
        // AC's rule for a placed object (ACE PhysicsObj.FindObjCollisions, Physics/PhysicsObj.cs:412-450, and
        // PartArray.CacheHasPhysicsBSP): when any of its parts has physics polygons, those, posed by the
        // Resting placement and scaled by DefaultScale (SetupInfo.PartToSetup); else its CylSpheres; else its
        // Spheres; else nothing at all (lights, banners, decals). This kept the polygons only, so 60,289
        // cylinder and sphere objects at body height in the dungeons were nowhere on the map: 0x6346's
        // pillars (0x0200028D), 0x0190's round tables (0x020000CE), 0x00FE's 0x02001055 (2026-11-18 survey).
        // The map, the blocked-doorway rays and combat LOS all take triangles, so cylinders and spheres are
        // handed over as closed solids (AppendCylinderSolid, AppendSphereSolid) and every consumer sees them
        // as it sees a statue. No box stands for anything (NoAabbFallback):
        // the box round an arch, a web or 0x039B's spiral stair is no wall, and the fallback reported 4,172
        // hits where no triangle was in 54,000 fan rays.

        /// <summary>The most physics triangles one placed object keeps (a sanity bound, not a cut: the
        /// largest setup any cell or landblock places has far fewer). Buildings keep MAX_BUILDING_TRIANGLES.</summary>
        internal const int MAX_STATIC_TRIANGLES = 200000;
        private const int MAX_STATIC_CACHE = 12;
        private const int MAX_COLLISION_CACHE = 4000;

        // The body band over a floor: the dungeon map's ClearWallLow (0.45 m) up to the top of AC's human
        // (setup 0x02000001: spheres at 0.475 and 1.35 m, radius 0.48, so 1.83 m).
        private const float BodyBandLow = 0.45f, BodyBandHigh = 1.83f;

        // A setup's (or GfxObj's) collision in its own space, as AC picks it.
        private sealed class SetupCollision
        {
            public StaticCollisionKind Kind;
            public Vector3[] Triangles = Array.Empty<Vector3>();
            public CollisionCylinder[] Cylinders = Array.Empty<CollisionCylinder>();
            public CollisionSphere[] Spheres = Array.Empty<CollisionSphere>();
            public int TriangleCount;      // physics triangles found (before MAX_STATIC_TRIANGLES)
            public bool Capped;
        }

        private readonly Dictionary<uint, SetupCollision> _collisionCache = new Dictionary<uint, SetupCollision>();
        private readonly Dictionary<uint, StaticCollision[]> _staticCache = new Dictionary<uint, StaticCollision[]>();
        private readonly List<uint> _staticOrder = new List<uint>();

        /// <summary>
        /// Every placed static of <paramref name="landblockKey"/> that AC collides with, in world metres: each
        /// EnvCell's stabs (a web, an altar, a pillar, a table), and the landblock's own objects (0xXXYYFFFE,
        /// not its buildings) that stand over an EnvCell's floor at body height (tagged with that cell).
        /// Built once per landblock and kept (two threads asking at once get the same list); the list and its
        /// meshes are shared, so callers read them and never change them.
        /// </summary>
        public IReadOnlyList<StaticCollision> GetStaticCollision(uint landblockKey)
        {
            lock (_cacheGate)
            {
                if (_staticCache.TryGetValue(landblockKey, out var have)) return have;
            }
            var built = BuildStaticCollision(landblockKey);
            lock (_cacheGate)
            {
                // Another thread built it meanwhile: everyone keeps the first one.
                if (_staticCache.TryGetValue(landblockKey, out var first)) return first;
                if (_staticCache.Count >= MAX_STATIC_CACHE && _staticOrder.Count > 0)
                {
                    _staticCache.Remove(_staticOrder[0]);
                    _staticOrder.RemoveAt(0);
                }
                _staticCache[landblockKey] = built;
                _staticOrder.Add(landblockKey);
            }
            return built;
        }

        /// <summary>
        /// The placed statics of a landblock (GetStaticCollision) as (model, cell, mesh), in world metres, triangle
        /// hits only: physics polygons, or each cylinder and sphere as a closed solid of its own. A stab's frame is in
        /// the landblock's space (its z is the floor's: DreamWeave Thread 0x5456's webs stand at z -24 on a -24
        /// floor). Objects AC doesn't collide with are left out. A new list each call over the shared meshes.
        /// </summary>
        public List<(uint SetupId, uint CellId, BoundingVolume Mesh)> GetDungeonStaticMeshes(uint landblockKey)
        {
            var all = GetStaticCollision(landblockKey);
            var list = new List<(uint, uint, BoundingVolume)>(all.Count);
            foreach (var s in all)
                foreach (var part in s.Parts) list.Add((s.SetupId, s.CellId, part));
            return list;
        }

        private StaticCollision[] BuildStaticCollision(uint landblockKey)
        {
            var list = new List<StaticCollision>();
            if (_cellDat == null || _portalDat == null) return list.ToArray();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            float gx = ((landblockKey >> 8) & 0xFF) * 192.0f, gy = (landblockKey & 0xFF) * 192.0f;
            int stabs = 0;
            foreach (var (cellId, setupId, frame) in ReadCellStabs(landblockKey))
            {
                stabs++;
                var c = GetSetupCollision(setupId);
                if (c.Kind == StaticCollisionKind.None) continue;
                list.Add(PlaceCollision(setupId, cellId, StaticSource.CellStab, c, frame, gx, gy));
            }
            int fromCells = list.Count;
            AddLandblockObjectsOnFloors(landblockKey, gx, gy, list);
            int poly = 0, cyl = 0, sph = 0;
            foreach (var s in list)
            {
                if (s.Kind == StaticCollisionKind.Polygons) poly++;
                else if (s.Kind == StaticCollisionKind.Cylinders) cyl++;
                else if (s.Kind == StaticCollisionKind.Spheres) sph++;
            }
            Log($"Statics 0x{landblockKey:X4}: {stabs} cell stabs, {fromCells} collide; {list.Count - fromCells} landblock objects over a cell's floor; " +
                $"{poly} polygons, {cyl} cylinders, {sph} spheres in all, {sw.Elapsed.TotalMilliseconds:F1} ms");
            return list.ToArray();
        }

        // Every EnvCell stab of the landblock: (cell, model, frame in the landblock's space).
        private List<(uint CellId, uint SetupId, Frame Frame)> ReadCellStabs(uint landblockKey)
        {
            var list = new List<(uint, uint, Frame)>();
            foreach (uint cellId in _cellDat.GetLandblockCellIds(landblockKey))
            {
                byte[] data = _cellDat.GetFileData(cellId);
                if (data == null || data.Length < 20) continue;
                try
                {
                    using var ms = new MemoryStream(data);
                    using var reader = new BinaryReader(ms);
                    reader.ReadUInt32();
                    uint flags = reader.ReadUInt32();
                    reader.ReadUInt32();
                    byte numSurfaces = reader.ReadByte();
                    byte numPortals = reader.ReadByte();
                    ushort numVisibleCells = reader.ReadUInt16();
                    for (int i = 0; i < numSurfaces; i++) reader.ReadUInt16();
                    reader.ReadUInt16(); reader.ReadUInt16();            // environment, cell structure
                    for (int i = 0; i < 7; i++) reader.ReadSingle();     // the cell's frame
                    for (int i = 0; i < numPortals; i++) reader.ReadUInt64();
                    for (int i = 0; i < numVisibleCells; i++) reader.ReadUInt16();
                    if ((flags & 0x02) == 0 || ms.Position + 4 > ms.Length) continue;
                    uint numStabs = Math.Min(reader.ReadUInt32(), 500u);
                    for (uint i = 0; i < numStabs && ms.Position + 32 <= ms.Length; i++)
                    {
                        uint setupId = reader.ReadUInt32();
                        var frame = new Frame();
                        frame.Unpack(reader);
                        list.Add((cellId, setupId, frame));
                    }
                }
                catch (Exception) { }
            }
            return list;
        }

        // The landblock's own objects (LandBlockInfo 0xXXYYFFFE's stab list; its buildings are not looked at)
        // that collide and stand over an EnvCell's floor with something in the body band above it: chairs
        // the outdoor list puts on a building's upper floor (0x7D64 at z 15.6, two at A9B4: 581 such objects
        // stand on building floors world-wide, 133 with physics). Each is filed under the cell whose floor
        // it overlaps most.
        private void AddLandblockObjectsOnFloors(uint landblockKey, float gx, float gy, List<StaticCollision> into)
        {
            byte[] info = _cellDat.GetFileData((landblockKey << 16) | 0xFFFE);
            if (info == null || info.Length < 12) return;
            uint n = Math.Min(BitConverter.ToUInt32(info, 8), 5000u);
            var placed = new List<StaticCollision>();
            using (var ms = new MemoryStream(info))
            using (var reader = new BinaryReader(ms))
            {
                ms.Position = 12;
                for (uint i = 0; i < n && ms.Position + 32 <= ms.Length; i++)
                {
                    uint setupId = reader.ReadUInt32();
                    var frame = new Frame();
                    frame.Unpack(reader);
                    if (float.IsNaN(frame.OriginX) || Math.Abs(frame.OriginX) > 50000) continue;
                    var c = GetSetupCollision(setupId);
                    if (c.Kind == StaticCollisionKind.None) continue;
                    placed.Add(PlaceCollision(setupId, 0, StaticSource.LandblockObject, c, frame, gx, gy));
                }
            }
            if (placed.Count == 0 || _dungeonLOS == null) return;
            var floors = _dungeonLOS.GetDungeonMapFloorPolygons(landblockKey);
            if (floors == null || floors.Count == 0) return;

            var boxes = new (float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ)[floors.Count];
            for (int f = 0; f < floors.Count; f++)
            {
                float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
                foreach (var p in floors[f].Vertices)
                {
                    x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                    x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
                }
                boxes[f] = (x0, y0, z0, x1, y1, z1);
            }
            foreach (var s in placed)
            {
                Vector3 mMin = s.Min, mMax = s.Max;
                uint bestCell = 0;
                double bestArea = 0.01;   // m2: a sliver along a wall's foot doesn't count
                for (int f = 0; f < floors.Count; f++)
                {
                    var b = boxes[f];
                    if (b.MaxX < mMin.X || b.MinX > mMax.X || b.MaxY < mMin.Y || b.MinY > mMax.Y) continue;
                    // Something of it in the body band over this floor (a fence on the ground under a floor
                    // 5 m up, or a beam over a walker's head, isn't in this cell's way).
                    if (mMax.Z <= b.MinZ + BodyBandLow || mMin.Z >= b.MaxZ + BodyBandHigh) continue;
                    double area = ClippedArea(floors[f].Vertices, mMin.X, mMin.Y, mMax.X, mMax.Y);
                    if (area > bestArea) { bestArea = area; bestCell = floors[f].CellId; }
                }
                if (bestCell == 0) continue;
                s.CellId = bestCell;
                into.Add(s);
            }
        }

        // The area of a polygon (its x, y) inside the box [x0, x1] x [y0, y1]: Sutherland-Hodgman against
        // the box's four sides (right for a concave floor too, as the box is convex).
        internal static double ClippedArea(Vector3[] poly, double x0, double y0, double x1, double y1)
        {
            var pts = new List<(double X, double Y)>(poly.Length);
            foreach (var p in poly) pts.Add((p.X, p.Y));
            for (int side = 0; side < 4 && pts.Count >= 3; side++)
            {
                var outPts = new List<(double X, double Y)>(pts.Count + 4);
                for (int i = 0; i < pts.Count; i++)
                {
                    var cur = pts[i];
                    var prev = pts[(i + pts.Count - 1) % pts.Count];
                    bool ci = InsideSide(side, cur, x0, y0, x1, y1), pi = InsideSide(side, prev, x0, y0, x1, y1);
                    if (ci)
                    {
                        if (!pi) outPts.Add(CrossSide(side, prev, cur, x0, y0, x1, y1));
                        outPts.Add(cur);
                    }
                    else if (pi) outPts.Add(CrossSide(side, prev, cur, x0, y0, x1, y1));
                }
                pts = outPts;
            }
            if (pts.Count < 3) return 0;
            double a2 = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                var q = pts[(i + 1) % pts.Count];
                a2 += p.X * q.Y - q.X * p.Y;
            }
            return Math.Abs(a2) / 2;
        }

        private static bool InsideSide(int side, (double X, double Y) q, double x0, double y0, double x1, double y1) =>
            side switch { 0 => q.X >= x0, 1 => q.X <= x1, 2 => q.Y >= y0, _ => q.Y <= y1 };

        private static (double X, double Y) CrossSide(int side, (double X, double Y) a, (double X, double Y) b, double x0, double y0, double x1, double y1)
        {
            double t = side switch
            {
                0 => (x0 - a.X) / (b.X - a.X),
                1 => (x1 - a.X) / (b.X - a.X),
                2 => (y0 - a.Y) / (b.Y - a.Y),
                _ => (y1 - a.Y) / (b.Y - a.Y),
            };
            return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        // A setup, read once and kept (shared, never changed). Null when portal.dat has no such file or
        // even the fallback parse found no parts.
        private SetupInfo GetSetupInfo(uint modelId)
        {
            lock (_cacheGate)
            {
                if (_setupCache.TryGetValue(modelId, out var have)) return have;
            }
            byte[] data = _portalDat.GetFileData(modelId);
            if (data == null) return null;
            var setup = new SetupInfo();
            setup.Unpack(data);
            if (setup.PartIds == null) return null;
            lock (_cacheGate)
            {
                if (_setupCache.Count >= MAX_SETUP_CACHE) _setupCache.Clear();
                _setupCache[modelId] = setup;
            }
            return setup;
        }

        private SetupCollision GetSetupCollision(uint modelId)
        {
            lock (_cacheGate)
            {
                if (_collisionCache.TryGetValue(modelId, out var have)) return have;
            }
            var c = BuildSetupCollision(modelId);
            lock (_cacheGate)
            {
                if (_collisionCache.Count >= MAX_COLLISION_CACHE) _collisionCache.Clear();
                _collisionCache[modelId] = c;
            }
            return c;
        }

        private SetupCollision BuildSetupCollision(uint modelId)
        {
            var c = new SetupCollision();
            var tris = new List<Vector3>();
            uint kind = modelId & 0xFF000000;
            if (kind == 0x01000000)
            {
                // A GfxObj placed on its own: one part at the object's origin, size 1. Cylinders and spheres
                // belong to setups, so with no physics polygons AC has nothing to collide with. (This fell back
                // to a box under 3 triangles or on a failed parse, and the box was then dropped; a one- or
                // two-triangle panel is kept now.)
                var mesh = LoadGfxObjMesh(modelId);
                if (mesh != null) c.TriangleCount += AddFaces(mesh, null, 0, tris, ref c.Capped);
            }
            else if (kind == 0x02000000)
            {
                var setup = GetSetupInfo(modelId);
                if (setup == null) return c;
                for (int i = 0; i < setup.PartIds.Length; i++)
                {
                    var mesh = LoadGfxObjMesh(setup.PartIds[i]);
                    if (mesh != null) c.TriangleCount += AddFaces(mesh, setup, i, tris, ref c.Capped);
                }
                if (tris.Count == 0)
                {
                    c.Cylinders = setup.Cylinders.ToArray();
                    c.Spheres = setup.Spheres.ToArray();
                }
            }
            if (tris.Count >= 3)
            {
                c.Kind = StaticCollisionKind.Polygons;
                c.Triangles = tris.ToArray();
                c.Cylinders = Array.Empty<CollisionCylinder>();
                c.Spheres = Array.Empty<CollisionSphere>();
            }
            else if (c.Cylinders.Length > 0)
            {
                c.Kind = StaticCollisionKind.Cylinders;
                c.Spheres = Array.Empty<CollisionSphere>();
            }
            else if (c.Spheres.Length > 0) c.Kind = StaticCollisionKind.Spheres;
            if (c.Capped) Log($"[STATIC] 0x{modelId:X8}: {c.TriangleCount} physics triangles, kept {MAX_STATIC_TRIANGLES}");
            return c;
        }

        // One part's physics polygons as triangles in its setup's space (no setup: as they are). Returns
        // how many triangles the part has, kept or not.
        private static int AddFaces(GfxObjMeshData mesh, SetupInfo setup, int part, List<Vector3> into, ref bool capped)
        {
            int count = 0;
            var identity = new Frame();
            foreach (var face in mesh.Faces)
            {
                if (into.Count / 3 >= MAX_STATIC_TRIANGLES)
                {
                    capped = true;
                    count += Math.Max(0, face.Length - 2);
                    continue;
                }
                count += setup != null ? TriangulateAndTransform(face, mesh.Vertices, setup, part, into)
                                       : TriangulateAndTransform(face, mesh.Vertices, identity, into);
            }
            return count;
        }

        // A setup's collision placed by a stab's frame, in world metres. Cylinders stay upright (AC moves
        // only a CylSphere's low point with the object: ACE CylSphere.IntersectsSphere). Each cylinder and
        // sphere is a closed piece of its own: where two of an object's spheres overlap (0x0200104E's pair,
        // 5,447 placed), one mesh of both would read the overlap as outside to an even-odd inside test, and
        // a ray only looks at the triangles of the pieces whose box it crosses.
        private static StaticCollision PlaceCollision(uint setupId, uint cellId, StaticSource source, SetupCollision c, Frame f, float gx, float gy)
        {
            Vector3 W(Vector3 local)
            {
                var w = f.TransformPoint(local);
                return new Vector3(w.X + gx, w.Y + gy, w.Z);
            }
            var parts = new List<BoundingVolume>();
            var cyls = Array.Empty<(Vector3 Low, float Radius, float Height)>();
            var sphs = Array.Empty<(Vector3 Center, float Radius)>();
            if (c.Kind == StaticCollisionKind.Polygons)
            {
                var tris = new Vector3[c.Triangles.Length];
                for (int i = 0; i < tris.Length; i++) tris[i] = W(c.Triangles[i]);
                parts.Add(MeshOf(tris));
            }
            else if (c.Kind == StaticCollisionKind.Cylinders)
            {
                cyls = new (Vector3, float, float)[c.Cylinders.Length];
                for (int i = 0; i < cyls.Length; i++)
                {
                    var low = W(c.Cylinders[i].BottomCenter);
                    cyls[i] = (low, c.Cylinders[i].Radius, c.Cylinders[i].Height);
                    var list = new List<Vector3>(SolidSlices * 12);
                    AppendCylinderSolid(list, low, c.Cylinders[i].Radius, c.Cylinders[i].Height);
                    if (list.Count > 0) parts.Add(RoundMesh(list));
                }
            }
            else if (c.Kind == StaticCollisionKind.Spheres)
            {
                sphs = new (Vector3, float)[c.Spheres.Length];
                for (int i = 0; i < sphs.Length; i++)
                {
                    var centre = W(c.Spheres[i].Center);
                    sphs[i] = (centre, c.Spheres[i].Radius);
                    var list = new List<Vector3>(SolidSlices * SphereStacks * 6);
                    AppendSphereSolid(list, centre, c.Spheres[i].Radius);
                    if (list.Count > 0) parts.Add(RoundMesh(list));
                }
            }
            if (parts.Count == 0) parts.Add(MeshOf(new[] { W(new Vector3(0, 0, 0)) }));
            return new StaticCollision(setupId, cellId, source, c.Kind, parts.ToArray(), cyls, sphs, c.TriangleCount, c.Capped);
        }

        // A cylinder's or a sphere's solid as a mesh, marked round: its ring faces are walkable at many heights, and the
        // building map's stair test took a 1 m ball chained to a dais for steps (8090012A's 0x0200035E: off the map).
        private static BoundingVolume RoundMesh(List<Vector3> tris)
        {
            var m = MeshOf(tris.ToArray());
            m.Round = true;
            return m;
        }

        // A world-space triangle list as a mesh volume: its box is the triangles' own, and only real triangle
        // hits count.
        internal static BoundingVolume MeshOf(Vector3[] tris)
        {
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            foreach (var v in tris)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            return new BoundingVolume
            {
                Type = BoundingVolume.VolumeType.TriangleMesh,
                Center = (min + max) * 0.5f,
                Dimensions = max - min,
                Min = min,
                Max = max,
                MeshTriangles = tris,
                IsDoor = false,
                NoAabbFallback = true
            };
        }

        /// <summary>For offline checks: which shapes AC collides with for a model, and how many physics
        /// triangles, cylinders and spheres it has (triangles before MAX_STATIC_TRIANGLES).</summary>
        internal StaticCollisionKind DescribeCollision(uint modelId, out int triangles, out int cylinders, out int spheres)
        {
            var c = GetSetupCollision(modelId);
            triangles = c.TriangleCount;
            cylinders = c.Cylinders.Length;
            spheres = c.Spheres.Length;
            return c.Kind;
        }

        /// <summary>For offline checks: a model's physics triangles in its own space, posed and scaled as AC
        /// places it (a copy; empty unless its kind is Polygons).</summary>
        internal Vector3[] SetupCollisionTriangles(uint modelId) => (Vector3[])GetSetupCollision(modelId).Triangles.Clone();

        // ── Closed solids for cylinders and spheres ────────────────────────────────────────────────────
        // Everything downstream cuts the triangles level (the map's walls) or casts rays at them, so a
        // CylSphere becomes a 12-sided prism over its height and a Sphere a 12-slice, 8-stack closed solid.
        // Their corners sit out by PolyAreaScale, so a level cut has the circle's area (its mid-edges 1.2 %
        // inside the radius, its corners 2.3 % outside); a sphere's rings are pushed out by SphereRingScale
        // so its side straddles the true surface (within 1 % of the radius both ways). A level cut through
        // the sphere up to 0.9 of the radius from its middle is within 1 % of the radius of the true circle
        // there (5 % at the very top and bottom). A sphere under SmallSphereRadius (a cup or a candle on a
        // table: 0x8804 has some 600 of 0.1 m) is 8 x 4, 48 triangles not 168, within 6 mm of its surface.
        internal const int SolidSlices = 12, SphereStacks = 8;
        internal const int SmallSphereSlices = 8, SmallSphereStacks = 4;
        internal const float SmallSphereRadius = 0.25f;
        internal static readonly double PolyAreaScale = AreaScale(SolidSlices);
        internal static readonly double SphereRingScale = RingScale(SphereStacks);

        // An n-gon's corner radius over the radius of the circle with its area.
        internal static double AreaScale(int n) => Math.Sqrt(Math.PI / (n / 2.0 * Math.Sin(2 * Math.PI / n)));
        // How far out a sphere's rings go so its side's chords straddle the true surface.
        internal static double RingScale(int stacks) => 2 / (1 + Math.Cos(Math.PI / (2 * stacks)));

        /// <summary>A closed upright prism round (low, radius, height), as triangles appended to <paramref name="into"/>.</summary>
        internal static void AppendCylinderSolid(List<Vector3> into, Vector3 low, float radius, float height)
        {
            if (!(radius > 0.001f) || !(height > 0.001f)) return;
            int n = SolidSlices;
            double r = radius * PolyAreaScale;
            var bot = new Vector3[n];
            var top = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                double a = 2 * Math.PI * k / n;
                float x = (float)(low.X + r * Math.Cos(a)), y = (float)(low.Y + r * Math.Sin(a));
                bot[k] = new Vector3(x, y, low.Z);
                top[k] = new Vector3(x, y, low.Z + height);
            }
            for (int k = 0; k < n; k++)
            {
                int k1 = (k + 1) % n;
                into.Add(bot[k]); into.Add(bot[k1]); into.Add(top[k1]);
                into.Add(bot[k]); into.Add(top[k1]); into.Add(top[k]);
            }
            for (int k = 1; k + 1 < n; k++)
            {
                into.Add(bot[0]); into.Add(bot[k + 1]); into.Add(bot[k]);
                into.Add(top[0]); into.Add(top[k]); into.Add(top[k + 1]);
            }
        }

        /// <summary>A closed solid round the sphere (centre, radius), as triangles appended to <paramref name="into"/>.</summary>
        internal static void AppendSphereSolid(List<Vector3> into, Vector3 centre, float radius)
        {
            if (!(radius > 0.001f)) return;
            bool small = radius < SmallSphereRadius;
            int n = small ? SmallSphereSlices : SolidSlices, stacks = small ? SmallSphereStacks : SphereStacks;
            double rr = radius * RingScale(stacks), areaScale = AreaScale(n);
            var rings = new Vector3[stacks - 1][];
            for (int j = 1; j < stacks; j++)
            {
                double phi = -Math.PI / 2 + Math.PI * j / stacks;
                double rho = rr * Math.Cos(phi) * areaScale;
                float z = (float)(centre.Z + rr * Math.Sin(phi));
                var ring = new Vector3[n];
                for (int k = 0; k < n; k++)
                {
                    double a = 2 * Math.PI * k / n;
                    ring[k] = new Vector3((float)(centre.X + rho * Math.Cos(a)), (float)(centre.Y + rho * Math.Sin(a)), z);
                }
                rings[j - 1] = ring;
            }
            var south = new Vector3(centre.X, centre.Y, (float)(centre.Z - rr));
            var north = new Vector3(centre.X, centre.Y, (float)(centre.Z + rr));
            for (int k = 0; k < n; k++)
            {
                int k1 = (k + 1) % n;
                into.Add(south); into.Add(rings[0][k1]); into.Add(rings[0][k]);
                for (int j = 0; j + 1 < rings.Length; j++)
                {
                    var lo = rings[j];
                    var hi = rings[j + 1];
                    into.Add(lo[k]); into.Add(lo[k1]); into.Add(hi[k1]);
                    into.Add(lo[k]); into.Add(hi[k1]); into.Add(hi[k]);
                }
                into.Add(north); into.Add(rings[rings.Length - 1][k]); into.Add(rings[rings.Length - 1][k1]);
            }
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
                        IsDoor = localVol.IsDoor,
                        NoAabbFallback = localVol.NoAabbFallback   // a collision mesh (MeshOf): its triangles are all of it
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
            lock (_cacheGate)
            {
                _landblockCache.Clear();
                _setupCache.Clear();
                _collisionCache.Clear();
                _staticCache.Clear();
                _staticOrder.Clear();
            }
            Log("All caches flushed");
        }

        public void FlushLandblock(uint landblockKey)
        {
            lock (_cacheGate)
            {
                _landblockCache.Remove(landblockKey);
                _landblockCache.Remove(landblockKey | 0x80000000u);
                _staticCache.Remove(landblockKey);
                _staticOrder.Remove(landblockKey);
            }
        }

        public void Dispose()
        {
            _portalDat?.Dispose();
            _cellDat?.Dispose();
            lock (_cacheGate)
            {
                _landblockCache.Clear();
                _setupCache.Clear();
                _collisionCache.Clear();
                _staticCache.Clear();
                _staticOrder.Clear();
            }
            _initialized = false;
        }

        private void Log(string msg)
        {
            string line = $"[GeoLoader] {msg}";
            System.Diagnostics.Debug.WriteLine(line);
            RynthLog.Trace(LogCat.Raycast, line); // visible via /ra trace raycast
            // Statics can be built on more than one thread (the tick's map, a UI inspect).
            lock (DiagLog)
            {
                if (DiagLog.Count < 200) DiagLog.Add(msg);
            }
        }
    }

    /// <summary>Which of a placed object's collision shapes AC collides with (ACE PhysicsObj.FindObjCollisions):
    /// its parts' physics polygons when any part has some, else its CylSpheres, else its Spheres, else none.</summary>
    public enum StaticCollisionKind { None = 0, Polygons = 1, Cylinders = 2, Spheres = 3 }

    /// <summary>Where a placed static comes from.</summary>
    public enum StaticSource
    {
        /// <summary>An EnvCell's stab list (a dungeon's or a building's cells).</summary>
        CellStab = 0,
        /// <summary>The landblock's own object list (LandBlockInfo 0xXXYYFFFE), standing over a cell's floor.</summary>
        LandblockObject = 1,
    }

    /// <summary>One placed static object as AC collides with it, in world metres (GeometryLoader.GetStaticCollision).
    /// Shared between callers: read it, never change it.</summary>
    public sealed class StaticCollision
    {
        private BoundingVolume _union;

        internal StaticCollision(uint setupId, uint cellId, StaticSource source, StaticCollisionKind kind, BoundingVolume[] parts,
                                 IReadOnlyList<(Vector3 Low, float Radius, float Height)> cylinders, IReadOnlyList<(Vector3 Center, float Radius)> spheres,
                                 int triangleCount, bool capped)
        {
            SetupId = setupId;
            CellId = cellId;
            Source = source;
            Kind = kind;
            Parts = parts;
            Cylinders = cylinders;
            Spheres = spheres;
            PhysicsTriangles = triangleCount;
            Capped = capped;
            var min = parts[0].Min;
            var max = parts[0].Max;
            foreach (var p in parts)
            {
                min = Vector3.Min(min, p.Min);
                max = Vector3.Max(max, p.Max);
            }
            Min = min;
            Max = max;
        }

        /// <summary>The model (a 0x02 setup or a 0x01 GfxObj).</summary>
        public uint SetupId { get; }
        /// <summary>The EnvCell it stands in: its stab's cell, or for a landblock object the cell whose floor it is over most.</summary>
        public uint CellId { get; internal set; }
        public StaticSource Source { get; }
        public StaticCollisionKind Kind { get; }
        /// <summary>Its closed pieces as triangle meshes, world metres, triangle hits only: the physics polygons as one,
        /// or each cylinder and sphere as a solid of its own.</summary>
        public IReadOnlyList<BoundingVolume> Parts { get; }
        /// <summary>The box round all its pieces.</summary>
        public Vector3 Min { get; }
        public Vector3 Max { get; }
        /// <summary>All of it as one mesh (its only piece, or the pieces' triangles together, made when first asked).</summary>
        public BoundingVolume Mesh
        {
            get
            {
                if (Parts.Count == 1) return Parts[0];
                var u = System.Threading.Volatile.Read(ref _union);
                if (u != null) return u;
                var all = new List<Vector3>();
                foreach (var p in Parts) all.AddRange(p.MeshTriangles);
                u = GeometryLoader.MeshOf(all.ToArray());
                return System.Threading.Interlocked.CompareExchange(ref _union, u, null) ?? u;
            }
        }
        /// <summary>Kind Cylinders: each upright cylinder, its low point in world metres.</summary>
        public IReadOnlyList<(Vector3 Low, float Radius, float Height)> Cylinders { get; }
        /// <summary>Kind Spheres: each sphere, its centre in world metres.</summary>
        public IReadOnlyList<(Vector3 Center, float Radius)> Spheres { get; }
        /// <summary>Kind Polygons: how many physics triangles the model has.</summary>
        public int PhysicsTriangles { get; }
        /// <summary>The model has more than GeometryLoader.MAX_STATIC_TRIANGLES physics triangles (not all kept).</summary>
        public bool Capped { get; }
    }
}

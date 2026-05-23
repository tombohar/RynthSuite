using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.TerrainData;

/// <summary>
/// Self-contained terrain-data provider for AC landblocks: opens the client's
/// portal/cell .dat files, parses the 256-entry land-height table from RegionDesc,
/// and exposes per-landblock heights, slope passability, and terrain-type/water
/// lookups. Reuses the shared DatDatabase/LandblockData parsers; the slope math
/// and height-table prefix parse are reimplemented here so this carries no
/// dependency on RynthAi's raycasting engine.
/// </summary>
public sealed class TerrainSampler : IDisposable
{
    // AC's walkable surface threshold is normal.Z >= 0.664 (cos 48°), but the
    // coarse 9x9 terrain mesh over-marks gentle slopes, so 0.5 (cos 60°) cuts
    // false positives. Matches RynthAi's ScatterSystem.FloorZ.
    public const float FloorZ = 0.5f;

    private const float CellLength = 24.0f;

    private readonly DatDatabase _portalDat = new();
    private readonly DatDatabase _cellDat = new();
    private float[]? _landHeightTable;
    private bool _ready;

    private readonly Dictionary<uint, LandblockData?> _cache = new();
    private const int MaxCache = 30;

    public bool IsReady => _ready;
    public string Status { get; private set; } = "uninitialized";

    /// <summary>
    /// Opens the AC .dat files and loads the land-height table. Idempotent. When
    /// acFolderPath is null, uses the running process's directory — inside an
    /// injected plugin that's acclient.exe's folder, where the active client's
    /// .dat files live.
    /// </summary>
    public bool Initialize(string? acFolderPath = null)
    {
        if (_ready) return true;
        try
        {
            string? folder = acFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                string? exe = Environment.ProcessPath;
                folder = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
            }
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Status = $"AC folder not found ({folder ?? "null"})";
                return false;
            }

            string? portalPath = FindDat(folder, "client_portal.dat", "portal.dat");
            string? cellPath = FindDat(folder, "client_cell_1.dat", "cell.dat");
            if (portalPath == null || cellPath == null)
            {
                Status = $"portal/cell .dat not found in {folder}";
                return false;
            }

            if (!_portalDat.Open(portalPath) || !_cellDat.Open(cellPath))
            {
                Status = "failed to open .dat files";
                return false;
            }

            if (!LoadLandHeightTable())
            {
                Status = "RegionDesc land-height table parse failed";
                return false;
            }

            _ready = true;
            Status = "ready";
            return true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return false;
        }
    }

    /// <summary>Parsed landblock (heights + terrain words), cached. Null if unavailable.</summary>
    public LandblockData? LoadLandblock(uint landblockKey)
    {
        if (!_ready || _landHeightTable == null) return null;
        if (_cache.TryGetValue(landblockKey, out var cached)) return cached;

        LandblockData? data = LandblockData.Load(_cellDat, landblockKey, _landHeightTable);
        if (_cache.Count >= MaxCache) _cache.Clear();
        _cache[landblockKey] = data;
        return data;
    }

    /// <summary>
    /// Per-triangle walkability for cell (cx,cy), 0–7. Triangle 1 = SE half,
    /// triangle 2 = NW half. Mirrors RynthAi's ScatterSystem.GetTrianglePassability.
    /// </summary>
    public static void GetTrianglePassability(LandblockData lb, int cx, int cy,
        out bool tri1Passable, out bool tri2Passable)
    {
        float h00 = lb.GetVertexZ(cx,     cy);
        float h10 = lb.GetVertexZ(cx + 1, cy);
        float h01 = lb.GetVertexZ(cx,     cy + 1);
        float h11 = lb.GetVertexZ(cx + 1, cy + 1);

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
    /// AC terrain-type index (0–31) at vertex (ix,iy), 0–8. Decoded from the
    /// CellLandblock terrain word: bits 0–1 road, 2–10 terrain type, 11+ scene.
    /// Returns -1 if unavailable.
    /// </summary>
    public static int GetTerrainType(LandblockData lb, int ix, int iy)
    {
        var words = lb.TerrainWords;
        if (words == null) return -1;
        int idx = ix * 9 + iy;
        if ((uint)idx >= 81) return -1;
        return (words[idx] >> 2) & 0x1F;
    }

    private static string? FindDat(string folder, params string[] names)
    {
        foreach (var n in names)
        {
            string p = Path.Combine(folder, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private bool LoadLandHeightTable()
    {
        byte[]? data = _portalDat.GetFileData(0x13000000);
        // 12 (header) + >=2 (PString) + 32 (LandDefs prefix) + 256*4 (table)
        if (data == null || data.Length < 12 + 2 + 32 + 256 * 4) return false;

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        r.ReadUInt32(); // id 0x13000000
        r.ReadUInt32(); // regionNumber
        r.ReadUInt32(); // version
        SkipPString(r, ms); // RegionName

        // LandDefs prefix (8 scalar fields) precedes the height table.
        r.ReadInt32();  // NumBlockLength
        r.ReadInt32();  // NumBlockWidth
        r.ReadSingle(); // SquareLength
        r.ReadInt32();  // LBlockLength
        r.ReadInt32();  // VertexPerCell
        r.ReadSingle(); // MaxObjHeight
        r.ReadSingle(); // SkyHeight
        r.ReadSingle(); // RoadWidth

        var table = new float[256];
        for (int i = 0; i < 256; i++) table[i] = r.ReadSingle();
        _landHeightTable = table;
        return true;
    }

    // PString: ushort length, that many bytes, padded to a 4-byte boundary.
    private static void SkipPString(BinaryReader reader, MemoryStream ms)
    {
        ushort len = reader.ReadUInt16();
        if (len > 0 && ms.Position + len <= ms.Length)
            ms.Seek(len, SeekOrigin.Current);
        long aligned = (ms.Position + 3) & ~3L;
        if (aligned <= ms.Length) ms.Position = aligned;
    }

    public void Dispose()
    {
        _portalDat.Dispose();
        _cellDat.Dispose();
        _ready = false;
    }
}

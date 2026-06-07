using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthAi.Raycasting; // DatDatabase lives in the RynthCore.TerrainData assembly under this namespace

namespace RynthCore.Plugin.RynthJuice;

/// <summary>
/// Converts an INDOOR (dungeon) object's CELL-LOCAL origin into LANDBLOCK-RELATIVE
/// coordinates by reading the object's EnvCell transform (position + rotation)
/// from the client's cell.dat. The engine builds its view matrix in landblock
/// space even indoors (GameMatrixCapture reads AC's pre-composed viewer pose), so
/// once a mob's position is landblock-relative the existing WorldToScreen pipeline
/// projects it correctly inside dungeons.
///
/// Uses the shared TerrainData <see cref="DatDatabase"/> (the same parser
/// RynthVision uses) — this does NOT pull in RynthAi. Outdoor cells (low word
/// &lt; 0x100) pass through unchanged. cell.dat is opened lazily on first indoor
/// use, from the running client's own folder, and per-cell transforms are cached.
/// </summary>
internal sealed class EnvCellTransforms : IDisposable
{
    private DatDatabase? _cellDat;
    private bool _tried;
    private bool _ready;
    private readonly Dictionary<uint, Cell> _cache = new();

    private struct Cell
    {
        public bool Ok;
        public float Px, Py, Pz;     // cell position within the landblock
        public float Rw, Rx, Ry, Rz; // cell rotation quaternion
    }

    public bool Ready => _ready;
    public string Status { get; private set; } = "uninitialized";

    /// <summary>
    /// If <paramref name="cellId"/> is indoor, rewrites (e,n,u) from cell-local to
    /// landblock-relative and returns true; returns false if the transform can't be
    /// resolved (no cell.dat / no record). Outdoor cells return true unchanged.
    /// </summary>
    public bool ToLandblockRelative(uint cellId, ref float e, ref float n, ref float u)
    {
        if ((cellId & 0xFFFF) < 0x100)
            return true; // outdoor: origin is already landblock-local

        if (!EnsureOpen())
            return false;
        if (!TryGetCell(cellId, out Cell c) || !c.Ok)
            return false;

        // world_landblock = rotate(local, quat) + cellPos  (matches DungeonLOS.TransformVertex)
        float lx = e, ly = n, lz = u;
        float cx = c.Ry * lz - c.Rz * ly;
        float cy = c.Rz * lx - c.Rx * lz;
        float cz = c.Rx * ly - c.Ry * lx;
        float cx2 = c.Ry * cz - c.Rz * cy;
        float cy2 = c.Rz * cx - c.Rx * cz;
        float cz2 = c.Rx * cy - c.Ry * cx;
        e = lx + 2f * (c.Rw * cx + cx2) + c.Px;
        n = ly + 2f * (c.Rw * cy + cy2) + c.Py;
        u = lz + 2f * (c.Rw * cz + cz2) + c.Pz;
        return true;
    }

    private bool EnsureOpen()
    {
        if (_tried) return _ready;
        _tried = true;

        // The running client's own folder may hold cell.dat with an exclusive lock
        // (AC opened it first, before our share-hook, when launched by another tool).
        // cell.dat is identical across installs, so fall back to any other AC copy
        // whose handle isn't locked. Surface the exact per-path failure for diag.
        var tried = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in CandidateFolders())
        {
            try
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                string? path = FindDat(folder, "client_cell_1.dat", "cell.dat");
                if (path == null) { tried.Add($"{folder}=no-dat"); continue; }
                if (!seen.Add(Path.GetFullPath(path))) continue; // don't retry the same file

                var db = new DatDatabase();
                if (db.Open(path))
                {
                    _cellDat = db;
                    _ready = true;
                    Status = $"ready: {path}";
                    return true;
                }
                string err = db.DiagLog.Count > 0 ? db.DiagLog[db.DiagLog.Count - 1] : "open=false";
                tried.Add($"{path} -> {err}");
                try { db.Dispose(); } catch { }
            }
            catch (Exception ex) { tried.Add($"{folder} -> {ex.Message}"); }
        }

        Status = "cell.dat open failed; tried: " + (tried.Count > 0 ? string.Join(" | ", tried) : "(no candidates)");
        return false;
    }

    // cell.dat search locations, in priority order: the running client's folder
    // first, then well-known AC installs. Identical data in each, so any that
    // opens works.
    private static IEnumerable<string> CandidateFolders()
    {
        string? exe = Environment.ProcessPath;
        string? exeDir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
        if (!string.IsNullOrEmpty(exeDir)) yield return exeDir!;
        yield return @"C:\Games\RynthCore\AcClient";
        yield return @"C:\Turbine\Asheron's Call";
        yield return @"C:\Games\Asheron's Call";
        yield return @"C:\Program Files (x86)\Turbine\Asheron's Call";
        yield return @"C:\Program Files\Turbine\Asheron's Call";
    }

    private bool TryGetCell(uint cellId, out Cell c)
    {
        if (_cache.TryGetValue(cellId, out c))
            return true;
        c = default;
        try
        {
            byte[]? data = _cellDat!.GetFileData(cellId);
            if (data != null && data.Length >= 0x30)
                ParseEnvCell(data, ref c);
        }
        catch { c.Ok = false; }
        _cache[cellId] = c; // cache failures too (avoid re-reading missing cells)
        return true;
    }

    // EnvCell header (matches RynthAi DungeonLOS.ParseEnvCellHeader exactly):
    //   u32 id, u32 flags, u32 cellId, u8 numSurfaces, u8 numPortals,
    //   u16 numVisibleCells, [numSurfaces × u16], u16 envId, u16 cellStruct,
    //   f32 PosX, PosY, PosZ, RotW, RotX, RotY, RotZ
    private static void ParseEnvCell(byte[] data, ref Cell c)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
            byte numSurfaces = r.ReadByte();
            r.ReadByte();    // numPortals
            r.ReadUInt16();  // numVisibleCells
            ms.Seek(numSurfaces * 2, SeekOrigin.Current);
            r.ReadUInt16();  // envId
            r.ReadUInt16();  // cellStruct
            c.Px = r.ReadSingle(); c.Py = r.ReadSingle(); c.Pz = r.ReadSingle();
            c.Rw = r.ReadSingle(); c.Rx = r.ReadSingle(); c.Ry = r.ReadSingle(); c.Rz = r.ReadSingle();
            c.Ok = true;
        }
        catch { c.Ok = false; }
    }

    private static string? FindDat(string folder, params string[] names)
    {
        foreach (string nm in names)
        {
            string p = Path.Combine(folder, nm);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public void Dispose()
    {
        try { _cellDat?.Dispose(); } catch { }
        _cellDat = null;
        _ready = false;
    }
}

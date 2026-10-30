using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.Raycasting;

namespace RynthCore.Plugin.RynthAi.Maps;

/// <summary>
/// Writes each dungeon floor as a finished top-down raster to
/// C:\Games\RynthSuite\RynthAi\Maps\{landblock:X8}_{layer}.bin, which the RynthCore StatusAgent
/// serves at /map (re-encoded to PNG) for DrakRemote's dungeon map.
///
/// File format (must match the StatusAgent's MapService and the 81 plans baked before
/// 2026-09-28 by the old DungeonMapTexture): 20-byte header [uint32 Magic "XTMR" 0x524D5458,
/// int32 xMin, int32 yMin, int32 w, int32 h], then w*h little-endian ARGB uint32 pixels
/// (A&lt;&lt;24|R&lt;&lt;16|G&lt;&lt;8|B), 0.5 world units a pixel, row 0 = northernmost, 0 = empty.
/// xMin/yMin are grid-cell coordinates (world units * 2). The landblock in the file name is the
/// short form (cellId &gt;&gt; 16, e.g. 00006346_1.bin) and the layer is the index into the
/// landblock's ascending floor heights (DungeonMapUi.LayerZs), empty layers included.
///
/// This is the disk half of the old DungeonMapTexture only: no D3D9 textures. Each landblock
/// is baked at most once per session, on a pool thread, and a file that already holds the
/// same bytes is left alone (no mtime churn on the plans already there). Writes go to a temp
/// file that is then renamed over the target, so the agent never reads a half-written plan.
/// </summary>
internal static class DungeonMapBake
{
    internal const uint Magic = 0x524D5458u; // "XTMR" - same constant as the StatusAgent's MapService
    private const int MaxDim = 8192;         // the agent refuses bigger rasters

    /// <summary>Where plans go. A test points this at its own folder.</summary>
    internal static string CacheDir { get; set; } = System.IO.Path.Combine(RynthCore.Install.RynthInstallPaths.RynthAiDir, "Maps");

    // Pixel colours, as the old DungeonMapTexture (and DungeonMapUi's former ImGui colours).
    private static uint Argb(float r, float g, float b, float a) =>
        ((uint)(a * 255f) << 24) | ((uint)(r * 255f) << 16) | ((uint)(g * 255f) << 8) | (uint)(b * 255f);

    private static readonly uint FillFlat      = Argb(0.18f, 0.55f, 0.22f, 0.40f);
    private static readonly uint FillSlopeUp   = Argb(0.55f, 0.12f, 0.08f, 0.40f);
    private static readonly uint FillSlopeDown = Argb(0.10f, 0.50f, 0.15f, 0.40f);
    private static readonly uint EdgeFlat      = Argb(0.55f, 0.80f, 1.00f, 1.00f);
    private static readonly uint EdgeSlopeUp   = Argb(1.00f, 0.35f, 0.25f, 1.00f);
    private static readonly uint EdgeSlopeDown = Argb(0.25f, 1.00f, 0.35f, 1.00f);

    internal enum SaveResult { Written, Unchanged, Failed }

    // Landblocks already baked (or being baked) this session.
    private static readonly ConcurrentDictionary<uint, byte> _requested = new();
    // Messages from the pool thread, logged by the game-thread tick (DrainLog).
    private static readonly ConcurrentQueue<string> _log = new();

    /// <summary>Normalise to the short landblock id the file names use (0x63460000 -> 0x6346).</summary>
    internal static uint ShortLandblock(uint landblock) =>
        landblock > 0xFFFFu ? landblock >> 16 : landblock;

    internal static bool WasRequested(uint landblock) => _requested.ContainsKey(ShortLandblock(landblock));

    /// <summary>Messages queued by background bakes since the last call (game thread logs them).</summary>
    internal static void DrainLog(Action<string> log)
    {
        while (_log.TryDequeue(out string? m)) log(m);
    }

    /// <summary>
    /// Bake already-built floor grids (DungeonMapUi.RefreshMap) once per landblock. Returns at
    /// once; the rasterise and disk work run on a pool thread. The dictionaries must not be
    /// changed afterwards (RefreshMap builds new ones each time).
    /// </summary>
    internal static void Request(uint landblock, List<float>? zLayers,
        Dictionary<float, Dictionary<(int, int), DungeonMapUi.CellType>>? floorCells)
    {
        if (zLayers is null || floorCells is null || floorCells.Count == 0) return;
        uint lb = ShortLandblock(landblock);
        if (lb == 0 || !_requested.TryAdd(lb, 0)) return;
        Task.Run(() => BakeLogged(lb, zLayers, floorCells));
    }

    /// <summary>
    /// Build the floor grids from the dat and bake them, once per landblock, on a pool thread.
    /// For a landblock change indoors with no Radar or Dungeon Map panel open.
    /// </summary>
    internal static void RequestFromDat(DungeonLOS los, uint landblock)
    {
        uint lb = ShortLandblock(landblock);
        if (lb == 0 || !_requested.TryAdd(lb, 0)) return;
        Task.Run(() =>
        {
            try
            {
                var (zLayers, cells) = DungeonMapUi.BuildFloorCells(los, lb);
                if (cells.Count == 0) return; // no floor geometry (not a dungeon, or no dat data)
                BakeLogged(lb, zLayers, cells);
            }
            catch (Exception ex)
            {
                _requested.TryRemove(lb, out _);
                _log.Enqueue($"[RynthAi] map bake 0x{lb:X4}: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private static void BakeLogged(uint lb, List<float> zLayers,
        Dictionary<float, Dictionary<(int, int), DungeonMapUi.CellType>> floorCells)
    {
        try
        {
            var (written, unchanged, failed) = BakeLayers(CacheDir, lb, zLayers, floorCells);
            // A layer that could not be written (file held open, disk full): let the next
            // RefreshMap or re-entry try again instead of skipping it for the session.
            if (failed > 0) _requested.TryRemove(lb, out _);
            if (written > 0 || failed > 0)
                _log.Enqueue($"[RynthAi] map bake 0x{lb:X4}: {written} written, {unchanged} unchanged, {failed} failed ({CacheDir})");
        }
        catch (Exception ex)
        {
            _requested.TryRemove(lb, out _);
            _log.Enqueue($"[RynthAi] map bake 0x{lb:X4}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Bake every non-empty layer of a landblock into <paramref name="dir"/>. Synchronous.</summary>
    internal static (int Written, int Unchanged, int Failed) BakeLayers(string dir, uint landblock, List<float> zLayers,
        Dictionary<float, Dictionary<(int, int), DungeonMapUi.CellType>> floorCells)
    {
        uint lb = ShortLandblock(landblock);
        int written = 0, unchanged = 0, failed = 0;
        foreach (var (layerZ, cells) in floorCells)
        {
            int idx = zLayers.IndexOf(layerZ);
            if (idx < 0) continue;
            byte[]? bytes = BuildFile(cells);
            if (bytes is null) continue;
            switch (SaveToDisk(dir, lb, idx, bytes))
            {
                case SaveResult.Written:   written++;   break;
                case SaveResult.Unchanged: unchanged++; break;
                default:                   failed++;    break;
            }
        }
        return (written, unchanged, failed);
    }

    internal static string FileName(uint landblock, int layerIdx) => $"{ShortLandblock(landblock):X8}_{layerIdx}.bin";

    /// <summary>The whole .bin (header + pixels) for one layer's cells, or null if empty / too big.</summary>
    internal static byte[]? BuildFile(Dictionary<(int gx, int gy), DungeonMapUi.CellType> cells)
    {
        if (cells.Count == 0) return null;
        int xMin = int.MaxValue, xMax = int.MinValue;
        int yMin = int.MaxValue, yMax = int.MinValue;
        foreach (var (gx, gy) in cells.Keys)
        {
            if (gx < xMin) xMin = gx; if (gx > xMax) xMax = gx;
            if (gy < yMin) yMin = gy; if (gy > yMax) yMax = gy;
        }
        int w = xMax - xMin + 1, h = yMax - yMin + 1;
        if (w <= 0 || h <= 0 || w > MaxDim || h > MaxDim) return null;

        uint[] pixels = Rasterise(cells, xMin, yMax, w, h);
        var bytes = new byte[20 + pixels.Length * 4];
        var span = bytes.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(4), xMin);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), yMin);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(12), w);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16), h);
        for (int i = 0; i < pixels.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20 + i * 4), pixels[i]);
        return bytes;
    }

    /// <summary>
    /// One layer's cells as ARGB pixels: cells bordering empty space get the bright edge colour,
    /// the rest the translucent fill, by cell type; empty = 0. Row 0 = northernmost (highest gy).
    /// Unchanged from the old DungeonMapTexture.Rasterise.
    /// </summary>
    internal static uint[] Rasterise(Dictionary<(int gx, int gy), DungeonMapUi.CellType> cells,
        int xMin, int yMax, int w, int h)
    {
        var pixels = new uint[w * h];
        foreach (var ((gx, gy), ctype) in cells)
        {
            bool isEdge = !cells.ContainsKey((gx + 1, gy))
                       || !cells.ContainsKey((gx - 1, gy))
                       || !cells.ContainsKey((gx, gy + 1))
                       || !cells.ContainsKey((gx, gy - 1));
            uint col = isEdge
                ? ctype switch
                {
                    DungeonMapUi.CellType.SlopeUp   => EdgeSlopeUp,
                    DungeonMapUi.CellType.SlopeDown => EdgeSlopeDown,
                    _                               => EdgeFlat,
                }
                : ctype switch
                {
                    DungeonMapUi.CellType.SlopeUp   => FillSlopeUp,
                    DungeonMapUi.CellType.SlopeDown => FillSlopeDown,
                    _                               => FillFlat,
                };
            pixels[(yMax - gy) * w + (gx - xMin)] = col;
        }
        return pixels;
    }

    /// <summary>
    /// Write one plan unless the file already holds exactly these bytes. Temp file + rename, so a
    /// reader sees the old plan or the new one, never a partial one. Never throws.
    /// </summary>
    internal static SaveResult SaveToDisk(string dir, uint landblock, int layerIdx, byte[] bytes)
    {
        string path = Path.Combine(dir, FileName(landblock, layerIdx));
        string? tmp = null;
        try
        {
            if (SameContent(path, bytes)) return SaveResult.Unchanged;

            Directory.CreateDirectory(dir);
            // Unique per process and call: two clients in the same dungeon can bake at once.
            string tmpPath = path + "." + Environment.ProcessId.ToString("X") + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            tmp = tmpPath;
            using (var fs = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(bytes, 0, bytes.Length);

            // The agent may hold the target open for a moment (FileShare.ReadWrite, not Delete),
            // which makes the replace fail; try a few times.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmpPath, path, overwrite: true);
                    tmp = null;
                    return SaveResult.Written;
                }
                catch (IOException) when (attempt < 4)
                {
                    if (SameContent(path, bytes)) return SaveResult.Unchanged; // another client won
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch
        {
            return SaveResult.Failed;
        }
        finally
        {
            if (tmp != null) { try { File.Delete(tmp); } catch { } }
        }
    }

    private static bool SameContent(string path, byte[] bytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != bytes.Length) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[64 * 1024];
            int off = 0;
            while (off < bytes.Length)
            {
                int got = fs.Read(buf, 0, Math.Min(buf.Length, bytes.Length - off));
                if (got <= 0) return false;
                if (!buf.AsSpan(0, got).SequenceEqual(bytes.AsSpan(off, got))) return false;
                off += got;
            }
            return true;
        }
        catch { return false; }
    }
}

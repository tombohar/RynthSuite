using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi.Maps;

/// <summary>
/// One D3D9 POOL_MANAGED A8R8G8B8 texture baked from a single Z-layer's floor cells.
/// Each texel covers one GridCell (0.5 AC units). Edge cells (cells bordering empty space)
/// use the bright wall colour; interior cells use the semi-transparent fill colour;
/// empty cells are fully transparent (alpha=0).
///
/// Displayed via dl.AddImageQuad — O(1) vertices per layer vs O(strips + edges) otherwise.
/// Disk-cached to C:\Games\RynthSuite\RynthAi\Maps\{landblock:X8}_{layerIdx}.bin so
/// re-entering a dungeon skips the rasterise step.
/// </summary>
internal sealed class DungeonMapTexture : IDisposable
{
    private const float GridCell = 0.5f;
    private const string CacheDir = @"C:\Games\RynthSuite\RynthAi\Maps";
    private const uint   Magic    = 0x524D5458u; // "XTMR"

    // D3D9 vtable indices on IDirect3DDevice9 / IDirect3DTexture9
    private const int VtCreateTexture = 23;
    private const int VtLockRect      = 19;
    private const int VtUnlockRect    = 20;
    private const int VtRelease       = 2;

    private const uint D3DFMT_A8R8G8B8 = 21;
    private const uint D3DPOOL_MANAGED  = 1;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureD(IntPtr dev, uint w, uint h, uint levels, uint usage, uint fmt, uint pool, out IntPtr ppTex, IntPtr shared);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockRectD(IntPtr tex, uint level, ref D3DLockedRect locked, IntPtr pRect, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockRectD(IntPtr tex, uint level);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseD(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DLockedRect { public int Pitch; public IntPtr pBits; }

    // ── D3D9 A8R8G8B8 colour helpers (ARGB uint: A<<24|R<<16|G<<8|B) ─────
    private static uint Argb(float r, float g, float b, float a) =>
        ((uint)(a * 255f) << 24) | ((uint)(r * 255f) << 16) | ((uint)(g * 255f) << 8) | (uint)(b * 255f);

    // Match DungeonMapUi colour definitions
    private static readonly uint FillFlat      = Argb(0.18f, 0.55f, 0.22f, 0.40f);
    private static readonly uint FillSlopeUp   = Argb(0.55f, 0.12f, 0.08f, 0.40f);
    private static readonly uint FillSlopeDown = Argb(0.10f, 0.50f, 0.15f, 0.40f);
    private static readonly uint EdgeFlat      = Argb(0.55f, 0.80f, 1.00f, 1.00f);
    private static readonly uint EdgeSlopeUp   = Argb(1.00f, 0.35f, 0.25f, 1.00f);
    private static readonly uint EdgeSlopeDown = Argb(0.25f, 1.00f, 0.35f, 1.00f);

    // World bounds of the baked area
    public float WorldX0, WorldY0, WorldX1, WorldY1;
    public IntPtr TexId => _tex;

    private IntPtr _tex;

    private DungeonMapTexture() {}

    /// <summary>
    /// Returns a texture for the given floor-cell grid.  Checks the disk cache first;
    /// if absent, rasterises, uploads to D3D9, and saves to disk.
    /// Returns null if device is invalid or texture upload fails.
    /// </summary>
    public static DungeonMapTexture? GetOrBuild(
        IntPtr device,
        Dictionary<(int gx, int gy), DungeonMapUi.CellType> cells,
        uint landblock, int layerIdx)
    {
        if (device == IntPtr.Zero || cells.Count == 0) return null;

        // Bounding box
        int xMin = int.MaxValue, xMax = int.MinValue;
        int yMin = int.MaxValue, yMax = int.MinValue;
        foreach (var (gx, gy) in cells.Keys)
        {
            if (gx < xMin) xMin = gx; if (gx > xMax) xMax = gx;
            if (gy < yMin) yMin = gy; if (gy > yMax) yMax = gy;
        }
        int w = xMax - xMin + 1, h = yMax - yMin + 1;
        if (w <= 0 || h <= 0 || w > 8192 || h > 8192) return null;

        // Try disk cache first (only valid when dimensions match the dat-computed bounds)
        var cached = TryLoadFromDisk(device, landblock, layerIdx, xMin, yMin, w, h);
        if (cached != null) return cached;

        // Rasterise → upload → cache
        var pixels = Rasterise(cells, xMin, yMax, w, h);
        IntPtr tex = Upload(device, w, h, pixels);
        if (tex == IntPtr.Zero) return null;

        SaveToDisk(landblock, layerIdx, xMin, yMin, w, h, pixels);

        return new DungeonMapTexture
        {
            _tex    = tex,
            WorldX0 = xMin * GridCell,
            WorldY0 = yMin * GridCell,
            WorldX1 = (xMax + 1) * GridCell,
            WorldY1 = (yMax + 1) * GridCell,
        };
    }

    public void Dispose()
    {
        if (_tex == IntPtr.Zero) return;
        try { GetVtFunc<ReleaseD>(_tex, VtRelease)(_tex); } catch { }
        _tex = IntPtr.Zero;
    }

    // ── Rasterisation ─────────────────────────────────────────────────────

    private static uint[] Rasterise(
        Dictionary<(int gx, int gy), DungeonMapUi.CellType> cells,
        int xMin, int yMax, int w, int h)
    {
        var pixels = new uint[w * h]; // 0 = transparent
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
            // Row 0 = northernmost cells (highest gy), so flip Y
            pixels[(yMax - gy) * w + (gx - xMin)] = col;
        }
        return pixels;
    }

    // ── D3D9 upload ───────────────────────────────────────────────────────

    private static unsafe IntPtr Upload(IntPtr device, int w, int h, uint[] pixels)
    {
        try
        {
            int hr = GetVtFunc<CreateTextureD>(device, VtCreateTexture)(
                device, (uint)w, (uint)h, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, out IntPtr tex, IntPtr.Zero);
            if (hr < 0 || tex == IntPtr.Zero) return IntPtr.Zero;

            var locked = new D3DLockedRect();
            hr = GetVtFunc<LockRectD>(tex, VtLockRect)(tex, 0, ref locked, IntPtr.Zero, 0);
            if (hr < 0) { GetVtFunc<ReleaseD>(tex, VtRelease)(tex); return IntPtr.Zero; }

            // Copy row by row — pitch may be wider than w*4
            for (int row = 0; row < h; row++)
            {
                IntPtr dst = IntPtr.Add(locked.pBits, row * locked.Pitch);
                fixed (uint* src = &pixels[row * w])
                    Buffer.MemoryCopy(src, (void*)dst, (nuint)(w * 4), (nuint)(w * 4));
            }

            GetVtFunc<UnlockRectD>(tex, VtUnlockRect)(tex, 0);
            return tex;
        }
        catch { return IntPtr.Zero; }
    }

    // ── Disk cache ────────────────────────────────────────────────────────

    private static DungeonMapTexture? TryLoadFromDisk(
        IntPtr device, uint lb, int layerIdx,
        int expXMin, int expYMin, int expW, int expH)
    {
        try
        {
            string path = Path.Combine(CacheDir, $"{lb:X8}_{layerIdx}.bin");
            if (!File.Exists(path)) return null;
            using var br = new BinaryReader(File.OpenRead(path));
            if (br.ReadUInt32() != Magic) return null;
            int xMin = br.ReadInt32(), yMin = br.ReadInt32();
            int w = br.ReadInt32(), h = br.ReadInt32();
            // Invalidate if bounds no longer match the dat
            if (w != expW || h != expH || xMin != expXMin || yMin != expYMin) return null;
            if (br.BaseStream.Length < 20L + (long)w * h * 4) return null;

            var pixels = new uint[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = br.ReadUInt32();

            IntPtr tex = Upload(device, w, h, pixels);
            if (tex == IntPtr.Zero) return null;

            return new DungeonMapTexture
            {
                _tex    = tex,
                WorldX0 = xMin * GridCell,
                WorldY0 = yMin * GridCell,
                WorldX1 = (xMin + w) * GridCell,
                WorldY1 = (yMin + h) * GridCell,
            };
        }
        catch { return null; }
    }

    private static void SaveToDisk(uint lb, int layerIdx, int xMin, int yMin, int w, int h, uint[] pixels)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            string path = Path.Combine(CacheDir, $"{lb:X8}_{layerIdx}.bin");
            using var bw = new BinaryWriter(File.Open(path, FileMode.Create));
            bw.Write(Magic); bw.Write(xMin); bw.Write(yMin); bw.Write(w); bw.Write(h);
            foreach (uint px in pixels) bw.Write(px);
        }
        catch { }
    }

    // ── VTable helper ─────────────────────────────────────────────────────

    private static unsafe TDelegate GetVtFunc<TDelegate>(IntPtr obj, int idx) where TDelegate : Delegate
    {
        void** vt = *(void***)obj;
        return Marshal.GetDelegateForFunctionPointer<TDelegate>((IntPtr)vt[idx]);
    }
}

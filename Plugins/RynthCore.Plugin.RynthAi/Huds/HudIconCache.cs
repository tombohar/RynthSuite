// HudIconCache.cs â€” Item icons for the RynthAi HUD windows.
//
// An item's icon DID (PWD _iconID, 0x06xxxxxx) points at a Texture file in portal.dat.
// Decoding (dat read + pixel conversion) runs on the pump thread; the D3D9 texture is
// created lazily on the render thread, a few per frame, so a full pack never stalls a frame.
// Textures are POOL_MANAGED (they survive device resets) and are never released: plugin
// unload can run off the render thread, where touching the device is unsafe, and the
// whole set is a few hundred 32×32 icons.
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Huds;

internal sealed class HudIconCache
{
    // IDirect3DDevice9 / IDirect3DTexture9 vtable slots.
    private const int VtCreateTexture = 23;
    private const int VtLockRect = 19;
    private const int VtUnlockRect = 20;
    private const int VtRelease = 2;
    private const uint D3DFMT_A8R8G8B8 = 21;
    private const uint D3DPOOL_MANAGED = 1;

    /// <summary>Texture uploads allowed per rendered frame.</summary>
    private const int UploadsPerFrame = 6;
    /// <summary>Icons decoded per pump tick.</summary>
    private const int DecodesPerTick = 12;
    /// <summary>Runaway guard; a full character's icons are a few hundred.</summary>
    private const int MaxIcons = 4000;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTextureD(IntPtr dev, uint w, uint h, uint levels, uint usage, uint fmt, uint pool, out IntPtr tex, IntPtr shared);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LockRectD(IntPtr tex, uint level, ref D3DLockedRect locked, IntPtr rect, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int UnlockRectD(IntPtr tex, uint level);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseD(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DLockedRect { public int Pitch; public IntPtr Bits; }

    /// <summary>Decoded icon in D3D A8R8G8B8 order (A&lt;&lt;24 | R&lt;&lt;16 | G&lt;&lt;8 | B).</summary>
    private sealed class Decoded
    {
        public int Width;
        public int Height;
        public uint[] Pixels = Array.Empty<uint>();
    }

    private readonly RynthCoreHost _host;
    private readonly Func<DatDatabase?> _portal;

    // DID â†’ decoded pixels (null value = decode failed, don't retry).
    private readonly ConcurrentDictionary<uint, Decoded?> _decoded = new();
    // DIDs the render thread asked for that the pump thread hasn't decoded yet.
    private readonly ConcurrentQueue<uint> _pending = new();
    private readonly ConcurrentDictionary<uint, byte> _queued = new();

    // Render thread only.
    private readonly System.Collections.Generic.Dictionary<uint, IntPtr> _textures = new();
    private IntPtr _textureDevice;
    private int _uploadFrame = -1;
    private int _uploadsThisFrame;

    public HudIconCache(RynthCoreHost host, Func<DatDatabase?> portal)
    {
        _host = host;
        _portal = portal;
    }

    // â”€â”€ Render thread â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// ImGui texture id for <paramref name="iconDid"/>, or IntPtr.Zero while it is still
    /// being decoded / uploaded (callers draw a placeholder).
    /// </summary>
    public IntPtr GetTexture(uint iconDid)
    {
        if (iconDid == 0) return IntPtr.Zero;
        IntPtr device = _host.D3DDevice;
        if (device == IntPtr.Zero) return IntPtr.Zero;

        // A new device means the old textures belong to a dead device; forget them (not
        // Release â€” the owning device may already be gone).
        if (device != _textureDevice)
        {
            _textures.Clear();
            _textureDevice = device;
        }

        if (_textures.TryGetValue(iconDid, out IntPtr tex)) return tex;

        if (!_decoded.TryGetValue(iconDid, out Decoded? d))
        {
            if (_queued.TryAdd(iconDid, 0)) _pending.Enqueue(iconDid);
            return IntPtr.Zero;
        }
        if (d == null) return IntPtr.Zero;

        int frame = ImGuiNET.ImGui.GetFrameCount();
        if (frame != _uploadFrame) { _uploadFrame = frame; _uploadsThisFrame = 0; }
        if (_uploadsThisFrame >= UploadsPerFrame) return IntPtr.Zero;
        _uploadsThisFrame++;

        tex = Upload(device, d);
        // Failed uploads are remembered as Zero so they aren't retried every frame.
        _textures[iconDid] = tex;
        return tex;
    }

    // â”€â”€ Pump thread â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Decodes a batch of requested icons (dat reads stay off the render thread).</summary>
    public void Tick()
    {
        var portal = _portal();
        if (portal == null || !portal.IsLoaded) return;
        for (int i = 0; i < DecodesPerTick && _pending.TryDequeue(out uint did); i++)
        {
            _queued.TryRemove(did, out _);
            if (_decoded.Count >= MaxIcons) { _decoded.TryAdd(did, null); continue; }
            Decoded? d = null;
            try { d = Decode(portal, did); }
            catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, $"icon decode 0x{did:X8}"); }
            _decoded[did] = d;
        }
    }

    // â”€â”€ Decoding (Texture 0x06 â†’ ARGB) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    // Texture layout: Id(u32) Unknown(i32) Width(i32) Height(i32) Format(u32) Length(i32)
    // Source[Length], then DefaultPaletteId(u32) for INDEX16 / P8.
    private static Decoded? Decode(DatDatabase portal, uint textureId)
    {
        byte[]? data = portal.GetFileData(textureId);
        if (data == null || data.Length < 24) return null;
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadUInt32(); r.ReadInt32();
        int w = r.ReadInt32(), h = r.ReadInt32();
        uint fmt = r.ReadUInt32();
        int len = r.ReadInt32();
        if (w <= 0 || h <= 0 || w > 1024 || h > 1024 || len < 0 || ms.Position + len > ms.Length) return null;
        byte[] src = r.ReadBytes(len);
        uint palId = 0;
        if ((fmt == 101 || fmt == 41) && ms.Position + 4 <= ms.Length) palId = r.ReadUInt32();

        var px = new uint[w * h];
        static uint Argb(int a, int rr, int g, int b) => ((uint)a << 24) | ((uint)rr << 16) | ((uint)g << 8) | (uint)b;

        switch (fmt)
        {
            case 827611204: return FromRgba(DxtUtil.DecompressDxt1(src, w, h), w, h); // DXT1
            case 861165636: return FromRgba(DxtUtil.DecompressDxt3(src, w, h), w, h); // DXT3
            case 894720068: return FromRgba(DxtUtil.DecompressDxt5(src, w, h), w, h); // DXT5
            case 20: // R8G8B8, stored B,G,R
                for (int i = 0; i < px.Length && i * 3 + 2 < src.Length; i++) px[i] = Argb(255, src[i * 3 + 2], src[i * 3 + 1], src[i * 3]);
                break;
            case 21: // A8R8G8B8, stored B,G,R,A
            case 22: // X8R8G8B8
                for (int i = 0; i < px.Length && i * 4 + 3 < src.Length; i++)
                    px[i] = Argb(fmt == 22 ? 255 : src[i * 4 + 3], src[i * 4 + 2], src[i * 4 + 1], src[i * 4]);
                break;
            case 23: // R5G6B5
                for (int i = 0; i < px.Length && i * 2 + 1 < src.Length; i++)
                {
                    int v = src[i * 2] | (src[i * 2 + 1] << 8);
                    px[i] = Argb(255, ((v >> 11) & 0x1F) << 3, ((v >> 5) & 0x3F) << 2, (v & 0x1F) << 3);
                }
                break;
            case 26: // A4R4G4B4
                for (int i = 0; i < px.Length && i * 2 + 1 < src.Length; i++)
                {
                    int v = src[i * 2] | (src[i * 2 + 1] << 8);
                    px[i] = Argb(((v >> 12) & 0xF) * 17, ((v >> 8) & 0xF) * 17, ((v >> 4) & 0xF) * 17, (v & 0xF) * 17);
                }
                break;
            case 28: // A8 greyscale
                for (int i = 0; i < px.Length && i < src.Length; i++) px[i] = Argb(255, src[i], src[i], src[i]);
                break;
            case 101: // INDEX16
            case 41:  // P8
            {
                uint[]? pal = LoadPalette(portal, palId);
                if (pal == null || pal.Length == 0) return null;
                bool p8 = fmt == 41;
                for (int i = 0; i < px.Length; i++)
                {
                    int idx;
                    if (p8) { if (i >= src.Length) break; idx = src[i]; }
                    else { if (i * 2 + 1 >= src.Length) break; idx = src[i * 2] | (src[i * 2 + 1] << 8); }
                    px[i] = pal[idx % pal.Length]; // palette entries are already ARGB
                }
                break;
            }
            default:
                return null;
        }
        return new Decoded { Width = w, Height = h, Pixels = px };
    }

    /// <summary>Palette (0x04): Id(u32), Count(i32), Count Ã— ARGB(u32).</summary>
    private static uint[]? LoadPalette(DatDatabase portal, uint paletteId)
    {
        byte[]? data = portal.GetFileData(paletteId);
        if (data == null || data.Length < 8) return null;
        int n = BitConverter.ToInt32(data, 4);
        if (n <= 0 || n > 65536) return null;
        n = Math.Min(n, (data.Length - 8) / 4);
        var pal = new uint[n];
        for (int i = 0; i < n; i++) pal[i] = BitConverter.ToUInt32(data, 8 + i * 4);
        return pal;
    }

    /// <summary>DxtUtil output (R,G,B,A bytes) â†’ ARGB.</summary>
    private static Decoded FromRgba(byte[] rgba, int w, int h)
    {
        var px = new uint[w * h];
        for (int i = 0; i < px.Length && i * 4 + 3 < rgba.Length; i++)
            px[i] = ((uint)rgba[i * 4 + 3] << 24) | ((uint)rgba[i * 4] << 16) | ((uint)rgba[i * 4 + 1] << 8) | rgba[i * 4 + 2];
        return new Decoded { Width = w, Height = h, Pixels = px };
    }

    // â”€â”€ D3D9 upload (render thread) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static unsafe IntPtr Upload(IntPtr device, Decoded d)
    {
        try
        {
            int hr = Vt<CreateTextureD>(device, VtCreateTexture)(device, (uint)d.Width, (uint)d.Height, 1, 0,
                D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, out IntPtr tex, IntPtr.Zero);
            if (hr < 0 || tex == IntPtr.Zero) return IntPtr.Zero;

            var locked = new D3DLockedRect();
            if (Vt<LockRectD>(tex, VtLockRect)(tex, 0, ref locked, IntPtr.Zero, 0) < 0)
            {
                Vt<ReleaseD>(tex, VtRelease)(tex);
                return IntPtr.Zero;
            }
            // Row by row: the locked pitch can be wider than Width * 4.
            for (int row = 0; row < d.Height; row++)
            {
                fixed (uint* src = &d.Pixels[row * d.Width])
                    Buffer.MemoryCopy(src, (void*)IntPtr.Add(locked.Bits, row * locked.Pitch), d.Width * 4, d.Width * 4);
            }
            Vt<UnlockRectD>(tex, VtUnlockRect)(tex, 0);
            return tex;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.Huds, ex, "icon upload");
            return IntPtr.Zero;
        }
    }

    private static unsafe T Vt<T>(IntPtr obj, int index) where T : Delegate
    {
        void** vt = *(void***)obj;
        return Marshal.GetDelegateForFunctionPointer<T>((IntPtr)vt[index]);
    }
}

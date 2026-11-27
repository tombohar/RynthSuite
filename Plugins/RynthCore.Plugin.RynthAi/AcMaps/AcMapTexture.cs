// ============================================================================
//  RynthAi - AcMaps/AcMapTexture.cs
//  A decoded map image as D3D9 textures for ImGui, in Tile x Tile pieces
//  (power-of-two textures, so no device limit or non-power-of-two rule bites;
//  the edge pieces use part of theirs). Managed pool: they survive a device
//  reset. A few pieces are made a frame so a big map doesn't stall a frame.
//
//  Render thread only (the D3D device isn't made for other threads). A new
//  device means the old textures belong to a dead one: they are forgotten,
//  not released, as HudIconCache does. Once every piece is made the decoded
//  pixels are let go (the managed pool keeps its own copy; the client is
//  32-bit): a new device after that asks for the image again (NeedsReload).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace RynthCore.Plugin.RynthAi.AcMaps;

internal sealed unsafe class AcMapTexture
{
    public const int Tile = 512;
    private const int TilesPerFrame = 6;

    private const int VtRelease = 2, VtCreateTexture = 23, VtLockRect = 19, VtUnlockRect = 20;
    private const uint D3DFMT_A8R8G8B8 = 21, D3DPOOL_MANAGED = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LockedRect { public int Pitch; public IntPtr Bits; }

    /// <summary>One piece: its texture (Zero until made, or when making it failed), where it sits and its UV corner.</summary>
    internal struct Piece
    {
        public IntPtr Tex;
        public int X, Y, W, H;
        public Vector2 Uv1;
        public bool Tried;
    }

    private uint[]? _pixels;                  // until every piece is made
    private readonly int _width;
    private readonly Piece[] _pieces;
    private IntPtr _device;
    private int _made;

    public AcMapTexture(DecodedImage image)
    {
        _pixels = image.Pixels;
        _width = image.Width;
        SourceWidth = image.SourceWidth;
        SourceHeight = image.SourceHeight;
        Factor = image.Factor;
        var list = new List<Piece>();
        for (int y = 0; y < image.Height; y += Tile)
            for (int x = 0; x < image.Width; x += Tile)
            {
                int w = Math.Min(Tile, image.Width - x), h = Math.Min(Tile, image.Height - y);
                int tw = Pow2(w), th = Pow2(h);
                list.Add(new Piece { X = x, Y = y, W = w, H = h, Uv1 = new Vector2(w / (float)tw, h / (float)th) });
            }
        _pieces = list.ToArray();
    }

    /// <summary>The file's size in pixels (spots and the view are in these).</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    /// <summary>Decoded (piece) pixels per file pixel.</summary>
    public float Factor { get; }
    /// <summary>The device changed after the pixels were let go: decode the file again.</summary>
    public bool NeedsReload { get; private set; }
    public int PieceCount => _pieces.Length;
    /// <summary>Every piece has been made (or tried).</summary>
    public bool Complete => _made >= _pieces.Length;
    public ReadOnlySpan<Piece> Pieces => _pieces;

    /// <summary>Makes the next few pieces on <paramref name="device"/>. Render thread, once a frame while drawn.</summary>
    public void Upload(IntPtr device)
    {
        if (device == IntPtr.Zero) return;
        if (device != _device && _device != IntPtr.Zero && _pixels == null)
        {
            NeedsReload = true;
            return;
        }
        if (device != _device)
        {
            for (int i = 0; i < _pieces.Length; i++) { _pieces[i].Tex = IntPtr.Zero; _pieces[i].Tried = false; }
            _made = 0;
            _device = device;
        }
        int budget = TilesPerFrame;
        for (int i = 0; i < _pieces.Length && budget > 0; i++)
        {
            if (_pieces[i].Tried) continue;
            _pieces[i].Tried = true;
            _pieces[i].Tex = Make(device, ref _pieces[i]);
            _made++;
            budget--;
        }
        if (_made >= _pieces.Length) _pixels = null;
    }

    /// <summary>Releases every texture. Render thread.</summary>
    public void Release()
    {
        for (int i = 0; i < _pieces.Length; i++)
        {
            IntPtr t = _pieces[i].Tex;
            _pieces[i].Tex = IntPtr.Zero;
            _pieces[i].Tried = false;
            if (t == IntPtr.Zero || _device == IntPtr.Zero) continue;
            try { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(t, VtRelease))(t); } catch { }
        }
        _made = 0;
    }

    private IntPtr Make(IntPtr device, ref Piece p)
    {
        try
        {
            int tw = Pow2(p.W), th = Pow2(p.H);
            IntPtr tex;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, uint, IntPtr*, IntPtr, int>)Slot(device, VtCreateTexture))(
                device, (uint)tw, (uint)th, 1, 0, D3DFMT_A8R8G8B8, D3DPOOL_MANAGED, &tex, IntPtr.Zero);
            if (hr < 0 || tex == IntPtr.Zero) return IntPtr.Zero;
            LockedRect locked;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, LockedRect*, IntPtr, uint, int>)Slot(tex, VtLockRect))(tex, 0, &locked, IntPtr.Zero, 0);
            if (hr < 0)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(tex, VtRelease))(tex);
                return IntPtr.Zero;
            }
            uint[]? pixels = _pixels;
            if (pixels == null) return IntPtr.Zero;
            fixed (uint* src = pixels)
            {
                for (int row = 0; row < th; row++)
                {
                    uint* dst = (uint*)((byte*)locked.Bits + (long)row * locked.Pitch);
                    if (row < p.H)
                    {
                        Buffer.MemoryCopy(src + (long)(p.Y + row) * _width + p.X, dst, (long)tw * 4, (long)p.W * 4);
                        for (int x = p.W; x < tw; x++) dst[x] = 0;
                    }
                    else
                        for (int x = 0; x < tw; x++) dst[x] = 0;
                }
            }
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(tex, VtUnlockRect))(tex, 0);
            return tex;
        }
        catch { return IntPtr.Zero; }
    }

    internal static int Pow2(int v)
    {
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];
}

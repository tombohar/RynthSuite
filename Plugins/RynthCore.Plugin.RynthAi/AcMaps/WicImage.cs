// ============================================================================
//  RynthAi - AcMaps/WicImage.cs
//  Reads a map image (GIF, PNG, JPG, BMP) into 32-bit BGRA pixels with
//  Windows' own decoder (WIC, windowscodecs.dll): no image library to ship,
//  and no reflection (RynthAi is NativeAOT), so COM is called through its
//  vtables. The first frame only (ACMAPS GIFs are single-frame).
//
//  A map larger than MaxSide or MaxPixels is scaled down on the way in (the
//  client is 32-bit; a 4096 x 4096 map is 64 MB of pixels): Width/Height are
//  what was decoded, SourceWidth/SourceHeight the file's, so spots stay in the
//  file's pixels.
//
//  Runs on its own thread (Decode starts one): it joins COM as MTA there.
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace RynthCore.Plugin.RynthAi.AcMaps;

/// <summary>A decoded image: BGRA pixels (one uint each, as D3DFMT_A8R8G8B8), top row first.</summary>
internal sealed class DecodedImage
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int SourceWidth { get; init; }
    public int SourceHeight { get; init; }
    public uint[] Pixels { get; init; } = Array.Empty<uint>();
    /// <summary>Decoded pixels per file pixel (1 unless scaled down).</summary>
    public float Factor => SourceWidth > 0 ? Width / (float)SourceWidth : 1f;
}

internal static unsafe class WicImage
{
    public const int MaxSide = 4096;
    public const long MaxPixels = 8L * 1024 * 1024;

    private static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IidFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid FormatBgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    private const uint ClsctxInprocServer = 1;
    private const uint GenericRead = 0x80000000;
    private const int CoinitMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    // vtable slots (wincodec.h order).
    private const int FactoryCreateDecoderFromFilename = 3, FactoryCreateFormatConverter = 10, FactoryCreateBitmapScaler = 11;
    private const int DecoderGetFrame = 13;
    private const int SourceGetSize = 3, SourceCopyPixels = 7;
    private const int ConverterInitialize = 8, ScalerInitialize = 8;
    private const int Release = 2;
    private const int DitherNone = 0, PaletteCustom = 0, InterpolationFant = 3;

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, int coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, IntPtr* ppv);

    /// <summary>
    /// Decodes <paramref name="path"/> on a new background thread and hands the result (or null and why) to
    /// <paramref name="done"/> on that thread.
    /// </summary>
    public static void DecodeAsync(string path, Action<DecodedImage?, string> done)
    {
        var t = new System.Threading.Thread(() =>
        {
            DecodedImage? image = null;
            string error;
            try { image = Load(path, out error); }
            catch (Exception ex) { error = ex.Message; }
            try { done(image, error); } catch { }
        })
        { IsBackground = true, Name = "RynthAi ACMAPS decode" };
        t.Start();
    }

    /// <summary>The image at <paramref name="path"/>, or null and why. Joins COM on the calling thread for the call.</summary>
    public static DecodedImage? Load(string path, out string error)
    {
        int init = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        bool uninit = init >= 0;   // S_OK or S_FALSE; RPC_E_CHANGED_MODE: the thread is STA already, still usable
        if (init < 0 && init != RpcEChangedMode) { error = $"COM didn't start (0x{init:X8})."; return null; }
        IntPtr factory = IntPtr.Zero, decoder = IntPtr.Zero, frame = IntPtr.Zero, scaler = IntPtr.Zero, converter = IntPtr.Zero;
        try
        {
            Guid clsid = ClsidFactory, iid = IidFactory;
            int hr = CoCreateInstance(&clsid, IntPtr.Zero, ClsctxInprocServer, &iid, &factory);
            if (hr < 0) { error = $"Windows' image decoder isn't available (0x{hr:X8})."; return null; }
            fixed (char* p = path)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, char*, Guid*, uint, int, IntPtr*, int>)Slot(factory, FactoryCreateDecoderFromFilename))(
                    factory, p, null, GenericRead, 0, &decoder);
            }
            if (hr < 0) { error = $"Couldn't open the image (0x{hr:X8}): not a GIF, PNG or JPG Windows can read?"; return null; }
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(decoder, DecoderGetFrame))(decoder, 0, &frame);
            if (hr < 0) { error = $"The image has no picture in it (0x{hr:X8})."; return null; }
            uint w, h;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint*, uint*, int>)Slot(frame, SourceGetSize))(frame, &w, &h);
            if (hr < 0 || w == 0 || h == 0) { error = "The image is empty."; return null; }

            (int ow, int oh) = FitSize((int)w, (int)h);
            IntPtr source = frame;
            if (ow != (int)w || oh != (int)h)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(factory, FactoryCreateBitmapScaler))(factory, &scaler);
                if (hr >= 0)
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, int, int>)Slot(scaler, ScalerInitialize))(
                        scaler, frame, (uint)ow, (uint)oh, InterpolationFant);
                if (hr < 0) { error = $"Couldn't scale the {w} x {h} image down (0x{hr:X8})."; return null; }
                source = scaler;
            }

            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(factory, FactoryCreateFormatConverter))(factory, &converter);
            if (hr < 0) { error = $"Couldn't convert the image (0x{hr:X8})."; return null; }
            Guid fmt = FormatBgra32;
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)Slot(converter, ConverterInitialize))(
                converter, source, &fmt, DitherNone, IntPtr.Zero, 0.0, PaletteCustom);
            if (hr < 0) { error = $"Couldn't convert the image to colour pixels (0x{hr:X8})."; return null; }

            var pixels = new uint[(long)ow * oh];
            uint stride = (uint)ow * 4;
            fixed (uint* dst = pixels)
            {
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, byte*, int>)Slot(converter, SourceCopyPixels))(
                    converter, IntPtr.Zero, stride, stride * (uint)oh, (byte*)dst);
            }
            if (hr < 0) { error = $"Couldn't read the image's pixels (0x{hr:X8})."; return null; }
            error = string.Empty;
            return new DecodedImage { Width = ow, Height = oh, SourceWidth = (int)w, SourceHeight = (int)h, Pixels = pixels };
        }
        finally
        {
            ReleaseCom(converter);
            ReleaseCom(scaler);
            ReleaseCom(frame);
            ReleaseCom(decoder);
            ReleaseCom(factory);
            if (uninit) CoUninitialize();
        }
    }

    /// <summary>The size to decode a w x h image at: itself, or scaled down (aspect kept) to MaxSide and MaxPixels.</summary>
    internal static (int W, int H) FitSize(int w, int h)
    {
        if (w <= 0 || h <= 0) return (0, 0);
        double k = 1.0;
        int side = Math.Max(w, h);
        if (side > MaxSide) k = MaxSide / (double)side;
        double px = (double)w * h * k * k;
        if (px > MaxPixels) k *= Math.Sqrt(MaxPixels / px);
        if (k >= 1.0) return (w, h);
        return (Math.Max(1, (int)Math.Floor(w * k)), Math.Max(1, (int)Math.Floor(h * k)));
    }

    private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    private static void ReleaseCom(IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        try { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, Release))(com); } catch { }
    }
}

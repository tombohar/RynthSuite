using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthVision;

/// <summary>
/// C ABI exports. Nothing may throw out of an [UnmanagedCallersOnly] method (it
/// would take acclient.exe down): the lifecycle and event exports go through
/// RynthPluginRuntime, which catches and logs; the rest catch here.
/// </summary>
public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthVisionPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        try { Runtime.Shutdown(); }
        catch { }
        try
        {
            // The engine copies the settings string as soon as it gets it, on
            // the same (pump) thread that shuts us down, so nothing still
            // reads this buffer.
            IntPtr old = Interlocked.Exchange(ref _settingsPtr, IntPtr.Zero);
            if (old != IntPtr.Zero)
                Marshal.FreeHGlobal(old);
        }
        catch { }
        try { ReleaseSections(); }
        catch { }
    }

    // ── The Vision window (2026-10-27; RynthCore hosts it in its Vision panel's place, docs/PLUGIN_WINDOWS.md) ──

    private static string? _sectionsJson;
    private static IntPtr _sectionsPtr;

    /// <summary>The sections manifest: just the Vision window, on an engine that hosts it (Ui/VisionManifest.cs).</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSectionsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSectionsJson()
    {
        try
        {
            RynthVisionPlugin? plugin = Runtime.Plugin;
            if (plugin == null) return IntPtr.Zero;
            string json = plugin.SectionsJson;
            if (json == _sectionsJson && _sectionsPtr != IntPtr.Zero) return _sectionsPtr;
            _sectionsJson = json;
            IntPtr fresh = Marshal.StringToCoTaskMemUTF8(json);
            IntPtr old = Interlocked.Exchange(ref _sectionsPtr, fresh);
            if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
            return fresh;
        }
        catch { return IntPtr.Zero; }
    }

    private static void ReleaseSections()
    {
        IntPtr old = Interlocked.Exchange(ref _sectionsPtr, IntPtr.Zero);
        if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
        _sectionsJson = null;
    }

    /// <summary>
    /// A window's body (AC's render thread): 1 drawn, 0 not this plugin's surface or not ready, -2 the
    /// draw failed (RynthCore then says the window couldn't draw; docs/PLUGIN_WINDOWS.md).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderEmbed", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RenderEmbed(byte* surface, float width, float height, float* wantWidth, float* wantHeight)
    {
        try
        {
            if (surface == null) return 0;
            string name = Marshal.PtrToStringUTF8((IntPtr)surface) ?? string.Empty;
            if (name.EndsWith(":popout", StringComparison.Ordinal)) name = name[..^":popout".Length];
            RynthVisionPlugin? plugin = Runtime.Plugin;
            if (plugin == null || !plugin.RenderEmbed(name)) return 0;
            if (wantWidth != null) *wantWidth = width;
            if (wantHeight != null) *wantHeight = height;
            return 1;
        }
        catch { return -2; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    // Name/version are UTF-8 literals: the compiler stores them null-terminated
    // in the module's read-only data, so there is nothing to allocate or free,
    // and they stay valid for as long as the module is loaded (the engine
    // copies them right after loading, before Init).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName()
    {
        try { return LiteralPointer("RynthVision"u8); }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion()
    {
        try { return LiteralPointer("0.1.0"u8); }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr LiteralPointer(ReadOnlySpan<byte> literal)
    {
        fixed (byte* p = literal)
            return (IntPtr)p;
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // ── Settings bridge for the engine's Vision panels (UI/Data/VisionData.cs) ──
    // Still used: the engine's own Vision panel on an engine that doesn't host
    // RynthVision's window, and the Avalonia twin. RynthVision's own window
    // doesn't go through here (RynthVisionPlugin.Ui.cs).
    // The engine calls these on the plugin pump thread (UiDataHub). The
    // pointer swap (alloc-new → swap → free-old) keeps the previous buffer
    // alive until the next call; the engine copies it immediately.

    private static IntPtr _settingsPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthVisionGetSettingsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSettingsJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildSettingsJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _settingsPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Applies any subset of the settings keys (the panels send only the changed field).</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthVisionSetSettings", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetSettings(IntPtr ansiJson)
    {
        try
        {
            if (ansiJson == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ansiJson);
            if (!string.IsNullOrEmpty(json))
                Runtime.Plugin?.ApplySettingsJson(json);
        }
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthVisionInspectTerrain", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void InspectTerrain()
    {
        try { Runtime.Plugin?.InspectTerrain(); }
        catch { }
    }
}

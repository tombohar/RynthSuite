using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDefective;

/// <summary>
/// C ABI exports. Nothing may throw out of an [UnmanagedCallersOnly] method (it would take
/// acclient.exe down): every export goes through RynthPluginRuntime, which catches and logs.
/// RynthDefective draws through the engine's script windows (host calls from its own tick), so it
/// exports no render callback and keeps no buffer the engine reads later.
/// </summary>
public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthDefectivePlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    // UTF-8 literals live in the module's read-only data: nothing to allocate or free.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => LiteralPointer("RynthDefective"u8);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => LiteralPointer("0.1.0"u8);

    private static IntPtr LiteralPointer(ReadOnlySpan<byte> literal)
    {
        fixed (byte* p = literal)
            return (IntPtr)p;
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUpdateHealth", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth) =>
        Runtime.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);

    // The Corpses window (RynthDefectivePlugin.Corpses.cs): the server's corpse lines, corpses coming into view and leaving it.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatWindowText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag) => Runtime.OnChatWindowText(textUtf16, chatType, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCreateObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCreateObject(uint objectId) => Runtime.OnCreateObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnDeleteObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnDeleteObject(uint objectId) => Runtime.OnDeleteObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnViewObjectContents", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnViewObjectContents(uint objectId) => Runtime.OnViewObjectContents(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // ── The shortcut bar's window (plugin API v90; RynthDefectivePlugin.Bar.cs) ──

    private static string? _sectionsJson;
    private static IntPtr _sectionsPtr;

    /// <summary>The sections manifest (polled every ~2 s): the bar's window. Kept until it changes.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSectionsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSectionsJson()
    {
        try
        {
            RynthDefectivePlugin? plugin = Runtime.Plugin;
            if (plugin == null) return IntPtr.Zero;
            string json = plugin.SectionsJson();
            if (json == _sectionsJson && _sectionsPtr != IntPtr.Zero) return _sectionsPtr;
            _sectionsJson = json;
            IntPtr fresh = Marshal.StringToCoTaskMemUTF8(json);
            IntPtr old = System.Threading.Interlocked.Exchange(ref _sectionsPtr, fresh);
            if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
            return fresh;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The bar's and the panel buttons' window bodies ("window:bar", "window:panels"; ":popout" when popped out). 1 drawn, 0 not ready, -2 failed.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderEmbed", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RenderEmbed(byte* surface, float width, float height, float* wantWidth, float* wantHeight)
    {
        try
        {
            if (surface == null) return 0;
            string name = Marshal.PtrToStringUTF8((IntPtr)surface) ?? string.Empty;
            bool popped = name.EndsWith(":popout", StringComparison.Ordinal);
            if (popped) name = name[..^":popout".Length];
            RynthDefectivePlugin? plugin = Runtime.Plugin;
            if (plugin == null) return 0;
            int result;
            float wantH;
            if (name == RynthDefectivePlugin.BarSurface) result = plugin.RenderBar(popped, out wantH);
            else if (name == RynthDefectivePlugin.PanelsSurface) result = plugin.RenderPanels(out wantH);
            else if (name == RynthDefectivePlugin.SelectionSurface) result = plugin.RenderSelection(out wantH);
            else if (name == RynthDefectivePlugin.CorpsesSurface) result = plugin.RenderCorpses(out wantH);
            else return 0;
            if (result != 1) return result;
            if (wantWidth != null) *wantWidth = width;
            if (wantHeight != null) *wantHeight = wantH > 0 ? wantH : height;
            return 1;
        }
        catch { return 0; }
    }
}

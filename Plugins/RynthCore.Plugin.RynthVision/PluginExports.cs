using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthVision;

public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthVisionPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => RynthVisionPlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => RynthVisionPlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRender", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Render() => Runtime.OnRender();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // ── Avalonia settings-panel bridge (engine RynthVisionPanel) ──────────────
    // The panel reads RynthVisionGetSettingsJson to populate its controls and
    // pushes changes via RynthVisionSetSettings. The pointer swap (alloc-new →
    // swap → free-old) keeps the UI-thread caller safe without a lock, matching
    // the RynthChat scrollback bridge.

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
    public static void InspectTerrain() => Runtime.Plugin?.InspectTerrain();
}

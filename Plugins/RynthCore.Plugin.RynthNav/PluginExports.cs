using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthNav;

// Native C-ABI exports the RynthCore engine probes by name (GetProcAddress).
// Mirrors RynthVision's PluginExports; RynthPluginRuntime<T> bridges to the
// managed RynthNavPlugin instance.
public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthNavPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => RynthNavPlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => RynthNavPlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // ── RynthNav panel bridge (engine RynthNavPanel reads/drives via these) ───────
    // GetStatusJson uses the alloc-new → swap → free-old pointer pattern (matches
    // RynthVision) so the UI-thread caller never races a free.

    private static IntPtr _statusPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthNavGetStatusJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetStatusJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildStatusJson() ?? "{}";
            IntPtr nw = Marshal.StringToHGlobalAnsi(json);
            IntPtr old = Interlocked.Exchange(ref _statusPtr, nw);
            if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
            return nw;
        }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthNavLoadTile", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void LoadTile() { try { Runtime.Plugin?.DoLoadTile(); } catch { } }

    [UnmanagedCallersOnly(EntryPoint = "RynthNavTestQuery", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void TestQuery() { try { Runtime.Plugin?.DoTestQuery(); } catch { } }

    [UnmanagedCallersOnly(EntryPoint = "RynthNavPreviewPath", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void PreviewPath(IntPtr ansiCoord)
    {
        try { Runtime.Plugin?.DoPreviewPath(Marshal.PtrToStringAnsi(ansiCoord)); } catch { }
    }

    // Manual movement from the panel d-pad. cmd: 1=fwd 2=back 3=left 4=right 5=stop.
    // The plugin only records the request here; OnTick issues the AC movement call.
    [UnmanagedCallersOnly(EntryPoint = "RynthNavMove", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Move(int cmd) { try { Runtime.Plugin?.DoMove(cmd); } catch { } }

    // Auto-walk to a /loc coordinate (computes the navmesh path + steers).
    [UnmanagedCallersOnly(EntryPoint = "RynthNavGoto", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Goto(IntPtr ansiCoord)
    {
        try { Runtime.Plugin?.DoGoto(Marshal.PtrToStringAnsi(ansiCoord)); } catch { }
    }
}

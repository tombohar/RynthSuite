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
    internal static readonly RynthPluginRuntime<RynthNavPlugin> Runtime = new();

    // Typed interfaces for other plugins (host GetPluginInterface, API v68): "RynthNav.Path" v1
    // (RynthAi plans stuck-recovery detours with it). Fixed signature: void* (const char* iface, uint version).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginQueryInterface", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr QueryInterface(IntPtr ifaceAnsi, uint version)
    {
        try
        {
            string iface = ifaceAnsi == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ifaceAnsi) ?? "";
            return PathInterface.Query(iface, version);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        // An exception escaping an UnmanagedCallersOnly export fail-fasts the client under NativeAOT.
        // Guard each step so one failure cannot skip the other.
        RynthNavPlugin? plugin = Runtime.Plugin;   // Runtime.Shutdown drops it; the log needs its host
        try { Runtime.Shutdown(); }
        catch (Exception ex) { LogShutdownError(plugin, "Runtime.Shutdown", ex); }
        try { PathInterface.Release(); }
        catch (Exception ex) { LogShutdownError(plugin, "PathInterface.Release", ex); }
    }

    private static void LogShutdownError(RynthNavPlugin? plugin, string step, Exception ex)
    {
        try { plugin?.LogFromExport($"[RynthNav] Shutdown: {step} failed: {ex.GetType().Name}: {ex.Message}"); }
        catch { /* never throw out of a native export */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName()
    {
        try { return RynthNavPlugin.NamePointer; }   // a static initializer that throws surfaces here (TypeInitializationException)
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion()
    {
        try { return RynthNavPlugin.VersionPointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // The server's lines about lifestones, ties and recalls teach RynthNav where recalls land.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatWindowText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag) => Runtime.OnChatWindowText(textUtf16, chatType, eatFlag);

    // Commands from other plugins (host SendPluginCommand): action "rnav", value a /rnav
    // command without the "/rnav" ("arrow 42.1N, 33.6E", "go Holtburg"). Copied and queued.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginApplyRemoteCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ApplyRemoteCommand(IntPtr actionAnsi, IntPtr valueAnsi)
    {
        try
        {
            string action = actionAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(actionAnsi) ?? "" : "";
            string value = valueAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(valueAnsi) ?? "" : "";
            if (action.Equals("rnav", StringComparison.OrdinalIgnoreCase)) Runtime.Plugin?.EnqueueCommand(value);
        }
        catch { }
    }

    // A /rnav command from the engine's RynthNav panel or the chat window's coordinate menu
    // (UTF-8, without "/rnav"): "arrow 42.1N, 33.6E Holtburg", "go Holtburg", "fav ...".
    // Queued; runs on the tick.
    [UnmanagedCallersOnly(EntryPoint = "RynthNavCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Command(IntPtr utf8)
    {
        try { if (utf8 != IntPtr.Zero) Runtime.Plugin?.EnqueueCommand(Marshal.PtrToStringUTF8(utf8)); } catch { }
    }

    // How many times a command has asked to show the arrow: the engine polls this one int
    // (twice a second, any thread) and opens the arrow overlay when it moves.
    [UnmanagedCallersOnly(EntryPoint = "RynthNavArrowSeq", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int ArrowSeq()
    {
        try { return Runtime.Plugin?.ArrowSeq ?? 0; } catch { return 0; }
    }

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

    private static IntPtr _statusUtf8Ptr = IntPtr.Zero;

    // The same JSON as RynthNavGetStatusJson, UTF-8 (0.6.1+): place and recall names keep
    // characters the ANSI code page can't hold ("→" in a portal recall's label). The engine
    // tries this first and falls back to the ANSI one for older RynthNavs. Its own buffer,
    // same alloc-new → swap → free-old pattern, so either getter can be called alone.
    [UnmanagedCallersOnly(EntryPoint = "RynthNavGetStatusJsonUtf8", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetStatusJsonUtf8()
    {
        try
        {
            RynthNavPlugin? plugin = Runtime.Plugin;
            byte[] bytes = plugin != null ? plugin.BuildStatusUtf8() : new byte[] { (byte)'{', (byte)'}', 0 };
            IntPtr nw = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, nw, bytes.Length);
            IntPtr old = Interlocked.Exchange(ref _statusUtf8Ptr, nw);
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

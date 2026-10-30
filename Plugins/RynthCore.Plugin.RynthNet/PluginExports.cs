using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthNet;

public static unsafe class PluginExports
{
    internal static readonly RynthPluginRuntime<RynthNetPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        // Each step is guarded alone: an exception escaping a native export fail-fasts the client.
        try { Runtime.Shutdown(); }   // stops the transport: joins its thread and tasks
        catch { }
        try { NetInterface.Release(); }
        catch { }
        try { ReleaseSnapshot(); }
        catch { }
        try { ReleaseSections(); }
        catch { }
    }

    // The windows RynthNet draws (plugin API v89 hosts them; docs in RynthCore docs/PLUGIN_WINDOWS.md).
    private static string? _sectionsJson;
    private static IntPtr _sectionsPtr;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSectionsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSectionsJson()
    {
        try
        {
            if (Runtime.Plugin == null) return IntPtr.Zero;
            string json = Characters.CharactersManifest.Json;
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

    // A window's body (AC's render thread): 1 drawn, 0 not this plugin's surface or not ready.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderEmbed", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RenderEmbed(byte* surface, float width, float height, float* wantWidth, float* wantHeight)
    {
        try
        {
            if (surface == null) return 0;
            string name = Marshal.PtrToStringUTF8((IntPtr)surface) ?? string.Empty;
            if (name.EndsWith(":popout", StringComparison.Ordinal)) name = name[..^":popout".Length];
            RynthNetPlugin? plugin = Runtime.Plugin;
            if (plugin == null || !plugin.RenderEmbed(name, out float wantH)) return 0;
            if (wantWidth != null) *wantWidth = width;
            if (wantHeight != null) *wantHeight = wantH > 0 ? wantH : height;
            return 1;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName()
    {
        try { return RynthNetPlugin.NamePointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion()
    {
        try { return RynthNetPlugin.VersionPointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // Typed interface for other plugins (host GetPluginInterface, API v68): "RynthNet.Net" v1.
    // Fixed signature: void* (const char* iface, uint version).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginQueryInterface", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr QueryInterface(IntPtr ifaceAnsi, uint version)
    {
        try
        {
            string iface = ifaceAnsi == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ifaceAnsi) ?? "";
            return NetInterface.Query(iface, version);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // Commands from other plugins (host SendPluginCommand): RynthAi forwards UtilityBelt's
    // /ub bc, /ub bct, /ub netclients as ("ub", "bc ..."). Copied and queued; run in OnTick.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginApplyRemoteCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ApplyRemoteCommand(IntPtr actionAnsi, IntPtr valueAnsi)
    {
        try
        {
            string action = actionAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(actionAnsi) ?? "" : "";
            string value = valueAnsi != IntPtr.Zero ? Marshal.PtrToStringAnsi(valueAnsi) ?? "" : "";
            if (action.Length > 0) Runtime.Plugin?.EnqueueForwarded(action, value);
        }
        catch { }
    }

    // Snapshot for panels (host GetPluginSnapshotJson): {online,node,char,tags,clients:[...]}.
    // Polled from other threads: a per-thread buffer, so no caller frees another's pointer
    // (the RynthAi pattern). The string itself is rebuilt by OnTick twice a second.
    [ThreadStatic] private static IntPtr _snapshotPtr;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSnapshotJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSnapshotJson()
    {
        try
        {
            string json = Runtime.Plugin?.Snapshot ?? "{\"online\":false,\"clients\":[]}";
            IntPtr fresh = Marshal.StringToHGlobalAnsi(json);
            IntPtr old = _snapshotPtr;
            _snapshotPtr = fresh;
            if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
            return fresh;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Frees this thread's snapshot buffer (Shutdown runs on the pump thread).</summary>
    private static void ReleaseSnapshot()
    {
        IntPtr old = Interlocked.Exchange(ref _snapshotPtr, IntPtr.Zero);
        if (old != IntPtr.Zero) Marshal.FreeHGlobal(old);
    }
}

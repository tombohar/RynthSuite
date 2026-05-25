using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthTracker;

public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthTrackerPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => RynthTrackerPlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => RynthTrackerPlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRender", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Render() => Runtime.OnRender();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatWindowText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag)
        => Runtime.OnChatWindowText(textUtf16, chatType, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUpdateHealth", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth)
        => Runtime.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnDeleteObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnDeleteObject(uint objectId) => Runtime.OnDeleteObject(objectId);

    // ── Snapshot bridge ──────────────────────────────────────────────────────
    // Panel polls RynthTrackerGetSnapshotJson every 500ms.
    // Alloc-new → swap → free-old keeps the polling thread safe without a lock.

    private static IntPtr _snapshotPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthTrackerGetSnapshotJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSnapshotJson()
    {
        try
        {
            string json   = Runtime.Plugin?.GetSnapshot()
                          ?? "{\"ss\":0.0,\"xt\":0,\"xh\":0,\"lt\":0,\"lh\":0,\"kt\":0,\"kh\":0.0,\"xk\":0,\"dt\":0}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _snapshotPtr, newPtr);
            if (oldPtr != IntPtr.Zero) Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthTrackerReset", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Reset()
    {
        try { Runtime.Plugin?.Reset(); }
        catch { }
    }
}

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
    public static void Shutdown()
    {
        try { Runtime.Shutdown(); }   // an exception out of a native export fail-fasts the client
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName()
    {
        try { return RynthTrackerPlugin.NamePointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion()
    {
        try { return RynthTrackerPlugin.VersionPointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

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

    // Corpses (0.3.0): a kill a ring, wall or chain spell made without a kill notice is found by its corpse.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCreateObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCreateObject(uint objectId) => Runtime.OnCreateObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnDeleteObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnDeleteObject(uint objectId) => Runtime.OnDeleteObject(objectId);

    // Kills and XP per kill (0.2.0) come from this; without the export the engine never calls it.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnKillNotification", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnKillNotification(IntPtr textUtf16) => Runtime.OnKillNotification(textUtf16);

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

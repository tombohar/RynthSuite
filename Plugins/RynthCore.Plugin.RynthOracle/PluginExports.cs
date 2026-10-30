using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthOracle;

/// <summary>
/// C ABI exports. Nothing may throw out of an [UnmanagedCallersOnly] method (it would take
/// acclient.exe down): every export goes through RynthPluginRuntime, which catches and logs.
/// RynthOracle draws through the engine's script windows (host calls from its own tick), so it
/// exports no render callback and keeps no buffer the engine reads later.
/// </summary>
public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthOraclePlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        try { Runtime.Shutdown(); }   // an exception out of a native export fail-fasts the client
        catch { }
    }

    // UTF-8 literals live in the module's read-only data: nothing to allocate or free.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName()
    {
        try { return LiteralPointer("RynthOracle"u8); }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion()
    {
        // Keep in step with RynthOraclePlugin.Version (a u8 literal can't be built from a const string).
        try { return LiteralPointer("0.3.0"u8); }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr LiteralPointer(ReadOnlySpan<byte> literal)
    {
        fixed (byte* p = literal)
            return (IntPtr)p;
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

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);
}

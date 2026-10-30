using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthChat;

public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthChatPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        try { Runtime.Shutdown(); }   // an exception out of a native export fail-fasts the client
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName()
    {
        try { return RynthChatPlugin.NamePointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion()
    {
        try { return RynthChatPlugin.VersionPointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatWindowText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag) => Runtime.OnChatWindowText(textUtf16, chatType, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    // ── Chat scrollback bridge for the engine-side RynthChatPanel ──────────
    // Panel polls RynthChatGetScrollbackJson (~10 Hz) to receive new lines.
    // Panel calls RynthChatSendLine to submit a message via InvokeChatParser.
    // Buffer swap pattern from RynthAi: alloc-new → swap → free-old keeps the
    // polling thread safe without a lock.

    private static IntPtr _scrollbackPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthChatGetScrollbackJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetScrollbackJson(ulong sinceSeq)
    {
        try
        {
            string json = Runtime.Plugin?.BuildScrollbackJson(sinceSeq) ?? "[]";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _scrollbackPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// How many lines the scrollback keeps (the engine's "Chat history" setting; 0.3.0).
    /// Returns the number applied (clamped to 100..10000), or 0 before the plugin is up.
    /// Engines before it never call this, and the plugin keeps ChatBuffer.DefaultCapacity.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthChatSetCapacity", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int SetCapacity(int lines)
    {
        try { return Runtime.Plugin?.SetScrollbackCapacity(lines) ?? 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthChatSendLine", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SendLine(IntPtr ansiText)
    {
        try
        {
            if (ansiText == IntPtr.Zero) return;
            string? text = Marshal.PtrToStringAnsi(ansiText);
            if (string.IsNullOrEmpty(text)) return;
            Runtime.Plugin?.SendLine(text);
        }
        catch { }
    }
}

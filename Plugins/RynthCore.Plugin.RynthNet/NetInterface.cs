// ============================================================================
//  RynthNet - NetInterface.cs
//  RynthNet's side of the "RynthNet.Net" interface (Plugins/Shared/RynthNetApi.cs):
//  the table RynthPluginQueryInterface hands out and the functions behind it.
//  They run on the plugin pump thread (the caller's OnTick) and act on the
//  current plugin instance. The table is freed in RynthPluginShutdown; callers
//  resolve it again every tick.
// ============================================================================

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.RynthNet.Transport;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNet;

internal static unsafe class NetInterface
{
    private static RynthNetApiV1* _table;
    private static IntPtr _out;   // the last string handed back (valid until the next call)

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthNetApiV1.InterfaceName || version == 0 || version > RynthNetApiV1.InterfaceVersion) return IntPtr.Zero;
        if (PluginExports.Runtime.Plugin == null) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthNetApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthNetApiV1));
            t->Version = RynthNetApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthNetApiV1);
            t->IsOnline = &IsOnline;
            t->GetClientsJson = &GetClientsJson;
            t->GetSelfJson = &GetSelfJson;
            t->SendCommand = &SendCommand;
            t->Publish = &Publish;
            t->Subscribe = &Subscribe;
            t->PollChannel = &PollChannel;
            t->GetTags = &GetTags;
            t->SetTag = &SetTag;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Frees the table and the last string (plugin shutdown).</summary>
    public static void Release()
    {
        var t = _table;
        _table = null;
        if (t != null) NativeMemory.Free(t);
        IntPtr o = _out;
        _out = IntPtr.Zero;
        if (o != IntPtr.Zero) Marshal.FreeCoTaskMem(o);
    }

    private static RynthNetPlugin? P => PluginExports.Runtime.Plugin;

    private static string In(byte* s) => s == null ? "" : Marshal.PtrToStringUTF8((IntPtr)s) ?? "";

    private static int Out(byte** dst, string? value)
    {
        if (dst == null || value == null) return 0;
        IntPtr fresh = Marshal.StringToCoTaskMemUTF8(value);
        IntPtr old = _out;
        _out = fresh;
        if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
        *dst = (byte*)fresh;
        return 1;
    }

    private static NetTarget Target(int kind, byte* target) => kind switch
    {
        RynthNetApiV1.TargetTags => new NetTarget(TargetKind.Tags, In(target)),
        RynthNetApiV1.TargetCharacter => new NetTarget(TargetKind.Character, In(target)),
        _ => NetTarget.All,
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int IsOnline()
    {
        try { return P?.IsOnline == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetClientsJson(byte** json)
    {
        try { return Out(json, P?.ClientsJson()); }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetSelfJson(byte** json)
    {
        try { return Out(json, P?.SelfJson()); }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SendCommand(int kind, byte* target, byte* command, int delayMs, int flags)
    {
        try
        {
            var p = P;
            if (p == null || kind < 0 || kind > 2) return -1;
            var names = p.SendCommand(Target(kind, target), In(command), delayMs, (flags & RynthNetApiV1.FlagIncludeSelf) != 0, quiet: true);
            return names?.Count ?? -1;
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Publish(byte* channel, byte* message, int kind, byte* target)
    {
        try
        {
            var p = P;
            if (p == null || kind < 0 || kind > 2) return -1;
            return p.Publish(In(channel), In(message), Target(kind, target));
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Subscribe(byte* channel, int on)
    {
        try { return P?.Subscribe(In(channel), on != 0) == true ? 1 : 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int PollChannel(byte* channel, byte** json)
    {
        try { return Out(json, P?.PollChannel(In(channel))); }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetTags(byte** text)
    {
        try { return Out(text, P?.TagsText); }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int SetTag(byte* tag, int on)
    {
        try { return P?.SetTag(In(tag), on != 0, out _) == true ? 1 : 0; }
        catch { return 0; }
    }
}

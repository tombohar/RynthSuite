using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// RynthNav's side of the "RynthNav.Routes" interface (Shared/RynthNavRoutesApi.cs): the table
/// RynthPluginQueryInterface hands out and the two functions behind it, which queue a route maker job
/// and report one (RynthNavPlugin.RouteMaker.cs). They run on the plugin pump thread and act on the
/// current plugin instance. <see cref="Release"/> frees the table and the last string at
/// RynthPluginShutdown; callers resolve the table again every tick.
/// </summary>
internal static unsafe class RoutesInterface
{
    private static RynthNavRoutesApiV1* _table;
    private static IntPtr _out;   // the last string handed back (valid until the next call)

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthNavRoutesApiV1.InterfaceName || version == 0 || version > RynthNavRoutesApiV1.InterfaceVersion) return IntPtr.Zero;
        if (PluginExports.Runtime.Plugin == null) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthNavRoutesApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthNavRoutesApiV1));
            t->Version = RynthNavRoutesApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthNavRoutesApiV1);
            t->Request = &Request;
            t->Poll = &Poll;
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

    private static void Out(byte** dst, string value)
    {
        if (dst == null) return;
        IntPtr fresh = Marshal.StringToCoTaskMemUTF8(value);
        IntPtr old = _out;
        _out = fresh;
        if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
        *dst = (byte*)fresh;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Request(byte* json, byte** reply)
    {
        try
        {
            if (reply != null) *reply = null;
            RynthNavPlugin? p = PluginExports.Runtime.Plugin;
            if (p == null) { Out(reply, "{\"error\":\"RynthNav isn't running\"}"); return 0; }
            string text = json == null ? "" : Marshal.PtrToStringUTF8((IntPtr)json) ?? "";
            string answer = p.RoutesRequest(text, out int id);
            Out(reply, answer);
            return id;
        }
        catch
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Poll(int id, byte** json)
    {
        try
        {
            if (json != null) *json = null;
            RynthNavPlugin? p = PluginExports.Runtime.Plugin;
            if (p == null) { Out(json, "{\"id\":" + id + ",\"state\":\"unknown\",\"error\":\"RynthNav isn't running\"}"); return 0; }
            string answer = p.RoutesPoll(id, out bool known);
            Out(json, answer);
            return known ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }
}

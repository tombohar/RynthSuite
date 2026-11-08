using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthAi's side of the "RynthAi.Routes" interface (Shared/RynthAiRoutesApi.cs, 2026-10-30): another
/// plugin has RynthAi make dungeon routes (the work of /ra dunroute and /ra dunpatrol-save) and reads
/// JSON answers. Request checks and queues; RynthAi's tick does the work (RynthAiPlugin.DunRoutes.cs);
/// Poll reads a job. Runs on the plugin pump thread (the caller's OnTick), on the current plugin instance.
/// </summary>
internal static unsafe class RoutesInterface
{
    private static RynthAiRoutesApiV1* _table;
    private static IntPtr _out;   // the last string handed back (valid until the next call)

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthAiRoutesApiV1.InterfaceName || version == 0 || version > RynthAiRoutesApiV1.InterfaceVersion) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthAiRoutesApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthAiRoutesApiV1));
            t->Version = RynthAiRoutesApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthAiRoutesApiV1);
            t->Request = &Request;
            t->Poll = &Poll;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Frees the table and the last string (plugin shutdown). Callers resolve it every tick.</summary>
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
        IntPtr fresh = Marshal.StringToCoTaskMemUTF8(value ?? "");
        IntPtr old = _out;
        _out = fresh;
        if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
        *dst = (byte*)fresh;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static uint Request(byte* json, byte** answer)
    {
        try
        {
            string text = json == null ? "" : Marshal.PtrToStringUTF8((IntPtr)json) ?? "";
            var p = PluginExports.Runtime.Plugin;
            if (p == null) { Out(answer, DunRouteJobs.ErrorJson("RynthAi isn't ready")); return 0; }
            uint id = p.RouteRequest(text, out string error);
            Out(answer, id != 0 ? DunRouteJobs.QueuedJson(id) : DunRouteJobs.ErrorJson(error));
            return id;
        }
        catch (Exception ex)
        {
            try { Out(answer, DunRouteJobs.ErrorJson("RynthAi failed to take the request: " + ex.Message)); } catch { }
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Poll(uint id, byte** json)
    {
        try
        {
            var p = PluginExports.Runtime.Plugin;
            if (p == null)
            {
                Out(json, "{\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                          + ",\"state\":\"unknown\",\"detail\":\"RynthAi isn't ready\"}");
                return 0;
            }
            bool known = p.RouteJobs.TryPoll(id, out string answer);
            Out(json, answer);
            return known ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// RynthNav's side of the "RynthNav.Path" interface (Shared/RynthNavPathApi.cs): the
/// table RynthPluginQueryInterface hands out. The functions run on the plugin pump
/// thread (the caller's OnTick) and act on the current plugin instance, so the table
/// survives a re-Init. <see cref="Release"/> frees it at RynthPluginShutdown.
/// </summary>
internal static unsafe class PathInterface
{
    private static RynthNavPathApiV1* _table;

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthNavPathApiV1.InterfaceName || version != RynthNavPathApiV1.InterfaceVersion) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthNavPathApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthNavPathApiV1));
            t->Version = RynthNavPathApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthNavPathApiV1);
            t->FindPath = &FindPath;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>Frees the table (plugin shutdown). Callers re-resolve on every use.</summary>
    public static void Release()
    {
        var t = _table;
        _table = null;
        if (t != null) NativeMemory.Free(t);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FindPath(double sx, double sy, double sz, double gx, double gy, double gz,
                                double* points, int maxPoints, int* count)
    {
        try
        {
            if (count != null) *count = 0;
            var p = PluginExports.Runtime.Plugin;
            if (p == null) return RynthNavPathApiV1.ResultNoMesh;
            return p.InterfaceFindPath(sx, sy, sz, gx, gy, gz, points, maxPoints, count);
        }
        catch
        {
            return RynthNavPathApiV1.ResultNoMesh;
        }
    }
}

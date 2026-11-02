using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.Plugin.Shared;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

/// <summary>
/// RynthNav's side of the "RynthNav.Path" interface (Shared/RynthNavPathApi.cs): the
/// table RynthPluginQueryInterface hands out. The functions run on the plugin pump
/// thread (the caller's OnTick) and act on the current plugin instance, so the table
/// survives a re-Init. <see cref="Release"/> frees it at RynthPluginShutdown.
/// Version 2 (2026-10-23) appends FindPathEx and Raycast; a caller asking for version 1
/// gets the same table (it checks Version/StructSize before a field past the ones it knows).
/// </summary>
internal static unsafe class PathInterface
{
    private static RynthNavPathApiV1* _table;
    private static long _lastErrorLogMs;

    public static IntPtr Query(string iface, uint version)
    {
        if (!NavPathQuery.ServesInterface(iface, version)) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthNavPathApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthNavPathApiV1));
            t->Version = RynthNavPathApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthNavPathApiV1);
            t->FindPath = &FindPath;
            t->FindPathEx = &FindPathEx;
            t->Raycast = &Raycast;
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

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int FindPathEx(double sx, double sy, double sz, double gx, double gy, double gz, int flags, int maxTiles,
                                  double* points, int maxPoints, int* count, double* info)
    {
        try
        {
            Clear(count, info);
            var p = PluginExports.Runtime.Plugin;
            if (p == null) return RynthNavPathApiV1.ResultNoMesh;
            return p.InterfaceFindPathEx(sx, sy, sz, gx, gy, gz, flags, maxTiles, points, maxPoints, count, info);
        }
        catch (Exception ex)
        {
            Clear(count, info);   // no half-written path counts
            LogError("FindPathEx", ex);
            return RynthNavPathApiV1.ResultNoMesh;
        }
    }

    /// <summary>FindPathEx's outputs before an answer: no corners, info all 0.</summary>
    private static void Clear(int* count, double* info)
    {
        if (count != null) *count = 0;
        if (info != null) for (int i = 0; i < RynthNavPathApiV1.InfoLength; i++) info[i] = 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Raycast(double sx, double sy, double sz, double gx, double gy, double gz)
    {
        try
        {
            var p = PluginExports.Runtime.Plugin;
            if (p == null) return RynthNavPathApiV1.ResultNoMesh;
            return p.InterfaceRaycast(sx, sy, sz, gx, gy, gz);
        }
        catch (Exception ex)
        {
            LogError("Raycast", ex);
            return RynthNavPathApiV1.ResultNoMesh;
        }
    }

    /// <summary>A failure inside an interface call, logged at most every 10 s (a caller may ask many times a second).</summary>
    private static void LogError(string what, Exception ex)
    {
        try
        {
            long now = Environment.TickCount64;
            if (now - _lastErrorLogMs < 10_000) return;
            _lastErrorLogMs = now;
            PluginExports.Runtime.Plugin?.LogFromExport($"[RynthNav] path interface: {what} failed: {ex.GetType().Name}: {ex.Message}");
        }
        catch { /* never throw out of a native export */ }
    }
}

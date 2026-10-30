using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.RynthAiHostTests.Fakes;

/// <summary>
/// RynthCore's server list (plugin API v86 GetServerStateJson) and the active server owner's
/// "RynthServer.Profile" table (GetPluginInterface), for tests of what RynthAi does with a server
/// plugin's profile values. <see cref="Install"/> after FakeHost.Reset and before FakeHost.Create;
/// the JSON can change between calls (the next poll reads it). Other interfaces answer nothing.
/// </summary>
internal static unsafe class FakeServerState
{
    /// <summary>GetServerStateJson's answer ("" = none, the call fails).</summary>
    public static string StateJson = string.Empty;
    /// <summary>The profile JSON the owner plugin serves ("" = it serves no profile).</summary>
    public static string ProfileJson = string.Empty;
    /// <summary>The plugin that serves it ("" = any plugin asked).</summary>
    public static string ProfilePlugin = string.Empty;
    /// <summary>Every (plugin, interface) asked for.</summary>
    public static readonly List<(string Plugin, string Iface)> Queries = new();

    private static RynthServerProfileApiV1* _profile;

    public static void Reset()
    {
        StateJson = ProfileJson = ProfilePlugin = string.Empty;
        Queries.Clear();
    }

    /// <summary>Puts the two engine calls into the next FakeHost.Create.</summary>
    public static void Install() => FakeHost.ConfigureApi = Configure;

    private static void Configure(ref RynthCoreApiNative api)
    {
        api.Version = Math.Max(api.Version, 86u);
        api.GetServerStateJsonFn = (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, int>)&GetServerStateJson;
        api.GetPluginInterfaceFn = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, IntPtr>)&GetPluginInterface;
    }

    /// <summary>A GetServerStateJson answer (schema 1) with one active server (or none: <paramref name="activeId"/> "").</summary>
    public static string State(string activeId, string activeName, string activePlugin, string world, params (string Id, string Name, bool Detected, string Plugin)[] others)
    {
        var sb = new StringBuilder();
        sb.Append("{\"schema\":1,\"revision\":1,\"host\":\"\",\"world\":\"").Append(world).Append("\",\"pick\":\"auto\",")
          .Append("\"active\":\"").Append(activeId).Append("\",\"activeName\":\"").Append(activeName)
          .Append("\",\"activePlugin\":\"").Append(activePlugin).Append("\",\"servers\":[");
        bool first = true;
        void Row(string id, string name, bool active, bool detected, string plugin)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"id\":\"").Append(id).Append("\",\"name\":\"").Append(name).Append("\",\"active\":").Append(active ? "true" : "false")
              .Append(",\"detected\":").Append(detected ? "true" : "false").Append(",\"force\":\"auto\",\"builtIn\":false,\"plugin\":\"")
              .Append(plugin).Append("\",\"reason\":\"\"}");
        }
        if (activeId.Length > 0) Row(activeId, activeName, true, true, activePlugin);
        foreach (var o in others) Row(o.Id, o.Name, false, o.Detected, o.Plugin);
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>A minimal profile JSON with these "values".</summary>
    public static string Profile(string id, params (string Key, double Value)[] values)
    {
        var sb = new StringBuilder("{\"schema\":1,\"id\":\"").Append(id).Append("\",\"values\":{");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(values[i].Key).Append("\":").Append(values[i].Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.Append("}}").ToString();
    }

    private static int Write(string s, byte* buffer, int capacity)
    {
        byte[] b = Encoding.UTF8.GetBytes(s);
        if (b.Length >= capacity || buffer == null) return b.Length;
        for (int i = 0; i < b.Length; i++) buffer[i] = b[i];
        buffer[b.Length] = 0;
        return b.Length;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetServerStateJson(byte* buffer, int capacity) => StateJson.Length == 0 ? -1 : Write(StateJson, buffer, capacity);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr GetPluginInterface(IntPtr plugin, IntPtr iface, uint version)
    {
        string p = Marshal.PtrToStringAnsi(plugin) ?? string.Empty;
        string i = Marshal.PtrToStringAnsi(iface) ?? string.Empty;
        Queries.Add((p, i));
        if (i != RynthServerProfileApiV1.InterfaceName || version != RynthServerProfileApiV1.InterfaceVersion || ProfileJson.Length == 0
            || (ProfilePlugin.Length > 0 && !p.Equals(ProfilePlugin, StringComparison.OrdinalIgnoreCase)))
            return IntPtr.Zero;
        if (_profile == null)
        {
            var t = (RynthServerProfileApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthServerProfileApiV1));
            t->StructSize = (uint)sizeof(RynthServerProfileApiV1);
            t->GetProfileJson = &GetProfileJson;
            t->Version = RynthServerProfileApiV1.InterfaceVersion;
            _profile = t;
        }
        return (IntPtr)_profile;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetProfileJson(byte* buffer, int capacity) => ProfileJson.Length == 0 ? 0 : Write(ProfileJson, buffer, capacity);
}

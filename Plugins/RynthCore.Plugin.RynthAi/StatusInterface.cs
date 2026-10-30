using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// RynthAi's side of the "RynthAi.Status" interface (Shared/RynthAiStatusApi.cs): a compact
/// summary of what the bot is doing, which RynthNet shares with the other clients. Runs on
/// the plugin pump thread (the caller's OnTick), on the current plugin instance.
/// </summary>
internal static unsafe class StatusInterface
{
    private static RynthAiStatusApiV1* _table;
    private static IntPtr _out;   // the last string handed back (valid until the next call)

    public static IntPtr Query(string iface, uint version)
    {
        if (iface != RynthAiStatusApiV1.InterfaceName || version == 0 || version > RynthAiStatusApiV1.InterfaceVersion) return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthAiStatusApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthAiStatusApiV1));
            t->Version = RynthAiStatusApiV1.InterfaceVersion;
            t->StructSize = (uint)sizeof(RynthAiStatusApiV1);
            t->GetStatusJson = &GetStatusJson;
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

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetStatusJson(byte** json)
    {
        try
        {
            string? s = PluginExports.Runtime.Plugin?.BuildBotStatusJson();
            if (s == null || json == null) return 0;
            IntPtr fresh = Marshal.StringToCoTaskMemUTF8(s);
            IntPtr old = _out;
            _out = fresh;
            if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
            *json = (byte*)fresh;
            return 1;
        }
        catch
        {
            return 0;
        }
    }
}

public sealed partial class RynthAiPlugin
{
    /// <summary>{macro,action,state,combat,buff,nav,loot,meta,navFile,metaFile,lootFile}; null before login.</summary>
    internal string? BuildBotStatusJson()
    {
        var st = _dashboard?.Settings;
        if (st == null || !_loginComplete) return null;
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("macro", st.IsMacroRunning);
            w.WriteString("action", ActivityArbiter.DisplayLabel(st.BotAction, st.BuffingLabel));   // display (RynthNet)
            w.WriteString("state", st.CurrentState ?? "");
            w.WriteBoolean("combat", st.EnableCombat);
            w.WriteBoolean("buff", st.EnableBuffing);
            w.WriteBoolean("nav", st.EnableNavigation);
            w.WriteBoolean("loot", st.EnableLooting);
            w.WriteBoolean("meta", st.EnableMeta);
            w.WriteString("navFile", FileName(st.CurrentNavPath));
            w.WriteString("metaFile", FileName(st.CurrentMetaPath));
            w.WriteString("lootFile", FileName(st.CurrentLootPath));
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string FileName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFileName(path); }
        catch { return ""; }
    }
}

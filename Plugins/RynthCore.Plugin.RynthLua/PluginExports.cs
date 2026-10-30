using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthLua;

public static unsafe class PluginExports
{
    internal static readonly RynthPluginRuntime<RynthLuaPlugin> Runtime = new();

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
        try { return RynthLuaPlugin.NamePointer; }   // a static initializer that throws surfaces here (TypeInitializationException)
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion()
    {
        try { return RynthLuaPlugin.VersionPointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatWindowText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag) => Runtime.OnChatWindowText(textUtf16, chatType, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnSelectedTargetChange", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId) => Runtime.OnSelectedTargetChange(currentTargetId, previousTargetId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCreateObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCreateObject(uint objectId) => Runtime.OnCreateObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnDeleteObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnDeleteObject(uint objectId) => Runtime.OnDeleteObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnViewObjectContents", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnViewObjectContents(uint objectId) => Runtime.OnViewObjectContents(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnStopViewingObjectContents", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnStopViewingObjectContents(uint objectId) => Runtime.OnStopViewingObjectContents(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnVendorOpen", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnVendorOpen(uint vendorId) => Runtime.OnVendorOpen(vendorId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnVendorClose", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnVendorClose(uint vendorId) => Runtime.OnVendorClose(vendorId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCombatModeChange", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCombatModeChange(int currentCombatMode, int previousCombatMode) => Runtime.OnCombatModeChange(currentCombatMode, previousCombatMode);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUpdateHealth", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth) => Runtime.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCombatDamage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCombatDamage(uint damage, uint damageType, uint crit, uint isAttacker) => Runtime.OnCombatDamage(damage, damageType, crit, isAttacker);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnKillNotification", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnKillNotification(IntPtr textUtf16) => Runtime.OnKillNotification(textUtf16);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnEnchantmentAdded", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEnchantmentAdded(uint spellId, double durationSeconds) => Runtime.OnEnchantmentAdded(spellId, durationSeconds);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnEnchantmentRemoved", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEnchantmentRemoved(uint enchantmentId) => Runtime.OnEnchantmentRemoved(enchantmentId);

    // Plugin commands (host SendPluginCommand, v64): RynthAi forwards "/ra lua ..." here as
    // ("lua", "run X") so metas' chat actions keep working. Copied, applied on the next tick.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginApplyRemoteCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ApplyRemoteCommand(IntPtr actionAnsi, IntPtr valueAnsi)
    {
        try
        {
            string action = actionAnsi == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(actionAnsi) ?? "";
            string value = valueAnsi == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(valueAnsi) ?? "";
            Runtime.Plugin?.ApplyRemoteCommand(action, value);
        }
        catch { }
    }

    // ── Lua panel (engine UI/Data/LuaData.cs) ────────────────────────────────
    // Same export names the panel used on RynthAi; the engine now asks RynthLua.
    // Called on the plugin pump thread (UiDataHub runs right after TickAll).

    private static IntPtr _luaPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetLuaJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetLuaJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildLuaJson() ?? "{}";
            IntPtr fresh = Marshal.StringToCoTaskMemUTF8(json);
            IntPtr old = Interlocked.Exchange(ref _luaPtr, fresh);
            if (old != IntPtr.Zero) Marshal.FreeCoTaskMem(old);
            return fresh;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSendLuaCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SendLuaCommand(IntPtr utf8Json)
    {
        try
        {
            if (utf8Json == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringUTF8(utf8Json);
            if (string.IsNullOrEmpty(json)) return;
            var cmd = JsonSerializer.Deserialize(json, RynthLuaJsonContext.Default.LuaCommand);
            if (cmd != null) Runtime.Plugin?.EnqueueLuaCommand(cmd);
        }
        catch { }
    }
}

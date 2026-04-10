using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public static unsafe class PluginExports
{
    private static readonly RynthPluginRuntime<RynthAiPlugin> Runtime = new();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown() => Runtime.Shutdown();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUIInitialized", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUIInitialized() => Runtime.OnUIInitialized();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnBarAction", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnBarAction() => Runtime.OnBarAction();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnSelectedTargetChange", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId) => Runtime.OnSelectedTargetChange(currentTargetId, previousTargetId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName() => RynthAiPlugin.NamePointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion() => RynthAiPlugin.VersionPointer;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRender", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Render() => Runtime.OnRender();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatBarEnter", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatBarEnter(IntPtr textUtf16, IntPtr eatFlag) => Runtime.OnChatBarEnter(textUtf16, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnChatWindowText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnChatWindowText(IntPtr textUtf16, int chatType, IntPtr eatFlag) => Runtime.OnChatWindowText(textUtf16, chatType, eatFlag);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCreateObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCreateObject(uint objectId) => Runtime.OnCreateObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnDeleteObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnDeleteObject(uint objectId) => Runtime.OnDeleteObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUpdateObject", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUpdateObject(uint objectId) => Runtime.OnUpdateObject(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUpdateObjectInventory", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUpdateObjectInventory(uint objectId) => Runtime.OnUpdateObjectInventory(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnViewObjectContents", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnViewObjectContents(uint objectId) => Runtime.OnViewObjectContents(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnStopViewingObjectContents", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnStopViewingObjectContents(uint objectId) => Runtime.OnStopViewingObjectContents(objectId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnBusyCountIncremented", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnBusyCountIncremented() => Runtime.OnBusyCountIncremented();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnBusyCountDecremented", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnBusyCountDecremented() => Runtime.OnBusyCountDecremented();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnSmartBoxEvent", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnSmartBoxEvent(uint opcode, uint blobSize, uint status) => Runtime.OnSmartBoxEvent(opcode, blobSize, status);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCombatModeChange", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCombatModeChange(int currentCombatMode, int previousCombatMode) => Runtime.OnCombatModeChange(currentCombatMode, previousCombatMode);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUpdateHealth", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUpdateHealth(uint targetId, float healthRatio, uint currentHealth, uint maxHealth) => Runtime.OnUpdateHealth(targetId, healthRatio, currentHealth, maxHealth);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnEnchantmentAdded", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEnchantmentAdded(uint spellId, double durationSeconds) => Runtime.OnEnchantmentAdded(spellId, durationSeconds);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnEnchantmentRemoved", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEnchantmentRemoved(uint enchantmentId) => Runtime.OnEnchantmentRemoved(enchantmentId);
}

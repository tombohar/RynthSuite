using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using RynthCore.Plugin.RynthAi.LegacyUi;
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

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

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

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnVendorOpen", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnVendorOpen(uint vendorId) => Runtime.OnVendorOpen(vendorId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnVendorClose", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnVendorClose(uint vendorId) => Runtime.OnVendorClose(vendorId);

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

    // ── Snapshot bridge for the engine-side Avalonia RynthAi panel ──────────
    // The panel polls these exports every ~250ms to mirror the live dashboard.
    // The returned ANSI buffer is freed on the next call; the caller MUST copy
    // its string before calling any of these again.

    private static IntPtr _snapshotPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSnapshotJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSnapshotJson()
    {
        try
        {
            string json = Runtime.Plugin?.DashboardRenderer?.BuildSnapshotJson() ?? "{}";
            // Allocate the new buffer FIRST, then swap, then free the old.
            // The Avalonia panel polls this from a different thread; if we
            // freed before allocating, a poll mid-call would read freed
            // memory — which on .NET 10 NativeAOT silently returns garbage,
            // making the panel appear to "freeze" intermittently.
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _snapshotPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginToggleMacro", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ToggleMacro() => Runtime.Plugin?.DashboardRenderer?.TogglePanelMacro();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetSubsystemEnabled", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetSubsystemEnabled(int subsystemId, int enabled)
        => Runtime.Plugin?.DashboardRenderer?.SetSubsystemEnabled(subsystemId, enabled != 0);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSelectProfile", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SelectProfile(int kind, int index)
        => Runtime.Plugin?.DashboardRenderer?.SelectProfileAtIndex(kind, index);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginForceRebuff", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ForceRebuff() => Runtime.Plugin?.DashboardRenderer?.RequestForceRebuff();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginCancelForceRebuff", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void CancelForceRebuff() => Runtime.Plugin?.DashboardRenderer?.RequestCancelForceRebuff();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginAdjustOpacity", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void AdjustOpacity(float delta) => Runtime.Plugin?.DashboardRenderer?.AdjustOpacity(delta);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTogglePanelLock", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void TogglePanelLock() => Runtime.Plugin?.DashboardRenderer?.TogglePanelLock();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTogglePanelMinimize", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void TogglePanelMinimize() => Runtime.Plugin?.DashboardRenderer?.TogglePanelMinimize();

    // ── Radar bridge (engine-side Avalonia radar panel) ─────────────────────
    // Engine passes the MapVersion it has cached (0 on first call). When the
    // live landblock matches, walls/fills are omitted from the payload.

    private static IntPtr _radarPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetRadarSnapshot", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetRadarSnapshot(uint engineKnownMapVersion)
    {
        try
        {
            string json = Runtime.Plugin?.DashboardRenderer?.BuildRadarJson(engineKnownMapVersion) ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _radarPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // ── Monsters bridge (engine-side Avalonia MonstersPanel) ────────────────

    private static IntPtr _monstersPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetMonstersJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetMonstersJson()
    {
        try
        {
            string json = Runtime.Plugin?.DashboardRenderer?.BuildMonstersJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _monstersPtr, newPtr);
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
    /// Engine sends an ANSI null-terminated JSON string. Plugin replaces
    /// MonsterRules in-memory and writes monsters.json.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetMonstersJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetMonstersJson(IntPtr ansiJson)
    {
        try
        {
            if (ansiJson == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ansiJson);
            if (string.IsNullOrEmpty(json)) return;
            Runtime.Plugin?.DashboardRenderer?.ApplyMonstersJson(json);
        }
        catch { }
    }

    // ── Settings bridge (engine-side Avalonia SettingsPanel) ────────────────

    private static IntPtr _settingsPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSettingsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSettingsJson()
    {
        try
        {
            string json = Runtime.Plugin?.DashboardRenderer?.BuildSettingsJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _settingsPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetSettingsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetSettingsJson(IntPtr ansiJson)
    {
        try
        {
            if (ansiJson == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ansiJson);
            if (string.IsNullOrEmpty(json)) return;
            Runtime.Plugin?.DashboardRenderer?.ApplySettingsJson(json);
        }
        catch { }
    }

    // ── Nav bridge (engine-side Avalonia NavPanel) ──────────────────────────

    private static IntPtr _navPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetNavJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetNavJson()
    {
        try
        {
            string json = Runtime.Plugin?.DashboardRenderer?.BuildNavJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _navPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSendNavCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SendNavCommand(IntPtr ansiJson)
    {
        try
        {
            if (ansiJson == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ansiJson);
            if (string.IsNullOrEmpty(json)) return;
            var cmd = JsonSerializer.Deserialize(json, RynthAiJsonContext.Default.NavCommand);
            if (cmd?.Cmd == "dunPatrol")
                Runtime.Plugin?.HandleDungeonNavPatrol();
            else
                Runtime.Plugin?.DashboardRenderer?.HandleNavCommand(json);
        }
        catch { }
    }

    // ── Items bridge (engine-side Avalonia ItemsPanel) ──────────────────────

    private static IntPtr _itemsPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetItemsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetItemsJson()
    {
        try
        {
            string json = Runtime.Plugin?.DashboardRenderer?.BuildItemsJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _itemsPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetItemsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetItemsJson(IntPtr ansiJson)
    {
        try
        {
            if (ansiJson == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ansiJson);
            if (string.IsNullOrEmpty(json)) return;
            Runtime.Plugin?.DashboardRenderer?.ApplyItemsJson(json);
        }
        catch { }
    }

    // ── Meta bridge (engine-side Avalonia MetaPanel) ────────────────────────

    private static IntPtr _metaPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetMetaJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetMetaJson()
    {
        try
        {
            string json = Runtime.Plugin?.DashboardRenderer?.BuildMetaJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _metaPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSendMetaCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SendMetaCommand(IntPtr ansiJson)
    {
        try
        {
            if (ansiJson == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringAnsi(ansiJson);
            if (string.IsNullOrEmpty(json)) return;
            Runtime.Plugin?.DashboardRenderer?.HandleMetaCommand(json);
        }
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginAddSelectedWeapon", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void AddSelectedWeapon()
    {
        try { Runtime.Plugin?.DashboardRenderer?.AddSelectedWeapon(); }
        catch { }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginAddSelectedConsumable", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void AddSelectedConsumable()
    {
        try { Runtime.Plugin?.DashboardRenderer?.AddSelectedConsumable(); }
        catch { }
    }
}

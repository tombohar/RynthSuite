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

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnCombatDamage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCombatDamage(uint damage, uint damageType, uint crit, uint isAttacker) => Runtime.OnCombatDamage(damage, damageType, crit, isAttacker);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnKillNotification", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnKillNotification(IntPtr textUtf16) => Runtime.OnKillNotification(textUtf16);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnEnchantmentAdded", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEnchantmentAdded(uint spellId, double durationSeconds) => Runtime.OnEnchantmentAdded(spellId, durationSeconds);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnEnchantmentRemoved", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnEnchantmentRemoved(uint enchantmentId) => Runtime.OnEnchantmentRemoved(enchantmentId);

    // ── Snapshot bridge for the engine-side Avalonia RynthAi panel ──────────
    // The panel polls these exports every ~250ms to mirror the live dashboard.
    // The returned ANSI buffer is freed on the next call; the caller MUST copy
    // its string before calling any of these again.

    // [ThreadStatic]: this snapshot is polled concurrently from MULTIPLE threads — the engine's Avalonia
    // RynthAiPanel (~Hz), the heartbeat status-export (1/s), and (Phase C) the RynthRemote plugin via the
    // engine's GetPluginSnapshotJson broker. With a single shared pointer the alloc-new→swap→free-old below
    // races: one caller frees the buffer another is mid-read → garbage ('0x18' JsonException) / use-after-free.
    // Per-thread storage gives each caller its own buffer (same discipline as the engine's account-name
    // scratch), so no caller ever frees another thread's pointer. (No initializer — [ThreadStatic] defaults
    // to IntPtr.Zero on every thread.)
    [ThreadStatic] private static IntPtr _snapshotPtr;

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

    // ── Full-inventory bridge (read-only remote inventory viewer, P1) ───────────
    // Dedicated export (NOT folded into the 150ms status snapshot) so 100s of items never ride the
    // hot path. The RynthRemote plugin polls this on its own slower cadence (P2). Per-thread buffer
    // (same reasoning as _snapshotPtr): polled from multiple threads; never free another's pointer.
    [ThreadStatic] private static IntPtr _inventoryPtr;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetInventoryJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetInventoryJson()
    {
        try
        {
            string json = Runtime.Plugin?.DashboardRenderer?.BuildInventoryJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _inventoryPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // ── Remote-command bridge (engine SendPluginCommand broker → this plugin) ──
    // When RynthRemote owns the command drain, the engine forwards each phone-issued
    // (action,value) command here. We COPY the ANSI args (the caller frees them right
    // after this returns) and enqueue them for the pump thread (RynthAiPlugin.OnTick →
    // ApplyRemoteCommand); we NEVER apply on the caller's thread. The whole body is
    // guarded — a managed exception must not cross the native boundary.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginApplyRemoteCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ApplyRemoteCommand(IntPtr actionAnsi, IntPtr valueAnsi)
    {
        try
        {
            string action = actionAnsi != IntPtr.Zero ? (Marshal.PtrToStringAnsi(actionAnsi) ?? string.Empty) : string.Empty;
            string value  = valueAnsi  != IntPtr.Zero ? (Marshal.PtrToStringAnsi(valueAnsi)  ?? string.Empty) : string.Empty;
            if (action.Length == 0) return;
            Runtime.Plugin?.EnqueueRemoteCommand(action, value);
        }
        catch { /* never let a managed exception cross the native boundary */ }
    }

    // Every void action export below is a reverse-P/Invoke (UnmanagedCallersOnly)
    // boundary: a managed exception escaping one fail-fasts the NativeAOT runtime
    // (no dump, no log — the "illegal exception" crash class). Funnel them through
    // this guard so a UI click that races plugin state (e.g. SelectProfile indexing
    // a profile list mid-Refresh) can never kill the AC client.
    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch { /* never let a managed exception cross the native boundary */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginToggleMacro", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ToggleMacro() => SafeInvoke(() => Runtime.Plugin?.DashboardRenderer?.TogglePanelMacro());

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetSubsystemEnabled", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetSubsystemEnabled(int subsystemId, int enabled)
        => SafeInvoke(() => Runtime.Plugin?.DashboardRenderer?.SetSubsystemEnabled(subsystemId, enabled != 0));

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSelectProfile", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SelectProfile(int kind, int index)
        => SafeInvoke(() => Runtime.Plugin?.DashboardRenderer?.SelectProfileAtIndex(kind, index));

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginForceRebuff", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ForceRebuff() => SafeInvoke(() => Runtime.Plugin?.DashboardRenderer?.RequestForceRebuff());

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginCancelForceRebuff", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void CancelForceRebuff() => SafeInvoke(() => Runtime.Plugin?.DashboardRenderer?.RequestCancelForceRebuff());

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginAdjustOpacity", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void AdjustOpacity(float delta) => SafeInvoke(() => Runtime.Plugin?.DashboardRenderer?.AdjustOpacity(delta));

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTogglePanelLock", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void TogglePanelLock() => SafeInvoke(() => Runtime.Plugin?.DashboardRenderer?.TogglePanelLock());

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTogglePanelMinimize", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void TogglePanelMinimize() => SafeInvoke(() => Runtime.Plugin?.DashboardRenderer?.TogglePanelMinimize());

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
    private static IntPtr _monsterDamagePtr = IntPtr.Zero;

    // Live per-monster casts-to-kill table (engine MonsterDamagePanel polls this).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetMonsterDamageText", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetMonsterDamageText()
    {
        try
        {
            string text = Runtime.Plugin?.BuildMonsterDamageText() ?? "";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(text);
            IntPtr oldPtr = Interlocked.Exchange(ref _monsterDamagePtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr _monsterDamageJsonPtr = IntPtr.Zero;

    // Structured per-monster rows for the interactive Damage panel (UTF-8/ANSI JSON).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetMonsterDamageJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetMonsterDamageJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildMonsterDamageJson() ?? "[]";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _monsterDamageJsonPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // Manual HP override from the Damage panel (hp <= 0 clears it).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetMonsterHp", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetMonsterHp(uint wcid, int hp)
    {
        try { Runtime.Plugin?.SetMonsterHp(wcid, hp); } catch { }
    }

    // Delete one learned row. keyUtf8 = ANSI "wcid:weaponId:element:tier". Returns 1 on success.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginDeleteMonsterRow", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int DeleteMonsterRow(IntPtr keyUtf8)
    {
        try
        {
            string? key = Marshal.PtrToStringAnsi(keyUtf8);
            return (Runtime.Plugin?.DeleteMonsterRow(key ?? "") ?? false) ? 1 : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static IntPtr _combatWeaponsPtr = IntPtr.Zero;

    // Selectable weapons for the Damage-panel weapon/offhand pickers (ANSI JSON array [{id,name}]).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetCombatWeaponsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetCombatWeaponsJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildCombatWeaponsJson() ?? "[]";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _combatWeaponsPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // Per-monster weapon override from the Damage panel (weaponId == 0 clears → use learned best).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetMonsterWeapon", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetMonsterWeapon(uint wcid, uint weaponId)
    {
        try { Runtime.Plugin?.SetMonsterWeapon(wcid, weaponId); } catch { }
    }

    // Per-monster offhand override from the Damage panel (offhandId == 0 clears). Stored only.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetMonsterOffhand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetMonsterOffhand(uint wcid, uint offhandId)
    {
        try { Runtime.Plugin?.SetMonsterOffhand(wcid, offhandId); } catch { }
    }

    // Per-character DEFAULT weapon from the Damage panel's Default line (weaponId == 0 clears →
    // monsters on Default fall through to learned-best). Sweeping fallback for all Default monsters.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetDefaultWeapon", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetDefaultWeapon(uint weaponId)
    {
        try { Runtime.Plugin?.SetDefaultWeapon(weaponId); } catch { }
    }

    // Master reset: zero all learned damage stats, keep monster names + manual overrides.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginClearMonsterStats", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ClearMonsterStats()
    {
        try { Runtime.Plugin?.ClearMonsterStats(); } catch { }
    }

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
            switch (cmd?.Cmd)
            {
                case "dunPatrol":       Runtime.Plugin?.HandleDungeonNavPatrol();              break;
                case "clearHazards":    Runtime.Plugin?.ClearDungeonHazards(cmd.NavName);      break;
                case "clearHazardsAll": Runtime.Plugin?.ClearAllDungeonHazards();              break;
                case "markHazardHere":  Runtime.Plugin?.MarkCurrentCellHazardFromUi();         break;
                case "unmarkHazardHere":Runtime.Plugin?.UnmarkCurrentCellHazardFromUi();       break;
                default:                Runtime.Plugin?.DashboardRenderer?.HandleNavCommand(json); break;
            }
        }
        catch { }
    }

    private static IntPtr _patrolInfoPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetPatrolInfoJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetPatrolInfoJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildPatrolInfoJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _patrolInfoPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
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

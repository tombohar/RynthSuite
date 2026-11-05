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
    internal static readonly RynthPluginRuntime<RynthAiPlugin> Runtime = new();

    // Typed interfaces for other plugins (host GetPluginInterface, API v68): "RynthAi.Script" v1
    // for RynthLua, "RynthAi.Status" v1 for RynthNet (the bot state it shares with the other clients),
    // "RynthAi.Activity" v1 for RynthSort (what the bot is doing, so pack moves stay out of its way),
    // "RynthAi.Combat" v1 for RynthLua (a script choosing attack and heal spells, Shared/RynthAiCombatApi.cs),
    // "RynthAi.Hub" v1 for RynthILT (the Mini Remote hosting its ILT tabs, /myquests, spell names, loot
    // matching, the .usd importer, session stats; Shared/RynthAiHubApi.cs, HubInterface.cs),
    // "RynthAi.Yield" v1 for RynthDefective (the player's key press goes before RynthAi's next action;
    // Shared/RynthAiYieldApi.cs, YieldInterface.cs).
    // Fixed signature: void* (const char* iface, uint version).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginQueryInterface", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr QueryInterface(IntPtr ifaceAnsi, uint version)
    {
        try
        {
            string iface = ifaceAnsi == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ifaceAnsi) ?? "";
            IntPtr table = ScriptInterface.Query(iface, version);
            if (table == IntPtr.Zero) table = StatusInterface.Query(iface, version);
            if (table == IntPtr.Zero) table = ActivityInterface.Query(iface, version);
            if (table == IntPtr.Zero) table = HubInterface.Query(iface, version);
            if (table == IntPtr.Zero) table = YieldInterface.Query(iface, version);
            return table != IntPtr.Zero ? table : CombatHookInterface.Query(iface, version);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginInit", CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Init(RynthCoreApiNative* api) => Runtime.Init(api);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginShutdown", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Shutdown()
    {
        // An exception escaping an UnmanagedCallersOnly export fail-fasts the whole client under
        // NativeAOT. Each step is guarded on its own so one failure cannot skip the rest.
        RynthAiPlugin? plugin = Runtime.Plugin;   // Runtime.Shutdown drops it; the log needs its host
        try { Runtime.Shutdown(); }
        catch (Exception ex) { LogShutdownError(plugin, "Runtime.Shutdown", ex); }
        try { StatusInterface.Release(); }
        catch (Exception ex) { LogShutdownError(plugin, "StatusInterface.Release", ex); }
        try { ActivityInterface.Release(); }
        catch (Exception ex) { LogShutdownError(plugin, "ActivityInterface.Release", ex); }
        try { YieldInterface.Release(); }
        catch (Exception ex) { LogShutdownError(plugin, "YieldInterface.Release", ex); }
        try { HubInterface.Release(); }   // Version 0; the table stays (RynthILT's render thread may hold it)
        catch (Exception ex) { LogShutdownError(plugin, "HubInterface.Release", ex); }
        try { CombatHookInterface.Shutdown(); }   // the hook goes native at once (RynthLua re-claims after a reload)
        catch (Exception ex) { LogShutdownError(plugin, "CombatHookInterface.Shutdown", ex); }
        try
        {
            // The loot editor's state can be a few hundred KB (LootSnobV4): don't leave it behind on a
            // reload. The sections manifest goes too.
            foreach (IntPtr p in new[] { Interlocked.Exchange(ref _lootStatePtr, IntPtr.Zero),
                         Interlocked.Exchange(ref _lootRulePtr, IntPtr.Zero), Interlocked.Exchange(ref _lootVocabPtr, IntPtr.Zero),
                         Interlocked.Exchange(ref _sectionsPtr, IntPtr.Zero) })
                if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
            _sectionsJson = null;
        }
        catch (Exception ex) { LogShutdownError(plugin, "loot buffers", ex); }
    }

    private static void LogShutdownError(RynthAiPlugin? plugin, string step, Exception ex)
    {
        try { plugin?.LogFromExport($"[RynthAi] Shutdown: {step} failed: {ex.GetType().Name}: {ex.Message}"); }
        catch { /* nothing more to do: never throw out of a native export */ }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnUIInitialized", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnUIInitialized() => Runtime.OnUIInitialized();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLoginComplete", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLoginComplete() => Runtime.OnLoginComplete();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnLogout", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnLogout() => Runtime.OnLogout();

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnSelectedTargetChange", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnSelectedTargetChange(uint currentTargetId, uint previousTargetId) => Runtime.OnSelectedTargetChange(currentTargetId, previousTargetId);

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginName", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetName()
    {
        try { return RynthAiPlugin.NamePointer; }   // a static initializer that throws surfaces here (TypeInitializationException)
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginVersion", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetVersion()
    {
        try { return RynthAiPlugin.VersionPointer; }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginTick", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Tick() => Runtime.OnTick();

    // Local ImGui windows (Mini Remote, HUDs, translator, item info, nav overlay). The engine
    // calls this when the export exists; the main panels stay on the engine side.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRender", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void Render() => Runtime.OnRender();

    /// <summary>Engine calls this instead of RynthPluginRender while its ImGui shell is off.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderOverlay", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void RenderOverlay()
    {
        try { Runtime.Plugin?.OnRenderOverlay(); }
        catch { /* an exception crossing UnmanagedCallersOnly would fail-fast the client */ }
    }

    /// <summary>
    /// Engine dashboard drawers (RynthCore 2026.10.5.21+): draws the named UTF-8 surface into the
    /// engine's current ImGui window on the render thread and reports the size it wants.
    /// Surfaces: "miniremote" (the RynthAi dashboard's Mini Remote drawer) and "miniremote-window"
    /// (the engine's Mini Remote window, which hosts the remote with its Toggles, its tabs and
    /// RynthILT's ILT tabs). The Guardian ILT panel ("guardian") is RynthILT's since 2026-10-13.
    /// RynthCore 2026.10.5.30+ adds ":popout" to the name when the panel is popped out into its
    /// own window, whose ImGui context is not the one in Host.ImGuiContext; the surfaces draw
    /// in the engine's current context either way (RenderEmbedded*).
    /// RynthCore 2026.10.5.46+ (Mini Remote sections): "miniremote-remote" (the remote without its
    /// tab bar, the engine's section rail replaces it), "miniremote-host" (the engine's Mini Remote
    /// is open on another section; draws nothing) and "section:&lt;name&gt;" (the views
    /// RynthPluginGetSectionsJson lists).
    /// 1 = drawn, 0 = not ready / unknown (nothing drawn).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginRenderEmbed", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int RenderEmbed(byte* surface, float width, float height, float* wantWidth, float* wantHeight)
    {
        try
        {
            if (surface == null) return 0;
            ReadOnlySpan<byte> name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(surface);
            bool popped = name.EndsWith(":popout"u8);
            if (popped) name = name[..^":popout"u8.Length];
            var plugin = Runtime.Plugin;
            if (plugin == null) return 0;
            System.Numerics.Vector2 want;
            var size = new System.Numerics.Vector2(width, height);
            if (name.SequenceEqual("miniremote-host"u8))
            {
                return plugin.MarkEngineMiniRemote() ? 1 : 0;
            }
            else if (name.SequenceEqual("window:dashboard"u8))
            {
                // RynthAi's own dashboard window (RynthAiPlugin.Dashboard.cs): 1 drawn, 0 not ready, -2 failed.
                int drawn = plugin.RenderDashboard(popped, out want);
                if (drawn != 1) return drawn;
            }
            else if (name.SequenceEqual("window:monsterstats"u8))
            {
                // The Monster Stats window (RynthAiPlugin.MonsterStats.cs): 1 drawn, 0 not ready, -2 failed.
                int drawn = plugin.RenderMonsterStats(popped, out want);
                if (drawn != 1) return drawn;
            }
            else if (name.StartsWith("section:"u8))
            {
                // Section names are short ASCII ids (Huds/SectionsManifest.cs); anything longer isn't ours.
                if (name.Length > 64) return 0;
                if (!plugin.RenderEmbeddedSection(System.Text.Encoding.UTF8.GetString(name), size, out want)) return 0;
            }
            else
            {
                // "guardian" is RynthILT's (since 2026-10-13): not answered here.
                bool remote = name.SequenceEqual("miniremote-remote"u8);
                bool window = remote || name.SequenceEqual("miniremote-window"u8);
                if (!window && !name.SequenceEqual("miniremote"u8)) return 0;
                if (!plugin.RenderEmbeddedMiniRemote(window, out want, tabs: !remote)) return 0;
            }
            if (wantWidth != null) *wantWidth = want.X;
            if (wantHeight != null) *wantHeight = want.Y;
            return 1;
        }
        catch
        {
            return 0;   // an exception crossing UnmanagedCallersOnly would fail-fast the client
        }
    }

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

    // API v78 server messages: only what RynthAiPlugin asked for (SetServerMessageInterest in
    // Initialize: the transient-text game event, for "The door is locked!", and the four combat
    // events, for the Monster Stats). `data` lives only during the call; the parsers copy out what
    // they keep (the text, the monster's name).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginOnServerMessage", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnServerMessage(uint opcode, byte* data, int length)
    {
        try
        {
            var msg = RynthCore.PluginSdk.Net.ServerMessage.FromCallback(opcode, data, length);
            if (!msg.IsGameEvent) return;
            if (msg.EventType == RynthCore.PluginSdk.Net.GameEventType.TransientString
                && RynthCore.PluginSdk.Net.SystemText.TryParse(msg.EventBody, true, out var text))
                Runtime.Plugin?.NoteDoorServerText(text.Text);
            else if (MonsterStats.CombatEvents.IsCombatEvent(msg.EventType))
                Runtime.Plugin?.NoteCombatEvent(msg.EventType, msg.EventBody);
        }
        catch { }
    }

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

    // ── Hot-reload deferral (2026-10-05) ─────────────────────────────────────────
    // The engine calls this on its reload thread before it tears itself down for a hot reload:
    // a non-empty text ("in combat (a monster engaged, ...)") makes it wait and ask again, up to
    // its cap. Fixed signature, const char* (void); null or "" = safe. Optional: an engine that
    // doesn't know it simply reloads, and a plugin without it never holds a reload. Per-thread
    // buffer (same reasoning as _snapshotPtr).
    [ThreadStatic] private static IntPtr _reloadBlockerPtr;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginReloadBlocker", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr ReloadBlocker()
    {
        try
        {
            string reason = Runtime.Plugin?.ReloadBlocker ?? "";
            if (reason.Length == 0) return IntPtr.Zero;
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(reason);
            IntPtr oldPtr = _reloadBlockerPtr;
            _reloadBlockerPtr = newPtr;
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
    public static void ToggleMacro() => SafeInvoke(() => Runtime.Plugin?.ToggleMacroFromPanel());   // refuses a start while VTank runs

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
    private static IntPtr _monsterDetailJsonPtr = IntPtr.Zero;

    // One monster's breakdown for the Damage tab's detail panel (freed on the next call).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetMonsterDetailJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetMonsterDetailJson(uint wcid)
    {
        try
        {
            string json = Runtime.Plugin?.BuildMonsterDetailJson(wcid) ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _monsterDetailJsonPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

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
    private static IntPtr _difficultyTierJsonPtr = IntPtr.Zero;

    // Difficulty tiers (2026-10-03; a server plugin's values since 2026-10-21, CreatureData/DifficultyTier.cs):
    // the Damage panel's tier label and picker. {"show":false} while no server plugin reports a tier; else
    // {"show":true,"current":N,"view":M,"following":bool,"percent":P,"tiers":[0,...]} (P: health % a tier).
    // The export keeps the name the engine binds ("...AwakenedTierJson", its first server's word).
    // An engine without the picker never calls this; the damage JSON then shows the current tier.
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetAwakenedTierJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetDifficultyTierJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildDifficultyTierJson() ?? "{\"show\":false}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _difficultyTierJsonPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // The tier the damage JSON and monster detail describe: -1 = the current one (default).
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetDamageViewTier", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetDamageViewTier(int tier)
    {
        try { Runtime.Plugin?.SetDamageViewTier(tier); } catch { }
    }

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

    // Damage panel Pet column: picker entries (Auto, elements, Items-panel essences)
    // and the per-monster choice ("" = Auto, "E:<element>", "I:<essence id>").
    private static IntPtr _petChoicesPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetPetChoicesJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetPetChoicesJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildPetChoicesJson() ?? "[]";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _petChoicesPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSetMonsterPet", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SetMonsterPet(uint wcid, IntPtr ansiChoice)
    {
        try
        {
            string choice = ansiChoice == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ansiChoice) ?? "";
            Runtime.Plugin?.SetMonsterPet(wcid, choice);
        }
        catch { }
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
            // Nothing until the character's profile is loaded (not "{}": the engine read that as a full
            // settings copy of its own defaults, and its next save wrote them over the profile, 2026-11-05).
            var renderer = Runtime.Plugin?.DashboardRenderer;
            if (renderer == null || !renderer.ProfileLoaded) return IntPtr.Zero;
            string json = renderer.BuildSettingsJson();
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
                // The Dungeon Map's edits (click to add, drag to move, set next target).
                case "addPointAt":
                case "movePointTo":
                case "setActive":       Runtime.Plugin?.DashboardRenderer?.HandleMapNavCommand(cmd); break;
                default:                Runtime.Plugin?.DashboardRenderer?.HandleNavCommand(json); break;
            }
        }
        catch { }
    }

    // ── Item Tool (the engine's Item Tool panel: chest looter + Quick give) ─────────────────
    // The panel polls the status JSON and posts its buttons' words. The command export is the
    // panel's alone: a Quick give started through it is a button Tom pressed (the plugin treats
    // chat lines from metas, scripts and the remote differently, RynthAiPlugin.ItemTool.cs).

    private static IntPtr _itemToolPtr = IntPtr.Zero;

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetItemToolJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetItemToolJson()
    {
        try
        {
            string json = Runtime.Plugin?.BuildItemToolJson() ?? "{}";
            IntPtr newPtr = Marshal.StringToHGlobalAnsi(json);
            IntPtr oldPtr = Interlocked.Exchange(ref _itemToolPtr, newPtr);
            if (oldPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(oldPtr);
            return newPtr;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginItemToolCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void ItemToolCommand(IntPtr ansiArgs)
    {
        try
        {
            string args = ansiArgs != IntPtr.Zero ? (Marshal.PtrToStringAnsi(ansiArgs) ?? string.Empty) : string.Empty;
            Runtime.Plugin?.QueueItemToolCommand(args.Trim(), trusted: true);
        }
        catch { /* never let a managed exception cross the native boundary */ }
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

    // ── Loot editor bridge (engine-side ImGui Loot Editor) ──────────────────
    // RynthCore docs/IMGUI_LOOT_EDITOR.md. Strings are UTF-8 both ways (the
    // bridges above are ANSI). Each Get frees its previous buffer on the next
    // call, so the engine's UI data hub is the only caller (plugin pump thread).

    private static IntPtr _lootStatePtr = IntPtr.Zero, _lootRulePtr = IntPtr.Zero, _lootVocabPtr = IntPtr.Zero;

    private static IntPtr Utf8Swap(ref IntPtr slot, string json)
    {
        IntPtr newPtr = Marshal.StringToCoTaskMemUTF8(json);
        IntPtr oldPtr = Interlocked.Exchange(ref slot, newPtr);
        if (oldPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(oldPtr);
        return newPtr;
    }

    private static IntPtr _sectionsPtr = IntPtr.Zero;
    private static string? _sectionsJson;

    /// <summary>
    /// RynthCore 2026.10.5.46+ (optional): what RynthAi adds to the Mini Remote's sections, as
    /// UTF-8 JSON (schema 1, Huds/SectionsManifest.cs). Null before login. The engine polls it on
    /// the render thread every couple of seconds; an unchanged manifest keeps the same buffer, a
    /// changed one frees the previous buffer (the engine copies the text before its next call).
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetSectionsJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetSectionsJson()
    {
        try
        {
            string? json = Runtime.Plugin?.BuildSectionsJson();
            if (json == null) return IntPtr.Zero;
            if (json == _sectionsJson && _sectionsPtr != IntPtr.Zero) return _sectionsPtr;
            _sectionsJson = json;
            return Utf8Swap(ref _sectionsPtr, json);
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>Changes whenever the editor's state does; 0 before RynthAi is ready.</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginLootEditRevision", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static int LootEditRevision()
    {
        try { return Runtime.Plugin?.LootEditor.Revision() ?? 0; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetLootEditJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetLootEditJson()
    {
        try
        {
            string? json = Runtime.Plugin?.LootEditor.StateJson();
            return json == null ? IntPtr.Zero : Utf8Swap(ref _lootStatePtr, json);
        }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetLootEditRuleJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetLootEditRuleJson(int index)
    {
        try
        {
            string? json = Runtime.Plugin?.LootEditor.RuleJson(index);
            return json == null ? IntPtr.Zero : Utf8Swap(ref _lootRulePtr, json);
        }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginGetLootEditVocabJson", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static IntPtr GetLootEditVocabJson()
    {
        try
        {
            string? json = Runtime.Plugin?.LootEditor.VocabJson();
            return json == null ? IntPtr.Zero : Utf8Swap(ref _lootVocabPtr, json);
        }
        catch { return IntPtr.Zero; }
    }

    [UnmanagedCallersOnly(EntryPoint = "RynthPluginSendLootEditCommand", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void SendLootEditCommand(IntPtr utf8Json)
    {
        try
        {
            if (utf8Json == IntPtr.Zero) return;
            string? json = Marshal.PtrToStringUTF8(utf8Json);
            if (string.IsNullOrEmpty(json)) return;
            Runtime.Plugin?.LootEditor.Command(json);
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

    /// <summary>Items panel "Remove Missing": drops weapons and consumables no longer in the inventory (reports in chat).</summary>
    [UnmanagedCallersOnly(EntryPoint = "RynthPluginCleanItems", CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void CleanItems()
    {
        try { Runtime.Plugin?.CleanMissingItems(); }
        catch { }
    }
}

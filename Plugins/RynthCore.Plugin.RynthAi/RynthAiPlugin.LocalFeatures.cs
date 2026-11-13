// RynthAiPlugin.LocalFeatures.cs — the Mini Remote's universal tabs, floating HUDs, chat
// translation, item info, the nav overlay and the UB damage seed. (The ILT Hub moved to the RynthILT
// plugin 2026-10-13: its tabs come through RynthILT.Hub, MiniRemote/ServerHubClient.cs, and RynthILT
// borrows from RynthAi through RynthAi.Hub, HubInterface.cs.)
//
// The engine draws the main panels. These features draw their own ImGui windows from
// RynthPluginRender / RynthPluginRenderOverlay. Lifecycle methods on RynthAiPlugin call into
// this file; they add to combat, loot and navigation and never replace them.
using System;
using System.Collections.Generic;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

public sealed partial class RynthAiPlugin
{
    private NavBreadcrumbTracker? _navBreadcrumbs;
    private NavOverlayRenderer? _navOverlay;
    private MiniRemote.MiniRemoteController? _miniRemote;
    // The server plugin's side of the Mini Remote (its tabs, sections, feature bits, verbs; RynthILT on ILT); lives for the plugin.
    private MiniRemote.ServerHubClient? _serverHub;
    private Huds.HudController? _huds;
    private Huds.HudIconCache? _hudIcons;
    private Translate.ChatTranslator? _translator;
    private Translate.TranslateUi? _translateUi;
    private LegacyItemInfoUi? _itemInfoUi;
    private CreatureData.UbMobSeedStore? _mobSeedStore;
    // Mini Remote session stats: RynthTracker's SessionStats (Plugins\Shared), so the Mini Remote and
    // the Tracker panel count the same way. Lives for the plugin; starts and ends with each login.
    private RynthCore.Plugin.Shared.SessionStatsTracker? _sessionStats;

    // One-time overlay diagnostics (render thread only).
    private string? _overlaySkipReasonLogged;
    private bool _overlayEnteredLogged;

    /// <summary>Daily diagnostics log. Feature code writes through <see cref="RynthLog"/>.</summary>
    private void InitLocalDiagnostics()
    {
        try { RynthLog.Init(Host); }
        catch (Exception ex) { Host.Log($"[RynthAi] diagnostics init failed: {ex.Message}"); }
        try { _mobSeedStore = new CreatureData.UbMobSeedStore(); }
        catch (Exception ex) { Host.Log($"[RynthAi] mob seed store failed: {ex.Message}"); }
    }

    /// <summary>Session stats for the Mini Remote, saved in their own folder (never RynthTracker's).</summary>
    private void InitSessionStats()
    {
        try
        {
            _sessionStats = new RynthCore.Plugin.Shared.SessionStatsTracker(Host, "[RynthAi] Mini Remote stats:",
                RynthCore.Plugin.Shared.TrackerSessionStore.DirectoryFor("miniremote-stats"));
        }
        catch (Exception ex) { Host.Log($"[RynthAi] session stats init failed: {ex.Message}"); }
    }

    /// <summary>
    /// CombatManager.MobSeedLookup: the seed's damage types for a monster, best first (highest
    /// Score), or null when the seed has nothing for it. Feeds the Auto element ranking only
    /// after the server table and the learned element (see CreatureWeakness.Rank).
    /// </summary>
    private IReadOnlyList<string>? LookupMobSeed(uint wcid, string name)
    {
        var store = _mobSeedStore;
        if (store == null || !store.TryGetRanked(wcid, name, out var ranked) || ranked.Count == 0)
            return null;
        var ordered = new List<RynthCore.CreatureSeed.UbMobSeedElement>(ranked);
        ordered.Sort((a, b) => b.Score.CompareTo(a.Score));
        var elements = new List<string>(ordered.Count);
        foreach (var e in ordered) elements.Add(e.Element);
        return elements;
    }

    /// <summary>Chat translator survives logout; session language resets on each login.</summary>
    private void EnsureTranslator()
    {
        try
        {
            if (_translator == null)
            {
                _translator = new Translate.ChatTranslator(Host, CurrentCharacterName);
                _translateUi = new Translate.TranslateUi(_translator);
            }
            _translator.ResetSession();
            if (_huds != null) _huds.Translate = _translateUi;
        }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, "translator init"); }
    }

    /// <summary>The logged-in character's name, or empty before login.</summary>
    private string CurrentCharacterName()
        => _playerId != 0 && Host.HasGetObjectName && Host.TryGetObjectName(_playerId, out string name) ? name ?? string.Empty : string.Empty;

    /// <summary>Waypoint HUD, labels, guide line, and the breadcrumb recorder.</summary>
    private void CreateNavOverlay()
    {
        if (_dashboard == null) return;
        _navBreadcrumbs = new NavBreadcrumbTracker(Host, _dashboard.Settings, DescribeNavPortal, ChatLine);
        _navOverlay = new NavOverlayRenderer(Host, _dashboard.Settings, _navBreadcrumbs);
        _dashboard.NavBreadcrumbs = _navBreadcrumbs;   // Nav panel: trail -> route, trail stats
    }

    /// <summary>Nav recorder hook: (name, object class) when <paramref name="id"/> is a portal.</summary>
    private (string Name, int ObjectClass)? DescribeNavPortal(uint id)
    {
        WorldObject? wo = _objectCache?[unchecked((int)id)];
        if (wo == null || wo.ObjectClass != AcObjectClass.Portal || string.IsNullOrEmpty(wo.Name)) return null;
        return (wo.Name, (int)AcObjectClass.Portal);
    }

    /// <summary>
    /// The Mini Remote's universal tabs and the floating HUDs for the loaded character. No-op until
    /// the per-character folder exists, so login and the deferred settings load in OnTick both call this.
    /// </summary>
    private void CreateMiniRemote()
    {
        if (_dashboard == null || string.IsNullOrEmpty(_dashboard.CharFolder) || _miniRemote != null)
            return;

        var dash = _dashboard;
        _serverHub ??= new MiniRemote.ServerHubClient(Host);
        // The engine's Settings window mirrors the server plugin's own settings (RynthILT's Vaulted Dungeons switch).
        var ilt = _serverHub;
        dash.ServerSettingGet = key => ilt.Setting(key);
        dash.ServerSettingSet = (key, value) => ilt.SetSetting(key, value);
        _miniRemote = new MiniRemote.MiniRemoteController(Host, dash.CharFolder,
            () => _objectCache, () => dash.Settings, () => _questTracker,
            () => dash.SaveSettings(), _serverHub);
        _hudIcons ??= new Huds.HudIconCache(Host, () => _raycast?.GeometryLoader?.PortalDat);
        _huds = new Huds.HudController(Host, dash.CharFolder, () => _objectCache, dash, () => _miniRemote, _hudIcons,
            () => _combatManager?.activeTargetId ?? 0)
        {
            SessionStats = _sessionStats,
            ToggleMonsterStats = MonsterStatsWindowWanted ? ToggleMonsterStatsWindow : null,
        };
        if (_translateUi != null) _huds.Translate = _translateUi;

        // "/ra hub [show|hide|toggle]" drives the remote, "/ra hub open <tab>" its tabs.
        var huds = _huds;
        _miniRemote.MiniRemoteCommand = mode => "[Mini Remote] " + huds.HandleCommand("remote", mode);
        _miniRemote.MiniRemoteTabCommand = (tab, mode) => huds.HandleTabCommand(tab, mode);
        EnsureItemInfoUi();
        EnsureSpellsUi();
    }

    // ── RynthAi.Hub (HubInterface.cs): what RynthILT borrows ────────────────

    /// <summary>The Mini Remote on a tab for RynthILT (any thread): show / hide / toggle the remote for tab "", else the tab. Null before login.</summary>
    internal string? MiniRemoteTabForIlt(string tab, string mode)
    {
        var huds = _huds;
        if (huds == null) return null;
        if (tab.Length == 0 && mode is "show" or "hide" or "toggle") return huds.HandleCommand("remote", mode);
        return huds.HandleTabCommand(tab, mode);
    }

    /// <summary>The Mini Remote is on screen (any thread).</summary>
    internal bool MiniRemoteVisibleForIlt => _huds?.MiniRemoteActive == true;

    internal RynthCoreHost HostForIlt => Host;
    internal Meta.QuestTracker? QuestTrackerForIlt => _questTracker;
    internal MiniRemote.MiniRemoteController? MiniRemoteForIlt => _miniRemote;
    internal WorldObjectCache? ObjectCacheForIlt => _objectCache;
    internal RynthCore.Plugin.Shared.SessionStatsTracker? SessionStatsForIlt => _sessionStats;
    /// <summary>
    /// RynthAi.Hub VaultedDungeonsEnabled (obsolete slot): the value the profile still carries, which
    /// RynthILT copied once and owns since 2026-10-20 (an older RynthILT still reads it here); false before login.
    /// </summary>
    internal bool VaultedDungeonsEnabledForIlt => _dashboard?.Settings.EnableVaultedDungeons == true;

    /// <summary>RynthAi.Hub BackAwayFrom: combat's back-away (CombatManager.BackAwayFrom); not now before login. Pump thread.</summary>
    internal int BackAwayForServer(uint objectId, float radiusYards, int holdMs)
    {
        var combat = _combatManager;
        if (combat == null || !_loginComplete) return RynthCore.Plugin.Shared.RynthAiHub.BackAwayNotNow;
        return combat.BackAwayFrom(unchecked((int)objectId), radiusYards, holdMs, "server plugin");
    }

    /// <summary>Item Info window, created once settings exist.</summary>
    private void EnsureItemInfoUi()
    {
        if (_dashboard == null || _itemInfoUi != null) return;
        _itemInfoUi = new LegacyItemInfoUi(_dashboard.Settings);
        _itemInfoUi.SetHooks(DescribeSelectedItemForPreview, PrintSelectedItemInfo, TestItemInfoChatType, TestItemInfoPartColors);
    }

    /// <summary>Pump-thread work for the ported features. Safe before login (each call is null-checked).</summary>
    private void TickLocalFeatures()
    {
        try { RynthLog.Pump(); }
        catch (Exception ex) { Host.Log($"[RynthAi] diagnostics pump: {ex.Message}"); }
        try { _sessionStats?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, "session stats tick"); }
        try { TickMonsterStats(); }   // after the session's tick: follows its start (RynthAiPlugin.MonsterStats.cs)
        catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, "monster stats tick"); }
        try { TickActiveGear(); }   // RynthAiPlugin.ActiveGear.cs
        catch (Exception ex) { RynthLog.Exception(LogCat.UI, ex, "active gear tick"); }
        try { _serverHub?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.UI, ex, "server plugin hub lookup"); }
        try { _miniRemote?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.UI, ex, "Mini Remote Tick"); }
        try { _huds?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, "Tick"); }
        try { _translator?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, "translator Tick"); }
        try { TickItemInfo(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.General, ex, "item info tick"); }
        try { TickSpellsUi(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.General, ex, "spells window tick"); }
        try { _navBreadcrumbs?.Tick(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Navigation, ex, "nav breadcrumbs tick"); }
    }

    /// <summary>Drop per-character feature objects. The translator is disposed only on plugin shutdown.</summary>
    private void TeardownLocalFeatures(bool disposeTranslator)
    {
        try { _navBreadcrumbs?.Shutdown(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Navigation, ex, "nav recorder shutdown"); }
        _navBreadcrumbs = null;
        if (_dashboard != null) _dashboard.NavBreadcrumbs = null;
        _navOverlay = null;
        try { _miniRemote?.OnLogout(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.UI, ex, "Mini Remote logout"); }
        _miniRemote = null;
        _serverHub?.Reset();
        try { _huds?.OnLogout(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Huds, ex, "logout"); }
        _huds = null;
        Huds.ServerCommandModel.Clear();
        _itemInfoUi = null;
        _spellsUi = null;
        ResetActiveGear();
        ResetGroundLoot(clearCaches: true);
        Loot.T11ItemSupport.Reset();
        if (!disposeTranslator) return;
        try { _translator?.Dispose(); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, "translator dispose"); }
        _translator = null;
        _translateUi = null;
    }

    /// <summary>The Pets tab watches for its heal pet's heal; the translator records the line either way.</summary>
    private void OnChatLocalFeatures(string text, int chatType, ref int eat)
    {
        try { _miniRemote?.OnChat(text); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Pets, ex, "Mini Remote OnChat"); }
        try { _translator?.OnChatWindowText(text, chatType); }
        catch (Exception ex) { RynthLog.Exception(LogCat.Chat, ex, "translator inbound"); }
    }

    /// <summary>True when the translator swallowed an outbound chat line (it will resend the translation).</summary>
    private bool TryEatOutboundTranslation(string trimmed)
    {
        try
        {
            return _translator != null && _translator.TryInterceptOutbound(trimmed);
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.Chat, ex, "translator outbound");
            return false;
        }
    }

    /// <summary>ImGui windows the engine panels do not draw.</summary>
    public override void OnRender()
    {
        if (!_initialized || !_loginComplete || Host.ImGuiContext == IntPtr.Zero)
            return;

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            DetectItemInfoClick();
            _navOverlay?.Render();
            _itemInfoUi?.Render();
            _spellsUi?.Render();
            _miniRemote?.Render();
            _huds?.Render();
            RenderTranslateWindow();
            RenderLootWhyWindow();
            RenderHuntPanel();
            RenderDfHeightPanel();
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "OnRender");
        }
        finally
        {
            ImGui.SetCurrentContext(previousContext);
        }
    }

    /// <summary>
    /// Called when the engine's ImGui shell is off. Same local windows as <see cref="OnRender"/>;
    /// the engine panels are absent in that mode, so these windows are the only UI for them.
    /// </summary>
    public void OnRenderOverlay()
    {
        if (!_initialized || !_loginComplete || Host.ImGuiContext == IntPtr.Zero)
        {
            string reason = !_initialized ? "not initialized" : !_loginComplete ? "login not complete" : "no ImGui context";
            if (reason != _overlaySkipReasonLogged)
            {
                _overlaySkipReasonLogged = reason;
                RynthLog.Write(LogCat.UI, $"[Overlay] OnRenderOverlay skipped: {reason}.");
            }
            return;
        }

        if (!_overlayEnteredLogged)
        {
            _overlayEnteredLogged = true;
            RynthLog.Write(LogCat.UI, $"[Overlay] OnRenderOverlay drawing (miniRemote={(_miniRemote != null ? "ready" : "null")}).");
        }

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Host.ImGuiContext);
        try
        {
            DetectItemInfoClick();
            int pushedColors = LegacyDashboardRenderer.PushDashboardStyle();
            try { _miniRemote?.Render(); }
            finally { ImGui.PopStyleColor(pushedColors); }
            _huds?.Render();
            RenderTranslateWindow();
            _navOverlay?.Render();
            _itemInfoUi?.Render();
            _spellsUi?.Render();
            RenderLootWhyWindow();
            RenderHuntPanel();
            RenderDfHeightPanel();
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "OnRenderOverlay");
        }
        finally
        {
            ImGui.SetCurrentContext(previousContext);
        }
    }

    /// <summary>
    /// The ImGui context an embedded surface draws in: the engine's current one, which is the
    /// context of the window it has begun (a popped-out panel has its own, not
    /// Host.ImGuiContext; switching to Host.ImGuiContext there drew into a context that wasn't
    /// mid-frame and crashed the client, 2026-10-11). Host.ImGuiContext only if none is current.
    /// </summary>
    private IntPtr EmbedContext()
    {
        IntPtr current = ImGui.GetCurrentContext();
        return current != IntPtr.Zero ? current : Host.ImGuiContext;
    }

    /// <summary>
    /// RynthPluginRenderEmbed "miniremote" / "miniremote-window": the Mini Remote inside the
    /// engine's RynthAi dashboard drawer, or (<paramref name="window"/>) inside the engine's Mini
    /// Remote window, docked or popped out, drawn into the engine's current ImGui window (and
    /// context, see <see cref="EmbedContext"/>). False (nothing drawn) until login and the HUDs
    /// exist. Render thread.
    /// </summary>
    public bool RenderEmbeddedMiniRemote(bool window, out System.Numerics.Vector2 want, bool tabs = true)
    {
        want = default;
        var huds = _huds;
        if (!_initialized || !_loginComplete || Host.ImGuiContext == IntPtr.Zero || huds == null)
            return false;

        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        try
        {
            want = huds.RenderEmbedded(window, tabs);
            return true;
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderEmbeddedMiniRemote");
            return false;
        }
        finally
        {
            ImGui.SetCurrentContext(previousContext);
        }
    }

    /// <summary>
    /// RynthPluginRenderEmbed "miniremote-host": RynthCore's Mini Remote window is open on one of
    /// its sections, so it is still the one Mini Remote (the floating copy stays hidden). Draws
    /// nothing. Render thread.
    /// </summary>
    public bool MarkEngineMiniRemote()
    {
        var huds = _huds;
        if (!_initialized || !_loginComplete || huds == null) return false;
        huds.MarkEngineWindow();
        return true;
    }

    private Huds.FellowshipSectionUi? _fellowshipUi;

    /// <summary>
    /// The fellowship view RynthCore's Fellowship section draws ("section:fellowship"); the
    /// player's main fellowship window is RynthOracle's Fellowship tab. Render thread.
    /// </summary>
    private Huds.FellowshipSectionUi FellowshipUi() =>
        _fellowshipUi ??= new Huds.FellowshipSectionUi(() => _fellowshipTracker,
            () => Host.HasGetPlayerId ? Host.GetPlayerId() : 0,
            QueueServerCommand);

    /// <summary>Render thread: queues a server chat command (a fellowship button) for the plugin tick.</summary>
    private bool QueueServerCommand(string command)
    {
        if (!Host.HasInvokeChatParser || _engineCommands.Count >= MaxQueuedEngineCommands) return false;
        _engineCommands.Enqueue(command);
        return true;
    }

    /// <summary>
    /// RynthPluginRenderEmbed "section:&lt;name&gt;": one of RynthAi's views in RynthCore's Mini
    /// Remote sections (Huds/SectionsManifest.cs lists them), drawn into the engine's current
    /// window (and context, see <see cref="EmbedContext"/>) at <paramref name="size"/>.
    /// <paramref name="want"/> is the size the view would like. False (nothing drawn) for an
    /// unknown view, or until login and the view's owner exist. (A server plugin's tab views are its own:
    /// RynthILT draws its ILT tabs since 2026-10-20.) Render thread.
    /// </summary>
    public bool RenderEmbeddedSection(string surface, System.Numerics.Vector2 size, out System.Numerics.Vector2 want)
    {
        want = size;
        var huds = _huds;
        var remote = _miniRemote;
        if (!_initialized || !_loginComplete || Host.ImGuiContext == IntPtr.Zero) return false;

        Func<bool>? draw = null;
        if (surface == Huds.SectionsManifest.SessionSurface)
        {
            // The one session (RynthCore's Session Stats section).
            if (huds == null) return false;
            draw = () => { huds.RenderSessionSection(); return true; };
        }
        else if (surface == Huds.SectionsManifest.FellowshipSurface)
        {
            // RynthILT's own pane carries ILT's /fship commands in RynthCore's section.
            draw = () => { FellowshipUi().Render(withServerCommands: false); return true; };
        }
        else if (Huds.SectionsManifest.TryOwnTabOf(surface, out string ownTab))
        {
            if (remote == null) return false;
            want = MiniRemoteTabs.Size(ownTab);
            draw = () => remote.RenderTab(ownTab);
        }
        else if (surface == Huds.SectionsManifest.SpellsSurface)
        {
            EnsureSpellsUi();
            var spells = _spellsUi;
            if (spells == null) return false;
            draw = () => { spells.RenderEmbedded(); return true; };
        }
        if (draw == null) return false;

        // The engine's Mini Remote window shows this section: it is still the one Mini Remote.
        huds?.MarkEngineWindow();
        IntPtr previousContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(EmbedContext());
        int pushedColors = LegacyDashboardRenderer.PushDashboardStyle();
        try
        {
            return draw();
        }
        catch (Exception ex)
        {
            RynthLog.Exception(LogCat.UI, ex, "RenderEmbeddedSection " + surface);
            return false;
        }
        finally
        {
            ImGui.PopStyleColor(pushedColors);
            ImGui.SetCurrentContext(previousContext);
        }
    }

    /// <summary>
    /// RynthPluginGetSectionsJson: what RynthAi adds to RynthCore's Mini Remote sections (Huds/
    /// SectionsManifest.cs; a server plugin lists its own). Null before login (RynthCore then shows
    /// none of RynthAi's views). Render thread.
    /// </summary>
    public string? BuildSectionsJson()
    {
        if (!_initialized || !_loginComplete) return null;
        return Huds.SectionsManifest.Build(Huds.SectionsManifest.Views(), Huds.SectionsManifest.OwnSections,
            dashboard: DashboardWindowWanted, monsterStats: MonsterStatsWindowWanted, timedCommands: TimedCommandsWindowWanted,
            activeGear: ActiveGearWindowWanted);
    }
    /// <summary>Chat Translate window in the dashboard colours (no-op while hidden).</summary>
    private void RenderTranslateWindow()
    {
        var ui = _translateUi;
        if (ui == null || _translator?.Settings.ShowWindow != true) return;
        int pushedColors = LegacyDashboardRenderer.PushDashboardStyle();
        try { ui.RenderWindow(); }
        finally { ImGui.PopStyleColor(pushedColors); }
    }

    /// <summary>"/ra huds|itemhud|remote|miniremote [show|hide|toggle]".</summary>
    private void HandleHudCommand(string which, string mode)
    {
        if (_huds == null) { ReplyFail("[RynthAi] HUDs not ready (log in first)."); return; }
        ChatLine("[RynthAi] " + _huds.HandleCommand(which, mode));
    }

    /// <summary>"/ra itemhud add &lt;name&gt;" and the inventory panel's "Add to item count HUD". Pump thread.</summary>
    private void HandleItemHudAdd(string name)
    {
        if (_huds == null) { ReplyFail("[RynthAi] HUDs not ready (log in first)."); return; }
        if (string.IsNullOrWhiteSpace(name)) { ReplyFail("[RynthAi] Usage: /ra itemhud add <item name>"); return; }
        ChatLine("[RynthAi] " + _huds.AddToItemHudAndShow(name));
    }

    /// <summary>/ra translate ... — forwards to the chat translator.</summary>
    private void HandleTranslateCommand(string[] parts)
    {
        if (_translator == null) { ReplyFail("[RynthAi] Chat translator not ready (log in first)."); return; }
        _translator.HandleCommand(parts.Length > 2 ? parts[2..] : Array.Empty<string>(), ChatLine);
    }

    /// <summary>Parses an on/off word; returns null for anything else (caller treats it as status).</summary>
    private static bool? ParseOnOff(string? word) => word?.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "enable" => true,
        "off" or "false" or "0" or "disable" => false,
        _ => null,
    };

    /// <summary>"/ra debug [on|off|status]" — echoes trace and exception lines to chat.</summary>
    private void HandleDebugCommand(string[] parts)
    {
        string arg = parts.Length > 2 ? parts[2] : "status";
        bool? state = arg.Equals("toggle", StringComparison.OrdinalIgnoreCase) ? !RynthLog.DebugToChat : ParseOnOff(arg);
        if (state.HasValue) RynthLog.DebugToChat = state.Value;
        string line = $"[RynthAi] Debug-to-chat is {(RynthLog.DebugToChat ? "ON" : "off")}."
                      + (RynthLog.DebugToChat && RynthLog.TracedCategories().Count == 0 ? " (no categories traced — see /ra trace list)" : "");
        if (state.HasValue) ReplyOk(line);   // a change
        else ChatLine(line);                 // the status asked for
    }

    /// <summary>"/ra trace &lt;cat|all|ilt|list&gt; [on|off|status]".</summary>
    private void HandleTraceCommand(string[] parts)
    {
        string target = parts.Length > 2 ? parts[2] : "status";
        string? action = parts.Length > 3 ? parts[3] : null;

        if (target.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string l in RynthLog.StatusLines()) ChatLine(l);
            return;
        }

        if (target.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (LogCat c in Enum.GetValues<LogCat>())
                names.Add(RynthLog.IsTracing(c) ? c + "*" : c.ToString());
            ChatLine("[RynthAi] Trace categories (* = on): " + string.Join(", ", names));
            return;
        }

        bool? state = ParseOnOff(action);
        bool isAll = target.Equals("all", StringComparison.OrdinalIgnoreCase);
        if (isAll)
        {
            if (!state.HasValue) { foreach (string l in RynthLog.StatusLines()) ChatLine(l); return; }
            int n = RynthLog.SetTracingByPrefix(string.Empty, state.Value);
            ReplyOk($"[RynthAi] Tracing {(state.Value ? "enabled" : "disabled")} for {n} categories.");
            return;
        }

        if (!RynthLog.TryParseCategory(target, out LogCat cat))
        {
            ReplyFail($"[RynthAi] Unknown trace category '{target}'. Use /ra trace list.");
            return;
        }

        if (action != null && action.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            ChatLine($"[RynthAi] Trace {cat}: {(RynthLog.IsTracing(cat) ? "ON" : "off")}");
            return;
        }

        bool enable = state ?? !RynthLog.IsTracing(cat);
        RynthLog.SetTracing(cat, enable);
        ReplyOk($"[RynthAi] Trace {cat}: {(enable ? "ON" : "off")} → {RynthLog.TraceDirectory}");
    }

    /// <summary>"/ra logs [open|prune|flush|file on|off]".</summary>
    private void HandleLogsCommand(string[] parts)
    {
        string sub = parts.Length > 2 ? parts[2].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "open":
                try
                {
                    RynthLog.Flush();
                    System.IO.Directory.CreateDirectory(RynthLog.Directory);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{RynthLog.Directory}\"") { UseShellExecute = true });
                    ReplyOk($"[RynthAi] Opened {RynthLog.Directory}.");
                }
                catch (Exception ex) { RynthLog.Exception(LogCat.Commands, ex, "logs open"); ReplyFail($"[RynthAi] Could not open {RynthLog.Directory}: {ex.Message}"); }
                break;
            case "prune":
                ReplyOk($"[RynthAi] Pruned {RynthLog.Prune()} old diagnostics file(s).");
                break;
            case "flush":
                RynthLog.Flush();
                ReplyOk("[RynthAi] Diagnostics flushed to disk.");
                break;
            case "file":
                bool? state = ParseOnOff(parts.Length > 3 ? parts[3] : null);
                if (state.HasValue) RynthLog.FileLogAll = state.Value;
                string fileLine = $"[RynthAi] Daily RynthAi log file is {(RynthLog.FileLogAll ? "ON" : "off")}.";
                if (state.HasValue) ReplyOk(fileLine); else ChatLine(fileLine);
                break;
            default:
                foreach (string l in RynthLog.StatusLines()) ChatLine(l);
                break;
        }
    }

    /// <summary>/ra navrec [on|off] — toggles route recording.</summary>
    private void HandleNavRecordCommand(string[] parts)
    {
        if (_dashboard == null) { ReplyFail("[RynthAi] Settings not ready (log in first)."); return; }
        var s = _dashboard.Settings;
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        s.IsRecordingNav = arg switch { "on" => true, "off" => false, _ => !s.IsRecordingNav };
        ReplyOk($"[RynthAi] Nav recording {(s.IsRecordingNav ? "ON — walk the route; portals are captured" : "OFF")}.");
    }

    /// <summary>/ra navhud [on|off] — shows or hides the navigation HUD window.</summary>
    private void HandleNavHudCommand(string[] parts)
    {
        if (_dashboard == null) { ReplyFail("[RynthAi] Settings not ready (log in first)."); return; }
        var o = _dashboard.Settings.NavOverlay;
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        o.ShowHud = arg switch { "on" => true, "off" => false, _ => !o.ShowHud };
        ReplyOk($"[RynthAi] Nav HUD {(o.ShowHud ? "shown" : "hidden")}.");
    }

    /// <summary>/ra navtrail [on|off|toggle|clear] — breadcrumb tracking switch, wipe, or a status report.</summary>
    private void HandleNavTrailCommand(string[] parts)
    {
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        if (arg == "clear")
        {
            _navBreadcrumbs?.RequestClear();
            ReplyOk("[RynthAi] Breadcrumb trail cleared.");
            return;
        }
        if (arg is "on" or "off" or "toggle")
        {
            if (_dashboard == null) { ReplyFail("[RynthAi] Settings not ready (log in first)."); return; }
            var o = _dashboard.Settings.NavOverlay;
            o.TrackBreadcrumbs = arg switch { "on" => true, "off" => false, _ => !o.TrackBreadcrumbs };
            _dashboard.SaveSettings();
            ReplyOk($"[RynthAi] Breadcrumb tracking {(o.TrackBreadcrumbs ? "on" : "off")}.");
            return;
        }
        bool tracking = _dashboard?.Settings.NavOverlay.TrackBreadcrumbs ?? false;
        int n = _navBreadcrumbs?.Trail.Length ?? 0;
        double yd = _navBreadcrumbs?.TrailYards ?? 0;
        ChatLine($"[RynthAi] Breadcrumbs {(tracking ? "on" : "off")}: {n} points, {yd:F0} yd. Use /ra navtrail on|off|clear.");
    }

    /// <summary>/ra navoverlay [on|off] — route overlay (rings / lines, waypoint labels, guide line); no argument toggles.</summary>
    private void HandleNavOverlayCommand(string[] parts)
    {
        if (_dashboard == null) { ReplyFail("[RynthAi] Settings not ready (log in first)."); return; }
        var o = _dashboard.Settings.NavOverlay;
        string arg = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        o.ShowRouteMarkers = arg switch { "on" => true, "off" => false, _ => !o.ShowRouteMarkers };
        _dashboard.SaveSettings();
        ReplyOk($"[RynthAi] Route overlay {(o.ShowRouteMarkers ? "on" : "off")}.");
    }
}

// MiniRemoteHud.cs — The Mini Remote (UtilityBelt-style) for RynthAi. Render thread only.
//
// One window with tabs: Remote (the sections below), the universal tabs Pets and Crafting (every
// world, MiniRemote/; the Quests tab moved to RynthOracle 2026-10-13), then whatever tabs the active
// server plugin offers (its hub: GetTabs / RenderTab through ServerHubClient; RynthILT's Banking, Gear,
// Charms, Games and Vaulted Dungeons on Infinite Leaftide). Nothing server-specific is drawn here
// without one. The tab bar's selection is saved (HudState.MiniRemoteTab) and "/ra hub open
// <section>" or RynthCore's dashboard switch it (RequestTab).
// Right-click on the remote: "Character" (Spells, Pets, Crafting, RynthOracle's Character, Quests,
// Quest Timers and Fellowship, Skills, Inventory, Item Count, Inventory HUDs: windows every server
// has), then "Server: <name>", i.e. the installed server plugins' windows for the world you're on,
// which RynthCore sends (ServerMenuModel), or a link to RynthCore's Options > Diagnostics (where a
// server is forced) when none matches. The Options button opens RynthAi Settings (/rc ui open
// Settings); the small gear next to it, or a right-click on Options, holds the Mini Remote's own
// settings (sections, layout, slot size), and so does a click on Options when there is no chat parser.
// The V/H button stacks the sections (vertical) or lays them out in three columns (horizontal).
//
// Remote sections (each can be hidden from the Options menu):
//   Stats    session time and per-hour XP / luminance / kills, then the server plugin's own rate lines
//            (section "remote-stats": RynthILT's coins / pyreals per hour)
//   Target   the creature combat is attacking: name, health bar, distance
//   Pet      the summon that is out (health bar, time left), else (while a server plugin is active)
//            the next combat essence and whether it is ready
//   Slots    5 × 6 quick-use item grid; click uses the item (an empty slot takes the selected
//            pack item), right-click assigns / clears, and items dragged from the game's
//            inventory or the RynthCore Inventory window drop straight into a slot. Ctrl+wheel
//            over the grid (or Options > Slot size) grows / shrinks the boxes.
//   Toggles  macro and subsystem switches (same as the dashboard buttons; not in the drawer)
//   Rebuff   force rebuff / cancel rebuff
//   Translate chat translator on/off and the receive <-> send language swap
//   + the server plugin's Remote sections around them (RynthILT: Bank, Guardian answer; their
//     switches are its own, drawn in the Options popup through section "remote-options")
// All game actions are posted to the pump thread.
//
// RenderEmbedded draws the same sections and Options menu inside the engine's RynthAi dashboard
// (its Mini Remote drawer, opened by the drawer tab) and
// reports the size it wants; the drawer is sized from that. The drawer leaves out Toggles (the
// dashboard it sits on already has those switches) and the tabs (it is too narrow for them).
// RenderEmbedded(window: true) draws it in the engine's Mini Remote window instead (bar button,
// /rc miniremote): tabs and Toggles included, and the floating window stays hidden meanwhile
// (HudController), so that window is the one Mini Remote. RynthCore 2026.10.5.46+ shows the tabs
// as its own sections (RynthPluginGetSectionsJson) and asks for the remote alone (tabs: false).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.RynthAi.MiniRemote;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Huds;

internal sealed class MiniRemoteHud
{
    private const string OptionsPopup = "##miniremoteopts";
    private const string MenuPopup = "##miniremotemenu";
    private const string SlotPopup = "##miniremoteslot";
    /// <summary>Pixels per Ctrl+wheel notch over the slot grid.</summary>
    private const float SlotSizeStep = 2f;
    /// <summary>Narrowest horizontal-layout column (fits the stat lines and the translate row).</summary>
    internal const float MinColumnWidth = 190f;

    private static readonly Vector4 ColLabel = new(0.80f, 0.84f, 0.90f, 1f);
    private static readonly Vector4 ColReady = new(0.40f, 0.95f, 0.45f, 1f);
    private static readonly Vector4 ColOut = new(0.45f, 0.85f, 0.95f, 1f);
    private static readonly Vector4 ColEmpty = new(0.95f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 ColBusy = new(0.95f, 0.80f, 0.35f, 1f);

    private readonly HudController _hud;
    private int _slotMenuIndex = -1;
    private bool _rescueChecked; // off-screen check done for the current show
    private bool _columnOpen; // horizontal layout: a column was already started this frame (next one goes SameLine)
    private bool _embedded;   // true only inside RenderEmbedded (an engine surface)
    private bool _inWindow;   // with _embedded: the engine's Mini Remote window, not the dashboard drawer
    // A tab switch asked for by a command or the engine (any thread), applied by the next tab bar;
    // null = none. Starts as the saved tab so the first tab bar restores it.
    private string? _pendingTab;
    private bool _pendingTabFromState = true;
    // This frame's tab bar showed the Remote tab (right-click Options belongs to it only).
    private bool _onRemoteTab = true;
    // The Options button was clicked this frame (the popup opens at window level).
    private bool _openOptions;
    // The header's Options button was right-clicked this frame: its popup opens, not the remote's menu.
    private bool _headerRightClick;
    /// <summary>One tab after Remote: a universal one (drawn here) or the server plugin's.</summary>
    private readonly record struct TabEntry(string Id, string Label, Vector2 Size, bool FromServer);

    // The tabs offered this frame (reused, so a frame doesn't allocate).
    private readonly List<TabEntry> _tabs = new();
    // The server plugin drew one of its Remote sections (Bank, Guardian) in the last frame: the
    // horizontal layout keeps their column for it.
    private bool _serverEconomyDrawn;

    public MiniRemoteHud(HudController hud) => _hud = hud;

    /// <summary>The remote was not drawn this frame: check its position again on the next show.</summary>
    public void OnHidden() => _rescueChecked = false;

    /// <summary>
    /// Switches to tab <paramref name="tab"/> ("" = the remote, else a tab id) on the next
    /// frame the tab bar draws. Any thread.
    /// </summary>
    public void RequestTab(string tab)
    {
        _pendingTabFromState = false;
        Volatile.Write(ref _pendingTab, tab ?? string.Empty);
    }

    public void Render()
    {
        var s = _hud.State;
        var flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoFocusOnAppearing;
        if (s.MiniRemoteLocked) flags |= ImGuiWindowFlags.NoMove;

        // Left of and above centre, mirroring the Item HUD's first spot.
        UiPlacement.CenterFirstUse(new Vector2(-220, -160));
        ImGui.SetNextWindowBgAlpha(0.70f);
        bool open = true;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 3));
        bool visible = ImGui.Begin("Mini Remote##rynthminiremote", ref open, flags);
        ImGui.PopStyleVar();
        if (!open) s.ShowMiniRemote = false;
        if (!visible) { ImGui.End(); return; }
        UiPlacement.RescueOncePerShow(ref _rescueChecked);

        RenderBody(s, _hud.Remote(), allowTabs: true);

        // Right-click on the remote opens the Character / server menu, unless a slot's own menu is
        // open (a server plugin's tabs have their own right-click menus).
        if (_onRemoteTab && _slotMenuIndex < 0 && !_headerRightClick && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
            ImGui.OpenPopup(MenuPopup);
        RenderOptions(s, embedded: false);
        RenderMenu(s);
        _slotMenuIndex = -1;
        ImGui.End();
    }

    /// <summary>
    /// The same sections drawn into the caller's current window (the engine's RynthAi dashboard
    /// drawer, or with <paramref name="window"/> the engine's Mini Remote window; both size
    /// themselves from the result) instead of a window of their own. Returns the content size it
    /// wants: the selected tab's width, and the height drawn.
    /// </summary>
    public Vector2 RenderEmbedded(bool window = false, bool tabs = true)
    {
        var s = _hud.State;
        _slotMenuIndex = -1;
        _embedded = true;
        _inWindow = window;
        float startY = ImGui.GetCursorPosY();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 3));
        try
        {
            // Without tabs (RynthCore's sections show them instead) the remote alone.
            float width = RenderBody(s, _hud.Remote(), allowTabs: tabs && !InDrawer);

            if (_onRemoteTab && _slotMenuIndex < 0 && !_headerRightClick && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows) && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                ImGui.OpenPopup(MenuPopup);
            RenderOptions(s, embedded: true);
            RenderMenu(s);

            float height = ImGui.GetCursorPosY() - startY - ImGui.GetStyle().ItemSpacing.Y;
            return new Vector2(width, Math.Max(0f, height) + 2f);
        }
        finally
        {
            ImGui.PopStyleVar();
            _slotMenuIndex = -1;
            _embedded = false;
            _inWindow = false;
        }
    }

    // ── Tabs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tab bar (when <paramref name="allowTabs"/>: the universal tabs on every world, then the
    /// server plugin's tabs on its server) and the selected tab: the remote, or a tab's body in a child of its
    /// fixed size. Returns the width the content wants (never narrower than the tab bar's labels).
    /// </summary>
    private float RenderBody(HudState s, MiniRemoteController? remote, bool allowTabs)
    {
        _onRemoteTab = true;
        _headerRightClick = false;
        _tabs.Clear();
        if (allowTabs && remote != null)
        {
            foreach (string id in MiniRemoteTabs.Universal)
                _tabs.Add(new TabEntry(id, MiniRemoteTabs.Label(id), MiniRemoteTabs.Size(id), FromServer: false));
            foreach (ServerTabInfo t in remote.ServerHub.Tabs())
                if (t.Shown) _tabs.Add(new TabEntry(t.Id, t.Label, new Vector2(t.Width, t.Height), FromServer: true));
        }
        if (_tabs.Count == 0)
        {
            RenderRemote(s, remote);
            return EmbeddedWidth(s);
        }

        if (_pendingTabFromState)
        {
            _pendingTabFromState = false;
            Interlocked.CompareExchange(ref _pendingTab, s.MiniRemoteTab ?? string.Empty, null);
        }
        string? pending = Interlocked.Exchange(ref _pendingTab, null);
        float width = TabBarWidth();
        _onRemoteTab = false;
        if (ImGui.BeginTabBar("##mrtabs", ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.NoTooltip))
        {
            if (BeginTab(RemoteTabLabel, TabFlags(MiniRemoteTabs.Remote, pending)))
            {
                _onRemoteTab = true;
                s.MiniRemoteTab = MiniRemoteTabs.Remote;
                RenderRemote(s, remote);
                width = Math.Max(width, EmbeddedWidth(s));
                ImGui.EndTabItem();
            }
            foreach (TabEntry tab in _tabs)
            {
                if (!BeginTab(TabLabel(tab), TabFlags(tab.Id, pending))) continue;
                s.MiniRemoteTab = tab.Id;
                // The tab draws into a child of its own size; tables in it scroll inside it.
                ImGui.BeginChild(TabBodyId(tab.Id), tab.Size, ImGuiChildFlags.None, ImGuiWindowFlags.None);
                bool drawn = tab.FromServer ? remote!.ServerHub.RenderTab(tab.Id) : remote!.RenderTab(tab.Id);
                if (!drawn) SafeImGui.TextDisabled(tab.FromServer ? "The server plugin isn't drawing this tab (not loaded, or not ready yet)." : "Not ready.");
                ImGui.EndChild();
                width = Math.Max(width, tab.Size.X);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
        return width;
    }

    // Tab labels as NUL-terminated UTF-8 and the tab body child ids, built once per tab so the tab
    // bar doesn't allocate per frame (a server plugin's tabs arrive by id at run time).
    private static readonly byte[] RemoteTabLabel = Utf8Z("Remote##mrtab");
    private readonly Dictionary<string, byte[]> _tabLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tabBodyIds = new(StringComparer.Ordinal);

    private byte[] TabLabel(TabEntry tab)
    {
        string key = tab.Label + "##mrtab";
        if (!_tabLabels.TryGetValue(key, out byte[]? label)) _tabLabels[key] = label = Utf8Z(key);
        return label;
    }

    private string TabBodyId(string id)
    {
        if (!_tabBodyIds.TryGetValue(id, out string? body)) _tabBodyIds[id] = body = "##mrtabbody_" + id;
        return body;
    }

    private static byte[] Utf8Z(string text)
    {
        byte[] bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(text) + 1];
        System.Text.Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    /// <summary>
    /// BeginTabItem without a close button: ImGui.NET only wraps the flags overload together with
    /// p_open, and a non-null p_open draws an X on the tab.
    /// </summary>
    private static unsafe bool BeginTab(byte[] labelUtf8Z, ImGuiTabItemFlags flags)
    {
        fixed (byte* label = labelUtf8Z)
            return ImGuiNative.igBeginTabItem(label, null, flags) != 0;
    }

    /// <summary>SetSelected for the tab a command asked for this frame.</summary>
    private static ImGuiTabItemFlags TabFlags(string id, string? pending) =>
        pending != null && string.Equals(pending, id, StringComparison.OrdinalIgnoreCase)
            ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;

    /// <summary>Width of every tab label side by side (the embedded window shouldn't be narrower, or tabs scroll).</summary>
    private float TabBarWidth()
    {
        var style = ImGui.GetStyle();
        float w = ImGui.CalcTextSize("Remote").X + style.FramePadding.X * 2f;
        foreach (TabEntry tab in _tabs)
            w += style.ItemInnerSpacing.X + ImGui.CalcTextSize(tab.Label).X + style.FramePadding.X * 2f;
        return w;
    }

    /// <summary>The Remote tab: the [V/H] [Options] row, then the sections in the chosen layout.</summary>
    private void RenderRemote(HudState s, MiniRemoteController? remote)
    {
        RenderHeaderRow(s);
        if (s.MiniRemoteHorizontal) RenderHorizontal(s, remote);
        else RenderVertical(s, remote);
    }

    /// <summary>In the dashboard drawer (not the floating window or the engine's Mini Remote window).</summary>
    private bool InDrawer => _embedded && !_inWindow;

    /// <summary>The Toggles section is drawn: chosen in Options, and never in the dashboard drawer.</summary>
    private bool ShowToggles(HudState s) => s.MiniShowToggles && !InDrawer;

    /// <summary>
    /// The width the sections need: one column stacked, else one per non-empty column side by side.
    /// Never measured from what was drawn, since widgets fill the width they are given.
    /// </summary>
    private float EmbeddedWidth(HudState s)
    {
        float colW = ColumnWidth(s);
        if (!s.MiniRemoteHorizontal) return colW;
        int cols = 0;
        if (s.MiniShowStats || s.MiniShowTarget || s.MiniShowPet) cols++;
        if (s.MiniShowGems || ShowToggles(s)) cols++;
        if (s.MiniShowRebuff || s.MiniShowTranslate || _serverEconomyDrawn) cols++;
        cols = Math.Max(1, cols);
        return cols * colW + (cols - 1) * ImGui.GetStyle().ItemSpacing.X;
    }

    // ── Layout ──────────────────────────────────────────────────────────────

    /// <summary>
    /// [V/H], [Options] and a small gear. Fixed widths (MiniRemoteHeader, sized to fit MinColumnWidth)
    /// so the auto-resizing window can't grow from them.
    /// </summary>
    private void RenderHeaderRow(HudState s)
    {
        if (ImGui.Button((s.MiniRemoteHorizontal ? "H" : "V") + "##mrlayout", new Vector2(MiniRemoteHeader.LayoutButtonWidth, 0)))
            s.MiniRemoteHorizontal = !s.MiniRemoteHorizontal;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip(s.MiniRemoteHorizontal ? "Horizontal: sections in columns. Click to stack them." : "Vertical: sections stacked. Click to lay them out in columns.");
        ImGui.SameLine();
        // Left-click: RynthAi Settings (the engine's "Settings" panel); without a chat parser, and on a
        // right-click, the Mini Remote popup (RenderOptions: this row sits inside the Remote tab, whose
        // ID the popup must not get, so the popup opens at window level through _openOptions).
        if (ImGui.Button("Options##mropts", new Vector2(MiniRemoteHeader.OptionsButtonWidth, 0)))
        {
            string? command = MiniRemoteHeader.OptionsLeftClickCommand(_hud.Host.HasInvokeChatParser);
            if (command != null) _hud.RunEngineCommand(command);
            else _openOptions = true;
        }
        if (ImGui.IsItemHovered())
        {
            _headerRightClick = true;   // the remote's right-click menu stays closed over this button
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Right)) _openOptions = true;
            SafeImGui.SetTooltip(_hud.Host.HasInvokeChatParser
                ? "Open RynthAi Settings (Advanced Settings).\nRight-click: Mini Remote sections, layout, slot size."
                : "Mini Remote settings: sections, layout, slot size.");
        }
        ImGui.SameLine();
        if (ImGui.Button("##mrgear", new Vector2(MiniRemoteHeader.GearButtonWidth, 0))) _openOptions = true;
        DrawGear(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip("Mini Remote sections, layout, slot size");
    }

    /// <summary>A small gear (ring and eight teeth) centred in the button's rectangle, in the text colour.</summary>
    private static void DrawGear(Vector2 min, Vector2 max)
    {
        var draw = ImGui.GetWindowDrawList();
        uint col = ImGui.GetColorU32(ImGuiCol.Text);
        Vector2 c = (min + max) * 0.5f;
        float r = MathF.Min(max.X - min.X, max.Y - min.Y) * 0.22f;
        draw.AddCircle(c, r, col, 12, 1.6f);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4f;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            draw.AddLine(c + dir * (r + 0.5f), c + dir * (r + 3f), col, 2f);
        }
    }

    private void RenderVertical(HudState s, MiniRemoteController? remote)
    {
        RenderStatusSections(s, remote);
        RenderActionSections(s);
        RenderEconomySections(s, remote);
    }

    /// <summary>
    /// Three side-by-side columns: status (stats, target, pet), actions (slots, toggles) and
    /// economy (bank, rebuff, translate). Each column is a fixed-width child that grows only in
    /// height; widgets sized from the available width would otherwise make the auto-resizing
    /// window grow every frame.
    /// </summary>
    private void RenderHorizontal(HudState s, MiniRemoteController? remote)
    {
        float colW = ColumnWidth(s);
        _columnOpen = false;
        if (s.MiniShowStats || s.MiniShowTarget || s.MiniShowPet)
        {
            BeginColumn("##mrcolstatus", colW);
            RenderStatusSections(s, remote);
            ImGui.EndChild();
        }
        if (s.MiniShowGems || ShowToggles(s))
        {
            BeginColumn("##mrcolactions", colW);
            RenderActionSections(s);
            ImGui.EndChild();
        }
        if (s.MiniShowRebuff || s.MiniShowTranslate || _serverEconomyDrawn)
        {
            BeginColumn("##mrcoleconomy", colW);
            RenderEconomySections(s, remote);
            ImGui.EndChild();
        }
    }

    /// <summary>Starts a column child (caller always calls EndChild, whatever BeginChild returned).</summary>
    private void BeginColumn(string id, float width)
    {
        if (_columnOpen) ImGui.SameLine();
        _columnOpen = true;
        ImGui.BeginChild(id, new Vector2(width, 0), ImGuiChildFlags.AutoResizeY, ImGuiWindowFlags.NoScrollbar);
    }

    /// <summary>Wide enough for the slot grid at its current size, and never narrower than MinColumnWidth.</summary>
    private static float ColumnWidth(HudState s)
    {
        int cols = HudState.MiniRemoteColumns;
        float grid = cols * SlotButtonSize(s) + (cols - 1) * ImGui.GetStyle().ItemSpacing.X;
        return Math.Max(MinColumnWidth, grid);
    }

    /// <summary>Slot icon edge in pixels (the saved size, clamped).</summary>
    private static float SlotSize(HudState s) =>
        Math.Clamp(s.MiniRemoteSlotSize, HudState.MinMiniRemoteSlotSize, HudState.MaxMiniRemoteSlotSize);

    /// <summary>Slot button edge including its 1 px frame padding on each side.</summary>
    private static float SlotButtonSize(HudState s) => SlotSize(s) + 2f;

    /// <summary>
    /// Grows (+) or shrinks (-) the slot boxes within the saved range (huds.json picks it up on
    /// the next SaveIfDirty, like every other HUD setting).
    /// </summary>
    private static void ResizeSlots(HudState s, float delta) =>
        s.MiniRemoteSlotSize = Math.Clamp(SlotSize(s) + delta, HudState.MinMiniRemoteSlotSize, HudState.MaxMiniRemoteSlotSize);

    private void RenderStatusSections(HudState s, MiniRemoteController? remote)
    {
        var combat = _hud.Combat;
        if (s.MiniShowStats) RenderStats(remote);
        if (s.MiniShowTarget) RenderTarget(combat);
        if (s.MiniShowPet) RenderPet(remote, combat);
        if (s.MiniShowStats) RenderStatButtons(remote);
    }

    private void RenderActionSections(HudState s)
    {
        if (s.MiniShowGems) RenderSlots(s);
        if (ShowToggles(s)) RenderToggles(_hud.Dashboard.Settings);
    }

    private void RenderEconomySections(HudState s, MiniRemoteController? remote)
    {
        // The server plugin's Remote sections (RynthILT: Bank, Guardian answer) decide themselves whether
        // they show (their switches are the plugin's); nothing is drawn without one.
        bool drawn = remote?.ServerHub.RenderSection(RynthServerHub.SectionRemoteBank) == true;
        if (s.MiniShowRebuff) RenderRebuff();
        if (s.MiniShowTranslate && _hud.Translate is { } translate) translate.RenderHubSection();
        drawn |= remote?.ServerHub.RenderSection(RynthServerHub.SectionRemoteGuardian) == true;
        _serverEconomyDrawn = drawn;
    }

    // ── Sections ────────────────────────────────────────────────────────────

    /// <summary>A server plugin is active for this world (RynthILT on Infinite Leaftide); false without one.</summary>
    private static bool ServerActive(MiniRemoteController? remote) => remote != null && remote.ServerHub.ServerActive;

    private static void Stat(string label, string value)
    {
        SafeImGui.TextColored(ColLabel, label);
        ImGui.SameLine();
        ImGui.TextUnformatted(value);
    }

    /// <summary>
    /// Session stats on every world: the shared SessionStats (RynthTracker's code, so these match the
    /// Tracker panel), plus the server plugin's own rate lines (RynthILT's coins and pyreals).
    /// </summary>
    private void RenderStats(MiniRemoteController? remote)
    {
        var v = _hud.SessionStats?.View ?? RynthCore.Plugin.Shared.SessionStatsView.Empty;
        Stat("Time:", MiniRemoteText.Duration(TimeSpan.FromSeconds(SessionClock(v))));
        Stat("XP:", HudDraw.Compact(v.XpPerHour) + "/h");
        Stat("Lum:", HudDraw.Compact(v.LumPerHour) + "/h");
        Stat("Lum 5m:", HudDraw.Compact(v.LumLast5Min));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Luminance earned in the last five minutes.");
        if (v.PassUpTracked)
        {
            Stat("Pass-up:", HudDraw.Compact(v.PassUpPerHour) + "/h");
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"{v.LumPassUp:N0} this session.\n{PassUpTooltip}");
        }
        Stat("Kills:", HudDraw.Compact(v.KillsPerHour) + "/h");
        remote?.ServerHub.RenderSection(RynthServerHub.SectionRemoteStats);
    }

    /// <summary>
    /// RynthCore's Mini Remote "Session Stats" section (surface "section:session"): the one session,
    /// totals and per-hour rates, with Reset and Report. RynthTracker, RynthOracle and RynthILT read
    /// these same numbers (RynthAi.Hub SessionStats), so every XP/h and lum/h on screen agrees.
    /// Render thread.
    /// </summary>
    public void RenderSessionSection()
    {
        var v = _hud.SessionStats?.View ?? RynthCore.Plugin.Shared.SessionStatsView.Empty;
        bool running = !ReferenceEquals(v, RynthCore.Plugin.Shared.SessionStatsView.Empty);
        SafeImGui.TextDisabled(running
            ? "Session " + MiniRemoteText.Duration(TimeSpan.FromSeconds(SessionClock(v)))
            : "The session starts once you are in the world.");
        if (ImGui.BeginTable("##mrsession", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Stat", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("Session", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Per hour", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();
            SessionRow("XP", HudDraw.Compact(v.Xp), HudDraw.Compact(v.XpPerHour));
            SessionRow("Luminance", HudDraw.Compact(v.Lum), HudDraw.Compact(v.LumPerHour));
            // The last five minutes' luminance, and that pace for an hour.
            SessionRow("  last 5 min", HudDraw.Compact(v.LumLast5Min), HudDraw.Compact(v.LumLast5Min * 12.0));
            if (v.PassUpTracked) SessionRow("  pass-up", HudDraw.Compact(v.LumPassUp), HudDraw.Compact(v.PassUpPerHour), PassUpTooltip);
            if (v.Rad > 0) SessionRow("Radiance", HudDraw.Compact(v.Rad), HudDraw.Compact(v.RadPerHour));
            SessionRow("Kills", v.Kills.ToString("N0"), HudDraw.Compact(v.KillsPerHour));
            if (v.XpPerKill > 0) SessionRow("  XP per kill", HudDraw.Compact(v.XpPerKill), string.Empty);
            if (v.Deaths > 0) SessionRow("Deaths", v.Deaths.ToString("N0"), string.Empty);
            ImGui.EndTable();
        }
        RenderStatButtons(_hud.Remote());
    }

    /// <summary>One Session Stats row: label, session total, per hour (and an optional tooltip on the label).</summary>
    private static void SessionRow(string label, string total, string perHour, string tooltip = "")
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        SafeImGui.TextColored(ColLabel, label);
        if (tooltip.Length > 0 && ImGui.IsItemHovered()) SafeImGui.SetTooltip(tooltip);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(total);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(perHour);
    }

    /// <summary>What the pass-up figure is (RynthILT's session rates say the same).</summary>
    private const string PassUpTooltip =
        "Luminance that reached your bank without a \"You've banked\" line: your vassals' pass-up.\n"
        + "Fellowship and Zone Share splits arrive the same silent way and are included.";

    /// <summary>Seconds since the session started; 0 before it has (no login yet).</summary>
    private static double SessionClock(RynthCore.Plugin.Shared.SessionStatsView v)
        => ReferenceEquals(v, RynthCore.Plugin.Shared.SessionStatsView.Empty)
            ? 0 : Math.Max(0, (DateTime.UtcNow - v.SessionStartUtc).TotalSeconds);

    private static void RenderTarget(CombatHudSnapshot c)
    {
        if (c.TargetId == 0) { ImGui.TextDisabled("Target: none"); return; }
        ImGui.TextColored(ColLabel, "Target:");
        ImGui.SameLine();
        ImGui.TextUnformatted(c.TargetName);
        if (c.TargetDistance >= 0)
        {
            ImGui.SameLine();
            SafeImGui.TextDisabled($"{c.TargetDistance:F0}yd");
        }
        HealthBar("##mrtargethp", c.TargetHealth);
    }

    /// <summary>The live summon when one is out, else (with a server plugin active) the Pets tab's next-essence line.</summary>
    private static void RenderPet(MiniRemoteController? remote, CombatHudSnapshot c)
    {
        if (c.PetId != 0)
        {
            RenderSummon(remote, c);
            return;
        }
        if (!ServerActive(remote)) { ImGui.TextDisabled("Summon: none"); return; }
        string name = remote!.Pets.HudPetName;
        string status = remote.Pets.HudPetStatus;
        if (name.Length == 0) { ImGui.TextDisabled("No combat pet set"); return; }
        ImGui.TextUnformatted(name);
        var col = status switch { "Ready" => ColReady, "Out" => ColOut, "Healing" => ColBusy, _ => ColEmpty };
        SafeImGui.TextColored(col, status);
    }

    private static void RenderSummon(MiniRemoteController? remote, CombatHudSnapshot c)
    {
        ImGui.TextColored(ColLabel, "Summon:");
        ImGui.SameLine();
        ImGui.TextUnformatted(c.PetName);
        if (ServerActive(remote) && remote!.Pets.HudPetStatus == "Healing")
        {
            ImGui.SameLine();
            ImGui.TextColored(ColBusy, "Healing");
        }
        HealthBar("##mrpethp", c.PetHealth);

        long now = Environment.TickCount64;
        if (c.PetExpiresAtMs != 0)
        {
            long leftMs = Math.Max(0, c.PetExpiresAtMs - now);
            var left = TimeSpan.FromMilliseconds(leftMs);
            string text = $"{FormatClock(left)} left";
            if (c.PetLifespanSec > 0)
            {
                float frac = Math.Clamp(leftMs / (c.PetLifespanSec * 1000f), 0f, 1f);
                var col = left.TotalSeconds <= 30 ? ColEmpty : left.TotalSeconds <= 90 ? ColBusy : ColOut;
                Bar("##mrpettime", frac, text, col);
            }
            else
            {
                SafeImGui.TextColored(left.TotalSeconds <= 30 ? ColEmpty : ColOut, text);
            }
        }
        else
        {
            // No RemainingLifespan from the server: show how long it has been out instead.
            var up = TimeSpan.FromMilliseconds(Math.Max(0, now - c.PetSeenAtMs));
            SafeImGui.TextDisabled($"Out {FormatClock(up)}");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("The server hasn't reported this summon's lifespan yet.");
        }
    }

    private static string FormatClock(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>Health ratio bar coloured green / yellow / red; "--" until the first health update.</summary>
    private static void HealthBar(string id, float ratio)
    {
        if (ratio < 0f) { Bar(id, 0f, "HP --", ColLabel); return; }
        var col = ratio > 0.6f ? ColReady : ratio > 0.3f ? ColBusy : ColEmpty;
        Bar(id, ratio, $"HP {ratio * 100f:F0}%", col);
    }

    private static void Bar(string id, float fraction, string overlay, Vector4 color)
    {
        float w = Math.Max(ImGui.GetContentRegionAvail().X, 140f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color with { W = 0.85f });
        ImGui.PushID(id);
        ImGui.ProgressBar(fraction, new Vector2(w, 14f), overlay);
        ImGui.PopID();
        ImGui.PopStyleColor();
    }

    private void RenderStatButtons(MiniRemoteController? remote)
    {
        float w = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        w = Math.Max(w, 70f);
        // A server plugin's own rates follow this session (RynthILT restarts its coins when it restarts).
        if (ImGui.Button("Reset XP", new Vector2(w, 0))) _hud.Post(() => _hud.SessionStats?.RequestReset());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Start the session stats over (RynthTracker, RynthOracle and a server plugin's rates follow this session).");
        ImGui.SameLine();
        if (ImGui.Button("Report", new Vector2(w, 0))) _hud.Post(ReportToChat);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Print the session rates in your chat window (local only).");
    }

    /// <summary>One-line session summary written to local chat (pump thread).</summary>
    private void ReportToChat()
    {
        var v = _hud.SessionStats?.View ?? RynthCore.Plugin.Shared.SessionStatsView.Empty;
        string time = MiniRemoteText.Duration(TimeSpan.FromSeconds(SessionClock(v)));
        string line = $"[RynthAi] Session {time}: XP {HudDraw.Compact(v.XpPerHour)}/h, Lum {HudDraw.Compact(v.LumPerHour)}/h "
                      + $"({HudDraw.Compact(v.LumLast5Min)} last 5m)";
        if (v.PassUpTracked) line += $", Pass-up {HudDraw.Compact(v.PassUpPerHour)}/h";
        line += $", Kills {HudDraw.Compact(v.KillsPerHour)}/h ({v.Kills:N0})";
        line += _hud.Remote()?.ServerHub.Text(RynthServerHub.TextSessionReport) ?? string.Empty;   // the server plugin's rates
        _hud.Chat(line);
    }

    private void RenderSlots(HudState s)
    {
        var slots = s.MiniRemoteSlots.ToArray();
        var pack = _hud.Pack;
        float slotSize = SlotSize(s);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(1, 1));
        ImGui.BeginGroup();
        for (int i = 0; i < slots.Length; i++)
        {
            if (i % HudState.MiniRemoteColumns != 0) ImGui.SameLine();
            var e = slots[i];
            long count = e.IsEmpty ? 0 : pack.CountOf(e.Name);
            ImGui.PushID(i);
            bool clicked;
            IntPtr tex = e.IsEmpty ? IntPtr.Zero : _hud.Icons.GetTexture(e.IconDid);
            if (tex != IntPtr.Zero)
            {
                var tint = count == 0 ? new Vector4(1, 1, 1, 0.35f) : Vector4.One;
                clicked = ImGui.ImageButton("##slot", tex, new Vector2(slotSize, slotSize), Vector2.Zero, Vector2.One, Vector4.Zero, tint);
            }
            else
            {
                string label = e.IsEmpty ? string.Empty : e.Name.Substring(0, 1).ToUpperInvariant();
                if (e.IsEmpty) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.13f, 0.18f, 0.6f));
                clicked = ImGui.Button(label + "##slot", new Vector2(slotSize + 2, slotSize + 2));
                if (e.IsEmpty) ImGui.PopStyleColor();
            }
            if (ImGui.IsItemHovered())
                SafeImGui.SetTooltip(e.IsEmpty ? EmptySlotTip(i) : $"{e.Name}\n{count:N0} carried\nClick to use, right-click for options.\nCtrl+wheel: box size.");
            int slotIndex = i;
            if (clicked)
            {
                // Empty slot: click assigns the selected (or last selected) pack item.
                if (e.IsEmpty) _hud.Post(() => _hud.SetSlotFromSelection(slotIndex));
                else
                {
                    var entry = e;
                    _hud.Post(() => _hud.UseItem(entry));
                }
            }
            AcceptItemDrop(slotIndex, e.IsEmpty);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                _slotMenuIndex = i;
                ImGui.OpenPopup(SlotPopup);
            }
            RenderSlotMenu(i, e);
            ImGui.PopID();
        }
        ImGui.EndGroup();
        ImGui.PopStyleVar();

        // Ctrl+wheel over the grid: bigger / smaller boxes (the window / drawer refits to them).
        var io = ImGui.GetIO();
        if (io.KeyCtrl && io.MouseWheel != 0f && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
        {
            ResizeSlots(s, io.MouseWheel > 0f ? SlotSizeStep : -SlotSizeStep);
            io.MouseWheel = 0f;   // the boxes took it: not the panel's size too
        }
    }

    /// <summary>
    /// ImGui drag-and-drop payload type for one inventory item (uint object id). Must match
    /// InventoryFace.ItemPayloadType in RynthCore.Engine, which publishes it while dragging.
    /// </summary>
    private const string ItemPayloadType = "RYNTH_INV_ITEM";

    /// <summary>
    /// Payload type for an AC-inventory drag whose item the engine guessed from the game selection
    /// (uint object id); engines that hook AC's drag start send the real item as ItemPayloadType
    /// instead. Must match ItemDragBridge.NativePayloadType in RynthCore.Engine. A guess can be the
    /// wrong item, so it is taken by empty slots only and never replaces an assigned one.
    /// </summary>
    private const string GameItemPayloadType = "RYNTH_GAME_ITEM";

    /// <summary>
    /// Makes the last slot button a drop target: RynthCore Inventory drags on any slot, game
    /// inventory drags on <paramref name="empty"/> slots.
    /// </summary>
    private unsafe void AcceptItemDrop(int slot, bool empty)
    {
        if (!ImGui.BeginDragDropTarget()) return;
        ImGuiPayloadPtr payload = ImGui.AcceptDragDropPayload(ItemPayloadType);
        if (payload.NativePtr == null && empty) payload = ImGui.AcceptDragDropPayload(GameItemPayloadType);
        if (payload.NativePtr != null && payload.Data != IntPtr.Zero && payload.DataSize >= sizeof(uint))
        {
            uint objectId = *(uint*)payload.Data;
            _hud.Post(() => _hud.SetSlotFromItemId(slot, objectId));
        }
        ImGui.EndDragDropTarget();
    }

    private string EmptySlotTip(int index)
    {
        string candidate = _hud.AssignCandidate;
        return candidate.Length > 0
            ? $"Slot {index + 1:00}: empty\nClick to assign {candidate}.\nOr drag an item here from the game inventory or the RynthCore Inventory.\nCtrl+wheel: box size."
            : $"Slot {index + 1:00}: empty\nClick an item in your pack, then click here to assign it.\nOr drag an item here from the game inventory or the RynthCore Inventory.\nCtrl+wheel: box size.";
    }

    /// <summary>Per-slot right-click menu (opened inside the slot's ID scope).</summary>
    private void RenderSlotMenu(int index, HudItemEntry e)
    {
        if (!ImGui.BeginPopup(SlotPopup)) return;
        _slotMenuIndex = index;
        SafeImGui.TextDisabled(e.IsEmpty ? $"Slot {index + 1:00}" : $"Slot {index + 1:00}: {e.Name}");
        string candidate = _hud.AssignCandidate;
        if (ImGui.MenuItem(candidate.Length > 0 ? $"Assign {candidate}" : "Assign selected item"))
            _hud.Post(() => _hud.SetSlotFromSelection(index));
        if (!e.IsEmpty && ImGui.MenuItem("Clear slot")) _hud.Post(() => _hud.ClearSlot(index));
        ImGui.EndPopup();
    }

    private void RenderToggles(LegacyUiSettings settings)
    {
        var dash = _hud.Dashboard;
        if (!ImGui.BeginTable("##mrtoggles", 3, ImGuiTableFlags.SizingStretchSame)) return;
        Toggle("Macro", settings.IsMacroRunning, _ => dash.TogglePanelMacro());
        Toggle("Combat", settings.EnableCombat, on => dash.SetSubsystemEnabled(0, on));
        Toggle("Buff", settings.EnableBuffing, on => dash.SetSubsystemEnabled(1, on));
        Toggle("Nav", settings.EnableNavigation, on => dash.SetSubsystemEnabled(2, on));
        Toggle("Loot", settings.EnableLooting, on => dash.SetSubsystemEnabled(3, on));
        Toggle("Meta", settings.EnableMeta, on => dash.SetSubsystemEnabled(4, on));
        Toggle("Pet", settings.SummonPets, on => { settings.SummonPets = on; dash.SaveSettings(); });
        ImGui.EndTable();
    }

    /// <summary>A checkbox in the next table cell whose change is applied on the pump thread.</summary>
    private void Toggle(string label, bool current, Action<bool> apply)
    {
        ImGui.TableNextColumn();
        bool v = current;
        if (ImGui.Checkbox(label + "##mrt", ref v)) _hud.Post(() => apply(v));
    }

    private void RenderRebuff()
    {
        var dash = _hud.Dashboard;
        ImGui.TextColored(ColLabel, "Rebuff:");
        float w = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
        w = Math.Max(w, 70f);
        if (ImGui.Button("FB", new Vector2(w, 0))) _hud.Post(dash.RequestForceRebuff);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Force rebuff: recast every buff now.");
        ImGui.SameLine();
        if (ImGui.Button("CFB", new Vector2(w, 0))) _hud.Post(dash.RequestCancelForceRebuff);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Cancel the running rebuff.");
    }

    /// <summary>
    /// The Options popup. <paramref name="embedded"/>: drawn in an engine surface, where the
    /// floating window's lock and hide don't apply. The dashboard drawer offers the floating
    /// window as a separate choice; the engine's Mini Remote window replaces it, so it doesn't.
    /// </summary>
    private void RenderOptions(HudState s, bool embedded)
    {
        if (_openOptions)
        {
            _openOptions = false;
            ImGui.OpenPopup(OptionsPopup);
        }
        if (!ImGui.BeginPopup(OptionsPopup)) return;
        ImGui.TextDisabled("Mini Remote sections");
        Flag("Session stats", ref s.MiniShowStats);
        Flag("Attack target", ref s.MiniShowTarget);
        Flag("Pet / summon", ref s.MiniShowPet);
        Flag("Item slots", ref s.MiniShowGems);
        // The drawer's dashboard has these switches already; the setting is for the windows.
        if (!InDrawer) Flag("Toggles", ref s.MiniShowToggles);
        Flag("Rebuff", ref s.MiniShowRebuff);
        Flag("Translate", ref s.MiniShowTranslate);
        // The server plugin's Remote sections (RynthILT: Bank, Guardian answer): its own switches.
        _hud.Remote()?.ServerHub.RenderSection(RynthServerHub.SectionRemoteOptions);
        ImGui.Separator();
        Flag("Horizontal layout", ref s.MiniRemoteHorizontal);
        RenderSlotSizeSlider(s);
        if (embedded && _inWindow)
        {
            if (ImGui.MenuItem("Inventory HUDs setup...")) s.ShowSetup = true;
        }
        else if (embedded)
        {
            Flag("Floating window too", ref s.ShowMiniRemote);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Also show the Mini Remote as its own window (/ra remote).");
            if (ImGui.MenuItem("Inventory HUDs setup...")) s.ShowSetup = true;
        }
        else
        {
            Flag("Lock position", ref s.MiniRemoteLocked);
            if (ImGui.MenuItem("Inventory HUDs setup...")) s.ShowSetup = true;
            if (ImGui.MenuItem("Hide Mini Remote")) s.ShowMiniRemote = false;
        }
        ImGui.EndPopup();
    }

    /// <summary>"Slot size" slider plus a reset, the menu twin of Ctrl+wheel over the slots.</summary>
    private static void RenderSlotSizeSlider(HudState s)
    {
        float size = SlotSize(s);
        ImGui.SetNextItemWidth(120f);
        if (ImGui.SliderFloat("Slot size##mrslotsize", ref size, HudState.MinMiniRemoteSlotSize, HudState.MaxMiniRemoteSlotSize, "%.0f px"))
            s.MiniRemoteSlotSize = MathF.Round(size);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Item slot box size (or Ctrl+wheel over the slots).");
        ImGui.SameLine();
        bool isDefault = s.MiniRemoteSlotSize == HudState.DefaultMiniRemoteSlotSize;
        if (isDefault) ImGui.BeginDisabled();
        if (ImGui.SmallButton("Default##mrslotsize")) s.MiniRemoteSlotSize = HudState.DefaultMiniRemoteSlotSize;
        if (isDefault) ImGui.EndDisabled();
    }

    // ── Right-click menu: Character, then this server's plugin windows ─────────

    /// <summary>
    /// The right-click menu. Top, "Character": windows every server has. Bottom: the server
    /// section, i.e. the installed server plugins' windows for the world you're on (RynthCore
    /// sends them, see <see cref="ServerMenuModel"/>), or the "Treat this world as ..." choices
    /// when none matches.
    /// </summary>
    private void RenderMenu(HudState s)
    {
        if (!ImGui.BeginPopup(MenuPopup)) return;
        try
        {
            RenderCharacterMenu(s);
            RenderServerMenu(_hud.Remote());
        }
        finally { ImGui.EndPopup(); }
    }

    /// <summary>
    /// "Character": RynthAi's own windows, the universal Mini Remote tabs (Pets, Crafting), RynthOracle's
    /// quest and fellowship windows (the Quests and Fellowship tabs moved there 2026-10-13) and
    /// RynthCore's panels, when chat commands can be run.
    /// </summary>
    private void RenderCharacterMenu(HudState s)
    {
        ImGui.TextDisabled("Character");
        var settings = _hud.Dashboard.Settings;
        if (ImGui.MenuItem("Spells##mrspells", "/ra spells", settings.ShowSpellsWindow))
            settings.ShowSpellsWindow = !settings.ShowSpellsWindow;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("The spell catalog and buff profiles.");
        foreach (string id in MiniRemoteTabs.Universal)
        {
            if (ImGui.MenuItem(MiniRemoteTabs.Label(id) + "##mrchar_" + id, "/ra hub open " + id))
                _hud.HandleTabCommand(id, "show");
        }
        if (_hud.Host.HasInvokeChatParser)
        {
            // RynthOracle's commands (it toggles its own windows; without RynthOracle nothing answers "/ro").
            // The character overview and its session are RynthOracle's Character tab (2026-10-13); a
            // server's own screens are its plugin's (the server section below).
            if (ImGui.MenuItem("Character##mrcharacter", MovedTabs.OracleCharacter)) _hud.RunEngineCommand(MovedTabs.OracleCharacter);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Character tab: the character and this session.");
            if (ImGui.MenuItem("Quests##mrquests", "/ro")) _hud.RunEngineCommand("/ro");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle: quest timers, tracking and rewards.");
            if (ImGui.MenuItem("Quest Timers##mrqtimers", "/ro timers")) _hud.RunEngineCommand("/ro timers");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Quest Timers window: your tracked quests and the time left on each.");
            if (ImGui.MenuItem("Fellowship##mrfellow", "/ro fellowship")) _hud.RunEngineCommand("/ro fellowship");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthOracle's Fellowship tab: members, levels and XP sharing.");
            if (ImGui.MenuItem("Skills##mrskills", "/rc skills")) _hud.RunEngineCommand("/rc skills toggle");
            if (ImGui.MenuItem("Inventory##mrinv", "/rc inventory")) _hud.RunEngineCommand("/rc inventory");
            if (ImGui.MenuItem("Item Count##mritemcount", "/rc itemcount")) _hud.RunEngineCommand("/rc itemcount");
        }
        if (ImGui.MenuItem("Inventory HUDs setup...##mrsetup")) s.ShowSetup = true;
    }

    /// <summary>
    /// The server section. Each section RynthCore marks active is listed, and so is the server
    /// plugin's own server while this character forces it (the plugin's override, e.g. RynthILT's
    /// "Treat this world as ILT"). Without RynthCore's list it falls back to the server plugin's own
    /// menu entries (its "server-menu" section).
    /// </summary>
    private void RenderServerMenu(MiniRemoteController? remote)
    {
        ServerMenuGroup[]? groups = ServerMenuModel.Groups;
        if (groups == null)
        {
            remote?.ServerHub.RenderSection(RynthServerHub.SectionServerMenu);
            return;
        }
        if (groups.Length == 0) return;   // no server plugin installed

        // The server the active server plugin's tabs and status belong to (RynthILT: "ilt"); "" without one.
        string pluginServer = remote?.ServerHub.Text(RynthServerHub.TextServerId) ?? string.Empty;
        bool any = false;
        foreach (ServerMenuGroup g in groups)
        {
            bool ours = pluginServer.Length > 0 && g.Id.Equals(pluginServer, StringComparison.OrdinalIgnoreCase);
            bool forcedHere = ours && remote?.ServerHub.ServerForced == true;
            if (!g.Active && !forcedHere) continue;
            any = true;
            ImGui.Separator();
            SafeImGui.TextDisabled("Server: " + (g.Active ? g.Name : g.Name + " (forced)"));
            foreach (ServerMenuEntry item in g.Items)
            {
                string? tab = ServerMenuModel.TabOf(item.Feature);
                // Universal tabs are under Character; RynthCore 2026.10.5.30 and older list them here too
                // (with the retired Quests tab, now RynthOracle's).
                if (tab != null && (MiniRemoteTabs.Parse(tab) != null || tab.Equals("quests", StringComparison.OrdinalIgnoreCase))) continue;
                // Character (an older RynthCore still lists it) moved out: it is under Character above
                // (RynthOracle's Character tab).
                if (tab != null && MovedTabs.CommandFor(tab) != null) continue;
                bool enabled = tab == null || TabAvailable(remote, tab);
                if (ImGui.MenuItem(item.Label + "##mrsrv_" + g.Id, item.Command, false, enabled))
                    RunServerItem(item, tab);
                if (item.Tooltip.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    SafeImGui.SetTooltip(enabled ? item.Tooltip : item.Tooltip + "\nShows once the server reports this feature on.");
            }
            // The plugin's status under its server (RynthILT: why no ILT feature is on, Refresh).
            if (ours) remote?.ServerHub.RenderSection(RynthServerHub.SectionServerStatus);
        }
        if (any) return;

        ImGui.Separator();
        ImGui.TextDisabled("Server: no installed server plugin matches this world");
        // Forcing a server lives in one place since 2026-10-13: RynthCore's Options > Diagnostics.
        if (_hud.Host.HasInvokeChatParser && ImGui.MenuItem("Force a server... (Options > Diagnostics)##mrforce", MovedTabs.Diagnostics))
            _hud.RunEngineCommand(MovedTabs.Diagnostics);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("RynthCore's server selection (Options > Diagnostics) shows a server's windows here anyway.\n"
                                 + "It only says which world this is: the server still decides which features exist.");
    }

    /// <summary>A Mini Remote tab switches here and now; anything else is a RynthCore command.</summary>
    private void RunServerItem(ServerMenuEntry item, string? tab)
    {
        if (tab != null) _hud.HandleTabCommand(tab, "show");
        else if (item.Command.Length > 0) _hud.RunEngineCommand(item.Command);
    }

    /// <summary>The server plugin reports that tab's feature on (false without one).</summary>
    private static bool TabAvailable(MiniRemoteController? remote, string tab)
    {
        if (remote == null) return false;
        foreach (ServerTabInfo t in remote.ServerHub.Tabs())
            if (string.Equals(t.Id, tab, StringComparison.OrdinalIgnoreCase)) return t.Available;
        return false;
    }

    private static void Flag(string label, ref bool value)
    {
        bool v = value;
        if (ImGui.Checkbox(label, ref v)) value = v;
    }
}

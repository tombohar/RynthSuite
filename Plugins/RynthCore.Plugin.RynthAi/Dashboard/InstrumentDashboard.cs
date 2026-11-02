// ============================================================================
//  RynthAi - Dashboard/InstrumentDashboard.cs
//  RynthAi's dashboard, drawn by RynthAi in its own window (2026-10-21; RynthCore's
//  ImGui/Panels/RynthAiFace.cs drew it until then). RynthCore hosts the window
//  (RynthAiPlugin.Dashboard.cs declares it: "RynthAi" with "takeOver", so it keeps
//  the engine panel's saved place, bar button, hotkeys and /rc commands, and
//  "chrome": "none" + "fitHeight", plugin API v90) and RynthAi draws all of it,
//  its frame included.
//
//  The look is the instrument design (Claude Design canvas "RynthSuite Dashboard",
//  2026-10-21): one machined surface, hairlines instead of boxes, keys with LEDs.
//  Top to bottom:
//    header     RYNTH (R in the accent), the run state's LED and word, then the
//               target bar, vitals, lock, options and minimize buttons. The row
//               is the drag handle (not while locked); right-click: options;
//               Ctrl+wheel: the text size.
//    activity   an inset line: what the bot is doing (coloured, with its icon) and
//               the meta's state.
//    controls   RUN / STOP, the five subsystem keys (lit LED = on; right-click:
//               their settings), then RynthSort's auto-sort key (when it's loaded;
//               right-click: its window) and Force rebuff.
//    launchers  a tray of window buttons (lit while open), labels when wide.
//    footer     FPS (coloured by rate), RynthAi's version, bot and client uptime.
//    bars       the vitals and the target (15 segments, coloured by health).
//  Minimized keeps the header, the activity line, the controls and the bars.
//  Drawers on the edges, docked or popped out: Ranges, Loaded files, Patrol and the Mini
//  Remote on the left, My settings on the right; a tab's right-click moves its
//  drawer to the other edge. Ranges, Patrol and My settings show the Settings
//  panel's rows, which RynthCore draws into them (RenderSettingsDrawer, v90).
//  Popped out the pickers are in the options as well.
//
//  Colours: the player's theme (GetUiColor v90: Background, KeyFace, Hairline,
//  Inset, Text, Muted, Faint, Accent, Highlight); health, stamina, mana and the
//  activities keep fixed colours. Fonts: Barlow (Narrow*) for labels, Cascadia
//  Mono (Dash*) for numbers, the engine's icons.
//
//  AC's render thread. Reads RynthAi's state directly (IDashboardHost); every
//  click that acts is queued for the plugin tick.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Dashboard;

/// <summary>What the dashboard reads from RynthAi and asks of it (RynthAiPlugin.Dashboard.cs).</summary>
internal interface IDashboardHost
{
    RynthCoreHost Host { get; }
    DashboardReadout Read();
    DashboardFiles ReadFiles();
    DashboardPrefs Prefs { get; }
    string Version { get; }
    DateTime LoadedUtc { get; }
    /// <summary>Runs <paramref name="action"/> on the plugin tick.</summary>
    void Queue(Action action);
    /// <summary>A chat command, run on the tick (InvokeChatParser).</summary>
    void Command(string command);
    void ToggleMacro();
    void SetSubsystem(int id, bool on);
    void ForceRebuff();
    void CancelForceRebuff();
    void AdjustOpacity(float delta);
    void ToggleLock();
    void SelectProfile(int kind, int index);
    /// <summary>The Loot Mode (Loot.LootModes): the loot profile, or rares and needed mana stones only.</summary>
    void SetLootMode(int mode);
    /// <summary>RynthSort's auto-sort: -1 RynthSort not loaded (or not logged in, or too old to say), 0 off, 1 on.</summary>
    int AutoSort { get; }
    /// <summary>Fight the monster I select (combat setting), for the options menu's toggle.</summary>
    bool FightSelectedTarget { get; }
    void SetAutoSort(bool on);
    /// <summary>RynthSort's own window, shown or hidden.</summary>
    void ToggleSortWindow();
    void StartPatrol();
    /// <summary>Outdoor hunting on or off (on the plugin tick), and whether it is on.</summary>
    void ToggleHunt();
    bool HuntActive { get; }
    /// <summary>The small Outdoor Hunt window (its two distances) open or closed.</summary>
    void ToggleHuntPanel();
    void ToggleSpells();
    /// <summary>The Classic (retail) look: RynthCore's own face comes back for it.</summary>
    void UseClassicLook();
    /// <summary>The Mini Remote, drawn into the current window; the size it wants.</summary>
    bool DrawMiniRemote(out Vector2 want);
}

internal sealed unsafe class InstrumentDashboard
{
    public const string Title = "RynthAi";

    private readonly IDashboardHost _d;

    public InstrumentDashboard(IDashboardHost host) => _d = host;

    // ── Size (pixels at 100% text size, times the panel's size factor) ──────

    private float _k = 1f;
    private float Px(float v) => v * _k;

    private const float PadX = 8, PadTop = 7, PadBottom = 8, Radius = 4;
    private const float RowH = 20, Gap = 6, ReadoutH = 20;
    private const float CtlH = 30, RunW = 56, KeyW = 27, KeyGap = 2, FrW = 30;
    private const float TrayH = 24, FootH = 12;
    private const float Chip = 18, ChipGap = 1;
    private const float VitalH = 12, VitalGap = 3, VitalLabelW = 16, VitalNumW = 62, VitalColGap = 7, BarH = 6;
    private const float TabW = 19, TabH = 22, TabGap = 2, TabsTop = 30, DrawerW = 216;

    // ── Colours (ABGR), read once a frame ────────────────────────────────

    private uint _bg, _key, _inset, _line, _text, _dim, _faint, _accent, _hi;
    private static readonly uint Hp = Argb(0xFFEB4E46), St = Argb(0xFF56C86D), Mn = Argb(0xFF4495EA),
        HpText = Argb(0xFFF98075), StText = Argb(0xFF73D885), MnText = Argb(0xFF7FB9F9),
        Gold = Argb(0xFFEFC054), Violet = Argb(0xFFC5ABF4),
        Black = Argb(0xFF000000), White = Argb(0xFFFFFFFF);

    private void ReadColors()
    {
        _bg = Color(RynthUiColor.Background);
        _key = Color(RynthUiColor.KeyFace);
        _inset = Color(RynthUiColor.Inset);
        _line = Color(RynthUiColor.Hairline);
        _text = Color(RynthUiColor.Text);
        _dim = Color(RynthUiColor.Muted);
        _faint = Color(RynthUiColor.Faint);
        _accent = Color(RynthUiColor.Accent);
        _hi = Color(RynthUiColor.Highlight);
    }

    /// <summary>ABGR mix: <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>, <paramref name="a"/>'s alpha.</summary>
    private static uint Mix(uint a, uint b, float t)
    {
        static uint Ch(uint c, int s) => (c >> s) & 0xFF;
        uint r = (uint)MathF.Round(Ch(a, 0) + (Ch(b, 0) - (float)Ch(a, 0)) * t);
        uint g = (uint)MathF.Round(Ch(a, 8) + (Ch(b, 8) - (float)Ch(a, 8)) * t);
        uint bl = (uint)MathF.Round(Ch(a, 16) + (Ch(b, 16) - (float)Ch(a, 16)) * t);
        return (a & 0xFF000000) | (bl << 16) | (g << 8) | r;
    }

    private static uint A(uint c, float alpha) => WithAlpha(c, (uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));

    // ── Frame ───────────────────────────────────────────────────────────

    private bool _popped;
    private DashboardReadout _r;

    /// <summary>
    /// Draws the dashboard into the host's window (its body child, edge to edge) and returns the
    /// size it takes (pixels): the host fits the window's height to it. Render thread.
    /// </summary>
    public Vector2 Draw(bool popped)
    {
        _popped = popped;
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui10).FontSize / 10f);
        _r = _d.Read();
        ReadColors();
        DashboardPrefs prefs = _d.Prefs;

        Vector2 origin = R(ImGui.GetCursorScreenPos());
        float width = MathF.Floor(ImGui.GetContentRegionAvail().X);
        if (width < 40) return new Vector2(width, Px(54));
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        // Channel 1: the content; channel 0: the frame under it, once the content's height is known.
        // Merged whatever happens: a split left open would break every draw after this one.
        float height;
        dl.ChannelsSplit(2);
        bool merged = false;
        try
        {
            dl.ChannelsSetCurrent(1);
            float x0 = origin.X + Px(PadX), w = width - 2 * Px(PadX);
            float y = origin.Y + Px(PadTop);
            y = Header(dl, prefs, x0, y, w);
            y += Px(Gap);
            y = Readout(dl, x0, y, w);
            y += Px(Gap);
            y = Controls(dl, x0, y, w);
            if (!prefs.Minimized)
            {
                y += Px(Gap);
                y = Launchers(dl, prefs, x0, y, w);
                y += Px(Gap);
                y = Footer(dl, x0, y, w);
            }
            if (prefs.ShowVitals || prefs.ShowTarget)
                y = Bars(dl, prefs, origin.X, width, x0, y, w);
            y += Px(PadBottom);
            height = MathF.Ceiling(y - origin.Y);

            dl.ChannelsSetCurrent(0);
            Frame(dl, origin, new Vector2(width, height), _r.Opacity);
            dl.ChannelsMerge();
            merged = true;
        }
        finally
        {
            if (!merged) dl.ChannelsMerge();
        }

        // The window's content is the panel (the host fits its height to it).
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));

        OptionsMenu(prefs);
        CharMenu();
        // Popped out too (Tom 2026-10-24: "many of the settings are built into the drawers"): the
        // pop-out's picture grows past the panel to hold them, and RynthCore's pop-outs take clicks
        // on a window drawn there, not only on a menu.
        Drawers(prefs, origin, new Vector2(width, height));
        return new Vector2(width, height);
    }

    /// <summary>
    /// The panel's surface: the background at the player's opacity, a hairline border, a lit top edge,
    /// and docked a soft shadow (not while a drawer slides: the shadow isn't clipped to its window).
    /// </summary>
    private void Frame(ImDrawListPtr dl, Vector2 min, Vector2 size, float opacity, bool shadow = true)
    {
        Vector2 max = min + size;
        float r = Px(Radius);
        if (!_popped && shadow)
        {
            // A soft shadow under the panel, on the background list (outside the window's clip).
            ImDrawListPtr back = ImGui.GetBackgroundDrawList();
            for (int i = 1; i <= 3; i++)
            {
                float grow = Px(2 * i), drop = Px(3 + i);
                back.AddRectFilled(min - new Vector2(grow, grow - drop), max + new Vector2(grow, grow + drop),
                    A(Black, 0.10f * opacity), r + grow);
            }
        }
        dl.AddRectFilled(min, max, A(_bg, Math.Clamp(opacity, 0.1f, 1f)), r);
        dl.AddLine(new Vector2(min.X + r, min.Y + 1.5f), new Vector2(max.X - r, min.Y + 1.5f), A(White, 0.05f));
        dl.AddRect(min, max, _line, r);
    }

    // ── Header: RYNTH, the run state, five buttons ──────────────────────

    private const string DragTip = "Drag to move the dashboard.\nRight-click: options. Ctrl+wheel here: bigger or smaller.";

    private float Header(ImDrawListPtr dl, DashboardPrefs prefs, float x0, float y, float w)
    {
        float rowH = Px(RowH);
        float chip = Px(Chip), chipGap = Px(ChipGap);
        const int chips = 5;
        float chipsW = chips * chip + (chips - 1) * chipGap;

        // The drag surface: the row left of the buttons.
        ImGui.SetCursorScreenPos(new Vector2(x0, y));
        ImGui.InvisibleButton("##dash_drag", new Vector2(Math.Max(1, w - chipsW - Px(4)), rowH));
        if (!_r.Locked) _d.Host.PanelDragWithLastItem();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) OpenOptions(ImGui.GetMousePos(), anchorRight: false);
        SafeImGui.SetItemTooltip(_r.Locked ? "Locked in place (the lock button unlocks it).\nRight-click: options. Ctrl+wheel here: bigger or smaller." : DragTip);

        // RYNTH: the R in the accent, the rest in the text colour, spaced out.
        ImFontPtr wm = Font(RynthUiFont.NarrowBold13);
        float track = 0.22f * wm.FontSize;
        float ty = MathF.Round(y + (rowH - wm.FontSize) * 0.5f);
        float x = Tracked(dl, wm, wm.FontSize, new Vector2(x0, ty), _accent, "R", track) + track;
        x = Tracked(dl, wm, wm.FontSize, new Vector2(x, ty), _text, "YNTH", track);

        // The run state: an LED (breathing while running) and the word.
        x += Px(10);
        bool run = _r.MacroRunning;
        Vector2 led = new(x + Px(3), y + rowH * 0.5f);
        if (run)
        {
            float pulse = 0.55f + 0.45f * MathF.Sin((float)(ImGui.GetTime() * Math.PI * 2 / 2.6));
            dl.AddCircleFilled(led, Px(5.5f), A(_accent, 0.10f + 0.12f * pulse), 20);
            dl.AddCircleFilled(led, Px(3), _accent, 16);
        }
        else
        {
            dl.AddCircleFilled(led, Px(3), _inset, 16);
            dl.AddCircle(led, Px(3), _line, 16, 1f);
        }
        ImFontPtr sf = Font(RynthUiFont.Narrow10);
        Tracked(dl, sf, sf.FontSize, new Vector2(x + Px(11), MathF.Round(y + (rowH - sf.FontSize) * 0.5f)),
            run ? _accent : _faint, run ? "RUNNING" : "STOPPED", 0.14f * sf.FontSize);

        // The buttons.
        float cx = x0 + w - chipsW, cy = y + (rowH - chip) * 0.5f;
        if (ChipButton("##c_target", RynthIcons.Crosshair, new Vector2(cx, cy), prefs.ShowTarget ? _text : _faint,
                prefs.ShowTarget ? "Target bar on: the target's name and health at the bottom. Click to hide it."
                                 : "Target bar off. Click to show the target's name and health at the bottom."))
            prefs.SetShowTarget(!prefs.ShowTarget);
        cx += chip + chipGap;
        if (ChipButton("##c_vitals", RynthIcons.Heartbeat, new Vector2(cx, cy), prefs.ShowVitals ? _text : _faint,
                prefs.ShowVitals ? "Your vitals on: health, stamina and mana at the bottom. Click to hide them."
                                 : "Your vitals off. Click to show your health, stamina and mana at the bottom."))
            prefs.SetShowVitals(!prefs.ShowVitals);
        cx += chip + chipGap;
        if (ChipButton("##c_lock", _r.Locked ? RynthIcons.Lock : RynthIcons.LockOpen, new Vector2(cx, cy), _r.Locked ? _hi : _faint,
                _r.Locked ? "Locked in place. Click to unlock the dashboard." : "Click to lock the dashboard in place."))
            _d.ToggleLock();
        cx += chip + chipGap;
        bool menuOpen = ImGui.IsPopupOpen(OptionsId);
        if (ChipButton("##c_menu", RynthIcons.DotsThree, new Vector2(cx, cy), menuOpen ? _text : _faint,
                "Options: opacity, size, the Classic look, pop out.", menuOpen))
            OpenOptions(new Vector2(cx + chip, cy + chip + Px(4)), anchorRight: true);
        cx += chip + chipGap;
        if (ChipButton("##c_min", prefs.Minimized ? RynthIcons.ArrowsOutLineVertical : RynthIcons.ArrowsInLineVertical,
                new Vector2(cx, cy), _faint, prefs.Minimized ? "Expand" : "Minimize"))
            prefs.SetMinimized(!prefs.Minimized);

        // Ctrl+wheel over the dashboard: RynthCore steps its size (the same over any panel).
        return y + rowH;
    }

    /// <summary>A quiet header button: an icon that lights on hover. True when clicked.</summary>
    private bool ChipButton(string id, string icon, Vector2 pos, uint color, string tip, bool held = false)
    {
        Vector2 size = new(Px(Chip), Px(Chip));
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(tip);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (hot || held) dl.AddRectFilled(pos, pos + size, _key, Px(3));
        DrawIcon(dl, icon, pos, size, Px(12), hot ? _text : color);
        return clicked;
    }

    // ── Activity line ───────────────────────────────────────────────────

    private string _actShown = "", _actCaps = "";
    private string _stateShown = "";

    private float Readout(ImDrawListPtr dl, float x0, float y, float w)
    {
        float h = Px(ReadoutH);
        Vector2 min = new(x0, y), max = new(x0 + w, y + h);
        float r = Px(3);
        dl.AddRectFilled(min, max, _inset, r);
        // Inset: a dark lip along the top inside, the hairline around, a faint light edge under it.
        dl.AddLine(new Vector2(min.X + r, min.Y + 1.5f), new Vector2(max.X - r, min.Y + 1.5f), A(Black, 0.45f));
        dl.AddLine(new Vector2(min.X + r, min.Y + 2.5f), new Vector2(max.X - r, min.Y + 2.5f), A(Black, 0.18f));
        dl.AddRect(min, max, Mix(_inset, _line, 0.75f), r);
        dl.AddLine(new Vector2(min.X + r, max.Y + 0.5f), new Vector2(max.X - r, max.Y + 0.5f), A(White, 0.04f));

        string activity = _r.Activity;
        if (!ReferenceEquals(activity, _actShown)) { _actShown = activity; _actCaps = activity.ToUpperInvariant(); }
        ActivityStyle(activity, out uint color, out string icon);

        ImFontPtr mono = Font(RynthUiFont.Dash10), small = Font(RynthUiFont.Dash9);
        float tx = x0 + Px(7);
        float ty = MathF.Round(y + (h - mono.FontSize) * 0.5f);
        dl.PushClipRect(min + new Vector2(Px(3), 0), max - new Vector2(Px(3), 0), true);
        if (icon.Length > 0)
        {
            DrawIcon(dl, icon, new Vector2(tx, y), new Vector2(Px(11), h), Px(11), color);
            tx += Px(11) + Px(5);
        }
        Tracked(dl, mono, mono.FontSize, new Vector2(tx, ty), color, _actCaps, 0.06f * mono.FontSize);

        // The meta's state, on the right: META faint, then the state.
        string state = _r.MetaState;
        if (!ReferenceEquals(state, _stateShown)) _stateShown = state;
        float stateW = mono.CalcTextSizeA(mono.FontSize, float.MaxValue, 0f, state).X;
        float labelW = TrackedWidth(small, small.FontSize, "META", 0.12f * small.FontSize);
        float sx = x0 + w - Px(7) - stateW;
        dl.AddText(mono, mono.FontSize, new Vector2(MathF.Round(sx), ty), _dim, state);
        Tracked(dl, small, small.FontSize, new Vector2(MathF.Round(sx - Px(5) - labelW), MathF.Round(ty + (mono.FontSize - small.FontSize) * 0.6f)),
            _faint, "META", 0.12f * small.FontSize);
        dl.PopClipRect();

        ImGui.SetCursorScreenPos(min);
        // A click asks why it is doing that (or nothing): /ra why's diagnosis, in chat.
        if (ImGui.InvisibleButton("##activity", new Vector2(w, h))) _d.Command("/ra why");
        // Boost Nav Priority (owner's rule, 2026-11-02): it reads Idle or Navigating, and says why.
        SafeImGui.SetItemTooltip("Bot activity: " + activity
            + (_r.BoostNav ? "\nBoost Nav Priority is on: RynthAi only navigates (no hunting, no looting)." : "")
            + "\nMeta state: " + state + "\nClick: why it's doing that, in chat (/ra why).");
        return y + h;
    }

    /// <summary>The activity's colour and icon: red in a fight, gold looting, the text colour moving, and so on.</summary>
    private void ActivityStyle(string activity, out uint color, out string icon)
    {
        switch (activity)
        {
            case "Combat": color = HpText; icon = RynthIcons.Sword; break;
            case "Looting":
            case "Salvaging": color = Gold; icon = RynthIcons.Bag; break;
            case "Navigating": color = _text; icon = RynthIcons.SneakerMove; break;
            case "Buffing": color = Violet; icon = RynthIcons.Sparkle; break;
            case "Healing": color = HpText; icon = RynthIcons.Heart; break;
            case "Restoring mana": color = MnText; icon = RynthIcons.Drop; break;
            case "Restoring stamina": color = StText; icon = RynthIcons.Lightning; break;
            case "Idle": color = _dim; icon = ""; break;
            default: color = _text; icon = ""; break;
        }
    }

    // ── Controls: RUN / STOP, the subsystem keys, Force rebuff ──────────

    private static readonly (string Icon, string Name, string Right)[] Subsystems =
    {
        (RynthIcons.Sword, "Combat", "the Monsters panel (weapons, spells and rules per monster)"),
        (RynthIcons.Sparkle, "Buffing", "the buffing settings"),
        (RynthIcons.SneakerMove, "Navigation", "the Nav panel (routes)"),
        (RynthIcons.Bag, "Looting", "the Loot Editor on the loot profile in use"),
        (RynthIcons.Code, "Meta (the macro rules)", "the Meta panel"),
    };

    private float Controls(ImDrawListPtr dl, float x0, float y, float w)
    {
        float h = Px(CtlH), r = Px(3);
        bool run = _r.MacroRunning;

        // RUN / STOP.
        Vector2 rp = new(x0, y), rs = new(Px(RunW), h);
        ImGui.SetCursorScreenPos(rp);
        if (ImGui.InvisibleButton("##run", rs)) _d.ToggleMacro();
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(run ? "Macro running. Click to stop it." : "Macro stopped. Click to start it.");
        if (run)
        {
            dl.AddRect(rp - new Vector2(1, 1), rp + rs + new Vector2(1, 1), A(_accent, 0.12f), r + 1, ImDrawFlags.None, 2f);
            dl.AddRectFilled(rp, rp + rs, Mix(_bg, _accent, 0.14f), r);
            dl.AddLine(new Vector2(rp.X + r, rp.Y + 1.5f), new Vector2(rp.X + rs.X - r, rp.Y + 1.5f), A(_accent, 0.20f));
            dl.AddRect(rp, rp + rs, Mix(_bg, _accent, hot ? 0.75f : 0.55f), r);
        }
        else
        {
            dl.AddRectFilled(rp, rp + rs, _key, r);
            dl.AddLine(new Vector2(rp.X + r, rp.Y + 1.5f), new Vector2(rp.X + rs.X - r, rp.Y + 1.5f), A(White, 0.06f));
            dl.AddRect(rp, rp + rs, hot ? Mix(_line, _accent, 0.4f) : _line, r);
        }
        {
            uint fg = run ? _accent : _text;
            ImFontPtr bf = Font(RynthUiFont.NarrowBold11);
            string word = run ? "STOP" : "RUN";
            float track = 0.14f * bf.FontSize;
            float glyph = Px(9);
            float total = glyph + Px(6) + TrackedWidth(bf, bf.FontSize, word, track);
            float gx = MathF.Round(rp.X + (rs.X - total) * 0.5f), gy = MathF.Round(rp.Y + (h - glyph) * 0.5f);
            if (run)
                dl.AddRectFilled(new Vector2(gx + glyph * 0.15f, gy + glyph * 0.15f), new Vector2(gx + glyph * 0.85f, gy + glyph * 0.85f), fg, Px(1));
            else
                dl.AddTriangleFilled(new Vector2(gx + glyph * 0.2f, gy + glyph * 0.1f), new Vector2(gx + glyph * 0.2f, gy + glyph * 0.9f),
                    new Vector2(gx + glyph * 0.9f, gy + glyph * 0.5f), fg);
            Tracked(dl, bf, bf.FontSize, new Vector2(gx + glyph + Px(6), MathF.Round(rp.Y + (h - bf.FontSize) * 0.5f)), fg, word, track);
        }

        // The subsystem keys. With RynthSort loaded its auto-sort key sits at the right beside Force
        // rebuff, and at the narrowest window every key gives up a pixel for it.
        int sort = _d.AutoSort;
        float kw = Px(KeyW);
        if (sort >= 0)
            kw = MathF.Min(kw, MathF.Floor((w - Px(RunW) - 2 * Px(Gap) - Subsystems.Length * Px(KeyGap) - Px(FrW)) / (Subsystems.Length + 1)));
        float kx = x0 + Px(RunW) + Px(Gap);
        bool[] on = { _r.Combat, _r.Buffing, _r.Navigation, _r.Looting, _r.Meta };
        for (int i = 0; i < Subsystems.Length; i++)
        {
            Key(dl, i, on[i], new Vector2(kx, y), new Vector2(kw, h));
            kx += kw + Px(KeyGap);
        }
        float keysEnd = kx - Px(KeyGap);

        // Auto-sort, then Force rebuff, set apart at the right.
        Vector2 fp = new(x0 + w - Px(FrW), y), fs = new(Px(FrW), h);
        float group = fp.X;
        if (sort >= 0)
        {
            group = fp.X - Px(KeyGap) - kw;
            SortKey(dl, sort == 1, new Vector2(group, y), new Vector2(kw, h));
        }
        float lx = MathF.Round(MathF.Max(group - Px(Gap), keysEnd + (group - keysEnd) * 0.5f)) - 0.5f;
        dl.AddLine(new Vector2(lx, y + (h - Px(18)) * 0.5f), new Vector2(lx, y + (h + Px(18)) * 0.5f), _line);
        ImGui.SetCursorScreenPos(fp);
        if (ImGui.InvisibleButton("##fr", fs, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight)
            && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            _d.ForceRebuff();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _d.CancelForceRebuff();
        hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip("Force rebuff: recast all buffs. Right-click: cancel.");
        dl.AddRectFilled(fp, fp + fs, hot ? Mix(_key, _hi, 0.12f) : _key, r);
        dl.AddLine(new Vector2(fp.X + r, fp.Y + 1.5f), new Vector2(fp.X + fs.X - r, fp.Y + 1.5f), A(White, 0.06f));
        dl.AddRect(fp, fp + fs, _line, r);
        DrawIcon(dl, RynthIcons.ArrowsClockwise, fp, fs, Px(15), _hi);
        return y + h;
    }

    /// <summary>
    /// A subsystem key: lit (raised, the icon in the text colour, its LED in the accent) when on,
    /// flat and dim when off. Click: on/off; right-click: its settings.
    /// </summary>
    private void Key(ImDrawListPtr dl, int i, bool on, Vector2 pos, Vector2 size)
    {
        var s = Subsystems[i];
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(Id("##key", i), size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight)
                       && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        bool right = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        bool hot = ImGui.IsItemHovered();
        string mode = i == 3 && Loot.LootModes.IsRares(_r.LootMode)
            ? "\nLoot Mode: rares and needed mana stones only (the loot profile is not used)." : "";
        SafeImGui.SetItemTooltip((on ? s.Name + " on. Click to turn it off." : s.Name + " off. Click to turn it on.") + mode + "\nRight-click: " + s.Right + ".");
        KeyFace(dl, s.Icon, on, hot, pos, size);
        if (clicked) _d.SetSubsystem(i, !on);
        if (right) SubsystemSettings(i);
    }

    /// <summary>RynthSort's auto-sort, a key like the subsystems'. Click: on/off; right-click: RynthSort's window.</summary>
    private void SortKey(ImDrawListPtr dl, bool on, Vector2 pos, Vector2 size)
    {
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton("##key_sort", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight)
                       && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
        bool right = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(on ? "Auto-sort (RynthSort) on. Click to turn it off.\nRight-click: RynthSort's window."
                                    : "Auto-sort (RynthSort) off. Click to turn it on.\nRight-click: RynthSort's window.");
        KeyFace(dl, RynthIcons.SortAscending, on, hot, pos, size);
        if (clicked) _d.SetAutoSort(!on);
        if (right) _d.ToggleSortWindow();
    }

    /// <summary>A key's face: raised with its LED lit when on, flat and dim when off.</summary>
    private void KeyFace(ImDrawListPtr dl, string icon, bool on, bool hot, Vector2 pos, Vector2 size)
    {
        float r = Px(3);
        if (on)
        {
            dl.AddRectFilled(pos, pos + size, _key, r);
            dl.AddLine(new Vector2(pos.X + r, pos.Y + 1.5f), new Vector2(pos.X + size.X - r, pos.Y + 1.5f), A(White, 0.06f));
            dl.AddRect(pos, pos + size, _line, r);
        }
        else
        {
            dl.AddRect(pos, pos + size, Mix(_bg, _line, 0.55f), r);
        }
        DrawIcon(dl, icon, pos, size, Px(15), on || hot ? _text : _faint);
        // The LED, top right: lit in the accent with a glow, or dark.
        Vector2 led = new(pos.X + size.X - Px(6), pos.Y + Px(6));
        if (on)
        {
            dl.AddCircleFilled(led, Px(4), A(_accent, 0.28f), 12);
            dl.AddCircleFilled(led, Px(2), _accent, 12);
        }
        else
        {
            dl.AddCircleFilled(led, Px(2), _inset, 12);
            dl.AddCircle(led, Px(2), _line, 12, 1f);
        }
    }

    /// <summary>A key's right-click: its settings.</summary>
    private void SubsystemSettings(int i)
    {
        RynthCoreHost host = _d.Host;
        switch (i)
        {
            case 0: host.SetPanelOpen("Monsters", RynthPanelOpen.Toggle); break;
            case 1: _d.Command("/rc settings Buffing"); break;
            case 2: host.SetPanelOpen("Nav", RynthPanelOpen.Toggle); break;
            case 3: host.SetPanelOpen("Loot Editor", RynthPanelOpen.Toggle); break;
            case 4: host.SetPanelOpen("Meta", RynthPanelOpen.Toggle); break;
        }
    }

    // ── Launchers: one tray of window buttons ───────────────────────────

    private static readonly (string Icon, string Label, string Key, string Tip)[] LauncherDefs =
    {
        (RynthIcons.FileCode, "Lua", "Lua", "Lua scripts"),
        (RynthIcons.Skull, "Monsters", "Monsters", "Monsters: weapons, spells and rules per monster (the Damage panel)"),
        (RynthIcons.Gear, "Settings", "Settings", "Settings"),
        (RynthIcons.MapPin, "Map", "Dungeon Map", "Dungeon Map (shows while you're in a dungeon)"),
        (RynthIcons.GlobeHemisphereWest, "World", "World Map", "World Map: Dereth, with your position, the route and places"),
        (RynthIcons.Backpack, "Items", "Items", "Items"),
        (RynthIcons.Footprints, "Patrol", "", "Patrol. Click: start dungeon patrol. Right-click: the Patrol drawer (hazards and routes)."),
        (RynthIcons.Crosshair, "Hunt", "", "Hunt. Click: start or stop hunting outdoors from here: walk from monster to monster round obstacles.\nRight-click: the Outdoor Hunt window (how far it looks for monsters, and how far it may roam)."),
        (RynthIcons.User, "Char", "", "Character. Click: RynthOracle's Character tab. Right-click: the character windows."),
    };

    private const string OracleKey = "RynthOracle/Oracle";

    private float Launchers(ImDrawListPtr dl, DashboardPrefs prefs, float x0, float y, float w)
    {
        RynthCoreHost host = _d.Host;
        float h = Px(TrayH), r = Px(3);
        bool world = host.SetPanelOpen("World Map", RynthPanelOpen.Ask) >= 0;
        int n = world ? LauncherDefs.Length : LauncherDefs.Length - 1;
        float bw = w / n;
        bool labels = bw >= Px(68);
        Vector2 min = new(x0, y), max = new(x0 + w, y + h);
        int slot = 0;
        for (int i = 0; i < LauncherDefs.Length; i++)
        {
            var l = LauncherDefs[i];
            if (!world && l.Key == "World Map") continue;
            float bx = MathF.Round(x0 + slot * bw), ex = MathF.Round(x0 + (slot + 1) * bw);
            Vector2 p = new(bx, y), s = new(ex - bx, h);
            bool lit = l.Label switch
            {
                "Patrol" => prefs.IsOpen(DashboardPrefs.PatrolDrawer),
                "Hunt" => _d.HuntActive,
                "Char" => host.SetPanelOpen(OracleKey, RynthPanelOpen.Ask) == 1 || host.SetPanelOpen("Mini Remote", RynthPanelOpen.Ask) == 1,
                _ => host.SetPanelOpen(l.Key, RynthPanelOpen.Ask) == 1,
            };
            ImGui.SetCursorScreenPos(p);
            bool clicked = ImGui.InvisibleButton(Id("##l", i), s, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight)
                           && ImGui.IsMouseReleased(ImGuiMouseButton.Left);
            bool right = ImGui.IsItemClicked(ImGuiMouseButton.Right);
            bool hot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(l.Tip);
            ImDrawFlags corners = slot == 0 ? ImDrawFlags.RoundCornersLeft : slot == n - 1 ? ImDrawFlags.RoundCornersRight : ImDrawFlags.RoundCornersNone;
            if (hot || lit) dl.AddRectFilled(p, p + s, _key, r, corners);
            if (slot > 0) dl.AddLine(new Vector2(bx + 0.5f, y + 1), new Vector2(bx + 0.5f, y + h - 1), _line);
            uint fg = lit || hot ? _text : _dim;
            if (labels)
            {
                ImFontPtr lf = Font(RynthUiFont.Narrow10);
                float iconBox = Px(16);
                float lw = lf.CalcTextSizeA(lf.FontSize, float.MaxValue, 0f, l.Label).X;
                float cx = MathF.Round(bx + (s.X - (iconBox + Px(4) + lw)) * 0.5f);
                DrawIcon(dl, l.Icon, new Vector2(cx, y), new Vector2(iconBox, h), Px(14), fg);
                dl.AddText(lf, lf.FontSize, new Vector2(cx + iconBox + Px(4), MathF.Round(y + (h - lf.FontSize) * 0.5f)), fg, l.Label);
            }
            else
            {
                DrawIcon(dl, l.Icon, p, s, Px(14), fg);
            }
            if (lit) dl.AddRectFilled(new Vector2(bx + s.X * 0.5f - Px(5), y + h - Px(4)), new Vector2(bx + s.X * 0.5f + Px(5), y + h - Px(2)), _accent, Px(1));

            if (clicked) Launch(l.Label, l.Key);
            if (right) LaunchRight(l.Label, prefs);
            slot++;
        }
        dl.AddRect(min, max, _line, r);
        return y + h;
    }

    private void Launch(string label, string key)
    {
        RynthCoreHost host = _d.Host;
        switch (label)
        {
            case "Patrol": _d.StartPatrol(); break;
            case "Hunt": _d.ToggleHunt(); break;
            case "Char":
                if (host.SetPanelOpen(OracleKey, RynthPanelOpen.Ask) >= 0) _d.Command("/ro character");
                else _d.Command("/rc miniremote section char");
                break;
            default: host.SetPanelOpen(key, RynthPanelOpen.Toggle); break;
        }
    }

    private void LaunchRight(string label, DashboardPrefs prefs)
    {
        if (label == "Patrol")
        {
            prefs.ToggleDrawer(DashboardPrefs.PatrolDrawer);
        }
        else if (label == "Hunt")
        {
            _d.ToggleHuntPanel();
        }
        else if (label == "Char")
        {
            ImGui.OpenPopup(CharId);
            _charAnchor = ImGui.GetMousePos();
        }
    }

    // ── Footer: FPS, version, uptime ────────────────────────────────────

    private int _fpsShown = -1;
    private string _fpsText = "— FPS";
    private long _upShown = -1;
    private string _botUp = "", _cliUp = "";
    private static DateTime _clientStartedUtc = DateTime.MinValue;
    private static bool _clientStartRead;

    private static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    private float Footer(ImDrawListPtr dl, float x0, float y, float w)
    {
        int fps = (int)MathF.Round(ImGui.GetIO().Framerate);
        if (fps != _fpsShown) { _fpsShown = fps; _fpsText = fps > 0 ? fps + " FPS" : "— FPS"; }
        DateTime now = DateTime.UtcNow;
        long secs = (long)(now - _d.LoadedUtc).TotalSeconds;
        if (secs != _upShown)
        {
            _upShown = secs;
            _botUp = Clock(now - _d.LoadedUtc);
            if (!_clientStartRead)
            {
                _clientStartRead = true;
                try
                {
                    using var p = System.Diagnostics.Process.GetCurrentProcess();
                    _clientStartedUtc = p.StartTime.ToUniversalTime();
                }
                catch { _clientStartedUtc = DateTime.MinValue; }
            }
            _cliUp = _clientStartedUtc != DateTime.MinValue ? Clock(now - _clientStartedUtc) : "";
        }

        ImFontPtr f = Font(RynthUiFont.Dash9);
        float ty = MathF.Round(y + (Px(FootH) - f.FontSize) * 0.5f);
        uint fpsCol = fps <= 0 ? _faint : fps >= 45 ? St : fps >= 25 ? Gold : HpText;
        dl.AddText(f, f.FontSize, new Vector2(x0, ty), fpsCol, _fpsText);

        // Uptime, right: BOT 1:42:07 · CLI 3:10:44 (labels faint, the times dim).
        float rx = x0 + w;
        float W(string t) => f.CalcTextSizeA(f.FontSize, float.MaxValue, 0f, t).X;
        float space = W(" ");
        float upW = W("BOT ") + W(_botUp) + (_cliUp.Length > 0 ? W(" · CLI ") + W(_cliUp) : 0);
        float ux = MathF.Round(rx - upW);
        float leftEnd = x0 + W(_fpsText) + Px(8);
        if (ux > leftEnd)
        {
            float cx = ux;
            dl.AddText(f, f.FontSize, new Vector2(cx, ty), _faint, "BOT "); cx += W("BOT ");
            dl.AddText(f, f.FontSize, new Vector2(cx, ty), _dim, _botUp); cx += W(_botUp);
            if (_cliUp.Length > 0)
            {
                dl.AddText(f, f.FontSize, new Vector2(cx, ty), _faint, " · CLI "); cx += W(" · CLI ");
                dl.AddText(f, f.FontSize, new Vector2(cx, ty), _dim, _cliUp);
            }
        }
        else ux = rx;

        // The version between them: centred when there's room, else after FPS.
        string version = _d.Version;
        if (version.Length > 0)
        {
            float left = leftEnd, right = ux - Px(8);
            float vw = W(version);
            if (right - left >= vw)
            {
                float vx = x0 + (w - vw) * 0.5f;
                if (vx < left || vx + vw > right) vx = left;
                dl.AddText(f, f.FontSize, new Vector2(MathF.Round(vx), ty), _faint, version);
            }
        }
        _ = space;
        return y + Px(FootH);
    }

    // ── Bars: the vitals, then the target ───────────────────────────────

    private readonly NumberText _hpText = new(), _stText = new(), _mnText = new(), _tStText = new(), _tMnText = new();
    private string _targetShown = "", _targetCaps = "";

    private float Bars(ImDrawListPtr dl, DashboardPrefs prefs, float originX, float width, float x0, float y, float w)
    {
        y += Px(8);
        // The hairline across the whole panel, inside its border.
        dl.AddLine(new Vector2(originX + 1, MathF.Floor(y) + 0.5f), new Vector2(originX + width - 1, MathF.Floor(y) + 0.5f), _line);
        y += Px(8);
        if (prefs.ShowVitals)
        {
            y = Vital(dl, x0, y, w, "HP", _r.Health, _r.MaxHealth, Hp, _hpText, lowWarn: true) + Px(VitalGap);
            y = Vital(dl, x0, y, w, "ST", _r.Stamina, _r.MaxStamina, St, _stText) + Px(VitalGap);
            y = Vital(dl, x0, y, w, "MN", _r.Mana, _r.MaxMana, Mn, _mnText);
        }
        if (prefs.ShowTarget)
        {
            if (prefs.ShowVitals) y += Px(8);
            y = Target(dl, x0, y, w);
        }
        return y;
    }

    /// <summary>A vital row: its label, a bar with quarter ticks, and "cur/max" in numbers that line up.</summary>
    private float Vital(ImDrawListPtr dl, float x0, float y, float w, string label, uint cur, uint max, uint color,
        NumberText text, bool lowWarn = false)
    {
        float h = Px(VitalH);
        float frac = max == 0 ? 0f : Math.Clamp((float)cur / max, 0f, 1f);
        bool low = lowWarn && max > 0 && frac < 0.25f;
        if (low)
            dl.AddRectFilled(new Vector2(x0 - Px(4), y - Px(1)), new Vector2(x0 + w + Px(4), y + h + Px(1)), A(Hp, 0.16f), Px(2));

        ImFontPtr lf = Font(RynthUiFont.NarrowBold11);
        float lsize = lf.FontSize * 10f / 11f;
        dl.AddText(lf, lsize, new Vector2(x0, MathF.Round(y + (h - lsize) * 0.5f)), low ? HpText : color, label);

        float bx = x0 + Px(VitalLabelW) + Px(VitalColGap);
        float bw = w - Px(VitalLabelW) - Px(VitalNumW) - 2 * Px(VitalColGap);
        float by = MathF.Round(y + (h - Px(BarH)) * 0.5f);
        Vector2 bmin = new(bx, by), bmax = new(bx + bw, by + Px(BarH));
        dl.AddRectFilled(bmin, bmax, _inset, Px(2));
        if (frac > 0) dl.AddRectFilled(bmin, new Vector2(bx + bw * frac, bmax.Y), color, Px(2));
        for (int q = 1; q < 4; q++)
        {
            float tx = MathF.Round(bx + bw * q / 4f) + 0.5f;
            dl.AddLine(new Vector2(tx, bmin.Y), new Vector2(tx, bmax.Y), A(Black, 0.5f));
        }
        dl.AddRect(bmin, bmax, A(Black, 0.4f), Px(2));

        // cur bright, /max faint, right-aligned.
        text.Set(cur, max);
        ImFontPtr nf = Font(RynthUiFont.Dash10);
        float ny = MathF.Round(y + (h - nf.FontSize) * 0.5f);
        float maxW = nf.CalcTextSizeA(nf.FontSize, float.MaxValue, 0f, text.Max).X;
        float curW = nf.CalcTextSizeA(nf.FontSize, float.MaxValue, 0f, text.Cur).X;
        float nx = x0 + w - maxW;
        dl.AddText(nf, nf.FontSize, new Vector2(MathF.Round(nx), ny), _faint, text.Max);
        dl.AddText(nf, nf.FontSize, new Vector2(MathF.Round(nx - curW), ny), low ? HpText : _text, text.Cur);
        return y + h;
    }

    /// <summary>
    /// The target: its name and health, then 15 segments lit in its health's colour. The monster
    /// RynthAi is fighting has a sword (it isn't selected); its health shows once the server sends it.
    /// </summary>
    private float Target(ImDrawListPtr dl, float x0, float y, float w)
    {
        bool none = _r.TargetId == 0;
        bool known = !none && _r.TargetHealth >= 0f;
        string label = none ? "No target" : _r.TargetLabel;
        if (!ReferenceEquals(label, _targetShown)) { _targetShown = label; _targetCaps = label.ToUpperInvariant(); }
        float pct = known ? Math.Clamp(_r.TargetHealth, 0f, 1f) : 0f;
        uint tier = !known ? _faint : pct >= 0.66f ? St : pct >= 0.33f ? Gold : Hp;

        float hh = Px(13);
        if (_r.TargetIsFight)
            DrawIcon(dl, RynthIcons.Sword, new Vector2(x0, y), new Vector2(Px(11), hh), Px(11), HpText);
        else
            DrawIcon(dl, RynthIcons.Crosshair, new Vector2(x0, y), new Vector2(Px(11), hh), Px(11), _dim);
        ImFontPtr nf = Font(RynthUiFont.Narrow10), mono = Font(RynthUiFont.Dash10);
        string pctText = !known ? "—" : PctText((int)MathF.Round(pct * 100f));
        float pw = mono.CalcTextSizeA(mono.FontSize, float.MaxValue, 0f, pctText).X;
        float nameX = x0 + Px(11) + Px(5);
        float nameMax = x0 + w - pw - Px(8) - nameX;
        float ty = MathF.Round(y + (hh - nf.FontSize) * 0.5f);
        dl.PushClipRect(new Vector2(nameX, y - Px(2)), new Vector2(nameX + Math.Max(1, nameMax), y + hh + Px(2)), true);
        Tracked(dl, nf, nf.FontSize, new Vector2(nameX, ty), none ? _faint : _text, _targetCaps, 0.1f * nf.FontSize);
        dl.PopClipRect();
        dl.AddText(mono, mono.FontSize, new Vector2(MathF.Round(x0 + w - pw), MathF.Round(y + (hh - mono.FontSize) * 0.5f)), tier, pctText);
        y += hh + Px(4);

        const int segments = 15;
        float gap = Px(2), sh = Px(6);
        float sw = (w - gap * (segments - 1)) / segments;
        int lit = 0;
        if (known) for (int i = 0; i < segments; i++) if ((float)i / segments <= pct) lit++;
        for (int i = 0; i < segments; i++)
        {
            float sx = MathF.Round(x0 + i * (sw + gap)), ex = MathF.Round(x0 + i * (sw + gap) + sw);
            Vector2 a = new(sx, y), b = new(ex, y + sh);
            if (i < lit) dl.AddRectFilled(a, b, tier, Px(1));
            else
            {
                dl.AddRectFilled(a, b, _inset, Px(1));
                dl.AddRect(a, b, A(White, 0.035f), Px(1));
            }
        }
        y += sh;

        // The target's stamina and mana, held while that option is on (so the box doesn't jump).
        if (_r.ShowTargetStaminaMana)
        {
            y += Px(5);
            bool shown = !none && _r.TargetMaxStamina > 0;
            y = Vital(dl, x0, y, w, "ST", shown ? _r.TargetStamina : 0, shown ? _r.TargetMaxStamina : 0, St, _tStText) + Px(VitalGap);
            y = Vital(dl, x0, y, w, "MN", shown ? _r.TargetMana : 0, shown ? _r.TargetMaxMana : 0, Mn, _tMnText);
        }
        return y;
    }

    private static readonly string[] Pcts = BuildPcts();
    private static string[] BuildPcts()
    {
        var a = new string[101];
        for (int i = 0; i <= 100; i++) a[i] = i + "%";
        return a;
    }
    private static string PctText(int pct) => Pcts[Math.Clamp(pct, 0, 100)];

    /// <summary>A vital's "271" and "/346", rebuilt only when the numbers change.</summary>
    private sealed class NumberText
    {
        private uint _cur = uint.MaxValue, _max = uint.MaxValue;
        public string Cur = "", Max = "";
        public void Set(uint cur, uint max)
        {
            if (cur == _cur && max == _max) return;
            _cur = cur; _max = max;
            Cur = max == 0 && cur == 0 ? "--" : cur.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Max = max == 0 ? "/--" : "/" + max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    // ── Options menu (⋯, or right-click the header) ─────────────────────

    private const string OptionsId = "##dash_options";
    private Vector2 _optionsAnchor;
    private bool _optionsAnchorRight;

    private void OpenOptions(Vector2 anchor, bool anchorRight)
    {
        _optionsAnchor = anchor;
        _optionsAnchorRight = anchorRight;
        ImGui.OpenPopup(OptionsId);
    }

    private void OptionsMenu(DashboardPrefs prefs)
    {
        if (!ImGui.IsPopupOpen(OptionsId)) return;
        ImGui.SetNextWindowPos(_optionsAnchor, ImGuiCond.Appearing, _optionsAnchorRight ? new Vector2(1, 0) : Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(Px(232), 0));
        PushPopupStyle();
        bool open = ImGui.BeginPopup(OptionsId, ImGuiWindowFlags.NoMove);
        PopPopupStyle();
        if (!open) return;
        try
        {
            KeepPopupOnScreen();
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            float w = ImGui.GetContentRegionAvail().X;
            Vector2 p = ImGui.GetCursorScreenPos();

            // OPACITY  [-] 95% [+]
            p = StepRow(dl, p, w, "OPACITY", PctText((int)MathF.Round(_r.Opacity * 100f)),
                "##op_dn", "More transparent", () => _d.AdjustOpacity(-0.05f),
                "##op_up", "More opaque", () => _d.AdjustOpacity(0.05f));
            // SIZE  [-] 100% [+]
            int size = _d.Host.PanelTextSize(Title, 0);
            p = StepRow(dl, p, w, "SIZE", size > 0 ? size + "%" : "—",
                "##sz_dn", "Smaller (Ctrl+wheel on the header)", () => _d.Host.PanelTextSize(Title, -1),
                "##sz_up", "Bigger (Ctrl+wheel on the header)", () => _d.Host.PanelTextSize(Title, 1));
            p.Y += Px(4);
            dl.AddLine(new Vector2(p.X, p.Y + 0.5f), new Vector2(p.X + w, p.Y + 0.5f), _line);
            p.Y += Px(5);

            if (MenuItem(dl, ref p, w, "##m_classic", RynthIcons.Scroll, "Classic look", "/rc dashboard classic",
                    "The retail-style window with tabs (Main, Monsters, Gear, Buffs, Route, Loot, Meta, Tuning)."))
            {
                ImGui.CloseCurrentPopup();
                _d.UseClassicLook();
            }
            if (MenuItem(dl, ref p, w, "##m_pop", _popped ? RynthIcons.ArrowSquareIn : RynthIcons.ArrowSquareOut,
                    _popped ? "Dock" : "Pop out", "",
                    _popped ? "Dock back into the game window" : "Pop out into a floating window"))
            {
                ImGui.CloseCurrentPopup();
                _d.Host.SetPanelPopOut(Title, RynthPanelPopOut.Toggle);
            }
            // When the bot seems stuck: what it is waiting for, and the hourglass reset.
            if (MenuItem(dl, ref p, w, "##m_why", RynthIcons.Info, "Why is it doing that?", "/ra why",
                    "A one-glance diagnosis in chat: why the bot is idle, or what it is attacking and why."))
            {
                ImGui.CloseCurrentPopup();
                _d.Command("/ra why");
            }
            // Fight the monster I select (also Settings > Ranges > Combat Targeting): a toggle, the
            // command's reply says the new state.
            bool fightSelected = _d.FightSelectedTarget;
            if (MenuItem(dl, ref p, w, "##m_fightsel", RynthIcons.Crosshair,
                    "Fight the monster I select", fightSelected ? "ON" : "OFF",
                    "On: in a fight, a monster you select is fought first, until it dies, can't be reached,\n" +
                    "or you select another or deselect (/ra fightselected). Click to turn it " + (fightSelected ? "off." : "on.")))
            {
                ImGui.CloseCurrentPopup();
                _d.Command("/ra fightselected");
            }
            if (MenuItem(dl, ref p, w, "##m_busy", RynthIcons.Hourglass, "Clear busy", "/ra clearbusy",
                    "The game thinks the character is still busy (the hourglass stays, nothing can be used): clear it."))
            {
                ImGui.CloseCurrentPopup();
                _d.Command("/ra clearbusy");
            }
            if (_popped)
            {
                // Popped out the pickers are here as well (an older RynthCore's pop-out lets clicks
                // beside the panel through, so its drawers can't be used).
                p.Y += Px(4);
                dl.AddLine(new Vector2(p.X, p.Y + 0.5f), new Vector2(p.X + w, p.Y + 0.5f), _line);
                p.Y += Px(5);
                ImGui.SetCursorScreenPos(p);
                DashboardFiles files = _d.ReadFiles();
                PickerMenu("Profile", files.Profiles, files.ProfileIdx, 3);
                PickerMenu("Nav", files.Navs, files.NavIdx, 0);
                PickerMenu("Loot", files.Loots, files.LootIdx, 1, files.LootMode);
                PickerMenu("Meta", files.Metas, files.MetaIdx, 2);
                PickerMenu("Buffs", files.Buffs, files.BuffIdx, 4);
                p = ImGui.GetCursorScreenPos();
            }
            ImGui.SetCursorScreenPos(p);
            ImGui.Dummy(new Vector2(w, 0));
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    private void PushPopupStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg, WithAlpha(_bg, 0xFA));
        ImGui.PushStyleColor(ImGuiCol.Border, _line);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Px(4));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(5), Px(5)));
    }

    private static void PopPopupStyle()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(2);
    }

    /// <summary>LABEL  [-] value [+], one row of the options menu. Returns the next row's position.</summary>
    private Vector2 StepRow(ImDrawListPtr dl, Vector2 p, float w, string label, string value,
        string downId, string downTip, Action down, string upId, string upTip, Action up)
    {
        float h = Px(24), step = Px(18), valueW = Px(40);
        ImFontPtr lf = Font(RynthUiFont.Narrow9), vf = Font(RynthUiFont.Dash10);
        Tracked(dl, lf, lf.FontSize, new Vector2(p.X + Px(6), MathF.Round(p.Y + (h - lf.FontSize) * 0.5f)), _faint, label, 0.16f * lf.FontSize);
        float sx = p.X + w - Px(4) - step - valueW - step;
        if (StepButton(dl, downId, new Vector2(sx, p.Y + (h - step) * 0.5f), step, RynthIcons.Minus, downTip)) down();
        float vw = vf.CalcTextSizeA(vf.FontSize, float.MaxValue, 0f, value).X;
        dl.AddText(vf, vf.FontSize, new Vector2(MathF.Round(sx + step + (valueW - vw) * 0.5f), MathF.Round(p.Y + (h - vf.FontSize) * 0.5f)), _text, value);
        if (StepButton(dl, upId, new Vector2(sx + step + valueW, p.Y + (h - step) * 0.5f), step, RynthIcons.Plus, upTip)) up();
        return new Vector2(p.X, p.Y + h);
    }

    private bool StepButton(ImDrawListPtr dl, string id, Vector2 pos, float size, string icon, string tip)
    {
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(tip);
        dl.AddRectFilled(pos, pos + new Vector2(size, size), _key, Px(3));
        dl.AddRect(pos, pos + new Vector2(size, size), _line, Px(3));
        DrawIcon(dl, icon, pos, new Vector2(size, size), Px(10), hot ? _text : _dim);
        return clicked;
    }

    /// <summary>An options menu item: icon, label, a command hint on the right. True when clicked.</summary>
    private bool MenuItem(ImDrawListPtr dl, ref Vector2 p, float w, string id, string icon, string label, string hint, string tip)
    {
        float h = Px(24);
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton(id, new Vector2(w, h));
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(tip);
        if (hot) dl.AddRectFilled(p, p + new Vector2(w, h), _key, Px(3));
        DrawIcon(dl, icon, p + new Vector2(Px(6), 0), new Vector2(Px(13), h), Px(13), _dim);
        ImFontPtr f = Font(RynthUiFont.Narrow11);
        dl.AddText(f, f.FontSize, new Vector2(MathF.Round(p.X + Px(6) + Px(13) + Px(8)), MathF.Round(p.Y + (h - f.FontSize) * 0.5f)), _text, label);
        if (hint.Length > 0)
        {
            ImFontPtr hf = Font(RynthUiFont.Dash9);
            float hw = hf.CalcTextSizeA(hf.FontSize, float.MaxValue, 0f, hint).X;
            dl.AddText(hf, hf.FontSize, new Vector2(MathF.Round(p.X + w - Px(6) - hw), MathF.Round(p.Y + (h - hf.FontSize) * 0.5f)), _faint, hint);
        }
        p.Y += h;
        return clicked;
    }

    /// <summary>A picker as a submenu (popped out, the options menu holds the pickers).</summary>
    private void PickerMenu(string label, string[] items, int selected, int kind, int lootMode = -1)
    {
        string current = selected >= 0 && selected < items.Length ? items[selected] : "None";
        if (Loot.LootModes.IsRares(lootMode)) current = "rares + mana stones";
        if (!ImGui.BeginMenu(label + ": " + SafeImGui.Escape(current) + "##pm" + kind)) return;
        try
        {
            if (lootMode >= 0)
            {
                for (int m = 0; m < LootModeRows.Length; m++)
                    if (ImGui.MenuItem(LootModeRows[m] + "##pmm" + m, "", Loot.LootModes.Clamp(lootMode) == m))
                        _d.SetLootMode(m);
                ImGui.Separator();
            }
            if (items.Length == 0) ImGui.TextDisabled("None");
            for (int i = 0; i < items.Length; i++)
                if (ImGui.MenuItem(SafeImGui.Escape(items[i]) + "##pmi" + kind + "_" + i, "", i == selected))
                    _d.SelectProfile(kind, i);
        }
        finally { ImGui.EndMenu(); }
    }

    // ── Char menu (right-click Char) ────────────────────────────────────

    private const string CharId = "##dash_char";
    private Vector2 _charAnchor;

    private void CharMenu()
    {
        if (!ImGui.IsPopupOpen(CharId)) return;
        ImGui.SetNextWindowPos(_charAnchor, ImGuiCond.Appearing, new Vector2(0, 1));
        PushPopupStyle();
        bool open = ImGui.BeginPopup(CharId);
        PopPopupStyle();
        if (!open) return;
        try
        {
            KeepPopupOnScreen(flipDownTo: _charAnchor.Y);
            RynthCoreHost host = _d.Host;
            bool oracle = host.SetPanelOpen(OracleKey, RynthPanelOpen.Ask) >= 0;
            ImGui.TextDisabled("Character");
            if (ImGui.MenuItem("Character", "/ro character", false, oracle)) _d.Command("/ro character");
            if (ImGui.MenuItem("Mini Remote", "/rc miniremote", host.SetPanelOpen("Mini Remote", RynthPanelOpen.Ask) == 1))
                host.SetPanelOpen("Mini Remote", RynthPanelOpen.Toggle);
            if (ImGui.MenuItem("Spells", "/rc spells")) _d.ToggleSpells();
            if (ImGui.MenuItem("Quests", "/ro", false, oracle)) _d.Command("/ro");
            if (ImGui.MenuItem("Quest Timers", "/ro timers", false, oracle)) _d.Command("/ro timers");
            if (ImGui.MenuItem("Pets", "/rc miniremote tab pets")) _d.Command("/rc miniremote tab pets");
            if (ImGui.MenuItem("Crafting", "/rc miniremote tab crafting")) _d.Command("/rc miniremote tab crafting");
            if (ImGui.MenuItem("Fellowship", "/ro fellowship", false, oracle)) _d.Command("/ro fellowship");
            if (ImGui.MenuItem("Skills", "/rc skills")) _d.Command("/rc skills");
            PanelItem(host, "Inventory", "/rc inventory");
            PanelItem(host, "Item Count", "/rc itemcount");
            bool ginv = host.SetPanelOpen("RynthInventory/Main", RynthPanelOpen.Ask) >= 0;
            if (ImGui.MenuItem("Global Inventory", "/ginv", false, ginv)) _d.Command("/ginv");
            PanelItem(host, "Characters", "/rc characters");
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// Keeps the current popup inside the game window. ImGui only fits a popup to the screen when
    /// it placed the popup itself; one placed with SetNextWindowPos stays where it was put, so the
    /// Char menu (opened upward from the icon) ran off the top with the dashboard in the top left
    /// corner (#rynth-issues "character window not clamped to screen", 2026-10-23).
    /// <paramref name="flipDownTo"/>: for a menu opened upward, the Y to open it downward from
    /// when its top is off screen; then it is pushed back inside the edges. Skipped on the
    /// appearing frame: the popup isn't measured yet, and moving it then would cancel the pivot
    /// ImGui applies once it is.
    /// </summary>
    private static void KeepPopupOnScreen(float flipDownTo = float.NaN)
    {
        if (ImGui.IsWindowAppearing()) return;
        Vector2 pos = ImGui.GetWindowPos(), size = ImGui.GetWindowSize();
        if (size.X <= 0 || size.Y <= 0) return;
        Vector2 display = ImGui.GetIO().DisplaySize;
        Vector2 p = pos;
        if (!float.IsNaN(flipDownTo) && p.Y < 0) p.Y = flipDownTo;
        p.X = Math.Clamp(p.X, 0, Math.Max(0, display.X - size.X));
        p.Y = Math.Clamp(p.Y, 0, Math.Max(0, display.Y - size.Y));
        if (p != pos) ImGui.SetWindowPos(p);
    }

    private static void PanelItem(RynthCoreHost host, string key, string command)
    {
        int state = host.SetPanelOpen(key, RynthPanelOpen.Ask);
        if (state < 0) return;
        if (ImGui.MenuItem(key, command, state == 1)) host.SetPanelOpen(key, RynthPanelOpen.Toggle);
    }

    // ── Drawers: tabs on both edges, one panel open per side ────────────
    //
    // Ranges, Loaded files, Patrol and the Mini Remote start on the left edge, My settings on the
    // right; a tab's right-click moves its drawer to the other edge (DashboardPrefs keeps the sides,
    // starting from the engine dashboard's). Ranges, Patrol and My settings show the Settings
    // panel's rows, which are still RynthCore's: the engine draws those bodies (RenderSettingsDrawer,
    // v90) and this window their frame and title.

    private sealed record DrawerDef(string Key, string Icon, string Title, string? Note, string Tip, string? EngineBody);

    private static readonly DrawerDef[] DrawerDefs =
    {
        new(DashboardPrefs.RangesDrawer, RynthIcons.Ruler, "RANGES", "yards",
            "Ranges: monster range, nav point reach and more, and the selected monster.", "ranges"),
        new(DashboardPrefs.FilesDrawer, RynthIcons.Files, "LOADED FILES", null,
            "Loaded files: the settings profile, nav, loot, meta and buff pickers.", null),
        new(DashboardPrefs.PatrolDrawer, RynthIcons.Footprints, "PATROL & ROUTES", null,
            "Patrol: the Dungeon Patrol settings, this dungeon's hazard cells, recorded hazards and saved routes.", "patrol"),
        new(DashboardPrefs.RemoteDrawer, RynthIcons.SquaresFour, "MINI REMOTE", null,
            "Mini Remote: session rates, attack target, summon, item slots, toggles, bank, rebuff.", null),
        new(DashboardPrefs.PinnedDrawer, RynthIcons.SlidersHorizontal, "MY SETTINGS", null,
            "My settings: the settings you pinned, to change them without opening Settings.\n" +
            "Pin one by right-clicking it in Settings, or with + in the drawer.", "pinned"),
    };

    private readonly float[] _bodyWant = new float[5];   // each drawer's body height last frame (pixels), 0 unknown
    private Vector2 _remoteWant;
    private bool _openPinList;                           // My settings' + was clicked: the engine opens its list
    private string _sideMenuKey = string.Empty;

    private const ImGuiWindowFlags DrawerFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

    /// <summary>The drawer can show here: the Settings-row drawers need the engine's bodies (v90).</summary>
    private bool Available(DrawerDef d) => d.EngineBody == null || _d.Host.HasSettingsDrawers;

    private void Drawers(DashboardPrefs prefs, Vector2 dashPos, Vector2 dashSize)
    {
        DrawSide(prefs, dashPos, dashSize, right: false);
        DrawSide(prefs, dashPos, dashSize, right: true);
    }

    private void DrawSide(DashboardPrefs prefs, Vector2 dashPos, Vector2 dashSize, bool right)
    {
        float tabW = Px(TabW), tabH = Px(TabH), tabGap = Px(TabGap);
        int tabs = 0;
        foreach (DrawerDef d in DrawerDefs) if (Available(d) && prefs.OnRight(d.Key) == right) tabs++;
        if (tabs == 0) return;
        string key = prefs.OpenOn(right);
        int di = Array.FindIndex(DrawerDefs, d => d.Key == key);
        if (di >= 0 && !Available(DrawerDefs[di])) di = -1;

        // The slide: the open drawer's panel eases out of its tab (and back in when it closes, still
        // drawn until it is in); another drawer opening on this side starts from closed.
        int side = right ? 1 : 0;
        if (di >= 0 && _slideKey[side] != key) { _slideKey[side] = key; _slideT[side] = 0f; }
        float step = ImGui.GetIO().DeltaTime / SlideSeconds;
        if (!(step > 0f)) step = 1f;
        // Only toward the target: at the target it stays (stepping back and forth there made an open
        // drawer shake by a few pixels every frame, more at low frame rates).
        float target = di >= 0 ? 1f : 0f;
        if (_slideT[side] < target) _slideT[side] = Math.Min(target, _slideT[side] + step);
        else if (_slideT[side] > target) _slideT[side] = Math.Max(target, _slideT[side] - step);
        if (_slideT[side] <= 0f && di < 0) _slideKey[side] = string.Empty;
        string shownKey = _slideKey[side];
        int si = shownKey.Length > 0 ? Array.FindIndex(DrawerDefs, d => d.Key == shownKey) : -1;
        if (si >= 0 && (!Available(DrawerDefs[si]) || prefs.OnRight(shownKey) != right)) si = -1;
        float u = 1f - _slideT[side], ease = 1f - u * u * u;   // ease-out: quick to start, settling at the end
        float panelW = si >= 0 ? PanelWidth(DrawerDefs[si]) : 0f;
        float shown = si >= 0 ? MathF.Round(panelW * ease) : 0f;

        // Where the panel goes: beside its tabs. With no room there (the dashboard against the screen's
        // edge) it opens on the dashboard's other side when that has room and no drawer of its own out,
        // the tabs staying put; else the tabs and the panel stay on the screen together, over the
        // dashboard, as the engine's drawers did.
        float dashL = dashPos.X, dashR = dashPos.X + dashSize.X;
        float tabX = right ? dashR : dashL - tabW;
        float displayW = ImGui.GetIO().DisplaySize.X;
        bool flip = false;
        float winX;
        if (displayW > 1 && si >= 0 && (right ? dashR + tabW + panelW > displayW : dashL - tabW - panelW < 0))
        {
            float otherTabs = TabsOn(prefs, !right) > 0 ? tabW : 0f;
            bool otherFree = _slideKey[1 - side].Length == 0;
            flip = otherFree && (right ? dashL - otherTabs - panelW >= 0 : dashR + otherTabs + panelW <= displayW);
        }
        if (flip)
        {
            float otherTabs = TabsOn(prefs, !right) > 0 ? tabW : 0f;
            winX = right ? dashL - otherTabs - shown : dashR + otherTabs;
        }
        else
        {
            float groupX = right ? tabX : tabX - shown, groupW = tabW + shown;
            if (groupX < 0) tabX -= groupX;
            else if (displayW > 1 && groupX + groupW > displayW) tabX -= groupX + groupW - displayW;
            winX = right ? tabX + tabW : tabX - shown;
        }
        // The panel's left edge while it slides: its leading edge comes out first.
        bool opensRight = right != flip;
        float panelX = opensRight ? winX + shown - panelW : winX;
        Vector2 tabsPos = new(MathF.Round(tabX), dashPos.Y + Px(TabsTop));
        Vector2 tabsSize = new(tabW, tabs * tabH + (tabs - 1) * tabGap);

        ImGui.SetNextWindowPos(tabsPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(tabsSize, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool tabsOpen = ImGui.Begin(right ? "##rynthai_dash_tabs_r" : "##rynthai_dash_tabs", DrawerFlags);
        ImGui.PopStyleVar(2);
        try
        {
            if (tabsOpen)
            {
                ImDrawListPtr dl = ImGui.GetWindowDrawList();
                ImDrawFlags outer = right ? ImDrawFlags.RoundCornersRight : ImDrawFlags.RoundCornersLeft;
                int slot = 0;
                for (int i = 0; i < DrawerDefs.Length; i++)
                {
                    DrawerDef t = DrawerDefs[i];
                    if (!Available(t) || prefs.OnRight(t.Key) != right) continue;
                    Vector2 p = new(tabsPos.X, tabsPos.Y + slot * (tabH + tabGap)), s = new(tabW, tabH);
                    slot++;
                    bool open = prefs.IsOpen(t.Key);
                    ImGui.SetCursorScreenPos(p);
                    bool clicked = ImGui.InvisibleButton(Id("##tab", i), s);
                    bool hot = ImGui.IsItemHovered();
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        _sideMenuKey = t.Key;
                        ImGui.OpenPopup(SideMenuId);
                    }
                    SafeImGui.SetItemTooltip((open ? "Hide the drawer." : t.Tip + " Click to slide it out.")
                                             + "\nRight-click: move it to the " + (right ? "left" : "right") + " edge.");
                    float r = Px(3);
                    dl.AddRectFilled(p, p + s, A(_bg, Math.Clamp(_r.Opacity, 0.1f, 1f)), r, outer);
                    Vector2 bmin = right ? p - new Vector2(r, 0) : p, bmax = right ? p + s : p + new Vector2(s.X + r, s.Y);
                    dl.AddRect(bmin, bmax, _line, r, outer);
                    if (open)
                    {
                        float ex = right ? p.X : p.X + s.X - Px(2);
                        dl.AddRectFilled(new Vector2(ex, p.Y + Px(3)), new Vector2(ex + Px(2), p.Y + s.Y - Px(3)), _accent);
                    }
                    DrawIcon(dl, t.Icon, p, s, Px(12), open || hot ? _text : _faint);
                    if (clicked) prefs.ToggleDrawer(t.Key);
                }
                SideMenu(prefs, right);
            }
        }
        finally
        {
            ImGui.End();
        }

        if (si >= 0 && shown >= 1f) DrawerPanel(prefs, DrawerDefs[si], si, panelW, shown, winX, panelX, dashPos.Y, dashSize.Y, right);
    }

    private int TabsOn(DashboardPrefs prefs, bool right)
    {
        int n = 0;
        foreach (DrawerDef d in DrawerDefs) if (Available(d) && prefs.OnRight(d.Key) == right) n++;
        return n;
    }

    private const float SlideSeconds = 0.16f;
    private const float PopOutReach = 600f;
    private readonly float[] _slideT = new float[2];                          // left, right: 0 closed .. 1 open
    private readonly string[] _slideKey = { string.Empty, string.Empty };     // the drawer whose panel draws

    private float PanelWidth(DrawerDef def) => def.Key switch
    {
        DashboardPrefs.RemoteDrawer when _remoteWant.X > 0 => _remoteWant.X + 2 * Px(8),
        DashboardPrefs.PatrolDrawer => Px(290),
        DashboardPrefs.RangesDrawer => Px(250),
        DashboardPrefs.PinnedDrawer => Px(300),
        _ => Px(DrawerW),
    };

    private const string SideMenuId = "##drawer_side";

    /// <summary>A tab's right-click: move its drawer to the other edge.</summary>
    private void SideMenu(DashboardPrefs prefs, bool right)
    {
        if (!ImGui.IsPopupOpen(SideMenuId)) return;
        PushPopupStyle();
        bool open = ImGui.BeginPopup(SideMenuId);
        PopPopupStyle();
        if (!open) return;
        try
        {
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            Vector2 p = ImGui.GetCursorScreenPos();
            float w = Px(170);
            if (MenuItem(dl, ref p, w, "##side_move", right ? RynthIcons.ArrowLeft : RynthIcons.ArrowRight,
                    right ? "Move to the left edge" : "Move to the right edge", "", "This drawer's tab goes to the dashboard's other edge."))
            {
                ImGui.CloseCurrentPopup();
                if (_sideMenuKey.Length > 0) prefs.MoveDrawer(_sideMenuKey);
            }
            ImGui.SetCursorScreenPos(p);
            ImGui.Dummy(new Vector2(w, 0));
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    /// <summary>
    /// The open drawer's panel (DrawSide places it): its window is the part slid out (<paramref name="shown"/>
    /// pixels of <paramref name="panelW"/>, from <paramref name="winX"/>), the panel drawn whole at
    /// <paramref name="x"/> and clipped to it.
    /// </summary>
    private void DrawerPanel(DashboardPrefs prefs, DrawerDef def, int di, float panelW, float shown, float winX, float x, float top, float dashH, bool right)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        // The frame around the body: padding, the title row, the gap under it.
        float chrome = Px(7) + Px(20) + Px(5) + Px(8);
        float maxBody = display.Y > 1 ? Math.Max(Px(60), display.Y - top - chrome - Px(8)) : Px(600);
        // Popped out, the picture reaches at most PopOutReach pixels past the panel (RynthCore's
        // ImGuiPopOuts.MaxMargin is 640): a longer drawer scrolls.
        if (_popped) maxBody = Math.Max(Px(60), Math.Min(maxBody, dashH + PopOutReach - chrome));
        float body = def.Key switch
        {
            DashboardPrefs.FilesDrawer => FilesHeight(),
            DashboardPrefs.RemoteDrawer => Math.Max(Px(60), _remoteWant.Y > 0 ? _remoteWant.Y : Px(120)),
            _ => _bodyWant[di] > 0 ? _bodyWant[di] : Px(160),
        };
        body = Math.Min(body, maxBody);
        float panelH = MathF.Ceiling(chrome + body);
        if (display.Y > 1 && top + panelH > display.Y) top = Math.Max(0f, display.Y - panelH);
        Vector2 pos = new(MathF.Round(x), MathF.Round(top));

        ImGui.SetNextWindowPos(new Vector2(MathF.Round(winX), pos.Y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(shown, panelH), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        bool panelOpen = ImGui.Begin(right ? "##rynthai_dash_drawer_r" : "##rynthai_dash_drawer", DrawerFlags);
        ImGui.PopStyleVar(2);
        try
        {
            if (!panelOpen) return;
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            dl.ChannelsSplit(2);
            bool merged = false;
            try
            {
                dl.ChannelsSetCurrent(1);
                float x0 = pos.X + Px(8), w = panelW - 2 * Px(8);
                float y = pos.Y + Px(7);
                y = DrawerHeader(dl, prefs, def, x0, y, w);
                y += Px(5);
                switch (def.Key)
                {
                    case DashboardPrefs.FilesDrawer:
                        FilesBody(dl, x0, y, w);
                        break;
                    case DashboardPrefs.RemoteDrawer:
                        RemoteBody(x0, y, w);
                        break;
                    default:
                    {
                        // The engine's body (the Settings panel's rows), in the room this panel has.
                        ImGui.SetCursorScreenPos(new Vector2(x0, y));
                        bool pins = _openPinList;
                        _openPinList = false;
                        float want = _d.Host.RenderSettingsDrawer(def.EngineBody!, w, body, pins);
                        if (want > 0) _bodyWant[di] = MathF.Ceiling(want);
                        break;
                    }
                }
                dl.ChannelsSetCurrent(0);
                Frame(dl, pos, new Vector2(panelW, panelH), _r.Opacity, shadow: shown >= panelW);
                dl.ChannelsMerge();
                merged = true;
            }
            finally
            {
                if (!merged) dl.ChannelsMerge();
            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private float DrawerHeader(ImDrawListPtr dl, DashboardPrefs prefs, DrawerDef def, float x0, float y, float w)
    {
        float h = Px(20);
        DrawIcon(dl, def.Icon, new Vector2(x0, y), new Vector2(Px(12), h), Px(12), _dim);
        ImFontPtr tf = Font(RynthUiFont.NarrowBold11);
        float size = tf.FontSize * 10f / 11f;
        float tx = Tracked(dl, tf, size, new Vector2(x0 + Px(12) + Px(6), MathF.Round(y + (h - size) * 0.5f)), _text, def.Title, 0.18f * size);
        if (def.Note != null)
        {
            ImFontPtr nf = Font(RynthUiFont.Narrow9);
            dl.AddText(nf, nf.FontSize, new Vector2(MathF.Round(tx + Px(6)), MathF.Round(y + (h - nf.FontSize) * 0.5f + Px(1))), _faint, def.Note);
        }
        float cx = x0 + w - Px(Chip);
        if (ChipButton("##drawer_close", RynthIcons.X, new Vector2(cx, y + (h - Px(Chip)) * 0.5f), _faint, "Close the drawer"))
            prefs.CloseDrawer(def.Key);
        if (def.Key == DashboardPrefs.PinnedDrawer
            && ChipButton("##drawer_pin", RynthIcons.Plus, new Vector2(cx - Px(Chip) - Px(2), y + (h - Px(Chip)) * 0.5f), _accent,
                "Pin or unpin settings: a list of every setting that can go here."))
            _openPinList = true;
        return y + h;
    }

    // The Loaded files drawer: label, picker, and the Loot Editor and Spells buttons.
    private float FilesHeight() => 5 * Px(22) + 4 * Px(4);

    private float FilesBody(ImDrawListPtr dl, float x0, float y, float w)
    {
        DashboardFiles f = _d.ReadFiles();
        y = PickerRow(dl, x0, y, w, "PROFILE", f.ProfileName, f.Profiles, f.ProfileIdx, 3, null, null, null) + Px(4);
        y = PickerRow(dl, x0, y, w, "NAV", f.NavName, f.Navs, f.NavIdx, 0, null, null, null) + Px(4);
        y = PickerRow(dl, x0, y, w, "LOOT", f.LootName, f.Loots, f.LootIdx, 1, RynthIcons.TreasureChest,
            "Loot Editor: the loot profile in use.", () => _d.Host.SetPanelOpen("Loot Editor", RynthPanelOpen.Toggle), f.LootMode) + Px(4);
        y = PickerRow(dl, x0, y, w, "META", f.MetaName, f.Metas, f.MetaIdx, 2, null, null, null) + Px(4);
        y = PickerRow(dl, x0, y, w, "BUFFS", f.BuffName, f.Buffs, f.BuffIdx, 4, RynthIcons.Sparkle,
            "RynthAi Spells: the spell list and buff profiles.", _d.ToggleSpells);
        return y;
    }

    private float PickerRow(ImDrawListPtr dl, float x0, float y, float w, string label, string current, string[] items, int selected,
        int kind, string? buttonIcon, string? buttonTip, Action? button, int lootMode = -1)
    {
        float h = Px(22), labelW = Px(44), r = Px(3);
        ImFontPtr lf = Font(RynthUiFont.Narrow9);
        Tracked(dl, lf, lf.FontSize, new Vector2(x0, MathF.Round(y + (h - lf.FontSize) * 0.5f)), _faint, label, 0.14f * lf.FontSize);
        // The LOOT row also has a one-click Loot Mode key (a star, lit in rares mode) left of its button.
        float fx = x0 + labelW, fw = w - labelW - (button != null ? h + Px(4) : 0) - (lootMode >= 0 ? h + Px(4) : 0);
        Vector2 p = new(fx, y), s = new(fw, h);
        string popup = Id("##pick", kind);
        ImGui.SetCursorScreenPos(p);
        if (ImGui.InvisibleButton(Id("##field", kind), s))
        {
            ImGui.OpenPopup(popup);
            _pickAnchor = new Vector2(fx, y + h + Px(2));
            _pickWidth = fw;
        }
        bool hot = ImGui.IsItemHovered();
        if (lootMode >= 0)
            SafeImGui.SetItemTooltip(Loot.LootModes.IsRares(lootMode)
                ? "Loot Mode: rares and needed mana stones only. The loot profile is not used; nothing else is looted.\nClick: a loot profile, or the loot mode."
                : "Loot Mode: the loot profile.\nClick: a loot profile, or the loot mode.");
        dl.AddRectFilled(p, p + s, _inset, r);
        dl.AddRect(p, p + s, hot ? Mix(_line, _accent, 0.45f) : _line, r);
        ImFontPtr vf = Font(RynthUiFont.Narrow11);
        dl.PushClipRect(p, p + s - new Vector2(Px(16), 0), true);
        dl.AddText(vf, vf.FontSize, new Vector2(MathF.Round(fx + Px(7)), MathF.Round(y + (h - vf.FontSize) * 0.5f)), _text, current);
        dl.PopClipRect();
        DrawIcon(dl, RynthIcons.CaretDown, new Vector2(fx + fw - Px(16), y), new Vector2(Px(10), h), Px(10), _dim);
        float bx = fx + fw + Px(4);
        if (lootMode >= 0)
        {
            bool rares = Loot.LootModes.IsRares(lootMode);
            Vector2 mp = new(bx, y), ms = new(h, h);
            ImGui.SetCursorScreenPos(mp);
            if (ImGui.InvisibleButton(Id("##lootmode", kind), ms))
                _d.SetLootMode(rares ? Loot.LootModes.Profile : Loot.LootModes.Rares);
            bool mhot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(rares
                ? "Loot Mode: rares and needed mana stones only (the loot profile is not used).\nClick: loot by the loot profile again."
                : "Loot Mode: the loot profile.\nClick: loot only rares and needed mana stones (the loot profile is not used).");
            dl.AddRectFilled(mp, mp + ms, rares ? Mix(_bg, _accent, 0.16f) : _key, r);
            dl.AddRect(mp, mp + ms, rares ? _accent : _line, r);
            DrawIcon(dl, RynthIcons.Star, mp, ms, Px(12), rares ? _accent : mhot ? _text : _dim);
            bx += h + Px(4);
        }
        if (button != null && buttonIcon != null)
        {
            Vector2 bp = new(bx, y), bs = new(h, h);
            ImGui.SetCursorScreenPos(bp);
            if (ImGui.InvisibleButton(Id("##fbtn", kind), bs)) button();
            bool bhot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip(buttonTip);
            dl.AddRectFilled(bp, bp + bs, _key, r);
            dl.AddRect(bp, bp + bs, _line, r);
            DrawIcon(dl, buttonIcon, bp, bs, Px(12), bhot ? _text : _dim);
        }
        PickerPopup(popup, items, selected, kind, lootMode);
        return y + h;
    }

    private Vector2 _pickAnchor;
    private float _pickWidth;

    /// <summary>The LOOT picker's Loot Mode rows, above the loot profiles.</summary>
    private static readonly string[] LootModeRows = { "Mode: the loot profile", "Mode: rares + needed mana stones only" };

    private void PickerPopup(string id, string[] items, int selected, int kind, int lootMode = -1)
    {
        if (!ImGui.IsPopupOpen(id)) return;
        ImGui.SetNextWindowPos(_pickAnchor, ImGuiCond.Appearing);
        float itemH = Px(22);
        int extra = lootMode >= 0 ? LootModeRows.Length : 0;
        float maxH = Math.Min((items.Length + extra) * itemH + (extra > 0 ? Px(9) : 0) + Px(10), Math.Max(Px(60), ImGui.GetIO().DisplaySize.Y - _pickAnchor.Y - Px(20)));
        ImGui.SetNextWindowSize(new Vector2(Math.Max(_pickWidth, Px(extra > 0 ? 220 : 160)), Math.Max(maxH, Px(32))));
        PushPopupStyle();
        bool open = ImGui.BeginPopup(id);
        PopPopupStyle();
        if (!open) return;
        try
        {
            ImDrawListPtr dl = ImGui.GetWindowDrawList();
            float w = ImGui.GetContentRegionAvail().X;
            ImFontPtr f = Font(RynthUiFont.Narrow11);

            bool Row(string rowId, string text, bool isSel)
            {
                Vector2 p = ImGui.GetCursorScreenPos();
                bool clicked = ImGui.InvisibleButton(rowId, new Vector2(w, itemH));
                bool hot = ImGui.IsItemHovered();
                if (hot || isSel) dl.AddRectFilled(p, p + new Vector2(w, itemH), isSel ? Mix(_bg, _accent, 0.16f) : _key, Px(3));
                if (isSel) dl.AddRectFilled(new Vector2(p.X + Px(4), p.Y + itemH * 0.5f - Px(2)), new Vector2(p.X + Px(8), p.Y + itemH * 0.5f + Px(2)), _accent, Px(2));
                dl.PushClipRect(p, p + new Vector2(w, itemH), true);
                dl.AddText(f, f.FontSize, new Vector2(MathF.Round(p.X + Px(14)), MathF.Round(p.Y + (itemH - f.FontSize) * 0.5f)), isSel ? _accent : _text, text);
                dl.PopClipRect();
                return clicked;
            }

            // The loot mode first: in rares mode the profile below isn't used.
            if (extra > 0)
            {
                for (int m = 0; m < LootModeRows.Length; m++)
                    if (Row(Id("##plm", m), LootModeRows[m], Loot.LootModes.Clamp(lootMode) == m))
                    {
                        _d.SetLootMode(m);
                        ImGui.CloseCurrentPopup();
                    }
                Vector2 sp = ImGui.GetCursorScreenPos();
                dl.AddLine(new Vector2(sp.X, sp.Y + Px(4) + 0.5f), new Vector2(sp.X + w, sp.Y + Px(4) + 0.5f), _line);
                ImGui.Dummy(new Vector2(w, Px(9)));
            }
            if (items.Length == 0)
            {
                ImGui.TextDisabled("None");
                return;
            }
            for (int i = 0; i < items.Length; i++)
                if (Row(Id("##pi", i), items[i], i == selected))
                {
                    _d.SelectProfile(kind, i);
                    ImGui.CloseCurrentPopup();
                }
        }
        finally
        {
            ImGui.EndPopup();
        }
    }

    // The Mini Remote drawer: RynthAi's remote, drawn into the drawer.
    private float RemoteBody(float x0, float y, float w)
    {
        ImGui.SetCursorScreenPos(new Vector2(x0, y));
        if (!_d.DrawMiniRemote(out Vector2 want))
        {
            ImGui.TextDisabled("The Mini Remote isn't ready yet.");
            return y + Px(20);
        }
        _remoteWant = want;
        return y + Math.Max(want.Y, ImGui.GetCursorScreenPos().Y - y);
    }

    // ── Drawing helpers ─────────────────────────────────────────────────

    // ImGui ids with a number ("##key3"), made once each: no strings built every frame.
    private static readonly System.Collections.Generic.Dictionary<(string, int), string> Ids = new();

    private static string Id(string prefix, int n)
    {
        if (!Ids.TryGetValue((prefix, n), out string? id)) Ids[(prefix, n)] = id = prefix + n;
        return id;
    }

    private static Vector2 R(Vector2 v) => new(MathF.Round(v.X), MathF.Round(v.Y));

    private static readonly string[] Chars = BuildChars();

    private static string[] BuildChars()
    {
        var a = new string[128];
        for (int i = 32; i < 127; i++) a[i] = ((char)i).ToString();
        return a;
    }

    /// <summary>
    /// Text with <paramref name="spacing"/> pixels between its letters (ImGui has no letter-spacing:
    /// one glyph at a time). Returns the x after the last letter.
    /// </summary>
    private static float Tracked(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, uint color, string text, float spacing)
    {
        if (font.NativePtr == null || text.Length == 0) return pos.X;
        float scale = size / font.FontSize;
        float x = pos.X;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            string s = c < 128 && Chars[c] != null ? Chars[c] : c.ToString();
            dl.AddText(font, size, new Vector2(MathF.Round(x), pos.Y), color, s);
            x += font.GetCharAdvance(c) * scale;
            if (i < text.Length - 1) x += spacing;
        }
        return x;
    }

    private static float TrackedWidth(ImFontPtr font, float size, string text, float spacing)
    {
        if (font.NativePtr == null || text.Length == 0) return 0f;
        float scale = size / font.FontSize, w = 0;
        for (int i = 0; i < text.Length; i++) w += font.GetCharAdvance(text[i]) * scale;
        return w + spacing * (text.Length - 1);
    }

    /// <summary>
    /// One icon (a Phosphor glyph in the engine's atlas) centred in the box, <paramref name="px"/> pixels
    /// across its em: drawn from Ui18's bake (icons there are about 16 px at normal size), scaled by the
    /// glyph's advance, which is its em.
    /// </summary>
    private void DrawIcon(ImDrawListPtr dl, string glyph, Vector2 min, Vector2 size, float px, uint color)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        ImFontPtr font = Font(RynthUiFont.Ui18);
        if (font.NativePtr == null) return;
        // ImFontGlyph (1.91): a 4-byte bitfield, AdvanceX, then X0 Y0 X1 Y1 (read by offset).
        byte* g = (byte*)ImGuiNative.ImFont_FindGlyphNoFallback(font.NativePtr, glyph[0]);
        if (g == null) return;
        float advance = *(float*)(g + 4);
        if (advance <= 0) return;
        float* box = (float*)(g + 8);
        float scale = px / advance;
        float drawSize = font.FontSize * scale;
        Vector2 centre = new((box[0] + box[2]) * 0.5f * scale, (box[1] + box[3]) * 0.5f * scale);
        Vector2 at = min + size * 0.5f - centre;
        dl.AddText(font, drawSize, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)), color, glyph);
    }
}

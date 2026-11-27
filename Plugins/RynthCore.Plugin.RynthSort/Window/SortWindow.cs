// ============================================================================
//  RynthSort - Window/SortWindow.cs
//  The RynthSort window drawn by the plugin with ImGui (2026-11-26, the approved
//  web design for #rynth-issues "The bag sort feature is lacking in UX"), in
//  RynthCore's frameless plugin window (plugin API v90: the manifest's
//  "chrome": "none", "scale": "draw"), the way RynthDefective's shortcut bar and
//  RynthAi's dashboard draw theirs.
//
//    header   the RynthSort mark, whose packs; the Auto-sort switch, the status
//             line and its pill; Sort now, Clean, Put back; Find an item;
//             Layouts and Settings; pop-out and close. Drag the header to move
//             the window; Ctrl+wheel over it: bigger or smaller (the engine's
//             PanelScale); the corner resizes it.
//    body     three columns when wide (main pack, side packs, the plan), two
//             (the plan under them), then one. The main pack and each side pack
//             as a slot grid of the items' real pictures; rule chips, "+ Add a
//             rule" (typed name or kind), a fold chevron; Next moves, Left alone, Activity.
//    popups   one at a time over the window: an item's detail, the drop choices
//             (an item dragged onto a pack card), the layouts, the settings;
//             and the rule picker under a pack's "+ Add a rule" (a typed name, or a kind).
//    toast    the last change, with Undo, in a strip at the bottom.
//
//  AC's render thread. It only reads the ScreenModel the plugin tick published
//  and queues every action for the tick (ISortWindowActions); what it keeps of
//  its own is the search text, which popup is open, drag and animation state.
//  Strings it draws come from the model or are made once (when a popup opens);
//  nothing per frame allocates in the steady state.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthSort.Screen;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthSort.Window;

/// <summary>What the window asks of the plugin (called on the render thread: each one only queues for the tick).</summary>
internal interface ISortWindowActions
{
    void SetAuto(bool on);
    void SortNow();
    void Clean();
    void PutBack();
    /// <summary>"All &lt;kind&gt;" to side pack <paramref name="pack"/> (1-based).</summary>
    void SendKind(int pack, int cls);
    /// <summary>"Only &lt;name&gt;": item <paramref name="itemId"/>'s name to side pack <paramref name="pack"/>.</summary>
    void SendName(int pack, uint itemId);
    /// <summary>"Just move it" / "Send to": the item to <paramref name="pack"/> (0 = main pack), then left alone.</summary>
    void MoveOnce(uint itemId, int pack);
    void Hold(uint itemId);
    void Release(uint itemId);
    /// <summary>No longer left alone, without a toast: an item dropped on a pack with "All <kind>" or "Only <name>" (dragging it held it).</summary>
    void Unhold(uint itemId);
    void RemoveChip(int ruleUid, bool isName, int cls, string name);
    void TurnOn(int ruleUid);
    /// <summary>The item selected in the game into side pack <paramref name="pack"/>'s rules (by name).</summary>
    void AddSelected(int pack);
    void UseLayout(string key);
    void Undo();
    void DismissToast();
    void SetSettle(int seconds);
    void SetHold(int minutes);
    void SetGap(int ms);
    void SetCombat(bool on);
    void SetOrder(bool on);
    void SetKindFirst(bool on);
    /// <summary>"Keep handy items at the front of the main pack".</summary>
    void SetHandy(bool on);
    /// <summary>"Overflow": side pack <paramref name="pack"/> takes class <paramref name="cls"/> when the pack that has it is full.</summary>
    void AddOverflow(int pack, int cls);
    /// <summary>Side pack <paramref name="pack"/>'s job (Sorting.JobKind as a number; 0 = none) and, for a mule, its name.</summary>
    void SetJob(int pack, int kind, string mule);
    /// <summary>A kind on or off the handy list.</summary>
    void HandyKind(int cls, bool add);
    /// <summary>A part of a name on or off the handy list.</summary>
    void HandyName(string name, bool add);
    void EmptySidePacks();
    void SelectInGame(uint objectId);
    /// <summary>The layouts popup opened or closed (the model carries the layouts only while it is open).</summary>
    void WantLayouts(bool open);
    /// <summary>"Name has...": items whose name has <paramref name="text"/> in it go to side pack <paramref name="pack"/>.</summary>
    void SendNameText(int pack, string text);
    /// <summary>A side pack's card folded down to its header and rules, or opened (by the pack's object id).</summary>
    void SetCollapsed(uint packId, bool collapsed);
    /// <summary>The main pack's card folded or opened.</summary>
    void SetMainCollapsed(bool collapsed);
    /// <summary>Every side pack carried folded or opened.</summary>
    void SetAllCollapsed(bool collapsed);
}

internal sealed unsafe partial class SortWindow
{
    public const string Title = "RynthSort";
    private const string PayloadType = "RSORT_ITEM";

    private readonly RynthCoreHost _host;
    private readonly ISortWindowActions _act;
    private ScreenModel _m = ScreenModel.Empty;
    private float _k = 1f, _r = 1f;
    private double _now;
    private bool _popped;

    public SortWindow(RynthCoreHost host, ISortWindowActions actions)
    {
        _host = host;
        _act = actions;
    }

    // ── Look: the design's palette (rynthsort-packs.html :root), ABGR ────

    private static readonly uint Ink = Argb(0xFFE9EEF2), Ink2 = Argb(0xFFB4C1CB), Ink3 = Argb(0xFF7F909D),
        AppTop = Argb(0xFF0D141B), AppBottom = Argb(0xFF0A1015), Card = Argb(0xFF111921), Card2 = Argb(0xFF16212B),
        CardHot = Argb(0xFF1C2A36), Well = Argb(0xFF0A1016), Line = Argb(0xFF1F2E3B), Line2 = Argb(0xFF2B4053),
        Teal = Argb(0xFF35D0B4), TealHot = Argb(0xFF4FE0C6), TealInk = Argb(0xFF062B25), Brass = Argb(0xFFD2A85A),
        Sky = Argb(0xFF78B8F0), Amber = Argb(0xFFF0BD5C), Mark = Argb(0xFFF4E6C8), DropBg = Argb(0xFF0F1F22),
        DoneInk = Argb(0xFF9FE9D9), HeldInk = Argb(0xFFE9CF96), ToastBg = Argb(0xFF10212A), ToastLine = Argb(0xFF1F4A48),
        ToastInk = Argb(0xFFC5F1E7), ToastBtn = Argb(0xFF2A5E5A), SheetBg = Argb(0xFF0F171F), BagTop = Argb(0xFF7A5530),
        BagBottom = Argb(0xFF4A321B), BagInk = Argb(0xFFF6E3C2), SwitchOff = Argb(0xFF1A2632), KnobOff = Argb(0xFF9FB0BD),
        PillOff = Argb(0xFF18222C), FeedDot = Argb(0xFF1F3A46), White = Argb(0xFFFFFFFF), Black = Argb(0xFF000000),
        Red = Argb(0xFFE5675B), NoFitInk = Argb(0xFFF4C2BC);

    private static uint A(uint c, float alpha) => WithAlpha(c, (uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));
    private static uint Soft(uint c, float alpha) => A(c, alpha);

    // ── Scale (the engine's PanelScale, Ctrl+wheel: 30-250 %, as the shortcut bar) ──

    /// <summary>The layout's scale and the text's against the baked font: the window's own size or, unset, the text size the engine baked.</summary>
    private void Scale()
    {
        float fontK = Math.Max(0.5f, Font(RynthUiFont.Ui10).FontSize / 10f);
        int pct = _host.PanelScale(Title);
        _k = pct > 0 ? pct / 100f : fontK;
        _r = _k / fontK;
    }

    /// <summary>A length in the design's pixels, in the window's (the design at 85 %: a 40 px tile is 34, a 32 px item picture and its frame).</summary>
    private float D(float v) => v * 0.85f * _k;

    /// <summary>A font's size at this scale.</summary>
    private float Fs(ImFontPtr f) => f.FontSize * _r;

    private static Vector2 Round(Vector2 p) => new(MathF.Round(p.X), MathF.Round(p.Y));

    // ── The frame ────────────────────────────────────────────────────────

    /// <summary>Draws the window into the host's frameless window; returns the height it drew. Render thread.</summary>
    public float Draw(ScreenModel model, bool popped)
    {
        _m = model;
        _popped = popped;
        _now = ImGui.GetTime();
        Scale();
        NewModel();

        Vector2 origin = Round(ImGui.GetCursorScreenPos());
        Vector2 size = ImGui.GetContentRegionAvail();
        size = new Vector2(MathF.Floor(size.X), MathF.Floor(size.Y));
        if (size.X < 60 || size.Y < 40) return size.Y;
        _winMin = origin;
        _winSize = size;

        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float round = D(14);
        Background(dl, origin, size, popped ? 0f : round);

        ImGui.PushID("rsort");
        int colors = PushColors();
        int vars = PushVars();
        ImGui.PushFont(Font(RynthUiFont.Ui11));
        // A popup closed this frame by a click outside it: that click goes through to what is under it
        // in ImGui, so for this one frame the window's own controls take no clicks.
        bool popupNow = ImGui.IsPopupOpen(SheetId) || ImGui.IsPopupOpen(KindsId);
        bool swallow = _popupLastFrame && !popupNow && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        _popupLastFrame = popupNow;
        if (swallow) ImGui.BeginDisabled();
        try
        {
            float y = Header(dl, origin, size.X);
            y = WarningsStrip(dl, origin.X, y, size.X);
            float toastH = ToastHeight();
            Vector2 bodyMin = new(origin.X, y);
            Vector2 bodySize = new(size.X, Math.Max(D(40), origin.Y + size.Y - y - toastH));
            Body(bodyMin, bodySize);
            if (toastH > 0) Toast(dl, new Vector2(origin.X, origin.Y + size.Y - toastH), size.X, toastH);
            if (swallow) { ImGui.EndDisabled(); swallow = false; }
            Sheets();
            if (SheetShowing) dl.AddRectFilled(origin, origin + size, A(Argb(0xFF020508), 0.62f), round);
        }
        finally
        {
            if (swallow) ImGui.EndDisabled();
            ImGui.PopFont();
            ImGui.PopStyleVar(vars);
            ImGui.PopStyleColor(colors);
            ImGui.PopID();
        }
        dl.AddRect(origin, origin + size, Line, round);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
        return size.Y;
    }

    private Vector2 _winMin, _winSize;
    private bool _popupLastFrame;

    /// <summary>
    /// The window's background (.app): a dark gradient, top to bottom, in a rounded frame (docked, the
    /// game shows at the corners; popped out, <paramref name="r"/> is 0 and it fills its own window).
    /// </summary>
    private static void Background(ImDrawListPtr dl, Vector2 min, Vector2 size, float r)
    {
        Vector2 max = min + size;
        dl.AddRectFilled(min, max, AppBottom, r);
        dl.AddRectFilled(min, new Vector2(max.X, min.Y + r + 1), AppTop, r, ImDrawFlags.RoundCornersTop);
        dl.AddRectFilledMultiColor(new Vector2(min.X, min.Y + r), new Vector2(max.X, min.Y + Math.Max(r + 1, size.Y * 0.5f)),
            AppTop, AppTop, AppBottom, AppBottom);
    }

    private int PushColors()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Ink);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Ink3);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Well);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Card2);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Card2);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Teal);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, TealHot);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Teal);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, SheetBg);
        ImGui.PushStyleColor(ImGuiCol.Border, Line2);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Line2);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Argb(0xFF3C5970));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Argb(0xFF3C5970));
        ImGui.PushStyleColor(ImGuiCol.Header, A(Teal, 0.14f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Card2);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, A(Teal, 0.22f));
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, A(Teal, 0.35f));
        return 18;
    }

    private int PushVars()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, D(8));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, D(8));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, D(14));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(D(8), D(5)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(D(8), D(6)));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, D(10));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, D(6));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, D(14));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        return 9;
    }

    // ── Header ───────────────────────────────────────────────────────────

    private string _query = string.Empty;
    private string _queryShown = string.Empty;
    private int _queryVersion = -1;
    private readonly HashSet<uint> _hits = new();
    private float _knob;

    /// <summary>The header row(s): mark, status, actions, search, Layouts, Settings, and the window's own buttons. Returns the y under it.</summary>
    private float Header(ImDrawListPtr dl, Vector2 o, float w)
    {
        float pad = D(16), gap = D(14), rowH = D(36);
        ImFontPtr markF = Font(RynthUiFont.NarrowBold13), whoF = Font(RynthUiFont.Ui10);
        float markW = TrackedWidth(markF, Fs(markF) * 1.25f, "RYNTHSORT", 0.12f * Fs(markF)) + D(10) + Measure(whoF, _m.Who).X;
        float winBtns = 2 * D(28) + D(4);
        bool narrow = w < D(740);

        // What goes where: the first row has the mark and the window's buttons, then each block in turn.
        float statusMin = narrow ? w - 2 * pad : D(320);
        float actionsW = ActionsWidth();
        float rightW = LayoutsWidth() + D(8) + D(36);
        float searchW = narrow ? Math.Max(D(120), w - 2 * pad - rightW - gap) : D(220);
        float x0 = o.X + pad, xMax = o.X + w - pad;

        // Rows: [mark][status][actions][search][layouts+settings] flowing; status grows to fill its row.
        Span<float> widths = stackalloc float[5] { markW, statusMin, actionsW, _m.Ready ? searchW : 0, _m.Ready ? rightW : 0 };
        Span<int> rowOf = stackalloc int[5];
        int row = 0;
        float x = x0, firstRowRight = xMax - winBtns - D(8);
        for (int i = 0; i < 5; i++)
        {
            if (widths[i] <= 0) { rowOf[i] = -1; continue; }
            float limit = row == 0 ? firstRowRight : xMax;
            if (x > x0 && x + widths[i] > limit) { row++; x = x0; }
            rowOf[i] = row;
            x += widths[i] + gap;
        }
        int rows = row + 1;
        float height = pad + rows * rowH + (rows - 1) * D(12) + pad - D(2);
        // The brass glow along the top (rounded with the window's corners), and the line under the header.
        float corner = _popped ? 0f : D(14);
        dl.AddRectFilled(o, o + new Vector2(w, corner + 1), A(Brass, 0.06f), corner, ImDrawFlags.RoundCornersTop);
        dl.AddRectFilledMultiColor(new Vector2(o.X, o.Y + corner + 1), o + new Vector2(w, height), A(Brass, 0.06f), A(Brass, 0.06f), 0u, 0u);
        dl.AddLine(new Vector2(o.X, o.Y + height), new Vector2(o.X + w, o.Y + height), Line);

        // The drag handle under everything (later items take the mouse where they are).
        ImGui.SetCursorScreenPos(o);
        ImGui.SetNextItemAllowOverlap();
        ImGui.InvisibleButton("##rs_drag", new Vector2(w, height));
        bool dragHot = ImGui.IsItemHovered();
        _host.PanelDragWithLastItem();

        // Status grows into its row's free room.
        float statusW = statusMin;
        if (rowOf[1] >= 0)
        {
            float used = 0;
            int n = 0;
            for (int i = 0; i < 5; i++) if (rowOf[i] == rowOf[1]) { used += widths[i]; n++; }
            float limit = rowOf[1] == 0 ? firstRowRight : xMax;
            statusW = Math.Max(statusMin, limit - x0 - used - (n - 1) * gap + statusMin);
        }

        Span<float> rowX = stackalloc float[8];
        for (int i = 0; i < rowX.Length; i++) rowX[i] = x0;
        float RowY(int r) => o.Y + pad + r * (rowH + D(12));
        for (int i = 0; i < 5; i++)
        {
            int r = rowOf[i];
            if (r < 0) continue;
            float bx = rowX[r], by = RowY(r);
            float bw = i == 1 ? statusW : widths[i];
            switch (i)
            {
                case 0: MarkBlock(dl, new Vector2(bx, by), rowH, markF, whoF, dragHot); break;
                case 1: StatusBlock(dl, new Vector2(bx, by), new Vector2(bw, rowH)); break;
                case 2: ActionsBlock(new Vector2(bx, by), rowH); break;
                case 3: SearchBlock(dl, new Vector2(bx, by), new Vector2(bw, rowH)); break;
                case 4: RightBlock(new Vector2(bx, by), rowH); break;
            }
            rowX[r] = bx + bw + gap;
        }
        WindowButtons(new Vector2(xMax - winBtns, RowY(0) + (rowH - D(28)) * 0.5f));
        return o.Y + height + 1;
    }

    private void MarkBlock(ImDrawListPtr dl, Vector2 p, float h, ImFontPtr markF, ImFontPtr whoF, bool dragHot)
    {
        float size = Fs(markF) * 1.25f;
        float x = Tracked(dl, markF, size, new Vector2(p.X, MathF.Round(p.Y + (h - size) * 0.5f)), Mark, "RYNTHSORT", 0.12f * Fs(markF));
        if (_m.Who.Length > 0)
            Text(dl, whoF, new Vector2(x + D(10), p.Y + (h - Fs(whoF)) * 0.5f + D(1)), Ink3, _m.Who);
        if (dragHot && ImGui.IsMouseHoveringRect(p, p + new Vector2(x - p.X, h)))
            ImGui.SetTooltip("Drag the header to move the window. Ctrl+wheel over it: bigger or smaller. The corner resizes it.");
    }

    private void StatusBlock(ImDrawListPtr dl, Vector2 p, Vector2 s)
    {
        dl.AddRectFilled(p, p + s, Card, D(12));
        dl.AddRect(p, p + s, Line, D(12));
        if (!_m.Ready)
        {
            Text(dl, Font(RynthUiFont.Ui10), new Vector2(p.X + D(12), p.Y + (s.Y - Fs(Font(RynthUiFont.Ui10))) * 0.5f), Ink2, _m.NotReady);
            return;
        }
        Vector2 sw = new(p.X + D(6), p.Y + (s.Y - D(26)) * 0.5f);
        if (Switch("##rs_auto", sw, _m.Auto)) _act.SetAuto(!_m.Auto);
        SafeImGui.SetItemTooltip(_m.Auto ? "Auto-sort is on: click to turn it off (/rsort off)." : "Auto-sort is off: click to turn it on (/rsort on).");

        // The pill on the right, then the two lines between.
        ImFontPtr pf = Font(RynthUiFont.NarrowBold11);
        float pSize = Fs(pf) * 0.92f, track = 0.1f * pSize;
        string pill = PillText(_m.Status);
        float pw = TrackedWidth(pf, pSize, pill, track) + D(18), ph = pSize + D(11);
        Vector2 pp = new(p.X + s.X - D(8) - pw, p.Y + (s.Y - ph) * 0.5f);
        (uint fg, uint bg) = _m.Status.Tone switch
        {
            PillTone.Tidy => (Teal, A(Teal, 0.14f)),
            PillTone.Moving => (Sky, A(Sky, 0.14f)),
            PillTone.Waiting => (Amber, A(Amber, 0.12f)),
            _ => (Ink3, PillOff),
        };
        dl.AddRectFilled(pp, pp + new Vector2(pw, ph), bg, ph * 0.5f);
        Tracked(dl, pf, pSize, new Vector2(pp.X + D(9), MathF.Round(pp.Y + (ph - pSize) * 0.5f)), fg, pill, track);

        float tx = sw.X + D(46) + D(12), tw = Math.Max(10, pp.X - D(8) - tx);
        ImFontPtr bold = Font(RynthUiFont.UiBold11), small = Font(RynthUiFont.Ui10);
        float lines = Fs(bold) + D(2) + Fs(small);
        float ty = p.Y + (s.Y - lines) * 0.5f;
        Text(dl, bold, new Vector2(tx, ty), Ink, "Auto-sort");
        TextFit(dl, small, new Vector2(tx, ty + Fs(bold) + D(2)), Ink2, _m.Status.Line, tw);
        if (ImGui.IsMouseHoveringRect(new Vector2(tx, p.Y), new Vector2(tx + tw, p.Y + s.Y)) && ImGui.IsWindowHovered())
            SafeImGui.SetTooltip(_m.Status.Tip);
    }

    // The pill's words in capitals, made once per word.
    private readonly Dictionary<string, string> _caps = new();

    private string PillText(StatusModel s)
    {
        if (!_caps.TryGetValue(s.Pill, out string? up)) _caps[s.Pill] = up = s.Pill.ToUpperInvariant();
        return up;
    }

    private float ActionsWidth()
    {
        if (!_m.Ready) return 0;
        float w = ButtonWidth("Sort now", true) + D(8) + ButtonWidth("Clean", false);
        if (ShowPutBack) w += D(8) + ButtonWidth("Put back", true);
        return w;
    }

    private bool ShowPutBack => _m.Ready && _m.PutBack > 0 && !_m.Busy;

    private void ActionsBlock(Vector2 p, float h)
    {
        float bh = D(34), y = p.Y + (h - bh) * 0.5f, x = p.X;
        if (Button("##rs_sortnow", "Sort now", RynthIcons.SortAscending, new Vector2(x, y), bh, Look.Primary, !_m.Busy,
                "Sort every pack once, now: items go to their packs, stacks are joined and packs put in order. "
                + "It doesn't wait for new items to settle; items you used lately are still left alone. Also: /rsort now", out float w))
            _act.SortNow();
        x += w + D(8);
        if (Button("##rs_clean", "Clean", null, new Vector2(x, y), bh, Look.Normal, !_m.Busy,
                "Sort now, and side-pack items no rule wants go back to the main pack. Also: /rsort clean", out w))
            _act.Clean();
        x += w + D(8);
        if (ShowPutBack && Button("##rs_back", "Put back", RynthIcons.ArrowCounterClockwise, new Vector2(x, y), bh, Look.Quiet, true, _m.PutBackTip, out _))
            _act.PutBack();
    }

    private void SearchBlock(ImDrawListPtr dl, Vector2 p, Vector2 s)
    {
        dl.AddRectFilled(p, p + s, Well, D(10));
        dl.AddRect(p, p + s, ImGui.IsMouseHoveringRect(p, p + s) ? Argb(0xFF3C5970) : Line2, D(10));
        DrawIcon(dl, RynthIcons.MagnifyingGlass, new Vector2(p.X + D(8), p.Y), new Vector2(D(16), s.Y), D(15), Ink3);
        float fh = ImGui.GetFontSize();
        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0u);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, 0u);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, 0u);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(D(2), Math.Max(0, (s.Y - fh) * 0.5f - 1)));
        ImGui.SetCursorScreenPos(new Vector2(p.X + D(28), p.Y));
        ImGui.SetNextItemWidth(Math.Max(20, s.X - D(28) - D(6)));
        ImGui.InputTextWithHint("##rs_q", "Find an item", ref _query, 64);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);
        SafeImGui.SetItemTooltip("Type part of an item's name: matching items light up, the rest dim.");
    }

    private float LayoutsWidth() => ButtonWidth("Layouts", true);

    private void RightBlock(Vector2 p, float h)
    {
        float bh = D(34), y = p.Y + (h - bh) * 0.5f;
        if (Button("##rs_layouts", "Layouts", RynthIcons.SquaresFour, new Vector2(p.X, y), bh, Look.Normal, true,
                "Starter layouts: a kind of item for each pack, fitted to the packs you carry. You see what would move first.", out float w))
            OpenLayouts(null);
        if (IconButton("##rs_settings", RynthIcons.SlidersHorizontal, new Vector2(p.X + w + D(8), y), new Vector2(D(36), bh), "Settings"))
            OpenSheet(Sheet.Settings);
    }

    private void WindowButtons(Vector2 p)
    {
        Vector2 s = new(D(28), D(28));
        if (IconButton("##rs_pop", _popped ? RynthIcons.ArrowSquareIn : RynthIcons.ArrowSquareOut, p, s,
                _popped ? "Dock it back into the game window" : "Pop out into its own window", quiet: true))
            _host.SetPanelPopOut(Title, RynthPanelPopOut.Toggle);
        if (IconButton("##rs_close", RynthIcons.X, p + new Vector2(D(32), 0), s, "Close (/rsort, or the Sort button on the Inventory, opens it again)", quiet: true))
            _host.SetPanelOpen(Title, RynthPanelOpen.Close);
    }

    /// <summary>Amber lines under the header (wrapped); returns the y under them.</summary>
    private float WarningsStrip(ImDrawListPtr dl, float x, float y, float w)
    {
        if (!_m.Ready) return y;
        y = Strip(dl, x, y, w, _m.NoFit, Red, NoFitInk, RynthIcons.Warning);
        return Strip(dl, x, y, w, _m.Warnings, Amber, HeldInk, RynthIcons.Warning);
    }

    /// <summary>A strip of wrapped lines under the header in <paramref name="tone"/> (red: what didn't fit; amber: warnings). Returns the y under it.</summary>
    private float Strip(ImDrawListPtr dl, float x, float y, float w, string[] lines, uint tone, uint ink, string icon)
    {
        if (lines.Length == 0) return y;
        ImFontPtr f = Font(RynthUiFont.Ui10);
        float pad = D(16), wrap = w - 2 * pad - D(24);
        float h = D(8);
        foreach (string line in lines) h += f.CalcTextSizeA(Fs(f), float.MaxValue, wrap, line).Y + D(4);
        h += D(4);
        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w, y + h), A(tone, 0.10f));
        dl.AddLine(new Vector2(x, y + h), new Vector2(x + w, y + h), A(tone, 0.30f));
        float cy = y + D(8);
        foreach (string line in lines)
        {
            DrawIcon(dl, icon, new Vector2(x + pad, cy), new Vector2(D(16), Fs(f)), D(13), tone);
            dl.AddText(f, Fs(f), Round(new Vector2(x + pad + D(22), cy)), ink, line, wrap);
            cy += f.CalcTextSizeA(Fs(f), float.MaxValue, wrap, line).Y + D(4);
        }
        return y + h + 1;
    }

    // ── Toast ────────────────────────────────────────────────────────────

    private int _toastGone = -1;

    private bool ToastShowing => _m.Toast is ToastModel t && t.Seq != _toastGone && Environment.TickCount64 < t.UntilMs;

    private float ToastHeight() => ToastShowing ? D(52) : 0f;

    private void Toast(ImDrawListPtr dl, Vector2 p, float w, float h)
    {
        ToastModel t = _m.Toast!;
        ImFontPtr f = Font(RynthUiFont.Ui11), bf = Font(RynthUiFont.UiBold11);
        float undoW = t.CanUndo ? Measure(bf, "Undo").X + D(20) : 0;
        float closeW = D(22);
        float maxText = w - D(32) - undoW - closeW - D(40);
        float textW = Math.Min(Measure(f, t.Text).X, maxText);
        float bw = textW + D(28) + (undoW > 0 ? undoW + D(12) : 0) + closeW + D(6);
        float bh = h - D(12);
        Vector2 bp = Round(new Vector2(p.X + (w - bw) * 0.5f, p.Y + D(4)));
        dl.AddRectFilled(bp, bp + new Vector2(bw, bh), ToastBg, D(12));
        dl.AddRect(bp, bp + new Vector2(bw, bh), ToastLine, D(12));
        TextFit(dl, f, new Vector2(bp.X + D(14), bp.Y + (bh - Fs(f)) * 0.5f), ToastInk, t.Text, textW + 1);
        float x = bp.X + D(14) + textW + D(12);
        if (t.CanUndo)
        {
            Vector2 up = new(x, bp.Y + (bh - D(26)) * 0.5f), us = new(undoW, D(26));
            ImGui.SetCursorScreenPos(up);
            bool clicked = ImGui.InvisibleButton("##rs_undo", us);
            bool hot = ImGui.IsItemHovered();
            SafeImGui.SetItemTooltip("Put the rules back as they were before this change. Nothing moves.");
            dl.AddRectFilled(up, up + us, hot ? A(ToastBtn, 0.6f) : 0u, D(8));
            dl.AddRect(up, up + us, ToastBtn, D(8));
            Text(dl, bf, new Vector2(up.X + (us.X - Measure(bf, "Undo").X) * 0.5f, up.Y + (us.Y - Fs(bf)) * 0.5f), ToastInk, "Undo");
            if (clicked) { _act.Undo(); _toastGone = t.Seq; }
            x += undoW + D(6);
        }
        if (IconButton("##rs_toastx", RynthIcons.X, new Vector2(x, bp.Y + (bh - closeW) * 0.5f), new Vector2(closeW, closeW), "Hide this", quiet: true))
        {
            _toastGone = t.Seq;
            _act.DismissToast();
        }
    }

    // ── A new model: search hits, arrivals, caches ───────────────────────

    private readonly Arrivals _arrivals = new();
    private int _modelVersion = -1;
    private string _modelWho = string.Empty;

    private void NewModel()
    {
        bool changed = _m.Version != _modelVersion;
        if (changed)
        {
            _modelVersion = _m.Version;
            if (_m.Who != _modelWho)
            {
                // Another character (or logged out): its search, popups and animations go.
                _modelWho = _m.Who;
                _query = _queryShown = string.Empty;
                _sheet = Sheet.None;
                _pendingSheet = Sheet.None;
                _arrivals.Clear();
                _layoutsOffered = false;
            }
            _arrivals.Update(_m, _now);
            if (_fit.Count > 1024) _fit.Clear();
        }
        if (changed || !ReferenceEquals(_query, _queryShown) || _queryVersion != _m.Version)
        {
            _queryShown = _query;
            _queryVersion = _m.Version;
            _hits.Clear();
            string q = _query.Trim();
            if (q.Length > 0)
                foreach (KeyValuePair<uint, TileModel> kv in _m.TilesById)
                    if (ScreenText.Matches(kv.Value, q)) _hits.Add(kv.Key);
            _searching = q.Length > 0;
        }
        // A new character with no rules: the layouts, once, with Everyday picked (the design's first run).
        if (_m.Ready && _m.NoRules && _m.Packs.Length > 0 && !_layoutsOffered && _sheet == Sheet.None)
        {
            _layoutsOffered = true;
            OpenLayouts("everyday");
        }
    }

    private bool _searching;
    private bool _layoutsOffered;

    // ── Widgets ──────────────────────────────────────────────────────────

    private enum Look { Normal, Primary, Quiet }

    private float ButtonWidth(string label, bool icon)
    {
        ImFontPtr f = Font(RynthUiFont.UiBold11);
        return MathF.Ceiling(Measure(f, label).X + (icon ? D(16) + D(7) : 0) + 2 * D(13));
    }

    /// <summary>The design's button (.btn, .btn.primary, .btn.quiet) at <paramref name="pos"/>; true when clicked.</summary>
    private bool Button(string id, string label, string? icon, Vector2 pos, float h, Look look, bool enabled, string? tip, out float width)
    {
        ImFontPtr f = Font(RynthUiFont.UiBold11);
        float w = ButtonWidth(label, icon != null);
        width = w;
        Vector2 size = new(w, h);
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, size) && enabled;
        bool hot = enabled && ImGui.IsItemHovered();
        bool held = enabled && ImGui.IsItemActive();
        if (tip != null) SafeImGui.SetItemTooltip(tip);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        uint fill, border, ink;
        switch (look)
        {
            case Look.Primary:
                fill = hot ? TealHot : Teal;
                border = Teal;
                ink = TealInk;
                break;
            case Look.Quiet:
                fill = hot ? CardHot : 0u;
                border = Line2;
                ink = Ink;
                break;
            default:
                fill = hot ? CardHot : Card2;
                border = Line2;
                ink = Ink;
                break;
        }
        if (held) fill = Darken(fill == 0 ? CardHot : fill);
        float alpha = enabled ? 1f : 0.45f;
        if (fill != 0) dl.AddRectFilled(pos, pos + size, A(fill, alpha * ((fill >> 24) / 255f)), D(10));
        dl.AddRect(pos, pos + size, A(border, alpha), D(10));
        float x = pos.X + D(13);
        if (icon != null)
        {
            DrawIcon(dl, icon, new Vector2(x, pos.Y), new Vector2(D(16), h), D(16), A(ink, alpha));
            x += D(16) + D(7);
        }
        Text(dl, f, new Vector2(x, pos.Y + (h - Fs(f)) * 0.5f), A(ink, alpha), label);
        return clicked;
    }

    /// <summary>The design's icon button (.icon-btn); <paramref name="quiet"/>: no fill until the mouse is over it.</summary>
    private bool IconButton(string id, string icon, Vector2 pos, Vector2 size, string tip, bool quiet = false, bool enabled = true)
    {
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, size) && enabled;
        bool hot = enabled && ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(tip);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float r = Math.Min(D(10), size.Y * 0.4f);
        if (!quiet || hot) dl.AddRectFilled(pos, pos + size, hot ? CardHot : Card2, r);
        if (!quiet) dl.AddRect(pos, pos + size, Line2, r);
        DrawIcon(dl, icon, pos, size, Math.Min(D(16), size.Y * 0.62f), A(hot ? Ink : Ink2, enabled ? 1f : 0.45f));
        return clicked;
    }

    /// <summary>The Auto-sort switch (.switch): its knob slides. True when clicked.</summary>
    private bool Switch(string id, Vector2 p, bool on)
    {
        Vector2 size = new(D(46), D(26));
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hot = ImGui.IsItemHovered();
        float target = on ? 1f : 0f;
        _knob += (target - _knob) * Math.Clamp(ImGui.GetIO().DeltaTime * 14f, 0f, 1f);
        if (MathF.Abs(_knob - target) < 0.01f) _knob = target;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        uint track = on ? Teal : hot ? Card2 : SwitchOff;
        dl.AddRectFilled(p, p + size, track, size.Y * 0.5f);
        dl.AddRect(p, p + size, on ? Teal : Line2, size.Y * 0.5f);
        float kr = D(9);
        Vector2 c = new(p.X + D(3) + kr + _knob * (size.X - 2 * D(3) - 2 * kr), p.Y + size.Y * 0.5f);
        dl.AddCircleFilled(c, kr, on ? White : KnobOff, 20);
        return clicked;
    }

    /// <summary>A meter (.meter): <paramref name="fill"/> of the width, teal, amber when nearly full, red when full.</summary>
    private void Meter(ImDrawListPtr dl, Vector2 p, float w, float fill, bool amber, bool full = false)
    {
        float h = D(6);
        dl.AddRectFilled(p, p + new Vector2(w, h), Well, h * 0.5f);
        float fw = Math.Max(h, w * Math.Clamp(fill, 0.02f, 1f));
        dl.AddRectFilled(p, p + new Vector2(fw, h), full ? Red : amber ? Amber : Teal, h * 0.5f);
    }

    /// <summary>The brass bag badge with the pack's number (.bag).</summary>
    private void Bag(ImDrawListPtr dl, Vector2 p, float s, string number)
    {
        float r = s * 0.26f;
        dl.AddRectFilled(p, p + new Vector2(s, s), BagBottom, r);
        dl.AddRectFilled(p, p + new Vector2(s, s * 0.55f), BagTop, r, ImDrawFlags.RoundCornersTop);
        dl.AddLine(new Vector2(p.X + r, p.Y + 1), new Vector2(p.X + s - r, p.Y + 1), A(White, 0.18f));
        dl.AddRect(p, p + new Vector2(s, s), A(Black, 0.5f), r);
        ImFontPtr f = Font(RynthUiFont.NarrowBold13);
        float size = Math.Min(Fs(f), s * 0.5f);
        Vector2 ts = f.CalcTextSizeA(size, float.MaxValue, 0, number);
        dl.AddText(f, size, Round(p + (new Vector2(s, s) - ts) * 0.5f), BagInk, number);
    }

    /// <summary>A dashed rounded-ish box (straight dashes on each side), for name chips, ghosts and "+ Add a kind...".</summary>
    private static void Dashed(ImDrawListPtr dl, Vector2 min, Vector2 max, uint col, float dash, float thick = 1f)
    {
        void Side(Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            float len = d.Length();
            if (len <= 0) return;
            Vector2 u = d / len;
            for (float t = 0; t < len; t += dash * 2)
                dl.AddLine(a + u * t, a + u * Math.Min(len, t + dash), col, thick);
        }
        float inset = dash * 0.8f;
        Side(new Vector2(min.X + inset, min.Y), new Vector2(max.X - inset, min.Y));
        Side(new Vector2(max.X, min.Y + inset), new Vector2(max.X, max.Y - inset));
        Side(new Vector2(max.X - inset, max.Y), new Vector2(min.X + inset, max.Y));
        Side(new Vector2(min.X, max.Y - inset), new Vector2(min.X, min.Y + inset));
    }

    // ── Text ─────────────────────────────────────────────────────────────

    private Vector2 Measure(ImFontPtr f, string s) => f.CalcTextSizeA(Fs(f), float.MaxValue, 0f, s);

    private void Text(ImDrawListPtr dl, ImFontPtr f, Vector2 p, uint c, string s) => dl.AddText(f, Fs(f), Round(p), c, s);

    // Text cut to a width with "...", made once per text and width.
    private readonly Dictionary<(string, int, int), string> _fit = new();

    /// <summary>Draws <paramref name="s"/> cut to <paramref name="maxW"/> with an ellipsis when it doesn't fit.</summary>
    private void TextFit(ImDrawListPtr dl, ImFontPtr f, Vector2 p, uint c, string s, float maxW)
    {
        if (maxW <= 4) return;
        Text(dl, f, p, c, Fit(f, s, maxW));
    }

    private string Fit(ImFontPtr f, string s, float maxW)
    {
        float size = Fs(f);
        if (f.CalcTextSizeA(size, float.MaxValue, 0f, s).X <= maxW) return s;
        var key = (s, (int)maxW, (int)(size * 10));
        if (_fit.TryGetValue(key, out string? cut)) return cut;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (f.CalcTextSizeA(size, float.MaxValue, 0f, string.Concat(s.AsSpan(0, mid).TrimEnd(), "…")).X <= maxW) lo = mid;
            else hi = mid - 1;
        }
        cut = lo == 0 ? "…" : string.Concat(s.AsSpan(0, lo).TrimEnd(), "…");
        _fit[key] = cut;
        return cut;
    }

    private static readonly string[] Chars = BuildChars();

    private static string[] BuildChars()
    {
        var a = new string[128];
        for (int i = 32; i < 127; i++) a[i] = ((char)i).ToString();
        return a;
    }

    /// <summary>Text with <paramref name="spacing"/> pixels between its letters (the shortcut bar's). Returns the x after it.</summary>
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
        if (font.NativePtr == null || text.Length == 0) return 0;
        float scale = size / font.FontSize, w = 0;
        for (int i = 0; i < text.Length; i++) w += font.GetCharAdvance(text[i]) * scale;
        return w + spacing * (text.Length - 1);
    }

    /// <summary>A section's eyebrow (.eyebrow): small tracked capitals, then a count in the number font. Returns the y under it.</summary>
    private float Eyebrow(ImDrawListPtr dl, Vector2 p, string caps, string count)
    {
        ImFontPtr f = Font(RynthUiFont.Narrow10), n = Font(RynthUiFont.Dash10);
        float size = Fs(f);
        float x = Tracked(dl, f, size, p, Ink3, caps, 0.12f * size);
        if (count.Length > 0) Text(dl, n, new Vector2(x + D(8), p.Y + (size - Fs(n)) * 0.5f), Ink2, count);
        return p.Y + size + D(10);
    }

    /// <summary>A Phosphor glyph centred in the box, <paramref name="px"/> across its em (the shortcut bar's DrawIcon, from Ui18's bake).</summary>
    private static void DrawIcon(ImDrawListPtr dl, string glyph, Vector2 min, Vector2 size, float px, uint color)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        ImFontPtr font = Font(RynthUiFont.Ui18);
        if (font.NativePtr == null) return;
        byte* g = (byte*)ImGuiNative.ImFont_FindGlyphNoFallback(font.NativePtr, glyph[0]);
        if (g == null) return;
        float advance = *(float*)(g + 4);
        if (advance <= 0) return;
        float* box = (float*)(g + 8);
        float scale = px / advance;
        Vector2 centre = new((box[0] + box[2]) * 0.5f * scale, (box[1] + box[3]) * 0.5f * scale);
        Vector2 at = min + size * 0.5f - centre;
        dl.AddText(font, font.FontSize * scale, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)), color, glyph);
    }

    // Small numbers as text, made once.
    private static readonly string[] Numbers = BuildNumbers();

    private static string[] BuildNumbers()
    {
        var a = new string[1000];
        for (int i = 0; i < a.Length; i++) a[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return a;
    }

    private static string Num(int n) => n >= 0 && n < Numbers.Length ? Numbers[n] : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

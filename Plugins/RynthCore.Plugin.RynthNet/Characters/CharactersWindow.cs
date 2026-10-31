// ============================================================================
//  RynthNet - Characters/CharactersWindow.cs
//  The Characters window, drawn by RynthNet (2026-10-21; it was RynthCore's
//  ImGui/Panels/CharactersFace.cs): RynthCore hosts the window (the manifest's
//  "characters" window: header, pop-out, bar button, remembered place) and calls
//  Draw through RynthPluginRenderEmbed. Each row: an arrow to the character
//  (turning with you), name, server, what it is doing, a stale marker, the
//  distance and compass point, the three vitals and where it is. Click selects
//  it in game; right-click for /rn commands. Looks: Shared/RynthUi (the
//  player's theme). AC's render thread: it reads the published rows and the
//  pose, and queues everything it does for the plugin tick.
//
//  Two looks (2026-10-31, CharactersLook.cs; /rn view, kept per character):
//    minimal  the default, in item-counter.lua's style: one line per
//             character (status dot, name coloured by its health, three thin
//             vital bars, the arrow and distance when there is room), no frame
//             or background on an engine with frameless windows (v90). Hover
//             it for the pencil that brings the full look back.
//    full     every column, as before. On a v90 engine the window is
//             frameless, so this look paints its own frame and title row:
//             drag it to move, Ctrl+wheel for the text size, the buttons for
//             the minimal look, pop-out and close; the corner resizes it.
//  Both ask for the height they need (the window fits it, "fitHeight").
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthNet.Characters;

internal sealed class CharactersWindow
{
    private readonly RynthCoreHost _host;
    private readonly Action<uint> _select;
    private readonly Action<string, string> _tell;
    private readonly Action _followMeAll;
    private readonly Func<(bool Ok, float Heading)> _heading;
    private readonly Action<CharactersViewMode> _setView;

    // The vitals colours of RynthAi's dashboard (fixed, whatever the theme).
    private static readonly uint HpCol = Argb(0xFFD93333), StCol = Argb(0xFF40D973), MpCol = Argb(0xFF268CF2),
        BarBg = Argb(0xFF0D161F), Purple = Argb(0xFFBF4DFF), White = Argb(0xFFFFFFFF);

    private const string HelpText =
        "Your other characters: every RynthCore client on this PC running the RynthNet plugin.\n\n" +
        "Each row shows health, stamina and mana, what the character is doing, and where it is. " +
        "The distance and direction show when you are both outdoors (or in surface buildings), or in the same dungeon; " +
        "otherwise the row says elsewhere and names the dungeon or gives map coordinates. Characters on another server " +
        "only show their server.\n\n" +
        "Rows stay in alphabetical order. A row that stops updating says so (its client may be frozen).\n\n" +
        "Click a row to select that character in game (when this client can see it). Right-click for commands: " +
        "follow me, stop following, start or stop the macro. They go through RynthNet, so the other client's " +
        "remote command setting (/rn remote) decides whether they run.\n\n" +
        "The arrows-in button (or /rn view minimal) shows one line per character without the frame; " +
        "hover it for the pencil (or /rn view full) to come back here. Kept per character.\n\n" +
        "/rc characters opens and closes this window.";

    private float _k = 1f;
    private float Px(float v) => v * _k;

    // The right-clicked row (copied: the rows can change while the popup is open).
    private string _menuName = "";
    private uint _menuId;
    private bool _menuInView, _menuOtherWorld, _menuSelf, _menuLoggedOut;
    private bool _openMenu;

    public CharactersWindow(RynthCoreHost host, Action<uint> select, Action<string, string> tell, Action followMeAll,
        Func<(bool, float)> heading, Action<CharactersViewMode> setView)
    {
        _host = host;
        _select = select;
        _tell = tell;
        _followMeAll = followMeAll;
        _heading = heading;
        _setView = setView;
    }

    /// <summary>
    /// Draws the window body into the current ImGui window in <paramref name="mode"/>'s look.
    /// <paramref name="frameless"/>: the engine gave the window no frame (v90), so the full look paints
    /// its own. <paramref name="popped"/>: drawn in its own OS window, so the minimal look keeps a
    /// background. Returns the height it needs (pixels; 0 = whatever the window is). Render thread.
    /// </summary>
    public float Draw(CharactersView view, CharactersViewMode mode, bool frameless, bool popped = false)
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui10).FontSize / 10f);
        if (mode == CharactersViewMode.Full) return DrawFull(view, ownFrame: frameless);
        if (popped)
        {
            Vector2 o = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddRectFilled(o, o + ImGui.GetContentRegionAvail(), Background, 4);
        }
        return DrawMinimal(view, frameless && !popped);
    }

    // ── Full look ───────────────────────────────────────────────────────

    /// <summary>
    /// Every column. <paramref name="ownFrame"/>: paint the frame and a title row (the engine gave none)
    /// and return the height the rows need; otherwise fill the engine's frame and return 0.
    /// </summary>
    private float DrawFull(CharactersView view, bool ownFrame)
    {
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        float titleH = ownFrame ? Px(20) : 0f;
        float rowsH = view.Rows.Length == 0 ? Px(30) : view.Rows.Length * RowH;
        // Margin, title, the status line, the rows, and room under them for the corner grip.
        float want = MathF.Ceiling(6 + titleH + Px(24) + rowsH + 6 + Px(12));
        if (ownFrame) size.Y = Math.Max(size.Y, want);   // a frame late: draw at the height asked for
        if (size.X < 40 || size.Y < 30) return ownFrame ? want : 0f;
        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        using (PushFont(RynthUiFont.Ui10))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
            if (ownFrame) DrawTitle(origin, size.X, titleH);
            ImGui.SetCursorScreenPos(origin + new Vector2(6, 6 + titleH));
            ImGui.Indent(6);
            float w = size.X - 12;
            DrawTop(view, w, minimalButton: !ownFrame);   // framed by the engine: no title row, so the button sits here

            // The host's resize grip sits over the bottom-right corner: the rows end above it.
            float bodyH = ownFrame ? rowsH : Math.Max(Px(30), origin.Y + size.Y - 6 - ImGui.GetCursorScreenPos().Y - Px(12));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Field);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
            ImGui.BeginChild("##chars_rows", new Vector2(w, bodyH));
            DrawRows(view);
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();

            ImGui.Unindent(6);
            ImGui.PopStyleVar();
            DrawMenu(view);
        }
        frame.AddRect(origin, origin + size, Border, 4);
        return ownFrame ? want : 0f;
    }

    /// <summary>The full look's own title row (frameless window): drag to move; minimal, pop-out and close.</summary>
    private void DrawTitle(Vector2 origin, float width, float h)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float bw = Px(20), gap = Px(3);
        float buttons = 3 * bw + 2 * gap + 6;
        var top = new Vector2(origin.X, origin.Y + 3);
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##chars_title", new Vector2(Math.Max(10, width - buttons), h + 3));
        _host.PanelDragWithLastItem();
        SafeImGui.SetItemTooltip("Drag to move. Ctrl+wheel: text size. The corner resizes it.");
        ImFontPtr bold = Font(RynthUiFont.UiBold11);
        DrawCentered(dl, Font(RynthUiFont.Ui11), RynthIcons.UsersThree, new Vector2(origin.X + 6, top.Y), new Vector2(Px(14), h), Accent);
        dl.AddText(bold, bold.FontSize, new Vector2(origin.X + 6 + Px(18), top.Y + (h - bold.FontSize) * 0.5f), Text, CharactersManifest.Title);

        float bx = origin.X + width - 6 - 3 * bw - 2 * gap;
        var bsize = new Vector2(bw, h - Px(2));
        if (IconButton("##chars_minimal", RynthIcons.ArrowsInLineVertical, new Vector2(bx, top.Y), bsize, Text, ButtonFill, font: RynthUiFont.Ui11))
            _setView(CharactersViewMode.Minimal);
        ImGui.SetItemTooltip("Minimal: one line per character, no frame (/rn view minimal)");
        if (IconButton("##chars_popout", RynthIcons.ArrowSquareOut, new Vector2(bx + bw + gap, top.Y), bsize, Text, ButtonFill, font: RynthUiFont.Ui11))
            _host.SetPanelPopOut(CharactersManifest.Title, RynthPanelPopOut.Toggle);
        ImGui.SetItemTooltip("Pop out into its own window, or dock it back");
        if (IconButton("##chars_close", RynthIcons.X, new Vector2(bx + 2 * (bw + gap), top.Y), bsize, Text, ButtonFill, font: RynthUiFont.Ui11))
            _host.SetPanelOpen(CharactersManifest.Title, RynthPanelOpen.Close);
        ImGui.SetItemTooltip("Close (/rc characters opens it again)");
    }

    // ── Minimal look ────────────────────────────────────────────────────

    private static readonly uint Shadow = 0xB0000000;

    /// <summary>
    /// One line per character: status dot, name (its health's colour), three thin bars, arrow and
    /// distance when there's room. <paramref name="frameless"/>: no frame or background, the text
    /// shadowed so it reads over the game. Returns the height it needs.
    /// </summary>
    private float DrawMinimal(CharactersView view, bool frameless)
    {
        Vector2 origin = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        CharacterRow[] rows = view.Rows;
        float want = CharactersLook.MinimalHeight(rows.Length, _k);
        if (width < 20) return want;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        using (PushFont(RynthUiFont.Ui10))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
            float fontH = ImGui.GetFontSize();
            float rowH = MathF.Max(Px(CharactersLook.RowHeight), MathF.Ceiling(fontH + 3));
            want = MathF.Max(want, MathF.Ceiling(Math.Max(1, rows.Length) * rowH + 4));
            var start = new Vector2(origin.X + 2, origin.Y + 2);
            bool windowHot = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            float lineW;

            if (rows.Length == 0)
            {
                // Nothing to list: the status, faint (and the pencil on hover).
                DrawDot(dl, new Vector2(start.X + Px(CharactersLook.DotWidth) * 0.5f, start.Y + rowH * 0.5f), CharDot.Off);
                string s = view.Status;
                float tw = Math.Min(ImGui.CalcTextSize(s).X, width - Px(CharactersLook.DotWidth) - Px(30));
                ShadowClip(dl, s, start.X + Px(CharactersLook.DotWidth), start.Y + (rowH - fontH) * 0.5f, tw, Faded(Text), frameless);
                lineW = Px(CharactersLook.DotWidth) + Math.Max(0, tw);
            }
            else
            {
                // Columns from the longest name and distance, so the bars line up.
                float nameW = 0, distW = 0;
                foreach (CharacterRow r in rows)
                {
                    nameW = MathF.Max(nameW, ImGui.CalcTextSize(r.Name).X);
                    if (ShowsDistance(r)) distW = MathF.Max(distW, ImGui.CalcTextSize(r.DistanceText).X);
                }
                nameW = MathF.Ceiling(nameW + Px(2));
                float editRoom = Px(20);
                MinimalLine line = CharactersLook.Minimal(width - 4 - editRoom, nameW, distW, _k);
                lineW = line.Width(_k, distW);
                (bool haveHeading, float heading) = _heading();
                int hovered = -1;
                for (int i = 0; i < rows.Length; i++)
                {
                    CharacterRow r = rows[i];
                    var p = new Vector2(start.X, start.Y + i * rowH);
                    ImGui.SetCursorScreenPos(p);
                    ImGui.PushID(i);
                    bool clicked = ImGui.InvisibleButton("##mrow", new Vector2(Math.Max(1, lineW), rowH),
                        ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
                    bool hot = ImGui.IsItemHovered();
                    bool right = clicked && ImGui.IsMouseReleased(ImGuiMouseButton.Right);
                    ImGui.PopID();
                    if (hot) hovered = i;
                    if (clicked && !right && r.InView && r.Id != 0 && !r.IsSelf) _select(r.Id);
                    if (right) OpenMenu(r);
                    DrawMinimalRow(dl, r, p, line, lineW, rowH, fontH, hot, r.Id != 0 && r.Id == view.SelectedId,
                        heading, haveHeading, distW, frameless);
                }
                if (hovered >= 0 && rows[hovered].Tooltip.Length > 0 && !ImGui.IsPopupOpen("##chars_menu"))
                {
                    ImGui.BeginTooltip();
                    ImGui.TextUnformatted(rows[hovered].Tooltip);
                    ImGui.EndTooltip();
                }
            }

            // The pencil: back to the full look (shown while the mouse is on the window).
            if (windowHot)
            {
                float bs = rowH - 2;
                var bp = new Vector2(Math.Min(start.X + lineW + Px(4), origin.X + width - bs - 2), start.Y + 1);
                if (IconButton("##chars_full", RynthIcons.PencilSimple, bp, new Vector2(bs, bs), Text, WithAlpha(Background, 0xE0),
                        font: RynthUiFont.Ui11))
                    _setView(CharactersViewMode.Full);
                ImGui.SetItemTooltip("Edit: the full window with every column, to move or resize it (/rn view full)");
            }

            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(Math.Max(1, width), Math.Max(1, want - 1)));
            ImGui.PopStyleVar();
            DrawMenu(view);
        }
        return want;
    }

    private static bool ShowsDistance(CharacterRow r) => !r.IsSelf && !r.LoggedOut && r.DistanceText.Length > 0;

    private void DrawMinimalRow(ImDrawListPtr dl, CharacterRow r, Vector2 p, MinimalLine line, float lineW, float rowH, float fontH,
        bool hot, bool isSel, float heading, bool haveHeading, float distW, bool frameless)
    {
        if (hot) dl.AddRectFilled(p, p + new Vector2(lineW, rowH), WithAlpha(Background, 0xB0), 2);
        if (isSel) dl.AddRect(p, p + new Vector2(lineW, rowH), Accent, 2);
        float dim = r.IsSelf || r.LoggedOut ? 0.6f : 1f;
        float x = p.X;
        float ty = p.Y + (rowH - fontH) * 0.5f;

        DrawDot(dl, new Vector2(x + Px(CharactersLook.DotWidth) * 0.5f, p.Y + rowH * 0.5f), CharactersLook.Dot(r));
        x += Px(CharactersLook.DotWidth);

        ShadowClip(dl, r.Name, x, ty, line.NameWidth, Fade(NameColor(r), dim), frameless);
        x += line.NameWidth;

        if (line.Bars)
        {
            x += Px(CharactersLook.Gap);
            float bh = Math.Max(2f, Px(CharactersLook.BarHeight)), by = MathF.Round(p.Y + (rowH - bh) * 0.5f), gap = Px(CharactersLook.BarGap);
            if (r.HasVitals)
            {
                ThinBar(dl, x, by, line.BarWidth, bh, r.Hp.Fraction, Fade(HpCol, dim));
                ThinBar(dl, x + line.BarWidth + gap, by, line.BarWidth, bh, r.St.Fraction, Fade(StCol, dim));
                ThinBar(dl, x + 2 * (line.BarWidth + gap), by, line.BarWidth, bh, r.Mp.Fraction, Fade(MpCol, dim));
            }
            x += 3 * line.BarWidth + 2 * gap;
        }

        if (line.Distance && ShowsDistance(r))
        {
            x += Px(CharactersLook.Gap);
            var mid = new Vector2(x + Px(CharactersLook.ArrowWidth) * 0.5f, p.Y + rowH * 0.5f);
            uint col = Fade(r.Measured ? Text : Faded(Mute), dim);
            if (r.Measured && haveHeading && !float.IsNaN(r.Bearing))
            {
                if (frameless) DrawArrow(dl, mid + new Vector2(1, 1), r.Bearing - heading, Shadow, _k * 0.8f);
                DrawArrow(dl, mid, r.Bearing - heading, Fade(StateColor(r), dim), _k * 0.8f);
            }
            else if (r.Measured)
                dl.AddCircleFilled(mid, Px(2f), col);
            x += Px(CharactersLook.ArrowWidth);
            ShadowClip(dl, r.DistanceText, x, ty, distW + 1, col, frameless);
        }
    }

    /// <summary>The name's colour: its health's warning (amber low, red critical or dead), else the text colour.</summary>
    private static uint NameColor(CharacterRow r) => r.IsSelf ? Text : CharactersLook.Hp(r) switch
    {
        HpWarn.Dead or HpWarn.Critical => Red,
        HpWarn.Low => Highlight,
        HpWarn.Unknown when r.LoggedOut => Faded(Mute),
        _ => Text,
    };

    private void DrawDot(ImDrawListPtr dl, Vector2 c, CharDot dot)
    {
        float r = Px(3f);
        switch (dot)
        {
            case CharDot.Off: dl.AddCircle(c, r, Faded(Mute), 12, Px(1.2f)); break;
            case CharDot.Stale: dl.AddCircle(c, r, Highlight, 12, Px(1.6f)); break;
            case CharDot.Self: dl.AddCircleFilled(c, r, Mute, 12); break;
            case CharDot.Dead: dl.AddCircleFilled(c, r, Red, 12); break;
            case CharDot.Portal: dl.AddCircleFilled(c, r, Purple, 12); break;
            case CharDot.Fighting: dl.AddCircleFilled(c, r, Highlight, 12); break;
            case CharDot.Botting: dl.AddCircleFilled(c, r, Green, 12); break;
            default: dl.AddCircleFilled(c, r, Faded(Mute), 12); break;   // idle
        }
    }

    private static void ThinBar(ImDrawListPtr dl, float x, float y, float w, float h, float fraction, uint color)
    {
        if (w <= 1) return;
        fraction = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0f;
        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w, y + h), WithAlpha(BarBg, 0xD0), 1);
        if (fraction > 0) dl.AddRectFilled(new Vector2(x, y), new Vector2(x + w * fraction, y + h), color, 1);
    }

    /// <summary>Text clipped to <paramref name="width"/>, with a 1 px shadow when it sits over the game.</summary>
    private static void ShadowClip(ImDrawListPtr dl, string text, float x, float y, float width, uint color, bool shadow)
    {
        if (text.Length == 0 || width <= 0) return;
        dl.PushClipRect(new Vector2(x, y - 2), new Vector2(x + width + 1, y + 40), true);
        if (shadow) dl.AddText(new Vector2(x + 1, y + 1), Shadow, text);
        dl.AddText(new Vector2(x, y), color, text);
        dl.PopClipRect();
    }

    // ── Top line ────────────────────────────────────────────────────────

    private void DrawTop(CharactersView view, float w, bool minimalButton)
    {
        Vector2 start = ImGui.GetCursorScreenPos();
        float h = Px(20), iconW = Px(22);
        int icons = minimalButton ? 3 : 2;
        float fontH = ImGui.GetFontSize();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float textW = Math.Max(10, w - icons * iconW - Px(4) * icons);
        dl.PushClipRect(start, start + new Vector2(textW, h), true);
        dl.AddText(new Vector2(start.X, start.Y + (h - fontH) * 0.5f), Mute, view.Status);
        dl.PopClipRect();

        float bx = start.X + w - icons * iconW - Px(4) * (icons - 1);
        if (minimalButton)
        {
            if (IconButton("##chars_minimal", RynthIcons.ArrowsInLineVertical, new Vector2(bx, start.Y), new Vector2(iconW, h), Text, ButtonFill,
                    font: RynthUiFont.Ui11))
                _setView(CharactersViewMode.Minimal);
            ImGui.SetItemTooltip("Minimal: one line per character (/rn view minimal)");
            bx += iconW + Px(4);
        }
        bool canFollow = view.MyName.Length > 0;
        if (IconButton("##chars_followall", RynthIcons.Footprints, new Vector2(bx, start.Y), new Vector2(iconW, h), Text, ButtonFill,
                enabled: canFollow, font: RynthUiFont.Ui11))
            _followMeAll();
        ImGui.SetItemTooltip("Everyone follow me (/rn followme): every other client runs /ra follow <you>.");
        IconButton("##chars_help", RynthIcons.Info, new Vector2(bx + iconW + Px(4), start.Y), new Vector2(iconW, h), Mute, ButtonFill,
            font: RynthUiFont.Ui11);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
            ImGui.TextUnformatted(HelpText);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h + Px(4)));
        ImGui.Dummy(Vector2.Zero);
    }

    // ── Rows ────────────────────────────────────────────────────────────

    private float RowH => Px(38);

    private void DrawRows(CharactersView view)
    {
        CharacterRow[] rows = view.Rows;
        float bw = ImGui.GetContentRegionAvail().X;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        if (rows.Length == 0)
        {
            Vector2 at = ImGui.GetCursorScreenPos() + new Vector2(Px(6), Px(6));
            dl.AddText(at, Faded(Mute), "Not in the world.");
            return;
        }

        (bool haveHeading, float heading) = _heading();
        float rowH = RowH;
        Vector2 start = ImGui.GetCursorScreenPos();
        int hovered = -1;

        for (int i = 0; i < rows.Length; i++)
        {
            CharacterRow r = rows[i];
            var p = new Vector2(start.X, start.Y + i * rowH);
            ImGui.SetCursorScreenPos(p);
            ImGui.PushID(i);
            bool clicked = ImGui.InvisibleButton("##row", new Vector2(bw, rowH),
                ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
            bool hot = ImGui.IsItemHovered();
            bool right = clicked && ImGui.IsMouseReleased(ImGuiMouseButton.Right);
            ImGui.PopID();
            if (hot) hovered = i;
            if (clicked && !right && r.InView && r.Id != 0 && !r.IsSelf) _select(r.Id);
            if (right) OpenMenu(r);

            DrawRow(dl, r, p, bw, rowH, i, hot, r.Id != 0 && r.Id == view.SelectedId, heading, haveHeading);
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(bw, rows.Length * rowH));

        if (hovered >= 0 && rows[hovered].Tooltip.Length > 0 && !ImGui.IsPopupOpen("##chars_menu"))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(rows[hovered].Tooltip);
            ImGui.EndTooltip();
        }
    }

    private void DrawRow(ImDrawListPtr dl, CharacterRow r, Vector2 p, float bw, float rowH, int index, bool hot, bool isSel,
        float heading, bool haveHeading)
    {
        uint bg = isSel ? Selected : hot ? Lighten(Field) : (index & 1) == 1 ? RowAlt : 0;
        if (bg != 0) dl.AddRectFilled(p, p + new Vector2(bw, rowH), bg);
        if (isSel) dl.AddRect(p, p + new Vector2(bw, rowH), Accent);
        // This client's row is dimmed: it's here for comparison.
        float dim = r.IsSelf || r.LoggedOut ? 0.55f : 1f;

        ImFontPtr bold = Font(RynthUiFont.UiBold11), f10 = Font(RynthUiFont.Ui10);
        float arrowW = Px(20), pad = Px(4);
        float line1 = p.Y + Px(3);
        float x = p.X + arrowW;
        float right = p.X + bw - pad;

        // Arrow / dot / ring.
        var mid = new Vector2(p.X + arrowW * 0.5f, line1 + bold.FontSize * 0.5f + Px(1));
        uint arrowCol = Fade(StateColor(r), dim);
        if (!r.IsSelf && r.Measured && haveHeading && !float.IsNaN(r.Bearing))
            DrawArrow(dl, mid, r.Bearing - heading, arrowCol, _k);
        else if (r.IsSelf || r.Measured)
            dl.AddCircleFilled(mid, Px(2.5f), arrowCol);
        else
            dl.AddCircle(mid, Px(3.5f), Faded(Mute), 12, Px(1.2f));

        // Right side of line 1: distance + compass (or "elsewhere").
        string dist = r.IsSelf ? "" : r.Measured ? r.DistanceText + (r.Compass.Length > 0 ? " " + r.Compass : "") : r.DistanceText;
        float distW = dist.Length > 0 ? ImGui.CalcTextSize(dist).X : 0;
        if (dist.Length > 0)
            dl.AddText(new Vector2(right - distW, line1 + (bold.FontSize - ImGui.GetFontSize()) * 0.5f),
                Fade(r.Measured ? Text : Faded(Mute), dim), dist);
        float limit = right - distW - (distW > 0 ? Px(8) : 0);

        // Name (+ server), then state and the stale marker.
        float nameW = Math.Min(CalcW(bold, r.Name), Math.Max(Px(40), (limit - x) * 0.55f));
        Clip(dl, bold, r.Name, x, line1, nameW, Fade(r.IsSelf ? Mute : Text, dim));
        float sx = x + nameW + Px(6);
        if (r.OtherWorld.Length > 0)
        {
            string wtext = "[" + r.OtherWorld + "]";
            float ww = Math.Min(CalcW(f10, wtext), Math.Max(0, limit - sx));
            Clip(dl, f10, wtext, sx, line1 + Px(1), ww, Fade(Highlight, dim));
            sx += ww + Px(6);
        }
        if (r.StateText.Length > 0 && sx < limit)
        {
            float tw = Math.Min(CalcW(f10, r.StateText), limit - sx);
            Clip(dl, f10, r.StateText, sx, line1 + Px(1), tw, Fade(StateColor(r), dim));
            sx += tw + Px(6);
        }
        if (r.Stale && sx < limit)
            Clip(dl, f10, RynthIcons.Warning + " " + r.StaleText, sx, line1 + Px(1), limit - sx, Highlight);

        // Line 2: bars, then the location.
        float line2 = line1 + bold.FontSize + Px(4);
        float barH = Px(13);
        float locW = r.Location.Length > 0 ? Math.Min(CalcW(f10, r.Location), Math.Max(Px(70), bw * 0.32f)) : 0;
        float barsW = right - x - (locW > 0 ? locW + Px(8) : 0);
        if (barsW < Px(150)) { barsW = right - x; locW = 0; }   // narrow: bars first, the tooltip has the place
        if (r.HasVitals)
        {
            float gap = Px(3);
            float one = (barsW - 2 * gap) / 3f;
            Bar(dl, x, line2, one, barH, r.Hp, HpCol, f10, dim);
            Bar(dl, x + one + gap, line2, one, barH, r.St, StCol, f10, dim);
            Bar(dl, x + 2 * (one + gap), line2, one, barH, r.Mp, MpCol, f10, dim);
        }
        else if (!r.LoggedOut)
        {
            Clip(dl, f10, "no vitals yet", x, line2, barsW, Faded(Mute));
        }
        if (locW > 0)
            Clip(dl, f10, r.Location, right - locW, line2 + (barH - f10.FontSize) * 0.5f, locW, Fade(Mute, dim));
    }

    private void Bar(ImDrawListPtr dl, float x, float y, float w, float h, CharVital v, uint color, ImFontPtr font, float dim)
    {
        if (w <= 2) return;
        var a = new Vector2(x, y);
        var b = new Vector2(x + w, y + h);
        float round = Px(3);
        dl.AddRectFilled(a, b, BarBg, round);
        if (v.Fraction > 0) dl.AddRectFilled(a, new Vector2(x + w * v.Fraction, b.Y), Fade(color, dim), round);
        float tw = CalcW(font, v.Text);
        float tx = x + Math.Max(Px(3), (w - tw) * 0.5f);
        dl.PushClipRect(a, b, true);
        dl.AddText(font, font.FontSize, new Vector2(tx, y + (h - font.FontSize) * 0.5f), Fade(White, dim), v.Text);
        dl.PopClipRect();
    }

    private static uint StateColor(CharacterRow r) => r.StateKind switch
    {
        _ when r.IsSelf => Mute,
        CharStateKind.Dead => Red,
        CharStateKind.Fighting => Highlight,
        CharStateKind.Botting => Green,
        CharStateKind.Portal => Purple,
        CharStateKind.CombatMode => Accent,
        CharStateKind.NoStatus or CharStateKind.LoggedOut => Faded(Mute),
        _ => Mute,
    };

    private static float CalcW(ImFontPtr font, string text) =>
        text.Length == 0 ? 0 : font.CalcTextSizeA(font.FontSize, float.MaxValue, 0f, text).X;

    private static void Clip(ImDrawListPtr dl, ImFontPtr font, string text, float x, float y, float width, uint color)
    {
        if (text.Length == 0 || width <= 0) return;
        dl.PushClipRect(new Vector2(x, y - 2), new Vector2(x + width, y + font.FontSize + 4), true);
        dl.AddText(font, font.FontSize, new Vector2(x, y), color, text);
        dl.PopClipRect();
    }

    private static uint Fade(uint abgr, float k)
    {
        if (k >= 1f) return abgr;
        uint a = (uint)Math.Clamp((int)(((abgr >> 24) & 0xFF) * k), 0, 255);
        return (abgr & 0x00FFFFFF) | (a << 24);
    }

    /// <summary>An arrow pointing <paramref name="degrees"/> clockwise from straight up (up = the way you face).</summary>
    private static void DrawArrow(ImDrawListPtr dl, Vector2 c, float degrees, uint color, float k)
    {
        float rad = degrees * (MathF.PI / 180f);
        float s = MathF.Sin(rad), co = MathF.Cos(rad);
        Vector2 tip = Rot(c, 0, -6.5f * k, s, co), left = Rot(c, -4.5f * k, 5f * k, s, co),
            notch = Rot(c, 0, 2.5f * k, s, co), rgt = Rot(c, 4.5f * k, 5f * k, s, co);
        dl.AddTriangleFilled(tip, left, notch, color);
        dl.AddTriangleFilled(tip, notch, rgt, color);
    }

    private static Vector2 Rot(Vector2 c, float x, float y, float s, float co) =>
        new(c.X + x * co - y * s, c.Y + x * s + y * co);

    // ── Right-click menu ────────────────────────────────────────────────

    private void OpenMenu(CharacterRow r)
    {
        _menuName = r.Name;
        _menuId = r.Id;
        _menuInView = r.InView;
        _menuOtherWorld = r.OtherWorld.Length > 0;
        _menuSelf = r.IsSelf;
        _menuLoggedOut = r.LoggedOut;
        _openMenu = true;
    }

    private void DrawMenu(CharactersView view)
    {
        if (_openMenu)
        {
            _openMenu = false;
            ImGui.OpenPopup("##chars_menu");
        }
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Background);
        ImGui.PushStyleColor(ImGuiCol.Border, Accent);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Px(8), Px(6)));
        bool open = ImGui.BeginPopup("##chars_menu");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);
        if (!open) return;
        try
        {
            string me = view.MyName;
            bool canFollow = me.Length > 0;
            if (!_menuSelf && !_menuLoggedOut)
            {
                SafeImGui.TextDisabled(_menuName);
                if (ImGui.MenuItem("Select", "", false, _menuInView && _menuId != 0)) _select(_menuId);
                ImGui.Separator();
                if (ImGui.MenuItem("Follow me", "", false, canFollow && !_menuOtherWorld)) _tell(_menuName, "/ra follow " + me);
                SafeImGui.SetItemTooltip($"/rn tell {_menuName}, /ra follow {me}");
                if (ImGui.MenuItem("Stop following")) _tell(_menuName, "/ra follow off");
                SafeImGui.SetItemTooltip($"/rn tell {_menuName}, /ra follow off");
                ImGui.Separator();
                if (ImGui.MenuItem("Start macro")) _tell(_menuName, "/ra start");
                SafeImGui.SetItemTooltip($"/rn tell {_menuName}, /ra start");
                if (ImGui.MenuItem("Stop macro")) _tell(_menuName, "/ra stop");
                SafeImGui.SetItemTooltip($"/rn tell {_menuName}, /ra stop");
                ImGui.Separator();
            }
            if (ImGui.MenuItem("Everyone: follow me", "", false, canFollow)) _followMeAll();
            ImGui.SetItemTooltip("/rn followme");
            ImGui.TextDisabled("The other client's /rn remote setting decides.");
        }
        finally
        {
            ImGui.EndPopup();
        }
    }
}

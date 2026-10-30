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
    private readonly Action<uint> _select;
    private readonly Action<string, string> _tell;
    private readonly Action _followMeAll;
    private readonly Func<(bool Ok, float Heading)> _heading;

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
        "/rc characters opens and closes this window.";

    private float _k = 1f;
    private float Px(float v) => v * _k;

    // The right-clicked row (copied: the rows can change while the popup is open).
    private string _menuName = "";
    private uint _menuId;
    private bool _menuInView, _menuOtherWorld, _menuSelf, _menuLoggedOut;
    private bool _openMenu;

    public CharactersWindow(Action<uint> select, Action<string, string> tell, Action followMeAll, Func<(bool, float)> heading)
    {
        _select = select;
        _tell = tell;
        _followMeAll = followMeAll;
        _heading = heading;
    }

    /// <summary>Draws the window body into the current ImGui window. Render thread.</summary>
    public void Draw(CharactersView view)
    {
        _k = Math.Max(0.5f, Font(RynthUiFont.Ui10).FontSize / 10f);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = ImGui.GetContentRegionAvail();
        if (size.X < 40 || size.Y < 30) return;
        ImDrawListPtr frame = ImGui.GetWindowDrawList();
        frame.AddRectFilled(origin, origin + size, Background, 4);
        using (PushFont(RynthUiFont.Ui10))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 4));
            ImGui.SetCursorScreenPos(origin + new Vector2(6, 6));
            ImGui.Indent(6);
            float w = size.X - 12;
            DrawTop(view, w);

            // The host's resize grip sits over the bottom-right corner: the rows end above it.
            float bodyH = Math.Max(Px(30), origin.Y + size.Y - 6 - ImGui.GetCursorScreenPos().Y - Px(12));
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
    }

    // ── Top line ────────────────────────────────────────────────────────

    private void DrawTop(CharactersView view, float w)
    {
        Vector2 start = ImGui.GetCursorScreenPos();
        float h = Px(20), iconW = Px(22);
        float fontH = ImGui.GetFontSize();
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        float textW = Math.Max(10, w - 2 * iconW - Px(8));
        dl.PushClipRect(start, start + new Vector2(textW, h), true);
        dl.AddText(new Vector2(start.X, start.Y + (h - fontH) * 0.5f), Mute, view.Status);
        dl.PopClipRect();

        float bx = start.X + w - 2 * iconW - Px(4);
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

// ============================================================================
//  RynthDefective - Corpses/CorpsesWindow.cs
//  Where your corpses are (Tom 2026-10-23): this character's, newest first,
//  then your other characters' on this PC. A small frameless window in the
//  Selection window's look (RynthCore hosts it, plugin API v90).
//
//    the title row  drag to move; Ctrl+wheel: bigger or smaller (30-250%);
//                   right-click: hide it (/rdef corpses, the bar button).
//    a row          the place, then how long ago, when it rots and, near it,
//                   how far and which way (the arrow is from where you face).
//                   Click: select the corpse when it's in view; X: forget it.
//
//  AC's render thread; reads the view the tick built (CorpsesView).
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthDefective.ShortcutBar;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthDefective.Corpses;

internal sealed class CorpsesWindow
{
    public const string Title = "Corpses";

    private readonly RynthCoreHost _host;
    private readonly Action<CorpseRow> _select, _forget;
    private float _k = 1f, _r = 1f;

    public CorpsesWindow(RynthCoreHost host, Action<CorpseRow> select, Action<CorpseRow> forget)
    {
        _host = host;
        _select = select;
        _forget = forget;
    }

    private float Px(float v) => v * _k;
    private float Fs(ImFontPtr f) => f.FontSize * _r;

    private void Scale()
    {
        float fontK = Math.Max(0.5f, Font(RynthUiFont.Ui10).FontSize / 10f);
        int pct = _host.PanelScale(Title);
        _k = pct > 0 ? pct / 100f : fontK;
        _r = _k / fontK;
    }

    private const float Pad = 6, HeadH = 18, RowH = 30, Radius = 4, Mark = 14, XW = 16;

    private uint _bg, _key, _inset, _line, _text, _dim, _faint, _accent;
    private static uint A(uint c, float alpha) => WithAlpha(c, (uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));

    /// <summary>Draws the corpses into the host's window; returns the height it needs (pixels). Render thread.</summary>
    public float Draw(CorpsesView v)
    {
        Scale();
        _bg = Color(RynthUiColor.Background);
        _key = Color(RynthUiColor.KeyFace);
        _inset = Color(RynthUiColor.Inset);
        _line = Color(RynthUiColor.Hairline);
        _text = Color(RynthUiColor.Text);
        _dim = Color(RynthUiColor.Muted);
        _faint = Color(RynthUiColor.Faint);
        _accent = Color(RynthUiColor.Accent);

        Vector2 origin = ImGui.GetCursorScreenPos();
        origin = new Vector2(MathF.Round(origin.X), MathF.Round(origin.Y));
        float width = MathF.Floor(ImGui.GetContentRegionAvail().X);
        if (width < 40) return Px(40);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        float pad = Px(Pad), rowH = Px(RowH), headH = Px(HeadH);
        int empty = v.Mine.Count == 0 && v.Others.Count == 0 ? 1 : 0;
        float height = MathF.Ceiling(pad + headH + (v.Mine.Count + empty) * rowH
                                     + (v.Others.Count > 0 ? headH + v.Others.Count * rowH : 0) + pad * 0.5f);
        Vector2 max = origin + new Vector2(width, height);
        dl.AddRectFilled(origin, max, A(_bg, 0.94f), Px(Radius));
        dl.AddRect(origin, max, _line, Px(Radius));

        // The title row: the drag surface.
        float y = origin.Y + pad * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y));
        ImGui.InvisibleButton("##corpses_drag", new Vector2(width, headH + pad * 0.5f));
        _host.PanelDragWithLastItem();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _host.SetPanelOpen(Title, RynthPanelOpen.Close);
        SafeImGui.SetItemTooltip("Where your corpses are: this character's, then your other characters' on this PC.\n"
                                 + "Drag to move; Ctrl+wheel: bigger or smaller; right-click: hide (/rdef corpses).");
        PanelButtonsWindow.DrawIcon(dl, RynthIcons.Skull, new Vector2(origin.X + pad, y), new Vector2(Px(Mark), headH), Px(12), _dim);
        ImFontPtr hf = Font(RynthUiFont.NarrowBold11), sf = Font(RynthUiFont.Narrow9), nf = Font(RynthUiFont.Narrow10);
        float tx = origin.X + pad + Px(Mark) + Px(4);
        dl.AddText(hf, Fs(hf), new Vector2(tx, MathF.Round(y + (headH - Fs(hf)) * 0.5f)), _dim, "CORPSES");
        if (v.Ready)
        {
            string count = v.Mine.Count.ToString(CultureInfo.InvariantCulture);
            float cw = sf.CalcTextSizeA(Fs(sf), float.MaxValue, 0f, count).X;
            dl.AddText(sf, Fs(sf), new Vector2(origin.X + width - pad - cw, MathF.Round(y + (headH - Fs(sf)) * 0.5f)), _faint, count);
        }
        y += headH;

        if (empty == 1)
        {
            dl.AddLine(new Vector2(origin.X + pad, y), new Vector2(origin.X + width - pad, y), _line);
            string msg = v.Ready ? "No corpses. Where you die shows here." : "Corpses";
            Clip(dl, sf, Fs(sf), new Vector2(origin.X + pad, MathF.Round(y + (rowH - Fs(sf)) * 0.5f)), _faint, msg, width - 2 * pad);
        }
        for (int i = 0; i < v.Mine.Count; i++)
        {
            Row(dl, v.Mine[i], i, new Vector2(origin.X, y), width, rowH, nf, sf, mine: true);
            y += rowH;
        }
        if (v.Others.Count > 0)
        {
            dl.AddLine(new Vector2(origin.X + pad, y + 0.5f), new Vector2(origin.X + width - pad, y + 0.5f), _line);
            dl.AddText(sf, Fs(sf), new Vector2(origin.X + pad, MathF.Round(y + (headH - Fs(sf)) * 0.5f)), _faint, "OTHER CHARACTERS");
            y += headH;
            for (int i = 0; i < v.Others.Count; i++)
            {
                Row(dl, v.Others[i], 1000 + i, new Vector2(origin.X, y), width, rowH, nf, sf, mine: false);
                y += rowH;
            }
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        return height;
    }

    private void Row(ImDrawListPtr dl, CorpseRow row, int n, Vector2 at, float width, float h, ImFontPtr nf, ImFontPtr sf, bool mine)
    {
        float pad = Px(Pad), xw = mine ? Px(XW) : 0f;
        dl.AddLine(new Vector2(at.X + pad, at.Y + 0.5f), new Vector2(at.X + width - pad, at.Y + 0.5f), A(_line, 0.6f));

        // The row: click selects the corpse (yours, while it's in view).
        ImGui.SetCursorScreenPos(at);
        bool click = ImGui.InvisibleButton(Id("##corpse", n), new Vector2(Math.Max(1, width - xw - pad), h));
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip(row.Tip);
        if (hot && mine) dl.AddRectFilled(at + new Vector2(1, 1), at + new Vector2(width - 1, h), A(_key, 0.55f), Px(3));
        if (click && mine) _select(row);

        // The arrow toward it, or a pin.
        Vector2 mc = new(at.X + pad + Px(Mark) * 0.5f, at.Y + h * 0.5f);
        if (!float.IsNaN(row.Relative)) Arrow(dl, mc, Px(Mark) * 0.42f, row.Relative, _accent);
        else PanelButtonsWindow.DrawIcon(dl, RynthIcons.MapPin, new Vector2(at.X + pad, at.Y), new Vector2(Px(Mark), h), Px(12), mine ? _dim : _faint);

        float tx = at.X + pad + Px(Mark) + Px(5), room = at.X + width - pad - xw - tx;
        float top = MathF.Round(at.Y + (h - Fs(nf) - Fs(sf) - Px(1)) * 0.5f);
        Clip(dl, nf, Fs(nf), new Vector2(tx, top), mine ? _text : _dim, row.Place, room);
        Clip(dl, sf, Fs(sf), new Vector2(tx, top + Fs(nf) + Px(1)), mine ? _dim : _faint, row.Detail, room);

        if (!mine) return;
        // X: forget it (shown on the row's hover, always clickable).
        Vector2 xp = new(at.X + width - pad - xw, at.Y + (h - xw) * 0.5f);
        ImGui.SetCursorScreenPos(xp);
        bool forget = ImGui.InvisibleButton(Id("##corpse_x", n), new Vector2(xw, xw));
        bool xhot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip("Forget this corpse (it's recovered, or not worth going back for).");
        if (hot || xhot) PanelButtonsWindow.DrawIcon(dl, RynthIcons.X, xp, new Vector2(xw, xw), Px(10), xhot ? _text : _faint);
        if (forget) _forget(row);
    }

    // ImGui ids with a number, made once each: no strings built every frame.
    private static readonly System.Collections.Generic.Dictionary<(string, int), string> Ids = new();

    private static string Id(string prefix, int n)
    {
        if (!Ids.TryGetValue((prefix, n), out string? id)) Ids[(prefix, n)] = id = prefix + n;
        return id;
    }

    /// <summary>Text cut with "..." to <paramref name="room"/> pixels.</summary>
    private static void Clip(ImDrawListPtr dl, ImFontPtr f, float size, Vector2 pos, uint color, string text, float room)
    {
        if (text.Length == 0 || room <= 4) return;
        float w = f.CalcTextSizeA(size, float.MaxValue, 0f, text).X;
        if (w > room)
        {
            int n = text.Length;
            while (n > 1 && f.CalcTextSizeA(size, float.MaxValue, 0f, text[..n] + "...").X > room) n--;
            text = text[..n] + "...";
        }
        dl.AddText(f, size, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), color, text);
    }

    /// <summary>A small arrow pointing <paramref name="degrees"/> from straight up (where the player faces), clockwise.</summary>
    private static void Arrow(ImDrawListPtr dl, Vector2 c, float r, float degrees, uint color)
    {
        float a = degrees * MathF.PI / 180f;
        Vector2 Dir(float t) => new(MathF.Sin(t), -MathF.Cos(t));
        Vector2 tip = c + Dir(a) * r;
        Vector2 left = c + Dir(a + 2.45f) * r * 0.85f, rightP = c + Dir(a - 2.45f) * r * 0.85f;
        dl.AddTriangleFilled(tip, left, rightP, color);
    }
}

// ============================================================================
//  RynthDefective - ShortcutBar/SelectionWindow.cs
//  What is selected, as the retail toolbar showed it (Tom 2026-10-22: "people
//  use it all the time to find things near them: doors, monsters"): its
//  picture and name, how far and which way it is (an arrow from where you
//  face, and the compass point), and the health bar of a creature or player.
//  USE uses it (opens a door, talks to an NPC). AC's own selection keys still
//  pick what is selected; this only shows it.
//
//    the name row  drag to move; Ctrl+wheel: bigger or smaller (30-250%);
//                  right-click: hide it (Settings > UI > Toolbar, /rdef selection).
//    the quantity  under a stack or a vendor's item (2026-10-30): how many, and
//                  Split / Give / Buy that many (SelectionWindow.Quantity.cs).
//
//  AC's render thread; reads the view the tick built (SelectionView).
// ============================================================================

using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthDefective.Quantity;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthDefective.ShortcutBar;

/// <summary>The selected object as drawn (built on the plugin tick).</summary>
internal sealed record SelectionView(
    bool Ready,
    uint Id,
    string Name,
    string Where,
    float Relative,      // degrees from where the player faces (0 ahead, + to the right); NaN = unknown
    float Health,        // 0-1, or -1 when it has none known
    uint Current,
    uint Max)
{
    public static readonly SelectionView NotReady = new(false, 0, string.Empty, string.Empty, float.NaN, -1f, 0, 0);

    /// <summary>The quantity box for a stack or a vendor's item; null = none.</summary>
    public QuantityView? Qty { get; init; }
}

internal sealed partial class SelectionWindow
{
    public const string Title = "Selection";

    private readonly RynthCoreHost _host;
    private readonly Action<uint> _use;
    private float _k = 1f, _r = 1f;

    public SelectionWindow(RynthCoreHost host, Action<uint> use)
    {
        _host = host;
        _use = use;
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

    private const float Pad = 6, Icon = 30, Gap = 6, Radius = 4, BarH = 6, UseW = 34, UseH = 16;

    private uint _bg, _key, _inset, _line, _text, _dim, _faint, _accent;
    private static readonly uint HealthColor = Argb(0xFFE0564E), HealthHigh = Argb(0xFF73D885), White = Argb(0xFFFFFFFF);
    private static uint A(uint c, float alpha) => WithAlpha(c, (uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));

    /// <summary>Draws the selection into the host's window; returns the height it needs (pixels). Render thread.</summary>
    public float Draw(SelectionView v)
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

        float pad = Px(Pad), icon = Px(Icon);
        float height = MathF.Ceiling(2 * pad + icon);
        // The quantity box under it (a stack, or a vendor's item): one background for both.
        QuantityView? qty = v.Ready && v.Id != 0 ? v.Qty : null;
        float qtyH = QuantityHeight(qty);
        Vector2 max = origin + new Vector2(width, height + qtyH);
        dl.AddRectFilled(origin, max, A(_bg, 0.94f), Px(Radius));
        dl.AddRect(origin, max, _line, Px(Radius));

        bool hasUse = v.Ready && v.Id != 0;
        float useW = hasUse ? Px(UseW) : 0f;

        // The drag surface: everything but USE.
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##sel_drag", new Vector2(Math.Max(1, width - useW - (hasUse ? pad : 0)), height));
        _host.PanelDragWithLastItem();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _host.SetPanelOpen(Title, RynthPanelOpen.Close);
        SafeImGui.SetItemTooltip(v.Id == 0
            ? "Nothing selected. Select with a click or the game's selection keys (- and = go through what's near you).\nDrag to move; Ctrl+wheel: bigger or smaller; right-click: hide."
            : $"{v.Name}{(v.Where.Length > 0 ? " - " + v.Where : "")}\nDrag to move; Ctrl+wheel: bigger or smaller; right-click: hide.");

        // The picture.
        Vector2 ip = origin + new Vector2(pad, pad);
        dl.AddRectFilled(ip, ip + new Vector2(icon, icon), v.Id == 0 ? _inset : _key, Px(3));
        IntPtr tex = v.Id != 0 ? _host.GetIconTexture(RynthIconKind.Object, v.Id) : IntPtr.Zero;
        if (tex != IntPtr.Zero)
            dl.AddImageRounded(tex, ip + new Vector2(Px(2), Px(2)), ip + new Vector2(icon - Px(2), icon - Px(2)), Vector2.Zero, Vector2.One, White, Px(2));
        dl.AddRect(ip, ip + new Vector2(icon, icon), _line, Px(3));

        float tx = ip.X + icon + Px(Gap);
        float right = origin.X + width - pad - (hasUse ? useW + Px(Gap) : 0f);
        ImFontPtr nf = Font(RynthUiFont.NarrowBold11), sf = Font(RynthUiFont.Narrow9);
        if (!v.Ready || v.Id == 0)
        {
            Clip(dl, nf, Fs(nf), new Vector2(tx, ip.Y + Px(1)), _faint, v.Ready ? "Nothing selected" : "Selection", right - tx);
            Clip(dl, sf, Fs(sf), new Vector2(tx, ip.Y + icon - Fs(sf)), _faint, "- and = go through what's near you", right - tx);
            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(new Vector2(width, height));
            return height;
        }

        // Name, then the health bar (a creature or player) and where it is.
        Clip(dl, nf, Fs(nf), new Vector2(tx, ip.Y), _text, v.Name, right - tx);
        float lineY = ip.Y + icon - Fs(sf);
        float x = tx;
        if (!float.IsNaN(v.Relative))
        {
            float s = Fs(sf);
            Arrow(dl, new Vector2(x + s * 0.5f, lineY + s * 0.5f), s * 0.5f, v.Relative, _accent);
            x += s + Px(3);
        }
        if (v.Health >= 0f)
        {
            float barY = ip.Y + Fs(nf) + Px(3);
            float barW = right - tx, barH = Px(BarH);
            Vector2 b0 = new(tx, barY), b1 = new(tx + barW, barY + barH);
            dl.AddRectFilled(b0, b1, _inset, Px(2));
            float f = Math.Clamp(v.Health, 0f, 1f);
            if (f > 0f) dl.AddRectFilled(b0, new Vector2(tx + barW * f, barY + barH), f > 0.6f ? A(HealthHigh, 0.9f) : HealthColor, Px(2));
            dl.AddRect(b0, b1, _line, Px(2));
            string pct = (v.Max > 0 ? $"{v.Current}/{v.Max}  " : "") + MathF.Round(f * 100f).ToString(CultureInfo.InvariantCulture) + "%";
            float pw = sf.CalcTextSizeA(Fs(sf), float.MaxValue, 0f, pct).X;
            dl.AddText(sf, Fs(sf), new Vector2(right - pw, lineY), _dim, pct);
            Clip(dl, sf, Fs(sf), new Vector2(x, lineY), _dim, v.Where, right - pw - Px(6) - x);
        }
        else Clip(dl, sf, Fs(sf), new Vector2(x, lineY), _dim, v.Where, right - x);

        // USE.
        Vector2 up = new(origin.X + width - pad - useW, origin.Y + (height - Px(UseH)) * 0.5f);
        ImGui.SetCursorScreenPos(up);
        bool use = ImGui.InvisibleButton("##sel_use", new Vector2(useW, Px(UseH)));
        bool hot = ImGui.IsItemHovered();
        SafeImGui.SetItemTooltip("Use it, as a double-click does (open a door, talk, pick up, use an item).");
        dl.AddRectFilled(up, up + new Vector2(useW, Px(UseH)), hot ? _key : A(_key, 0.6f), Px(3));
        dl.AddRect(up, up + new Vector2(useW, Px(UseH)), hot ? _accent : _line, Px(3));
        ImFontPtr uf = Font(RynthUiFont.Dash9);
        float uw = uf.CalcTextSizeA(Fs(uf), float.MaxValue, 0f, "USE").X;
        dl.AddText(uf, Fs(uf), new Vector2(MathF.Round(up.X + (useW - uw) * 0.5f), MathF.Round(up.Y + (Px(UseH) - Fs(uf)) * 0.5f)), hot ? _text : _dim, "USE");
        if (use) _use(v.Id);

        if (qty != null && qtyH > 0) DrawQuantity(dl, qty, new Vector2(origin.X, origin.Y + height), width);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height + qtyH));
        return height + qtyH;
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

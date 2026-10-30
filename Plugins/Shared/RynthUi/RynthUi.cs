// ============================================================================
//  RynthSuite - Plugins/Shared/RynthUi/RynthUi.cs
//  The engine's look for a plugin's own windows (RynthCore docs/PLUGIN_WINDOWS.md):
//  the player's colour theme and the engine's fonts (plugin API v89, with the
//  default theme's colours and ImGui's current font on older engines), and the
//  widgets the engine's own faces are made of (FaceKit's flat buttons, icon
//  buttons, labels, badges, vital bars), so a plugin's screens look like the
//  rest of RynthCore. Icons: RynthIcons (the ones baked into the atlas).
//
//  Call RynthUi.Init(Host) once in the plugin's Initialize. Everything else is
//  for drawing: AC's render thread, inside RynthPluginRenderEmbed. Colours are
//  ImGui's packed ABGR, read each frame (a theme change shows at once). Text
//  that isn't a literal goes through SafeImGui. Compiled into each plugin that
//  draws (linked source, like SafeImGui.cs and ImGuiNativeBinding.cs).
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.Shared.RynthUi;

internal static unsafe class RynthUi
{
    private static RynthCoreHost? _host;

    /// <summary>The plugin's host, once (Initialize).</summary>
    public static void Init(RynthCoreHost host) => _host = host;

    /// <summary>True when the engine hands out its theme and fonts (plugin API v89+).</summary>
    public static bool Themed => _host?.HasUiTheme == true;

    // ── Colours ──────────────────────────────────────────────────────────

    /// <summary>A theme colour (ABGR): the player's, or RynthCore's default theme on an older engine.</summary>
    public static uint Color(RynthUiColor token)
    {
        uint fallback = Fallback(token);
        return _host is RynthCoreHost h ? h.GetUiColor(token, fallback) : fallback;
    }

    public static uint Text => Color(RynthUiColor.Text);
    public static uint Mute => Color(RynthUiColor.TextMuted);
    /// <summary>The accent (teal by default): titles, the current item.</summary>
    public static uint Accent => Color(RynthUiColor.Accent);
    /// <summary>The highlight (amber by default): what to notice.</summary>
    public static uint Highlight => Color(RynthUiColor.Highlight);
    public static uint ButtonFill => Color(RynthUiColor.Button);
    public static uint Border => Color(RynthUiColor.Border);
    public static uint Field => Color(RynthUiColor.Field);
    public static uint Selected => Color(RynthUiColor.Selected);
    public static uint RowAlt => Color(RynthUiColor.RowAlt);
    /// <summary>A face's background (an edge-to-edge window paints it itself).</summary>
    public static uint Background => Color(RynthUiColor.Background);
    public static uint AccentFill => Color(RynthUiColor.AccentFill);
    public static uint Green => Color(RynthUiColor.Green);
    public static uint Red => Color(RynthUiColor.Red);

    /// <summary>ARGB (as the colour pickers write it) to ImGui's ABGR.</summary>
    public static uint Argb(uint argb) => (argb & 0xFF00FF00) | ((argb & 0xFF) << 16) | ((argb >> 16) & 0xFF);

    public static Vector4 Vec(uint abgr) => ImGui.ColorConvertU32ToFloat4(abgr);

    public static uint Lighten(uint abgr) => Scale(abgr, 1.25f, 12);
    public static uint Darken(uint abgr) => Scale(abgr, 0.8f, 0);
    /// <summary>Half as opaque (a disabled control's text).</summary>
    public static uint Faded(uint abgr) => (abgr & 0x00FFFFFF) | ((abgr >> 25) << 24);
    public static uint WithAlpha(uint abgr, uint alpha) => (abgr & 0x00FFFFFF) | (alpha << 24);

    private static uint Scale(uint abgr, float k, int add)
    {
        uint a = abgr & 0xFF000000;
        uint r = (uint)Math.Min(255, (int)((abgr & 0xFF) * k) + add);
        uint g = (uint)Math.Min(255, (int)(((abgr >> 8) & 0xFF) * k) + add);
        uint b = (uint)Math.Min(255, (int)(((abgr >> 16) & 0xFF) * k) + add);
        return a | (b << 16) | (g << 8) | r;
    }

    /// <summary>RynthCore's default theme (ThemePalette.Default), for engines before v89.</summary>
    internal static uint Fallback(RynthUiColor token) => Argb(token switch
    {
        RynthUiColor.Text => 0xFFF2F7FC,
        RynthUiColor.TextMuted => 0xFFC8D6E4,
        RynthUiColor.Accent => 0xFF26D9E6,
        RynthUiColor.Highlight => 0xFFE8B333,
        RynthUiColor.Button => 0xFF16283A,
        RynthUiColor.Border => 0xFF34587A,
        RynthUiColor.Field => 0xFF141F29,
        RynthUiColor.Selected => 0xFF1A2E42,
        RynthUiColor.RowAlt => 0xFF101822,
        RynthUiColor.Background => 0xFF0A0F14,
        RynthUiColor.PanelGlass => 0xF212121C,
        RynthUiColor.AccentFill => 0xFF264C59,
        RynthUiColor.BoxBg => 0xFF0A121A,
        RynthUiColor.FrameBg => 0xFF1A2230,
        RynthUiColor.FrameHover => 0xFF2A3850,
        RynthUiColor.FrameActive => 0xFF34466A,
        RynthUiColor.Green => 0xFF33CC66,
        RynthUiColor.Red => 0xFFE05A5A,
        RynthUiColor.TitleBar => 0xFF1A1A28,
        RynthUiColor.WindowBorder => 0xFF008080,
        // v90: the default theme's muted text and instrument shades (ThemePalette.DeriveInstrument).
        RynthUiColor.Muted => 0xFFB8C8D8,
        RynthUiColor.KeyFace => 0xFF111E2B,
        RynthUiColor.Hairline => 0xFF1D3042,
        RynthUiColor.Inset => 0xFF06080B,
        RynthUiColor.Faint => 0xFF8794A1,
        _ => 0xFFFFFFFF,
    });

    // ── Fonts ────────────────────────────────────────────────────────────

    /// <summary>The engine's font at the panel's text size, or ImGui's current font on an older engine.</summary>
    public static ImFontPtr Font(RynthUiFont font)
    {
        IntPtr p = _host?.GetUiFont(font) ?? IntPtr.Zero;
        return p != IntPtr.Zero ? new ImFontPtr((ImFont*)p) : ImGui.GetFont();
    }

    /// <summary>Pushes <paramref name="font"/> until the scope is disposed: <c>using (RynthUi.PushFont(RynthUiFont.UiBold11)) { ... }</c>.</summary>
    public static FontScope PushFont(RynthUiFont font)
    {
        ImGui.PushFont(Font(font));
        return new FontScope();
    }

    public readonly struct FontScope : IDisposable
    {
        public void Dispose() => ImGui.PopFont();
    }

    // ── Widgets (FaceKit's, the engine's faces) ──────────────────────────

    /// <summary>Coloured text at the cursor.</summary>
    public static void Label(string text, uint color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>A flat bordered button at <paramref name="pos"/> (screen pixels); true when clicked.</summary>
    public static bool Button(string id, string label, Vector2 pos, Vector2 size, uint fg, uint bg,
        bool enabled = true, bool leftAlign = false, uint border = 0)
    {
        bool clicked = ButtonFrame(id, pos, size, bg, enabled, border);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 ts = ImGui.CalcTextSize(label);
        float tx = leftAlign ? pos.X + 6 : pos.X + (size.X - ts.X) * 0.5f;
        dl.PushClipRect(pos, pos + size, true);
        dl.AddText(new Vector2(tx, pos.Y + (size.Y - ts.Y) * 0.5f), enabled ? fg : Faded(fg), label);
        dl.PopClipRect();
        return clicked;
    }

    /// <summary>The button's look with an icon (RynthIcons) centred: Ui14 for 20+ px buttons, Ui11 smaller.</summary>
    public static bool IconButton(string id, string icon, Vector2 pos, Vector2 size, uint fg, uint bg,
        bool enabled = true, uint border = 0, RynthUiFont font = RynthUiFont.Ui14)
    {
        bool clicked = ButtonFrame(id, pos, size, bg, enabled, border);
        DrawCentered(ImGui.GetWindowDrawList(), Font(font), icon, pos, size, enabled ? fg : Faded(fg));
        return clicked;
    }

    /// <summary>Width a button needs for <paramref name="label"/> in the current font.</summary>
    public static float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + 16;

    private static bool ButtonFrame(string id, Vector2 pos, Vector2 size, uint bg, bool enabled, uint border)
    {
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, size) && enabled;
        bool hot = enabled && ImGui.IsItemHovered();
        uint fill = enabled && ImGui.IsItemActive() ? Darken(bg) : hot ? Lighten(bg) : bg;
        dl.AddRectFilled(pos, pos + size, fill, 3);
        dl.AddRect(pos, pos + size, border != 0 ? border : Border, 3);
        return clicked;
    }

    /// <summary>A small rounded count ("12", "99+") at <paramref name="p"/>, <paramref name="h"/> tall.</summary>
    public static float Badge(ImDrawListPtr dl, Vector2 p, float h, string text, uint fill, uint fg)
    {
        ImFontPtr f = Font(RynthUiFont.Ui9);
        float tw = f.CalcTextSizeA(f.FontSize, float.MaxValue, 0, text).X;
        float w = tw + 8;
        dl.AddRectFilled(p, p + new Vector2(w, h), fill, h * 0.5f);
        dl.AddText(f, f.FontSize, p + new Vector2(4, (h - f.FontSize) * 0.5f), fg, text);
        return w;
    }

    /// <summary>
    /// A vital bar: <paramref name="fraction"/> (0-1) of <paramref name="size"/> filled, with
    /// <paramref name="text"/> ("300/400") centred in Ui9 when there is room.
    /// </summary>
    public static void VitalBar(ImDrawListPtr dl, Vector2 p, Vector2 size, float fraction, uint fill, uint bg, string? text = null)
    {
        fraction = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0f;
        dl.AddRectFilled(p, p + size, bg, 2);
        if (fraction > 0) dl.AddRectFilled(p, p + new Vector2(size.X * fraction, size.Y), fill, 2);
        if (string.IsNullOrEmpty(text)) return;
        ImFontPtr f = Font(RynthUiFont.Ui9);
        Vector2 ts = f.CalcTextSizeA(f.FontSize, float.MaxValue, 0, text);
        if (ts.X + 4 > size.X || f.FontSize > size.Y + 2) return;
        dl.AddText(f, f.FontSize, p + (size - ts) * 0.5f, Text, text);
    }

    /// <summary>
    /// An icon (one glyph) centred in the box at <paramref name="min"/>, by the glyph's own bounds
    /// (the engine's PhosphorIcons.DrawCentered), at the font's size.
    /// </summary>
    public static void DrawCentered(ImDrawListPtr dl, ImFontPtr font, string glyph, Vector2 min, Vector2 size, uint color)
    {
        if (string.IsNullOrEmpty(glyph)) return;
        float px = font.FontSize;
        if (px <= 0) return;
        Vector2 centre;
        // ImFontGlyph (1.91): a 4-byte bitfield, AdvanceX, then X0 Y0 X1 Y1 (read by offset).
        byte* g = font.NativePtr == null ? null : (byte*)ImGuiNative.ImFont_FindGlyphNoFallback(font.NativePtr, glyph[0]);
        if (g != null && glyph.Length == 1)
        {
            float* box = (float*)(g + 8);
            centre = new Vector2((box[0] + box[2]) * 0.5f, (box[1] + box[3]) * 0.5f);
        }
        else
        {
            centre = font.CalcTextSizeA(px, float.MaxValue, 0f, glyph) * 0.5f;
        }
        Vector2 at = min + size * 0.5f - centre;
        dl.AddText(font, px, new Vector2(MathF.Round(at.X), MathF.Round(at.Y)), color, glyph);
    }
}

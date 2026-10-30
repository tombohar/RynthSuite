// ============================================================================
//  RynthDefective - ShortcutBar/PanelButtonsWindow.cs
//  Tiny buttons for AC's own panels, in the bottom-right corner (Tom
//  2026-10-22: with the retail toolbar hidden, its panel buttons go with it).
//  RynthCore hosts the frameless window (sections manifest "corner":
//  "bottomRight", plugin API v90) and opens and closes the panels the way AC's
//  toolbar and keys do (RynthCoreHost.AcPanel): one main panel at a time.
//
//    grip     the dots on the left: drag the strip; Ctrl+wheel: bigger or
//             smaller; right-click: hide it (/rdef panels shows it again).
//    buttons  the pack, attributes & skills, the spellbook, fellowship &
//             allegiance, the map, quests, spells on you, character info,
//             options. Lit while their panel is open. They fill the window's
//             width and wrap: drag it narrow for a column.
//
//  AC's render thread; the panel calls are queued by the engine.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthDefective.ShortcutBar;

internal sealed class PanelButtonsWindow
{
    public const string Title = "Game Panels";

    private readonly RynthCoreHost _host;
    private float _k = 1f, _r = 1f;

    public PanelButtonsWindow(RynthCoreHost host) => _host = host;

    private readonly record struct Button(RynthAcPanel Panel, string Icon, string Tip);

    private static readonly Button[] Buttons =
    [
        new(RynthAcPanel.Inventory, RynthIcons.Backpack, "Pack"),
        new(RynthAcPanel.Character, RynthIcons.User, "Attributes and skills"),
        new(RynthAcPanel.Spellbook, RynthIcons.MagicWand, "Spellbook"),
        new(RynthAcPanel.Social, RynthIcons.UsersThree, "Fellowship, allegiance and friends"),
        new(RynthAcPanel.Map, RynthIcons.MapTrifold, "Map"),
        new(RynthAcPanel.Quests, RynthIcons.Scroll, "Contracts and journal"),
        new(RynthAcPanel.HelpfulSpells, RynthIcons.Sparkle, "Spells on you"),
        new(RynthAcPanel.CharacterInfo, RynthIcons.Info, "Character information"),
        new(RynthAcPanel.Options, RynthIcons.Gear, "Options"),
    ];

    private const float Pad = 4, Cell = 22, Gap = 2, GripW = 9, Radius = 4;

    private float Px(float v) => v * _k;

    /// <summary>
    /// The layout's scale and the text's against the baked font: the window's own size (Settings > UI >
    /// Toolbar, Ctrl+wheel: 30-250%, PanelScale) or, unset, the text size the engine baked for it.
    /// </summary>
    private void Scale()
    {
        float fontK = Math.Max(0.5f, Font(RynthUiFont.Ui10).FontSize / 10f);
        int pct = _host.PanelScale(Title);
        _k = pct > 0 ? pct / 100f : fontK;
        _r = _k / fontK;
    }

    private uint _bg, _key, _line, _text, _faint, _accent;
    private static uint A(uint c, float alpha) => WithAlpha(c, (uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));

    /// <summary>Draws the strip into the host's window; returns the height it needs (pixels). Render thread.</summary>
    public float Draw()
    {
        Scale();
        _bg = Color(RynthUiColor.Background);
        _key = Color(RynthUiColor.KeyFace);
        _line = Color(RynthUiColor.Hairline);
        _text = Color(RynthUiColor.Text);
        _faint = Color(RynthUiColor.Faint);
        _accent = Color(RynthUiColor.Accent);

        Vector2 origin = ImGui.GetCursorScreenPos();
        origin = new Vector2(MathF.Round(origin.X), MathF.Round(origin.Y));
        float width = MathF.Floor(ImGui.GetContentRegionAvail().X);
        if (width < 20) return Px(Cell + 2 * Pad);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();

        float cell = Px(Cell), gap = Px(Gap), pad = Px(Pad), grip = Px(GripW);
        float room = width - 2 * pad - grip - gap;
        int perRow = Math.Max(1, (int)MathF.Floor((room + gap) / (cell + gap)));
        int rows = (Buttons.Length + perRow - 1) / perRow;
        float height = MathF.Ceiling(2 * pad + rows * cell + (rows - 1) * gap);

        // The frame, as the shortcut bar's.
        Vector2 max = origin + new Vector2(width, height);
        dl.AddRectFilled(origin, max, A(_bg, 0.94f), Px(Radius));
        dl.AddRect(origin, max, _line, Px(Radius));

        // The grip: drag, Ctrl+wheel, right-click to hide.
        Vector2 gp = origin + new Vector2(pad, pad);
        ImGui.SetCursorScreenPos(gp);
        ImGui.InvisibleButton("##gp_grip", new Vector2(grip, height - 2 * pad));
        _host.PanelDragWithLastItem();
        bool gripHot = ImGui.IsItemHovered();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _host.SetPanelOpen(Title, RynthPanelOpen.Close);
        SafeImGui.SetItemTooltip("Drag to move. Ctrl+wheel: bigger or smaller. Right-click: hide (/rdef panels shows it again).\nDrag the window's edge narrower for a column.");
        DrawIcon(dl, RynthIcons.DotsSixVertical, gp, new Vector2(grip, height - 2 * pad), Px(11), gripHot ? _text : _faint);

        float x0 = gp.X + grip + gap;
        for (int i = 0; i < Buttons.Length; i++)
        {
            int col = i % perRow, row = i / perRow;
            Vector2 p = new(MathF.Round(x0 + col * (cell + gap)), MathF.Round(origin.Y + pad + row * (cell + gap)));
            DrawButton(dl, Buttons[i], p, cell);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        return height;
    }

    private void DrawButton(ImDrawListPtr dl, in Button b, Vector2 p, float cell)
    {
        Vector2 size = new(cell, cell);
        ImGui.SetCursorScreenPos(p);
        bool clicked = ImGui.InvisibleButton("##gp_" + (int)b.Panel, size);
        bool hot = ImGui.IsItemHovered();
        bool? open = _host.IsAcPanelOpen(b.Panel);
        SafeImGui.SetItemTooltip(open == null ? b.Tip + " (not in this client)" : b.Tip + (open == true ? " - open: click to close" : ""));
        float r = Px(3);
        if (hot || open == true) dl.AddRectFilled(p, p + size, _key, r);
        if (open == true) dl.AddLine(new Vector2(p.X + Px(4), p.Y + cell - Px(1.5f)), new Vector2(p.X + cell - Px(4), p.Y + cell - Px(1.5f)), _accent, Px(1.5f));
        uint color = open == null ? A(_faint, 0.5f) : open == true ? _accent : hot ? _text : A(_text, 0.72f);
        DrawIcon(dl, b.Icon, p, size, Px(14), color);
        if (clicked && open != null) _host.AcPanel(b.Panel, RynthAcPanelOp.Toggle);
    }

    /// <summary>A Phosphor glyph centred in the box, <paramref name="px"/> across its em (from Ui18's bake).</summary>
    internal static unsafe void DrawIcon(ImDrawListPtr dl, string glyph, Vector2 min, Vector2 size, float px, uint color)
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
}

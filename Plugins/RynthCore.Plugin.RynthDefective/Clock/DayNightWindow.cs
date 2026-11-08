// ============================================================================
//  RynthDefective - Clock/DayNightWindow.cs
//  The Day/Night timer (2026-11-04, a request on 2026-11-01: a tiny window with
//  the real minutes until the next day/night change): a sun or a moon, then
//  "Night in 23m" / "Day in 4m 10s", and the hour's name when there's room.
//  "—" until the player is in world (the clock can't be read before).
//  Plugins/Shared/DerethClock.cs has the math; RynthCore hosts the frameless
//  window (plugin API v90) and remembers where it is.
//
//    anywhere   drag to move; Ctrl+wheel: bigger or smaller (30-250%);
//               right-click: hide it (/rdef daynight, the bar button, or
//               Settings > UI > Toolbar show it again).
//
//  AC's render thread; reads the view the tick built (DayNightView) and counts
//  the seconds down from when it was read, so the seconds move every frame.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.RynthDefective.ShortcutBar;
using RynthCore.Plugin.Shared;
using RynthCore.Plugin.Shared.RynthUi;
using RynthCore.PluginSdk;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthDefective.Clock;

/// <summary>The clock as the tick read it; Ready = in world with a usable reading.</summary>
internal sealed record DayNightView(bool Ready, DerethNow Now, long ReadAtMs)
{
    public static readonly DayNightView NotReady = new(false, default, 0);

    /// <summary>The real seconds to the change at <paramref name="nowMs"/> (counted down from the reading).</summary>
    public double SecondsLeft(long nowMs) => Math.Max(0, Now.SecondsToChange - (nowMs - ReadAtMs) / 1000.0);
}

internal sealed class DayNightWindow
{
    public const string Title = "Day/Night";
    public const string NotInWorld = "—";

    private readonly RynthCoreHost _host;
    private float _k = 1f, _r = 1f;

    // The label, made again only when the second it shows changes (no string every frame).
    private long _shownSecond = -1;
    private bool _shownDay;
    private string _label = NotInWorld;

    public DayNightWindow(RynthCoreHost host) => _host = host;

    private float Px(float v) => v * _k;
    private float Fs(ImFontPtr f) => f.FontSize * _r;

    private void Scale()
    {
        float fontK = Math.Max(0.5f, Font(RynthUiFont.Ui10).FontSize / 10f);
        int pct = _host.PanelScale(Title);
        _k = pct > 0 ? pct / 100f : fontK;
        _r = _k / fontK;
    }

    private const float Pad = 5, RowH = 16, Mark = 14, Gap = 4, Radius = 4;

    private static readonly uint SunColor = Argb(0xFFF2C14E), MoonColor = Argb(0xFFB8C7F0);
    private static uint A(uint c, float alpha) => WithAlpha(c, (uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));

    /// <summary>Draws the timer into the host's window; returns the height it needs (pixels). Render thread.</summary>
    public float Draw(DayNightView v)
    {
        Scale();
        uint bg = Color(RynthUiColor.Background), line = Color(RynthUiColor.Hairline);
        uint text = Color(RynthUiColor.Text), faint = Color(RynthUiColor.Faint);

        Vector2 origin = ImGui.GetCursorScreenPos();
        origin = new Vector2(MathF.Round(origin.X), MathF.Round(origin.Y));
        float width = MathF.Floor(ImGui.GetContentRegionAvail().X);
        float pad = Px(Pad), rowH = Px(RowH);
        float height = MathF.Ceiling(2 * pad + rowH);
        if (width < 30) return height;
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        Vector2 max = origin + new Vector2(width, height);
        dl.AddRectFilled(origin, max, A(bg, 0.94f), Px(Radius));
        dl.AddRect(origin, max, line, Px(Radius));

        // The whole window is the drag surface.
        ImGui.SetCursorScreenPos(origin);
        ImGui.InvisibleButton("##daynight_drag", new Vector2(width, height));
        _host.PanelDragWithLastItem();
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) _host.SetPanelOpen(Title, RynthPanelOpen.Close);
        bool hot = ImGui.IsItemHovered();

        long nowMs = Environment.TickCount64;
        double left = v.Ready ? v.SecondsLeft(nowMs) : 0;
        string label = Label(v, left);

        // The sun by day, the moon at night (an engine from before the moon was baked: a star).
        Vector2 at = origin + new Vector2(pad, pad);
        string icon = !v.Ready || v.Now.IsDay ? RynthIcons.Sun : HasGlyph(RynthIcons.Moon) ? RynthIcons.Moon : RynthIcons.Star;
        uint iconColor = !v.Ready ? faint : v.Now.IsDay ? SunColor : MoonColor;
        PanelButtonsWindow.DrawIcon(dl, icon, at, new Vector2(Px(Mark), rowH), Px(13), iconColor);

        ImFontPtr nf = Font(RynthUiFont.NarrowBold11), sf = Font(RynthUiFont.Narrow9);
        float tx = at.X + Px(Mark) + Px(Gap), right = origin.X + width - pad;
        float lw = nf.CalcTextSizeA(Fs(nf), float.MaxValue, 0f, label).X;
        dl.AddText(nf, Fs(nf), new Vector2(MathF.Round(tx), MathF.Round(at.Y + (rowH - Fs(nf)) * 0.5f)), v.Ready ? text : faint, label);

        // The hour's name, right-aligned, only when it fits whole.
        if (v.Ready)
        {
            string hour = v.Now.HourName;
            float hw = sf.CalcTextSizeA(Fs(sf), float.MaxValue, 0f, hour).X;
            if (tx + lw + Px(8) + hw <= right)
                dl.AddText(sf, Fs(sf), new Vector2(MathF.Round(right - hw), MathF.Round(at.Y + (rowH - Fs(sf)) * 0.5f)), faint, hour);
        }

        if (hot) SafeImGui.SetItemTooltip(Tip(v, left));

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        return height;
    }

    private string Label(DayNightView v, double left)
    {
        if (!v.Ready) { _shownSecond = -1; return _label = NotInWorld; }
        long second = (long)Math.Ceiling(left);
        if (second != _shownSecond || v.Now.IsDay != _shownDay)
        {
            _shownSecond = second;
            _shownDay = v.Now.IsDay;
            _label = DerethClock.Label(v.Now, left);
        }
        return _label;
    }

    /// <summary>The tooltip (built only while hovered).</summary>
    internal static string Tip(DayNightView v, double left)
    {
        const string help = "A Dereth day is 2 h 7 min of real time: 63.5 minutes of day, then 63.5 of night.\n"
                            + "Drag to move; Ctrl+wheel: bigger or smaller; right-click: hide (/rdef daynight).";
        if (!v.Ready) return "Day or night in Dereth, and the real minutes until it changes. Shown once you're in the world.\n" + help;
        TimeSpan t = TimeSpan.FromSeconds(Math.Ceiling(left));
        string until = t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{t.Seconds} s";
        return $"{(v.Now.IsDay ? "Day" : "Night")} now ({v.Now.HourName}). {v.Now.Next} comes at {v.Now.NextHourName}, in {until} of real time.\n" + help;
    }

    /// <summary>Whether the engine's atlas has <paramref name="glyph"/> (an icon baked after this engine was built isn't).</summary>
    private static unsafe bool HasGlyph(string glyph)
    {
        ImFontPtr font = Font(RynthUiFont.Ui18);
        return font.NativePtr != null && ImGuiNative.ImFont_FindGlyphNoFallback(font.NativePtr, glyph[0]) != null;
    }
}

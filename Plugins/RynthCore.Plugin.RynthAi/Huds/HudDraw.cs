// HudDraw.cs — Small ImGui drawing helpers shared by the HUD windows (render thread only).
using System;
using System.Numerics;
using ImGuiNET;

namespace RynthCore.Plugin.RynthAi.Huds;

internal static class HudDraw
{
    private static readonly Vector4 Opaque = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 Faded = new(1f, 1f, 1f, 0.35f);
    private static readonly Vector4 NoBorder = Vector4.Zero;

    /// <summary>
    /// Draws an item icon of <paramref name="size"/> pixels. While the texture is still
    /// loading (or the item has no icon) a framed box with the name's first letter stands in.
    /// <paramref name="dim"/> fades it (e.g. none carried).
    /// </summary>
    public static void Icon(HudIconCache icons, uint iconDid, string name, float size, bool dim)
    {
        IntPtr tex = icons.GetTexture(iconDid);
        var sz = new Vector2(size, size);
        if (tex != IntPtr.Zero)
        {
            ImGui.Image(tex, sz, Vector2.Zero, Vector2.One, dim ? Faded : Opaque, NoBorder);
            return;
        }
        Placeholder(name, sz, dim);
    }

    /// <summary>Framed stand-in with the item's first letter, same footprint as an icon.</summary>
    public static void Placeholder(string name, Vector2 size, bool dim)
    {
        Vector2 p = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var dl = ImGui.GetWindowDrawList();
        uint frame = ImGui.GetColorU32(new Vector4(0.45f, 0.55f, 0.65f, dim ? 0.35f : 0.8f));
        uint fill = ImGui.GetColorU32(new Vector4(0.12f, 0.16f, 0.22f, dim ? 0.35f : 0.8f));
        dl.AddRectFilled(p, p + size, fill, 3f);
        dl.AddRect(p, p + size, frame, 3f);
        if (string.IsNullOrEmpty(name)) return;
        string letter = name.Substring(0, 1).ToUpperInvariant();
        Vector2 ts = ImGui.CalcTextSize(letter);
        dl.AddText(p + (size - ts) * 0.5f, ImGui.GetColorU32(new Vector4(0.9f, 0.9f, 0.9f, dim ? 0.4f : 1f)), letter);
    }

    /// <summary>"1468", "12.3k", "4.5m", "1.2b" — compact counts for tight HUD rows.</summary>
    public static string Compact(double v)
    {
        double a = Math.Abs(v);
        if (a >= 1e9) return (v / 1e9).ToString("0.0") + "b";
        if (a >= 1e6) return (v / 1e6).ToString("0.0") + "m";
        if (a >= 1e4) return (v / 1e3).ToString("0.0") + "k";
        return v.ToString("0");
    }
}

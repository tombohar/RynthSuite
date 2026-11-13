// ============================================================================
//  RynthAi - Dashboard/IconKit.cs
//  What the dashboard's drawn key icons share (BuffHalo, CombatCrossed,
//  NavTrail): the clock they animate by, directions, alpha and mix, the soft
//  glow and the four-point star. Render thread; allocates nothing.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Dashboard;

internal static class IconKit
{
    /// <summary>The icons are drawn on a 15 px em (the keys' icon size) and scaled to the em asked for.</summary>
    internal const float DesignEm = 15f;

    private const int CurveSteps = 6;
    /// <summary>The points <see cref="Star"/> writes (4 sides).</summary>
    internal const int StarPoints = 4 * CurveSteps;

    /// <summary>ImGui's clock in seconds, wrapped so the float keeps its precision in a long session.</summary>
    internal static float Clock() => (float)(ImGui.GetTime() % 3600.0);

    internal static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    /// <summary>(u, v) turned to angle <paramref name="a"/>: u runs along the angle, v across it.</summary>
    internal static Vector2 Turn(float u, float v, float a)
    {
        float c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(u * c - v * s, u * s + v * c);
    }

    internal static uint Alpha(uint c, float alpha) => WithAlpha(c, (uint)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f));

    /// <summary>ABGR mix: <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>, <paramref name="a"/>'s alpha.</summary>
    internal static uint Mix(uint a, uint b, float t)
    {
        static uint Ch(uint c, int s) => (c >> s) & 0xFF;
        uint r = (uint)MathF.Round(Ch(a, 0) + (Ch(b, 0) - (float)Ch(a, 0)) * t);
        uint g = (uint)MathF.Round(Ch(a, 8) + (Ch(b, 8) - (float)Ch(a, 8)) * t);
        uint bl = (uint)MathF.Round(Ch(a, 16) + (Ch(b, 16) - (float)Ch(a, 16)) * t);
        return (a & 0xFF000000) | (bl << 16) | (g << 8) | r;
    }

    /// <summary>A soft glow: four discs of <paramref name="color"/> at <paramref name="alpha"/>, each a fifth smaller.</summary>
    internal static void Glow(ImDrawListPtr dl, Vector2 c, float r, uint color, float alpha)
    {
        uint g = Alpha(color, alpha);
        for (int i = 0; i < 4; i++) dl.AddCircleFilled(c, r * (1f - i * 0.2f), g, 24);
    }

    /// <summary>
    /// A four-point star, tip up, with concave sides (one quadratic curve each, its control point
    /// <paramref name="waist"/> from the centre). Writes <see cref="StarPoints"/> points; returns the count.
    /// </summary>
    internal static int Star(Span<Vector2> pts, Vector2 c, float r, float waist)
    {
        int n = 0;
        for (int i = 0; i < 4; i++)
        {
            float a = -MathF.PI / 2 + i * (MathF.PI / 2);
            Vector2 p0 = c + Dir(a) * r;
            Vector2 cp = c + Dir(a + MathF.PI / 4) * waist;
            Vector2 p1 = c + Dir(a + MathF.PI / 2) * r;
            // p0 and the curve up to p1; p1 is the next side's first point.
            for (int k = 0; k < CurveSteps; k++)
            {
                float u = k / (float)CurveSteps, v = 1f - u;
                pts[n++] = v * v * p0 + 2f * v * u * cp + u * u * p1;
            }
        }
        return n;
    }

    /// <summary>A filled star (see <see cref="Star"/>).</summary>
    internal static void FillStar(ImDrawListPtr dl, Vector2 c, float r, float waist, uint color)
    {
        Span<Vector2> pts = stackalloc Vector2[StarPoints];
        int n = Star(pts, c, r, waist);
        dl.AddConcavePolyFilled(ref pts[0], n, color);
    }
}

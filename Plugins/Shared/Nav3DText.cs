// ============================================================================
//  RynthSuite - Plugins/Shared/Nav3DText.cs
//  Text in the world on the engine's Nav3D buffer: a small stroke font drawn as
//  filled strokes (Nav3D triangles) on an upright plane turned toward the
//  character. RynthAi's waypoint labels (NavOverlay) and RynthNav's path
//  markers both draw with it. Compiled into each plugin (linked source); no
//  host calls, so the offline tests compile it too.
//
//  Frame: the Nav3D one (X = east metres, Y = height, Z = north metres, in the
//  player's landblock). Call only from the plugin tick (the Nav3D submit side).
// ============================================================================

using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.Shared;

/// <summary>Where Nav3D geometry goes: the engine's buffer in the game, a recorder in the tests.</summary>
internal interface INav3DSink
{
    void Line(float x1, float y1, float z1, float x2, float y2, float z2, float thickness, uint colorArgb);
    void Triangle(float x1, float y1, float z1, float x2, float y2, float z2, float x3, float y3, float z3, uint colorArgb);
}

/// <summary>
/// One line of text over a point in the world, as filled strokes facing the viewer, sized up with
/// distance so far text stays readable. Rows stack upward from just above the point.
/// </summary>
internal static class Nav3DText
{
    /// <summary>Row 0's baseline above the point (m).</summary>
    public const float LabelLift = 1.1f;
    public const float MinLineHeight = 0.30f, MaxLineHeight = 1.5f, HeightPerMetre = 0.015f;

    /// <summary>The line height (m) for text this far from the viewer.</summary>
    public static float LineHeight(float distance) =>
        Math.Clamp(MinLineHeight + HeightPerMetre * distance, MinLineHeight, MaxLineHeight);

    /// <summary>
    /// Emits <paramref name="text"/> over (x, y, z), row <paramref name="row"/>, facing the viewer at
    /// (viewX, viewZ). Counts triangles in <paramref name="tris"/>; returns false (having drawn what
    /// fitted) when the next stroke would go past <paramref name="maxTris"/>.
    /// </summary>
    public static bool Emit(float x, float y, float z, string text, int row, uint color,
        float viewX, float viewZ, INav3DSink sink, ref int tris, int maxTris)
    {
        float dx = x - viewX, dz = z - viewZ;
        float d = MathF.Sqrt(dx * dx + dz * dz);
        // Text faces the viewer: its right-hand direction is the view direction turned 90° clockwise.
        float fx = 0f, fz = 1f;
        if (d > 0.01f) { fx = dx / d; fz = dz / d; }
        float rx = fz, rz = -fx;

        float h = LineHeight(d);
        float s = h / NavStrokeFont.Height;
        float width = NavStrokeFont.Width(text) * s;
        float u0 = -width * 0.5f;
        float v0 = y + LabelLift + row * h * 1.5f;
        float half = NavStrokeFont.Stroke * 0.5f * s;

        for (int c = 0; c < text.Length; c++)
        {
            float[][]? strokes = NavStrokeFont.Glyph(text[c]);
            if (strokes == null) continue;
            float cu = u0 + c * NavStrokeFont.Advance * s;
            foreach (float[] poly in strokes)
            {
                for (int k = 0; k + 3 < poly.Length; k += 2)
                {
                    if (tris + 2 > maxTris) return false;
                    float au = cu + poly[k] * s, av = v0 + poly[k + 1] * s;
                    float bu = cu + poly[k + 2] * s, bv = v0 + poly[k + 3] * s;
                    Quad(sink, x, z, color, rx, rz, au, av, bu, bv, half);
                    tris += 2;
                }
            }
        }
        return true;
    }

    // A stroke a→b in the text plane (u along the text, v up) as two triangles, its ends pushed
    // out half a stroke so corners close.
    private static void Quad(INav3DSink sink, float x, float z, uint color, float rx, float rz,
        float au, float av, float bu, float bv, float half)
    {
        float eu = bu - au, ev = bv - av;
        float len = MathF.Sqrt(eu * eu + ev * ev);
        if (len < 1e-5f) { eu = 0f; ev = 1f; }
        else { eu /= len; ev /= len; }
        au -= eu * half; av -= ev * half;
        bu += eu * half; bv += ev * half;
        float nu = -ev * half, nv = eu * half;

        // (u, v) → world: u runs along the right-hand direction, v is height.
        float a1x = x + rx * (au + nu), a1z = z + rz * (au + nu), a1y = av + nv;
        float a2x = x + rx * (au - nu), a2z = z + rz * (au - nu), a2y = av - nv;
        float b1x = x + rx * (bu + nu), b1z = z + rz * (bu + nu), b1y = bv + nv;
        float b2x = x + rx * (bu - nu), b2z = z + rz * (bu - nu), b2y = bv - nv;
        sink.Triangle(a1x, a1y, a1z, a2x, a2y, a2z, b1x, b1y, b1z, color);
        sink.Triangle(a2x, a2y, a2z, b2x, b2y, b2z, b1x, b1y, b1z, color);
    }
}

/// <summary>
/// A small stroke font for labels in the world: digits, A-Z and a few marks, each a set of
/// polylines on a 4 x 6 grid (y up). Lower case draws as upper case; anything else is a gap.
/// </summary>
internal static class NavStrokeFont
{
    public const float Height = 6f, Advance = 5.5f, Stroke = 0.75f;

    /// <summary>The text's drawn width in grid units.</summary>
    public static float Width(string text) => text.Length == 0 ? 0f : text.Length * Advance - (Advance - 4f);

    public static float[][]? Glyph(char c)
    {
        c = char.ToUpperInvariant(c);
        return Glyphs.TryGetValue(c, out float[][]? g) ? g : null;
    }

    private static float[] P(params float[] xy) => xy;

    private static readonly Dictionary<char, float[][]> Glyphs = new()
    {
        ['0'] = new[] { P(0, 0, 4, 0, 4, 6, 0, 6, 0, 0), P(0, 0, 4, 6) },
        ['1'] = new[] { P(1, 5, 2, 6, 2, 0), P(1, 0, 3, 0) },
        ['2'] = new[] { P(0, 6, 4, 6, 4, 3, 0, 3, 0, 0, 4, 0) },
        ['3'] = new[] { P(0, 6, 4, 6, 4, 0, 0, 0), P(1, 3, 4, 3) },
        ['4'] = new[] { P(0, 6, 0, 3, 4, 3), P(4, 6, 4, 0) },
        ['5'] = new[] { P(4, 6, 0, 6, 0, 4, 3, 4, 4, 3, 4, 1, 3, 0, 0, 0) },
        ['6'] = new[] { P(4, 6, 0, 6, 0, 0, 4, 0, 4, 3, 0, 3) },
        ['7'] = new[] { P(0, 6, 4, 6, 1, 0) },
        ['8'] = new[] { P(0, 0, 4, 0, 4, 6, 0, 6, 0, 0), P(0, 3, 4, 3) },
        ['9'] = new[] { P(4, 3, 0, 3, 0, 6, 4, 6, 4, 0, 0, 0) },
        ['A'] = new[] { P(0, 0, 0, 4, 2, 6, 4, 4, 4, 0), P(0, 3, 4, 3) },
        ['B'] = new[] { P(0, 0, 0, 6, 3, 6, 4, 5, 4, 4, 3, 3, 0, 3), P(3, 3, 4, 2, 4, 1, 3, 0, 0, 0) },
        ['C'] = new[] { P(4, 6, 0, 6, 0, 0, 4, 0) },
        ['D'] = new[] { P(0, 0, 0, 6, 2, 6, 4, 4, 4, 2, 2, 0, 0, 0) },
        ['E'] = new[] { P(4, 6, 0, 6, 0, 0, 4, 0), P(0, 3, 3, 3) },
        ['F'] = new[] { P(4, 6, 0, 6, 0, 0), P(0, 3, 3, 3) },
        ['G'] = new[] { P(4, 6, 0, 6, 0, 0, 4, 0, 4, 3, 2, 3) },
        ['H'] = new[] { P(0, 0, 0, 6), P(4, 0, 4, 6), P(0, 3, 4, 3) },
        ['I'] = new[] { P(1, 6, 3, 6), P(2, 6, 2, 0), P(1, 0, 3, 0) },
        ['J'] = new[] { P(4, 6, 4, 0, 0, 0, 0, 2) },
        ['K'] = new[] { P(0, 0, 0, 6), P(4, 6, 0, 3, 4, 0) },
        ['L'] = new[] { P(0, 6, 0, 0, 4, 0) },
        ['M'] = new[] { P(0, 0, 0, 6, 2, 3, 4, 6, 4, 0) },
        ['N'] = new[] { P(0, 0, 0, 6, 4, 0, 4, 6) },
        ['O'] = new[] { P(0, 0, 4, 0, 4, 6, 0, 6, 0, 0) },
        ['P'] = new[] { P(0, 0, 0, 6, 4, 6, 4, 3, 0, 3) },
        ['Q'] = new[] { P(0, 0, 4, 0, 4, 6, 0, 6, 0, 0), P(2, 2, 4, -1) },
        ['R'] = new[] { P(0, 0, 0, 6, 4, 6, 4, 3, 0, 3, 4, 0) },
        ['S'] = new[] { P(4, 6, 0, 6, 0, 3, 4, 3, 4, 0, 0, 0) },
        ['T'] = new[] { P(0, 6, 4, 6), P(2, 6, 2, 0) },
        ['U'] = new[] { P(0, 6, 0, 0, 4, 0, 4, 6) },
        ['V'] = new[] { P(0, 6, 2, 0, 4, 6) },
        ['W'] = new[] { P(0, 6, 1, 0, 2, 3, 3, 0, 4, 6) },
        ['X'] = new[] { P(0, 0, 4, 6), P(0, 6, 4, 0) },
        ['Y'] = new[] { P(0, 6, 2, 3, 4, 6), P(2, 3, 2, 0) },
        ['Z'] = new[] { P(0, 6, 4, 6, 0, 0, 4, 0) },
        ['-'] = new[] { P(1, 3, 3, 3) },
        ['+'] = new[] { P(1, 3, 3, 3), P(2, 2, 2, 4) },
        ['.'] = new[] { P(2, 0, 2, 0.4f) },
        [','] = new[] { P(2, 0.6f, 1.5f, -0.6f) },
        [':'] = new[] { P(2, 1, 2, 1.4f), P(2, 4, 2, 4.4f) },
        ['\''] = new[] { P(2, 6, 2, 4.6f) },
        ['/'] = new[] { P(0, 0, 4, 6) },
        ['#'] = new[] { P(1, 0, 1, 6), P(3, 0, 3, 6), P(0, 2, 4, 2), P(0, 4, 4, 4) },
        ['('] = new[] { P(3, 6, 2, 5, 2, 1, 3, 0) },
        [')'] = new[] { P(1, 6, 2, 5, 2, 1, 1, 0) },
        ['?'] = new[] { P(0, 5, 1, 6, 3, 6, 4, 5, 4, 4, 2, 3, 2, 2), P(2, 0, 2, 0.4f) },
        ['!'] = new[] { P(2, 6, 2, 2), P(2, 0, 2, 0.4f) },
        ['_'] = new[] { P(0, 0, 4, 0) },
    };
}

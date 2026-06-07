using System.Numerics;

namespace RynthCore.Plugin.RynthJuice;

/// <summary>
/// A tiny stroke-based vector font. Nav3D has no text primitive, so each glyph
/// is defined as a set of line segments on a normalized 2×4 grid (x ∈ [0,2],
/// y ∈ [0,4], y increasing DOWNWARD to match screen space). The renderer
/// (<see cref="JuiceEffects"/>) turns every stroke into a thin billboarded quad.
///
/// Only the characters combat juice needs are defined: digits 0-9, '+' (heals),
/// '!' and the letters C R I T (for the "CRIT!" tag). Unknown chars render as a
/// blank advance.
/// </summary>
internal static class VectorFont
{
    public const float GlyphWidth = 2f;   // max x in grid units
    public const float GlyphHeight = 4f;  // max y in grid units
    public const float Advance = 3f;      // per-char horizontal step (glyph + gap)

    // Seven-segment grid points:
    //   (0,0)──A──(2,0)
    //     │           │
    //     F           B
    //     │           │
    //   (0,2)──G──(2,2)
    //     │           │
    //     E           C
    //     │           │
    //   (0,4)──D──(2,4)
    private static readonly Vector4 A = new(0, 0, 2, 0); // top
    private static readonly Vector4 B = new(2, 0, 2, 2); // upper-right
    private static readonly Vector4 C = new(2, 2, 2, 4); // lower-right
    private static readonly Vector4 D = new(0, 4, 2, 4); // bottom
    private static readonly Vector4 E = new(0, 2, 0, 4); // lower-left
    private static readonly Vector4 F = new(0, 0, 0, 2); // upper-left
    private static readonly Vector4 G = new(0, 2, 2, 2); // middle

    private static readonly Vector4[] Zero  = { A, B, C, D, E, F };
    private static readonly Vector4[] One   = { B, C };
    private static readonly Vector4[] Two   = { A, B, G, E, D };
    private static readonly Vector4[] Three = { A, B, G, C, D };
    private static readonly Vector4[] Four  = { F, G, B, C };
    private static readonly Vector4[] Five  = { A, F, G, C, D };
    private static readonly Vector4[] Six   = { A, F, G, E, C, D };
    private static readonly Vector4[] Seven = { A, B, C };
    private static readonly Vector4[] Eight = { A, B, C, D, E, F, G };
    private static readonly Vector4[] Nine  = { A, B, C, D, F, G };

    private static readonly Vector4[] Plus  = { new(0, 2, 2, 2), new(1, 1, 1, 3) };
    private static readonly Vector4[] Bang  = { new(1, 0, 1, 2.6f), new(1, 3.4f, 1, 4f) };

    private static readonly Vector4[] LetterC = { A, F, E, D };
    // R: left bar + top + upper-right + middle + diagonal leg
    private static readonly Vector4[] LetterR = { F, E, A, B, G, new(1, 2, 2, 4) };
    private static readonly Vector4[] LetterI = { new(1, 0, 1, 4) };
    private static readonly Vector4[] LetterT = { A, new(1, 0, 1, 4) };

    private static readonly Vector4[] Empty = System.Array.Empty<Vector4>();

    /// <summary>Strokes for a glyph, in grid units. Empty array = blank advance.</summary>
    public static Vector4[] Strokes(char c) => c switch
    {
        '0' => Zero,  '1' => One,   '2' => Two,   '3' => Three, '4' => Four,
        '5' => Five,  '6' => Six,   '7' => Seven, '8' => Eight, '9' => Nine,
        '+' => Plus,  '!' => Bang,
        'C' => LetterC, 'R' => LetterR, 'I' => LetterI, 'T' => LetterT,
        _   => Empty,
    };

    /// <summary>Total grid width of a string (for centering): N chars × Advance, trimmed of the trailing gap.</summary>
    public static float MeasureWidth(string s) =>
        s.Length == 0 ? 0f : s.Length * Advance - (Advance - GlyphWidth);
}

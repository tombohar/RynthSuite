// ============================================================================
//  RynthAi - Dashboard/BuffHalo.cs
//  The Buffing key's icon, "Halo" (picked 2026-11-10 over Ascend, Aura and
//  Crystal; the mock-ups are the artifact "Buffing Key Icons"). A four-point
//  star in a sigil ring, drawn with the draw list rather than a font glyph so
//  the icon itself says what buffing is doing, not only the key's LED:
//    Off      a hollow star in a dotted ring: buffing is off.
//    On       a solid star in a faint ring with four ticks, over a faint
//             violet glow: buffing is on, nothing to cast.
//    Casting  On, and a light runs round the ring while the star breathes:
//             RynthAi is casting buffs right now (the activity reads Buffing).
//  Drawn on a 15 px em (IconKit) and scaled to the em asked for.
//  Render thread; allocates nothing.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using static RynthCore.Plugin.RynthAi.Dashboard.IconKit;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Dashboard;

internal static class BuffHalo
{
    internal enum Look { Off, On, Casting }

    // The buffing colour (the activity line's violet), the running light's paler violet, white.
    private static readonly uint Violet = Argb(0xFFC5ABF4), Light = Argb(0xFFE9DFFF), White = Argb(0xFFFFFFFF);

    // The design, in pixels at a 15 px em.
    private const float RingR = 6.8f, TickIn = 6.0f, TickOut = 7.6f;
    private const float StarR = 5.4f, OffStarR = 4.8f, Waist = 0.6f;   // Waist: the curves' control point, from the centre
    private const float GlowR = 6.0f, DotR = 0.5f;
    private const int RingDots = 12;
    // While casting: the light goes round 0.8 times a second, its tail a third of the ring; the
    // star swells and shrinks 8% at 1.4 Hz.
    private const float TurnsPerSecond = 0.8f, TailSpan = MathF.Tau * 0.34f, BreathHz = 1.4f, Breath = 0.08f;
    private const int TailSegments = 16;
    private const float HeadR = 1.9f, HeadWaist = 0.2f;

    /// <summary>
    /// Draws the halo centred on <paramref name="centre"/>, <paramref name="em"/> pixels across, in
    /// <paramref name="color"/> (the key's icon colour). <paramref name="glow"/> adds the violet glow
    /// under the star (the key has it; the activity line, already violet, doesn't).
    /// </summary>
    public static void Draw(ImDrawListPtr dl, Vector2 centre, float em, uint color, Look look, bool glow = true)
    {
        float s = em / DesignEm;
        float line = MathF.Max(1f, s);
        Span<Vector2> pts = stackalloc Vector2[StarPoints];

        if (look == Look.Off)
        {
            int n = Star(pts, centre, OffStarR * s, Waist * s);
            dl.AddPolyline(ref pts[0], n, color, ImDrawFlags.Closed, line);
            uint dot = Alpha(color, 0.5f);
            float dotR = MathF.Max(0.55f, DotR * s);
            for (int i = 0; i < RingDots; i++)
            {
                float a = (i + 0.5f) * MathF.Tau / RingDots;
                dl.AddCircleFilled(centre + Dir(a) * (RingR * s), dotR, dot, 6);
            }
            return;
        }

        bool casting = look == Look.Casting;
        float t = Clock();
        float swell = casting ? 1f + Breath * MathF.Sin(t * MathF.Tau * BreathHz) : 1f;

        if (glow) Glow(dl, centre, GlowR * s, Violet, casting ? 0.09f : 0.05f);
        dl.AddCircle(centre, RingR * s, Alpha(color, 0.4f), 32, line);
        for (int k = 0; k < 4; k++)
        {
            Vector2 d = Dir(MathF.PI / 4 + k * MathF.PI / 2);
            dl.AddLine(centre + d * (TickIn * s), centre + d * (TickOut * s), color, 1.2f * line);
        }
        int count = Star(pts, centre, StarR * s * swell, Waist * s * swell);
        dl.AddConcavePolyFilled(ref pts[0], count, color);

        if (!casting) return;
        // The running light: a head and a tail that fades behind it, then a small white star on the head.
        float head = (t * TurnsPerSecond % 1f) * MathF.Tau;
        float step = TailSpan / TailSegments;
        for (int j = 0; j < TailSegments; j++)
        {
            float a1 = head - j * step;
            dl.PathArcTo(centre, RingR * s, a1 - step - 0.03f, a1, 3);
            dl.PathStroke(Alpha(Light, MathF.Pow(1f - j / (float)TailSegments, 1.6f)), ImDrawFlags.None, 1.6f * line);
        }
        FillStar(dl, centre + Dir(head) * (RingR * s), HeadR * s, HeadWaist * s, White);
    }
}

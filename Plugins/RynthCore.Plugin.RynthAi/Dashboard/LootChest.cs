// ============================================================================
//  RynthAi - Dashboard/LootChest.cs
//  The Looting key's icon, "Chest" (picked 2026-11-11 over Pouch and Gem, with
//  the Gilded priority mark; the mock-ups are the artifact "Looting Key
//  Icons"). A domed treasure chest with two bands and a lock:
//    Off      the chest in outline: looting is off.
//    On       a solid chest over a faint gold glow: looting is on.
//    Looting  On, and the lid pops up, the inside glows and three sparks fly
//             out: RynthAi is looting right now (the activity reads Looting).
//  Priority (Boost Loot Priority, where it applies) gilds the chest: RynthAi
//  loots each kill before it starts the next fight.
//  Drawn on a 15 px em (IconKit) and scaled to the em asked for.
//  Render thread; allocates nothing.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using static RynthCore.Plugin.RynthAi.Dashboard.IconKit;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Dashboard;

internal static class LootChest
{
    internal enum Look { Off, On, Looting }

    // The looting colour (the activity line's gold) and the light inside the chest and the sparks.
    private static readonly uint Gold = Argb(0xFFEFC054), Light = Argb(0xFFFFE9AE);

    // The design, in pixels at a 15 px em. The body is a box under a domed lid; a gap of 0.8
    // between them. The lid turns on its back-left corner (the hinge).
    private const float Half = 6f, BodyTop = -0.4f, BodyBottom = 5.4f, BodyRound = 0.9f;
    private const float LidBottom = -1.2f, LidShoulder = -2.7f, DomeCtrl = -5.0f, LidBandTop = -4.0f;
    private const float BandX = 3.7f, BandW = 0.8f, GapInset = 0.4f;
    private const float LockL = -1.25f, LockR = 1.25f, LockTop = -2.1f, LockBottom = 0.6f, KeyholeY = -0.9f, KeyholeR = 0.5f;
    private const float Cut = 1.5f, LockCut = 1.1f;   // the knockouts: a ground-coloured edge that sets a part off what's behind it
    private const float GlowR = 6.8f, GlowY = 0.4f, OpenGlowR = 6.2f, OpenGlowY = -2f;
    private static readonly Vector2 Hinge = new(-Half, LidBottom);

    // While looting: a cycle every 1/0.72 s. The lid rises 2.3 and tips back 0.1 rad, holds with a
    // small shake, drops, and bounces once. The sparks leave the gap one after another.
    private const float CyclesPerSecond = 0.72f, Rise = 2.3f, Tip = 0.1f;
    private const float SparkR = 1.5f, SparkWaist = 0.24f, SparkStart = 0.08f, SparkStagger = 0.07f, SparkLife = 0.5f;
    // Each spark: from (x0, y0) to (x1, y1): out to the left, straight up, out to the right (low,
    // to keep clear of the key's LED).
    private static readonly Vector4[] Sparks =
    {
        new(-5.4f, -1.6f, -9.2f, -5.8f),
        new(0f, -6.4f, 0f, -11.2f),
        new(5.4f, -1.4f, 9.4f, -2.8f),
    };

    /// <summary>
    /// Draws the chest centred on <paramref name="centre"/>, <paramref name="em"/> pixels across, in
    /// <paramref name="color"/> (the key's icon colour), or gold with <paramref name="priority"/>.
    /// <paramref name="ground"/> is the colour behind it (the key face, or the activity line's
    /// inset): the bands, the keyhole and the edges that set the lid and lock apart are cut in it.
    /// <paramref name="glow"/> adds the gold glow (the key has it; the activity line, already gold, doesn't).
    /// </summary>
    public static void Draw(ImDrawListPtr dl, Vector2 centre, float em, uint color, Look look, bool priority, uint ground, bool glow = true)
    {
        float s = em / DesignEm;
        float line = MathF.Max(1f, s);

        if (look == Look.Off)
        {
            LidPath(dl, centre, s, 0f, 0f);
            dl.PathStroke(color, ImDrawFlags.Closed, 1.1f * line);
            dl.AddRect(centre + new Vector2(-Half, BodyTop) * s, centre + new Vector2(Half, BodyBottom) * s, color, BodyRound * s, ImDrawFlags.None, 1.1f * line);
            dl.AddRect(centre + new Vector2(LockL, LockTop) * s, centre + new Vector2(LockR, LockBottom) * s, color, 0.5f * s, ImDrawFlags.None, 1.1f * line);
            return;
        }

        uint ink = priority ? Gold : color;
        bool looting = look == Look.Looting;
        float ph = Clock() * CyclesPerSecond % 1f;
        float lift = looting ? MathF.Max(0f, Lift(ph)) : 0f;
        float a = -Tip * lift, dy = -Rise * lift;

        if (glow)
        {
            Glow(dl, centre + new Vector2(0, GlowY) * s, GlowR * s, Gold, priority ? 0.075f : 0.045f);
            if (lift > 0f) Glow(dl, centre + new Vector2(0, OpenGlowY) * s, OpenGlowR * s, Gold, 0.09f * lift);
        }

        // The body, its edge cut, and its two bands.
        Vector2 bMin = centre + new Vector2(-Half, BodyTop) * s, bMax = centre + new Vector2(Half, BodyBottom) * s;
        dl.AddRect(bMin, bMax, ground, BodyRound * s, ImDrawFlags.None, Cut * s);
        dl.AddRectFilled(bMin, bMax, ink, BodyRound * s);
        for (int k = -1; k <= 1; k += 2)
            dl.AddLine(centre + new Vector2(k * BandX, BodyTop) * s, centre + new Vector2(k * BandX, BodyBottom) * s, ground, BandW * s);

        // The light inside, while the lid is up (clockwise for ImGui's anti-aliased fill).
        if (lift > 0.02f)
            dl.AddQuadFilled(centre + new Vector2(-Half + GapInset, BodyTop) * s, Lid(centre, s, a, dy, -Half + GapInset, LidBottom),
                Lid(centre, s, a, dy, Half - GapInset, LidBottom), centre + new Vector2(Half - GapInset, BodyTop) * s,
                Alpha(Light, MathF.Min(1f, lift * 1.6f)));

        // The lid, its edge cut, its bands, then the lock on its front edge.
        LidPath(dl, centre, s, a, dy);
        dl.PathStroke(ground, ImDrawFlags.Closed, Cut * s);
        LidPath(dl, centre, s, a, dy);
        dl.PathFillConvex(ink);
        for (int k = -1; k <= 1; k += 2)
            dl.AddLine(Lid(centre, s, a, dy, k * BandX, LidBottom), Lid(centre, s, a, dy, k * BandX, LidBandTop), ground, BandW * s);
        Vector2 l0 = Lid(centre, s, a, dy, LockL, LockTop), l1 = Lid(centre, s, a, dy, LockR, LockTop);
        Vector2 l2 = Lid(centre, s, a, dy, LockR, LockBottom), l3 = Lid(centre, s, a, dy, LockL, LockBottom);
        dl.AddQuad(l0, l1, l2, l3, ground, LockCut * s);
        dl.AddQuadFilled(l0, l1, l2, l3, ink);
        dl.AddCircleFilled(Lid(centre, s, a, dy, 0f, KeyholeY), KeyholeR * s, ground, 8);

        if (!looting) return;
        // The sparks: each eases out along its path, swelling in and fading.
        for (int k = 0; k < Sparks.Length; k++)
        {
            float u = (ph - SparkStart - k * SparkStagger) / SparkLife;
            if (u <= 0f || u >= 1f) continue;
            float e = 1f - (1f - u) * (1f - u);
            Vector4 p = Sparks[k];
            float r = SparkR * (1f - 0.4f * u) * s;
            float alpha = MathF.Min(1f, MathF.Sin(u * MathF.PI) * 1.2f);
            if (r < 0.3f || alpha <= 0.01f) continue;
            Vector2 at = centre + new Vector2(p.X + (p.Z - p.X) * e, p.Y + (p.W - p.Y) * e) * s;
            FillStar(dl, at, r, r * SparkWaist, Alpha(Light, alpha));
        }
    }

    /// <summary>How far the lid is up (0 shut, 1 open) at phase <paramref name="ph"/> of the cycle.</summary>
    private static float Lift(float ph)
    {
        if (ph < 0.14f) { float u = ph / 0.14f; return 1f - (1f - u) * (1f - u); }       // pops up
        if (ph < 0.62f) return 1f + 0.05f * MathF.Sin((ph - 0.14f) * 30f);              // holds, with a shake
        if (ph < 0.76f) { float u = (ph - 0.62f) / 0.14f; return 1f - u * u; }          // drops
        float v = (ph - 0.76f) / 0.24f;                                                   // one small bounce
        return 0.16f * MathF.Sin(MathF.Min(1f, v * 2.2f) * MathF.PI) * (1f - v);
    }

    /// <summary>A point (x, y) of the lid, raised by <paramref name="dy"/> and turned <paramref name="a"/> on the hinge, on screen.</summary>
    private static Vector2 Lid(Vector2 centre, float s, float a, float dy, float x, float y)
        => centre + (Hinge + Turn(x - Hinge.X, y - Hinge.Y, a) + new Vector2(0f, dy)) * s;

    /// <summary>The lid's outline as the draw list's path: up the left side, over the dome, down the right (clockwise).</summary>
    private static void LidPath(ImDrawListPtr dl, Vector2 centre, float s, float a, float dy)
    {
        dl.PathLineTo(Lid(centre, s, a, dy, -Half, LidBottom));
        dl.PathLineTo(Lid(centre, s, a, dy, -Half, LidShoulder));
        dl.PathBezierCubicCurveTo(Lid(centre, s, a, dy, -Half, DomeCtrl), Lid(centre, s, a, dy, Half, DomeCtrl),
            Lid(centre, s, a, dy, Half, LidShoulder), 12);
        dl.PathLineTo(Lid(centre, s, a, dy, Half, LidBottom));
    }
}

// ============================================================================
//  RynthAi - Dashboard/CombatCrossed.cs
//  The Combat key's icon, "Crossed" (picked 2026-11-11 over Sigil, Lock-on and
//  Swing; the mock-ups are the artifact "Combat Key Icons"). Two crossed
//  weapons for the stance the character is in:
//    melee    crossed swords; fighting, they knock together and throw sparks.
//    missile  crossed arrows; fighting, they loose outward with speed lines.
//    magic    crossed wands; fighting, the orbs flare blue and a spark jumps
//             between them.
//  Off is hollow (blades, heads, orbs drawn in outline); On is solid over a
//  faint glow; Fighting (the activity reads Combat) moves, about 1.3 times a
//  second. Drawn on a 15 px em (IconKit) and scaled to the em asked for.
//  Render thread; allocates nothing.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using static RynthCore.Plugin.RynthAi.Dashboard.IconKit;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Dashboard;

internal static class CombatCrossed
{
    internal enum Look { Off, On, Fighting }

    // The combat red (the activity line's) and its light, the mana blue and its light, white.
    private static readonly uint Red = Argb(0xFFF98075), RedLight = Argb(0xFFFFD6CF), SparkCore = Argb(0xFFFFEBE7),
        Blue = Argb(0xFF7FB9F9), BlueLight = Argb(0xFFDCEBFF), White = Argb(0xFFFFFFFF);

    private const float ClashHz = 1.3f, ClashShare = 0.32f;   // a knock 1.3 times a second, lasting 32% of the cycle
    private const float MinStarK = 0.08f;   // the knock's star starts here: smaller, it would be a degenerate polygon to fill
    private const float Up = -MathF.PI / 2, Right = -MathF.PI / 4, Left = -3 * MathF.PI / 4;   // up-right and up-left

    /// <summary>
    /// Draws the crossed weapons for <paramref name="stance"/> (the client's combat mode: Melee,
    /// Missile or Magic; anything else draws swords) centred on <paramref name="centre"/>,
    /// <paramref name="em"/> pixels across, in <paramref name="color"/>. <paramref name="glow"/> adds the
    /// faint glow under them (the key has it; the activity line doesn't).
    /// </summary>
    public static void Draw(ImDrawListPtr dl, Vector2 centre, float em, uint color, Look look, int stance, bool glow = true)
    {
        float s = em / DesignEm;
        float line = MathF.Max(1f, s);
        float t = Clock();
        // The knock: 0 most of the cycle, rising to 1 and back over its first third.
        float k = 0f;
        if (look == Look.Fighting)
        {
            float ph = t * ClashHz % 1f;
            if (ph < ClashShare) k = MathF.Sin(ph / ClashShare * MathF.PI);
        }
        switch (stance)
        {
            case CombatMode.Missile: Arrows(dl, centre, s, line, color, look, k, glow); break;
            case CombatMode.Magic: Wands(dl, centre, s, line, color, look, k, glow, t); break;
            default: Swords(dl, centre, s, line, color, look, k, glow); break;
        }
    }

    // ── Melee: crossed swords ──────────────────────────────────────────

    private static void Swords(ImDrawListPtr dl, Vector2 c, float s, float line, uint color, Look look, float k, bool glow)
    {
        bool solid = look != Look.Off;
        if (solid && glow) Glow(dl, c + new Vector2(0, -1.5f) * s, 5.5f * s, Red, look == Look.Fighting ? 0.04f + 0.12f * k : 0.035f);
        float kick = 0.08f * k;   // the blades knock apart a little
        Vector2 o = c + new Vector2(0, 0.6f) * s;
        Sword(dl, o, Right - kick, s, line, color, solid);
        Sword(dl, o, Left + kick, s, line, color, solid);
        if (k <= 0f) return;
        // Sparks where the blades meet: five rays thrown upward and a star.
        Vector2 at = c + new Vector2(0, -2.3f) * s;
        uint ray = Alpha(RedLight, k);
        ReadOnlySpan<float> rays = stackalloc float[] { 0f, -0.75f, 0.75f, -1.45f, 1.45f };
        foreach (float da in rays)
        {
            Vector2 d = Dir(Up + da);
            dl.AddLine(at + d * ((1.8f + 1.6f * k) * s), at + d * ((2.6f + 2.4f * k) * s), ray, 1.1f * line);
        }
        if (k > MinStarK) FillStar(dl, at, 2.4f * k * s, 0.3f * s, SparkCore);
    }

    /// <summary>A sword from <paramref name="o"/> along angle <paramref name="a"/>: blade (solid or outlined), guard, grip and pommel.</summary>
    private static void Sword(ImDrawListPtr dl, Vector2 o, float a, float s, float line, uint color, bool solid)
    {
        // Slim, with the guards well below the crossing: at 15 px wider blades and guards near the
        // middle drew a second X under the first (seen on the test client, 2026-11-11).
        const float Tip = 7.4f, Guard = -3.6f, Pommel = -6.7f, GuardW = 1.9f, W = 0.85f;
        Span<Vector2> blade = stackalloc Vector2[5];
        blade[0] = o + Turn(Guard, -W, a) * s;
        blade[1] = o + Turn(Tip - 2.3f * W, -W, a) * s;
        blade[2] = o + Turn(Tip, 0, a) * s;
        blade[3] = o + Turn(Tip - 2.3f * W, W, a) * s;
        blade[4] = o + Turn(Guard, W, a) * s;
        if (solid) dl.AddConvexPolyFilled(ref blade[0], 5, color);
        else dl.AddPolyline(ref blade[0], 5, color, ImDrawFlags.Closed, line);
        dl.AddLine(o + Turn(Guard, -GuardW, a) * s, o + Turn(Guard, GuardW, a) * s, color, 1.25f * line);
        dl.AddLine(o + Turn(Guard, 0, a) * s, o + Turn(Pommel + 0.9f, 0, a) * s, color, 1.15f * line);
        Vector2 pommel = o + Turn(Pommel, 0, a) * s;
        if (solid) dl.AddCircleFilled(pommel, 0.95f * s, color, 10);
        else dl.AddCircle(pommel, 0.85f * s, color, 10, line);
    }

    // ── Missile: crossed arrows ────────────────────────────────────────

    private static void Arrows(ImDrawListPtr dl, Vector2 c, float s, float line, uint color, Look look, float k, bool glow)
    {
        bool solid = look != Look.Off;
        if (solid && glow) Glow(dl, c + new Vector2(0, -1f) * s, 5.5f * s, Red, look == Look.Fighting ? 0.04f + 0.12f * k : 0.035f);
        float push = 1.6f * k;   // loosed: each arrow slides out along its shaft
        uint streak = Alpha(RedLight, k);
        Span<float> angles = stackalloc float[] { Right, Left };
        foreach (float a in angles)
        {
            Vector2 o = c + (new Vector2(0, 0.5f) + Dir(a) * push) * s;
            Arrow(dl, o, a, s, line, color, solid);
            if (k <= 0f) continue;
            for (int side = -1; side <= 1; side += 2)
                dl.AddLine(o + Turn(1.2f, 1.5f * side, a) * s, o + Turn(3.6f, 1.5f * side, a) * s, streak, line);
        }
    }

    /// <summary>An arrow from <paramref name="o"/> along angle <paramref name="a"/>: shaft, barbed head (solid or outlined), two pairs of fletching.</summary>
    private static void Arrow(ImDrawListPtr dl, Vector2 o, float a, float s, float line, uint color, bool solid)
    {
        const float Tip = 7.0f, Tail = -7.0f;
        dl.AddLine(o + Turn(Tail, 0, a) * s, o + Turn(Tip - 2.4f, 0, a) * s, color, 1.15f * line);
        Span<Vector2> head = stackalloc Vector2[4];
        head[0] = o + Turn(Tip, 0, a) * s;
        head[1] = o + Turn(Tip - 3.1f, -1.8f, a) * s;
        head[2] = o + Turn(Tip - 2.3f, 0, a) * s;
        head[3] = o + Turn(Tip - 3.1f, 1.8f, a) * s;
        if (solid) dl.AddConcavePolyFilled(ref head[0], 4, color);
        else dl.AddPolyline(ref head[0], 4, color, ImDrawFlags.Closed, line);
        for (int f = 0; f < 2; f++)
        {
            float u = Tail + 1.6f * f;
            Vector2 root = o + Turn(u, 0, a) * s;
            dl.AddLine(root, o + Turn(u - 1.5f, -1.5f, a) * s, color, 1.05f * line);
            dl.AddLine(root, o + Turn(u - 1.5f, 1.5f, a) * s, color, 1.05f * line);
        }
    }

    // ── Magic: crossed wands ───────────────────────────────────────────

    private static void Wands(ImDrawListPtr dl, Vector2 c, float s, float line, uint color, Look look, float k, bool glow, float t)
    {
        const float Tip = 6.1f, Pommel = -7.0f;
        bool solid = look != Look.Off;
        Vector2 o = c + new Vector2(0, 0.6f) * s;
        Vector2 orbR = o + Dir(Right) * (Tip * s), orbL = o + Dir(Left) * (Tip * s);
        if (solid && glow) Glow(dl, c + new Vector2(0, -1f) * s, 5.5f * s, Blue, look == Look.Fighting ? 0.04f + 0.1f * k : 0.035f);
        if (k > 0f)
        {
            Glow(dl, orbR, 3.2f * s, Blue, 0.16f * k);
            Glow(dl, orbL, 3.2f * s, Blue, 0.16f * k);
        }
        float orb = 1.75f * (1f + 0.3f * k) * s;
        Wand(dl, o, Right, Tip, Pommel, orb, s, line, color, solid);
        Wand(dl, o, Left, Tip, Pommel, orb, s, line, color, solid);
        if (k <= 0f) return;
        // The spark: a jagged line from orb to orb, its kinks new each knock, and a star above.
        Span<Vector2> bolt = stackalloc Vector2[5];
        int knock = (int)(t * ClashHz);
        bolt[0] = orbL;
        for (int i = 1; i < 4; i++)
        {
            double r = Math.Abs(Math.Sin(knock * 12.9898 + i * 78.233) * 43758.5453 % 1.0);
            Vector2 p = Vector2.Lerp(orbL, orbR, i / 4f);
            bolt[i] = p + new Vector2(0, (-2.0f + 2.4f * (float)r) * s);
        }
        bolt[4] = orbR;
        dl.AddPolyline(ref bolt[0], 5, Alpha(BlueLight, k), ImDrawFlags.None, 1.1f * line);
        FillStar(dl, new Vector2(c.X, orbL.Y - 2.3f * s), (1.6f * k + 0.4f) * s, 0.2f * s, Alpha(White, k));
    }

    /// <summary>A wand from <paramref name="o"/> along angle <paramref name="a"/>: shaft, collar, orb (solid or ringed) and a pommel bead.</summary>
    private static void Wand(ImDrawListPtr dl, Vector2 o, float a, float tip, float pommel, float orb, float s, float line, uint color, bool solid)
    {
        dl.AddLine(o + Turn(pommel, 0, a) * s, o + Turn(tip - 2.0f, 0, a) * s, color, 1.35f * line);
        dl.AddLine(o + Turn(tip - 2.5f, -1.2f, a) * s, o + Turn(tip - 2.5f, 1.2f, a) * s, color, 1.1f * line);
        Vector2 c = o + Turn(tip, 0, a) * s;
        if (solid) dl.AddCircleFilled(c, orb, color, 12);
        else dl.AddCircle(c, orb, color, 12, line);
        dl.AddCircleFilled(o + Turn(pommel, 0, a) * s, 0.8f * s, color, 8);
    }
}

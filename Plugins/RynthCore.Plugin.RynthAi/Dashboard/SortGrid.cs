// ============================================================================
//  RynthAi - Dashboard/SortGrid.cs
//  RynthSort's auto-sort key's icon, "Grid" (picked 2026-11-11 over Packs and
//  List; the mock-ups are the artifact "Auto-sort Key Icons"). A pack's slots,
//  3 by 3, with its items in order:
//    Off      the items in outline, the empty slots as dots: auto-sort off.
//    On       the items solid in faint slots: auto-sort on.
//    Sorting  an item lifts out of its slot, arcs to its place with a teal
//             trail and lands with a ring, then the next; the two then fade
//             back and it plays again. Shown while RynthSort is moving items
//             (its "moving" flag), whether auto-sort or a job asked for moves them.
//  Drawn on a 15 px em (IconKit) and scaled to the em asked for. The top
//  right stays clear for the key's LED. Render thread; allocates nothing.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using static RynthCore.Plugin.RynthAi.Dashboard.IconKit;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Dashboard;

internal static class SortGrid
{
    internal enum Look { Off, On, Sorting }

    // The sort colour (a teal of its own) and its light, for the moving item and its ring.
    private static readonly uint Teal = Argb(0xFF62D5C8), Light = Argb(0xFFD4F6F1);

    // The design, in pixels at a 15 px em: 3 by 3 cells, set a little low to clear the LED.
    private const float Cell = 3.3f, Gap = 0.85f, X0 = -5.8f, Y0 = -5.2f, Round = 0.8f;
    private const float SlotInset = 0.3f, SlotRound = 0.6f, SlotW = 0.8f, SlotAlpha = 0.3f, DotR = 0.55f;
    private const int Slots = 9, Items = 6;   // sorted: the first six cells hold items

    // Sorting: the items start in cells 0 1 3 4 7 8; 7 goes to 2, then 8 to 5 (a second each),
    // then a pause sorted, then the two fade back to 7 and 8.
    private const int Unsorted = (1 << 0) | (1 << 1) | (1 << 3) | (1 << 4) | (1 << 7) | (1 << 8);
    private static readonly (int From, int To)[] Moves = { (7, 2), (8, 5) };
    private const float MoveSeconds = 1f, Cycle = 2.8f, ResetAt = 2.45f, ResetSeconds = 0.35f;
    // Within a move: lift (to 0.12), travel on an arc (to 0.72), land with a ring (to 1).
    private const float LiftEnd = 0.12f, TravelEnd = 0.72f, Arc = 2.6f, Swell = 0.18f;
    private const int TrailDots = 5;
    private const float TrailStep = 0.06f, TrailR = 0.9f, TrailAlpha = 0.5f;
    private const float GlowR = 3.2f, RingGrow = 2f, RingW = 1.1f;

    /// <summary>
    /// Draws the grid centred on <paramref name="centre"/>, <paramref name="em"/> pixels across, in
    /// <paramref name="color"/> (the key's icon colour). <paramref name="glow"/> adds the teal glow
    /// round the moving item.
    /// </summary>
    public static void Draw(ImDrawListPtr dl, Vector2 centre, float em, uint color, Look look, bool glow = true)
    {
        float s = em / DesignEm;
        float line = MathF.Max(1f, s);

        if (look == Look.Off)
        {
            for (int i = 0; i < Slots; i++)
            {
                Vector2 p = CellAt(i);
                if (i < Items) dl.AddRect(At(centre, s, p), At(centre, s, p + new Vector2(Cell)), color, Round * s, ImDrawFlags.None, line);
                else dl.AddCircleFilled(At(centre, s, p + new Vector2(Cell / 2)), MathF.Max(DotR, DotR * s), Alpha(color, 0.55f), 6);
            }
            return;
        }

        uint slot = Alpha(color, SlotAlpha);
        for (int i = 0; i < Slots; i++)
        {
            Vector2 p = CellAt(i);
            dl.AddRect(At(centre, s, p + new Vector2(SlotInset)), At(centre, s, p + new Vector2(Cell - SlotInset)), slot, SlotRound * s, ImDrawFlags.None, SlotW * line);
        }
        if (look == Look.On)
        {
            for (int i = 0; i < Items; i++) Item(dl, centre, s, CellAt(i) + new Vector2(Cell / 2), 1f, color);
            return;
        }

        float ph = Clock() % Cycle;
        int done = Math.Min(Moves.Length, (int)(ph / MoveSeconds));
        int held = Unsorted;
        for (int j = 0; j < done; j++) held = (held & ~(1 << Moves[j].From)) | (1 << Moves[j].To);
        bool moving = done < Moves.Length;
        float f = ph - done * MoveSeconds;
        float reset = ph > ResetAt ? Ease((ph - ResetAt) / ResetSeconds) : 0f;

        // The items at rest; once both moves are done the moved two fade out as they fade back in below.
        for (int i = 0; i < Slots; i++)
        {
            if ((held & (1 << i)) == 0 || (moving && i == Moves[done].From)) continue;
            bool moved = !moving && (i == Moves[0].To || i == Moves[1].To);
            Item(dl, centre, s, CellAt(i) + new Vector2(Cell / 2), 1f, moved ? Alpha(color, 1f - reset) : color);
        }
        if (reset > 0f)
            foreach (var m in Moves) Item(dl, centre, s, CellAt(m.From) + new Vector2(Cell / 2), 1f, Alpha(color, reset));
        if (!moving) return;

        // The moving item: lifts, arcs over with a fading trail, lands with a ring.
        Vector2 a = CellAt(Moves[done].From) + new Vector2(Cell / 2), b = CellAt(Moves[done].To) + new Vector2(Cell / 2);
        float u = Ease((f - LiftEnd) / (TravelEnd - LiftEnd));
        float lift = f < LiftEnd ? f / LiftEnd : f > TravelEnd ? MathF.Max(0f, 1f - (f - TravelEnd) / LiftEnd) : 1f;
        if (f > LiftEnd && f < TravelEnd)
        {
            for (int k = 1; k <= TrailDots; k++)
            {
                float fade = 1f - k / (TrailDots + 1f);
                dl.AddCircleFilled(At(centre, s, OnArc(a, b, MathF.Max(0f, u - k * TrailStep))), TrailR * fade * s, Alpha(Teal, TrailAlpha * fade), 8);
            }
        }
        Vector2 c = OnArc(a, b, u);
        if (glow) Glow(dl, At(centre, s, c), GlowR * s, Teal, 0.16f * lift + 0.04f);
        Item(dl, centre, s, c, 1f + Swell * lift, Mix(color, Light, 0.55f * lift));
        if (f > TravelEnd)
        {
            float k = 1f - (f - TravelEnd) / (1f - TravelEnd);
            dl.AddCircle(At(centre, s, b), (Cell * 0.55f + RingGrow * (1f - k)) * s, Alpha(Teal, k), 16, RingW * line);
        }
    }

    private static Vector2 CellAt(int i) => new(X0 + (i % 3) * (Cell + Gap), Y0 + (i / 3) * (Cell + Gap));

    private static Vector2 At(Vector2 centre, float s, Vector2 design) => centre + design * s;

    /// <summary>A point a fraction <paramref name="u"/> along the arc from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static Vector2 OnArc(Vector2 a, Vector2 b, float u) => Vector2.Lerp(a, b, u) + new Vector2(0f, -Arc * MathF.Sin(u * MathF.PI));

    /// <summary>An item: a filled rounded cell centred on <paramref name="c"/>, <paramref name="scale"/> times a cell.</summary>
    private static void Item(ImDrawListPtr dl, Vector2 centre, float s, Vector2 c, float scale, uint color)
    {
        Vector2 half = new(Cell * scale / 2);
        dl.AddRectFilled(At(centre, s, c - half), At(centre, s, c + half), color, Round * s);
    }

    private static float Ease(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return u * u * (3f - 2f * u);
    }
}

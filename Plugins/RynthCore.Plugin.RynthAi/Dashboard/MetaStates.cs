// ============================================================================
//  RynthAi - Dashboard/MetaStates.cs
//  The Meta key's icon, "States" (picked 2026-11-11 over Gears and Rule; the
//  mock-ups are the artifact "Meta Key Icons"). Three states joined in a
//  cycle, the current one ringed: what a meta is, states and the jumps
//  between them.
//    Off      hollow states, dotted links: Meta is off.
//    Empty    the same in the key's lit colour: Meta is on, no meta loaded.
//    On       solid states, arrowed links, the current state ringed.
//    Running  On, and a token hops from state to state: the macro runs, so
//             the meta is checking its rules.
//  A state change (the META readout's state changes) moves the ring to the
//  next state with a blue burst; the dashboard passes how far that flash is.
//  Drawn on a 15 px em (IconKit) and scaled to the em asked for. The top
//  right stays clear for the key's LED. Render thread; allocates nothing.
// ============================================================================

using System;
using System.Numerics;
using ImGuiNET;
using static RynthCore.Plugin.RynthAi.Dashboard.IconKit;
using static RynthCore.Plugin.Shared.RynthUi.RynthUi;

namespace RynthCore.Plugin.RynthAi.Dashboard;

internal static class MetaStates
{
    internal enum Look { Off, Empty, On, Running }

    /// <summary>The states drawn (the ring moves round them, one step per state change).</summary>
    internal const int Nodes = 3;

    // The meta colour (the mana blue) and its light, for the token and the burst.
    private static readonly uint Blue = Argb(0xFF7FB9F9), Light = Argb(0xFFD6E8FF);

    // The design, in pixels at a 15 px em: three states in a triangle, top left, right, bottom.
    private static readonly Vector2[] Node = { new(-5.0f, -3.4f), new(5.0f, -0.6f), new(-1.5f, 5.3f) };
    private const float NodeR = 2.0f, NodeCut = 0.6f, HollowR = 1.75f, RingR = 3.1f;
    private const float LinkFrom = 0.6f, LinkTo = 0.8f, LinkW = 1.05f, ArrowSize = 1.0f, DotGap = 1.6f, DotR = 0.55f;
    // Running: the token takes 1/1.1 s per link, easing in and out of each state. A blue bead
    // with a light core, so it shows against the white links at the key's size.
    private const float HopsPerSecond = 1.1f, TokenR = 1.35f, TokenCoreR = 0.6f, TokenGlowR = 3.2f, TokenGlow = 0.22f;
    // The burst on a state change: the ring swells 3.4 and fades, the state lights up.
    private const float BurstGrow = 3.4f, BurstW = 1.4f, BurstGlowR = 5f;

    /// <summary>
    /// Draws the states centred on <paramref name="centre"/>, <paramref name="em"/> pixels across, in
    /// <paramref name="color"/> (the key's icon colour). <paramref name="current"/> is the ringed state
    /// (0..2); <paramref name="flash"/> is how far the burst of a state change has gone (0..1), or
    /// outside that range for none. <paramref name="ground"/> is the key face, cut round the states
    /// so the links end clear of them. <paramref name="glow"/> adds the blue glows.
    /// </summary>
    public static void Draw(ImDrawListPtr dl, Vector2 centre, float em, uint color, Look look, int current, float flash, uint ground, bool glow = true)
    {
        float s = em / DesignEm;
        float line = MathF.Max(1f, s);
        bool hollow = look is Look.Off or Look.Empty;
        bool lit = look != Look.Off;

        // The links: state to state round the cycle.
        for (int i = 0; i < Nodes; i++)
        {
            Vector2 a = Node[i], b = Node[(i + 1) % Nodes];
            Vector2 u = Vector2.Normalize(b - a);
            Vector2 from = a + u * (NodeR + LinkFrom), to = b - u * (NodeR + LinkTo);
            if (hollow)
            {
                uint dot = Alpha(color, lit ? 0.7f : 0.5f);
                int n = Math.Max(2, (int)MathF.Round(Vector2.Distance(from, to) / DotGap));
                float r = MathF.Max(0.55f, DotR * s);
                for (int k = 0; k <= n; k++) dl.AddCircleFilled(centre + Vector2.Lerp(from, to, k / (float)n) * s, r, dot, 6);
            }
            else
            {
                dl.AddLine(centre + from * s, centre + to * s, Alpha(color, 0.6f), LinkW * line);
                Arrow(dl, centre, s, to, MathF.Atan2(u.Y, u.X), Alpha(color, 0.85f), LinkW * line);
            }
        }

        // Running: the token, under the states so it slips behind them at each end.
        if (look == Look.Running)
        {
            float hop = Clock() * HopsPerSecond % Nodes;
            int k = (int)hop;
            float f = hop - k;
            f = f * f * (3f - 2f * f);
            Vector2 at = centre + Vector2.Lerp(Node[k], Node[(k + 1) % Nodes], f) * s;
            if (glow) Glow(dl, at, TokenGlowR * s, Blue, TokenGlow);
            dl.AddCircleFilled(at, TokenR * s, Blue, 10);
            dl.AddCircleFilled(at, TokenCoreR * s, Light, 8);
        }

        // The states.
        for (int i = 0; i < Nodes; i++)
        {
            Vector2 c = centre + Node[i] * s;
            if (hollow) dl.AddCircle(c, HollowR * s, Alpha(color, lit ? 0.9f : 0.6f), 14, line);
            else
            {
                dl.AddCircleFilled(c, (NodeR + NodeCut) * s, ground, 14);
                dl.AddCircleFilled(c, NodeR * s, color, 14);
            }
        }
        if (hollow) return;

        // The current state's ring, and the burst when it has just changed.
        Vector2 cur = centre + Node[Math.Clamp(current, 0, Nodes - 1)] * s;
        dl.AddCircle(cur, RingR * s, Alpha(color, 0.85f), 20, line);
        if (flash is >= 0f and < 1f)
        {
            if (glow) Glow(dl, cur, BurstGlowR * s, Blue, 0.2f * (1f - flash));
            dl.AddCircle(cur, (RingR + BurstGrow * MathF.Sqrt(flash)) * s, Alpha(Blue, 0.95f * (1f - flash)), 24, BurstW * line);
            dl.AddCircleFilled(cur, NodeR * s, Mix(color, Light, 0.6f * (1f - flash)), 14);
        }
    }

    /// <summary>An open arrowhead with its tip at <paramref name="tip"/> (design pixels), pointing along <paramref name="angle"/>.</summary>
    private static void Arrow(ImDrawListPtr dl, Vector2 centre, float s, Vector2 tip, float angle, uint color, float width)
    {
        Span<Vector2> pts = stackalloc Vector2[3];
        pts[0] = centre + (tip + Turn(-ArrowSize, -ArrowSize, angle)) * s;
        pts[1] = centre + tip * s;
        pts[2] = centre + (tip + Turn(-ArrowSize, ArrowSize, angle)) * s;
        dl.AddPolyline(ref pts[0], 3, color, ImDrawFlags.None, width);
    }
}

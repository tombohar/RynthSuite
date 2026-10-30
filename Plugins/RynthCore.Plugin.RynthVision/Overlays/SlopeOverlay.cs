using System;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision.Overlays;

/// <summary>
/// Highlights unclimbable (too-steep) terrain triangles around the player by
/// submitting each unwalkable triangle as a filled Nav3D triangle whose
/// vertices are the actual landblock mesh corners. The face sits exactly on
/// the slope so the player sees precisely the area they can't traverse.
///
/// Coverage extends past the player's landblock — at high radii the iteration
/// reaches up to 3 landblocks out (<see cref="LandblockWindow"/>) and renders
/// their slopes too. All coordinates are emitted in PLAYER-landblock-local
/// space; the engine's view-matrix capture shifts the camera into the same
/// frame so the markers stay anchored to the world even when the camera orbits
/// across a landblock boundary and AC re-anchors its rendering origin to a
/// different landblock.
///
/// Each 24 m cell is split along an AC-PRNG-selected diagonal (SW→NE or
/// SE→NW — <see cref="TerrainSampler.SwToNeCut"/>); only the unwalkable half
/// or halves are painted.
/// </summary>
internal sealed class SlopeOverlay
{
    private readonly RynthCoreHost _host;
    private readonly VisionSettings _settings;

    public SlopeOverlay(RynthCoreHost host, VisionSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    public void Submit(TickContext ctx)
    {
        if (!_settings.ShowUnclimbableSlopes || ctx.Terrain == null || !ctx.OnLandscape)
            return;
        if (!_host.HasNav3DTriangle) // requires API v60+
            return;

        LandblockWindow window = ctx.Terrain;
        const int cells = TerrainPaint.CellsPerSide;
        // Player's cell within their landblock.
        int playerCX = Math.Clamp((int)(ctx.X / TerrainPaint.Cell), 0, cells - 1);
        int playerCY = Math.Clamp((int)(ctx.Y / TerrainPaint.Cell), 0, cells - 1);
        int radius = Math.Clamp(_settings.SlopeRenderRadius, 1, VisionSettings.MaxRenderRadius);
        uint color = _settings.SlopeColorArgb;
        float floorZ = Math.Clamp(_settings.SlopeFloorZ, 0.05f, 0.999f);
        float bias = Math.Clamp(_settings.SlopeHeightBias, 0f, 5f);

        // Iterate outward in cell rings so the nearest cells are submitted
        // first — they survive if the engine's triangle budget is hit.
        for (int r = 0; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) < r && Math.Abs(dy) < r) continue;

                    int absCellX = playerCX + dx;
                    int absCellY = playerCY + dy;
                    int lbDX = TerrainPaint.FloorDiv(absCellX, cells);
                    int lbDY = TerrainPaint.FloorDiv(absCellY, cells);
                    int localCX = absCellX - lbDX * cells;
                    int localCY = absCellY - lbDY * cells;

                    LandblockData? lb = window.Get(lbDX, lbDY);
                    if (lb == null) continue;

                    bool swToNe = TerrainSampler.SwToNeCut(lb.LandblockKey, localCX, localCY);
                    TerrainSampler.GetTrianglePassability(lb, localCX, localCY, swToNe,
                        out bool t1ok, out bool t2ok, floorZ);
                    if (t1ok && t2ok) continue;

                    TerrainPaint.EmitCell(_host, lb, lbDX, lbDY, localCX, localCY, swToNe, !t1ok, !t2ok, bias, color);
                }
            }
        }
    }
}

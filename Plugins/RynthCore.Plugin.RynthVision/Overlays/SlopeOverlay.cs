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
/// reaches into the neighbouring 8 landblocks and renders their slopes too.
/// All coordinates are emitted in PLAYER-landblock-local space; the engine's
/// view-matrix capture shifts the camera into the same frame so the markers
/// stay anchored to the world even when the camera orbits across a landblock
/// boundary and AC re-anchors its rendering origin to a different landblock.
///
/// Each 24 m cell is split along an AC-PRNG-selected diagonal (SW→NE or
/// SE→NW — <see cref="TerrainSampler.SwToNeCut"/>); only the unwalkable half
/// or halves are painted.
/// </summary>
internal sealed class SlopeOverlay
{
    private const float Cell = 24f;
    private const int   CellsPerSide = 8;

    private readonly RynthCoreHost _host;
    private readonly VisionSettings _settings;
    private readonly TerrainSampler _terrain;

    public SlopeOverlay(RynthCoreHost host, VisionSettings settings, TerrainSampler terrain)
    {
        _host = host;
        _settings = settings;
        _terrain = terrain;
    }

    public void Submit()
    {
        if (!_settings.ShowUnclimbableSlopes || !_host.HasNav3D || !_terrain.IsReady)
            return;
        if (!_host.HasNav3DTriangle) // requires API v60+
            return;

        if (!_host.TryGetPlayerPose(out uint cellId, out float px, out float py, out _,
                out _, out _, out _, out _))
            return;
        if ((cellId & 0xFFFF) >= 0x100) return; // indoor
        if ((cellId >> 16) == 0) return;         // portalspace

        int playerLbX = (int)((cellId >> 24) & 0xFF);
        int playerLbY = (int)((cellId >> 16) & 0xFF);
        // Player's cell within their landblock.
        int playerCX = Math.Clamp((int)(px / Cell), 0, CellsPerSide - 1);
        int playerCY = Math.Clamp((int)(py / Cell), 0, CellsPerSide - 1);
        int radius = Math.Clamp(_settings.SlopeRenderRadius, 1, 24);
        uint color = _settings.SlopeColorArgb;
        float floorZ = Math.Clamp(_settings.SlopeFloorZ, 0.05f, 0.999f);
        float bias = Math.Clamp(_settings.SlopeHeightBias, 0f, 5f);

        // Per-tick landblock cache so a single Submit doesn't reload the same
        // landblock 50 times. The TerrainSampler also caches, but that's a
        // dictionary lookup per cell — this is a small local array indexed by
        // (lbDX+2, lbDY+2) covering up to ±2 landblocks (24-cell radius caps
        // at 3-landblock extent each direction).
        const int LbSpan = 5; // -2..+2
        var lbCache = new LandblockData?[LbSpan * LbSpan];
        var lbCacheLoaded = new bool[LbSpan * LbSpan];

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

                    // Decompose into (landblock-delta, local-cell) using
                    // arithmetic that handles negative values cleanly.
                    int lbDX = FloorDiv(absCellX, CellsPerSide);
                    int lbDY = FloorDiv(absCellY, CellsPerSide);
                    int localCX = absCellX - lbDX * CellsPerSide;
                    int localCY = absCellY - lbDY * CellsPerSide;

                    int lbX = playerLbX + lbDX;
                    int lbY = playerLbY + lbDY;
                    if (lbX < 0 || lbX > 0xFF || lbY < 0 || lbY > 0xFF) continue;
                    if (lbDX < -2 || lbDX > 2 || lbDY < -2 || lbDY > 2) continue;

                    int cacheIdx = (lbDX + 2) * LbSpan + (lbDY + 2);
                    LandblockData? lb;
                    if (lbCacheLoaded[cacheIdx])
                    {
                        lb = lbCache[cacheIdx];
                    }
                    else
                    {
                        uint lbKey = (uint)((lbX << 8) | lbY);
                        lb = _terrain.LoadLandblock(lbKey);
                        lbCache[cacheIdx] = lb;
                        lbCacheLoaded[cacheIdx] = true;
                    }
                    if (lb == null) continue;

                    bool swToNe = TerrainSampler.SwToNeCut(lb.LandblockKey, localCX, localCY);
                    TerrainSampler.GetTrianglePassability(lb, localCX, localCY, swToNe,
                        out bool t1ok, out bool t2ok, floorZ);
                    if (t1ok && t2ok) continue;

                    // World coords in PLAYER-landblock-local frame: cell SW
                    // corner is (lbDX*192 + localCX*24, lbDY*192 + localCY*24).
                    float x0 = lbDX * 192f + localCX * Cell;
                    float x1 = x0 + Cell;
                    float z0 = lbDY * 192f + localCY * Cell;
                    float z1 = z0 + Cell;
                    float h00 = lb.GetVertexZ(localCX,     localCY)     + bias;
                    float h10 = lb.GetVertexZ(localCX + 1, localCY)     + bias;
                    float h01 = lb.GetVertexZ(localCX,     localCY + 1) + bias;
                    float h11 = lb.GetVertexZ(localCX + 1, localCY + 1) + bias;

                    if (swToNe)
                    {
                        // SE half (SW, SE, NE) below the SW→NE diagonal
                        if (!t1ok)
                            _host.Nav3DAddTriangle(x0, h00, z0,  x1, h10, z0,  x1, h11, z1, color);
                        // NW half (SW, NE, NW) above the SW→NE diagonal
                        if (!t2ok)
                            _host.Nav3DAddTriangle(x0, h00, z0,  x1, h11, z1,  x0, h01, z1, color);
                    }
                    else
                    {
                        // SW half (SW, SE, NW) below the SE→NW diagonal
                        if (!t1ok)
                            _host.Nav3DAddTriangle(x0, h00, z0,  x1, h10, z0,  x0, h01, z1, color);
                        // NE half (SE, NE, NW) above the SE→NW diagonal
                        if (!t2ok)
                            _host.Nav3DAddTriangle(x1, h10, z0,  x1, h11, z1,  x0, h01, z1, color);
                    }
                }
            }
        }
    }

    // Floor division that handles negatives the way we need: FloorDiv(-1, 8) = -1, not 0.
    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}

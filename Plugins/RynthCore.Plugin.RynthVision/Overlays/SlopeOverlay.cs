using System;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision.Overlays;

/// <summary>
/// Highlights unclimbable (too-steep) terrain triangles around the player as
/// semi-transparent fill strips drawn with the engine's 3D line API. Reproduces
/// RynthAi's TerrainPassabilityOverlay against the shared TerrainSampler. Each
/// 24m cell splits into two triangles; only the steep halves are filled.
/// </summary>
internal sealed class SlopeOverlay
{
    private const float HeightBias = 0.5f;
    private const float Thickness  = 4.0f;
    private const int   FillSteps  = 3;

    private readonly RynthCoreHost _host;
    private readonly VisionSettings _settings;
    private readonly TerrainSampler _terrain;

    public SlopeOverlay(RynthCoreHost host, VisionSettings settings, TerrainSampler terrain)
    {
        _host = host;
        _settings = settings;
        _terrain = terrain;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void Submit()
    {
        if (!_settings.ShowUnclimbableSlopes || !_host.HasNav3D || !_terrain.IsReady)
            return;

        if (!_host.TryGetPlayerPose(out uint cellId, out float px, out float py, out _,
                out _, out _, out _, out _))
            return;
        if ((cellId & 0xFFFF) >= 0x100) return; // indoor
        if ((cellId >> 16) == 0) return;         // portalspace

        LandblockData? lb = _terrain.LoadLandblock(cellId >> 16);
        if (lb == null) return;

        int playerCX = Math.Clamp((int)(px / 24f), 0, 7);
        int playerCY = Math.Clamp((int)(py / 24f), 0, 7);
        int radius = Math.Clamp(_settings.SlopeRenderRadius, 1, 7);
        uint color = _settings.SlopeColorArgb;

        // Expand outward from the player so nearest cells are submitted first
        // (they survive if the Nav3D line budget is hit).
        for (int r = 0; r <= radius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) < r && Math.Abs(dy) < r) continue;
                    int cx = playerCX + dx;
                    int cy = playerCY + dy;
                    if (cx < 0 || cx > 7 || cy < 0 || cy > 7) continue;

                    TerrainSampler.GetTrianglePassability(lb, cx, cy, out bool t1ok, out bool t2ok);
                    if (t1ok && t2ok) continue;

                    float x0 = cx * 24f, x1 = (cx + 1) * 24f;
                    float z0 = cy * 24f;
                    float h00 = lb.GetVertexZ(cx,     cy)     + HeightBias;
                    float h10 = lb.GetVertexZ(cx + 1, cy)     + HeightBias;
                    float h01 = lb.GetVertexZ(cx,     cy + 1) + HeightBias;
                    float h11 = lb.GetVertexZ(cx + 1, cy + 1) + HeightBias;

                    // SE triangle: SW(x0,h00,z0) → SE(x1,h10,z0) → NE(x1,h11,z1)
                    if (!t1ok)
                    {
                        for (int s = 0; s < FillSteps; s++)
                        {
                            float t  = (s + 0.5f) / FillSteps;
                            float sz = z0 + t * 24f;
                            _host.Nav3DAddLine(x0 + t * 24f, Lerp(h00, h11, t), sz,
                                               x1,            Lerp(h10, h11, t), sz, Thickness, color);
                        }
                    }

                    // NW triangle: SW(x0,h00,z0) → NE(x1,h11,z1) → NW(x0,h01,z1)
                    if (!t2ok)
                    {
                        for (int s = 0; s < FillSteps; s++)
                        {
                            float t  = (s + 0.5f) / FillSteps;
                            float sz = z0 + t * 24f;
                            _host.Nav3DAddLine(x0,            Lerp(h00, h01, t), sz,
                                               x0 + t * 24f,  Lerp(h00, h11, t), sz, Thickness, color);
                        }
                    }
                }
            }
        }
    }
}

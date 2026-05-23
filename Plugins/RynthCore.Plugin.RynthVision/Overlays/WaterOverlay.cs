using System;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;
using RynthCore.TerrainData;

namespace RynthCore.Plugin.RynthVision.Overlays;

/// <summary>
/// Highlights cells whose terrain is impassable water, drawn as fill strips with
/// the engine's 3D line API. Water is identified by AC terrain-type index
/// (decoded from the landblock terrain words). The exact "impassable water"
/// indices vary by client data — tune VisionSettings.WaterTerrainTypes;
/// the /rv watertypes command logs the indices present in the current landblock.
/// </summary>
internal sealed class WaterOverlay
{
    private const float HeightBias = 0.3f;
    private const float Thickness  = 4.0f;
    private const int   FillSteps  = 3;

    private readonly RynthCoreHost _host;
    private readonly VisionSettings _settings;
    private readonly TerrainSampler _terrain;

    public WaterOverlay(RynthCoreHost host, VisionSettings settings, TerrainSampler terrain)
    {
        _host = host;
        _settings = settings;
        _terrain = terrain;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void Submit()
    {
        if (!_settings.ShowImpassableWater || !_host.HasNav3D || !_terrain.IsReady)
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
        int radius = Math.Clamp(_settings.WaterRenderRadius, 1, 7);
        uint color = _settings.WaterColorArgb;

        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int cx = playerCX + dx;
                int cy = playerCY + dy;
                if (cx < 0 || cx > 7 || cy < 0 || cy > 7) continue;
                if (!IsWaterCell(lb, cx, cy)) continue;

                float x0 = cx * 24f, x1 = (cx + 1) * 24f;
                float z0 = cy * 24f;
                float h00 = lb.GetVertexZ(cx,     cy)     + HeightBias;
                float h10 = lb.GetVertexZ(cx + 1, cy)     + HeightBias;
                float h01 = lb.GetVertexZ(cx,     cy + 1) + HeightBias;
                float h11 = lb.GetVertexZ(cx + 1, cy + 1) + HeightBias;

                // Fill both triangles of the cell.
                for (int s = 0; s < FillSteps; s++)
                {
                    float t  = (s + 0.5f) / FillSteps;
                    float sz = z0 + t * 24f;
                    _host.Nav3DAddLine(x0 + t * 24f, Lerp(h00, h11, t), sz,
                                       x1,            Lerp(h10, h11, t), sz, Thickness, color);
                    _host.Nav3DAddLine(x0,            Lerp(h00, h01, t), sz,
                                       x0 + t * 24f,  Lerp(h00, h11, t), sz, Thickness, color);
                }
            }
        }
    }

    // Cell counts as water only when all four corners are a water terrain type —
    // keeps shorelines from over-highlighting. Loosen to "any corner" if desired.
    private bool IsWaterCell(LandblockData lb, int cx, int cy)
        => IsWater(lb, cx,     cy)     && IsWater(lb, cx + 1, cy) &&
           IsWater(lb, cx,     cy + 1) && IsWater(lb, cx + 1, cy + 1);

    private bool IsWater(LandblockData lb, int ix, int iy)
    {
        int type = TerrainSampler.GetTerrainType(lb, ix, iy);
        int[] set = _settings.WaterTerrainTypes;
        for (int i = 0; i < set.Length; i++)
            if (set[i] == type) return true;
        return false;
    }
}

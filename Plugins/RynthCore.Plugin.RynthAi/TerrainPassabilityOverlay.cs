using System;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi;

/// <summary>
/// Renders a filled highlight over impassable (too-steep) terrain triangles
/// using the engine's 3D line API. Each 24m cell is split into its two triangles;
/// only the steep halves are highlighted, matching how AC treats passability.
/// Iterates outward from the player so the nearest cells always render first.
/// </summary>
internal sealed class TerrainPassabilityOverlay
{
    // Triangle 1 (SE): vertices (cx,cy),(cx+1,cy),(cx+1,cy+1)
    // Triangle 2 (NW): vertices (cx,cy),(cx+1,cy+1),(cx,cy+1)
    private const uint  FillColor    = 0x60FF2020; // semi-transparent red ARGB
    private const float FillThick    = 4.0f;
    private const float HeightBias   = 0.5f;
    private const int   RenderRadius = 4;
    private const int   FillSteps    = 3;           // strips per triangle; 81 cells×2tri×3=486 ≤ MaxLines(512)

    private readonly RynthCoreHost _host;
    private MainLogic? _raycast;

    public TerrainPassabilityOverlay(RynthCoreHost host)
    {
        _host = host;
    }

    public void SetRaycast(MainLogic raycast) => _raycast = raycast;

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void Render()
    {
        if (_raycast == null || !_host.HasNav3D)
            return;

        if (!_host.TryGetPlayerPose(out uint cellId, out float px, out float py, out _,
                out _, out _, out _, out _))
            return;

        if ((cellId & 0xFFFF) >= 0x100) return; // indoor
        if ((cellId >> 16) == 0) return;         // portalspace

        var geo = _raycast.GeometryLoader;
        if (geo == null) return;

        uint landblockKey = cellId >> 16;

        byte[]? heights = geo.GetTerrainHeightGrid(landblockKey);
        if (heights == null) return;

        int playerCX = Math.Clamp((int)(px / 24f), 0, 7);
        int playerCY = Math.Clamp((int)(py / 24f), 0, 7);

        // Expand outward from player — nearest cells always submitted first
        for (int r = 0; r <= RenderRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) < r && Math.Abs(dy) < r) continue;

                    int cx = playerCX + dx;
                    int cy = playerCY + dy;
                    if (cx < 0 || cx > 7 || cy < 0 || cy > 7) continue;

                    geo.GetTerrainTrianglePassability(landblockKey, cx, cy,
                        out bool t1ok, out bool t2ok);

                    if (t1ok && t2ok) continue;

                    float x0 = cx * 24f,       x1 = (cx + 1) * 24f;
                    float z0 = cy * 24f,        z1 = (cy + 1) * 24f;
                    float h00 = geo.GetTerrainVertexHeight(landblockKey, cx,     cy    ) + HeightBias;
                    float h10 = geo.GetTerrainVertexHeight(landblockKey, cx + 1, cy    ) + HeightBias;
                    float h01 = geo.GetTerrainVertexHeight(landblockKey, cx,     cy + 1) + HeightBias;
                    float h11 = geo.GetTerrainVertexHeight(landblockKey, cx + 1, cy + 1) + HeightBias;

                    if (!t1ok)
                    {
                        // SE triangle: SW(x0,h00,z0) → SE(x1,h10,z0) → NE(x1,h11,z1)
                        // Horizontal strips at z-step t; left on hypotenuse (SW→NE), right on east edge (SE→NE)
                        for (int s = 0; s < FillSteps; s++)
                        {
                            float t  = (s + 0.5f) / FillSteps;
                            float sz = z0 + t * 24f;
                            float lx = x0 + t * 24f;
                            float rx = x1;
                            float ly = Lerp(h00, h11, t); // plane height along SW→NE
                            float ry = Lerp(h10, h11, t); // plane height along SE→NE
                            _host.Nav3DAddLine(lx, ly, sz, rx, ry, sz, FillThick, FillColor);
                        }
                    }

                    if (!t2ok)
                    {
                        // NW triangle: SW(x0,h00,z0) → NE(x1,h11,z1) → NW(x0,h01,z1)
                        // Horizontal strips at z-step t; left on west edge (SW→NW), right on hypotenuse (SW→NE)
                        for (int s = 0; s < FillSteps; s++)
                        {
                            float t  = (s + 0.5f) / FillSteps;
                            float sz = z0 + t * 24f;
                            float lx = x0;
                            float rx = x0 + t * 24f;
                            float ly = Lerp(h00, h01, t); // plane height along SW→NW
                            float ry = Lerp(h00, h11, t); // plane height along SW→NE
                            _host.Nav3DAddLine(lx, ly, sz, rx, ry, sz, FillThick, FillColor);
                        }
                    }
                }
            }
        }
    }
}

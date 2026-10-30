using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthVision.Overlays;

/// <summary>
/// Draws a ring centred on the player at the radar detection range. When
/// terrain data is loaded (slopes or water on) the ring is built from Nav3D
/// triangles that follow the ground under each segment, so it doesn't float
/// over valleys or sink into hills; otherwise it is the engine's flat ring at
/// the player's height (terrain is never loaded just for the ring).
/// </summary>
internal sealed class RadarRingOverlay
{
    private const float SegmentLength = 16f;   // metres of ring per segment
    private const int MinSegments = 32, MaxSegments = 96;
    private const float GroundBias = 0.10f;

    private readonly RynthCoreHost _host;
    private readonly VisionSettings _settings;

    private float[] _cos = Array.Empty<float>(), _sin = Array.Empty<float>();

    // /rv debug: log the first submit per landblock (where the ring centre
    // projects on screen) to confirm it tracks the character, not the camera.
    private uint _lastLoggedLandblock;

    public RadarRingOverlay(RynthCoreHost host, VisionSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    public void Submit(TickContext ctx)
    {
        if (!_settings.ShowRadarRing || !ctx.OnLandscape)
            return;

        float radius = MathF.Max(5f, _settings.RadarRangeWorld);
        if (ctx.Terrain != null && _host.HasNav3DTriangle)
        {
            SubmitGroundRing(ctx, ctx.Terrain, radius);
        }
        else
        {
            // Nav3D world frame is Y-up: (x=east, y=up, z=north). The player
            // pose is AC convention (x=east, y=north, z=up), so swap y/z here.
            _host.Nav3DAddRingEx(ctx.X, ctx.Z, ctx.Y, radius,
                _settings.RadarRingThickness, _settings.RadarRingHeight,
                _settings.RadarRingColorArgb);
        }

        if (ctx.Debug && ctx.Landblock != _lastLoggedLandblock)
        {
            _lastLoggedLandblock = ctx.Landblock;
            LogRingPlacement(ctx, radius);
        }
    }

    /// <summary>
    /// Wall (lower band at the ring colour, upper band fainter, like the
    /// engine ring's fade) plus a flat rim, every vertex on the ground below it.
    /// </summary>
    private void SubmitGroundRing(TickContext ctx, LandblockWindow terrain, float radius)
    {
        int segs = Math.Clamp((int)MathF.Ceiling(2f * MathF.PI * radius / SegmentLength), MinSegments, MaxSegments);
        EnsureTrig(segs);

        uint color = _settings.RadarRingColorArgb;
        uint alpha = color >> 24;
        uint colorTop = (color & 0x00FFFFFFu) | ((alpha * 35 / 100) << 24);
        float half = MathF.Max(_settings.RadarRingHeight, 0.1f) * 0.5f;
        float thick = MathF.Max(_settings.RadarRingThickness, 0.05f);
        float inner = MathF.Max(0.01f, radius - thick), outer = radius + thick;

        float px = ctx.X, py = ctx.Y;
        float prevX = 0, prevY = 0, prevG = 0, prevIx = 0, prevIy = 0, prevOx = 0, prevOy = 0;
        for (int i = 0; i <= segs; i++)
        {
            float c = _cos[i % segs], s = _sin[i % segs];
            float x = px + radius * c, y = py + radius * s;
            float g = (terrain.TryGetTerrainZ(x, y, out float tz) ? tz : ctx.Z) + GroundBias;
            float ix = px + inner * c, iy = py + inner * s;
            float ox = px + outer * c, oy = py + outer * s;

            if (i > 0)
            {
                // Nav3D (x=east, y=up, z=north).
                Quad(prevX, prevG, prevY,  x, g, y,  x, g + half, y,  prevX, prevG + half, prevY, color);
                Quad(prevX, prevG + half, prevY,  x, g + half, y,  x, g + 2 * half, y,  prevX, prevG + 2 * half, prevY, colorTop);
                Quad(prevIx, prevG, prevIy,  prevOx, prevG, prevOy,  ox, g, oy,  ix, g, iy, color);
            }

            prevX = x; prevY = y; prevG = g;
            prevIx = ix; prevIy = iy; prevOx = ox; prevOy = oy;
        }
    }

    private void Quad(float ax, float ay, float az, float bx, float by, float bz,
                      float cx, float cy, float cz, float dx, float dy, float dz, uint color)
    {
        _host.Nav3DAddTriangle(ax, ay, az, bx, by, bz, cx, cy, cz, color);
        _host.Nav3DAddTriangle(ax, ay, az, cx, cy, cz, dx, dy, dz, color);
    }

    private void EnsureTrig(int segs)
    {
        if (_cos.Length == segs) return;
        _cos = new float[segs];
        _sin = new float[segs];
        for (int i = 0; i < segs; i++)
        {
            float a = 2f * MathF.PI * i / segs;
            _cos[i] = MathF.Cos(a);
            _sin[i] = MathF.Sin(a);
        }
    }

    // Reports where the ring centre projects on screen. If the projected centre
    // is near the viewport's horizontal middle and roughly at the character's
    // feet, the ring is anchored to the player. If it tracks the camera-look
    // direction instead, either TryGetPlayerPose is returning the wrong object
    // or the engine view matrix is misaligned with what AC is rendering.
    private void LogRingPlacement(TickContext ctx, float radius)
    {
        string screen = "n/a";
        if (_host.HasWorldToScreen && _host.WorldToScreen(ctx.X, ctx.Z, ctx.Y, out float sx, out float sy))
            screen = $"({sx:F0},{sy:F0})";
        string vp = _host.TryGetViewportSize(out uint vpW, out uint vpH) ? $"{vpW}x{vpH}" : "n/a";
        _host.Log($"[RynthVision] RadarRing placed cell=0x{ctx.CellId:X8} centre=({ctx.X:F1},{ctx.Y:F1},{ctx.Z:F1}) " +
                  $"r={radius:F0} ground={(ctx.Terrain != null)} screen={screen} vp={vp}");
    }
}

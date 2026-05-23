using System;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthVision.Overlays;

/// <summary>
/// Draws a horizontal ring on the ground centred on the player at the radar
/// detection range. Needs no terrain data — just the player pose — so it's the
/// reference for the Nav3D render path being wired correctly.
/// </summary>
internal sealed class RadarRingOverlay
{
    private readonly RynthCoreHost _host;
    private readonly VisionSettings _settings;

    public RadarRingOverlay(RynthCoreHost host, VisionSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    public void Submit()
    {
        if (!_settings.ShowRadarRing)
            return;

        try
        {
            if (!_host.TryGetPlayerPose(out uint cellId, out float east, out float north, out float up,
                    out _, out _, out _, out _))
                return;
            if ((cellId >> 16) == 0) // portalspace / not in the world
                return;

            // Nav3D world frame is Y-up: (x=east, y=up, z=north). The player
            // pose is AC convention (x=east, y=north, z=up), so swap y/z here.
            // (See RynthAi's TerrainPassabilityOverlay for the same mapping.)
            float radius = MathF.Max(5f, _settings.RadarRangeWorld);
            _host.Nav3DAddRing(east, up, north, radius,
                _settings.RadarRingThickness, _settings.RadarRingColorArgb);
        }
        catch (Exception ex)
        {
            _host.Log($"[RynthVision] RadarRing: {ex.Message}");
        }
    }
}
